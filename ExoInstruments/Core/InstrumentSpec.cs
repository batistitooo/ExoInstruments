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

        // --- Career progression: unlock economy -----------------------------
        // PLACEHOLDER values (see Observatories.cs) -- balance à valider avec
        // Baptiste. Sandbox/science-sandbox games ignore all of this, same gate
        // as the fog-of-war reveal (ExoInstrumentsGUI.CareerFogActive).

        /// <summary>Available from the start of any career game, no purchase needed -- the player's first, cheapest instrument.</summary>
        public bool UnlockedByDefault { get; set; }

        /// <summary>One-time Funds cost to unlock this instrument in career mode.</summary>
        public double UnlockCostFunds { get; set; }

        /// <summary>
        /// Cumulative Science earned through this mod's own scans (tracked
        /// separately from the player's current, spendable R&amp;D balance -- see
        /// ExoInstrumentsScenario.TotalScienceEarned) required before the
        /// instrument becomes purchasable. Represents needing a track record of
        /// survey results before the observatory can justify a bigger telescope,
        /// not "afford it outright on day one."
        /// </summary>
        public double UnlockScienceThreshold { get; set; }

        /// <summary>
        /// Funds charged every time an observation is started with this
        /// instrument in career mode -- telescope time is the scarce commodity of
        /// real astronomy, and it's what keeps scans from being free spam.
        /// Scales with the instrument's class: flagship time costs flagship money.
        /// </summary>
        public double ScanCostFunds { get; set; }

        /// <summary>
        /// Multiplier on the detection Science award when the discovery is made
        /// with this instrument. An explicit balance number set alongside the
        /// unlock cost (bigger investment, bigger payoff per detection), NOT
        /// derived from ReferencePrecision: per-point precision inverts the
        /// intended incentive within the transit class (SPECULOOS, the free
        /// starter, is the most precise transit instrument per point -- a
        /// precision-derived multiplier would pay it 7x more than TESS).
        /// </summary>
        public double ScienceRewardMultiplier { get; set; } = 1.0;

        /// <summary>1-sigma measurement precision at the given apparent magnitude, in this instrument's native unit.</summary>
        public double EstimatePrecision(double apparentMagnitude)
        {
            return ReferencePrecision * Math.Pow(10.0, PrecisionExponent * (apparentMagnitude - ReferenceMagnitude));
        }
    }
}
