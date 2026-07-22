using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Physics for the RC20 camera pipeline: atmospheric extinction (Bouguer's law)
    /// and CCD sensor noise (shot noise, dark current). Pure C#, no Unity dependency.
    /// Cloud cover is handled separately by EveCloudIntegration — no procedural fallback.
    /// </summary>
    public static class AtmosphericImagingNoise
    {
        // Broadband extinction at a decent mid-altitude site — 0.20 mag/airmass is a
        // representative average. Separate from ImagingObservingConditions (which only
        // gates whether imaging is allowed, not how bright the result looks).
        public const double ExtinctionMagPerAirmass = 0.20;

        /// <summary>Fraction of flux transmitted at the given airmass (1 at zenith, falling toward horizon).</summary>
        public static double ExtinctionTransmission(double airmass)
        {
            if (double.IsNaN(airmass)) return 0.0;
            if (airmass < 1.0) return 1.0;
            if (double.IsPositiveInfinity(airmass)) return 0.0;
            return Math.Pow(10.0, -0.4 * ExtinctionMagPerAirmass * (airmass - 1.0));
        }

        /// <summary>
        /// Scintillation sigma above the zenith value, for a given aperture/airmass/exposure.
        /// Lets the camera reuse the same Young formula as the transit photometers
        /// without going through the Transit-only API.
        /// </summary>
        public static double ScintillationExcessSigma(double apertureMeters, double siteAltitudeMeters, double airmass, double exposureSeconds)
        {
            if (double.IsNaN(airmass) || double.IsInfinity(airmass) || airmass <= 1.0) return 0.0;
            double atZenith = AtmosphericNoise.YoungSigmaRaw(apertureMeters, siteAltitudeMeters, 1.0, exposureSeconds);
            double atAirmass = AtmosphericNoise.YoungSigmaRaw(apertureMeters, siteAltitudeMeters, airmass, exposureSeconds);
            return Math.Sqrt(Math.Max(0.0, atAirmass * atAirmass - atZenith * atZenith));
        }

        // Sensor noise: Poisson shot noise + dark current, in the same abstract [0,1]
        // signal units the RC20 pipeline uses. The *shape* is real (sqrt(signal),
        // shadows noisier than highlights) even if the units aren't photon counts.
        private const double ShotNoiseCoefficient = 0.55;
        private const double DarkCurrentRatePerSecond = 0.01; // abstract units/sec -- negligible on short subs, real on long ones
        private const double DarkNoiseCoefficient = 0.55;

        /// <summary>1-sigma shot noise for a pixel at the given (pre-gain) signal fraction.</summary>
        public static double ShotNoiseSigma(double signalFraction) => ShotNoiseCoefficient * Math.Sqrt(Math.Max(0.0, signalFraction));

        /// <summary>Dark-current pedestal + noise for the given exposure. Pre-gain — accumulates regardless of gain setting.</summary>
        public static void DarkCurrent(double exposureSeconds, out double pedestalFraction, out double sigmaFraction)
        {
            double darkUnits = DarkCurrentRatePerSecond * Math.Max(0.0, exposureSeconds);
            pedestalFraction = darkUnits;
            sigmaFraction = DarkNoiseCoefficient * Math.Sqrt(darkUnits);
        }
    }
}
