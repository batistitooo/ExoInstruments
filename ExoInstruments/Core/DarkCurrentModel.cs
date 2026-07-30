using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// How a silicon detector's dark current depends on its temperature, and what a hot pixel
    /// actually is.
    ///
    /// WHAT THIS REPLACES. VisualTelescopeSpec carries one dark-current figure per instrument, at
    /// that instrument's own cooled operating temperature, and the pipeline used it as a constant.
    /// Alongside it, DetectorTemperatureCelsius was carried through to the FITS CCD-TEMP keyword
    /// and otherwise did nothing: two numbers that are physically one relationship, with the
    /// relationship missing. Dark current is the single most temperature-sensitive quantity in the
    /// whole sensor chain; it roughly doubles every 6 or 7 degrees, so a detector temperature
    /// that changes nothing is the most misleading kind of unmodelled parameter.
    ///
    /// THE PUBLISHED MODEL. Thermal generation in the depletion region, as given by Janesick
    /// (2001, "Scientific Charge-Coupled Devices", SPIE Press) in the form used throughout CCD
    /// characterisation:
    ///
    ///     D(T) = 2.5e15 * P_s * D_FM * T^1.5 * exp( -E_g(T) / (2 k T) )      [e- / pixel / s]
    ///
    /// with P_s the pixel area in cm^2, D_FM the device's dark-current figure of merit at 300 K in
    /// nA/cm^2, k Boltzmann's constant in eV/K and E_g the silicon band gap in eV. The half-gap in
    /// the exponent is the signature of depletion-region (Shockley-Read-Hall) generation, which is
    /// the dominant mechanism at the cooled temperatures every instrument in this roster runs at;
    /// Widenhorn et al. (2002, SPIE 4669, 193, "Temperature dependence of dark current in a CCD")
    /// measure the crossover and show that above it diffusion current, characterised by the FULL
    /// band gap, takes over instead.
    ///
    /// ONLY THE RATIO IS USED HERE, which is what makes this rigorous without any new per-device
    /// data: P_s and D_FM are properties of the device that cancel exactly between D(T) and
    /// D(T_ref), leaving
    ///
    ///     D(T)/D(T_ref) = (T/T_ref)^1.5 * exp[ -E_g(T)/(2kT) + E_g(T_ref)/(2kT_ref) ]
    ///
    /// so each instrument's already-published dark current at its own published operating
    /// temperature is all the calibration this needs. Nothing is invented and nothing new has to
    /// be looked up.
    ///
    /// THE BAND GAP IS TEMPERATURE-DEPENDENT TOO, by the Varshni (1967, Physica 34, 149) relation
    /// with the standard silicon parameters used in this equation:
    ///
    ///     E_g(T) = 1.1557 - 7.021e-4 * T^2 / (1108 + T)      [eV]
    ///
    /// Leaving it constant would be a visible error over a hundred-degree span, since it sits
    /// inside the exponential.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class DarkCurrentModel
    {
        /// <summary>Silicon band gap extrapolated to 0 K, eV (Varshni form as used in Janesick's dark-current equation).</summary>
        public const double BandGapAtZeroKelvinEv = 1.1557;

        /// <summary>Varshni alpha for silicon, eV/K.</summary>
        public const double VarshniAlphaEvPerKelvin = 7.021e-4;

        /// <summary>Varshni beta for silicon, K.</summary>
        public const double VarshniBetaKelvin = 1108.0;

        /// <summary>Boltzmann constant in eV/K, the unit this equation is written in (CODATA 2018 exact value 1.380649e-23 J/K over the exact elementary charge).</summary>
        public const double BoltzmannEvPerKelvin = 8.617333262e-5;

        /// <summary>
        /// Power of T outside the exponential, 1.5, per Janesick's form. Widenhorn et al. discuss
        /// why the effective exponent is not a clean constant across the full range (the mechanism
        /// changes); over the cooled span these instruments operate in, where depletion current
        /// dominates throughout, 1.5 is the right one and the exponential dominates the behaviour
        /// regardless.
        /// </summary>
        public const double TemperatureExponent = 1.5;

        public const double AbsoluteZeroCelsius = -273.15;

        public static double CelsiusToKelvin(double celsius) => celsius - AbsoluteZeroCelsius;

        /// <summary>Silicon band gap at the given temperature, eV, by Varshni (1967).</summary>
        public static double SiliconBandGapEv(double temperatureKelvin)
        {
            if (!(temperatureKelvin > 0.0)) return BandGapAtZeroKelvinEv;
            return BandGapAtZeroKelvinEv
                 - VarshniAlphaEvPerKelvin * temperatureKelvin * temperatureKelvin
                   / (VarshniBetaKelvin + temperatureKelvin);
        }

        /// <summary>
        /// D(T)/D(T_ref): the factor the published dark current at the reference temperature must
        /// be multiplied by to give the rate at another temperature.
        ///
        /// Returns 0 at or below absolute zero, and 1 when the two temperatures agree, which is
        /// the case for every instrument today, since none yet exposes a cooler setpoint, so this
        /// changes no existing behaviour until one does.
        /// </summary>
        public static double ScaleFactor(double temperatureKelvin, double referenceTemperatureKelvin)
        {
            if (!(temperatureKelvin > 0.0)) return 0.0;
            if (!(referenceTemperatureKelvin > 0.0)) return 1.0;

            double exponent = -SiliconBandGapEv(temperatureKelvin)
                                / (2.0 * BoltzmannEvPerKelvin * temperatureKelvin)
                            + SiliconBandGapEv(referenceTemperatureKelvin)
                                / (2.0 * BoltzmannEvPerKelvin * referenceTemperatureKelvin);

            return Math.Pow(temperatureKelvin / referenceTemperatureKelvin, TemperatureExponent)
                 * Math.Exp(exponent);
        }

        /// <summary>
        /// Dark current (e-/pixel/s) at an actual detector temperature, from the device's published
        /// rate at its own published operating temperature. Both temperatures in Celsius, which is
        /// the unit the catalogue and the FITS CCD-TEMP keyword both use.
        /// </summary>
        public static double ElectronsPerSecond(
            double referenceRateElectronsPerSecond,
            double referenceTemperatureCelsius,
            double temperatureCelsius)
        {
            if (!(referenceRateElectronsPerSecond > 0.0)) return 0.0;
            if (double.IsNaN(referenceTemperatureCelsius) || double.IsNaN(temperatureCelsius))
                return referenceRateElectronsPerSecond;

            return referenceRateElectronsPerSecond
                 * ScaleFactor(CelsiusToKelvin(temperatureCelsius),
                               CelsiusToKelvin(referenceTemperatureCelsius));
        }

        /// <summary>
        /// Temperature change, in degrees, over which dark current doubles at the given
        /// temperature. Not used by the pipeline; it exists because it is the number detector
        /// engineers quote and remember ("dark doubles every 6 to 7 degrees"), so it is the
        /// cheapest way to check this model against common knowledge rather than only against
        /// itself.
        /// </summary>
        public static double DoublingTemperatureDelta(double temperatureCelsius)
        {
            double t0 = CelsiusToKelvin(temperatureCelsius);
            double lo = 0.1, hi = 40.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (ScaleFactor(t0 + mid, t0) < 2.0) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>
        /// What a hot pixel is: a pixel whose depletion-region generation rate is anomalously high
        /// because of a bulk lattice defect, so its dark current is a MULTIPLE of the array's
        /// median. Widenhorn et al. (2002) establish this directly; the depletion component that
        /// dominates at cooled temperatures is the one that varies from pixel to pixel, while the
        /// diffusion component that dominates when warm is uniform across the array.
        ///
        /// It is emphatically NOT what the pipeline used to do, which was to overwrite hot pixels
        /// with a fixed near-full-scale value AFTER digitisation. That had three consequences a
        /// real frame does not share: the defects did not grow with exposure time (a 1-second sub
        /// showed them exactly as blown as a 300-second one), they did not respond to detector
        /// temperature at all, and above all they could not be removed by subtracting a dark frame,
        /// which is the entire reason a real observer takes one.
        ///
        /// The multiplier is a DEFINITION rather than a measurement, and is stated as one: it is
        /// set so that a hot pixel just reaches the converter's top code in the instrument's own
        /// longest supported exposure. That ties it to two real published instrument parameters
        /// (the maximum exposure and the ADC range) instead of to a free constant, and it
        /// reproduces the operational definition a sensor characterisation uses ("a pixel that
        /// saturates in a nominal long dark"). Per-device hot-pixel dark rates are not published
        /// for any camera in this roster.
        /// </summary>
        public static double HotPixelDarkMultiplier(
            double baseDarkElectronsPerSecond,
            double longestExposureSeconds,
            double saturationElectrons)
        {
            if (!(baseDarkElectronsPerSecond > 0.0) || !(longestExposureSeconds > 0.0)) return 1.0;
            if (!(saturationElectrons > 0.0)) return 1.0;

            double multiplier = saturationElectrons / (baseDarkElectronsPerSecond * longestExposureSeconds);
            return Math.Max(1.0, multiplier);
        }
    }
}
