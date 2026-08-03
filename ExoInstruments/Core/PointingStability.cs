using System;

namespace ExoInstruments.Core
{
    /// <summary>How a spacecraft is holding its attitude while the shutter is open.</summary>
    public enum AttitudeControlMode
    {
        /// <summary>Nothing is holding the attitude: the vehicle is tumbling or drifting free.</summary>
        Uncontrolled,

        /// <summary>
        /// Continuous, proportional torque from momentum-exchange devices (reaction wheels,
        /// control moment gyros). No deadband, so no limit cycle; what is left is the
        /// instrument's own stability floor.
        /// </summary>
        MomentumExchange,

        /// <summary>
        /// On-off thrusters holding the attitude inside a deadband. A thruster has no
        /// proportional setting, so the attitude cannot be held at a point, only inside a band,
        /// and it traverses that band continuously (see LimitCycle).
        /// </summary>
        ReactionControl,
    }

    /// <summary>
    /// What the spacecraft's attitude motion does to the photograph.
    ///
    /// WHY THIS IS THE INTERESTING PART OF PUTTING A TELESCOPE ON A VEHICLE. On the ground the
    /// mount is bolted to a mountain and the only motion is the planet's own rotation, which an
    /// autoguider cancels. In orbit the telescope IS the vehicle: every attitude excursion the
    /// control system fails to null is written straight onto the focal plane. That makes the
    /// choice of control hardware an optical decision, and the difference between the two kinds
    /// is not a matter of degree but of kind.
    ///
    ///   * A reaction wheel exchanges momentum continuously. It can be commanded to any torque
    ///     down to zero, so a proportional controller can hold the boresight at a point, and
    ///     what remains is sensor noise and the wheels' own imbalance. This is how every real
    ///     space telescope points, and it is why HST's figure is 0.008 arcsec rms.
    ///
    ///   * A thruster is on or it is off. It cannot be commanded to a small torque, so the
    ///     attitude cannot be held at a point at all: the controller lets the vehicle drift
    ///     until it leaves a deadband, pulses it back, and the attitude traverses the band
    ///     forever. That is the LIMIT CYCLE, and its amplitude is the deadband, which is
    ///     typically arcMINUTES. Against a 0.04 arcsec pixel that is four orders of magnitude
    ///     too coarse: an exposure taken under thruster control alone is not a slightly worse
    ///     photograph, it is a streak.
    ///
    /// The limit cycle is textbook attitude control (Wertz, ed., "Spacecraft Attitude
    /// Determination and Control", Reidel 1978, Sect. 18.3; Sidi, "Spacecraft Dynamics and
    /// Control", CUP 1997, Chap. 7). With no external torque, a minimum-impulse pulse changes
    /// the body rate by delta-omega = M t_pulse / I; the vehicle then coasts across the deadband
    /// at that rate until it reaches the far side, where an opposite pulse reverses it. The
    /// attitude therefore traces a triangle wave of peak-to-peak amplitude 2 theta_db at rate
    /// delta-omega, and the cycle period is 4 theta_db / delta-omega.
    ///
    /// Pure C# with no Unity dependency, like the rest of Core: the caller supplies the
    /// vehicle's real inertia, torque and deadband, and this returns arcseconds.
    /// </summary>
    public static class PointingStability
    {
        private const double RadToArcsec = 180.0 / Math.PI * 3600.0;

        /// <summary>
        /// Body-rate increment one minimum-length thruster pulse imparts, rad/s, from the
        /// vehicle's own control torque and moment of inertia about the axis.
        /// </summary>
        public static double LimitCycleRateRadPerSecond(double controlTorqueNm, double inertiaKgM2,
                                                        double minimumPulseSeconds)
        {
            if (!(controlTorqueNm > 0.0) || !(inertiaKgM2 > 0.0) || !(minimumPulseSeconds > 0.0)) return 0.0;
            return controlTorqueNm * minimumPulseSeconds / inertiaKgM2;
        }

        /// <summary>
        /// Period of the limit cycle, seconds: the time to cross the deadband and come back.
        /// PositiveInfinity when the rate increment is zero, i.e. when nothing is pulsing.
        /// </summary>
        public static double LimitCyclePeriodSeconds(double deadbandArcsec, double rateRadPerSecond)
        {
            if (!(rateRadPerSecond > 0.0)) return double.PositiveInfinity;
            double deadbandRad = deadbandArcsec / RadToArcsec;
            return 4.0 * deadbandRad / rateRadPerSecond;
        }

        /// <summary>
        /// RMS angular displacement of the image over one exposure under a limit cycle,
        /// arcseconds.
        ///
        /// TWO REGIMES, ONE EXPRESSION. If the exposure is long compared with the cycle period,
        /// the boresight visits the whole deadband and the image is spread over its full
        /// peak-to-peak width, 2 theta_db. If it is short, the boresight has only moved omega T
        /// in a straight line. Both are uniform distributions over their own length L, whose RMS
        /// is L / sqrt(12), so the two regimes are the same formula with
        ///
        ///     L = min(omega T, 2 theta_db)
        ///
        /// and they agree exactly at the crossover, which is what makes this one expression
        /// rather than two cases stitched together. The triangle wave really is uniform in
        /// angle: it traverses the band at constant rate, so it spends equal time at every angle
        /// in it.
        /// </summary>
        public static double LimitCycleSmearArcsec(double deadbandArcsec, double rateRadPerSecond,
                                                   double exposureSeconds)
        {
            if (!(exposureSeconds > 0.0) || !(deadbandArcsec > 0.0)) return 0.0;
            double travelledArcsec = Math.Max(0.0, rateRadPerSecond) * exposureSeconds * RadToArcsec;
            double length = Math.Min(travelledArcsec, 2.0 * deadbandArcsec);
            return length / Math.Sqrt(12.0);
        }

        /// <summary>
        /// RMS image displacement from a steady drift at <paramref name="rateArcsecPerSecond"/>
        /// over the exposure, arcseconds. Same uniform-distribution RMS as above: a constant
        /// rate lays the image down evenly along a line of length rate x time.
        ///
        /// This is the term that catches an uncontrolled vehicle, and the one that catches a
        /// controlled one whose control is fighting a disturbance torque it cannot fully null.
        /// </summary>
        public static double DriftSmearArcsec(double rateArcsecPerSecond, double exposureSeconds)
        {
            if (!(rateArcsecPerSecond > 0.0) || !(exposureSeconds > 0.0)) return 0.0;
            return rateArcsecPerSecond * exposureSeconds / Math.Sqrt(12.0);
        }

        /// <summary>
        /// Total RMS pointing excursion over the exposure, arcseconds: the instrument's own
        /// stability floor and the vehicle's attitude motion, added in quadrature.
        ///
        /// Quadrature is right here and is not right everywhere in this codebase (see
        /// OpticalPsf.AtmosphericFwhmForDelivered for a case where it is wrong): these are
        /// independent random displacements of the same image, and the variance of a sum of
        /// independent random variables is the sum of their variances. That is a statement about
        /// the displacements, not about the shape of any profile, so it holds whatever the
        /// distributions are.
        /// </summary>
        public static double TotalPointingRmsArcsec(double instrumentJitterArcsecRms,
                                                    double vehicleSmearArcsecRms)
        {
            double a = Math.Max(0.0, instrumentJitterArcsecRms);
            double b = Math.Max(0.0, vehicleSmearArcsecRms);
            return Math.Sqrt(a * a + b * b);
        }

        /// <summary>
        /// Gaussian FWHM equivalent to an RMS displacement, arcseconds.
        ///
        /// The pointing excursion smears the image by convolving it with the distribution of
        /// where the boresight was pointing during the exposure. Treating that distribution as
        /// Gaussian is exact for the jitter term, which is the sum of many independent
        /// disturbances, and is an approximation for the limit-cycle term, whose true
        /// distribution is uniform. The approximation is deliberate and it is conservative in
        /// the direction that matters: matching the RMS puts the same second moment on the image
        /// while giving the profile wings a uniform distribution does not have, so a marginally
        /// resolved feature is never reported sharper than it would really be.
        /// </summary>
        public static double RmsToFwhmArcsec(double rmsArcsec)
        {
            return Math.Max(0.0, rmsArcsec) * 2.0 * Math.Sqrt(2.0 * Math.Log(2.0));
        }

        /// <summary>
        /// Everything above, for one exposure on one vehicle: what the pointing does to this
        /// frame. Handed the vehicle's real state, it returns the numbers the imaging pipeline
        /// and the readout both need.
        /// </summary>
        public static PointingBudget Evaluate(in PointingInputs inputs)
        {
            var b = new PointingBudget();
            b.Mode = inputs.Mode;

            switch (inputs.Mode)
            {
                case AttitudeControlMode.MomentumExchange:
                    // Wheels null the error continuously; the residual is the instrument's own
                    // floor plus whatever drift the control is failing to hold against.
                    b.VehicleSmearArcsecRms = DriftSmearArcsec(inputs.ResidualDriftArcsecPerSecond,
                                                               inputs.ExposureSeconds);
                    break;

                case AttitudeControlMode.ReactionControl:
                    b.LimitCycleRateRadPerSecond = LimitCycleRateRadPerSecond(
                        inputs.ControlTorqueNm, inputs.InertiaKgM2, inputs.MinimumPulseSeconds);
                    b.LimitCyclePeriodSeconds = LimitCyclePeriodSeconds(
                        inputs.DeadbandArcsec, b.LimitCycleRateRadPerSecond);
                    b.VehicleSmearArcsecRms = LimitCycleSmearArcsec(
                        inputs.DeadbandArcsec, b.LimitCycleRateRadPerSecond, inputs.ExposureSeconds);
                    break;

                default:
                    b.VehicleSmearArcsecRms = DriftSmearArcsec(inputs.ResidualDriftArcsecPerSecond,
                                                               inputs.ExposureSeconds);
                    break;
            }

            // A directly measured body rate always wins over the analytic estimate: when the
            // vehicle is loaded and its physics are running, the game is integrating the real
            // attitude motion, and no model of what the controller ought to achieve beats
            // watching what it did achieve.
            if (inputs.HasMeasuredRate)
            {
                double measured = DriftSmearArcsec(inputs.MeasuredRateArcsecPerSecond, inputs.ExposureSeconds);
                if (inputs.Mode == AttitudeControlMode.ReactionControl && inputs.DeadbandArcsec > 0.0)
                    measured = Math.Min(measured, 2.0 * inputs.DeadbandArcsec / Math.Sqrt(12.0));
                b.VehicleSmearArcsecRms = Math.Max(b.VehicleSmearArcsecRms, measured);
                b.RateWasMeasured = true;
            }

            b.InstrumentJitterArcsecRms = Math.Max(0.0, inputs.InstrumentJitterArcsecRms);
            b.TotalArcsecRms = TotalPointingRmsArcsec(b.InstrumentJitterArcsecRms, b.VehicleSmearArcsecRms);
            b.EquivalentFwhmArcsec = RmsToFwhmArcsec(b.TotalArcsecRms);
            return b;
        }
    }

    /// <summary>The vehicle state one exposure is taken in.</summary>
    public struct PointingInputs
    {
        public AttitudeControlMode Mode;
        public double ExposureSeconds;

        /// <summary>The instrument's own published stability floor, arcsec rms (HST: 0.008).</summary>
        public double InstrumentJitterArcsecRms;

        /// <summary>Attitude deadband half-width the controller holds to, arcsec. Only used under thruster control.</summary>
        public double DeadbandArcsec;

        /// <summary>Control torque one thruster pulse applies about the axis, N m.</summary>
        public double ControlTorqueNm;

        /// <summary>Vehicle moment of inertia about the axis, kg m^2.</summary>
        public double InertiaKgM2;

        /// <summary>Shortest pulse the controller can command, seconds.</summary>
        public double MinimumPulseSeconds;

        /// <summary>Steady rate the control is failing to null, arcsec/s. Zero for a control system that is winning.</summary>
        public double ResidualDriftArcsecPerSecond;

        /// <summary>True when the caller sampled the vehicle's real body rate rather than estimating it.</summary>
        public bool HasMeasuredRate;
        public double MeasuredRateArcsecPerSecond;
    }

    /// <summary>What the pointing costs this frame.</summary>
    public struct PointingBudget
    {
        public AttitudeControlMode Mode;
        public double InstrumentJitterArcsecRms;
        public double VehicleSmearArcsecRms;
        public double TotalArcsecRms;

        /// <summary>The Gaussian FWHM to convolve the optical PSF with; what the pipeline actually consumes.</summary>
        public double EquivalentFwhmArcsec;

        public double LimitCycleRateRadPerSecond;
        public double LimitCyclePeriodSeconds;
        public bool RateWasMeasured;
    }
}
