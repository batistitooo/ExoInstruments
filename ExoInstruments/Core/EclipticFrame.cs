using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The ecliptic: the plane the zodiacal light is distributed about, and the frame
    /// ZodiacalLight's published table is indexed in.
    ///
    /// WHERE IT COMES FROM IN A GAME. The real ecliptic is Earth's orbital plane, by definition.
    /// The counterpart here is the HOME BODY's orbital plane, read from its own orbit, which is
    /// the same definition applied to whatever world the player is actually flying from. That
    /// makes it exact under Real Solar System, where the home body IS Earth, and it makes it the
    /// physically right plane in a stock system, where every planet orbits within a couple of
    /// degrees of it. Nothing is assumed about the system's layout: if a planet pack gives the
    /// home world a wildly inclined orbit, this returns that plane, and the zodiacal cloud is
    /// modelled about it, which is what a dust cloud co-orbiting with that system would do.
    ///
    /// WHAT THE ANGLES MEAN. Ecliptic latitude is the angle above the plane, unsigned in the
    /// table because the cloud is symmetric about it. Heliocentric ecliptic longitude is the
    /// target's longitude MINUS the Sun's, so 0 degrees is toward the Sun and 180 is the
    /// anti-solar point; that is the convention Table 9.4 of the WFC3 Instrument Handbook and
    /// Leinert et al. (1998) both use, and it is what makes the table independent of the time of
    /// year.
    ///
    /// Pure C# with no Unity dependency: the caller supplies the orbit normal and the direction
    /// to the Sun, both of which the KSP layer reads off the live game.
    /// </summary>
    public static class EclipticFrame
    {
        private const double DegPerRad = 180.0 / Math.PI;

        /// <summary>
        /// Ecliptic latitude and heliocentric ecliptic longitude of a direction, degrees.
        ///
        /// <paramref name="eclipticNorth"/> is the unit normal of the home body's orbital plane
        /// and <paramref name="sunDirection"/> the unit direction toward the Sun; both in the
        /// same frame as <paramref name="direction"/>. The longitude origin is the Sun itself,
        /// so no separate reference direction is needed and none is invented.
        ///
        /// Returns false when the geometry is degenerate, which happens only if the Sun lies
        /// along the orbit normal (it cannot, for a body orbiting that Sun) or if either input
        /// is a zero vector.
        /// </summary>
        public static bool TryCompute(SkyVector direction, SkyVector eclipticNorth, SkyVector sunDirection,
                                      out double latitudeDeg, out double heliocentricLongitudeDeg)
        {
            latitudeDeg = 0.0;
            heliocentricLongitudeDeg = 180.0;

            if (!TryNormalise(eclipticNorth, out SkyVector n)) return false;
            if (!TryNormalise(sunDirection, out SkyVector sun)) return false;
            if (!TryNormalise(direction, out SkyVector d)) return false;

            // Latitude: the angle out of the plane.
            double sinBeta = Clamp(n.Dot(d), -1.0, 1.0);
            latitudeDeg = Math.Asin(sinBeta) * DegPerRad;

            // In-plane basis with the Sun's own projected direction as the longitude origin.
            SkyVector x = ProjectOntoPlane(sun, n);
            if (!TryNormalise(x, out x)) return false;
            SkyVector y = Cross(n, x);

            SkyVector dPlane = ProjectOntoPlane(d, n);
            double px = dPlane.Dot(x), py = dPlane.Dot(y);
            if (Math.Abs(px) < 1e-15 && Math.Abs(py) < 1e-15)
            {
                // The direction is along the pole: longitude is undefined and irrelevant, since
                // the table is flat in longitude there anyway.
                heliocentricLongitudeDeg = 180.0;
                return true;
            }

            double lon = Math.Atan2(py, px) * DegPerRad;
            heliocentricLongitudeDeg = lon < 0.0 ? lon + 360.0 : lon;
            return true;
        }

        private static SkyVector ProjectOntoPlane(SkyVector v, SkyVector unitNormal)
        {
            double d = v.Dot(unitNormal);
            return new SkyVector(v.X - d * unitNormal.X, v.Y - d * unitNormal.Y, v.Z - d * unitNormal.Z);
        }

        private static SkyVector Cross(SkyVector a, SkyVector b)
            => new SkyVector(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        private static bool TryNormalise(SkyVector v, out SkyVector unit)
        {
            double m = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (!(m > 1e-12)) { unit = new SkyVector(0, 0, 1); return false; }
            unit = new SkyVector(v.X / m, v.Y / m, v.Z / m);
            return true;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
