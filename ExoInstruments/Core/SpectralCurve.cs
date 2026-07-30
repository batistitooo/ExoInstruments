using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// A measured quantity tabulated against wavelength (a detector's quantum efficiency
    /// curve, a coating's reflectivity, a filter's transmission profile), sampled at whatever
    /// wavelengths its source actually publishes, and read back at any wavelength in between.
    ///
    /// This exists because a single "peak" number is not the quantity the photometry needs. A
    /// detector's peak QE is by definition the best it ever does; using it across a whole
    /// passband overstates every filter that does not sit exactly on the peak. FORS2's own
    /// published curve runs 58% at 400nm to 86% at 600nm, so its blue filter collects a
    /// factor 1.5 fewer electrons than the peak figure implies, an error larger than most of
    /// the effects this pipeline models carefully elsewhere.
    ///
    /// Interpolation is linear between published points, and FLAT beyond the ends rather than
    /// extrapolated: past the last measurement there is no information, and continuing the last
    /// slope invents some (a QE curve extrapolated linearly off its red end reaches zero, then
    /// negative, at wavelengths the detector is still demonstrably sensitive at). Holding the
    /// end value is the conservative reading of "the source stops here".
    ///
    /// Pure C#, no Unity dependency, immutable once built.
    /// </summary>
    public sealed class SpectralCurve
    {
        private readonly double[] wavelengthsMeters;
        private readonly double[] values;

        /// <summary>
        /// Builds a curve from published sample points. wavelengthsNm must be strictly
        /// increasing, which is how every published table is laid out anyway; the constructor
        /// throws rather than silently sorting, because a table that arrived out of order is far
        /// more likely to be a transcription error than an ordering preference.
        /// </summary>
        public SpectralCurve(double[] wavelengthsNm, double[] values)
        {
            if (wavelengthsNm == null || values == null)
                throw new ArgumentNullException("wavelengthsNm");
            if (wavelengthsNm.Length != values.Length || wavelengthsNm.Length < 2)
                throw new ArgumentException("A spectral curve needs at least two matching (wavelength, value) points.");

            wavelengthsMeters = new double[wavelengthsNm.Length];
            for (int i = 0; i < wavelengthsNm.Length; i++)
            {
                if (i > 0 && wavelengthsNm[i] <= wavelengthsNm[i - 1])
                    throw new ArgumentException("Spectral curve wavelengths must be strictly increasing.");
                wavelengthsMeters[i] = wavelengthsNm[i] * 1e-9;
            }
            this.values = (double[])values.Clone();
        }

        /// <summary>Shortest wavelength this curve was measured at, in metres.</summary>
        public double MinWavelengthMeters => wavelengthsMeters[0];

        /// <summary>Longest wavelength this curve was measured at, in metres.</summary>
        public double MaxWavelengthMeters => wavelengthsMeters[wavelengthsMeters.Length - 1];

        /// <summary>
        /// The curve's value at the given wavelength: linearly interpolated between the two
        /// bracketing published points, held flat outside the measured range.
        /// </summary>
        public double At(double wavelengthMeters)
        {
            if (wavelengthMeters <= wavelengthsMeters[0]) return values[0];
            int last = wavelengthsMeters.Length - 1;
            if (wavelengthMeters >= wavelengthsMeters[last]) return values[last];

            // Linear scan: these curves are a handful of points long (every published QE table
            // in the roster is 3 to 6 samples), so a binary search would cost more in branches
            // than it saves in comparisons.
            int i = 1;
            while (wavelengthMeters > wavelengthsMeters[i]) i++;

            double span = wavelengthsMeters[i] - wavelengthsMeters[i - 1];
            double t = span > 0.0 ? (wavelengthMeters - wavelengthsMeters[i - 1]) / span : 0.0;
            return values[i - 1] + t * (values[i] - values[i - 1]);
        }
    }
}
