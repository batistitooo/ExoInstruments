using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The three things an HgCdTe infrared array does that a CCD does not, beyond persistence
    /// (which is Core.HgCdTePersistence): interpixel capacitance, count-rate non-linearity, and a
    /// read noise set by how many times the ramp was sampled rather than by one destructive read.
    ///
    /// WHY THIS IS A SEPARATE CHAIN AND NOT A SET OF EXTRA PARAMETERS. An infrared array is not a
    /// CCD with different numbers in it. Each pixel has its own amplifier and is read where it
    /// sits, so there is no shift register, and three things follow that are not adjustments but
    /// absences:
    ///
    ///   * NO CHARGE TRANSFER, therefore no CTI. The WFC3 Instrument Handbook says so directly:
    ///     "IR detectors show minimal long-term on-orbit CTE degradation". There is nothing to be
    ///     inefficient at, because the charge never moves.
    ///   * NO BLOOMING. Again the handbook, on the IR detector: there is "no charge bleeding at
    ///     saturation". A CCD's full well overflows into its neighbours along the column because
    ///     that is the direction charge is clocked; a photodiode that is full simply stops
    ///     collecting.
    ///   * NO DESTRUCTIVE READ. The array is sampled non-destructively up the ramp (MULTIACCUM), so
    ///     the effective read noise falls as the number of samples rises, and "read noise" is not
    ///     one number for the device.
    ///
    /// Running such a detector through the CCD chain would apply two effects it does not have and
    /// use a read noise that does not describe it.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class InfraredArray
    {
        // ---------------------------------------------------------------- interpixel capacitance

        /// <summary>
        /// The measured interpixel-capacitance kernel of the WFC3/IR array, from Hilbert &amp;
        /// McCullough (2011, WFC3 ISR 2011-10, "Interpixel Capacitance in the IR Channel:
        /// Measurements Made On Orbit"), Table 2, measured on the SPARS200 dark-current reference
        /// file using high signal-to-noise hot pixels:
        ///
        ///     0.0011  0.0127  0.0011
        ///     0.0163  0.9360  0.0164
        ///     0.0011  0.0127  0.0011
        ///
        /// with uncertainties 0.0006 on the corners, 0.0009-0.0010 above and below, 0.0011-0.0014
        /// left and right, and 0.0045 on the centre.
        ///
        /// WHAT IT IS, AND WHAT IT IS NOT. IPC is a CAPACITIVE coupling between neighbouring pixels'
        /// sense nodes: a signal in one pixel raises the apparent signal in its neighbours without
        /// any charge moving. It is therefore not charge diffusion and not brighter-fatter, which
        /// both move real electrons; it is crosstalk at the readout, applied to the signal as
        /// measured, and it is linear. That is why it is a fixed convolution and not a
        /// signal-dependent one.
        ///
        /// THE ANISOTROPY IS MEASURED AND IS KEPT. The report finds the coupling identical above and
        /// below the central pixel (0.0127 both), identical left and right (0.0163 and 0.0164), and
        /// the two pairs different from each other. Averaging them into one number would discard a
        /// difference the measurement resolves at four sigma.
        ///
        /// THE KERNEL SUMS TO 0.9985, not to 1, and this is the published sum: the report notes the
        /// missing 0.15% "is comparable to the signal measured in one of the 4 corner pixels and
        /// also the uncertainty in the 4 adjacent pixels". It is NOT renormalised here. Forcing the
        /// sum to unity would be asserting a conservation the measurement does not quite show, and
        /// silently changing five published numbers to do it.
        ///
        /// INDEPENDENTLY CORROBORATED: Seshadri et al. (2008) measured a very similar HgCdTe device
        /// by resetting individual pixels to known voltages and found 1.4-1.55% in the four adjacent
        /// pixels and 0.13% in the corners, against this report's 1.27-1.64% and 0.11%.
        /// </summary>
        public static readonly double[,] Wfc3IrKernel =
        {
            { 0.0011, 0.0127, 0.0011 },
            { 0.0163, 0.9360, 0.0164 },
            { 0.0011, 0.0127, 0.0011 },
        };

        /// <summary>Published sum of the kernel above, stated in the report itself.</summary>
        public const double Wfc3IrKernelSum = 0.9985;

        /// <summary>Total IPC, i.e. the fraction of a pixel's signal that appears in its neighbours: 1 - centre.</summary>
        public const double Wfc3IrTotalCoupling = 0.064;

        /// <summary>
        /// Applies an interpixel-capacitance kernel to a frame, in place.
        ///
        /// EDGE HANDLING IS REPLICATION, not zero-padding. A pixel at the array edge has a real
        /// neighbour outside the frame in the real detector - the reference-pixel border - and
        /// treating it as empty would darken the border by the coupling fraction, putting a
        /// one-pixel artefact around every frame that no real image has.
        /// </summary>
        public static void ApplyCoupling(float[] frame, int width, int height, double[,] kernel)
        {
            if (frame == null || width <= 0 || height <= 0) return;
            if (kernel == null || kernel.GetLength(0) != 3 || kernel.GetLength(1) != 3) return;

            var source = (float[])frame.Clone();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double sum = 0.0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        // A CONVOLUTION, NOT A CORRELATION: the source is read at MINUS the offset.
                        //
                        // The report defines each kernel cell as "the normalized fraction of
                        // measured signal associated with a single pixel source located in the
                        // central pixel", so kernel[1+dr, 1+dc] is what a source contributes to the
                        // pixel dr rows and dc columns AWAY from it. The measured value at (y, x) is
                        // therefore the sum over sources at (y-dr, x-dc), which is the convolution.
                        //
                        // Reading the source at +dy, +dx instead flips the kernel, and on a kernel
                        // this nearly symmetric the difference is the 0.0001 between the left and
                        // right couplings - invisible in a frame and wrong in principle. It was
                        // written that way first and tools/infrared-tests caught it by asserting
                        // the point-source response cell by cell.
                        int sy = Clamp(y - dy, 0, height - 1);
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sx = Clamp(x - dx, 0, width - 1);
                            sum += kernel[dy + 1, dx + 1] * source[sy * width + sx];
                        }
                    }
                    frame[y * width + x] = (float)sum;
                }
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // ---------------------------------------------------------------- count-rate non-linearity

        /// <summary>
        /// WFC3/IR's count-rate non-linearity, also called reciprocity failure: the measured flux of
        /// a source is not a linear function of the true flux, so a faint source loses a fixed
        /// fraction of its counts per decade of flux below the level the calibration was anchored
        /// at.
        ///
        /// 0.75% +/- 0.06% PER DEX, WITH NO APPARENT WAVELENGTH DEPENDENCE, measured across 16
        /// astronomical magnitudes. Riess, Narayan &amp; Calamida (2019, WFC3 ISR 2019-01,
        /// "Calibration of the WFC3-IR Count-rate Non-linearity, Sub-percent Accuracy for a Factor
        /// of a Million in Flux") reach this by combining cluster-star photometry between WFC3-IR
        /// and WFC3-UVIS, observed and synthetic magnitudes of white dwarfs, and ground-to-orbit
        /// comparisons of LMC and Milky Way Cepheids, with all previous measurements and the WFC3
        /// grism.
        ///
        /// It is an order of magnitude smaller than NICMOS's, which the same report gives as 3% to
        /// 10% per dex, and it is included here for the reason that report gives for measuring it:
        /// at 1% per dex the effect reaches ~0.06 mag between a Milky Way star at 1 kpc and its
        /// extragalactic equivalent at 1 Mpc, which is the difference between a 1% Hubble constant
        /// and no such thing.
        /// </summary>
        public const double Wfc3IrCountRateNonLinearityPerDex = 0.0075;
        public const double Wfc3IrCountRateNonLinearityUncertaintyPerDex = 0.0006;

        /// <summary>
        /// The measured count rate, given the true one, for a device with the given non-linearity
        /// slope anchored at <paramref name="referenceRateElectronsPerSecond"/>.
        ///
        ///     measured = true * (1 + slope * log10(true / reference))
        ///
        /// so a source a decade fainter than the anchor is measured 0.75% low, one two decades
        /// fainter 1.5% low, and the anchor itself is unchanged by construction.
        ///
        /// THE ANCHOR IS THE PART THAT NEEDS DECLARING, and it is declared rather than hidden. The
        /// slope is measured to sub-percent accuracy; the flux level it is measured RELATIVE to is
        /// set by where the photometric zero point was established, and ISR 2019-01 states the
        /// convention rather than a number - "flux zeropoints are established from standard stars
        /// which are about ten astronomical magnitudes (4 dex) brighter than faint, sky-dominated
        /// targets". This function therefore takes the anchor as an argument, the instrument
        /// declares it, and section 12 records that it is the one unpinned constant in this chain.
        /// Nothing about the SHAPE of the effect depends on it; it sets where zero correction sits.
        /// </summary>
        public static double MeasuredRate(
            double trueRateElectronsPerSecond,
            double referenceRateElectronsPerSecond,
            double slopePerDex)
        {
            if (!(trueRateElectronsPerSecond > 0.0)) return 0.0;
            if (!(referenceRateElectronsPerSecond > 0.0)) return trueRateElectronsPerSecond;
            if (!(slopePerDex != 0.0) || double.IsNaN(slopePerDex)) return trueRateElectronsPerSecond;

            double dex = Math.Log10(trueRateElectronsPerSecond / referenceRateElectronsPerSecond);
            double factor = 1.0 + slopePerDex * dex;

            // The linear-in-log form is a calibration fit over 16 magnitudes, not a law valid to
            // arbitrarily faint flux: extended far enough below the anchor it reaches zero and then
            // turns negative. Clamped at zero, which cannot be reached over any range this
            // instrument observes (it would take 133 dex) and which keeps the function total.
            return trueRateElectronsPerSecond * Math.Max(0.0, factor);
        }

        // ---------------------------------------------------------------- up the ramp

        /// <summary>
        /// The effective read noise of a MULTIACCUM ramp sampled <paramref name="reads"/> times,
        /// interpolated between the two published points.
        ///
        /// WFC3 Instrument Handbook 5.7 gives, for a SPARS200 ramp: about 20.0 e- with 2 reads plus
        /// the zeroth, and about 12.0 e- with 15 reads plus the zeroth; and correlated double
        /// sampling alone at 20.2-21.4 e-. Fitting to multiple reads is what reduces the net
        /// effective read noise, and the handbook says that in those words.
        ///
        /// INTERPOLATED IN 1/SQRT(N), NOT LINEARLY IN N. Averaging N independent samples of a
        /// noise reduces it as 1/sqrt(N), and that is the shape the two published points are
        /// interpolated along; a straight line in N between them would be a curve of no physical
        /// origin that happens to pass through both. Checked against the two anchors themselves in
        /// tools/infrared-tests, which is all that can be checked, since the handbook publishes two
        /// points and not a curve.
        ///
        /// Clamped outside [2, 15] reads, which is the range NSAMP actually spans.
        /// </summary>
        public static double EffectiveReadNoiseElectrons(
            int reads, double noiseAtFewReads, int fewReads, double noiseAtManyReads, int manyReads)
        {
            if (manyReads <= fewReads) return noiseAtFewReads;

            int n = reads < fewReads ? fewReads : (reads > manyReads ? manyReads : reads);

            double a = 1.0 / Math.Sqrt(fewReads);
            double b = 1.0 / Math.Sqrt(manyReads);
            double x = 1.0 / Math.Sqrt(n);

            double f = (x - a) / (b - a);
            return noiseAtFewReads + f * (noiseAtManyReads - noiseAtFewReads);
        }

        /// <summary>WFC3/IR's two published ramp read-noise points; see EffectiveReadNoiseElectrons.</summary>
        public const double Wfc3IrReadNoiseTwoReadsElectrons = 20.0;
        public const int Wfc3IrTwoReads = 2;
        public const double Wfc3IrReadNoiseFifteenReadsElectrons = 12.0;
        public const int Wfc3IrFifteenReads = 15;

        /// <summary>Correlated double sampling alone, the range WFC3 IHB 5.7 quotes.</summary>
        public const double Wfc3IrCdsReadNoiseLowElectrons = 20.2;
        public const double Wfc3IrCdsReadNoiseHighElectrons = 21.4;
    }
}
