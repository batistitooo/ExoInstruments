using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The Fraunhofer diffraction pattern of a REAL telescope pupil: an annular aperture crossed by
    /// straight secondary-support vanes. Rings and diffraction spikes come out of one calculation,
    /// because on a real telescope they come from one pupil.
    ///
    /// WHY THIS EXISTS. OpticalPsf and RadialPsfProfile handle the radially symmetric part exactly,
    /// but a spider is not radially symmetric, so no radial profile can carry it. The imaging
    /// display therefore drew its six spikes with an invented amplitude (4e-4 of peak at 1 lambda/D)
    /// on an invented azimuthal Gaussian (sigma = 1.3 degrees) and an invented 1/r^2 falloff. Three
    /// free parameters standing in for something the pupil determines outright.
    ///
    /// THE CALCULATION, and why it needs no free parameter. The far-field amplitude is the Fourier
    /// transform of the pupil's transmission. Transmission is a sum of simple shapes, and the
    /// transform is linear, so the amplitude is a sum of their transforms in closed form:
    ///
    ///   * A disc of radius a transforms to  pi*a^2 * 2*J1(2*pi*a*u) / (2*pi*a*u).
    ///     The annulus is the outer disc minus the obstruction disc.
    ///   * A rectangle transforms to a product of two sinc functions. Each vane is a rectangle of
    ///     width w spanning the open annulus radially, so it carries a phase from sitting off
    ///     centre; vanes on opposite sides of the pupil carry conjugate phases, and their sum is
    ///     real. That is why the six-vane sum below reduces to three cosine terms.
    ///
    ///       A_vane_pair = 2*w*L * sinc(pi*w*u_perp) * sinc(pi*L*u_par) * cos(2*pi*d*u_par)
    ///
    ///     with L the vane's radial length, d the radius of its midpoint, and u resolved along and
    ///     across the vane.
    ///
    /// Intensity is |A_total|^2, normalised by the on-axis value, which is just the pupil's open
    /// AREA: pi*R^2 - pi*R_in^2 - n*w*L. Every quantity above is a length measured on the real
    /// telescope. The spikes' amplitude, their angular width and their radial falloff are now all
    /// consequences of the vane geometry, and none of them can be tuned.
    ///
    /// The vanes are modelled as spanning only the OPEN annulus, from the obstruction's edge
    /// outward, so they neither overlap each other at the centre nor double-subtract the region the
    /// secondary already blocks. A real spider does converge on the secondary, which sits inside
    /// the obstruction and is therefore already dark.
    ///
    /// REDUCIBILITY. With vaneCount = 0 this must reproduce OpticalPsf.AiryIntensity exactly, since
    /// both are then the same annular pupil by two different routes (this one via the difference of
    /// two disc transforms, that one via the published obstructed-aperture form). The headless
    /// harness checks it.
    ///
    /// Pure C# with no Unity dependency, like the rest of Core.
    /// </summary>
    public sealed class PupilDiffraction
    {
        private const double ArcsecToRad = Math.PI / (180.0 * 3600.0);

        private readonly double _outerRadius;      // R, metres
        private readonly double _innerRadius;      // eps*R, metres
        private readonly double _wavelength;       // metres
        private readonly double _vaneWidth;        // w, metres
        private readonly double _vaneLength;       // L = R - R_in, metres
        private readonly double _vaneMidRadius;    // d = (R + R_in)/2, metres
        private readonly int _vanePairs;           // vaneCount / 2
        private readonly double[] _vaneCos;        // direction cosines, one per pair
        private readonly double[] _vaneSin;
        private readonly double _onAxisAmplitude;  // the pupil's open area

        // Mirror support pads: circular obscurations at arbitrary positions in the pupil, in
        // metres. Unlike the annulus and the opposed vane pairs, these are NOT centrally
        // symmetric, so the far-field amplitude they produce is complex; see Amplitude.
        private readonly double[] _padX;
        private readonly double[] _padY;
        private readonly double[] _padRadius;
        private readonly bool _hasPads;
        private readonly bool _padRadiiEqual;

        /// <summary>Aperture diameter (m).</summary>
        public double ApertureMeters => 2.0 * _outerRadius;

        /// <summary>Wavelength (m).</summary>
        public double WavelengthMeters => _wavelength;

        /// <summary>lambda/D in radians: the pattern's natural angular unit.</summary>
        public double LambdaOverDRad => _wavelength / (2.0 * _outerRadius);

        /// <summary>
        /// Fraction of the pupil's open area the vanes remove. Small, and that is the point: the
        /// spikes are visible not because the vanes block much light but because what they block is
        /// a long thin shape, which concentrates its diffracted light into a narrow line.
        /// </summary>
        public double VaneObscurationFraction { get; }

        /// <param name="vaneCount">Number of support vanes. Must be even (they come in opposed pairs) or zero.</param>
        /// <param name="vaneWidthMeters">Vane width. Zero disables the vanes.</param>
        /// <param name="vaneRotationRad">Orientation of the first vane, so a pupil can be clocked.</param>
        public PupilDiffraction(
            double apertureMeters, double obstructionRatio, double wavelengthMeters,
            int vaneCount, double vaneWidthMeters, double vaneRotationRad)
            : this(apertureMeters, obstructionRatio, wavelengthMeters, vaneCount, vaneWidthMeters,
                   vaneRotationRad, null)
        {
        }

        /// <param name="pads">
        /// Circular obscurations at arbitrary positions in the pupil, each given in FRACTIONS OF
        /// THE PUPIL RADIUS, which is the convention every published pupil table uses (Tiny Tim's
        /// .pup files among them). Null or empty for a pupil with none.
        ///
        /// What these are, on a real telescope: the pads that hold the primary mirror in its
        /// cell. HST has three, and Krist's Tiny Tim paper names them alongside the secondary and
        /// the spider as one of the telescope's three obscurations; the three-lobed shadow they
        /// cast is visible in its spherically-aberrated Faint Object Camera images.
        ///
        /// They block very little, about 1.4 per cent of HST's open pupil between them, and that
        /// is precisely why they are worth computing rather than lumping into the obstruction
        /// ratio: like the spider vanes, what makes them visible is not how much light they stop
        /// but the SHAPE of what they stop and where it sits. Three pads at roughly 120 degrees
        /// are not centrally symmetric, which is the reason this class had to learn to carry a
        /// complex amplitude at all: unlike the annulus and the opposed vane pairs, their
        /// transform has an imaginary part that does not cancel.
        /// </param>
        public PupilDiffraction(
            double apertureMeters, double obstructionRatio, double wavelengthMeters,
            int vaneCount, double vaneWidthMeters, double vaneRotationRad,
            PupilPad[] pads)
        {
            if (apertureMeters <= 0.0 || wavelengthMeters <= 0.0)
                throw new ArgumentException("aperture and wavelength must be positive");
            if (vaneCount < 0 || (vaneCount & 1) != 0)
                throw new ArgumentException("vaneCount must be zero or even: vanes come in opposed pairs");

            _outerRadius = 0.5 * apertureMeters;
            _innerRadius = 0.5 * apertureMeters * Math.Max(0.0, Math.Min(0.95, obstructionRatio));
            _wavelength = wavelengthMeters;
            _vaneWidth = Math.Max(0.0, vaneWidthMeters);
            _vaneLength = _outerRadius - _innerRadius;
            _vaneMidRadius = 0.5 * (_outerRadius + _innerRadius);

            _vanePairs = (vaneWidthMeters > 0.0) ? vaneCount / 2 : 0;
            _vaneCos = new double[Math.Max(1, _vanePairs)];
            _vaneSin = new double[Math.Max(1, _vanePairs)];
            for (int k = 0; k < _vanePairs; k++)
            {
                // Pairs are opposed, so n pairs span 180 degrees rather than 360.
                double phi = vaneRotationRad + Math.PI * k / _vanePairs;
                _vaneCos[k] = Math.Cos(phi);
                _vaneSin[k] = Math.Sin(phi);
            }

            int padCount = 0;
            if (pads != null)
                for (int i = 0; i < pads.Length; i++)
                    if (pads[i].RadiusFraction > 0.0) padCount++;

            _hasPads = padCount > 0;
            _padX = new double[Math.Max(1, padCount)];
            _padY = new double[Math.Max(1, padCount)];
            _padRadius = new double[Math.Max(1, padCount)];
            double padArea = 0.0;
            if (_hasPads)
            {
                int j = 0;
                for (int i = 0; i < pads.Length; i++)
                {
                    if (!(pads[i].RadiusFraction > 0.0)) continue;
                    _padX[j] = pads[i].XFraction * _outerRadius;
                    _padY[j] = pads[i].YFraction * _outerRadius;
                    _padRadius[j] = pads[i].RadiusFraction * _outerRadius;
                    padArea += Math.PI * _padRadius[j] * _padRadius[j];
                    j++;
                }
            }

            _padRadiiEqual = true;
            for (int i = 1; i < _padRadius.Length; i++)
                if (_padRadius[i] != _padRadius[0]) _padRadiiEqual = false;

            // Which reflections of the sampling grid this pupil is invariant under. Read off the
            // geometry rather than assumed: a sampler that folds a grid on the strength of a
            // symmetry the pupil does not have would silently mirror the spikes into the wrong
            // quadrants, and the failure would look like structure.
            var vaneAngles = new double[_vanePairs];
            for (int k = 0; k < _vanePairs; k++) vaneAngles[k] = vaneRotationRad + Math.PI * k / _vanePairs;

            AxisMirrorSymmetric = !_hasPads && LineSetInvariant(vaneAngles, a => -a);
            DiagonalMirrorSymmetric = AxisMirrorSymmetric
                                   && LineSetInvariant(vaneAngles, a => 0.5 * Math.PI - a);

            double annulusArea = Math.PI * (_outerRadius * _outerRadius - _innerRadius * _innerRadius);
            double vaneArea = 2.0 * _vanePairs * _vaneWidth * _vaneLength;
            _onAxisAmplitude = annulusArea - vaneArea - padArea;
            VaneObscurationFraction = annulusArea > 0.0 ? vaneArea / annulusArea : 0.0;
            PadObscurationFraction = annulusArea > 0.0 ? padArea / annulusArea : 0.0;

            if (_onAxisAmplitude <= 0.0)
                throw new ArgumentException("obscurations cover the entire pupil");
        }

        /// <summary>Fraction of the pupil's open area the mirror support pads remove.</summary>
        public double PadObscurationFraction { get; }

        /// <summary>
        /// True when the far field is unchanged by reflecting the angular offset in either
        /// coordinate axis, so one QUADRANT of a sampling grid determines the whole pattern.
        ///
        /// The pattern always has central symmetry, I(-theta) = I(theta), because the pupil is
        /// real. This is the stronger statement, and it holds when the pupil ITSELF is symmetric
        /// about both axes: the annulus is symmetric about everything, so what decides it is
        /// whether the set of vane lines survives being reflected, and whether there are pads -
        /// three pads at 120 degrees do not, which is why Hubble gets only the central symmetry.
        /// </summary>
        public bool AxisMirrorSymmetric { get; }

        /// <summary>
        /// True when the far field is additionally unchanged by swapping the two axes, so one
        /// OCTANT determines the pattern. Four vanes at 0 and 90 degrees have it, which is every
        /// ground instrument in the roster; six at 60 degrees would not.
        /// </summary>
        public bool DiagonalMirrorSymmetric { get; }

        /// <summary>
        /// Normalised intensity (1.0 on axis) at an angular offset, in radians, resolved into two
        /// axes because the pattern is not radially symmetric once the pupil has vanes.
        /// </summary>
        public double Intensity(double thetaXRad, double thetaYRad)
        {
            AmplitudeComplex(thetaXRad, thetaYRad, out double re, out double im);
            double norm = 1.0 / _onAxisAmplitude;
            re *= norm; im *= norm;
            return re * re + im * im;
        }

        /// <summary>
        /// The REAL part of the far-field amplitude, in units of area.
        ///
        /// For a pupil made only of the annulus and opposed vane pairs this is the whole
        /// amplitude, because such a pupil is centrally symmetric and its transform is real;
        /// that is the case this class was originally written for and it is unchanged. Once
        /// mirror pads are present the amplitude has an imaginary part too, and Intensity uses
        /// AmplitudeComplex rather than this. Kept because the harness's reducibility check
        /// against OpticalPsf.AiryIntensity is written against it.
        /// </summary>
        public double Amplitude(double thetaXRad, double thetaYRad)
        {
            AmplitudeComplex(thetaXRad, thetaYRad, out double re, out _);
            return re;
        }

        /// <summary>
        /// The far-field amplitude, in units of area, as a complex number.
        ///
        /// Each obscuration contributes minus its own transform, and a shape displaced by d in
        /// the pupil carries the phase factor exp(-2 pi i u . d). For the annulus that
        /// displacement is zero and for a pair of opposed vanes the two phases are conjugate and
        /// sum to a cosine, which is why both stay real. A mirror pad sits at neither, so it
        /// contributes a genuine complex term.
        /// </summary>
        public void AmplitudeComplex(double thetaXRad, double thetaYRad, out double re, out double im)
        {
            // Spatial frequency, in cycles per metre of pupil.
            double ux = thetaXRad / _wavelength;
            double uy = thetaYRad / _wavelength;
            double u = Math.Sqrt(ux * ux + uy * uy);

            re = DiscTransform(_outerRadius, u) - DiscTransform(_innerRadius, u);
            im = 0.0;

            for (int k = 0; k < _vanePairs; k++)
            {
                double uPar = ux * _vaneCos[k] + uy * _vaneSin[k];
                double uPerp = -ux * _vaneSin[k] + uy * _vaneCos[k];
                re -= 2.0 * _vaneWidth * _vaneLength
                    * Sinc(Math.PI * _vaneWidth * uPerp)
                    * Sinc(Math.PI * _vaneLength * uPar)
                    * Math.Cos(2.0 * Math.PI * _vaneMidRadius * uPar);
            }

            if (!_hasPads) return;

            // The pads on a real telescope are one part repeated, so their radii are equal and
            // their disc transform is one Bessel evaluation rather than one each. HST's three
            // are; the general case still pays per pad.
            double shared = _padRadiiEqual ? DiscTransform(_padRadius[0], u) : 0.0;
            for (int k = 0; k < _padRadius.Length; k++)
            {
                double a = _padRadiiEqual ? shared : DiscTransform(_padRadius[k], u);
                if (a == 0.0) continue;
                double phase = 2.0 * Math.PI * (ux * _padX[k] + uy * _padY[k]);
                re -= a * Math.Cos(phase);
                im += a * Math.Sin(phase);
            }
        }

        /// <summary>
        /// Mean intensity over a square detector pixel of angular side pixelScaleRad centred at the
        /// given offset, by midpoint rule in both axes.
        ///
        /// Required for the same reason RadialPsfProfile averages: a detector integrates over its
        /// pixel, and at the coarse plate scales this display reaches, a pixel spans several rings.
        /// The node count is set by the finest structure the pattern actually contains, which for a
        /// vaned pupil is the ring period lambda/D and not the much broader spike envelope.
        /// </summary>
        public double PixelAveragedIntensity(double thetaXRad, double thetaYRad, double pixelScaleRad)
            => PixelAveragedIntensity(thetaXRad, thetaYRad, pixelScaleRad, NodeCount(pixelScaleRad));

        /// <summary>
        /// The same average with the node count named by the caller, for a caller that knows
        /// something about WHERE in the pattern it is sampling. NodeCount answers for the worst
        /// case, the core, where a pixel spans several rings and every one of them has to be
        /// integrated; the far wings of a broadband kernel do not need that and OpticalPsf's
        /// sampler says so explicitly rather than paying the core's price everywhere.
        /// </summary>
        public double PixelAveragedIntensity(double thetaXRad, double thetaYRad, double pixelScaleRad, int n)
        {
            if (pixelScaleRad <= 0.0) return Intensity(thetaXRad, thetaYRad);
            if (n <= 1) return Intensity(thetaXRad, thetaYRad);

            double step = pixelScaleRad / n;
            double origin = -0.5 * pixelScaleRad + 0.5 * step;
            double sum = 0.0;
            for (int iy = 0; iy < n; iy++)
            {
                double dy = origin + iy * step;
                for (int ix = 0; ix < n; ix++)
                {
                    double dx = origin + ix * step;
                    sum += Intensity(thetaXRad + dx, thetaYRad + dy);
                }
            }
            return sum / ((double)n * n);
        }

        /// <summary>Midpoint nodes per pixel axis, from how many ring periods the pixel straddles. Capped, since cost grows as its square.</summary>
        public int NodeCount(double pixelScaleRad) => NodeCount(pixelScaleRad, 4, 12);

        /// <summary>
        /// The same, with the caller naming both the density and the ceiling.
        ///
        /// A caller sampling only a HANDFUL of pixels can afford what the whole grid cannot, and
        /// the default answer above is visibly not enough for either kind of pixel at the ends of
        /// the roster: at the coarsest plate scale here (FORS2 binned 4x4) a pixel spans eighteen
        /// ring periods, so the ceiling of twelve is less than one node per period and the pixel
        /// holding the core comes out wrong by 8 per cent of the peak; at the finest (SPHERE)
        /// four nodes per period is the midpoint rule on half a period and is worth 1.6 per cent.
        /// Both are paid on the few dozen pixels that hold the light, so OpticalPsf's sampler
        /// buys them back there and nowhere else.
        /// </summary>
        public int NodeCount(double pixelScaleRad, int nodesPerRingPeriod, int cap)
        {
            int n = (int)Math.Ceiling(nodesPerRingPeriod * pixelScaleRad / LambdaOverDRad);
            return Math.Max(1, Math.Min(cap, n));
        }

        // Whether a set of vane LINES maps onto itself under an angular map. Lines, not directions, so angles
        // compare modulo pi: a vane pair lies along one line and its two ends are the same obscuration. An
        // empty set (no vanes) is invariant under everything, which is the correct answer for a bare annulus.
        private static bool LineSetInvariant(double[] angles, Func<double, double> map)
        {
            for (int i = 0; i < angles.Length; i++)
            {
                double image = Mod(map(angles[i]), Math.PI);
                bool found = false;
                for (int j = 0; j < angles.Length && !found; j++)
                {
                    double d = Math.Abs(image - Mod(angles[j], Math.PI));
                    if (d < 1e-12 || Math.Abs(d - Math.PI) < 1e-12) found = true;
                }
                if (!found) return false;
            }
            return true;
        }

        private static double Mod(double x, double m)
        {
            double r = x - m * Math.Floor(x / m);
            return r < 0.0 ? r + m : r;
        }

        // Fourier transform of a filled disc of radius a, evaluated at spatial frequency u. Equals the disc's
        // area at u = 0.
        private static double DiscTransform(double a, double u)
        {
            if (a <= 0.0) return 0.0;
            double x = 2.0 * Math.PI * a * u;
            if (x < 1e-9) return Math.PI * a * a;
            return Math.PI * a * a * (2.0 * OpticalPsf.BesselJ1(x) / x);
        }

        // sin(x)/x, with its removable singularity at zero.
        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-9) return 1.0;
            return Math.Sin(x) / x;
        }

        /// <summary>Convenience: the same pattern addressed in arcsec rather than radians.</summary>
        public double IntensityArcsec(double thetaXArcsec, double thetaYArcsec)
            => Intensity(thetaXArcsec * ArcsecToRad, thetaYArcsec * ArcsecToRad);

        /// <summary>Convenience: pixel-averaged intensity addressed in arcsec.</summary>
        public double PixelAveragedIntensityArcsec(double thetaXArcsec, double thetaYArcsec, double pixelScaleArcsec)
            => PixelAveragedIntensity(thetaXArcsec * ArcsecToRad, thetaYArcsec * ArcsecToRad, pixelScaleArcsec * ArcsecToRad);

        /// <summary>Convenience: the same, in arcsec, at a node count the caller chooses.</summary>
        public double PixelAveragedIntensityArcsec(double thetaXArcsec, double thetaYArcsec, double pixelScaleArcsec, int nodes)
            => PixelAveragedIntensity(thetaXArcsec * ArcsecToRad, thetaYArcsec * ArcsecToRad, pixelScaleArcsec * ArcsecToRad, nodes);
    }

    /// <summary>
    /// One circular obscuration in the pupil, positioned and sized in fractions of the pupil's
    /// RADIUS: the convention published pupil tables use, so a table can be transcribed without
    /// arithmetic. See PupilDiffraction's pads constructor parameter.
    /// </summary>
    public struct PupilPad
    {
        public double XFraction;
        public double YFraction;
        public double RadiusFraction;

        public PupilPad(double xFraction, double yFraction, double radiusFraction)
        {
            XFraction = xFraction;
            YFraction = yFraction;
            RadiusFraction = radiusFraction;
        }
    }
}
