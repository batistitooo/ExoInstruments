using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Rossiter-McLaughlin effect: a transiting planet blocks rotating starlight,
    /// producing an apparent RV anomaly dV = −f_blocked × vsini × (x·cos λ + y·sin λ).
    /// Peak amplitude ~ depth × vsini. vsini is derived from StellarActivity's rotation
    /// period (sin i* = 1). The spin-orbit angle λ is a persistent per-planet draw —
    /// mostly aligned, with a misaligned tail matching the hot-Jupiter distribution.
    /// </summary>
    public static class RossiterMcLaughlin
    {
        private const double SolarRadiusMeters = 6.957e8;
        private const double SecondsPerDay = 86400.0;

        /// <summary>70% of planets are drawn near-aligned; the rest are uniformly misaligned.</summary>
        private const double AlignedFraction = 0.7;
        private const double AlignedSpreadDeg = 20.0;

        public const double DetectionSnrThreshold = 5.0;
        public const int MinInTransitEpochs = 8;

        /// <summary>Projected rotation speed v_eq × sin(i*) in m/s, with sin(i*) = 1 (unknowable; standard default).</summary>
        public static double ProjectedRotationSpeedMps(StarTarget star)
        {
            if (star.RadiusSolar <= 0) return 0.0;
            double rotationPeriodSeconds = StellarActivity.RotationPeriodDays(star) * SecondsPerDay;
            if (rotationPeriodSeconds <= 0) return 0.0;
            return 2.0 * Math.PI * star.RadiusSolar * SolarRadiusMeters / rotationPeriodSeconds;
        }

        /// <summary>Sky-projected spin-orbit angle λ in (-180, 180], deterministic per planet (same idiom as StellarActivity).</summary>
        public static double SpinOrbitAngleDeg(StarTarget star)
        {
            double selector = Hash01(star, "rmAligned");
            double draw = Hash01(star, "rmLambda");
            if (selector < AlignedFraction)
            {
                return (draw * 2.0 - 1.0) * AlignedSpreadDeg;
            }
            return draw * 360.0 - 180.0;
        }

        /// <summary>Instantaneous RM anomaly (m/s) for one planet. Zero outside transit or without geometry. Blocking the blueshifted limb swings the disk velocity redward (positive).</summary>
        public static double AnomalyMps(StarTarget star, double ut)
        {
            if (!LightCurveSimulator.TryGetTransitChordState(star, ut, out double x, out double dip)) return 0.0;
            if (dip <= 0.0) return 0.0;

            double vsini = ProjectedRotationSpeedMps(star);
            if (vsini <= 0.0) return 0.0;

            double lambdaRad = SpinOrbitAngleDeg(star) * Math.PI / 180.0;
            double y = star.ImpactParameter ?? 0.0;
            double blockedVelocity = vsini * (x * Math.Cos(lambdaRad) + y * Math.Sin(lambdaRad));
            return -dip * blockedVelocity;
        }

        /// <summary>Sum of RM anomalies over a system's transiting planets -- added onto the Keplerian reflex signal by RvSimulator.</summary>
        public static double SystemAnomalyMps(IList<StarTarget> systemPlanets, double ut)
        {
            double total = 0.0;
            for (int i = 0; i < systemPlanets.Count; i++)
            {
                total += AnomalyMps(systemPlanets[i], ut);
            }
            return total;
        }

        // ------------------------------------------------------------------
        // Measurement side.
        // ------------------------------------------------------------------

        public class RmFitResult
        {
            public bool Detected;
            public bool InsufficientData;
            public int InTransitEpochs;
            public double MeasuredVsiniMps;
            public double MeasuredLambdaDeg;
            public double LambdaUncertaintyDeg;
            public double AnomalyAmplitudeMps;   // peak model anomaly over the fitted epochs
            public double Snr;
        }

        /// <summary>
        /// Fits the RM anomaly in prewhitened RV residuals. Linear in two unknowns:
        /// residual = c1×(−dip×x) + c2×(−dip×y), where c1 = vsini·cos λ and c2 = vsini·sin λ.
        /// Requires the photometric ephemeris — the regressors are pure transit geometry.
        /// </summary>
        public static RmFitResult Fit(List<RvSample> residuals, StarTarget transitingPlanet)
        {
            var result = new RmFitResult { InsufficientData = true };
            if (residuals == null || residuals.Count == 0 || transitingPlanet == null) return result;

            double y = transitingPlanet.ImpactParameter ?? 0.0;

            // Build the two regressors at each in-transit epoch.
            var g1 = new List<double>();
            var g2 = new List<double>();
            var v = new List<double>();
            for (int i = 0; i < residuals.Count; i++)
            {
                if (!LightCurveSimulator.TryGetTransitChordState(transitingPlanet, residuals[i].Ut, out double x, out double dip)) return result;
                if (dip <= 0.0) continue;
                g1.Add(-dip * x);
                g2.Add(-dip * y);
                v.Add(residuals[i].VelocityMps);
            }

            result.InTransitEpochs = v.Count;
            if (v.Count < MinInTransitEpochs) return result;
            result.InsufficientData = false;

            // g1 tracks dip×x, g2 tracks dip only — ingress/egress asymmetry separates them and encodes λ.
            double s11 = 0, s12 = 0, s22 = 0, s1v = 0, s2v = 0;
            for (int i = 0; i < v.Count; i++)
            {
                s11 += g1[i] * g1[i];
                s12 += g1[i] * g2[i];
                s22 += g2[i] * g2[i];
                s1v += g1[i] * v[i];
                s2v += g2[i] * v[i];
            }
            double det = s11 * s22 - s12 * s12;

            double c1, c2;
            if (Math.Abs(det) < 1e-12)
            {
                // Degenerate (b ≈ 0 kills the second regressor): fit c1 alone; λ unconstrained.
                if (s11 < 1e-12) return result;
                c1 = s1v / s11;
                c2 = 0.0;
            }
            else
            {
                c1 = (s22 * s1v - s12 * s2v) / det;
                c2 = (s11 * s2v - s12 * s1v) / det;
            }

            double vsini = Math.Sqrt(c1 * c1 + c2 * c2);
            double lambdaDeg = Math.Atan2(c2, c1) * 180.0 / Math.PI;

            // Residual scatter after the RM model, for the uncertainty budget.
            double rss = 0.0;
            double peakModel = 0.0;
            for (int i = 0; i < v.Count; i++)
            {
                double model = c1 * g1[i] + c2 * g2[i];
                double r = v[i] - model;
                rss += r * r;
                if (Math.Abs(model) > peakModel) peakModel = Math.Abs(model);
            }
            double residualStd = Math.Sqrt(rss / Math.Max(1, v.Count - 2));

            // Amplitude uncertainty via the dominant regressor's leverage.
            double sigmaC1 = residualStd / Math.Sqrt(Math.Max(1e-12, s11));
            double snr = sigmaC1 > 0 ? Math.Abs(c1) / sigmaC1 : 0.0;
            // When the anomaly is carried mostly by c2 (polar orbit), score that instead.
            if (Math.Abs(det) >= 1e-12)
            {
                double sigmaC2 = residualStd / Math.Sqrt(Math.Max(1e-12, s22));
                double snr2 = sigmaC2 > 0 ? Math.Abs(c2) / sigmaC2 : 0.0;
                if (snr2 > snr) snr = snr2;
            }

            result.MeasuredVsiniMps = vsini;
            result.MeasuredLambdaDeg = lambdaDeg;
            result.LambdaUncertaintyDeg = snr > 0 ? Math.Min(180.0, 57.3 / snr) : 180.0;
            result.AnomalyAmplitudeMps = peakModel;
            result.Snr = snr;
            result.Detected = snr >= DetectionSnrThreshold;
            return result;
        }

        /// <summary>Next mid-transit UT at or after fromUt; NaN if the ephemeris is unusable.</summary>
        public static double NextTransitCenterUt(StarTarget star, double fromUt)
        {
            if (!star.IsTransiting || star.PlanetPeriodDays <= 0) return double.NaN;
            double periodSeconds = star.PlanetPeriodDays * SecondsPerDay;
            // Mid-transit at phase 0 (see LightCurveSimulator's phaseCentered convention).
            double k = Math.Ceiling(fromUt / periodSeconds + star.PlanetPhaseOffset01);
            return (k - star.PlanetPhaseOffset01) * periodSeconds;
        }

        /// <summary>Next mid-transit whose full window is observable from KSC, scanning up to maxTransits ahead. NaN if ephemeris is unusable, PositiveInfinity if nothing works.</summary>
        public static double NextObservableTransitCenterUt(StarTarget star, double fromUt, ImagingObserverContext observer, int maxTransits = 200)
        {
            double centerUt = NextTransitCenterUt(star, fromUt);
            if (double.IsNaN(centerUt)) return double.NaN;
            double periodSeconds = star.PlanetPeriodDays * SecondsPerDay;
            double halfWindow = (star.EstimatedTransitDurationHours ?? 3.0) * 3600.0;

            for (int i = 0; i < maxTransits; i++)
            {
                double ut = centerUt + i * periodSeconds;
                bool windowObservable =
                    ImagingObservingConditions.Evaluate(ut - halfWindow, star.RaDeg, star.DecDeg, observer).Observable
                    && ImagingObservingConditions.Evaluate(ut, star.RaDeg, star.DecDeg, observer).Observable
                    && ImagingObservingConditions.Evaluate(ut + halfWindow, star.RaDeg, star.DecDeg, observer).Observable;
                if (windowObservable) return ut;
            }
            return double.PositiveInfinity;
        }

        private static double Hash01(StarTarget star, string salt)
        {
            string identity = (star.Name ?? star.CatalogKey ?? "") + "|" + salt;
            const uint fnvPrime = 16777619;
            uint hash = 2166136261;
            for (int i = 0; i < identity.Length; i++)
            {
                hash ^= identity[i];
                hash *= fnvPrime;
            }
            return hash / 4294967296.0;
        }
    }
}
