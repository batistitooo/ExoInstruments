using System;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// Rebuilds a capture's BACKGROUND outside the game, stage by stage, using the shipped Core code at
/// every step, and writes each stage to disk so the one that introduces an artefact can be named
/// rather than guessed.
///
/// The stars are not reproduced -- they need the Gaia catalogue and the whole photometric chain --
/// and they do not need to be: the artefact under investigation is in the DIFFUSE background, which
/// a real frame lets us isolate by masking every source. What this reproduces is exactly that
/// background: the emission map deposited through the real projection and Galactic rotation, then
/// the real PSF convolution, then the real detector arithmetic.
///
/// Every stage is dumped, so a comparison against the real frame can be made after each rather than
/// only at the end.
/// </summary>
static class DumpFrame
{
    static void Main(string[] args)
    {
        string mapPath = args.Length > 0 ? args[0]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
                "GameData/ExoInstruments/PluginData/HalphaMap.emission");
        var map = new EmissionMap();
        map.Load(mapPath);

        // The user's configuration.
        const int w = 4144, h = 2822;
        const double plateScale = 3.8200;
        const double raDeg = 85.25, decDeg = -2.20472, latitudeDeg = 28.53;
        const double exposure = 121.770798;
        const double apertureAreaCm2 = 20.4;
        const double electronsPerAdu = 66000.0 / 16383.0;

        double meridianRaDeg = raDeg;
        HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(raDeg, decDeg, meridianRaDeg, latitudeDeg);
        SkyVector boresight = SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg);
        SkyVector zenith = SkyVector.FromHorizontal(90.0, 0.0);
        double dot = zenith.Dot(boresight);
        SkyVector up = SkyVector.Normalized(zenith.X - dot * boresight.X, zenith.Y - dot * boresight.Y, zenith.Z - dot * boresight.Z);
        SkyVector right = SkyVector.Normalized(
            up.Y * boresight.Z - up.Z * boresight.Y,
            up.Z * boresight.X - up.X * boresight.Z,
            up.X * boresight.Y - up.Y * boresight.X);
        var projection = new GnomonicProjection(boresight, up, right, w * plateScale / 3600.0, w, h);
        var rotation = HorizontalToGalactic.Build(meridianRaDeg, latitudeDeg);

        // --- Stage 1: the emission deposit, exactly as DepositEmissionField does it -------------
        var plane = new float[w * h];
        EmissionMap.AllocateScratch(out long[] px, out double[] wt);

        // Throughput at each line the 7 nm H-alpha filter admits. Held as a fixed set here so the
        // stage is reproducible without the whole bandpass chain; the SHAPE is what is under test.
        var lines = new[] { EmissionLines.NII6548, EmissionLines.HAlpha, EmissionLines.NII6584 };
        double perRayleigh = EmissionLines.ElectronsPerPixelPerSecond(1.0, plateScale, apertureAreaCm2, 0.9) * exposure;

        long zero = 0, nan = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                SkyVector dir = projection.Deproject(x + 0.5, y + 0.5);
                rotation.ToGalactic(dir, out double l, out double b);
                double r = map.RayleighsAtGalactic(l, b, px, wt);
                if (double.IsNaN(r)) { nan++; continue; }
                if (!(r > 0.0)) { zero++; continue; }

                double e = 0.0;
                foreach (var line in lines)
                {
                    double ratio = NebularLineRatios.RatioToHalpha(line, r);
                    if (double.IsNaN(ratio) || !(ratio > 0.0)) continue;
                    e += r * ratio * perRayleigh;
                }
                plane[y * w + x] += (float)e;
            }
        Report("1-deposit", plane, w, h, electronsPerAdu);
        Console.WriteLine($"        ({nan} NaN, {zero} zero readings)");

        // --- Stage 2: the PSF ------------------------------------------------------------------
        double lambda = 656.28e-9;
        double atm = OpticalPsf.AtmosphericFwhmForDelivered(2.5, plateScale, 0.051, 0.0, lambda);
        float[] kernel = OpticalPsf.BuildKernel(plateScale, 0.051, 0.0, lambda, atm, 0.0, out int radius);
        Console.WriteLine($"  PSF kernel radius {radius} px");
        FourierConvolution.Convolve(plane, w, h, kernel, radius);
        Report("2-psf", plane, w, h, electronsPerAdu);

        // --- Stage 3: sky and dark -------------------------------------------------------------
        const float skyElectrons = 6.0f;
        for (int i = 0; i < plane.Length; i++) plane[i] += skyElectrons;
        Report("3-sky", plane, w, h, electronsPerAdu);

        WriteRaw("frame_final.bin", plane, w, h);
        Console.WriteLine("\nwrote frame_stage*.bin and frame_final.bin");
    }

    static void Report(string tag, float[] plane, int w, int h, double electronsPerAdu)
    {
        double min = double.MaxValue, max = 0.0;
        foreach (float v in plane) { if (v < min) min = v; if (v > max) max = v; }
        // The column the artefact sits on, in ADU, so it can be read against the real frame.
        Console.Write($"  {tag,-10} {min,8:F2} to {max,10:F1} e-   column x=2100-2150 in ADU: ");
        for (int y = 1600; y < 1740; y += 20)
        {
            double s = 0.0;
            for (int x = 2100; x < 2150; x++) s += plane[y * w + x];
            Console.Write($"{s / 50.0 / electronsPerAdu,6:F1}");
        }
        Console.WriteLine();
        WriteRaw($"frame_{tag}.bin", plane, w, h);
    }

    static void WriteRaw(string path, float[] plane, int w, int h)
    {
        using (var bw = new BinaryWriter(File.Create(path)))
        {
            bw.Write(w); bw.Write(h);
            foreach (float v in plane) bw.Write(v);
        }
    }
}
