using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Why a bright star is wider than a faint one on the same detector, and why a photon transfer
    /// curve lies about the gain.
    ///
    /// THE MECHANISM. Charge already collected in a pixel's well sits there as a space charge, and
    /// it repels electrons arriving afterwards. The pixel's effective collecting area therefore
    /// SHRINKS as it fills, and its neighbours' grow by the same amount. Two consequences follow
    /// and they look nothing alike:
    ///
    ///   * A point source's core, which is where the charge is, pushes its own light outward. A
    ///     bright star is measurably broader than a faint one imaged through the same optics, which
    ///     is the effect's name and the reason precision photometry and weak-lensing shape
    ///     measurement both have to correct for it.
    ///   * In a FLAT FIELD, a fluctuation up in one pixel pushes charge into its neighbours, so
    ///     neighbouring pixels become positively correlated and the pixel-to-pixel variance falls
    ///     below Poisson. Since the photon transfer curve derives the conversion gain as
    ///     signal/variance, a suppressed variance means an OVER-ESTIMATED gain, and every quantity
    ///     computed from that gain - read noise, dark current, quantum efficiency, full well -
    ///     inherits the error.
    ///
    /// WHAT SECTION 12 USED TO SAY, AND WHY IT WAS WRONG. This effect was declared not implemented
    /// on the grounds that "its real formula needs per-sensor electrostatic-vertex calibration
    /// tables (e2v/ITL-specific) with no generic published values", and that "none of these
    /// instruments do stellar photometry". Both clauses have since become false. The second stopped
    /// being true when the pipeline gained measured aperture photometry. The first was never quite
    /// true: ESO measured the effect directly, by spatial autocorrelation, on their own detectors,
    /// and published the numbers in prose.
    ///
    /// Downing, Baade, Sinclaire, Deiries and Christen (2006, SPIE Orlando, "CCD riddle: a) signal
    /// vs time: linear; b) signal vs variance: non-linear") report, for an e2v CCD44-82 at about
    /// 90 ke- in a flat field: nearest-neighbour correlation of 1.4% horizontally and 2.2%
    /// vertically, and a SUMMED correlation over all neighbours of 10%. They also state the
    /// consequence outright, that the summed correlation "results in over estimating the gain of
    /// the system by 10%", and demonstrate that using an autocorrelation variance instead recovers
    /// a gain independent of signal level.
    ///
    /// The anisotropy is structural rather than noise: a pixel is bounded in x by channel stops and
    /// in y by the electric fields of the clock lines, so the two directions have different
    /// stiffness, and the paper says so.
    ///
    /// WHAT IS STILL NOT AVAILABLE FOR THIS ROSTER, which is why every instrument here carries NaN.
    /// The same paper tested three devices, two e2v CCD44-82 and the MIT/LL CCID-20 that FORS2
    /// actually uses. The autocorrelation analysis is reported for the e2v alone; the CCID-20
    /// appears in the linearity and photon-transfer sections and nowhere after. So the mechanism is
    /// now modelled and validated against the one device it is published for, and the amplitude for
    /// every instrument on this roster remains unpublished. That is a different statement from the
    /// one section 12 used to make, and a better one: the model is here and waiting for a number,
    /// rather than absent because a number was believed not to exist.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class BrighterFatter
    {
        /// <summary>
        /// The one device this effect is published for: e2v CCD44-82, from Downing et al. (2006).
        /// Nearest-neighbour correlation coefficients in a flat field, and the signal they were
        /// measured at.
        ///
        /// Carried as a named reference point rather than as a default. Nothing on this roster is
        /// this detector, and using these numbers for one that is not would be borrowing another
        /// device's electrostatics; what they are for is validating the model, which
        /// tools/detector-tests does by reproducing both this pair and the summed 10% from them.
        /// </summary>
        public const double Ccd4482HorizontalCorrelation = 0.014;
        public const double Ccd4482VerticalCorrelation = 0.022;
        public const double Ccd4482SummedCorrelation = 0.10;
        public const double Ccd4482ReferenceSignalElectrons = 90000.0;

        /// <summary>
        /// The area coefficient a, in inverse electrons: the fractional change in a pixel's
        /// collecting area per electron held by its neighbour.
        ///
        /// DERIVED FROM THE MEASURED CORRELATION RATHER THAN FITTED. Write the measured charge as
        /// Q'_i = Q_i (1 + sum_j a_ij Q_j), which is the standard area formulation (Antilogus et al.
        /// 2014; Guyonnet et al. 2015) with sum_j a_ij = 0 for conservation. For a flat of mean Qbar
        /// and Poisson fluctuations, the covariance between neighbours to first order is
        /// 2 a Qbar Var, so the measured correlation coefficient is
        ///
        ///     R = 2 a Qbar        hence        a = R / (2 Qbar)
        ///
        /// At the published 1.4% and 90 ke- that is 7.8e-8 per electron, which is the order every
        /// published brighter-fatter coefficient sits at, and the agreement is a check rather than
        /// a coincidence: the two were derived from unrelated measurements.
        /// </summary>
        public static double AreaCoefficient(double correlation, double signalElectrons)
        {
            if (!(signalElectrons > 0.0) || double.IsNaN(correlation)) return 0.0;
            return correlation / (2.0 * signalElectrons);
        }

        /// <summary>The inverse: what neighbour correlation a given area coefficient produces at a given signal.</summary>
        public static double CorrelationAtSignal(double areaCoefficient, double signalElectrons)
            => 2.0 * areaCoefficient * signalElectrons;

        /// <summary>
        /// By how much a photon transfer curve over-estimates the conversion gain, as a factor.
        ///
        /// The consequence Downing et al. state and the reason the effect matters to a simulator
        /// that never images a star. Charge shared with neighbours is charge removed from a pixel's
        /// own variance without being removed from its mean, so the simple variance under-reports
        /// and signal/variance over-reports by exactly the summed correlation:
        ///
        ///     Gain_simple / Gain_true = 1 + sum of correlations over all neighbours
        ///
        /// Their summed 10% therefore predicts a 10% over-estimate, which is what they measure.
        /// Any conversion factor in a catalogue that came from a photon transfer curve carries this
        /// bias unless its measurer used an autocorrelation variance.
        /// </summary>
        public static double PhotonTransferGainBias(double summedCorrelation)
            => 1.0 + Math.Max(0.0, summedCorrelation);

        /// <summary>
        /// Redistributes charge between neighbouring pixels, in place.
        ///
        /// A SYMMETRIC FLUX ACROSS EACH BOUNDARY, which is the only form that conserves charge.
        /// The obvious implementation, scaling each pixel by its own area change
        /// Q'_i = Q_i (1 + sum_j a_ij (Q_j - Q_i)), is the textbook area formulation and it does
        /// NOT conserve: summing it over a neighbouring pair leaves
        /// a(Q_j - Q_i)Q_i + a(Q_i - Q_j)Q_j = -a(Q_i - Q_j)^2, which is negative definite, so the
        /// array quietly loses charge in proportion to its own variance. That was measured before
        /// it was reasoned about: the harness read a 2e-4 deficit on a flat and the algebra
        /// explained it afterwards.
        ///
        /// The physical statement is about a BOUNDARY, not a pixel. Charge on one side pushes the
        /// boundary toward the other, and what crosses is a flux:
        ///
        ///     F = a (Q_i - Q_j) (Q_i + Q_j)/2
        ///
        /// the difference setting which way and how hard the boundary moves, the mean setting how
        /// much charge density there is at it to be moved. Subtracting F from one pixel and adding
        /// it to the other conserves exactly, by construction rather than by cancellation.
        ///
        /// It reproduces the same first-order correlation: for a flat of mean Qbar the flux is
        /// a Qbar times the fluctuation difference, so the operator is [c, 1-2c, c] with c = a Qbar,
        /// whose neighbour correlation is 2c = 2 a Qbar, which is the relation AreaCoefficient
        /// inverts. And unlike the area form it behaves correctly on a point source, where the
        /// local density that scales the flux is the star's own rather than the array's mean.
        ///
        /// FIRST ORDER, AND THAT IS A REAL LIMIT rather than a simplification of convenience. The
        /// expansion is in a*Q, which at the published coefficient and a full well of 100 ke- is
        /// about 0.8%, so second-order terms are of order 6e-5 and negligible; a device with a
        /// larger coefficient, or a pixel driven far past full well, is outside what this
        /// describes. Blooming has already redistributed such a pixel's charge by the time this
        /// would run.
        /// </summary>
        public static void Apply(float[] charge, int width, int height,
                                 double horizontalCoefficient, double verticalCoefficient)
        {
            if (charge == null || width < 3 || height < 3) return;
            if (!(horizontalCoefficient > 0.0) && !(verticalCoefficient > 0.0)) return;

            var original = new float[charge.Length];
            Array.Copy(charge, original, charge.Length);

            var delta = new double[charge.Length];

            // Each boundary visited once, so each flux is applied exactly twice with opposite
            // signs. Visiting from both sides instead would double the effect and still conserve,
            // which is the failure mode this loop structure exists to make impossible.
            if (horizontalCoefficient > 0.0)
            {
                for (int y = 0; y < height; y++)
                    for (int x = 0; x + 1 < width; x++)
                    {
                        int i = y * width + x, j = i + 1;
                        double qi = original[i], qj = original[j];
                        double flux = horizontalCoefficient * (qi - qj) * 0.5 * (qi + qj);
                        delta[i] -= flux;
                        delta[j] += flux;
                    }
            }
            if (verticalCoefficient > 0.0)
            {
                for (int y = 0; y + 1 < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x, j = i + width;
                        double qi = original[i], qj = original[j];
                        double flux = verticalCoefficient * (qi - qj) * 0.5 * (qi + qj);
                        delta[i] -= flux;
                        delta[j] += flux;
                    }
            }

            for (int i = 0; i < charge.Length; i++) charge[i] = (float)(original[i] + delta[i]);
        }

        /// <summary>
        /// How much wider a Gaussian source of the given peak charge appears, as a fraction of its
        /// own width. The effect's namesake, in closed form and exact to first order.
        ///
        /// THE DERIVATION, because the answer has a factor in it that guessing does not produce.
        /// The symmetric flux above is F = a (Q_i - Q_j)(Q_i + Q_j)/2, whose divergence for a smooth
        /// profile is
        ///
        ///     dQ = (a/2) d2(Q^2)/dx2
        ///
        /// so the charge moved is set by the Laplacian of the SQUARE of the profile, not of the
        /// profile. Integrating that against x^2 and twice by parts, the second moment gains
        ///
        ///     dM2 = a * (integral of Q^2) / (integral of Q)
        ///
        /// with the denominator unchanged because the redistribution conserves charge. For a
        /// two-dimensional Gaussian of peak P and width s, those integrals are P^2 pi s^2 and
        /// 2 pi s^2 P, so their ratio is P/2 and
        ///
        ///     d(s^2) = a P / 2        hence        ds/s = a P / (4 s^2)
        ///
        /// THE FOUR IS THE PART WORTH KEEPING. The obvious answers are a*P, which is
        /// dimensionally wrong, and a*P/(2 s^2), which is the one-dimensional kernel argument
        /// applied without the two-dimensional normalisation. This code carried the second of
        /// those, and tools/detector-tests measured the true growth at a ratio of 0.50 to it, at
        /// three different brightnesses and to two decimals, which is not noise and is exactly the
        /// missing factor of two.
        /// </summary>
        public static double FractionalWidthIncrease(double peakChargeElectrons, double areaCoefficient,
                                                     double sigmaPixels)
        {
            if (!(peakChargeElectrons > 0.0) || !(areaCoefficient > 0.0)) return 0.0;
            if (!(sigmaPixels > 0.0)) return 0.0;
            return areaCoefficient * peakChargeElectrons / (4.0 * sigmaPixels * sigmaPixels);
        }
    }
}
