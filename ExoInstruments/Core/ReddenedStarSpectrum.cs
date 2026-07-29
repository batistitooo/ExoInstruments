using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// A star's spectral shape when its catalogue colour is a REDDENED colour.
    ///
    /// THE PROBLEM THIS SOLVES. StellarPhotometry.CollectedElectrons turns a catalogue B-V into an
    /// effective temperature (Ballesteros 2012) and integrates a blackbody at that temperature
    /// across the passband. The catalogue colour is the OBSERVED one -- Gaia measures reddened
    /// photometry and GaiaPhotometry converts it without dereddening anything -- so a hot star
    /// behind two magnitudes of dust is modelled as an intrinsically cool star. Those two have the
    /// same B-V by construction and genuinely different spectra: one is a smooth Planck curve
    /// peaking in the red, the other is a steeply blue Planck curve with the dust curve carved out
    /// of it. Across a wide band they integrate to different effective widths, so the electron
    /// count is wrong by whatever that difference is.
    ///
    /// WHY THIS IS NOT DOUBLE COUNTING. The bandpass integrand is normalised at Johnson V, so what
    /// it uses is a SHAPE and the observed V magnitude sets the scale. Multiplying the shape by
    ///
    ///     10^(-0.4 A(lambda)) / 10^(-0.4 A(V))
    ///
    /// leaves it exactly 1 at V by construction. The star's observed brightness is untouched;
    /// only the distribution of that brightness across the band changes. Nothing is attenuated
    /// twice, because nothing is attenuated at all -- the observed magnitude already contains the
    /// dimming, and this only stops the reddening from being mistaken for a cool photosphere.
    ///
    /// WHAT IT NEEDS. The intrinsic colour, (B-V)_0 = (B-V) - E(B-V), which needs E(B-V) for that
    /// star. Gaia DR3's own astrophysical-parameters pipeline publishes one per source, which is
    /// where RenderedStar.ReddeningEBv comes from; a sight-line dust map is the fallback for a
    /// catalogue that carries none. With no estimate at all this class is not used and the
    /// photometry is exactly what it was, which is the honest behaviour rather than a guess.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class ReddenedStarSpectrum
    {
        /// <summary>
        /// Intrinsic colour from the observed one: (B-V)_0 = (B-V) - E(B-V). The definition of the
        /// colour excess, and the only place the two are related.
        /// </summary>
        public static double IntrinsicColorIndexBV(double observedBv, double eBv)
            => observedBv - Math.Max(0.0, eBv);

        /// <summary>
        /// Intrinsic effective temperature of a star with the given observed colour and reddening.
        /// Null when the dereddened colour falls outside the range Ballesteros' relation covers,
        /// which the caller must treat as "unknown" rather than clamp: an over-corrected colour is
        /// a sign the reddening estimate is wrong, not a reason to invent a temperature.
        /// </summary>
        public static double? IntrinsicTeffK(double observedBv, double eBv)
            => StellarColor.TeffFromColorIndexBV(IntrinsicColorIndexBV(observedBv, eBv));

        /// <summary>
        /// The extinction factor to fold into the bandpass integrand, normalised at Johnson V so
        /// that it is exactly 1 there and the observed magnitude stays the anchor.
        /// </summary>
        public static double NormalisedTransmission(double lambdaMeters, double eBv, double rv)
        {
            if (!(eBv > 0.0)) return 1.0;

            double kLambda = InterstellarExtinction.RelativeExtinction(lambdaMeters, rv);
            if (!(kLambda > 0.0)) return 1.0;   // outside the law's range: not modelled, see there

            double kV = InterstellarExtinction.RelativeExtinction(
                StellarPhotometry.JohnsonVWavelengthMeters, rv);
            if (!(kV > 0.0)) return 1.0;

            return Math.Pow(10.0, -0.4 * rv * eBv * (kLambda - kV));
        }
    }
}
