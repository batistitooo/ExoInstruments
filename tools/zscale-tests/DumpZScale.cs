using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Dumps synthetic frames and the black/white points ZScale picks for them, for astropy to check.
///
/// The frames span what this pipeline actually produces: a faint nebula on a sky pedestal, which is
/// the case that made the algorithm necessary; a star field, where a few saturated pixels must NOT
/// be allowed to set the white point; a flat field, where the fit has nothing to grip; and a bright
/// planet, where the subject really does fill the converter and the limits should stay wide.
/// </summary>
static class DumpZScale
{
    static void Main()
    {
        var meta = new StringBuilder();
        meta.AppendLine("name,black,white");

        foreach (var (name, frame) in Frames())
        {
            bool ok = ZScale.TryLimits(frame, out double black, out double white);
            meta.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R}", name, black, white));

            using (var w = new BinaryWriter(File.Create("frame_" + name + ".bin")))
            {
                w.Write(frame.Length);
                foreach (float v in frame) w.Write(v);
            }
            Console.WriteLine($"  {name,-18} black {black:E4}  white {white:E4}  ok={ok}");
        }
        File.WriteAllText("exo_zscale.csv", meta.ToString());
        Console.WriteLine("written exo_zscale.csv and the frames beside it");
    }

    static System.Collections.Generic.IEnumerable<(string, float[])> Frames()
    {
        const int w = 400, h = 300;
        yield return ("faint_nebula", Build(w, h, (x, y, rng) =>
        {
            // A nebula spanning a tenth of a percent of full scale on a sky pedestal, which is what
            // 40 s on the Elephant's Trunk actually produces.
            double sky = 0.0012, amp = 0.0011;
            double dx = (x - w * 0.5) / (w * 0.35), dy = (y - h * 0.5) / (h * 0.35);
            double neb = amp * Math.Exp(-(dx * dx + dy * dy));
            return sky + neb + 6e-5 * rng.NextGaussian();
        }));

        yield return ("star_field", Build(w, h, (x, y, rng) =>
        {
            double v = 0.002 + 1e-4 * rng.NextGaussian();
            // A handful of saturated stars, the thing a max-based scaling would be destroyed by.
            if ((x * 7919 + y * 104729) % 4001 == 0) v = 1.0;
            return v;
        }));

        yield return ("flat", Build(w, h, (x, y, rng) => 0.4));

        yield return ("bright_planet", Build(w, h, (x, y, rng) =>
        {
            double dx = (x - w * 0.5) / 40.0, dy = (y - h * 0.5) / 40.0;
            double r2 = dx * dx + dy * dy;
            return (r2 < 1.0 ? 0.85 : 0.0008) + 3e-4 * rng.NextGaussian();
        }));

        yield return ("gradient", Build(w, h, (x, y, rng) => 0.1 + 0.6 * x / (double)w));
    }

    static float[] Build(int w, int h, Func<int, int, Gauss, double> f)
    {
        var rng = new Gauss(0x5CA1E5UL);
        var frame = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                frame[y * w + x] = (float)f(x, y, rng);
        return frame;
    }

    /// <summary>Box-Muller on the project's own PCG32, so the frames are reproducible on both sides.</summary>
    sealed class Gauss
    {
        private readonly Pcg32 _rng;
        public Gauss(ulong seed) { _rng = new Pcg32(seed, 1UL); }
        public double NextGaussian()
        {
            double u1 = Math.Max(1e-12, _rng.NextDouble()), u2 = _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
