using System;
using System.Collections.Generic;
using UnityEngine;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>Flux range (already padded) a render call used, lets the GUI layer place axis tick labels that line up with the plotted data.</summary>
    public struct LightCurvePlotRange
    {
        public double MinFlux;
        public double MaxFlux;
    }

    /// <summary>
    /// Renders FluxSample data into Texture2D scatter plots for IMGUI display.
    /// Unity-dependent by design — kept separate from Core so Core stays
    /// engine-agnostic and testable.
    /// </summary>
    public static class LightCurveTexture
    {
        private static readonly Color BackgroundColor = new Color(0.08f, 0.08f, 0.1f, 1f);
        private static readonly Color PointColor = new Color(0.4f, 0.85f, 1f, 1f);
        private static readonly Color ErrorBarColor = new Color(0.4f, 0.85f, 1f, 0.45f);
        private static readonly Color BaselineColor = new Color(0.5f, 0.9f, 0.5f, 0.6f);

        /// <summary>Phase bins for the folded view — enough to resolve transit shape, few enough that error bars don't visually pile up (raw sample counts run into the hundreds+ per orbit).</summary>
        private const int PhaseFoldBinCount = 100;

        /// <summary>Bins under this count are dropped rather than drawn: their uncertainty barely shrinks from the raw per-sample value (matches TransitDetector's own nIn/nOut &gt;= 3 cutoff), so next to a well-populated neighbor bin they render as a lone full-scale error bar instead of useful signal.</summary>
        private const int MinBinSampleCount = 3;

        /// <summary>Raw flux vs. time (universal time, seconds), x-axis spans the full observation.</summary>
        public static Texture2D RenderRawLightCurve(List<FluxSample> samples, int width, int height, out LightCurvePlotRange range)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            var pixels = FillBackground(width, height);

            if (samples == null || samples.Count == 0)
            {
                tex.SetPixels(pixels);
                tex.Apply();
                range = new LightCurvePlotRange { MinFlux = 0.999, MaxFlux = 1.001 };
                return tex;
            }

            double minUt = samples[0].Ut;
            double maxUt = samples[samples.Count - 1].Ut;
            double utRange = Math.Max(maxUt - minUt, 1.0);

            double minFlux, maxFlux;
            ComputeFluxRange(samples, out minFlux, out maxFlux);
            range = new LightCurvePlotRange { MinFlux = minFlux, MaxFlux = maxFlux };
            DrawBaseline(pixels, width, height, minFlux, maxFlux, 1.0);

            foreach (var s in samples)
            {
                int x = (int)((s.Ut - minUt) / utRange * (width - 1));
                DrawErrorBar(pixels, width, height, x, s.Flux, s.UncertaintyFlux, minFlux, maxFlux);
            }
            foreach (var s in samples)
            {
                int x = (int)((s.Ut - minUt) / utRange * (width - 1));
                int y = MapFluxToY(s.Flux, minFlux, maxFlux, height);
                PlotPoint(pixels, width, height, x, y, PointColor);
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>Phase-folded flux vs. orbital phase (0-1), using a known period from detection.</summary>
        public static Texture2D RenderPhaseFoldedCurve(List<FluxSample> samples, double periodDays, int width, int height, out LightCurvePlotRange range)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            var pixels = FillBackground(width, height);

            if (samples == null || samples.Count == 0 || periodDays <= 0)
            {
                tex.SetPixels(pixels);
                tex.Apply();
                range = new LightCurvePlotRange { MinFlux = 0.999, MaxFlux = 1.001 };
                return tex;
            }

            double periodSeconds = periodDays * 86400.0;
            var bins = BinByPhase(samples, periodSeconds, PhaseFoldBinCount);

            double minFlux, maxFlux;
            ComputeFluxRangeFromBins(bins, out minFlux, out maxFlux);
            range = new LightCurvePlotRange { MinFlux = minFlux, MaxFlux = maxFlux };
            DrawBaseline(pixels, width, height, minFlux, maxFlux, 1.0);

            foreach (var bin in bins)
            {
                if (bin.Count == 0) continue;
                int x = (int)(bin.PhaseCenter * (width - 1));
                DrawErrorBar(pixels, width, height, x, bin.MeanFlux, bin.UncertaintyFlux, minFlux, maxFlux);
            }
            foreach (var bin in bins)
            {
                if (bin.Count == 0) continue;
                int x = (int)(bin.PhaseCenter * (width - 1));
                int y = MapFluxToY(bin.MeanFlux, minFlux, maxFlux, height);
                PlotPoint(pixels, width, height, x, y, PointColor);
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private struct PhaseBin
        {
            public double PhaseCenter;
            public double MeanFlux;
            public double UncertaintyFlux;
            public int Count;
        }

        /// <summary>
        /// Bins raw samples by orbital phase and reduces each bin to a mean flux plus the
        /// standard error of that mean: sigma_bin = sqrt(sum(sigma_i^2)) / n, i.e. formal
        /// propagation of each sample's own photometric uncertainty rather than empirical
        /// scatter — this is what shrinks the error bars as points accumulate per bin.
        /// </summary>
        private static PhaseBin[] BinByPhase(List<FluxSample> samples, double periodSeconds, int binCount)
        {
            var sumFlux = new double[binCount];
            var sumVariance = new double[binCount];
            var count = new int[binCount];

            foreach (var s in samples)
            {
                double phase = (s.Ut / periodSeconds) % 1.0;
                if (phase < 0) phase += 1.0;
                int bin = (int)(phase * binCount);
                if (bin >= binCount) bin = binCount - 1;

                sumFlux[bin] += s.Flux;
                sumVariance[bin] += s.UncertaintyFlux * s.UncertaintyFlux;
                count[bin]++;
            }

            var result = new PhaseBin[binCount];
            for (int i = 0; i < binCount; i++)
            {
                if (count[i] < MinBinSampleCount) continue;
                result[i] = new PhaseBin
                {
                    PhaseCenter = (i + 0.5) / binCount,
                    MeanFlux = sumFlux[i] / count[i],
                    UncertaintyFlux = Math.Sqrt(sumVariance[i]) / count[i],
                    Count = count[i]
                };
            }
            return result;
        }

        private static void ComputeFluxRangeFromBins(PhaseBin[] bins, out double minFlux, out double maxFlux)
        {
            minFlux = double.MaxValue;
            maxFlux = double.MinValue;
            foreach (var bin in bins)
            {
                if (bin.Count == 0) continue;
                double lo = bin.MeanFlux - bin.UncertaintyFlux;
                double hi = bin.MeanFlux + bin.UncertaintyFlux;
                if (lo < minFlux) minFlux = lo;
                if (hi > maxFlux) maxFlux = hi;
            }

            if (minFlux > maxFlux)
            {
                minFlux = 0.999;
                maxFlux = 1.001;
                return;
            }

            double pad = Math.Max((maxFlux - minFlux) * 0.1, 0.0001);
            minFlux -= pad;
            maxFlux += pad;
        }

        private static Color[] FillBackground(int width, int height)
        {
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = BackgroundColor;
            return pixels;
        }

        private static void ComputeFluxRange(List<FluxSample> samples, out double minFlux, out double maxFlux)
        {
            minFlux = double.MaxValue;
            maxFlux = double.MinValue;
            foreach (var s in samples)
            {
                double lo = s.Flux - s.UncertaintyFlux;
                double hi = s.Flux + s.UncertaintyFlux;
                if (lo < minFlux) minFlux = lo;
                if (hi > maxFlux) maxFlux = hi;
            }
            double pad = Math.Max((maxFlux - minFlux) * 0.1, 0.0001);
            minFlux -= pad;
            maxFlux += pad;
        }

        private static void DrawBaseline(Color[] pixels, int width, int height, double minFlux, double maxFlux, double baselineFlux)
        {
            if (baselineFlux < minFlux || baselineFlux > maxFlux) return;
            int y = MapFluxToY(baselineFlux, minFlux, maxFlux, height);
            for (int x = 0; x < width; x++)
            {
                SetPixelSafe(pixels, width, height, x, y, BaselineColor);
            }
        }

        /// <summary>Vertical whisker from (flux - sigma) to (flux + sigma), with short horizontal caps at each end.</summary>
        private static void DrawErrorBar(Color[] pixels, int width, int height, int x, double flux, double sigma, double minFlux, double maxFlux)
        {
            if (sigma <= 0) return;
            int yLo = MapFluxToY(flux - sigma, minFlux, maxFlux, height);
            int yHi = MapFluxToY(flux + sigma, minFlux, maxFlux, height);
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

        private static int MapFluxToY(double flux, double minFlux, double maxFlux, int height)
        {
            double range = Math.Max(maxFlux - minFlux, 1e-9);
            double t = (flux - minFlux) / range; // 0 = low flux, 1 = high flux
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
