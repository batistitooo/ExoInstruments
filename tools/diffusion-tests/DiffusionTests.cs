using System;
using System.Globalization;
using ExoInstruments.Core;

// Headless checks on CCD charge diffusion: Core/ChargeDiffusion.cs, and WFPC2/PC1's kernel and
// delivered PSF width in the shipped catalogue.
//
// Run:  dotnet run -p:Core=../../ExoInstruments/Core
internal static class DiffusionTests
{
    private static int failures;

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) failures++;
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string P(double v) => (100.0 * v).ToString("F2", CultureInfo.InvariantCulture) + " %";

    // Tiny Tim 7.5 wfpc2pc1.pup, "WFPC2 CCD Pixel Scattering Function (estimates charge diffusion)",
    // printed identically in the Tiny Tim User's Guide v6.3.
    private static readonly double[,] TinyTimPublished =
    {
        { 0.0125, 0.05, 0.0125 },
        { 0.0500, 0.75, 0.0500 },
        { 0.0125, 0.05, 0.0125 },
    };

    // WFPC2 Instrument Handbook Sect. 5.4, the kernel it offers for "convolving a simulated image".
    // Asymmetric as printed, which is what the orientation checks below need.
    private static readonly double[,] HandbookKernel =
    {
        { 0.016, 0.067, 0.016 },
        { 0.080, 0.635, 0.080 },
        { 0.015, 0.078, 0.015 },
    };

    private static int Main()
    {
        Console.WriteLine();
        Console.WriteLine("A. The kernel against Tiny Tim");
        TinyTim();

        Console.WriteLine();
        Console.WriteLine("B. The handbook's account of the same pinhole test");
        Handbook();

        Console.WriteLine();
        Console.WriteLine("C. Binning");
        Binning();

        Console.WriteLine();
        Console.WriteLine("D. Applying it to a frame");
        Applying();

        Console.WriteLine();
        Console.WriteLine("E. Why it acts before the shot-noise draw");
        ShotNoise();

        Console.WriteLine();
        Console.WriteLine("F. The shipped WFPC2/PC1 entry");
        CatalogueEntry();

        Console.WriteLine();
        Console.WriteLine("G. PC1's delivered width against the handbook's model PSFs (Table 5.3)");
        DeliveredWidth();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- A

    private static void TinyTim()
    {
        var k = ChargeDiffusion.Wfpc2TinyTimKernel;

        bool verbatim = true;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                if (k[r, c] != TinyTimPublished[r, c]) verbatim = false;
        Check("the kernel is wfpc2pc1.pup verbatim", verbatim, "centre 0.75, sides 0.05, corners 0.0125");

        double sum = Sum(k);
        Check("it sums to 1: diffusion moves charge and loses none", Math.Abs(sum - 1.0) < 1e-12, F(sum));

        // The guide calls it "a Gaussian kernel of 3 x 3 pixels", which cannot prefer a direction.
        Check("it is symmetric, as a Gaussian estimate must be",
              k[0, 1] == k[1, 0] && k[0, 1] == k[1, 2] && k[0, 1] == k[2, 1]
              && k[0, 0] == k[0, 2] && k[0, 0] == k[2, 0] && k[0, 0] == k[2, 2], "");
    }

    // ---------------------------------------------------------------- B

    private static void Handbook()
    {
        var k = ChargeDiffusion.Wfpc2TinyTimKernel;

        double printedSum = Sum(HandbookKernel);
        Check("the handbook's kernel sums to 1.002 as printed", Math.Abs(printedSum - 1.002) < 1e-9, F(printedSum));

        // "even when a pinhole was centered over a pixel only about 70% of the light was detected in
        // that pixel". The two published kernels straddle it.
        Check("the pinhole's 70 % lies between the handbook's kernel and Tiny Tim's",
              HandbookKernel[1, 1] < 0.70 && 0.70 < k[1, 1],
              P(HandbookKernel[1, 1]) + " < 70 % < " + P(k[1, 1]));

        // Each document also states the effect as a Gaussian jitter: 18 mas in the handbook, 14 mas
        // in the Tiny Tim guide. A jitter and a kernel describing one centred-pinhole test must leave
        // the same share of it in its own pixel, so each figure should sit with its own kernel.
        double share18 = CentredShare(0.018 / 0.0455);    // handbook Table 4.1 plate scale
        double share14 = CentredShare(0.014 / 0.04555);   // wfpc2pc1.pup plate scale
        Check("the handbook's 18 mas reproduces its own kernel's centre",
              Math.Abs(share18 - HandbookKernel[1, 1]) < Math.Abs(share14 - HandbookKernel[1, 1])
              && Math.Abs(share18 - HandbookKernel[1, 1]) < 0.02,
              P(share18) + " against " + P(HandbookKernel[1, 1]));
        Check("Tiny Tim's 14 mas sits nearer its own kernel than 18 mas does",
              Math.Abs(share14 - k[1, 1]) < Math.Abs(share18 - k[1, 1]),
              P(share14) + " and " + P(share18) + " against " + P(k[1, 1]));
    }

    // Share of a pixel-centred point that a Gaussian jitter of this RMS (pixels) leaves in the pixel.
    private static double CentredShare(double sigmaPx)
    {
        double e = Erf(0.5 / (sigmaPx * Math.Sqrt(2.0)));
        return e * e;
    }

    // Abramowitz and Stegun 7.1.26, good to 1.5e-7.
    private static double Erf(double x)
    {
        double sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);
        double t = 1.0 / (1.0 + 0.3275911 * x);
        double poly = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429))));
        return sign * (1.0 - poly * Math.Exp(-x * x));
    }

    // ---------------------------------------------------------------- C

    private static void Binning()
    {
        var k = ChargeDiffusion.Wfpc2TinyTimKernel;
        Check("binning 1 returns the native kernel", ReferenceEquals(ChargeDiffusion.ForBinning(k, 1), k), "");

        // Independently of ForBinning: light one binned pixel uniformly at native resolution,
        // diffuse it there, and bin the result. The asymmetric handbook kernel is used as well, so a
        // flipped axis cannot hide behind a symmetric one.
        foreach (var kernel in new[] { k, HandbookKernel })
        {
            string name = ReferenceEquals(kernel, k) ? "Tiny Tim" : "handbook";
            double worst = 0.0, prevCentre = kernel[1, 1];
            bool conserved = true, weakens = true;
            for (int b = 2; b <= 4; b++)
            {
                var binned = ChargeDiffusion.ForBinning(kernel, b);
                conserved &= Math.Abs(Sum(binned) - Sum(kernel)) < 1e-12;
                weakens &= binned[1, 1] > prevCentre;
                prevCentre = binned[1, 1];

                int w = 3 * b;
                var native = new float[w * w];
                for (int y = b; y < 2 * b; y++)
                    for (int x = b; x < 2 * b; x++)
                        native[y * w + x] = 1f;
                ChargeDiffusion.Apply(native, w, w, kernel);

                for (int by = 0; by < 3; by++)
                {
                    for (int bx = 0; bx < 3; bx++)
                    {
                        double block = 0.0;
                        for (int y = by * b; y < (by + 1) * b; y++)
                            for (int x = bx * b; x < (bx + 1) * b; x++)
                                block += native[y * w + x];
                        worst = Math.Max(worst, Math.Abs(block / (b * b) - binned[by, bx]));
                    }
                }
            }
            Check(name + ": binned kernel matches diffusing at native pixels then binning", worst < 1e-6,
                  "worst cell " + worst.ToString("E1", CultureInfo.InvariantCulture) + ", binning 2 to 4");
            Check(name + ": binning conserves charge", conserved, "");
            Check(name + ": wider bins keep more charge at home", weakens,
                  "centre " + P(kernel[1, 1]) + " native, " + P(prevCentre) + " at 4x4");
        }
    }

    // ---------------------------------------------------------------- D

    private static void Applying()
    {
        const int w = 9, h = 9;

        // The asymmetric kernel pins the orientation: a cell's value is what a source sends to the
        // pixel that many rows and columns away, so the row below receives the bottom row's 0.078.
        var point = new float[w * h];
        point[4 * w + 4] = 1000f;
        ChargeDiffusion.Apply(point, w, h, HandbookKernel);
        Check("a point source spreads by exactly the kernel, the right way up",
              Math.Abs(point[4 * w + 4] - 635f) < 1e-2
              && Math.Abs(point[3 * w + 4] - 67f) < 1e-2
              && Math.Abs(point[5 * w + 4] - 78f) < 1e-2
              && Math.Abs(point[4 * w + 3] - 80f) < 1e-2
              && Math.Abs(point[5 * w + 3] - 15f) < 1e-2,
              "centre " + F(point[4 * w + 4]) + ", above " + F(point[3 * w + 4])
              + ", below " + F(point[5 * w + 4]) + ", left " + F(point[4 * w + 3]));

        var k = ChargeDiffusion.Wfpc2TinyTimKernel;

        var interior = new float[w * h];
        interior[4 * w + 4] = 1000f;
        ChargeDiffusion.Apply(interior, w, h, k);
        double total = 0.0;
        foreach (float v in interior) total += v;
        Check("an interior source keeps all its charge", Math.Abs(total - 1000.0) < 1e-2, F(total) + " e-");

        // A uniform sky must come out unchanged, which is why the chain need not diffuse it.
        var flat = new float[w * h];
        for (int i = 0; i < flat.Length; i++) flat[i] = 1000f;
        ChargeDiffusion.Apply(flat, w, h, k);
        double worstEdge = 0.0;
        foreach (float v in flat) worstEdge = Math.Max(worstEdge, Math.Abs(v - 1000f));
        Check("a uniform field is unchanged, edges included", worstEdge < 1e-2, "worst " + F(worstEdge) + " e-");
    }

    // ---------------------------------------------------------------- E

    private static void ShotNoise()
    {
        // Each photoelectron wanders on its own, so diffusion splits a Poisson count into
        // independent Poisson counts: variance equals mean afterwards. Convolving counts already
        // drawn would instead scale the variance by the sum of the squared kernel cells.
        var k = ChargeDiffusion.Wfpc2TinyTimKernel;
        const int size = 256;
        const double mean = 50.0;
        int n = size * size;

        var cumulative = new double[9];
        double run = 0.0;
        for (int i = 0; i < 9; i++) { run += k[i / 3, i % 3]; cumulative[i] = run; }

        var rng = new Pcg32(20260913UL, Pcg32.StreamShotNoise);
        var electrons = new double[n];
        var drawn = new double[n];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int count = Poisson(rng, mean);
                drawn[y * size + x] = count;
                for (int e = 0; e < count; e++)
                {
                    double u = rng.NextDouble() * run;
                    int cell = 0;
                    while (cell < 8 && u > cumulative[cell]) cell++;
                    int ty = (y + cell / 3 - 1 + size) % size;
                    int tx = (x + cell % 3 - 1 + size) % size;
                    electrons[ty * size + tx] += 1.0;
                }
            }
        }

        var convolved = new double[n];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        convolved[y * size + x] += k[dy + 1, dx + 1]
                            * drawn[((y - dy + size) % size) * size + (x - dx + size) % size];

        double sumSquares = 0.0;
        foreach (double v in k) sumSquares += v * v;

        double perElectron = Dispersion(electrons, out double perElectronMean);
        double onCounts = Dispersion(convolved, out _);

        Check("electrons diffused one by one conserve the mean", Math.Abs(perElectronMean - mean) / mean < 0.005,
              F(perElectronMean) + " e- against " + F(mean));
        Check("and stay Poisson: variance over mean is 1", Math.Abs(perElectron - 1.0) < 0.02, F(perElectron));
        Check("convolving drawn counts would not: it gives the sum of squared cells",
              Math.Abs(onCounts - sumSquares) / sumSquares < 0.02,
              F(onCounts) + " against " + F(sumSquares) + ", shot noise " + P(1.0 - Math.Sqrt(sumSquares)) + " too low");
    }

    private static double Dispersion(double[] values, out double mean)
    {
        double s = 0.0, s2 = 0.0;
        foreach (double v in values) { s += v; s2 += v * v; }
        mean = s / values.Length;
        double variance = s2 / values.Length - mean * mean;
        return variance / mean;
    }

    private static int Poisson(Pcg32 rng, double mean)
    {
        double limit = Math.Exp(-mean), p = 1.0;
        int k = -1;
        do { k++; p *= rng.NextDouble(); } while (p > limit);
        return k;
    }

    // ---------------------------------------------------------------- F

    private static void CatalogueEntry()
    {
        var pc = VisualTelescopeCatalog.HubbleWfpc2Pc1;

        Check("PC1 carries Tiny Tim's kernel", ReferenceEquals(pc.ChargeDiffusionKernel, ChargeDiffusion.Wfpc2TinyTimKernel), "");
        Check("PC1 is a CCD", pc.Technology == DetectorTechnology.Ccd, "");
        Check("PC1 carries no interpixel-capacitance kernel", pc.InterpixelCapacitanceKernel == null,
              "IPC is readout crosstalk on an HgCdTe array, not diffusion");

        // The kernel is defined on PC pixels, so the entry must image at the scale it was made for.
        double scale = 206265.0 * pc.NativePixelSizeMeters / pc.FocalLengthMeters;
        Check("the entry's plate scale is the pup file's 0.04555 arcsec pixel",
              Math.Abs(scale - 0.04555) / 0.04555 < 0.002, F(scale) + " arcsec/px");

        // The chain silently skips any kernel that is not 3 x 3, so a malformed one would not show.
        int malformed = 0, withKernel = 0;
        foreach (var s in VisualTelescopeCatalog.All)
        {
            var kernel = s.ChargeDiffusionKernel;
            if (kernel == null) continue;
            withKernel++;
            if (kernel.GetLength(0) != 3 || kernel.GetLength(1) != 3 || Math.Abs(Sum(kernel) - 1.0) > 0.005
                || s.Technology != DetectorTechnology.Ccd)
                malformed++;
        }
        Check("every diffusion kernel on the roster is a 3 x 3 on a CCD that conserves charge", malformed == 0,
              withKernel + " carrying one, " + malformed + " malformed");
    }

    // ---------------------------------------------------------------- G

    // WFPC2 IHB (Cycle 12) Table 5.3, peak near the centre of a pixel: the central pixel's share of
    // the 5 x 5 flux, in percent, for the model PSF and for the diffraction-limited one.
    private static readonly (double Nm, double Model, double DiffractionLimited)[] Table53 =
    {
        (200.0, 25.0, 62.9),
        (400.0, 26.1, 49.4),
        (600.0, 20.7, 31.6),
        (800.0, 15.4, 22.5),
    };

    private static void DeliveredWidth()
    {
        var pc = VisualTelescopeCatalog.HubbleWfpc2Pc1;
        var curve = pc.SpacePlatform.DeliveredPsfFwhmArcsec;
        double scale = 206265.0 * pc.NativePixelSizeMeters / pc.FocalLengthMeters;

        // The model carries the observed wavefront errors and Sect. 5.4's kernel, and no jitter. Its
        // ratio to the diffraction-limited peak is checked rather than the peak itself, because the
        // star is only near the centre and both columns share that offset.
        foreach (var row in Table53)
        {
            double lambda = row.Nm * 1e-9;
            double width = curve.At(lambda);
            double wavefront = WavefrontFor(pc, width, lambda, scale);

            var optics = Kernel(pc, scale, lambda, wavefront, out int r);
            ChargeDiffusion.Apply(optics, 2 * r + 1, 2 * r + 1, HandbookKernel);
            var diffraction = Kernel(pc, scale, lambda, 0.0, out int rd);

            double ratio = PeakShare(optics, r) / PeakShare(diffraction, rd);
            double published = row.Model / row.DiffractionLimited;

            // 2 %: the table's 0.1 rounding and the widths' 1 mas rounding.
            Check(F(row.Nm) + " nm: " + F(1000.0 * width) + " mas gives Table 5.3's peak ratio",
                  Math.Abs(ratio / published - 1.0) < 0.02,
                  F(Math.Round(ratio, 4)) + " against " + F(Math.Round(published, 4)));
        }
    }

    private static float[] Kernel(VisualTelescopeSpec s, double scale, double lambda, double gaussianFwhm, out int r)
        => OpticalPsf.BuildKernel(scale, s.ApertureMeters, s.SecondaryObstructionFraction, lambda, 0.0, 0.0,
                                  s.SpiderVaneCount, s.SpiderVaneWidthMeters, gaussianFwhm, s.PrimaryMirrorPads, out r);

    // The Gaussian whose convolution with the pupil's diffraction has this FWHM before pixelation,
    // measured on a grid nine times finer than the pixel. Independent of GaussianFwhmForDelivered.
    private static double WavefrontFor(VisualTelescopeSpec s, double widthArcsec, double lambda, double scale)
    {
        double fine = scale / 9.0;
        double lo = 0.0, hi = 0.1;
        for (int i = 0; i < 12; i++)
        {
            double mid = 0.5 * (lo + hi);
            var k = Kernel(s, fine, lambda, mid, out int r);
            if (OpticalPsf.MeasureKernelFwhmArcsec(k, r, fine) < widthArcsec) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    private static double PeakShare(float[] kernel, int r)
    {
        int size = 2 * r + 1;
        double sum = 0.0;
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
                sum += kernel[(y + r) * size + x + r];
        return kernel[r * size + r] / sum;
    }

    private static double Sum(double[,] kernel)
    {
        double s = 0.0;
        foreach (double v in kernel) s += v;
        return s;
    }
}
