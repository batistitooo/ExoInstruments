using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Moving the telescope a little between exposures, and why that is a measurement technique
    /// rather than a fidget.
    ///
    /// WHAT STACKING CAN AND CANNOT DO. Averaging N frames divides temporal noise by sqrt(N) and
    /// does nothing at all to anything fixed to the detector. Section 7.51's calibration removes the
    /// fixed terms it can MODEL - the photo response, the offsets, the dark - and what is left over
    /// is the part of them the calibration got wrong: the shot noise on the master flat, the
    /// pixels the bad-pixel map missed, the fringe pattern that changed with the airglow between
    /// the science frame and the sky frame. Those residuals are still fixed to the detector, so
    /// they still do not average down, and on a deep stack they are what limits it.
    ///
    /// DITHERING BREAKS THE REGISTRATION. Offset the telescope by a few pixels between subs, and a
    /// patch of SKY lands on different silicon each time. Align on the sky before averaging and the
    /// sky adds coherently while the detector's residuals are smeared over as many different pixels
    /// as there are dither positions, so they now average down as 1/sqrt(N) like everything else.
    /// Nothing about the detector changed; what changed is that the two things being separated stop
    /// sharing a coordinate system.
    ///
    /// This is why every survey dithers, why HST and JWST specify dither patterns as part of an
    /// observing mode rather than as an option, and why an undithered stack of a hundred frames can
    /// be worse than a dithered stack of ten.
    ///
    /// NO INVENTED PARAMETERS. A dither pattern is a choice an observer makes, not a property of an
    /// instrument, so nothing here is sourced from a datasheet and nothing needs to be: the
    /// patterns below are the standard geometric ones, and the only physical input is the amplitude
    /// the observer picks. What IS a property of the instrument, and what this file deliberately
    /// does not model, is the mount's own pointing error (see section 12).
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class DitherPattern
    {
        /// <summary>How the telescope is moved between subs.</summary>
        public enum Kind
        {
            /// <summary>Not moved. Every sub lands on the same silicon, and no detector residual averages down.</summary>
            None,

            /// <summary>
            /// A spiral of increasing radius, one step per sub. The pattern most amateur
            /// acquisition software offers by default, and the one that keeps the field within a
            /// bounded region however long the sequence runs.
            /// </summary>
            Spiral,

            /// <summary>
            /// Positions drawn uniformly inside a disc of the given amplitude. Better than a
            /// regular pattern at breaking up periodic detector structure, because a regular
            /// pattern can land on a period of its own and resonate with the very thing it is
            /// meant to smear.
            /// </summary>
            Random,
        }

        /// <summary>
        /// Where the telescope should point for sub number i, as an offset in pixels from the
        /// nominal pointing.
        ///
        /// The spiral is the standard square spiral in units of the step: it visits the centre,
        /// then the ring of eight positions around it, then the twenty-four around those, and so
        /// on, so a sequence of any length is a compact, evenly covered pattern rather than an
        /// arbitrary walk. The random pattern draws from a fixed seed mixed with the sub index, so
        /// a sequence is reproducible and a re-run lands on the same positions.
        /// </summary>
        public static void OffsetForSub(Kind kind, int subIndex, double amplitudePixels,
                                        ulong seed, out double dx, out double dy)
        {
            dx = 0.0; dy = 0.0;
            if (kind == Kind.None || !(amplitudePixels > 0.0) || subIndex < 0) return;

            if (kind == Kind.Random)
            {
                var rng = new Pcg32(Pcg32.MixSeed((long)seed, subIndex), Pcg32.StreamDither);
                // Uniform over the DISC, which needs the square root: drawing the radius uniformly
                // would concentrate the positions at the centre, where they overlap most and smear
                // least.
                double r = amplitudePixels * Math.Sqrt(rng.NextDouble());
                double theta = 2.0 * Math.PI * rng.NextDouble();
                dx = r * Math.Cos(theta);
                dy = r * Math.Sin(theta);
                return;
            }

            // Square spiral: ring 0 is the origin, ring k has 8k positions.
            if (subIndex == 0) return;
            int ring = 1;
            int index = subIndex - 1;
            while (index >= 8 * ring) { index -= 8 * ring; ring++; }

            int side = index / (2 * ring);
            int along = index % (2 * ring);
            int ix, iy;
            switch (side)
            {
                case 0: ix = ring; iy = -ring + along; break;
                case 1: ix = ring - along; iy = ring; break;
                case 2: ix = -ring; iy = ring - along; break;
                default: ix = -ring + along; iy = -ring; break;
            }
            dx = ix * amplitudePixels;
            dy = iy * amplitudePixels;
        }

        /// <summary>
        /// How many distinct detector pixels a given sky pixel visits over a sequence, which is the
        /// number that decides how far the detector's residuals average down.
        ///
        /// Counted from the integer part of the offsets, because a residual fixed to pixel (i,j) is
        /// smeared over as many pixels as the sequence lands the same sky on, and a sub-pixel
        /// difference lands on the same pixel. Returns 1 for no dither, which is the honest answer:
        /// nothing averages down.
        /// </summary>
        public static int DistinctPositions(Kind kind, int subCount, double amplitudePixels, ulong seed)
        {
            if (kind == Kind.None || subCount <= 0) return 1;

            var seen = new System.Collections.Generic.HashSet<long>();
            for (int i = 0; i < subCount; i++)
            {
                double dx, dy;
                OffsetForSub(kind, i, amplitudePixels, seed, out dx, out dy);
                long key = ((long)Math.Round(dx) << 32) ^ (uint)(int)Math.Round(dy);
                seen.Add(key);
            }
            return Math.Max(1, seen.Count);
        }

        /// <summary>
        /// The factor by which a detector-fixed residual is reduced by dithering a sequence, as an
        /// amplitude rather than a variance.
        ///
        /// A residual that lands on k distinct pixels is averaged over k independent values of
        /// itself, so its amplitude falls as 1/sqrt(k). That is the whole benefit, and it is capped
        /// by the number of positions rather than by the number of subs: a hundred frames at four
        /// dither positions buys a factor of two, not of ten.
        /// </summary>
        public static double ResidualSuppression(int distinctPositions)
            => distinctPositions <= 1 ? 1.0 : 1.0 / Math.Sqrt(distinctPositions);
    }
}
