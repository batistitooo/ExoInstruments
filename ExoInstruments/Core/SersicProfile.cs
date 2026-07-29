using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The Sersic (1968) surface-brightness profile, which is what a galaxy looks like.
    ///
    ///     I(R) = I_e exp{ -b_n [ (R/R_e)^(1/n) - 1 ] }
    ///
    /// R_e is the half-light radius and b_n is defined by the requirement that half the light lies
    /// inside it. n = 1 is the exponential disk of a spiral, n = 4 the de Vaucouleurs law of an
    /// elliptical; both are the same expression at different indices, which is why one profile
    /// covers the whole morphological sequence.
    ///
    /// WHY b_n IS SOLVED AND NOT QUOTED. Every textbook carries the series b_n = 2n - 1/3 +
    /// 4/(405n) + ... (Ciotti &amp; Bertin 1999). It is an asymptotic expansion, good to about 1e-5
    /// at n = 4 and progressively worse below n = 1, and its error goes straight into the total
    /// flux through the e^(b_n) factor. b_n has an exact definition -- the regularised incomplete
    /// gamma P(2n, b_n) = 1/2 -- so it is inverted numerically here instead, the same discipline
    /// OpticalPsf uses for its own FWHM constants. tools/galaxy-tests checks it against SciPy's
    /// gammaincinv.
    ///
    /// TOTAL FLUX. Integrating the profile over the plane gives
    ///
    ///     F = I_e R_e^2 * 2 pi n e^(b_n) Gamma(2n) / b_n^(2n)
    ///
    /// (Graham &amp; Driver 2005, PASA 22, 118, eq. 4), which is what turns a catalogued apparent
    /// magnitude into the profile's normalisation with nothing summed.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class SersicProfile
    {
        /// <summary>
        /// b_n: the constant for which half the profile's light lies inside R_e, i.e. the solution
        /// of P(2n, b_n) = 1/2 for the regularised lower incomplete gamma function.
        /// </summary>
        public static double Bn(double n)
        {
            if (!(n > 0.0)) return 0.0;
            double a = 2.0 * n;

            // The asymptotic series only supplies a bracket; the answer comes from the bisection.
            double guess = Math.Max(1e-6, a - 1.0 / 3.0 + 4.0 / (405.0 * n));
            double lo = Math.Max(1e-9, 0.25 * guess), hi = Math.Max(2.0 * guess, 1.0);
            while (RegularisedGammaP(a, hi) < 0.5 && hi < 1e6) hi *= 2.0;
            while (RegularisedGammaP(a, lo) > 0.5 && lo > 1e-9) lo *= 0.5;

            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (RegularisedGammaP(a, mid) < 0.5) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>
        /// Total flux of the profile divided by I_e R_e^2 -- the dimensionless factor of
        /// Graham &amp; Driver (2005) eq. 4. Multiply by I_e R_e^2 for the flux itself.
        /// </summary>
        public static double TotalFluxFactor(double n)
        {
            if (!(n > 0.0)) return 0.0;
            double b = Bn(n);
            return 2.0 * Math.PI * n * Math.Exp(b + LogGamma(2.0 * n) - 2.0 * n * Math.Log(b));
        }

        /// <summary>Fraction of the total flux inside radius R, which is P(2n, b_n (R/R_e)^(1/n)) exactly.</summary>
        public static double EnclosedFraction(double radiusOverRe, double n)
        {
            if (!(radiusOverRe > 0.0) || !(n > 0.0)) return 0.0;
            return RegularisedGammaP(2.0 * n, Bn(n) * Math.Pow(radiusOverRe, 1.0 / n));
        }

        /// <summary>Radius, in units of R_e, holding a given fraction of the total flux.</summary>
        public static double RadiusForEnclosedFraction(double fraction, double n)
        {
            if (!(fraction > 0.0) || fraction >= 1.0 || !(n > 0.0)) return double.NaN;
            double lo = 1e-6, hi = 1.0;
            while (EnclosedFraction(hi, n) < fraction && hi < 1e9) hi *= 2.0;
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (EnclosedFraction(mid, n) < fraction) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>
        /// Surface brightness at R, in magnitudes per square arcsecond, for a profile of total
        /// magnitude totalMag and half-light radius R_e in arcsec.
        ///
        /// The two are tied by the total-flux factor above: mu_e = m + 2.5 log10(f(n) R_e^2), and
        /// mu(R) = mu_e + (2.5 b_n / ln 10) [(R/R_e)^(1/n) - 1].
        /// </summary>
        public static double SurfaceBrightnessMagPerArcsec2(
            double radiusArcsec, double totalMag, double effectiveRadiusArcsec, double n)
        {
            if (!(effectiveRadiusArcsec > 0.0) || !(n > 0.0)) return double.NaN;
            double b = Bn(n);
            double muE = totalMag + 2.5 * Math.Log10(TotalFluxFactor(n) * effectiveRadiusArcsec * effectiveRadiusArcsec);
            return muE + 2.5 * b / Math.Log(10.0) * (Math.Pow(radiusArcsec / effectiveRadiusArcsec, 1.0 / n) - 1.0);
        }

        /// <summary>
        /// The half-light radius that makes a profile of the catalogued total magnitude reach the
        /// catalogued ISOPHOTAL radius at the catalogued surface brightness -- i.e. the R_e for
        /// which mu(isophotalRadius) = isophotalMag.
        ///
        /// This is how a galaxy's size gets into the render without inventing anything: RC3 and
        /// HyperLEDA publish the total magnitude and D25, the diameter of the 25 B-mag/arcsec^2
        /// isophote, and those two plus a profile shape determine the profile completely.
        ///
        /// TWO ROOTS, AND WHY ONLY ONE IS PHYSICAL. At fixed total flux, a very compact profile
        /// puts nothing at the isophotal radius and a very extended one spreads the same flux too
        /// thin, so the surface brightness there passes through a maximum as R_e grows. The
        /// physical branch is the compact one, R_e below the isophotal radius: on the other branch
        /// the half-light radius would lie outside the isophote that defines the galaxy's edge.
        ///
        /// Returns NaN when the maximum itself is fainter than the isophote, which is not a
        /// numerical failure but a real statement about the catalogue entry -- a galaxy whose
        /// total magnitude is too faint to reach 25 mag/arcsec^2 anywhere at its quoted size.
        /// Callers fall back to a shape-based ratio and say so.
        /// </summary>
        public static double EffectiveRadiusFromIsophote(
            double totalMag, double isophotalRadiusArcsec, double isophotalMagPerArcsec2, double n)
        {
            if (!(isophotalRadiusArcsec > 0.0) || !(n > 0.0)) return double.NaN;

            Func<double, double> mu = re => SurfaceBrightnessMagPerArcsec2(
                isophotalRadiusArcsec, totalMag, re, n);

            // Locate the maximum (numerically smallest mu) on the compact branch by golden search,
            // then bisect between it and zero, where mu diverges.
            double lo = 1e-4 * isophotalRadiusArcsec, hi = isophotalRadiusArcsec;
            const double phi = 0.6180339887498949;
            double c = hi - phi * (hi - lo), d = lo + phi * (hi - lo);
            for (int i = 0; i < 200; i++)
            {
                if (mu(c) < mu(d)) hi = d; else lo = c;
                c = hi - phi * (hi - lo);
                d = lo + phi * (hi - lo);
            }
            double brightestRe = 0.5 * (lo + hi);
            if (mu(brightestRe) > isophotalMagPerArcsec2) return double.NaN;

            double a = 1e-6 * isophotalRadiusArcsec, bHi = brightestRe;
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (a + bHi);
                if (mu(mid) > isophotalMagPerArcsec2) a = mid; else bHi = mid;
            }
            return 0.5 * (a + bHi);
        }

        // ------------------------------------------------------------------ special functions

        /// <summary>
        /// log Gamma(x) by the Lanczos approximation, g = 7, n = 9 (Press et al., Numerical
        /// Recipes 3rd ed., 6.1). Relative error below 1e-15 over the range these profiles use.
        /// </summary>
        public static double LogGamma(double x)
        {
            double[] c =
            {
                0.99999999999980993, 676.5203681218851, -1259.1392167224028,
                771.32342877765313, -176.61502916214059, 12.507343278686905,
                -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
            };
            if (x < 0.5)
                return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1.0 - x);

            x -= 1.0;
            double a = c[0];
            double t = x + 7.5;
            for (int i = 1; i < 9; i++) a += c[i] / (x + i);
            return 0.5 * Math.Log(2.0 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
        }

        /// <summary>
        /// Regularised lower incomplete gamma P(a, x) = gamma(a, x) / Gamma(a), by the series
        /// below x = a + 1 and the Lentz continued fraction for Q above it (Numerical Recipes
        /// 6.2). Both converge to machine precision in their own domain, which is why the switch
        /// is at the crossover rather than anywhere convenient.
        /// </summary>
        public static double RegularisedGammaP(double a, double x)
        {
            if (!(a > 0.0) || x < 0.0) return double.NaN;
            if (x == 0.0) return 0.0;
            return x < a + 1.0 ? GammaSeries(a, x) : 1.0 - GammaContinuedFraction(a, x);
        }

        private static double GammaSeries(double a, double x)
        {
            double ap = a, sum = 1.0 / a, del = sum;
            for (int i = 0; i < 1000; i++)
            {
                ap += 1.0;
                del *= x / ap;
                sum += del;
                if (Math.Abs(del) < Math.Abs(sum) * 1e-16) break;
            }
            return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
        }

        private static double GammaContinuedFraction(double a, double x)
        {
            const double tiny = 1e-300;
            double b = x + 1.0 - a, c = 1.0 / tiny, d = 1.0 / b, h = d;
            for (int i = 1; i < 1000; i++)
            {
                double an = -i * (i - a);
                b += 2.0;
                d = an * d + b;
                if (Math.Abs(d) < tiny) d = tiny;
                c = b + an / c;
                if (Math.Abs(c) < tiny) c = tiny;
                d = 1.0 / d;
                double del = d * c;
                h *= del;
                if (Math.Abs(del - 1.0) < 1e-16) break;
            }
            return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
        }
    }
}
