using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ExoInstruments.Core
{
    public class CsvLoadResult
    {
        public List<StarTarget> Targets { get; set; }
        public int Loaded { get; set; }
        public int SkippedNoStarData { get; set; }
        public int SkippedNoMagnitude { get; set; }
        public int NoCoordinates { get; set; } // loaded fine, but ra/dec missing, won't appear on the sky chart
    }

    /// <summary>
    /// Parses a CSV/TSV export from exoplanet.eu's catalog into StarTarget entries.
    /// Pure C#, no file I/O, no Unity/KSP dependency. The KSP glue layer reads
    /// the file from disk, passes the raw text in, and handles logging on its own.
    /// Delimiter (comma or tab) is auto-detected from the header row.
    /// </summary>
    public static class ExoplanetCsvLoader
    {
        private const double JupiterRadiusInEarthRadii = 11.209;

        public static CsvLoadResult LoadFromCsv(string csvText)
        {
            var result = new CsvLoadResult { Targets = new List<StarTarget>() };
            if (string.IsNullOrWhiteSpace(csvText)) return result;

            var lines = csvText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            if (lines.Length < 2) return result;

            char delimiter = lines[0].IndexOf('\t') >= 0 ? '\t' : ',';
            string[] headers = SplitLine(lines[0], delimiter);
            var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
                colIndex[headers[i].Trim()] = i;

            for (int lineNum = 1; lineNum < lines.Length; lineNum++)
            {
                if (string.IsNullOrWhiteSpace(lines[lineNum])) continue;
                string[] row = SplitLine(lines[lineNum], delimiter);

                string name = Get(row, colIndex, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                double? starMass = GetDouble(row, colIndex, "star_mass");
                double? starRadius = GetDouble(row, colIndex, "star_radius");
                double? distance = GetDouble(row, colIndex, "star_distance");
                if (!starMass.HasValue || !starRadius.HasValue || !distance.HasValue)
                {
                    result.SkippedNoStarData++;
                    continue;
                }

                double? magnitude = GetDouble(row, colIndex, "mag_v")
                                     ?? GetDouble(row, colIndex, "mag_i")
                                     ?? GetDouble(row, colIndex, "mag_j")
                                     ?? GetDouble(row, colIndex, "mag_h")
                                     ?? GetDouble(row, colIndex, "mag_k");
                if (!magnitude.HasValue)
                {
                    result.SkippedNoMagnitude++;
                    continue;
                }

                double? periodDays = GetDouble(row, colIndex, "orbital_period");
                if (!periodDays.HasValue || periodDays.Value <= 0) continue;

                double? radiusJup = GetDouble(row, colIndex, "radius");
                double? planetRadiusEarth = radiusJup.HasValue ? radiusJup.Value * JupiterRadiusInEarthRadii : (double?)null;

                double? raDeg = GetDouble(row, colIndex, "ra");
                double? decDeg = GetDouble(row, colIndex, "dec");
                if (!raDeg.HasValue || !decDeg.HasValue) result.NoCoordinates++;

                var target = new StarTarget
                {
                    Name = name,
                    HostStarName = Get(row, colIndex, "star_name"),
                    HostStarAlternateNames = Get(row, colIndex, "star_alternate_names"),
                    PlanetAlternateNames = Get(row, colIndex, "alternate_names"),
                    Status = ParseStatus(Get(row, colIndex, "planet_status")),
                    DetectionType = Get(row, colIndex, "detection_type"),
                    DiscoveryYear = (int?)GetDouble(row, colIndex, "discovered"),

                    RadiusSolar = starRadius.Value,
                    StellarMassSolar = starMass.Value,
                    DistanceParsec = distance.Value,
                    RaDeg = raDeg,
                    DecDeg = decDeg,
                    ApparentMagnitude = magnitude.Value,
                    EffectiveTempK = GetDouble(row, colIndex, "star_teff"),

                    HasPlanet = true,
                    PlanetRadiusEarth = planetRadiusEarth,
                    // Both mass columns are kept. `mass` is the true mass where some other
                    // measurement (a transit, an astrometric orbit) constrained the inclination;
                    // `mass_sini` is what the RV data alone gives, and it is the one the RV mass
                    // function takes. Collapsing them into one field made K wrong by 1/sin(i) on
                    // the 168 entries whose two masses differ by more than 10%.
                    PlanetMassJupiter = GetDouble(row, colIndex, "mass") ?? GetDouble(row, colIndex, "mass_sini"),
                    PlanetMinimumMassJupiter = GetDouble(row, colIndex, "mass_sini"),
                    PublishedRvSemiAmplitudeMps = GetDouble(row, colIndex, "k"),
                    PublishedRvSemiAmplitudeErrorMps = LargerError(row, colIndex, "k_error_min", "k_error_max"),
                    TimeOfPeriastronJd = GetDouble(row, colIndex, "tperi"),
                    PlanetPeriodDays = periodDays.Value,
                    SemiMajorAxisAU = GetDouble(row, colIndex, "semi_major_axis"),
                    Eccentricity = GetDouble(row, colIndex, "eccentricity") ?? 0.0,
                    InclinationDeg = GetDouble(row, colIndex, "inclination"),
                    MeasuredImpactParameter = GetDouble(row, colIndex, "impact_parameter"),
                    PlanetPhaseOffset01 = DeterministicPhaseOffset(name),
                    ArgumentOfPeriastronDeg = GetDouble(row, colIndex, "omega"),
                    // Measured takes precedence: temp_calculated is the catalog's own
                    // equilibrium estimate, temp_measured comes from actual spectra/photometry.
                    PlanetTempK = GetDouble(row, colIndex, "temp_measured") ?? GetDouble(row, colIndex, "temp_calculated"),
                };

                target.CatalogKey = StarNames.CatalogKeyForHost(target.HostStarName, target.Name);

                result.Targets.Add(target);
                result.Loaded++;
            }

            return result;
        }

        private static PlanetStatus ParseStatus(string raw)
        {
            if (raw != null && Enum.TryParse(raw, true, out PlanetStatus status)) return status;
            return PlanetStatus.Confirmed;
        }

        private static double DeterministicPhaseOffset(string name)
        {
            unchecked
            {
                int hash = 0;
                foreach (char c in name) hash = hash * 31 + c;
                return (Math.Abs(hash) % 10000) / 10000.0;
            }
        }

        private static string Get(string[] row, Dictionary<string, int> colIndex, string column)
        {
            if (!colIndex.TryGetValue(column, out int i) || i >= row.Length) return null;
            string v = row[i].Trim().Trim('"');
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        /// <summary>
        /// The catalog's error bars are asymmetric (separate min/max columns). Collapsed to the
        /// larger of the two: a single symmetric sigma is what a comparison against a recovered
        /// value needs, and taking the larger side keeps it from claiming false precision.
        /// </summary>
        private static double? LargerError(string[] row, Dictionary<string, int> colIndex, string minColumn, string maxColumn)
        {
            double? lo = GetDouble(row, colIndex, minColumn);
            double? hi = GetDouble(row, colIndex, maxColumn);
            if (!lo.HasValue && !hi.HasValue) return null;
            return Math.Max(Math.Abs(lo ?? 0.0), Math.Abs(hi ?? 0.0));
        }

        private static double? GetDouble(string[] row, Dictionary<string, int> colIndex, string column)
        {
            string v = Get(row, colIndex, column);
            if (v == null) return null;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : (double?)null;
        }

        private static string[] SplitLine(string line, char delimiter)
        {
            var fields = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();
            foreach (char c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (c == delimiter && !inQuotes) { fields.Add(current.ToString()); current.Clear(); continue; }
                current.Append(c);
            }
            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}