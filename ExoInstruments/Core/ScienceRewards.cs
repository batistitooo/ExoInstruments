namespace ExoInstruments.Core
{
    /// <summary>
    /// Career-mode Science values for the fog-of-war survey loop. Pure constants,
    /// kept in Core so the balance numbers are greppable in one place.
    ///
    /// ALL VALUES ARE PLACEHOLDERS -- balance à valider avec Baptiste. The only
    /// design commitment baked in here is the *shape*: identifying a star (any
    /// outcome, decoy included) pays a little, confirming a real catalog planet
    /// pays a lot more, and nothing pays twice. A possible refinement once real
    /// numbers are discussed: scale the detection bonus by instrument tier or by
    /// target difficulty (magnitude, required baseline/integration).
    /// </summary>
    public static class ScienceRewards
    {
        /// <summary>
        /// PLACEHOLDER -- balance à valider avec Baptiste.
        /// First completed scan (observation + analysis) of a star, whatever the
        /// outcome. A null result on a decoy earns this too: ruling a star out is
        /// real survey work, and it's what un-fogs the sky map. This is the *only*
        /// Science a decoy can ever yield.
        /// </summary>
        public const float ScienceRewardFirstScan = 5.0f;

        /// <summary>
        /// PLACEHOLDER -- balance à valider avec Baptiste.
        /// One-time bonus per host star for a confirmed detection of a real
        /// catalog planet (transit dip found, RV signal above threshold, or
        /// imaging SNR over 5), on top of ScienceRewardFirstScan. Deliberately
        /// several times the decoy value: real detections are rare and demand
        /// long baselines. Scaled by the observing instrument's ScienceMultiplier
        /// (see InstrumentSpec) before being awarded -- this base value is what a
        /// baseline instrument in its DetectionMethod earns.
        /// </summary>
        public const float ScienceRewardRealDetection = 40.0f;

        /// <summary>
        /// PLACEHOLDER -- balance à valider avec Baptiste.
        /// One-time award per star for a stellar characterization: completing a
        /// direct-imaging campaign on a star whose effective temperature is
        /// measurable (catalog star_teff, or derived from its BSC B-V color).
        /// This is what makes pointing the ELT at a star *worth something in
        /// itself* -- a resolved AO image plus color temperature is real
        /// astrophysics even when no companion turns up. Deliberately flat (not
        /// scaled by instrument multiplier): there's only one imaging instrument
        /// today, and characterization shouldn't rival an actual detection.
        /// </summary>
        public const float ScienceRewardStellarCharacterization = 10.0f;

        /// <summary>
        /// PLACEHOLDER -- balance à valider avec Baptiste.
        /// One-time award per host star for a confirmed transit-timing-variation
        /// detection: a sinusoidal O-C signal in the measured transit mid-times,
        /// on a system where the catalog really does carry a perturbing
        /// companion (same truth-gating as the detection bonus -- an O-C
        /// wobble fit to pure noise can't be farmed). Pure-gravity evidence of
        /// an unseen body, distinct from photometrically detecting the planet
        /// itself, hence its own award. Flat: the discovery is in the timing
        /// analysis, not the collecting instrument's aperture.
        /// </summary>
        public const float ScienceRewardTtvDetection = 25.0f;

        /// <summary>
        /// PLACEHOLDER -- balance à valider avec Baptiste.
        /// One-time award per host star for a Rossiter-McLaughlin measurement:
        /// resolving the in-transit RV anomaly and extracting the sky-projected
        /// spin-orbit angle. Deliberately the cross-method payoff -- it requires
        /// a transit ephemeris (photometry) AND a spectrograph on target during
        /// a transit window, the first mechanic where both pipelines observe the
        /// same event together.
        /// </summary>
        public const float ScienceRewardRossiterMcLaughlin = 30.0f;

        /// <summary>
        /// PLACEHOLDER -- balance à valider avec Baptiste.
        /// Extra multiplier applied to the *whole* per-planet detection award
        /// when more than one real catalog planet is confirmed in the same
        /// observing campaign (currently only reachable via RV: one campaign's
        /// reflex-velocity signal carries every companion's orbit at once, see
        /// RvSimulator.GenerateSystemVelocityAtTime) -- e.g. 3 confirmed planets
        /// applies (1 + 2*0.5) = 2x on top of the per-planet award those 3 planets
        /// already earned individually. Deliberately additive, not compounded
        /// with the instrument's ScienceMultiplier a second time (that award is
        /// already per-planet-times-instrument-quality) -- untangling a
        /// multi-planet system with a top-tier spectrograph is still the biggest
        /// single payout in the game, just without squaring the precision bonus.
        /// </summary>
        public const double JackpotMultiplierPerExtraPlanet = 0.5;
    }
}
