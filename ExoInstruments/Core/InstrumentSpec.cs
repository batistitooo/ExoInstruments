using System;

namespace ExoInstruments.Core
{
    public enum DetectionMethod
    {
        Transit,
        RadialVelocity,
        DirectImaging,

        /// <summary>
        /// Not an exoplanet-detection method at all: a ground telescope pointed
        /// at a solar-system body for its own sake (see SolarSystemCameraTexture).
        /// Every InstrumentSpec field the star-catalog session types read
        /// (ReferencePrecision, CadenceSeconds, etc.) is meaningless here --
        /// only the unlock economy fields (UnlockCostFunds, ScanCostFunds, ...)
        /// and the presentation fields apply.
        /// </summary>
        SolarSystemPhotography
    }

    /// <summary>
    /// Real-instrument specs driving simulated precision and cadence for a given
    /// observatory. Measurement noise is photon-noise-limited: sigma scales as
    /// 10^(exponent*(mag-refMag)), the same relation regardless of instrument
    /// (aperture/optics/detector differences live in ReferencePrecision, not the
    /// exponent) -- fainter star, fewer photons, same magnitude-flux relation.
    /// </summary>
    public class InstrumentSpec
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public DetectionMethod Method { get; set; }
        public double ReferenceMagnitude { get; set; }
        public double ReferencePrecision { get; set; }  // ppm for Transit, m/s for RadialVelocity
        public double PrecisionExponent { get; set; }
        public double CadenceSeconds { get; set; }       // exposure interval (Transit) or epoch spacing (RV)
        public string Citation { get; set; }

        /// <summary>
        /// Short plain-language presentation of the real instrument, shown in
        /// the observatory selector when the player picks it: what the device
        /// physically is, what it measures, and what it is good at. Written for
        /// a player who has never heard of it.
        /// </summary>
        public string Description { get; set; }

        // --- Site & platform: ground-based observing reality ----------------

        /// <summary>
        /// True for satellite observatories: no day/night cycle, no target
        /// altitude limit, no airmass, no scintillation. Ground-based
        /// instruments only collect data when their session's observing
        /// conditions allow it (see ImagingObservingConditions).
        /// </summary>
        public bool IsSpaceBased { get; set; }

        /// <summary>
        /// Effective aperture diameter (m), feeding the Young scintillation
        /// relation for ground-based photometers (see AtmosphericNoise).
        /// 0 = term neglected.
        /// </summary>
        public double ApertureMeters { get; set; }

        /// <summary>Site altitude above sea level (m), for the scintillation exp(-h/8000) factor.</summary>
        public double SiteAltitudeMeters { get; set; }

        /// <summary>
        /// The visual telescope this instrument drives in SolarSystemCameraTexture, when Method
        /// == SolarSystemPhotography (see VisualTelescopeCatalog). Selecting this row in the
        /// Observatory dropdown calls SolarSystemCameraTexture.SetActiveTelescope(VisualTelescope)
        /// so the rendering/noise pipeline matches whichever instrument is chosen. Null for every
        /// exoplanet-detection method, where it has no meaning.
        /// </summary>
        public VisualTelescopeSpec VisualTelescope { get; set; }

        // --- Career progression: unlock economy -----------------------------
        // PLACEHOLDER values (see Observatories.cs) -- balance à valider avec
        // Baptiste. Sandbox/science-sandbox games ignore all of this, same gate
        // as the fog-of-war reveal (ExoInstrumentsGUI.CareerFogActive).

        /// <summary>Available from the start of any career game, no purchase needed -- the player's first, cheapest instrument.</summary>
        public bool UnlockedByDefault { get; set; }

        /// <summary>One-time Funds cost to unlock this instrument in career mode.</summary>
        public double UnlockCostFunds { get; set; }

        /// <summary>Cumulative Science this mod has awarded (see ExoInstrumentsScenario.TotalScienceEarned) needed before the instrument is purchasable — a track record gate, not an immediate affordability check.</summary>
        public double UnlockScienceThreshold { get; set; }

        /// <summary>Funds charged per observation in career mode — keeps scans from being free spam. Scales with instrument class.</summary>
        public double ScanCostFunds { get; set; }

        /// <summary>Multiplier on the detection Science award. Set explicitly alongside unlock cost — NOT derived from precision, which would perversely make the free starter (SPECULOOS) pay more than TESS.</summary>
        public double ScienceRewardMultiplier { get; set; } = 1.0;

        /// <summary>
        /// The real optics and detector this instrument photographs with, when they have been
        /// sourced. Present ONLY for transit photometers, and only where every field could be
        /// looked up: with it, LightCurveSimulator computes each exposure's noise from the CCD
        /// equation (see TransitPhotometry); without it, EstimatePrecision below is used unchanged.
        ///
        /// Null is the normal state for an instrument nobody has entered the hardware for, and for
        /// every radial-velocity and direct-imaging instrument, whose precision is not an
        /// aperture-photometry S/N at all and would need its own model (Bouchy et al. 2001 for the
        /// RV photon-noise limit; a contrast curve for imaging).
        /// </summary>
        public PhotometricDetector Detector { get; set; }

        /// <summary>
        /// 1-sigma measurement precision at the given apparent magnitude, in this instrument's
        /// native unit, from the fitted scaling sigma = sigma_ref * 10^(k (m - m_ref)).
        ///
        /// This is the EMPIRICAL path, kept as the fallback for every instrument with no sourced
        /// Detector. Its limits are stated in CcdEquation's summary: with k = 0.2 it is the pure
        /// source-shot-noise limit, so it carries no sky, no read noise and no dependence on the
        /// hardware, and it cannot bend toward the sky-limited slope at the faint end.
        /// </summary>
        public double EstimatePrecision(double apparentMagnitude)
        {
            return ReferencePrecision * Math.Pow(10.0, PrecisionExponent * (apparentMagnitude - ReferenceMagnitude));
        }
    }
}
