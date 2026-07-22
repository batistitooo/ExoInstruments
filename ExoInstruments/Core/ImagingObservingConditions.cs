using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Everything the imaging session needs to know about the observer: where it
    /// sits, how the home body spins, and where the home body is on its orbit
    /// (which fixes the Sun's apparent position in the same fictional sky frame
    /// SkyCoordinates uses). Filled from FlightGlobals by the GUI layer -- this
    /// module stays pure C#.
    /// </summary>
    public struct ImagingObserverContext
    {
        public double LatitudeDeg;
        public double LongitudeDeg;
        public double BodyRotationPeriodSeconds;
        public double BodyInitialRotationDeg;

        /// <summary>False when the home body has no orbit (degenerate save/system) -- day/night gating is skipped.</summary>
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
        /// photometric noise budget only -- RV and H-band imaging don't pay it.
        /// </summary>
        public double MoonSkyFactor;

        /// <summary>True when a moon's disk sits over the target -- nothing gets through, any method, any instrument.</summary>
        public bool OccultedByMoon;
        public string OccultingMoonName;

        /// <summary>The single moon contributing most sky pollution right now (name null when no moon is up), for the UI status line.</summary>
        public MoonlightPollution.MoonState DominantMoon;
    }

    /// <summary>
    /// Ground-based observing constraints, shared by every ground-based
    /// instrument: the imaging session integrates through them continuously,
    /// and the transit/RV sessions gate their exposures on the same
    /// Observable flag (space-based instruments skip the gate entirely):
    ///
    /// - Night only: science integration requires the Sun below -12 deg (nautical
    ///   twilight -- near-IR AO genuinely starts earlier than visible-light work,
    ///   so -12 rather than the astronomical -18).
    /// - Altitude limit: AO needs the target above ~20 deg (airmass < 3); below
    ///   that the wavefront correction collapses. Real ELT-class AO proposals are
    ///   typically capped near airmass 2.
    /// - Airmass weighting: the Fried parameter degrades as r0 ~ X^(-3/5)
    ///   (Kolmogorov turbulence along a slanted path), so the post-AO residual
    ///   wavefront variance -- and with it the speckle-halo contrast floor --
    ///   scales roughly linearly with airmass X. SNR goes as contrast/floor, so
    ///   the SNR^2 accumulation rate scales as X^-2: one hour at airmass 2 is
    ///   worth 15 minutes at zenith. Effective time dt_eff = dt / X^2.
    /// - Weather is deliberately excluded (design decision: random dome closures
    ///   are frustration, not education).
    ///
    /// The Sun's sky position reuses the mod's fictional RA/Dec frame: the home
    /// body's orbital angle (circular approximation -- exact for stock Kerbin,
    /// e = 0) plus 180 deg gives the Sun's RA; declination is 0 because stock
    /// bodies have no axial tilt. Both the Sun and the star catalog then go
    /// through the same SkyCoordinates meridian, so sunset here is consistent
    /// with the sky chart, and -- since initialRotation and the orbital elements
    /// share KSP's inertial reference frame -- with the rendered lighting too.
    /// </summary>
    public static class ImagingObservingConditions
    {
        public const double TwilightSunAltitudeDeg = -12.0;
        public const double MinTelescopeAltitudeDeg = 20.0;

        /// <summary>
        /// Targets with no catalog RA/Dec can't be tracked across the sky --
        /// they integrate at this assumed altitude whenever it's night, rather
        /// than being unobservable (the catalog gap is ours, not the player's).
        /// </summary>
        public const double FallbackAltitudeDeg = 45.0;

        /// <summary>Sun's RA in the fictional sky frame: orbital angle of the home body + 180 deg. Circular-orbit approximation (exact for stock Kerbin).</summary>
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
                // Dec 0: stock KSP bodies have no axial tilt, so the Sun rides the
                // celestial equator all year -- no seasons in the day/night cycle.
                var sun = SkyCoordinates.EquatorialToHorizontal(sunRaDeg, 0.0, meridianRaDeg, ctx.LatitudeDeg);
                s.SunAltitudeDeg = sun.AltitudeDeg;
                s.IsNight = sun.AltitudeDeg < TwilightSunAltitudeDeg;
            }
            else
            {
                // No orbit on record for the home body: can't place the Sun, so
                // don't pretend to -- permanent night, the old idealization.
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

            // Moon geometry (occultation + sky-pollution kernel, a handful of
            // trig/Pow calls per moon) only ever changes the outcome when the
            // other two gates already pass -- daytime or below-the-limit
            // candidates are unobservable regardless of the Mun's position.
            // Sessions probe thousands of candidate instants per Tick while
            // searching through an unobservable stretch under time warp, so
            // skipping this block there (instead of paying it on every single
            // candidate) is what keeps that search loop from falling behind
            // the warp clock -- previously it didn't, and long warps could
            // starve sample collection.
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
        /// Plane-parallel airmass sec(z). Diverges at the horizon where the real
        /// curved-atmosphere value stays finite (~38), but the telescope limit
        /// keeps us above 20 deg where sec(z) is accurate to better than 1%.
        /// </summary>
        public static double AirmassAt(double altitudeDeg)
        {
            if (altitudeDeg <= 0.0) return double.PositiveInfinity;
            return 1.0 / Math.Sin(altitudeDeg * Math.PI / 180.0);
        }

        /// <summary>Highest altitude this declination ever reaches from this latitude (at meridian transit).</summary>
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
