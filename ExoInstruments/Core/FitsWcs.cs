using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The world coordinate system of a finished frame: the eight numbers that tell any
    /// astronomical tool which patch of sky each pixel looked at.
    ///
    /// WHY THIS EXISTS. The pipeline already performed the exact gnomonic (tangent-plane)
    /// projection FITS calls TAN, built from the camera's own three axes (see
    /// GnomonicProjection), and it already knew the boresight's right ascension and declination,
    /// because that is how the star catalogue was searched. None of that reached the exported
    /// file. A FITS frame without a WCS is a picture: it opens, and nothing can say where it
    /// points, so it cannot be plate-solved, cross-matched against a catalogue, stacked with
    /// another frame by coordinate, or handed to astropy, DS9, SExtractor or photutils for
    /// anything positional. The information existed and was thrown away at the last step.
    ///
    /// THE STANDARD. Calabretta &amp; Greisen (2002, A&amp;A 395, 1077, "Representations of celestial
    /// coordinates in FITS"), with Greisen &amp; Calabretta (2002, A&amp;A 395, 1061) for the keyword
    /// conventions. A TAN projection is described by:
    ///
    ///   CTYPE1/2 = 'RA---TAN' / 'DEC--TAN'   the projection
    ///   CRVAL1/2                              sky coordinates of the reference point
    ///   CRPIX1/2                              pixel coordinates of that reference point
    ///   CD1_1 .. CD2_2                        linear map from pixel offsets to the projection
    ///                                         plane, in degrees
    ///
    /// HOW THE MATRIX IS OBTAINED. Not by re-deriving the plate scale and a position angle from
    /// the instrument's focal length and the observatory's latitude -- that would be a second,
    /// independent implementation of the geometry, free to disagree with the one that actually
    /// placed the stars, and a WCS that disagrees with its own image is worse than none. Instead
    /// the CD matrix is measured FROM the projection the frame was built with: step a small
    /// distance east and north of the boresight on the sky, ask that same projection where those
    /// directions land on the sensor, and invert the resulting 2x2 Jacobian. Whatever orientation,
    /// handedness or field rotation the camera had is captured automatically, because the same
    /// object is answering both questions.
    ///
    /// The offsets are built in the tangent plane at the reference point rather than by stepping
    /// in right ascension, which would divide by cos(dec) and lose all meaning near the pole.
    ///
    /// Pure C#, no Unity dependency, so the whole construction is exercised by the headless
    /// harness against the projection it describes.
    /// </summary>
    public struct FitsWcs
    {
        /// <summary>Right ascension of the reference point, degrees.</summary>
        public double ReferenceRaDeg;
        /// <summary>Declination of the reference point, degrees.</summary>
        public double ReferenceDecDeg;
        /// <summary>Reference pixel, FITS convention: 1-based, and integer values fall on pixel CENTRES.</summary>
        public double ReferencePixelX;
        public double ReferencePixelY;
        /// <summary>CD matrix, degrees per pixel: [CD1_1 CD1_2; CD2_1 CD2_2].</summary>
        public double Cd11, Cd12, Cd21, Cd22;
        /// <summary>False when the geometry could not be resolved, in which case no WCS keywords should be written at all rather than wrong ones.</summary>
        public bool IsValid;

        /// <summary>
        /// Plate scale along each axis, arcsec/pixel, from the CD matrix itself -- the column
        /// norms. Reported for the header's own SECPIX-style bookkeeping and as a sanity check
        /// against the instrument's independently computed plate scale; the two agreeing is a
        /// real check that the WCS describes this frame and not a different one.
        /// </summary>
        public double ScaleXArcsecPerPixel => Math.Sqrt(Cd11 * Cd11 + Cd21 * Cd21) * 3600.0;
        public double ScaleYArcsecPerPixel => Math.Sqrt(Cd12 * Cd12 + Cd22 * Cd22) * 3600.0;

        /// <summary>
        /// Angular offset used to measure the Jacobian, in degrees.
        ///
        /// The usual tension over a finite-difference step -- small enough to be linear, large
        /// enough to beat rounding -- does not apply here, because the map being differentiated is
        /// EXACTLY linear: GnomonicProjection turns a direction into a pixel through
        /// xi = (d.right)/(d.boresight) and then an affine scaling, so a central difference at any
        /// step returns the same Jacobian up to rounding. That makes a LARGER step strictly better,
        /// and the step is set by the one place precision does bite.
        ///
        /// Near the celestial pole, recovering a declination from a direction vector goes through
        /// asin(z) with z within 1e-14 of 1, where the derivative is of order 1e7 and double
        /// precision buys only about 3e-8 degrees. Against a 1e-5 degree step that is a 0.3% error
        /// in the measured scale -- and it showed up exactly there: a field centred on the pole
        /// came back with its plate scale 0.44% wrong while every other pointing was right to
        /// 3e-6. At 1e-3 degrees the same absolute error is a part in 1e5 of the step, so the pole
        /// is as accurate as anywhere else. Verified in tools/bandpass-wcs-tests.
        /// </summary>
        private const double JacobianStepDeg = 1e-3;

        /// <summary>
        /// Builds the WCS for a frame taken with the given projection, at the given local
        /// meridian and observer latitude.
        ///
        /// meridianRaDeg should be the meridian at the START of the exposure, matching DATE-OBS:
        /// on an unguided mount the sky turns during the exposure and every source draws a trail,
        /// so a single WCS can only describe one instant of it, and the start is the instant the
        /// header already timestamps.
        /// </summary>
        public static FitsWcs Build(GnomonicProjection projection, double meridianRaDeg, double observerLatitudeDeg)
        {
            var wcs = new FitsWcs { IsValid = false };

            SkyVector boresight = projection.Boresight;
            double altDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, boresight.Z))) * 180.0 / Math.PI;
            double azDeg = Math.Atan2(boresight.Y, boresight.X) * 180.0 / Math.PI;

            SkyCoordinates.HorizontalToEquatorial(altDeg, azDeg, meridianRaDeg, observerLatitudeDeg,
                                                  out double raDeg, out double decDeg);

            // The reference pixel is taken by projecting the boresight DIRECTLY, not by sending it
            // out through the equatorial frame and back. The round trip is exact away from the
            // pole, but EquatorialToHorizontal carries a tan(dec) that diverges at dec = +/-90, so
            // a field centred on the celestial pole -- Polaris, a circumpolar target, an
            // instrument parked on the pole -- came back with its plate scale off by 0.4%. The
            // boresight is already a direction in the frame the projection works in, so nothing
            // needs converting to place it.
            if (!projection.TryProject(boresight, out double x0, out double y0))
                return wcs;

            // Central differences in the tangent plane: one step each way along east and north.
            if (!TryProjectSky(projection, raDeg, decDeg, +JacobianStepDeg, 0.0, meridianRaDeg, observerLatitudeDeg, out double xEastPlus, out double yEastPlus)) return wcs;
            if (!TryProjectSky(projection, raDeg, decDeg, -JacobianStepDeg, 0.0, meridianRaDeg, observerLatitudeDeg, out double xEastMinus, out double yEastMinus)) return wcs;
            if (!TryProjectSky(projection, raDeg, decDeg, 0.0, +JacobianStepDeg, meridianRaDeg, observerLatitudeDeg, out double xNorthPlus, out double yNorthPlus)) return wcs;
            if (!TryProjectSky(projection, raDeg, decDeg, 0.0, -JacobianStepDeg, meridianRaDeg, observerLatitudeDeg, out double xNorthMinus, out double yNorthMinus)) return wcs;

            // Jacobian of pixel position with respect to the projection-plane coordinates
            // (xi east, eta north), in pixels per degree.
            double dxdXi = (xEastPlus - xEastMinus) / (2.0 * JacobianStepDeg);
            double dydXi = (yEastPlus - yEastMinus) / (2.0 * JacobianStepDeg);
            double dxdEta = (xNorthPlus - xNorthMinus) / (2.0 * JacobianStepDeg);
            double dydEta = (yNorthPlus - yNorthMinus) / (2.0 * JacobianStepDeg);

            // The CD matrix is the inverse: degrees of projection plane per pixel.
            double det = dxdXi * dydEta - dxdEta * dydXi;
            if (Math.Abs(det) < 1e-30 || double.IsNaN(det)) return wcs;

            wcs.Cd11 = dydEta / det;
            wcs.Cd12 = -dxdEta / det;
            wcs.Cd21 = -dydXi / det;
            wcs.Cd22 = dxdXi / det;

            wcs.ReferenceRaDeg = raDeg;
            wcs.ReferenceDecDeg = decDeg;
            // Array index i spans [i, i+1) and is centred at i+0.5 (see StarFieldRenderer.Splat),
            // while FITS puts pixel centres on integers starting at 1. The two conventions differ
            // by exactly half a pixel.
            wcs.ReferencePixelX = x0 + 0.5;
            wcs.ReferencePixelY = y0 + 0.5;
            wcs.IsValid = true;
            return wcs;
        }

        /// <summary>
        /// Where the sky point at tangent-plane offset (xiDeg east, etaDeg north) from
        /// (raDeg, decDeg) lands on the sensor.
        ///
        /// The offset is applied as a real displacement of the direction vector in the equatorial
        /// frame, which is the exact inverse gnomonic projection rather than an approximation to
        /// it, and is well behaved at the pole where stepping in right ascension is not.
        /// </summary>
        private static bool TryProjectSky(GnomonicProjection projection,
                                          double raDeg, double decDeg, double xiDeg, double etaDeg,
                                          double meridianRaDeg, double observerLatitudeDeg,
                                          out double pixelX, out double pixelY)
        {
            const double DegToRad = Math.PI / 180.0;
            double ra = raDeg * DegToRad;
            double dec = decDeg * DegToRad;
            double cosDec = Math.Cos(dec), sinDec = Math.Sin(dec);
            double cosRa = Math.Cos(ra), sinRa = Math.Sin(ra);

            // Unit vector at the reference point, and the local east/north tangent basis there.
            double rx = cosDec * cosRa, ry = cosDec * sinRa, rz = sinDec;
            double ex = -sinRa, ey = cosRa, ez = 0.0;
            double nx = -sinDec * cosRa, ny = -sinDec * sinRa, nz = cosDec;

            double xi = xiDeg * DegToRad;
            double eta = etaDeg * DegToRad;
            double vx = rx + xi * ex + eta * nx;
            double vy = ry + xi * ey + eta * ny;
            double vz = rz + xi * ez + eta * nz;
            double norm = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (norm < 1e-12) { pixelX = pixelY = 0.0; return false; }
            vx /= norm; vy /= norm; vz /= norm;

            double offsetRaDeg = Math.Atan2(vy, vx) / DegToRad;
            double offsetDecDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, vz))) / DegToRad;

            HorizontalCoordinates horizontal = SkyCoordinates.EquatorialToHorizontal(
                offsetRaDeg, offsetDecDeg, meridianRaDeg, observerLatitudeDeg);
            SkyVector direction = SkyVector.FromHorizontal(horizontal.AltitudeDeg, horizontal.AzimuthDeg);

            return projection.TryProject(direction, out pixelX, out pixelY);
        }
    }
}
