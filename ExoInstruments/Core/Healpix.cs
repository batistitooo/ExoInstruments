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
                RingInfo(nside, ir1, out long start, out long ringPix, out theta1, out bool shifted);
                FillRing(phi, start, ringPix, shifted, pixels, weights, 0);
            }
            if (ir2 < 4L * nside)
            {
                RingInfo(nside, ir2, out long start, out long ringPix, out theta2, out bool shifted);
                FillRing(phi, start, ringPix, shifted, pixels, weights, 2);
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

        /// <summary>Converts one RING index on a known ring into NESTED, by way of the pixel centre, which lies strictly inside the pixel, so the containing-pixel lookup returns the pixel itself.</summary>
        public static long RingToNested(int nside, long ringPixel)
        {
            CheckNside(nside);
            RingCentre(nside, ringPixel, out double theta, out double phi);
            return AngleToNested(nside, theta, phi);
        }

        private static void FillRing(double phi, long start, long ringPix, bool shifted,
                                     long[] pixels, double[] weights, int slot)
        {
            double dPhi = 2.0 * Math.PI / ringPix;
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

        /// <summary>First pixel, pixel count, colatitude and half-pixel offset of a ring, numbered 1 at the north pole to 4*nside-1 at the south.</summary>
        private static void RingInfo(int nside, long ring, out long startPix, out long ringPix,
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

            RingInfo(nside, ring, out long start, out long ringPix, out theta, out bool shifted);
            phi = (indexInRing + (shifted ? 0.5 : 0.0)) * (2.0 * Math.PI / ringPix);
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
