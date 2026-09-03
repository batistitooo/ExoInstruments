using System;

namespace ExoInstruments.Core
{
    /// <summary>Where the observer is and what is around it, at one instant. All positions in metres, all in one inertial frame.</summary>
    public struct SpaceObserverContext
    {
        /// <summary>Observer position relative to the centre of the body it orbits.</summary>
        public SkyVector PositionFromHostBody;

        /// <summary>Radius of that body, metres.</summary>
        public double HostBodyRadiusMeters;

        /// <summary>Its geometric albedo, for the scattered-light term (see Earthshine).</summary>
        public double HostBodyAlbedo;

        /// <summary>Its distance from the Sun, metres, which sets how brightly its limb shines.</summary>
        public double HostBodySunDistanceMeters;

        /// <summary>Direction from the host body's centre toward the Sun. Decides which limb is lit.</summary>
        public SkyVector SunFromHostBody;

        /// <summary>Direction from the OBSERVER toward the Sun, for the solar avoidance angle.</summary>
        public SkyVector SunFromObserver;

        /// <summary>Unit normal of the observer's orbital plane, for the continuous-viewing calculation. Zero when unknown.</summary>
        public SkyVector OrbitNormal;

        /// <summary>Orbital period, seconds, so a visibility fraction can be reported as minutes. Zero when unknown.</summary>
        public double OrbitPeriodSeconds;

        /// <summary>Natural satellites of the host body, for the moon avoidance angle. Null or empty for none.</summary>
        public SpaceMoonContext[] Moons;

        /// <summary>The target's ecliptic latitude, degrees, and its heliocentric ecliptic longitude, for the zodiacal light.</summary>
        public double TargetEclipticLatitudeDeg;
        public double TargetHeliocentricLongitudeDeg;
        public bool HasEclipticCoordinates;
    }

    /// <summary>One natural satellite, as the avoidance check needs it.</summary>
    public struct SpaceMoonContext
    {
        public string Name;
        /// <summary>Direction from the observer toward the moon.</summary>
        public SkyVector DirectionFromObserver;
        /// <summary>Its angular radius from here, degrees, so a close moon is avoided by its edge and not its centre.</summary>
        public double AngularRadiusDeg;
    }

    /// <summary>What an orbiting telescope may do with one line of sight at one instant.</summary>
    public struct SpaceConditionsSnapshot
    {
        /// <summary>Geometry of the host body against this line of sight.</summary>
        public LimbGeometry Host;

        public double SunAngleDeg;
        public double NearestMoonAngleDeg;
        public string NearestMoonName;

        public bool OccultedByHost;
        public bool InsideLimbAvoidance;
        public bool InsideSunAvoidance;
        public bool InsideMoonAvoidance;

        /// <summary>True when every constraint is satisfied and the exposure may start.</summary>
        public bool Observable;

        /// <summary>Why not, in one phrase, for the UI. Null when observable.</summary>
        public string BlockingConstraint;

        /// <summary>Total sky surface brightness on this line of sight, V mag/arcsec^2.</summary>
        public double SkyVMagPerArcsec2;

        /// <summary>Its two components, separately, because they behave differently and the UI says so.</summary>
        public double ZodiacalVMagPerArcsec2;
        public double EarthshineVMagPerArcsec2;

        /// <summary>False when the zodiacal figure came from an extrapolation rather than the published grid.</summary>
        public bool ZodiacalIsPublished;

        /// <summary>Fraction of one orbit this target spends blocked, counting the limb avoidance angle. NaN when the orbit is unknown.</summary>
        public double OccultedOrbitFraction;

        /// <summary>Longest uninterrupted exposure this orbit allows, seconds. PositiveInfinity in the continuous viewing zone.</summary>
        public double MaxContiguousExposureSeconds;
    }

    /// <summary>
    /// The orbital counterpart of ImagingObservingConditions: the gate an exposure from space
    /// has to pass, and the sky it is taken against.
    ///
    /// The two modules answer the same question and share no code, because they share no
    /// physics. A ground telescope waits for the Sun to set and the target to rise. An orbiting
    /// one waits for the planet to get out of the way, and then has to still be pointing far
    /// enough from that planet's sunlit edge, from the Sun, and from any moon. Nothing carries
    /// over, and pretending otherwise by generalising one into the other would put an airmass on
    /// a spacecraft.
    ///
    /// Pure C# with no Unity or KSP dependency, like the rest of Core.
    /// </summary>
    public static class SpaceObservingConditions
    {
        /// <summary>
        /// Evaluates one line of sight. <paramref name="lineOfSight"/> is the unit direction the
        /// telescope is pointing, in the same frame as everything in the context.
        /// </summary>
        public static SpaceConditionsSnapshot Evaluate(
            SkyVector lineOfSight, in SpaceObserverContext ctx, SpacePlatformSpec platform)
        {
            var s = new SpaceConditionsSnapshot();
            if (platform == null) platform = new SpacePlatformSpec();

            s.Host = OrbitalVisibility.EvaluateLimb(
                ctx.PositionFromHostBody, lineOfSight, ctx.HostBodyRadiusMeters, ctx.SunFromHostBody);

            s.OccultedByHost = s.Host.Occulted;

            double limbAvoidance = s.Host.LimbIsSunlit
                ? platform.BrightLimbAvoidanceAngleDeg
                : platform.DarkLimbAvoidanceAngleDeg;
            s.InsideLimbAvoidance = !s.OccultedByHost && s.Host.LimbAngleDeg < limbAvoidance;

            s.SunAngleDeg = OrbitalVisibility.SeparationDeg(lineOfSight, ctx.SunFromObserver);
            s.InsideSunAvoidance = s.SunAngleDeg < platform.SunAvoidanceAngleDeg;

            s.NearestMoonAngleDeg = double.PositiveInfinity;
            if (ctx.Moons != null)
            {
                for (int i = 0; i < ctx.Moons.Length; i++)
                {
                    // Measured to the moon's LIMB, not its centre: a large close moon has to be
                    // avoided by its edge, and taking the centre would let the telescope point
                    // straight at a body whose disk it is inside.
                    double edge = OrbitalVisibility.SeparationDeg(lineOfSight, ctx.Moons[i].DirectionFromObserver)
                                - ctx.Moons[i].AngularRadiusDeg;
                    if (edge < s.NearestMoonAngleDeg)
                    {
                        s.NearestMoonAngleDeg = edge;
                        s.NearestMoonName = ctx.Moons[i].Name;
                    }
                }
            }
            s.InsideMoonAvoidance = s.NearestMoonAngleDeg < platform.MoonAvoidanceAngleDeg;

            s.Observable = !s.OccultedByHost && !s.InsideLimbAvoidance
                        && !s.InsideSunAvoidance && !s.InsideMoonAvoidance;

            if (s.OccultedByHost) s.BlockingConstraint = "target occulted";
            else if (s.InsideSunAvoidance) s.BlockingConstraint = "inside solar avoidance";
            else if (s.InsideLimbAvoidance)
                s.BlockingConstraint = s.Host.LimbIsSunlit ? "too near the sunlit limb" : "too near the limb";
            else if (s.InsideMoonAvoidance) s.BlockingConstraint = "too near " + (s.NearestMoonName ?? "a moon");

            ComputeSky(ref s, in ctx);
            ComputeVisibilityWindow(ref s, lineOfSight, in ctx, platform);
            return s;
        }

        private static void ComputeSky(ref SpaceConditionsSnapshot s, in SpaceObserverContext ctx)
        {
            if (ctx.HasEclipticCoordinates)
            {
                s.ZodiacalVMagPerArcsec2 = ZodiacalLight.VMagPerArcsec2(
                    ctx.TargetHeliocentricLongitudeDeg, ctx.TargetEclipticLatitudeDeg,
                    out bool published);
                s.ZodiacalIsPublished = published;
            }
            else
            {
                // No ecliptic frame on record: fall back to the faintest published value, which
                // is what SkyBrightnessModel used everywhere before this table existed. Flagged
                // as unpublished, because at this pointing it is a floor rather than a value.
                s.ZodiacalVMagPerArcsec2 = ZodiacalLight.MinimumVMagPerArcsec2;
                s.ZodiacalIsPublished = false;
            }

            double observerDistance = Length(ctx.PositionFromHostBody);
            double hostScaling = Earthshine.HostBodyScaling(
                ctx.HostBodyAlbedo, ctx.HostBodyRadiusMeters, observerDistance, ctx.HostBodySunDistanceMeters);

            s.EarthshineVMagPerArcsec2 = Earthshine.VMagPerArcsec2(
                s.Host.LimbAngleDeg, s.Host.LimbIsSunlit, hostScaling);

            double flux = SkyBrightnessModel.AddMagnitude(0.0, s.ZodiacalVMagPerArcsec2);
            flux = SkyBrightnessModel.AddMagnitude(flux, s.EarthshineVMagPerArcsec2);
            s.SkyVMagPerArcsec2 = SkyBrightnessModel.FluxToMagPerArcsec2(flux);
        }

        // How much of the orbit this target is available for, and therefore the longest single exposure that
        // can run without the planet cutting it off. The blocking half-angle is the body's own angular radius
        // PLUS the limb avoidance angle, because an exposure ends when the pointing enters the avoidance zone,
        // not when the target finally disappears behind the disk. That distinction is not academic: for HST it
        // is the difference between the 36 minutes of geometric occultation and the roughly 44 minutes STScI
        // actually quotes.
        private static void ComputeVisibilityWindow(ref SpaceConditionsSnapshot s, SkyVector lineOfSight,
                                                    in SpaceObserverContext ctx, SpacePlatformSpec platform)
        {
            s.OccultedOrbitFraction = double.NaN;
            s.MaxContiguousExposureSeconds = double.PositiveInfinity;

            double normalLength = Length(ctx.OrbitNormal);
            if (!(normalLength > 0.0) || !(ctx.OrbitPeriodSeconds > 0.0)) return;

            double blocking = s.Host.AngularRadiusDeg
                            + (s.Host.LimbIsSunlit ? platform.BrightLimbAvoidanceAngleDeg
                                                   : platform.DarkLimbAvoidanceAngleDeg);
            double elevation = OrbitalVisibility.ElevationAboveOrbitPlaneDeg(ctx.OrbitNormal, lineOfSight);
            double fraction = OrbitalVisibility.OccultedOrbitFraction(blocking, elevation);

            s.OccultedOrbitFraction = fraction;
            s.MaxContiguousExposureSeconds = fraction <= 0.0
                ? double.PositiveInfinity
                : (1.0 - fraction) * ctx.OrbitPeriodSeconds;
        }

        private static double Length(SkyVector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }
}
