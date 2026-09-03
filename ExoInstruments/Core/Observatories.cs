namespace ExoInstruments.Core
{
    /// <summary>
    /// Registry of real instruments the player can observe with. Precision/cadence from each
    /// instrument's own papers (see Citation).
    ///
    /// THE CAREER PRICE OF AN INSTRUMENT IS ITS APERTURE, THROUGH THE PUBLISHED COST LAW.
    /// Every UnlockCostFunds and ScanCostFunds below comes out of one formula rather than being
    /// chosen instrument by instrument, so the ladder cannot be argued with piecemeal: change the
    /// two constants and the whole roster moves together, in the order real telescopes cost.
    ///
    ///   UnlockCostFunds = C * (D / 1 m) ^ (alpha * kappa)
    ///
    ///   alpha   THE SOURCED PART. van Belle, Meinel &amp; Meinel (2004), Proc. SPIE 5489, 563
    ///           ("The scaling relationship between telescope cost and aperture size for very
    ///           large telescopes", also arXiv:2107.09605) fit ground-based optical telescopes
    ///           built since 1980 at cost proportional to D^2.46, and report a distinctly
    ///           shallower law near D^2.0 for space-based ones. Both are used here, ground and
    ///           space, and they are why TESS is cheap (10.5 cm of aperture) while being in orbit
    ///           is what costs.
    ///
    ///   kappa   THE GAME NUMBER, 0.65, and the only place a balance choice enters. Real
    ///           telescopes span six decades of cost, from a 51 mm astrograph to a VLT unit; a
    ///           KSP career can pay across about three. Raising the law to a constant power is
    ///           the one compression that leaves the ORDER and the relative spacing in log space
    ///           exactly as published: no single instrument is nudged, they are all squeezed by
    ///           the same amount. Undiluted, alpha = 2.46 would put the VLT 39 000x above WASP.
    ///
    ///   C       the scale. 44 000 for ground, which is what fixes the RC20 (0.51 m) at 15 000
    ///           funds, the one number kept from the old hand-picked table because it is a good
    ///           first purchase. For space it is 75x that per unit aperture before compression
    ///           (16.5x after), the middle of the range usually quoted for what putting a given
    ///           mirror in orbit multiplies its cost by.
    ///
    /// SCAN COST IS OPERATIONS, WHICH TRACK CAPITAL. An observatory's running budget is a roughly
    /// fixed fraction of what it cost to build, so telescope time is priced at UnlockCost/300 per
    /// night, times how much of a night one observation of that KIND actually consumes: 0.3 for a
    /// photograph, 2 for a radial-velocity run, 3 for a transit campaign. A floor of 20 funds
    /// keeps the smallest instruments from being free to run. This is why SPECULOOS, four 1 m
    /// telescopes, is no longer the free starting instrument and WASP is: WASP is a rack of
    /// camera lenses, and it is genuinely the cheapest thing in the roster to build AND to run.
    ///
    /// WHAT IS STILL UNBALANCED, DELIBERATELY: the photography instruments carry a real price and
    /// return almost no Science, because their gameplay loop does not exist yet. Do not read
    /// their ScienceRewardMultiplier of 0 as a balance decision.
    ///
    /// PHOTOMETRIC PRECISION: every transit instrument here still uses the fitted magnitude
    /// scaling (InstrumentSpec.EstimatePrecision), because none of them yet carries a
    /// PhotometricDetector. The machinery to compute precision from the real electron budget
    /// instead (the CCD equation of Merline &amp; Howell (1995) over the same integrated bandpass,
    /// sky model and extinction the imaging half already uses) is implemented in CcdEquation and
    /// TransitPhotometry, and switches on per instrument the moment a complete detector block is
    /// added here. It stays off until then by design: PhotometricDetector explains why a partly
    /// sourced block is worse than the empirical relation it would replace, and MissingFields()
    /// names exactly which quantities are still needed.
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
                          "Precise, patient, and the first real capital purchase a programme makes: the workhorse it graduates to once WASP has cleared the easy targets.",
            IsSpaceBased = false,
            ApertureMeters = 1.0,          // each SSO unit: 1m Ritchey-Chretien
            SiteAltitudeMeters = 2490.0,   // Paranal SPECULOOS Southern Observatory
            // NO LONGER THE STARTING INSTRUMENT. Four 1 m telescopes is 2 m of effective aperture
            // and the cost law prices it accordingly; a programme does not open with the best
            // small-star photometer in the world. WASP took the free slot, which is also the
            // historical order: hot Jupiters off camera lenses first, ultra-cool dwarfs later.
            UnlockedByDefault = false,
            UnlockCostFunds = 133_000.0,  // D_eff = sqrt(4) * 1 m = 2.0 m
            UnlockScienceThreshold = 65.0,
            ScanCostFunds = 1_330.0,      // transit campaign, 3 nights of a 133 000-funds facility
            ScienceRewardMultiplier = 1.5,

            // Every figure below is from Murray et al. 2020 (MNRAS 495, 2446, "Photometry and
            // performance of SPECULOOS-South") Table 1, except where noted. With this block
            // present, SPECULOOS's photometry runs on the CCD equation instead of the fitted
            // scaling above; see TransitPhotometry.
            Detector = new PhotometricDetector
            {
                ApertureMeters = 1.0,                      // "1 m diameter primary"
                CentralObstructionFraction = 0.28,         // "28 cm diameter secondary" / 1 m primary

                // Two aluminium mirrors (f/8 Ritchey-Chretien) at the mid-recoating-cycle 87%
                // this codebase already uses everywhere, from Ma & Cai (arXiv:1708.01257) Sect.
                // 4.2 (see VisualTelescopeSpec.MirrorReflectivity for the full sourcing)
                // times the I+z' filter's own published ">90 per cent" transmittance.
                //   0.87^2 * 0.90 = 0.681
                OpticsTransmission = 0.87 * 0.87 * 0.90,

                PlateScaleArcsecPerPixel = 0.35,           // "0.35 arcsec pixel^-1", 12x12 arcmin FOV
                ReadNoiseElectrons = 6.2,                  // "6.2 e-" in the 1 MHz readout mode
                DarkCurrentElectronsPerSecond = 0.1,       // "~0.1 e- s^-1 pixel^-1 ... at -60 C"
                GainElectronsPerAdu = 1.04,                // "1.04 e- ADU^-1"

                // The I+z' band, bounded at BOTH ends by published numbers: the filter's own
                // cut-on at 750 nm (Murray et al. 2020 Sect. 2, "transmittance >90 per cent from
                // 750 nm to beyond 1000 nm") and the detector's stated sensitivity limit near
                // 950 nm (Table 1, QE range "~350 (near-UV) to ~950 nm (near-IR)"). The filter's
                // red edge is therefore set by the CCD, not the glass, which is why the band is
                // not carried out to 1000 nm.
                FilterCentralWavelengthNm = 850.0,         // midpoint of 750-950
                FilterWidthNm = 200.0,                     // 950 - 750

                // KNOWN UPPER BOUND, and the one soft number in this block. The published figures
                // are a 94% peak at 740 nm (Murray et al. 2020 Table 1) and ">90% at 750 nm" for
                // the Andor BEX2-DD sensor, both at the BLUE EDGE of the band above, where this
                // detector is at its best. The real curve falls away toward 950 nm, so holding
                // 0.90 flat across 750-950 nm overstates the collected electrons and makes the
                // resulting precision optimistic; the error is signed and bounded, unlike the
                // empirical relation this replaces, but it is real.
                //
                // Removing it needs the measured BEX2-DD curve digitised into a SpectralCurve and
                // assigned to QuantumEfficiencyCurve below, exactly what FilterCurves.cs already
                // does for FORS2, and exactly the error SystemBandpass.cs was written to fix
                // (using QE_peak there overstated FORS2's b_HIGH band by 1.33x). Until then this
                // is deliberately the band's blue edge rather than its 94% peak.
                QuantumEfficiency = 0.90,

                // Paranal DIMM median, from the 2016-DIMM over 2016 April - 2018 April; the
                // 1998-DIMM's older 0.98 arcsec figure is known to over-estimate poor seeing
                // (Butterley et al. 2024, MNRAS 529, 320, comparison of turbulence profilers at
                // Paranal). SPECULOOS-South sits on the same platform.
                MedianZenithSeeingArcsec = 0.69,

                // ExposureSeconds left null, so CadenceSeconds (30 s) is used as the open-shutter
                // time. That sits inside the "10-60s" typical range Murray et al. 2020 Sect. 3
                // quotes, and SPECULOOS reads out once per point, so IntegrationsPerMeasurement
                // stays 1.

                Citation = "Murray et al. 2020, MNRAS 495, 2446, Table 1 (aperture, obstruction, plate scale, "
                         + "read noise, dark current, gain, QE range, I+z' filter); Ma & Cai arXiv:1708.01257 "
                         + "Sect. 4.2 (aluminium coating 87%/surface); Butterley et al. 2024, MNRAS 529, 320 "
                         + "(Paranal 2016-DIMM median seeing 0.69 arcsec). QE held at the band's blue-edge "
                         + "figure; see the comment above, it is an upper bound.",
            },
        };

        public static readonly InstrumentSpec Wasp = new InstrumentSpec
        {
            Name = "WASP",
            DisplayName = "WASP (Transit)",
            Method = DetectionMethod.Transit,
            ReferenceMagnitude = 9.5,
            ReferencePrecision = 1000.0,  // ppm (~1 mmag), small 200mm-lens apertures, wide field
            PrecisionExponent = 0.2,
            CadenceSeconds = 600.0,       // ~10 min imaging cadence per field
            Citation = "Pollacco et al. 2006. SuperWASP: wide-field survey with 200mm camera lenses, bright-star hot-Jupiter hunting.",
            Description = "An array of off-the-shelf 200mm camera lenses on a single mount, photographing huge swaths of sky every few minutes. " +
                          "No giant mirror, no exotic optics: just relentless wide-field coverage of bright stars, looking for transit dips. " +
                          "Individually noisy, but it discovered nearly two hundred hot Jupiters by sheer persistence. " +
                          "The cheapest telescope time available: ideal for burning through easy bright targets on a budget.",
            IsSpaceBased = false,
            ApertureMeters = 0.111,        // Canon 200mm f/1.8 lens: 111mm entrance pupil, scintillation-limited on bright stars, WASP's real noise regime
            SiteAltitudeMeters = 2400.0,   // Roque de los Muchachos, La Palma (SuperWASP-North)
            // THE STARTING TRANSIT INSTRUMENT. The cost law makes it the cheapest thing in the
            // roster to build (1 300 funds notional, on 111 mm of entrance pupil) and its nights
            // hit the 20-funds floor, so it is the one instrument a career can run freely from
            // the first day. Its precision is also the worst here by a factor of seven, which is
            // the right opening: bright stars and hot Jupiters, exactly what SuperWASP found.
            UnlockedByDefault = true,
            UnlockCostFunds = 0.0,
            UnlockScienceThreshold = 0.0,
            ScanCostFunds = 20.0,
            ScienceRewardMultiplier = 1.0,

            // NO Detector BLOCK: SuperWASP is two numbers short of one, so it stays on the fitted
            // scaling above. Sourced already, from Pollacco et al. 2006 (PASP 118, 1407), for
            // whoever finishes it:
            //   aperture 111 mm (Canon 200 mm f/1.8 lens), obstruction 0 (refractive, no secondary)
            //   e2v 2048x2048, 13.5 um pixels, in an Andor DW436     (Sect. 2.3)
            //   plate scale 13.7 arcsec/pixel, FOV 7.8 x 7.8 deg     (Sect. 2.4)
            //   read noise "~8-10 electrons", gain "~2" e-/ADU       (Sect. 2.3)
            //   dark current ~72 e- pixel^-1 hr^-1 at -50 C = 0.020 e-/pixel/s   (Sect. 2.3)
            //   peak QE >90%, back-illuminated                       (Sect. 2.3)
            //   passband 400-700 nm -> central 550 nm, width 300 nm  (Sect. 2.4)
            //   ExposureSeconds = 30, against the 600 s CadenceSeconds above (Sect. 2.3/4.1)
            //
            // WHAT BLOCKS IT: at 13.7 arcsec/pixel against sub-arcsecond seeing this instrument is
            // drastically undersampled, so CcdEquation's seeing-derived optimal aperture is
            // meaningless; the aperture is set by the pixel grid and by the survey's own
            // reduction. It needs PhotometricApertureRadiusPixels and a delivered PSF FWHM, and
            // neither is in Pollacco et al. 2006. Optics transmission for the Canon lens is not
            // published either, and a camera lens is not a mirror train the 0.87^N rule covers.
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
            // The space law, and the one place it visibly disagrees with intuition: four 10.5 cm
            // cameras is 21 cm of effective aperture, so almost nothing here is being paid for
            // the optics. The 16.5x space premium is the entire price, which is the honest
            // description of TESS as a mission.
            UnlockCostFunds = 96_000.0,   // D_eff = sqrt(4) * 0.105 m = 0.21 m, space law
            UnlockScienceThreshold = 50.0,
            ScanCostFunds = 950.0,        // transit campaign, 3 nights
            ScienceRewardMultiplier = 2.0,

            // NO Detector BLOCK: one number short. Sourced already, from Ricker et al. 2015
            // (JATIS 1, 014003), Sullivan et al. 2015 (ApJ 809, 77) Sect. 2, and the TESS
            // telescope information page:
            //   entrance pupil 105 mm -> 86.6 cm^2 geometric; effective collecting area 69 cm^2
            //     "after accounting for transmissive losses in the lenses and their coatings",
            //     which makes OpticsTransmission = 69/86.6 = 0.797 and obstruction 0 (refractive)
            //   MIT/LL CCID-80 deep-depletion, 15 um pixels, 2048x2048, 21.1 arcsec/pixel
            //   read noise 10 e- pixel^-1 RMS, incurred on EVERY 2 s image
            //   ExposureSeconds = 2, IntegrationsPerMeasurement = 60 for the 2-min cadence
            //   bandpass 600-1000 nm, centred on Cousins I_C, effective wavelength 786.5 nm
            //   dark current: not published as a value; CCDs run at -75 C to suppress it
            //
            // WHAT BLOCKS IT: no band-averaged quantum efficiency is published. Sullivan et al.
            // 2015 state outright that the 69 cm^2 covers lens transmission ONLY and that the CCD
            // QE is treated separately, but give no figure for it; the only QE number found is 40%
            // at 1 um, which is the extreme red edge and not a band average. The mission publishes
            // a full spectral response function instead, and digitising that into a SpectralCurve
            // is the right fix; it would also make the 786.5 nm effective wavelength fall out of
            // the integral rather than being asserted.
            //
            // Secondly, TESS is deliberately undersampled: its PSF is quoted as a "50%
            // ensquared-energy half-width of 15 micron", i.e. one pixel, which is not a FWHM and
            // does not convert into one without assuming a profile.
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
            UnlockCostFunds = 340_000.0,  // 3.6 m, ground law
            UnlockScienceThreshold = 120.0,
            ScanCostFunds = 2_275.0,      // RV run, 2 nights
            ScienceRewardMultiplier = 2.5,
        };

        public static readonly InstrumentSpec Espresso = new InstrumentSpec
        {
            Name = "ESPRESSO",
            DisplayName = "ESPRESSO (Radial Velocity)",
            Method = DetectionMethod.RadialVelocity,
            ReferenceMagnitude = 8.0,
            ReferencePrecision = 0.15,    // m/s, near the instrument's best-case sub-10cm/s spec on bright quiet stars
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
            // Same 8.2 m unit telescope as FORS2 and SPHERE, so the aperture term is identical
            // and the three prices differ only by an instrument multiplier: 1.0 for a workhorse
            // imager, 1.25 for SPHERE's extreme-AO bench, 1.5 for an ultra-stable vacuum
            // spectrograph. That ordering is not in doubt; the exact factors are a balance
            // choice, unlike the aperture term above them.
            UnlockCostFunds = 1_910_000.0,  // 8.2 m ground law x 1.5
            UnlockScienceThreshold = 350.0,
            ScanCostFunds = 12_700.0,       // RV run, 2 nights on the dearest facility in the roster
            ScienceRewardMultiplier = 4.0,
        };

        public static readonly InstrumentSpec Sophie = new InstrumentSpec
        {
            Name = "SOPHIE",
            DisplayName = "SOPHIE (Radial Velocity)",
            Method = DetectionMethod.RadialVelocity,
            ReferenceMagnitude = 8.0,
            ReferencePrecision = 2.0,     // m/s, smaller 1.93m aperture than HARPS, needs brighter targets for similar S/N
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
            UnlockCostFunds = 126_000.0,  // 1.93 m, ground law
            UnlockScienceThreshold = 60.0,
            ScanCostFunds = 840.0,        // RV run, 2 nights
            ScienceRewardMultiplier = 1.5,
        };

        public static readonly InstrumentSpec Elt = new InstrumentSpec
        {
            Name = "ELT",
            DisplayName = "ELT (Direct Imaging)",
            Method = DetectionMethod.DirectImaging,
            ReferenceMagnitude = 6.0,
            // For imaging, "precision" is the 5-sigma contrast floor at 1 lambda/D
            // after 1 hour of integration, order-of-magnitude for next-generation
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
            // NOT OFFERED. The direct-imaging physics behind this instrument is the one path in
            // the mod that is still an ordering heuristic rather than a performance model: the
            // planet radiates as a blackbody at its equilibrium temperature (which makes every
            // real directly-imaged planet undetectable), the contrast floor scales with target
            // magnitude by a photon-noise law in a speckle-limited regime, its radial law is a
            // chosen inverse square, and integration improves it as sqrt(t) without bound. The
            // better model already exists in this codebase for SPHERE (Core/SpeckleField,
            // Coronagraph, AngularDifferentialImaging, ContrastCurve) and is not wired here yet.
            // TECHNICAL_REFERENCE section 12.3 and section 12 items 112-120 carry the detail.
            UnderConstruction = true,
            // THE ONE INSTRUMENT THE APERTURE LAW IS NOT ALLOWED TO PRICE. van Belle, Meinel &
            // Meinel report that the monolithic-mirror relation breaks for segmented apertures:
            // Keck, LBT and GTC all come in materially under it. At 39.3 m the law would ask
            // 15.6 M funds, twelve times a VLT unit, where the real ELT is costed at about
            // EUR 1.45 B against roughly EUR 330 M for one VLT unit, a factor of 4.4. That
            // measured ratio is used directly instead, through the same kappa compression:
            // 4.4^0.65 = 2.62 times the FORS2 baseline.
            UnlockCostFunds = 3_330_000.0,
            UnlockScienceThreshold = 500.0,
            ScanCostFunds = 11_100.0,     // a direct-imaging ADI sequence is about one night
            ScienceRewardMultiplier = 6.0,
        };

        public static readonly InstrumentSpec RedCat51 = new InstrumentSpec
        {
            Name = "RedCat51",
            DisplayName = "William Optics RedCat 51 (Wide-Field Astrograph)",
            Method = DetectionMethod.SolarSystemPhotography,
            // Exoplanet-detection fields zeroed out; this instrument doesn't do exoplanet science.
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "William Optics RedCat 51: 51 mm f/4.9 Petzval apochromatic astrograph, 250 mm focal length, quadruplet FPL-53 " +
                       "objective, flat corrected field over a 45 mm image circle (williamoptics.com). Sited at the Observatoire de " +
                       "Haute-Provence (650 m), median seeing 2.5 arcsec per Schmitt et al. 2024, A&A 687, A198.",
            Description = "Fifty-one millimeters of glass, and the most useful telescope in this catalog for one specific job. Every other " +
                          "instrument here is built to resolve: long focus, narrow field, a planet filling the frame. This one is built to " +
                          "COVER: 250 mm of focal length opens a 4.4 x 3.0 degree field, forty times wider than the RC20's, and roughly eight " +
                          "hundred real catalog stars land in every exposure instead of four. Point it at a planet and you get a bright dot. " +
                          "Point it at anything at all and you get a sky.",
            IsSpaceBased = false,
            ApertureMeters = VisualTelescopeCatalog.RedCat51.ApertureMeters,
            SiteAltitudeMeters = VisualTelescopeCatalog.RedCat51.SiteAltitudeMeters, // Observatoire de Haute-Provence
            VisualTelescope = VisualTelescopeCatalog.RedCat51,
            // THE STARTING PHOTOGRAPHIC INSTRUMENT, the counterpart to WASP on the detection
            // side. The cost law puts 51 mm of aperture at 380 funds notional, which is close
            // enough to nothing that charging for it would be noise; a career opens holding it.
            // Its nights hit the 20-funds floor for the same reason.
            UnlockedByDefault = true,
            UnlockCostFunds = 0.0,
            UnlockScienceThreshold = 0.0,
            ScanCostFunds = 20.0,
            ScienceRewardMultiplier = 0.0, // no detections to reward; this instrument doesn't feed the science-reward economy
        };

        public static readonly InstrumentSpec Rc20 = new InstrumentSpec
        {
            Name = "RC20",
            DisplayName = "PlaneWave RC20 (Amateur Astrograph)",
            Method = DetectionMethod.SolarSystemPhotography,
            // Exoplanet-detection fields zeroed out; this instrument doesn't do exoplanet science.
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "PlaneWave Instruments RC20: 20-inch (0.51 m) Ritchey-Chretien astrograph, the class of telescope used at university " +
                       "observatories for hands-on amateur/semi-pro imaging. Sited at the Observatoire de Haute-Provence (650 m), whose " +
                       "median seeing of 2.5 arcsec is published in Schmitt et al. 2024, A&A 687, A198.",
            Description = "A 20-inch Ritchey-Chretien astrograph, the kind of telescope a university observatory or a serious amateur actually " +
                          "owns, not a billion-Fund flagship. Point it at anything in the neighborhood (Duna, Jool, the Mun) and it takes a " +
                          "real photograph: true position, phase, and relative brightness, straight off the sensor. No spectra, no light curves, " +
                          "just a picture, monochrome and grainy the way a real long-exposure frame off an amateur CCD looks before anyone " +
                          "stacks and processes it.",
            IsSpaceBased = false,
            ApertureMeters = VisualTelescopeCatalog.Rc20.ApertureMeters,
            SiteAltitudeMeters = VisualTelescopeCatalog.Rc20.SiteAltitudeMeters, // Observatoire de Haute-Provence
            VisualTelescope = VisualTelescopeCatalog.Rc20,
            // 0.51 m through the ground law. This is the instrument the scale constant C was
            // chosen against, so 15 000 is where every other price in the file hangs from.
            UnlockedByDefault = false,
            UnlockCostFunds = 15_000.0,
            UnlockScienceThreshold = 10.0,
            ScanCostFunds = 20.0,         // a photograph is 0.3 of a night; hits the floor
            ScienceRewardMultiplier = 0.0, // no detections to reward; this instrument doesn't feed the science-reward economy
        };

        public static readonly InstrumentSpec Cdk1000 = new InstrumentSpec
        {
            Name = "CDK1000",
            DisplayName = "PlaneWave CDK1000 (Research Astrograph)",
            Method = DetectionMethod.SolarSystemPhotography,
            // Exoplanet-detection fields zeroed out; this instrument doesn't do exoplanet science.
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "PlaneWave Instruments CDK1000: 1-meter (39.4 in) Corrected Dall-Kirkham astrograph, f/6, 47% central obstruction " +
                       "(planewave.com). A real unit was installed at Palomar Observatory in 2024 to support MIT's WINTER project and " +
                       "Caltech research.",
            Description = "A full meter of aperture, research-observatory class, the same telescope PlaneWave installed at Palomar in 2024. " +
                          "Nearly four times the RC20's light-collecting area and close to double its resolving power, with a corrected " +
                          "Dall-Kirkham design that cancels off-axis coma AND astigmatism (an RC only cancels coma), so the field stays flat " +
                          "corner to corner. That reach is what it takes to frame the small, faint, or distant bodies the RC20 can't " +
                          "usefully resolve, at real research-instrument cost.",
            IsSpaceBased = false,
            ApertureMeters = VisualTelescopeCatalog.Cdk1000.ApertureMeters,
            SiteAltitudeMeters = VisualTelescopeCatalog.Cdk1000.SiteAltitudeMeters, // Palomar Observatory
            VisualTelescope = VisualTelescopeCatalog.Cdk1000,
            // Placeholders, balance à valider avec Baptiste. Strictly better than the RC20 in
            // every optical respect, so priced and gated above it per this file's own "bigger
            // investment -> bigger payoff" ordering rule.
            UnlockedByDefault = false,
            UnlockCostFunds = 44_000.0,   // 1.0 m, ground law
            UnlockScienceThreshold = 25.0,
            ScanCostFunds = 45.0,         // a photograph is 0.3 of a night
            ScienceRewardMultiplier = 0.0, // no detections to reward; this instrument doesn't feed the science-reward economy
        };

        public static readonly InstrumentSpec Fors2Vlt = new InstrumentSpec
        {
            Name = "VLT FORS2",
            DisplayName = "VLT UT1 + FORS2 (Flagship Astrograph)",
            Method = DetectionMethod.SolarSystemPhotography,
            // Exoplanet-detection fields zeroed out; this instrument doesn't do exoplanet science.
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "ESO Very Large Telescope, Unit Telescope 1 (Antu), 8.2m, fitted with the real FORS2 imager: mosaic of two MIT/LL " +
                       "CCID20 CCDs, 15um pixels, 0.126\"/pixel (eso.org FORS2 User Manual and Standard Filters page). Same Paranal site " +
                       "(2635m) already used for ESPRESSO in this mod.",
            Description = "Not a hobbyist instrument at all: one of the four 8.2m Unit Telescopes of the actual Very Large Telescope, " +
                          "carrying its real optical imager/spectrograph, FORS2. Every number driving this camera (aperture, plate scale, " +
                          "the real MIT CCID20 detector, its real filters) is FORS2's own published spec, not a reskinned amateur camera. " +
                          "Sixteen times the RC20's raw aperture area puts the faintest, smallest, most distant bodies in the system within " +
                          "reach at last. The gain dial is gone, too: a real research CCD doesn't have one; what you get is what the " +
                          "hardware's own fixed readout mode gives you.",
            IsSpaceBased = false,
            ApertureMeters = VisualTelescopeCatalog.Fors2Vlt.ApertureMeters,
            SiteAltitudeMeters = VisualTelescopeCatalog.Fors2Vlt.SiteAltitudeMeters, // Paranal Observatory
            VisualTelescope = VisualTelescopeCatalog.Fors2Vlt,
            // 8.2 m through the ground law, with the baseline instrument multiplier of 1.0: this
            // is the cheapest of the three ways into the VLT, and the jump over the CDK1000 is
            // 29x because that is what the published cost law says an 8.2 m mirror is worth
            // against a 1 m one, not because a step was wanted here.
            UnlockedByDefault = false,
            UnlockCostFunds = 1_270_000.0,
            UnlockScienceThreshold = 250.0,
            ScanCostFunds = 1_270.0,      // a photograph is 0.3 of a night, on a very expensive night
            ScienceRewardMultiplier = 0.0, // no detections to reward; this instrument doesn't feed the science-reward economy
        };

        public static readonly InstrumentSpec Sphere = new InstrumentSpec
        {
            Name = "VLT SPHERE",
            DisplayName = "VLT UT3 + SPHERE (Adaptive-Optics Astrograph)",
            Method = DetectionMethod.SolarSystemPhotography,
            // Exoplanet-detection fields zeroed out; this instrument doesn't do exoplanet science.
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "ESO Very Large Telescope, Unit Telescope 3 (Melipal), 8.2m, fitted with the real SPHERE/ZIMPOL extreme-AO imaging " +
                       "polarimeter: SAXO adaptive optics, ~25 mas achieved FWHM, real CCD (640,000 e- full well) (Schmid et al. 2018, " +
                       "A&A 619, A9). Same Paranal site (2635m) as FORS2, different Unit Telescope.",
            Description = "The same VLT, a different real instrument, solving a different real problem: FORS2 is seeing-limited; " +
                          "Paranal's own atmosphere blurs it to about an arcsecond no matter the mirror size. SPHERE instead corrects " +
                          "that turbulence in real time with its SAXO adaptive-optics system, reaching about 25 milliarcseconds of real " +
                          "resolution, some 24 to 40 times finer. That's what it takes to actually resolve the system's smallest, most " +
                          "marginal bodies, not just collect more of their light. The tradeoff is real too: ZIMPOL's true field of view " +
                          "is barely 3.6 arcseconds wide, and it has no blue filter at all, a specialist's instrument, not a generalist's.",
            IsSpaceBased = false,
            ApertureMeters = VisualTelescopeCatalog.Sphere.ApertureMeters,
            SiteAltitudeMeters = VisualTelescopeCatalog.Sphere.SiteAltitudeMeters, // Paranal Observatory
            VisualTelescope = VisualTelescopeCatalog.Sphere,
            // Placeholders, balance à valider avec Baptiste. A specialist upgrade rather than a
            // strict step up from the CDK1000/FORS2 (tiny FOV, no blue channel). Same 8.2 m
            // mirror as FORS2, so the same aperture term, times the 1.25 instrument multiplier
            // that SAXO and ZIMPOL are worth over a workhorse imager.
            UnlockedByDefault = false,
            UnlockCostFunds = 1_590_000.0,
            UnlockScienceThreshold = 300.0,
            ScanCostFunds = 1_590.0,      // a photograph is 0.3 of a night
            ScienceRewardMultiplier = 0.0, // no detections to reward; this instrument doesn't feed the science-reward economy
        };

        /// <summary>
        /// The orbital telescope, and the one entry in this catalogue that is not bought.
        ///
        /// EVERY OTHER INSTRUMENT HERE IS TELESCOPE TIME. The player pays Funds for an allocation
        /// on a facility that already exists and that somebody else built; that is what the
        /// unlock economy in this file models, and it is what observing on a real ground
        /// telescope is. This one is different in kind: nobody sells time on it, because it does
        /// not exist until the player launches it. So UnlockCostFunds is zero and
        /// UnlockedByDefault is false, and neither is doing the gating; the gate is whether there
        /// is an operational telescope in orbit, which ExoInstrumentsGUI asks
        /// SpaceTelescopeRegistry rather than the scenario's unlock list.
        ///
        /// The cost is still real, it is just paid somewhere else: in the part's entry cost, in
        /// the launch, and in having built a spacecraft that can hold a target steady enough to
        /// photograph through. That is the right place for it in a game about launching things.
        ///
        /// ScanCostFunds is likewise zero. There is no facility to bill: the observatory is
        /// commanding hardware it already owns, and the consumables it burns doing so are
        /// electric charge and, if the attitude is on thrusters, propellant. Both are already
        /// modelled where they are actually spent.
        /// </summary>
        public static readonly InstrumentSpec OrbitalObservatory = new InstrumentSpec
        {
            Name = "Orbital Observatory",
            DisplayName = "Orbital Observatory (Space Telescope)",
            Method = DetectionMethod.SolarSystemPhotography,
            ReferenceMagnitude = 0.0,
            ReferencePrecision = 0.0,
            PrecisionExponent = 0.0,
            CadenceSeconds = 0.0,
            Citation = "Hubble Space Telescope Optical Telescope Assembly (2.4 m f/24 Ritchey-Chretien; HST Primer, "
                     + "Cycle 34) with Wide Field Camera 3's UVIS channel (WFC3 Instrument Handbook, Cycle 24). "
                     + "Pupil geometry from Tiny Tim's wfc3_uvis1.pup (Krist & Hook). See "
                     + "VisualTelescopeCatalog.HubbleWfc3Uvis for the full per-figure sourcing.",
            Description = "A 2.4-metre telescope you have to put in orbit yourself. It does not out-resolve the "
                        + "VLT and it does not out-collect it; on both counts it loses to instruments already in "
                        + "this list. What it has instead is the near-ultraviolet, which the air blocks outright "
                        + "and no mountain gets above; a point-spread function that is identical in every frame "
                        + "ever taken, because there is no atmosphere to vary; and a sky about 1.6 magnitudes "
                        + "darker, because airglow is something an atmosphere does. It also has constraints no "
                        + "ground telescope has: the planet occults most targets for part of every orbit, the "
                        + "Sun and the sunlit limb are hard exclusion zones, and every exposure is only as sharp "
                        + "as the spacecraft's attitude control can hold.",
            IsSpaceBased = true,
            ApertureMeters = VisualTelescopeCatalog.HubbleWfc3Uvis.ApertureMeters,
            SiteAltitudeMeters = 0.0,
            VisualTelescope = VisualTelescopeCatalog.HubbleWfc3Uvis,
            UnlockedByDefault = false,
            UnlockCostFunds = 0.0,
            UnlockScienceThreshold = 0.0,
            ScanCostFunds = 0.0,
            ScienceRewardMultiplier = 0.0, // no detections to reward; this instrument doesn't feed the science-reward economy
        };

        public static readonly InstrumentSpec[] All =
        {
            Speculoos, Wasp, Tess, Harps, Espresso, Sophie, Elt, RedCat51, Rc20, Cdk1000, Fors2Vlt, Sphere,
            OrbitalObservatory
        };
    }
}
