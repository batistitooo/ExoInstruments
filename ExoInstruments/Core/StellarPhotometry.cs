using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// How many electrons a catalogue star actually puts into the frame, through a specific
    /// filter on a specific telescope.
    ///
    /// The catalogue gives one number, Johnson V, measured through one passband centred at
    /// 5556 Angstrom. The instrument may be looking through a blue filter, a red one, or a 7nm
    /// H-alpha filter. Treating V as though it applied to whichever filter is fitted would make
    /// every star the same colour, which is exactly the thing that makes a synthetic star field
    /// look synthetic: in a real LRGB set a B star is obviously blue and an M giant obviously
    /// orange, because they are.
    ///
    /// So the star's V magnitude is transported to the filter's own passband across a Planck
    /// spectrum at the star's effective temperature, itself derived from the catalogue B-V
    /// colour index by the Ballesteros (2012, EPL 97, 34008) relation already used elsewhere in
    /// this mod. The colour term is a RATIO of photon spectral densities at the two central
    /// wavelengths, so at lambda = 5556 Angstrom it is exactly 1 and the result reduces to the
    /// measured V magnitude; the model only ever interpolates away from a real measurement,
    /// never replaces it.
    ///
    /// A blackbody is not a stellar spectrum: it has no absorption lines and no Balmer jump.
    /// It is however the right first-order continuum shape, it is what the mod's existing
    /// StellarColor display tint already assumes, and it needs no spectral library to ship.
    /// A star with no catalogue colour gets no colour term at all rather than an assumed one.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class StellarPhotometry
    {
        /// <summary>Effective wavelength of the Johnson V band, where the catalogue magnitudes are defined.</summary>
        public const double JohnsonVWavelengthMeters = 5556e-10;

        private const double PlanckH = 6.62607015e-34;   // J s   (SI defining constant)
        private const double SpeedOfLight = 2.99792458e8; // m/s  (SI defining constant)
        private const double BoltzmannK = 1.380649e-23;  // J/K   (SI defining constant)

        /// <summary>
        /// Ratio of photon spectral density (photons per unit wavelength) at wavelength to
        /// that at the Johnson V effective wavelength, for a blackbody at teffK.
        ///
        /// Photon density, not energy density, because every downstream quantity in this
        /// pipeline counts photons: n_lambda ~ lambda^-4 / (exp(hc/(lambda k T)) - 1), which is
        /// the Planck function divided by the photon energy hc/lambda.
        /// </summary>
        public static double ColorTerm(double wavelengthMeters, double teffK)
        {
            if (wavelengthMeters <= 0.0 || teffK <= 0.0) return 1.0;
            double atFilter = PhotonSpectralDensity(wavelengthMeters, teffK);
            double atV = PhotonSpectralDensity(JohnsonVWavelengthMeters, teffK);
            if (atV <= 0.0 || double.IsNaN(atFilter) || double.IsNaN(atV)) return 1.0;
            return atFilter / atV;
        }

        /// <summary>Planck photon spectral density up to a constant factor; only ratios of this are ever used.</summary>
        public static double PhotonSpectralDensity(double wavelengthMeters, double teffK)
        {
            double x = PlanckH * SpeedOfLight / (wavelengthMeters * BoltzmannK * teffK);
            if (x > 700.0) return 0.0;                 // exp() would overflow; the density is zero to any precision that matters
            double l2 = wavelengthMeters * wavelengthMeters;
            return 1.0 / (l2 * l2 * (Math.Exp(x) - 1.0));
        }

        /// <summary>
        /// Electrons a star of the given V magnitude and B-V colour deposits over the exposure,
        /// through the given instrument response. Pass double.NaN for colourIndexBV when the
        /// catalogue has no colour: the star is then integrated as a flat spectrum rather than
        /// one at a guessed temperature.
        ///
        /// The colour term is no longer a separate multiplier. It emerged as a ratio of photon
        /// spectral densities at two wavelengths because the photometry was a product of scalars;
        /// now that SystemResponse integrates the source spectrum across the real passband, the
        /// colour is simply part of that integral (SystemBandpass explains why this is the same
        /// quantity done properly). ColorTerm above is retained as the two-wavelength ratio it
        /// always was, because it is still the right tool for a display tint and for the harness
        /// check that the integral reduces to it on a narrow band.
        ///
        /// extraTransmission carries anything the response was not built with, currently the
        /// per-exposure scintillation draw, which is a real modulation of the incoming intensity
        /// and has no wavelength dependence in Young's formula.
        ///
        /// Everything here is the same photometric chain the resolved bodies go through, so a
        /// star and a planet in the same frame are on one consistent flux scale, which is the
        /// whole point of building the frame from a source list instead of scaling a rendered
        /// image.
        /// </summary>
        public static double CollectedElectrons(
            double vMag, double colorIndexBV, SystemResponse response,
            double apertureAreaCm2, double exposureSeconds, double extraTransmission)
            => CollectedElectrons(vMag, colorIndexBV, double.NaN, response, null,
                                  apertureAreaCm2, exposureSeconds, extraTransmission);

        /// <summary>
        /// As above, with the star's reddening taken into account.
        ///
        /// The catalogue colour is an OBSERVED colour, so a reddened hot star and an intrinsically
        /// cool one arrive here indistinguishable, and the overload above models both as the cool
        /// one. Given E(B-V) they separate: the colour deredden to the star's real temperature, and
        /// the extinction curve goes into the integrand as a shape normalised at V. The observed
        /// magnitude still sets the flux, so nothing is attenuated twice; see ReddenedStarSpectrum.
        ///
        /// eBv NaN or non-positive is the no-estimate case and reproduces the overload above
        /// exactly, which is what a catalogue carrying no reddening column gets.
        /// </summary>
        public static double CollectedElectrons(
            double vMag, double colorIndexBV, double eBv,
            SystemResponse response, ReddenedResponseCache cache,
            double apertureAreaCm2, double exposureSeconds, double extraTransmission)
        {
            if (response == null) return 0.0;

            bool reddened = !double.IsNaN(eBv) && eBv > 0.0;

            double teffK = 0.0;
            if (!double.IsNaN(colorIndexBV))
            {
                double? teff = reddened
                    ? ReddenedStarSpectrum.IntrinsicTeffK(colorIndexBV, eBv)
                    : StellarColor.TeffFromColorIndexBV(colorIndexBV);
                if (teff.HasValue && teff.Value > 0.0) teffK = teff.Value;
            }

            double width = reddened
                ? (cache != null
                    ? cache.EffectiveWidthAngstrom(teffK, eBv)
                    : response.EffectiveWidthAngstromForReddenedStar(teffK, eBv))
                : response.EffectiveWidthAngstromForTemperature(teffK);

            return PhotonFluxModel.CollectedElectrons(vMag, width, apertureAreaCm2, exposureSeconds)
                 * Math.Max(0.0, extraTransmission);
        }
    }
}
