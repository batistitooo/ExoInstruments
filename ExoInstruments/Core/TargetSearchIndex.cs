using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// One thing the telescope can be pointed at, as the search box sees it.
    ///
    /// Deliberately not a union of the catalogue types. A search result needs a name, a position, a
    /// kind and enough numbers to sort by; what it does NOT need is the twenty measured quantities
    /// a Galaxy or a StarTarget carries. Those stay where they are, and Payload carries the
    /// original object back to whoever needs it, which keeps this file free of any dependency on
    /// what the caller does with a selection.
    /// </summary>
    public sealed class SearchTarget
    {
        /// <summary>What to call it: the best designation, with a common name alongside when there is one.</summary>
        public string DisplayName;

        /// <summary>The catalogue designation shown in DisplayName, e.g. "M 31". Null for a target that has only a name.</summary>
        public string Designation;

        /// <summary>The common name shown in DisplayName, e.g. "Andromeda". Null when the object has none.</summary>
        public string CommonName;

        /// <summary>"M 31 (Andromeda)", or whichever half exists.</summary>
        public void ComposeDisplayName()
        {
            if (string.IsNullOrEmpty(Designation)) { DisplayName = CommonName ?? DisplayName; return; }
            DisplayName = string.IsNullOrEmpty(CommonName) ? Designation : Designation + " (" + CommonName + ")";
        }

        /// <summary>The source's own classification in its own words, e.g. "Globular Cluster", "Sb galaxy". Shown as-is.</summary>
        public string TypeLabel;

        /// <summary>Which catalogue this row came from, named so a result can be traced to its source.</summary>
        public string Provenance;

        public TargetKind Kind;

        /// <summary>J2000 position, degrees. NaN for a solar-system body, whose position is read live from the game.</summary>
        public double RaDeg = double.NaN;
        public double DecDeg = double.NaN;

        /// <summary>Apparent magnitude, in whatever band the source measured; NaN when unknown. Sorting treats unknown as faintest.</summary>
        public double Magnitude = double.NaN;

        /// <summary>Major axis of the apparent extent, arcminutes; NaN for a point source or an unmeasured one.</summary>
        public double MajorArcmin = double.NaN;

        /// <summary>Three-letter IAU constellation abbreviation, or null for a solar-system body (which moves through them).</summary>
        public string Constellation;

        /// <summary>Current altitude above the horizon in degrees, refreshed by the caller; NaN when not computed.</summary>
        public double AltitudeDeg = double.NaN;

        /// <summary>The caller's own object: a StarTarget, a DeepSkyObject, a Galaxy, a body index. Never read here.</summary>
        public object Payload;

        /// <summary>Every name this target answers to, as written. The first is the primary designation.</summary>
        public string[] Aliases;

        /// <summary>
        /// True when the game is deliberately hiding what this target is (career fog of war before
        /// a star has been scanned). Such an entry is indexed under its provisional designation and
        /// refuses to take on further names, so no later catalogue can leak the identity back in.
        /// </summary>
        public bool IdentityWithheld;

        // Precomputed at Add time. Building these per query over tens of thousands of entries is
        // the difference between a search box that keeps up with typing and one that does not.
        internal string[] Keys;
        internal string[] LooseAliases;
    }

    /// <summary>One entry that matched, with the score it matched at.</summary>
    public struct SearchResult
    {
        public SearchTarget Target;
        public int Score;
    }

    /// <summary>
    /// The search box's index: every target the mod can point at, from every installed catalogue,
    /// findable by any name it is known under.
    ///
    /// WHY ONE INDEX AND NOT A SEARCH PER CATALOGUE. An observer does not think in catalogues. They
    /// think "the Ring Nebula", and whether that lives in a hand-written nebula list, in HyperLEDA
    /// or only in a cross-identification table is an implementation detail of this mod, not a fact
    /// about the sky. So everything goes in one index and comes out ranked together.
    ///
    /// HOW MATCHING WORKS, and what it deliberately does not do. Each term of the query is matched
    /// against every name of every target, strongest first: an exact designation key, then a key
    /// that starts with the term, then a name that starts with it, then a name that contains it.
    /// There is no fuzzy matching, no edit distance and no phonetic collapsing. In a catalogue of
    /// numbered objects those turn a typo into a confident wrong answer, and the cost of a wrong
    /// answer here is a night of telescope time spent on the wrong thing.
    ///
    /// Terms combine with AND: every term must match something. Filters (type, constellation,
    /// magnitude, altitude) are hard constraints applied before scoring.
    ///
    /// Pure C#, no Unity dependency, so the index can be built and queried off the main thread.
    /// </summary>
    public sealed class TargetSearchIndex
    {
        private readonly List<SearchTarget> entries = new List<SearchTarget>();
        private readonly Dictionary<string, SearchTarget> byKey = new Dictionary<string, SearchTarget>(StringComparer.Ordinal);

        public int Count => entries.Count;
        public IList<SearchTarget> Entries => entries;

        // Scores, in the order the match was found. The gaps are wide so that a stronger match on
        // one term always outranks a weaker match on another, however many terms there are.
        private const int ScoreExactKey = 1000;
        private const int ScoreKeyPrefix = 400;
        private const int ScoreNamePrefix = 200;
        private const int ScoreContains = 50;

        // The whole typed phrase matching outright is worth more than any accumulation of
        // word-by-word matches can be, because it means the searcher wrote the object's name.
        private const int ScorePhrase = 100000;

        public void Add(SearchTarget target)
        {
            if (target == null || target.Aliases == null || target.Aliases.Length == 0)
                throw new ArgumentException("a search target needs at least one name", "target");

            var keys = new List<string>(target.Aliases.Length + 1);
            var loose = new List<string>(target.Aliases.Length);
            foreach (string alias in target.Aliases)
            {
                foreach (string key in TargetDesignations.Keys(alias))
                    if (!keys.Contains(key)) keys.Add(key);
                string l = TargetDesignations.Loose(alias);
                if (l.Length > 0 && !loose.Contains(l)) loose.Add(l);
            }

            target.Keys = keys.ToArray();
            target.LooseAliases = loose.ToArray();
            entries.Add(target);

            // First writer wins. The catalogues overlap (a galaxy can be in HyperLEDA and in the
            // cross-identification table both), and the earlier one is the one with real measured
            // parameters, which is why the caller adds them in that order.
            foreach (string key in target.Keys)
                if (!byKey.ContainsKey(key)) byKey[key] = target;
        }

        /// <summary>
        /// Gives an entry further names, from a source that identified it after it was added. The
        /// entry keeps everything it already had; only the names grow.
        ///
        /// Refused outright on an entry whose identity is being withheld: the whole point of
        /// career fog is that an unscanned star cannot be looked up by what it really is, and a
        /// cross-identification table would otherwise hand that straight back.
        /// </summary>
        public void AddAliases(SearchTarget target, IEnumerable<string> aliases)
        {
            if (target == null || aliases == null || target.IdentityWithheld) return;

            var names = new List<string>(target.Aliases);
            var keys = new List<string>(target.Keys);
            var loose = new List<string>(target.LooseAliases);
            bool changed = false;

            foreach (string alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || names.Contains(alias)) continue;
                names.Add(alias);
                changed = true;
                foreach (string key in TargetDesignations.Keys(alias))
                {
                    if (!keys.Contains(key)) keys.Add(key);
                    if (!byKey.ContainsKey(key)) byKey[key] = target;
                }
                string l = TargetDesignations.Loose(alias);
                if (l.Length > 0 && !loose.Contains(l)) loose.Add(l);
            }

            if (!changed) return;
            target.Aliases = names.ToArray();
            target.Keys = keys.ToArray();
            target.LooseAliases = loose.ToArray();
        }

        /// <summary>Whether some target already answers to this name, so a second source does not add a duplicate.</summary>
        public bool Contains(string name)
        {
            foreach (string key in TargetDesignations.Keys(name))
                if (byKey.ContainsKey(key)) return true;
            return false;
        }

        /// <summary>The target registered under a name, or null. Exact designation match only; no ranking involved.</summary>
        public SearchTarget Find(string name)
        {
            foreach (string key in TargetDesignations.Keys(name))
                if (byKey.TryGetValue(key, out SearchTarget found)) return found;
            return null;
        }

        /// <summary>
        /// The best <paramref name="max"/> matches, ordered by how well they match and then by
        /// brightness. With no search terms this is a browse rather than a search: everything the
        /// filters allow, nearest and brightest first.
        /// </summary>
        public List<SearchResult> Query(TargetQuery query, int max, out int totalMatched)
        {
            List<SearchResult> matched = QueryAll(query);
            totalMatched = matched.Count;
            if (matched.Count > max) matched.RemoveRange(max, matched.Count - max);
            return matched;
        }

        /// <summary>
        /// Every match, ranked, with no cap.
        ///
        /// The list shown to the player is capped, because IMGUI lays out every row it is handed;
        /// what must NOT be capped is the set of matches the sky chart highlights. Lighting up the
        /// first hundred galaxies of fifteen hundred, and dimming the rest, would be a chart that
        /// says the search found a hundred galaxies.
        /// </summary>
        public List<SearchResult> QueryAll(TargetQuery query)
        {
            var matched = new List<SearchResult>();
            foreach (SearchTarget entry in entries)
            {
                if (!PassesFilters(entry, query)) continue;

                int score = 0;
                bool ok = true;
                foreach (QueryTerm term in query.Terms)
                {
                    int termScore = ScoreTerm(entry, term);
                    if (termScore == 0) { ok = false; break; }
                    score += termScore;
                }
                if (!ok) continue;

                // The phrase is scored on top of the terms, never instead of them, so it can only
                // reorder results that already matched every word.
                int phrase = ScoreTerm(entry, query.Phrase);
                if (phrase > 0) score += ScorePhrase + phrase;

                matched.Add(new SearchResult { Target = entry, Score = score });
            }

            matched.Sort(query.HasTerms ? (Comparison<SearchResult>)CompareRanked : CompareBrowsing);
            return matched;
        }

        private static bool PassesFilters(SearchTarget entry, TargetQuery query)
        {
            if (query.Kinds.Count > 0 && !query.Kinds.Contains(entry.Kind)) return false;

            if (query.Constellations.Count > 0)
            {
                // A solar-system body has no fixed constellation, so a constellation filter
                // excludes it rather than guessing from where it happens to be tonight.
                if (entry.Constellation == null) return false;
                if (!query.Constellations.Contains(entry.Constellation)) return false;
            }

            // An unknown magnitude fails a magnitude filter. The alternative, letting it through,
            // would put targets of unknown brightness in a list the observer asked to be
            // brighter than ninth, which is a claim the catalogue does not support.
            if (!double.IsNaN(query.MaxMagnitude)
                && !(entry.Magnitude <= query.MaxMagnitude)) return false;
            if (!double.IsNaN(query.MinMagnitude)
                && !(entry.Magnitude >= query.MinMagnitude)) return false;
            if (!double.IsNaN(query.MinAltitudeDeg)
                && !(entry.AltitudeDeg >= query.MinAltitudeDeg)) return false;

            return true;
        }

        private static int ScoreTerm(SearchTarget entry, QueryTerm term)
        {
            if (term.Keys != null)
            {
                foreach (string key in entry.Keys)
                {
                    foreach (string wanted in term.Keys)
                    {
                        if (string.Equals(key, wanted, StringComparison.Ordinal)) return ScoreExactKey;
                    }
                }
                foreach (string key in entry.Keys)
                {
                    foreach (string wanted in term.Keys)
                    {
                        if (wanted.Length > 0 && key.StartsWith(wanted, StringComparison.Ordinal))
                            return ScoreKeyPrefix;
                    }
                }
            }

            if (string.IsNullOrEmpty(term.Loose)) return 0;
            foreach (string alias in entry.LooseAliases)
                if (alias.StartsWith(term.Loose, StringComparison.Ordinal)) return ScoreNamePrefix;
            foreach (string alias in entry.LooseAliases)
                if (alias.IndexOf(term.Loose, StringComparison.Ordinal) >= 0) return ScoreContains;

            return 0;
        }

        // Score first, then the brighter target, then alphabetically so the order never depends on catalogue
        // order.
        private static int CompareRanked(SearchResult a, SearchResult b)
        {
            if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
            return CompareBrowsing(a, b);
        }

        // Browsing order: solar-system bodies first, because they are what a telescope in the back garden
        // points at, then everything by brightness. A target with no measured magnitude sorts as if it were
        // faint, which is where an unmeasured object belongs.
        private static int CompareBrowsing(SearchResult a, SearchResult b)
        {
            bool aLocal = TargetKinds.IsSolarSystem(a.Target.Kind);
            bool bLocal = TargetKinds.IsSolarSystem(b.Target.Kind);
            if (aLocal != bLocal) return aLocal ? -1 : 1;

            double am = double.IsNaN(a.Target.Magnitude) ? double.MaxValue : a.Target.Magnitude;
            double bm = double.IsNaN(b.Target.Magnitude) ? double.MaxValue : b.Target.Magnitude;
            if (am != bm) return am.CompareTo(bm);

            return string.Compare(a.Target.DisplayName, b.Target.DisplayName, StringComparison.Ordinal);
        }
    }
}
