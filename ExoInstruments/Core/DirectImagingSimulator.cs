using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// ELT high-contrast direct imaging in H band (1.6 µm, D=39.3 m). Planet flux
    /// uses the catalog temperature when available, else equilibrium Teq with Bond
    /// albedo 0.3. Contrast = Planck ratio * (Rp/R*)^2. Speckle floor scales as
    /// base * (lambda/D / theta)^2, improving as sqrt(time). Order-of-magnitude estimates.
    /// </summary>
    public static class DirectImagingSimulator
    {
        public const double WavelengthMeters = 1.6e-6;   // H band
        public const double ApertureMeters = 39.3;       // ELT primary

        /// <summary>
        /// Linear central obstruction of the ELT pupil. ESO's own E-ELT optics page states the
        /// segmented primary "has a diameter of approximately 39 m" with "a 11.1 m central
        /// obstruction" (the filled primary runs from an inner radius of 5.5 m to an outer 18.5 m).
        /// The ratio is formed against the 39.3 m this class already uses everywhere else, so the
        /// pupil stays internally consistent: 11.1/39.3 = 0.2824, against 0.2846 if ESO's rounded
        /// 39 m were used instead, a 0.8% difference far below anything the pattern shows.
        ///
        /// This is what makes a real diffraction pattern computable here at all. Without an
        /// obstruction ratio the pupil is undefined, and the imaging display had to invent a
        /// profile; see RadialPsfProfile.
        /// </summary>
        public const double ObstructionRatio = 11.1 / 39.3;

        /// <summary>
        /// The ELT's secondary support. **Schwartz et al. 2018** (AO4ELT5, "Sensing and control of
        /// segmented mirrors with a pyramid wavefront sensor in the presence of spiders") states it
        /// outright: "The secondary mirror unit of the European Extremely Large Telescope (ELT) is
        /// supported by six 50-cm wide spiders, providing the necessary stiffness to the structure
        /// while minimising the obstruction of the beam." ESO's own main-structure page independently
        /// confirms the count: the M2 crown "is connected to the top ring by means of six beams,
        /// forming the 'spider'".
        ///
        /// The published width is not perfectly settled: METIS phase D simulations quote 54 cm, and
        /// at least one pupil figure in the literature is drawn with 40 cm. 50 cm is used because it
        /// is the figure stated in prose by an ESO-co-authored paper rather than read off a diagram,
        /// and because the spread is small where it matters: spike brightness scales as the vane
        /// area squared, so 40 to 54 cm spans a factor 1.8 in an effect that is itself ~1e-4 of the
        /// peak. Quantified in TECHNICAL_REFERENCE section 7.112 rather than hidden.
        /// </summary>
        public const int SpiderVaneCount = 6;
        public const double SpiderVaneWidthMeters = 0.50;
        public const double AssumedBondAlbedo = 0.3;     // assumption, not measurement, no catalog column
        public const double DeepContrastLimit = 1.0e-8;  // post-processing floor far from the star
        public const double DetectionSnrThreshold = 5.0; // standard imaging detection criterion
        private const double RadiansToArcsec = 206264.806;
        private const double SolarRadiiPerAU = 215.032;  // same constant family as StarTarget
        private const double EarthRadiiPerSolarRadius = 109.2;
        private const double PlanckHcOverK = 8995.9;     // h*c/(lambda*kB) at 1.6 um, in Kelvin

        /// <summary>lambda/D in arcsec: the pattern's natural angular unit, and the scale its rings repeat on.</summary>
        public static double LambdaOverDArcsec => WavelengthMeters / ApertureMeters * RadiansToArcsec;

        /// <summary>
        /// The telescope's real resolution limit: the first null of ITS OWN pupil's diffraction
        /// pattern, found on the exact profile.
        ///
        /// This used to return the textbook 1.22*lambda/D. That figure is the first null of an
        /// UNOBSTRUCTED circular aperture, and the ELT is not one: its 11.1 m secondary obstruction
        /// pushes the first null inward, to 1.124*lambda/D (9.44 mas in H band against 10.24 mas).
        /// Quoting the unobstructed number while modelling an obstructed pupil made the simulator
        /// disagree with its own optics by 8.4%, and the frame it drew showed the disagreement:
        /// the guide ring marking "the diffraction limit" sat visibly outside the first dark ring
        /// the pattern actually had.
        ///
        /// Computed once. FirstNullRad scans and bisects the exact profile, which is far too much
        /// work to repeat per pixel, and the pupil never changes at runtime.
        /// </summary>
        public static readonly double DiffractionLimitArcsec =
            RadialPsfProfile.FirstNullRad(ApertureMeters, ObstructionRatio, WavelengthMeters) * RadiansToArcsec;

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

        /// <summary>Teq = Teff * sqrt(R*/(2a)) * (1-A)^(1/4). Zero-redistribution equilibrium estimate.</summary>
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
            // exp(xPlanet) overflows for very cold planets; the ratio is effectively
            // zero there anyway, so short-circuit rather than risk Infinity/Infinity.
            if (xPlanet > 700.0) return 0.0;
            return (Math.Exp(xStar) - 1.0) / (Math.Exp(xPlanet) - 1.0);
        }

        /// <summary>
        /// 5-sigma contrast floor after 1 hour at a given separation. Improves quadratically with
        /// separation; returns the base value inside one lambda/D.
        ///
        /// Scaled against lambda/D rather than against the first null. The speckle field's own
        /// grid spacing is lambda/D, which is what sets how many independent speckles fall at a
        /// given separation; the first null is a property of the core, not of the halo. The two
        /// were the same quantity here only as long as DiffractionLimitArcsec was a fixed multiple
        /// of lambda/D, which it no longer is now that it comes from the real obstructed pupil.
        /// </summary>
        public static double SpeckleFloorAtSeparation(double baseFloor1LambdaD, double separationArcsec)
        {
            double lambdaOverD = LambdaOverDArcsec;
            if (separationArcsec <= lambdaOverD) return baseFloor1LambdaD;
            double ratio = lambdaOverD / separationArcsec;
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
