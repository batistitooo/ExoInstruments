using System;
using System.Globalization;
using ExoInstruments.Core;

// Headless checks on Core.DetectorPersistence and on the sourcing of the roster's persistence
// parameters. No Unity, no KSP, no game running.
//
// Run:  dotnet run -p:Core=../../ExoInstruments/Core
internal static class PersistenceTests
{
    private static int failures;

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) failures++;
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private static int Main()
    {
        Console.WriteLine();
        Console.WriteLine("A. Capture: the shape the measurements have");
        CaptureShape();

        Console.WriteLine();
        Console.WriteLine("B. Release: two populations, exact under any split of the interval");
        ReleaseExactness();

        Console.WriteLine();
        Console.WriteLine("C. The published reference points, reproduced");
        ReferencePoints();

        Console.WriteLine();
        Console.WriteLine("D. The roster's sourcing");
        RosterSourcing();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- A

    private const double FullWell = 63000.0;
    private const double Threshold = 0.9;      // fraction of full well
    private const double Fraction = 0.003;     // WFPC2's measured order, used here as a TEST value
    private const double Density = 500.0;      // electrons of interface states per pixel

    private static void CaptureShape()
    {
        // Nothing below threshold. This is the whole content of "residual images follow saturated
        // sources", and a proportional model would fail it.
        double belowT = DetectorPersistence.Capture(
            0.5 * FullWell, FullWell, Threshold, Fraction, Density, 0.0);
        Check("nothing captured below threshold", belowT == 0.0, "captured = " + F(belowT) + " e-");

        double atT = DetectorPersistence.Capture(
            Threshold * FullWell, FullWell, Threshold, Fraction, Density, 0.0);
        Check("nothing captured exactly at threshold", atT == 0.0, "captured = " + F(atT) + " e-");

        // Monotonic above it, and proportional to the excess while the traps are far from full.
        double a = DetectorPersistence.Capture(FullWell, FullWell, Threshold, Fraction, Density, 0.0);
        double b = DetectorPersistence.Capture(2.0 * FullWell, FullWell, Threshold, Fraction, Density, 0.0);
        Check("captured charge grows with the excess", b > a, F(a) + " -> " + F(b) + " e-");

        // Saturating: a pixel driven to 100x full well does not trap 100x the charge. This is what
        // the WFPC2 handbook's behaviour after such an overexposure requires.
        double gross = DetectorPersistence.Capture(
            100.0 * FullWell, FullWell, Threshold, Fraction, Density, 0.0);
        Check("capture saturates at the trap density", Math.Abs(gross - Density) < 1e-9,
              F(gross) + " e- against a density of " + F(Density));

        // Already-full traps take nothing more, so a repeatedly saturated pixel stops accumulating.
        double onFull = DetectorPersistence.Capture(
            100.0 * FullWell, FullWell, Threshold, Fraction, Density, Density);
        Check("full traps take nothing further", onFull == 0.0, "captured = " + F(onFull) + " e-");

        // A zero full well is a meaningless device, not a divide by zero.
        double degenerate = DetectorPersistence.Capture(1000.0, 0.0, Threshold, Fraction, Density, 0.0);
        Check("a zero full well captures nothing", degenerate == 0.0, "captured = " + F(degenerate));
    }

    // ---------------------------------------------------------------- B

    private static void ReleaseExactness()
    {
        const double tau = 120.0;
        const double q0 = 400.0;

        // A single exponential composes with itself: releasing over 300 s in one step must equal
        // releasing over 100 s three times. This is the property that makes carrying the two
        // populations separately worth the second array, because a SUM of two exponentials does
        // NOT have it, and a model that stored one number with a two-term decay law would be
        // correct only at the cadence its fit was measured at.
        double one = DetectorPersistence.Release(q0, 300.0, tau);

        double held = q0, cumulative = 0.0;
        for (int i = 0; i < 3; i++)
        {
            double r = DetectorPersistence.Release(held, 100.0, tau);
            cumulative += r;
            held -= r;
        }
        double err = Math.Abs(one - cumulative) / q0;
        Check("release over 3x100 s equals release over 300 s", err < 1e-12,
              "relative difference " + err.ToString("E2", CultureInfo.InvariantCulture));

        // And the counter-example, stated as a measurement rather than asserted: the same test on a
        // single population carrying a TWO-term law fails, which is why the code does not do that.
        double twoTermOneStep = q0 * (1.0 - DetectorPersistence.RemainingFraction(300.0, 0.5, 20.0, 600.0));
        double twoTermHeld = q0, twoTermCumulative = 0.0;
        for (int i = 0; i < 3; i++)
        {
            double r = twoTermHeld * (1.0 - DetectorPersistence.RemainingFraction(100.0, 0.5, 20.0, 600.0));
            twoTermCumulative += r;
            twoTermHeld -= r;
        }
        double twoTermErr = Math.Abs(twoTermOneStep - twoTermCumulative) / q0;
        Check("a one-array two-term law would NOT compose (the reason for two arrays)",
              twoTermErr > 1e-3,
              "relative difference " + twoTermErr.ToString("E2", CultureInfo.InvariantCulture));

        // Conservation: nothing is released that was not trapped, ever.
        double over = DetectorPersistence.Release(q0, 1.0e9, tau);
        Check("release never exceeds what was trapped", over <= q0 + 1e-9,
              F(over) + " of " + F(q0) + " e-");

        // Degenerate inputs return zero rather than a NaN that would propagate into the frame.
        Check("zero elapsed time releases nothing", DetectorPersistence.Release(q0, 0.0, tau) == 0.0, "");
        Check("empty traps release nothing", DetectorPersistence.Release(0.0, 100.0, tau) == 0.0, "");
    }

    // ---------------------------------------------------------------- C

    private static void ReferencePoints()
    {
        // WFPC2 Instrument Handbook Sect. 4.5: residual images disappear within 1000 s at -70 C.
        // "Disappear" is a detection statement, so the comparator is that essentially nothing is
        // left: the model must have shed the great majority of the trapped charge by then, for a
        // decay pair whose slow constant is set to the handbook's own timescale.
        double leftAt1000 = DetectorPersistence.RemainingFraction(
            DetectorPersistence.Wfpc2ClearingTimeSeconds, 0.5, 20.0, 250.0);
        Check("WFPC2's 1000 s clearing leaves a negligible residual", leftAt1000 < 0.01,
              F(100.0 * leftAt1000) + " % still held at "
              + F(DetectorPersistence.Wfpc2ClearingTimeSeconds) + " s");

        // The same handbook: no measurable residual half an hour after a 100x full-well
        // overexposure. Half an hour is 1800 s, well past the above.
        double leftAt1800 = DetectorPersistence.RemainingFraction(1800.0, 0.5, 20.0, 250.0);
        Check("nothing measurable half an hour later", leftAt1800 < 1e-3,
              F(100.0 * leftAt1800) + " % still held at 1800 s");

        // arXiv:2502.05418 on LSSTCam's e2v CCD250: the residual takes well over a hundred seconds
        // to dissipate. So at the paper's own scale the model must still be holding something
        // appreciable, which is the opposite comparator to the two above and catches a decay pair
        // that clears too fast.
        double leftAtLsst = DetectorPersistence.RemainingFraction(
            DetectorPersistence.Ccd250ClearingTimeSeconds, 0.5, 20.0, 250.0);
        Check("still holding charge at the CCD250's own clearing scale", leftAtLsst > 0.05,
              F(100.0 * leftAtLsst) + " % still held at "
              + F(DetectorPersistence.Ccd250ClearingTimeSeconds) + " s");

        // The reference constants are what the sources say. Locked here so an edit to them has to
        // be deliberate.
        Check("WFPC2 residual fraction is the handbook's 0.3%",
              Math.Abs(DetectorPersistence.Wfpc2ResidualFractionOfSaturatedFlux - 0.003) < 1e-12,
              F(DetectorPersistence.Wfpc2ResidualFractionOfSaturatedFlux));
        Check("WFPC2 uncertainty is the handbook's +/- 0.1%",
              Math.Abs(DetectorPersistence.Wfpc2ResidualFractionUncertainty - 0.001) < 1e-12,
              F(DetectorPersistence.Wfpc2ResidualFractionUncertainty));
        Check("CCD250 trail residual is the paper's 10 e-",
              Math.Abs(DetectorPersistence.Ccd250TrailResidualElectrons - 10.0) < 1e-12,
              F(DetectorPersistence.Ccd250TrailResidualElectrons));
    }

    // ---------------------------------------------------------------- D

    private static void RosterSourcing()
    {
        // The point of this section: the effect must be OFF on every instrument, and off for a
        // recorded reason. A parameter that appears without a citation should break this.
        var roster = VisualTelescopeCatalog.All;
        Check("the roster is non-empty", roster != null && roster.Length > 0,
              (roster?.Length ?? 0) + " instruments");
        if (roster == null) return;

        foreach (var spec in roster)
        {
            string who = spec.Name + " / " + spec.CameraName;

            Check("persistence is off on " + who, !spec.HasPersistence,
                  spec.PersistenceMeasuredAbsent
                      ? "measured absent (ISR WFC3 2005-10)"
                      : "no published amplitude");

            // Measured-absent and unpublished are different facts and must not be conflated: an
            // instrument declared measured-absent must not also carry an amplitude.
            if (spec.PersistenceMeasuredAbsent)
            {
                Check("  measured-absent carries no invented amplitude on " + who,
                      double.IsNaN(spec.PersistenceTrappedFraction)
                      && double.IsNaN(spec.PersistenceTrapDensityElectrons),
                      "both NaN");
            }
        }

        // Exactly one instrument on this roster has been tested and found not to show it.
        int measuredAbsent = 0;
        foreach (var spec in roster) if (spec.PersistenceMeasuredAbsent) measuredAbsent++;
        Check("exactly one detector is measured-absent", measuredAbsent == 1,
              measuredAbsent + " instrument(s)");
    }
}
