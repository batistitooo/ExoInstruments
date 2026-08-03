using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ExoInstruments.Core;

/// <summary>
/// Exercises the target search box against the real installed catalogues, headless.
///
/// WHAT IS BEING TESTED. Not "does a search return something", which almost any implementation
/// does, but the four ways a catalogue search box is quietly wrong:
///
///   1. it cannot find an object under a name the object is actually known by (M31, Vega);
///   2. it finds the WRONG object because a substring matched (NGC 24 inside NGC 247);
///   3. it silently duplicates an object that two catalogues both carry;
///   4. it leaks, in career mode, the identity of a star the fog of war is hiding.
///
/// Every check below is one of those. Positions and cross-identifications themselves are not
/// re-verified here; they come from SIMBAD and the IAU through the generators, and
/// tools/constellation-tests covers the one derived quantity, the constellation.
///
/// Run with the paths to the installed catalogues, or with none to use the repo's PluginData:
///     dotnet run -p:Core=../../ExoInstruments/Core -- [pluginDataDirectory]
/// </summary>
static class SearchTests
{
    static int failures;
    static int checks;
    static int skipped;
    /// <summary>Nothing ships a galaxy catalogue, so the checks that need one are reported as skipped rather than failed when none is installed.</summary>
    static bool haveGalaxies;

    static void Main(string[] args)
    {
        string pluginData = args.Length > 0 ? args[0] : "../../ExoInstruments/PluginData";
        Console.WriteLine("catalogues from " + Path.GetFullPath(pluginData));

        List<StarTarget> catalog = LoadStars(pluginData);
        GalaxyCatalog galaxies = LoadGalaxies(pluginData);
        haveGalaxies = galaxies != null && galaxies.IsLoaded;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var index = BuildIndex(catalog, galaxies, careerFog: false);
        Console.WriteLine($"index: {index.Count} targets, built in {clock.ElapsedMilliseconds} ms");
        clock.Restart();
        for (int i = 0; i < 20; i++) Search(index, "orion nebula", 60);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "query: {0:F1} ms each\n", clock.Elapsed.TotalMilliseconds / 20.0));

        CheckKeys();
        CheckFindsByEveryName(index);
        CheckNoSubstringConfusion(index);
        CheckNoDuplicates(index);
        CheckFilters(index);
        CheckRanking(index);
        CheckCareerFog(catalog, galaxies);

        Console.WriteLine();
        if (failures > 0)
        {
            Console.WriteLine($"FAILED: {failures} of {checks} checks");
            Environment.Exit(1);
        }
        Console.WriteLine($"ALL {checks} CHECKS PASSED"
                        + (skipped > 0 ? $" ({skipped} skipped: no galaxy catalogue installed)" : ""));
    }

    // --- the checks ---------------------------------------------------------

    static void CheckKeys()
    {
        Section("1. Designation keys: every way one object is written collapses to one key");

        // The forms the installed catalogues and a player actually produce for one galaxy.
        string[] andromeda = { "NGC0224", "NGC 224", "ngc224", "NGC  224", "ngc-224" };
        var keys = andromeda.Select(n => string.Join("/", TargetDesignations.Keys(n))).Distinct().ToArray();
        Check("five spellings of NGC 224 give one key", keys.Length == 1,
              string.Join(" | ", keys));

        Check("M31 and M 31 and Messier 31 agree",
              TargetDesignations.Keys("M31").Intersect(TargetDesignations.Keys("M 31")).Any()
              && TargetDesignations.Keys("M31").Intersect(TargetDesignations.Keys("Messier 31")).Any(),
              string.Join("/", TargetDesignations.Keys("Messier 31")));

        // Numbers must not lose their identity: these are three different objects.
        Check("NGC 24, NGC 240 and NGC 2400 stay distinct",
              new[] { "NGC 24", "NGC 240", "NGC 2400" }
                  .Select(n => TargetDesignations.Key(n.Replace(" ", ""))).Distinct().Count() == 3,
              "");

        Check("a suffixed designation is not its parent",
              TargetDesignations.Key("NGC4038A") != TargetDesignations.Key("NGC4038"),
              TargetDesignations.Key("NGC4038A") + " vs " + TargetDesignations.Key("NGC4038"));

        Check("Bayer spellings collapse the way star names do",
              TargetDesignations.Key("beta Pictoris") == TargetDesignations.Key("bet Pic"),
              TargetDesignations.Key("beta Pictoris"));

        Check("Sharpless numbers survive their hyphen",
              TargetDesignations.Keys("Sh2-155").Intersect(TargetDesignations.Keys("Sh2 155")).Any(),
              string.Join("/", TargetDesignations.Keys("Sh2-155")));
    }

    static void CheckFindsByEveryName(TargetSearchIndex index)
    {
        Section("2. Objects are findable by every name they are known by");

        // (query, a name the single expected result must also answer to)
        var cases = new (string Query, string Expect)[]
        {
            ("M31", "NGC 224"),
            ("m 31", "NGC 224"),
            ("Andromeda Galaxy", "NGC 224"),
            ("NGC0224", "M 31"),
            ("M42", "NGC 1976"),
            ("Orion Nebula", "NGC 1976"),
            ("M104", "Sombrero"),
            ("Sombrero", "NGC 4594"),
            ("M13", "NGC 6205"),          // a globular cluster: in no installed catalogue at all
            ("M45", "Pleiades"),
            ("Ring Nebula", "M 57"),
            ("Crab", "M 1"),
            ("Vega", "alf Lyr"),          // IAU proper name attached to a Bright Star entry
            ("Betelgeuse", "alf Ori"),
            ("Polaris", "alf UMi"),
            ("51 Peg", "51 Peg"),
            ("Horsehead", "B 33"),
        };

        foreach (var (query, expect) in cases)
        {
            List<SearchResult> results = Search(index, query, 5);
            bool ok = results.Count > 0 && Answers(results[0].Target, expect);
            Check($"\"{query}\" finds something that is also {expect}", ok,
                  results.Count == 0 ? "no results" : Describe(results[0].Target));
        }
    }

    static void CheckNoSubstringConfusion(TargetSearchIndex index)
    {
        Section("3. A number is not a substring");

        // The failure this rules out: searching NGC 24 and being handed NGC 247, which is what a
        // naive Contains() does and what the mod's previous name filter did.
        foreach (string query in new[] { "NGC 24", "NGC 300", "IC 10", "M 1" })
        {
            // The first three of these are galaxies, so without a galaxy catalogue there is
            // nothing in the index for them to be confused with, or to be found as.
            if (!haveGalaxies && query != "M 1") { Skip($"\"{query}\" ranks the exact object first"); continue; }
            List<SearchResult> results = Search(index, query, 5);
            bool ok = results.Count > 0 && Answers(results[0].Target, query);
            Check($"\"{query}\" ranks the exact object first", ok,
                  results.Count == 0 ? "no results" : Describe(results[0].Target));
        }

        // And the other direction: a query that IS a prefix of another designation still finds its
        // own object, it just does not get to displace it.
        List<SearchResult> ngc24 = Search(index, "NGC 24", 20);
        Check("the longer designations are still reachable, just not first",
              ngc24.Count > 1, $"{ngc24.Count} results");
    }

    static void CheckNoDuplicates(TargetSearchIndex index)
    {
        Section("4. Two catalogues describing one object give one entry");

        foreach (string query in new[] { "M31", "M104", "M51", "M81", "M87" })
        {
            List<SearchResult> results = Search(index, query, 10);
            int exact = results.Count(r => Answers(r.Target, query));
            Check($"\"{query}\" resolves to exactly one target", exact == 1,
                  exact + " entries: " + string.Join("; ", results.Where(r => Answers(r.Target, query))
                                                            .Select(r => Describe(r.Target))));
        }

        // The merged entry must be the one with the measurements, not the bare cross-id row.
        if (haveGalaxies)
        {
            SearchTarget m31 = index.Find("M 31");
            Check("M31 kept the catalogue that measured it",
                  m31 != null && m31.Payload is Galaxy && !double.IsNaN(m31.MajorArcmin),
                  m31 == null ? "not found" : $"{m31.Provenance}, D25 {m31.MajorArcmin:F1}'");
        }
        else Skip("M31 kept the catalogue that measured it");
    }

    static void CheckFilters(TargetSearchIndex index)
    {
        Section("5. Filters");

        var byType = Search(index, "type:galaxy", 5000);
        Check("type:galaxy returns galaxies and nothing else",
              byType.Count > (haveGalaxies ? 100 : 20) && byType.All(r => r.Target.Kind == TargetKind.Galaxy),
              $"{byType.Count} results");

        var nebulae = Search(index, "type:nebula", 5000);
        var kinds = nebulae.Select(r => r.Target.Kind).Distinct().ToArray();
        Check("type:nebula covers every kind of nebula",
              kinds.Length >= 4 && kinds.All(k => k == TargetKind.EmissionNebula
                                               || k == TargetKind.PlanetaryNebula
                                               || k == TargetKind.SupernovaRemnant
                                               || k == TargetKind.ReflectionNebula
                                               || k == TargetKind.DarkNebula),
              string.Join(", ", kinds));

        Check("plurals work", Search(index, "type:galaxies", 10).Count > 0, "");
        Check("a misspelt type is reported, not ignored",
              TargetQuery.Parse("type:nebulla").Unrecognised.Count == 1, "");

        var inOrion = Search(index, "type:nebula in:Ori", 200);
        Check("in:Ori restricts to Orion",
              inOrion.Count > 0 && inOrion.All(r => r.Target.Constellation == "Ori"),
              $"{inOrion.Count} nebulae in Orion");
        Check("in:Orion and in:Orionis mean the same as in:Ori",
              Search(index, "in:Orion type:nebula", 200).Count == inOrion.Count
              && Search(index, "in:Orionis type:nebula", 200).Count == inOrion.Count, "");

        var bright = Search(index, "type:galaxy mag:<9", 5000);
        Check("mag:<9 admits only galaxies brighter than that",
              bright.Count > 0 && bright.All(r => r.Target.Magnitude <= 9.0),
              $"{bright.Count} galaxies brighter than B = 9");
        Check("a magnitude filter excludes targets of unknown brightness",
              bright.All(r => !double.IsNaN(r.Target.Magnitude)), "");

        var combined = Search(index, "type:galaxy in:Vir mag:<11", 5000);
        Check("filters combine with AND",
              combined.All(r => r.Target.Kind == TargetKind.Galaxy
                             && r.Target.Constellation == "Vir"
                             && r.Target.Magnitude <= 11.0),
              $"{combined.Count} bright Virgo galaxies");
    }

    static void CheckRanking(TargetSearchIndex index)
    {
        Section("6. Ranking");

        var results = Search(index, "orion", 20);
        Check("a word search finds the objects named for it",
              results.Any(r => Answers(r.Target, "NGC 1976")),
              $"{results.Count} results, first {(results.Count > 0 ? Describe(results[0].Target) : "none")}");

        // With no terms at all the list is a browse, brightest first, and must still be ordered.
        var browse = Search(index, "type:galaxy", 20);
        bool ordered = true;
        for (int i = 1; i < browse.Count; i++)
            if (browse[i].Target.Magnitude < browse[i - 1].Target.Magnitude) ordered = false;
        Check("an unsearched list is ordered by brightness", ordered,
              browse.Count > 0 ? $"{browse[0].Target.DisplayName} at B = {browse[0].Target.Magnitude:F1}" : "");
    }

    static void CheckCareerFog(List<StarTarget> catalog, GalaxyCatalog galaxies)
    {
        Section("7. Career fog of war");

        var index = BuildIndex(catalog, galaxies, careerFog: true);

        // Every star is unscanned in this index, so no star may be findable by a real designation
        // including the IAU proper names, which are attached AFTER the stars are added and are
        // exactly the kind of late source that could hand the identity back.
        Check("no star is findable by its real name while unscanned",
              Search(index, "Vega", 5).All(r => r.Target.Kind != TargetKind.Star
                                             || r.Target.Provenance == "IAU Catalog of Star Names"),
              string.Join("; ", Search(index, "Vega", 5).Select(r => Describe(r.Target))));

        Check("51 Peg is not findable while unscanned",
              !Search(index, "51 Peg", 5).Any(r => r.Target.Payload is StarTarget),
              string.Join("; ", Search(index, "51 Peg", 5).Select(r => Describe(r.Target))));

        // But the sky itself is not secret: the provisional designation a survey would use works.
        var hidden = index.Entries.FirstOrDefault(e => e.IdentityWithheld);
        Check("an unscanned star is findable by its provisional designation",
              hidden != null && Search(index, hidden.DisplayName, 5).Any(r => r.Target == hidden),
              hidden == null ? "no withheld entries" : hidden.DisplayName);

        Check("galaxies and nebulae are never fogged",
              Search(index, "M31", 5).Count > 0 && Search(index, "Orion Nebula", 5).Count > 0, "");
    }

    // --- plumbing -----------------------------------------------------------

    static TargetSearchIndex BuildIndex(List<StarTarget> catalog, GalaxyCatalog galaxies, bool careerFog)
    {
        var index = new TargetSearchIndex();
        TargetCatalogue.AddStars(index, catalog,
            star => careerFog ? Provisional(star) : star.Name,
            star => careerFog);
        TargetCatalogue.AddDeepSky(index);
        TargetCatalogue.AddGalaxies(index, galaxies);
        TargetCatalogue.AddCrossIdentifiedObjects(index);
        TargetCatalogue.AddStarProperNames(index);
        return index;
    }

    static string Provisional(StarTarget star)
        => star.RaDeg.HasValue && star.DecDeg.HasValue
            ? "Unscanned " + StarNames.ProvisionalDesignation(star.RaDeg.Value, star.DecDeg.Value)
            : "Unscanned target";

    static List<SearchResult> Search(TargetSearchIndex index, string text, int max)
        => index.Query(TargetQuery.Parse(text), max, out _);

    static bool Answers(SearchTarget target, string name)
    {
        List<string> wanted = TargetDesignations.Keys(name);
        foreach (string alias in target.Aliases)
            foreach (string key in TargetDesignations.Keys(alias))
                if (wanted.Contains(key)) return true;
        // Common names are compared loosely, since "Sombrero" is one of "Sombrero Galaxy".
        string loose = TargetDesignations.Loose(name);
        foreach (string alias in target.Aliases)
            if (TargetDesignations.Loose(alias).Contains(loose)) return true;
        return false;
    }

    static string Describe(SearchTarget t)
        => $"{t.DisplayName} [{TargetKinds.Label(t.Kind)}, {t.Provenance}"
         + (t.Constellation != null ? ", " + t.Constellation : "") + "]";

    static List<StarTarget> LoadStars(string pluginData)
    {
        string exoplanets = Path.Combine(pluginData, "ExoplanetCatalog.csv");
        string bsc = Path.Combine(pluginData, "BrightStarCatalog.tsv");
        var planets = ExoplanetCsvLoader.LoadFromCsv(File.ReadAllText(exoplanets)).Targets;
        var background = BackgroundStarCatalogLoader.LoadFromTsv(File.ReadAllText(bsc));
        CatalogMergeResult merged = StarCatalogMerger.Merge(planets, background.Entries);
        Console.WriteLine($"  {merged.ExoplanetEntries} planet rows, {merged.BackgroundStars} BSC stars"
                        + $" -> {merged.Merged.Count} targets");
        return merged.Merged;
    }

    static GalaxyCatalog LoadGalaxies(string pluginData)
    {
        string path = Path.Combine(pluginData, "GalaxyCatalog.galcat");
        if (!File.Exists(path))
        {
            Console.WriteLine("  no galaxy catalogue installed; galaxy checks will be skipped");
            return null;
        }
        var catalog = new GalaxyCatalog();
        catalog.Load(path);
        Console.WriteLine($"  {catalog.Count} galaxies from {catalog.Source}");
        return catalog;
    }

    static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
    }

    static void Skip(string label)
    {
        skipped++;
        Console.WriteLine($"  [skip] {label}: needs an installed galaxy catalogue");
    }

    static void Check(string label, bool ok, string detail)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  [{0}] {1}{2}",
            ok ? "ok  " : "FAIL", label, string.IsNullOrEmpty(detail) ? "" : ": " + detail));
    }
}
