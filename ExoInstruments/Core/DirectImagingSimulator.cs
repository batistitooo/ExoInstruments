using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Physics for ELT-class high-contrast direct imaging. Pure C#, mirrors the
    /// other pipelines' honesty about approximation:
    ///
    /// - Angular separation: theta(arcsec) = a(AU) / d(pc) (small-angle/parallax).
    /// - Diffraction limit: theta = 1.22 lambda/D (H band, 1.6 um, D = 39.3 m).
    /// - Planet flux: blackbody at the catalog's planet temperature when available
    ///   (temp_measured/temp_calculated -- the right driver for young self-luminous
    ///   giants, whose near-IR light is internal heat), else the irradiation
    ///   equilibrium temperature Teq = Teff*sqrt(R*/2a)*(1-A)^(1/4) with an assumed
    ///   Bond albedo of 0.3 (Earth-like; no catalog column for it).
    /// - Contrast: Planck ratio at 1.6 um times (Rp/R*)^2. Blackbodies underestimate
    ///   the H-band flux of young giants somewhat (non-equilibrium chemistry) --
    ///   order-of-magnitude honest, same caveat class as the BLS/sinusoid detectors.
    /// - Speckle floor: post-processed 5-sigma contrast limit modeled as
    ///   base * (theta_diff/theta)^2, clamped at a 1e-8 deep limit, improving as
    ///   sqrt(integration time). Order-of-magnitude for ELT extreme-AO predictions
    ///   (Kasper et al. 2021, PCS); the base at 1 lambda/D and its magnitude scaling
    ///   come from the InstrumentSpec (see Observatories.Elt).
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

        /// <summary>Teq = Teff * sqrt(R*/(2a)) * (1-A)^(1/4), the standard zero-redistribution-free equilibrium estimate.</summary>
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

        /// <summary>
        /// 5-sigma contrast floor after 1 hour at a given separation: quadratic
        /// improvement with separation (speckle halo falls off), clamped at the
        /// deep post-processing limit. Inside the diffraction limit the floor is
        /// meaningless (nothing is resolvable there) -- returns the base value.
        /// </summary>
        public static double SpeckleFloorAtSeparation(double baseFloor1LambdaD, double separationArcsec)
        {
            double thetaDiff = DiffractionLimitArcsec;
            if (separationArcsec <= thetaDiff) return baseFloor1LambdaD;
            double ratio = thetaDiff / separationArcsec;
            return Math.Max(DeepContrastLimit, baseFloor1LambdaD * ratio * ratio);
        }

        /// <summary>
        /// SNR after a given integration: (contrast / 5-sigma 1-hr floor) * 5 * sqrt(hours).
        /// exposureSeconds is EFFECTIVE on-sky time (zenith-equivalent, airmass-weighted,
        /// night-only -- see ImagingObservationSession), not wall-clock time.
        /// </summary>
        public static double ComputeSnr(DirectImagingAssessment a, double exposureSeconds)
        {
            if (!a.HasRequiredData || !a.Resolvable || !a.SignalPresent) return 0.0;
            if (a.SpeckleFloor5Sigma1Hr <= 0 || exposureSeconds <= 0) return 0.0;
            double hours = exposureSeconds / 3600.0;
            return 5.0 * (a.ContrastRatio / a.SpeckleFloor5Sigma1Hr) * Math.Sqrt(hours);
        }

        /// <summary>Effective on-sky integration needed to reach the detection threshold; PositiveInfinity when nothing can ever be detected.</summary>
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
