using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// HEALPix pixel indexing, RING and NESTED, from Gorski et al. (2005, ApJ 622, 759).
    ///
    /// Every all-sky map worth reading is distributed on this grid: the Planck products, the SFD
    /// and Planck dust maps, the Finkbeiner H-alpha composite, the Green and Edenhofer 3D
    /// extinction cubes. Reading any of them means computing the pixel a direction falls in.
    ///
    /// The scheme divides the sphere into 12 equal-area base pixels and subdivides each into
    /// nside^2, giving 12 nside^2 pixels of exactly equal solid angle. The equal-area property is
    /// what makes it the right grid for a surface-brightness or column-density map, and it is why
    /// the projection is piecewise: an equatorial band where pixel rings are equally spaced in
    /// cos(theta), and two polar caps where they are not.
    ///
    /// Only the direction-to-pixel half is implemented, which is the half a map READER needs.
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class Healpix
    {
        /// <summary>Largest nside supported. 2^13 is 8192, i.e. 0.43 arcmin pixels, finer than any published dust or H-alpha map.</summary>
        public const int MaxNside = 1 << 13;

        public static bool IsValidNside(int nside)
            => nside > 0 && nside <= MaxNside && (nside & (nside - 1)) == 0;

        public static long PixelCount(int nside) => 12L * nside * nside;

        /// <summary>Solid angle of one pixel, steradians: 4 pi / (12 nside^2), exactly equal for every pixel.</summary>
        public static double PixelSolidAngle(int nside) => 4.0 * Math.PI / PixelCount(nside);

        /// <summary>Mean pixel spacing, degrees. sqrt of the solid angle, which is how map resolutions are quoted.</summary>
        public static double PixelResolutionDeg(int nside)
            => Math.Sqrt(PixelSolidAngle(nside)) * 180.0 / Math.PI;

        /// <summary>
        /// RING-scheme pixel containing the direction (theta, phi), with theta the colatitude in
        /// radians from the north pole and phi the longitude, both in the map's own frame.
        /// </summary>
        public static long AngleToRing(int nside, double theta, double phi)
        {
            CheckNside(nside);
            double z = Math.Cos(Clamp(theta, 0.0, Math.PI));
            return ZPhiToRing(nside, z, phi);
        }

        /// <summary>
        /// NESTED-scheme pixel for the same direction, which is what most FITS map files use.
        ///
        /// Computed directly from (z, phi) rather than by converting a RING index: nested numbering
        /// is a Morton code of the pixel's position WITHIN its base face, so the face and the two
        /// in-face coordinates fall straight out of the same projection RING uses, while going
        /// through RING means inverting that numbering first.
        /// </summary>
        public static long AngleToNested(int nside, double theta, double phi)
        {
            CheckNside(nside);
            double z = Math.Cos(Clamp(theta, 0.0, Math.PI));
            double za = Math.Abs(z);
            double tt = Mod(phi * (2.0 / Math.PI), 4.0);

            long face, ix, iy;
            if (za <= 2.0 / 3.0)
            {
                double temp1 = nside * (0.5 + tt);
                double temp2 = nside * z * 0.75;
                long jp = (long)(temp1 - temp2);
                long jm = (long)(temp1 + temp2);

                long ifp = jp / nside;    // 0..4, which base face the ascending edge is in
                long ifm = jm / nside;
                face = ifp == ifm ? ((ifp & 3) + 4)
                     : ifp < ifm ? (ifp & 3)
                     : ((ifm & 3) + 8);

                ix = jm & (nside - 1);
                iy = nside - (jp & (nside - 1)) - 1;
            }
            else
            {
                long ntt = Math.Min(3L, (long)tt);
                double tp = tt - ntt;
                double tmp = nside * Math.Sqrt(3.0 * (1.0 - za));

                long jp = Math.Min((long)(tp * tmp), nside - 1);
                long jm = Math.Min((long)((1.0 - tp) * tmp), nside - 1);

                if (z >= 0.0) { face = ntt; ix = nside - jm - 1; iy = nside - jp - 1; }
                else { face = ntt + 8; ix = jp; iy = jm; }
            }

            return face * (long)nside * nside + Interleave(ix, iy);
        }

        /// <summary>
        /// Equatorial coordinates to a pixel, in the map's own spherical frame: theta = 90 - dec,
        /// phi = ra. A map tabulated in Galactic coordinates needs the direction converted first
        /// (see GalacticCoordinates).
        /// </summary>
        public static long SphericalDegreesToRing(int nside, double longitudeDeg, double latitudeDeg)
            => AngleToRing(nside, (90.0 - latitudeDeg) * Math.PI / 180.0, longitudeDeg * Math.PI / 180.0);

        public static long SphericalDegreesToNested(int nside, double longitudeDeg, double latitudeDeg)
            => AngleToNested(nside, (90.0 - latitudeDeg) * Math.PI / 180.0, longitudeDeg * Math.PI / 180.0);

        // ------------------------------------------------------------------ Interpolation

        /// <summary>
        /// The four pixels surrounding a direction and their bilinear weights, in RING numbering.
        /// Weights sum to 1.
        ///
        /// WHY A MAP MUST BE READ THIS WAY AND NOT PIXEL BY PIXEL. Every all-sky map this project
        /// reads has been smoothed to a beam: the Finkbeiner H-alpha composite to 6 arcmin, SFD98
        /// to 6.1. The field it tabulates therefore has no structure below that scale; it is
        /// band-limited, and the pixel values are samples of a function already known to be smooth
        /// between them. Returning the containing pixel's value instead makes the map piecewise
        /// constant, which introduces discontinuities at cell edges that the data does not have.
        /// On a wide-field frame those edges are visible directly: a 3.4 arcmin cell is 54 pixels
        /// across on the RedCat, so a nebula renders as a mosaic of flat blocks rather than the
        /// smooth glow the survey actually measured. Interpolating is the reconstruction the
        /// sampling implies, not an embellishment of it.
        ///
        /// This is the scheme's own standard interpolation (Gorski et al. 2005; the
        /// get_interpol/get_interp_val of the reference HEALPix library and of healpy): bilinear
        /// in ring index and in azimuth within each ring, with the poles handled by folding in the
        /// four pixels of the top or bottom ring. tools/dustmap-tests checks it against healpy.
        /// </summary>
        public static void InterpolationWeights(int nside, double theta, double phi, long[] pixels, double[] weights)
        {
            CheckNside(nside);
            if (pixels == null || pixels.Length < 4 || weights == null || weights.Length < 4)
                throw new ArgumentException("pixels and weights must each hold at least four entries");

            theta = Clamp(theta, 0.0, Math.PI);
            phi = Mod(phi, 2.0 * Math.PI);
            double z = Math.Cos(theta);

            long ir1 = RingAbove(nside, z);
            long ir2 = ir1 + 1;
            double theta1 = 0.0, theta2 = 0.0;

            if (ir1 > 0)
            {
                RingInfo(nside, ir1, out long start, out long ringPix, out theta1, out double dPhi, out bool shifted);
                FillRing(phi, start, ringPix, shifted, dPhi, pixels, weights, 0);
            }
            if (ir2 < 4L * nside)
            {
                RingInfo(nside, ir2, out long start, out long ringPix, out theta2, out double dPhi, out bool shifted);
                FillRing(phi, start, ringPix, shifted, dPhi, pixels, weights, 2);
            }

            if (ir1 == 0)
            {
                // Above the topmost ring: the four pixels of that ring surround the pole, so the
                // two "upper" slots become its other two pixels and the weight is shared evenly.
                double w = theta / theta2;
                weights[2] *= w; weights[3] *= w;
                double fac = (1.0 - w) * 0.25;
                pixels[0] = (pixels[2] + 2L) & 3L;
                pixels[1] = (pixels[3] + 2L) & 3L;
                weights[0] = fac; weights[1] = fac;
                weights[2] += fac; weights[3] += fac;
            }
            else if (ir2 == 4L * nside)
            {
                double w = (theta - theta1) / (Math.PI - theta1);
                weights[0] *= 1.0 - w; weights[1] *= 1.0 - w;
                double fac = w * 0.25;
                long npix = PixelCount(nside);
                pixels[2] = ((pixels[0] + 2L) & 3L) + npix - 4L;
                pixels[3] = ((pixels[1] + 2L) & 3L) + npix - 4L;
                weights[0] += fac; weights[1] += fac;
                weights[2] = fac; weights[3] = fac;
            }
            else
            {
                double w = (theta - theta1) / (theta2 - theta1);
                weights[0] *= 1.0 - w; weights[1] *= 1.0 - w;
                weights[2] *= w; weights[3] *= w;
            }
        }

        /// <summary>The same for degrees in the map's own frame, matching SphericalDegreesToRing.</summary>
        public static void InterpolationWeightsDegrees(int nside, double longitudeDeg, double latitudeDeg,
                                                       long[] pixels, double[] weights)
            => InterpolationWeights(nside, (90.0 - latitudeDeg) * Math.PI / 180.0,
                                    longitudeDeg * Math.PI / 180.0, pixels, weights);

        // ------------------------------------------------------- Cubic (C1) reconstruction

        /// <summary>Taps the cubic reconstruction uses: four rings by four pixels along each.</summary>
        public const int CubicTaps = 16;

        /// <summary>
        /// The reconstruction problem InterpolationWeights solves, solved again with a scheme whose
        /// FIRST DERIVATIVE is continuous.
        ///
        /// WHY THE BILINEAR ANSWER IS NOT GOOD ENOUGH, MEASURED. Bilinear interpolation is C0 but
        /// not C1: the gradient jumps across every cell edge, so every edge of the grid leaves a
        /// crease in the reconstructed field. On a frame those creases are not a texture, they are
        /// a RULED MESH, because the cells are a lattice; and because these maps are tabulated in
        /// Galactic coordinates while a frame is aligned on equatorial axes, the mesh arrives
        /// rotated. Toward the Horsehead the Galactic axes sit 62.4 degrees from the RedCat's, so
        /// at 3.822 arcsec per pixel the composite's 3.44 arcmin cells rule the whole frame
        /// diagonally every 36.7 by 39.6 pixels, along position angles -27.6 and +62.4, and a
        /// SHASSA patch's 0.86 arcmin cells every 9.2 by 9.9. Rendering the packed maps through
        /// this class at that frame's own WCS, with no noise, no PSF and no detector, reproduces
        /// the mesh in full; folding the Laplacian of that render on each candidate lattice's own
        /// phase puts 5.2 times the mean edge amplitude in the single phase bin holding the cell
        /// boundary, against 0.32 elsewhere. The eye finds it because lateral inhibition is a
        /// derivative detector, which is exactly the quantity bilinear interpolation gets wrong.
        ///
        /// WHY A HIGHER ORDER IS THE DATA'S OWN ANSWER AND NOT AN EMBELLISHMENT. The Finkbeiner
        /// composite is smoothed to a 6 arcmin beam and tabulated on 3.44 arcmin cells, so it is
        /// oversampled by 1.75 and the field between samples is known to be smooth. A C1
        /// reconstruction is therefore a strictly better estimator of the mapped field than one
        /// that is not: the creases are an artefact of the reader, not a feature of the survey.
        ///
        /// CATMULL-ROM rather than a B-spline, because it INTERPOLATES: a cell centre still returns
        /// that cell's own value and no resolution is lost, where a cubic B-spline would blur by
        /// about 1.2 cells and cost the SHASSA patches the very structure they exist for. Its outer
        /// taps are negative, so on a step of contrast C it undershoots by at most 6.3 percent of C;
        /// the field is positive and every caller already rejects a non-positive sample, so that
        /// bound is the whole of the price. Uniform-sample Catmull-Rom (Catmull and Rom 1974) is the
        /// cubic convolution kernel with a = -0.5, the value Keys (1981, IEEE Trans. ASSP 29, 1153)
        /// shows to be third-order accurate -- the highest any four-tap convolution reaches.
        ///
        /// GEOMETRY. Along a ring the samples are exactly uniform in azimuth, which is Catmull-Rom's
        /// own case. Across rings the parameter is the fractional RING INDEX, because that is what
        /// the samples are uniform in; in the equatorial belt it is nside*(2 - 1.5z) in closed form.
        /// The half-pixel stagger between neighbouring rings needs no special handling: each ring is
        /// interpolated in its own phase before the four are combined.
        ///
        /// Returns the number of taps written: CubicTaps normally, or 4 within two rings of either
        /// pole, where the stencil runs off the grid and the bilinear scheme -- which folds across
        /// the pole -- answers instead. A caller reads pixels[0..n) and weights[0..n) either way,
        /// and the weights sum to one in both cases.
        /// </summary>
        public static int CubicInterpolationWeights(int nside, double theta, double phi,
                                                    long[] pixels, double[] weights)
        {
            CheckNside(nside);
            if (pixels == null || pixels.Length < CubicTaps || weights == null || weights.Length < CubicTaps)
                throw new ArgumentException("pixels and weights must each hold at least sixteen entries");

            theta = Clamp(theta, 0.0, Math.PI);
            phi = Mod(phi, 2.0 * Math.PI);
            double z = Math.Cos(theta);

            long ir1 = RingAbove(nside, z);
            long ir2 = ir1 + 1L;

            // The stencil needs one ring beyond the bracketing pair on each side. Rings 1 and
            // 4*nside-1 hold four pixels around a pole; there is no cubic to compute there.
            if (ir1 < 2L || ir2 > 4L * nside - 2L)
            {
                InterpolationWeights(nside, theta, phi, pixels, weights);
                return 4;
            }

            RingInfo(nside, ir1 - 1L, out long start0, out long ringPix0, out _, out double dPhi0, out bool shifted0);
            RingInfo(nside, ir1, out long start1, out long ringPix1, out double theta1, out double dPhi1, out bool shifted1);
            RingInfo(nside, ir2, out long start2, out long ringPix2, out double theta2, out double dPhi2, out bool shifted2);
            RingInfo(nside, ir2 + 1L, out long start3, out long ringPix3, out _, out double dPhi3, out bool shifted3);

            // Fractional position between the two bracketing rings. In the belt the ring index is
            // nside*(2 - 1.5z) exactly, and that is the coordinate the rings are equally spaced in.
            // In the caps their spacing changes from ring to ring, so the colatitude ratio stands in;
            // that leaves the scheme interpolating and C1 and only makes its slope estimate slightly
            // uneven, over cells which are in any case the same solid angle as every other.
            double t = (ir1 - 1L >= nside && ir2 + 1L <= 3L * nside)
                     ? nside * (2.0 - 1.5 * z) - ir1
                     : (theta - theta1) / (theta2 - theta1);
            t = Clamp(t, 0.0, 1.0);

            CatmullRom(t, out double rowMinus, out double row0, out double row1, out double rowPlus);

            FillRingCubic(phi, start0, ringPix0, shifted0, dPhi0, pixels, weights, 0, rowMinus);
            FillRingCubic(phi, start1, ringPix1, shifted1, dPhi1, pixels, weights, 4, row0);
            FillRingCubic(phi, start2, ringPix2, shifted2, dPhi2, pixels, weights, 8, row1);
            FillRingCubic(phi, start3, ringPix3, shifted3, dPhi3, pixels, weights, 12, rowPlus);
            return CubicTaps;
        }

        /// <summary>The same for degrees in the map's own frame, matching SphericalDegreesToRing.</summary>
        public static int CubicInterpolationWeightsDegrees(int nside, double longitudeDeg, double latitudeDeg,
                                                           long[] pixels, double[] weights)
            => CubicInterpolationWeights(nside, (90.0 - latitudeDeg) * Math.PI / 180.0,
                                         longitudeDeg * Math.PI / 180.0, pixels, weights);

        /// <summary>
        /// The four pixels of one ring bracketing an azimuth, with that ring's Catmull-Rom weights
        /// already scaled by the ring's own weight, so the sixteen taps a caller reads are the
        /// finished tensor product.
        /// </summary>
        private static void FillRingCubic(double phi, long start, long ringPix, bool shifted, double dPhi,
                                          long[] pixels, double[] weights, int slot, double ringWeight)
        {
            double offset = shifted ? 0.5 : 0.0;
            double t = phi / dPhi - offset;
            long i1 = t < 0.0 ? (long)t - 1L : (long)t;
            double w = (phi - (i1 + offset) * dPhi) / dPhi;

            CatmullRom(w, out double c0, out double c1, out double c2, out double c3);

            pixels[slot] = start + WrapOnce(i1 - 1L, ringPix);
            pixels[slot + 1] = start + WrapOnce(i1, ringPix);
            pixels[slot + 2] = start + WrapOnce(i1 + 1L, ringPix);
            pixels[slot + 3] = start + WrapOnce(i1 + 2L, ringPix);
            weights[slot] = ringWeight * c0;
            weights[slot + 1] = ringWeight * c1;
            weights[slot + 2] = ringWeight * c2;
            weights[slot + 3] = ringWeight * c3;
        }

        /// <summary>
        /// The same answer Mod(v, m) gives, for a v that is already within one period of the range.
        ///
        /// EXACT, not approximate, and the range is guaranteed by the caller rather than assumed:
        /// phi lies in [0, 2*pi) so phi/dPhi lies in [0, ringPix), which puts the stencil's four
        /// consecutive indices between -2 and ringPix+1. Every ring HEALPix defines holds at least
        /// four pixels, so a single add or subtract brings any of them into [0, ringPix), which is
        /// what the remainder would have returned.
        ///
        /// Worth writing out because it replaces a 64-bit integer division, the one operation on
        /// this path that the processor cannot pipeline: sixteen of them per sample, and a capture
        /// takes one sample per native pixel of the sensor.
        /// </summary>
        private static long WrapOnce(long v, long m)
        {
            if (v < 0L) return v + m;
            if (v >= m) return v - m;
            return v;
        }

        /// <summary>
        /// Catmull-Rom basis at a fractional position between the middle two of four uniform
        /// samples. Sums to one at every t, returns (0,1,0,0) at t=0 and (0,0,1,0) at t=1, which is
        /// what makes the scheme interpolating rather than approximating.
        /// </summary>
        private static void CatmullRom(double t, out double w0, out double w1, out double w2, out double w3)
        {
            double t2 = t * t, t3 = t2 * t;
            w0 = 0.5 * (-t3 + 2.0 * t2 - t);
            w1 = 0.5 * (3.0 * t3 - 5.0 * t2 + 2.0);
            w2 = 0.5 * (-3.0 * t3 + 4.0 * t2 + t);
            w3 = 0.5 * (t3 - t2);
        }

        /// <summary>Converts one RING index on a known ring into NESTED, by way of the pixel centre, which lies strictly inside the pixel, so the containing-pixel lookup returns the pixel itself.</summary>
        public static long RingToNested(int nside, long ringPixel)
        {
            CheckNside(nside);
            RingCentre(nside, ringPixel, out double theta, out double phi);
            return AngleToNested(nside, theta, phi);
        }

        private static void FillRing(double phi, long start, long ringPix, bool shifted, double dPhi,
                                     long[] pixels, double[] weights, int slot)
        {
            double t = phi / dPhi - (shifted ? 0.5 : 0.0);
            long i1 = t < 0.0 ? (long)t - 1L : (long)t;
            double w = (phi - (i1 + (shifted ? 0.5 : 0.0)) * dPhi) / dPhi;
            long i2 = i1 + 1L;
            if (i1 < 0L) i1 += ringPix;
            if (i2 >= ringPix) i2 -= ringPix;
            pixels[slot] = start + i1;
            pixels[slot + 1] = start + i2;
            weights[slot] = 1.0 - w;
            weights[slot + 1] = w;
        }

        /// <summary>Index of the ring immediately above (smaller theta than) the given z, 0 meaning "above the topmost ring".</summary>
        private static long RingAbove(int nside, double z)
        {
            double az = Math.Abs(z);
            if (az <= 2.0 / 3.0) return (long)(nside * (2.0 - 1.5 * z));
            long ring = (long)(nside * Math.Sqrt(3.0 * (1.0 - az)));
            return z > 0.0 ? ring : 4L * nside - ring - 1L;
        }

        /// <summary>
        /// Ring geometry a stencil has already asked for, remembered so it is computed once.
        ///
        /// WHY THIS IS NOT A SHORTCUT. RingInfo is a pure function of (nside, ring): given the
        /// two, its four outputs are determined, so returning a remembered set is returning the
        /// same numbers and not an estimate of them. What makes it worth remembering is the
        /// ratio between how many times it is asked and how many distinct answers exist. It
        /// costs an inverse trigonometric call, a stencil needs four per sample, and a frame is
        /// FAR smaller than a map cell: the RC20 at 4x4 with its Barlow spans 4.75 arcmin, where
        /// the Finkbeiner composite's rings lie 2.6 arcmin apart, so its 11.7 million samples
        /// (one per native pixel, see the emission fill) fall across three rings and ask for
        /// them 47 million times. Measured on that frame, the four calls per sample were 43
        /// percent of the whole lookup.
        ///
        /// DIRECT-MAPPED ON THE RING INDEX, sixteen slots, because the rings a frame touches are
        /// consecutive: any field narrower than sixteen rings (every instrument in the roster on
        /// every map this project ships) never evicts anything. A wider one still gets the right
        /// answer, just more misses.
        ///
        /// THREAD-STATIC, so a parallel frame fill gives each worker its own and no lock is
        /// needed. It holds no state a caller can observe: nside is checked on every use and the
        /// table cleared if it changed, so two maps of different resolutions cannot mix.
        /// </summary>
        private sealed class RingCache
        {
            private const int Slots = 16;
            private const int Mask = Slots - 1;

            private int nside = -1;
            private readonly long[] ring = new long[Slots];
            private readonly long[] startPix = new long[Slots];
            private readonly long[] ringPix = new long[Slots];
            private readonly double[] theta = new double[Slots];
            private readonly double[] deltaPhi = new double[Slots];
            private readonly bool[] shifted = new bool[Slots];

            internal RingCache() { Clear(); }

            private void Clear()
            {
                // Rings are numbered from 1, so -1 is a value no query can carry.
                for (int i = 0; i < Slots; i++) ring[i] = -1L;
            }

            internal void Get(int n, long r, out long start, out long pix, out double th,
                              out double dPhi, out bool shift)
            {
                if (nside != n) { nside = n; Clear(); }

                int slot = (int)(r & Mask);
                if (ring[slot] != r)
                {
                    RingInfoUncached(n, r, out startPix[slot], out ringPix[slot],
                                     out theta[slot], out shifted[slot]);
                    deltaPhi[slot] = 2.0 * Math.PI / ringPix[slot];
                    ring[slot] = r;
                }

                start = startPix[slot];
                pix = ringPix[slot];
                th = theta[slot];
                dPhi = deltaPhi[slot];
                shift = shifted[slot];
            }
        }

        [ThreadStatic] private static RingCache ringCache;

        /// <summary>
        /// First pixel, pixel count, colatitude, azimuthal pixel width and half-pixel offset of a
        /// ring, numbered 1 at the north pole to 4*nside-1 at the south. Answered from the
        /// per-thread memo above; see RingCache for why that is exact.
        /// </summary>
        private static void RingInfo(int nside, long ring, out long startPix, out long ringPix,
                                     out double theta, out double deltaPhi, out bool shifted)
        {
            RingCache cache = ringCache;
            if (cache == null) ringCache = cache = new RingCache();
            cache.Get(nside, ring, out startPix, out ringPix, out theta, out deltaPhi, out shifted);
        }

        /// <summary>The geometry itself, computed rather than remembered.</summary>
        private static void RingInfoUncached(int nside, long ring, out long startPix, out long ringPix,
                                             out double theta, out bool shifted)
        {
            long npix = PixelCount(nside);
            double fact2 = 4.0 / npix;
            long north = ring > 2L * nside ? 4L * nside - ring : ring;

            if (north < nside)
            {
                double tmp = north * north * fact2;
                theta = Math.Atan2(Math.Sqrt(tmp * (2.0 - tmp)), 1.0 - tmp);
                ringPix = 4L * north;
                shifted = true;
                startPix = 2L * north * (north - 1L);
            }
            else
            {
                theta = Math.Acos(Clamp((2L * nside - north) * (2.0 * nside * fact2), -1.0, 1.0));
                ringPix = 4L * nside;
                shifted = ((north - nside) & 1L) == 0L;
                startPix = 2L * nside * (nside - 1L) + (north - nside) * ringPix;
            }

            if (north != ring)
            {
                theta = Math.PI - theta;
                startPix = npix - startPix - ringPix;
            }
        }

        /// <summary>Centre of a RING pixel, in the map's own frame, degrees. The inverse of SphericalDegreesToRing, for a caller that has a pixel and needs the direction it stands for.</summary>
        public static void RingPixelCentreDegrees(int nside, long pixel, out double longitudeDeg, out double latitudeDeg)
        {
            CheckNside(nside);
            RingCentre(nside, pixel, out double theta, out double phi);
            longitudeDeg = phi * 180.0 / Math.PI;
            latitudeDeg = 90.0 - theta * 180.0 / Math.PI;
        }

        /// <summary>Centre of a RING pixel, in the map's own frame.</summary>
        private static void RingCentre(int nside, long pixel, out double theta, out double phi)
        {
            long npix = PixelCount(nside);
            long ncap = 2L * nside * (nside - 1L);
            long ring, indexInRing;

            if (pixel < ncap)
            {
                ring = (long)(0.5 * (1.0 + Math.Sqrt(1.0 + 2.0 * pixel)));
                indexInRing = pixel - 2L * ring * (ring - 1L);
            }
            else if (pixel < npix - ncap)
            {
                long ip = pixel - ncap;
                ring = ip / (4L * nside) + nside;
                indexInRing = ip % (4L * nside);
            }
            else
            {
                long ip = npix - pixel - 1L;
                long southRing = (long)(0.5 * (1.0 + Math.Sqrt(1.0 + 2.0 * ip)));
                ring = 4L * nside - southRing;
                indexInRing = 4L * southRing - 1L - (ip - 2L * southRing * (southRing - 1L));
            }

            RingInfo(nside, ring, out long start, out long ringPix, out theta, out double dPhi, out bool shifted);
            phi = (indexInRing + (shifted ? 0.5 : 0.0)) * dPhi;
        }

        // ------------------------------------------------------------------ RING

        private static long ZPhiToRing(int nside, double z, double phi)
        {
            double za = Math.Abs(z);
            double tt = Mod(phi * (2.0 / Math.PI), 4.0);   // phi in units of pi/2, wrapped to [0,4)

            if (za <= 2.0 / 3.0)
            {
                // Equatorial band: rings are equally spaced in z, and each holds 4*nside pixels.
                double temp1 = nside * (0.5 + tt);
                double temp2 = nside * z * 0.75;
                long jp = (long)(temp1 - temp2);   // ascending edge index
                long jm = (long)(temp1 + temp2);   // descending edge index

                long ir = nside + 1 + jp - jm;                       // ring number, 1 at the top of the band
                long kshift = 1 - (ir & 1);                          // alternate rings are offset by half a pixel
                long ip = (jp + jm - nside + kshift + 1) / 2;
                ip = Mod(ip, 4L * nside);

                return 2L * nside * (nside - 1) + (ir - 1) * 4L * nside + ip;
            }

            // Polar caps: ring k holds 4k pixels, so the ring index comes from a square root.
            double tp = tt - (long)tt;
            double tmp = nside * Math.Sqrt(3.0 * (1.0 - za));

            long jpCap = (long)(tp * tmp);
            long jmCap = (long)((1.0 - tp) * tmp);

            long irCap = jpCap + jmCap + 1;                          // 1 at the pole
            long ipCap = (long)(tt * irCap);
            ipCap = Mod(ipCap, 4L * irCap);

            return z > 0.0
                ? 2L * irCap * (irCap - 1) + ipCap
                : PixelCount(nside) - 2L * irCap * (irCap + 1) + ipCap;
        }

        /// <summary>Bit-interleaves x and y into the Morton (Z-order) index the nested scheme uses within a face.</summary>
        private static long Interleave(long x, long y)
        {
            return Spread(x) | (Spread(y) << 1);
        }

        private static long Spread(long v)
        {
            long x = v & 0x00000000ffffffffL;
            x = (x ^ (x << 16)) & 0x0000ffff0000ffffL;
            x = (x ^ (x << 8)) & 0x00ff00ff00ff00ffL;
            x = (x ^ (x << 4)) & 0x0f0f0f0f0f0f0f0fL;
            x = (x ^ (x << 2)) & 0x3333333333333333L;
            x = (x ^ (x << 1)) & 0x5555555555555555L;
            return x;
        }

        private static void CheckNside(int nside)
        {
            if (!IsValidNside(nside))
                throw new ArgumentOutOfRangeException(nameof(nside), "nside must be a power of two in 1.." + MaxNside);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private static double Mod(double v, double m)
        {
            double r = v % m;
            return r < 0.0 ? r + m : r;
        }

        private static long Mod(long v, long m)
        {
            long r = v % m;
            return r < 0 ? r + m : r;
        }
    }
}
