using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

/// <summary>
/// Dumps the emission-line photometry: the line list, the rayleigh conversion, and what a
/// narrowband filter buys over a broadband one on the same nebula.
/// </summary>
static class DumpEmission
{
    static void Main()
    {
        DumpLines();
        DumpRayleigh();
        DumpNarrowband();
        DumpRotation();
        Console.WriteLine("written exo_lines.csv, exo_rayleigh.csv, exo_narrowband.csv, exo_rotation.csv");
    }

    /// <summary>
    /// The (north, east, up) to Galactic rotation the emission deposit uses, against the literal
    /// chain it replaces. One is a matrix multiply, the other is four transforms; they must agree.
    /// </summary>
    static void DumpRotation()
    {
        var sb = new StringBuilder();
        sb.AppendLine("lst_deg,latitude_deg,alt_deg,az_deg,l_matrix,b_matrix,l_chain,b_chain");

        double[] lsts = { 0.0, 73.4, 180.0, 291.7 };
        double[] latitudes = { -24.6, 0.0, 33.4, 43.9, 89.0 };
        var rng = new Pcg32(0x60A1AC01UL, 13UL);

        foreach (double lst in lsts)
        foreach (double lat in latitudes)
        {
            var rotation = HorizontalToGalactic.Build(lst, lat);
            for (int i = 0; i < 200; i++)
            {
                double alt = Math.Asin(2.0 * rng.NextDouble() - 1.0) * 180.0 / Math.PI;
                double az = 360.0 * rng.NextDouble();
                SkyVector v = SkyVector.FromHorizontal(alt, az);

                rotation.ToGalactic(v, out double lm, out double bm);

                // The literal chain, written out here so the comparison is against a different
                // route rather than against a refactor of the same one.
                SkyCoordinates.HorizontalToEquatorial(alt, az, lst, lat,
                                                      out double ra, out double dec);
                GalacticCoordinates.EquatorialToGalactic(ra, dec, out double lc, out double bc);

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R}",
                    lst, lat, alt, az, lm, bm, lc, bc));
            }
        }
        File.WriteAllText("exo_rotation.csv", sb.ToString());
    }

    static void DumpLines()
    {
        var sb = new StringBuilder();
        sb.AppendLine("name,wavelength_angstrom,forbidden");
        foreach (var line in EmissionLines.All)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\"{0}\",{1:R},{2}",
                line.Name, line.WavelengthAngstrom, line.Forbidden ? 1 : 0));
        File.WriteAllText("exo_lines.csv", sb.ToString());
    }

    static void DumpRayleigh()
    {
        var sb = new StringBuilder();
        sb.AppendLine("surface_brightness_r,plate_scale_arcsec,aperture_cm2,throughput,electrons_per_px_per_s");
        double[] brightnesses = { 1.0, 10.0, 100.0, 1000.0 };
        double[] scales = { 0.1, 0.2754, 1.1015, 3.82 };
        double[] areas = { 20.0, 1732.0, 518320.0 };
        foreach (double r in brightnesses)
        foreach (double scale in scales)
        foreach (double area in areas)
        {
            const double throughput = 0.5;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4:R}",
                r, scale, area, throughput,
                EmissionLines.ElectronsPerPixelPerSecond(r, scale, area, throughput)));
        }
        File.WriteAllText("exo_rayleigh.csv", sb.ToString());

        // The photon-energy conversion, on its own, for the same reason.
        var flux = new StringBuilder();
        flux.AppendLine("line,wavelength_m,flux_erg_cm2_s,electrons_per_s");
        foreach (var line in EmissionLines.All)
            flux.AppendLine(string.Format(CultureInfo.InvariantCulture, "\"{0}\",{1:R},{2:R},{3:R}",
                line.Name, line.WavelengthMeters, 1.0e-13,
                EmissionLines.ElectronsPerSecondFromLineFlux(1.0e-13, line.WavelengthMeters, 1732.0, 0.5)));
        File.WriteAllText("exo_lineflux.csv", flux.ToString());
    }

    /// <summary>
    /// What narrowband actually buys, computed with the real response rather than asserted: the
    /// line signal is set by the throughput AT the line, the sky by the throughput INTEGRATED over
    /// the filter, so narrowing the filter holds one and shrinks the other.
    /// </summary>
    static void DumpNarrowband()
    {
        VisualTelescopeSpec spec = VisualTelescopeCatalog.Rc20;
        double areaCm2 = Math.PI * (spec.ApertureMeters / 2.0) * (spec.ApertureMeters / 2.0)
                       * (1.0 - spec.SecondaryObstructionFraction * spec.SecondaryObstructionFraction) * 1e4;
        double plateScale = spec.NativePixelSizeMeters / spec.FocalLengthMeters * (180.0 * 3600.0 / Math.PI);

        // Filters centred on H-alpha, from a broadband Luminance down to a 1 nm narrowband that
        // separates H-alpha from [N II] 6584. Peak transmission held fixed so the comparison is
        // about bandwidth alone.
        double lineM = EmissionLines.HAlpha.WavelengthMeters;
        double[] widthsNm = { 260.0, 30.0, 12.0, 7.0, 5.0, 3.0, 1.5, 1.0 };

        var sb = new StringBuilder();
        sb.AppendLine("width_nm,throughput_at_line,effective_width_a,line_e_per_px_s,"
                    + "sky_e_per_px_s,contrast,nii6584_admitted");
        foreach (double widthNm in widthsNm)
        {
            var response = new SystemResponse(
                lineM, widthNm * 10.0, 0.95 * spec.OpticsTransmission, null,
                spec.QuantumEfficiencyCurve, spec.QuantumEfficiency, 1.0, spec.SiteAltitudeMeters);

            double throughput = response.ThroughputAt(lineM);

            // 100 R of H-alpha, which is the order a bright Galactic H II region reaches in the
            // WHAM survey, against the model's own dark-sky continuum.
            double lineElectrons = EmissionLines.ElectronsPerPixelPerSecond(100.0, plateScale, areaCm2, throughput);
            double skyElectrons = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                SkyBrightnessModel.DarkSkyZenithVMagPerArcsec2, plateScale, response, areaCm2,
                1.0, 0.0);

            // Whether the filter still lets [N II] 6584 through, which is the question that decides
            // if an "H-alpha" frame is really H-alpha.
            double niiThroughput = response.ThroughputAt(EmissionLines.NII6584.WavelengthMeters);

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R}",
                widthNm, throughput, response.EffectiveWidthAngstromFlat,
                lineElectrons, skyElectrons,
                skyElectrons > 0.0 ? lineElectrons / skyElectrons : double.NaN,
                niiThroughput > 0.0 ? 1.0 : 0.0));
        }
        File.WriteAllText("exo_narrowband.csv", sb.ToString());
    }
}
