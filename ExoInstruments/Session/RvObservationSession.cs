using System;
using System.Collections.Generic;
using ExoInstruments.Core;

namespace ExoInstruments.Session
{
    /// <summary>
    /// Mirrors ObservationSession for the RV path. Cadence comes from the chosen
    /// Instrument, not a fixed constant. The session observes the host star, not a
    /// single planet: the measured reflex velocity carries every catalog companion's
    /// signal superposed (see RvSimulator.GenerateSystemVelocityAtTime).
    ///
    /// All the registry's spectrographs are ground-based, so epochs are only
    /// taken when the site can see the target (night + above the telescope
    /// limit -- same gate as the other paths). No scintillation term here: a
    /// spectrograph measures line positions, not integrated flux. The reported
    /// per-epoch uncertainty stays instrument-only while the generated scatter
    /// includes the star's activity jitter -- the observer discovers an active
    /// star the way real surveys do, as unexplained excess residuals.
    /// </summary>
    public class RvObservationSession
    {
        /// <summary>High-cadence epoch spacing inside a scheduled transit window -- the dense sequence a real Rossiter-McLaughlin run takes across one transit night.</summary>
        public const double RmBurstCadenceSeconds = 600.0;

        public StarTarget Target { get; private set; }
        public List<StarTarget> SystemPlanets { get; private set; }
        public InstrumentSpec Instrument { get; private set; }
        public double StartUt { get; private set; }
        public double LastSampleUt { get; private set; }
        public List<RvSample> Samples { get; private set; }
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Transiting system members with a usable photometric ephemeris, whose
        /// transit windows this session samples at RmBurstCadenceSeconds instead
        /// of the regular epoch cadence. Empty = no scheduling (the physics stays
        /// in the signal regardless): a real observatory can only schedule an RM
        /// sequence around an ephemeris it actually knows, so career mode passes
        /// this only for identified targets.
        /// </summary>
        public List<StarTarget> TransitBurstPlanets { get; private set; }

        /// <summary>True while the current epoch spacing is the high-cadence RM sequence, for the UI status line.</summary>
        public bool InTransitBurst { get; private set; }

        /// <summary>Conditions at the last tick, for the UI status line.</summary>
        public ImagingConditionsSnapshot CurrentConditions { get; private set; }

        private readonly ImagingObserverContext observer;
        private readonly Random _rng;

        // See ObservationSession.nextSampleUt: sliding anchor, not a rigid
        // grid, so a cadence commensurate with the day length can't starve.
        private double nextSampleUt;

        public RvObservationSession(StarTarget target, List<StarTarget> systemPlanets, InstrumentSpec instrument, double startUt, ImagingObserverContext observerContext,
            List<StarTarget> transitBurstPlanets = null)
        {
            Target = target;
            SystemPlanets = systemPlanets != null && systemPlanets.Count > 0
                ? systemPlanets
                : new List<StarTarget> { target };
            Instrument = instrument;
            observer = observerContext;
            StartUt = startUt;
            LastSampleUt = startUt;
            Samples = new List<RvSample>();
            IsRunning = true;
            _rng = new Random();
            TransitBurstPlanets = transitBurstPlanets ?? new List<StarTarget>();
            nextSampleUt = startUt + CadenceSecondsAt(startUt);
            CurrentConditions = SnapshotAt(startUt);
        }

        // See ObservationSession.MaxStepsPerTick for why this cap exists: without
        // it, a big warp-driven jump in currentUt combined with a long
        // unobservable stretch forces this loop to grind through the whole gap
        // in 60s-minimum steps in a single frame -- a multi-second hitch that
        // looks like "elapsed stopped, no new points" until the frame finally
        // finishes (stopping/restarting warp doesn't fix anything, it just gives
        // the stalled frame a chance to complete). Catch-up spreads across
        // however many Update() frames it takes instead.
        private const int MaxStepsPerTick = 20000;

        public void Tick(double currentUt)
        {
            if (!IsRunning) return;

            double searchStepSeconds = Math.Max(60.0, Instrument.CadenceSeconds / 8.0);

            int steps = 0;
            while (nextSampleUt <= currentUt && steps < MaxStepsPerTick)
            {
                ImagingConditionsSnapshot conditions = SnapshotAt(nextSampleUt);
                if (conditions.Observable)
                {
                    double velocity = RvSimulator.GenerateSystemVelocityAtTime(SystemPlanets, Instrument, nextSampleUt, _rng);
                    double uncertaintyMps = Instrument.EstimatePrecision(Target.ApparentMagnitude);
                    Samples.Add(new RvSample(nextSampleUt, velocity, uncertaintyMps));
                    LastSampleUt = nextSampleUt;

                    // Step by the local cadence, but never leap over an upcoming
                    // transit window: an 8h epoch spacing would skip a 3h transit
                    // entirely, and the whole point of scheduling is to be on sky
                    // when ingress starts.
                    double step = CadenceSecondsAt(nextSampleUt);
                    double burstStart = NextBurstWindowStartUt(nextSampleUt);
                    if (burstStart > nextSampleUt && burstStart < nextSampleUt + step)
                    {
                        step = burstStart - nextSampleUt;
                    }
                    nextSampleUt += step;
                }
                else
                {
                    nextSampleUt += searchStepSeconds;
                }
                steps++;
            }

            CurrentConditions = SnapshotAt(currentUt);
            InTransitBurst = IsInBurstWindow(currentUt);
        }

        private double CadenceSecondsAt(double ut)
        {
            return IsInBurstWindow(ut)
                ? Math.Min(RmBurstCadenceSeconds, Instrument.CadenceSeconds)
                : Instrument.CadenceSeconds;
        }

        /// <summary>A burst window spans mid-transit +/- one full duration -- enough out-of-transit shoulder on both sides to anchor the anomaly's baseline.</summary>
        private bool IsInBurstWindow(double ut)
        {
            for (int i = 0; i < TransitBurstPlanets.Count; i++)
            {
                StarTarget planet = TransitBurstPlanets[i];
                double periodSeconds = planet.PlanetPeriodDays * 86400.0;
                if (periodSeconds <= 0) continue;
                double halfWindowSeconds = (planet.EstimatedTransitDurationHours ?? 0.0) * 3600.0;
                if (halfWindowSeconds <= 0) continue;

                double phase = (ut / periodSeconds + planet.PlanetPhaseOffset01) % 1.0;
                if (phase < 0) phase += 1.0;
                double phaseCentered = phase > 0.5 ? phase - 1.0 : phase;
                if (Math.Abs(phaseCentered) * periodSeconds <= halfWindowSeconds) return true;
            }
            return false;
        }

        /// <summary>Earliest upcoming burst-window start strictly after ut; PositiveInfinity when none is scheduled.</summary>
        private double NextBurstWindowStartUt(double ut)
        {
            double earliest = double.PositiveInfinity;
            for (int i = 0; i < TransitBurstPlanets.Count; i++)
            {
                StarTarget planet = TransitBurstPlanets[i];
                double halfWindowSeconds = (planet.EstimatedTransitDurationHours ?? 0.0) * 3600.0;
                if (halfWindowSeconds <= 0) continue;
                double centerUt = RossiterMcLaughlin.NextTransitCenterUt(planet, ut);
                if (double.IsNaN(centerUt)) continue;
                double windowStart = centerUt - halfWindowSeconds;
                if (windowStart > ut && windowStart < earliest) earliest = windowStart;
            }
            return earliest;
        }

        private ImagingConditionsSnapshot SnapshotAt(double ut)
        {
            if (Instrument.IsSpaceBased)
            {
                return new ImagingConditionsSnapshot
                {
                    SunAltitudeDeg = -90.0,
                    HasTargetCoordinates = Target.RaDeg.HasValue && Target.DecDeg.HasValue,
                    TargetAltitudeDeg = 90.0,
                    IsNight = true,
                    TargetUp = true,
                    Observable = true,
                    Airmass = 1.0,
                    Efficiency = 1.0,
                };
            }
            return ImagingObservingConditions.Evaluate(ut, Target.RaDeg, Target.DecDeg, observer);
        }

        public void Stop()
        {
            IsRunning = false;
        }
    }
}
