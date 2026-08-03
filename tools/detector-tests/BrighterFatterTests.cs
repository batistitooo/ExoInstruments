using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// Does the brighter-fatter model reproduce the one measurement it is published against?
///
/// Section 12 of the technical reference used to declare this effect not implemented because "its
/// real formula needs per-sensor electrostatic-vertex calibration tables with no generic published
/// values". That was not quite true. ESO measured it by spatial autocorrelation on their own
/// detectors and reported the numbers in prose: for an e2v CCD44-82 at about 90 ke-, a
/// nearest-neighbour correlation of 1.4% horizontally and 2.2% vertically, a summed correlation
/// over all neighbours of 10%, and the consequence that the summed correlation "results in over
/// estimating the gain of the system by 10%".
///
/// That is enough to close the loop, and closing it is what this harness does: take the published
/// correlations, convert them to area coefficients, apply the redistribution to a simulated flat
/// field, and measure the correlations back out. If they return, the model is the effect ESO
/// measured. If the gain bias also returns, it is the effect for the right reason.
///
/// What this does NOT establish is an amplitude for any instrument on this roster. The same paper
/// tested the MIT/LL CCID-20 that FORS2 uses and reports its autocorrelation nowhere.
/// </summary>
static class BrighterFatterTests
{
    static int failures;
    static string outDir = ".";

    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--out") outDir = args[i + 1];
        Directory.CreateDirectory(outDir);

        Console.WriteLine("Brighter-fatter against the one measurement it is published for");
        Console.WriteLine(new string('=', 78));

        SectionCoefficients();
        SectionChargeIsConserved();
        SectionCorrelationsComeBack();
        SectionGainBias();
        SectionBrighterIsFatter();
        SectionRoster();

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    static void SectionCoefficients()
    {
        Header("1. From a published correlation to an area coefficient");

        double ax = BrighterFatter.AreaCoefficient(
            BrighterFatter.Ccd4482HorizontalCorrelation, BrighterFatter.Ccd4482ReferenceSignalElectrons);
        double ay = BrighterFatter.AreaCoefficient(
            BrighterFatter.Ccd4482VerticalCorrelation, BrighterFatter.Ccd4482ReferenceSignalElectrons);

        Console.WriteLine($"   ESO measure {BrighterFatter.Ccd4482HorizontalCorrelation * 100:F1}% horizontally and " +
                          $"{BrighterFatter.Ccd4482VerticalCorrelation * 100:F1}% vertically at " +
                          $"{BrighterFatter.Ccd4482ReferenceSignalElectrons / 1000:F0} ke-");
        Console.WriteLine($"   a_x = {ax:E3} /e-, a_y = {ay:E3} /e-");
        Console.WriteLine("   published brighter-fatter coefficients sit at 1e-7 per electron; these are that order");

        Check("the horizontal coefficient is of the published order", ax > 1e-8 && ax < 1e-6);
        Check("the vertical coefficient is larger, as the structure requires", ay > ax);

        // The conversion must invert, or the model and its parameterisation disagree.
        Check("coefficient to correlation inverts",
              BrighterFatter.CorrelationAtSignal(ax, BrighterFatter.Ccd4482ReferenceSignalElectrons),
              BrighterFatter.Ccd4482HorizontalCorrelation, 1e-12);

        // And the correlation must grow with signal, which is the whole character of the effect.
        Console.WriteLine("   signal [ke-]   horizontal correlation");
        double prev = -1.0;
        foreach (double ke in new[] { 10.0, 30.0, 60.0, 90.0 })
        {
            double r = BrighterFatter.CorrelationAtSignal(ax, ke * 1000.0);
            Console.WriteLine($"   {ke,12:F0}   {r * 100,20:F3}%");
            Check($"correlation grows with signal at {ke} ke-", r > prev);
            prev = r;
        }
    }

    /// <summary>A redistribution that does not conserve charge is a gain error wearing a disguise.</summary>
    static void SectionChargeIsConserved()
    {
        Header("2. Charge is conserved");
        const int Size = 128;
        double ax = BrighterFatter.AreaCoefficient(0.014, 90000.0);
        double ay = BrighterFatter.AreaCoefficient(0.022, 90000.0);

        var rng = new Pcg32(Pcg32.MixSeed(11), Pcg32.StreamShotNoise);
        var frame = new float[Size * Size];
        for (int i = 0; i < frame.Length; i++) frame[i] = (float)(50000.0 + NoiseSampler.Gaussian(rng, 5000.0));

        double before = 0.0; foreach (float v in frame) before += v;
        BrighterFatter.Apply(frame, Size, Size, ax, ay);
        double after = 0.0; foreach (float v in frame) after += v;

        Console.WriteLine($"   {before:E8} e- before, {after:E8} after, difference {(after / before - 1.0) * 100:E2}%");
        // EXACTLY, to floating point. The symmetric-flux form subtracts from one pixel precisely
        // what it adds to the other, so there is nothing left to tolerate but the accumulation of
        // sixteen thousand single-precision stores. The first version of Apply used the textbook
        // area formulation and lost 2e-4 of the charge, which this check is what found.
        Check("charge is conserved exactly", after / before - 1.0, 0.0, 1e-6);
    }

    /// <summary>
    /// THE CLOSED LOOP. Put the published correlations in as coefficients, and measure them back
    /// out of a simulated flat.
    /// </summary>
    static void SectionCorrelationsComeBack()
    {
        Header("3. The correlations come back out");
        const int Size = 512;
        const double Signal = 90000.0;

        double ax = BrighterFatter.AreaCoefficient(BrighterFatter.Ccd4482HorizontalCorrelation, Signal);
        double ay = BrighterFatter.AreaCoefficient(BrighterFatter.Ccd4482VerticalCorrelation, Signal);

        // Two flats, differenced, exactly as ESO's own procedure does it: the difference removes
        // any fixed pattern and leaves twice the shot noise, which is what the autocorrelation is
        // computed on.
        var a = MakeFlat(Size, Signal, seed: 101, ax, ay);
        var b = MakeFlat(Size, Signal, seed: 202, ax, ay);
        var diff = new float[a.Length];
        for (int i = 0; i < a.Length; i++) diff[i] = a[i] - b[i];

        double rx = NeighbourCorrelation(diff, Size, Size, 1, 0);
        double ry = NeighbourCorrelation(diff, Size, Size, 0, 1);

        Console.WriteLine($"   put in:      horizontal {BrighterFatter.Ccd4482HorizontalCorrelation * 100:F2}%, " +
                          $"vertical {BrighterFatter.Ccd4482VerticalCorrelation * 100:F2}%");
        Console.WriteLine($"   measured out: horizontal {rx * 100:F2}%, vertical {ry * 100:F2}%");

        // 20%: the correlation of a 512x512 difference image is estimated from 262,000 pairs, whose
        // own standard error is 1/sqrt(N) = 0.2%, against a signal of 1.4%. So the tolerance is set
        // by the estimator and not by the model.
        Check("the horizontal correlation returns", rx, BrighterFatter.Ccd4482HorizontalCorrelation,
              0.20 * BrighterFatter.Ccd4482HorizontalCorrelation);
        Check("the vertical correlation returns", ry, BrighterFatter.Ccd4482VerticalCorrelation,
              0.20 * BrighterFatter.Ccd4482VerticalCorrelation);
        Check("and the anisotropy survives", ry > rx);

        // A control: with no coefficient there must be no correlation at all, or the measurement is
        // reading something else.
        var clean = MakeFlat(Size, Signal, seed: 101, 0.0, 0.0);
        var clean2 = MakeFlat(Size, Signal, seed: 202, 0.0, 0.0);
        var cleanDiff = new float[clean.Length];
        for (int i = 0; i < clean.Length; i++) cleanDiff[i] = clean[i] - clean2[i];
        double r0 = NeighbourCorrelation(cleanDiff, Size, Size, 1, 0);
        Console.WriteLine($"   control, no effect applied: {r0 * 100:F3}%");
        Check("shot noise alone is uncorrelated", Math.Abs(r0) < 0.003);
    }

    /// <summary>
    /// The consequence ESO state: a summed correlation of 10% means the photon transfer curve
    /// over-estimates the gain by 10%. Measured on a simulated pair of flats.
    /// </summary>
    static void SectionGainBias()
    {
        Header("4. The gain a photon transfer curve reports");
        const int Size = 512;
        const double TrueGain = 2.0;               // e-/ADU, whatever we choose it to be

        Console.WriteLine("   signal [ke-]   summed correlation   PTC gain / true   predicted 1+sum");
        var rows = new List<string> { "signal_e,summed_correlation,gain_ratio,predicted" };

        foreach (double ke in new[] { 20.0, 50.0, 90.0 })
        {
            double signal = ke * 1000.0;
            // Coefficients fixed at the published value; the correlation they produce grows with
            // signal, which is the effect.
            double ax = BrighterFatter.AreaCoefficient(BrighterFatter.Ccd4482HorizontalCorrelation,
                                                       BrighterFatter.Ccd4482ReferenceSignalElectrons);
            double ay = BrighterFatter.AreaCoefficient(BrighterFatter.Ccd4482VerticalCorrelation,
                                                       BrighterFatter.Ccd4482ReferenceSignalElectrons);

            var a = MakeFlat(Size, signal, seed: 301, ax, ay);
            var b = MakeFlat(Size, signal, seed: 302, ax, ay);
            var diff = new float[a.Length];
            for (int i = 0; i < a.Length; i++) diff[i] = a[i] - b[i];

            // The simple variance, halved because a difference of two frames carries twice it.
            double simpleVariance = 0.5 * Variance(diff);

            // The summed correlation over the four nearest neighbours, which is what ESO's
            // autocorrelation variance adds back.
            double summed = 2.0 * NeighbourCorrelation(diff, Size, Size, 1, 0)
                          + 2.0 * NeighbourCorrelation(diff, Size, Size, 0, 1);

            double mean = 0.0; foreach (float v in a) mean += v; mean /= a.Length;
            double ptcGain = mean / simpleVariance;               // e-/ADU if the frame were in ADU
            double ratio = ptcGain / 1.0;                          // the frame IS in electrons, so true gain = 1
            double predicted = BrighterFatter.PhotonTransferGainBias(summed);

            Console.WriteLine($"   {ke,12:F0}   {summed * 100,18:F2}%   {ratio,16:F4}   {predicted,16:F4}");
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0:G6},{1:G6},{2:G6},{3:G6}",
                                   signal, summed, ratio, predicted));

            Check($"the PTC gain bias at {ke} ke- is the summed correlation", ratio, predicted, 0.02);
        }
        File.WriteAllLines(Path.Combine(outDir, "bf_gain_bias.csv"), rows);

        // And ESO's own headline number, from their own summed correlation.
        double esoPredicted = BrighterFatter.PhotonTransferGainBias(BrighterFatter.Ccd4482SummedCorrelation);
        Console.WriteLine($"   ESO's summed 10% predicts a gain over-estimate of {(esoPredicted - 1) * 100:F0}%, " +
                          $"and they measure 10%");
        Check("the model reproduces ESO's stated 10%", esoPredicted, 1.10, 1e-12);
    }

    /// <summary>The effect's namesake, measured: a brighter source is a wider one.</summary>
    static void SectionBrighterIsFatter()
    {
        Header("5. Brighter is fatter");
        const int Size = 129;
        const double SigmaPx = 2.0;

        double ax = BrighterFatter.AreaCoefficient(BrighterFatter.Ccd4482HorizontalCorrelation,
                                                   BrighterFatter.Ccd4482ReferenceSignalElectrons);
        double ay = BrighterFatter.AreaCoefficient(BrighterFatter.Ccd4482VerticalCorrelation,
                                                   BrighterFatter.Ccd4482ReferenceSignalElectrons);

        Console.WriteLine("   peak [e-]   width x   width y   growth x   growth y   a*Q/4sigma^2");
        var rows = new List<string> { "peak_e,width_x,width_y,growth_x,growth_y,predicted" };
        double baseX = 0.0, baseY = 0.0;

        foreach (double peak in new[] { 100.0, 10000.0, 40000.0, 90000.0 })
        {
            var frame = MakeStar(Size, SigmaPx, peak);
            BrighterFatter.Apply(frame, Size, Size, ax, ay);
            SecondMoments(frame, Size, out double wx, out double wy);

            if (peak == 100.0) { baseX = wx; baseY = wy; }
            double gx = wx / baseX - 1.0, gy = wy / baseY - 1.0;
            double predicted = BrighterFatter.FractionalWidthIncrease(peak, ax, SigmaPx);

            Console.WriteLine($"   {peak,9:F0}   {wx,7:F4}   {wy,7:F4}   {gx * 100,7:F3}%   {gy * 100,7:F3}%   {predicted * 100,12:F3}%" +
                              (peak > 100.0 ? $"   (measured/predicted {gx / predicted:F2})" : ""));
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0:G6},{1:G6},{2:G6},{3:G6},{4:G6},{5:G6}",
                                   peak, wx, wy, gx, gy, predicted));

            if (peak > 100.0)
            {
                Check($"a {peak:F0} e- source is wider than a faint one", gx > 0.0 && gy > 0.0);
                Check($"and wider in y than x, as the coefficients require at {peak:F0} e-", gy > gx);
            }
        }
        File.WriteAllLines(Path.Combine(outDir, "bf_width.csv"), rows);
        // The closed form is derived rather than fitted, so it must be right rather than close.
        // It was a*Q/(2 sigma^2) until this section measured a ratio of exactly 0.50 at three
        // brightnesses, which is the two-dimensional normalisation the one-dimensional kernel
        // argument leaves out.
        double predictedAt90k = BrighterFatter.FractionalWidthIncrease(90000.0, ax, SigmaPx);
        var frame90 = MakeStar(Size, SigmaPx, 90000.0);
        BrighterFatter.Apply(frame90, Size, Size, ax, ay);
        SecondMoments(frame90, Size, out double w90, out _);
        var frameFaint = MakeStar(Size, SigmaPx, 100.0);
        BrighterFatter.Apply(frameFaint, Size, Size, ax, ay);
        SecondMoments(frameFaint, Size, out double wFaint, out _);
        double measuredAt90k = w90 / wFaint - 1.0;
        Console.WriteLine($"   closed form against measurement at 90 ke-: {measuredAt90k / predictedAt90k:F3}");
        Check("the closed form matches the measurement", measuredAt90k / predictedAt90k, 1.0, 0.10);
    }

    /// <summary>What this means for the instruments actually on the roster, which is: nothing yet.</summary>
    static void SectionRoster()
    {
        Header("6. The roster");
        Console.WriteLine("   Downing et al. tested three devices and report the autocorrelation for one.");
        Console.WriteLine();
        Console.WriteLine("   device                       on this roster   autocorrelation published");
        Console.WriteLine("   e2v CCD44-82                 no               YES (1.4% / 2.2% / 10% summed)");
        Console.WriteLine("   MIT/LL CCID-20 (FORS2)       yes              no");
        Console.WriteLine("   Sony IMX492 (ASI294MM Pro)   yes              no");
        Console.WriteLine("   ZIMPOL CCD (SPHERE)          yes              no");
        Console.WriteLine();
        Console.WriteLine("   So the mechanism is modelled and validated, and every instrument here carries");
        Console.WriteLine("   no amplitude. That is section 12's entry rewritten rather than removed.");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A Poisson flat with the effect applied, in electrons.</summary>
    static float[] MakeFlat(int size, double signal, int seed, double ax, double ay)
    {
        var frame = new float[size * size];
        var rng = new Pcg32(Pcg32.MixSeed(seed), Pcg32.StreamShotNoise);
        for (int i = 0; i < frame.Length; i++)
            frame[i] = (float)Math.Max(0.0, signal + NoiseSampler.Gaussian(rng, Math.Sqrt(signal)));
        if (ax > 0.0 || ay > 0.0) BrighterFatter.Apply(frame, size, size, ax, ay);
        return frame;
    }

    static float[] MakeStar(int size, double sigmaPx, double peak)
    {
        var frame = new float[size * size];
        double c = 0.5 * (size - 1);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double d2 = (x - c) * (x - c) + (y - c) * (y - c);
                frame[y * size + x] = (float)(peak * Math.Exp(-d2 / (2 * sigmaPx * sigmaPx)));
            }
        return frame;
    }

    /// <summary>Pearson correlation between a frame and itself displaced by (dx, dy), over the overlap.</summary>
    static double NeighbourCorrelation(float[] frame, int width, int height, int dx, int dy)
    {
        double s1 = 0, s2 = 0, s11 = 0, s22 = 0, s12 = 0; int n = 0;
        for (int y = 0; y + dy < height; y++)
            for (int x = 0; x + dx < width; x++)
            {
                double a = frame[y * width + x], b = frame[(y + dy) * width + (x + dx)];
                s1 += a; s2 += b; s11 += a * a; s22 += b * b; s12 += a * b; n++;
            }
        if (n < 2) return 0.0;
        double m1 = s1 / n, m2 = s2 / n;
        double c12 = s12 / n - m1 * m2;
        double v1 = s11 / n - m1 * m1, v2 = s22 / n - m2 * m2;
        return c12 / Math.Sqrt(Math.Max(1e-300, v1 * v2));
    }

    static double Variance(float[] frame)
    {
        double s = 0, s2 = 0;
        foreach (float v in frame) { s += v; s2 += (double)v * v; }
        double m = s / frame.Length;
        return s2 / frame.Length - m * m;
    }

    static void SecondMoments(float[] frame, int size, out double sigmaX, out double sigmaY)
    {
        double c = 0.5 * (size - 1), w = 0, sx = 0, sy = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double v = frame[y * size + x];
                if (v <= 0) continue;
                w += v; sx += v * (x - c) * (x - c); sy += v * (y - c) * (y - c);
            }
        sigmaX = Math.Sqrt(sx / w); sigmaY = Math.Sqrt(sy / w);
    }

    static void Header(string t) { Console.WriteLine(); Console.WriteLine(t); Console.WriteLine(new string('-', t.Length)); }
    static void Check(string what, double got, double expected, double tol)
    {
        if (!(Math.Abs(got - expected) <= tol)) { failures++; Console.WriteLine($"     FAIL {what}: got {got:G8}, expected {expected:G8} +/- {tol:G4}"); }
    }
    static void Check(string what, bool ok) { if (!ok) { failures++; Console.WriteLine($"     FAIL {what}"); } }
}
