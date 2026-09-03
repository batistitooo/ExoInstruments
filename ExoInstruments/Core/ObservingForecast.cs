using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Observing-quality forecast grid for a (target, instrument) pair: one row per body
    /// rotation, one column per time slot. Quality is 0 when unobservable, else:
    /// transit: (sigma_ideal / sigma_actual)^2; imaging: airmass efficiency; RV: 1.
    /// No weather (stock KSP has none). Deterministic in UT; the forecast is exactly
    /// what the corresponding session would observe.
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
            /// Row-major quality grid, normalized so the best upcoming cell = 1.0. Relative by design: a
            /// scintillation-dominated pairing's raw values (~0.01) would render as a flat, useless map.
            /// </summary>
            public double[] Quality01;

            public double BestUt;            // center of the single best cell; NaN when nothing is observable

            /// <summary>The best cell's un-normalized quality (fraction of the zenith-moonless ideal), what the normalization divided by.</summary>
            public double PeakQualityRaw;

            public double CellUt(int row, int col) => StartUt + (row * (double)Columns + col + 0.5) * CellSeconds;
        }

        /// <summary>
        /// Computes the forecast grid starting at startUt. Rows span one body rotation so night structure lines
        /// up vertically, the standard observing-calendar layout. Safe to call off the main thread.
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

            // Zenith-moonless noise: the best this instrument can do on this target.
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
