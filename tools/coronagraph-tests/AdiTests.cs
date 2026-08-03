using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// Angular differential imaging, run rather than parameterised.
///
/// Core.AngularDifferentialImaging carried an ANALYTIC self-subtraction throughput: a declared
/// functional form with the right limits, checked against one published data point. That was
/// honest and it was not a measurement. This harness builds the sequence the technique is actually
/// performed on - speckles fixed to the instrument, a companion fixed to the sky, the field turning
/// between them - reduces it the way an observer does, and MEASURES the throughput by injecting a
/// companion of known flux and recovering it.
///
/// That is how VIP calibrates a contrast curve, and it is the only way the number means anything:
/// a throughput is a property of the reduction, not of a formula.
///
/// The speckle field is the shipped Core one, with the static half drawn from a seed that does not
/// change between frames and the temporal half from one that does. If those two were not separated
/// there would be nothing for the reduction to remove and this whole file would measure zero.
/// </summary>
static class AdiTests
{
    static int failures;
    static string outDir = ".";

    static readonly VisualTelescopeSpec Sphere = VisualTelescopeCatalog.Sphere;
    const int Size = 385;                       // odd, so the star sits on a pixel centre
    const int Frames = 21;
    const double ExposureSeconds = 60.0;
    const double WindSpeed = 4.0;
    const double Paranal = -24.6272;

    static double PlateScaleMas => Sphere.NativePixelSizeMeters / Sphere.FocalLengthMeters
                                 * (180.0 / Math.PI) * 3600.0 * 1000.0;
    static double Wavelength => Coronagraph.IPrimWavelengthNm;
    static double Aperture => Coronagraph.StopB1_2.ApertureMeters;
    static double LambdaOverDMas => SpeckleField.LambdaOverDMas(Wavelength, Aperture);
    static double LambdaOverDPx => LambdaOverDMas / PlateScaleMas;

    public static int Run(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--out") outDir = args[i + 1];
        Directory.CreateDirectory(outDir);

        Console.WriteLine();
        Console.WriteLine("Angular differential imaging, run rather than parameterised");
        Console.WriteLine(new string('=', 78));

        SectionRotation();
        SectionTheSequence();
        SectionMeasuredThroughput();
        SectionContrastGain();

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(failures == 0 ? "ADI: ALL CHECKS PASSED" : $"ADI: {failures} CHECK(S) FAILED");
        return failures;
    }

    /// <summary>Derotation has to be an inverse before anything built on it means anything.</summary>
    static void SectionRotation()
    {
        Header("D1. Rotation");

        var frame = new float[Size * Size];
        var rng = new Pcg32(Pcg32.MixSeed(5), Pcg32.StreamShotNoise);
        // A smooth field, because bilinear interpolation of white noise is not invertible and
        // testing it on white noise would be testing the interpolator's low-pass, not the rotation.
        SpeckleField.BuildModulation(frame, Size, Size, PlateScaleMas, LambdaOverDMas,
                                     SpeckleField.StaticVarianceFraction, 1.0, 4242UL, 99UL);

        foreach (double angle in new[] { 0.0, 7.5, 30.0, 90.0 })
        {
            var there = AngularDifferentialImaging.Rotate(frame, Size, Size, angle);
            var back = AngularDifferentialImaging.Rotate(there, Size, Size, -angle);

            // Compared over the inscribed disc only: a square rotated by 30 degrees has corners
            // that genuinely left the frame, and asking for them back is asking for data that was
            // discarded rather than for the rotation to be exact.
            double sum2 = 0.0, ref2 = 0.0; int n = 0;
            double c = 0.5 * (Size - 1), r = 0.40 * Size;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    double dx = x - c, dy = y - c;
                    if (dx * dx + dy * dy > r * r) continue;
                    double d = back[y * Size + x] - frame[y * Size + x];
                    sum2 += d * d; ref2 += frame[y * Size + x] * frame[y * Size + x]; n++;
                }
            double relative = Math.Sqrt(sum2 / Math.Max(1e-30, ref2));
            Console.WriteLine($"   rotate {angle,5:F1} deg and back: relative residual {relative:F5} over {n} pixels");

            if (angle == 0.0) Check("zero rotation is the identity", relative, 0.0, 1e-9);
            else if (angle == 90.0) Check("a right angle is exact", relative, 0.0, 1e-6);
            else Check($"round trip at {angle} deg is interpolation only", relative < 0.15);
        }

        // A point source must land where the geometry says.
        var point = new float[Size * Size];
        int px = (int)(0.5 * (Size - 1)) + 80;
        int py = (int)(0.5 * (Size - 1));
        point[py * Size + px] = 1000f;
        var turned = AngularDifferentialImaging.Rotate(point, Size, Size, 90.0);
        double bx = 0, by = 0, w = 0;
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
        { double v = turned[y * Size + x]; if (v > 0) { bx += v * x; by += v * y; w += v; } }
        bx /= w; by /= w;
        Console.WriteLine($"   a source 80 px east, rotated 90 deg counter-clockwise, lands at ({bx - 0.5 * (Size - 1):F2}, {by - 0.5 * (Size - 1):F2})");
        Check("90 degrees moves +x to +y", bx - 0.5 * (Size - 1), 0.0, 0.01);
        Check("and by 80 pixels", by - 0.5 * (Size - 1), 80.0, 0.01);
    }

    /// <summary>
    /// The sequence itself: speckles that do not move in the detector, a companion that does.
    /// Without that separation there is nothing to reduce.
    /// </summary>
    static void SectionTheSequence()
    {
        Header("D2. The sequence");

        double[] angles = ParallacticAngles(out double totalRotation);
        Console.WriteLine($"   {Frames} frames of {ExposureSeconds:F0} s from Paranal, hour angle -1 h to +1 h at dec -40");
        Console.WriteLine($"   parallactic angle runs {angles[0]:F2} to {angles[Frames - 1]:F2} deg, {totalRotation:F2} deg of field rotation");
        Check("the sequence turns", Math.Abs(totalRotation) > 30.0);

        var frames = BuildSequence(angles, companionSeparationMas: 0.0, companionContrast: 0.0,
                                   out _, out _);

        // The speckles must be common between frames and must NOT rotate: that is what makes them
        // removable. Measured as the correlation of frame 0 with frame N-1.
        double rho = Correlation(frames[0], frames[Frames - 1]);
        Console.WriteLine($"   first and last frame correlate at {rho:F4} (the static pattern does not turn)");
        Check("the speckle pattern is common to the sequence", rho > 0.9);

        // And the reduction of a companion-free sequence must leave essentially nothing.
        var residual = AngularDifferentialImaging.Reduce(frames, angles, Size, Size);
        double before = AnnulusRms(frames[0], 200.0);
        double after = AnnulusRms(residual, 200.0);
        Console.WriteLine($"   at 200 mas, one frame scatters {before:E3}, the reduced stack {after:E3}, " +
                          $"a factor {before / after:F1}");
        Check("the reduction removes most of the speckle field", after < 0.35 * before);
    }

    /// <summary>
    /// THE MEASUREMENT. Inject a companion of known flux, reduce, recover it, and see what fraction
    /// survived. This is what replaces a declared functional form with a number.
    ///
    /// THE SEPARATION IS FIXED AND THE ROTATION IS SWEPT, which is the experiment the analytic form
    /// is a claim about: it says throughput depends on the arc length in resolution elements, and
    /// arc length is rotation times separation. Sweeping separation instead would move the
    /// companion through parts of the frame with different speckle brightness and confound the two.
    ///
    /// It is also bounded by geometry rather than by choice. This frame is 385 px at 1.80 mas, so
    /// its half-width is 346 mas; a companion injected beyond that is outside the detector, which
    /// is how the first version of this section came to report a throughput of exactly zero at 500
    /// and 700 mas and think it had measured something.
    /// </summary>
    static void SectionMeasuredThroughput()
    {
        Header("D3. Throughput, measured by injection and recovery");

        const double SeparationMas = 150.0;      // 6.8 lambda/D, comfortably inside the frame
        double perElement = AngularDifferentialImaging.RotationForOneResolutionElementDeg(
            SeparationMas, Wavelength, Aperture);

        Console.WriteLine($"   companion at {SeparationMas:F0} mas = {SeparationMas / LambdaOverDMas:F1} lambda/D, " +
                          $"one resolution element of travel costs {perElement:F2} deg");
        Console.WriteLine();
        Console.WriteLine("   rotation   arc [l/D]   measured   analytic n/(n+1)");

        var rows = new List<string> { "rotation_deg,arc_elements,measured_throughput,analytic_throughput" };
        var measured = new List<(double Rotation, double Value, double Analytic)>();

        foreach (double rotation in new[] { 1.0, 3.0, 6.0, 12.0, 30.0, 90.0 })
        {
            double[] angles = LinearAngles(Frames, rotation);
            double t = MeasureThroughput(angles, SeparationMas, Frames);
            double analytic = AngularDifferentialImaging.SelfSubtractionThroughput(
                SeparationMas, rotation, Wavelength, Aperture);
            double arc = rotation / perElement;

            Console.WriteLine($"   {rotation,7:F0} deg   {arc,9:F2}   {t,8:F3}   {analytic,17:F3}");
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0:G6},{1:G6},{2:G6},{3:G6}",
                                   rotation, arc, t, analytic));
            measured.Add((rotation, t, analytic));

            Check($"throughput at {rotation} deg is a fraction", t > -0.05 && t <= 1.05);
        }
        File.WriteAllLines(Path.Combine(outDir, "adi_throughput.csv"), rows);

        // The two limits the analytic form was built to have, now measured rather than asserted.
        Check("throughput rises with rotation", measured[measured.Count - 1].Value > measured[0].Value);
        Check("almost nothing survives with almost no rotation", measured[0].Value < 0.35);
        Check("almost everything survives a long arc", measured[measured.Count - 1].Value > 0.85);

        // AND THE VARIABLE THE ANALYTIC FORM DOES NOT HAVE, whose direction is reported rather
        // than predicted. n/(n+1) is a function of the arc alone, so at fixed rotation it says
        // nothing about how the sequence was sampled; the reduction does depend on it, because a
        // median of three values and a median of thirty-one are different estimators of the same
        // thing. The measurement below is left to say which way, having contradicted the obvious
        // guess once already.
        Console.WriteLine();
        Console.WriteLine("   at a fixed 12 deg of rotation, against the number of frames:");
        Console.WriteLine("   frames   measured   analytic (frame-count blind)");
        double blind = AngularDifferentialImaging.SelfSubtractionThroughput(
            SeparationMas, 12.0, Wavelength, Aperture);
        var byFrames = new List<double>();
        var rows2 = new List<string> { "frames,measured_throughput,analytic_throughput" };
        foreach (int frames in new[] { 3, 7, 15, 31 })
        {
            double t = MeasureThroughput(LinearAngles(frames, 12.0), SeparationMas, frames);
            Console.WriteLine($"   {frames,6}   {t,8:F3}   {blind,27:F3}");
            rows2.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1:G6},{2:G6}", frames, t, blind));
            byFrames.Add(t);
        }
        File.WriteAllLines(Path.Combine(outDir, "adi_frames.csv"), rows2);
        Check("frame count changes the throughput at fixed rotation",
              Math.Abs(byFrames[byFrames.Count - 1] - byFrames[0]) > 0.02);
        Check("but never by more than the rotation does",
              Math.Abs(byFrames[byFrames.Count - 1] - byFrames[0]) < Math.Abs(measured[measured.Count - 1].Value - measured[0].Value));
    }

    /// <summary>
    /// One injection-and-recovery: build the sequence with and without the companion, reduce both,
    /// and take the difference at the companion's own position.
    ///
    /// The companion-free reduction is subtracted rather than ignored because the speckle residual
    /// at that position is not zero and does not average away; VIP does the same for the same
    /// reason. What is left is the companion's own contribution and nothing else.
    /// </summary>
    static double MeasureThroughput(double[] angles, double separationMas, int frames)
    {
        // The companion-free reduction first, both to measure the noise the injection is scaled
        // against and to subtract from the recovery afterwards.
        var without = BuildSequence(angles, separationMas, 0.0, out _, out _, frames);
        var residualWithout = AngularDifferentialImaging.Reduce(without, angles, Size, Size);

        double sepPx = separationMas / PlateScaleMas;
        double radius = 0.75 * LambdaOverDPx;
        double noise = ApertureScatterInAnnulus(residualWithout, sepPx, radius);

        // INJECTED AT A FIXED SIGNAL-TO-NOISE, which is VIP's own convention (its fc_snr, default
        // 100) and not a detail. A median is not a linear operator, so how much of a companion it
        // absorbs depends on how far the companion stands above what it is being medianed with: a
        // source a thousand times the local speckle dominates every frame it appears in and is
        // subtracted almost entirely, while one at the noise floor barely perturbs the reference at
        // all. A throughput quoted without saying at what brightness is therefore not a number.
        //
        // The first version of this section injected a fixed contrast of 3e-3 against a halo of
        // 2.2e-6 at this separation: a companion a thousand times brighter than the sky it sat on,
        // which is not a companion.
        const double InjectionSnr = 100.0;
        double targetFlux = InjectionSnr * noise;

        // The contrast that produces that aperture flux, from the Gaussian's own closed form.
        double sigmaPx = LambdaOverDPx / 2.355;
        double fluxPerUnitPeak = ApertureSumOfGaussian(1.0, sigmaPx, radius);
        double contrast = targetFlux / fluxPerUnitPeak;

        var with = BuildSequence(angles, separationMas, contrast, out double injected, out double[] pa, frames);
        var residualWith = AngularDifferentialImaging.Reduce(with, angles, Size, Size);

        double theta = pa[0] * Math.PI / 180.0;
        double cx = 0.5 * (Size - 1) + sepPx * Math.Cos(theta);
        double cy = 0.5 * (Size - 1) + sepPx * Math.Sin(theta);

        double recovered = ApertureSum(residualWith, cx, cy, radius)
                         - ApertureSum(residualWithout, cx, cy, radius);
        return injected > 0.0 ? recovered / injected : 0.0;
    }

    /// <summary>Scatter of non-overlapping apertures laid around an annulus, which is the noise a companion has to stand above.</summary>
    static double ApertureScatterInAnnulus(float[] frame, double sepPx, double radiusPx)
    {
        int count = Math.Max(4, (int)Math.Floor(2.0 * Math.PI * sepPx / (2.0 * radiusPx)));
        double c = 0.5 * (Size - 1);
        var flux = new double[count];
        for (int k = 0; k < count; k++)
        {
            double t = 2.0 * Math.PI * k / count;
            flux[k] = ApertureSum(frame, c + sepPx * Math.Cos(t), c + sepPx * Math.Sin(t), radiusPx);
        }
        double mean = 0.0; foreach (double v in flux) mean += v; mean /= count;
        double var = 0.0; foreach (double v in flux) { double d = v - mean; var += d * d; }
        return Math.Sqrt(var / Math.Max(1, count - 1));
    }

    /// <summary>What the reduction buys in detection limit, which is the number an observer cares about.</summary>
    static void SectionContrastGain()
    {
        Header("D4. What it buys");

        double[] angles = ParallacticAngles(out double totalRotation);
        var frames = BuildSequence(angles, 0.0, 0.0, out _, out _);

        // Three products: one frame, the plain average of the sequence, and the ADI reduction.
        var plain = new float[Size * Size];
        for (int k = 0; k < Frames; k++)
            for (int i = 0; i < plain.Length; i++) plain[i] += frames[k][i] / Frames;
        var reduced = AngularDifferentialImaging.Reduce(frames, angles, Size, Size);

        double starPeak = 1.0;
        var mask = Coronagraph.Find("CLC-S-WF").Value;

        Console.WriteLine("   sep [mas]   one frame      stacked        ADI       gain over stacking");
        var rows = new List<string> { "separation_mas,single,stacked,adi,gain_mag" };
        // Inside the frame's own half-width of 346 mas, for the reason D3 records.
        foreach (double sep in new[] { 100.0, 150.0, 220.0, 300.0 })
        {
            double a = ContrastAt(frames[0], sep, starPeak);
            double b = ContrastAt(plain, sep, starPeak);
            double c = ContrastAt(reduced, sep, starPeak);
            double gainMag = 2.5 * Math.Log10(b / c);
            Console.WriteLine($"   {sep,9:F0}   {a,9:E2}   {b,10:E2}   {c,9:E2}   {gainMag,17:F2} mag");
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0:G6},{1:G6},{2:G6},{3:G6},{4:G6}",
                                   sep, a, b, c, gainMag));

            // STACKING BARELY HELPS AND ADI DOES. That is the whole argument of section 7.52 made
            // as a measurement: the speckle field is 71% static, so averaging frames removes the
            // 29% that is not and leaves the rest exactly where it was.
            Check($"stacking helps little at {sep} mas", b > 0.5 * a);
            Check($"ADI helps a lot at {sep} mas", c < 0.5 * b);
        }
        File.WriteAllLines(Path.Combine(outDir, "adi_contrast.csv"), rows);
    }

    // ---------------------------------------------------------------- the sequence

    /// <summary>Parallactic angles over a two-hour sequence from Paranal at a declination that turns well.</summary>
    static double[] ParallacticAngles(out double totalRotation)
    {
        var angles = new double[Frames];
        for (int k = 0; k < Frames; k++)
        {
            double hourAngle = -1.0 + 2.0 * k / (Frames - 1.0);
            angles[k] = AngularDifferentialImaging.ParallacticAngleDeg(hourAngle, -40.0, Paranal);
        }
        totalRotation = AngularDifferentialImaging.FieldRotationDeg(-1.0, 1.0, -40.0, Paranal);
        return angles;
    }

    /// <summary>
    /// A sequence that turns by exactly the requested amount, in equal steps.
    ///
    /// Used where the EXPERIMENT is the rotation, rather than where the sequence is. Deriving the
    /// angles from an hour angle instead would confound the variable under test with the
    /// trigonometry of a particular declination, which section 4 of the main harness already
    /// checks on its own.
    /// </summary>
    static double[] LinearAngles(int frames, double totalRotationDeg)
    {
        var angles = new double[frames];
        for (int k = 0; k < frames; k++) angles[k] = totalRotationDeg * k / (frames - 1.0);
        return angles;
    }

    /// <summary>
    /// One coronagraphic sequence: a halo modulated by a speckle field whose static half never
    /// changes, plus an optional companion at a FIXED SKY position, which therefore moves in the
    /// detector as the field turns.
    ///
    /// The companion's flux is returned so that recovery can be compared against it, and its
    /// detector position angle per frame so the reduction's output can be searched where it should
    /// be rather than wherever the brightest thing is.
    /// </summary>
    static List<float[]> BuildSequence(double[] angles, double companionSeparationMas, double companionContrast,
                                       out double injectedFlux, out double[] detectorPositionAngles,
                                       int frameCount = Frames)
    {
        var frames = new List<float[]>(frameCount);
        double controlRadius = SpeckleField.ControlRadiusMas(41, Wavelength, Aperture);
        var mask = Coronagraph.Find("CLC-S-WF").Value;

        double surviving = SpeckleField.SurvivingVarianceFraction(ExposureSeconds, Aperture, WindSpeed);
        double f = SpeckleField.StaticVarianceFraction;
        double realisations = Math.Max(1.0, (1.0 - f) / Math.Max(1e-9, surviving - f));

        double cx = 0.5 * (Size - 1), cy = 0.5 * (Size - 1);
        double sepPx = companionSeparationMas / PlateScaleMas;
        double sigmaPx = LambdaOverDPx / 2.355;

        // The companion's own peak, from its contrast against the unocculted star.
        injectedFlux = 0.0;
        detectorPositionAngles = new double[frameCount];

        var modulation = new float[Size * Size];
        for (int k = 0; k < frameCount; k++)
        {
            // The STATIC seed is the same for every frame; only the temporal one changes. That one
            // line is what the whole technique rests on.
            SpeckleField.BuildModulation(modulation, Size, Size, PlateScaleMas, LambdaOverDMas,
                                         f, realisations, staticSeed: 20260803UL, temporalSeed: (ulong)(500 + k));

            var frame = new float[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                double dy = y - cy;
                for (int x = 0; x < Size; x++)
                {
                    double dx = x - cx;
                    double r = Math.Sqrt(dx * dx + dy * dy) * PlateScaleMas;
                    double halo = HaloIntensity(r, LambdaOverDMas, controlRadius);
                    frame[y * Size + x] = (float)(halo * modulation[y * Size + x]
                                                * Coronagraph.MaskTransmission(mask, r));
                }
            }

            // A companion fixed on the sky sits at a detector position angle that follows the
            // parallactic angle. Its sky position angle is arbitrary; zero at the first frame.
            double detectorPa = angles[k] - angles[0];
            detectorPositionAngles[k] = detectorPa;

            if (companionContrast > 0.0 && sepPx > 0.0)
            {
                double t = detectorPa * Math.PI / 180.0;
                double px = cx + sepPx * Math.Cos(t);
                double py = cy + sepPx * Math.Sin(t);
                double peak = companionContrast;
                double total = 0.0;

                int r0 = (int)(py - 5 * sigmaPx), r1 = (int)(py + 5 * sigmaPx);
                int c0 = (int)(px - 5 * sigmaPx), c1 = (int)(px + 5 * sigmaPx);
                for (int y = Math.Max(0, r0); y <= Math.Min(Size - 1, r1); y++)
                    for (int x = Math.Max(0, c0); x <= Math.Min(Size - 1, c1); x++)
                    {
                        double d2 = (x - px) * (x - px) + (y - py) * (y - py);
                        double v = peak * Math.Exp(-d2 / (2 * sigmaPx * sigmaPx));
                        frame[y * Size + x] += (float)v;
                        total += v;
                    }
                if (k == 0) injectedFlux = ApertureSumOfGaussian(peak, sigmaPx, 0.75 * LambdaOverDPx);
            }
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>Analytic flux of a Gaussian inside an aperture, which is what recovery is measured against.</summary>
    static double ApertureSumOfGaussian(double peak, double sigmaPx, double radiusPx)
        => peak * 2.0 * Math.PI * sigmaPx * sigmaPx * (1.0 - Math.Exp(-radiusPx * radiusPx / (2 * sigmaPx * sigmaPx)));

    static double HaloIntensity(double separationMas, double lambdaOverDMas, double controlRadiusMas)
    {
        double r = Math.Max(separationMas, 0.5 * lambdaOverDMas);
        double x = r / lambdaOverDMas;
        double inner = 1e-4 * Math.Pow(x, -2.0);
        if (r <= controlRadiusMas) return inner;
        double atBreak = 1e-4 * Math.Pow(controlRadiusMas / lambdaOverDMas, -2.0);
        return atBreak * Math.Pow(r / controlRadiusMas, -3.0);
    }

    // ---------------------------------------------------------------- measurement helpers

    static double ApertureSum(float[] frame, double cx, double cy, double radiusPx)
    {
        double sum = 0.0, r2 = radiusPx * radiusPx;
        int y0 = (int)Math.Floor(cy - radiusPx), y1 = (int)Math.Ceiling(cy + radiusPx);
        int x0 = (int)Math.Floor(cx - radiusPx), x1 = (int)Math.Ceiling(cx + radiusPx);
        for (int y = Math.Max(0, y0); y <= Math.Min(Size - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(Size - 1, x1); x++)
            {
                double d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d2 <= r2) sum += frame[y * Size + x];
            }
        return sum;
    }

    static double AnnulusRms(float[] frame, double separationMas)
    {
        double sepPx = separationMas / PlateScaleMas;
        double c = 0.5 * (Size - 1);
        double sum = 0.0, sum2 = 0.0; int n = 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double r = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                if (Math.Abs(r - sepPx) > 0.5 * LambdaOverDPx) continue;
                sum += frame[y * Size + x]; sum2 += (double)frame[y * Size + x] * frame[y * Size + x]; n++;
            }
        if (n < 2) return 0.0;
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0.0, sum2 / n - mean * mean));
    }

    static double ContrastAt(float[] frame, double separationMas, double starPeak)
    {
        var curve = ContrastCurve.Measure(frame, Size, Size, PlateScaleMas, LambdaOverDMas, starPeak,
                                          separationMas - 0.5 * LambdaOverDMas, separationMas + 0.5 * LambdaOverDMas,
                                          ContrastCurve.FiveSigmaTailProbability, null);
        return curve.Count > 0 ? curve[0].Contrast : double.NaN;
    }

    static double Correlation(float[] a, float[] b)
    {
        double ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++)
        { double da = a[i] - ma, db = b[i] - mb; sab += da * db; saa += da * da; sbb += db * db; }
        return sab / Math.Sqrt(saa * sbb);
    }

    static void Header(string t) { Console.WriteLine(); Console.WriteLine(t); Console.WriteLine(new string('-', t.Length)); }
    static void Check(string what, double got, double expected, double tol)
    {
        if (!(Math.Abs(got - expected) <= tol)) { failures++; Console.WriteLine($"     FAIL {what}: got {got:G8}, expected {expected:G8} +/- {tol:G4}"); }
    }
    static void Check(string what, bool ok) { if (!ok) { failures++; Console.WriteLine($"     FAIL {what}"); } }
}
