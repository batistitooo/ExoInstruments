using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Equatorial to Galactic coordinates.
    ///
    /// Every all-sky dust, H-alpha and CO map is tabulated in Galactic coordinates, because that is
    /// the frame the emitting material is organised in. A map lookup from a catalogue position
    /// therefore has to cross this boundary, and crossing it wrongly puts the Galactic plane at the
    /// wrong angle across the sky -- visible, but only if you know what it should look like.
    ///
    /// The pole is the ICRS realisation of the IAU 1958 Galactic frame, from the Hipparcos
    /// catalogue documentation (ESA 1997, SP-1200, Vol. 1, Sect. 1.5.3):
    ///
    ///     alpha_NGP = 192.85948 deg, delta_NGP = 27.12825 deg, l_NCP = 122.93192 deg
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class GalacticCoordinates
    {
        /// <summary>Right ascension of the north Galactic pole, degrees (ICRS).</summary>
        public const double NorthGalacticPoleRaDeg = 192.85948;

        /// <summary>Declination of the north Galactic pole, degrees (ICRS).</summary>
        public const double NorthGalacticPoleDecDeg = 27.12825;

        /// <summary>Galactic longitude of the north celestial pole, degrees.</summary>
        public const double NorthCelestialPoleGalacticLonDeg = 122.93192;

        private const double Deg = Math.PI / 180.0;

        /// <summary>Galactic longitude and latitude of an ICRS position, degrees.</summary>
        public static void EquatorialToGalactic(double raDeg, double decDeg, out double lDeg, out double bDeg)
        {
            double ra = raDeg * Deg, dec = decDeg * Deg;
            double raP = NorthGalacticPoleRaDeg * Deg, decP = NorthGalacticPoleDecDeg * Deg;

            double sinB = Math.Sin(decP) * Math.Sin(dec)
                        + Math.Cos(decP) * Math.Cos(dec) * Math.Cos(ra - raP);
            sinB = sinB < -1.0 ? -1.0 : (sinB > 1.0 ? 1.0 : sinB);
            bDeg = Math.Asin(sinB) / Deg;

            double y = Math.Cos(dec) * Math.Sin(ra - raP);
            double x = Math.Cos(decP) * Math.Sin(dec) - Math.Sin(decP) * Math.Cos(dec) * Math.Cos(ra - raP);
            lDeg = SexagesimalCoordinates.Normalize360(NorthCelestialPoleGalacticLonDeg - Math.Atan2(y, x) / Deg);
        }

        /// <summary>The inverse, so a map cell can be reported back as a sky position.</summary>
        public static void GalacticToEquatorial(double lDeg, double bDeg, out double raDeg, out double decDeg)
        {
            double l = lDeg * Deg, b = bDeg * Deg;
            double decP = NorthGalacticPoleDecDeg * Deg;
            double lNcp = NorthCelestialPoleGalacticLonDeg * Deg;

            double sinDec = Math.Sin(decP) * Math.Sin(b)
                          + Math.Cos(decP) * Math.Cos(b) * Math.Cos(lNcp - l);
            sinDec = sinDec < -1.0 ? -1.0 : (sinDec > 1.0 ? 1.0 : sinDec);
            decDeg = Math.Asin(sinDec) / Deg;

            double y = Math.Cos(b) * Math.Sin(lNcp - l);
            double x = Math.Cos(decP) * Math.Sin(b) - Math.Sin(decP) * Math.Cos(b) * Math.Cos(lNcp - l);
            raDeg = SexagesimalCoordinates.Normalize360(Math.Atan2(y, x) / Deg + NorthGalacticPoleRaDeg);
        }
    }
}
