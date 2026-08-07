using System;
using System.Collections.Generic;
using ExoInstruments.Core;
using ExoInstruments.Flight;
using UnityEngine;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// Where the telescope currently being used actually IS: a fixed site on the home body, or a
    /// spacecraft in orbit.
    ///
    /// WHY ONE OBJECT AND NOT A BOOLEAN ON THE CAMERA. Everything the imaging pipeline computes
    /// about geometry runs off the observer's position: the target's distance and phase angle,
    /// its angular diameter, which way is up, what the sky background is, and where the render
    /// camera goes. That position used to be one expression, ObservatorySite.WorldPosition, hard
    /// against the home body's surface, repeated at every site that needed it. Adding an orbiting
    /// telescope means adding a second answer to that one question, so the question is asked in
    /// one place instead.
    ///
    /// The ground path is unchanged by construction: with no space telescope selected, every
    /// method here returns exactly what the call site it replaced returned.
    /// </summary>
    public static class ObservingPlatform
    {
        /// <summary>The orbiting telescope in use, or null when observing from the ground site.</summary>
        public static SpaceTelescopeLink ActiveSpaceTelescope { get; private set; }

        /// <summary>True while an orbiting telescope is selected.</summary>
        public static bool IsSpaceBased => ActiveSpaceTelescope != null;

        /// <summary>Selects an orbiting telescope. Passing null returns to the ground site.</summary>
        public static void SetSpaceTelescope(SpaceTelescopeLink link)
        {
            ActiveSpaceTelescope = link;
        }

        /// <summary>Returns to the ground observatory.</summary>
        public static void SetGround() => ActiveSpaceTelescope = null;

        /// <summary>
        /// The body the observer is attached to: the one it stands on, or the one it orbits.
        ///
        /// These are the same body in the ordinary case of a telescope in orbit around the home
        /// world, and they are deliberately not assumed to be: a telescope parked at a Lagrange
        /// point or in orbit around another planet is a thing a player can build, and everything
        /// downstream that asks "which body might occult this" has to be told the real answer.
        /// </summary>
        public static CelestialBody HostBody
        {
            get
            {
                SpaceTelescopeLink link = ActiveSpaceTelescope;
                if (link != null && link.Vessel != null && link.Vessel.mainBody != null)
                    return link.Vessel.mainBody;
                return FlightGlobals.GetHomeBody();
            }
        }

        /// <summary>
        /// The observer's world position. The single replacement for every
        /// ObservatorySite.WorldPosition(home) call the imaging pipeline used to make.
        /// </summary>
        public static Vector3d WorldPosition(CelestialBody home)
        {
            SpaceTelescopeLink link = ActiveSpaceTelescope;
            if (link != null && link.Vessel != null) return link.Vessel.GetWorldPos3D();
            return home != null ? ObservatorySite.WorldPosition(home) : Vector3d.zero;
        }

        /// <summary>The observer's position in KSP's scaled space, which is where the render cameras live.</summary>
        public static Vector3 ScaledSpacePosition(CelestialBody home)
        {
            return ScaledSpace.LocalToScaledSpace(WorldPosition(home));
        }

        /// <summary>
        /// Builds the pure-Core context the orbital visibility and sky models need, from the live
        /// game. Returns false when there is no space telescope selected or the geometry cannot
        /// be resolved, in which case nothing orbital applies and the ground path stands.
        /// </summary>
        public static bool TryBuildContext(SkyVector lineOfSight, out SpaceObserverContext ctx)
        {
            ctx = default(SpaceObserverContext);
            SpaceTelescopeLink link = ActiveSpaceTelescope;
            if (link == null || link.Vessel == null) return false;

            CelestialBody host = link.Vessel.mainBody;
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (host == null || sun == null) return false;

            Vector3d observer = link.Vessel.GetWorldPos3D();
            Vector3d fromHost = observer - host.position;
            Vector3d sunFromHost = sun.position - host.position;
            Vector3d sunFromObserver = sun.position - observer;

            // Position, not direction: OrbitalVisibility needs the observer's DISTANCE from the
            // centre to work out the body's angular radius, so this one must keep its magnitude.
            ctx.PositionFromHostBody = ToSkyPosition(fromHost);
            ctx.HostBodyRadiusMeters = host.Radius;
            ctx.HostBodyAlbedo = host.albedo;
            ctx.HostBodySunDistanceMeters = sunFromHost.magnitude;
            ctx.SunFromHostBody = ToSky(sunFromHost);
            ctx.SunFromObserver = ToSky(sunFromObserver);
            ctx.OrbitNormal = link.OrbitNormal();
            ctx.OrbitPeriodSeconds = link.OrbitPeriodSeconds;
            ctx.Moons = BuildMoons(host, observer);

            // The ecliptic is the HOME body's orbital plane, whatever body this telescope
            // happens to be orbiting (see EclipticFrame): the zodiacal cloud belongs to the
            // system, not to the spacecraft's local geometry.
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home != null && home.orbit != null)
            {
                Vector3d normal = home.orbit.GetOrbitNormal();
                if (EclipticFrame.TryCompute(lineOfSight, ToSky(normal), ToSky(sunFromObserver),
                                             out double latDeg, out double lonDeg))
                {
                    ctx.TargetEclipticLatitudeDeg = latDeg;
                    ctx.TargetHeliocentricLongitudeDeg = lonDeg;
                    ctx.HasEclipticCoordinates = true;
                }
            }

            return true;
        }

        /// <summary>
        /// The host body's natural satellites, as the avoidance check needs them. Only bodies
        /// that are actually up there: a moon on the far side of its primary is behind the
        /// planet and cannot constrain a pointing the planet is already blocking.
        /// </summary>
        private static SpaceMoonContext[] BuildMoons(CelestialBody host, Vector3d observer)
        {
            if (host == null || host.orbitingBodies == null || host.orbitingBodies.Count == 0)
                return null;

            var moons = new List<SpaceMoonContext>(host.orbitingBodies.Count);
            for (int i = 0; i < host.orbitingBodies.Count; i++)
            {
                CelestialBody moon = host.orbitingBodies[i];
                if (moon == null) continue;

                Vector3d toMoon = moon.position - observer;
                double distance = toMoon.magnitude;
                if (distance < 1.0) continue;

                moons.Add(new SpaceMoonContext
                {
                    Name = moon.bodyName,
                    DirectionFromObserver = ToSky(toMoon),
                    AngularRadiusDeg = OrbitalVisibility.AngularRadiusDeg(moon.Radius, distance),
                });
            }
            return moons.Count > 0 ? moons.ToArray() : null;
        }

        /// <summary>
        /// The frame the star catalogue lives in, snapshot at the current instant, for turning a
        /// live world direction into RA/Dec. The exact inverse of the camera's
        /// TryEquatorialDirection chain (equatorial to horizontal to the GROUND site's world
        /// basis), so a body plotted on the chart through this lands exactly where the camera
        /// will point when it is clicked, whatever platform is observing.
        /// </summary>
        public struct EquatorialFrameSnapshot
        {
            public Vector3d North, East, Up;
            public double MeridianRaDeg;
            public double LatitudeDeg;

            public void WorldToEquatorial(Vector3d worldDirection, out double raDeg, out double decDeg)
            {
                Vector3d d = worldDirection.normalized;
                double n = Vector3d.Dot(d, North);
                double e = Vector3d.Dot(d, East);
                double u = Vector3d.Dot(d, Up);
                double altDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, u))) * (180.0 / Math.PI);
                double azDeg = (Math.Atan2(e, n) * (180.0 / Math.PI) + 360.0) % 360.0;
                SkyCoordinates.HorizontalToEquatorial(altDeg, azDeg, MeridianRaDeg, LatitudeDeg,
                                                      out raDeg, out decDeg);
            }

            /// <summary>The same direction as a unit vector in the equatorial frame's own axes (x toward RA 0, z toward the pole).</summary>
            public SkyVector WorldToEquatorialVector(Vector3d worldDirection)
            {
                WorldToEquatorial(worldDirection, out double raDeg, out double decDeg);
                return SkyChartProjection.DirectionFromEquatorial(raDeg, decDeg);
            }
        }

        public static bool TryGetEquatorialFrame(double ut, out EquatorialFrameSnapshot frame)
        {
            frame = default(EquatorialFrameSnapshot);
            if (!SolarSystemCameraTexture.TryBuildSiteBasis(out Vector3d north, out Vector3d east,
                    out Vector3d up, out double latitudeDeg, out double longitudeDeg)) return false;

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return false;

            frame.North = north;
            frame.East = east;
            frame.Up = up;
            frame.LatitudeDeg = latitudeDeg;
            frame.MeridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                ut, home.rotationPeriod, home.initialRotation, longitudeDeg);
            return true;
        }

        /// <summary>A world vector as a Core SkyVector, normalised. Core carries no Unity types, which is what keeps the physics testable outside the game.</summary>
        public static SkyVector ToSky(Vector3d v) => SkyVector.Normalized(v.x, v.y, v.z);

        /// <summary>A world vector as a Core SkyVector, keeping its magnitude: for positions rather than directions.</summary>
        public static SkyVector ToSkyPosition(Vector3d v) => new SkyVector(v.x, v.y, v.z);
    }
}
