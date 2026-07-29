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
