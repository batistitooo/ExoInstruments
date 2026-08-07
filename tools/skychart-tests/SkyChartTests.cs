using System;
using ExoInstruments.Core;

/// <summary>
/// Exercises the full-sky chart's geometry, headless. What is being tested is not "does the
/// projection return numbers" but the four ways a sky chart is quietly wrong:
///
///   1. it is MIRRORED: the projection's parity disagrees with the sky seen from inside the
///      celestial sphere. Checked against the horizontal dome projection the chart replaced,
///      whose parity was verified in game: the local north/east handedness must match at every
///      test point, both hemispheres;
///   2. its inverse is not its inverse (clicks land on the wrong sky);
///   3. its occlusion test hides a body that is actually IN FRONT of the occluder, or grades a
///      partial occultation with the wrong thresholds;
///   4. its rendered terminator disagrees with the phase geometry: the illuminated fraction of
///      a Lambert sphere's disc is (1 + cos i)/2.
///
/// Run: dotnet run -p:Core=../../ExoInstruments/Core
/// </summary>
static class SkyChartTests
{
    static int failures;
    static int checks;

    static void Main()
    {
        Landmarks();
        RoundTrip();
        DirectionRoundTrip();
        ParityAgainstDome();
        LocalBasisAgainstFiniteDifferences();
        OcclusionFiniteDistance();
        OcclusionGrading();
        HorizonDipIdentity();
        TerminatorIlluminatedFraction();
        LimbGlowIsSunlitSided();

        Console.WriteLine();
        Console.WriteLine($"{checks} checks, {failures} failures");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    static void Check(bool ok, string what)
    {
        checks++;
        if (!ok)
        {
            failures++;
            Console.WriteLine("FAIL  " + what);
        }
    }

    const int W = 640, H = 360;   // the game's own chart buffer: the 2:1 Hammer oval plus margins

    // ------------------------------------------------------------------
    static void Landmarks()
    {
        // The oval's anchor points: RA 12h / Dec 0 at the centre, the poles at the top and
        // bottom of the ellipse, RA 0h on both rims, and north up.
        SkyChartProjection.EllipseHalfAxes(W, H, out double a, out double b);
        Check(Math.Abs(a - 2.0 * b) < 1e-9, $"ellipse is exactly 2:1 ({a:F2} x {b:F2})");

        SkyChartProjection.ProjectRaw(180.0, 0.0, W, H, out double x, out double y);
        Check(Math.Abs(x - W / 2.0) < 1e-9 && Math.Abs(y - H / 2.0) < 1e-9, "RA 12h Dec 0 at centre");
        SkyChartProjection.ProjectRaw(0.0, 90.0, W, H, out x, out y);
        Check(Math.Abs(x - W / 2.0) < 1e-9 && Math.Abs(y - (H / 2.0 + b)) < 1e-9, "north pole at top");
        SkyChartProjection.ProjectRaw(0.0, -90.0, W, H, out x, out y);
        Check(Math.Abs(y - (H / 2.0 - b)) < 1e-9, "south pole at bottom");
        SkyChartProjection.ProjectRaw(180.0 + 179.999, 0.0, W, H, out x, out y);
        Check(x > W / 2.0 + a - 0.1, "RA just short of 0h (eastward) on the +x rim");
    }

    static void RoundTrip()
    {
        double worst = 0.0;
        for (double ra = 0.0; ra < 360.0; ra += 10.0)
        for (double dec = -85.0; dec <= 85.0; dec += 5.0)
        {
            SkyChartProjection.ProjectRaw(ra, dec, W, H, out double x, out double y);
            if (!SkyChartProjection.TryUnprojectRaw(x, y, W, H, out double ra2, out double dec2))
            {
                Check(false, $"unproject refused a projected point at ra={ra} dec={dec}");
                continue;
            }
            double dRa = Math.Abs(((ra2 - ra + 540.0) % 360.0) - 180.0) * Math.Cos(dec * Math.PI / 180.0);
            double dDec = Math.Abs(dec2 - dec);
            worst = Math.Max(worst, Math.Max(dRa, dDec));
        }
        Check(worst < 1e-9, $"project/unproject round trip, worst error {worst:E2} deg");
    }

    static void DirectionRoundTrip()
    {
        double worst = 0.0;
        for (double ra = 5.0; ra < 360.0; ra += 25.0)
        for (double dec = -88.0; dec <= 88.0; dec += 8.0)
        {
            SkyVector d = SkyChartProjection.DirectionFromEquatorial(ra, dec);
            SkyChartProjection.EquatorialFromDirection(d, out double ra2, out double dec2);
            double dRa = Math.Abs(((ra2 - ra + 540.0) % 360.0) - 180.0) * Math.Cos(dec * Math.PI / 180.0);
            worst = Math.Max(worst, Math.Max(dRa, Math.Abs(dec2 - dec)));
        }
        Check(worst < 1e-9, $"direction round trip, worst error {worst:E2} deg");
    }

    // ------------------------------------------------------------------
    /// <summary>The dome projection the chart replaced, verbatim: known-good parity in game.</summary>
    static void ProjectDome(double altDeg, double azDeg, out double x, out double y)
    {
        double rMax = Math.Min(W, H) / 2.0 - 4.0;
        double r = rMax * (90.0 - altDeg) / 90.0;
        double az = azDeg * Math.PI / 180.0;
        x = W / 2.0 + r * Math.Sin(az);
        y = H / 2.0 + r * Math.Cos(az);
    }

    static void ParityAgainstDome()
    {
        // At any sky point visible from any site, step 0.1 deg toward local north and toward
        // local east. The signed area of the (point, north-step, east-step) triangle must have
        // the same sign in both projections: same parity means not mirrored.
        double[] latitudes = { -60.0, -5.0, 0.0, 12.0, 47.0 };
        double[] meridians = { 0.0, 101.0, 250.0 };
        foreach (double lat in latitudes)
        foreach (double m in meridians)
        {
            for (double alt = 15.0; alt <= 75.0; alt += 30.0)
            for (double az = 10.0; az < 360.0; az += 47.0)
            {
                const double step = 0.1;
                SkyCoordinates.HorizontalToEquatorial(alt, az, m, lat, out double ra0, out double dec0);
                SkyCoordinates.HorizontalToEquatorial(alt + step, az, m, lat, out double raN, out double decN);
                SkyCoordinates.HorizontalToEquatorial(alt, az + step, m, lat, out double raE, out double decE);

                ProjectDome(alt, az, out double dx0, out double dy0);
                ProjectDome(alt + step, az, out double dxN, out double dyN);
                ProjectDome(alt, az + step, out double dxE, out double dyE);
                double crossDome = (dxN - dx0) * (dyE - dy0) - (dyN - dy0) * (dxE - dx0);

                SkyChartProjection.ProjectRaw(ra0, dec0, W, H, out double ex0, out double ey0);
                SkyChartProjection.ProjectRaw(raN, decN, W, H, out double exN, out double eyN);
                SkyChartProjection.ProjectRaw(raE, decE, W, H, out double exE, out double eyE);
                double crossEq = (exN - ex0) * (eyE - ey0) - (eyN - ey0) * (exE - ex0);

                if (Math.Abs(crossDome) < 1e-9 || Math.Abs(crossEq) < 1e-9) continue;
                Check(Math.Sign(crossDome) == Math.Sign(crossEq),
                      $"parity vs dome at lat={lat} M={m} alt={alt} az={az}");
            }
        }
    }

    // ------------------------------------------------------------------
    static void LocalBasisAgainstFiniteDifferences()
    {
        double worst = 0.0;
        for (double ra = 15.0; ra < 360.0; ra += 30.0)
        for (double dec = -80.0; dec <= 80.0; dec += 20.0)
        {
            const double h = 1e-6;
            SkyChartProjection.ProjectRaw(ra, dec, W, H, out double x0, out double y0);
            SkyChartProjection.ProjectRaw(ra, dec + h, W, H, out double xD, out double yD);
            SkyChartProjection.ProjectRaw(ra + h, dec, W, H, out double xR, out double yR);
            double cosDec = Math.Cos(dec * Math.PI / 180.0);
            // Numeric Jacobian, converted to per-arc-degree along RA like LocalBasis reports.
            double nDecX = (xD - x0) / h, nDecY = (yD - y0) / h;
            double nRaX = (xR - x0) / (h * cosDec), nRaY = (yR - y0) / (h * cosDec);

            SkyChartProjection.LocalBasis(ra, dec, W, H,
                out double jDecX, out double jDecY, out double jRaX, out double jRaY);

            worst = Math.Max(worst, Math.Abs(nDecX - jDecX) + Math.Abs(nDecY - jDecY)
                                  + Math.Abs(nRaX - jRaX) + Math.Abs(nRaY - jRaY));
        }
        Check(worst < 1e-3, $"closed-form Jacobian vs finite differences, worst {worst:E2} px/deg");
    }

    // ------------------------------------------------------------------
    static void OcclusionFiniteDistance()
    {
        var ahead = new SkyVector(1, 0, 0);
        var occ = SkyOccluder.From(ahead, 10.0, 1.0);   // angular radius asin(0.1) = 5.74 deg

        Check(SkyOcclusion.Classify(ahead, 20.0, 0.0, in occ) == OcclusionState.Full,
              "point target behind the occluder is Full");
        Check(SkyOcclusion.Classify(ahead, 5.0, 0.0, in occ) == OcclusionState.Clear,
              "point target IN FRONT of the occluder on the same sight line is Clear");
        Check(SkyOcclusion.Classify(ahead, double.PositiveInfinity, 0.0, in occ) == OcclusionState.Full,
              "star behind the occluder is Full");
        Check(SkyOcclusion.Classify(new SkyVector(0, 1, 0), double.PositiveInfinity, 0.0, in occ)
              == OcclusionState.Clear, "star 90 deg away is Clear");

        // Just past the tangent: near surface distance ~ d cos(sep); target closer stays Clear.
        double alpha = occ.AngularRadiusDeg;
        SkyVector graze = SkyChartProjection.DirectionFromEquatorial(alpha + 1.0, 0.0);
        occ = SkyOccluder.From(SkyChartProjection.DirectionFromEquatorial(0.0, 0.0), 10.0, 1.0);
        Check(SkyOcclusion.Classify(graze, double.PositiveInfinity, 0.0, in occ) == OcclusionState.Clear,
              "star 1 deg past the limb is Clear");
    }

    static void OcclusionGrading()
    {
        var centre = SkyChartProjection.DirectionFromEquatorial(0.0, 0.0);
        var occ = SkyOccluder.From(centre, 10.0, 1.0);
        double alpha = occ.AngularRadiusDeg;
        const double targetRadius = 1.0;

        SkyVector At(double sepDeg) => SkyChartProjection.DirectionFromEquatorial(sepDeg, 0.0);

        Check(SkyOcclusion.Classify(At(alpha - targetRadius - 0.1), 1e12, targetRadius, in occ)
              == OcclusionState.Full, "extended target inside alpha - r is Full");
        Check(SkyOcclusion.Classify(At(alpha), 1e12, targetRadius, in occ)
              == OcclusionState.Partial, "extended target straddling the limb is Partial");
        Check(SkyOcclusion.Classify(At(alpha + targetRadius + 0.1), 1e12, targetRadius, in occ)
              == OcclusionState.Clear, "extended target past alpha + r is Clear");
    }

    static void HorizonDipIdentity()
    {
        // From 100 m above a 600 km sphere the horizon sits below the astronomical horizontal:
        // the cap's angular radius asin(R/(R+h)) and the dip acos(R/(R+h)) are complementary.
        const double R = 600_000.0, h = 100.0;
        double alpha = OrbitalVisibility.AngularRadiusDeg(R, R + h);
        double dip = Math.Acos(R / (R + h)) * 180.0 / Math.PI;
        Check(Math.Abs(alpha + dip - 90.0) < 1e-9, $"cap radius + dip = 90 (got {alpha + dip})");
        Check(dip > 1.0 && dip < 1.1, $"100 m horizon dip on Kerbin ~1.05 deg (got {dip:F3})");
    }

    // ------------------------------------------------------------------
    static double DiscDayFraction(double phaseDeg, out int discPixels)
    {
        const int size = 512;
        // Host toward RA 12h Dec 0 = the centre of the oval, the least distorted zone.
        var host = new OverlayHost
        {
            HasBody = true,
            Direction = new SkyVector(-1, 0, 0),
            AngularRadiusDeg = 12.0,
            TintR = 200, TintG = 200, TintB = 200,
        };
        // Phase angle i at the body between the Sun and the observer: observer direction from
        // the body is -Direction = +x, so sun = cos(i) * (+x) + sin(i) * t.
        double i = phaseDeg * Math.PI / 180.0;
        host.SunDirection = new SkyVector(Math.Cos(i), 0.0, Math.Sin(i));

        byte[] rgba = SkyChartOverlayRenderer.EnsureBuffer(null, size, size);
        SkyChartOverlayRenderer.Render(rgba, size, size, in host, null);

        discPixels = 0;
        int day = 0;
        // The exact terminator (lit = 0) renders at shade 0.0848, byte 17 for this tint; night
        // far from it at 9. "Day" = strictly brighter than the terminator itself, so the count
        // splits at lit = 0 rather than at some brightness bias.
        for (int p = 0; p < size * size; p++)
        {
            if (rgba[p * 4 + 3] != 255) continue;   // only the opaque disc
            discPixels++;
            if (rgba[p * 4] > 17) day++;
        }
        return discPixels > 0 ? (double)day / discPixels : double.NaN;
    }

    static void TerminatorIlluminatedFraction()
    {
        // Lambert sphere: illuminated fraction of the disc = (1 + cos i)/2. The render is at
        // finite distance (sin 12 deg), worth ~2 percent, and the terminator band is smoothed:
        // 6 percent tolerance.
        foreach ((double phase, double expected) in new[] { (0.0, 1.0), (90.0, 0.5), (150.0, 0.0670) })
        {
            double fraction = DiscDayFraction(phase, out int discPixels);
            Check(discPixels > 500, $"disc at phase {phase} has enough pixels to measure ({discPixels})");
            Check(Math.Abs(fraction - expected) < 0.06,
                  $"illuminated fraction at phase {phase}: {fraction:F3} vs (1+cos i)/2 = {expected:F3}");
        }
    }

    static void LimbGlowIsSunlitSided()
    {
        const int size = 512;
        var host = new OverlayHost
        {
            HasBody = true,
            Direction = new SkyVector(-1, 0, 0),
            AngularRadiusDeg = 12.0,
            TintR = 200, TintG = 200, TintB = 200,
            SunlitLimbGlowDeg = 20.0,
            DarkLimbGlowDeg = 6.0,
            // Sun toward +z at phase 90: the +z (Dec > 0) limb is lit, the -z limb dark.
            SunDirection = new SkyVector(0, 0, 1),
        };
        byte[] rgba = SkyChartOverlayRenderer.EnsureBuffer(null, size, size);
        SkyChartOverlayRenderer.Render(rgba, size, size, in host, null);

        // Sample 3 degrees past the limb on the lit and dark sides (15 deg from centre along Dec).
        SkyChartProjection.EquatorialFromDirection(new SkyVector(-Math.Cos(15.0 * Math.PI / 180.0), 0,
            Math.Sin(15.0 * Math.PI / 180.0)), out double raLit, out double decLit);
        SkyChartProjection.EquatorialFromDirection(new SkyVector(-Math.Cos(15.0 * Math.PI / 180.0), 0,
            -Math.Sin(15.0 * Math.PI / 180.0)), out double raDark, out double decDark);
        SkyChartProjection.ProjectRaw(raLit, decLit, size, size, out double xl, out double yl);
        SkyChartProjection.ProjectRaw(raDark, decDark, size, size, out double xd, out double yd);
        int litAlpha = rgba[(((int)yl) * size + (int)xl) * 4 + 3];
        int darkAlpha = rgba[(((int)yd) * size + (int)xd) * 4 + 3];

        Check(litAlpha > 0, $"sunlit limb glows 3 deg past the limb (alpha {litAlpha})");
        Check(litAlpha > darkAlpha,
              $"sunlit limb glows more than the dark limb ({litAlpha} vs {darkAlpha})");
    }
}
