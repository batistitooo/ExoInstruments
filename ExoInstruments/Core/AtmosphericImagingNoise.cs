using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Physics for the solar-system camera pipeline: atmospheric extinction (Bouguer's law)
    /// and CCD sensor noise (shot noise, dark current). Pure C#, no Unity dependency.
    /// Sensor numbers (full well, dark current, read noise) are NOT hardcoded here — callers
    /// pass in the active telescope's own VisualTelescopeSpec values, so this stays correct
    /// for whichever camera VisualTelescopeCatalog says is attached.
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
        /// Effective height of the dominant turbulent layer, used to project a source's
        /// angular size into a linear size at that layer (see ScintillationExcessSigma).
        /// Same order of magnitude as the pressure scale height above; both trace to
        /// where the bulk of the atmosphere (and its turbulence) actually sits.
        /// </summary>
        private const double TurbulenceLayerHeightMeters = 8000.0;

        /// <summary>
        /// Scintillation sigma above the zenith value, for a given aperture/airmass/exposure.
        /// Lets the camera reuse the same Young formula as the transit photometers
        /// without going through the Transit-only API.
        ///
        /// angularDiameterRad is the imaged body's own apparent angular size (0 for a
        /// point source, e.g. a star). Young's formula alone models a point source: a
        /// resolved planetary disk spans many independent turbulent cells at once, and
        /// their intensity fluctuations average out across the disk the same way a
        /// larger telescope aperture averages them out over its own area (extended-source
        /// scintillation suppression, Dravins, Lindegren, Mezey &amp; Young 1997, "Atmospheric
        /// Intensity Scintillation of Stars I", PASP 109, 173). Modeled here by projecting
        /// the source's angular size to a linear size at the turbulent layer's height and
        /// combining it with the real aperture (root-sum-square, i.e. as an equivalent
        /// larger averaging aperture) before applying Young's formula -- so a resolved
        /// planet scintillates far less than a point star through the same telescope,
        /// while angularDiameterRad=0 leaves star photometry exactly as before.
        /// </summary>
        public static double ScintillationExcessSigma(double apertureMeters, double siteAltitudeMeters, double airmass, double exposureSeconds, double angularDiameterRad = 0.0)
        {
            if (double.IsNaN(airmass) || double.IsInfinity(airmass) || airmass <= 1.0) return 0.0;
            double sourceSizeMeters = Math.Max(0.0, angularDiameterRad) * TurbulenceLayerHeightMeters;
            double effectiveApertureMeters = Math.Sqrt(apertureMeters * apertureMeters + sourceSizeMeters * sourceSizeMeters);
            double atZenith = AtmosphericNoise.YoungSigmaRaw(effectiveApertureMeters, siteAltitudeMeters, 1.0, exposureSeconds);
            double atAirmass = AtmosphericNoise.YoungSigmaRaw(effectiveApertureMeters, siteAltitudeMeters, airmass, exposureSeconds);
            return Math.Sqrt(Math.Max(0.0, atAirmass * atAirmass - atZenith * atZenith));
        }

        // Sensor noise: Poisson shot noise + dark current. The abstract [0,1] "signal
        // fraction" this pipeline uses is defined as a fraction of the active telescope's real
        // sensor full well (VisualTelescopeSpec.FullWellElectrons). With that anchor, shot
        // noise and dark-current shot noise both reduce to pure Poisson statistics -- sigma
        // (electrons) = sqrt(N), so as a fraction of full well: sigma_fraction =
        // sqrt(signalFraction * Fw) / Fw = sqrt(signalFraction / Fw). No separate tuned
        // coefficient is needed once a real Fw is chosen; the same 1/sqrt(Fw) relation applies
        // to both shot noise and dark-current shot noise, since both are the same physical
        // process (Poisson-distributed electron counts).

        /// <summary>1-sigma shot noise for a pixel at the given (pre-gain) signal fraction, for a sensor with the given real full well (electrons).</summary>
        public static double ShotNoiseSigma(double signalFraction, double fullWellElectrons)
            => Math.Sqrt(Math.Max(0.0, signalFraction) / Math.Max(1.0, fullWellElectrons));

        /// <summary>Dark-current pedestal + noise for the given exposure, on a sensor with the given real full well and dark current rate (both electrons). Pre-gain — accumulates regardless of gain setting.</summary>
        public static void DarkCurrent(double exposureSeconds, double fullWellElectrons, double darkCurrentElectronsPerSecond, out double pedestalFraction, out double sigmaFraction)
        {
            double darkUnits = (darkCurrentElectronsPerSecond / Math.Max(1.0, fullWellElectrons)) * Math.Max(0.0, exposureSeconds);
            pedestalFraction = darkUnits;
            sigmaFraction = Math.Sqrt(darkUnits / Math.Max(1.0, fullWellElectrons)); // same Poisson process, same full-well-derived relation
        }
    }
}
