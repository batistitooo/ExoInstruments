using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Does the calibration chain calibrate?
///
/// WHAT THIS IS FOR. A flat field and a bias frame are worth taking only if two things are true:
/// what they measure is really in the light frames, and dividing or subtracting them really takes
/// it out. Neither is self-evident from reading the code, because both are statements about the
/// COMPOSITION of stages that are written far apart: a map built once per sensor, a multiplication
/// applied before a Poisson draw, an addition applied before a converter. This harness closes that
/// loop numerically. It builds the same maps the pipeline builds, from the shipped Core, runs
/// frames through the same arithmetic, and then reduces them the way an observer would.
///
/// WHAT IS REAL AND WHAT IS REPLICATED. Every physical model is the shipped Core, called
/// unmodified: SensorNonUniformity for the two maps, FocalPlaneIllumination for the optics,
/// DetectorLinearity for the amplifier, DarkCurrentModel for the thermal term, Pcg32 and
/// NoiseSampler for every draw, and VisualTelescopeCatalog for every instrument figure. What is
/// replicated is the ORDER those are applied in, because RunDetectorChain lives in the Unity layer
/// and cannot be linked here; the replication is one function, RunChain below, written out in full
/// so that a divergence from the pipeline's order would be visible on the page rather than hidden.
///
/// Sections:
///   1. The maps carry the published numbers.
///   2. Binning moves the two non-uniformities in opposite directions.
///   3. The illumination geometry, per instrument.
///   4. The linearity model inverts itself.
///   5. ESO's own bias QC1 decomposition, run on a simulated bias.
///   6. The calibration removes what it should, and stops where photon noise says it must.
/// </summary>
static class CalibrationTests
{
    const long SensorSerialSeed = 20260721L;

    static readonly (string Name, VisualTelescopeSpec Spec)[] Ground =
    {
        ("RC20",     VisualTelescopeCatalog.Rc20),
        ("RedCat51", VisualTelescopeCatalog.RedCat51),
        ("CDK1000",  VisualTelescopeCatalog.Cdk1000),
        ("FORS2",    VisualTelescopeCatalog.Fors2Vlt),
        ("SPHERE",   VisualTelescopeCatalog.Sphere),
    };

    static int failures;
    static string outDir = ".";

    static int Main(string[] args)
    {
        // Invariant throughout. A harness whose output is a table of numbers must print the same
        // table on every machine, and this one is developed on a locale that writes decimal commas.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--out") outDir = args[i + 1];
        Directory.CreateDirectory(outDir);

        Console.WriteLine("ExoInstruments calibration chain: does it calibrate?");
        Console.WriteLine(new string('=', 78));

        SectionMapsCarryPublishedNumbers();
        SectionBinningLaw();
        SectionIlluminationGeometry();
        SectionLinearityInverts();
        SectionBiasQc1();
        SectionCalibrationRemovesWhatItShould();
        ExportForCrossValidation();

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- section 1

    /// <summary>
    /// The map's measured spread must be the catalogue's published figure, transformed by the
    /// binning law and by nothing else. This is the check that would catch a factor of two in
    /// either direction, which is exactly the size of the error that hardware binning invites.
    /// </summary>
    static void SectionMapsCarryPublishedNumbers()
    {
        Header("1. The maps carry the published numbers");
        const int n = 1 << 20;   // a megapixel: enough that the sample sigma is good to 0.07%

        foreach (var (name, spec) in Ground)
        {
            int nativePerSide = Math.Max(1, spec.SensorNativePixelsPerSide);

            double prnuExpected = SensorNonUniformity.BinnedPhotoResponseSigma(
                spec.PhotoResponseNonUniformity, nativePerSide);
            double fpnExpected = SensorNonUniformity.BinnedOffsetSigmaElectrons(
                spec.OffsetFixedPatternElectrons, nativePerSide);

            ushort[] prnuMap = SensorNonUniformity.BuildPhotoResponseMap(
                Pcg32.MixSeed(SensorSerialSeed), n, prnuExpected);
            ushort[] fpnMap = SensorNonUniformity.BuildOffsetMap(
                Pcg32.MixSeed(SensorSerialSeed), n, fpnExpected);

            double prnuMeasured = Sigma(prnuMap, n);
            double fpnMeasured = Sigma(fpnMap, n);

            string published = double.IsNaN(spec.PhotoResponseNonUniformity)
                ? "not published"
                : (spec.PhotoResponseNonUniformity * 100.0).ToString("F3", CultureInfo.InvariantCulture) + "%";

            Console.WriteLine($"  {name,-9} sensor pixel PRNU {published,-14} " +
                              $"read-out pixel {prnuMeasured * 100.0:F4}% (expect {prnuExpected * 100.0:F4}%), " +
                              $"offset {fpnMeasured:F4} e- (expect {fpnExpected:F4})");

            // 1% of the value, which is fifteen times the sampling error of a megapixel draw, so
            // this tolerance catches a real discrepancy and never a run of the dice.
            Check($"{name} PRNU map sigma", prnuMeasured, prnuExpected, Math.Max(1e-9, 0.01 * prnuExpected));
            Check($"{name} offset map sigma", fpnMeasured, fpnExpected, Math.Max(1e-9, 0.01 * fpnExpected));

            // The mean must be zero to well within its own sampling error, or the map carries a
            // scale error no flat can remove because the flat carries the same one.
            Check($"{name} PRNU map mean", Mean(prnuMap, n), 0.0, 1e-6);
            Check($"{name} offset map mean", Mean(fpnMap, n), 0.0, 1e-4);
        }
    }

    // ---------------------------------------------------------------- section 2

    /// <summary>
    /// Binning averages a multiplicative per-pixel term and sums an additive one, so one falls as
    /// 1/n while the other grows as n. Checked as a measured ratio over the pipeline's whole
    /// binning range rather than as an assertion about the formula, since the formula is the thing
    /// under test.
    /// </summary>
    static void SectionBinningLaw()
    {
        Header("2. Binning moves the two non-uniformities in opposite directions");
        var spec = VisualTelescopeCatalog.Rc20;   // the roster's only device with both published
        const int n = 1 << 20;

        Console.WriteLine("  ASI294MM Pro, sensor pixel PRNU 0.620%, DSNU 0.970 e-");
        Console.WriteLine("  bin  native/side   PRNU        offset");
        foreach (int bin in new[] { 1, 2, 3, 4 })
        {
            int nativePerSide = spec.SensorNativePixelsPerSide * bin;
            double prnu = SensorNonUniformity.BinnedPhotoResponseSigma(spec.PhotoResponseNonUniformity, nativePerSide);
            double fpn = SensorNonUniformity.BinnedOffsetSigmaElectrons(spec.OffsetFixedPatternElectrons, nativePerSide);

            double prnuMeasured = Sigma(SensorNonUniformity.BuildPhotoResponseMap(Pcg32.MixSeed(SensorSerialSeed), n, prnu), n);
            double fpnMeasured = Sigma(SensorNonUniformity.BuildOffsetMap(Pcg32.MixSeed(SensorSerialSeed), n, fpn), n);

            Console.WriteLine($"  {bin,3}  {nativePerSide,11}   {prnuMeasured * 100.0,7:F4}%   {fpnMeasured,7:F4} e-");

            Check($"bin {bin} PRNU follows 1/n", prnuMeasured, spec.PhotoResponseNonUniformity / nativePerSide,
                  0.01 * spec.PhotoResponseNonUniformity / nativePerSide);
            Check($"bin {bin} offset follows n", fpnMeasured, spec.OffsetFixedPatternElectrons * nativePerSide,
                  0.01 * spec.OffsetFixedPatternElectrons * nativePerSide);
        }

        // The product is invariant, which is the statement that binning trades one for the other
        // rather than improving or worsening the pair.
        double p1 = SensorNonUniformity.BinnedPhotoResponseSigma(spec.PhotoResponseNonUniformity, 2);
        double f1 = SensorNonUniformity.BinnedOffsetSigmaElectrons(spec.OffsetFixedPatternElectrons, 2);
        double p4 = SensorNonUniformity.BinnedPhotoResponseSigma(spec.PhotoResponseNonUniformity, 8);
        double f4 = SensorNonUniformity.BinnedOffsetSigmaElectrons(spec.OffsetFixedPatternElectrons, 8);
        Console.WriteLine($"  product PRNU x offset: bin 1 {p1 * f1:E4}, bin 4 {p4 * f4:E4} (invariant)");
        Check("PRNU x offset invariant under binning", p4 * f4, p1 * f1, 1e-12);
    }

    // ---------------------------------------------------------------- section 3

    /// <summary>
    /// What the optics alone do to a flat, per instrument, from each one's own published focal
    /// length, pixel pitch and format. The interesting output here is not a pass or a fail but the
    /// TABLE: it is the statement that a wide-field astrograph and a long-focus one differ by two
    /// orders of magnitude in the same term, and that exactly one instrument on the roster has a
    /// detector larger than its illuminated field.
    /// </summary>
    static void SectionIlluminationGeometry()
    {
        Header("3. Illumination geometry");
        Console.WriteLine("  instrument  f (m)    corner (deg)  cos^4 loss   illuminated");
        var rows = new List<string> { "instrument,focal_length_m,corner_deg,cos4_loss_percent,illuminated_fraction" };

        foreach (var (name, spec) in Ground)
        {
            int w = spec.NativeSensorWidthPx, h = spec.NativeSensorHeightPx;
            double pitch = spec.NativePixelSizeMeters;
            double f = spec.FocalLengthMeters;

            double halfDiagonal = 0.5 * Math.Sqrt((w * pitch) * (w * pitch) + (h * pitch) * (h * pitch));
            double cornerDeg = Math.Atan(halfDiagonal / f) * 180.0 / Math.PI;
            double cornerLoss = 1.0 - FocalPlaneIllumination.CosineFourth(halfDiagonal, f);

            // Fraction of the detector's pixels that receive any light at all. Sampled on a coarse
            // grid rather than at full resolution: the stop is a straight edge, so a 512x512 sample
            // locates it to a fifth of a percent of the frame and the full array would only cost
            // time.
            const int Sample = 512;
            int inside = 0;
            for (int y = 0; y < Sample; y++)
            {
                double dy = ((y + 0.5) / Sample - 0.5) * h * pitch;
                for (int x = 0; x < Sample; x++)
                {
                    double dx = ((x + 0.5) / Sample - 0.5) * w * pitch;
                    if (FocalPlaneIllumination.Factor(dx, dy, f, spec.FieldStopSquareArcmin, spec.ImageCircleMillimetres) > 0.0)
                        inside++;
                }
            }
            double illuminated = inside / (double)(Sample * Sample);

            Console.WriteLine($"  {name,-10}  {f,7:F3}  {cornerDeg,11:F4}  {cornerLoss * 100.0,9:F4}%  {illuminated * 100.0,9:F2}%");
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1:G6},{2:G6},{3:G6},{4:G6}",
                                   name, f, cornerDeg, cornerLoss * 100.0, illuminated));

            Check($"{name} illumination is unity on axis",
                  FocalPlaneIllumination.Factor(0, 0, f, spec.FieldStopSquareArcmin, spec.ImageCircleMillimetres), 1.0, 1e-12);
        }

        // FORS2's stop is published as an angle, so the check is an angle: the illuminated square
        // must be 6.8 arcmin on a side at the instrument's own plate scale, whatever the detector
        // around it happens to be.
        var fors2 = VisualTelescopeCatalog.Fors2Vlt;
        double halfSide = 0.5 * fors2.FocalLengthMeters * Math.Tan(6.8 * Math.PI / (180.0 * 60.0));
        Check("FORS2 stop edge is inside", FocalPlaneIllumination.Factor(halfSide * 0.999, 0, fors2.FocalLengthMeters,
              fors2.FieldStopSquareArcmin, fors2.ImageCircleMillimetres) > 0.0);
        Check("FORS2 stop edge is outside", FocalPlaneIllumination.Factor(halfSide * 1.001, 0, fors2.FocalLengthMeters,
              fors2.FieldStopSquareArcmin, fors2.ImageCircleMillimetres) == 0.0);

        File.WriteAllLines(Path.Combine(outDir, "illumination.csv"), rows);
        Console.WriteLine($"  -> {Path.Combine(outDir, "illumination.csv")}");
    }

    // ---------------------------------------------------------------- section 4

    /// <summary>
    /// The effect and its correction are one quadratic solved in both directions, so composing them
    /// must return the identity to machine precision. If it ever does not, a reduction pipeline
    /// built on Correct would not undo what Measured did, which is the only property that makes
    /// either of them worth having.
    /// </summary>
    static void SectionLinearityInverts()
    {
        Header("4. The linearity model inverts itself");
        var spec = VisualTelescopeCatalog.Fors2Vlt;
        double fullWell = spec.FullWellElectrons;
        double d = spec.LinearityDeviationAtFullWell;

        Console.WriteLine($"  FORS2 MIT chip 1, low gain: deviation at full well {d * 100.0:F2}%, full well {fullWell:F0} e-");
        Console.WriteLine("  charge (e-)   measured     recovered     round-trip error");

        double worst = 0.0;
        foreach (double frac in new[] { 0.01, 0.1, 0.25, 0.5, 0.75, 0.9, 1.0 })
        {
            double q = frac * fullWell;
            double m = DetectorLinearity.Measured(q, fullWell, d);
            double back = DetectorLinearity.Correct(m, fullWell, d);
            double err = Math.Abs(back - q);
            worst = Math.Max(worst, err / q);
            Console.WriteLine($"  {q,11:F0}   {m,10:F1}   {back,10:F1}   {err / q,16:E3}");
        }
        Check("linearity round trip", worst, 0.0, 1e-12);

        // And the parameter means what it is named: at full well the reported charge is short of
        // the real one by exactly the published fraction.
        double atFull = DetectorLinearity.Measured(fullWell, fullWell, d);
        Check("deviation at full well equals the published figure", (fullWell - atFull) / fullWell, d, 1e-12);

        // An unpublished deviation must leave the charge untouched rather than default to something.
        Check("NaN deviation is a no-op", DetectorLinearity.Measured(1234.5, fullWell, double.NaN), 1234.5, 0.0);
    }

    // ---------------------------------------------------------------- section 5

    /// <summary>
    /// ESO's own recipe, run on our own bias.
    ///
    /// The FORS2 bias QC1 procedure decomposes a raw bias into three numbers: the read-out noise,
    /// the fixed pattern, and the large-scale structure. RON comes from the pixel-by-pixel
    /// difference of two biases divided by sqrt(2); the fixed pattern from the difference of one
    /// bias with itself shifted by 10x10 pixels, with the read-noise contribution removed; the
    /// structure from what is left of the total standard deviation once those two are taken out.
    ///
    /// This is the strongest available check on the offset map, because it is not our own
    /// definition of what a fixed pattern is. Feed the simulated frames to the observatory's
    /// procedure and it must return the numbers that went in.
    ///
    /// The shift-difference works because the read noise is independent between the two copies of
    /// the frame while the fixed pattern is the SAME field displaced, so a shift by more than the
    /// pattern's correlation length turns it into two independent draws of the same distribution;
    /// both terms therefore contribute twice their variance, and halving gives each back.
    /// </summary>
    static void SectionBiasQc1()
    {
        Header("5. ESO's bias QC1 decomposition, run on a simulated bias");
        var spec = VisualTelescopeCatalog.Rc20;
        const int W = 1024, H = 1024;

        int nativePerSide = spec.SensorNativePixelsPerSide;   // binning 1
        double fpnSigmaElectrons = SensorNonUniformity.BinnedOffsetSigmaElectrons(
            spec.OffsetFixedPatternElectrons, nativePerSide);
        double ronElectrons = spec.ReadNoiseElectrons;
        double k = spec.ElectronsPerAduAtUnityGain;

        ushort[] offsets = SensorNonUniformity.BuildOffsetMap(Pcg32.MixSeed(SensorSerialSeed), W * H, fpnSigmaElectrons);

        float[] biasA = SimulateBias(spec, offsets, W, H, seed: 11);
        float[] biasB = SimulateBias(spec, offsets, W, H, seed: 22);

        // QC.RON: sigma of the pairwise difference over sqrt(2), in ADU.
        double ronAdu = SigmaOfDifference(biasA, biasB) / Math.Sqrt(2.0);

        // QC.BIAS.FPN: the same construction on one frame against itself shifted by 10x10, minus
        // the read noise that the shift also doubled.
        double shiftedSigma = SigmaOfShiftedDifference(biasA, W, H, 10, 10) / Math.Sqrt(2.0);
        double fpnAdu = Math.Sqrt(Math.Max(0.0, shiftedSigma * shiftedSigma - ronAdu * ronAdu));

        // QC.BIAS.STRUCT: whatever the frame's total spread has that those two do not explain.
        double totalAdu = Sigma(biasA);
        double structAdu = Math.Sqrt(Math.Max(0.0, totalAdu * totalAdu - ronAdu * ronAdu - fpnAdu * fpnAdu));

        // WHAT THE CONVERTER ADDS, and why the expected RON is not simply the catalogue's.
        //
        // Digitisation truncates to whole counts, which is a uniform error over one ADU and
        // therefore a variance of 1/12 of an ADU squared, independent of everything else. Any
        // estimator that measures the spread of a bias frame measures that too, so what QC.RON can
        // recover is the read noise AND the quantisation in quadrature, and expecting the read
        // noise alone would be expecting the estimator to see through the converter.
        //
        // On this camera that is not a correction, it is the dominant term: 1.2 e- of read noise at
        // 4.03 e-/ADU is 0.298 ADU, against 0.289 ADU of quantisation. The camera is
        // QUANTISATION-LIMITED in a bias frame, which is a real and well-known operating regime
        // (an observer's remedy is more gain, so that the read noise spans several counts) and one
        // this pipeline can now demonstrate rather than assert.
        double quantisationAdu = 1.0 / Math.Sqrt(12.0);
        double expectedRonAdu = Math.Sqrt(ronElectrons / k * (ronElectrons / k) + quantisationAdu * quantisationAdu);

        Console.WriteLine($"  input:     RON {ronElectrons:F3} e- = {ronElectrons / k:F4} ADU,   " +
                          $"FPN {fpnSigmaElectrons:F3} e- = {fpnSigmaElectrons / k:F4} ADU");
        Console.WriteLine($"  converter: quantisation {quantisationAdu:F4} ADU = {quantisationAdu * k:F3} e- " +
                          $"(K = {k:F3} e-/ADU), so a bias can show at best {expectedRonAdu:F4} ADU");
        Console.WriteLine($"  recovered: RON {ronAdu:F4} ADU (expect {expectedRonAdu:F4}),   " +
                          $"FPN {fpnAdu * k:F3} e- = {fpnAdu:F4} ADU");
        Console.WriteLine($"  structure: {structAdu:F4} ADU (nothing in the model produces any)");
        Console.WriteLine($"  total:     {totalAdu:F4} ADU");

        // 3% tolerances: the estimator is itself noisy at a megapixel, and the two shifted copies
        // in the FPN construction overlap on 99% of the frame rather than all of it.
        Check("QC.RON recovers read noise plus quantisation", ronAdu, expectedRonAdu, 0.03 * expectedRonAdu);
        Check("QC.BIAS.FPN recovers the offset map", fpnAdu * k, fpnSigmaElectrons, 0.03 * fpnSigmaElectrons);
        Check("QC.BIAS.STRUCT finds no large-scale term", structAdu, 0.0, 0.10 * totalAdu);
    }

    // ---------------------------------------------------------------- section 6

    /// <summary>
    /// The whole point, measured: take lights, take calibration frames, reduce, and see what is
    /// left.
    ///
    /// STACKED RATHER THAN SINGLE, and that is the only way this test can mean anything. In one
    /// frame the photon noise is 1.2% and the PRNU is 0.31%, so the fixed pattern is four times
    /// below the noise and a reduction that did nothing at all would pass. Averaging N frames is
    /// what separates them: temporal noise falls as 1/sqrt(N) and the fixed pattern does not fall
    /// at all, so at 64 frames the PRNU stands two and a half times ABOVE what is left of the
    /// photon noise and its removal is something one can actually see.
    ///
    /// That is not a device for the test. It is exactly why the distinction matters to an observer:
    /// stacking is what makes fixed-pattern noise the thing that limits a deep image, and therefore
    /// what makes a flat field worth taking at all.
    ///
    /// The residual is compared against the floor that photon statistics impose. A master flat
    /// built from N frames carries its own shot noise, so dividing by it cannot make a light
    /// flatter than that; a reduction that appeared to beat the floor would be measuring its own
    /// arithmetic rather than the sky.
    /// </summary>
    static void SectionCalibrationRemovesWhatItShould()
    {
        Header("6. The calibration removes what it should");
        var spec = VisualTelescopeCatalog.Rc20;
        const int W = 512, H = 512;
        const int MasterFrames = 64;
        const int LightFrames = 64;
        const double ExposureSeconds = 60.0;

        double k = spec.ElectronsPerAduAtUnityGain;
        int nativePerSide = spec.SensorNativePixelsPerSide;
        double prnuSigma = SensorNonUniformity.BinnedPhotoResponseSigma(spec.PhotoResponseNonUniformity, nativePerSide);
        double fpnSigma = SensorNonUniformity.BinnedOffsetSigmaElectrons(spec.OffsetFixedPatternElectrons, nativePerSide);

        ushort[] prnu = SensorNonUniformity.BuildPhotoResponseMap(Pcg32.MixSeed(SensorSerialSeed), W * H, prnuSigma);
        ushort[] offsets = SensorNonUniformity.BuildOffsetMap(Pcg32.MixSeed(SensorSerialSeed), W * H, fpnSigma);

        // The flat field this sensor and this tube actually have, at the centre of the RC20's
        // field, where the cosine-fourth term is negligible and the PRNU is all of it.
        var flat = new double[W * H];
        for (int i = 0; i < flat.Length; i++) flat[i] = SensorNonUniformity.PhotoResponse(prnu, i);

        double darkPerSecond = spec.DarkCurrentElectronsPerSecond;
        double darkElectrons = darkPerSecond * ExposureSeconds;

        // A stack of light frames of a perfectly uniform sky. Uniform because the residual after
        // calibration is what is being measured, and any real structure would have to be modelled
        // out again before it could be seen.
        const double SkyElectrons = 8000.0;
        float[] light = AverageOf(LightFrames, s => RunChain(spec, flat, offsets, SkyElectrons, darkElectrons, W, H, 100 + s));

        // Masters, built the way an observer builds them: N frames, averaged.
        float[] masterBias = AverageOf(MasterFrames, s => RunChain(spec, flat, offsets, 0.0, 0.0, W, H, 200 + s));
        float[] masterDark = AverageOf(MasterFrames, s => RunChain(spec, flat, offsets, 0.0, darkElectrons, W, H, 300 + s));
        float[] masterFlat = AverageOf(MasterFrames, s => RunChain(spec, flat, offsets,
                                       0.5 * spec.FullWellElectrons, darkElectrons, W, H, 400 + s));

        double before = RelativeSigma(light, masterBias);

        // Reduce: subtract the dark (which carries the bias with it), then divide by the
        // bias-subtracted, normalised flat. This is the standard sequence and the order matters,
        // since the flat is multiplicative and the pedestal is not.
        var reduced = new double[light.Length];
        var flatNorm = new double[light.Length];
        double flatMean = 0.0;
        for (int i = 0; i < light.Length; i++) { flatNorm[i] = masterFlat[i] - masterBias[i]; flatMean += flatNorm[i]; }
        flatMean /= light.Length;
        for (int i = 0; i < light.Length; i++)
        {
            double f = flatNorm[i] / flatMean;
            reduced[i] = (light[i] - masterDark[i]) / (f > 0.0 ? f : 1.0);
        }

        double after = RelativeSigma(reduced);

        // The floor. Every term is a variance in electrons expressed as a fraction of the level it
        // sits on, which is what RelativeSigma returns. The light's own shot and read noise, both
        // averaged over the stack; the converter's quantisation, which averages down with them; and
        // the master flat's shot noise, which the division carries into the result.
        double signalElectrons = SkyElectrons + darkElectrons;
        double quantisationElectrons = spec.ElectronsPerAduAtUnityGain / Math.Sqrt(12.0);
        double perLightVariance = signalElectrons
                                + spec.ReadNoiseElectrons * spec.ReadNoiseElectrons
                                + quantisationElectrons * quantisationElectrons;
        double lightShot = Math.Sqrt(perLightVariance / LightFrames) / signalElectrons;

        double flatLevel = 0.5 * spec.FullWellElectrons;
        double flatVariance = flatLevel + spec.ReadNoiseElectrons * spec.ReadNoiseElectrons
                            + quantisationElectrons * quantisationElectrons;
        double flatShot = Math.Sqrt(flatVariance / MasterFrames) / flatLevel;

        double darkVariance = darkElectrons + spec.ReadNoiseElectrons * spec.ReadNoiseElectrons
                            + quantisationElectrons * quantisationElectrons;
        double darkShot = Math.Sqrt(darkVariance / MasterFrames) / signalElectrons;

        double floor = Math.Sqrt(lightShot * lightShot + flatShot * flatShot + darkShot * darkShot);

        Console.WriteLine($"  sky {SkyElectrons:F0} e-, dark {darkElectrons:F2} e-, PRNU {prnuSigma * 100.0:F3}%, " +
                          $"offset {fpnSigma:F2} e-");
        Console.WriteLine($"  {LightFrames} lights stacked, {MasterFrames} frames per master");

        // The control comes FIRST, because it is what gives the result below its meaning: with the
        // flat step omitted, the PRNU must still be there, standing above what is left of the
        // photon noise. Without this the section would pass just as well on a pipeline that had no
        // PRNU at all.
        var unflattened = new double[light.Length];
        for (int i = 0; i < light.Length; i++) unflattened[i] = light[i] - masterDark[i];
        double withoutFlat = RelativeSigma(unflattened);
        double expectedWithoutFlat = Math.Sqrt(floor * floor + prnuSigma * prnuSigma);

        Console.WriteLine($"  raw stack, pedestal removed:  {before * 100.0:F4}% rms");
        Console.WriteLine($"  bias and dark only, no flat:  {withoutFlat * 100.0:F4}% rms " +
                          $"(expect {expectedWithoutFlat * 100.0:F4}% = floor and PRNU in quadrature)");
        Console.WriteLine($"  bias, dark and flat:          {after * 100.0:F4}% rms");
        Console.WriteLine($"  photon-noise floor:           {floor * 100.0:F4}% rms");
        Console.WriteLine($"  the flat removes a factor of {withoutFlat / after:F2} of residual pattern");

        Check("omitting the flat leaves exactly the PRNU behind", withoutFlat, expectedWithoutFlat, 0.10 * expectedWithoutFlat);

        // The PRNU has to be the LARGER term in that control, or the control proves nothing.
        Check("the control is dominated by the PRNU, not by the photon floor", prnuSigma > floor);

        // The reduced frame must sit at the floor: not above it, which would mean the calibration
        // left something behind, and not below it, which would mean the frames were never
        // independent in the first place.
        Check("reduction reaches the photon-noise floor", after <= floor * 1.10);
        Check("reduction does not beat the photon-noise floor", after >= floor * 0.90);

        var rows = new List<string> { "stage,rms_fraction" };
        rows.Add(Row("raw", before));
        rows.Add(Row("no_flat", withoutFlat));
        rows.Add(Row("reduced", after));
        rows.Add(Row("photon_floor", floor));
        File.WriteAllLines(Path.Combine(outDir, "reduction.csv"), rows);
        Console.WriteLine($"  -> {Path.Combine(outDir, "reduction.csv")}");
    }

    // ---------------------------------------------------------------- cross-validation

    /// <summary>
    /// Writes what compare_pyxel.py needs to put this pipeline's models side by side with ESA's.
    ///
    /// Arrays rather than summary statistics, so that the comparison computes the SAME statistic on
    /// both sides with the same code. A table of numbers we computed ourselves, compared against a
    /// table Pyxel computed, would be comparing two estimators as much as two models.
    /// </summary>
    static void ExportForCrossValidation()
    {
        Header("7. Export for cross-validation against Pyxel");
        var spec = VisualTelescopeCatalog.Rc20;
        const int N = 1 << 20;

        // The photo-response multipliers themselves, at the sensor's own published PRNU, so the
        // comparison sees the distribution rather than a parameter we claim it has.
        double sigma = spec.PhotoResponseNonUniformity;
        ushort[] map = SensorNonUniformity.BuildPhotoResponseMap(Pcg32.MixSeed(SensorSerialSeed), N, sigma);
        using (var w = new BinaryWriter(File.Create(Path.Combine(outDir, "exo_prnu_multiplier.bin"))))
        {
            w.Write(N);
            for (int i = 0; i < N; i++) w.Write(SensorNonUniformity.PhotoResponse(map, i));
        }

        // The linearity curve, in electrons, over the full well.
        var lin = new List<string> { "electrons,measured_electrons,recovered_electrons" };
        double d = VisualTelescopeCatalog.Fors2Vlt.LinearityDeviationAtFullWell;
        double fullWell = VisualTelescopeCatalog.Fors2Vlt.FullWellElectrons;
        for (int i = 0; i <= 100; i++)
        {
            double q = i / 100.0 * fullWell;
            double m = DetectorLinearity.Measured(q, fullWell, d);
            lin.Add(string.Format(CultureInfo.InvariantCulture, "{0:G8},{1:G8},{2:G8}",
                                  q, m, DetectorLinearity.Correct(m, fullWell, d)));
        }
        File.WriteAllLines(Path.Combine(outDir, "linearity.csv"), lin);

        // The converter, over a range that crosses both ends of its own scale.
        var adc = new List<string> { "electrons,adu" };
        double k = spec.ElectronsPerAduAtUnityGain;
        double bias = spec.EffectiveBiasLevelAdu(k);
        double adcMax = Math.Pow(2.0, spec.AdcBits) - 1.0;
        for (int i = 0; i <= 200; i++)
        {
            double q = -500.0 + i * (spec.FullWellElectrons + 1000.0) / 200.0;
            double adu = Math.Floor(q / k + bias);
            if (adu < 0.0) adu = 0.0; else if (adu > adcMax) adu = adcMax;
            adc.Add(string.Format(CultureInfo.InvariantCulture, "{0:G8},{1:G8}", q, adu));
        }
        File.WriteAllLines(Path.Combine(outDir, "adc.csv"), adc);

        // The parameters the comparison has to drive Pyxel with, so that the two sides are given
        // the same instrument rather than two similar ones.
        var meta = new List<string> { "key,value" };
        void Meta(string key, double value) => meta.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1:G10}", key, value));
        Meta("prnu_sigma", sigma);
        Meta("quantum_efficiency", spec.QuantumEfficiency);
        Meta("full_well_electrons", spec.FullWellElectrons);
        Meta("read_noise_electrons", spec.ReadNoiseElectrons);
        Meta("electrons_per_adu", k);
        Meta("bias_level_adu", bias);
        Meta("adc_bits", spec.AdcBits);
        Meta("fors2_linearity_deviation", d);
        Meta("fors2_full_well_electrons", fullWell);
        File.WriteAllLines(Path.Combine(outDir, "meta.csv"), meta);

        Console.WriteLine($"  -> exo_prnu_multiplier.bin ({N} multipliers), linearity.csv, adc.csv, meta.csv");
    }

    // ---------------------------------------------------------------- the chain

    /// <summary>
    /// The pipeline's detector chain, in the pipeline's order, on the pipeline's Core.
    ///
    /// Written out rather than called because RunDetectorChain is in the Unity layer. The stages
    /// omitted here are the localised ones (cosmic rays, hot and dead pixels, blooming, transfer
    /// smear), for the same reason tools/photometry-roundtrip omits them: each exists to damage
    /// individual pixels, and a statistical measurement over a whole frame would be measuring the
    /// damage instead of the calibration.
    /// </summary>
    static float[] RunChain(VisualTelescopeSpec spec, double[] flat, ushort[] offsets,
                            double sceneElectrons, double darkElectrons, int w, int h, int seed)
    {
        int n = w * h;
        var raw = new float[n];
        var rng = new Pcg32(Pcg32.MixSeed(seed), Pcg32.StreamShotNoise);
        var rngRead = new Pcg32(Pcg32.MixSeed(seed), Pcg32.StreamReadNoise);

        double k = spec.ElectronsPerAduAtUnityGain;
        double bias = spec.EffectiveBiasLevelAdu(k);
        double adcMax = Math.Pow(2.0, spec.AdcBits) - 1.0;
        double d = spec.LinearityDeviationAtFullWell;

        for (int i = 0; i < n; i++)
        {
            // The flat multiplies the light and not the dark, which is the whole distinction.
            double collected = sceneElectrons * (flat != null ? flat[i] : 1.0);
            double mean = Math.Max(0.0, collected + darkElectrons);
            double q = Poisson(rng, mean);

            q = DetectorLinearity.Measured(q, spec.FullWellElectrons, d);
            q += NoiseSampler.Gaussian(rngRead, spec.ReadNoiseElectrons)
               + SensorNonUniformity.OffsetElectrons(offsets, i);

            double adu = Math.Floor(q / k + bias);
            if (adu < 0.0) adu = 0.0; else if (adu > adcMax) adu = adcMax;
            raw[i] = (float)adu;
        }
        return raw;
    }

    static float[] SimulateBias(VisualTelescopeSpec spec, ushort[] offsets, int w, int h, int seed)
        => RunChain(spec, null, offsets, 0.0, 0.0, w, h, seed);

    /// <summary>
    /// A Poisson deviate. Knuth's product method below 30, where it is exact and cheap, and a
    /// Gaussian of matching moments above it, where the two are indistinguishable to better than a
    /// part in a thousand and Knuth's loop is not. The pipeline's own sampler makes the same split;
    /// the boundary is restated here rather than shared because SamplePoisson lives in the Unity
    /// layer.
    /// </summary>
    static double Poisson(Random rng, double mean)
    {
        if (mean <= 0.0) return 0.0;
        if (mean < 30.0)
        {
            double limit = Math.Exp(-mean), p = 1.0;
            int count = 0;
            do { count++; p *= rng.NextDouble(); } while (p > limit);
            return count - 1;
        }
        return Math.Max(0.0, mean + NoiseSampler.Gaussian(rng, Math.Sqrt(mean)));
    }

    // ---------------------------------------------------------------- statistics

    static double Mean(ushort[] map, int n)
    {
        double s = 0.0;
        for (int i = 0; i < n; i++) s += Float16.ToDouble(map[i]);
        return s / n;
    }

    static double Sigma(ushort[] map, int n)
    {
        double mean = Mean(map, n), s = 0.0;
        for (int i = 0; i < n; i++) { double d = Float16.ToDouble(map[i]) - mean; s += d * d; }
        return Math.Sqrt(s / n);
    }

    static double Sigma(float[] a)
    {
        double mean = 0.0;
        for (int i = 0; i < a.Length; i++) mean += a[i];
        mean /= a.Length;
        double s = 0.0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - mean; s += d * d; }
        return Math.Sqrt(s / a.Length);
    }

    static double SigmaOfDifference(float[] a, float[] b)
    {
        var diff = new float[a.Length];
        for (int i = 0; i < a.Length; i++) diff[i] = a[i] - b[i];
        return Sigma(diff);
    }

    /// <summary>The frame differenced against itself displaced by (dx, dy), over the overlap only.</summary>
    static double SigmaOfShiftedDifference(float[] a, int w, int h, int dx, int dy)
    {
        var diff = new float[(w - dx) * (h - dy)];
        int j = 0;
        for (int y = 0; y < h - dy; y++)
            for (int x = 0; x < w - dx; x++)
                diff[j++] = a[y * w + x] - a[(y + dy) * w + (x + dx)];
        return Sigma(diff);
    }

    static double RelativeSigma(double[] a)
    {
        double mean = 0.0;
        for (int i = 0; i < a.Length; i++) mean += a[i];
        mean /= a.Length;
        double s = 0.0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - mean; s += d * d; }
        return Math.Sqrt(s / a.Length) / Math.Abs(mean);
    }

    static double RelativeSigma(float[] a, float[] pedestal)
    {
        var v = new double[a.Length];
        for (int i = 0; i < a.Length; i++) v[i] = a[i] - pedestal[i];
        return RelativeSigma(v);
    }

    static float[] AverageOf(int count, Func<int, float[]> make)
    {
        float[] first = make(0);
        var sum = new double[first.Length];
        for (int i = 0; i < first.Length; i++) sum[i] = first[i];
        for (int s = 1; s < count; s++)
        {
            float[] f = make(s);
            for (int i = 0; i < f.Length; i++) sum[i] += f[i];
        }
        var avg = new float[first.Length];
        for (int i = 0; i < avg.Length; i++) avg[i] = (float)(sum[i] / count);
        return avg;
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
        bool ok = Math.Abs(got - expected) <= tolerance;
        if (!ok)
        {
            failures++;
            Console.WriteLine($"    FAIL {what}: got {got:G8}, expected {expected:G8} +/- {tolerance:G4}");
        }
    }

    static void Check(string what, bool ok)
    {
        if (!ok) { failures++; Console.WriteLine($"    FAIL {what}"); }
    }

    static string Row(string stage, double value)
        => string.Format(CultureInfo.InvariantCulture, "{0},{1:G8}", stage, value);
}
