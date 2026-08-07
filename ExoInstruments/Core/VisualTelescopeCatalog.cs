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
    /// Which detector technology an instrument carries, and therefore which physics its frames go
    /// through. This is not a label: the two branches differ by which effects EXIST, not by which
    /// numbers are plugged into one chain. See VisualTelescopeSpec.Technology.
    /// </summary>
    public enum DetectorTechnology
    {
        /// <summary>Charge is clocked to a shared output: charge-transfer inefficiency, blooming along the column, one destructive read.</summary>
        Ccd,

        /// <summary>Each pixel is read where it sits through its own amplifier: no transfer, no bleeding, sampled non-destructively up the ramp.</summary>
        HgCdTeArray,
    }

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

        /// <summary>
        /// The orbiting platform this instrument flies on, or null for a ground instrument.
        ///
        /// This one field is what the whole imaging pipeline branches on. Non-null means: no
        /// seeing, no extinction, no scintillation, no atmospheric dispersion, no airglow, no
        /// day/night cycle and no airmass, because none of those exist above the atmosphere;
        /// and in their place, the constraints and the sky background that only exist up there
        /// (see SpacePlatformSpec, OrbitalVisibility, Earthshine, ZodiacalLight).
        ///
        /// Deliberately NOT a bool. A boolean would say only that the atmosphere is gone; what
        /// actually has to be known is which spacecraft, because every replacement term is a
        /// property of that spacecraft rather than of "space".
        /// </summary>
        public SpacePlatformSpec SpacePlatform;

        /// <summary>True when this instrument observes from orbit. Ground instruments carry no platform at all.</summary>
        public bool IsSpaceBased => SpacePlatform != null;

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
        /// Circular obscurations in the pupil that are neither the secondary nor the spider: the
        /// pads holding the primary mirror in its cell, positioned and sized in fractions of the
        /// pupil radius (see PupilPad).
        ///
        /// Null for every instrument whose pupil table nobody publishes, which is all of the
        /// amateur and most of the professional roster: a pad's position has to be measured on
        /// the actual telescope, and no manufacturer datasheet carries one. It is populated only
        /// where a real pupil table exists to transcribe, which so far means HST, whose Tiny Tim
        /// pupil files give all three pads to four decimal places.
        /// </summary>
        public PupilPad[] PrimaryMirrorPads;

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

        /// <summary>
        /// Charge one cosmic-ray EVENT deposits in total, electrons, spread along its track.
        ///
        /// Separate from the rate above because the two are separately measured and separately
        /// published: how OFTEN the silicon is struck and how MUCH charge a strike leaves are
        /// different quantities, and a pipeline that gets the first right can still draw the
        /// second as a saturated streak. Zero means the instrument's maker publishes no figure,
        /// in which case ApplyCosmicRays keeps its old full-well behaviour rather than inventing
        /// one; this file's usual convention for an unmodelled quantity.
        /// </summary>
        public double CosmicRayElectronsPerEvent;

        /// <summary>
        /// How many pixels of the underlying silicon, along one axis, are summed into one pixel of
        /// the format this entry quotes above.
        ///
        /// Almost always 1, and NOT always: the ASI294MM Pro's 4144x2822 at 4.63um is the Sony
        /// IMX492's 8288x5644 at 2.315um summed 2x2 inside the sensor, which is why its full well
        /// is four times the single pixel's. That distinction cannot be ignored once per-pixel
        /// non-uniformity is modelled, because binning moves the two components of it in OPPOSITE
        /// directions: the photo-response spread averages down as 1/n while the additive offset
        /// spread grows as n (see Core.SensorNonUniformity). Quoting a sensor-level figure against
        /// the wrong pixel would therefore be wrong twice over, by a factor of two each way.
        /// </summary>
        public int SensorNativePixelsPerSide = 1;

        /// <summary>
        /// Photo-response non-uniformity: the spatial standard deviation of this sensor's response
        /// to uniform illumination, as a FRACTION of the mean, quoted for one pixel of the
        /// underlying silicon (see SensorNativePixelsPerSide).
        ///
        /// NaN means NOT PUBLISHED for this device, the same convention the filter transmissions
        /// use, and it disables the term rather than substituting a plausible one. A borrowed PRNU
        /// would be worse than none: it is a floor on the photometric precision of every
        /// measurement made from the frame, so an invented value would set an invented floor and
        /// the pipeline would report it as a result.
        /// </summary>
        public double PhotoResponseNonUniformity = double.NaN;

        /// <summary>
        /// Offset fixed-pattern noise: the spatial standard deviation of the per-pixel readout
        /// zero, in electrons, again for one pixel of the underlying silicon.
        ///
        /// This is EMVA 1288's DSNU where a camera maker publishes one, and ESO's QC.BIAS.FPN where
        /// an observatory trends one. NaN means not published. Note what it is not: it is not the
        /// pixel-to-pixel spread of the DARK CURRENT, which grows with exposure time and with
        /// temperature. This one is present in a zero-second exposure and independent of both,
        /// because it is an offset rather than a rate.
        /// </summary>
        public double OffsetFixedPatternElectrons = double.NaN;

        /// <summary>
        /// Relative deviation from linearity at full well, as a fraction. NaN means not published.
        /// See Core.DetectorLinearity for the form, and for what the published figure it is read
        /// from does and does not state.
        /// </summary>
        public double LinearityDeviationAtFullWell = double.NaN;

        /// <summary>
        /// Side of a square field stop in the focal plane, in arcminutes, for an instrument whose
        /// illuminated field is smaller than its detector. NaN means the whole detector is
        /// illuminated, which is true of every instrument here except FORS2.
        /// </summary>
        public double FieldStopSquareArcmin = double.NaN;

        /// <summary>
        /// The focal-plane masks this instrument's coronagraph carries, or null for an instrument
        /// that has none, which is every entry on this roster except SPHERE.
        ///
        /// Deliberately the mask TABLE rather than a boolean. A coronagraph is not a capability an
        /// instrument either has or lacks; it is a set of masks that trade inner working angle
        /// against attenuation, and choosing between them is the observation (see
        /// Core.Coronagraph).
        /// </summary>
        public Coronagraph.Mask[] CoronagraphMasks;

        /// <summary>
        /// The Lyot pupil stop that goes with those masks. Only meaningful when CoronagraphMasks
        /// is non-null, and it is where the suppression actually happens: the focal-plane mask
        /// converts the star's light into a ring around the pupil edge, and this is what throws
        /// that ring away.
        /// </summary>
        public Coronagraph.LyotStop CoronagraphLyotStop;

        /// <summary>Actuators across the deformable mirror of the adaptive-optics system in front of this instrument, or 0 where there is none. Sets the AO control radius, and with it where the speckle ring falls (see Core.SpeckleField).</summary>
        public int AdaptiveOpticsActuatorsAcrossPupil;

        /// <summary>
        /// Brighter-fatter: the nearest-neighbour charge correlation this detector shows in a flat
        /// field, horizontally and vertically, and the signal level they were measured at. NaN
        /// means not published, which is every instrument on this roster.
        ///
        /// THE MECHANISM IS MODELLED AND THE AMPLITUDE IS NOT AVAILABLE, which is a different
        /// statement from the one section 12 used to make and a better one. ESO measured this
        /// effect by spatial autocorrelation and published the numbers (Downing et al. 2006), so
        /// the claim that no generic published values exist was wrong; but they measured it on an
        /// e2v CCD44-82, and the same paper reports nothing for the MIT/LL CCID-20 that FORS2 uses.
        /// Core.BrighterFatter therefore carries the physics, validated against the one device it
        /// is published for, and waits for a number.
        ///
        /// Two directions rather than one because the device is not isotropic: a pixel is bounded
        /// in x by channel stops and in y by the electric fields of the clock lines, and ESO
        /// measure 1.4% against 2.2% on the same chip for exactly that reason.
        /// </summary>
        public double BrighterFatterHorizontalCorrelation = double.NaN;
        public double BrighterFatterVerticalCorrelation = double.NaN;
        public double BrighterFatterReferenceSignalElectrons = double.NaN;

        /// <summary>True when this detector has a published brighter-fatter amplitude. False for every instrument currently on the roster.</summary>
        public bool HasBrighterFatter =>
            !double.IsNaN(BrighterFatterHorizontalCorrelation)
            && !double.IsNaN(BrighterFatterReferenceSignalElectrons)
            && BrighterFatterReferenceSignalElectrons > 0.0;

        /// <summary>
        /// Residual surface image: the charge this detector holds at the silicon-oxide interface
        /// after a saturated exposure, and the timescale on which it comes back. NaN means not
        /// published, which is every instrument on this roster whose detector has not been tested.
        ///
        /// The threshold is a fraction of full well, because every published measurement reports
        /// residual images following saturated sources and none below that. The trap density is per
        /// pixel and bounds what an arbitrarily overexposed pixel can hold. The two decay constants
        /// and the share between them are the two-exponential fit that arXiv:2502.05418 measures on
        /// an e2v CCD250, carried as two separate populations so the release stays exact under any
        /// sequence of exposure times. See Core.DetectorPersistence for all of it.
        /// </summary>
        public double PersistenceThresholdFractionOfFullWell = double.NaN;
        public double PersistenceTrappedFraction = double.NaN;
        public double PersistenceTrapDensityElectrons = double.NaN;
        public double PersistenceFastDecaySeconds = double.NaN;
        public double PersistenceSlowDecaySeconds = double.NaN;
        public double PersistenceFastShare = double.NaN;

        /// <summary>
        /// True when this detector has been TESTED for image persistence and found not to show it,
        /// which is a different fact from an unmeasured one and is why it is not another NaN.
        ///
        /// Set on WFC3/UVIS alone: ISR WFC3 2005-10 took dark images following highly saturated PSF
        /// images specifically to look for it and found no significant image persistence,
        /// consistent with previous ambient testing. The report states the null qualitatively and
        /// gives no numerical upper limit, so none is recorded. HST's much-cited persistence is
        /// WFC3/IR's HgCdTe array, a different technology on a different channel, and does not
        /// transfer to these CCDs.
        /// </summary>
        public bool PersistenceMeasuredAbsent;

        /// <summary>
        /// True when this CCD has a published residual-surface-image amplitude to simulate. False
        /// for every CCD on the roster: measured absent on WFC3/UVIS, and unpublished for the
        /// IMX492, for FORS2's CCID-20 and for ZIMPOL's CCDs.
        ///
        /// This gates the CCD model alone (Core.DetectorPersistence). The infrared array's
        /// persistence is a different measured law with its own gate, HasHgCdTePersistence below.
        /// </summary>
        public bool HasPersistence =>
            !PersistenceMeasuredAbsent
            && !double.IsNaN(PersistenceTrappedFraction)
            && !double.IsNaN(PersistenceThresholdFractionOfFullWell)
            && !double.IsNaN(PersistenceTrapDensityElectrons)
            && PersistenceTrapDensityElectrons > 0.0;

        /// <summary>
        /// What kind of detector this is, which decides WHICH CHAIN the frame goes through rather
        /// than merely which numbers it carries.
        ///
        /// A CCD clocks charge to one output, so it has charge-transfer inefficiency and it blooms
        /// along the column when a well overflows. An HgCdTe array reads every pixel where it sits
        /// through the pixel's own amplifier, so it has neither - the WFC3 handbook states both
        /// absences outright, "no charge bleeding at saturation" and "minimal long-term on-orbit CTE
        /// degradation" - and instead has interpixel capacitance, count-rate non-linearity, a read
        /// noise set by how far up the ramp it was sampled, and persistence that follows a power law
        /// rather than a sum of exponentials. See Core.InfraredArray and Core.HgCdTePersistence.
        /// </summary>
        public DetectorTechnology Technology = DetectorTechnology.Ccd;

        /// <summary>
        /// The array's interpixel-capacitance kernel, or null where the detector has none published
        /// or is not an array at all. Applied at readout, because IPC is capacitive crosstalk
        /// between sense nodes rather than anything that moves charge (Core.InfraredArray).
        /// </summary>
        public double[,] InterpixelCapacitanceKernel;

        /// <summary>
        /// Count-rate non-linearity: the fractional loss of measured flux per decade of true flux
        /// below the level the photometric zero point was anchored at, and that anchor in electrons
        /// per second. NaN where unmeasured, which is every detector here but WFC3/IR.
        ///
        /// The SLOPE is measured to sub-percent accuracy (ISR 2019-01). The ANCHOR is a convention
        /// rather than a measurement and is declared in section 12 as this chain's one unpinned
        /// constant; it sets where the correction is zero and nothing about the effect's shape.
        /// </summary>
        public double CountRateNonLinearityPerDex = double.NaN;
        public double CountRateNonLinearityReferenceElectronsPerSecond = double.NaN;

        /// <summary>
        /// MULTIACCUM ramp sampling: how many non-destructive reads the ramp is fitted to, and the
        /// two published read-noise anchors that number is interpolated between. NSAMP on WFC3/IR
        /// runs 1 to 15.
        /// </summary>
        public int RampReads;
        public double RampReadNoiseAtFewReadsElectrons = double.NaN;
        public int RampFewReads;
        public double RampReadNoiseAtManyReadsElectrons = double.NaN;
        public int RampManyReads;

        /// <summary>
        /// True when this detector carries the published HgCdTe persistence model, whose parameters
        /// are Core.HgCdTePersistence's transcription of WFC3 ISR 2015-15 Table 2.
        ///
        /// Unlike every other persistence gate on this roster, this one is TRUE on the instrument
        /// that has it: WFC3/IR's persistence is measured, fitted, and published with its own
        /// error budget, so the effect runs rather than waiting for a number.
        /// </summary>
        public bool HasHgCdTePersistence;

        /// <summary>
        /// Thickness of the detector's silicon in microns, for instruments whose is published, or
        /// NaN. Sets the fringe period and with it everything about how this detector corrugates a
        /// red sky (see Core.Fringing).
        ///
        /// NaN means no fringing is modelled, which for this roster is every instrument but FORS2:
        /// the ZWO camera's IMX492 is a front-side-illuminated-style stacked CMOS whose layer
        /// structure Sony does not publish, and Schmid et al. give no thickness for ZIMPOL's CCDs.
        /// </summary>
        public double DetectorSiliconThicknessMicrons = double.NaN;

        /// <summary>
        /// Peak-to-peak variation of that thickness across the array, as a fraction, and the
        /// spatial scale it varies on in pixels.
        ///
        /// The AMPLITUDE and the SCALE are measured; the particular realisation is not, and is
        /// drawn from the sensor's serial seed exactly as the photo-response and defect maps are.
        /// Walsh et al. (2008) give the scale directly, at "around 40 pixels peak-to-peak for the
        /// closest spaced fringes", and the amplitude follows from it: one fringe is one turn of
        /// phase, which is a thickness change of lambda/(2n), or 0.33% of a 40 um layer at 950 nm.
        /// NaN on any instrument with no published thickness.
        /// </summary>
        public double DetectorThicknessVariationFraction = double.NaN;
        public double DetectorThicknessVariationScalePixels = double.NaN;

        /// <summary>True when this instrument carries a coronagraph at all.</summary>
        public bool HasCoronagraph => CoronagraphMasks != null && CoronagraphMasks.Length > 0;

        /// <summary>
        /// Diameter of the illuminated/corrected image circle at the focal plane, in millimetres,
        /// as the manufacturer publishes it. NaN means not published. Used to cut off illumination
        /// outside it; on this roster every published circle is larger than the sensor's diagonal,
        /// so it cuts nothing and exists to record that fact rather than to change a frame.
        /// </summary>
        public double ImageCircleMillimetres = double.NaN;

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
            // ESTIMATED, NOT PUBLISHED. This is the one number in this file that no source backs,
            // and it is marked so deliberately. PlaneWave publishes no vane width; the telescope
            // plainly has a four-vane spider, and leaving it at zero drew no spikes at all, which
            // is a worse error than a bracketed estimate. The derivation, including the two
            // methods that were tried and rejected, is TECHNICAL_REFERENCE section 7.113.
            //
            // Short version: stiffness demands only 0.17 mm here, so the blade is set by buckling
            // and handling, not by optics or by the secondary's weight. 1.5 mm is ordinary
            // commercial blade stock, ~9x the stiffness floor, and it obscures 0.54 % of the pupil
            // against 1.1-2.1 % for the two professional spiders in this file. Spike brightness
            // goes as the vane AREA SQUARED, so treat the resulting spikes as good to a factor of
            // a few, not as photometry.
            SpiderVaneCount = 4,
            SpiderVaneWidthMeters = 0.0015,
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
            // ZWO's own ASI294 Manual (EN, V2.2, Feb 2022), section 3, mono row: "Full well 66.4k e
            // (mono)". The product page's 66,387 e- is the same number unrounded.
            FullWellElectrons = 66400.0,

            // CORRECTED, and the correction is the point rather than the value. The figure here was
            // 1.2 e-, which is real and published but belongs to a DIFFERENT OPERATING POINT from
            // the full well and conversion factor beside it, so the three could not hold at once.
            //
            // The same manual gives the read noise as a RANGE, "1.2-8e (mono)", because this camera
            // has two conversion-gain configurations. Its HCG mode engages at ZWO gain 120 and
            // switches the sense node; there the read noise reaches 1.2 e- and, as the manual says,
            // "the dynamic range can still be close to 14bit", which for a 14-bit converter puts
            // the full well near 1.2 x 16383 = 19,700 e- and the conversion factor near 1.2 e-/ADU.
            // The 66.4k well and the 4.05 e-/ADU derived from it are the LOW-gain point.
            //
            // Pairing the high-gain read noise with the low-gain well overstated this camera's
            // dynamic range by a factor of four and made its bias frames quantisation-limited, at
            // 1.2 e- against a converter step of 4.05 e-; tools/calibration-tests found exactly
            // that, recovering 0.413 ADU of noise where 1.2 e- is 0.298. Taking the read noise that
            // belongs WITH the well and the converter already here removes the inconsistency, and
            // leaves the read noise nearly two counts wide, which is what a correctly matched chain
            // looks like.
            //
            // The consequence is stated rather than hidden: this entry now models the low-gain
            // operating point throughout, and the HCG point is a real capability of the real camera
            // that this pipeline does not offer (section 12).
            ReadNoiseElectrons = 8.0,               // ASI294 Manual V2.2 section 3, mono, low-gain end of "1.2-8e"
            DarkCurrentElectronsPerSecond = 0.0022, // ZWO ASI294MM Pro, cooled to -20C

            AdcBits = 14,                       // ZWO's published figure for this readout mode
            // Derived, not invented: at gain 1 the full well fills the ADC range exactly, so
            // K = FullWell / (2^14 - 1) = 66400 / 16383 = 4.053 e-/ADU. Both inputs are ZWO's
            // own published numbers; ZWO does not tabulate K itself for this camera.
            ElectronsPerAduAtUnityGain = 66400.0 / 16383.0,
            // Sea-level cosmic-ray (muon) flux, ~1 per cm^2 per minute for a horizontal
            // detector, the standard figure (Particle Data Group, Cosmic Rays review). This
            // site is at 650m/1712m rather than sea level and the flux climbs with altitude, so
            // this is a floor rather than a measurement; unlike FORS2 no rate is published for
            // this camera at its site.
            CosmicRayEventsPerMinutePerCm2 = 1.0,

            // The ASI294MM Pro's format is the IMX492 summed 2x2 in the sensor: 4144x2822 at
            // 4.63um against the sensor's own 8288x5644 at 2.315um. Not an inference, and it
            // checks out arithmetically at both ends: 4144*2 = 8288 exactly, 4.63/2 = 2.315
            // exactly, and ZWO's published 66000 e- full well is four times the 15655 e- LUCID
            // measure for one pixel of the same silicon, as summing four wells requires.
            SensorNativePixelsPerSide = 2,

            // EMVA 1288 measurements of this sensor, from LUCID Vision Labs' published report for
            // the Atlas10 ATX470S-M, the monochrome IMX492 camera (thinklucid.com, "EMVA 1288
            // Data"): PRNU 0.62% and DSNU 0.97 e-.
            //
            // WHY A MACHINE-VISION CAMERA'S REPORT IS THE RIGHT SOURCE FOR AN ASTRONOMY CAMERA,
            // and where it stops being one. Both quantities are properties of the SILICON: PRNU is
            // the spread of pixel quantum efficiency and fill factor, DSNU the spread of the
            // per-pixel readout offset, and the IMX492 in LUCID's camera is the IMX492 in ZWO's.
            // What is NOT transferable is anything the surrounding electronics set, and those are
            // exactly the figures this entry already takes from ZWO: read noise, conversion gain,
            // cooled dark current. LUCID's 7.83 e- temporal dark noise against ZWO's 1.2 e- read
            // noise is the size of that difference, and the reason the two sources are used for
            // different lines rather than one of them for all.
            //
            // Both are quoted for ONE pixel of the sensor, which SensorNativePixelsPerSide above
            // converts to the 2x2-summed pixel this catalogue's format describes: 0.31% of response
            // spread and 1.94 e- of offset spread.
            PhotoResponseNonUniformity = 0.0062,
            OffsetFixedPatternElectrons = 0.97,

            // Not published: ZWO quotes no linearity figure for this camera, and the EMVA report
            // above gives linearity error as a plot rather than a number. Declared rather than
            // guessed (section 12), so this camera's frames carry no non-linearity at all.
            LinearityDeviationAtFullWell = double.NaN,

            // PlaneWave publishes no illuminated-field diameter for the RC20, stating only that the
            // design is for on-axis work and that "wide-field imaging is not a concern". Left
            // unpublished rather than inferred from the back focus.
            ImageCircleMillimetres = double.NaN,

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
        /// unchanged. This is not a shortcut but how amateur astrophotography actually works
        /// (one camera, swapped between tubes), and it isolates this entry's difference to the
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
            // ZWO's own ASI294 Manual (EN, V2.2, Feb 2022), section 3, mono row: "Full well 66.4k e
            // (mono)". The product page's 66,387 e- is the same number unrounded.
            FullWellElectrons = 66400.0,

            // CORRECTED, and the correction is the point rather than the value. The figure here was
            // 1.2 e-, which is real and published but belongs to a DIFFERENT OPERATING POINT from
            // the full well and conversion factor beside it, so the three could not hold at once.
            //
            // The same manual gives the read noise as a RANGE, "1.2-8e (mono)", because this camera
            // has two conversion-gain configurations. Its HCG mode engages at ZWO gain 120 and
            // switches the sense node; there the read noise reaches 1.2 e- and, as the manual says,
            // "the dynamic range can still be close to 14bit", which for a 14-bit converter puts
            // the full well near 1.2 x 16383 = 19,700 e- and the conversion factor near 1.2 e-/ADU.
            // The 66.4k well and the 4.05 e-/ADU derived from it are the LOW-gain point.
            //
            // Pairing the high-gain read noise with the low-gain well overstated this camera's
            // dynamic range by a factor of four and made its bias frames quantisation-limited, at
            // 1.2 e- against a converter step of 4.05 e-; tools/calibration-tests found exactly
            // that, recovering 0.413 ADU of noise where 1.2 e- is 0.298. Taking the read noise that
            // belongs WITH the well and the converter already here removes the inconsistency, and
            // leaves the read noise nearly two counts wide, which is what a correctly matched chain
            // looks like.
            //
            // The consequence is stated rather than hidden: this entry now models the low-gain
            // operating point throughout, and the HCG point is a real capability of the real camera
            // that this pipeline does not offer (section 12).
            ReadNoiseElectrons = 8.0,               // ASI294 Manual V2.2 section 3, mono, low-gain end of "1.2-8e"
            DarkCurrentElectronsPerSecond = 0.0022, // ZWO ASI294MM Pro, cooled to -20C

            AdcBits = 14,                       // ZWO's published figure for this readout mode
            // Derived, not invented: at gain 1 the full well fills the ADC range exactly, so
            // K = FullWell / (2^14 - 1) = 66400 / 16383 = 4.053 e-/ADU. Both inputs are ZWO's
            // own published numbers; ZWO does not tabulate K itself for this camera.
            ElectronsPerAduAtUnityGain = 66400.0 / 16383.0,
            // Sea-level cosmic-ray (muon) flux, ~1 per cm^2 per minute for a horizontal
            // detector, the standard figure (Particle Data Group, Cosmic Rays review). This
            // site is at 650m/1712m rather than sea level and the flux climbs with altitude, so
            // this is a floor rather than a measurement; unlike FORS2 no rate is published for
            // this camera at its site.
            CosmicRayEventsPerMinutePerCm2 = 1.0,

            // Same camera as the RC20, so the same sensor figures for the same reasons; see that
            // entry for the sourcing of all four.
            SensorNativePixelsPerSide = 2,
            PhotoResponseNonUniformity = 0.0062,
            OffsetFixedPatternElectrons = 0.97,
            LinearityDeviationAtFullWell = double.NaN,

            // William Optics publish a 45mm flat corrected image circle for the Gen III. The
            // sensor's diagonal is 23.2mm, so the whole frame sits inside the inner half of it and
            // this stop removes nothing; it is carried because a published illuminated field that
            // is comfortably larger than the detector is a fact about the instrument worth
            // recording, and because the same field would cut a full-frame sensor.
            ImageCircleMillimetres = 45.0,

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
            // ESTIMATED, NOT PUBLISHED, exactly as for the RC20 and by the same method; see
            // TECHNICAL_REFERENCE section 7.113. The heavier 47 cm secondary raises the stiffness
            // floor to 0.79 mm, so 2.5 mm is a smaller margin over it than the RC20's, which is
            // the right direction: a bigger secondary needs a stouter blade. It obscures 0.43 %
            // of the pupil. Same caveat: the spikes are good to a factor of a few.
            SpiderVaneCount = 4,
            SpiderVaneWidthMeters = 0.0025,
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
            // ZWO's own ASI294 Manual (EN, V2.2, Feb 2022), section 3, mono row: "Full well 66.4k e
            // (mono)". The product page's 66,387 e- is the same number unrounded.
            FullWellElectrons = 66400.0,

            // CORRECTED, and the correction is the point rather than the value. The figure here was
            // 1.2 e-, which is real and published but belongs to a DIFFERENT OPERATING POINT from
            // the full well and conversion factor beside it, so the three could not hold at once.
            //
            // The same manual gives the read noise as a RANGE, "1.2-8e (mono)", because this camera
            // has two conversion-gain configurations. Its HCG mode engages at ZWO gain 120 and
            // switches the sense node; there the read noise reaches 1.2 e- and, as the manual says,
            // "the dynamic range can still be close to 14bit", which for a 14-bit converter puts
            // the full well near 1.2 x 16383 = 19,700 e- and the conversion factor near 1.2 e-/ADU.
            // The 66.4k well and the 4.05 e-/ADU derived from it are the LOW-gain point.
            //
            // Pairing the high-gain read noise with the low-gain well overstated this camera's
            // dynamic range by a factor of four and made its bias frames quantisation-limited, at
            // 1.2 e- against a converter step of 4.05 e-; tools/calibration-tests found exactly
            // that, recovering 0.413 ADU of noise where 1.2 e- is 0.298. Taking the read noise that
            // belongs WITH the well and the converter already here removes the inconsistency, and
            // leaves the read noise nearly two counts wide, which is what a correctly matched chain
            // looks like.
            //
            // The consequence is stated rather than hidden: this entry now models the low-gain
            // operating point throughout, and the HCG point is a real capability of the real camera
            // that this pipeline does not offer (section 12).
            ReadNoiseElectrons = 8.0,               // ASI294 Manual V2.2 section 3, mono, low-gain end of "1.2-8e"
            DarkCurrentElectronsPerSecond = 0.0022, // ZWO ASI294MM Pro, cooled to -20C

            AdcBits = 14,                       // ZWO's published figure for this readout mode
            // Derived, not invented: at gain 1 the full well fills the ADC range exactly, so
            // K = FullWell / (2^14 - 1) = 66400 / 16383 = 4.053 e-/ADU. Both inputs are ZWO's
            // own published numbers; ZWO does not tabulate K itself for this camera.
            ElectronsPerAduAtUnityGain = 66400.0 / 16383.0,
            // Sea-level cosmic-ray (muon) flux, ~1 per cm^2 per minute for a horizontal
            // detector, the standard figure (Particle Data Group, Cosmic Rays review). This
            // site is at 650m/1712m rather than sea level and the flux climbs with altitude, so
            // this is a floor rather than a measurement; unlike FORS2 no rate is published for
            // this camera at its site.
            CosmicRayEventsPerMinutePerCm2 = 1.0,

            // Same camera as the RC20, so the same sensor figures for the same reasons; see that
            // entry for the sourcing of all four.
            SensorNativePixelsPerSide = 2,
            PhotoResponseNonUniformity = 0.0062,
            OffsetFixedPatternElectrons = 0.97,
            LinearityDeviationAtFullWell = double.NaN,

            // PlaneWave publish a "perfectly flat field across a 100mm image circle" for the
            // CDK1000, four times the sensor's 23.2mm diagonal.
            ImageCircleMillimetres = 100.0,

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
            // CORRECTED AGAIN, and this time with both revisions in hand. Table 2.8 of the CURRENT
            // manual (Issue 103, 30/08/18) gives 2.7 e- for MIT chip 1 at low gain / 200 kHz and
            // 3.6 e- for chip 2. The value previously here, 3.8, is in neither revision available:
            // Issue 82.1 (27/02/2008), which is the older FORS User Manual, gives 4.1 e- for the
            // same chip and mode in its own Table 2.9. So the detector's measured read noise fell
            // from 4.1 to 2.7 over the decade between the two documents, which is what a controller
            // upgrade does and is not a contradiction between them; 3.8 sits between the two and
            // matches neither.
            //
            // The current manual is the authority, so 2.7 it is.
            ReadNoiseElectrons = 2.7,                       // Issue 103, Table 2.8, MIT chip 1, 200 kHz low gain
            DarkCurrentElectronsPerSecond = 2.1 / 3600.0,   // Table 2.9, MIT chip 1, -120C

            AdcBits = 16,
            ElectronsPerAduAtUnityGain = 1.25,              // Table 2.8, K for MIT chip 1, 200 kHz
            // Table 2.9, measured on the MIT mosaic at Paranal (2635m). Nearly eight times the
            // sea-level muon flux, which is what 2.6 km of altitude does to the cosmic-ray rate,
            // and a good illustration of why this belongs per instrument rather than as one
            // global constant.
            CosmicRayEventsPerMinutePerCm2 = 7.7,

            // Table 2.9 again, MIT chip 1 at low gain, the 200 kHz imaging mode this entry models
            // throughout. See Core.DetectorLinearity for why the manual's "% RMS" heading is read
            // as a relative deviation at full well rather than as an RMS residual, and for what the
            // manual does not state about its sign.
            LinearityDeviationAtFullWell = 0.018,

            // ESO: the field of view "is restricted by the MOS unit in the focal plane of the unit
            // telescope to about 6.8x6.8 arcminutes" for the standard-resolution collimator
            // (manual section 2.2). This is the one instrument on the roster whose detector is
            // BIGGER than its illuminated field: 4096 unbinned pixels at 0.125 arcsec is 8.5
            // arcminutes, so the stop lands well inside the chip and roughly a third of the frame's
            // area sees no sky at all. The exact pattern is published as a figure (Appendix G)
            // rather than as a formula, and the mosaic's two chips are mounted 33 arcsec off the
            // optical axis, so what is modelled is the manual's own figure read literally: a square
            // stop, centred (see Core.FocalPlaneIllumination and section 12).
            FieldStopSquareArcmin = 6.8,

            // The MIT/LL CCID-20's silicon, from ESO's own SPIE paper on these very devices:
            // "The MIT/LL CCID-20 is a 40 um thick high resistivity deep depletion CCD" (Downing,
            // Baade, Sinclaire, Deiries and Christen 2006). Independently confirmed by the fringe
            // period: Walsh et al. (2008) measure 2.9 nm near 950 nm, which implies 43.4 um through
            // silicon's own dispersion, an 8.5% agreement between a spectroscopic measurement and a
            // fabrication figure that were never compared before (see Core.Fringing).
            DetectorSiliconThicknessMicrons = 40.0,

            // One fringe is one turn of phase, i.e. a thickness change of lambda/(2n) = 132 nm at
            // 950 nm, which on a 40 um layer is 0.33%; Walsh et al. put the closest fringes about
            // 40 pixels apart. Amplitude and scale are therefore both measured, and only the
            // particular map is drawn, from the same serial seed the defect and flat maps use.
            DetectorThicknessVariationFraction = 0.0033,
            DetectorThicknessVariationScalePixels = 40.0,

            // Not published for the MIT/LL CCID-20: neither the user manual nor ESO's QC1 pages
            // give a PRNU or a fixed-pattern figure for this mosaic, so both are left unpublished
            // rather than borrowed from another device. FORS2's flat field is therefore the field
            // stop and the cosine-fourth term alone, which for this instrument is where nearly all
            // of it is anyway.
            PhotoResponseNonUniformity = double.NaN,
            OffsetFixedPatternElectrons = double.NaN,

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

            // Not published. Schmid et al. 2018 is a detailed instrument paper with a full detector
            // table, and it gives no PRNU, no fixed-pattern figure and no linearity coefficient; it
            // refers to detector non-linearity only qualitatively, as one of the effects the
            // polarization compensator plate exists to keep out of the polarimetric signal. All
            // three are therefore left unpublished rather than borrowed.
            //
            // It costs this instrument less than it would cost any other on the roster, and for a
            // reason the same paper states: ZIMPOL's two polarimetric beams land on the SAME pixels,
            // so the flat-fielding factors and bias levels divide out of the differential signal
            // exactly. The instrument is built so that what is missing here matters least.
            PhotoResponseNonUniformity = double.NaN,
            OffsetFixedPatternElectrons = double.NaN,
            LinearityDeviationAtFullWell = double.NaN,

            // What makes this instrument SPHERE rather than a telescope with a good Strehl ratio.
            // The five classical Lyot masks of the visual coronagraph, their measured attenuations,
            // and the pupil stop that does the actual suppressing; all from Schmid et al. (2018)
            // Tables 8 and 9, and all in Core.Coronagraph rather than restated here.
            CoronagraphMasks = Coronagraph.VisualMasks,
            CoronagraphLyotStop = Coronagraph.StopB1_2,

            // SAXO's high-order deformable mirror, 41x41 actuators (Fusco et al. 2006; Beuzit
            // et al. 2019). Its half-width in resolution elements is the AO control radius, and
            // that number is checkable rather than decorative: 20.5 lambda/D at 626 nm on 8.2 m is
            // 323 mas, and Schmid et al. report the observed speckle ring at 0.3 to 0.4 arcsec.
            AdaptiveOpticsActuatorsAcrossPupil = 41,

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

        /// <summary>
        /// The Hubble Space Telescope's Optical Telescope Assembly with Wide Field Camera 3's
        /// UVIS channel: the first instrument in this roster that observes from orbit.
        ///
        /// WHAT IT IS NOT. It is not simply the biggest telescope here, and presenting it that
        /// way would be a lie the numbers immediately expose. At 2.4 m it has less than a
        /// twelfth of the VLT's collecting area, and its delivered 0.067 arcsec core at 500 nm
        /// is nearly three times COARSER than SPHERE's adaptive-optics 25 mas. On the two axes
        /// this catalogue has been climbing so far, aperture and resolution, HST loses to
        /// instruments already in it.
        ///
        /// WHAT IT IS. Three things no ground instrument in this roster can offer at all:
        ///
        ///   * The near-ultraviolet. WFC3/UVIS works from 200 nm. The ground does not: ozone's
        ///     Hartley band closes the atmosphere below about 320 nm outright, and no mirror
        ///     size, site altitude or adaptive optics buys any of it back, because the ozone is
        ///     in the stratosphere and a mountain is underneath it. That is why no ground
        ///     instrument in this roster carries a filter below 420 nm and this one reaches to
        ///     200: the difference is expressed as the instruments' real filter sets rather than
        ///     asserted here. The extinction model itself has no ozone term (its Chappuis-band
        ///     residual is folded into the aerosol fit; see AtmosphericImagingNoise), so if a
        ///     ground UV filter is ever added to this catalogue, one will have to be added with
        ///     it. Recorded in section 12.
        ///   * A deterministic point-spread function. Every frame has the same PSF, because
        ///     there is no atmosphere to vary. That is what the published FWHM table below
        ///     MEANS: not a typical value with a distribution around it, but the width the
        ///     instrument delivers.
        ///   * A sky roughly 1.6 magnitudes darker, because the airglow that sets a dark
        ///     ground site's 21.7 mag/arcsec^2 floor is a property of the atmosphere and is
        ///     simply absent. What is left is zodiacal light, and above the atmosphere it is
        ///     nearly the entire background (see ZodiacalLight).
        ///
        /// Optics: HST Primer (Cycle 34), "Optical Performance, Guiding Performance, and
        /// Observing Efficiency": Ritchey-Chretien Cassegrain, 2.4 m aperture, f/24, plate scale
        /// 3.58 arcsec/mm on axis, PSF FWHM 0.043 arcsec at 5000 A, encircled energy within
        /// 0.1 arcsec 87 per cent at 5000 A. Note that the focal length below is not independently
        /// asserted: f/24 on 2.4 m gives 57.6 m, and 206265/57600 mm gives 3.581 arcsec/mm, which
        /// is the published plate scale. The harness checks that identity rather than trusting
        /// the transcription.
        ///
        /// Pupil: Tiny Tim's own wfc3_uvis1.pup table (Krist &amp; Hook, "The Tiny Tim User's
        /// Guide"; the source distribution at github.com/spacetelescope/tinytim), which gives
        /// "0.330 = OTA Secondary Mirror Radius", "0.022 = OTA Spider Width", and three mirror
        /// pads of radius 0.065 at (0.8921, 0.0000), (-0.4615, 0.7555) and (-0.4564, -0.7606),
        /// all in units of the pupil radius. HST's spider has four vanes.
        ///
        /// Detector: WFC3 Instrument Handbook (Cycle 24) Table 5.1, with the per-amplifier
        /// figures from Tables 5.3 and 5.4; see the field comments for which number is which.
        /// </summary>
        public static readonly VisualTelescopeSpec HubbleWfc3Uvis = new VisualTelescopeSpec
        {
            Name = "Hubble Space Telescope (OTA)",
            CameraName = "WFC3/UVIS",
            SiteName = "Low Earth orbit",
            // WFC3 IHB Table 5.1, "Operating Temperature": -83 C for the UVIS CCDs.
            DetectorTemperatureCelsius = -83.0,
            // No adjustable cooler: the detector runs at a fixed setpoint held by the
            // instrument's own thermoelectric coolers and radiator, and no observer is offered a
            // dial. Same treatment as FORS2 and SPHERE.
            CoolerDeltaBelowAmbientC = 0.0,

            ApertureMeters = 2.4,

            // THE EFFECTIVE FOCAL LENGTH AT WFC3/UVIS, NOT THE OTA's OWN. The telescope is f/24,
            // giving 57.6 m, and using that here would be wrong by a third: WFC3 is not at the
            // OTA's Cassegrain focus, it is behind its own relay optics, and the instrument's
            // published plate scale is what fixes the focal length that actually forms the image.
            //
            // So it is derived from two published numbers rather than asserted:
            //     f = 206265 * pixel_size / plate_scale = 206265 * 15e-6 / 0.0396 = 78.13 m
            // which is f/32.6. Both inputs are the WFC3 Instrument Handbook's own (Table 5.1 for
            // the 15 um pixel, the "UVIS Plate Scale" section for the 0.0396 arcsec/pixel of
            // UVIS1's x axis), and tools/spacecraft-tests asserts that the plate scale this
            // pipeline computes from it comes back out at 0.0396.
            //
            // 0.0396 rather than Table 5.1's rounded 0.040, and rather than an average over both
            // chips: the handbook gives 0.0396 and 0.0393 for UVIS1's two axes and 0.0400 and
            // 0.0398 for UVIS2's. That anisotropy is real, WFC3/UVIS's field is rhomboidal
            // rather than square because of it, and this pipeline carries ONE plate scale and
            // cannot express it. Recorded in section 12; the figure used is UVIS1's, the
            // aperture most programmes are placed on.
            FocalLengthMeters = 206265.0 * 15.0e-6 / 0.0396,
            BarlowFactor = 1.0,          // a space telescope carries the instruments it launched with
            SecondaryObstructionFraction = 0.330,
            SpiderVaneCount = 4,
            // 0.022 of the pupil RADIUS, and the pupil radius is 1.2 m: 0.0264 m. This is the one
            // instrument in the roster whose vane width is published, which is why it is the one
            // whose diffraction spikes are computed rather than declared unmodelled.
            SpiderVaneWidthMeters = 0.022 * 1.2,
            PrimaryMirrorPads = new[]
            {
                new PupilPad(0.8921,  0.0000, 0.065),
                new PupilPad(-0.4615, 0.7555, 0.065),
                new PupilPad(-0.4564, -0.7606, 0.065),
            },

            // Two mirrors in the OTA, and WFC3 adds its own pick-off mirror and channel-select
            // optics. Their combined throughput is not carried as a reflectivity product here,
            // because STScI publishes the whole chain measured end to end instead: the handbook's
            // throughput curves "include the throughput of the OTA, all of the optical elements"
            // and the QE. Multiplying an assumed 0.87^N on top of a measured system throughput
            // would double-count it. MirrorCount is therefore 0 and the loss lives in the
            // measured QE figures below, which is where the measurement put it.
            MirrorCount = 0,
            RelayOpticsTransmission = 1.0,

            // No atmosphere: no site altitude, no seeing, no dispersion corrector, and no
            // adaptive optics to correct a turbulence that is not there. ZenithSeeingFwhmArcsec
            // is 0 for the one instrument in this roster for which zero is the physically correct
            // value, and the field's own comment says so.
            SiteAltitudeMeters = 0.0,
            ZenithSeeingFwhmArcsec = 0.0,
            HasAtmosphericDispersionCorrector = false,
            AdaptiveOpticsFwhmArcsec = 0.0,

            // WFC3 IHB Table 5.1: "2 butted 2051 x 4096, 31-pixel gap (1.2")", 15 um pixels.
            // The imaging pipeline works on ONE rectangular frame, so the sensor carried here is
            // one 4096 x 4102 chip: the two CCDs butted along their long edges, 2 x 2051 rows,
            // WITHOUT the 31-pixel gap, which cannot be represented as a rectangle. The gap is a
            // real feature of a real WFC3 frame and its absence is recorded in section 12. The
            // downlink volume, where the gap does not matter, uses the true pixel count (see
            // SpacePlatformSpec.FullFramePixels).
            NativeSensorWidthPx = 4096,
            NativeSensorHeightPx = 4102,
            NativePixelSizeMeters = 15.0e-6,

            // WFC3 IHB Table 5.1: "50-59% @ 250 nm, 68-69% @ 600 nm, 47-52% @ 800 nm". Three
            // published points, so this is one of the two instruments in the roster with a real
            // QE CURVE rather than a peak held flat. The midpoint of each published range is
            // used, and the curve is carried only over the detector's own stated 200-1000 nm
            // range. The 250 nm figure is the handbook's own, excluding multiple-electron events
            // as its footnote 1 specifies, which is the conservative reading.
            QuantumEfficiency = 0.685,
            QuantumEfficiencyCurve = new SpectralCurve(
                new[] { 200.0, 250.0, 600.0, 800.0, 1000.0 },
                new[] { 0.400, 0.545, 0.685, 0.495, 0.150 }),

            // WFC3 IHB Table 5.1: full well "63,000-72,000 e-"; Sect. 5.4.6 gives the maximum as
            // "~72500 e-". The ETC's own working value is used, because it is the one STScI
            // computes saturation against: "the ETC uses a CCD full-well value of 63,000 e-".
            FullWellElectrons = 63000.0,
            // Table 5.1 quotes "3.1-3.2 e-"; Table 5.4's per-amplifier unbinned means are 2.91,
            // 2.99, 2.90 and 3.01. The four-amplifier mean, 2.95, is used: the frame is read
            // through all four, so no single amplifier's figure describes it.
            ReadNoiseElectrons = 2.95,
            // Table 5.1: "~7 e-/hr/pixel (median, Dec. 2015)". Per second.
            DarkCurrentElectronsPerSecond = 7.0 / 3600.0,

            // The one detector on this roster tested for image persistence and found not to show
            // it. ISR WFC3 2005-10 ("WFC3 UVIS PSF Evaluation") obtained dark images following
            // highly saturated PSF images specifically to evaluate image persistence in the CCDs,
            // and found no significant image persistence, consistent with previous ambient
            // testing. Recorded as a measured absence rather than as another NaN, because the two
            // are different facts: everything else on this roster is untested, this is tested.
            //
            // NOT to be confused with HST's famous persistence, which is WFC3/IR's HgCdTe array on
            // the other channel of the same instrument. That literature is extensive and none of
            // it transfers: a CCD and an infrared array trap charge in different places for
            // different reasons.
            PersistenceMeasuredAbsent = true,

            // Table 5.1: "ADC Maximum 65,535 DN", i.e. 16 bits, and "Gain 1.55 e-/DN". Table 5.3
            // gives the four amplifiers as 1.56, 1.55, 1.58, 1.57; 1.55 is the handbook's own
            // summary figure and the only supported setting.
            AdcBits = 16,
            ElectronsPerAduAtUnityGain = 1.55,

            // Cosmic rays in orbit are not the sea-level muon flux this roster's ground
            // instruments carry: there is no atmosphere overhead. WFC3 IHB Sect. 5.4.10 gives
            // the quantity that is actually measured, "the fraction of WFC3 pixels impacted by
            // cosmic rays varies from 5% to 9% per chip during 1800 sec exposures in SAA-free
            // orbits", together with the deposition, "negligible events of less than 500 e- and
            // a median of ~1000 e-". The event RATE below is derived from that impacted-pixel
            // fraction and this pipeline's own track-length distribution rather than quoted, and
            // tools/spacecraft-tests asserts that a simulated 1800 s frame lands back inside the
            // published 5-9 per cent. See TECHNICAL_REFERENCE for the derivation.
            CosmicRayEventsPerMinutePerCm2 = 110.0,

            // HOW MUCH CHARGE ONE EVENT LEAVES, which is a separate measurement from the rate
            // above and was previously not modelled at all: every track was drawn at 0.85 of full
            // well, i.e. 53,550 e- in EVERY pixel it crossed, which is why a raw frame came back
            // covered in saturated white worms. WFC3 IHB Sect. 5.4.10 gives the real
            // distribution, measured on ACS/WFC: "negligible events of less than 500 e- and a
            // median of ~1000 e-", and quotes Miles et al. (2021) for "a typical hit corresponds
            // to ~2200 e-". The median is used, being the handbook's own primary figure, and
            // ApplyCosmicRays spreads it along the track rather than putting it in each pixel.
            //
            // Real HST frames DO show cosmic rays plainly; they are simply not saturated, and the
            // handbook's own remedy for them is combination, not shorter exposures: "at least 4-5
            // images will be needed to ensure that fewer than 100 pixels will be hit in all
            // images of the combination".
            CosmicRayElectronsPerEvent = 1000.0,

            // WFC3 IHB Sect. 6.7: UVIS exposure times run from 0.5 s to 3600 s. There is no
            // shorter setting; the shutter is a rotating disk and 0.5 s is its floor.
            MinExposureSeconds = 0.5f,
            MaxExposureSeconds = 3600.0f,
            // A real research CCD with one supported gain: the observer has no dial, exactly as
            // for FORS2. MinGain == MaxGain is this catalogue's own convention for that.
            MinGain = 1.0f,
            MaxGain = 1.0f,

            // WFC3/UVIS's real broadband filter set, from the handbook's Table 6.2 filter list:
            // F606W as the wide V, F438W as B, F547M as V, F625W as R, and F656N as H-alpha.
            // Widths are the filters' own published rectangular widths.
            LuminanceCentralWavelengthNm = 588.7,   // F606W pivot
            LuminanceBandwidthAngstrom = 2182.0,
            BlueCentralWavelengthNm = 432.6,        // F438W
            BlueBandwidthAngstrom = 618.0,
            GreenCentralWavelengthNm = 544.7,       // F547M
            GreenBandwidthAngstrom = 650.0,
            RedCentralWavelengthNm = 624.2,         // F625W
            RedBandwidthAngstrom = 1463.0,
            HAlphaCentralWavelengthNm = 656.1,      // F656N
            HAlphaBandwidthAngstrom = 18.0,

            // Not applied: STScI publishes full system throughput curves that already include
            // each filter's transmission along with the OTA and the QE, so a separate peak
            // transmission here would double-count. 1.0 by this file's convention means the
            // separate figure is not modelled, which is exactly the case.
            LuminanceFilterPeakTransmission = 1.0,
            RedFilterPeakTransmission = 1.0,
            GreenFilterPeakTransmission = 1.0,
            BlueFilterPeakTransmission = 1.0,
            HAlphaFilterPeakTransmission = 1.0,

            AvailableFilters = new[]
            {
                CameraFilter.Luminance, CameraFilter.Red, CameraFilter.Green,
                CameraFilter.Blue, CameraFilter.HAlpha,
            },

            // Field-dependent aberration is real on WFC3/UVIS and Tiny Tim carries a full
            // polynomial for it, but this pipeline's astigmatism term is a single corner
            // amplitude and cannot express a field polynomial. The delivered PSF table below
            // already carries the on-axis figure the handbook publishes, so adding a corner
            // amplitude on top would double-count what is measured. Declared in section 12.
            AstigmatismStrengthPxAtCorner = 0.0f,
            AlwaysAutoguided = true,   // an observatory does not point without its guidance system

            SpacePlatform = new SpacePlatformSpec
            {
                PlatformName = "Hubble Space Telescope",

                // HST Primer, Pointing, Orientation, and Roll Constraints: "The target-to-sun
                // angle at the time of observation must be greater than 62.5 degrees."
                SunAvoidanceAngleDeg = 62.5,
                // WFC3 IHB Sect. 7.9.5: the standard bright Earth avoidance angle is 20 degrees,
                // with a non-standard 25 available at a cost in observing time.
                BrightLimbAvoidanceAngleDeg = 20.0,
                // The dark limb carries no scattered-light constraint (SRW98 measure the
                // dark-limb background as flat), so what is left is the geometric margin the
                // guidance system needs. STScI's published figure for the dark limb is 7.6
                // degrees.
                DarkLimbAvoidanceAngleDeg = 7.6,
                MoonAvoidanceAngleDeg = 9.0,

                // HST Primer: "designed to keep telescope jitter below 0.007 arcsec rms, but the
                // current performance has jitter of 0.008 arcsec rms". The achieved figure.
                PointingJitterArcsecRms = 0.008,

                // WFC3 IHB Table 6.7, "WFC3/UVIS PSF FWHM (pre-pixelation, in units of pixels and
                // arcseconds), and sharpness, vs. wavelength", arcsec column, transcribed whole.
                // The turnover near 500 nm and the climb back into the UV are the OTA's
                // mid-frequency polishing errors, which the handbook names as the cause; this is
                // the measured consequence and it is why HST is not diffraction-limited anywhere
                // in this band.
                DeliveredPsfFwhmArcsec = new SpectralCurve(
                    new[] { 200.0, 300.0, 400.0, 500.0, 600.0, 700.0, 800.0, 900.0, 1000.0, 1100.0 },
                    new[] { 0.083, 0.075, 0.070, 0.067, 0.067, 0.070, 0.074, 0.078, 0.084, 0.089 }),

                HasApertureDoor = true,

                // HST Primer, "Pointing, Orientation, and Roll Constraints": "The slew rate of
                // HST is limited to approximately 6 degrees per minute of time." The same
                // paragraph states the consequence, "about one hour is needed to go full circle
                // in pitch, yaw, or roll", which is the same figure read the other way round and
                // is the cross-check the harness runs: 360 deg at 6 deg/min is 60 minutes exactly.
                MaxSlewRateDegPerSecond = 6.0 / 60.0,

                // HST Primer, "Orbital Visibility, Acquisition Times, and Overheads": "A normal
                // guide star acquisition, required in the first orbit of every visit, takes 6.5
                // minutes." Charged on every repoint, because this model has no notion of staying
                // within a visit; the reacquisition figure the Primer gives for later orbits of a
                // multi-orbit visit is the same 6.5 minutes in Cycle 34, so nothing is lost by it.
                GuideStarAcquisitionSeconds = 6.5 * 60.0,

                // The true readout: two 2051 x 4096 CCDs. Used for the downlink volume, where the
                // 31-pixel gap between them genuinely does not travel and the imaging pipeline's
                // rectangular approximation does not apply.
                FullFramePixels = 2L * 2051L * 4096L,
                DownlinkBitsPerPixel = 16,
            },
        };

        /// <summary>Every visual telescope available to the in-game instrument selector (the Observatory dropdown in ExoInstrumentsGUI; see InstrumentSpec.VisualTelescope), in unlock/display order.</summary>
        /// <summary>
        /// The SECOND CHANNEL OF THE SAME INSTRUMENT ON THE SAME TELESCOPE. Not a second telescope:
        /// WFC3 has a Channel Select Mechanism, and light goes to the UVIS CCDs or to the IR array,
        /// never to both at once. Everything upstream of the detector is therefore identical to
        /// HubbleWfc3Uvis above and is repeated here rather than shared, because a spec is a flat
        /// record and cross-referencing one field of it would hide which numbers are the same by
        /// physics and which merely happen to agree.
        ///
        /// WHAT IS GENUINELY THE SAME: the 2.4 m primary, the 0.330 obstruction, the four spider
        /// vanes and their width, the three primary-mirror pads, the orbit, and every avoidance
        /// angle and jitter figure on the platform.
        ///
        /// WHAT IS NOT: everything from the detector inwards, and not by degree. This is an HgCdTe
        /// photovoltaic array, not a CCD, so it has no charge transfer and no blooming, it is read
        /// non-destructively up a ramp, and it carries interpixel capacitance, count-rate
        /// non-linearity and a measured persistence law. See Core.InfraredArray,
        /// Core.HgCdTePersistence, and TECHNICAL_REFERENCE section 13.8.
        ///
        /// Sources throughout: WFC3 Instrument Handbook (IHB) chapters 5 and 7, WFC3 Data Handbook
        /// (DHB) chapters 1 and 7, and the three Instrument Science Reports named at each number.
        /// </summary>
        public static readonly VisualTelescopeSpec HubbleWfc3Ir = new VisualTelescopeSpec
        {
            // A DISTINCT Name, because Name is the key ModuleExoSpaceTelescope resolves a saved
            // telescope through, and two entries sharing one would make the second unreachable.
            // The platform is the same telescope and says so; the channel is what differs.
            Name = "Hubble Space Telescope (OTA/IR)",
            CameraName = "WFC3/IR",
            SiteName = "Low Earth orbit",

            Technology = DetectorTechnology.HgCdTeArray,

            // IHB Table 5.1 equivalent for the IR channel: nominal operating temperature 145 K,
            // held by a six-stage thermoelectric cooler. Far colder than any CCD here, which is
            // what a 1.7 um cutoff costs in dark current.
            DetectorTemperatureCelsius = 145.0 - 273.15,

            // ---- The OTA. Identical to WFC3/UVIS because it IS the same telescope. ----
            ApertureMeters = 2.4,
            // As on UVIS, and it must be stated rather than left to default to zero: MinFovDeg is
            // MaxFovDeg / BarlowFactor, so a zero here makes the narrow end of the zoom infinite
            // and Mathf.Clamp then pins every capture's field of view to it. Found by
            // tools/psf-truncation, which reported this channel's plate scale as infinite.
            BarlowFactor = 1.0,
            SecondaryObstructionFraction = 0.330,
            SpiderVaneCount = 4,
            SpiderVaneWidthMeters = 0.022 * 1.2,
            PrimaryMirrorPads = new[]
            {
                new PupilPad(0.8921,  0.0000, 0.065),
                new PupilPad(-0.4615, 0.7555, 0.065),
                new PupilPad(-0.4564, -0.7606, 0.065),
            },

            // THE EFFECTIVE FOCAL LENGTH AT WFC3/IR, from its own plate scale and its own 18 um
            // pixel, exactly as the UVIS entry derives its own from 15 um and 0.0396.
            //
            // The scale is ANISOTROPIC and measured: IHB 7.4 gives pixels "covering approximately
            // 0.135 x 0.121 arcsec", and the same section's 136 x 123 arcsec field over 1014
            // pixels reproduces both to three figures. The anisotropy is not a defect, it is the
            // IR focal plane being tilted with respect to the incoming beam, which the handbook
            // gives as 24 degrees (IHB 7.4) where the Data Handbook says 22 (DHB 1.3); that
            // disagreement between the two handbooks is recorded in section 12 and is not resolved
            // here, because neither is derivable from the other's numbers.
            //
            // This pipeline carries ONE plate scale, so one has to be chosen. The GEOMETRIC MEAN
            // is used rather than either axis, because the quantity that matters most to a frame
            // is the SOLID ANGLE per pixel - it sets the sky background, which dominates every
            // deep near-infrared exposure - and the geometric mean is the scale that preserves it
            // exactly. (The UVIS entry chose one axis instead, because there the two axes belong
            // to two different chips and picking the aperture most programmes use was the more
            // meaningful choice. Here both axes are the same chip.)
            FocalLengthMeters = 206265.0 * 18.0e-6 / System.Math.Sqrt(0.135 * 0.121),

            // Measured end to end by STScI, so nothing is multiplied on top: IHB 7.5 states that
            // "The throughput calculations include the HST OTA, WFC3 IR-channel internal
            // throughput, filter transmittance, and the QE of the IR detector." The whole of that
            // product is carried in the per-filter peak transmission below, which is why the
            // QuantumEfficiency field is 1.0 and the mirrors are not counted: putting a QE or a
            // reflectivity here as well would square a loss that was measured once.
            MirrorCount = 0,
            RelayOpticsTransmission = 1.0,
            QuantumEfficiency = 1.0,

            // No atmosphere, same as UVIS.
            SiteAltitudeMeters = 0.0,
            ZenithSeeingFwhmArcsec = 0.0,
            HasAtmosphericDispersionCorrector = false,
            AdaptiveOpticsFwhmArcsec = 0.0,

            // IHB 7.3: "HgCdTe 1024 x 1024 array, with 18 micron pixels, bonded to a silicon
            // multiplexer, with 1014 x 1014 pixels sensitive to incoming light".
            //
            // 1014, NOT 1024. The outer 5-pixel rim is REFERENCE PIXELS: light-insensitive cells
            // read out with the array so the pipeline can track bias and thermal drift. They are
            // part of the detector and not part of the image, and carrying 1024 here would hand
            // the imaging path 20 rows and columns that never see sky.
            NativeSensorWidthPx = 1014,
            NativeSensorHeightPx = 1014,
            NativePixelSizeMeters = 18.0e-6,

            // IHB 5.7: saturation limit "~78,000 electrons". Two other figures appear in the
            // literature for two other purposes and neither is wrong: DHB 7.2 gives the full well
            // as "approximately 80,000 electrons", and ISR 2015-15's Table 1 footnote uses 70,000
            // as "the nominal saturation level" when counting saturated pixels for the persistence
            // fit. The IHB's saturation figure is used here because it is the one tied to the
            // handbook's own 5% departure-from-linearity criterion; the persistence model carries
            // ISR 2015-15's 70,000 separately, against which its own parameters were measured.
            FullWellElectrons = 78000.0,

            // The FULL RAMP's effective read noise. IHB 5.7 gives a SPARS200 ramp at "~20.0 e-"
            // with 2 reads plus the zeroth and "~12.0 e-" with 15 reads plus the zeroth, and
            // correlated double sampling alone at 20.2-21.4 e-. There is no single read noise for
            // this detector: it depends on how far up the ramp it was sampled, which is what
            // RampReads and Core.InfraredArray.EffectiveReadNoiseElectrons carry. This field holds
            // the deepest ramp's value so that a caller which ignores the ramp gets the
            // configuration this instrument is flown in rather than a worst case.
            ReadNoiseElectrons = 12.0,
            RampReads = 15,
            RampReadNoiseAtFewReadsElectrons = InfraredArray.Wfc3IrReadNoiseTwoReadsElectrons,
            RampFewReads = InfraredArray.Wfc3IrTwoReads,
            RampReadNoiseAtManyReadsElectrons = InfraredArray.Wfc3IrReadNoiseFifteenReadsElectrons,
            RampManyReads = InfraredArray.Wfc3IrFifteenReads,

            // IHB 5.7: dark current mode 0.045, median 0.048, mean 0.048 e-/s/pixel. The median and
            // mean agree, so 0.048 is used. ISR 2015-15 quotes 0.046 from Hilbert & Petro (2012)
            // and notes it varies by 20-30% for reasons that are not understood; that variation is
            // a real property of this detector and is larger than the difference between the two
            // published means, which is why no attempt is made to split them.
            DarkCurrentElectronsPerSecond = 0.048,

            // DHB 7.2: "the Analog to Digital Converter (ADC) outputs a 16-bit number, allowing
            // output signal values ranging from 0 to 65535", and "only a gain setting of 2.5 e-/DN
            // is supported".
            //
            // THE MEASURED FOUR-QUADRANT MEAN IS USED, NOT THE COMMANDED 2.5. DHB 7.2's own table
            // of effective gains averaged over all epochs gives 2.28, 2.221, 2.24 and 2.265 e-/DN
            // for quadrants 1-4, monitored twice yearly. The frame is read through all four, so no
            // single quadrant's figure describes it, and their mean 2.2515 is what actually
            // converts a DN back to electrons. This is the same reasoning the UVIS entry applies to
            // its four amplifiers, with one difference worth stating: there the measured values sat
            // within 2% of the handbook's summary figure, here they sit 10% below the commanded
            // setting, so the choice changes the answer.
            AdcBits = 16,
            ElectronsPerAduAtUnityGain = (2.28 + 2.221 + 2.24 + 2.265) / 4.0,

            // Same orbit as UVIS, so the same rate; see the UVIS entry for the derivation from the
            // handbook's measured impacted-pixel fraction.
            CosmicRayEventsPerMinutePerCm2 = 110.0,

            // HOW MUCH CHARGE ONE EVENT LEAVES, which is a separate measurement from the rate
            // above and was previously not modelled at all: every track was drawn at 0.85 of full
            // well, i.e. 53,550 e- in EVERY pixel it crossed, which is why a raw frame came back
            // covered in saturated white worms. WFC3 IHB Sect. 5.4.10 gives the real
            // distribution, measured on ACS/WFC: "negligible events of less than 500 e- and a
            // median of ~1000 e-", and quotes Miles et al. (2021) for "a typical hit corresponds
            // to ~2200 e-". The median is used, being the handbook's own primary figure, and
            // ApplyCosmicRays spreads it along the track rather than putting it in each pixel.
            //
            // Real HST frames DO show cosmic rays plainly; they are simply not saturated, and the
            // handbook's own remedy for them is combination, not shorter exposures: "at least 4-5
            // images will be needed to ensure that fewer than 100 pixels will be hit in all
            // images of the combination".
            CosmicRayElectronsPerEvent = 1000.0,

            // ---- HgCdTe-specific physics, each measured and each cited ----

            // Hilbert & McCullough (2011, WFC3 ISR 2011-10) Table 2, measured on orbit from hot
            // pixels in the SPARS200 dark reference file. Anisotropic and NOT renormalised; see
            // Core.InfraredArray for why the published 0.9985 sum is left alone.
            InterpixelCapacitanceKernel = InfraredArray.Wfc3IrKernel,

            // Riess, Narayan & Calamida (2019, WFC3 ISR 2019-01): 0.75% +/- 0.06% per dex, with no
            // apparent wavelength dependence, measured across 16 astronomical magnitudes.
            CountRateNonLinearityPerDex = InfraredArray.Wfc3IrCountRateNonLinearityPerDex,

            // THE ANCHOR, and it is the one unpinned constant in this chain, declared in section 12
            // rather than dressed up. The slope above is measured to sub-percent accuracy; the flux
            // level it is measured RELATIVE to is wherever the photometric zero point was
            // established, and ISR 2019-01 states the convention without giving a number: "flux
            // zeropoints are established from standard stars which are about ten astronomical
            // magnitudes (4 dex) brighter than faint, sky-dominated targets."
            //
            // 100 e-/s is used, which is a bright-standard-star count rate on this detector and
            // sits 4 dex above the ~0.01 e-/s of a sky-dominated faint source, reproducing exactly
            // the span that sentence describes. Nothing about the effect's SHAPE depends on it; it
            // sets where the correction passes through zero.
            CountRateNonLinearityReferenceElectronsPerSecond = 100.0,

            // Long, Baggett & MacKenty (2015, WFC3 ISR 2015-15). The one instrument on this roster
            // whose persistence is measured, fitted and published with its own error budget, so the
            // effect RUNS rather than waiting for a number. Parameters in Core.HgCdTePersistence.
            HasHgCdTePersistence = true,

            // ---- Filters: IHB Table 7.2, "WFC3 IR Channel Filters and Grisms" ----
            //
            // THE FOUR WIDE FILTERS ONTO THE FOUR BROADBAND SLOTS, in wavelength order. As with the
            // UVIS entry, the slot is a position on a wheel and the real filter is what it is; the
            // pivot wavelength and width below carry the truth. Peak throughput is the INTEGRATED
            // SYSTEM throughput per IHB 7.5, which is why QuantumEfficiency above is 1.0.
            //
            // Values are in nm in the handbook and are converted to this catalogue's units:
            // central wavelength in nm, bandwidth in angstrom.
            LuminanceCentralWavelengthNm = 1153.4,      // F110W, the widest
            LuminanceBandwidthAngstrom = 4430.0,
            LuminanceFilterPeakTransmission = 0.56,
            RedCentralWavelengthNm = 1536.9,            // F160W
            RedBandwidthAngstrom = 2683.0,
            RedFilterPeakTransmission = 0.56,
            GreenCentralWavelengthNm = 1248.6,          // F125W
            GreenBandwidthAngstrom = 2845.0,
            GreenFilterPeakTransmission = 0.56,
            BlueCentralWavelengthNm = 1055.2,           // F105W, the shortest wide filter
            BlueBandwidthAngstrom = 2650.0,
            BlueFilterPeakTransmission = 0.52,

            // NO H-ALPHA SLOT, and this is a physical statement rather than an omission. H-alpha is
            // at 656 nm and this detector's filter set starts above 900 nm, so the line is not
            // merely unavailable, it is outside the channel entirely. Offering a slot labelled
            // H-alpha and putting a 1.28 um Paschen-beta filter in it would be the mislabelling
            // that section 12 already refuses for SPHERE's absent blue.
            AvailableFilters = new[]
            {
                CameraFilter.Luminance, CameraFilter.Red, CameraFilter.Green, CameraFilter.Blue,
            },

            // IHB 7.6: exposure times reachable by the MULTIACCUM sequences. RAPID reaches 2.932 s
            // at NSAMP=1 and 43.984 s at NSAMP=15; the SPARS and STEP sequences extend from there.
            MinExposureSeconds = 2.932f,
            MaxExposureSeconds = 3600.0f,

            // A hardware conversion factor, not a player control, same as FORS2 and UVIS.
            MinGain = 1.0f,
            MaxGain = 1.0f,

            AstigmatismStrengthPxAtCorner = 0.0f,
            AlwaysAutoguided = true,

            SpacePlatform = new SpacePlatformSpec
            {
                PlatformName = "Hubble Space Telescope",

                // Identical to the UVIS entry: these are properties of the spacecraft and its
                // orbit, not of which channel the light is sent to.
                SunAvoidanceAngleDeg = 62.5,
                BrightLimbAvoidanceAngleDeg = 20.0,
                DarkLimbAvoidanceAngleDeg = 7.6,
                MoonAvoidanceAngleDeg = 9.0,
                PointingJitterArcsecRms = 0.008,

                // IHB Table 7.5, "WFC3/IR PSF FWHM ... vs. wavelength", arcsec column, transcribed
                // whole from 800 to 1700 nm. Unlike UVIS's, this curve rises monotonically across
                // the whole band, and the handbook names the reason: "The monotonic increase in
                // FWHM and decrease in sharpness with wavelength is due to diffraction." The IR
                // channel, unlike the UV, is genuinely diffraction-limited.
                DeliveredPsfFwhmArcsec = new SpectralCurve(
                    new[] { 800.0, 900.0, 1000.0, 1100.0, 1200.0, 1300.0, 1400.0, 1500.0, 1600.0, 1700.0 },
                    new[] { 0.124, 0.126, 0.128, 0.130, 0.133, 0.137, 0.141, 0.145, 0.151, 0.156 }),

                HasApertureDoor = true,

                // HST Primer, "Pointing, Orientation, and Roll Constraints": "The slew rate of
                // HST is limited to approximately 6 degrees per minute of time." The same
                // paragraph states the consequence, "about one hour is needed to go full circle
                // in pitch, yaw, or roll", which is the same figure read the other way round and
                // is the cross-check the harness runs: 360 deg at 6 deg/min is 60 minutes exactly.
                MaxSlewRateDegPerSecond = 6.0 / 60.0,

                // HST Primer, "Orbital Visibility, Acquisition Times, and Overheads": "A normal
                // guide star acquisition, required in the first orbit of every visit, takes 6.5
                // minutes." Charged on every repoint, because this model has no notion of staying
                // within a visit; the reacquisition figure the Primer gives for later orbits of a
                // multi-orbit visit is the same 6.5 minutes in Cycle 34, so nothing is lost by it.
                GuideStarAcquisitionSeconds = 6.5 * 60.0,

                // The true readout is the whole 1024 x 1024 multiplexer, reference pixels included:
                // they are read out and they are downlinked, even though they carry no sky. The
                // imaging path works on the 1014 x 1014 light-sensitive area above.
                FullFramePixels = 1024L * 1024L,
                DownlinkBitsPerPixel = 16,
            },
        };

        public static readonly VisualTelescopeSpec[] All = { RedCat51, Rc20, Cdk1000, Fors2Vlt, Sphere, HubbleWfc3Uvis, HubbleWfc3Ir };
    }
}
