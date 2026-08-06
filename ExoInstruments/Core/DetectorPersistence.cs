using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Why a bright star can still be in the frame after it has left the field, and why this is the
    /// first effect in the pipeline that needs the detector to remember the previous exposure.
    ///
    /// THE MECHANISM. Not every electron a bright pixel collects leaves with the readout. Under
    /// some clocking conditions charge reaches the silicon-silicon-oxide interface, where the
    /// lattice terminates and leaves states in the band gap, and is held there. Those states then
    /// empty thermally, on a timescale set by their depth rather than by the readout, so the charge
    /// returns to the well over the exposures that follow and is read out again as a ghost of
    /// whatever was bright before. This is the RESIDUAL SURFACE IMAGE (RSI).
    ///
    /// It is distinct from the residual BULK image (RBI), where charge is held in the field-free
    /// region of a thick device instead of at the surface, and which is the effect that makes
    /// infrared arrays and deep-depletion CCDs need a light flush between exposures. The two decay
    /// differently and are cured differently. Only RSI is modelled here, following the one modern
    /// quantitative characterisation of a scientific CCD's residual images
    /// ("Characterization of Residual Charge Images in LSST Camera e2v CCDs", arXiv:2502.05418),
    /// which reports RSI and states that RBI is not seen in that camera.
    ///
    /// WHY IT IS NOT COSMETIC, and why it does not belong with the effects a calibration frame
    /// removes. Persistence is the only term in this chain that depends on WHAT WAS OBSERVED
    /// BEFORE. A bias, a dark and a flat are each properties of the detector and the exposure; a
    /// residual image is a property of the observing SEQUENCE. Two identical exposures of the same
    /// field differ if one of them followed a bright star and the other did not, so no master frame
    /// can carry the correction and the standard calibration set cannot touch it. The observer's
    /// remedy is scheduling, which is why every handbook that documents it documents it as an
    /// avoidance constraint rather than as a correction.
    ///
    /// THE FORM, and why each part of it is the shape the measurements have rather than a choice.
    ///
    ///   * CAPTURE IS THRESHOLDED, not proportional. Every published measurement reports residual
    ///     images following SATURATED or near-saturated sources and reports nothing below that.
    ///     The WFPC2 Instrument Handbook (Section 4.5) states the effect is a concern "only for
    ///     stars that were saturated in a previous image"; the LSSTCam characterisation finds the
    ///     residual concentrated where the charge packet is largest and closest to the oxide. A
    ///     proportional model would put a ghost under every source in the frame, which is not what
    ///     any of them measure.
    ///
    ///   * CAPTURE SATURATES. The interface has a finite density of states, so the trapped charge
    ///     cannot grow without bound however overexposed the pixel is. This is what the WFPC2
    ///     handbook's behaviour after a 100x-full-well overexposure requires: the residual there is
    ///     not a hundred times the residual after a 1x saturation.
    ///
    ///   * RELEASE IS A SUM OF TWO EXPONENTIALS, carried as TWO SEPARATE TRAP POPULATIONS rather
    ///     than as one population with a two-term decay law. arXiv:2502.05418 fits the decay as a
    ///     sum of two exponentials; two exponentials is what two trap depths look like, and holding
    ///     them apart makes the release exact under an arbitrary sequence of exposure times instead
    ///     of correct only for the uniform cadence the fit was measured at. A single population
    ///     with a two-term law has no defined state after a partial decay; two populations do.
    ///
    ///   * RELEASE IS IN ELAPSED TIME, NOT IN FRAMES. The states empty thermally, so what empties
    ///     them is the clock, not the shutter. The same paper reports the residual taking "well
    ///     over a hundred seconds (around ten 15s exposures)" to dissipate, and the timescale is
    ///     stated in seconds with the frame count as a gloss on it. A frame-counted model would
    ///     make a residual survive a long dark and vanish in a fast sequence, which is backwards.
    ///
    /// WHAT IS PUBLISHED FOR THIS ROSTER, which is why every instrument here has the effect off.
    /// This is the same situation as brighter-fatter (see Core.BrighterFatter) and it is recorded
    /// the same way: the mechanism is modelled and waiting for a number.
    ///
    ///   * WFC3/UVIS is the one instrument here whose detector has been TESTED FOR THIS AND FOUND
    ///     NOT TO SHOW IT. ISR WFC3 2005-10 ("WFC3 UVIS PSF Evaluation") obtained dark images
    ///     following highly saturated PSF images specifically to evaluate image persistence in the
    ///     CCDs, and found no significant image persistence, consistent with previous ambient
    ///     testing. That is a MEASURED ABSENCE and it is a different fact from an unknown, so it is
    ///     carried as its own flag (VisualTelescopeSpec.PersistenceMeasuredAbsent) rather than as
    ///     another NaN. The report states the null qualitatively and gives no numerical upper
    ///     limit, so none is recorded here. Note that HST's well-documented persistence is WFC3/IR's
    ///     HgCdTe array, a different detector technology on a different channel, and none of that
    ///     literature transfers to the UVIS CCDs this pipeline carries.
    ///
    ///   * The IMX492 in the three amateur tubes is a pinned-photodiode CMOS, an architecture whose
    ///     defining advantage is that the photodiode empties completely into the sense node, so its
    ///     equivalent effect (image lag) is small by construction: a scientific CMOS measured in
    ///     X-ray shows lag confined to the immediately following frame and below 0.05%
    ///     (Nucl. Instrum. Methods A 1050, 168155, 2023). Neither Sony nor ZWO publish a lag figure
    ///     for this device, and a generic architectural expectation is not a measurement, so it is
    ///     NaN.
    ///
    ///   * FORS2's MIT/LL CCID-20 and SPHERE/ZIMPOL's CCDs have no published residual-image figure.
    ///     Checked against the FORS2 user manual and Schmid et al. (2018), the same two sources the
    ///     PRNU entry was checked against.
    ///
    /// WHAT IS DELIBERATELY NOT BORROWED. Two real, quantitative, citable measurements exist and
    /// neither is of a detector on this roster: the WFPC2 handbook's 0.3% +/- 0.1% of the original
    /// star flux, decaying within about 1000 s at -70 C, on WFPC2's Loral CCDs; and the LSSTCam
    /// e2v CCD250's residual at the scale of 10 electrons above sky, taking over a hundred seconds
    /// to clear. Both are carried below as named reference points, for validating the model and for
    /// nothing else. Using either as a default would be borrowing another device's interface
    /// physics, which is the thing this codebase refuses to do for brighter-fatter, for PRNU and
    /// for fringing.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class DetectorPersistence
    {
        /// <summary>
        /// WFPC2's Loral CCDs, from the WFPC2 Instrument Handbook Section 4.5: residual flux of
        /// 0.3% +/- 0.1% of the original star flux, for stars that were saturated in the previous
        /// image, with the trapped charge escaping so that residual images disappear within 1000 s
        /// at -70 C, and no measurable residual half an hour after overexposures of 100x full well.
        ///
        /// A named reference point, not a default. Nothing on this roster is this detector.
        /// </summary>
        public const double Wfpc2ResidualFractionOfSaturatedFlux = 0.003;
        public const double Wfpc2ResidualFractionUncertainty = 0.001;
        public const double Wfpc2ClearingTimeSeconds = 1000.0;

        /// <summary>
        /// LSSTCam's e2v CCD250, from arXiv:2502.05418: the residual in the trail region sits at the
        /// scale of 10 electrons above the sky background, decays as a sum of two exponentials, and
        /// takes well over a hundred seconds to dissipate completely.
        ///
        /// A named reference point, not a default, for the same reason as above.
        /// </summary>
        public const double Ccd250TrailResidualElectrons = 10.0;
        public const double Ccd250ClearingTimeSeconds = 150.0;

        /// <summary>
        /// The charge this pixel captures into surface traps at the end of an exposure that left it
        /// holding <paramref name="wellElectrons"/>, given how much room the two trap populations
        /// have left.
        ///
        /// Returns zero below the threshold, which is the whole content of the statement that a
        /// residual image follows a saturated source and not an ordinary one.
        /// </summary>
        /// <param name="wellElectrons">Charge in the well at the end of the exposure.</param>
        /// <param name="fullWellElectrons">The device's full well, which the threshold is a fraction of.</param>
        /// <param name="thresholdFraction">Fraction of full well below which nothing is trapped.</param>
        /// <param name="trappedFraction">Fraction of the charge above threshold that the interface takes.</param>
        /// <param name="trapDensityElectrons">Total capacity of the interface states, per pixel.</param>
        /// <param name="alreadyTrappedElectrons">What the traps are already holding, which reduces what they can take.</param>
        public static double Capture(
            double wellElectrons,
            double fullWellElectrons,
            double thresholdFraction,
            double trappedFraction,
            double trapDensityElectrons,
            double alreadyTrappedElectrons)
        {
            if (!(fullWellElectrons > 0.0)) return 0.0;

            double threshold = thresholdFraction * fullWellElectrons;
            double above = wellElectrons - threshold;
            if (!(above > 0.0)) return 0.0;

            // Bounded by what the interface can still hold. A pixel driven to a hundred times full
            // well does not trap a hundred times the charge; it fills the states and stops, which
            // is what the WFPC2 handbook's behaviour after such an overexposure requires.
            double room = trapDensityElectrons - alreadyTrappedElectrons;
            if (!(room > 0.0)) return 0.0;

            return Math.Min(trappedFraction * above, room);
        }

        /// <summary>
        /// What one trap population releases over <paramref name="elapsedSeconds"/>, and what it is
        /// left holding.
        ///
        /// Exact for any elapsed time rather than for one cadence, which is the reason the two
        /// populations are carried separately: each is a single exponential and a single exponential
        /// composes with itself over any split of the interval.
        /// </summary>
        public static double Release(double trappedElectrons, double elapsedSeconds, double decayTimeSeconds)
        {
            if (!(trappedElectrons > 0.0)) return 0.0;
            if (!(elapsedSeconds > 0.0)) return 0.0;
            if (!(decayTimeSeconds > 0.0)) return trappedElectrons;

            double remaining = trappedElectrons * Math.Exp(-elapsedSeconds / decayTimeSeconds);
            return trappedElectrons - remaining;
        }

        /// <summary>
        /// How the captured charge divides between the fast and the slow population.
        ///
        /// The split is the weight of the two-exponential fit, and it is a property of the device's
        /// interface states, so it comes from the spec rather than from anything about the exposure.
        /// </summary>
        public static void Split(double capturedElectrons, double fastShare, out double toFast, out double toSlow)
        {
            double w = Math.Min(1.0, Math.Max(0.0, fastShare));
            toFast = capturedElectrons * w;
            toSlow = capturedElectrons * (1.0 - w);
        }

        /// <summary>
        /// The fraction of a saturated pixel's trapped charge still held after a given time, as an
        /// observer planning a sequence would want it: the quantity that says how long to wait.
        ///
        /// Exposed because it is the comparator the published clearing times are stated as, and
        /// therefore the one tools/persistence-tests checks the model against.
        /// </summary>
        public static double RemainingFraction(
            double elapsedSeconds, double fastShare, double fastDecaySeconds, double slowDecaySeconds)
        {
            double w = Math.Min(1.0, Math.Max(0.0, fastShare));
            double fast = fastDecaySeconds > 0.0 ? Math.Exp(-elapsedSeconds / fastDecaySeconds) : 0.0;
            double slow = slowDecaySeconds > 0.0 ? Math.Exp(-elapsedSeconds / slowDecaySeconds) : 0.0;
            return w * fast + (1.0 - w) * slow;
        }
    }
}
