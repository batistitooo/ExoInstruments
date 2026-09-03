using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// IEEE 754 binary16, decoded.
    ///
    /// WHY THE MAPS USE IT. A sky map's dynamic range is not something a fixed-point scale can
    /// span. SFD98's reddening runs from 0.00037 magnitudes at the Galactic poles to 135 in the
    /// inner plane, a ratio of 3.6e5, against the 6.5e4 levels a 16-bit fixed-point value offers:
    /// any scale fine enough for the poles saturates in the plane, and any scale coarse enough for
    /// the plane quantises the poles to nothing. Choosing one silently discards the other, and the
    /// first version of the packer did exactly that; it marked 48615 pixels "no value", all of
    /// them at |b| below a degree.
    ///
    /// A half float has 11 bits of mantissa, so its precision is RELATIVE: 4.9e-4 of the value
    /// everywhere. That is 3e-5 magnitudes at the median sight line and 0.07 at the worst one in
    /// the sky, both far below SFD's own 16% calibration uncertainty, in the same two bytes.
    ///
    /// .NET Framework 4.7.2 has no System.Half, hence this. The layout is the standard one, so
    /// numpy's float16 writes exactly what this reads; the harness checks that against numpy.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class Float16
    {
        /// <summary>Largest finite value a binary16 can hold, 65504.</summary>
        public const double MaxValue = 65504.0;

        /// <summary>
        /// The reverse: a double as the nearest IEEE 754 binary16, with round-half-to-even, which is
        /// the rounding the format's own arithmetic uses and what numpy's float16 cast performs.
        ///
        /// Needed because a packed map is stored in this format and a reader that repairs a cell
        /// has to put the repair back in it. Written out rather than delegated because .NET
        /// Framework 4.7.2, which this mod targets, has no Half type. Exercised against numpy over
        /// the whole representable range in tools/dustmap-tests.
        /// </summary>
        public static ushort FromDouble(double value)
        {
            if (double.IsNaN(value)) return 0x7E00;
            if (double.IsPositiveInfinity(value)) return 0x7C00;
            if (double.IsNegativeInfinity(value)) return 0xFC00;

            ushort sign = (ushort)(value < 0.0 || (value == 0.0 && 1.0 / value < 0.0) ? 0x8000 : 0);
            double a = Math.Abs(value);
            if (a == 0.0) return sign;

            if (a >= 65520.0) return (ushort)(sign | 0x7C00);       // rounds to infinity

            // Subnormals: the exponent is pinned and the mantissa carries the whole value.
            if (a < 6.103515625e-05)
            {
                double scaled = a / 5.960464477539063e-08;           // the smallest subnormal
                int m = (int)Math.Round(scaled, MidpointRounding.ToEven);
                if (m > 0x3FF) return (ushort)(sign | 0x0400);       // rounded up into the normals
                return (ushort)(sign | (ushort)m);
            }

            int exponent = (int)Math.Floor(Math.Log(a, 2.0));
            if (exponent < -14) exponent = -14;
            if (exponent > 15) exponent = 15;
            double mantissa = a / Math.Pow(2.0, exponent) - 1.0;
            int frac = (int)Math.Round(mantissa * 1024.0, MidpointRounding.ToEven);
            if (frac > 0x3FF) { frac = 0; exponent++; }
            if (frac < 0) { frac = 0; }
            if (exponent > 15) return (ushort)(sign | 0x7C00);
            return (ushort)(sign | (ushort)((exponent + 15) << 10) | (ushort)frac);
        }

        // The thirty-two scale factors a binary16 exponent field can select, 2^-15 through 2^16. A TABLE RATHER
        // THAN Math.Pow, and the two are the same number rather than nearly the same: every entry is a power of
        // two, which a double represents EXACTLY, and Math.Pow(2, n) for an integer n in this range returns
        // that same exact value. Nothing is rounded here that was not rounded before. It matters because this
        // decoder is the innermost operation of every map read. The interpolation stencil that reads the
        // H-alpha composite is sixteen taps wide and a capture takes one sample per native pixel, so an RC20
        // frame decodes 187 million half floats; at one Math.Pow each that was measured at 2.4 of the 5.5
        // seconds the emission fill took, for an exponentiation whose answer is one of thirty-two constants.
        // Written out as literals so the values are visible and cannot drift with whatever evaluates them:
        // 2^-15 through 2^16, the biased exponent read straight off the field.
        private static readonly double[] ExponentScale =
        {
            //  e = 0 is the subnormal case and shares 2^-14 with e = 1; the rest are 2^(e-15).
            3.0517578125e-05,          // 2^-15, unused (subnormals take the entry below)
            6.103515625e-05,           // 2^-14
            1.220703125e-04,           // 2^-13
            2.44140625e-04,            // 2^-12
            4.8828125e-04,             // 2^-11
            9.765625e-04,              // 2^-10
            1.953125e-03,              // 2^-9
            3.90625e-03,               // 2^-8
            7.8125e-03,                // 2^-7
            1.5625e-02,                // 2^-6
            3.125e-02,                 // 2^-5
            6.25e-02,                  // 2^-4
            1.25e-01,                  // 2^-3
            2.5e-01,                   // 2^-2
            5.0e-01,                   // 2^-1
            1.0,                       // 2^0
            2.0,                       // 2^1
            4.0,                       // 2^2
            8.0,                       // 2^3
            1.6e+01,                   // 2^4
            3.2e+01,                   // 2^5
            6.4e+01,                   // 2^6
            1.28e+02,                  // 2^7
            2.56e+02,                  // 2^8
            5.12e+02,                  // 2^9
            1.024e+03,                 // 2^10
            2.048e+03,                 // 2^11
            4.096e+03,                 // 2^12
            8.192e+03,                 // 2^13
            1.6384e+04,                // 2^14
            3.2768e+04,                // 2^15
            6.5536e+04,                // 2^16, the infinity/NaN slot, never scaled by
        };

        // 2^-14, the fixed exponent of the subnormals.
        private const double SubnormalScale = 6.103515625e-05;

        /// <summary>
        /// Decodes one binary16. Returns NaN for the NaN encodings and infinity for the infinite
        /// ones, which is what a map reader wants: a pixel with no measurement stays a pixel with
        /// no measurement rather than becoming a number.
        /// </summary>
        public static double ToDouble(ushort bits)
        {
            int exponent = (bits >> 10) & 0x1F;
            int mantissa = bits & 0x3FF;
            double s = (bits & 0x8000) == 0 ? 1.0 : -1.0;

            if (exponent == 0)
            {
                // Zero, or subnormal: no implicit leading one, fixed exponent of -14.
                if (mantissa == 0) return s * 0.0;
                return s * (mantissa / 1024.0) * SubnormalScale;
            }

            if (exponent == 0x1F)
                return mantissa == 0 ? s * double.PositiveInfinity : double.NaN;

            return s * (1.0 + mantissa / 1024.0) * ExponentScale[exponent];
        }
    }
}
