namespace ExoInstruments.Core
{
    /// <summary>
    /// Registry of real instruments the player can observe with. Precision/cadence from each
    /// instrument's own papers (see Citation). Career economy fields are ALL PLACEHOLDERS —
    /// balance à valider avec Baptiste; relative ordering (bigger investment → bigger payoff)
    /// is the only constraint honored here.
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
            Citation = "Gillon et al. 2018. SPECULOOS: four 1m robotic telescopes at Paranal targeting ultra-cool dwarfs.",
            Description = "Four robotic 1-meter telescopes in the Atacama desert, built to stare at the smallest, coolest stars in the neighborhood. " +
                          "It hunts for transits: the tiny periodic dip in a star's brightness when a planet crosses in front of it. " +
                          "Small red stars make that dip proportionally deeper, which is how its sibling project caught the seven TRAPPIST-1 worlds. " +
                          "Precise, patient, and free to start with: the workhorse of this program.",
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
            Citation = "Pollacco et al. 2006. SuperWASP: wide-field survey with 200mm camera lenses, bright-star hot-Jupiter hunting.",
            Description = "An array of off-the-shelf 200mm camera lenses on a single mount, photographing huge swaths of sky every few minutes. " +
                          "No giant mirror, no exotic optics: just relentless wide-field coverage of bright stars, looking for transit dips. " +
                          "Individually noisy, but it discovered nearly two hundred hot Jupiters by sheer persistence. " +
                          "The cheapest telescope time available: ideal for burning through easy bright targets on a budget.",
            IsSpaceBased = false,
            ApertureMeters = 0.111,        // Canon 200mm f/1.8 lens: 111mm entrance pupil -- scintillation-limited on bright stars, WASP's real noise regime
            SiteAltitudeMeters = 2400.0,   // Roque de los Muchachos, La Palma (SuperWASP-North)
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
            Citation = "Ricker et al. 2015. TESS: space-based, 10.5cm aperture, all-sky survey.",
            Description = "A NASA space telescope in a high Earth orbit, scanning the entire sky with four small wide-field cameras. " +
                          "Being above the atmosphere changes everything: no daylight interruptions, no clouds, no twinkling. " +
                          "It watches each patch of sky continuously for weeks, which is exactly what catching repeated transits demands. " +
                          "The aperture is tiny (10.5 cm), so it favors bright stars, but the uninterrupted coverage is something no ground telescope can offer.",
            IsSpaceBased = true,           // Earth-orbiting: observes around the clock, no atmosphere in the way
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
            Citation = "Mayor et al. 2003. HARPS: ESO 3.6m telescope, La Silla, ~1 m/s long-term precision.",
            Description = "A spectrograph on the 3.6-meter ESO telescope in Chile, and for years the most precise planet-hunting machine on Earth. " +
                          "Instead of watching brightness, it measures the star's spectrum: an orbiting planet tugs its star back and forth, " +
                          "and that wobble shifts the star's spectral lines by a few meters per second. " +
                          "HARPS reads that shift to about 1 m/s, walking pace, on a star trillions of kilometers away. It found hundreds of planets this way.",
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
            Citation = "Pepe et al. 2021. ESPRESSO: VLT, sub-10cm/s precision under ideal conditions.",
            Description = "The successor to HARPS, fed by the 8.2-meter Very Large Telescope at Paranal. " +
                          "Same principle (measuring the star's back-and-forth wobble through its spectrum) pushed to the current limit of the art: " +
                          "on a bright quiet star it resolves velocity changes of centimeters per second, gentle enough to feel an Earth-mass planet. " +
                          "The final word in radial velocity, priced like it.",
            IsSpaceBased = false,
            ApertureMeters = 8.2,          // one VLT unit telescope
            SiteAltitudeMeters = 2635.0,   // Paranal
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
            Citation = "Perruchot et al. 2008; Bouchy et al. 2009. SOPHIE: Observatoire de Haute-Provence 1.93m spectrograph.",
            Description = "A spectrograph on the historic 1.93-meter telescope in Haute-Provence, France, the same observatory where the first " +
                          "exoplanet around a Sun-like star (51 Peg b, Nobel Prize 2019) was discovered. " +
                          "It measures the star's wobble through shifts in its spectral lines, at a precision of a couple of meters per second. " +
                          "Less sensitive than HARPS, but far cheaper: the affordable entry into radial-velocity work.",
            IsSpaceBased = false,
            ApertureMeters = 1.93,
            SiteAltitudeMeters = 650.0,    // Observatoire de Haute-Provence
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
            Citation = "Gilmozzi & Spyromilio 2007. ELT: 39.3m primary at Cerro Armazones; contrast targets per Kasper et al. 2021 (PCS).",
            Description = "The Extremely Large Telescope: a 39-meter segmented mirror on a Chilean mountaintop, the largest optical telescope ever built. " +
                          "Where the others infer planets from dips and wobbles, the ELT attempts the hardest observation in astronomy: " +
                          "actually photographing the planet, a faint dot next to a star millions of times brighter, " +
                          "using deformable mirrors that reshape themselves a thousand times a second to cancel the atmosphere. " +
                          "Flagship time at flagship prices, but a direct image also characterizes the star itself.",
            IsSpaceBased = false,
            ApertureMeters = 39.3,
            SiteAltitudeMeters = 3046.0,   // Cerro Armazones
            UnlockCostFunds = 4_000_000.0,
            UnlockScienceThreshold = 900.0,
            ScanCostFunds = 25_000.0,
            ScienceRewardMultiplier = 6.0,
        };

        public static readonly InstrumentSpec Rc20 = new InstrumentSpec
        {
            Name = "RC20",
            DisplayName = "PlaneWave RC20 (Amateur Astrograph)",
            Method = DetectionMethod.SolarSystemPhotography,
            // Exoplanet-detection fields zeroed out — this instrument doesn't do exoplanet science.
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "PlaneWave Instruments RC20: 20-inch (0.51 m) Ritchey-Chretien astrograph, the class of telescope used at university " +
                       "observatories (e.g. ETH Zurich) for hands-on amateur/semi-pro imaging.",
            Description = "A 20-inch Ritchey-Chretien astrograph -- the kind of telescope a university observatory or a serious amateur actually " +
                          "owns, not a billion-Fund flagship. Point it at anything in the neighborhood -- Duna, Jool, the Mun -- and it takes a " +
                          "real photograph: true position, phase, and relative brightness, straight off the sensor. No spectra, no light curves, " +
                          "just a picture, monochrome and grainy the way a real long-exposure frame off an amateur CCD looks before anyone " +
                          "stacks and processes it.",
            IsSpaceBased = false,
            ApertureMeters = VisualTelescopeCatalog.Rc20.ApertureMeters,
            SiteAltitudeMeters = VisualTelescopeCatalog.Rc20.SiteAltitudeMeters, // ETH Zurich's own observatory site
            VisualTelescope = VisualTelescopeCatalog.Rc20,
            // Placeholders -- balance à valider avec Baptiste.
            UnlockedByDefault = false,
            UnlockCostFunds = 15_000.0,
            UnlockScienceThreshold = 5.0,
            ScanCostFunds = 50.0,
            ScienceRewardMultiplier = 0.0, // no detections to reward -- this instrument doesn't feed the science-reward economy
        };

        public static readonly InstrumentSpec Cdk1000 = new InstrumentSpec
        {
            Name = "CDK1000",
            DisplayName = "PlaneWave CDK1000 (Research Astrograph)",
            Method = DetectionMethod.SolarSystemPhotography,
            // Exoplanet-detection fields zeroed out — this instrument doesn't do exoplanet science.
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "PlaneWave Instruments CDK1000: 1-meter (39.4 in) Corrected Dall-Kirkham astrograph, f/6, 47% central obstruction " +
                       "(planewave.com). A real unit was installed at Palomar Observatory in 2024 to support MIT's WINTER project and " +
                       "Caltech research.",
            Description = "A full meter of aperture -- research-observatory class, the same telescope PlaneWave installed at Palomar in 2024. " +
                          "Nearly four times the RC20's light-collecting area and close to double its resolving power, with a corrected " +
                          "Dall-Kirkham design that cancels off-axis coma AND astigmatism (an RC only cancels coma), so the field stays flat " +
                          "corner to corner. That reach is what it takes to frame the small, faint, or distant bodies the RC20 can't " +
                          "usefully resolve -- at real research-instrument cost.",
            IsSpaceBased = false,
            ApertureMeters = VisualTelescopeCatalog.Cdk1000.ApertureMeters,
            SiteAltitudeMeters = VisualTelescopeCatalog.Cdk1000.SiteAltitudeMeters, // Palomar Observatory
            VisualTelescope = VisualTelescopeCatalog.Cdk1000,
            // Placeholders -- balance à valider avec Baptiste. Strictly better than the RC20 in
            // every optical respect, so priced and gated above it per this file's own "bigger
            // investment -> bigger payoff" ordering rule.
            UnlockedByDefault = false,
            UnlockCostFunds = 60_000.0,
            UnlockScienceThreshold = 20.0,
            ScanCostFunds = 120.0,
            ScienceRewardMultiplier = 0.0, // no detections to reward -- this instrument doesn't feed the science-reward economy
        };

        public static readonly InstrumentSpec Fors2Vlt = new InstrumentSpec
        {
            Name = "VLT FORS2",
            DisplayName = "VLT UT1 + FORS2 (Flagship Astrograph)",
            Method = DetectionMethod.SolarSystemPhotography,
            // Exoplanet-detection fields zeroed out — this instrument doesn't do exoplanet science.
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "ESO Very Large Telescope, Unit Telescope 1 (Antu), 8.2m, fitted with the real FORS2 imager: mosaic of two MIT/LL " +
                       "CCID20 CCDs, 15um pixels, 0.126\"/pixel (eso.org FORS2 User Manual and Standard Filters page). Same Paranal site " +
                       "(2635m) already used for ESPRESSO in this mod.",
            Description = "Not a hobbyist instrument at all: one of the four 8.2m Unit Telescopes of the actual Very Large Telescope, " +
                          "carrying its real optical imager/spectrograph, FORS2. Every number driving this camera -- aperture, plate scale, " +
                          "the real MIT CCID20 detector, its real filters -- is FORS2's own published spec, not a reskinned amateur camera. " +
                          "Sixteen times the RC20's raw aperture area puts the faintest, smallest, most distant bodies in the system within " +
                          "reach at last. The gain dial is gone, too: a real research CCD doesn't have one -- what you get is what the " +
                          "hardware's own fixed readout mode gives you.",
            IsSpaceBased = false,
            ApertureMeters = VisualTelescopeCatalog.Fors2Vlt.ApertureMeters,
            SiteAltitudeMeters = VisualTelescopeCatalog.Fors2Vlt.SiteAltitudeMeters, // Paranal Observatory
            VisualTelescope = VisualTelescopeCatalog.Fors2Vlt,
            // Placeholders -- balance à valider avec Baptiste. The biggest jump yet over the
            // CDK1000, priced and gated accordingly per this file's own ordering rule.
            UnlockedByDefault = false,
            UnlockCostFunds = 250_000.0,
            UnlockScienceThreshold = 80.0,
            ScanCostFunds = 400.0,
            ScienceRewardMultiplier = 0.0, // no detections to reward -- this instrument doesn't feed the science-reward economy
        };

        public static readonly InstrumentSpec Sphere = new InstrumentSpec
        {
            Name = "VLT SPHERE",
            DisplayName = "VLT UT3 + SPHERE (Adaptive-Optics Astrograph)",
            Method = DetectionMethod.SolarSystemPhotography,
            // Exoplanet-detection fields zeroed out — this instrument doesn't do exoplanet science.
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "ESO Very Large Telescope, Unit Telescope 3 (Melipal), 8.2m, fitted with the real SPHERE/ZIMPOL extreme-AO imaging " +
                       "polarimeter: SAXO adaptive optics, ~25 mas achieved FWHM, real CCD (640,000 e- full well) (Schmid et al. 2018, " +
                       "A&A 619, A9). Same Paranal site (2635m) as FORS2, different Unit Telescope.",
            Description = "The same VLT, a different real instrument, solving a different real problem: FORS2 is seeing-limited -- " +
                          "Paranal's own atmosphere blurs it to about an arcsecond no matter the mirror size. SPHERE instead corrects " +
                          "that turbulence in real time with its SAXO adaptive-optics system, reaching about 25 milliarcseconds of real " +
                          "resolution -- some 24 to 40 times finer. That's what it takes to actually resolve the system's smallest, most " +
                          "marginal bodies, not just collect more of their light. The tradeoff is real too: ZIMPOL's true field of view " +
                          "is barely 3.6 arcseconds wide, and it has no blue filter at all -- a specialist's instrument, not a generalist's.",
            IsSpaceBased = false,
            ApertureMeters = VisualTelescopeCatalog.Sphere.ApertureMeters,
            SiteAltitudeMeters = VisualTelescopeCatalog.Sphere.SiteAltitudeMeters, // Paranal Observatory
            VisualTelescope = VisualTelescopeCatalog.Sphere,
            // Placeholders -- balance à valider avec Baptiste. A specialist upgrade rather than a
            // strict step up from the CDK1000/FORS2 (tiny FOV, no blue channel), so priced
            // similarly to FORS2 rather than automatically higher.
            UnlockedByDefault = false,
            UnlockCostFunds = 300_000.0,
            UnlockScienceThreshold = 100.0,
            ScanCostFunds = 450.0,
            ScienceRewardMultiplier = 0.0, // no detections to reward -- this instrument doesn't feed the science-reward economy
        };

        public static readonly InstrumentSpec[] All =
        {
            Speculoos, Wasp, Tess, Harps, Espresso, Sophie, Elt, Rc20, Cdk1000, Fors2Vlt, Sphere
        };
    }
}
