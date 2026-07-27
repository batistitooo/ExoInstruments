using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Altitude/azimuth of a target, as seen from a fixed observer at a given moment.
    /// Azimuth is measured from North, clockwise through East (standard compass convention).
    /// </summary>
    public struct HorizontalCoordinates
    {
        public double AltitudeDeg;
        public double AzimuthDeg;

        public bool IsAboveHorizon(double minAltitudeDeg) => AltitudeDeg > minAltitudeDeg;
    }

    /// <summary>
    /// Pure C# equatorial-to-horizontal transform -- no Unity/KSP dependency.
    ///
    /// Nothing here is tied to a particular home world. Every input that could be
    /// -- the observer's latitude and longitude, the body's rotation period and
    /// initial rotation -- is passed in by the caller, which reads them from the
    /// running game (see ObservatorySite and BuildImagingObserverContext). The
    /// transform itself is the standard one and is equally valid on Kerbin, on a
    /// Real Solar System Earth, or on anything else a planet pack installs.
    ///
    /// On the RA zero point: on stock KSP the real star catalog has no physical
    /// relationship to Kerbin's rotation, so the convention is arbitrary -- at
    /// UT=0 the observer's meridian is defined to sit at RA=0h, and Kerbin's spin
    /// sweeps it around the sky from there, one lap per sidereal day, four times
    /// faster than Earth's. On a pack that models the real solar system, the same
    /// arithmetic acquires real meaning for free: the home body's rotation period
    /// becomes a real sidereal day, and because such packs define their inertial
    /// frame to be the real one (that is how they place bodies on real orbital
    /// elements), the angle this returns tracks genuine local sidereal time.
    /// Whether it also matches a given skybox's own orientation is that skybox's
    /// business -- see SkyAlignmentOffsetHours, which exists for exactly that.
    ///
    /// Rotation is computed from UT and the body's rotation period/initial
    /// rotation, not read live from CelestialBody.rotationAngle, so this works
    /// outside the Flight scene (Space Center included).
    /// </summary>
    public static class SkyCoordinates
    {
        /// <summary>
        /// The right ascension currently sitting on the observer's meridian.
        /// Feed this bodyRotationPeriodSeconds/bodyInitialRotationDeg from
        /// FlightGlobals.GetHomeBody().rotationPeriod / .initialRotation.
        /// </summary>
        public static double ComputeLocalMeridianRaDeg(
            double universalTime,
            double bodyRotationPeriodSeconds,
            double bodyInitialRotationDeg,
            double observerLongitudeDeg)
        {
            if (bodyRotationPeriodSeconds <= 0) return NormalizeDegrees(observerLongitudeDeg);
            double rotationFraction = (universalTime / bodyRotationPeriodSeconds) % 1.0;
            double bodyRotationDeg = bodyInitialRotationDeg + rotationFraction * 360.0;
            return NormalizeDegrees(bodyRotationDeg + observerLongitudeDeg);
        }

        /// <summary>
        /// Standard equatorial -> horizontal transform (Meeus' atan2 form --
        /// robust near the zenith and poles, no naive-division blowups).
        /// Azimuth returned from North, clockwise.
        /// </summary>
        public static HorizontalCoordinates EquatorialToHorizontal(
            double raDeg, double decDeg,
            double localMeridianRaDeg, double observerLatitudeDeg)
        {
            double hourAngleDeg = NormalizeSigned(localMeridianRaDeg - raDeg);
            double hRad = DegToRad(hourAngleDeg);
            double latRad = DegToRad(observerLatitudeDeg);
            double decRad = DegToRad(decDeg);

            double sinAlt = Math.Sin(latRad) * Math.Sin(decRad) + Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(hRad);
            sinAlt = Math.Min(1.0, Math.Max(-1.0, sinAlt));
            double altDeg = RadToDeg(Math.Asin(sinAlt));

            double y = Math.Sin(hRad);
            double x = Math.Cos(hRad) * Math.Sin(latRad) - Math.Tan(decRad) * Math.Cos(latRad);
            double azFromSouthDeg = RadToDeg(Math.Atan2(y, x));
            double azDeg = NormalizeDegrees(azFromSouthDeg + 180.0);

            return new HorizontalCoordinates { AltitudeDeg = altDeg, AzimuthDeg = azDeg };
        }

        /// <summary>
        /// The inverse of EquatorialToHorizontal: where a direction the observer is physically
        /// pointing at sits in the catalogue's equatorial frame (Meeus, "Astronomical
        /// Algorithms", Ch. 13, the same transformation pair read the other way).
        ///
        /// This is what ties the camera to the star catalogue. The telescope's aim is known as a
        /// real direction in the game's world, which converts to altitude and azimuth over the
        /// observatory; the catalogue is in right ascension and declination. Without this
        /// inversion there is no way to ask "which stars is the instrument actually looking at",
        /// and the rendered frame can only ever contain what the game itself drew.
        /// </summary>
        public static void HorizontalToEquatorial(
            double altitudeDeg, double azimuthDeg,
            double localMeridianRaDeg, double observerLatitudeDeg,
            out double raDeg, out double decDeg)
        {
            double altRad = DegToRad(altitudeDeg);
            double latRad = DegToRad(observerLatitudeDeg);
            // Meeus measures azimuth westward from south; this mod measures it from north
            // through east, the compass convention, so the two differ by half a turn.
            double azFromSouthRad = DegToRad(azimuthDeg - 180.0);

            double sinDec = Math.Sin(latRad) * Math.Sin(altRad) - Math.Cos(latRad) * Math.Cos(altRad) * Math.Cos(azFromSouthRad);
            sinDec = Math.Min(1.0, Math.Max(-1.0, sinDec));
            decDeg = RadToDeg(Math.Asin(sinDec));

            double y = Math.Sin(azFromSouthRad);
            double x = Math.Cos(azFromSouthRad) * Math.Sin(latRad) + Math.Tan(altRad) * Math.Cos(latRad);
            double hourAngleDeg = RadToDeg(Math.Atan2(y, x));

            raDeg = NormalizeDegrees(localMeridianRaDeg - hourAngleDeg);
        }

        /// <summary>Convenience overload for a catalog target; null if it has no RA/Dec.</summary>
        public static HorizontalCoordinates? TryComputeHorizontal(
            StarTarget target,
            double localMeridianRaDeg, double observerLatitudeDeg)
        {
            if (!target.RaDeg.HasValue || !target.DecDeg.HasValue) return null;
            return EquatorialToHorizontal(target.RaDeg.Value, target.DecDeg.Value, localMeridianRaDeg, observerLatitudeDeg);
        }

        private static double DegToRad(double deg) => deg * Math.PI / 180.0;
        private static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

        /// <summary>Wraps to [0, 360).</summary>
        private static double NormalizeDegrees(double deg)
        {
            double d = deg % 360.0;
            return d < 0 ? d + 360.0 : d;
        }

        /// <summary>Wraps to (-180, 180] -- keeps the hour angle small near the meridian.</summary>
        private static double NormalizeSigned(double deg)
        {
            double d = NormalizeDegrees(deg);
            return d > 180.0 ? d - 360.0 : d;
        }
    }
}