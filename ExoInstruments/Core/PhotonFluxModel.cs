using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Real apparent magnitude of a sunlit reflecting body, and real electron counts
    /// collected from it through a real telescope+filter+detector chain. Pure C#, no
    /// Unity/KSP dependency; callers pass in plain doubles (radius, albedo, distances,
    /// phase angle) already extracted from a live CelestialBody.
    /// </summary>
    public static class PhotonFluxModel
    {
        /// <summary>
        /// Real V-band apparent magnitude of the Sun at the reference distance, where the star delivers the
        /// solar constant (1 AU for the Sun). Standard photometric constant.
        /// </summary>
        public const double SunApparentMagnitudeV = -26.74;

        /// <summary>IAU-defined astronomical unit, in meters. The reference distance for the real sky.</summary>
        public const double AuMeters = 149597870700.0;

        /// <summary>The solar constant SunApparentMagnitudeV corresponds to, W/m2 (Kopp and Lean 2011).</summary>
        public const double SolarConstantWm2 = 1361.0;

        /// <summary>
        /// Semi-major axis of the home world's star-orbiting ancestor, metres, walked up parent as
        /// PhysicsGlobals.CalculateValues walks it: a moon home world is lit at its planet's distance.
        /// NaN when the chain never reaches the star.
        /// </summary>
        public static double HomeStarOrbitMeters<T>(T home, T star, Func<T, T> parent, Func<T, double> semiMajorAxis)
            where T : class
        {
            if (home == null || star == null || ReferenceEquals(home, star)) return double.NaN;
            T body = home;
            for (int depth = 0; depth < 64; depth++)
            {
                T up = parent(body);
                if (up == null || ReferenceEquals(up, body)) return double.NaN;
                if (ReferenceEquals(up, star))
                {
                    double a = semiMajorAxis(body);
                    return a > 0.0 ? a : double.NaN;
                }
                body = up;
            }
            return double.NaN;
        }

        /// <summary>
        /// Where the star delivers SolarConstantWm2, metres, given the home orbit and the flux the game
        /// delivers there (Physics.cfg solarLuminosityAtHome). The AU when the orbit is unknown.
        /// </summary>
        public static double SunReferenceDistanceMeters(double homeStarOrbitMeters, double solarFluxAtHomeWm2)
        {
            if (!(homeStarOrbitMeters > 0.0)) return AuMeters;
            double flux = solarFluxAtHomeWm2 > 0.0 ? solarFluxAtHomeWm2 : SolarConstantWm2;
            return homeStarOrbitMeters * Math.Sqrt(flux / SolarConstantWm2);
        }

        /// <summary>
        /// Real V-band zero-magnitude photon flux density (Vega calibration), 948
        /// photons/cm^2/s/Angstrom at the V effective wavelength (5556 Angstrom), the
        /// standard reference value used across observational photometry and exposure-time
        /// calculators.
        ///
        /// This is a MONOCHROMATIC flux density at 5556 Angstrom, and it is used as one: it fixes
        /// the absolute scale of a source's spectrum at that single wavelength, and SystemResponse
        /// then integrates the spectrum's own shape across the instrument's passband from there.
        /// The one thing this does not reproduce is the definition of the V magnitude itself,
        /// which is properly an integral over the Johnson V transmission curve rather than a
        /// sample at its effective wavelength; closing that would need the V passband tabulated
        /// (Bessell 1990) and its integrated photon zero point, and is recorded as a remaining
        /// simplification rather than approximated.
        /// </summary>
        public const double ZeroMagPhotonFluxPerAngstrom = 948.0;

        /// <summary>
        /// Lambertian-sphere phase integral (Russell 1916): the fraction of a diffusely
        /// reflecting sphere's full-phase brightness visible at phase angle alpha. 1.0 at
        /// alpha=0 (full phase), 0 at alpha=pi (new/unlit). More rigorous than a raw cosine
        /// half-phase approximation; this is the textbook phase law for a Lambertian sphere.
        /// </summary>
        public static double LambertianPhaseFunction(double phaseAngleRad)
        {
            double alpha = Math.Max(0.0, Math.Min(Math.PI, phaseAngleRad));
            return (Math.Sin(alpha) + (Math.PI - alpha) * Math.Cos(alpha)) / Math.PI;
        }

        /// <summary>
        /// V magnitude of the Sun seen from distanceMeters. referenceDistanceMeters is where it delivers the
        /// solar constant (SunApparentMagnitudeV there): AuMeters for the real sky, the home world's orbit in KSP.
        /// </summary>
        public static double SunApparentMagnitude(double distanceMeters, double referenceDistanceMeters)
        {
            if (!(distanceMeters > 0.0) || !(referenceDistanceMeters > 0.0)) return double.PositiveInfinity;
            return SunApparentMagnitudeV + 5.0 * Math.Log10(distanceMeters / referenceDistanceMeters);
        }

        /// <summary>
        /// Real apparent magnitude of a sunlit spherical body, via the standard planetary
        /// H-G-system flux-ratio formalism: fluxRatio = albedo * (R/d_obs)^2 * (d_ref/d_sun)^2 *
        /// phi(alpha), m_body = m_sun - 2.5*log10(fluxRatio). radiusMeters/distances are the
        /// body's own real radius and its real distances to the Sun and to the observer.
        /// sunReferenceDistanceMeters: where the star delivers the solar constant; AuMeters for the
        /// real sky.
        /// The albedo is used as the geometric albedo, unconverted. KSP's thermal code treats it as Bond,
        /// but measured phase integrals (0.44 to 1.35) sit below the Lambert 1.5, so p = 2A/3 fits worse.
        /// Returns +Infinity if the body has no usable geometry (can't be a signal source).
        /// </summary>
        public static double ApparentMagnitude(
            double albedo, double radiusMeters,
            double distanceToSunMeters, double distanceToObserverMeters,
            double phaseAngleRad, double sunReferenceDistanceMeters)
        {
            if (albedo <= 0.0 || radiusMeters <= 0.0 || distanceToSunMeters <= 0.0 || distanceToObserverMeters <= 0.0
                || !(sunReferenceDistanceMeters > 0.0))
                return double.PositiveInfinity;

            double sizeRatio = radiusMeters / distanceToObserverMeters;
            double sunRatio = sunReferenceDistanceMeters / distanceToSunMeters;
            double phi = LambertianPhaseFunction(phaseAngleRad);
            if (phi <= 0.0) return double.PositiveInfinity;

            double fluxRatio = albedo * sizeRatio * sizeRatio * sunRatio * sunRatio * phi;
            if (fluxRatio <= 0.0 || double.IsNaN(fluxRatio)) return double.PositiveInfinity;

            return SunApparentMagnitudeV - 2.5 * Math.Log10(fluxRatio);
        }

        /// <summary>
        /// Real electrons collected from a source of the given apparent magnitude, through a real
        /// telescope aperture over a real exposure.
        ///
        /// Everything spectral is carried by effectiveWidthAngstrom, the system's effective
        /// photometric width from SystemResponse: the filter profile, the optical throughput, the
        /// detector's QE curve, atmospheric extinction and the source's own colour, integrated
        /// over the passband rather than sampled at one wavelength. See SystemBandpass for the
        /// derivation and for why this single number is enough.
        ///
        /// Zero for a non-signal (infinite magnitude).
        /// </summary>
        public static double CollectedElectrons(
            double apparentMagnitude, double effectiveWidthAngstrom,
            double apertureAreaCm2, double exposureSeconds)
        {
            if (double.IsPositiveInfinity(apparentMagnitude) || double.IsNaN(apparentMagnitude)) return 0.0;

            double photonFluxPerCm2PerSecond = ZeroMagPhotonFluxPerAngstrom
                * Math.Pow(10.0, -0.4 * apparentMagnitude)
                * Math.Max(0.0, effectiveWidthAngstrom);

            return photonFluxPerCm2PerSecond
                * Math.Max(0.0, apertureAreaCm2)
                * Math.Max(0.0, exposureSeconds);
        }

        /// <summary>
        /// The superseded grey-band form: one rectangular bandwidth, one scalar QE, one
        /// transmission, no spectral integration at all.
        ///
        /// Kept for exactly one purpose, and not called from the pipeline: the headless harness
        /// asserts that SystemResponse's integral reduces to THIS expression when the source
        /// spectrum is flat, the QE is grey and the atmosphere is transparent. That makes the new
        /// model a provable generalisation of the old one rather than a replacement that merely
        /// resembles it, which is the kind of claim a paper has to be able to back.
        /// </summary>
        public static double CollectedElectronsGreyBand(
            double apparentMagnitude, double filterBandwidthAngstrom,
            double apertureAreaCm2, double quantumEfficiency,
            double exposureSeconds, double extinctionTransmission)
        {
            return CollectedElectrons(
                apparentMagnitude,
                Math.Max(0.0, filterBandwidthAngstrom) * Math.Max(0.0, quantumEfficiency) * Math.Max(0.0, extinctionTransmission),
                apertureAreaCm2, exposureSeconds);
        }
    }
}
