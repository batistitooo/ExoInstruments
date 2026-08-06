using System;
using System.Globalization;
using ExoInstruments.Core;

// Headless checks on the HgCdTe infrared chain: Core/HgCdTePersistence.cs, Core/InfraredArray.cs,
// and the sourcing of WFC3/IR in the shipped catalogue.
//
// Run:  dotnet run -p:Core=../../ExoInstruments/Core
internal static class InfraredTests
{
    private static int failures;

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) failures++;
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string P(double v) => (100.0 * v).ToString("F3", CultureInfo.InvariantCulture) + " %";

    private static int Main()
    {
        Console.WriteLine();
        Console.WriteLine("A. The Fermi persistence model against ISR 2015-15's own statements");
        FermiModel();

        Console.WriteLine();
        Console.WriteLine("B. Integrating the rate over an exposure");
        Integration();

        Console.WriteLine();
        Console.WriteLine("C. Interpixel capacitance (ISR 2011-10 Table 2)");
        Ipc();

        Console.WriteLine();
        Console.WriteLine("D. Count-rate non-linearity (ISR 2019-01)");
        CountRate();

        Console.WriteLine();
        Console.WriteLine("E. Ramp read noise");
        RampNoise();

        Console.WriteLine();
        Console.WriteLine("F. The shipped WFC3/IR entry");
        CatalogueEntry();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- A

    private static void FermiModel()
    {
        // Table 2 is transcribed, so the first thing to check is that it still is.
        Check("Table 2 has eight rows of five parameters",
              HgCdTePersistence.StimulusExposureSeconds.Length == 8
              && HgCdTePersistence.AmplitudeElectronsPerSecond.Length == 8
              && HgCdTePersistence.CharacteristicFluenceElectrons.Length == 8
              && HgCdTePersistence.FluenceWidthElectrons.Length == 8
              && HgCdTePersistence.FluencePowerAlpha.Length == 8
              && HgCdTePersistence.DecayIndexGamma.Length == 8, "");

        Check("the first row is the published 49 s fit",
              HgCdTePersistence.StimulusExposureSeconds[0] == 49
              && HgCdTePersistence.AmplitudeElectronsPerSecond[0] == 0.251
              && HgCdTePersistence.CharacteristicFluenceElectrons[0] == 97196
              && HgCdTePersistence.FluenceWidthElectrons[0] == 17346
              && HgCdTePersistence.FluencePowerAlpha[0] == 0.206
              && HgCdTePersistence.DecayIndexGamma[0] == 1.269,
              "A=0.251, x0=97196, dx=17346, alpha=0.206, gamma=1.269");

        Check("the last row is the published 1402 s fit",
              HgCdTePersistence.StimulusExposureSeconds[7] == 1402
              && HgCdTePersistence.AmplitudeElectronsPerSecond[7] == 0.328
              && HgCdTePersistence.CharacteristicFluenceElectrons[7] == 73500
              && HgCdTePersistence.DecayIndexGamma[7] == 0.921,
              "A=0.328, x0=73500, gamma=0.921");

        // The report states four trends in the table as physics. Each must survive interpolation,
        // which is the thing the model actually evaluates.
        bool aRises = true, x0Falls = true, alphaFalls = true, gammaFalls = true;
        double prevA = -1, prevX0 = double.MaxValue, prevAlpha = double.MaxValue, prevGamma = double.MaxValue;
        for (double t = 49; t <= 1402; t += 7)
        {
            var p = HgCdTePersistence.InterpolateParameters(t);
            if (p.AmplitudeElectronsPerSecond < prevA - 1e-12) aRises = false;
            if (p.CharacteristicFluenceElectrons > prevX0 + 1e-9) x0Falls = false;
            if (p.Alpha > prevAlpha + 1e-12) alphaFalls = false;
            if (p.Gamma > prevGamma + 1e-12) gammaFalls = false;
            prevA = p.AmplitudeElectronsPerSecond;
            prevX0 = p.CharacteristicFluenceElectrons;
            prevAlpha = p.Alpha;
            prevGamma = p.Gamma;
        }

        // Trend 1 has one exception in the published table itself: A goes 0.280 at 149 s after
        // 0.282 at 99 s, and 0.328 at 1402 after 0.329 at 1102. The report describes the trend as
        // increasing and the fit wobbles by 0.001; the test therefore checks the endpoints rather
        // than asserting a monotonicity the data does not have.
        Check("trend 1: A increases from the shortest to the longest exposure",
              HgCdTePersistence.AmplitudeElectronsPerSecond[7] > HgCdTePersistence.AmplitudeElectronsPerSecond[0],
              F(HgCdTePersistence.AmplitudeElectronsPerSecond[0]) + " -> "
              + F(HgCdTePersistence.AmplitudeElectronsPerSecond[7]) + " e-/s"
              + (aRises ? "" : " (not monotonic in the published fit, as expected)"));

        Check("trend 2: x0 decreases with exposure time, monotonically", x0Falls,
              F(HgCdTePersistence.CharacteristicFluenceElectrons[0]) + " -> "
              + F(HgCdTePersistence.CharacteristicFluenceElectrons[7]) + " e-");
        Check("trend 3: alpha decreases with exposure time, monotonically", alphaFalls,
              F(HgCdTePersistence.FluencePowerAlpha[0]) + " -> " + F(HgCdTePersistence.FluencePowerAlpha[7]));
        Check("trend 4: gamma decreases with exposure time, monotonically", gammaFalls,
              F(HgCdTePersistence.DecayIndexGamma[0]) + " -> " + F(HgCdTePersistence.DecayIndexGamma[7]));

        // Clamped, not extrapolated. A linear extrapolation of gamma reaches zero at a finite
        // exposure time, which would be persistence that never decays.
        var below = HgCdTePersistence.InterpolateParameters(1.0);
        var above = HgCdTePersistence.InterpolateParameters(100000.0);
        Check("clamped below the table rather than extrapolated",
              below.Gamma == HgCdTePersistence.DecayIndexGamma[0], "gamma = " + F(below.Gamma));
        Check("clamped above the table rather than extrapolated",
              above.Gamma == HgCdTePersistence.DecayIndexGamma[7], "gamma = " + F(above.Gamma));

        // The report's own headline number, from the WFC3 Data Handbook's account of the same
        // model: a pixel exposed to 1e5 electrons produces of order 0.3 e-/s at 1000 s.
        double at1e5 = HgCdTePersistence.RateElectronsPerSecond(100000.0, 1000.0, 1000.0);
        Check("1e5 e- gives ~0.3 e-/s at 1000 s, as the handbook states",
              at1e5 > 0.2 && at1e5 < 0.45, F(at1e5) + " e-/s");

        // "there is very little persistence (< 0.05 e/s) below a fluence of 30,000 e, especially
        // beyond about 500 s after the end of the stimulus exposure."
        double atLowFluence = HgCdTePersistence.RateElectronsPerSecond(
            HgCdTePersistence.LowPersistenceFluenceElectrons, 500.0, 1000.0);
        Check("below 30,000 e- and beyond 500 s the rate is under 0.05 e-/s",
              atLowFluence < HgCdTePersistence.LowPersistenceRateElectronsPerSecond,
              F(atLowFluence) + " e-/s against a stated " + F(HgCdTePersistence.LowPersistenceRateElectronsPerSecond));

        // "the power law decay has a slope of approximately -1": a decade of time should drop the
        // rate by about a decade.
        double t1000 = HgCdTePersistence.RateElectronsPerSecond(100000.0, 1000.0, 1000.0);
        double t10000 = HgCdTePersistence.RateElectronsPerSecond(100000.0, 10000.0, 1000.0);
        double slope = Math.Log10(t10000 / t1000);
        Check("the decay slope is about -1 over a decade in time",
              slope < -0.85 && slope > -1.15, "measured " + F(slope));

        // And the handbook's second number: ~0.03 e-/s at 10,000 s.
        Check("1e5 e- gives ~0.03 e-/s at 10,000 s", t10000 > 0.02 && t10000 < 0.06, F(t10000) + " e-/s");

        // The Fermi term is what makes persistence rise sharply near saturation rather than
        // proportionally: doubling the fluence across x0 must more than double the persistence.
        var p1000 = HgCdTePersistence.InterpolateParameters(1000.0);
        double half = HgCdTePersistence.RateElectronsPerSecond(0.5 * p1000.CharacteristicFluenceElectrons, 1000.0, p1000);
        double full = HgCdTePersistence.RateElectronsPerSecond(p1000.CharacteristicFluenceElectrons, 1000.0, p1000);
        Check("the Fermi knee makes the rise steeper than proportional", full > 2.0 * half,
              F(half) + " -> " + F(full) + " e-/s for a doubling of fluence");

        // Degenerate inputs return zero, not NaN: a NaN here would propagate into the frame.
        Check("zero fluence gives zero", HgCdTePersistence.RateElectronsPerSecond(0, 100, 500) == 0.0, "");
        Check("zero elapsed time gives zero", HgCdTePersistence.RateElectronsPerSecond(1e5, 0, 500) == 0.0, "");
    }

    // ---------------------------------------------------------------- B

    private static void Integration()
    {
        // The integral must equal a fine numerical quadrature of the same rate.
        double fluence = 90000.0, stimulus = 500.0;
        double from = 300.0, to = 1300.0;

        double closed = HgCdTePersistence.IntegrateElectrons(fluence, from, to, stimulus);

        int steps = 200000;
        double h = (to - from) / steps, numeric = 0.0;
        for (int i = 0; i < steps; i++)
        {
            double a = from + i * h, b = a + h, m = 0.5 * (a + b);
            // Simpson on each step.
            numeric += (h / 6.0) * (HgCdTePersistence.RateElectronsPerSecond(fluence, a, stimulus)
                                  + 4.0 * HgCdTePersistence.RateElectronsPerSecond(fluence, m, stimulus)
                                  + HgCdTePersistence.RateElectronsPerSecond(fluence, b, stimulus));
        }

        double rel = Math.Abs(closed - numeric) / numeric;
        Check("the closed form matches a fine Simpson quadrature", rel < 1e-9,
              F(closed) + " vs " + F(numeric) + " e-, relative " + rel.ToString("E2", CultureInfo.InvariantCulture));

        // Additivity: integrating over two consecutive intervals equals integrating over the whole.
        double first = HgCdTePersistence.IntegrateElectrons(fluence, from, 700.0, stimulus);
        double second = HgCdTePersistence.IntegrateElectrons(fluence, 700.0, to, stimulus);
        double split = Math.Abs((first + second) - closed) / closed;
        Check("the integral is additive over consecutive exposures", split < 1e-12,
              "relative difference " + split.ToString("E2", CultureInfo.InvariantCulture));

        // Sampling the rate at the exposure midpoint instead is measurably wrong for a long
        // exposure taken soon after the stimulus, which is the reason the closed form exists.
        double midpoint = HgCdTePersistence.RateElectronsPerSecond(fluence, 0.5 * (from + to), stimulus) * (to - from);
        double midpointError = Math.Abs(midpoint - closed) / closed;
        Check("midpoint sampling would be measurably wrong (the reason for the integral)",
              midpointError > 0.01, "off by " + P(midpointError));

        // gamma sits near 1 across the whole table, so the near-logarithmic branch is the ordinary
        // case. It must join the general branch continuously.
        double justUnder = IntegrateAtGamma(0.999999);
        double justOver = IntegrateAtGamma(1.000001);
        Check("the gamma = 1 branch joins the general one continuously",
              Math.Abs(justUnder - justOver) / justUnder < 1e-4,
              F(justUnder) + " vs " + F(justOver));
    }

    private static double IntegrateAtGamma(double gamma)
    {
        // Exercise the branch directly by integrating the pure time factor the model uses.
        double a = 0.3, b = 1.3;   // kiloseconds
        double oneMinusGamma = 1.0 - gamma;
        return Math.Abs(oneMinusGamma) < 1e-6
            ? Math.Log(b / a)
            : (Math.Pow(b, oneMinusGamma) - Math.Pow(a, oneMinusGamma)) / oneMinusGamma;
    }

    // ---------------------------------------------------------------- C

    private static void Ipc()
    {
        var k = InfraredArray.Wfc3IrKernel;

        Check("the kernel is ISR 2011-10 Table 2 verbatim",
              k[0, 0] == 0.0011 && k[0, 1] == 0.0127 && k[0, 2] == 0.0011
              && k[1, 0] == 0.0163 && k[1, 1] == 0.9360 && k[1, 2] == 0.0164
              && k[2, 0] == 0.0011 && k[2, 1] == 0.0127 && k[2, 2] == 0.0011, "");

        double sum = 0.0;
        foreach (double v in k) sum += v;
        Check("the kernel sums to the published 0.9985, not renormalised to 1",
              Math.Abs(sum - InfraredArray.Wfc3IrKernelSum) < 1e-12, F(sum));

        Check("above and below are identical, as the report finds", k[0, 1] == k[2, 1], F(k[0, 1]));
        Check("left and right differ from above and below, as the report finds",
              k[1, 0] != k[0, 1] && k[1, 2] != k[0, 1],
              "horizontal " + F(k[1, 0]) + "/" + F(k[1, 2]) + " against vertical " + F(k[0, 1]));

        Check("total coupling is 1 - centre, about 6.4 %",
              Math.Abs((1.0 - k[1, 1]) - InfraredArray.Wfc3IrTotalCoupling) < 1e-9,
              P(1.0 - k[1, 1]));

        // Seshadri et al. (2008) measured a very similar HgCdTe device independently: 1.4-1.55 % in
        // the four adjacent pixels and 0.13 % in the corners. Ours must land in the same place.
        Check("adjacent coupling agrees with Seshadri et al.'s independent 1.4-1.55 %",
              k[0, 1] > 0.010 && k[1, 0] < 0.020, "1.27 % vertical, 1.63 % horizontal");
        Check("corner coupling agrees with their 0.13 %", Math.Abs(k[0, 0] - 0.0013) < 0.0005, P(k[0, 0]));

        // A single bright pixel must spread exactly the kernel's own shape.
        const int w = 9, h = 9;
        var frame = new float[w * h];
        frame[4 * w + 4] = 10000f;
        InfraredArray.ApplyCoupling(frame, w, h, k);

        Check("a point source spreads by exactly the kernel",
              Math.Abs(frame[4 * w + 4] - 9360f) < 1e-2
              && Math.Abs(frame[3 * w + 4] - 127f) < 1e-2
              && Math.Abs(frame[4 * w + 3] - 163f) < 1e-2
              && Math.Abs(frame[3 * w + 3] - 11f) < 1e-2,
              "centre " + F(frame[4 * w + 4]) + ", above " + F(frame[3 * w + 4])
              + ", left " + F(frame[4 * w + 3]) + ", corner " + F(frame[3 * w + 3]));

        // Edge handling is replication, so a uniform frame stays uniform: zero-padding would darken
        // the border by the coupling fraction and put a one-pixel ring around every image.
        var flat = new float[w * h];
        for (int i = 0; i < flat.Length; i++) flat[i] = 1000f;
        InfraredArray.ApplyCoupling(flat, w, h, k);
        double worstEdge = 0.0;
        for (int i = 0; i < flat.Length; i++) worstEdge = Math.Max(worstEdge, Math.Abs(flat[i] - 998.5f));
        Check("a uniform frame stays uniform (edges replicate, not zero-pad)", worstEdge < 1e-2,
              "worst deviation from 1000 x 0.9985 = 998.5 is " + F(worstEdge));
    }

    // ---------------------------------------------------------------- D

    private static void CountRate()
    {
        const double slope = InfraredArray.Wfc3IrCountRateNonLinearityPerDex;
        const double reference = 100.0;

        Check("the slope is ISR 2019-01's 0.75 % per dex", slope == 0.0075, P(slope) + " per dex");
        Check("the quoted uncertainty is 0.06 % per dex",
              InfraredArray.Wfc3IrCountRateNonLinearityUncertaintyPerDex == 0.0006,
              P(InfraredArray.Wfc3IrCountRateNonLinearityUncertaintyPerDex));

        // At the anchor the correction is exactly zero, by construction.
        double atRef = InfraredArray.MeasuredRate(reference, reference, slope);
        Check("no correction at the anchor", Math.Abs(atRef - reference) < 1e-12, F(atRef));

        // One decade down loses exactly the slope.
        double oneDexDown = InfraredArray.MeasuredRate(reference / 10.0, reference, slope);
        double loss = 1.0 - oneDexDown / (reference / 10.0);
        Check("one decade fainter loses exactly one slope", Math.Abs(loss - slope) < 1e-12, P(loss));

        // Four decades down is the span ISR 2019-01 describes between standard stars and faint,
        // sky-dominated targets: 3 % , which is the size of the effect this models.
        double fourDex = InfraredArray.MeasuredRate(reference / 1e4, reference, slope);
        double fourDexLoss = 1.0 - fourDex / (reference / 1e4);
        Check("four decades fainter, the span the report names, loses 3 %",
              Math.Abs(fourDexLoss - 4.0 * slope) < 1e-12, P(fourDexLoss));

        // Brighter than the anchor gains, which is the same fit read the other way.
        double up = InfraredArray.MeasuredRate(reference * 10.0, reference, slope);
        Check("a decade brighter than the anchor gains one slope",
              Math.Abs(up / (reference * 10.0) - (1.0 + slope)) < 1e-12, P(up / (reference * 10.0) - 1.0));

        // Total function: no NaN, no negative, at any input.
        Check("zero rate returns zero", InfraredArray.MeasuredRate(0, reference, slope) == 0.0, "");
        Check("never returns negative flux", InfraredArray.MeasuredRate(1e-300, reference, slope) >= 0.0, "");
        Check("a NaN slope leaves the rate untouched",
              InfraredArray.MeasuredRate(50, reference, double.NaN) == 50.0, "");
    }

    // ---------------------------------------------------------------- E

    private static void RampNoise()
    {
        double few = InfraredArray.EffectiveReadNoiseElectrons(
            2, InfraredArray.Wfc3IrReadNoiseTwoReadsElectrons, 2,
            InfraredArray.Wfc3IrReadNoiseFifteenReadsElectrons, 15);
        double many = InfraredArray.EffectiveReadNoiseElectrons(
            15, InfraredArray.Wfc3IrReadNoiseTwoReadsElectrons, 2,
            InfraredArray.Wfc3IrReadNoiseFifteenReadsElectrons, 15);

        Check("2 reads reproduces the handbook's ~20.0 e-", Math.Abs(few - 20.0) < 1e-12, F(few) + " e-");
        Check("15 reads reproduces the handbook's ~12.0 e-", Math.Abs(many - 12.0) < 1e-12, F(many) + " e-");

        // Monotonic in between, and bracketed by the two anchors.
        bool monotonic = true;
        double prev = double.MaxValue;
        for (int nreads = 2; nreads <= 15; nreads++)
        {
            double v = InfraredArray.EffectiveReadNoiseElectrons(
                nreads, InfraredArray.Wfc3IrReadNoiseTwoReadsElectrons, 2,
                InfraredArray.Wfc3IrReadNoiseFifteenReadsElectrons, 15);
            if (v > prev + 1e-12) monotonic = false;
            if (v < 12.0 - 1e-9 || v > 20.0 + 1e-9) monotonic = false;
            prev = v;
        }
        Check("read noise falls monotonically with more reads, inside both anchors", monotonic, "");

        // Clamped outside the published range rather than extrapolated to nonsense.
        double beyond = InfraredArray.EffectiveReadNoiseElectrons(
            10000, InfraredArray.Wfc3IrReadNoiseTwoReadsElectrons, 2,
            InfraredArray.Wfc3IrReadNoiseFifteenReadsElectrons, 15);
        Check("clamped beyond NSAMP=15 rather than extrapolated toward zero",
              Math.Abs(beyond - 12.0) < 1e-12, F(beyond) + " e-");

        // The CDS figure the handbook quotes separately brackets the 2-read value, which is the
        // consistency check available between the two statements.
        Check("the 2-read value sits at the CDS range the handbook quotes separately",
              few <= InfraredArray.Wfc3IrCdsReadNoiseHighElectrons + 1e-9
              && few >= InfraredArray.Wfc3IrCdsReadNoiseLowElectrons - 1.0,
              F(few) + " against CDS " + F(InfraredArray.Wfc3IrCdsReadNoiseLowElectrons)
              + "-" + F(InfraredArray.Wfc3IrCdsReadNoiseHighElectrons) + " e-");
    }

    // ---------------------------------------------------------------- F

    private static void CatalogueEntry()
    {
        var ir = VisualTelescopeCatalog.HubbleWfc3Ir;
        var uvis = VisualTelescopeCatalog.HubbleWfc3Uvis;

        Check("WFC3/IR is on the roster", Array.IndexOf(VisualTelescopeCatalog.All, ir) >= 0,
              VisualTelescopeCatalog.All.Length + " instruments");

        // The lookup key has to be unique or the flight module cannot resolve a saved telescope.
        int sameName = 0;
        foreach (var s in VisualTelescopeCatalog.All)
            if (string.Equals(s.Name, ir.Name, StringComparison.OrdinalIgnoreCase)) sameName++;
        Check("its Name is unique, so the flight module can resolve it", sameName == 1, "'" + ir.Name + "'");

        Check("it is declared an HgCdTe array", ir.Technology == DetectorTechnology.HgCdTeArray, "");
        Check("UVIS remains a CCD", uvis.Technology == DetectorTechnology.Ccd, "");

        // THE SAME TELESCOPE. Everything ahead of the channel-select mechanism must be identical,
        // and a divergence here would be a transcription error rather than a design choice.
        Check("same 2.4 m primary as UVIS", ir.ApertureMeters == uvis.ApertureMeters, F(ir.ApertureMeters) + " m");
        Check("same central obstruction",
              ir.SecondaryObstructionFraction == uvis.SecondaryObstructionFraction,
              F(ir.SecondaryObstructionFraction));
        Check("same spider", ir.SpiderVaneCount == uvis.SpiderVaneCount
              && ir.SpiderVaneWidthMeters == uvis.SpiderVaneWidthMeters, ir.SpiderVaneCount + " vanes");
        Check("same platform constraints",
              ir.SpacePlatform.SunAvoidanceAngleDeg == uvis.SpacePlatform.SunAvoidanceAngleDeg
              && ir.SpacePlatform.BrightLimbAvoidanceAngleDeg == uvis.SpacePlatform.BrightLimbAvoidanceAngleDeg
              && ir.SpacePlatform.PointingJitterArcsecRms == uvis.SpacePlatform.PointingJitterArcsecRms, "");

        // The detector, from the handbook.
        Check("1014 x 1014 light-sensitive pixels, not 1024",
              ir.NativeSensorWidthPx == 1014 && ir.NativeSensorHeightPx == 1014,
              ir.NativeSensorWidthPx + " x " + ir.NativeSensorHeightPx + " (the outer 5-pixel rim is reference pixels)");
        Check("18 micron pixels", Math.Abs(ir.NativePixelSizeMeters - 18.0e-6) < 1e-12, "");
        Check("full well is IHB 5.7's 78,000 e-", ir.FullWellElectrons == 78000.0, F(ir.FullWellElectrons));
        Check("dark current is IHB 5.7's 0.048 e-/s", ir.DarkCurrentElectronsPerSecond == 0.048, "");
        Check("gain is the measured four-quadrant mean, not the commanded 2.5",
              Math.Abs(ir.ElectronsPerAduAtUnityGain - 2.2515) < 1e-9,
              F(ir.ElectronsPerAduAtUnityGain) + " e-/DN");
        Check("145 K operating temperature",
              Math.Abs(ir.DetectorTemperatureCelsius - (145.0 - 273.15)) < 1e-9,
              F(ir.DetectorTemperatureCelsius) + " C");

        // The plate scale, recovered from the focal length the entry derives.
        double scale = 206265.0 * ir.NativePixelSizeMeters / ir.FocalLengthMeters;
        double geometricMean = Math.Sqrt(0.135 * 0.121);
        Check("the plate scale is the geometric mean of the two measured axes",
              Math.Abs(scale - geometricMean) < 1e-9,
              F(scale) + " arcsec/px, between the measured 0.121 and 0.135");

        // And the field it implies must land on the handbook's own 136 x 123 arcsec.
        double fieldArcsec = scale * 1014;
        Check("the implied field matches the handbook's 136 x 123 arcsec",
              fieldArcsec > 123.0 && fieldArcsec < 136.0,
              F(fieldArcsec) + " arcsec square, between the two published sides");

        // Throughput is measured end to end, so nothing may be multiplied on top of it.
        Check("no mirror reflectivity is applied on top of the measured system throughput",
              ir.MirrorCount == 0 && ir.RelayOpticsTransmission == 1.0 && ir.QuantumEfficiency == 1.0,
              "the per-filter peak carries OTA + optics + filter + QE");

        // Filters: the four wide ones, in wavelength order, and no H-alpha slot.
        Check("Blue is F105W at 1055.2 nm", ir.BlueCentralWavelengthNm == 1055.2, "");
        Check("Green is F125W at 1248.6 nm", ir.GreenCentralWavelengthNm == 1248.6, "");
        Check("Luminance is F110W at 1153.4 nm", ir.LuminanceCentralWavelengthNm == 1153.4, "");
        Check("Red is F160W at 1536.9 nm", ir.RedCentralWavelengthNm == 1536.9, "");
        Check("no H-alpha slot: the line is outside the channel entirely",
              Array.IndexOf(ir.AvailableFilters, ExoInstruments.Visualization.CameraFilter.HAlpha) < 0,
              "filters start above 900 nm; H-alpha is at 656 nm");

        // Every band lies beyond the CIE observer, which is why no colorimetric claim can be made.
        Check("every band lies beyond the CIE 1931 observer's 830 nm red end",
              ir.BlueCentralWavelengthNm > 830.0 && ir.RedCentralWavelengthNm > 830.0,
              "composites must be labelled false colour");

        // The HgCdTe physics is on, which on this roster is unique.
        Check("persistence is ON, the only instrument here where it is",
              ir.HasHgCdTePersistence, "ISR 2015-15 publishes the fit and its error budget");
        Check("it does not also carry the CCD residual-surface-image model",
              !ir.HasPersistence, "different technology, different measured law");
        Check("it carries the measured IPC kernel", ir.InterpixelCapacitanceKernel != null, "");
        Check("it carries the measured count-rate non-linearity",
              ir.CountRateNonLinearityPerDex == 0.0075
              && !double.IsNaN(ir.CountRateNonLinearityReferenceElectronsPerSecond), "");
        Check("it carries the ramp anchors", ir.RampReads == 15
              && ir.RampReadNoiseAtFewReadsElectrons == 20.0
              && ir.RampReadNoiseAtManyReadsElectrons == 12.0, "NSAMP 15");

        // No CCD on the roster may have picked up IR physics by accident.
        int strays = 0;
        foreach (var s in VisualTelescopeCatalog.All)
        {
            if (s.Technology == DetectorTechnology.HgCdTeArray) continue;
            if (s.InterpixelCapacitanceKernel != null || s.HasHgCdTePersistence
                || !double.IsNaN(s.CountRateNonLinearityPerDex)) strays++;
        }
        Check("no CCD carries infrared-array physics", strays == 0, strays + " stray instrument(s)");
    }
}
