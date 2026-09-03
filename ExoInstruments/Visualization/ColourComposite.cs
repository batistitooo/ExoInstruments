using System;
using System.Collections.Generic;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>How a set of stacked monochrome frames becomes one colour image.</summary>
    public enum ColourCompositeMode
    {
        /// <summary>
        /// TRUE COLOUR: what the sky would look like to the eye, through the instrument. The band
        /// measurements go through the instrument's own fitted transform into CIE tristimulus values
        /// and then into sRGB, so the colour is a measurement rather than a channel assignment.
        /// </summary>
        TrueColour,

        /// <summary>
        /// H-alpha to red, [O III] to green and blue. The bicolour convention every narrowband
        /// imager uses, and a CONVENTION; there is no sense in which [O III] is cyan.
        /// </summary>
        NarrowbandHoo,

        /// <summary>
        /// [S II] to red, H-alpha to green, [O III] to blue: the Hubble palette, ordered by
        /// wavelength rather than by appearance. Also a convention, and the reason those images look
        /// gold and teal rather than red.
        /// </summary>
        NarrowbandSho,
    }

    /// <summary>
    /// Turns stacked per-filter frames into a colour image, colorimetrically where the data allows
    /// it and by a stated convention where it does not.
    ///
    /// WHAT WAS WRONG BEFORE. The previous composite fed the red filter's electron count into the
    /// display's red primary, and so on, then blended in some fraction of the H-alpha frame with a
    /// strength the player chose. Three separate problems: a red filter is not the sRGB red primary,
    /// so the colours depended on the filter set rather than on the sky; the H-alpha blend was an
    /// artist's knob with no physical meaning; and each channel was stretched independently, which
    /// desaturates everything bright, because the stretch compresses the biggest channel hardest.
    ///
    /// WHAT IT DOES INSTEAD.
    ///
    ///   * Colour comes from ColourCalibration, a transform from the instrument's real filter
    ///     response curves to CIE tristimulus values, fitted over blackbodies and nebular line
    ///     spectra. Same construction as a camera's colour matrix, and with a measured residual.
    ///   * The STRETCH IS APPLIED TO LUMINANCE ALONE, and the chromaticity is carried through it
    ///     untouched. This is the step that makes an astronomical colour image look like one: a
    ///     nebula's core stays the colour it measured instead of washing to white as the exposure
    ///     lengthens, because scaling all three tristimulus values by one factor moves brightness
    ///     without moving hue or saturation.
    ///   * The luminance range comes from the frame itself, by the same extended-source zscale the
    ///     single-frame display uses (Core.ZScale).
    ///   * A LUMINANCE frame, when there is one, supplies that luminance, because it is the deepest
    ///     channel; it collects the whole passband. The colour still comes from the colour
    ///     channels. Separating the two is standard LRGB practice and it is why observers spend
    ///     most of their integration on L.
    ///   * Out-of-gamut colours are desaturated toward the white point rather than clipped, so hue
    ///     survives (Core.Colorimetry).
    ///
    /// The narrowband palettes are labelled conventions and are not run through the colorimetry at
    /// all: assigning [O III] to blue is a choice about presentation, and dressing it up as a
    /// colour measurement would be a lie about what the frame contains.
    /// </summary>
    public static class ColourComposite
    {
        // Cached per-instrument fit: it costs a few thousand bandpass integrals and depends only on the filter
        // set.
        private static ColourCalibration cachedCalibration;
        private static VisualTelescopeSpec cachedSpec;

        /// <summary>The instrument's band-to-tristimulus transform, fitted once per filter set.</summary>
        public static ColourCalibration CalibrationFor(VisualTelescopeSpec spec, out string report)
        {
            if (cachedCalibration != null && cachedSpec == spec)
            {
                report = Describe(cachedCalibration);
                return cachedCalibration;
            }

            // AN INSTRUMENT WHOSE BANDS ARE NOT VISIBLE LIGHT CANNOT HAVE A COLORIMETRIC TRANSFORM,
            // and refusing to fit one is the whole of the difference between a false-colour image
            // labelled as such and a false claim of true colour.
            //
            // The CIE 1931 observer is defined over 360-830 nm and is identically zero outside it,
            // so a least-squares fit of bands lying beyond 830 nm is a fit to a matrix of zeros: it
            // returns something, and that something means nothing. WFC3/IR is the instrument this
            // is written for - its shortest filter pivots at 986 nm, a full 156 nm past the red end
            // of human vision - but the test is on the wavelengths rather than on the instrument,
            // so any future infrared or ultraviolet band is caught by the same line.
            //
            // Returning null puts the composite onto the labelled-palette path, which makes no
            // colorimetric claim. That is what a real WFC3/IR colour image is.
            foreach (CameraFilter f in new[] { CameraFilter.Red, CameraFilter.Green, CameraFilter.Blue })
            {
                double centreNm = SolarSystemCameraTexture.CentralWavelengthNmOf(f);
                if (centreNm < CieColourMatchingTable.MinWavelengthNm
                    || centreNm > CieColourMatchingTable.MaxWavelengthNm)
                {
                    report = "no colorimetric transform: this instrument's "
                           + f + " band is centred at " + centreNm.ToString("F0")
                           + " nm, outside the CIE 1931 observer's 360-830 nm support. "
                           + "Composites are labelled false colour.";
                    return null;
                }
            }

            var bands = new List<SystemResponse>();
            foreach (CameraFilter f in new[] { CameraFilter.Red, CameraFilter.Green, CameraFilter.Blue })
            {
                if (Array.IndexOf(spec.AvailableFilters, f) < 0) { report = null; return null; }
                SystemResponse response = SolarSystemCameraTexture.SystemResponseForColour(f);
                if (response == null) { report = null; return null; }
                bands.Add(response);
            }

            ColourCalibration fit = ColourCalibration.Fit(bands);
            cachedCalibration = fit;
            cachedSpec = spec;
            report = Describe(fit);
            return fit;
        }

        private static string Describe(ColourCalibration c)
            => c == null ? null
             : $"colour transform fitted over {c.TrainingSpectra} spectra: "
             + $"{c.RmsResidual * 100.0:F1}% rms in tristimulus, "
             + $"worst chromaticity error {c.WorstChromaticityError:F4} in CIE xy";

        /// <summary>
        /// Builds the colour image. Channels absent from the stack are simply zero, which is what an
        /// unfilled channel means; the caller reports which were present.
        /// </summary>
        /// <param name="channels">Stacked frames per filter, in electrons. Missing entries may be null.</param>
        public static UnityEngine.Color[] Compose(
            ColourCompositeMode mode, int width, int height,
            IDictionary<CameraFilter, float[]> channels,
            VisualTelescopeSpec spec,
            out string report)
        {
            report = null;
            if (channels == null || width <= 0 || height <= 0) return null;
            int n = width * height;

            switch (mode)
            {
                case ColourCompositeMode.NarrowbandHoo:
                    return ComposePalette(width, height, channels, out report,
                        new[] { CameraFilter.HAlpha },
                        new[] { CameraFilter.OIII },
                        new[] { CameraFilter.OIII },
                        "bicolour HOO (H-alpha to red, [O III] to green and blue), a presentation "
                        + "convention, not a colour measurement");

                case ColourCompositeMode.NarrowbandSho:
                    return ComposePalette(width, height, channels, out report,
                        new[] { CameraFilter.SII },
                        new[] { CameraFilter.HAlpha },
                        new[] { CameraFilter.OIII },
                        "Hubble palette SHO ([S II] to red, H-alpha to green, [O III] to blue, "
                        + "ordered by wavelength), a presentation convention, not a colour measurement");

                default:
                    return ComposeTrueColour(width, height, channels, spec, out report);
            }
        }

        private static UnityEngine.Color[] ComposeTrueColour(
            int width, int height, IDictionary<CameraFilter, float[]> channels,
            VisualTelescopeSpec spec, out string report)
        {
            report = null;
            ColourCalibration calibration = CalibrationFor(spec, out string fitReport);
            if (calibration == null)
            {
                report = "This instrument has no red, green and blue filters, so it cannot make a "
                       + "true-colour image. Use a narrowband palette, or a single filter.";
                return null;
            }

            float[] r = Get(channels, CameraFilter.Red);
            float[] g = Get(channels, CameraFilter.Green);
            float[] b = Get(channels, CameraFilter.Blue);
            float[] lum = Get(channels, CameraFilter.Luminance);
            if (r == null || g == null || b == null)
            {
                report = "True colour needs a sub in each of Red, Green and Blue.";
                return null;
            }

            int n = width * height;
            // Tristimulus values first, for the whole frame, so the luminance the stretch works on
            // is the real photometric luminance rather than one channel standing in for it.
            var xs = new float[n];
            var ys = new float[n];
            var zs = new float[n];
            var bands = new double[3];
            for (int i = 0; i < n; i++)
            {
                bands[0] = r[i]; bands[1] = g[i]; bands[2] = b[i];
                calibration.ToXyz(bands, out double x, out double y, out double z);
                xs[i] = (float)x; ys[i] = (float)y; zs[i] = (float)z;
            }

            // The luminance channel replaces Y where there is one: it is the deepest frame, having
            // collected the whole passband, so it carries the faint structure the colour channels
            // cannot. Scaled to Y's own median so the two are on one footing rather than one being
            // brighter for having a wider band.
            float[] luminance = ys;
            if (lum != null)
            {
                double scale = MedianRatio(lum, ys);
                if (scale > 0.0)
                {
                    luminance = new float[n];
                    for (int i = 0; i < n; i++) luminance[i] = (float)(lum[i] * scale);
                }
            }

            if (!ZScale.TryExtendedSourceLimits(luminance, width, height,
                                                out double black, out double white)
                || !(white > black))
            {
                black = 0.0;
                white = Max(luminance);
                if (!(white > black)) { report = "The stack is empty."; return null; }
            }

            var result = new UnityEngine.Color[n];
            double invRange = 1.0 / (white - black);
            for (int i = 0; i < n; i++)
            {
                double y = luminance[i];
                double stretched = AsinhStretch((y - black) * invRange);
                if (!(stretched > 0.0)) { result[i] = new UnityEngine.Color(0f, 0f, 0f, 1f); continue; }

                // Chromaticity from the tristimulus values, luminance from the stretch. Scaling all
                // three by one factor is exactly what leaves hue and saturation alone.
                double y0 = ys[i];
                double k = y0 > 1e-12 ? stretched / y0 : 0.0;
                Colorimetry.XyzToDisplaySrgb(xs[i] * k, ys[i] * k, zs[i] * k,
                                             out double dr, out double dg, out double db);
                result[i] = new UnityEngine.Color((float)dr, (float)dg, (float)db, 1f);
            }

            report = "True colour: " + fitReport
                   + (lum != null ? "; luminance from the L channel" : "; luminance from R, G and B")
                   + $"; showing {black:E2} to {white:E2} electrons";
            return result;
        }

        // A palette: each display channel is the sum of one or more narrowband frames, each stretched on its
        // own limits. No colorimetry, because there is none to do; see the enum.
        private static UnityEngine.Color[] ComposePalette(
            int width, int height, IDictionary<CameraFilter, float[]> channels, out string report,
            CameraFilter[] toRed, CameraFilter[] toGreen, CameraFilter[] toBlue, string description)
        {
            int n = width * height;
            var present = new List<string>();
            var missing = new List<string>();

            float[] rc = Combine(channels, toRed, width, height, present, missing);
            float[] gc = Combine(channels, toGreen, width, height, present, missing);
            float[] bc = Combine(channels, toBlue, width, height, present, missing);
            if (rc == null && gc == null && bc == null)
            {
                report = "None of the filters this palette needs has a sub in the stack.";
                return null;
            }

            var result = new UnityEngine.Color[n];
            for (int i = 0; i < n; i++)
                result[i] = new UnityEngine.Color(
                    rc != null ? rc[i] : 0f, gc != null ? gc[i] : 0f, bc != null ? bc[i] : 0f, 1f);

            report = description
                   + (missing.Count > 0
                        ? "; EMPTY channels: " + string.Join(", ", missing.ToArray())
                          + " (no sub in the stack, or no such survey exists; see the README)"
                        : "");
            return result;
        }

        // Sums the named filters and stretches the result on its own zscale limits, or null when none is
        // present.
        private static float[] Combine(IDictionary<CameraFilter, float[]> channels, CameraFilter[] filters,
                                       int width, int height, List<string> present, List<string> missing)
        {
            float[] sum = null;
            int n = width * height;
            foreach (CameraFilter f in filters)
            {
                float[] frame = Get(channels, f);
                if (frame == null)
                {
                    if (!missing.Contains(f.ToString())) missing.Add(f.ToString());
                    continue;
                }
                if (!present.Contains(f.ToString())) present.Add(f.ToString());
                if (sum == null) { sum = new float[n]; }
                for (int i = 0; i < n; i++) sum[i] += frame[i];
            }
            if (sum == null) return null;

            if (!ZScale.TryExtendedSourceLimits(sum, width, height, out double black, out double white)
                || !(white > black))
            {
                black = 0.0;
                white = Max(sum);
                if (!(white > black)) return sum;
            }
            double invRange = 1.0 / (white - black);
            var outFrame = new float[n];
            for (int i = 0; i < n; i++)
            {
                double v = AsinhStretch((sum[i] - black) * invRange);
                // Palette channels go to the display directly, so they carry the sRGB transfer
                // function like any other displayed value.
                outFrame[i] = (float)Colorimetry.LinearToSrgbTransfer(v);
            }
            return outFrame;
        }

        private static float[] Get(IDictionary<CameraFilter, float[]> channels, CameraFilter f)
            => channels.TryGetValue(f, out float[] frame) && frame != null ? frame : null;

        // Normalised asinh stretch, the standard astronomical curve (Lupton et al. 2004): linear at the noise,
        // logarithmic above it, so faint structure lifts without the bright end saturating. Applied to
        // LUMINANCE only in the true-colour path.
        private static double AsinhStretch(double v)
        {
            if (!(v > 0.0)) return 0.0;
            if (v >= 1.0) return 1.0;
            const double softening = 0.02;
            double num = Math.Log(v / softening + Math.Sqrt(v * v / (softening * softening) + 1.0));
            double den = Math.Log(1.0 / softening + Math.Sqrt(1.0 / (softening * softening) + 1.0));
            return den > 0.0 ? num / den : v;
        }

        // Ratio of medians, which puts two frames on one footing without either one's outliers deciding it.
        private static double MedianRatio(float[] a, float[] b)
        {
            double ma = Median(a), mb = Median(b);
            return ma > 0.0 && mb > 0.0 ? mb / ma : 0.0;
        }

        private static double Median(float[] frame)
        {
            if (frame == null || frame.Length == 0) return 0.0;
            int stride = Math.Max(1, frame.Length / 4096);
            var sample = new List<float>();
            for (int i = 0; i < frame.Length; i += stride) sample.Add(frame[i]);
            sample.Sort();
            return sample[sample.Count / 2];
        }

        private static double Max(float[] frame)
        {
            double m = 0.0;
            if (frame == null) return 0.0;
            for (int i = 0; i < frame.Length; i++) if (frame[i] > m) m = frame[i];
            return m;
        }
    }
}
