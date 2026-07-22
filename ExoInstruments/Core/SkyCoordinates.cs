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
    /// The real star catalog (RA/Dec) has no physical relationship to Kerbin's
    /// rotation -- this is a deliberate simplification, consistent with the rest
    /// of the mod's design (see StarCatalog/ExoplanetCsvLoader). We pick an
    /// arbitrary zero point: at UT=0, the observer's meridian is defined to sit
    /// at RA=0h. From there, Kerbin's rotation sweeps the meridian around the
    /// sky -- one full lap per sidereal day, four times faster than Earth's.
    ///
    /// Rotation is computed from UT and the body's rotation period/initial
    /// rotation, not read live from CelestialBody.rotationAngle, so this works
    /// outside the Flight scene (Space Center included).
    /// </summary>
    public static class SkyCoordinates
    {
        // Kerbal Space Center, stock KSP.
        public const double KscLatitudeDeg = -0.0972;
        public const double KscLongitudeDeg = -74.6002;

        // Below this altitude a target is treated as below the horizon --
        // a rough stand-in for terrain/buildings blocking the view.
        public const double DefaultMinAltitudeDeg = 10.0;

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