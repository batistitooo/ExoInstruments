using System;

namespace ExoInstruments.Core
{
    public enum PlanetStatus
    {
        Confirmed,
        Candidate,
        Controversial,
        Retracted
    }

    /// <summary>
    /// Pure data, no KSP/Unity dependency.
    /// Populated from a real exoplanet.eu export via ExoplanetCsvLoader.
    /// Many fields are nullable because real catalog entries are often
    /// incomplete; that incompleteness is itself meaningful (see
    /// IsTransiting) and should not be papered over with invented defaults.
    /// </summary>
    public class StarTarget
    {
        public string Name { get; set; }            // full designation, e.g. "51 Peg b"
        public string HostStarName { get; set; }     // e.g. "51 Peg"

        // Raw alternate-designation lists from the catalog ("HD 217014, GJ 882"),
        // star-level and planet-level respectively. Kept verbatim: the catalog
        // merger extracts HD/HR numbers and normalized name variants from them
        // to cross-match against the background-star catalog.
        public string HostStarAlternateNames { get; set; }
        public string PlanetAlternateNames { get; set; }

        // Stable save-file identifier for the host star (see StarNames.CatalogKeyForHost):
        // planets of one host share it, and it survives catalog reloads. Used by the
        // career fog-of-war scenario state.
        public string CatalogKey { get; set; }

        public PlanetStatus Status { get; set; } = PlanetStatus.Confirmed;
        public string DetectionType { get; set; }    // "Radial Velocity", "Primary Transit", etc.
        public int? DiscoveryYear { get; set; }

        public double RadiusSolar { get; set; }         // star radius, R_sun
        public double StellarMassSolar { get; set; }     // star mass, M_sun
        public double DistanceParsec { get; set; }

        // Equatorial coordinates of the host star, degrees. Null if the catalog
        // entry didn't carry them; a target without coordinates still works
        // for name search and detection, it just can't be placed on the sky chart.
        public double? RaDeg { get; set; }    // right ascension, 0-360
        public double? DecDeg { get; set; }   // declination, -90 to 90

        public double ApparentMagnitude { get; set; }

        // Stellar effective temperature (K), from the catalog's star_teff column.
        // Null when the entry doesn't carry one; required by the habitable-zone
        // and direct-imaging estimates, which must say "unknown" rather than guess.
        public double? EffectiveTempK { get; set; }

        // True when EffectiveTempK is a photometric estimate from the star's
        // B-V color (Ballesteros 2012; BSC decoys, and hosts backfilled by the
        // catalog merger) rather than the planet catalog's own spectroscopic
        // value. Reported to the player: a color temperature and a measured
        // spectrum are not the same grade of evidence.
        public bool EffectiveTempDerivedFromColor { get; set; }

        public bool HasPlanet { get; set; } = true;
        public double? PlanetRadiusEarth { get; set; }    // null if unmeasured (typical for RV-only)
        public double? PlanetMassJupiter { get; set; }    // true mass if known, else M*sin(i)

        // The catalog's own mass_sini column, kept separately from PlanetMassJupiter because
        // the two are different measurements and the RV mass function wants this one. Where a
        // true mass exists it comes from geometry the RV data does not carry (a transit, or an
        // astrometric orbit), so it can exceed M*sin(i) by 1/sin(i): 33x on HD 181720 b, whose
        // orbit is nearly face-on. Feeding that into K would inflate the reflex signal by the
        // same factor. See EstimatedRvSemiAmplitudeMps.
        public double? PlanetMinimumMassJupiter { get; set; }

        // Published RV semi-amplitude and its error bar, straight from the catalog's k columns.
        // Not used by the simulation, which injects its own K: this is the literature value, so
        // a player's recovered K can be compared against the real measurement.
        public double? PublishedRvSemiAmplitudeMps { get; set; }
        public double? PublishedRvSemiAmplitudeErrorMps { get; set; }

        // Time of periastron passage, BJD. Carried for reference only: the simulator has no
        // real epoch to anchor to (see PlanetPhaseOffset01) because KSP's clock is not BJD.
        public double? TimeOfPeriastronJd { get; set; }

        public double PlanetPeriodDays { get; set; }
        public double? SemiMajorAxisAU { get; set; }       // measured value, when the catalog has one
        public double Eccentricity { get; set; }
        public double PlanetPhaseOffset01 { get; set; }    // 0-1, arbitrary, no real epoch to align with KSP's clock
        public double? TransitDurationHours { get; set; }  // measured, rare; see EstimatedTransitDurationHours
        public double? InclinationDeg { get; set; }         // null = unmeasured geometry (common for RV-only)
        public double? MeasuredImpactParameter { get; set; } // direct from catalog, preferred over derived when present

        public double? ArgumentOfPeriastronDeg { get; set; } // omega, null defaults to 0 (periastron at ascending node)

        // Planet temperature (K): catalog temp_measured preferred, else temp_calculated.
        // For directly-imaged young giants this is the real driver of near-IR brightness
        // (internal heat, not irradiation), an irradiation-equilibrium estimate would
        // wrongly render them undetectable, so the catalog value matters when present.
        public double? PlanetTempK { get; set; }

        private const double EarthRadiusKm = 6371.0;
        private const double SolarRadiusKm = 696000.0;
        private const double AuToSolarRadii = 215.032;
        private const double DaysPerYear = 365.25;
        private const double JupiterMassInSolarMasses = 0.0009543;

        // K for Mp*sin(i) = 1 Mjup, Mtot = 1 Msun, P = 1 yr, e = 0
        // (Lovis & Fischer 2010, "Radial Velocity Techniques for Exoplanets", eq. 2; also Cumming et al. 1999).
        private const double RvSemiAmplitudeConstantMps = 28.4329;

        public double TransitDepthPpm
        {
            get
            {
                if (!HasPlanet || !PlanetRadiusEarth.HasValue || RadiusSolar <= 0) return 0.0;
                double rpKm = PlanetRadiusEarth.Value * EarthRadiusKm;
                double rsKm = RadiusSolar * SolarRadiusKm;
                double ratio = rpKm / rsKm;
                return ratio * ratio * 1_000_000.0;
            }
        }

        /// <summary>Semi-major axis in AU, measured value if the catalog has one, else derived via Kepler's third law.</summary>
        public double EstimatedSemiMajorAxisAU
        {
            get
            {
                if (SemiMajorAxisAU.HasValue && SemiMajorAxisAU.Value > 0) return SemiMajorAxisAU.Value;
                if (StellarMassSolar <= 0 || PlanetPeriodDays <= 0) return 0.0;
                double periodYears = PlanetPeriodDays / DaysPerYear;
                return Math.Pow(StellarMassSolar * periodYears * periodYears, 1.0 / 3.0);
            }
        }

        /// <summary>Orbit scale a/R_star, the ratio driving transit geometry. 0 when the orbit or the star is unconstrained.</summary>
        public double ScaledSemiMajorAxis
        {
            get
            {
                double a = EstimatedSemiMajorAxisAU;
                if (a <= 0 || RadiusSolar <= 0) return 0.0;
                return a * AuToSolarRadii / RadiusSolar;
            }
        }

        /// <summary>
        /// Impact parameter b = (a/R_star)*cos(i). Null means we genuinely don't
        /// know the geometry, a real, common state for RV-discovered planets,
        /// not a bug to work around.
        /// </summary>
        public double? ImpactParameter
        {
            get
            {
                if (MeasuredImpactParameter.HasValue) return MeasuredImpactParameter.Value;
                if (!InclinationDeg.HasValue) return null;
                double a = EstimatedSemiMajorAxisAU;
                if (a <= 0 || RadiusSolar <= 0) return null;
                double aInStellarRadii = a * AuToSolarRadii / RadiusSolar;
                double inclinationRad = InclinationDeg.Value * Math.PI / 180.0;
                return aInStellarRadii * Math.Cos(inclinationRad);
            }
        }

        /// <summary>
        /// Approximate total transit duration (T14), derived from period, geometry,
        /// and impact parameter for a circular orbit, the standard textbook
        /// approximation. Used because exoplanet.eu doesn't export a duration
        /// column at all.
        /// </summary>
        public double? EstimatedTransitDurationHours
        {
            get
            {
                if (TransitDurationHours.HasValue) return TransitDurationHours;
                double? b = ImpactParameter;
                if (!b.HasValue || Math.Abs(b.Value) >= 1.0) return null;
                double a = EstimatedSemiMajorAxisAU;
                if (a <= 0 || RadiusSolar <= 0) return null;
                double aInStellarRadii = a * AuToSolarRadii / RadiusSolar;
                double sinArg = Math.Sqrt(1.0 - b.Value * b.Value) / aInStellarRadii;
                sinArg = Math.Min(1.0, Math.Max(-1.0, sinArg));
                double durationDays = (PlanetPeriodDays / Math.PI) * Math.Asin(sinArg);
                return durationDays * 24.0;
            }
        }

        /// <summary>
        /// True only when we have real evidence of a transit: a measured planet
        /// radius (i.e. it wasn't RV-only) and geometry that puts the transit
        /// chord on the stellar disk. Retracted entries are always false, regardless
        /// of what the original (now-disproven) claim said.
        /// </summary>
        public bool IsTransiting
        {
            get
            {
                if (!HasPlanet) return false;
                if (Status == PlanetStatus.Retracted) return false;
                if (!PlanetRadiusEarth.HasValue) return false;
                double? b = ImpactParameter;
                if (!b.HasValue) return false;
                double rpRsRatio = Math.Sqrt(TransitDepthPpm / 1_000_000.0);
                return Math.Abs(b.Value) < (1.0 + rpRsRatio);
            }
        }

        /// <summary>Geometric transit probability ~ R_star/a, for scan-report context.</summary>
        public double TransitProbability
        {
            get
            {
                double a = EstimatedSemiMajorAxisAU;
                if (a <= 0 || RadiusSolar <= 0) return 0.0;
                double aInStellarRadii = a * AuToSolarRadii / RadiusSolar;
                return 1.0 / aInStellarRadii;
            }
        }

        /// <summary>
        /// Mp*sin(i) in Jupiter masses, the only mass an RV measurement constrains, and so
        /// the term the mass function below takes. Three cases, in order of evidence:
        /// the catalog's measured mass_sini; else a true mass projected onto the line of
        /// sight with a measured inclination; else the true mass alone, the standard i = 90
        /// assumption, which is what an entry carrying no geometry at all means.
        ///
        /// The measured value wins even where it looks wrong. A handful of entries pair an
        /// inclination near 90 with a mass_sini well below the true mass, which cannot both be
        /// right. Two of those also carry a published K, and mass_sini is what reproduces it
        /// (HIP 56640 b: 52.5 m/s against a published 53.4, where the true mass gives 96.8).
        /// The excess sits in the true mass, not in mass_sini.
        ///
        /// The projection is measured too: on the 245 loaded entries carrying mass, mass_sini
        /// and inclination together, mass*sin(i) reproduces the published mass_sini to a median
        /// of 0.27%, and exactly where the true mass was derived from mass_sini in the first
        /// place. It is the one path here with no measurement behind it, so it is used last.
        /// </summary>
        public double? RvMinimumMassJupiter
        {
            get
            {
                if (PlanetMinimumMassJupiter.HasValue && PlanetMinimumMassJupiter.Value > 0)
                    return PlanetMinimumMassJupiter.Value;
                if (!PlanetMassJupiter.HasValue || PlanetMassJupiter.Value <= 0) return null;
                // Out-of-range inclinations are malformed, not measurements: the catalog carries
                // i = -2 on Wolf 503 b, a transiting planet whose real inclination is near 90.
                // Projecting onto that would cut its amplitude by 30x.
                if (!InclinationDeg.HasValue || InclinationDeg.Value < 0.0 || InclinationDeg.Value > 180.0)
                    return PlanetMassJupiter.Value;
                return PlanetMassJupiter.Value * Math.Sin(InclinationDeg.Value * Math.PI / 180.0);
            }
        }

        /// <summary>
        /// True when there's a real periodic RV signal to look for: a known planet
        /// mass and a well-formed orbit. Retracted entries are always false, same
        /// reasoning as IsTransiting.
        /// </summary>
        public bool IsRvDetectable
        {
            get
            {
                if (!HasPlanet) return false;
                if (Status == PlanetStatus.Retracted) return false;
                double? mSini = RvMinimumMassJupiter;
                // Zero only when the orbit is exactly face-on, in which case there is
                // genuinely no line-of-sight reflex to detect.
                if (!mSini.HasValue || mSini.Value <= 0) return false;
                return StellarMassSolar > 0 && PlanetPeriodDays > 0;
            }
        }

        /// <summary>
        /// RV semi-amplitude K from the standard mass-function formula.
        /// K = 28.4329 m/s * (Mp*sini/Mjup) * ((Ms+Mp)/Msun)^(-2/3) * (P/yr)^(-1/3) * (1-e^2)^(-1/2)
        ///
        /// The numerator is RvMinimumMassJupiter, never PlanetMassJupiter: the latter prefers
        /// the true mass when the catalog has one, and reading a true mass as a minimum mass
        /// overstates K by 1/sin(i). Measured against the catalog's own published k column,
        /// 51 Peg b lands at 56.7 m/s from mass_sini = 0.46 Mjup against a published
        /// 55.77 +/- 0.15, and at 75.1 m/s from the true mass of 0.61 Mjup, 35% high.
        ///
        /// The total-mass term does take the true mass, which is what it means. It is a 1e-4
        /// correction on any planet, but the two masses are not interchangeable and using the
        /// projected one here would be a second, smaller version of the same mistake.
        /// </summary>
        public double EstimatedRvSemiAmplitudeMps
        {
            get
            {
                if (!IsRvDetectable) return 0.0;
                // Read once: RvSimulator calls this per generated epoch, and a long campaign
                // under extreme warp generates a lot of them.
                double minimumMassJupiter = RvMinimumMassJupiter.Value;
                double periodYears = PlanetPeriodDays / DaysPerYear;
                double trueMassJupiter = PlanetMassJupiter ?? minimumMassJupiter;
                double totalMassSolar = StellarMassSolar + trueMassJupiter * JupiterMassInSolarMasses;
                double eccFactor = 1.0 / Math.Sqrt(Math.Max(1e-9, 1.0 - Eccentricity * Eccentricity));
                return RvSemiAmplitudeConstantMps
                    * minimumMassJupiter
                    * Math.Pow(totalMassSolar, -2.0 / 3.0)
                    * Math.Pow(periodYears, -1.0 / 3.0)
                    * eccFactor;
            }
        }
    }
}