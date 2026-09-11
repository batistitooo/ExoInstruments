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
    /// CLAIM A, DETAIL, counts resolution elements across the target, AND ONLY WHEN THE TARGET
    /// FITS IN THE FRAME. Clipping to the field instead was the first version and it was wrong
    /// twice over: the count collapsed to sensor width over twice the plate scale, a property of
    /// the camera with no body, range or aperture in it, so a telescope in low orbit scored the
    /// ladder's ceiling on a featureless patch of surface, and every body over the field width
    /// scored identically. Requiring the whole disc is the honest reading of "photographed it", and
    /// it gives each instrument a range band: Hubble's 0.045 degree field cannot frame the Mun from
    /// anywhere, and LORRI's 0.29 degree field frames Jool from its sphere of influence boundary
    /// almost exactly, which is the mission New Horizons actually flew.
    ///
    /// CLAIM B, GROUND SAMPLE, is metres of target per resolution element, against a fixed
    /// reference rather than against the body's own size. Against the body's size it was purely
    /// angular: distance times element under radius over five hundred cancels the range out
    /// completely, so an 8 metre telescope on the ground claimed Jool without anything flying. In
    /// absolute metres the range cannot cancel, and it is a ratchet rather than a threshold so
    /// getting closer keeps paying. This is the half that says you flew there.
    ///
    /// WHAT IS SOURCED AND WHAT IS CHOSEN, stated plainly because the rest of this mod is sourced.
    /// The floor of the detail ladder is: two resolution elements across a target is the point it
    /// stops being a dot, which is the same threshold ResolvedBodyMinDiameterPx already applies.
    /// The Nyquist floor on the resolution element is standard sampling theory. Everything above
    /// that is a game-balance choice: the factor-two rungs, the payout per rung, and the metre
    /// reference the ground sample is counted down from. None of them has a citation and none is
    /// dressed up as having one. They are measured against the tech tree in ScienceRewards.
    /// </summary>
    public static class ImagingScience
    {
        private const double ArcsecToRadians = Math.PI / (180.0 * 3600.0);

        /// <summary>
        /// How fine a detail this frame can actually hold, arcseconds: the blur the optics and the
        /// air and the spacecraft deliver, but never finer than the detector samples it.
        ///
        /// The Nyquist floor is why binning matters here and is not an exploit. The field in
        /// arcseconds does not move with binning; the element does. So binning up coarsens the
        /// element and costs rungs on a sampling-limited instrument, binning back down recovers
        /// them, and on a blur-limited one neither direction changes anything. Unbinning does pay,
        /// and it should: it is a genuinely better photograph, bought with memory and reduction
        /// time. An earlier comment here claimed the floor made binning inert in both directions,
        /// which was simply false in the direction a player actually clicks.
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

        /// <summary>Whether the whole target fits in the frame. Nothing is paid for detail unless it does.</summary>
        public static bool Frames(double targetDiameterArcsec, double fieldWidthArcsec)
        {
            return targetDiameterArcsec > 0.0 && fieldWidthArcsec > 0.0
                && targetDiameterArcsec <= fieldWidthArcsec;
        }

        /// <summary>
        /// Resolution elements across the target, and zero unless the whole of it is in the frame.
        /// A body that overflows the field is not photographed, it is skimmed.
        /// </summary>
        public static double ResolvedElements(double targetDiameterArcsec, double fieldWidthArcsec,
                                              double resolutionElementArcsec)
        {
            if (!(resolutionElementArcsec > 0.0)) return 0.0;
            if (!Frames(targetDiameterArcsec, fieldWidthArcsec)) return 0.0;
            return targetDiameterArcsec / resolutionElementArcsec;
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
        /// The ground-sample rung a frame reaches: how many halvings below the reference its metres
        /// per element sit. Absolute, so the range cannot cancel out of it the way it did against
        /// the body's own radius, and a ratchet, so closing on a world keeps paying.
        /// </summary>
        public static int GroundSampleRung(double groundSampleMetres, double referenceMetres, int rungCap)
        {
            if (!(groundSampleMetres > 0.0) || !(referenceMetres > 0.0)) return 0;
            if (groundSampleMetres >= referenceMetres) return 0;

            int rung = (int)Math.Floor(Math.Log(referenceMetres / groundSampleMetres, 2.0));
            if (rung < 1) rung = 1;
            return rung > rungCap ? rungCap : rung;
        }
    }
}
