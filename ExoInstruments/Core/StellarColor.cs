using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Stellar temperature and color helpers, pure C#.
    ///
    /// - B-V color index to effective temperature: Ballesteros 2012 (EPL 97,
    ///   34008), a blackbody-based fit calibrated so B-V = 0.65 gives the Sun's
    ///   5778 K. Good to a few percent on the main sequence, which is all the
    ///   display and characterization uses here need.
    /// - Effective temperature to an sRGB display tint: piecewise fit to the
    ///   Planckian locus (T. Helland's 2012 fit to M. Charity's blackbody color
    ///   data). This is a *display* mapping -- an H-band frame is monochromatic,
    ///   the tint encodes the star's measured color temperature for the player.
    /// - Temperature to MK spectral class: standard main-sequence Teff ranges
    ///   (O &gt;= 30000 K, B 10000-30000, A 7500-10000, F 6000-7500, G 5200-6000,
    ///   K 3700-5200, M below).
    /// </summary>
    public static class StellarColor
    {
        /// <summary>
        /// Ballesteros 2012: T = 4600 K * (1/(0.92(B-V) + 1.7) + 1/(0.92(B-V) + 0.62)).
        /// Null for a color index outside any physically sensible range.
        /// </summary>
        public static double? TeffFromColorIndexBV(double? colorIndexBV)
        {
            if (!colorIndexBV.HasValue) return null;
            double bv = colorIndexBV.Value;
            if (bv < -0.5 || bv > 2.5) return null; // beyond O-star blue / far-M red -- likely bad data
            return 4600.0 * (1.0 / (0.92 * bv + 1.7) + 1.0 / (0.92 * bv + 0.62));
        }

        /// <summary>
        /// sRGB tint (components 0-1) of a blackbody at the given temperature.
        /// Piecewise log/power fit to the Planckian locus (Helland 2012), valid
        /// roughly 1000-40000 K; clamped outside. Very cold sources (planets at
        /// a few hundred K) saturate to deep red, which is the honest limit of
        /// an RGB display.
        /// </summary>
        public static void BlackbodyRgb(double teffK, out double r, out double g, out double b)
        {
            double t = Math.Min(40000.0, Math.Max(1000.0, teffK)) / 100.0;

            r = t <= 66.0 ? 1.0 : 1.29294 * Math.Pow(t - 60.0, -0.1332047592);
            g = t <= 66.0
                ? 0.39008 * Math.Log(t) - 0.63184
                : 1.12989 * Math.Pow(t - 60.0, -0.0755148492);
            b = t >= 66.0 ? 1.0
                : t <= 19.0 ? 0.0
                : 0.54320 * Math.Log(t - 10.0) - 1.19625;

            r = Clamp01(r);
            g = Clamp01(g);
            b = Clamp01(b);
        }

        /// <summary>MK spectral class letter for a main-sequence effective temperature.</summary>
        public static string SpectralClass(double teffK)
        {
            if (teffK >= 30000.0) return "O";
            if (teffK >= 10000.0) return "B";
            if (teffK >= 7500.0) return "A";
            if (teffK >= 6000.0) return "F";
            if (teffK >= 5200.0) return "G";
            if (teffK >= 3700.0) return "K";
            return "M";
        }

        private static double Clamp01(double v)
        {
            return v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
        }
    }
}
