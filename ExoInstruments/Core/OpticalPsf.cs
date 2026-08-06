using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The instrument's real point-spread function, built from first principles instead of being
    /// stood in for by a generic blur kernel.
    ///
    /// Two exact ingredients, convolved:
    ///
    /// 1. DIFFRACTION: the Fraunhofer pattern of the telescope's own ANNULAR pupil (circular
    ///    aperture with the real central obstruction of its secondary mirror). This is
    ///    |FT(pupil)|^2 in closed form (Born &amp; Wolf, "Principles of Optics", the obstructed-
    ///    aperture case): with x = pi*D*theta/lambda and obstruction ratio eps,
    ///
    ///        I(x)/I(0) = { [ 2*J1(x)/x - eps^2 * 2*J1(eps*x)/(eps*x) ] / (1 - eps^2) }^2
    ///
    ///    Real Airy rings, and the real effect of the obstruction on them (a larger secondary
    ///    pushes energy out of the core and into the first ring). No profile is assumed.
    ///
    /// 2. ATMOSPHERE: the exact long-exposure Kolmogorov term, not a fitted Gaussian or
    ///    Moffat profile. Fried (1966) gives the long-exposure atmospheric transfer function
    ///
    ///        T_atm(f) = exp[ -3.44 * (lambda*f/r0)^(5/3) ]
    ///
    ///    for angular frequency f (cycles/radian), which is Kolmogorov turbulence's own
    ///    5/3-power structure function and nothing more. Because that has no closed-form
    ///    real-space counterpart, the PSF is recovered by numerically Hankel-transforming it
    ///    (a radially symmetric 2D Fourier transform is a zeroth-order Hankel transform), which
    ///    is exact up to quadrature error rather than a shape approximation.
    ///
    /// Why this matters, and why the box blur it replaces was wrong: a box kernel's transfer
    /// function is a sinc, which has ZEROS and NEGATIVE LOBES. It doesn't merely soften an
    /// image; at some spatial frequencies it annihilates detail outright and at others it
    /// inverts contrast. Mid-scale structure (crater-sized features on a resolved planetary
    /// disk) sits squarely in that range, so a box blur destroyed far more real detail than its
    /// nominal width implied, and did so unphysically. Every profile here has a monotonically
    /// decreasing transfer function with no zeros inside the passband.
    ///
    /// Pure C# with no Unity dependency, like the rest of Core; so it can be exercised by a
    /// standalone harness against published reference values.
    /// </summary>
    public static class OpticalPsf
    {
        /// <summary>Radians per arcsecond.</summary>
        private const double ArcsecToRad = Math.PI / (180.0 * 3600.0);

        /// <summary>
        /// Hard ceiling on the kernel's half-width in pixels. Airy wings extend formally to
        /// infinity, so ANY finite implementation truncates somewhere (professional simulation
        /// codes included); the kernel is renormalised to unit sum afterwards so truncation
        /// costs no flux, only the very faintest far wings. This is the one approximation in
        /// this file, and it is a computational bound rather than a physical assumption.
        ///
        /// Raised from 48 to 128 because 48 was not a faint place to stop. At the RC20's 0.0688
        /// arcsec pixels it fell 1.32 seeing-FWHM out, where the Kolmogorov profile is still
        /// 1.8e-2 of its peak, and the renormalisation that follows conserves the flux but not
        /// that step; so a bright star showed a square edge at 1.8% of its own core brightness.
        /// 128 reaches 3.5 FWHM and 4.3e-4 there, 42 times fainter, for about 1.5x the transform
        /// work (a wider kernel needs a larger tile, but proportionally fewer of them). The
        /// residuals per instrument are measured in tools/psf-truncation.
        ///
        /// A wide, heavy-tailed component cannot be handled by raising this further; see
        /// FourierConvolution.RadialKernelSpectrum, which carries one across the whole frame.
        /// </summary>
        private const int MaxKernelRadiusPx = 128;

        /// <summary>Kernel half-width is this many times the relevant FWHM before the ceiling above applies, far enough out to carry the first several Airy rings.</summary>
        private const double KernelRadiusInFwhm = 3.0;

        /// <summary>
        /// Fraction of its own peak the atmospheric profile must have fallen to at the kernel's
        /// edge. 1e-4 is where the step stops being the brightest thing at that radius: the
        /// Kolmogorov wing itself falls as theta^(-11/3), so one more pixel outward costs only a
        /// few percent, and a discontinuity of 1e-4 of a star's peak sits under the read noise for
        /// anything short of a naked-eye star.
        /// </summary>
        private const double AtmosphericTailFraction = 1e-4;

        // ---------------------------------------------------------------- Bessel functions

        /// <summary>
        /// J0, via the standard polynomial approximations of Abramowitz &amp; Stegun 9.4.1/9.4.3
        /// (absolute error &lt; 5e-8 and &lt; 1.6e-8 respectively). A numerical method for a
        /// well-defined special function, not a physical approximation.
        /// </summary>
        public static double BesselJ0(double x)
        {
            x = Math.Abs(x);
            if (x < 3.0)
            {
                double t = x / 3.0, t2 = t * t;
                return 1.0 + t2 * (-2.2499997 + t2 * (1.2656208 + t2 * (-0.3163866
                     + t2 * (0.0444479 + t2 * (-0.0039444 + t2 * 0.0002100)))));
            }
            else
            {
                double t = 3.0 / x;
                double f = 0.79788456 + t * (-0.00000077 + t * (-0.00552740 + t * (-0.00009512
                         + t * (0.00137237 + t * (-0.00072805 + t * 0.00014476)))));
                double theta = x - 0.78539816 + t * (-0.04166397 + t * (-0.00003954 + t * (0.00262573
                             + t * (-0.00054125 + t * (-0.00029333 + t * 0.00013558)))));
                return f * Math.Cos(theta) / Math.Sqrt(x);
            }
        }

        /// <summary>J1, via Abramowitz &amp; Stegun 9.4.4/9.4.6 (same accuracy class as BesselJ0). Odd, so the sign of x is carried through.</summary>
        public static double BesselJ1(double x)
        {
            double ax = Math.Abs(x), result;
            if (ax < 3.0)
            {
                double t = ax / 3.0, t2 = t * t;
                result = ax * (0.5 + t2 * (-0.56249985 + t2 * (0.21093573 + t2 * (-0.03954289
                       + t2 * (0.00443319 + t2 * (-0.00031761 + t2 * 0.00001109))))));
            }
            else
            {
                double t = 3.0 / ax;
                double f = 0.79788456 + t * (0.00000156 + t * (0.01659667 + t * (0.00017105
                         + t * (-0.00249511 + t * (0.00113653 + t * -0.00020033)))));
                double theta = ax - 2.35619449 + t * (0.12499612 + t * (0.00005650 + t * (-0.00637879
                             + t * (0.00074348 + t * (0.00079824 + t * -0.00029166)))));
                result = f * Math.Cos(theta) / Math.Sqrt(ax);
            }
            return x < 0.0 ? -result : result;
        }

        // ---------------------------------------------------------------- Diffraction

        /// <summary>
        /// Normalised intensity (1.0 on axis) of the annular-pupil Airy pattern at angular
        /// offset theta, for a real aperture diameter, central obstruction ratio (secondary
        /// diameter / primary diameter) and wavelength. See the class summary for the closed
        /// form and its source.
        /// </summary>
        public static double AiryIntensity(double thetaRad, double apertureMeters, double obstructionRatio, double wavelengthMeters)
        {
            if (apertureMeters <= 0.0 || wavelengthMeters <= 0.0) return thetaRad == 0.0 ? 1.0 : 0.0;
            double eps = Math.Max(0.0, Math.Min(0.95, obstructionRatio));

            double x = Math.PI * apertureMeters * Math.Abs(thetaRad) / wavelengthMeters;
            if (x < 1e-9) return 1.0; // removable singularity: both 2*J1(u)/u terms -> 1

            double outer = 2.0 * BesselJ1(x) / x;
            double inner = eps > 1e-9 ? eps * eps * (2.0 * BesselJ1(eps * x) / (eps * x)) : 0.0;
            double amp = (outer - inner) / (1.0 - eps * eps);
            return amp * amp;
        }

        /// <summary>
        /// FWHM (arcsec) of that diffraction pattern's core, found by bisection on the exact
        /// profile rather than quoted from the usual 1.028*lambda/D rule of thumb; the rule
        /// only holds for an UNOBSTRUCTED aperture, and every telescope modelled here has a
        /// secondary mirror that narrows the core and redistributes energy into the rings.
        /// </summary>
        public static double AiryFwhmArcsec(double apertureMeters, double obstructionRatio, double wavelengthMeters)
        {
            if (apertureMeters <= 0.0 || wavelengthMeters <= 0.0) return 0.0;

            // The half-power point always lies inside the first null, which itself is at or
            // within 1.22*lambda/D for any obstruction, a safe bracket.
            double hi = 1.22 * wavelengthMeters / apertureMeters;
            double lo = 0.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (AiryIntensity(mid, apertureMeters, obstructionRatio, wavelengthMeters) > 0.5) lo = mid;
                else hi = mid;
            }
            return 2.0 * (0.5 * (lo + hi)) / ArcsecToRad; // half-width -> full width
        }

        // ---------------------------------------------------------------- Atmosphere

        /// <summary>
        /// The constant k in the long-exposure seeing relation FWHM = k * lambda / r0, MEASURED
        /// from the profile this file evaluates rather than quoted.
        ///
        /// This used to be the literature's round 0.98 (Roddier 1981), and that was an internal
        /// inconsistency rather than a sourcing choice: the exact Kolmogorov profile below has a
        /// half-power point at rho = 3.0648, so its own FWHM is 0.97554 lambda/r0, and inverting
        /// with 0.98 therefore delivered a PSF 0.45% NARROWER than the seeing figure the caller
        /// asked for. A telescope told to deliver Paranal's 0.72 arcsec produced 0.7167.
        ///
        /// Deriving the constant from the profile removes the discrepancy by construction, and it
        /// is not a private convention: GalSim, which tabulates the same transform by a different
        /// method, reports 0.9758634. The two agree to 0.03%, which is this bisection's own
        /// resolution. Same discipline as AiryFwhmArcsec, which bisects the real Airy profile
        /// instead of quoting the 1.028 lambda/D rule of thumb that only holds unobstructed.
        /// </summary>
        public static readonly double SeeingFwhmOverLambdaR0 = MeasureSeeingFwhmConstant();

        private static double MeasureSeeingFwhmConstant()
        {
            // In reduced form rho = 2*pi*r0*theta/lambda, so r0 = 1 and lambda = 2*pi make
            // rho = theta and let the profile be probed in its own only variable.
            const double r0 = 1.0, lambda = 2.0 * Math.PI;
            double peak = AtmosphericIntensity(0.0, r0, lambda);

            double lo = 0.0, hi = 6.0; // the half-power point is near rho = 3
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (AtmosphericIntensity(mid, r0, lambda) > 0.5 * peak) lo = mid; else hi = mid;
            }
            // FWHM = 2 * rho_half in reduced units, and theta = rho * lambda / (2*pi*r0), so
            // FWHM_theta = (rho_half / pi) * lambda / r0.
            return 0.5 * (lo + hi) / Math.PI;
        }

        /// <summary>
        /// Kernel half-width the atmospheric term needs, in units of its own FWHM, to fall to
        /// AtmosphericTailFraction of its peak, measured from the profile rather than assumed,
        /// in the same reduced variable and by the same bisection as SeeingFwhmOverLambdaR0. The
        /// profile has one shape, so this is a constant of it and not of any instrument.
        ///
        /// Declared after SeeingFwhmOverLambdaR0 because it divides by it, and static field
        /// initialisers run in declaration order.
        /// </summary>
        public static readonly double AtmosphericTailRadiusInFwhm = MeasureTailRadius();

        private static double MeasureTailRadius()
        {
            const double r0 = 1.0, lambda = 2.0 * Math.PI;   // makes rho = theta
            double peak = AtmosphericIntensity(0.0, r0, lambda);
            double lo = 0.0, hi = 400.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (AtmosphericIntensity(mid, r0, lambda) > AtmosphericTailFraction * peak) lo = mid; else hi = mid;
            }
            // rho -> theta is the same scaling the FWHM constant carries, so dividing by it in the
            // same units leaves a pure ratio.
            return 0.5 * (lo + hi) / Math.PI / SeeingFwhmOverLambdaR0;
        }

        /// <summary>
        /// Fried parameter r0 (metres) corresponding to a seeing FWHM, via the long-exposure
        /// relation FWHM = k * lambda / r0 with k measured from the profile itself; see
        /// SeeingFwhmOverLambdaR0 for why the constant is measured rather than quoted.
        /// </summary>
        public static double FriedParameterMeters(double seeingFwhmArcsec, double wavelengthMeters)
        {
            double fwhmRad = seeingFwhmArcsec * ArcsecToRad;
            if (fwhmRad <= 0.0) return double.PositiveInfinity;
            return SeeingFwhmOverLambdaR0 * wavelengthMeters / fwhmRad;
        }

        /// <summary>
        /// Factor turning AtmosphericIntensity's output into the fraction of a source's TOTAL flux
        /// landing in one pixel, the normalisation, in closed form, with nothing summed.
        ///
        /// AtmosphericIntensity evaluates PSF(rho) = Int_0^inf T(u) J0(rho u) u du, which is the
        /// order-zero Hankel transform of Fried's OTF. That transform is self-reciprocal, so
        /// Int_0^inf PSF(rho) rho drho = T(0) = 1 exactly, and the integral over the plane is
        /// therefore 2*pi. A pixel spans drho = 2*pi*r0*p/lambda on a side for plate scale p, so
        /// its share is PSF * drho^2 / (2*pi).
        ///
        /// Why it matters that this is analytic: the alternative is to divide a finite kernel by
        /// its own sum, which quietly hands the flux that fell outside the kernel back to the
        /// pixels inside it. For a compact PSF that is a rounding error. For a seeing halo whose
        /// wings genuinely run off the edge of the sensor, it is an invention; that light left,
        /// and a detector never saw it.
        /// </summary>
        public static double AtmosphericPerPixelScale(double friedParameterMeters, double wavelengthMeters, double plateScaleArcsecPerPixel)
        {
            if (friedParameterMeters <= 0.0 || wavelengthMeters <= 0.0 || plateScaleArcsecPerPixel <= 0.0) return 0.0;
            double dRho = 2.0 * Math.PI * friedParameterMeters * (plateScaleArcsecPerPixel * ArcsecToRad) / wavelengthMeters;
            return dRho * dRho / (2.0 * Math.PI);
        }

        /// <summary>
        /// The long-exposure atmospheric profile tabulated against pixel radius, for callers that
        /// must evaluate it millions of times across a frame. Each entry costs a Bessel quadrature;
        /// the profile depends on radius alone and is smooth on the scale of a quarter pixel, so
        /// tabulating once and interpolating is the same discipline SampleRadial already uses.
        ///
        /// Values are already scaled by AtmosphericPerPixelScale, i.e. they are fractions of the
        /// source's total flux and sum to 1 over the whole plane.
        /// </summary>
        public sealed class AtmosphericProfileTable
        {
            private const int SamplesPerPixel = 4;
            private readonly double[] _lut;

            public AtmosphericProfileTable(double maxRadiusPx, double plateScaleArcsecPerPixel,
                                           double friedParameterMeters, double wavelengthMeters)
            {
                double scale = AtmosphericPerPixelScale(friedParameterMeters, wavelengthMeters, plateScaleArcsecPerPixel);
                int count = (int)Math.Ceiling(Math.Max(1.0, maxRadiusPx) * SamplesPerPixel) + 2;
                _lut = new double[count];
                for (int i = 0; i < count; i++)
                {
                    double rPx = (double)i / SamplesPerPixel;
                    _lut[i] = scale * Math.Max(0.0, AtmosphericIntensity(
                        rPx * plateScaleArcsecPerPixel * ArcsecToRad, friedParameterMeters, wavelengthMeters));
                }
            }

            public double AtPixelRadius(double radiusPx)
            {
                double pos = radiusPx * SamplesPerPixel;
                int i = (int)pos;
                if (i >= _lut.Length - 1) return _lut[_lut.Length - 1];
                double f = pos - i;
                return _lut[i] * (1.0 - f) + _lut[i + 1] * f;
            }
        }

        /// <summary>
        /// Long-exposure Kolmogorov atmospheric PSF at angular offset theta, up to an overall
        /// constant (the kernel is normalised later, so the constant is irrelevant).
        ///
        /// Evaluates the zeroth-order Hankel transform of Fried's OTF,
        ///     PSF(r) proportional to  Integral[ exp(-3.44 u^(5/3)) * J0(rho*u) * u , {u,0,inf} ],
        /// after substituting u = lambda*f/r0, which leaves rho = 2*pi*r0*theta/lambda as the
        /// only argument. The integrand is killed by its own exponential: at u = 4,
        /// exp(-3.44*u^(5/3)) is below 1e-15, so the upper limit is finite in practice.
        ///
        /// THE STEP COUNT HAS TO FOLLOW RHO, and a fixed one is where this used to be wrong. The
        /// integrand oscillates with J0(rho*u), whose period in u is 2*pi/rho, so the number of
        /// oscillations across the range grows linearly with rho, which is to say, with how far
        /// into the wings the profile is being asked about. At a fixed 512 steps the quadrature was
        /// accurate to 0.3% out to 5 lambda/r0 and then failed progressively: it returned 4.5% high
        /// at 8 lambda/r0, 46% high at 12, and a factor of 10.2 high at 20, turning the true
        /// theta^(-11/3) Kolmogorov wing into an apparent theta^(-2.2). That is not a small error in
        /// a faint place: the seeing halo is what aperture photometry integrates over, and a wing
        /// an order of magnitude too bright puts light in the sky annulus that is not there.
        ///
        /// SamplesPerOscillation below fixes the resolution PER PERIOD instead, which is the
        /// quantity Simpson's error actually depends on. Verified against a high-order adaptive
        /// quadrature of the same integral: the fitted wing index over 6-18 lambda/r0 becomes
        /// -3.70, against -3.7097 exact and -3.667 for the asymptotic power law.
        /// </summary>
        public static double AtmosphericIntensity(double thetaRad, double friedParameterMeters, double wavelengthMeters)
        {
            if (double.IsInfinity(friedParameterMeters) || friedParameterMeters <= 0.0)
                return thetaRad == 0.0 ? 1.0 : 0.0;

            double rho = 2.0 * Math.PI * friedParameterMeters * Math.Abs(thetaRad) / wavelengthMeters;

            const double uMax = 4.0;

            // Oscillations of J0(rho*u) across [0, uMax], and enough Simpson points on each.
            double oscillations = rho * uMax / (2.0 * Math.PI);
            int steps = (int)Math.Ceiling(SamplesPerOscillation * oscillations);
            if (steps < MinQuadratureSteps) steps = MinQuadratureSteps;
            if (steps > MaxQuadratureSteps) steps = MaxQuadratureSteps;
            if ((steps & 1) != 0) steps++;  // Simpson needs an even count

            double h = uMax / steps;
            double sum = 0.0;
            for (int i = 0; i <= steps; i++)
            {
                double u = i * h;
                double integrand = Math.Exp(-3.44 * Math.Pow(u, 5.0 / 3.0)) * BesselJ0(rho * u) * u;
                double weight = (i == 0 || i == steps) ? 1.0 : ((i % 2 == 1) ? 4.0 : 2.0);
                sum += weight * integrand;
            }
            return sum * h / 3.0;
        }

        /// <summary>
        /// Simpson points per oscillation of J0 in the atmospheric quadrature. 24 is where the
        /// wing index converges: 6 (which a fixed 512 steps amounts to at rho = 126) gives -2.18,
        /// 24 gives -3.70, and quadrupling it again to 96 moves the profile by under 1e-4 anywhere.
        /// </summary>
        private const int SamplesPerOscillation = 24;

        /// <summary>Floor on the step count, so the smooth core is integrated as finely as it always was.</summary>
        private const int MinQuadratureSteps = 512;

        /// <summary>
        /// Ceiling, reached at about 27 lambda/r0. Beyond that the profile is 1e-5 of its peak and
        /// far outside any kernel this file builds, so the bound costs nothing real; it exists so
        /// that a caller asking about an absurd radius cannot make one sample unbounded.
        /// </summary>
        private const int MaxQuadratureSteps = 4096;

        // ---------------------------------------------------------------- Kernel assembly

        /// <summary>
        /// Builds the instrument's full normalised PSF as a square (2R+1)x(2R+1) kernel sampled
        /// at the current plate scale, ready for convolution. Returns the kernel and sets
        /// radiusPx to R.
        ///
        /// The diffraction and atmospheric terms are each sampled on their own grid and then
        /// convolved, which is the definition of what the light actually undergoes (the two
        /// effects act in series) rather than a blend or a quadrature-summed single profile.
        ///
        /// atmosphericFwhmArcsec == 0 gives a purely diffraction-limited kernel, correct for a
        /// space telescope, and the right limiting behaviour for an instrument whose atmospheric
        /// residual has been driven below its own diffraction limit.
        /// </summary>
        public static float[] BuildKernel(
            double plateScaleArcsecPerPixel,
            double apertureMeters,
            double obstructionRatio,
            double wavelengthMeters,
            double atmosphericFwhmArcsec,
            double defocusDiscRadiusPx,
            out int radiusPx)
            => BuildKernel(plateScaleArcsecPerPixel, apertureMeters, obstructionRatio, wavelengthMeters,
                           atmosphericFwhmArcsec, defocusDiscRadiusPx, 0, 0.0, 0.0, out radiusPx);

        /// <summary>The spider overload without a Gaussian term, kept so existing ground-based callers read unchanged.</summary>
        public static float[] BuildKernel(
            double plateScaleArcsecPerPixel,
            double apertureMeters,
            double obstructionRatio,
            double wavelengthMeters,
            double atmosphericFwhmArcsec,
            double defocusDiscRadiusPx,
            int vaneCount,
            double vaneWidthMeters,
            out int radiusPx)
            => BuildKernel(plateScaleArcsecPerPixel, apertureMeters, obstructionRatio, wavelengthMeters,
                           atmosphericFwhmArcsec, defocusDiscRadiusPx, vaneCount, vaneWidthMeters, 0.0,
                           null, out radiusPx);

        /// <summary>The overload without mirror pads, for the pupils that have none.</summary>
        public static float[] BuildKernel(
            double plateScaleArcsecPerPixel,
            double apertureMeters,
            double obstructionRatio,
            double wavelengthMeters,
            double atmosphericFwhmArcsec,
            double defocusDiscRadiusPx,
            int vaneCount,
            double vaneWidthMeters,
            double gaussianFwhmArcsec,
            out int radiusPx)
            => BuildKernel(plateScaleArcsecPerPixel, apertureMeters, obstructionRatio, wavelengthMeters,
                           atmosphericFwhmArcsec, defocusDiscRadiusPx, vaneCount, vaneWidthMeters,
                           gaussianFwhmArcsec, null, out radiusPx);

        /// <summary>
        /// As above, but for a pupil whose secondary sits on a spider. With vanes the diffraction
        /// term stops being radially symmetric; it grows the spikes every real reflector shows,
        /// so it is sampled in two dimensions from PupilDiffraction instead of from the radial
        /// closed form. The atmospheric and defocus terms are unaffected and stay radial.
        ///
        /// vaneCount = 0 takes the radial path and is bit-for-bit the previous behaviour.
        ///
        /// Note on truncation: spikes formally run across the whole frame, while this kernel is
        /// bounded by MaxKernelRadiusPx. The kernel therefore carries the spikes only within its
        /// own support and is renormalised as always, so no flux is lost but the very far spike
        /// wings are not drawn. That is the same computational bound the Airy wings already have.
        ///
        /// gaussianFwhmArcsec adds a fourth, Gaussian component. It carries the two effects that
        /// really are Gaussian and that a ground instrument does not have to care about:
        ///
        ///   * the optics' own residual wavefront error, the polishing figure a real mirror is
        ///     left with, which is why HST delivers 0.067 arcsec at 500 nm where its 2.4 m
        ///     aperture alone would give 0.044. The WFC3 Instrument Handbook states outright
        ///     that "the PSFs over most of the UVIS wavelength range are well described by
        ///     gaussian profiles (before pixelation)" (Sect. 6.6.1), so this is the profile its
        ///     own published table is quoted against, not a convenient stand-in;
        ///   * the spacecraft's pointing excursion over the exposure (see PointingStability).
        ///
        /// The two are independent and are handed in already summed in quadrature by the caller.
        /// Zero leaves the kernel exactly as it was.
        /// </summary>
        public static float[] BuildKernel(
            double plateScaleArcsecPerPixel,
            double apertureMeters,
            double obstructionRatio,
            double wavelengthMeters,
            double atmosphericFwhmArcsec,
            double defocusDiscRadiusPx,
            int vaneCount,
            double vaneWidthMeters,
            double gaussianFwhmArcsec,
            PupilPad[] pads,
            out int radiusPx)
        {
            radiusPx = 0;
            if (plateScaleArcsecPerPixel <= 0.0 || apertureMeters <= 0.0 || wavelengthMeters <= 0.0)
                return null;

            // Component 1: diffraction. Always present; it is the instrument's hard limit.
            double airyFwhm = AiryFwhmArcsec(apertureMeters, obstructionRatio, wavelengthMeters);
            int accR = RadiusFor(airyFwhm, plateScaleArcsecPerPixel);
            double[] acc;
            bool hasPads = pads != null && pads.Length > 0;
            bool hasVanes = vaneCount > 0 && vaneWidthMeters > 0.0;
            if (hasVanes || hasPads)
            {
                // Spikes and pad shadows reach far beyond the core, so the diffraction term is
                // given the widest support the kernel budget allows rather than the core's own
                // few pixels.
                //
                // THE SUPPORT MUST NOT BE SCALED BY THE CORE, which is what a multiple of the Airy
                // FWHM does, and it is the reason Hubble's spikes were invisible. A spike is faint
                // structure reaching far out; how far it can be SEEN is set by the source's
                // brightness against the sky, not by the width of the core it comes from. Tying
                // the two together fails hardest exactly where the optics are best: Hubble's Airy
                // FWHM is 0.052 arcsec, so at the UVIS plate scale eight of them is 11 pixels, and
                // binned 4x4 it is 3. The pupil sum was computed in full and then thrown away
                // inside a kernel three pixels across.
                //
                // So the vaned case takes the whole budget. That is what the paragraph above
                // always claimed and never did. It costs a 257x257 kernel where the radial path
                // needs a handful of taps, which is why it is spent only when there is azimuthal
                // structure to carry: vaneCount = 0 still takes the core-sized radial path.
                accR = MaxKernelRadiusPx;
                var pupil = new PupilDiffraction(apertureMeters, obstructionRatio, wavelengthMeters,
                                                 hasVanes ? vaneCount : 0,
                                                 hasVanes ? vaneWidthMeters : 0.0,
                                                 0.0, pads);
                acc = SampleTwoDimensional(accR, plateScaleArcsecPerPixel, pupil);
            }
            else
            {
                acc = SampleRadial(accR, plateScaleArcsecPerPixel,
                    theta => AiryIntensity(theta, apertureMeters, obstructionRatio, wavelengthMeters));
            }

            // Component 2: atmosphere. The effects act in series along the light path, so they
            // compose by convolution, not by blending profiles or summing widths in quadrature.
            double atmFwhm = Math.Max(0.0, atmosphericFwhmArcsec);
            if (atmFwhm > 0.0)
            {
                // Sized by where the profile has actually got faint, not by a multiple of its FWHM:
                // a Kolmogorov wing at 3 FWHM is still 1e-3 of the peak, where an Airy wing at the
                // same multiple of its own core is 1e-6. The two components need different rules
                // because they have different tails.
                int atmR = Math.Max(1, Math.Min(MaxKernelRadiusPx,
                    (int)Math.Ceiling(AtmosphericTailRadiusInFwhm * atmFwhm / plateScaleArcsecPerPixel)));
                double r0 = FriedParameterMeters(atmFwhm, wavelengthMeters);
                double[] atm = SampleRadial(atmR, plateScaleArcsecPerPixel,
                    theta => AtmosphericIntensity(theta, r0, wavelengthMeters));

                int outR = Math.Min(MaxKernelRadiusPx, accR + atmR);
                acc = Convolve(acc, accR, atm, atmR, outR);
                accR = outR;
            }

            // Component 2b: the Gaussian term, wavefront error and pointing (see the summary).
            // Placed before defocus and after the atmosphere for no reason other than order of
            // discovery: convolution commutes, so where it sits in this chain cannot matter.
            double gaussFwhm = Math.Max(0.0, gaussianFwhmArcsec);
            if (gaussFwhm > 0.0)
            {
                // A Gaussian is down to 1e-6 of peak at 2.2 FWHM, so it needs nothing like the
                // atmospheric profile's reach; three FWHM is already beyond any pixel that could
                // carry a value.
                int gR = Math.Max(1, Math.Min(MaxKernelRadiusPx,
                    (int)Math.Ceiling(3.0 * gaussFwhm / plateScaleArcsecPerPixel)));
                double[] g = SampleGaussian(gR, plateScaleArcsecPerPixel, gaussFwhm);

                int outR = Math.Min(MaxKernelRadiusPx, accR + gR);
                acc = Convolve(acc, accR, g, gR, outR);
                accR = outR;
            }

            // Component 3: defocus, when the observer has taken manual focus off its optimum.
            // Geometrical optics gives a uniformly illuminated blur disc of the defocused
            // cone's radius; so this one really is a flat-topped kernel, unlike the box blur
            // that used to stand in for the whole PSF. Its transfer function has genuine zeros,
            // which is a real property of defocus (they are why a defocused image can show
            // contrast reversals), not a numerical artefact.
            if (defocusDiscRadiusPx >= 0.5)
            {
                int discR = (int)Math.Min(MaxKernelRadiusPx, Math.Ceiling(defocusDiscRadiusPx));
                double[] disc = SampleDisc(discR, defocusDiscRadiusPx);

                int outR = Math.Min(MaxKernelRadiusPx, accR + discR);
                acc = Convolve(acc, accR, disc, discR, outR);
                accR = outR;
            }

            radiusPx = accR;
            return Normalise(acc, accR);
        }

        /// <summary>
        /// The instrument's PSF built ACROSS its passband rather than at one wavelength, with
        /// atmospheric dispersion folded in.
        ///
        /// TWO EFFECTS, ONE KERNEL, AND WHY THAT IS EXACT. Everything in the monochromatic kernel
        /// depends on wavelength: the Airy pattern scales as lambda/D, the seeing disc as
        /// lambda^(-1/5) through r0, and the atmosphere lifts the source by an angle that depends on
        /// colour, so a star at low altitude is smeared into a short spectrum pointing at the zenith.
        /// A frame is not monochromatic, so what it records is the sum of the monochromatic images
        /// weighted by how many photons arrive at each wavelength.
        ///
        /// Convolution is linear, so that sum can be taken on the KERNELS before convolving instead
        /// of on the images afterwards:
        ///
        ///     sum_i w_i (image * K_i)  =  image * (sum_i w_i K_i)
        ///
        /// One convolution with the weighted mean kernel is therefore not an approximation of a
        /// chromatic PSF, it IS one, and it costs nothing beyond building the kernel. Each
        /// sub-band's kernel is laid down at its own dispersion offset, which is what makes the
        /// smear appear.
        ///
        /// WHAT STAYS COMMON AND WHAT DOES NOT. The dispersion offset depends only on wavelength and
        /// zenith distance, both the same for every source in a field arcminutes across, so the smear
        /// is genuinely common and belongs in the kernel. The WEIGHTS depend on the source's own
        /// spectrum, and a red star's smear is shorter than a blue one's. That second-order
        /// difference is not in this kernel; the first-order part of it, the shift of a source's
        /// own centroid with its colour, is applied per source where the source is deposited.
        /// </summary>
        /// <param name="subBands">Wavelength (m), photon weight, dispersion offset (pixels) and Gaussian FWHM (arcsec) per sub-band. Weights need not be normalised.</param>
        public static float[] BuildChromaticKernel(
            double plateScaleArcsecPerPixel,
            double apertureMeters,
            double obstructionRatio,
            double atmosphericFwhmArcsecAtReference,
            double referenceWavelengthMeters,
            double defocusDiscRadiusPx,
            int vaneCount,
            double vaneWidthMeters,
            IList<ChromaticSubBand> subBands,
            out int radiusPx)
            => BuildChromaticKernel(plateScaleArcsecPerPixel, apertureMeters, obstructionRatio,
                                    atmosphericFwhmArcsecAtReference, referenceWavelengthMeters,
                                    defocusDiscRadiusPx, vaneCount, vaneWidthMeters, null,
                                    subBands, out radiusPx);

        /// <summary>As above, for a pupil that also carries mirror support pads.</summary>
        public static float[] BuildChromaticKernel(
            double plateScaleArcsecPerPixel,
            double apertureMeters,
            double obstructionRatio,
            double atmosphericFwhmArcsecAtReference,
            double referenceWavelengthMeters,
            double defocusDiscRadiusPx,
            int vaneCount,
            double vaneWidthMeters,
            PupilPad[] pads,
            IList<ChromaticSubBand> subBands,
            out int radiusPx)
        {
            radiusPx = 0;
            if (subBands == null || subBands.Count == 0) return null;
            if (plateScaleArcsecPerPixel <= 0.0 || apertureMeters <= 0.0) return null;

            double totalWeight = 0.0;
            foreach (ChromaticSubBand band in subBands)
                if (band.Weight > 0.0 && band.WavelengthMeters > 0.0) totalWeight += band.Weight;
            if (!(totalWeight > 0.0)) return null;

            // Two passes: size the output first, because the offsets push the support out and a
            // kernel that has to be grown after the fact would have to be re-accumulated.
            //
            // THE SUB-BANDS ARE BUILT IN PARALLEL, and that changes nothing about the kernel.
            // Each one is an independent BuildKernel over its own wavelength: no sub-band reads
            // another's result, and the weighted sum below still runs serially in sub-band order,
            // so the accumulation order (which is what a floating-point sum depends on) is the
            // order the caller supplied whatever the thread count. What it buys is real: this
            // loop is twelve numerical Hankel quadratures and twelve direct kernel convolutions,
            // measured at a quarter of the whole capture on the RC20 at 4x4 binning, and it
            // cannot be cached between exposures because the seeing follows the airmass and the
            // airmass follows the sky.
            var bandKernels = new float[subBands.Count][];
            var bandRadii = new int[subBands.Count];

            Action<int> buildBand = i =>
            {
                ChromaticSubBand band = subBands[i];
                if (!(band.Weight > 0.0) || !(band.WavelengthMeters > 0.0)) return;

                // Seeing scales as lambda^(-1/5): r0 goes as lambda^(6/5) and the FWHM as
                // lambda/r0. Fried (1966); the same exponent every seeing-monitor paper quotes.
                double fwhm = atmosphericFwhmArcsecAtReference > 0.0 && referenceWavelengthMeters > 0.0
                    ? atmosphericFwhmArcsecAtReference
                      * Math.Pow(band.WavelengthMeters / referenceWavelengthMeters, -0.2)
                    : atmosphericFwhmArcsecAtReference;

                bandKernels[i] = BuildKernel(plateScaleArcsecPerPixel, apertureMeters, obstructionRatio,
                                             band.WavelengthMeters, fwhm, defocusDiscRadiusPx,
                                             vaneCount, vaneWidthMeters, band.GaussianFwhmArcsec,
                                             pads, out bandRadii[i]);
            };

            if (subBands.Count > 1 && ParallelWork.MaxWorkers > 1)
                Parallel.For(0, subBands.Count, ParallelWork.Options, buildBand);
            else
                for (int i = 0; i < subBands.Count; i++) buildBand(i);

            int maxRadius = 1;
            var kernels = new List<float[]>(subBands.Count);
            var radii = new List<int>(subBands.Count);
            var used = new List<ChromaticSubBand>(subBands.Count);
            for (int i = 0; i < subBands.Count; i++)
            {
                if (bandKernels[i] == null) continue;
                ChromaticSubBand band = subBands[i];
                kernels.Add(bandKernels[i]);
                radii.Add(bandRadii[i]);
                used.Add(band);

                int reach = bandRadii[i] + (int)Math.Ceiling(Math.Sqrt(band.OffsetX * band.OffsetX
                                                                    + band.OffsetY * band.OffsetY));
                if (reach > maxRadius) maxRadius = reach;
            }
            if (kernels.Count == 0) return null;
            maxRadius = Math.Min(MaxKernelRadiusPx, maxRadius);

            int size = 2 * maxRadius + 1;
            var acc = new double[size * size];
            for (int b = 0; b < kernels.Count; b++)
            {
                float[] k = kernels[b];
                int r = radii[b];
                int ks = 2 * r + 1;
                double w = used[b].Weight / totalWeight;

                // The offset is fractional, so each sub-band's kernel is laid down with bilinear
                // weights rather than snapped to a pixel. Snapping would quantise the smear into
                // steps and, worse, bias the centroid by up to half a pixel.
                double ox = used[b].OffsetX, oy = used[b].OffsetY;
                int fx = (int)Math.Floor(ox), fy = (int)Math.Floor(oy);
                double tx = ox - fx, ty = oy - fy;

                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        double v = k[(dy + r) * ks + dx + r];
                        if (v <= 0.0) continue;
                        v *= w;
                        Accumulate(acc, size, maxRadius, dx + fx, dy + fy, v * (1.0 - tx) * (1.0 - ty));
                        Accumulate(acc, size, maxRadius, dx + fx + 1, dy + fy, v * tx * (1.0 - ty));
                        Accumulate(acc, size, maxRadius, dx + fx, dy + fy + 1, v * (1.0 - tx) * ty);
                        Accumulate(acc, size, maxRadius, dx + fx + 1, dy + fy + 1, v * tx * ty);
                    }
                }
            }

            radiusPx = maxRadius;
            return Normalise(acc, maxRadius);
        }

        private static void Accumulate(double[] acc, int size, int radius, int dx, int dy, double v)
        {
            if (v == 0.0) return;
            int x = dx + radius, y = dy + radius;
            if (x < 0 || x >= size || y < 0 || y >= size) return;
            acc[y * size + x] += v;
        }

        /// <summary>
        /// The wide, uncorrected seeing halo of an adaptive-optics PSF: the pure long-exposure
        /// Kolmogorov profile at the site's own median seeing, normalised to unit sum.
        ///
        /// FALLBACK PATH. A halo this wide cannot be truncated anywhere a kernel can afford to
        /// stop; see FourierConvolution.RadialKernelSpectrum, which the caller in
        /// SolarSystemCameraTexture.ApplyPsf uses instead, reaching this only on a frame too large
        /// to pad for one.
        ///
        /// This deliberately does NOT convolve in the diffraction pattern the way BuildKernel
        /// does. At the scales involved the omission is quantified and negligible: an 8.2m
        /// aperture's 18 mas core broadens a 650 mas halo to sqrt(650^2 + 18^2) = 650.2 mas,
        /// a 0.04% change in width, in exchange for a convolution of two very large kernels.
        /// The halo is carried at a coarser radius budget than the core for the same reason;
        /// it has no fine structure to preserve, only total width and enclosed flux.
        /// </summary>
        public static float[] BuildSeeingHaloKernel(
            double plateScaleArcsecPerPixel,
            double seeingFwhmArcsec,
            double wavelengthMeters,
            int maxRadiusPx,
            out int radiusPx)
        {
            radiusPx = 0;
            if (plateScaleArcsecPerPixel <= 0.0 || seeingFwhmArcsec <= 0.0 || wavelengthMeters <= 0.0) return null;

            int r = (int)Math.Ceiling(AtmosphericTailRadiusInFwhm * seeingFwhmArcsec / plateScaleArcsecPerPixel);
            r = Math.Max(1, Math.Min(Math.Max(1, maxRadiusPx), r));

            double r0 = FriedParameterMeters(seeingFwhmArcsec, wavelengthMeters);
            double[] halo = SampleRadial(r, plateScaleArcsecPerPixel,
                theta => Math.Max(0.0, AtmosphericIntensity(theta, r0, wavelengthMeters)));

            radiusPx = r;
            return Normalise(halo, r);
        }

        /// <summary>
        /// Measured FWHM (arcsec) of a finished kernel, read off its own radial profile with
        /// linear interpolation between samples so the answer isn't quantised to whole pixels.
        /// </summary>
        public static double MeasureKernelFwhmArcsec(float[] kernel, int radius, double plateScaleArcsecPerPixel)
        {
            if (kernel == null || radius < 1) return 0.0;
            int size = 2 * radius + 1;
            double peak = kernel[radius * size + radius];
            if (peak <= 0.0) return 0.0;

            for (int x = 1; x <= radius; x++)
            {
                double prev = kernel[radius * size + radius + x - 1];
                double cur = kernel[radius * size + radius + x];
                if (cur <= 0.5 * peak)
                {
                    double frac = (prev - 0.5 * peak) / Math.Max(1e-12, prev - cur);
                    return 2.0 * (x - 1 + frac) * plateScaleArcsecPerPixel;
                }
            }
            return 2.0 * radius * plateScaleArcsecPerPixel;
        }

        /// <summary>
        /// The atmospheric FWHM which, once convolved with THIS telescope's own diffraction
        /// pattern, makes the finished PSF deliver exactly deliveredFwhmArcsec.
        ///
        /// Solved by bisection on the real kernel rather than by subtracting the diffraction
        /// term in quadrature. Quadrature is only exact for Gaussians, and neither an Airy
        /// pattern nor a Kolmogorov profile is one; both carry far heavier wings, so the naive
        /// subtraction leaves a PSF measurably wider than the instrument's published figure
        /// (about 29 mas against SPHERE/SAXO's quoted 25). Inverting numerically makes the
        /// published number the thing the finished frame actually delivers, which is the whole
        /// point of quoting it.
        ///
        /// Returns 0 when diffraction alone already meets or exceeds the delivered figure.
        /// </summary>
        public static double AtmosphericFwhmForDelivered(
            double deliveredFwhmArcsec,
            double plateScaleArcsecPerPixel,
            double apertureMeters,
            double obstructionRatio,
            double wavelengthMeters)
        {
            if (deliveredFwhmArcsec <= 0.0) return 0.0;

            double diffractionOnly = MeasuredFwhmFor(0.0, plateScaleArcsecPerPixel, apertureMeters, obstructionRatio, wavelengthMeters);
            if (diffractionOnly >= deliveredFwhmArcsec) return 0.0;

            double lo = 0.0, hi = deliveredFwhmArcsec;
            for (int i = 0; i < 24; i++)
            {
                double mid = 0.5 * (lo + hi);
                double fwhm = MeasuredFwhmFor(mid, plateScaleArcsecPerPixel, apertureMeters, obstructionRatio, wavelengthMeters);
                if (fwhm < deliveredFwhmArcsec) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        private static double MeasuredFwhmFor(double atmFwhm, double plateScale, double aperture, double obstruction, double wavelength)
        {
            float[] k = BuildKernel(plateScale, aperture, obstruction, wavelength, atmFwhm, 0.0, out int r);
            return MeasureKernelFwhmArcsec(k, r, plateScale);
        }

        /// <summary>
        /// The Gaussian FWHM which, convolved with THIS telescope's own diffraction pattern,
        /// makes the finished PSF deliver exactly deliveredFwhmArcsec.
        ///
        /// The Gaussian counterpart of AtmosphericFwhmForDelivered, solved the same way and for
        /// the same reason: an Airy pattern is not a Gaussian, so subtracting the diffraction
        /// core in quadrature leaves a kernel measurably wider than the published figure. What
        /// this exists for is to take an instrument's OWN tabulated delivered FWHM, which is
        /// what an observatory publishes and what a user can check, and turn it into the one
        /// number the kernel builder needs, so the finished frame reproduces the table.
        ///
        /// Returns 0 when diffraction alone already meets or exceeds the delivered figure, which
        /// is the correct answer and not a failure: it says the published width is at or below
        /// this aperture's own limit, and nothing should be added.
        /// </summary>
        public static double GaussianFwhmForDelivered(
            double deliveredFwhmArcsec,
            double plateScaleArcsecPerPixel,
            double apertureMeters,
            double obstructionRatio,
            double wavelengthMeters,
            int vaneCount,
            double vaneWidthMeters)
        {
            if (deliveredFwhmArcsec <= 0.0) return 0.0;

            double diffractionOnly = MeasuredGaussianFwhmFor(
                0.0, plateScaleArcsecPerPixel, apertureMeters, obstructionRatio, wavelengthMeters,
                vaneCount, vaneWidthMeters);
            if (diffractionOnly >= deliveredFwhmArcsec) return 0.0;

            double lo = 0.0, hi = deliveredFwhmArcsec;
            for (int i = 0; i < 24; i++)
            {
                double mid = 0.5 * (lo + hi);
                double fwhm = MeasuredGaussianFwhmFor(mid, plateScaleArcsecPerPixel, apertureMeters,
                                                      obstructionRatio, wavelengthMeters,
                                                      vaneCount, vaneWidthMeters);
                if (fwhm < deliveredFwhmArcsec) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        private static double MeasuredGaussianFwhmFor(double gaussFwhm, double plateScale, double aperture,
                                                      double obstruction, double wavelength,
                                                      int vaneCount, double vaneWidthMeters)
        {
            float[] k = BuildKernel(plateScale, aperture, obstruction, wavelength, 0.0, 0.0,
                                    vaneCount, vaneWidthMeters, gaussFwhm, out int r);
            return MeasureKernelFwhmArcsec(k, r, plateScale);
        }

        /// <summary>
        /// A circular Gaussian of the given FWHM, integrated over each pixel rather than sampled
        /// at its centre.
        ///
        /// Integration matters here in a way it does not for the wide profiles: the Gaussians
        /// this carries can be NARROWER than one pixel (HST's pointing jitter is 0.008 arcsec
        /// against a 0.04 arcsec pixel), and a sub-pixel profile sampled at pixel centres is
        /// simply wrong, in the specific way of putting all the flux in one pixel and none in
        /// its neighbours. A 2D Gaussian is separable and each factor integrates to a difference
        /// of error functions, so the exact pixel integral costs no more than the sample would.
        /// </summary>
        private static double[] SampleGaussian(int radius, double plateScaleArcsecPerPixel, double fwhmArcsec)
        {
            int size = 2 * radius + 1;
            var k = new double[size * size];

            double sigmaPx = fwhmArcsec / (2.0 * Math.Sqrt(2.0 * Math.Log(2.0))) / plateScaleArcsecPerPixel;
            if (!(sigmaPx > 0.0))
            {
                k[radius * size + radius] = 1.0;
                return k;
            }

            // Separable: build the 1D integrated profile once and take outer products.
            var line = new double[size];
            double norm = 1.0 / (sigmaPx * Math.Sqrt(2.0));
            for (int i = -radius; i <= radius; i++)
            {
                double hi = Erf((i + 0.5) * norm);
                double lo = Erf((i - 0.5) * norm);
                line[i + radius] = 0.5 * (hi - lo);
            }

            for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                    k[dy * size + dx] = line[dy] * line[dx];

            return k;
        }

        /// <summary>
        /// The error function, to about 1.2e-7 absolute: Abramowitz &amp; Stegun (1964), "Handbook
        /// of Mathematical Functions", Eq. 7.1.26. The .NET Framework this mod targets has no
        /// Math.Erf, and the pixel integral above needs one; A&amp;S 7.1.26 is the standard rational
        /// approximation for exactly this situation and its stated error bound is four orders
        /// below the kernel's own truncation.
        /// </summary>
        private static double Erf(double x)
        {
            const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741;
            const double a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;

            int sign = x < 0.0 ? -1 : 1;
            double ax = Math.Abs(x);
            double t = 1.0 / (1.0 + p * ax);
            double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-ax * ax);
            return sign * y;
        }

        /// <summary>Uniformly illuminated defocus blur disc, antialiased at its rim by the fraction of each pixel that falls inside.</summary>
        private static double[] SampleDisc(int radius, double discRadiusPx)
        {
            int size = 2 * radius + 1;
            var k = new double[size * size];
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    double r = Math.Sqrt((double)dx * dx + (double)dy * dy);
                    double coverage = discRadiusPx + 0.5 - r; // linear ramp across the boundary pixel
                    k[(dy + radius) * size + (dx + radius)] = Math.Max(0.0, Math.Min(1.0, coverage));
                }
            }
            return k;
        }

        /// <summary>
        /// Samples a pupil's full two-dimensional pattern onto the kernel grid, pixel-averaged the
        /// way PupilDiffraction defines it. No radial lookup table is possible here: with a spider
        /// the pattern depends on azimuth as well as radius, which is the entire point.
        /// </summary>
        private static double[] SampleTwoDimensional(int radius, double plateScaleArcsecPerPixel, PupilDiffraction pupil)
        {
            int size = 2 * radius + 1;
            var k = new double[size * size];
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    k[(dy + radius) * size + (dx + radius)] = pupil.PixelAveragedIntensityArcsec(
                        dx * plateScaleArcsecPerPixel, dy * plateScaleArcsecPerPixel, plateScaleArcsecPerPixel);
                }
            }
            return k;
        }

        private static int RadiusFor(double fwhmArcsec, double plateScaleArcsecPerPixel)
        {
            int r = (int)Math.Ceiling(KernelRadiusInFwhm * fwhmArcsec / plateScaleArcsecPerPixel);
            return Math.Max(1, Math.Min(MaxKernelRadiusPx, r));
        }

        /// <summary>Radial lookup samples per pixel. At 4/px the spacing is a quarter pixel, far finer than any structure these smooth profiles contain.</summary>
        private const int RadialLutSamplesPerPixel = 4;

        /// <summary>
        /// Samples a radially symmetric profile onto a square kernel grid.
        ///
        /// The profile is evaluated on a fine 1D radial lookup table and interpolated onto the
        /// grid, rather than evaluated once per pixel. This is not a shortcut for its own sake:
        /// the atmospheric profile costs a 512-step quadrature with a Bessel evaluation per step,
        /// so a halo kernel of radius 256 would otherwise mean 263,169 quadratures, of order
        /// 10^8 special-function evaluations for a single capture. Both profiles here depend on
        /// radius alone and are smooth on the scale of a quarter pixel, so tabulating and
        /// interpolating is ~180x cheaper for a difference far below the kernel's own truncation.
        /// </summary>
        private static double[] SampleRadial(int radius, double plateScaleArcsecPerPixel, Func<double, double> intensityAtThetaRad)
        {
            int size = 2 * radius + 1;
            var k = new double[size * size];

            double maxRadiusPx = radius * Math.Sqrt(2.0);
            int lutCount = (int)Math.Ceiling(maxRadiusPx * RadialLutSamplesPerPixel) + 2;
            var lut = new double[lutCount];
            for (int i = 0; i < lutCount; i++)
            {
                double rPx = (double)i / RadialLutSamplesPerPixel;
                lut[i] = intensityAtThetaRad(rPx * plateScaleArcsecPerPixel * ArcsecToRad);
            }

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    double rPx = Math.Sqrt((double)dx * dx + (double)dy * dy);
                    double pos = rPx * RadialLutSamplesPerPixel;
                    int i0 = (int)pos;
                    if (i0 >= lutCount - 1) { k[(dy + radius) * size + (dx + radius)] = lut[lutCount - 1]; continue; }
                    double frac = pos - i0;
                    k[(dy + radius) * size + (dx + radius)] = lut[i0] * (1.0 - frac) + lut[i0 + 1] * frac;
                }
            }
            return k;
        }

        /// <summary>Discrete convolution of two square kernels, evaluated only over the output radius that will actually be kept.</summary>
        private static double[] Convolve(double[] a, int ra, double[] b, int rb, int rOut)
        {
            int sizeA = 2 * ra + 1, sizeB = 2 * rb + 1, sizeOut = 2 * rOut + 1;
            var outK = new double[sizeOut * sizeOut];

            for (int ay = -ra; ay <= ra; ay++)
            {
                for (int ax = -ra; ax <= ra; ax++)
                {
                    double av = a[(ay + ra) * sizeA + (ax + ra)];
                    if (av <= 0.0) continue;

                    for (int by = -rb; by <= rb; by++)
                    {
                        int oy = ay + by;
                        if (oy < -rOut || oy > rOut) continue;
                        int rowOut = (oy + rOut) * sizeOut;
                        int rowB = (by + rb) * sizeB;

                        for (int bx = -rb; bx <= rb; bx++)
                        {
                            int ox = ax + bx;
                            if (ox < -rOut || ox > rOut) continue;
                            outK[rowOut + ox + rOut] += av * b[rowB + bx + rb];
                        }
                    }
                }
            }
            return outK;
        }

        /// <summary>
        /// Clips a kernel to a CIRCULAR support, when that costs nothing real, and scales it to
        /// unit sum.
        ///
        /// Circular because the array is square and a real PSF is not. Sampled into its corners, a
        /// square kernel of half-width R carries the profile out to R at the mid-edges and to
        /// R*sqrt(2) at the corners, so where it ends depends on azimuth, and where a kernel ends
        /// is where the surface brightness steps to zero. That step is what draws a square around a
        /// bright star. Clipping to the inscribed circle makes the step isotropic, which is the
        /// shape the physics has.
        ///
        /// BUT ONLY WHEN THE RING IT DISCARDS IS EMPTY. On a wide seeing kernel the annulus between
        /// the inscribed circle and the corners holds ~1e-4 of the energy and the clip is free. On a
        /// compact kernel (the RedCat's whole PSF spans two pixels), that annulus holds several
        /// percent of REAL profile, and clipping it re-concentrated the energy enough to shift the
        /// encircled-energy curve 17% against GalSim. So the decision is measured, not assumed: the
        /// clip applies only when the annulus carries less than CircularClipBudget of the total,
        /// which reproduces the isotropic edge exactly where it was needed and the full square
        /// exactly where GalSim-validated physics lives.
        ///
        /// Unit sum either way, so convolution conserves total flux despite the finite support.
        /// </summary>
        private static float[] Normalise(double[] kernel, int radius)
        {
            int size = 2 * radius + 1;
            double limit = (double)radius * radius;
            double inside = 0.0, annulus = 0.0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int row = (dy + radius) * size;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    double v = kernel[row + dx + radius];
                    if ((double)dx * dx + (double)dy * dy > limit) annulus += v;
                    else inside += v;
                }
            }
            double total = inside + annulus;
            var result = new float[size * size];
            if (total <= 0.0) { result[radius * size + radius] = 1f; return result; }

            bool clip = annulus <= CircularClipBudget * total;
            double sum = clip ? inside : total;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int row = (dy + radius) * size;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int i = row + dx + radius;
                    bool outside = clip && (double)dx * dx + (double)dy * dy > limit;
                    result[i] = outside ? 0f : (float)(kernel[i] / sum);
                }
            }
            return result;
        }

        /// <summary>
        /// Largest fraction of a kernel's energy the circular clip may discard. 1e-3 sits an order
        /// of magnitude above the 1e-4 the wide seeing kernels actually carry in their corners;
        /// so they keep their isotropic edge, and two orders below the several percent a compact
        /// kernel carries there, so nothing measurable is ever thrown away.
        /// </summary>
        private const double CircularClipBudget = 1e-3;
    }
}
