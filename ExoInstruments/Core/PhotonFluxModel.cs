using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Real apparent magnitude of a sunlit reflecting body, and real electron counts
    /// collected from it through a real telescope+filter+detector chain. Pure C#, no
    /// Unity/KSP dependency -- callers pass in plain doubles (radius, albedo, distances,
    /// phase angle) already extracted from a live CelestialBody.
    /// </summary>
    public static class PhotonFluxModel
    {
        /// <summary>Real V-band apparent magnitude of the Sun at 1 AU (standard photometric constant).</summary>
        public const double SunApparentMagnitudeV = -26.74;

        /// <summary>IAU-defined astronomical unit, in meters.</summary>
        public const double AuMeters = 149597870700.0;

        /// <summary>
        /// Real V-band zero-magnitude photon flux density (Vega calibration), 948
        /// photons/cm^2/s/Angstrom at the V effective wavelength (5556 Angstrom) -- the
        /// standard reference value used across observational photometry and exposure-time
        /// calculators. Applied here across each filter's own bandwidth as a flat-spectrum
        /// approximation (the same simplification real back-of-envelope ETC calculations use).
        /// </summary>
        public const double ZeroMagPhotonFluxPerAngstrom = 948.0;

        /// <summary>
        /// Lambertian-sphere phase integral (Russell 1916): the fraction of a diffusely
        /// reflecting sphere's full-phase brightness visible at phase angle alpha. 1.0 at
        /// alpha=0 (full phase), 0 at alpha=pi (new/unlit). More rigorous than a raw cosine
        /// half-phase approximation -- this is the textbook phase law for a Lambertian sphere.
        /// </summary>
        public static double LambertianPhaseFunction(double phaseAngleRad)
        {
            double alpha = Math.Max(0.0, Math.Min(Math.PI, phaseAngleRad));
            return (Math.Sin(alpha) + (Math.PI - alpha) * Math.Cos(alpha)) / Math.PI;
        }

        /// <summary>
        /// Real apparent magnitude of a sunlit spherical body, via the standard planetary
        /// H-G-system flux-ratio formalism: fluxRatio = albedo * (R/d_obs)^2 / d_sun^2 *
        /// phi(alpha), m_body = m_sun - 2.5*log10(fluxRatio). radiusMeters/distances are the
        /// body's own real radius and its real distances to the Sun and to the observer.
        /// Returns +Infinity if the body has no usable geometry (can't be a signal source).
        /// </summary>
        public static double ApparentMagnitude(
            double albedo, double radiusMeters,
            double distanceToSunMeters, double distanceToObserverMeters,
            double phaseAngleRad)
        {
            if (albedo <= 0.0 || radiusMeters <= 0.0 || distanceToSunMeters <= 0.0 || distanceToObserverMeters <= 0.0)
                return double.PositiveInfinity;

            double rAu = radiusMeters / AuMeters;
            double dObsAu = distanceToObserverMeters / AuMeters;
            double dSunAu = distanceToSunMeters / AuMeters;
            double sizeRatio = rAu / dObsAu;
            double phi = LambertianPhaseFunction(phaseAngleRad);
            if (phi <= 0.0) return double.PositiveInfinity;

            double fluxRatio = albedo * sizeRatio * sizeRatio / (dSunAu * dSunAu) * phi;
            if (fluxRatio <= 0.0 || double.IsNaN(fluxRatio)) return double.PositiveInfinity;

            return SunApparentMagnitudeV - 2.5 * Math.Log10(fluxRatio);
        }

        /// <summary>
        /// Real electrons collected from a source of the given apparent magnitude, through a
        /// real telescope aperture, filter bandpass, detector QE, exposure time, and
        /// atmospheric extinction transmission. Zero for a non-signal (infinite magnitude).
        /// </summary>
        public static double CollectedElectrons(
            double apparentMagnitude, double filterBandwidthAngstrom,
            double apertureAreaCm2, double quantumEfficiency,
            double exposureSeconds, double extinctionTransmission)
        {
            if (double.IsPositiveInfinity(apparentMagnitude) || double.IsNaN(apparentMagnitude)) return 0.0;

            double photonFluxPerCm2PerSecond = ZeroMagPhotonFluxPerAngstrom
                * Math.Pow(10.0, -0.4 * apparentMagnitude)
                * Math.Max(0.0, filterBandwidthAngstrom);

            return photonFluxPerCm2PerSecond
                * Math.Max(0.0, apertureAreaCm2)
                * Math.Max(0.0, quantumEfficiency)
                * Math.Max(0.0, exposureSeconds)
                * Math.Max(0.0, extinctionTransmission);
        }
    }
}
