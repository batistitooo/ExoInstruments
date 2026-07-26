using ExoInstruments.Visualization;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Physical identity of one visual (solar-system photography) telescope+camera setup:
    /// optics, sensor, and capture range. This is everything SolarSystemCameraTexture's
    /// rendering pipeline needs to know about which instrument it's simulating -- kept out of
    /// the pipeline itself so a new visual telescope (e.g. a cheap beginner scope for the Mun,
    /// or a larger instrument that can reach the small/distant planets the RC20 can't) is a new
    /// entry in VisualTelescopeCatalog below, not a change to the rendering code.
    /// </summary>
    public sealed class VisualTelescopeSpec
    {
        public string Name;

        // Optics
        public double ApertureMeters;
        public double FocalLengthMeters;
        public double BarlowFactor;
        public double SecondaryObstructionFraction;

        // Site (feeds the shared atmospheric/scintillation model in AtmosphericImagingNoise)
        public double SiteAltitudeMeters;

        // Sensor
        public int NativeSensorWidthPx;
        public int NativeSensorHeightPx;
        public double NativePixelSizeMeters;
        public double QuantumEfficiency;
        public double FullWellElectrons;
        public double ReadNoiseElectrons;
        /// <summary>Dark current at this sensor's own real cooled operating temperature (see each entry's comment for the actual temperature -- it varies by instrument, so it doesn't belong in the field name).</summary>
        public double DarkCurrentElectronsPerSecond;

        // Capture range
        public float MinExposureSeconds;
        public float MaxExposureSeconds;
        /// <summary>Continuously-variable electronic gain range. Set MinGain == MaxGain for a real instrument whose gain is fixed by its readout electronics rather than player-adjustable (e.g. a professional CCD with no ISO-like control) -- see VisualTelescopeCatalog.Fors2Vlt.</summary>
        public float MinGain;
        public float MaxGain;

        // Filters: real bandwidth (FWHM, Angstrom) per filter-wheel position. Luminance is the
        // wide/clear reference; R/G/B and HAlpha are each their own real filter on instruments
        // that have one (not assumed fractions of Luminance) -- see each entry's comment.
        public double LuminanceBandwidthAngstrom;
        public double RedBandwidthAngstrom;
        public double GreenBandwidthAngstrom;
        public double BlueBandwidthAngstrom;
        public double HAlphaBandwidthAngstrom;

        // Real CENTRAL wavelength (nm) per filter-wheel position. Separate from the bandwidths
        // above because diffraction cares about where the passband sits, not how wide it is:
        // the whole PSF scales as lambda/D (see OpticalPsf), so the same telescope genuinely
        // resolves finer through a blue filter than a red one -- a real, measurable effect that
        // a single instrument-wide wavelength would erase. Each entry's own comment sources its
        // filter set; a position the instrument doesn't physically have is left at 0 and is
        // unreachable (see AvailableFilters).
        public double LuminanceCentralWavelengthNm;
        public double RedCentralWavelengthNm;
        public double GreenCentralWavelengthNm;
        public double BlueCentralWavelengthNm;
        public double HAlphaCentralWavelengthNm;

        /// <summary>
        /// Which CameraFilter positions actually exist as a real filter on this instrument --
        /// the GUI's filter wheel only offers these. Most instruments carry all five; an
        /// instrument with a real gap (e.g. ZIMPOL has no broadband blue filter -- its filter
        /// set targets red/near-IR reflected-light and circumstellar science, not true-color
        /// RGB) simply omits that entry rather than a made-up bandwidth standing in for a
        /// filter that doesn't exist.
        /// </summary>
        public CameraFilter[] AvailableFilters;

        /// <summary>
        /// Real AO-corrected resolution (FWHM, arcsec) this instrument achieves under good
        /// conditions, for an instrument with genuine adaptive optics -- see
        /// SolarSystemCameraTexture.ComputeSeeingBlurPx, which uses this INSTEAD OF the plain
        /// airmass-based seeing model when it's nonzero. 0 (default) means no adaptive optics:
        /// the plain ground-based seeing model applies, same as every telescope before SPHERE.
        /// </summary>
        public double AdaptiveOpticsFwhmArcsec;

        /// <summary>
        /// Strehl ratio this AO system really achieves -- the fraction of the light it actually
        /// concentrates into the diffraction-limited core. Only meaningful alongside
        /// AdaptiveOpticsFwhmArcsec.
        ///
        /// This is what makes a real AO point-spread function two-component rather than one
        /// broadened blob: a corrected core carrying this fraction, plus a wide halo carrying
        /// the rest (see AdaptiveOpticsHaloSeeingFwhmArcsec, and OpticalPsf.BuildAdaptiveOptics
        /// Kernel). Collapsing the two into a single profile of the right total FWHM gets the
        /// width right but puts far too much light at intermediate scales, which is exactly
        /// where a resolved planetary disk's surface detail lives -- it smears features that a
        /// real AO frame keeps sharp on top of a diffuse background.
        /// </summary>
        public double AdaptiveOpticsStrehlRatio;

        /// <summary>Seeing FWHM (arcsec) of the uncorrected halo the AO leaves behind -- the site's own real median seeing, since the halo is simply the light the correction failed to gather.</summary>
        public double AdaptiveOpticsHaloSeeingFwhmArcsec;

        /// <summary>
        /// True for an instrument that always has precision active tracking, with no real bare/
        /// unguided operating mode -- a professional research telescope like the VLT is never
        /// pointed without one, unlike an amateur astrograph a player might genuinely run
        /// without an autoguider attached. When true, SolarSystemCameraTexture forces its
        /// Autoguiding property on and locks the GUI toggle, instead of leaving drift/trailing
        /// as a player choice.
        /// </summary>
        public bool AlwaysAutoguided;

        // Off-axis aberration: peak astigmatism blur (pixels) at the sensor's corner. The
        // radial-quadratic FALLOFF this drives is the same optical physics for any two-mirror
        // astrograph (Seidel aberration theory -- see SolarSystemCameraTexture's own comment on
        // ApplyAstigmatismBlur), but the PEAK amplitude depends on how completely THIS
        // telescope's own design cancels off-axis aberrations, so it lives per-instrument here
        // rather than as one pipeline-wide constant.
        public float AstigmatismStrengthPxAtCorner;
    }

    /// <summary>
    /// Catalog of visual telescopes selectable in-game. Each one that should appear in the
    /// Observatory dropdown needs a matching InstrumentSpec in Observatories.cs (Method =
    /// SolarSystemPhotography, VisualTelescope = the entry below) -- picking that row calls
    /// SolarSystemCameraTexture.SetActiveTelescope. Add another VisualTelescopeSpec here -- e.g.
    /// a beginner Mun-class refractor -- add it to All, and give it an Observatories.cs entry to
    /// ship a third instrument; the rendering pipeline needs no further changes.
    /// </summary>
    public static class VisualTelescopeCatalog
    {
        /// <summary>Every filter position, for an instrument (RC20/CDK1000/FORS2) that really has all five.</summary>
        private static readonly CameraFilter[] AllFilters =
            { CameraFilter.Luminance, CameraFilter.Red, CameraFilter.Green, CameraFilter.Blue, CameraFilter.HAlpha };

        /// <summary>
        /// PlaneWave RC20: 20-inch (0.51m) Ritchey-Chretien astrograph at f/6.8, 3.468m focal
        /// length, 39% secondary obstruction (planewave.eu product page), paired with a real 4x
        /// Barlow for the "high power" end of the zoom range. Camera is a ZWO ASI294MM Pro mono
        /// CCD (zwoastro.com/product/asi294): 4144x2822 native resolution, 4.63um pixel pitch,
        /// 90% peak QE, 66,000 e- full well, 1.2 e- read noise (best case), 0.0022 e-/s/pixel
        /// dark current at -20C, 32us-2000s exposure range. Site altitude is ETH Zurich's own
        /// observatory, the real institution this instrument class is modeled on (see
        /// Observatories.Rc20 for the career-economy side of this same instrument).
        ///
        /// Filters: a real LRGB astro filter wheel has no single published per-channel bandwidth
        /// the way a research instrument's named filters do, so R/G/B keep the even-third-of-L
        /// split (modern "1:1:1 balanced" CMOS LRGB design -- see FilterThroughput's own comment)
        /// and HAlpha keeps the real ~7nm narrowband figure.
        ///
        /// Astigmatism: for a true Ritchey-Chretien, third-order coma is corrected to zero by
        /// the RC hyperbolic-mirror design itself -- that is the entire reason the RC form
        /// exists (Ritchey &amp; Chretien 1922). The dominant remaining off-axis third-order
        /// (Seidel) aberration for this telescope class is astigmatism. Its absolute amplitude
        /// depends on the telescope's actual optical prescription (focal ratio, field curvature
        /// radius), which no published PlaneWave RC20 datasheet specifies to the precision an
        /// aberration coefficient would need -- 3.0px at the frame corner is a display
        /// calibration, not a measured quantity.
        /// </summary>
        public static readonly VisualTelescopeSpec Rc20 = new VisualTelescopeSpec
        {
            Name = "RC20",

            ApertureMeters = 0.51,
            FocalLengthMeters = 0.51 * 6.8,
            BarlowFactor = 4.0,
            SecondaryObstructionFraction = 0.39,
            SiteAltitudeMeters = 560.0,

            NativeSensorWidthPx = 4144,
            NativeSensorHeightPx = 2822,
            NativePixelSizeMeters = 4.63e-6,
            QuantumEfficiency = 0.90,
            FullWellElectrons = 66000.0,
            ReadNoiseElectrons = 1.2,
            DarkCurrentElectronsPerSecond = 0.0022, // ZWO ASI294MM Pro, cooled to -20C

            MinExposureSeconds = 0.000032f,
            MaxExposureSeconds = 2000.0f,
            MinGain = 0.7f,
            MaxGain = 8.0f,

            LuminanceBandwidthAngstrom = 2650.0,
            RedBandwidthAngstrom = 2650.0 / 3.0,
            GreenBandwidthAngstrom = 2650.0 / 3.0,
            BlueBandwidthAngstrom = 2650.0 / 3.0,
            HAlphaBandwidthAngstrom = 70.0,

            // Amateur LRGB set: L is the real ~420-685nm visible band this filter class covers
            // (centre 552.5nm), and R/G/B are its even thirds -- the same 1:1:1 balanced split
            // the bandwidths above already assume, so the centres fall at the midpoint of each
            // third (B 420-508.3, G 508.3-596.7, R 596.7-685nm). H-alpha is the real line.
            LuminanceCentralWavelengthNm = 552.5,
            RedCentralWavelengthNm = 640.8,
            GreenCentralWavelengthNm = 552.5,
            BlueCentralWavelengthNm = 464.2,
            HAlphaCentralWavelengthNm = 656.3,

            AvailableFilters = AllFilters,
            AstigmatismStrengthPxAtCorner = 3.0f,
        };

        /// <summary>
        /// PlaneWave CDK1000: 1.0m (1000mm / 39.37") Corrected Dall-Kirkham astrograph at f/6,
        /// 6000mm focal length, 47% central obstruction of the primary mirror diameter (all
        /// planewave.com official CDK1000 product page specs -- the same optical tube PlaneWave
        /// also sells as part of the "PW1000" 1-meter observatory system). A real one of these
        /// was installed at Palomar Observatory, California (1712m altitude, per its Wikipedia
        /// entry) in 2024 to support MIT's WINTER project and Caltech research -- used here as
        /// the site altitude, since PlaneWave's own product page doesn't specify a site. Paired
        /// with a real 4x Barlow for the "high power" end of the zoom range, same accessory
        /// class as the RC20 (see VisualTelescopeCatalog.Rc20). Camera is the same real ZWO
        /// ASI294MM Pro mono CCD as the RC20 (zwoastro.com/product/asi294) -- a genuine, common
        /// prosumer pairing on CDK-class instruments, not invented for this entry; see Rc20's own
        /// comment for the sensor's full datasheet sourcing (4144x2822 native resolution, 4.63um
        /// pixel pitch, 90% peak QE, 66,000 e- full well, 1.2 e- read noise, 0.0022 e-/s/pixel
        /// dark current at -20C, 32us-2000s exposure range).
        ///
        /// Net result vs. the RC20, both through the same sensor/Barlow: aperture diameter ratio
        /// 1000mm/510mm = 1.961, so despite the larger 47%-vs-39% obstruction, raw-area-ratio
        /// (1.961^2=3.845) * obstruction-factor-ratio ((1-0.47^2)/(1-0.39^2)=0.919) = ~3.53x the
        /// RC20's effective light-collecting area -- plus a Dawes-limit resolving power
        /// (116/D(mm) arcsec: 0.116" vs the RC20's 0.227") that's nearly DOUBLE, not a marginal
        /// gain. At the same 4x Barlow, native (unbinned) plate scale is 0.0398"/px vs the RC20's
        /// 0.0688"/px -- landing almost exactly at this telescope's own Dawes/3 critical-sampling
        /// point (0.0387"/px), so the extra magnification is fully backed by its finer diffraction
        /// limit, not empty magnification. That finer plate scale gives MinFovDeg ~0.0458 deg
        /// (~2.75') against the RC20's ~0.0792 deg (~4.75') -- a real, visible 42% narrower frame
        /// for tightly resolving small, faint, or distant bodies the RC20 can't usefully reach.
        ///
        /// Astigmatism: unlike the plain-RC RC20, PlaneWave's own CDK1000 page states the design
        /// is "free of off-axis coma, astigmatism, and field curvature" -- the CDK form adds a
        /// corrector near the focal plane specifically to cancel both third-order aberrations a
        /// bare Dall-Kirkham would otherwise have, not just coma the way an RC does. Taking the
        /// manufacturer's own flat-field claim at face value (no published CDK1000 datasheet
        /// gives a nonzero residual to the precision an aberration coefficient would need, so
        /// inventing one would be less defensible than the manufacturer's stated design goal),
        /// the corner astigmatism blur is 0px here.
        /// </summary>
        public static readonly VisualTelescopeSpec Cdk1000 = new VisualTelescopeSpec
        {
            Name = "CDK1000",

            ApertureMeters = 1.000,
            FocalLengthMeters = 6.000,
            BarlowFactor = 4.0,
            SecondaryObstructionFraction = 0.47,
            SiteAltitudeMeters = 1712.0,

            NativeSensorWidthPx = 4144,
            NativeSensorHeightPx = 2822,
            NativePixelSizeMeters = 4.63e-6,
            QuantumEfficiency = 0.90,
            FullWellElectrons = 66000.0,
            ReadNoiseElectrons = 1.2,
            DarkCurrentElectronsPerSecond = 0.0022, // ZWO ASI294MM Pro, cooled to -20C

            MinExposureSeconds = 0.000032f,
            MaxExposureSeconds = 2000.0f,
            MinGain = 0.7f,
            MaxGain = 8.0f,

            LuminanceBandwidthAngstrom = 2650.0,
            RedBandwidthAngstrom = 2650.0 / 3.0,
            GreenBandwidthAngstrom = 2650.0 / 3.0,
            BlueBandwidthAngstrom = 2650.0 / 3.0,
            HAlphaBandwidthAngstrom = 70.0,

            // Same real amateur LRGB filter set as the RC20 (same camera, same accessory class)
            // -- see Rc20's own comment for how these centres follow from the band's even thirds.
            LuminanceCentralWavelengthNm = 552.5,
            RedCentralWavelengthNm = 640.8,
            GreenCentralWavelengthNm = 552.5,
            BlueCentralWavelengthNm = 464.2,
            HAlphaCentralWavelengthNm = 656.3,

            AvailableFilters = AllFilters,
            AstigmatismStrengthPxAtCorner = 0.0f,
        };

        /// <summary>
        /// The VLT (Very Large Telescope), Unit Telescope 1 "Antu", Paranal Observatory --
        /// fitted with its real FORS2 (FOcal Reducer/low dispersion Spectrograph 2) imager.
        /// Every number below is FORS2's own real, published spec -- no ZWO/amateur hardware
        /// substituted in, per Baptiste's explicit call: this is meant to double as a real
        /// scientific reference, not a reskinned consumer camera.
        ///
        /// Optics: 8.2m Cassegrain aperture (ESO), M2 secondary 1.116m diameter (ESO M2 Unit
        /// page, eso.org/sci/facilities/paranal/telescopes/ut/m2unit.html) -> obstruction
        /// fraction 1.116/8.2 = 0.1361. FORS2's own collimator+camera relay reduces the VLT's
        /// natural f/15 Cassegrain beam to a real measured/published plate scale of 0.126"/pixel
        /// (unbinned) in its Standard-Resolution (SR) mode -- rather than simulate the multi-
        /// element relay, the equivalent single focal length that reproduces that REAL plate
        /// scale with the REAL 15um pixel is used: FL = pixelSize / (0.126"/206265) = 24.556m.
        /// FORS2 also has a real High-Resolution (HR) collimator, independently confirmed via its
        /// own published focal length (1233mm SR vs 616mm HR, ratio 2.001) -- used here as the
        /// real "Barlow" for the zoom range's tight end, in place of an invented amateur
        /// accessory. Site altitude 2635m, Paranal (same value already used for ESPRESSO in
        /// Observatories.cs -- one physical site, one number).
        ///
        /// Sensor: real mosaic of two MIT/Lincoln-Lab CCID20 CCDs (eso.org FORS2 User Manual;
        /// chip identity cross-confirmed via Wittman et al. 1998 SPIE 3355, 598 and the CFH12K
        /// technical notes, which used the same part), each 4096x2048px at 15x15um, stacked
        /// vertically with a real 32px/480um gap -> combined mosaic 4096x4128px. QE: real
        /// measured curve (eso.org/sci/php/optdet/instruments/fors2/Fors2old/qe.html) -- 400nm
        /// 58%, 500nm 74%, 600nm 86% (peak), 700nm 83%, 800nm 66%, 900nm 39%; 86% (peak) is used
        /// as this pipeline's single QE scalar, the same "headline/peak" convention the RC20/
        /// CDK1000 entries use for their ZWO datasheet's 90%. Full well: 150,000 e-, the CCID20
        /// chip's own real spec (Cuillandre et al., CFH12K/ESO CCD workshop 1999 technical note --
        /// FORS2's own manual doesn't restate a full-well number for the shared chip). Gain and
        /// read noise are FORS2's own directly-published values for its real "100kHz,2x2,high"
        /// readout mode: 0.7 e-/ADU, RON 2.7 ADU (Chip1) = 1.89 e-. Dark current: FORS2's own
        /// published 3 e-/pixel/hour at its real -120C operating temperature (0.000833 e-/s).
        /// As of this codebase's current date, the FORS-Up detector replacement project (arXiv
        /// 2012.09227, progress report arXiv:2407.02979) is still in ground testing and not
        /// expected on-sky before 2027 -- so this CCID20-based spec IS the currently operating
        /// real instrument, not an outdated one.
        ///
        /// Gain control: unlike the RC20/CDK1000's ZWO CMOS cameras, a real scientific CCD like
        /// FORS2 has no continuously-variable ISO-like gain -- its gain is fixed by the readout
        /// electronics at whichever mode is configured (0.7 e-/ADU above). MinGain == MaxGain
        /// here for that reason: it's a real, documented instrument limitation, not a shortcut.
        ///
        /// Exposure range: 0.25s minimum is FORS2's own published shortest full-frame imaging
        /// exposure. There is no real published maximum -- a professional CCD isn't electronically
        /// capped the way a consumer camera is, only practically limited by sky background/cosmic-
        /// ray accumulation. 3600s (1 hour) is used as a deliberate, coherent design choice
        /// matching standard real observatory practice of capping a single sub around that length
        /// and reaching longer total integration by stacking (this mod's own AstroImageStack
        /// already does exactly that) -- not a fabricated hardware spec.
        ///
        /// Filters: FORS2's own real broadband filter set, each with its own real bandwidth (ESO
        /// FORS2 Standard Filters page) -- b_HIGH (429nm/88nm FWHM) as Blue, v_HIGH (554nm/111nm
        /// FWHM) as Green, R_SPECIAL (655nm/165nm FWHM) as Red. HAlpha uses the real Halpha+83
        /// narrowband filter (656.3nm center, 61 Angstrom FWHM). Luminance represents a genuine
        /// unfiltered/clear exposure across the CCD's real full quoted sensitivity range
        /// (330-1100nm = 7700 Angstrom) -- FORS2 has no dedicated amateur-style "L" filter, so
        /// this is the real clear-aperture equivalent, not an invented one.
        ///
        /// Astigmatism: FORS2/the VLT Cassegrain focus is a real, well-corrected two-mirror
        /// system, but no published VLT optical prescription gives a field-dependent astigmatism
        /// coefficient to the precision this pipeline's display model would need (same honesty
        /// standard as the RC20's own 3.0px figure) -- rather than invent one for an instrument
        /// this well-documented everywhere else, astigmatism is left at 0px here.
        ///
        /// Tracking: a real 8.2m Unit Telescope always has precision active guiding -- there is
        /// no real "bare, unguided VLT" the way a hobbyist's RC20 might genuinely lack an
        /// autoguider. AlwaysAutoguided forces this in the pipeline, since without it the same
        /// diurnal-drift trailing the RC20/CDK1000 can show at high zoom (correctly, for those
        /// amateur instruments) would appear on VLT frames too, which isn't how the real
        /// instrument operates.
        /// </summary>
        public static readonly VisualTelescopeSpec Fors2Vlt = new VisualTelescopeSpec
        {
            Name = "VLT FORS2",

            ApertureMeters = 8.2,
            FocalLengthMeters = 24.556,
            BarlowFactor = 2.0,
            SecondaryObstructionFraction = 1.116 / 8.2,
            AlwaysAutoguided = true,
            SiteAltitudeMeters = 2635.0,

            NativeSensorWidthPx = 4096,
            NativeSensorHeightPx = 4128,
            NativePixelSizeMeters = 15e-6,
            QuantumEfficiency = 0.86,
            FullWellElectrons = 150000.0,
            ReadNoiseElectrons = 1.89,
            DarkCurrentElectronsPerSecond = 3.0 / 3600.0, // real FORS2 spec, -120C

            MinExposureSeconds = 0.25f,
            MaxExposureSeconds = 3600.0f,
            MinGain = 1.0f,
            MaxGain = 1.0f,

            LuminanceBandwidthAngstrom = 7700.0,
            RedBandwidthAngstrom = 1650.0,
            GreenBandwidthAngstrom = 1110.0,
            BlueBandwidthAngstrom = 880.0,
            HAlphaBandwidthAngstrom = 61.0,

            // Real FORS2 broadband set, the same filters the bandwidths above come from: Bessell
            // B (429nm/88nm), V (554nm/111nm), R (655nm/165nm) and the narrowband H-alpha
            // (656.3nm/6.1nm). Luminance is FORS2's own full sensitivity range (~330-1100nm,
            // i.e. the 7700 Angstrom width above), whose centre is 715nm.
            LuminanceCentralWavelengthNm = 715.0,
            RedCentralWavelengthNm = 655.0,
            GreenCentralWavelengthNm = 554.0,
            BlueCentralWavelengthNm = 429.0,
            HAlphaCentralWavelengthNm = 656.3,

            AvailableFilters = AllFilters,
            AstigmatismStrengthPxAtCorner = 0.0f,
        };

        /// <summary>
        /// The VLT, Unit Telescope 3 "Melipal", Paranal -- fitted with its real SPHERE/ZIMPOL
        /// extreme-adaptive-optics imaging polarimeter. Same 8.2m aperture and Paranal site
        /// (2635m) as FORS2/UT1, but a different, dedicated UT (Schmid et al. 2018, A&amp;A 619,
        /// A9, "SPHERE/ZIMPOL high resolution polarimetric imager. I."). Every number below is
        /// that paper's own published spec (its Table 4 gives the detector figures directly) --
        /// nothing here is estimated or invented.
        ///
        /// The whole point of this instrument: FORS2 is SEEING-limited (atmospheric turbulence
        /// blurs it to Paranal's real ~0.6-1" typical seeing, no matter the 8.2m mirror behind
        /// it), while SPHERE's real-time adaptive optics (SAXO) actively corrects that
        /// turbulence, so ZIMPOL gets much closer to the telescope's own true diffraction limit
        /// instead. See AdaptiveOpticsFwhmArcsec below.
        ///
        /// Optics: real f/221 system feeding ZIMPOL, giving a real published plate scale of
        /// 3.6 mas/pixel at the detector's standard 2x2-on-chip-binned mode -- the equivalent
        /// focal length that reproduces this with the real 15um native (unbinned) pixel is used
        /// (FL = 30um / (3.6mas/206265) = 1718.7m), so this pipeline's own BinningFactor=1 gives
        /// ZIMPOL's real unbinned 1.8 mas/pixel mode and BinningFactor=2 reproduces its real
        /// documented "standard imaging" 3.6 mas/pixel mode exactly -- no separate Barlow exists
        /// for this instrument (BarlowFactor=1). Cross-check: at native pixel count (2048px),
        /// this gives a computed FOV of ~3.49", matching ZIMPOL's own real published 3.6"x3.6"
        /// field to within rounding of the two independently-quoted source numbers. Obstruction
        /// reuses the VLT UT's own real M2/M1 ratio (see Fors2Vlt) -- the same shared telescope
        /// hardware, not a SPHERE-internal figure (none published to the precision needed).
        ///
        /// Sensor: real ZIMPOL CCD, 15um native pixels, back-illuminated frame-transfer, 2k x 2k
        /// raw format. QE 95% (peak, at 600nm; the paper also gives 90% at 700nm and 65% at
        /// 800nm). Imaging-mode figures straight from the paper's Table 4: full well 640,000 e-
        /// /pixel, read noise 20 e-/pixel, dark current 0.2 e-/s/pixel, minimum integration time
        /// 1.1s. No published maximum -- same 3600s (1 hour) coherent design choice as Fors2Vlt,
        /// for the same reasoning (real observatory practice, not a fabricated hardware limit).
        /// Gain is FORS2-style fixed (10.5 e-/ADU is the real hardware conversion factor, not a
        /// player-adjustable ISO), so MinGain == MaxGain == 1.0 here too.
        ///
        /// Adaptive optics: SAXO achieves a real, published resolution of about 25 mas FWHM in
        /// good conditions (Strehl ~40% in I-band) per the ZIMPOL system paper itself; a second,
        /// independent paper (Milli et al., search results for ZIMPOL H-alpha imaging) states
        /// SPHERE/ZIMPOL "routinely" reaches 22-28 mas FWHM across V/R/I -- 25 mas sits at the
        /// middle of that independently-confirmed range. Used as AdaptiveOpticsFwhmArcsec, this
        /// REPLACES the plain ground-based seeing model (see ComputeSeeingBlurPx) with this
        /// real, roughly airmass-independent achieved resolution -- about 24-40x finer than
        /// FORS2's typical seeing-limited blur, which is the entire reason this instrument can
        /// resolve targets FORS2 can only show as a barely-resolved smudge.
        ///
        /// Filters: real ZIMPOL broadband filters, each with its own real published bandwidth
        /// (search results citing the paper's filter table) -- V (554nm/80.6nm FWHM) as Green,
        /// N_R (646nm/57nm FWHM) as Red, B_Ha (655.6nm/5.5nm FWHM; the broader of ZIMPOL's two
        /// real Halpha filters, N_Ha at 0.97nm FWHM being too narrow for a simple broadband-style
        /// single exposure) as HAlpha. Luminance uses ZIMPOL's own quoted working spectral
        /// regime, 500-900nm (4000 Angstrom), as the real clear/broadband-equivalent range --
        /// same "genuine full-sensitivity range, not an amateur L filter" approach as Fors2Vlt.
        /// ZIMPOL genuinely has NO real blue broadband filter (its filter set targets red/near-IR
        /// reflected-light and circumstellar-disk science, not true-color RGB) -- rather than
        /// invent one, AvailableFilters simply omits Blue, and the GUI's filter wheel doesn't
        /// offer it for this instrument. BlueBandwidthAngstrom is left at 0 and is unreachable.
        ///
        /// Astigmatism: ZIMPOL's real field of view is only 3.6"x3.6" -- far too narrow for
        /// off-axis Seidel astigmatism to grow to any meaningful amplitude regardless of the
        /// telescope's prescription, so 0px here is well-justified by the field size alone, not
        /// just the usual "no published coefficient" reasoning.
        ///
        /// Tracking: an extreme-AO system inherently requires continuous, high-precision guiding
        /// on a reference star to work at all -- if anything a harder requirement than FORS2's,
        /// so this is AlwaysAutoguided too.
        /// </summary>
        public static readonly VisualTelescopeSpec Sphere = new VisualTelescopeSpec
        {
            Name = "VLT SPHERE",

            ApertureMeters = 8.2,
            FocalLengthMeters = 1718.7,
            BarlowFactor = 1.0,
            SecondaryObstructionFraction = 1.116 / 8.2,
            SiteAltitudeMeters = 2635.0,
            AlwaysAutoguided = true,

            NativeSensorWidthPx = 2048,
            NativeSensorHeightPx = 2048,
            NativePixelSizeMeters = 15e-6,
            QuantumEfficiency = 0.95,
            FullWellElectrons = 640000.0,
            ReadNoiseElectrons = 20.0,
            DarkCurrentElectronsPerSecond = 0.2, // real ZIMPOL imaging-mode spec (Table 4)

            MinExposureSeconds = 1.1f,
            MaxExposureSeconds = 3600.0f,
            MinGain = 1.0f,
            MaxGain = 1.0f,

            LuminanceBandwidthAngstrom = 4000.0,
            RedBandwidthAngstrom = 570.0,
            GreenBandwidthAngstrom = 806.0,
            BlueBandwidthAngstrom = 0.0, // no real ZIMPOL blue filter -- see AvailableFilters
            HAlphaBandwidthAngstrom = 55.0,

            // Real ZIMPOL filter centres, the same ones the bandwidths above come from (Schmid
            // et al. 2018): V 554nm as Green, N_R 646nm as Red, B_Ha 655.6nm as HAlpha.
            // Luminance is ZIMPOL's own quoted 500-900nm working regime, centre 700nm. Blue
            // stays 0 -- there is no real ZIMPOL broadband blue filter and the position is
            // unreachable (see AvailableFilters immediately below).
            LuminanceCentralWavelengthNm = 700.0,
            RedCentralWavelengthNm = 646.0,
            GreenCentralWavelengthNm = 554.0,
            BlueCentralWavelengthNm = 0.0,
            HAlphaCentralWavelengthNm = 655.6,

            AvailableFilters = new[] { CameraFilter.Luminance, CameraFilter.Red, CameraFilter.Green, CameraFilter.HAlpha },

            AdaptiveOpticsFwhmArcsec = 0.025,
            // Strehl ~40% in I band, the ZIMPOL system paper's own quoted performance alongside
            // the 25 mas figure above. The halo is Paranal's real median seeing (0.65", ESO's
            // published site figure -- the same site FORS2 above observes from), since the halo
            // is by definition the fraction SAXO did not correct.
            AdaptiveOpticsStrehlRatio = 0.40,
            AdaptiveOpticsHaloSeeingFwhmArcsec = 0.65,
            AstigmatismStrengthPxAtCorner = 0.0f,
        };

        /// <summary>Every visual telescope available to the in-game instrument selector (the Observatory dropdown in ExoInstrumentsGUI -- see InstrumentSpec.VisualTelescope), in unlock/display order.</summary>
        public static readonly VisualTelescopeSpec[] All = { Rc20, Cdk1000, Fors2Vlt, Sphere };
    }
}
