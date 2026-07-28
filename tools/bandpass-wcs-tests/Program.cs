using System;
using ExoInstruments.Core;

/// <summary>
/// Headless checks for the two pieces of Core added alongside the optical-throughput and
/// integrated-bandpass work: SystemResponse (SystemBandpass.cs) and FitsWcs.
///
/// Deliberately separate from tools/skyfield-tests: that harness covers the star-field geometry
/// and photometry chain, and its committed copy still calls the pre-electrons API
/// (PointSource.SignalFraction, a FullWell argument to DepositStars), so it does not build as
/// committed. These checks are self-contained so they can be run and trusted on their own.
///
/// Run: dotnet run -p:Core=../../ExoInstruments/Core
/// </summary>
class Program
{
    // Real RC20 + ZWO ASI294MM Pro, matching VisualTelescopeCatalog.Rc20.
    const double Aperture = 0.51, Obstruction = 0.39, FocalLength = 0.51 * 6.8;
    const double PixelPitch = 4.63e-6 * 4;
    const int W = 4144 / 4, H = 2822 / 4;
    const double QE = 0.90;
    const double LumBandwidth = 2650.0, LumWavelength = 552.5e-9;
    const double SiteAltitude = 650.0;

    // The two VLT instruments' real optical trains, per VisualTelescopeCatalog.
    const double AlReflectivity = 0.87;
    static double Fors2Optics => Math.Pow(AlReflectivity, 2);            // Cassegrain: M1 + M2
    static double SphereOptics => Math.Pow(AlReflectivity, 3) * 0.79;    // Nasmyth: M1 + M2 + M3, then the 79% beam splitter

    static double PlateScale => PixelPitch / FocalLength * (180.0 / Math.PI) * 3600.0;
    static double FovDeg => W * PlateScale / 3600.0;
    static double AreaCm2 => Math.PI * Math.Pow(Aperture * 100 / 2, 2) * (1 - Obstruction * Obstruction);

    static int failures = 0;

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) failures++;
    }

    static void Main()
    {
        Console.WriteLine($"RC20 @ bin4: plate scale {PlateScale:F3}\"/px, field {FovDeg:F3} x {FovDeg * H / W:F3} deg, area {AreaCm2:F0} cm2");
        Console.WriteLine();

        TestReduction();
        TestColour();
        TestThroughput();
        TestQeCurve();
        TestExtinctionInsideBand();
        TestCurve();
        TestWcs();
        TestRadialPsfProfile();
        TestPupilDiffraction();
        TestSpiderKernels();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    /// <summary>A response with no curve, no throughput loss and no atmosphere: the grey limit.</summary
    static SystemResponse GreyResponse(double centralM, double widthA, double grey, double airmass)
        => new SystemResponse(centralM, widthA, grey, null, QE, airmass, SiteAltitude);

    // ------------------------------------------------------------------------
    /// <summary>
    /// The central claim: the integral is a GENERALISATION of the grey-band formula it replaced,
    /// not a different model. With a flat source spectrum, a scalar QE and no atmosphere, it must
    /// reproduce FWHM * QE * transmission exactly.
    /// </summary>
    static void TestReduction()
    {
        Console.WriteLine("Reduction to the superseded grey-band model");

        var response = GreyResponse(LumWavelength, LumBandwidth, 1.0, 1.0);
        double expected = LumBandwidth * QE;
        double got = response.EffectiveWidthAngstromFlatNoExtinction;
        Check("flat spectrum, grey QE, no air: W = FWHM x QE",
              Math.Abs(got / expected - 1.0) < 1e-12, $"{got:F6} vs {expected:F6} Angstrom");

        // And the same statement at the level the pipeline actually uses: electron counts.
        double viaIntegral = PhotonFluxModel.CollectedElectrons(12.0, got, AreaCm2, 30.0);
        double viaGrey = PhotonFluxModel.CollectedElectronsGreyBand(12.0, LumBandwidth, AreaCm2, QE, 30.0, 1.0);
        Check("electron counts agree with the grey-band form",
              Math.Abs(viaIntegral / viaGrey - 1.0) < 1e-12, $"{viaIntegral:E6} vs {viaGrey:E6} e-");

        // A narrow filter centred exactly at Johnson V must be colour-blind, because that is
        // where the magnitude normalisation is applied: the shape ratio is 1 there by definition.
        var atV = GreyResponse(StellarPhotometry.JohnsonVWavelengthMeters, 1.0, 1.0, 1.0);
        double hot = atV.EffectiveWidthAngstromForTemperatureNoExtinction(30000.0);
        double cool = atV.EffectiveWidthAngstromForTemperatureNoExtinction(2800.0);
        double flat = atV.EffectiveWidthAngstromFlatNoExtinction;
        Check("a filter at Johnson V has no colour term at all",
              Math.Abs(hot / flat - 1.0) < 1e-6 && Math.Abs(cool / flat - 1.0) < 1e-6,
              $"30000K {hot / flat:F9}, 2800K {cool / flat:F9}");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------------
    /// <summary>The colour term must now fall out of the integral, with the right sign and sense.</summary>
    static void TestColour()
    {
        Console.WriteLine("Colour, as a consequence of the integral");

        // The RC20's own real LRGB centres and even-third widths.
        var blue = GreyResponse(464.2e-9, 2650.0 / 3.0, 1.0, 1.0);
        var red = GreyResponse(640.8e-9, 2650.0 / 3.0, 1.0, 1.0);

        double hotB = blue.EffectiveWidthAngstromForTemperatureNoExtinction(20000.0);
        double hotR = red.EffectiveWidthAngstromForTemperatureNoExtinction(20000.0);
        double coolB = blue.EffectiveWidthAngstromForTemperatureNoExtinction(3500.0);
        double coolR = red.EffectiveWidthAngstromForTemperatureNoExtinction(3500.0);

        Check("a hot star is bluer than a cool one", hotB / hotR > coolB / coolR,
              $"B/R: 20000K {hotB / hotR:F3} vs 3500K {coolB / coolR:F3}");
        Check("an M star is genuinely red-dominated", coolR / coolB > 2.0, $"R/B = {coolR / coolB:F2} at 3500 K");

        // Agreement with the two-wavelength ColorTerm the model used before, on a band narrow
        // enough that the integral and a single sample must coincide. The residual is the colour
        // table's own log-temperature interpolation (see SystemResponse.TableEntries), not a
        // difference between the two models: reading the table is what buys O(1) cost per star.
        var narrow = GreyResponse(650e-9, 1.0, 1.0, 1.0);
        double worstRatio = 0.0;
        foreach (double teff in new[] { 2800.0, 3500.0, 4000.0, 5772.0, 9000.0, 20000.0 })
        {
            double fromIntegral = narrow.EffectiveWidthAngstromForTemperatureNoExtinction(teff)
                                / narrow.EffectiveWidthAngstromFlatNoExtinction;
            double fromRatio = StellarPhotometry.ColorTerm(650e-9, teff);
            double error = Math.Abs(fromIntegral / fromRatio - 1.0);
            if (error > worstRatio) worstRatio = error;
        }
        Check("on a narrow band the integral equals the old two-wavelength ratio",
              worstRatio < 2e-4, $"worst disagreement {worstRatio * 100:F4}% over 2800-20000 K");

        // The Sun's own shape must be mildly red-favouring relative to flat, and much less
        // extreme than an M dwarf's -- a sanity check on the reflected-sunlight spectrum used for
        // every planet in the frame.
        double sunB = blue.EffectiveWidthAngstromForTemperatureNoExtinction(SourceSpectra.SolarPhotosphereTemperatureK);
        double sunR = red.EffectiveWidthAngstromForTemperatureNoExtinction(SourceSpectra.SolarPhotosphereTemperatureK);
        Check("reflected sunlight sits between a hot star and an M dwarf",
              sunR / sunB > hotR / hotB && sunR / sunB < coolR / coolB,
              $"R/B: hot {hotR / hotB:F3} < Sun {sunR / sunB:F3} < M {coolR / coolB:F3}");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------------
    /// <summary>Optical throughput: the factor that was missing entirely.</summary>
    static void TestThroughput()
    {
        Console.WriteLine("Optical throughput");

        double rc20 = Math.Pow(AlReflectivity, 2);
        Check("RC20's two aluminium surfaces give 0.757", Math.Abs(rc20 - 0.7569) < 1e-4, $"{rc20:F4}");
        Check("FORS2 (Cassegrain, 2 mirrors) beats SPHERE (Nasmyth + beam splitter)",
              Fors2Optics > SphereOptics,
              $"FORS2 {Fors2Optics:F4} vs SPHERE {SphereOptics:F4}");

        // Ma & Cai state the consequence explicitly: with aluminium, one extra mirror costs 13%.
        double extraMirrorLoss = 1.0 - AlReflectivity;
        Check("an extra aluminium mirror costs 13% of the light",
              Math.Abs(extraMirrorLoss - 0.13) < 1e-9, $"{extraMirrorLoss * 100:F1}%");

        // W must be strictly linear in the grey factor, since it multiplies the integrand.
        double wFull = GreyResponse(LumWavelength, LumBandwidth, 1.0, 1.0).EffectiveWidthAngstromFlatNoExtinction;
        double wLossy = GreyResponse(LumWavelength, LumBandwidth, rc20, 1.0).EffectiveWidthAngstromFlatNoExtinction;
        Check("W is linear in throughput", Math.Abs(wLossy / wFull - rc20) < 1e-12,
              $"ratio {wLossy / wFull:F9} vs {rc20:F9}");

        // What it does to the limiting magnitude, which is the point of the whole exercise: the
        // pipeline used to reach about 1.5 mag deeper than a real instrument of this aperture.
        double deltaMag = -2.5 * Math.Log10(rc20);
        Check("throughput makes the RC20's limiting magnitude ~0.3 mag shallower",
              deltaMag > 0.25 && deltaMag < 0.35, $"{deltaMag:F3} mag");
        double sphereDelta = -2.5 * Math.Log10(SphereOptics);
        Check("and SPHERE's about 0.7 mag shallower", sphereDelta > 0.6 && sphereDelta < 0.8, $"{sphereDelta:F3} mag");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------------
    /// <summary>
    /// The QE curve is the second half of the point. FORS2's blue filter sits where its detector
    /// is at 58%, not its 86% peak, and the integral has to see that.
    /// </summary>
    static void TestQeCurve()
    {
        Console.WriteLine("Detector QE curve, integrated across the band");

        var fors2Qe = new SpectralCurve(
            new[] { 400.0, 500.0, 600.0, 700.0, 800.0, 900.0 },
            new[] { 0.58, 0.74, 0.86, 0.83, 0.66, 0.39 });

        Check("the curve reproduces its own published points",
              Math.Abs(fors2Qe.At(600e-9) - 0.86) < 1e-12 && Math.Abs(fors2Qe.At(400e-9) - 0.58) < 1e-12,
              $"600nm {fors2Qe.At(600e-9):F3}, 400nm {fors2Qe.At(400e-9):F3}");
        Check("it interpolates between them", Math.Abs(fors2Qe.At(450e-9) - 0.66) < 1e-9, $"450nm {fors2Qe.At(450e-9):F4}");
        Check("and holds flat outside the measured range rather than extrapolating",
              Math.Abs(fors2Qe.At(200e-9) - 0.58) < 1e-12 && Math.Abs(fors2Qe.At(2000e-9) - 0.39) < 1e-12,
              "200nm and 2000nm clamp to the end values");

        // b_HIGH: 440nm, 103.5nm FWHM (ESO's own current figures).
        const double bCentral = 440e-9, bWidth = 1035.0;
        double withCurve = new SystemResponse(bCentral, bWidth, 1.0, fors2Qe, 0.86, 1.0, 2635.0)
                               .EffectiveWidthAngstromFlatNoExtinction;
        double withPeak = new SystemResponse(bCentral, bWidth, 1.0, null, 0.86, 1.0, 2635.0)
                               .EffectiveWidthAngstromFlatNoExtinction;
        double ratio = withPeak / withCurve;
        Check("using the peak QE overstated the blue band by ~1.4x",
              ratio > 1.3 && ratio < 1.5, $"peak/curve = {ratio:F3}");

        // At the peak itself the two must nearly agree, which confirms the difference above is
        // the curve's shape and not a normalisation error.
        double atPeakCurve = new SystemResponse(600e-9, 200.0, 1.0, fors2Qe, 0.86, 1.0, 2635.0)
                                 .EffectiveWidthAngstromFlatNoExtinction;
        double atPeakScalar = new SystemResponse(600e-9, 200.0, 1.0, null, 0.86, 1.0, 2635.0)
                                  .EffectiveWidthAngstromFlatNoExtinction;
        Check("at the QE peak the curve and the peak figure agree",
              Math.Abs(atPeakScalar / atPeakCurve - 1.0) < 0.02,
              $"ratio {atPeakScalar / atPeakCurve:F4} on a 20nm band at 600nm");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------------
    /// <summary>Extinction integrated across the band rather than sampled at its centre.</summary>
    static void TestExtinctionInsideBand()
    {
        Console.WriteLine("Extinction inside the integral");

        var lum = GreyResponse(LumWavelength, LumBandwidth, 1.0, 2.0);
        Check("air costs light", lum.EffectiveWidthAngstromFlat < lum.EffectiveWidthAngstromFlatNoExtinction,
              $"{lum.EffectiveWidthAngstromFlat:F1} vs {lum.EffectiveWidthAngstromFlatNoExtinction:F1} Angstrom");

        double w1 = GreyResponse(LumWavelength, LumBandwidth, 1.0, 1.0).EffectiveWidthAngstromFlat;
        double w2 = GreyResponse(LumWavelength, LumBandwidth, 1.0, 2.0).EffectiveWidthAngstromFlat;
        double w3 = GreyResponse(LumWavelength, LumBandwidth, 1.0, 3.0).EffectiveWidthAngstromFlat;
        Check("and monotonically more of it with airmass", w1 > w2 && w2 > w3,
              $"X=1 {w1:F1}, X=2 {w2:F1}, X=3 {w3:F1}");

        // A blue band must lose more than a red one through the same air.
        double blueLoss = 1.0 - GreyResponse(464.2e-9, 883.3, 1.0, 2.0).EffectiveWidthAngstromFlat
                              / GreyResponse(464.2e-9, 883.3, 1.0, 1.0).EffectiveWidthAngstromFlat;
        double redLoss = 1.0 - GreyResponse(640.8e-9, 883.3, 1.0, 2.0).EffectiveWidthAngstromFlat
                             / GreyResponse(640.8e-9, 883.3, 1.0, 1.0).EffectiveWidthAngstromFlat;
        Check("blue loses more to the air than red", blueLoss > redLoss * 1.5,
              $"at X=2: blue {blueLoss * 100:F1}% vs red {redLoss * 100:F1}%");

        // The reason this had to move inside the integral: on the widest band in the roster
        // (FORS2 unfiltered, 7700 Angstrom) the coefficient varies by a factor of three across it,
        // so evaluating at the centre and integrating give measurably different answers.
        const double wideCentral = 715e-9, wideWidth = 7700.0;
        double integrated = new SystemResponse(wideCentral, wideWidth, 1.0, null, 1.0, 2.0, 2635.0)
                                .EffectiveWidthAngstromFlat;
        double atCentre = wideWidth * AtmosphericImagingNoise.ExtinctionTransmissionAt(2.0, wideCentral, 2635.0);
        Check("on a 7700 Angstrom band, integrating differs from sampling the centre",
              Math.Abs(integrated / atCentre - 1.0) > 0.01,
              $"integrated {integrated:F1} vs centre-sampled {atCentre:F1} ({(integrated / atCentre - 1.0) * 100:+0.0;-0.0}%)");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------------
    static void TestCurve()
    {
        Console.WriteLine("Spectral curve guards");
        bool threwOnUnsorted = false;
        try { new SpectralCurve(new[] { 500.0, 400.0 }, new[] { 0.5, 0.6 }); }
        catch (ArgumentException) { threwOnUnsorted = true; }
        Check("an out-of-order table is rejected rather than silently sorted", threwOnUnsorted, "");

        bool threwOnShort = false;
        try { new SpectralCurve(new[] { 500.0 }, new[] { 0.5 }); }
        catch (ArgumentException) { threwOnShort = true; }
        Check("a single-point table is rejected", threwOnShort, "");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------------
    /// <summary>
    /// The WCS must describe the very image the pipeline built. The decisive check is a round
    /// trip: place a direction on the sky, let GnomonicProjection put it on the sensor, then
    /// recover its coordinates from the exported header's own CRVAL/CRPIX/CD by the standard
    /// inverse TAN, and compare.
    /// </summary>
    static void TestWcs()
    {
        Console.WriteLine("FITS world coordinate system");

        const double lat = 28.6;          // Cape Canaveral, where Real Solar System puts the space centre
        const double meridianRa = 84.0;   // looking near Orion
        double alt = 60.0, az = 150.0;

        var bore = SkyVector.FromHorizontal(alt, az);
        var right = SkyVector.FromHorizontal(0, az - 90);
        var up = SkyVector.Normalized(
            bore.Y * right.Z - bore.Z * right.Y,
            bore.Z * right.X - bore.X * right.Z,
            bore.X * right.Y - bore.Y * right.X);
        var proj = new GnomonicProjection(bore, up, right, FovDeg, W, H);

        FitsWcs wcs = FitsWcs.Build(proj, meridianRa, lat);
        Check("the WCS resolves", wcs.IsValid, "");
        if (!wcs.IsValid) { Console.WriteLine(); return; }

        SkyCoordinates.HorizontalToEquatorial(alt, az, meridianRa, lat, out double boreRa, out double boreDec);
        Check("CRVAL is the boresight's own RA/Dec",
              Math.Abs(wcs.ReferenceRaDeg - boreRa) < 1e-9 && Math.Abs(wcs.ReferenceDecDeg - boreDec) < 1e-9,
              $"({wcs.ReferenceRaDeg:F6}, {wcs.ReferenceDecDeg:F6})");

        // FITS puts pixel centres on integers starting at 1, while the renderer's pixel i spans
        // [i, i+1). The half-pixel between the two conventions is exactly the bug that would put
        // every plate solve half a pixel out and never be noticed.
        Check("CRPIX carries the half-pixel FITS offset",
              Math.Abs(wcs.ReferencePixelX - (W / 2.0 + 0.5)) < 1e-6
              && Math.Abs(wcs.ReferencePixelY - (H / 2.0 + 0.5)) < 1e-6,
              $"({wcs.ReferencePixelX:F3}, {wcs.ReferencePixelY:F3}) for a {W}x{H} frame");

        Check("the CD matrix reproduces the instrument's own plate scale",
              Math.Abs(wcs.ScaleXArcsecPerPixel / PlateScale - 1.0) < 1e-4
              && Math.Abs(wcs.ScaleYArcsecPerPixel / PlateScale - 1.0) < 1e-4,
              $"{wcs.ScaleXArcsecPerPixel:F4} and {wcs.ScaleYArcsecPerPixel:F4} vs {PlateScale:F4} \"/px");

        // Round trip, at the centre and at all four corners. TAN is an exact tangent plane and
        // the pixel-to-plane map is exactly linear, so the CD matrix taken at the reference point
        // is exact everywhere on the sensor -- the corners must round-trip as well as the centre.
        double worstArcsec = 0.0;
        foreach (var probe in new[]
                 {
                     new { X = W * 0.5, Y = H * 0.5, Where = "centre" },
                     new { X = 2.0, Y = 2.0, Where = "bottom-left" },
                     new { X = W - 2.0, Y = 2.0, Where = "bottom-right" },
                     new { X = 2.0, Y = H - 2.0, Where = "top-left" },
                     new { X = W - 2.0, Y = H - 2.0, Where = "top-right" },
                 })
        {
            // Where does the header say this pixel looked?
            DeprojectTan(wcs, probe.X, probe.Y, out double ra, out double dec);

            // And where does the pipeline's own projection put that direction?
            HorizontalCoordinates hc = SkyCoordinates.EquatorialToHorizontal(ra, dec, meridianRa, lat);
            proj.TryProject(SkyVector.FromHorizontal(hc.AltitudeDeg, hc.AzimuthDeg), out double px, out double py);

            double errPx = Math.Sqrt((px - probe.X) * (px - probe.X) + (py - probe.Y) * (py - probe.Y));
            double errArcsec = errPx * PlateScale;
            if (errArcsec > worstArcsec) worstArcsec = errArcsec;
            Console.WriteLine($"          {probe.Where,13}: RA {ra:F6} Dec {dec:F6}, closes to {errPx:E2} px ({errArcsec:E2}\")");
        }
        Check("header and image agree across the whole sensor, corners included",
              worstArcsec < 1e-6, $"worst round-trip error {worstArcsec:E3} arcsec");

        // A frame at the pole is where a naive implementation that steps in right ascension
        // divides by cos(dec) and falls apart. This one steps in the tangent plane instead.
        var poleBore = SkyVector.FromHorizontal(lat, 0.0);   // the celestial pole, at altitude = latitude due north
        var poleRight = SkyVector.FromHorizontal(0, 270);
        var poleUp = SkyVector.Normalized(
            poleBore.Y * poleRight.Z - poleBore.Z * poleRight.Y,
            poleBore.Z * poleRight.X - poleBore.X * poleRight.Z,
            poleBore.X * poleRight.Y - poleBore.Y * poleRight.X);
        var poleProj = new GnomonicProjection(poleBore, poleUp, poleRight, FovDeg, W, H);
        FitsWcs poleWcs = FitsWcs.Build(poleProj, meridianRa, lat);
        Check("a field on the celestial pole still resolves",
              poleWcs.IsValid && Math.Abs(poleWcs.ReferenceDecDeg - 90.0) < 0.01
              && Math.Abs(poleWcs.ScaleXArcsecPerPixel / PlateScale - 1.0) < 1e-4,
              $"Dec {poleWcs.ReferenceDecDeg:F4}, scale {poleWcs.ScaleXArcsecPerPixel:F4} \"/px");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ RadialPsfProfile
    //
    // The high-contrast imaging display used to synthesise its stellar PSF from a Gaussian core
    // plus an invented ring envelope, while Core already computed the exact annular-pupil Airy
    // pattern for every other instrument. RadialPsfProfile removes that second model. These checks
    // establish three separate things: that the profile is the exact pattern (against closed forms
    // published for it), that pixel averaging reduces to point sampling in the fine-plate-scale
    // limit (the reducibility standard the integrated bandpass work set), and that the averaging
    // is what makes a coarse plate scale render the optics instead of the raster.

    const double ArcsecPerRad = 180.0 * 3600.0 / Math.PI;

    static void TestRadialPsfProfile()
    {
        Console.WriteLine("Direct-imaging PSF: exact annular-pupil pattern (RadialPsfProfile)");

        double D = DirectImagingSimulator.ApertureMeters;         // 39.3 m, ELT primary
        double eps = DirectImagingSimulator.ObstructionRatio;     // 11.1/39.3, ESO E-ELT optics page
        double lambda = DirectImagingSimulator.WavelengthMeters;  // 1.6 um, H band
        double lambdaOverD = lambda / D;                          // rad
        double lodArcsec = lambdaOverD * ArcsecPerRad;

        Check("the ELT pupil obstruction is ESO's 11.1 m on the 39.3 m this class already uses",
              Math.Abs(eps - 0.28244) < 1e-4,
              $"eps = {eps:F5} (11.1/39.3); on ESO's rounded 39 m it would be {11.1 / 39.0:F5}");

        Check("lambda/D in H band puts the ELT's core where the literature does",
              Math.Abs(lodArcsec - 0.008398) < 1e-5,
              $"lambda/D = {lodArcsec * 1000.0:F3} mas at 1.6 um on 39.3 m");

        // --- The pattern itself, against closed forms -------------------------------------

        // Encircled energy of an UNOBSTRUCTED pupil has the closed form 2*[1 - J0(x)^2 - J1(x)^2]
        // (Born & Wolf). This tests intensity and quadrature together, at every radius, which
        // shape-only checks near the core cannot.
        double worstEe = 0.0;
        foreach (double xTarget in new[] { 1.0, 3.8317, 7.0156, 10.1735, 20.0 })
        {
            double theta = xTarget * lambda / (Math.PI * D);
            double numeric = RadialPsfProfile.EncircledEnergy(theta, D, 0.0, lambda);
            double j0 = OpticalPsf.BesselJ0(xTarget), j1 = OpticalPsf.BesselJ1(xTarget);
            double closed = 2.0 * (1.0 - j0 * j0 - j1 * j1) * Math.Pow(lambda / (Math.PI * D), 2.0);
            worstEe = Math.Max(worstEe, Math.Abs(numeric / closed - 1.0));
        }
        Check("encircled energy reproduces the closed form 2*(1 - J0^2 - J1^2) at every radius",
              worstEe < 1e-6, $"worst relative deviation {worstEe:E2} over x = 1 .. 20");

        // The first null: textbook 1.22*lambda/D unobstructed, and measurably INWARD of it once
        // the pupil is obstructed. This is the number DirectImagingSimulator.DiffractionLimitArcsec
        // still quotes as 1.22*lambda/D; the discrepancy is real and is recorded in the reference.
        double nullClear = RadialPsfProfile.FirstNullRad(D, 0.0, lambda) / lambdaOverD;
        double nullElt = RadialPsfProfile.FirstNullRad(D, eps, lambda) / lambdaOverD;
        Check("an unobstructed pupil's first null lands on the textbook 1.22 lambda/D",
              Math.Abs(nullClear - 1.2197) < 1e-3, $"{nullClear:F4} lambda/D vs 3.8317/pi = 1.2197");
        Check("the ELT's obstruction moves its first null inward of 1.22 lambda/D",
              nullElt < nullClear - 0.02,
              $"{nullElt:F4} lambda/D ({nullElt * lodArcsec * 1000.0:F3} mas) vs {nullClear:F4} clear");

        // Obstruction narrows the core and moves energy into the rings: the classic signature.
        double fwhmClear = OpticalPsf.AiryFwhmArcsec(D, 0.0, lambda);
        double fwhmElt = OpticalPsf.AiryFwhmArcsec(D, eps, lambda);
        Check("the obstruction narrows the core, as an annular pupil must",
              fwhmElt < fwhmClear,
              $"FWHM {fwhmElt * 1000.0:F3} mas obstructed vs {fwhmClear * 1000.0:F3} mas clear");

        // --- Reducibility: pixel averaging -> point sampling ------------------------------

        // The standard this project holds new models to: the new one must provably become the old
        // one in the appropriate limit. Here the limit is a plate scale fine compared with the
        // ring period, where a pixel resolves the pattern and averaging changes nothing.
        // Reducibility is asserted twice over: that the deviation is small at a fine plate scale,
        // AND that it falls as the SQUARE of the pixel size, which is the convergence order an
        // area average must have and which a coincidentally small number would not show.
        double devCoarse = WorstReductionDeviation(lambdaOverD / 250.0, D, eps, lambda, lambdaOverD);
        double devFine = WorstReductionDeviation(lambdaOverD / 500.0, D, eps, lambda, lambdaOverD);
        double devFiner = WorstReductionDeviation(lambdaOverD / 1000.0, D, eps, lambda, lambdaOverD);
        double order1 = devCoarse / devFine, order2 = devFine / devFiner;
        Check("pixel averaging reduces to point sampling as the plate scale becomes fine",
              devFiner < 1e-6,
              $"worst absolute deviation {devFiner:E2} of peak intensity at lambda/D / 1000, over 0 .. 8 lambda/D");
        Check("and it converges at second order, as an area average must",
              Math.Abs(order1 - 4.0) < 0.2 && Math.Abs(order2 - 4.0) < 0.2,
              $"halving the pixel divides the deviation by {order1:F2} then {order2:F2}, against 4 for O(p^2)");

        // --- The one-dimensional reduction, measured against a true square pixel -----------

        // Beyond six pixels the average collapses to an integral over the pixel's radial extent.
        // Brute-force the real two-dimensional average of a square pixel -- written independently,
        // in this file -- and report the residual across the full range of plate scales the
        // display produces, from a tightly sampled core to several rings inside one pixel.
        double worstAzimuthal = 0.0, worstAt = 0.0, worstPlate = 0.0;
        foreach (double pxInLod in new[] { 0.1, 0.5, 1.5, 4.0 })
        {
            double p = pxInLod * lambdaOverD;
            for (double rLod = 0.0; rLod <= 16.0; rLod += 0.1)
            {
                double theta = rLod * lambdaOverD;
                double model = RadialPsfProfile.PixelAveragedIntensity(theta, p, D, eps, lambda);
                double square = SquarePixelAverage(theta, p, D, eps, lambda, 128, 8);
                double dev = Math.Abs(model - square);
                if (dev > worstAzimuthal) { worstAzimuthal = dev; worstAt = rLod; worstPlate = pxInLod; }
            }
        }
        Check("the profile matches a brute-force average over a real square pixel at every plate scale",
              worstAzimuthal < 1e-3,
              $"worst {worstAzimuthal:E2} of peak at {worstAt:F2} lambda/D, {worstPlate} lambda/D per pixel "
              + $"(a twentieth of one level of the display's 256)");

        // The crossover between the two regimes must not leave a visible step in the profile.
        double worstSeam = 0.0, seamPlate = 0.0;
        foreach (double pxInLod in new[] { 0.1, 0.5, 1.5, 4.0 })
        {
            double p = pxInLod * lambdaOverD;
            double inside = RadialPsfProfile.PixelAveragedIntensity(6.0 * p - 1e-6 * p, p, D, eps, lambda);
            double outside = RadialPsfProfile.PixelAveragedIntensity(6.0 * p + 1e-6 * p, p, D, eps, lambda);
            double step = Math.Abs(inside - outside);
            if (step > worstSeam) { worstSeam = step; seamPlate = pxInLod; }
        }
        // The seam cannot be made to vanish -- it IS the residual of the one-dimensional reduction,
        // evaluated at the radius where that reduction takes over. What must hold is that the
        // crossover adds nothing to the error budget the check above already bounds.
        Check("the crossover between the two regimes adds nothing to the model's own residual",
              worstSeam < 1.5e-3 && worstSeam < 2.0 * worstAzimuthal,
              $"worst step {worstSeam:E2} of peak at {seamPlate} lambda/D per pixel, against the model's "
              + $"{worstAzimuthal:E2} residual; 1.9E-003 if the crossover sat at 3 px instead of 6");

        // --- Why the averaging is required at all -----------------------------------------

        // At a coarse plate scale, point sampling lands at random on ring maxima and nulls, so
        // adjacent pixels differ by orders of magnitude for no physical reason. The averaged
        // profile varies smoothly across the same pixels. This is the aliasing the class exists to
        // remove, quantified on the pattern rather than argued.
        double coarsePixelRad = 1.5 * lambdaOverD; // deliberately coarse: several rings per pixel
        double worstPointJump = 0.0, worstAveragedJump = 0.0;
        for (int i = 2; i <= 40; i++)
        {
            double t0 = i * coarsePixelRad, t1 = (i + 1) * coarsePixelRad;
            double p0 = OpticalPsf.AiryIntensity(t0, D, eps, lambda);
            double p1 = OpticalPsf.AiryIntensity(t1, D, eps, lambda);
            double a0 = RadialPsfProfile.PixelAveragedIntensity(t0, coarsePixelRad, D, eps, lambda);
            double a1 = RadialPsfProfile.PixelAveragedIntensity(t1, coarsePixelRad, D, eps, lambda);
            worstPointJump = Math.Max(worstPointJump, Ratio(p0, p1));
            worstAveragedJump = Math.Max(worstAveragedJump, Ratio(a0, a1));
        }
        // The averaged profile still falls steeply here, and it should: beyond the core the Airy
        // envelope really does drop as theta^-3, so ~4x between pixels 1.5 lambda/D apart is
        // physics rather than aliasing. What must go is the ORDER-OF-MAGNITUDE swing that point
        // sampling shows when consecutive samples land on a ring maximum and then a null.
        Check("averaging suppresses the ring aliasing point sampling shows at a coarse plate scale",
              worstAveragedJump < 0.1 * worstPointJump,
              $"worst adjacent-pixel ratio {worstPointJump:F1}x point-sampled vs {worstAveragedJump:F2}x averaged, "
              + $"a {worstPointJump / worstAveragedJump:F0}x reduction");

        // --- The tabulated profile, as the display actually uses it ------------------------

        // A plate scale straight out of the display: a target whose planet sits 0.5" out gives a
        // 2.3" field over 400 px.
        double platePerPx = 2.3 / 400.0;
        var profile = RadialPsfProfile.Build(D, eps, lambda, platePerPx, 480.0);
        Check("the tabulated profile is built and peaks on axis",
              profile != null && profile.OnAxisPixelValue > 0.0
              && profile.AtPixelRadius(0.0) >= profile.AtPixelRadius(1.0),
              $"on-axis pixel value {profile.OnAxisPixelValue:E3} of the point peak, at {platePerPx * 1000.0:F2} mas/px "
              + $"({platePerPx / lodArcsec:F2} lambda/D per pixel)");

        // A coarse pixel genuinely dilutes the core: the peak pixel holds less of the peak
        // intensity the larger it is. This is detector physics, not a modelling loss, and it is
        // the one visible change to the finished frame.
        double prevPeak = double.PositiveInfinity;
        bool monotone = true;
        foreach (double pxInLod in new[] { 0.05, 0.25, 1.0, 2.0, 4.0 })
        {
            double v = RadialPsfProfile.PixelAveragedIntensity(0.0, pxInLod * lambdaOverD, D, eps, lambda);
            if (v > prevPeak + 1e-12) monotone = false;
            prevPeak = v;
        }
        double fineCorePeak = RadialPsfProfile.PixelAveragedIntensity(0.0, 0.05 * lambdaOverD, D, eps, lambda);
        Check("the peak pixel dilutes monotonically as the plate scale coarsens, and recovers 1.0 when fine",
              monotone && Math.Abs(fineCorePeak - 1.0) < 2e-3 && prevPeak < 0.1,
              $"{fineCorePeak:F4} at 0.05 lambda/D per pixel falling to {prevPeak:E2} at 4 lambda/D per pixel");

        // Interpolating the table must not lose light against evaluating the profile directly:
        // the display integrates the frame nowhere, but a profile that loses flux between table
        // entries would change a companion's apparent brightness with the field of view.
        //
        // Sampled at the MIDPOINTS between table entries, which is where linear interpolation of a
        // convex profile is at its worst -- a check on the table's spacing rather than a
        // restatement of it.
        double tabulated = 0.0, direct = 0.0, worstEntry = 0.0;
        double platePerPxRad = platePerPx / ArcsecPerRad;
        double tableStep = 1.0 / RadialPsfProfile.SamplesPerPixel;
        for (double rPx = 0.5 * tableStep; rPx < 400.0; rPx += tableStep)
        {
            double w = rPx * tableStep; // annulus area element, r dr
            double t = profile.AtPixelRadius(rPx);
            double d = RadialPsfProfile.PixelAveragedIntensity(rPx * platePerPxRad, platePerPxRad, D, eps, lambda);
            tabulated += t * w;
            direct += d * w;
            worstEntry = Math.Max(worstEntry, Math.Abs(t - d));
        }
        Check("the interpolated table carries the same integrated light as the profile it tabulates",
              Math.Abs(tabulated / direct - 1.0) < 5e-3 && worstEntry < 5e-3,
              $"table vs direct evaluation over the full 400 px raster: {tabulated / direct - 1.0:+0.0000%;-0.0000%;0.0000%} integrated, "
              + $"worst point {worstEntry:E2} of peak");

        Console.WriteLine();
    }

    // ------------------------------------------------------------------ PupilDiffraction
    //
    // The imaging display drew its six diffraction spikes from three invented constants: an
    // amplitude of 4e-4 at 1 lambda/D, an azimuthal Gaussian of sigma 1.3 degrees, and a 1/r^2
    // falloff. PupilDiffraction replaces all three with the Fourier transform of the real pupil,
    // vanes included. These checks establish that it reduces to the exact annular pattern when the
    // vanes are removed (the standard this project holds new models to), that its normalisation is
    // the pupil's own open area, and that the spikes it produces are where a spider puts them.

    static void TestPupilDiffraction()
    {
        Console.WriteLine("Real ELT pupil: rings and spikes from one transform (PupilDiffraction)");

        double D = DirectImagingSimulator.ApertureMeters;
        double eps = DirectImagingSimulator.ObstructionRatio;
        double lambda = DirectImagingSimulator.WavelengthMeters;
        double lod = lambda / D;
        int vanes = DirectImagingSimulator.SpiderVaneCount;
        double vaneW = DirectImagingSimulator.SpiderVaneWidthMeters;

        // --- Reducibility: no vanes must give back the published closed form ---------------

        var bare = new PupilDiffraction(D, eps, lambda, 0, 0.0, 0.0);
        double worstBare = 0.0, worstAt = 0.0;
        for (double rLod = 0.0; rLod <= 20.0; rLod += 0.01)
        {
            double a = bare.Intensity(rLod * lod, 0.0);
            double b = OpticalPsf.AiryIntensity(rLod * lod, D, eps, lambda);
            double dev = Math.Abs(a - b);
            if (dev > worstBare) { worstBare = dev; worstAt = rLod; }
        }
        Check("with its vanes removed the pupil transform reproduces the closed-form annular pattern",
              worstBare < 1e-9,
              $"worst absolute deviation {worstBare:E2} of peak at {worstAt:F2} lambda/D, over 0 .. 20 lambda/D");

        // Same statement rotated: with no vanes the pattern must have no azimuthal structure.
        var bareAz = 0.0;
        for (double rLod = 0.5; rLod <= 10.0; rLod += 0.5)
        {
            double ref0 = bare.Intensity(rLod * lod, 0.0);
            for (double deg = 0; deg < 180; deg += 7.5)
            {
                double a = deg * Math.PI / 180.0;
                double v = bare.Intensity(rLod * lod * Math.Cos(a), rLod * lod * Math.Sin(a));
                bareAz = Math.Max(bareAz, Math.Abs(v - ref0));
            }
        }
        Check("and it is azimuthally flat without them, as a circular pupil must be",
              bareAz < 1e-9, $"worst azimuthal spread {bareAz:E2} of peak");

        // --- Normalisation is the pupil's own geometry, not a fitted scale -----------------

        var elt = new PupilDiffraction(D, eps, lambda, vanes, vaneW, 0.0);
        double R = D / 2.0, Rin = eps * D / 2.0;
        double expectedObsc = vanes * vaneW * (R - Rin) / (Math.PI * (R * R - Rin * Rin));
        Check("the vanes remove the area the real spider removes",
              Math.Abs(elt.VaneObscurationFraction - expectedObsc) < 1e-12 && elt.VaneObscurationFraction < 0.05,
              $"{elt.VaneObscurationFraction * 100:F3}% of the open pupil, for {vanes} vanes {vaneW} m wide "
              + $"spanning {R - Rin:F2} m");
        Check("on-axis intensity is exactly 1 with the vanes in place",
              Math.Abs(elt.Intensity(0, 0) - 1.0) < 1e-12, $"{elt.Intensity(0, 0):F12}");

        // --- The spikes: where a spider puts them, and how bright the vanes make them -------

        // Six vanes give three spike axes (opposed pairs share one line), 60 degrees apart.
        // Sample a ring well outside the core and find where the pattern peaks in azimuth.
        double ringLod = 6.0;
        double bestOn = 0.0, bestOff = double.MaxValue;
        double onAngle = 0.0;
        for (double deg = 0; deg < 180; deg += 0.25)
        {
            double a = deg * Math.PI / 180.0;
            double v = elt.Intensity(ringLod * lod * Math.Cos(a), ringLod * lod * Math.Sin(a));
            if (v > bestOn) { bestOn = v; onAngle = deg; }
            if (v < bestOff) bestOff = v;
        }
        // A long thin bar transforms to something NARROW along the bar and WIDE across it, so each
        // spike lies PERPENDICULAR to the vane that makes it. With vane axes at 0, 60 and 120
        // degrees the spikes must therefore fall at 90, 150 and 30. Getting this backwards is the
        // easiest way to draw a plausible-looking frame that is rotated 90 degrees from reality,
        // which is exactly why it is worth asserting.
        double offAxis = Math.Abs(onAngle % 60.0 - 30.0);
        Check("the spikes lie perpendicular to the vanes that cast them",
              offAxis < 1.0,
              $"brightest azimuth at {onAngle:F2} deg, with vane axes every 60 deg from 0 -- "
              + $"perpendicular to the {(onAngle + 90.0) % 180.0:F0} deg vane");
        Check("and they stand well above the ring background they cross",
              bestOn / bestOff > 20.0,
              $"{bestOn / bestOff:E1}x contrast between the spike and the faintest azimuth at {ringLod} lambda/D");

        // How wrong the invented constant was, now that the pupil answers instead. Measured along
        // a spike, well outside the core, against the same pupil with its vanes taken away.
        double spikeDir = (onAngle) * Math.PI / 180.0;
        double withVanes = elt.Intensity(ringLod * lod * Math.Cos(spikeDir), ringLod * lod * Math.Sin(spikeDir));
        double withoutVanes = bare.Intensity(ringLod * lod, 0.0);
        Console.WriteLine($"         (along a spike at {ringLod} lambda/D: {withVanes:E3} of peak with the real vanes, "
                          + $"{withoutVanes:E3} without them, so the vanes add {withVanes / withoutVanes:F1}x there;");
        Console.WriteLine($"          the display's discarded constant asserted 4.0E-004 at 1 lambda/D with a 1/r^2 falloff, "
                          + $"which is {4.0e-4 / (ringLod * ringLod):E3} at this radius)");

        // --- Pixel averaging still reduces to point sampling --------------------------------

        double worstAvg = 0.0;
        for (double rLod = 0.0; rLod <= 8.0; rLod += 0.37)
        {
            double t = rLod * lod;
            worstAvg = Math.Max(worstAvg, Math.Abs(
                elt.PixelAveragedIntensity(t, 0.3 * t, lod / 500.0) - elt.Intensity(t, 0.3 * t)));
        }
        Check("pixel averaging over the vaned pupil reduces to point sampling at a fine plate scale",
              worstAvg < 1e-6, $"worst absolute deviation {worstAvg:E2} of peak at lambda/D / 500");

        // --- The first null, which 9b now makes the simulator quote --------------------------

        Check("the simulator's diffraction limit is now its own pupil's first null",
              Math.Abs(DirectImagingSimulator.DiffractionLimitArcsec
                       / DirectImagingSimulator.LambdaOverDArcsec - 1.1242) < 2e-3,
              $"{DirectImagingSimulator.DiffractionLimitArcsec * 1000:F3} mas "
              + $"= {DirectImagingSimulator.DiffractionLimitArcsec / DirectImagingSimulator.LambdaOverDArcsec:F4} lambda/D, "
              + $"against 1.22 lambda/D = {1.22 * DirectImagingSimulator.LambdaOverDArcsec * 1000:F3} mas before");

        Console.WriteLine();
    }

    // ------------------------------------------------------------------ Spider kernels
    //
    // The visual roster's PSF kernel was radially symmetric by construction, so none of these five
    // telescopes could show a diffraction spike however real its spider. BuildKernel now takes the
    // vane geometry and samples PupilDiffraction in two dimensions when there is one.

    static void TestSpiderKernels()
    {
        Console.WriteLine("Visual roster: spider vanes in the PSF kernel");

        var sphere = VisualTelescopeCatalog.Sphere;
        double lambda = 554e-9;
        // ZIMPOL's real native plate scale, from its focal length and pixel pitch.
        double plate = sphere.NativePixelSizeMeters / sphere.FocalLengthMeters * (180.0 / Math.PI) * 3600.0;

        // The vaneless path must be exactly what it was before this change existed.
        float[] before = OpticalPsf.BuildKernel(plate, sphere.ApertureMeters,
            sphere.SecondaryObstructionFraction, lambda, 0.0, 0.0, out int rBefore);
        float[] viaNew = OpticalPsf.BuildKernel(plate, sphere.ApertureMeters,
            sphere.SecondaryObstructionFraction, lambda, 0.0, 0.0, 0, 0.0, out int rNew);
        bool identical = rBefore == rNew && before.Length == viaNew.Length;
        if (identical)
            for (int i = 0; i < before.Length; i++) if (before[i] != viaNew[i]) { identical = false; break; }
        Check("a pupil with no vanes takes the radial path, bit for bit as before",
              identical, $"radius {rBefore} px, {before.Length} taps identical");

        // With vanes: still a normalised kernel, and now with spikes in it.
        float[] vaned = OpticalPsf.BuildKernel(plate, sphere.ApertureMeters,
            sphere.SecondaryObstructionFraction, lambda, 0.0, 0.0,
            sphere.SpiderVaneCount, sphere.SpiderVaneWidthMeters, out int rVaned);
        double sum = 0.0;
        foreach (float v in vaned) sum += v;
        Check("the vaned kernel still conserves flux",
              Math.Abs(sum - 1.0) < 1e-6, $"sums to {sum:F9} over a {2 * rVaned + 1}x{2 * rVaned + 1} kernel");

        // Four vanes at 0/90 degrees put spikes on the perpendicular axes, which for a
        // four-fold spider are the same two axes. Compare along an axis against the diagonal.
        // Measured as azimuthal STRUCTURE at a fixed radius rather than as flux summed along a
        // line: summing along a line is dominated by the rings, which both kernels share. The
        // decisive statement is that the vaned kernel varies with azimuth at all, and the vaneless
        // one cannot, being radially symmetric by construction.
        // Same PHYSICAL radius in both, since the two kernels have different supports.
        double ring = 0.75 * Math.Min(rBefore, rVaned);
        double vanedRatio = AzimuthalContrast(vaned, rVaned, ring);
        double bareRatio = AzimuthalContrast(before, rBefore, ring);
        Check("and it carries azimuthal structure the radial kernel cannot represent at all",
              vanedRatio > 10.0 * bareRatio,
              $"max/min around a ring at {ring:F0} px: {vanedRatio:F1}x vaned "
              + $"against {bareRatio:F2}x vaneless");

        // --- What this actually changes, per instrument ------------------------------------
        //
        // A spike can only be drawn if the plate scale resolves the diffraction pattern at all.
        // Reported rather than asserted, because it is the honest scope of this change.
        Console.WriteLine("         instrument            plate scale    Airy FWHM    px per FWHM   spikes visible?");
        foreach (var spec in VisualTelescopeCatalog.All)
        {
            double ps = spec.NativePixelSizeMeters / spec.FocalLengthMeters * (180.0 / Math.PI) * 3600.0;
            double fw = OpticalPsf.AiryFwhmArcsec(spec.ApertureMeters, spec.SecondaryObstructionFraction, lambda);
            double perFwhm = fw / ps;
            string verdict = spec.SpiderVaneCount == 0
                ? (spec.SecondaryObstructionFraction <= 0 ? "no secondary" : "no vane width published")
                : (perFwhm >= 1.0 ? "YES" : "below one pixel");
            Console.WriteLine($"         {spec.Name,-20} {ps * 1000,8:F2} mas {fw * 1000,10:F2} mas {perFwhm,12:F2}   {verdict}");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Ratio of brightest to faintest value around a ring of the given radius in a kernel, sampled
    /// by bilinear interpolation rather than at rounded pixel centres.
    ///
    /// The interpolation is what makes this a measurement of AZIMUTHAL structure. Rounding to whole
    /// pixels makes the sampled radius wobble by up to half a pixel, and on the steep flank of a
    /// diffraction ring that wobble alone produces a several-fold spread in a kernel that is
    /// perfectly radially symmetric. Measured that way a vaneless kernel scored 3.11x, which was an
    /// artifact of the sampling and nothing else.
    /// </summary>
    static double AzimuthalContrast(float[] kernel, int radius, double ringRadiusPx)
    {
        int size = 2 * radius + 1;
        double hi = 0.0, lo = double.MaxValue;
        for (double deg = 0; deg < 360; deg += 0.5)
        {
            double a = deg * Math.PI / 180.0;
            double fx = radius + ringRadiusPx * Math.Cos(a);
            double fy = radius + ringRadiusPx * Math.Sin(a);
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            if (x0 < 0 || x0 + 1 >= size || y0 < 0 || y0 + 1 >= size) continue;
            double tx = fx - x0, ty = fy - y0;
            double v = kernel[y0 * size + x0] * (1 - tx) * (1 - ty)
                     + kernel[y0 * size + x0 + 1] * tx * (1 - ty)
                     + kernel[(y0 + 1) * size + x0] * (1 - tx) * ty
                     + kernel[(y0 + 1) * size + x0 + 1] * tx * ty;
            if (v > hi) hi = v;
            if (v < lo) lo = v;
        }
        return lo > 0.0 ? hi / lo : double.PositiveInfinity;
    }

    /// <summary>Larger of the two ratios, so a jump is measured whichever way it goes.</summary>
    static double Ratio(double a, double b)
    {
        a = Math.Max(a, 1e-30); b = Math.Max(b, 1e-30);
        return Math.Max(a / b, b / a);
    }

    /// <summary>
    /// Brute-force average of the exact intensity over a real SQUARE pixel of side p whose centre
    /// sits at angular offset theta along the x axis -- written independently of RadialPsfProfile
    /// so it is a genuine check on the radial-extent approximation rather than a restatement of it.
    /// </summary>
    static double SquarePixelAverage(double thetaRad, double pixelRad, double D, double eps, double lambda, int n, int orientations)
    {
        double sum = 0.0;
        double step = pixelRad / n;
        for (int k = 0; k < orientations; k++)
        {
            double phi = (Math.PI / 4.0) * k / orientations;
            double cos = Math.Cos(phi), sin = Math.Sin(phi);
            for (int iy = 0; iy < n; iy++)
            {
                double v = -0.5 * pixelRad + (iy + 0.5) * step;
                for (int ix = 0; ix < n; ix++)
                {
                    double u = -0.5 * pixelRad + (ix + 0.5) * step;
                    double x = thetaRad + u * cos - v * sin;
                    double y = u * sin + v * cos;
                    sum += OpticalPsf.AiryIntensity(Math.Sqrt(x * x + y * y), D, eps, lambda);
                }
            }
        }
        return sum / ((double)n * n * orientations);
    }

    /// <summary>Worst absolute departure of the pixel average from point sampling, over the core and the first several rings.</summary>
    static double WorstReductionDeviation(double pixelRad, double D, double eps, double lambda, double lambdaOverD)
    {
        double worst = 0.0;
        foreach (double rLod in new[] { 0.0, 0.5, 1.0, 1.63, 2.5, 4.0, 8.0 })
        {
            double theta = rLod * lambdaOverD;
            // Compared against the on-axis peak, not against the local value: near a null the
            // local value is ~0 and a relative comparison measures nothing but the null's depth.
            worst = Math.Max(worst, Math.Abs(
                RadialPsfProfile.PixelAveragedIntensity(theta, pixelRad, D, eps, lambda)
                - OpticalPsf.AiryIntensity(theta, D, eps, lambda)));
        }
        return worst;
    }

    /// <summary>
    /// The standard inverse gnomonic (TAN) deprojection, from FITS keywords to sky coordinates --
    /// written independently here, from the textbook relations, so it is a real check on
    /// FitsWcs rather than a rearrangement of it:
    ///
    ///     tan(alpha - alpha0) = xi / (cos dec0 - eta sin dec0)
    ///     tan(dec) = (sin dec0 + eta cos dec0) cos(alpha - alpha0) / (cos dec0 - eta sin dec0)
    /// </summary>
    static void DeprojectTan(FitsWcs wcs, double arrayPixelX, double arrayPixelY, out double raDeg, out double decDeg)
    {
        const double DegToRad = Math.PI / 180.0;

        // Array index -> FITS pixel, then the CD matrix maps the offset to the projection plane.
        double dx = arrayPixelX + 0.5 - wcs.ReferencePixelX;
        double dy = arrayPixelY + 0.5 - wcs.ReferencePixelY;
        double xi = (wcs.Cd11 * dx + wcs.Cd12 * dy) * DegToRad;
        double eta = (wcs.Cd21 * dx + wcs.Cd22 * dy) * DegToRad;

        double dec0 = wcs.ReferenceDecDeg * DegToRad;
        double cosDec0 = Math.Cos(dec0), sinDec0 = Math.Sin(dec0);

        double denominator = cosDec0 - eta * sinDec0;
        double deltaRa = Math.Atan2(xi, denominator);
        decDeg = Math.Atan2((sinDec0 + eta * cosDec0) * Math.Cos(deltaRa), denominator) / DegToRad;
        raDeg = wcs.ReferenceRaDeg + deltaRa / DegToRad;
    }
}
