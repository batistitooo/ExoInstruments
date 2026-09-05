using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// A whole detector's fringe map, as the excess over unity, packed the way the sky maps are.
    ///
    /// HERE RATHER THAN IN THE RENDERER, AND THAT PLACEMENT IS THE FIX RATHER THAN A TIDY-UP.
    /// This is the loop that must stay outside the spectral integral (see Fringing.Passband for
    /// what happened when it did not), and the way to guarantee that is to write it once, next to
    /// the passband, where the integral is not reachable from inside it and where a harness in
    /// tools/ compiles the SHIPPED loop instead of a transcription of it. A frame-sized loop living
    /// in the Unity layer is a loop no harness can compile, so every harness that wants to time it
    /// must reproduce it, and a reproduction is only worth what its arguments are.
    ///
    /// Nothing about a camera enters. The caller supplies a unit-mean thickness modulation and the
    /// layer's own published figures, so this file has no opinion about which instrument is fitted.
    ///
    /// Pure C#, no Unity dependency, like the rest of Core.
    /// </summary>
    public static class FringeMap
    {
        /// <summary>
        /// Builds the map: one binary16 per pixel, holding F(P) - 1 at that pixel's optical path.
        ///
        /// Split across cores under ParallelWork's policy, which is exact here for the reason that
        /// policy states: each pixel writes one element no other worker touches, nothing is
        /// accumulated across pixels, and the thickness field was drawn serially before the loop
        /// began. The block size is a scheduling granularity only; the map does not depend on it,
        /// and tools/fringe-tests checks that at one worker and at many.
        /// </summary>
        public static ushort[] Build(
            Fringing.Passband passband, float[] thicknessModulation,
            double thicknessMicrons, double thicknessSigmaMicrons, double referenceWavelengthNm)
        {
            if (passband == null) throw new ArgumentNullException("passband");
            if (thicknessModulation == null) throw new ArgumentNullException("thicknessModulation");

            int n = thicknessModulation.Length;
            var map = new ushort[n];
            if (n <= 0 || !passband.CanFringe) return map;

            // The refractive index is evaluated at the FILTER's centre, which does not vary across
            // the frame, so the linear scan inside SiliconRefractiveIndex is hoisted out of the
            // loop. Same value, and the multiplication below is left in the order
            // Fringing.OpticalPathNm forms it, so the path is the same double it always was.
            double indexAtCentre = Fringing.SiliconRefractiveIndex(referenceWavelengthNm);

            const int Block = 4096;
            int blocks = (n + Block - 1) / Block;

            Action<int> fill = delegate(int b)
            {
                int from = b * Block;
                int to = Math.Min(from + Block, n);
                for (int i = from; i < to; i++)
                {
                    double localThickness = thicknessMicrons
                                          + (thicknessModulation[i] - 1.0) * thicknessSigmaMicrons;
                    double path = 2.0 * localThickness * 1000.0 * indexAtCentre;
                    // (1 + x) - 1, and NOT x. Adding one snaps x onto the 2^-53 grid before the
                    // subtraction takes it off again (which is then exact, by Sterbenz), and the
                    // map has always stored the snapped value. Storing the unsnapped x would be
                    // very slightly more accurate and therefore wrong: it would make the harness's
                    // bit-identity assertion a tolerance instead of an equality, and the point of
                    // this whole change is that no archived frame moved.
                    map[i] = Float16.FromDouble(passband.ModulationAt(path) - 1.0);
                }
            };

            if (ParallelWork.Worthwhile((long)n * passband.FringingSampleCount))
                System.Threading.Tasks.Parallel.For(0, blocks, ParallelWork.Options, delegate(int b) { fill(b); });
            else
                for (int b = 0; b < blocks; b++) fill(b);

            return map;
        }
    }
}
