using System;
using System.Threading.Tasks;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Lays a galaxy's MEASURED shape onto the signal plane, in electrons.
    ///
    /// THE TRANSFORM IS A HOMOGRAPHY, EXACTLY. The stored map and the frame are both gnomonic
    /// (tangent-plane) projections of the same sky, about different tangent points, and the
    /// mapping between two such planes is projective and nothing weaker. An affine approximation,
    /// which is what deriving a rotation and a scale would amount to, has an error that grows with
    /// the square of the map's angular size: tools/galaxy-image-tests measures it at 0.02 map
    /// pixels for M51's 22 arcminute map, which is negligible, and the same scaling puts it at a
    /// few pixels for a map of M31's extent. Solving the projective form exactly costs four
    /// correspondences once and two divisions per pixel, so there is no reason to approximate it.
    ///
    /// THE FOUR CORRESPONDENCES COME FROM THE FRAME'S OWN PROJECTION, not from a formula written
    /// here. The caller projects four known map positions through the very code that places the
    /// stars, so the mount's field rotation, the sensor's parity and the projection's distortion
    /// are inherited rather than re-derived; re-deriving any of them by hand is how a galaxy ends
    /// up mirrored while still looking like a galaxy.
    ///
    /// FLUX IS CONSERVED BY CONSTRUCTION. The map holds a fraction of the total flux per MAP
    /// pixel, so moving it onto frame pixels of a different size and shape means multiplying by the
    /// Jacobian of the transform, which is the number of map pixels a frame pixel covers. Where a
    /// frame pixel covers many map pixels the map is averaged over it rather than point sampled,
    /// because point sampling a resolved galaxy at a coarser grid aliases its arms.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class GalaxyImageRenderer
    {
        /// <summary>Ceiling on the supersampling per axis inside one frame pixel. 8x8 covers a frame pixel spanning 64 map pixels, which is past any instrument in the roster.</summary>
        private const int MaxSupersample = 8;

        /// <summary>
        /// Solves the projective transform taking FRAME pixels to MAP pixels from four
        /// correspondences.
        ///
        /// Returns null when the four points are degenerate, which is what a galaxy behind the
        /// observer or a field seen edge-on would produce; the caller then draws nothing rather
        /// than something arbitrary.
        /// </summary>
        public static double[] SolveFrameToMap(double[] frameX, double[] frameY,
                                               double[] mapU, double[] mapV)
        {
            if (frameX == null || frameY == null || mapU == null || mapV == null) return null;
            if (frameX.Length < 4 || frameY.Length < 4 || mapU.Length < 4 || mapV.Length < 4) return null;

            // u = (h0 x + h1 y + h2) / (h6 x + h7 y + 1), v likewise with h3..h5.
            var a = new double[8, 9];
            for (int i = 0; i < 4; i++)
            {
                double x = frameX[i], y = frameY[i], u = mapU[i], v = mapV[i];
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(u) || double.IsNaN(v)) return null;

                int r = 2 * i;
                a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1.0;
                a[r, 6] = -u * x; a[r, 7] = -u * y; a[r, 8] = u;

                r++;
                a[r, 3] = x; a[r, 4] = y; a[r, 5] = 1.0;
                a[r, 6] = -v * x; a[r, 7] = -v * y; a[r, 8] = v;
            }

            for (int col = 0; col < 8; col++)
            {
                int pivot = col;
                for (int row = col + 1; row < 8; row++)
                    if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
                if (Math.Abs(a[pivot, col]) < 1e-12) return null;

                if (pivot != col)
                    for (int k = col; k < 9; k++)
                    {
                        double t = a[col, k]; a[col, k] = a[pivot, k]; a[pivot, k] = t;
                    }

                double d = a[col, col];
                for (int k = col; k < 9; k++) a[col, k] /= d;

                for (int row = 0; row < 8; row++)
                {
                    if (row == col) continue;
                    double f = a[row, col];
                    if (f == 0.0) continue;
                    for (int k = col; k < 9; k++) a[row, k] -= f * a[col, k];
                }
            }

            var h = new double[8];
            for (int i = 0; i < 8; i++)
            {
                h[i] = a[i, 8];
                if (double.IsNaN(h[i]) || double.IsInfinity(h[i])) return null;
            }
            return h;
        }

        /// <summary>
        /// Adds the galaxy to the plane and returns the electrons actually deposited inside it.
        /// </summary>
        /// <param name="image">The measured map, with its pixels loaded.</param>
        /// <param name="frameToMap">Transform from SolveFrameToMap.</param>
        /// <param name="wavelengthNm">Effective wavelength of the instrument's passband, which decides how the two measured bands are blended.</param>
        /// <param name="totalElectrons">Total flux of the galaxy (and of any companion the map swallows), electrons.</param>
        /// <param name="boundsX">Frame x of the map's four corners, for the box to scan.</param>
        public static double Deposit(
            float[] plane, int width, int height,
            GalaxyImage image, double[] frameToMap, double wavelengthNm, double totalElectrons,
            double[] boundsX, double[] boundsY)
        {
            if (plane == null || plane.Length != width * height) return 0.0;
            if (image == null || image.Bands == null || frameToMap == null) return 0.0;
            if (!(totalElectrons > 0.0)) return 0.0;

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < boundsX.Length; i++)
            {
                if (double.IsNaN(boundsX[i]) || double.IsNaN(boundsY[i])) return 0.0;
                if (boundsX[i] < minX) minX = boundsX[i];
                if (boundsX[i] > maxX) maxX = boundsX[i];
                if (boundsY[i] < minY) minY = boundsY[i];
                if (boundsY[i] > maxY) maxY = boundsY[i];
            }

            int x0 = (int)Math.Floor(minX) - 1, x1 = (int)Math.Ceiling(maxX) + 1;
            int y0 = (int)Math.Floor(minY) - 1, y1 = (int)Math.Ceiling(maxY) + 1;
            if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0;
            if (x1 > width) x1 = width; if (y1 > height) y1 = height;
            if (x1 <= x0 || y1 <= y0) return 0.0;

            double h0 = frameToMap[0], h1 = frameToMap[1], h2 = frameToMap[2];
            double h3 = frameToMap[3], h4 = frameToMap[4], h5 = frameToMap[5];
            double h6 = frameToMap[6], h7 = frameToMap[7];

            // ROW BY ROW, and across cores when the box is big enough to pay for it. Each frame
            // pixel is written by one row and read by none, so the picture does not depend on how
            // the rows were divided; the electrons deposited are totalled PER ROW and the rows
            // added afterwards in row order, which is a fixed order whatever the thread count.
            // See ParallelWork for why that distinction is the whole of the rule.
            var rowDeposited = new double[y1 - y0];

            Action<int> depositRow = y =>
            {
                double rowSum = 0.0;
                for (int x = x0; x < x1; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double den = h6 * px + h7 * py + 1.0;
                    if (Math.Abs(den) < 1e-12) continue;
                    double au = h0 * px + h1 * py + h2;
                    double av = h3 * px + h4 * py + h5;
                    double u = au / den, v = av / den;

                    // A margin of one map pixel, so a frame pixel straddling the edge still reads
                    // the taper the packer put there instead of being dropped.
                    if (u < -1.0 || v < -1.0 || u > image.Size || v > image.Size) continue;

                    double inv = 1.0 / (den * den);
                    double dudx = (h0 * den - au * h6) * inv;
                    double dudy = (h1 * den - au * h7) * inv;
                    double dvdx = (h3 * den - av * h6) * inv;
                    double dvdy = (h4 * den - av * h7) * inv;
                    double jacobian = Math.Abs(dudx * dvdy - dudy * dvdx);
                    if (!(jacobian > 0.0)) continue;

                    int steps = (int)Math.Ceiling(Math.Sqrt(jacobian));
                    if (steps < 1) steps = 1;
                    if (steps > MaxSupersample) steps = MaxSupersample;

                    double mean;
                    if (steps == 1)
                    {
                        mean = image.Sample(u, v, wavelengthNm);
                    }
                    else
                    {
                        double sum = 0.0, step = 1.0 / steps;
                        for (int sy = 0; sy < steps; sy++)
                        {
                            double qy = y + (sy + 0.5) * step;
                            for (int sx = 0; sx < steps; sx++)
                            {
                                double qx = x + (sx + 0.5) * step;
                                double d = h6 * qx + h7 * qy + 1.0;
                                if (Math.Abs(d) < 1e-12) continue;
                                sum += image.Sample((h0 * qx + h1 * qy + h2) / d,
                                                    (h3 * qx + h4 * qy + h5) / d, wavelengthNm);
                            }
                        }
                        mean = sum / (steps * steps);
                    }

                    if (!(mean > 0.0)) continue;
                    double value = mean * jacobian * totalElectrons;
                    plane[y * width + x] += (float)value;
                    rowSum += value;
                }
                rowDeposited[y - y0] = rowSum;
            };

            long pixelsInBox = (long)(x1 - x0) * (y1 - y0);
            if (ParallelWork.Worthwhile(pixelsInBox))
                Parallel.For(y0, y1, ParallelWork.Options, depositRow);
            else
                for (int y = y0; y < y1; y++) depositRow(y);

            double deposited = 0.0;
            for (int i = 0; i < rowDeposited.Length; i++) deposited += rowDeposited[i];
            return deposited;
        }
    }
}
