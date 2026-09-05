using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Why a red image of a dark sky comes out corrugated.
    ///
    /// WHAT FRINGING IS. A thinned CCD is an etalon nobody meant to build. Light that reaches the
    /// silicon without being absorbed reflects off the far surface, comes back, and interferes with
    /// what is still arriving; the detector's response is therefore modulated by
    /// cos(2 pi P / lambda) with P = 2 n d the optical path across the layer. In the blue the
    /// silicon absorbs everything within a micron and there is nothing to reflect, so there are no
    /// fringes; past about 800 nm the absorption length approaches the layer thickness and the
    /// modulation becomes the dominant flat-field structure in the frame.
    ///
    /// WHY IT DOES NOT SIMPLY AVERAGE AWAY. Over a broad passband the phase 2 pi P / lambda runs
    /// through many turns, so a smooth source's fringes cancel almost exactly. The night sky is not
    /// a smooth source. Past 700 nm it is a picket fence of OH Meinel bands, and each line samples
    /// the modulation at one phase rather than averaging over it, so the fringes survive with the
    /// LINE SPECTRUM'S own weighting. That is why fringing is a property of the sky as much as of
    /// the detector, why it changes between exposures as the airglow does, and why flat-fielding on
    /// a continuum lamp does not remove it. This pipeline can compute that integral rather than
    /// assert it, because it already carries the airglow line spectrum at 0.1 nm sampling
    /// (Core.Airglow, Core.AirglowTable).
    ///
    /// WHAT IS MEASURED, AND BY WHOM. Two independent ESO measurements, from two papers, and the
    /// check that they agree is what this model rests on rather than on either alone:
    ///
    ///   * Walsh, Kuntschner, Jehin, Kaufer, O'Brien, Riquelme and Smette (2008, "Modelling the
    ///     Fringing of the FORS2 CCD", in "2007 ESO Instrument Calibration Workshop") fed a tunable
    ///     monochromator into FORS2's calibration unit and measured the peak-to-peak fringe
    ///     amplitude on the MIT mosaic at six wavelengths: nothing visible at 774 nm, then 2.2% at
    ///     876, 3.0% at 906, 5.1% at 926, 7.0% at 956 and 7.5% at 986 nm. They also give the fringe
    ///     WAVELENGTH PERIOD as 2.9 nm, measured from 300I flats taken in the ESO GOODS programme,
    ///     and the spatial scale as about 40 pixels between adjacent fringes at their closest.
    ///   * Downing, Baade, Sinclaire, Deiries and Christen (2006, SPIE Orlando) state the device:
    ///     "The MIT/LL CCID-20 is a 40 um thick high resistivity deep depletion CCD."
    ///
    /// THE TWO AGREE, and neither was derived from the other. A modulation of period Delta lambda
    /// at wavelength lambda implies an optical path P = lambda^2 / Delta lambda, so Walsh's 2.9 nm
    /// at 950 nm gives P = 311 um; dividing by twice the refractive index of silicon there (3.585,
    /// Green 2008) gives a layer 43.4 um thick, against the 40 um Downing et al. state outright.
    /// An 8.5% agreement between a spectroscopic period and a fabrication specification is a
    /// stronger foundation than either number on its own, and it is the reason the thickness below
    /// is the published one rather than the fitted one.
    ///
    /// WHAT THIS MEANS FOR THIS ROSTER, which is less than it first appears and is stated plainly.
    /// ESO's FORS2 manual is explicit that the MIT mosaic was chosen for low fringing: "For Bessel
    /// I imaging, fringes are hardly visible ... For z_Gunn imaging, the fringe amplitudes are below
    /// 1% and in the strongest telluric lines in spectroscopic modes fringe amplitudes were found
    /// to be of the order of 5% in the worst cases." Walsh's 7.5% is the MONOCHROMATIC amplitude at
    /// 986 nm, which is the spectroscopic regime; a broadband imaging filter integrates it down to
    /// the sub-percent figure the manual quotes. Both numbers are correct and they describe
    /// different measurements, and the integral below is what turns one into the other. On this
    /// roster the effect reaches FORS2's unfiltered "Luminance" band, which runs to 1100 nm, and
    /// essentially nothing else.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class Fringing
    {
        /// <summary>
        /// Thickness of the MIT/LL CCID-20's silicon, as Downing et al. (2006) state it. Not
        /// fitted: see the cross-check in this class's own summary for why the published figure is
        /// used rather than the 43.4 um Walsh's fringe period implies.
        /// </summary>
        public const double Ccid20ThicknessMicrons = 40.0;

        // Refractive index of crystalline silicon at 300 K, from Green (2008, "Self-consistent optical
        // parameters of intrinsic silicon at 300K including temperature coefficients", Solar Energy Materials
        // and Solar Cells 92, 1305), which is the standard modern tabulation and supersedes Aspnes and Studna
        // for this purpose. Only the range fringes exist in is carried. Below 700 nm the absorption length is
        // far shorter than any thinned layer and there is no second surface to interfere with, so the index
        // there would be describing an effect that does not occur.
        private static readonly double[] IndexWavelengthNm = { 700.0, 750.0, 800.0, 850.0, 900.0, 950.0, 1000.0, 1050.0, 1100.0 };
        private static readonly double[] IndexValue = { 3.785, 3.726, 3.681, 3.646, 3.618, 3.585, 3.565, 3.548, 3.531 };

        /// <summary>Silicon's refractive index at the given wavelength, linearly interpolated and held flat outside the tabulated range.</summary>
        public static double SiliconRefractiveIndex(double wavelengthNm)
            => Interpolate(IndexWavelengthNm, IndexValue, wavelengthNm);

        // Peak-to-peak fringe amplitude at one wavelength, as a fraction, from Walsh et al.'s six monochromatic
        // flats. Zero below 774 nm, which is a measurement rather than an assumption: their 774 nm flat showed
        // no fringes at all, and the physics says the same, since the absorption length in silicon there is
        // still short against 40 um. Held flat above 986 nm rather than extrapolated; their own text notes the
        // rise "levels off to higher wavelength", and the detector's quantum efficiency has collapsed by then
        // anyway.
        private static readonly double[] AmplitudeWavelengthNm = { 774.0, 876.0, 906.0, 926.1, 956.1, 986.0 };
        private static readonly double[] AmplitudeValue = { 0.000, 0.022, 0.030, 0.051, 0.070, 0.075 };

        public static double MonochromaticPeakToPeak(double wavelengthNm)
        {
            if (wavelengthNm <= AmplitudeWavelengthNm[0]) return 0.0;
            return Interpolate(AmplitudeWavelengthNm, AmplitudeValue, wavelengthNm);
        }

        /// <summary>
        /// The optical path across the layer, in nanometres: twice the thickness times the
        /// refractive index, which is the distance the reflected light travels further than the
        /// light that did not reflect.
        /// </summary>
        public static double OpticalPathNm(double thicknessMicrons, double wavelengthNm)
            => 2.0 * thicknessMicrons * 1000.0 * SiliconRefractiveIndex(wavelengthNm);

        /// <summary>
        /// The fringe period in wavelength at a given wavelength: the interval over which the phase
        /// advances by one turn.
        ///
        /// This is the quantity Walsh et al. measured independently of everything else, at 2.9 nm,
        /// and the one this model is checked against. Returned as a method rather than a constant
        /// because it is a strong function of wavelength, falling as lambda^2.
        /// </summary>
        public static double PeriodNm(double thicknessMicrons, double wavelengthNm)
        {
            double path = OpticalPathNm(thicknessMicrons, wavelengthNm);
            if (!(path > 0.0)) return 0.0;
            return wavelengthNm * wavelengthNm / path;
        }

        /// <summary>
        /// The layer thickness a measured fringe period implies. The inverse of the above, and the
        /// half of the cross-check that starts from a spectroscopic measurement rather than from a
        /// fabrication specification.
        /// </summary>
        public static double ThicknessFromPeriodMicrons(double periodNm, double wavelengthNm)
        {
            if (!(periodNm > 0.0)) return 0.0;
            double path = wavelengthNm * wavelengthNm / periodNm;
            return path / (2.0 * SiliconRefractiveIndex(wavelengthNm) * 1000.0);
        }

        /// <summary>
        /// The passband side of the fringe integral, walked once.
        ///
        /// THE INTEGRAL THAT DECIDES EVERYTHING:
        ///
        ///     F(P) = 1 + [ integral S(l) R(l) (A(l)/2) cos(2 pi P / l) dl ] / [ integral S(l) R(l) dl ]
        ///
        /// with S the sky's spectral radiance, R the system's response, A the monochromatic
        /// peak-to-peak amplitude above, and P the pixel's own optical path. Everything that makes
        /// fringing behave the way it does is in the ratio of those two integrals: a broad, smooth
        /// passband runs the cosine through many turns and the numerator cancels itself; a passband
        /// dominated by isolated OH lines samples the cosine at a few phases and it does not.
        ///
        /// P varies across the detector because the layer thickness does, by a fraction of a
        /// percent, and that variation is what draws the pattern. It is passed in rather than
        /// modelled here: its map is a property of one piece of silicon and nobody publishes one.
        ///
        /// WHY A TYPE AND NOT AN ARGUMENT LIST. S, R and A are not functions of the pixel; only the
        /// cosine is. Written as one scalar function of six arguments, the whole spectral walk had
        /// to be repeated for every pixel of the detector -- 1,056,768 times for a 4x4-binned FORS2
        /// frame and 16,908,288 at 1x1 -- and building one fringe map stopped the capture for
        /// minutes with no stage timing to say why. Sampling the band once and then asking it for a
        /// modulation is not an optimisation of a correct thing into a faster thing: it is the
        /// shape this integral already has. The six-argument scalar entry point is GONE rather than
        /// deprecated, because a scalar signature over a 6500-point body is precisely the affordance
        /// that put the walk inside a per-pixel loop, and leaving it would leave the next caller the
        /// same trap.
        ///
        /// WHAT IS STORED. One entry per grid point that can fringe at all: everything above Walsh
        /// et al.'s 774 nm onset and nothing below it, which for an unfiltered 350-1000 nm band at
        /// AirglowTable's 0.1 nm sampling is 2260 points of 6500. The denominator carries the weight
        /// sum over ALL of them, fringing or not, because the normalisation is the whole passband's
        /// flux and not only the part of it that interferes.
        /// </summary>
        public sealed class Passband
        {
            private readonly double[] wavelengthNm;   // l_k, exactly as the walk below produced them
            private readonly double[] coefficient;    // (w_k * 0.5) * A_k, in that association
            private readonly double weightTotal;      // sum of w_k across the whole band

            private static readonly Passband EmptyBand = new Passband(new double[0], new double[0], 0.0);

            private Passband(double[] wavelengths, double[] coefficients, double total)
            {
                wavelengthNm = wavelengths;
                coefficient = coefficients;
                weightTotal = total;
            }

            /// <summary>A band that fringes nowhere, which is what every guard failure returns.</summary>
            public static Passband Empty { get { return EmptyBand; } }

            /// <summary>Grid points carrying a fringe amplitude: the length of the per-pixel sum.</summary>
            public int FringingSampleCount { get { return wavelengthNm.Length; } }

            /// <summary>False for every filter that stops short of 774 nm, which is most of this roster.</summary>
            public bool CanFringe { get { return wavelengthNm.Length > 0 && weightTotal > 0.0; } }

            /// <summary>
            /// Samples a passband on AirglowTable's own 0.1 nm grid, which is fine enough to resolve
            /// both a 2.9 nm fringe period and the OH lines that drive it. Coarser sampling would
            /// alias the cosine against the line spectrum and produce a number with the right units
            /// and no meaning. Call this ONCE per capture.
            ///
            /// THE INDUCTION VARIABLE IS ACCUMULATED RATHER THAN RECONSTRUCTED, and that is
            /// load-bearing rather than careless. Rewriting the walk as min + k*step is the obvious
            /// tidy-up and it moves the physics: the drifting sum reaches Walsh et al.'s onset at
            /// 774.0000000000964, on the fringing side of it where the exact grid point is not, and
            /// Airglow's tables are BIN INTEGRALS addressed by a floor over 0.1 nm cells, so a
            /// sub-nanometre drift of either sign moves a sample into a different OH bin. This is
            /// the same walk, in the same order, on the same accumulator as the per-pixel form it
            /// replaces, deliberately, so that the two agree to the bit.
            /// </summary>
            public static Passband Sample(
                Func<double, double> skyRadiance, Func<double, double> response,
                double minWavelengthNm, double maxWavelengthNm, double stepNm)
            {
                if (skyRadiance == null || response == null) return EmptyBand;
                if (!(stepNm > 0.0)) return EmptyBand;
                if (!(maxWavelengthNm > minWavelengthNm)) return EmptyBand;

                int capacity = (int)((maxWavelengthNm - minWavelengthNm) / stepNm) + 2;
                if (capacity < 1) capacity = 1;
                var lambda = new double[capacity];
                var coef = new double[capacity];
                int count = 0;
                double total = 0.0;

                for (double l = minWavelengthNm; l <= maxWavelengthNm; l += stepNm)
                {
                    double w = skyRadiance(l) * response(l);
                    if (!(w > 0.0)) continue;
                    total += w;
                    double amplitude = MonochromaticPeakToPeak(l);
                    if (amplitude > 0.0)
                    {
                        if (count == lambda.Length)
                        {
                            Array.Resize(ref lambda, count * 2);
                            Array.Resize(ref coef, count * 2);
                        }
                        lambda[count] = l;
                        // (w * 0.5) * A, left to right, which is the association the per-pixel form
                        // evaluated before multiplying by the cosine. Reassociating it would be a
                        // different double and the equivalence test would stop being an equality.
                        coef[count] = w * 0.5 * amplitude;
                        count++;
                    }
                }

                if (!(total > 0.0)) return EmptyBand;
                Array.Resize(ref lambda, count);
                Array.Resize(ref coef, count);
                return new Passband(lambda, coef, total);
            }

            /// <summary>
            /// F(P) at one pixel's optical path: the sum above, with everything that does not depend
            /// on the pixel already done.
            ///
            /// THE PER-TERM DIVIDE STAYS A DIVIDE. Precomputing 2*pi/l per sample and multiplying is
            /// the obvious next hoist and it is about 15 per cent faster; it is also not the same
            /// double, differing on roughly two thirds of the realised paths at the 1e-16 level.
            /// That is far below anything physical, but it would turn a refactor whose output is
            /// provably identical into one that merely agrees closely, and the whole point of this
            /// change is that the frame a recorded seed reproduces did not move.
            /// </summary>
            public double ModulationAt(double opticalPathNm)
            {
                if (!(opticalPathNm > 0.0)) return 1.0;
                if (!(weightTotal > 0.0)) return 1.0;

                double weighted = 0.0;
                for (int k = 0; k < coefficient.Length; k++)
                    weighted += coefficient[k] * Math.Cos(2.0 * Math.PI * opticalPathNm / wavelengthNm[k]);

                return 1.0 + weighted / weightTotal;
            }
        }

        /// <summary>
        /// Peak-to-peak fringe amplitude in a finished broadband image, found by walking the
        /// optical path across one full fringe period and taking the range of the modulation.
        ///
        /// This is the number an observer measures off a frame, and the one ESO's manual quotes as
        /// "below 1%" for z_Gunn imaging against Walsh's monochromatic 7%. The two are the same
        /// detector and different measurements, and this method is the bridge between them.
        ///
        /// Sampled at 64 phases, which resolves a sinusoid to better than a tenth of a percent of
        /// its own amplitude and costs one pass of the integral above per phase.
        /// </summary>
        public static double BroadbandPeakToPeak(
            double thicknessMicrons, Func<double, double> skyRadiance, Func<double, double> response,
            double minWavelengthNm, double maxWavelengthNm, double stepNm, double referenceWavelengthNm)
        {
            double basePath = OpticalPathNm(thicknessMicrons, referenceWavelengthNm);
            if (!(basePath > 0.0)) return 0.0;

            // One full turn of phase at the reference wavelength, which is what a thickness change
            // of lambda / (2n) produces and what the fringe pattern repeats over.
            double pathPeriod = referenceWavelengthNm;

            // Sampled once for all sixty-four phases rather than once per phase. It is the same
            // band each time round, so this is the reassociation Passband exists for, applied to
            // the caller that reaches this integral most often after the map builder.
            Passband band = Passband.Sample(skyRadiance, response, minWavelengthNm, maxWavelengthNm, stepNm);

            const int Phases = 64;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < Phases; i++)
            {
                double path = basePath + pathPeriod * i / Phases;
                double m = band.ModulationAt(path);
                if (m < lo) lo = m;
                if (m > hi) hi = m;
            }
            return hi - lo;
        }

        private static double Interpolate(double[] xs, double[] ys, double x)
        {
            if (xs == null || ys == null || xs.Length == 0) return 0.0;
            if (x <= xs[0]) return ys[0];
            if (x >= xs[xs.Length - 1]) return ys[ys.Length - 1];
            for (int i = 1; i < xs.Length; i++)
            {
                if (x <= xs[i])
                {
                    double f = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                    return ys[i - 1] + f * (ys[i] - ys[i - 1]);
                }
            }
            return ys[ys.Length - 1];
        }
    }
}
