using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The Rossiter-McLaughlin effect: a transiting planet crossing the rotating
    /// stellar disk blocks a moving sliver of blueshifted, then redshifted (or
    /// vice versa) starlight, distorting the disk-averaged line profile into an
    /// apparent radial-velocity anomaly. First-order model (Ohta, Taruya &amp;
    /// Suto 2005): the anomaly is the negative of the blocked region's rotation
    /// velocity times the blocked flux fraction,
    ///
    ///   dV = -f_blocked * v_eq*sin(i_star) * (x*cos(lambda) + y*sin(lambda))
    ///
    /// with (x, y) the planet's projected position in stellar radii (x along the
    /// transit chord, y the impact parameter) and lambda the sky-projected
    /// spin-orbit angle. Peak amplitude ~ depth * vsini -- tens of m/s for a hot
    /// Jupiter, the classic RM regime.
    ///
    /// This is the first mechanic where the transit and RV pipelines observe the
    /// same event together: measuring lambda is how real surveys discovered that
    /// hot Jupiters are often badly misaligned with their star's spin -- direct
    /// fossil evidence of violent migration.
    ///
    /// vsini derives from the same stellar rotation period StellarActivity
    /// already assigns (v_eq = 2*pi*R_star/P_rot, sin(i_star) taken as 1), so an
    /// active fast rotator is loud in spots, jitter, AND RM at once -- one
    /// consistent star. lambda is a persistent per-planet draw: mostly aligned,
    /// with a genuinely misaligned tail (the observed hot-Jupiter distribution,
    /// e.g. Albrecht et al. 2012).
    /// </summary>
    public static class RossiterMcLaughlin
    {
        private const double SolarRadiusMeters = 6.957e8;
        private const double SecondsPerDay = 86400.0;

        /// <summary>Fraction of planets drawn well-aligned; the rest get a uniform misalignment -- the qualitative shape of the measured obliquity distribution.</summary>
        private const double AlignedFraction = 0.7;
        private const double AlignedSpreadDeg = 20.0;

        public const double DetectionSnrThreshold = 5.0;
        public const int MinInTransitEpochs = 8;

        /// <summary>Projected equatorial rotation speed v_eq*sin(i_star), m/s, with sin(i_star) = 1 (unknowable here; the standard optimistic default).</summary>
        public static double ProjectedRotationSpeedMps(StarTarget star)
        {
            if (star.RadiusSolar <= 0) return 0.0;
            double rotationPeriodSeconds = StellarActivity.RotationPeriodDays(star) * SecondsPerDay;
            if (rotationPeriodSeconds <= 0) return 0.0;
            return 2.0 * Math.PI * star.RadiusSolar * SolarRadiusMeters / rotationPeriodSeconds;
        }

        /// <summary>
        /// Persistent sky-projected spin-orbit angle for this planet, degrees in
        /// (-180, 180]. Deterministic hash draw, same idiom as StellarActivity:
        /// the same planet always has the same obliquity, nothing stored.
        /// </summary>
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

        /// <summary>
        /// Instantaneous RM anomaly (m/s) for one planet. Zero outside transit,
        /// for RV-only geometry, or for a non-rotating star. Sign convention:
        /// blocking the approaching (blueshifted) limb removes blueshifted light,
        /// so the disk-averaged velocity swings redward (positive).
        /// </summary>
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
        /// Fits the RM anomaly in RV residuals (Keplerian signals already
        /// prewhitened out -- see RvDetector.DetectMultiple's final stage) for a
        /// planet with a known transit ephemeris. Linear in the two unknowns:
        ///
        ///   residual = c1 * [-dip*x] + c2 * [-dip*y]  =>  c1 = vsini*cos(lambda), c2 = vsini*sin(lambda)
        ///
        /// which is why RM analyses need the photometric ephemeris first: the
        /// regressors are pure transit geometry evaluated at each epoch. SNR is
        /// the fitted anomaly amplitude over its propagated uncertainty.
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

            // 2x2 normal equations. The y regressor is x's scaled copy when b is
            // constant along the chord... it isn't: g2 tracks dip only while g1
            // tracks dip*x, so ingress/egress asymmetry separates them -- exactly
            // the information content real RM fits use to get lambda.
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
                // Degenerate (b ~ 0 kills the second regressor): fit c1 alone,
                // lambda unconstrained in its sin component -- report it as 0.
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

        /// <summary>
        /// Next mid-transit UT at or after fromUt for a planet with known
        /// geometry; NaN when the ephemeris can't be computed. Used to schedule
        /// the high-cadence RM sequence -- real RM campaigns are literally built
        /// around this number.
        /// </summary>
        public static double NextTransitCenterUt(StarTarget star, double fromUt)
        {
            if (!star.IsTransiting || star.PlanetPeriodDays <= 0) return double.NaN;
            double periodSeconds = star.PlanetPeriodDays * SecondsPerDay;
            // Mid-transit at phase 0 (see LightCurveSimulator's phaseCentered convention).
            double k = Math.Ceiling(fromUt / periodSeconds + star.PlanetPhaseOffset01);
            return (k - star.PlanetPhaseOffset01) * periodSeconds;
        }

        /// <summary>
        /// Next mid-transit whose full window (center +/- one duration) is
        /// observable from the site, scanning up to maxTransits epochs ahead --
        /// no point warping to a transit that happens in daytime. NaN when the
        /// ephemeris is unusable, PositiveInfinity when nothing in range works.
        /// </summary>
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
