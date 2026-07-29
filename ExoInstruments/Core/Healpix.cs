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
