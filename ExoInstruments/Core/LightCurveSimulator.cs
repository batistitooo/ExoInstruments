using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Generates synthetic flux measurements: stellar spot baseline (StellarActivity) +
    /// limb-darkened transit (Mandel &amp; Agol 2002 small-planet approximation) +
    /// instrument/scintillation noise in quadrature.
    /// </summary>
    public static class LightCurveSimulator
    {
        private const double SecondsPerDay = 86400.0;

        public static double GenerateFluxAtTime(StarTarget star, InstrumentSpec instrument, double ut, Random rng, double airmass = 1.0)
        {
            double baseline = 1.0 + StellarActivity.SpotModulationFlux(star, ut);
            double noise = GaussianNoise(rng, TotalNoiseSigma(star, instrument, airmass));
            double dip = ComputeTransitDip(star, ut);
            return baseline - dip + noise;
        }

        /// <summary>
        /// Combined flux from all planets on a shared stellar baseline. ttvSignals is
        /// parallel to systemPlanets (null = strict ephemerides): each transit is shifted
        /// by its own TTV term, which the O-C analysis later recovers.
        /// </summary>
        public static double GenerateSystemFluxAtTime(
            IList<StarTarget> systemPlanets,
            TransitTimingVariations.TtvSignal[] ttvSignals,
            InstrumentSpec instrument, double ut, Random rng,
            double airmass = 1.0, double moonSkyFactor = 0.0)
        {
            StarTarget host = systemPlanets[0];
            double baseline = 1.0 + StellarActivity.SpotModulationFlux(host, ut);
            double totalDip = 0.0;
            for (int i = 0; i < systemPlanets.Count; i++)
            {
                double shiftSeconds = ttvSignals != null ? ttvSignals[i].ShiftSeconds(ut) : 0.0;
                totalDip += ComputeTransitDip(systemPlanets[i], ut - shiftSeconds);
            }
            double noise = GaussianNoise(rng, TotalNoiseSigma(host, instrument, airmass, moonSkyFactor));
            return baseline - totalDip + noise;
        }

        /// <summary>
        /// 1-sigma noise for one exposure, as a fractional flux uncertainty. Also reported as the
        /// per-sample uncertainty on every FluxSample.
        ///
        /// TWO PATHS, and which one runs depends on whether the instrument's hardware has been
        /// sourced (see PhotometricDetector):
        ///
        ///   * PHYSICAL. With a complete detector, the whole budget comes from the CCD equation
        ///     (Merline &amp; Howell 1995) over this instrument's real collecting area, throughput,
        ///     QE, plate scale, read noise and dark current, against the real sky brightness at
        ///     this airmass and lunar phase, plus Young (1967) scintillation. Sky and moonlight
        ///     enter as electrons in the aperture, which is what they physically are.
        ///
        ///   * EMPIRICAL. Without one, the previous behaviour is used unchanged: the fitted
        ///     magnitude scaling, plus the scintillation EXCESS above zenith (because that fit
        ///     already contains typical-conditions scintillation) and MoonlightPollution's own
        ///     excess-noise term. Both of those exist to patch a relation that has no sky in it;
        ///     the physical path does not use either, and must not, or the moon would be counted
        ///     twice.
        /// </summary>
        public static double TotalNoiseSigma(StarTarget star, InstrumentSpec instrument, double airmass, double moonSkyFactor = 0.0)
        {
            TransitPhotometry.Budget budget;
            if (TransitPhotometry.TryEstimate(star, instrument, airmass, moonSkyFactor, out budget))
                return budget.TotalSigma;

            double instrumentSigma = instrument.EstimatePrecision(star.ApparentMagnitude) / 1_000_000.0;
            double scintillationSigma = AtmosphericNoise.ScintillationExcessSigma(instrument, airmass);
            double moonSigma = MoonlightPollution.ExcessNoiseSigma(instrument, star.ApparentMagnitude, moonSkyFactor);
            return Math.Sqrt(instrumentSigma * instrumentSigma + scintillationSigma * scintillationSigma + moonSigma * moonSigma);
        }

        /// <summary>
        /// The full electron budget behind one exposure's error bar, when the instrument has a
        /// sourced detector: for a diagnostic readout, the same way the imaging pipeline exposes
        /// its own last-capture figures. False when the empirical path is in use, in which case
        /// there is no budget to show.
        /// </summary>
        public static bool TryGetNoiseBudget(
            StarTarget star, InstrumentSpec instrument, double airmass, double moonSkyFactor,
            out TransitPhotometry.Budget budget)
        {
            return TransitPhotometry.TryEstimate(star, instrument, airmass, moonSkyFactor, out budget);
        }

        public static List<FluxSample> GenerateSamples(StarTarget star, InstrumentSpec instrument, double startUt, double endUt, double cadenceSeconds, Random rng)
        {
            double uncertaintyFlux = TotalNoiseSigma(star, instrument, 1.0);
            var samples = new List<FluxSample>();
            for (double t = startUt; t <= endUt; t += cadenceSeconds)
            {
                samples.Add(new FluxSample(t, GenerateFluxAtTime(star, instrument, t, rng), uncertaintyFlux));
            }
            return samples;
        }

        /// <summary>
        /// Transit chord state for RM calculations. Returns false when a/R_star or impact
        /// parameter is missing (the box-model fallback isn't good enough for RM).
        /// xStellarRadii is 0 at mid-transit (negative approaching); dipFraction is 0 outside transit.
        /// </summary>
        public static bool TryGetTransitChordState(StarTarget star, double ut, out double xStellarRadii, out double dipFraction)
        {
            xStellarRadii = 0.0;
            dipFraction = 0.0;
            if (!star.IsTransiting) return false;

            double depthFraction = star.TransitDepthPpm / 1_000_000.0;
            double p = Math.Sqrt(depthFraction);
            if (p <= 0) return false;

            double aRs = star.ScaledSemiMajorAxis;
            double? b = star.ImpactParameter;
            if (aRs <= 1.0 || !b.HasValue) return false;

            double periodSeconds = star.PlanetPeriodDays * SecondsPerDay;
            double phase = (ut / periodSeconds + star.PlanetPhaseOffset01) % 1.0;
            if (phase < 0) phase += 1.0;
            double phaseCentered = phase > 0.5 ? phase - 1.0 : phase;
            if (Math.Abs(phaseCentered) > 0.25) return true; // far side: valid geometry, no transit right now

            xStellarRadii = aRs * Math.Sin(2.0 * Math.PI * phaseCentered);
            double z = Math.Sqrt(xStellarRadii * xStellarRadii + b.Value * b.Value);
            QuadraticLimbDarkening(star.EffectiveTempK, out double u1, out double u2);
            dipFraction = LimbDarkenedDip(z, p, u1, u2);
            return true;
        }

        private static double ComputeTransitDip(StarTarget star, double ut)
        {
            if (!star.IsTransiting) return 0.0;

            double depthFraction = star.TransitDepthPpm / 1_000_000.0;
            double p = Math.Sqrt(depthFraction);  // Rp/R_star
            if (p <= 0) return 0.0;

            double periodSeconds = star.PlanetPeriodDays * SecondsPerDay;
            double phase = (ut / periodSeconds + star.PlanetPhaseOffset01) % 1.0;
            if (phase < 0) phase += 1.0;
            double phaseCentered = phase > 0.5 ? phase - 1.0 : phase;

            double aRs = star.ScaledSemiMajorAxis;
            double? b = star.ImpactParameter;
            if (aRs > 1.0 && b.HasValue)
            {
                // Far side of the orbit: no transit (the secondary eclipse is
                // orders of magnitude below these instruments' noise floors).
                if (Math.Abs(phaseCentered) > 0.25) return 0.0;

                // Projected star-planet separation in stellar radii, circular
                // chord, same approximation EstimatedTransitDurationHours uses.
                double x = aRs * Math.Sin(2.0 * Math.PI * phaseCentered);
                double z = Math.Sqrt(x * x + b.Value * b.Value);

                QuadraticLimbDarkening(star.EffectiveTempK, out double u1, out double u2);
                return LimbDarkenedDip(z, p, u1, u2);
            }

            // Incomplete geometry (e.g. measured impact parameter but no usable
            // a/R_star): fall back to the duration-based box model.
            double? durationHours = star.EstimatedTransitDurationHours;
            if (!durationHours.HasValue || durationHours.Value <= 0) return 0.0;
            double halfDurationPhase = (durationHours.Value * 3600.0) / periodSeconds / 2.0;
            return Math.Abs(phaseCentered) <= halfDurationPhase ? depthFraction : 0.0;
        }

        // Fractional flux deficit for planet radius ratio p at separation z, with quadratic limb darkening.
        // Mandel & Agol 2002 small-planet approximation.
        private static double LimbDarkenedDip(double z, double p, double u1, double u2)
        {
            if (z >= 1.0 + p) return 0.0;

            double meanIntensity = 1.0 - u1 / 3.0 - u2 / 6.0;

            if (z <= 1.0 - p)
            {
                double mu = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
                return p * p * LocalIntensity(mu, u1, u2) / meanIntensity;
            }

            // Ingress/egress: circle-overlap area * limb intensity (mu goes to 0 at the limb, giving the smooth shallow shoulders).
            double coveredFraction = CircleOverlapArea(z, p) / (Math.PI * p * p);
            double rEdge = Math.Min(1.0, z);
            double muEdge = Math.Sqrt(Math.Max(0.0, 1.0 - rEdge * rEdge));
            return coveredFraction * p * p * LocalIntensity(muEdge, u1, u2) / meanIntensity;
        }

        private static double LocalIntensity(double mu, double u1, double u2)
        {
            double oneMinusMu = 1.0 - mu;
            return 1.0 - u1 * oneMinusMu - u2 * oneMinusMu * oneMinusMu;
        }

        // Intersection area of the stellar disk (radius 1) and the planet disk (radius p) at center separation
        // d.
        private static double CircleOverlapArea(double d, double p)
        {
            if (d >= 1.0 + p) return 0.0;
            if (d <= 1.0 - p) return Math.PI * p * p;
            if (d <= p - 1.0) return Math.PI;  // planet larger than star, not physical here, completeness only

            double d2 = d * d;
            double cosHalf1 = Clamp((d2 + 1.0 - p * p) / (2.0 * d), -1.0, 1.0);
            double cosHalf2 = Clamp((d2 + p * p - 1.0) / (2.0 * d * p), -1.0, 1.0);
            double kernel = (-d + 1.0 + p) * (d + 1.0 - p) * (d - 1.0 + p) * (d + 1.0 + p);
            return Math.Acos(cosHalf1)
                 + p * p * Math.Acos(cosHalf2)
                 - 0.5 * Math.Sqrt(Math.Max(0.0, kernel));
        }

        // Quadratic limb-darkening coefficients vs Teff, interpolated from Claret & Bloemen 2011. Falls back to
        // solar values for unknown Teff.
        private static void QuadraticLimbDarkening(double? effectiveTempK, out double u1, out double u2)
        {
            double teff = effectiveTempK ?? 5800.0;
            double[] teffGrid = { 3500.0, 4500.0, 5000.0, 5800.0, 6500.0, 7500.0 };
            double[] u1Grid = { 0.56, 0.57, 0.51, 0.41, 0.31, 0.22 };
            double[] u2Grid = { 0.19, 0.16, 0.20, 0.26, 0.30, 0.32 };

            if (teff <= teffGrid[0]) { u1 = u1Grid[0]; u2 = u2Grid[0]; return; }
            int last = teffGrid.Length - 1;
            if (teff >= teffGrid[last]) { u1 = u1Grid[last]; u2 = u2Grid[last]; return; }

            int i = 1;
            while (teff > teffGrid[i]) i++;
            double f = (teff - teffGrid[i - 1]) / (teffGrid[i] - teffGrid[i - 1]);
            u1 = u1Grid[i - 1] + f * (u1Grid[i] - u1Grid[i - 1]);
            u2 = u2Grid[i - 1] + f * (u2Grid[i] - u2Grid[i - 1]);
        }

        private static double Clamp(double v, double min, double max)
        {
            return v < min ? min : (v > max ? max : v);
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
