using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// What a searchable target IS, at the granularity the observer chooses an instrument by.
    ///
    /// The distinctions are the ones that change how a target is observed, not every distinction
    /// the source catalogues draw. A galaxy is a galaxy whether it is a Seyfert or a LINER; the
    /// telescope does the same thing either way, and the finer classification is carried alongside
    /// as the source's own words (see SearchTarget.TypeLabel) rather than being flattened into
    /// this enum. What IS separated is anything that changes the observation: a planetary nebula
    /// is arcseconds across and a supernova remnant is degrees, a dark nebula emits nothing at all,
    /// and a solar-system body moves.
    /// </summary>
    public enum TargetKind
    {
        /// <summary>The system's own star, in whatever planet pack is installed.</summary>
        SolarSystemStar,
        /// <summary>A planet: a body orbiting the star directly.</summary>
        SolarSystemPlanet,
        /// <summary>A moon: a body orbiting a planet.</summary>
        SolarSystemMoon,
        /// <summary>A catalogue star with no known planet.</summary>
        Star,
        /// <summary>A star with at least one known planet.</summary>
        PlanetHost,
        Galaxy,
        /// <summary>H II region: ionised hydrogen around hot young stars.</summary>
        EmissionNebula,
        PlanetaryNebula,
        SupernovaRemnant,
        ReflectionNebula,
        /// <summary>A dust cloud seen in silhouette. Nothing emits, so no emission map can show one.</summary>
        DarkNebula,
        GlobularCluster,
        OpenCluster,
        /// <summary>Catalogued, but not one of the kinds above; its source's own classification is shown instead.</summary>
        Other,
    }

    /// <summary>
    /// Names for the kinds, and the vocabulary the search box accepts for filtering by them.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class TargetKinds
    {
        /// <summary>Every kind, in the order a browsing list should group them: nearest and largest first.</summary>
        public static readonly TargetKind[] All =
        {
            TargetKind.SolarSystemStar, TargetKind.SolarSystemPlanet, TargetKind.SolarSystemMoon,
            TargetKind.PlanetHost, TargetKind.Star,
            TargetKind.EmissionNebula, TargetKind.PlanetaryNebula, TargetKind.SupernovaRemnant,
            TargetKind.ReflectionNebula, TargetKind.DarkNebula,
            TargetKind.GlobularCluster, TargetKind.OpenCluster,
            TargetKind.Galaxy, TargetKind.Other,
        };

        public static string Label(TargetKind kind)
        {
            switch (kind)
            {
                case TargetKind.SolarSystemStar: return "star of this system";
                case TargetKind.SolarSystemPlanet: return "planet";
                case TargetKind.SolarSystemMoon: return "moon";
                case TargetKind.Star: return "star";
                case TargetKind.PlanetHost: return "planet host";
                case TargetKind.Galaxy: return "galaxy";
                case TargetKind.EmissionNebula: return "H II region";
                case TargetKind.PlanetaryNebula: return "planetary nebula";
                case TargetKind.SupernovaRemnant: return "supernova remnant";
                case TargetKind.ReflectionNebula: return "reflection nebula";
                case TargetKind.DarkNebula: return "dark nebula";
                case TargetKind.GlobularCluster: return "globular cluster";
                case TargetKind.OpenCluster: return "open cluster";
                default: return "other";
            }
        }

        /// <summary>
        /// The filter groups the search box offers, in the order they are shown. Each is a word the
        /// player can type after "type:" and also, for the common ones, a button.
        ///
        /// Groups overlap on purpose: "nebula" is every kind of nebula, and "planetary" is one of
        /// them, because both are things an observer means.
        /// </summary>
        public static readonly string[] FilterWords =
        {
            "planet", "moon", "body", "star", "host", "exoplanet",
            "galaxy", "nebula", "emission", "hii", "planetarynebula", "remnant", "supernova",
            "reflection", "dark", "cluster", "globular", "open", "other",
        };

        /// <summary>
        /// The kinds a filter word selects, or null when the word names no group. The comparison is
        /// on the word with spaces and punctuation removed and lowercased, so "planetary nebula",
        /// "planetary-nebula" and "planetarynebula" are one word.
        /// </summary>
        public static TargetKind[] Group(string word)
        {
            if (string.IsNullOrEmpty(word)) return null;
            switch (Singular(TargetDesignations.Loose(word)))
            {
                case "planet":
                    return new[] { TargetKind.SolarSystemPlanet };
                case "moon":
                case "satellite":
                    return new[] { TargetKind.SolarSystemMoon };
                case "body":
                case "solarsystem":
                case "solar":
                    return new[] { TargetKind.SolarSystemStar, TargetKind.SolarSystemPlanet, TargetKind.SolarSystemMoon };
                case "star":
                case "sun":
                    return new[] { TargetKind.Star, TargetKind.PlanetHost, TargetKind.SolarSystemStar };
                case "host":
                case "planethost":
                case "exoplanet":
                case "exoplanethost":
                    return new[] { TargetKind.PlanetHost };
                case "galaxy":
                case "galaxie":     // "galaxies" reaches here after the plural rule below
                    return new[] { TargetKind.Galaxy };
                case "nebula":      // and "nebulas", through the plural rule
                case "nebulae":     // the Latin plural, which that rule cannot reach
                    return new[] { TargetKind.EmissionNebula, TargetKind.PlanetaryNebula,
                                   TargetKind.SupernovaRemnant, TargetKind.ReflectionNebula,
                                   TargetKind.DarkNebula };
                case "emission":
                case "hii":
                case "hiiregion":
                    return new[] { TargetKind.EmissionNebula };
                case "planetarynebula":
                case "planetary":
                    return new[] { TargetKind.PlanetaryNebula };
                case "remnant":
                case "supernova":
                case "supernovaremnant":
                case "snr":
                    return new[] { TargetKind.SupernovaRemnant };
                case "reflection":
                case "reflectionnebula":
                    return new[] { TargetKind.ReflectionNebula };
                case "dark":
                case "darknebula":
                    return new[] { TargetKind.DarkNebula };
                case "cluster":
                    return new[] { TargetKind.GlobularCluster, TargetKind.OpenCluster };
                case "globular":
                case "globularcluster":
                    return new[] { TargetKind.GlobularCluster };
                case "open":
                case "opencluster":
                    return new[] { TargetKind.OpenCluster };
                case "other":
                    return new[] { TargetKind.Other };
                default:
                    return null;
            }
        }

        /// <summary>
        /// English plurals, to the extent this vocabulary needs them: a trailing "s" is dropped
        /// unless the word ends in "ss". Nothing here is irregular except "nebulae", which is
        /// listed as its own case above.
        /// </summary>
        private static string Singular(string word)
        {
            if (word.Length > 2 && word[word.Length - 1] == 's' && word[word.Length - 2] != 's')
                return word.Substring(0, word.Length - 1);
            return word;
        }

        /// <summary>
        /// The kind a SIMBAD object-type code means. SIMBAD's vocabulary is far finer than this
        /// enum, so anything without a clear observational counterpart comes back as Other and
        /// keeps SIMBAD's own description for display; guessing would be worse than saying so.
        ///
        /// The mapping follows SIMBAD's own hierarchy (Wenger et al. 2000; the otypedef table).
        /// </summary>
        public static TargetKind FromSimbadType(string otype)
        {
            if (string.IsNullOrEmpty(otype)) return TargetKind.Other;
            switch (otype)
            {
                case "GlC": return TargetKind.GlobularCluster;
                case "OpC": return TargetKind.OpenCluster;
                case "HII": return TargetKind.EmissionNebula;
                case "PN": return TargetKind.PlanetaryNebula;
                case "SNR": return TargetKind.SupernovaRemnant;
                case "RNe": return TargetKind.ReflectionNebula;
                case "DNe":
                case "MoC":
                case "glb": return TargetKind.DarkNebula;
                // Every galaxy classification SIMBAD draws, including the active-nucleus ones: an
                // AGN, a Seyfert and a LINER are galaxies, and the telescope treats them as such.
                case "G":
                case "AGN":
                case "LIN":
                case "Sy1":
                case "Sy2":
                case "SyG":
                case "BLL":
                case "Bla":
                case "rG":
                case "EmG":
                case "SBG":
                case "H2G":
                case "GiC":
                case "GiG":
                case "GiP":
                case "IG": return TargetKind.Galaxy;
                default: return TargetKind.Other;
            }
        }

        /// <summary>The kind a shipped DeepSkyCatalog entry is.</summary>
        public static TargetKind FromDeepSkyKind(DeepSkyKind kind)
        {
            switch (kind)
            {
                case DeepSkyKind.HiiRegion: return TargetKind.EmissionNebula;
                case DeepSkyKind.SupernovaRemnant: return TargetKind.SupernovaRemnant;
                case DeepSkyKind.PlanetaryNebula: return TargetKind.PlanetaryNebula;
                case DeepSkyKind.ReflectionNebula: return TargetKind.ReflectionNebula;
                case DeepSkyKind.DarkNebula: return TargetKind.DarkNebula;
                default: return TargetKind.Galaxy;
            }
        }

        /// <summary>True for the kinds whose position is read live from the game rather than from a catalogue.</summary>
        public static bool IsSolarSystem(TargetKind kind)
            => kind == TargetKind.SolarSystemStar
            || kind == TargetKind.SolarSystemPlanet
            || kind == TargetKind.SolarSystemMoon;
    }
}
