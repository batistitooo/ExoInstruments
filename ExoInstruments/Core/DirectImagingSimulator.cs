using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// ELT high-contrast direct imaging in H band (1.6 µm, D=39.3 m). Planet flux
    /// uses the catalog temperature when available, else equilibrium Teq with Bond
    /// albedo 0.3. Contrast = Planck ratio × (Rp/R*)². Speckle floor scales as
    /// base × (λ/D / θ)², improving as sqrt(time). Order-of-magnitude estimates.
    /// </summary>
    public static class DirectImagingSimulator
    {
        public const double WavelengthMeters = 1.6e-6;   // H band
        public const double ApertureMeters = 39.3;       // ELT primary
        public const double AssumedBondAlbedo = 0.3;     // assumption, not measurement -- no catalog column
        public const double DeepContrastLimit = 1.0e-8;  // post-processing floor far from the star
        public const double DetectionSnrThreshold = 5.0; // standard imaging detection criterion
        private const double RadiansToArcsec = 206264.806;
        private const double SolarRadiiPerAU = 215.032;  // same constant family as StarTarget
        private const double EarthRadiiPerSolarRadius = 109.2;
        private const double PlanckHcOverK = 8995.9;     // h*c/(lambda*kB) at 1.6 um, in Kelvin

        public static double DiffractionLimitArcsec =>
            1.22 * WavelengthMeters / ApertureMeters * RadiansToArcsec;

        public static DirectImagingAssessment Assess(StarTarget star, InstrumentSpec instrument)
        {
            var a = new DirectImagingAssessment
            {
                DiffractionLimitArcsec = DiffractionLimitArcsec,
                SignalPresent = star.HasPlanet && star.Status != PlanetStatus.Retracted
            };

            double semiMajorAxisAU = star.EstimatedSemiMajorAxisAU;
            if (semiMajorAxisAU <= 0 || star.DistanceParsec <= 0)
            {
                a.MissingDataReason = "no usable orbit/distance on record (semi-major axis or distance missing)";
                return a;
            }
            a.SeparationArcsec = semiMajorAxisAU / star.DistanceParsec;
            a.Resolvable = a.SeparationArcsec > a.DiffractionLimitArcsec;

            if (!star.EffectiveTempK.HasValue || star.RadiusSolar <= 0)
            {
                a.MissingDataReason = "no stellar effective temperature on record";
                return a;
            }
            if (!star.PlanetRadiusEarth.HasValue || star.PlanetRadiusEarth.Value <= 0)
            {
                a.MissingDataReason = "no measured planet radius on record";
                return a;
            }

            double starTempK = star.EffectiveTempK.Value;
            if (star.PlanetTempK.HasValue && star.PlanetTempK.Value > 0)
            {
                a.PlanetTempKUsed = star.PlanetTempK.Value;
                a.PlanetTempFromCatalog = true;
            }
            else
            {
                a.PlanetTempKUsed = EquilibriumTempK(starTempK, star.RadiusSolar, semiMajorAxisAU);
                a.PlanetTempFromCatalog = false;
            }

            double radiusRatioSquared = Math.Pow(
                star.PlanetRadiusEarth.Value / (star.RadiusSolar * EarthRadiiPerSolarRadius), 2.0);
            a.ContrastRatio = PlanckRatio(a.PlanetTempKUsed, starTempK) * radiusRatioSquared;

            a.BaseFloor5Sigma1Hr = instrument.EstimatePrecision(star.ApparentMagnitude);
            a.SpeckleFloor5Sigma1Hr = SpeckleFloorAtSeparation(a.BaseFloor5Sigma1Hr, a.SeparationArcsec);

            a.HasRequiredData = true;
            return a;
        }

        /// <summary>Teq = Teff × sqrt(R*/(2a)) × (1-A)^(1/4). Zero-redistribution equilibrium estimate.</summary>
        public static double EquilibriumTempK(double starTeffK, double starRadiusSolar, double semiMajorAxisAU)
        {
            double starRadiusAU = starRadiusSolar / SolarRadiiPerAU;
            return starTeffK * Math.Sqrt(starRadiusAU / (2.0 * semiMajorAxisAU))
                             * Math.Pow(1.0 - AssumedBondAlbedo, 0.25);
        }

        /// <summary>Ratio of Planck functions at 1.6 um: B(Tp)/B(Tstar) = (exp(x*)-1)/(exp(xp)-1) with x = hc/(lambda k T).</summary>
        public static double PlanckRatio(double planetTempK, double starTempK)
        {
            if (planetTempK <= 0 || starTempK <= 0) return 0.0;
            double xStar = PlanckHcOverK / starTempK;
            double xPlanet = PlanckHcOverK / planetTempK;
            // exp(xPlanet) overflows for very cold planets -- the ratio is effectively
            // zero there anyway, so short-circuit rather than risk Infinity/Infinity.
            if (xPlanet > 700.0) return 0.0;
            return (Math.Exp(xStar) - 1.0) / (Math.Exp(xPlanet) - 1.0);
        }

        /// <summary>5-sigma contrast floor after 1 hour at a given separation. Improves quadratically with separation; returns the base value inside the diffraction limit.</summary>
        public static double SpeckleFloorAtSeparation(double baseFloor1LambdaD, double separationArcsec)
        {
            double thetaDiff = DiffractionLimitArcsec;
            if (separationArcsec <= thetaDiff) return baseFloor1LambdaD;
            double ratio = thetaDiff / separationArcsec;
            return Math.Max(DeepContrastLimit, baseFloor1LambdaD * ratio * ratio);
        }

        /// <summary>SNR after a given integration. exposureSeconds is effective on-sky time (airmass-weighted, night-only), not wall clock.</summary>
        public static double ComputeSnr(DirectImagingAssessment a, double exposureSeconds)
        {
            if (!a.HasRequiredData || !a.Resolvable || !a.SignalPresent) return 0.0;
            if (a.SpeckleFloor5Sigma1Hr <= 0 || exposureSeconds <= 0) return 0.0;
            double hours = exposureSeconds / 3600.0;
            return 5.0 * (a.ContrastRatio / a.SpeckleFloor5Sigma1Hr) * Math.Sqrt(hours);
        }

        /// <summary>Effective on-sky time to reach the detection threshold; PositiveInfinity if undetectable.</summary>
        public static double RequiredExposureSeconds(DirectImagingAssessment a, double snrThreshold = DetectionSnrThreshold)
        {
            if (!a.HasRequiredData || !a.Resolvable || !a.SignalPresent) return double.PositiveInfinity;
            if (a.ContrastRatio <= 0 || a.SpeckleFloor5Sigma1Hr <= 0) return double.PositiveInfinity;
            double snrPerSqrtHour = 5.0 * a.ContrastRatio / a.SpeckleFloor5Sigma1Hr;
            double hours = Math.Pow(snrThreshold / snrPerSqrtHour, 2.0);
            return hours * 3600.0;
        }

        public static DirectImagingResult Analyze(DirectImagingAssessment a, double exposureSeconds, double snrThreshold = DetectionSnrThreshold)
        {
            double snr = ComputeSnr(a, exposureSeconds);
            return new DirectImagingResult
            {
                Assessment = a,
                ExposureSeconds = exposureSeconds,
                Snr = snr,
                Detected = snr >= snrThreshold
            };
        }
    }
}
