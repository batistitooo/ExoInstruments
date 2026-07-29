using System;
using System.Globalization;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Reading and writing equatorial coordinates in the form catalogues quote them and observers
    /// type them. Pure C#, no Unity dependency, so it can be checked against astropy's parser.
    /// </summary>
    public static class SexagesimalCoordinates
    {
        /// <summary>"05h35m17.3s -05d23'28"" -- the form a target list is written in.</summary>
        public static string Format(double raDeg, double decDeg)
        {
            double raHours = Normalize360(raDeg) / 15.0;
            int rh = (int)raHours;
            double raMinutes = (raHours - rh) * 60.0;
            int rm = (int)raMinutes;
            double rs = (raMinutes - rm) * 60.0;

            double absDec = Math.Abs(decDeg);
            int dd = (int)absDec;
            double decMinutes = (absDec - dd) * 60.0;
            int dm = (int)decMinutes;
            double ds = (decMinutes - dm) * 60.0;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}h{1:00}m{2:00.0}s {3}{4:00}d{5:00}'{6:00}\"",
                rh, rm, rs, decDeg < 0 ? "-" : "+", dd, dm, ds);
        }

        /// <summary>
        /// Parses a right ascension and declination pair. One numeric field is decimal degrees;
        /// two or three are sexagesimal, and a sexagesimal right ascension is in HOURS, which is
        /// the convention every catalogue writes it in. Returns false rather than guessing.
        /// </summary>
        public static bool TryParse(string raText, string decText, out double raDeg, out double decDeg)
        {
            raDeg = decDeg = double.NaN;
            if (!TryParseAngle(raText, out double ra, out int raFields)) return false;
            if (!TryParseAngle(decText, out double dec, out int decFields)) return false;

            raDeg = raFields > 1 ? ra * 15.0 : ra;
            decDeg = dec;
            return !double.IsNaN(raDeg) && !double.IsNaN(decDeg)
                && decDeg >= -90.0 && decDeg <= 90.0;
        }

        private static bool TryParseAngle(string text, out double value, out int fieldCount)
        {
            value = double.NaN;
            fieldCount = 0;
            if (string.IsNullOrEmpty(text)) return false;

            string cleaned = text.Trim()
                .Replace('h', ' ').Replace('m', ' ').Replace('s', ' ')
                .Replace('d', ' ').Replace('\'', ' ').Replace('"', ' ')
                .Replace('°', ' ').Replace(':', ' ').Replace(',', '.');
            string[] parts = cleaned.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > 3) return false;

            // The sign belongs to the whole angle, not to each field: "-05 23 28" is minus five
            // degrees twenty-three minutes, not minus five plus twenty-three.
            bool negative = parts[0].TrimStart().StartsWith("-");
            double total = 0.0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                    return false;
                if (i > 0 && (p < 0.0 || p >= 60.0)) return false;
                total += Math.Abs(p) / Math.Pow(60.0, i);
            }

            fieldCount = parts.Length;
            value = negative ? -total : total;
            return true;
        }

        public static double Normalize360(double deg)
        {
            double d = deg % 360.0;
            return d < 0.0 ? d + 360.0 : d;
        }
    }
}
