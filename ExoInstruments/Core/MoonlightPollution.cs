using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// One natural satellite of the home body, as the observing-conditions
    /// evaluator needs it: where it is on its orbit (which fixes its position in
    /// the mod's fictional sky frame, same convention as the Sun in
    /// ImagingObservingConditions), how big it looks, and how reflective it is.
    /// Filled from FlightGlobals by the GUI layer -- pure C# here.
    /// </summary>
    public struct MoonContext
    {
        public string Name;
        public double OrbitPeriodSeconds;
        public double MeanAnomalyAtEpochRad;   // KSP convention, radians
        public double OrbitEpochUt;
        public double LanPlusArgPeDeg;         // LAN + argument of periapsis
        public double SemiMajorAxisMeters;     // from the home body's center
        public double BodyRadiusMeters;
        public double Albedo;                  // geometric/Bond-ish; KSP's CelestialBody.albedo
    }

    /// <summary>
    /// Moonlight as an observing constraint, two faces:
    ///
    /// - Occultation: the Mün subtends about a degree of sky from Kerbin
    ///   (~15x the solid angle of Earth's Moon) -- when it crosses the line of
    ///   sight the target is simply gone, for every ground-based method at once.
    ///
    /// - Sky-background pollution: scattered moonlight raises the sky surface
    ///   brightness, and sky photon noise eats faint-target *photometry*.
    ///   Separation dependence follows the Krisciunas &amp; Schaefer (1991)
    ///   scattering shape (Rayleigh + Mie forward-scatter lobe); source
    ///   brightness scales with illuminated fraction, albedo, and apparent
    ///   solid angle. RV spectrographs measure line positions, not integrated
    ///   flux, and the ELT images in H band where scattered moonlight is
    ///   negligible against the OH airglow -- so only transit photometry pays,
    ///   the same scoping logic as AtmosphericNoise.
    ///
    /// Sky-noise magnitude scaling: instrument precision already scales as
    /// 10^(0.2*dm) (source photon noise); a sky-dominated term scales as
    /// 10^(0.4*dm) (fixed sky variance under the aperture, shrinking source
    /// flux) -- which is exactly why moonlight is what drives faint targets off
    /// the schedule around full moon while bright ones barely notice.
    /// </summary>
    public static class MoonlightPollution
    {
        /// <summary>Reference source strength: a full Mün (albedo 0.12, 200 km radius at 12,000 km) -- MoonSkyFactor is sqrt(skyExcess) relative to this at the reference separation.</summary>
        private const double ReferenceMoonFlux = 0.12 * (200000.0 / 12000000.0) * (200000.0 / 12000000.0);

        /// <summary>Separation at which a full Mün yields MoonSkyFactor = 1 -- i.e. doubles the variance of an observation at the instrument's reference magnitude.</summary>
        private const double ReferenceSeparationDeg = 30.0;

        /// <summary>Moon contribution ramps in as it clears the horizon (scattering path shortens); fully counted above this altitude.</summary>
        private const double FullContributionAltitudeDeg = 10.0;

        /// <summary>Per-moon geometry at one instant, for the conditions snapshot and the UI status line.</summary>
        public struct MoonState
        {
            public string Name;
            public double RaDeg;
            public double AltitudeDeg;
            public double IlluminatedFraction;   // 0 = new, 1 = full
            public double AngularRadiusDeg;
            public double SeparationFromTargetDeg;
            public double SkyExcessContribution;  // relative sky-brightness excess near the target
        }

        /// <summary>
        /// Sky position of a moon in the fictional RA/Dec frame: its orbital
        /// angle around the home body, seen *from* the home body (no +180 flip --
        /// that flip is for the Sun, which sits opposite the home body's own
        /// orbital position). Dec 0: the Mün orbits in Kerbin's equatorial plane
        /// exactly; Minmus' 6 deg inclination is neglected, same order of
        /// approximation as the mod's no-axial-tilt Sun.
        /// </summary>
        public static double ComputeMoonRaDeg(double ut, MoonContext moon)
        {
            if (moon.OrbitPeriodSeconds <= 0) return 0.0;
            double meanAnomalyRad = moon.MeanAnomalyAtEpochRad
                + 2.0 * Math.PI * (ut - moon.OrbitEpochUt) / moon.OrbitPeriodSeconds;
            return NormalizeDegrees(meanAnomalyRad * 180.0 / Math.PI + moon.LanPlusArgPeDeg);
        }

        /// <summary>Apparent angular radius from the surface; distance approximated by the orbit's semi-major axis (the observer's 600 km offset is a few percent).</summary>
        public static double AngularRadiusDeg(MoonContext moon)
        {
            if (moon.SemiMajorAxisMeters <= 0) return 0.0;
            double ratio = Math.Min(1.0, moon.BodyRadiusMeters / moon.SemiMajorAxisMeters);
            return Math.Asin(ratio) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Illuminated fraction from solar elongation: (1 - cos(elongation))/2.
        /// Both the Sun and the moons ride Dec 0 in this frame, so elongation is
        /// just the RA difference -- new moon near the Sun, full moon opposite it.
        /// </summary>
        public static double IlluminatedFraction(double moonRaDeg, double sunRaDeg)
        {
            double elongationRad = (moonRaDeg - sunRaDeg) * Math.PI / 180.0;
            return (1.0 - Math.Cos(elongationRad)) / 2.0;
        }

        /// <summary>Great-circle separation between a moon (Dec 0) and the target.</summary>
        public static double SeparationDeg(double moonRaDeg, double targetRaDeg, double targetDecDeg)
        {
            double decRad = targetDecDeg * Math.PI / 180.0;
            double dRaRad = (moonRaDeg - targetRaDeg) * Math.PI / 180.0;
            double cosSep = Math.Cos(decRad) * Math.Cos(dRaRad);
            cosSep = Math.Min(1.0, Math.Max(-1.0, cosSep));
            return Math.Acos(cosSep) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Krisciunas &amp; Schaefer (1991) scattering shape vs. moon-target
        /// separation rho: Rayleigh term (1.06 + cos^2 rho) plus the Mie
        /// forward-scatter lobe 10^(6.15 - rho/40). Returned normalized to the
        /// reference separation so the calibration constant lives in one place.
        /// </summary>
        public static double ScatteringKernel(double separationDeg)
        {
            double rho = Math.Max(0.5, separationDeg);  // kernel diverges into the moon's own disk; occultation handles that region
            double rhoRad = rho * Math.PI / 180.0;
            double cosRho = Math.Cos(rhoRad);
            return Math.Pow(10.0, 5.36) * (1.06 + cosRho * cosRho) + Math.Pow(10.0, 6.15 - rho / 40.0);
        }

        /// <summary>
        /// Evaluates every moon against one target line of sight. Returns the
        /// aggregate sqrt(sky excess) factor (see ExcessNoiseSigma), whether any
        /// moon occults the target outright, and the per-moon states (dominant
        /// first) for the UI.
        /// </summary>
        public static void Evaluate(
            double ut,
            MoonContext[] moons,
            double sunRaDeg, bool hasSun,
            double targetRaDeg, double targetDecDeg,
            double localMeridianRaDeg, double observerLatitudeDeg,
            out double moonSkyFactor, out bool occulted, out string occultingMoonName, out MoonState dominantMoon)
        {
            moonSkyFactor = 0.0;
            occulted = false;
            occultingMoonName = null;
            dominantMoon = default(MoonState);

            if (moons == null || moons.Length == 0) return;

            double kernelAtReference = ScatteringKernel(ReferenceSeparationDeg);
            double totalExcess = 0.0;
            double dominantContribution = -1.0;

            for (int i = 0; i < moons.Length; i++)
            {
                MoonContext moon = moons[i];
                if (moon.OrbitPeriodSeconds <= 0 || moon.SemiMajorAxisMeters <= 0) continue;

                double raDeg = ComputeMoonRaDeg(ut, moon);
                var horizontal = SkyCoordinates.EquatorialToHorizontal(raDeg, 0.0, localMeridianRaDeg, observerLatitudeDeg);
                if (horizontal.AltitudeDeg <= 0.0) continue;  // below the horizon: no scattered light, no occultation

                double angularRadiusDeg = AngularRadiusDeg(moon);
                double separationDeg = SeparationDeg(raDeg, targetRaDeg, targetDecDeg);
                // A new moon still blocks the sky behind it -- occultation is
                // geometry, not illumination.
                if (separationDeg < angularRadiusDeg)
                {
                    occulted = true;
                    occultingMoonName = moon.Name;
                }

                // No Sun on record means no phase to compute -- treat as full
                // (bright-sky-conservative), matching the evaluator's permanent-night fallback.
                double illuminated = hasSun ? IlluminatedFraction(raDeg, sunRaDeg) : 1.0;
                double sizeRatio = moon.BodyRadiusMeters / moon.SemiMajorAxisMeters;
                double moonFlux = Math.Max(0.0, moon.Albedo) * illuminated * sizeRatio * sizeRatio;
                double altitudeRamp = Math.Min(1.0, horizontal.AltitudeDeg / FullContributionAltitudeDeg);
                double contribution = (moonFlux / ReferenceMoonFlux)
                                    * (ScatteringKernel(separationDeg) / kernelAtReference)
                                    * altitudeRamp;
                totalExcess += contribution;

                if (contribution > dominantContribution)
                {
                    dominantContribution = contribution;
                    dominantMoon = new MoonState
                    {
                        Name = moon.Name,
                        RaDeg = raDeg,
                        AltitudeDeg = horizontal.AltitudeDeg,
                        IlluminatedFraction = illuminated,
                        AngularRadiusDeg = angularRadiusDeg,
                        SeparationFromTargetDeg = separationDeg,
                        SkyExcessContribution = contribution,
                    };
                }
            }

            moonSkyFactor = Math.Sqrt(totalExcess);
        }

        /// <summary>
        /// 1-sigma fractional-flux noise excess from moonlit sky, to add in
        /// quadrature with instrument precision and scintillation. Zero for
        /// space-based instruments and non-photometric methods (see class
        /// comment). Scales as 10^(0.4*dm): sky noise, not source noise.
        /// Calibration: MoonSkyFactor 1 (full Mün at the reference separation)
        /// doubles the noise variance at the instrument's reference magnitude.
        /// </summary>
        public static double ExcessNoiseSigma(InstrumentSpec instrument, double apparentMagnitude, double moonSkyFactor)
        {
            if (moonSkyFactor <= 0.0) return 0.0;
            if (instrument.IsSpaceBased) return 0.0;
            if (instrument.Method != DetectionMethod.Transit) return 0.0;

            double referenceSigma = instrument.ReferencePrecision / 1_000_000.0;
            double skyMagnitudeScaling = Math.Pow(10.0, 0.4 * (apparentMagnitude - instrument.ReferenceMagnitude));
            return referenceSigma * moonSkyFactor * skyMagnitudeScaling;
        }

        private static double NormalizeDegrees(double deg)
        {
            double d = deg % 360.0;
            return d < 0 ? d + 360.0 : d;
        }
    }
}
