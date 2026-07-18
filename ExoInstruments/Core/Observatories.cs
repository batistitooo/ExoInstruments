namespace ExoInstruments.Core
{
    /// <summary>
    /// Registry of real instruments the player can observe with. Reference
    /// precision/magnitude/cadence figures are drawn from each instrument's own
    /// papers -- see Citation on each entry. Adding another observatory (DUET,
    /// SPIRIT, WASP's northern/southern successor, ...) is just another entry here.
    ///
    /// UnlockCostFunds/UnlockScienceThreshold/ScanCostFunds/ScienceRewardMultiplier
    /// are career-mode progression knobs (ExoInstrumentsGUI's unlock and scan-cost
    /// UI, ExoInstrumentsScenario's persistence) -- ALL VALUES ARE PLACEHOLDERS,
    /// balance à valider avec Baptiste. The only real constraint honored here is
    /// relative ordering: cost, per-scan operating cost, and reward multiplier
    /// all track each instrument's real-world budget class, so a bigger
    /// investment really does buy scans that cost more but pay more. Ignored
    /// entirely outside career mode.
    /// </summary>
    public static class Observatories
    {
        public static readonly InstrumentSpec Speculoos = new InstrumentSpec
        {
            Name = "SPECULOOS",
            DisplayName = "SPECULOOS (Transit)",
            Method = DetectionMethod.Transit,
            ReferenceMagnitude = 9.5,
            ReferencePrecision = 150.0,   // ppm
            PrecisionExponent = 0.2,
            CadenceSeconds = 30.0,        // exposure cadence
            Citation = "Gillon et al. 2018 -- SPECULOOS: four 1m robotic telescopes at Paranal targeting ultra-cool dwarfs.",
            IsSpaceBased = false,
            ApertureMeters = 1.0,          // each SSO unit: 1m Ritchey-Chretien
            SiteAltitudeMeters = 2490.0,   // Paranal SPECULOOS Southern Observatory
            UnlockedByDefault = true,     // the observatory's own starting instrument
            UnlockCostFunds = 0.0,
            UnlockScienceThreshold = 0.0,
            ScanCostFunds = 500.0,
            ScienceRewardMultiplier = 1.0,
        };

        public static readonly InstrumentSpec Wasp = new InstrumentSpec
        {
            Name = "WASP",
            DisplayName = "WASP (Transit)",
            Method = DetectionMethod.Transit,
            ReferenceMagnitude = 9.5,
            ReferencePrecision = 1000.0,  // ppm (~1 mmag) -- small 200mm-lens apertures, wide field
            PrecisionExponent = 0.2,
            CadenceSeconds = 600.0,       // ~10 min imaging cadence per field
            Citation = "Pollacco et al. 2006 -- SuperWASP: wide-field survey with 200mm camera lenses, bright-star hot-Jupiter hunting.",
            IsSpaceBased = false,
            ApertureMeters = 0.111,        // Canon 200mm f/1.8 lens: 111mm entrance pupil -- scintillation-limited on bright stars, WASP's real noise regime
            SiteAltitudeMeters = 2400.0,   // Roque de los Muchachos, La Palma (SuperWASP-North)
            // Cheapest purchasable upgrade: real WASP hardware was off-the-shelf
            // camera lenses, far below SPECULOOS's four 1m robotic domes -- worse
            // per-point precision, but immediately affordable, no track record
            // needed, and the cheapest telescope time in the registry: its niche
            // is burning down the fog on bright easy targets without draining
            // the budget.
            UnlockCostFunds = 10_000.0,
            UnlockScienceThreshold = 0.0,
            ScanCostFunds = 250.0,
            ScienceRewardMultiplier = 1.0,
        };

        public static readonly InstrumentSpec Tess = new InstrumentSpec
        {
            Name = "TESS",
            DisplayName = "TESS (Transit)",
            Method = DetectionMethod.Transit,
            ReferenceMagnitude = 10.0,
            ReferencePrecision = 1095.0,  // ppm per 2-min point, from the mission's ~200 ppm/hr requirement at V=10
            PrecisionExponent = 0.2,
            CadenceSeconds = 120.0,       // 2-minute short-cadence targets
            Citation = "Ricker et al. 2015 -- TESS: space-based, 10.5cm aperture, all-sky survey.",
            IsSpaceBased = true,           // Earth-orbiting: observes around the clock, no atmosphere in the way
            // A NASA Explorer-class space mission, several orders of magnitude
            // above ground-based WASP/SPECULOOS -- priced and gated accordingly.
            UnlockCostFunds = 300_000.0,
            UnlockScienceThreshold = 100.0,
            ScanCostFunds = 2_500.0,
            ScienceRewardMultiplier = 2.0,
        };

        public static readonly InstrumentSpec Harps = new InstrumentSpec
        {
            Name = "HARPS",
            DisplayName = "HARPS (Radial Velocity)",
            Method = DetectionMethod.RadialVelocity,
            ReferenceMagnitude = 9.5,
            ReferencePrecision = 1.0,     // m/s
            PrecisionExponent = 0.2,
            CadenceSeconds = 6.0 * 3600.0,
            Citation = "Mayor et al. 2003 -- HARPS: ESO 3.6m telescope, La Silla, ~1 m/s long-term precision.",
            IsSpaceBased = false,
            ApertureMeters = 3.6,
            SiteAltitudeMeters = 2400.0,   // La Silla
            UnlockCostFunds = 200_000.0,
            UnlockScienceThreshold = 120.0,
            ScanCostFunds = 3_500.0,
            ScienceRewardMultiplier = 2.5,
        };

        public static readonly InstrumentSpec Espresso = new InstrumentSpec
        {
            Name = "ESPRESSO",
            DisplayName = "ESPRESSO (Radial Velocity)",
            Method = DetectionMethod.RadialVelocity,
            ReferenceMagnitude = 8.0,
            ReferencePrecision = 0.15,    // m/s -- near the instrument's best-case sub-10cm/s spec on bright quiet stars
            PrecisionExponent = 0.2,
            CadenceSeconds = 8.0 * 3600.0,
            Citation = "Pepe et al. 2021 -- ESPRESSO: VLT, sub-10cm/s precision under ideal conditions.",
            IsSpaceBased = false,
            ApertureMeters = 8.2,          // one VLT unit telescope
            SiteAltitudeMeters = 2635.0,   // Paranal
            // The RV ceiling: VLT-class aperture plus the best achieved long-term
            // precision of any instrument in this registry -- priced as the RV
            // path's capstone, just under ELT's imaging capstone.
            UnlockCostFunds = 900_000.0,
            UnlockScienceThreshold = 400.0,
            ScanCostFunds = 8_000.0,
            ScienceRewardMultiplier = 4.0,
        };

        public static readonly InstrumentSpec Sophie = new InstrumentSpec
        {
            Name = "SOPHIE",
            DisplayName = "SOPHIE (Radial Velocity)",
            Method = DetectionMethod.RadialVelocity,
            ReferenceMagnitude = 8.0,
            ReferencePrecision = 2.0,     // m/s -- smaller 1.93m aperture than HARPS, needs brighter targets for similar S/N
            PrecisionExponent = 0.2,
            CadenceSeconds = 6.0 * 3600.0,
            Citation = "Perruchot et al. 2008; Bouchy et al. 2009 -- SOPHIE: Observatoire de Haute-Provence 1.93m spectrograph.",
            IsSpaceBased = false,
            ApertureMeters = 1.93,
            SiteAltitudeMeters = 650.0,    // Observatoire de Haute-Provence
            // The RV path's entry point: smaller aperture than HARPS, cheapest
            // way into radial-velocity detection.
            UnlockCostFunds = 60_000.0,
            UnlockScienceThreshold = 30.0,
            ScanCostFunds = 1_500.0,
            ScienceRewardMultiplier = 1.5,
        };

        public static readonly InstrumentSpec Elt = new InstrumentSpec
        {
            Name = "ELT",
            DisplayName = "ELT (Direct Imaging)",
            Method = DetectionMethod.DirectImaging,
            ReferenceMagnitude = 6.0,
            // For imaging, "precision" is the 5-sigma contrast floor at 1 lambda/D
            // after 1 hour of integration -- order-of-magnitude for next-generation
            // extreme-AO on a 39m aperture (raw ~1e-4 at small separations, deep
            // post-processed limits approaching 1e-8; Kasper et al. 2021, PCS/ELT).
            // Magnitude scaling reuses the shared photon-noise relation: fainter AO
            // reference star, worse wavefront correction.
            ReferencePrecision = 1.0e-4,
            PrecisionExponent = 0.2,
            CadenceSeconds = 3600.0,      // nominal exposure block; integration accrues continuously
            Citation = "Gilmozzi & Spyromilio 2007 -- ELT: 39.3m primary at Cerro Armazones; contrast targets per Kasper et al. 2021 (PCS).",
            IsSpaceBased = false,
            ApertureMeters = 39.3,
            SiteAltitudeMeters = 3046.0,   // Cerro Armazones
            // The registry's most expensive instrument by a wide margin, matching
            // the ELT's real ~39m-class flagship-observatory budget tier --
            // deliberately the last thing a career playthrough unlocks. Flagship
            // telescope time is priced to make each pointing a real decision, but
            // an ELT campaign can also characterize the star itself (see
            // ScienceRewards.ScienceRewardStellarCharacterization), so even a
            // null companion search isn't a total write-off.
            UnlockCostFunds = 4_000_000.0,
            UnlockScienceThreshold = 900.0,
            ScanCostFunds = 25_000.0,
            ScienceRewardMultiplier = 6.0,
        };

        public static readonly InstrumentSpec[] All =
        {
            Speculoos, Wasp, Tess, Harps, Espresso, Sophie, Elt
        };
    }
}
