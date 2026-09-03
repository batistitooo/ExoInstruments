using System;
using ExoInstruments.Core;

namespace ExoInstruments.Session
{
    /// <summary>
    /// Direct-imaging campaign. Accumulates EffectiveExposureSeconds: on-sky integration
    /// normalized to zenith conditions (what DirectImagingSimulator's sqrt(t) consumes).
    /// Each tick integrates by midpoint subsampling over [LastUt, currentUt]; conditions
    /// are deterministic in UT, so the same integrator also drives forward prediction.
    /// </summary>
    public class ImagingObservationSession
    {
        // 120 s step: the Sun crosses twilight at ~1°/min on Kerbin's 6-hour day, so window
        // edges are mislocated by at most ~2 minutes. Step widens automatically beyond 4000 substeps.
        private const double IntegrationStepSeconds = 120.0;
        private const int MaxSubstepsPerTick = 4000;

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

        // Midpoint-rule integral of Efficiency(t) dt over [fromUt, toUt].
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
        /// UT at which EffectiveExposureSeconds will reach targetEffectiveSeconds. PositiveInfinity when it
        /// won't happen within maxWallSeconds (target unobservable or requirement unreachable).
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
        /// Next UT when the target becomes observable, scanning up to one home body orbit. PositiveInfinity
        /// means it's never visible from this site.
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
