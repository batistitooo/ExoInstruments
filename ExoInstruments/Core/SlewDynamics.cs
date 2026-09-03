using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// How long it takes to repoint a spacecraft, and what that costs it.
    ///
    /// A rest-to-rest manoeuvre about the eigenaxis joining two attitudes, which is what a
    /// three-axis vehicle performs: it does not slew in pitch and then in yaw, it rotates once.
    ///
    ///   alpha = tau / J                         Euler's equation
    ///   triangular   t = 2 sqrt(theta / alpha)  accelerate half way, decelerate half way
    ///   trapezoidal  t = theta / w_max + w_max / alpha
    ///
    /// The branches meet at theta = w_max^2 / alpha, where both give 2 w_max / alpha.
    ///
    /// The rate ceiling is published per spacecraft rather than derived, because it is not a
    /// torque limit: it is set by the rate gyroscopes' measuring range and the guidance system's
    /// ability to track its own attitude while moving. Guide-star acquisition is carried
    /// separately because it does not scale with the angle, which is why real programmes group
    /// targets that sit near each other.
    ///
    /// Sources: STScI, HST Primer for Cycle 34, "Pointing, Orientation, and Roll Constraints"
    /// (slew rate, and the full-circle time that cross-checks it) and "Orbital Visibility,
    /// Acquisition Times, and Overheads" (acquisition). The profile is the standard rest-to-rest
    /// eigenaxis manoeuvre of Wertz, the same reference PointingStability's limit cycle uses.
    /// </summary>
    public static class SlewDynamics
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;

        // Earth's radius (m) and gravitational parameter (m^3/s^2): the reference UniverseTimeScale measures
        // against.
        private const double EarthRadiusMeters = 6.371e6;
        private const double EarthGravParameter = 3.986004418e14;

        /// <summary>
        /// How much faster this universe runs than the one the published figures were measured in.
        ///
        /// A published rate cannot be copied into a scaled game literally. What shapes an observing
        /// programme is the FRACTION OF AN ORBIT spent turning: for HST, 15 min of slew plus 6.5 of
        /// acquisition against a 96 min orbit. Kerbin is a tenth of Earth's size, so a low orbit
        /// takes about 29 min and 6 deg/min makes the same repoint cost half an orbit, putting the
        /// target behind the planet before the telescope arrives. So the ratio is preserved
        /// instead, through the grazing-orbit period sqrt(R^3/mu) of the home body against Earth's.
        /// No reference altitude is needed: the ratio of two grazing periods is independent of any
        /// altitude one might pick.
        ///
        /// Exactly 1 on a real-scale install, where every published figure is then used as
        /// published. That is the check which makes this a scale transplant and not a difficulty
        /// slider, and tools/slew-tests asserts it.
        /// </summary>
        public static double UniverseTimeScale(double homeRadiusMeters, double homeGravParameter)
        {
            if (!(homeRadiusMeters > 0.0) || !(homeGravParameter > 0.0)) return 1.0;

            double home = Math.Sqrt(homeRadiusMeters * homeRadiusMeters * homeRadiusMeters / homeGravParameter);
            double earth = Math.Sqrt(EarthRadiusMeters * EarthRadiusMeters * EarthRadiusMeters / EarthGravParameter);
            return home > 0.0 ? earth / home : 1.0;   // the 2 pi cancels
        }

        /// <summary>
        /// Angular acceleration a control torque gives against an inertia, deg/s^2. Zero means a vehicle that
        /// cannot slew, not one that slews instantly.
        /// </summary>
        public static double AngularAccelerationDegPerSecond2(double torqueNm, double inertiaKgM2)
        {
            if (!(torqueNm > 0.0) || !(inertiaKgM2 > 0.0)) return 0.0;
            return torqueNm / inertiaKgM2 * RadToDeg;
        }

        /// <summary>
        /// The manoeuvre: how long it takes, how fast it gets, and how much of it is spent working.
        /// A zero angle still pays the acquisition, because guidance has to lock before the shutter
        /// opens.
        /// </summary>
        public static SlewProfile Compute(double angleDeg, double torqueNm, double inertiaKgM2,
                                          double maxRateDegPerSecond, double acquisitionSeconds)
        {
            var p = new SlewProfile
            {
                AngleDeg = Math.Max(0.0, angleDeg),
                AcquisitionSeconds = Math.Max(0.0, acquisitionSeconds),
            };
            p.AccelerationDegPerSecond2 = AngularAccelerationDegPerSecond2(torqueNm, inertiaKgM2);

            if (!(p.AngleDeg > 0.0))
            {
                p.TotalSeconds = p.AcquisitionSeconds;
                return p;
            }

            // No torque is no slew, reported as infinite rather than as a NaN that would compare
            // false against every threshold it is tested against.
            if (!(p.AccelerationDegPerSecond2 > 0.0))
            {
                p.ManoeuvreSeconds = double.PositiveInfinity;
                p.TotalSeconds = double.PositiveInfinity;
                return p;
            }

            double alpha = p.AccelerationDegPerSecond2;
            double triangularPeak = Math.Sqrt(alpha * p.AngleDeg);

            if (!(maxRateDegPerSecond > 0.0) || triangularPeak <= maxRateDegPerSecond)
            {
                p.PeakRateDegPerSecond = triangularPeak;
                p.ManoeuvreSeconds = 2.0 * Math.Sqrt(p.AngleDeg / alpha);
            }
            else
            {
                p.PeakRateDegPerSecond = maxRateDegPerSecond;
                p.ManoeuvreSeconds = p.AngleDeg / maxRateDegPerSecond + maxRateDegPerSecond / alpha;
                p.RateLimited = true;
            }

            p.TotalSeconds = p.ManoeuvreSeconds + p.AcquisitionSeconds;
            return p;
        }

        /// <summary>
        /// Seconds spent applying torque: the two ramps, 2 w_peak / alpha, and nothing between.
        /// Not what the charge is billed on (see ReactionWheelChargeUnits); it is what decides
        /// which branch of the profile the vehicle is on.
        /// </summary>
        public static double TorquingSeconds(in SlewProfile profile)
        {
            if (!(profile.AccelerationDegPerSecond2 > 0.0)) return 0.0;
            return Math.Min(2.0 * profile.PeakRateDegPerSecond / profile.AccelerationDegPerSecond2,
                            profile.ManoeuvreSeconds);
        }

        /// <summary>
        /// Electric charge a momentum-exchange slew costs, in KSP's units. The rate is the vessel's
        /// own wheels' published draw; nothing here converts to watts, which ElectricCharge has no
        /// conversion to.
        ///
        /// BILLED FOR THE WHOLE MANOEUVRE, not only the ramps, and this was wrong first time round.
        /// Charging only TorquingSeconds sounds right (a wheel coasting at constant momentum takes
        /// no torque current) and fails twice: KSP's own ModuleReactionWheel bills whenever the
        /// autopilot is commanding it, so a ground-commanded slew would have cost less than the
        /// identical slew flown by hand; and a real wheel assembly draws tens of watts continuously
        /// for bearing and motor losses. With KSP's rocket-sized wheels the ramps last tens of
        /// milliseconds, so billing them made a 90 degree repoint cost 0.017 EC out of 400.
        /// </summary>
        public static double ReactionWheelChargeUnits(in SlewProfile profile, double wheelChargeUnitsPerSecond)
        {
            if (!(wheelChargeUnitsPerSecond > 0.0)) return 0.0;
            double t = profile.ManoeuvreSeconds;
            if (double.IsInfinity(t) || double.IsNaN(t) || t < 0.0) return 0.0;
            return wheelChargeUnitsPerSecond * t;
        }

        /// <summary>
        /// Total impulse a thruster-controlled slew spends, N s. Momentum goes from zero to
        /// J w_peak and back, so the thrusters deliver twice that in angular impulse; over the
        /// moment arm it is the linear impulse propellant is measured against.
        /// </summary>
        public static double ThrusterImpulseNewtonSeconds(double inertiaKgM2, double peakRateDegPerSecond,
                                                          double momentArmMeters)
        {
            if (!(inertiaKgM2 > 0.0) || !(peakRateDegPerSecond > 0.0) || !(momentArmMeters > 0.0)) return 0.0;
            return 2.0 * inertiaKgM2 * peakRateDegPerSecond * DegToRad / momentArmMeters;
        }

        /// <summary>Propellant an impulse costs, kg, from I = m Isp g0.</summary>
        public static double PropellantMassKg(double impulseNewtonSeconds, double specificImpulseSeconds)
        {
            const double StandardGravity = 9.80665;
            if (!(impulseNewtonSeconds > 0.0) || !(specificImpulseSeconds > 0.0)) return 0.0;
            return impulseNewtonSeconds / (specificImpulseSeconds * StandardGravity);
        }

        /// <summary>
        /// Fraction of the ANGLE covered after a given fraction of the manoeuvre TIME. The two are
        /// not the same, and the difference is the shape of the profile: the readout interpolates
        /// the boresight along this, so a slew eases off the old target and onto the new one.
        /// </summary>
        public static double FractionOfAngleCovered(in SlewProfile profile, double elapsedSeconds)
        {
            if (!(profile.AngleDeg > 0.0)) return 1.0;
            if (!(profile.ManoeuvreSeconds > 0.0) || double.IsInfinity(profile.ManoeuvreSeconds)) return 0.0;
            if (elapsedSeconds <= 0.0) return 0.0;
            if (elapsedSeconds >= profile.ManoeuvreSeconds) return 1.0;

            double alpha = profile.AccelerationDegPerSecond2;
            double t = elapsedSeconds;

            if (!profile.RateLimited)
            {
                double half = 0.5 * profile.ManoeuvreSeconds;
                double covered = t <= half
                    ? 0.5 * alpha * t * t
                    : profile.AngleDeg - 0.5 * alpha * (profile.ManoeuvreSeconds - t) * (profile.ManoeuvreSeconds - t);
                return Clamp01(covered / profile.AngleDeg);
            }

            double ramp = profile.PeakRateDegPerSecond / alpha;
            double c;
            if (t <= ramp) c = 0.5 * alpha * t * t;
            else if (t >= profile.ManoeuvreSeconds - ramp)
            {
                double remaining = profile.ManoeuvreSeconds - t;
                c = profile.AngleDeg - 0.5 * alpha * remaining * remaining;
            }
            else c = 0.5 * alpha * ramp * ramp + profile.PeakRateDegPerSecond * (t - ramp);

            return Clamp01(c / profile.AngleDeg);
        }

        /// <summary>
        /// How fast the vehicle is turning at a point in the manoeuvre, deg/s. Zero once it is
        /// over. This is what smears a frame taken mid-slew: PointingStability already turns a rate
        /// and an exposure into a streak, so exposing during a repoint needs the rate, not a case.
        /// </summary>
        public static double RateDegPerSecondAt(in SlewProfile profile, double elapsedSeconds)
        {
            if (elapsedSeconds < 0.0 || elapsedSeconds >= profile.ManoeuvreSeconds) return 0.0;
            if (!(profile.AccelerationDegPerSecond2 > 0.0)) return 0.0;

            double alpha = profile.AccelerationDegPerSecond2;
            double ramp = profile.PeakRateDegPerSecond / alpha;
            if (elapsedSeconds <= ramp) return alpha * elapsedSeconds;
            if (elapsedSeconds >= profile.ManoeuvreSeconds - ramp)
                return alpha * (profile.ManoeuvreSeconds - elapsedSeconds);
            return profile.PeakRateDegPerSecond;
        }

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);
    }

    /// <summary>One rest-to-rest repoint, as planned before it is flown.</summary>
    public struct SlewProfile
    {
        /// <summary>Eigenaxis angle between the two attitudes, degrees.</summary>
        public double AngleDeg;

        /// <summary>Torque over inertia, deg/s^2. Zero for a vehicle with no attitude control.</summary>
        public double AccelerationDegPerSecond2;

        /// <summary>Fastest the vehicle turns during the manoeuvre, deg/s.</summary>
        public double PeakRateDegPerSecond;

        /// <summary>Rest-to-rest rotation time, seconds. Infinite with no control torque.</summary>
        public double ManoeuvreSeconds;

        /// <summary>Guide-star acquisition after it, seconds. Paid even for a zero-angle repoint.</summary>
        public double AcquisitionSeconds;

        /// <summary>Everything the player waits for before the shutter may open.</summary>
        public double TotalSeconds;

        /// <summary>True when the vehicle hit its published rate ceiling and coasted.</summary>
        public bool RateLimited;
    }
}
