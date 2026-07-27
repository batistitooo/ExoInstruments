using System;
using System.Collections.Generic;
using ExoInstruments.Core;

class Program
{
    // Real RC20 + ZWO ASI294MM Pro at binning 4, matching VisualTelescopeCatalog.Rc20.
    const double Aperture = 0.51, Obstruction = 0.39, FocalLength = 0.51 * 6.8;
    const double PixelPitch = 4.63e-6 * 4;
    const int W = 4144 / 4, H = 2822 / 4;
    const double QE = 0.90, FullWell = 66000.0 * 16, ReadNoise = 1.2, Dark = 0.002;
    const double SiteAltitude = 100.0;
    const double LumBandwidth = 2650.0, LumWavelength = 550e-9;

    static double PlateScale => PixelPitch / FocalLength * (180.0 / Math.PI) * 3600.0;
    static double FovDeg => W * PlateScale / 3600.0;
    static double AreaCm2 => Math.PI * Math.Pow(Aperture * 100 / 2, 2) * (1 - Obstruction * Obstruction);

    static int failures = 0;

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) failures++;
    }

    static void Main(string[] args)
    {
        Console.WriteLine($"RC20 @ bin4: plate scale {PlateScale:F3}\"/px, field {FovDeg:F3} x {FovDeg * H / W:F3} deg, "
                        + $"area {AreaCm2:F0} cm2, full well {FullWell:F0} e-");
        Console.WriteLine();

        TestProjection();
        TestPhotometry();
        TestSkyBrightness();
        TestExtinction();
        TestFluxConservation();
        if (args.Length > 0) { TestCatalog(args[0]); TestEndToEnd(args[0]); }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // --- Geometry -----------------------------------------------------------
    static void TestProjection()
    {
        Console.WriteLine("Gnomonic projection");

        // Boresight due south, 45 deg up; frame up = local up component, right = perpendicular.
        var bore = SkyVector.FromHorizontal(45, 180);
        var right = SkyVector.FromHorizontal(0, 270);
        var up = SkyVector.FromHorizontal(45, 0);
        var proj = new GnomonicProjection(bore, up, right, FovDeg, W, H);

        proj.TryProject(bore, out double cx, out double cy);
        Check("boresight lands at frame centre", Math.Abs(cx - W / 2.0) < 1e-6 && Math.Abs(cy - H / 2.0) < 1e-6,
              $"({cx:F4}, {cy:F4}) vs ({W / 2.0}, {H / 2.0})");

        // Offsets must be taken by rotating the boresight toward an axis, NOT by nudging the
        // azimuth: at altitude h, one degree of azimuth is only cos(h) degrees on the sky.
        Func<double, SkyVector> offsetRight = deg =>
        {
            double a = deg * Math.PI / 180.0;
            return SkyVector.Normalized(
                Math.Cos(a) * bore.X + Math.Sin(a) * right.X,
                Math.Cos(a) * bore.Y + Math.Sin(a) * right.Y,
                Math.Cos(a) * bore.Z + Math.Sin(a) * right.Z);
        };

        proj.TryProject(offsetRight(FovDeg / 2), out double ex, out double ey);
        Check("half-field offset lands on the frame edge", Math.Abs(ex - W) < 0.6,
              $"x = {ex:F2}, expected {W}");

        proj.TryProject(offsetRight(0.001), out double nx, out double ny);
        double measured = 0.001 * 3600.0 / (nx - cx);
        Check("measured plate scale matches the optics", Math.Abs(measured - PlateScale) / PlateScale < 0.001,
              $"{measured:F4}\"/px vs {PlateScale:F4}\"/px");

        Check("behind the camera is rejected", !proj.TryProject(SkyVector.FromHorizontal(-45, 0), out _, out _), "");
        Console.WriteLine();
    }

    // --- Photometry ---------------------------------------------------------
    static void TestPhotometry()
    {
        Console.WriteLine("Stellar photometry");

        // At the V effective wavelength the colour term must vanish exactly, whatever the star.
        double term = StellarPhotometry.ColorTerm(StellarPhotometry.JohnsonVWavelengthMeters, 5772);
        Check("colour term is exactly 1 at Johnson V", Math.Abs(term - 1.0) < 1e-9, $"{term:F12}");

        // A hot star must be relatively brighter in the blue than a cool one, and vice versa.
        double blueHot = StellarPhotometry.ColorTerm(450e-9, 20000);
        double blueCool = StellarPhotometry.ColorTerm(450e-9, 3500);
        double redHot = StellarPhotometry.ColorTerm(650e-9, 20000);
        double redCool = StellarPhotometry.ColorTerm(650e-9, 3500);
        Check("hot star is bluer than cool star", blueHot / redHot > blueCool / redCool,
              $"B/R hot {blueHot / redHot:F3} vs cool {blueCool / redCool:F3}");

        // Absolute anchor: a V=0 star through the RC20's luminance filter for 1 s.
        double e0 = PhotonFluxModel.CollectedElectrons(0.0, LumBandwidth, AreaCm2, QE, 1.0, 1.0);
        Check("V=0 star gives a plausible 1 s electron count", e0 > 1e9 && e0 < 1e10, $"{e0:E3} e-/s");

        // Limiting magnitude of a 30 s sub against a dark sky, at SNR = 5.
        double t = 30.0;
        double skyPerPx = SkyBrightnessModel.ElectronsPerPixelPerSecond(
            SkyBrightnessModel.DarkSkyZenithVMagPerArcsec2, PlateScale, LumBandwidth, AreaCm2, QE, 1.0);
        double seeingFwhm = 1.5;
        double npix = Math.PI * Math.Pow(seeingFwhm / PlateScale, 2) / 4.0;
        double limit = SolveLimitingMag(t, skyPerPx * t, npix, 5.0);
        // 22.5 is what the model says, and it is roughly 1.5 mag deeper than a real 0.51 m
        // instrument achieves; PhotonFluxModel carries no optical throughput term (mirror
        // reflectivity, filter peak transmission, window), so it collects every photon that
        // enters the aperture. That omission is shared by the planets, so it does not distort
        // the star field RELATIVE to its subject; it is recorded here rather than papered over.
        Check("30 s sub limiting magnitude is in the expected range", limit > 21 && limit < 24,
              $"V_lim = {limit:F2} (sky {skyPerPx:F2} e-/px/s over {npix:F1} px)");
        Console.WriteLine();
    }

    /// <summary>CCD equation solved for the magnitude giving the requested SNR.</summary>
    static double SolveLimitingMag(double t, double skyElectrons, double npix, double snr)
    {
        double lo = 5, hi = 30;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            double s = PhotonFluxModel.CollectedElectrons(mid, LumBandwidth, AreaCm2, QE, t, 1.0);
            double noise = Math.Sqrt(s + npix * (skyElectrons + Dark * t + ReadNoise * ReadNoise));
            if (s / noise > snr) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    // --- Sky ----------------------------------------------------------------
    static void TestSkyBrightness()
    {
        Console.WriteLine("Sky brightness");

        double atZenith = SkyBrightnessModel.AirglowVanRhijnFactor(0, 6.0e5);
        double at60 = SkyBrightnessModel.AirglowVanRhijnFactor(60, 6.0e5);
        double at85 = SkyBrightnessModel.AirglowVanRhijnFactor(85, 6.0e5);
        Check("van Rhijn is 1 at the zenith", Math.Abs(atZenith - 1.0) < 1e-9, $"{atZenith:F6}");
        Check("van Rhijn grows toward the horizon", at60 > 1.1 && at85 > at60 && at85 < 12,
              $"60deg {at60:F3}, 85deg {at85:F3}");

        double full = SkyBrightnessModel.MoonlightVMagPerArcsec2(1.0);
        double quarter = SkyBrightnessModel.MoonlightVMagPerArcsec2(0.25);
        Check("full moon is ~3 mag above dark sky",
              Math.Abs(full - SkyBrightnessModel.FullMoonVMagPerArcsec2) < 1e-9 && full < SkyBrightnessModel.DarkSkyZenithVMagPerArcsec2 - 2.5,
              $"full {full:F2}, quarter {quarter:F2}, dark {SkyBrightnessModel.DarkSkyZenithVMagPerArcsec2:F2}");
        Check("no moon contributes nothing", double.IsPositiveInfinity(SkyBrightnessModel.MoonlightVMagPerArcsec2(0)), "");

        Check("twilight ends exactly at -18 deg",
              double.IsPositiveInfinity(SkyBrightnessModel.TwilightVMagPerArcsec2(-18.0))
              && SkyBrightnessModel.TwilightVMagPerArcsec2(-14.0) < SkyBrightnessModel.DarkSkyZenithVMagPerArcsec2,
              $"-14deg -> {SkyBrightnessModel.TwilightVMagPerArcsec2(-14):F2} mag/arcsec2");

        // Sky rate must scale with pixel area, not with pixel count.
        double r1 = SkyBrightnessModel.ElectronsPerPixelPerSecond(21.7, 1.0, LumBandwidth, AreaCm2, QE, 1.0);
        double r2 = SkyBrightnessModel.ElectronsPerPixelPerSecond(21.7, 2.0, LumBandwidth, AreaCm2, QE, 1.0);
        Check("sky scales with pixel solid angle", Math.Abs(r2 / r1 - 4.0) < 1e-9, $"ratio {r2 / r1:F6} for 2x the plate scale");
        Console.WriteLine();
    }

    static void TestExtinction()
    {
        Console.WriteLine("Wavelength-dependent extinction");
        double kB = AtmosphericImagingNoise.ExtinctionMagPerAirmassAt(445e-9, SiteAltitude);
        double kV = AtmosphericImagingNoise.ExtinctionMagPerAirmassAt(551e-9, SiteAltitude);
        double kR = AtmosphericImagingNoise.ExtinctionMagPerAirmassAt(658e-9, SiteAltitude);
        Check("k_B > k_V > k_R", kB > kV && kV > kR, $"B {kB:F3}, V {kV:F3}, R {kR:F3} mag/airmass");
        Check("k at V reproduces the site's own coefficient",
              Math.Abs(AtmosphericImagingNoise.ExtinctionMagPerAirmassAt(StellarPhotometry.JohnsonVWavelengthMeters, SiteAltitude)
                       - AtmosphericImagingNoise.ExtinctionMagPerAirmass) < 1e-9,
              $"{AtmosphericImagingNoise.ExtinctionMagPerAirmassAt(StellarPhotometry.JohnsonVWavelengthMeters, SiteAltitude):F4}");
        double kHigh = AtmosphericImagingNoise.ExtinctionMagPerAirmassAt(551e-9, 2400.0);
        Check("a higher site is more transparent", kHigh < kV, $"2400 m: {kHigh:F3} vs sea level {kV:F3}");
        Console.WriteLine();
    }

    // --- Renderer -----------------------------------------------------------
    static void TestFluxConservation()
    {
        Console.WriteLine("Star field renderer");

        var plane = new float[W * H];
        StarFieldRenderer.Deposit(plane, W, H, new PointSource
        {
            SignalFraction = 1.0,
            StartPixelX = 100.37, StartPixelY = 200.62,
            EndPixelX = 100.37, EndPixelY = 200.62,
        });
        double sum = 0; foreach (var v in plane) sum += v;
        Check("a stationary source conserves its flux", Math.Abs(sum - 1.0) < 1e-5, $"sum = {sum:F8}");

        // Sub-pixel position must actually split across pixels rather than snap to one.
        int nonzero = 0; foreach (var v in plane) if (v > 0) nonzero++;
        Check("sub-pixel position splits across neighbours", nonzero == 4, $"{nonzero} pixels lit");

        Array.Clear(plane, 0, plane.Length);
        StarFieldRenderer.Deposit(plane, W, H, new PointSource
        {
            SignalFraction = 1.0,
            StartPixelX = 300, StartPixelY = 300,
            EndPixelX = 420, EndPixelY = 360,
        });
        sum = 0; nonzero = 0;
        foreach (var v in plane) { sum += v; if (v > 0) nonzero++; }
        Check("a trailed source conserves its flux", Math.Abs(sum - 1.0) < 1e-5, $"sum = {sum:F8}");
        Check("the trail is continuous", nonzero > 120, $"{nonzero} pixels along a 134 px streak");

        Array.Clear(plane, 0, plane.Length);
        StarFieldRenderer.Deposit(plane, W, H, new PointSource
        {
            SignalFraction = 1.0, StartPixelX = -50, StartPixelY = -50, EndPixelX = -40, EndPixelY = -40,
        });
        sum = 0; foreach (var v in plane) sum += v;
        Check("an off-sensor source deposits nothing", sum == 0.0, $"sum = {sum}");
        Console.WriteLine();
    }

    // --- Whole chain --------------------------------------------------------
    /// <summary>Catalogue -> projection -> deposit -> trail, on a real patch of sky.</summary>
    static void TestEndToEnd(string path)
    {
        Console.WriteLine("End to end: a real field, tracked and untracked");
        var catalog = new RenderedStarCatalog();
        catalog.Load(path);

        // Observatory on the equator; a 6 h rotation period, like Kerbin's.
        const double lat = 0.0, rotationPeriod = 21600.0;
        double meridianRa = 84.0;   // looking near Orion, a genuinely rich field

        // Boresight on the meridian, 60 deg up, with a level frame.
        double alt = 60.0, az = 180.0;
        var bore = SkyVector.FromHorizontal(alt, az);
        var right = SkyVector.FromHorizontal(0, az - 90);
        var up = SkyVector.Normalized(
            bore.Y * right.Z - bore.Z * right.Y,
            bore.Z * right.X - bore.X * right.Z,
            bore.X * right.Y - bore.Y * right.X);
        var proj = new GnomonicProjection(bore, up, right, FovDeg, W, H);

        SkyCoordinates.HorizontalToEquatorial(alt, az, meridianRa, lat, out double ra, out double dec);
        var stars = new List<RenderedStar>();
        catalog.Search(ra, dec, proj.SearchRadiusDeg(0.05), 99.0, stars);
        Console.WriteLine($"          pointing RA {ra:F3} Dec {dec:F3}; {stars.Count} stars within the search cone");

        foreach (double exposure in new[] { 1.0, 30.0 })
        {
            var plane = new float[W * H];
            double endMer = meridianRa;
            double startMer = meridianRa - 360.0 * exposure / rotationPeriod;

            double sky = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                SkyBrightnessModel.DarkSkyZenithVMagPerArcsec2, PlateScale, LumBandwidth, AreaCm2, QE, 1.0) * exposure;
            double floor = 0.05 * (Math.Sqrt(sky + Dark * exposure) + ReadNoise) / FullWell;

            int drawn = StarFieldRenderer.DepositStars(
                plane, W, H, stars, proj, startMer, endMer, lat, FullWell, floor,
                st => StellarPhotometry.CollectedElectrons(st.VMag, st.ColorIndexBV, LumWavelength,
                                                          LumBandwidth, AreaCm2, QE, exposure, 1.0));

            double sum = 0; int lit = 0; float peak = 0;
            foreach (var v in plane) { sum += v; if (v > 0) { lit++; if (v > peak) peak = v; } }
            double trailPx = 360.0 * exposure / rotationPeriod * 3600.0 / PlateScale;

            Console.WriteLine($"          {exposure,4:F0} s: {drawn} drawn, {lit} pixels lit, "
                            + $"peak {peak * 100:F1}% of full well, expected trail ~{trailPx:F0} px");
            Check($"{exposure:F0} s exposure draws stars", drawn > 0 && lit > 0, "");
            // Even a 1 s unguided sub trails visibly on a world with a 6 h day, because the sky
            // sweeps four times faster than Earth's, so 1 s is already 54 px at this plate
            // scale. The streak must be a good fraction of that predicted length, allowing for
            // the part of it that runs off the sensor.
            Check($"{exposure:F0} s trail length matches the sky's own rotation",
                  // Bilinear splatting gives a diagonal streak roughly two pixels of width, so
                  // the lit count runs up to several times the trail's own length; the lower
                  // bound catches a streak that ran off the sensor or was never drawn.
                  lit > drawn * trailPx * 0.2 && lit < drawn * trailPx * 4.5,
                  $"{lit} lit for {drawn} stars, {lit / (double)drawn:F0} px each vs {trailPx:F0} predicted");
        }

        // Tracked: no trail at all, every star a compact point.
        var tracked = new float[W * H];
        int n2 = StarFieldRenderer.DepositStars(tracked, W, H, stars, proj, meridianRa, meridianRa, lat,
            FullWell, 0.0,
            st => StellarPhotometry.CollectedElectrons(st.VMag, st.ColorIndexBV, LumWavelength,
                                                      LumBandwidth, AreaCm2, QE, 30.0, 1.0));
        int litTracked = 0; foreach (var v in tracked) if (v > 0) litTracked++;
        Check("autoguided exposure leaves no trail", litTracked <= 4 * n2,
              $"{litTracked} pixels for {n2} stars (4 per star = pure sub-pixel splitting)");
        Console.WriteLine();
    }

    // --- Catalogue ----------------------------------------------------------
    static void TestCatalog(string path)
    {
        Console.WriteLine("Tycho-2 catalogue");
        var catalog = new RenderedStarCatalog();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        catalog.Load(path);
        sw.Stop();
        Check("catalogue loads", catalog.IsLoaded && catalog.Count > 2_500_000,
              $"{catalog.Count} stars in {sw.ElapsedMilliseconds} ms");

        // All-sky density from a large random sample of cones, against the published total.
        var rng = new Random(7);
        var results = new List<RenderedStar>();
        double radius = 1.0;
        int trials = 200, total = 0;
        sw.Restart();
        for (int i = 0; i < trials; i++)
        {
            results.Clear();
            double ra = rng.NextDouble() * 360.0;
            double dec = Math.Asin(rng.NextDouble() * 2 - 1) * 180.0 / Math.PI;
            catalog.Search(ra, dec, radius, 99.0, results);
            total += results.Count;
        }
        sw.Stop();
        double area = 2 * Math.PI * (1 - Math.Cos(radius * Math.PI / 180.0)) * Math.Pow(180.0 / Math.PI, 2);
        double density = total / (double)trials / area;
        double expected = catalog.Count / 41252.96;
        Check("cone-search density matches the whole catalogue", Math.Abs(density - expected) / expected < 0.35,
              $"{density:F1}/deg2 measured vs {expected:F1}/deg2 all-sky ({sw.ElapsedMilliseconds} ms / {trials} searches)");

        // Poles are where a naive RA bracket breaks; the count there must still be sane.
        results.Clear(); catalog.Search(0, 89.7, 1.0, 99.0, results);
        int nearPole = results.Count;
        results.Clear(); catalog.Search(180, 89.7, 1.0, 99.0, results);
        Check("searches near the pole agree from opposite RA", Math.Abs(nearPole - results.Count) < Math.Max(20, nearPole),
              $"RA 0h: {nearPole}, RA 12h: {results.Count}");

        // The RA=0 wrap is the other classic failure; a cone straddling it must find both halves.
        results.Clear(); catalog.Search(0.0, 0.0, 0.5, 99.0, results);
        int wrap = results.Count;
        int above = 0, below = 0;
        foreach (var s in results) { if (s.RaDeg < 180) above++; else below++; }
        Check("a cone straddling RA=0h finds stars on both sides", above > 0 && below > 0,
              $"{above} at RA<180, {below} at RA>180 ({wrap} total)");

        // What an actual RC20 frame contains.
        results.Clear();
        double frameRadius = Math.Sqrt(FovDeg * FovDeg + Math.Pow(FovDeg * H / W, 2)) / 2;
        int sum = 0;
        for (int i = 0; i < 200; i++)
        {
            results.Clear();
            catalog.Search(rng.NextDouble() * 360, Math.Asin(rng.NextDouble() * 2 - 1) * 180 / Math.PI,
                           frameRadius, 99.0, results);
            sum += results.Count;
        }
        Console.WriteLine($"          typical RC20 frame: {sum / 200.0:F2} Tycho-2 stars "
                        + $"(vs {9110 / 41252.96 * Math.PI * frameRadius * frameRadius:F4} for the Bright Star Catalogue)");
        Console.WriteLine();
    }
}
