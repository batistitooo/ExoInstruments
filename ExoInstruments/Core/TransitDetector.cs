using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Simplified BLS transit search (Kovacs et al. 2002). No false-alarm calibration
    /// or ingress/egress shape — treat SNR as relative confidence. Phase bins are
    /// adaptive: tied to the data cadence so the bin width stays physically meaningful
    /// across the full range of trial periods.
    /// </summary>
    public static class TransitDetector
    {
        public const double DefaultSnrThreshold = 8.0;
        public const int MinSampleCount = 20;

        /// <summary>
        /// A candidate box must contain at least this many points. With
        /// thousands of trial (period, phase, width) combinations, a box
        /// holding a handful of samples will eventually catch a low-flux noise
        /// cluster and clear any SNR threshold -- the small-number false alarms
        /// real BLS searches suppress the same way.
        /// </summary>
        public const int MinInTransitPoints = 12;

        /// <summary>
        /// Longest trial box as a fraction of the period. Real transit duties
        /// top out near 0.1-0.15 (a/R_star >= ~2); wider boxes stop being
        /// transit-shaped and start absorbing slow trends and window structure.
        /// </summary>
        private const double MaxTransitDuty = 0.15;

        /// <summary>
        /// Period-dependent duty ceiling, duty_max = C * P_days^(-2/3): a
        /// transit's duration scales as P^(1/3) while the period grows as P, so
        /// duty shrinks as P^(-2/3) (Kepler's third law through T14 ~ P/(pi*a/R*);
        /// the same q ~ P^(-2/3) envelope BLS implementations use). C = 0.23 is
        /// ~3x the solar-density value -- room for giants and grazing chords,
        /// but a 54-hour "transit" on a 15-day period (exactly what a starspot
        /// rotation signal tries to book itself as) stays rejected.
        /// </summary>
        private const double DutyEnvelopeCoeff = 0.23;

        private const int MinPhaseBins = 50;
        // 400 -> 320: the box-slide cost scales roughly with bins^2 (bins x
        // maxWidth, and maxWidth itself scales with bins), so this alone cuts
        // it to ~(320/400)^2 = 64% -- on a long baseline with thousands of
        // samples this search was legitimately taking minutes with nothing on
        // screen but a static "Analyzing..." label (see the elapsed-time
        // readout added where this button is drawn). Still well above
        // MinPhaseBins, so short-period, short-duration transits keep plenty
        // of phase resolution.
        private const int MaxPhaseBins = 320;
        private const double BinsPerCadence = 4.0;

        // Auto period grid (periodSteps = 0): uniform in frequency, not period.
        // The trial-period spacing must keep a transit's phase drift across the
        // whole baseline under a fraction of its own duration, or the folded
        // transit smears out and the peak dies -- the requirement is
        // dP < q*P^2/(OverSampling*T), which in frequency space (df = dP/P^2)
        // is a *constant* step df = q/(OverSampling*T): exactly why every real
        // BLS implementation (Kovacs 2002; astropy BLS) walks a uniform
        // frequency grid that grows finer as the baseline grows.
        private const double TypicalTransitDuty = 0.03;   // duration/period of a close-in transit
        // 3.0 -> 2.0: still comfortably above the ~1x oversampling the dP
        // requirement itself demands (2x is the conventional floor cited
        // alongside Kovacs 2002 / astropy's BLS docs); trims ~a third of the
        // period grid's cost for the safety margin it wasn't using.
        private const double FrequencyOverSampling = 2.0;
        // 4000 -> 3000: long-baseline, many-sample sessions (exactly the slow
        // case) hit this cap rather than the formula above, so it's the term
        // that actually bounds worst-case runtime; -25% here on top of the
        // phase-bin and oversampling trims roughly halves total search cost.
        private const int MaxAutoPeriodSteps = 3000;      // caps the O(steps * bins^2/4) search cost
        private const int MinAutoPeriodSteps = 200;

        public static DetectionResult Detect(
            List<FluxSample> samples,
            double minPeriodDays = 0.3,
            double maxPeriodDays = 15.0,
            int periodSteps = 0,
            int phaseBins = 0,
            double snrThreshold = DefaultSnrThreshold)
        {
            if (samples == null || samples.Count < MinSampleCount)
                return DetectionResult.Insufficient(samples?.Count ?? 0);

            double totalSum = 0.0;
            for (int i = 0; i < samples.Count; i++) totalSum += samples[i].Flux;
            double meanFlux = totalSum / samples.Count;

            double sqDiffSum = 0.0;
            for (int i = 0; i < samples.Count; i++)
            {
                double d = samples[i].Flux - meanFlux;
                sqDiffSum += d * d;
            }
            double stdFlux = Math.Sqrt(sqDiffSum / Math.Max(1, samples.Count - 1));
            if (stdFlux < 1e-12) stdFlux = 1e-12;

            double minPeriodSec = minPeriodDays * 86400.0;
            double maxPeriodSec = maxPeriodDays * 86400.0;
            double medianCadenceSeconds = ComputeMedianCadenceSeconds(samples);

            double baselineSec = samples[samples.Count - 1].Ut - samples[0].Ut;
            if (periodSteps <= 0)
            {
                double frequencyStep = TypicalTransitDuty / (FrequencyOverSampling * Math.Max(baselineSec, maxPeriodSec));
                double frequencySpan = 1.0 / minPeriodSec - 1.0 / maxPeriodSec;
                periodSteps = (int)Math.Ceiling(frequencySpan / frequencyStep);
                periodSteps = Math.Max(MinAutoPeriodSteps, Math.Min(MaxAutoPeriodSteps, periodSteps));
            }

            double bestSnr = 0.0, bestPeriodDays = 0.0, bestDepth = 0.0, bestPhase = 0.0, bestDurationDays = 0.0;
            int bestNIn = 0;

            int arraySize = phaseBins > 0 ? phaseBins : MaxPhaseBins;
            var binSum = new double[arraySize];
            var binCount = new int[arraySize];

            double minFrequency = 1.0 / maxPeriodSec;
            double maxFrequency = 1.0 / minPeriodSec;
            for (int p = 0; p < periodSteps; p++)
            {
                double frequency = maxFrequency + (minFrequency - maxFrequency) * p / Math.Max(1, periodSteps - 1);
                double periodSec = 1.0 / frequency;
                int binsForPeriod = phaseBins > 0 ? phaseBins : ComputeAdaptivePhaseBins(periodSec, medianCadenceSeconds);
                double dutyCeiling = Math.Min(MaxTransitDuty, DutyEnvelopeCoeff * Math.Pow(periodSec / 86400.0, -2.0 / 3.0));
                int maxWidth = Math.Max(1, (int)(binsForPeriod * dutyCeiling));

                Array.Clear(binSum, 0, binsForPeriod);
                Array.Clear(binCount, 0, binsForPeriod);

                for (int i = 0; i < samples.Count; i++)
                {
                    double phase = (samples[i].Ut / periodSec) % 1.0;
                    if (phase < 0) phase += 1.0;
                    int bin = (int)(phase * binsForPeriod);
                    if (bin >= binsForPeriod) bin = binsForPeriod - 1;
                    binSum[bin] += samples[i].Flux;
                    binCount[bin]++;
                }

                for (int width = 1; width <= maxWidth; width++)
                {
                    double sumIn = 0.0;
                    int nIn = 0;
                    for (int w = 0; w < width; w++)
                    {
                        sumIn += binSum[w];
                        nIn += binCount[w];
                    }

                    for (int start = 0; start < binsForPeriod; start++)
                    {
                        if (start > 0)
                        {
                            int leave = (start - 1) % binsForPeriod;
                            int enter = (start - 1 + width) % binsForPeriod;
                            sumIn -= binSum[leave];
                            nIn -= binCount[leave];
                            sumIn += binSum[enter];
                            nIn += binCount[enter];
                        }

                        if (nIn < MinInTransitPoints) continue;
                        int nOut = samples.Count - nIn;
                        if (nOut < MinInTransitPoints) continue;

                        double meanIn = sumIn / nIn;
                        double meanOut = (totalSum - sumIn) / nOut;
                        double depth = meanOut - meanIn;
                        if (depth <= 0) continue;

                        double sigma = stdFlux / Math.Sqrt(nIn);
                        double snr = depth / sigma;

                        if (snr > bestSnr)
                        {
                            bestSnr = snr;
                            bestPeriodDays = periodSec / 86400.0;
                            bestDepth = depth;
                            bestPhase = (double)start / binsForPeriod;
                            bestDurationDays = ((double)width / binsForPeriod) * bestPeriodDays;
                            bestNIn = nIn;
                        }
                    }
                }
            }

            double baselineDays = (samples[samples.Count - 1].Ut - samples[0].Ut) / 86400.0;
            double depthUncertaintyPpm = bestNIn > 0 ? (stdFlux / Math.Sqrt(bestNIn)) * 1_000_000.0 : 0.0;

            return new DetectionResult
            {
                Detected = bestSnr >= snrThreshold,
                InsufficientData = false,
                BestPeriodDays = bestPeriodDays,
                BestDepthPpm = bestDepth * 1_000_000.0,
                BestPhase01 = bestPhase,
                BestDurationHours = bestDurationDays * 24.0,
                Snr = bestSnr,
                SampleCount = samples.Count,
                InTransitPointCount = bestNIn,
                BaselineDays = baselineDays,
                PhotometricPrecisionPpm = stdFlux * 1_000_000.0,
                DepthUncertaintyPpm = depthUncertaintyPpm
            };
        }

        /// <summary>
        /// Iterative multi-planet transit search, the photometric analogue of
        /// RvDetector.DetectMultiple's prewhitening: detect the strongest box,
        /// mask out its in-transit points (with margin -- a subtraction would
        /// need the exact shape, masking only needs the ephemeris), search the
        /// remaining points for the next signal, repeat until nothing clears the
        /// threshold or maxPlanets is reached. This is how compact multi-transit
        /// systems (TRAPPIST-1 being the canonical case) are actually pulled
        /// apart from a single light curve.
        ///
        /// Every attempted stage is returned (the final, below-threshold one
        /// included), each carrying the masked series it searched -- that's the
        /// series its phase-folded plot should display.
        /// </summary>
        public const int MaxPlanetsPerSearch = 4;

        /// <summary>Masking margin around the detected box, in box durations -- covers ingress/egress wings and modest period error.</summary>
        private const double MaskMarginFactor = 0.6;

        public static List<TransitDetectionStage> DetectMultiple(
            List<FluxSample> samples,
            int maxPlanets = MaxPlanetsPerSearch,
            double minPeriodDays = 0.3,
            double maxPeriodDays = 15.0,
            double snrThreshold = DefaultSnrThreshold)
        {
            var stages = new List<TransitDetectionStage>();
            // Detrend first: starspot rotation puts real, coherent structure in
            // the light curve, and once the genuine transits are masked out the
            // residual search will happily book a spot dip as a "transit" (long
            // period, maximum duty -- the classic false positive). Every real
            // multi-planet pipeline detrends before its BLS for exactly this reason.
            var working = DetrendSamples(samples);

            for (int planetIndex = 0; planetIndex < maxPlanets; planetIndex++)
            {
                DetectionResult result = Detect(working, minPeriodDays, maxPeriodDays, snrThreshold: snrThreshold);
                stages.Add(new TransitDetectionStage { Result = result, SearchedSamples = working });

                if (result.InsufficientData || !result.Detected) break;

                double periodSec = result.BestPeriodDays * 86400.0;
                double boxWidthPhase = (result.BestDurationHours * 3600.0) / periodSec;
                // Distance-to-box-center test with phase wrapping, so a box
                // straddling phase 0/1 masks correctly on both sides.
                double maskCenter = result.BestPhase01 + boxWidthPhase / 2.0;
                double halfMask = boxWidthPhase * (0.5 + MaskMarginFactor);

                var masked = new List<FluxSample>(working.Count);
                foreach (var s in working)
                {
                    double phase = (s.Ut / periodSec) % 1.0;
                    if (phase < 0) phase += 1.0;
                    double d = phase - maskCenter;
                    d -= Math.Round(d);  // wrap to (-0.5, 0.5]
                    if (Math.Abs(d) > halfMask) masked.Add(s);
                }
                if (masked.Count == working.Count) break; // mask removed nothing: bail rather than loop on the same signal
                working = masked;
            }
            return stages;
        }

        /// <summary>
        /// Moving-median detrend: bin the series into fixed time windows, take
        /// each bin's median flux, linearly interpolate between bin centers, and
        /// divide that slow trend out. A median over half a day barely feels a
        /// few-hour transit (small duty inside the window) but tracks starspot
        /// rotation (4+ day periods, see StellarActivity) faithfully -- so the
        /// astrophysical trend comes out while the transits stay in. Public so
        /// the transit-timing analysis can run on the same cleaned series (a
        /// local spot slope across a transit window biases the measured
        /// mid-time coherently at the rotation period -- a textbook TTV false
        /// positive).
        /// </summary>
        public static List<FluxSample> DetrendSamples(List<FluxSample> samples, double binSeconds = 43200.0)
        {
            if (samples == null || samples.Count < 3) return samples;

            double t0 = samples[0].Ut;
            double span = samples[samples.Count - 1].Ut - t0;
            int binCount = Math.Max(1, (int)Math.Ceiling(span / binSeconds));
            // Fewer than 3 usable bins: the "trend" would be indistinguishable
            // from the signal itself -- leave the data alone.
            if (binCount < 3) return samples;

            var binValues = new List<double>[binCount];
            for (int i = 0; i < samples.Count; i++)
            {
                int b = (int)((samples[i].Ut - t0) / binSeconds);
                if (b >= binCount) b = binCount - 1;
                if (binValues[b] == null) binValues[b] = new List<double>();
                binValues[b].Add(samples[i].Flux);
            }

            // Median per populated bin (day/night gaps leave empty bins -- skipped).
            var binCenters = new List<double>();
            var binMedians = new List<double>();
            for (int b = 0; b < binCount; b++)
            {
                if (binValues[b] == null || binValues[b].Count == 0) continue;
                binValues[b].Sort();
                int n = binValues[b].Count;
                double median = n % 2 == 1 ? binValues[b][n / 2] : (binValues[b][n / 2 - 1] + binValues[b][n / 2]) / 2.0;
                binCenters.Add(t0 + (b + 0.5) * binSeconds);
                binMedians.Add(median);
            }
            if (binCenters.Count < 3) return samples;

            var detrended = new List<FluxSample>(samples.Count);
            int seg = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                double t = samples[i].Ut;
                while (seg < binCenters.Count - 2 && t > binCenters[seg + 1]) seg++;
                double c0 = binCenters[seg], c1 = binCenters[seg + 1];
                double f = c1 > c0 ? (t - c0) / (c1 - c0) : 0.0;
                if (f < 0) f = 0; else if (f > 1) f = 1; // clamp at the ends: hold the edge bins' medians
                double trend = binMedians[seg] + f * (binMedians[seg + 1] - binMedians[seg]);
                detrended.Add(new FluxSample(t, samples[i].Flux - (trend - 1.0), samples[i].UncertaintyFlux));
            }
            return detrended;
        }

        /// <summary>
        /// Bin width scales with the trial period so a fixed count never
        /// gets coarser than the data can support: width = medianCadence / BinsPerCadence,
        /// clamped to [MinPhaseBins, MaxPhaseBins] to bound the O(periodSteps * phaseBins^2 / 4) search cost.
        /// </summary>
        private static int ComputeAdaptivePhaseBins(double periodSeconds, double medianCadenceSeconds)
        {
            if (medianCadenceSeconds <= 0) return MinPhaseBins;
            double desiredBinWidthSeconds = medianCadenceSeconds / BinsPerCadence;
            int bins = (int)Math.Round(periodSeconds / desiredBinWidthSeconds);
            return Math.Max(MinPhaseBins, Math.Min(MaxPhaseBins, bins));
        }

        /// <summary>Samples arrive in increasing Ut order (see ObservationSession.Tick); median of consecutive gaps is robust to any stray irregular spacing.</summary>
        private static double ComputeMedianCadenceSeconds(List<FluxSample> samples)
        {
            if (samples.Count < 2) return 0.0;

            var gaps = new double[samples.Count - 1];
            for (int i = 1; i < samples.Count; i++) gaps[i - 1] = samples[i].Ut - samples[i - 1].Ut;
            Array.Sort(gaps);

            int mid = gaps.Length / 2;
            return gaps.Length % 2 == 0 ? (gaps[mid - 1] + gaps[mid]) / 2.0 : gaps[mid];
        }
    }
}