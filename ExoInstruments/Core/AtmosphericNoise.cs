using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Atmospheric scintillation for ground-based photometry: high-altitude
    /// turbulence focuses and defocuses the incoming wavefront, making the
    /// whole star twinkle in integrated flux. It is the dominant noise source
    /// for bright stars on small apertures (exactly WASP's regime) and it
    /// grows steeply toward the horizon.
    ///
    /// Young's (1967) empirical relation, as standardized in Osborn et al.
    /// 2015 (MNRAS 452, 1707):
    ///
    ///   sigma = 0.09 * D^(-2/3) * X^(7/4) * exp(-h/8000) / sqrt(2*t_exp)
    ///
    /// with D the aperture diameter in cm, X the airmass, h the site altitude
    /// in meters (8000 m atmospheric scale height), t_exp the exposure in
    /// seconds, and sigma the fractional flux RMS.
    ///
    /// Each instrument's ReferencePrecision is an as-achieved, on-sky figure,
    /// so typical-conditions scintillation is already inside it. What that
    /// figure cannot contain is the airmass dependence -- so only the excess
    /// above the zenith value is added here, in quadrature: observing at
    /// airmass 1 changes nothing, chasing a target down toward the telescope
    /// limit costs real precision. RV spectrographs measure line positions,
    /// not integrated flux, so scintillation does not apply to them.
    /// </summary>
    public static class AtmosphericNoise
    {
        private const double AtmosphericScaleHeightMeters = 8000.0;

        /// <summary>
        /// Fractional-flux scintillation RMS above the zenith value at the
        /// given airmass, i.e. sqrt(sigma(X)^2 - sigma(1)^2). Zero for
        /// space-based instruments, non-photometric methods, unknown aperture,
        /// or airmass at/below 1.
        /// </summary>
        public static double ScintillationExcessSigma(InstrumentSpec instrument, double airmass)
        {
            if (instrument.IsSpaceBased) return 0.0;
            if (instrument.Method != DetectionMethod.Transit) return 0.0;
            if (instrument.ApertureMeters <= 0.0 || airmass <= 1.0) return 0.0;
            if (double.IsInfinity(airmass) || double.IsNaN(airmass)) return 0.0;

            double atZenith = YoungSigma(instrument, 1.0);
            double atAirmass = YoungSigma(instrument, airmass);
            return Math.Sqrt(Math.Max(0.0, atAirmass * atAirmass - atZenith * atZenith));
        }

        private static double YoungSigma(InstrumentSpec instrument, double airmass)
        {
            double exposureSeconds = Math.Max(1.0, instrument.CadenceSeconds);
            return YoungSigmaRaw(instrument.ApertureMeters, instrument.SiteAltitudeMeters, airmass, exposureSeconds);
        }

        /// <summary>
        /// The raw Young (1967) scintillation formula (see class doc),
        /// independent of any InstrumentSpec -- lets non-photometric
        /// ground-based imaging (e.g. the RC20 astrograph's frame-to-frame
        /// flux flicker, see AtmosphericImagingNoise) reuse the exact same
        /// real physics without going through the Transit-only-gated API
        /// above. Exposure is floored at 0.01s rather than the 1s the
        /// photometric API uses -- real sub-second planetary imaging
        /// exposures are physically meaningful here, unlike a photometric
        /// instrument's multi-second-plus cadence.
        /// </summary>
        public static double YoungSigmaRaw(double apertureMeters, double siteAltitudeMeters, double airmass, double exposureSeconds)
        {
            if (apertureMeters <= 0.0 || double.IsNaN(airmass) || double.IsInfinity(airmass) || airmass < 1.0) return 0.0;
            double apertureCm = apertureMeters * 100.0;
            double exposure = Math.Max(0.01, exposureSeconds);
            return 0.09
                * Math.Pow(apertureCm, -2.0 / 3.0)
                * Math.Pow(airmass, 7.0 / 4.0)
                * Math.Exp(-siteAltitudeMeters / AtmosphericScaleHeightMeters)
                / Math.Sqrt(2.0 * exposure);
        }
    }
}
