using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Effective widths for reddened stars, memoised for one frame.
    ///
    /// SystemResponse.EffectiveWidthAngstromForReddenedStar runs a quadrature per call because
    /// tabulating a second axis on the colour table costs more to build than it saves. A wide field
    /// holds tens of thousands of stars, so the quadrature has to be shared: a frame covers one
    /// small solid angle, its stars sit behind much the same dust, and their intrinsic temperatures
    /// fall into a few dozen bins. In practice a handful of quadratures serve the whole field.
    ///
    /// Quantisation is what makes that sharing possible, so both bin widths are set by measured
    /// interpolation error rather than by taste -- see the harness in tools/reddening-tests.
    ///
    /// NOT THREAD SAFE, and not meant to be: one cache belongs to one frame's gather pass.
    /// </summary>
    public sealed class ReddenedResponseCache
    {
        /// <summary>
        /// Reddening bin width, magnitudes. A frame is one small solid angle, so its stars sit
        /// behind much the same dust and a fine bin still shares well.
        /// </summary>
        public const double ReddeningBinMag = 0.01;

        /// <summary>
        /// Temperature entries per decade in the per-reddening table. Three times the density of
        /// SystemResponse's own colour table, because a reddened width varies far more steeply with
        /// temperature than an unreddened one: at the table's 111 per decade the interpolation
        /// costs 0.7 percent, at 333 it costs a quarter of that.
        /// </summary>
        public const double TemperatureEntriesPerDecade = 333.0;

        private const double MinTeffK = 1500.0;
        private const double MaxTeffK = 60000.0;

        private readonly SystemResponse response;
        private readonly Dictionary<long, double[]> tables = new Dictionary<long, double[]>();
        private readonly int entryCount;
        private readonly double logMin, logMax;

        public ReddenedResponseCache(SystemResponse response)
        {
            this.response = response;
            logMin = Math.Log10(MinTeffK);
            logMax = Math.Log10(MaxTeffK);
            entryCount = (int)Math.Ceiling((logMax - logMin) * TemperatureEntriesPerDecade) + 1;
        }

        /// <summary>Quadratures actually run. Read after a frame to know whether the sharing is working.</summary>
        public int Evaluations { get; private set; }

        /// <summary>
        /// Effective width for a star of the given intrinsic temperature behind the given
        /// reddening. Falls through to the unreddened table with no estimate, which is what a
        /// catalogue carrying no reddening column gets.
        ///
        /// INTERPOLATED ON BOTH AXES, and both had to be. Rounding the temperature to its bin costs
        /// 3.3% in effective width and rounding the reddening costs 0.6%, each as large as or larger
        /// than the error this whole path exists to remove; interpolating both leaves 0.005%.
        /// </summary>
        public double EffectiveWidthAngstrom(double intrinsicTeffK, double eBv)
        {
            if (response == null) return 0.0;
            if (!(eBv > 0.0) || double.IsNaN(eBv))
                return response.EffectiveWidthAngstromForTemperature(intrinsicTeffK);

            double position = eBv / ReddeningBinMag;
            long lo = (long)Math.Floor(position);
            double frac = position - lo;

            double widthLo = Lookup(lo, intrinsicTeffK);
            if (frac <= 0.0) return widthLo;
            double widthHi = Lookup(lo + 1, intrinsicTeffK);
            return widthLo + frac * (widthHi - widthLo);
        }

        private double Lookup(long reddeningBin, double intrinsicTeffK)
        {
            double binnedEbv = reddeningBin * ReddeningBinMag;
            if (!tables.TryGetValue(reddeningBin, out double[] table))
            {
                table = new double[entryCount];
                for (int i = 0; i < entryCount; i++)
                {
                    double teff = Math.Pow(10.0, logMin + (logMax - logMin) * i / (entryCount - 1));
                    table[i] = response.EffectiveWidthAngstromForReddenedStar(teff, binnedEbv);
                }
                Evaluations += entryCount;
                tables[reddeningBin] = table;
            }

            if (!(intrinsicTeffK > 0.0))
                return response.EffectiveWidthAngstromForReddenedStar(0.0, binnedEbv);

            double position = (Math.Log10(intrinsicTeffK) - logMin) / (logMax - logMin) * (entryCount - 1);
            if (position <= 0.0) return table[0];
            if (position >= entryCount - 1) return table[entryCount - 1];

            int lo = (int)position;
            double frac = position - lo;
            return table[lo] + frac * (table[lo + 1] - table[lo]);
        }
    }
}
