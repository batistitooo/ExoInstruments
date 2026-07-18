using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Forward-simulated observing-quality forecast for one (target, instrument)
    /// pairing: a grid of quality values over the nights ahead, one row per home
    /// body rotation ("night"), one column per time-of-day slot. Everything the
    /// per-instant models already compute, folded into one number per cell:
    ///
    /// - 0 when unobservable (daytime, below the telescope limit, or occulted
    ///   by a moon);
    /// - transit photometry: the noise-variance ratio
    ///   (sigma_zenith-moonless / sigma_actual)^2, scintillation and moonlit-sky
    ///   excess included -- the fraction of ideal per-exposure SNR^2 an exposure
    ///   taken at that moment actually delivers;
    /// - direct imaging: the session's own airmass efficiency 1/X^2 (its
    ///   integration weight, see ImagingObservingConditions);
    /// - radial velocity: 1 whenever observable -- the simulated spectrograph
    ///   precision genuinely doesn't vary with airmass or moonlight, and the
    ///   forecast must not invent structure the session won't deliver.
    ///
    /// Weather is deliberately absent: stock KSP has no weather system to read
    /// (see ImagingObservingConditions -- a hook for a weather mod is a
    /// roadmap item). Deterministic in UT, so the grid is a pure function of
    /// the same models the sessions run -- what the forecast promises is
    /// exactly what an observation started there will get.
    /// </summary>
    public static class ObservingForecast
    {
        public class ForecastResult
        {
            public double StartUt;
            public double CellSeconds;
            public int Columns;              // slots per night/row
            public int Rows;                 // nights covered

            /// <summary>
            /// Row-major [row * Columns + col], normalized so the best upcoming
            /// cell is 1.0. Relative on purpose: this is a scheduling tool
            /// ("when is it best"), and on a scintillation-dominated pairing the
            /// un-normalized fraction-of-zenith-ideal is a uniform ~0.01 that
            /// renders as a uselessly flat, dark map.
            /// </summary>
            public double[] Quality01;

            public double BestUt;            // center of the single best cell; NaN when nothing is observable

            /// <summary>The best cell's un-normalized quality (fraction of the zenith-moonless ideal) -- what the normalization divided by.</summary>
            public double PeakQualityRaw;

            public double CellUt(int row, int col) => StartUt + (row * (double)Columns + col + 0.5) * CellSeconds;
        }

        /// <summary>
        /// Computes the forecast grid starting at startUt. Row length is one
        /// body rotation, so fixed local-time structure (night, target transit)
        /// lines up vertically across rows -- the standard observing-calendar
        /// layout. Pure math over Evaluate(): safe to run off the main thread.
        /// </summary>
        public static ForecastResult Compute(
            StarTarget target, InstrumentSpec instrument, ImagingObserverContext observer,
            double startUt, int nights, int columnsPerNight)
        {
            double nightSeconds = observer.BodyRotationPeriodSeconds > 0
                ? observer.BodyRotationPeriodSeconds
                : 21600.0;
            double cellSeconds = nightSeconds / columnsPerNight;

            var result = new ForecastResult
            {
                StartUt = startUt,
                CellSeconds = cellSeconds,
                Columns = columnsPerNight,
                Rows = nights,
                Quality01 = new double[nights * columnsPerNight],
                BestUt = double.NaN,
                PeakQualityRaw = 0.0,
            };

            // The ideal per-exposure noise this instrument could ever achieve on
            // this target: zenith, moonless. The transit noise penalty is measured
            // against it.
            double idealSigma = instrument.Method == DetectionMethod.Transit
                ? LightCurveSimulator.TotalNoiseSigma(target, instrument, 1.0)
                : 0.0;

            for (int i = 0; i < result.Quality01.Length; i++)
            {
                double ut = startUt + (i + 0.5) * cellSeconds;
                ImagingConditionsSnapshot c = ImagingObservingConditions.Evaluate(ut, target.RaDeg, target.DecDeg, observer);

                double quality;
                if (!c.Observable)
                {
                    quality = 0.0;
                }
                else if (instrument.Method == DetectionMethod.Transit && idealSigma > 0.0)
                {
                    double actualSigma = LightCurveSimulator.TotalNoiseSigma(target, instrument, c.Airmass, c.MoonSkyFactor);
                    double ratio = idealSigma / actualSigma;
                    quality = ratio * ratio;
                }
                else if (instrument.Method == DetectionMethod.DirectImaging)
                {
                    quality = c.Efficiency;
                }
                else
                {
                    quality = 1.0;
                }

                result.Quality01[i] = quality;
                if (quality > result.PeakQualityRaw)
                {
                    result.PeakQualityRaw = quality;
                    result.BestUt = ut;
                }
            }

            if (result.PeakQualityRaw > 0.0)
            {
                for (int i = 0; i < result.Quality01.Length; i++)
                {
                    result.Quality01[i] /= result.PeakQualityRaw;
                }
            }
            return result;
        }
    }
}
