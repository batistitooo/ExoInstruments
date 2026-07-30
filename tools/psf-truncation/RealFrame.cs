using System;
using ExoInstruments.Core;

/// <summary>
/// Runs FourierConvolution.Convolve over a frame the size and composition of a real capture, and
/// compares it against a literal convolution of the same data.
///
/// Everything smaller has passed. What has not been tested is a FULL frame carrying a real star
/// field: 11.7 megapixels, a background of a few tens of electrons, and thousands of sources
/// spanning five decades. A defect that depends on the tile grid interacting with the frame's own
/// dimensions cannot show up on a 400 x 260 test image, because 400 and 260 are not 4144 and 2822.
/// </summary>
static class RealFrame
{
    public static void Run()
    {
        const int w = 4144, h = 2822;
        const int kernelRadius = 2;
        int k = 2 * kernelRadius + 1;

        var kernel = new float[k * k];
        double sum = 0.0;
        for (int dy = -kernelRadius; dy <= kernelRadius; dy++)
            for (int dx = -kernelRadius; dx <= kernelRadius; dx++)
            {
                double v = Math.Exp(-(dx * dx + dy * dy) / 1.2);
                kernel[(dy + kernelRadius) * k + dx + kernelRadius] = (float)v;
                sum += v;
            }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= (float)sum;

        var image = new float[w * h];
        for (int i = 0; i < image.Length; i++) image[i] = 32f;
        var rng = new Pcg32(0x5747A1UL, 3UL);
        for (int s = 0; s < 5000; s++)
        {
            int x = (int)(rng.NextDouble() * w), y = (int)(rng.NextDouble() * h);
            double mag = 6.0 + 9.0 * rng.NextDouble();
            image[y * w + x] += (float)(3.0e7 * Math.Pow(10.0, -0.4 * (mag - 6.0)));
        }

        var viaFft = (float[])image.Clone();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        FourierConvolution.Convolve(viaFft, w, h, kernel, kernelRadius);
        sw.Stop();
        Console.WriteLine($"  {w}x{h}, 5000 stars, kernel radius {kernelRadius}: convolved in {sw.ElapsedMilliseconds} ms");

        // Direct convolution of the same thing.
        var direct = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double acc = 0.0;
                for (int dy = -kernelRadius; dy <= kernelRadius; dy++)
                {
                    int sy = y - dy;
                    if (sy < 0 || sy >= h) continue;
                    for (int dx = -kernelRadius; dx <= kernelRadius; dx++)
                    {
                        int sx = x - dx;
                        if (sx < 0 || sx >= w) continue;
                        acc += image[sy * w + sx] * kernel[(dy + kernelRadius) * k + dx + kernelRadius];
                    }
                }
                direct[y * w + x] = (float)acc;
            }

        // Background only: the question is what happened to the sky, not to the stars.
        double worst = 0.0; int wx = 0, wy = 0;
        double meanDev = 0.0; long n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (direct[y * w + x] > 60f) continue;   // skip stars and their immediate wings
                double e = viaFft[y * w + x] - direct[y * w + x];
                meanDev += e; n++;
                if (Math.Abs(e) > Math.Abs(worst)) { worst = e; wx = x; wy = y; }
            }
        Console.WriteLine($"  background pixels compared: {n}");
        Console.WriteLine($"  mean deviation {meanDev / n:E3} e-, worst {worst:+0.000;-0.000} e- at ({wx},{wy})");
        Console.WriteLine($"  as a fraction of a 32 e- sky: {100.0 * Math.Abs(worst) / 32.0:F3}%");

        // Per tile-row, so a tile-structured defect would be unmistakable.
        int tile = 60;
        Console.WriteLine("  worst deviation per tile row:");
        for (int ty = 0; ty < h; ty += tile * 8)
        {
            double rowWorst = 0.0;
            for (int y = ty; y < Math.Min(h, ty + tile * 8); y++)
                for (int x = 0; x < w; x++)
                {
                    if (direct[y * w + x] > 60f) continue;
                    double e = Math.Abs(viaFft[y * w + x] - direct[y * w + x]);
                    if (e > rowWorst) rowWorst = e;
                }
            Console.WriteLine($"    y {ty,5}-{Math.Min(h, ty + tile * 8),5}: {rowWorst:F4} e-");
        }
    }
}
