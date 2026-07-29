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
        DumpRealTargets();
        DumpLineRatios();
        DumpPatchSet();
        DumpRotation();
        Console.WriteLine("written exo_lines.csv, exo_rayleigh.csv, exo_narrowband.csv, exo_rotation.csv");
    }

    /// <summary>
    /// What a real H II region actually puts on each instrument, read from a real packed map if one
    /// has been installed.
    ///
    /// This exists because "I pointed at M42 in H-alpha and saw nothing" has two very different
    /// causes -- a signal that is not being computed, and a signal that is being computed and is a
    /// tenth of a percent of full well. Only the numbers separate them, and the second one is not a
    /// bug: it is why nebula photography is a stacking-and-stretching discipline.
    /// </summary>
    static void DumpRealTargets()
    {
        string path = Environment.GetEnvironmentVariable("EXO_HALPHA_MAP")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
                "GameData/ExoInstruments/PluginData/HalphaMap.emission");
        if (!File.Exists(path)) { Console.WriteLine("  (no installed H-alpha map; skipped)"); return; }

        var map = new EmissionMap();
        map.Load(path);

        CheckCatalogPositions(map);
        TimeFrameFill(map);

        (string name, double ra, double dec)[] targets =
        {
            ("M42 Orion",     83.8221,  -5.3911),
            ("Rosette",       98.0000,   5.0333),
            ("M8 Lagoon",    270.9042, -24.3833),
            ("North America", 315.4875, 44.2067),
        };

        double lineM = EmissionLines.HAlpha.WavelengthMeters;
        Console.WriteLine("\nWhat the installed map puts on each instrument, H-alpha filter, 30 s:");

        foreach (var spec in VisualTelescopeCatalog.All)
        {
            bool hasHa = Array.IndexOf(spec.AvailableFilters, CameraFilter.HAlpha) >= 0;
            if (!hasHa || !(spec.HAlphaBandwidthAngstrom > 0.0)) continue;

            int binning = spec.CameraName.Contains("ZIMPOL") ? 2 : 1;
            double plateScale = spec.NativePixelSizeMeters * binning
                              / (spec.FocalLengthMeters * spec.BarlowFactor) * (180.0 * 3600.0 / Math.PI);
            double areaCm2 = Math.PI * (spec.ApertureMeters / 2.0) * (spec.ApertureMeters / 2.0)
                           * (1.0 - spec.SecondaryObstructionFraction * spec.SecondaryObstructionFraction) * 1e4;

            var response = new SystemResponse(
                spec.HAlphaCentralWavelengthNm * 1e-9, spec.HAlphaBandwidthAngstrom,
                spec.HAlphaFilterPeakTransmission * spec.OpticsTransmission, null,
                spec.QuantumEfficiencyCurve, spec.QuantumEfficiency, 1.0, spec.SiteAltitudeMeters);
            double throughput = response.ThroughputAt(lineM);

            Console.WriteLine($"\n  {spec.Name} / {spec.CameraName}: {plateScale:F4}\"/px, "
                            + $"H-alpha filter {spec.HAlphaBandwidthAngstrom / 10.0:F1} nm, "
                            + $"throughput at the line {throughput:F4}, full well {spec.FullWellElectrons:N0} e-");
            foreach (var t in targets)
            {
                double r = map.RayleighsAt(t.ra, t.dec);
                if (double.IsNaN(r)) { Console.WriteLine($"    {t.name,-14} no value"); continue; }
                double perSecond = EmissionLines.ElectronsPerPixelPerSecond(r, plateScale, areaCm2, throughput);
                double e30 = perSecond * 30.0;
                Console.WriteLine($"    {t.name,-14} {r,8:F0} R -> {perSecond,10:F3} e-/px/s"
                                + $" -> {e30,10:F1} e- in 30 s = {100.0 * e30 / spec.FullWellElectrons,7:F3}% of full well"
                                + $" = {e30 / spec.ElectronsPerAduAtUnityGain,8:F1} ADU of {(1 << spec.AdcBits) - 1}");
            }
        }
        Console.WriteLine();
    }

    /// <summary>
    /// The forbidden-line ratios against the values they are measured at, which is the only check
    /// that matters here: the emissivity expressions carry coefficients and abundances, and a wrong
    /// one still produces a smooth, plausible ratio map.
    ///
    /// The anchors are published. Classical H II regions: [N II] 6584/H-alpha near 0.25 at the
    /// 6000-7000 K they are measured at. The warm ionised medium near the midplane: 0.3-0.6 at
    /// about 8000 K, rising toward unity at high |z| where it approaches 10^4 K (Haffner, Reynolds
    /// &amp; Tufte 1999; Madsen, Reynolds &amp; Haffner 2006). [S II]/[N II] near 0.3-0.5 and far
    /// flatter than either ratio to H-alpha, which is the observational statement that what varies
    /// is temperature and not abundance.
    /// </summary>
    static void DumpLineRatios()
    {
        var rb = new StringBuilder();
        rb.AppendLine("temperature_k,nii6584_over_ha,sii6716_over_ha");
        for (int i = 0; i <= 80; i++)
        {
            double tk = 6000.0 + i * 50.0;
            rb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}",
                tk, NebularLineRatios.Nii6584OverHalpha(tk), NebularLineRatios.Sii6716OverHalpha(tk)));
        }
        File.WriteAllText("exo_lineratios.csv", rb.ToString());

        Console.WriteLine("\nForbidden-line ratios against the temperatures they are measured at:");
        Console.WriteLine("     T_e      I_Ha      [NII]6584/Ha   [SII]6716/Ha   [SII]/[NII]");
        double[] temps = { 6000, 6500, 7000, 8000, 9000, 10000 };
        foreach (double t in temps)
        {
            double nii = NebularLineRatios.Nii6584OverHalpha(t);
            double sii = NebularLineRatios.Sii6716OverHalpha(t);
            Console.WriteLine($"   {t,6:F0} K              {nii,10:F3}     {sii,10:F3}     {sii / nii,10:F3}");
        }

        Console.WriteLine("\n   and as a function of the H-alpha brightness the map supplies:");
        double[] intensities = { 5000, 1000, 300, 100, 30, 10, 3, 1, 0.3 };
        foreach (double i in intensities)
        {
            double t = NebularLineRatios.ElectronTemperatureK(i);
            double nii = NebularLineRatios.Nii6584OverHalpha(t);
            double sii = NebularLineRatios.Sii6716OverHalpha(t);
            Console.WriteLine($"   {t,6:F0} K  {i,8:F1} R  {nii,10:F3}     {sii,10:F3}     {sii / nii,10:F3}");
        }
        Console.WriteLine();
    }

    /// <summary>How long reading the map costs across a full sensor, which is what the deposit does once per capture.</summary>
    static void TimeFrameFill(EmissionMap map)
    {
        EmissionMap.AllocateScratch(out long[] px, out double[] wt);
        int n = 4144 * 2822;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double acc = 0.0;
        for (int i = 0; i < n; i++)
        {
            double l = 80.0 + (i % 1000) * 1e-4;
            double b = -20.0 + (i / 1000 % 1000) * 1e-4;
            double v = map.RayleighsAtGalactic(l, b, px, wt);
            if (!double.IsNaN(v)) acc += v;
        }
        sw.Stop();
        Console.WriteLine($"\n  reading the map over a 4144x2822 frame: {sw.ElapsedMilliseconds} ms "
                        + $"({sw.ElapsedMilliseconds * 1e6 / n:F0} ns/px, sum {acc:E3})");
    }

    /// <summary>
    /// Every line-emitting entry in DeepSkyCatalog, against the sky the H-alpha map measures.
    ///
    /// A catalogue of hand-entered coordinates fails silently: a transposed digit still names a
    /// real-looking direction, and the chart draws a cross on empty sky. The map is an independent
    /// witness. A real H II region sits on a local maximum of it, so the test is the offset from
    /// the catalogued position to the brightest point within half a degree, and the ratio of the
    /// two. An arcminute or two is the beam and the object's own asymmetry; a tenth of a degree
    /// with the value climbing all the way is a wrong position.
    ///
    /// Planetary nebulae are excluded: every one of them is smaller than the map's 6 arcmin beam,
    /// so the composite has nothing to say about where they are.
    /// </summary>
    static void CheckCatalogPositions(EmissionMap map)
    {
        Console.WriteLine("\nDeepSkyCatalog positions against the H-alpha map "
                        + "(offset to the brightest point within 30'):");
        double worst = 0.0;
        string worstName = null;

        foreach (var obj in DeepSkyCatalog.All)
        {
            if (obj.Kind != DeepSkyKind.HiiRegion && obj.Kind != DeepSkyKind.SupernovaRemnant) continue;

            double atPosition = map.RayleighsAt(obj.RaDeg, obj.DecDeg);
            double best = atPosition, bestOffset = 0.0;
            double cosDec = Math.Cos(obj.DecDeg * Math.PI / 180.0);

            for (int i = -20; i <= 20; i++)
            for (int j = -20; j <= 20; j++)
            {
                double dDec = j * 0.025;
                double dRa = cosDec > 1e-6 ? i * 0.025 / cosDec : 0.0;
                double offset = Math.Sqrt((i * 0.025) * (i * 0.025) + dDec * dDec);
                if (offset > 0.5) continue;
                double v = map.RayleighsAt(obj.RaDeg + dRa, obj.DecDeg + dDec);
                if (!double.IsNaN(v) && v > best) { best = v; bestOffset = offset; }
            }

            double ratio = atPosition > 0.0 ? best / atPosition : double.NaN;
            if (bestOffset > worst) { worst = bestOffset; worstName = obj.DisplayName; }
            Console.WriteLine($"  {obj.DisplayName,-28} {atPosition,8:F0} R at the catalogue position,"
                            + $" peak {best,8:F0} R  {bestOffset * 60.0,5:F1}' away  (x{ratio:F2})");
        }
        Console.WriteLine($"  worst offset to a local maximum: {worst * 60.0:F1}' ({worstName})");
    }

    /// <summary>
    /// Reads a packed patch set through the real Core reader and dumps what it returns, so the
    /// Python side can compare against the same file read independently.
    ///
    /// The format is new and every part of it fails silently: a run-length table off by one returns
    /// a neighbouring cell's value, a frame mix-up returns a disc of sky from somewhere else, and a
    /// half-float read at the wrong offset returns numbers that are still plausible surface
    /// brightnesses. Only a value-by-value comparison catches those.
    /// </summary>
    static void DumpPatchSet()
    {
        string path = Environment.GetEnvironmentVariable("EXO_PATCHSET") ?? "/tmp/test.patchset";
        if (!File.Exists(path)) { Console.WriteLine($"  (no patch set at {path}; skipped)"); return; }

        var set = new EmissionPatchSet();
        set.Load(path);
        Console.WriteLine($"\n  patch set: {set.PatchCount} patches at nside {set.Nside} "
                        + $"({set.ResolutionArcmin:F3} arcmin), {set.LineName}");
        foreach (string n in set.PatchNames) Console.WriteLine($"    {n}");

        // A grid over the first patch, sampled through the real interpolation, plus a ring of points
        // outside it which must come back uncovered rather than wrong.
        var sb = new StringBuilder();
        sb.AppendLine("ra_deg,dec_deg,covered,rayleighs");
        EmissionMap.AllocateScratch(out long[] px, out double[] wt);
        var cursor = EmissionPatchSet.Cursor.New();

        var rng = new Pcg32(0x9A7C4EUL, 3UL);
        for (int i = 0; i < 4000; i++)
        {
            // Deliberately spread out to 1.4x the patch radius, so both sides of the edge are tested.
            double t = 2.0 * Math.PI * rng.NextDouble();
            double rad = 1.4 * Math.Sqrt(rng.NextDouble());
            double dec = -2.45 + rad * Math.Sin(t);
            double ra = 85.25 + rad * Math.Cos(t) / Math.Cos(dec * Math.PI / 180.0);

            var patch = set.FindCoveringPatch(ra, dec, 0.0);
            GalacticCoordinates.EquatorialToGalactic(ra, dec, out double l, out double b);
            bool covered = patch != null
                && set.TryRayleighsAtGalactic(patch, l, b, px, wt, ref cursor, out double v);
            double value = 0.0;
            if (covered) set.TryRayleighsAtGalactic(patch, l, b, px, wt, ref cursor, out value);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2},{3:R}",
                ra, dec, covered ? 1 : 0, value));
        }
        File.WriteAllText("exo_patchset.csv", sb.ToString());
        Console.WriteLine("  written exo_patchset.csv");
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
