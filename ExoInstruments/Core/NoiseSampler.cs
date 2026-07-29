using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The two random deviates the detector chain is built out of: a Poisson count for charge
    /// collection, and a Gaussian for the readout amplifier.
    ///
    /// WHY THESE MOVED HERE. They were private statics inside SolarSystemCameraTexture, which is
    /// Unity-dependent and therefore cannot be compiled by any headless harness. That made the two
    /// most safety-critical numerical routines in the whole pipeline the only ones that could not
    /// be checked against a reference implementation: every electron count, every noise figure and
    /// every signal-to-noise ratio the mod reports rests on these being exactly the distributions
    /// they claim to be, and nothing verified that they were. Moving them into Core changes no
    /// behaviour (the call sites delegate here) and makes them testable -- see
    /// tools/photometry-roundtrip, which compares them against SciPy.
    ///
    /// Both take System.Random so that Pcg32 can be passed in unchanged; see Pcg32 for why the
    /// pipeline does not use System.Random itself.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class NoiseSampler
    {
        /// <summary>Mean above which Poisson switches from Knuth's method to PTRS. Hormann's own paper recommends 10; NumPy uses the same value.</summary>
        public const double PtrsThreshold = 10.0;

        /// <summary>
        /// A Poisson deviate of the given mean. Exact at every mean, by two exact methods.
        ///
        /// Photon arrival IS a counting process, and a Gaussian of matching width only agrees with
        /// it once the count is large. At the few electrons per pixel a faint sky or a short dark
        /// reaches, a Gaussian goes negative and is measurably the wrong distribution -- the same
        /// reason GalSim and Pyxel both draw real Poisson deviates here.
        /// </summary>
        public static double Poisson(Random rng, double lambda)
        {
            if (!(lambda > 0.0)) return 0.0;

            // Knuth's product method: exact, and the cheapest thing available while the mean is
            // small. It is O(lambda) and needs exp(-lambda), so it is confined to the range
            // where both are harmless -- at a mean of 150,000 electrons it would run 150,000
            // iterations per pixel against an exp() that has already underflowed to zero, and
            // never terminate.
            if (lambda < PtrsThreshold)
            {
                double l = Math.Exp(-lambda);
                int k = 0;
                double p = 1.0;
                do
                {
                    k++;
                    p *= rng.NextDouble();
                } while (p > l);
                return k - 1;
            }

            // Above that, the transformed rejection method PTRS (Hormann 1993, "The transformed
            // rejection method for generating Poisson random variables", Insurance: Mathematics
            // and Economics 12, 39). This is a genuine Poisson generator, not a normal
            // approximation: it is a rejection sampler whose accepted values are exactly Poisson
            // distributed, at constant cost independent of the mean. It is the same algorithm
            // NumPy uses above its own threshold, and therefore the one behind GalSim's and
            // Pyxel's shot noise.
            double smu = Math.Sqrt(lambda);
            double b = 0.931 + 2.53 * smu;
            double a = -0.059 + 0.02483 * b;
            double invAlpha = 1.1239 + 1.1328 / (b - 3.4);
            double vr = 0.9277 - 3.6224 / (b - 2.0);

            while (true)
            {
                double u = rng.NextDouble() - 0.5;
                double v = rng.NextDouble();
                double us = 0.5 - Math.Abs(u);

                double k = Math.Floor((2.0 * a / us + b) * u + lambda + 0.43);
                if (us >= 0.07 && v <= vr) return k;
                if (k < 0.0 || (us < 0.013 && v > us)) continue;

                if (Math.Log(v * invAlpha / (a / (us * us) + b))
                    <= k * Math.Log(lambda) - lambda - LogGamma(k + 1.0))
                    return k;
            }
        }

        /// <summary>
        /// A zero-mean Gaussian deviate of the given standard deviation, by the Box-Muller
        /// transform. This is what the readout amplifier adds, in electrons, ahead of the
        /// converter -- which is where it physically enters.
        /// </summary>
        public static double Gaussian(Random rng, double sigma)
        {
            if (!(sigma > 0.0)) return 0.0;
            double u1 = 1.0 - rng.NextDouble();   // (0,1], so the log is finite
            double u2 = rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return z * sigma;
        }

        /// <summary>
        /// log(Gamma(x)) for x &gt; 0, by the Lanczos approximation (Lanczos 1964, "A precision
        /// approximation of the gamma function", SIAM J. Numer. Anal. B 1, 86) with the g=7,
        /// n=9 coefficient set. Accurate to about 15 significant digits over the range PTRS
        /// needs, which is the factorial term of the Poisson mass function.
        /// </summary>
        public static double LogGamma(double x)
        {
            double sum = LanczosCoefficients[0];
            for (int i = 1; i < LanczosCoefficients.Length; i++) sum += LanczosCoefficients[i] / (x + i - 1.0);

            double t = x + 6.5; // x + g - 0.5, with g = 7
            return 0.5 * Math.Log(2.0 * Math.PI) + (x - 0.5) * Math.Log(t) - t + Math.Log(sum);
        }

        private static readonly double[] LanczosCoefficients =
        {
            0.99999999999980993, 676.5203681218851, -1259.1392167224028,
            771.32342877765313, -176.61502916214059, 12.507343278686905,
            -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7
        };
    }
}
