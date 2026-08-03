using System;
using System.Threading.Tasks;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Convolution of a full sensor frame with an arbitrary 2D kernel, via the overlap-add
    /// method over FFT tiles.
    ///
    /// Why this exists rather than a direct convolution loop: a real point-spread function
    /// (see OpticalPsf) is radially symmetric but NOT separable, so it cannot be applied as a
    /// horizontal pass followed by a vertical one the way a box or Gaussian kernel can. Applied
    /// directly, an instrument PSF a few tens of pixels across costs O(W*H*K^2), of order
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
        /// The kernel is used exactly as supplied; normalise it beforehand if flux is to be
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
            var kernelRe = new double[n * n];
            var kernelIm = new double[n * n];
            for (int y = 0; y < k; y++)
                for (int x = 0; x < k; x++)
                    kernelRe[y * n + x] = kernel[y * k + x];
            Transform2D(kernelRe, kernelIm, n, false);

            // The ACCUMULATOR stays double as well: a frame's tiles are summed into it, and the
            // whole point of the change is that a faint background must survive being added to
            // beside a bright star.
            var accum = new double[width * height];
            var re = new double[n * n];
            var im = new double[n * n];

            for (int tileY = 0; tileY < height; tileY += tile)
            {
                for (int tileX = 0; tileX < width; tileX += tile)
                {
                    Array.Clear(re, 0, re.Length);
                    Array.Clear(im, 0, im.Length);

                    int spanY = Math.Min(tile, height - tileY);
                    int spanX = Math.Min(tile, width - tileX);
                    bool anySignal = false;
                    for (int y = 0; y < spanY; y++)
                    {
                        int src = (tileY + y) * width + tileX;
                        int dst = y * n;
                        for (int x = 0; x < spanX; x++)
                        {
                            float v = image[src + x];
                            re[dst + x] = v;
                            if (v != 0f) anySignal = true;
                        }
                    }

                    // A TILE WITH NOTHING IN IT CONTRIBUTES NOTHING, exactly. Convolution is
                    // linear, so an all-zero input transforms to zero, multiplies to zero and
                    // comes back zero, and adding that to the accumulator leaves every bit of it
                    // as it was. This is a skipped no-op, not a dropped term.
                    //
                    // It is worth testing for because the signal plane at this point is SPARSE by
                    // construction: the sky has not been added yet (it is uniform, and a uniform
                    // field through a unit-sum kernel is unchanged, so it goes in afterwards), the
                    // stars are points, and a deep-sky target occupies the part of the frame it
                    // subtends. A galaxy portrait on a wide field, or any frame where the rendered
                    // scene came back empty, leaves whole tiles at zero.
                    if (!anySignal) continue;

                    Transform2D(re, im, n, false);

                    // Pointwise complex product with the kernel spectrum.
                    for (int i = 0; i < re.Length; i++)
                    {
                        double ar = re[i], ai = im[i], br = kernelRe[i], bi = kernelIm[i];
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

            for (int i = 0; i < image.Length; i++) image[i] = (float)accum[i];
        }

        /// <summary>
        /// A wide radially symmetric PSF, prepared once as the spectrum of a kernel that spans the
        /// WHOLE frame, so a convolution with it costs one transform pair and truncates nothing.
        ///
        /// WHY THIS EXISTS ALONGSIDE Convolve. A tiled kernel has to stop somewhere, and where it
        /// stops the profile drops to zero in one pixel. Renormalising afterwards conserves the
        /// flux but not the surface brightness, so around a bright source the boundary is a visible
        /// edge in the shape of the kernel's support. For a compact PSF that edge sits where the
        /// profile is already 1e-6 of its peak and nothing shows. Fried's long-exposure atmospheric
        /// PSF is the opposite case: its wings fall only as theta^(-11/3), so at any radius a tile
        /// can afford the profile is still percent-level, and the edge is plainly visible.
        ///
        /// WHY IT IS EXACT AND NOT MERELY BIGGER. The grid is padded to at least 2N-1 on each axis
        /// and the kernel is laid out over lags -N/2 to N/2-1 with negative lags wrapped to the top
        /// of the array, which is the standard ordering that makes a circular convolution agree
        /// with a linear one. Two pixels of an N-wide frame are never more than N-1 apart, so every
        /// lag that can occur inside the frame is present in the kernel at its true value and no
        /// wrap-around can reach the frame. The profile IS truncated, at a lag no pair of sensor
        /// pixels can realise. Light at larger offsets left the sensor, which is the same thing the
        /// zero-padding in Convolve above already assumes.
        ///
        /// Sampling the transfer function straight onto the grid instead would be cheaper by one
        /// transform, and is wrong for the same reason in reverse: it yields the ALIASED kernel,
        /// sum over m of PSF(lag + mN), which re-injects wing flux that should have left the sensor
        /// on the far side. Measured on ZIMPOL's halo that reaches 3.5e-2 of the profile at 500 px.
        ///
        /// The kernel is used exactly as supplied and never renormalised, so the caller's profile
        /// must already be in fractions of the source's total flux (see
        /// OpticalPsf.AtmosphericPerPixelScale). Flux beyond the frame is then genuinely lost,
        /// which is what a finite sensor does.
        /// </summary>
        public sealed class RadialKernelSpectrum
        {
            private readonly float[] _re;
            private readonly float[] _im;

            /// <summary>Padded grid dimensions.</summary>
            public int Nx { get; }
            public int Ny { get; }

            /// <summary>Fraction of the source's flux the grid holds; the remainder falls at lags no two sensor pixels can span.</summary>
            public double EnclosedFraction { get; }

            private RadialKernelSpectrum(float[] re, float[] im, int nx, int ny, double enclosed)
            {
                _re = re; _im = im; Nx = nx; Ny = ny; EnclosedFraction = enclosed;
            }

            /// <summary>Prepares the spectrum, or returns null when the padded grid would exceed maxTransformCells so a caller can fall back and say so.</summary>
            public static RadialKernelSpectrum Build(int width, int height,
                                                     Func<double, double> profileAtPixelRadius,
                                                     long maxTransformCells)
            {
                if (profileAtPixelRadius == null || width <= 0 || height <= 0) return null;
                int nx = NextPowerOfTwoAtLeast(2 * width - 1);
                int ny = NextPowerOfTwoAtLeast(2 * height - 1);
                if (nx <= 0 || ny <= 0 || (long)nx * ny > maxTransformCells) return null;

                var re = new double[nx * ny];
                var im = new double[nx * ny];

                double sum = 0.0;
                for (int dy = -ny / 2; dy < ny / 2; dy++)
                {
                    int row = ((dy + ny) % ny) * nx;
                    double dy2 = (double)dy * dy;
                    for (int dx = -nx / 2; dx < nx / 2; dx++)
                    {
                        double v = profileAtPixelRadius(Math.Sqrt(dx * (double)dx + dy2));
                        re[row + (dx + nx) % nx] = v;
                        sum += v;
                    }
                }

                Transform2D(re, im, nx, ny, false);

                // Kept single: the kernel's own spectrum spans no great range, and at 2048x2048 a
                // double copy would be 67 MB held for the life of the cache rather than 33.
                var reF = new float[re.Length];
                var imF = new float[im.Length];
                for (int i = 0; i < re.Length; i++) { reF[i] = (float)re[i]; imF[i] = (float)im[i]; }
                return new RadialKernelSpectrum(reF, imF, nx, ny, sum);
            }

            /// <summary>Convolves the frame in place. width and height must match what Build was given.</summary>
            public void Apply(float[] image, int width, int height)
            {
                if (image == null || image.Length != width * height) return;
                if (NextPowerOfTwoAtLeast(2 * width - 1) != Nx || NextPowerOfTwoAtLeast(2 * height - 1) != Ny) return;

                var re = new double[Nx * Ny];
                var im = new double[Nx * Ny];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++) re[y * Nx + x] = image[y * width + x];

                Transform2D(re, im, Nx, Ny, false);
                for (int i = 0; i < re.Length; i++)
                {
                    double ar = re[i], ai = im[i], br = _re[i], bi = _im[i];
                    re[i] = ar * br - ai * bi;
                    im[i] = ar * bi + ai * br;
                }
                Transform2D(re, im, Nx, Ny, true);

                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++) image[y * width + x] = (float)re[y * Nx + x];
            }
        }

        private static int NextPowerOfTwoAtLeast(int value)
        {
            int n = 1;
            while (n < value)
            {
                n <<= 1;
                if (n <= 0) return 0;   // overflowed
            }
            return n;
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
        private static void Transform2D(double[] re, double[] im, int n, bool inverse)
            => Transform2D(re, im, n, n, inverse);

        /// <summary>
        /// The rectangular form. nx and ny must each be a power of two.
        ///
        /// SPREAD ACROSS CORES, AND EXACTLY. A separable transform is a set of INDEPENDENT
        /// one-dimensional transforms: no row's result depends on another row's, and once the row
        /// pass is finished, no column's depends on another column's. Nothing is accumulated
        /// across workers, so each output cell is produced by the same sequence of operations on
        /// the same inputs whichever worker ran it, and the result does not depend on the thread
        /// count. That is the condition ParallelWork sets out, and this is the easiest place in
        /// the pipeline to meet it.
        /// </summary>
        private static void Transform2D(double[] re, double[] im, int nx, int ny, bool inverse)
        {
            Twiddles rowTwiddles = TwiddlesFor(nx);
            Twiddles columnTwiddles = nx == ny ? rowTwiddles : TwiddlesFor(ny);
            int scratch = Math.Max(nx, ny * ColumnBlock);
            int blocks = (nx + ColumnBlock - 1) / ColumnBlock;

            // Cells transformed, as a stand-in for the work: each pass is one transform per line
            // and each transform is O(length log length).
            bool parallel = ParallelWork.Worthwhile((long)nx * ny);

            if (parallel)
            {
                Parallel.For(0, ny, ParallelWork.Options,
                    () => new LineBuffers(scratch),
                    (y, state, buffers) => { TransformRow(re, im, nx, y, inverse, rowTwiddles, buffers); return buffers; },
                    buffers => { });
                Parallel.For(0, blocks, ParallelWork.Options,
                    () => new LineBuffers(scratch),
                    (b, state, buffers) =>
                    {
                        int x0 = b * ColumnBlock;
                        TransformColumns(re, im, nx, ny, x0, Math.Min(ColumnBlock, nx - x0),
                                         inverse, columnTwiddles, buffers);
                        return buffers;
                    },
                    buffers => { });
                return;
            }

            var single = new LineBuffers(scratch);
            for (int y = 0; y < ny; y++) TransformRow(re, im, nx, y, inverse, rowTwiddles, single);
            for (int x0 = 0; x0 < nx; x0 += ColumnBlock)
                TransformColumns(re, im, nx, ny, x0, Math.Min(ColumnBlock, nx - x0),
                                 inverse, columnTwiddles, single);
        }

        /// <summary>
        /// Columns taken at a time in the column pass: eight doubles, which is one 64-byte cache
        /// line.
        ///
        /// WHY IT MATTERS AND WHAT IT DOES NOT CHANGE. Each column is still its own independent
        /// transform over the same values, so every output is bit for bit what it was; only the
        /// order the cells are FETCHED in changes. A column of a row-major grid is strided, so
        /// reading one column pulls a whole cache line per element and uses one of its eight
        /// doubles; the other seven belong to the seven neighbouring columns, which were then read
        /// again, one line each, when their turn came. Gathering the eight together spends one
        /// fetch where the plain loop spent eight. On a 1024x1024 tile the grid is 8 MB per plane
        /// and nothing of it stays in cache between columns, which is why the column pass, doing
        /// exactly the same arithmetic as the row pass, cost several times as much.
        /// </summary>
        private const int ColumnBlock = 8;

        /// <summary>One worker's line buffers, so the row and column passes allocate once per worker rather than once per line.</summary>
        private sealed class LineBuffers
        {
            internal readonly double[] Re;
            internal readonly double[] Im;
            internal LineBuffers(int length) { Re = new double[length]; Im = new double[length]; }
        }

        private static void TransformRow(double[] re, double[] im, int nx, int y,
                                         bool inverse, Twiddles twiddles, LineBuffers buffers)
        {
            int row = y * nx;
            Array.Copy(re, row, buffers.Re, 0, nx);
            Array.Copy(im, row, buffers.Im, 0, nx);
            Transform1D(buffers.Re, buffers.Im, nx, inverse, twiddles, 0);
            Array.Copy(buffers.Re, 0, re, row, nx);
            Array.Copy(buffers.Im, 0, im, row, nx);
        }

        private static void TransformColumns(double[] re, double[] im, int nx, int ny,
                                             int x0, int count,
                                             bool inverse, Twiddles twiddles, LineBuffers buffers)
        {
            if (count <= 0) return;
            double[] lineRe = buffers.Re, lineIm = buffers.Im;

            for (int y = 0; y < ny; y++)
            {
                int row = y * nx + x0;
                for (int c = 0; c < count; c++)
                {
                    lineRe[c * ny + y] = re[row + c];
                    lineIm[c * ny + y] = im[row + c];
                }
            }

            for (int c = 0; c < count; c++)
                Transform1D(lineRe, lineIm, ny, inverse, twiddles, c * ny);

            for (int y = 0; y < ny; y++)
            {
                int row = y * nx + x0;
                for (int c = 0; c < count; c++)
                {
                    re[row + c] = lineRe[c * ny + y];
                    im[row + c] = lineIm[c * ny + y];
                }
            }
        }

        /// <summary>
        /// The roots of unity a transform of length N steps through, and its bit-reversal
        /// permutation, computed once per length and shared by every transform of that length.
        ///
        /// A TABLE IS MORE ACCURATE THAN THE RECURRENCE IT REPLACES, not merely faster. The
        /// previous form advanced the twiddle factor by repeated complex multiplication,
        /// w_(j+1) = w_j * w, which is the textbook implementation and also the textbook example
        /// of error accumulation: the relative error grows as the square root of the number of
        /// steps, so the last butterflies of a 1024-point stage carry roughly thirty times the
        /// rounding error of the first. Each entry here is evaluated directly from its own angle,
        /// so every one is correct to a rounding, and the transform's error stops depending on
        /// how far into the stage a butterfly sits. It is also faster, because the recurrence
        /// serialises the inner loop behind a chain of dependent multiplications.
        ///
        /// One table serves both directions: the inverse transform's twiddle is the forward
        /// one's conjugate, so only the sign of the imaginary part changes.
        /// </summary>
        private sealed class Twiddles
        {
            internal readonly double[] Cos;      // cos(2 pi m / N), m in [0, N/2)
            internal readonly double[] Sin;      // sin(2 pi m / N)
            internal readonly int[] Reversed;    // bit-reversal permutation of [0, N)

            internal Twiddles(int n)
            {
                int half = Math.Max(1, n / 2);
                Cos = new double[half];
                Sin = new double[half];
                for (int m = 0; m < half; m++)
                {
                    double angle = 2.0 * Math.PI * m / n;
                    Cos[m] = Math.Cos(angle);
                    Sin[m] = Math.Sin(angle);
                }

                Reversed = new int[n];
                int bits = 0;
                while ((1 << bits) < n) bits++;
                for (int i = 0; i < n; i++)
                {
                    int r = 0;
                    for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                    Reversed[i] = r;
                }
            }
        }

        /// <summary>
        /// Tables by length. A capture uses at most a handful of lengths and reuses each of them
        /// for every line of every tile, so they are built once and held; the largest this file
        /// can ask for is 2048 entries, a few tens of kilobytes.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<int, Twiddles> twiddleTables
            = new System.Collections.Generic.Dictionary<int, Twiddles>();

        private static Twiddles TwiddlesFor(int n)
        {
            lock (twiddleTables)
            {
                if (!twiddleTables.TryGetValue(n, out Twiddles table))
                {
                    table = new Twiddles(n);
                    twiddleTables[n] = table;
                }
                return table;
            }
        }

        /// <summary>In-place iterative radix-2 Cooley-Tukey FFT over n entries starting at offset. n must be a power of two. The inverse pass carries the 1/n normalisation.</summary>
        private static void Transform1D(double[] re, double[] im, int n, bool inverse, Twiddles twiddles, int offset)
        {
            int[] reversed = twiddles.Reversed;
            for (int i = 0; i < n; i++)
            {
                int j = reversed[i];
                if (i < j)
                {
                    int a = offset + i, b = offset + j;
                    double tr = re[a]; re[a] = re[b]; re[b] = tr;
                    double ti = im[a]; im[a] = im[b]; im[b] = ti;
                }
            }

            double[] cos = twiddles.Cos, sin = twiddles.Sin;
            double sign = inverse ? 1.0 : -1.0;

            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                int stride = n / len;
                for (int i = offset; i < offset + n; i += len)
                {
                    for (int j = 0, m = 0; j < half; j++, m += stride)
                    {
                        double wRe = cos[m], wIm = sign * sin[m];
                        int a = i + j, b = a + half;
                        double ur = re[a], ui = im[a];
                        double br = re[b], bi = im[b];
                        double vr = br * wRe - bi * wIm;
                        double vi = br * wIm + bi * wRe;
                        re[a] = ur + vr; im[a] = ui + vi;
                        re[b] = ur - vr; im[b] = ui - vi;
                    }
                }
            }

            if (inverse)
            {
                double inv = 1.0 / n;
                for (int i = offset; i < offset + n; i++) { re[i] *= inv; im[i] *= inv; }
            }
        }
    }
}
