using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using ExoInstruments.Core;

// Headless checks on inverting a published delivered PSF width into the kernel's Gaussian term
// (OpticalPsf.GaussianFwhmForDelivered), and on the plane each shipped entry files its width under.
//
// Run:  dotnet run -c Release -p:Core=../../ExoInstruments/Core
internal static class DeliveredPsfTests
{
    private const double ArcsecPerRad = 180.0 * 3600.0 / Math.PI;
    private const double FwhmPerSigma = 2.3548200450309493;

    // Sampling of the reference profile, in lambda/D: four times the solve's own twelve.
    private const double ReferenceSamplesPerLambdaOverD = 48.0;

    // Most reference samples across a pixel, which keeps a 4x4 pixel's grid small.
    private const int MaxSamplesPerPixel = 64;

    // Agreement asked of a reproduced width. Table 6.7's arcsec column is rounded to 0.0005, which
    // is 0.75 % of its 0.067, so a quarter of a per cent is well inside what the source can say.
    private const double RelativeTolerance = 0.0025;

    private static int failures;

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) failures++;
    }

    private static string As(double arcsec) => arcsec.ToString("F4", CultureInfo.InvariantCulture) + "\"";

    private static string Pc(double fraction)
        => Math.Round(100.0 * fraction, 2).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " %";

    private static int Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var clock = Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine("A. What each shipped width is, against its source");
        Sourcing();

        Console.WriteLine();
        Console.WriteLine("B. WFC3/UVIS, IHB Table 6.7, before pixelation");
        BeforePixelation();

        Console.WriteLine();
        Console.WriteLine("C. Widths measured in the image, at native pixels");
        ImagePlane();

        Console.WriteLine();
        Console.WriteLine("D. Why the solve cannot look through the binned pixel");
        BinnedPixel();

        Console.WriteLine();
        Console.WriteLine($"{clock.Elapsed.TotalSeconds:F0} s");
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- A

    // WFC3 IHB Table 6.7, "WFC3/UVIS PSF FWHM (pre-pixelation, in units of pixels and arcseconds)",
    // both columns as published.
    private static readonly double[] Table67Nm = { 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100 };
    private static readonly double[] Table67Pixels = { 2.069, 1.870, 1.738, 1.675, 1.681, 1.746, 1.844, 1.960, 2.091, 2.236 };
    private static readonly double[] Table67Arcsec = { 0.083, 0.075, 0.070, 0.067, 0.067, 0.070, 0.074, 0.078, 0.084, 0.089 };

    // WFC3 IHB Table 7.5, the IR channel's counterpart, captioned "before pixelation".
    private static readonly double[] Table75Nm = { 800, 900, 1000, 1100, 1200, 1300, 1400, 1500, 1600, 1700 };
    private static readonly double[] Table75Pixels = { 0.971, 0.986, 1.001, 1.019, 1.040, 1.067, 1.100, 1.136, 1.176, 1.219 };
    private static readonly double[] Table75Arcsec = { 0.124, 0.126, 0.128, 0.130, 0.133, 0.137, 0.141, 0.145, 0.151, 0.156 };

    private static void Sourcing()
    {
        SpacePlatformSpec uvis = VisualTelescopeCatalog.HubbleWfc3Uvis.SpacePlatform;
        Check("WFC3/UVIS carries Table 6.7's arcsec column", Carries(uvis, Table67Nm, Table67Arcsec), "ten rows, 200 to 1100 nm");
        Check("and files it before pixelation, as the caption does",
              uvis.DeliveredPsfFwhmPlane == PsfWidthPlane.BeforePixelation, "");
        OneScale("Table 6.7", Table67Pixels, Table67Arcsec);

        SpacePlatformSpec ir = VisualTelescopeCatalog.HubbleWfc3Ir.SpacePlatform;
        Check("WFC3/IR carries Table 7.5's arcsec column", Carries(ir, Table75Nm, Table75Arcsec), "ten rows, 800 to 1700 nm");
        Check("and files it before pixelation, as the caption does",
              ir.DeliveredPsfFwhmPlane == PsfWidthPlane.BeforePixelation, "");
        OneScale("Table 7.5", Table75Pixels, Table75Arcsec);

        // ACS IHB Sect. 5.6.6 gives each channel a range in F550M, in a paragraph about CCD charge diffusion.
        SpacePlatformSpec wfc = VisualTelescopeCatalog.HubbleAcsWfc.SpacePlatform;
        Check("ACS/WFC carries the midpoint of Sect. 5.6.6's 0.10 to 0.13\"",
              Math.Abs(wfc.DeliveredPsfFwhmArcsec.At(550e-9) - 0.5 * (0.10 + 0.13)) < 1e-12, "");
        Check("and files it as an image width", wfc.DeliveredPsfFwhmPlane == PsfWidthPlane.NativePixels, "");

        SpacePlatformSpec hrc = VisualTelescopeCatalog.HubbleAcsHrc.SpacePlatform;
        Check("ACS/HRC carries the midpoint of Sect. 5.6.6's 0.060 to 0.073\"",
              Math.Abs(hrc.DeliveredPsfFwhmArcsec.At(550e-9) - 0.5 * (0.060 + 0.073)) < 1e-12, "");
        Check("and files it as an image width", hrc.DeliveredPsfFwhmPlane == PsfWidthPlane.NativePixels, "");

        // Weaver et al. 2020 Sect. 3.1: Gaussian fits to stars in 1x1 flight frames, (2.06, 2.65) px.
        VisualTelescopeSpec lorri = VisualTelescopeCatalog.NewHorizonsLorri;
        double shipped = lorri.SpacePlatform.DeliveredPsfFwhmArcsec.At(607.6e-9);
        double mean = Math.Sqrt(2.06 * 2.65) * NativeScale(lorri);
        Check("LORRI carries the geometric mean of Weaver et al.'s (2.06, 2.65) px",
              Math.Abs(shipped - mean) <= 0.005, $"{As(mean)} at this plate scale, against {As(shipped)}");
        Check("and files it as an image width", lorri.SpacePlatform.DeliveredPsfFwhmPlane == PsfWidthPlane.NativePixels, "");
    }

    private static bool Carries(SpacePlatformSpec platform, double[] nm, double[] arcsec)
    {
        for (int i = 0; i < nm.Length; i++)
            if (Math.Abs(platform.DeliveredPsfFwhmArcsec.At(nm[i] * 1e-9) - arcsec[i]) > 1e-12) return false;
        return true;
    }

    // A pre-pixelation table gives ONE width in two units, so every row's arcsec over pixels must admit a
    // single plate scale within both columns' rounding. A pixel column measured through the pixel would not.
    private static void OneScale(string table, double[] pixels, double[] arcsec)
    {
        double lo = 0.0, hi = double.MaxValue;
        for (int i = 0; i < pixels.Length; i++)
        {
            lo = Math.Max(lo, (arcsec[i] - 0.0005) / (pixels[i] + 0.0005));
            hi = Math.Min(hi, (arcsec[i] + 0.0005) / (pixels[i] - 0.0005));
        }
        Check($"{table}'s two columns are one width at one plate scale", lo <= hi,
              $"every row admits {lo:F5} to {hi:F5}\"/px");
    }

    // ---------------------------------------------------------------- B

    private static void BeforePixelation()
    {
        VisualTelescopeSpec s = VisualTelescopeCatalog.HubbleWfc3Uvis;
        double native = NativeScale(s);
        Console.WriteLine("      nm   table    Gaussian  optics    1x1 image  4x4 image");

        var clock = new Stopwatch();
        int solves = 0;
        for (int i = 0; i < Table67Nm.Length; i++)
        {
            // 1100 nm is past the detector's 1000 nm and below the aperture's own limit; see spacecraft-tests.
            if (Table67Nm[i] > s.DetectorMaxWavelengthNm) continue;

            double lambda = Table67Nm[i] * 1e-9, table = Table67Arcsec[i];
            clock.Start();
            double g = Solve(s, lambda, table);
            clock.Stop();
            solves++;

            double optics = Width(s, lambda, g, 0.0, table);
            double one = Width(s, lambda, g, native, table);
            double four = Width(s, lambda, g, 4.0 * native, table);
            Console.WriteLine($"    {Table67Nm[i],4:F0}   {table:F4}   {g:F4}    {optics:F4}    {one:F4}     {four:F4}");

            Check($"{Table67Nm[i]:F0} nm: the optics deliver Table 6.7's {As(table)}", Near(optics, table),
                  $"{As(optics)}, {Pc(optics / table - 1.0)}");
            Check($"{Table67Nm[i]:F0} nm: a pixelated image is wider, as the caption says a fit will be",
                  one > table && four > one, $"1x1 {As(one)}, 4x4 {As(four)}");
        }
        Console.WriteLine($"    {clock.Elapsed.TotalMilliseconds / Math.Max(1, solves):F0} ms per solve on this vaned, padded pupil");
    }

    // ---------------------------------------------------------------- C

    private static void ImagePlane()
    {
        ImageWidth(VisualTelescopeCatalog.HubbleAcsWfc, 550e-9, "ACS/WFC in F550M");
        ImageWidth(VisualTelescopeCatalog.HubbleAcsHrc, 550e-9, "ACS/HRC in F550M");
        ImageWidth(VisualTelescopeCatalog.NewHorizonsLorri, 607.6e-9, "LORRI at its pivot");
    }

    private static void ImageWidth(VisualTelescopeSpec s, double lambda, string label)
    {
        double published = s.SpacePlatform.DeliveredPsfFwhmArcsec.At(lambda);
        double native = NativeScale(s);
        double g = Solve(s, lambda, published);
        double image = Width(s, lambda, g, native, published);
        double optics = Width(s, lambda, g, 0.0, published);
        Check($"{label}: {As(published)} delivered in the image at native pixels", Near(image, published),
              $"{As(image)}, {Pc(image / published - 1.0)}; the optics alone {As(optics)}");
    }

    // ---------------------------------------------------------------- D

    // Matching Table 6.7 through the frame's own 4x4 kernel, as the solve once did. The row read there is
    // mostly one pixel wide before any optics are added, so the match can only return zero.
    private static void BinnedPixel()
    {
        VisualTelescopeSpec s = VisualTelescopeCatalog.HubbleWfc3Uvis;
        const double lambda = 500e-9, table = 0.067;
        double binned = 4.0 * NativeScale(s);

        float[] k = OpticalPsf.BuildKernel(binned, s.ApertureMeters, s.SecondaryObstructionFraction, lambda,
                                           0.0, 0.0, s.SpiderVaneCount, s.SpiderVaneWidthMeters, 0.0,
                                           s.PrimaryMirrorPads, out int r);
        double kernel = OpticalPsf.MeasureKernelFwhmArcsec(k, r, binned);
        double diffraction = Width(s, lambda, 0.0, 0.0, table);

        Check("at 4x4 the diffraction kernel alone measures wider than Table 6.7's 500 nm row", kernel > table,
              $"{As(kernel)} against {As(table)}, so a match there adds nothing");
        Check("while the diffraction it would leave is narrower than that row", diffraction < table,
              $"{As(diffraction)}, {Pc(diffraction / table - 1.0)}");
    }

    // ---------------------------------------------------------------- the reference

    private static double NativeScale(VisualTelescopeSpec s)
        => ArcsecPerRad * s.NativePixelSizeMeters / s.FocalLengthMeters;

    // The solve exactly as the camera calls it: the entry's own plane, and the unbinned plate scale.
    private static double Solve(VisualTelescopeSpec s, double lambda, double delivered)
        => OpticalPsf.GaussianFwhmForDelivered(delivered, s.SpacePlatform.DeliveredPsfFwhmPlane, NativeScale(s),
                                               s.ApertureMeters, s.SecondaryObstructionFraction, lambda,
                                               s.SpiderVaneCount, s.SpiderVaneWidthMeters, s.PrimaryMirrorPads);

    private static bool Near(double measured, double published)
        => Math.Abs(measured / published - 1.0) <= RelativeTolerance;

    // Half-power width along the central row of the pupil's pattern convolved with a Gaussian and, when
    // pixelArcsec is positive, integrated over a square pixel: the width a fit to stars at every sub-pixel
    // phase converges on. Nothing of OpticalPsf's kernel builder or solve is in it: the pattern comes from
    // PupilDiffraction at points, and the Gaussian and the pixel are one analytic filter, a difference of
    // error functions. The grid runs past the crossing by the filter's whole reach, so nothing it reads is cut.
    private static double Width(VisualTelescopeSpec s, double lambda, double gauss, double pixelArcsec, double roughWidth)
    {
        double lambdaOverD = lambda / s.ApertureMeters * ArcsecPerRad;
        double h = Math.Max(lambdaOverD / ReferenceSamplesPerLambdaOverD, pixelArcsec / MaxSamplesPerPixel);
        double sigma = gauss / FwhmPerSigma;

        int rowReach = (int)Math.Ceiling(0.5 * (1.3 * roughWidth + 1.2 * pixelArcsec + 1.1 * lambdaOverD) / h) + 2;
        int filterReach = (int)Math.Ceiling((5.0 * sigma + 0.5 * pixelArcsec) / h) + 1;
        int radius = rowReach + filterReach;
        int size = 2 * radius + 1;

        bool vanes = s.SpiderVaneCount > 0 && s.SpiderVaneWidthMeters > 0.0;
        var grid = new double[size * size];
        Parallel.For(0, radius + 1,
            () => new PupilDiffraction(s.ApertureMeters, s.SecondaryObstructionFraction, lambda,
                                       vanes ? s.SpiderVaneCount : 0, vanes ? s.SpiderVaneWidthMeters : 0.0,
                                       0.0, s.PrimaryMirrorPads),
            (v, _, pupil) =>
            {
                for (int u = v == 0 ? 0 : -radius; u <= radius; u++)
                {
                    double value = pupil.IntensityArcsec(u * h, v * h);
                    grid[(v + radius) * size + u + radius] = value;
                    grid[(radius - v) * size + radius - u] = value;
                }
                return pupil;
            },
            _ => { });

        var filter = new double[2 * filterReach + 1];
        for (int k = -filterReach; k <= filterReach; k++)
            filter[k + filterReach] = FilterWeight(k * h, sigma, pixelArcsec, h);

        var column = new double[size];
        for (int u = -radius; u <= radius; u++)
        {
            double sum = 0.0;
            for (int k = -filterReach; k <= filterReach; k++)
                sum += grid[(k + radius) * size + u + radius] * filter[k + filterReach];
            column[u + radius] = sum;
        }

        Func<int, double> row = x =>
        {
            double sum = 0.0;
            for (int k = -filterReach; k <= filterReach; k++)
                sum += column[x - k + radius] * filter[k + filterReach];
            return sum;
        };

        double peak = row(0), prev = peak;
        for (int x = 1; x <= rowReach; x++)
        {
            double cur = row(x);
            if (cur <= 0.5 * peak) return 2.0 * (x - 1 + (prev - 0.5 * peak) / (prev - cur)) * h;
            prev = cur;
        }
        throw new InvalidOperationException("half power not reached inside the reference grid");
    }

    // A Gaussian of this sigma convolved with a box of this width, at offset x. Below half a sample the
    // Gaussian is a point, and the box is then weighted by how much of each sample's cell it covers.
    private static double FilterWeight(double x, double sigma, double width, double h)
    {
        if (sigma < 0.5 * h)
        {
            if (!(width > 0.0)) return Math.Abs(x) < 0.5 * h ? 1.0 : 0.0;
            return Math.Max(0.0, Math.Min(1.0, (0.5 * width + 0.5 * h - Math.Abs(x)) / h));
        }
        if (!(width > 0.0)) return Math.Exp(-0.5 * x * x / (sigma * sigma));
        double scale = 1.0 / (sigma * Math.Sqrt(2.0));
        return 0.5 * (Erf((x + 0.5 * width) * scale) - Erf((x - 0.5 * width) * scale));
    }

    // Abramowitz and Stegun 7.1.26, good to 1.5e-7.
    private static double Erf(double x)
    {
        double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
        double y = 1.0 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592)
                   * t * Math.Exp(-x * x);
        return x < 0.0 ? -y : y;
    }
}
