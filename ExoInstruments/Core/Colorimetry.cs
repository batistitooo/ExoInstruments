using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Real colorimetry: a spectrum in, a displayable colour out, through the CIE 1931 standard
    /// observer and the sRGB standard.
    ///
    /// WHY THIS REPLACES A CURVE FIT. The colour of a source is not a free choice, and it is not a
    /// function of temperature that can be fitted directly to RGB. It is the projection of the
    /// source's spectrum onto the three colour matching functions of a standard observer, which is a
    /// measurement of human vision (CIE 1931, still the basis of every colour standard), followed by
    /// a transform into whatever primaries the display has. Both steps are defined exactly. The
    /// piecewise power law this file replaces was a fit to somebody's plot of that chain, valid over
    /// a limited temperature range and applicable to nothing but blackbodies, which excludes every
    /// emission-line source in the mod, since a nebula's colour comes from three narrow lines and has
    /// no temperature at all.
    ///
    /// THE CHAIN:
    ///
    ///   X = Int S(lambda) xbar(lambda) dlambda,  and likewise Y and Z
    ///   (R,G,B)_linear = M_sRGB . (X,Y,Z)                       IEC 61966-2-1, D65
    ///   (R,G,B)_display = transfer((R,G,B)_linear)               the sRGB transfer function
    ///
    /// Y is luminance by construction: ybar IS the CIE luminous efficiency function V(lambda).
    ///
    /// GAMUT. Astronomical colours routinely fall outside what a display can show; a pure emission
    /// line is a monochromatic stimulus, which lies ON the spectral locus and so outside every real
    /// set of primaries. Clipping the negative components would shift the hue; instead the colour is
    /// desaturated toward the white point, along the line joining it in chromaticity space, by the
    /// smallest amount that brings it inside. That preserves hue and luminance and loses only
    /// saturation, which is the one attribute the display genuinely cannot reproduce, and it is the
    /// standard treatment.
    ///
    /// The table this uses is generated rather than typed; see tools/generate_cie_table.py, and
    /// tools/colour-tests compares the whole chain against the colour-science package.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class Colorimetry
    {
        // Physical constants, SI defining values (the 2019 redefinition, exact).
        //
        // NOTE ON c2. CIE 15:2004 recommends c2 = hc/k = 1.4388e-2 m K for COLORIMETRIC use, a
        // legacy value carried over from the 1968 temperature scale so that quoted colour
        // temperatures stay consistent with the historical literature. The SI defining constants
        // give 1.438776877e-2, which differs by 1.55e-5 relative. This file uses the SI values,
        // because the same Planck function is integrated elsewhere in the mod for photometry and
        // two different Planck constants in one codebase would be worse than either. The
        // consequence is quantified rather than assumed: it shifts a blackbody's chromaticity by
        // 3.6e-6, which is a ten-thousandth of one 8-bit display level, and tools/colour-tests
        // measures it against colour-science, which follows the CIE convention.
        private const double PlanckH = 6.62607015e-34;
        private const double SpeedOfLight = 2.99792458e8;
        private const double BoltzmannK = 1.380649e-23;

        /// <summary>Shortest and longest wavelength the standard observer is defined at, nanometres.</summary>
        public const double MinWavelengthNm = CieColourMatchingTable.MinWavelengthNm;
        public const double MaxWavelengthNm = CieColourMatchingTable.MaxWavelengthNm;

        /// <summary>
        /// The three colour matching functions at a wavelength, CUBICALLY interpolated between the
        /// table's one-nanometre entries. Zero outside the visible range, which is what "invisible"
        /// means: an H-band photon has no colour.
        ///
        /// Cubic rather than linear because CIE 167:2005 recommends an interpolating polynomial of
        /// third degree or higher for uniformly spaced spectral data, and because it matters here:
        /// the emission lines this mod renders sit at fractional wavelengths (H-alpha at 656.28 nm,
        /// [O III] at 500.68), so their colour comes from an interpolated value rather than a
        /// tabulated one. Linear interpolation of these curves at 1 nm is off by up to 8.4e-4 of
        /// peak; a Catmull-Rom cubic through the same four points reduces that by two orders of
        /// magnitude for four extra multiplies.
        /// </summary>
        public static void ColourMatchingFunctions(double wavelengthNm,
                                                   out double xBar, out double yBar, out double zBar)
        {
            xBar = yBar = zBar = 0.0;
            if (wavelengthNm < MinWavelengthNm || wavelengthNm > MaxWavelengthNm) return;

            double pos = (wavelengthNm - MinWavelengthNm) / CieColourMatchingTable.StepNm;
            int i = (int)pos;
            int last = CieColourMatchingTable.XBar.Length - 1;
            if (i >= last) { i = last; pos = last; }
            double f = pos - i;

            xBar = CubicAt(CieColourMatchingTable.XBar, i, f);
            yBar = CubicAt(CieColourMatchingTable.YBar, i, f);
            zBar = CubicAt(CieColourMatchingTable.ZBar, i, f);
        }

        // Catmull-Rom cubic through the four samples around index i, at fraction f between i and i+1. Clamped
        // at the table's ends, where the curves are already at the 1e-4 level.
        private static double CubicAt(double[] table, int i, double f)
        {
            if (f <= 0.0) return table[i];
            int last = table.Length - 1;
            double p0 = table[Math.Max(0, i - 1)];
            double p1 = table[i];
            double p2 = table[Math.Min(last, i + 1)];
            double p3 = table[Math.Min(last, i + 2)];
            double f2 = f * f, f3 = f2 * f;
            return 0.5 * (2.0 * p1
                        + (-p0 + p2) * f
                        + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * f2
                        + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * f3);
        }

        /// <summary>
        /// Tristimulus values of a continuous spectrum, integrated over the standard observer's own
        /// range at its own one-nanometre spacing.
        ///
        /// The integral is a plain sum over the table's grid rather than an adaptive quadrature: the
        /// colour matching functions ARE that table, so nothing exists between its entries to
        /// resolve, and any finer rule would only be interpolating them.
        /// </summary>
        /// <param name="spectralRadiancePerNm">Any quantity proportional to power per nanometre. The scale carries through to Y.</param>
        public static void SpectrumToXyz(Func<double, double> spectralRadiancePerNm,
                                         out double x, out double y, out double z)
        {
            x = y = z = 0.0;
            if (spectralRadiancePerNm == null) return;

            int n = CieColourMatchingTable.XBar.Length;
            for (int i = 0; i < n; i++)
            {
                double lambda = MinWavelengthNm + i * CieColourMatchingTable.StepNm;
                double s = spectralRadiancePerNm(lambda);
                if (!(s > 0.0)) continue;
                x += s * CieColourMatchingTable.XBar[i];
                y += s * CieColourMatchingTable.YBar[i];
                z += s * CieColourMatchingTable.ZBar[i];
            }
            double step = CieColourMatchingTable.StepNm;
            x *= step; y *= step; z *= step;
        }

        /// <summary>
        /// Tristimulus values of a single emission line of the given total power. A line is a delta
        /// function in wavelength, so its contribution is the power times the colour matching
        /// functions AT the line, no width and no integral.
        /// </summary>
        public static void LineToXyz(double wavelengthNm, double power,
                                     out double x, out double y, out double z)
        {
            ColourMatchingFunctions(wavelengthNm, out double xb, out double yb, out double zb);
            x = power * xb;
            y = power * yb;
            z = power * zb;
        }

        /// <summary>Planck's law, spectral radiance per unit wavelength, for a wavelength in nanometres.</summary>
        public static double PlanckSpectralRadiance(double wavelengthNm, double temperatureK)
        {
            if (!(temperatureK > 0.0) || !(wavelengthNm > 0.0)) return 0.0;
            double lambda = wavelengthNm * 1e-9;
            double exponent = PlanckH * SpeedOfLight / (lambda * BoltzmannK * temperatureK);
            if (exponent > 700.0) return 0.0;                 // exp overflows below this anyway
            double denominator = Math.Exp(exponent) - 1.0;
            if (!(denominator > 0.0)) return 0.0;
            return 2.0 * PlanckH * SpeedOfLight * SpeedOfLight / Math.Pow(lambda, 5.0) / denominator;
        }

        /// <summary>Chromaticity of a blackbody, which is the Planckian locus. Normalised to unit luminance so only the colour is returned.</summary>
        public static void BlackbodyXyz(double temperatureK, out double x, out double y, out double z)
        {
            SpectrumToXyz(l => PlanckSpectralRadiance(l, temperatureK), out x, out y, out z);
            if (y > 0.0) { x /= y; z /= y; y = 1.0; }
        }

        /// <summary>Linear sRGB from tristimulus values. Components may be negative or above one: see MapIntoGamut.</summary>
        public static void XyzToLinearSrgb(double x, double y, double z,
                                           out double r, out double g, out double b)
        {
            double[,] m = CieColourMatchingTable.XyzToLinearSrgb;
            r = m[0, 0] * x + m[0, 1] * y + m[0, 2] * z;
            g = m[1, 0] * x + m[1, 1] * y + m[1, 2] * z;
            b = m[2, 0] * x + m[2, 1] * y + m[2, 2] * z;
        }

        /// <summary>
        /// Brings a linear sRGB triple inside the display's gamut by desaturating it toward the white
        /// point, which is the only operation that preserves both hue and LUMINANCE.
        ///
        /// TWO WAYS OUT OF THE GAMUT, and both have to be handled. A colour can need more of a
        /// primary than exists (a negative component), which is what a monochromatic line does. It
        /// can equally need MORE of a primary than the display can emit at that luminance: a
        /// saturated red at Y = 0.3 asks for a linear R of 1.4, because a saturated colour puts far
        /// more than its luminance into one channel. Handling only the first and letting the second
        /// clip is what shifts a bright H-alpha nebula's hue: its red channel pins at 1 while green
        /// and blue stay where they were, so the colour drifts as the exposure lengthens.
        ///
        /// The white point in linear sRGB is (1,1,1), so desaturating by a fraction t is a lerp
        /// toward the triple's own luminance, and the smallest t that satisfies every component is
        /// solved directly rather than searched.
        ///
        /// LUMINANCE ABOVE ONE cannot be fixed this way and is not meant to be: a source brighter
        /// than display white clips, which is what an over-exposed frame does. The caller scales
        /// luminance into range first; that is the stretch's job, not the gamut's.
        ///
        /// Returns the saturation given up, 0 meaning the colour was already displayable.
        /// </summary>
        public static double MapIntoGamut(ref double r, ref double g, ref double b)
            => MapIntoGamut(ref r, ref g, ref b, true);

        /// <summary>
        /// As above, with the option to fix only the NEGATIVE side.
        ///
        /// A caller after a source's TINT (the star chart's markers, say) has no meaningful
        /// luminance yet: it will normalise the triple by its own peak afterwards. For it the upper
        /// constraint is not merely unnecessary but wrong, since a saturated colour normalised to
        /// unit luminance always has a component above one, and honouring that constraint would
        /// desaturate every saturated tint to white.
        /// </summary>
        public static double MapIntoGamut(ref double r, ref double g, ref double b, bool limitHighlights)
        {
            double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (!(luminance > 0.0)) { r = g = b = 0.0; return 0.0; }

            double t = 0.0;
            t = Math.Max(t, NeededDesaturation(r, luminance, limitHighlights));
            t = Math.Max(t, NeededDesaturation(g, luminance, limitHighlights));
            t = Math.Max(t, NeededDesaturation(b, luminance, limitHighlights));
            if (t <= 0.0) return 0.0;

            t = Math.Min(1.0, t);
            r = r + t * (luminance - r);
            g = g + t * (luminance - g);
            b = b + t * (luminance - b);
            return t;
        }

        // How far toward the luminance a component has to move to land inside [0, 1]. Below zero it has to rise
        // to zero; above one it has to fall to one. Both are the same lerp, and the second is only solvable
        // while the luminance itself is at most one; above that the colour is simply brighter than the display
        // and clips.
        private static double NeededDesaturation(double component, double luminance, bool limitHighlights = true)
        {
            if (component < 0.0)
            {
                double denominator = luminance - component;
                return denominator > 0.0 ? -component / denominator : 1.0;
            }
            if (limitHighlights && component > 1.0)
            {
                double denominator = component - luminance;
                if (!(denominator > 0.0)) return 1.0;
                if (luminance >= 1.0) return 1.0;
                return (component - 1.0) / denominator;
            }
            return 0.0;
        }

        /// <summary>
        /// The sRGB transfer function (IEC 61966-2-1): a linear segment near black joined to a
        /// 1/2.4 power law. NOT a plain gamma of 2.2, which is the usual shortcut; the standard's
        /// own piecewise form is what a display implements.
        /// </summary>
        public static double LinearToSrgbTransfer(double linear)
        {
            if (linear <= 0.0) return 0.0;
            if (linear >= 1.0) return 1.0;
            return linear <= 0.0031308
                ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        }

        /// <summary>Its inverse, for reading a display value back into linear light.</summary>
        public static double SrgbTransferToLinear(double encoded)
        {
            if (encoded <= 0.0) return 0.0;
            if (encoded >= 1.0) return 1.0;
            return encoded <= 0.04045
                ? encoded / 12.92
                : Math.Pow((encoded + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// The whole chain for a colour whose luminance is already scaled to the display's range:
        /// tristimulus in, gamma-encoded sRGB out, gamut-mapped on the way.
        /// </summary>
        public static void XyzToDisplaySrgb(double x, double y, double z,
                                            out double r, out double g, out double b)
        {
            XyzToLinearSrgb(x, y, z, out r, out g, out b);
            MapIntoGamut(ref r, ref g, ref b);
            r = LinearToSrgbTransfer(r);
            g = LinearToSrgbTransfer(g);
            b = LinearToSrgbTransfer(b);
        }

        /// <summary>
        /// Display tint of a blackbody: its real chromaticity, normalised so the brightest component
        /// is one. Replaces the piecewise fit in StellarColor with the same chain everything else
        /// uses, so a star and a nebula are coloured by one definition rather than two.
        /// </summary>
        public static void BlackbodyDisplayRgb(double temperatureK,
                                               out double r, out double g, out double b)
        {
            BlackbodyXyz(temperatureK, out double x, out double y, out double z);
            XyzToLinearSrgb(x, y, z, out r, out g, out b);
            // Negatives only: the peak normalisation below is what puts this inside the display's
            // range, so applying the upper constraint first would whiten every saturated tint.
            MapIntoGamut(ref r, ref g, ref b, false);

            double peak = Math.Max(r, Math.Max(g, b));
            if (peak > 0.0) { r /= peak; g /= peak; b /= peak; }
            r = LinearToSrgbTransfer(Math.Max(0.0, r));
            g = LinearToSrgbTransfer(Math.Max(0.0, g));
            b = LinearToSrgbTransfer(Math.Max(0.0, b));
        }

        /// <summary>CIE xy chromaticity, the coordinates the Planckian locus and the spectral locus are drawn in.</summary>
        public static void XyzToChromaticity(double x, double y, double z, out double cx, out double cy)
        {
            double sum = x + y + z;
            if (!(sum > 0.0)) { cx = CieColourMatchingTable.D65x; cy = CieColourMatchingTable.D65y; return; }
            cx = x / sum;
            cy = y / sum;
        }
    }
}
