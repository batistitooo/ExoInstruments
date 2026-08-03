using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// What the forbidden lines do relative to H-alpha, from the physics that sets them rather than
    /// from a ratio picked to look right.
    ///
    /// WHY THIS IS NOT A FUDGE FACTOR. H-alpha is a RECOMBINATION line: its emissivity falls slowly
    /// with temperature and is otherwise fixed by how many protons recombine. [N II] and [S II] are
    /// COLLISIONALLY EXCITED: an electron has to be knocked into a level about 2 eV up, so their
    /// emissivity carries exp(-E/kT) and rises steeply with temperature. The ratio between them is
    /// therefore a thermometer, not a free parameter, and that is exactly how it is used
    /// observationally; Madsen, Reynolds &amp; Haffner (2006, ApJ 652, 401) measure the warm ionised
    /// medium's temperature by inverting these very expressions.
    ///
    /// The emissivity ratios are Haffner, Reynolds &amp; Tufte (1999, ApJ 523, 223), eq. 1 and 2:
    ///
    ///     I([N II] 6584)/I(Ha) = 1.63e5 (N+/N)(H/H+)(N/H) T4^0.426 exp(-2.18/T4)
    ///     I([S II] 6716)/I(Ha) = 7.64e5 (S+/S)(H/H+)(S/H) T4^0.307 exp(-2.14/T4)
    ///
    /// with T4 the electron temperature in units of 10^4 K. Nitrogen needs no ionisation
    /// correction: charge exchange with hydrogen is fast enough that N+/N tracks H+/H closely
    /// (Butler &amp; Dalgarno 1980), which is the reason [N II]/H-alpha is the cleaner thermometer of
    /// the two. Sulphur has no such lock; S++ is a significant stage, so S+/S stays an explicit
    /// input.
    ///
    /// WHAT IS NOT SYNTHESISED, and why that is the scientific answer. [O III] 5007 needs O++,
    /// which needs photons above 35 eV; the diffuse ionised gas is lit by Lyman continuum that
    /// leaked out of H II regions and is far too soft to make much of it, so [O III] does not track
    /// H-alpha at all; it is strong in planetary nebulae, supernova remnants and the hot cores of
    /// a few H II regions, and weak everywhere in between. [O I] 6300 traces the neutral boundary
    /// rather than the ionised gas, and is also the brightest terrestrial airglow line. Deriving
    /// either from an H-alpha map would be inventing a sky. They stay empty until a survey of their
    /// own is installed, which is a real gap and is reported as one.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class NebularLineRatios
    {
        // --- Emissivity coefficients, Haffner, Reynolds & Tufte (1999) eq. 1 and 2 -----------

        private const double NiiCoefficient = 1.63e5;
        private const double NiiTemperatureExponent = 0.426;
        /// <summary>Excitation energy of the N+ 1D term, in units of 10^4 K.</summary>
        private const double NiiExcitationT4 = 2.18;

        private const double SiiCoefficient = 7.64e5;
        private const double SiiTemperatureExponent = 0.307;
        /// <summary>Excitation energy of the S+ 2D term, in units of 10^4 K.</summary>
        private const double SiiExcitationT4 = 2.14;

        // --- Gas-phase abundances, the values Madsen et al. (2006) adopt for the WIM ---------

        /// <summary>Gas-phase N/H. Some nitrogen is in dust, so this is below the solar total.</summary>
        public const double NitrogenAbundance = 7.5e-5;
        /// <summary>Gas-phase S/H.</summary>
        public const double SulphurAbundance = 1.86e-5;

        /// <summary>
        /// Fraction of sulphur in S+.
        ///
        /// Unlike nitrogen, sulphur is not locked to hydrogen by charge exchange; S++ is a
        /// significant stage, so this cannot be set from atomic physics and is the one free
        /// parameter in the pair. It is not guessed either: [S II]/[N II] is nearly INDEPENDENT of
        /// temperature, because the two lines have almost the same excitation energy (2.14 against
        /// 2.18 in units of 10^4 K), so the observed ratio measures this fraction directly. That is
        /// how Madsen, Reynolds &amp; Haffner (2006) obtain it, and 0.35 is what reproduces the
        /// [S II]/[N II] near 0.44 that they measure across the warm ionised medium.
        ///
        /// The insensitivity is also why that ratio is nearly constant across the sky while both
        /// ratios to H-alpha vary by a factor of five, the observational statement that what
        /// changes from place to place is the temperature, not the abundances.
        /// </summary>
        public const double SulphurSinglyIonisedFraction = 0.35;

        // --- Fixed atomic ratios ------------------------------------------------------------

        /// <summary>
        /// [S II] 6716/6731 in the low-density limit, 1.43 (Osterbrock &amp; Ferland 2006, sect. 5.6).
        /// The pair is the standard density diagnostic and falls toward 0.44 above about 10^4
        /// cm^-3, but a 6 arcmin beam averages over volumes whose mean density is orders of
        /// magnitude below that, so the low-density limit is the value the map's own resolution
        /// implies.
        /// </summary>
        public const double SiiDoubletRatio = 1.43;

        /// <summary>
        /// Electron temperature implied by an H-alpha surface brightness, kelvin.
        ///
        /// The anticorrelation between [N II]/H-alpha and H-alpha intensity is one of the WIM's
        /// most robust observed properties (Haffner et al. 1999; Madsen et al. 2006), and it is a
        /// temperature gradient: dense, bright, classical H II regions cool efficiently and sit at
        /// 6000-7000 K, while the faint diffuse gas is hotter, about 8000 K near the midplane and
        /// approaching 10^4 K at a kiloparsec above it, because photoelectric and other heating
        /// per recombination rises as the density falls.
        ///
        /// Interpolated logarithmically in intensity between those two measured anchors and clamped
        /// outside them. It is a two-point model of a measured trend, not a fit to data this project
        /// holds, and it is the one modelled step between the H-alpha map and the other lines,
        /// which is why the temperature it used is reported alongside the frame.
        /// </summary>
        public static double ElectronTemperatureK(double halphaRayleighs)
        {
            if (!(halphaRayleighs > 0.0)) return FaintTemperatureK;
            double logI = Math.Log10(halphaRayleighs);
            double t = (logI - FaintAnchorLogRayleighs) / (BrightAnchorLogRayleighs - FaintAnchorLogRayleighs);
            t = Math.Max(0.0, Math.Min(1.0, t));
            return FaintTemperatureK + t * (BrightTemperatureK - FaintTemperatureK);
        }

        /// <summary>Bright anchor: a classical H II region, 1000 R, at the 6500 K such regions are measured at.</summary>
        private const double BrightAnchorLogRayleighs = 3.0;
        private const double BrightTemperatureK = 6500.0;
        /// <summary>Faint anchor: high-latitude diffuse gas, 1 R, at 10000 K.</summary>
        private const double FaintAnchorLogRayleighs = 0.0;
        private const double FaintTemperatureK = 10000.0;

        /// <summary>I([N II] 6584) / I(H-alpha) at a given electron temperature.</summary>
        public static double Nii6584OverHalpha(double electronTempK)
        {
            double t4 = electronTempK / 1.0e4;
            if (!(t4 > 0.0)) return 0.0;
            return NiiCoefficient * NitrogenAbundance
                 * Math.Pow(t4, NiiTemperatureExponent) * Math.Exp(-NiiExcitationT4 / t4);
        }

        /// <summary>I([S II] 6716) / I(H-alpha) at a given electron temperature.</summary>
        public static double Sii6716OverHalpha(double electronTempK)
        {
            double t4 = electronTempK / 1.0e4;
            if (!(t4 > 0.0)) return 0.0;
            return SiiCoefficient * SulphurAbundance * SulphurSinglyIonisedFraction
                 * Math.Pow(t4, SiiTemperatureExponent) * Math.Exp(-SiiExcitationT4 / t4);
        }

        /// <summary>
        /// Surface brightness of one line relative to H-alpha, given the H-alpha brightness itself.
        /// Returns NaN for a line this cannot derive; see the class summary for which and why.
        /// </summary>
        public static double RatioToHalpha(EmissionLines.Line line, double halphaRayleighs)
        {
            double t = ElectronTemperatureK(halphaRayleighs);

            if (Same(line, EmissionLines.HAlpha)) return 1.0;
            if (Same(line, EmissionLines.NII6584)) return Nii6584OverHalpha(t);
            if (Same(line, EmissionLines.NII6548)) return Nii6584OverHalpha(t) / EmissionLines.NiiDoubletRatio;
            if (Same(line, EmissionLines.SII6716)) return Sii6716OverHalpha(t);
            if (Same(line, EmissionLines.SII6731)) return Sii6716OverHalpha(t) / SiiDoubletRatio;
            return double.NaN;
        }

        /// <summary>
        /// Every ratio one pixel needs, solved once from that pixel's own H-alpha brightness.
        ///
        /// IDENTICAL ARITHMETIC, NOT A CHEAPER ONE. RatioToHalpha below takes the brightness and
        /// derives the temperature before answering, so a caller stepping through the five
        /// derivable lines at one pixel recomputed the same logarithm five times and the same two
        /// exponentials twice each; the doublet partners then divided a value their sibling had
        /// just computed. This evaluates each of those exactly once and hands back the same
        /// numbers. Nothing is tabulated or interpolated: the two ratio functions are still
        /// evaluated in full, at the temperature this pixel's own brightness implies.
        ///
        /// It is worth a type of its own because it runs once per frame pixel: an RC20 exposure
        /// is 11.7 million pixels at 1x1, where the repetition was measured at about four seconds
        /// of the capture.
        /// </summary>
        public struct RatioSet
        {
            /// <summary>The electron temperature this pixel's brightness implies, kelvin.</summary>
            public readonly double ElectronTemperatureK;

            private readonly double nii6584;
            private readonly double sii6716;

            public RatioSet(double halphaRayleighs)
            {
                ElectronTemperatureK = NebularLineRatios.ElectronTemperatureK(halphaRayleighs);
                nii6584 = Nii6584OverHalpha(ElectronTemperatureK);
                sii6716 = Sii6716OverHalpha(ElectronTemperatureK);
            }

            /// <summary>The same answer RatioToHalpha gives, from the temperature already solved for.</summary>
            public double RatioToHalpha(EmissionLines.Line line)
            {
                if (Same(line, EmissionLines.HAlpha)) return 1.0;
                if (Same(line, EmissionLines.NII6584)) return nii6584;
                if (Same(line, EmissionLines.NII6548)) return nii6584 / EmissionLines.NiiDoubletRatio;
                if (Same(line, EmissionLines.SII6716)) return sii6716;
                if (Same(line, EmissionLines.SII6731)) return sii6716 / SiiDoubletRatio;
                return double.NaN;
            }
        }

        /// <summary>Every line this can derive from an H-alpha map, in wavelength order.</summary>
        public static readonly EmissionLines.Line[] DerivableLines =
        {
            EmissionLines.NII6548, EmissionLines.HAlpha, EmissionLines.NII6584,
            EmissionLines.SII6716, EmissionLines.SII6731,
        };

        private static bool Same(EmissionLines.Line a, EmissionLines.Line b)
            => Math.Abs(a.WavelengthMeters - b.WavelengthMeters) < 1e-14;
    }
}
