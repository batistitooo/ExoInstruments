using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Simplified Lomb-Scargle-style period search: fits v(t) = A*cos(wt) + B*sin(wt) + C
    /// for each trial period. No false-alarm calibration; treat SNR as relative confidence.
    /// Known bias: single-harmonic fit underestimates K on eccentric orbits (real power in
    /// 2nd/3rd harmonics). Period recovery stays accurate; amplitude runs low with eccentricity.
    /// </summary>
    public static class RvDetector
    {
        public const double DefaultSnrThreshold = 8.0;
        public const int MinSampleCount = 10;
        public const int MaxPlanetsPerSearch = 4;

        // Second defense against near-singular fits: a real signal can't carry amplitude
        // many times the data's own scatter. FitSinusoid's determinant guard alone wasn't
        // enough at large sample counts.
        private const double MaxPlausibleAmplitudeFactor = 8.0;

        // Periods near an integer multiple of the sampling cadence make sin(ωt_i) vanish at
        // every sample; the fit goes degenerate and produces phantom high-SNR signals. Skip them.
        // (Verified with a 51 Peg b simulation: real period at SNR ~110, phantom at 2* cadence SNR ~33.)
        private const double CadenceAliasToleranceFraction = 0.03;
        private const int MaxCadenceAliasMultiple = 6;

        /// <summary>
        /// Minimum baseline before a given period is testable; mirrors the effectiveMaxPeriodDays/2 clamp in
        /// Detect. Lets the GUI estimate observing time before the player commits.
        /// </summary>
        public static double EstimateRequiredBaselineDays(double catalogPeriodDays, double cadenceSeconds)
        {
            double periodBaseline = catalogPeriodDays > 0 ? catalogPeriodDays * 2.0 : 0.0;
            double sampleCountBaseline = MinSampleCount * cadenceSeconds / 86400.0;
            return Math.Max(periodBaseline, sampleCountBaseline);
        }

        public static RvDetectionResult Detect(
            List<RvSample> samples,
            double minPeriodDays = 0.5,
            double maxPeriodDays = 1000.0,
            int periodSteps = 2000,
            double snrThreshold = DefaultSnrThreshold)
        {
            if (samples == null || samples.Count < MinSampleCount)
                return RvDetectionResult.Insufficient(samples?.Count ?? 0);

            int n = samples.Count;
            double totalSum = 0.0;
            for (int i = 0; i < n; i++) totalSum += samples[i].VelocityMps;
            double meanV = totalSum / n;

            double sqDiffSum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double d = samples[i].VelocityMps - meanV;
                sqDiffSum += d * d;
            }
            double stdV = Math.Sqrt(sqDiffSum / Math.Max(1, n - 1));
            if (stdV < 1e-9) stdV = 1e-9;

            double baselineDaysForSearch = (samples[n - 1].Ut - samples[0].Ut) / 86400.0;
            double medianCadenceSeconds = ComputeMedianCadenceSeconds(samples);

            // Cap at baseline/2: longer periods aren't constrained and the fit goes spurious.
            double effectiveMaxPeriodDays = Math.Min(maxPeriodDays, Math.Max(minPeriodDays, baselineDaysForSearch / 2.0));

            double minPeriodSec = minPeriodDays * 86400.0;
            double maxPeriodSec = effectiveMaxPeriodDays * 86400.0;

            double bestSnr = 0.0, bestPeriodDays = 0.0, bestAmplitude = 0.0, bestPhase = 0.0, bestResidualStd = stdV;

            for (int p = 0; p < periodSteps; p++)
            {
                double periodSec = minPeriodSec + (maxPeriodSec - minPeriodSec) * p / Math.Max(1, periodSteps - 1);
                if (IsNearCadenceAlias(periodSec, medianCadenceSeconds)) continue;
                double omega = 2.0 * Math.PI / periodSec;

                if (!FitSinusoid(samples, omega, out double a, out double b, out _, out double residualStd))
                    continue;

                double amplitude = Math.Sqrt(a * a + b * b);
                if (amplitude > MaxPlausibleAmplitudeFactor * stdV) continue; // see MaxPlausibleAmplitudeFactor
                double sigmaAmplitude = residualStd * Math.Sqrt(2.0 / n);
                if (sigmaAmplitude < 1e-9) sigmaAmplitude = 1e-9;
                double snr = amplitude / sigmaAmplitude;

                if (snr > bestSnr)
                {
                    bestSnr = snr;
                    bestPeriodDays = periodSec / 86400.0;
                    bestAmplitude = amplitude;
                    bestPhase = Math.Atan2(-b, a) / (2.0 * Math.PI);
                    if (bestPhase < 0) bestPhase += 1.0;
                    bestResidualStd = residualStd;
                }
            }

            // Fine refinement: the coarse grid can leave a fractional period error that,
            // over many baseline cycles, creates a large coherent residual and fools
            // the next prewhitening pass into a phantom re-detection.
            if (bestPeriodDays > 0)
            {
                double coarseStepDays = (maxPeriodSec - minPeriodSec) / 86400.0 / Math.Max(1, periodSteps - 1);
                double loDays = Math.Max(minPeriodDays, bestPeriodDays - coarseStepDays);
                double hiDays = Math.Min(effectiveMaxPeriodDays, bestPeriodDays + coarseStepDays);
                const int fineSteps = 200;
                for (int k = 0; k <= fineSteps; k++)
                {
                    double periodSec = (loDays + (hiDays - loDays) * k / fineSteps) * 86400.0;
                    if (IsNearCadenceAlias(periodSec, medianCadenceSeconds)) continue;
                    double omega = 2.0 * Math.PI / periodSec;

                    if (!FitSinusoid(samples, omega, out double a, out double b, out _, out double residualStd))
                        continue;

                    double amplitude = Math.Sqrt(a * a + b * b);
                    if (amplitude > MaxPlausibleAmplitudeFactor * stdV) continue;
                    double sigmaAmplitude = residualStd * Math.Sqrt(2.0 / n);
                    if (sigmaAmplitude < 1e-9) sigmaAmplitude = 1e-9;
                    double snr = amplitude / sigmaAmplitude;

                    if (snr > bestSnr)
                    {
                        bestSnr = snr;
                        bestPeriodDays = periodSec / 86400.0;
                        bestAmplitude = amplitude;
                        bestPhase = Math.Atan2(-b, a) / (2.0 * Math.PI);
                        if (bestPhase < 0) bestPhase += 1.0;
                        bestResidualStd = residualStd;
                    }
                }
            }

            double amplitudeUncertainty = bestResidualStd * Math.Sqrt(2.0 / n);

            return new RvDetectionResult
            {
                Detected = bestSnr >= snrThreshold,
                InsufficientData = false,
                BestPeriodDays = bestPeriodDays,
                BestSemiAmplitudeMps = bestAmplitude,
                BestPhase01 = bestPhase,
                Snr = bestSnr,
                SampleCount = n,
                BaselineDays = baselineDaysForSearch,
                RvPrecisionMps = stdV,
                SemiAmplitudeUncertaintyMps = amplitudeUncertainty
            };
        }

        /// <summary>
        /// Iterative prewhitening: detect the strongest signal, subtract it, repeat on
        /// residuals until nothing clears the threshold. Returns all stages including
        /// the final below-threshold one, each carrying the series it was searched in
        /// (needed for phase-folded plots so stronger signals don't swamp weaker ones).
        /// </summary>
        public static List<RvDetectionStage> DetectMultiple(
            List<RvSample> samples,
            int maxPlanets = MaxPlanetsPerSearch,
            double minPeriodDays = 0.5,
            double maxPeriodDays = 1000.0,
            int periodSteps = 2000,
            double snrThreshold = DefaultSnrThreshold)
        {
            var stages = new List<RvDetectionStage>();
            // RvSample is a struct, so this shallow copy fully decouples the working
            // series from the caller's list before we start rewriting velocities.
            var working = samples == null ? null : new List<RvSample>(samples);

            for (int planetIndex = 0; planetIndex < maxPlanets; planetIndex++)
            {
                RvDetectionResult result = Detect(working, minPeriodDays, maxPeriodDays, periodSteps, snrThreshold);
                result.LikelyHarmonicOfPeriodDays = FindHarmonicParentPeriodDays(result.BestPeriodDays, stages);
                stages.Add(new RvDetectionStage { Result = result, SearchedSamples = working });

                if (result.InsufficientData || !result.Detected) break;

                double omega = 2.0 * Math.PI / (result.BestPeriodDays * 86400.0);
                if (!FitSinusoid(working, omega, out double a, out double b, out double c, out _)) break;

                var residuals = new List<RvSample>(working.Count);
                foreach (var s in working)
                {
                    double model = a * Math.Cos(omega * s.Ut) + b * Math.Sin(omega * s.Ut) + c;
                    residuals.Add(new RvSample(s.Ut, s.VelocityMps - model, s.UncertaintyMps));
                }
                working = residuals;
            }
            return stages;
        }

        // Prior detection whose period this one sits within 5% of a 1:1 to 3:1 ratio with; null otherwise.
        private static double? FindHarmonicParentPeriodDays(double periodDays, List<RvDetectionStage> priorStages)
        {
            if (periodDays <= 0) return null;
            foreach (var stage in priorStages)
            {
                double prior = stage.Result.BestPeriodDays;
                if (prior <= 0 || !stage.Result.Detected) continue;
                double ratio = Math.Max(prior, periodDays) / Math.Min(prior, periodDays);
                double nearestInteger = Math.Round(ratio);
                if (nearestInteger >= 1.0 && nearestInteger <= 3.0 && Math.Abs(ratio - nearestInteger) < 0.05 * nearestInteger)
                    return prior;
            }
            return null;
        }

        // See CadenceAliasToleranceFraction. medianCadenceSeconds <= 0 (fewer than 2 samples) disables the
        // guard.
        private static bool IsNearCadenceAlias(double periodSec, double medianCadenceSeconds)
        {
            if (medianCadenceSeconds <= 0) return false;
            for (int k = 1; k <= MaxCadenceAliasMultiple; k++)
            {
                double aliasPeriod = k * medianCadenceSeconds;
                if (Math.Abs(periodSec - aliasPeriod) < CadenceAliasToleranceFraction * aliasPeriod) return true;
            }
            return false;
        }

        // Samples arrive in increasing Ut order; median of consecutive gaps is robust to any stray irregular
        // spacing. Mirrors TransitDetector's helper of the same name.
        private static double ComputeMedianCadenceSeconds(List<RvSample> samples)
        {
            if (samples.Count < 2) return 0.0;

            var gaps = new double[samples.Count - 1];
            for (int i = 1; i < samples.Count; i++) gaps[i - 1] = samples[i].Ut - samples[i - 1].Ut;
            Array.Sort(gaps);

            int mid = gaps.Length / 2;
            return gaps.Length % 2 == 0 ? (gaps[mid - 1] + gaps[mid]) / 2.0 : gaps[mid];
        }

        // Least-squares fit of v(t) = A*cos(wt) + B*sin(wt) + C via the 3x3 normal equations (Cramer's rule).
        private static bool FitSinusoid(List<RvSample> samples, double omega, out double a, out double b, out double c, out double residualStd)
        {
            a = b = c = 0.0;
            residualStd = 0.0;
            int n = samples.Count;

            double sCC = 0, sSS = 0, sCS = 0, sC = 0, sS = 0;
            double sCV = 0, sSV = 0, sV = 0;

            for (int i = 0; i < n; i++)
            {
                double t = samples[i].Ut;
                double v = samples[i].VelocityMps;
                double cosWt = Math.Cos(omega * t);
                double sinWt = Math.Sin(omega * t);

                sCC += cosWt * cosWt;
                sSS += sinWt * sinWt;
                sCS += cosWt * sinWt;
                sC += cosWt;
                sS += sinWt;
                sCV += cosWt * v;
                sSV += sinWt * v;
                sV += v;
            }

            double det = Determinant3(sCC, sCS, sC, sCS, sSS, sS, sC, sS, n);
            // Reject relative to the ideal det scale (~n^3/4): an absolute 1e-12 floor misses
            // ill-conditioned fits at large n, letting them return spuriously large amplitude/SNR.
            double idealDetScale = Math.Max(1.0, n) * Math.Max(1.0, n) * Math.Max(1.0, n) / 4.0;
            if (Math.Abs(det) < Math.Max(1e-12, 1e-6 * idealDetScale)) return false;

            a = Determinant3(sCV, sCS, sC, sSV, sSS, sS, sV, sS, n) / det;
            b = Determinant3(sCC, sCV, sC, sCS, sSV, sS, sC, sV, n) / det;
            c = Determinant3(sCC, sCS, sCV, sCS, sSS, sSV, sC, sS, sV) / det;

            double rss = 0.0;
            for (int i = 0; i < n; i++)
            {
                double t = samples[i].Ut;
                double model = a * Math.Cos(omega * t) + b * Math.Sin(omega * t) + c;
                double resid = samples[i].VelocityMps - model;
                rss += resid * resid;
            }
            residualStd = Math.Sqrt(rss / Math.Max(1, n - 3));
            return true;
        }

        private static double Determinant3(
            double m00, double m01, double m02,
            double m10, double m11, double m12,
            double m20, double m21, double m22)
        {
            return m00 * (m11 * m22 - m12 * m21)
                 - m01 * (m10 * m22 - m12 * m20)
                 + m02 * (m10 * m21 - m11 * m20);
        }
    }
}
