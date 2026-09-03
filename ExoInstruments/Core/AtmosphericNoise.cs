using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Atmospheric scintillation for ground-based photometry (Young 1967).
    /// ReferencePrecision already bakes in typical-conditions scintillation,
    /// so only the excess above the zenith value is added here in quadrature,
    /// airmass 1 changes nothing, low targets get penalized. RV instruments excluded.
    /// </summary>
    public static class AtmosphericNoise
    {
        private const double AtmosphericScaleHeightMeters = 8000.0;

        /// <summary>
        /// Scintillation RMS above the zenith value at the given airmass: sqrt(sigma(X)^2 - sigma(1)^2). Zero
        /// for space-based or non-transit instruments.
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
        /// Raw Young scintillation formula, instrument-independent. Reused by
        /// AtmosphericImagingNoise for the RC20 camera. Exposure floored at 0.01s
        /// (sub-second imaging is valid here, unlike the photometric cadence).
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
