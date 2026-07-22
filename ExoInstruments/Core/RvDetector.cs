using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Simplified Lomb-Scargle-style period search: for each trial period, fits
    /// v(t) = A*cos(wt) + B*sin(wt) + C by linear least squares and scores it by
    /// semi-amplitude / fit uncertainty. Not a full generalized Lomb-Scargle
    /// (Zechmeister &amp; Kurster 2009) — no proper false-alarm probability
    /// calibration, no aliasing/window-function handling. Treat SNR as relative
    /// confidence, not a calibrated statistic — same caveat as TransitDetector.
    ///
    /// Known bias: a single-harmonic sinusoid fit systematically underestimates K
    /// for eccentric orbits (a Keplerian RV curve has real power at the 2nd/3rd
    /// harmonics once e is non-trivial) — period recovery stays accurate, but the
    /// reported semi-amplitude runs low with increasing eccentricity. A full
    /// nonlinear Keplerian fit would fix this; out of scope for this pass.
    /// </summary>
    public static class RvDetector
    {
        public const double DefaultSnrThreshold = 8.0;
        public const int MinSampleCount = 10;
        public const int MaxPlanetsPerSearch = 4;

        /// <summary>
        /// Physical sanity cap on a fitted semi-amplitude: a real periodic signal
        /// present in the data can't legitimately carry an amplitude many times
        /// the data's own total scatter (a sinusoid of amplitude A contributes
        /// ~A^2/2 to the variance, so A can't cleanly exceed stdV*sqrt(2) without
        /// something being numerically wrong). This is the second, independent
        /// line of defense against the near-singular-fit failure this class's
        /// own header already warns about -- FitSinusoid's determinant guard
        /// alone wasn't catching it at large sample counts (see there).
        /// </summary>
        private const double MaxPlausibleAmplitudeFactor = 8.0;

        /// <summary>
        /// Trial periods within this fraction of an exact small-integer multiple
        /// of the data's own sampling cadence are skipped. At a perfectly regular
        /// cadence, a trial period of exactly 2x that cadence makes
        /// sin(omega*t_i) vanish at every sample (omega*t_i = pi*i), collapsing
        /// the cos/sin design matrix onto a near-degenerate axis that noise can
        /// fit with a deceptively plausible amplitude and SNR -- reproduced with
        /// a clean single-planet 51 Peg b simulation (real period correctly
        /// recovered at SNR ~110, but a phantom second "planet" also detected at
        /// P = 2x the RV cadence, SNR ~33, on every run). Real periodogram
        /// practice excludes exactly these cadence aliases rather than trying to
        /// out-threshold them statistically. Checked against k = 1..6.
        /// </summary>
        private const double CadenceAliasToleranceFraction = 0.03;
        private const int MaxCadenceAliasMultiple = 6;

        /// <summary>
        /// Minimum observation baseline for a period to even be testable by Detect --
        /// mirrors the baselineDaysForSearch/2 clamp below, floored by whatever
        /// MinSampleCount needs at this instrument's cadence. Lets the GUI tell the
        /// player up front how long a target/instrument pairing will realistically take,
        /// instead of them finding out only after warping blind for a while.
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

            // A period can't be meaningfully constrained unless the baseline covers
            // at least a couple of cycles of it -- past that, cos(wt)/sin(wt) barely
            // vary over the observed window, the fit becomes numerically near-singular,
            // and it "fits" pure noise with a spuriously huge amplitude and SNR. Cap the
            // search so it never trusts periods the data genuinely can't distinguish.
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

            // Local refinement around the coarse-grid peak. The coarse step can be off
            // by a fractional period error that, integrated over many cycles of
            // baseline, leaves a large coherent residual after subtraction -- enough
            // for DetectMultiple's next prewhitening pass to re-detect the same planet
            // as a phantom signal. One fine scan (+/- one coarse step) fixes both the
            // reported period and the subtraction quality.
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
        /// Iterative prewhitening for multi-planet systems, the standard RV-survey
        /// procedure: detect the strongest periodic signal, subtract its best-fit
        /// sinusoid from the data, search the residuals for the next signal, repeat
        /// until nothing clears the SNR threshold or MaxPlanetsPerSearch is reached.
        ///
        /// Every attempted stage is returned (the final, below-threshold one included,
        /// so the report can show what the residual search bottomed out at). Each stage
        /// carries the residual series it searched — that's the series its phase-folded
        /// plot should display, otherwise the stronger planets' signals visually swamp
        /// the weaker ones.
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

        /// <summary>
        /// Returns the period of an earlier detection this one sits at a near-integer
        /// ratio of (within 5%, ratios 1:1 to 3:1), else null. See
        /// RvDetectionResult.LikelyHarmonicOfPeriodDays for why this matters.
        /// </summary>
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

        /// <summary>See CadenceAliasToleranceFraction. medianCadenceSeconds &lt;= 0 (fewer than 2 samples) disables the guard.</summary>
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

        /// <summary>Samples arrive in increasing Ut order; median of consecutive gaps is robust to any stray irregular spacing. Mirrors TransitDetector's helper of the same name.</summary>
        private static double ComputeMedianCadenceSeconds(List<RvSample> samples)
        {
            if (samples.Count < 2) return 0.0;

            var gaps = new double[samples.Count - 1];
            for (int i = 1; i < samples.Count; i++) gaps[i - 1] = samples[i].Ut - samples[i - 1].Ut;
            Array.Sort(gaps);

            int mid = gaps.Length / 2;
            return gaps.Length % 2 == 0 ? (gaps[mid - 1] + gaps[mid]) / 2.0 : gaps[mid];
        }

        /// <summary>
        /// Least-squares fit of v(t) = A*cos(wt) + B*sin(wt) + C via the 3x3 normal
        /// equations, solved directly with Cramer's rule (cheap at this size, no
        /// matrix library needed).
        /// </summary>
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
            // A well-conditioned fit (full phase coverage) has sCC ~ sSS ~ n/2 and
            // sCS ~ sC ~ sS ~ 0, so det scales like n^3/4. The old fixed 1e-12
            // absolute floor only rejected fits that were essentially exactly
            // singular; at the sample counts a long baseline produces (n in the
            // thousands, ideal det ~1e9-1e10), a trial period with poor phase
            // coverage could land many orders of magnitude below that scale --
            // ill-conditioned, not truly singular -- and still clear 1e-12,
            // producing the wildly inflated amplitude/SNR this class's header
            // already warned about. Reject relative to the ideal scale instead.
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
