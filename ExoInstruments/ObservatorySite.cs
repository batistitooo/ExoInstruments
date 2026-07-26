using System;
using ExoInstruments.Core;
using UnityEngine;

namespace ExoInstruments
{
    /// <summary>
    /// Where the observatory actually stands, resolved from the running game rather than assumed.
    ///
    /// Every ground-based quantity in this mod is anchored to the observer's position: which stars
    /// are ever above the horizon and for how long (SkyCoordinates), the airmass and extinction
    /// along the line of sight, the geometry of moonlight scattering, and the diurnal drift rate
    /// that trails an unguided exposure. All of it was previously hardcoded to stock KSP's Kerbal
    /// Space Center at latitude -0.0972 deg, which is correct only for stock.
    ///
    /// A planet pack that relocates the space centre -- Real Solar System puts it at Cape
    /// Canaveral, 28.6 deg N -- changes every one of those answers. At the equator the whole
    /// celestial sphere is reachable and everything rises perpendicular to the horizon; at 28.6 deg
    /// N the south celestial pole is permanently invisible, circumpolar northern targets never set,
    /// and airmass at a given hour angle is different for every declination. Hardcoding one
    /// latitude silently produced the wrong sky for anyone not running stock.
    ///
    /// This resolves the real position instead, from KSP's own SpaceCenter object -- whose
    /// latitude/longitude are derived from the actual space-centre transform on the actual host
    /// body (verified by decompiling Assembly-CSharp: SpaceCenter.Start() reads
    /// cb.GetLatitudeAndLongitude(transform.position)). That makes this correct for stock, for
    /// RSS, and for any other pack that moves or replaces the home world, with no per-pack special
    /// casing and no dependency on any of them being installed.
    /// </summary>
    public static class ObservatorySite
    {
        /// <summary>Stock KSP's Kerbal Space Center, used only when the running game can't be asked (see Resolve).</summary>
        public const double StockKscLatitudeDeg = -0.0972;
        public const double StockKscLongitudeDeg = -74.6002;

        /// <summary>Elevation above the datum used when converting the site to a world position. The instrument's own real site altitude is a separate, physical quantity (VisualTelescopeSpec.SiteAltitudeMeters) and is not this.</summary>
        public const double SiteElevationMeters = 100.0;

        private static bool resolved;
        private static double latitudeDeg = StockKscLatitudeDeg;
        private static double longitudeDeg = StockKscLongitudeDeg;
        private static bool fromGame;

        /// <summary>Observer latitude in degrees, positive north.</summary>
        public static double LatitudeDeg { get { Resolve(); return latitudeDeg; } }

        /// <summary>Observer longitude in degrees, in the home body's own frame.</summary>
        public static double LongitudeDeg { get { Resolve(); return longitudeDeg; } }

        /// <summary>True once the position came from the running game rather than the stock fallback.</summary>
        public static bool ResolvedFromGame { get { Resolve(); return fromGame; } }

        /// <summary>
        /// Resolves the site once and remembers it. KSP's SpaceCenter object only populates its
        /// coordinates in scenes where it exists, so a value obtained once is kept rather than
        /// re-queried -- the space centre does not move during a game, and falling back to stock
        /// coordinates mid-session on a modded install would be worse than a stale-but-correct one.
        /// </summary>
        private static void Resolve()
        {
            if (resolved) return;

            try
            {
                SpaceCenter sc = SpaceCenter.Instance;
                if (sc != null && sc.cb != null)
                {
                    // Guard against a degenerate transform before SpaceCenter.Start() has run:
                    // exactly (0,0) is not a plausible space-centre position on any pack.
                    if (Math.Abs(sc.Latitude) > 1e-6 || Math.Abs(sc.Longitude) > 1e-6)
                    {
                        latitudeDeg = sc.Latitude;
                        longitudeDeg = sc.Longitude;
                        fromGame = true;
                        resolved = true;
                        Debug.Log($"[ExoInstruments] Observatory site resolved from the running game: "
                                + $"{FormatLatitude(latitudeDeg)} {FormatLongitude(longitudeDeg)} on {sc.cb.bodyName}.");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ExoInstruments] Could not read the space centre's position, falling back to stock KSC: " + e.Message);
            }

            // Not resolved: leave the stock fallback in place and try again next time, since a
            // later scene may have the SpaceCenter object even if this one didn't.
        }

        /// <summary>Forces the next query to re-resolve. For a scene change that may bring the space-centre object into existence.</summary>
        public static void Invalidate()
        {
            if (!fromGame) resolved = false;
        }

        /// <summary>The observer's world-space position on the home body, the anchor for every line-of-sight calculation in the imaging pipeline.</summary>
        public static Vector3d WorldPosition(CelestialBody homeBody)
        {
            if (homeBody == null) return Vector3d.zero;
            return homeBody.GetWorldSurfacePosition(LatitudeDeg, LongitudeDeg, SiteElevationMeters);
        }

        /// <summary>Player-facing one-liner: where the telescope stands and on what.</summary>
        public static string Describe()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            string body = home != null ? home.bodyName : "the home world";
            string source = ResolvedFromGame ? "" : " (stock default -- space centre position unavailable)";
            return $"{FormatLatitude(LatitudeDeg)} {FormatLongitude(LongitudeDeg)} on {body}{source}";
        }

        private static string FormatLatitude(double deg)
            => $"{Math.Abs(deg):F2}°{(deg >= 0 ? "N" : "S")}";

        private static string FormatLongitude(double deg)
        {
            double d = NormalizeSigned(deg);
            return $"{Math.Abs(d):F2}°{(d >= 0 ? "E" : "W")}";
        }

        private static double NormalizeSigned(double deg)
        {
            double d = deg % 360.0;
            if (d < 0) d += 360.0;
            return d > 180.0 ? d - 360.0 : d;
        }
    }
}
