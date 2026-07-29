using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// How interstellar dust dims and reddens everything behind it.
    ///
    /// WHY THIS IS NOT THE SAME THING AS AtmosphericImagingNoise. Both attenuate, both are
    /// wavelength-dependent, and both belong inside the bandpass integral rather than at a central
    /// wavelength. They differ in the one way that matters to the architecture: the atmosphere is
    /// the SAME for every source in a frame (one airmass, one site), while dust is DIFFERENT for
    /// every source (each star sits at its own distance in its own direction, behind its own
    /// column). Atmospheric extinction can therefore be baked into SystemResponse's tabulation
    /// once per capture; interstellar extinction cannot, and needs a second axis on that table.
    ///
    /// WHAT IS PARAMETERISED AND WHAT IS NOT. An extinction curve is conventionally split into an
    /// AMOUNT and a SHAPE:
    ///
    ///     A(lambda) = A(V) * k(lambda),      A(V) = R_V * E(B-V)
    ///
    /// with k(lambda) = A(lambda)/A(V) the dimensionless shape, equal to 1 at V by construction.
    /// The amount, E(B-V), comes from a dust map and is a property of the line of sight. The shape
    /// is a property of the dust grains, parameterised by R_V = A(V)/E(B-V), the ratio of total to
    /// selective extinction: 3.1 is the Milky Way diffuse-ISM average (Fitzpatrick 1999 and
    /// essentially every survey since), rising toward 5 in dense clouds where grain growth has
    /// removed the small particles.
    ///
    /// TWO LAWS, DELIBERATELY.
    ///
    ///   * CCM89 (Cardelli, Clayton and Mathis 1989, ApJ 345, 245) is a closed-form polynomial in
    ///     x = 1/lambda. It can be implemented exactly, in a few lines, with nothing to interpolate
    ///     and no table to ship. It is also the single most-cited extinction law in astronomy.
    ///   * F99 (Fitzpatrick 1999, PASP 111, 63) is better in the optical, which is where every
    ///     instrument in this roster works: CCM89's optical polynomial carries systematic residuals
    ///     of a few hundredths of a magnitude that F99's spline does not. It is not closed-form --
    ///     it is a cubic spline through published anchor points -- so it is carried as a TABLE,
    ///     generated from the published law and checked against the reference implementation.
    ///
    /// F99 is the default. CCM89 is kept because it is exactly implementable, which makes it the
    /// control: tools/dust-crossvalidation checks it against the reference implementation to 1e-13,
    /// establishing that the machinery around the law is right, and then checks the F99 table
    /// through the same machinery. Same device as PupilDiffraction reproducing AiryIntensity with
    /// its vanes removed.
    ///
    /// WHAT IS NOT MODELLED HERE, and is named rather than approximated:
    ///
    ///   * The 2175 Angstrom bump and the far-UV rise. Both laws have them; this file covers
    ///     0.3 to 3.4 inverse microns (294 nm to 3.3 microns), which contains every filter in
    ///     FilterCurves and leaves the bump, at 4.6 inverse microns, outside. A UV instrument
    ///     would need the Fitzpatrick and Massa (1990) parameterisation added, and the table
    ///     extended; the range check below refuses rather than extrapolating into it.
    ///   * Any variation of R_V along a single line of sight. One value per sight line is what
    ///     every published dust map supports.
    ///   * Scattering into the beam. Extinction is absorption plus scattering OUT of the line of
    ///     sight; a reflection nebula is the light scattered IN, which is a separate source term
    ///     and not an attenuation.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class InterstellarExtinction
    {
        /// <summary>
        /// Ratio of total to selective extinction for the Milky Way's diffuse interstellar medium,
        /// R_V = A(V)/E(B-V) = 3.1. The value both CCM89 and F99 adopt as the Galactic average, and
        /// the one every all-sky dust map's E(B-V) is calibrated against.
        /// </summary>
        public const double MilkyWayRv = 3.1;

        /// <summary>Shortest wavelength F99 is tabulated to here, metres (x = 3.40 inverse microns).</summary>
        public const double MinWavelengthMeters = 294e-9;

        /// <summary>
        /// Shortest wavelength CCM89 is evaluated to, metres (x = 3.3 inverse microns).
        ///
        /// This is CCM89's own published boundary, not a choice made here: their optical and
        /// near-infrared polynomials (equations 3a/3b) are stated for 1.1 &lt;= x &lt;= 3.3, and above
        /// it the paper switches to a different ultraviolet parameterisation which this file does
        /// not carry -- see the class summary for why the ultraviolet is refused rather than
        /// extrapolated into. The two laws therefore have different blue limits, which is a
        /// property of the laws.
        /// </summary>
        public const double Ccm89MinWavelengthMeters = 1.0e-6 / 3.3;

        /// <summary>Longest wavelength, metres: 1/0.3 microns, the red end of CCM89's infrared branch and of the F99 table.</summary>
        public const double MaxWavelengthMeters = 3.333e-6;

        // ------------------------------------------------------------------ the amount

        /// <summary>
        /// Extinction in magnitudes at V for a given reddening: A(V) = R_V * E(B-V). The definition
        /// of R_V, and the only place the two quantities are related.
        /// </summary>
        public static double AvFromReddening(double eBv, double rv = MilkyWayRv)
            => Math.Max(0.0, eBv) * rv;

        /// <summary>
        /// Fraction of a source's flux transmitted at one wavelength through a given reddening.
        ///
        /// This is Pogson's definition applied to the extinction the law gives:
        ///     T = 10^(-0.4 * A(lambda)) = 10^(-0.4 * R_V * E(B-V) * k(lambda))
        ///
        /// Returns 1 for no reddening, and is the quantity SystemResponse multiplies into its
        /// integrand -- the same place and the same form as the atmosphere's own transmission.
        /// </summary>
        public static double Transmission(double wavelengthMeters, double eBv, double rv = MilkyWayRv)
        {
            if (!(eBv > 0.0)) return 1.0;
            double k = RelativeExtinction(wavelengthMeters, rv);
            if (!(k > 0.0)) return 1.0;
            return Math.Pow(10.0, -0.4 * rv * eBv * k);
        }

        // ------------------------------------------------------------------ the shape

        /// <summary>Which published law RelativeExtinction evaluates.</summary>
        public enum Law
        {
            /// <summary>Fitzpatrick (1999, PASP 111, 63). The default: better in the optical, carried as a table.</summary>
            Fitzpatrick99,
            /// <summary>Cardelli, Clayton and Mathis (1989, ApJ 345, 245). Closed form, exactly implementable, kept as the control.</summary>
            Ccm89,
        }

        /// <summary>The law used unless a caller asks for another. F99, because every instrument in the roster works in the optical.</summary>
        public static Law ActiveLaw { get; set; } = Law.Fitzpatrick99;

        /// <summary>
        /// k(lambda) = A(lambda)/A(V), the dimensionless shape of the extinction curve. Equal to 1
        /// at Johnson V by construction of the ratio.
        ///
        /// Returns 0 outside the range the laws are defined over, which the caller must read as
        /// "not modelled here" rather than as "transparent": Transmission above turns a zero into
        /// no attenuation, and that is a deliberate refusal to extrapolate a law into the
        /// ultraviolet bump it does not carry.
        /// </summary>
        public static double RelativeExtinction(double wavelengthMeters, double rv = MilkyWayRv)
            => RelativeExtinction(wavelengthMeters, rv, ActiveLaw);

        public static double RelativeExtinction(double wavelengthMeters, double rv, Law law)
        {
            if (!(wavelengthMeters > 0.0)) return 0.0;
            if (wavelengthMeters > MaxWavelengthMeters) return 0.0;

            if (law == Law.Ccm89)
            {
                // Strict, because 3.3 is where CCM89 hands over TO the ultraviolet branch rather
                // than the last point of the optical one. An inclusive bound here is a single
                // point of disagreement with every other implementation of the law, and the
                // harness finds it: 1.8e-4 at x = 3.3 exactly, machine precision everywhere else.
                if (wavelengthMeters <= Ccm89MinWavelengthMeters) return 0.0;
                return Ccm89RelativeExtinction(wavelengthMeters, rv);
            }

            if (wavelengthMeters < MinWavelengthMeters) return 0.0;
            return Fitzpatrick99Table.Evaluate(wavelengthMeters, rv);
        }

        // ------------------------------------------------------------------ CCM89, closed form

        /// <summary>
        /// A(lambda)/A(V) by Cardelli, Clayton and Mathis (1989, ApJ 345, 245), equations 2a/2b
        /// (infrared) and 3a/3b (optical and near-infrared), in the form
        ///
        ///     A(x)/A(V) = a(x) + b(x)/R_V
        ///
        /// with x the wavenumber in inverse microns. Every coefficient below is theirs; none is
        /// fitted, adjusted or rounded here, and tools/dust-crossvalidation checks the whole
        /// function against the reference implementation to machine precision.
        ///
        /// The two branches meet at x = 1.1 (909 nm), which is where CCM89 themselves place the
        /// join between their infrared power law and their optical polynomial.
        /// </summary>
        public static double Ccm89RelativeExtinction(double wavelengthMeters, double rv = MilkyWayRv)
        {
            if (!(wavelengthMeters > 0.0) || !(rv > 0.0)) return 0.0;
            double x = 1.0e-6 / wavelengthMeters;   // inverse microns

            double a, b;
            if (x < 1.1)
            {
                // Equations 2a/2b: the infrared power law, A(lambda) proportional to lambda^-1.61.
                double p = Math.Pow(x, 1.61);
                a = 0.574 * p;
                b = -0.527 * p;
            }
            else
            {
                // Equations 3a/3b: seventh-order polynomials in y = x - 1.82, i.e. offsets from
                // the V band's own wavenumber.
                double y = x - 1.82;
                a = 1.0 + y * (0.17699 + y * (-0.50447 + y * (-0.02427 + y * (0.72085
                        + y * (0.01979 + y * (-0.77530 + y * 0.32999))))));
                b = y * (1.41338 + y * (2.28305 + y * (1.07233 + y * (-5.38434
                      + y * (-0.62251 + y * (5.30260 + y * -2.09002))))));
            }

            return a + b / rv;
        }
    }
}
