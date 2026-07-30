using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Dumps the galaxy profile arithmetic for the Python side to check against SciPy and astropy.
///
/// Four things can go wrong here and none of them throws: b_n slightly off, which shifts every
/// galaxy's total flux through the e^(b_n) factor; the total-flux factor wrong, which makes every
/// galaxy uniformly too bright or too faint; the R_e solve landing on the wrong branch, which makes
/// them the wrong SIZE while keeping the right flux; and the pixel integration losing light at the
/// nucleus, where the profile's slope is infinite.
/// </summary>
static class DumpGalaxy
{
    static void Main()
    {
        DumpSpecialFunctions();
        DumpSersic();
        DumpEffectiveRadius();
        DumpDeposit();
        Console.WriteLine("written exo_gammafn.csv, exo_sersic.csv, exo_re.csv, exo_deposit.csv, exo_profile.csv");
    }

    /// <summary>The incomplete gamma and log gamma the Sersic maths is built on, over the range it uses them in.</summary>
    static void DumpSpecialFunctions()
    {
        var sb = new StringBuilder();
        sb.AppendLine("a,x,gammap,loggamma_a");
        double[] avals = { 0.2, 0.5, 1.0, 1.6, 2.0, 3.0, 5.0, 8.0, 12.0, 20.0, 30.0 };
        foreach (double a in avals)
        {
            for (int i = 0; i <= 60; i++)
            {
                double x = Math.Pow(10.0, -3.0 + 5.0 * i / 60.0);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R}",
                    a, x, SersicProfile.RegularisedGammaP(a, x), SersicProfile.LogGamma(a)));
            }
        }
        File.WriteAllText("exo_gammafn.csv", sb.ToString());
    }

    /// <summary>b_n, the total-flux factor and the enclosed-flux curve, across every index a galaxy takes.</summary>
    static void DumpSersic()
    {
        var sb = new StringBuilder();
        sb.AppendLine("n,bn,total_flux_factor,r_half,r90,r99");
        for (int i = 0; i <= 80; i++)
        {
            double n = 0.3 + i * (8.0 - 0.3) / 80.0;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R}",
                n, SersicProfile.Bn(n), SersicProfile.TotalFluxFactor(n),
                SersicProfile.RadiusForEnclosedFraction(0.5, n),
                SersicProfile.RadiusForEnclosedFraction(0.9, n),
                SersicProfile.RadiusForEnclosedFraction(0.99, n)));
        }
        File.WriteAllText("exo_sersic.csv", sb.ToString());

        var pb = new StringBuilder();
        pb.AppendLine("n,r_over_re,enclosed,mu_minus_mu_e");
        foreach (double n in new[] { 0.5, 1.0, 1.5, 2.0, 4.0, 6.0 })
        {
            for (int i = 1; i <= 200; i++)
            {
                double r = i * 0.05;
                double mu = SersicProfile.SurfaceBrightnessMagPerArcsec2(r, 0.0, 1.0, n)
                          - SersicProfile.SurfaceBrightnessMagPerArcsec2(1.0, 0.0, 1.0, n);
                pb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R}",
                    n, r, SersicProfile.EnclosedFraction(r, n), mu));
            }
        }
        File.WriteAllText("exo_profile.csv", pb.ToString());
    }

    /// <summary>
    /// The R_e that reconciles a catalogued total magnitude with a catalogued isophotal diameter,
    /// over a grid spanning what HyperLEDA actually contains, and the residual, which must be
    /// zero by construction: feeding R_e back through the profile has to put mu = 25 exactly at
    /// D25/2, or the solve found the wrong root.
    /// </summary>
    static void DumpEffectiveRadius()
    {
        var sb = new StringBuilder();
        sb.AppendLine("total_mag,r25_arcsec,n,re_arcsec,mu_at_r25,enclosed_at_r25");
        double[] mags = { 4.0, 6.0, 8.0, 10.0, 12.0, 14.0 };
        double[] radii = { 6.0, 30.0, 120.0, 600.0, 3000.0 };
        double[] indices = { 1.0, 1.5, 2.0, 3.0, 4.0 };

        foreach (double m in mags)
        foreach (double r25 in radii)
        foreach (double n in indices)
        {
            double re = SersicProfile.EffectiveRadiusFromIsophote(m, r25, 25.0, n);
            double mu = double.IsNaN(re) ? double.NaN
                : SersicProfile.SurfaceBrightnessMagPerArcsec2(r25, m, re, n);
            double enc = double.IsNaN(re) ? double.NaN : SersicProfile.EnclosedFraction(r25 / re, n);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R}",
                m, r25, n, re, mu, enc));
        }
        File.WriteAllText("exo_re.csv", sb.ToString());
    }

    /// <summary>
    /// The renderer itself: a galaxy dropped on an empty plane, with the deposited total against
    /// the analytic enclosed flux. This is what catches a pixel integration that loses the nucleus,
    /// an ellipse normalised without its axis ratio, or a truncation that quietly drops light.
    /// </summary>
    static void DumpDeposit()
    {
        var sb = new StringBuilder();
        sb.AppendLine("n,re_px,axis_ratio,pa_deg,radii,total_e,deposited_e,analytic_enclosed,"
                    + "centroid_x,centroid_y,second_moment_major,second_moment_minor");

        // The plane has to hold the WHOLE truncation box, or the comparison measures the frame
        // edge rather than the renderer: at 8 effective radii a 40 px profile reaches 320 px, and
        // clipping that against a 401 px plane loses flux, shifts the centroid and inflates the
        // measured axis ratio all at once. 601 px holds the largest case here with margin.
        const int w = 601, h = 601;
        double[] indices = { 1.0, 2.0, 4.0 };
        double[] radiiPx = { 3.0, 12.0, 30.0 };
        double[] ratios = { 1.0, 0.5, 0.25 };
        double[] angles = { 0.0, 30.0, 90.0 };

        foreach (double n in indices)
        foreach (double re in radiiPx)
        foreach (double q in ratios)
        foreach (double paDeg in angles)
        {
            var plane = new float[w * h];
            // Deliberately not on a pixel centre: a nucleus that lands exactly on one is the easy
            // case, and the pixel integration has to hold at every sub-pixel offset.
            double cx = w / 2.0 + 0.31, cy = h / 2.0 - 0.17;
            double pa = paDeg * Math.PI / 180.0;
            double total = 1.0e6;
            double truncation = 8.0;

            double deposited = GalaxyRenderer.Deposit(
                plane, w, h, cx, cy, Math.Cos(pa), Math.Sin(pa), re, q, n, total, truncation);

            double analytic = total * SersicProfile.EnclosedFraction(truncation, n);

            // The centroid is measured over the INNER profile only. Out at the truncation edge the
            // cut is a hard ellipse and the centre sits at a sub-pixel offset, so which boundary
            // pixels fall inside is slightly asymmetric, a real property of cutting a continuous
            // profile on a pixel grid, and not what "did it land where it was asked to" is asking.
            double sum = 0.0, mx = 0.0, my = 0.0;
            double inner = 3.0 * re;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double v = plane[y * w + x];
                    if (v <= 0.0) continue;
                    double ddx = x + 0.5 - cx, ddy = y + 0.5 - cy;
                    if (ddx * ddx + ddy * ddy > inner * inner) continue;
                    sum += v; mx += v * (x + 0.5); my += v * (y + 0.5);
                }
            mx = sum > 0 ? mx / sum : 0.0;
            my = sum > 0 ? my / sum : 0.0;

            double mMajor = 0.0, mMinor = 0.0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double v = plane[y * w + x];
                    if (v <= 0.0) continue;
                    double dx = x + 0.5 - mx, dy = y + 0.5 - my;
                    double along = dx * Math.Cos(pa) + dy * Math.Sin(pa);
                    double across = -dx * Math.Sin(pa) + dy * Math.Cos(pa);
                    mMajor += v * along * along;
                    mMinor += v * across * across;
                }
            mMajor = sum > 0 ? Math.Sqrt(mMajor / sum) : 0.0;
            mMinor = sum > 0 ? Math.Sqrt(mMinor / sum) : 0.0;

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R},{10:R},{11:R}",
                n, re, q, paDeg, truncation, total, deposited, analytic,
                mx - cx, my - cy, mMajor, mMinor));
        }
        File.WriteAllText("exo_deposit.csv", sb.ToString());
    }
}
