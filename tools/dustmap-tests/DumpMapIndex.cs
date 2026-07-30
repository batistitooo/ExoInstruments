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
        DumpInterpolation();
        DumpHealpix();
        DumpGalactic();
        DumpMapQueries();
        DumpFloat16();
        DumpRealMapQueries();
        Console.WriteLine("written exo_healpix.csv, exo_galactic.csv, exo_mapquery.csv, exo_float16.csv");
    }

    /// <summary>
    /// The four surrounding pixels and their weights, which is how a beam-smoothed map has to be
    /// sampled. Compared against healpy's get_interp_weights, the reference implementation of the
    /// same scheme, pixel identity as well as weight, since a plausible weight on the wrong
    /// pixel is the failure mode that still produces a sky.
    /// </summary>
    static void DumpInterpolation()
    {
        int[] nsides = { 1, 2, 4, 16, 64, 256, 1024 };
        var rng = new Pcg32(0x117E12AUL, 11UL);

        var sb = new StringBuilder();
        sb.AppendLine("nside,theta_rad,phi_rad,p0,p1,p2,p3,w0,w1,w2,w3");
        var pix = new long[4];
        var wgt = new double[4];

        foreach (int nside in nsides)
        {
            // The poles and the cap/band transition first: the scheme is piecewise across them and
            // the interpolation folds in extra pixels at both ends.
            double[] specialZ = { 1.0, 0.999999, 2.0 / 3.0 + 1e-12, 2.0 / 3.0 - 1e-12, 0.0, -2.0 / 3.0, -0.999999, -1.0 };
            double[] specialPhi = { 0.0, 1e-12, Math.PI / 2.0, Math.PI, 3.0 * Math.PI / 2.0, 2.0 * Math.PI - 1e-12 };
            foreach (double z in specialZ)
            foreach (double phi in specialPhi)
                InterpRow(sb, nside, Math.Acos(Math.Max(-1.0, Math.Min(1.0, z))), phi, pix, wgt);

            for (int i = 0; i < 3000; i++)
                InterpRow(sb, nside, Math.Acos(2.0 * rng.NextDouble() - 1.0),
                          2.0 * Math.PI * rng.NextDouble(), pix, wgt);
        }
        File.WriteAllText("exo_interp.csv", sb.ToString());
    }

    static void InterpRow(StringBuilder sb, int nside, double theta, double phi, long[] pix, double[] wgt)
    {
        Healpix.InterpolationWeights(nside, theta, phi, pix, wgt);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0},{1:R},{2:R},{3},{4},{5},{6},{7:R},{8:R},{9:R},{10:R}",
            nside, theta, phi, pix[0], pix[1], pix[2], pix[3], wgt[0], wgt[1], wgt[2], wgt[3]));
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

    /// <summary>
    /// Queries a REAL packed map, if one has been built next door, so the whole chain (packer,
    /// format, HEALPix, Galactic transform) can be compared against dustmaps on the real sky
    /// rather than against a pattern this project wrote itself.
    /// </summary>
    static void DumpRealMapQueries()
    {
        const string path = "../DustMap.dustmap";
        if (!File.Exists(path)) { File.Delete("exo_realmap.csv"); return; }

        var map = new DustMap();
        map.Load(path);

        var sb = new StringBuilder();
        sb.AppendLine("ra_deg,dec_deg,ebv");
        var rng = new Pcg32(0x5F1DEEDUL, 17UL);
        for (int i = 0; i < 4000; i++)
        {
            double ra = 360.0 * rng.NextDouble();
            double dec = Math.Asin(2.0 * rng.NextDouble() - 1.0) * 180.0 / Math.PI;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}",
                ra, dec, map.ReddeningAt(ra, dec)));
        }
        File.WriteAllText("exo_realmap.csv", sb.ToString());
        Console.WriteLine($"  real map: nside {map.Nside} ({map.ResolutionArcmin:F1} arcmin), {map.Source}");
    }

    /// <summary>Every one of the 65536 half-float encodings, decoded, for numpy to check.</summary>
    static void DumpFloat16()
    {
        var sb = new StringBuilder();
        sb.AppendLine("bits,value");
        for (int i = 0; i <= ushort.MaxValue; i++)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R}",
                i, Float16.ToDouble((ushort)i)));
        File.WriteAllText("exo_float16.csv", sb.ToString());
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
        Console.WriteLine($"  dust map: nside {map.Nside} ({map.ResolutionArcmin:F1} arcmin), {map.Source}");

        // The emission format shares the dust one's layout but adds the line it belongs to, so it
        // gets its own read: a header field nothing checks is a header field nothing catches.
        if (!File.Exists("test_map.emission")) return;
        var emission = new EmissionMap();
        emission.Load("test_map.emission");

        var eb = new StringBuilder();
        eb.AppendLine("ra_deg,dec_deg,l_deg,b_deg,rayleighs");
        var erng = new Pcg32(0xE1155104UL, 9UL);
        for (int i = 0; i < 3000; i++)
        {
            double ra = 360.0 * erng.NextDouble();
            double dec = Math.Asin(2.0 * erng.NextDouble() - 1.0) * 180.0 / Math.PI;
            GalacticCoordinates.EquatorialToGalactic(ra, dec, out double l, out double b);
            eb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4:R}",
                ra, dec, l, b, emission.RayleighsAt(ra, dec)));
        }
        File.WriteAllText("exo_emissionquery.csv", eb.ToString());
        Console.WriteLine($"  emission map: {emission.LineName} at "
                        + $"{emission.LineWavelengthMeters * 1e9:F2} nm, nside {emission.Nside}");
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
