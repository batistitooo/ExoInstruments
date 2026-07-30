using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    public class CatalogMergeResult
    {
        public List<StarTarget> Merged { get; set; }

        public int ExoplanetEntries { get; set; }
        public int ExoplanetHosts { get; set; }
        public int BackgroundStars { get; set; }
        public int DecoysKept { get; set; }

        public int MatchedByHd { get; set; }
        public int MatchedByHr { get; set; }
        public int MatchedByName { get; set; }
        public int MatchedByPosition { get; set; }

        /// <summary>Hosts whose missing star_teff was backfilled from their consumed BSC entry's B-V-derived temperature.</summary>
        public int TeffBackfilled { get; set; }

        /// <summary>One line per resolved match ("51 peg -> HR 8729 via HD 217014"), for the merge harness.</summary>
        public List<string> MatchLog { get; set; } = new List<string>();

        /// <summary>
        /// Name keys that mapped to more than one BSC star and were therefore
        /// refused (the host fell through to positional matching). Every entry
        /// here deserves a manual look; an over-eager guess in this situation
        /// is exactly how a real host could survive as a phantom decoy.
        /// </summary>
        public List<string> AmbiguousNameKeys { get; set; } = new List<string>();

        /// <summary>
        /// exoplanet.eu hosts bright enough to plausibly be in BSC (V &lt;=
        /// BrightHostReviewMagnitude) that matched nothing. Either genuinely
        /// absent from BSC or a missed dedup; the harness prints these for
        /// human review rather than silently trusting the miss.
        /// </summary>
        public List<string> UnmatchedBrightHosts { get; set; } = new List<string>();
    }

    /// <summary>
    /// Merges exoplanet.eu with the Bright Star Catalogue into one duplicate-free list.
    /// Key invariant: a known planet host must never appear as a decoy. Matching is
    /// layered strongest-first: (1) HD number, (2) HR number, (3) normalized Bayer/Flamsteed
    /// name (refused if ambiguous), (4) positional fallback within PositionalToleranceArcsec
    /// with a magnitude sanity check.
    /// </summary>
    public static class StarCatalogMerger
    {
        /// <summary>Positional match radius (20 arcsec). Absorbs exoplanet.eu's RA rounding (~13 arcsec drift on 51 Peg) while staying below resolved binary separations — verified against 83 Leo A/B (27 arcsec apart).</summary>
        public const double PositionalToleranceArcsec = 20.0;

        /// <summary>Magnitude sanity check on positional matches — loose because exoplanet.eu may report a different band for red stars, but 3 magnitudes off is a different star.</summary>
        public const double MagnitudeSanityTolerance = 1.5;

        /// <summary>BSC is nominally complete to V = 6.5 with stragglers slightly fainter; review unmatched hosts up to here.</summary>
        public const double BrightHostReviewMagnitude = 6.7;

        public static CatalogMergeResult Merge(List<StarTarget> exoplanetTargets, List<BackgroundStarEntry> backgroundStars)
        {
            var result = new CatalogMergeResult
            {
                ExoplanetEntries = exoplanetTargets.Count,
                BackgroundStars = backgroundStars.Count,
            };

            // --- Index the background catalog ---------------------------------
            // HR numbers are unique by construction. HD numbers and name keys can
            // legitimately collide (binary components sharing one HD entry or one
            // Bayer name), hence lists.
            var byHr = new Dictionary<int, BackgroundStarEntry>();
            var byHd = new Dictionary<int, List<BackgroundStarEntry>>();
            var byNameKey = new Dictionary<string, List<BackgroundStarEntry>>();
            foreach (var entry in backgroundStars)
            {
                byHr[entry.HrNumber] = entry;
                if (entry.HdNumber.HasValue)
                {
                    if (!byHd.TryGetValue(entry.HdNumber.Value, out var hdList))
                        byHd[entry.HdNumber.Value] = hdList = new List<BackgroundStarEntry>();
                    hdList.Add(entry);
                }
                foreach (string key in entry.NameKeys)
                {
                    if (!byNameKey.TryGetValue(key, out var nameList))
                        byNameKey[key] = nameList = new List<BackgroundStarEntry>();
                    nameList.Add(entry);
                }
            }

            // Group planets by host: all identifier evidence counts (tau Cet's HD only appears on planet alternate names).
            var hosts = new Dictionary<string, List<StarTarget>>();
            var hostOrder = new List<string>();
            foreach (var target in exoplanetTargets)
            {
                string hostKey = target.CatalogKey
                    ?? StarNames.CatalogKeyForHost(target.HostStarName, target.Name);
                if (!hosts.TryGetValue(hostKey, out var group))
                {
                    hosts[hostKey] = group = new List<StarTarget>();
                    hostOrder.Add(hostKey);
                }
                group.Add(target);
            }
            result.ExoplanetHosts = hosts.Count;

            // --- Match each host, strongest evidence first ---------------------
            var consumed = new HashSet<BackgroundStarEntry>();
            foreach (string hostKey in hostOrder)
            {
                var planets = hosts[hostKey];
                BackgroundStarEntry match = null;
                string via = null;

                var designations = CollectDesignations(planets);

                foreach (int hd in StarNames.ExtractHdNumbers(designations))
                {
                    if (byHd.TryGetValue(hd, out var candidates))
                    {
                        match = PickClosest(candidates, planets[0]);
                        via = "HD " + hd;
                        result.MatchedByHd++;
                        break;
                    }
                }

                if (match == null)
                {
                    foreach (int hr in StarNames.ExtractHrNumbers(designations))
                    {
                        if (byHr.TryGetValue(hr, out var candidate))
                        {
                            match = candidate;
                            via = "HR " + hr;
                            result.MatchedByHr++;
                            break;
                        }
                    }
                }

                if (match == null)
                {
                    foreach (string nameKey in CollectNameKeys(planets, hostKey))
                    {
                        if (!byNameKey.TryGetValue(nameKey, out var candidates)) continue;
                        if (candidates.Count > 1)
                        {
                            // Two BSC stars answer to this name (shared binary
                            // designation). Guessing is how a phantom decoy gets
                            // made; record it and let position decide instead.
                            result.AmbiguousNameKeys.Add($"{hostKey}: name key '{nameKey}' matches {candidates.Count} BSC entries");
                            continue;
                        }
                        match = candidates[0];
                        via = $"name '{nameKey}'";
                        result.MatchedByName++;
                        break;
                    }
                }

                // Positional fallback forbidden for non-primary components ("83 Leo B"): the BSC
                // entry there is the primary, not the planet host. A B component in BSC has its
                // own HD number and must match through identifiers (16 Cyg B → HD 186427).
                if (match == null && !IsNonPrimaryComponent(planets))
                {
                    match = NearestWithinTolerance(backgroundStars, planets[0]);
                    if (match != null)
                    {
                        via = $"position ({SeparationArcsec(match.Target, planets[0]):F1} arcsec)";
                        result.MatchedByPosition++;
                    }
                }

                if (match != null)
                {
                    result.MatchLog.Add($"{hostKey} -> HR {match.HrNumber} ({match.Target.Name}) via {via}");
                    consumed.Add(match);

                    // Backfill Teff from BSC B-V when exoplanet.eu forgot it (HR 8799) — without a
                    // temperature the direct-imaging pipeline can't compute contrast and reports missing data.
                    if (match.DerivedTeffK.HasValue)
                    {
                        bool backfilled = false;
                        foreach (var planet in planets)
                        {
                            if (planet.EffectiveTempK.HasValue) continue;
                            planet.EffectiveTempK = match.DerivedTeffK;
                            planet.EffectiveTempDerivedFromColor = true;
                            backfilled = true;
                        }
                        if (backfilled) result.TeffBackfilled++;
                    }
                }
                else if (planets[0].ApparentMagnitude <= BrightHostReviewMagnitude)
                {
                    result.UnmatchedBrightHosts.Add(
                        $"{hostKey} (V={planets[0].ApparentMagnitude:F2}, {planets[0].Name})");
                }
            }

            // --- Assemble: every real planet entry, plus unconsumed decoys -----
            var merged = new List<StarTarget>(exoplanetTargets.Count + backgroundStars.Count);
            merged.AddRange(exoplanetTargets);
            foreach (var entry in backgroundStars)
            {
                if (consumed.Contains(entry)) continue;
                merged.Add(entry.Target);
                result.DecoysKept++;
            }
            result.Merged = merged;
            return result;
        }

        private static string[] CollectDesignations(List<StarTarget> planets)
        {
            var designations = new List<string>(planets.Count * 4);
            foreach (var p in planets)
            {
                designations.Add(p.HostStarName);
                designations.Add(p.HostStarAlternateNames);
                designations.Add(p.Name);
                designations.Add(p.PlanetAlternateNames);
            }
            return designations.ToArray();
        }

        /// <summary>Normalized name variants for the host: own name + alternate names, with trailing " a" stripped ("tau boo a" → "tau boo"). Never strips " b"/" c" — those may be distinct BSC entries.</summary>
        private static List<string> CollectNameKeys(List<StarTarget> planets, string hostKey)
        {
            var keys = new List<string> { hostKey };
            foreach (var p in planets)
            {
                AddNameKey(keys, StarNames.Normalize(p.HostStarName));
                if (p.HostStarAlternateNames != null)
                {
                    foreach (string alt in p.HostStarAlternateNames.Split(','))
                        AddNameKey(keys, StarNames.Normalize(alt));
                }
            }
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                if (keys[i].EndsWith(" a"))
                    AddNameKey(keys, keys[i].Substring(0, keys[i].Length - 2));
            }
            return keys;
        }

        private static void AddNameKey(List<string> keys, string key)
        {
            if (key != null && !keys.Contains(key)) keys.Add(key);
        }

        /// <summary>True when any host-level designation ends in an explicit non-primary component letter (" b"/" c"/" d" after normalization).</summary>
        private static bool IsNonPrimaryComponent(List<StarTarget> planets)
        {
            foreach (var p in planets)
            {
                if (EndsWithComponentLetter(StarNames.Normalize(p.HostStarName))) return true;
                if (p.HostStarAlternateNames == null) continue;
                foreach (string alt in p.HostStarAlternateNames.Split(','))
                    if (EndsWithComponentLetter(StarNames.Normalize(alt))) return true;
            }
            return false;
        }

        private static bool EndsWithComponentLetter(string normalizedKey)
        {
            return normalizedKey != null &&
                   (normalizedKey.EndsWith(" b") || normalizedKey.EndsWith(" c") || normalizedKey.EndsWith(" d"));
        }

        /// <summary>Of several candidates sharing an HD number (binary components), take the positionally closest; first if the host has no coordinates.</summary>
        private static BackgroundStarEntry PickClosest(List<BackgroundStarEntry> candidates, StarTarget host)
        {
            if (candidates.Count == 1 || !host.RaDeg.HasValue || !host.DecDeg.HasValue)
                return candidates[0];
            BackgroundStarEntry best = candidates[0];
            double bestSep = double.MaxValue;
            foreach (var c in candidates)
            {
                double sep = SeparationArcsec(c.Target, host);
                if (sep < bestSep) { bestSep = sep; best = c; }
            }
            return best;
        }

        private static BackgroundStarEntry NearestWithinTolerance(List<BackgroundStarEntry> entries, StarTarget host)
        {
            if (!host.RaDeg.HasValue || !host.DecDeg.HasValue) return null;

            BackgroundStarEntry best = null;
            double bestSep = PositionalToleranceArcsec;
            foreach (var entry in entries)
            {
                double sep = SeparationArcsec(entry.Target, host);
                if (sep > bestSep) continue;
                if (Math.Abs(entry.Target.ApparentMagnitude - host.ApparentMagnitude) > MagnitudeSanityTolerance) continue;
                bestSep = sep;
                best = entry;
            }
            return best;
        }

        /// <summary>Small-angle separation with cos(dec) RA foreshortening and wraparound — exact enough for the arcsecond tolerances here.</summary>
        private static double SeparationArcsec(StarTarget a, StarTarget b)
        {
            if (!a.RaDeg.HasValue || !a.DecDeg.HasValue || !b.RaDeg.HasValue || !b.DecDeg.HasValue)
                return double.MaxValue;

            double dRa = Math.Abs(a.RaDeg.Value - b.RaDeg.Value);
            if (dRa > 180.0) dRa = 360.0 - dRa;
            double meanDecRad = (a.DecDeg.Value + b.DecDeg.Value) / 2.0 * Math.PI / 180.0;
            double dx = dRa * Math.Cos(meanDecRad);
            double dy = a.DecDeg.Value - b.DecDeg.Value;
            return Math.Sqrt(dx * dx + dy * dy) * 3600.0;
        }
    }
}
