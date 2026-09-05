using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// Does the fringe model turn one ESO measurement into the other?
///
/// The point of this harness is a single question. Walsh et al. (2008) measured 7.0% peak-to-peak
/// fringing on the FORS2 MIT mosaic at 956 nm with a MONOCHROMATOR. ESO's FORS2 user manual states
/// that in z_Gunn IMAGING the amplitude is "below 1%". Both are true, they describe the same
/// detector, and what separates them is an integral over a passband weighted by the night sky's own
/// line spectrum. If Core.Fringing computes that integral correctly it must reproduce both numbers
/// from one model; if it does not, it is describing something else.
///
/// Everything here calls the shipped Core: the airglow line and continuum spectra at their own
/// 0.1 nm sampling, the silicon dispersion, and Walsh's measured amplitude curve.
/// </summary>
static class FringeTests
{
    static int failures;
    static string outDir = ".";

    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--out") outDir = args[i + 1];
        Directory.CreateDirectory(outDir);

        Console.WriteLine("FORS2 CCD fringing: one model, two ESO measurements");
        Console.WriteLine(new string('=', 78));

        SectionThicknessCrossCheck();
        SectionMonochromatic();
        SectionBroadband();
        SectionMapAcrossCores();

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The foundation: a fringe period measured spectroscopically and a thickness stated by the
    /// people who bought the device must describe the same layer.
    /// </summary>
    static void SectionThicknessCrossCheck()
    {
        Header("1. Two independent measurements of one layer");

        const double WalshPeriodNm = 2.9;       // Walsh et al. 2008, from ESO GOODS 300I flats
        const double At = 950.0;
        double implied = Fringing.ThicknessFromPeriodMicrons(WalshPeriodNm, At);
        double published = Fringing.Ccid20ThicknessMicrons;

        Console.WriteLine($"   Walsh et al. measure a fringe period of {WalshPeriodNm} nm near {At:F0} nm");
        Console.WriteLine($"   silicon index there (Green 2008): n = {Fringing.SiliconRefractiveIndex(At):F3}");
        Console.WriteLine($"   implied layer thickness: {implied:F1} um");
        Console.WriteLine($"   Downing et al. (2006) state the CCID-20 is {published:F0} um thick");
        Console.WriteLine($"   agreement: {(implied / published - 1.0) * 100.0:+0.0;-0.0}%");

        Check("the spectroscopic period and the fabrication figure agree to 15%",
              Math.Abs(implied / published - 1.0) < 0.15);

        // And the model must return Walsh's own period from the published thickness, which is the
        // same statement read the other way.
        double period = Fringing.PeriodNm(published, At);
        Console.WriteLine($"   the published thickness predicts a period of {period:F2} nm against the measured {WalshPeriodNm}");
        Check("the published thickness predicts the measured period", period, WalshPeriodNm, 0.15 * WalshPeriodNm);

        // The period must fall as lambda^2, which is what makes fringes crowd together to the red.
        double p800 = Fringing.PeriodNm(published, 800.0), p1000 = Fringing.PeriodNm(published, 1000.0);
        Console.WriteLine($"   period at 800 nm {p800:F2} nm, at 1000 nm {p1000:F2} nm");
        Check("the period grows with wavelength", p1000 > p800);
    }

    /// <summary>Walsh's six monochromatic points, returned unchanged, and the wavelength dependence between them.</summary>
    static void SectionMonochromatic()
    {
        Header("2. The monochromatic amplitude curve");
        Console.WriteLine("   lambda [nm]   peak-to-peak   Walsh et al. 2008");
        var measured = new (double L, double A)[]
        { (774.0, 0.000), (876.0, 0.022), (906.0, 0.030), (926.1, 0.051), (956.1, 0.070), (986.0, 0.075) };

        foreach (var (l, a) in measured)
        {
            double got = Fringing.MonochromaticPeakToPeak(l);
            Console.WriteLine($"   {l,11:F1}   {got * 100,12:F1}%   {a * 100:F1}%");
            Check($"amplitude at {l} nm", got, a, 1e-9);
        }
        Check("no fringes in the visible", Fringing.MonochromaticPeakToPeak(600.0), 0.0, 1e-12);
        Check("held flat past the last measurement", Fringing.MonochromaticPeakToPeak(1100.0), 0.075, 1e-9);
    }

    /// <summary>
    /// THE CHECK THIS HARNESS EXISTS FOR. Integrate the same model over real passbands against the
    /// real night sky and see whether it lands where ESO's manual says.
    /// </summary>
    static void SectionBroadband()
    {
        Header("3. From a monochromator to an imaging filter");

        Func<double, double> sky = l => Airglow.LineDensityAtZenith(l) + Airglow.ContinuumDensityAtZenith(l);

        // Real passbands, as top-hats of their published centres and widths. FORS2's own filters,
        // plus z_Gunn, which is the one the manual quotes a number for.
        var bands = new (string Name, double Centre, double Width)[]
        {
            ("R_SPECIAL", 655.0, 165.0),      // ESO FORS filter page
            ("I_BESS",    768.0, 138.0),      // Bessell I as FORS2 carries it
            ("z_Gunn",    910.0,  13.0),      // Gunn z, the manual's own reference point
            ("Luminance", 715.0, 770.0),      // FORS2 unfiltered, 330-1100 nm
            ("1 nm line", 956.0,   1.0),      // a monochromator slit, for the limit
        };

        Console.WriteLine("   passband      centre   width    broadband peak-to-peak");
        var rows = new List<string> { "band,centre_nm,width_nm,peak_to_peak" };
        double zGunn = 0.0, monochromatic = 0.0;

        foreach (var (name, centre, width) in bands)
        {
            double lo = centre - 0.5 * width, hi = centre + 0.5 * width;
            Func<double, double> response = l => (l >= lo && l <= hi) ? 1.0 : 0.0;

            double amp = Fringing.BroadbandPeakToPeak(
                Fringing.Ccid20ThicknessMicrons, sky, response,
                Math.Max(AirglowTable.MinWavelengthNm, lo), Math.Min(AirglowTable.MaxWavelengthNm, hi),
                AirglowTable.StepNm, Math.Min(centre, 990.0));

            Console.WriteLine($"   {name,-11} {centre,7:F0} {width,7:F0}    {amp * 100,8:F3}%");
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1:G6},{2:G6},{3:G6}", name, centre, width, amp));
            if (name == "z_Gunn") zGunn = amp;
            if (name == "1 nm line") monochromatic = amp;
        }
        File.WriteAllLines(Path.Combine(outDir, "fringe_bands.csv"), rows);

        Console.WriteLine();
        Console.WriteLine("   ESO's FORS2 manual, section 2.7.2, on the MIT mosaic:");
        Console.WriteLine("     \"For Bessel I imaging, fringes are hardly visible\"");
        Console.WriteLine("     \"For z_Gunn imaging, the fringe amplitudes are below 1%\"");
        Console.WriteLine("     \"in the strongest telluric lines in spectroscopic modes ... of the order of 5%\"");
        Console.WriteLine();
        Console.WriteLine($"   this model: z_Gunn {zGunn * 100:F3}%, a 1 nm slit at 956 nm {monochromatic * 100:F2}%");

        Check("z_Gunn imaging comes out below 1 percent, as the manual states", zGunn < 0.01);
        Check("z_Gunn is not zero either", zGunn > 1e-5);
        Check("a monochromator slit recovers the spectroscopic regime", monochromatic > 0.03);

        // WHY THE SKY'S SPECTRUM IS HALF THE EFFECT, shown by running the same detector against
        // two skies. Against a SMOOTH source the washing-out is monotonic in bandwidth, because a
        // wider band simply runs the cosine through more turns and cancels more of it. Against the
        // REAL sky it is not, and that is not a numerical artefact: widening the band brings in
        // whole OH bands at once, and if the lines a widening admits happen to sit near one phase
        // they add rather than cancel. Fringing therefore does not have a bandwidth below which it
        // is safe, which is precisely why observatories measure it per filter instead of predicting
        // it, and why a night's own airglow changes it.
        Func<double, double> continuum = l => Airglow.ContinuumDensityAtZenith(l);

        Console.WriteLine();
        Console.WriteLine("   the same detector at 956 nm, against two skies:");
        Console.WriteLine("     width      real sky (lines + continuum)      smooth continuum only");

        var widths = new[] { 1.0, 3.0, 10.0, 30.0, 100.0 };
        var real = new double[widths.Length];
        var smooth = new double[widths.Length];
        var rows2 = new List<string> { "width_nm,real_sky,continuum_only" };

        for (int i = 0; i < widths.Length; i++)
        {
            double lo = 956.0 - 0.5 * widths[i], hi = 956.0 + 0.5 * widths[i];
            Func<double, double> response = l => (l >= lo && l <= hi) ? 1.0 : 0.0;
            double min = Math.Max(AirglowTable.MinWavelengthNm, lo);
            double max = Math.Min(AirglowTable.MaxWavelengthNm, hi);

            real[i] = Fringing.BroadbandPeakToPeak(
                Fringing.Ccid20ThicknessMicrons, sky, response, min, max, AirglowTable.StepNm, 956.0);
            smooth[i] = Fringing.BroadbandPeakToPeak(
                Fringing.Ccid20ThicknessMicrons, continuum, response, min, max, AirglowTable.StepNm, 956.0);

            Console.WriteLine($"     {widths[i],5:F0} nm {real[i] * 100,20:F3}% {smooth[i] * 100,26:F3}%");
            rows2.Add(string.Format(CultureInfo.InvariantCulture, "{0:G6},{1:G6},{2:G6}",
                                    widths[i], real[i], smooth[i]));
        }
        File.WriteAllLines(Path.Combine(outDir, "fringe_bandwidth.csv"), rows2);

        // NEITHER SKY IS MONOTONIC, and the two are not monotonic for different reasons. That is
        // the result, and it took a failing test to arrive at rather than an assumption.
        //
        // Even a perfectly smooth source does not wash out monotonically: integrating cos(2 pi P/l)
        // over a top-hat gives a sinc envelope in bandwidth, so the amplitude collapses wherever
        // the band spans a whole number of fringe periods and revives between those zeros. The
        // period here is 3.15 nm, and the smooth sky duly bottoms out at a 3 nm band, at 0.185%
        // against 0.461% at 10 nm.
        //
        // The real sky adds a second, unrelated structure on top: its OH bands sample the cosine at
        // particular phases rather than averaging over it, so widening the band can ADMIT more
        // coherent light than it cancels. At a 3 nm band the two skies differ by a factor of 11.
        //
        // Together these are why fringing has no safe bandwidth and why observatories measure it
        // per filter rather than predicting it, and why a night's own airglow changes it.
        double envelopeFall = real[real.Length - 1] / real[0];
        Console.WriteLine($"   envelope over a hundredfold widening: {real[0] * 100:F3}% to {real[real.Length - 1] * 100:F3}%, " +
                          $"a factor {1.0 / envelopeFall:F1}");
        Check("the envelope falls by more than an order of magnitude, on the real sky", envelopeFall < 0.1);
        Check("and on a smooth one", smooth[smooth.Length - 1] / smooth[0] < 0.1);

        // The sinc zero: a smooth source integrated over one whole fringe period must cancel almost
        // exactly, and that is a sharp prediction rather than a trend.
        double period = Fringing.PeriodNm(Fringing.Ccid20ThicknessMicrons, 956.0);
        Console.WriteLine($"   the fringe period at 956 nm is {period:F2} nm, and the smooth sky's minimum " +
                          $"falls at the {widths[1]:F0} nm band");
        Check("a smooth source cancels at a band of one fringe period",
              smooth[1] < 0.1 * smooth[0]);

        // And the point of the whole comparison: at that same bandwidth the line-dominated sky does
        // NOT cancel, by a large factor.
        double lineExcess = real[1] / smooth[1];
        Console.WriteLine($"   at that bandwidth the real sky fringes {lineExcess:F1} times harder than a smooth one");
        Check("the sky's line spectrum is what keeps the fringes alive", lineExcess > 3.0);

        bool realMonotonic = true, smoothMonotonic = true;
        for (int i = 1; i < widths.Length; i++)
        {
            if (real[i] >= real[i - 1]) realMonotonic = false;
            if (smooth[i] >= smooth[i - 1]) smoothMonotonic = false;
        }
        Check("neither sky washes out monotonically", !realMonotonic && !smoothMonotonic);
    }

    /// <summary>
    /// The map itself, and the two claims made for it that are not claims about optics.
    ///
    /// Core.FringeMap.Build is the loop EnsureFringeMap used to run with the spectral integral
    /// inside it, once per pixel. Moving it into Core was argued for on the grounds that a
    /// frame-sized loop in the Unity layer is a loop no harness can compile, so this is the harness
    /// compiling it. Until this section existed nothing under tools/ referenced the file at all, and
    /// the equivalence figures for the move lived only in a commit message, which is not a thing
    /// that re-runs.
    ///
    /// Neither claim below is about fringing. One is that the map does not depend on how the work
    /// was divided across cores, which is what ParallelWork's policy demands of every parallel stage
    /// and the reason that policy is written down rather than assumed. The other is that hoisting
    /// the refractive index out of the loop did not move the optical path, which is the step that
    /// made the loop cheap and the one place the refactor could have changed a frame.
    /// </summary>
    static void SectionMapAcrossCores()
    {
        Header("4. The map, and what dividing it across cores may not change");

        var spec = VisualTelescopeCatalog.Fors2Vlt;
        double thickness = spec.DetectorSiliconThicknessMicrons;
        double variation = spec.DetectorThicknessVariationFraction;

        // The frame builder's own conversion: the modulation field carries unit variance, and four
        // sigma of a unit-variance field spans the published peak-to-peak, so this is the thickness
        // sigma EnsureFringeMap passes.
        double thicknessSigma = thickness * variation / 4.0;

        // I_BESS over the real night sky, one of section 3's own bands. It has to be wide enough to
        // matter: ParallelWork.Worthwhile gates on pixels times samples, so a narrow band on a small
        // frame would take the serial branch twice over and the comparison below would pass whatever
        // the code did.
        Func<double, double> sky = l => Airglow.LineDensityAtZenith(l) + Airglow.ContinuumDensityAtZenith(l);
        const double Lo = 699.0, Hi = 837.0, Centre = 768.0;
        var passband = Fringing.Passband.Sample(
            sky, l => (l >= Lo && l <= Hi) ? 1.0 : 0.0,
            Math.Max(AirglowTable.MinWavelengthNm, Lo), Math.Min(AirglowTable.MaxWavelengthNm, Hi),
            AirglowTable.StepNm);

        // 200 x 200 and not a token array. Build divides the frame into 4096-pixel blocks, so a
        // frame of one block or less is ONE block, which Parallel.For hands to a single worker: the
        // comparison below would then be serial against serial and would prove nothing.
        const int W = 200, H = 200;
        int n = W * H;
        int blocks = (n + 4095) / 4096;
        long operations = (long)n * passband.FringingSampleCount;
        float[] modulation = ThicknessModulation(n);

        Console.WriteLine($"   {W}x{H} = {n} pixels in {blocks} blocks of 4096");
        Console.WriteLine($"   {Lo:F0}-{Hi:F0} nm, {passband.FringingSampleCount} samples past the 774 nm onset");
        Console.WriteLine($"   {operations:N0} elementary operations against ParallelWork's 200,000 threshold");
        Check("the frame divides into more than one block", blocks > 1);

        // ---- the map may not depend on the worker count -----------------------------------------

        ParallelWork.UseWorkers(1);
        Check("at one worker the serial branch is the one taken", !ParallelWork.Worthwhile(operations));
        ushort[] serial = FringeMap.Build(passband, modulation, thickness, thicknessSigma, Centre);

        var counts = new List<int> { 2, 3 };
        int cores = Math.Max(2, Environment.ProcessorCount - 1);
        if (!counts.Contains(cores)) counts.Add(cores);

        Console.WriteLine();
        Console.WriteLine("   workers   differing half words");
        // Not written to a CSV beside the other two: the worker counts come from
        // Environment.ProcessorCount, so the file would differ from machine to machine while saying
        // nothing the console line does not. The other CSVs here are physics and are the same
        // everywhere.
        foreach (int workers in counts)
        {
            ParallelWork.UseWorkers(workers);
            Check($"at {workers} workers the parallel branch is the one taken", ParallelWork.Worthwhile(operations));

            ushort[] parallel = FringeMap.Build(passband, modulation, thickness, thicknessSigma, Centre);
            int differing = 0;
            for (int i = 0; i < n; i++) if (parallel[i] != serial[i]) differing++;

            Console.WriteLine($"   {workers,7}   {differing,10} out of {n}");

            Check($"the map is bit for bit unchanged at {workers} workers", differing == 0);
        }
        ParallelWork.UseWorkers(Math.Max(1, Environment.ProcessorCount - 1));

        // ---- what the map stores, and the hoist that made it cheap ------------------------------

        // SiliconRefractiveIndex is now evaluated ONCE at the filter's centre instead of once per
        // pixel, which is legitimate only if the path it produces is the same double the per-pixel
        // form produced.
        //
        // TWO CHECKS, AND ONLY THE SECOND ONE HAS TEETH. The first compares the two spellings of
        // the path against each other, which is an algebraic statement about association order and
        // is the premise the hoist rests on; it is computed here, so nothing inside FringeMap can
        // make it fail. The second compares the SHIPPED map against Fringing.OpticalPathNm, the
        // per-pixel form the loop replaced and which is still shipped unchanged, so it is the one
        // that fails if the hoist inside Build ever stops agreeing with it. Verified by perturbing
        // the index in a scratch copy of Core: the first check stayed green and the second went red.
        //
        // Equality rather than tolerance throughout, because a path that moved by one ulp would move
        // the frame a recorded seed reproduces, which is the property this pipeline exists to have.
        double indexAtCentre = Fringing.SiliconRefractiveIndex(Centre);
        int pathDiffs = 0, storedDiffs = 0;
        for (int i = 0; i < n; i++)
        {
            double localThickness = thickness + (modulation[i] - 1.0) * thicknessSigma;
            double hoisted = 2.0 * localThickness * 1000.0 * indexAtCentre;
            double perPixel = Fringing.OpticalPathNm(localThickness, Centre);
            if (hoisted != perPixel) pathDiffs++;

            // And the map holds (1 + x) - 1 snapped to half precision, not x. Adding one snaps x
            // onto the 2^-53 grid before the subtraction takes it off again, and the map has always
            // stored the snapped value; storing the unsnapped x would be slightly more accurate and
            // therefore wrong, because it would make this line a tolerance.
            if (serial[i] != Float16.FromDouble(passband.ModulationAt(perPixel) - 1.0)) storedDiffs++;
        }
        Console.WriteLine();
        Console.WriteLine($"   the hoisted and per-pixel paths differ on {pathDiffs} of {n} pixels");
        Console.WriteLine($"   the shipped map differs from the per-pixel form on {storedDiffs} of {n}");
        Check("the two spellings of the optical path agree bit for bit", pathDiffs == 0);
        Check("the shipped map matches the per-pixel form it replaced, exactly", storedDiffs == 0);

        // ---- what it refuses, and what it returns when there is nothing to do --------------------

        // Below the onset there is no second surface to interfere with and Walsh's own 774 nm flat
        // showed no fringes, so CanFringe is false. The map must then be exact ZEROS and not ones:
        // it stores the excess over unity, and FringeAt adds the one back.
        var noFringe = Fringing.Passband.Sample(
            sky, l => (l >= 500.0 && l <= 600.0) ? 1.0 : 0.0,
            Math.Max(AirglowTable.MinWavelengthNm, 500.0), Math.Min(AirglowTable.MaxWavelengthNm, 600.0),
            AirglowTable.StepNm);
        ushort[] unfringed = FringeMap.Build(noFringe, modulation, thickness, thicknessSigma, 550.0);
        bool allZero = unfringed.Length == n;
        for (int i = 0; allZero && i < n; i++) allZero = unfringed[i] == 0;
        Check("a passband below the onset cannot fringe", !noFringe.CanFringe);
        Check("and gives a map of exact zeros, not of ones", allZero);

        Check("an empty frame gives an empty map",
              FringeMap.Build(passband, new float[0], thickness, thicknessSigma, Centre).Length == 0);
        Check("a null passband is refused rather than dereferenced",
              Throws<ArgumentNullException>(() => FringeMap.Build(null, modulation, thickness, thicknessSigma, Centre)));
        Check("a null thickness field is refused rather than dereferenced",
              Throws<ArgumentNullException>(() => FringeMap.Build(passband, null, thickness, thicknessSigma, Centre)));
    }

    /// <summary>
    /// A deterministic stand-in for the thickness field the frame builder draws from
    /// Core.SpeckleField: unit mean, unit variance, one value per pixel.
    ///
    /// FringeMap.Build is a pure function of its arguments and has no opinion about where the field
    /// came from, so nothing above depends on this being the real speckle draw. What the worker
    /// comparison needs is that it is the SAME field at every worker count, which a fixed seed
    /// gives, and that it is not constant, which would make a per-pixel loop trivially agree with
    /// itself however it was divided.
    /// </summary>
    static float[] ThicknessModulation(int n)
    {
        var rng = new Pcg32(0x46524E47UL, Pcg32.StreamPhotoResponse);
        var field = new float[n];
        for (int i = 0; i < n; i++) field[i] = (float)(1.0 + NoiseSampler.Gaussian(rng, 1.0));
        return field;
    }

    static bool Throws<T>(Action act) where T : Exception
    {
        try { act(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }

    static void Header(string t) { Console.WriteLine(); Console.WriteLine(t); Console.WriteLine(new string('-', t.Length)); }
    static void Check(string what, double got, double expected, double tol)
    {
        if (!(Math.Abs(got - expected) <= tol)) { failures++; Console.WriteLine($"     FAIL {what}: got {got:G8}, expected {expected:G8} +/- {tol:G4}"); }
    }
    static void Check(string what, bool ok) { if (!ok) { failures++; Console.WriteLine($"     FAIL {what}"); } }
}
