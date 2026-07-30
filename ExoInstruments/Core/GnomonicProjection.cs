using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// A unit direction on the celestial sphere. Plain doubles, no Unity type, so the whole
    /// field-projection path can run on the background pipeline thread like the rest of Core.
    /// </summary>
    public struct SkyVector
    {
        public double X, Y, Z;

        public SkyVector(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static SkyVector Normalized(double x, double y, double z)
        {
            double m = Math.Sqrt(x * x + y * y + z * z);
            if (m < 1e-12) return new SkyVector(0, 0, 1);
            return new SkyVector(x / m, y / m, z / m);
        }

        public double Dot(SkyVector o) => X * o.X + Y * o.Y + Z * o.Z;

        /// <summary>
        /// Direction of a target at the given horizontal coordinates, in an orthonormal
        /// (north, east, up) basis. Azimuth is measured from north through east, matching
        /// SkyCoordinates' own convention.
        /// </summary>
        public static SkyVector FromHorizontal(double altitudeDeg, double azimuthDeg)
        {
            double alt = altitudeDeg * Math.PI / 180.0;
            double az = azimuthDeg * Math.PI / 180.0;
            double cosAlt = Math.Cos(alt);
            return new SkyVector(cosAlt * Math.Cos(az), cosAlt * Math.Sin(az), Math.Sin(alt));
        }
    }

    /// <summary>
    /// Where a direction on the sky lands on the sensor: the gnomonic (tangent-plane)
    /// projection, which is what a telescope with a flat focal plane physically performs and
    /// what FITS calls TAN (Calabretta &amp; Greisen 2002, A&amp;A 395, 1077, "Representations of
    /// celestial coordinates in FITS", Sect. 5.1.3).
    ///
    /// Built from the camera's own three axes rather than from a catalogue frame, for one
    /// reason: the rendered planetary disk comes out of KSP's scaled-space camera, and a star
    /// drawn at a position derived from anything other than that camera's real orientation
    /// would simply not line up with it. Feeding in the actual boresight/up/right axes makes
    /// the star field and the rendered scene share one geometry by construction.
    ///
    /// The projection deliberately reproduces the same perspective transform the renderer uses,
    /// so that "one pixel" means the same thing on both paths: a direction d projects to
    /// xi = (d.right)/(d.boresight), eta = (d.up)/(d.boresight), divided by the half-field
    /// tangents. Pixel row 0 is the BOTTOM of the frame, matching Texture2D.GetPixels.
    /// </summary>
    public struct GnomonicProjection
    {
        private SkyVector right, up, boresight;
        private double tanHalfWidth, tanHalfHeight;
        private double halfWidthPx, halfHeightPx;

        /// <summary>Sensor width in pixels.</summary>
        public int WidthPx { get; private set; }
        /// <summary>Sensor height in pixels.</summary>
        public int HeightPx { get; private set; }

        /// <summary>
        /// horizontalFovDeg is the field across the sensor's long axis; the vertical field
        /// follows from the pixel count, since the pixels are square.
        /// </summary>
        public GnomonicProjection(SkyVector boresight, SkyVector up, SkyVector right,
                                  double horizontalFovDeg, int widthPx, int heightPx)
        {
            this.boresight = boresight;
            this.up = up;
            this.right = right;
            WidthPx = widthPx;
            HeightPx = heightPx;
            tanHalfWidth = Math.Tan(0.5 * horizontalFovDeg * Math.PI / 180.0);
            tanHalfHeight = widthPx > 0 ? tanHalfWidth * heightPx / widthPx : tanHalfWidth;
            halfWidthPx = 0.5 * widthPx;
            halfHeightPx = 0.5 * heightPx;
        }

        /// <summary>
        /// Projects a direction onto the sensor. Returns false for anything at or behind the
        /// focal plane, where the gnomonic projection is undefined; the same half-sphere
        /// limit the real optics have.
        ///
        /// The returned coordinates are continuous, not rounded: a star lands between pixels
        /// as often as on one, and the renderer needs that sub-pixel position to place it
        /// correctly (rounding to the nearest pixel centre puts every star on a lattice and is
        /// visible as soon as more than a handful share a frame).
        /// </summary>
        public bool TryProject(SkyVector direction, out double pixelX, out double pixelY)
        {
            pixelX = pixelY = 0.0;
            double w = direction.Dot(boresight);
            if (w <= 1e-9) return false;

            double xi = direction.Dot(right) / w;
            double eta = direction.Dot(up) / w;

            pixelX = halfWidthPx * (1.0 + xi / tanHalfWidth);
            pixelY = halfHeightPx * (1.0 + eta / tanHalfHeight);
            return true;
        }

        /// <summary>
        /// The inverse: which direction a pixel looks at. Needed by anything that fills the frame
        /// FROM the sky rather than placing a source ON it, a diffuse emission map, where every
        /// pixel has to ask what lies behind it.
        ///
        /// Exact rather than iterative: the forward transform is a division by the boresight
        /// component, so undoing it is a linear combination of the three axes and a normalisation.
        /// </summary>
        public SkyVector Deproject(double pixelX, double pixelY)
        {
            double xi = (pixelX / halfWidthPx - 1.0) * tanHalfWidth;
            double eta = (pixelY / halfHeightPx - 1.0) * tanHalfHeight;

            return SkyVector.Normalized(
                boresight.X + xi * right.X + eta * up.X,
                boresight.Y + xi * right.Y + eta * up.Y,
                boresight.Z + xi * right.Z + eta * up.Z);
        }

        /// <summary>
        /// Angular radius (degrees) of a cone that certainly contains the whole sensor: the
        /// half-diagonal of the field, plus a caller-supplied margin. Used to size the
        /// catalogue cone search, which must not miss a star whose light spills onto the sensor
        /// from just outside it.
        /// </summary>
        public double SearchRadiusDeg(double marginDeg)
        {
            double diag = Math.Sqrt(tanHalfWidth * tanHalfWidth + tanHalfHeight * tanHalfHeight);
            return Math.Atan(diag) * 180.0 / Math.PI + Math.Max(0.0, marginDeg);
        }

        /// <summary>The boresight direction, in the same (north, east, up) basis the projection was built in.</summary>
        public SkyVector Boresight => boresight;
    }
}
