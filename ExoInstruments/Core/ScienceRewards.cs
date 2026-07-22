namespace ExoInstruments.Core
{
    /// <summary>
    /// Career-mode Science values for the survey loop. ALL PLACEHOLDERS — balance à valider avec Baptiste.
    /// Shape is fixed: any scan pays a little, a real detection pays more, nothing pays twice.
    /// </summary>
    public static class ScienceRewards
    {
        /// <summary>PLACEHOLDER. First completed scan of a star, any outcome. Decoys earn this too — ruling a star out is real survey work.</summary>
        public const float ScienceRewardFirstScan = 5.0f;

        /// <summary>PLACEHOLDER. One-time bonus per host for a confirmed planet detection, on top of ScienceRewardFirstScan. Scaled by the instrument's ScienceMultiplier — this is the baseline-instrument value.</summary>
        public const float ScienceRewardRealDetection = 40.0f;

        /// <summary>PLACEHOLDER. One-time award for imaging a star with a measurable temperature — real astrophysics even when no planet shows up. Flat (not multiplied by instrument tier).</summary>
        public const float ScienceRewardStellarCharacterization = 10.0f;

        /// <summary>PLACEHOLDER. One-time award for a confirmed TTV signal — gravity evidence of an unseen body, truth-gated same as the detection bonus. Flat (the discovery is in the timing, not the aperture).</summary>
        public const float ScienceRewardTtvDetection = 25.0f;

        /// <summary>PLACEHOLDER. One-time award for a Rossiter-McLaughlin spin-orbit measurement — the cross-method payoff requiring both a transit ephemeris and a spectrograph on target during a transit.</summary>
        public const float ScienceRewardRossiterMcLaughlin = 30.0f;

        /// <summary>PLACEHOLDER. Extra multiplier on the per-planet award when multiple real planets are confirmed in one campaign. Additive, not compounded with the instrument multiplier again — 3 planets gives (1 + 2×0.5)×base, not base³.</summary>
        public const double JackpotMultiplierPerExtraPlanet = 0.5;
    }
}
