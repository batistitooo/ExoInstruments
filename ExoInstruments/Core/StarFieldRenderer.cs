using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// A point source to be drawn into the frame: how much light it delivers, and where the
    /// sensor sees it at the start and end of the exposure.
    /// </summary>
    public struct PointSource
    {
        /// <summary>Signal in ELECTRONS, the unit the imaging pipeline works in end to end.</summary>
        public double SignalElectrons;
        public double StartPixelX, StartPixelY;
        public double EndPixelX, EndPixelY;
    }

    /// <summary>
    /// Draws unresolved sources, meaning catalogue stars and any solar-system body too small for
    /// the renderer to resolve, onto the frame's signal plane.
    ///
    /// This is the piece that was missing, and its absence is why the sky came out black. The
    /// old pipeline built a frame by taking one rendered image and scaling it so its total
    /// matched the target body's computed electron count. Under that scheme nothing except the
    /// target can have a correct brightness, because there is only one scale factor and it
    /// belongs to the target: a star drawn into the render would be scaled by the planet's
    /// budget rather than its own. Building the frame as a SUM OF SOURCES instead, each carrying
    /// its own independently computed flux, is how every serious image simulator works: GalSim
    /// (Rowe et al. 2015, Astron. Comput. 10, 121), SkyMaker (Bertin 2009), ESA's own Pyxel.
    /// It is what lets a star, a moon and a planet coexist in one frame on one flux scale.
    ///
    /// Sources are deposited as bare, sub-pixel-positioned delta functions. They are NOT given
    /// a profile here: the instrument's real point-spread function is convolved over the whole
    /// signal plane afterwards, in one pass, so giving each source a shape first would blur
    /// everything twice. Depositing a delta and convolving is exactly equivalent to drawing the
    /// PSF at each source position, for a fraction of the cost.
    ///
    /// Pure C# with no Unity dependency, so it runs on the background imaging thread.
    /// </summary>
    public static class StarFieldRenderer
    {
        /// <summary>
        /// Faintest star worth drawing, expressed as a fraction of the frame's own noise floor.
        /// Below this a source is far under the read+shot noise it lands in and cannot be
        /// distinguished from it, and drawing it costs time and changes no pixel a viewer or a
        /// stacking pass could ever recover. Set well below 1 so nothing marginally detectable
        /// is discarded.
        /// </summary>
        public const double NoiseFloorCutoffFraction = 0.05;

        /// <summary>
        /// Deposits one source, spread along its trail, into the plane. Flux-conserving: the
        /// source's whole signal lands on the plane unless the trail runs off the sensor.
        ///
        /// Sub-pixel position is honoured by bilinear splitting across the four pixels the
        /// source falls between. That is the discrete equivalent of integrating a delta function
        /// over each pixel's own area, and it matters: rounding every source to the nearest
        /// pixel centre lays the whole star field on a lattice, which is plainly visible as soon
        /// as a frame holds more than a handful of stars.
        /// </summary>
        public static void Deposit(float[] plane, int width, int height, PointSource source)
        {
            if (plane == null || source.SignalElectrons <= 0.0) return;

            double dx = source.EndPixelX - source.StartPixelX;
            double dy = source.EndPixelY - source.StartPixelY;
            double trailLengthPx = Math.Sqrt(dx * dx + dy * dy);

            // One sample per pixel of trail, so a trailed star lays down a continuous streak
            // rather than a dotted line. A stationary source is a single sample.
            int samples = (int)Math.Ceiling(trailLengthPx) + 1;
            if (samples > MaxTrailSamples) samples = MaxTrailSamples;
            double perSample = source.SignalElectrons / samples;

            for (int s = 0; s < samples; s++)
            {
                double t = samples > 1 ? (double)s / (samples - 1) : 0.0;
                Splat(plane, width, height,
                      source.StartPixelX + dx * t,
                      source.StartPixelY + dy * t,
                      perSample);
            }
        }

        // Cap on how finely a trail is sampled. A source trailed across the whole sensor is already a streak;
        // sampling it more finely than this changes nothing visible and only costs time on frames with
        // thousands of stars.
        private const int MaxTrailSamples = 512;

        // Bilinear deposit at a continuous position. Pixel i spans [i, i+1) and is centred at i+0.5, so the
        // weights are taken about the centres.
        private static void Splat(float[] plane, int width, int height, double px, double py, double amount)
        {
            double fx = px - 0.5, fy = py - 0.5;
            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            double wx = fx - ix, wy = fy - iy;

            AddPixel(plane, width, height, ix, iy, amount * (1.0 - wx) * (1.0 - wy));
            AddPixel(plane, width, height, ix + 1, iy, amount * wx * (1.0 - wy));
            AddPixel(plane, width, height, ix, iy + 1, amount * (1.0 - wx) * wy);
            AddPixel(plane, width, height, ix + 1, iy + 1, amount * wx * wy);
        }

        private static void AddPixel(float[] plane, int width, int height, int x, int y, double amount)
        {
            if (x < 0 || x >= width || y < 0 || y >= height || amount <= 0.0) return;
            plane[y * width + x] += (float)amount;
        }

        /// <summary>
        /// Turns a catalogue search result into deposited signal.
        ///
        /// Each star is projected twice, at the start and at the end of the exposure, through
        /// the SAME fixed sensor geometry but against a sky that has rotated in between. That is
        /// physically what an unguided mount does, holding still while the sky turns under it,
        /// and it produces the real trail for free, including two things a single "smear the
        /// image sideways" step cannot reproduce: trails that curve, and field rotation, which
        /// makes stars near the frame edge trail further than stars near its centre.
        ///
        /// electronsFor converts a star's catalogue magnitude and colour into electrons; it is
        /// supplied by the caller so this stays free of any instrument knowledge.
        /// </summary>
        public static int DepositStars(
            float[] plane, int width, int height,
            List<RenderedStar> stars,
            GnomonicProjection projection,
            double startMeridianRaDeg, double endMeridianRaDeg,
            double observerLatitudeDeg,
            double signalCutoffElectrons,
            Func<RenderedStar, double> electronsFor)
        {
            if (plane == null || stars == null) return 0;

            int drawn = 0;
            for (int i = 0; i < stars.Count; i++)
            {
                RenderedStar star = stars[i];

                double signal = star.FixedElectrons > 0.0 ? star.FixedElectrons : electronsFor(star);
                if (signal <= signalCutoffElectrons) continue;

                HorizontalCoordinates startAltAz = SkyCoordinates.EquatorialToHorizontal(
                    star.RaDeg, star.DecDeg, startMeridianRaDeg, observerLatitudeDeg);
                if (!projection.TryProject(SkyVector.FromHorizontal(startAltAz.AltitudeDeg, startAltAz.AzimuthDeg),
                                           out double sx, out double sy))
                    continue;

                double ex = sx, ey = sy;
                if (endMeridianRaDeg != startMeridianRaDeg)
                {
                    HorizontalCoordinates endAltAz = SkyCoordinates.EquatorialToHorizontal(
                        star.RaDeg, star.DecDeg, endMeridianRaDeg, observerLatitudeDeg);
                    if (!projection.TryProject(SkyVector.FromHorizontal(endAltAz.AltitudeDeg, endAltAz.AzimuthDeg),
                                               out ex, out ey))
                    {
                        ex = sx; ey = sy;
                    }
                }

                // Cheap reject for a source whose whole trail is off the sensor. The margin is
                // what keeps a star that TRAILS onto the sensor from being thrown away before
                // its streak is drawn; Deposit clips per sample, so only the part that actually
                // lands on the sensor is recorded. A star wholly outside the frame contributes
                // nothing, which drops the far PSF wing it would really cast onto the edge; that
                // is negligible, since the atmospheric profile falls as theta^(-11/3) out there.
                if (Math.Max(sx, ex) < -OffSensorMarginPx || Math.Min(sx, ex) > width + OffSensorMarginPx) continue;
                if (Math.Max(sy, ey) < -OffSensorMarginPx || Math.Min(sy, ey) > height + OffSensorMarginPx) continue;

                Deposit(plane, width, height, new PointSource
                {
                    SignalElectrons = signal,
                    StartPixelX = sx,
                    StartPixelY = sy,
                    EndPixelX = ex,
                    EndPixelY = ey,
                });
                drawn++;
            }
            return drawn;
        }

        // How far outside the sensor a source is still deposited, so its PSF wings can spill inward. Matches
        // OpticalPsf's own maximum kernel radius.
        private const int OffSensorMarginPx = 48;
    }
}
