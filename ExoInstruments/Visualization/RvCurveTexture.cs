using System;
using System.Collections.Generic;
using UnityEngine;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>Velocity range (already padded) a render call used -- lets the GUI layer place axis tick labels that line up with the plotted data.</summary>
    public struct RvPlotRange
    {
        public double MinVelocityMps;
        public double MaxVelocityMps;
    }

    /// <summary>
    /// Renders RvSample data into Texture2D scatter plots for IMGUI display,
    /// mirroring LightCurveTexture. Unity-dependent by design — kept separate
    /// from Core so Core stays engine-agnostic and testable.
    /// </summary>
    public static class RvCurveTexture
    {
        private static readonly Color BackgroundColor = new Color(0.08f, 0.08f, 0.1f, 1f);
        private static readonly Color PointColor = new Color(1f, 0.65f, 0.3f, 1f); // distinct from the transit plot's cyan
        private static readonly Color ErrorBarColor = new Color(1f, 0.65f, 0.3f, 0.45f);
        private static readonly Color BaselineColor = new Color(0.5f, 0.9f, 0.5f, 0.6f);

        /// <summary>Phase bins for the folded view -- same rationale as LightCurveTexture's: RV baselines that cover many cadence epochs pile up hundreds of raw error bars per plot otherwise.</summary>
        private const int MaxPhaseFoldBinCount = 100;

        /// <summary>Floor on bin count so short-baseline RV sessions (a few dozen points, typical given hours-long cadences) still get *some* folding rather than one bin per point.</summary>
        private const int MinPhaseFoldBinCount = 8;

        /// <summary>Bins under this count are dropped rather than drawn -- matches LightCurveTexture's MinBinSampleCount cutoff.</summary>
        private const int MinBinSampleCount = 3;

        /// <summary>
        /// Fixed 100 bins assumes hundreds-to-thousands of points, true for transit's
        /// 30s-600s cadences but not for RV's 6-8h cadences -- a real session might only
        /// have a few dozen samples, which spread ~0.3/bin at a fixed 100 and get filtered
        /// out almost entirely by MinBinSampleCount, leaving the plot blank. Scale bin
        /// count down so the average bin actually clears the threshold.
        /// </summary>
        private static int ComputeAdaptiveBinCount(int sampleCount)
        {
            int bins = sampleCount / MinBinSampleCount;
            return Math.Max(MinPhaseFoldBinCount, Math.Min(MaxPhaseFoldBinCount, bins));
        }

        /// <summary>Raw velocity vs. time (universal time, seconds), x-axis spans the full observation.</summary>
        public static Texture2D RenderRawRvCurve(List<RvSample> samples, int width, int height, out RvPlotRange range)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            var pixels = FillBackground(width, height);

            if (samples == null || samples.Count == 0)
            {
                tex.SetPixels(pixels);
                tex.Apply();
                range = new RvPlotRange { MinVelocityMps = -1.0, MaxVelocityMps = 1.0 };
                return tex;
            }

            double minUt = samples[0].Ut;
            double maxUt = samples[samples.Count - 1].Ut;
            double utRange = Math.Max(maxUt - minUt, 1.0);

            double minV, maxV;
            ComputeVelocityRange(samples, out minV, out maxV);
            range = new RvPlotRange { MinVelocityMps = minV, MaxVelocityMps = maxV };
            DrawBaseline(pixels, width, height, minV, maxV, 0.0);

            foreach (var s in samples)
            {
                int x = (int)((s.Ut - minUt) / utRange * (width - 1));
                DrawErrorBar(pixels, width, height, x, s.VelocityMps, s.UncertaintyMps, minV, maxV);
            }
            foreach (var s in samples)
            {
                int x = (int)((s.Ut - minUt) / utRange * (width - 1));
                int y = MapVelocityToY(s.VelocityMps, minV, maxV, height);
                PlotPoint(pixels, width, height, x, y, PointColor);
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>Phase-folded velocity vs. orbital phase (0-1), using a known period from detection.</summary>
        public static Texture2D RenderPhaseFoldedRvCurve(List<RvSample> samples, double periodDays, int width, int height, out RvPlotRange range)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            var pixels = FillBackground(width, height);

            if (samples == null || samples.Count == 0 || periodDays <= 0)
            {
                tex.SetPixels(pixels);
                tex.Apply();
                range = new RvPlotRange { MinVelocityMps = -1.0, MaxVelocityMps = 1.0 };
                return tex;
            }

            double periodSeconds = periodDays * 86400.0;
            int binCount = ComputeAdaptiveBinCount(samples.Count);
            var bins = BinByPhase(samples, periodSeconds, binCount);

            bool anyBinPopulated = false;
            foreach (var bin in bins)
            {
                if (bin.Count > 0) { anyBinPopulated = true; break; }
            }

            // Even the adaptive floor can come up empty for a handful of points spread
            // thin by phase aliasing (short period relative to a coarse cadence). Rather
            // than hand back a blank texture, fall back to the raw per-sample scatter --
            // less tidy, but at least the player sees the data they collected.
            if (!anyBinPopulated)
            {
                double minVRaw, maxVRaw;
                ComputeVelocityRange(samples, out minVRaw, out maxVRaw);
                range = new RvPlotRange { MinVelocityMps = minVRaw, MaxVelocityMps = maxVRaw };
                DrawBaseline(pixels, width, height, minVRaw, maxVRaw, 0.0);

                foreach (var s in samples)
                {
                    double rawPhase = (s.Ut / periodSeconds) % 1.0;
                    if (rawPhase < 0) rawPhase += 1.0;
                    int rawX = (int)(rawPhase * (width - 1));
                    DrawErrorBar(pixels, width, height, rawX, s.VelocityMps, s.UncertaintyMps, minVRaw, maxVRaw);
                }
                foreach (var s in samples)
                {
                    double rawPhase = (s.Ut / periodSeconds) % 1.0;
                    if (rawPhase < 0) rawPhase += 1.0;
                    int rawX = (int)(rawPhase * (width - 1));
                    int rawY = MapVelocityToY(s.VelocityMps, minVRaw, maxVRaw, height);
                    PlotPoint(pixels, width, height, rawX, rawY, PointColor);
                }

                tex.SetPixels(pixels);
                tex.Apply();
                return tex;
            }

            double minV, maxV;
            ComputeVelocityRangeFromBins(bins, out minV, out maxV);
            range = new RvPlotRange { MinVelocityMps = minV, MaxVelocityMps = maxV };
            DrawBaseline(pixels, width, height, minV, maxV, 0.0);

            foreach (var bin in bins)
            {
                if (bin.Count == 0) continue;
                int x = (int)(bin.PhaseCenter * (width - 1));
                DrawErrorBar(pixels, width, height, x, bin.MeanVelocityMps, bin.UncertaintyMps, minV, maxV);
            }
            foreach (var bin in bins)
            {
                if (bin.Count == 0) continue;
                int x = (int)(bin.PhaseCenter * (width - 1));
                int y = MapVelocityToY(bin.MeanVelocityMps, minV, maxV, height);
                PlotPoint(pixels, width, height, x, y, PointColor);
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private struct PhaseBin
        {
            public double PhaseCenter;
            public double MeanVelocityMps;
            public double UncertaintyMps;
            public int Count;
        }

        /// <summary>
        /// Bins raw samples by orbital phase and reduces each bin to a mean velocity plus
        /// the standard error of that mean: sigma_bin = sqrt(sum(sigma_i^2)) / n, formal
        /// propagation of each sample's own instrument precision rather than empirical
        /// scatter -- mirrors LightCurveTexture.BinByPhase.
        /// </summary>
        private static PhaseBin[] BinByPhase(List<RvSample> samples, double periodSeconds, int binCount)
        {
            var sumVelocity = new double[binCount];
            var sumVariance = new double[binCount];
            var count = new int[binCount];

            foreach (var s in samples)
            {
                double phase = (s.Ut / periodSeconds) % 1.0;
                if (phase < 0) phase += 1.0;
                int bin = (int)(phase * binCount);
                if (bin >= binCount) bin = binCount - 1;

                sumVelocity[bin] += s.VelocityMps;
                sumVariance[bin] += s.UncertaintyMps * s.UncertaintyMps;
                count[bin]++;
            }

            var result = new PhaseBin[binCount];
            for (int i = 0; i < binCount; i++)
            {
                if (count[i] < MinBinSampleCount) continue;
                result[i] = new PhaseBin
                {
                    PhaseCenter = (i + 0.5) / binCount,
                    MeanVelocityMps = sumVelocity[i] / count[i],
                    UncertaintyMps = Math.Sqrt(sumVariance[i]) / count[i],
                    Count = count[i]
                };
            }
            return result;
        }

        private static void ComputeVelocityRangeFromBins(PhaseBin[] bins, out double minV, out double maxV)
        {
            minV = double.MaxValue;
            maxV = double.MinValue;
            foreach (var bin in bins)
            {
                if (bin.Count == 0) continue;
                double lo = bin.MeanVelocityMps - bin.UncertaintyMps;
                double hi = bin.MeanVelocityMps + bin.UncertaintyMps;
                if (lo < minV) minV = lo;
                if (hi > maxV) maxV = hi;
            }

            if (minV > maxV)
            {
                minV = -1.0;
                maxV = 1.0;
                return;
            }

            double pad = Math.Max((maxV - minV) * 0.1, 0.01);
            minV -= pad;
            maxV += pad;
        }

        private static Color[] FillBackground(int width, int height)
        {
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = BackgroundColor;
            return pixels;
        }

        private static void ComputeVelocityRange(List<RvSample> samples, out double minV, out double maxV)
        {
            minV = double.MaxValue;
            maxV = double.MinValue;
            foreach (var s in samples)
            {
                double lo = s.VelocityMps - s.UncertaintyMps;
                double hi = s.VelocityMps + s.UncertaintyMps;
                if (lo < minV) minV = lo;
                if (hi > maxV) maxV = hi;
            }
            double pad = Math.Max((maxV - minV) * 0.1, 0.01);
            minV -= pad;
            maxV += pad;
        }

        private static void DrawBaseline(Color[] pixels, int width, int height, double minV, double maxV, double baselineV)
        {
            if (baselineV < minV || baselineV > maxV) return;
            int y = MapVelocityToY(baselineV, minV, maxV, height);
            for (int x = 0; x < width; x++)
            {
                SetPixelSafe(pixels, width, height, x, y, BaselineColor);
            }
        }

        /// <summary>Vertical whisker from (v - sigma) to (v + sigma), with short horizontal caps at each end.</summary>
        private static void DrawErrorBar(Color[] pixels, int width, int height, int x, double v, double sigma, double minV, double maxV)
        {
            if (sigma <= 0) return;
            int yLo = MapVelocityToY(v - sigma, minV, maxV, height);
            int yHi = MapVelocityToY(v + sigma, minV, maxV, height);
            if (yLo > yHi) { int t = yLo; yLo = yHi; yHi = t; }

            for (int y = yLo; y <= yHi; y++)
            {
                SetPixelSafe(pixels, width, height, x, y, ErrorBarColor);
            }
            for (int dx = -1; dx <= 1; dx++)
            {
                SetPixelSafe(pixels, width, height, x + dx, yLo, ErrorBarColor);
                SetPixelSafe(pixels, width, height, x + dx, yHi, ErrorBarColor);
            }
        }

        private static int MapVelocityToY(double velocity, double minV, double maxV, int height)
        {
            double range = Math.Max(maxV - minV, 1e-9);
            double t = (velocity - minV) / range; // 0 = low velocity, 1 = high velocity
            int y = (int)(t * (height - 1));
            return Math.Max(0, Math.Min(height - 1, y));
        }

        private static void PlotPoint(Color[] pixels, int width, int height, int x, int y, Color color)
        {
            SetPixelSafe(pixels, width, height, x, y, color);
            SetPixelSafe(pixels, width, height, x + 1, y, color);
            SetPixelSafe(pixels, width, height, x, y + 1, color);
        }

        private static void SetPixelSafe(Color[] pixels, int width, int height, int x, int y, Color color)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            pixels[y * width + x] = color;
        }
    }
}
