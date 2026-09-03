using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The transformation from the J2000 equatorial frame every catalogue in this mod is expressed
    /// in to the mean equinox of B1875, which is the frame the IAU constellation boundaries are
    /// drawn in and the only frame they are rectangles in.
    ///
    /// WHY THIS IS NOT JUST A PRECESSION. B1875 is a BESSELIAN equinox of the old fundamental
    /// system (FK4), and J2000 is a JULIAN equinox of the new one (FK5). The two differ by more
    /// than the 125 years between them: the FK4 system carries a systematic rotation and a
    /// different precession constant, and running a modern precession model back to "the year
    /// 1875" silently conflates the two. The rigorous route, and the one astropy's FK4NoETerms
    /// frame takes, is in two steps:
    ///
    ///   1. FK5 J2000 -&gt; FK4 B1950, by the rotation matrix of Murray (1989, A&amp;A 218, 325,
    ///      eq. 28), which is the measured relationship between the two fundamental systems;
    ///   2. FK4 B1950 -&gt; FK4 B1875, by Newcomb's precession, the precession the FK4 system is
    ///      built on (expressions as tabulated in the Explanatory Supplement to the Astronomical
    ///      Almanac, 1992, chapter 3).
    ///
    /// WHY NO E-TERMS. FK4 positions of stars carry the E-terms of elliptic aberration, up to
    /// 0.343 arcsec. Those are a property of an OBSERVED position, not of the coordinate grid;
    /// Delporte's boundaries are lines of constant right ascension and declination on that grid,
    /// so the E-terms do not belong in this chain. Roman's table quantises right ascension to
    /// 0.0001 h, which is 1.5 arcsec at the equator, so they would sit below its own resolution
    /// in any case.
    ///
    /// HOW ACCURATE THIS HAS TO BE. Only positions within a few arcseconds of a boundary can be
    /// affected at all, and the answer for those is genuinely ambiguous at the level of the
    /// published table. tools/constellation-tests compares this implementation against astropy's
    /// own FK4NoETerms transform.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class BesselianFrames
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;

        // FK4 B1950 to FK5 J2000, Murray (1989, A&A 218, 325) eq. 28: the rotation between the two fundamental
        // systems, with the E-terms already removed (this is the matrix that acts on FK4 positions from which
        // the elliptic aberration has been subtracted).
        private static readonly double[] B1950ToJ2000 =
        {
            +0.9999256794956877, -0.0111814832204662, -0.0048590038153592,
            +0.0111814832391717, +0.9999374848933135, -0.0000271625947142,
            +0.0048590037723143, -0.0000271702937440, +0.9999881946023742,
        };

        // Murray's eq. 29 correction, per Julian century from 1950. FK4 is a ROTATING system with respect to
        // FK5 (its equinox creeps), so the relation between the two is not one fixed matrix but depends on the
        // epoch the FK4 coordinates belong to. Over the 75 years from B1950 back to B1875 this reaches 1.6e-6
        // in the matrix elements, about a third of an arcsecond on the sky: below the 1.5 arcsec that Roman's
        // own right-ascension quantisation carries, but included because leaving it out would be a choice to be
        // wrong by a known amount, and because including it makes this an exact reimplementation of astropy's
        // own FK4NoETerms chain rather than an approximation of it.
        private static readonly double[] Fk4RotationPerCentury =
        {
            -0.0026455262e-6, -1.1539918689e-6, +2.1111346190e-6,
            +1.1540628161e-6, -0.0129042997e-6, +0.0236021478e-6,
            -2.1112979048e-6, -0.0056024448e-6, +0.0102587734e-6,
        };

        /// <summary>Equinox of the IAU constellation boundaries, Delporte (1930).</summary>
        public const double ConstellationBoundaryEpoch = 1875.0;

        /// <summary>The standard Besselian epoch the FK4/FK5 relation above is defined at.</summary>
        public const double Fk4StandardEpoch = 1950.0;

        /// <summary>
        /// Newcomb's precession between two Besselian epochs, as a row-major 3x3 rotation matrix
        /// acting on equatorial direction cosines.
        ///
        /// The three Euler angles are the classical zeta, z and theta, with the coefficients of
        /// the Explanatory Supplement (1992); the same expressions astropy's
        /// earth_orientation._precession_matrix_besselian evaluates.
        /// </summary>
        public static double[] NewcombPrecession(double epochFrom, double epochTo)
        {
            // Tropical centuries... in units of a thousand tropical years, which is the unit
            // Newcomb's coefficients are tabulated in.
            double t1 = (epochFrom - 1850.0) / 1000.0;
            double dt = (epochTo - 1850.0) / 1000.0 - t1;

            double common = 23035.545 + t1 * (139.720 + 0.060 * t1);
            double zetaArcsec = dt * (common + dt * ((30.240 - 0.27 * t1) + dt * 17.995));
            double zArcsec = dt * (common + dt * ((109.480 + 0.39 * t1) + dt * 18.325));
            double thetaArcsec = dt * ((20051.12 - t1 * (85.29 + 0.37 * t1))
                                       + dt * ((-42.65 - 0.37 * t1) + dt * -41.8));

            double zeta = zetaArcsec / 3600.0 * DegToRad;
            double z = zArcsec / 3600.0 * DegToRad;
            double theta = thetaArcsec / 3600.0 * DegToRad;

            // R_z(-z) . R_y(theta) . R_z(-zeta), in the convention where R_z(a) rotates the frame
            // (not the vector) about z by a.
            return Multiply(RotationZ(-z), Multiply(RotationY(theta), RotationZ(-zeta)));
        }

        /// <summary>
        /// A J2000 equatorial position (FK5, and ICRS to well within the resolution of anything
        /// this is used for) expressed in the mean equinox of the given Besselian epoch.
        /// </summary>
        public static void J2000ToBesselian(double raDegJ2000, double decDegJ2000, double besselianEpoch,
                                            out double raDegOut, out double decDegOut)
        {
            double ra = raDegJ2000 * DegToRad;
            double dec = decDegJ2000 * DegToRad;
            double cosDec = Math.Cos(dec);

            double x = cosDec * Math.Cos(ra);
            double y = cosDec * Math.Sin(ra);
            double z = Math.Sin(dec);

            // The FK4 coordinates being produced belong to the target equinox, so that is the
            // epoch Murray's rotating-system correction is evaluated at; it is expressed in Julian
            // centuries from 1950, hence the Besselian-to-Julian epoch conversion.
            double centuries = (BesselianEpochToJulianYear(besselianEpoch) - 1950.0) / 100.0;
            var b = new double[9];
            for (int i = 0; i < 9; i++) b[i] = B1950ToJ2000[i] + Fk4RotationPerCentury[i] * centuries;

            // J2000 -> B1950 is the transpose of the B1950 -> J2000 rotation.
            double bx = b[0] * x + b[3] * y + b[6] * z;
            double by = b[1] * x + b[4] * y + b[7] * z;
            double bz = b[2] * x + b[5] * y + b[8] * z;

            double[] precession = NewcombPrecession(Fk4StandardEpoch, besselianEpoch);
            double px = precession[0] * bx + precession[1] * by + precession[2] * bz;
            double py = precession[3] * bx + precession[4] * by + precession[5] * bz;
            double pz = precession[6] * bx + precession[7] * by + precession[8] * bz;

            double length = Math.Sqrt(px * px + py * py + pz * pz);
            if (!(length > 0.0)) { raDegOut = raDegJ2000; decDegOut = decDegJ2000; return; }

            raDegOut = Math.Atan2(py, px) * RadToDeg;
            if (raDegOut < 0.0) raDegOut += 360.0;
            decDegOut = Math.Asin(Math.Max(-1.0, Math.Min(1.0, pz / length))) * RadToDeg;
        }

        /// <summary>
        /// The Julian year a Besselian epoch falls on. A Besselian year is the tropical year of
        /// 365.242198781 days counted from B1900.0 = JD 2415020.31352; a Julian year is 365.25 days
        /// counted from J2000.0 = JD 2451545.0. The two differ by about half a day per century,
        /// which is why B1950.0 is 1949.99979 in Julian years and not 1950.
        /// </summary>
        public static double BesselianEpochToJulianYear(double besselianEpoch)
        {
            double jd = 2415020.31352 + (besselianEpoch - 1900.0) * 365.242198781;
            return 2000.0 + (jd - 2451545.0) / 365.25;
        }

        private static double[] RotationZ(double angleRad)
        {
            double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
            return new[] { c, s, 0.0, -s, c, 0.0, 0.0, 0.0, 1.0 };
        }

        private static double[] RotationY(double angleRad)
        {
            double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
            return new[] { c, 0.0, -s, 0.0, 1.0, 0.0, s, 0.0, c };
        }

        private static double[] Multiply(double[] a, double[] b)
        {
            var m = new double[9];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    m[row * 3 + col] = a[row * 3] * b[col]
                                     + a[row * 3 + 1] * b[3 + col]
                                     + a[row * 3 + 2] * b[6 + col];
            return m;
        }
    }
}
