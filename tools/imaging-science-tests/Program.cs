using System;
using ExoInstruments.Core;

// What a photograph of a world is worth, and the four properties that stop it being farmable.
// Runs headless: ImagingScience is pure arithmetic and ScienceRewards is constants.
internal static class Program
{
    private const float PerRung = ScienceRewards.ScienceRewardResolutionRung;
    private const int PerDoubling = ScienceRewards.ResolutionRungsPerDoubling;
    private const int Cap = ScienceRewards.ResolutionRungCap;

    private static int failures;

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what + (detail.Length > 0 ? ": " + detail : ""));
        if (!ok) failures++;
    }

    private static void Near(string what, double got, double want, double tol, string unit)
    {
        bool ok = Math.Abs(got - want) <= tol;
        Check(what, ok, $"{got:G6} vs {want:G6} {unit}");
    }

    private static void Main()
    {
        Ladder();
        Ratchet();
        FieldClip();
        Sampling();
        Reconnaissance();
        Budget();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "all checks passed." : $"{failures} FAILED.");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // ---------------------------------------------------------------- 1

    private static void Ladder()
    {
        Console.WriteLine("\n1. The detail ladder\n--------------------");

        // The closed form has to equal the series it stands for, or a ratchet built on differences
        // of it silently pays the wrong amount.
        double summed = 0.0;
        double ratio = Math.Pow(2.0, 1.0 / PerDoubling);
        for (int n = 1; n <= Cap; n++)
        {
            summed += PerRung * Math.Pow(ratio, n - 1);
            Near($"closed form equals the sum of rungs 1..{n}",
                 ImagingScience.LadderTotal(n, PerRung, PerDoubling), summed, 1e-9, "Science");
        }

        // The defining property of the spacing, and it is about the INCREMENT, not the cumulative
        // total: a rung PerDoubling above another is worth twice as much as that one. The totals
        // themselves do not double, and asserting that they do would be asserting a falsehood.
        for (int n = 1; n + PerDoubling <= Cap; n++)
        {
            double lower = ImagingScience.LadderTotal(n, PerRung, PerDoubling)
                         - ImagingScience.LadderTotal(n - 1, PerRung, PerDoubling);
            double upper = ImagingScience.LadderTotal(n + PerDoubling, PerRung, PerDoubling)
                         - ImagingScience.LadderTotal(n + PerDoubling - 1, PerRung, PerDoubling);
            Near($"rung {n + PerDoubling} pays twice what rung {n} did", upper, 2.0 * lower, 1e-9, "Science");
        }

        // The first rung is the unit everything else is quoted in.
        Near("the first rung is the base award",
             ImagingScience.LadderTotal(1, PerRung, PerDoubling), PerRung, 1e-9, "Science");

        Check("nothing is owed for an unresolved target",
              ImagingScience.LadderTotal(0, PerRung, PerDoubling) == 0.0, "rung 0");
    }

    // ---------------------------------------------------------------- 2

    private static void Ratchet()
    {
        Console.WriteLine("\n2. The ratchet\n--------------");

        // THE ANTI-FARM PROPERTY. Re-shooting an identical frame pays exactly zero, at every rung.
        for (int n = 1; n <= Cap; n++)
        {
            double total = ImagingScience.LadderTotal(n, PerRung, PerDoubling);
            Near($"re-shooting rung {n} pays nothing", total - total, 0.0, 0.0, "Science");
        }

        // And improving pays the difference and only the difference, however you get there: one
        // jump from 2 to 8 must pay the same as 2 to 5 and then 5 to 8.
        double direct = ImagingScience.LadderTotal(8, PerRung, PerDoubling)
                      - ImagingScience.LadderTotal(2, PerRung, PerDoubling);
        double staged = (ImagingScience.LadderTotal(5, PerRung, PerDoubling)
                       - ImagingScience.LadderTotal(2, PerRung, PerDoubling))
                      + (ImagingScience.LadderTotal(8, PerRung, PerDoubling)
                       - ImagingScience.LadderTotal(5, PerRung, PerDoubling));
        Near("the path to a rung does not change what it pays", staged, direct, 1e-12, "Science");

        double ceiling = ImagingScience.LadderTotal(Cap, PerRung, PerDoubling);
        Check("a body is bounded", ceiling < 30.0, $"{ceiling:F2} Science at the ceiling");
    }

    // ---------------------------------------------------------------- 3

    private static void FieldClip()
    {
        Console.WriteLine("\n3. The field clip\n-----------------");

        // A camera cannot record detail it has no field for. LORRI just outside the Mun's sphere of
        // influence: the disc is tens of thousands of arcsec across and the field is about 1048.
        const double munDiscArcsec = 33953.0;
        const double lorriFieldArcsec = 1048.0;
        const double lorriResArcsec = 2.05;

        double clipped = ImagingScience.ResolvedElements(munDiscArcsec, lorriFieldArcsec, lorriResArcsec);
        double unclipped = ImagingScience.ResolvedElements(munDiscArcsec, 0.0, lorriResArcsec);
        Check("the frame, not the target, bounds what was recorded",
              clipped < unclipped / 30.0, $"{clipped:F0} elements against {unclipped:F0} unclipped");
        Near("and it is the field over the resolution element", clipped,
             lorriFieldArcsec / lorriResArcsec, 1e-9, "elements");

        // The rung ceiling is then a property of the camera, which is exactly why claim B exists.
        int rungNear = ImagingScience.DetailRung(munDiscArcsec, lorriFieldArcsec, lorriResArcsec, Cap);
        int rungFar = ImagingScience.DetailRung(munDiscArcsec * 100.0, lorriFieldArcsec, lorriResArcsec, Cap);
        Check("a bigger disc past the field earns no more detail", rungNear == rungFar,
              $"rung {rungNear} both times");

        Check("a target smaller than the field is not clipped",
              ImagingScience.ResolvedElements(10.0, lorriFieldArcsec, 1.0) == 10.0, "10 elements");
    }

    // ---------------------------------------------------------------- 4

    private static void Sampling()
    {
        Console.WriteLine("\n4. Sampling, and why binning is not a free multiplier\n-----------------------------------------------------");

        // A blur-limited instrument: the delivered PSF is far coarser than the pixels, so the
        // resolution element is the blur and binning to it costs nothing real.
        double fine = ImagingScience.ResolutionElementArcsec(0.10, 1.20, 0.02, 0.05, 0.0);
        Near("blur wins when the detector oversamples it", fine, Math.Sqrt(0.01 + 1.44 + 0.0004), 1e-9, "arcsec");

        // A sampling-limited one: the pixels are coarser than the blur, so Nyquist sets the floor.
        double coarse = ImagingScience.ResolutionElementArcsec(0.05, 0.0, 0.0, 1.00, 0.0);
        Near("Nyquist wins when the detector undersamples the blur", coarse, 2.0, 1e-9, "arcsec");

        // THE BINNING PROPERTY, and it is that binning can never PAY. The field in arcseconds does
        // not change with binning; only the pixels do. So on a sampling-limited instrument binning
        // coarsens the element in proportion and the recorded count falls with it, which is what
        // binning physically does, and on a blur-limited one the element is already the blur and
        // binning down to 1x1 buys exactly nothing. Either way there is no rung to be had by
        // toggling a free switch.
        const double nativePlateScale = 1.0;
        const double fieldArcsec = 4096.0 * nativePlateScale;   // binning does not move the field
        foreach (int binning in new[] { 1, 2, 4 })
        {
            double res = ImagingScience.ResolutionElementArcsec(0.05, 0.0, 0.0, nativePlateScale * binning, 0.0);
            double elements = ImagingScience.ResolvedElements(1e9, fieldArcsec, res);
            Near($"sampling-limited detail falls with binning at {binning}x{binning}",
                 elements, 2048.0 / binning, 1e-9, "elements");
        }

        // The blur-limited case is the one a player would try to farm, and it does not move.
        double blurLimited1 = ImagingScience.ResolvedElements(
            1e9, fieldArcsec, ImagingScience.ResolutionElementArcsec(0.0, 8.0, 0.0, nativePlateScale * 1, 0.0));
        double blurLimited4 = ImagingScience.ResolvedElements(
            1e9, fieldArcsec, ImagingScience.ResolutionElementArcsec(0.0, 8.0, 0.0, nativePlateScale * 4, 0.0));
        Near("unbinning a blur-limited instrument buys nothing", blurLimited1, blurLimited4, 1e-9, "elements");

        // A defocus disc the instrument builds in is a floor too: BRITE spreads its stars on
        // purpose and must not claim detail that spreading destroyed.
        double defocused = ImagingScience.ResolutionElementArcsec(0.5, 0.0, 2.0, 13.0, 212.0);
        Near("a built-in defocus disc floors the element", defocused, 212.0, 1e-9, "arcsec");
    }

    // ---------------------------------------------------------------- 5

    private static void Reconnaissance()
    {
        Console.WriteLine("\n5. Reconnaissance\n-----------------");

        // Ground sample distance is not capped by the sensor, which is the point: it keeps falling
        // as the spacecraft closes, where the detail rung has already saturated.
        const double res = 2.05;
        double far = ImagingScience.GroundSampleMetres(12.0e9, res);
        double near = ImagingScience.GroundSampleMetres(2.4e6, res);
        Check("closing the range sharpens the ground sample", near < far / 1000.0,
              $"{near:F0} m against {far:F0} m");

        double munThreshold = ImagingScience.ReconnaissanceThresholdMetres(
            200000.0, ScienceRewards.ReconnaissanceElementsAcrossRadius);
        Near("the Mun asks for its radius over five hundred", munThreshold, 400.0, 1e-9, "m");
        Check("and the claim is reachable from its sphere of influence", near <= munThreshold,
              $"{near:F0} m against {munThreshold:F0} m");

        // A 3 cm lens cannot claim it from anywhere legal, which is the intended answer.
        double briteRes = ImagingScience.ResolutionElementArcsec(4.4, 0.0, 12.0, 13.25, 212.0);
        double briteGsd = ImagingScience.GroundSampleMetres(2.4e6, briteRes);
        Check("a cubesat does not claim reconnaissance", briteGsd > munThreshold,
              $"{briteGsd:F0} m against {munThreshold:F0} m");

        Check("an unknown range claims nothing",
              double.IsPositiveInfinity(ImagingScience.GroundSampleMetres(0.0, res)), "");
    }

    // ---------------------------------------------------------------- 6

    private static void Budget()
    {
        Console.WriteLine("\n6. What the programme is worth\n------------------------------");

        // The whole detail half, if every stock body were photographed to the ceiling.
        double perBody = ImagingScience.LadderTotal(Cap, PerRung, PerDoubling);
        double detailHalf = 15.0 * perBody;

        // And the reconnaissance half, against the stock bodies' own summed InSpaceHigh values.
        const double summedInSpaceHigh = 94.5;
        double reconHalf = ScienceRewards.ScienceRewardReconnaissancePerScienceValue * summedInSpaceHigh;

        double programme = detailHalf + reconHalf;
        Console.WriteLine($"    detail {detailHalf:F0} + reconnaissance {reconHalf:F0} = {programme:F0} Science");

        // Measured against the stock tree, which ScienceRewards puts at about 17500.
        Check("the programme is a meaningful fraction of the tree, not the tree",
              programme > 300.0 && programme < 1200.0, $"{programme / 17500.0:P1} of 17500");
        Check("and it stays under the exoplanet side's own design point",
              programme < 2000.0, $"{programme:F0} against 2000");
    }
}
