using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// PCG-XSH-RR 64/32: the permuted congruential generator of O'Neill (2014, Harvey Mudd College
    /// technical report HMC-CS-2014-0905, "PCG: A Family of Simple Fast Space-Efficient
    /// Statistically Good Algorithms for Random Number Generation"), transcribed from the author's
    /// own reference implementation at pcg-random.org.
    ///
    /// WHY THE PIPELINE NEEDED ITS OWN GENERATOR. Every stochastic step in this mod -- shot noise,
    /// read noise, cosmic rays, the defect map, scintillation -- ran on System.Random. That is a
    /// subtractive lagged-Fibonacci generator whose statistical weaknesses are well known, but the
    /// disqualifying problem is a different one: ITS OUTPUT IS NOT PART OF .NET'S CONTRACT. The
    /// sequence produced for a given seed is not guaranteed across runtime versions, and it in
    /// fact changed between .NET Framework and .NET Core. A simulation whose output cannot be
    /// reproduced from a recorded seed cannot be regression-tested at all, which blocks every
    /// check that would otherwise pin this pipeline's behaviour down.
    ///
    /// PCG fixes exactly that and nothing else is claimed for it: the algorithm is published, the
    /// reference implementation is fixed, the sequence for a seed is therefore stable forever and
    /// across platforms, and it passes TestU01's BigCrush battery, which System.Random does not.
    /// It is also smaller and faster: 64 bits of state, one multiply-add and one rotate per draw.
    ///
    /// DELIBERATELY A System.Random SUBCLASS. Every method the pipeline uses (Next, Next(int),
    /// NextDouble, NextBytes, and the protected Sample the base class routes through) is virtual,
    /// so overriding them makes this a drop-in replacement at every existing call site without
    /// changing a single signature -- and without a second RNG interface for callers to get wrong.
    ///
    /// The stream ("sequence") parameter selects one of 2^63 distinct sequences from the same seed.
    /// That is what lets the capture pipeline draw its shot noise, its defect map and its cosmic
    /// rays from independent streams of one recorded seed, rather than interleaving them in one
    /// sequence where adding a draw in one place silently shifts every later draw everywhere.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public sealed class Pcg32 : Random
    {
        /// <summary>LCG multiplier from the reference implementation. Not adjustable: the period and equidistribution properties are proved for this constant.</summary>
        private const ulong Multiplier = 6364136223846793005UL;

        /// <summary>Stream identifiers used by the imaging pipeline, so its independent stochastic processes cannot correlate through a shared sequence.</summary>
        public const ulong StreamShotNoise = 1UL;
        public const ulong StreamReadNoise = 2UL;
        public const ulong StreamCosmicRays = 3UL;
        public const ulong StreamDefectMap = 4UL;
        public const ulong StreamScintillation = 5UL;

        private ulong state;
        private readonly ulong increment;

        /// <summary>
        /// Seeds the generator. sequence selects the stream; two generators with the same seed and
        /// different sequences produce unrelated output.
        ///
        /// The base System.Random constructor is given a constant rather than left to its
        /// time-based default: nothing of the base state is ever used (every method that could read
        /// it is overridden), and a constant keeps construction free of any hidden time dependence.
        /// </summary>
        public Pcg32(ulong seed, ulong sequence = StreamShotNoise) : base(0)
        {
            // The reference seeding routine, exactly: the increment must be odd for the LCG to
            // reach full period, hence the shift-and-set-low-bit.
            increment = (sequence << 1) | 1UL;
            state = 0UL;
            NextUInt32();
            unchecked { state += seed; }
            NextUInt32();
        }

        /// <summary>One 32-bit draw: advance the LCG, then apply the XSH-RR output permutation to the PREVIOUS state (which is what decorrelates the low bits an LCG alone leaves poor).</summary>
        public uint NextUInt32()
        {
            ulong old = state;
            unchecked { state = old * Multiplier + increment; }

            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rotation = (int)(old >> 59);

            // Rotate right by `rotation`. The (32 - rotation) & 31 form gives a shift of 0 when
            // rotation is 0, where the OR then returns xorshifted unchanged -- which is the
            // correct result, rather than relying on C#'s shift-count masking to produce it.
            return (xorshifted >> rotation) | (xorshifted << ((32 - rotation) & 31));
        }

        /// <summary>Two 32-bit draws combined, high word first so the stream order is defined rather than left to argument-evaluation order.</summary>
        public ulong NextUInt64()
        {
            ulong high = NextUInt32();
            ulong low = NextUInt32();
            return (high << 32) | low;
        }

        /// <summary>
        /// A double in [0,1), built from 53 random bits -- one for every bit of a double's
        /// significand, so every representable value in the interval is reachable with the right
        /// probability. System.Random's own Sample resolves only about 2^31 values.
        /// </summary>
        protected override double Sample()
        {
            return (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);   // 2^53
        }

        public override double NextDouble() => Sample();

        public override int Next() => Next(int.MaxValue);

        public override int Next(int maxValue)
        {
            if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
            if (maxValue <= 1) return 0;
            return (int)BoundedUInt32((uint)maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
            long range = (long)maxValue - minValue;
            if (range <= 1L) return minValue;
            if (range <= uint.MaxValue) return (int)(minValue + BoundedUInt32((uint)range));
            return (int)(minValue + (long)(NextDouble() * range));
        }

        public override void NextBytes(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            int i = 0;
            while (i + 4 <= buffer.Length)
            {
                uint word = NextUInt32();
                buffer[i++] = (byte)word;
                buffer[i++] = (byte)(word >> 8);
                buffer[i++] = (byte)(word >> 16);
                buffer[i++] = (byte)(word >> 24);
            }
            if (i < buffer.Length)
            {
                uint word = NextUInt32();
                while (i < buffer.Length) { buffer[i++] = (byte)word; word >>= 8; }
            }
        }

        /// <summary>
        /// A uniform value in [0, bound), by the reference implementation's rejection method.
        ///
        /// The naive `NextUInt32() % bound` is biased whenever bound does not divide 2^32: the
        /// first (2^32 mod bound) values come up one time more often than the rest. Discarding
        /// draws below that threshold removes the bias exactly, at the cost of a retry whose
        /// probability is below (bound / 2^32).
        /// </summary>
        private uint BoundedUInt32(uint bound)
        {
            uint threshold = (uint)((0x100000000UL - bound) % bound);
            while (true)
            {
                uint r = NextUInt32();
                if (r >= threshold) return r % bound;
            }
        }

        /// <summary>
        /// Mixes an arbitrary set of integers into one 64-bit seed, so a capture's seed can be
        /// derived reproducibly from what identifies it (target, time, filter) and still be
        /// recorded as a single number in the FITS header.
        ///
        /// SplitMix64's finalizer (Steele, Lea &amp; Flood 2014, OOPSLA, "Fast splittable
        /// pseudorandom number generators"), which is the standard avalanche mix for exactly this
        /// job: it is a bijection, so distinct inputs cannot collide, and one changed input bit
        /// changes about half the output bits.
        /// </summary>
        public static ulong MixSeed(params long[] values)
        {
            ulong h = 0x9E3779B97F4A7C15UL;
            if (values != null)
            {
                foreach (long v in values)
                {
                    unchecked
                    {
                        h += (ulong)v + 0x9E3779B97F4A7C15UL;
                        ulong z = h;
                        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                        h = z ^ (z >> 31);
                    }
                }
            }
            return h;
        }
    }
}
