using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// Does a pointing that goes in come back out?
///
/// The astrometric half of the same argument PhotometryTests makes about flux. A World Coordinate
/// System written from the COMMANDED pointing is a description of intent; one fitted to where the
/// stars actually landed is a description of the result, and the difference between them is the
/// measurement. This checks that the fitted one recovers a truth it was never told.
///
/// The test is built the only way this can be built honestly: construct a WCS with a known tangent
/// point, plate scale, rotation and parity; project catalogue stars through it to get pixel
/// positions; perturb those positions with a known centroid error; then hand the fitter the pixels
/// and the sky and see whether it returns the WCS it was never shown.
/// </summary>
static class AstrometryTests
{
    static int failures;
    static string outDir = ".";

    // A frame on the sky. Deliberately not at the pole and not on a round number.
    const double TrueRaDeg = 83.63308, TrueDecDeg = 22.01450;   // the Crab, near enough
    const double TruePlateScaleArcsec = 0.125;                  // FORS2 unbinned
    const double TrueRotationDeg = 17.5;
    const int FrameSize = 2048;

    public static int Run(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--out") outDir = args[i + 1];
        Directory.CreateDirectory(outDir);

        Console.WriteLine();
        Console.WriteLine("Astrometry: does a pointing that goes in come back out?");
        Console.WriteLine(new string('=', 78));

        SectionProjectionRoundTrip();
        SectionExactRecovery();
        SectionWithCentroidNoise();
        SectionOutlierRejection();
        SectionMatching();

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(failures == 0 ? "ASTROMETRY: ALL CHECKS PASSED" : $"ASTROMETRY: {failures} CHECK(S) FAILED");
        return failures;
    }

    /// <summary>The projection and its inverse must compose to the identity, or nothing downstream means anything.</summary>
    static void SectionProjectionRoundTrip()
    {
        Header("A1. The gnomonic projection inverts itself");
        var wcs = BuildTruth();

        double worstSky = 0.0, worstPix = 0.0;
        for (int i = 0; i < 400; i++)
        {
            double x = 1.0 + (FrameSize - 1) * ((i * 37) % 400) / 400.0;
            double y = 1.0 + (FrameSize - 1) * ((i * 91) % 400) / 400.0;

            wcs.PixelToSky(x, y, out double ra, out double dec);
            if (!wcs.TrySkyToPixel(ra, dec, out double bx, out double by)) { Check("round trip projects", false); continue; }
            worstPix = Math.Max(worstPix, Math.Sqrt((bx - x) * (bx - x) + (by - y) * (by - y)));

            FitsWcs.TryStandardCoordinates(ra, dec, TrueRaDeg, TrueDecDeg, out double xi, out double eta);
            FitsWcs.SkyFromStandardCoordinates(xi, eta, TrueRaDeg, TrueDecDeg, out double ra2, out double dec2);
            worstSky = Math.Max(worstSky, Separation(ra, dec, ra2, dec2) * 3600.0);
        }
        Console.WriteLine($"   pixel -> sky -> pixel: worst {worstPix:E3} px over 400 positions");
        Console.WriteLine($"   sky -> tangent plane -> sky: worst {worstSky:E3} arcsec");
        Check("pixel round trip", worstPix, 0.0, 1e-9);
        Check("tangent-plane round trip", worstSky, 0.0, 1e-9);

        // The truth's own derived quantities must be what was built in.
        Console.WriteLine($"   built scale {TruePlateScaleArcsec:F4}\"/px, rotation {TrueRotationDeg:F2} deg, " +
                          $"reads {wcs.ScaleXArcsecPerPixel:F4}\"/px, {wcs.RotationDeg:F2} deg, flipped {wcs.FlippedParity}");
        Check("the built plate scale reads back", wcs.ScaleXArcsecPerPixel, TruePlateScaleArcsec, 1e-9);
        Check("the built rotation reads back", wcs.RotationDeg, TrueRotationDeg, 1e-9);
        Check("a normal frame reads as unflipped", !wcs.FlippedParity);
    }

    /// <summary>With no noise, the fit must return the truth to machine precision.</summary>
    static void SectionExactRecovery()
    {
        Header("A2. Exact recovery from noiseless positions");
        var truth = BuildTruth();
        var matches = MakeMatches(truth, 40, 0.0, seed: 1);

        // The tangent point given to the fitter is DELIBERATELY WRONG by an arcminute, to show
        // that the fit does not need to be told the answer to find it.
        double guessRa = TrueRaDeg + 1.0 / 60.0, guessDec = TrueDecDeg - 1.0 / 60.0;
        var solved = AstrometricSolution.Fit(matches, guessRa, guessDec, 3.0, 3);

        Console.WriteLine($"   tangent point offered {Separation(guessRa, guessDec, TrueRaDeg, TrueDecDeg) * 3600:F1}\" from the truth");
        Report(solved);

        Check("the fit is valid", solved.IsValid);
        Check("all 40 stars used", solved.Used, 40, 0);
        Check("plate scale recovered", solved.PlateScaleXArcsecPerPixel, TruePlateScaleArcsec, 1e-8);
        Check("parity recovered", solved.FlippedParity == false);

        // The residual is not zero and should not be: a frame taken on ONE tangent plane is only
        // exactly linear in the standard coordinates of that same tangent point, and this fit was
        // deliberately given a different one. What is left is second order in (field radius x
        // tangent offset), which here is ten nano-arcseconds.
        Check("residuals are at the tangent-point's second order", solved.RmsArcsec, 0.0, 1e-4);

        // THE ROTATION IS NOT THE TRUTH'S, AND THAT IS CORRECT. A CD matrix is expressed against
        // its own tangent point's local north, and two tangent points an arcminute apart do not
        // share one: meridians converge. The predicted difference is dAlpha x sin(dec), and
        // checking THAT rather than equality is the difference between testing the fit and testing
        // a misunderstanding.
        double convergenceDeg = (1.0 / 60.0) * Math.Sin(TrueDecDeg * Math.PI / 180.0);
        double rotationDifference = TrueRotationDeg - solved.RotationDeg;
        Console.WriteLine($"   rotation differs from the truth's by {rotationDifference * 3600:F2}\", " +
                          $"against a meridian convergence of {convergenceDeg * 3600:F2}\"");
        Check("the rotation difference is meridian convergence", rotationDifference, convergenceDeg, 0.03 * convergenceDeg);

        // And the real invariant: two WCS with different tangent points can be the SAME MAP.
        var truthWcs = BuildTruth();
        double worstMap = 0.0;
        for (int i = 0; i < 200; i++)
        {
            double x = 1.0 + (FrameSize - 1) * ((i * 53) % 200) / 200.0;
            double y = 1.0 + (FrameSize - 1) * ((i * 97) % 200) / 200.0;
            truthWcs.PixelToSky(x, y, out double raT, out double decT);
            solved.Wcs.PixelToSky(x, y, out double raS, out double decS);
            worstMap = Math.Max(worstMap, AstrometricSolution.SeparationArcsec(raT, decT, raS, decS));
        }
        Console.WriteLine($"   the solved map agrees with the truth's to {worstMap:E3}\" over the whole frame");
        Check("the solved WCS is the same map as the truth", worstMap, 0.0, 1e-4);

        // And the centre of the frame must map back to where the frame was really pointed.
        double centre = 0.5 * (FrameSize + 1);
        double pointing = AstrometricSolution.PointingErrorArcsec(solved.Wcs, TrueRaDeg, TrueDecDeg, centre, centre);
        Console.WriteLine($"   frame centre maps to within {pointing:E3}\" of the true pointing");
        // Same second-order tangent-point term as the residual above, and the same tolerance: six
        // micro-arcseconds, four orders of magnitude below anything an instrument here can measure.
        Check("the frame centre maps to the true pointing", pointing, 0.0, 1e-4);
    }

    /// <summary>
    /// With centroid noise, the residual must be the noise, and the SOLUTION must be better than
    /// any single star by roughly sqrt(N): that is what fitting many stars buys, and if it does not
    /// appear, the fit is not using them.
    /// </summary>
    static void SectionWithCentroidNoise()
    {
        Header("A3. With centroid noise");
        Console.WriteLine("   stars   centroid sigma   residual rms   pointing error   expected ~sigma/sqrt(N)");
        var rows = new List<string> { "stars,centroid_sigma_px,rms_arcsec,pointing_error_arcsec,expected_arcsec" };

        foreach (int stars in new[] { 10, 40, 160 })
        {
            const double CentroidSigmaPx = 0.05;
            var truth = BuildTruth();

            // Averaged over realisations, because a single draw of a pointing error is itself a
            // random variable and comparing one of them against a prediction proves nothing.
            const int Trials = 200;
            double sumRms = 0.0, sumErr2 = 0.0;
            for (int t = 0; t < Trials; t++)
            {
                var matches = MakeMatches(truth, stars, CentroidSigmaPx, seed: 1000 + t);
                var solved = AstrometricSolution.Fit(matches, TrueRaDeg, TrueDecDeg, 5.0, 2);
                if (!solved.IsValid) { Check("fit valid under noise", false); continue; }
                sumRms += solved.RmsArcsec;
                double centre = 0.5 * (FrameSize + 1);
                double err = AstrometricSolution.PointingErrorArcsec(solved.Wcs, TrueRaDeg, TrueDecDeg, centre, centre);
                sumErr2 += err * err;
            }
            double rms = sumRms / Trials;
            double pointingErr = Math.Sqrt(sumErr2 / Trials);
            double sigmaArcsec = CentroidSigmaPx * TruePlateScaleArcsec;
            double expected = sigmaArcsec / Math.Sqrt(stars);

            Console.WriteLine($"   {stars,5}   {CentroidSigmaPx,14:F3}   {rms,12:F5}\"   {pointingErr,14:F5}\"   {expected,20:F5}\"");
            rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1:G6},{2:G6},{3:G6},{4:G6}",
                                   stars, CentroidSigmaPx, rms, pointingErr, expected));

            // The per-star residual is the centroid noise on both axes, REDUCED BY THE FIT'S OWN
            // DEGREES OF FREEDOM. Six parameters are spent on 2N measurements, so the residual
            // scatter is short of the input noise by sqrt(1 - 6/2N): 84% at ten stars, 96% at
            // forty, 99% at a hundred and sixty. Expecting the raw noise instead would call a
            // correct fit wrong at small N, which is exactly what it did on the first run.
            double dof = Math.Sqrt(1.0 - 6.0 / (2.0 * stars));
            double expectedRms = sigmaArcsec * Math.Sqrt(2.0) * dof;
            Check($"residual at {stars} stars is the centroid noise less the fit's dof",
                  rms, expectedRms, 0.10 * expectedRms);

            // The pointing error falls as 1/sqrt(N). The constant is not exactly one because the
            // centre of the frame is not the centroid of the stars and the fit spends three of its
            // parameters elsewhere, so this checks the SCALING with a generous constant rather than
            // a value.
            Check($"pointing error at {stars} stars beats a single star", pointingErr < sigmaArcsec);
            Check($"pointing error at {stars} stars is of order sigma/sqrt(N)",
                  pointingErr > 0.5 * expected && pointingErr < 2.0 * expected);
        }
        File.WriteAllLines(Path.Combine(outDir, "astrometry_noise.csv"), rows);
    }

    /// <summary>One mismatched pair must not be able to move the answer.</summary>
    static void SectionOutlierRejection()
    {
        Header("A4. One bad match must not move the answer");
        var truth = BuildTruth();
        var clean = MakeMatches(truth, 40, 0.03, seed: 7);

        var poisoned = new List<AstrometricSolution.Match>(clean);
        // A star paired with the wrong catalogue entry: right pixel, sky position 30 arcsec away.
        var bad = poisoned[13];
        bad.RaDeg += 30.0 / 3600.0 / Math.Cos(TrueDecDeg * Math.PI / 180.0);
        poisoned[13] = bad;

        var withoutClip = AstrometricSolution.Fit(poisoned, TrueRaDeg, TrueDecDeg, 0.0, 0);
        var withClip = AstrometricSolution.Fit(poisoned, TrueRaDeg, TrueDecDeg, 3.0, 5);
        var reference = AstrometricSolution.Fit(clean, TrueRaDeg, TrueDecDeg, 3.0, 5);

        double centre = 0.5 * (FrameSize + 1);
        double errNoClip = AstrometricSolution.PointingErrorArcsec(withoutClip.Wcs, TrueRaDeg, TrueDecDeg, centre, centre);
        double errClip = AstrometricSolution.PointingErrorArcsec(withClip.Wcs, TrueRaDeg, TrueDecDeg, centre, centre);
        double errClean = AstrometricSolution.PointingErrorArcsec(reference.Wcs, TrueRaDeg, TrueDecDeg, centre, centre);

        Console.WriteLine($"   clean, 40 stars:              rms {reference.RmsArcsec:F5}\", pointing error {errClean:F5}\"");
        Console.WriteLine($"   one bad pair, no clipping:    rms {withoutClip.RmsArcsec:F5}\", pointing error {errNoClip:F5}\"");
        Console.WriteLine($"   one bad pair, 3-sigma clip:   rms {withClip.RmsArcsec:F5}\", pointing error {errClip:F5}\", " +
                          $"{withClip.Rejected} rejected");

        Check("without clipping, one bad pair wrecks the fit", errNoClip > 10.0 * errClean);
        Check("clipping finds it", withClip.Rejected >= 1);
        Check("and recovers the clean answer", errClip, errClean, 0.02);
        Check("and the clipped rms is the clean rms", withClip.RmsArcsec, reference.RmsArcsec, 0.2 * reference.RmsArcsec);
    }

    /// <summary>Matching must pair what it can and refuse what it cannot.</summary>
    static void SectionMatching()
    {
        Header("A5. Matching, and refusing to guess");
        var truth = BuildTruth();

        // A catalogue, and detections generated from it through the truth.
        var catalogue = new List<(double RaDeg, double DecDeg)>();
        var detections = new List<(double X, double Y)>();
        var rng = new Pcg32(Pcg32.MixSeed(31337), Pcg32.StreamShotNoise);
        for (int i = 0; i < 60; i++)
        {
            double x = 100.0 + rng.NextDouble() * (FrameSize - 200);
            double y = 100.0 + rng.NextDouble() * (FrameSize - 200);
            truth.PixelToSky(x, y, out double ra, out double dec);
            catalogue.Add((ra, dec));
            detections.Add((x - 1.0, y - 1.0));       // FITS back to array indices
        }

        // The initial guess is the commanded pointing, wrong by 4 arcsec: a real mount's error.
        var guess = BuildWcs(TrueRaDeg + 4.0 / 3600.0 / Math.Cos(TrueDecDeg * Math.PI / 180.0),
                             TrueDecDeg, TruePlateScaleArcsec, TrueRotationDeg, flipped: false);

        // THE TOLERANCE IS A TRADE AND THE HARNESS SHOULD SHOW IT. It must exceed the pointing
        // error or nothing matches at all; it must stay well inside the mean separation of the
        // field or half the sources acquire a rival and are refused. Sixty stars over 2048 pixels
        // sit 264 px apart on average, so a 10 arcsec (80 px) tolerance makes a third of them
        // ambiguous while a 6 arcsec (48 px) one, still comfortably above the 4 arcsec pointing
        // error, keeps nearly all.
        Console.WriteLine("   tolerance   matched   ambiguous");
        foreach (double tol in new[] { 3.0, 6.0, 10.0, 20.0 })
        {
            var m = AstrometricSolution.MatchToCatalogue(detections, catalogue, guess, tol, out int amb);
            Console.WriteLine($"   {tol,8:F0}\"   {m.Count,7}   {amb,9}");
        }

        var matches = AstrometricSolution.MatchToCatalogue(detections, catalogue, guess, 6.0, out int ambiguous);
        Console.WriteLine($"   using 6\": {matches.Count} of {detections.Count} matched, {ambiguous} ambiguous");
        Check("a tolerance above the pointing error and below the crowding pairs most of the field",
              matches.Count >= 45);
        Check("a tolerance below the pointing error pairs almost nothing",
              AstrometricSolution.MatchToCatalogue(detections, catalogue, guess, 3.0, out _).Count < 5);

        var solved = AstrometricSolution.Fit(matches, guess.ReferenceRaDeg, guess.ReferenceDecDeg, 3.0, 3);
        Report(solved);
        double centre = 0.5 * (FrameSize + 1);
        double err = AstrometricSolution.PointingErrorArcsec(solved.Wcs, TrueRaDeg, TrueDecDeg, centre, centre);
        Console.WriteLine($"   the solved frame points {err:E2}\" from the truth, against the 4\" the guess was wrong by");
        Check("the solution beats the guess it started from", err < 0.01);

        // THE REFUSAL, ON A FIELD SPARSE ENOUGH THAT THE ANSWER IS UNAMBIGUOUS TO BEGIN WITH.
        // Adding a rival to a source that already had one proves nothing, and the crowded field
        // above has eleven such sources; this is a separate, deliberately sparse setup so that
        // exactly one thing changes. Getting that wrong the first time is what the table above is
        // for: at a 6 arcsec tolerance eleven of sixty sources are already contested.
        var sparseCat = new List<(double RaDeg, double DecDeg)>();
        var sparseDet = new List<(double X, double Y)>();
        for (int i = 0; i < 9; i++)
        {
            double sx = 300.0 + (i % 3) * 700.0;
            double sy = 300.0 + (i / 3) * 700.0;
            truth.PixelToSky(sx, sy, out double sra, out double sdec);
            sparseCat.Add((sra, sdec));
            sparseDet.Add((sx - 1.0, sy - 1.0));
        }

        var sparseMatches = AstrometricSolution.MatchToCatalogue(sparseDet, sparseCat, guess, 6.0, out int sparseAmb);
        Console.WriteLine($"   sparse field, 700 px apart: {sparseMatches.Count} of 9 matched, {sparseAmb} ambiguous");
        Check("a sparse field pairs everything", sparseMatches.Count, 9, 0);
        Check("with nothing ambiguous", sparseAmb, 0, 0);

        // One rival, two pixels from one source, well inside the tolerance.
        var withRival = new List<(double RaDeg, double DecDeg)>(sparseCat);
        truth.PixelToSky(sparseDet[4].X + 1.0 + 2.0, sparseDet[4].Y + 1.0, out double nra, out double ndec);
        withRival.Add((nra, ndec));

        var rivalMatches = AstrometricSolution.MatchToCatalogue(sparseDet, withRival, guess, 6.0, out int rivalAmb);
        Console.WriteLine($"   one rival 2 px from source 4: {rivalMatches.Count} matched, {rivalAmb} refused as ambiguous");
        Check("the contested source is refused rather than guessed", rivalAmb, 1, 0);
        Check("and only that one is lost", rivalMatches.Count, 8, 0);
    }

    // ---------------------------------------------------------------- helpers

    static FitsWcs BuildTruth()
        => BuildWcs(TrueRaDeg, TrueDecDeg, TruePlateScaleArcsec, TrueRotationDeg, flipped: false);

    /// <summary>
    /// A WCS with a chosen scale, rotation and parity.
    ///
    /// The CD matrix of a normal astronomical frame at rotation zero is diag(-s, +s): north up and
    /// east LEFT, which is what gives it a negative determinant. Rotating it is an ordinary
    /// rotation matrix on top; flipping it negates the first column.
    /// </summary>
    static FitsWcs BuildWcs(double raDeg, double decDeg, double scaleArcsec, double rotationDeg, bool flipped)
    {
        double s = scaleArcsec / 3600.0;
        double t = rotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(t), sin = Math.Sin(t);

        double a11 = -s, a12 = 0.0, a21 = 0.0, a22 = s;
        if (flipped) { a11 = -a11; a21 = -a21; }

        return new FitsWcs
        {
            ReferenceRaDeg = raDeg,
            ReferenceDecDeg = decDeg,
            ReferencePixelX = 0.5 * (FrameSize + 1),
            ReferencePixelY = 0.5 * (FrameSize + 1),
            Cd11 = cos * a11 - sin * a21,
            Cd12 = cos * a12 - sin * a22,
            Cd21 = sin * a11 + cos * a21,
            Cd22 = sin * a12 + cos * a22,
            IsValid = true,
        };
    }

    /// <summary>Stars spread over the frame, projected through the truth, with a given centroid error.</summary>
    static List<AstrometricSolution.Match> MakeMatches(FitsWcs truth, int count, double centroidSigmaPx, int seed)
    {
        var list = new List<AstrometricSolution.Match>(count);
        var rng = new Pcg32(Pcg32.MixSeed(seed), Pcg32.StreamShotNoise);
        for (int i = 0; i < count; i++)
        {
            double x = 50.0 + rng.NextDouble() * (FrameSize - 100);
            double y = 50.0 + rng.NextDouble() * (FrameSize - 100);
            truth.PixelToSky(x, y, out double ra, out double dec);
            list.Add(new AstrometricSolution.Match
            {
                PixelX = x + (centroidSigmaPx > 0 ? NoiseSampler.Gaussian(rng, centroidSigmaPx) : 0.0),
                PixelY = y + (centroidSigmaPx > 0 ? NoiseSampler.Gaussian(rng, centroidSigmaPx) : 0.0),
                RaDeg = ra, DecDeg = dec,
            });
        }
        return list;
    }

    /// <summary>Degrees, through the Core haversine: a second copy of a separation formula is a second chance to get its precision wrong.</summary>
    static double Separation(double ra1, double dec1, double ra2, double dec2)
        => AstrometricSolution.SeparationArcsec(ra1, dec1, ra2, dec2) / 3600.0;

    static void Report(AstrometricSolution.Result r)
    {
        Console.WriteLine($"   {r.Used} used / {r.Rejected} rejected, rms {r.RmsArcsec:E3}\" " +
                          $"(x {r.RmsXArcsec:E3}\", y {r.RmsYArcsec:E3}\"), worst {r.WorstResidualArcsec:E3}\"");
        Console.WriteLine($"   scale {r.PlateScaleXArcsecPerPixel:F6}\"/px, rotation {r.RotationDeg:F6} deg, flipped {r.FlippedParity}");
    }

    static void Header(string t) { Console.WriteLine(); Console.WriteLine(t); Console.WriteLine(new string('-', t.Length)); }
    static void Check(string what, double got, double expected, double tol)
    {
        if (!(Math.Abs(got - expected) <= tol)) { failures++; Console.WriteLine($"     FAIL {what}: got {got:G8}, expected {expected:G8} +/- {tol:G4}"); }
    }
    static void Check(string what, bool ok) { if (!ok) { failures++; Console.WriteLine($"     FAIL {what}"); } }
}
