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
        /// <summary>
        /// Positional match radius (20 arcsec). Absorbs exoplanet.eu's RA rounding (~13 arcsec drift on 51 Peg)
        /// while staying below resolved binary separations, verified against 83 Leo A/B (27 arcsec apart).
        /// </summary>
        public const double PositionalToleranceArcsec = 20.0;

        /// <summary>
        /// Magnitude sanity check on positional matches, loose because exoplanet.eu may report a different band
        /// for red stars, but 3 magnitudes off is a different star.
        /// </summary>
        public const double MagnitudeSanityTolerance = 1.5;

        /// <summary>BSC is nominally complete to V = 6.5 with stragglers slightly fainter; review unmatched hosts up to here.</summary>
        public const double BrightHostReviewMagnitude = 6.7;

        // Declination band searched around a host: twice the tolerance, so no rounding can bring a star outside it
        // within tolerance.
        private const double BandHalfWidthDeg = 2.0 * PositionalToleranceArcsec / 3600.0;

        // Coordinates up to this size keep every step of SeparationArcsec finite, which the band relies on.
        private const double BoundedCoordinateDeg = 1e6;

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
            var normalized = new Dictionary<string, string>();
            PositionIndex positions = null;
            foreach (string hostKey in hostOrder)
            {
                var planets = hosts[hostKey];
                BackgroundStarEntry match = null;
                string via = null;
                List<string> hostNames = null;

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
                    hostNames = NormalizedHostNames(planets, normalized);
                    foreach (string nameKey in CollectNameKeys(hostNames, hostKey))
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
                // own HD number and must match through identifiers (16 Cyg B is HD 186427).
                if (match == null && !IsNonPrimaryComponent(hostNames ?? NormalizedHostNames(planets, normalized)))
                {
                    match = NearestWithinTolerance(backgroundStars, planets[0], ref positions);
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

                    // Backfill Teff from BSC B-V when exoplanet.eu forgot it (HR 8799). Without a
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

        // Every planet's normalized host name and alternate names, each once, in first-seen order. The name match and
        // the component check both read this list, and a repeat changes neither.
        private static List<string> NormalizedHostNames(List<StarTarget> planets, Dictionary<string, string> memo)
        {
            var names = new List<string>();
            foreach (var p in planets)
            {
                AddDistinct(names, NormalizeOnce(p.HostStarName, memo));
                if (p.HostStarAlternateNames != null)
                {
                    foreach (string alt in p.HostStarAlternateNames.Split(','))
                        AddDistinct(names, NormalizeOnce(alt, memo));
                }
            }
            return names;
        }

        private static void AddDistinct(List<string> names, string name)
        {
            if (!names.Contains(name)) names.Add(name);
        }

        // Normalize is pure, so a name every planet of a system repeats is normalized once per merge.
        private static string NormalizeOnce(string rawName, Dictionary<string, string> memo)
        {
            if (rawName == null) return null;
            if (!memo.TryGetValue(rawName, out string key))
                memo[rawName] = key = StarNames.Normalize(rawName);
            return key;
        }

        // Normalized name variants for the host: own name + alternate names, with trailing " a" stripped ("tau
        // boo a" becomes "tau boo"). Never strips " b"/" c"; those may be distinct BSC entries.
        private static List<string> CollectNameKeys(List<string> hostNames, string hostKey)
        {
            var keys = new List<string> { hostKey };
            foreach (string name in hostNames)
                AddNameKey(keys, name);
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

        // True when any host-level designation ends in an explicit non-primary component letter (" b"/" c"/" d"
        // after normalization).
        private static bool IsNonPrimaryComponent(List<string> hostNames)
        {
            foreach (string name in hostNames)
                if (EndsWithComponentLetter(name)) return true;
            return false;
        }

        private static bool EndsWithComponentLetter(string normalizedKey)
        {
            return normalizedKey != null &&
                   (normalizedKey.EndsWith(" b") || normalizedKey.EndsWith(" c") || normalizedKey.EndsWith(" d"));
        }

        // Of several candidates sharing an HD number (binary components), take the positionally closest; first
        // if the host has no coordinates.
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

        // Same result as scanning every star in list order: stars outside the declination band are more than the
        // tolerance away, and the band's stars are visited in list order so ties still go to the later star.
        // A band holding over a quarter of the list takes the plain scan, which is then cheaper.
        private static BackgroundStarEntry NearestWithinTolerance(List<BackgroundStarEntry> entries, StarTarget host,
                                                                  ref PositionIndex index)
        {
            if (!host.RaDeg.HasValue || !host.DecDeg.HasValue) return null;

            if (index == null) index = PositionIndex.Build(entries);
            double hostDec = host.DecDeg.Value;
            if (!index.Usable || !IsBounded(host.RaDeg.Value) || !IsBounded(hostDec))
                return NearestFrom(entries, host, 0, null, PositionalToleranceArcsec);

            int first = LowerBound(index.Dec, index.Count, hostDec - BandHalfWidthDeg);
            int end = UpperBound(index.Dec, index.Count, hostDec + BandHalfWidthDeg);
            if (end - first > entries.Count / 4)
                return NearestFrom(entries, host, 0, null, PositionalToleranceArcsec);

            List<int> band = index.Band;
            band.Clear();
            for (int i = first; i < end; i++)
                band.Add(index.Order[i]);
            band.Sort();

            // Band and unbounded stars merged back into list order.
            BackgroundStarEntry best = null;
            double bestSep = PositionalToleranceArcsec;
            int[] unbounded = index.Unbounded;
            int a = 0, b = 0;
            while (a < band.Count || b < unbounded.Length)
            {
                int k;
                if (b == unbounded.Length || (a < band.Count && band[a] < unbounded[b])) k = band[a++];
                else k = unbounded[b++];
                Consider(entries[k], host, ref best, ref bestSep);

                // Every later 'sep > NaN' test fails, so from here on no star may be skipped.
                if (double.IsNaN(bestSep)) return NearestFrom(entries, host, k + 1, best, bestSep);
            }
            return best;
        }

        // The plain scan over every star from list position 'start'.
        private static BackgroundStarEntry NearestFrom(List<BackgroundStarEntry> entries, StarTarget host, int start,
                                                       BackgroundStarEntry best, double bestSep)
        {
            for (int k = start; k < entries.Count; k++)
                Consider(entries[k], host, ref best, ref bestSep);
            return best;
        }

        // One step of the scan: a star no farther than the best so far (a tie goes to the later star) and of
        // plausible magnitude becomes the best.
        private static void Consider(BackgroundStarEntry entry, StarTarget host,
                                     ref BackgroundStarEntry best, ref double bestSep)
        {
            double sep = SeparationArcsec(entry.Target, host);
            if (sep > bestSep) return;
            if (Math.Abs(entry.Target.ApparentMagnitude - host.ApparentMagnitude) > MagnitudeSanityTolerance) return;
            bestSep = sep;
            best = entry;
        }

        private static bool IsBounded(double deg)
        {
            return deg >= -BoundedCoordinateDeg && deg <= BoundedCoordinateDeg;
        }

        // First position in sorted[0, count) not below value.
        private static int LowerBound(double[] sorted, int count, double value)
        {
            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        // First position in sorted[0, count) above value.
        private static int UpperBound(double[] sorted, int count, double value)
        {
            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] <= value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        // Background stars sorted by declination, built on the first positional lookup of a merge.
        private sealed class PositionIndex
        {
            public bool Usable;          // false if a star has no Target: the plain scan then throws where it always did
            public double[] Dec;         // declinations of stars with bounded coordinates, ascending
            public int[] Order;          // list position of each Dec value
            public int Count;
            public int[] Unbounded;      // list positions of stars with NaN, infinite or huge coordinates
            public readonly List<int> Band = new List<int>();

            public static PositionIndex Build(List<BackgroundStarEntry> entries)
            {
                var index = new PositionIndex();
                var dec = new double[entries.Count];
                var order = new int[entries.Count];
                var unbounded = new List<int>();
                int count = 0;
                for (int k = 0; k < entries.Count; k++)
                {
                    StarTarget t = entries[k].Target;
                    if (t == null) return index;

                    // Without coordinates the separation is double.MaxValue, never within tolerance.
                    if (!t.RaDeg.HasValue || !t.DecDeg.HasValue) continue;
                    if (IsBounded(t.RaDeg.Value) && IsBounded(t.DecDeg.Value))
                    {
                        dec[count] = t.DecDeg.Value;
                        order[count] = k;
                        count++;
                    }
                    else unbounded.Add(k);
                }
                Array.Sort(dec, order, 0, count);
                index.Dec = dec;
                index.Order = order;
                index.Count = count;
                index.Unbounded = unbounded.ToArray();
                index.Usable = true;
                return index;
            }
        }

        // Small-angle separation with cos(dec) RA foreshortening and wraparound, exact enough for the arcsecond
        // tolerances here.
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
