using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Star-name normalization and identifier extraction, shared by the catalog
    /// merger (cross-matching exoplanet.eu host names against Bright Star
    /// Catalogue designations) and by the career fog-of-war save state (stable
    /// per-star keys). Pure C# -- no Unity/KSP dependency.
    ///
    /// The same physical star appears under different conventions across the two
    /// catalogs: "51 Peg" / "51 Pegasi" / "HD 217014" are one star. Normalization
    /// reduces every variant to one canonical lowercase form ("51 peg") by
    /// canonicalizing Greek-letter spellings (beta/bet, alf/alp, ksi/xi...) and
    /// constellation genitives (Pegasi -> Peg, Ursae Majoris -> UMa).
    /// </summary>
    public static class StarNames
    {
        // Greek letters: every spelling seen in the wild (full name, IAU-style and
        // BSC-style abbreviations) -> one canonical 3-letters-or-fewer token.
        private static readonly Dictionary<string, string> GreekCanonical = new Dictionary<string, string>
        {
            { "alpha", "alp" }, { "alf", "alp" }, { "alp", "alp" },
            { "beta", "bet" }, { "bet", "bet" },
            { "gamma", "gam" }, { "gam", "gam" },
            { "delta", "del" }, { "del", "del" },
            { "epsilon", "eps" }, { "eps", "eps" },
            { "zeta", "zet" }, { "zet", "zet" },
            { "eta", "eta" },
            { "theta", "the" }, { "the", "the" }, { "tet", "the" },
            { "iota", "iot" }, { "iot", "iot" },
            { "kappa", "kap" }, { "kap", "kap" },
            { "lambda", "lam" }, { "lam", "lam" },
            { "mu", "mu" },
            { "nu", "nu" },
            { "xi", "xi" }, { "ksi", "xi" },
            { "omicron", "omi" }, { "omi", "omi" },
            { "pi", "pi" },
            { "rho", "rho" },
            { "sigma", "sig" }, { "sig", "sig" },
            { "tau", "tau" },
            { "upsilon", "ups" }, { "ups", "ups" },
            { "phi", "phi" },
            { "chi", "chi" }, { "khi", "chi" },
            { "psi", "psi" },
            { "omega", "ome" }, { "ome", "ome" },
        };

        // Two-word constellation genitives, checked before the single-word table
        // (otherwise "Leonis Minoris" would be eaten by the standalone
        // "leonis" -> Leo mapping). Keys are lowercase.
        private static readonly Dictionary<string, string> ConstellationGenitivePairs = new Dictionary<string, string>
        {
            { "canum venaticorum", "cvn" },
            { "canis majoris", "cma" },
            { "canis minoris", "cmi" },
            { "comae berenices", "com" },
            { "coronae australis", "cra" },
            { "coronae borealis", "crb" },
            { "leonis minoris", "lmi" },
            { "piscis austrini", "psa" },
            { "trianguli australis", "tra" },
            { "ursae majoris", "uma" },
            { "ursae minoris", "umi" },
        };

        // Single-word constellation genitives -> IAU 3-letter abbreviation
        // (lowercase on both sides; the 3-letter abbreviations themselves need
        // no entry because lowercasing already canonicalizes "UMa"/"Uma"/"uma").
        private static readonly Dictionary<string, string> ConstellationGenitive = new Dictionary<string, string>
        {
            { "andromedae", "and" }, { "antliae", "ant" }, { "apodis", "aps" },
            { "aquarii", "aqr" }, { "aquilae", "aql" }, { "arae", "ara" },
            { "arietis", "ari" }, { "aurigae", "aur" },
            { "bootis", "boo" }, { "bootes", "boo" },
            { "caeli", "cae" }, { "camelopardalis", "cam" }, { "cancri", "cnc" },
            { "capricorni", "cap" }, { "carinae", "car" }, { "cassiopeiae", "cas" },
            { "centauri", "cen" }, { "cephei", "cep" }, { "ceti", "cet" },
            { "chamaeleontis", "cha" }, { "circini", "cir" }, { "columbae", "col" },
            { "corvi", "crv" }, { "crateris", "crt" }, { "crucis", "cru" },
            { "cygni", "cyg" }, { "delphini", "del" }, { "doradus", "dor" },
            { "draconis", "dra" }, { "equulei", "equ" }, { "eridani", "eri" },
            { "fornacis", "for" }, { "geminorum", "gem" }, { "gruis", "gru" },
            { "herculis", "her" }, { "horologii", "hor" }, { "hydrae", "hya" },
            { "hydri", "hyi" }, { "indi", "ind" }, { "lacertae", "lac" },
            { "leonis", "leo" }, { "leporis", "lep" }, { "librae", "lib" },
            { "lupi", "lup" }, { "lyncis", "lyn" }, { "lyrae", "lyr" },
            { "mensae", "men" }, { "microscopii", "mic" }, { "monocerotis", "mon" },
            { "muscae", "mus" }, { "normae", "nor" }, { "octantis", "oct" },
            { "ophiuchi", "oph" }, { "orionis", "ori" }, { "pavonis", "pav" },
            { "pegasi", "peg" }, { "persei", "per" }, { "phoenicis", "phe" },
            { "pictoris", "pic" }, { "piscium", "psc" }, { "puppis", "pup" },
            { "pyxidis", "pyx" }, { "reticuli", "ret" }, { "sagittae", "sge" },
            { "sagittarii", "sgr" }, { "scorpii", "sco" }, { "sculptoris", "scl" },
            { "scuti", "sct" }, { "serpentis", "ser" }, { "sextantis", "sex" },
            { "tauri", "tau" }, { "telescopii", "tel" }, { "trianguli", "tri" },
            { "tucanae", "tuc" }, { "velorum", "vel" }, { "virginis", "vir" },
            { "volantis", "vol" }, { "vulpeculae", "vul" },
        };

        // exoplanet.eu exports carry raw HTML entities in a few names
        // ("24 Bo&ouml;" for 24 Boo) -- reduce accented entities to their base
        // ASCII letter before any other processing.
        private static readonly Regex HtmlEntityRegex = new Regex(
            @"&([a-zA-Z])(acute|grave|uml|circ|tilde|ring|cedil|slash);?",
            RegexOptions.Compiled);

        private static readonly Regex ParentheticalRegex = new Regex(@"\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex TrailingDigitRegex = new Regex(@"^([a-z]+)([1-9])$", RegexOptions.Compiled);

        // "HD 217014", "HD217014", "hd 217014  b" -- the digits are what matters.
        private static readonly Regex HdRegex = new Regex(@"\bHD[\s-]*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HrRegex = new Regex(@"\bHR[\s-]*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Canonical lowercase form of a star designation: HTML entities decoded,
        /// parentheticals dropped, Greek letters and constellation genitives
        /// canonicalized, whitespace collapsed. Returns null for null/blank input.
        /// "51 Pegasi" -> "51 peg", "beta Pic" -> "bet pic", "ups2 Eri" -> "ups2 eri".
        /// </summary>
        public static string Normalize(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return null;

            string s = HtmlEntityRegex.Replace(rawName, "$1");
            s = ParentheticalRegex.Replace(s, " ");
            s = s.ToLowerInvariant();

            string[] rawTokens = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var tokens = new List<string>(rawTokens.Length);

            for (int i = 0; i < rawTokens.Length; i++)
            {
                string token = rawTokens[i];

                // Two-word genitives first ("ursae majoris" -> "uma").
                if (i + 1 < rawTokens.Length)
                {
                    string pair = token + " " + rawTokens[i + 1];
                    if (ConstellationGenitivePairs.TryGetValue(pair, out string pairAbbrev))
                    {
                        tokens.Add(pairAbbrev);
                        i++;
                        continue;
                    }
                }

                if (ConstellationGenitive.TryGetValue(token, out string constAbbrev))
                {
                    tokens.Add(constAbbrev);
                    continue;
                }

                // Greek letter, possibly with an attached index ("rho1", "ups2").
                string bare = token;
                string index = "";
                Match m = TrailingDigitRegex.Match(token);
                if (m.Success)
                {
                    bare = m.Groups[1].Value;
                    index = m.Groups[2].Value;
                }
                if (GreekCanonical.TryGetValue(bare, out string greek))
                {
                    tokens.Add(greek + index);
                    continue;
                }

                tokens.Add(token);
            }

            return tokens.Count == 0 ? null : string.Join(" ", tokens);
        }

        /// <summary>
        /// Every HD number found anywhere in the given designation strings
        /// (host name, alternate-name lists, planet designations -- a trailing
        /// planet letter doesn't disturb the match).
        /// </summary>
        public static List<int> ExtractHdNumbers(params string[] designations)
        {
            return ExtractNumbers(HdRegex, designations);
        }

        /// <summary>Every HR (Bright Star) number found in the given designation strings.</summary>
        public static List<int> ExtractHrNumbers(params string[] designations)
        {
            return ExtractNumbers(HrRegex, designations);
        }

        private static List<int> ExtractNumbers(Regex regex, string[] designations)
        {
            var numbers = new List<int>();
            foreach (string text in designations)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                foreach (Match m in regex.Matches(text))
                {
                    if (int.TryParse(m.Groups[1].Value, out int n) && !numbers.Contains(n))
                        numbers.Add(n);
                }
            }
            return numbers;
        }

        /// <summary>
        /// Stable save-file identifier for the star a target belongs to. Planets of
        /// one host share the key (scanning a star reveals the whole system), and
        /// the key survives catalog reloads because it derives from the catalog's
        /// own designation, not object identity or list position.
        /// </summary>
        public static string CatalogKeyForHost(string hostStarName, string fallbackName)
        {
            string key = Normalize(hostStarName) ?? Normalize(fallbackName);
            return key ?? "unnamed";
        }

        /// <summary>
        /// IAU-style truncated positional designation ("J2257+2046") -- what a
        /// survey would call a source it hasn't identified yet. Truncation, not
        /// rounding, per the IAU specification for coordinate-based identifiers.
        /// </summary>
        public static string ProvisionalDesignation(double raDeg, double decDeg)
        {
            double ra = raDeg % 360.0;
            if (ra < 0) ra += 360.0;
            double raHours = ra / 15.0;
            int raHH = (int)raHours;
            int raMM = (int)((raHours - raHH) * 60.0);

            char sign = decDeg < 0 ? '-' : '+';
            double absDec = Math.Min(90.0, Math.Abs(decDeg));
            int decDD = (int)absDec;
            int decMM = (int)((absDec - decDD) * 60.0);

            var sb = new StringBuilder(10);
            sb.Append('J');
            sb.Append(raHH.ToString("00")).Append(raMM.ToString("00"));
            sb.Append(sign);
            sb.Append(decDD.ToString("00")).Append(decMM.ToString("00"));
            return sb.ToString();
        }
    }
}
