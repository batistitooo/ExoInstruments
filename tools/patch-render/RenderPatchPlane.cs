using System;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// Samples one line's plane out of a patch set over a grid of Galactic directions, through the
/// SAME EmissionPatchSet.TryRayleighsAtGalactic the capture uses, and writes the values back.
///
/// The point is to be able to look at what a frame will contain WITHOUT taking the frame: the
/// interpolation is the part that goes wrong (a C0 fallback rules a lattice across the picture),
/// and it is not visible in the stored cells at all, only in what the reconstruction makes of them.
/// Reimplementing the sampler in the analysis script would have tested the reimplementation.
///
/// Input  : one binary file, little-endian, n then 2n doubles of l,b in degrees.
/// Output : n doubles, NaN where nothing covers the direction.
/// </summary>
static class RenderPatchPlane
{
    static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: render <patchset> <wavelength-metres|halpha> <in.bin> <out.bin>");
            return 2;
        }

        // "basemap" samples the all-sky composite instead, through its own reconstruction. The
        // H-alpha frame is the only one where that layer contributes at all, so it is the only one
        // where its cell structure can show, and isolating it is the only way to tell its texture
        // apart from a patch's.
        if (args[0].Equals("basemap", StringComparison.OrdinalIgnoreCase))
        {
            var map = new EmissionMap();
            map.Load(args[1]);
            Console.WriteLine($"base map at nside {map.Nside} ({map.ResolutionArcmin:F2} arcmin), {map.Source}");
            long bn;
            double[] bl, bb;
            using (var r = new BinaryReader(File.OpenRead(args[2])))
            {
                bn = r.ReadInt64();
                bl = new double[bn];
                bb = new double[bn];
                for (long i = 0; i < bn; i++) { bl[i] = r.ReadDouble(); bb[i] = r.ReadDouble(); }
            }
            EmissionMap.AllocateScratch(out long[] bpx, out double[] bwt);
            using (var wtr = new BinaryWriter(File.Create(args[3])))
                for (long i = 0; i < bn; i++)
                    wtr.Write(map.RayleighsAtGalactic(bl[i], bb[i], bpx, bwt));
            Console.WriteLine($"wrote {bn} samples");
            return 0;
        }

        var set = new EmissionPatchSet();
        set.Load(args[0]);

        // Optional 5th argument: run the load-time repair the game runs, so the two renders differ
        // by exactly that and nothing else.
        if (args.Length > 4 && args[4].Length > 0)
        {
            var composite = new EmissionMap();
            composite.Load(args[4]);
            set.RejectOutliers();
            set.CalibrateAgainst(composite);
            Console.WriteLine($"repaired: {set.RejectedCells} cells rejected, "
                            + $"{set.CalibratedCells} gain-matched to the composite");
        }

        bool halpha = args[1].Equals("halpha", StringComparison.OrdinalIgnoreCase);
        double wavelength = halpha ? 0.0 : double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);

        long n;
        double[] l, b;
        using (var r = new BinaryReader(File.OpenRead(args[2])))
        {
            n = r.ReadInt64();
            l = new double[n];
            b = new double[n];
            for (long i = 0; i < n; i++) { l[i] = r.ReadDouble(); b[i] = r.ReadDouble(); }
        }

        var patches = new System.Collections.Generic.List<EmissionPatchSet.Patch>(set.Patches);
        var pixels = new long[16];
        var weights = new double[16];
        var cursor = EmissionPatchSet.Cursor.New(patches.Count);
        var outv = new double[n];
        long hit = 0;

        for (long i = 0; i < n; i++)
        {
            outv[i] = double.NaN;
            for (int p = 0; p < patches.Count; p++)
            {
                int plane = halpha ? -1 : patches[p].PlaneFor(wavelength);
                if (!halpha && plane < 0) continue;
                if (!set.TryRayleighsAtGalactic(patches[p], p, plane, l[i], b[i],
                                                pixels, weights, ref cursor, out double v)) continue;
                outv[i] = v;
                hit++;
                break;
            }
        }

        using (var w = new BinaryWriter(File.Create(args[3])))
            for (long i = 0; i < n; i++) w.Write(outv[i]);

        Console.WriteLine($"{set.PatchCount} patches; {hit} of {n} directions covered "
                        + $"({100.0 * hit / n:F1}%)");
        foreach (var p in set.Patches)
            Console.WriteLine($"  {p.Name,-26} nside {p.Nside,6} "
                            + $"({EmissionPatchSet.PatchResolutionArcmin(p) * 60:F1} arcsec) "
                            + $"{p.CellCount,9} cells");
        return 0;
    }
}
