using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// What a telescope in orbit can and cannot look at, and when.
    ///
    /// WHY THIS EXISTS, AND WHY IT IS NOT THE GROUND MODEL WITH THE ATMOSPHERE DELETED.
    /// ImagingObservingConditions gates a ground instrument on two things: is the Sun down, and
    /// is the target high enough. Neither question means anything in orbit. There is no night,
    /// there is no horizon, and there is no airmass. What replaces them is a different set of
    /// constraints entirely, and they are the ones that actually shape a space telescope's
    /// observing programme:
    ///
    ///   * the host body occults the target for part of every orbit;
    ///   * pointing too near the bright limb of that body floods the detector with scattered
    ///     light long before the target is geometrically occulted;
    ///   * the Sun and the Moon carry hard avoidance angles, set by the spacecraft's thermal
    ///     design and its detectors' safety, not by image quality.
    ///
    /// All three are geometry, all three are published for the real instruments, and together
    /// they produce the observing duty cycle that is the whole reason a space telescope behaves
    /// differently from a ground one.
    ///
    /// EVERY ANGLE HERE IS THE INSTRUMENT'S OWN. The avoidance angles live on
    /// SpacePlatformSpec, not in this file, because they are properties of a particular
    /// spacecraft: HST's bright-Earth avoidance is a number STScI publishes for HST, and a
    /// different telescope has a different one. This module supplies only the geometry.
    ///
    /// Pure C# with no Unity or KSP dependency, like the rest of Core; positions come in as
    /// plain metres in whatever inertial frame the caller is using, and the only requirement is
    /// that all of them share it.
    /// </summary>
    public static class OrbitalVisibility
    {
        private const double DegPerRad = 180.0 / Math.PI;
        private const double RadPerDeg = Math.PI / 180.0;

        /// <summary>
        /// Angular radius of a body of radius <paramref name="bodyRadiusMeters"/> seen from
        /// <paramref name="observerDistanceMeters"/> from its centre, in degrees.
        ///
        /// Returns 90 degrees when the observer is at or inside the surface: the body then fills
        /// the entire lower hemisphere, which is the correct limit and keeps every caller below
        /// from having to special-case a launch pad.
        /// </summary>
        public static double AngularRadiusDeg(double bodyRadiusMeters, double observerDistanceMeters)
        {
            if (!(bodyRadiusMeters > 0.0) || !(observerDistanceMeters > 0.0)) return 0.0;
            if (observerDistanceMeters <= bodyRadiusMeters) return 90.0;
            return Math.Asin(bodyRadiusMeters / observerDistanceMeters) * DegPerRad;
        }

        /// <summary>
        /// Geometry of one line of sight past one body, for an observer at
        /// <paramref name="observerFromBody"/> (metres, body centre to observer) looking along
        /// the unit vector <paramref name="lineOfSight"/>.
        ///
        /// <paramref name="sunFromBody"/> is the direction from the body's centre toward the
        /// Sun, and decides whether the limb the telescope is looking past is the lit one. That
        /// distinction is worth far more than it looks: the published avoidance angle for a
        /// sunlit limb is several times the one for a dark limb, because the constraint is
        /// scattered sunlight rather than the geometry of the body.
        /// </summary>
        public static LimbGeometry EvaluateLimb(SkyVector observerFromBody, SkyVector lineOfSight,
                                                double bodyRadiusMeters, SkyVector sunFromBody)
        {
            var g = new LimbGeometry();

            double r = Math.Sqrt(observerFromBody.X * observerFromBody.X
                               + observerFromBody.Y * observerFromBody.Y
                               + observerFromBody.Z * observerFromBody.Z);
            if (!(r > 0.0) || !(bodyRadiusMeters > 0.0))
            {
                // No body to be blocked by (or a degenerate position): everything is visible.
                g.CentreSeparationDeg = 180.0;
                g.AngularRadiusDeg = 0.0;
                g.LimbAngleDeg = 180.0;
                g.Occulted = false;
                g.LimbIsSunlit = false;
                return g;
            }

            // Direction from the observer TO the body centre. Note the sign: observerFromBody
            // points outward from the body, so the body is in the opposite direction.
            var toCentre = SkyVector.Normalized(-observerFromBody.X, -observerFromBody.Y, -observerFromBody.Z);

            double cosSep = Clamp(toCentre.Dot(lineOfSight), -1.0, 1.0);
            g.CentreSeparationDeg = Math.Acos(cosSep) * DegPerRad;
            g.AngularRadiusDeg = AngularRadiusDeg(bodyRadiusMeters, r);
            g.LimbAngleDeg = g.CentreSeparationDeg - g.AngularRadiusDeg;
            g.Occulted = g.LimbAngleDeg <= 0.0;

            g.LimbIsSunlit = NearestLimbIsSunlit(observerFromBody, lineOfSight, bodyRadiusMeters,
                                                 sunFromBody, r, toCentre, cosSep);
            return g;
        }

        /// <summary>
        /// Whether the point on the limb nearest the line of sight is on the body's day side.
        ///
        /// THE POINT IS FOUND, NOT ASSUMED. A tempting shortcut is to ask whether the observer
        /// is over the day side, or whether the target is roughly sunward; both are wrong near
        /// the terminator, which is exactly where the answer matters, because that is where a
        /// telescope skims from a dark limb to a bright one within a single exposure.
        ///
        /// The tangent point is constructed instead. It lies in the plane spanned by the
        /// direction to the body centre and the line of sight, at the tangent angle from the
        /// centre direction. Its outward surface normal is what the Sun either does or does not
        /// illuminate, and the sign of one dot product settles it.
        /// </summary>
        private static bool NearestLimbIsSunlit(SkyVector observerFromBody, SkyVector lineOfSight,
                                                double bodyRadiusMeters, SkyVector sunFromBody,
                                                double r, SkyVector toCentre, double cosSep)
        {
            double sunMag = Math.Sqrt(sunFromBody.X * sunFromBody.X
                                    + sunFromBody.Y * sunFromBody.Y
                                    + sunFromBody.Z * sunFromBody.Z);
            if (!(sunMag > 0.0)) return false;   // no Sun on record: treat every limb as dark
            var sunDir = SkyVector.Normalized(sunFromBody.X, sunFromBody.Y, sunFromBody.Z);

            // In-plane unit vector perpendicular to toCentre, pointing toward the line of sight.
            // Degenerate when the line of sight is straight at (or straight away from) the
            // centre, where every limb point is equally near and the plane is undefined.
            double px = lineOfSight.X - cosSep * toCentre.X;
            double py = lineOfSight.Y - cosSep * toCentre.Y;
            double pz = lineOfSight.Z - cosSep * toCentre.Z;
            double pMag = Math.Sqrt(px * px + py * py + pz * pz);

            SkyVector limbPoint;
            if (pMag < 1e-9)
            {
                // Looking down the centre line: take the sub-solar side of the limb ring, which
                // is the brightest part of it, so the constraint stays conservative.
                double dotSun = Clamp(sunDir.Dot(toCentre), -1.0, 1.0);
                double sx = sunDir.X - dotSun * toCentre.X;
                double sy = sunDir.Y - dotSun * toCentre.Y;
                double sz = sunDir.Z - dotSun * toCentre.Z;
                double sMag = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                if (sMag < 1e-9) return dotSun > 0.0;   // Sun exactly behind or in front of the body
                px = sx / sMag; py = sy / sMag; pz = sz / sMag;
            }
            else
            {
                px /= pMag; py /= pMag; pz /= pMag;
            }

            // The tangent point sits at angle acos(R/r) from the observer direction, measured at
            // the body's centre, rotated toward the line of sight.
            double cosTangent = Clamp(bodyRadiusMeters / r, -1.0, 1.0);
            double tangentAngle = Math.Acos(cosTangent);      // from the observer direction
            var outward = SkyVector.Normalized(observerFromBody.X, observerFromBody.Y, observerFromBody.Z);
            double c = Math.Cos(tangentAngle), s = Math.Sin(tangentAngle);
            limbPoint = new SkyVector(c * outward.X + s * px,
                                      c * outward.Y + s * py,
                                      c * outward.Z + s * pz);

            return limbPoint.Dot(sunDir) > 0.0;
        }

        /// <summary>
        /// Fraction of one circular orbit for which a target at infinity is blocked, counting a
        /// blocking half-angle of <paramref name="blockingHalfAngleDeg"/> measured from the body's
        /// centre. Pass the body's own angular radius for pure geometric occultation, or that
        /// radius plus a limb-avoidance angle for the operational figure.
        ///
        /// <paramref name="targetElevationDeg"/> is the target's angle above the orbital plane;
        /// it is the only thing about the target that matters, since a target at infinity is
        /// occulted by where the observer is in its orbit, not by where the target is along that
        /// direction.
        ///
        /// THE DERIVATION, since it is short and the result is the shape of the whole duty cycle.
        /// Put the observer at radius a and phase phi in its orbital plane, and the target at
        /// elevation beta. The body blocks the line of sight when the observer's position lies
        /// inside the cylinder of the blocking cone behind the body, which reduces to
        ///
        ///     |cos phi| &gt; sqrt(1 - sin^2 rho) / cos beta = cos rho / cos beta
        ///
        /// with rho the blocking half-angle, together with the observer being on the far side.
        /// Writing k = cos(rho) / cos(beta), the blocked phase interval has width 2 acos(k), so
        /// the blocked fraction is acos(k)/pi, and k &gt;= 1 is the continuous-viewing condition:
        /// a target more than (90 - rho) degrees off the orbital plane is never blocked at all.
        /// </summary>
        public static double OccultedOrbitFraction(double blockingHalfAngleDeg, double targetElevationDeg)
        {
            double rho = Math.Abs(blockingHalfAngleDeg) * RadPerDeg;
            if (rho <= 0.0) return 0.0;
            if (rho >= Math.PI / 2.0) return 1.0;   // blocked in every direction

            double beta = Math.Abs(targetElevationDeg) * RadPerDeg;
            if (beta >= Math.PI / 2.0) return 0.0;

            double cosBeta = Math.Cos(beta);
            if (cosBeta <= 1e-12) return 0.0;

            double k = Math.Cos(rho) / cosBeta;
            if (k >= 1.0) return 0.0;               // continuous viewing zone
            if (k <= -1.0) return 1.0;
            return Math.Acos(k) / Math.PI;
        }

        /// <summary>
        /// Smallest angle from the orbital plane at which a target is never blocked, in degrees:
        /// the half-width of the continuous viewing zone, measured from the orbital POLE rather
        /// than the plane (so a small number is a small zone).
        ///
        /// This is just the complement of the blocking half-angle, and it is stated separately
        /// because it is the form the number is always published in: STScI quotes HST's zone as
        /// targets "within 24 degrees of the orbital poles".
        /// </summary>
        public static double ContinuousViewingHalfWidthDeg(double blockingHalfAngleDeg)
        {
            double w = 90.0 - Math.Abs(blockingHalfAngleDeg);
            return w > 0.0 ? w : 0.0;
        }

        /// <summary>
        /// Elevation of a direction above an orbital plane, in degrees, given the plane's unit
        /// normal. Sign is dropped: only the magnitude matters to visibility, and a signed value
        /// would only invite the caller to care which side of the plane a target is on, which
        /// nothing here does.
        /// </summary>
        public static double ElevationAboveOrbitPlaneDeg(SkyVector orbitNormal, SkyVector direction)
        {
            double n = Math.Sqrt(orbitNormal.X * orbitNormal.X
                               + orbitNormal.Y * orbitNormal.Y
                               + orbitNormal.Z * orbitNormal.Z);
            if (!(n > 0.0)) return 0.0;
            var unit = new SkyVector(orbitNormal.X / n, orbitNormal.Y / n, orbitNormal.Z / n);
            double sinBeta = Clamp(unit.Dot(direction), -1.0, 1.0);
            return Math.Abs(Math.Asin(sinBeta)) * DegPerRad;
        }

        /// <summary>Angle between two unit directions, in degrees.</summary>
        public static double SeparationDeg(SkyVector a, SkyVector b)
        {
            return Math.Acos(Clamp(a.Dot(b), -1.0, 1.0)) * DegPerRad;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>One line of sight's relationship to one body's disk.</summary>
    public struct LimbGeometry
    {
        /// <summary>Angle between the line of sight and the body's centre, degrees.</summary>
        public double CentreSeparationDeg;

        /// <summary>The body's own angular radius from here, degrees.</summary>
        public double AngularRadiusDeg;

        /// <summary>
        /// Angle from the line of sight down to the nearest point of the body's limb, degrees.
        /// Negative while the target is behind the disk, which is the quantity's natural
        /// continuation rather than a sentinel: it is how far INSIDE the disk the sight line is.
        /// </summary>
        public double LimbAngleDeg;

        /// <summary>True while the body's disk covers the target.</summary>
        public bool Occulted;

        /// <summary>True when the nearest limb point is on the body's day side (see NearestLimbIsSunlit).</summary>
        public bool LimbIsSunlit;
    }
}
