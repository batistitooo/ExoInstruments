namespace ExoInstruments.Core
{
    /// <summary>
    /// Science paid by the survey loop. Shape is fixed: any scan pays a little, a real detection
    /// pays more, nothing pays twice.
    ///
    /// WHAT THESE NUMBERS ARE MEASURED AGAINST. The stock tech tree costs about 17 500 Science in
    /// total (sum of all 57 nodes). A career that runs a serious survey should be able to earn a
    /// meaningful fraction of that and not the whole thing, so the roster's own gates are set
    /// where twenty confirmed detections on a mid-tier spectrograph (20 x 40 x 2.5 = 2000) sit
    /// near a tenth of the tree. The instrument acquisition thresholds in Observatories.cs top
    /// out at 500, which is well inside what a working programme earns.
    ///
    /// THE ONE THING THAT HAD TO BE CAPPED. There are 12 011 clickable targets once the decoy
    /// catalogue is blended in, and a flat first-scan award would put 60 000 Science on the table
    /// for pointing the free instrument at every one of them, three times the whole tech tree,
    /// with no detection ever required. ScienceRewardScanHalvingCount is the answer, and it is
    /// also the true statement: a survey's first null result is news and its thousandth is not.
    /// </summary>
    public static class ScienceRewards
    {
        /// <summary>
        /// First completed scan of a star, any outcome, before the diminishing-returns divisor
        /// below. Decoys earn this too: ruling a star out is real survey work.
        /// </summary>
        public const float ScienceRewardFirstScan = 5.0f;

        /// <summary>
        /// Scans after which the first-scan award is worth half of ScienceRewardFirstScan, then
        /// half again at three times this count, and so on: award = base / (1 + n / this).
        ///
        /// The total is therefore logarithmic in the number of stars surveyed rather than linear,
        /// which bounds it: 12 011 targets pay about 1 370 Science in total instead of 60 000,
        /// and the first fifty scans still pay near full rate, so the opening of a career is
        /// untouched. Detections are NOT damped, because they are one-time per host, truth-gated
        /// against the catalogue, and there are only 3 077 real hosts to find.
        /// </summary>
        public const int ScienceRewardScanHalvingCount = 50;

        /// <summary>One-time bonus per host for a confirmed planet detection, on top of ScienceRewardFirstScan. Scaled by the instrument's ScienceRewardMultiplier; this is the baseline-instrument value.</summary>
        public const float ScienceRewardRealDetection = 40.0f;

        /// <summary>
        /// One-time award for imaging a star with a measurable temperature: real astrophysics
        /// even when no planet shows up. Flat, not multiplied by instrument tier.
        ///
        /// CURRENTLY UNREACHABLE, and that is a consequence rather than a decision about this
        /// number. It is claimed only from the direct-imaging analysis, whose one instrument is
        /// the ELT, and the ELT is UnderConstruction until its contrast model is finished. The
        /// value is left as it was so that turning the ELT back on restores the old behaviour
        /// exactly; do not read it as live balance in the meantime.
        /// </summary>
        public const float ScienceRewardStellarCharacterization = 10.0f;

        /// <summary>One-time award for a confirmed TTV signal: gravity evidence of an unseen body, truth-gated same as the detection bonus. Flat, since the discovery is in the timing, not the aperture.</summary>
        public const float ScienceRewardTtvDetection = 25.0f;

        /// <summary>One-time award for a Rossiter-McLaughlin spin-orbit measurement: the cross-method payoff requiring both a transit ephemeris and a spectrograph on target during a transit.</summary>
        public const float ScienceRewardRossiterMcLaughlin = 30.0f;

        /// <summary>Extra multiplier on the per-planet award when multiple real planets are confirmed in one campaign. Additive, not compounded with the instrument multiplier again: 3 planets gives (1 + 2 x 0.5) x base, not base cubed.</summary>
        public const double JackpotMultiplierPerExtraPlanet = 0.5;

        /// <summary>One-time award for spotting a supernova in a frame, priced beside the Rossiter-McLaughlin measurement: one discovery, one payout, no aperture scaling.</summary>
        public const float ScienceRewardSupernovaDiscovery = 30.0f;

        /// <summary>
        /// The first-scan award once <paramref name="scansAlreadyCompleted"/> stars have been
        /// surveyed. Kept here rather than in the GUI so the curve is stated once, next to the
        /// constant that shapes it.
        /// </summary>
        public static float FirstScanAward(int scansAlreadyCompleted)
        {
            if (scansAlreadyCompleted <= 0) return ScienceRewardFirstScan;
            return ScienceRewardFirstScan / (1.0f + (float)scansAlreadyCompleted / ScienceRewardScanHalvingCount);
        }
    }
}
