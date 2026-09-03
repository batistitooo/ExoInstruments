using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The sky chart's projection: the full celestial sphere as the classic all-sky oval, the
    /// Hammer projection (Snyder, "Map Projections - A Working Manual", USGS PP 1395, p. 160):
    /// a 2:1 ellipse, equal-area, so a patch of sky covers the same chart area wherever it sits
    /// and the star field keeps its true density instead of piling up at a pole. RA 12h runs
    /// down the central meridian, the celestial equator along the long axis, the poles at the
    /// top and bottom of the ellipse, RA 0h at both rims. The chart is inertial: a star's raw
    /// position never changes, which is what lets the whole catalogue be projected once per
    /// session instead of once per second.
    ///
    /// PARITY. A chart of the sky is viewed from INSIDE the sphere, so the RA direction is not
    /// a free choice: the local handedness must match the horizontal dome chart this lineage
    /// replaced, which was verified in game (east 90 degrees clockwise from north in raw y-up
    /// space). That forces RA to increase toward +x here. tools/skychart-tests checks it against
    /// the dome numerically, the check that catches a mirrored sky.
    /// </summary>
    public static class SkyChartProjection
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;
        private const double Sqrt2 = 1.4142135623730951;
        // Right ascension of the central meridian: RA 0h sits on the rims.
        private const double CentralRaDeg = 180.0;
        private const double MarginPx = 4.0;

        /// <summary>Uniform screen scale: the largest s for which the 4*sqrt(2)*s by 2*sqrt(2)*s Hammer ellipse fits the buffer with a margin.</summary>
        public static double ScreenScale(int width, int height)
        {
            double a = width / 2.0 - MarginPx;
            double b = height / 2.0 - MarginPx;
            return Math.Min(a / (2.0 * Sqrt2), b / Sqrt2);
        }

        /// <summary>The ellipse's half axes in raw pixels (2:1 exactly).</summary>
        public static void EllipseHalfAxes(int width, int height, out double halfWidth, out double halfHeight)
        {
            double s = ScreenScale(width, height);
            halfWidth = 2.0 * Sqrt2 * s;
            halfHeight = Sqrt2 * s;
        }

        /// <summary>
        /// Raw chart pixels per degree of great-circle arc AT THE MAP CENTRE, where the Hammer
        /// scale is unity. A nominal figure for UI thresholds (marker-vs-real-disc switchover,
        /// glyph sizes, click tolerances); anything drawn as sky geometry uses the exact local
        /// Jacobian from LocalBasis instead.
        /// </summary>
        public static double RawPixelsPerDegree(int width, int height)
            => ScreenScale(width, height) * DegToRad;

        public static void ProjectRaw(double raDeg, double decDeg, int width, int height,
                                      out double x, out double y)
        {
            double s = ScreenScale(width, height);
            double lambda = Normalize180(raDeg - CentralRaDeg) * DegToRad;
            double phi = decDeg * DegToRad;
            double cosPhi = Math.Cos(phi);
            double z = Math.Sqrt(1.0 + cosPhi * Math.Cos(lambda / 2.0));
            x = width / 2.0 + s * (2.0 * Sqrt2 * cosPhi * Math.Sin(lambda / 2.0) / z);
            y = height / 2.0 + s * (Sqrt2 * Math.Sin(phi) / z);
        }

        /// <summary>Inverse of ProjectRaw (Snyder's closed form). False outside the ellipse, where there is no sky.</summary>
        public static bool TryUnprojectRaw(double rawX, double rawY, int width, int height,
                                           out double raDeg, out double decDeg)
        {
            raDeg = 0.0;
            decDeg = 0.0;
            double s = ScreenScale(width, height);
            if (s <= 0.0) return false;
            // Hammer's own coordinates: X in [-2*sqrt(2), 2*sqrt(2)], Y in [-sqrt(2), sqrt(2)].
            double X = (rawX - width / 2.0) / s;
            double Y = (rawY - height / 2.0) / s;
            double inside = 1.0 - X * X / 16.0 - Y * Y / 4.0;
            // z^2 = 1/2 is exactly the ellipse rim (the RA 0h meridian), which IS sky; the
            // epsilon admits it against floating-point rounding of its own projection.
            if (inside < 0.5 - 1e-9) return false;
            double z = Math.Sqrt(Math.Max(0.5, inside));
            double lambda = 2.0 * Math.Atan2(z * X, 2.0 * (2.0 * z * z - 1.0));
            double sinPhi = z * Y;
            if (sinPhi < -1.0 || sinPhi > 1.0) return false;
            decDeg = Math.Asin(sinPhi) * RadToDeg;
            raDeg = Normalize360(lambda * RadToDeg + CentralRaDeg);
            return true;
        }

        /// <summary>
        /// Unit direction of an (RA, Dec) pair in the equatorial frame: x toward (RA 0, Dec 0),
        /// y toward (RA 90, Dec 0), z toward the north celestial pole.
        /// </summary>
        public static SkyVector DirectionFromEquatorial(double raDeg, double decDeg)
        {
            double ra = raDeg * DegToRad, dec = decDeg * DegToRad;
            double cosDec = Math.Cos(dec);
            return new SkyVector(cosDec * Math.Cos(ra), cosDec * Math.Sin(ra), Math.Sin(dec));
        }

        public static void EquatorialFromDirection(SkyVector dir, out double raDeg, out double decDeg)
        {
            double m = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
            if (m < 1e-12) { raDeg = 0.0; decDeg = 90.0; return; }
            decDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dir.Z / m))) * RadToDeg;
            raDeg = Math.Atan2(dir.Y, dir.X) * RadToDeg;
            if (raDeg < 0.0) raDeg += 360.0;
            }

        /// <summary>
        /// The projection's exact local Jacobian at one sky position, in raw pixels per degree
        /// of great-circle ARC: the screen displacement of one arc-degree along growing
        /// declination and along growing right ascension. Closed-form partial derivatives of the
        /// Hammer forward equations (checked against finite differences by tools/skychart-tests).
        /// Hammer is neither conformal nor equidistant, so the two columns are neither unit nor
        /// orthogonal away from the centre; a body's real disc is drawn by inverting this 2x2,
        /// which keeps the footprint exact to first order (sub-pixel for any disc a few degrees
        /// across) at any position on the map.
        /// </summary>
        public static void LocalBasis(double raDeg, double decDeg, int width, int height,
                                      out double jDecX, out double jDecY,
                                      out double jRaX, out double jRaY)
        {
            double s = ScreenScale(width, height);
            double lambda = Normalize180(raDeg - CentralRaDeg) * DegToRad;
            double phi = decDeg * DegToRad;
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);
            double cosHalf = Math.Cos(lambda / 2.0), sinHalf = Math.Sin(lambda / 2.0);
            double z = Math.Sqrt(1.0 + cosPhi * cosHalf);
            double z2 = z * z;

            double dzdPhi = -sinPhi * cosHalf / (2.0 * z);
            double dzdLambda = -cosPhi * sinHalf / (4.0 * z);

            double dXdPhi = 2.0 * Sqrt2 * (-sinPhi * sinHalf * z - cosPhi * sinHalf * dzdPhi) / z2;
            double dYdPhi = Sqrt2 * (cosPhi * z - sinPhi * dzdPhi) / z2;
            double dXdLambda = 2.0 * Sqrt2 * (cosPhi * cosHalf / 2.0 * z - cosPhi * sinHalf * dzdLambda) / z2;
            double dYdLambda = Sqrt2 * (-sinPhi * dzdLambda) / z2;

            jDecX = s * dXdPhi * DegToRad;
            jDecY = s * dYdPhi * DegToRad;

            // Per arc-degree along RA: one RA-degree spans cos(phi) arc-degrees. The ratio stays
            // finite at the poles (dX/dLambda carries its own cos(phi)); the clamp only guards
            // the exactly-degenerate pole itself.
            double c = Math.Max(1e-9, Math.Abs(cosPhi));
            jRaX = s * dXdLambda * DegToRad / c;
            jRaY = s * dYdLambda * DegToRad / c;
        }

        private static double Normalize180(double deg)
        {
            double d = deg % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d <= -180.0) d += 360.0;
            return d;
        }

        private static double Normalize360(double deg)
        {
            double d = deg % 360.0;
            return d < 0.0 ? d + 360.0 : d;
        }
    }

    /// <summary>One body able to hide targets: its direction, distance and size from the observer.</summary>
    public struct SkyOccluder
    {
        /// <summary>Unit direction from the observer toward the body's centre, equatorial frame.</summary>
        public SkyVector Direction;
        public double DistanceMeters;
        public double RadiusMeters;
        public double AngularRadiusDeg;

        public static SkyOccluder From(SkyVector direction, double distanceMeters, double radiusMeters)
        {
            return new SkyOccluder
            {
                Direction = direction,
                DistanceMeters = distanceMeters,
                RadiusMeters = radiusMeters,
                AngularRadiusDeg = OrbitalVisibility.AngularRadiusDeg(radiusMeters, distanceMeters),
            };
        }
    }

    public enum OcclusionState { Clear, Partial, Full }

    /// <summary>
    /// Exact line-of-sight occlusion, shared by the chart's click test, the plotted-point flags
    /// and the drawing order of body discs. Finite target distance matters and is not skipped: a
    /// moon transiting IN FRONT of its planet overlaps the planet's disc in angle yet is fully
    /// visible, so the segment test against the occluding sphere is the criterion, not the cone.
    /// </summary>
    public static class SkyOcclusion
    {
        /// <summary>
        /// How much of a target of angular radius <paramref name="targetAngularRadiusDeg"/> at
        /// <paramref name="targetDistanceMeters"/> (PositiveInfinity for anything on the celestial
        /// sphere) the occluder hides. Full when the separation is under the occluder's angular
        /// radius minus the target's, Partial out to their sum, Clear beyond, always provided the
        /// occluding surface actually lies in front of the target along the sight line.
        /// </summary>
        public static OcclusionState Classify(SkyVector targetDirection, double targetDistanceMeters,
                                              double targetAngularRadiusDeg, in SkyOccluder occluder)
        {
            if (!(occluder.AngularRadiusDeg > 0.0)) return OcclusionState.Clear;

            double sepDeg = OrbitalVisibility.SeparationDeg(targetDirection, occluder.Direction);
            if (sepDeg >= occluder.AngularRadiusDeg + targetAngularRadiusDeg) return OcclusionState.Clear;

            // Distance along the sight line to the occluder's near surface (down to the tangent
            // point when the centre line misses the sphere). The target is only hidden if that
            // surface is closer than the target.
            double cosSep = Math.Cos(sepDeg * Math.PI / 180.0);
            double sinSep = Math.Sin(sepDeg * Math.PI / 180.0);
            double d = occluder.DistanceMeters;
            double perp = d * sinSep;
            double inside = occluder.RadiusMeters * occluder.RadiusMeters - perp * perp;
            double tNear = d * cosSep - Math.Sqrt(Math.Max(0.0, inside));
            if (!(tNear < targetDistanceMeters)) return OcclusionState.Clear;

            return sepDeg <= occluder.AngularRadiusDeg - targetAngularRadiusDeg
                ? OcclusionState.Full
                : OcclusionState.Partial;
        }

        /// <summary>A point source (a star, a catalogue position): hidden or not.</summary>
        public static bool IsPointOccluded(SkyVector direction, SkyOccluder[] occluders)
        {
            if (occluders == null) return false;
            for (int i = 0; i < occluders.Length; i++)
            {
                if (Classify(direction, double.PositiveInfinity, 0.0, in occluders[i]) == OcclusionState.Full)
                    return true;
            }
            return false;
        }
    }
}
