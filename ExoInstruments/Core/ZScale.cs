using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The zscale algorithm (Tody 1986, "The IRAF Data Reduction and Analysis System", SPIE 627,
    /// 733), which picks the black and white points a frame should be displayed between.
    ///
    /// WHY A TRANSFER CURVE IS NOT ENOUGH. A log or asinh curve decides how the range between black
    /// and white is distributed. It does not decide where black and white ARE, and on an
    /// astronomical frame that is the larger question by far: an exposure of a faint nebula spans
    /// perhaps twenty counts of a sixteen-thousand-count converter, sitting on a pedestal of sky
    /// and bias. Mapping the converter's full range to the display puts the entire subject inside
    /// the bottom few percent of it, and no curve applied afterwards can recover contrast that was
    /// never allocated. That is the difference between a grey fog and a nebula, and it is why every
    /// real display tool -- DS9, IRAF, Siril, PixInsight -- sets its limits from the data.
    ///
    /// HOW IT WORKS, and why it is not just a percentile clip. The samples are SORTED and a line is
    /// fitted to them against their rank, with iterative rejection. On an astronomical frame most
    /// pixels are sky, so the middle of that sorted array is a long shallow stretch whose slope
    /// measures the noise; the sources are the steep tail at the top, and the rejection throws them
    /// out of the fit. Extrapolating the sky's own slope across the full pixel count, divided by a
    /// contrast factor, gives limits set by the noise the frame actually has rather than by its
    /// extremes -- so one saturated star cannot flatten the whole image, which is exactly what a
    /// max-based or high-percentile clip does.
    ///
    /// This is a faithful transcription of the IRAF algorithm, the same one astropy's
    /// ZScaleInterval implements, and tools/zscale-tests compares the two on real frames.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class ZScale
    {
        /// <summary>Samples drawn from the frame. IRAF's own default; more does not move the answer, because the fit is to the sorted distribution rather than to individual pixels.</summary>
        public const int DefaultSamples = 1000;

        /// <summary>Contrast. The fitted slope is divided by it, so below 1 it stretches the limits in around the median. 0.25 is IRAF's and DS9's default.</summary>
        public const double DefaultContrast = 0.25;

        /// <summary>Rejection threshold in sigma about the fitted line.</summary>
        private const double KRej = 2.5;
        private const int MaxIterations = 5;
        /// <summary>Fraction of the samples that may be rejected before the fit is abandoned for the plain minimum and maximum.</summary>
        private const double MaxReject = 0.5;
        private const int MinPixels = 5;

        /// <summary>
        /// Black and white points for a frame. Returns false and the plain extremes when the frame
        /// carries too little structure for the fit to mean anything -- a flat field, or one whose
        /// samples are nearly all identical.
        /// </summary>
        public static bool TryLimits(float[] image, out double blackPoint, out double whitePoint,
                                     int sampleCount = DefaultSamples, double contrast = DefaultContrast)
        {
            blackPoint = 0.0;
            whitePoint = 1.0;
            if (image == null || image.Length == 0) return false;

            // Strided sampling, as IRAF does it: a regular stride across the whole frame rather
            // than a random draw, so the answer is reproducible and covers the field evenly.
            int stride = Math.Max(1, image.Length / Math.Max(1, sampleCount));
            int count = 0;
            for (int i = 0; i < image.Length && count < sampleCount; i += stride) count++;
            if (count < MinPixels) return false;

            var samples = new double[count];
            int k = 0;
            for (int i = 0; i < image.Length && k < count; i += stride) samples[k++] = image[i];
            Array.Sort(samples);

            int npix = samples.Length;
            double zmin = samples[0], zmax = samples[npix - 1];
            if (!(zmax > zmin)) { blackPoint = zmin; whitePoint = zmax; return false; }

            int minpix = Math.Max(MinPixels, (int)(npix * MaxReject));
            int ngrow = Math.Max(1, (int)(npix * 0.01));

            var bad = new bool[npix];
            int ngood = npix, lastNgood = npix + 1;
            double slope = 0.0, intercept = 0.0;

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                if (ngood >= lastNgood || ngood < minpix) break;

                if (!FitLine(samples, bad, out slope, out intercept)) break;

                // Residuals about the fit, and the rejection threshold from their own spread.
                double sum = 0.0, sumSq = 0.0;
                int used = 0;
                for (int i = 0; i < npix; i++)
                {
                    if (bad[i]) continue;
                    double residual = samples[i] - (intercept + slope * i);
                    sum += residual;
                    sumSq += residual * residual;
                    used++;
                }
                if (used == 0) break;
                double mean = sum / used;
                double sigma = Math.Sqrt(Math.Max(0.0, sumSq / used - mean * mean));
                double threshold = KRej * sigma;

                var rejected = new bool[npix];
                for (int i = 0; i < npix; i++)
                {
                    double residual = samples[i] - (intercept + slope * i);
                    rejected[i] = bad[i] || residual < -threshold || residual > threshold;
                }

                // Grow the rejected regions, which is what stops a single outlier's neighbours from
                // dragging the next fit back toward it.
                for (int i = 0; i < npix; i++)
                {
                    if (!rejected[i]) continue;
                    int lo = Math.Max(0, i - ngrow / 2), hi = Math.Min(npix - 1, i + ngrow / 2);
                    for (int j = lo; j <= hi; j++) bad[j] = true;
                }

                lastNgood = ngood;
                ngood = 0;
                for (int i = 0; i < npix; i++) if (!bad[i]) ngood++;
            }

            if (ngood < minpix)
            {
                blackPoint = zmin;
                whitePoint = zmax;
                return true;
            }

            double useSlope = contrast > 0.0 ? slope / contrast : slope;
            int centre = (npix - 1) / 2;
            double median = npix % 2 == 1
                ? samples[centre]
                : 0.5 * (samples[centre] + samples[centre + 1]);

            blackPoint = Math.Max(zmin, median - (centre - 1) * useSlope);
            whitePoint = Math.Min(zmax, median + (npix - centre) * useSlope);
            if (!(whitePoint > blackPoint)) { blackPoint = zmin; whitePoint = zmax; }
            return true;
        }

        /// <summary>
        /// Black and white points for a frame whose subject is EXTENDED, which is the case zscale
        /// alone gets wrong.
        ///
        /// zscale finds the sky beautifully and sets the white point from the sky's own noise, on
        /// the assumption that sources are a small minority of pixels. A nebula filling a third of
        /// the frame breaks that assumption outright: on a 40 s exposure of M42 the emission spans
        /// 34 to 5116 rayleighs while zscale's limits stop at 329, so an eighth of the frame clips
        /// to flat white and the nebula becomes a featureless polygon -- the shape of an iso-contour
        /// rather than of a nebula.
        ///
        /// The two halves of the question have different answers, so they are answered separately.
        /// The BLACK point still comes from zscale, because finding the sky is exactly what it is
        /// good at. The WHITE point comes from a high percentile of a BLOCK-AVERAGED copy of the
        /// frame: averaging over a block dilutes anything that covers a few pixels by the block's
        /// area while leaving anything extended untouched, so the white point ends up set by the
        /// brightest extended structure and not by a star. That is the right answer on physical
        /// grounds -- a stretch exists to show structure, and a point source has none -- and it is
        /// also what every real astrophotograph does, clipping its stars to white.
        /// </summary>
        public static bool TryExtendedSourceLimits(float[] image, int width, int height,
                                                   out double blackPoint, out double whitePoint)
        {
            blackPoint = 0.0;
            whitePoint = 1.0;
            if (image == null || image.Length == 0 || width <= 0 || height <= 0) return false;
            if (image.Length != width * height) return false;

            if (!TryLimits(image, out double zBlack, out double zWhite)) return false;
            blackPoint = zBlack;
            whitePoint = zWhite;

            // Block MEDIAN, not mean. A mean still carries a saturated star, merely divided by the
            // block's area, and on a star field that is enough to set the white point 7 times too
            // high and compress the sky again. A median over 64 pixels is untouched by anything
            // covering fewer than 32 of them, so a star vanishes completely while a nebula, which
            // fills the block, is unchanged. The block size is the compromise: large enough that
            // the seeing disk is a small part of it at every plate scale in this roster, small
            // enough to preserve the finest extended structure worth stretching for.
            const int block = 8;
            int bw = Math.Max(1, width / block), bh = Math.Max(1, height / block);
            var averaged = new double[bw * bh];
            var cell = new double[block * block];
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int count = 0;
                    for (int y = by * block; y < Math.Min(height, (by + 1) * block); y++)
                        for (int x = bx * block; x < Math.Min(width, (bx + 1) * block); x++)
                            cell[count++] = image[y * width + x];
                    if (count == 0) { averaged[by * bw + bx] = 0.0; continue; }
                    Array.Sort(cell, 0, count);
                    averaged[by * bw + bx] = count % 2 == 1
                        ? cell[count / 2]
                        : 0.5 * (cell[count / 2 - 1] + cell[count / 2]);
                }
            }

            Array.Sort(averaged);
            int index = (int)(ExtendedWhitePercentile * (averaged.Length - 1));
            double extendedWhite = averaged[Math.Max(0, Math.Min(averaged.Length - 1, index))];

            // Never darker than zscale's own white point: on a frame with no extended source at all
            // the block average is just the sky, and taking it would leave nothing above black.
            if (extendedWhite > whitePoint) whitePoint = extendedWhite;
            return whitePoint > blackPoint;
        }

        /// <summary>Percentile of the block-averaged frame that becomes white. 99.5% lets the very brightest extended structure clip, which is what a real exposure of a nebula core does.</summary>
        private const double ExtendedWhitePercentile = 0.995;

        /// <summary>Ordinary least squares of value against sample rank, over the samples not yet rejected.</summary>
        private static bool FitLine(double[] samples, bool[] bad, out double slope, out double intercept)
        {
            slope = 0.0;
            intercept = 0.0;
            double n = 0.0, sx = 0.0, sy = 0.0, sxx = 0.0, sxy = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                if (bad[i]) continue;
                double x = i, y = samples[i];
                n++; sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            if (n < 2.0) return false;
            double denominator = n * sxx - sx * sx;
            if (Math.Abs(denominator) < 1e-30) return false;
            slope = (n * sxy - sx * sy) / denominator;
            intercept = (sy - slope * sx) / n;
            return true;
        }
    }
}
