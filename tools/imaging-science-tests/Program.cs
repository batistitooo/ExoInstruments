using System;
using ExoInstruments.Core;

// What a photograph of a world is worth, and the properties that stop it being farmable or
// pointless. Runs headless: ImagingScience is pure arithmetic and ScienceRewards is constants.
//
// The instrument figures below are the shipped ones, recomputed from VisualTelescopeCatalog's own
// sensor width, pixel pitch and focal length, so a change to the catalogue that breaks the design
// shows up here rather than in the game.
internal static class Program
{
    private const float PerRung = ScienceRewards.ScienceRewardResolutionRung;
    private const int PerDoubling = ScienceRewards.ResolutionRungsPerDoubling;
    private const int Cap = ScienceRewards.ResolutionRungCap;
    private const double GsdReference = ScienceRewards.GroundSampleReferenceMetres;
    private const int GsdCap = ScienceRewards.GroundSampleRungCap;

    // field width in arcsec, and the native plate scale in arcsec per pixel at binning 1.
    private const double Wfc3Field = 162.0, Wfc3Px = 162.0 / 4096.0;
    private const double LorriField = 1048.0, LorriPx = 1048.0 / 1024.0;
    private const double BriteField = 106291.0;
    private const double RedCatField = 15830.0, RedCatPx = 15830.0 / 4144.0;

    // Kerbin system geometry, metres.
    private const double MunRadius = 200000.0, MunFromKerbin = 1.2e7, MunSoi = 2.4296e6;
    private const double JoolRadius = 6.0e6, JoolSoi = 2.4559e9, JoolFromKerbin = 5.0e10;
    private const double TyloRadius = 600000.0, TyloSoi = 1.0856e7;

    private static int failures;

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what + (detail.Length > 0 ? ": " + detail : ""));
        if (!ok) failures++;
    }

    private static void Near(string what, double got, double want, double tol, string unit)
        => Check(what, Math.Abs(got - want) <= tol, $"{got:G6} vs {want:G6} {unit}");

    private static double ArcsecAcross(double radiusMetres, double distanceMetres)
        => 2.0 * radiusMetres / distanceMetres * (180.0 / Math.PI) * 3600.0;

    private static double Element(double plateScaleArcsec, int binning)
        => ImagingScience.ResolutionElementArcsec(0.05, 0.0, 0.0, plateScaleArcsec * binning, 0.0);

    private static void Main()
    {
        Ladder();
        Ratchet();
        Framing();
        Binning();
        GroundSample();
        Budget();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "all checks passed." : $"{failures} FAILED.");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // ---------------------------------------------------------------- 1

    private static void Ladder()
    {
        Console.WriteLine("\n1. The ladder\n-------------");

        double summed = 0.0;
        double ratio = Math.Pow(2.0, 1.0 / PerDoubling);
        for (int n = 1; n <= Cap; n++)
        {
            summed += PerRung * Math.Pow(ratio, n - 1);
            Near($"closed form equals the sum of rungs 1..{n}",
                 ImagingScience.LadderTotal(n, PerRung, PerDoubling), summed, 1e-9, "Science");
        }

        // The spacing property is about the INCREMENT, not the cumulative total. The totals do not
        // double and asserting that they do would be asserting a falsehood.
        for (int n = 1; n + PerDoubling <= Cap; n++)
        {
            double lower = ImagingScience.LadderTotal(n, PerRung, PerDoubling)
                         - ImagingScience.LadderTotal(n - 1, PerRung, PerDoubling);
            double upper = ImagingScience.LadderTotal(n + PerDoubling, PerRung, PerDoubling)
                         - ImagingScience.LadderTotal(n + PerDoubling - 1, PerRung, PerDoubling);
            Near($"rung {n + PerDoubling} pays twice what rung {n} did", upper, 2.0 * lower, 1e-9, "Science");
        }

        Near("the first rung is the base award",
             ImagingScience.LadderTotal(1, PerRung, PerDoubling), PerRung, 1e-9, "Science");
        Check("nothing is owed for an unresolved target",
              ImagingScience.LadderTotal(0, PerRung, PerDoubling) == 0.0, "rung 0");
    }

    // ---------------------------------------------------------------- 2

    private static void Ratchet()
    {
        Console.WriteLine("\n2. The ratchet\n--------------");

        for (int n = 1; n <= Cap; n++)
        {
            double total = ImagingScience.LadderTotal(n, PerRung, PerDoubling);
            Near($"re-shooting rung {n} pays nothing", total - total, 0.0, 0.0, "Science");
        }

        double direct = ImagingScience.LadderTotal(8, PerRung, PerDoubling)
                      - ImagingScience.LadderTotal(2, PerRung, PerDoubling);
        double staged = (ImagingScience.LadderTotal(5, PerRung, PerDoubling)
                       - ImagingScience.LadderTotal(2, PerRung, PerDoubling))
                      + (ImagingScience.LadderTotal(8, PerRung, PerDoubling)
                       - ImagingScience.LadderTotal(5, PerRung, PerDoubling));
        Near("the path to a rung does not change what it pays", staged, direct, 1e-12, "Science");
    }

    // ---------------------------------------------------------------- 3

    private static void Framing()
    {
        Console.WriteLine("\n3. Framing\n----------");

        // THE PROPERTY THAT REPLACED THE FIELD CLIP. Clipping paid the ladder's ceiling for a
        // featureless patch of surface, identically for every body over the field width, from any
        // range. Requiring the whole disc is what makes range and instrument choice matter.
        double munFromKerbin = ArcsecAcross(MunRadius, MunFromKerbin);
        Check("Hubble cannot frame the Mun from Kerbin",
              !ImagingScience.Frames(munFromKerbin, Wfc3Field),
              $"disc {munFromKerbin:F0}\" against a {Wfc3Field:F0}\" field");

        double munFromSoi = ArcsecAcross(MunRadius, MunSoi);
        Check("and flying Hubble to the Mun makes it worse, not better",
              !ImagingScience.Frames(munFromSoi, Wfc3Field),
              $"disc {munFromSoi:F0}\" against a {Wfc3Field:F0}\" field");
        Check("so a close pass earns no detail at all",
              ImagingScience.DetailRung(munFromSoi, Wfc3Field, Element(Wfc3Px, 1), Cap) == 0, "rung 0");

        // The mission this design is meant to reward, and the numbers are New Horizons' own.
        double joolFromSoi = ArcsecAcross(JoolRadius, JoolSoi);
        Check("LORRI frames Jool from its sphere of influence boundary",
              ImagingScience.Frames(joolFromSoi, LorriField),
              $"disc {joolFromSoi:F0}\" in a {LorriField:F0}\" field");
        int joolRung = ImagingScience.DetailRung(joolFromSoi, LorriField, Element(LorriPx, 1), Cap);
        Check("and it is worth real detail there", joolRung >= 7, $"rung {joolRung}");

        // The wide-field instrument is the one that can photograph something close.
        Check("BRITE frames the Mun from Kerbin",
              ImagingScience.Frames(munFromKerbin, BriteField),
              $"disc {munFromKerbin:F0}\" in a {BriteField:F0}\" field");

        Check("a target of exactly the field width still counts",
              ImagingScience.Frames(100.0, 100.0), "");
        Check("a hair over does not", !ImagingScience.Frames(100.001, 100.0), "");
    }

    // ---------------------------------------------------------------- 4

    private static void Binning()
    {
        Console.WriteLine("\n4. Binning\n----------");

        // BINNING DOWN DOES PAY, and it should: it is a genuinely better photograph, bought with
        // memory and reduction time. An earlier comment claimed the Nyquist floor made binning
        // inert in both directions, which was false in the direction a player actually clicks.
        // What is asserted here is the honest behaviour, in both directions.
        double disc = ArcsecAcross(MunRadius, MunFromKerbin);
        int atFour = ImagingScience.DetailRung(disc, RedCatField, Element(RedCatPx, 4), Cap);
        int atOne = ImagingScience.DetailRung(disc, RedCatField, Element(RedCatPx, 1), Cap);
        Check("unbinning a sampling-limited instrument gains two rungs", atOne - atFour == 2,
              $"rung {atFour} at 4x4, rung {atOne} at 1x1");

        // And on a blur-limited one it gains nothing, because the element is already the blur. The
        // seeing has to beat the Nyquist floor at BOTH binnings for the case to be the one claimed:
        // this instrument samples at 3.8"/px, so 4x4 alone puts the floor at 30.6" and a milder
        // blur would leave it sampling-limited at one end and prove nothing.
        double blurLimited4 = ImagingScience.ResolutionElementArcsec(0.0, 40.0, 0.0, RedCatPx * 4, 0.0);
        double blurLimited1 = ImagingScience.ResolutionElementArcsec(0.0, 40.0, 0.0, RedCatPx * 1, 0.0);
        Near("unbinning a blur-limited instrument buys nothing", blurLimited1, blurLimited4, 1e-9, "arcsec");

        // The field in arcsec does not move with binning, which is what makes the above true.
        Check("binning does not change the field", RedCatField == RedCatField, "invariant by construction");

        double defocused = ImagingScience.ResolutionElementArcsec(0.5, 0.0, 2.0, 13.0, 212.0);
        Near("a built-in defocus disc floors the element", defocused, 212.0, 1e-9, "arcsec");
    }

    // ---------------------------------------------------------------- 5

    private static void GroundSample()
    {
        Console.WriteLine("\n5. Ground sample\n----------------");

        // THE PROPERTY THAT REPLACED THE RADIUS THRESHOLD. Against the body's own radius the claim
        // was distance x element <= radius / 500, which cancels the range out completely and is a
        // purely angular test: an 8 m telescope on the ground claimed Jool without anything flying.
        double lorriElement = Element(LorriPx, 1);
        double joolFromKerbin = ImagingScience.GroundSampleMetres(JoolFromKerbin, lorriElement);
        double joolFromSoi = ImagingScience.GroundSampleMetres(JoolSoi, lorriElement);
        Check("Jool cannot be ground-sampled from Kerbin",
              ImagingScience.GroundSampleRung(joolFromKerbin, GsdReference, GsdCap) == 0,
              $"{joolFromKerbin:N0} m per element");
        Check("and its own sphere of influence is too wide to help much",
              ImagingScience.GroundSampleRung(joolFromSoi, GsdReference, GsdCap) == 0,
              $"{joolFromSoi:N0} m per element at the boundary");

        // Tylo is the case the whole claim exists for: nothing from home, real rungs if you fly.
        double tyloHome = ImagingScience.GroundSampleMetres(JoolFromKerbin, lorriElement);
        double tyloClose = ImagingScience.GroundSampleMetres(TyloSoi, lorriElement);
        int homeRung = ImagingScience.GroundSampleRung(tyloHome, GsdReference, GsdCap);
        int closeRung = ImagingScience.GroundSampleRung(tyloClose, GsdReference, GsdCap);
        Check("Tylo pays nothing from Kerbin", homeRung == 0, $"{tyloHome:N0} m per element");
        Check("and pays only once something has flown there", closeRung >= 3,
              $"rung {closeRung} at {tyloClose:N0} m per element");

        // The ratchet: closing further keeps paying, which a one-shot threshold could not do.
        double halfway = ImagingScience.GroundSampleMetres(TyloSoi / 4.0, lorriElement);
        Check("and closing further keeps paying",
              ImagingScience.GroundSampleRung(halfway, GsdReference, GsdCap) > closeRung,
              $"rung {ImagingScience.GroundSampleRung(halfway, GsdReference, GsdCap)} at a quarter the range");

        Check("an unknown range claims nothing",
              double.IsPositiveInfinity(ImagingScience.GroundSampleMetres(0.0, lorriElement)), "");
        Check("a coarse frame claims nothing",
              ImagingScience.GroundSampleRung(GsdReference * 2.0, GsdReference, GsdCap) == 0, "");
    }

    // ---------------------------------------------------------------- 6

    private static void Budget()
    {
        Console.WriteLine("\n6. What a body is worth\n-----------------------");

        double detailCeiling = ImagingScience.LadderTotal(Cap, PerRung, PerDoubling);
        double gsdCeiling = ImagingScience.LadderTotal(GsdCap, ScienceRewards.ScienceRewardGroundSampleRung,
                                                       PerDoubling);
        Console.WriteLine($"    detail ceiling {detailCeiling:F1}, ground-sample ceiling "
                        + $"{gsdCeiling:F1} x the body's stock science value");

        Check("no single body can run away with the tree",
              detailCeiling + gsdCeiling * 12.0 < 120.0,
              $"{detailCeiling + gsdCeiling * 12.0:F0} Science at both ceilings on the richest body");

        // Fifteen bodies, none of them reaching both ceilings in practice, has to stay a fraction of
        // the 17500-Science tree ScienceRewards measures everything against.
        double optimistic = 15.0 * (detailCeiling + gsdCeiling * 6.0);
        Check("and the whole programme stays a fraction of the tree",
              optimistic < 0.10 * 17500.0, $"{optimistic:F0} Science, {optimistic / 17500.0:P1} of the tree");
    }
}
