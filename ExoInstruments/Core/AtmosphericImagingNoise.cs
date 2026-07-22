using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Pure-C# physics for the RC20 amateur astrograph's image-quality
    /// pipeline: atmospheric extinction and CCD sensor noise (photon shot
    /// noise, dark current). Kept separate from SolarSystemCameraTexture
    /// (which owns the Unity-side pixel buffer and this mod's game-specific
    /// brightness scale) so the underlying physics is self-contained and
    /// testable the same way as the rest of Core.
    ///
    /// Cloud cover is NOT modeled here at all -- stock KSP has no weather
    /// system of its own, so real cloud data (when available) comes only
    /// from an installed EVE cloud config, read directly off its own painted
    /// textures by EveCloudIntegration (Visualization). No procedural
    /// stand-in is used when EVE isn't installed/configured: the frame
    /// simply has no cloud effect, rather than a fabricated one.
    /// </summary>
    public static class AtmosphericImagingNoise
    {
        // --- Atmospheric extinction (Bouguer's law) --------------------------
        // Typical broadband atmospheric extinction coefficient at a decent
        // mid-altitude site (Sterken & Manfroid, "Astronomical Photometry",
        // typical range ~0.1-0.3 mag/airmass depending on site/wavelength/
        // aerosol load) -- 0.20 is a representative average. This is a
        // genuine dimming of the target itself, separate from
        // ImagingObservingConditions (which only gates whether an
        // observation is *allowed*, never how bright the result looks).
        public const double ExtinctionMagPerAirmass = 0.20;

        /// <summary>Fraction of zenith-equivalent flux transmitted at the given airmass: 1 at airmass 1, falling toward the horizon. 0/1-safe for non-finite or sub-1 airmass.</summary>
        public static double ExtinctionTransmission(double airmass)
        {
            if (double.IsNaN(airmass)) return 0.0;
            if (airmass < 1.0) return 1.0;
            if (double.IsPositiveInfinity(airmass)) return 0.0;
            return Math.Pow(10.0, -0.4 * ExtinctionMagPerAirmass * (airmass - 1.0));
        }

        // --- Scintillation reuse ---------------------------------------------
        /// <summary>
        /// Excess (above-zenith) Young (1967) scintillation sigma for a given
        /// aperture/site/airmass/exposure, independent of any InstrumentSpec --
        /// lets non-photometric ground-based imaging (this camera) reuse the
        /// exact real formula AtmosphericNoise already uses for the transit
        /// photometers, without going through that Transit-only-gated API.
        /// </summary>
        public static double ScintillationExcessSigma(double apertureMeters, double siteAltitudeMeters, double airmass, double exposureSeconds)
        {
            if (double.IsNaN(airmass) || double.IsInfinity(airmass) || airmass <= 1.0) return 0.0;
            double atZenith = AtmosphericNoise.YoungSigmaRaw(apertureMeters, siteAltitudeMeters, 1.0, exposureSeconds);
            double atAirmass = AtmosphericNoise.YoungSigmaRaw(apertureMeters, siteAltitudeMeters, airmass, exposureSeconds);
            return Math.Sqrt(Math.Max(0.0, atAirmass * atAirmass - atZenith * atZenith));
        }

        // --- Sensor noise physics ---------------------------------------------
        // A real amateur CCD/CMOS's noise budget, given real signal-dependent
        // (Poisson) shape and a genuine constant read-noise floor, expressed
        // in the SAME abstract [0,1]-ish signal units the rest of the RC20
        // pipeline already uses (the underlying scene render isn't
        // radiometrically calibrated to real photon counts, so claiming a
        // literal electron-count calibration here would be dishonest --
        // what's physically real and worth getting right is the *shape*:
        // shot/dark noise growing as sqrt(signal)/sqrt(exposure), i.e.
        // proportionally noisier in the shadows and cleaner in the
        // highlights, exactly like a real sensor).
        private const double ShotNoiseCoefficient = 0.55;
        private const double DarkCurrentRatePerSecond = 0.01; // abstract units/sec -- negligible on short subs, real on long ones
        private const double DarkNoiseCoefficient = 0.55;

        /// <summary>1-sigma photon shot-noise amplitude for a pixel carrying the given (pre-gain) signal fraction. sqrt(signal) shape -- the real Poisson-noise behavior.</summary>
        public static double ShotNoiseSigma(double signalFraction) => ShotNoiseCoefficient * Math.Sqrt(Math.Max(0.0, signalFraction));

        /// <summary>Dark-current pedestal + its own shot noise, both pre-gain, for the given exposure time -- accumulates independent of ISO/gain (real thermal electrons build up in the well regardless of amplifier setting).</summary>
        public static void DarkCurrent(double exposureSeconds, out double pedestalFraction, out double sigmaFraction)
        {
            double darkUnits = DarkCurrentRatePerSecond * Math.Max(0.0, exposureSeconds);
            pedestalFraction = darkUnits;
            sigmaFraction = DarkNoiseCoefficient * Math.Sqrt(darkUnits);
        }
    }
}
