using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Dumps the point-spread function ExoInstruments' shipped Core builds for each real instrument in
/// the roster, for comparison against GalSim.
///
/// Nothing here reimplements the physics. Every kernel comes out of OpticalPsf.BuildKernel /
/// BuildSeeingHaloKernel called exactly as SolarSystemCameraTexture.EnsurePsfKernels calls them,
/// and every instrument parameter is read from the shipped VisualTelescopeCatalog rather than
/// restated; so what the Python side compares is what the mod convolves a capture with.
///
/// Three kernels per instrument, because the comparison has to separate what it is testing:
///
///   *_diff  diffraction alone (atmospheric FWHM 0). Already cross-validated against POPPY in
///           tools/poppy-crossvalidation; carried here as a control, so that a disagreement in
///           the full kernel can be attributed to the atmosphere rather than left ambiguous.
///   *_atm   the long-exposure Kolmogorov term alone (BuildSeeingHaloKernel). This is the piece
///           POPPY could not check, and the reason this harness exists.
///   *_full  the two convolved, which is what a capture is actually blurred by.
///
/// Plus two grid-free dumps, which is the point of them: kolmogorov_profile.csv samples
/// AtmosphericIntensity continuously rather than on the kernel grid, so a disagreement there is
/// about the profile itself and not about sampling; and lambda_scaling.csv records what the mod's
/// own seeing law does with wavelength, which Kolmogorov theory predicts independently.
/// </summary>
class DumpKernel
{
    const double ArcsecPerRad = 180.0 * 3600.0 / Math.PI;

    /// <summary>Reference wavelength of the seeing figures, restated from SolarSystemCameraTexture.SeeingReferenceWavelengthMeters (private there, and a compile-time constant rather than a modelling choice this harness could get wrong silently: lambda_scaling.csv records it so the Python side checks against the value actually used).</summary>
    const double SeeingReferenceWavelengthMeters = 500e-9;

    /// <summary>Chromatic exponent of the seeing law, restated from ComputeGroundSeeingFwhmArcsec.</summary>
    const double SeeingChromaticExponent = -0.2;

    static void Main()
    {
        // The four instruments the imaging pipeline actually flies, at binning 1 and zenith.
        // SPHERE is excluded deliberately: its PSF is the two-component AO core-plus-halo, not
        // the ground-seeing kernel this harness compares, and GalSim has no AO residual model to
        // compare it against.
        Dump("redcat", VisualTelescopeCatalog.RedCat51);
        Dump("rc20", VisualTelescopeCatalog.Rc20);
        Dump("cdk1000", VisualTelescopeCatalog.Cdk1000);
        Dump("fors2", VisualTelescopeCatalog.Fors2Vlt);

        DumpKolmogorovProfile();
        DumpLambdaScaling();

        Console.WriteLine("written");
    }

    /// <summary>Plate scale exactly as SolarSystemCameraTexture.PlateScaleArcsecPerPixel derives it, at binning 1.</summary>
    static double PlateScale(VisualTelescopeSpec spec)
        => spec.NativePixelSizeMeters / spec.FocalLengthMeters * ArcsecPerRad;

    static void Dump(string tag, VisualTelescopeSpec spec)
    {
        double plateScale = PlateScale(spec);
        double lambda = spec.LuminanceCentralWavelengthNm * 1e-9;

        // Zenith (airmass 1), so the airmass^0.6 term is exactly 1 and only the chromatic factor
        // applies, the same expression ComputeGroundSeeingFwhmArcsec evaluates.
        double atmFwhm = spec.ZenithSeeingFwhmArcsec
                       * Math.Pow(lambda / SeeingReferenceWavelengthMeters, SeeingChromaticExponent);

        float[] diff = OpticalPsf.BuildKernel(
            plateScale, spec.ApertureMeters, spec.SecondaryObstructionFraction, lambda,
            0.0, 0.0, spec.SpiderVaneCount, spec.SpiderVaneWidthMeters, out int diffR);

        float[] full = OpticalPsf.BuildKernel(
            plateScale, spec.ApertureMeters, spec.SecondaryObstructionFraction, lambda,
            atmFwhm, 0.0, spec.SpiderVaneCount, spec.SpiderVaneWidthMeters, out int fullR);

        // MaxHaloKernelRadiusPx in the camera is 128; the halo builder clamps to whatever it is
        // given, and 128 is far more than these seeing discs need at these plate scales.
        float[] atm = OpticalPsf.BuildSeeingHaloKernel(plateScale, atmFwhm, lambda, 128, out int atmR);

        WriteKernel($"exo_{tag}_diff.csv", diff, diffR);
        WriteKernel($"exo_{tag}_full.csv", full, fullR);
        WriteKernel($"exo_{tag}_atm.csv", atm, atmR);

        var meta = new StringBuilder();
        meta.AppendLine("key,value");
        Row(meta, "name", spec.Name);
        Row(meta, "camera", spec.CameraName);
        Row(meta, "site", spec.SiteName);
        Num(meta, "aperture_m", spec.ApertureMeters);
        Num(meta, "obstruction", spec.SecondaryObstructionFraction);
        Num(meta, "focal_length_m", spec.FocalLengthMeters);
        Num(meta, "pixel_size_m", spec.NativePixelSizeMeters);
        Num(meta, "plate_scale_arcsec_px", plateScale);
        Num(meta, "wavelength_m", lambda);
        Num(meta, "zenith_seeing_arcsec", spec.ZenithSeeingFwhmArcsec);
        Num(meta, "atmospheric_fwhm_arcsec", atmFwhm);
        Num(meta, "fried_r0_m", OpticalPsf.FriedParameterMeters(atmFwhm, lambda));
        Row(meta, "vane_count", spec.SpiderVaneCount.ToString(CultureInfo.InvariantCulture));
        Num(meta, "vane_width_m", spec.SpiderVaneWidthMeters);
        Num(meta, "vane_width_over_diameter",
            spec.ApertureMeters > 0.0 ? spec.SpiderVaneWidthMeters / spec.ApertureMeters : 0.0);
        Num(meta, "lambda_over_d_arcsec", lambda / spec.ApertureMeters * ArcsecPerRad);
        Num(meta, "airy_fwhm_arcsec",
            OpticalPsf.AiryFwhmArcsec(spec.ApertureMeters, spec.SecondaryObstructionFraction, lambda));
        Row(meta, "radius_diff_px", diffR.ToString(CultureInfo.InvariantCulture));
        Row(meta, "radius_full_px", fullR.ToString(CultureInfo.InvariantCulture));
        Row(meta, "radius_atm_px", atmR.ToString(CultureInfo.InvariantCulture));
        Num(meta, "measured_fwhm_diff_arcsec", OpticalPsf.MeasureKernelFwhmArcsec(diff, diffR, plateScale));
        Num(meta, "measured_fwhm_full_arcsec", OpticalPsf.MeasureKernelFwhmArcsec(full, fullR, plateScale));
        Num(meta, "measured_fwhm_atm_arcsec", OpticalPsf.MeasureKernelFwhmArcsec(atm, atmR, plateScale));
        File.WriteAllText($"exo_{tag}_meta.csv", meta.ToString());
    }

    /// <summary>
    /// The long-exposure Kolmogorov profile sampled continuously in units of lambda/r0, which is
    /// the only argument it has once Fried's OTF is written in reduced form. Grid-free on purpose:
    /// this isolates the profile from every sampling and truncation choice on either side, so a
    /// disagreement here is a disagreement about the physics.
    ///
    /// Normalised to its own on-axis value, since AtmosphericIntensity carries an arbitrary
    /// overall constant (the kernel is normalised later, so the constant never reaches a frame).
    /// </summary>
    static void DumpKolmogorovProfile()
    {
        // Paranal's median seeing at FORS2's luminance wavelength: a real operating point rather
        // than a round number, so the comparison is at a configuration the mod actually runs.
        double lambda = 715e-9;
        double fwhmArcsec = 0.72;
        double r0 = OpticalPsf.FriedParameterMeters(fwhmArcsec, lambda);

        double peak = OpticalPsf.AtmosphericIntensity(0.0, r0, lambda);

        var sb = new StringBuilder();
        sb.AppendLine("theta_over_lambda_r0,intensity_norm");
        // Out to 20 lambda/r0: the Kolmogorov profile falls as theta^(-11/3), so this covers the
        // core and four decades of wing.
        for (int i = 0; i <= 4000; i++)
        {
            double x = i * 20.0 / 4000.0;                 // theta in units of lambda/r0
            double theta = x * lambda / r0;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R}",
                x, OpticalPsf.AtmosphericIntensity(theta, r0, lambda) / peak));
        }
        File.WriteAllText("exo_kolmogorov_profile.csv", sb.ToString());

        var meta = new StringBuilder();
        meta.AppendLine("key,value");
        Num(meta, "wavelength_m", lambda);
        Num(meta, "fwhm_arcsec", fwhmArcsec);
        Num(meta, "fried_r0_m", r0);
        Num(meta, "lambda_over_r0_arcsec", lambda / r0 * ArcsecPerRad);
        // The constant in FWHM = k * lambda / r0 that FriedParameterMeters inverts. Dumped rather
        // than assumed on the Python side: GalSim uses its own, and the difference between the two
        // is exactly what the matched-r0 / matched-FWHM comparison separates.
        Num(meta, "fwhm_over_lambda_r0_constant", 0.98);
        File.WriteAllText("exo_kolmogorov_profile_meta.csv", meta.ToString());
    }

    /// <summary>
    /// What the mod's seeing law does with wavelength, and what its finished kernel then delivers.
    ///
    /// Kolmogorov turbulence predicts r0 proportional to lambda^(6/5) and hence a seeing FWHM
    /// proportional to lambda^(-1/5), independently of any implementation. ComputeGroundSeeingFwhmArcsec
    /// applies that exponent by hand; this table lets the Python side check the hand-applied law
    /// against GalSim's, which derives it from r0 instead.
    /// </summary>
    static void DumpLambdaScaling()
    {
        VisualTelescopeSpec spec = VisualTelescopeCatalog.Rc20;
        double plateScale = PlateScale(spec);

        var sb = new StringBuilder();
        sb.AppendLine("wavelength_m,atmospheric_fwhm_arcsec,fried_r0_m,kernel_fwhm_arcsec,airy_fwhm_arcsec");
        double[] lambdas = { 400e-9, 450e-9, 500e-9, 552.5e-9, 600e-9, 656e-9, 700e-9, 800e-9 };
        foreach (double lambda in lambdas)
        {
            double atmFwhm = spec.ZenithSeeingFwhmArcsec
                           * Math.Pow(lambda / SeeingReferenceWavelengthMeters, SeeingChromaticExponent);
            float[] k = OpticalPsf.BuildKernel(
                plateScale, spec.ApertureMeters, spec.SecondaryObstructionFraction, lambda,
                atmFwhm, 0.0, spec.SpiderVaneCount, spec.SpiderVaneWidthMeters, out int r);

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4:R}",
                lambda,
                atmFwhm,
                OpticalPsf.FriedParameterMeters(atmFwhm, lambda),
                OpticalPsf.MeasureKernelFwhmArcsec(k, r, plateScale),
                OpticalPsf.AiryFwhmArcsec(spec.ApertureMeters, spec.SecondaryObstructionFraction, lambda)));
        }
        File.WriteAllText("exo_lambda_scaling.csv", sb.ToString());

        var meta = new StringBuilder();
        meta.AppendLine("key,value");
        Row(meta, "instrument", spec.Name);
        Num(meta, "plate_scale_arcsec_px", plateScale);
        Num(meta, "aperture_m", spec.ApertureMeters);
        Num(meta, "obstruction", spec.SecondaryObstructionFraction);
        Num(meta, "zenith_seeing_arcsec", spec.ZenithSeeingFwhmArcsec);
        Num(meta, "seeing_reference_wavelength_m", SeeingReferenceWavelengthMeters);
        Num(meta, "seeing_chromatic_exponent", SeeingChromaticExponent);
        File.WriteAllText("exo_lambda_scaling_meta.csv", meta.ToString());
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Writes a square kernel as (2R+1) rows of (2R+1) values, with the radius in a leading
    /// comment so the Python side does not have to infer it from the file's shape.
    /// </summary>
    static void WriteKernel(string path, float[] kernel, int radius)
    {
        if (kernel == null) { File.WriteAllText(path, "# radius=0\n"); return; }
        int size = 2 * radius + 1;

        var sb = new StringBuilder();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "# radius={0}", radius));
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (x > 0) sb.Append(',');
                sb.Append(kernel[y * size + x].ToString("R", CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    static void Row(StringBuilder sb, string key, string value) => sb.AppendLine($"{key},{value}");

    static void Num(StringBuilder sb, string key, double value)
        => sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R}", key, value));
}
