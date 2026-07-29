using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Lays a galaxy's Sersic profile onto the signal plane, in electrons.
    ///
    /// The profile is elliptical: the isophotes share the catalogued axis ratio and position angle,
    /// and R is the semi-major axis of the isophote a point lies on. That is the same convention
    /// the catalogued D25 and b/a are measured in, so nothing has to be reinterpreted between the
    /// catalogue and the render.
    ///
    /// PIXEL INTEGRATION, and why the centre needs it. A Sersic profile of index n has an infinite
    /// central slope -- for n = 4 the surface brightness rises as R^(-3/4) all the way in -- so the
    /// value at a pixel's centre is not its average over the pixel, and point sampling a galaxy's
    /// nucleus overstates or understates it by an unbounded factor depending only on where the
    /// pixel grid happens to fall. Pixels near the centre are therefore integrated by subdivision,
    /// with the subdivision level set by how much the profile varies across the pixel rather than
    /// by a fixed rule. Far out the profile is flat on a pixel scale and one sample is exact to
    /// better than the frame's own noise.
    ///
    /// TRUNCATION is by enclosed flux, at a radius the caller sets, and the profile is NOT
    /// renormalised afterwards: what falls outside the drawn region either left the sensor or is
    /// below one electron, and handing it back to the pixels inside would brighten the galaxy by
    /// an amount that depends on the frame size. The deposited total is returned so a caller can
    /// check it against the analytic one.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class GalaxyRenderer
    {
        /// <summary>
        /// Largest fractional change in surface brightness across one pixel that a single sample is
        /// allowed to represent. Below it the profile is flat enough that the midpoint value IS the
        /// average to second order; above it the pixel is subdivided until it is.
        /// </summary>
        private const double MaxVariationPerPixel = 0.02;

        /// <summary>
        /// Subdivision ceiling per axis, so a nucleus cannot cost unbounded time.
        ///
        /// 128 rather than 32 because 32 is where the flux residual stops being negligible on the
        /// hardest case a real catalogue contains: an n = 4 spheroid whose half-light radius is
        /// three pixels and whose minor axis is a quarter of that. There the variation across the
        /// central pixel asks for nearly 300 subdivisions and the cap bound hard, losing 2.6e-3 of
        /// the total. Only a handful of pixels ever reach the ceiling -- the requirement falls as
        /// 1/R -- so raising it costs a bounded number of extra samples per galaxy, not a factor.
        /// </summary>
        private const int MaxSubdivision = 128;

        /// <summary>
        /// Adds the galaxy to the plane and returns the electrons actually deposited inside it.
        /// </summary>
        /// <param name="plane">Signal plane, electrons, row-major.</param>
        /// <param name="centreX">Galaxy centre, pixels, measured from the plane's origin.</param>
        /// <param name="majorAxisX">Unit vector along the major axis IN PIXEL SPACE, which the caller derives by projecting two sky positions so that field rotation and projection distortion are already in it.</param>
        /// <param name="effectiveRadiusPx">Half-light semi-major axis, pixels.</param>
        /// <param name="axisRatio">Minor over major.</param>
        /// <param name="sersicIndex">Sersic n.</param>
        /// <param name="totalElectrons">Total flux of the whole profile, electrons.</param>
        /// <param name="truncationRadii">Draw out to this many effective radii along the major axis.</param>
        public static double Deposit(
            float[] plane, int width, int height,
            double centreX, double centreY,
            double majorAxisX, double majorAxisY,
            double effectiveRadiusPx, double axisRatio, double sersicIndex,
            double totalElectrons, double truncationRadii)
        {
            if (plane == null || plane.Length != width * height) return 0.0;
            if (!(effectiveRadiusPx > 0.0) || !(sersicIndex > 0.0) || !(totalElectrons > 0.0)) return 0.0;

            double q = Math.Max(1e-3, Math.Min(1.0, axisRatio));
            double axisLength = Math.Sqrt(majorAxisX * majorAxisX + majorAxisY * majorAxisY);
            if (!(axisLength > 0.0)) return 0.0;
            double ux = majorAxisX / axisLength, uy = majorAxisY / axisLength;

            double b = SersicProfile.Bn(sersicIndex);
            double invN = 1.0 / sersicIndex;

            // I_e from the total: F = I_e R_e^2 q f(n), the circular result scaled by the ellipse's
            // own area factor. Per pixel, so the pixel area of 1 is already in it.
            double intensityAtRe = totalElectrons
                / (effectiveRadiusPx * effectiveRadiusPx * q * SersicProfile.TotalFluxFactor(sersicIndex));

            double maxR = truncationRadii * effectiveRadiusPx;
            int x0 = (int)Math.Floor(centreX - maxR) - 1, x1 = (int)Math.Ceiling(centreX + maxR) + 1;
            int y0 = (int)Math.Floor(centreY - maxR) - 1, y1 = (int)Math.Ceiling(centreY + maxR) + 1;
            if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0;
            if (x1 > width) x1 = width; if (y1 > height) y1 = height;

            double deposited = 0.0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    double dx = x + 0.5 - centreX, dy = y + 0.5 - centreY;
                    double along = dx * ux + dy * uy;
                    double across = -dx * uy + dy * ux;
                    double r = Math.Sqrt(along * along + (across / q) * (across / q));
                    if (r > maxR) continue;

                    // How fast the profile changes across one pixel, from its own logarithmic
                    // derivative: dlnI/dR = -(b/n) R^(1/n - 1) / R_e^(1/n).
                    double variation = r > 0.0
                        ? b * invN * Math.Pow(r / effectiveRadiusPx, invN) / (q * r)
                        : double.PositiveInfinity;
                    int steps = variation > MaxVariationPerPixel
                        ? (int)Math.Min(MaxSubdivision, Math.Ceiling(variation / MaxVariationPerPixel))
                        : 1;

                    double value;
                    if (steps <= 1)
                    {
                        value = intensityAtRe * Math.Exp(-b * (Math.Pow(r / effectiveRadiusPx, invN) - 1.0));
                    }
                    else
                    {
                        double sum = 0.0, h = 1.0 / steps;
                        for (int sy = 0; sy < steps; sy++)
                        {
                            double sdy = y + (sy + 0.5) * h - centreY;
                            for (int sx = 0; sx < steps; sx++)
                            {
                                double sdx = x + (sx + 0.5) * h - centreX;
                                double sa = sdx * ux + sdy * uy;
                                double sc = -sdx * uy + sdy * ux;
                                double sr = Math.Sqrt(sa * sa + (sc / q) * (sc / q));
                                sum += Math.Exp(-b * (Math.Pow(sr / effectiveRadiusPx, invN) - 1.0));
                            }
                        }
                        value = intensityAtRe * sum * h * h;
                    }

                    plane[y * width + x] += (float)value;
                    deposited += value;
                }
            }
            return deposited;
        }

        /// <summary>
        /// Radius, in effective radii, at which the profile's surface brightness falls to
        /// floorElectronsPerPixel -- the point past which the galaxy cannot register on this
        /// detector at all.
        ///
        /// This is the truncation criterion that makes the cut physical rather than arbitrary: a
        /// Sersic profile has no edge, but a detector does, and light spread below one electron per
        /// pixel over an exposure is light the frame has no way to record. Bounded by
        /// maxRadii so a bright galaxy on a large sensor cannot ask for an unbounded box.
        /// </summary>
        public static double TruncationRadiiForFloor(
            double totalElectrons, double effectiveRadiusPx, double axisRatio, double sersicIndex,
            double floorElectronsPerPixel, double maxRadii)
        {
            if (!(totalElectrons > 0.0) || !(effectiveRadiusPx > 0.0) || !(floorElectronsPerPixel > 0.0))
                return 0.0;

            double q = Math.Max(1e-3, Math.Min(1.0, axisRatio));
            double intensityAtRe = totalElectrons
                / (effectiveRadiusPx * effectiveRadiusPx * q * SersicProfile.TotalFluxFactor(sersicIndex));
            if (!(intensityAtRe > floorElectronsPerPixel)) return 0.0;

            // I(R) = I_e exp{-b[(R/R_e)^(1/n) - 1]} = floor, solved directly.
            double b = SersicProfile.Bn(sersicIndex);
            double t = 1.0 - Math.Log(floorElectronsPerPixel / intensityAtRe) / b;
            if (!(t > 0.0)) return 0.0;
            return Math.Min(maxRadii, Math.Pow(t, sersicIndex));
        }
    }
}
