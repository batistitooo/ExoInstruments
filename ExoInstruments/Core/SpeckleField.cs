using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The noise that actually limits high-contrast imaging, which is not photon noise.
    ///
    /// WHAT A CORONAGRAPH LEAVES BEHIND. Once the focal-plane mask has removed the stellar core and
    /// the Lyot stop has thrown away the diffracted light (Core.Coronagraph), what remains around
    /// the star is not a smooth halo. It is a field of SPECKLES: the residual wavefront error, both
    /// what the adaptive optics failed to correct and what the instrument's own optics add after
    /// the wavefront sensor, interfering into grains one resolution element across. Each grain is
    /// as bright as a planet and looks exactly like one. Distinguishing the two is the entire
    /// discipline, and a simulator without speckles reports detection limits that are wrong by
    /// orders of magnitude, in the optimistic direction.
    ///
    /// THE STATISTICS ARE NOT GAUSSIAN. Soummer, Ferrari, Aime and Jolissaint (2007, ApJ 669, 642)
    /// and Aime and Soummer (2004, ApJ 612, L85) derive the distribution: the complex amplitude at
    /// a point is a deterministic part A_c plus a circular complex Gaussian random part A_s, so the
    /// intensity I = |A_c + A_s|^2 follows a MODIFIED RICIAN,
    ///
    ///     p(I) = (1/I_s) exp(-(I + I_c)/I_s) I_0( 2 sqrt(I I_c) / I_s )
    ///
    /// with I_c = |A_c|^2, I_s = &lt;|A_s|^2&gt; and I_0 the modified Bessel function of the first
    /// kind. Its mean is I_c + I_s and its variance is I_s^2 + 2 I_c I_s. Two consequences follow
    /// that a Gaussian of the same width does not have, and both are the reason the distribution
    /// matters rather than a refinement of it: the tail is heavy, so bright speckles are far more
    /// common than a Gaussian predicts and false positives with them; and the variance depends on
    /// the STATIC field I_c, so an instrument with better static wavefront quality is quieter as
    /// well as fainter, which is why non-common-path aberration correction is worth doing at all.
    ///
    /// WHICH PARTS AVERAGE DOWN, AND WHICH DO NOT. This is the whole of the observing strategy, and
    /// Milli et al. (2016, SPIE 9909, arXiv:1608.02149) measured it on SPHERE directly: 52 minutes
    /// of coronagraphic images of HR 3484 at 1.6 Hz, correlated pairwise. They find three regimes,
    /// and this file carries their three numbers:
    ///
    ///   * A STATIC pattern holding 71.3% of the correlation (their rho_0 = 0.713 +/- 0.0005),
    ///     unchanged over the whole hour. Nothing in an exposure averages this down and no
    ///     calibration frame removes it. Only ADI or a reference star does.
    ///   * A FAST component of 5.9% (their Lambda = 0.059 +/- 0.002) decaying with tau = 3.5 +/- 0.2
    ///     seconds. Notably, they show this one is INSTRUMENTAL rather than atmospheric, since it
    ///     appears with the internal calibration lamp too.
    ///   * The remaining 22.8%, which has already decorrelated by their 0.63 s cadence. This is the
    ///     atmospheric residual, and its own timescale comes from Macintosh et al. (2005, SPIE
    ///     5903, 170): speckles from uncorrected atmospheric perturbation are refreshed as the wind
    ///     carries the turbulence across the pupil, on a timescale of 0.6 D/v.
    ///
    /// Over longer times the static part is not quite static either: they measure it decorrelating
    /// linearly at 73 ppm/s once the temporal median is subtracted, faster near the axis (a slope
    /// of about 49 ppm/s per arcsec) and settling to about 10 ppm/s beyond 500 mas. That rate is
    /// what sets how much of an ADI sequence's own reference is still valid at its end.
    ///
    /// WHERE THE SPECKLES ARE. Adaptive optics can only correct spatial frequencies its deformable
    /// mirror can reach, so the corrected region is a square of half-width (N/2) lambda/D for N
    /// actuators across the pupil, and the uncorrected light piles up at its edge in the "speckle
    /// ring" or AO control radius. SPHERE's SAXO carries a 41x41 actuator deformable mirror
    /// (Fusco et al. 2006; Beuzit et al. 2019), giving 20.5 lambda/D, which at 8.2 m and ZIMPOL's
    /// R_PRIM is 323 mas. Schmid et al. (2018) report the observed speckle ring at rho = 0.3 to 0.4
    /// arcsec. That agreement is not an input to this model; it is a check on it, and it is the
    /// reason the control radius is computed from the actuator count rather than tabulated.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class SpeckleField
    {
        // ---------------------------------------------------------------- temporal decomposition

        /// <summary>Fraction of the speckle pattern's variance that is static over an observation, from Milli et al. (2016) rho_0.</summary>
        public const double StaticVarianceFraction = 0.713;

        /// <summary>Fraction in the fast, instrumental component, from their Lambda.</summary>
        public const double FastVarianceFraction = 0.059;

        /// <summary>Decay time of that fast component in seconds, from their tau. Instrumental rather than atmospheric: it is present with the internal lamp.</summary>
        public const double FastDecorrelationSeconds = 3.5;

        /// <summary>
        /// What is left for the atmosphere: everything that had already decorrelated by their
        /// 0.63 s cadence. Derived rather than measured, and stated as such; it is the residual of
        /// a decomposition whose other two terms are fitted.
        /// </summary>
        public static double AtmosphericVarianceFraction
            => 1.0 - StaticVarianceFraction - FastVarianceFraction;

        /// <summary>
        /// Rate at which the quasi-static pattern itself decorrelates, per second, once the
        /// temporal median is removed. Milli et al. (2016) fit -73 ppm/s over 5 to 15 minutes.
        /// This is what limits how long an ADI sequence's own frames stay usable as each other's
        /// reference.
        /// </summary>
        public const double QuasiStaticDecorrelationPerSecond = 73e-6;

        /// <summary>
        /// Lifetime of the atmospheric speckles: 0.6 D / v, with D the aperture and v the wind
        /// speed carrying turbulence across it (Macintosh et al. 2005, as applied to SPHERE by
        /// Milli et al. 2016, who note that 3 to 4 m/s on the ground gives 0.6 D/v of at most
        /// 1.6 s on the VLT).
        ///
        /// Returns positive infinity for zero wind, which is the correct limit rather than a
        /// division by zero: with nothing moving the turbulence across the pupil, the pattern
        /// would not refresh at all.
        /// </summary>
        public static double AtmosphericLifetimeSeconds(double apertureMeters, double windSpeedMetersPerSecond)
        {
            if (!(apertureMeters > 0.0)) return 0.0;
            if (!(windSpeedMetersPerSecond > 0.0)) return double.PositiveInfinity;
            return 0.6 * apertureMeters / windSpeedMetersPerSecond;
        }

        /// <summary>
        /// How many independent realisations of a process of the given lifetime an exposure
        /// averages over. Never below one: an exposure shorter than the lifetime sees one frozen
        /// realisation, not a fraction of one.
        /// </summary>
        public static double IndependentRealisations(double exposureSeconds, double lifetimeSeconds)
        {
            if (!(exposureSeconds > 0.0)) return 1.0;
            if (!(lifetimeSeconds > 0.0)) return 1.0;
            if (double.IsPositiveInfinity(lifetimeSeconds)) return 1.0;
            return Math.Max(1.0, exposureSeconds / lifetimeSeconds);
        }

        /// <summary>
        /// The fraction of the speckle field's variance that survives an exposure of the given
        /// length, before any differential imaging.
        ///
        /// Each component is divided by its own number of independent realisations and the three
        /// are added, because they are independent contributions to one variance. The static term
        /// is divided by one, always, which is the entire point of separating it: an eight-hour
        /// exposure has exactly as much of it as a one-second exposure, and only a technique that
        /// moves the astrophysical signal relative to the instrument can remove it.
        /// </summary>
        public static double SurvivingVarianceFraction(
            double exposureSeconds, double apertureMeters, double windSpeedMetersPerSecond)
        {
            double nFast = IndependentRealisations(exposureSeconds, FastDecorrelationSeconds);
            double nAtm = IndependentRealisations(
                exposureSeconds, AtmosphericLifetimeSeconds(apertureMeters, windSpeedMetersPerSecond));

            return StaticVarianceFraction
                 + FastVarianceFraction / nFast
                 + AtmosphericVarianceFraction / nAtm;
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>
        /// The adaptive optics control radius in milliarcseconds: (N/2) lambda/D for N actuators
        /// across the pupil.
        ///
        /// Outside it the deformable mirror has no spatial frequency to work with and the residual
        /// wavefront is uncorrected, so the corrected dark region ends and the seeing halo begins.
        /// The edge is where the uncorrected light piles up, which is why it is visible as a ring
        /// rather than merely as a boundary.
        /// </summary>
        public static double ControlRadiusMas(int actuatorsAcrossPupil, double wavelengthNm, double apertureMeters)
        {
            if (actuatorsAcrossPupil < 2 || !(wavelengthNm > 0.0) || !(apertureMeters > 0.0)) return 0.0;
            return 0.5 * actuatorsAcrossPupil * LambdaOverDMas(wavelengthNm, apertureMeters);
        }

        /// <summary>One resolution element in milliarcseconds, which is also the size of one speckle grain.</summary>
        public static double LambdaOverDMas(double wavelengthNm, double apertureMeters)
        {
            if (!(apertureMeters > 0.0)) return 0.0;
            double radians = (wavelengthNm * 1e-9) / apertureMeters;
            return radians * (180.0 / Math.PI) * 3600.0 * 1000.0;
        }

        // ---------------------------------------------------------------- the distribution

        /// <summary>Mean intensity of a modified Rician with static part I_c and random part I_s.</summary>
        public static double MeanIntensity(double coherent, double random) => coherent + random;

        /// <summary>Variance of a single realisation: I_s^2 + 2 I_c I_s (Soummer et al. 2007).</summary>
        public static double Variance(double coherent, double random)
            => random * random + 2.0 * coherent * random;

        /// <summary>
        /// Splits a measured halo intensity into its static and random parts, given what fraction
        /// of the VARIANCE is static.
        ///
        /// The pipeline knows the halo's mean intensity at each radius, because that is what the
        /// AO PSF model already delivers; what it does not know is how that mean divides between a
        /// frozen pattern and a boiling one, and the division is what decides how much survives an
        /// exposure.
        ///
        /// THE DERIVATION, because the answer is simpler than it looks and the simplicity is the
        /// reason to trust it. Write the mean as m = I_c + I_s. A fully developed speckle pattern
        /// has spatial variance equal to the square of its mean, so the static field alone
        /// contributes I_c^2. The random field contributes its own I_s^2, and the interference
        /// between the two contributes 2 I_c I_s, which is the cross term already in Variance
        /// above. The three sum to
        ///
        ///     I_c^2 + I_s^2 + 2 I_c I_s = (I_c + I_s)^2 = m^2
        ///
        /// so the TOTAL variance of the pattern is m^2 whatever the split, and the fraction of it
        /// carried by the static part is exactly (I_c/m)^2. Setting that equal to Milli's measured
        /// correlation floor f gives
        ///
        ///     I_c = m sqrt(f),      I_s = m (1 - sqrt(f))
        ///
        /// with no free parameter and no root to choose between. At f = 0.713 that is 84.4% of the
        /// halo's light in the frozen pattern and 15.6% in the boiling one.
        ///
        /// f = 0 puts everything in the random part, which is the pre-adaptive-optics limit of a
        /// pure atmospheric speckle pattern; f = 1 would put everything in the static part, where
        /// nothing fluctuates at all. The caller is expected to use the measured value rather than
        /// either extreme.
        /// </summary>
        public static void Split(double meanIntensity, double staticVarianceFraction,
                                 out double coherent, out double random)
        {
            coherent = 0.0;
            random = Math.Max(0.0, meanIntensity);
            if (!(meanIntensity > 0.0)) return;

            double f = staticVarianceFraction;
            if (f <= 0.0) return;
            if (f >= 1.0) { coherent = meanIntensity; random = 0.0; return; }

            coherent = meanIntensity * Math.Sqrt(f);
            random = meanIntensity - coherent;
        }

        /// <summary>
        /// One draw from the modified Rician, by construction rather than by inverting its
        /// cumulative distribution.
        ///
        /// I = |A_c + A_s|^2 with A_s a circular complex Gaussian of total power I_s, so the two
        /// quadratures each carry I_s/2. Written this way the draw is exact, needs no Bessel
        /// function and no rejection step, and is manifestly the physics rather than a fit to it.
        /// </summary>
        public static double Sample(Random rng, double coherent, double random)
        {
            if (!(random > 0.0)) return Math.Max(0.0, coherent);

            double sigma = Math.Sqrt(0.5 * random);
            double re = Math.Sqrt(Math.Max(0.0, coherent)) + NoiseSampler.Gaussian(rng, sigma);
            double im = NoiseSampler.Gaussian(rng, sigma);
            return re * re + im * im;
        }

        /// <summary>
        /// The average of n independent realisations, which is what an exposure records.
        ///
        /// Drawn exactly by summing for small n, and by a Gaussian of matching mean and variance
        /// above the threshold, where the central limit theorem has taken hold and the sum's own
        /// skewness has fallen below a part in ten. The threshold is a numerical choice and is
        /// stated as one; the moments are exact either side of it, and only the shape of the tail
        /// differs.
        /// </summary>
        public const int ExactSumRealisationLimit = 32;

        public static double SampleAveraged(Random rng, double coherent, double random, double realisations)
        {
            if (!(realisations > 1.0)) return Sample(rng, coherent, random);

            int n = (int)Math.Round(realisations);
            if (n <= 1) return Sample(rng, coherent, random);

            if (n <= ExactSumRealisationLimit)
            {
                double sum = 0.0;
                for (int i = 0; i < n; i++) sum += Sample(rng, coherent, random);
                return sum / n;
            }

            double mean = MeanIntensity(coherent, random);
            double sigma = Math.Sqrt(Variance(coherent, random) / n);
            return Math.Max(0.0, mean + NoiseSampler.Gaussian(rng, sigma));
        }
    }
}
