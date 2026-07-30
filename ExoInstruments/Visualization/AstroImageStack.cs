using System;
using System.Collections.Generic;
using UnityEngine;
using ExoInstruments.Core;

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

        /// <summary>Variance-of-Laplacian sharpness score (see AstroImageStack.ComputeSharpness) -- the lucky-imaging selection metric for this sub.</summary>
        public float Quality;
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
        // Resolution-aware cap: a fixed ~30 subs/filter was fine at the old 480x480 abstract
        // frame (~330 MB worst case across 5 filters), but at real sensor resolution (up to the
        // native 4144x2822) that same constant would reach several GB and risk exhausting the
        // managed heap. Instead this targets a fixed total memory budget across all 5 filters,
        // divided evenly, so the effective cap shrinks automatically as resolution goes up.
        private const long TotalStackMemoryBudgetBytes = 1_000_000_000L; // ~1 GB across all filters
        private const int FilterCount = 5;
        private const int MinSubsPerFilter = 3;
        private const int MaxSubsPerFilterCeiling = 30; // never allow more than this even at low resolution/binning -- diminishing returns past this many subs

        public static int MaxSubsPerFilter
        {
            get
            {
                long bytesPerSub = (long)SolarSystemCameraTexture.TextureWidth * SolarSystemCameraTexture.TextureHeight * sizeof(float);
                long perFilterBudget = TotalStackMemoryBudgetBytes / FilterCount;
                int bySize = (int)Math.Max(MinSubsPerFilter, perFilterBudget / Math.Max(1, bytesPerSub));
                return Math.Min(MaxSubsPerFilterCeiling, bySize);
            }
        }

        private const float CentroidThreshold = 0.1f; // ignore background/noise pixels when locating the target
        private static int MaxAlignShiftPx => SolarSystemCameraTexture.TextureWidth / 2; // TextureWidth varies with the camera's binning factor
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

        // Lucky imaging (Fried 1978; practical use e.g. Baldwin et al. 2001): keep only the
        // sharpest fraction of subs, ranked by ComputeSharpness, before aligning and averaging.
        // 30% sits in the middle of the 1-60% range used in the literature depending on seeing.
        private const float LuckyKeepFraction = 0.3f;

        // Sharpness scoring window: the RC20 always centers its aim, so scoring only the
        // central fraction of the frame -- the actual target, not background stars or
        // corner vignetting/coma -- mirrors AutoStakkert!'s alignment-point "quality box"
        // used for real planetary lucky imaging.
        private const float SharpnessRegionFraction = 0.6f;

        // Trims the sharpest-magnitude 2% of Laplacian values before computing the
        // variance, the same robust-trimming idiom EstimateBackground already uses --
        // without it, a single cosmic-ray hit or hot pixel (a huge, isolated Laplacian
        // spike) would inflate the variance enough to make a contaminated frame look
        // like the sharpest one, exactly backwards from what the metric is for.
        private const float SharpnessTrimFraction = 0.02f;

        // At real sensor resolution the scoring region can hold millions of pixels; sorting all
        // of them for every captured sub would be the single slowest operation in the stacking
        // pipeline. Sharpness/focus is a spatially broad property (real focus operators are
        // routinely evaluated on a representative sub-sample rather than every pixel), so this
        // strides through the region to keep the sample count roughly constant regardless of
        // resolution -- statistically equivalent, orders of magnitude cheaper to sort.
        private const int SharpnessTargetSampleCount = 50_000;

        private readonly Dictionary<CameraFilter, List<AstroSub>> rawSubs = new Dictionary<CameraFilter, List<AstroSub>>();

        /// <summary>
        /// Adds a raw grayscale sub for the given filter. Rejects if MaxSubsPerFilter is
        /// reached or if fovDeg doesn't match the existing subs — mixing FOVs makes alignment
        /// meaningless. defectPixelIndices (SolarSystemCameraTexture.GetDefectPixelIndices) is
        /// cosmetically corrected out before the sub is stored -- see CosmeticCorrect.
        /// </summary>
        public AstroSubResult AddSub(CameraFilter filter, float[] gray, float fovDeg, float exposureSeconds, int[] defectPixelIndices)
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

            float[] corrected = CosmeticCorrect(gray, defectPixelIndices);
            list.Add(new AstroSub { Gray = corrected, FovDeg = fovDeg, ExposureSeconds = exposureSeconds, Quality = ComputeSharpness(corrected) });
            return AstroSubResult.Added;
        }

        /// <summary>
        /// Bad-pixel-map cosmetic correction: each known defect pixel (the sensor's fixed
        /// hot/dead map, known ahead of time the same way a real calibration dark-frame
        /// characterizes one) is replaced by the mean of its immediate orthogonal neighbors,
        /// skipping any neighbor that's itself a known defect. This is the standard
        /// professional calibration step real pipelines run before registration/stacking
        /// (PixInsight's CosmeticCorrection process, IRAF/ccdproc's fixpix, ESO Reflex
        /// bad-pixel handling) -- run here, once per sub, before alignment. Doing it before
        /// alignment matters: a fixed sensor defect co-added with per-sub alignment shifts
        /// would scatter into a cloud of artifacts at different composite positions instead
        /// of being corrected once at its one true location.
        /// </summary>
        private static float[] CosmeticCorrect(float[] gray, int[] defectPixelIndices)
        {
            if (defectPixelIndices == null || defectPixelIndices.Length == 0) return gray;

            int w = SolarSystemCameraTexture.TextureWidth, h = SolarSystemCameraTexture.TextureHeight;
            var isDefect = new HashSet<int>(defectPixelIndices);
            var corrected = (float[])gray.Clone();

            foreach (int idx in defectPixelIndices)
            {
                int x = idx % w, y = idx / w;
                float sum = 0f;
                int count = 0;
                if (x > 0 && !isDefect.Contains(idx - 1)) { sum += gray[idx - 1]; count++; }
                if (x < w - 1 && !isDefect.Contains(idx + 1)) { sum += gray[idx + 1]; count++; }
                if (y > 0 && !isDefect.Contains(idx - w)) { sum += gray[idx - w]; count++; }
                if (y < h - 1 && !isDefect.Contains(idx + w)) { sum += gray[idx + w]; count++; }
                corrected[idx] = count > 0 ? sum / count : gray[idx];
            }
            return corrected;
        }

        /// <summary>
        /// Variance-of-Laplacian focus/sharpness measure (Pech-Pacheco et al. 2000,
        /// "Diatom autofocusing in brightfield microscopy: a comparative study" -- the
        /// top-performing general-purpose sharpness operator in the Pertuz, Puig &amp; Garcia
        /// 2013 survey of focus measures). A sharp, well-resolved frame has strong local
        /// contrast everywhere (high Laplacian variance); a seeing-blurred one is smoothed
        /// out (low variance). Computed only over the central SharpnessRegionFraction of
        /// the frame (see its doc comment), with the SharpnessTrimFraction largest-magnitude
        /// values excluded before the variance is taken, so an isolated cosmic-ray hit or
        /// hot pixel can't be mistaken for genuine sharpness.
        /// </summary>
        private static float ComputeSharpness(float[] gray)
        {
            int w = SolarSystemCameraTexture.TextureWidth, h = SolarSystemCameraTexture.TextureHeight;
            int marginX = (int)(w * (1f - SharpnessRegionFraction) / 2f);
            int marginY = (int)(h * (1f - SharpnessRegionFraction) / 2f);
            int xLo = Mathf.Max(1, marginX), xHi = Mathf.Min(w - 2, w - 1 - marginX);
            int yLo = Mathf.Max(1, marginY), yHi = Mathf.Min(h - 2, h - 1 - marginY);
            if (xHi <= xLo || yHi <= yLo) return 0f;

            long regionPixels = (long)(xHi - xLo + 1) * (yHi - yLo + 1);
            int stride = Math.Max(1, (int)Math.Sqrt(regionPixels / (double)SharpnessTargetSampleCount));

            var laplacians = new List<float>(SharpnessTargetSampleCount + 64);
            for (int y = yLo; y <= yHi; y += stride)
            {
                int row = y * w;
                for (int x = xLo; x <= xHi; x += stride)
                {
                    int i = row + x;
                    float lap = -4f * gray[i] + gray[i - 1] + gray[i + 1] + gray[i - w] + gray[i + w];
                    laplacians.Add(lap);
                }
            }

            // Largest |Laplacian| first, so the outlier trim below drops exactly the
            // spikes a cosmic ray or hot pixel would produce.
            laplacians.Sort((a, b) => Math.Abs(b).CompareTo(Math.Abs(a)));
            int trim = Mathf.FloorToInt(laplacians.Count * SharpnessTrimFraction);
            int keepCount = laplacians.Count - trim;
            if (keepCount <= 1) return 0f;

            double sum = 0.0;
            for (int i = trim; i < laplacians.Count; i++) sum += laplacians[i];
            double mean = sum / keepCount;

            double sumSq = 0.0;
            for (int i = trim; i < laplacians.Count; i++)
            {
                double d = laplacians[i] - mean;
                sumSq += d * d;
            }
            return (float)(sumSq / keepCount);
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
        /// <summary>
        /// Stacks every filter that has subs and hands the set to ColourComposite, which is where the
        /// colour is decided. Replaces ComposeLRGB's channel-per-primary assignment -- see
        /// ColourComposite for why that was wrong and what it does instead.
        /// </summary>
        public Color[] Compose(ColourCompositeMode mode, bool align, bool lucky,
                               VisualTelescopeSpec spec, out string report)
        {
            report = null;
            if (!HasAnySubs)
            {
                report = "No subs captured yet -- capture at least one series first.";
                return null;
            }

            var channels = new Dictionary<CameraFilter, float[]>();
            foreach (CameraFilter f in Enum.GetValues(typeof(CameraFilter)))
            {
                float[] stacked = StackFilter(f, align, lucky);
                if (stacked != null) channels[f] = stacked;
            }

            return ColourComposite.Compose(mode, SolarSystemCameraTexture.TextureWidth,
                                           SolarSystemCameraTexture.TextureHeight,
                                           channels, spec, out report);
        }

        private float[] StackFilter(CameraFilter filter, bool align, bool lucky)
        {
            if (!rawSubs.TryGetValue(filter, out List<AstroSub> allSubs) || allSubs.Count == 0) return null;

            List<AstroSub> subs = allSubs;
            if (lucky && allSubs.Count > 1)
            {
                subs = new List<AstroSub>(allSubs);
                subs.Sort((a, b) => b.Quality.CompareTo(a.Quality)); // sharpest (highest peak) first
                int keep = Mathf.Max(1, Mathf.CeilToInt(allSubs.Count * LuckyKeepFraction));
                subs = subs.GetRange(0, keep);
            }
            align = align || lucky; // lucky imaging always registers the kept frames

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
