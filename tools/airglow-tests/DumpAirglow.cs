using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

/// <summary>
/// Dumps what the airglow model delivers, for the Python side to compare against ESO's SkyCalc
/// queried independently, and the two headline numbers the whole construction stands on: the
/// V surface brightness must come out at the measured Paranal dark sky, and an H-alpha filter must
/// see a far darker sky than a broadband one, because that asymmetry is what the table exists to
/// express.
/// </summary>
static class DumpAirglow
{
    static void Main()
    {
        DumpDensity();
        DumpVanRhijn();
        DumpBands();
        DumpVSurfaceBrightness();
        Console.WriteLine("written exo_airglow_density.csv, exo_vanrhijn.csv, exo_airglow_bands.csv, exo_airglow_v.csv");
    }

    /// <summary>The stored spectral density, resampled through the same accessor the pipeline uses.</summary>
    static void DumpDensity()
    {
        var sb = new StringBuilder();
        sb.AppendLine("wavelength_nm,lines_r_per_nm,continuum_r_per_nm");
        for (double l = 350.05; l < 1000.0; l += 0.1)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}",
                l, Airglow.LineDensityAtZenith(l), Airglow.ContinuumDensityAtZenith(l)));
        File.WriteAllText("exo_airglow_density.csv", sb.ToString());
    }

    static void DumpVanRhijn()
    {
        var sb = new StringBuilder();
        sb.AppendLine("zenith_deg,factor_90km,factor_250km,sec_z");
        for (double z = 0.0; z <= 85.0; z += 1.0)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R}",
                z, Airglow.VanRhijnFactor(z, Airglow.MainLayerHeightKm),
                Airglow.VanRhijnFactor(z, Airglow.RedLineLayerHeightKm),
                1.0 / Math.Cos(z * Math.PI / 180.0)));
        File.WriteAllText("exo_vanrhijn.csv", sb.ToString());
    }

    /// <summary>What each filter position of the RC20 actually sees, which is the asymmetry the table exists for.</summary>
    static void DumpBands()
    {
        var spec = VisualTelescopeCatalog.Rc20;
        var sb = new StringBuilder();
        sb.AppendLine("filter,centre_nm,width_nm,rayleighs_in_band,line_share,electrons_per_px_s");
        double scale = spec.NativePixelSizeMeters / (spec.FocalLengthMeters * spec.BarlowFactor)
                     * (180.0 * 3600.0 / Math.PI);
        double area = Math.PI * Math.Pow(spec.ApertureMeters / 2.0, 2)
                    * (1.0 - Math.Pow(spec.SecondaryObstructionFraction, 2)) * 1e4;

        (string name, double centreNm, double widthNm)[] bands =
        {
            ("Luminance", spec.LuminanceCentralWavelengthNm, spec.LuminanceBandwidthAngstrom * 0.1),
            ("Red", spec.RedCentralWavelengthNm, spec.RedBandwidthAngstrom * 0.1),
            ("Green", spec.GreenCentralWavelengthNm, spec.GreenBandwidthAngstrom * 0.1),
            ("Blue", spec.BlueCentralWavelengthNm, spec.BlueBandwidthAngstrom * 0.1),
            ("HAlpha", spec.HAlphaCentralWavelengthNm, spec.HAlphaBandwidthAngstrom * 0.1),
            ("OI6300", 630.0, 3.0),
            ("SII", 671.6, 3.0),
            ("OIII", 500.7, 3.0),
        };
        Console.WriteLine("\n  what each band sees from the RC20's site, zenith:");
        foreach (var band in bands)
        {
            var response = new SystemResponse(band.centreNm * 1e-9, band.widthNm * 10.0,
                spec.OpticsTransmission, null, spec.QuantumEfficiencyCurve, spec.QuantumEfficiency,
                1.0, spec.SiteAltitudeMeters);
            double rayleighs = Airglow.RayleighsInBand(response, 0.0, out double lineShare);
            double electrons = Airglow.ElectronsPerPixelPerSecond(response, scale, area, 0.0);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R},{4:R},{5:R}",
                band.name, band.centreNm, band.widthNm, rayleighs, lineShare, electrons));
            Console.WriteLine($"    {band.name,-10} {band.centreNm,6:F1} nm x {band.widthNm,5:F1} nm: "
                            + $"{rayleighs,8:F1} R  ({lineShare * 100,4:F0}% lines)  {electrons,10:F4} e-/px/s");
        }
        File.WriteAllText("exo_airglow_bands.csv", sb.ToString());
    }

    static void DumpVSurfaceBrightness()
    {
        var sb = new StringBuilder();
        sb.AppendLine("zenith_deg,v_mag_per_arcsec2");
        Console.WriteLine("\n  V surface brightness of the airglow alone:");
        foreach (double z in new[] { 0.0, 30.0, 45.0, 60.0, 70.0, 80.0 })
        {
            double v = Airglow.VBandMagPerArcsec2(z);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R}", z, v));
            Console.WriteLine($"    z = {z,4:F0} deg: V = {v:F2} mag/arcsec^2");
        }
        File.WriteAllText("exo_airglow_v.csv", sb.ToString());

        var vb = new StringBuilder();
        vb.AppendLine("wavelength_nm,transmission");
        for (double l = 460.0; l <= 710.0; l += 1.0)
            vb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R}",
                l, Airglow.JohnsonVTransmission(l)));
        File.WriteAllText("exo_bessellv.csv", vb.ToString());
    }
}
