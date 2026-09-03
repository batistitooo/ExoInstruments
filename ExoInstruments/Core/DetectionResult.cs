namespace ExoInstruments.Core
{
    /// <summary>Result of a transit search over a light curve.</summary>
    public class DetectionResult
    {
        public bool Detected { get; set; }
        public bool InsufficientData { get; set; }
        public double BestPeriodDays { get; set; }
        public double BestDepthPpm { get; set; }
        public double BestPhase01 { get; set; }
        public double BestDurationHours { get; set; }
        public double Snr { get; set; }
        public int SampleCount { get; set; }

        // Presentation stats for the scan report, not a pass/fail conclusion.
        public int InTransitPointCount { get; set; }
        public double BaselineDays { get; set; }
        public double PhotometricPrecisionPpm { get; set; }   // empirical std of the full light curve
        public double DepthUncertaintyPpm { get; set; }       // 1-sigma uncertainty on BestDepthPpm

        public static DetectionResult Insufficient(int sampleCount)
        {
            return new DetectionResult { Detected = false, InsufficientData = true, SampleCount = sampleCount };
        }
    }

    /// <summary>
    /// One pass of the iterative multi-planet transit search
    /// (TransitDetector.DetectMultiple): the detection plus the masked series it
    /// was searched in, the original data with every previously found planet's
    /// in-transit points removed, which is exactly what this stage's
    /// phase-folded plot should display. Mirrors RvDetectionStage.
    /// </summary>
    public class TransitDetectionStage
    {
        public DetectionResult Result { get; set; }
        public System.Collections.Generic.List<FluxSample> SearchedSamples { get; set; }
    }
}