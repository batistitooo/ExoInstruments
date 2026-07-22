using System;
using ExoInstruments.Core;

namespace ExoInstruments.Session
{
    /// <summary>
    /// Direct-imaging campaign. Like every ground-based session, integration
    /// accrues only when the target is observable (night + above the telescope's
    /// altitude limit -- see ImagingObservingConditions), weighted by airmass.
    /// The accumulating quantity is EffectiveExposureSeconds: on-sky integration
    /// normalized to zenith conditions, the number DirectImagingSimulator's
    /// sqrt(t) SNR relation consumes.
    ///
    /// Ticks arrive with arbitrary UT gaps (time warp jumps hours per frame), so
    /// each tick integrates the observable fraction over [LastUt, currentUt] by
    /// midpoint subsampling. Conditions are a deterministic function of UT, which
    /// also makes forward prediction (next observing window, UT of 5-sigma) a
    /// straight re-run of the same integrator.
    /// </summary>
    public class ImagingObservationSession
    {
        // Subsampling: the fastest-changing gate is the Sun crossing twilight
        // (1 deg of altitude per minute of Kerbin's 6-hour day), so a 120 s step
        // mislocates a window edge by at most ~2 minutes. The step only widens
        // when a single tick spans more than MaxSubsteps * 120 s (~5.5 Kerbin
        // days), where per-window precision stops mattering.
        private const double IntegrationStepSeconds = 120.0;
        private const int MaxSubstepsPerTick = 4000;

        // Prediction horizons. A target whose RA sits near the Sun's is simply
        // out of season -- with no axial tilt the geometry repeats once per home
        // body orbit, so scanning one full orbit decides observability for good.
        private const double PredictionStepSeconds = 120.0;

        public StarTarget Target { get; private set; }
        public InstrumentSpec Instrument { get; private set; }
        public DirectImagingAssessment Assessment { get; private set; }
        public double StartUt { get; private set; }
        public double LastUt { get; private set; }
        public bool IsRunning { get; private set; }

        /// <summary>Wall-clock time since the campaign opened, observable or not.</summary>
        public double ElapsedSeconds => LastUt - StartUt;

        /// <summary>Zenith-equivalent on-sky integration: sum of dt / airmass^2 over observable intervals.</summary>
        public double EffectiveExposureSeconds { get; private set; }

        /// <summary>Conditions at the last tick, for the UI status line.</summary>
        public ImagingConditionsSnapshot CurrentConditions { get; private set; }

        private readonly ImagingObserverContext observer;

        public ImagingObservationSession(StarTarget target, InstrumentSpec instrument, double startUt, ImagingObserverContext observerContext)
        {
            Target = target;
            Instrument = instrument;
            observer = observerContext;
            Assessment = DirectImagingSimulator.Assess(target, instrument);
            StartUt = startUt;
            LastUt = startUt;
            IsRunning = true;
            CurrentConditions = EvaluateAt(startUt);
        }

        public void Tick(double currentUt)
        {
            if (!IsRunning) return;
            if (currentUt <= LastUt) return;
            EffectiveExposureSeconds += IntegrateEffective(LastUt, currentUt);
            LastUt = currentUt;
            CurrentConditions = EvaluateAt(currentUt);
        }

        public void Stop()
        {
            IsRunning = false;
        }

        private ImagingConditionsSnapshot EvaluateAt(double ut)
        {
            return ImagingObservingConditions.Evaluate(ut, Target.RaDeg, Target.DecDeg, observer);
        }

        /// <summary>Midpoint-rule integral of Efficiency(t) dt over [fromUt, toUt].</summary>
        private double IntegrateEffective(double fromUt, double toUt)
        {
            double interval = toUt - fromUt;
            double step = Math.Max(IntegrationStepSeconds, interval / MaxSubstepsPerTick);
            int n = Math.Max(1, (int)Math.Ceiling(interval / step));
            double dt = interval / n;
            double accumulated = 0.0;
            for (int i = 0; i < n; i++)
            {
                double midUt = fromUt + (i + 0.5) * dt;
                accumulated += EvaluateAt(midUt).Efficiency * dt;
            }
            return accumulated;
        }

        /// <summary>
        /// UT at which EffectiveExposureSeconds will reach the given value,
        /// simulating the upcoming nights from the session's current state.
        /// PositiveInfinity when it won't happen within maxWallSeconds of
        /// additional wall-clock time (target effectively unobservable, or the
        /// requirement is beyond any sane campaign).
        /// </summary>
        public double PredictUtForEffectiveExposure(double targetEffectiveSeconds, double maxWallSeconds)
        {
            if (targetEffectiveSeconds <= EffectiveExposureSeconds) return LastUt;
            double effective = EffectiveExposureSeconds;
            double ut = LastUt;
            double horizonUt = LastUt + maxWallSeconds;
            while (ut < horizonUt)
            {
                double efficiency = EvaluateAt(ut + PredictionStepSeconds * 0.5).Efficiency;
                double gained = efficiency * PredictionStepSeconds;
                if (effective + gained >= targetEffectiveSeconds)
                {
                    double fraction = gained > 0 ? (targetEffectiveSeconds - effective) / gained : 1.0;
                    return ut + fraction * PredictionStepSeconds;
                }
                effective += gained;
                ut += PredictionStepSeconds;
            }
            return double.PositiveInfinity;
        }

        /// <summary>
        /// Next UT at which the target becomes observable, scanning one full home
        /// body orbit (the seasonal repeat period). PositiveInfinity means the
        /// target is never observable from this site -- circumpolar-low, or pinned
        /// to the daytime sky year-round.
        /// </summary>
        public double PredictNextObservableUt()
        {
            if (CurrentConditions.Observable) return LastUt;
            double horizon = observer.HasSunOrbit && observer.SunOrbitPeriodSeconds > 0
                ? observer.SunOrbitPeriodSeconds
                : 2.0 * Math.Max(observer.BodyRotationPeriodSeconds, 21600.0);
            for (double dt = PredictionStepSeconds; dt <= horizon; dt += PredictionStepSeconds)
            {
                if (EvaluateAt(LastUt + dt).Observable) return LastUt + dt;
            }
            return double.PositiveInfinity;
        }
    }
}
