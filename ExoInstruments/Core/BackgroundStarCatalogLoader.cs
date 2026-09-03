using System;
using System.Collections.Generic;
using System.Globalization;

namespace ExoInstruments.Core
{
    /// <summary>
    /// One Bright Star Catalogue star plus the identifiers the merger needs to
    /// cross-match it against exoplanet.eu hosts. Kept separate from StarTarget
    /// itself: HR/HD numbers and normalized name keys are merge-time metadata,
    /// not gameplay state.
    /// </summary>
    public class BackgroundStarEntry
    {
        public StarTarget Target { get; set; }
        public int HrNumber { get; set; }
        public int? HdNumber { get; set; }
        public List<string> NameKeys { get; set; }  // normalized Bayer/Flamsteed designations

        /// <summary>
        /// Effective temperature derived from the BSC B-V color index
        /// (Ballesteros 2012). Also used by the merger to backfill exoplanet.eu
        /// hosts whose export lacks star_teff (HR 8799 in the current export),
        /// the consumed BSC entry knows the star's color even when the planet
        /// catalog forgot its temperature.
        /// </summary>
        public double? DerivedTeffK { get; set; }
    }

    public class BackgroundStarLoadResult
    {
        public List<BackgroundStarEntry> Entries { get; set; }
        public int Loaded { get; set; }
        public int SkippedNoPosition { get; set; }   // novae/clusters, V/50 carries 14 non-stellar rows
        public int SkippedNoMagnitude { get; set; }
    }

    /// <summary>
    /// Parses a VizieR TSV export of the Yale Bright Star Catalogue (V/50,
    /// Hoffleit &amp; Warren 1991, ~9110 stars to V~6.5) into decoy StarTargets:
    /// HasPlanet = false, no planetary data of any kind. Pure C#, no file I/O,
    /// no Unity/KSP dependency, same division of labor as ExoplanetCsvLoader.
    ///
    /// Expected export (regenerate with the same query if the file is lost):
    /// https://vizier.cds.unistra.fr/viz-bin/asu-tsv?-source=V/50/catalog
    ///   &amp;-out.max=unlimited&amp;-out=HR,Name,HD,Vmag,B-V
    ///   &amp;-out.add=_RAJ2000,_DEJ2000&amp;-oc.form=dec
    /// Format: '#' comment lines, a tab-separated header row, a units row and a
    /// dashes row (both skipped because their HR column isn't an integer), then data.
    /// </summary>
    public static class BackgroundStarCatalogLoader
    {
        public static BackgroundStarLoadResult LoadFromTsv(string tsvText)
        {
            var result = new BackgroundStarLoadResult { Entries = new List<BackgroundStarEntry>() };
            if (string.IsNullOrWhiteSpace(tsvText)) return result;

            var lines = tsvText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            Dictionary<string, int> colIndex = null;

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;

                if (colIndex == null)
                {
                    string[] headers = line.Split('\t');
                    colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < headers.Length; i++)
                        colIndex[headers[i].Trim()] = i;
                    continue;
                }

                string[] row = line.Split('\t');

                // The units row ("deg deg ... mag") and the dashes row fail this
                // parse, which is exactly why HR is checked first.
                int? hr = GetInt(row, colIndex, "HR");
                if (!hr.HasValue) continue;

                double? raDeg = GetDouble(row, colIndex, "_RAJ2000");
                double? decDeg = GetDouble(row, colIndex, "_DEJ2000");
                if (!raDeg.HasValue || !decDeg.HasValue)
                {
                    result.SkippedNoPosition++;
                    continue;
                }

                double? vmag = GetDouble(row, colIndex, "Vmag");
                if (!vmag.HasValue)
                {
                    result.SkippedNoMagnitude++;
                    continue;
                }

                int? hd = GetInt(row, colIndex, "HD");
                double? teffK = StellarColor.TeffFromColorIndexBV(GetDouble(row, colIndex, "B-V"));
                string rawName = GetRaw(row, colIndex, "Name");

                ParseBscName(rawName, out string flamsteed, out string bayer, out string constellation);
                string friendlyName = BuildFriendlyName(flamsteed, bayer, constellation, hd, hr.Value);
                var nameKeys = BuildNameKeys(flamsteed, bayer, constellation);

                var target = new StarTarget
                {
                    Name = friendlyName,
                    HostStarName = friendlyName,
                    CatalogKey = "hr " + hr.Value,

                    // No planet, and no invented planetary or stellar bulk data:
                    // BSC5 carries neither mass nor radius, and its distances are
                    // pre-Hipparcos; 0 means "unknown", which every downstream
                    // consumer (transit geometry, RV mass function, imaging
                    // separation) already treats as "cannot compute".
                    HasPlanet = false,
                    RadiusSolar = 0.0,
                    StellarMassSolar = 0.0,
                    DistanceParsec = 0.0,
                    PlanetPeriodDays = 0.0,
                    PlanetPhaseOffset01 = 0.0,

                    RaDeg = raDeg,
                    DecDeg = decDeg,
                    ApparentMagnitude = vmag.Value,

                    // Derived from the BSC B-V color index (Ballesteros 2012), not a
                    // spectroscopic measurement, good to a few percent on the main
                    // sequence, which covers the display tint and characterization
                    // uses. Null when the row carries no color.
                    EffectiveTempK = teffK,
                    EffectiveTempDerivedFromColor = teffK.HasValue,
                };

                result.Entries.Add(new BackgroundStarEntry
                {
                    Target = target,
                    HrNumber = hr.Value,
                    HdNumber = hd,
                    NameKeys = nameKeys,
                    DerivedTeffK = teffK,
                });
                result.Loaded++;
            }

            DisambiguateDuplicateNames(result.Entries);
            return result;
        }

        // BSC Name is a 10-character fixed-width field: columns [0,3) Flamsteed number, [3,6) Greek-letter
        // abbreviation, [6] Bayer superscript index, [7,10) constellation. " 21Alp And" -> 21 / Alp / "" / And;
        // " 50Ups1Eri" -> 50 / Ups / 1 / Eri; " Mu 1Sco" -> "" / Mu / 1 / Sco. Padded left because the
        // constellation is right-anchored.
        private static void ParseBscName(string rawName, out string flamsteed, out string bayer, out string constellation)
        {
            flamsteed = null;
            bayer = null;
            constellation = null;
            if (string.IsNullOrWhiteSpace(rawName)) return;

            string name = rawName.Length < 10 ? rawName.PadLeft(10) : rawName;
            string flam = name.Substring(0, 3).Trim();
            string greek = name.Substring(3, 3).Trim();
            string index = name.Substring(6, 1).Trim();
            string constl = name.Substring(7, 3).Trim();

            if (constl.Length == 0) return; // not a Bayer/Flamsteed designation (e.g. "NOVA 1572")

            constellation = constl;
            if (flam.Length > 0) flamsteed = flam;
            if (greek.Length > 0) bayer = greek + index;  // "Ups" + "2" -> "Ups2"
        }

        // Human-facing designation, most-recognizable form first: Bayer ("alp And"), else Flamsteed ("51 Peg"),
        // else HD, else HR. Matches the abbreviated style exoplanet.eu itself uses ("ups And", "mu2 Sco").
        private static string BuildFriendlyName(string flamsteed, string bayer, string constellation, int? hd, int hr)
        {
            if (bayer != null) return bayer.ToLowerInvariant() + " " + constellation;
            if (flamsteed != null) return flamsteed + " " + constellation;
            if (hd.HasValue) return "HD " + hd.Value;
            return "HR " + hr;
        }

        private static List<string> BuildNameKeys(string flamsteed, string bayer, string constellation)
        {
            var keys = new List<string>(2);
            if (constellation == null) return keys;
            if (bayer != null)
            {
                string k = StarNames.Normalize(bayer + " " + constellation);
                if (k != null) keys.Add(k);
            }
            if (flamsteed != null)
            {
                string k = StarNames.Normalize(flamsteed + " " + constellation);
                if (k != null) keys.Add(k);
            }
            return keys;
        }

        // A handful of visual-binary components share one Bayer/Flamsteed name in BSC (e.g. HR 8085/8086 are
        // both "61 Cyg"); append the HR number so the player-facing list never shows two identical entries.
        private static void DisambiguateDuplicateNames(List<BackgroundStarEntry> entries)
        {
            var counts = new Dictionary<string, int>();
            foreach (var e in entries)
            {
                counts.TryGetValue(e.Target.Name, out int n);
                counts[e.Target.Name] = n + 1;
            }
            foreach (var e in entries)
            {
                if (counts[e.Target.Name] > 1 && !e.Target.Name.StartsWith("HR "))
                {
                    e.Target.Name += " (HR " + e.HrNumber + ")";
                    e.Target.HostStarName = e.Target.Name;
                }
            }
        }

        private static string GetRaw(string[] row, Dictionary<string, int> colIndex, string column)
        {
            if (!colIndex.TryGetValue(column, out int i) || i >= row.Length) return null;
            return row[i];
        }

        private static double? GetDouble(string[] row, Dictionary<string, int> colIndex, string column)
        {
            string v = GetRaw(row, colIndex, column)?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : (double?)null;
        }

        private static int? GetInt(string[] row, Dictionary<string, int> colIndex, string column)
        {
            string v = GetRaw(row, colIndex, column)?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : (int?)null;
        }
    }
}
