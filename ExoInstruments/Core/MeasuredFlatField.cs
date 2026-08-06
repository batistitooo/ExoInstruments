using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Turns a flat frame the observer actually took into the per-pixel response map the detector
    /// chain multiplies by, replacing the parametric model rather than adding to it.
    ///
    /// WHY A MEASURED FLAT IS NOT JUST A BETTER MODEL, but a different kind of thing. The
    /// parametric map (Core.SensorNonUniformity, Core.FocalPlaneIllumination) can only ever produce
    /// the structure it was told about: a white photo-response spread and a cosine-fourth
    /// illumination falloff. What a real flat contains is mostly neither.
    ///
    ///   * Concentric TREE RINGS from radial dopant variations laid down as the silicon ingot grew,
    ///     and the periodic BRICK-WALL imprint of laser annealing. Luo et al. (2024, AJ 168, 251)
    ///     measure both on one device; neither is published for any detector on this roster, and
    ///     borrowing another device's rings would put a specific, visible, wrong structure into
    ///     every frame.
    ///   * DUST MOTES: out-of-focus shadows of dust on a filter or window, the most recognisable
    ///     feature of a real amateur flat.
    ///   * ACCESSORY VIGNETTING from undersized filters, a narrow drawtube or an off-axis guider,
    ///     which is what produces the deep corners in most real flats.
    ///
    /// None of those three is a property of the INSTRUMENT. They are properties of one optical
    /// surface on one night, which is why no specification publishes them and why no model can
    /// legitimately invent them. The observer's own file is the only source that has them, and it
    /// is the source of record for their own data. That is the whole argument, and it is why this
    /// is the one gap in the detector chain that closes without a citation.
    ///
    /// THE PEDESTAL HAS TO GO FIRST, and getting this wrong is the classic way to ruin a flat. A
    /// raw flat carries the readout's zero offset on top of the light, so the ratio between two
    /// pixels' RAW counts is not the ratio of their responses: it is that ratio pulled toward one
    /// by however large the pedestal is. At a 3000 ADU pedestal on a 20000 ADU flat, a pixel that
    /// really responds 10 % low reads only 8.7 % low. The bias is therefore subtracted before
    /// anything is normalised, and it is a required input rather than an optional one, because
    /// there is no way to detect from the file alone whether it has already been removed.
    ///
    /// NORMALISED BY THE MEAN, not by the maximum. The quantity the chain wants is "fraction of the
    /// array's mean response", which is what makes the flat divide out of a frame without changing
    /// its overall level; normalising by the peak would rescale every calibrated frame by the
    /// reciprocal of the brightest pixel's response, which is a hot pixel as often as not.
    ///
    /// WHAT IS REFUSED RATHER THAN ACCEPTED QUIETLY. A flat with saturated pixels is not a flat
    /// over those pixels: the response there is unmeasured, not unity. A flat whose dimensions do
    /// not match the frame is refused rather than resampled, because resampling a flat blurs
    /// exactly the fine structure that is the reason for loading one. A flat that is mostly at or
    /// below the pedestal was not exposed. Each refusal names the number that failed.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class MeasuredFlatField
    {
        /// <summary>The response map, plus what was measured about the file it came from.</summary>
        public sealed class Result
        {
            /// <summary>Per-pixel response as a fraction of the array mean: 1.0 is an average pixel.</summary>
            public double[] Response;
            public int Width;
            public int Height;

            /// <summary>Mean level of the bias-subtracted flat, in ADU. The signal the response was measured at.</summary>
            public double MeanLevelAdu;

            /// <summary>Standard deviation of the response over the whole array, as a fraction. Dominated by vignetting when there is any.</summary>
            public double ResponseSigma;

            /// <summary>
            /// The HIGH-FREQUENCY part of that scatter, estimated from differences between
            /// horizontally adjacent pixels and divided by sqrt(2).
            ///
            /// This is the statistic the noise diagnostic uses, and the whole-array sigma is not,
            /// because the two answer different questions. Vignetting and tree rings are smooth on
            /// the scale of a pixel and contribute almost nothing to an adjacent-pair difference,
            /// while shot noise is uncorrelated and contributes all of it. A flat with 20 % corner
            /// vignetting and no noise has a large ResponseSigma and a tiny one of these.
            /// </summary>
            public double HighFrequencySigma;

            /// <summary>Full range of the response across the array, which vignetting dominates when there is any.</summary>
            public double MinResponse;
            public double MaxResponse;

            /// <summary>
            /// The shot-noise floor this flat's own level implies, as a fraction, given the
            /// conversion factor: 1/sqrt(N electrons). A single sub has ResponseSigma at or near
            /// this floor, which means its map is mostly a photograph of its own noise; a master
            /// flat combined from many subs sits well below it. See NoiseWarning.
            /// </summary>
            public double ShotNoiseFloor;

            /// <summary>
            /// Set when the flat's high-frequency scatter is consistent with being its own shot
            /// noise rather than the detector's response, which means most of what would be baked
            /// into every frame as fixed pattern is this one file's randomness.
            ///
            /// The test is HighFrequencySigma >= 0.7 x ShotNoiseFloor. A single sub cannot scatter
            /// below its own shot-noise floor, so a flat that does must have been combined from
            /// several, which is exactly what a master flat is. Not an error: it is the caller's
            /// data and the caller's call.
            /// </summary>
            public bool NoiseWarning;

            public string Summary;
        }

        /// <summary>Thrown when the file cannot serve as a flat, with the measured reason.</summary>
        public sealed class UnusableException : Exception
        {
            public UnusableException(string message) : base(message) { }
        }

        /// <summary>
        /// Builds the response map from a loaded image.
        /// </summary>
        /// <param name="image">The flat frame, as read by Core.FitsImageReader.</param>
        /// <param name="expectedWidth">Frame width the map has to match; 0 to skip the check.</param>
        /// <param name="expectedHeight">Frame height the map has to match; 0 to skip the check.</param>
        /// <param name="biasLevelAdu">Readout pedestal to remove. Required; see the class remarks.</param>
        /// <param name="saturationAdu">Level at or above which a pixel is saturated and its response unmeasured.</param>
        /// <param name="electronsPerAdu">Conversion factor, for the shot-noise floor. NaN to skip that diagnostic.</param>
        public static Result Build(
            FitsImageReader.Image image,
            int expectedWidth,
            int expectedHeight,
            double biasLevelAdu,
            double saturationAdu,
            double electronsPerAdu)
        {
            if (image == null) throw new UnusableException("No image was read.");

            if (expectedWidth > 0 && expectedHeight > 0
                && (image.Width != expectedWidth || image.Height != expectedHeight))
            {
                throw new UnusableException(
                    "The flat is " + image.Width + "x" + image.Height + " and the frame is "
                    + expectedWidth + "x" + expectedHeight + ". A flat is not resampled onto a"
                    + " different grid: interpolation would blur exactly the fine structure that is"
                    + " the reason for loading a measured flat at all. Take the flat at the capture"
                    + " resolution and binning in use.");
            }

            int n = image.PixelCount;
            var levels = new double[n];

            int saturated = 0, undefined = 0, nonPositive = 0;
            double sum = 0.0;
            int counted = 0;

            for (int i = 0; i < n; i++)
            {
                double raw = image.Values[i];

                if (double.IsNaN(raw)) { undefined++; levels[i] = double.NaN; continue; }

                if (!double.IsNaN(saturationAdu) && raw >= saturationAdu)
                {
                    // A saturated pixel's response is not high, it is UNMEASURED: the well clipped
                    // before the response could express itself. Counted and reported, never used.
                    saturated++;
                    levels[i] = double.NaN;
                    continue;
                }

                double level = raw - biasLevelAdu;
                if (!(level > 0.0)) { nonPositive++; levels[i] = double.NaN; continue; }

                levels[i] = level;
                sum += level;
                counted++;
            }

            if (counted == 0)
                throw new UnusableException(
                    "No usable pixel in the flat: " + saturated + " saturated, " + nonPositive
                    + " at or below the " + biasLevelAdu.ToString("F1") + " ADU pedestal, "
                    + undefined + " undefined. Either the frame was not exposed, or the bias level"
                    + " given is wrong.");

            double usableFraction = (double)counted / n;
            if (usableFraction < 0.5)
                throw new UnusableException(
                    "Only " + (100.0 * usableFraction).ToString("F1") + " % of the flat is usable ("
                    + saturated + " saturated, " + nonPositive + " at or below the pedestal, "
                    + undefined + " undefined). A flat has to measure the response over the array,"
                    + " not over half of it.");

            double mean = sum / counted;

            // Normalised by the mean of the USABLE pixels, and the unusable ones are set to 1.0:
            // an unmeasured response is best left neutral, which changes nothing, rather than left
            // at zero, which would erase the pixel, or extrapolated, which would invent it.
            var response = new double[n];
            double min = double.MaxValue, max = double.MinValue;
            double sumSq = 0.0;

            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(levels[i])) { response[i] = 1.0; continue; }

                double r = levels[i] / mean;
                response[i] = r;
                if (r < min) min = r;
                if (r > max) max = r;
                double d = r - 1.0;
                sumSq += d * d;
            }

            double sigma = Math.Sqrt(sumSq / counted);

            // The high-frequency part, from horizontally adjacent pairs. Vignetting, tree rings and
            // a dust mote's penumbra are all smooth across one pixel and drop out; uncorrelated
            // shot noise does not. Dividing by sqrt(2) undoes the variance doubling a difference of
            // two independent samples introduces, so the result is comparable with the floor below.
            double diffSq = 0.0;
            int pairs = 0;
            for (int y = 0; y < image.Height; y++)
            {
                int row = y * image.Width;
                for (int x = 1; x < image.Width; x++)
                {
                    if (double.IsNaN(levels[row + x]) || double.IsNaN(levels[row + x - 1])) continue;
                    double d = response[row + x] - response[row + x - 1];
                    diffSq += d * d;
                    pairs++;
                }
            }
            double highFrequency = pairs > 0 ? Math.Sqrt(diffSq / pairs / 2.0) : 0.0;

            // A single flat sub carries its own shot noise, and normalising it turns that noise
            // into a FIXED pattern applied to every frame thereafter. The floor is 1/sqrt(N) in
            // electrons; a master flat stacked from k subs sits at the floor divided by sqrt(k),
            // and nothing can sit below its own floor without having been combined.
            double floor = double.NaN;
            bool warn = false;
            if (!double.IsNaN(electronsPerAdu) && electronsPerAdu > 0.0)
            {
                double electrons = mean * electronsPerAdu;
                if (electrons > 0.0)
                {
                    floor = 1.0 / Math.Sqrt(electrons);
                    warn = highFrequency >= 0.7 * floor;
                }
            }

            string summary =
                "flat " + image.Width + "x" + image.Height + ", BITPIX " + image.BitPix
                + ", mean " + mean.ToString("F1") + " ADU above a " + biasLevelAdu.ToString("F1")
                + " ADU pedestal; response " + min.ToString("F4") + " to " + max.ToString("F4")
                + ", sigma " + (100.0 * sigma).ToString("F3") + " % overall and "
                + (100.0 * highFrequency).ToString("F3") + " % high-frequency"
                + (double.IsNaN(floor) ? "" : " against a " + (100.0 * floor).ToString("F3") + " % shot-noise floor")
                + "; " + saturated + " saturated, " + nonPositive + " below pedestal, "
                + undefined + " undefined, all held at unity";

            return new Result
            {
                Response = response,
                Width = image.Width,
                Height = image.Height,
                MeanLevelAdu = mean,
                ResponseSigma = sigma,
                HighFrequencySigma = highFrequency,
                MinResponse = min,
                MaxResponse = max,
                ShotNoiseFloor = floor,
                NoiseWarning = warn,
                Summary = summary,
            };
        }
    }
}
