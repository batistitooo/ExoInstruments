using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Dumps the map-indexing arithmetic the shipped Core performs: HEALPix pixel numbers in both
/// schemes, and the Galactic transform that gets a catalogue position into the frame every all-sky
/// dust map is tabulated in.
/// </summary>
static class DumpMapIndex
{
    static void Main()
    {
        DumpHealpix();
        DumpGalactic();
        DumpMapQueries();
        Console.WriteLine("written exo_healpix.csv, exo_galactic.csv, exo_mapquery.csv");
    }

    static void DumpHealpix()
    {
        int[] nsides = { 1, 2, 4, 16, 64, 256, 1024, 4096 };

        // A deterministic spread over the sphere rather than a lattice: a lattice can sit exactly
        // on pixel boundaries at every nside and miss the branch cuts entirely.
        var rng = new Pcg32(0x5EED1234UL, 7UL);

        var sb = new StringBuilder();
        sb.AppendLine("nside,theta_rad,phi_rad,ring,nested");

        foreach (int nside in nsides)
        {
            // The boundaries the scheme is piecewise across: the cap/band transition at
            // |z| = 2/3, both poles, and phi at each quadrant edge.
            double[] specialZ = { 1.0, 2.0 / 3.0 + 1e-12, 2.0 / 3.0 - 1e-12, 0.0, -2.0 / 3.0, -1.0 };
            double[] specialPhi = { 0.0, Math.PI / 2.0, Math.PI, 3.0 * Math.PI / 2.0, 2.0 * Math.PI - 1e-12 };
            foreach (double z in specialZ)
            foreach (double phi in specialPhi)
                Row(sb, nside, Math.Acos(Math.Max(-1.0, Math.Min(1.0, z))), phi);

            for (int i = 0; i < 4000; i++)
            {
                double z = 2.0 * rng.NextDouble() - 1.0;      // uniform in z is uniform on the sphere
                double phi = 2.0 * Math.PI * rng.NextDouble();
                Row(sb, nside, Math.Acos(z), phi);
            }
        }
        File.WriteAllText("exo_healpix.csv", sb.ToString());
    }

    static void Row(StringBuilder sb, int nside, double theta, double phi)
    {
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3},{4}",
            nside, theta, phi,
            Healpix.AngleToRing(nside, theta, phi),
            Healpix.AngleToNested(nside, theta, phi)));
    }

    /// <summary>Reads the synthetic map make_test_map.py wrote and queries it, so the format and the lookup are checked end to end.</summary>
    static void DumpMapQueries()
    {
        var map = new DustMap();
        if (!File.Exists("test_map.dustmap"))
        {
            File.WriteAllText("exo_mapquery.csv", "ra_deg,dec_deg,l_deg,b_deg,ebv,av\n");
            return;
        }
        map.Load("test_map.dustmap");

        var sb = new StringBuilder();
        sb.AppendLine("ra_deg,dec_deg,l_deg,b_deg,ebv,av");
        var rng = new Pcg32(0x0D0570EUL, 5UL);
        for (int i = 0; i < 3000; i++)
        {
            double ra = 360.0 * rng.NextDouble();
            double dec = Math.Asin(2.0 * rng.NextDouble() - 1.0) * 180.0 / Math.PI;
            GalacticCoordinates.EquatorialToGalactic(ra, dec, out double l, out double b);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R}",
                ra, dec, l, b, map.ReddeningAt(ra, dec), map.ExtinctionAtV(ra, dec)));
        }
        File.WriteAllText("exo_mapquery.csv", sb.ToString());
        Console.WriteLine($"  map: nside {map.Nside} ({map.ResolutionArcmin:F1} arcmin), {map.Source}");
    }

    static void DumpGalactic()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ra_deg,dec_deg,l_deg,b_deg,ra_back_deg,dec_back_deg");
        var rng = new Pcg32(0xDEADBEEFUL, 3UL);

        // Named sight lines first: the Galactic centre and both poles are where a sign error is
        // unmistakable, and the centre is the one a dust map is most often asked about.
        (double ra, double dec)[] named =
        {
            (266.404996, -28.936172),   // Sgr A*, l = 0, b = 0
            (192.85948, 27.12825),      // the north Galactic pole itself
            (12.85948, -27.12825),      // the south Galactic pole
            (0.0, 0.0), (180.0, 0.0), (0.0, 89.999), (0.0, -89.999),
        };
        foreach (var p in named) GalacticRow(sb, p.ra, p.dec);
        for (int i = 0; i < 6000; i++)
            GalacticRow(sb, 360.0 * rng.NextDouble(), Math.Asin(2.0 * rng.NextDouble() - 1.0) * 180.0 / Math.PI);

        File.WriteAllText("exo_galactic.csv", sb.ToString());
    }

    static void GalacticRow(StringBuilder sb, double ra, double dec)
    {
        GalacticCoordinates.EquatorialToGalactic(ra, dec, out double l, out double b);
        GalacticCoordinates.GalacticToEquatorial(l, b, out double raBack, out double decBack);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R}",
            ra, dec, l, b, raBack, decBack));
    }
}
