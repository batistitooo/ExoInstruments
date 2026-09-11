using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Science for photographing a world, paid for what a photograph newly SHOWS.
    ///
    /// Two claims per body, and both are ratchets: they pay the difference between what you have
    /// just recorded and the best you had recorded before, so re-shooting the same frame pays
    /// exactly nothing and there is no rate to farm. That is the whole anti-exploit design, and it
    /// has to be, because the shutter is not a limit here: the RC20's minimum exposure is 32
    /// microseconds, so a frame costs one reduction pass and not two minutes of anybody's time.
    ///
    /// CLAIM A, DETAIL, counts resolution elements across the target and pays for doubling that
    /// count. It is clipped by the FIELD, because a detector cannot record detail it has no pixels
    /// for, and the clip is what makes a wide instrument and a narrow one differ honestly.
    ///
    /// CLAIM B, RECONNAISSANCE, is the ground sample distance: how many metres of the target one
    /// resolution element covers. The sensor does not cap that, which is why it is a separate claim:
    /// once a body overflows the field, Claim A saturates into a property of the camera alone and
    /// stops being able to tell one world from another. Claim B is the half that says you flew
    /// there.
    ///
    /// WHAT IS SOURCED AND WHAT IS CHOSEN, stated plainly because the rest of this mod is sourced.
    /// The floor of the detail ladder is: two resolution elements across a target is the point it
    /// stops being a dot, which is the same threshold ResolvedBodyMinDiameterPx already applies.
    /// The Nyquist floor on the resolution element is standard sampling theory. Everything above
    /// that is a game-balance choice: the factor-two rungs, the payout per rung, and the /500 in
    /// the reconnaissance threshold. None of them has a citation and none is dressed up as having
    /// one. They are measured against the tech tree in ScienceRewards.
    /// </summary>
    public static class ImagingScience
    {
        private const double ArcsecToRadians = Math.PI / (180.0 * 3600.0);

        /// <summary>
        /// How fine a detail this frame can actually hold, arcseconds: the blur the optics and the
        /// air and the spacecraft deliver, but never finer than the detector samples it.
        ///
        /// The Nyquist floor is what stops binning being a free multiplier. Binning divides the
        /// pixel count and multiplies the plate scale by the same factor, so on a sampling-limited
        /// instrument the two cancel and the ladder does not move; on a blur-limited one, binning
        /// genuinely loses nothing and the ladder says so.
        /// </summary>
        public static double ResolutionElementArcsec(double diffractionFwhmArcsec, double atmosphericFwhmArcsec,
                                                     double jitterFwhmArcsec, double plateScaleArcsecPerPixel,
                                                     double defocusDiscDiameterArcsec)
        {
            double d = Math.Max(0.0, diffractionFwhmArcsec);
            double a = Math.Max(0.0, atmosphericFwhmArcsec);
            double j = Math.Max(0.0, jitterFwhmArcsec);
            double blur = Math.Sqrt(d * d + a * a + j * j);

            double nyquist = 2.0 * Math.Max(0.0, plateScaleArcsecPerPixel);
            return Math.Max(Math.Max(blur, nyquist), Math.Max(0.0, defocusDiscDiameterArcsec));
        }

        /// <summary>
        /// Resolution elements recorded across the target: its own angular size, or the field,
        /// whichever is smaller, over the resolution element. A body larger than the field is
        /// recorded a field at a time, and that is what the frame contains.
        /// </summary>
        public static double ResolvedElements(double targetDiameterArcsec, double fieldWidthArcsec,
                                              double resolutionElementArcsec)
        {
            if (!(resolutionElementArcsec > 0.0) || !(targetDiameterArcsec > 0.0)) return 0.0;
            double recorded = fieldWidthArcsec > 0.0
                ? Math.Min(targetDiameterArcsec, fieldWidthArcsec)
                : targetDiameterArcsec;
            return recorded / resolutionElementArcsec;
        }

        /// <summary>
        /// The rung this frame reaches, or zero for a target it did not resolve. Each rung is a
        /// doubling of the element count, starting from the two elements that make a disc rather
        /// than a dot.
        /// </summary>
        public static int DetailRung(double targetDiameterArcsec, double fieldWidthArcsec,
                                     double resolutionElementArcsec, int rungCap)
        {
            double elements = ResolvedElements(targetDiameterArcsec, fieldWidthArcsec, resolutionElementArcsec);
            if (!(elements >= 2.0)) return 0;

            int rung = (int)Math.Floor(Math.Log(elements, 2.0));
            if (rung < 1) rung = 1;
            return rung > rungCap ? rungCap : rung;
        }

        /// <summary>
        /// Everything a body is worth up to and including a rung, so an award is the difference
        /// between two of these and a re-shoot is worth their difference of zero.
        ///
        /// Closed form rather than a loop: the rungs are a geometric series whose ratio is set by
        /// how many of them make a doubling of the payout. With three rungs to a doubling, an
        /// eightfold gain in recorded detail is worth twice what the rung before it was.
        /// </summary>
        public static double LadderTotal(int rung, double perRung, int rungsPerDoubling)
        {
            if (rung <= 0 || !(perRung > 0.0) || rungsPerDoubling <= 0) return 0.0;
            double ratio = Math.Pow(2.0, 1.0 / rungsPerDoubling);
            return perRung * (Math.Pow(2.0, (double)rung / rungsPerDoubling) - 1.0) / (ratio - 1.0);
        }

        /// <summary>What one resolution element covers on the target, metres.</summary>
        public static double GroundSampleMetres(double distanceMetres, double resolutionElementArcsec)
        {
            if (!(distanceMetres > 0.0) || !(resolutionElementArcsec > 0.0)) return double.PositiveInfinity;
            return distanceMetres * resolutionElementArcsec * ArcsecToRadians;
        }

        /// <summary>
        /// Ground sample a reconnaissance claim asks for: the body across five hundred elements.
        /// A chosen number, and the one lever that decides how hard the flying half is.
        /// </summary>
        public static double ReconnaissanceThresholdMetres(double bodyRadiusMetres, double elementsAcrossRadius)
        {
            if (!(bodyRadiusMetres > 0.0) || !(elementsAcrossRadius > 0.0)) return 0.0;
            return bodyRadiusMetres / elementsAcrossRadius;
        }
    }
}
