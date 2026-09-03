using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Turns the many ways one object is written into one key, so that searching for it works
    /// whatever form the searcher happens to know.
    ///
    /// WHY THIS IS NEEDED AT ALL. Every catalogue writes its own designations its own way, and the
    /// mod installs several. HyperLEDA writes the Andromeda Galaxy NGC0224, zero-padded and
    /// unspaced. SIMBAD writes it NGC 224 with the number right-aligned in a fixed field, so its
    /// raw form is "NGC   224". The nebula list here writes NGC 1976 with one space. A player types
    /// ngc224, or ngc 224, or M31. A substring search over the raw strings gets none of those right
    /// and several of them wrong: "NGC 24" is a substring of "NGC 247".
    ///
    /// WHAT A KEY IS. Lowercase, whitespace and punctuation removed between the catalogue prefix
    /// and its number, leading zeros dropped, so all of the above collapse onto "ngc224". Star
    /// designations go through StarNames.Normalize first, which is the same idea applied to Greek
    /// letters and constellation genitives: "beta Pictoris", "bet Pic" and "Beta Pic" all become
    /// "bet pic".
    ///
    /// EXACT, NOT FUZZY. There is no edit distance and no phonetic matching anywhere here. A key
    /// either matches or it does not, and everything else is left to plain substring search on a
    /// separate loose form. Fuzzy matching in a catalogue of tens of thousands of numbered objects
    /// produces confident wrong answers, and a wrong target is a wasted night of observing.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class TargetDesignations
    {
        // "NGC0224", "NGC 224", "ngc-224", "IC 1396A", "Sh2 155". The suffix letter is kept
        // because it distinguishes real objects: NGC 4038 and NGC 4038A are not the same galaxy.
        //
        // The digit after the letters is captured SEPARATELY from the number, because whether it
        // belongs to the prefix or to the number cannot be decided by shape alone: in "Sh2 155" it
        // is part of the catalogue's name, and in "NGC0224" and "M104" it is the first digit of the
        // object. Getting that wrong is not cosmetic: reading "M104" as prefix "M1" and number 4
        // turns a search for the Sombrero into a search for M14, which is a globular cluster in a
        // different half of the sky. AlphanumericPrefixes below is what decides.
        private static readonly Regex CatalogueForm = new Regex(
            @"^([a-z]+)([0-9]?)[\s\-_]*([0-9]+)\s*([a-z])?$", RegexOptions.Compiled);

        // The catalogues whose NAME ends in a digit. Short by construction: a prefix is only listed
        // here on the evidence that the catalogue is designated that way, and everything unlisted
        // is read the safe way round, with the digit belonging to the object's number.
        private static readonly HashSet<string> AlphanumericPrefixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "sh1",   // Sharpless (1953), the first edition
            "sh2",   // Sharpless (1959), the one universally cited
            "rcw3",  // Rodgers, Campbell & Whiteoak, occasionally written this way
        };

        // Catalogue prefixes that are written more than one way. Kept deliberately short: this is
        // a list of SYNONYMS, not an attempt to know every catalogue, and an unknown prefix is
        // passed through unchanged rather than guessed at.
        private static readonly Dictionary<string, string> PrefixSynonyms =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "messier", "m" },
                { "sharpless", "sh2" },
                { "sh", "sh2" },       // Sharpless is universally cited as Sh2-nnn, its second edition
                { "barnard", "b" },
                { "caldwell", "c" },
                { "collinder", "cr" },
                { "melotte", "mel" },
                { "berkeley", "berk" },
                { "abell", "a" },
                { "arp", "arp" },
                { "pgc", "pgc" },
                { "leda", "pgc" },     // HyperLEDA's own numbering IS the PGC numbering
                { "eso", "eso" },
                { "ugc", "ugc" },
            };

        /// <summary>
        /// The canonical key for one written form of a name, or null when there is nothing left
        /// after cleaning. Two names are the same designation exactly when their keys are equal.
        /// </summary>
        public static string Key(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return null;

            // SIMBAD's own prefix for a common name is not part of the name.
            string text = rawName.Trim();
            if (text.StartsWith("NAME ", StringComparison.OrdinalIgnoreCase)) text = text.Substring(5);

            // StarNames handles the parts of this problem that are specific to star designations:
            // HTML entities from the exoplanet export, parentheticals, Greek letters and
            // constellation genitives. It leaves everything else alone.
            string normalised = StarNames.Normalize(text);
            if (string.IsNullOrEmpty(normalised)) return null;

            if (!TrySplit(normalised, out string prefix, out string number, out string suffix))
                return normalised;
            return prefix + number + suffix;
        }

        // Splits a catalogue designation into its canonical prefix, its number with leading zeros removed, and
        // any single-letter suffix. False when the text is not a catalogue designation at all, which is the
        // common case for a star's Bayer or Flamsteed name.
        private static bool TrySplit(string normalised, out string prefix, out string number, out string suffix)
        {
            prefix = number = suffix = null;

            Match match = CatalogueForm.Match(normalised);
            if (!match.Success) return false;

            string letters = match.Groups[1].Value;
            string ambiguousDigit = match.Groups[2].Value;
            string digits = match.Groups[3].Value;
            suffix = match.Groups[4].Value;

            if (ambiguousDigit.Length > 0 && !AlphanumericPrefixes.Contains(letters + ambiguousDigit))
            {
                // Not a catalogue whose name carries a digit, so the digit is the object's.
                digits = ambiguousDigit + digits;
                ambiguousDigit = "";
            }

            prefix = letters + ambiguousDigit;
            if (PrefixSynonyms.TryGetValue(prefix, out string canonical)) prefix = canonical;
            number = digits.TrimStart('0');
            if (number.Length == 0) number = "0";
            return true;
        }

        /// <summary>
        /// A catalogue designation written the way the catalogue's own papers write it: uppercase
        /// prefix, one space, the number without its padding. "NGC0224" becomes "NGC 224". Anything
        /// that is not a catalogue designation comes back tidied but otherwise untouched, because
        /// there is no canonical form to impose on a common name.
        /// </summary>
        public static string Pretty(string rawName)
        {
            string tidied = Tidy(rawName);
            if (string.IsNullOrEmpty(tidied)) return tidied;

            string normalised = StarNames.Normalize(tidied);
            if (string.IsNullOrEmpty(normalised)) return tidied;
            if (!TrySplit(normalised, out string prefix, out string number, out string suffix)) return tidied;

            // Only reformat designations whose prefix the mod recognises as a catalogue. A star's
            // "51 peg" would otherwise come back as "51 PEG", and "bet pic" is not a designation to
            // be uppercased either.
            if (!IsKnownCataloguePrefix(prefix)) return tidied;
            return prefix.ToUpperInvariant() + " " + number + suffix.ToUpperInvariant();
        }

        private static bool IsKnownCataloguePrefix(string prefix)
            => KnownCataloguePrefixes.Contains(prefix);

        // The prefixes the installed catalogues actually use, plus the ones an observer types.
        // Anything else keeps whatever form its own catalogue wrote it in.
        private static readonly HashSet<string> KnownCataloguePrefixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "m", "ngc", "ic", "ugc", "pgc", "eso", "sh1", "sh2", "b", "c", "cr", "mel", "berk", "a", "arp", "rcw3",
        };

        /// <summary>
        /// Every key one written form can legitimately reduce to. Usually one; a designation
        /// written with its prefix separated from its number by a space, like "NGC 224", also
        /// yields the joined key, and a hyphenated Sharpless number yields both halves' form.
        ///
        /// Returns an empty list rather than null for a name that reduces to nothing.
        /// </summary>
        public static List<string> Keys(string rawName)
        {
            var keys = new List<string>(2);
            string primary = Key(rawName);
            if (!string.IsNullOrEmpty(primary)) keys.Add(primary);

            // "NGC 224" normalises to "ngc 224" (two tokens), which the catalogue-form regex does
            // not see because of the space. Joining the tokens and re-keying catches it, and is
            // what makes "ngc 224" and "NGC0224" the same search.
            if (!string.IsNullOrEmpty(primary) && primary.IndexOf(' ') >= 0)
            {
                string joined = Key(primary.Replace(" ", ""));
                if (!string.IsNullOrEmpty(joined) && joined != primary) keys.Add(joined);
            }
            return keys;
        }

        /// <summary>
        /// The loose form used for substring search: lowercase letters and digits only, everything
        /// else dropped. "Barnard's Loop" becomes "barnardsloop", so typing "barnards loop" or
        /// "barnardsloop" both find it, and a query's loose form is compared the same way.
        /// </summary>
        public static string Loose(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "";
            var sb = new StringBuilder(rawName.Length);
            foreach (char c in rawName)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// A catalogue designation written the way a catalogue writes it: prefix, one space, the
        /// number as given. Used to display SIMBAD's fixed-width identifiers ("NGC   224") without
        /// their padding, and only ever for display.
        /// </summary>
        public static string Tidy(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return rawName;
            string text = rawName.Trim();
            if (text.StartsWith("NAME ", StringComparison.OrdinalIgnoreCase)) text = text.Substring(5);
            return string.Join(" ", text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
