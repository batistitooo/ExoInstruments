using System;
using System.Collections.Generic;
using UnityEngine;

namespace ExoInstruments.Visualization
{
    /// <summary>Result of AstroImageStack.AddSub -- lets the caller give the player a clear reason when a sub was rejected instead of silently dropping it.</summary>
    public enum AstroSubResult
    {
        Added,
        FilterFull,
        FovMismatch,
    }

    /// <summary>One captured sub: its pixels plus the settings it was shot under, kept alongside the pixels so a later sub can be checked for compatibility (FOV) and so the stack can report real integration time.</summary>
    internal struct AstroSub
    {
        public float[] Gray;
        public float FovDeg;
        public float ExposureSeconds;
    }

    /// <summary>
    /// Holds raw monochrome subs captured per filter by the RC20 (see
    /// SolarSystemCameraTexture) and combines them into a stacked LRGB
    /// composite -- the real amateur-astrophotography workflow: many short
    /// subs per filter average down read/shot noise far better than one long
    /// exposure, and the Luminance channel (usually the sharpest/deepest
    /// stack) supplies detail while the R/G/B stacks only supply color.
    /// </summary>
    public class AstroImageStack
    {
        // 30 subs × 480×480 × 5 filters ≈ 330 MB in the worst case — capped per filter to stay reasonable.
        public const int MaxSubsPerFilter = 30;

        private const float CentroidThreshold = 0.1f; // ignore background/noise pixels when locating the target
        private const int MaxAlignShiftPx = SolarSystemCameraTexture.TextureWidth / 2;
        private const float FovMatchToleranceDeg = 0.01f; // subs of the same filter must share (near enough) the same FOV, or a pixel means a different angle in each and stacking would just blur the target

        // Strength of the display-only asinh stretch applied to the finished
        // composite (see AsinhStretch) -- higher lifts shadows more
        // aggressively. Only ever applied to the returned composite, never to
        // the stored raw subs.
        private const float StretchStrength = 5f;

        // Background estimated from a border band (target is near center, so border is real sky)
        // and subtracted before LRGB composition — prevents the three independent noisy R/G/B
        // stacks from blowing up as color confetti at near-zero background pixels.
        private const int BackgroundBorderPx = 20;
        private const float BackgroundTrimFraction = 0.15f; // trim brightest border pixels (planet limb or hot pixel) before median

        // Cap at 4× — real planetary color ratios sit close to 1, so this preserves the signal
        // while stopping leftover background noise from dividing into an unbounded color spike.
        private const float MaxLuminanceScale = 4f;

        private readonly Dictionary<CameraFilter, List<AstroSub>> rawSubs = new Dictionary<CameraFilter, List<AstroSub>>();

        /// <summary>Adds a raw grayscale sub for the given filter. Rejects if MaxSubsPerFilter is reached or if fovDeg doesn't match the existing subs — mixing FOVs makes alignment meaningless.</summary>
        public AstroSubResult AddSub(CameraFilter filter, float[] gray, float fovDeg, float exposureSeconds)
        {
            if (gray == null || gray.Length != SolarSystemCameraTexture.TextureWidth * SolarSystemCameraTexture.TextureHeight)
                return AstroSubResult.FovMismatch; // malformed input, treat like an incompatible sub rather than silently accepting it

            if (!rawSubs.TryGetValue(filter, out List<AstroSub> list))
            {
                list = new List<AstroSub>();
                rawSubs[filter] = list;
            }

            if (list.Count > 0 && Mathf.Abs(list[0].FovDeg - fovDeg) > FovMatchToleranceDeg)
                return AstroSubResult.FovMismatch;

            if (list.Count >= MaxSubsPerFilter) return AstroSubResult.FilterFull;

            list.Add(new AstroSub { Gray = gray, FovDeg = fovDeg, ExposureSeconds = exposureSeconds });
            return AstroSubResult.Added;
        }

        public int SubCount(CameraFilter filter) => rawSubs.TryGetValue(filter, out List<AstroSub> list) ? list.Count : 0;

        /// <summary>Total real exposure time stacked into this filter so far, in seconds -- the actual integration time a real stack of this many subs represents.</summary>
        public float TotalExposureSeconds(CameraFilter filter)
        {
            if (!rawSubs.TryGetValue(filter, out List<AstroSub> list)) return 0f;
            float total = 0f;
            foreach (AstroSub sub in list) total += sub.ExposureSeconds;
            return total;
        }

        /// <summary>True once at least one sub exists in any filter.</summary>
        public bool HasAnySubs
        {
            get
            {
                foreach (List<AstroSub> list in rawSubs.Values)
                {
                    if (list.Count > 0) return true;
                }
                return false;
            }
        }

        public void ClearAll() => rawSubs.Clear();

        /// <summary>
        /// Stacks each filter (with optional centroid alignment and sky-background subtraction),
        /// then composes LRGB via luminance transfer: R/G/B are scaled by (L stack / rgbLum),
        /// capped at MaxLuminanceScale. The background subtraction + cap keep dark-sky pixels
        /// neutral instead of blowing up as color noise. Halpha boosts the red channel when present.
        /// Missing RGB falls back to black (bi-color, not a crash). Asinh-stretched for display.
        /// Returns null with an error when nothing has been captured yet.
        /// </summary>
        public Color[] ComposeLRGB(bool align, float haBlendStrength, out string error)
        {
            if (!HasAnySubs)
            {
                error = "No subs captured yet -- capture at least one series first.";
                return null;
            }

            float[] stackedL = StackFilter(CameraFilter.Luminance, align);
            float[] stackedR = StackFilter(CameraFilter.Red, align);
            float[] stackedG = StackFilter(CameraFilter.Green, align);
            float[] stackedB = StackFilter(CameraFilter.Blue, align);
            float[] stackedHa = StackFilter(CameraFilter.HAlpha, align);

            int n = SolarSystemCameraTexture.TextureWidth * SolarSystemCameraTexture.TextureHeight;
            var result = new Color[n];
            for (int i = 0; i < n; i++)
            {
                float r = stackedR != null ? stackedR[i] : 0f;
                float g = stackedG != null ? stackedG[i] : 0f;
                float b = stackedB != null ? stackedB[i] : 0f;

                if (stackedL != null)
                {
                    const float epsilon = 1e-4f;
                    float rgbLum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    float scale = Mathf.Min(MaxLuminanceScale, Mathf.Max(0f, stackedL[i]) / Mathf.Max(rgbLum, epsilon));
                    r *= scale;
                    g *= scale;
                    b *= scale;
                }

                if (stackedHa != null && haBlendStrength > 0f)
                {
                    r += haBlendStrength * stackedHa[i];
                }

                result[i] = new Color(
                    AsinhStretch(Mathf.Max(0f, r)),
                    AsinhStretch(Mathf.Max(0f, g)),
                    AsinhStretch(Mathf.Max(0f, b)),
                    1f);
            }

            error = null;
            return result;
        }

        /// <summary>
        /// Normalized arcsinh stretch: 0 -> 0, 1 -> 1, monotonic, lifting
        /// shadows more than it compresses highlights -- the standard
        /// astrophotography "make the faint stacked detail visible" curve.
        /// Display-only; never applied to stored sub or stack data.
        /// </summary>
        private static float AsinhStretch(float v)
        {
            const float k = StretchStrength;
            float num = Mathf.Log(k * v + Mathf.Sqrt(k * v * k * v + 1f));
            float den = Mathf.Log(k + Mathf.Sqrt(k * k + 1f));
            return den > 1e-6f ? Mathf.Clamp01(num / den) : Mathf.Clamp01(v);
        }

        /// <summary>Aligns (if requested) and averages all subs for one filter, then subtracts the sky background. Null if the filter has no subs.</summary>
        private float[] StackFilter(CameraFilter filter, bool align)
        {
            if (!rawSubs.TryGetValue(filter, out List<AstroSub> subs) || subs.Count == 0) return null;

            int n = SolarSystemCameraTexture.TextureWidth * SolarSystemCameraTexture.TextureHeight;
            var sum = new float[n];

            if (!align || subs.Count == 1)
            {
                foreach (AstroSub sub in subs)
                {
                    for (int i = 0; i < n; i++) sum[i] += sub.Gray[i];
                }
            }
            else
            {
                (double refX, double refY) = ComputeCentroid(subs[0].Gray);
                for (int s = 0; s < subs.Count; s++)
                {
                    float[] gray = subs[s].Gray;
                    if (s == 0)
                    {
                        for (int i = 0; i < n; i++) sum[i] += gray[i];
                        continue;
                    }

                    (double cx, double cy) = ComputeCentroid(gray);
                    int dx = Mathf.Clamp((int)Math.Round(refX - cx), -MaxAlignShiftPx, MaxAlignShiftPx);
                    int dy = Mathf.Clamp((int)Math.Round(refY - cy), -MaxAlignShiftPx, MaxAlignShiftPx);
                    float[] shifted = (dx == 0 && dy == 0) ? gray : ShiftImage(gray, dx, dy);
                    for (int i = 0; i < n; i++) sum[i] += shifted[i];
                }
            }

            float inv = 1f / subs.Count;
            for (int i = 0; i < n; i++) sum[i] *= inv;

            // Subtract background once on the averaged stack, not per-sub — avoids uneven amplification from noisy individual estimates.
            float background = EstimateBackground(sum);
            for (int i = 0; i < n; i++) sum[i] = Mathf.Max(0f, sum[i] - background);

            return sum;
        }

        /// <summary>Robust sky background from the image border: sorts samples, trims the brightest BackgroundTrimFraction (limb/hot pixel), returns the median of what's left.</summary>
        private static float EstimateBackground(float[] gray)
        {
            int w = SolarSystemCameraTexture.TextureWidth, h = SolarSystemCameraTexture.TextureHeight;
            var samples = new List<float>((w + h) * BackgroundBorderPx * 2);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                if (y < BackgroundBorderPx || y >= h - BackgroundBorderPx)
                {
                    for (int x = 0; x < w; x++) samples.Add(gray[row + x]);
                }
                else
                {
                    for (int x = 0; x < BackgroundBorderPx; x++) samples.Add(gray[row + x]);
                    for (int x = w - BackgroundBorderPx; x < w; x++) samples.Add(gray[row + x]);
                }
            }
            if (samples.Count == 0) return 0f;

            samples.Sort();
            int keep = Mathf.Max(1, Mathf.RoundToInt(samples.Count * (1f - BackgroundTrimFraction)));
            return samples[keep / 2];
        }

        /// <summary>Brightness-weighted centroid in pixel coordinates, ignoring anything below CentroidThreshold. Falls back to the frame center when nothing exceeds it (target too faint/absent to detect -- avoids a divide-by-zero and just skips alignment for that sub).</summary>
        private static (double cx, double cy) ComputeCentroid(float[] gray)
        {
            int w = SolarSystemCameraTexture.TextureWidth, h = SolarSystemCameraTexture.TextureHeight;
            double sumW = 0, sumX = 0, sumY = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float v = gray[row + x];
                    if (v <= CentroidThreshold) continue;
                    sumW += v;
                    sumX += v * x;
                    sumY += v * y;
                }
            }
            if (sumW < 1e-6) return (w / 2.0, h / 2.0);
            return (sumX / sumW, sumY / sumW);
        }

        /// <summary>Integer pixel shift, zero-filling pixels that shift in from outside the frame.</summary>
        private static float[] ShiftImage(float[] src, int dx, int dy)
        {
            int w = SolarSystemCameraTexture.TextureWidth, h = SolarSystemCameraTexture.TextureHeight;
            var dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                int sy = y - dy;
                if (sy < 0 || sy >= h) continue;
                int dstRow = y * w;
                int srcRow = sy * w;
                for (int x = 0; x < w; x++)
                {
                    int sx = x - dx;
                    if (sx < 0 || sx >= w) continue;
                    dst[dstRow + x] = src[srcRow + sx];
                }
            }
            return dst;
        }
    }
}
