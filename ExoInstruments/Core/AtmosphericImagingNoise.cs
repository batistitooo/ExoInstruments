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

        // Sensor noise: Poisson shot noise + dark current. The abstract [0,1] "signal
        // fraction" this pipeline uses is defined as a fraction of a real sensor's full
        // well, anchored to the ZWO ASI294MM Pro (a real, commercially available cooled
        // monochrome astronomy camera; ZWO official datasheet, zwoastro.com/product/asi294):
        // full well 66,000 e-, read noise 1.2 e- (best case), dark current 0.0022 e-/s/pixel
        // at -20C. With that anchor, shot noise and dark-current shot noise both reduce to
        // pure Poisson statistics -- sigma (electrons) = sqrt(N), so as a fraction of full
        // well: sigma_fraction = sqrt(signalFraction * Fw) / Fw = sqrt(signalFraction) /
        // sqrt(Fw). No separate tuned coefficient is needed once a real Fw is chosen; the
        // same 1/sqrt(Fw) constant applies to both shot noise and dark-current shot noise,
        // since both are the same physical process (Poisson-distributed electron counts).
        public const double SensorFullWellElectrons = 66000.0;
        private static readonly double PoissonNoiseCoefficient = 1.0 / Math.Sqrt(SensorFullWellElectrons);
        private const double DarkCurrentRatePerSecond = 0.0022 / SensorFullWellElectrons; // real e-/s/pixel at -20C, as a full-well fraction
        private const double ReadNoiseElectrons = 1.2; // real ZWO ASI294MM Pro spec, best case
        public static readonly double ReadNoiseFraction = ReadNoiseElectrons / SensorFullWellElectrons;

        /// <summary>1-sigma shot noise for a pixel at the given (pre-gain) signal fraction.</summary>
        public static double ShotNoiseSigma(double signalFraction) => PoissonNoiseCoefficient * Math.Sqrt(Math.Max(0.0, signalFraction));

        /// <summary>Dark-current pedestal + noise for the given exposure. Pre-gain — accumulates regardless of gain setting.</summary>
        public static void DarkCurrent(double exposureSeconds, out double pedestalFraction, out double sigmaFraction)
        {
            double darkUnits = DarkCurrentRatePerSecond * Math.Max(0.0, exposureSeconds);
            pedestalFraction = darkUnits;
            sigmaFraction = PoissonNoiseCoefficient * Math.Sqrt(darkUnits); // same Poisson process, same full-well-derived coefficient
        }
    }
}
