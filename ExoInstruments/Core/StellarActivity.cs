using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Astrophysical noise intrinsic to the star: RV jitter (spots, granulation,
    /// p-modes) and photometric spot modulation. Each star gets a persistent
    /// activity level from a deterministic hash, same star, same noise, every session,
    /// without storing anything. The catalog carries no activity indicators, so
    /// you don't know which targets are quiet until you observe them.
    /// </summary>
    public static class StellarActivity
    {
        /// <summary>
        /// Persistent activity multiplier, log-uniform in [0.5, 2.5]. Applied to both RV jitter and spot
        /// amplitude; an active star is loud in both.
        /// </summary>
        public static double ActivityFactor(StarTarget star)
        {
            return LogUniform(Hash01(star, "activity"), 0.5, 2.5);
        }

        /// <summary>White RV jitter (m/s, 1-sigma) to add in quadrature with instrument precision. Teff-banded baselines, scaled by activity factor.</summary>
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

        /// <summary>Stellar rotation period (days), drawn from Teff-banded ranges based on the Kepler rotation catalog.</summary>
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

        /// <summary>Peak spot-modulation amplitude (ppm), log-uniform in the range Kepler observed, scaled by activity factor.</summary>
        public static double SpotAmplitudePpm(StarTarget star)
        {
            return LogUniform(Hash01(star, "spots"), 120.0, 1200.0) * ActivityFactor(star);
        }

        /// <summary>
        /// Fractional flux offset from spot rotation: fundamental + half-amplitude first harmonic (the classic
        /// two-spot-group shape). Deterministic in (star, ut).
        /// </summary>
        public static double SpotModulationFlux(StarTarget star, double ut)
        {
            double amplitude = SpotAmplitudePpm(star) / 1_000_000.0;
            double omega = 2.0 * Math.PI * ut / (RotationPeriodDays(star) * 86400.0);
            double phase1 = Hash01(star, "spotPhase1") * 2.0 * Math.PI;
            double phase2 = Hash01(star, "spotPhase2") * 2.0 * Math.PI;
            return amplitude * (Math.Sin(omega + phase1) + 0.5 * Math.Sin(2.0 * omega + phase2));
        }

        // Deterministic uniform draw in [0,1) from the star's identity + a salt string. FNV-1a hash, stable
        // across runtimes.
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
