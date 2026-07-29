using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

/// <summary>
/// Dumps what reddening does to a star's photometry, three ways:
///
///   error.csv       how wrong the old model is -- a reddened star integrated as an intrinsically
///                   cool blackbody, against the same star integrated as what it really is.
///   anchor.csv      that the V normalisation is untouched, which is the statement that nothing is
///                   double counted.
///   integrand.csv   the integrand itself, sampled, for an independent recomputation in Python.
/// </summary>
static class DumpReddening
{
    static void Main()
    {
        // The roster's two extremes of bandwidth: the RC20's ~2600 Angstrom Luminance and FORS2's
        // 7700 Angstrom unfiltered position, where a shape error has the most room to matter.
        Dump("rc20", VisualTelescopeCatalog.Rc20);
        Dump("fors2", VisualTelescopeCatalog.Fors2Vlt);
        DumpCacheError();
        Console.WriteLine("written exo_reddening_*.csv");
    }

    static SystemResponse LuminanceResponse(VisualTelescopeSpec s)
    {
        double peak = s.LuminanceFilterPeakTransmission > 0.0 ? s.LuminanceFilterPeakTransmission : 1.0;
        return new SystemResponse(
            s.LuminanceCentralWavelengthNm * 1e-9, s.LuminanceBandwidthAngstrom,
            peak * s.OpticsTransmission, null,
            s.QuantumEfficiencyCurve, s.QuantumEfficiency, 1.0, s.SiteAltitudeMeters);
    }

    static void Dump(string tag, VisualTelescopeSpec spec)
    {
        SystemResponse response = LuminanceResponse(spec);

        // Intrinsic temperatures spanning the main sequence, and reddenings spanning a
        // high-latitude sight line to a heavily obscured bulge one.
        double[] teffs = { 3000, 4000, 5772, 8000, 12000, 20000, 30000 };
        double[] ebvs = { 0.0, 0.05, 0.1, 0.25, 0.5, 1.0, 2.0, 3.0 };

        var sb = new StringBuilder();
        sb.AppendLine("instrument,intrinsic_teff_k,ebv,intrinsic_bv,observed_bv,"
                    + "width_true_a,width_naive_a,ratio,mag_error");
        foreach (double teff in teffs)
        foreach (double ebv in ebvs)
        {
            // The intrinsic colour this temperature implies, inverted from the same relation the
            // pipeline uses forward, so the two are consistent by construction.
            double bv0 = ColorIndexForTeff(teff);
            if (double.IsNaN(bv0)) continue;
            double observedBv = bv0 + ebv;

            // What the pipeline does today: observed colour straight to a temperature.
            double? naiveTeff = StellarColor.TeffFromColorIndexBV(observedBv);
            double widthNaive = naiveTeff.HasValue
                ? response.EffectiveWidthAngstromForTemperature(naiveTeff.Value)
                : response.EffectiveWidthAngstromFlat;

            // What it is: the real photosphere, seen through the real dust.
            double widthTrue = response.EffectiveWidthAngstromForReddenedStar(teff, ebv);

            double ratio = widthTrue > 0.0 ? widthNaive / widthTrue : double.NaN;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R}",
                tag, teff, ebv, bv0, observedBv, widthTrue, widthNaive, ratio,
                ratio > 0.0 ? 2.5 * Math.Log10(ratio) : double.NaN));
        }
        File.WriteAllText($"exo_reddening_error_{tag}.csv", sb.ToString());

        // The anchor. The effective width DOES change for a flat source -- a reddened flat
        // spectrum is not flat, and the band extends redward of V where the extinction is lower.
        // What must not change is the factor AT V, which is exactly 1 by construction and is the
        // no-double-counting proof: the observed magnitude still sets the flux.
        var anchor = new StringBuilder();
        anchor.AppendLine("instrument,ebv,width_flat_a,transmission_at_v");
        foreach (double ebv in ebvs)
        {
            anchor.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R}",
                tag, ebv,
                response.EffectiveWidthAngstromForReddenedStar(0.0, ebv),
                ReddenedStarSpectrum.NormalisedTransmission(
                    StellarPhotometry.JohnsonVWavelengthMeters, ebv, InterstellarExtinction.MilkyWayRv)));
        }
        File.WriteAllText($"exo_reddening_anchor_{tag}.csv", anchor.ToString());

        // The integrand, for Python to rebuild the same integral from published pieces.
        var integ = new StringBuilder();
        integ.AppendLine("instrument,wavelength_m,ebv,normalised_transmission");
        foreach (double ebv in new[] { 0.5, 2.0 })
        for (int i = 0; i <= 200; i++)
        {
            double lambda = (300.0 + i * 5.0) * 1e-9;
            integ.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R}",
                tag, lambda, ebv,
                ReddenedStarSpectrum.NormalisedTransmission(lambda, ebv, InterstellarExtinction.MilkyWayRv)));
        }
        File.WriteAllText($"exo_reddening_integrand_{tag}.csv", integ.ToString());
    }

    /// <summary>
    /// Inverts Ballesteros' relation by bisection. The pipeline only ever runs it forward, so this
    /// exists to build a self-consistent test case: a star of temperature T whose intrinsic colour
    /// maps back to T exactly.
    /// </summary>
    static double ColorIndexForTeff(double teffK)
    {
        double lo = -0.4, hi = 2.4;
        if (StellarColor.TeffFromColorIndexBV(lo) < teffK || StellarColor.TeffFromColorIndexBV(hi) > teffK)
            return double.NaN;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (StellarColor.TeffFromColorIndexBV(mid) > teffK) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>What the per-frame cache's quantisation costs, measured against unquantised calls.</summary>
    static void DumpCacheError()
    {
        SystemResponse response = LuminanceResponse(VisualTelescopeCatalog.Fors2Vlt);
        var cache = new ReddenedResponseCache(response);
        var rng = new Pcg32(0xCAC5E01UL, 11UL);

        var sb = new StringBuilder();
        sb.AppendLine("teff_k,ebv,width_cached_a,width_exact_a,evaluations");
        // A realistic field: one sight line, so its stars share a reddening to within the scatter
        // of the estimate, and span the whole main sequence in temperature.
        const double FieldEbv = 0.5, FieldSpread = 0.05;
        for (int i = 0; i < 400; i++)
        {
            double teff = 3000.0 + rng.NextDouble() * 27000.0;
            double ebv = FieldEbv + (2.0 * rng.NextDouble() - 1.0) * FieldSpread;
            double cached = cache.EffectiveWidthAngstrom(teff, ebv);
            double exact = response.EffectiveWidthAngstromForReddenedStar(teff, ebv);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4}",
                teff, ebv, cached, exact, cache.Evaluations));
        }
        File.WriteAllText("exo_reddening_cache.csv", sb.ToString());
    }
}
