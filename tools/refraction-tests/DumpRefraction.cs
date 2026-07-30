using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

/// <summary>
/// Dumps the refractive index of air, the differential refraction it produces, and the chromatic
/// kernel built from both, for PyAstronomy to check and for the geometry to be verified against the
/// values it is supposed to reproduce.
///
/// Three things fail silently here. A wrong coefficient in the dispersion formula gives a plausible
/// index and a wrong smear. A sign error in the differential puts the blue end of the smear on the
/// wrong side, which no photometric test would notice. And a chromatic kernel that is subtly
/// mis-weighted still looks like a PSF.
/// </summary>
static class DumpRefraction
{
    static void Main()
    {
        DumpIndex();
        DumpDifferential();
        DumpChromaticKernel();
        Console.WriteLine("written exo_index.csv, exo_differential.csv, exo_chromatic.csv");
    }

    /// <summary>The refractive index over the optical range, at standard conditions and at real sites.</summary>
    static void DumpIndex()
    {
        var sb = new StringBuilder();
        sb.AppendLine("wavelength_um,n_minus_1_standard,n_minus_1_paranal_dry,n_minus_1_paranal_humid,n_minus_1_sealevel");
        double paranalP = AtmosphericRefraction.StandardPressureMillibar(2635.0);
        double paranalT = AtmosphericRefraction.StandardTemperatureCelsius(2635.0);
        double paranalF = AtmosphericRefraction.WaterVapourPressureMillibar(
            paranalT, AtmosphericRefraction.DefaultRelativeHumidity);

        for (double um = 0.32; um <= 1.05; um += 0.005)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4:R}",
                um,
                AtmosphericRefraction.RefractivityStandard(um),
                AtmosphericRefraction.Refractivity(um, paranalT, paranalP, 0.0),
                AtmosphericRefraction.Refractivity(um, paranalT, paranalP, paranalF),
                AtmosphericRefraction.Refractivity(um, 15.0, 1013.25, 0.0)));
        }
        File.WriteAllText("exo_index.csv", sb.ToString());

        Console.WriteLine($"  Paranal (2635 m): {paranalP:F1} mbar, {paranalT:F1} C, "
                        + $"water vapour {paranalF:F2} mbar at {AtmosphericRefraction.DefaultRelativeHumidity:P0} humidity");
    }

    /// <summary>Differential refraction across the optical band, over zenith distance, per instrument.</summary>
    static void DumpDifferential()
    {
        var sb = new StringBuilder();
        sb.AppendLine("zenith_deg,refraction_5500_arcsec,diff_400_700_arcsec,diff_486_656_arcsec");
        for (double z = 0.0; z <= 80.0; z += 1.0)
        {
            double n = AtmosphericRefraction.Refractivity(0.55, 15.0, 1013.25, 0.0);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R}",
                z,
                AtmosphericRefraction.RefractionArcsec(n, z),
                AtmosphericRefraction.DifferentialRefractionArcsec(0.40, 0.70, z, 15.0, 1013.25, 0.0),
                AtmosphericRefraction.DifferentialRefractionArcsec(0.4861, 0.6563, z, 15.0, 1013.25, 0.0)));
        }
        File.WriteAllText("exo_differential.csv", sb.ToString());

        Console.WriteLine("\n  What the smear is worth on each instrument, 400-700 nm at 45 deg zenith distance:");
        double smear45 = AtmosphericRefraction.DifferentialRefractionArcsec(0.40, 0.70, 45.0, 15.0, 1013.25, 0.0);
        foreach (var spec in VisualTelescopeCatalog.All)
        {
            int binning = spec.CameraName.Contains("ZIMPOL") ? 2 : 1;
            double scale = spec.NativePixelSizeMeters * binning
                         / (spec.FocalLengthMeters * spec.BarlowFactor) * (180.0 * 3600.0 / Math.PI);
            double p = AtmosphericRefraction.StandardPressureMillibar(spec.SiteAltitudeMeters);
            double t = AtmosphericRefraction.StandardTemperatureCelsius(spec.SiteAltitudeMeters);
            double site = AtmosphericRefraction.DifferentialRefractionArcsec(0.40, 0.70, 45.0, t, p, 0.0);
            Console.WriteLine($"    {spec.CameraName,-22} {scale:F4}\"/px at {spec.SiteAltitudeMeters,5:F0} m: "
                            + $"{site:F3}\" = {site / scale,7:F1} px of smear");
        }
        Console.WriteLine($"    (at sea level and standard conditions the same figure is {smear45:F3}\")");
    }

    /// <summary>
    /// The chromatic kernel, against the two things it must reduce to: a single sub-band with no
    /// dispersion has to be bit-comparable with the monochromatic kernel, and a dispersed set has to
    /// have its first moment where the weighted mean offset says.
    /// </summary>
    static void DumpChromaticKernel()
    {
        var spec = VisualTelescopeCatalog.Rc20;
        double scale = spec.NativePixelSizeMeters / (spec.FocalLengthMeters * spec.BarlowFactor)
                     * (180.0 * 3600.0 / Math.PI);
        double reference = 550e-9;
        double seeing = spec.ZenithSeeingFwhmArcsec;

        var sb = new StringBuilder();
        sb.AppendLine("case,zenith_deg,radius_px,sum,centroid_x,centroid_y,rms_major,rms_minor,"
                    + "expected_centroid_x,mono_max_abs_diff");

        // 1. One sub-band, no offset: must equal the monochromatic kernel.
        var single = new List<ChromaticSubBand>
        {
            new ChromaticSubBand { WavelengthMeters = reference, Weight = 1.0, OffsetX = 0.0, OffsetY = 0.0 },
        };
        float[] chrom = OpticalPsf.BuildChromaticKernel(scale, spec.ApertureMeters,
            spec.SecondaryObstructionFraction, seeing, reference, 0.0,
            spec.SpiderVaneCount, spec.SpiderVaneWidthMeters, single, out int cr);
        float[] mono = OpticalPsf.BuildKernel(scale, spec.ApertureMeters,
            spec.SecondaryObstructionFraction, reference, seeing, 0.0,
            spec.SpiderVaneCount, spec.SpiderVaneWidthMeters, out int mr);
        double monoDiff = 0.0;
        if (cr == mr && chrom != null && mono != null)
            for (int i = 0; i < chrom.Length; i++)
                monoDiff = Math.Max(monoDiff, Math.Abs(chrom[i] - mono[i]));
        else monoDiff = double.NaN;
        Row(sb, "single_subband", 0.0, chrom, cr, 0.0, monoDiff);

        // 2. A real passband, dispersed, at three zenith distances. The kernel must lengthen with
        // tan z and its centroid must sit at the photon-weighted mean offset.
        var response = new SystemResponse(spec.LuminanceCentralWavelengthNm * 1e-9,
            spec.LuminanceBandwidthAngstrom, spec.LuminanceFilterPeakTransmission * spec.OpticsTransmission,
            null, spec.QuantumEfficiencyCurve, spec.QuantumEfficiency, 1.0, spec.SiteAltitudeMeters);
        double p = AtmosphericRefraction.StandardPressureMillibar(spec.SiteAltitudeMeters);
        double t = AtmosphericRefraction.StandardTemperatureCelsius(spec.SiteAltitudeMeters);

        foreach (double z in new[] { 0.0, 30.0, 45.0, 60.0 })
        {
            ChromaticSubBand[] bands = AtmosphericRefraction.SplitPassband(
                response, l => Colorimetry.PlanckSpectralRadiance(l * 1e9, 6000.0) * l,
                400e-9, 800e-9, 16, z, scale, 1.0, 0.0, reference, t, p, 0.0);
            if (bands == null) continue;

            double wsum = 0.0, wox = 0.0;
            foreach (var band in bands) { wsum += band.Weight; wox += band.Weight * band.OffsetX; }
            double expected = wsum > 0.0 ? wox / wsum : 0.0;

            float[] k = OpticalPsf.BuildChromaticKernel(scale, spec.ApertureMeters,
                spec.SecondaryObstructionFraction, seeing, reference, 0.0,
                spec.SpiderVaneCount, spec.SpiderVaneWidthMeters, bands, out int r);
            Row(sb, "dispersed", z, k, r, expected, double.NaN);
        }
        File.WriteAllText("exo_chromatic.csv", sb.ToString());
    }

    static void Row(StringBuilder sb, string name, double z, float[] k, int radius,
                    double expectedCentroidX, double monoDiff)
    {
        if (k == null) { sb.AppendLine($"{name},{z},0,0,0,0,0,0,0,0"); return; }
        int size = 2 * radius + 1;
        double sum = 0.0, cx = 0.0, cy = 0.0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double v = k[y * size + x];
                sum += v; cx += v * (x - radius); cy += v * (y - radius);
            }
        if (sum > 0.0) { cx /= sum; cy /= sum; }

        double mxx = 0.0, myy = 0.0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double v = k[y * size + x];
                if (v <= 0.0) continue;
                double dx = x - radius - cx, dy = y - radius - cy;
                mxx += v * dx * dx; myy += v * dy * dy;
            }
        if (sum > 0.0) { mxx = Math.Sqrt(mxx / sum); myy = Math.Sqrt(myy / sum); }

        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0},{1:R},{2},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R}",
            name, z, radius, sum, cx, cy, mxx, myy, expectedCentroidX, monoDiff));
    }
}
