using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// What each instrument in the roster actually collects from a bright resolved solar-system target,
/// and therefore whether the neutral-density roster is the right one.
///
/// THE QUESTION THIS ANSWERS. A player photographing Jupiter or Saturn reports needing the ND1000
/// stop routinely and still over-exposing, while the next stop up, the solar filter, leaves
/// nothing visible. That is a two-hundred-fold gap between the two, and the question is whether the
/// gap is real (in which case an intermediate stop is missing) or whether the exposure control is
/// doing something else.
///
/// WHAT IT COMPUTES, AND WHY THAT IS RIGOROUS. A resolved planet is an extended source, so the
/// quantity that fills a pixel is its SURFACE BRIGHTNESS, not its integrated magnitude. That makes
/// SkyBrightnessModel.ElectronsPerPixelPerSecond, the shipped function the pipeline already uses
/// for the night sky, which takes exactly a V surface brightness, the correct and unmodified tool
/// for it. Nothing is reimplemented here: the bandpass, the throughput, the QE curve, the aperture
/// area and the ND transmissions all come from the shipped Core and VisualTelescopeCatalog.
///
/// SOURCES FOR THE TARGETS. Apparent magnitudes at mean opposition are from Mallama and Hilton
/// (2018, Astronomy and Computing 25, 10, "Computing apparent planetary magnitudes for The
/// Astronomical Almanac", arXiv:1808.01973), which is the model The Astronomical Almanac itself
/// uses. Semi-diameters are the IAU/IAG working-group radii over the mean opposition distance.
/// Surface brightness is then the definition, mu = V + 2.5 log10(area in arcsec^2), with the area
/// taken as the real oblate ellipse pi*a*b rather than a circle. Every number is derived in
/// Target.cs below with its own arithmetic shown, so none of it has to be taken on trust.
///
/// Pure C# against the shipped Core. No Unity, no game.
/// </summary>
static class NdFilterAudit
{
    const double ArcsecPerRad = 180.0 * 3600.0 / Math.PI;

    /// <summary>
    /// Restated from SolarSystemCameraTexture, which is Unity-dependent and cannot be compiled
    /// here. Each is a one-line arithmetic relation rather than a modelling choice, and each is
    /// re-derived in the report so a drift would be visible rather than silent.
    /// </summary>
    static double PlateScaleArcsecPerPixel(VisualTelescopeSpec s, int binning)
        => s.NativePixelSizeMeters * binning / s.FocalLengthMeters * ArcsecPerRad;

    static double ElectronsPerAdu(VisualTelescopeSpec s, double gain)
        => s.ElectronsPerAduAtUnityGain / Math.Max(1e-6, gain);

    static int AdcMaxCount(VisualTelescopeSpec s) => (1 << Math.Max(1, s.AdcBits)) - 1;

    /// <summary>
    /// The charge a pixel stops responding at: the smaller of the physical well (which grows with
    /// binning, because a binned pixel merges that many wells) and the converter's top code (which
    /// does NOT, because on-chip binning sums charge ahead of one amplifier and one ADC).
    ///
    /// That asymmetry is the whole answer to this audit's question, so it is spelled out rather
    /// than folded into one number.
    /// </summary>
    static double SaturationElectrons(VisualTelescopeSpec s, double gain, int binning)
    {
        double k = ElectronsPerAdu(s, gain);
        double digital = k * Math.Max(0.0, AdcMaxCount(s) - s.EffectiveBiasLevelAdu(k));
        double well = s.FullWellElectrons * binning * binning;
        return Math.Min(well, digital);
    }

    /// <summary>Real optical-density transmissions, restated from SolarSystemCameraTexture.NdFilterTransmission.</summary>
    static readonly (string name, double od)[] NdLadder =
    {
        ("none",     0.0),
        ("ND8",      0.9),
        ("ND64",     1.8),
        ("ND1000",   3.0),
        ("OD3.8",    3.8),   // Baader AstroSolar PHOTO Film, added because this audit found the gap
        ("solar",    5.0),
    };

    /// <summary>
    /// A resolved target, reduced to the one number that decides whether it saturates a pixel.
    ///
    /// mu = V + 2.5 log10(pi * a * b), with a and b the semi-diameters in arcsec. Both inputs are
    /// carried so the derivation is auditable rather than a bare result.
    /// </summary>
    sealed class Target
    {
        public string Name;
        public double V;                 // apparent V at the stated geometry
        public double SemiMajorArcsec;   // equatorial semi-diameter
        public double SemiMinorArcsec;   // polar semi-diameter
        public string Source;

        public double AreaArcsec2 => Math.PI * SemiMajorArcsec * SemiMinorArcsec;
        public double SurfaceBrightness => V + 2.5 * Math.Log10(AreaArcsec2);
    }

    static readonly Target[] Targets =
    {
        new Target
        {
            Name = "Sun",
            V = -26.74, SemiMajorArcsec = 959.63, SemiMinorArcsec = 959.63,
            Source = "V from Willmer (2018, ApJS 236, 47); semi-diameter at 1 au, Astronomical Almanac",
        },
        new Target
        {
            Name = "Moon (full)",
            V = -12.74, SemiMajorArcsec = 932.58, SemiMinorArcsec = 932.58,
            Source = "V at full phase, Astronomical Almanac; semi-diameter at mean distance",
        },
        new Target
        {
            // Mallama & Hilton give the mean opposition magnitude as -2.70 from both the
            // semi-major-axis estimate and the analysis of daily values, sigma 0.17.
            Name = "Jupiter (opp.)",
            V = -2.70, SemiMajorArcsec = 23.45, SemiMinorArcsec = 21.93,
            Source = "Mallama & Hilton (2018) mean opposition V; radii 71492/66854 km at 4.2029 au",
        },
        new Target
        {
            // The globe alone, using the V1(0) = -8.95 that Mallama (2012) derived from the
            // 1995 ring-plane crossing. The catalogue's +0.05 mean opposition magnitude includes
            // the rings, whose area is four times the globe's, so it is the wrong number for a
            // per-pixel surface brightness of the disk.
            Name = "Saturn globe (opp.)",
            V = 0.60, SemiMajorArcsec = 9.73, SemiMinorArcsec = 8.72,
            Source = "Mallama & Hilton (2018) Eq.10 with globe-only V1(0) = -8.95 at 8.5367 au",
        },
        new Target
        {
            Name = "Mars (opp.)",
            V = -1.98, SemiMajorArcsec = 8.94, SemiMinorArcsec = 8.89,
            Source = "Mallama & Hilton (2018) mean opposition V; radii 3396.2/3376.2 km at 0.5236 au",
        },
        new Target
        {
            Name = "Uranus (opp.)",
            V = 5.57, SemiMajorArcsec = 1.83, SemiMinorArcsec = 1.79,
            Source = "Mallama & Hilton (2018) mean opposition V; radii 25559/24973 km at 18.3286 au",
        },
    };

    /// <summary>
    /// The instrument's system response in the Luminance position at zenith, built the way
    /// SolarSystemCameraTexture.BuildSystemResponse builds it. Luminance because it is the
    /// widest position and therefore the worst case for saturation, which is the regime under test.
    /// </summary>
    static SystemResponse LuminanceResponse(VisualTelescopeSpec s)
    {
        double peak = s.LuminanceFilterPeakTransmission > 0.0 ? s.LuminanceFilterPeakTransmission : 1.0;
        return new SystemResponse(
            s.LuminanceCentralWavelengthNm * 1e-9,
            s.LuminanceBandwidthAngstrom,
            peak * s.OpticsTransmission,
            null,                       // no measured curve for the unfiltered Luminance position
            s.QuantumEfficiencyCurve,
            s.QuantumEfficiency,
            1.0,                        // airmass at zenith: the brightest case
            s.SiteAltitudeMeters);
    }

    static double ApertureAreaCm2(VisualTelescopeSpec s)
    {
        double r = s.ApertureMeters / 2.0;
        return Math.PI * r * r * (1.0 - s.SecondaryObstructionFraction * s.SecondaryObstructionFraction) * 1.0e4;
    }

    static void Main()
    {
        var report = new StringBuilder();
        var csv = new StringBuilder();
        csv.AppendLine("instrument,binning,gain,target,surface_brightness_vmag_arcsec2," +
                       "plate_scale_arcsec_px,electrons_per_px_per_s,saturation_electrons," +
                       "t_saturate_s,nd_needed_for_default_exposure,nd_needed_for_10ms");

        void W(string line) { report.AppendLine(line); Console.WriteLine(line); }

        W("ND FILTER AUDIT: what a bright resolved target really puts in a pixel");
        W("=====================================================================");
        W("");
        W("Every figure below comes from the shipped Core: SkyBrightnessModel.ElectronsPerPixelPerSecond");
        W("against a SystemResponse built from VisualTelescopeCatalog's own throughput, QE and");
        W("bandpass, at zenith, in the Luminance position (the widest, hence the worst case).");
        W("");
        W("Target surface brightnesses, derived rather than quoted:");
        W("");
        W($"  {"target",-22} {"V",7} {"semi-diam (arcsec)",20} {"area (arcsec2)",15} {"mu (V/arcsec2)",15}");
        foreach (Target t in Targets)
        {
            W($"  {t.Name,-22} {t.V,7:F2} {t.SemiMajorArcsec + " x " + t.SemiMinorArcsec,20} " +
              $"{t.AreaArcsec2,15:F1} {t.SurfaceBrightness,15:F2}");
        }
        W("");
        foreach (Target t in Targets) W($"    {t.Name}: {t.Source}");
        W("");

        // The exposure the camera opens with, and the one a real planetary imager uses.
        const double DefaultExposureSeconds = 0.5;   // SolarSystemCameraTexture.ExposureSeconds
        const double LuckyExposureSeconds = 0.010;   // typical planetary lucky-imaging sub

        foreach (VisualTelescopeSpec spec in VisualTelescopeCatalog.All)
        {
            if (spec.AdaptiveOpticsFwhmArcsec > 0.0) continue;   // SPHERE is a coronagraph, not a planetary camera

            SystemResponse response = LuminanceResponse(spec);
            double areaCm2 = ApertureAreaCm2(spec);

            W("");
            W($"{spec.Name} + {spec.CameraName}   ({spec.SiteName})");
            W(new string('-', 78));
            W($"  aperture {spec.ApertureMeters:F3} m, obstruction {spec.SecondaryObstructionFraction:F3}, " +
              $"collecting area {areaCm2:F0} cm2");
            W($"  focal length {spec.FocalLengthMeters:F3} m, pixel {spec.NativePixelSizeMeters * 1e6:F2} um, " +
              $"gain range {spec.MinGain:F1}-{spec.MaxGain:F1}");
            W($"  effective photometric width (flat SED) {response.EffectiveWidthAngstromFlat:F0} A");

            int[] binnings = { 1, 4 };
            double[] gains = { Math.Max(1.0, spec.MinGain), spec.MaxGain };

            foreach (int binning in binnings)
            {
                double plateScale = PlateScaleArcsecPerPixel(spec, binning);
                W("");
                W($"  binning {binning}x{binning}, plate scale {plateScale:F4} arcsec/px");

                foreach (double gain in gains)
                {
                    double sat = SaturationElectrons(spec, gain, binning);
                    double well = spec.FullWellElectrons * binning * binning;
                    double k = ElectronsPerAdu(spec, gain);
                    double digital = k * Math.Max(0.0, AdcMaxCount(spec) - spec.EffectiveBiasLevelAdu(k));
                    string limiter = digital < well ? "ADC-limited" : "well-limited";

                    W($"    gain {gain:F1}: K = {k:F3} e-/ADU, well {well:F0} e-, converter ceiling " +
                      $"{digital:F0} e-  ->  saturates at {sat:F0} e- ({limiter})");
                    W($"      {"target",-22} {"e-/px/s",13} {"t_sat",12} {"ND @0.5s",10} {"ND @10ms",10}");

                    foreach (Target t in Targets)
                    {
                        double rate = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                            t.SurfaceBrightness, plateScale, response, areaCm2,
                            1.0, SourceSpectra.SolarPhotosphereTemperatureK);

                        double tSat = rate > 0.0 ? sat / rate : double.PositiveInfinity;
                        string ndDefault = RequiredNd(rate, sat, DefaultExposureSeconds);
                        string ndLucky = RequiredNd(rate, sat, LuckyExposureSeconds);

                        W($"      {t.Name,-22} {rate,13:E3} {FormatTime(tSat),12} {ndDefault,10} {ndLucky,10}");

                        csv.AppendLine(string.Join(",", new[]
                        {
                            Q(spec.Name), binning.ToString(CultureInfo.InvariantCulture),
                            gain.ToString("R", CultureInfo.InvariantCulture), Q(t.Name),
                            t.SurfaceBrightness.ToString("R", CultureInfo.InvariantCulture),
                            plateScale.ToString("R", CultureInfo.InvariantCulture),
                            rate.ToString("R", CultureInfo.InvariantCulture),
                            sat.ToString("R", CultureInfo.InvariantCulture),
                            tSat.ToString("R", CultureInfo.InvariantCulture),
                            ndDefault, ndLucky,
                        }));
                    }
                }
            }
        }

        W("");
        W(new string('=', 78));
        W("Exposure range each instrument offers, against what these targets need:");
        W("");
        foreach (VisualTelescopeSpec spec in VisualTelescopeCatalog.All)
        {
            if (spec.AdaptiveOpticsFwhmArcsec > 0.0) continue;
            W($"  {spec.Name,-20} {FormatTime(spec.MinExposureSeconds)} to {FormatTime(spec.MaxExposureSeconds)}" +
              $"   (opens at {FormatTime(DefaultExposureSeconds)})");
        }

        File.WriteAllText("nd_filter_audit.txt", report.ToString());
        File.WriteAllText("nd_filter_audit.csv", csv.ToString());
        Console.WriteLine();
        Console.WriteLine("written nd_filter_audit.txt and nd_filter_audit.csv");
    }

    /// <summary>The weakest stop on the ladder that keeps the given exposure below saturation, or "over" if none does.</summary>
    static string RequiredNd(double electronsPerSecond, double saturationElectrons, double exposureSeconds)
    {
        foreach ((string name, double od) in NdLadder)
        {
            double charge = electronsPerSecond * Math.Pow(10.0, -od) * exposureSeconds;
            if (charge <= saturationElectrons) return name;
        }
        return "over";
    }

    static string FormatTime(double seconds)
    {
        if (double.IsInfinity(seconds) || double.IsNaN(seconds)) return "-";
        if (seconds >= 1.0) return $"{seconds:F1} s";
        if (seconds >= 1e-3) return $"{seconds * 1e3:F2} ms";
        return $"{seconds * 1e6:F1} us";
    }

    static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
