using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Dumps the annular-pupil diffraction pattern as ExoInstruments' shipped Core computes it, for
/// comparison against POPPY. Nothing here reimplements the physics: it calls OpticalPsf and
/// RadialPsfProfile exactly as the mod does.
/// </summary>
class Diag
{
    const double ArcsecPerRad = 180.0 * 3600.0 / Math.PI;

    static void Main()
    {
        Dump("elt", 39.3, 11.1 / 39.3, 1.6e-6);
        Dump("rc20", 0.51, 0.39, 552.5e-9);
        Dump("clear", 39.3, 0.0, 1.6e-6);
        DumpVaned("eltvanes", 39.3, 11.1 / 39.3, 1.6e-6, 6, 0.50);
        Console.WriteLine("written");
    }

    /// <summary>
    /// The vaned pupil, dumped as a two-dimensional cut rather than a radial profile: with a
    /// spider the pattern is no longer radially symmetric, and the whole point of the comparison is
    /// the azimuthal structure. Two cuts, one along a spike and one between spikes.
    /// </summary>
    static void DumpVaned(string tag, double D, double eps, double lambda, int vanes, double vaneW)
    {
        double lod = lambda / D;
        var pupil = new PupilDiffraction(D, eps, lambda, vanes, vaneW, 0.0);

        var sb = new StringBuilder();
        sb.AppendLine("r_over_lod,along_spike,between_spikes");
        // Vanes at 0/60/120 deg, so spikes fall at 90/150/30. Sample along 30 deg (a spike) and
        // along 0 deg (a vane axis, which is the darkest azimuth).
        double sa = 30.0 * Math.PI / 180.0, sb2 = 0.0;
        for (int i = 0; i <= 8000; i++)
        {
            double rLod = i * 40.0 / 8000.0;
            double t = rLod * lod;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}",
                rLod,
                pupil.Intensity(t * Math.Cos(sa), t * Math.Sin(sa)),
                pupil.Intensity(t * Math.Cos(sb2), t * Math.Sin(sb2))));
        }
        File.WriteAllText($"exo_{tag}.csv", sb.ToString());

        var meta = new StringBuilder();
        meta.AppendLine("key,value");
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "aperture_m,{0:R}", D));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "obstruction,{0:R}", eps));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "wavelength_m,{0:R}", lambda));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "vane_count,{0}", vanes));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "vane_width_m,{0:R}", vaneW));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "vane_obscuration_fraction,{0:R}", pupil.VaneObscurationFraction));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "lambda_over_d_arcsec,{0:R}", lod * ArcsecPerRad));
        File.WriteAllText($"exo_{tag}_meta.csv", meta.ToString());
    }

    static void Dump(string tag, double D, double eps, double lambda)
    {
        double lod = lambda / D; // rad

        var sb = new StringBuilder();
        sb.AppendLine("r_over_lod,intensity_norm,encircled_energy");
        // Out to 40 lambda/D, finely enough to resolve every ring maximum and null.
        for (int i = 0; i <= 8000; i++)
        {
            double rLod = i * 40.0 / 8000.0;
            double theta = rLod * lod;
            double I = OpticalPsf.AiryIntensity(theta, D, eps, lambda);
            double ee = i == 0 ? 0.0 : RadialPsfProfile.EncircledEnergy(theta, D, eps, lambda, 8192);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:R},{1:R},{2:R}", rLod, I, ee));
        }
        File.WriteAllText($"exo_{tag}.csv", sb.ToString());

        double fwhmArcsec = OpticalPsf.AiryFwhmArcsec(D, eps, lambda);
        double firstNull = RadialPsfProfile.FirstNullRad(D, eps, lambda) / lod;
        var meta = new StringBuilder();
        meta.AppendLine("key,value");
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "aperture_m,{0:R}", D));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "obstruction,{0:R}", eps));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "wavelength_m,{0:R}", lambda));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "lambda_over_d_arcsec,{0:R}", lod * ArcsecPerRad));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "fwhm_arcsec,{0:R}", fwhmArcsec));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "fwhm_over_lod,{0:R}", fwhmArcsec / (lod * ArcsecPerRad)));
        meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "first_null_over_lod,{0:R}", firstNull));
        File.WriteAllText($"exo_{tag}_meta.csv", meta.ToString());
    }
}
