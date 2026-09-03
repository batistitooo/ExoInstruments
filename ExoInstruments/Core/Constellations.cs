using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Which of the 88 IAU constellations a direction falls in, and the names that go with it.
    ///
    /// The boundaries are Delporte's (1930), adopted by the IAU in 1928 and unchanged since,
    /// rearranged for lookup by Roman (1987, PASP 99, 695) and shipped here as the generated
    /// ConstellationTable. They are lines of constant right ascension and declination in the mean
    /// equinox of B1875 and in no other frame, so a J2000 position is brought there first; see
    /// BesselianFrames for why that is a frame change and not only a precession.
    ///
    /// The lookup itself is Roman's own: her table lists the SOUTHERN edge of every boundary arc,
    /// sorted by declination and then by eastern terminus, so the first arc encountered that lies
    /// at or below the position and brackets it in right ascension is the answer. Record order is
    /// therefore part of the data, which is why the generated table says so.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class Constellations
    {
        private static Dictionary<string, int> indexByAbbreviation;
        private static Dictionary<string, int> indexByAnyName;

        /// <summary>The 88 IAU abbreviations, alphabetical.</summary>
        public static IList<string> AllAbbreviations => ConstellationTable.AllAbbreviations;

        /// <summary>
        /// The constellation containing a J2000 position, as its three-letter IAU abbreviation.
        /// Never null: the 88 constellations tile the sphere with no gaps.
        /// </summary>
        public static string FindAbbreviation(double raDegJ2000, double decDegJ2000)
        {
            BesselianFrames.J2000ToBesselian(raDegJ2000, decDegJ2000,
                                             BesselianFrames.ConstellationBoundaryEpoch,
                                             out double raDeg, out double decDeg);
            return FindAbbreviationB1875(raDeg / 15.0, decDeg);
        }

        /// <summary>
        /// The same lookup on a position ALREADY in the mean equinox of B1875, right ascension in
        /// hours. Exposed because it is the step Roman's own published worked examples exercise,
        /// and tools/constellation-tests reproduces them through it.
        /// </summary>
        public static string FindAbbreviationB1875(double raHours, double decDeg)
        {
            string[] abbreviations = ConstellationTable.Abbreviations;
            double[] raLow = ConstellationTable.RaLowHours;
            double[] raHigh = ConstellationTable.RaHighHours;
            double[] decLow = ConstellationTable.DecLowDeg;

            for (int i = 0; i < abbreviations.Length; i++)
            {
                if (decLow[i] > decDeg) continue;
                if (raHours < raLow[i] || raHours >= raHigh[i]) continue;
                return abbreviations[i];
            }

            // Unreachable for a well-formed table, and worth being loud about rather than
            // returning a plausible wrong constellation: it would mean the table lost a record.
            throw new InvalidOperationException(
                "No IAU constellation contains RA " + raHours.ToString("F4") + " h, Dec "
                + decDeg.ToString("F4") + " deg (B1875). The boundary table is incomplete.");
        }

        /// <summary>The IAU's official name for an abbreviation, e.g. "Ori" -&gt; "Orion". Null if the abbreviation is not one of the 88.</summary>
        public static string NameOf(string abbreviation)
        {
            int i = IndexOf(abbreviation);
            return i < 0 ? null : ConstellationTable.AllNames[i];
        }

        /// <summary>What the Latin name means, as the IAU's own table glosses it: "Ori" -&gt; "the Hunter".</summary>
        public static string MeaningOf(string abbreviation)
        {
            int i = IndexOf(abbreviation);
            return i < 0 ? null : ConstellationTable.AllMeanings[i];
        }

        /// <summary>The genitive, the form that appears inside a Bayer or Flamsteed star name: "Peg" -&gt; "Pegasi".</summary>
        public static string GenitiveOf(string abbreviation)
        {
            int i = IndexOf(abbreviation);
            return i < 0 ? null : ConstellationTable.AllGenitives[i];
        }

        /// <summary>
        /// The abbreviation a player might have meant by a typed word: the abbreviation itself,
        /// the nominative, or the genitive, case-insensitively. "ori", "Orion" and "Orionis" all
        /// resolve to "Ori". Returns null when the word is not a constellation at all.
        /// </summary>
        public static string Resolve(string word)
        {
            if (string.IsNullOrEmpty(word)) return null;
            EnsureNameIndex();
            return indexByAnyName.TryGetValue(Canonicalise(word), out int i)
                ? ConstellationTable.AllAbbreviations[i]
                : null;
        }

        private static int IndexOf(string abbreviation)
        {
            if (string.IsNullOrEmpty(abbreviation)) return -1;
            EnsureAbbreviationIndex();
            return indexByAbbreviation.TryGetValue(abbreviation.ToLowerInvariant(), out int i) ? i : -1;
        }

        private static void EnsureAbbreviationIndex()
        {
            if (indexByAbbreviation != null) return;
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < ConstellationTable.AllAbbreviations.Length; i++)
                map[ConstellationTable.AllAbbreviations[i].ToLowerInvariant()] = i;
            indexByAbbreviation = map;
        }

        private static void EnsureNameIndex()
        {
            if (indexByAnyName != null) return;
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < ConstellationTable.AllAbbreviations.Length; i++)
            {
                map[Canonicalise(ConstellationTable.AllAbbreviations[i])] = i;
                map[Canonicalise(ConstellationTable.AllNames[i])] = i;
                map[Canonicalise(ConstellationTable.AllGenitives[i])] = i;
            }
            indexByAnyName = map;
        }

        // Lowercase, spaces dropped, and the two names the IAU spells with a diaeresis reduced to their plain-
        // letter form, so a player who types "bootes" is understood as readily as one who types "Bo&#246;tes".
        // Nothing else is transliterated because nothing else needs it: these are the only non-ASCII characters
        // in the list.
        private static string Canonicalise(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == ' ' || c == '\t') continue;
                if (c == 'ö' || c == 'Ö') { sb.Append('o'); continue; }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
