using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Turning a frame back into magnitudes, which is the step that makes an image a measurement.
    ///
    /// WHY THIS BELONGS IN THE SIMULATOR AND NOT ONLY IN THE USER'S OWN PIPELINE. Everything before
    /// this file is a FORWARD model: a magnitude goes in, a frame comes out. That direction can be
    /// wrong in ways nothing catches, because a forward model is only ever checked against itself.
    /// Running the inverse closes the loop: put a star of known magnitude in, reduce the frame the
    /// way an observer would, and see whether the magnitude comes back. If it does not, one of the
    /// zero point, the bandpass, the point-spread function's normalisation, the gain or the
    /// detector chain is wrong, and no amount of staring at the forward code will say which.
    ///
    /// It is also what a contrast curve, a light curve and a colour all rest on, and what makes an
    /// exported frame a scientific product rather than a picture.
    ///
    /// THE UNCERTAINTY IS THE POINT, not the flux. A measured flux without an error bar cannot be
    /// compared with anything, and the error bar is not free: it is the CCD equation applied to the
    /// pixels this particular aperture actually summed, including the noise on the background
    /// estimate itself, which is the term most implementations forget. This file carries it
    /// explicitly and the harness checks it the only way it can be checked, by measuring the same
    /// source many times and comparing the scatter with the predicted sigma.
    ///
    /// Pure C#, no Unity dependency. Verified against photutils in tools/photometry-tests.
    /// </summary>
    public static class AperturePhotometry
    {
        /// <summary>One measured source.</summary>
        public struct Source
        {
            public double X, Y;                  // centroid, pixels
            public double Flux;                  // background-subtracted aperture sum, electrons
            public double FluxUncertainty;       // electrons, from the CCD equation on this aperture
            public double Background;            // per-pixel background level, electrons
            public double BackgroundRms;         // per-pixel scatter of the background, electrons
            public int AperturePixels;
            public int AnnulusPixels;
            public double InstrumentalMagnitude; // -2.5 log10(flux), no zero point applied
            public double MagnitudeUncertainty;
            public bool Saturated;
        }

        /// <summary>
        /// Finds sources as local maxima standing a given number of background sigmas above the
        /// background, with a minimum separation.
        ///
        /// Deliberately the simplest detection that works, because detection is not what this file
        /// is for: a frame whose sources are already known by construction needs finding, not
        /// deblending, and anything cleverer would be a second algorithm to validate. The minimum
        /// separation is what stops one point-spread function being reported as several sources,
        /// and one resolution element is the right value for it.
        /// </summary>
        public static List<(int X, int Y)> FindSources(
            float[] frame, int width, int height,
            double background, double backgroundRms, double thresholdSigma, int minSeparationPx)
        {
            var found = new List<(int X, int Y)>();
            if (frame == null || width <= 0 || height <= 0) return found;
            if (!(backgroundRms > 0.0)) return found;

            double threshold = background + thresholdSigma * backgroundRms;
            int sep = Math.Max(1, minSeparationPx);

            for (int y = sep; y < height - sep; y++)
            {
                for (int x = sep; x < width - sep; x++)
                {
                    float v = frame[y * width + x];
                    if (v < threshold) continue;

                    bool isPeak = true;
                    for (int dy = -sep; dy <= sep && isPeak; dy++)
                        for (int dx = -sep; dx <= sep; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            if (frame[(y + dy) * width + (x + dx)] > v) { isPeak = false; break; }
                        }
                    if (isPeak) found.Add((x, y));
                }
            }
            return found;
        }

        /// <summary>
        /// The frame's background level and its scatter, by iterative sigma clipping.
        ///
        /// Clipped rather than taken as a plain median, because a frame with sources in it has a
        /// one-sided contaminant: stars only ever add. Three iterations at three sigma is the
        /// standard recipe and converges on any frame whose sources cover less than half of it.
        /// </summary>
        public static void EstimateBackground(float[] frame, int count, out double level, out double rms)
        {
            level = 0.0; rms = 0.0;
            if (frame == null || count <= 1) return;

            var values = new double[count];
            for (int i = 0; i < count; i++) values[i] = frame[i];
            Array.Sort(values);

            double lo = values[0], hi = values[count - 1];
            for (int iteration = 0; iteration < 3; iteration++)
            {
                double sum = 0.0; int n = 0;
                for (int i = 0; i < count; i++)
                {
                    if (values[i] < lo || values[i] > hi) continue;
                    sum += values[i]; n++;
                }
                if (n < 2) break;
                double mean = sum / n;

                double var = 0.0;
                for (int i = 0; i < count; i++)
                {
                    if (values[i] < lo || values[i] > hi) continue;
                    double d = values[i] - mean; var += d * d;
                }
                var /= (n - 1);
                double sigma = Math.Sqrt(var);

                level = mean; rms = sigma;
                lo = mean - 3.0 * sigma; hi = mean + 3.0 * sigma;
            }
        }

        /// <summary>
        /// Measures one source: centroid, aperture sum, local background from an annulus, and the
        /// uncertainty on the flux.
        ///
        /// THE UNCERTAINTY, written out because its last term is the one that is usually missing:
        ///
        ///     sigma^2 = F + n_ap * (B + sigma_read^2 + sigma_bkg^2) + (n_ap^2 / n_ann) * sigma_bkg^2
        ///
        /// F is the source's own shot noise; the middle group is what the aperture's own pixels
        /// contribute from sky, read noise and dark; and the last term is the noise on the
        /// BACKGROUND ESTIMATE, which is subtracted from every aperture pixel and therefore enters
        /// n_ap times, reduced by however many annulus pixels went into estimating it. Leaving it
        /// out understates the error on a faint source measured against a small annulus, which is
        /// exactly the regime where an error bar matters. This is the standard form given by
        /// Merline and Howell (1995) and by Howell's "Handbook of CCD Astronomy".
        ///
        /// Everything is in ELECTRONS, so the caller multiplies ADU by the conversion factor first;
        /// the CCD equation is a statement about counted charges and only holds in those units.
        /// </summary>
        public static Source Measure(
            float[] frame, int width, int height, double centreX, double centreY,
            double apertureRadiusPx, double innerAnnulusPx, double outerAnnulusPx,
            double readNoiseElectrons, double saturationElectrons)
        {
            var s = new Source { X = centreX, Y = centreY };
            if (frame == null) return s;

            // Local background from the annulus, sigma-clipped for the neighbour that always
            // wanders into it.
            var annulus = new List<double>();
            int r0 = (int)Math.Floor(centreY - outerAnnulusPx), r1 = (int)Math.Ceiling(centreY + outerAnnulusPx);
            int c0 = (int)Math.Floor(centreX - outerAnnulusPx), c1 = (int)Math.Ceiling(centreX + outerAnnulusPx);
            for (int y = Math.Max(0, r0); y <= Math.Min(height - 1, r1); y++)
            {
                double dy = y - centreY;
                for (int x = Math.Max(0, c0); x <= Math.Min(width - 1, c1); x++)
                {
                    double dx = x - centreX;
                    double d2 = dx * dx + dy * dy;
                    if (d2 >= innerAnnulusPx * innerAnnulusPx && d2 <= outerAnnulusPx * outerAnnulusPx)
                        annulus.Add(frame[y * width + x]);
                }
            }
            if (annulus.Count > 1)
            {
                var arr = annulus.ToArray();
                var f = new float[arr.Length];
                for (int i = 0; i < arr.Length; i++) f[i] = (float)arr[i];
                double lvl, rms;
                EstimateBackground(f, f.Length, out lvl, out rms);
                s.Background = lvl; s.BackgroundRms = rms;
            }
            s.AnnulusPixels = annulus.Count;

            // The centroid, from the background-subtracted first moment inside the aperture. Taken
            // before the flux so the aperture is centred on the source rather than on the guess.
            double sumW = 0.0, sumX = 0.0, sumY = 0.0;
            int ar0 = (int)Math.Floor(centreY - apertureRadiusPx), ar1 = (int)Math.Ceiling(centreY + apertureRadiusPx);
            int ac0 = (int)Math.Floor(centreX - apertureRadiusPx), ac1 = (int)Math.Ceiling(centreX + apertureRadiusPx);
            for (int y = Math.Max(0, ar0); y <= Math.Min(height - 1, ar1); y++)
            {
                double dy = y - centreY;
                for (int x = Math.Max(0, ac0); x <= Math.Min(width - 1, ac1); x++)
                {
                    double dx = x - centreX;
                    if (dx * dx + dy * dy > apertureRadiusPx * apertureRadiusPx) continue;
                    double w = frame[y * width + x] - s.Background;
                    if (w <= 0.0) continue;
                    sumW += w; sumX += w * x; sumY += w * y;
                }
            }
            if (sumW > 0.0) { s.X = sumX / sumW; s.Y = sumY / sumW; }

            // The flux, on the refined centre.
            double flux = 0.0; int nAp = 0; bool saturated = false;
            ar0 = (int)Math.Floor(s.Y - apertureRadiusPx); ar1 = (int)Math.Ceiling(s.Y + apertureRadiusPx);
            ac0 = (int)Math.Floor(s.X - apertureRadiusPx); ac1 = (int)Math.Ceiling(s.X + apertureRadiusPx);
            for (int y = Math.Max(0, ar0); y <= Math.Min(height - 1, ar1); y++)
            {
                double dy = y - s.Y;
                for (int x = Math.Max(0, ac0); x <= Math.Min(width - 1, ac1); x++)
                {
                    double dx = x - s.X;
                    if (dx * dx + dy * dy > apertureRadiusPx * apertureRadiusPx) continue;
                    double v = frame[y * width + x];
                    if (saturationElectrons > 0.0 && v >= saturationElectrons) saturated = true;
                    flux += v - s.Background;
                    nAp++;
                }
            }
            s.Flux = flux;
            s.AperturePixels = nAp;
            s.Saturated = saturated;

            // The CCD equation on the pixels this aperture actually summed.
            double backgroundVariancePerPixel = Math.Max(0.0, s.BackgroundRms * s.BackgroundRms);
            double variance = Math.Max(0.0, flux)
                            + nAp * backgroundVariancePerPixel
                            + nAp * readNoiseElectrons * readNoiseElectrons;
            if (s.AnnulusPixels > 0)
                variance += (double)nAp * nAp / s.AnnulusPixels * backgroundVariancePerPixel;
            s.FluxUncertainty = Math.Sqrt(variance);

            if (flux > 0.0)
            {
                s.InstrumentalMagnitude = -2.5 * Math.Log10(flux);
                // d(-2.5 log10 F) = -2.5/ln10 * dF/F, and 2.5/ln10 = 1.0857.
                s.MagnitudeUncertainty = 2.5 / Math.Log(10.0) * s.FluxUncertainty / flux;
            }
            else
            {
                s.InstrumentalMagnitude = double.NaN;
                s.MagnitudeUncertainty = double.NaN;
            }

            return s;
        }

        /// <summary>
        /// Fits a photometric zero point from sources of known magnitude: the constant that turns
        /// an instrumental magnitude into a calibrated one.
        ///
        /// Weighted by each star's own uncertainty, because a zero point fitted with equal weights
        /// is dominated by whichever faint star happened to be included. Returns the weighted mean
        /// offset and its own standard error, which is what a calibrated magnitude's error bar has
        /// to be added to; a zero point without an error is a systematic waiting to be discovered.
        ///
        /// This is deliberately the mean rather than a colour-dependent fit. A real zero point
        /// carries a colour term, because a filter's effective wavelength depends on the spectrum
        /// behind it; that term needs standards of known colour and is not modelled here (see
        /// section 12).
        /// </summary>
        public static void FitZeroPoint(
            IList<double> instrumentalMagnitudes, IList<double> knownMagnitudes,
            IList<double> uncertainties, out double zeroPoint, out double zeroPointError, out int used)
        {
            zeroPoint = double.NaN; zeroPointError = double.NaN; used = 0;
            if (instrumentalMagnitudes == null || knownMagnitudes == null) return;
            int n = Math.Min(instrumentalMagnitudes.Count, knownMagnitudes.Count);

            double sumW = 0.0, sumWX = 0.0;
            for (int i = 0; i < n; i++)
            {
                double instrumental = instrumentalMagnitudes[i], known = knownMagnitudes[i];
                if (double.IsNaN(instrumental) || double.IsNaN(known)) continue;
                double sigma = (uncertainties != null && i < uncertainties.Count) ? uncertainties[i] : 1.0;
                if (!(sigma > 0.0) || double.IsNaN(sigma)) continue;

                double w = 1.0 / (sigma * sigma);
                sumW += w; sumWX += w * (known - instrumental); used++;
            }
            if (used == 0 || !(sumW > 0.0)) return;

            zeroPoint = sumWX / sumW;
            zeroPointError = 1.0 / Math.Sqrt(sumW);
        }

        /// <summary>The calibrated magnitude, and its error with the zero point's own folded in.</summary>
        public static void Calibrate(Source source, double zeroPoint, double zeroPointError,
                                     out double magnitude, out double magnitudeError)
        {
            magnitude = source.InstrumentalMagnitude + zeroPoint;
            magnitudeError = Math.Sqrt(source.MagnitudeUncertainty * source.MagnitudeUncertainty
                                     + zeroPointError * zeroPointError);
        }
    }
}
