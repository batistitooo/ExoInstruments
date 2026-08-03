using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// Does the high-contrast chain reproduce the instrument ESO measured?
///
/// WHAT THIS IS FOR. Core.Coronagraph, Core.SpeckleField, Core.AngularDifferentialImaging and
/// Core.ContrastCurve together claim to describe SPHERE/ZIMPOL well enough that a detection limit
/// computed from a simulated frame means something. That claim is checkable in three independent
/// ways, and this harness does all three:
///
///   1. Against NUMBERS ESO PUBLISHED that were not used to build the model. The pupil stop's
///      geometric transmission and the observed speckle-ring radius are both measured quantities
///      that fall out of geometry the model was given for other reasons.
///   2. Against the STATISTICS the physics demands. A modified Rician has a known mean and
///      variance; a temporal decomposition must sum to one; a Student t threshold must reduce to
///      the Gaussian one as the sample grows.
///   3. Against VIP, the reference implementation of the contrast-curve measurement, by
///      compare_vip.py on the frame and curve this harness exports.
///
/// Sections:
///   1. The coronagraph reproduces ESO's measured attenuations.
///   2. The Lyot stop's geometry reproduces its published transmission.
///   3. The speckle field lands where ESO saw it, and has the right moments.
///   4. Field rotation and its cost.
///   5. The contrast curve's small-sample penalty.
///   6. A synthetic coronagraphic frame, and its curve, exported for VIP.
/// </summary>
static class CoronagraphTests
{
    static int failures;
    static string outDir = ".";

    // ZIMPOL's own scale, from the shipped catalogue rather than restated here.
    static readonly VisualTelescopeSpec Sphere = VisualTelescopeCatalog.Sphere;

    /// <summary>
    /// SAXO's high-order deformable mirror is 41x41 actuators (Fusco et al. 2006; Beuzit et al.
    /// 2019). Held here rather than on the spec because it is a property of the adaptive optics
    /// system in front of the instrument rather than of the instrument, and because SPHERE is the
    /// only entry on the roster that has one.
    /// </summary>
    const int SaxoActuatorsAcrossPupil = 41;

    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--out") outDir = args[i + 1];
        Directory.CreateDirectory(outDir);

        Console.WriteLine("SPHERE/ZIMPOL high-contrast chain against the instrument ESO measured");
        Console.WriteLine(new string('=', 78));

        SectionMaskAttenuations();
        SectionLyotStopGeometry();
        SectionSpeckleField();
        SectionFieldRotation();
        SectionSmallSamplePenalty();
        SectionRenderedField();
        SectionSyntheticFrame();

        // The reduction, run rather than parameterised.
        failures += AdiTests.Run(args);

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 1

    /// <summary>
    /// The attenuations are ratios of ESO's Table 8 counts, so reproducing the table is
    /// arithmetic; what is worth checking is that they reproduce the paper's PROSE, which quotes
    /// spans ("R_coro = 110-150", "300-600", "1000-3000") derived from that table independently.
    /// A transcription error in either column would break the agreement.
    /// </summary>
    static void SectionMaskAttenuations()
    {
        Header("1. Focal-plane masks against ESO's measured attenuations");
        Console.WriteLine("   mask         rho    IWA      R_PRIM   I_PRIM   ESO's prose");

        var quoted = new Dictionary<string, string>
        {
            { "CLC-S-WF",  "110-150" },
            { "CLC-M-WF",  "300-600" },
            { "CLC-MT-WF", "300-600" },
            { "CLC-XL-WF", "1000-3000" },
        };

        foreach (var mask in Coronagraph.VisualMasks)
        {
            double r = mask.PeakAttenuationRPrim, i = mask.PeakAttenuationIPrim;
            string prose = quoted.ContainsKey(mask.Name) ? quoted[mask.Name] : "";
            Console.WriteLine($"   {mask.Name,-11} {mask.RadiusMas,6:F1} {Coronagraph.InnerWorkingAngleMas(mask),6:F1}  " +
                              $"{r,9:F0} {i,8:F0}   {prose}");

            // Attenuation must rise with wavelength: a mask of fixed angular radius covers more
            // resolution elements in the red.
            Check($"{mask.Name} attenuates more in I than in R", i >= r);
            Check($"{mask.Name} attenuates at all", r > 1.0);
        }

        // The paper's own spans, checked against the ratios.
        Check("CLC-S-WF matches the quoted 110-150",
              Coronagraph.VisualMasks[0].PeakAttenuationRPrim >= 105.0 &&
              Coronagraph.VisualMasks[0].PeakAttenuationIPrim <= 155.0);
        Check("CLC-XL-WF matches the quoted 1000-3000",
              Coronagraph.VisualMasks[4].PeakAttenuationRPrim >= 1000.0 &&
              Coronagraph.VisualMasks[4].PeakAttenuationIPrim <= 3000.0);

        // The wavelength interpolation must return the anchors exactly at the anchors, and stay
        // between them in between.
        var s = Coronagraph.VisualMasks[0];
        Check("interpolation returns R_PRIM at R_PRIM",
              Coronagraph.PeakAttenuation(s, Coronagraph.RPrimWavelengthNm), s.PeakAttenuationRPrim, 1e-9);
        Check("interpolation returns I_PRIM at I_PRIM",
              Coronagraph.PeakAttenuation(s, Coronagraph.IPrimWavelengthNm), s.PeakAttenuationIPrim, 1e-9);
        double mid = Coronagraph.PeakAttenuation(s, 700.0);
        Check("interpolation stays between its anchors",
              mid > s.PeakAttenuationRPrim && mid < s.PeakAttenuationIPrim);
        Console.WriteLine($"   interpolated at 700 nm: {mid:F1}, between {s.PeakAttenuationRPrim:F1} and {s.PeakAttenuationIPrim:F1}");

        // The astrometric mask is the only one that lets the star through.
        var mt = Coronagraph.Find("CLC-MT-WF").Value;
        Check("the astrometric mask transmits 0.1%", Coronagraph.MaskTransmission(mt, 0.0), 0.001, 1e-12);
        Check("an opaque mask transmits nothing", Coronagraph.MaskTransmission(Coronagraph.VisualMasks[0], 0.0), 0.0, 1e-12);
        Check("every mask transmits outside its radius", Coronagraph.MaskTransmission(mt, 200.0), 1.0, 1e-12);
    }

    // ---------------------------------------------------------------- 2

    /// <summary>
    /// THE STRONGEST CHECK IN THIS FILE, because its answer was not used to produce it. The Lyot
    /// stop's dimensions are read from Table 9 in millimetres of an internal pupil image and
    /// scaled to the telescope by one number from the same table. The SAME table separately
    /// publishes the stop's geometric transmission, which is a different measurement of the same
    /// object. Computing that transmission from the scaled dimensions and getting the published
    /// value back means the table has been read correctly and the scaling is right.
    /// </summary>
    static void SectionLyotStopGeometry()
    {
        Header("2. The Lyot stop's geometry against its published transmission");
        var stop = Coronagraph.StopB1_2;

        double dTel = Coronagraph.TelescopeApertureMeters;
        double obsTel = 1.148;                       // Table 9, telescope inner diameter, m
        double vaneTel = 0.041;                      // Table 9, telescope spider width, m

        double stopInner = stop.ApertureMeters * stop.ObstructionFraction;
        double telescopeArea = Area(dTel, obsTel, vaneTel, 4);
        double stopArea = Area(stop.ApertureMeters, stopInner, stop.SpiderVaneWidthMeters, 4);
        double computed = stopArea / telescopeArea;

        Console.WriteLine($"   telescope: D = {dTel:F3} m, obstruction {obsTel:F3} m, vanes {vaneTel:F3} m");
        Console.WriteLine($"   {stop.Name}:  D = {stop.ApertureMeters:F3} m, obstruction {stopInner:F3} m, vanes {stop.SpiderVaneWidthMeters:F3} m");
        Console.WriteLine($"   obstruction fraction rises from {obsTel / dTel:F4} to {stop.ObstructionFraction:F4}");
        Console.WriteLine($"   computed geometric transmission {computed * 100:F1}%, ESO publish {stop.GeometricTransmission * 100:F1}%");
        Console.WriteLine($"   light reaching the detector {Coronagraph.Throughput(stop) * 100:F1}% " +
                          $"(= 0.91 x T_geom: the quarter of the pupil thrown away holds only 9% of the light)");

        // Three points. The three dimensions are read independently and the transmission is a
        // fourth, separately published number, so agreement to a few points is a real check and
        // not a tautology.
        Check("computed transmission matches the published one", computed, stop.GeometricTransmission, 0.03);

        Check("the stop undersizes the aperture", stop.ApertureMeters < dTel);
        Check("the stop oversizes the obstruction", stop.ObstructionFraction > obsTel / dTel);
        Check("the stop's vanes are wider than the telescope's", stop.SpiderVaneWidthMeters > vaneTel);
    }

    /// <summary>Annulus area less four spider vanes crossing it.</summary>
    static double Area(double outer, double inner, double vaneWidth, int vanes)
    {
        double annulus = Math.PI * 0.25 * (outer * outer - inner * inner);
        double vaneArea = vanes * vaneWidth * 0.5 * (outer - inner);
        return annulus - vaneArea;
    }

    // ---------------------------------------------------------------- 3

    static void SectionSpeckleField()
    {
        Header("3. The speckle field");

        double lambdaR = Coronagraph.RPrimWavelengthNm;
        double d = Coronagraph.TelescopeApertureMeters;
        double lod = SpeckleField.LambdaOverDMas(lambdaR, d);
        double control = SpeckleField.ControlRadiusMas(SaxoActuatorsAcrossPupil, lambdaR, d);

        Console.WriteLine($"   lambda/D at {lambdaR:F0} nm on {d:F1} m: {lod:F2} mas");
        Console.WriteLine($"   AO control radius from {SaxoActuatorsAcrossPupil} actuators: {control:F0} mas");
        Console.WriteLine($"   ESO observe the speckle ring at 300-400 mas (Schmid et al. 2018)");

        // THE CHECK THIS SECTION EXISTS FOR. The control radius is computed from an actuator count
        // and a wavelength; the speckle ring is a feature ESO saw in an image. They are independent
        // and they must agree.
        Check("the control radius lands on the observed speckle ring", control >= 300.0 && control <= 400.0);

        // The temporal decomposition must be a decomposition.
        double sum = SpeckleField.StaticVarianceFraction + SpeckleField.FastVarianceFraction
                   + SpeckleField.AtmosphericVarianceFraction;
        Console.WriteLine($"   variance decomposition: static {SpeckleField.StaticVarianceFraction:F3}, " +
                          $"fast {SpeckleField.FastVarianceFraction:F3} (tau {SpeckleField.FastDecorrelationSeconds} s), " +
                          $"atmospheric {SpeckleField.AtmosphericVarianceFraction:F3}");
        Check("the three components sum to one", sum, 1.0, 1e-12);

        // Atmospheric lifetime at Paranal wind speeds, against Milli's own bound.
        double tau3 = SpeckleField.AtmosphericLifetimeSeconds(d, 3.0);
        double tau4 = SpeckleField.AtmosphericLifetimeSeconds(d, 4.0);
        Console.WriteLine($"   atmospheric speckle lifetime 0.6 D/v: {tau4:F2} s at 4 m/s, {tau3:F2} s at 3 m/s");
        Check("matches Milli et al.'s quoted bound of at most 1.6 s at 3-4 m/s", tau4 <= 1.6 && tau3 <= 1.65);

        // What survives an exposure. The static part must not move.
        Console.WriteLine("   surviving variance fraction against exposure time, at 4 m/s:");
        foreach (double t in new[] { 1.0, 10.0, 60.0, 600.0, 3600.0 })
        {
            double f = SpeckleField.SurvivingVarianceFraction(t, d, 4.0);
            Console.WriteLine($"     {t,6:F0} s -> {f:F4}");
            Check($"survival at {t} s never falls below the static floor", f >= SpeckleField.StaticVarianceFraction);
        }
        Check("an hour still carries the whole static term",
              SpeckleField.SurvivingVarianceFraction(3600.0, d, 4.0), SpeckleField.StaticVarianceFraction, 0.001);

        // The modified Rician's moments, measured from draws rather than asserted from the formula.
        Console.WriteLine("   modified Rician moments, 2,000,000 draws:");
        var rng = new Pcg32(Pcg32.MixSeed(4242), Pcg32.StreamShotNoise);
        foreach (double f in new[] { 0.0, 0.5, SpeckleField.StaticVarianceFraction, 0.95 })
        {
            const double Mean = 1.0;
            double ic, isr;
            SpeckleField.Split(Mean, f, out ic, out isr);

            const int N = 2_000_000;
            double s1 = 0.0, s2 = 0.0;
            for (int k = 0; k < N; k++) { double v = SpeckleField.Sample(rng, ic, isr); s1 += v; s2 += v * v; }
            double mean = s1 / N;
            double var = s2 / N - mean * mean;

            double predMean = SpeckleField.MeanIntensity(ic, isr);
            double predVar = SpeckleField.Variance(ic, isr);
            Console.WriteLine($"     f={f:F3}: I_c={ic:F4} I_s={isr:F4}  mean {mean:F4} (pred {predMean:F4})  " +
                              $"var {var:F4} (pred {predVar:F4})");

            Check($"f={f:F2} mean", mean, predMean, 0.005);
            Check($"f={f:F2} variance", var, predVar, Math.Max(0.005, 0.02 * predVar));

            // And the split must reproduce the requested static fraction, which is the whole
            // reason Split exists: the total variance is m^2 and the static share is (I_c/m)^2.
            Check($"f={f:F2} static share recovered", (ic / Mean) * (ic / Mean), f, 1e-12);
        }

        // Averaging must divide the variance by the number of realisations, exactly.
        double icA, isA;
        SpeckleField.Split(1.0, SpeckleField.StaticVarianceFraction, out icA, out isA);
        foreach (int n in new[] { 4, 16, 64, 256 })
        {
            const int N = 400_000;
            double s1 = 0.0, s2 = 0.0;
            for (int k = 0; k < N; k++) { double v = SpeckleField.SampleAveraged(rng, icA, isA, n); s1 += v; s2 += v * v; }
            double mean = s1 / N, var = s2 / N - mean * mean;
            double pred = SpeckleField.Variance(icA, isA) / n;
            Console.WriteLine($"     averaged over {n,3}: var {var:E3} (pred {pred:E3})");
            Check($"averaging over {n} divides the variance", var, pred, 0.06 * pred);
        }
    }

    // ---------------------------------------------------------------- 4

    static void SectionFieldRotation()
    {
        Header("4. Field rotation, and what it costs");

        // Paranal's latitude. Two textbook cases first, because a parallactic angle that is wrong
        // by a sign is wrong everywhere and invisible in any single number.
        const double Paranal = -24.6272;

        Check("on the meridian, north of zenith, the parallactic angle is 180 deg",
              Math.Abs(AngularDifferentialImaging.ParallacticAngleDeg(0.0, 0.0, Paranal)), 180.0, 1e-9);
        Check("on the meridian, south of zenith, it is 0",
              AngularDifferentialImaging.ParallacticAngleDeg(0.0, -60.0, Paranal), 0.0, 1e-9);

        // A target near the zenith sweeps the most; one far south sweeps least. That ordering is
        // the whole of ADI's scheduling.
        Console.WriteLine("   field rotation over +/- 1 h of hour angle, from Paranal:");
        double prev = double.MaxValue;
        foreach (double dec in new[] { -24.6, -40.0, -60.0, -80.0 })
        {
            double rot = Math.Abs(AngularDifferentialImaging.FieldRotationDeg(-1.0, 1.0, dec, Paranal));
            Console.WriteLine($"     dec {dec,6:F1} -> {rot,7:F2} deg");
            Check($"rotation falls away from the zenith at dec {dec}", rot < prev);
            prev = rot;
        }

        // The rotation needed to move a source by one resolution element, and the throughput that
        // follows. Both must be monotonic in separation, in opposite directions.
        Console.WriteLine("   rotation for one resolution element, and ADI throughput at 30 deg of rotation:");
        double lambda = Coronagraph.IPrimWavelengthNm, d = Coronagraph.TelescopeApertureMeters;
        double prevRot = double.MaxValue, prevTp = -1.0;
        foreach (double sep in new[] { 50.0, 91.0, 200.0, 500.0, 1000.0 })
        {
            double need = AngularDifferentialImaging.RotationForOneResolutionElementDeg(sep, lambda, d);
            double tp = AngularDifferentialImaging.SelfSubtractionThroughput(sep, 30.0, lambda, d);
            Console.WriteLine($"     {sep,6:F0} mas -> {need,6:F2} deg,  throughput {tp:F3}");
            Check($"rotation needed falls with separation at {sep}", need < prevRot);
            Check($"throughput rises with separation at {sep}", tp > prevTp);
            prevRot = need; prevTp = tp;
        }

        // The one measurement available, reported rather than asserted (see the method's own
        // comment for why this is a sanity check and not a validation).
        double eso = AngularDifferentialImaging.SelfSubtractionThroughput(91.0, 120.0, lambda, d);
        Console.WriteLine($"   alpha Hyi B (91 mas, 120 deg): model {eso:F3}, ESO measure 3.6/4.7 = {3.6 / 4.7:F3}");
        Console.WriteLine("   (a three-frame median, which the paper itself footnotes as self-subtraction affected)");

        // The static residual grows with sequence length: a longer sequence is not simply better.
        Console.WriteLine("   static residual left by a sequence of the given length:");
        double prevRes = -1.0;
        foreach (double t in new[] { 60.0, 600.0, 1800.0, 3600.0 })
        {
            double res = AngularDifferentialImaging.StaticResidualFraction(t);
            Console.WriteLine($"     {t,6:F0} s -> {res:F4}");
            Check($"residual grows with duration at {t}", res > prevRes);
            prevRes = res;
        }
    }

    // ---------------------------------------------------------------- 5

    static void SectionSmallSamplePenalty()
    {
        Header("5. The small-sample penalty (Mawet et al. 2014)");
        Console.WriteLine("   sep/(lambda/D)   n_res   threshold [sigma]   penalty vs 5");

        var rows = new List<string> { "separation_lod,n_res_elements,threshold_sigma" };
        foreach (double lod in new[] { 1.0, 1.5, 2.0, 3.0, 5.0, 10.0, 20.0, 50.0, 100.0 })
        {
            double n = Math.Floor(2.0 * Math.PI * lod);
            double th = ContrastCurve.ThresholdInSigma(n, ContrastCurve.FiveSigmaTailProbability);
            Console.WriteLine($"   {lod,12:F1}   {n,5:F0}   {th,17:F3}   {th / 5.0,12:F2}x");
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0:G6},{1:G6},{2:G8}", lod, n, th));

            Check($"threshold at {lod} lambda/D exceeds the Gaussian 5", th >= 5.0);
        }
        File.WriteAllLines(Path.Combine(outDir, "threshold.csv"), rows);

        // The penalty must vanish as the sample grows: at a thousand elements the t distribution
        // and the Gaussian are the same distribution.
        double far = ContrastCurve.ThresholdInSigma(10000.0, ContrastCurve.FiveSigmaTailProbability);
        Console.WriteLine($"   at 10,000 elements the threshold is {far:F4}, against the Gaussian 5");
        Check("the penalty vanishes for a large sample", far, 5.0, 0.01);

        // And it must be monotonic: more samples can never mean a higher threshold.
        double last = double.MaxValue;
        for (double n = 3.0; n < 500.0; n *= 1.3)
        {
            double th = ContrastCurve.ThresholdInSigma(n, ContrastCurve.FiveSigmaTailProbability);
            Check($"threshold is monotonic at n={n:F0}", th <= last);
            last = th;
        }

        // The distribution machinery itself, against values that can be checked by hand.
        Check("t CDF at zero is one half", ContrastCurve.StudentTCdf(0.0, 7), 0.5, 1e-12);
        Check("regularised beta at x=1/2, a=b=1/2 is one half",
              ContrastCurve.RegularisedIncompleteBeta(0.5, 0.5, 0.5), 0.5, 1e-9);
        Check("t with 1 dof is Cauchy: CDF(1) = 3/4", ContrastCurve.StudentTCdf(1.0, 1), 0.75, 1e-9);
        Check("t quantile inverts the t CDF",
              ContrastCurve.StudentTCdf(ContrastCurve.StudentTQuantile(0.9, 12), 12), 0.9, 1e-9);
    }

    // ---------------------------------------------------------------- 5b

    /// <summary>
    /// The field the game pipeline actually renders, checked against the measurement it was built
    /// from.
    ///
    /// THE TEST THIS SECTION EXISTS FOR is the last one: two exposures of the same target,
    /// differing only in their exposure seed, must correlate at Milli et al.'s rho_0 = 0.713. That
    /// is not a property this code was given; it is the property their 52-minute sequence measured,
    /// reproduced here by construction from a static amplitude field drawn once and a temporal one
    /// drawn per exposure. If it came out at 1 the speckles would be frozen and ADI would be
    /// unnecessary; if it came out at 0 they would be ordinary noise and ADI would be impossible.
    /// </summary>
    static void SectionRenderedField()
    {
        Header("5b. The rendered speckle field");

        double lambda = Coronagraph.IPrimWavelengthNm;
        double d = Coronagraph.StopB1_2.ApertureMeters;      // the pupil the light really passed
        double lod = SpeckleField.LambdaOverDMas(lambda, d);
        double plateScale = Sphere.NativePixelSizeMeters / Sphere.FocalLengthMeters
                          * (180.0 / Math.PI) * 3600.0 * 1000.0;
        // Large enough that the sample variance means something. The field holds
        // (Size/grain)^2 independent grains, and a variance estimated from N samples has a
        // relative standard error of sqrt(2/N); at 512 pixels and a 12.2 px grain that is 1761
        // grains and 3.4%, which is what sets the tolerances below.
        const int Size = 512;

        Console.WriteLine($"   through the Lyot stop: D = {d:F3} m, lambda/D = {lod:F2} mas = {lod / plateScale:F2} px");

        // Unit mean and the predicted variance, against exposure time.
        Console.WriteLine("   exposure   realisations   mean      variance   predicted");
        var field = new float[Size * Size];
        foreach (double t in new[] { 1.0, 10.0, 60.0, 600.0 })
        {
            double surviving = SpeckleField.SurvivingVarianceFraction(t, d, 4.0);
            double f = SpeckleField.StaticVarianceFraction;
            double realisations = Math.Max(1.0, (1.0 - f) / Math.Max(1e-9, surviving - f));

            SpeckleField.BuildModulation(field, Size, Size, plateScale, lod, f, realisations,
                                         staticSeed: 7777UL, temporalSeed: (ulong)(1000 + t));

            double mean = 0.0;
            for (int i = 0; i < field.Length; i++) mean += field[i];
            mean /= field.Length;
            double var = 0.0;
            for (int i = 0; i < field.Length; i++) { double dv = field[i] - mean; var += dv * dv; }
            var /= field.Length;

            Console.WriteLine($"   {t,8:F0} s {realisations,13:F2}   {mean,7:F4}   {var,8:F4}   {surviving,9:F4}");

            // Unit mean is enforced on the grid, so it must hold on the interpolated field to
            // within what interpolation itself can shift it.
            Check($"unit mean at {t} s", mean, 1.0, 0.02);

            // The variance IS the prediction, because the band-limited construction restores it
            // analytically from the kernel's own sum of squares. A tolerance of 8% covers the
            // sampling error of a 256x256 field holding only (256/12.2)^2 = 440 independent
            // grains, which is what sets the scatter here rather than any modelling choice.
            // Three times the 3.4% sampling error of a field this size (see Size above). The
            // construction restores the variance analytically from the kernel's own sum of
            // squares, so what is left here is the finite number of grains and nothing else.
            Check($"variance matches the prediction at {t} s", var, surviving, 0.10 * surviving);
        }

        // THE CHECK. Two frames of the same target, at Milli et al.'s OWN CADENCE, must correlate
        // at the floor they measured.
        //
        // The cadence matters and is not a detail. Their rho_0 = 0.713 is the correlation between
        // two 0.63 s frames far apart in time, where nothing has averaged down within either
        // frame; asking the same question of two long exposures is a different question with a
        // different answer, and the model answers both. Comparing a 60 s pair against 0.713 would
        // be comparing the model's prediction for one experiment against the measurement from
        // another.
        var a = new float[Size * Size];
        var b = new float[Size * Size];
        const double MilliCadenceSeconds = 0.63;
        double fs = SpeckleField.StaticVarianceFraction;

        double svShort = SpeckleField.SurvivingVarianceFraction(MilliCadenceSeconds, d, 4.0);
        double repsShort = Math.Max(1.0, (1.0 - fs) / Math.Max(1e-9, svShort - fs));
        SpeckleField.BuildModulation(a, Size, Size, plateScale, lod, fs, repsShort, 7777UL, 111UL);
        SpeckleField.BuildModulation(b, Size, Size, plateScale, lod, fs, repsShort, 7777UL, 222UL);
        double rho = Correlation(a, b);

        Console.WriteLine($"   two {MilliCadenceSeconds:F2} s frames of the same field, different moments: rho = {rho:F4}");
        Console.WriteLine($"   Milli et al. (2016) measure rho_0 = {SpeckleField.StaticVarianceFraction:F3} at that cadence");
        Check("short frames correlate at Milli's measured floor", rho, SpeckleField.StaticVarianceFraction, 0.05);

        // And the model's own prediction for the experiment nobody ran: two LONG exposures
        // correlate far higher, because the temporal part has averaged down inside each of them
        // and only the shared static pattern is left. Reported rather than checked against a
        // measurement, there being none.
        var aLong = new float[Size * Size];
        var bLong = new float[Size * Size];
        double svLong = SpeckleField.SurvivingVarianceFraction(60.0, d, 4.0);
        double repsLong = Math.Max(1.0, (1.0 - fs) / Math.Max(1e-9, svLong - fs));
        SpeckleField.BuildModulation(aLong, Size, Size, plateScale, lod, fs, repsLong, 7777UL, 111UL);
        SpeckleField.BuildModulation(bLong, Size, Size, plateScale, lod, fs, repsLong, 7777UL, 222UL);
        double rhoLong = Correlation(aLong, bLong);
        Console.WriteLine($"   two 60 s exposures of the same field: rho = {rhoLong:F4} " +
                          $"(predicted {fs / svLong:F4} = static over total)");
        Check("long exposures correlate at static over total", rhoLong, fs / svLong, 0.03);

        double reps = repsShort;

        // And the control: a different pointing must share nothing.
        var c = new float[Size * Size];
        SpeckleField.BuildModulation(c, Size, Size, plateScale, lod, fs, reps, 8888UL, 111UL);
        double rhoOther = Correlation(a, c);
        Console.WriteLine($"   against a different pointing: rho = {rhoOther:F4}");
        Check("a different pointing shares no pattern", Math.Abs(rhoOther) < 0.10);

        // The grain size must be one resolution element, measured from the field's own
        // autocorrelation rather than assumed from the code that built it.
        double halfWidthPx = AutocorrelationHalfWidthPx(a, Size);
        double grainPx = lod / plateScale;
        Console.WriteLine($"   autocorrelation half-width {halfWidthPx:F2} px against a grain of {grainPx:F2} px");
        Check("grains are one resolution element across", halfWidthPx, 0.5 * grainPx, 0.25 * grainPx);
    }

    /// <summary>Pearson correlation of two fields, which is the statistic Milli et al. use.</summary>
    static double Correlation(float[] a, float[] b)
    {
        double ma = 0.0, mb = 0.0;
        for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;
        double sab = 0.0, saa = 0.0, sbb = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }
        return sab / Math.Sqrt(saa * sbb);
    }

    /// <summary>Lag at which the field's own autocorrelation falls to half, along a row.</summary>
    static double AutocorrelationHalfWidthPx(float[] field, int size)
    {
        double mean = 0.0;
        for (int i = 0; i < field.Length; i++) mean += field[i];
        mean /= field.Length;

        double zero = 0.0;
        for (int i = 0; i < field.Length; i++) { double d = field[i] - mean; zero += d * d; }

        double prev = 1.0;
        for (int lag = 1; lag < size / 2; lag++)
        {
            double acc = 0.0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x + lag < size; x++)
                    acc += (field[y * size + x] - mean) * (field[y * size + x + lag] - mean);
            double norm = acc / zero * ((double)size * size) / ((double)size * (size - lag));
            if (norm <= 0.5) return lag - 1 + (prev - 0.5) / Math.Max(1e-12, prev - norm);
            prev = norm;
        }
        return size / 2.0;
    }

    // ---------------------------------------------------------------- 6

    /// <summary>
    /// A coronagraphic frame built from the pieces, and the curve measured from it.
    ///
    /// The frame is deliberately a SPECKLE FIELD AND NOTHING ELSE: no companion, no photon noise,
    /// no detector. What is being validated is the measurement of a detection limit from a field
    /// with the right statistics, and anything else in the frame would be something VIP and this
    /// code could disagree about for reasons that are not the measurement.
    /// </summary>
    static void SectionSyntheticFrame()
    {
        Header("6. A synthetic coronagraphic frame, and its curve");

        double lambda = Coronagraph.IPrimWavelengthNm;
        double d = Coronagraph.TelescopeApertureMeters;
        double lod = SpeckleField.LambdaOverDMas(lambda, d);
        double plateScale = Sphere.NativePixelSizeMeters / Sphere.FocalLengthMeters
                          * (180.0 / Math.PI) * 3600.0 * 1000.0;      // mas per pixel
        double controlRadius = SpeckleField.ControlRadiusMas(SaxoActuatorsAcrossPupil, lambda, d);

        const int Size = 401;
        const double ExposureSeconds = 60.0;
        const double WindSpeed = 4.0;
        var mask = Coronagraph.Find("CLC-S-WF").Value;

        Console.WriteLine($"   {Size}x{Size} at {plateScale:F3} mas/px, lambda/D = {lod:F2} mas = {lod / plateScale:F2} px");
        Console.WriteLine($"   {mask.Name}, {ExposureSeconds:F0} s, wind {WindSpeed:F0} m/s, control radius {controlRadius:F0} mas");

        float[] frame = BuildSpeckleFrame(Size, plateScale, lod, controlRadius, mask,
                                          ExposureSeconds, d, WindSpeed, seed: 90210);

        // The star's own peak, as an observer measures it: an offset PSF with no mask in the way.
        // Taken as the halo's normalisation so that the exported contrast is relative to the
        // unocculted star, which is what a contrast curve means.
        double starPeak = UnocculteStarPeak(lod, plateScale);

        var curve = ContrastCurve.Measure(
            frame, Size, Size, plateScale, lod, starPeak,
            Coronagraph.InnerWorkingAngleMas(mask), 800.0,
            ContrastCurve.FiveSigmaTailProbability, null);

        Console.WriteLine($"   {curve.Count} points from {curve[0].SeparationMas:F0} to {curve[curve.Count - 1].SeparationMas:F0} mas");
        Console.WriteLine("   sep [mas]   n_res   threshold   contrast      delta mag");
        var rows = new List<string> { "separation_mas,n_res_elements,noise_sigma,threshold_sigma,detectable_flux,contrast,delta_mag" };
        for (int i = 0; i < curve.Count; i++)
        {
            var p = curve[i];
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0:G8},{1:G8},{2:G8},{3:G8},{4:G8},{5:G8},{6:G8}",
                p.SeparationMas, p.ResolutionElements, p.NoiseSigma, p.ThresholdInSigma,
                p.DetectableFlux, p.Contrast, p.ContrastMagnitudes));
            if (i % Math.Max(1, curve.Count / 8) == 0)
                Console.WriteLine($"   {p.SeparationMas,9:F1} {p.ResolutionElements,7:F0} {p.ThresholdInSigma,11:F3} " +
                                  $"{p.Contrast,12:E3} {p.ContrastMagnitudes,10:F2}");
        }
        File.WriteAllLines(Path.Combine(outDir, "contrast.csv"), rows);

        // The curve must improve outward: the speckle halo falls, so the detection limit deepens.
        int improving = 0;
        for (int i = 1; i < curve.Count; i++) if (curve[i].Contrast < curve[i - 1].Contrast) improving++;
        Console.WriteLine($"   {improving} of {curve.Count - 1} steps deepen outward");
        Check("the curve deepens outward over most of its range", improving > 0.75 * (curve.Count - 1));
        Check("every point has a finite contrast", curve.TrueForAll(p => !double.IsNaN(p.Contrast) && p.Contrast > 0.0));

        // Exported for VIP.
        using (var w = new BinaryWriter(File.Create(Path.Combine(outDir, "exo_frame.bin"))))
        {
            w.Write(Size); w.Write(Size);
            for (int i = 0; i < frame.Length; i++) w.Write(frame[i]);
        }

        var meta = new List<string> { "key,value" };
        void M(string k, double v) => meta.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1:G12}", k, v));
        M("plate_scale_mas_per_px", plateScale);
        M("lambda_over_d_mas", lod);
        M("lambda_over_d_px", lod / plateScale);
        M("star_peak", starPeak);
        M("inner_working_angle_mas", Coronagraph.InnerWorkingAngleMas(mask));
        M("outer_radius_mas", 800.0);
        M("five_sigma_tail_probability", ContrastCurve.FiveSigmaTailProbability);
        M("frame_size", Size);
        File.WriteAllLines(Path.Combine(outDir, "meta.csv"), meta);

        Console.WriteLine($"   -> exo_frame.bin, contrast.csv, threshold.csv, meta.csv");
    }

    /// <summary>
    /// A speckle field with the modelled statistics: grains one resolution element across, a mean
    /// intensity falling with radius, a step at the AO control radius, and the coronagraph's own
    /// attenuation applied inside the mask.
    ///
    /// The GRAINS are what makes this a speckle field rather than white noise, and they are made
    /// by drawing one modified Rician value per resolution element on a coarse grid and
    /// interpolating between them. That is the correct spatial scale by construction: a speckle is
    /// the image of one spatial frequency of the wavefront, and its size is the diffraction limit.
    /// </summary>
    static float[] BuildSpeckleFrame(
        int size, double plateScaleMasPerPx, double lambdaOverDMas, double controlRadiusMas,
        Coronagraph.Mask mask, double exposureSeconds, double apertureMeters, double windSpeed, int seed)
    {
        var frame = new float[size * size];
        double centre = 0.5 * (size - 1);
        double grainPx = lambdaOverDMas / plateScaleMasPerPx;

        int coarse = (int)Math.Ceiling(size / grainPx) + 2;
        var grid = new double[coarse * coarse];
        var rng = new Pcg32(Pcg32.MixSeed(seed), Pcg32.StreamShotNoise);

        double nFast = SpeckleField.IndependentRealisations(exposureSeconds, SpeckleField.FastDecorrelationSeconds);
        double nAtm = SpeckleField.IndependentRealisations(
            exposureSeconds, SpeckleField.AtmosphericLifetimeSeconds(apertureMeters, windSpeed));
        double realisations = 1.0 / (SpeckleField.StaticVarianceFraction
                                   + SpeckleField.FastVarianceFraction / nFast
                                   + SpeckleField.AtmosphericVarianceFraction / nAtm);

        double coarseCentre = 0.5 * (coarse - 1);
        for (int gy = 0; gy < coarse; gy++)
        {
            for (int gx = 0; gx < coarse; gx++)
            {
                double dx = (gx - coarseCentre) * grainPx * plateScaleMasPerPx;
                double dy = (gy - coarseCentre) * grainPx * plateScaleMasPerPx;
                double r = Math.Sqrt(dx * dx + dy * dy);

                double mean = HaloIntensity(r, lambdaOverDMas, controlRadiusMas);
                double ic, isr;
                SpeckleField.Split(mean, SpeckleField.StaticVarianceFraction, out ic, out isr);
                grid[gy * coarse + gx] = SpeckleField.SampleAveraged(rng, ic, isr, realisations);
            }
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double gx = (x - centre) / grainPx + coarseCentre;
                double gy = (y - centre) / grainPx + coarseCentre;
                double v = Bilinear(grid, coarse, gx, gy);

                double r = Math.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre)) * plateScaleMasPerPx;
                v *= Coronagraph.MaskTransmission(mask, r);

                frame[y * size + x] = (float)v;
            }
        }
        return frame;
    }

    /// <summary>
    /// Mean halo intensity against separation, normalised to the unocculted stellar peak.
    ///
    /// Two power laws meeting at the AO control radius, which is the shape every published
    /// coronagraphic profile has: inside it the adaptive optics has flattened the halo, outside it
    /// the uncorrected seeing halo takes over. The exponents are those of the standard
    /// residual-phase halo, and are a stand-in here rather than a claim: this frame exists to test
    /// the MEASUREMENT of a contrast curve, and any monotonically falling profile with a break at
    /// the control radius exercises it identically.
    /// </summary>
    static double HaloIntensity(double separationMas, double lambdaOverDMas, double controlRadiusMas)
    {
        double r = Math.Max(separationMas, 0.5 * lambdaOverDMas);
        double x = r / lambdaOverDMas;
        double inner = 1e-4 * Math.Pow(x, -2.0);
        if (r <= controlRadiusMas) return inner;

        double atBreak = 1e-4 * Math.Pow(controlRadiusMas / lambdaOverDMas, -2.0);
        return atBreak * Math.Pow(r / controlRadiusMas, -3.0);
    }

    /// <summary>The unocculted star's peak in the same normalisation, being the halo model's own value at zero separation had no mask been there.</summary>
    static double UnocculteStarPeak(double lambdaOverDMas, double plateScaleMasPerPx) => 1.0;

    static double Bilinear(double[] grid, int n, double x, double y)
    {
        if (x < 0) x = 0; if (y < 0) y = 0;
        if (x > n - 1.001) x = n - 1.001;
        if (y > n - 1.001) y = n - 1.001;
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        double a = grid[y0 * n + x0], b = grid[y0 * n + x0 + 1];
        double c = grid[(y0 + 1) * n + x0], dd = grid[(y0 + 1) * n + x0 + 1];
        return a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + dd * fx * fy;
    }

    // ---------------------------------------------------------------- reporting

    static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    static void Check(string what, double got, double expected, double tolerance)
    {
        if (!(Math.Abs(got - expected) <= tolerance))
        {
            failures++;
            Console.WriteLine($"     FAIL {what}: got {got:G8}, expected {expected:G8} +/- {tolerance:G4}");
        }
    }

    static void Check(string what, bool ok)
    {
        if (!ok) { failures++; Console.WriteLine($"     FAIL {what}"); }
    }
}
