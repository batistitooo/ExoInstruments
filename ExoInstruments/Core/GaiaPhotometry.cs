using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Gaia's own published transformations between its photometric system and Johnson-Cousins.
    ///
    /// WHY THIS IS NEEDED. Everything downstream of the star catalogue works in Johnson V and B-V:
    /// the magnitude normalisation (948 photons/cm^2/s/Angstrom at V), the colour term, the
    /// rendered star field. Gaia measures G, G_BP and G_RP instead. Any use of Gaia data therefore
    /// has to cross that boundary, and crossing it by assuming G = V would be wrong by up to a
    /// magnitude for a red star.
    ///
    /// THE COEFFICIENTS ARE GAIA'S OWN. From the Gaia DR3 documentation, "Photometric
    /// relationships with other photometric systems" (Table 5.9), the polynomial in
    /// (G_BP - G_RP) for the Johnson-Cousins V band:
    ///
    ///     G - V = -0.02704 + 0.01424*x - 0.2156*x^2 + 0.01426*x^3,   x = G_BP - G_RP
    ///
    /// valid over -0.5 &lt; x &lt; 5.0 (Table 5.10) with a residual scatter of 0.03017 mag. Nothing
    /// here is fitted, adjusted or extrapolated by this project.
    ///
    /// Pure C# with no Unity dependency, like the rest of Core.
    /// </summary>
    public static class GaiaPhotometry
    {
        // Gaia DR3 Table 5.9, G - V as a polynomial in (G_BP - G_RP), constant term first.
        private static readonly double[] GMinusVCoefficients = { -0.02704, 0.01424, -0.2156, 0.01426 };

        /// <summary>Residual scatter of that relation, in magnitudes (Gaia DR3 Table 5.9).</summary>
        public const double GMinusVScatterMag = 0.03017;

        /// <summary>Lower bound of the relation's published validity range in G_BP - G_RP (Table 5.10).</summary>
        public const double MinBpRp = -0.5;

        /// <summary>Upper bound of the relation's published validity range in G_BP - G_RP (Table 5.10).</summary>
        public const double MaxBpRp = 5.0;

        /// <summary>
        /// G - V for a star of the given Gaia colour.
        ///
        /// Outside the published validity range the colour is CLAMPED to the range's edge rather
        /// than extrapolated. A cubic fitted over -0.5 to 5.0 diverges fast beyond it, and an
        /// extrapolated value would be this project inventing photometry Gaia did not publish;
        /// holding the edge value is visibly an approximation instead of silently a fabrication.
        /// The same choice SpectralCurve makes outside a measured QE curve's range.
        /// </summary>
        public static double GMinusV(double bpRp)
        {
            double x = Math.Max(MinBpRp, Math.Min(MaxBpRp, bpRp));
            double result = 0.0, power = 1.0;
            for (int i = 0; i < GMinusVCoefficients.Length; i++)
            {
                result += GMinusVCoefficients[i] * power;
                power *= x;
            }
            return result;
        }

        /// <summary>Johnson V from a Gaia G magnitude and colour.</summary>
        public static double VFromG(double gMag, double bpRp) => gMag - GMinusV(bpRp);

        /// <summary>Gaia G from a Johnson V magnitude and Gaia colour.</summary>
        public static double GFromV(double vMag, double bpRp) => vMag + GMinusV(bpRp);

        /// <summary>
        /// Johnson B-V from Gaia's G_BP - G_RP.
        ///
        /// Gaia's Table 5.9 publishes the inverse direction, (G_BP - G_RP) as a polynomial in
        /// (B - V), and does NOT publish this one. Rather than invent a fit, this inverts the
        /// published polynomial numerically by bisection, so the only quantity used is still
        /// Gaia's own. Returns NaN when the requested colour falls outside what the published
        /// relation can produce, which the caller must treat as "no colour known" rather than
        /// substituting a default.
        /// </summary>
        public static double BMinusVFromBpRp(double bpRp)
        {
            // Bracket in B-V over the range the published relation covers. Monotonic across it,
            // which is what makes bisection valid here.
            double lo = -0.4, hi = 2.0;
            double fLo = BpRpFromBMinusV(lo) - bpRp;
            double fHi = BpRpFromBMinusV(hi) - bpRp;
            if (fLo * fHi > 0.0) return double.NaN;

            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                double fMid = BpRpFromBMinusV(mid) - bpRp;
                if (fLo * fMid <= 0.0) { hi = mid; fHi = fMid; }
                else { lo = mid; fLo = fMid; }
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>
        /// Gaia DR3 Table 5.9, (G_BP - G_RP) as a polynomial in (B - V):
        ///     G_BP - G_RP = -0.03298 + 1.259*y - 0.1155*y^2 + 0.0364*y^3,  y = B - V
        /// </summary>
        public static double BpRpFromBMinusV(double bMinusV)
        {
            double y = bMinusV;
            return -0.03298 + y * (1.259 + y * (-0.1155 + y * 0.0364));
        }
    }
}
