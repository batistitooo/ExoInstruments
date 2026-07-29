using System;

namespace ExoInstruments.Core
{
    /// <summary>What kind of object a deep-sky entry is, which decides both how it is drawn and what light it emits.</summary>
    public enum DeepSkyKind
    {
        /// <summary>H II region: ionised hydrogen around hot young stars. The H-alpha sky is made of these.</summary>
        HiiRegion,
        /// <summary>Supernova remnant. Also a strong line emitter, but shock-excited rather than photoionised, so [S II]/H-alpha runs high.</summary>
        SupernovaRemnant,
        /// <summary>Planetary nebula. Arcseconds to a few arcminutes across, so it is a target rather than something an all-sky map resolves.</summary>
        PlanetaryNebula,
        /// <summary>Reflection nebula: dust scattering a nearby star's continuum, with no emission lines of its own.</summary>
        ReflectionNebula,
        /// <summary>A galaxy, placed on the chart from the packed GalaxyCatalog rather than from the list below.</summary>
        Galaxy,
    }

    /// <summary>One catalogued nebula: what it is called, where it is, and how big.</summary>
    public struct DeepSkyObject
    {
        /// <summary>Catalogue designation, e.g. "NGC 7000".</summary>
        public string Id;
        /// <summary>Common name, or null when it has none.</summary>
        public string CommonName;
        public double RaDeg;
        public double DecDeg;
        /// <summary>Apparent size, arcminutes. Major then minor axis; equal for a round object.</summary>
        public double MajorArcmin;
        public double MinorArcmin;
        public DeepSkyKind Kind;

        public string DisplayName => string.IsNullOrEmpty(CommonName) ? Id : CommonName + " (" + Id + ")";

        /// <summary>True for the line emitters an H-alpha frame is taken of.</summary>
        public bool EmitsLines => Kind == DeepSkyKind.HiiRegion
                               || Kind == DeepSkyKind.SupernovaRemnant
                               || Kind == DeepSkyKind.PlanetaryNebula;
    }

    /// <summary>
    /// The bright nebulae, so the sky chart can show where they are.
    ///
    /// WHY A CATALOGUE AND NOT THE MAP. The packed H-alpha map knows the surface brightness of
    /// every direction, but it does not know that a particular patch is called the Rosette, and it
    /// resolves nothing smaller than its 6 arcmin beam -- every planetary nebula in this list is
    /// below that. Finding a target and rendering it are different jobs.
    ///
    /// POSITIONS are J2000 catalogue centres, rounded to the arcminute, which is the precision that
    /// matters for pointing an instrument whose narrowest field is 3.7 arcsec only after the chart
    /// has got it into the right degree. Sizes are the usual quoted apparent extents. Both come
    /// from the standard catalogues these objects are designated in -- Messier, the New General
    /// Catalogue and Index Catalogue (Dreyer 1888, 1895, 1908), and Sharpless (1959, ApJS 4, 257)
    /// for the H II regions that have no NGC number.
    ///
    /// Every H II region and remnant here is checked against the installed H-alpha map in
    /// tools/emission-tests: a real one must sit on a local maximum of the Finkbeiner composite,
    /// and a transcription error in a position shows up immediately as an offset to that maximum.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class DeepSkyCatalog
    {
        private static DeepSkyObject O(string id, string name, double raH, double raM, double decSign,
                                       double decD, double decM, double major, double minor, DeepSkyKind kind)
            => new DeepSkyObject
            {
                Id = id,
                CommonName = name,
                RaDeg = (raH + raM / 60.0) * 15.0,
                DecDeg = decSign * (decD + decM / 60.0),
                MajorArcmin = major,
                MinorArcmin = minor,
                Kind = kind,
            };

        public static readonly DeepSkyObject[] All =
        {
            // --- H II regions -----------------------------------------------------------
            O("NGC 281",  "Pacman",           0, 53,  1, 56, 37, 35, 30, DeepSkyKind.HiiRegion),
            O("IC 1805",  "Heart",            2, 33,  1, 61, 27, 60, 60, DeepSkyKind.HiiRegion),
            O("IC 1848",  "Soul",             2, 52,  1, 60, 26, 60, 30, DeepSkyKind.HiiRegion),
            O("NGC 1499", "California",       4,  3,  1, 36, 25,145, 40, DeepSkyKind.HiiRegion),
            O("NGC 1976", "Orion Nebula",     5, 35, -1,  5, 23, 85, 60, DeepSkyKind.HiiRegion),
            O("NGC 2024", "Flame",            5, 42, -1,  1, 51, 30, 30, DeepSkyKind.HiiRegion),
            O("IC 434",   "Horsehead",        5, 41, -1,  2, 27, 60, 10, DeepSkyKind.HiiRegion),
            O("NGC 2070", "Tarantula",        5, 39, -1, 69,  6, 40, 25, DeepSkyKind.HiiRegion),
            O("NGC 2237", "Rosette",          6, 32,  1,  4, 57, 80, 80, DeepSkyKind.HiiRegion),
            O("IC 2177",  "Seagull",          7,  4, -1, 10, 27,120, 40, DeepSkyKind.HiiRegion),
            O("NGC 3372", "Carina Nebula",   10, 45, -1, 59, 52,120,120, DeepSkyKind.HiiRegion),
            O("NGC 6188", "Rim",             16, 40, -1, 48, 46, 20, 12, DeepSkyKind.HiiRegion),
            O("NGC 6334", "Cat's Paw",       17, 20, -1, 36,  6, 40, 30, DeepSkyKind.HiiRegion),
            O("NGC 6357", "Lobster",         17, 25, -1, 34, 12, 50, 40, DeepSkyKind.HiiRegion),
            O("NGC 6523", "Lagoon",          18,  4, -1, 24, 23, 90, 40, DeepSkyKind.HiiRegion),
            O("NGC 6514", "Trifid",          18,  2, -1, 22, 58, 28, 28, DeepSkyKind.HiiRegion),
            O("NGC 6611", "Eagle",           18, 19, -1, 13, 47, 35, 28, DeepSkyKind.HiiRegion),
            O("NGC 6618", "Omega",           18, 21, -1, 16, 11, 46, 37, DeepSkyKind.HiiRegion),
            O("NGC 6888", "Crescent",        20, 12,  1, 38, 21, 18, 12, DeepSkyKind.HiiRegion),
            O("NGC 7000", "North America",   20, 59,  1, 44, 32,120,100, DeepSkyKind.HiiRegion),
            O("IC 5070",  "Pelican",         20, 51,  1, 44, 21, 60, 50, DeepSkyKind.HiiRegion),
            O("IC 1396",  "Elephant's Trunk",21, 39,  1, 57, 30,170,140, DeepSkyKind.HiiRegion),
            O("Sh2-155",  "Cave",            22, 57,  1, 62, 37, 50, 30, DeepSkyKind.HiiRegion),
            O("NGC 7635", "Bubble",          23, 21,  1, 61, 13, 15, 15, DeepSkyKind.HiiRegion),

            // --- Supernova remnants -----------------------------------------------------
            O("NGC 1952", "Crab",             5, 35,  1, 22,  1,  7,  5, DeepSkyKind.SupernovaRemnant),
            O("NGC 6960", "Veil, west",      20, 46,  1, 30, 43, 70,  6, DeepSkyKind.SupernovaRemnant),
            O("NGC 6992", "Veil, east",      20, 56,  1, 31, 43, 60,  8, DeepSkyKind.SupernovaRemnant),

            // --- Planetary nebulae ------------------------------------------------------
            O("NGC 2392", "Eskimo",           7, 29,  1, 20, 55,  0.8, 0.8, DeepSkyKind.PlanetaryNebula),
            O("NGC 6543", "Cat's Eye",       17, 59,  1, 66, 38,  0.4, 0.3, DeepSkyKind.PlanetaryNebula),
            O("NGC 6720", "Ring",            18, 54,  1, 33,  2,  1.4, 1.0, DeepSkyKind.PlanetaryNebula),
            O("NGC 6853", "Dumbbell",        20,  0,  1, 22, 43,  8.0, 5.6, DeepSkyKind.PlanetaryNebula),
            O("NGC 7009", "Saturn",          21,  4, -1, 11, 22,  0.7, 0.4, DeepSkyKind.PlanetaryNebula),
            O("NGC 7293", "Helix",           22, 30, -1, 20, 50, 25.0,17.0, DeepSkyKind.PlanetaryNebula),

            // --- Reflection -------------------------------------------------------------
            O("NGC 1435", "Merope",           3, 46,  1, 23, 47, 30, 30, DeepSkyKind.ReflectionNebula),
            O("NGC 7023", "Iris",            21,  1,  1, 68, 10, 18, 18, DeepSkyKind.ReflectionNebula),
        };

        /// <summary>Catalogue entry whose designation or common name matches, case-insensitively, or null.</summary>
        public static DeepSkyObject? Find(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string q = query.Trim();
            foreach (var o in All)
            {
                if (string.Equals(o.Id, q, StringComparison.OrdinalIgnoreCase)) return o;
                if (!string.IsNullOrEmpty(o.CommonName)
                    && string.Equals(o.CommonName, q, StringComparison.OrdinalIgnoreCase)) return o;
            }
            return null;
        }
    }
}
