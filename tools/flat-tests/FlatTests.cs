using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

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
