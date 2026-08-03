using System;
using System.Collections.Generic;
using System.Globalization;

namespace ExoInstruments.Core
{
    /// <summary>
    /// One term of a search: a piece of text to match names against, in both the forms
    /// TargetDesignations produces.
    /// </summary>
    public struct QueryTerm
    {
        /// <summary>Canonical designation keys this term could be, for exact matching.</summary>
        public List<string> Keys;
        /// <summary>Letters and digits only, for substring matching.</summary>
        public string Loose;
    }

    /// <summary>
    /// A parsed search box.
    ///
    /// The syntax is the one every catalogue browser has converged on, because it is the one that
    /// does not need a manual: bare words match names, and a word with a colon in it is a filter.
    ///
    ///     orion                 anything whose name contains it
    ///     type:galaxy           only galaxies; see TargetKinds.Group for the vocabulary
    ///     in:Ori   in:Orion     only what lies inside that constellation's IAU boundary
    ///     mag:&lt;9              brighter than ninth magnitude
    ///     alt:&gt;30             at least thirty degrees above the horizon right now
    ///
    /// Filters combine with AND; several type: filters combine with OR among themselves, so
    /// "type:galaxy type:nebula" is both and not neither.
    ///
    /// WHAT IT DOES WITH SOMETHING IT DOES NOT UNDERSTAND. Nothing silent. "type:nebulla" is
    /// recorded in Unrecognised and reported to the player, rather than being dropped (which would
    /// quietly widen the search to everything) or treated as a name (which would quietly narrow it
    /// to nothing).
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public sealed class TargetQuery
    {
        public readonly List<QueryTerm> Terms = new List<QueryTerm>();

        /// <summary>
        /// The whole of the typed text, filters removed, as one term.
        ///
        /// WHY IT IS SEPARATE FROM Terms. "NGC 24" is one designation written with a space in it,
        /// and splitting it into the words "NGC" and "24" asks for something that matches both,
        /// which NGC 224, NGC 2400 and several thousand others do. Scoring the phrase as well means
        /// the object that IS NGC 24 wins outright, while the word-by-word terms still let "orion
        /// nebula" find an object called the Great Orion Nebula.
        /// </summary>
        public QueryTerm Phrase;

        /// <summary>Kinds the result must be one of. Empty means no constraint.</summary>
        public readonly List<TargetKind> Kinds = new List<TargetKind>();

        /// <summary>Three-letter IAU abbreviations the result must lie in. Empty means no constraint.</summary>
        public readonly List<string> Constellations = new List<string>();

        /// <summary>Faintest acceptable magnitude, or NaN. A target with no known magnitude fails this filter.</summary>
        public double MaxMagnitude = double.NaN;

        /// <summary>Brightest acceptable magnitude, or NaN. Same treatment of unknowns.</summary>
        public double MinMagnitude = double.NaN;

        /// <summary>Lowest acceptable current altitude in degrees, or NaN. A target of unknown altitude fails this filter.</summary>
        public double MinAltitudeDeg = double.NaN;

        /// <summary>Filters the player wrote that mean nothing, verbatim, so the interface can say so.</summary>
        public readonly List<string> Unrecognised = new List<string>();

        public bool IsEmpty => Terms.Count == 0 && Kinds.Count == 0 && Constellations.Count == 0
                            && double.IsNaN(MaxMagnitude) && double.IsNaN(MinMagnitude)
                            && double.IsNaN(MinAltitudeDeg);

        /// <summary>True when the query says anything about names, which is what decides whether results are ranked or merely listed.</summary>
        public bool HasTerms => Terms.Count > 0;

        public static TargetQuery Parse(string text)
        {
            var query = new TargetQuery();
            if (string.IsNullOrWhiteSpace(text)) return query;

            var words = new List<string>();
            foreach (string raw in text.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = raw.IndexOf(':');
                if (colon > 0 && colon < raw.Length - 1)
                {
                    if (ApplyFilter(query, raw.Substring(0, colon), raw.Substring(colon + 1))) continue;
                    query.Unrecognised.Add(raw);
                    continue;
                }

                words.Add(raw);
                var term = new QueryTerm
                {
                    Keys = TargetDesignations.Keys(raw),
                    Loose = TargetDesignations.Loose(raw),
                };
                if (term.Loose.Length > 0 || term.Keys.Count > 0) query.Terms.Add(term);
            }

            if (words.Count > 1)
            {
                string joined = string.Join(" ", words.ToArray());
                query.Phrase = new QueryTerm
                {
                    Keys = TargetDesignations.Keys(joined),
                    Loose = TargetDesignations.Loose(joined),
                };
            }
            else if (words.Count == 1)
            {
                query.Phrase = query.Terms.Count > 0 ? query.Terms[0] : default(QueryTerm);
            }
            return query;
        }

        private static bool ApplyFilter(TargetQuery query, string name, string value)
        {
            switch (name.ToLowerInvariant())
            {
                case "type":
                case "kind":
                {
                    TargetKind[] group = TargetKinds.Group(value);
                    if (group == null) return false;
                    foreach (TargetKind kind in group)
                        if (!query.Kinds.Contains(kind)) query.Kinds.Add(kind);
                    return true;
                }
                case "in":
                case "constellation":
                case "con":
                {
                    // Fully qualified: this class has its own Constellations member, and the
                    // simple name would resolve to that field rather than to the lookup class.
                    string abbreviation = ExoInstruments.Core.Constellations.Resolve(value);
                    if (abbreviation == null) return false;
                    if (!query.Constellations.Contains(abbreviation)) query.Constellations.Add(abbreviation);
                    return true;
                }
                case "mag":
                case "magnitude":
                case "v":
                    return ApplyMagnitude(query, value);
                case "alt":
                case "altitude":
                    return ApplyAltitude(query, value);
                default:
                    return false;
            }
        }

        /// <summary>"mag:&lt;9", "mag:&gt;12", or "mag:9" read as "no fainter than 9", which is what an observer means.</summary>
        private static bool ApplyMagnitude(TargetQuery query, string value)
        {
            if (!SplitComparison(value, out char comparison, out double number)) return false;
            if (comparison == '>') query.MinMagnitude = number;
            else query.MaxMagnitude = number;
            return true;
        }

        /// <summary>"alt:&gt;30" or "alt:30", both meaning at least thirty degrees up. "alt:&lt;x" is refused: nobody asks for targets that are lower.</summary>
        private static bool ApplyAltitude(TargetQuery query, string value)
        {
            if (!SplitComparison(value, out char comparison, out double number)) return false;
            if (comparison == '<') return false;
            query.MinAltitudeDeg = number;
            return true;
        }

        private static bool SplitComparison(string value, out char comparison, out double number)
        {
            comparison = '=';
            number = double.NaN;
            if (string.IsNullOrEmpty(value)) return false;

            string rest = value;
            if (value[0] == '<' || value[0] == '>')
            {
                comparison = value[0];
                rest = value.Substring(1);
                if (rest.StartsWith("=")) rest = rest.Substring(1);
            }
            // Invariant parse on purpose: a machine locale that reads "9.5" as nine and a half or
            // as ninety-five decides what the player gets, and only one of those is right.
            return double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }
    }
}
