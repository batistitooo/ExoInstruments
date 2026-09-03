using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Everything the imaging session needs to know about the observer: where it
    /// sits, how the home body spins, and where the home body is on its orbit
    /// (which fixes the Sun's apparent position in the same fictional sky frame
    /// SkyCoordinates uses). Filled from FlightGlobals by the GUI layer; this
    /// module stays pure C#.
    /// </summary>
    public struct ImagingObserverContext
    {
        public double LatitudeDeg;
        public double LongitudeDeg;
        public double BodyRotationPeriodSeconds;
        public double BodyInitialRotationDeg;

        /// <summary>False when the home body has no orbit (degenerate save/system); day/night gating is skipped.</summary>
        public bool HasSunOrbit;
        public double SunOrbitPeriodSeconds;      // home body's orbital period around the Sun
        public double SunMeanAnomalyAtEpochRad;   // home body's orbit, radians (KSP convention)
        public double SunOrbitEpochUt;
        public double SunLanPlusArgPeDeg;         // LAN + argument of periapsis (0 for stock Kerbin)

        /// <summary>The home body's natural satellites (Mün, Minmus, ...), for occultation and moonlit-sky pollution. Null/empty = no moons modeled.</summary>
        public MoonContext[] Moons;
    }

    /// <summary>Observing conditions at one instant, as evaluated for one target.</summary>
    public struct ImagingConditionsSnapshot
    {
        public double SunAltitudeDeg;
        public bool HasTargetCoordinates;
        public double TargetAltitudeDeg;   // meaningless when HasTargetCoordinates is false

        public bool IsNight;               // Sun below the twilight limit
        public bool TargetUp;              // target above the telescope's altitude limit
        public bool Observable;            // night, target up, and not occulted

        /// <summary>sec(z) at the target's altitude; PositiveInfinity when the target is at/below the horizon.</summary>
        public double Airmass;

        /// <summary>
        /// Fraction of wall-clock time that counts as effective on-sky integration,
        /// relative to a zenith observation: 0 when not observable, else 1/airmass^2
        /// (see ImagingObservingConditions for the derivation).
        /// </summary>
        public double Efficiency;

        /// <summary>
        /// Aggregate moonlit-sky factor along this line of sight: sqrt of the
        /// scattered-moonlight sky excess relative to a full Mün at 30 deg
        /// separation (see MoonlightPollution). 0 = moonless sky. Feeds the
        /// photometric noise budget only; RV and H-band imaging don't pay it.
        /// </summary>
        public double MoonSkyFactor;

        /// <summary>True when a moon's disk sits over the target; nothing gets through, any method, any instrument.</summary>
        public bool OccultedByMoon;
        public string OccultingMoonName;

        /// <summary>The single moon contributing most sky pollution right now (name null when no moon is up), for the UI status line.</summary>
        public MoonlightPollution.MoonState DominantMoon;
    }

    /// <summary>
    /// Ground-based observing gates used by all sessions:
    /// - Night: Sun below -12 deg (nautical twilight).
    /// - Altitude: target above 20 deg (airmass below ~3).
    /// - Airmass weighting: SNR^2 accumulates at 1/X^2 (one hour at X=2 ~ 15 min at zenith).
    /// Weather is excluded by design: random dome closures are frustrating, not educational.
    /// </summary>
    public static class ImagingObservingConditions
    {
        public const double TwilightSunAltitudeDeg = -12.0;
        public const double MinTelescopeAltitudeDeg = 20.0;

        /// <summary>Fallback altitude for targets with no catalog RA/Dec; the catalog gap is ours, not the player's fault.</summary>
        public const double FallbackAltitudeDeg = 45.0;

        /// <summary>Sun's RA in the fictional sky frame (home body orbital angle + 180 deg). Circular orbit, exact for stock Kerbin.</summary>
        public static double ComputeSunRaDeg(double ut, ImagingObserverContext ctx)
        {
            double meanAnomalyRad = ctx.SunMeanAnomalyAtEpochRad
                + 2.0 * Math.PI * (ut - ctx.SunOrbitEpochUt) / ctx.SunOrbitPeriodSeconds;
            return NormalizeDegrees(meanAnomalyRad * 180.0 / Math.PI + ctx.SunLanPlusArgPeDeg + 180.0);
        }

        public static ImagingConditionsSnapshot Evaluate(double ut, double? targetRaDeg, double? targetDecDeg, ImagingObserverContext ctx)
        {
            var s = new ImagingConditionsSnapshot();

            double meridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                ut, ctx.BodyRotationPeriodSeconds, ctx.BodyInitialRotationDeg, ctx.LongitudeDeg);

            bool hasSun = ctx.HasSunOrbit && ctx.SunOrbitPeriodSeconds > 0;
            double sunRaDeg = 0.0;
            if (hasSun)
            {
                sunRaDeg = ComputeSunRaDeg(ut, ctx);
                // Dec 0: stock KSP bodies have no axial tilt, so no seasons.
                var sun = SkyCoordinates.EquatorialToHorizontal(sunRaDeg, 0.0, meridianRaDeg, ctx.LatitudeDeg);
                s.SunAltitudeDeg = sun.AltitudeDeg;
                s.IsNight = sun.AltitudeDeg < TwilightSunAltitudeDeg;
            }
            else
            {
                // No orbit on record: can't place the Sun, so default to permanent night.
                s.SunAltitudeDeg = -90.0;
                s.IsNight = true;
            }

            if (targetRaDeg.HasValue && targetDecDeg.HasValue)
            {
                s.HasTargetCoordinates = true;
                var t = SkyCoordinates.EquatorialToHorizontal(targetRaDeg.Value, targetDecDeg.Value, meridianRaDeg, ctx.LatitudeDeg);
                s.TargetAltitudeDeg = t.AltitudeDeg;
            }
            else
            {
                s.HasTargetCoordinates = false;
                s.TargetAltitudeDeg = FallbackAltitudeDeg;
            }

            s.TargetUp = s.TargetAltitudeDeg >= MinTelescopeAltitudeDeg;

            // Moon geometry is only evaluated when both other gates pass; it's expensive
            // enough per frame to stall sample collection under time warp otherwise.
            if (s.IsNight && s.TargetUp && targetRaDeg.HasValue && targetDecDeg.HasValue)
            {
                MoonlightPollution.Evaluate(
                    ut, ctx.Moons, sunRaDeg, hasSun,
                    targetRaDeg.Value, targetDecDeg.Value,
                    meridianRaDeg, ctx.LatitudeDeg,
                    out double moonSkyFactor, out bool occulted, out string occultingMoon,
                    out MoonlightPollution.MoonState dominantMoon);
                s.MoonSkyFactor = moonSkyFactor;
                s.OccultedByMoon = occulted;
                s.OccultingMoonName = occultingMoon;
                s.DominantMoon = dominantMoon;
            }

            s.Airmass = AirmassAt(s.TargetAltitudeDeg);
            s.Observable = s.IsNight && s.TargetUp && !s.OccultedByMoon;
            s.Efficiency = s.Observable ? 1.0 / (s.Airmass * s.Airmass) : 0.0;
            return s;
        }

        /// <summary>
        /// Airmass, from Kasten and Young (1989, Applied Optics 28, 4735).
        ///
        ///     X = 1 / [ sin(h) + 0.50572 (h + 6.07995)^-1.6364 ],   h in degrees
        ///
        /// WHY NOT sec(z). This used to be the plane-parallel 1/sin(h), whose own comment claimed
        /// accuracy better than 1% above the 20 degree telescope floor. Measured, it is 0.69% at
        /// 20 degrees and the error grows fast below that: sec(z) treats the atmosphere as a flat
        /// slab, so it diverges at the horizon where the real airmass tops out near 38. Kasten and
        /// Young fit the real refracting, curved atmosphere and stay within 0.1% of it all the way
        /// down to the horizon, at the cost of one power.
        ///
        /// This term multiplies every extinction and sky-brightness figure in a frame, so it is
        /// worth having right rather than nearly right, and it removes a floor on how low the
        /// telescope can be pointed before the model stops meaning anything.
        /// </summary>
        public static double AirmassAt(double altitudeDeg)
        {
            if (altitudeDeg <= 0.0) return double.PositiveInfinity;
            double h = altitudeDeg;
            return 1.0 / (Math.Sin(h * Math.PI / 180.0)
                          + 0.50572 * Math.Pow(h + 6.07995, -1.6364));
        }

        /// <summary>Maximum altitude this declination ever reaches from this latitude (at meridian transit).</summary>
        public static double MaxTargetAltitudeDeg(double targetDecDeg, double observerLatitudeDeg)
        {
            return 90.0 - Math.Abs(targetDecDeg - observerLatitudeDeg);
        }

        private static double NormalizeDegrees(double deg)
        {
            double d = deg % 360.0;
            return d < 0 ? d + 360.0 : d;
        }
    }
}
