using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Whether anything is standing in front of the telescope, and what that costs.
    ///
    /// WHY A CLEAR APERTURE IS A HARD GATE AND NOT A PENALTY. A partially blocked pupil is not a
    /// telescope that collects proportionally less light. It is a telescope with a different
    /// pupil, and therefore a different point-spread function: an obstruction of arbitrary shape
    /// diffracts into a pattern that depends on its outline, its distance from the pupil plane
    /// and its orientation, none of which can be recovered from "12 per cent of the area is
    /// covered". This pipeline computes its PSF from a real pupil (see PupilDiffraction) and
    /// there is no honest way to hand it a fairing edge.
    ///
    /// So the model is the one real observatories use: the aperture is clear, or the instrument
    /// does not observe. That is not a simplification standing in for something better, it is
    /// how the constraint actually works. No observatory takes science frames through its own
    /// structure and corrects for it afterward.
    ///
    /// What IS reported is the blocked fraction and what is blocking, because a player who has
    /// mounted the telescope behind a solar panel needs to be told which part to move, not just
    /// that something is wrong.
    ///
    /// THE SAMPLING GEOMETRY, and why it is not a single ray down the boresight. A single ray
    /// passes cleanly through a ring-shaped obstruction and misses a fairing that clips only the
    /// edge of the pupil. Light enters across the whole open annulus, so the annulus is what has
    /// to be sampled: a sunflower (Vogel) spiral over it, which distributes points evenly by
    /// area without the clustering a polar grid produces at the centre or the anisotropy a
    /// square grid produces at the corners.
    ///
    /// Pure C# with no Unity dependency: this generates the sample offsets, and the KSP layer
    /// casts the rays.
    /// </summary>
    public static class ApertureObstruction
    {
        /// <summary>
        /// The golden angle, 2 pi (1 - 1/phi), in radians. Successive points in a Vogel spiral
        /// are separated by this angle, which is the arrangement that packs points over a disc
        /// most evenly because the golden ratio is the irrational hardest to approximate by
        /// rationals, so no two points ever fall on the same radial line.
        /// </summary>
        public static readonly double GoldenAngleRad = Math.PI * (3.0 - Math.Sqrt(5.0));

        /// <summary>
        /// Sample offsets across the telescope's OPEN annulus, in metres from the boresight axis,
        /// as (x, y) pairs packed into a flat array of length 2*count.
        ///
        /// The secondary's shadow is excluded rather than sampled: a ray through the middle of a
        /// Cassegrain is blocked by the instrument's own secondary mirror whatever else is in
        /// front, so counting it as an obstruction would report every reflector in the roster as
        /// permanently blocked.
        /// </summary>
        public static double[] SampleOffsets(double apertureMeters, double obstructionRatio, int count)
        {
            if (count < 1) count = 1;
            var offsets = new double[2 * count];
            if (!(apertureMeters > 0.0)) return offsets;

            double outer = 0.5 * apertureMeters;
            double inner = outer * Math.Max(0.0, Math.Min(0.95, obstructionRatio));

            // Equal-area radial mapping: for a point uniformly distributed over an annulus,
            // r = sqrt(inner^2 + t (outer^2 - inner^2)) with t uniform on [0,1]. The half-step
            // offset keeps samples off both rims, where a ray is ambiguous.
            double innerSq = inner * inner, outerSq = outer * outer;
            for (int i = 0; i < count; i++)
            {
                double t = (i + 0.5) / count;
                double r = Math.Sqrt(innerSq + t * (outerSq - innerSq));
                double theta = i * GoldenAngleRad;
                offsets[2 * i] = r * Math.Cos(theta);
                offsets[2 * i + 1] = r * Math.Sin(theta);
            }
            return offsets;
        }

        /// <summary>
        /// Fraction of the open aperture blocked, from how many sample rays were stopped.
        ///
        /// Equal-area sampling is what makes this a straight count: every sample stands for the
        /// same area of pupil, so no weighting is needed and none is applied.
        /// </summary>
        public static double BlockedFraction(int blockedSamples, int totalSamples)
        {
            if (totalSamples <= 0) return 0.0;
            return (double)Math.Max(0, Math.Min(totalSamples, blockedSamples)) / totalSamples;
        }

        /// <summary>
        /// Below this blocked fraction the aperture counts as clear.
        ///
        /// Not a tolerance for real obstruction: it is a tolerance for the SAMPLING, since a ray
        /// grazing the telescope's own mounting hardware at the very rim of the pupil can
        /// register on one sample out of a hundred and mean nothing. One sample in a hundred is
        /// where it sits, so a genuine obstruction of any real size is caught.
        /// </summary>
        public const double ClearApertureTolerance = 0.01;

        /// <summary>True when the aperture is clear enough to observe through.</summary>
        public static bool IsClear(double blockedFraction) => blockedFraction <= ClearApertureTolerance;
    }
}
