using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ExoInstruments.Core;

/// <summary>
/// Pins the radial-velocity semi-amplitude to a published measurement instead of to itself.
///
/// WHY THIS EXISTS. K is the one quantity in the RV path that the catalogue independently
/// publishes: the `k` column is somebody's fitted semi-amplitude with its error bar, measured
/// from the same star the mod is simulating. Every other check on the RV chain (does the
/// detector find the period, does the amplitude come back out) compares the mod against its own
/// prediction, so a wrong K passes all of them. This one does not.
///
/// It caught a real bug. The mass function's mass term is Mp*sin(i), but the loader stored
/// `mass ?? mass_sini` in one field, preferring the TRUE mass wherever the catalogue had one.
/// A true mass read as a minimum mass inflates K by 1/sin(i): 35% on 51 Peg b, 33x on the
/// nearly face-on HD 181720 b. Section 2 is that comparison, section 5 shows it reached the
/// simulated data and not only the readout.
///
///     dotnet run -p:Core=../../ExoInstruments/Core -- [pluginDataDirectory]
/// </summary>
static class RvTests
{
    static int failures;
    static int checks;

    static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        string pluginData = args.Length > 0 ? args[0] : "../../ExoInstruments/PluginData";
        string catalogPath = Path.Combine(pluginData, "ExoplanetCatalog.csv");
        Console.WriteLine("catalogue " + Path.GetFullPath(catalogPath));

        string csv = File.ReadAllText(catalogPath);
        List<StarTarget> catalog = ExoplanetCsvLoader.LoadFromCsv(csv).Targets;
        // The published columns are read straight from the file, independently of the loader,
        // so the reference K stays a measurement even if the loader is the thing that is broken.
        Dictionary<string, double> publishedK = ReadColumn(csv, "k");
        Dictionary<string, double> publishedKError = ReadColumn(csv, "k_error_max");
        Dictionary<string, double> catalogMass = ReadColumn(csv, "mass");
        Dictionary<string, double> catalogMassSini = ReadColumn(csv, "mass_sini");
        Dictionary<string, double> catalogInclination = ReadColumn(csv, "inclination");
        Console.WriteLine($"{catalog.Count} targets loaded, {publishedK.Count} rows carry a published K\n");

        CheckColumnsSurviveTheLoad(catalog, publishedK, publishedKError);
        Check51PegAgainstItsPublishedK(catalog, publishedK, publishedKError, catalogMass, catalogMassSini);
        CheckCatalogueAgainstPublishedK(catalog, publishedK, catalogMass, catalogMassSini);
        CheckProjectionFallback(catalog, publishedK, catalogMass, catalogMassSini, catalogInclination);
        CheckInjectedSignal(catalog, publishedK, publishedKError);
        CheckNothingElseMoved(catalog, catalogMass, catalogMassSini, catalogInclination);

        Console.WriteLine();
        if (failures > 0)
        {
            Console.WriteLine($"{failures} of {checks} CHECKS FAILED");
            Environment.Exit(1);
        }
        Console.WriteLine($"ALL {checks} CHECKS PASSED");
    }

    // =============================================================================================
    static void CheckColumnsSurviveTheLoad(List<StarTarget> catalog, Dictionary<string, double> publishedK,
                                           Dictionary<string, double> publishedKError)
    {
        Section("1. The catalogue's own columns survive the load");

        StarTarget peg = ByName(catalog, "51 Peg b");
        Check("51 Peg b keeps a true mass", Near(peg.PlanetMassJupiter, 0.61, 1e-9), $"{peg.PlanetMassJupiter}");
        Check("51 Peg b keeps a minimum mass, distinct from it",
              Near(peg.PlanetMinimumMassJupiter, 0.46, 1e-9), $"{peg.PlanetMinimumMassJupiter}");
        Check("51 Peg b keeps the published K", Near(peg.PublishedRvSemiAmplitudeMps, 55.77, 1e-9),
              $"{peg.PublishedRvSemiAmplitudeMps}");
        Check("51 Peg b keeps the error bar on it", Near(peg.PublishedRvSemiAmplitudeErrorMps, 0.15, 1e-9),
              $"{peg.PublishedRvSemiAmplitudeErrorMps}");

        int withMinimum = catalog.Count(t => t.PlanetMinimumMassJupiter.HasValue);
        int withK = catalog.Count(t => t.PublishedRvSemiAmplitudeMps.HasValue);
        int withKError = catalog.Count(t => t.PublishedRvSemiAmplitudeErrorMps.HasValue);
        Console.WriteLine($"    {withMinimum} loaded targets carry mass_sini, {withK} carry K, {withKError} carry its error");

        // Every published value in the file must reach a target, or the comparisons below are
        // being run on a subset chosen by a parsing accident.
        int lostK = catalog.Count(t => publishedK.ContainsKey(t.Name) && !t.PublishedRvSemiAmplitudeMps.HasValue);
        Check("no loaded target drops a K the file carries", lostK == 0, $"{lostK} dropped");
        int lostError = catalog.Count(t => publishedKError.ContainsKey(t.Name) && !t.PublishedRvSemiAmplitudeErrorMps.HasValue);
        Check("no loaded target drops a K error bar the file carries", lostError == 0, $"{lostError} dropped");
    }

    // =============================================================================================
    static void Check51PegAgainstItsPublishedK(List<StarTarget> catalog, Dictionary<string, double> publishedK,
                                               Dictionary<string, double> publishedKError,
                                               Dictionary<string, double> mass, Dictionary<string, double> massSini)
    {
        Section("2. 51 Peg b against its published K");

        StarTarget peg = ByName(catalog, "51 Peg b");
        double kPub = publishedK["51 Peg b"];
        double kErr = publishedKError["51 Peg b"];

        double kNow = peg.EstimatedRvSemiAmplitudeMps;
        double kFromTrueMass = SemiAmplitude(mass["51 Peg b"], peg);

        double errNow = PercentError(kNow, kPub);
        double errTrue = PercentError(kFromTrueMass, kPub);

        Console.WriteLine($"    published        {kPub:F2} +/- {kErr:F2} m/s   (Mayor & Queloz 1995 onward)");
        Console.WriteLine($"    from M sin i     {kNow:F2} m/s   ({errNow:F1}% off, mass_sini = {massSini["51 Peg b"]:F2} Mjup)");
        Console.WriteLine($"    from true mass   {kFromTrueMass:F2} m/s   ({errTrue:F1}% off, mass = {mass["51 Peg b"]:F2} Mjup)");

        Check("the shipped formula reproduces the published K to better than 3%", errNow < 3.0, $"{errNow:F2}%");
        // The residual is the catalogue rounding mass_sini to two decimals: 0.4553 Mjup lands on
        // the published K exactly, and 0.46 is as close as two decimals get to it.
        Check("the residual is consistent with mass_sini's own rounding", errNow > 1.0, $"{errNow:F2}%");
        Check("the true mass does not, which is the bug this file pins", errTrue > 20.0, $"{errTrue:F2}%");
    }

    // =============================================================================================
    static void CheckCatalogueAgainstPublishedK(List<StarTarget> catalog, Dictionary<string, double> publishedK,
                                                Dictionary<string, double> mass, Dictionary<string, double> massSini)
    {
        Section("3. The whole catalogue against its published K");

        // One target proves nothing about a formula; a few hundred, none of them chosen, do.
        // Split on whether the two masses actually disagree: where they agree the two formulas
        // are the same formula, and averaging those in would dilute the comparison to nothing.
        var errorsNow = new List<double>();
        var errorsTrue = new List<double>();
        var divergentNow = new List<double>();
        var divergentTrue = new List<double>();
        int divergentWins = 0;
        foreach (StarTarget t in catalog)
        {
            if (!publishedK.TryGetValue(t.Name, out double kPub) || kPub <= 0) continue;
            if (!mass.TryGetValue(t.Name, out double m) || !massSini.TryGetValue(t.Name, out double ms)) continue;
            if (ms <= 0 || !t.IsRvDetectable) continue;

            double eNow = PercentError(t.EstimatedRvSemiAmplitudeMps, kPub);
            double eTrue = PercentError(SemiAmplitude(m, t), kPub);
            errorsNow.Add(eNow);
            errorsTrue.Add(eTrue);
            if (PercentError(m, ms) <= 10.0) continue;
            divergentNow.Add(eNow);
            divergentTrue.Add(eTrue);
            if (eNow < eTrue) divergentWins++;
        }

        Console.WriteLine($"    {errorsNow.Count} entries carry both masses and a published K");
        Console.WriteLine($"    median error   M sin i {Median(errorsNow):F2}%   true mass {Median(errorsTrue):F2}%");
        Console.WriteLine($"    of those, {divergentNow.Count} have masses differing by more than 10%, the entries the bug reached:");
        Console.WriteLine($"      median error {Median(divergentNow):F2}% against {Median(divergentTrue):F2}%");
        Console.WriteLine($"      grossly wrong (>50%) on {divergentNow.Count(e => e > 50.0)} against {divergentTrue.Count(e => e > 50.0)}");
        Console.WriteLine($"      closer to the published value on {divergentWins} of {divergentNow.Count}");

        Check("enough entries to be a population, not an anecdote", errorsNow.Count >= 200, $"{errorsNow.Count}");
        Check("median error under 4% with M sin i", Median(errorsNow) < 4.0, $"{Median(errorsNow):F2}%");
        Check("median error over 6% with the true mass", Median(errorsTrue) > 6.0, $"{Median(errorsTrue):F2}%");
        Check("on the divergent entries, median error under 6% with M sin i", Median(divergentNow) < 6.0,
              $"{Median(divergentNow):F2}%");
        Check("and over 30% with the true mass", Median(divergentTrue) > 30.0, $"{Median(divergentTrue):F2}%");
        Check("M sin i is closer on the large majority of them", divergentWins > 0.8 * divergentNow.Count,
              $"{divergentWins}/{divergentNow.Count}");
        Check("and grossly wrong on a third as many", divergentNow.Count(e => e > 50.0) * 3 <= divergentTrue.Count(e => e > 50.0),
              $"{divergentNow.Count(e => e > 50.0)} vs {divergentTrue.Count(e => e > 50.0)}");

        // The formula itself, on every entry with a published K, whether or not the two masses
        // differ. This is what says the 28.4329 constant and the exponents are right.
        var allErrors = new List<double>();
        foreach (StarTarget t in catalog)
        {
            if (!publishedK.TryGetValue(t.Name, out double kPub) || kPub <= 0) continue;
            if (!t.IsRvDetectable) continue;
            allErrors.Add(PercentError(t.EstimatedRvSemiAmplitudeMps, kPub));
        }
        Console.WriteLine($"    over all {allErrors.Count} entries with a published K: median {Median(allErrors):F2}%");
        Check("median error under 5% across every entry with a published K", Median(allErrors) < 5.0,
              $"{Median(allErrors):F2}%");
    }

    // =============================================================================================
    static void CheckProjectionFallback(List<StarTarget> catalog, Dictionary<string, double> publishedK,
                                        Dictionary<string, double> mass, Dictionary<string, double> massSini,
                                        Dictionary<string, double> inclination)
    {
        Section("4. The projection fallback, where only a true mass exists");

        // Most entries carry a true mass and no mass_sini, and about half of those carry an
        // inclination. Projecting is the one path here with no measurement behind it, so it is
        // checked twice: against the catalogue's own mass_sini where both exist, and against the
        // published K, which is the independent one.
        var errors = new List<double>();
        var lowInclinationErrors = new List<double>();
        foreach (StarTarget t in catalog)
        {
            if (!mass.TryGetValue(t.Name, out double m) || !massSini.TryGetValue(t.Name, out double ms)) continue;
            if (!inclination.TryGetValue(t.Name, out double inc) || ms <= 0) continue;
            if (inc < 0.0 || inc > 180.0) continue;
            double err = PercentError(m * Math.Sin(inc * Math.PI / 180.0), ms);
            errors.Add(err);
            if (inc < 30.0 || inc > 150.0) lowInclinationErrors.Add(err);
        }

        Console.WriteLine($"    mass*sin(i) vs the catalogue's own mass_sini, {errors.Count} entries: median {Median(errors):F2}%");
        Console.WriteLine($"    {lowInclinationErrors.Count} of them nearly face-on (i < 30 or > 150 deg): median {Median(lowInclinationErrors):F3}%, " +
                          $"{lowInclinationErrors.Count(e => e < 0.02)} agreeing to better than 0.02%");
        Check("the projection reproduces mass_sini to better than 1% in the median", Median(errors) < 1.0,
              $"{Median(errors):F2}%");
        // The near-face-on set splits: entries whose true mass was derived from mass_sini agree
        // to five digits, and a few pair a mass and an inclination from different papers, which
        // no rule here can reconcile. The median stays sub-percent through both.
        Check("and to better than 2% on the low-inclination systems", Median(lowInclinationErrors) < 2.0,
              $"{Median(lowInclinationErrors):F4}%");

        // Against the published K: the projection has to earn its place on the entries where it
        // is the only thing available, not merely agree with a column it could be copying.
        var withProjection = new List<double>();
        var withoutProjection = new List<double>();
        foreach (StarTarget t in catalog)
        {
            if (!publishedK.TryGetValue(t.Name, out double kPub) || kPub <= 0) continue;
            if (massSini.ContainsKey(t.Name) || !mass.TryGetValue(t.Name, out double m)) continue;
            if (!inclination.ContainsKey(t.Name) || !t.IsRvDetectable) continue;
            withProjection.Add(PercentError(t.EstimatedRvSemiAmplitudeMps, kPub));
            withoutProjection.Add(PercentError(SemiAmplitude(m, t), kPub));
        }
        Console.WriteLine($"    against the published K on {withProjection.Count} projected entries: " +
                          $"median {Median(withProjection):F2}% projected, {Median(withoutProjection):F2}% unprojected");
        Console.WriteLine($"    grossly wrong (>50%) on {withProjection.Count(e => e > 50.0)} against {withoutProjection.Count(e => e > 50.0)}");
        Check("projecting lowers the median error against the published K",
              Median(withProjection) < Median(withoutProjection),
              $"{Median(withProjection):F2}% vs {Median(withoutProjection):F2}%");
        Check("and leaves fewer entries grossly wrong",
              withProjection.Count(e => e > 50.0) < withoutProjection.Count(e => e > 50.0),
              $"{withProjection.Count(e => e > 50.0)} vs {withoutProjection.Count(e => e > 50.0)}");

        // An inclination outside 0-180 is malformed input, not geometry. Wolf 503 b carries
        // i = -2 with a transit detection, so projecting onto it would cut a real 3 m/s signal
        // to 0.1 m/s: the same class of error as the one this file exists to catch.
        StarTarget wolf = ByName(catalog, "Wolf 503 b");
        double wolfError = PercentError(wolf.EstimatedRvSemiAmplitudeMps, publishedK["Wolf 503 b"]);
        Console.WriteLine($"    Wolf 503 b (i = {wolf.InclinationDeg}, transiting): K = {wolf.EstimatedRvSemiAmplitudeMps:F2} m/s " +
                          $"against a published {publishedK["Wolf 503 b"]:F2} ({wolfError:F1}% off)");
        Check("a malformed inclination is ignored rather than projected onto", wolfError < 10.0, $"{wolfError:F1}%");

        // The three cases of RvMinimumMassJupiter, on constructed targets rather than catalogue
        // rows, so the precedence is pinned even if no shipped entry exercises a branch.
        var withBoth = new StarTarget { PlanetMassJupiter = 2.0, PlanetMinimumMassJupiter = 0.5, InclinationDeg = 30.0 };
        Check("measured mass_sini wins over anything derived", Near(withBoth.RvMinimumMassJupiter, 0.5, 1e-12));

        var projectedOnly = new StarTarget { PlanetMassJupiter = 2.0, InclinationDeg = 30.0 };
        Check("a true mass with an inclination is projected", Near(projectedOnly.RvMinimumMassJupiter, 1.0, 1e-12),
              $"{projectedOnly.RvMinimumMassJupiter}");

        var bare = new StarTarget { PlanetMassJupiter = 2.0 };
        Check("a true mass with no geometry is taken as is (the i = 90 assumption)",
              Near(bare.RvMinimumMassJupiter, 2.0, 1e-12));

        // Retrograde inclinations are in the file, and sin stays positive across 0-180, so an
        // orbit at 150 deg is as inclined as one at 30 and must project the same way.
        var retrograde = new StarTarget { PlanetMassJupiter = 2.0, InclinationDeg = 150.0 };
        Check("a retrograde inclination projects to the same positive mass",
              Near(retrograde.RvMinimumMassJupiter, 1.0, 1e-12), $"{retrograde.RvMinimumMassJupiter}");

        var malformed = new StarTarget { PlanetMassJupiter = 2.0, InclinationDeg = -2.0 };
        Check("an out-of-range inclination is refused", Near(malformed.RvMinimumMassJupiter, 2.0, 1e-12),
              $"{malformed.RvMinimumMassJupiter}");
    }

    // =============================================================================================
    static void CheckInjectedSignal(List<StarTarget> catalog, Dictionary<string, double> publishedK,
                                    Dictionary<string, double> publishedKError)
    {
        Section("5. The injected data, not only the readout");

        // The amplitude is not a display quantity: RvSimulator feeds it into every generated
        // measurement. Simulate a HARPS campaign on 51 Peg and let the mod's own detector fit it
        // back, then compare THAT against the literature. This is the end-to-end statement:
        // observe the mod's 51 Peg with the mod's spectrograph and you recover the real K.
        StarTarget peg = ByName(catalog, "51 Peg b");
        double kPub = publishedK["51 Peg b"];
        double kErr = publishedKError["51 Peg b"];

        var rng = new Random(20260812);
        double cadenceSeconds = 0.37 * 86400.0; // avoids an integer ratio with the 4.23 d period
        List<RvSample> samples = RvSimulator.GenerateSamples(
            peg, Observatories.Harps, 0.0, 120.0 * 86400.0, cadenceSeconds, rng);

        RvDetectionResult result = RvDetector.Detect(samples, 0.5, 100.0);
        double periodError = PercentError(result.BestPeriodDays, peg.PlanetPeriodDays);
        double kError = PercentError(result.BestSemiAmplitudeMps, kPub);

        Console.WriteLine($"    {samples.Count} HARPS epochs over 120 d, noise {RvSimulator.TotalNoiseSigmaMps(peg, Observatories.Harps):F2} m/s");
        Console.WriteLine($"    recovered  P = {result.BestPeriodDays:F4} d   K = {result.BestSemiAmplitudeMps:F2} +/- {result.SemiAmplitudeUncertaintyMps:F2} m/s   SNR {result.Snr:F0}");
        Console.WriteLine($"    published  P = {peg.PlanetPeriodDays:F4} d   K = {kPub:F2} +/- {kErr:F2} m/s");

        Check("the campaign detects the planet", result.Detected, $"SNR {result.Snr:F1}");
        Check("the recovered period is the catalogue period", periodError < 1.0, $"{periodError:F2}%");
        Check("the recovered K matches the published K to better than 5%", kError < 5.0, $"{kError:F2}%");

        // Same campaign, same seed, against a target holding the true mass: what the player used
        // to measure. The point is that no analysis could have recovered the published value.
        var inflated = Clone(peg);
        inflated.PlanetMinimumMassJupiter = null; // falls back to the true mass, no inclination projection
        inflated.InclinationDeg = null;
        List<RvSample> inflatedSamples = RvSimulator.GenerateSamples(
            inflated, Observatories.Harps, 0.0, 120.0 * 86400.0, cadenceSeconds, new Random(20260812));
        RvDetectionResult inflatedResult = RvDetector.Detect(inflatedSamples, 0.5, 100.0);
        double inflatedError = PercentError(inflatedResult.BestSemiAmplitudeMps, kPub);
        Console.WriteLine($"    from the true mass, the same campaign returns K = {inflatedResult.BestSemiAmplitudeMps:F2} m/s ({inflatedError:F1}% off)");
        Check("and the uncorrected signal could not have been fitted back to it", inflatedError > 20.0,
              $"{inflatedError:F2}%");
    }

    // =============================================================================================
    static void CheckNothingElseMoved(List<StarTarget> catalog, Dictionary<string, double> mass,
                                      Dictionary<string, double> massSini, Dictionary<string, double> inclination)
    {
        Section("6. Nothing moves without a reason in the catalogue");

        // Every target whose K changed must have a cause in its own catalogue row: a mass_sini
        // that disagrees with the true mass, or an inclination to project onto. A target with
        // neither must return exactly the number it returned before, to the bit.
        int noReason = 0, noReasonMoved = 0;
        int nearEdgeOn = 0, nearEdgeOnMovedOver1Pct = 0;
        int moved = 0, movedOver10Pct = 0;
        double worst = 0.0;
        string worstName = null;
        foreach (StarTarget t in catalog)
        {
            if (!t.IsRvDetectable || !t.PlanetMassJupiter.HasValue) continue;
            double before = SemiAmplitude(t.PlanetMassJupiter.Value, t);
            double delta = PercentError(t.EstimatedRvSemiAmplitudeMps, before);

            bool hasMinimum = massSini.TryGetValue(t.Name, out double ms) && ms > 0;
            bool hasInclination = inclination.TryGetValue(t.Name, out double inc) && inc >= 0.0 && inc <= 180.0;
            if (!hasMinimum && !hasInclination)
            {
                noReason++;
                if (t.EstimatedRvSemiAmplitudeMps != before) noReasonMoved++;
                continue;
            }
            if (hasInclination && Math.Abs(Math.Sin(inc * Math.PI / 180.0)) > 0.999 && !hasMinimum)
            {
                nearEdgeOn++;
                if (delta > 1.0) nearEdgeOnMovedOver1Pct++;
            }
            if (delta <= 0.0) continue;
            moved++;
            if (delta > 10.0) movedOver10Pct++;
            if (delta > worst) { worst = delta; worstName = t.Name; }
        }

        Console.WriteLine($"    {noReason} targets have neither a mass_sini nor a usable inclination: {noReasonMoved} moved");
        Console.WriteLine($"    {moved} corrected in all, {movedOver10Pct} by more than 10%");
        Console.WriteLine($"    {nearEdgeOn} projected targets are within 2.6 deg of edge-on: {nearEdgeOnMovedOver1Pct} moved by more than 1%");
        Console.WriteLine($"    largest correction: {worstName}, {worst:F0}% lower");

        Check("a target the catalogue says nothing about is bit-identical", noReasonMoved == 0, $"{noReasonMoved} moved");
        // The report's own claim about which planets this reaches: transiting systems have
        // sin(i) within 0.1% of 1, so the projection is a no-op on them by construction.
        Check("edge-on systems are untouched in practice", nearEdgeOnMovedOver1Pct == 0, $"{nearEdgeOnMovedOver1Pct} moved");
        Check("the low-inclination systems move by more than a factor of ten", worst > 900.0, $"{worst:F0}%");
    }

    // =============================================================================================

    /// <summary>
    /// K from the mass function with an arbitrary mass in the numerator, so the harness can ask
    /// what the old code returned without keeping a second copy of the formula's constants.
    /// Mirrors StarTarget.EstimatedRvSemiAmplitudeMps by scaling its result.
    /// </summary>
    static double SemiAmplitude(double massTermJupiter, StarTarget t)
    {
        double mSini = t.RvMinimumMassJupiter ?? 0.0;
        if (mSini <= 0.0) return 0.0;
        // Scaling by m/m is not the identity in floating point, and section 6 asks an
        // exact-equality question, so the unscaled case returns the unscaled value.
        if (massTermJupiter == mSini) return t.EstimatedRvSemiAmplitudeMps;
        return t.EstimatedRvSemiAmplitudeMps * massTermJupiter / mSini;
    }

    static StarTarget Clone(StarTarget t)
    {
        return new StarTarget
        {
            Name = t.Name,
            HostStarName = t.HostStarName,
            StellarMassSolar = t.StellarMassSolar,
            RadiusSolar = t.RadiusSolar,
            ApparentMagnitude = t.ApparentMagnitude,
            EffectiveTempK = t.EffectiveTempK,
            PlanetMassJupiter = t.PlanetMassJupiter,
            PlanetMinimumMassJupiter = t.PlanetMinimumMassJupiter,
            PlanetPeriodDays = t.PlanetPeriodDays,
            Eccentricity = t.Eccentricity,
            InclinationDeg = t.InclinationDeg,
            ArgumentOfPeriastronDeg = t.ArgumentOfPeriastronDeg,
            PlanetPhaseOffset01 = t.PlanetPhaseOffset01,
        };
    }

    /// <summary>
    /// Reads one column keyed by planet name, straight from the CSV text. Deliberately not
    /// ExoplanetCsvLoader: the reference values must not come through the code under test.
    /// </summary>
    static Dictionary<string, double> ReadColumn(string csv, string column)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string[] lines = csv.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        string[] headers = SplitCsv(lines[0]);
        int iName = Array.IndexOf(headers, "name");
        int iColumn = Array.IndexOf(headers, column);
        if (iName < 0 || iColumn < 0) return values;

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            string[] fields = SplitCsv(lines[i]);
            if (fields.Length <= Math.Max(iName, iColumn)) continue;
            string name = fields[iName].Trim();
            string raw = fields[iColumn].Trim();
            if (name.Length == 0 || raw.Length == 0) continue;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) values[name] = v;
        }
        return values;
    }

    /// <summary>The export quotes fields containing commas; the alternate-name lists do.</summary>
    static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { fields.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    static StarTarget ByName(List<StarTarget> catalog, string name)
    {
        StarTarget t = catalog.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (t == null) throw new InvalidOperationException($"{name} is not in the catalogue; the harness needs it");
        return t;
    }

    static double PercentError(double value, double reference)
    {
        return reference == 0.0 ? 0.0 : 100.0 * Math.Abs(value - reference) / Math.Abs(reference);
    }

    static double Median(List<double> values)
    {
        if (values.Count == 0) return 0.0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }

    static bool Near(double? value, double expected, double tolerance)
    {
        return value.HasValue && Math.Abs(value.Value - expected) <= tolerance;
    }

    static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('=', title.Length));
    }

    static void Check(string what, bool ok, string detail = null)
    {
        checks++;
        if (ok)
        {
            Console.WriteLine($"  PASS  {what}");
            return;
        }
        failures++;
        Console.WriteLine($"  FAIL  {what}" + (detail != null ? $"   [{detail}]" : ""));
    }
}
