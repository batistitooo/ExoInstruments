using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// A fixed rotation from the observatory's (north, east, up) basis straight to Galactic
    /// Cartesian coordinates, built once per frame.
    ///
    /// WHY IT EXISTS. Filling a frame from an all-sky map means asking, for every pixel, which
    /// Galactic direction it looks at. Done literally that is a deprojection, an altitude and
    /// azimuth, an equatorial transform and a Galactic transform per pixel -- six trigonometric
    /// calls on eleven million pixels at the largest sensor's native resolution.
    ///
    /// But the whole chain from (north, east, up) to Galactic is a rotation, and it does not change
    /// during a capture: the site basis is fixed and so is the local sidereal time the exposure is
    /// referred to. So it is built ONCE, by transforming the three basis vectors through the full
    /// chain and reading the result off as the matrix columns, after which a pixel costs one matrix
    /// multiply.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public struct HorizontalToGalactic
    {
        // Columns: the images of north, east and up.
        private double nx, ny, nz;
        private double ex, ey, ez;
        private double ux, uy, uz;

        public bool IsValid { get; private set; }

        /// <summary>
        /// Builds the rotation for one observatory and one moment. localMeridianRaDeg and
        /// observerLatitudeDeg are the same pair SkyCoordinates uses everywhere else.
        /// </summary>
        public static HorizontalToGalactic Build(double localMeridianRaDeg, double observerLatitudeDeg)
        {
            var r = new HorizontalToGalactic();
            ImageOf(1.0, 0.0, 0.0, localMeridianRaDeg, observerLatitudeDeg, out r.nx, out r.ny, out r.nz);
            ImageOf(0.0, 1.0, 0.0, localMeridianRaDeg, observerLatitudeDeg, out r.ex, out r.ey, out r.ez);
            ImageOf(0.0, 0.0, 1.0, localMeridianRaDeg, observerLatitudeDeg, out r.ux, out r.uy, out r.uz);
            r.IsValid = true;
            return r;
        }

        /// <summary>Where one basis vector lands, as a Galactic unit vector.</summary>
        private static void ImageOf(double n, double e, double u,
                                    double localMeridianRaDeg, double observerLatitudeDeg,
                                    out double gx, out double gy, out double gz)
        {
            double altDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, u))) * 180.0 / Math.PI;
            double azDeg = Math.Atan2(e, n) * 180.0 / Math.PI;

            SkyCoordinates.HorizontalToEquatorial(altDeg, azDeg, localMeridianRaDeg, observerLatitudeDeg,
                                                  out double raDeg, out double decDeg);
            GalacticCoordinates.EquatorialToGalactic(raDeg, decDeg, out double lDeg, out double bDeg);

            double l = lDeg * Math.PI / 180.0, b = bDeg * Math.PI / 180.0;
            double cosB = Math.Cos(b);
            gx = cosB * Math.Cos(l);
            gy = cosB * Math.Sin(l);
            gz = Math.Sin(b);
        }

        /// <summary>
        /// Galactic longitude and latitude of a direction given in the (north, east, up) basis.
        /// One matrix multiply and two trigonometric calls, against six for the literal chain.
        /// </summary>
        public void ToGalactic(SkyVector direction, out double lDeg, out double bDeg)
        {
            double gx = nx * direction.X + ex * direction.Y + ux * direction.Z;
            double gy = ny * direction.X + ey * direction.Y + uy * direction.Z;
            double gz = nz * direction.X + ez * direction.Y + uz * direction.Z;

            double m = Math.Sqrt(gx * gx + gy * gy + gz * gz);
            if (m < 1e-12) { lDeg = 0.0; bDeg = 0.0; return; }

            bDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, gz / m))) * 180.0 / Math.PI;
            lDeg = SexagesimalCoordinates.Normalize360(Math.Atan2(gy, gx) * 180.0 / Math.PI);
        }
    }
}
