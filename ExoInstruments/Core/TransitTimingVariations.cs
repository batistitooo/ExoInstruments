using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Transit Timing Variations: in a multi-planet system, mutual gravitational
    /// perturbation makes each transit land slightly early or late instead of on
    /// a perfectly periodic ephemeris. The dominant, observable term for systems
    /// near a first-order mean-motion resonance j:j-1 is a sinusoid at the
    /// "super-period" P_ttv = 1/|j/P_outer - (j-1)/P_inner| with amplitude
    /// scaling as P * (m_perturber/M_star) / (j^(2/3) * |Delta|), Delta the
    /// fractional distance to exact resonance (Lithwick, Xie &amp; Wu 2012;
    /// Agol et al. 2005). The order-unity Laplace-coefficient factor is absorbed
    /// into the scaling -- order-of-magnitude honest, same caveat class as the
    /// other pipelines.
    ///
    /// This is how Kepler weighed the TRAPPIST-1 planets, and how planets that
    /// never transit at all have been discovered: pure gravity, no photons from
    /// the perturber needed.
    /// </summary>
    public static class TransitTimingVariations
    {
        private const double SecondsPerDay = 86400.0;
        private const double JupiterMassInSolarMasses = 0.0009543;

        /// <summary>Highest first-order resonance index considered (j:j-1 for j = 2..MaxResonanceIndex covers 2:1 down to 5:4).</summary>
        private const int MaxResonanceIndex = 5;

        /// <summary>
        /// |Delta| floor: exactly-on-resonance pairs would diverge in this
        /// first-order formula (the real dynamics saturate into libration);
        /// clamping keeps amplitudes finite without special-casing.
        /// </summary>
        private const double MinResonanceDistance = 0.01;

        /// <summary>Amplitude cap as a fraction of the transiter's period -- beyond this the sinusoidal approximation has no business being trusted.</summary>
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
        /// The TTV signal imprinted on one transiting planet by its catalog
        /// siblings -- the strongest single perturber wins (real multi-planet
        /// TTVs superpose, but one dominant near-resonant pair is the typical
        /// observed regime). Returns a non-significant signal for single-planet
        /// systems, unknown perturber masses, or decoys: no companion, no TTV,
        /// so a detected TTV is always genuine dynamical evidence.
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
                // A TTV bigger than the transit itself would defeat the linear-
                // ephemeris fold the detector relies on -- physically the system
                // would be deep in resonant libration, beyond this sinusoidal
                // model's remit anyway.
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

        /// <summary>Nearest first-order resonance j:j-1 to the pair's period ratio, and the fractional distance Delta = (P_out/P_in)*(j-1)/j - 1.</summary>
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

        /// <summary>Deterministic phase for a (transiter, perturber) pair -- same FNV-1a idiom as StellarActivity.</summary>
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

            /// <summary>
            /// Period from re-fitting the linear ephemeris to the measured
            /// mid-times themselves -- integrating transit times over the whole
            /// baseline beats the BLS grid's period resolution by orders of
            /// magnitude, which is exactly how surveys refine ephemerides.
            /// </summary>
            public double RefinedPeriodDays;

            public int EpochCount => Measurements.Count;
        }

        public const double DetectionSnrThreshold = 5.0;
        public const int MinMeasuredEpochs = 6;
        private const int MinPointsPerEpoch = 5;
        private const int ShiftGridSteps = 60;

        /// <summary>
        /// Measures individual transit mid-times against the detected linear
        /// ephemeris by sliding a box template of the detected depth/duration
        /// across each expected transit window and taking the chi^2 minimum --
        /// then searches the O-C series for a sinusoid, the TTV signature.
        /// Everything derives from the player's own detection (period, phase,
        /// depth, duration), no catalog truth consumed: fog-of-war safe.
        /// </summary>
        public static TtvAnalysisResult Analyze(List<FluxSample> samples, DetectionResult detection)
        {
            var result = new TtvAnalysisResult();
            if (samples == null || samples.Count == 0 || detection == null) return result;
            if (!detection.Detected || detection.BestPeriodDays <= 0 || detection.BestDurationHours <= 0) return result;

            // Same detrending the multi-planet search runs on: a starspot slope
            // across a transit window drags the chi^2 mid-time coherently at the
            // stellar rotation period -- a spurious "TTV" that has fooled real
            // surveys. Removing the slow trend first is the standard defense.
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

                // Samples arrive time-ordered (see ObservationSession.Tick): walk a
                // cursor forward instead of re-scanning the full series per epoch.
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

        /// <summary>
        /// Re-fits the linear ephemeris from the measured mid-times and
        /// subtracts it. The BLS period grid is coarse: even a 0.1% period error
        /// integrates into a monotonic O-C drift across the baseline -- that
        /// drift IS the period correction, not a timing variation, and left in
        /// place it fits a long-period sinusoid with spurious confidence. "O-C"
        /// in the literature always means observed minus a linear fit to the
        /// observations themselves, for exactly this reason.
        /// </summary>
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

        /// <summary>
        /// Chi^2 template scan for one epoch: box of the detected depth/duration,
        /// slid over a grid of trial shifts. Uncertainty from the local chi^2
        /// curvature (delta-chi^2 = 1), floored at a quarter grid step. Rejects
        /// epochs whose best shift pins the grid edge (no real minimum inside).
        /// </summary>
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

            // Grid-edge minimum: the transit isn't actually in this window (data
            // gap, or the detected ephemeris drifted) -- don't report a fake time.
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

        /// <summary>
        /// Sinusoid search over the O-C series -- same least-squares-per-trial-period
        /// idiom as RvDetector, on far fewer points. Trial super-periods span
        /// 4x the transit period up to the O-C baseline itself: a period the
        /// baseline doesn't cover at least once is degenerate with a slow drift,
        /// and fits noise with spurious confidence (same reasoning as
        /// RvDetector's baseline/2 cap, adapted to the sparser O-C series).
        /// </summary>
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
