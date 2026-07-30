using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Builds synthetic star fields from the shipped Core and writes them out for photutils to measure.
///
/// WHAT THIS IS FOR. Every other harness in tools/ checks one mechanism against a reference. This
/// one checks that the mechanisms COMPOSE: stars of known magnitude go in, a frame comes out, and an
/// independent photometry package has to get the magnitudes back. It is the only test here that can
/// catch an error of assembly (a zero point that disagrees with the electron counts, a PSF that
/// does not conserve flux, a gain applied twice), because each of those is invisible to a test that
/// looks at one stage alone.
///
/// It also settles a disagreement the codebase names itself. CcdEquation.cs states that the transit
/// half and the imaging half "disagreed about what an instrument is": one predicted photometric
/// precision from an electron budget, the other rendered frames from a different one. Section 3 of
/// roundtrip.py measures the scatter of real aperture photometry across many noise realisations and
/// compares it with what CcdEquation predicts for the same frame. If the two halves agree, that is
/// the strongest single statement available about the whole chain.
///
/// WHAT IS REAL AND WHAT IS REPLICATED. The photon flux, the bandpass, the sky, the PSF kernel, the
/// RNG and both noise deviates are the shipped Core, called unmodified. The detector chain's last
/// four lines (bias, divide by K, floor, clip) are replicated here rather than called, because
/// RunDetectorChain lives in the Unity layer; they are four lines of arithmetic and they are
/// written out in full below so a divergence would be visible. Everything the replication depends
/// on (K, the pedestal, the ADC ceiling) comes from VisualTelescopeSpec.
///
/// Deliberately NOT modelled here, because the pipeline applies them and they would defeat the
/// measurement rather than test it: cosmic rays, hot and dead pixels, blooming and charge-transfer
/// smear. Each is a localised artefact whose job is to damage pixels; photometry of a clean field is
/// what establishes the chain, and the artefacts have their own checks in TESTING.md.
/// </summary>
static class MakeFrame
{
    const double ArcsecPerRad = 180.0 * 3600.0 / Math.PI;

    // The frame. Small enough to write as text and measure quickly, large enough that a sky
    // annulus around every star fits inside it.
    const int Size = 512;

    /// <summary>
    /// Border kept clear, and the spacing that follows from it. Not cosmetic: at 4 columns across
    /// 512 pixels the nearest neighbour sits 130 px away, its PSF reaches 30 px (the kernel radius),
    /// and the sky annulus below runs to 90 px; so no star's wings fall in another's background
    /// region. A tighter field measures neighbour contamination instead of photometry.
    /// </summary>
    const int Margin = 60;

    // The field: magnitudes spanning the regimes the CCD equation distinguishes, from
    // source-noise-limited at the bright end to sky-limited at the faint end.
    static readonly double[] Magnitudes = { 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0 };

    const double ExposureSeconds = 60.0;
    const double Gain = 1.0;
    const int Binning = 1;
    const double Airmass = 1.0;
    const int Realisations = 128;

    static void Main(string[] args)
    {
        VisualTelescopeSpec spec = VisualTelescopeCatalog.Rc20;

        double plateScale = spec.NativePixelSizeMeters * Binning / spec.FocalLengthMeters * ArcsecPerRad;
        double lambda = spec.LuminanceCentralWavelengthNm * 1e-9;
        double areaCm2 = ApertureAreaCm2(spec);

        double peak = spec.LuminanceFilterPeakTransmission > 0.0 ? spec.LuminanceFilterPeakTransmission : 1.0;
        var response = new SystemResponse(
            lambda, spec.LuminanceBandwidthAngstrom, peak * spec.OpticsTransmission,
            null, spec.QuantumEfficiencyCurve, spec.QuantumEfficiency, Airmass, spec.SiteAltitudeMeters);

        // Seeing at this airmass and wavelength, exactly as ComputeGroundSeeingFwhmArcsec derives it.
        double seeingFwhm = spec.ZenithSeeingFwhmArcsec
                          * Math.Pow(Airmass, 0.6)
                          * Math.Pow(lambda / 500e-9, -0.2);

        float[] kernel = OpticalPsf.BuildKernel(
            plateScale, spec.ApertureMeters, spec.SecondaryObstructionFraction, lambda,
            seeingFwhm, 0.0, spec.SpiderVaneCount, spec.SpiderVaneWidthMeters, out int kr);

        // Sky: the model's own dark-zenith value at this site, through the same response.
        double skyMag = SkyBrightnessModel.DarkSkyZenithVMagPerArcsec2;
        double skyPerPixelPerSecond = SkyBrightnessModel.ElectronsPerPixelPerSecond(
            skyMag, plateScale, response, areaCm2, 1.0, SourceSpectra.SolarPhotosphereTemperatureK);
        double skyElectrons = skyPerPixelPerSecond * ExposureSeconds;

        double darkPerSecond = DarkCurrentModel.ElectronsPerSecond(
            spec.DarkCurrentElectronsPerSecond, spec.DetectorTemperatureCelsius, spec.DetectorTemperatureCelsius);
        double darkElectrons = darkPerSecond * Binning * Binning * ExposureSeconds;

        double k = spec.ElectronsPerAduAtUnityGain / Gain;
        double biasAdu = spec.EffectiveBiasLevelAdu(k);
        int adcMax = (1 << Math.Max(1, spec.AdcBits)) - 1;
        double saturationElectrons = Math.Min(
            spec.FullWellElectrons * Binning * Binning,
            k * Math.Max(0.0, adcMax - biasAdu));

        // A star's own effective width carries its colour. These are all taken as solar-type, so
        // one width serves the whole field and the truth catalogue is unambiguous.
        double width = response.EffectiveWidthAngstromForTemperature(SourceSpectra.SolarPhotosphereTemperatureK);
        double flatWidth = response.EffectiveWidthAngstromFlat;

        // The zero point the FITS header would carry, from the same expression the pipeline uses.
        double magZero = 2.5 * Math.Log10(
            PhotonFluxModel.ZeroMagPhotonFluxPerAngstrom * flatWidth * areaCm2 / k);

        // ---- truth catalogue -------------------------------------------------------------
        // Sub-pixel positions on purpose: a star landing exactly on a pixel centre would hide any
        // interpolation error in the deposition.
        int n = Magnitudes.Length;
        var xs = new double[n];
        var ys = new double[n];
        var electrons = new double[n];
        for (int i = 0; i < n; i++)
        {
            int cols = 4;
            double cell = (Size - 2.0 * Margin) / (cols - 1.0);
            xs[i] = Margin + (i % cols) * cell + 0.37;
            ys[i] = Margin + (i / cols) * cell + 0.61;
            electrons[i] = PhotonFluxModel.CollectedElectrons(Magnitudes[i], width, areaCm2, ExposureSeconds);
        }

        var truth = new StringBuilder();
        truth.AppendLine("x_px,y_px,v_mag,electrons_total");
        for (int i = 0; i < n; i++)
            truth.AppendLine(Inv($"{xs[i]:R},{ys[i]:R},{Magnitudes[i]:R},{electrons[i]:R}"));
        File.WriteAllText("truth.csv", truth.ToString());

        // ---- the noiseless signal plane, built once ---------------------------------------
        var signal = new double[Size * Size];
        for (int i = 0; i < n; i++) DepositStar(signal, xs[i], ys[i], electrons[i], kernel, kr);

        // What fraction of each star's flux the kernel actually put on the frame. Not a fudge: the
        // kernel is truncated, so a star near the edge loses wing flux off the frame, and the
        // Python side needs to know the truth includes it.
        double depositedTotal = 0.0;
        foreach (double v in signal) depositedTotal += v;
        double injectedTotal = 0.0;
        foreach (double e in electrons) injectedTotal += e;

        // ---- realisations -----------------------------------------------------------------
        Directory.CreateDirectory("frames");
        for (int r = 0; r < Realisations; r++)
        {
            ulong seed = Pcg32.MixSeed(20260729L, r, (long)(ExposureSeconds * 1000.0), Binning);
            var rngShot = new Pcg32(seed, Pcg32.StreamShotNoise);
            var rngRead = new Pcg32(seed, Pcg32.StreamReadNoise);

            var adu = new int[Size * Size];
            for (int i = 0; i < signal.Length; i++)
            {
                // Charge collection: one Poisson draw over signal + sky + dark, exactly as
                // RunDetectorChain does it.
                double mean = Math.Max(0.0, signal[i] + skyElectrons + darkElectrons);
                double charge = NoiseSampler.Poisson(rngShot, mean);

                // Readout: the amplifier's Gaussian, in electrons, ahead of the converter.
                charge += NoiseSampler.Gaussian(rngRead, spec.ReadNoiseElectrons);

                // Digitisation, replicated from RunDetectorChain: pedestal, divide by K, floor to
                // an integer count, clip at 0 and at the converter's top code.
                double counts = Math.Floor(charge / k + biasAdu);
                if (counts < 0.0) counts = 0.0;
                else if (counts > adcMax) counts = adcMax;
                adu[i] = (int)counts;
            }
            WriteFrame($"frames/frame_{r:D3}.u16", adu);
        }

        // ---- what CcdEquation predicts for the same field ---------------------------------
        // The comparison target for section 3. Every argument comes from the frame that was just
        // written, so this is the same observation described two ways rather than two observations.
        double apertureRadiusArcsec = CcdEquation.OptimalApertureRadiusInFwhm * seeingFwhm;
        double aperturePixels = CcdEquation.AperturePixels(apertureRadiusArcsec, plateScale);
        double backgroundPixels = aperturePixels * CcdEquation.BackgroundToApertureAreaRatio;
        double enclosed = CcdEquation.GaussianEnclosedEnergy(CcdEquation.OptimalApertureRadiusInFwhm);

        var pred = new StringBuilder();
        pred.AppendLine("v_mag,electrons_total,electrons_in_aperture,predicted_relative_sigma,predicted_snr");
        for (int i = 0; i < n; i++)
        {
            double inAperture = electrons[i] * enclosed;
            double sigma = CcdEquation.RelativeFluxSigma(
                inAperture, aperturePixels, backgroundPixels,
                skyElectrons, darkElectrons, spec.ReadNoiseElectrons, k);
            pred.AppendLine(Inv($"{Magnitudes[i]:R},{electrons[i]:R},{inAperture:R},{sigma:R},{(sigma > 0 ? 1.0 / sigma : 0.0):R}"));
        }
        File.WriteAllText("ccd_equation_prediction.csv", pred.ToString());

        // ---- metadata ---------------------------------------------------------------------
        var meta = new StringBuilder();
        meta.AppendLine("key,value");
        Row(meta, "instrument", spec.Name);
        Row(meta, "camera", spec.CameraName);
        Num(meta, "size_px", Size);
        Num(meta, "realisations", Realisations);
        Num(meta, "exptime_s", ExposureSeconds);
        Num(meta, "gain_setting", Gain);
        Num(meta, "binning", Binning);
        Num(meta, "airmass", Airmass);
        Num(meta, "plate_scale_arcsec_px", plateScale);
        Num(meta, "wavelength_m", lambda);
        Num(meta, "aperture_area_cm2", areaCm2);
        Num(meta, "seeing_fwhm_arcsec", seeingFwhm);
        Num(meta, "kernel_fwhm_arcsec", OpticalPsf.MeasureKernelFwhmArcsec(kernel, kr, plateScale));
        Num(meta, "kernel_radius_px", kr);
        // The radius at which the kernel's own support ends, so an aperture of this size holds the
        // whole deposited star by construction rather than by assumption. The Python side measures
        // total flux there.
        Num(meta, "total_flux_radius_px", kr);
        Num(meta, "effective_width_solar_A", width);
        Num(meta, "effective_width_flat_A", flatWidth);
        Num(meta, "sky_vmag_arcsec2", skyMag);
        Num(meta, "sky_electrons_per_px", skyElectrons);
        Num(meta, "dark_electrons_per_px", darkElectrons);
        Num(meta, "read_noise_e", spec.ReadNoiseElectrons);
        Num(meta, "electrons_per_adu", k);
        Num(meta, "bias_adu", biasAdu);
        Num(meta, "adc_max", adcMax);
        Num(meta, "saturation_electrons", saturationElectrons);
        Num(meta, "magzero", magZero);
        Num(meta, "deposited_flux_fraction", injectedTotal > 0 ? depositedTotal / injectedTotal : 0.0);
        Num(meta, "ccd_aperture_radius_arcsec", apertureRadiusArcsec);
        Num(meta, "ccd_aperture_pixels", aperturePixels);
        Num(meta, "ccd_background_pixels", backgroundPixels);
        Num(meta, "ccd_enclosed_energy", enclosed);
        File.WriteAllText("meta.csv", meta.ToString());

        DumpNoiseSamples();

        Console.WriteLine($"written {Realisations} frames, truth.csv, meta.csv, " +
                          "ccd_equation_prediction.csv, noise_samples_*.csv");
    }

    /// <summary>
    /// Raw draws from Core.NoiseSampler, for SciPy to test the distributions of.
    ///
    /// The means bracket PtrsThreshold deliberately: 9.9, 10.0 and 10.1 sit either side of the
    /// switch from Knuth's method to PTRS, which is where a sampling bug would hide without ever
    /// throwing, and would then quietly bias every noise statistic downstream of it.
    /// </summary>
    static void DumpNoiseSamples()
    {
        double[] lambdas = { 0.05, 0.5, 2.0, 9.9, 10.0, 10.1, 50.0, 1000.0, 150000.0 };
        const int Draws = 200000;

        var sb = new StringBuilder();
        sb.AppendLine("lambda,sample");
        foreach (double lam in lambdas)
        {
            var rng = new Pcg32(0xC0FFEEUL, Pcg32.StreamShotNoise);
            for (int i = 0; i < Draws; i++)
                sb.AppendLine(Inv($"{lam:R},{NoiseSampler.Poisson(rng, lam):R}"));
        }
        File.WriteAllText("noise_samples_poisson.csv", sb.ToString());

        var g = new StringBuilder();
        g.AppendLine("sigma,sample");
        var grng = new Pcg32(0xBEEFUL, Pcg32.StreamReadNoise);
        for (int i = 0; i < Draws; i++)
            g.AppendLine(Inv($"1.2,{NoiseSampler.Gaussian(grng, 1.2):R}"));
        File.WriteAllText("noise_samples_gaussian.csv", g.ToString());
    }

    /// <summary>
    /// Puts one star's electrons on the plane at a sub-pixel position, by shifting the PSF kernel
    /// with bilinear weights over the four pixels the fractional position straddles.
    ///
    /// The kernel is already normalised to unit sum, and bilinear weights also sum to one, so the
    /// deposit conserves flux exactly except where the kernel runs off the frame, which the
    /// caller reports as deposited_flux_fraction rather than hiding.
    /// </summary>
    static void DepositStar(double[] plane, double x, double y, double electrons, float[] kernel, int radius)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double[] wx = { 1.0 - fx, fx };
        double[] wy = { 1.0 - fy, fy };
        int size = 2 * radius + 1;

        for (int oy = 0; oy < 2; oy++)
        for (int ox = 0; ox < 2; ox++)
        {
            double w = wx[ox] * wy[oy] * electrons;
            if (w <= 0.0) continue;
            for (int ky = -radius; ky <= radius; ky++)
            {
                int py = y0 + oy + ky;
                if (py < 0 || py >= Size) continue;
                for (int kx = -radius; kx <= radius; kx++)
                {
                    int px = x0 + ox + kx;
                    if (px < 0 || px >= Size) continue;
                    plane[py * Size + px] += w * kernel[(ky + radius) * size + (kx + radius)];
                }
            }
        }
    }

    static double ApertureAreaCm2(VisualTelescopeSpec s)
    {
        double r = s.ApertureMeters / 2.0;
        return Math.PI * r * r * (1.0 - s.SecondaryObstructionFraction * s.SecondaryObstructionFraction) * 1.0e4;
    }

    /// <summary>
    /// Raw little-endian unsigned 16-bit, row-major: the same width the ADC's counts occupy and
    /// the same width a FITS BITPIX 16 frame carries them in. Text would be 128 frames of 512x512
    /// decimal numbers for no gain.
    /// </summary>
    static void WriteFrame(string path, int[] adu)
    {
        var bytes = new byte[adu.Length * 2];
        for (int i = 0; i < adu.Length; i++)
        {
            ushort v = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, adu[i]));
            bytes[2 * i] = (byte)(v & 0xFF);
            bytes[2 * i + 1] = (byte)(v >> 8);
        }
        File.WriteAllBytes(path, bytes);
    }

    static string Inv(FormattableString s) => FormattableString.Invariant(s);
    static void Row(StringBuilder sb, string k, string v) => sb.AppendLine($"{k},{v}");
    static void Num(StringBuilder sb, string k, double v) => sb.AppendLine(Inv($"{k},{v:R}"));
}
