using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ExoInstruments.Core
{
    /// <summary>What one star-field search read, and whether it stopped short of the depth asked for.</summary>
    public sealed class StarFieldPlan
    {
        /// <summary>File name of the catalogue or tier the stars came from.</summary>
        public string Source;

        /// <summary>Faintest V asked for.</summary>
        public double RequestedVMag;

        /// <summary>Faintest V searched: the limit asked for, or a shallower catalogue depth or tier cut.</summary>
        public double LimitVMag;

        /// <summary>True when the limit asked for would have read more records than the budget allows.</summary>
        public bool LimitedByBudget;

        /// <summary>True when the limit asked for is fainter than the deepest file installed holds.</summary>
        public bool LimitedByCatalogue;

        /// <summary>True when even the shallowest file available reads more than the budget.</summary>
        public bool OverBudget;

        /// <summary>Records the search reads.</summary>
        public long Candidates;

        /// <summary>Records the search was allowed to read.</summary>
        public long Budget;

        internal int Level;
    }

    /// <summary>
    /// A star catalogue with the magnitude tiers tools/tier_star_catalog.py cuts from it. NAME.V17.starcat holds
    /// exactly the main file's stars at V 17 or brighter, in the same order, so the shallowest tier deep enough
    /// returns the same stars from far fewer records. Without the main file the deepest consistent tier is the base.
    /// </summary>
    public sealed class TieredStarCatalog : IDisposable
    {
        private sealed class Level
        {
            public RenderedStarCatalog Catalog;
            public string Name;
            public string Path;
            public double CutVMag;
        }

        private const string Extension = ".starcat";

        // Shallowest first. The last is the base: the main file with no cut, or without one the deepest tier.
        private readonly List<Level> levels = new List<Level>();

        /// <summary>Files that were found and not used, one sentence each.</summary>
        public List<string> Report { get; } = new List<string>();

        /// <summary>Stars in the base file: the main catalogue, or the deepest tier without one.</summary>
        public int Count => levels.Count > 0 ? levels[levels.Count - 1].Catalog.Count : 0;

        public bool IsLoaded => levels.Count > 0 && levels[levels.Count - 1].Catalog.IsLoaded;

        /// <summary>File name of the base: the main catalogue, or the deepest tier without one.</summary>
        public string BaseName => levels.Count > 0 ? levels[levels.Count - 1].Name : null;

        /// <summary>Faintest V the catalogue holds: infinite with the main file, the base tier's cut without it.</summary>
        public double DepthVMag => levels.Count > 0 ? levels[levels.Count - 1].CutVMag : double.PositiveInfinity;

        /// <summary>True when no main file is installed, so no search goes past DepthVMag.</summary>
        public bool IsDepthLimited => !double.IsPositiveInfinity(DepthVMag);

        /// <summary>Magnitude cuts of the tiers in use below the base, shallowest first.</summary>
        public List<double> TierCuts
        {
            get
            {
                var cuts = new List<double>();
                for (int i = 0; i < levels.Count - 1; i++) cuts.Add(levels[i].CutVMag);
                return cuts;
            }
        }

        /// <summary>
        /// Maps the catalogue at path and every valid tier beside it; refused files go to Report. Without the main
        /// file a tier is the base, and FileNotFoundException means there is none. Throws on a malformed base.
        /// </summary>
        public void Load(string path)
        {
            Dispose();
            Report.Clear();
            string mainName = System.IO.Path.GetFileName(path);

            RenderedStarCatalog main = null;
            if (File.Exists(path))
            {
                main = new RenderedStarCatalog();
                try
                {
                    main.Load(path);
                }
                catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
                {
                    // File.Exists is true for a link whose target has moved.
                    main.Dispose();
                    main = null;
                    Report.Add($"{mainName}: the file it names is not there, as when a link's target has moved; "
                             + "treated as not installed.");
                }
            }

            string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);

            var candidates = new List<Level>();
            string[] found;
            try
            {
                found = Directory.GetFiles(directory, stem + ".V*" + Extension);
            }
            catch (Exception e)
            {
                Report.Add($"tiers beside {mainName} could not be listed ({e.Message}); none used.");
                found = new string[0];
            }
            Array.Sort(found, StringComparer.Ordinal);
            foreach (string tierPath in found)
            {
                string name = System.IO.Path.GetFileName(tierPath);
                string cutText = name.Substring(stem.Length + 2, name.Length - stem.Length - 2 - Extension.Length);
                if (!double.TryParse(cutText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double cut))
                {
                    Report.Add($"{name}: its name gives no magnitude cut; ignored.");
                    continue;
                }
                candidates.Add(new Level { Name = name, Path = tierPath, CutVMag = cut });
            }

            if (main == null)
            {
                if (candidates.Count == 0)
                {
                    throw new FileNotFoundException(
                        $"neither {mainName} nor a magnitude tier beside it is installed"
                        + (Report.Count > 0 ? " (" + string.Join(" ", Report) + ")" : ""), path);
                }
                LoadTiersOnly(mainName, candidates);
                return;
            }

            var baseLevel = new Level { Catalog = main, Name = mainName, CutVMag = double.PositiveInfinity };
            var tiers = new List<Level>();
            foreach (Level candidate in candidates)
            {
                double cut = candidate.CutVMag;
                if (tiers.Exists(t => t.CutVMag == cut))
                {
                    Report.Add($"{candidate.Name}: a second tier at V {cut}; ignored.");
                    continue;
                }

                var tier = new RenderedStarCatalog();
                string fault;
                try
                {
                    tier.Load(candidate.Path);
                    fault = TierFault(main, "the main catalogue", tier, cut);
                }
                catch (Exception e)
                {
                    fault = "failed to load (" + e.Message + ")";
                }
                if (fault != null)
                {
                    Report.Add($"{candidate.Name}: {fault}; ignored.");
                    tier.Dispose();
                    continue;
                }
                candidate.Catalog = tier;
                tiers.Add(candidate);
            }

            tiers.Sort((a, b) => a.CutVMag.CompareTo(b.CutVMag));
            levels.AddRange(tiers);
            levels.Add(baseLevel);
        }

        // Without a main file the base is the tier most of the shallower ones agree with, the deeper on a tie, so a
        // stray deep file cannot demote a good install. Its cut is as deep as any search goes.
        private void LoadTiersOnly(string mainName, List<Level> candidates)
        {
            var loaded = new List<Level>();
            foreach (Level candidate in candidates)
            {
                if (loaded.Exists(t => t.CutVMag == candidate.CutVMag))
                {
                    Report.Add($"{candidate.Name}: a second tier at V {candidate.CutVMag}; ignored.");
                    continue;
                }
                var catalog = new RenderedStarCatalog();
                string fault;
                try
                {
                    catalog.Load(candidate.Path);
                    fault = catalog.IsLoaded ? null : "it holds no stars";
                }
                catch (Exception e)
                {
                    fault = "failed to load (" + e.Message + ")";
                }
                if (fault != null)
                {
                    Report.Add($"{candidate.Name}: {fault}; ignored.");
                    catalog.Dispose();
                    continue;
                }
                candidate.Catalog = catalog;
                loaded.Add(candidate);
            }
            if (loaded.Count == 0)
            {
                throw new InvalidDataException(
                    $"{mainName} is not installed and no magnitude tier beside it loads: " + string.Join(" ", Report));
            }

            // Deepest first. A candidate with no more tiers below it than the best agreement so far cannot win.
            loaded.Sort((a, b) => b.CutVMag.CompareTo(a.CutVMag));
            var agreed = new int[loaded.Count];
            string[] baseFaults = null;
            int best = 0;
            for (int b = 0; b < loaded.Count && (b == 0 || loaded.Count - 1 - b > agreed[best]); b++)
            {
                string label = "the V " + loaded[b].CutVMag.ToString(CultureInfo.InvariantCulture) + " tier";
                var faults = new string[loaded.Count];
                for (int t = b + 1; t < loaded.Count; t++)
                {
                    try
                    {
                        faults[t] = TierFault(loaded[b].Catalog, label, loaded[t].Catalog, loaded[t].CutVMag);
                    }
                    catch (Exception e)
                    {
                        faults[t] = "could not be checked (" + e.Message + ")";
                    }
                    if (faults[t] == null) agreed[b]++;
                }
                if (b == 0 || agreed[b] > agreed[best])
                {
                    best = b;
                    baseFaults = faults;
                }
            }

            Level baseLevel = loaded[best];
            for (int b = 0; b < best; b++)
            {
                Report.Add($"{loaded[b].Name}: it agrees with {agreed[b]} of the {loaded.Count - 1 - b} tiers below it, "
                         + $"where {baseLevel.Name} agrees with {agreed[best]}; ignored.");
                loaded[b].Catalog.Dispose();
            }
            // Shallowest first.
            for (int t = loaded.Count - 1; t > best; t--)
            {
                if (baseFaults[t] == null)
                {
                    levels.Add(loaded[t]);
                    continue;
                }
                Report.Add($"{loaded[t].Name}: {baseFaults[t]}; ignored.");
                loaded[t].Catalog.Dispose();
            }
            levels.Add(baseLevel);
        }

        /// <summary>
        /// Picks the file for a search: the shallowest complete to limitVMag or, when that reads more than
        /// maxCandidates, the deepest shallower tier that fits, so the whole field gets shallower.
        /// </summary>
        public StarFieldPlan Plan(double centreRaDeg, double centreDecDeg, double radiusDeg,
                                  double limitVMag, long maxCandidates)
        {
            if (!IsLoaded) return null;

            int complete = levels.Count - 1;
            for (int i = 0; i < levels.Count - 1; i++)
            {
                if (limitVMag <= levels[i].CutVMag) { complete = i; break; }
            }

            for (int i = complete; ; i--)
            {
                long candidates = levels[i].Catalog.CountCandidates(centreRaDeg, centreDecDeg, radiusDeg);
                if (candidates <= maxCandidates || i == 0)
                {
                    return new StarFieldPlan
                    {
                        Source = levels[i].Name,
                        RequestedVMag = limitVMag,
                        LimitVMag = i == complete ? Math.Min(limitVMag, levels[i].CutVMag) : levels[i].CutVMag,
                        LimitedByBudget = i != complete,
                        LimitedByCatalogue = limitVMag > DepthVMag,
                        OverBudget = candidates > maxCandidates,
                        Budget = maxCandidates,
                        Candidates = candidates,
                        Level = i,
                    };
                }
            }
        }

        /// <summary>Plans the search, then hands every star it finds to visit.</summary>
        public StarFieldPlan Search(double centreRaDeg, double centreDecDeg, double radiusDeg,
                                    double limitVMag, long maxCandidates, Action<RenderedStar> visit)
        {
            StarFieldPlan plan = Plan(centreRaDeg, centreDecDeg, radiusDeg, limitVMag, maxCandidates);
            if (plan != null)
                levels[plan.Level].Catalog.Search(centreRaDeg, centreDecDeg, radiusDeg, plan.LimitVMag, visit);
            return plan;
        }

        public void Dispose()
        {
            foreach (Level level in levels) level.Catalog.Dispose();
            levels.Clear();
        }

        // Cheap checks that a tier is what its name says: the same bands, no band fuller than the base
        // file's, and in sixteen small fields over the sphere exactly the base file's stars down to the cut.
        private static string TierFault(RenderedStarCatalog main, string mainLabel, RenderedStarCatalog tier, double cut)
        {
            if (!tier.IsLoaded) return "it holds no stars";
            if (tier.BandCount != main.BandCount || tier.BandWidthDeg != main.BandWidthDeg)
                return $"its declination bands differ from {mainLabel}'s";
            if (tier.Count >= main.Count)
                return $"it holds {tier.Count:N0} stars, no fewer than {mainLabel}'s {main.Count:N0}";
            for (int band = 0; band < main.BandCount; band++)
            {
                if (tier.StarsInBand(band) > main.StarsInBand(band))
                    return $"declination band {band} holds more stars than {mainLabel}'s";
            }

            const int fields = 16;
            const double radiusDeg = 0.2;
            for (int i = 0; i < fields; i++)
            {
                double dec = Math.Asin(1.0 - 2.0 * (i + 0.5) / fields) * 180.0 / Math.PI;
                double ra = 360.0 * ((i * 0.61803398875) % 1.0);
                var inMain = new Fingerprint();
                var inTier = new Fingerprint();
                main.Search(ra, dec, radiusDeg, cut, inMain.Add);
                tier.Search(ra, dec, radiusDeg, 99.0, inTier.Add);
                if (inMain.Stars != inTier.Stars || inMain.Sum != inTier.Sum)
                {
                    return $"near RA {ra:F1} Dec {dec:F1} it holds {inTier.Stars:N0} stars where {mainLabel} "
                         + $"has {inMain.Stars:N0} at V {cut} or brighter";
                }
            }
            return null;
        }

        // Same records in the same order give the same sum to the last bit.
        private sealed class Fingerprint
        {
            public long Stars;
            public double Sum;

            public void Add(RenderedStar star)
            {
                Stars++;
                Sum += star.RaDeg + 3.0 * star.DecDeg + 7.0 * star.VMag;
            }
        }
    }
}
