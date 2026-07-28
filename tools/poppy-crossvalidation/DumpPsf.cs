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
        Console.WriteLine("written");
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
