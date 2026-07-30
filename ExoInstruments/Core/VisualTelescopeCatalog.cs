using ExoInstruments.Visualization;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Physical identity of one visual (solar-system photography) telescope+camera setup:
    /// optics, sensor, and capture range. This is everything SolarSystemCameraTexture's
    /// rendering pipeline needs to know about which instrument it's simulating, kept out of
    /// the pipeline itself so a new visual telescope (e.g. a cheap beginner scope for the Mun,
    /// or a larger instrument that can reach the small/distant planets the RC20 can't) is a new
    /// entry in VisualTelescopeCatalog below, not a change to the rendering code.
    /// </summary>
    /// <summary>
    /// One narrowband filter on an instrument's wheel: the line it sits on, its FWHM and its peak
    /// transmission. See VisualTelescopeSpec.NarrowbandFilters for why this is a table.
    /// </summary>
    public struct NarrowbandFilterSpec
    {
        public CameraFilter Position;
        public double CentralWavelengthNm;
        public double BandwidthAngstrom;
        /// <summary>1.0 means NOT PUBLISHED for this filter, not a perfect one, the same convention the broadband fields use.</summary>
        public double PeakTransmission;
    }

    public sealed class VisualTelescopeSpec
    {
        public string Name;

        /// <summary>
        /// The detector, named separately from the optics, because on this roster they genuinely
        /// come apart: one ZWO camera is shared between three different tubes, exactly as amateur
        /// astrophotography works. Goes to the FITS INSTRUME keyword while Name goes to TELESCOP,
        /// which is the distinction those two keywords are for.
        /// </summary>
        public string CameraName;

        /// <summary>The observatory this instrument stands at, for the FITS OBSERVAT keyword. The same site the altitude and seeing figures below are measured at, named rather than implied.</summary>
        public string SiteName;

        /// <summary>Detector operating temperature in Celsius, the one the dark current below was measured at. NaN when the instrument's is not modelled.</summary>
        public double DetectorTemperatureCelsius = double.NaN;

        /// <summary>
        /// Bias pedestal (offset) in ADU: the constant the readout electronics add ahead of the
        /// converter so that a pixel carrying no charge still digitises to a positive count.
        ///
        /// WHY IT MATTERS, WHICH IS NOT WHAT IT LOOKS LIKE. Read noise is symmetric about zero. A
        /// converter cannot represent a negative count. Without a pedestal, the negative half of
        /// the read-noise distribution is clipped away, which for a pixel holding no charge biases
        /// the result upward by sigma/sqrt(2*pi) = 0.40 sigma (the ADC's own downward truncation
        /// then gives back half a count, leaving about +0.21 sigma net at 1.2 e- read noise), makes
        /// the noise non-Gaussian exactly at the floor where faint detail lives, and, the real
        /// cost, leaves the read noise unmeasurable from the exported data and dark subtraction
        /// wrong. Every real camera has a pedestal for precisely this reason.
        ///
        /// The bias is SIGNAL-DEPENDENT, which is what makes it pernicious rather than merely
        /// present: it only bites where a pixel's total charge sits within a read noise of zero. In
        /// a long exposure on a bright sky that is no pixel at all; in a short one it is many; and
        /// in a bias or dark frame it is EVERY pixel. So the pedestal is not a cosmetic fix, it is
        /// a precondition for the calibration frames being worth taking.
        ///
        /// ITS VALUE IS ARBITRARY BY CONSTRUCTION, and that is why leaving this NaN is not a gap
        /// in the way an unsourced read noise would be. The pedestal is a fixed additive constant
        /// that calibration subtracts, so it cancels out of every measurement made from the frame;
        /// what matters physically is only that it is large enough not to clip. NaN therefore
        /// takes DefaultBiasLevelAdu below rather than disabling the pedestal.
        /// </summary>
        public double BiasLevelAdu = double.NaN;

        /// <summary>
        /// The pedestal to use when none is quoted for the device: five times the read noise,
        /// rounded up to a whole count.
        ///
        /// Five sigma is a design rule, not a measurement, and is labelled as one; it is the
        /// margin at which clipping stops mattering (the probability of a read-noise excursion
        /// below the pedestal is 2.9e-7 per pixel, so a 17-megapixel frame clips about five pixels
        /// rather than eight million). Since the pedestal cancels in calibration, no measurable
        /// quantity depends on the choice; only the raw ADU values do.
        /// </summary>
        public static double DefaultBiasLevelAdu(double readNoiseElectrons, double electronsPerAdu)
        {
            if (!(readNoiseElectrons > 0.0) || !(electronsPerAdu > 0.0)) return 0.0;
            return System.Math.Ceiling(5.0 * readNoiseElectrons / electronsPerAdu);
        }

        /// <summary>This instrument's pedestal in ADU at the given conversion gain: its own published figure, or the default rule above.</summary>
        public double EffectiveBiasLevelAdu(double electronsPerAdu)
        {
            if (!double.IsNaN(BiasLevelAdu) && BiasLevelAdu >= 0.0) return BiasLevelAdu;
            return DefaultBiasLevelAdu(ReadNoiseElectrons, electronsPerAdu);
        }

        /// <summary>
        /// How far below ambient this camera's thermoelectric cooler can hold the sensor, in
        /// degrees. Zero means the setpoint is NOT adjustable, which for this roster is the honest
        /// description of the professional instruments: FORS2 and SPHERE run their detectors at a
        /// fixed cryogenic temperature, and no observer at a VLT unit telescope is offered a dial.
        ///
        /// A TEC figure is published as a DELTA rather than an absolute temperature because that is
        /// what the device can actually do; it pumps heat, so where it lands depends on where it
        /// starts. That is also the caveat on it: ZWO measure their delta at 30 C ambient and state
        /// that it falls as ambient does, so at a cold mountain site the true reachable minimum is
        /// warmer than SiteAmbientTemperatureCelsius minus this. The cold end of the range below is
        /// therefore optimistic, by an amount no manufacturer publishes.
        /// </summary>
        public double CoolerDeltaBelowAmbientC;

        /// <summary>
        /// Ambient air temperature at the site, Celsius: the temperature the cooler works against.
        ///
        /// An annual mean from published climate records for the site's own location, NOT a
        /// night-time or observatory-logged figure: a real observer cools against the air at 3 a.m.
        /// in whatever season it is, and neither KSP nor this mod has a weather model to derive that
        /// from. So it is a single representative number, stated as one, and it moves the reachable
        /// floor rather than any measurement made through the instrument.
        /// </summary>
        public double SiteAmbientTemperatureCelsius = double.NaN;

        /// <summary>True when this instrument's detector temperature is a control the observer actually has.</summary>
        public bool HasAdjustableCooler =>
            CoolerDeltaBelowAmbientC > 0.0 && !double.IsNaN(SiteAmbientTemperatureCelsius);

        /// <summary>Coldest setpoint the cooler can hold at this site.</summary>
        public double CoolerMinimumTemperatureCelsius =>
            SiteAmbientTemperatureCelsius - CoolerDeltaBelowAmbientC;

        /// <summary>Warmest setpoint worth offering: ambient, since a cooler cannot heat the sensor above the air around it.</summary>
        public double CoolerMaximumTemperatureCelsius => SiteAmbientTemperatureCelsius;

        // Optics
        public double ApertureMeters;
        public double FocalLengthMeters;
        public double BarlowFactor;
        public double SecondaryObstructionFraction;

        /// <summary>
        /// Secondary-support spider: number of vanes, and their width in metres. Together these
        /// give the pupil its diffraction spikes, computed by Core/PupilDiffraction rather than
        /// drawn (see TECHNICAL_REFERENCE 7.112).
        ///
        /// Zero means "no spider modelled", and for each instrument that is either a physical fact
        /// or a declared gap, never a convenience:
        ///   * A refractor has no secondary and therefore no spider at all.
        ///   * For the PlaneWave instruments the vanes exist but no manufacturer figure gives
        ///     their width, and spike brightness scales as the vane area SQUARED, so guessing it
        ///     would be guessing the effect itself. Left at zero and recorded in section 12,
        ///     the same treatment the CDK1000's astigmatism already gets.
        /// </summary>
        public int SpiderVaneCount;
        public double SpiderVaneWidthMeters;

        /// <summary>
        /// Measured transmission curves for this instrument's Red/Green/Blue filter positions,
        /// when the observatory publishes them. Null means the filter is carried as a top-hat of
        /// its published FWHM and peak instead, which is the honest treatment when nothing else
        /// exists (see FilterCurves and SystemBandpass).
        ///
        /// When a curve IS supplied it carries the filter's own transmission, so the matching
        /// FilterPeakTransmission must not be applied on top of it.
        /// </summary>
        public SpectralCurve RedFilterCurve;
        public SpectralCurve GreenFilterCurve;
        public SpectralCurve BlueFilterCurve;

        // Site (feeds the shared atmospheric/scintillation model in AtmosphericImagingNoise)
        public double SiteAltitudeMeters;

        /// <summary>
        /// The site's own median seeing FWHM (arcsec) AT ZENITH, referred to 500nm, the
        /// convention every published DIMM figure uses. This is the dominant term in the
        /// delivered image quality of any ground-based telescope without adaptive optics: a
        /// 0.5m and an 8m telescope at the same site resolve a planet equally well, because
        /// both are limited by this and not by their own apertures.
        ///
        /// Must be nonzero for every non-AO instrument. Zero means "no atmosphere", which is
        /// only true for a space telescope; on the ground it produces a perfectly sharp,
        /// diffraction-limited disk that no real telescope has ever recorded.
        /// </summary>
        public double ZenithSeeingFwhmArcsec;

        // --- Optical throughput -------------------------------------------------------------
        // How much of the light entering the aperture actually reaches the detector. This used to
        // be absent entirely: the photometry collected every photon the aperture intercepted, so
        // every instrument in the roster reached about 1.5 magnitudes deeper than a real one of
        // the same size does. It is split into the factors that are separately published, rather
        // than one lumped efficiency, so each can be sourced or declared unmodelled on its own.

        /// <summary>
        /// Number of reflecting surfaces in the light path. This is a property of where the
        /// instrument sits on the telescope, not of the telescope alone: FORS2 is at UT1's
        /// CASSEGRAIN focus and sees M1+M2 only, while SPHERE is on UT3's NASMYTH platform and
        /// picks up the M3 flat as well; so the same 8.2m telescope delivers measurably
        /// different throughput to the two instruments. Zero for a refractor, which has no
        /// mirrors at all.
        /// </summary>
        public int MirrorCount;

        /// <summary>
        /// Reflectivity of one mirror surface, band-averaged over the optical range.
        ///
        /// The throughput of a mirror train is r^N times the obstruction factor (1 - eps^2),
        /// Ma &amp; Cai, "Scientific performance analysis of the SYZ telescope design vs. the RC
        /// telescope design" (MNRAS; arXiv:1708.01257), Sect. 4.2, whose Eq. 3 is exactly the form
        /// used here and whose obstruction term this pipeline already applied. That paper also
        /// supplies the value: aluminium is "about 90%" in the 300-1000nm range when fresh and
        /// "will degrade from 90% to about 87% after 1 year and to 84% after two years (Magrath
        /// 1997)", from which the authors "take the reflectivity of aluminum coating for the full
        /// optical wavelength range as 87% during a 2-year lifetime".
        ///
        /// 0.87 is used throughout for that reason: it is an operating figure over a realistic
        /// re-coating cycle, not a laboratory best case for a mirror on the day it was coated.
        /// Independently consistent with ESO's own measurement of the VLT coating, which
        /// Ettlinger, Giordano &amp; Schneermann (1999, The Messenger 97, 4-8, "Performance of the
        /// VLT Mirror Coating Unit") place between Bennett et al.'s (1963, JOSA 53, 1089) fresh
        /// and aged evaporated-aluminium samples across 300-2500nm.
        ///
        /// Deliberately grey: the source quotes it as a band average over the full optical range,
        /// so resolving it in wavelength would mean inventing a curve the citation does not give.
        /// PlaneWave's optional enhanced coatings are likewise not modelled, since no measured
        /// curve is published for them.
        /// </summary>
        public double MirrorReflectivity;

        /// <summary>
        /// Everything else in the light path, as one factor: relay optics, correctors, beam
        /// splitters, Barlow glass, dewar windows.
        ///
        /// 1.0 means NOT MODELLED, not lossless, and it is the honest value wherever no figure is
        /// published; see each entry's own comment for which case it is in. The one instrument
        /// with a real number here is SPHERE, whose grey beam splitter's 79% transmission to
        /// ZIMPOL is published (Schmid et al. 2018).
        /// </summary>
        public double RelayOpticsTransmission;

        /// <summary>Reflection and relay losses combined: r^N times the relay factor. The aperture obstruction is NOT here; it is already in EffectiveApertureAreaM2, which is where the collecting area belongs.</summary>
        public double OpticsTransmission
        {
            get
            {
                double reflection = MirrorCount > 0 && MirrorReflectivity > 0.0
                    ? System.Math.Pow(MirrorReflectivity, MirrorCount)
                    : 1.0;
                double relay = RelayOpticsTransmission > 0.0 ? RelayOpticsTransmission : 1.0;
                return reflection * relay;
            }
        }

        // Sensor
        public int NativeSensorWidthPx;
        public int NativeSensorHeightPx;
        public double NativePixelSizeMeters;

        /// <summary>
        /// The detector's PEAK quantum efficiency. Used across the whole band only when
        /// QuantumEfficiencyCurve is null, i.e. when the manufacturer publishes nothing else.
        /// </summary>
        public double QuantumEfficiency;

        /// <summary>
        /// The detector's published QE curve, when one exists. Null means only a peak figure is
        /// published and the peak is used flat across the band, which overstates every filter
        /// away from the peak, and is recorded as such rather than papered over with a borrowed
        /// curve from a different sensor.
        /// </summary>
        public SpectralCurve QuantumEfficiencyCurve;
        public double FullWellElectrons;
        public double ReadNoiseElectrons;
        /// <summary>Dark current at this sensor's own real cooled operating temperature (see each entry's comment for the actual temperature; it varies by instrument, so it doesn't belong in the field name).</summary>
        public double DarkCurrentElectronsPerSecond;

        /// <summary>
        /// Bit depth of this camera's real analogue-to-digital converter. With
        /// ElectronsPerAduAtUnityGain this fixes the digital saturation level in electrons,
        /// K*(2^bits - 1), which is a DIFFERENT limit from the full well and is often the one
        /// that actually bites: ESO's FORS2 manual states outright that "none of the CCDs will
        /// saturate before reaching the numerical truncation limits (65535 adu)". A pipeline
        /// working in fractions of full well cannot express that at all.
        /// </summary>
        public int AdcBits;

        /// <summary>
        /// Real conversion factor K in electrons per ADU, at gain multiplier 1. This is the
        /// number that turns a simulated charge into a digital count, and the same number a real
        /// observer needs to turn the counts back into electrons; it is written to the FITS
        /// EGAIN keyword, which is what makes the exported frame genuinely calibratable rather
        /// than a picture of one.
        /// </summary>
        public double ElectronsPerAduAtUnityGain;

        /// <summary>
        /// Measured cosmic-ray event rate on this detector, events per minute per cm^2. Site
        /// altitude matters here (the flux climbs steeply above sea level), so this is per
        /// instrument rather than one pipeline-wide constant.
        /// </summary>
        public double CosmicRayEventsPerMinutePerCm2;

        // Capture range
        public float MinExposureSeconds;
        public float MaxExposureSeconds;
        /// <summary>Continuously-variable electronic gain range. Set MinGain == MaxGain for a real instrument whose gain is fixed by its readout electronics rather than player-adjustable (e.g. a professional CCD with no ISO-like control); see VisualTelescopeCatalog.Fors2Vlt.</summary>
        public float MinGain;
        public float MaxGain;

        // Filters: real bandwidth (FWHM, Angstrom) per filter-wheel position. Luminance is the
        // wide/clear reference; R/G/B and HAlpha are each their own real filter on instruments
        // that have one (not assumed fractions of Luminance); see each entry's comment.
        public double LuminanceBandwidthAngstrom;
        public double RedBandwidthAngstrom;
        public double GreenBandwidthAngstrom;
        public double BlueBandwidthAngstrom;
        public double HAlphaBandwidthAngstrom;

        // Real CENTRAL wavelength (nm) per filter-wheel position. Separate from the bandwidths
        // above because diffraction cares about where the passband sits, not how wide it is:
        // the whole PSF scales as lambda/D (see OpticalPsf), so the same telescope genuinely
        // resolves finer through a blue filter than a red one, a real, measurable effect that
        // a single instrument-wide wavelength would erase. Each entry's own comment sources its
        // filter set; a position the instrument doesn't physically have is left at 0 and is
        // unreachable (see AvailableFilters).
        public double LuminanceCentralWavelengthNm;
        public double RedCentralWavelengthNm;
        public double GreenCentralWavelengthNm;
        public double BlueCentralWavelengthNm;
        public double HAlphaCentralWavelengthNm;

        // Peak transmission at each filter position. A filter's published FWHM says how WIDE its
        // passband is, not how much light it lets through at the top of it, and a real interference
        // filter is well short of 1: ESO publishes 0.70 for FORS2's own H_Alpha+83 in its standard
        // collimator. 1.0 here means the figure is NOT PUBLISHED for that filter and the loss is
        // therefore unmodelled; it is not a claim of a perfect filter. Combined with the top-hat
        // of the published FWHM, peak transmission fixes the filter's equivalent width, which is
        // the quantity the photometry integral actually needs (see SystemBandpass).
        public double LuminanceFilterPeakTransmission;
        public double RedFilterPeakTransmission;
        public double GreenFilterPeakTransmission;
        public double BlueFilterPeakTransmission;
        public double HAlphaFilterPeakTransmission;

        /// <summary>
        /// Which CameraFilter positions actually exist as a real filter on this instrument;
        /// the GUI's filter wheel only offers these. Most instruments carry all five; an
        /// instrument with a real gap (e.g. ZIMPOL has no broadband blue filter; its filter
        /// set targets red/near-IR reflected-light and circumstellar science, not true-color
        /// RGB) simply omits that entry rather than a made-up bandwidth standing in for a
        /// filter that doesn't exist.
        /// </summary>
        public CameraFilter[] AvailableFilters;

        /// <summary>
        /// The instrument's narrowband filters, one entry per position it physically carries.
        ///
        /// A table rather than four more flat fields per line: a narrowband filter is fully
        /// described by which line it sits on, how wide it is and how much it passes at the top,
        /// and adding one to an instrument should be a row rather than a spec-wide change. A
        /// position absent from this table is absent from AvailableFilters too, and is a filter
        /// the instrument does not have rather than one modelled with invented numbers.
        /// </summary>
        public NarrowbandFilterSpec[] NarrowbandFilters;

        /// <summary>This instrument's entry for a narrowband position, or null when it carries none.</summary>
        public NarrowbandFilterSpec? Narrowband(CameraFilter position)
        {
            if (NarrowbandFilters == null) return null;
            for (int i = 0; i < NarrowbandFilters.Length; i++)
                if (NarrowbandFilters[i].Position == position) return NarrowbandFilters[i];
            return null;
        }

        /// <summary>
        /// Real AO-corrected resolution (FWHM, arcsec) this instrument achieves under good
        /// conditions, for an instrument with genuine adaptive optics; see
        /// SolarSystemCameraTexture.ComputeGroundSeeingFwhmArcsec, which uses this INSTEAD OF the plain
        /// airmass-based seeing model when it's nonzero. 0 (default) means no adaptive optics:
        /// the plain ground-based seeing model applies, same as every telescope before SPHERE.
        /// </summary>
        public double AdaptiveOpticsFwhmArcsec;

        /// <summary>
        /// Strehl ratio this AO system really achieves, the fraction of the light it actually
        /// concentrates into the diffraction-limited core. Only meaningful alongside
        /// AdaptiveOpticsFwhmArcsec.
        ///
        /// This is what makes a real AO point-spread function two-component rather than one
        /// broadened blob: a corrected core carrying this fraction, plus a wide halo carrying
        /// the rest (see AdaptiveOpticsHaloSeeingFwhmArcsec, and OpticalPsf.BuildAdaptiveOptics
        /// Kernel). Collapsing the two into a single profile of the right total FWHM gets the
        /// width right but puts far too much light at intermediate scales, which is exactly
        /// where a resolved planetary disk's surface detail lives; it smears features that a
        /// real AO frame keeps sharp on top of a diffuse background.
        /// </summary>
        public double AdaptiveOpticsStrehlRatio;

        /// <summary>Seeing FWHM (arcsec) of the uncorrected halo the AO leaves behind, the site's own real median seeing, since the halo is simply the light the correction failed to gather.</summary>
        public double AdaptiveOpticsHaloSeeingFwhmArcsec;

        /// <summary>
        /// True for an instrument that always has precision active tracking, with no real bare/
        /// unguided operating mode; a professional research telescope like the VLT is never
        /// pointed without one, unlike an amateur astrograph a player might genuinely run
        /// without an autoguider attached. When true, SolarSystemCameraTexture forces its
        /// Autoguiding property on and locks the GUI toggle, instead of leaving drift/trailing
        /// as a player choice.
        /// </summary>
        public bool AlwaysAutoguided;

        // Off-axis aberration: peak astigmatism blur (pixels) at the sensor's corner. The
        // radial-quadratic FALLOFF this drives is the same optical physics for any two-mirror
        // astrograph (Seidel aberration theory; see SolarSystemCameraTexture's own comment on
        // ApplyAstigmatismBlur), but the PEAK amplitude depends on how completely THIS
        // telescope's own design cancels off-axis aberrations, so it lives per-instrument here
        // rather than as one pipeline-wide constant.
        public float AstigmatismStrengthPxAtCorner;

        /// <summary>
        /// True when the instrument carries an atmospheric dispersion corrector: a pair of
        /// counter-rotating prisms that cancels the atmosphere's own dispersion before it reaches the
        /// detector.
        ///
        /// This is not a detail for a high-resolution instrument. At 45 degrees from the zenith the
        /// atmosphere spreads 400 to 700 nm over 1.1 arcsec at Paranal, which on ZIMPOL's 3.6 mas
        /// pixels is THREE HUNDRED pixels of smear; an instrument delivering a 25 mas core cannot
        /// exist without one, and SPHERE has one (Beuzit et al. 2019, A&amp;A 631, A155, sect. 3.2:
        /// the common path carries an ADC for the visible arm). FORS2 does not: it is a
        /// low-resolution imager and spectrograph on 0.126 arcsec pixels, where the residual matters
        /// far less, and ESO's own manual discusses the resulting dispersion rather than correcting
        /// it. Amateur instruments do not.
        ///
        /// A real corrector leaves a residual rather than nothing, so this scales the dispersion
        /// rather than switching it off; see AtmosphericDispersionResidual.
        /// </summary>
        public bool HasAtmosphericDispersionCorrector;

        /// <summary>
        /// Fraction of the atmospheric dispersion a corrector leaves behind.
        ///
        /// A counter-rotating prism pair cancels the dispersion of a model atmosphere at a design
        /// zenith distance; what it cannot cancel is the difference between that model and the night's
        /// real air, and its own glasses' departure from the exact inverse dispersion curve. Published
        /// residuals for visible-arm ADCs of this class are at the few-percent level, so 0.05 is the
        /// figure used and it is labelled as an order rather than a measurement for a specific
        /// instrument; the alternative, cancelling the dispersion exactly, is the one value that is
        /// certainly wrong.
        /// </summary>
        public const double AtmosphericDispersionResidual = 0.05;
    }

    /// <summary>
    /// Catalog of visual telescopes selectable in-game. Each one that should appear in the
    /// Observatory dropdown needs a matching InstrumentSpec in Observatories.cs (Method =
    /// SolarSystemPhotography, VisualTelescope = the entry below); picking that row calls
    /// SolarSystemCameraTexture.SetActiveTelescope. Add another VisualTelescopeSpec here (e.g.
    /// a beginner Mun-class refractor), add it to All, and give it an Observatories.cs entry to
    /// ship a third instrument; the rendering pipeline needs no further changes.
    /// </summary>
    public static class VisualTelescopeCatalog
    {
        /// <summary>
        /// Every broadband position plus H-alpha. FORS2 stays on this set: ESO publishes a real
        /// narrowband list for it, and until those central wavelengths, widths and transmissions
        /// are read off the instrument manual it carries no narrowband position rather than one
        /// with numbers borrowed from an amateur filter.
        /// </summary>
        private static readonly CameraFilter[] AllFilters =
            { CameraFilter.Luminance, CameraFilter.Red, CameraFilter.Green, CameraFilter.Blue, CameraFilter.HAlpha };

        /// <summary>
        /// The amateur LRGB wheel plus the SHO narrowband set: H-alpha, [O III] and [S II], the
        /// three positions an amateur narrowband wheel is actually sold with. [N II], [O II] and
        /// [O I] are deliberately absent; [N II] at a width that separates it from H-alpha is a
        /// specialist item, [O II] at 372 nm is below where a CMOS sensor has usable quantum
        /// efficiency, and neither is a filter these telescopes would have.
        /// </summary>
        private static readonly CameraFilter[] AmateurNarrowbandFilters =
        {
            CameraFilter.Luminance, CameraFilter.Red, CameraFilter.Green, CameraFilter.Blue,
            CameraFilter.HAlpha, CameraFilter.OIII, CameraFilter.SII,
        };

        /// <summary>
        /// The amateur narrowband set, at the same 7 nm the H-alpha position already carries.
        /// Peak transmission is left at 1.0, which by this file's convention means the figure is
        /// NOT PUBLISHED for these and the loss is unmodelled rather than a claim of a perfect
        /// filter; the same treatment the H-alpha position already gets.
        /// </summary>
        private static readonly NarrowbandFilterSpec[] AmateurNarrowbandSet =
        {
            new NarrowbandFilterSpec
            {
                Position = CameraFilter.OIII,
                CentralWavelengthNm = 500.7,   // [O III] 5007
                BandwidthAngstrom = 70.0,
                PeakTransmission = 1.0,
            },
            new NarrowbandFilterSpec
            {
                Position = CameraFilter.SII,
                CentralWavelengthNm = 671.6,   // [S II] 6716, the brighter of the doublet
                BandwidthAngstrom = 70.0,
                PeakTransmission = 1.0,
            },
        };

        /// <summary>
        /// PlaneWave RC20: 20-inch (0.51m) Ritchey-Chretien astrograph at f/6.8, 3.468m focal
        /// length, 39% secondary obstruction (planewave.eu product page), paired with a real 4x
        /// Barlow for the "high power" end of the zoom range. Camera is a ZWO ASI294MM Pro mono
        /// CCD (zwoastro.com/product/asi294): 4144x2822 native resolution, 4.63um pixel pitch,
        /// 90% peak QE, 66,000 e- full well, 1.2 e- read noise (best case), 0.0022 e-/s/pixel
        /// dark current at -20C, 32us-2000s exposure range. (See Observatories.Rc20 for the
        /// career-economy side of this same instrument.)
        ///
        /// Site is the Observatoire de Haute-Provence, 650m. Previously this was left as a
        /// generic "university observatory, e.g. ETH Zurich", a site named only by example,
        /// which is fine for an altitude but not for seeing: seeing is a MEASURED property of a
        /// specific mountain, so an unnamed site simply has no value that can be cited. OHP is a
        /// real observatory of exactly this instrument's tier (a working national facility
        /// hosting small telescopes, not a flagship), and it has published seeing statistics.
        ///
        /// Filters: a real LRGB astro filter wheel has no single published per-channel bandwidth
        /// the way a research instrument's named filters do, so R/G/B keep the even-third-of-L
        /// split (modern "1:1:1 balanced" CMOS LRGB design; see FilterThroughput's own comment)
        /// and HAlpha keeps the real ~7nm narrowband figure.
        ///
        /// Astigmatism: for a true Ritchey-Chretien, third-order coma is corrected to zero by
        /// the RC hyperbolic-mirror design itself; that is the entire reason the RC form
        /// exists (Ritchey &amp; Chretien 1922). The dominant remaining off-axis third-order
        /// (Seidel) aberration for this telescope class is astigmatism. Its absolute amplitude
        /// depends on the telescope's actual optical prescription (focal ratio, field curvature
        /// radius), which no published PlaneWave RC20 datasheet specifies to the precision an
        /// aberration coefficient would need; 3.0px at the frame corner is a display
        /// calibration, not a measured quantity.
        /// </summary>
        public static readonly VisualTelescopeSpec Rc20 = new VisualTelescopeSpec
        {
            Name = "PlaneWave RC20",
            CameraName = "ZWO ASI294MM Pro",
            SiteName = "Observatoire de Haute-Provence",
            DetectorTemperatureCelsius = -20.0,
            // Two-stage TEC, "more than 35 degrees Celsius below ambient" (zwoastro.com ASI294 Pro
            // series), which ZWO measure at 30 C ambient and state falls as ambient does; so the
            // cold end this implies at a mountain site is optimistic, as CoolerDeltaBelowAmbientC
            // records. Ambient is the annual mean air temperature at Saint-Michel-l'Observatoire,
            // 11.8 C (climate-data.org), the commune OHP stands in.
            CoolerDeltaBelowAmbientC = 35.0,
            SiteAmbientTemperatureCelsius = 11.8,

            ApertureMeters = 0.51,
            FocalLengthMeters = 0.51 * 6.8,
            // The RC20 has a real vane spider, but PlaneWave publishes no vane width and spike
            // brightness goes as the vane area squared. Declared rather than guessed (section 12).
            SpiderVaneCount = 0,
            SpiderVaneWidthMeters = 0.0,
            BarlowFactor = 4.0,
            SecondaryObstructionFraction = 0.39,
            // A Ritchey-Chretien is two mirrors, and both are in the imaging path. Aluminium at
            // the mid-recoating-cycle figure (see MirrorReflectivity) gives 0.87^2 = 0.757.
            MirrorCount = 2,
            MirrorReflectivity = 0.87,
            // Not modelled: the real 4x Barlow's glass and the camera's own window both cost
            // light, and neither PlaneWave nor ZWO publishes a transmission for them.
            RelayOpticsTransmission = 1.0,
            SiteAltitudeMeters = 650.0,
            // OHP's own published median: Schmitt et al. 2024, A&A 687, A198 (the MISTRAL@OHP
            // instrument paper) quotes performance "under a median seeing (for OHP) of 2.5
            // arcsec". Taken as-is. The paper does not state a reference wavelength; 500nm is
            // assumed, being the convention every seeing figure is quoted at.
            ZenithSeeingFwhmArcsec = 2.5,

            NativeSensorWidthPx = 4144,
            NativeSensorHeightPx = 2822,
            NativePixelSizeMeters = 4.63e-6,
            QuantumEfficiency = 0.90,
            FullWellElectrons = 66000.0,
            ReadNoiseElectrons = 1.2,
            DarkCurrentElectronsPerSecond = 0.0022, // ZWO ASI294MM Pro, cooled to -20C

            AdcBits = 14,                       // ZWO's published figure for this readout mode
            // Derived, not invented: at gain 1 the full well fills the ADC range exactly, so
            // K = FullWell / (2^14 - 1) = 66000 / 16383 = 4.029 e-/ADU. Both inputs are ZWO's
            // own published numbers; ZWO does not tabulate K itself for this camera.
            ElectronsPerAduAtUnityGain = 66000.0 / 16383.0,
            // Sea-level cosmic-ray (muon) flux, ~1 per cm^2 per minute for a horizontal
            // detector, the standard figure (Particle Data Group, Cosmic Rays review). This
            // site is at 650m/1712m rather than sea level and the flux climbs with altitude, so
            // this is a floor rather than a measurement; unlike FORS2 no rate is published for
            // this camera at its site.
            CosmicRayEventsPerMinutePerCm2 = 1.0,

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
            // (centre 552.5nm), and R/G/B are its even thirds, the same 1:1:1 balanced split
            // the bandwidths above already assume, so the centres fall at the midpoint of each
            // third (B 420-508.3, G 508.3-596.7, R 596.7-685nm). H-alpha is the real line.
            LuminanceCentralWavelengthNm = 552.5,
            RedCentralWavelengthNm = 640.8,
            GreenCentralWavelengthNm = 552.5,
            BlueCentralWavelengthNm = 464.2,
            HAlphaCentralWavelengthNm = 656.3,

            // Not published: amateur LRGB and narrowband filter makers advertise "high
            // transmission" without a figure per filter, so the loss is unmodelled here rather
            // than assigned an invented number. See the field comments on FilterPeakTransmission.
            LuminanceFilterPeakTransmission = 1.0,
            RedFilterPeakTransmission = 1.0,
            GreenFilterPeakTransmission = 1.0,
            BlueFilterPeakTransmission = 1.0,
            HAlphaFilterPeakTransmission = 1.0,

            AvailableFilters = AmateurNarrowbandFilters,
            NarrowbandFilters = AmateurNarrowbandSet,
            AstigmatismStrengthPxAtCorner = 3.0f,
        };

        /// <summary>
        /// William Optics RedCat 51: a 51mm f/4.9 Petzval apochromatic astrograph, 250mm focal
        /// length, quadruplet FPL-53 objective, flat corrected field over a 45mm image circle
        /// (williamoptics.com / dealer product pages). The genuinely amateur end of the catalogue:
        /// it costs less than a used car, weighs 1.8kg, and every other instrument here outguns
        /// it on aperture by a factor of ten or more.
        ///
        /// It exists because APERTURE IS NOT THE ONLY AXIS, and this catalogue had only been
        /// exploring one of them. Every other entry is a long-focus astrograph built to resolve:
        /// the RC20's 3468mm gives a 0.32x0.22 degree field, and with its 4x Barlow, 0.08x0.05
        /// degrees. At Tycho-2's 62 stars/deg^2 that is 4 stars in a frame, and 0.26 with the
        /// Barlow in; so three planetary frames in four contain no catalogue star at all, and
        /// the star field the pipeline can draw is invisible for want of anything to draw.
        /// 250mm of focal length through the same sensor gives 4.40x2.99 degrees, 13.2 deg^2,
        /// and about 800 stars in every single exposure at that same density, far more with a
        /// deep Gaia catalogue installed. Nothing about the renderer
        /// changed; the instrument was simply pointed at the wrong end of the problem.
        ///
        /// Sampling: 3.82"/px unbinned, against 2.5" seeing at OHP. That is genuinely
        /// UNDERSAMPLED; a star lands on about one pixel and the PSF is narrower than the
        /// pixel grid, which is not a defect but the defining trade of every wide-field
        /// astrograph: sky coverage bought with resolution. Do not "fix" it.
        ///
        /// Optics: a refractor, so SecondaryObstructionFraction is 0; there is no secondary
        /// mirror in the light path, and the pupil is a filled circle rather than an annulus.
        /// No Barlow (BarlowFactor 1): a wide-field astrograph has one field, and bolting a
        /// Barlow onto it would simply undo the only reason to own it.
        ///
        /// Camera: the same ZWO ASI294MM Pro as the RC20, with every figure carried over
        /// unchanged. This is not a shortcut but how amateur astrophotography actually works (
        /// one camera, swapped between tubes), and it isolates this entry's difference to the
        /// optics alone.
        ///
        /// Filters: the same amateur LRGB + H-alpha wheel as the RC20, for the same reason.
        ///
        /// Astigmatism: 0px, and here that is documented rather than assumed. The Petzval design
        /// exists specifically to deliver a flat, corrected field, and the manufacturer specifies
        /// that correction across a 45mm image circle; the ASI294MM's sensor is 19.2x13.1mm, a
        /// 23.2mm diagonal, so the whole frame sits inside the inner half of the corrected
        /// circle. The RC20's 3.0px, by contrast, is a display calibration (see its own comment).
        /// </summary>
        public static readonly VisualTelescopeSpec RedCat51 = new VisualTelescopeSpec
        {
            Name = "William Optics RedCat 51",
            CameraName = "ZWO ASI294MM Pro",
            SiteName = "Observatoire de Haute-Provence",
            DetectorTemperatureCelsius = -20.0,
            CoolerDeltaBelowAmbientC = 35.0,        // same camera and same site as the RC20; see its comment
            SiteAmbientTemperatureCelsius = 11.8,

            ApertureMeters = 0.051,
            FocalLengthMeters = 0.250,
            // A Petzval refractor: no secondary mirror, so no spider and no spikes. A physical
            // fact about the design, not a missing measurement.
            SpiderVaneCount = 0,
            SpiderVaneWidthMeters = 0.0,
            BarlowFactor = 1.0,
            SecondaryObstructionFraction = 0.0,
            // A refractor has no mirrors, so there is no reflection loss to apply. What it does
            // have is eight air-glass surfaces (a Petzval quadruplet) and the camera window, and
            // William Optics publishes no transmission figure for the objective; so this
            // instrument's glass path is the roster's one wholly unmodelled optical loss, and it
            // is flagged rather than filled with a plausible-looking coating assumption.
            MirrorCount = 0,
            RelayOpticsTransmission = 1.0,
            SiteAltitudeMeters = 650.0,
            // Same OHP site as the RC20, same published median (Schmitt et al. 2024, A&A 687,
            // A198). An amateur rig is wherever its owner is, and putting it beside the RC20
            // keeps the two instruments differing in optics alone.
            ZenithSeeingFwhmArcsec = 2.5,

            NativeSensorWidthPx = 4144,
            NativeSensorHeightPx = 2822,
            NativePixelSizeMeters = 4.63e-6,
            QuantumEfficiency = 0.90,
            FullWellElectrons = 66000.0,
            ReadNoiseElectrons = 1.2,
            DarkCurrentElectronsPerSecond = 0.0022, // ZWO ASI294MM Pro, cooled to -20C

            AdcBits = 14,                       // ZWO's published figure for this readout mode
            // Derived, not invented: at gain 1 the full well fills the ADC range exactly, so
            // K = FullWell / (2^14 - 1) = 66000 / 16383 = 4.029 e-/ADU. Both inputs are ZWO's
            // own published numbers; ZWO does not tabulate K itself for this camera.
            ElectronsPerAduAtUnityGain = 66000.0 / 16383.0,
            // Sea-level cosmic-ray (muon) flux, ~1 per cm^2 per minute for a horizontal
            // detector, the standard figure (Particle Data Group, Cosmic Rays review). This
            // site is at 650m/1712m rather than sea level and the flux climbs with altitude, so
            // this is a floor rather than a measurement; unlike FORS2 no rate is published for
            // this camera at its site.
            CosmicRayEventsPerMinutePerCm2 = 1.0,

            MinExposureSeconds = 0.000032f,
            MaxExposureSeconds = 2000.0f,
            MinGain = 0.7f,
            MaxGain = 8.0f,

            LuminanceBandwidthAngstrom = 2650.0,
            RedBandwidthAngstrom = 2650.0 / 3.0,
            GreenBandwidthAngstrom = 2650.0 / 3.0,
            BlueBandwidthAngstrom = 2650.0 / 3.0,
            HAlphaBandwidthAngstrom = 70.0,

            LuminanceCentralWavelengthNm = 552.5,
            RedCentralWavelengthNm = 640.8,
            GreenCentralWavelengthNm = 552.5,
            BlueCentralWavelengthNm = 464.2,
            HAlphaCentralWavelengthNm = 656.3,

            // Same unpublished amateur filter set as the RC20; see that entry.
            LuminanceFilterPeakTransmission = 1.0,
            RedFilterPeakTransmission = 1.0,
            GreenFilterPeakTransmission = 1.0,
            BlueFilterPeakTransmission = 1.0,
            HAlphaFilterPeakTransmission = 1.0,

            AvailableFilters = AmateurNarrowbandFilters,
            NarrowbandFilters = AmateurNarrowbandSet,
            AstigmatismStrengthPxAtCorner = 0.0f,
        };

        /// <summary>
        /// PlaneWave CDK1000: 1.0m (1000mm / 39.37") Corrected Dall-Kirkham astrograph at f/6,
        /// 6000mm focal length, 47% central obstruction of the primary mirror diameter (all
        /// planewave.com official CDK1000 product page specs, the same optical tube PlaneWave
        /// also sells as part of the "PW1000" 1-meter observatory system). A real one of these
        /// was installed at Palomar Observatory, California (1712m altitude, per its Wikipedia
        /// entry) in 2024 to support MIT's WINTER project and Caltech research, used here as
        /// the site altitude, since PlaneWave's own product page doesn't specify a site. Paired
        /// with a real 4x Barlow for the "high power" end of the zoom range, same accessory
        /// class as the RC20 (see VisualTelescopeCatalog.Rc20). Camera is the same real ZWO
        /// ASI294MM Pro mono CCD as the RC20 (zwoastro.com/product/asi294), a genuine, common
        /// prosumer pairing on CDK-class instruments, not invented for this entry; see Rc20's own
        /// comment for the sensor's full datasheet sourcing (4144x2822 native resolution, 4.63um
        /// pixel pitch, 90% peak QE, 66,000 e- full well, 1.2 e- read noise, 0.0022 e-/s/pixel
        /// dark current at -20C, 32us-2000s exposure range).
        ///
        /// Net result vs. the RC20, both through the same sensor/Barlow: aperture diameter ratio
        /// 1000mm/510mm = 1.961, so despite the larger 47%-vs-39% obstruction, raw-area-ratio
        /// (1.961^2=3.845) * obstruction-factor-ratio ((1-0.47^2)/(1-0.39^2)=0.919) = ~3.53x the
        /// RC20's effective light-collecting area, plus a Dawes-limit resolving power
        /// (116/D(mm) arcsec: 0.116" vs the RC20's 0.227") that's nearly DOUBLE, not a marginal
        /// gain. At the same 4x Barlow, native (unbinned) plate scale is 0.0398"/px vs the RC20's
        /// 0.0688"/px, landing almost exactly at this telescope's own Dawes/3 critical-sampling
        /// point (0.0387"/px), so the extra magnification is fully backed by its finer diffraction
        /// limit, not empty magnification. That finer plate scale gives MinFovDeg ~0.0458 deg
        /// (~2.75') against the RC20's ~0.0792 deg (~4.75'), a real, visible 42% narrower frame
        /// for tightly resolving small, faint, or distant bodies the RC20 can't usefully reach.
        ///
        /// Astigmatism: unlike the plain-RC RC20, PlaneWave's own CDK1000 page states the design
        /// is "free of off-axis coma, astigmatism, and field curvature"; the CDK form adds a
        /// corrector near the focal plane specifically to cancel both third-order aberrations a
        /// bare Dall-Kirkham would otherwise have, not just coma the way an RC does. Taking the
        /// manufacturer's own flat-field claim at face value (no published CDK1000 datasheet
        /// gives a nonzero residual to the precision an aberration coefficient would need, so
        /// inventing one would be less defensible than the manufacturer's stated design goal),
        /// the corner astigmatism blur is 0px here.
        /// </summary>
        public static readonly VisualTelescopeSpec Cdk1000 = new VisualTelescopeSpec
        {
            Name = "PlaneWave CDK1000",
            CameraName = "ZWO ASI294MM Pro",
            SiteName = "Palomar Observatory",
            DetectorTemperatureCelsius = -20.0,
            // Same ZWO camera as the RC20 (see its comment for the delta and its caveat); different
            // site, so a different ambient to cool against. Palomar Mountain's published annual
            // high and low are 65 F and 47 F (usclimatedata.com), a mean of 56 F = 13.3 C.
            CoolerDeltaBelowAmbientC = 35.0,
            SiteAmbientTemperatureCelsius = 13.3,

            ApertureMeters = 1.000,
            FocalLengthMeters = 6.000,
            // Same as the RC20: real spider, no published vane width (section 12).
            SpiderVaneCount = 0,
            SpiderVaneWidthMeters = 0.0,
            BarlowFactor = 4.0,
            SecondaryObstructionFraction = 0.47,
            // Two mirrors, same aluminium figure as the RC20. The CDK's defining third element is
            // a refractive corrector near the focal plane, not a third mirror, so it costs
            // transmission rather than reflection, and PlaneWave publishes no figure for it, so
            // that loss sits in the unmodelled RelayOpticsTransmission below alongside the Barlow.
            MirrorCount = 2,
            MirrorReflectivity = 0.87,
            RelayOpticsTransmission = 1.0,
            SiteAltitudeMeters = 1712.0,
            // Cenko et al. 2006, PASP 118, 1396 (the automated P60 paper) is the citable Palomar
            // seeing measurement: "The average seeing at the P60 in the summer is ~1.1" in
            // R-band". Two things that figure is NOT, and which the number below accounts for:
            //
            //   * It is not a 500nm figure. It is quoted in R, and seeing goes as lambda^(-1/5)
            //     (r0 ~ lambda^(6/5), FWHM = 0.98*lambda/r0, the same relation
            //     SolarSystemCameraTexture applies per-filter). Referred to 500nm through
            //     Cousins R at 641nm (Bessell 1990, PASP 102, 1181), 1.1" becomes 1.16".
            //   * It is not an annual median. The same paper gives ~1.6" in winter. The summer
            //     value is used here because it is the one stated without qualification; this is
            //     therefore the site's GOOD season, not its year-round typical.
            //
            // The paper also notes the P60 runs "~0.2" worse than the values reported at the
            // 200" Hale", not applied, since that would be arithmetic on a second quoted
            // figure to reach a number the paper never states.
            ZenithSeeingFwhmArcsec = 1.16,

            NativeSensorWidthPx = 4144,
            NativeSensorHeightPx = 2822,
            NativePixelSizeMeters = 4.63e-6,
            QuantumEfficiency = 0.90,
            FullWellElectrons = 66000.0,
            ReadNoiseElectrons = 1.2,
            DarkCurrentElectronsPerSecond = 0.0022, // ZWO ASI294MM Pro, cooled to -20C

            AdcBits = 14,                       // ZWO's published figure for this readout mode
            // Derived, not invented: at gain 1 the full well fills the ADC range exactly, so
            // K = FullWell / (2^14 - 1) = 66000 / 16383 = 4.029 e-/ADU. Both inputs are ZWO's
            // own published numbers; ZWO does not tabulate K itself for this camera.
            ElectronsPerAduAtUnityGain = 66000.0 / 16383.0,
            // Sea-level cosmic-ray (muon) flux, ~1 per cm^2 per minute for a horizontal
            // detector, the standard figure (Particle Data Group, Cosmic Rays review). This
            // site is at 650m/1712m rather than sea level and the flux climbs with altitude, so
            // this is a floor rather than a measurement; unlike FORS2 no rate is published for
            // this camera at its site.
            CosmicRayEventsPerMinutePerCm2 = 1.0,

            MinExposureSeconds = 0.000032f,
            MaxExposureSeconds = 2000.0f,
            MinGain = 0.7f,
            MaxGain = 8.0f,

            LuminanceBandwidthAngstrom = 2650.0,
            RedBandwidthAngstrom = 2650.0 / 3.0,
            GreenBandwidthAngstrom = 2650.0 / 3.0,
            BlueBandwidthAngstrom = 2650.0 / 3.0,
            HAlphaBandwidthAngstrom = 70.0,

            // Same real amateur LRGB filter set as the RC20 (same camera, same accessory class);
            // see Rc20's own comment for how these centres follow from the band's even thirds.
            LuminanceCentralWavelengthNm = 552.5,
            RedCentralWavelengthNm = 640.8,
            GreenCentralWavelengthNm = 552.5,
            BlueCentralWavelengthNm = 464.2,
            HAlphaCentralWavelengthNm = 656.3,

            // Same unpublished amateur filter set as the RC20; see that entry.
            LuminanceFilterPeakTransmission = 1.0,
            RedFilterPeakTransmission = 1.0,
            GreenFilterPeakTransmission = 1.0,
            BlueFilterPeakTransmission = 1.0,
            HAlphaFilterPeakTransmission = 1.0,

            AvailableFilters = AmateurNarrowbandFilters,
            NarrowbandFilters = AmateurNarrowbandSet,
            AstigmatismStrengthPxAtCorner = 0.0f,
        };

        /// <summary>
        /// The VLT (Very Large Telescope), Unit Telescope 1 "Antu", Paranal Observatory,
        /// fitted with its real FORS2 (FOcal Reducer/low dispersion Spectrograph 2) imager.
        /// Every number below is FORS2's own real, published spec; no ZWO/amateur hardware
        /// substituted in, per Baptiste's explicit call: this is meant to double as a real
        /// scientific reference, not a reskinned consumer camera.
        ///
        /// Optics: 8.2m Cassegrain aperture (ESO), M2 secondary 1.116m diameter (ESO M2 Unit
        /// page, eso.org/sci/facilities/paranal/telescopes/ut/m2unit.html) -> obstruction
        /// fraction 1.116/8.2 = 0.1361. FORS2's own collimator+camera relay reduces the VLT's
        /// natural f/15 Cassegrain beam to a real measured/published plate scale of 0.126"/pixel
        /// (unbinned) in its Standard-Resolution (SR) mode; rather than simulate the multi-
        /// element relay, the equivalent single focal length that reproduces that REAL plate
        /// scale with the REAL 15um pixel is used: FL = pixelSize / (0.126"/206265) = 24.556m.
        /// FORS2 also has a real High-Resolution (HR) collimator, independently confirmed via its
        /// own published focal length (1233mm SR vs 616mm HR, ratio 2.001), used here as the
        /// real "Barlow" for the zoom range's tight end, in place of an invented amateur
        /// accessory. Site altitude 2635m, Paranal (same value already used for ESPRESSO in
        /// Observatories.cs, one physical site, one number).
        ///
        /// Sensor: real mosaic of two MIT/Lincoln-Lab CCID20 CCDs (eso.org FORS2 User Manual;
        /// chip identity cross-confirmed via Wittman et al. 1998 SPIE 3355, 598 and the CFH12K
        /// technical notes, which used the same part), each 4096x2048px at 15x15um, stacked
        /// vertically with a real 32px/480um gap -> combined mosaic 4096x4128px. QE: real
        /// measured curve (eso.org/sci/php/optdet/instruments/fors2/Fors2old/qe.html): 400nm
        /// 58%, 500nm 74%, 600nm 86% (peak), 700nm 83%, 800nm 66%, 900nm 39%; 86% (peak) is used
        /// as this pipeline's single QE scalar, the same "headline/peak" convention the RC20/
        /// CDK1000 entries use for their ZWO datasheet's 90%. Full well: 150,000 e-, the CCID20
        /// chip's own real spec (Cuillandre et al., CFH12K/ESO CCD workshop 1999 technical note;
        /// FORS2's own manual doesn't restate a full-well number for the shared chip). Gain and
        /// read noise are FORS2's own directly-published values for its real "100kHz,2x2,high"
        /// readout mode: 0.7 e-/ADU, RON 2.7 ADU (Chip1) = 1.89 e-. Dark current: FORS2's own
        /// published 3 e-/pixel/hour at its real -120C operating temperature (0.000833 e-/s).
        /// As of this codebase's current date, the FORS-Up detector replacement project (arXiv
        /// 2012.09227, progress report arXiv:2407.02979) is still in ground testing and not
        /// expected on-sky before 2027; so this CCID20-based spec IS the currently operating
        /// real instrument, not an outdated one.
        ///
        /// Gain control: unlike the RC20/CDK1000's ZWO CMOS cameras, a real scientific CCD like
        /// FORS2 has no continuously-variable ISO-like gain; its gain is fixed by the readout
        /// electronics at whichever mode is configured (0.7 e-/ADU above). MinGain == MaxGain
        /// here for that reason: it's a real, documented instrument limitation, not a shortcut.
        ///
        /// Exposure range: 0.25s minimum is FORS2's own published shortest full-frame imaging
        /// exposure. There is no real published maximum; a professional CCD isn't electronically
        /// capped the way a consumer camera is, only practically limited by sky background/cosmic-
        /// ray accumulation. 3600s (1 hour) is used as a deliberate, coherent design choice
        /// matching standard real observatory practice of capping a single sub around that length
        /// and reaching longer total integration by stacking (this mod's own AstroImageStack
        /// already does exactly that), not a fabricated hardware spec.
        ///
        /// Filters: FORS2's own real broadband filter set, each with its own real bandwidth (ESO
        /// FORS2 Standard Filters page): b_HIGH (429nm/88nm FWHM) as Blue, v_HIGH (554nm/111nm
        /// FWHM) as Green, R_SPECIAL (655nm/165nm FWHM) as Red. HAlpha uses the real Halpha+83
        /// narrowband filter (656.3nm center, 61 Angstrom FWHM). Luminance represents a genuine
        /// unfiltered/clear exposure across the CCD's real full quoted sensitivity range
        /// (330-1100nm = 7700 Angstrom); FORS2 has no dedicated amateur-style "L" filter, so
        /// this is the real clear-aperture equivalent, not an invented one.
        ///
        /// Astigmatism: FORS2/the VLT Cassegrain focus is a real, well-corrected two-mirror
        /// system, but no published VLT optical prescription gives a field-dependent astigmatism
        /// coefficient to the precision this pipeline's display model would need (same honesty
        /// standard as the RC20's own 3.0px figure), rather than invent one for an instrument
        /// this well-documented everywhere else, astigmatism is left at 0px here.
        ///
        /// Tracking: a real 8.2m Unit Telescope always has precision active guiding; there is
        /// no real "bare, unguided VLT" the way a hobbyist's RC20 might genuinely lack an
        /// autoguider. AlwaysAutoguided forces this in the pipeline, since without it the same
        /// diurnal-drift trailing the RC20/CDK1000 can show at high zoom (correctly, for those
        /// amateur instruments) would appear on VLT frames too, which isn't how the real
        /// instrument operates.
        /// </summary>
        public static readonly VisualTelescopeSpec Fors2Vlt = new VisualTelescopeSpec
        {
            Name = "ESO-VLT-U1",
            CameraName = "FORS2",
            SiteName = "Paranal Observatory",
            DetectorTemperatureCelsius = -120.0,

            ApertureMeters = 8.2,
            FocalLengthMeters = 24.556,
            // ESO measured these three in the instrument and publishes the tables, so they are
            // carried as real curves rather than as top-hats of their published FWHM. The other
            // two filter positions (Luminance = unfiltered, H-alpha) keep the top-hat treatment.
            BlueFilterCurve = FilterCurves.Fors2B,
            GreenFilterCurve = FilterCurves.Fors2V,
            RedFilterCurve = FilterCurves.Fors2R,

            // VLT UT spider. The width comes from the scaled VLT pupil masks used throughout the
            // coronagraphy literature: Martinez et al. (2011) cut a 3 mm mask for the 8 m pupil
            // with "the spider-vane thickness is 15 um +/- 4 um", which at this telescope's 8.2 m
            // is 4.1 +/- 1.1 cm. The scaling validates itself: the SAME paper's E-ELT mask uses
            // 40 um vanes, which scaled to 39.3 m gives 52 cm against the 50 cm Schwartz et al.
            // (2018) state in prose for the real ELT, a 4% agreement.
            // The COUNT is weaker evidence than the width: ESO's technical prose says only that
            // M2 is held "by means of metallic beams called spiders" without giving a number, so
            // four is read from the telescope's own structure rather than quoted (section 12).
            SpiderVaneCount = 4,
            SpiderVaneWidthMeters = 0.041,
            BarlowFactor = 2.0,
            SecondaryObstructionFraction = 1.116 / 8.2,
            // FORS2 sits at UT1's CASSEGRAIN focus (ESO's own caption for image eso9857a reads
            // "FORS at VLT UT1 Cassegrain focus"), and the VLT's Cassegrain path is M1 -> M2 ->
            // focus. Two aluminium surfaces, therefore, against SPHERE's three at the Nasmyth
            // focus of UT3, the same telescope delivering measurably different throughput to
            // the two instruments purely because of where they are bolted on.
            MirrorCount = 2,
            MirrorReflectivity = 0.87,
            // Not modelled: FORS2's own collimator + camera relay is a multi-element refractive
            // train, and ESO's manual publishes the resulting plate scale (which this entry uses)
            // but no transmission for the relay itself.
            RelayOpticsTransmission = 1.0,
            AlwaysAutoguided = true,
            SiteAltitudeMeters = 2635.0,
            // Paranal's published median seeing, from ESO's own astroclimate page for the site
            // (eso.org/sci/facilities/paranal/astroclimate): "The 50% percentile is 0.72" FWHM".
            // This, not the 8.2m mirror, is what sets FORS2's delivered resolution, the whole
            // reason the instrument is described as seeing-limited.
            ZenithSeeingFwhmArcsec = 0.72,

            NativeSensorWidthPx = 4096,
            NativeSensorHeightPx = 4128,
            NativePixelSizeMeters = 15e-6,
            QuantumEfficiency = 0.86,
            // ESO's own published QE curve for the MIT/LL CCID20 mosaic
            // (eso.org/sci/php/optdet/instruments/fors2/Fors2old/qe.html). This is why the curve
            // matters rather than the 86% peak alone: b_HIGH sits at 440nm where the detector is
            // at 58%, so using the peak credited a blue exposure with about 1.5x the electrons it
            // really collects. Now integrated across the passband (see SystemBandpass).
            QuantumEfficiencyCurve = new SpectralCurve(
                new[] { 400.0, 500.0, 600.0, 700.0, 800.0, 900.0 },
                new[] { 0.58,  0.74,  0.86,  0.83,  0.66,  0.39 }),
            FullWellElectrons = 150000.0,

            // Detector figures below are the MIT mosaic's, in its real IMAGING readout: the
            // manual states the imaging modes run at 200 kHz, which is the "low gain" column of
            // its Table 2.8 (the 100 kHz / high gain column is the spectroscopic mode). All from
            // the current ESO FORS2 User Manual, VLT-MAN-ESO-13100-1543.
            //
            // Two of these CORRECT earlier values in this file that did not match the manual:
            // read noise was 1.89 e- (the manual's MIT chip-1 200 kHz figure is 3.8 e-), and
            // dark current was 3.0 e-/px/h (the manual's Table 2.9 gives 2.1 +/- 0.4 e-/px/h for
            // MIT chip 1 at -120C). The old numbers appear to come from an older manual revision.
            ReadNoiseElectrons = 3.8,                       // Table 2.8, MIT chip 1, 200 kHz
            DarkCurrentElectronsPerSecond = 2.1 / 3600.0,   // Table 2.9, MIT chip 1, -120C

            AdcBits = 16,
            ElectronsPerAduAtUnityGain = 1.25,              // Table 2.8, K for MIT chip 1, 200 kHz
            // Table 2.9, measured on the MIT mosaic at Paranal (2635m). Nearly eight times the
            // sea-level muon flux, which is what 2.6 km of altitude does to the cosmic-ray rate,
            // and a good illustration of why this belongs per instrument rather than as one
            // global constant.
            CosmicRayEventsPerMinutePerCm2 = 7.7,

            MinExposureSeconds = 0.25f,
            MaxExposureSeconds = 3600.0f,
            MinGain = 1.0f,
            MaxGain = 1.0f,

            // FORS2's own real broadband filters, from ESO's current FORS Filter Specifications
            // page: b_HIGH+113 at 440nm/103.5nm FWHM, v_HIGH+114 at 557nm/123.5nm, R_SPECIAL+76
            // at 655nm/165.0nm, and the narrowband H_Alpha+83 at 656.3nm/6.1nm in the standard
            // collimator. Luminance is FORS2's own full sensitivity range (~330-1100nm, i.e. 7700
            // Angstrom), centre 715nm, a genuine unfiltered exposure, since FORS2 has no
            // amateur-style clear "L" filter.
            //
            // Two of these CORRECT earlier values in this file, which were the standard Bessell B
            // and V figures (429nm/88nm and 554nm/111nm) rather than FORS2's own b_HIGH and
            // v_HIGH: same passband names, different real filters, and ESO's page is the
            // authority for the ones actually in this instrument's wheel.
            LuminanceBandwidthAngstrom = 7700.0,
            RedBandwidthAngstrom = 1650.0,
            GreenBandwidthAngstrom = 1235.0,
            BlueBandwidthAngstrom = 1035.0,
            HAlphaBandwidthAngstrom = 61.0,

            LuminanceCentralWavelengthNm = 715.0,
            RedCentralWavelengthNm = 655.0,
            GreenCentralWavelengthNm = 557.0,
            BlueCentralWavelengthNm = 440.0,
            HAlphaCentralWavelengthNm = 656.3,

            // The one published peak transmission anywhere in this roster: ESO's FORS filter page
            // tabulates H_Alpha+83 at 0.70 in the standard-resolution collimator (0.76 in the HR
            // collimator, which this pipeline models as the tight end of the zoom range; 0.70 is
            // used as the value for the default configuration rather than switching between them,
            // since the pipeline applies one filter transmission per exposure). The broadband
            // filters have no published peak transmission and are therefore unmodelled.
            LuminanceFilterPeakTransmission = 1.0,
            RedFilterPeakTransmission = 1.0,
            GreenFilterPeakTransmission = 1.0,
            BlueFilterPeakTransmission = 1.0,
            HAlphaFilterPeakTransmission = 0.70,

            AvailableFilters = AllFilters,
            AstigmatismStrengthPxAtCorner = 0.0f,
        };

        /// <summary>
        /// The VLT, Unit Telescope 3 "Melipal", Paranal, fitted with its real SPHERE/ZIMPOL
        /// extreme-adaptive-optics imaging polarimeter. Same 8.2m aperture and Paranal site
        /// (2635m) as FORS2/UT1, but a different, dedicated UT (Schmid et al. 2018, A&amp;A 619,
        /// A9, "SPHERE/ZIMPOL high resolution polarimetric imager. I."). Every number below is
        /// that paper's own published spec (its Table 4 gives the detector figures directly);
        /// nothing here is estimated or invented.
        ///
        /// The whole point of this instrument: FORS2 is SEEING-limited (atmospheric turbulence
        /// blurs it to Paranal's real ~0.6-1" typical seeing, no matter the 8.2m mirror behind
        /// it), while SPHERE's real-time adaptive optics (SAXO) actively corrects that
        /// turbulence, so ZIMPOL gets much closer to the telescope's own true diffraction limit
        /// instead. See AdaptiveOpticsFwhmArcsec below.
        ///
        /// Optics: real f/221 system feeding ZIMPOL, giving a real published plate scale of
        /// 3.6 mas/pixel at the detector's standard 2x2-on-chip-binned mode, the equivalent
        /// focal length that reproduces this with the real 15um native (unbinned) pixel is used
        /// (FL = 30um / (3.6mas/206265) = 1718.7m), so this pipeline's own BinningFactor=1 gives
        /// ZIMPOL's real unbinned 1.8 mas/pixel mode and BinningFactor=2 reproduces its real
        /// documented "standard imaging" 3.6 mas/pixel mode exactly; no separate Barlow exists
        /// for this instrument (BarlowFactor=1). Cross-check: at native pixel count (2048px),
        /// this gives a computed FOV of ~3.49", matching ZIMPOL's own real published 3.6"x3.6"
        /// field to within rounding of the two independently-quoted source numbers. Obstruction
        /// reuses the VLT UT's own real M2/M1 ratio (see Fors2Vlt), the same shared telescope
        /// hardware, not a SPHERE-internal figure (none published to the precision needed).
        ///
        /// Sensor: real ZIMPOL CCD, 15um native pixels, back-illuminated frame-transfer, 2k x 2k
        /// raw format. QE 95% (peak, at 600nm; the paper also gives 90% at 700nm and 65% at
        /// 800nm). Imaging-mode figures straight from the paper's Table 4: full well 640,000 e-
        /// /pixel, read noise 20 e-/pixel, dark current 0.2 e-/s/pixel, minimum integration time
        /// 1.1s. No published maximum, same 3600s (1 hour) coherent design choice as Fors2Vlt,
        /// for the same reasoning (real observatory practice, not a fabricated hardware limit).
        /// Gain is FORS2-style fixed (10.5 e-/ADU is the real hardware conversion factor, not a
        /// player-adjustable ISO), so MinGain == MaxGain == 1.0 here too.
        ///
        /// Adaptive optics: SAXO achieves a real, published resolution of about 25 mas FWHM in
        /// good conditions (Strehl ~40% in I-band) per the ZIMPOL system paper itself; a second,
        /// independent paper (Milli et al., search results for ZIMPOL H-alpha imaging) states
        /// SPHERE/ZIMPOL "routinely" reaches 22-28 mas FWHM across V/R/I; 25 mas sits at the
        /// middle of that independently-confirmed range. Used as AdaptiveOpticsFwhmArcsec, this
        /// REPLACES the plain ground-based seeing model (see ComputeGroundSeeingFwhmArcsec) with this
        /// real, roughly airmass-independent achieved resolution, about 24-40x finer than
        /// FORS2's typical seeing-limited blur, which is the entire reason this instrument can
        /// resolve targets FORS2 can only show as a barely-resolved smudge.
        ///
        /// Filters: real ZIMPOL broadband filters, each with its own real published bandwidth
        /// (search results citing the paper's filter table): V (554nm/80.6nm FWHM) as Green,
        /// N_R (646nm/57nm FWHM) as Red, B_Ha (655.6nm/5.5nm FWHM; the broader of ZIMPOL's two
        /// real Halpha filters, N_Ha at 0.97nm FWHM being too narrow for a simple broadband-style
        /// single exposure) as HAlpha. Luminance uses ZIMPOL's own quoted working spectral
        /// regime, 500-900nm (4000 Angstrom), as the real clear/broadband-equivalent range,
        /// same "genuine full-sensitivity range, not an amateur L filter" approach as Fors2Vlt.
        /// ZIMPOL genuinely has NO real blue broadband filter (its filter set targets red/near-IR
        /// reflected-light and circumstellar-disk science, not true-color RGB); rather than
        /// invent one, AvailableFilters simply omits Blue, and the GUI's filter wheel doesn't
        /// offer it for this instrument. BlueBandwidthAngstrom is left at 0 and is unreachable.
        ///
        /// Astigmatism: ZIMPOL's real field of view is only 3.6"x3.6", far too narrow for
        /// off-axis Seidel astigmatism to grow to any meaningful amplitude regardless of the
        /// telescope's prescription, so 0px here is well-justified by the field size alone, not
        /// just the usual "no published coefficient" reasoning.
        ///
        /// Tracking: an extreme-AO system inherently requires continuous, high-precision guiding
        /// on a reference star to work at all, if anything a harder requirement than FORS2's,
        /// so this is AlwaysAutoguided too.
        /// </summary>
        public static readonly VisualTelescopeSpec Sphere = new VisualTelescopeSpec
        {
            Name = "ESO-VLT-U3",
            CameraName = "SPHERE/ZIMPOL",
            SiteName = "Paranal Observatory",

            ApertureMeters = 8.2,
            FocalLengthMeters = 1718.7,
            BarlowFactor = 1.0,
            SecondaryObstructionFraction = 1.116 / 8.2,
            // Same telescope structure as UT1, so the same spider (see the FORS2 entry for the
            // sourcing). It matters far more here: ZIMPOL is the only instrument in the roster
            // whose plate scale actually RESOLVES the diffraction pattern, so it is the only one
            // where the spikes are visible rather than sitting below one pixel.
            SpiderVaneCount = 4,
            SpiderVaneWidthMeters = 0.041,
            // SPHERE is on UT3's NASMYTH platform, so its path is M1 -> M2 -> M3 flat -> focus:
            // three aluminium surfaces where Cassegrain-mounted FORS2 has two. At 0.87 per surface
            // that is 0.659 against FORS2's 0.757, i.e. the extra relay mirror alone costs 13% of
            // the light, the same "an extra mirror yields 13% extra light loss with Al coating"
            // that Ma & Cai (arXiv:1708.01257) state explicitly.
            MirrorCount = 3,
            MirrorReflectivity = 0.87,
            // Real, published, and the largest single throughput term on this instrument: the grey
            // zonal beam splitter (zw.BS) transmits "about 79% of the light to ZIMPOL and 21% to
            // the WFS" (Schmid et al. 2018, Sect. 2); an extreme-AO system must spend a fifth of
            // its light on sensing the wavefront it is correcting, which is a real cost of the
            // correction, not an inefficiency. SPHERE's other internal optics are not separately
            // published, so only this factor is modelled. Polarimetric mode, which the same paper
            // says costs a further factor 0.85, is not simulated here; this pipeline images.
            RelayOpticsTransmission = 0.79,
            SiteAltitudeMeters = 2635.0,
            AlwaysAutoguided = true,
            // Same Paranal sky as FORS2, same ESO 50%-percentile figure. Recorded for
            // completeness only: an instrument with AdaptiveOpticsFwhmArcsec set never takes the
            // plain seeing path, because SAXO corrects this turbulence rather than suffering it.
            ZenithSeeingFwhmArcsec = 0.72,

            NativeSensorWidthPx = 2048,
            NativeSensorHeightPx = 2048,
            NativePixelSizeMeters = 15e-6,
            QuantumEfficiency = 0.95,
            // Schmid et al. 2018: "The quantum efficiencies of the (bare) CCDs are about 0.95,
            // 0.90 and 0.65 at lambda = 600 nm, 700 nm and 800 nm respectively." Three points is
            // all the paper gives, and flat extrapolation below 600nm is the honest reading, but
            // it is enough to matter, since ZIMPOL's own Luminance regime runs to 900nm where the
            // detector has clearly fallen away from its peak.
            QuantumEfficiencyCurve = new SpectralCurve(
                new[] { 600.0, 700.0, 800.0 },
                new[] { 0.95,  0.90,  0.65 }),
            FullWellElectrons = 640000.0,
            ReadNoiseElectrons = 20.0,
            DarkCurrentElectronsPerSecond = 0.2, // real ZIMPOL imaging-mode spec (Table 4)

            AdcBits = 16,
            // 10.5 e-/ADU is ZIMPOL's real published hardware conversion factor (Schmid et al.
            // 2018, Table 4). The bit depth is not stated there, but 16 is the only value
            // consistent with it: 10.5 x 65535 = 688,100 e-, just above this detector's own
            // 640,000 e- full well, which is exactly how a well-matched CCD chain is specified
            // (the ADC reaches the full well and barely more). 14 bits would truncate at
            // 172,000 e-, throwing away three quarters of the well the paper documents.
            ElectronsPerAduAtUnityGain = 10.5,
            CosmicRayEventsPerMinutePerCm2 = 7.7, // same Paranal altitude and epoch as FORS2

            MinExposureSeconds = 1.1f,
            MaxExposureSeconds = 3600.0f,
            MinGain = 1.0f,
            MaxGain = 1.0f,

            LuminanceBandwidthAngstrom = 4000.0,
            RedBandwidthAngstrom = 570.0,
            GreenBandwidthAngstrom = 806.0,
            BlueBandwidthAngstrom = 0.0, // no real ZIMPOL blue filter; see AvailableFilters
            HAlphaBandwidthAngstrom = 55.0,

            // Real ZIMPOL filter centres, the same ones the bandwidths above come from (Schmid
            // et al. 2018): V 554nm as Green, N_R 646nm as Red, B_Ha 655.6nm as HAlpha.
            // Luminance is ZIMPOL's own quoted 500-900nm working regime, centre 700nm. Blue
            // stays 0; there is no real ZIMPOL broadband blue filter and the position is
            // unreachable (see AvailableFilters immediately below).
            LuminanceCentralWavelengthNm = 700.0,
            RedCentralWavelengthNm = 646.0,
            GreenCentralWavelengthNm = 554.0,
            BlueCentralWavelengthNm = 0.0,
            HAlphaCentralWavelengthNm = 655.6,

            // Not published per filter: Schmid et al. 2018 states that total instrument throughput
            // per filter "should be determined for flux measurements" and refers the reader to
            // Schmid et al. (2017) for preliminary values, so no per-filter figure is taken from
            // the cited paper. The instrument's own dominant throughput term, the 79% beam
            // splitter, is real and applied above in RelayOpticsTransmission instead.
            LuminanceFilterPeakTransmission = 1.0,
            RedFilterPeakTransmission = 1.0,
            GreenFilterPeakTransmission = 1.0,
            BlueFilterPeakTransmission = 1.0, // no real ZIMPOL blue filter, unreachable, see AvailableFilters
            HAlphaFilterPeakTransmission = 1.0,

            AvailableFilters = new[] { CameraFilter.Luminance, CameraFilter.Red, CameraFilter.Green, CameraFilter.HAlpha },

            AdaptiveOpticsFwhmArcsec = 0.025,
            // Strehl ~40% in I band, the ZIMPOL system paper's own quoted performance alongside
            // the 25 mas figure above. The halo is Paranal's real median seeing, since the halo
            // is by definition the fraction SAXO did not correct; so it must be, and now is,
            // the identical figure ZenithSeeingFwhmArcsec carries above: ESO's own published
            // 50% percentile of 0.72" (eso.org/sci/facilities/paranal/astroclimate). This field
            // previously read 0.65" while citing ESO, which is not the number ESO publishes.
            AdaptiveOpticsStrehlRatio = 0.40,
            AdaptiveOpticsHaloSeeingFwhmArcsec = 0.72,
            AstigmatismStrengthPxAtCorner = 0.0f,
            // SPHERE's common path carries an ADC for the visible arm (Beuzit et al. 2019, A&A 631,
            // A155). Without one the atmosphere would spread 400-700 nm across 307 of ZIMPOL's
            // pixels at 45 degrees from the zenith, and a 25 mas core would be unreachable.
            HasAtmosphericDispersionCorrector = true,
        };

        /// <summary>Every visual telescope available to the in-game instrument selector (the Observatory dropdown in ExoInstrumentsGUI; see InstrumentSpec.VisualTelescope), in unlock/display order.</summary>
        public static readonly VisualTelescopeSpec[] All = { RedCat51, Rc20, Cdk1000, Fors2Vlt, Sphere };
    }
}
