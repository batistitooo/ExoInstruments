using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Astrophysical noise from the star itself -- the thing no instrument
    /// upgrade can buy away, and the actual precision floor of modern RV and
    /// photometric surveys. Two faces of the same magnetic activity:
    ///
    /// - RV "jitter": stochastic line-profile distortions from spots, plage,
    ///   granulation and p-mode oscillations. Modeled as a white-noise term
    ///   added in quadrature with instrument precision, the standard treatment
    ///   in RV orbit fitting (Wright 2005; Saar, Butler &amp; Marcy 1998).
    ///   Baselines follow the observed Teff trend: F stars are jitter-loud
    ///   (shallow convective envelopes, fast rotation), K stars are the
    ///   quietest, M dwarfs climb again through flaring and spot coverage.
    ///
    /// - Photometric spot modulation: dark spots rotating across the visible
    ///   disk imprint a quasi-periodic signal at the stellar rotation period
    ///   (fundamental + first harmonic, the classic two-spot-group light
    ///   curve). Rotation-period bands per spectral type follow the Kepler
    ///   rotation catalog (McQuillan, Mazeh &amp; Aigrain 2014); variability
    ///   amplitudes span the quiet-Sun-to-active-dwarf range observed by
    ///   Kepler (Basri et al. 2013).
    ///
    /// The catalog carries no activity indicators (log R'HK, S-index), so each
    /// star draws a persistent activity level from a deterministic hash of its
    /// identity: the same star is always the same star, across sessions and
    /// saves, without storing anything. That unknown-until-observed scatter is
    /// itself realistic -- target lists get built before anyone knows which
    /// stars will turn out too active to be useful.
    /// </summary>
    public static class StellarActivity
    {
        /// <summary>
        /// Persistent per-star activity multiplier, log-uniform in [0.5, 2.5].
        /// Applied to both the RV jitter and the spot amplitude -- an active
        /// star is loud in both observables at once, as in reality.
        /// </summary>
        public static double ActivityFactor(StarTarget star)
        {
            return LogUniform(Hash01(star, "activity"), 0.5, 2.5);
        }

        /// <summary>
        /// White RV jitter (m/s, 1-sigma) to add in quadrature with instrument
        /// precision. Teff-band baselines after Wright 2005 (median jitter of
        /// inactive field dwarfs), scaled by the star's activity factor.
        /// </summary>
        public static double RvJitterMps(StarTarget star)
        {
            double teff = star.EffectiveTempK ?? 5500.0; // unknown: assume solar-ish
            double baseline;
            if (teff >= 6250.0) baseline = 3.5;       // F: shallow convection zones, rapid rotators
            else if (teff >= 5250.0) baseline = 1.8;  // G: solar-type granulation + activity
            else if (teff >= 4000.0) baseline = 1.2;  // K: the classic quiet RV targets
            else baseline = 2.2;                      // M: spot/flare activity dominates
            return baseline * ActivityFactor(star);
        }

        /// <summary>
        /// Stellar rotation period (days): Teff-banded ranges from the Kepler
        /// rotation-period catalog (McQuillan et al. 2014), position within the
        /// band fixed by the star's hash.
        /// </summary>
        public static double RotationPeriodDays(StarTarget star)
        {
            double teff = star.EffectiveTempK ?? 5500.0;
            double lo, hi;
            if (teff >= 6250.0) { lo = 4.0; hi = 18.0; }        // F: spun up, weak magnetic braking
            else if (teff >= 5250.0) { lo = 12.0; hi = 35.0; }  // G: Sun sits at ~25 d
            else if (teff >= 4000.0) { lo = 20.0; hi = 50.0; }  // K
            else { lo = 25.0; hi = 90.0; }                      // M: wide observed spread
            return lo + Hash01(star, "rotation") * (hi - lo);
        }

        /// <summary>
        /// Peak spot-modulation amplitude (ppm). Log-uniform across the range
        /// Kepler observed for dwarfs -- quiet-Sun ~100 ppm up to low-mmag
        /// active stars (Basri et al. 2013) -- times the activity factor.
        /// </summary>
        public static double SpotAmplitudePpm(StarTarget star)
        {
            return LogUniform(Hash01(star, "spots"), 120.0, 1200.0) * ActivityFactor(star);
        }

        /// <summary>
        /// Fractional flux offset from spot rotation at time ut: fundamental at
        /// the rotation period plus a half-amplitude first harmonic (two spot
        /// groups on opposite hemispheres -- the shape real Kepler rotators show).
        /// Deterministic in (star, ut): the light curve is repeatable, only the
        /// measurement noise is random.
        /// </summary>
        public static double SpotModulationFlux(StarTarget star, double ut)
        {
            double amplitude = SpotAmplitudePpm(star) / 1_000_000.0;
            double omega = 2.0 * Math.PI * ut / (RotationPeriodDays(star) * 86400.0);
            double phase1 = Hash01(star, "spotPhase1") * 2.0 * Math.PI;
            double phase2 = Hash01(star, "spotPhase2") * 2.0 * Math.PI;
            return amplitude * (Math.Sin(omega + phase1) + 0.5 * Math.Sin(2.0 * omega + phase2));
        }

        /// <summary>
        /// Deterministic uniform draw in [0,1) from the star's stable identity
        /// plus a per-quantity salt. FNV-1a: tiny, stable across runtimes, and
        /// well-mixed enough for game-noise purposes.
        /// </summary>
        private static double Hash01(StarTarget star, string salt)
        {
            string identity = (star.CatalogKey ?? star.HostStarName ?? star.Name ?? "") + "|" + salt;
            const uint fnvPrime = 16777619;
            uint hash = 2166136261;
            for (int i = 0; i < identity.Length; i++)
            {
                hash ^= identity[i];
                hash *= fnvPrime;
            }
            return hash / 4294967296.0;
        }

        private static double LogUniform(double u01, double min, double max)
        {
            return min * Math.Pow(max / min, u01);
        }
    }
}
