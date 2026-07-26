using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Convolution of a full sensor frame with an arbitrary 2D kernel, via the overlap-add
    /// method over FFT tiles.
    ///
    /// Why this exists rather than a direct convolution loop: a real point-spread function
    /// (see OpticalPsf) is radially symmetric but NOT separable, so it cannot be applied as a
    /// horizontal pass followed by a vertical one the way a box or Gaussian kernel can. Applied
    /// directly, an instrument PSF a few tens of pixels across costs O(W*H*K^2) -- of order
    /// 10^10 operations on a multi-megapixel frame, i.e. minutes per exposure. Transforming
    /// tiles instead makes it O(W*H*log N), which keeps a capture in the second range while
    /// computing exactly the same result: overlap-add is an exact restructuring of linear
    /// convolution, not an approximation of it.
    ///
    /// Outside the frame the image is treated as zero rather than edge-clamped. That is the
    /// physically right choice here: beyond the sensor there is sky, and in these frames the sky
    /// is black. Edge-clamping would smear the border pixel outwards and invent flux that the
    /// detector never collected.
    ///
    /// Pure C# with no Unity dependency, like the rest of Core.
    /// </summary>
    public static class FourierConvolution
    {
        /// <summary>Smallest transform this will use; below it the per-tile overhead dominates.</summary>
        private const int MinTransformSize = 64;
        /// <summary>Largest transform this will use, bounding the per-tile working set.</summary>
        private const int MaxTransformSize = 1024;

        /// <summary>
        /// Convolves image (row-major, width*height) in place with a square kernel of
        /// half-width kernelRadius, i.e. (2*kernelRadius+1)^2 taps, centred on each pixel.
        /// The kernel is used exactly as supplied -- normalise it beforehand if flux is to be
        /// conserved (OpticalPsf.BuildKernel already does).
        /// </summary>
        public static void Convolve(float[] image, int width, int height, float[] kernel, int kernelRadius)
        {
            if (image == null || kernel == null || kernelRadius < 1) return;
            int k = 2 * kernelRadius + 1;
            if (kernel.Length != k * k) return;
            if (width <= 0 || height <= 0 || image.Length != width * height) return;

            int n = TransformSizeFor(k);
            int tile = n - k + 1;               // usable input span per tile
            if (tile < 1) return;

            // Kernel transform, computed once and reused by every tile.
            var kernelRe = new float[n * n];
            var kernelIm = new float[n * n];
            for (int y = 0; y < k; y++)
                for (int x = 0; x < k; x++)
                    kernelRe[y * n + x] = kernel[y * k + x];
            Transform2D(kernelRe, kernelIm, n, false);

            var accum = new float[width * height];
            var re = new float[n * n];
            var im = new float[n * n];

            for (int tileY = 0; tileY < height; tileY += tile)
            {
                for (int tileX = 0; tileX < width; tileX += tile)
                {
                    Array.Clear(re, 0, re.Length);
                    Array.Clear(im, 0, im.Length);

                    int spanY = Math.Min(tile, height - tileY);
                    int spanX = Math.Min(tile, width - tileX);
                    for (int y = 0; y < spanY; y++)
                    {
                        int src = (tileY + y) * width + tileX;
                        int dst = y * n;
                        for (int x = 0; x < spanX; x++) re[dst + x] = image[src + x];
                    }

                    Transform2D(re, im, n, false);

                    // Pointwise complex product with the kernel spectrum.
                    for (int i = 0; i < re.Length; i++)
                    {
                        float ar = re[i], ai = im[i], br = kernelRe[i], bi = kernelIm[i];
                        re[i] = ar * br - ai * bi;
                        im[i] = ar * bi + ai * br;
                    }

                    Transform2D(re, im, n, true);

                    // Overlap-add: this tile's full linear-convolution support is
                    // (span + k - 1) wide and sits shifted back by the kernel's half-width,
                    // so neighbouring tiles' tails land on the same output pixels and sum.
                    int outSpanY = Math.Min(spanY + k - 1, n);
                    int outSpanX = Math.Min(spanX + k - 1, n);
                    for (int y = 0; y < outSpanY; y++)
                    {
                        int oy = tileY + y - kernelRadius;
                        if (oy < 0 || oy >= height) continue;
                        int rowIn = y * n;
                        int rowOut = oy * width;
                        for (int x = 0; x < outSpanX; x++)
                        {
                            int ox = tileX + x - kernelRadius;
                            if (ox < 0 || ox >= width) continue;
                            accum[rowOut + ox] += re[rowIn + x];
                        }
                    }
                }
            }

            Array.Copy(accum, image, image.Length);
        }

        /// <summary>Transform size for a given kernel width: a power of two large enough that each tile carries a useful span of real pixels, within the bounds above.</summary>
        private static int TransformSizeFor(int kernelWidth)
        {
            int target = Math.Max(MinTransformSize, 4 * kernelWidth);
            int n = MinTransformSize;
            while (n < target && n < MaxTransformSize) n <<= 1;
            // A transform must still be wider than the kernel or no input span fits.
            while (n <= kernelWidth) n <<= 1;
            return n;
        }

        /// <summary>Separable 2D transform: every row, then every column. n must be a power of two.</summary>
        private static void Transform2D(float[] re, float[] im, int n, bool inverse)
        {
            var rowRe = new float[n];
            var rowIm = new float[n];

            for (int y = 0; y < n; y++)
            {
                int row = y * n;
                Array.Copy(re, row, rowRe, 0, n);
                Array.Copy(im, row, rowIm, 0, n);
                Transform1D(rowRe, rowIm, n, inverse);
                Array.Copy(rowRe, 0, re, row, n);
                Array.Copy(rowIm, 0, im, row, n);
            }

            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++) { rowRe[y] = re[y * n + x]; rowIm[y] = im[y * n + x]; }
                Transform1D(rowRe, rowIm, n, inverse);
                for (int y = 0; y < n; y++) { re[y * n + x] = rowRe[y]; im[y * n + x] = rowIm[y]; }
            }
        }

        /// <summary>In-place iterative radix-2 Cooley-Tukey FFT. n must be a power of two. The inverse pass carries the 1/n normalisation.</summary>
        private static void Transform1D(float[] re, float[] im, int n, bool inverse)
        {
            // Bit-reversal permutation.
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float tr = re[i]; re[i] = re[j]; re[j] = tr;
                    float ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = 2.0 * Math.PI / len * (inverse ? 1.0 : -1.0);
                float wRe = (float)Math.Cos(angle);
                float wIm = (float)Math.Sin(angle);
                for (int i = 0; i < n; i += len)
                {
                    float curRe = 1f, curIm = 0f;
                    int half = len >> 1;
                    for (int j = 0; j < half; j++)
                    {
                        int a = i + j, b = i + j + half;
                        float ur = re[a], ui = im[a];
                        float vr = re[b] * curRe - im[b] * curIm;
                        float vi = re[b] * curIm + im[b] * curRe;
                        re[a] = ur + vr; im[a] = ui + vi;
                        re[b] = ur - vr; im[b] = ui - vi;

                        float nextRe = curRe * wRe - curIm * wIm;
                        curIm = curRe * wIm + curIm * wRe;
                        curRe = nextRe;
                    }
                }
            }

            if (inverse)
            {
                float inv = 1f / n;
                for (int i = 0; i < n; i++) { re[i] *= inv; im[i] *= inv; }
            }
        }
    }
}
