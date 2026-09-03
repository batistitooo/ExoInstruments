using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The two ways one pixel differs from its neighbours no matter how long you look at it:
    /// it converts light at its own rate, and it reads out from its own zero.
    ///
    /// WHY THIS EXISTS, WHICH IS NOT "MORE NOISE". Every stochastic term already in this pipeline
    /// (shot, read, dark) is TEMPORAL: draw a second frame and you get a different realisation, so
    /// stacking averages it down and no calibration frame can remove it. The two terms here are the
    /// opposite kind. They are FIXED properties of this particular piece of silicon, identical in
    /// every exposure it ever takes, so stacking does NOT average them down and a calibration frame
    /// DOES remove them exactly. That distinction is the entire reason an observer takes flats and
    /// biases, and until now this pipeline had nothing for either of them to remove:
    ///
    ///   * A BIAS frame measured a pedestal that was one constant over the whole array, so
    ///     subtracting it was arithmetically the same as subtracting a number.
    ///   * A FLAT frame could not exist at all, because the array's photo response was uniform to
    ///     machine precision. Dividing by a flat would have divided by 1.
    ///
    /// PHOTO-RESPONSE NON-UNIFORMITY (PRNU) is the multiplicative one: pixel i collects
    /// (1 + p_i) times the array's mean signal from the same illumination, because its quantum
    /// efficiency, its fill factor and its microlens are all manufactured to a tolerance. It scales
    /// with the light, so it is invisible in a dark and dominant in a bright flat, and it is removed
    /// by DIVISION.
    ///
    /// OFFSET FIXED-PATTERN NOISE is the additive one: pixel i reads out from a zero displaced by
    /// o_i electrons, because its own source follower and its column amplifier have their own
    /// offsets. It does not scale with anything, it is present in a zero-second exposure, and it is
    /// removed by SUBTRACTION. This is the quantity ESO's own FORS2 bias QC1 recipe isolates and
    /// trends as QC.BIAS.FPN, measuring it from the difference of two raw biases shifted by 10x10
    /// pixels with the read-noise contribution removed, precisely because it is the component of a
    /// bias frame that a shift does not move.
    ///
    /// BOTH ARE DRAWN ONCE PER SENSOR, from a fixed serial-number seed, exactly as the hot/dead
    /// pixel map already is. That is not an optimisation, it is the physics: if these maps were
    /// redrawn per exposure they would be temporal noise wearing a fixed pattern's name, a flat
    /// taken on Tuesday would not correct a light taken on Wednesday, and the calibration this
    /// whole file exists to make possible would silently do nothing.
    ///
    /// WHAT IS NOT MODELLED HERE, and is listed in section 12 rather than approximated:
    ///
    ///   * TREE RINGS and BRICK WALLS. A real flat is not white noise. Thick back-illuminated CCDs
    ///     show concentric rings from radial dopant variations laid down as the silicon ingot grew,
    ///     and laser-annealed devices show a periodic imprint of the anneal; Luo et al. (2024,
    ///     AJ 168, 251, arXiv:2401.14944) measure both on one such device, the rings falling from
    ///     1.6% peak-to-valley at 287nm to 0.7% at 947nm and the brick walls from 18% to below 0.5%
    ///     over the same span. Neither pattern is published for any detector in this roster, and
    ///     borrowing another device's rings would put a specific, visible, wrong structure into
    ///     every frame. The white component below is therefore the whole of the modelled PRNU, and
    ///     is stated as a floor rather than a full description.
    ///   * WAVELENGTH DEPENDENCE. The same paper shows PRNU is a function of wavelength, because
    ///     shorter-wavelength photons are absorbed nearer the back surface. The published EMVA
    ///     figure used here is a single broadband number, so the model carries no colour term.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class SensorNonUniformity
    {
        /// <summary>
        /// PRNU of a read-out pixel that sums n x n pixels of the underlying sensor.
        ///
        /// The read-out pixel's response is the MEAN of the n^2 responses it merges, so their
        /// independent deviations average down as 1/n: a 2x2 binned pixel is twice as uniform as
        /// the silicon it is made of. This is not a modelling choice, it follows from binning being
        /// a sum of independent draws over a signal that is also summed.
        ///
        /// It matters immediately rather than theoretically, because the amateur camera on this
        /// roster is ALREADY BINNED at what the catalogue calls its native resolution: the
        /// ASI294MM Pro's 4144x2822 at 4.63um is the IMX492's 8288x5644 at 2.315um, summed 2x2 in
        /// the sensor. Its full well is the arithmetic proof, at four times the single pixel's.
        /// </summary>
        public static double BinnedPhotoResponseSigma(double nativeSigmaFraction, int nativePixelsPerSide)
        {
            if (!(nativeSigmaFraction > 0.0) || double.IsNaN(nativeSigmaFraction)) return 0.0;
            if (nativePixelsPerSide < 1) return nativeSigmaFraction;
            return nativeSigmaFraction / nativePixelsPerSide;
        }

        /// <summary>
        /// Offset fixed-pattern noise of a read-out pixel that sums n x n pixels of the underlying
        /// sensor, in electrons.
        ///
        /// The opposite scaling to PRNU above, and for the same reason: this one is an ADDITIVE
        /// per-pixel quantity, so binning SUMS n^2 independent offsets and their standard deviation
        /// grows as n rather than falling as 1/n. A 2x2 binned pixel carries twice the offset spread
        /// of a single one while carrying half its response spread.
        /// </summary>
        public static double BinnedOffsetSigmaElectrons(double nativeSigmaElectrons, int nativePixelsPerSide)
        {
            if (!(nativeSigmaElectrons > 0.0) || double.IsNaN(nativeSigmaElectrons)) return 0.0;
            if (nativePixelsPerSide < 1) return nativeSigmaElectrons;
            return nativeSigmaElectrons * nativePixelsPerSide;
        }

        /// <summary>
        /// The per-pixel photo-response deviations p_i, mean 0 and standard deviation sigma, for a
        /// frame of the given size.
        ///
        /// Gaussian rather than log-normal. EMVA 1288 defines PRNU as the spatial standard deviation
        /// of a dark-corrected flat about its own mean, which is a statement about the second moment
        /// and not about the shape; at the sub-percent widths every device here reports, the two
        /// distributions differ by less than the quantity itself, and the Gaussian is the one whose
        /// parameter IS the published number.
        ///
        /// Stored as the DEVIATION rather than as the gain 1 + p, and as Float16 rather than float.
        /// Half-precision holds a relative 4.9e-4, which on a deviation of order 3e-3 is 1.5e-6 of
        /// absolute error and therefore nothing; on the gain itself, where the same relative
        /// precision applies to a value near 1, it would have been 4.9e-4, or 16% of the sigma being
        /// represented. Same storage, and the difference between exact and useless.
        /// </summary>
        public static ushort[] BuildPhotoResponseMap(ulong sensorSerialSeed, int pixelCount, double sigma)
            => BuildDeviationMap(sensorSerialSeed, Pcg32.StreamPhotoResponse, pixelCount, sigma);

        /// <summary>
        /// The per-pixel readout offsets o_i in electrons, mean 0 and standard deviation sigma. Same storage
        /// argument as the photo-response map above.
        /// </summary>
        public static ushort[] BuildOffsetMap(ulong sensorSerialSeed, int pixelCount, double sigmaElectrons)
            => BuildDeviationMap(sensorSerialSeed, Pcg32.StreamOffsetFpn, pixelCount, sigmaElectrons);

        // A zero-mean Gaussian deviation per pixel, packed to half precision. The draws are made in index order
        // from one generator on its own stream, so the map is a function of the seed alone: the same silicon on
        // any machine, any runtime and any session, which is what makes a stored master flat or master bias
        // meaningful across them. The sample mean is subtracted afterwards. A finite draw of N samples has a
        // mean of order sigma/sqrt(N) rather than exactly zero, and leaving it in would put a constant scale
        // error into every frame that no flat could remove, since the flat carries the same error. It is a
        // small number (at 11.7 megapixels and 0.31%, about 1e-6), and removing it costs one pass; the point is
        // that the map's mean is then exactly the value the physics says it is rather than approximately it.
        private static ushort[] BuildDeviationMap(ulong sensorSerialSeed, ulong stream, int pixelCount, double sigma)
        {
            if (pixelCount <= 0) return null;
            var map = new ushort[pixelCount];
            if (!(sigma > 0.0) || double.IsNaN(sigma)) return map;   // Float16 zero is 0x0000

            var rng = new Pcg32(sensorSerialSeed, stream);
            var draws = new double[pixelCount];
            double sum = 0.0;
            for (int i = 0; i < pixelCount; i++)
            {
                double d = NoiseSampler.Gaussian(rng, sigma);
                draws[i] = d;
                sum += d;
            }

            double mean = sum / pixelCount;
            for (int i = 0; i < pixelCount; i++)
                map[i] = Float16.FromDouble(draws[i] - mean);

            return map;
        }

        /// <summary>
        /// The gain multiplier of pixel i: 1 + p_i, from a map built above. Returns 1 for a null map, which is
        /// what "this device's PRNU is not published" has to mean.
        /// </summary>
        public static float PhotoResponse(ushort[] map, int index)
        {
            if (map == null || index < 0 || index >= map.Length) return 1f;
            return 1f + (float)Float16.ToDouble(map[index]);
        }

        /// <summary>The readout offset of pixel i in electrons. Returns 0 for a null map.</summary>
        public static float OffsetElectrons(ushort[] map, int index)
        {
            if (map == null || index < 0 || index >= map.Length) return 0f;
            return (float)Float16.ToDouble(map[index]);
        }
    }
}
