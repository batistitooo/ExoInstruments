using System;
using System.Collections.Generic;
using System.Globalization;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Fills a TargetSearchIndex from every catalogue the mod has, in the order that decides which
    /// source wins when two describe the same object.
    ///
    /// THE ORDER IS THE POINT. The same galaxy can be described by HyperLEDA, which measured its
    /// isophotal diameter and axis ratio, and by the cross-identification table, which knows it is
    /// called M31. Adding HyperLEDA first means the entry carries the measured shape and merely
    /// GAINS the name; adding them the other way round would keep the name and lose the
    /// measurements. Every merge here is one-directional for that reason: a later source may add
    /// aliases to an existing entry, never replace its numbers.
    ///
    /// WHAT IS NOT MERGED. Nothing is cross-matched by position between catalogues, except the one
    /// place a designation cannot do the job (attaching IAU proper names to Bright Star Catalogue
    /// entries, where the tolerance is stated and tight). Positional merging of two catalogues with
    /// different depths silently fuses close pairs, and this mod's own galaxy catalogue rejects
    /// rows for less than that.
    ///
    /// Pure C#, no Unity dependency: the solar-system bodies, which are the one thing that needs
    /// the game, are added by the caller.
    /// </summary>
    public static class TargetCatalogue
    {
        /// <summary>How close an IAU proper name has to be to a Bright Star Catalogue entry to be the same star, arcseconds.</summary>
        public const double ProperNamePositionToleranceArcsec = 10.0;

        /// <summary>
        /// The exoplanet and Bright Star Catalogue targets. displayName and identityHidden come
        /// from the caller because career mode withholds a star's identity until it is scanned:
        /// a hidden star is indexed under its provisional designation and under nothing else, so
        /// the search box cannot answer a question the fog of war is supposed to be hiding.
        /// </summary>
        public static void AddStars(TargetSearchIndex index, IList<StarTarget> catalog,
                                    Func<StarTarget, string> displayName,
                                    Func<StarTarget, bool> identityHidden)
        {
            if (catalog == null) return;
            foreach (StarTarget star in catalog)
            {
                bool hidden = identityHidden != null && identityHidden(star);
                string shown = displayName != null ? displayName(star) : star.Name;
                if (string.IsNullOrEmpty(shown)) continue;

                var aliases = new List<string> { shown };
                if (!hidden)
                {
                    AddAlias(aliases, star.Name);
                    AddAlias(aliases, star.HostStarName);
                    AddAliasList(aliases, star.HostStarAlternateNames);
                    AddAliasList(aliases, star.PlanetAlternateNames);
                }

                var entry = new SearchTarget
                {
                    DisplayName = shown,
                    Kind = star.HasPlanet ? TargetKind.PlanetHost : TargetKind.Star,
                    TypeLabel = hidden ? "unidentified source"
                                       : star.HasPlanet ? DescribePlanet(star) : "star",
                    Provenance = star.HasPlanet ? "exoplanet.eu" : "Bright Star Catalogue (V/50)",
                    RaDeg = star.RaDeg ?? double.NaN,
                    DecDeg = star.DecDeg ?? double.NaN,
                    Magnitude = star.ApparentMagnitude,
                    Payload = star,
                    Aliases = aliases.ToArray(),
                    IdentityWithheld = hidden,
                };
                entry.Constellation = ConstellationOf(entry.RaDeg, entry.DecDeg);
                index.Add(entry);
            }
        }

        /// <summary>The bright nebulae the mod ships a hand-checked list of.</summary>
        public static void AddDeepSky(TargetSearchIndex index)
        {
            foreach (DeepSkyObject obj in DeepSkyCatalog.All)
            {
                var aliases = new List<string> { obj.Id };
                AddAlias(aliases, obj.CommonName);

                var entry = new SearchTarget
                {
                    Designation = TargetDesignations.Pretty(obj.Id),
                    CommonName = obj.CommonName,
                    Kind = TargetKinds.FromDeepSkyKind(obj.Kind),
                    TypeLabel = TargetKinds.Label(TargetKinds.FromDeepSkyKind(obj.Kind)),
                    Provenance = "NGC/IC and Sharpless designations",
                    RaDeg = obj.RaDeg,
                    DecDeg = obj.DecDeg,
                    MajorArcmin = obj.MajorArcmin,
                    Payload = obj,
                    Aliases = aliases.ToArray(),
                };
                entry.ComposeDisplayName();
                entry.Constellation = ConstellationOf(entry.RaDeg, entry.DecDeg);
                index.Add(entry);
            }
        }

        /// <summary>
        /// The packed galaxy catalogue, if one is installed. Nothing ships, so an absent catalogue
        /// simply means no galaxies in the list rather than an error.
        /// </summary>
        public static void AddGalaxies(TargetSearchIndex index, GalaxyCatalog catalog)
        {
            if (catalog == null || !catalog.IsLoaded) return;
            foreach (Galaxy galaxy in catalog.Search(0.0, 0.0, 180.0, double.MaxValue))
            {
                var entry = new SearchTarget
                {
                    Designation = TargetDesignations.Pretty(galaxy.Name),
                    Kind = TargetKind.Galaxy,
                    TypeLabel = DescribeMorphology(galaxy.MorphologicalType),
                    Provenance = catalog.Source,
                    RaDeg = galaxy.RaDeg,
                    DecDeg = galaxy.DecDeg,
                    Magnitude = galaxy.TotalBMag,
                    MajorArcmin = galaxy.D25Arcmin,
                    Payload = galaxy,
                    Aliases = new[] { galaxy.Name },
                };
                entry.ComposeDisplayName();
                entry.Constellation = ConstellationOf(entry.RaDeg, entry.DecDeg);
                index.Add(entry);
            }
        }

        /// <summary>
        /// The Messier and named NGC/IC objects, from SIMBAD. Two jobs, and only ever in this
        /// order:
        ///
        ///   * an object an earlier catalogue already carries GAINS its other names, so M31 finds
        ///     the HyperLEDA row with its measured shape rather than a second, emptier entry;
        ///   * an object no installed catalogue carries becomes a target in its own right, which
        ///     is how the globular and open clusters, of which this mod has no catalogue at all,
        ///     become pointable.
        /// </summary>
        public static void AddCrossIdentifiedObjects(TargetSearchIndex index)
        {
            for (int i = 0; i < DeepSkyCrossIdTable.Count; i++)
            {
                string[] identifiers = DeepSkyCrossIdTable.Identifiers[i];

                SearchTarget existing = null;
                foreach (string identifier in identifiers)
                {
                    existing = index.Find(TargetDesignations.Tidy(identifier));
                    if (existing != null) break;
                }

                if (existing != null)
                {
                    index.AddAliases(existing, Tidy(identifiers));
                    // The measured row keeps its measurements, and takes the best designation it
                    // now knows about: HyperLEDA's "NGC0224" is the right galaxy under a label
                    // nobody reads, and this is the table that knows it is also M 31. Its own
                    // common name, if it has one, is left alone: the shipped nebula list is
                    // hand-checked and says "Crab" where SIMBAD's first alphabetical name is
                    // "CRAB NEB".
                    string designation = BestDesignation(identifiers);
                    if (!string.IsNullOrEmpty(designation)) existing.Designation = designation;
                    if (string.IsNullOrEmpty(existing.CommonName))
                        existing.CommonName = BestCommonName(identifiers);
                    existing.ComposeDisplayName();
                    continue;
                }

                string type = DeepSkyCrossIdTable.ObjectTypes[i];
                var entry = new SearchTarget
                {
                    Designation = BestDesignation(identifiers),
                    CommonName = BestCommonName(identifiers),
                    Kind = TargetKinds.FromSimbadType(type),
                    TypeLabel = DescribeSimbadType(type),
                    Provenance = "SIMBAD",
                    RaDeg = DeepSkyCrossIdTable.RaDeg[i],
                    DecDeg = DeepSkyCrossIdTable.DecDeg[i],
                    Magnitude = DeepSkyCrossIdTable.VMags[i],
                    MajorArcmin = DeepSkyCrossIdTable.MajorArcmin[i],
                    Aliases = Tidy(identifiers),
                };
                entry.ComposeDisplayName();
                entry.Constellation = ConstellationOf(entry.RaDeg, entry.DecDeg);
                index.Add(entry);
            }
        }

        /// <summary>
        /// The IAU's approved proper names, attached to the stars they belong to.
        ///
        /// The join is by catalogue number first (Bright Star, then Henry Draper, then the Bayer
        /// designation) and only falls back to position when none of those is available on both
        /// sides. That order matters: a name attached to the wrong star is worse than a name that
        /// fails to attach, because the wrong one is invisible.
        ///
        /// A name that attaches to nothing becomes its own entry. Those are real stars with real
        /// positions that the installed catalogues simply do not reach; several are exoplanet hosts
        /// the WGSN has named.
        /// </summary>
        public static void AddStarProperNames(TargetSearchIndex index)
        {
            // One pass over the index to collect what the stars can be joined on, rather than a
            // scan of every star for every name.
            var byHr = new Dictionary<int, SearchTarget>();
            var byHd = new Dictionary<int, SearchTarget>();
            var stars = new List<SearchTarget>();

            foreach (SearchTarget entry in index.Entries)
            {
                if (entry.Kind != TargetKind.Star && entry.Kind != TargetKind.PlanetHost) continue;
                stars.Add(entry);

                var star = entry.Payload as StarTarget;
                if (star == null) continue;

                // A Bright Star decoy carries its HR number as its save key ("hr 7001"); every
                // other designation has to be read out of the names the catalogues wrote.
                if (star.CatalogKey != null && star.CatalogKey.StartsWith("hr ", StringComparison.Ordinal)
                    && int.TryParse(star.CatalogKey.Substring(3), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out int keyHr))
                    if (!byHr.ContainsKey(keyHr)) byHr[keyHr] = entry;

                foreach (int hr in StarNames.ExtractHrNumbers(star.Name, star.HostStarName,
                                                              star.HostStarAlternateNames, star.PlanetAlternateNames))
                    if (!byHr.ContainsKey(hr)) byHr[hr] = entry;
                foreach (int hd in StarNames.ExtractHdNumbers(star.Name, star.HostStarName,
                                                              star.HostStarAlternateNames, star.PlanetAlternateNames))
                    if (!byHd.ContainsKey(hd)) byHd[hd] = entry;
            }

            for (int i = 0; i < StarProperNameTable.Count; i++)
            {
                string name = StarProperNameTable.Names[i];
                SearchTarget host = null;

                int hr = StarProperNameTable.HrNumbers[i];
                int hd = StarProperNameTable.HdNumbers[i];
                if (hr > 0) byHr.TryGetValue(hr, out host);
                if (host == null && hd > 0) byHd.TryGetValue(hd, out host);
                if (host == null) host = index.Find(BayerDesignation(i));
                if (host == null) host = NearestStarWithin(stars, StarProperNameTable.RaDeg[i],
                                                           StarProperNameTable.DecDeg[i],
                                                           ProperNamePositionToleranceArcsec);

                if (host != null)
                {
                    // Career fog: a star whose identity is withheld must not become findable by
                    // typing its famous name. Index.AddAliases refuses on an entry the caller
                    // marked hidden, which is what IsIdentityWithheld records.
                    index.AddAliases(host, new[] { name, StarProperNameTable.NamesWithDiacritics[i] });
                    continue;
                }

                var entry = new SearchTarget
                {
                    DisplayName = name,
                    Kind = TargetKind.Star,
                    TypeLabel = "star, IAU-named",
                    Provenance = "IAU Catalog of Star Names",
                    RaDeg = StarProperNameTable.RaDeg[i],
                    DecDeg = StarProperNameTable.DecDeg[i],
                    Magnitude = StarProperNameTable.Magnitudes[i],
                    Aliases = ProperNameAliases(i),
                };
                entry.Constellation = ConstellationOf(entry.RaDeg, entry.DecDeg);
                index.Add(entry);
            }
        }

        private static string[] ProperNameAliases(int i)
        {
            var aliases = new List<string> { StarProperNameTable.Names[i] };
            AddAlias(aliases, StarProperNameTable.NamesWithDiacritics[i]);
            AddAlias(aliases, StarProperNameTable.Designations[i]);
            AddAlias(aliases, BayerDesignation(i));
            return aliases.ToArray();
        }

        private static string BayerDesignation(int i)
        {
            string bayer = StarProperNameTable.BayerIds[i];
            string constellation = StarProperNameTable.Constellations[i];
            if (string.IsNullOrEmpty(bayer) || string.IsNullOrEmpty(constellation)) return null;
            return bayer + " " + constellation;
        }

        private static SearchTarget NearestStarWithin(List<SearchTarget> stars, double raDeg, double decDeg,
                                                      double toleranceArcsec)
        {
            if (double.IsNaN(raDeg) || double.IsNaN(decDeg)) return null;
            double toleranceDeg = toleranceArcsec / 3600.0;
            double cosLimit = Math.Cos(toleranceDeg * Math.PI / 180.0);

            double d = Math.PI / 180.0;
            double sinDec = Math.Sin(decDeg * d), cosDec = Math.Cos(decDeg * d);

            SearchTarget best = null;
            double bestCos = cosLimit;
            foreach (SearchTarget star in stars)
            {
                if (double.IsNaN(star.RaDeg) || double.IsNaN(star.DecDeg)) continue;
                double sd = Math.Sin(star.DecDeg * d), cd = Math.Cos(star.DecDeg * d);
                double cos = sinDec * sd + cosDec * cd * Math.Cos((star.RaDeg - raDeg) * d);
                if (cos > bestCos) { bestCos = cos; best = star; }
            }
            return best;
        }

        private static string[] Tidy(string[] identifiers)
        {
            var tidied = new string[identifiers.Length];
            for (int i = 0; i < identifiers.Length; i++) tidied[i] = TargetDesignations.Tidy(identifiers[i]);
            return tidied;
        }

        // The first catalogue designation, which the table already orders Messier, then NGC, then IC.
        private static string BestDesignation(string[] identifiers)
        {
            foreach (string raw in identifiers)
                if (!raw.StartsWith("NAME ", StringComparison.Ordinal))
                    return TargetDesignations.Pretty(raw);
            return null;
        }

        // Which of an object's common names to put on the label. SIMBAD records every name an object has ever
        // been published under and expresses no preference among them, so this is a presentation choice and is
        // written as one: no name is discarded, every one of them stays searchable, and only the label is
        // decided. Three forms are set aside because they name something OTHER than the object an observer
        // means: * all-uppercase legacy transliterations ("CRAB NEB", "SMOKING GUN"); * designations carrying
        // an asterisk, which in SIMBAD denote the central object rather than the nebula or galaxy around it ("M
        // 87*" is the black hole); * radio-source designations, a constellation followed by a single letter
        // ("Vir A"), and names that begin with a three-letter constellation abbreviation ("Ori Nebula"), both
        // of which are the abbreviated forms rather than the spoken ones. Among what is left, the shortest:
        // "Crab" over "Crab Nebula", "Whirlpool" over "Whirlpool Galaxy". If every candidate is set aside, the
        // rule yields rather than inventing, and the first name is used as it stands.
        private static string BestCommonName(string[] identifiers)
        {
            string first = null, best = null;
            foreach (string raw in identifiers)
            {
                if (!raw.StartsWith("NAME ", StringComparison.Ordinal)) continue;
                string name = TargetDesignations.Tidy(raw);
                if (first == null) first = name;
                if (!IsSpokenName(name)) continue;
                if (best == null || name.Length < best.Length) best = name;
            }
            return best ?? first;
        }

        private static bool IsSpokenName(string name)
        {
            if (name.IndexOf('*') >= 0) return false;
            if (name == name.ToUpperInvariant() && name != name.ToLowerInvariant()) return false;

            string[] words = name.Split(' ');
            if (words.Length == 2 && words[1].Length == 1 && char.IsUpper(words[1][0])) return false;
            if (words.Length > 1 && Constellations.Resolve(words[0]) != null
                && words[0].Length <= 3) return false;
            return true;
        }

        /// <summary>SIMBAD's own description of a type code, or the code itself if the shipped vocabulary has no entry.</summary>
        public static string DescribeSimbadType(string code)
        {
            for (int i = 0; i < DeepSkyCrossIdTable.TypeCodes.Length; i++)
                if (string.Equals(DeepSkyCrossIdTable.TypeCodes[i], code, StringComparison.Ordinal))
                    return DeepSkyCrossIdTable.TypeDescriptions[i];
            return code;
        }

        /// <summary>
        /// A de Vaucouleurs numerical type read back as the Hubble class it stands for. The
        /// boundaries are the ones the RC3 tabulates the T scale against (de Vaucouleurs et al.
        /// 1991, Table 2); nothing is interpolated, because the scale is a coding of discrete
        /// classes and reporting "Sab and a half" would claim a distinction the classification
        /// does not make.
        /// </summary>
        public static string DescribeMorphology(double t)
        {
            if (double.IsNaN(t)) return "galaxy, unclassified";
            if (t < -3.5) return "elliptical galaxy";
            if (t < -0.5) return "lenticular galaxy";
            if (t < 0.5) return "S0/a galaxy";
            if (t < 2.5) return "Sa-Sab spiral galaxy";
            if (t < 4.5) return "Sb-Sbc spiral galaxy";
            if (t < 6.5) return "Sc-Scd spiral galaxy";
            if (t < 8.5) return "Sd-Sdm spiral galaxy";
            if (t < 9.5) return "Magellanic spiral galaxy";
            return "irregular galaxy";
        }

        private static string DescribePlanet(StarTarget star)
        {
            string status = star.Status == PlanetStatus.Confirmed ? "" : ", " + star.Status.ToString().ToLowerInvariant();
            return string.IsNullOrEmpty(star.DetectionType)
                ? "planet host" + status
                : "planet host, " + star.DetectionType.ToLowerInvariant() + status;
        }

        private static string ConstellationOf(double raDeg, double decDeg)
            => double.IsNaN(raDeg) || double.IsNaN(decDeg) ? null : Constellations.FindAbbreviation(raDeg, decDeg);

        private static void AddAlias(List<string> aliases, string alias)
        {
            if (string.IsNullOrWhiteSpace(alias)) return;
            string tidied = TargetDesignations.Tidy(alias);
            if (!aliases.Contains(tidied)) aliases.Add(tidied);
        }

        // Catalogue alternate-name fields are comma-separated lists written as one string.
        private static void AddAliasList(List<string> aliases, string commaSeparated)
        {
            if (string.IsNullOrWhiteSpace(commaSeparated)) return;
            foreach (string part in commaSeparated.Split(','))
                AddAlias(aliases, part);
        }
    }
}
