using System;
using System.Collections.Generic;
using ExoInstruments.Core;

namespace ExoInstruments.Session
{
    /// <summary>
    /// Transit photometry campaign. Ground-based instruments only expose while
    /// their site can actually see the target -- Sun below twilight, target
    /// above the telescope limit (ImagingObservingConditions, the same gate the
    /// imaging path uses) -- and their per-point noise carries the
    /// airmass-dependent scintillation excess. Space-based instruments (TESS)
    /// keep the continuous coverage that is precisely their real-world selling
    /// point. The resulting diurnal gaps give ground-based data an honest
    /// window function, aliases and all, exactly the artifact real BLS searches
    /// fight.
    /// </summary>
    public class ObservationSession
    {
        public StarTarget Target { get; private set; }

        /// <summary>
        /// Every catalog planet sharing this target's host, target first --
        /// photometry observes the star, so a compact system's transits
        /// superpose on the one light curve whether the player asked or not
        /// (same reasoning as RvObservationSession.SystemPlanets).
        /// </summary>
        public List<StarTarget> SystemPlanets { get; private set; }

        public InstrumentSpec Instrument { get; private set; }
        public double StartUt { get; private set; }
        public double LastSampleUt { get; private set; }
        public List<FluxSample> Samples { get; private set; }
        public bool IsRunning { get; private set; }

        /// <summary>Conditions at the last tick, for the UI status line. Space-based sessions report a synthetic always-observable state.</summary>
        public ImagingConditionsSnapshot CurrentConditions { get; private set; }

        private readonly ImagingObserverContext observer;
        private readonly Random _rng;

        // Per-planet transit-timing variations, parallel to SystemPlanets --
        // deterministic in the catalog, so computed once at session start.
        private readonly TransitTimingVariations.TtvSignal[] ttvSignals;

        // Next UT at which an exposure is attempted. Not a rigid grid: when a
        // slot lands in daytime the anchor slides forward in sub-cadence steps
        // until the sky opens, so a cadence commensurate with the day length
        // (e.g. 6h on 6h-day Kerbin) can never lock every slot into daylight.
        private double nextSampleUt;

        public ObservationSession(StarTarget target, List<StarTarget> systemPlanets, InstrumentSpec instrument, double startUt, ImagingObserverContext observerContext)
        {
            Target = target;
            SystemPlanets = systemPlanets != null && systemPlanets.Count > 0
                ? systemPlanets
                : new List<StarTarget> { target };
            Instrument = instrument;
            observer = observerContext;
            StartUt = startUt;
            LastSampleUt = startUt;
            Samples = new List<FluxSample>();
            IsRunning = true;
            _rng = new Random();
            ttvSignals = new TransitTimingVariations.TtvSignal[SystemPlanets.Count];
            for (int i = 0; i < SystemPlanets.Count; i++)
            {
                ttvSignals[i] = TransitTimingVariations.ComputeSignal(SystemPlanets[i], SystemPlanets);
            }
            nextSampleUt = startUt + instrument.CadenceSeconds;
            CurrentConditions = SnapshotAt(startUt);
        }

        // Caps how many search/sample steps a single Tick can process. High time
        // warp can advance currentUt by days or years between consecutive Update()
        // calls; without a cap, a target with long unobservable stretches (daytime,
        // below the altitude limit) forces this loop to grind through millions of
        // 60s-minimum search steps synchronously on the main thread in one frame --
        // a multi-second hitch that reads as "the game froze, no new points, elapsed
        // stopped" (exactly what stopping and restarting warp "fixes": it's not
        // fixing anything, it's just giving the stalled frame a chance to finish).
        // Instead, catch-up work is spread across as many Update() frames as it
        // takes -- nextSampleUt is left wherever the budget ran out, and the next
        // Tick call picks up right there.
        private const int MaxStepsPerTick = 20000;

        public void Tick(double currentUt)
        {
            if (!IsRunning) return;

            double cadenceSeconds = Instrument.CadenceSeconds;
            double searchStepSeconds = Math.Max(60.0, cadenceSeconds / 8.0);

            int steps = 0;
            while (nextSampleUt <= currentUt && steps < MaxStepsPerTick)
            {
                ImagingConditionsSnapshot conditions = SnapshotAt(nextSampleUt);
                if (conditions.Observable)
                {
                    double flux = LightCurveSimulator.GenerateSystemFluxAtTime(
                        SystemPlanets, ttvSignals, Instrument, nextSampleUt, _rng, conditions.Airmass, conditions.MoonSkyFactor);
                    double uncertaintyFlux = LightCurveSimulator.TotalNoiseSigma(Target, Instrument, conditions.Airmass, conditions.MoonSkyFactor);
                    Samples.Add(new FluxSample(nextSampleUt, flux, uncertaintyFlux));
                    LastSampleUt = nextSampleUt;
                    nextSampleUt += cadenceSeconds;
                }
                else
                {
                    nextSampleUt += searchStepSeconds;
                }
                steps++;
            }

            CurrentConditions = SnapshotAt(currentUt);
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
