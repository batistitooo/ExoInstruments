using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Transit Timing Variations: mutual gravitational perturbation near a first-order
    /// mean-motion resonance j:(j-1) shifts each transit by a sinusoid at the
    /// super-period P_ttv = 1/|j/P_out - (j-1)/P_in|. Amplitude scales as
    /// P * (m_perturber/M*) / (j^(2/3) * |delta|). Order-of-magnitude honest.
    /// </summary>
    public static class TransitTimingVariations
    {
        private const double SecondsPerDay = 86400.0;
        private const double JupiterMassInSolarMasses = 0.0009543;

        // Highest j checked: covers 2:1 through 5:4 resonances.
        private const int MaxResonanceIndex = 5;

        // |delta| floor: exactly on-resonance would diverge (real dynamics saturate into libration there).
        private const double MinResonanceDistance = 0.01;

        // Amplitude cap as a fraction of the transiter's period; beyond this the sinusoidal approximation has
        // no business being trusted.
        private const double MaxAmplitudeFractionOfPeriod = 0.02;

        /// <summary>Sinusoidal TTV of one transiting planet: transit k occurs at linear ephemeris + this shift.</summary>
        public struct TtvSignal
        {
            public double AmplitudeSeconds;
            public double SuperPeriodSeconds;
            public double Phase01;              // deterministic per pair, no real epoch to align with
            public string PerturberName;

            public bool IsSignificant => AmplitudeSeconds > 0.0 && SuperPeriodSeconds > 0.0;

            public double ShiftSeconds(double ut)
            {
                if (!IsSignificant) return 0.0;
                return AmplitudeSeconds * Math.Sin(2.0 * Math.PI * (ut / SuperPeriodSeconds + Phase01));
            }
        }

        /// <summary>
        /// TTV signal from the strongest perturber (one dominant near-resonant pair is
        /// the typical observed regime). Returns a non-significant signal for single-planet
        /// systems or unknown masses, so a detected TTV is always genuine dynamical evidence.
        /// </summary>
        public static TtvSignal ComputeSignal(StarTarget transiter, IList<StarTarget> systemPlanets)
        {
            var best = new TtvSignal();
            if (transiter == null || systemPlanets == null) return best;
            if (transiter.PlanetPeriodDays <= 0 || transiter.StellarMassSolar <= 0) return best;

            for (int i = 0; i < systemPlanets.Count; i++)
            {
                StarTarget perturber = systemPlanets[i];
                if (perturber == transiter) continue;
                if (!perturber.HasPlanet || perturber.Status == PlanetStatus.Retracted) continue;
                if (!perturber.PlanetMassJupiter.HasValue || perturber.PlanetMassJupiter.Value <= 0) continue;
                if (perturber.PlanetPeriodDays <= 0) continue;

                double innerPeriodDays = Math.Min(transiter.PlanetPeriodDays, perturber.PlanetPeriodDays);
                double outerPeriodDays = Math.Max(transiter.PlanetPeriodDays, perturber.PlanetPeriodDays);
                if (innerPeriodDays <= 0 || outerPeriodDays / innerPeriodDays > 6.0) continue; // far from any first-order resonance: negligible

                FindNearestFirstOrderResonance(innerPeriodDays, outerPeriodDays, out int j, out double delta);
                double absDelta = Math.Max(MinResonanceDistance, Math.Abs(delta));

                double massRatio = perturber.PlanetMassJupiter.Value * JupiterMassInSolarMasses / transiter.StellarMassSolar;
                double periodSeconds = transiter.PlanetPeriodDays * SecondsPerDay;
                double amplitudeSeconds = periodSeconds * (massRatio / Math.PI) / (Math.Pow(j, 2.0 / 3.0) * absDelta);
                amplitudeSeconds = Math.Min(amplitudeSeconds, periodSeconds * MaxAmplitudeFractionOfPeriod);
                // Cap at transit duration: a bigger TTV defeats the linear-ephemeris fold, and the system would be in deep resonant libration anyway.
                double? durationHours = transiter.EstimatedTransitDurationHours;
                if (durationHours.HasValue && durationHours.Value > 0)
                {
                    amplitudeSeconds = Math.Min(amplitudeSeconds, 0.75 * durationHours.Value * 3600.0);
                }

                // Super-period from the resonant angle's circulation rate.
                double superPeriodDays = 1.0 / Math.Abs(j / outerPeriodDays - (j - 1.0) / innerPeriodDays);

                if (amplitudeSeconds > best.AmplitudeSeconds)
                {
                    best = new TtvSignal
                    {
                        AmplitudeSeconds = amplitudeSeconds,
                        SuperPeriodSeconds = superPeriodDays * SecondsPerDay,
                        Phase01 = PairHash01(transiter, perturber),
                        PerturberName = perturber.Name,
                    };
                }
            }
            return best;
        }

        // Nearest first-order resonance j:j-1 to the pair's period ratio, and the fractional distance Delta =
        // (P_out/P_in)*(j-1)/j - 1.
        private static void FindNearestFirstOrderResonance(double innerPeriodDays, double outerPeriodDays, out int bestJ, out double bestDelta)
        {
            double ratio = outerPeriodDays / innerPeriodDays;
            bestJ = 2;
            bestDelta = double.MaxValue;
            for (int j = 2; j <= MaxResonanceIndex; j++)
            {
                double delta = ratio * (j - 1.0) / j - 1.0;
                if (Math.Abs(delta) < Math.Abs(bestDelta))
                {
                    bestDelta = delta;
                    bestJ = j;
                }
            }
        }

        // Deterministic phase for a (transiter, perturber) pair, same FNV-1a idiom as StellarActivity.
        private static double PairHash01(StarTarget a, StarTarget b)
        {
            string identity = (a.Name ?? "") + "|ttv|" + (b.Name ?? "");
            const uint fnvPrime = 16777619;
            uint hash = 2166136261;
            for (int i = 0; i < identity.Length; i++)
            {
                hash ^= identity[i];
                hash *= fnvPrime;
            }
            return hash / 4294967296.0;
        }

        // ------------------------------------------------------------------
        // Measurement side: per-transit mid-times and the O-C search.
        // ------------------------------------------------------------------

        /// <summary>One measured transit mid-time: epoch index, observed-minus-calculated offset, and its 1-sigma uncertainty.</summary>
        public struct TransitTimeMeasurement
        {
            public double ExpectedCenterUt;
            public double OMinusCSeconds;
            public double UncertaintySeconds;
            public int InTransitPoints;
        }

        public class TtvAnalysisResult
        {
            public bool Detected;
            public List<TransitTimeMeasurement> Measurements = new List<TransitTimeMeasurement>();
            public double BestAmplitudeSeconds;
            public double BestSuperPeriodDays;
            public double Snr;
            public double RmsSeconds;             // scatter of the O-C series, after the linear re-fit

            /// <summary>Period from a linear re-fit to measured mid-times, far finer than the BLS grid, which is how real surveys refine ephemerides.</summary>
            public double RefinedPeriodDays;

            public int EpochCount => Measurements.Count;
        }

        public const double DetectionSnrThreshold = 5.0;
        public const int MinMeasuredEpochs = 6;
        private const int MinPointsPerEpoch = 5;
        private const int ShiftGridSteps = 60;

        /// <summary>
        /// Measures individual transit mid-times by chi-squared template scan, then searches the
        /// O-C series for a sinusoid. Derives everything from the player's detection,
        /// no catalog truth consumed.
        /// </summary>
        public static TtvAnalysisResult Analyze(List<FluxSample> samples, DetectionResult detection)
        {
            var result = new TtvAnalysisResult();
            if (samples == null || samples.Count == 0 || detection == null) return result;
            if (!detection.Detected || detection.BestPeriodDays <= 0 || detection.BestDurationHours <= 0) return result;

            // Detrend first: a starspot slope across a transit window biases mid-times at the rotation period, creating a spurious TTV signal.
            samples = TransitDetector.DetrendSamples(samples);

            double periodSec = detection.BestPeriodDays * SecondsPerDay;
            double durationSec = detection.BestDurationHours * 3600.0;
            double depthFraction = detection.BestDepthPpm / 1_000_000.0;
            if (depthFraction <= 0) return result;

            // The BLS phase marks the box start; mid-transit sits half a duration later.
            double centerPhase = detection.BestPhase01 + 0.5 * (durationSec / periodSec);

            double firstUt = samples[0].Ut;
            double lastUt = samples[samples.Count - 1].Ut;
            long firstEpoch = (long)Math.Floor(firstUt / periodSec - centerPhase);
            long lastEpoch = (long)Math.Ceiling(lastUt / periodSec - centerPhase);

            double maxShift = Math.Min(0.5 * durationSec, 0.25 * periodSec);
            double windowHalfWidth = durationSec * 1.5 + maxShift;

            int sampleCursor = 0;
            var window = new List<FluxSample>();
            for (long k = firstEpoch; k <= lastEpoch; k++)
            {
                double centerUt = (k + centerPhase) * periodSec;
                if (centerUt < firstUt - windowHalfWidth || centerUt > lastUt + windowHalfWidth) continue;

                // Samples are time-ordered: cursor walks forward instead of re-scanning per epoch.
                while (sampleCursor < samples.Count && samples[sampleCursor].Ut < centerUt - windowHalfWidth) sampleCursor++;
                window.Clear();
                for (int i = sampleCursor; i < samples.Count && samples[i].Ut <= centerUt + windowHalfWidth; i++)
                {
                    window.Add(samples[i]);
                }
                if (window.Count < MinPointsPerEpoch) continue;

                if (MeasureOneEpoch(window, centerUt, durationSec, depthFraction, maxShift,
                        out double oMinusC, out double uncertainty, out int inTransitPoints))
                {
                    result.Measurements.Add(new TransitTimeMeasurement
                    {
                        ExpectedCenterUt = centerUt,
                        OMinusCSeconds = oMinusC,
                        UncertaintySeconds = uncertainty,
                        InTransitPoints = inTransitPoints,
                    });
                }
            }

            if (result.Measurements.Count < MinMeasuredEpochs) return result;

            SubtractLinearEphemeris(result, periodSec);
            ComputeRms(result);
            SearchSinusoid(result, periodSec);
            return result;
        }

        // Re-fits and subtracts the linear ephemeris from measured mid-times. A coarse BLS period leaves a
        // monotonic O-C drift that would be mistaken for a long-period TTV; "O-C" always means observed minus a
        // fresh linear fit.
        private static void SubtractLinearEphemeris(TtvAnalysisResult result, double detectedPeriodSec)
        {
            var m = result.Measurements;
            int n = m.Count;
            double t0 = m[0].ExpectedCenterUt;

            double sT = 0, sTT = 0, sV = 0, sTV = 0;
            for (int i = 0; i < n; i++)
            {
                double t = m[i].ExpectedCenterUt - t0;
                double v = m[i].OMinusCSeconds;
                sT += t; sTT += t * t; sV += v; sTV += t * v;
            }
            double det = n * sTT - sT * sT;
            if (Math.Abs(det) < 1e-12)
            {
                result.RefinedPeriodDays = detectedPeriodSec / SecondsPerDay;
                return;
            }
            double slope = (n * sTV - sT * sV) / det;       // seconds of drift per second of time
            double intercept = (sV - slope * sT) / n;

            for (int i = 0; i < n; i++)
            {
                double t = m[i].ExpectedCenterUt - t0;
                var updated = m[i];
                updated.OMinusCSeconds -= intercept + slope * t;
                m[i] = updated;
            }
            result.RefinedPeriodDays = detectedPeriodSec * (1.0 + slope) / SecondsPerDay;
        }

        // Chi-squared template scan for one epoch. Uncertainty from the local curvature (delta chi-squared =
        // 1), floored at a quarter grid step. Rejects epochs where the minimum pins the edge.
        private static bool MeasureOneEpoch(List<FluxSample> window, double centerUt, double durationSec,
            double depthFraction, double maxShift, out double bestShift, out double uncertainty, out int inTransitPoints)
        {
            bestShift = 0.0;
            uncertainty = 0.0;
            inTransitPoints = 0;

            double step = 2.0 * maxShift / ShiftGridSteps;
            double bestChi2 = double.MaxValue;
            var chi2Grid = new double[ShiftGridSteps + 1];

            for (int s = 0; s <= ShiftGridSteps; s++)
            {
                double shift = -maxShift + s * step;
                double chi2 = 0.0;
                for (int i = 0; i < window.Count; i++)
                {
                    double model = Math.Abs(window[i].Ut - (centerUt + shift)) <= durationSec / 2.0
                        ? 1.0 - depthFraction
                        : 1.0;
                    double sigma = Math.Max(1e-9, window[i].UncertaintyFlux);
                    double r = (window[i].Flux - model) / sigma;
                    chi2 += r * r;
                }
                chi2Grid[s] = chi2;
                if (chi2 < bestChi2)
                {
                    bestChi2 = chi2;
                    bestShift = shift;
                }
            }

            // Grid-edge minimum means the transit is outside this window, skip it.
            if (bestShift <= -maxShift + step / 2.0 || bestShift >= maxShift - step / 2.0) return false;

            for (int i = 0; i < window.Count; i++)
            {
                if (Math.Abs(window[i].Ut - (centerUt + bestShift)) <= durationSec / 2.0) inTransitPoints++;
            }
            if (inTransitPoints < MinPointsPerEpoch) return false;

            // 1-sigma from where chi^2 rises by 1 above the minimum, scanning outward.
            double sigmaShift = maxShift;
            int bestIndex = (int)Math.Round((bestShift + maxShift) / step);
            for (int d = 1; d <= ShiftGridSteps; d++)
            {
                int lo = bestIndex - d, hi = bestIndex + d;
                bool loRose = lo >= 0 && chi2Grid[lo] >= bestChi2 + 1.0;
                bool hiRose = hi <= ShiftGridSteps && chi2Grid[hi] >= bestChi2 + 1.0;
                if (loRose || hiRose)
                {
                    sigmaShift = d * step;
                    break;
                }
            }
            uncertainty = Math.Max(sigmaShift, step / 4.0);
            return true;
        }

        private static void ComputeRms(TtvAnalysisResult result)
        {
            double sum = 0.0, sumSq = 0.0;
            int n = result.Measurements.Count;
            for (int i = 0; i < n; i++)
            {
                sum += result.Measurements[i].OMinusCSeconds;
            }
            double mean = sum / n;
            for (int i = 0; i < n; i++)
            {
                double d = result.Measurements[i].OMinusCSeconds - mean;
                sumSq += d * d;
            }
            result.RmsSeconds = Math.Sqrt(sumSq / Math.Max(1, n - 1));
        }

        // Sinusoid search over the O-C series, same idiom as RvDetector. Super-period range: 4* transit period
        // up to the O-C baseline (a longer period is degenerate with drift).
        private static void SearchSinusoid(TtvAnalysisResult result, double transitPeriodSec)
        {
            var m = result.Measurements;
            int n = m.Count;
            double baselineSec = m[n - 1].ExpectedCenterUt - m[0].ExpectedCenterUt;
            double minPeriodSec = 4.0 * transitPeriodSec;
            double maxPeriodSec = baselineSec;
            if (maxPeriodSec <= minPeriodSec) return;

            const int periodSteps = 400;
            double bestSnr = 0.0, bestAmplitude = 0.0, bestPeriodSec = 0.0;

            for (int p = 0; p < periodSteps; p++)
            {
                double periodSec = minPeriodSec + (maxPeriodSec - minPeriodSec) * p / (periodSteps - 1.0);
                double omega = 2.0 * Math.PI / periodSec;

                double sCC = 0, sSS = 0, sCS = 0, sC = 0, sS = 0, sCV = 0, sSV = 0, sV = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = m[i].ExpectedCenterUt;
                    double v = m[i].OMinusCSeconds;
                    double c = Math.Cos(omega * t), s = Math.Sin(omega * t);
                    sCC += c * c; sSS += s * s; sCS += c * s; sC += c; sS += s;
                    sCV += c * v; sSV += s * v; sV += v;
                }

                double det = sCC * (sSS * n - sS * sS) - sCS * (sCS * n - sS * sC) + sC * (sCS * sS - sSS * sC);
                if (Math.Abs(det) < 1e-12) continue;
                double a = (sCV * (sSS * n - sS * sS) - sCS * (sSV * n - sS * sV) + sC * (sSV * sS - sSS * sV)) / det;
                double b = (sCC * (sSV * n - sV * sS) - sCV * (sCS * n - sS * sC) + sC * (sCS * sV - sSV * sC)) / det;
                double c0 = (sCC * (sSS * sV - sSV * sS) - sCS * (sCS * sV - sSV * sC) + sCV * (sCS * sS - sSS * sC)) / det;

                double rss = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double t = m[i].ExpectedCenterUt;
                    double model = a * Math.Cos(omega * t) + b * Math.Sin(omega * t) + c0;
                    double r = m[i].OMinusCSeconds - model;
                    rss += r * r;
                }
                double residualStd = Math.Sqrt(rss / Math.Max(1, n - 3));
                double amplitude = Math.Sqrt(a * a + b * b);
                double sigmaAmplitude = Math.Max(1e-9, residualStd * Math.Sqrt(2.0 / n));
                double snr = amplitude / sigmaAmplitude;

                if (snr > bestSnr)
                {
                    bestSnr = snr;
                    bestAmplitude = amplitude;
                    bestPeriodSec = periodSec;
                }
            }

            result.Snr = bestSnr;
            result.BestAmplitudeSeconds = bestAmplitude;
            result.BestSuperPeriodDays = bestPeriodSec / SecondsPerDay;
            result.Detected = bestSnr >= DetectionSnrThreshold;
        }
    }
}
