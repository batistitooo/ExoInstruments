using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using ExoInstruments.Core;

/// <summary>
/// Where a capture's seconds go, and whether spending them on several cores changes the frame.
///
/// WHY IT EXISTS. Baptiste's report was that a galaxy photograph on the RC20 at 4x4 binning took
/// thirty seconds or more. At 4x4 that frame is 1036x705 = 0.73 Mpx, so thirty seconds is not
/// "a big frame"; something in the pipeline cost far more per pixel than the pixel count
/// suggested, and reading the code was not going to say which. This times the shipped stages at
/// the shipped parameters.
///
/// WHAT IT ALSO CHECKS. Several stages now run across cores. A stage allowed to do that must
/// return the same frame however many workers it had, or the seed recorded in the FITS header
/// stops reproducing the exposure. --determinism runs each parallel stage at one worker and at
/// the machine's full count and compares the results BIT FOR BIT, which is the only standard
/// worth applying to it.
///
/// The two frame-sized loops that live in the Unity layer (SolarSystemCameraTexture's emission
/// fill and its detector chain) cannot be compiled headless, so they are reproduced here call for
/// call against the same Core entry points. Everything else is the shipped code itself.
///
///   dotnet run -c Release -p:Core=../../ExoInstruments/Core -- [dataDir] [binning] [options]
///     --repeat N       time each stage N times and report the fastest (default 3)
///     --workers N      pin the worker count instead of using the machine's
///     --determinism    check every parallel stage against its own serial result
/// </summary>
internal static class Program
{
    private const double ArcsecPerRad = 206264.80624709636;

    // ---- The instrument, exactly as VisualTelescopeCatalog.Rc20 declares it ----------------
    private const double Aperture = 0.51;
    private const double Obstruction = 0.39;
    private const double FocalLength = 0.51 * 6.8;
    private const double Barlow = 4.0;
    private const double NativePixelMeters = 4.63e-6;
    private const int NativeW = 4144, NativeH = 2822;
    private const double ZenithSeeing = 2.5;          // OHP median, Schmitt et al. 2024

    // ---- Pointed at M51 from OHP, an hour east of the meridian -----------------------------
    // --target ra,dec moves it. The Horsehead (85.24,-2.46) is the field to check the
    // high-resolution patch layer on, because that path carries a per-worker run cursor and is
    // the one place where sharing state between workers could actually go wrong.
    private static double RaDeg = 202.4696, DecDeg = 47.1952;
    private const double LatDeg = 43.9308;

    private static int repeats = 3;
    private static string dataDir;
    private static int bin = 4;

    private static int w, h;
    private static double plateScale, seeing, zenithDistance;
    private static GnomonicProjection projection;
    private static double meridianRa;

    private static EmissionMap emissionMap;
    private static EmissionPatchSet patchSet;
    private static List<EmissionPatchSet.Patch> patchList;
    private static HorizontalToGalactic rotation;
    private static List<EmissionLines.Line> lines;
    private static double[] lineCoefficients;

    private static GalaxyImage galaxy;
    private static double[] galaxyTransform;
    private static double[] galaxyBoundsX, galaxyBoundsY;

    private static ChromaticSubBand[] subBands;

    private static int Main(string[] args)
    {
        // An empty first argument means "the installed one", so a caller can pass the binning
        // without having to spell the path out.
        dataDir = args.Length > 0 && args[0].Length > 0 && !args[0].StartsWith("--")
            ? args[0]
            : "/Users/baptiste/Library/Application Support/Steam/steamapps/common/" +
              "Kerbal Space Program/GameData/ExoInstruments/PluginData";
        if (args.Length > 1 && !args[1].StartsWith("--")) bin = int.Parse(args[1]);

        bool determinism = Array.IndexOf(args, "--determinism") >= 0;
        int pinnedWorkers = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--repeat") repeats = int.Parse(args[i + 1]);
            if (args[i] == "--workers") pinnedWorkers = int.Parse(args[i + 1]);
            if (args[i] == "--target")
            {
                string[] parts = args[i + 1].Split(',');
                RaDeg = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                DecDeg = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        if (pinnedWorkers > 0) ParallelWork.UseWorkers(pinnedWorkers);

        SetUpGeometry();
        LoadData();

        Console.WriteLine($"{Environment.ProcessorCount} cores reported, {ParallelWork.MaxWorkers} workers used");
        Console.WriteLine(patchList != null
            ? $"high-resolution patches over this field: {patchList.Count}"
            : "no high-resolution patch over this field");
        Console.WriteLine();

        if (Array.IndexOf(args, "--accuracy") >= 0) return CheckConvolutionAccuracy();
        if (determinism) return CheckDeterminism();

        Console.WriteLine("stage                                       ms");
        Console.WriteLine("------------------------------------------------");

        var plane = new float[w * h];
        var scratch = new float[w * h];
        double total = 0.0;
        total += Time($"emission fill ({(double)w * h * bin * bin / 1e6:F1} M samples)",
                      () => FillEmission(plane));
        total += Time("galaxy deposit", () => DepositGalaxy(plane));

        float[] kernel = null;
        int radius = 0;
        total += Time("PSF kernel (12 sub-bands)", () => kernel = BuildPsf(out radius));
        Console.WriteLine($"  kernel radius {radius} px ({2 * radius + 1}x{2 * radius + 1})");

        total += Time("PSF convolution", () =>
        {
            Array.Copy(plane, scratch, plane.Length);
            FourierConvolution.Convolve(scratch, w, h, kernel, radius);
        });
        total += Time("detector: Poisson", () => Poisson(plane));
        total += Time("detector: read noise + digitise", () => ReadAndDigitise(plane));

        Console.WriteLine("------------------------------------------------");
        Console.WriteLine($"{"TOTAL",-41} {total,8:F1}");
        return 0;
    }

    // ------------------------------------------------------------------ set-up

    private static void SetUpGeometry()
    {
        w = NativeW / bin;
        h = NativeH / bin;
        plateScale = NativePixelMeters * bin / (FocalLength * Barlow) * ArcsecPerRad;
        double fovDeg = w * plateScale / 3600.0;

        Console.WriteLine($"RC20, binning {bin}x{bin}, Barlow in: {w}x{h} = {(double)w * h / 1e6:F2} Mpx");
        Console.WriteLine($"plate scale {plateScale:F4} arcsec/px, field {fovDeg * 60:F2} x {fovDeg * 60 * h / w:F2} arcmin");

        meridianRa = RaDeg - 15.0;                    // one hour of hour angle
        var horizontal = SkyCoordinates.EquatorialToHorizontal(RaDeg, DecDeg, meridianRa, LatDeg);
        zenithDistance = 90.0 - horizontal.AltitudeDeg;

        SkyVector boresight = SkyVector.FromHorizontal(horizontal.AltitudeDeg, horizontal.AzimuthDeg);
        var zenith = new SkyVector(0, 0, 1);
        double d = zenith.Dot(boresight);
        SkyVector up = SkyVector.Normalized(zenith.X - d * boresight.X,
                                            zenith.Y - d * boresight.Y,
                                            zenith.Z - d * boresight.Z);
        SkyVector right = SkyVector.Normalized(up.Y * boresight.Z - up.Z * boresight.Y,
                                               up.Z * boresight.X - up.X * boresight.Z,
                                               up.X * boresight.Y - up.Y * boresight.X);
        projection = new GnomonicProjection(boresight, up, right, fovDeg, w, h);

        double airmass = 1.0 / Math.Cos(zenithDistance * Math.PI / 180.0);
        seeing = ZenithSeeing * Math.Pow(airmass, 0.6);
        Console.WriteLine($"target altitude {horizontal.AltitudeDeg:F1} deg, seeing {seeing:F2} arcsec " +
                          $"= {seeing / plateScale:F1} px");
    }

    private static void LoadData()
    {
        emissionMap = new EmissionMap();
        emissionMap.Load(System.IO.Path.Combine(dataDir, "HalphaMap.emission"));

        patchSet = new EmissionPatchSet();
        try { patchSet.Load(System.IO.Path.Combine(dataDir, "HalphaPatches.patchset")); } catch { }

        rotation = HorizontalToGalactic.Build(meridianRa, LatDeg);

        double fieldRadiusDeg = 0.5 * Math.Sqrt((double)w * w + (double)h * h) * plateScale / 3600.0;
        patchList = patchSet.IsLoaded ? patchSet.FindOverlappingPatches(RaDeg, DecDeg, fieldRadiusDeg) : null;
        if (patchList != null && patchList.Count == 0) patchList = null;

        lines = new List<EmissionLines.Line>(NebularLineRatios.DerivableLines);
        lineCoefficients = new double[lines.Count];
        for (int i = 0; i < lines.Count; i++) lineCoefficients[i] = 1e-4;

        var images = new GalaxyImageSet();
        images.Load(System.IO.Path.Combine(dataDir, "GalaxyImages.galimg"));
        foreach (string name in new[] { "NGC5194", "M51", "NGC0224", "NGC5457" })
        {
            if (images.Describe(name) == null) continue;
            galaxy = images.Fetch(name);
            break;
        }
        if (galaxy == null)
            foreach (string name in images.Names) { galaxy = images.Fetch(name); break; }

        if (galaxy != null)
        {
            // The map laid over the whole frame, which is what a galaxy portrait is and the
            // expensive case for the deposit.
            galaxyBoundsX = new double[] { 0, w, 0, w };
            galaxyBoundsY = new double[] { 0, 0, h, h };
            galaxyTransform = GalaxyImageRenderer.SolveFrameToMap(
                galaxyBoundsX, galaxyBoundsY,
                new double[] { 0, galaxy.Size, 0, galaxy.Size },
                new double[] { 0, 0, galaxy.Size, galaxy.Size });
            Console.WriteLine($"galaxy {galaxy.Name}: {galaxy.Size}x{galaxy.Size} map, " +
                              $"{galaxy.HalfWidthArcsec / 30.0:F1} arcmin across");
        }

        // The L filter's passband split into the shipped twelve sub-bands. The weights' values do
        // not change the cost; the count, the wavelengths and the dispersion offsets do.
        const double centre = 552.5e-9, bandwidth = 2650e-10;
        double lo = centre - 0.75 * bandwidth, hi = centre + 0.75 * bandwidth;
        subBands = new ChromaticSubBand[12];
        for (int i = 0; i < subBands.Length; i++)
        {
            double lambda = lo + (i + 0.5) * (hi - lo) / subBands.Length;
            double offset = AtmosphericRefraction.DifferentialRefractionArcsec(
                centre * 1e6, lambda * 1e6, zenithDistance, 10.0, 940.0, 6.0) / plateScale;
            subBands[i] = new ChromaticSubBand
            {
                WavelengthMeters = lambda,
                Weight = 1.0,
                OffsetY = double.IsNaN(offset) ? 0.0 : offset,
            };
        }
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ timing

    /// <summary>
    /// Repeats a stage and reports the FASTEST run.
    ///
    /// The minimum, not the mean: this machine normally has KSP itself running while a capture is
    /// timed, so a slow run measures how much of the machine the game happened to be holding while
    /// the fastest one measures the code. The mean of a contended benchmark measures the
    /// contention.
    /// </summary>
    private static double Time(string label, Action work)
    {
        double best = double.MaxValue;
        for (int i = 0; i < repeats; i++)
        {
            var sw = Stopwatch.StartNew();
            work();
            sw.Stop();
            if (sw.Elapsed.TotalMilliseconds < best) best = sw.Elapsed.TotalMilliseconds;
        }
        Console.WriteLine($"{label,-41} {best,8:F1}");
        return best;
    }

    // ------------------------------------------------------------------ emission fill

    /// <summary>One worker's stencil buffers and patch run cursors, as SolarSystemCameraTexture holds them.</summary>
    private sealed class Scratch
    {
        public readonly long[] Pixels;
        public readonly double[] Weights;
        public EmissionPatchSet.Cursor Cursor;
        public Scratch(int patchCount)
        {
            EmissionMap.AllocateScratch(out Pixels, out Weights);
            Cursor = EmissionPatchSet.Cursor.New(patchCount);
        }
    }

    /// <summary>SolarSystemCameraTexture.DepositEmissionField, call for call.</summary>
    private static void FillEmission(float[] signal)
    {
        Array.Clear(signal, 0, signal.Length);
        double subStep = 1.0 / bin;
        var rowRayleighs = new double[h];

        Action<int, Scratch> fillRow = (y, scratch) =>
        {
            double rowSum = 0.0;
            for (int x = 0; x < w; x++)
            {
                double rSum = 0.0;
                int rCount = 0;
                for (int sy = 0; sy < bin; sy++)
                for (int sx = 0; sx < bin; sx++)
                {
                    SkyVector direction = projection.Deproject(x + (sx + 0.5) * subStep,
                                                               y + (sy + 0.5) * subStep);
                    rotation.ToGalactic(direction, out double l, out double b);

                    double sample = double.NaN;
                    bool fromPatch = false;
                    if (patchList != null)
                    {
                        for (int pi = 0; pi < patchList.Count; pi++)
                        {
                            if (!patchSet.TryRayleighsAtGalactic(patchList[pi], pi, l, b,
                                    scratch.Pixels, scratch.Weights, ref scratch.Cursor, out sample)) continue;
                            fromPatch = true;
                            break;
                        }
                    }
                    if (!fromPatch) sample = emissionMap.RayleighsAtGalactic(l, b, scratch.Pixels, scratch.Weights);
                    if (double.IsNaN(sample)) continue;
                    rSum += sample;
                    rCount++;
                }
                if (rCount == 0) continue;
                double r = rSum / rCount;
                if (!(r > 0.0)) continue;

                var ratios = new NebularLineRatios.RatioSet(r);
                double pixelRayleighs = 0.0, pixelElectrons = 0.0;
                for (int i = 0; i < lines.Count; i++)
                {
                    double ratio = ratios.RatioToHalpha(lines[i]);
                    if (double.IsNaN(ratio) || !(ratio > 0.0)) continue;
                    double lineR = r * ratio;
                    pixelRayleighs += lineR;
                    pixelElectrons += lineR * lineCoefficients[i];
                }
                rowSum += pixelRayleighs;
                if (pixelElectrons > 0.0) signal[y * w + x] += (float)pixelElectrons;
            }
            rowRayleighs[y] = rowSum;
        };

        int patchCount = patchList != null ? patchList.Count : 1;
        if (ParallelWork.Worthwhile((long)w * h * bin * bin))
            Parallel.For(0, h, ParallelWork.Options,
                () => new Scratch(patchCount),
                (y, state, scratch) => { fillRow(y, scratch); return scratch; },
                scratch => { });
        else
        {
            var scratch = new Scratch(patchCount);
            for (int y = 0; y < h; y++) fillRow(y, scratch);
        }
    }

    // ------------------------------------------------------------------ galaxy, PSF, detector

    private static void DepositGalaxy(float[] plane)
    {
        if (galaxy == null || galaxyTransform == null) return;
        GalaxyImageRenderer.Deposit(plane, w, h, galaxy, galaxyTransform, 552.5, 1e7,
                                    galaxyBoundsX, galaxyBoundsY);
    }

    private static float[] BuildPsf(out int radius)
        => OpticalPsf.BuildChromaticKernel(plateScale, Aperture, Obstruction, seeing, 552.5e-9,
                                           0.0, 0, 0.0, null, subBands, out radius);

    private static void Poisson(float[] signal)
    {
        var raw = new float[signal.Length];
        var rng = new Pcg32(12345UL, Pcg32.StreamShotNoise);
        for (int i = 0; i < signal.Length; i++)
            raw[i] = (float)NoiseSampler.Poisson(rng, Math.Max(0.0, signal[i] + 120.0 + 1.06));
        GC.KeepAlive(raw);
    }

    private static void ReadAndDigitise(float[] signal)
    {
        var raw = (float[])signal.Clone();
        var rng = new Pcg32(12345UL, Pcg32.StreamReadNoise);
        for (int i = 0; i < raw.Length; i++) raw[i] += (float)NoiseSampler.Gaussian(rng, 1.2);
        for (int i = 0; i < raw.Length; i++)
        {
            double adu = Math.Floor(raw[i] / 4.029 + 500.0);
            raw[i] = (float)Math.Min(16383.0, Math.Max(0.0, adu));
        }
        GC.KeepAlive(raw);
    }

    // ------------------------------------------------------------------ determinism

    /// <summary>
    /// Every stage that runs across cores, run at one worker and at the machine's full count, and
    /// the two results compared bit for bit.
    ///
    /// Bit for bit and not "to a tolerance", because the claim being checked is not that the
    /// parallel result is close: it is that splitting the work cannot change it at all. A stage
    /// that only nearly agrees has an accumulation crossing workers in it, and the exposure's
    /// recorded seed would stop reproducing the frame.
    /// </summary>
    private static int CheckDeterminism()
    {
        int full = Math.Max(1, Environment.ProcessorCount - 1);
        int failures = 0;

        Console.WriteLine($"one worker against {full}, bit for bit");
        Console.WriteLine("------------------------------------------------");

        var serialPlane = new float[w * h];
        var parallelPlane = new float[w * h];

        ParallelWork.UseWorkers(1);
        FillEmission(serialPlane);
        DepositGalaxy(serialPlane);
        float[] serialKernel = BuildPsf(out int serialRadius);
        var serialConvolved = (float[])serialPlane.Clone();
        FourierConvolution.Convolve(serialConvolved, w, h, serialKernel, serialRadius);

        ParallelWork.UseWorkers(full);
        FillEmission(parallelPlane);
        DepositGalaxy(parallelPlane);
        float[] parallelKernel = BuildPsf(out int parallelRadius);
        var parallelConvolved = (float[])parallelPlane.Clone();
        FourierConvolution.Convolve(parallelConvolved, w, h, parallelKernel, parallelRadius);

        failures += Compare("emission fill + galaxy deposit", serialPlane, parallelPlane);
        failures += Compare("PSF kernel", serialKernel, parallelKernel);
        if (serialRadius != parallelRadius)
        {
            Console.WriteLine($"  [FAIL] PSF kernel radius {serialRadius} vs {parallelRadius}");
            failures++;
        }
        failures += Compare("PSF convolution", serialConvolved, parallelConvolved);

        Console.WriteLine("------------------------------------------------");
        Console.WriteLine(failures == 0
            ? "every parallel stage reproduces its serial result exactly"
            : $"{failures} stage(s) DIFFER");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The tiled FFT convolution against the DEFINITION of a convolution: the direct sum, in
    /// double precision, over the same frame and the same kernel.
    ///
    /// WHY IT IS KEPT. Overlap-add over transformed tiles is an exact restructuring of the direct
    /// sum, so the only thing separating them is floating-point rounding, and that makes the
    /// direct sum a reference this can be held to permanently rather than a one-off comparison
    /// against whatever the previous implementation happened to produce. It is what says whether a
    /// change to the transform -- the roots of unity now coming from a table, the column pass now
    /// taken in cache-line blocks, the empty tiles now skipped -- moved the answer.
    ///
    /// The frame is small on purpose: the direct sum is O(W*H*K^2) and that is the whole reason
    /// the transform exists.
    /// </summary>
    private static int CheckConvolutionAccuracy()
    {
        const int fw = 512, fh = 384, radius = 24;
        int k = 2 * radius + 1;

        // A kernel with a core and a faint wide skirt, which is the shape a real PSF has and the
        // case where a transform's error shows: the skirt is four orders down from the core.
        var kernel = new float[k * k];
        double norm = 0.0;
        for (int y = -radius; y <= radius; y++)
        for (int x = -radius; x <= radius; x++)
        {
            double r2 = x * x + y * y;
            double v = Math.Exp(-r2 / 18.0) + 0.01 * Math.Exp(-r2 / 800.0);
            kernel[(y + radius) * k + x + radius] = (float)v;
            norm += v;
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] = (float)(kernel[i] / norm);

        // A bright compact source beside a faint extended one: a star and a galaxy, which is the
        // pair the pipeline convolves and the one an inaccurate transform ruins.
        var source = new float[fw * fh];
        var rng = new Random(7);
        for (int i = 0; i < 30; i++) source[rng.Next(source.Length)] += 1e6f;
        for (int y = 120; y < 260; y++)
        for (int x = 150; x < 330; x++) source[y * fw + x] += 40f;

        var transformed = (float[])source.Clone();
        FourierConvolution.Convolve(transformed, fw, fh, kernel, radius);

        var direct = new double[fw * fh];
        for (int y = 0; y < fh; y++)
        for (int x = 0; x < fw; x++)
        {
            double acc = 0.0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int sy = y + dy;
                if (sy < 0 || sy >= fh) continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int sx = x + dx;
                    if (sx < 0 || sx >= fw) continue;
                    acc += (double)source[sy * fw + sx] * kernel[(dy + radius) * k + dx + radius];
                }
            }
            direct[y * fw + x] = acc;
        }

        double peak = 0.0, worst = 0.0, mean = 0.0;
        for (int i = 0; i < direct.Length; i++)
        {
            if (direct[i] > peak) peak = direct[i];
            double e = Math.Abs(transformed[i] - direct[i]);
            if (e > worst) worst = e;
            mean += e;
        }
        mean /= direct.Length;

        // The output is a float, so a residual of an ulp of the peak is the floor: nothing stored
        // in single precision can be closer to the double-precision sum than that.
        double floorAtPeak = peak * 1.1920929e-7;
        bool ok = worst <= 8.0 * floorAtPeak;

        Console.WriteLine($"tiled transform against the direct sum, {fw}x{fh} frame, {k}x{k} kernel");
        Console.WriteLine($"  peak of the convolution      {peak:E6}");
        Console.WriteLine($"  worst residual               {worst:E3}  ({worst / peak:E2} of peak)");
        Console.WriteLine($"  mean residual                {mean:E3}");
        Console.WriteLine($"  single-precision floor       {floorAtPeak:E3}  (one ulp of the peak)");
        Console.WriteLine(ok
            ? "  [ok  ] the transform agrees with the direct sum to the precision the output is stored in"
            : "  [FAIL] the transform departs from the direct sum by more than the storage can explain");
        return ok ? 0 : 1;
    }

    private static int Compare(string label, float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            Console.WriteLine($"  [FAIL] {label}: lengths differ");
            return 1;
        }
        long differing = 0;
        double worst = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Equals(b[i])) continue;
            differing++;
            double d = Math.Abs((double)a[i] - b[i]);
            if (d > worst) worst = d;
        }
        Console.WriteLine(differing == 0
            ? $"  [ok  ] {label}: {a.Length} values identical"
            : $"  [FAIL] {label}: {differing} of {a.Length} differ, worst {worst:E3}");
        return differing == 0 ? 0 : 1;
    }
}
