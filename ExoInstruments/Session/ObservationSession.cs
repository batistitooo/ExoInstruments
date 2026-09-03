using System;
using System.Collections.Generic;
using ExoInstruments.Core;

namespace ExoInstruments.Session
{
    /// <summary>
    /// Transit photometry campaign. Ground-based instruments only expose at night with the
    /// target above the altitude limit; noise carries airmass scintillation. Space-based
    /// (TESS) observes continuously. The diurnal gaps give ground data an honest window
    /// function, exactly the aliasing real BLS searches have to deal with.
    /// </summary>
    public class ObservationSession
    {
        public StarTarget Target { get; private set; }

        /// <summary>
        /// Every planet sharing this host, target first. Photometry observes the star, so all transits
        /// superpose on the same light curve whether or not the player knew about them.
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

        // Per-planet transit-timing variations, parallel to SystemPlanets,
        // deterministic in the catalog, so computed once at session start.
        private readonly TransitTimingVariations.TtvSignal[] ttvSignals;

        // Next UT at which an exposure is attempted. Not a rigid grid: when a
        // slot lands in daytime the anchor slides forward in sub-cadence steps
        // until the sky opens, so a cadence commensurate with the day length
        // (e.g. 6h on 6h-day Kerbin) can never lock every slot into daylight.
        private double nextSampleUt;

        /// <summary>
        /// The seed every noise draw in this campaign came from, so the run can be repeated. See
        /// RvObservationSession.RandomSeed for why this exists.
        /// </summary>
        public int RandomSeed { get; }

        public ObservationSession(StarTarget target, List<StarTarget> systemPlanets, InstrumentSpec instrument, double startUt, ImagingObserverContext observerContext,
            int? randomSeed = null)
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
            RandomSeed = randomSeed ?? new Random().Next();
            _rng = new Random(RandomSeed);
            ttvSignals = new TransitTimingVariations.TtvSignal[SystemPlanets.Count];
            for (int i = 0; i < SystemPlanets.Count; i++)
            {
                ttvSignals[i] = TransitTimingVariations.ComputeSignal(SystemPlanets[i], SystemPlanets);
            }
            nextSampleUt = startUt + instrument.CadenceSeconds;
            CurrentConditions = SnapshotAt(startUt);
        }

        // Prevents a single Tick from grinding through millions of 60s search steps on a
        // long warp jump across an unobservable stretch, which stalls the main thread for seconds.
        // Catch-up spreads across multiple Update() frames; nextSampleUt tracks where we left off.
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
