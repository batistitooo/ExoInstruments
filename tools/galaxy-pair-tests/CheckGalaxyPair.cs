using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// The mutually-covering pair, end to end against the shipped data.
///
/// The shipped GalaxyImages.galimg holds M51 as two maps that each swallowed the other:
/// NGC5194's map lists NGC5195 as a companion and NGC5195's map lists NGC5194. Both claims are
/// TRUE -- each map's pixels really do contain the other galaxy's light, and each map is
/// normalised to the SUM of the two catalogued fluxes. The camera's deposit loop, however, used
/// to skip any galaxy covered by a map that is present in the frame, with no tie-break for the
/// mutual case: both members were skipped and the pair was absent from the photograph.
///
/// The selection loop lives in the Unity layer (SolarSystemCameraTexture.DepositGalaxies) and
/// cannot be compiled headless, so, as in tools/capture-profile, it is reproduced here call for
/// call against the same Core entry points. If the loop in the camera changes, this copy has to
/// follow it.
///
/// What is asserted, in order:
///   1. the shipped data still holds the mutual pair, so the tie-break is load-bearing and this
///      check is measuring the selection rather than a data set that no longer trips it;
///   2. the camera's own cone search of the M51 field sees both members;
///   3. the OLD selection (unconditional skip) deposits neither member -- the bug, reproduced;
///   4. the FIXED selection deposits exactly one member, the brighter catalogued total, and the
///      choice does not depend on the order the search returned the galaxies in;
///   5. the winner's electron total folds in every companion's catalogued flux, so the pair
///      comes out at combined brightness;
///   6. the winner's map actually lands those electrons on a frame that contains it.
///
///   dotnet run -c Release -p:Core=../../ExoInstruments/Core -- [dataDir]
///
/// dataDir defaults to the installed PluginData directory and must hold GalaxyCatalog.galcat
/// and GalaxyImages.galimg.
/// </summary>
static class CheckGalaxyPair
{
    const string A = "NGC5194";       // M51 proper
    const string B = "NGC5195";       // its companion

    // ---- The M51 field as capture-profile points at it: RC20 at 4x4 from OHP ---------------
    const double RaDeg = 202.4696, DecDeg = 47.1952, LatDeg = 43.9308;
    const double Aperture = 0.51, Obstruction = 0.39;
    const double PlateScaleArcsec = 4.63e-6 * 4 / (0.51 * 6.8 * 4.0) * 206264.80624709636;
    const int FrameW = 4144 / 4, FrameH = 2822 / 4;

    // Stand-ins with the L filter's published numbers; every electron figure below is compared
    // against another figure computed through the same response, so only consistency matters.
    const double ExposureSeconds = 300.0;
    const double FieldEBv = 0.035;

    static int failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine("  " + (ok ? "ok    " : "FAILED") + "  " + what);
        if (!ok) failures++;
    }

    static int Main(string[] args)
    {
        string dataDir = args.Length > 0 ? args[0]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
                "GameData/ExoInstruments/PluginData");

        var catalog = new GalaxyCatalog();
        catalog.Load(Path.Combine(dataDir, "GalaxyCatalog.galcat"));
        var images = new GalaxyImageSet();
        images.Load(Path.Combine(dataDir, "GalaxyImages.galimg"));
        Console.WriteLine($"catalogue: {catalog.Count} galaxies; maps: {images.Count}, {images.Source}");

        // ---- 1. The data premise: the pair is mutual --------------------------------------
        Console.WriteLine("\nthe shipped data");
        bool aCovered = images.IsCoveredByAnother(A, out string aOwner);
        bool bCovered = images.IsCoveredByAnother(B, out string bOwner);
        Check(aCovered && string.Equals(aOwner, B, StringComparison.OrdinalIgnoreCase),
              $"{A} is covered by {B}'s map (got owner {aOwner ?? "none"})");
        Check(bCovered && string.Equals(bOwner, A, StringComparison.OrdinalIgnoreCase),
              $"{B} is covered by {A}'s map (got owner {bOwner ?? "none"})");
        Check(catalog.TryGetByName(A, out Galaxy galA), $"{A} is in the catalogue");
        Check(catalog.TryGetByName(B, out Galaxy galB), $"{B} is in the catalogue");
        if (failures > 0) return Done();

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  {0}: B_T {1:F2}   {2}: B_T {3:F2}", A, galA.TotalBMag, B, galB.TotalBMag));

        // ---- 2. The camera's own cone search sees both members ----------------------------
        // Same construction as SearchGalaxyCatalog: the frame's half-diagonal about the
        // reference point; Search itself widens per galaxy by its D25.
        Console.WriteLine("\nthe cone search");
        double halfDiagonalDeg = 0.5 * Math.Sqrt(
            (FrameW * PlateScaleArcsec) * (FrameW * PlateScaleArcsec)
          + (FrameH * PlateScaleArcsec) * (FrameH * PlateScaleArcsec)) / 3600.0;
        List<Galaxy> field = catalog.Search(RaDeg, DecDeg, halfDiagonalDeg, double.PositiveInfinity);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  half-diagonal {0:F4} deg, {1} galaxies in the field", halfDiagonalDeg, field.Count));
        Check(field.Exists(g => Is(g, A)), $"{A} is in the field");
        Check(field.Exists(g => Is(g, B)), $"{B} is in the field");
        if (failures > 0) return Done();

        // ---- 3. The old selection loses the pair ------------------------------------------
        Console.WriteLine("\nthe old selection (unconditional skip)");
        List<Galaxy> old = Select(field, images, catalog, withTieBreak: false);
        Check(!old.Exists(g => Is(g, A)) && !old.Exists(g => Is(g, B)),
              "deposits neither member, which is the bug this data reproduces");

        // ---- 4. The fixed selection deposits exactly one, order-independently -------------
        Console.WriteLine("\nthe fixed selection");
        List<Galaxy> now = Select(field, images, catalog, withTieBreak: true);
        int members = now.FindAll(g => Is(g, A) || Is(g, B)).Count;
        Check(members == 1, $"exactly one member deposits (got {members})");
        if (members != 1) return Done();
        Galaxy winner = now.Find(g => Is(g, A) || Is(g, B));

        string expected = galA.TotalBMag < galB.TotalBMag ? A
                        : galB.TotalBMag < galA.TotalBMag ? B
                        : (string.CompareOrdinal(A, B) < 0 ? A : B);
        Check(Is(winner, expected), $"the winner is {expected}, the brighter catalogued total "
            + $"(got {winner.Name})");

        var reversed = new List<Galaxy>(field);
        reversed.Reverse();
        List<Galaxy> again = Select(reversed, images, catalog, withTieBreak: true);
        Check(again.FindAll(g => Is(g, A) || Is(g, B)).Count == 1
              && Is(again.Find(g => Is(g, A) || Is(g, B)), winner.Name),
              "the same member wins with the search order reversed");

        // ---- 5. The winner's total folds in the companions --------------------------------
        // The same folding TryDepositGalaxyImage performs: the map sums to one and is scaled by
        // the winner's electrons plus every companion's, all through one photometric chain.
        Console.WriteLine("\nthe combined brightness");
        var response = new SystemResponse(552.5e-9, 2650.0, 0.7, null, 0.8, 1.15, 650.0);
        var reddening = new ReddenedResponseCache(response);
        double areaCm2 = Math.PI * 0.25 * Aperture * Aperture * (1.0 - Obstruction * Obstruction) * 1e4;

        GalaxyImage map = images.Fetch(winner.Name);
        Check(map != null && map.Bands != null && map.Bands.Length > 0, "the winner's map loads");
        if (failures > 0) return Done();

        string other = Is(winner, A) ? B : A;
        bool folded = false;
        double own = Electrons(winner, response, reddening, areaCm2);
        double total = own;
        foreach (string companion in map.Companions)
        {
            if (string.Equals(companion, other, StringComparison.OrdinalIgnoreCase)) folded = true;
            if (!catalog.TryGetByName(companion, out Galaxy c)) continue;
            total += Electrons(c, response, reddening, areaCm2);
        }
        Check(folded, $"the map's companion list folds {other} into the total");
        Check(total > own, "the total exceeds the winner's own flux");

        catalog.TryGetByName(other, out Galaxy otherGal);
        foreach (Galaxy g in new[] { winner, otherGal })
        {
            double c = double.IsNaN(g.ColourBv) ? MeanColourForType(g.MorphologicalType) : g.ColourBv;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0}: B_T {1:F2}, B-V {2:F2}{3}, V {4:F2}, {5:E4} e-",
                g.Name, g.TotalBMag, c, double.IsNaN(g.ColourBv) ? " (from type)" : "",
                g.TotalBMag - c, Electrons(g, response, reddening, areaCm2)));
        }
        double pair = own + Electrons(otherGal, response, reddening, areaCm2);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  own {0:E4} e-, with companions {1:E4} e-, the pair alone {2:E4} e-",
            own, total, pair));
        Check(total >= pair * (1.0 - 1e-12),
              "the total holds at least both catalogued fluxes");

        // ---- 6. And the map lands them on a frame that contains it ------------------------
        // A synthetic wide frame rather than the RC20's: the map box is larger than the RC20's
        // field, and what is under test here is that the deposit carries the COMBINED total,
        // not the camera's framing. Same construction as tools/galaxy-image-tests.
        Console.WriteLine("\nthe deposit");
        const int w = 512, h = 384;
        const double fovDeg = 1.0;
        SkyVector boresight = SkyVector.FromHorizontal(map.DecDeg, map.RaDeg);
        SkyVector north = SkyVector.FromHorizontal(map.DecDeg + 0.001, map.RaDeg);
        double dot = north.Dot(boresight);
        SkyVector up = SkyVector.Normalized(north.X - dot * boresight.X,
                                            north.Y - dot * boresight.Y,
                                            north.Z - dot * boresight.Z);
        SkyVector right = SkyVector.Normalized(
            up.Y * boresight.Z - up.Z * boresight.Y,
            up.Z * boresight.X - up.X * boresight.Z,
            up.X * boresight.Y - up.Y * boresight.X);
        var projection = new GnomonicProjection(boresight, up, right, fovDeg, w, h);

        double last = map.Size - 1;
        var mapU = new double[] { 0.0, last, 0.0, last };
        var mapV = new double[] { 0.0, 0.0, last, last };
        var frameX = new double[4];
        var frameY = new double[4];
        bool corners = true;
        for (int i = 0; i < 4; i++)
        {
            map.MapPixelToRaDec(mapU[i], mapV[i], out double ra, out double dec);
            corners &= projection.TryProject(SkyVector.FromHorizontal(dec, ra),
                                             out frameX[i], out frameY[i]);
        }
        Check(corners, "all four map corners project");
        if (failures > 0) return Done();

        double[] transform = GalaxyImageRenderer.SolveFrameToMap(frameX, frameY, mapU, mapV);
        Check(transform != null, "the frame-to-map transform solves");
        if (failures > 0) return Done();

        var plane = new float[w * h];
        double deposited = GalaxyImageRenderer.Deposit(
            plane, w, h, map, transform, 552.5, total, frameX, frameY);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  deposited {0:E4} of {1:E4} e- ({2:P3})", deposited, total, deposited / total));
        Check(deposited > 0.95 * total && deposited < 1.02 * total,
              "the frame receives the combined total, within resampling losses");

        return Done();
    }

    static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    static bool Is(Galaxy g, string name)
        => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One deposit-selection pass, reproducing the loop's coverage decisions call for call:
    /// with the tie-break it is DepositGalaxies as fixed, without it as shipped before the fix.
    /// </summary>
    static List<Galaxy> Select(List<Galaxy> galaxies, GalaxyImageSet images,
                               GalaxyCatalog catalog, bool withTieBreak)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Galaxy g in galaxies) present.Add(g.Name);

        bool CatalogDominates(Galaxy a, string otherName)
        {
            if (!catalog.TryGetByName(otherName, out Galaxy other)) return true;
            if (!double.IsNaN(a.TotalBMag) && !double.IsNaN(other.TotalBMag)
                && Math.Abs(a.TotalBMag - other.TotalBMag) > 1e-9)
                return a.TotalBMag < other.TotalBMag;
            return string.CompareOrdinal(a.Name, otherName) < 0;
        }

        var deposits = new List<Galaxy>();
        foreach (Galaxy g in galaxies)
        {
            if (images.IsCoveredByAnother(g.Name, out string owner) && present.Contains(owner))
            {
                if (!withTieBreak) continue;
                bool mutual = images.IsCoveredByAnother(owner, out string ownersOwner)
                           && string.Equals(ownersOwner, g.Name, StringComparison.OrdinalIgnoreCase);
                if (!mutual || !CatalogDominates(g, owner)) continue;
            }
            deposits.Add(g);
        }
        return deposits;
    }

    /// <summary>The camera's photometric chain for one galaxy, colour fallback included.</summary>
    static double Electrons(Galaxy g, SystemResponse response,
                            ReddenedResponseCache reddening, double areaCm2)
    {
        double colour = g.ColourBv;
        if (double.IsNaN(colour)) colour = MeanColourForType(g.MorphologicalType);
        return StellarPhotometry.CollectedElectrons(
            g.TotalBMag - colour, colour, FieldEBv, response, reddening,
            areaCm2, ExposureSeconds, 1.0);
    }

    // SolarSystemCameraTexture.MeanColourForType, which lives in the Unity layer.
    static double MeanColourForType(double t)
    {
        if (double.IsNaN(t)) return 0.7;
        if (t <= -4.0) return 0.96;
        if (t <= -1.0) return 0.93;
        if (t <= 0.5) return 0.91;
        if (t <= 2.5) return 0.79;
        if (t <= 4.5) return 0.68;
        if (t <= 6.5) return 0.55;
        if (t <= 8.5) return 0.44;
        return 0.39;
    }
}
