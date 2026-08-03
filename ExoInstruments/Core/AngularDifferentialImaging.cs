using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The only thing that separates a planet from a speckle: the planet moves and the speckle does
    /// not.
    ///
    /// WHY A LONGER EXPOSURE DOES NOT HELP. Core.SpeckleField establishes that 71.3% of a
    /// coronagraphic image's pattern is static over an hour. Static noise does not average down, so
    /// integrating longer raises the signal and the speckle together and the contrast stops
    /// improving. That is the wall every high-contrast observation hits, and no amount of aperture
    /// or exposure time gets through it.
    ///
    /// WHAT GETS THROUGH IT. Marois, Lafreniere, Doyon, Macintosh and Nadeau (2006, ApJ 641, 556)
    /// showed the way: stop tracking the field. An alt-azimuth telescope observing in PUPIL-
    /// STABILISED mode holds the instrument fixed relative to the telescope pupil and lets the sky
    /// rotate through it at the parallactic rate. The speckles, which belong to the optics, stay
    /// where they are; a real companion, which belongs to the sky, sweeps around the star. Build a
    /// reference from the sequence's own median, subtract it from every frame, then DEROTATE and
    /// stack: the static pattern subtracts away and the companion adds up.
    ///
    /// This is why SPHERE is on a Nasmyth platform behind a derotator that can be switched off, and
    /// why its pupil stops come in a family that hides the telescope spiders (Core.Coronagraph's
    /// STOPB1_2 and its siblings): with the pupil fixed, the spiders no longer rotate out of the
    /// way of anything.
    ///
    /// THE COST, WHICH IS NOT OPTIONAL. The companion is present in the reference too, so
    /// subtracting the reference subtracts part of the companion from itself. That SELF-SUBTRACTION
    /// is worst where the field rotation moves the companion least, which is at small separations,
    /// and it is the reason an ADI contrast curve turns over near the inner working angle instead
    /// of improving all the way in. Schmid et al. (2018) measure it on SPHERE directly: their
    /// median-subtracted flux ratio for alpha Hyi B falls from 4.7 to 3.6 (I_PRIM) and from 2.6 to
    /// 1.6 (R_PRIM) against the un-subtracted value, while the signal-to-noise rises from 10.0 to
    /// 15.9 and from 4.6 to 10.5. Both halves of that trade are real and both are modelled here.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class AngularDifferentialImaging
    {
        /// <summary>
        /// Parallactic angle in degrees: the angle at the target between the direction to the
        /// celestial pole and the direction to the zenith.
        ///
        /// This is the quantity that rotates the sky through a pupil-stabilised instrument, and it
        /// is pure spherical trigonometry on the observatory's latitude, the target's declination
        /// and the hour angle:
        ///
        ///     tan q = sin H / (tan(lat) cos(dec) - sin(dec) cos H)
        ///
        /// Written with Atan2 rather than Atan so the quadrant is right without a sign table; the
        /// numerator and denominator are passed separately for exactly that reason.
        /// </summary>
        public static double ParallacticAngleDeg(double hourAngleHours, double declinationDeg, double latitudeDeg)
        {
            double h = hourAngleHours * (Math.PI / 12.0);
            double dec = declinationDeg * (Math.PI / 180.0);
            double lat = latitudeDeg * (Math.PI / 180.0);

            double numerator = Math.Sin(h);
            double denominator = Math.Tan(lat) * Math.Cos(dec) - Math.Sin(dec) * Math.Cos(h);
            return Math.Atan2(numerator, denominator) * (180.0 / Math.PI);
        }

        /// <summary>
        /// Total field rotation over an observation, in degrees: the parallactic angle swept
        /// between its two ends.
        ///
        /// Wrapped into (-180, 180] because the raw difference of two angles either side of the
        /// meridian can come out the long way round, and a sequence that crosses the meridian is
        /// precisely the one an observer schedules: the parallactic angle changes fastest there, so
        /// that is where the most rotation is bought per hour of telescope time.
        /// </summary>
        public static double FieldRotationDeg(
            double startHourAngleHours, double endHourAngleHours, double declinationDeg, double latitudeDeg)
        {
            double q0 = ParallacticAngleDeg(startHourAngleHours, declinationDeg, latitudeDeg);
            double q1 = ParallacticAngleDeg(endHourAngleHours, declinationDeg, latitudeDeg);
            double d = q1 - q0;
            while (d > 180.0) d -= 360.0;
            while (d <= -180.0) d += 360.0;
            return d;
        }

        /// <summary>
        /// Rotation, in degrees, that displaces a source at the given separation by one resolution
        /// element.
        ///
        /// Marois et al. (2006) state the criterion in exactly this form: a companion is separated
        /// from its own speckle when the field has turned far enough to move it by about
        /// lambda/D, and the required angle therefore grows as the separation shrinks. At SPHERE's
        /// scale (lambda/D = 15.7 mas at 626 nm on 8.2 m) a source at 200 mas needs 4.5 degrees
        /// while one at 1 arcsec needs 0.9, which is why the inner region is the expensive one.
        /// </summary>
        public static double RotationForOneResolutionElementDeg(
            double separationMas, double wavelengthNm, double apertureMeters)
        {
            if (!(separationMas > 0.0)) return 180.0;
            double lambdaOverD = SpeckleField.LambdaOverDMas(wavelengthNm, apertureMeters);
            if (!(lambdaOverD > 0.0)) return 0.0;
            double radians = lambdaOverD / separationMas;
            if (radians >= Math.PI) return 180.0;
            return radians * (180.0 / Math.PI);
        }

        /// <summary>
        /// The fraction of a companion's own flux that survives median-subtraction ADI, at a given
        /// separation and total field rotation.
        ///
        /// THE MODEL, and the reason it is geometric rather than fitted. Over the sequence a
        /// companion at separation r traces an arc; the reference built from that sequence contains
        /// the companion smeared along the arc. Where the arc is shorter than one resolution
        /// element the companion sits on top of itself in every frame, the reference contains it at
        /// essentially full strength, and subtracting the reference removes essentially all of it.
        /// Where the arc is many resolution elements long the companion occupies each position for
        /// only a small part of the sequence, so the MEDIAN of the sequence at any one position is
        /// the speckle rather than the companion, and almost nothing is lost.
        ///
        /// The transition is therefore governed by one dimensionless number, the arc length in
        /// resolution elements:
        ///
        ///     n = rotation / RotationForOneResolutionElement(r)
        ///
        /// and the throughput is modelled as n/(n+1), which is 0.5 at one resolution element of
        /// travel, tends to 1 for a long arc, and tends to 0 for none. The FORM of that transition
        /// is a modelling choice and is declared as one (section 12); what is not a choice is the
        /// variable it is a function of, nor the two limits it runs between.
        ///
        /// WHAT THE ONE AVAILABLE MEASUREMENT SAYS, and why it is a sanity check rather than a
        /// validation. Schmid et al. (2018) Test C rotates the instrument to 0, 60 and 120 degrees
        /// on alpha Hyi B, whose separation is 91 mas, and reports a median-subtracted flux ratio
        /// of 3.6 against 4.7 un-subtracted in I_PRIM, a throughput of 0.77, with the
        /// signal-to-noise rising from 10.0 to 15.9. At that separation and 790 nm, lambda/D is
        /// 19.9 mas and one resolution element of travel costs 12.5 degrees, so n = 9.6 and this
        /// expression returns 0.91.
        ///
        /// That is the right order and not an agreement, and the gap should not be read as either
        /// side being wrong. Their test is a median of THREE frames, the smallest number for which
        /// a median exists, where the companion is present in one of the three at any sky position
        /// and the estimator's behaviour is dominated by that; the paper's own table footnotes the
        /// ratio as "affected by self-subtraction". A continuous sequence of many frames, which is
        /// what this expression describes, is a different estimator. No published throughput curve
        /// for SPHERE/ZIMPOL was found to calibrate against, and one measurement at one separation
        /// cannot constrain a curve, so the form stays declared rather than fitted.
        /// </summary>
        public static double SelfSubtractionThroughput(
            double separationMas, double fieldRotationDeg, double wavelengthNm, double apertureMeters)
        {
            double perElement = RotationForOneResolutionElementDeg(separationMas, wavelengthNm, apertureMeters);
            if (!(perElement > 0.0)) return 1.0;

            double n = Math.Abs(fieldRotationDeg) / perElement;
            if (!(n > 0.0)) return 0.0;
            return n / (n + 1.0);
        }

        // ---------------------------------------------------------------- the reduction itself

        /// <summary>
        /// Rotates a frame about its centre by the given angle, counter-clockwise, with bilinear
        /// interpolation.
        ///
        /// The derotation step of angular differential imaging, and the reason the technique has a
        /// cost beyond self-subtraction: interpolating a speckle field smooths it slightly, so a
        /// derotated frame is marginally quieter than the one it came from, and a reduction that
        /// forgot to derotate the reference as well would mistake that for a gain.
        ///
        /// Pixels that rotate in from outside the frame read zero. That is what a detector with no
        /// data there records, and padding them with the frame's mean instead would inject a
        /// discontinuity at exactly the radius a contrast curve is measured over.
        /// </summary>
        public static float[] Rotate(float[] frame, int width, int height, double angleDeg)
        {
            var output = new float[width * height];
            if (frame == null || width <= 0 || height <= 0) return output;

            double t = angleDeg * Math.PI / 180.0;
            double cos = Math.Cos(t), sin = Math.Sin(t);
            double cx = 0.5 * (width - 1), cy = 0.5 * (height - 1);

            for (int y = 0; y < height; y++)
            {
                double dy = y - cy;
                for (int x = 0; x < width; x++)
                {
                    double dx = x - cx;
                    // Sample the SOURCE at the inverse rotation of this destination pixel, which is
                    // what makes the output complete rather than speckled with holes.
                    double sx = cx + cos * dx + sin * dy;
                    double sy = cy - sin * dx + cos * dy;

                    int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                    if (x0 < 0 || y0 < 0 || x0 + 1 >= width || y0 + 1 >= height) continue;

                    double fx = sx - x0, fy = sy - y0;
                    double a = frame[y0 * width + x0], b = frame[y0 * width + x0 + 1];
                    double c = frame[(y0 + 1) * width + x0], d = frame[(y0 + 1) * width + x0 + 1];
                    output[y * width + x] = (float)(a * (1 - fx) * (1 - fy) + b * fx * (1 - fy)
                                                  + c * (1 - fx) * fy + d * fx * fy);
                }
            }
            return output;
        }

        /// <summary>
        /// The reference frame: the temporal MEDIAN of the sequence, pixel by pixel.
        ///
        /// Median rather than mean, and that is the whole of why this works. A companion occupies
        /// any one sky position for only part of a sequence that rotates, so at that position most
        /// frames show speckle and a few show speckle plus companion; the median returns one of the
        /// many rather than an average pulled up by the few. A mean reference would contain the
        /// companion in proportion to its dwell time and subtract exactly that much of it away.
        /// </summary>
        public static float[] MedianReference(IList<float[]> frames, int pixelCount)
        {
            var reference = new float[pixelCount];
            if (frames == null || frames.Count == 0) return reference;

            int n = frames.Count;
            var column = new float[n];
            for (int i = 0; i < pixelCount; i++)
            {
                for (int k = 0; k < n; k++) column[k] = frames[k][i];
                Array.Sort(column);
                reference[i] = (n % 2 == 1)
                    ? column[n / 2]
                    : 0.5f * (column[n / 2 - 1] + column[n / 2]);
            }
            return reference;
        }

        /// <summary>
        /// The whole reduction: build the reference from the sequence, subtract it from every
        /// frame, derotate each by its own parallactic angle, and average.
        ///
        /// THE ORDER IS THE TECHNIQUE. Subtract BEFORE derotating, because the speckles are fixed
        /// in the instrument's frame and that is the frame they are common in; derotate AFTER,
        /// because the companion is fixed on the sky and that is the frame it adds up in. Doing
        /// either the other way round aligns the wrong thing and the method stops working.
        ///
        /// The angles are subtracted from the first frame's, so the output is aligned to the
        /// sequence's own starting orientation rather than to an absolute north the caller may not
        /// have. A caller who wants north up rotates once more, afterwards, by that first angle.
        /// </summary>
        public static float[] Reduce(IList<float[]> frames, IList<double> parallacticAnglesDeg,
                                     int width, int height)
        {
            int pixelCount = width * height;
            var stacked = new float[pixelCount];
            if (frames == null || frames.Count == 0 || parallacticAnglesDeg == null) return stacked;
            int n = Math.Min(frames.Count, parallacticAnglesDeg.Count);
            if (n == 0) return stacked;

            float[] reference = MedianReference(frames, pixelCount);

            var residual = new float[pixelCount];
            var accumulator = new double[pixelCount];
            double reference0 = parallacticAnglesDeg[0];

            for (int k = 0; k < n; k++)
            {
                for (int i = 0; i < pixelCount; i++) residual[i] = frames[k][i] - reference[i];

                float[] derotated = Rotate(residual, width, height,
                                           -(parallacticAnglesDeg[k] - reference0));
                for (int i = 0; i < pixelCount; i++) accumulator[i] += derotated[i];
            }

            for (int i = 0; i < pixelCount; i++) stacked[i] = (float)(accumulator[i] / n);
            return stacked;
        }

        /// <summary>
        /// How much of the static speckle pattern's amplitude survives the subtraction, given how
        /// long the sequence ran.
        ///
        /// A perfect reference would remove all of it. A real one cannot, because the pattern
        /// itself drifts: Milli et al. (2016) measure the quasi-static component decorrelating at
        /// 73 ppm/s once the temporal median is removed, so a frame at one end of a sequence is no
        /// longer a valid reference for a frame at the other. Over a duration t the correlation has
        /// fallen by rate*t, and the residual amplitude that leaves behind is sqrt(2(1 - rho)),
        /// which is the relation Milli et al. give in their own Appendix C between a correlation
        /// coefficient and the contrast of the difference of two frames.
        ///
        /// The consequence is the one every observer knows and few simulators show: a LONGER ADI
        /// sequence is not monotonically better. It buys field rotation, which helps, and it costs
        /// reference validity, which does not.
        /// </summary>
        public static double StaticResidualFraction(double sequenceDurationSeconds)
        {
            if (!(sequenceDurationSeconds > 0.0)) return 0.0;
            double lostCorrelation = SpeckleField.QuasiStaticDecorrelationPerSecond * sequenceDurationSeconds;
            if (lostCorrelation > 1.0) lostCorrelation = 1.0;
            return Math.Sqrt(2.0 * lostCorrelation);
        }
    }
}
