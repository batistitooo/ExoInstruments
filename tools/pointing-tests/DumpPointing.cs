using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Dumps the pointing transforms the shipped Core performs, for comparison against IAU SOFA.
///
/// Three things, in the order the camera composes them when it aims at a catalogue position:
///
///   equatorial.csv   RA/Dec -> altitude/azimuth (SkyCoordinates.EquatorialToHorizontal), and the
///                    inverse, which is the pair the star field and the aim now share.
///   basis.csv        altitude/azimuth -> the (north, east, up) direction cosines SkyVector builds
///                    and SolarSystemCameraTexture.TryEquatorialDirection resolves onto the site's
///                    world basis. This is the leg that is new.
///   parsing.csv      SexagesimalCoordinates against what a catalogue writes.
/// </summary>
static class DumpPointing
{
    static void Main()
    {
        DumpEquatorial();
        DumpBasis();
        DumpParsing();
        Console.WriteLine("written exo_equatorial.csv, exo_basis.csv, exo_parsing.csv");
    }

    static void DumpEquatorial()
    {
        var sb = new StringBuilder();
        sb.AppendLine("lst_deg,latitude_deg,ra_deg,dec_deg,alt_deg,az_deg,ra_back_deg,dec_back_deg");

        // Latitudes spanning the roster's sites and both poles; the pole is where an
        // implementation that steps in right ascension and divides by cos(dec) falls apart.
        double[] latitudes = { -89.9, -45.0, -24.6, 0.0, 33.4, 43.9, 89.9 };
        double[] lsts = { 0.0, 37.5, 90.0, 180.0, 271.3, 359.9 };

        foreach (double lat in latitudes)
        foreach (double lst in lsts)
        for (int i = 0; i < 24; i++)
        for (int j = 0; j < 13; j++)
        {
            double ra = i * 15.0 + 3.7;
            double dec = -88.0 + j * (176.0 / 12.0);

            HorizontalCoordinates h = SkyCoordinates.EquatorialToHorizontal(ra, dec, lst, lat);
            SkyCoordinates.HorizontalToEquatorial(h.AltitudeDeg, h.AzimuthDeg, lst, lat,
                                                  out double raBack, out double decBack);

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R}",
                lst, lat, ra, dec, h.AltitudeDeg, h.AzimuthDeg, raBack, decBack));
        }
        File.WriteAllText("exo_equatorial.csv", sb.ToString());
    }

    static void DumpBasis()
    {
        var sb = new StringBuilder();
        sb.AppendLine("alt_deg,az_deg,north,east,up");
        for (int i = 0; i <= 36; i++)
        for (int j = 0; j <= 24; j++)
        {
            double az = i * 10.0;
            double alt = -90.0 + j * 7.5;
            SkyVector v = SkyVector.FromHorizontal(alt, az);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2:R},{3:R},{4:R}", alt, az, v.X, v.Y, v.Z));
        }
        File.WriteAllText("exo_basis.csv", sb.ToString());
    }

    static void DumpParsing()
    {
        (string ra, string dec, string label)[] cases =
        {
            ("05 35 17.3", "-05 23 28",   "M42"),
            ("05h35m17.3s", "-05d23m28s", "M42 with unit letters"),
            ("05:35:17.3", "-05:23:28",   "M42 colon separated"),
            ("83.82208", "-5.39111",      "M42 decimal degrees"),
            ("12 30 49.4", "+12 23 28",   "M87"),
            ("00 42 44.3", "+41 16 09",   "M31"),
            ("17 45 40.0", "-29 00 28",   "Galactic centre"),
            ("06 45 08.9", "-16 42 58",   "Sirius"),
            ("00 00 00", "+90 00 00",     "north celestial pole"),
            ("23 59 59.9", "-89 59 59",   "near the south pole"),
            ("garbage", "-05 23 28",      "unparseable RA"),
            ("05 35 17.3", "-95 00 00",   "declination out of range"),
            ("05 75 17.3", "-05 23 28",   "minutes out of range"),
        };

        var sb = new StringBuilder();
        sb.AppendLine("label,ra_text,dec_text,ok,ra_deg,dec_deg,formatted");
        foreach (var c in cases)
        {
            bool ok = SexagesimalCoordinates.TryParse(c.ra, c.dec, out double ra, out double dec);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "\"{0}\",\"{1}\",\"{2}\",{3},{4:R},{5:R},\"{6}\"",
                c.label, c.ra, c.dec, ok ? 1 : 0, ra, dec,
                ok ? SexagesimalCoordinates.Format(ra, dec).Replace("\"", "\"\"") : ""));
        }
        File.WriteAllText("exo_parsing.csv", sb.ToString());
    }
}
