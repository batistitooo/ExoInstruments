using System;

namespace ExoInstruments.Core
{
    public enum HzVerdict
    {
        TooHot,          // inside even the optimistic (Recent Venus) inner edge
        ConservativeHz,  // between runaway greenhouse and maximum greenhouse
        OptimisticOnly,  // in the optimistic band but outside the conservative one
        TooCold          // beyond even the optimistic (Early Mars) outer edge
    }

    /// <summary>Habitable-zone boundary distances (AU) for one star, plus the luminosity they derive from.</summary>
    public class HabitableZoneResult
    {
        public double LuminositySolar { get; set; }
        public double InnerOptimisticAU { get; set; }   // Recent Venus
        public double InnerConservativeAU { get; set; } // Runaway Greenhouse (1 M_earth)
        public double OuterConservativeAU { get; set; } // Maximum Greenhouse
        public double OuterOptimisticAU { get; set; }   // Early Mars
    }

    /// <summary>
    /// Habitable-zone boundaries from Kopparapu et al. 2013 (ApJ 765, 131, as
    /// corrected by the published erratum) and Kopparapu et al. 2014 (ApJL 787,
    /// L29, 1 M_earth runaway greenhouse). Each boundary is a polynomial in
    /// T* = Teff - 5780 K giving the effective stellar flux Seff at that edge:
    ///   Seff = Seff_sun + a*T* + b*T*^2 + c*T*^3 + d*T*^4
    /// and the boundary distance follows from d(AU) = sqrt(L/Lsun / Seff).
    /// Luminosity comes from Stefan-Boltzmann: L/Lsun = (R/Rsun)^2 (Teff/5778)^4.
    /// The fits are only valid for 2600 K &lt;= Teff &lt;= 7200 K; outside that,
    /// Compute returns null rather than extrapolating.
    /// </summary>
    public static class HabitableZoneCalculator
    {
        public const double MinValidTeffK = 2600.0;
        public const double MaxValidTeffK = 7200.0;
        private const double SolarTeffK = 5778.0;

        // Kopparapu et al. 2013 erratum / 2014 Table 1 coefficients {Seff_sun, a, b, c, d}.
        private static readonly double[] RecentVenus = { 1.776, 2.136e-4, 2.533e-8, -1.332e-11, -3.097e-15 };
        private static readonly double[] RunawayGreenhouse = { 1.107, 1.332e-4, 1.580e-8, -8.308e-12, -1.931e-15 }; // 1 M_earth (Kopparapu 2014)
        private static readonly double[] MaximumGreenhouse = { 0.356, 6.171e-5, 1.698e-9, -3.198e-12, -5.575e-16 };
        private static readonly double[] EarlyMars = { 0.320, 5.547e-5, 1.526e-9, -2.874e-12, -5.011e-16 };

        /// <summary>Null when Teff/radius are unusable or Teff falls outside the fit's validity range.</summary>
        public static HabitableZoneResult Compute(double teffK, double radiusSolar)
        {
            if (radiusSolar <= 0) return null;
            if (teffK < MinValidTeffK || teffK > MaxValidTeffK) return null;

            double tRatio = teffK / SolarTeffK;
            double luminositySolar = radiusSolar * radiusSolar * tRatio * tRatio * tRatio * tRatio;

            return new HabitableZoneResult
            {
                LuminositySolar = luminositySolar,
                InnerOptimisticAU = BoundaryDistanceAU(luminositySolar, teffK, RecentVenus),
                InnerConservativeAU = BoundaryDistanceAU(luminositySolar, teffK, RunawayGreenhouse),
                OuterConservativeAU = BoundaryDistanceAU(luminositySolar, teffK, MaximumGreenhouse),
                OuterOptimisticAU = BoundaryDistanceAU(luminositySolar, teffK, EarlyMars)
            };
        }

        public static HzVerdict Classify(HabitableZoneResult zone, double semiMajorAxisAU)
        {
            if (semiMajorAxisAU < zone.InnerOptimisticAU) return HzVerdict.TooHot;
            if (semiMajorAxisAU > zone.OuterOptimisticAU) return HzVerdict.TooCold;
            if (semiMajorAxisAU >= zone.InnerConservativeAU && semiMajorAxisAU <= zone.OuterConservativeAU)
                return HzVerdict.ConservativeHz;
            return HzVerdict.OptimisticOnly;
        }

        private static double BoundaryDistanceAU(double luminositySolar, double teffK, double[] c)
        {
            double tStar = teffK - 5780.0; // the papers' own reference temperature, not SolarTeffK
            double seff = c[0] + c[1] * tStar + c[2] * tStar * tStar
                        + c[3] * tStar * tStar * tStar + c[4] * tStar * tStar * tStar * tStar;
            return Math.Sqrt(luminositySolar / seff);
        }
    }
}
