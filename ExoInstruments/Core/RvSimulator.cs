using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Generates synthetic radial-velocity measurements from a Keplerian orbit.
    /// Pure function of (star, UT) plus an injected RNG, mirroring LightCurveSimulator.
    /// </summary>
    public static class RvSimulator
    {
        public static double GenerateVelocityAtTime(StarTarget star, InstrumentSpec instrument, double ut, Random rng)
        {
            double signal = ComputeRadialVelocity(star, ut);
            double noise = GaussianNoise(rng, TotalNoiseSigmaMps(star, instrument));
            return signal + noise;
        }

        /// <summary>1-sigma scatter per epoch: instrument precision and stellar activity jitter added in quadrature. Sessions report only the instrument term — the jitter shows up as excess residuals, exactly as in real surveys.</summary>
        public static double TotalNoiseSigmaMps(StarTarget star, InstrumentSpec instrument)
        {
            double instrumentSigma = instrument.EstimatePrecision(star.ApparentMagnitude);
            double jitterSigma = StellarActivity.RvJitterMps(star);
            return Math.Sqrt(instrumentSigma * instrumentSigma + jitterSigma * jitterSigma);
        }

        /// <summary>Keplerian reflex from every system planet (signals superpose linearly) plus the RM anomaly of any transiting companion that happens to be in transit at this epoch — always in the physics whether the observer planned for it or not.</summary>
        public static double GenerateSystemVelocityAtTime(IList<StarTarget> systemPlanets, InstrumentSpec instrument, double ut, Random rng)
        {
            double signal = RossiterMcLaughlin.SystemAnomalyMps(systemPlanets, ut);
            for (int i = 0; i < systemPlanets.Count; i++)
            {
                signal += ComputeRadialVelocity(systemPlanets[i], ut);
            }
            double noise = GaussianNoise(rng, TotalNoiseSigmaMps(systemPlanets[0], instrument));
            return signal + noise;
        }

        public static List<RvSample> GenerateSamples(StarTarget star, InstrumentSpec instrument, double startUt, double endUt, double cadenceSeconds, Random rng)
        {
            double uncertaintyMps = instrument.EstimatePrecision(star.ApparentMagnitude);
            var samples = new List<RvSample>();
            for (double t = startUt; t <= endUt; t += cadenceSeconds)
            {
                samples.Add(new RvSample(t, GenerateVelocityAtTime(star, instrument, t, rng), uncertaintyMps));
            }
            return samples;
        }

        private static double ComputeRadialVelocity(StarTarget star, double ut)
        {
            if (!star.IsRvDetectable) return 0.0;

            double k = star.EstimatedRvSemiAmplitudeMps;
            if (k <= 0) return 0.0;

            double e = star.Eccentricity;
            double omegaRad = (star.ArgumentOfPeriastronDeg ?? 0.0) * Math.PI / 180.0;
            double periodSeconds = star.PlanetPeriodDays * 86400.0;

            // PlanetPhaseOffset01 as the periastron epoch: same arbitrary-zero-point logic as transit phase.
            double meanAnomaly = 2.0 * Math.PI * ((ut / periodSeconds + star.PlanetPhaseOffset01) % 1.0);
            double eccentricAnomaly = SolveKeplerEquation(meanAnomaly, e);
            double trueAnomaly = TrueAnomalyFromEccentric(eccentricAnomaly, e);

            return k * (Math.Cos(trueAnomaly + omegaRad) + e * Math.Cos(omegaRad));
        }

        /// <summary>Newton-Raphson solve of M = E - e*sin(E) for E, given mean anomaly M (radians).</summary>
        private static double SolveKeplerEquation(double meanAnomaly, double eccentricity)
        {
            double e = eccentricity;
            double eccentricAnomaly = meanAnomaly; // good starting guess for e < ~0.8, which covers the vast majority of catalog entries
            for (int i = 0; i < 50; i++)
            {
                double f = eccentricAnomaly - e * Math.Sin(eccentricAnomaly) - meanAnomaly;
                double fPrime = 1.0 - e * Math.Cos(eccentricAnomaly);
                double delta = f / fPrime;
                eccentricAnomaly -= delta;
                if (Math.Abs(delta) < 1e-10) break;
            }
            return eccentricAnomaly;
        }

        private static double TrueAnomalyFromEccentric(double eccentricAnomaly, double eccentricity)
        {
            double halfE = eccentricAnomaly / 2.0;
            double factor = Math.Sqrt((1.0 + eccentricity) / (1.0 - eccentricity));
            return 2.0 * Math.Atan(factor * Math.Tan(halfE));
        }

        private static double GaussianNoise(Random rng, double sigma)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return sigma * randStdNormal;
        }
    }
}
