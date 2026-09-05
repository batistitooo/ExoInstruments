using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;
using ExoInstruments.Visualization;   // CameraFilter, from Stub.cs

// Headless checks on Core/FitsImageReader.cs and Core/MeasuredFlatField.cs.
//
// Run:  dotnet run -p:Core=../../ExoInstruments/Core -- --out .
//       ../env/bin/python compare_astropy.py
//
// The C# side writes the FITS files, reads them back and asserts on the values; the Python side
// reads the SAME files with astropy.io.fits and compares. Both halves are needed: a reader and a
// writer that share a misunderstanding of the format agree with each other perfectly, and only an
// independent implementation can catch that.
internal static class FlatTests
{
    private static int failures;
    private static string outDir = ".";

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) failures++;
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private static int Main(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--out") outDir = args[i + 1];
        Directory.CreateDirectory(outDir);

        Console.WriteLine();
        Console.WriteLine("A. Every BITPIX the standard defines, decoded exactly");
        BitPixRoundTrip();

        Console.WriteLine();
        Console.WriteLine("B. BZERO/BSCALE, the keyword every camera depends on");
        ScalingCases();

        Console.WriteLine();
        Console.WriteLine("C. What is refused rather than guessed");
        Rejections();

        Console.WriteLine();
        Console.WriteLine("D. Header parsing corner cases");
        HeaderParsing();

        Console.WriteLine();
        Console.WriteLine("E. The flat itself");
        FlatBuilding();

        Console.WriteLine();
        Console.WriteLine("F. One instrument, one binning, two filters");
        FilterFlats();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ A

    private const int W = 17;   // deliberately not a multiple of anything: the 2880-byte block
    private const int H = 11;   // padding has to be handled, not stumbled over

    private static void BitPixRoundTrip()
    {
        // One value pattern, written at every BITPIX, so a decoding fault shows as a disagreement
        // between representations of the same numbers rather than as an absolute error.
        var expected = new Dictionary<int, double[]>();

        foreach (int bitpix in new[] { 8, 16, 32, 64, -32, -64 })
        {
            // BZERO=32768 on BITPIX 16 is the unsigned convention every camera writes, and it puts
            // the STORED value at physical-32768, so the physical range has to be [0, 65535] or the
            // short overflows. Getting this wrong in the test is how the range below was chosen:
            // an earlier ramp reaching -5000 wrapped, and the round-trip reported an error of
            // exactly 65536, which is the wrap and not a reader fault.
            double bzero = bitpix == 16 ? 32768.0 : 0.0;
            var values = new double[W * H];
            for (int i = 0; i < values.Length; i++)
            {
                double v;
                if (bitpix == 8) v = i % 251;                       // 8 bits is unsigned, [0,255]
                else if (bitpix == 16) v = (i * 337) % 65536;       // unsigned via BZERO, [0,65535]
                else v = (i * 37 % 20011) - 5000;                   // signed, exercises the sign bit
                values[i] = v;
            }

            string path = Path.Combine(outDir, "case_bitpix" + (bitpix < 0 ? "m" : "") + Math.Abs(bitpix) + ".fits");
            WriteFits(path, values, W, H, bitpix, bzero, 1.0, null);

            var image = FitsImageReader.Read(path);
            expected[bitpix] = image.Values;

            bool shapeOk = image.Width == W && image.Height == H && image.BitPix == bitpix;
            double worst = 0.0;
            for (int i = 0; i < values.Length; i++)
                worst = Math.Max(worst, Math.Abs(image.Values[i] - values[i]));

            Check("BITPIX " + bitpix + " round-trips", shapeOk && worst == 0.0,
                  image.Width + "x" + image.Height + ", worst error " + F(worst));
        }

        // The representations must agree with each other, not just each with itself. Same values,
        // two encodings: unsigned-via-BZERO 16-bit against a 64-bit float.
        var shared = new double[W * H];
        for (int i = 0; i < shared.Length; i++) shared[i] = (i * 337) % 65536;
        string a = Path.Combine(outDir, "case_cross16.fits");
        string b = Path.Combine(outDir, "case_cross64.fits");
        WriteFits(a, shared, W, H, 16, 32768.0, 1.0, null);
        WriteFits(b, shared, W, H, -64, 0.0, 1.0, null);
        var imA = FitsImageReader.Read(a);
        var imB = FitsImageReader.Read(b);
        double cross = 0.0;
        for (int i = 0; i < shared.Length; i++)
            cross = Math.Max(cross, Math.Abs(imA.Values[i] - imB.Values[i]));
        Check("16-bit-with-BZERO and 64-bit float agree", cross == 0.0, "worst difference " + F(cross));

        WriteExpectedCsv();
    }

    private static void WriteExpectedCsv()
    {
        // What Python compares against: whatever the C# reader decoded, per file.
        var sb = new StringBuilder();
        sb.AppendLine("file,index,value");
        foreach (string path in Directory.GetFiles(outDir, "case_*.fits"))
        {
            var image = FitsImageReader.Read(path);
            string name = Path.GetFileName(path);
            for (int i = 0; i < image.Values.Length; i++)
                sb.AppendLine(name + "," + i + "," + image.Values[i].ToString("R", CultureInfo.InvariantCulture));
        }
        File.WriteAllText(Path.Combine(outDir, "expected.csv"), sb.ToString());
        Console.WriteLine("  ....  wrote expected.csv for the astropy comparison");
    }

    // ------------------------------------------------------------------ B

    private static void ScalingCases()
    {
        // THE trap this reader exists to avoid. Unsigned 16-bit data is stored in FITS as signed
        // shorts with BZERO=32768, which is what essentially every astronomy camera writes. A
        // reader that ignores BZERO reads a 60000-count flat as -5536 and every calibration built
        // on it is wrong by the pedestal.
        var values = new double[] { 0.0, 1.0, 32767.0, 32768.0, 60000.0, 65535.0 };
        string path = Path.Combine(outDir, "case_unsigned16.fits");
        WriteFits(path, values, values.Length, 1, 16, 32768.0, 1.0, null);

        var image = FitsImageReader.Read(path);
        double worst = 0.0;
        for (int i = 0; i < values.Length; i++)
            worst = Math.Max(worst, Math.Abs(image.Values[i] - values[i]));
        Check("unsigned 16-bit via BZERO=32768", worst == 0.0,
              "60000 reads as " + F(image.Values[4]) + ", not -5536");

        // A non-unit BSCALE, which is how a reduced or compressed product often arrives.
        var scaled = new double[] { -2.5, 0.0, 12.5, 100.0 };
        string scaledPath = Path.Combine(outDir, "case_bscale.fits");
        WriteFits(scaledPath, scaled, scaled.Length, 1, 16, 10.0, 0.5, null);
        var scaledImage = FitsImageReader.Read(scaledPath);
        double scaledWorst = 0.0;
        for (int i = 0; i < scaled.Length; i++)
            scaledWorst = Math.Max(scaledWorst, Math.Abs(scaledImage.Values[i] - scaled[i]));
        Check("BSCALE=0.5 with BZERO=10", scaledWorst == 0.0, "worst error " + F(scaledWorst));

        // BLANK marks an undefined integer pixel; it must become NaN and not a huge number.
        var withBlank = new double[] { 5.0, -32768.0, 7.0 };
        string blankPath = Path.Combine(outDir, "case_blank.fits");
        WriteFits(blankPath, withBlank, withBlank.Length, 1, 16, 0.0, 1.0, "-32768");
        var blankImage = FitsImageReader.Read(blankPath);
        Check("BLANK becomes NaN", double.IsNaN(blankImage.Values[1])
              && blankImage.Values[0] == 5.0 && blankImage.Values[2] == 7.0,
              "middle pixel is " + blankImage.Values[1]);
    }

    // ------------------------------------------------------------------ C

    private static void Rejections()
    {
        Refuses("not a FITS file (SIMPLE=F)", () =>
        {
            string p = Path.Combine(outDir, "bad_simple.fits");
            WriteFitsRaw(p, new[] { "SIMPLE  =                    F", "BITPIX  =                   16",
                                    "NAXIS   =                    2", "NAXIS1  =                    2",
                                    "NAXIS2  =                    1" }, new byte[2880]);
            FitsImageReader.Read(p);
        });

        Refuses("a data cube (NAXIS=3)", () =>
        {
            string p = Path.Combine(outDir, "bad_naxis.fits");
            WriteFitsRaw(p, new[] { "SIMPLE  =                    T", "BITPIX  =                   16",
                                    "NAXIS   =                    3", "NAXIS1  =                    2",
                                    "NAXIS2  =                    2", "NAXIS3  =                    2" },
                         new byte[2880]);
            FitsImageReader.Read(p);
        });

        Refuses("an undefined BITPIX", () =>
        {
            string p = Path.Combine(outDir, "bad_bitpix.fits");
            WriteFitsRaw(p, new[] { "SIMPLE  =                    T", "BITPIX  =                   24",
                                    "NAXIS   =                    2", "NAXIS1  =                    2",
                                    "NAXIS2  =                    1" }, new byte[2880]);
            FitsImageReader.Read(p);
        });

        Refuses("a truncated data segment", () =>
        {
            string p = Path.Combine(outDir, "bad_truncated.fits");
            WriteFitsRaw(p, new[] { "SIMPLE  =                    T", "BITPIX  =                   16",
                                    "NAXIS   =                    2", "NAXIS1  =                 1000",
                                    "NAXIS2  =                 1000" }, new byte[16]);
            FitsImageReader.Read(p);
        });

        Refuses("a missing mandatory keyword", () =>
        {
            string p = Path.Combine(outDir, "bad_missing.fits");
            WriteFitsRaw(p, new[] { "SIMPLE  =                    T", "BITPIX  =                   16",
                                    "NAXIS   =                    2", "NAXIS1  =                    2" },
                         new byte[2880]);
            FitsImageReader.Read(p);
        });

        Refuses("a zero BSCALE", () =>
        {
            string p = Path.Combine(outDir, "bad_bscale.fits");
            WriteFitsRaw(p, new[] { "SIMPLE  =                    T", "BITPIX  =                   16",
                                    "NAXIS   =                    2", "NAXIS1  =                    2",
                                    "NAXIS2  =                    1", "BSCALE  =                    0" },
                         new byte[2880]);
            FitsImageReader.Read(p);
        });

        Refuses("a file that does not exist", () => FitsImageReader.Read(Path.Combine(outDir, "nope.fits")));
    }

    private static void Refuses(string what, Action act)
    {
        try
        {
            act();
            Check("refuses " + what, false, "it was accepted");
        }
        catch (FitsImageReader.FormatException e)
        {
            Check("refuses " + what, true, "\"" + Shorten(e.Message) + "\"");
        }
        catch (Exception e)
        {
            Check("refuses " + what, false, "threw " + e.GetType().Name + " rather than FormatException");
        }
    }

    private static string Shorten(string s) => s.Length <= 78 ? s : s.Substring(0, 75) + "...";

    // ------------------------------------------------------------------ D

    private static void HeaderParsing()
    {
        // A slash inside a quoted string is not the start of a comment. A FILTER card reading
        // 'Ha 3nm / OIII' is one value; splitting on the first slash would truncate it.
        string p = Path.Combine(outDir, "case_header.fits");
        WriteFitsRaw(p, new[]
        {
            "SIMPLE  =                    T",
            "BITPIX  =                   16",
            "NAXIS   =                    2",
            "NAXIS1  =                    1",
            "NAXIS2  =                    1",
            "FILTER  = 'Ha 3nm / OIII'      / dual band",
            "PEDESTAL=                  512 / bias level",
            "OBJECT  = 'it''s M42'          / quoted quote",
            "EXPTIME =            1.5D2     / Fortran exponent",
        }, new byte[2880]);

        var image = FitsImageReader.Read(p);
        Check("a slash inside a quoted string is not a comment",
              image.Card("FILTER") == "Ha 3nm / OIII", "read \"" + image.Card("FILTER") + "\"");
        Check("a doubled quote is one literal quote",
              image.Card("OBJECT") == "it's M42", "read \"" + image.Card("OBJECT") + "\"");
        Check("an integer card parses", image.Card("PEDESTAL") == "512", "read \"" + image.Card("PEDESTAL") + "\"");
        Check("COMMENT/HISTORY are not indexed as values",
              image.Card("COMMENT") == null && image.Card("HISTORY") == null, "both null");
    }

    // ------------------------------------------------------------------ E

    private static void FlatBuilding()
    {
        const int fw = 64, fh = 64;
        const double pedestal = 3000.0;
        const double level = 20000.0;

        // A flat with a known 10 % low pixel, on a uniform field, above a real pedestal.
        var raw = new double[fw * fh];
        for (int i = 0; i < raw.Length; i++) raw[i] = pedestal + level;
        raw[100] = pedestal + level * 0.90;

        var image = MakeImage(raw, fw, fh);
        var flat = MeasuredFlatField.Build(image, fw, fh, pedestal, 65535.0, 1.0);

        // With the pedestal removed the response is the real 0.90 (bar the mean shifting by one
        // pixel in 4096). Without it, the same pixel would read about 0.913: the pedestal pulls
        // every ratio toward unity, and that is the whole reason the bias is a required input.
        var naive = MeasuredFlatField.Build(image, fw, fh, 0.0, 65535.0, 1.0);
        Check("pedestal removed gives the true 10 % deficit",
              Math.Abs(flat.Response[100] - 0.90) < 1e-3, "response " + F(flat.Response[100]));
        Check("leaving the pedestal in understates it, as the algebra says",
              naive.Response[100] > 0.910 && naive.Response[100] < 0.916,
              "response " + F(naive.Response[100]) + " instead of 0.90");

        // Normalised by the mean, so an average pixel is exactly 1.
        Check("an average pixel sits at unity", Math.Abs(flat.Response[0] - 1.0) < 1e-3,
              "response " + F(flat.Response[0]));

        // A saturated pixel's response is UNMEASURED, so it is held at unity and counted, never
        // read as a very high response.
        var withSat = (double[])raw.Clone();
        withSat[200] = 65535.0;
        var satFlat = MeasuredFlatField.Build(MakeImage(withSat, fw, fh), fw, fh, pedestal, 65535.0, 1.0);
        Check("a saturated pixel is held at unity, not read as high response",
              Math.Abs(satFlat.Response[200] - 1.0) < 1e-12, "response " + F(satFlat.Response[200]));

        // Dimensions must match: a flat is not resampled onto a different grid.
        bool refusedShape = false;
        try { MeasuredFlatField.Build(image, fw * 2, fh, pedestal, 65535.0, 1.0); }
        catch (MeasuredFlatField.UnusableException) { refusedShape = true; }
        Check("a flat of the wrong size is refused, not resampled", refusedShape, "");

        // An unexposed frame is refused rather than normalised into nonsense.
        var dark = new double[fw * fh];
        for (int i = 0; i < dark.Length; i++) dark[i] = pedestal - 10.0;
        bool refusedDark = false;
        try { MeasuredFlatField.Build(MakeImage(dark, fw, fh), fw, fh, pedestal, 65535.0, 1.0); }
        catch (MeasuredFlatField.UnusableException) { refusedDark = true; }
        Check("a frame at or below the pedestal is refused", refusedDark, "");

        // The noise diagnostic: a flat whose scatter is at its own shot-noise floor is mostly a
        // photograph of its own noise, and saying so is the difference between a tool and a trap.
        var noisy = new double[fw * fh];
        var rng = new Random(7);
        for (int i = 0; i < noisy.Length; i++)
        {
            // Gaussian of width sqrt(level), i.e. exactly Poisson at 1 e-/ADU.
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            noisy[i] = pedestal + level + g * Math.Sqrt(level);
        }
        var noisyFlat = MeasuredFlatField.Build(MakeImage(noisy, fw, fh), fw, fh, pedestal, 65535.0, 1.0);
        Check("a single noisy sub raises the noise warning", noisyFlat.NoiseWarning,
              "high-frequency sigma " + F(100.0 * noisyFlat.HighFrequencySigma) + " % against a floor of "
              + F(100.0 * noisyFlat.ShotNoiseFloor) + " %");
        Check("a clean master flat does not", !flat.NoiseWarning,
              "high-frequency sigma " + F(100.0 * flat.HighFrequencySigma) + " %");
    }

    private static FitsImageReader.Image MakeImage(double[] values, int w, int h)
        => new FitsImageReader.Image { Values = values, Width = w, Height = h, BitPix = -64, BZero = 0, BScale = 1 };

    // ------------------------------------------------------------------ F

    // One instrument, one binning, two filters.
    //
    // What this section pins is a CACHE KEY rather than a number. SolarSystemCameraTexture's
    // EnsureFlatFieldMap held the flat field map keyed on the array being non-null and on nothing
    // else, while the field it loads is chosen per filter: MeasuredFlatPath puts the filter IN THE
    // FILE NAME, because the dust motes and accessory vignetting that make a real flat worth having
    // sit on the filter and move when it is swapped. Changing filter on one instrument at one
    // binning therefore went on serving the PREVIOUS passband's measured flat, and every frame after
    // it was divided by a flat belonging to a different light path. Nothing announced it, and nothing
    // could: a frame divided by the wrong flat looks exactly like a frame divided by the right one.
    // EnsureFringeMap, a few lines below in the same file, has carried the filter in its key from the
    // start, and the fix is to give this one the same key in the same shape.
    //
    // TWO HALVES, for the reason the rest of this file has two halves. The MAPS are the real shipped
    // Core - FitsImageReader and MeasuredFlatField for the measured branch, FocalPlaneIllumination
    // and SensorNonUniformity for the modelled one - so "the two maps differ" is a measurement of the
    // code that ships rather than of a copy. The four lines of the CACHE are restated in FlatCache
    // below, because they live in a file that needs Unity and cannot be compiled headlessly; the
    // README quotes both forms verbatim beside the original so the copy can be checked by eye.

    // The one instrument, held fixed across both filters. The name is the roster's amateur camera,
    // which carries Luminance and H-alpha in the same wheel and so is a device the fault is actually
    // reachable on. Its numbers are NOT read from Core.VisualTelescopeCatalog: that file names
    // SpectralCurve, FilterCurves, SystemBandpass, OpticalPsf and five more, and pulling fifty files
    // into a FITS-and-flat harness to borrow a pixel pitch would couple this test to most of the mod
    // for no gain. Nothing below is a claim about the real ASI294MM Pro. What the test needs is only
    // that the camera, the binning and the array are THE SAME for both filters, so that every
    // difference between the two maps is the filter's.
    private const string FCamera = "ZWO ASI294MM Pro";
    private const int FBinning = 2;
    private const int FW = 96, FH = 96;
    private const double FPedestal = 3000.0;
    private const double FLevel = 25000.0;
    private const double FAdcMax = 65535.0;
    private const double FGain = 1.0;              // e-/ADU
    private const double FPixelPitchMetres = 4.63e-6;
    private const double FFocalLengthMetres = 0.80;
    private const double FPrnuFraction = 0.003;    // 0.3 %, an ordinary published figure
    private const long FSerialSeed = 0x5A17C0DEL;  // one piece of silicon, the same under both filters

    private static void FilterFlats()
    {
        // The naming rule is the mechanism, so it is stated rather than assumed: one instrument at
        // one binning still has one file per filter, and two filters name two different files.
        string lumPath = Path.Combine(outDir, FlatFileName(FCamera, CameraFilter.Luminance, FBinning));
        string haPath  = Path.Combine(outDir, FlatFileName(FCamera, CameraFilter.HAlpha,    FBinning));
        Check("one instrument at one binning still has one flat per filter",
              lumPath != haPath, Path.GetFileName(lumPath) + "  vs  " + Path.GetFileName(haPath));

        // Two master flats through the same telescope on the same night, differing only in which
        // filter was in the way: same silicon, same tube, different mote and different filter cell.
        double sigma = SensorNonUniformity.BinnedPhotoResponseSigma(FPrnuFraction, FBinning);
        ushort[] prnu = SensorNonUniformity.BuildPhotoResponseMap(Pcg32.MixSeed(FSerialSeed), FW * FH, sigma);

        var lumFrame = SyntheticFlatFrame(prnu, moteXPx: 30.0, moteYPx: 34.0,
                                          moteRadiusPx: 9.0, moteDepth: 0.12, vignetteScalePx: 90.0);
        var haFrame  = SyntheticFlatFrame(prnu, moteXPx: 66.0, moteYPx: 58.0,
                                          moteRadiusPx: 7.0, moteDepth: 0.15, vignetteScalePx: 72.0);

        // BITPIX 16 with BZERO=32768, which is what a real camera writes, so the maps under test come
        // through the same unsigned path section B exists to defend.
        WriteFits(lumPath, lumFrame, FW, FH, 16, 32768.0, 1.0, null);
        WriteFits(haPath,  haFrame,  FW, FH, 16, 32768.0, 1.0, null);

        double[] lumMap = MeasuredResponse(lumPath);
        double[] haMap  = MeasuredResponse(haPath);

        // THE ASSERTION THE KEY EXISTS FOR. If these two were the same map the cache key would not
        // matter, so the premise is measured rather than asserted in prose.
        double rms = 0.0, worst = 0.0;
        int worstIndex = 0;
        for (int i = 0; i < lumMap.Length; i++)
        {
            double d = haMap[i] - lumMap[i];
            rms += d * d;
            if (Math.Abs(d) > Math.Abs(worst)) { worst = d; worstIndex = i; }
        }
        rms = Math.Sqrt(rms / lumMap.Length);
        Check("the two filters' measured flats are different maps", rms > 0.01,
              "RMS difference " + F(100.0 * rms) + " %, worst pixel " + F(100.0 * worst)
              + " % at (" + (worstIndex % FW) + "," + (worstIndex / FW) + ")");

        // What it costs to divide by the wrong one, in the unit the observer reads. A frame's counts
        // carry the response of the path they were taken through, so dividing by a different path's
        // flat leaves the ratio of the two behind as a photometric error.
        int lumMote = 34 * FW + 30, haMote = 58 * FW + 66;
        double errAtHaMote  = -2.5 * Math.Log10(haMap[haMote]  / lumMap[haMote]);
        double errAtLumMote = -2.5 * Math.Log10(haMap[lumMote] / lumMap[lumMote]);
        Check("an H-alpha frame divided by the Luminance flat is wrong by a measurable magnitude",
              Math.Abs(errAtHaMote) > 0.05 && Math.Abs(errAtLumMote) > 0.05,
              "a star under H-alpha's own mote reads " + F(errAtHaMote) + " mag too faint; one under"
              + " Luminance's mote reads " + F(-errAtLumMote) + " mag too bright");

        // ---- the cache, in both forms, driven by those maps -------------------------------------

        Func<CameraFilter, double[]> build = f => BuildAsTheFrameBuilderWould(f);

        // A. The filter change itself. One instrument, one binning, Luminance and then H-alpha.
        var shipped = new FlatCache(keyOnFilter: true);
        var previous = new FlatCache(keyOnFilter: false);

        shipped.Ensure(CameraFilter.Luminance, build);
        previous.Ensure(CameraFilter.Luminance, build);
        double[] shippedAfter = shipped.Ensure(CameraFilter.HAlpha, build);
        double[] previousAfter = previous.Ensure(CameraFilter.HAlpha, build);

        Check("keyed on the filter, a filter change rebuilds the flat",
              Same(shippedAfter, haMap) && shipped.Builds == 2,
              "H-alpha's own flat, after " + shipped.Builds + " builds");
        Check("keyed on the array alone it did not, which is the regression",
              Same(previousAfter, lumMap) && previous.Builds == 1,
              "still Luminance's flat, after " + previous.Builds + " build");

        // B. Measured to none. H-alpha has a flat on disk and OIII does not, so OIII has to fall back
        // to the modelled map rather than go on dividing by H-alpha's measured one.
        var toNone = new FlatCache(keyOnFilter: true);
        var toNonePrev = new FlatCache(keyOnFilter: false);
        toNone.Ensure(CameraFilter.HAlpha, build);
        toNonePrev.Ensure(CameraFilter.HAlpha, build);
        Check("a filter with no flat on disk falls back to the model",
              Same(toNone.Ensure(CameraFilter.OIII, build), ModelledFlat()), "the modelled map");
        Check("keyed on the array alone it kept the previous passband's measured flat",
              Same(toNonePrev.Ensure(CameraFilter.OIII, build), haMap), "H-alpha's flat, under OIII");

        // C. None to measured, the direction that is just as wrong and easier to miss. OIII has no
        // flat, Luminance does, and changing to Luminance has to pick it up.
        var toMeasured = new FlatCache(keyOnFilter: true);
        var toMeasuredPrev = new FlatCache(keyOnFilter: false);
        toMeasured.Ensure(CameraFilter.OIII, build);
        toMeasuredPrev.Ensure(CameraFilter.OIII, build);
        Check("a filter that does have a flat on disk picks it up",
              Same(toMeasured.Ensure(CameraFilter.Luminance, build), lumMap), "Luminance's flat");
        Check("keyed on the array alone the observer's own flat was never loaded",
              Same(toMeasuredPrev.Ensure(CameraFilter.Luminance, build), ModelledFlat()),
              "still the modelled map");

        // D. The control, and the reason EnsureOffsetFpnMap is left keyed on the array alone. Offset
        // fixed-pattern noise is the readout chain's per-pixel zero level, which is what a bias frame
        // measures with the shutter shut: no light means no passband. The call graph says the same
        // thing - BuildOffsetMap takes the serial seed, the pixel count and a sigma that comes from
        // the spec and the binning, and the filter reaches none of the three - and this checks it
        // rather than taking it on trust, because taking exactly this on trust is what produced the
        // fault above.
        double offsetSigma = SensorNonUniformity.BinnedOffsetSigmaElectrons(3.5, FBinning);
        ushort[] offsetUnderLum = SensorNonUniformity.BuildOffsetMap(
            Pcg32.MixSeed(FSerialSeed), FW * FH, offsetSigma);
        ushort[] offsetUnderHa = SensorNonUniformity.BuildOffsetMap(
            Pcg32.MixSeed(FSerialSeed), FW * FH, offsetSigma);
        bool identical = offsetUnderLum.Length == offsetUnderHa.Length;
        for (int i = 0; identical && i < offsetUnderLum.Length; i++)
            identical = offsetUnderLum[i] == offsetUnderHa[i];
        Check("the offset map is filter-independent, so keying it on the array alone is right",
              identical, "bit-identical across " + offsetUnderLum.Length + " pixels");
    }

    // MeasuredFlatPath's rule, restated: the camera, the FILTER and the binning are all in the name,
    // because a flat belongs to one optical train at one filter at one binning.
    private static string FlatFileName(string camera, CameraFilter filter, int binning)
        => "Flat_" + Sanitise(camera) + "_" + Sanitise(filter.ToString()) + "_bin" + binning + ".fits";

    private static string Sanitise(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    // What a master flat through one filter looks like: the sensor's photo-response and the tube's
    // cosine-fourth falloff, which are the SAME under both filters because one is the silicon and the
    // other is the optics, and on top of them the two terms that are not - a dust mote and an
    // undersized filter cell, both of which sit ON THE FILTER and move when it is swapped. Written
    // clean rather than with shot noise, which is what stacking a master flat is for and which keeps
    // the difference between the two maps attributable to the filter rather than to the draw.
    private static double[] SyntheticFlatFrame(ushort[] prnu, double moteXPx, double moteYPx,
                                               double moteRadiusPx, double moteDepth,
                                               double vignetteScalePx)
    {
        double centreX = 0.5 * (FW - 1), centreY = 0.5 * (FH - 1);
        double pitch = FPixelPitchMetres * FBinning;
        var raw = new double[FW * FH];

        for (int y = 0; y < FH; y++)
        {
            for (int x = 0; x < FW; x++)
            {
                int i = y * FW + x;

                double dx = (x - centreX) * pitch, dy = (y - centreY) * pitch;
                double illumination = FocalPlaneIllumination.Factor(
                    dx, dy, FFocalLengthMetres, double.NaN, double.NaN);
                double response = illumination * SensorNonUniformity.PhotoResponse(prnu, i);

                // A dust mote is an out-of-focus shadow, taken here as a smooth disc rather than as a
                // real defocused pupil. What the test needs from it is that it is LOCAL and that it
                // belongs to the filter, not its exact profile.
                double mx = x - moteXPx, my = y - moteYPx;
                double mr = Math.Sqrt(mx * mx + my * my);
                if (mr < moteRadiusPx)
                {
                    double t = mr / moteRadiusPx;
                    response *= 1.0 - moteDepth * (1.0 - t * t);
                }

                // Accessory vignetting from an undersized filter cell, which is what puts the deep
                // corners in most real amateur flats and differs from one filter to the next.
                double vx = x - centreX, vy = y - centreY;
                double t2 = Math.Min(1.0, Math.Sqrt(vx * vx + vy * vy) / vignetteScalePx);
                response *= 1.0 - 0.5 * t2 * t2 * t2 * t2;

                raw[i] = FPedestal + FLevel * response;
            }
        }
        return raw;
    }

    // The measured branch, end to end through the shipped reader and the shipped flat builder, then
    // packed to half precision as a deviation from unity because that is how the frame builder stores
    // it. Packing here rather than comparing the full-precision result means the map under test is
    // the map that would actually be applied.
    private static double[] MeasuredResponse(string path)
    {
        var image = FitsImageReader.Read(path);
        var flat = MeasuredFlatField.Build(image, FW, FH, FPedestal, FAdcMax, FGain);
        var map = new double[flat.Response.Length];
        for (int i = 0; i < map.Length; i++)
            map[i] = 1.0 + Float16.ToDouble(Float16.FromDouble(flat.Response[i] - 1.0));
        return map;
    }

    // EnsureFlatFieldMap's modelled branch, through the same shipped Core it uses. This array spans
    // 1.26 mm at f = 0.8 m, so the cosine-fourth term is about one part in a million corner to
    // centre and what survives is essentially the photo-response spread; that is a fact about a
    // small chip on a long focal length rather than a shortcut, and the branch's job here is only to
    // be the distinct third map the cache has to be able to fall back to.
    private static double[] ModelledFlat()
    {
        int n = FW * FH;
        double centreX = 0.5 * (FW - 1), centreY = 0.5 * (FH - 1);
        double pitch = FPixelPitchMetres * FBinning;
        double sigma = SensorNonUniformity.BinnedPhotoResponseSigma(FPrnuFraction, FBinning);
        ushort[] prnu = SensorNonUniformity.BuildPhotoResponseMap(Pcg32.MixSeed(FSerialSeed), n, sigma);

        var map = new double[n];
        for (int y = 0; y < FH; y++)
        {
            for (int x = 0; x < FW; x++)
            {
                int i = y * FW + x;
                double dx = (x - centreX) * pitch, dy = (y - centreY) * pitch;
                double illumination = FocalPlaneIllumination.Factor(
                    dx, dy, FFocalLengthMetres, double.NaN, double.NaN);
                double response = illumination * SensorNonUniformity.PhotoResponse(prnu, i);
                map[i] = 1.0 + Float16.ToDouble(Float16.FromDouble(response - 1.0));
            }
        }
        return map;
    }

    // EnsureFlatFieldMap's own structure: the observer's flat REPLACES the model wherever there is
    // one, and there is one per filter, which is the whole of why the map is filter-specific.
    private static double[] BuildAsTheFrameBuilderWould(CameraFilter filter)
    {
        string path = Path.Combine(outDir, FlatFileName(FCamera, filter, FBinning));
        if (File.Exists(path)) return MeasuredResponse(path);
        return ModelledFlat();
    }

    private static bool Same(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-12) return false;
        return true;
    }

    // The cache from SolarSystemCameraTexture.EnsureFlatFieldMap, in both forms.
    //
    //   keyOnFilter: true    if (flatFieldMap != null && flatFieldMapFilter == Filter) return;
    //                        flatFieldMap = null;
    //                        flatFieldMapFilter = Filter;
    //
    //   keyOnFilter: false   if (flatFieldMap != null) return;
    //
    // The second is the guard as it stood. Restated here rather than compiled because that file needs
    // Unity; the maps it is driven with are the shipped Core's, so what is reproduced is the decision
    // and not the physics it decides over.
    private sealed class FlatCache
    {
        private readonly bool keyOnFilter;
        private double[] map;
        private CameraFilter mapFilter = (CameraFilter)(-1);

        public int Builds;

        public FlatCache(bool keyOnFilter) { this.keyOnFilter = keyOnFilter; }

        public double[] Ensure(CameraFilter filter, Func<CameraFilter, double[]> build)
        {
            if (map != null && (!keyOnFilter || mapFilter == filter)) return map;

            map = null;
            mapFilter = filter;
            map = build(filter);
            Builds++;
            return map;
        }
    }

    // ------------------------------------------------------------------ a minimal writer

    // Deliberately NOT Visualization/FitsWriter.cs: that one needs Unity, writes 16-bit only, and
    // stamps a full instrument header. This writes the cases the reader has to handle.
    private static void WriteFits(string path, double[] values, int w, int h,
                                  int bitpix, double bzero, double bscale, string blank)
    {
        var cards = new List<string>
        {
            "SIMPLE  =                    T",
            "BITPIX  = " + bitpix.ToString(CultureInfo.InvariantCulture).PadLeft(20),
            "NAXIS   =                    2",
            "NAXIS1  = " + w.ToString(CultureInfo.InvariantCulture).PadLeft(20),
            "NAXIS2  = " + h.ToString(CultureInfo.InvariantCulture).PadLeft(20),
            "BZERO   = " + bzero.ToString("R", CultureInfo.InvariantCulture).PadLeft(20),
            "BSCALE  = " + bscale.ToString("R", CultureInfo.InvariantCulture).PadLeft(20),
        };
        if (blank != null) cards.Add("BLANK   = " + blank.PadLeft(20));

        int bytesPer = Math.Abs(bitpix) / 8;
        var data = new byte[values.Length * bytesPer];

        for (int i = 0; i < values.Length; i++)
        {
            double stored = (values[i] - bzero) / bscale;
            int at = i * bytesPer;
            switch (bitpix)
            {
                case 8: data[at] = (byte)Math.Round(stored); break;
                case 16:
                    {
                        short v = (short)Math.Round(stored);
                        data[at] = (byte)((v >> 8) & 0xFF); data[at + 1] = (byte)(v & 0xFF);
                        break;
                    }
                case 32:
                    {
                        int v = (int)Math.Round(stored);
                        for (int b = 0; b < 4; b++) data[at + b] = (byte)((v >> (24 - 8 * b)) & 0xFF);
                        break;
                    }
                case 64:
                    {
                        long v = (long)Math.Round(stored);
                        for (int b = 0; b < 8; b++) data[at + b] = (byte)((v >> (56 - 8 * b)) & 0xFF);
                        break;
                    }
                case -32:
                    {
                        byte[] le = BitConverter.GetBytes((float)stored);
                        for (int b = 0; b < 4; b++) data[at + b] = le[3 - b];
                        break;
                    }
                default:
                    {
                        byte[] le = BitConverter.GetBytes(stored);
                        for (int b = 0; b < 8; b++) data[at + b] = le[7 - b];
                        break;
                    }
            }
        }

        WriteFitsRaw(path, cards.ToArray(), data);
    }

    private static void WriteFitsRaw(string path, string[] cards, byte[] data)
    {
        var sb = new StringBuilder();
        foreach (string c in cards) sb.Append(c.PadRight(80));
        sb.Append("END".PadRight(80));
        int cardCount = sb.Length / 80;
        int pad = (36 - (cardCount % 36)) % 36;
        sb.Append(new string(' ', pad * 80));

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(header, 0, header.Length);
            stream.Write(data, 0, data.Length);
            int rem = data.Length % 2880;
            if (rem != 0) stream.Write(new byte[2880 - rem], 0, 2880 - rem);
        }
    }
}
