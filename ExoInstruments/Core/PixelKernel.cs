namespace ExoInstruments.Core
{
    /// <summary>
    /// 3x3 pixel-coupling kernels measured on native pixels, applied to a binned frame. Neutral
    /// about what does the coupling: interpixel capacitance and charge diffusion both use it.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class PixelKernel
    {
        /// <summary>
        /// A 3x3 native-pixel kernel as pixels binned <paramref name="binning"/> to a side see it,
        /// for light spread evenly inside each bin: only the native pixels along a bin's edge couple
        /// across it. The sum is kept, never renormalised. Binning 1 returns the kernel itself.
        /// </summary>
        public static double[,] AtBinning(double[,] nativeKernel, int binning)
        {
            if (nativeKernel == null || nativeKernel.GetLength(0) != 3 || nativeKernel.GetLength(1) != 3) return null;
            if (binning <= 1) return nativeKernel;

            var binned = new double[3, 3];
            double share = 1.0 / ((double)binning * binning);
            for (int row = 0; row < binning; row++)
            {
                for (int col = 0; col < binning; col++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int by = BinOffset(row + dy, binning);
                        for (int dx = -1; dx <= 1; dx++)
                            binned[by + 1, BinOffset(col + dx, binning) + 1] += share * nativeKernel[dy + 1, dx + 1];
                    }
                }
            }
            return binned;
        }

        // The neighbouring bin a native index falls in: -1, 0 or +1, since the kernel reaches one pixel.
        private static int BinOffset(int nativeIndex, int binning) => nativeIndex < 0 ? -1 : nativeIndex / binning;
    }
}
