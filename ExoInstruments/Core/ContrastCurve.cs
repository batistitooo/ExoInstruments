using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The number a high-contrast observation exists to produce: how faint a companion could have
    /// been seen, as a function of how far from the star it sits.
    ///
    /// WHY THIS IS THE DELIVERABLE. An image of a star with nothing beside it is not a null result
    /// until someone says what would have been visible. The contrast curve is that statement, and
    /// it is what every direct-imaging paper reports, what every survey's completeness is computed
    /// from, and what decides whether a non-detection constrains anything at all. A pipeline that
    /// renders coronagraphic frames but cannot say what they rule out has stopped one step short of
    /// the science.
    ///
    /// "FIVE SIGMA" IS NOT FIVE SIGMA AT SMALL SEPARATIONS, and getting that wrong is the single
    /// most common error in the literature this file exists to avoid. The noise at a given
    /// separation is estimated from the resolution elements in an annulus at that separation, and
    /// close to the star there are very few of them: at 2 lambda/D the annulus holds about 12, and
    /// at 1 lambda/D about 6. Estimating a standard deviation from six numbers and then treating
    /// it as if it were known exactly is what a Gaussian threshold assumes, and it is wrong in the
    /// optimistic direction.
    ///
    /// Mawet et al. (2014, ApJ 792, 97) give the correction: with n resolution elements available,
    /// the test statistic follows a Student t distribution with n-1 degrees of freedom, and the
    /// threshold for a false-alarm probability equal to a Gaussian 5 sigma is
    ///
    ///     threshold = tau_xi * sigma * sqrt(1 + 1/n)
    ///
    /// where tau_xi is the t-statistic for that probability at n-1 degrees of freedom, and the
    /// sqrt(1 + 1/n) accounts for the noise on the estimate of the mean being subtracted. The
    /// penalty is severe where it matters: at 2 lambda/D the threshold is several times the naive
    /// 5 sigma, so a curve computed without it overstates the achieved contrast by more than a
    /// magnitude exactly in the region an instrument was built to reach.
    ///
    /// Pure C#, no Unity dependency. Verified against VIP's own contrast_curve in
    /// tools/coronagraph-tests, VIP being the reference implementation of this measurement.
    /// </summary>
    public static class ContrastCurve
    {
        /// <summary>One point of a contrast curve.</summary>
        public struct Point
        {
            /// <summary>Separation from the star, in milliarcseconds.</summary>
            public double SeparationMas;
            /// <summary>Resolution elements available in the annulus at this separation.</summary>
            public double ResolutionElements;
            /// <summary>Standard deviation of the resolution-element fluxes in the annulus, in the frame's own units.</summary>
            public double NoiseSigma;
            /// <summary>The threshold in units of that sigma, after the small-sample penalty.</summary>
            public double ThresholdInSigma;
            /// <summary>Detectable flux, in the frame's own units, before any throughput correction.</summary>
            public double DetectableFlux;
            /// <summary>Detectable flux as a fraction of the unocculted stellar peak: the contrast.</summary>
            public double Contrast;
            /// <summary>The same, in magnitudes.</summary>
            public double ContrastMagnitudes;
        }

        /// <summary>
        /// Number of independent resolution elements in an annulus of the given radius: the
        /// circumference divided by one resolution element.
        ///
        /// This is the count Mawet et al.'s penalty is a function of, and it is why the penalty
        /// grows without bound as the separation falls: at r = lambda/(2 pi D) the annulus holds
        /// one element and there is nothing left to estimate a variance from.
        /// </summary>
        public static double ResolutionElementsInAnnulus(double separationMas, double resolutionElementMas)
        {
            if (!(resolutionElementMas > 0.0) || !(separationMas > 0.0)) return 0.0;
            return 2.0 * Math.PI * separationMas / resolutionElementMas;
        }

        /// <summary>
        /// The one-sided Gaussian tail probability that "5 sigma" names, and the target every
        /// threshold below is computed to match.
        ///
        /// Quoted rather than computed at call time so that the number this file means by five
        /// sigma is written down once: 2.866516e-7.
        /// </summary>
        public const double FiveSigmaTailProbability = 2.866515719235352e-7;

        /// <summary>
        /// The threshold, in units of the measured standard deviation, that gives the requested
        /// false-alarm probability when the standard deviation itself was estimated from n samples.
        ///
        /// Two effects, both from Mawet et al. (2014), and they compound: the t distribution's
        /// heavier tail at few degrees of freedom, and the sqrt(1 + 1/n) inflation from having had
        /// to estimate the mean as well. Returns positive infinity below two samples, which is the
        /// honest answer rather than a large number: with one resolution element there is no
        /// variance to speak of and no detection can be claimed.
        /// </summary>
        public static double ThresholdInSigma(double resolutionElements, double falseAlarmProbability)
        {
            if (!(resolutionElements >= 2.0)) return double.PositiveInfinity;

            int dof = (int)Math.Floor(resolutionElements) - 1;
            if (dof < 1) return double.PositiveInfinity;

            double t = StudentTQuantile(1.0 - falseAlarmProbability, dof);
            return t * Math.Sqrt(1.0 + 1.0 / resolutionElements);
        }

        /// <summary>
        /// Measures a contrast curve from a frame.
        ///
        /// The noise at each separation is the standard deviation of NON-OVERLAPPING apertures of
        /// one resolution element laid around the annulus, not the pixel-to-pixel standard
        /// deviation. That distinction is the whole of the measurement: speckles are correlated
        /// over a resolution element, so a pixel-wise sigma counts each speckle several times and
        /// comes out too small. VIP, and the literature it implements, place apertures for exactly
        /// this reason.
        ///
        /// starPeak is the peak of the UNOCCULTED stellar PSF in the same units as the frame, which
        /// is what a real observer measures from an offset or neutral-density exposure and what
        /// turns a flux into a contrast.
        ///
        /// throughput may be null, and should not be for an ADI-processed frame: post-processing
        /// removes companion flux as well as speckles (see Core.AngularDifferentialImaging), and a
        /// contrast curve that ignores it reports the instrument as better than it is. When
        /// supplied it is evaluated per separation and divides the detectable flux.
        /// </summary>
        public static List<Point> Measure(
            float[] frame, int width, int height,
            double plateScaleMasPerPixel, double resolutionElementMas,
            double starPeak, double innerWorkingAngleMas, double outerRadiusMas,
            double falseAlarmProbability, Func<double, double> throughput)
        {
            var points = new List<Point>();
            if (frame == null || width <= 0 || height <= 0) return points;
            if (!(plateScaleMasPerPixel > 0.0) || !(resolutionElementMas > 0.0)) return points;

            double cx = 0.5 * (width - 1), cy = 0.5 * (height - 1);
            double stepMas = resolutionElementMas;                 // one curve point per resolution element
            double apertureRadiusPx = 0.5 * resolutionElementMas / plateScaleMasPerPixel;

            for (double sep = Math.Max(stepMas, innerWorkingAngleMas); sep <= outerRadiusMas; sep += stepMas)
            {
                double n = ResolutionElementsInAnnulus(sep, resolutionElementMas);
                int apertures = (int)Math.Floor(n);
                if (apertures < 2) continue;

                double sepPx = sep / plateScaleMasPerPixel;
                var fluxes = new double[apertures];
                bool complete = true;

                for (int k = 0; k < apertures; k++)
                {
                    double angle = 2.0 * Math.PI * k / apertures;
                    double ax = cx + sepPx * Math.Cos(angle);
                    double ay = cy + sepPx * Math.Sin(angle);
                    if (!TryApertureSum(frame, width, height, ax, ay, apertureRadiusPx, out fluxes[k]))
                    {
                        complete = false;
                        break;
                    }
                }
                if (!complete) break;      // the annulus has left the frame; nothing beyond it is measurable either

                double mean = 0.0;
                for (int k = 0; k < apertures; k++) mean += fluxes[k];
                mean /= apertures;

                double variance = 0.0;
                for (int k = 0; k < apertures; k++) { double d = fluxes[k] - mean; variance += d * d; }
                variance /= (apertures - 1);          // sample variance, which is what the t statistic assumes
                double sigma = Math.Sqrt(variance);

                double threshold = ThresholdInSigma(apertures, falseAlarmProbability);
                double detectable = threshold * sigma;

                double tp = throughput != null ? throughput(sep) : 1.0;
                if (tp > 0.0) detectable /= tp;

                double contrast = starPeak > 0.0 ? detectable / starPeak : double.NaN;

                points.Add(new Point
                {
                    SeparationMas = sep,
                    ResolutionElements = apertures,
                    NoiseSigma = sigma,
                    ThresholdInSigma = threshold,
                    DetectableFlux = detectable,
                    Contrast = contrast,
                    ContrastMagnitudes = contrast > 0.0 ? -2.5 * Math.Log10(contrast) : double.NaN,
                });
            }

            return points;
        }

        // Sum of the frame inside a circular aperture, by exact pixel-centre membership. Centre membership
        // rather than partial-pixel area, matching what aperture photometry packages do by default and what
        // VIP's own contrast measurement does, so that the two can be compared without the comparison measuring
        // a difference of convention. Returns false when the aperture would reach outside the frame, which the
        // caller treats as the end of the measurable range rather than padding with zeros: a zero-padded
        // aperture reads as an anomalously faint resolution element and biases the annulus's variance downward,
        // which would make the contrast curve improve at exactly the radius where the data runs out.
        private static bool TryApertureSum(
            float[] frame, int width, int height, double cx, double cy, double radiusPx, out double sum)
        {
            sum = 0.0;
            int x0 = (int)Math.Floor(cx - radiusPx), x1 = (int)Math.Ceiling(cx + radiusPx);
            int y0 = (int)Math.Floor(cy - radiusPx), y1 = (int)Math.Ceiling(cy + radiusPx);
            if (x0 < 0 || y0 < 0 || x1 >= width || y1 >= height) return false;

            double r2 = radiusPx * radiusPx;
            for (int y = y0; y <= y1; y++)
            {
                double dy = y - cy;
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - cx;
                    if (dx * dx + dy * dy <= r2) sum += frame[y * width + x];
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ Student t

        /// <summary>
        /// The Student t quantile: the value t for which P(T &lt;= t) = p at the given degrees of
        /// freedom.
        ///
        /// By bisection on the cumulative distribution below. Not the fastest route, and it does
        /// not need to be: a contrast curve has of order a hundred points and each needs one
        /// quantile. Bisection is chosen over a series expansion because the probabilities involved
        /// are extreme (2.9e-7 in the tail) and every published rational approximation to the t
        /// quantile is fitted over a range that stops far short of it.
        /// </summary>
        public static double StudentTQuantile(double p, int degreesOfFreedom)
        {
            if (degreesOfFreedom < 1) return double.PositiveInfinity;
            if (p <= 0.0) return double.NegativeInfinity;
            if (p >= 1.0) return double.PositiveInfinity;

            double lo = 0.0, hi = 1.0;
            while (StudentTCdf(hi, degreesOfFreedom) < p)
            {
                hi *= 2.0;
                if (hi > 1e12) return hi;
            }
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (StudentTCdf(mid, degreesOfFreedom) < p) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>
        /// The Student t cumulative distribution, via the regularised incomplete beta function:
        /// for t &gt;= 0, P(T &lt;= t) = 1 - 0.5 * I_x(nu/2, 1/2) with x = nu/(nu + t^2).
        /// </summary>
        public static double StudentTCdf(double t, int degreesOfFreedom)
        {
            double nu = degreesOfFreedom;
            double x = nu / (nu + t * t);
            double half = 0.5 * RegularisedIncompleteBeta(0.5 * nu, 0.5, x);
            return t >= 0.0 ? 1.0 - half : half;
        }

        /// <summary>
        /// The regularised incomplete beta function I_x(a,b), by the continued fraction of
        /// Numerical Recipes' betacf with its symmetry reflection, which is the standard route and
        /// converges over the whole range once the reflection puts x on the favourable side.
        /// </summary>
        public static double RegularisedIncompleteBeta(double a, double b, double x)
        {
            if (x <= 0.0) return 0.0;
            if (x >= 1.0) return 1.0;

            double front = Math.Exp(
                NoiseSampler.LogGamma(a + b) - NoiseSampler.LogGamma(a) - NoiseSampler.LogGamma(b)
                + a * Math.Log(x) + b * Math.Log(1.0 - x));

            if (x < (a + 1.0) / (a + b + 2.0))
                return front * BetaContinuedFraction(a, b, x) / a;
            return 1.0 - Math.Exp(
                NoiseSampler.LogGamma(a + b) - NoiseSampler.LogGamma(a) - NoiseSampler.LogGamma(b)
                + b * Math.Log(1.0 - x) + a * Math.Log(x)) * BetaContinuedFraction(b, a, 1.0 - x) / b;
        }

        private static double BetaContinuedFraction(double a, double b, double x)
        {
            const double Tiny = 1e-300;
            double qab = a + b, qap = a + 1.0, qam = a - 1.0;
            double c = 1.0;
            double d = 1.0 - qab * x / qap;
            if (Math.Abs(d) < Tiny) d = Tiny;
            d = 1.0 / d;
            double h = d;

            for (int m = 1; m <= 300; m++)
            {
                int m2 = 2 * m;
                double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
                d = 1.0 + aa * d; if (Math.Abs(d) < Tiny) d = Tiny;
                c = 1.0 + aa / c; if (Math.Abs(c) < Tiny) c = Tiny;
                d = 1.0 / d;
                h *= d * c;

                aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
                d = 1.0 + aa * d; if (Math.Abs(d) < Tiny) d = Tiny;
                c = 1.0 + aa / c; if (Math.Abs(c) < Tiny) c = Tiny;
                d = 1.0 / d;
                double del = d * c;
                h *= del;

                if (Math.Abs(del - 1.0) < 3e-16) break;
            }
            return h;
        }
    }
}
