using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Dumps what the shipped Core says about constellations, for comparison against astropy.
///
/// Three files, matching the three things that can independently be wrong:
///
///   exo_frame.csv       J2000 -> B1875, the frame change on its own. If this is wrong every
///                       lookup near a boundary is wrong and nothing else shows it.
///   exo_lookup.csv      the constellation each J2000 position resolves to, over a grid dense
///                       enough that all 88 have to appear.
///   exo_roman.csv       Roman's own worked examples from the VI/42 ReadMe, which are given at
///                       equinox 1950 and therefore exercise the Newcomb precession alone.
/// </summary>
static class DumpConstellations
{
    static void Main()
    {
        CheckPoles();
        DumpFrame();
        DumpLookup();
        DumpRomanExamples();
        Console.WriteLine("written exo_frame.csv, exo_lookup.csv, exo_roman.csv");
    }

    /// <summary>
    /// The two poles and the right-ascension wrap, which the grids below deliberately avoid landing
    /// exactly on. The lookup THROWS rather than guessing if no boundary arc contains a position,
    /// so this is the check that the table tiles the sphere with no gap at its most degenerate
    /// points; a gap there would surface in the game as a failed index build, not as a wrong answer.
    /// </summary>
    static void CheckPoles()
    {
        (double Ra, double Dec)[] degenerate =
        {
            (0.0, 90.0), (0.0, -90.0), (180.0, 90.0), (180.0, -90.0),
            (0.0, 0.0), (359.9999, 0.0), (0.0, 89.99999), (0.0, -89.99999),
        };
        foreach (var (ra, dec) in degenerate)
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  RA {0,9:F4} Dec {1,9:F4} -> {2}", ra, dec, Constellations.FindAbbreviation(ra, dec)));
    }

    /// <summary>The frame change alone, on a grid that includes both poles and the RA wrap.</summary>
    static void DumpFrame()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ra_j2000_deg,dec_j2000_deg,ra_b1875_deg,dec_b1875_deg");

        for (int i = 0; i < 72; i++)
        for (int j = 0; j <= 36; j++)
        {
            double ra = i * 5.0 + 0.37;
            double dec = -90.0 + j * 5.0;
            BesselianFrames.J2000ToBesselian(ra, dec, 1875.0, out double ra75, out double dec75);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2:R},{3:R}", ra, dec, ra75, dec75));
        }
        File.WriteAllText("exo_frame.csv", sb.ToString());
    }

    /// <summary>
    /// The full lookup over a 0.5 x 0.5 degree grid in right ascension hours and declination.
    /// Coarse enough to run in seconds, fine enough that every one of the 88 constellations is
    /// hit, including Equuleus and Crux, the two smallest.
    /// </summary>
    static void DumpLookup()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ra_j2000_deg,dec_j2000_deg,abbreviation");

        for (int i = 0; i < 720; i++)
        for (int j = 0; j <= 358; j++)
        {
            double ra = i * 0.5 + 0.213;      // offset off the round numbers the boundaries sit on
            double dec = -89.5 + j * 0.5 + 0.137;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2}", ra, dec, Constellations.FindAbbreviation(ra, dec)));
        }
        File.WriteAllText("exo_lookup.csv", sb.ToString());
    }

    /// <summary>
    /// The eight examples printed in the VI/42 ReadMe, at equinox 1950. Fed through the Newcomb
    /// precession and the B1875 lookup directly, which is exactly the path Roman's own FORTRAN
    /// takes, so a disagreement is a disagreement with the table's author.
    /// </summary>
    static void DumpRomanExamples()
    {
        (double RaHours, double DecDeg, string Expected)[] examples =
        {
            (9.0000, 65.0000, "UMa"),
            (23.5000, -20.0000, "Aqr"),
            (5.1200, 9.1200, "Ori"),
            (9.4555, -19.9000, "Hya"),
            (12.8888, 22.0000, "Com"),
            (15.6687, -12.1234, "Lib"),
            (19.0000, -40.0000, "CrA"),
            (6.2222, -81.1234, "Men"),
        };

        var sb = new StringBuilder();
        sb.AppendLine("ra_b1950_hours,dec_b1950_deg,expected,got");
        foreach (var (raHours, decDeg, expected) in examples)
        {
            double[] m = BesselianFrames.NewcombPrecession(1950.0, 1875.0);
            double ra = raHours * 15.0 * Math.PI / 180.0;
            double dec = decDeg * Math.PI / 180.0;
            double x = Math.Cos(dec) * Math.Cos(ra), y = Math.Cos(dec) * Math.Sin(ra), z = Math.Sin(dec);

            double px = m[0] * x + m[1] * y + m[2] * z;
            double py = m[3] * x + m[4] * y + m[5] * z;
            double pz = m[6] * x + m[7] * y + m[8] * z;

            double ra75 = Math.Atan2(py, px) * 180.0 / Math.PI;
            if (ra75 < 0.0) ra75 += 360.0;
            double dec75 = Math.Asin(Math.Max(-1.0, Math.Min(1.0, pz))) * 180.0 / Math.PI;

            string got = Constellations.FindAbbreviationB1875(ra75 / 15.0, dec75);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2},{3}", raHours, decDeg, expected, got));
        }
        File.WriteAllText("exo_roman.csv", sb.ToString());
    }
}
