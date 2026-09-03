using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>Result of a periodogram search over radial-velocity measurements.</summary>
    public class RvDetectionResult
    {
        public bool Detected { get; set; }
        public bool InsufficientData { get; set; }
        public double BestPeriodDays { get; set; }
        public double BestSemiAmplitudeMps { get; set; }
        public double BestPhase01 { get; set; }
        public double Snr { get; set; }
        public int SampleCount { get; set; }

        // Presentation stats for the scan report, not a pass/fail conclusion.
        public double BaselineDays { get; set; }
        public double RvPrecisionMps { get; set; }              // empirical std of the full RV series
        public double SemiAmplitudeUncertaintyMps { get; set; } // 1-sigma uncertainty on BestSemiAmplitudeMps

        /// <summary>
        /// Set when this period sits at a near-integer ratio of an earlier, stronger
        /// detection in the same prewhitening run. A single-sinusoid subtraction of an
        /// eccentric orbit leaves real residual power at P/2, P/3 (the higher Keplerian
        /// harmonics the fit can't absorb), which can masquerade as an inner planet.
        /// A flag for the report, not proof either way; real RV surveys face the same call.
        /// </summary>
        public double? LikelyHarmonicOfPeriodDays { get; set; }

        public static RvDetectionResult Insufficient(int sampleCount)
        {
            return new RvDetectionResult { Detected = false, InsufficientData = true, SampleCount = sampleCount };
        }
    }

    /// <summary>
    /// One pass of the iterative prewhitening search (RvDetector.DetectMultiple):
    /// the detection plus the residual series it was searched in, the original data
    /// minus every previously subtracted sinusoid, which is exactly what this
    /// stage's phase-folded plot should display.
    /// </summary>
    public class RvDetectionStage
    {
        public RvDetectionResult Result { get; set; }
        public List<RvSample> SearchedSamples { get; set; }
    }
}
