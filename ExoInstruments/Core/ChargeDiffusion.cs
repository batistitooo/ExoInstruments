namespace ExoInstruments.Core
{
    /// <summary>
    /// CCD charge diffusion: photoelectrons that cross a pixel boundary before they are collected.
    ///
    /// It moves real charge, so the chain applies it to the expected photoelectrons ahead of the
    /// shot-noise draw. Electrons that diffuse independently leave every pixel's count Poisson about
    /// the diffused mean; convolving counts already drawn would smooth their shot noise instead.
    /// Interpixel capacitance (Core.InfraredArray) is the readout-side effect and moves no charge.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class ChargeDiffusion
    {
        /// <summary>
        /// Tiny Tim's WFPC2 "CCD Pixel Scattering Function (estimates charge diffusion)", verbatim
        /// from wfpc2pc1.pup. The Tiny Tim User's Guide (v6.3) prints the same kernel, uses it at all
        /// wavelengths although it suits 500 to 600 nm, and puts it at about 14 mas of jitter in the PC.
        /// </summary>
        public static readonly double[,] Wfpc2TinyTimKernel =
        {
            { 0.0125, 0.05, 0.0125 },
            { 0.0500, 0.75, 0.0500 },
            { 0.0125, 0.05, 0.0125 },
        };

        /// <summary>
        /// A native-pixel kernel as seen by pixels binned <paramref name="binning"/> to a side,
        /// averaged over where in the binned pixel the charge starts. Only the outer native rows
        /// and columns leak, so binning weakens it. Binning 1 returns the kernel itself. The same
        /// rescaling the IPC step uses.
        /// </summary>
        public static double[,] ForBinning(double[,] kernel, int binning)
            => PixelKernel.AtBinning(kernel, binning);

        /// <summary>
        /// Diffuses a plane of expected electrons in place. The convolution and edge replication are
        /// the IPC step's, so a uniform sky comes out unchanged.
        /// </summary>
        public static void Apply(float[] electrons, int width, int height, double[,] kernel)
            => InfraredArray.ApplyCoupling(electrons, width, height, kernel);
    }
}
