using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

/// <summary>
/// Dumps the colorimetric chain for colour-science to check: the tabulated standard observer, the
/// blackbody locus, monochromatic stimuli, the sRGB transfer function and the gamut mapping.
///
/// Colour is the one thing in this mod a reader judges by eye, and a wrong colour is invisible to
/// every other test: a transcription error in the colour matching functions, a transposed sRGB
/// matrix or a gamma applied twice all produce images that still look like images.
/// </summary>
static class DumpColour
{
    static void Main()
    {
        DumpTable();
        DumpBlackbody();
        DumpMonochromatic();
        DumpTransfer();
        DumpLegacyComparison();
        DumpCalibration();
        Console.WriteLine("written exo_cmf.csv, exo_blackbody.csv, exo_mono.csv, exo_transfer.csv, exo_legacy.csv");
    }

    /// <summary>The interpolated standard observer, including between table entries and outside the range.</summary>
    static void DumpTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("wavelength_nm,xbar,ybar,zbar");
        for (double l = 340.0; l <= 850.0; l += 0.25)
        {
            Colorimetry.ColourMatchingFunctions(l, out double x, out double y, out double z);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R}", l, x, y, z));
        }
        File.WriteAllText("exo_cmf.csv", sb.ToString());
    }

    /// <summary>The Planckian locus in chromaticity, and the display tint, over every temperature a star or a planet takes.</summary>
    static void DumpBlackbody()
    {
        var sb = new StringBuilder();
        sb.AppendLine("temperature_k,X,Y,Z,x,y,r,g,b");
        double[] temps = { 300, 500, 800, 1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500, 5000,
                           5500, 5778, 6000, 6500, 7000, 8000, 9000, 10000, 12000, 15000, 20000,
                           25000, 30000, 40000, 50000 };
        foreach (double t in temps)
        {
            Colorimetry.SpectrumToXyz(l => Colorimetry.PlanckSpectralRadiance(l, t),
                                      out double X, out double Y, out double Z);
            Colorimetry.XyzToChromaticity(X, Y, Z, out double cx, out double cy);
            Colorimetry.BlackbodyDisplayRgb(t, out double r, out double g, out double b);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R}", t, X, Y, Z, cx, cy, r, g, b));
        }
        File.WriteAllText("exo_blackbody.csv", sb.ToString());
    }

    /// <summary>
    /// Single emission lines, which is what a nebula is. These sit ON the spectral locus, outside
    /// every display gamut, so they exercise the gamut mapping at its limit -- and the lines the
    /// mod actually renders are in the list.
    /// </summary>
    static void DumpMonochromatic()
    {
        var sb = new StringBuilder();
        sb.AppendLine("wavelength_nm,x,y,r_linear,g_linear,b_linear,desaturation,r_display,g_display,b_display");
        double[] lines = { 372.6, 434.0, 486.1, 495.9, 500.7, 630.0, 654.8, 656.3, 658.3, 671.6, 673.1 };
        for (int i = 0; i < lines.Length + 49; i++)
        {
            double l = i < lines.Length ? lines[i] : 400.0 + (i - lines.Length) * 6.0;

            // Normalised to a mid-grey LUMINANCE first. Otherwise the chain's final clip at display
            // white is mixed into the measurement, and the gamut mapping -- which is about negative
            // components, not about brightness -- gets blamed for it.
            Colorimetry.LineToXyz(l, 1.0, out double y0, out double yy, out double y2);
            double power = yy > 1e-12 ? 0.3 / yy : 0.0;
            Colorimetry.LineToXyz(l, power, out double X, out double Y, out double Z);
            Colorimetry.XyzToChromaticity(X, Y, Z, out double cx, out double cy);
            Colorimetry.XyzToLinearSrgb(X, Y, Z, out double lr, out double lg, out double lb);
            double dr = lr, dg = lg, db = lb;
            double desat = Colorimetry.MapIntoGamut(ref dr, ref dg, ref db);
            Colorimetry.XyzToDisplaySrgb(X, Y, Z, out double sr, out double sg, out double sb2);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R}",
                l, cx, cy, lr, lg, lb, desat, sr, sg, sb2));
        }
        File.WriteAllText("exo_mono.csv", sb.ToString());
    }

    static void DumpTransfer()
    {
        var sb = new StringBuilder();
        sb.AppendLine("linear,encoded,round_trip");
        for (int i = 0; i <= 400; i++)
        {
            double v = i / 400.0;
            double e = Colorimetry.LinearToSrgbTransfer(v);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}",
                v, e, Colorimetry.SrgbTransferToLinear(e)));
        }
        File.WriteAllText("exo_transfer.csv", sb.ToString());
    }

    /// <summary>
    /// The band-to-tristimulus transform for every instrument that has R, G and B, and what it
    /// cannot do.
    ///
    /// Three bands determine colour up to a 3x3 matrix, and the matrix has a residual because two
    /// spectra with the same three band counts are metamers and must come out the same colour. What
    /// matters is how big that residual is on the spectra an instrument actually sees, so it is
    /// measured on the training set and printed rather than assumed small.
    /// </summary>
    static void DumpCalibration()
    {
        var sb = new StringBuilder();
        sb.AppendLine("instrument,rms_residual,worst_chromaticity,training,m00,m01,m02,m10,m11,m12,m20,m21,m22");
        Console.WriteLine();

        // THE FITTER ITSELF, separated from the filter sets. Three bands proportional to the colour
        // matching functions ARE a colorimeter, so the fit against them must be essentially exact --
        // if it is not, the machinery is broken rather than the instrument limited.
        var ideal = new System.Collections.Generic.List<Func<double, double>>
        {
            m => Cmf(m, 0), m => Cmf(m, 1), m => Cmf(m, 2),
        };
        var idealFit = ColourCalibration.Fit(ideal);
        var continuumOnly = ColourCalibration.FitContinuumOnly(ideal);
        Console.WriteLine(idealFit == null
            ? "  ideal colorimeter        FIT FAILED"
            : $"  ideal colorimeter        rms {idealFit.RmsResidual * 100.0,5:F2}%   "
              + $"worst xy: continuum {idealFit.ContinuumChromaticityError:E2}, "
              + $"lines {idealFit.LineChromaticityError:E2}   <-- the fitter's own floor");
        if (continuumOnly != null)
            Console.WriteLine($"  ideal, continuum only    rms {continuumOnly.RmsResidual * 100.0,5:F2}%   "
                            + $"continuum xy median {continuumOnly.MedianContinuumChromaticityError:E2}, "
                            + $"worst {continuumOnly.ContinuumChromaticityError:E2}");
        Console.WriteLine();
        foreach (var spec in VisualTelescopeCatalog.All)
        {
            var bands = new System.Collections.Generic.List<SystemResponse>();
            bool complete = true;
            foreach (CameraFilter f in new[] { CameraFilter.Red, CameraFilter.Green, CameraFilter.Blue })
            {
                if (Array.IndexOf(spec.AvailableFilters, f) < 0) { complete = false; break; }
                bands.Add(BuildResponse(spec, f));
            }
            if (!complete)
            {
                Console.WriteLine($"  {spec.CameraName,-22} no R/G/B set -- cannot make true colour");
                continue;
            }

            var fit = ColourCalibration.Fit(bands);
            if (fit == null) { Console.WriteLine($"  {spec.CameraName,-22} degenerate filter set"); continue; }
            double[,] m = fit.Matrix;
            Console.WriteLine($"  {spec.CameraName,-22} rms {fit.RmsResidual * 100.0,5:F1}%   "
                            + $"continuum xy: median {fit.MedianContinuumChromaticityError:F4}, "
                            + $"worst {fit.ContinuumChromaticityError:F4}   "
                            + $"lines worst {fit.LineChromaticityError:F4}");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:R},{2:R},{3},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R},{10:R},{11:R},{12:R}",
                spec.CameraName.Replace(",", " "), fit.RmsResidual, fit.WorstChromaticityError,
                fit.TrainingSpectra,
                m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2], m[2, 0], m[2, 1], m[2, 2]));
        }
        File.WriteAllText("exo_calibration.csv", sb.ToString());
    }

    /// <summary>
    /// A band whose throughput IS one of the colour matching functions. Three of these are a
    /// colorimeter by definition, so fitting against them measures the fitting machinery rather than
    /// any instrument.
    /// </summary>
    static double Cmf(double wavelengthMeters, int which)
    {
        double nm = wavelengthMeters * 1e9;
        Colorimetry.ColourMatchingFunctions(nm, out double x, out double y, out double z);
        // DIVIDED BY WAVELENGTH, and that is the whole point of this control. Tristimulus values are
        // integrals of ENERGY against the colour matching functions; a detector counts PHOTONS, and
        // the two differ by hc/lambda inside the integral. So a photon-counting instrument whose
        // filters are shaped like x-bar is NOT a colorimeter -- it needs filters shaped like
        // x-bar/lambda. Getting this wrong left the control at 2.4% rms and made every real
        // instrument's residual uninterpretable.
        double scale = nm > 0.0 ? 1.0 / nm : 0.0;
        return (which == 0 ? x : which == 1 ? y : z) * scale;
    }

    static SystemResponse BuildResponse(VisualTelescopeSpec spec, CameraFilter f)
    {
        double centreNm, widthA, peak;
        switch (f)
        {
            case CameraFilter.Red: centreNm = spec.RedCentralWavelengthNm; widthA = spec.RedBandwidthAngstrom; peak = spec.RedFilterPeakTransmission; break;
            case CameraFilter.Green: centreNm = spec.GreenCentralWavelengthNm; widthA = spec.GreenBandwidthAngstrom; peak = spec.GreenFilterPeakTransmission; break;
            default: centreNm = spec.BlueCentralWavelengthNm; widthA = spec.BlueBandwidthAngstrom; peak = spec.BlueFilterPeakTransmission; break;
        }
        return new SystemResponse(centreNm * 1e-9, widthA, peak * spec.OpticsTransmission, null,
                                  spec.QuantumEfficiencyCurve, spec.QuantumEfficiency, 1.0,
                                  spec.SiteAltitudeMeters);
    }

    /// <summary>The curve fit this replaces, so the difference it was making is on record rather than asserted.</summary>
    static void DumpLegacyComparison()
    {
        var sb = new StringBuilder();
        sb.AppendLine("temperature_k,legacy_r,legacy_g,legacy_b,cie_r,cie_g,cie_b");
        for (double t = 1000.0; t <= 40000.0; t *= 1.05)
        {
            StellarColor.LegacyBlackbodyRgb(t, out double lr, out double lg, out double lb);
            Colorimetry.BlackbodyDisplayRgb(t, out double cr, out double cg, out double cb);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R}", t, lr, lg, lb, cr, cg, cb));
        }
        File.WriteAllText("exo_legacy.csv", sb.ToString());
    }
}
