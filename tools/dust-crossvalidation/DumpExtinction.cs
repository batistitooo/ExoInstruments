using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Dumps the interstellar extinction curve the shipped Core computes, for comparison against the
/// dust_extinction package's reference implementations of the same two published laws.
///
/// Nothing here reimplements anything: every value comes from InterstellarExtinction called exactly
/// as the pipeline calls it.
/// </summary>
static class DumpExtinction
{
    static void Main()
    {
        double[] rvs = { 2.0, 2.6, 3.1, 3.85, 4.4, 5.5 };  // includes values BETWEEN table rows, on purpose
        double xMin = 0.30, xMax = 3.40;
        int steps = 1240;                                   // 0.0025 inverse microns, finer than the table's 0.01

        var sb = new StringBuilder();
        sb.AppendLine("law,rv,x_inv_micron,wavelength_m,k_alambda_over_av");
        foreach (InterstellarExtinction.Law law in new[]
                 { InterstellarExtinction.Law.Ccm89, InterstellarExtinction.Law.Fitzpatrick99 })
        {
            foreach (double rv in rvs)
            {
                for (int i = 0; i <= steps; i++)
                {
                    double x = xMin + (xMax - xMin) * i / steps;
                    double lambda = 1.0e-6 / x;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R},{4:R}",
                        law, rv, x, lambda,
                        InterstellarExtinction.RelativeExtinction(lambda, rv, law)));
                }
            }
        }
        File.WriteAllText("exo_extinction.csv", sb.ToString());

        // Transmission, which is what the bandpass integrand actually multiplies by, at reddenings
        // spanning what the Galaxy offers: a high-latitude sight line, the local disc, and a
        // heavily obscured bulge line where E(B-V) reaches several magnitudes.
        var t = new StringBuilder();
        t.AppendLine("law,rv,ebv,wavelength_m,transmission,a_lambda_mag");
        double[] ebvs = { 0.0, 0.02, 0.1, 0.3, 1.0, 3.0 };
        foreach (InterstellarExtinction.Law law in new[]
                 { InterstellarExtinction.Law.Ccm89, InterstellarExtinction.Law.Fitzpatrick99 })
        {
            InterstellarExtinction.ActiveLaw = law;
            foreach (double ebv in ebvs)
            {
                for (int i = 0; i <= 200; i++)
                {
                    double lambda = (300.0 + i * 5.0) * 1e-9;   // 300 to 1300 nm, the roster's range
                    double tr = InterstellarExtinction.Transmission(lambda, ebv);
                    t.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R},{4:R},{5:R}",
                        law, InterstellarExtinction.MilkyWayRv, ebv, lambda, tr,
                        tr > 0.0 ? -2.5 * Math.Log10(tr) : double.NaN));
                }
            }
        }
        InterstellarExtinction.ActiveLaw = InterstellarExtinction.Law.Fitzpatrick99;
        File.WriteAllText("exo_transmission.csv", t.ToString());

        // The law's own normalisation, which is a closure condition rather than a comparison:
        // k(V) must be exactly 1, and k(B) - k(V) must be exactly 1/R_V, because that is what
        // R_V = A(V)/E(B-V) means. A law that fails these is not an extinction law.
        var n = new StringBuilder();
        n.AppendLine("law,rv,k_v,k_b,k_b_minus_k_v,one_over_rv");
        const double JohnsonV = 1.0e-6 / 1.82;   // CCM89's own V wavenumber, x = 1.82 inverse microns
        const double JohnsonB = 1.0e-6 / 2.27;   // and their B, x = 2.27
        foreach (InterstellarExtinction.Law law in new[]
                 { InterstellarExtinction.Law.Ccm89, InterstellarExtinction.Law.Fitzpatrick99 })
        {
            foreach (double rv in rvs)
            {
                double kv = InterstellarExtinction.RelativeExtinction(JohnsonV, rv, law);
                double kb = InterstellarExtinction.RelativeExtinction(JohnsonB, rv, law);
                n.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R},{4:R},{5:R}",
                    law, rv, kv, kb, kb - kv, 1.0 / rv));
            }
        }
        File.WriteAllText("exo_normalisation.csv", n.ToString());

        Console.WriteLine("written exo_extinction.csv, exo_transmission.csv, exo_normalisation.csv");
    }
}
