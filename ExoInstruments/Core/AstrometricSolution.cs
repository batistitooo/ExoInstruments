using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Finding out where the frame was really pointed, from the frame.
    ///
    /// THE OTHER HALF OF TURNING AN IMAGE INTO A MEASUREMENT. Core.AperturePhotometry answers HOW
    /// MUCH LIGHT; this answers WHERE, and the two together are what a scientific frame is for. A
    /// position with no error bar constrains nothing, an orbit is a sequence of positions, and a
    /// catalogue cross-match is a position compared with someone else's.
    ///
    /// WHAT WAS THERE BEFORE, AND WHY IT WAS NOT THIS. The pipeline already writes a World
    /// Coordinate System into every exported frame (Core.FitsWcs), built from where the telescope
    /// was COMMANDED to point and from the instrument's nominal plate scale. That is a
    /// description of the intent. This file produces the other kind: a WCS FITTED to where the
    /// stars actually landed, which is a description of the result. The difference between the two
    /// is the measurement - the pointing error, the plate-scale error, the rotation the instrument
    /// really had - and until now the pipeline could not express it because it only ever had one
    /// of them.
    ///
    /// A REFINEMENT, NOT A BLIND SOLVE, and the distinction is deliberate. Blind plate solving
    /// (astrometry.net and its quad hashes) exists to place a frame whose pointing is completely
    /// unknown. This pipeline always knows roughly where it pointed, so the problem here is the one
    /// an observatory actually has: take a good initial guess, match the detected sources to a
    /// catalogue, and fit the six parameters that say what the frame really did. Solving the harder
    /// problem would be solving a different one.
    ///
    /// THE FIT IS EXACTLY LINEAR, and that is why it is done in the tangent plane. Pixel position
    /// is an affine function of the standard coordinates (xi, eta) and a thoroughly non-linear
    /// function of (RA, Dec), so projecting first turns the solution into ordinary least squares
    /// with a closed form, no starting guess and no convergence to worry about. What iterates is
    /// only the OUTLIER REJECTION, because one mismatched pair will drag a least-squares fit
    /// anywhere.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class AstrometricSolution
    {
        /// <summary>
        /// One detected source paired with the catalogue position believed to be the same object.
        ///
        /// Pixel coordinates are in the FITS convention, centres on integers starting at 1. A
        /// centroid from Core.AperturePhotometry is an ARRAY index and needs +1; see
        /// FitsWcs.TrySkyToPixel for why that half-pixel family of conventions is written out at
        /// every boundary rather than assumed.
        /// </summary>
        public struct Match
        {
            public double PixelX, PixelY;
            public double RaDeg, DecDeg;
        }

        /// <summary>What a solved frame knows about itself that an unsolved one does not.</summary>
        public struct Result
        {
            public FitsWcs Wcs;

            /// <summary>Matches that survived rejection, and those that did not.</summary>
            public int Used, Rejected;

            /// <summary>Residual scatter about the fit, arcsec: total and per axis.</summary>
            public double RmsArcsec, RmsXArcsec, RmsYArcsec;

            /// <summary>Largest single residual, arcsec. A fit can have a good RMS and one star badly wrong, and that is worth seeing.</summary>
            public double WorstResidualArcsec;

            /// <summary>Plate scale the frame really has, arcsec per pixel, from the fitted CD matrix.</summary>
            public double PlateScaleXArcsecPerPixel, PlateScaleYArcsecPerPixel;

            /// <summary>Position angle of the +y axis, degrees east of north, and whether the frame is mirrored.</summary>
            public double RotationDeg;
            public bool FlippedParity;

            public bool IsValid;
        }

        /// <summary>
        /// Fits a tangent-plane WCS to matched sources.
        ///
        /// The tangent point is supplied rather than fitted, and it does not need to be right. A
        /// frame really taken on a TAN projection is exactly linear in the standard coordinates of
        /// ITS OWN tangent point; choosing a different one leaves a residual of order the cube of
        /// the field radius, which for a field of a tenth of a degree is under a milliarcsecond and
        /// far below anything else here. What the tangent point must be is INSIDE the field, and
        /// the commanded pointing always is.
        ///
        /// clipSigma rejects on residual, iterating until nothing more is rejected or the iteration
        /// budget runs out. Three sigma is the usual value and the usual reason: a mismatched pair
        /// is not a large residual, it is a residual from a different distribution, and least
        /// squares has no defence against one.
        /// </summary>
        public static Result Fit(IList<Match> matches, double tangentRaDeg, double tangentDecDeg,
                                 double clipSigma, int maxIterations)
        {
            var result = new Result { IsValid = false };
            if (matches == null || matches.Count < 3) return result;

            int n = matches.Count;
            var xi = new double[n];
            var eta = new double[n];
            var alive = new bool[n];
            int living = 0;

            for (int i = 0; i < n; i++)
            {
                alive[i] = FitsWcs.TryStandardCoordinates(
                    matches[i].RaDeg, matches[i].DecDeg, tangentRaDeg, tangentDecDeg,
                    out xi[i], out eta[i]);
                if (alive[i]) living++;
            }
            if (living < 3) return result;

            double a = 0, b = 0, c = 0, d = 0, e = 0, f = 0;

            for (int iteration = 0; iteration <= Math.Max(0, maxIterations); iteration++)
            {
                // Two independent three-parameter least-squares fits sharing one normal matrix:
                //     xi  = a x + b y + c
                //     eta = d x + e y + f
                double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, s1 = 0;
                double sxXi = 0, syXi = 0, sXi = 0, sxEta = 0, syEta = 0, sEta = 0;

                for (int i = 0; i < n; i++)
                {
                    if (!alive[i]) continue;
                    double x = matches[i].PixelX, y = matches[i].PixelY;
                    sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; s1 += 1.0;
                    sxXi += x * xi[i]; syXi += y * xi[i]; sXi += xi[i];
                    sxEta += x * eta[i]; syEta += y * eta[i]; sEta += eta[i];
                }
                if (s1 < 3.0) return result;

                if (!Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, s1, sxXi, syXi, sXi, out a, out b, out c)) return result;
                if (!Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, s1, sxEta, syEta, sEta, out d, out e, out f)) return result;

                // Residuals in the tangent plane, which is where they are homoscedastic: a residual
                // in degrees of sky is the same quantity everywhere in the frame, while a residual
                // in pixels is not once the plate scale differs between the axes.
                double sum2 = 0.0; int count = 0;
                var residual = new double[n];
                for (int i = 0; i < n; i++)
                {
                    if (!alive[i]) continue;
                    double x = matches[i].PixelX, y = matches[i].PixelY;
                    double rx = (a * x + b * y + c) - xi[i];
                    double ry = (d * x + e * y + f) - eta[i];
                    residual[i] = Math.Sqrt(rx * rx + ry * ry);
                    sum2 += rx * rx + ry * ry; count++;
                }
                if (count < 3) return result;
                double rms = Math.Sqrt(sum2 / count);

                if (iteration == maxIterations || !(clipSigma > 0.0) || !(rms > 0.0)) break;

                int rejectedThisPass = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!alive[i]) continue;
                    if (residual[i] > clipSigma * rms) { alive[i] = false; rejectedThisPass++; }
                }
                if (rejectedThisPass == 0) break;
                if (count - rejectedThisPass < 3) return result;
            }

            // The CD matrix is the linear part; the reference pixel is where the tangent plane's
            // origin falls, which is the pixel at which both fitted planes are zero.
            double detCd = a * e - b * d;
            if (Math.Abs(detCd) < 1e-30 || double.IsNaN(detCd)) return result;

            var wcs = new FitsWcs
            {
                Cd11 = a, Cd12 = b, Cd21 = d, Cd22 = e,
                ReferenceRaDeg = tangentRaDeg,
                ReferenceDecDeg = tangentDecDeg,
                ReferencePixelX = (-c * e + b * f) / detCd,
                ReferencePixelY = (-a * f + c * d) / detCd,
                IsValid = true,
            };

            // Final residuals, reported against the WCS that will actually be written rather than
            // against the intermediate fit, so what is quoted is what a user gets.
            double sx2 = 0.0, sy2 = 0.0, worst = 0.0; int used = 0, rejected = 0;
            for (int i = 0; i < n; i++)
            {
                if (!alive[i]) { rejected++; continue; }
                if (!wcs.TrySkyToPixel(matches[i].RaDeg, matches[i].DecDeg, out double px, out double py))
                { rejected++; continue; }

                double dxPix = px - matches[i].PixelX;
                double dyPix = py - matches[i].PixelY;
                double dxArcsec = dxPix * wcs.ScaleXArcsecPerPixel;
                double dyArcsec = dyPix * wcs.ScaleYArcsecPerPixel;

                sx2 += dxArcsec * dxArcsec;
                sy2 += dyArcsec * dyArcsec;
                worst = Math.Max(worst, Math.Sqrt(dxArcsec * dxArcsec + dyArcsec * dyArcsec));
                used++;
            }
            if (used < 3) return result;

            result.Wcs = wcs;
            result.Used = used;
            result.Rejected = rejected;
            result.RmsXArcsec = Math.Sqrt(sx2 / used);
            result.RmsYArcsec = Math.Sqrt(sy2 / used);
            result.RmsArcsec = Math.Sqrt((sx2 + sy2) / used);
            result.WorstResidualArcsec = worst;
            result.PlateScaleXArcsecPerPixel = wcs.ScaleXArcsecPerPixel;
            result.PlateScaleYArcsecPerPixel = wcs.ScaleYArcsecPerPixel;
            result.RotationDeg = wcs.RotationDeg;
            result.FlippedParity = wcs.FlippedParity;
            result.IsValid = true;
            return result;
        }

        /// <summary>
        /// Pairs detected sources with catalogue entries, using an initial WCS to say which is
        /// which.
        ///
        /// AMBIGUOUS PAIRS ARE DROPPED RATHER THAN GUESSED. When two catalogue entries fall within
        /// the tolerance of one detection there is no evidence to choose between them, and taking
        /// the nearer one is not a tie-break but a coin toss whose result enters a least-squares
        /// fit as though it were data. A crowded field solved from its unambiguous pairs alone is a
        /// smaller and better solution than one solved from all of them.
        ///
        /// detections are ARRAY coordinates as a centroid returns them; the +1 to FITS convention
        /// is applied here, once, so the returned matches are in the convention Fit expects.
        /// </summary>
        public static List<Match> MatchToCatalogue(
            IList<(double X, double Y)> detections,
            IList<(double RaDeg, double DecDeg)> catalogue,
            FitsWcs initialGuess, double toleranceArcsec, out int ambiguous)
        {
            var matched = new List<Match>();
            ambiguous = 0;
            if (detections == null || catalogue == null || !initialGuess.IsValid) return matched;

            double scale = 0.5 * (initialGuess.ScaleXArcsecPerPixel + initialGuess.ScaleYArcsecPerPixel);
            if (!(scale > 0.0)) return matched;
            double tolerancePx = toleranceArcsec / scale;
            double tol2 = tolerancePx * tolerancePx;

            // Every catalogue entry projected once, rather than once per detection.
            var projected = new List<(double X, double Y, int Index)>();
            for (int j = 0; j < catalogue.Count; j++)
            {
                if (initialGuess.TrySkyToPixel(catalogue[j].RaDeg, catalogue[j].DecDeg,
                                               out double cx, out double cy))
                    projected.Add((cx, cy, j));
            }

            for (int i = 0; i < detections.Count; i++)
            {
                double px = detections[i].X + 1.0;      // array index to FITS
                double py = detections[i].Y + 1.0;

                double best = double.MaxValue, second = double.MaxValue;
                int bestIndex = -1;
                foreach (var (cx, cy, index) in projected)
                {
                    double dx = cx - px, dy = cy - py;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < best) { second = best; best = d2; bestIndex = index; }
                    else if (d2 < second) { second = d2; }
                }

                if (bestIndex < 0 || best > tol2) continue;
                if (second <= tol2) { ambiguous++; continue; }

                matched.Add(new Match
                {
                    PixelX = px, PixelY = py,
                    RaDeg = catalogue[bestIndex].RaDeg,
                    DecDeg = catalogue[bestIndex].DecDeg,
                });
            }
            return matched;
        }

        /// <summary>
        /// How far the frame really pointed from where it was commanded to, in arcsec.
        ///
        /// The number the whole file exists to produce, and the one an observer checks first: it is
        /// the mount's pointing error for this frame, measured rather than assumed, and on a
        /// sequence it is what says whether the telescope drifted.
        /// </summary>
        public static double PointingErrorArcsec(FitsWcs solved, double commandedRaDeg, double commandedDecDeg,
                                                 double frameCentreFitsX, double frameCentreFitsY)
        {
            if (!solved.IsValid) return double.NaN;
            solved.PixelToSky(frameCentreFitsX, frameCentreFitsY, out double raDeg, out double decDeg);
            return SeparationArcsec(commandedRaDeg, commandedDecDeg, raDeg, decDeg);
        }

        /// <summary>
        /// Angular separation of two sky positions, in arcsec, by the haversine formula.
        ///
        /// NOT THE COSINE RULE, and the difference is the whole reason this method exists rather
        /// than one line at the call site. acos(sin d1 sin d2 + cos d1 cos d2 cos da) is exact in
        /// algebra and useless in floating point for exactly the separations this file cares about:
        /// near zero the cosine is 1 - theta^2/2, a double resolves it to about 1e-16, so theta is
        /// recovered only to sqrt(2e-16) = 1.4e-8 radians, which is 3 milliarcseconds. An
        /// astrometric solution good to a milliarcsecond measured with that formula reads 3 mas
        /// however good it really is, and a harness checking the solution against the truth hits
        /// the same floor and blames the solution.
        ///
        /// That is not hypothetical: it is how this was found. The first version used the cosine
        /// rule, and every pointing-error check in tools/photometry-tests bottomed out at
        /// 3.07e-3 arcsec, in three different sections, until the floor was recognised as the
        /// formula's rather than the fit's.
        ///
        /// The haversine puts the small quantity inside a sine instead, where relative precision is
        /// preserved all the way to zero.
        /// </summary>
        public static double SeparationArcsec(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg)
        {
            const double DegToRad = Math.PI / 180.0;
            double d1 = dec1Deg * DegToRad, d2 = dec2Deg * DegToRad;
            double halfDDec = 0.5 * (d2 - d1);
            double halfDRa = 0.5 * (ra2Deg - ra1Deg) * DegToRad;

            double sinDec = Math.Sin(halfDDec);
            double sinRa = Math.Sin(halfDRa);
            double h = sinDec * sinDec + Math.Cos(d1) * Math.Cos(d2) * sinRa * sinRa;
            h = Math.Max(0.0, Math.Min(1.0, h));
            return 2.0 * Math.Asin(Math.Sqrt(h)) / DegToRad * 3600.0;
        }

        /// <summary>Three-by-three symmetric solve by Cramer's rule, which at this size is exact, allocation-free and shorter than a decomposition.</summary>
        private static bool Solve3(double m11, double m12, double m13,
                                   double m21, double m22, double m23,
                                   double m31, double m32, double m33,
                                   double r1, double r2, double r3,
                                   out double x, out double y, out double z)
        {
            x = y = z = 0.0;
            double det = m11 * (m22 * m33 - m23 * m32)
                       - m12 * (m21 * m33 - m23 * m31)
                       + m13 * (m21 * m32 - m22 * m31);
            if (Math.Abs(det) < 1e-30 || double.IsNaN(det)) return false;

            x = (r1 * (m22 * m33 - m23 * m32)
               - m12 * (r2 * m33 - m23 * r3)
               + m13 * (r2 * m32 - m22 * r3)) / det;
            y = (m11 * (r2 * m33 - m23 * r3)
               - r1 * (m21 * m33 - m23 * m31)
               + m13 * (m21 * r3 - r2 * m31)) / det;
            z = (m11 * (m22 * r3 - r2 * m32)
               - m12 * (m21 * r3 - r2 * m31)
               + r1 * (m21 * m32 - m22 * m31)) / det;
            return true;
        }
    }
}
