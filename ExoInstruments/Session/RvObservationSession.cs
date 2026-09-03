using System;
using System.Collections.Generic;
using ExoInstruments.Core;

namespace ExoInstruments.Session
{
    /// <summary>
    /// RV analog of ObservationSession. Observes the host star, so the reflex signal from
    /// every companion superpose on the same measurement. Ground-based: epochs only when the
    /// target is up at night. No scintillation (spectrograph measures line positions, not flux).
    /// Per-epoch uncertainty is instrument-only; stellar jitter shows up as excess residuals.
    /// </summary>
    public class RvObservationSession
    {
        /// <summary>
        /// High-cadence epoch spacing inside a scheduled transit window, the dense sequence a real Rossiter-
        /// McLaughlin run takes across one transit night.
        /// </summary>
        public const double RmBurstCadenceSeconds = 600.0;

        public StarTarget Target { get; private set; }
        public List<StarTarget> SystemPlanets { get; private set; }
        public InstrumentSpec Instrument { get; private set; }
        public double StartUt { get; private set; }
        public double LastSampleUt { get; private set; }
        public List<RvSample> Samples { get; private set; }
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Transiting planets this session schedules an RM burst around, sampled at RmBurstCadenceSeconds
        /// during transit windows. Empty means no scheduling (RM is always in the physics, but a real
        /// observatory can only plan around a known ephemeris).
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

        /// <summary>
        /// The seed every noise draw in this campaign came from, so the run can be repeated.
        ///
        /// A campaign that cannot be reproduced cannot be published against, and this one could
        /// not: the generator was constructed unseeded, so a recovered semi-amplitude was
        /// unrepeatable even from an identical target, instrument, site and start date. The
        /// imaging path never had that gap, its PCG32 streams are seeded per exposure and the seed
        /// is written into the FITS header as RANDSEED; this is the same discipline for the
        /// campaigns. Passing null still draws a seed, and then SAYS which, which is the point: an
        /// interesting run stays reproducible after the fact rather than only when someone thought
        /// to pin it in advance.
        /// </summary>
        public int RandomSeed { get; }

        public RvObservationSession(StarTarget target, List<StarTarget> systemPlanets, InstrumentSpec instrument, double startUt, ImagingObserverContext observerContext,
            List<StarTarget> transitBurstPlanets = null, int? randomSeed = null)
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
            RandomSeed = randomSeed ?? new Random().Next();
            _rng = new Random(RandomSeed);
            TransitBurstPlanets = transitBurstPlanets ?? new List<StarTarget>();
            nextSampleUt = startUt + CadenceSecondsAt(startUt);
            CurrentConditions = SnapshotAt(startUt);
        }

        // Same reasoning as ObservationSession.MaxStepsPerTick: prevents a single frame from
        // grinding the whole warp gap in 60s steps. Catch-up spreads across Update() frames.
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

                    // Never leap over an upcoming transit window: an 8h cadence would otherwise skip a 3h transit entirely.
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

        // A burst window spans mid-transit +/- one full duration, enough out-of-transit shoulder on both sides
        // to anchor the anomaly's baseline.
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

        // Earliest upcoming burst-window start strictly after ut; PositiveInfinity when none is scheduled.
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
