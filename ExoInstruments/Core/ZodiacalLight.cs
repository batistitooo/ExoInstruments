using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Zodiacal light as a function of where on the sky you point: sunlight scattered by
    /// interplanetary dust, in V magnitudes per square arcsecond.
    ///
    /// WHAT THIS REPLACES, AND WHY IT MATTERS MORE IN ORBIT. SkyBrightnessModel carried the
    /// zodiacal light as ONE number, 23.3 mag/arcsec^2, Leinert et al. (1998)'s value at the
    /// ecliptic pole, and said so: the cloud's angular distribution is a Solar System
    /// measurement and the game supplies no counterpart to read it from. On the ground that
    /// choice was nearly free, because airglow at 21.7 mag/arcsec^2 is four times brighter and
    /// swamps the difference. Above the atmosphere there is no airglow, and the zodiacal light
    /// is not one term among several but very nearly the entire sky background. Holding it at
    /// its faintest possible value there understates the real sky by up to 2.4 magnitudes at the
    /// smallest elongation a telescope can legally observe at, a factor of nine in flux and a
    /// factor of three in the noise it contributes, on exactly the faint targets a space
    /// telescope exists to reach.
    ///
    /// THE SOURCE IS THE PRIMARY ONE. Table 16 of Leinert, Ch. et al. (1998), "The 1997
    /// reference of diffuse night sky brightness", A&amp;AS 127, 1: "Zodiacal light brightness
    /// observed from the Earth (in S10sun) at 500 nm", on a grid of helioecliptic longitude
    /// (lambda - lambda_sun) against ecliptic latitude beta, reproduced below unchanged. It is
    /// the reference table the field uses, it is an update of Levasseur-Regourd &amp; Dumont
    /// (1980), and the paper states its own interpolation rule: "Intermediate values may be
    /// obtained by smooth interpolations, although small scale irregularities (e.g. cometary
    /// trails) cannot be taken into account." Bilinear interpolation is that rule; nothing here
    /// is fitted, smoothed or extended.
    ///
    /// WHY LEINERT AND NOT THE WFC3 HANDBOOK'S TABLE 9.4, which covers the same quantity. They
    /// are the same measurement: converting Leinert's S10sun values through the unit conversion
    /// below reproduces Table 9.4 cell for cell (22.02 against 22.0 at (90, 0), 21.29 against
    /// 21.3 at (60, 0), 23.34 against 23.3 at the pole). But STScI's table stops at HST's own
    /// 50 degree solar avoidance limit and marks everything inside it "SA", while Leinert
    /// measures all the way in to 15 degrees elongation. Taking the primary source therefore
    /// removes the extrapolation the handbook's table would have forced in the one corner where
    /// the zodiacal light is brightest and matters most. tools/spacecraft-tests asserts the
    /// agreement between the two, which is a real check on the transcription and on the unit
    /// conversion at once.
    ///
    /// WHAT IT IS STILL AN APPROXIMATION OF. The dust cloud this measures is the Solar System's.
    /// A stock KSP system has no modelled dust at all, and no planet pack ships one, so applying
    /// the real cloud to whatever system is loaded assumes that system's zodiacal light looks
    /// like ours. That assumption was already being made when the value was a constant; what
    /// changes here is only that its SHAPE is now the measured one instead of flat. The
    /// ecliptic plane itself is not assumed: it is read from the home body's own orbit (see
    /// EclipticFrame), which is the closest thing any KSP system has to one and is exact for a
    /// system whose planets are coplanar.
    ///
    /// Pure C# with no Unity dependency, like the rest of Core.
    /// </summary>
    public static class ZodiacalLight
    {
        // Helioecliptic longitudes tabulated, degrees: the target's ecliptic longitude minus the Sun's, so 0 is
        // toward the Sun and 180 is the anti-solar point. Leinert Table 16's own row headings, unevenly spaced
        // as published (5 degree steps near the Sun where the gradient is steep, 15 degree steps beyond 45).
        private static readonly double[] LongitudeDeg =
        {
            0.0, 5.0, 10.0, 15.0, 20.0, 25.0, 30.0, 35.0, 40.0, 45.0,
            60.0, 75.0, 90.0, 105.0, 120.0, 135.0, 150.0, 165.0, 180.0,
        };

        // Ecliptic latitudes tabulated, degrees: Leinert Table 16's own column headings, plus the pole. The
        // table is symmetric about the ecliptic, so only one hemisphere is given. The 90 degree column is not
        // an extrapolation: the table's own caption supplies it, "Towards the ecliptic pole, the brightness as
        // given above is 60 +/- 3 S10sun".
        private static readonly double[] LatitudeDeg = { 0.0, 5.0, 10.0, 15.0, 20.0, 25.0, 30.0, 45.0, 60.0, 75.0, 90.0 };

        // Leinert Table 16's own pole value, S10sun. Quoted in its caption rather than in the grid.
        private const double PoleS10 = 60.0;

        // Leinert et al. (1998) Table 16, in S10sun at 500 nm, indexed [longitude][latitude]. NaN marks the
        // cells the table leaves blank, which are exactly those inside 15 degrees of the Sun: the paper states
        // it completes the earlier table "in the solar vicinity, up to 15 degrees solar elongation", and
        // cos(elongation) = cos(lambda - lambda_sun) cos(beta) puts every blank cell below that limit and every
        // filled one above it. That is a real boundary of the measurement, not a gap in the transcription, and
        // it is left as NaN. Transcribed verbatim; do not smooth it.
        private static readonly double[][] TableS10 =
        {
            //        beta=0      5       10      15      20      25      30      45     60     75    90
            new[] { double.NaN, double.NaN, double.NaN, 2450.0, 1260.0, 770.0, 500.0, 215.0, 117.0, 78.0, PoleS10 }, // 0
            new[] { double.NaN, double.NaN, double.NaN, 2300.0, 1200.0, 740.0, 490.0, 212.0, 117.0, 78.0, PoleS10 }, // 5
            new[] { double.NaN, double.NaN, 3700.0,     1930.0, 1070.0, 675.0, 460.0, 206.0, 116.0, 78.0, PoleS10 }, // 10
            new[] { 9000.0,     5300.0,     2690.0,     1450.0,  870.0, 590.0, 410.0, 196.0, 114.0, 78.0, PoleS10 }, // 15
            new[] { 5000.0,     3500.0,     1880.0,     1100.0,  710.0, 495.0, 355.0, 185.0, 110.0, 77.0, PoleS10 }, // 20
            new[] { 3000.0,     2210.0,     1350.0,      860.0,  585.0, 425.0, 320.0, 174.0, 106.0, 76.0, PoleS10 }, // 25
            new[] { 1940.0,     1460.0,      955.0,      660.0,  480.0, 365.0, 285.0, 162.0, 102.0, 74.0, PoleS10 }, // 30
            new[] { 1290.0,      990.0,      710.0,      530.0,  400.0, 310.0, 250.0, 151.0,  98.0, 73.0, PoleS10 }, // 35
            new[] {  925.0,      735.0,      545.0,      415.0,  325.0, 264.0, 220.0, 140.0,  94.0, 72.0, PoleS10 }, // 40
            new[] {  710.0,      570.0,      435.0,      345.0,  278.0, 228.0, 195.0, 130.0,  91.0, 70.0, PoleS10 }, // 45
            new[] {  395.0,      345.0,      275.0,      228.0,  190.0, 163.0, 143.0, 105.0,  81.0, 67.0, PoleS10 }, // 60
            new[] {  264.0,      248.0,      210.0,      177.0,  153.0, 134.0, 118.0,  91.0,  73.0, 64.0, PoleS10 }, // 75
            new[] {  202.0,      196.0,      176.0,      151.0,  130.0, 115.0, 103.0,  81.0,  67.0, 62.0, PoleS10 }, // 90
            new[] {  166.0,      164.0,      154.0,      133.0,  117.0, 104.0,  93.0,  75.0,  64.0, 60.0, PoleS10 }, // 105
            new[] {  147.0,      145.0,      138.0,      120.0,  108.0,  98.0,  88.0,  70.0,  60.0, 58.0, PoleS10 }, // 120
            new[] {  140.0,      139.0,      130.0,      115.0,  105.0,  95.0,  86.0,  70.0,  60.0, 57.0, PoleS10 }, // 135
            new[] {  140.0,      139.0,      129.0,      116.0,  107.0,  99.0,  91.0,  75.0,  62.0, 56.0, PoleS10 }, // 150
            new[] {  153.0,      150.0,      140.0,      129.0,  118.0, 110.0, 102.0,  81.0,  64.0, 56.0, PoleS10 }, // 165
            new[] {  180.0,      166.0,      152.0,      139.0,  127.0, 116.0, 105.0,  82.0,  65.0, 56.0, PoleS10 }, // 180
        };

        /// <summary>
        /// Surface brightness of one S10sun, in V magnitudes per square arcsecond: 27.78.
        ///
        /// THE UNIT'S DEFINITION IS THE DERIVATION. One S10sun is the surface brightness of a
        /// single 10th-magnitude solar-type star spread over one square degree. A square degree
        /// is 3600^2 = 1.296e7 square arcseconds, and spreading a fixed flux over N times the
        /// area costs 2.5 log10(N) magnitudes, so
        ///
        ///     m(1 S10sun) = 10 + 2.5 log10(3600^2) = 10 + 17.7815 = 27.7815 mag/arcsec^2
        ///
        /// and a brightness of N S10sun is m = 27.7815 - 2.5 log10(N).
        ///
        /// WHY NO COLOUR TERM IS NEEDED, which is the one thing that could go wrong here. The
        /// unit is defined against a SOLAR-TYPE star, and the zodiacal light is scattered
        /// sunlight, so the ratio the table records is between two objects of nearly the same
        /// spectrum and is therefore nearly wavelength-independent. That is also why the table's
        /// 500 nm reference wavelength and the V band's 551 nm effective wavelength do not
        /// require a correction between them: what is being converted is a ratio, not a flux.
        /// The pipeline then integrates this surface brightness with the solar spectral shape
        /// (SourceSpectra.SolarPhotosphereTemperatureK), the same treatment every other
        /// scattered-sunlight term in the sky model already gets.
        ///
        /// The residual is the zodiacal light's own slight reddening relative to the Sun, which
        /// Leinert et al. document in their Sect. 8.4 and Fig. 39. It is not modelled, and is
        /// recorded in section 12.
        /// </summary>
        public static readonly double S10SunVMagPerArcsec2 = 10.0 + 2.5 * Math.Log10(3600.0 * 3600.0);

        /// <summary>
        /// Elongation the table is described as reaching, degrees: the paper states it completes
        /// the earlier Levasseur-Regourd &amp; Dumont table "in the solar vicinity, up to 15 degrees
        /// solar elongation".
        ///
        /// The grid's actual inner edge is a little inside that, because the boundary falls
        /// between cells rather than on a contour: the innermost cell with a value is
        /// (lambda - lambda_sun, beta) = (10, 10) at 14.1 degrees elongation, and the outermost
        /// blank one is (10, 5) at 11.2. So every blank is inside 15 degrees and every measured
        /// cell is outside 11, which is what tools/spacecraft-tests asserts rather than a sharp
        /// cut this constant does not really represent.
        ///
        /// The region is unreachable in practice rather than merely unmeasured. The smallest
        /// solar avoidance angle in this mod's whole instrument roster is HST's 62.5 degrees, so
        /// nothing here may point within four times this limit of the Sun. That is why the
        /// fallback below is a clamp to the nearest measured value rather than an extrapolation:
        /// nothing needs a value there, and inventing one would be inventing a number no
        /// telescope can check.
        /// </summary>
        public const double MinimumMeasuredElongationDeg = 15.0;

        /// <summary>
        /// The faintest tabulated value: 56 S10sun, i.e. 23.41 V mag/arcsec^2, reached at
        /// (150-180, 75).
        ///
        /// NOT at the ecliptic pole, which is 60 S10sun and therefore slightly BRIGHTER. That is
        /// the measurement's own shape and not a transcription slip: the zodiacal cloud's minimum
        /// sits a little off the pole, on the anti-solar side at high latitude. The old constant
        /// this table replaced was the pole value, so it was not even the darkest sky available.
        /// </summary>
        public static readonly double MinimumVMagPerArcsec2 = S10ToVMagPerArcsec2(56.0);

        /// <summary>The ecliptic pole's own value, 60 S10sun: the constant SkyBrightnessModel carried before this table existed.</summary>
        public static readonly double EclipticPoleVMagPerArcsec2 = S10ToVMagPerArcsec2(PoleS10);

        /// <summary>The brightest tabulated value, at 15 degrees elongation along the ecliptic: 9000 S10sun, i.e. 17.9 V mag/arcsec^2.</summary>
        public static readonly double MaximumVMagPerArcsec2 = S10ToVMagPerArcsec2(9000.0);

        /// <summary>One S10sun brightness as a V surface brightness. See S10SunVMagPerArcsec2 for the derivation.</summary>
        public static double S10ToVMagPerArcsec2(double s10)
        {
            if (!(s10 > 0.0)) return double.PositiveInfinity;
            return S10SunVMagPerArcsec2 - 2.5 * Math.Log10(s10);
        }

        /// <summary>
        /// Elongation from the Sun of a point at the given helioecliptic longitude and ecliptic
        /// latitude, degrees, from the spherical law of cosines with the Sun on the ecliptic:
        ///
        ///     cos(elongation) = cos(lambda - lambda_sun) cos(beta)
        ///
        /// This is what decides whether the table has a value at all.
        /// </summary>
        public static double ElongationDeg(double heliocentricLongitudeDeg, double eclipticLatitudeDeg)
        {
            double lam = heliocentricLongitudeDeg * Math.PI / 180.0;
            double beta = eclipticLatitudeDeg * Math.PI / 180.0;
            double c = Math.Cos(lam) * Math.Cos(beta);
            if (c > 1.0) c = 1.0; else if (c < -1.0) c = -1.0;
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Brightness at the given helioecliptic longitude and ecliptic latitude, in S10sun at
        /// 500 nm: the table's own unit, bilinearly interpolated as the paper's caption
        /// sanctions.
        ///
        /// <paramref name="isMeasured"/> comes back false only inside 15 degrees elongation,
        /// where Leinert Table 16 stops; see MinimumMeasuredElongationDeg for why nothing in
        /// this mod can point there.
        /// </summary>
        public static double S10(double heliocentricLongitudeDeg, double eclipticLatitudeDeg,
                                 out bool isMeasured)
        {
            double lambda = FoldLongitude(heliocentricLongitudeDeg);
            double beta = Math.Min(Math.Abs(eclipticLatitudeDeg), 90.0);

            FindBracket(LongitudeDeg, lambda, out int i0, out int i1, out double fi);
            FindBracket(LatitudeDeg, beta, out int j0, out int j1, out double fj);

            double v00 = TableS10[i0][j0], v01 = TableS10[i0][j1];
            double v10 = TableS10[i1][j0], v11 = TableS10[i1][j1];

            if (!double.IsNaN(v00) && !double.IsNaN(v01) && !double.IsNaN(v10) && !double.IsNaN(v11))
            {
                isMeasured = true;
                double a = v00 + (v01 - v00) * fj;
                double b = v10 + (v11 - v10) * fj;
                return a + (b - a) * fi;
            }

            isMeasured = false;
            return ClampToNearestMeasured(j0, j1, fj);
        }

        /// <summary>
        /// Surface brightness at the given helioecliptic longitude and ecliptic latitude,
        /// V mag/arcsec^2 (larger is fainter).
        /// </summary>
        public static double VMagPerArcsec2(double heliocentricLongitudeDeg, double eclipticLatitudeDeg,
                                            out bool isMeasured)
        {
            return S10ToVMagPerArcsec2(S10(heliocentricLongitudeDeg, eclipticLatitudeDeg, out isMeasured));
        }

        /// <summary>Same, for callers that do not need to know whether the value was measured.</summary>
        public static double VMagPerArcsec2(double heliocentricLongitudeDeg, double eclipticLatitudeDeg)
        {
            return VMagPerArcsec2(heliocentricLongitudeDeg, eclipticLatitudeDeg, out _);
        }

        // Inside 15 degrees elongation, where the table stops: the brightest MEASURED value at this latitude,
        // held. A clamp and not an extrapolation, deliberately. The zodiacal light does keep rising toward the
        // Sun, so this understates it, and that is the safe direction to be wrong in a region no instrument in
        // the roster may point at: the smallest solar avoidance angle here is 62.5 degrees. Extrapolating a
        // steep power law past the end of the data to serve a pointing that cannot happen would be inventing a
        // number for its own sake.
        private static double ClampToNearestMeasured(int j0, int j1, double fj)
        {
            for (int i = 0; i < TableS10.Length; i++)
            {
                double a = TableS10[i][j0], b = TableS10[i][j1];
                if (double.IsNaN(a) || double.IsNaN(b)) continue;
                return a + (b - a) * fj;
            }
            return PoleS10;
        }

        // Helioecliptic longitude folded onto 0-180: the cloud is symmetric about the Sun-anti-Sun line, which
        // is why the table only tabulates half of it.
        private static double FoldLongitude(double deg)
        {
            double d = deg % 360.0;
            if (d < 0.0) d += 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        private static void FindBracket(double[] axis, double v, out int i0, out int i1, out double f)
        {
            if (v <= axis[0]) { i0 = 0; i1 = 0; f = 0.0; return; }
            if (v >= axis[axis.Length - 1]) { i0 = i1 = axis.Length - 1; f = 0.0; return; }
            for (int i = 0; i < axis.Length - 1; i++)
            {
                if (v <= axis[i + 1])
                {
                    i0 = i; i1 = i + 1;
                    f = (v - axis[i]) / (axis[i + 1] - axis[i]);
                    return;
                }
            }
            i0 = i1 = axis.Length - 1; f = 0.0;
        }
    }
}
