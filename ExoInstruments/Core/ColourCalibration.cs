using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The transform from an instrument's own filters to CIE tristimulus values, fitted from the
    /// filters' real response curves.
    ///
    /// WHY A FIT AND NOT AN ASSIGNMENT. A frame taken through a red filter is not the red primary of
    /// a display. It is the source's spectrum integrated against that filter times the optics times
    /// the detector's quantum efficiency times the atmosphere -- a completely different weighting
    /// function from the CIE x-bar. Feeding the three band counts straight into R, G and B, which is
    /// what this replaces, produces an image whose colours depend on the filter set rather than on
    /// the sky: the same star comes out a different colour on two instruments.
    ///
    /// The correct operation is the one every camera does. Colour is three numbers, so a set of three
    /// bands determines it up to a 3x3 matrix, and that matrix is found by requiring it to reproduce
    /// the tristimulus values of a TRAINING SET of spectra passed through the same bands:
    ///
    ///     minimise  sum over spectra of | M . f(spectrum) - XYZ(spectrum) |^2
    ///
    /// where f is the vector of band responses. This is the same construction as a raw converter's
    /// colour matrix, and like one it has a residual, because three numbers cannot describe every
    /// spectrum -- two different spectra with the same band counts are metamers and must come out the
    /// same colour. That residual is measured and reported rather than hidden.
    ///
    /// THE TRAINING SET is what the instrument is pointed at: blackbodies across the stellar
    /// temperature range, which is every star and every reflecting body, and the nebular line
    /// spectrum, which is every H II region. Both are weighted, because a matrix fitted on
    /// continuum alone gets a nebula's colour wrong and one fitted on lines alone gets every star
    /// wrong.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public sealed class ColourCalibration
    {
        private readonly double[,] matrix;      // 3 rows (X, Y, Z) by bandCount columns
        private readonly int bandCount;

        /// <summary>Root-mean-square error of the fit, as a fraction of the training set's mean luminance. The colour error the matrix cannot avoid.</summary>
        public double RmsResidual { get; }

        /// <summary>Worst chromaticity error over the training set, in CIE xy distance. Under about 0.002 is a just-noticeable difference.</summary>
        public double WorstChromaticityError { get; }

        /// <summary>
        /// The same two figures split by what the spectrum is, because they mean different things: a
        /// continuum residual is how well the instrument measures a STAR's colour, and a line
        /// residual is how well it measures a NEBULA's. A filter set with gaps between its bands can
        /// be excellent at the first and hopeless at the second -- an [O III] line at 500.7 nm falls
        /// exactly between the RedCat's green and blue passbands, so almost no light from it reaches
        /// either, and no matrix can recover a colour from a measurement that was not made.
        /// </summary>
        public double ContinuumChromaticityError { get; }
        public double LineChromaticityError { get; }

        /// <summary>Median chromaticity error over the continuum training set -- the typical star, rather than the worst one at the end of the temperature range.</summary>
        public double MedianContinuumChromaticityError { get; }

        /// <summary>Spectra the fit was built from.</summary>
        public int TrainingSpectra { get; }

        private ColourCalibration(double[,] m, int bands, double rms, double worstXy, int training,
                                  double continuumXy, double lineXy, double medianContinuumXy)
        {
            MedianContinuumChromaticityError = medianContinuumXy;
            matrix = m;
            bandCount = bands;
            RmsResidual = rms;
            WorstChromaticityError = worstXy;
            TrainingSpectra = training;
            ContinuumChromaticityError = continuumXy;
            LineChromaticityError = lineXy;
        }

        /// <summary>
        /// Fits the transform for a set of bands. Returns null when the bands are degenerate -- fewer
        /// than three, or three that cannot span colour because two of them are the same filter.
        /// </summary>
        public static ColourCalibration Fit(IList<SystemResponse> bands)
        {
            if (bands == null || bands.Count < 3) return null;
            var throughputs = new List<Func<double, double>>(bands.Count);
            foreach (SystemResponse band in bands)
            {
                if (band == null) return null;
                SystemResponse local = band;
                throughputs.Add(m => local.ThroughputAt(m));
            }
            return Fit(throughputs);
        }

        /// <summary>
        /// The same, for bands given directly as throughput functions of wavelength in metres.
        ///
        /// This is the form the fit actually needs -- a band is nothing more than its throughput --
        /// and it is what lets the harness fit an IDEAL colorimeter, three bands proportional to the
        /// colour matching functions themselves. Those are a colorimeter by definition, so the fit
        /// against them must come out essentially exact, which separates "the fitting machinery is
        /// wrong" from "this filter set cannot measure that colour". Without such a control a large
        /// residual is uninterpretable.
        /// </summary>
        public static ColourCalibration Fit(IList<Func<double, double>> bands)
            => Fit(bands, includeLines: true);

        private static ColourCalibration Fit(IList<Func<double, double>> bands, bool includeLines)
        {
            if (bands == null || bands.Count < 3) return null;
            for (int i = 0; i < bands.Count; i++) if (bands[i] == null) return null;

            var samples = new List<double[]>();     // band responses
            var targets = new List<double[]>();     // XYZ
            var weights = new List<double>();
            var isLine = new List<bool>();

            // Continuum: blackbodies over the stellar range and below it, which covers every star,
            // planet and galaxy in the mod. Logarithmic in temperature, because colour changes with
            // log T rather than with T.
            const int continuumSamples = 48;
            for (int i = 0; i < continuumSamples; i++)
            {
                double t = 1500.0 * Math.Pow(40000.0 / 1500.0, i / (double)(continuumSamples - 1));
                double localT = t;
                AddSample(bands, samples, targets, weights,
                          l => Colorimetry.PlanckSpectralRadiance(l, localT), ContinuumWeight);
                while (isLine.Count < samples.Count) isLine.Add(false);
            }

            // Lines: the nebular spectrum, in the combinations a real nebula shows. Each is a comb of
            // delta functions, so it is added through the single-wavelength path rather than by
            // integrating a continuum.
            if (includeLines)
            {
                foreach (double[] comb in NebularCombs())
                {
                    AddLineSample(bands, samples, targets, weights, comb, LineWeight);
                    while (isLine.Count < samples.Count) isLine.Add(true);
                }
            }

            if (samples.Count < 3) return null;

            // Least squares, one right-hand side per tristimulus component. The normal equations are
            // small (bandCount x bandCount) and well conditioned for real filter sets, and solving
            // them directly avoids pulling in a decomposition for a 3x3 or 5x5 system.
            int n = bands.Count;
            var ata = new double[n, n];
            var atb = new double[n, 3];
            for (int s = 0; s < samples.Count; s++)
            {
                double w = weights[s];
                double[] f = samples[s];
                double[] xyz = targets[s];
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++) ata[i, j] += w * f[i] * f[j];
                    for (int k = 0; k < 3; k++) atb[i, k] += w * f[i] * xyz[k];
                }
            }

            var m = new double[3, n];
            for (int k = 0; k < 3; k++)
            {
                var rhs = new double[n];
                for (int i = 0; i < n; i++) rhs[i] = atb[i, k];
                if (!SolveSymmetric(ata, rhs, out double[] solution)) return null;
                for (int i = 0; i < n; i++) m[k, i] = solution[i];
            }

            // What the fit could not do. Both numbers matter and they measure different things: the
            // luminance residual says whether brightness is right, the chromaticity error says
            // whether colour is.
            double sumSq = 0.0, meanY = 0.0, worstXy = 0.0, worstContinuum = 0.0, worstLine = 0.0;
            var continuumErrors = new List<double>();
            for (int s = 0; s < samples.Count; s++)
            {
                double[] f = samples[s];
                double[] xyz = targets[s];
                double px = 0.0, py = 0.0, pz = 0.0;
                for (int i = 0; i < n; i++)
                {
                    px += m[0, i] * f[i];
                    py += m[1, i] * f[i];
                    pz += m[2, i] * f[i];
                }
                sumSq += (px - xyz[0]) * (px - xyz[0]) + (py - xyz[1]) * (py - xyz[1])
                       + (pz - xyz[2]) * (pz - xyz[2]);
                meanY += xyz[1];

                Colorimetry.XyzToChromaticity(px, py, pz, out double cx, out double cy);
                Colorimetry.XyzToChromaticity(xyz[0], xyz[1], xyz[2], out double rx, out double ry);
                double d = Math.Sqrt((cx - rx) * (cx - rx) + (cy - ry) * (cy - ry));
                if (d > worstXy) worstXy = d;
                if (isLine[s]) { if (d > worstLine) worstLine = d; }
                else
                {
                    continuumErrors.Add(d);
                    if (d > worstContinuum) worstContinuum = d;
                }
            }
            meanY /= samples.Count;
            double rms = meanY > 0.0
                ? Math.Sqrt(sumSq / (3.0 * samples.Count)) / meanY
                : double.NaN;

            continuumErrors.Sort();
            double medianContinuum = continuumErrors.Count > 0
                ? continuumErrors[continuumErrors.Count / 2] : double.NaN;
            return new ColourCalibration(m, n, rms, worstXy, samples.Count, worstContinuum, worstLine,
                                         medianContinuum);
        }

        /// <summary>
        /// Relative weights of the two kinds of training spectrum.
        ///
        /// Continuum carries more because it is what a broadband filter set can actually measure and
        /// what most of the sky is: every star, planet and galaxy. Weighting the two equally drags the
        /// continuum fit toward line spectra it cannot reproduce anyway -- an [O III] line falls in
        /// the gap between two passbands, and no matrix recovers a colour from light that was not
        /// collected. Both residuals are reported separately so the trade is visible rather than
        /// buried in one number.
        /// </summary>
        private const double ContinuumWeight = 4.0;
        private const double LineWeight = 1.0;

        /// <summary>Fits on continuum spectra alone, for the harness to measure what including the lines costs.</summary>
        public static ColourCalibration FitContinuumOnly(IList<Func<double, double>> bands)
            => Fit(bands, includeLines: false);

        /// <summary>
        /// Nebular line combinations, as relative photon rates in the lines the mod renders.
        ///
        /// Three regimes, all real: an H II region where H-alpha dominates and the forbidden lines
        /// are a quarter of it; the diffuse warm ionised medium where [N II] rivals H-alpha; and a
        /// high-excitation object where [O III] leads. Together they span the colours a line source
        /// can have, which is what the fit needs -- not a claim about any particular object.
        /// </summary>
        private static IEnumerable<double[]> NebularCombs()
        {
            // { Hbeta, [O III] 4959, [O III] 5007, [N II] 6548, Halpha, [N II] 6584, [S II] 6716, [S II] 6731 }
            yield return new[] { 0.35, 0.05, 0.15, 0.09, 1.00, 0.26, 0.12, 0.08 };   // H II region
            yield return new[] { 0.35, 0.02, 0.05, 0.25, 1.00, 0.73, 0.32, 0.22 };   // diffuse WIM
            yield return new[] { 0.35, 0.40, 1.20, 0.03, 1.00, 0.09, 0.05, 0.04 };   // high excitation
            yield return new[] { 0.35, 0.00, 0.00, 0.00, 1.00, 0.00, 0.00, 0.00 };   // pure recombination
        }

        private static readonly double[] CombWavelengthsNm =
        {
            486.132, 495.891, 500.684, 654.805, 656.280, 658.345, 671.644, 673.082,
        };

        private static void AddSample(IList<Func<double, double>> bands, List<double[]> samples,
                                      List<double[]> targets, List<double> weights,
                                      Func<double, double> spectrum, double weight)
        {
            var f = new double[bands.Count];
            for (int i = 0; i < bands.Count; i++) f[i] = BandResponse(bands[i], spectrum);
            Colorimetry.SpectrumToXyz(spectrum, out double x, out double y, out double z);
            if (!(y > 0.0)) return;

            // Normalised to unit luminance, so a 40000 K blackbody and a 1500 K one weigh the same
            // in the fit instead of the hot one dominating by six orders of magnitude.
            for (int i = 0; i < f.Length; i++) f[i] /= y;
            samples.Add(f);
            targets.Add(new[] { x / y, 1.0, z / y });
            weights.Add(weight);
        }

        private static void AddLineSample(IList<Func<double, double>> bands, List<double[]> samples,
                                          List<double[]> targets, List<double> weights,
                                          double[] comb, double weight)
        {
            // The comb is in PHOTON rates, because that is what a line list and a detector both
            // count. A band response is therefore the photon rate times the throughput, while the
            // tristimulus values are defined on ENERGY, so each line's photon rate is converted with
            // its own hc/lambda -- the constant hc drops out of the normalisation below, leaving 1/lambda.
            // Mixing the two, which this did, put the line samples and the continuum samples on
            // different scales and asked one matrix to fit both: 33% rms rather than 2%.
            var f = new double[bands.Count];
            double x = 0.0, y = 0.0, z = 0.0;
            for (int k = 0; k < comb.Length && k < CombWavelengthsNm.Length; k++)
            {
                if (!(comb[k] > 0.0)) continue;
                double lambdaNm = CombWavelengthsNm[k];
                for (int i = 0; i < bands.Count; i++)
                    f[i] += comb[k] * bands[i](lambdaNm * 1e-9);
                Colorimetry.LineToXyz(lambdaNm, comb[k] / lambdaNm, out double lx, out double ly, out double lz);
                x += lx; y += ly; z += lz;
            }
            if (!(y > 0.0)) return;
            for (int i = 0; i < f.Length; i++) f[i] /= y;
            samples.Add(f);
            targets.Add(new[] { x / y, 1.0, z / y });
            weights.Add(weight);
        }

        /// <summary>
        /// A band's response to a continuum: the spectrum integrated against the system's throughput,
        /// in PHOTONS, because that is what a detector counts.
        /// </summary>
        private static double BandResponse(Func<double, double> band, Func<double, double> spectrum)
        {
            const double stepNm = 1.0;
            double sum = 0.0;
            for (double l = Colorimetry.MinWavelengthNm; l <= Colorimetry.MaxWavelengthNm; l += stepNm)
            {
                double s = spectrum(l);
                if (!(s > 0.0)) continue;
                double t = band(l * 1e-9);
                if (!(t > 0.0)) continue;
                // Energy per nanometre times throughput, divided by the photon energy: proportional
                // to l, since E = hc/l.
                sum += s * t * l;
            }
            return sum * stepNm;
        }

        /// <summary>Tristimulus values from a pixel's band measurements.</summary>
        public void ToXyz(double[] bandValues, out double x, out double y, out double z)
        {
            x = y = z = 0.0;
            if (bandValues == null) return;
            int n = Math.Min(bandCount, bandValues.Length);
            for (int i = 0; i < n; i++)
            {
                x += matrix[0, i] * bandValues[i];
                y += matrix[1, i] * bandValues[i];
                z += matrix[2, i] * bandValues[i];
            }
        }

        /// <summary>The fitted matrix, row-major (X, Y, Z) by band, for the harness and the report.</summary>
        public double[,] Matrix
        {
            get
            {
                var copy = new double[3, bandCount];
                Array.Copy(matrix, copy, matrix.Length);
                return copy;
            }
        }

        /// <summary>Cholesky solve of a symmetric positive-definite system. False when the bands are degenerate.</summary>
        private static bool SolveSymmetric(double[,] a, double[] b, out double[] solution)
        {
            int n = b.Length;
            solution = null;
            var l = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    double sum = a[i, j];
                    for (int k = 0; k < j; k++) sum -= l[i, k] * l[j, k];
                    if (i == j)
                    {
                        if (!(sum > 1e-300)) return false;      // not positive definite
                        l[i, i] = Math.Sqrt(sum);
                    }
                    else
                    {
                        l[i, j] = sum / l[j, j];
                    }
                }
            }

            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = b[i];
                for (int k = 0; k < i; k++) sum -= l[i, k] * y[k];
                y[i] = sum / l[i, i];
            }
            var x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = y[i];
                for (int k = i + 1; k < n; k++) sum -= l[k, i] * x[k];
                x[i] = sum / l[i, i];
            }
            solution = x;
            return true;
        }
    }
}
