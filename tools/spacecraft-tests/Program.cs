using System;
using System.Globalization;
using ExoInstruments.Core;

// Headless cross-validation of the orbital half of the pipeline, against the published figures
// the code claims to reproduce. Nothing here checks that the code does what the code says; every
// assertion is against a number from STScI, from an instrument science report, or from a
// self-consistency identity between two independently published quantities.
//
// Run from this directory with:
//   dotnet run -c Release -p:Core=../../ExoInstruments/Core

internal static class Program
{
    private static int failures;
    private static int checks;

    private static void Main()
    {
        // The harness prints numbers, and a machine with a comma decimal separator makes them
        // unreadable next to the published figures they are being compared with.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        Section("1. HST optics: published identities");
        HstOpticalIdentities();

        Section("2. Orbital visibility geometry against the HST Primer");
        OrbitalGeometry();

        Section("3. Zodiacal light (Leinert et al. 1998 Table 16, vs WFC3 IHB Table 9.4)");
        Zodiacal();

        Section("4. Earth-shine (SRW98 + WFC3 IHB Table 9.3)");
        EarthshineChecks();

        Section("5. Delivered PSF against WFC3 IHB Table 6.7");
        DeliveredPsf();

        Section("6. Pupil with mirror pads");
        PupilPads();

        Section("7. Pointing stability and the thruster limit cycle");
        Pointing();

        Section("8. Aperture sampling");
        ApertureSampling();

        Section("9. Frame volume and downlink");
        Telemetry();

        Section("10. Cosmic-ray rate in orbit (WFC3 IHB 5.4.10)");
        CosmicRays();

        Console.WriteLine();
        Console.WriteLine($"{checks - failures}/{checks} checks passed.");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // ------------------------------------------------------------------ 1

    private const double HstApertureMeters = 2.4;
    private const double HstObstruction = 0.330;          // Tiny Tim wfc3_uvis1.pup
    private const double HstVaneWidthMeters = 0.022 * 1.2; // 0.022 of the pupil radius
    private const int HstVaneCount = 4;
    private const double UvisPixelMeters = 15.0e-6;
    private const double UvisPlateScaleArcsec = 0.0396;

    private static readonly PupilPad[] HstPads =
    {
        new PupilPad(0.8921, 0.0000, 0.065),
        new PupilPad(-0.4615, 0.7555, 0.065),
        new PupilPad(-0.4564, -0.7606, 0.065),
    };

    private static void HstOpticalIdentities()
    {
        // The HST Primer's optics table gives the focal ratio (f/24), the aperture (2.4 m) AND
        // the on-axis plate scale (3.58 arcsec/mm) as three separate figures. They are not
        // independent: 206265 / (2.4 * 24 * 1000) mm has to be 3.58. If it is not, one of the
        // three has been transcribed wrong.
        double otaFocalMm = HstApertureMeters * 24.0 * 1000.0;
        double otaPlateScale = 206265.0 / otaFocalMm;
        Near("OTA plate scale from f/24 and 2.4 m", otaPlateScale, 3.58, 0.005, "arcsec/mm");

        // The instrument's own effective focal length, derived the way the catalogue derives it,
        // must return the published plate scale through this pipeline's own relation.
        double effectiveFocalM = 206265.0 * UvisPixelMeters / UvisPlateScaleArcsec;
        double plateScale = UvisPixelMeters / effectiveFocalM * (180.0 / Math.PI) * 3600.0;
        Near("WFC3/UVIS plate scale round trip", plateScale, UvisPlateScaleArcsec, 1e-6, "arcsec/px");

        // And it must NOT be the OTA's own f/24, which is the mistake the catalogue comment
        // exists to prevent: at f/24 the plate scale would be a third too coarse.
        double wrongPlateScale = UvisPixelMeters / (HstApertureMeters * 24.0) * (180.0 / Math.PI) * 3600.0;
        Check("f/24 would NOT give the published plate scale",
              Math.Abs(wrongPlateScale - UvisPlateScaleArcsec) > 0.01,
              $"f/24 gives {wrongPlateScale:F4} arcsec/px against the published {UvisPlateScaleArcsec:F4}");

        // Diffraction alone. The Primer quotes the OTA's PSF FWHM at 5000 A as 0.043 arcsec,
        // which is the pure aperture figure before the instrument's own optics; this is the one
        // number in the set that the aperture and the obstruction have to produce on their own.
        double airy = OpticalPsf.AiryFwhmArcsec(HstApertureMeters, HstObstruction, 500e-9);
        Near("Airy FWHM at 500 nm vs Primer's 0.043\"", airy, 0.043, 0.004, "arcsec");
    }

    // ------------------------------------------------------------------ 2

    private static void OrbitalGeometry()
    {
        // HST Primer: Orbital Constraints. "HST is in a relatively low orbit (500 km above
        // Earth) ... most targets are occulted by the Earth for varying lengths of time during
        // each 96-minute orbit. Targets lying in the orbital plane are occulted for the longest
        // interval, about 44 minutes per orbit."
        const double earthRadiusKm = 6371.0;
        const double altitudeKm = 500.0;
        const double periodMinutes = 96.0;
        double r = earthRadiusKm + altitudeKm;

        double rho = OrbitalVisibility.AngularRadiusDeg(earthRadiusKm, r);
        Near("Earth's angular radius from 500 km", rho, 68.0, 0.5, "deg");

        // Pure geometric occultation of an in-plane target.
        double geometric = OrbitalVisibility.OccultedOrbitFraction(rho, 0.0) * periodMinutes;
        Near("geometric occultation, in-plane target", geometric, 36.3, 0.5, "min");

        // The Primer's 44 minutes is an OPERATIONAL figure: an exposure ends when the pointing
        // enters the bright-limb avoidance zone, not when the target finally goes behind the
        // disk. Adding the bright Earth avoidance angle in force under reduced-gyro operations
        // (15.5 deg) reproduces it. That the two figures differ by exactly the avoidance angle
        // is the check: it says the model and STScI are measuring the same thing.
        double operational = OrbitalVisibility.OccultedOrbitFraction(rho + 15.5, 0.0) * periodMinutes;
        Near("occultation incl. 15.5 deg bright-limb avoidance vs Primer's ~44 min",
             operational, 44.0, 1.0, "min");

        // The handbook's standard 20 deg BEA, for reference: a longer cut still.
        double conservative = OrbitalVisibility.OccultedOrbitFraction(rho + 20.0, 0.0) * periodMinutes;
        Check("20 deg avoidance costs more than 15.5 deg", conservative > operational,
              $"{conservative:F1} min vs {operational:F1} min");

        // Continuous viewing zone. The Primer says "Targets lying within 24 degrees of the
        // orbital poles are not geometrically occulted at any time".
        double cvz = OrbitalVisibility.ContinuousViewingHalfWidthDeg(rho);
        Near("CVZ half-width at the stated 500 km", cvz, 22.0, 0.5, "deg");

        // The Primer's own two figures are not quite consistent with each other, and it is worth
        // recording which one this reproduces. 24 degrees corresponds to a higher orbit than the
        // 500 km the same document states; HST flew near 610 km after its first servicing
        // missions and has decayed since, so the two numbers are from different epochs.
        double altitudeFor24 = earthRadiusKm / Math.Sin(66.0 * Math.PI / 180.0) - earthRadiusKm;
        Console.WriteLine($"    note: a 24 deg CVZ implies an altitude of {altitudeFor24:F0} km, "
                        + "not the 500 km quoted alongside it in the same document.");

        // A target on the orbit pole is never occulted, whatever the avoidance angle, as long as
        // the avoidance angle leaves the pole outside the blocked cone.
        Near("polar target is never occulted",
             OrbitalVisibility.OccultedOrbitFraction(rho, 90.0), 0.0, 1e-12, "fraction");

        // Occultation must grow monotonically as the target approaches the orbital plane.
        double prev = -1.0;
        bool monotone = true;
        for (double beta = 90.0; beta >= 0.0; beta -= 5.0)
        {
            double f = OrbitalVisibility.OccultedOrbitFraction(rho, beta);
            if (f < prev - 1e-12) monotone = false;
            prev = f;
        }
        Check("occulted fraction is monotonic in target elevation", monotone, "");

        // Limb geometry: a line of sight straight down at the body centre is occulted, one at
        // 90 degrees from it is not, and the limb angle is the separation minus the radius.
        var observer = new SkyVector(0, 0, r);
        var sunFromBody = new SkyVector(1, 0, 0);

        LimbGeometry down = OrbitalVisibility.EvaluateLimb(observer, new SkyVector(0, 0, -1),
                                                           earthRadiusKm, sunFromBody);
        Check("nadir pointing is occulted", down.Occulted, $"limb angle {down.LimbAngleDeg:F2} deg");

        LimbGeometry up = OrbitalVisibility.EvaluateLimb(observer, new SkyVector(0, 0, 1),
                                                          earthRadiusKm, sunFromBody);
        Check("zenith pointing is not occulted", !up.Occulted, $"limb angle {up.LimbAngleDeg:F2} deg");
        Near("zenith limb angle", up.LimbAngleDeg, 180.0 - rho, 0.01, "deg");

        // The Sun is at +X, so the limb on the +X side is lit and the one on -X is dark. A sight
        // line leaning toward +X must find a sunlit limb, and one leaning toward -X a dark one.
        var towardLit = SkyVector.Normalized(1, 0, -0.2);
        var towardDark = SkyVector.Normalized(-1, 0, -0.2);
        LimbGeometry lit = OrbitalVisibility.EvaluateLimb(observer, towardLit, earthRadiusKm, sunFromBody);
        LimbGeometry dark = OrbitalVisibility.EvaluateLimb(observer, towardDark, earthRadiusKm, sunFromBody);
        Check("limb toward the Sun reads as sunlit", lit.LimbIsSunlit, "");
        Check("limb away from the Sun reads as dark", !dark.LimbIsSunlit, "");
    }

    // ------------------------------------------------------------------ 3

    /// <summary>
    /// WFC3 IHB Table 9.4, in V mag/arcsec^2: STScI's own version of the same measurement, on
    /// their coarser 15-degree longitude grid, with "SA" cells inside HST's 50 degree solar
    /// avoidance limit left as NaN. Indexed [longitude 0..180 step 15][latitude 0,15,30,45,60,75,90].
    /// </summary>
    private static readonly double[][] Wfc3Table94 =
    {
        new[] { double.NaN, double.NaN, double.NaN, double.NaN, 22.6, 23.0, 23.3 },
        new[] { double.NaN, double.NaN, double.NaN, double.NaN, 22.6, 23.1, 23.3 },
        new[] { double.NaN, double.NaN, double.NaN, 22.3,       22.7, 23.1, 23.3 },
        new[] { double.NaN, double.NaN, 22.1,       22.5,       22.9, 23.1, 23.3 },
        new[] { 21.3,       21.9,       22.4,       22.7,       23.0, 23.2, 23.3 },
        new[] { 21.7,       22.2,       22.6,       22.9,       23.1, 23.2, 23.3 },
        new[] { 22.0,       22.3,       22.7,       23.0,       23.2, 23.3, 23.3 },
        new[] { 22.2,       22.5,       22.9,       23.1,       23.3, 23.3, 23.3 },
        new[] { 22.4,       22.6,       22.9,       23.2,       23.3, 23.3, 23.3 },
        new[] { 22.4,       22.6,       22.9,       23.2,       23.3, 23.4, 23.3 },
        new[] { 22.4,       22.6,       22.9,       23.1,       23.3, 23.4, 23.3 },
        new[] { 22.3,       22.5,       22.8,       23.0,       23.2, 23.4, 23.3 },
        new[] { 22.1,       22.4,       22.7,       23.0,       23.2, 23.4, 23.3 },
    };

    private static void Zodiacal()
    {
        // THE UNIT CONVERSION, checked against its own definition. One S10sun is a 10th-magnitude
        // solar-type star spread over a square degree, so its surface brightness must be
        // 10 + 2.5 log10(3600^2).
        Near("S10sun zero point", ZodiacalLight.S10SunVMagPerArcsec2,
             10.0 + 2.5 * Math.Log10(3600.0 * 3600.0), 1e-9, "mag/arcsec^2");

        // Leinert's caption gives the ecliptic pole as 60 S10sun. Through that conversion it has
        // to land on the 23.3 the same paper quotes as the pole's V surface brightness elsewhere,
        // and on the value SkyBrightnessModel carried as a constant before this table existed.
        Near("ecliptic pole", ZodiacalLight.VMagPerArcsec2(180.0, 90.0), 23.34, 0.01, "mag/arcsec^2");

        // Grid points come back exactly: the table is used as published, and an interpolation
        // that does not reproduce its own nodes has a bug in the bracketing.
        Near("Table 16 at (90, 0)", ZodiacalLight.S10(90.0, 0.0, out _), 202.0, 1e-9, "S10sun");
        Near("Table 16 at (15, 0)", ZodiacalLight.S10(15.0, 0.0, out _), 9000.0, 1e-9, "S10sun");
        Near("Table 16 at (45, 30)", ZodiacalLight.S10(45.0, 30.0, out _), 195.0, 1e-9, "S10sun");
        Near("Table 16 at (180, 75)", ZodiacalLight.S10(180.0, 75.0, out _), 56.0, 1e-9, "S10sun");

        // THE CROSS-CHECK THAT MATTERS. STScI's Table 9.4 and Leinert's Table 16 are the same
        // measurement published in two different units by two different groups. Converting
        // Leinert's S10sun through the derivation above has to reproduce STScI's magnitudes
        // wherever both publish a value. Agreement to STScI's own rounding (0.1 mag) is a check
        // on the transcription of two tables and on the unit conversion between them at once.
        double worst = 0.0;
        int compared = 0;
        string worstAt = "";
        for (int i = 0; i < Wfc3Table94.Length; i++)
        {
            double lon = i * 15.0;
            for (int j = 0; j < 7; j++)
            {
                double lat = j == 0 ? 0.0 : (j <= 4 ? j * 15.0 : (j == 5 ? 75.0 : 90.0));
                lat = new[] { 0.0, 15.0, 30.0, 45.0, 60.0, 75.0, 90.0 }[j];
                double expected = Wfc3Table94[i][j];
                if (double.IsNaN(expected)) continue;
                double actual = ZodiacalLight.VMagPerArcsec2(lon, lat, out bool measured);
                if (!measured) continue;
                compared++;
                double d = Math.Abs(actual - expected);
                if (d > worst) { worst = d; worstAt = $"({lon:F0}, {lat:F0})"; }
            }
        }
        Console.WriteLine($"    compared {compared} cells against WFC3 IHB Table 9.4");
        // STScI publish to 0.1 mag, so one rounding unit is the tightest agreement that can be
        // asked for; the worst cell lands well inside it.
        Check("Leinert Table 16 converts onto WFC3 Table 9.4 within STScI's own rounding",
              worst <= 0.1, $"worst discrepancy {worst:F3} mag at {worstAt}");

        // Leinert covers the corner STScI marks "SA": at 20 degrees elongation on the ecliptic
        // there is a real measurement, where the handbook's table has nothing. This is the whole
        // reason for preferring the primary source.
        double sa = ZodiacalLight.VMagPerArcsec2(20.0, 0.0, out bool saMeasured);
        Check("the primary source measures inside HST's solar avoidance zone", saMeasured,
              $"(20, 0) = {sa:F2} mag/arcsec^2, where Table 9.4 has \"SA\"");

        // And it stops where the measurement stops, at 15 degrees elongation, reported honestly.
        ZodiacalLight.VMagPerArcsec2(5.0, 0.0, out bool tooClose);
        Check("inside 15 deg elongation is reported as unmeasured", !tooClose, "");
        Near("the elongation relation", ZodiacalLight.ElongationDeg(0.0, 15.0), 15.0, 1e-9, "deg");
        Near("elongation combines both angles: acos(cos10 cos10)",
             ZodiacalLight.ElongationDeg(10.0, 10.0),
             Math.Acos(Math.Cos(10.0 * Math.PI / 180.0) * Math.Cos(10.0 * Math.PI / 180.0)) * 180.0 / Math.PI,
             1e-9, "deg");

        // The grid's inner boundary falls BETWEEN cells rather than on an elongation contour, so
        // the honest assertion is a bracket rather than a sharp cut: every blank cell is inside
        // the 15 degrees the paper describes, and every measured cell is outside 11.
        double innermostMeasured = 999.0, outermostBlank = 0.0;
        foreach (double lon in new[] { 0.0, 5.0, 10.0, 15.0, 20.0, 25.0 })
        {
            foreach (double lat in new[] { 0.0, 5.0, 10.0, 15.0, 20.0, 25.0 })
            {
                ZodiacalLight.S10(lon, lat, out bool m);
                double eps = ZodiacalLight.ElongationDeg(lon, lat);
                if (m) innermostMeasured = Math.Min(innermostMeasured, eps);
                else outermostBlank = Math.Max(outermostBlank, eps);
            }
        }
        // Note what is being measured here: not the raw table's edge but the INTERPOLATION's.
        // A bilinear cell with any blank corner cannot be interpolated across, so the model
        // refuses one grid step further out than the table itself stops. That is the correct
        // conservative behaviour, and it is why the numbers below sit outside the table's own
        // 14.1 degree innermost cell.
        Console.WriteLine($"    interpolation edge: innermost usable point at {innermostMeasured:F2} deg "
                        + $"elongation, outermost refused at {outermostBlank:F2} deg");
        Check("the refusal region is one grid cell wider than the table's own hole, no more",
              outermostBlank < 20.0, $"outermost refused at {outermostBlank:F2} deg");
        Check("and the whole refusal region lies far inside every avoidance angle in the roster",
              outermostBlank < 62.5, $"{outermostBlank:F2} deg against HST's 62.5 deg solar avoidance");
        Check("nothing is refused outside 20 deg elongation", innermostMeasured <= 20.0 + 1e-9,
              $"innermost usable point at {innermostMeasured:F2} deg");

        // Symmetries the source asserts.
        Near("longitude symmetry about the Sun-antisolar line",
             ZodiacalLight.VMagPerArcsec2(300.0, 30.0),
             ZodiacalLight.VMagPerArcsec2(60.0, 30.0), 1e-9, "mag/arcsec^2");
        Near("latitude symmetry about the ecliptic",
             ZodiacalLight.VMagPerArcsec2(90.0, -45.0),
             ZodiacalLight.VMagPerArcsec2(90.0, 45.0), 1e-9, "mag/arcsec^2");

        // THE GEGENSCHEIN. The zodiacal light is NOT monotonic in elongation: along the ecliptic
        // it falls to a minimum near 135-150 degrees and then brightens again toward the
        // anti-solar point, where dust grains backscatter. That is the gegenschein, it is real,
        // and a model that smoothed it away would have lost a named phenomenon. Leinert Table 16
        // records it as 140 S10sun at 135 and 150 degrees rising to 180 at the anti-solar point.
        double minimumLon = 0.0, minimumValue = 0.0;
        for (double lon = 15.0; lon <= 180.0; lon += 5.0)
        {
            double s = ZodiacalLight.S10(lon, 0.0, out _);
            if (minimumValue == 0.0 || s < minimumValue) { minimumValue = s; minimumLon = lon; }
        }
        Console.WriteLine($"    along the ecliptic the minimum is {minimumValue:F0} S10sun at "
                        + $"{minimumLon:F0} deg, rising to {ZodiacalLight.S10(180.0, 0.0, out _):F0} "
                        + "at the anti-solar point");
        Check("the gegenschein is preserved: the anti-solar point is brighter than the minimum",
              ZodiacalLight.S10(180.0, 0.0, out _) > minimumValue * 1.2,
              $"{ZodiacalLight.S10(180.0, 0.0, out _):F0} against {minimumValue:F0} S10sun");
        Check("and that minimum sits between 120 and 165 deg elongation",
              minimumLon >= 120.0 && minimumLon <= 165.0, $"{minimumLon:F0} deg");

        // Monotonic on the sunward side of that minimum, which is the part that is monotonic.
        bool monotone = true;
        double prev = 99.0;
        for (double lon = 120.0; lon >= 15.0; lon -= 5.0)
        {
            double v = ZodiacalLight.VMagPerArcsec2(lon, 0.0);
            if (v > prev + 1e-9) monotone = false;
            prev = v;
        }
        Check("brightens monotonically toward the Sun inside 120 deg", monotone,
             $"(15,0) = {ZodiacalLight.VMagPerArcsec2(15.0, 0.0):F2}, "
           + $"(120,0) = {ZodiacalLight.VMagPerArcsec2(120.0, 0.0):F2}");

        // The darkest sky is NOT the ecliptic pole, which is what the old constant used.
        Check("the table's own minimum is fainter than the ecliptic pole",
              ZodiacalLight.MinimumVMagPerArcsec2 > ZodiacalLight.EclipticPoleVMagPerArcsec2,
              $"{ZodiacalLight.MinimumVMagPerArcsec2:F3} against the pole's "
            + $"{ZodiacalLight.EclipticPoleVMagPerArcsec2:F3} mag/arcsec^2");

        // Nothing outside the table's own bounds anywhere on the sphere.
        double brightest = 99.0, faintest = 0.0;
        for (double lon = 0.0; lon <= 360.0; lon += 2.0)
        {
            for (double lat = -90.0; lat <= 90.0; lat += 2.0)
            {
                double v = ZodiacalLight.VMagPerArcsec2(lon, lat);
                if (v < brightest) brightest = v;
                if (v > faintest) faintest = v;
            }
        }
        Check("never brighter than the table's own maximum",
              brightest >= ZodiacalLight.MaximumVMagPerArcsec2 - 1e-9, $"brightest sampled {brightest:F2}");
        Check("never fainter than the table's own minimum",
              faintest <= ZodiacalLight.MinimumVMagPerArcsec2 + 1e-9, $"faintest sampled {faintest:F2}");

        // THE SIZE OF WHAT THE OLD CONSTANT WAS GETTING WRONG, at the brightest pointing an
        // instrument in this roster may legally take (HST's 62.5 degree solar avoidance).
        double atLimit = ZodiacalLight.VMagPerArcsec2(62.5, 0.0);
        double error = ZodiacalLight.MinimumVMagPerArcsec2 - atLimit;
        Console.WriteLine($"    a flat pole value understates the sky by {error:F2} mag "
                        + $"(x{Math.Pow(10.0, 0.4 * error):F1} in flux) at the 62.5 deg solar avoidance limit");
        Check("the flat constant was understating the reachable sky by more than a magnitude",
              error > 1.0, $"{error:F2} mag");
    }

    // ------------------------------------------------------------------ 4

    private static void EarthshineChecks()
    {
        // SRW98's own internal consistency. Their exponential fit is
        //     C_BG = 3.4564 * 10^(-0.06564 alpha)  electrons/s/pixel
        // and they separately quote the plateau above the knee as "~0.075 electrons/s/pixel".
        // At the 25 degree knee the two have to meet.
        double atKnee = 3.4564 * Math.Pow(10.0, -0.06564 * 25.0);
        Near("SRW98 fit meets its own quoted plateau at the 25 deg knee", atKnee, 0.075, 0.006,
             "e-/s/px");

        // The model's shape factor is 1 at the reference angle by construction, since that is
        // where the WFC3 spectrum is quoted.
        Near("shape factor at the 24 deg reference", Earthshine.LimbAngleFactor(24.0, true), 1.0,
             1e-12, "ratio");

        // ACS ISR 2003-05 measured a roughly 40-fold rise from the plateau down to 14 degrees on
        // ACS, and describes the STIS trend this model uses as shallower. So the model's own rise
        // over the same span must be large but below 40.
        double rise = Earthshine.LimbAngleFactor(14.0, true) / Earthshine.LimbAngleFactor(25.0, true);
        Check("14 deg is much brighter than the plateau", rise > 4.0, $"factor {rise:F1}");
        Check("and below the 40x ACS measured, as the STIS slope is shallower", rise < 40.0,
              $"factor {rise:F1}");

        // No scattered planet light off a dark limb: SRW98 measure the dark-limb background as
        // flat at the Earth-shadow level, which they attribute to zodiacal light.
        Near("dark limb contributes no earth-shine", Earthshine.LimbAngleFactor(15.0, false), 0.0,
             0.0, "ratio");

        // Flat above the knee, which is what they measured and explicitly found puzzling.
        Near("flat above the knee", Earthshine.LimbAngleFactor(40.0, true),
             Earthshine.LimbAngleFactor(60.0, true), 1e-12, "ratio");

        // ABSOLUTE CROSS-CHECK BETWEEN TWO INSTRUMENTS A DECADE APART. SRW98's count rate at the
        // reference angle, converted through their own stated PHOTFLAM (8.968e-20 erg/s/cm^2/A
        // per count) and plate scale (0.0508 arcsec/pixel), is a surface flux density. The WFC3
        // handbook's Table 9.3 gives the earth-shine flux density at the same angle directly.
        // They are different detectors, different bandpasses and different epochs, so agreement
        // to a factor of order unity is the most that can be asked, and is what is asserted.
        double srwCounts = 3.4564 * Math.Pow(10.0, -0.06564 * Earthshine.ReferenceLimbAngleDeg);
        double srwFlux = srwCounts * 8.968e-20 / (0.0508 * 0.0508);
        SpectralCurve wfc3 = Earthshine.ReferenceSpectrum();
        double wfc3Flux = wfc3.At(550e-9);
        double ratio = srwFlux / wfc3Flux;
        Console.WriteLine($"    SRW98 at 24 deg -> {srwFlux:E3}; WFC3 Table 9.3 at 5500 A -> {wfc3Flux:E3}");
        Check("STIS and WFC3 absolute levels agree within a factor of 2",
              ratio > 0.5 && ratio < 2.0, $"ratio {ratio:F2}");

        // The V surface brightness at the reference angle, on the same convention the airglow
        // model uses, so the two sky terms are summable. Earth-shine at the CVZ-centre pointing
        // should land near the sky brightnesses this codebase already works in, i.e. between the
        // zodiacal floor and a moonlit ground sky.
        double v = Earthshine.VMagPerArcsec2(24.0, true, 1.0);
        Console.WriteLine($"    earth-shine at 24 deg = {v:F2} V mag/arcsec^2");
        Check("earth-shine at the reference angle is a plausible sky brightness",
              v > 20.0 && v < 25.0, $"{v:F2} mag/arcsec^2");

        // Fainter at a larger limb angle, brighter at a smaller one.
        Check("brighter closer to the bright limb",
              Earthshine.VMagPerArcsec2(16.0, true, 1.0) < v, "");
        Check("no earth-shine at all off a dark limb",
              double.IsPositiveInfinity(Earthshine.VMagPerArcsec2(16.0, false, 1.0)), "");

        // Host scaling is 1 for Earth at 1 AU seen from HST's own altitude, by construction: it
        // is the geometry SRW98's curve was measured in.
        double scaling = Earthshine.HostBodyScaling(
            Earthshine.EarthGeometricAlbedo, Earthshine.EarthRadiusMeters,
            Earthshine.EarthRadiusMeters + Earthshine.HstOrbitAltitudeMeters,
            PhotonFluxModel.AuMeters);
        Near("host scaling is unity for Earth at 1 AU from 500 km", scaling, 1.0, 1e-9, "ratio");

        // And falls away with distance: from geostationary the Earth subtends far less sky.
        double geo = Earthshine.HostBodyScaling(
            Earthshine.EarthGeometricAlbedo, Earthshine.EarthRadiusMeters,
            42164000.0, PhotonFluxModel.AuMeters);
        Check("scattered planet light is far weaker from geostationary", geo < 0.1,
              $"factor {geo:F4} of the LEO value");
    }

    // ------------------------------------------------------------------ 5

    /// <summary>WFC3 IHB Table 6.7, the arcsec column: the delivered PSF FWHM vs wavelength.</summary>
    private static readonly double[] Table67WavelengthNm =
        { 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100 };
    private static readonly double[] Table67FwhmArcsec =
        { 0.083, 0.075, 0.070, 0.067, 0.067, 0.070, 0.074, 0.078, 0.084, 0.089 };

    private static void DeliveredPsf()
    {
        // The whole point of inverting the published table into a Gaussian: a kernel built with
        // the solved width has to measure back at the published width. If this fails, every
        // frame this instrument takes is the wrong sharpness.
        //
        // Only wavelengths where the delivered figure is actually wider than this aperture's own
        // diffraction limit can be reproduced; below that the solver correctly returns zero and
        // the kernel is diffraction-limited, which would be a real finding rather than a bug.
        for (int i = 0; i < Table67WavelengthNm.Length; i++)
        {
            double lambda = Table67WavelengthNm[i] * 1e-9;
            double target = Table67FwhmArcsec[i];

            double airy = OpticalPsf.AiryFwhmArcsec(HstApertureMeters, HstObstruction, lambda);
            if (airy >= target)
            {
                Console.WriteLine($"    {Table67WavelengthNm[i]:F0} nm: diffraction alone is "
                                + $"{airy:F4}\" against a published {target:F3}\"; nothing to add.");
                continue;
            }

            double gauss = OpticalPsf.GaussianFwhmForDelivered(
                target, UvisPlateScaleArcsec, HstApertureMeters, HstObstruction, lambda,
                HstVaneCount, HstVaneWidthMeters);

            float[] kernel = OpticalPsf.BuildKernel(
                UvisPlateScaleArcsec, HstApertureMeters, HstObstruction, lambda,
                0.0, 0.0, HstVaneCount, HstVaneWidthMeters, gauss, HstPads, out int radius);

            double measured = OpticalPsf.MeasureKernelFwhmArcsec(kernel, radius, UvisPlateScaleArcsec);
            Near($"delivered FWHM at {Table67WavelengthNm[i]:F0} nm", measured, target, 0.006, "arcsec");
        }

        // The table's shape is not monotonic, and that is physics rather than noise: it turns
        // over near 500 nm because diffraction grows with wavelength while the wavefront error
        // costs more at shorter ones. The model has to keep that, not smooth it away.
        Check("published table has its minimum in the 500-600 nm range",
              Table67FwhmArcsec[3] <= Table67FwhmArcsec[0]
              && Table67FwhmArcsec[3] <= Table67FwhmArcsec[9], "");

        // HST is NOT diffraction limited anywhere in the detector's own band, which is the claim
        // the catalogue entry makes about it.
        //
        // "In the detector's own band" is not a hedge, it is the finding. WFC3 IHB Table 5.1
        // gives the UVIS CCDs' wavelength range as 200 to 1000 nm, and Table 6.7 tabulates the
        // PSF out to 1100 nm, one row past it. At that last row the published width, 0.089
        // arcsec, is NARROWER than a 2.4 m aperture's own diffraction limit, which no telescope
        // can deliver. So the 1100 nm row is the handbook's optical model run past the band
        // rather than a measurement, and it is excluded here and recorded in section 12 rather
        // than quietly averaged in.
        bool everDiffractionLimited = false;
        for (int i = 0; i < Table67WavelengthNm.Length; i++)
        {
            if (Table67WavelengthNm[i] > 1000.0) continue;   // outside the detector's stated range
            double airy = OpticalPsf.AiryFwhmArcsec(
                HstApertureMeters, HstObstruction, Table67WavelengthNm[i] * 1e-9);
            if (airy >= Table67FwhmArcsec[i]) everDiffractionLimited = true;
        }
        Check("HST delivers wider than its diffraction limit throughout the detector's 200-1000 nm band",
              !everDiffractionLimited, "");

        double airy1100 = OpticalPsf.AiryFwhmArcsec(HstApertureMeters, HstObstruction, 1100e-9);
        Check("Table 6.7's out-of-band 1100 nm row is below the aperture's diffraction limit",
              airy1100 > 0.089,
              $"diffraction {airy1100:F4}\" against a tabulated {0.089:F3}\"; the row is a model "
              + "extrapolation past the detector's published 200-1000 nm range");

        // And the size of that gap at V, which is the headline number in the catalogue comment.
        double airyV = OpticalPsf.AiryFwhmArcsec(HstApertureMeters, HstObstruction, 500e-9);
        Console.WriteLine($"    at 500 nm: diffraction {airyV:F4}\", delivered 0.067\", "
                        + $"ratio {0.067 / airyV:F2}");
    }

    // ------------------------------------------------------------------ 6

    private static void PupilPads()
    {
        const double lambda = 550e-9;

        // Reducibility, unchanged: with no vanes and no pads the two-dimensional pupil transform
        // must still reproduce the closed-form obstructed-aperture intensity. This is the
        // existing contract and the pad support must not have broken it.
        var plain = new PupilDiffraction(HstApertureMeters, HstObstruction, lambda, 0, 0.0, 0.0);
        double worst = 0.0;
        for (double arcsec = 0.001; arcsec < 0.5; arcsec += 0.003)
        {
            double a = plain.IntensityArcsec(arcsec, 0.0);
            double b = OpticalPsf.AiryIntensity(arcsec * Math.PI / (180.0 * 3600.0),
                                                HstApertureMeters, HstObstruction, lambda);
            worst = Math.Max(worst, Math.Abs(a - b));
        }
        Check("pupil transform still reduces to the closed-form Airy pattern", worst < 1e-9,
              $"worst absolute difference {worst:E2}");

        // The pads' own area, from Tiny Tim's table: three discs of radius 0.065 pupil radii
        // against an annulus of (1 - 0.330^2) pupil radii squared. Both areas carry a factor of
        // pi which cancels, giving 3 * 0.065^2 / (1 - 0.330^2) = 1.4 per cent.
        var padded = new PupilDiffraction(HstApertureMeters, HstObstruction, lambda,
                                          HstVaneCount, HstVaneWidthMeters, 0.0, HstPads);
        double expected = 3.0 * 0.065 * 0.065 / (1.0 - HstObstruction * HstObstruction);
        Near("pad obscuration fraction", padded.PadObscurationFraction, expected, 1e-9, "fraction");
        Check("pads obscure about one and a half per cent of the pupil",
              padded.PadObscurationFraction > 0.012 && padded.PadObscurationFraction < 0.017,
              $"{padded.PadObscurationFraction:P2}");

        // On axis the intensity is 1 by normalisation, with or without pads.
        Near("padded pupil is normalised on axis", padded.Intensity(0.0, 0.0), 1.0, 1e-9, "ratio");

        // Three pads at roughly 120 degrees break the pattern's rotational symmetry, which is
        // exactly why they needed a complex amplitude. If the intensity came out the same at
        // every azimuth, the phase terms would have cancelled and the pads would be doing nothing
        // but dimming the pupil.
        double minI = double.MaxValue, maxI = 0.0;
        const double sampleArcsec = 0.30;
        for (double deg = 0.0; deg < 360.0; deg += 3.0)
        {
            double rad = deg * Math.PI / 180.0;
            double v = padded.IntensityArcsec(sampleArcsec * Math.Cos(rad), sampleArcsec * Math.Sin(rad));
            minI = Math.Min(minI, v);
            maxI = Math.Max(maxI, v);
        }
        Check("pads and spider make the pattern azimuthally structured", maxI > 2.0 * minI,
              $"azimuthal range {minI:E2} to {maxI:E2} at {sampleArcsec}\"");

        // A kernel built with pads must still normalise to unit total, which is what guarantees
        // the photometry is unaffected by adding an obscuration to the model.
        float[] k = OpticalPsf.BuildKernel(UvisPlateScaleArcsec, HstApertureMeters, HstObstruction,
                                           lambda, 0.0, 0.0, HstVaneCount, HstVaneWidthMeters, 0.0,
                                           HstPads, out int radius);
        double sum = 0.0;
        for (int i = 0; i < k.Length; i++) sum += k[i];
        Near("padded kernel is normalised", sum, 1.0, 1e-5, "total");
    }

    // ------------------------------------------------------------------ 7

    private static void Pointing()
    {
        // HST Primer: "current performance has jitter of 0.008 arcsec rms". With wheels holding
        // and nothing else going wrong, the budget is that figure and nothing more.
        var wheels = new PointingInputs
        {
            Mode = AttitudeControlMode.MomentumExchange,
            ExposureSeconds = 1000.0,
            InstrumentJitterArcsecRms = 0.008,
        };
        PointingBudget w = PointingStability.Evaluate(in wheels);
        Near("wheel-controlled pointing is the instrument's own floor", w.TotalArcsecRms, 0.008,
             1e-12, "arcsec rms");

        // Against a 0.0396 arcsec pixel that is a fifth of a pixel, which is why a real space
        // telescope's pointing is not what limits its images.
        Check("HST's jitter is a small fraction of a UVIS pixel",
              w.TotalArcsecRms / UvisPlateScaleArcsec < 0.25,
              $"{w.TotalArcsecRms / UvisPlateScaleArcsec:F2} px");

        // The limit cycle. A 4500 kg spacecraft of 3 m radius on 1 kN of RCS, deadband 30 arcsec.
        double inertia = 0.4 * 4500.0 * 3.0 * 3.0;
        var rcs = new PointingInputs
        {
            Mode = AttitudeControlMode.ReactionControl,
            ExposureSeconds = 1000.0,
            InstrumentJitterArcsecRms = 0.008,
            DeadbandArcsec = 30.0,
            ControlTorqueNm = 1000.0 * 3.0,
            InertiaKgM2 = inertia,
            MinimumPulseSeconds = 0.05,
        };
        PointingBudget r = PointingStability.Evaluate(in rcs);

        // Over a long exposure the boresight traverses the whole deadband, so the RMS is the
        // uniform-distribution value over the full peak-to-peak width: 2 theta / sqrt(12).
        Near("long exposure under a limit cycle samples the whole deadband",
             r.VehicleSmearArcsecRms, 2.0 * 30.0 / Math.Sqrt(12.0), 1e-9, "arcsec rms");

        // And that is catastrophic at this plate scale, which is the design claim the part
        // config's reaction wheels exist to answer.
        Check("thruster pointing smears across many pixels",
              r.TotalArcsecRms / UvisPlateScaleArcsec > 100.0,
              $"{r.TotalArcsecRms / UvisPlateScaleArcsec:F0} px");

        // THE TWO REGIMES AGREE AT THE CROSSOVER. This is the check that the single expression in
        // LimitCycleSmearArcsec really is one formula and not two cases stitched together: at an
        // exposure of exactly the half-period, the travelled arc equals the deadband width and
        // both branches must give the same answer.
        double rate = PointingStability.LimitCycleRateRadPerSecond(1000.0 * 3.0, inertia, 0.05);
        double period = PointingStability.LimitCyclePeriodSeconds(30.0, rate);
        double atCrossover = PointingStability.LimitCycleSmearArcsec(30.0, rate, period / 2.0);
        double saturated = PointingStability.LimitCycleSmearArcsec(30.0, rate, period * 10.0);
        Near("limit-cycle regimes meet at the crossover", atCrossover, saturated, 1e-9, "arcsec rms");

        // A short exposure catches only part of the cycle and is correspondingly sharper: the
        // reason a real thruster-controlled vehicle can still take short frames.
        double shortExposure = PointingStability.LimitCycleSmearArcsec(30.0, rate, period / 100.0);
        Check("a short exposure under a limit cycle is much sharper", shortExposure < saturated / 10.0,
              $"{shortExposure:F3}\" against {saturated:F3}\"");

        // Quadrature: two independent contributions add in variance.
        Near("quadrature sum", PointingStability.TotalPointingRmsArcsec(0.3, 0.4), 0.5, 1e-12,
             "arcsec rms");

        // RMS to FWHM is the standard Gaussian factor.
        Near("RMS to FWHM", PointingStability.RmsToFwhmArcsec(1.0), 2.3548, 1e-4, "ratio");

        // An uncontrolled vehicle with a real body rate smears in proportion to the exposure.
        var drifting = new PointingInputs
        {
            Mode = AttitudeControlMode.Uncontrolled,
            ExposureSeconds = 100.0,
            HasMeasuredRate = true,
            MeasuredRateArcsecPerSecond = 3600.0,   // one degree per second
        };
        PointingBudget d = PointingStability.Evaluate(in drifting);
        Near("a measured body rate smears by rate * time / sqrt(12)",
             d.VehicleSmearArcsecRms, 3600.0 * 100.0 / Math.Sqrt(12.0), 1e-6, "arcsec rms");
        Check("a measured rate is reported as measured", d.RateWasMeasured, "");
    }

    // ------------------------------------------------------------------ 8

    private static void ApertureSampling()
    {
        const int n = 400;
        double[] offsets = ApertureObstruction.SampleOffsets(HstApertureMeters, HstObstruction, n);
        Check("one (x, y) pair per sample", offsets.Length == 2 * n, $"{offsets.Length} values");

        double outer = 0.5 * HstApertureMeters;
        double inner = outer * HstObstruction;
        bool allInside = true;
        for (int i = 0; i < n; i++)
        {
            double r = Math.Sqrt(offsets[2 * i] * offsets[2 * i] + offsets[2 * i + 1] * offsets[2 * i + 1]);
            if (r < inner - 1e-9 || r > outer + 1e-9) allInside = false;
        }
        Check("every sample lies in the open annulus", allInside, "");

        // EQUAL AREA IS THE PROPERTY THE BLOCKED-FRACTION COUNT DEPENDS ON. Split the annulus into
        // two halves of equal area at r = sqrt((inner^2 + outer^2)/2); each must get about half
        // the samples, or a straight count of blocked rays would not be an area fraction.
        double split = Math.Sqrt(0.5 * (inner * inner + outer * outer));
        int insideHalf = 0;
        for (int i = 0; i < n; i++)
        {
            double r = Math.Sqrt(offsets[2 * i] * offsets[2 * i] + offsets[2 * i + 1] * offsets[2 * i + 1]);
            if (r <= split) insideHalf++;
        }
        Near("equal-area sampling puts half the rays in the inner half-area",
             insideHalf / (double)n, 0.5, 0.02, "fraction");

        // Azimuthal coverage: a Vogel spiral must not leave a quadrant empty.
        var quadrant = new int[4];
        for (int i = 0; i < n; i++)
        {
            double a = Math.Atan2(offsets[2 * i + 1], offsets[2 * i]);
            if (a < 0) a += 2.0 * Math.PI;
            quadrant[(int)(a / (Math.PI / 2.0)) % 4]++;
        }
        bool even = true;
        for (int q = 0; q < 4; q++) if (quadrant[q] < n / 8) even = false;
        Check("azimuthal coverage is even across quadrants", even,
              $"{quadrant[0]}/{quadrant[1]}/{quadrant[2]}/{quadrant[3]}");

        // The clear-aperture tolerance is a sampling tolerance, not a real allowance.
        Check("one blocked ray in a hundred still counts as clear",
              ApertureObstruction.IsClear(ApertureObstruction.BlockedFraction(1, 100)), "");
        Check("two blocked rays in a hundred does not",
              !ApertureObstruction.IsClear(ApertureObstruction.BlockedFraction(2, 100)), "");
    }

    // ------------------------------------------------------------------ 9

    private static void Telemetry()
    {
        // WFC3/UVIS reads out two 2051 x 4096 CCDs at 16 bits.
        long pixels = 2L * 2051L * 4096L;
        double bits = TelemetryBudget.FrameBits(pixels, 16);
        Near("full-frame volume", bits / 1e6, 268.9, 0.5, "Mbit");
        Console.WriteLine($"    one WFC3/UVIS frame = {TelemetryBudget.DescribeBits(bits)}");

        // Against KSP's own antennas. The Communotron 16 is 500 kBit/s at full signal, so a full
        // frame takes most of an orbit; that is the design constraint the part description warns
        // about, and it is arithmetic rather than a balance decision.
        double smallAntenna = TelemetryBudget.DownlinkSeconds(bits, 500e3, 1.0);
        Check("a small antenna takes many minutes per frame", smallAntenna > 300.0,
              $"{smallAntenna:F0} s");

        // A relay dish at 2 MBit/s is several times quicker.
        double bigAntenna = TelemetryBudget.DownlinkSeconds(bits, 2e6, 1.0);
        Check("a large antenna is proportionally quicker", bigAntenna < smallAntenna / 3.0,
              $"{bigAntenna:F0} s against {smallAntenna:F0} s");

        // Half signal strength, twice the time.
        Near("signal strength scales the link linearly",
             TelemetryBudget.DownlinkSeconds(bits, 2e6, 0.5), 2.0 * bigAntenna, 1e-6, "s");

        // No link is not a slow link.
        Check("no link never completes",
              double.IsPositiveInfinity(TelemetryBudget.DownlinkSeconds(bits, 2e6, 0.0)), "");
    }

    // ------------------------------------------------------------------ 10

    private static void CosmicRays()
    {
        // WFC3 IHB Sect. 5.4.10 publishes the IMPACTED-PIXEL FRACTION, not an event rate: "the
        // fraction of WFC3 pixels impacted by cosmic rays varies from 5% to 9% per chip during
        // 1800 sec exposures in SAA-free orbits". The catalogue has to carry an event rate in
        // events per minute per cm^2, because that is what the pipeline's cosmic-ray generator
        // takes, so the rate is DERIVED from the published fraction rather than quoted.
        //
        // This check runs the derivation in reverse: take the catalogue's rate, apply the
        // pipeline's own track-length distribution, and confirm the impacted fraction lands back
        // inside the published 5-9 per cent. If the rate is ever retuned, this is what catches it
        // drifting away from the measurement it came from.
        const double eventsPerMinutePerCm2 = 110.0;    // VisualTelescopeCatalog.HubbleWfc3Uvis
        const double pixelMeters = UvisPixelMeters;
        const int chipWidth = 4096, chipHeight = 2051;
        const double exposureSeconds = 1800.0;

        // The generator lays each event down as a straight track of a uniformly distributed
        // length between these bounds (SolarSystemCameraTexture.CosmicRayMinTrackPx/MaxTrackPx).
        const double minTrackPx = 2.0, maxTrackPx = 14.0;
        double meanTrackPx = 0.5 * (minTrackPx + maxTrackPx);

        double chipAreaCm2 = (chipWidth * pixelMeters * 100.0) * (chipHeight * pixelMeters * 100.0);
        double events = eventsPerMinutePerCm2 * chipAreaCm2 * (exposureSeconds / 60.0);
        double impactedPixels = events * meanTrackPx;
        double fraction = impactedPixels / ((double)chipWidth * chipHeight);

        Console.WriteLine($"    chip area {chipAreaCm2:F1} cm^2, {events:N0} events in {exposureSeconds:F0} s, "
                        + $"mean track {meanTrackPx:F0} px");
        Check("impacted-pixel fraction lands in the published 5-9 per cent",
              fraction >= 0.05 && fraction <= 0.09, $"{fraction:P1}");

        // And it is two orders of magnitude above the sea-level muon flux the ground instruments
        // in this roster carry, which is the whole reason it is a per-instrument field.
        const double seaLevelPerMinutePerCm2 = 1.0;
        double ratio = eventsPerMinutePerCm2 / seaLevelPerMinutePerCm2;
        Check("orbital rate is about two orders of magnitude above sea level",
              ratio > 50.0 && ratio < 500.0, $"factor {ratio:F0}");
    }

    // ------------------------------------------------------------------ helpers

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private static void Near(string what, double actual, double expected, double tolerance, string unit)
    {
        checks++;
        bool ok = Math.Abs(actual - expected) <= tolerance;
        if (!ok) failures++;
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {what}: {actual:G6} {unit} "
                        + $"(expected {expected:G6} +/- {tolerance:G3})");
    }

    private static void Check(string what, bool ok, string detail)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {what}"
                        + (string.IsNullOrEmpty(detail) ? "" : $": {detail}"));
    }
}
