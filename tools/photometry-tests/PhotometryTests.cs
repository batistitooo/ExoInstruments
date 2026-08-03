using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// Does a magnitude that goes in come back out?
///
/// Every other harness here checks a forward model against a measurement. This one checks the
/// forward model against ITS OWN INVERSE, which is the only test that can catch an error of
/// assembly: a zero point that disagrees with the electron counts, a point-spread function that
/// does not conserve flux, a gain applied twice. Each of those is invisible to a test that looks at
/// one stage, and fatal to every number the pipeline reports.
///
/// It also checks the thing a measured flux is worthless without: whether the ERROR BAR is honest.
/// That cannot be checked on one measurement. It is checked here by measuring the same star in many
/// noise realisations and comparing the scatter of the answers with the sigma predicted for one of
/// them. If the two agree the uncertainty means what it says, and if they do not, every detection
/// significance downstream is wrong by the same factor.
/// </summary>
static class PhotometryTests
{
    static int failures;
    static string outDir = ".";

    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--out") outDir = args[i + 1];
        Directory.CreateDirectory(outDir);

        Console.WriteLine("Photometry: does a magnitude that goes in come back out?");
        Console.WriteLine(new string('=', 78));

        SectionApertureAndBackground();
        SectionUncertaintyIsHonest();
        SectionZeroPointAndRoundTrip();

        // The astrometric half of the same argument, in its own file: how much light, and where.
        failures += AstrometryTests.Run(args);

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // The instrument, from the shipped catalogue.
    static readonly VisualTelescopeSpec Spec = VisualTelescopeCatalog.Rc20;
    const double FwhmPx = 4.0;
    static double SigmaPx => FwhmPx / (2.0 * Math.Sqrt(2.0 * Math.Log(2.0)));

    /// <summary>
    /// A noiseless frame first, so that what is measured is the aperture and nothing else.
    ///
    /// The number that has to come out right is the ENCLOSED ENERGY: a Gaussian of this width
    /// inside an aperture of that radius contains a known fraction of its own flux, 1 - exp(-r^2 /
    /// 2 sigma^2), and an aperture sum that does not reproduce it is summing the wrong pixels.
    /// </summary>
    static void SectionApertureAndBackground()
    {
        Header("1. The aperture, on a frame with nothing else in it");
        const int Size = 121;
        const double TrueFlux = 1.0e5, Sky = 500.0;

        Console.WriteLine("   aperture [FWHM]   enclosed   analytic   measured/analytic");
        foreach (double apFwhm in new[] { 0.5, 1.0, 1.5, 2.0, 3.0 })
        {
            double rap = apFwhm * FwhmPx;
            var frame = BuildFrame(Size, new[] { (Size / 2.0, Size / 2.0, TrueFlux) }, Sky, 0.0, null);

            var s = AperturePhotometry.Measure(frame, Size, Size, Size / 2.0, Size / 2.0,
                                               rap, 4.0 * FwhmPx, 6.0 * FwhmPx,
                                               Spec.ReadNoiseElectrons, 0.0);

            double analytic = 1.0 - Math.Exp(-rap * rap / (2.0 * SigmaPx * SigmaPx));
            double measured = s.Flux / TrueFlux;
            Console.WriteLine($"   {apFwhm,15:F1}   {measured,8:F5}   {analytic,8:F5}   {measured / analytic,17:F5}");

            // The tolerance grows as the aperture shrinks, and that is discretisation rather than
            // slack. Pixel-centre membership approximates a circle by a jagged one whose area is
            // wrong by of order the pixels within half a pixel of its edge, a fraction going as
            // 1/r; the measured departures are 4.3% at a 2-pixel radius, 0.35% at 4 and 0.05% at 6,
            // which is that law. Matching photutils' own default convention is worth more here than
            // sub-pixel area weighting would be, because it is what the comparison is against.
            double tolerance = (0.02 + 0.2 / (rap * rap)) * analytic;
            Check($"enclosed energy at {apFwhm} FWHM", measured, analytic, tolerance);
        }

        // The background must be recovered, and the centroid must land on the source.
        var f2 = BuildFrame(Size, new[] { (60.3, 59.7, TrueFlux) }, Sky, 0.0, null);
        var m = AperturePhotometry.Measure(f2, Size, Size, 60.0, 60.0, 2.0 * FwhmPx,
                                           4.0 * FwhmPx, 6.0 * FwhmPx, Spec.ReadNoiseElectrons, 0.0);
        Console.WriteLine($"   background recovered {m.Background:F2} against {Sky:F0} e-");
        Console.WriteLine($"   centroid ({m.X:F3}, {m.Y:F3}) against (60.300, 59.700)");
        Check("the background is recovered", m.Background, Sky, 1.0);
        Check("the centroid finds x", m.X, 60.3, 0.02);
        Check("the centroid finds y", m.Y, 59.7, 0.02);
    }

    /// <summary>
    /// THE CHECK THAT MATTERS. Measure the same star in many independent noise realisations; the
    /// scatter of the answers must be the sigma predicted for one of them.
    /// </summary>
    static void SectionUncertaintyIsHonest()
    {
        Header("2. Is the error bar honest?");
        const int Size = 121, Trials = 400;
        const double Sky = 500.0;

        Console.WriteLine("   flux [e-]   predicted sigma   measured scatter   ratio");
        var rows = new List<string> { "flux,predicted_sigma,measured_scatter,ratio" };

        foreach (double trueFlux in new[] { 3.0e3, 1.0e4, 3.0e4, 1.0e5, 3.0e5 })
        {
            double sum = 0.0, sum2 = 0.0, predicted = 0.0;
            for (int t = 0; t < Trials; t++)
            {
                var rng = new Pcg32(Pcg32.MixSeed(1234, t), Pcg32.StreamShotNoise);
                var frame = BuildFrame(Size, new[] { (Size / 2.0, Size / 2.0, trueFlux) },
                                       Sky, Spec.ReadNoiseElectrons, rng);
                var s = AperturePhotometry.Measure(frame, Size, Size, Size / 2.0, Size / 2.0,
                                                   2.0 * FwhmPx, 4.0 * FwhmPx, 6.0 * FwhmPx,
                                                   Spec.ReadNoiseElectrons, 0.0);
                sum += s.Flux; sum2 += s.Flux * s.Flux;
                predicted += s.FluxUncertainty;
            }
            double mean = sum / Trials;
            double scatter = Math.Sqrt(Math.Max(0.0, sum2 / Trials - mean * mean));
            predicted /= Trials;
            double ratio = scatter / predicted;

            Console.WriteLine($"   {trueFlux,9:E1}   {predicted,15:F1}   {scatter,16:F1}   {ratio,5:F3}");
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0:G6},{1:G6},{2:G6},{3:G6}",
                                   trueFlux, predicted, scatter, ratio));

            // 15%: the scatter of a scatter estimated from 400 trials is itself 1/sqrt(2*400) = 3.5%,
            // so this catches a wrong term in the CCD equation and never a run of the dice.
            Check($"the error bar matches the scatter at {trueFlux:E0} e-", ratio, 1.0, 0.15);
        }
        File.WriteAllLines(Path.Combine(outDir, "uncertainty.csv"), rows);

        // The background term has to be there. Without the last term of the CCD equation, a small
        // annulus would be as good as a large one, and it is not.
        Console.WriteLine();
        Console.WriteLine("   the same star, measured against annuli of different size:");
        foreach (double outer in new[] { 4.5, 6.0, 10.0 })
        {
            var rng = new Pcg32(Pcg32.MixSeed(99), Pcg32.StreamShotNoise);
            var frame = BuildFrame(Size, new[] { (Size / 2.0, Size / 2.0, 1.0e4) }, Sky, Spec.ReadNoiseElectrons, rng);
            var s = AperturePhotometry.Measure(frame, Size, Size, Size / 2.0, Size / 2.0,
                                               2.0 * FwhmPx, 4.0 * FwhmPx, outer * FwhmPx,
                                               Spec.ReadNoiseElectrons, 0.0);
            Console.WriteLine($"     outer {outer,4:F1} FWHM -> {s.AnnulusPixels,6} annulus pixels, sigma {s.FluxUncertainty:F1} e-");
        }
    }

    /// <summary>
    /// THE ROUND TRIP. Magnitudes in, frame out, magnitudes back.
    /// </summary>
    static void SectionZeroPointAndRoundTrip()
    {
        Header("3. Magnitudes in, magnitudes out");
        const int Size = 401;
        const double Sky = 100.0;
        const double TrueZeroPoint = 24.0;     // the value the field is built with

        // A field of stars of known magnitude, laid out so no aperture touches another's annulus.
        //
        // The range is chosen so every star is DETECTED, which is what this section is testing: a
        // star below the frame's own limit does not come back with a large error bar, it does not
        // come back at all, and a round trip that includes one is testing the detection limit
        // instead of the photometry. At this zero point and sky the faintest here is recovered at
        // about ten sigma; the limit itself is section 5's business, not this one's.
        var mags = new[] { 12.0, 13.0, 14.0, 15.0, 16.0 };
        var placed = new List<(double X, double Y, double Flux)>();
        var known = new List<double>();
        int columns = 3;
        for (int i = 0; i < mags.Length; i++)
        {
            double x = 80.0 + (i % columns) * 120.0;
            double y = 110.0 + (i / columns) * 190.0;
            double flux = Math.Pow(10.0, -0.4 * (mags[i] - TrueZeroPoint));
            placed.Add((x, y, flux));
            known.Add(mags[i]);
        }

        var rng = new Pcg32(Pcg32.MixSeed(20260803), Pcg32.StreamShotNoise);
        var frame = BuildFrame(Size, placed.ToArray(), Sky, Spec.ReadNoiseElectrons, rng);

        double bgLevel, bgRms;
        AperturePhotometry.EstimateBackground(frame, frame.Length, out bgLevel, out bgRms);
        Console.WriteLine($"   background {bgLevel:F1} +/- {bgRms:F1} e- against {Sky:F0} put in");
        Check("the frame's background is recovered", bgLevel, Sky, 3.0 * bgRms / Math.Sqrt(frame.Length) + 2.0);

        var detected = AperturePhotometry.FindSources(frame, Size, Size, bgLevel, bgRms, 5.0, (int)Math.Round(FwhmPx));
        Console.WriteLine($"   {detected.Count} sources found above 5 sigma, against {placed.Count} placed");
        Check("every star is found and nothing else is", detected.Count, placed.Count, 0);

        // Measure them all, then fit the zero point from them.
        var instrumental = new List<double>();
        var uncertainties = new List<double>();
        var measured = new List<AperturePhotometry.Source>();
        foreach (var (px, py, _) in placed)
        {
            var s = AperturePhotometry.Measure(frame, Size, Size, px, py, 2.0 * FwhmPx,
                                               4.0 * FwhmPx, 6.0 * FwhmPx, Spec.ReadNoiseElectrons, 0.0);
            measured.Add(s);
            instrumental.Add(s.InstrumentalMagnitude);
            uncertainties.Add(s.MagnitudeUncertainty);
        }

        double zp, zpErr; int used;
        AperturePhotometry.FitZeroPoint(instrumental, known, uncertainties, out zp, out zpErr, out used);

        // The fitted zero point must differ from the one the field was built with by exactly the
        // aperture correction: a 2-FWHM aperture holds 1 - exp(-2 ln2 * 4) of a Gaussian, and the
        // missing light makes every star look fainter by the same amount.
        double enclosed = 1.0 - Math.Exp(-(2.0 * FwhmPx) * (2.0 * FwhmPx) / (2.0 * SigmaPx * SigmaPx));
        double apertureCorrection = -2.5 * Math.Log10(enclosed);
        Console.WriteLine($"   zero point fitted from {used} stars: {zp:F4} +/- {zpErr:F4}");
        Console.WriteLine($"   built with {TrueZeroPoint:F4}, aperture correction {apertureCorrection:F4} mag " +
                          $"({enclosed * 100:F2}% enclosed at 2 FWHM)");
        Console.WriteLine($"   difference {zp - TrueZeroPoint:F4} against an expected {apertureCorrection:F4}");
        Check("the zero point is the true one plus the aperture correction",
              zp - TrueZeroPoint, apertureCorrection, 0.02);

        Console.WriteLine();
        Console.WriteLine("   known    recovered      error    residual   in sigma");
        var rows = new List<string> { "known_mag,recovered_mag,error,residual,residual_in_sigma" };
        double chi2 = 0.0; int n = 0; double worst = 0.0;
        for (int i = 0; i < measured.Count; i++)
        {
            double mag, magErr;
            AperturePhotometry.Calibrate(measured[i], zp, zpErr, out mag, out magErr);
            double residual = mag - known[i];
            double inSigma = residual / magErr;
            chi2 += inSigma * inSigma; n++;
            worst = Math.Max(worst, Math.Abs(residual));

            Console.WriteLine($"   {known[i],6:F2}   {mag,10:F4}   {magErr,8:F4}   {residual,+9:F4}   {inSigma,+8:F2}");
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0:G6},{1:G8},{2:G6},{3:G6},{4:G6}",
                                   known[i], mag, magErr, residual, inSigma));

            Check($"star at {known[i]:F1} comes back within three sigma", Math.Abs(inSigma) < 3.0);
        }
        File.WriteAllLines(Path.Combine(outDir, "roundtrip.csv"), rows);

        double reducedChi2 = chi2 / n;
        Console.WriteLine($"   worst residual {worst:F4} mag, reduced chi-squared {reducedChi2:F3} over {n} stars");
        Check("the residuals are consistent with the error bars", reducedChi2 < 3.0);
        Check("and are not suspiciously small either", reducedChi2 > 0.1);
    }

    /// <summary>
    /// A frame of Gaussian sources on a flat sky, in electrons, with Poisson and read noise when a
    /// generator is given and neither when it is not.
    ///
    /// The Gaussian is integrated over each pixel rather than sampled at its centre, because a
    /// point sampled at 4 px FWHM is wrong by half a percent at the core, which is larger than
    /// every tolerance in this file.
    /// </summary>
    static float[] BuildFrame(int size, (double X, double Y, double Flux)[] sources,
                              double sky, double readNoise, Random rng)
    {
        var frame = new float[size * size];
        double s = SigmaPx;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double v = sky;
                foreach (var (sx, sy, flux) in sources)
                {
                    double ex = Erf((x + 0.5 - sx) / (s * Math.Sqrt(2.0))) - Erf((x - 0.5 - sx) / (s * Math.Sqrt(2.0)));
                    double ey = Erf((y + 0.5 - sy) / (s * Math.Sqrt(2.0))) - Erf((y - 0.5 - sy) / (s * Math.Sqrt(2.0)));
                    v += flux * 0.25 * ex * ey;
                }
                if (rng != null)
                {
                    v = Poisson(rng, Math.Max(0.0, v));
                    if (readNoise > 0.0) v += NoiseSampler.Gaussian(rng, readNoise);
                }
                frame[y * size + x] = (float)v;
            }
        }
        return frame;
    }

    /// <summary>Abramowitz and Stegun 7.1.26, good to 1.5e-7, which is far below anything measured here.</summary>
    static double Erf(double x)
    {
        int sign = x < 0 ? -1 : 1;
        x = Math.Abs(x);
        double t = 1.0 / (1.0 + 0.3275911 * x);
        double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }

    static double Poisson(Random rng, double mean)
    {
        if (mean <= 0.0) return 0.0;
        if (mean < 30.0)
        {
            double limit = Math.Exp(-mean), p = 1.0; int count = 0;
            do { count++; p *= rng.NextDouble(); } while (p > limit);
            return count - 1;
        }
        return Math.Max(0.0, mean + NoiseSampler.Gaussian(rng, Math.Sqrt(mean)));
    }

    static void Header(string t) { Console.WriteLine(); Console.WriteLine(t); Console.WriteLine(new string('-', t.Length)); }
    static void Check(string what, double got, double expected, double tol)
    {
        if (!(Math.Abs(got - expected) <= tol)) { failures++; Console.WriteLine($"     FAIL {what}: got {got:G8}, expected {expected:G8} +/- {tol:G4}"); }
    }
    static void Check(string what, bool ok) { if (!ok) { failures++; Console.WriteLine($"     FAIL {what}"); } }
}
