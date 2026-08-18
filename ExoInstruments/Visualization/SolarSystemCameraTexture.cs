using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using ExoInstruments.Core;
using ExoInstruments.Flight;

namespace ExoInstruments.Visualization
{
    /// <summary>Filter-wheel positions. A mono CCD shoots one filter at a time, so each is its own grayscale frame; the filter just selects which of the rendered scene's channels (and how much throughput) forms the signal.</summary>
    /// <summary>
    /// Display transfer function applied when a finished frame is turned into something the eye
    /// can read. DISPLAY ONLY, the science path (GetLastCaptureFullPrecision, the FITS export
    /// and everything AstroImageStack stacks) always receives the untouched linear signal, which
    /// is the same separation every real observing tool keeps between its viewer and its data.
    ///
    /// No astronomical image is looked at linearly. A resolved planetary disk puts almost all of
    /// its pixels into a narrow bright range, so real surface contrast, a few percent of the
    /// local level, occupies a handful of the 256 levels an 8-bit display has and is invisible,
    /// even though the data holds it perfectly. Every real viewer (DS9, PixInsight, IRAF, ESO's
    /// Reflex) therefore offers exactly this choice of stretch.
    /// </summary>
    public enum DisplayStretch
    {
        /// <summary>Raw linear signal, faithful to the detector but the hardest to read on a bright extended source.</summary>
        Linear,
        /// <summary>Logarithmic, DS9's own formulation y = log(a*x + 1) / log(a + 1) with its default a = 1000 (Joye &amp; Mandel 2003, ADASS XII, the SAOImage DS9 paper). Strong lift of faint detail; compresses the bright end hard.</summary>
        Log,
        /// <summary>Inverse hyperbolic sine, the astronomical standard from Lupton et al. 2004 (PASP 116, 133, "Preparing Red-Green-Blue Images from CCD Data"). Linear near zero and logarithmic far from it, so it lifts faint structure without crushing bright regions the way a pure log does, what SDSS's own imagery uses.</summary>
        Asinh
    }

    public enum CameraFilter
    {
        Luminance, // clear/L: full luminance, maximum throughput
        Red,
        Green,
        Blue,
        HAlpha,    // narrowband: red channel only, low throughput (needs longer exposure)

        // Narrowband positions on the forbidden lines. A filter here collects the same line
        // photons as a broadband one while admitting a fraction of the sky, which is the whole
        // point of it; see Core/EmissionLines and tools/emission-tests for the measurement.
        // An instrument only offers the ones it physically carries (VisualTelescopeSpec.AvailableFilters).
        OIII,      // [O III] 5007
        SII,       // [S II] 6716/6731
        NII,       // [N II] 6584, 20.65 Angstrom from H-alpha: needs under about 4 nm to separate
        OII,       // [O II] 3726/3729, below where an amateur CMOS has usable QE
        OI         // [O I] 6300, which is also a bright terrestrial airglow line
    }

    /// <summary>
    /// Neutral-density filter slot, real optical-density stops used by real astrophotographers
    /// on targets too bright for exposure/gain alone to handle (Kerbin's compressed-scale solar
    /// system puts nearby moons in exactly that regime; Mun sits only a few magnitudes fainter
    /// than Kerbol itself). OD/transmission values: Nd8/Nd64/Nd1000 are the standard photographic
    /// ND stops (OD 0.9/1.8/3.0, transmission = 10^-OD); Nd100000 matches the real optical density
    /// of a Baader AstroSolar safety film / Thousand Oaks solar filter (OD ~5.0), the real
    /// accessory class used for direct imaging of the brightest object in the sky.
    ///
    /// WHY THERE IS A STOP BETWEEN ND1000 AND THE SOLAR FILM. Those two are a hundredfold apart,
    /// which is the largest gap on the ladder by a factor of eight, and tools/nd-filter-audit
    /// measures a real configuration that falls straight into it: an RC20 at 4x4 binning and high
    /// gain, on Jupiter at the camera's default half-second exposure, over-exposes through ND1000
    /// and reads 0.2% of full scale through the solar film. Nothing in between existed to reach for.
    ///
    /// Nd6300 is not an interpolation invented to fill that gap. It is Baader's AstroSolar PHOTO
    /// Film, optical density 3.8 (transmission 1.6e-4): the same optically-treated carrier as the
    /// OD 5.0 safety film with the coating density reduced in a controlled fashion, sold expressly
    /// for digital imaging at high magnification and short exposure, and expressly NOT for visual
    /// use at any magnification. It is the one real product occupying this part of the ladder.
    /// </summary>
    public enum NdFilterStop
    {
        None,
        Nd8,
        Nd64,
        Nd1000,
        /// <summary>Baader AstroSolar PHOTO Film, OD 3.8. Photographic only, never safe for visual use.</summary>
        Nd6300,
        Nd100000
    }

    /// <summary>
    /// RC20 astrograph camera: clones KSP's scaled-space camera and
    /// points them at a solar-system body from KSC. Same technique as Tarsier Space
    /// Technology's TSTCameraModule, reimplemented here to avoid the dependency.
    /// Outputs a monochrome, noisy "single raw CCD frame" through a full physics
    /// pipeline (extinction, shot noise, seeing, EVE cloud cover, moon scattering,
    /// cosmic rays, full-well blooming, charge-transfer smear, astigmatism); see
    /// ProcessFrame for the per-effect citations.
    /// </summary>
    public class SolarSystemCameraTexture : IDisposable
    {
        // All optics/sensor identity constants (aperture, focal length, native resolution,
        // pixel pitch, QE, full well, exposure/gain range, ...) live in VisualTelescopeCatalog,
        // not here; this class is the rendering pipeline for whichever spec it's pointed at.
        // Mutable (not readonly): the player can switch instruments from the Observatory dropdown
        // in the GUI (ExoInstrumentsGUI.SelectObservatory), which re-derives every optics/sensor-
        // driven quantity below from whichever VisualTelescopeSpec is now active. See
        // SetActiveTelescope for the switch itself, and builtSpec/EnsureSceneBuilt for how the
        // render targets and scratch buffers get rebuilt at the new instrument's resolution.
        internal static VisualTelescopeSpec Spec = VisualTelescopeCatalog.Rc20;

        /// <summary>The visual telescope this pipeline is currently simulating.</summary>
        public static VisualTelescopeSpec ActiveTelescope => Spec;

        /// <summary>
        /// Switches the active telescope: every optics/sensor constant this class exposes
        /// (aperture, focal length, FOV range, exposure/gain range, full well, read/dark noise,
        /// ...) is re-derived from the new spec on the next read, and EnsureSceneBuilt rebuilds
        /// the render targets and scratch buffers at its resolution on the next capture (see
        /// builtSpec). Resets zoom to the new instrument's wide end.
        ///
        /// Exposure is rescaled by the ratio of the two telescopes' real effective collecting
        /// area (aperture squared, minus obstruction), the same thing a real astronomer redoes
        /// with an exposure-time calculator when changing instruments; without it, a exposure
        /// tuned for the RC20's 0.51m aperture carried straight over to the VLT's 8.2m one
        /// (~258x the collecting area) blows every pixel far past full well, and the pipeline's
        /// per-column blooming (ApplyBlooming, real CCD physics, only ever spills vertically)
        /// turns the saturated body into a tall white bar instead of a photo. Then clamped into
        /// the new instrument's real exposure range.
        ///
        /// Forces Autoguiding on for a spec with AlwaysAutoguided (a real research telescope like
        /// the VLT has no bare/unguided mode); otherwise Autoguiding, being a plain player-set
        /// toggle, would silently carry over whatever the player last chose on the RC20/CDK1000,
        /// including off.
        ///
        /// Does NOT discard an already-captured photo or stacked subs; those belong to the
        /// instrument that took them, so ExoInstrumentsGUI.SwitchTelescope discards them itself
        /// before calling this.
        /// </summary>
        public void SetActiveTelescope(VisualTelescopeSpec spec)
        {
            if (spec == null || spec == Spec) return;

            double oldAreaM2 = EffectiveApertureAreaM2(Spec);
            Spec = spec;
            double newAreaM2 = EffectiveApertureAreaM2(spec);
            if (oldAreaM2 > 0.0 && newAreaM2 > 0.0)
            {
                ExposureSeconds *= (float)(oldAreaM2 / newAreaM2);
            }

            ConformSettingsToSpec();
        }

        /// <summary>
        /// A fresh camera on whatever instrument is already active.
        ///
        /// The reconciliation is not optional housekeeping: the GUI addon is
        /// [KSPAddon(Startup.SpaceCentre, false)], so it and the camera it owns are rebuilt on
        /// every return to the Space Centre, while Spec is static and survives. Selecting the
        /// VLT, flying a mission and coming back therefore produced a camera holding the plain
        /// property DEFAULTS (0.5 s, unguided, gain 1) under SPHERE's spec, which are not values
        /// that instrument can take. Autoguiding was the visible half: AutoguidingForced disables
        /// the very toggle needed to put it back, so the box sat unchecked and unclickable on an
        /// instrument with no unguided mode at all.
        /// </summary>
        public SolarSystemCameraTexture()
        {
            ConformSettingsToSpec();
        }

        /// <summary>
        /// Forces every player-set control into what the active spec can actually accept: zoom to
        /// its wide end, exposure and gain into its real ranges, the filter wheel onto a position
        /// it physically carries, and Autoguiding on for an instrument with no bare/unguided mode
        /// (a real research telescope like the VLT). Without the last one, Autoguiding, being a
        /// plain player-set toggle, would carry over whatever was last chosen on the RC20/CDK1000,
        /// including off.
        ///
        /// Shared by SetActiveTelescope and the constructor, because the two situations that can
        /// leave a control out of step with the spec are switching the spec under the settings and
        /// rebuilding the settings under the spec.
        /// </summary>
        private void ConformSettingsToSpec()
        {
            FovDeg = MaxFovDeg;
            ExposureSeconds = Mathf.Clamp(ExposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            Gain = Mathf.Clamp(Gain, MinGain, MaxGain);
            if (Spec.AlwaysAutoguided) Autoguiding = true;
            if (Array.IndexOf(Spec.AvailableFilters, Filter) < 0) Filter = CameraFilter.Luminance;

            // A cooler setpoint belongs to the camera that was on the telescope, not to the
            // observer: carrying -30 C from a TEC-cooled ZWO onto FORS2's cryogenic detector would
            // be meaningless, and the two instruments' reachable ranges do not even overlap.
            ResetCoolerSetpoint();
        }

        /// <summary>Real effective light-collecting area (m^2): full aperture minus the real secondary-mirror obstruction. Shared by SetActiveTelescope's exposure rescaling and RealApertureAreaCm2's per-frame photon-flux calc, same physical quantity, different units for each caller's convenience.</summary>
        private static double EffectiveApertureAreaM2(VisualTelescopeSpec spec)
        {
            double radiusM = spec.ApertureMeters / 2.0;
            double fullAreaM2 = Math.PI * radiusM * radiusM;
            return fullAreaM2 * (1.0 - spec.SecondaryObstructionFraction * spec.SecondaryObstructionFraction);
        }

        /// <summary>True when the active telescope always has precision tracking and the Autoguiding toggle shouldn't be player-editable (see VisualTelescopeSpec.AlwaysAutoguided).</summary>
        public static bool AutoguidingForced => Spec.AlwaysAutoguided;

        private static float NativePixelSizeMeters => (float)Spec.NativePixelSizeMeters;
        private static float RealFocalLengthMeters => (float)Spec.FocalLengthMeters;
        private static float BarlowFactor => (float)Spec.BarlowFactor;

        public static int NativeTextureWidth => Spec.NativeSensorWidthPx;
        public static int NativeTextureHeight => Spec.NativeSensorHeightPx;

        /// <summary>
        /// Pixel binning factor (1=native resolution, 2/3/4 = NxN binning), the real technique
        /// astrophotography acquisition software (SharpCap, NINA) offers for exactly this
        /// trade-off (resolution vs. processing cost/noise). Changing this rebuilds the
        /// camera's textures and scratch buffers on the next capture.
        /// </summary>
        public static int BinningFactor { get; set; } = 4;

        public static int TextureWidth => NativeTextureWidth / BinningFactor;
        public static int TextureHeight => NativeTextureHeight / BinningFactor;

        /// <summary>
        /// Bytes of MANAGED memory this pipeline holds per pixel of the frame, counted from the
        /// buffers it actually allocates rather than estimated:
        ///
        ///   float[] at 4 bytes each: pixelScratch, frameScratch, lastCaptureSnapshot,
        ///                               rawScratch, signalScratch, lastAduFrame,
        ///                               passScratch                                = 28
        ///   byte[]: displayScratch, three bytes for an RGB24 texture =  3
        ///   float[] transient: FourierConvolution's overlap-add accumulator =  4
        ///
        /// The rendered Color[] is NOT counted: it is released before the optics run (see
        /// pendingSrc), so it does not coincide with the convolution's own peak. It is 16 bytes a
        /// pixel while it lives.
        ///
        /// Keep this in step with the buffers themselves; it is what the panel warns from, and a
        /// warning that has drifted from the allocation is worse than none.
        /// </summary>
        private const long ManagedBytesPerPixel = 35;

        /// <summary>
        /// Bytes of TEXTURE memory per pixel: the half-float render target and its 24-bit depth
        /// buffer (12), the half-float readback texture (8), and the 8-bit display texture (4).
        /// Graphics memory rather than heap, and the part a driver refuses hardest.
        /// </summary>
        private const long TextureBytesPerPixel = 24;

        /// <summary>
        /// What one capture at the current instrument and binning will cost, in bytes.
        ///
        /// This exists because the cost is quartic in the binning factor; halving the binning
        /// quadruples the pixel count, and nothing told the player. At its native resolution the
        /// largest instrument in the roster needs about two gigabytes across the heap and the
        /// graphics device together, on top of everything KSP already holds, and a native
        /// allocation failure at that size does not raise a catchable exception: the process
        /// simply goes away. A managed OutOfMemoryException is caught and reported (see
        /// PollProcessTask); this is the case that cannot be.
        /// </summary>
        public static long EstimatedCaptureMemoryBytes
        {
            get
            {
                long pixels = (long)TextureWidth * TextureHeight;
                return pixels * (ManagedBytesPerPixel + TextureBytesPerPixel);
            }
        }

        /// <summary>Above this, the panel warns: the combination is one where a failure kills the process rather than raising an error.</summary>
        public const long CaptureMemoryWarningBytes = 1_200L * 1024 * 1024;

        /// <summary>Real (binned) pixel pitch in microns, for FITS XPIXSZ/YPIXSZ header keywords.</summary>
        public static double PixelSizeMicrons => NativePixelSizeMeters * BinningFactor * 1e6;

        /// <summary>Real focal length in mm, for the FITS FOCALLEN header keyword.</summary>
        public static double FocalLengthMm => RealFocalLengthMeters * 1000.0;

        /// <summary>
        /// Real full well AT THE CURRENT BINNING, in electrons, for FITS header info and the
        /// shot-noise/saturation pipeline. Binning here is real on-chip/charge-domain summing
        /// (the same assumption BinningFactor's own doc comment already makes), which combines
        /// BinningFactor^2 physical pixels' charge into one before it's ever read out, so a
        /// binned pixel's real saturation capacity is BinningFactor^2 times the native
        /// per-pixel spec, not that same native figure applied unchanged. Getting this wrong at
        /// high binning on a huge-aperture instrument (e.g. the VLT at 4x4) makes every pixel
        /// look saturated far too early, which the per-column blooming pass (ApplyBlooming) then
        /// turns into a large white smear instead of the real, correctly-exposed frame.
        /// </summary>
        public static double FullWellElectrons => Spec.FullWellElectrons * BinningFactor * BinningFactor;

        /// <summary>Largest count this camera's real ADC can output: 2^AdcBits - 1.</summary>
        public static int AdcMaxCount => (1 << Math.Max(1, Spec.AdcBits)) - 1;

        /// <summary>Real conversion factor in electrons per ADU at the current gain setting. Gain amplifies the signal ahead of the converter, so a higher gain means FEWER electrons per count.</summary>
        public static double ElectronsPerAdu(double gainMultiplier)
            => Spec.ElectronsPerAduAtUnityGain / Math.Max(1e-6, gainMultiplier);

        /// <summary>
        /// Charge at which the ADC's top count is reached. Deliberately NOT scaled by binning:
        /// on-chip binning sums charge ahead of one amplifier and one converter, so the digital
        /// ceiling stays put in ADU while the well below it grows, which is why binning a
        /// sensor hard makes it digitally saturation-limited rather than well-limited.
        /// </summary>
        /// <remarks>
        /// The bias pedestal is subtracted from the available range, because it is added ahead of
        /// the converter and therefore genuinely eats headroom: a camera with a 500-count offset
        /// reaches its top code 500 counts of signal early. Small, but it is the same class of
        /// distinction as the well-versus-converter one this method exists to express.
        /// </remarks>
        public static double DigitalSaturationElectrons(double gainMultiplier)
        {
            double k = ElectronsPerAdu(gainMultiplier);
            return k * Math.Max(0.0, AdcMaxCount - Spec.EffectiveBiasLevelAdu(k));
        }

        /// <summary>
        /// The charge a pixel actually stops responding at: whichever of the physical well and
        /// the digital ceiling comes first.
        ///
        /// These are two different limits and real instruments live on both sides of the line.
        /// ESO's FORS2 manual says plainly that "none of the CCDs will saturate before reaching
        /// the numerical truncation limits (65535 adu)"; its 150,000 e- well is never reached,
        /// because at K = 1.25 e-/ADU the converter tops out at 81,919 e- first. A pipeline
        /// carrying fractions of full well cannot represent that at all: it has only one ceiling,
        /// and it is the wrong one.
        /// </summary>
        public static double SaturationElectrons(double gainMultiplier)
            => Math.Min(FullWellElectrons, DigitalSaturationElectrons(gainMultiplier));

        /// <summary>
        /// Which of the instrument's coronagraphic focal-plane masks is in the beam, or null for
        /// none. Null on every instrument that has no coronagraph, which is all of them but SPHERE.
        /// </summary>
        public static Coronagraph.Mask? SelectedCoronagraphMask
        {
            get
            {
                if (!Spec.HasCoronagraph) return null;
                if (selectedCoronagraphMaskName == null) return null;
                return Coronagraph.Find(selectedCoronagraphMaskName);
            }
        }

        /// <summary>Selects a coronagraphic mask by ESO's own name, or null to take it out of the beam. Ignored on an instrument that does not carry it.</summary>
        public static void SetCoronagraphMask(string maskName)
        {
            if (maskName == null) { selectedCoronagraphMaskName = null; return; }
            if (!Spec.HasCoronagraph) return;
            if (Coronagraph.Find(maskName) == null) return;
            selectedCoronagraphMaskName = maskName;
        }

        private static string selectedCoronagraphMaskName;

        /// <summary>
        /// The pupil the light last passed through, which is what the point-spread function is the
        /// diffraction pattern of.
        ///
        /// WHY THESE ARE PROPERTIES AND NOT Spec FIELDS. With a coronagraph in the beam the
        /// telescope's own pupil is no longer the one that forms the image: the Lyot stop
        /// downstream undersizes the outer edge, oversizes the central obstruction and widens the
        /// spider vanes, all deliberately, to throw away the light the focal-plane mask diffracted
        /// to the pupil rims. On SPHERE that turns an 8.2 m aperture with a 14.0% obstruction into
        /// a 7.42 m aperture with a 22.2% one (Schmid et al. 2018, Table 9). The first dark ring
        /// moves outward, the core widens, and the diffraction rings the mask scattered are gone.
        /// That is not a brightness correction; it is a different PSF, and every site that builds
        /// one reads these rather than the spec.
        /// </summary>
        public static double PupilApertureMeters
            => SelectedCoronagraphMask.HasValue ? Spec.CoronagraphLyotStop.ApertureMeters : Spec.ApertureMeters;

        public static double PupilObstructionFraction
            => SelectedCoronagraphMask.HasValue ? Spec.CoronagraphLyotStop.ObstructionFraction : Spec.SecondaryObstructionFraction;

        public static double PupilVaneWidthMeters
            => SelectedCoronagraphMask.HasValue ? Spec.CoronagraphLyotStop.SpiderVaneWidthMeters : Spec.SpiderVaneWidthMeters;

        /// <summary>
        /// Vane count under the stop. The "B" family of SPHERE's pupil masks exists specifically to
        /// hide the telescope's own spiders during pupil-stabilised observing, so with one in the
        /// beam the vanes in the pupil are the STOP's four rather than the telescope's, and they
        /// are six times wider.
        /// </summary>
        public static int PupilVaneCount
            => SelectedCoronagraphMask.HasValue ? 4 : Spec.SpiderVaneCount;

        /// <summary>Fraction of the entering light the pupil stop passes, 1 with no coronagraph in the beam.</summary>
        public static double CoronagraphThroughput
            => SelectedCoronagraphMask.HasValue ? Coronagraph.Throughput(Spec.CoronagraphLyotStop) : 1.0;

        /// <summary>Real plate scale at the current binning: arcsec per (binned) pixel, from the telescope's real focal length and the sensor's real pixel pitch. Public because it's the single number that decides whether a target is resolvable at all; real acquisition software (SharpCap, NINA, ESO's own ETCs) all put it front and center for exactly that reason.</summary>
        public static float PlateScaleArcsecPerPixel
        {
            get
            {
                float pixelSizeMeters = NativePixelSizeMeters * BinningFactor;
                float plateScaleRad = pixelSizeMeters / RealFocalLengthMeters;
                return plateScaleRad * (180f / Mathf.PI) * 3600f;
            }
        }

        /// <summary>Native (no-accessory) field of view across the sensor's long axis, the "wide" end of the zoom range.</summary>
        public static float MaxFovDeg => (TextureWidth * PlateScaleArcsecPerPixel) / 3600f;

        /// <summary>Field of view with a real Barlow, the "high power" end of the zoom range.</summary>
        public static float MinFovDeg => MaxFovDeg / BarlowFactor;

        /// <summary>
        /// False when the instrument's field is FIXED, i.e. it carries no Barlow/Powermate to
        /// change it (BarlowFactor 1: the RedCat 51, SPHERE's real 3.69" field, and both Hubble
        /// channels, which fly the instruments they launched with). MinFovDeg == MaxFovDeg there, so
        /// the GUI drops the zoom slider entirely rather than drawing a control with nothing to
        /// express. Read this rather than comparing the two FOVs at the call site: the fact being
        /// tested is a property of the optics, not a float coincidence.
        /// </summary>
        public static bool HasZoomRange => BarlowFactor > 1f;

        private const string ScaledSpaceCameraName = "Camera ScaledSpace";

        // Real filter bandwidths in Angstrom, matching FilterThroughput's ratios: L covers the
        // whole ~420-685nm visible band; R/G/B each get an even third (modern "1:1:1 balanced"
        // CMOS LRGB filter design); H-alpha is a real ~7nm (70 Angstrom) narrowband filter.
        private static double LuminanceBandwidthAngstrom => Spec.LuminanceBandwidthAngstrom;

        /// <summary>Real sensor exposure range; see VisualTelescopeCatalog for sourcing.</summary>
        public static float MinExposureSeconds => Spec.MinExposureSeconds;
        public static float MaxExposureSeconds => Spec.MaxExposureSeconds;

        private const float MaxDefocusBlurPx = 7.0f;

        /// <summary>
        /// Airmass at which the seeing power law stops growing. X = 6 is about 9.5 degrees
        /// altitude, already below where anyone would image, and far below where the
        /// plane-parallel atmosphere the X^(3/5) law assumes still holds.
        /// </summary>
        private const double MaxSeeingAirmass = 6.0;

        /// <summary>Wavelength every published seeing figure is referred to (500nm), and so the wavelength ZenithSeeingFwhmArcsec is quoted at.</summary>
        private const double SeeingReferenceWavelengthMeters = 500e-9;

        // Sky brightness now comes from SkyBrightnessModel, in the real V mag/arcsec^2 the
        // quantity is measured and published in. The per-second, per-pixel rates that used to
        // live here (twilight 0.30, moon 0.02, airglow 0.004, cloud haze 0.25) had no physical
        // unit, could not be checked against any published sky-brightness measurement, and
        // silently depended on the plate scale, so binning the sensor or fitting a Barlow
        // changed how bright the night sky was.
        internal const double CloudMaxAttenuation = 0.85;                // thick cloud, never 100% opaque
        // NOT amplified by gain (applied after the analog stage), the active telescope's real
        // read noise (a fixed per-readout-event electron figure, unaffected by binning) as a
        // fraction of the CURRENT BINNED full well (this class's own FullWellElectrons, not
        // Spec.FullWellElectrons directly), the same sensor anchor used for shot and
        // dark-current noise. Binning genuinely reduces read noise's relative significance in a
        // real sensor (same read noise electrons, now a smaller slice of a bigger binned well),
        // which this correctly reflects.

        /// <summary>
        /// Reference flux the moonlight term is expressed in: the home world's own brightest moon,
        /// full and at its mean distance, as albedo * (radius/distance)^2. So MoonSkyExcess = 1
        /// always means "this system's full moon overhead", whatever system that is.
        ///
        /// Derived at runtime rather than hardcoded, because the same absolute number means very
        /// different things on different home worlds. Stock's Mün is a 200km body only 12,000km
        /// away; the real Moon is 1737km at 384,400km, and the ratio of those two fluxes is a
        /// factor of ~13.6. A constant calibrated on one makes moonlight roughly an order of
        /// magnitude wrong on the other, and it is a real observational effect worth getting
        /// right, since lunar phase is the single biggest driver of usable dark time at any site.
        ///
        /// Cached per home body: this depends only on the system's geometry, which does not change.
        /// </summary>
        private static double MoonReferenceFluxUnits(CelestialBody home)
        {
            if (home == null) return 0.0;
            if (ReferenceEquals(home, moonReferenceBody)) return moonReferenceFlux;

            double best = 0.0;
            if (home.orbitingBodies != null)
            {
                foreach (CelestialBody moon in home.orbitingBodies)
                {
                    if (moon == null || moon.orbit == null) continue;
                    double distance = moon.orbit.semiMajorAxis;
                    if (distance <= 0.0) continue;
                    double ratio = moon.Radius / distance;
                    double flux = Math.Max(0.0, moon.albedo) * ratio * ratio;
                    if (flux > best) best = flux;
                }
            }

            moonReferenceBody = home;
            moonReferenceFlux = best;
            return best;
        }

        private static CelestialBody moonReferenceBody;
        private static double moonReferenceFlux;

        // Zodiacal light and the dark-sky airglow baseline now live in SkyBrightnessModel,
        // as the published V surface brightnesses they are (Leinert et al. 1998; Patat 2003).

        // Full-well overflow ("blooming"): Pyxel only hard-clips at full well and ships no
        // redistribution model to port. Real CCD full-well overflow is described in Janesick
        // (2001, "Scientific Charge-Coupled Devices", SPIE Press) as charge spilling along the
        // column (parallel/vertical shift-register) direction; absent a specific device's
        // anti-blooming-gate asymmetry data, the textbook default is a charge-conserving,
        // symmetric split between the two vertical neighbors; 0.5 to each means all of the
        // excess is conserved, none invented or discarded.
        private const float BloomingSpillFraction = 0.5f;

        // Numerical convergence cap for the cascading overflow above (a spilled-over pixel can
        // itself overflow into the next), not a physical quantity, same role as the 50-
        // iteration cap on RvSimulator's Kepler-equation solver elsewhere in this codebase.
        private const int BloomingMaxIterations = 4;

        // Charge-transfer smear along the readout (vertical) direction: a simplified,
        // single-trap-species version of Short et al. (2010)'s CDM model used by Pyxel's CTI
        // simulation (pyxel/models/charge_transfer/cdm.py). The real model computes capture via
        // SRH physics (trap density, thermal velocity, electron-cloud volume) in real electron
        // counts; those don't exist in this abstract [0,1] pipeline, so capture/release are
        // constant fractions per row instead, applied with the same nc/nr structure:
        // nc (captured) leaves the pixel and enters the trap state; nr (released) does the reverse.
        // Capture fraction is calibrated to real measured CTI (charge transfer INefficiency, not
        // efficiency): a fresh, undamaged CCD sits near 1e-6/transfer; even HST's ACS/WFC at
        // severely radiation-damaged end-of-life reaches only ~1e-4-1e-3/transfer (Massey,
        // Stoughton & Rhodes 2010, PASP 122, 1035). 1e-4 sits at that damaged-device ceiling,
        // the conservative (most-visible) end of the real documented range for a healthy sensor.
        // Release fraction represents the fast-trap species real CDM models always include
        // alongside slow traps (Short et al. 2010): a trap species whose release time is
        // comparable to the pixel transfer period empties most of its charge within the first
        // few subsequent transfers, which is what produces the short, only-just-visible trail
        // immediately below a bright source in real CCD frames, rather than a long faint one.
        private const float CtiCaptureFraction = 1.0e-4f;
        private const float CtiReleaseFraction = 0.35f;

        // Cosmic ray hits: Pyxel's CosmiX/TARS model (pyxel/models/charge_generation/cosmix)
        // samples a track length and deposits ionization energy along it; its own angle
        // sampling is an unused stub in the shipped source, so an isotropic incidence angle
        // here is no less physical than upstream. Rate is derived, not tuned: sea-level cosmic
        // ray (mostly muon) flux is ~1 cm^-2 min^-1 (Particle Data Group, "Passage of Particles
        // Through Matter" review; Grieder 2001, "Cosmic Rays at Earth"), applied to the active
        // telescope's own real physical silicon area (native sensor width/height at its real
        // pixel pitch; the physical exposed area doesn't change with binning, only how pixels
        // are grouped on readout, so this is computed from the native resolution regardless of
        // the camera's current BinningFactor). A property, not a cached field: it must re-read
        // Spec every call so a telescope switch with a different sensor recomputes the real
        // rate instead of silently keeping the old instrument's.
        private static float CosmicRayHitsPerSecond => ComputeCosmicRayHitsPerSecond();
        private const int CosmicRayMinTrackPx = 2;
        private const int CosmicRayMaxTrackPx = 14;
        /// <summary>
        /// Charge a cosmic-ray track leaves in a pixel, as a fraction of the physical full well.
        /// A minimum-ionising particle crossing the full depletion depth deposits far more than
        /// a well can hold, which is why real cosmic rays read out saturated; 0.85 leaves them
        /// just short of it so they stay distinguishable from a genuinely blown pixel.
        /// </summary>
        private const float CosmicRayDepositWellFraction = 0.85f;

        // Astigmatism: the radial-quadratic FORM (Seidel aberration theory: S_II/coma scales
        // linearly with field, S_III/astigmatism quadratically; see Schroeder, "Astronomical
        // Optics" 2nd ed. 2000, Ch. 6, or Rutten & van Venrooij, "Telescope Optics") is the same
        // for every two-mirror astrograph in this pipeline, so it's applied here regardless of
        // which telescope is active; the PEAK amplitude at the frame corner is instrument-
        // specific (VisualTelescopeSpec.AstigmatismStrengthPxAtCorner) since it depends on that
        // telescope's own optical prescription and how completely its design cancels off-axis
        // aberrations; see each catalog entry's own comment for its sourcing.
        private static float AstigmatismStrengthPxAtCorner => Spec.AstigmatismStrengthPxAtCorner;

        private bool builtOnce;
        private int builtBinningFactor = -1;
        private VisualTelescopeSpec builtSpec;
        private bool available;

        private GameObject root;
        private Camera scaledSpaceCam;
        private RenderTexture renderTexture;
        private Texture2D readbackTexture;

        /// <summary>True when the device granted a half-float render target, i.e. the rendered scene reaches the physics unquantised. False means the 8-bit fallback is in use.</summary>
        public bool HalfFloatCapture => halfFloatCapture;
        private bool halfFloatCapture;
        private Texture2D capturedTexture;
        /// <summary>
        /// The Unity render handed to the background pass, held in a field rather than inside
        /// FrameComputeInputs so that the pass can RELEASE it the moment it is done reading.
        ///
        /// It is a Color[] over the whole frame, 270 MB on the largest instrument at native
        /// resolution, and it is read exactly once, at the very start, before the expensive
        /// optics. Carried in the struct it stayed reachable from the task's closure for the whole
        /// capture, holding that memory across the convolution that needs it most.
        /// </summary>
        private Color[] pendingSrc;

        // The pipeline is MONOCHROME end to end; every write was new Color(v, v, v, 1f) and
        // every read was .r; so these carried four copies of one number plus a constant alpha,
        // at 16 bytes a pixel where 4 will do. Nothing is lost: the float stored is the identical
        // float that used to sit in Color.r.
        private float[] pixelScratch;
        private float[] frameScratch;
        /// <summary>
        /// One scratch plane shared by every pass that needs a full-frame temporary:
        /// ApplyLinearSmear, ApplyPsf's halo component and ApplyAstigmatismBlur.
        ///
        /// They run strictly in sequence and each one fills the whole buffer before reading it
        /// (Array.Clear for the smear, Array.Copy for the other two), so none can observe another's
        /// leftovers. Three separate planes cost three times the memory for no benefit, 135 MB of
        /// it on the largest instrument at native resolution.
        /// </summary>
        private float[] passScratch;

        private float[] EnsurePassScratch(int n)
        {
            if (passScratch == null || passScratch.Length != n) passScratch = new float[n];
            return passScratch;
        }
        private byte[] displayScratch;

        /// <summary>
        /// Renders a monochrome frame to 8-bit RGB through the SAME display chain a live preview
        /// gets: zscale limits from the frame's own extended structure, then the selected stretch.
        ///
        /// Exists so an exported per-filter or per-sub PNG looks like what the observer saw rather
        /// than like a second, differently-scaled rendering of it. The FITS beside it carries the
        /// linear data; this is the quick look.
        /// </summary>
        public static byte[] RenderMonoPreviewRgb24(float[] gray, int width, int height)
        {
            if (gray == null || width <= 0 || height <= 0 || gray.Length != width * height) return null;

            double black = 0.0, white = 1.0;
            bool scaled = AutoScaleDisplay
                       && ZScale.TryExtendedSourceLimits(gray, width, height, out black, out white)
                       && white > black;
            float offset = scaled ? (float)black : 0f;
            float invRange = scaled ? (float)(1.0 / (white - black)) : 1f;

            var rgb = new byte[gray.Length * 3];
            for (int i = 0; i < gray.Length; i++)
            {
                float v = ApplyDisplayStretch((gray[i] - offset) * invRange);
                byte b = (byte)(Mathf.Clamp01(v) * 255f + 0.5f);
                int o = i * 3;
                rgb[o] = b;
                rgb[o + 1] = b;
                rgb[o + 2] = b;
            }
            return rgb;
        }

        /// <summary>
        /// Display transfer function for finished frames. Affects only what is shown and the PNG
        /// quick-look; the FITS export and the stacking path always get the linear signal.
        /// </summary>
        public static DisplayStretch Stretch { get; set; } = DisplayStretch.Asinh;

        /// <summary>
        /// Whether the display picks its own black and white points from the frame (zscale) rather
        /// than mapping the converter's whole range. On by default, because off is only right for
        /// a frame whose subject fills that range, a bright planet, and wrong for everything
        /// faint, where it buries the subject in the bottom few percent of the display.
        ///
        /// A VIEWER control, like Stretch: the FITS export and the stacker always get the linear
        /// frame regardless.
        /// </summary>
        public static bool AutoScaleDisplay { get; set; } = true;

        /// <summary>Where the last displayed frame's black and white points landed, as fractions of full scale. Diagnostics.</summary>
        public double LastDisplayBlackPoint { get; private set; }
        public double LastDisplayWhitePoint { get; private set; } = 1.0;

        /// <summary>DS9's own default scaling constant for its log transfer function.</summary>
        private const double LogStretchA = 1000.0;

        /// <summary>
        /// Softening parameter of the asinh stretch: the signal level, as a fraction of full
        /// scale, below which the curve stays essentially linear. 0.02 puts the turnover just
        /// above this pipeline's real noise floor, so genuine faint structure is lifted while the
        /// noise itself is not amplified into visible grain.
        /// </summary>
        private const double AsinhSoftening = 0.02;

        /// <summary>
        /// Radius ceiling for the seeing-halo kernel, which is now only the FALLBACK path; see
        /// ApplyPsf, which applies the halo as a transfer function and truncates nothing.
        ///
        /// The kernel form could not be made correct by raising this number. At Paranal's 0.72"
        /// seeing and ZIMPOL's binned 3.6 mas pixels, a 256 px radius stops 1.28 FWHM out, where
        /// the profile is still 3.1e-2 of its peak; renormalising conserves the flux but leaves
        /// that 3.1e-2 dropping to zero across one pixel, which is a hard edge in the shape of the
        /// kernel's support. Pushing the step below the read noise of a tenth-magnitude star needs
        /// about 10 FWHM, i.e. a 3985 px kernel across a 1024 px frame. The measurements are in
        /// tools/psf-truncation.
        /// </summary>
        private const int MaxHaloKernelRadiusPx = 256;

        /// <summary>
        /// Cells a padded frequency-domain pass may allocate. 2048x2048 complex singles is 33 MB
        /// of transient working set, which covers every frame small enough for the halo to span;
        /// ZIMPOL binned 2x2 is 1024x1024 and pads to exactly this. Beyond it the kernel fallback
        /// takes over rather than the allocation growing unbounded.
        /// </summary>
        private const long MaxOtfTransformCells = 2048L * 2048L;

        // Built PSF, cached on everything it depends on (see EnsurePsfKernels).
        private VisualTelescopeSpec psfCacheSpec;
        private CameraFilter psfCacheFilter;
        private double psfCachePlateScale = -1.0;
        private double psfCacheAtmosphericFwhm = -1.0;
        private double psfCacheDefocusRadius = -1.0;
        private double psfCacheZenithDistance = double.NaN;
        private double psfCacheZenithX = double.NaN;
        private double psfCacheZenithY = double.NaN;
        private double psfCachePointingFwhm = double.NaN;
        private float[] psfCacheCore;
        private int psfCacheCoreRadius;
        private float psfCacheCoreWeight = 1f;
        private float[] psfCacheHalo;
        private int psfCacheHaloRadius;
        private double psfCacheHaloR0;
        private double psfCacheHaloWavelength;
        /// <summary>Full-frame halo spectrum, cached with the kernels: it costs a transform to prepare and a stacking batch would otherwise pay for it once per sub.</summary>
        private FourierConvolution.RadialKernelSpectrum haloSpectrum;
        private int haloSpectrumWidth;
        private int haloSpectrumHeight;
        private double psfCacheDiffractionFwhm;
        private float[] rawScratch;
        /// <summary>The frame's signal plane, in fractions of full well: rendered bodies plus every point source, before any noise exists.</summary>
        private float[] signalScratch;
        private float[] smearLineScratch;
        private int lastStarsDrawnInternal;

        private ScaledSpaceFader[] scaledSpaceFaders;

        /// <summary>Enabled state of every ScaledSpaceFader as a capture found it, so RenderScene can put all of them back. Reused rather than reallocated, since a capture runs this on every shot.</summary>
        private bool[] faderRestoreBuffer;

        /// <summary>Conversion factor actually used by the last capture, electrons per ADU. Written to the FITS EGAIN keyword.</summary>
        public double LastElectronsPerAdu => lastElectronsPerAdu;
        private double lastElectronsPerAdu = 1.0;

        /// <summary>Bias pedestal in the last capture's raw ADU. Written to the FITS BIASLVL keyword, so the exported frame can be pedestal-corrected without guessing.</summary>
        public double LastBiasLevelAdu => lastBiasLevelAdu;
        private double lastBiasLevelAdu;

        /// <summary>
        /// Photometric zero point of the last capture: m = -2.5 log10(ADU/s) + ZP, for a flat
        /// source spectrum. Written to the FITS MAGZERO keyword. NaN when it could not be computed.
        ///
        /// This is the keyword that turns an exported frame from a picture into a measurement. All
        /// of its ingredients were already being computed: the integrated system response, the
        /// real obstructed collecting area, the conversion gain, and never combined into the one
        /// number a reduction needs.
        /// </summary>
        public double LastPhotometricZeroPoint => lastPhotometricZeroPoint;
        private double lastPhotometricZeroPoint = double.NaN;

        /// <summary>Dark current actually used by the last capture, e-/pixel/s at the detector's current temperature. Written to the FITS DARKCURR keyword.</summary>
        public double LastDarkCurrentElectronsPerSecond => lastDarkCurrentElectronsPerSecond;
        private double lastDarkCurrentElectronsPerSecond;

        /// <summary>
        /// The two numbers that say whether the last capture's TARGET made it into the frame, and
        /// which half of the pipeline to blame if it did not.
        ///
        /// LastTargetElectrons is what the physics computed the body should deliver: aperture,
        /// exposure, bandpass, distance, phase. LastRenderedLuminanceSum is what Unity actually
        /// drew of it. The pipeline multiplies the render by their ratio, so a healthy capture has
        /// both non-zero; electrons without luminance means the physics is fine and the RENDER
        /// produced nothing, which is the one failure that looks exactly like an under-exposure.
        /// </summary>
        public double LastTargetElectrons => lastTargetElectrons;
        public double LastRenderedLuminanceSum => lastRenderedLuminanceSum;
        private double lastTargetElectrons;
        private double lastRenderedLuminanceSum;

        /// <summary>
        /// The 64-bit seed the last capture's noise was drawn from. Written to the FITS RANDSEED
        /// keyword: with it and the rest of the header, the frame is reproducible exactly.
        /// </summary>
        public ulong LastCaptureSeed => lastCaptureSeed;
        private ulong lastCaptureSeed;

        /// <summary>
        /// The detector's actual temperature in Celsius: the cooler setpoint on an instrument that
        /// has one, and the instrument's fixed operating temperature on one that does not.
        ///
        /// Setting it moves real physics, not a label. Dark current follows the depletion-generation
        /// law (Core.DarkCurrentModel), hot pixels follow with it because they ARE dark current, and
        /// CCD-TEMP in the exported header reports what was actually used. Clamped to what the
        /// instrument's own cooler can reach, so the control cannot promise a temperature the
        /// hardware could not hold.
        /// </summary>
        public static double DetectorTemperatureCelsius
        {
            get
            {
                if (double.IsNaN(detectorTemperatureOverrideCelsius)) return Spec.DetectorTemperatureCelsius;
                return ClampToCoolerRange(detectorTemperatureOverrideCelsius);
            }
            set => detectorTemperatureOverrideCelsius = ClampToCoolerRange(value);
        }
        private static double detectorTemperatureOverrideCelsius = double.NaN;

        /// <summary>True when the active instrument's detector temperature is a control the observer has (see VisualTelescopeSpec.CoolerDeltaBelowAmbientC).</summary>
        public static bool HasAdjustableCooler => Spec.HasAdjustableCooler;

        /// <summary>Coldest and warmest setpoint the active instrument's cooler can hold.</summary>
        public static double CoolerMinimumTemperatureCelsius => Spec.CoolerMinimumTemperatureCelsius;
        public static double CoolerMaximumTemperatureCelsius => Spec.CoolerMaximumTemperatureCelsius;

        /// <summary>Returns the setpoint to the instrument's own published operating temperature, the one its catalogued dark current was measured at, so the model is back on its calibration point.</summary>
        public static void ResetCoolerSetpoint() => detectorTemperatureOverrideCelsius = double.NaN;

        private static double ClampToCoolerRange(double celsius)
        {
            if (double.IsNaN(celsius)) return celsius;
            if (!Spec.HasAdjustableCooler) return Spec.DetectorTemperatureCelsius;
            return Math.Max(Spec.CoolerMinimumTemperatureCelsius,
                            Math.Min(Spec.CoolerMaximumTemperatureCelsius, celsius));
        }

        /// <summary>Charge at which the last capture stopped responding, the smaller of the physical well and the converter's ceiling.</summary>
        public double LastSaturationElectrons => lastSaturationElectrons;
        private double lastSaturationElectrons;

        /// <summary>
        /// The last capture as the detector's own ADU counts, the calibratable data product.
        ///
        /// This is what FITS export writes, unaltered, so that EGAIN converts it back to
        /// electrons and the frame reduces like an observed one. Distinct from CapturedPhoto and
        /// GetLastCaptureFullPrecision, which are display frames normalised to [0,1].
        /// </summary>
        public float[] GetLastCaptureAdu() => lastAduFrame != null ? (float[])lastAduFrame.Clone() : null;
        private float[] lastAduFrame;

        // Fixed hot/dead pixel map: a chip's defect pattern is persistent, so seeded
        // once from a constant, never from the target or UT.
        private int[] hotPixelIndices;

        /// <summary>
        /// The sensor's flat field and its readout offsets, as deviations packed to half precision:
        /// 2 bytes a pixel each rather than 4, for the reason Core.SensorNonUniformity gives.
        /// Both are properties of this instrument at this binning rather than of any exposure, so
        /// both are built once and discarded with the resolution-sized buffers.
        /// </summary>
        private ushort[] flatFieldMap;
        private ushort[] offsetFpnMap;
        private int[] deadPixelIndices;

        /// <summary>
        /// Charge held in the detector's surface traps, carried FROM ONE EXPOSURE TO THE NEXT.
        ///
        /// The only state in this class that is not a property of the instrument or of the frame
        /// being built: it is a property of the observing SEQUENCE, and it exists because a
        /// residual image is the one detector effect that depends on what was observed before (see
        /// Core.DetectorPersistence). Full precision rather than the half-float packing the flat
        /// and offset maps use, because unlike those two this one is read, decremented and written
        /// back on every exposure, and a rounding error there would accumulate over a sequence
        /// instead of staying put.
        ///
        /// Two arrays because the two trap populations empty at different rates and have to be
        /// tracked apart; one array with a two-term decay law has no defined state after a partial
        /// decay. Both are null on every instrument currently on the roster, since none has a
        /// published amplitude to simulate.
        /// </summary>
        private float[] persistenceFastTrapped;
        private float[] persistenceSlowTrapped;

        /// <summary>
        /// The infrared array's persistence state, which is a different quantity from the CCD's.
        ///
        /// The CCD model holds TRAPPED CHARGE and decrements it. The published HgCdTe model is not
        /// written that way: it is a function of the FLUENCE the pixel reached in an earlier
        /// exposure, how long that exposure lasted, and how long ago it ended, so those three are
        /// what have to be carried. Storing an equivalent trapped charge instead would mean
        /// inverting a fit that was never published in that form.
        ///
        /// ONE STIMULUS PER PIXEL, NOT A SUM, because that is what the published pipeline does:
        /// where several earlier exposures could each cause persistence, it counts only the one
        /// that would cause the most in the current image. The fitted parameters were derived under
        /// that rule and using them under another would apply them outside their own calibration.
        /// </summary>
        private float[] hgcdtePersistenceFluence;
        private float[] hgcdtePersistenceStimulusSeconds;
        private float[] hgcdtePersistenceElapsedSeconds;

        /// <summary>Real continuous gain control range; see VisualTelescopeCatalog for sourcing.</summary>
        public static float MinGain => Spec.MinGain;
        public static float MaxGain => Spec.MaxGain;
        /// <summary>Field of view in degrees (zoom). Clamped to [MinFovDeg, MaxFovDeg]. Defaults to
        /// the wide end (MaxFovDeg); the old flat 3.0 default predates the real derived FOV
        /// range (roughly 0.06-0.32 deg for the RC20) and sat outside it, which is why the GUI
        /// slider showed a stale "3.0 deg" label until the user's first drag: GUILayout.HorizontalSlider
        /// only clamps the value it returns once there's actual pointer interaction, so an
        /// out-of-range starting value round-trips unchanged on every frame before that. Starting
        /// at MaxFovDeg also matches how real acquisition software (SharpCap, NINA) opens a
        /// session zoomed out, and stays correct for whichever VisualTelescopeSpec is active.</summary>
        public float FovDeg { get; set; } = MaxFovDeg;
        /// <summary>Exposure time in seconds. Drives brightness, noise, drift trailing, and how long BeginExposure takes.</summary>
        public float ExposureSeconds { get; set; } = 0.5f;
        /// <summary>When true, the frame is always sharp; when false, FocusOffset controls defocus blur.</summary>
        public bool Autofocus { get; set; } = true;
        /// <summary>Manual focus, [-1, 1]. 0 = sharp, magnitude = amount of defocus blur. Ignored when Autofocus is on.</summary>
        public float FocusOffset { get; set; } = 0f;
        /// <summary>Selected filter-wheel position.</summary>
        public CameraFilter Filter { get; set; } = CameraFilter.Luminance;

        /// <summary>Neutral-density filter slot: optical attenuation for targets too bright for exposure/gain alone.</summary>
        public NdFilterStop NdFilter { get; set; } = NdFilterStop.None;

        /// <summary>Real optical-density transmission (10^-OD) for each ND filter stop; see NdFilterStop for sourcing.</summary>
        public static double NdFilterTransmission(NdFilterStop stop)
        {
            switch (stop)
            {
                case NdFilterStop.Nd8: return Math.Pow(10.0, -0.9);
                case NdFilterStop.Nd64: return Math.Pow(10.0, -1.8);
                case NdFilterStop.Nd1000: return Math.Pow(10.0, -3.0);
                case NdFilterStop.Nd6300: return Math.Pow(10.0, -3.8);
                case NdFilterStop.Nd100000: return Math.Pow(10.0, -5.0);
                default: return 1.0;
            }
        }
        /// <summary>Sensor gain, [MinGain, MaxGain]: higher = brighter + noisier.</summary>
        public float Gain { get; set; } = 1.0f;
        /// <summary>When true the mount tracks the sky (no drift). Off by default: a bare RC20 has no autoguider.</summary>
        public bool Autoguiding { get; set; } = false;

        // --- Timed-exposure capture state ----------------------------------
        private bool isCapturing;
        private float captureElapsed;
        private float captureDuration;
        private SkyTarget pendingTarget;

        /// <summary>
        /// Debug: skip the real-time wait, keeping the exposure itself. Toggled from the KSP
        /// console with exoinstruments_debug.
        ///
        /// This gates ONLY captureDuration, which is the wall clock the shutter is held open
        /// against and nothing else -- the physics reads ExposureSeconds, which is untouched. So a
        /// 120 s frame taken with this on is the same 120 s frame: same photon count, same shot and
        /// dark noise, same drift trailing, same saturation, and EXPTIME in the FITS still says 120.
        /// The only thing that disappears is the two minutes of waiting, which is what makes
        /// testing a change to the imaging pipeline practical at all.
        ///
        /// Static because it is a session-wide debug switch rather than a property of one camera,
        /// and deliberately not persisted: it resets to off on every KSP start, so a saved game can
        /// never quietly be playing with instant exposures.
        /// </summary>
        public static bool InstantExposures { get; set; }

        // --- Background processing state (the heavy per-pixel physics pipeline runs off the
        // main thread once the exposure's integration time has elapsed; see GatherFrameInputs
        // /ComputeFramePixels/PollProcessTask) ------------------------------
        private Task<float[]> processTask;
        private bool isProcessing;

        /// <summary>True while a timed exposure is integrating (between BeginExposure and completion).</summary>
        public bool IsCapturing => isCapturing;
        /// <summary>0..1 progress through the current timed exposure.</summary>
        public float CaptureProgress => isCapturing && captureDuration > 0f ? Mathf.Clamp01(captureElapsed / captureDuration) : 0f;
        /// <summary>True while the captured frame's noise/effects pipeline is running on a background task, after the exposure's integration time has elapsed but before the photo is ready.</summary>
        public bool IsProcessing => isProcessing;
        /// <summary>True once a timed exposure has completed and a finished photo is available.</summary>
        public bool HasCapturedPhoto { get; private set; }

        private float[] lastCaptureSnapshot;

        /// <summary>False only if KSP's own scaled-space camera can't be found (should not happen on a stock install).</summary>
        public bool IsAvailable
        {
            get
            {
                if (!builtOnce || builtBinningFactor != BinningFactor || builtSpec != Spec) EnsureSceneBuilt();
                return available;
            }
        }

        /// <summary>The finished, timed-exposure photo (frozen at exposure completion), or null before the first BeginExposure completes.</summary>
        public Texture2D CapturedPhoto => capturedTexture;

        /// <summary>Half-width (pixels) of the PSF kernel convolved into the last capture. See OpticalPsf for what the kernel contains.</summary>
        public float LastAppliedBlurRadiusPx { get; private set; }

        /// <summary>
        /// Fraction of the last capture's pixels that reached or exceeded full well BEFORE the
        /// sensor's clipping was applied, i.e. genuinely blown-out, their real surface contrast
        /// irrecoverably destroyed by saturation rather than merely softened.
        ///
        /// Diagnostic, and a necessary one on a large-aperture instrument: an 8.2m telescope on a
        /// bright solar-system disk saturates almost instantly, and a saturated disk looks like a
        /// featureless blur (flat white core, blooming halo) that is easily mistaken for an
        /// optics/atmosphere problem when it is really just gross over-exposure. Written on the
        /// background pipeline thread; safe to read after the processing Task completes, which is
        /// the only place PollProcessTask ever does.
        /// </summary>
        public float LastSaturatedFraction => lastSaturatedFraction;
        private float lastSaturatedFraction;

        /// <summary>
        /// The scintillation multiplier drawn for the last capture, and the sigma it came from
        /// (see ScintillationMultiplier). Surfaced because this is a single per-exposure random
        /// draw applied to the whole target: it is the reason two otherwise identical captures
        /// can differ in brightness, which is real, and it must never be negative, which is the
        /// bug that used to turn a bright planet into a black disc on a lit sky.
        /// </summary>
        /// <summary>Why the last capture failed, or null if it succeeded. Shown in the panel; see PollProcessTask.</summary>
        public string LastProcessingError { get; private set; }

        /// <summary>True when the graphics device refused the render target at the current binning; every capture at this resolution will be garbage until the player bins down.</summary>
        public bool RenderTargetRefused => renderTextureRefused;
        private bool renderTextureRefused;

        /// <summary>
        /// The untouched Unity render behind the last capture, before any of this pipeline's
        /// physics. Diagnostic only, and the one measurement that cleanly attributes a bad frame:
        /// if this is already wrong the fault is in the game's own scaled-space rendering or in
        /// how the clone cameras are set up, and no change to the optical or sensor model can
        /// recover detail that was never drawn; if this is right and the finished frame is not,
        /// the fault is downstream and reproducible offline. Written out only when the player
        /// asks for it (see the Diagnostics toggle); it costs a second full-resolution PNG.
        /// </summary>
        public Texture2D RawRenderFrame => readbackTexture;

        public float LastScintillationFactor => lastScintillationFactor;
        public float LastScintillationSigma => lastScintillationSigma;
        private float lastScintillationFactor = 1f;
        private float lastScintillationSigma;

        /// <summary>Atmospheric FWHM (arcsec) fed to the PSF for the last capture, the residual left by adaptive optics, or the plain ground-based seeing figure. 0 means diffraction-limited.</summary>
        /// <summary>
        /// Where the last capture actually pointed, as a FITS world coordinate system. Invalid
        /// (and therefore not written to the header) when the field geometry could not be
        /// resolved, outside the scenes where the observatory site is available, for instance.
        /// </summary>
        public Core.FitsWcs LastWcs { get; private set; }

        /// <summary>
        /// The geometry of one finished frame, frozen at the same instant its pixels are.
        ///
        /// WHY IT IS A SNAPSHOT AND NOT THE LastWcs/LastTargetPixel PROPERTIES. A stacking series
        /// PIPELINES (see CanBeginExposure): the next exposure opens its shutter as soon as the
        /// previous one's integration ends, and TickCapture can finish frame N's reduction and
        /// render frame N+1 within a single tick. The gather pass for N+1 overwrites LastWcs and
        /// LastTargetPixelX/Y before the caller that collects N ever runs, so a consumer reading
        /// those properties at collection time gets the NEXT frame's pointing attached to THIS
        /// frame's pixels. Off by one exposure, silently, and in a header a plate solve is
        /// supposed to trust.
        /// </summary>
        public struct CapturedFrameGeometry
        {
            /// <summary>Where this frame pointed. Invalid when the field geometry could not be resolved.</summary>
            public Core.FitsWcs Wcs;
            /// <summary>True when the sky turned under the sensor during this exposure, so its sources are trailed.</summary>
            public bool Trailed;
            /// <summary>Where the aim point landed in this frame, pixels; NaN when it could not be measured.</summary>
            public double RegistrationX;
            public double RegistrationY;
        }

        /// <summary>The geometry of the frame currently held in lastCaptureSnapshot; see CapturedFrameGeometry.</summary>
        public CapturedFrameGeometry LastCaptureGeometry { get; private set; }

        /// <summary>The geometry of the exposure being reduced right now, waiting to be published with its pixels.</summary>
        private CapturedFrameGeometry pendingCaptureGeometry;

        /// <summary>Airmass the last capture was taken through; +Infinity if the target was below the horizon.</summary>
        public double LastAirmass { get; private set; }

        /// <summary>
        /// The effective photometric width (Angstrom) the last capture was calibrated with, for a
        /// flat source spectrum, the single number that turns an apparent magnitude into
        /// electrons through this instrument at this airmass (see SystemBandpass). Recorded in the
        /// exported header because with it, the aperture area and the exposure time, a reader can
        /// reproduce this frame's photometry exactly rather than having to trust it.
        /// </summary>
        public double LastEffectiveWidthAngstrom { get; private set; }

        /// <summary>Central wavelength (nm) of the fitted filter.</summary>
        public double ActiveFilterCentralWavelengthNm => FilterCentralWavelengthMeters(Filter) * 1e9;

        /// <summary>Central wavelength of any filter position, nanometres. Needed by an export that writes one file per filter rather than only the active one.</summary>
        public static double CentralWavelengthNmOf(CameraFilter filter) => FilterCentralWavelengthMeters(filter) * 1e9;

        /// <summary>Bandwidth of any filter position, nanometres.</summary>
        public static double BandwidthNmOf(CameraFilter filter) => FilterBandwidthAngstrom(filter) * 0.1;

        /// <summary>Published FWHM (nm) of the fitted filter.</summary>
        public double ActiveFilterBandwidthNm => FilterBandwidthAngstrom(Filter) * 0.1;

        /// <summary>True when the last capture ran unguided long enough for the sky to turn under the sensor, so its sources are trailed and its WCS describes only the exposure's start.</summary>
        public bool LastFrameTrailed { get; private set; }

        public double LastAtmosphericFwhmArcsec { get; private set; }

        /// <summary>The instrument's own diffraction-limited FWHM (arcsec) at the current filter's wavelength, computed from its real annular pupil, the hard floor no observing condition can beat.</summary>
        public double LastDiffractionFwhmArcsec { get; private set; }

        /// <summary>
        /// Length of the last frame's atmospheric dispersion smear, arcseconds, across the filter's
        /// own passband and after any corrector. The number that says whether a frame's stars are
        /// points or short spectra.
        /// </summary>
        public double LastDispersionSmearArcsec { get; private set; }

        /// <summary>Zenith distance the last frame was taken at, degrees.</summary>
        public double LastZenithDistanceDeg { get; private set; }

        /// <summary>Airglow surface brightness the active filter's passband sees, rayleighs, van Rhijn included. The number a nebula's own surface brightness competes against.</summary>
        public double LastAirglowRayleighsInBand { get; private set; }

        /// <summary>Fraction of that which is line emission rather than airglow continuum.</summary>
        public double LastAirglowLineShare { get; private set; }

        /// <summary>True when the last capture's adaptive-optics halo used a kernel spanning the whole frame, so nothing detectable was truncated, rather than the bounded fallback. See ApplyPsf.</summary>
        public bool LastHaloSpannedFrame { get; private set; }

        /// <summary>Fraction of a source's halo flux that kernel held; the rest falls at offsets larger than the sensor and never reached a pixel. Zero when no halo applies.</summary>
        public double LastHaloEnclosedFraction { get; private set; }

        /// <summary>
        /// Last capture at full float precision, straight from the physics pipeline, NOT
        /// CapturedPhoto, which round-trips through an 8-bit RGB24 Texture2D and destroys nearly
        /// all of the real, physically-computed noise (shot/dark/read noise live at a small
        /// fraction of full well, far below 1/255). This is what AstroImageStack consumes.
        ///
        /// Row-major, y-down, one float per pixel. Null before the first capture; a fresh copy
        /// every call, so a caller can hold it while the next exposure overwrites the pipeline's
        /// own buffers.
        /// </summary>
        public float[] GetLastCaptureGray()
            => lastCaptureSnapshot != null ? (float[])lastCaptureSnapshot.Clone() : null;

        /// <summary>Starts a timed exposure on the target. Nothing renders until the exposure completes.</summary>
        public void BeginExposure(SkyTarget target)
        {
            if (!IsAvailable || !target.HasTarget) return;
            pendingTarget = target;
            isCapturing = true;
            integrationComplete = false;
            captureElapsed = 0f;
            // Zero, not a small number: TickCapture completes the integration on the first tick
            // whose deltaTime is positive, so the shutter closes next frame instead of next minute.
            captureDuration = InstantExposures
                ? 0f
                : Mathf.Clamp(ExposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            HasCapturedPhoto = false;
        }

        /// <summary>Cancels any in-progress exposure and discards the finished photo. Releases the locked aim so the next shot re-centers.</summary>
        public void DiscardCapturedPhoto()
        {
            HasCapturedPhoto = false;
            isCapturing = false;
            integrationComplete = false;
            hasLockedAim = false;
            // Doesn't stop the background Task itself (no cancellation token), but nulling the
            // reference makes PollProcessTask ignore its result once it does finish.
            processTask = null;
            isProcessing = false;
        }

        /// <summary>Marks the photo as consumed without releasing the locked aim. Used between stacking subs, where the natural drift is what alignment is supposed to correct.</summary>
        public void ConsumeCapturedPhoto()
        {
            HasCapturedPhoto = false;
        }

        /// <summary>Cancels an in-progress timed exposure without producing a photo.</summary>
        public void CancelExposure()
        {
            isCapturing = false;
            integrationComplete = false;
        }

        /// <summary>
        /// Advances an in-progress timed exposure by deltaTime real seconds. When the
        /// exposure's integration time elapses, renders the target and kicks the noise/effects
        /// pipeline off onto a background Task (see PollProcessTask); when already processing,
        /// polls that task for completion instead. No-op when neither capturing nor processing.
        /// </summary>
        /// <summary>
        /// Frames waited so far for the target's scaled-space textures to become resident before
        /// the shutter fires. Reset on every capture.
        /// </summary>
        private int textureWaitFrames;

        /// <summary>
        /// How long the shutter will wait for those textures. The real gap measured between a
        /// Kopernicus unload and its matching reload was 1.9 s, so this is set well past it at
        /// 60 fps; beyond the cap the frame is taken regardless rather than the capture hanging.
        /// </summary>
        private const int MaxTextureWaitFrames = 240;

        /// <summary>
        /// True when the shutter is free to open: nothing integrating, and nothing already waiting
        /// its turn to be rendered.
        ///
        /// This is what lets a stacking series PIPELINE. An exposure is real elapsed time and the
        /// physics pass behind it is a background task of a few seconds, so a series that waits for
        /// each frame to finish reducing before opening the shutter again throws that time away;
        /// on a ten-sub series at two minutes it is minutes of dead sky. The next exposure can start
        /// the moment the previous one's INTEGRATION ends; only the render and the reduction need
        /// exclusive use of the frame buffers, and TickCapture holds the shutter closed if the
        /// previous reduction has not finished by the time the new integration does.
        /// </summary>
        public bool CanBeginExposure => !isCapturing && !integrationComplete;

        /// <summary>An exposure whose integration time has elapsed but which has not been rendered yet, because the previous frame's reduction still owns the buffers.</summary>
        private bool integrationComplete;

        public void TickCapture(float deltaTime)
        {
            // Poll first, always: the reduction of the PREVIOUS frame may be what is blocking this
            // one's render, and a series only pipelines if that gets picked up in the same tick.
            if (isProcessing) PollProcessTask();

            if (isCapturing)
            {
                captureElapsed += deltaTime;
                if (captureElapsed >= captureDuration)
                {
                    isCapturing = false;
                    integrationComplete = true;
                }
            }

            if (!integrationComplete) return;

            // The buffers are still owned by the previous frame's reduction. The photons for this
            // one are already counted; the integration is over, so nothing is lost by waiting
            // here, and overlapping two reductions would have them share scratch arrays.
            if (isProcessing) return;

            // The exposure has elapsed, but the target's scaled-space textures may not be bound.
            // Kopernicus unloads them when no camera IT knows about can see the body, and this
            // telescope renders through clones it does not know about; the reload it then
            // performs on request is DEFERRED, not synchronous (see
            // KopernicusOnDemandIntegration for the log that establishes this). Rendering into
            // that gap draws the body's geometry with no colour map: a black disc with a lit rim.
            //
            // So the capture waits for residency rather than racing it. Bounded, because a body
            // whose loader never flips its own isLoaded flag must not hang the shutter forever;
            // after the cap the frame is taken regardless, which is exactly the old behaviour.
            if (!KopernicusOnDemandIntegration.EnsureScaledSpaceTexturesLoaded(pendingTarget.Body))
            {
                if (++textureWaitFrames <= MaxTextureWaitFrames) return;

                Debug.LogWarning($"[ExoInstruments] Scaled-space textures for {pendingTarget.DisplayName} did not "
                               + $"become resident within {MaxTextureWaitFrames} frames; capturing anyway. "
                               + "The body may render without its colour map.");
            }

            textureWaitFrames = 0;
            integrationComplete = false;
            RenderExposure(pendingTarget);
        }

        /// <summary>Renders the target into readbackTexture (main thread), gathers every input the physics pipeline needs, then kicks that pipeline off onto a background Task.</summary>
        private void RenderExposure(SkyTarget target)
        {
            if (!target.HasTarget) return;
            // Without autoguiding, the aim stays locked at the last UpdateAim position, which is
            // how a body drifts off-center if it moved, and how a fixed target drifts as the sky turns.
            if (Autoguiding) UpdateAim(target);
            RenderScene(target);

            FrameComputeInputs inputs = GatherFrameInputs(target);

            // Taken here, while the gather that produced it is still the most recent one, and held
            // until PollProcessTask publishes it with the pixels it belongs to. See
            // CapturedFrameGeometry for what reading the live properties later would get instead.
            pendingCaptureGeometry = new CapturedFrameGeometry
            {
                Wcs = LastWcs,
                Trailed = LastFrameTrailed,
                RegistrationX = LastTargetPixelX,
                RegistrationY = LastTargetPixelY,
            };

            isProcessing = true;
            processTask = Task.Run(() => ComputeFramePixels(inputs));
        }

        /// <summary>Checks the background frame-processing Task; once complete, uploads the result to the output/captured textures and snapshots it for AstroImageStack, the only parts that must happen on the main thread.</summary>
        private void PollProcessTask()
        {
            if (processTask == null) { isProcessing = false; return; }
            if (!processTask.IsCompleted) return;

            if (processTask.IsFaulted)
            {
                Exception cause = processTask.Exception?.GetBaseException();
                // Surfaced in the panel, not just the log: a failed capture otherwise looks like
                // a corrupted image rather than an error, and the player has no way to tell the
                // difference without opening the debug console.
                LastProcessingError = cause is OutOfMemoryException
                    ? $"Out of memory processing a {TextureWidth}x{TextureHeight} frame "
                      + $"({(double)TextureWidth * TextureHeight / 1e6:F1} Mpx). Use a higher binning factor."
                    : cause?.Message ?? "unknown error";
                Debug.LogError($"[ExoInstruments] Frame processing failed at {TextureWidth}x{TextureHeight}: {cause}");
                processTask = null;
                isProcessing = false;
                return;
            }
            LastProcessingError = null;

            // The empty-render failure, reported here rather than from the background pass that
            // detected it: the target's own electron count was computed and the render drew none
            // of it, so the frame carries its sky, its noise and its stars but no body.
            if (lastTargetElectrons > 0.0 && lastRenderedLuminanceSum <= 1e-6)
            {
                Debug.LogError(
                    $"[ExoInstruments] The scene render came back empty at {TextureWidth}x{TextureHeight} "
                  + $"(binning {BinningFactor}): summed luminance {lastRenderedLuminanceSum:E3}, while the physics "
                  + $"computed {lastTargetElectrons:E3} electrons from the target. It is absent from this frame. "
                  + "Use a higher binning factor.");
            }

            pixelScratch = processTask.Result;
            processTask = null;
            isProcessing = false;

            // Logged here rather than from the reduction itself, which runs on a background Task
            // and by this file's own rule does no logging. One line per capture: it is what says
            // whether a slow exposure is the optics, the sky maps or the detector, and it is the
            // question that gets asked every time a capture feels slow.
            if (!string.IsNullOrEmpty(LastStageTimings))
            {
                Debug.Log($"[ExoInstruments] Reduced a {TextureWidth}x{TextureHeight} frame "
                        + $"(binning {BinningFactor}) in {LastReductionMilliseconds:F0} ms on "
                        + $"{Core.ParallelWork.MaxWorkers} worker(s): {LastStageTimings}");
            }

            // The snapshot is taken from the LINEAR pipeline output, before any display transfer
            // function; it is what the FITS export and AstroImageStack consume, and stretching
            // it would corrupt every downstream measurement.
            if (lastCaptureSnapshot == null || lastCaptureSnapshot.Length != pixelScratch.Length)
                lastCaptureSnapshot = new float[pixelScratch.Length];
            Array.Copy(pixelScratch, lastCaptureSnapshot, pixelScratch.Length);

            // Published with the snapshot, in the same statement group, because the two describe
            // the same exposure and any gap between them is where the pipelined next frame gets in.
            LastCaptureGeometry = pendingCaptureGeometry;

            HasCapturedPhoto = true;
            UploadDisplayTextures();
        }

        /// <summary>
        /// Rebuilds the on-screen/preview textures from the stored linear capture through the
        /// current display stretch. Separate from PollProcessTask so changing the stretch
        /// re-renders the existing frame instead of forcing a new exposure, the same way a real
        /// viewer restretches what is already on screen.
        /// </summary>
        public void UploadDisplayTextures()
        {
            if (lastCaptureSnapshot == null) return;

            int n = lastCaptureSnapshot.Length;
            if (capturedTexture == null || capturedTexture.width != TextureWidth || capturedTexture.height != TextureHeight)
            {
                if (capturedTexture != null) UnityEngine.Object.Destroy(capturedTexture);
                capturedTexture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGB24, false);
            }

            // Straight into the texture's own raw bytes rather than through a Color[] staging
            // array. The destination is RGB24, three bytes a pixel, which is all a monitor can
            // show and all this path ever claimed to carry; so a 16-byte Color per pixel was
            // buying nothing on the way there. On the largest instrument at native resolution that
            // staging array alone was 270 MB.
            //
            // Nothing is lost that was not already being lost: this is the DISPLAY path, and the
            // linear full-precision frame it is built from stays untouched in lastCaptureSnapshot
            // for the FITS export and the stacker.
            if (displayScratch == null || displayScratch.Length != n * 3) displayScratch = new byte[n * 3];

            // Black and white points BEFORE the curve. A transfer function decides how the range
            // between them is distributed; it cannot decide where they are, and on a frame whose
            // subject spans twenty counts of a sixteen-thousand-count converter that is the larger
            // question. See ZScale.
            double black = 0.0, white = 1.0;
            bool scaled = AutoScaleDisplay
                       && ZScale.TryExtendedSourceLimits(lastCaptureSnapshot, TextureWidth, TextureHeight,
                                                         out black, out white)
                       && white > black;
            LastDisplayBlackPoint = scaled ? black : 0.0;
            LastDisplayWhitePoint = scaled ? white : 1.0;
            float invRange = scaled ? (float)(1.0 / (white - black)) : 1f;
            float offset = scaled ? (float)black : 0f;

            for (int i = 0; i < n; i++)
            {
                float v = ApplyDisplayStretch((lastCaptureSnapshot[i] - offset) * invRange);
                byte b = (byte)(Mathf.Clamp01(v) * 255f + 0.5f);
                int o = i * 3;
                displayScratch[o] = b;
                displayScratch[o + 1] = b;
                displayScratch[o + 2] = b;
            }

            capturedTexture.LoadRawTextureData(displayScratch);
            capturedTexture.Apply();
        }

        /// <summary>
        /// Display transfer function; see the DisplayStretch enum for what each mode is and
        /// where it comes from. Input and output are both normalised to [0,1].
        /// </summary>
        private static float ApplyDisplayStretch(float linear)
        {
            float v = Mathf.Clamp01(linear);
            switch (Stretch)
            {
                case DisplayStretch.Log:
                    // DS9's log scale, at its own default a = 1000.
                    return (float)(Math.Log(LogStretchA * v + 1.0) / Math.Log(LogStretchA + 1.0));

                case DisplayStretch.Asinh:
                    // Lupton et al. 2004. The softening parameter sets where the curve turns over
                    // from linear to logarithmic; normalising by asinh(1/beta) keeps white at 1.
                    return (float)(Math.Log(v / AsinhSoftening + Math.Sqrt(v * v / (AsinhSoftening * AsinhSoftening) + 1.0))
                                 / Math.Log(1.0 / AsinhSoftening + Math.Sqrt(1.0 / (AsinhSoftening * AsinhSoftening) + 1.0)));

                default:
                    return v;
            }
        }

        private bool hasLockedAim;
        private Vector3 lockedCamPos;
        private Quaternion lockedLook;

        /// <summary>Locks the camera aim on the target's current position. A capture always renders through the locked aim, so without autoguiding the field drifts between shots.</summary>
        /// <summary>
        /// Where the render cameras go, in KSP's scaled space.
        ///
        /// The ground path keeps taking the live scaled-space camera's own position, which is the
        /// technique this pipeline was built on and the one that made the ground captures work
        /// (see the class summary): the game has already placed that camera at the observer, and
        /// borrowing it avoids re-deriving a transform the game is authoritative about.
        ///
        /// An orbiting telescope is somewhere else entirely, and often somewhere the player's
        /// own camera has never been, so its position is converted explicitly. ScaledSpace is a
        /// uniform scaling of the world about a moving origin, and LocalToScaledSpace is KSP's
        /// own conversion for it, so this is not an approximation of the ground path's trick but
        /// the general form of it.
        ///
        /// THE ONE THING WORTH CHECKING when a new host body is used: at 500 km over Kerbin the
        /// observer sits at 1100 km from the centre, which is 183 scaled units against Kerbin's
        /// own scaled radius of 100, so the camera is outside the body's scaled stand-in. A
        /// telescope in a very low orbit around a large body would be inside it, and would
        /// photograph the inside of a sphere. That is not a case any real telescope is in, and
        /// the limb-avoidance constraint forbids it long before the geometry does.
        /// </summary>
        private static Vector3 ResolveScaledSpaceObserverPosition(CelestialBody home)
        {
            if (ObservingPlatform.IsSpaceBased) return ObservingPlatform.ScaledSpacePosition(home);

            Camera liveScaledSpace = FindCameraByName(ScaledSpaceCameraName);
            if (liveScaledSpace != null) return liveScaledSpace.transform.position;
            return home != null && home.scaledBody != null
                ? home.scaledBody.transform.position
                : Vector3.zero;
        }

        public void UpdateAim(SkyTarget target)
        {
            if (!TryResolveWorldDirection(target, out Vector3d direction)) return;

            lockedCamPos = ResolveScaledSpaceObserverPosition(FlightGlobals.GetHomeBody());
            lockedLook = Quaternion.LookRotation((Vector3)direction, Vector3.up);
            hasLockedAim = true;
        }

        /// <summary>
        /// Unit world direction toward a target, WITHOUT touching the camera's own aim.
        ///
        /// Split out of UpdateAim because an orbiting telescope has to be slewed onto the same
        /// direction the camera looks along, and asking for that direction must not re-centre the
        /// frame as a side effect: with autoguiding off, when the frame re-centres is the player's
        /// decision and nothing else may make it for them.
        ///
        /// Scaled space is a uniform scaling of the world about a moving origin, so a DIRECTION is
        /// the same vector in both and needs no conversion, the same fact TryBuildFieldGeometry
        /// relies on.
        /// </summary>
        public bool TryResolveWorldDirection(SkyTarget target, out Vector3d direction)
        {
            direction = Vector3d.zero;
            if (!target.HasTarget) return false;

            if (target.IsBody)
            {
                if (target.Body == null || target.Body.scaledBody == null) return false;
                Vector3 camPos = ResolveScaledSpaceObserverPosition(FlightGlobals.GetHomeBody());
                Vector3 toTarget = target.Body.scaledBody.transform.position - camPos;
                if (toTarget.sqrMagnitude < 1e-6f) return false; // observer coincides with the target's scaled position
                direction = (Vector3d)toTarget.normalized;
                return true;
            }

            if (!TryEquatorialDirection(target.RaDeg, target.DecDeg, Planetarium.GetUniversalTime(),
                                        out Vector3d skyDirection)) return false;
            direction = skyDirection;
            return true;
        }

        /// <summary>
        /// Unit direction toward a catalogue position, as a scaled-space vector.
        ///
        /// The exact inverse of the chain TryBuildFieldGeometry runs forward: equatorial to
        /// horizontal by SkyCoordinates, horizontal to a (north, east, up) triple by SkyVector, and
        /// that triple back onto the observatory's own world basis. Scaled space is a uniform
        /// scaling of the world about a moving origin, so a DIRECTION is the same vector in both
        /// and no conversion is needed, the same fact TryBuildFieldGeometry relies on.
        /// </summary>
        /// <remarks>
        /// Internal rather than private because the flight side needs the same chain: a telescope
        /// commanded at a catalogue position stores the RA/Dec, not a frozen world vector, and has
        /// to resolve it through THIS composition so that where the spacecraft is told to point and
        /// where the chart draws the target are the same computation. See GroundStation.
        /// </remarks>
        internal static bool TryEquatorialDirection(double raDeg, double decDeg, double ut, out Vector3d direction)
        {
            direction = Vector3d.zero;
            if (double.IsNaN(raDeg) || double.IsNaN(decDeg)) return false;
            if (!TryBuildSiteBasis(out Vector3d north, out Vector3d east, out Vector3d up,
                                   out double latitudeDeg, out double longitudeDeg)) return false;

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return false;

            double meridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                ut, home.rotationPeriod, home.initialRotation, longitudeDeg);
            HorizontalCoordinates horizontal =
                SkyCoordinates.EquatorialToHorizontal(raDeg, decDeg, meridianRaDeg, latitudeDeg);
            SkyVector v = SkyVector.FromHorizontal(horizontal.AltitudeDeg, horizontal.AzimuthDeg);

            direction = (north * v.X + east * v.Y + up * v.Z).normalized;
            return direction.sqrMagnitude > 0.5;
        }

        /// <summary>
        /// The observatory's local north/east/up in world coordinates.
        ///
        /// Read from KSP's own latitude/longitude convention by asking the home body where a point
        /// slightly north and slightly east of the site is, rather than from cross products of a
        /// rotation axis: Unity's left-handed frame makes the sign of such a product easy to get
        /// backwards and impossible to notice, and this form simply cannot be wrong about east.
        /// </summary>
        internal static bool TryBuildSiteBasis(out Vector3d north, out Vector3d east, out Vector3d up,
                                               out double latitudeDeg, out double longitudeDeg)
        {
            north = east = up = Vector3d.zero;
            latitudeDeg = longitudeDeg = 0.0;

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return false;

            latitudeDeg = ObservatorySite.LatitudeDeg;
            longitudeDeg = ObservatorySite.LongitudeDeg;
            double elevation = ObservatorySite.SiteElevationMeters;

            // The GROUND site, always, even when an orbiting telescope is the observer. This
            // basis is not the observer's own horizon: it is how the mod's fictional RA/Dec
            // frame is defined, and TryEquatorialDirection composes equatorial-to-horizontal
            // with horizontal-to-world THROUGH it. That composition is independent of which
            // site is used, so the direction it produces is inertial and correct from anywhere
            // in the system; substituting an orbiting observer's position here would pair a
            // spacecraft's radial vector with the ground site's latitude and produce a frame
            // that is neither.
            Vector3d observer = ObservatorySite.WorldPosition(home);
            up = (observer - home.position).normalized;
            if (up.sqrMagnitude < 0.5) return false;

            // At the pole the northward step would run over the top, so it is taken southward and negated.
            const double StepDeg = 0.01;
            bool nearNorthPole = latitudeDeg + StepDeg > 90.0;
            Vector3d northProbe = home.GetWorldSurfacePosition(
                nearNorthPole ? latitudeDeg - StepDeg : latitudeDeg + StepDeg, longitudeDeg, elevation) - observer;
            if (nearNorthPole) northProbe = -northProbe;
            Vector3d eastProbe = home.GetWorldSurfacePosition(latitudeDeg, longitudeDeg + StepDeg, elevation) - observer;

            north = Orthonormalize(northProbe, up);
            east = Orthonormalize(eastProbe, up);
            return north.sqrMagnitude > 0.5 && east.sqrMagnitude > 0.5;
        }

        /// <summary>
        /// Renders one frame through the locked aim into readbackTexture. Called once at
        /// exposure completion, with no live preview. Works entirely in KSP's scaled-space frame
        /// using the game's own scaledBody transforms, so no coordinate conversion is needed.
        /// </summary>
        private void RenderScene(SkyTarget target)
        {
            if (!target.HasTarget || !IsAvailable) return;
            if (target.IsBody && target.Body.scaledBody == null) return; // no scaled stand-in, nothing to frame
            if (!hasLockedAim) UpdateAim(target); // first-ever shot on this target: always start centered

            CelestialBody home = FlightGlobals.GetHomeBody();
            Vector3 camPos = lockedCamPos;
            Quaternion look = lockedLook;

            // Deliberately NOT touching Sun.Instance's rotation the way Tarsier does,
            // mutating that global object bleeds a color shift into the live scene.
            // Sun parallax from KSC is ~0.05 deg (negligible). May need revisiting for
            // distant bodies like Jool, but only with a technique that can't affect the game view.

            // Large planet packs unload a body's scaled-space textures when no camera the GAME
            // knows about can see it, and the telescope's cameras are clones it doesn't know
            // about. Photographing an unloaded body draws its mesh with no colour map: a black
            // disc with a lit rim. Force the target (and its moons, which share the field)
            // resident first. No-op without Kopernicus.
            KopernicusOnDemandIntegration.EnsureScaledSpaceTexturesLoaded(target.Body);

            // A RenderTexture's contents are volatile: Unity documents them as lost on graphics-
            // device events, fullscreen transitions among them, which is what alt-tabbing is.
            // A texture whose backing surface was released reports IsCreated() false and must be
            // re-created before anything renders into it.
            if (renderTexture != null && !renderTexture.IsCreated()) renderTexture.Create();

            RenderTexture activeRT = RenderTexture.active;
            RenderTexture.active = renderTexture;

            float fov = Mathf.Clamp(FovDeg, MinFovDeg, MaxFovDeg);

            // Force every body's scaled stand-in visible, because KSP fades them by real-camera
            // distance, which has nothing to do with where our clone points.
            //
            // The home body is the exception, and it must be switched OFF rather than merely
            // left alone. The clone sits at the home body's own scaled position, i.e. INSIDE
            // its scaled stand-in, so a stand-in left enabled is rendered as a shell wrapped
            // around the camera: a large smooth coloured gradient across the frame, brightest
            // where the shell is lit, with a curved terminator running through it. Skipping it
            // only avoided switching it on; whether it was already on was left to KSP's own
            // distance fade, whose thresholds are set per body and differ between planet packs;
            // so the same code could look clean on one install and produce a coloured wash on
            // another. Restored afterwards, so the live scene is unaffected.
            // Every fader touched is recorded and put back, not just the home body's. A capture
            // is an observation and must leave the game exactly as it found it: forcing every
            // body's stand-in on and walking away leaves the live scene drawing stand-ins KSP had
            // deliberately faded out. That relied on ScaledSpaceFader re-deciding the flag on its
            // own next frame, probably true, never verified, and not something a capture should
            // be betting the player's scene on.
            if (faderRestoreBuffer == null || faderRestoreBuffer.Length != scaledSpaceFaders.Length)
                faderRestoreBuffer = new bool[scaledSpaceFaders.Length];

            for (int i = 0; i < scaledSpaceFaders.Length; i++)
            {
                ScaledSpaceFader fader = scaledSpaceFaders[i];
                if (fader == null || fader.r == null) continue;

                faderRestoreBuffer[i] = fader.r.enabled;

                // The home body's own stand-in is suppressed only for a GROUND observer, which is
                // the case the paragraph above is about: a camera sitting at the surface is inside
                // that sphere. A telescope in orbit is outside it, and the planet below is then a
                // real object in the sky that can legitimately appear in the frame or occult the
                // target, exactly as it does for the real instrument. Suppressing it there would
                // delete the one thing an orbiting telescope has to look past.
                bool suppress = !ObservingPlatform.IsSpaceBased
                             && home != null && fader.celestialBody == home;
                fader.r.enabled = !suppress;
            }

            // KSP's galaxy camera is NOT rendered, and that is deliberate.
            //
            // It draws the game's painted sky cube, and a telescope cannot use it. The cube is
            // 4096 pixels across a 90-degree face, i.e. 1.32 arcmin per texel, while FORS2's
            // field is 8.6 arcmin: the frame covers about six texels and magnifies them 628x.
            // What reaches the sensor is therefore not a sky but a bilinear interpolation of a
            // handful of texels, vast smooth blobs, which the 8-bit render target then slices
            // into hard-edged contour bands as soon as any non-linear display stretch pulls the
            // bottom of the range up. (This was latent until the galaxy camera's projection
            // matrix was correctly reset to the telescope's own field; before that it rendered
            // at the game's wide field, where the same cube is sampled near its native
            // resolution and looks perfectly fine.)
            //
            // Magnification aside, it does not belong in a calibrated frame at all. It is an
            // artistic texture with no photometric meaning, and the pipeline would fold it into
            // the same electron budget as the target and scale it by whatever that target's
            // brightness happened to be. It also double-counts: its painted stars would sit on
            // top of the real, correctly-placed, correctly-illuminated catalogue stars this
            // pipeline now draws itself.
            //
            // Everything the background should contain is modelled instead, in real V surface
            // brightness, by SkyBrightnessModel: airglow (Patat 2003), zodiacal light (Leinert
            // et al. 1998), moonlight (Krisciunas & Schaefer 1991) and twilight (Patat et al.
            // 2006), and added after the optics, where a uniform sky belongs.
            //
            // Rendered TWICE, and only the second pass is read back.
            //
            // The first capture after the window regains focus was reliably wrong, a black disc
            // with a lit rim where the planet should be, i.e. its geometry drawn without its
            // surface texture, while every subsequent capture from the same setup was correct.
            // That is a graphics-device reset: alt-tabbing releases GPU-side resources, and the
            // scaled-space bodies' textures are restored lazily, on demand, by the frame that
            // first asks for them. Our capture WAS that frame, so it rendered against surfaces
            // that had not been restored yet and read back the result before they were.
            //
            // A discarded warm-up pass makes the demand explicit and lets the restore happen
            // before the frame that counts. Done unconditionally rather than behind focus
            // tracking: it costs one extra camera render (single-digit milliseconds, against the
            // hundreds this capture already spends convolving the PSF), and it removes the whole
            // class of first-frame staleness instead of a state machine that has to guess which
            // events invalidate what.
            // try/finally, not a plain sequence: if the render throws, the player's scene must
            // still be handed back intact rather than left with every stand-in forced on.
            try
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    // The matrix resets are critical: KSP's ScaledSpace camera carries a custom
                    // view/projection matrix that CopyFrom inherits and silently overrides our
                    // transform. Resetting them makes the clone's own transform authoritative.
                    AimCamera(scaledSpaceCam, ScaledSpaceCameraName, camPos, look, fov);
                    scaledSpaceCam.ResetWorldToCameraMatrix();
                    scaledSpaceCam.ResetProjectionMatrix();

                    // Solid black, not Depth. This used to clear only the depth buffer because
                    // the galaxy camera ran first and filled the colour buffer with KSP's painted
                    // sky; that pass is gone (see the comment above), so this camera now owns the
                    // clear. An empty background is the correct starting point: everything that
                    // belongs in it (airglow, zodiacal light, moonlight, twilight, and every
                    // catalogue star) is added later by the physics, in real surface brightness.
                    scaledSpaceCam.clearFlags = CameraClearFlags.SolidColor;
                    scaledSpaceCam.backgroundColor = Color.black;
                    scaledSpaceCam.farClipPlane = 3e15f;
                    scaledSpaceCam.Render();
                }

                readbackTexture.ReadPixels(new Rect(0, 0, TextureWidth, TextureHeight), 0, 0);
                readbackTexture.Apply();
            }
            finally
            {
                RenderTexture.active = activeRT;

                // Hand every stand-in back exactly as it was found; the live scene draws
                // through them and must not be left rearranged by a capture.
                for (int i = 0; i < scaledSpaceFaders.Length; i++)
                {
                    ScaledSpaceFader fader = scaledSpaceFaders[i];
                    if (fader == null || fader.r == null) continue;
                    fader.r.enabled = faderRestoreBuffer[i];
                }
            }
        }

        /// <summary>Copies the live camera settings onto the clone, then sets position/rotation/FOV.</summary>
        private void AimCamera(Camera clone, string liveCameraName, Vector3 pos, Quaternion rot, float fovDeg)
        {
            ResetCameraFromLive(clone, liveCameraName);
            clone.transform.position = pos;
            clone.transform.rotation = rot;
            clone.aspect = (float)TextureWidth / TextureHeight;
            clone.fieldOfView = HorizontalToVerticalFovDeg(fovDeg);
        }

        /// <summary>
        /// Unity's Camera.fieldOfView is the VERTICAL field; every field of view in this class
        /// (FovDeg, MinFovDeg, MaxFovDeg) is quoted across the sensor's long axis, because that
        /// is how a telescope's field is normally quoted and how the zoom range is derived from
        /// the real focal length. Assigning one to the other left the scene rendered at the
        /// sensor's aspect ratio too wide, 1.47x on the RC20's 4144x2822 chip, so a body's
        /// size in the frame did not match the plate scale the same class reports for the FITS
        /// header, and no star drawn at its real position could line up with it.
        /// </summary>
        private static float HorizontalToVerticalFovDeg(float horizontalFovDeg)
        {
            if (TextureWidth <= 0 || TextureHeight <= 0) return horizontalFovDeg;
            double tanHalfH = Math.Tan(0.5 * horizontalFovDeg * Math.PI / 180.0);
            double tanHalfV = tanHalfH * TextureHeight / TextureWidth;
            return (float)(2.0 * Math.Atan(tanHalfV) * 180.0 / Math.PI);
        }

        /// <summary>Copies the live camera settings onto the clone and restores the clone's own render target (the only property that must survive CopyFrom).</summary>
        private void ResetCameraFromLive(Camera clone, string liveCameraName)
        {
            Camera live = FindCameraByName(liveCameraName);
            if (live == null) return;
            RenderTexture rt = clone.targetTexture;
            float depth = clone.depth;
            clone.CopyFrom(live);
            clone.targetTexture = rt;
            clone.depth = depth;
            clone.rect = new Rect(0, 0, 1, 1);
        }

        private static Camera FindCameraByName(string name)
        {
            return Camera.allCameras.FirstOrDefault(c => c.name == name);
        }

        private void EnsureSceneBuilt()
        {
            if (builtOnce && builtBinningFactor == BinningFactor && builtSpec == Spec) return;
            if (builtOnce) Dispose(); // binning or active telescope changed since the last build; tear down and rebuild at the new resolution
            builtOnce = true;
            builtBinningFactor = BinningFactor;
            builtSpec = Spec;

            try
            {
                Camera liveScaledSpace = FindCameraByName(ScaledSpaceCameraName);
                if (liveScaledSpace == null)
                {
                    Debug.LogWarning("[ExoInstruments] Could not find KSP's scaled-space camera, solar-system camera disabled.");
                    available = false;
                    return;
                }

                root = new GameObject("ExoInstrumentsSolarSystemCamera");

                // Half-float capture, not 8-bit.
                //
                // The rendered scene supplies every bit of spatial structure this pipeline has
                // (the belts, the terminator, the limb darkening, and any companion sharing the
                // frame), and the physics then multiplies the whole plane by a single
                // calibration factor. Quantising it first therefore quantises the finished
                // photograph, and 8 bits is nowhere near enough for the range a real frame holds:
                // sRGB-encoded ARGB32 resolves 3295:1, i.e. 8.8 magnitudes, so Jupiter at V=-2.5
                // and a Galilean moon at V=5.0 (a real 1000:1 ratio) put that moon on 3.3
                // quantisation levels. Its limb, its phase and its shading are gone before the
                // optics are even applied, and any non-linear display stretch then slices what
                // remains into visible contour bands, the same mechanism that made the painted
                // sky cube's texels show up as hard-edged polygons (see RenderScene).
                //
                // Half float removes the quantisation and nothing else. It is NOT a claim that
                // the values become linear radiance: KSP renders in Gamma colour space, so its
                // shader output is display-referred, and no inverse transform recovers true
                // radiance from it (in gamma space the lighting itself is computed on encoded
                // albedos, so raising the result to 2.2 would darken the terminator without
                // justification rather than linearise anything). That limitation is inherent to
                // building on the game's own renderer and is documented rather than papered over;
                // what changes here is only that this mod stops ADDING an error of its own.
                // ReadWrite.Linear accordingly means "store what the renderer produced, verbatim",
                // which is exactly the intent; a float target needs no encoding to hold range.
                //
                // Falls back to the previous 8-bit target on a device that cannot give a
                // half-float render surface, since a working capture beats no capture.
                halfFloatCapture = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf);
                if (!halfFloatCapture)
                    Debug.LogWarning("[ExoInstruments] This graphics device has no half-float render target; "
                                   + "falling back to 8-bit capture. Faint detail beside a bright body will band.");

                renderTexture = new RenderTexture(TextureWidth, TextureHeight, 24,
                                                  halfFloatCapture ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32,
                                                  halfFloatCapture ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB)
                {
                    name = "ExoInstrumentsSolarSystemCameraRT"
                };
                // Create() reports whether the graphics device actually granted the surface. At
                // the largest instrument's native resolution this is a 4096x4128 half-float
                // target with a 24-bit depth buffer, 12 bytes per pixel, so about 203 MB of
                // VRAM for this one texture, on top of whatever the game already holds. A
                // refusal here is silent otherwise: the camera still "renders", and the readback
                // returns whatever was in memory.
                if (!renderTexture.Create())
                {
                    Debug.LogError($"[ExoInstruments] The graphics device refused a {TextureWidth}x{TextureHeight} "
                                 + $"({(double)TextureWidth * TextureHeight / 1e6:F1} Mpx) render target. Use a higher binning factor.");
                    renderTextureRefused = true;
                }
                else renderTextureRefused = false;

                var scaledSpaceObj = new GameObject("ExoInstrumentsScaledSpaceCamClone");
                scaledSpaceObj.transform.parent = root.transform;
                scaledSpaceObj.transform.localPosition = Vector3.zero;
                scaledSpaceObj.transform.localRotation = Quaternion.identity;
                scaledSpaceCam = scaledSpaceObj.AddComponent<Camera>();
                scaledSpaceCam.CopyFrom(liveScaledSpace);
                scaledSpaceCam.targetTexture = renderTexture;
                scaledSpaceCam.depth = 18; // same relative depth Tarsier uses for its scaled-space clone
                scaledSpaceCam.enabled = false;

                // Matches the render target: a half-float readback would be pointless through an
                // 8-bit intermediate, which is exactly what this used to be. Marked linear so
                // Unity applies no colour conversion on the way through; the readback is a
                // copy, not an interpretation.
                readbackTexture = halfFloatCapture
                    ? new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGBAHalf, false, true)
                    : new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGB24, false);

                // Display textures stay 8-bit on purpose: these are what goes to the screen, and
                // a monitor has no more than that. The full-precision frame lives in
                // lastCaptureSnapshot for FITS export (see GetLastCaptureFullPrecision).
                pixelScratch = new float[TextureWidth * TextureHeight];

                scaledSpaceFaders = UnityEngine.Object.FindObjectsOfType<ScaledSpaceFader>();

                available = true;
            }
            catch (Exception e)
            {
                Debug.LogError("[ExoInstruments] Solar-system camera setup failed, disabling the feature: " + e.Message);
                available = false;
                Dispose();
            }
        }

        /// <summary>Plain-data snapshot of everything ComputeFramePixels needs, gathered on the main thread (it touches CelestialBody/Unity APIs), then handed to a background Task that touches none of that, mirroring the StartImagingRefresh/PollImagingRenderTask pattern used elsewhere in this mod.</summary>
        private struct FrameComputeInputs
        {
            public long TargetSeed;
            public double Ut;
            public float ExposureSeconds;
            public float IsoGain;
            public CameraFilter Filter;
            public double ScintSigma;
            public double MoonSkyExcess;
            public float CloudCoverage;
            public double TotalElectrons;
            // PSF ingredients rather than a finished kernel. Building the kernel is pure C# that
            // touches nothing Unity-owned, so it belongs on the background side of this boundary;
            // doing it here would stall the main thread at the moment the player presses
            // Capture, which is exactly when a stall is most visible.
            public double PlateScaleArcsec;
            /// <summary>Plain ground-based seeing (arcsec), already resolved from the target's airmass on the main thread. Ignored when the instrument has adaptive optics.</summary>
            public double SeeingFwhmArcsec;
            public double DefocusDiscRadiusPx;

            // --- Sky field ------------------------------------------------------------
            /// <summary>Where each direction on the sky lands on the sensor, built from the camera's own axes (see BuildFieldGeometry).</summary>
            public GnomonicProjection Projection;
            /// <summary>Catalogue stars whose light reaches the sensor this exposure, already cone-searched on the main thread.</summary>
            public List<RenderedStar> Stars;
            /// <summary>Galaxies whose own extent reaches the sensor, already cone-searched on the main thread.</summary>
            public List<Galaxy> Galaxies;
            /// <summary>Total Galactic E(B-V) toward the boresight. A galaxy sits behind the whole column, so this is the reddening that applies to it in full.</summary>
            public double FieldReddeningEBv;

            // --- Atmospheric dispersion ------------------------------------------------
            /// <summary>Zenith distance of the field, degrees. Drives the dispersion, which goes as tan z.</summary>
            public double ZenithDistanceDeg;
            /// <summary>Unit vector toward the zenith IN PIXEL SPACE, derived by projecting the zenith through the frame's own geometry so that field rotation and parity are already in it.</summary>
            public double ZenithUnitX;
            public double ZenithUnitY;
            /// <summary>Site air, for the refractive index: from the ICAO standard atmosphere at the observatory's altitude.</summary>
            public double AirTemperatureCelsius;
            public double AirPressureMillibar;
            public double WaterVapourPressureMillibar;
            /// <summary>Solar-system bodies in the field too small for the renderer to resolve, already projected and converted to signal.</summary>
            public List<PointSource> UnresolvedBodies;
            public bool HaveFieldGeometry;
            public double StartMeridianRaDeg;
            public double EndMeridianRaDeg;
            public double ObserverLatitudeDeg;
            /// <summary>Sky background over the whole exposure, in electrons per pixel, from a real surface brightness (see SkyBrightnessModel), not a per-pixel rate.</summary>
            public double SkyElectronsPerPixel;
            /// <summary>Scintillation for a POINT source: stars get no benefit from the extended-source averaging that quietens a resolved disk.</summary>
            public double PointSourceScintSigma;
            /// <summary>Signal below which a source cannot be told from the frame's own noise and is not drawn (see BuildStarSignalFloor).</summary>
            public double SignalCutoffElectrons;
            // Photometric chain for a catalogue star, resolved on the main thread so the
            // background pass needs nothing but arithmetic.
            /// <summary>The instrument's integrated spectral response for this filter and airmass: optics, filter, QE curve and extinction in one object (see SystemBandpass). Built on the main thread, read-only thereafter.</summary>
            public SystemResponse Response;
            public List<RenderedSupernova> Supernovae;
            public double ApertureAreaCm2;
            /// <summary>Atmospheric extinction at the fitted filter's own wavelength: extinction alone, no ND filter, no cloud.</summary>
            public double CloudTransmission;
            /// <summary>
            /// Transmission a catalogue star loses OUTSIDE the spectral response: cloud and the ND
            /// filter. Atmospheric extinction is deliberately not here; it is wavelength
            /// dependent and now lives inside Response's integral, so including it again would
            /// attenuate every source twice.
            /// </summary>
            public double StarNonAtmosphericTransmission;

            // --- Diurnal drift, as a real vector on the sensor ---------------------------
            /// <summary>Pixel displacement of the field centre over the exposure. Zero when the mount tracks.</summary>
            public double DriftPixelX;
            public double DriftPixelY;

            /// <summary>True when this frame is being taken from orbit, which is what turns off every atmospheric term (see GatherFrameInputs).</summary>
            public bool IsSpaceBased;

            /// <summary>The direction the telescope is looking, world space. Needed by the zodiacal-light lookup, which is the one sky term that depends on WHERE in the sky the frame is, on the ground as much as in orbit.</summary>
            public Vector3d LineOfSight;
            public bool HasLineOfSight;

            /// <summary>The spacecraft's pointing budget over this exposure. Zeroed for a ground instrument, which has no attitude to hold.</summary>
            public PointingBudget Pointing;
        }

        /// <summary>
        /// Gathers every CelestialBody/Unity-API-touching input ComputeFramePixels needs, on
        /// the main thread. Real photon-flux calibration: the imaged body's actual apparent
        /// magnitude (real albedo/radius/Sun-distance/observer-distance/phase-angle, Lambertian
        /// phase law; see PhotonFluxModel) converted into real electrons collected through
        /// the RC20's real aperture/obstruction/QE/filter-bandwidth/exposure/extinction.
        /// </summary>
        private FrameComputeInputs GatherFrameInputs(SkyTarget target)
        {
            // Handed to the background pass through a field rather than through the inputs struct,
            // so that pass can drop it as soon as it has read it; see pendingSrc.
            pendingSrc = readbackTexture.GetPixels();
            EnsureDefectMap();

            float exposureSeconds = Mathf.Clamp(ExposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            float isoGain = Mathf.Clamp(Gain, MinGain, MaxGain);

            double ut = Planetarium.GetUniversalTime();

            // THE ONE BRANCH THE WHOLE SPACE PATH TURNS ON. Above the atmosphere there is no
            // airmass, no extinction, no scintillation, no seeing, no cloud and no scattered
            // moonlight, and every one of those is set to its absent value here rather than
            // being computed and quietly coming out small. They are not small in orbit, they do
            // not exist, and an airmass computed from a spacecraft's "altitude above the
            // horizon" would be a number with no referent.
            bool spaceBased = Spec.IsSpaceBased;

            TryComputeAltitudeDeg(target, out double targetAltDeg);
            // Airmass 1 rather than 0 or NaN: it is the value at which every relation downstream
            // that takes an airmass (extinction, the response's own integration) reduces exactly
            // to no atmosphere, so the space path runs the identical code rather than a parallel
            // one that has to be kept in step.
            double airmass = spaceBased
                ? 1.0
                : (targetAltDeg > 0.0 ? ImagingObservingConditions.AirmassAt(targetAltDeg) : double.PositiveInfinity);

            // Extinction at the fitted filter's own wavelength: a real site is far more
            // transparent in the red than in the blue, so a single grey coefficient made every
            // filter of an LRGB set lose exactly the same light, which they do not.
            float extinction = spaceBased
                ? 1f
                : (float)AtmosphericImagingNoise.ExtinctionTransmissionAt(
                      airmass, FilterCentralWavelengthMeters(Filter), Spec.SiteAltitudeMeters);

            double angularDiameterRad = AngularDiameterArcsec(target) * Math.PI / (180.0 * 3600.0);
            double scintSigma = spaceBased
                ? 0.0
                : AtmosphericImagingNoise.ScintillationExcessSigma(
                      Spec.ApertureMeters, Spec.SiteAltitudeMeters, airmass, exposureSeconds, angularDiameterRad);

            // The Sun's real altitude, handed straight to the sky model; twilight brightness is
            // a measured function of solar depression, not a normalised ramp between two limits.
            double sunAltDeg = 0.0;
            bool haveSunAlt = false;
            if (!spaceBased)
            {
                haveSunAlt = TryComputeAltitudeDeg(
                    Planetarium.fetch != null ? Planetarium.fetch.Sun : null, out sunAltDeg);
            }

            double moonSkyExcess = spaceBased ? 0.0 : ComputeMoonSkyExcess(target);
            float coverage = spaceBased ? 0f : ComputeCloudCoverage();

            // The instrument's whole spectral response for this filter and airmass, integrated
            // once here and then reused by every source in the frame, bodies, stars and sky
            // alike, which is what keeps them on one flux scale (see SystemBandpass).
            SystemResponse response = BuildSystemResponse(Filter, airmass);
            LastAirmass = airmass;
            LastEffectiveWidthAngstrom = response.EffectiveWidthAngstromFlat;

            double totalElectrons = target.IsBody
                ? ComputeCollectedElectrons(target.Body, response, 1.0, exposureSeconds)
                : 0.0;

            // Seeing is the site's own atmospheric term and nothing else. Cloud cover used to add
            // a blur here, and no longer does, for two independent reasons.
            //
            // It was quoted in PIXELS (a fixed 2px scaled by the plate scale), so the same
            // overcast sky delivered four times the angular blur at binning 4 as at binning 1,
            // exactly the defect this function was rewritten to remove from the seeing term.
            //
            // And correcting the unit would only have moved the problem: there is no published
            // coefficient relating cloud cover to delivered FWHM, because it is not an optical
            // mechanism. Cloud ATTENUATES, and that is modelled; CloudTransmission removes up
            // to CloudMaxAttenuation of every source's flux, from EVE's real cloud texture
            // sampled at the observatory's own zenith, and cloud VEILS, which is modelled too
            // (see CloudVeilingSkyGain). Bad seeing and cloud are correlated symptoms of unsettled
            // weather, not one causing the other, so a blur term here would have been an invented
            // constant standing in for a mechanism that does not exist.
            //
            // Only the plain ground-based term is resolved here, because only it needs the
            // target's airmass; the adaptive-optics solve is pure arithmetic and happens
            // off-thread.
            double seeingFwhmArcsec = spaceBased ? 0.0 : ComputeGroundSeeingFwhmArcsec(airmass);
            double defocusDiscRadiusPx = Autofocus ? 0.0 : Mathf.Abs(FocusOffset) * MaxDefocusBlurPx;

            var inputs = new FrameComputeInputs
            {
                TargetSeed = target.Seed,
                Ut = ut,
                ExposureSeconds = exposureSeconds,
                IsoGain = isoGain,
                Filter = Filter,
                ScintSigma = scintSigma,
                MoonSkyExcess = moonSkyExcess,
                CloudCoverage = coverage,
                TotalElectrons = totalElectrons,
                Response = response,
                PlateScaleArcsec = PlateScaleArcsecPerPixel,
                SeeingFwhmArcsec = seeingFwhmArcsec,
                DefocusDiscRadiusPx = defocusDiscRadiusPx,
                IsSpaceBased = spaceBased,
            };

            inputs.HasLineOfSight = TryResolveLineOfSight(target, out Vector3d frameLineOfSight);
            inputs.LineOfSight = frameLineOfSight;

            // A star is a point source, so it gets none of the extended-source scintillation
            // suppression a resolved disk enjoys; it is the same reason a planet looks steady to the
            // naked eye while a star of the same brightness twinkles.
            inputs.PointSourceScintSigma = spaceBased
                ? 0.0
                : AtmosphericImagingNoise.ScintillationExcessSigma(
                      Spec.ApertureMeters, Spec.SiteAltitudeMeters, airmass, exposureSeconds, 0.0);

            if (spaceBased) GatherSpacecraftPointing(ref inputs, exposureSeconds);

            if (spaceBased) GatherOrbitalSkyBackground(ref inputs, target);
            else GatherSkyBackground(ref inputs, targetAltDeg, sunAltDeg, haveSunAlt, coverage);

            GatherSkyField(ref inputs, target, exposureSeconds, airmass);

            return inputs;
        }

        /// <summary>
        /// The spacecraft's pointing budget for this exposure, and the wavefront error the
        /// instrument delivers on top of its own diffraction limit. Both end up as one Gaussian
        /// broadening per sub-band; see BuildSubBands and OpticalPsf.BuildKernel.
        /// </summary>
        private void GatherSpacecraftPointing(ref FrameComputeInputs inputs, float exposureSeconds)
        {
            SpacePlatformSpec platform = Spec.SpacePlatform;
            if (platform == null) return;

            var link = ObservingPlatform.ActiveSpaceTelescope;

            // A vehicle mid-repoint is turning, and that dominates every other term in the budget.
            // Handing the rate in as the drift the control system is failing to null is not a
            // special case: it is exactly what it is, and PointingStability already turns a rate
            // and an exposure into a streak.
            double slewRateArcsecPerSecond = link != null
                ? GroundStation.Readout(link).SlewRateDegPerSecond * 3600.0 : 0.0;

            if (link != null && link.Module != null)
            {
                inputs.Pointing = link.Module.EvaluatePointing(exposureSeconds, slewRateArcsecPerSecond);
            }
            else
            {
                // Unloaded vessel, or a preview taken before one is selected: the analytic path,
                // from the authority measured the last time the vessel WAS loaded.
                var pointingInputs = new PointingInputs
                {
                    Mode = link != null ? link.ControlMode : AttitudeControlMode.MomentumExchange,
                    ExposureSeconds = exposureSeconds,
                    InstrumentJitterArcsecRms = platform.PointingJitterArcsecRms,
                    DeadbandArcsec = platform.ThrusterDeadbandArcsec,
                    MinimumPulseSeconds = platform.MinimumControlPulseSeconds,
                    ControlTorqueNm = link != null ? link.ControlTorqueNm : 0.0,
                    InertiaKgM2 = link != null ? link.InertiaKgM2 : 0.0,
                    ResidualDriftArcsecPerSecond = slewRateArcsecPerSecond,
                };
                inputs.Pointing = PointingStability.Evaluate(in pointingInputs);
            }

            LastPointingBudget = inputs.Pointing;
        }

        /// <summary>
        /// The orbital sky: zodiacal light plus scattered planet light, and nothing else.
        ///
        /// The ground path's four terms all vanish here for the same reason, which is that each
        /// of them is made by an atmosphere. Airglow is emitted by one, twilight is scattered
        /// through one, moonlight reaches the detector by being scattered in one, and cloud
        /// veiling needs one to hold the cloud. What is left is the two terms that come from
        /// outside: interplanetary dust (ZodiacalLight) and the sunlit face of the planet the
        /// telescope is orbiting (Earthshine).
        /// </summary>
        private void GatherOrbitalSkyBackground(ref FrameComputeInputs inputs, SkyTarget target)
        {
            inputs.SkyElectronsPerPixel = 0.0;
            if (!TryBuildOrbitalConditions(target, out SpaceConditionsSnapshot snapshot)) return;

            LastSpaceConditions = snapshot;
            LastSkyBrightnessVMagPerArcsec2 = snapshot.SkyVMagPerArcsec2;

            double apertureAreaCm2 = EffectiveApertureAreaM2(Spec) * 1.0e4;

            // Both terms are scattered SUNLIGHT, so both are integrated with the solar spectral
            // shape, which is the same convention the ground path already uses for its own
            // scattered-sunlight terms (moonlight, twilight, zodiacal). Transmission is 1: there
            // is nothing between the sky and the aperture.
            double perSecond = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                snapshot.SkyVMagPerArcsec2, inputs.PlateScaleArcsec, inputs.Response,
                apertureAreaCm2, 1.0, SourceSpectra.SolarPhotosphereTemperatureK);

            inputs.SkyElectronsPerPixel = perSecond * inputs.ExposureSeconds;
        }

        /// <summary>Evaluates the orbital observing constraints for the current aim. False when no orbiting telescope is selected.</summary>
        internal bool TryBuildOrbitalConditions(SkyTarget target, out SpaceConditionsSnapshot snapshot)
        {
            snapshot = default(SpaceConditionsSnapshot);
            if (!ObservingPlatform.IsSpaceBased) return false;
            if (!TryResolveLineOfSight(target, out Vector3d lineOfSight)) return false;

            SkyVector los = ObservingPlatform.ToSky(lineOfSight);
            if (!ObservingPlatform.TryBuildContext(los, out SpaceObserverContext ctx)) return false;

            snapshot = SpaceObservingConditions.Evaluate(los, in ctx, Spec.SpacePlatform);
            return true;
        }

        /// <summary>
        /// The direction the telescope is looking, in world space: toward the target body's real
        /// position for a solar-system target, or along the catalogue direction for a fixed one.
        /// </summary>
        private bool TryResolveLineOfSight(SkyTarget target, out Vector3d direction)
        {
            direction = Vector3d.zero;
            if (!target.HasTarget) return false;

            if (target.IsBody)
            {
                if (target.Body == null) return false;
                CelestialBody home = FlightGlobals.GetHomeBody();
                Vector3d observer = ObservingPlatform.WorldPosition(home);
                Vector3d toBody = target.Body.position - observer;
                if (toBody.sqrMagnitude < 1.0) return false;
                direction = toBody.normalized;
                return true;
            }

            if (!TryEquatorialDirection(target.RaDeg, target.DecDeg, Planetarium.GetUniversalTime(),
                                        out Vector3d skyDirection)) return false;
            direction = skyDirection;
            return true;
        }

        /// <summary>
        /// The zodiacal light on this line of sight, V mag/arcsec^2, from Leinert Table 16.
        ///
        /// Shared by the ground and the orbital paths, because it is the same cloud seen from two
        /// places inside it: what differs between them is only the extinction the ground path
        /// then applies, and the fact that an orbiting telescope has nothing else left in its sky.
        ///
        /// Falls back to the ecliptic-pole constant when the frame cannot be resolved, which
        /// happens only for a home body with no orbit on record; SkyBrightnessModel's own comment
        /// on that constant explains why it is a fallback and not a model.
        /// </summary>
        private static double ComputeZodiacalVMagPerArcsec2(Vector3d lineOfSight, bool haveLineOfSight)
        {
            if (!haveLineOfSight) return SkyBrightnessModel.ZodiacalVMagPerArcsec2;

            CelestialBody home = FlightGlobals.GetHomeBody();
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (home == null || home.orbit == null || sun == null)
                return SkyBrightnessModel.ZodiacalVMagPerArcsec2;

            Vector3d observer = ObservingPlatform.WorldPosition(home);
            Vector3d sunFromObserver = sun.position - observer;
            if (sunFromObserver.sqrMagnitude < 1.0) return SkyBrightnessModel.ZodiacalVMagPerArcsec2;

            if (!EclipticFrame.TryCompute(ObservingPlatform.ToSky(lineOfSight),
                                          ObservingPlatform.ToSky(home.orbit.GetOrbitNormal()),
                                          ObservingPlatform.ToSky(sunFromObserver),
                                          out double latitudeDeg, out double longitudeDeg))
                return SkyBrightnessModel.ZodiacalVMagPerArcsec2;

            return ZodiacalLight.VMagPerArcsec2(longitudeDeg, latitudeDeg);
        }

        /// <summary>Zodiacal surface brightness the last capture ran under, V mag/arcsec^2, for the readout.</summary>
        public double LastZodiacalVMagPerArcsec2 { get; private set; } = double.NaN;

        /// <summary>The orbital constraints as of the last capture, for the readout. Only meaningful for a space telescope.</summary>
        public SpaceConditionsSnapshot LastSpaceConditions { get; private set; }

        /// <summary>The pointing budget the last capture ran under. Only meaningful for a space telescope.</summary>
        public PointingBudget LastPointingBudget { get; private set; }

        /// <summary>
        /// Total sky background over the exposure, in electrons per pixel.
        ///
        /// Every term is a real V surface brightness (see SkyBrightnessModel) summed as flux and
        /// then pushed through the same photometric chain as the sources sitting on it, instead
        /// of the per-pixel-per-second rates this used to carry. Those rates had no unit, could
        /// not be compared against a published sky-brightness measurement, and silently changed
        /// meaning whenever the plate scale did, so binning the sensor or fitting a Barlow
        /// altered how bright the night sky was.
        /// </summary>
        private void GatherSkyBackground(ref FrameComputeInputs inputs, double targetAltDeg,
                                         double sunAltDeg, bool haveSunAlt, float cloudCoverage)
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            double planetRadius = home != null ? home.Radius : 0.0;
            double zenithAngleDeg = 90.0 - targetAltDeg;

            double wavelength = FilterCentralWavelengthMeters(inputs.Filter);
            double airmass = targetAltDeg > 0.0 ? ImagingObservingConditions.AirmassAt(targetAltDeg) : double.PositiveInfinity;
            double transmission = AtmosphericImagingNoise.ExtinctionTransmissionAt(airmass, wavelength, Spec.SiteAltitudeMeters);

            // The sky is summed in two groups, because its terms do not share a spectrum. Three of
            // the four are sunlight scattered off something (the zodiacal dust cloud, the Moon,
            // and the daytime atmosphere itself), so they genuinely carry the solar spectral
            // shape. Airglow does not: it is atmospheric LINE emission, and it is no longer
            // integrated flat. ESO's measured sky model supplies its real spectrum (the [O I]
            // lines, sodium, the OH Meinel forest and the residual continuum), so a narrowband
            // filter now sees the sky IT sees: an H-alpha filter sits in a window between OH
            // bands, an [O I] 6300 filter stares straight at 150 rayleighs of the very line it is
            // trying to image. A flat sky made those two look equally easy. See Core/Airglow.
            //
            // Extinction is inside ThroughputAt (the response carries the field's own airmass) and
            // the van Rhijn factor is applied per bin inside Airglow, with the [O I] red doublet
            // on its own 250 km layer; so neither appears here, where applying either again
            // would double it.

            // Zodiacal light originates outside the atmosphere, so it is simply attenuated by it.
            //
            // Angle-resolved from Leinert Table 16 (see ZodiacalLight), not the flat polar
            // constant this used to carry. It is the same cloud and the same measurement the
            // orbital path uses; the only difference on the ground is the extinction factor
            // applied here, since the light has to come through the air. A ground frame taken
            // toward the ecliptic shortly after sunset genuinely sits on a brighter sky than one
            // taken at the pole, and the flat constant said otherwise by up to two magnitudes.
            double zodiacalVMag = ComputeZodiacalVMagPerArcsec2(inputs.LineOfSight, inputs.HasLineOfSight);
            double fluxSolar = Math.Pow(10.0, -0.4 * zodiacalVMag) * transmission;
            LastZodiacalVMagPerArcsec2 = zodiacalVMag;

            // Moonlight and twilight are both sunlight scattered WITHIN the atmosphere, so the
            // extinction along the line of sight is already part of the measured surface
            // brightness the model is calibrated against and is not applied again.
            fluxSolar = SkyBrightnessModel.AddMagnitude(fluxSolar, SkyBrightnessModel.MoonlightVMagPerArcsec2(inputs.MoonSkyExcess));
            if (haveSunAlt) fluxSolar = SkyBrightnessModel.AddMagnitude(fluxSolar, SkyBrightnessModel.TwilightVMagPerArcsec2(sunAltDeg));

            // Cloud veiling: cloud scatters ground and sky light back down, which is why an
            // overcast night sky is brighter than a clear one rather than darker. Modelled as a
            // multiplier on the sky that is already there, since that light is its source; so it
            // applies to both groups alike.
            double veiling = 1.0 + cloudCoverage * CloudVeilingSkyGain;
            fluxSolar *= veiling;

            // The response is used without extinction here and the transmission above is applied
            // per term instead, since each of the four is attenuated differently.
            double area = RealApertureAreaCm2();
            double nd = NdFilterTransmission(NdFilter);
            double airglowPerSecond = Airglow.ElectronsPerPixelPerSecond(
                inputs.Response, inputs.PlateScaleArcsec, area, zenithAngleDeg) * nd * veiling;
            double perSecond = airglowPerSecond
              + SkyBrightnessModel.ElectronsPerPixelPerSecond(
                    SkyBrightnessModel.FluxToMagPerArcsec2(fluxSolar),
                    inputs.PlateScaleArcsec, inputs.Response, area, nd,
                    SourceSpectra.SolarPhotosphereTemperatureK);

            inputs.SkyElectronsPerPixel = perSecond * inputs.ExposureSeconds;

            // The readout: what the airglow amounts to in this band, and how much of it is lines.
            LastAirglowRayleighsInBand = Airglow.RayleighsInBand(
                inputs.Response, zenithAngleDeg, out double lineShare) * veiling;
            LastAirglowLineShare = lineShare;

            // For the reported V surface brightness the airglow's own V-band equivalent stands in
            // for the retired flat term; the harness checks it reproduces the classical 21.7.
            double fluxFlat = Math.Pow(10.0, -0.4 * Airglow.VBandMagPerArcsec2(zenithAngleDeg)) * veiling;
            LastSkyBrightnessVMagPerArcsec2 = SkyBrightnessModel.FluxToMagPerArcsec2(fluxFlat + fluxSolar);
        }

        /// <summary>
        /// How much brighter cloud makes the sky, at full coverage. Cloud is lit from below by
        /// the same scattered light the clear sky already carries, so it is expressed as a gain
        /// on that rather than as an independent source: the pipeline has no ground-light model
        /// to derive an absolute cloud brightness from, and inventing one would be worse than
        /// scaling the term whose light the cloud is actually reflecting.
        /// </summary>
        private const double CloudVeilingSkyGain = 2.0;

        /// <summary>
        /// Builds the frame's sky geometry and gathers everything that will be drawn into it as
        /// a point source: catalogue stars, and any solar-system body the optics cannot resolve.
        ///
        /// All of it happens here, on the main thread, because it needs CelestialBody positions
        /// and the observatory's real orientation; what leaves is plain data the background pass
        /// can work on.
        /// </summary>
        private void GatherSkyField(ref FrameComputeInputs inputs, SkyTarget target,
                                    float exposureSeconds, double airmass)
        {
            inputs.HaveFieldGeometry = false;

            // Cleared BEFORE the geometry is attempted, not only overwritten after it succeeds. A
            // capture taken without a locked aim, or outside the scenes where the observatory site
            // exists, leaves TryBuildFieldGeometry with nothing to report; without this the frame
            // would silently inherit the PREVIOUS capture's pointing and export a header claiming a
            // direction it never looked in.
            LastWcs = default(Core.FitsWcs);
            LastFrameTrailed = false;
            LastTargetPixelX = double.NaN;
            LastTargetPixelY = double.NaN;
            LastTargetOffsetArcsec = double.NaN;
            LastTargetInFrame = false;
            LastSupernovae = null;
            LastSupernovaNoiseElectrons = 0.0;

            if (!TryBuildFieldGeometry(inputs.Ut, out GnomonicProjection projection,
                                       out double meridianRaDeg, out double latitudeDeg))
                return;

            CelestialBody home = FlightGlobals.GetHomeBody();
            double rotationPeriod = home != null && home.rotationPeriod > 0 ? home.rotationPeriod : 0.0;

            inputs.HaveFieldGeometry = true;
            inputs.Projection = projection;
            inputs.ObserverLatitudeDeg = latitudeDeg;

            // With the mount tracking, the sky is held still relative to the sensor and nothing
            // trails. Without it the sky turns underneath a fixed instrument, one full turn per
            // sidereal day of whatever world the observatory stands on, and every source in the
            // frame draws a streak. Modelling it as the sky's own rotation, rather than as a
            // sideways smear of the finished image, is what makes the streaks curve and makes
            // stars at the frame edge trail further than those at its centre (field rotation).
            //
            // The exposure ENDS at the moment the scene was rendered, so it integrates over the
            // interval leading up to it: the sky's position now is the end of every streak, and
            // the start is where it was one exposure earlier. Running the interval the other way
            // would put every trail on the wrong side of its source.
            inputs.EndMeridianRaDeg = meridianRaDeg;
            inputs.StartMeridianRaDeg = (Autoguiding || rotationPeriod <= 0.0)
                ? meridianRaDeg
                : meridianRaDeg - 360.0 * exposureSeconds / rotationPeriod;

            // The exported frame's pointing, measured from the very projection that places the
            // stars in it, so the header and the image cannot disagree. Referred to the START of
            // the exposure, which is what DATE-OBS timestamps.
            LastWcs = Core.FitsWcs.Build(projection, inputs.StartMeridianRaDeg, latitudeDeg);

            // Sight-line extinction toward the boresight, from the WCS's own reference point so
            // the header's pointing and its extinction cannot describe different directions.
            LastFieldReddeningEBv = DustMap != null
                ? DustMap.ReddeningAt(LastWcs.ReferenceRaDeg, LastWcs.ReferenceDecDeg)
                : double.NaN;
            LastFrameTrailed = inputs.EndMeridianRaDeg != inputs.StartMeridianRaDeg;

            inputs.ApertureAreaCm2 = RealApertureAreaCm2();

            // Extinction is integrated ACROSS the filter's passband inside Response rather than
            // sampled at its central wavelength: a site loses far more blue light than red, and on
            // the widest bands here (FORS2's 7700 Angstrom unfiltered position) the coefficient
            // varies threefold from one edge to the other, so a single sample cannot stand for the
            // band. Which is why the per-filter wavelength, bandwidth and single extinction figure
            // this struct used to carry are gone: the response holds all three, integrated, and
            // keeping duplicates of them here would let a caller reach for the sampled version by
            // accident. The sky background still needs a central-wavelength figure, and computes
            // its own locally, because its four terms are each attenuated differently.
            inputs.CloudTransmission = 1.0 - inputs.CloudCoverage * CloudMaxAttenuation;
            inputs.StarNonAtmosphericTransmission = inputs.CloudTransmission * NdFilterTransmission(NdFilter);

            inputs.SignalCutoffElectrons = BuildStarSignalFloor(inputs);

            // Drift first: the unresolved bodies gathered next trail along the same vector.
            inputs.DriftPixelX = 0.0;
            inputs.DriftPixelY = 0.0;
            if (inputs.EndMeridianRaDeg != inputs.StartMeridianRaDeg)
                ComputeFieldCentreDrift(inputs, projection, latitudeDeg,
                                        out inputs.DriftPixelX, out inputs.DriftPixelY);

            MeasurePointingError(target, projection, meridianRaDeg, latitudeDeg);

            inputs.FieldReddeningEBv = LastFieldReddeningEBv;
            GatherDispersionGeometry(ref inputs, projection, meridianRaDeg, latitudeDeg, target);
            inputs.Stars = SearchStarCatalog(inputs, projection, meridianRaDeg, latitudeDeg);
            inputs.Galaxies = SearchGalaxyCatalog(inputs, latitudeDeg);
            inputs.Supernovae = GatherSupernovae(inputs);
            inputs.UnresolvedBodies = GatherUnresolvedBodies(inputs, target, projection, exposureSeconds);
            inputs.TotalElectrons = ComputeSceneElectrons(inputs, target, projection, exposureSeconds);
        }

        /// <summary>
        /// Where the target actually lands in the finished frame, measured through the very
        /// projection that places the stars in it.
        ///
        /// The aim and the star field are built from one geometry, so this SHOULD be the frame
        /// centre. When it is not, this says by how much and in which direction, which separates
        /// "the telescope is pointed wrong" from "the target is simply not in the catalogue being
        /// drawn", two failures that look identical on a frame with nothing in it.
        /// </summary>
        private void MeasurePointingError(SkyTarget target, GnomonicProjection projection,
                                          double meridianRaDeg, double latitudeDeg)
        {
            LastTargetInFrame = false;
            LastTargetOffsetArcsec = double.NaN;
            // NaN rather than the last frame's value: a sub whose aim point did not project has no
            // registration reference, and AstroImageStack.HasRegistration tests exactly this, so
            // leaving a stale number here would register the stack on a position never measured.
            LastTargetPixelX = double.NaN;
            LastTargetPixelY = double.NaN;
            LastFieldWidthArcsec = projection.WidthPx * PlateScaleArcsecPerPixel;

            bool projected = false;
            double px = 0.0, py = 0.0;

            if (target.IsBody)
            {
                projected = TryProjectBody(target.Body, projection, out px, out py);
            }
            else if (target.IsEquatorial)
            {
                HorizontalCoordinates h = SkyCoordinates.EquatorialToHorizontal(
                    target.RaDeg, target.DecDeg, meridianRaDeg, latitudeDeg);
                projected = projection.TryProject(
                    SkyVector.FromHorizontal(h.AltitudeDeg, h.AzimuthDeg), out px, out py);
            }
            if (!projected) return;

            double dx = px - 0.5 * projection.WidthPx;
            double dy = py - 0.5 * projection.HeightPx;

            LastTargetPixelX = px;
            LastTargetPixelY = py;
            LastTargetOffsetArcsec = Math.Sqrt(dx * dx + dy * dy) * PlateScaleArcsecPerPixel;
            LastTargetInFrame = px >= 0.0 && px <= projection.WidthPx
                             && py >= 0.0 && py <= projection.HeightPx;
        }

        /// <summary>Where the target landed in the last frame, in pixels, and how far that is from the centre.</summary>
        public double LastTargetPixelX { get; private set; }
        public double LastTargetPixelY { get; private set; }
        public double LastTargetOffsetArcsec { get; private set; } = double.NaN;
        public bool LastTargetInFrame { get; private set; }

        /// <summary>Field of view across the sensor's long axis, arcsec. Quoted because at the extreme ends of this roster it is the whole explanation of what a frame contains.</summary>
        public double LastFieldWidthArcsec { get; private set; }

        /// <summary>
        /// Electron budget the rendered image is calibrated against: the target's own signal
        /// plus that of every other body large enough for the renderer to have drawn as a disk
        /// in the same frame.
        ///
        /// The renderer produces one image containing every body in view, but only one number
        /// can scale it. Using the target's electrons alone, which is what this used to do, meant a
        /// moon sharing the frame stole part of the target's budget and neither came out at its
        /// real brightness. Summing the resolved bodies fixes the frame's TOTAL, and leaves the
        /// renderer's own shading to decide how that total is divided between them; since the
        /// renderer already lights each body from the same Sun with its own albedo map, that
        /// division is close to right. Bodies too small to resolve are excluded here because
        /// they are drawn separately as point sources, so nothing is counted twice.
        /// </summary>
        private double ComputeSceneElectrons(FrameComputeInputs inputs, SkyTarget target,
                                             GnomonicProjection projection, float exposureSeconds)
        {
            // Extinction is inside inputs.Response, so only the cloud term is handed over here.
            double bodyTransmission = inputs.CloudTransmission;
            CelestialBody targetBody = target.Body;
            double total = target.IsBody
                ? ComputeCollectedElectrons(targetBody, inputs.Response, bodyTransmission, exposureSeconds)
                : 0.0;
            if (FlightGlobals.Bodies == null) return total;

            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || body == targetBody) continue;
                if (!IsResolvedByOptics(body, inputs.PlateScaleArcsec)) continue;
                if (!TryProjectBody(body, projection, out double px, out double py)) continue;
                if (px < 0.0 || px > projection.WidthPx || py < 0.0 || py > projection.HeightPx) continue;

                total += ComputeCollectedElectrons(body, inputs.Response, bodyTransmission, exposureSeconds);
            }
            return total;
        }

        /// <summary>
        /// The catalogue of stars drawn into photographs. Set once by the GUI at load time (see
        /// ExoInstrumentsGUI.LoadRenderedStarCatalog); null or empty simply means no star field,
        /// and every other part of the pipeline carries on unchanged.
        ///
        /// Deliberately NOT the Bright Star Catalogue the exoplanet instruments search: that one
        /// is small on purpose, so that hunting for a transit stays a tractable game, and nothing
        /// here touches it. See RenderedStarCatalog for why one catalogue cannot do both jobs.
        /// </summary>
        public static RenderedStarCatalog StarCatalog { get; set; }

        /// <summary>
        /// Optional all-sky reddening map, set by the GUI at load time. Null simply means no
        /// sight-line extinction is reported, and nothing else changes: this is the TOTAL Galactic
        /// column, so it describes what lies beyond the whole Galaxy and is never applied to a
        /// catalogue star; see DustMap.
        /// </summary>
        public static DustMap DustMap { get; set; }

        /// <summary>
        /// Optional all-sky emission-line map, set by the GUI at load time. Deposited only when the
        /// active filter's passband actually contains the map's line, which is what makes a
        /// narrowband frame show the gas and a broadband one drown it.
        /// </summary>
        public static EmissionMap EmissionMap { get; set; }

        /// <summary>
        /// Optional high-resolution patches of the same line over the sky where a finer survey
        /// exists, set by the GUI at load time. Null simply means every field is drawn at the
        /// all-sky map's own 6 arcmin beam; see EmissionPatchSet for why that is the difference
        /// between a nebula and a smudge.
        /// </summary>
        public static EmissionPatchSet EmissionPatches { get; set; }

        /// <summary>Name of the high-resolution patch the last frame was drawn from, or null when the base map answered.</summary>
        public string LastEmissionPatchName { get; private set; }

        /// <summary>Lines this frame took from a MEASURED plane rather than from the ratio model, or null.</summary>
        public string LastEmissionMeasuredLines { get; private set; }

        // The registration reference a stack aligns on used to be exposed here as
        // LastRegistrationX/Y, aliases onto the live LastTargetPixelX/Y. It now travels with the
        // rest of the frame's geometry in LastCaptureGeometry, because reading it live is what
        // handed a collected sub the NEXT exposure's pointing; see CapturedFrameGeometry.

        /// <summary>Fraction of the last frame the high-resolution patch answered for; the rest came from the all-sky map, joined by the patch's own apodised rim.</summary>
        public double LastEmissionPatchCoverage { get; private set; }

        /// <summary>Resolution the last frame's emission actually came at, arcminutes: the patch's when one covered the field, the base map's otherwise.</summary>
        public double LastEmissionResolutionArcmin { get; private set; }

        /// <summary>
        /// Optional galaxy catalogue, set by the GUI at load time. Unlike a star, a galaxy is
        /// resolved by every instrument here, so it is drawn from its own measured shape rather
        /// than by the PSF; see GalaxyCatalog and GalaxyRenderer.
        /// </summary>
        public static GalaxyCatalog GalaxyCatalog { get; set; }

        /// <summary>
        /// Optional measured shape maps, set by the GUI at load time. Where one exists it replaces
        /// the analytic profile entirely: the galaxy is drawn from a real image of itself, at the
        /// catalogue's own brightness. See GalaxyImageSet and tools/pack_galaxy_images.py.
        /// </summary>
        public static GalaxyImageSet GalaxyImages { get; set; }

        /// <summary>The packed spectral templates, or null when the file is absent (supernovae then simply never occur).</summary>
        public static SupernovaTemplateSet SupernovaTemplates { get; set; }

        /// <summary>Per-save seed the deterministic supernova history is generated from. Zero disables the model.</summary>
        public static long SupernovaSeed { get; set; }

        /// <summary>Supernovae the LAST capture had in its field, with the electrons the frame really gave them. For the discovery check.</summary>
        public List<SupernovaSighting> LastSupernovae { get; private set; }

        /// <summary>Per-pixel 1-sigma noise of that frame (sky + dark shot noise, read noise), the denominator of the 5-sigma discovery rule.</summary>
        public double LastSupernovaNoiseElectrons { get; private set; }

        /// <summary>Galaxies drawn into the last frame, and the electrons they contributed. Diagnostics, read after a capture.</summary>
        public int LastGalaxiesDrawn { get; private set; }
        public double LastGalaxyElectrons { get; private set; }
        /// <summary>How many of the last frame's galaxies were drawn from a real image rather than from a Sersic profile.</summary>
        public int LastGalaxiesFromImages { get; private set; }
        /// <summary>Sampling of the coarsest shape map used in the last frame, arcseconds per map pixel, or NaN when none was. Against the plate scale this says whether the structure on screen is the survey's or an interpolation of it.</summary>
        public double LastGalaxyMapSamplingArcsec { get; private set; } = double.NaN;
        /// <summary>How many of those had no catalogued colour, so their band conversion used the mean colour of their morphological type instead of a measured one.</summary>
        public int LastGalaxiesWithModelledColour { get; private set; }

        /// <summary>Mean line surface brightness the last capture collected, rayleighs, or NaN when no map contributed.</summary>
        public double LastEmissionRayleighs { get; private set; } = double.NaN;

        /// <summary>Electrons the brightest pixel of that diffuse emission collected this exposure. Against the full well it says whether a linear stretch could ever show it, which for a nebula is usually no.</summary>
        public double LastEmissionPeakElectrons { get; private set; } = double.NaN;

        /// <summary>Which lines the last frame's filter actually admitted, e.g. "[N II] 6548, H-alpha, [N II] 6584" for a 7 nm H-alpha filter. Null when none did.</summary>
        public string LastEmissionLines { get; private set; }

        /// <summary>Mean electron temperature the forbidden-line ratios were taken at, kelvin. The one modelled quantity between the H-alpha map and the other lines, so it is reported rather than buried.</summary>
        public double LastEmissionTemperatureK { get; private set; } = double.NaN;

        /// <summary>Total Galactic E(B-V) toward the last capture's field centre, or NaN with no map installed.</summary>
        public double LastFieldReddeningEBv { get; private set; } = double.NaN;

        /// <summary>Sky surface brightness (V mag/arcsec^2) behind the last capture: the number a real observer would quote for the conditions. Higher is darker.</summary>
        public double LastSkyBrightnessVMagPerArcsec2 { get; private set; }

        /// <summary>Number of catalogue stars actually drawn into the last capture.</summary>
        public int LastStarsDrawn => lastStarsDrawnInternal;

        /// <summary>Quadratures the reddening cache ran for the last capture. Zero when no star in the field carries a reddening estimate.</summary>
        public int LastReddeningQuadratures => lastReddeningQuadratures;
        private int lastReddeningQuadratures;

        /// <summary>Limiting V magnitude of the last capture: the faintest star that rose above its noise floor.</summary>
        public double LastLimitingVMag { get; private set; }

        /// <summary>
        /// Builds the frame's sky geometry from the camera's OWN axes.
        ///
        /// The chain is: the telescope's aim is a real direction in the game's world; the
        /// observatory's local north/east/up turn that into an altitude and azimuth; and
        /// SkyCoordinates turns those into the right ascension and declination the catalogue is
        /// indexed by. Deriving the frame this way rather than from an assumed orientation is
        /// what guarantees the star field lines up with the rendered planet, since both come from the
        /// same three axes.
        ///
        /// The local basis is read from KSP's own latitude/longitude convention, by asking the
        /// home body where a point slightly north and slightly east of the observatory is, rather
        /// than from cross products of a rotation axis, because Unity's left-handed frame makes the
        /// sign of such a product easy to get backwards and impossible to notice, and this form
        /// simply cannot be wrong about which way east is.
        /// </summary>
        private bool TryBuildFieldGeometry(double ut, out GnomonicProjection projection,
                                           out double meridianRaDeg, out double latitudeDeg)
        {
            projection = default(GnomonicProjection);
            meridianRaDeg = 0.0;
            latitudeDeg = 0.0;
            haveSiteBasis = false;

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || !hasLockedAim) return false;

            if (!TryBuildSiteBasis(out Vector3d north, out Vector3d east, out Vector3d up,
                                   out latitudeDeg, out double longitudeDeg)) return false;

            // Held for the rest of the gather pass: every body projected into this frame is
            // resolved against the same basis, and it does not change within one capture.
            siteNorth = north;
            siteEast = east;
            siteUp = up;
            haveSiteBasis = true;

            // Scaled space is a uniform scaling of the world about a moving origin, so a
            // DIRECTION is identical in both frames, which is why the camera's axes, held in
            // scaled space, can be resolved against a local basis built in world space.
            SkyVector boresight = ToLocalBasis(lockedLook * Vector3.forward, north, east, up);
            SkyVector frameUp = ToLocalBasis(lockedLook * Vector3.up, north, east, up);
            SkyVector frameRight = ToLocalBasis(lockedLook * Vector3.right, north, east, up);

            float fov = Mathf.Clamp(FovDeg, MinFovDeg, MaxFovDeg);
            projection = new GnomonicProjection(boresight, frameUp, frameRight, fov, TextureWidth, TextureHeight);

            meridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                ut, home.rotationPeriod, home.initialRotation, longitudeDeg);
            return true;
        }

        /// <summary>Component of v perpendicular to axis, normalised. Zero-length when v is parallel to axis.</summary>
        private static Vector3d Orthonormalize(Vector3d v, Vector3d axis)
        {
            Vector3d perpendicular = v - axis * Vector3d.Dot(v, axis);
            double magnitude = perpendicular.magnitude;
            return magnitude < 1e-9 ? Vector3d.zero : perpendicular / magnitude;
        }

        /// <summary>Resolves a world direction into the observatory's (north, east, up) basis, the one SkyVector.FromHorizontal works in.</summary>
        private static SkyVector ToLocalBasis(Vector3 direction, Vector3d north, Vector3d east, Vector3d up)
        {
            Vector3d d = direction;
            return SkyVector.Normalized(Vector3d.Dot(d, north), Vector3d.Dot(d, east), Vector3d.Dot(d, up));
        }

        /// <summary>
        /// Fills the frame from an all-sky emission-line map.
        ///
        /// This is the one source that is drawn FROM the sky rather than placed ON it, so every
        /// pixel has to ask what lies behind it. Two things keep that affordable without
        /// approximating anything: it does nothing unless a map is loaded AND the active filter
        /// admits at least one line, so a frame that cannot see gas costs zero; and the
        /// (north, east, up) to Galactic chain is one rotation for the whole frame, built once
        /// (HorizontalToGalactic) rather than six trigonometric calls per pixel.
        ///
        /// EVERY LINE THE FILTER ADMITS, not just the mapped one. The map measures H-alpha, but a
        /// 7 nm filter centred on it also passes [N II] 6548 and 6584, and an [S II] filter passes
        /// a doublet the map says nothing about directly. Those are derived from the H-alpha
        /// brightness by the physics that sets them; see NebularLineRatios, where the ratio is a
        /// thermometer rather than a coefficient. Each admitted line gets its own throughput at its
        /// own wavelength, which is the whole point of narrowband: a 3 nm filter separates H-alpha
        /// from [N II] 6584 and a 7 nm one does not.
        ///
        /// Deposited into the SIGNAL plane, before the optics, because that is where sky light
        /// enters. At this resolution the convolution barely changes it, which is a property of the
        /// data rather than a reason to skip a step.
        /// </summary>
        private void DepositEmissionField(float[] signal, FrameComputeInputs inputs)
        {
            LastEmissionRayleighs = double.NaN;
            LastEmissionPeakElectrons = double.NaN;
            LastEmissionLines = null;
            LastEmissionPatchName = null;
            LastEmissionResolutionArcmin = 0.0;

            EmissionMap map = EmissionMap;
            if (map == null || !map.IsLoaded || !inputs.HaveFieldGeometry || signal == null) return;
            if (inputs.Response == null) return;

            // THE PATCHES FIRST, because which lines this frame can render depends on which of
            // them are MEASURED here, and that is a property of the patch.
            EmissionPatchSet patchSetEarly = EmissionPatches;
            List<EmissionPatchSet.Patch> patchesHere = null;
            if (patchSetEarly != null && patchSetEarly.IsLoaded)
            {
                // The field radius needs the WCS, which GatherSkyField has already built.
                double radiusDeg = 0.5 * Math.Sqrt((double)TextureWidth * TextureWidth
                                                 + (double)TextureHeight * TextureHeight)
                                 * inputs.PlateScaleArcsec / 3600.0;
                patchesHere = patchSetEarly.FindOverlappingPatches(
                    LastWcs.ReferenceRaDeg, LastWcs.ReferenceDecDeg, radiusDeg);
                if (patchesHere.Count == 0) patchesHere = null;
            }

            // EVERY LINE THIS FRAME COULD CARRY: the ones derivable from H-alpha, plus the ones a
            // patch here actually measures. The second half is not optional. DerivableLines is by
            // construction the list of lines that FOLLOW from an H-alpha map, and [O III] is
            // deliberately absent from it (NebularLineRatios explains why deriving it would be
            // inventing a sky). Iterating over that list alone meant an [O III] filter admitted
            // nothing, returned before the patch was ever consulted, and rendered an empty frame
            // even with a measured [O III] plane sitting under the field.
            var candidates = new List<EmissionLines.Line>(NebularLineRatios.DerivableLines);
            if (patchesHere != null)
            {
                foreach (EmissionPatchSet.Patch patch in patchesHere)
                {
                    if (patch.ExtraWavelengthMeters == null) continue;
                    for (int i = 0; i < patch.ExtraWavelengthMeters.Length; i++)
                    {
                        EmissionLines.Line measuredLine = EmissionLines.Nearest(patch.ExtraWavelengthMeters[i]);
                        if (measuredLine.WavelengthMeters <= 0.0) continue;
                        bool known = false;
                        foreach (EmissionLines.Line c in candidates)
                            if (Math.Abs(c.WavelengthMeters - measuredLine.WavelengthMeters) < 1e-12) { known = true; break; }
                        if (!known) candidates.Add(measuredLine);
                    }
                }
            }

            // Which of them this filter admits, and what each is worth per rayleigh. ThroughputAt
            // is zero outside the passband, so the admission test and the coefficient are one call.
            var lines = new List<EmissionLines.Line>();
            var coefficients = new List<double>();
            double exposureTransmission = inputs.ExposureSeconds
                                        * Math.Max(0.0, inputs.StarNonAtmosphericTransmission);
            foreach (EmissionLines.Line line in candidates)
            {
                double throughput = inputs.Response.ThroughputAt(line.WavelengthMeters);
                if (!(throughput > 0.0)) continue;
                double perRayleigh = EmissionLines.ElectronsPerPixelPerSecond(
                    1.0, inputs.PlateScaleArcsec, inputs.ApertureAreaCm2, throughput) * exposureTransmission;
                if (!(perRayleigh > 0.0)) continue;
                lines.Add(line);
                coefficients.Add(perRayleigh);
            }
            if (lines.Count == 0) return;

            var rotation = HorizontalToGalactic.Build(inputs.EndMeridianRaDeg, inputs.ObserverLatitudeDeg);
            if (!rotation.IsValid) return;

            var names = new string[lines.Count];
            for (int i = 0; i < lines.Count; i++) names[i] = lines[i].Name;
            LastEmissionLines = string.Join(", ", names);

            int w = TextureWidth, h = TextureHeight;

            // The high-resolution layer, if one covers this field. Resolved once: a frame cannot
            // span two patches, and asking per pixel would be a hundred dot products for an answer
            // that does not change.
            EmissionPatchSet patchSet = patchSetEarly;
            List<EmissionPatchSet.Patch> patchList = patchesHere;
            if (patchList != null)
            {
                var patchNames = new string[patchList.Count];
                for (int i = 0; i < patchList.Count; i++) patchNames[i] = patchList[i].Name;
                LastEmissionPatchName = string.Join(" + ", patchNames);
            }
            else LastEmissionPatchName = null;
            // The FINEST patch over the field, not the set's default: patches carry their own
            // resolution now, and a northern one is four times finer than a southern one.
            if (patchList != null)
            {
                double finest = double.MaxValue;
                foreach (EmissionPatchSet.Patch p in patchList)
                    finest = Math.Min(finest, EmissionPatchSet.PatchResolutionArcmin(p));
                LastEmissionResolutionArcmin = finest < double.MaxValue ? finest : patchSet.ResolutionArcmin;
            }
            else LastEmissionResolutionArcmin = map.ResolutionArcmin;

            // ONE SAMPLE PER NATIVE PIXEL, NOT PER BINNED PIXEL. Binning here is charge-domain
            // summing, which is what FullWellElectrons and the dark-current terms already assume:
            // BinningFactor^2 physical pixels collect light over their own solid angles and their
            // charge is added before readout. Reading the map once at the binned pixel's centre
            // models something else -- a detector with one big pixel that samples the sky at a
            // point -- and at 4x4 that point stands for 15.3 arcsec of sky whose surface brightness
            // was measured on 0.86 arcmin cells. The average over the native sub-pixels IS the
            // integral the sensor performs, so this costs the same number of map lookups as a 1x1
            // frame whatever the binning, and collapses to the old single sample at 1x1.
            //
            // It is also why this loop is the most expensive thing in a capture and why it is the
            // one that is spread across cores: the sample count is the sensor's NATIVE pixel
            // count, 11.7 million on the RC20, whatever binning the observer chose.
            int bin = Math.Max(1, BinningFactor);
            double subStep = 1.0 / bin;
            double subCount = bin * bin;

            // Per-ROW accumulators, summed afterwards in row order. The frame itself is written
            // per pixel and no two rows share one, so the picture is identical however the rows
            // were divided; the reported means would not be, if the threads added into one total
            // in whatever order they finished. See ParallelWork.
            var rowRayleighs = new double[h];
            var rowTemperature = new double[h];
            var rowBrightest = new double[h];
            var rowCounted = new long[h];
            var rowPatchSamples = new double[h];

            var linesArray = lines.ToArray();
            var coefficientsArray = coefficients.ToArray();

            // WHICH ADMITTED LINES THIS FIELD HAS A MEASUREMENT FOR, resolved once. A patch packed
            // from NSNS carries [O III] and [S II] planes beside its H-alpha; SHASSA's carry only
            // H-alpha. Where a plane exists the frame uses the MEASURED line, and the ratio model
            // is not consulted at all: NebularLineRatios derives the forbidden lines from a warm
            // ionised medium relation (Haffner, Reynolds & Tufte 1999) that a supernova remnant's
            // shocks do not obey, and [O III] it declines to derive at all, by design. A measured
            // plane settles both cases with data.
            //
            // -1 means no plane and the derived ratio answers, which is every southern patch and
            // every field with no patch at all: unchanged behaviour where there is nothing new.
            int[][] planeForLine = null;
            if (patchList != null)
            {
                planeForLine = new int[patchList.Count][];
                var measured = new List<string>();
                for (int pi = 0; pi < patchList.Count; pi++)
                {
                    planeForLine[pi] = new int[linesArray.Length];
                    for (int i = 0; i < linesArray.Length; i++)
                    {
                        planeForLine[pi][i] = patchList[pi].PlaneFor(linesArray[i].WavelengthMeters);
                        if (planeForLine[pi][i] >= 0 && !measured.Contains(linesArray[i].Name))
                            measured.Add(linesArray[i].Name);
                    }
                }
                LastEmissionMeasuredLines = measured.Count > 0 ? string.Join(", ", measured) : null;
            }
            else LastEmissionMeasuredLines = null;

            Action<int, EmissionScratch> fillRow = (y, scratch) =>
            {
                long[] pixelScratch = scratch.Pixels;
                double[] weightScratch = scratch.Weights;
                EmissionPatchSet.Cursor patchCursor = scratch.Cursor;

                double rowSum = 0.0, rowPeak = 0.0, rowTemp = 0.0, rowPatch = 0.0;
                long rowCount = 0;

                for (int x = 0; x < w; x++)
                {
                    double rSum = 0.0;
                    int rCount = 0;
                    int patchSamples = 0;

                    // Measured planes accumulate beside H-alpha, on the same sub-pixel grid.
                    double[] measuredSum = scratch.MeasuredSum;
                    int[] measuredCount = scratch.MeasuredCount;
                    if (measuredSum != null)
                        for (int i = 0; i < measuredSum.Length; i++) { measuredSum[i] = 0.0; measuredCount[i] = 0; }

                    for (int sy = 0; sy < bin; sy++)
                    for (int sx = 0; sx < bin; sx++)
                    {
                        SkyVector direction = inputs.Projection.Deproject(
                            x + (sx + 0.5) * subStep, y + (sy + 0.5) * subStep);
                        rotation.ToGalactic(direction, out double l, out double b);

                        // The patch carries total surface brightness, apodised into the base map at
                        // its own edge, so it substitutes rather than adds. Any sample it cannot
                        // answer for falls through to the base map.
                        double sample = double.NaN;
                        bool fromPatch = false;
                        if (patchList != null)
                        {
                            for (int pi = 0; pi < patchList.Count; pi++)
                            {
                                if (!patchSet.TryRayleighsAtGalactic(patchList[pi], pi, l, b,
                                        pixelScratch, weightScratch, ref patchCursor, out sample)) continue;
                                fromPatch = true;
                                patchSamples++;

                                // The same position on whatever forbidden-line planes this patch
                                // carries for the filter's admitted lines.
                                if (measuredSum != null)
                                {
                                    for (int i = 0; i < linesArray.Length; i++)
                                    {
                                        int plane = planeForLine[pi][i];
                                        if (plane < 0) continue;
                                        if (!patchSet.TryRayleighsAtGalactic(patchList[pi], pi, plane, l, b,
                                                pixelScratch, weightScratch, ref patchCursor, out double lv)) continue;
                                        measuredSum[i] += lv;
                                        measuredCount[i]++;
                                    }
                                }
                                break;
                            }
                        }
                        if (!fromPatch) sample = map.RayleighsAtGalactic(l, b, pixelScratch, weightScratch);
                        if (double.IsNaN(sample)) continue;
                        rSum += sample;
                        rCount++;
                    }

                    if (rCount == 0) continue;
                    double r = rSum / rCount;
                    rowPatch += patchSamples / subCount;
                    rowCount++;
                    if (!(r > 0.0)) continue;

                    // One temperature per pixel, from that pixel's own H-alpha brightness, and
                    // every admitted line's ratio taken at it. That is what makes a bright H II
                    // region come out H-alpha dominated and the faint diffuse gas [N II] rich,
                    // which is the WIM's most robust measured property rather than a stylistic
                    // choice. Solved ONCE per pixel and read per line: the ratios are functions of
                    // that one temperature, so asking each line to derive it again was the same
                    // logarithm and the same two exponentials repeated five times over.
                    var ratios = new NebularLineRatios.RatioSet(r);
                    rowTemp += ratios.ElectronTemperatureK;

                    double pixelRayleighs = 0.0, pixelElectrons = 0.0;
                    for (int i = 0; i < linesArray.Length; i++)
                    {
                        double lineR;
                        if (measuredSum != null && measuredCount[i] > 0)
                        {
                            lineR = measuredSum[i] / measuredCount[i];   // measured beats derived
                        }
                        else
                        {
                            double ratio = ratios.RatioToHalpha(linesArray[i]);
                            if (double.IsNaN(ratio) || !(ratio > 0.0)) continue;
                            lineR = r * ratio;
                        }
                        if (!(lineR > 0.0)) continue;
                        pixelRayleighs += lineR;
                        pixelElectrons += lineR * coefficientsArray[i];
                    }

                    rowSum += pixelRayleighs;
                    if (pixelElectrons > rowPeak) rowPeak = pixelElectrons;
                    if (pixelElectrons > 0.0) signal[y * w + x] += (float)pixelElectrons;
                }

                scratch.Cursor = patchCursor;
                rowRayleighs[y] = rowSum;
                rowTemperature[y] = rowTemp;
                rowBrightest[y] = rowPeak;
                rowCounted[y] = rowCount;
                rowPatchSamples[y] = rowPatch;
            };

            int patchCount = patchList != null ? patchList.Count : 1;
            int measuredLines = LastEmissionMeasuredLines != null ? linesArray.Length : 0;
            if (ParallelWork.Worthwhile((long)w * h * bin * bin))
            {
                Parallel.For(0, h, ParallelWork.Options,
                    () => new EmissionScratch(patchCount, measuredLines),
                    (y, state, scratch) => { fillRow(y, scratch); return scratch; },
                    scratch => { });
            }
            else
            {
                var scratch = new EmissionScratch(patchCount, measuredLines);
                for (int y = 0; y < h; y++) fillRow(y, scratch);
            }

            double sum = 0.0, brightest = 0.0, temperatureSum = 0.0, patchSubSamples = 0.0;
            long counted = 0;
            for (int y = 0; y < h; y++)
            {
                sum += rowRayleighs[y];
                temperatureSum += rowTemperature[y];
                if (rowBrightest[y] > brightest) brightest = rowBrightest[y];
                counted += rowCounted[y];
                patchSubSamples += rowPatchSamples[y];
            }

            LastEmissionRayleighs = counted > 0 ? sum / counted : double.NaN;
            LastEmissionPeakElectrons = counted > 0 ? brightest : double.NaN;
            LastEmissionTemperatureK = counted > 0 ? temperatureSum / counted : double.NaN;
            LastEmissionPatchCoverage = patchSubSamples / Math.Max(1, (long)w * h);
        }

        /// <summary>
        /// One worker's private buffers for the emission fill: the interpolation stencil and the
        /// run cursors into whatever patches the field overlaps.
        ///
        /// Private per worker rather than shared, because the cursor is written on every lookup
        /// (it remembers which run of the patch the last tap fell in, which is what keeps a
        /// neighbouring pixel from paying a binary search). Sharing one would be a data race for
        /// no gain; the buffers are a few hundred bytes each.
        /// </summary>
        private sealed class EmissionScratch
        {
            public readonly long[] Pixels;
            public readonly double[] Weights;
            public EmissionPatchSet.Cursor Cursor;

            /// <summary>Per-line accumulators for the measured forbidden-line planes. Null when no patch in the field carries any, which costs nothing on the ordinary path.</summary>
            public readonly double[] MeasuredSum;
            public readonly int[] MeasuredCount;

            public EmissionScratch(int patchCount, int lineCount)
            {
                EmissionMap.AllocateScratch(out Pixels, out Weights);
                Cursor = EmissionPatchSet.Cursor.New(patchCount);
                if (lineCount > 0)
                {
                    MeasuredSum = new double[lineCount];
                    MeasuredCount = new int[lineCount];
                }
            }
        }

        /// <summary>Dispersion across the active filter's passband before any corrector, arcseconds.</summary>
        private double RawDispersionSmearArcsec(FrameComputeInputs inputs)
        {
            double centre = FilterCentralWavelengthMeters(inputs.Filter);
            double bandwidth = FilterBandwidthAngstrom(inputs.Filter) * 1e-10;
            if (!(centre > 0.0) || !(bandwidth > 0.0)) return 0.0;
            double lo = Math.Max(300e-9, centre - 0.5 * bandwidth);
            double hi = Math.Min(1100e-9, centre + 0.5 * bandwidth);
            double smear = AtmosphericRefraction.DifferentialRefractionArcsec(
                lo * 1e6, hi * 1e6, inputs.ZenithDistanceDeg,
                inputs.AirTemperatureCelsius, inputs.AirPressureMillibar,
                inputs.WaterVapourPressureMillibar);
            return double.IsNaN(smear) ? 0.0 : Math.Abs(smear);
        }

        /// <summary>
        /// Splits the active filter into sub-bands with their photon weights and their dispersion
        /// offsets, or null when there is nothing chromatic to do.
        ///
        /// The source spectrum used for the weights is a 6000 K blackbody: the FIELD's dispersion
        /// smear is one kernel shared by everything in the frame, so it has to be built on one
        /// spectrum, and a solar-type continuum is the middle of what this roster photographs. The
        /// error that leaves is second order (a redder source's smear is slightly shorter) while
        /// the first-order effect, the shift of a source's own centroid with its colour, is a
        /// per-source scalar and belongs where the source is deposited rather than in a shared kernel.
        /// </summary>
        private ChromaticSubBand[] BuildSubBands(FrameComputeInputs inputs, double centralWavelength)
        {
            if (inputs.Response == null || !(inputs.PlateScaleArcsec > 0.0)) return null;
            if (inputs.ZenithUnitX == 0.0 && inputs.ZenithUnitY == 0.0) return null;

            double bandwidth = FilterBandwidthAngstrom(Filter) * 1e-10;
            if (!(bandwidth > 0.0)) return null;

            // A little past the nominal edges, because a real filter's transmission does not stop
            // dead there and the roll-off carries a real share of the photons.
            double lo = Math.Max(300e-9, centralWavelength - 0.75 * bandwidth);
            double hi = Math.Min(1100e-9, centralWavelength + 0.75 * bandwidth);
            if (!(hi > lo)) return null;

            // A corrector cancels most of the dispersion but not all of it.
            double residual = Spec.HasAtmosphericDispersionCorrector
                ? VisualTelescopeSpec.AtmosphericDispersionResidual : 1.0;

            var bands = AtmosphericRefraction.SplitPassband(
                inputs.Response,
                l => Colorimetry.PlanckSpectralRadiance(l * 1e9, SubBandReferenceTemperatureK) * l,
                lo, hi, ChromaticSubBandCount,
                inputs.ZenithDistanceDeg, inputs.PlateScaleArcsec,
                inputs.ZenithUnitX * residual, inputs.ZenithUnitY * residual,
                centralWavelength,
                inputs.AirTemperatureCelsius, inputs.AirPressureMillibar,
                inputs.WaterVapourPressureMillibar);
            return bands;
        }

        /// <summary>
        /// The orbital counterpart of BuildSubBands: the same split of the passband, with the
        /// dispersion offsets gone and a Gaussian width per sub-band in their place.
        ///
        /// TWO INDEPENDENT GAUSSIANS, SUMMED IN QUADRATURE.
        ///
        ///   * The instrument's own residual WAVEFRONT ERROR. This is not computed, it is
        ///     inverted out of the instrument's published delivered widths: at each sub-band's
        ///     wavelength, look up what the observatory says the telescope actually delivers, and
        ///     solve for the Gaussian which, convolved with this pupil's real diffraction pattern,
        ///     reproduces it (OpticalPsf.GaussianFwhmForDelivered). So the finished frame
        ///     reproduces the published table by construction, and a telescope with no such table
        ///     stays diffraction-limited rather than being given an invented figure.
        ///   * The spacecraft's POINTING excursion over the exposure, achromatic, from
        ///     PointingStability.
        ///
        /// They are independent random displacements of the same image, so their variances add,
        /// which for Gaussians is the quadrature sum of their widths. This is the case where
        /// quadrature is legitimate; see PointingStability.TotalPointingRmsArcsec for why, and
        /// OpticalPsf.AtmosphericFwhmForDelivered for a case in this codebase where it is not.
        ///
        /// Weights come from the same 6000 K continuum the ground path uses, for the same reason:
        /// one kernel is shared by every source in the frame, so it has to be built on one
        /// spectrum.
        /// </summary>
        private ChromaticSubBand[] BuildSpaceSubBands(FrameComputeInputs inputs, double centralWavelength,
                                                      double pointingFwhmArcsec)
        {
            if (inputs.Response == null || !(inputs.PlateScaleArcsec > 0.0)) return null;

            SpectralCurve delivered = Spec.SpacePlatform != null
                ? Spec.SpacePlatform.DeliveredPsfFwhmArcsec : null;

            // Nothing chromatic and nothing to broaden: let the caller take the plain
            // monochromatic path rather than build twelve identical kernels.
            if (delivered == null && !(pointingFwhmArcsec > 0.0)) return null;

            double bandwidth = FilterBandwidthAngstrom(Filter) * 1e-10;
            if (!(bandwidth > 0.0)) return null;

            double lo = Math.Max(150e-9, centralWavelength - 0.75 * bandwidth);
            double hi = Math.Min(1200e-9, centralWavelength + 0.75 * bandwidth);
            if (!(hi > lo)) return null;

            // Zero zenith geometry: there is no atmosphere to disperse anything, so every offset
            // SplitPassband computes is multiplied by a zero direction and the sub-bands stack
            // concentrically, which is exactly what a space telescope's kernel should do.
            ChromaticSubBand[] bands = AtmosphericRefraction.SplitPassband(
                inputs.Response,
                l => Colorimetry.PlanckSpectralRadiance(l * 1e9, SubBandReferenceTemperatureK) * l,
                lo, hi, ChromaticSubBandCount,
                0.0, inputs.PlateScaleArcsec,
                0.0, 0.0,
                centralWavelength,
                0.0, 0.0, 0.0);
            if (bands == null) return null;

            for (int i = 0; i < bands.Length; i++)
            {
                if (!(bands[i].Weight > 0.0)) continue;

                double wavefront = 0.0;
                if (delivered != null)
                {
                    double lambdaM = bands[i].WavelengthMeters;
                    double deliveredFwhm = delivered.At(lambdaM);
                    if (deliveredFwhm > 0.0)
                    {
                        wavefront = OpticalPsf.GaussianFwhmForDelivered(
                            deliveredFwhm, inputs.PlateScaleArcsec, PupilApertureMeters,
                            PupilObstructionFraction, lambdaM,
                            PupilVaneCount, PupilVaneWidthMeters);
                    }
                }

                bands[i].GaussianFwhmArcsec =
                    Math.Sqrt(wavefront * wavefront + pointingFwhmArcsec * pointingFwhmArcsec);
                bands[i].OffsetX = 0.0;
                bands[i].OffsetY = 0.0;
            }
            return bands;
        }

        /// <summary>
        /// Sub-bands the passband is split into. Twelve, because the quantity being resolved is the
        /// dispersion smear and its length is at most a few tens of pixels: twelve samples across it
        /// leave steps under a pixel once the bilinear placement in BuildChromaticKernel has spread
        /// each one, and the kernel cost is linear in this while the convolution cost is not affected
        /// at all.
        /// </summary>
        private const int ChromaticSubBandCount = 12;

        /// <summary>Temperature of the reference continuum the shared dispersion kernel is weighted with. Solar-type, the middle of what this roster photographs.</summary>
        private const double SubBandReferenceTemperatureK = 6000.0;

        /// <summary>
        /// The geometry atmospheric dispersion needs: how far from the zenith the field is, and which
        /// way the zenith lies ON THE SENSOR.
        ///
        /// The direction is obtained by projecting the zenith itself and differencing against the
        /// field centre, rather than by computing a parallactic angle and rotating it in: the
        /// projection already carries the mount's field rotation, the sensor's parity and the
        /// gnomonic distortion, and re-deriving any of those by hand is how a dispersion smear ends
        /// up pointing at the ground.
        /// </summary>
        private void GatherDispersionGeometry(ref FrameComputeInputs inputs, GnomonicProjection projection,
                                              double meridianRaDeg, double latitudeDeg, SkyTarget target)
        {
            inputs.ZenithDistanceDeg = 90.0;
            inputs.ZenithUnitX = 0.0;
            inputs.ZenithUnitY = 0.0;
            inputs.AirTemperatureCelsius = AtmosphericRefraction.StandardTemperatureCelsius(Spec.SiteAltitudeMeters);
            inputs.AirPressureMillibar = AtmosphericRefraction.StandardPressureMillibar(Spec.SiteAltitudeMeters);
            inputs.WaterVapourPressureMillibar = AtmosphericRefraction.WaterVapourPressureMillibar(
                inputs.AirTemperatureCelsius, AtmosphericRefraction.DefaultRelativeHumidity);

            // The frame centre's own horizontal coordinates, read back out of the projection rather
            // than recomputed: deprojecting the centre pixel returns the direction in the
            // observatory's (north, east, up) basis, from which altitude and azimuth fall straight
            // out. Nothing about the mount or the parity has to be assumed.
            SkyVector centre = projection.Deproject(0.5 * TextureWidth, 0.5 * TextureHeight);
            double altDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, centre.Z))) * 180.0 / Math.PI;
            double azDeg = Math.Atan2(centre.Y, centre.X) * 180.0 / Math.PI;
            if (altDeg <= 0.0) return;
            inputs.ZenithDistanceDeg = Math.Max(0.0, 90.0 - altDeg);

            // A point one degree closer to the zenith at the same azimuth, projected through the
            // same geometry: the difference is the zenith direction on the sensor.
            if (!projection.TryProject(SkyVector.FromHorizontal(altDeg, azDeg), out double cx, out double cy))
                return;
            double higher = Math.Min(89.999, altDeg + 1.0);
            if (!projection.TryProject(SkyVector.FromHorizontal(higher, azDeg), out double zx, out double zy))
                return;

            double dx = zx - cx, dy = zy - cy;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!(length > 0.0)) return;
            inputs.ZenithUnitX = dx / length;
            inputs.ZenithUnitY = dy / length;
        }

        /// <summary>
        /// Galaxies whose light lands on the sensor. The cone is the frame's own half-diagonal
        /// about the WCS reference point, widened inside GalaxyCatalog.Search by the catalogue's
        /// largest object so that a galaxy centred just off the edge still puts its disk in frame.
        ///
        /// No magnitude cut here: for an extended source the question is not total brightness but
        /// SURFACE brightness, and a nearby dwarf can be brighter in total than a distant spiral
        /// while being invisible per pixel. DepositGalaxies applies the real test, which is whether
        /// the profile's own centre clears the frame's noise floor.
        /// </summary>
        private List<Galaxy> SearchGalaxyCatalog(FrameComputeInputs inputs, double latitudeDeg)
        {
            GalaxyCatalog catalog = GalaxyCatalog;
            if (catalog == null || !catalog.IsLoaded || !inputs.HaveFieldGeometry) return null;

            double halfDiagonalDeg = 0.5 * Math.Sqrt(
                (TextureWidth * inputs.PlateScaleArcsec) * (TextureWidth * inputs.PlateScaleArcsec)
              + (TextureHeight * inputs.PlateScaleArcsec) * (TextureHeight * inputs.PlateScaleArcsec)) / 3600.0;

            return catalog.Search(LastWcs.ReferenceRaDeg, LastWcs.ReferenceDecDeg,
                                  halfDiagonalDeg, double.PositiveInfinity);
        }

        /// <summary>
        /// Draws the galaxies, in electrons, from their catalogued photometry and shape.
        ///
        /// PHOTOMETRY goes down the same path as a catalogue star: an apparent magnitude and a
        /// colour, through the instrument's integrated response, with the Galactic foreground
        /// extinction applied. For a galaxy the extinction is the WHOLE column; it sits behind
        /// all of it, which is the one case DustMap's total reddening applies to without
        /// qualification, and it is why LastFieldReddeningEBv exists.
        ///
        /// The colour is used the way a star's is, to set a blackbody standing in for the source's
        /// spectrum inside the bandpass integral. For a star that is close to the truth; for a
        /// galaxy it is a composite stellar population being represented by its own colour
        /// temperature. What it affects is only the conversion from the catalogued V to this
        /// instrument's band, not the flux, and there is no all-sky catalogue of galaxy spectra to
        /// do better from. Entries with no catalogued colour are counted and reported rather than
        /// silently filled.
        ///
        /// SHAPE comes from the catalogue: D25, the axis ratio and the position angle. The major
        /// axis is turned into a PIXEL direction by projecting two sky positions through the very
        /// projection that places the stars, so field rotation and the projection's own distortion
        /// are already in it rather than being corrected for afterwards.
        /// </summary>
        private void DepositGalaxies(float[] signal, FrameComputeInputs inputs, double scintillation)
        {
            LastGalaxiesDrawn = 0;
            LastGalaxyElectrons = 0.0;
            LastGalaxiesWithModelledColour = 0;
            LastGalaxiesFromImages = 0;
            LastGalaxyMapSamplingArcsec = double.NaN;

            List<Galaxy> galaxies = inputs.Galaxies;
            if (galaxies == null || galaxies.Count == 0 || inputs.Response == null) return;
            if (!inputs.HaveFieldGeometry || signal == null) return;

            var reddening = new ReddenedResponseCache(inputs.Response);
            double eBv = inputs.FieldReddeningEBv;
            double plateScale = inputs.PlateScaleArcsec;
            if (!(plateScale > 0.0)) return;

            // One electron in the exposure is the floor: a surface brightness below that cannot be
            // recorded at all, so it is where the profile stops. Same criterion as the PSF's.
            double floorElectrons = Math.Max(1.0, inputs.SignalCutoffElectrons);

            GalaxyImageSet images = GalaxyImages;
            bool haveImages = images != null && images.IsLoaded;
            double bandWavelengthNm = FilterCentralWavelengthMeters(inputs.Filter) * 1e9;

            // Names in this frame, so a companion is only skipped when the map that swallowed it is
            // itself being drawn. The cone search widens by the catalogue's own largest object, not
            // by the largest MAP, so a frame can hold a companion whose owner's D25 ellipse does not
            // quite reach it; skipping it unconditionally would then draw neither.
            HashSet<string> present = null;
            if (haveImages)
            {
                present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Galaxy g in galaxies) present.Add(g.Name);
            }

            // Brighter catalogued total wins a mutual-coverage tie; name order settles a dead heat.
            bool CatalogDominates(Galaxy a, string otherName)
            {
                GalaxyCatalog catalog = GalaxyCatalog;
                if (catalog == null || !catalog.TryGetByName(otherName, out Galaxy other)) return true;
                if (!double.IsNaN(a.TotalBMag) && !double.IsNaN(other.TotalBMag)
                    && Math.Abs(a.TotalBMag - other.TotalBMag) > 1e-9)
                    return a.TotalBMag < other.TotalBMag;
                return string.CompareOrdinal(a.Name, otherName) < 0;
            }

            foreach (Galaxy g in galaxies)
            {
                // A companion whose light is already inside a neighbour's map is drawn by that
                // map, once, with its own catalogued flux folded into the neighbour's total.
                //
                // For MUTUAL coverage (an interacting pair so close that each map swallowed the
                // other, M51 + NGC5195 in the shipped data) an unconditional skip drops BOTH
                // members and the pair vanishes. One member must deposit: its map total already
                // folds the companion's catalogued flux in, so the pair is drawn once at
                // combined brightness. The tie-break picks that member.
                if (haveImages && images.IsCoveredByAnother(g.Name, out string owner)
                    && present.Contains(owner))
                {
                    bool mutual = images.IsCoveredByAnother(owner, out string ownersOwner)
                               && string.Equals(ownersOwner, g.Name, StringComparison.OrdinalIgnoreCase);
                    if (!mutual || !CatalogDominates(g, owner))
                        continue;
                }

                double colour = g.ColourBv;
                bool modelledColour = double.IsNaN(colour);
                if (modelledColour) colour = MeanColourForType(g.MorphologicalType);
                double vMag = g.TotalBMag - colour;

                double electrons = StellarPhotometry.CollectedElectrons(
                    vMag, colour, eBv, inputs.Response, reddening,
                    inputs.ApertureAreaCm2, inputs.ExposureSeconds,
                    inputs.StarNonAtmosphericTransmission * Math.Max(0.0, scintillation));
                if (!(electrons > 0.0)) continue;

                if (haveImages && TryDepositGalaxyImage(signal, inputs, g, images, electrons,
                                                        reddening, eBv, scintillation,
                                                        bandWavelengthNm, out double imageElectrons))
                {
                    LastGalaxiesDrawn++;
                    LastGalaxiesFromImages++;
                    LastGalaxyElectrons += imageElectrons;
                    if (modelledColour) LastGalaxiesWithModelledColour++;
                    continue;
                }

                if (!TryProjectGalaxy(g, inputs, out double cx, out double cy,
                                      out double majorX, out double majorY))
                    continue;

                double semiMajorPx = g.SemiMajorArcsec / plateScale;
                double n = g.SersicIndex > 0.0
                    ? g.SersicIndex
                    : Core.GalaxyCatalog.SersicIndexForType(g.MorphologicalType);

                // R_e from the two measured quantities together, with no free constant.
                //
                // Where they are inconsistent (a galaxy whose total magnitude is too faint to
                // reach 25 mag/arcsec^2 anywhere at its catalogued size), the isophote cannot be
                // honoured by any R_e, and the fallback keeps the SIZE, which is what the frame
                // shows, by reading D25/2 as the radius enclosing FallbackEnclosedAtD25 of the
                // light instead. tools/galaxy-tests reports what fraction the solved cases put
                // there, which is where that number comes from.
                double reArcsec = SersicProfile.EffectiveRadiusFromIsophote(
                    g.TotalBMag, g.SemiMajorArcsec, D25SurfaceBrightness, n);
                double rePx = double.IsNaN(reArcsec)
                    ? semiMajorPx / Math.Max(1e-6,
                        SersicProfile.RadiusForEnclosedFraction(FallbackEnclosedAtD25, n))
                    : reArcsec / plateScale;
                if (!(rePx > 0.0)) continue;

                double radii = GalaxyRenderer.TruncationRadiiForFloor(
                    electrons, rePx, g.AxisRatio, n, floorElectrons, MaxGalaxyTruncationRadii);
                if (!(radii > 0.0)) continue;

                double deposited = GalaxyRenderer.Deposit(
                    signal, TextureWidth, TextureHeight, cx, cy, majorX, majorY,
                    rePx, g.AxisRatio, n, electrons, radii);
                if (deposited <= 0.0) continue;

                LastGalaxiesDrawn++;
                LastGalaxyElectrons += deposited;
                if (modelledColour) LastGalaxiesWithModelledColour++;
            }
        }

        /// <summary>
        /// Draws a galaxy from its MEASURED shape instead of from a Sersic profile, when a map of
        /// it is installed and it actually lands on the sensor.
        ///
        /// THE GEOMETRY IS SOLVED, NOT ASSUMED. Four corners of the map are turned into sky
        /// directions by the map's own gnomonic deprojection and then projected into the frame by
        /// the very code that places the stars, so field rotation, sensor parity and projection
        /// distortion arrive already applied. The transform between the two tangent planes is then
        /// the exact projective one those four correspondences determine.
        ///
        /// THE BRIGHTNESS IS STILL THE CATALOGUE'S. The map sums to one; it is multiplied by the
        /// electrons the same photometric chain gives a mapless galaxy. A companion the map
        /// swallowed adds its own catalogued flux to that total and is skipped in its own right,
        /// which is what keeps an interacting pair from being drawn one and a half times.
        ///
        /// The pixels are read from disk only after the corners prove the galaxy touches the
        /// sensor, because a map is megabytes and most of the catalogue is off-frame.
        /// </summary>
        private bool TryDepositGalaxyImage(float[] signal, FrameComputeInputs inputs, Galaxy g,
                                           GalaxyImageSet images, double electrons,
                                           ReddenedResponseCache reddening, double eBv,
                                           double scintillation, double bandWavelengthNm,
                                           out double deposited)
        {
            deposited = 0.0;

            GalaxyImage image = images.Describe(g.Name);
            if (image == null || image.Size < 8) return false;

            // The four corners, in map pixels and in frame pixels.
            double last = image.Size - 1;
            var mapU = new double[] { 0.0, last, 0.0, last };
            var mapV = new double[] { 0.0, 0.0, last, last };
            var frameX = new double[4];
            var frameY = new double[4];

            for (int i = 0; i < 4; i++)
            {
                image.MapPixelToRaDec(mapU[i], mapV[i], out double raDeg, out double decDeg);
                HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(
                    raDeg, decDeg, inputs.EndMeridianRaDeg, inputs.ObserverLatitudeDeg);
                if (!inputs.Projection.TryProject(
                        SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg),
                        out frameX[i], out frameY[i]))
                    return false;
            }

            double minX = Math.Min(Math.Min(frameX[0], frameX[1]), Math.Min(frameX[2], frameX[3]));
            double maxX = Math.Max(Math.Max(frameX[0], frameX[1]), Math.Max(frameX[2], frameX[3]));
            double minY = Math.Min(Math.Min(frameY[0], frameY[1]), Math.Min(frameY[2], frameY[3]));
            double maxY = Math.Max(Math.Max(frameY[0], frameY[1]), Math.Max(frameY[2], frameY[3]));
            if (maxX < 0.0 || maxY < 0.0 || minX > TextureWidth || minY > TextureHeight) return false;

            double[] frameToMap = GalaxyImageRenderer.SolveFrameToMap(frameX, frameY, mapU, mapV);
            if (frameToMap == null) return false;

            // Only now is it worth the read.
            if (images.Fetch(g.Name) == null || image.Bands == null) return false;

            double total = electrons;
            if (image.Companions != null && GalaxyCatalog != null)
            {
                foreach (string companion in image.Companions)
                {
                    if (!GalaxyCatalog.TryGetByName(companion, out Galaxy other)) continue;
                    double colour = other.ColourBv;
                    if (double.IsNaN(colour)) colour = MeanColourForType(other.MorphologicalType);
                    total += StellarPhotometry.CollectedElectrons(
                        other.TotalBMag - colour, colour, eBv, inputs.Response, reddening,
                        inputs.ApertureAreaCm2, inputs.ExposureSeconds,
                        inputs.StarNonAtmosphericTransmission * Math.Max(0.0, scintillation));
                }
            }

            deposited = GalaxyImageRenderer.Deposit(
                signal, TextureWidth, TextureHeight, image, frameToMap,
                bandWavelengthNm, total, frameX, frameY);
            if (!(deposited > 0.0)) return false;

            if (double.IsNaN(LastGalaxyMapSamplingArcsec)
                || image.SamplingArcsec > LastGalaxyMapSamplingArcsec)
                LastGalaxyMapSamplingArcsec = image.SamplingArcsec;
            return true;
        }

        /// <summary>Surface brightness the catalogued D25 isophote is defined at, B magnitudes per square arcsecond (de Vaucouleurs et al. 1991, RC3).</summary>
        private const double D25SurfaceBrightness = 25.0;

        /// <summary>Ceiling on how far out a galaxy is drawn, in effective radii. A Sersic n = 4 profile has no edge; this bounds the box when a bright one would otherwise ask for the whole sensor several times over.</summary>
        private const double MaxGalaxyTruncationRadii = 12.0;

        /// <summary>Fraction of a galaxy's light the D25 isophote is taken to enclose when the catalogued magnitude and size admit no exact solution. See the use site.</summary>
        private const double FallbackEnclosedAtD25 = 0.9;

        /// <summary>
        /// Mean B-V of a morphological type, for the catalogue entries with no measured colour.
        /// Roberts &amp; Haynes (1994, ARA&amp;A 32, 115) Table 2: the integrated colours of galaxies
        /// redden monotonically from irregulars to ellipticals as the young stellar population
        /// thins out. Only used where the catalogue has no V magnitude, and counted when it is.
        /// </summary>
        private static double MeanColourForType(double t)
        {
            if (double.IsNaN(t)) return 0.7;
            if (t <= -4.0) return 0.96;   // E
            if (t <= -1.0) return 0.93;   // E-S0
            if (t <= 0.5) return 0.91;    // S0
            if (t <= 2.5) return 0.79;    // Sa-Sab
            if (t <= 4.5) return 0.68;    // Sb-Sbc
            if (t <= 6.5) return 0.55;    // Sc-Scd
            if (t <= 8.5) return 0.44;    // Sd-Sdm
            return 0.39;                  // Im and later
        }

        /// <summary>
        /// Galaxy centre and major-axis direction, both in pixels.
        ///
        /// The direction is obtained by projecting a second point one arcminute along the major
        /// axis rather than by rotating the catalogued position angle into the frame: the
        /// projection already contains the field rotation of an alt-azimuth mount, the parity of
        /// the sensor's axes and the gnomonic distortion, and re-deriving any of those by hand is
        /// how a position angle ends up mirrored.
        /// </summary>
        private bool TryProjectGalaxy(Galaxy g, FrameComputeInputs inputs,
                                      out double cx, out double cy,
                                      out double majorX, out double majorY)
        {
            cx = cy = majorX = majorY = 0.0;

            HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(
                g.RaDeg, g.DecDeg, inputs.EndMeridianRaDeg, inputs.ObserverLatitudeDeg);
            if (!inputs.Projection.TryProject(
                    SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg), out cx, out cy))
                return false;

            const double stepDeg = 1.0 / 60.0;
            double pa = g.PositionAngleDeg * Math.PI / 180.0;
            double cosDec = Math.Cos(g.DecDeg * Math.PI / 180.0);
            double ra2 = g.RaDeg + (Math.Abs(cosDec) > 1e-6 ? stepDeg * Math.Sin(pa) / cosDec : 0.0);
            double dec2 = g.DecDeg + stepDeg * Math.Cos(pa);

            HorizontalCoordinates tip = SkyCoordinates.EquatorialToHorizontal(
                ra2, dec2, inputs.EndMeridianRaDeg, inputs.ObserverLatitudeDeg);
            if (!inputs.Projection.TryProject(
                    SkyVector.FromHorizontal(tip.AltitudeDeg, tip.AzimuthDeg), out double tx, out double ty))
                return false;

            majorX = tx - cx;
            majorY = ty - cy;
            return majorX * majorX + majorY * majorY > 0.0;
        }

        /// <summary>
        /// When set, every stage of the frame's signal plane is written to disk as it is built.
        ///
        /// This exists because eliminating stages by reasoning has a poor record: the same artefact
        /// has now survived six correct-but-irrelevant fixes. A stage dump does not reason. It
        /// writes what the plane holds after each step, so the step that introduces something can
        /// be named from one exposure instead of guessed from many.
        ///
        /// Off by default and written only when the diagnostic toggle is on, because it costs one
        /// frame-sized file per stage.
        /// </summary>
        public static string StageDumpDirectory { get; set; }

        // ---------------------------------------------------------------- Stage timings

        /// <summary>
        /// How long each stage of the last reduction took, milliseconds, in pipeline order.
        ///
        /// WHY THE MOD CARRIES THIS RATHER THAN A HARNESS. A capture that takes half a minute is a
        /// real defect, and the first question is always which stage owns the time; answering it by
        /// reading the code got the wrong answer once already. The cost is one Stopwatch reading per
        /// stage, i.e. six per exposure, against a reduction that touches millions of pixels.
        ///
        /// Read on the main thread by PollProcessTask, which logs it, and shown in the capture
        /// readout. The stages here are the ones the pipeline actually spends time in; the rest are
        /// bundled into the total, which is measured separately so the two can be compared.
        /// </summary>
        public string LastStageTimings { get; private set; }

        /// <summary>Wall-clock time the whole background reduction took, milliseconds.</summary>
        public double LastReductionMilliseconds { get; private set; }

        private readonly System.Diagnostics.Stopwatch stageClock = new System.Diagnostics.Stopwatch();
        private System.Text.StringBuilder stageReport;
        private long stageMark;

        private void BeginStageTiming()
        {
            stageReport = new System.Text.StringBuilder();
            stageClock.Restart();
            stageMark = 0;
        }

        private void MarkStage(string name)
        {
            if (stageReport == null) return;
            long now = stageClock.ElapsedTicks;
            double ms = (now - stageMark) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            stageMark = now;
            if (stageReport.Length > 0) stageReport.Append(", ");
            stageReport.Append(name).Append(' ').Append(ms.ToString("F0")).Append(" ms");
        }

        private void EndStageTiming()
        {
            if (stageReport == null) return;
            stageClock.Stop();
            LastReductionMilliseconds = stageClock.Elapsed.TotalMilliseconds;
            LastStageTimings = stageReport.ToString();
            stageReport = null;
        }

        private int stageDumpIndex;

        /// <summary>Writes one stage of the plane: width, height, then the raw float electrons. Silent on any failure -- a diagnostic must never break a capture.</summary>
        private void DumpStage(string name, float[] plane)
        {
            if (string.IsNullOrEmpty(StageDumpDirectory) || plane == null) return;
            try
            {
                System.IO.Directory.CreateDirectory(StageDumpDirectory);
                string path = System.IO.Path.Combine(StageDumpDirectory,
                    string.Format("stage{0:D2}_{1}.bin", stageDumpIndex++, name));
                using (var stream = System.IO.File.Create(path))
                using (var writer = new System.IO.BinaryWriter(stream))
                {
                    writer.Write(TextureWidth);
                    writer.Write(TextureHeight);
                    // The mean is written first so a reader can sanity-check the file without
                    // parsing all of it, and so a stage that zeroed the plane is obvious.
                    double mean = 0.0;
                    for (int i = 0; i < plane.Length; i++) mean += plane[i];
                    writer.Write(mean / Math.Max(1, plane.Length));
                    for (int i = 0; i < plane.Length; i++) writer.Write(plane[i]);
                }
            }
            catch (Exception e) { lastStageDumpError = e.Message; }
        }

        private string lastStageDumpError;

        /// <summary>
        /// Faintest signal worth drawing, as a fraction of full well.
        ///
        /// A source far below the noise in the pixel it lands on changes nothing a viewer or a
        /// stacking pass could recover, so drawing it only costs time. The floor is the frame's
        /// own noise (sky shot noise, dark current and read noise, exactly the terms
        /// ComputeFramePixels goes on to apply), scaled by StarFieldRenderer's cutoff fraction,
        /// which sits well below 1 so nothing marginally detectable is thrown away.
        /// </summary>
        private double BuildStarSignalFloor(FrameComputeInputs inputs)
        {
            double skyElectrons = Math.Max(0.0, inputs.SkyElectronsPerPixel);
            double darkElectrons = Spec.DarkCurrentElectronsPerSecond * BinningFactor * BinningFactor * inputs.ExposureSeconds;
            double noiseElectrons = Math.Sqrt(skyElectrons + darkElectrons) + Spec.ReadNoiseElectrons;

            return StarFieldRenderer.NoiseFloorCutoffFraction * Math.Max(1.0, noiseElectrons);
        }

        /// <summary>Sky position of a resolved supernova, cached because the sampling walks the host's light map.</summary>
        private static readonly Dictionary<string, KeyValuePair<double, double>> supernovaPositions =
            new Dictionary<string, KeyValuePair<double, double>>();

        private static double longestTemplateDays = -1.0;

        /// <summary>
        /// The supernovae shining in any of the frame's galaxies at this instant.
        ///
        /// Runs on the gather (main) thread because resolving a first-seen event's position may
        /// touch the host's light map on disk; after that the position is cached and the cost is
        /// the deterministic event arithmetic. The template's measured spectrum at the current
        /// phase rides along so the deposit stage prices it through the real passband.
        /// </summary>
        private List<RenderedSupernova> GatherSupernovae(FrameComputeInputs inputs)
        {
            SupernovaTemplateSet templates = SupernovaTemplates;
            long seed = SupernovaSeed;
            if (templates == null || seed == 0 || inputs.Galaxies == null || inputs.Galaxies.Count == 0)
                return null;

            if (longestTemplateDays < 0.0)
            {
                double longest = 0.0;
                foreach (Core.SupernovaClass c in Enum.GetValues(typeof(Core.SupernovaClass)))
                {
                    SupernovaTemplate t = templates.Get(c);
                    if (t != null && t.ActiveDays > longest) longest = t.ActiveDays;
                }
                longestTemplateDays = longest;
            }

            List<RenderedSupernova> found = null;
            double eBv = double.IsNaN(inputs.FieldReddeningEBv) ? 0.0 : Math.Max(0.0, inputs.FieldReddeningEBv);
            double extinctionAtV = eBv > 0.0
                ? -2.5 * Math.Log10(Math.Max(1e-30, SystemResponse.ExtinctionTransmission(
                      StellarPhotometry.JohnsonVWavelengthMeters, eBv)))
                : 0.0;

            for (int gi = 0; gi < inputs.Galaxies.Count; gi++)
            {
                Galaxy g = inputs.Galaxies[gi];
                if (double.IsNaN(g.DistanceModulusMag)) continue;

                List<SupernovaEvent> events = Core.Supernovae.ActiveAt(seed, in g, inputs.Ut, longestTemplateDays);
                for (int i = 0; i < events.Count; i++)
                {
                    SupernovaEvent e = events[i];
                    SupernovaTemplate template = templates.Get(e.Class);
                    if (template == null) continue;

                    double phase = e.PhaseDaysAt(inputs.Ut);
                    double vAnchor = template.VAnchorAt(phase);
                    if (double.IsInfinity(vAnchor)) continue;

                    Core.SpectralCurve shape = template.ShapeAt(phase);
                    if (shape == null) continue;

                    if (!supernovaPositions.TryGetValue(e.Key, out KeyValuePair<double, double> pos))
                    {
                        GalaxyImage map = GalaxyImages != null ? GalaxyImages.Fetch(g.Name) : null;
                        e = Core.Supernovae.ResolvePosition(e, in g, map);
                        pos = new KeyValuePair<double, double>(e.RaDeg, e.DecDeg);
                        supernovaPositions[e.Key] = pos;
                    }

                    (found = found ?? new List<RenderedSupernova>()).Add(new RenderedSupernova
                    {
                        Key = e.Key,
                        HostName = e.HostName,
                        Class = e.Class,
                        IsIIb = e.IsIIb,
                        PhaseDays = phase,
                        RaDeg = pos.Key,
                        DecDeg = pos.Value,
                        VMagApparent = e.PeakAbsoluteBMag + g.DistanceModulusMag + vAnchor + extinctionAtV,
                        EBv = eBv,
                        Shape = shape,
                    });
                }
            }
            return found;
        }

        /// <summary>
        /// Cone-searches the catalogue for everything that could land on the sensor.
        ///
        /// The search is cut at the magnitude whose signal equals the frame's noise floor, so a
        /// short exposure reads only the bright stars while a long one pulls in everything the
        /// catalogue holds, the same way a real frame's star count grows with exposure time.
        /// </summary>
        private List<RenderedStar> SearchStarCatalog(FrameComputeInputs inputs, GnomonicProjection projection,
                                                     double meridianRaDeg, double latitudeDeg)
        {
            RenderedStarCatalog catalog = StarCatalog;
            if (catalog == null || !catalog.IsLoaded) return null;

            SkyVector boresight = projection.Boresight;
            double altDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, boresight.Z))) * 180.0 / Math.PI;
            double azDeg = Math.Atan2(boresight.Y, boresight.X) * 180.0 / Math.PI;

            SkyCoordinates.HorizontalToEquatorial(altDeg, azDeg, meridianRaDeg, latitudeDeg,
                                                  out double centreRaDeg, out double centreDecDeg);

            // The search cone must cover where the field will have TURNED to by the end of the
            // exposure as well as where it starts, or a star that trails into frame is missed.
            double trailDeg = Math.Abs(inputs.EndMeridianRaDeg - inputs.StartMeridianRaDeg);
            double radiusDeg = projection.SearchRadiusDeg(trailDeg + StarSearchMarginDeg);

            double limitingVMag = LimitingVMagFor(inputs);
            LastLimitingVMag = limitingVMag;

            var stars = new List<RenderedStar>(256);
            catalog.Search(centreRaDeg, centreDecDeg, radiusDeg, limitingVMag, stars);
            return stars;
        }

        /// <summary>Extra cone-search radius, covering the PSF wings of a star just outside the sensor and any small inconsistency between the rendered and catalogue frames.</summary>
        private const double StarSearchMarginDeg = 0.05;

        /// <summary>
        /// The apparent magnitude whose collected signal equals the frame's noise floor, which is the
        /// faintest star this exposure can show. Inverts PhotonFluxModel's own flux relation
        /// rather than approximating it, so it stays consistent with what the sources are
        /// actually drawn at, including the optical throughput, which makes this figure
        /// shallower than it used to be and correctly so.
        ///
        /// Evaluated for a flat spectrum: this sets the catalogue search's depth cut, and the
        /// search must not depend on the colour of a star it has not read yet.
        /// </summary>
        private double LimitingVMagFor(FrameComputeInputs inputs)
        {
            if (inputs.Response == null) return 0.0;
            double floorElectrons = inputs.SignalCutoffElectrons;
            double perZeroMag = PhotonFluxModel.CollectedElectrons(
                0.0, inputs.Response.EffectiveWidthAngstromFlat,
                inputs.ApertureAreaCm2, inputs.ExposureSeconds)
                * inputs.StarNonAtmosphericTransmission;

            if (floorElectrons <= 0.0 || perZeroMag <= 0.0) return 0.0;
            return -2.5 * Math.Log10(floorElectrons / perZeroMag);
        }

        /// <summary>
        /// Solar-system bodies sharing the field that the optics cannot resolve into a disk.
        ///
        /// A moon whose apparent diameter is under a couple of pixels is a point of light, and
        /// the renderer draws it as at most a dim sub-pixel speck with no correct brightness.
        /// Computing its real apparent magnitude and depositing it through the same path as a
        /// star puts it in the frame at the right place with the right flux, which is how the
        /// moons of a giant planet show up as points beside it in a real photograph.
        ///
        /// A body large enough to be resolved is left to the renderer, and is instead counted in
        /// the electron budget the rendered image is calibrated against (see
        /// ComputeSceneElectrons), so it is never drawn twice.
        /// </summary>
        private List<PointSource> GatherUnresolvedBodies(FrameComputeInputs inputs, SkyTarget target,
                                                         GnomonicProjection projection, float exposureSeconds)
        {
            var sources = new List<PointSource>();
            if (FlightGlobals.Bodies == null) return sources;
            CelestialBody targetBody = target.Body;

            // ComputeCollectedElectrons applies the ND filter itself and takes extinction from the
            // response, so it is handed the cloud term only; passing the full star chain would
            // attenuate twice.
            double bodyTransmission = inputs.CloudTransmission;

            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || body == targetBody) continue;
                if (IsResolvedByOptics(body, inputs.PlateScaleArcsec)) continue;
                if (!TryProjectBody(body, projection, out double px, out double py)) continue;

                double electrons = ComputeCollectedElectrons(body, inputs.Response, bodyTransmission, exposureSeconds);
                if (electrons <= inputs.SignalCutoffElectrons) continue;

                sources.Add(new PointSource
                {
                    SignalElectrons = electrons,
                    // The body's live position is where the exposure ENDED, so its streak runs
                    // back from there. Over one exposure its own orbital motion is far below the
                    // diurnal drift, so the field centre's displacement is the whole of it.
                    StartPixelX = px - inputs.DriftPixelX,
                    StartPixelY = py - inputs.DriftPixelY,
                    EndPixelX = px,
                    EndPixelY = py,
                });
            }
            return sources;
        }

        /// <summary>True when a body's apparent disk spans enough pixels for the renderer to draw it as a disk rather than a point.</summary>
        private static bool IsResolvedByOptics(CelestialBody body, double plateScaleArcsec)
        {
            if (plateScaleArcsec <= 0.0) return true;
            return AngularDiameterArcsec(body) >= ResolvedBodyMinDiameterPx * plateScaleArcsec;
        }

        /// <summary>Apparent diameter, in pixels, below which a body is treated as a point source rather than a rendered disk. Two pixels is the sampling limit; below it there is no disk to resolve.</summary>
        private const double ResolvedBodyMinDiameterPx = 2.0;

        // Observatory's local (north, east, up) basis in world space, built once per capture by
        // TryBuildFieldGeometry and reused for every body projected into that frame.
        private Vector3d siteNorth, siteEast, siteUp;
        private bool haveSiteBasis;

        /// <summary>
        /// Projects a live body onto the sensor through the frame geometry. The direction is
        /// resolved straight against the observatory's local basis rather than converted to an
        /// azimuth first, which is both cheaper and free of the singularity an azimuth has
        /// directly overhead.
        /// </summary>
        private bool TryProjectBody(CelestialBody body, GnomonicProjection projection, out double px, out double py)
        {
            px = py = 0.0;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || body == null || !haveSiteBasis) return false;

            Vector3d observer = ObservingPlatform.WorldPosition(home);
            Vector3d toBody = body.position - observer;
            if (toBody.sqrMagnitude < 1.0) return false;
            toBody = toBody.normalized;

            // Below the observatory's horizon. Skipped in orbit, where there is no horizon: a
            // spacecraft's sky is the whole sphere, and what blocks a line of sight there is the
            // host body's own disk, which OrbitalVisibility handles analytically and far more
            // precisely than a hemisphere test could.
            if (!ObservingPlatform.IsSpaceBased && Vector3d.Dot(toBody, siteUp) <= 0.0) return false;

            SkyVector direction = SkyVector.Normalized(
                Vector3d.Dot(toBody, siteNorth), Vector3d.Dot(toBody, siteEast), Vector3d.Dot(toBody, siteUp));
            return projection.TryProject(direction, out px, out py);
        }

        /// <summary>
        /// How far the field centre slides across the sensor over the exposure, as a real vector
        /// rather than the horizontal-only smear this used to assume. Measured by projecting the
        /// boresight's own sky position through the geometry at both ends of the exposure, so it
        /// carries the true direction of the drift at whatever latitude and hour angle the
        /// observatory happens to be looking from.
        /// </summary>
        private static void ComputeFieldCentreDrift(FrameComputeInputs inputs, GnomonicProjection projection,
                                                    double latitudeDeg, out double driftX, out double driftY)
        {
            driftX = driftY = 0.0;

            SkyVector boresight = projection.Boresight;
            double altDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, boresight.Z))) * 180.0 / Math.PI;
            double azDeg = Math.Atan2(boresight.Y, boresight.X) * 180.0 / Math.PI;

            SkyCoordinates.HorizontalToEquatorial(altDeg, azDeg, inputs.StartMeridianRaDeg, latitudeDeg,
                                                  out double raDeg, out double decDeg);

            HorizontalCoordinates endAltAz = SkyCoordinates.EquatorialToHorizontal(
                raDeg, decDeg, inputs.EndMeridianRaDeg, latitudeDeg);

            if (!projection.TryProject(SkyVector.FromHorizontal(endAltAz.AltitudeDeg, endAltAz.AzimuthDeg),
                                       out double endX, out double endY))
                return;

            driftX = endX - 0.5 * projection.WidthPx;
            driftY = endY - 0.5 * projection.HeightPx;
        }

        /// <summary>
        /// Builds (or reuses) the instrument's PSF for this frame. Runs on the background pipeline
        /// thread; none of it touches Unity or KSP state.
        ///
        /// Cached on everything it depends on, because none of those change between two captures
        /// with the same settings, and the adaptive-optics solve alone builds a couple of dozen
        /// trial kernels. A stacking batch therefore pays for the PSF once, not once per sub.
        /// </summary>
        private void EnsurePsfKernels(FrameComputeInputs inputs, out float[] core, out int coreRadius,
                                      out float coreWeight, out float[] halo, out int haloRadius)
        {
            double wavelength = FilterCentralWavelengthMeters(inputs.Filter);
            bool hasAo = Spec.AdaptiveOpticsFwhmArcsec > 0.0;

            double atmosphericFwhm;
            if (hasAo)
            {
                // The published delivered figure already contains diffraction, which is now
                // computed separately from the pupil; solve for the residual that reproduces it.
                atmosphericFwhm = OpticalPsf.AtmosphericFwhmForDelivered(
                    Spec.AdaptiveOpticsFwhmArcsec, inputs.PlateScaleArcsec,
                    PupilApertureMeters, PupilObstructionFraction, wavelength);
            }
            else
            {
                atmosphericFwhm = inputs.SeeingFwhmArcsec;
            }

            // The pointing excursion is part of the kernel in orbit (see BuildSpaceSubBands), and
            // it changes with the exposure length and with what the spacecraft is doing, so it
            // has to be in the cache key or a longer sub would reuse a shorter one's PSF.
            double pointingFwhm = inputs.IsSpaceBased ? inputs.Pointing.EquivalentFwhmArcsec : 0.0;

            bool reusable = psfCacheSpec == Spec
                         && psfCacheFilter == inputs.Filter
                         && psfCachePlateScale == inputs.PlateScaleArcsec
                         && psfCacheAtmosphericFwhm == atmosphericFwhm
                         && psfCacheDefocusRadius == inputs.DefocusDiscRadiusPx
                         && psfCacheZenithDistance == inputs.ZenithDistanceDeg
                         && psfCacheZenithX == inputs.ZenithUnitX
                         && psfCacheZenithY == inputs.ZenithUnitY
                         && psfCachePointingFwhm == pointingFwhm
                         && haloSpectrumWidth == TextureWidth
                         && haloSpectrumHeight == TextureHeight;

            if (!reusable)
            {
                // The kernel is built ACROSS the passband, not at its central wavelength. Three
                // things vary with wavelength inside one filter and all three are in here: the Airy
                // pattern scales as lambda/D, the seeing disc as lambda^(-1/5), and the atmosphere
                // refracts blue more than red so the source is smeared toward the zenith. Convolution
                // is linear, so summing the monochromatic kernels with their photon weights and
                // convolving once is not an approximation of a chromatic PSF; it is one, at no cost
                // beyond building the kernel. See OpticalPsf.BuildChromaticKernel.
                //
                // In orbit the dispersion term is gone and the two Gaussian terms take its
                // place, but the structure is identical and for the same reason: both of the
                // new terms are chromatic. The residual wavefront error costs more image quality
                // in the blue than in the red, which is why HST's own published widths turn over
                // near 500 nm, so the sub-band split is what carries it (see BuildSpaceSubBands).
                ChromaticSubBand[] subBands = inputs.IsSpaceBased
                    ? BuildSpaceSubBands(inputs, wavelength, pointingFwhm)
                    : BuildSubBands(inputs, wavelength);

                psfCacheCore = subBands != null
                    ? OpticalPsf.BuildChromaticKernel(
                        inputs.PlateScaleArcsec, PupilApertureMeters, PupilObstructionFraction,
                        atmosphericFwhm, wavelength, inputs.DefocusDiscRadiusPx,
                        PupilVaneCount, PupilVaneWidthMeters, Spec.PrimaryMirrorPads,
                        subBands, out psfCacheCoreRadius)
                    : OpticalPsf.BuildKernel(
                        inputs.PlateScaleArcsec, PupilApertureMeters, PupilObstructionFraction,
                        wavelength, atmosphericFwhm, inputs.DefocusDiscRadiusPx,
                        PupilVaneCount, PupilVaneWidthMeters, 0.0,
                        Spec.PrimaryMirrorPads, out psfCacheCoreRadius);

                // A real adaptive-optics PSF is two-component: a corrected core carrying the
                // system's Strehl ratio, plus the wide halo of everything it failed to correct.
                psfCacheHalo = null;
                psfCacheHaloRadius = 0;
                psfCacheHaloR0 = 0.0;
                psfCacheHaloWavelength = wavelength;
                psfCacheCoreWeight = 1f;
                haloSpectrum = null;
                haloSpectrumWidth = TextureWidth;
                haloSpectrumHeight = TextureHeight;
                if (hasAo && Spec.AdaptiveOpticsStrehlRatio > 0.0 && Spec.AdaptiveOpticsHaloSeeingFwhmArcsec > 0.0)
                {
                    psfCacheCoreWeight = Mathf.Clamp01((float)Spec.AdaptiveOpticsStrehlRatio);
                    psfCacheHaloR0 = OpticalPsf.FriedParameterMeters(
                        Spec.AdaptiveOpticsHaloSeeingFwhmArcsec, wavelength);

                    // The frame-wide kernel first. Its radius is the longest diagonal two sensor
                    // pixels can span, so nothing detectable is left out of the table.
                    double maxLagPx = Math.Sqrt((double)TextureWidth * TextureWidth
                                              + (double)TextureHeight * TextureHeight);
                    var table = new OpticalPsf.AtmosphericProfileTable(
                        maxLagPx, inputs.PlateScaleArcsec, psfCacheHaloR0, wavelength);
                    haloSpectrum = FourierConvolution.RadialKernelSpectrum.Build(
                        TextureWidth, TextureHeight, table.AtPixelRadius, MaxOtfTransformCells);

                    // Bounded fallback, for a frame too large to pad for the above.
                    if (haloSpectrum == null)
                        psfCacheHalo = OpticalPsf.BuildSeeingHaloKernel(
                            inputs.PlateScaleArcsec, Spec.AdaptiveOpticsHaloSeeingFwhmArcsec,
                            wavelength, MaxHaloKernelRadiusPx, out psfCacheHaloRadius);
                }

                psfCacheSpec = Spec;
                psfCacheFilter = inputs.Filter;
                psfCachePlateScale = inputs.PlateScaleArcsec;
                psfCacheAtmosphericFwhm = atmosphericFwhm;
                psfCacheDefocusRadius = inputs.DefocusDiscRadiusPx;
                psfCacheZenithDistance = inputs.ZenithDistanceDeg;
                psfCacheZenithX = inputs.ZenithUnitX;
                psfCacheZenithY = inputs.ZenithUnitY;
                psfCachePointingFwhm = pointingFwhm;

                psfCacheDiffractionFwhm = OpticalPsf.AiryFwhmArcsec(
                    PupilApertureMeters, PupilObstructionFraction, wavelength);
            }

            core = psfCacheCore;
            coreRadius = psfCacheCoreRadius;
            coreWeight = psfCacheCoreWeight;
            halo = psfCacheHalo;
            haloRadius = psfCacheHaloRadius;

            // Diagnostics, read on the main thread after the task completes.
            LastDispersionSmearArcsec = Spec.HasAtmosphericDispersionCorrector
                ? VisualTelescopeSpec.AtmosphericDispersionResidual * RawDispersionSmearArcsec(inputs)
                : RawDispersionSmearArcsec(inputs);
            LastZenithDistanceDeg = inputs.ZenithDistanceDeg;
            LastHaloSpannedFrame = haloSpectrum != null;
            LastHaloEnclosedFraction = haloSpectrum != null ? haloSpectrum.EnclosedFraction : 0.0;
            LastAppliedBlurRadiusPx = psfCacheCoreRadius;
            LastAtmosphericFwhmArcsec = atmosphericFwhm;
            LastDiffractionFwhmArcsec = psfCacheDiffractionFwhm;
        }

        /// <summary>
        /// Builds the finished frame from the rendered scene, the star field and the sky, in the
        /// order the light and the electronics really act.
        ///
        /// That order is the point of this method, and it changed:
        ///
        ///   1. SIGNAL PLANE. The rendered bodies, scaled to their real electron count, plus
        ///      every point source, catalogue stars and unresolved moons alike, deposited at its
        ///      own sub-pixel position with its own independently computed flux.
        ///   2. OPTICS. One convolution with the instrument's real PSF, plus off-axis
        ///      astigmatism. This acts on the SIGNAL, before any noise exists.
        ///   3. SKY. A real surface brightness, uniform across the frame.
        ///   4. DETECTOR. Shot noise, dark current, gain, read noise, cosmic rays, blooming,
        ///      charge-transfer smear, then the sensor's own defects.
        ///
        /// The previous version convolved the PSF AFTER drawing noise, which is backwards in a
        /// way that matters: blurring a noise field correlates neighbouring pixels and shrinks
        /// its variance, so the frame's measured signal-to-noise ratio no longer matched the
        /// physics that produced it, and no stacking or photometry done on it could be trusted.
        /// Optics blur light; they cannot blur the readout that happens afterwards.
        ///
        /// Pure C#/array math only, with no CelestialBody or UnityEngine.Object API touches, so
        /// this runs on a background Task; only the gather step and the texture upload need the
        /// main thread.
        /// </summary>
        private float[] ComputeFramePixels(FrameComputeInputs inputs)
        {
            BeginStageTiming();

            Color[] src = pendingSrc;

            int n = TextureWidth * TextureHeight;
            if (rawScratch == null || rawScratch.Length != n) rawScratch = new float[n];
            if (signalScratch == null || signalScratch.Length != n) signalScratch = new float[n];

            // Reused, not freshly allocated per capture. A Color is 16 bytes, so at the largest
            // instrument's native resolution (4096x4128 = 16.9 Mpx) this single array is 270 MB;
            // allocating it again every exposure churned that much through the large-object heap
            // per shot, on top of the several other frame-sized buffers this pipeline already
            // holds. The result is handed straight to PollProcessTask on the main thread and
            // copied out there, so one buffer is enough.
            if (frameScratch == null || frameScratch.Length != n) frameScratch = new float[n];
            float[] pixels = frameScratch;
            float[] signal = signalScratch;

            // Deliberately the NATIVE (unbinned) Spec.FullWellElectrons here, paired with the
            // native per-physical-pixel DarkCurrentElectronsPerSecond; both real electron
            // quantities scale by BinningFactor^2 together in a real binned pixel, so the
            // resulting pedestal/sigma FRACTION (what DarkCurrent actually returns) comes out
            // identical either way; using the raw per-pixel numbers is just simpler than
            // multiplying both sides by the same factor for no change in the answer.
            // A binned pixel collects the dark current of every physical pixel it merges, so the
            // rate scales with the binned area. In electrons, like everything else here.
            // Dark current at the detector's ACTUAL temperature, scaled from its published rate at
            // its own published operating temperature by the depletion-generation law (Janesick
            // 2001; Varshni 1967 band gap; see Core.DarkCurrentModel). While the two agree, which
            // is the case for every instrument until a cooler setpoint exists, this returns exactly
            // the catalogue figure and changes nothing; what it buys now is that CCD-TEMP has
            // become a live physical input rather than a header decoration, and that hot pixels
            // below can be expressed as what they are.
            double darkPerSecond = DarkCurrentModel.ElectronsPerSecond(
                Spec.DarkCurrentElectronsPerSecond, Spec.DetectorTemperatureCelsius, DetectorTemperatureCelsius);
            lastDarkCurrentElectronsPerSecond = darkPerSecond;

            double binnedDarkPerSecond = darkPerSecond * BinningFactor * BinningFactor;
            double darkElectrons = binnedDarkPerSecond * Math.Max(0.0, inputs.ExposureSeconds);

            // ONE recorded 64-bit seed per exposure, mixed from what identifies the exposure, and
            // written to the FITS header: given the seed, the frame is reproducible bit for bit.
            // That was impossible before; System.Random's sequence for a seed is not part of
            // .NET's contract and has changed between runtimes, and it is what any regression
            // test on this pipeline's stochastic output has to stand on. See Core.Pcg32.
            //
            // Each stochastic process draws from its OWN stream of that seed rather than sharing
            // one sequence, so adding or removing a draw in one of them cannot shift the others.
            ulong captureSeed = Pcg32.MixSeed(
                inputs.TargetSeed, (long)(inputs.Ut * 1000.0), (long)inputs.Filter, BinningFactor);
            lastCaptureSeed = captureSeed;

            var rngScint = new Pcg32(captureSeed, Pcg32.StreamScintillation);

            float scintJitter = ScintillationMultiplier(rngScint, inputs.ScintSigma);
            lastScintillationFactor = scintJitter;
            lastScintillationSigma = (float)inputs.ScintSigma;

            float cloudTransmission = (float)inputs.CloudTransmission;

            // Unity's own rendered pixel values (src[]) keep supplying the real spatial shading
            // (terminator, limb, craters from the game's own 3D lighting); only the ABSOLUTE
            // scale of that shading is recalibrated to match the physically-derived total
            // electron count (inputs.TotalElectrons), so noise/saturation/SNR are all anchored
            // to real physics rather than an invented flat exposure multiplier.
            //
            // Calibrating against THIS filter's own rendered sum (e.g. sum of src[].r for the
            // Red filter) would force every filter's stack to the same total electron budget;
            // TotalElectrons is the same physical value for R/G/B (one body-wide albedo, split
            // into equal thirds, see ComputeCollectedElectrons), so that erases the body's real
            // per-channel color balance (a green-dominant body like Jool would have its R and B
            // channels artificially boosted to match G's total, then LRGB-composited into
            // whatever arbitrary hue survives the per-pixel contrast differences, not Jool's
            // actual color). Calibrating every filter against the SAME reference (the frame's
            // luminance-weighted sum, matching FilterSignal's own Luminance formula) instead
            // scales each channel by its real relative share of that luminance, so R:G:B keeps
            // the body's true color ratio through calibration and into the later luminance-
            // transfer step in the colour composite (which already assumes R/G/B carry
            // real relative color, not independently-normalized ones).
            // ONE pass over the render, taking both things this needs from it: the luminance sum
            // that calibrates the frame, and this filter's own channel parked in the signal plane
            // awaiting that calibration. Two passes read a 270 MB array twice for no reason, and
            // more importantly they kept it alive until the second one.
            double totalRenderedLuminance = 0.0;
            for (int i = 0; i < n; i++)
            {
                Color rendered = src[i];
                totalRenderedLuminance += FilterSignal(rendered, CameraFilter.Luminance);
                signal[i] = FilterSignal(rendered, inputs.Filter);
            }

            // Finished with the render. Released HERE, before the optics, because the PSF
            // convolution that follows allocates the largest working set of the whole capture and
            // there is no reason for these two peaks to coincide. Both references have to go: the
            // local, and the field the background task reaches it through.
            src = null;
            pendingSrc = null;

            // Electrons, not fractions of full well. The rendered frame's luminance sum is the
            // denominator, so this factor converts one unit of rendered brightness into the real
            // electron count the physics computed for the scene.
            //
            // THE ZERO BRANCH IS A SILENT FAILURE and is recorded rather than swallowed. If the
            // Unity render came back empty (a refused render target, a readback that produced
            // nothing, a scaled-space body that was not drawn), then this sum is zero, the factor
            // is zero, and every scene pixel is multiplied to nothing. The frame still comes out
            // with its sky, its noise and its catalogue stars, because those are deposited by the
            // physics rather than taken from the render, so the result looks like a working
            // exposure that simply missed the target. There is no way to tell that apart from a
            // genuinely too-faint frame by looking at it, which is exactly why it is reported.
            lastRenderedLuminanceSum = totalRenderedLuminance;
            lastTargetElectrons = inputs.TotalElectrons;

            float calibratedSignalPerUnit = totalRenderedLuminance > 1e-6
                ? (float)(inputs.TotalElectrons / totalRenderedLuminance)
                : 0f;

            // NOT logged from here. This method runs on a background Task, and every other
            // Debug call in this file is on the main thread; the fields above are read by
            // PollProcessTask, which is where the report belongs.

            // --- 1. Signal plane -------------------------------------------------------
            // The rendered bodies first: the renderer supplied the spatial shading above, the
            // physics supplies the scale here. Identical arithmetic to before, only with the
            // rendered channel already in place and the render itself already let go.
            stageDumpIndex = 0;
            DumpStage("render", signal);
            MarkStage("render readout");

            float sceneScale = calibratedSignalPerUnit * scintJitter * cloudTransmission;
            for (int i = 0; i < n; i++) signal[i] *= sceneScale;
            DumpStage("scene-scaled", signal);

            // Galaxies go in with the rendered scene rather than with the star field, because like
            // the scene they are RESOLVED: they are drawn from their own measured profile, they
            // take the extended-source scintillation instead of the point-source one, and the
            // smear below trails them as a shape rather than as a streak from a point.
            if (inputs.HaveFieldGeometry) DepositGalaxies(signal, inputs, scintJitter);
            DumpStage("galaxies", signal);
            MarkStage("galaxies");

            // The rendered scene is a snapshot at one instant; an unguided mount lets the sky
            // slide across the sensor during the exposure, so the whole scene draws a streak
            // along the real drift vector rather than the horizontal-only smear assumed before.
            // Negated: the rendered snapshot is the END of the exposure, so the scene's streak
            // extends backwards from where it was drawn, the same way each star's does.
            ApplyLinearSmear(signal, -inputs.DriftPixelX, -inputs.DriftPixelY);
            DumpStage("smear", signal);
            MarkStage("smear");

            // Then everything unresolved. Stars are point sources, so they carry the point-source
            // scintillation rather than the resolved disk's much quieter figure.
            lastStarsDrawnInternal = 0;
            if (inputs.HaveFieldGeometry)
            {
                float starScint = ScintillationMultiplier(rngScint, inputs.PointSourceScintSigma);
                DepositSkyField(signal, inputs, starScint);
            }
            DumpStage("skyfield", signal);
            MarkStage("stars + emission");

            // --- 2. Optics -------------------------------------------------------------
            // The instrument's real PSF: diffraction off its own annular pupil, convolved with
            // the Kolmogorov atmosphere and any defocus (see OpticalPsf). One convolution over
            // the whole signal plane, so a star and the planet beside it get the same optics and
            // nothing is blurred twice.
            EnsurePsfKernels(inputs, out float[] psfCore, out int psfRadius,
                             out float psfCoreWeight, out float[] psfHalo, out int psfHaloRadius);
            MarkStage("PSF kernel");
            ApplyPsf(signal, psfCore, psfRadius, psfCoreWeight, psfHalo, psfHaloRadius);
            DumpStage("psf", signal);
            MarkStage("PSF convolution");

            // Field-dependent astigmatism, applied after the PSF so it reads as a distinct
            // off-axis smear rather than blending into the on-axis profile.
            ApplyAstigmatismBlur(signal);
            DumpStage("astigmatism", signal);

            // --- 2b. Extreme adaptive optics ------------------------------------------
            // The coronagraph's focal-plane mask, and the speckle field an AO-corrected halo
            // really is. Both act on the SIGNAL, after the optics that formed it and before the
            // sky that never went through them.
            ApplyCoronagraphMask(signal, inputs);
            DumpStage("coronagraph", signal);
            ApplySpeckleField(signal, inputs, captureSeed);
            DumpStage("speckles", signal);
            MarkStage("coronagraph + speckles");

            // --- 3. Sky, then 4. detector ----------------------------------------------
            // The sky is uniform, and convolving a constant field with a unit-sum kernel returns
            // it unchanged, so adding it after the PSF is exact and saves a transform.
            float skyElectrons = (float)Math.Max(0.0, inputs.SkyElectronsPerPixel);

            // Everything from charge collection to the converter's output, in one place so that a
            // calibration frame (CaptureCalibrationFrameAdu) goes through the SAME chain rather
            // than a second copy of it free to drift.
            DetectorChainResult chain = RunDetectorChain(
                signal, skyElectrons, darkElectrons,
                inputs.ExposureSeconds, inputs.IsoGain, captureSeed, rawScratch, pixels);

            MarkStage("detector");

            lastSaturatedFraction = chain.SaturatedFraction;
            lastElectronsPerAdu = chain.ElectronsPerAdu;
            lastSaturationElectrons = chain.SaturationElectrons;
            lastBiasLevelAdu = chain.BiasLevelAdu;

            // The zero point, so the exported frame can actually be turned back into magnitudes:
            //   m = -2.5 log10(ADU/s) + ZP,   ZP = 2.5 log10(F0 * W * A * T_nd / K)
            // which is just the pipeline's own photometry equation solved for m. Quoted for a FLAT
            // source spectrum, as a zero point always is; a real star's own colour enters through
            // its own effective width, which is the colour term (see SystemBandpass). It lives here
            // rather than in the detector chain because it describes the OPTICS as much as the
            // sensor, and a calibration frame taken with the shutter closed has no zero point.
            double ndTransmission = NdFilterTransmission(NdFilter);
            double apertureAreaCm2 = RealApertureAreaCm2();
            double flatWidthAngstrom = inputs.Response != null ? inputs.Response.EffectiveWidthAngstromFlat : 0.0;
            lastPhotometricZeroPoint =
                (flatWidthAngstrom > 0.0 && apertureAreaCm2 > 0.0 && chain.ElectronsPerAdu > 0.0 && ndTransmission > 0.0)
                    ? 2.5 * Math.Log10(PhotonFluxModel.ZeroMagPhotonFluxPerAngstrom
                                       * flatWidthAngstrom * apertureAreaCm2 * ndTransmission / chain.ElectronsPerAdu)
                    : double.NaN;

            if (lastAduFrame == null || lastAduFrame.Length != n) lastAduFrame = new float[n];
            Array.Copy(rawScratch, lastAduFrame, n);
            EndStageTiming();

            // No defect overlay here any more. Hot and dead pixels are applied in the charge domain
            // (ApplyPixelDefects, above) where they physically originate, so by this point they
            // have already been through blooming, charge transfer, read noise and digitisation like
            // every other pixel, which is what makes them removable by a dark frame and a bad
            // pixel map instead of being permanent marks on the data.
            //
            // The frame stays genuinely raw and uncorrected: AstroImageStack.AddSub still receives
            // it and cosmetically corrects it against the same fixed defect map before aligning and
            // stacking, the order real calibration pipelines (PixInsight, IRAF/ccdproc, ESO Reflex)
            // use: raw frame -> bad-pixel-map correction -> registration -> stacking.

            return pixels;
        }

        /// <summary>What the detector chain reports back about the exposure it just digitised.</summary>
        private struct DetectorChainResult
        {
            public double ElectronsPerAdu;
            public double SaturationElectrons;
            public double BiasLevelAdu;
            public float SaturatedFraction;
        }

        /// <summary>
        /// Everything between the light landing on the silicon and the converter's output, in the
        /// order the sensor applies it: charge collection, defects, cosmic rays, full-well overflow,
        /// charge transfer, readout noise, and digitisation.
        ///
        /// Extracted so that a shutter-closed calibration frame runs the SAME code as a science
        /// frame. That is the whole point of the exercise: a dark frame is only worth subtracting if
        /// it was produced by the same chain, and a second implementation of the chain, however
        /// carefully written, is free to drift from the first the moment either is edited.
        ///
        /// signal may be null, which means no scene light reached the sensor at all. That is not a
        /// convenience: it is exactly what a closed shutter is.
        ///
        /// displayPixels may be null when the caller wants only the converter counts.
        /// </summary>
        private DetectorChainResult RunDetectorChain(
            float[] signal, float skyElectrons, double darkElectrons,
            float exposureSeconds, float isoGain,
            ulong seed, float[] raw, float[] displayPixels)
        {
            int n = raw.Length;

            // Each stochastic process on its own stream of the exposure's seed, so that they cannot
            // correlate and so that adding a draw to one cannot shift the others.
            var rng = new Pcg32(seed, Pcg32.StreamShotNoise);
            var rngRead = new Pcg32(seed, Pcg32.StreamReadNoise);
            var rngCosmic = new Pcg32(seed, Pcg32.StreamCosmicRays);

            EnsureFlatFieldMap();
            EnsureOffsetFpnMap();
            EnsureFringeMap();

            // Charge collection. Poisson, not a Gaussian of matching width: photon arrival IS a
            // counting process, and the two only agree once the count is large. At the few
            // electrons per pixel a faint sky or a short dark reaches, a Gaussian goes negative
            // and is measurably the wrong distribution, the same reason GalSim and Pyxel both
            // draw real Poisson deviates here.
            //
            // The flat field multiplies the SCENE AND THE SKY AND NOT THE DARK, which is the whole
            // content of the distinction between an additive and a multiplicative term. Scene light
            // and sky light both entered the same aperture and land on the same pixel, so both are
            // scaled by whatever fraction of the entering light that pixel converts; dark charge is
            // generated in the silicon itself and never travelled through the optics at all. Get
            // this wrong in either direction and dividing by a flat stops being a calibration:
            // scale the dark too and a flat over-corrects it, leave the sky out and it under-corrects
            // the term that dominates every deep exposure.
            //
            // It multiplies the MEAN, ahead of the Poisson draw, rather than the drawn sample. A
            // pixel with 1% lower response does not collect the same photons and lose some
            // afterwards, it collects 1% fewer, and its shot noise is the square root of what it
            // actually collected. Scaling after the draw would leave the frame carrying the shot
            // noise of a signal it does not have.
            // COUNT-RATE NON-LINEARITY, on the detectors that have one. An HgCdTe array's measured
            // flux is not a linear function of the true flux: a source a decade fainter than where
            // the photometric zero point was anchored is measured a fixed fraction low. It is
            // applied HERE, to the mean collected rate and ahead of the Poisson draw, because it is
            // a change in the detector's SENSITIVITY rather than a loss applied afterwards - the
            // pixel really does convert fewer of the photons it was sent, and the shot noise it
            // carries is the square root of what it actually collected.
            //
            // Hoisted out of the loop as a flag rather than tested per pixel; NaN on every CCD here.
            bool countRateNonLinear = !double.IsNaN(Spec.CountRateNonLinearityPerDex)
                                   && !double.IsNaN(Spec.CountRateNonLinearityReferenceElectronsPerSecond)
                                   && exposureSeconds > 0f;
            double crnlSlope = Spec.CountRateNonLinearityPerDex;
            double crnlReference = Spec.CountRateNonLinearityReferenceElectronsPerSecond;

            for (int i = 0; i < n; i++)
            {
                double sceneElectrons = signal != null ? signal[i] : 0.0;
                // The fringe modulation rides on the SKY alone: it is an interference effect whose
                // depth depends on the source's spectrum, and the sky's line forest fringes where a
                // stellar continuum does not (see EnsureFringeMap).
                double collected = (sceneElectrons + skyElectrons * FringeAt(i)) * FlatFieldAt(i);

                if (countRateNonLinear && collected > 0.0)
                {
                    double rate = collected / exposureSeconds;
                    collected = InfraredArray.MeasuredRate(rate, crnlReference, crnlSlope) * exposureSeconds;
                }

                double meanElectrons = Math.Max(0.0, collected + darkElectrons);
                raw[i] = (float)SamplePoisson(rng, meanElectrons);
            }

            // The sensor's own defects, applied HERE (in the charge domain, alongside the dark
            // current they are made of) rather than stamped over the finished counts after
            // digitisation, which is what this pipeline used to do.
            //
            // A hot pixel is not a bright dot the readout paints on. It is a pixel whose depletion
            // region generates charge at a multiple of the array's rate because of a bulk lattice
            // defect; Widenhorn et al. (2002) show it is precisely the depletion component, the one
            // that dominates in a cooled detector, that varies from pixel to pixel. Three things
            // follow that the old treatment got wrong: the defect now GROWS WITH EXPOSURE TIME (a
            // 1-second sub shows a faint speck where a 300-second one shows a blown pixel, instead
            // of both showing the same near-full-scale dot), it responds to detector temperature
            // through the same law as the rest of the dark current, and, the point of all of it,
            // subtracting a dark frame REMOVES it, which is the entire reason an observer takes one.
            //
            // A dead pixel is the converse: no photo response at all, so it collects no signal and
            // no sky, but its silicon still generates dark charge like any other pixel. It reads
            // near the pedestal rather than at exactly zero, and a flat frame is what identifies it.
            DumpStage("poisson", raw);

            // Charge the previous exposures left in the surface traps, coming back.
            //
            // It is added HERE, after the Poisson draw and NOT through the flat field, for the two
            // reasons that already separate dark current from scene light a few lines above: these
            // electrons never travelled through the optics, so no pixel's photo-response scales
            // them, and they are not a fresh counting process, so drawing them from a Poisson mean
            // would give them a variance they do not have. They are electrons that were already
            // counted once, held, and released.
            //
            // Off on every instrument on this roster; see Core.DetectorPersistence for which
            // detectors have been measured and which have simply never been published.
            ApplyPersistenceRelease(raw, exposureSeconds);
            ApplyHgCdTePersistenceRelease(raw, exposureSeconds);
            DumpStage("persistence-release", raw);

            ApplyPixelDefects(raw, signal, skyElectrons, darkElectrons, isoGain, rng);
            DumpStage("defects", raw);

            // Charge-domain effects, in the order the silicon applies them and now on real
            // electron counts against a real well, so the thresholds mean something.
            ApplyCosmicRays(raw, exposureSeconds, rngCosmic);
            DumpStage("cosmic", raw);

            // THE CHAIN FORKS HERE, and it forks on which effects EXIST rather than on which
            // numbers apply. A CCD clocks its charge to a shared output, so a full well overflows
            // along the column and the transfer is imperfect. An HgCdTe array reads every pixel
            // where it sits: the WFC3 handbook records both consequences outright, "no charge
            // bleeding at saturation" and "minimal long-term on-orbit CTE degradation". Running an
            // infrared array through the CCD branch would add two effects the device does not have.
            if (Spec.Technology == DetectorTechnology.HgCdTeArray)
            {
                // A full photodiode stops collecting. It does not spill, so the well is a ceiling
                // and nothing is redistributed.
                float ceiling = (float)FullWellElectrons;
                for (int i = 0; i < n; i++) if (raw[i] > ceiling) raw[i] = ceiling;
                DumpStage("saturation", raw);

                // What this exposure leaves for the next: the fluence each pixel reached, and how
                // long it sat there, which is the pair the published persistence model is a
                // function of.
                RecordHgCdTeStimulus(raw, exposureSeconds);
            }
            else
            {
                ApplyBlooming(raw, (float)FullWellElectrons);
                DumpStage("blooming", raw);

                // What this exposure leaves behind for the next one, taken from the well AFTER
                // blooming and BEFORE transfer: blooming is what caps the charge a pixel actually
                // held, and transfer is what happens to that charge on the way out. The traps see
                // the charge that sat there, which is the post-blooming quantity.
                //
                // Reading the well and writing the trap state in the same pass is deliberate: the
                // capture depends on how full the traps already are, so a pixel that has been
                // saturated repeatedly takes less each time, which is what a finite density of
                // interface states means and what the published behaviour after a gross
                // overexposure requires.
                ApplyPersistenceCapture(raw);

                ApplyChargeTransferSmear(raw);
                DumpStage("cti", raw);
            }

            // The output amplifier's own departure from linearity, applied to the charge it is
            // handed and therefore AFTER transfer and BEFORE the noise it adds. It is the one
            // detector effect in this chain that no calibration frame in the standard set removes,
            // because it depends on how full the well is and each calibration frame sits at its own
            // level; see Core.DetectorLinearity.
            double linearityDeviation = Spec.LinearityDeviationAtFullWell;
            if (linearityDeviation > 0.0 && !double.IsNaN(linearityDeviation))
            {
                double fullWell = FullWellElectrons;
                for (int i = 0; i < n; i++)
                    raw[i] = (float)DetectorLinearity.Measured(raw[i], fullWell, linearityDeviation);
                DumpStage("linearity", raw);
            }

            // INTERPIXEL CAPACITANCE, applied at the readout because that is where it happens.
            //
            // IPC is a capacitive coupling between neighbouring pixels' sense nodes: a signal in one
            // pixel raises the APPARENT signal in its neighbours without any charge moving. So it is
            // neither charge diffusion nor brighter-fatter, both of which move real electrons, and
            // it comes after everything in the charge domain and before the noise the amplifier
            // adds. It is linear, which is why it is a fixed convolution.
            //
            // Off on every CCD here; the kernel is Core.InfraredArray's transcription of WFC3 ISR
            // 2011-10's on-orbit measurement.
            if (Spec.InterpixelCapacitanceKernel != null)
            {
                InfraredArray.ApplyCoupling(raw, TextureWidth, TextureHeight, Spec.InterpixelCapacitanceKernel);
                DumpStage("ipc", raw);
            }

            // Readout: the amplifier's own noise is added in electrons, ahead of the converter,
            // which is where it physically enters, and alongside it the part of the readout's zero
            // that does NOT change from frame to frame.
            //
            // Those two are the same electrons and opposite quantities. The Gaussian is temporal:
            // a fresh draw every exposure, so stacking averages it down and no calibration frame can
            // remove it. The offset is spatial and fixed: identical in every exposure this sensor
            // ever takes, so stacking does nothing to it and subtracting a master bias removes it
            // exactly. Adding them in the same loop is how a real readout produces them, and keeping
            // them distinct in the model is what makes a bias frame worth taking.
            // On a ramp-sampled array there is no single read noise: fitting the ramp to more
            // non-destructive samples averages it down, so the number depends on how far up the
            // ramp this exposure went. Interpolated between the two published anchors in
            // 1/sqrt(N), which is how averaging N samples behaves; see Core.InfraredArray.
            float readNoiseElectrons = Spec.RampReads > 0
                                    && !double.IsNaN(Spec.RampReadNoiseAtFewReadsElectrons)
                                    && !double.IsNaN(Spec.RampReadNoiseAtManyReadsElectrons)
                ? (float)InfraredArray.EffectiveReadNoiseElectrons(
                      Spec.RampReads,
                      Spec.RampReadNoiseAtFewReadsElectrons, Spec.RampFewReads,
                      Spec.RampReadNoiseAtManyReadsElectrons, Spec.RampManyReads)
                : (float)Spec.ReadNoiseElectrons;
            for (int i = 0; i < n; i++)
                raw[i] += NextGaussian(rngRead, readNoiseElectrons)
                        + SensorNonUniformity.OffsetElectrons(offsetFpnMap, i);

            // Digitisation: charge divided by the real conversion factor K, truncated to an integer
            // count the way an ADC actually works, and clipped at the converter's own top code,
            // which for FORS2 arrives well before its full well ever does.
            var result = new DetectorChainResult
            {
                ElectronsPerAdu = ElectronsPerAdu(isoGain),
                SaturationElectrons = SaturationElectrons(isoGain),
            };
            int adcMax = AdcMaxCount;

            // The bias pedestal, added ahead of the converter exactly where the readout electronics
            // add it. Without one, the clip at zero below removed the negative half of the read
            // noise wherever a pixel's total charge sat within a read noise of zero, biasing it
            // upward, destroying the Gaussian shape at the faint floor, and leaving the read noise
            // unmeasurable from the exported data. That regime is rare in a long exposure on a
            // bright sky, common in a short one, and UNIVERSAL in the calibration frames this
            // method now also produces. See VisualTelescopeSpec.BiasLevelAdu for why its VALUE is
            // arbitrary and its PRESENCE is not.
            result.BiasLevelAdu = Spec.EffectiveBiasLevelAdu(result.ElectronsPerAdu);
            double displayRange = Math.Max(1.0, adcMax - result.BiasLevelAdu);

            int saturated = 0;
            for (int i = 0; i < n; i++)
            {
                if (raw[i] >= result.SaturationElectrons) saturated++;

                double adu = Math.Floor(raw[i] / result.ElectronsPerAdu + result.BiasLevelAdu);
                if (adu < 0.0) adu = 0.0;
                else if (adu > adcMax) adu = adcMax;

                raw[i] = (float)adu;

                if (displayPixels != null)
                {
                    // Display only: bias-subtracted and normalised by the range left above the
                    // pedestal, so the stretch functions keep working on [0,1] and the pedestal does
                    // not read as a grey floor. The calibratable data is the RAW ADU count above,
                    // pedestal included, which is what the FITS export receives.
                    float value = (float)((adu - result.BiasLevelAdu) / displayRange);
                    if (value < 0f) value = 0f; else if (value > 1f) value = 1f;
                    displayPixels[i] = value;
                }
            }
            result.SaturatedFraction = n > 0 ? (float)saturated / n : 0f;
            return result;
        }

        /// <summary>The kind of shutter-closed frame CaptureCalibrationFrameAdu produces.</summary>
        public enum CalibrationFrameType
        {
            /// <summary>Zero exposure: the pedestal and the read noise, and nothing else. What a bias frame is for is measuring exactly those two.</summary>
            Bias,
            /// <summary>A real exposure with the shutter closed: everything a bias has, plus dark current, hot pixels and cosmic rays.</summary>
            Dark,

            /// <summary>
            /// A real exposure of a uniformly illuminated screen: everything a dark has, plus the
            /// one thing only light can reveal, which is what fraction of it each pixel converts.
            ///
            /// This is the frame that could not previously exist. The pipeline's photo response was
            /// uniform to machine precision and its optics had no illumination falloff, so a flat
            /// would have recorded a constant and dividing by it would have divided by one. It
            /// exists now because there is finally something in the frame for it to measure: the
            /// cosine-fourth falloff, any field stop the instrument has, and the sensor's own PRNU
            /// where the device's is published.
            /// </summary>
            Flat,
        }

        /// <summary>
        /// Illumination level a flat frame is taken at, as a fraction of what the chain saturates
        /// at. Half is the conventional operating point at both ends of the field: EMVA 1288
        /// specifies PRNU be measured at 50% saturation, and observatory flat-field recipes aim for
        /// between a third and a half of full scale, high enough that photon noise is negligible
        /// against the response being measured and low enough to stay clear of the non-linear top of
        /// the range. Against the CONVERTER's saturation rather than the well's, because on more
        /// than one instrument here the converter is the limit that arrives first.
        /// </summary>
        private const double FlatFieldTargetFraction = 0.5;

        /// <summary>
        /// A calibration frame, in the detector's own ADU: the shutter-closed companion to a
        /// science exposure, and what makes one reducible.
        ///
        /// It runs the same RunDetectorChain a science frame does, with no scene light and no sky,
        /// so what it records is exactly what a real bias or dark records: the pedestal, the read
        /// noise, and (for a dark) the dark current with its hot pixels and whatever cosmic rays
        /// arrived. Subtracting a dark of matching exposure and temperature from a light frame
        /// removes all of it, which is now true of this pipeline in the way it is true of a real
        /// one; it was not while hot pixels were stamped on after digitisation.
        ///
        /// The exposure is what a dark must match: a dark frame is only valid for lights of the
        /// SAME exposure time, gain, binning and detector temperature, which is why the caller
        /// passes the exposure explicitly rather than this reaching for the camera's current one.
        ///
        /// Deliberately allocates its own buffer instead of borrowing the capture scratch: this is
        /// invoked straight from the GUI and a science capture may be in flight on the background
        /// thread. Returns null if the scene has never been built (no sensor dimensions yet).
        /// </summary>
        public float[] CaptureCalibrationFrameAdu(CalibrationFrameType type, float exposureSeconds,
                                                  out float exposureUsedSeconds, out double biasLevelAdu)
        {
            exposureUsedSeconds = type == CalibrationFrameType.Bias
                ? 0f
                : Mathf.Clamp(exposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            biasLevelAdu = 0.0;

            int width = TextureWidth, height = TextureHeight;
            if (width <= 0 || height <= 0) return null;

            var raw = new float[width * height];

            double darkPerSecond = DarkCurrentModel.ElectronsPerSecond(
                Spec.DarkCurrentElectronsPerSecond, Spec.DetectorTemperatureCelsius, DetectorTemperatureCelsius);
            double darkElectrons = darkPerSecond * BinningFactor * BinningFactor * exposureUsedSeconds;

            // Its own seed, mixed from the frame type and exposure as well as the clock, so a bias
            // and a dark taken in the same instant are not the same noise realisation.
            ulong seed = Pcg32.MixSeed(
                DateTime.UtcNow.Ticks, (long)type, (long)(exposureUsedSeconds * 1000f), BinningFactor);

            // A flat is a real exposure of a uniform screen, so the screen's light arrives exactly
            // where the sky's does: as a level that is the same everywhere before the flat field
            // scales it, and is then scaled by that field like any other light entering the
            // aperture. Passing it as the sky term is not a trick, it is what a uniformly
            // illuminated dome screen IS to a detector.
            float screenElectrons = type == CalibrationFrameType.Flat
                ? (float)(FlatFieldTargetFraction * SaturationElectrons(Gain))
                : 0f;

            DetectorChainResult chain = RunDetectorChain(
                null, screenElectrons, darkElectrons, exposureUsedSeconds, Gain, seed, raw, null);

            biasLevelAdu = chain.BiasLevelAdu;
            lastCalibrationSeed = seed;
            lastCalibrationElectronsPerAdu = chain.ElectronsPerAdu;
            lastCalibrationSaturationElectrons = chain.SaturationElectrons;
            lastCalibrationDarkPerSecond = darkPerSecond;
            return raw;
        }

        /// <summary>Seed, conversion factor, saturation and dark rate of the last calibration frame, the header fields its FITS export needs, kept apart from the science frame's own.</summary>
        public ulong LastCalibrationSeed => lastCalibrationSeed;
        public double LastCalibrationElectronsPerAdu => lastCalibrationElectronsPerAdu;
        public double LastCalibrationSaturationElectrons => lastCalibrationSaturationElectrons;
        public double LastCalibrationDarkPerSecond => lastCalibrationDarkPerSecond;
        private ulong lastCalibrationSeed;
        private double lastCalibrationElectronsPerAdu = 1.0;
        private double lastCalibrationSaturationElectrons;
        private double lastCalibrationDarkPerSecond;

        /// <summary>
        /// Redraws the sensor's known defective pixels with their own charge statistics. See the
        /// call site for why this belongs in the charge domain rather than over the finished counts.
        ///
        /// The pixels are RE-DRAWN rather than scaled: each is a fresh Poisson sample at its own
        /// mean, which is the correct distribution for it, where multiplying an already-drawn
        /// sample would keep the array's variance and merely stretch it.
        /// </summary>
        private void ApplyPixelDefects(float[] raw, float[] signal, float skyElectrons,
                                       double darkElectrons, float isoGain, System.Random rng)
        {
            EnsureDefectMap();
            if (raw == null) return;

            // The multiplier is derived at the detector's REFERENCE temperature, not its current
            // one, and this distinction is the whole physics of it: a hot pixel is hot because of a
            // fixed impurity concentration in its own silicon, so what it owns is a RATIO to the
            // array around it. Deriving the ratio from the current rate instead would make it fall
            // by exactly as much as the dark current rose, and a hot pixel would then look identical
            // at -20 C and at ambient, which is the opposite of what warming a sensor does.
            double referenceBinnedDarkPerSecond =
                Spec.DarkCurrentElectronsPerSecond * BinningFactor * BinningFactor;

            double hotMultiplier = DarkCurrentModel.HotPixelDarkMultiplier(
                referenceBinnedDarkPerSecond, Spec.MaxExposureSeconds, SaturationElectrons(isoGain));

            foreach (int idx in hotPixelIndices)
            {
                if (idx < 0 || idx >= raw.Length) continue;
                double sceneElectrons = signal != null ? signal[idx] : 0.0;   // null = shutter closed
                // Its photo response is the array's, through the same flat field as every other
                // pixel: what makes it hot is its dark current, not its sensitivity to light.
                double collected = (sceneElectrons + skyElectrons * FringeAt(idx)) * FlatFieldAt(idx);
                double mean = Math.Max(0.0, collected + darkElectrons * hotMultiplier);
                raw[idx] = (float)SamplePoisson(rng, mean);
            }

            // No photo response: the pixel collects neither scene light nor sky, but its silicon
            // still generates dark charge at the array's own rate.
            foreach (int idx in deadPixelIndices)
            {
                if (idx < 0 || idx >= raw.Length) continue;
                raw[idx] = (float)SamplePoisson(rng, Math.Max(0.0, darkElectrons));
            }
        }

        /// <summary>
        /// Per-exposure scintillation: the fractional intensity fluctuation the atmosphere
        /// imposes on the target's light, drawn once per frame.
        ///
        /// Drawn from a LOG-NORMAL distribution, not an additive Gaussian. Scintillation is a
        /// multiplicative modulation of an intensity, and an intensity cannot be negative;
        /// atmospheric turbulence redistributes starlight, it does not remove more than all of
        /// it. Real scintillation is in fact measured to be approximately log-normal (Dravins,
        /// Lindegren, Mezey &amp; Young 1997, the same series this pipeline's Young formula and
        /// extended-source suppression already come from), so this is the physically correct
        /// distribution rather than a defensive clamp bolted onto the wrong one.
        ///
        /// The previous form, 1 + N(0, sigma), was unbounded below. Because this factor scales
        /// the TARGET's signal but not the sky background added after it, a single unlucky draw
        /// at large sigma did not merely dim the frame; it INVERTED it: the target went
        /// negative and clamped to black while the sky kept its own positive background and
        /// saturated white. A bright planet came out as a black disc on a white field.
        ///
        /// Parameters are chosen so the multiplier has unit mean and a relative standard
        /// deviation of exactly sigma, matching what the Young/Dravins formula returns: for
        /// X = exp(mu + s*Z), Var(X)/E(X)^2 = exp(s^2) - 1, so s = sqrt(ln(1 + sigma^2)) and
        /// mu = -s^2/2. For small sigma this is indistinguishable from the old form (s -> sigma),
        /// so ordinary observing conditions behave exactly as before.
        /// </summary>
        private static float ScintillationMultiplier(System.Random rng, double sigma)
        {
            if (!(sigma > 0.0) || double.IsNaN(sigma) || double.IsInfinity(sigma)) return 1f;

            double s = Math.Sqrt(Math.Log(1.0 + sigma * sigma));
            double z = NextGaussian(rng, 1f);
            return (float)Math.Exp(-0.5 * s * s + s * z);
        }

        /// <summary>Altitude of a live body above KSC's horizon. Returns false if the home body or the body itself is unavailable.</summary>
        /// <summary>Altitude of a target above the observatory horizon: read from geometry for a body, from the equatorial transform for a fixed position.</summary>
        public static bool TryComputeAltitudeDeg(SkyTarget target, out double altDeg)
        {
            altDeg = 0.0;
            if (target.IsBody) return TryComputeAltitudeDeg(target.Body, out altDeg);
            if (!target.IsEquatorial) return false;

            if (!TryBuildSiteBasis(out _, out _, out _, out double latitudeDeg, out double longitudeDeg)) return false;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return false;

            double meridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                Planetarium.GetUniversalTime(), home.rotationPeriod, home.initialRotation, longitudeDeg);
            altDeg = SkyCoordinates.EquatorialToHorizontal(
                target.RaDeg, target.DecDeg, meridianRaDeg, latitudeDeg).AltitudeDeg;
            return true;
        }

        private static bool TryComputeAltitudeDeg(CelestialBody body, out double altDeg)
        {
            altDeg = 0.0;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || body == null) return false;

            Vector3d obsPos = ObservingPlatform.WorldPosition(home);
            Vector3d up = (obsPos - home.position).normalized;
            Vector3d toBody = (body.position - obsPos).normalized;
            altDeg = 90.0 - Vector3d.Angle(up, toBody);
            return true;
        }

        /// <summary>
        /// Blur from looking through the home world's own atmosphere: the site's real seeing,
        /// at the airmass and wavelength this frame is actually being taken at.
        ///
        /// This is the term that decides what a ground-based image looks like. It is NOT a small
        /// correction on top of diffraction; for every instrument in the catalog it is three to
        /// ten times larger than the telescope's own Airy FWHM, which is precisely why the whole
        /// profession describes these telescopes as seeing-limited.
        ///
        /// Two things it must not do, both of which the previous model did:
        ///
        ///   * It must not vanish at the zenith. The old form was (airmass - 1) * k, i.e. zero
        ///     blur for anything overhead, leaving a perfectly sharp diffraction-limited disk,
        ///     the 8.2m FORS2 resolving Jupiter at 0.017" from the ground. Seeing is the
        ///     atmosphere's own turbulence; looking straight up traverses less of it, not none.
        ///     Zenith is where the site's median DIMM figure is quoted, so that figure IS the
        ///     value here at airmass 1, not the point where the model returns nothing.
        ///
        ///   * It must not depend on the sensor. The old form built a pixel count and multiplied
        ///     by the plate scale, so the same sky delivered four times the angular blur at
        ///     binning 4 as at binning 1. Turbulence has never heard of the camera behind the
        ///     telescope. Everything below is angles throughout.
        ///
        /// Airmass scaling is the standard Kolmogorov result: r0 goes as cos(z)^(3/5), and
        /// FWHM = 0.98*lambda/r0, so FWHM goes as X^(3/5), the relation every site-monitoring
        /// paper uses to reduce DIMM measurements to zenith.
        ///
        /// Wavelength scaling comes from the same two relations: r0 goes as lambda^(6/5), so the
        /// delivered FWHM goes as lambda^(-1/5). Modest, but real and free: the blue channel of
        /// an LRGB set is genuinely softer than the red one through the same air, which is why
        /// planetary imagers stack far more blue frames to get a usable one.
        ///
        /// An instrument with real adaptive optics (VisualTelescopeSpec.AdaptiveOpticsFwhmArcsec,
        /// e.g. SPHERE/ZIMPOL) never takes this path; SAXO cancels the wavefront distortion in
        /// front of the sensor rather than suffering it, so its atmospheric term is the residual
        /// left after correction, solved for in EnsurePsfKernels.
        /// </summary>
        private double ComputeGroundSeeingFwhmArcsec(double airmass)
        {
            if (Spec.AdaptiveOpticsFwhmArcsec > 0.0) return 0.0;

            double zenithFwhm = Spec.ZenithSeeingFwhmArcsec;
            if (!(zenithFwhm > 0.0)) return 0.0;

            // Below the horizon or otherwise unusable geometry: cap rather than run the power
            // law off to infinity. Imaging shouldn't be reachable there anyway.
            if (double.IsNaN(airmass) || double.IsInfinity(airmass) || airmass < 1.0)
                airmass = MaxSeeingAirmass;
            airmass = Math.Min(airmass, MaxSeeingAirmass);

            double lambda = FilterCentralWavelengthMeters(Filter);
            double chromatic = lambda > 0.0
                ? Math.Pow(lambda / SeeingReferenceWavelengthMeters, -0.2)
                : 1.0;

            return zenithFwhm * Math.Pow(airmass, 0.6) * chromatic;
        }

        /// <summary>Real central wavelength (metres) of the filter currently in the wheel, the lambda in lambda/D. Falls back to Luminance for a position this instrument doesn't physically carry.</summary>
        private static double FilterCentralWavelengthMeters(CameraFilter filter)
        {
            double nm;
            switch (filter)
            {
                case CameraFilter.Red:    nm = Spec.RedCentralWavelengthNm; break;
                case CameraFilter.Green:  nm = Spec.GreenCentralWavelengthNm; break;
                case CameraFilter.Blue:   nm = Spec.BlueCentralWavelengthNm; break;
                case CameraFilter.HAlpha: nm = Spec.HAlphaCentralWavelengthNm; break;
                case CameraFilter.OIII:
                case CameraFilter.SII:
                case CameraFilter.NII:
                case CameraFilter.OII:
                case CameraFilter.OI:
                {
                    NarrowbandFilterSpec? nb = Spec.Narrowband(filter);
                    nm = nb.HasValue ? nb.Value.CentralWavelengthNm : 0.0;
                    break;
                }
                default:                  nm = Spec.LuminanceCentralWavelengthNm; break;
            }
            if (nm <= 0.0) nm = Spec.LuminanceCentralWavelengthNm;
            return nm * 1e-9;
        }

        /// <summary>
        /// Draws every unresolved source into the signal plane: the catalogue stars found for
        /// this pointing, and the solar-system bodies too small for the renderer to resolve.
        ///
        /// Both go through the same path because they are the same thing optically, a point of
        /// light of known flux at a known place, and putting them on one path is what keeps a
        /// moon and a star of the same magnitude equally bright in the finished frame.
        /// </summary>
        private void DepositSkyField(float[] signal, FrameComputeInputs inputs, float scintillation)
        {
            int drawn = 0;

            SystemResponse response = inputs.Response;
            double area = inputs.ApertureAreaCm2;
            double exposure = inputs.ExposureSeconds;
            double transmission = inputs.StarNonAtmosphericTransmission * scintillation;

            if (inputs.Stars != null && inputs.Stars.Count > 0)
            {
                // One cache per frame, built here rather than alongside the response: it runs
                // quadratures, this is the background thread, and a frame is one sight line so its
                // stars share nearly all of them. See ReddenedResponseCache.
                var reddening = new ReddenedResponseCache(response);

                drawn = StarFieldRenderer.DepositStars(
                    signal, TextureWidth, TextureHeight,
                    inputs.Stars, inputs.Projection,
                    inputs.StartMeridianRaDeg, inputs.EndMeridianRaDeg,
                    inputs.ObserverLatitudeDeg,
                    inputs.SignalCutoffElectrons,
                    star => StellarPhotometry.CollectedElectrons(
                        star.VMag, star.ColorIndexBV, star.ReddeningEBv,
                        response, reddening, area, exposure, transmission));

                lastReddeningQuadratures = reddening.Evaluations;
            }

            // Supernovae, on the SAME deposit path (trails included). Their electrons come from
            // the template's measured spectrum through the spectrum overload, which is what makes
            // a II-P's H-alpha land in a narrowband filter; everything else about them is a star.
            if (inputs.Supernovae != null && inputs.Supernovae.Count > 0 && response != null)
            {
                var snStars = new List<RenderedStar>(inputs.Supernovae.Count);
                var sightings = new List<SupernovaSighting>(inputs.Supernovae.Count);

                foreach (RenderedSupernova sn in inputs.Supernovae)
                {
                    double width = response.EffectiveWidthAngstromForSpectrum(sn.Shape, sn.EBv);
                    double electrons = PhotonFluxModel.CollectedElectrons(
                        sn.VMagApparent, width, area, exposure) * transmission;

                    snStars.Add(new RenderedStar
                    {
                        RaDeg = sn.RaDeg,
                        DecDeg = sn.DecDeg,
                        VMag = sn.VMagApparent,
                        ColorIndexBV = double.NaN,
                        ReddeningEBv = double.NaN,
                        FixedElectrons = Math.Max(electrons, 1e-12),
                    });

                    var sighting = new SupernovaSighting
                    {
                        Key = sn.Key,
                        HostName = sn.HostName,
                        Class = sn.Class,
                        IsIIb = sn.IsIIb,
                        PhaseDays = sn.PhaseDays,
                        RaDeg = sn.RaDeg,
                        DecDeg = sn.DecDeg,
                        VMagApparent = sn.VMagApparent,
                        PredictedElectrons = electrons,
                        PixelX = double.NaN,
                        PixelY = double.NaN,
                        ExplosionUt = inputs.Ut - sn.PhaseDays * 86400.0,
                    };
                    HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(
                        sn.RaDeg, sn.DecDeg, inputs.EndMeridianRaDeg, inputs.ObserverLatitudeDeg);
                    if (inputs.Projection.TryProject(
                            SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg),
                            out double px, out double py))
                    {
                        sighting.PixelX = px;
                        sighting.PixelY = py;
                    }
                    sightings.Add(sighting);
                }

                drawn += StarFieldRenderer.DepositStars(
                    signal, TextureWidth, TextureHeight,
                    snStars, inputs.Projection,
                    inputs.StartMeridianRaDeg, inputs.EndMeridianRaDeg,
                    inputs.ObserverLatitudeDeg,
                    inputs.SignalCutoffElectrons,
                    ignored => 0.0);

                LastSupernovae = sightings;

                // THE HOST'S OWN LIGHT IS PART OF THE BACKGROUND, and leaving it out is what made
                // the detector call a source the player could not see a 6500 sigma discovery: an
                // event 8 arcsec from its nucleus sits on the galaxy's core, where the surface
                // brightness buries the sky by orders of magnitude. It is sampled from the plane
                // the galaxy was deposited into, before the supernova itself goes in.
                //
                // THE SNR IS CcdEquation.SignalToNoise, not an expression written here. That is
                // the same Merline and Howell equation the photometry uses, cross-validated in
                // tools/photometry-tests, and it carries the terms an ad-hoc sqrt(sky+dark)+read
                // drops: the source's OWN shot noise, the aperture's pixel count, the background
                // estimation factor, and read noise in quadrature rather than added linearly.
                double skyElectrons = Math.Max(0.0, inputs.SkyElectronsPerPixel);
                double darkElectrons = Spec.DarkCurrentElectronsPerSecond * BinningFactor * BinningFactor * inputs.ExposureSeconds;
                double aperturePixels = SupernovaAperturePixels(inputs);
                for (int i = 0; i < sightings.Count; i++)
                {
                    SupernovaSighting sn = sightings[i];
                    double local = SampleSignalAround(signal, sn.PixelX, sn.PixelY);
                    sn.LocalBackgroundElectrons = local;
                    sn.SignalToNoise = CcdEquation.SignalToNoise(
                        sn.PredictedElectrons, aperturePixels,
                        BackgroundAnnulusPixels(aperturePixels),
                        skyElectrons + Math.Max(0.0, local), darkElectrons,
                        Spec.ReadNoiseElectrons, ElectronsPerAdu(Gain));
                    sightings[i] = sn;
                }
                LastSupernovaNoiseElectrons = Math.Sqrt(skyElectrons + darkElectrons) + Spec.ReadNoiseElectrons;
            }

            // Outside the block above on purpose. The diffuse gas does not depend on whether any
            // catalogue star happened to fall in the field, and while it was nested there a frame
            // with no star in it (a narrow field, a short exposure, a gap in the catalogue)
            // rendered no nebula at all.
            DepositEmissionField(signal, inputs);

            if (inputs.UnresolvedBodies != null)
            {
                foreach (PointSource body in inputs.UnresolvedBodies)
                {
                    PointSource scaled = body;
                    scaled.SignalElectrons *= scintillation;
                    StarFieldRenderer.Deposit(signal, TextureWidth, TextureHeight, scaled);
                }
            }

            lastStarsDrawnInternal = drawn;
        }

        /// <summary>
        /// Pixels the detection aperture covers, from the frame's own delivered PSF width and
        /// plate scale through the same CcdEquation helper the photometry uses.
        /// </summary>
        private static double SupernovaAperturePixels(FrameComputeInputs inputs)
        {
            double fwhmArcsec = Math.Max(1e-6, inputs.Pointing.EquivalentFwhmArcsec > 0.0
                ? inputs.Pointing.EquivalentFwhmArcsec : PlateScaleArcsecPerPixel);
            double radiusArcsec = CcdEquation.OptimalApertureRadiusInFwhm * fwhmArcsec;
            return Math.Max(1.0, CcdEquation.AperturePixels(radiusArcsec, PlateScaleArcsecPerPixel));
        }

        /// <summary>Sky annulus area, from the equation's own published aperture-to-annulus ratio.</summary>
        private static double BackgroundAnnulusPixels(double aperturePixels)
            => aperturePixels * CcdEquation.BackgroundToApertureAreaRatio;

        /// <summary>
        /// Mean deposited signal per pixel in a small box around a point: what a source at that
        /// position has to stand out from. Zero off the sensor.
        ///
        /// A BOX MEAN, AND THAT IS THE ONE APPROXIMATION HERE, declared in section 12: the exact
        /// quantity is the host's surface brightness integrated over the detection aperture, and
        /// this is its mean over a 5x5 box. It feeds the DISCOVERY THRESHOLD only. No pixel of the
        /// image, no FITS value and no photometric measurement is computed from it.
        /// </summary>
        private static double SampleSignalAround(float[] plane, double px, double py, int radius = 2)
        {
            if (plane == null || double.IsNaN(px) || double.IsNaN(py)) return 0.0;
            int cx = (int)Math.Round(px), cy = (int)Math.Round(py);
            double sum = 0.0; int n = 0;
            for (int y = cy - radius; y <= cy + radius; y++)
            {
                if (y < 0 || y >= TextureHeight) continue;
                for (int x = cx - radius; x <= cx + radius; x++)
                {
                    if (x < 0 || x >= TextureWidth) continue;
                    sum += plane[y * TextureWidth + x];
                    n++;
                }
            }
            return n > 0 ? sum / n : 0.0;
        }

        /// <summary>
        /// Smears the plane along a straight path, conserving flux, which is what a source sweeping
        /// across the sensor during the exposure actually lays down.
        ///
        /// Implemented as a sliding-window sum along parallel rasterised lines in the drift
        /// direction, so every pixel is visited a constant number of times regardless of how
        /// long the streak is. The naive form, resampling each pixel once per step of the
        /// trail, costs the trail's length per pixel, and an unguided exposure can trail
        /// further than the sensor is wide.
        ///
        /// Light that runs off the edge is gone rather than clamped back in: a body drifting out
        /// of frame really does leave, and edge-clamping would invent flux that was never
        /// collected.
        /// </summary>
        private void ApplyLinearSmear(float[] plane, double driftX, double driftY)
        {
            int w = TextureWidth, h = TextureHeight;
            double length = Math.Sqrt(driftX * driftX + driftY * driftY);
            if (length < 1.0) return;

            float[] smearScratch = EnsurePassScratch(plane.Length);
            Array.Clear(smearScratch, 0, smearScratch.Length);

            // The axis the drift travels furthest along is stepped one pixel at a time, so the
            // rasterised lines have |slope| <= 1 and together cover every pixel exactly once,
            // which is what makes the smear conserve flux rather than gain or lose it to gaps.
            bool xMajor = Math.Abs(driftX) >= Math.Abs(driftY);
            double majorDrift = xMajor ? driftX : driftY;
            double minorDrift = xMajor ? driftY : driftX;
            int window = (int)Math.Round(Math.Abs(majorDrift));
            if (window < 1) return;

            double slope = minorDrift / majorDrift;
            int majorLen = xMajor ? w : h;
            int minorLen = xMajor ? h : w;
            bool forward = majorDrift >= 0.0;

            if (smearLineScratch == null || smearLineScratch.Length < majorLen) smearLineScratch = new float[majorLen];

            // Only the lines that can actually cross the frame are walked. A line rises or falls
            // by slope*(majorLen-1) from end to end, so which side of the frame it has to start
            // outside of depends on the sign of the slope; widening both sides would double
            // the work for lines that are empty by construction.
            int minorSpread = (int)Math.Ceiling(Math.Abs(slope) * (majorLen - 1)) + 1;
            int firstStart = slope >= 0.0 ? -minorSpread : 0;
            int lastStart = slope >= 0.0 ? minorLen : minorLen + minorSpread;
            float invSamples = 1f / (window + 1);

            for (int start = firstStart; start < lastStart; start++)
            {
                // Gathered in DRIFT order, so the smear below is a plain causal box filter.
                for (int k = 0; k < majorLen; k++)
                {
                    int majorPos = forward ? k : majorLen - 1 - k;
                    int minorPos = start + (int)Math.Round(slope * majorPos);
                    smearLineScratch[k] = (minorPos >= 0 && minorPos < minorLen)
                        ? plane[xMajor ? minorPos * w + majorPos : majorPos * w + minorPos]
                        : 0f;
                }

                float running = 0f;
                for (int k = 0; k < majorLen; k++)
                {
                    running += smearLineScratch[k];
                    int leaving = k - window - 1;
                    if (leaving >= 0) running -= smearLineScratch[leaving];

                    int majorPos = forward ? k : majorLen - 1 - k;
                    int minorPos = start + (int)Math.Round(slope * majorPos);
                    if (minorPos >= 0 && minorPos < minorLen)
                        smearScratch[xMajor ? minorPos * w + majorPos : majorPos * w + minorPos] += running * invSamples;
                }
            }

            Array.Copy(smearScratch, plane, plane.Length);
        }

        /// <summary>
        /// Convolves the signal plane with the instrument's PSF. The pipeline is monochrome, so
        /// this works on a single plane rather than three, a third of the transform work for
        /// an identical result.
        ///
        /// Deliberately NOT clamped to full well: a saturated star core has to reach the
        /// blooming pass with its real over-full-well value, or the charge that should spill
        /// down the column is silently discarded here instead.
        /// </summary>
        private void ApplyPsf(float[] plane, float[] kernel, int radius,
                              float coreWeight, float[] haloKernel, int haloRadius)
        {
            if (kernel == null || radius < 1) return;

            int n = plane.Length;
            bool hasHalo = coreWeight < 0.999f
                        && (haloSpectrum != null || (haloKernel != null && haloRadius >= 1));

            // Convolution is linear, so a PSF that is the sum of two components can be applied as
            // the weighted sum of two convolutions, exactly equivalent to convolving once with
            // the combined kernel, but it lets each component be sized to its own scale instead
            // of forcing the compact core to carry the halo's enormous support.
            float[] haloPlane = null;
            if (hasHalo)
            {
                haloPlane = EnsurePassScratch(n);
                Array.Copy(plane, haloPlane, n);

                // The halo goes in as a kernel spanning the whole frame, which is the only support
                // at which it can stop without leaving a step: its theta^(-11/3) wings are still
                // percent-level at any radius a tile can afford. Cut instead at a lag no two
                // sensor pixels can span, it truncates nothing that could have been detected,
                // and the square that used to appear around bright stars on SPHERE was exactly
                // that step. See MaxHaloKernelRadiusPx for the measurements.
                if (haloSpectrum == null)
                    FourierConvolution.Convolve(haloPlane, TextureWidth, TextureHeight, haloKernel, haloRadius);
                else
                    haloSpectrum.Apply(haloPlane, TextureWidth, TextureHeight);
            }

            FourierConvolution.Convolve(plane, TextureWidth, TextureHeight, kernel, radius);

            if (hasHalo)
            {
                for (int i = 0; i < n; i++)
                    plane[i] = coreWeight * plane[i] + (1f - coreWeight) * haloPlane[i];
            }
        }

        /// <summary>
        /// Sky-glow from the home body's moons, in "1.0 = full Mün at zenith, 30 deg away"
        /// units. Uses live 3D positions (not the RA/Dec catalog frame) and the same
        /// Krisciunas &amp; Schaefer (1991) forward-scattering kernel MoonlightPollution uses for
        /// the exoplanet instruments, weighted by each moon's real angular separation from the
        /// imaged body (closer moons pollute the frame far more than distant ones at the same
        /// altitude). A moon being imaged is excluded from its own sky background.
        /// </summary>
        private static double ComputeMoonSkyExcess(SkyTarget target)
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (home == null || sun == null || home.orbitingBodies == null) return 0.0;

            CelestialBody targetBody = target.Body;
            Vector3d obsPos = ObservingPlatform.WorldPosition(home);

            // The moon-target separation drives the scattering kernel, so a fixed target needs the
            // aim direction rather than a body position.
            Vector3d toTarget = Vector3d.zero;
            bool haveTarget = false;
            if (target.IsBody)
            {
                toTarget = targetBody.position - obsPos;
                haveTarget = toTarget.sqrMagnitude > 1e-6;
                if (haveTarget) toTarget = toTarget.normalized;
            }
            else if (target.IsEquatorial)
            {
                haveTarget = TryEquatorialDirection(target.RaDeg, target.DecDeg,
                                                    Planetarium.GetUniversalTime(), out toTarget);
            }

            double kernelAtReference = MoonlightPollution.ScatteringKernel(30.0);
            double referenceFlux = MoonReferenceFluxUnits(home);
            if (referenceFlux <= 0.0) return 0.0; // a home world with no moons has no lunar pollution
            double total = 0.0;
            foreach (CelestialBody moon in home.orbitingBodies)
            {
                if (moon == null || moon == targetBody) continue;
                if (!TryComputeAltitudeDeg(moon, out double altDeg) || altDeg <= 0.0) continue;

                Vector3d toMoon = (moon.position - obsPos).normalized;
                double separationDeg = haveTarget ? Vector3d.Angle(toTarget, toMoon) : 90.0;

                Vector3d toSunFromMoon = (sun.position - moon.position).normalized;
                Vector3d toHomeFromMoon = (home.position - moon.position).normalized;
                double phaseAngleDeg = Vector3d.Angle(toSunFromMoon, toHomeFromMoon);
                double illuminated = (1.0 + Math.Cos(phaseAngleDeg * Math.PI / 180.0)) / 2.0;

                double distance = Math.Max(1.0, (moon.position - home.position).magnitude);
                double sizeRatio = moon.Radius / distance;
                double moonFlux = Math.Max(0.0, moon.albedo) * illuminated * sizeRatio * sizeRatio;
                double altitudeRamp = Math.Min(1.0, altDeg / 10.0);
                double scatterWeight = MoonlightPollution.ScatteringKernel(separationDeg) / kernelAtReference;

                total += (moonFlux / referenceFlux) * altitudeRamp * scatterWeight;
            }
            return total;
        }

        /// <summary>
        /// Real electrons collected from the imaged body this exposure: its real apparent
        /// magnitude (PhotonFluxModel.ApparentMagnitude, from real albedo/radius/positions),
        /// converted through the instrument's integrated spectral response: aperture and
        /// obstruction, mirror and relay throughput, filter profile, the detector's own QE curve,
        /// and atmospheric extinction across the whole passband (see SystemBandpass).
        ///
        /// The body's spectrum is the Sun's, because that is what it is: a planet shines by
        /// reflected sunlight, so its photon spectrum is the solar one modulated by the surface's
        /// reflectance. The reflectance is treated as grey, since a KSP CelestialBody carries a
        /// single albedo and no wavelength dependence to read (see SourceSpectra).
        ///
        /// nonAtmosphericTransmission carries the losses the response does not: cloud cover. The
        /// ND filter is applied here, as it always was.
        ///
        /// Zero if any required geometry is missing.
        /// </summary>
        private double ComputeCollectedElectrons(CelestialBody targetBody, SystemResponse response,
                                                 double nonAtmosphericTransmission, float exposureSeconds)
        {
            if (response == null) return 0.0;
            CelestialBody home = FlightGlobals.GetHomeBody();
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (home == null || sun == null || targetBody == null) return 0.0;

            Vector3d obsPos = ObservingPlatform.WorldPosition(home);
            double distanceToObserverMeters = (targetBody.position - obsPos).magnitude;
            double distanceToSunMeters = (targetBody.position - sun.position).magnitude;
            if (distanceToObserverMeters < 1.0 || distanceToSunMeters < 1.0) return 0.0;

            Vector3d toSunFromBody = (sun.position - targetBody.position).normalized;
            Vector3d toObserverFromBody = (obsPos - targetBody.position).normalized;
            double phaseAngleRad = Vector3d.Angle(toSunFromBody, toObserverFromBody) * Math.PI / 180.0;

            double magnitude = PhotonFluxModel.ApparentMagnitude(
                targetBody.albedo, targetBody.Radius, distanceToSunMeters, distanceToObserverMeters, phaseAngleRad);

            double width = response.EffectiveWidthAngstromForTemperature(SourceSpectra.SolarPhotosphereTemperatureK);
            double apertureAreaCm2 = RealApertureAreaCm2();
            double greyTransmission = Math.Max(0.0, nonAtmosphericTransmission) * NdFilterTransmission(NdFilter);

            return PhotonFluxModel.CollectedElectrons(magnitude, width, apertureAreaCm2, exposureSeconds)
                 * greyTransmission;
        }

        /// <summary>
        /// Angular diameter (radians) of targetBody as seen from KSC right now, feeds
        /// AtmosphericImagingNoise.ScintillationExcessSigma's extended-source suppression
        /// (a resolved planetary disk, unlike a star, isn't a point source). Small-angle
        /// approximation (2*radius/distance), which is fine at solar-system distances.
        /// </summary>
        /// <summary>targetBody's apparent diameter in arcsec as seen from KSC right now, paired with PlateScaleArcsecPerPixel this is what decides how many pixels across the disk actually lands on, i.e. whether any surface detail is resolvable in principle.</summary>
        public static double AngularDiameterArcsec(CelestialBody targetBody)
            => ComputeAngularDiameterRad(targetBody) * (180.0 / Math.PI) * 3600.0;

        /// <summary>Zero for a fixed target: a star is a point source, and that is what the scintillation model needs to be told.</summary>
        public static double AngularDiameterArcsec(SkyTarget target)
            => target.IsBody ? AngularDiameterArcsec(target.Body) : 0.0;

        private static double ComputeAngularDiameterRad(CelestialBody targetBody)
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || targetBody == null) return 0.0;

            Vector3d obsPos = ObservingPlatform.WorldPosition(home);
            double distanceMeters = (targetBody.position - obsPos).magnitude;
            if (distanceMeters < 1.0) return 0.0;

            return 2.0 * targetBody.Radius / distanceMeters;
        }

        /// <summary>Real effective collecting area (cm^2): full aperture minus the real secondary-mirror obstruction.</summary>
        private static double RealApertureAreaCm2() => EffectiveApertureAreaM2(Spec) * 1.0e4; // m^2 -> cm^2

        /// <summary>Real cosmic-ray hit rate: sea-level flux (~1/cm^2/min) over the sensor's real, native (binning-independent) physical silicon area.</summary>
        private static float ComputeCosmicRayHitsPerSecond()
        {
            double fluxPerCm2PerMinute = Spec.CosmicRayEventsPerMinutePerCm2;
            double sideXCm = NativeTextureWidth * NativePixelSizeMeters * 100.0;
            double sideYCm = NativeTextureHeight * NativePixelSizeMeters * 100.0;
            double areaCm2 = sideXCm * sideYCm;
            return (float)(fluxPerCm2PerMinute * areaCm2 / 60.0);
        }

        /// <summary>Real filter bandwidth in Angstrom for the active telescope's own real filter set (VisualTelescopeSpec), each filter's real bandwidth, not a fraction of Luminance, since a research instrument's R/G/B are each their own named filter with their own published FWHM (unlike an amateur LRGB wheel, where an even split is the real design; see VisualTelescopeCatalog.Rc20's own comment).</summary>
        private static double FilterBandwidthAngstrom(CameraFilter filter)
        {
            switch (filter)
            {
                case CameraFilter.Red:    return Spec.RedBandwidthAngstrom;
                case CameraFilter.Green:  return Spec.GreenBandwidthAngstrom;
                case CameraFilter.Blue:   return Spec.BlueBandwidthAngstrom;
                case CameraFilter.HAlpha: return Spec.HAlphaBandwidthAngstrom;
                default:
                {
                    NarrowbandFilterSpec? nb = Spec.Narrowband(filter);
                    return nb.HasValue ? nb.Value.BandwidthAngstrom : LuminanceBandwidthAngstrom;
                }
            }
        }

        /// <summary>
        /// Peak transmission of the fitted filter. A non-positive value means the instrument's
        /// maker publishes no figure for that filter, in which case the loss is left unmodelled
        /// (1.0) rather than invented; see VisualTelescopeSpec's own field comment.
        /// </summary>
        private static double FilterPeakTransmission(CameraFilter filter)
        {
            double t;
            switch (filter)
            {
                case CameraFilter.Red:    t = Spec.RedFilterPeakTransmission; break;
                case CameraFilter.Green:  t = Spec.GreenFilterPeakTransmission; break;
                case CameraFilter.Blue:   t = Spec.BlueFilterPeakTransmission; break;
                case CameraFilter.HAlpha: t = Spec.HAlphaFilterPeakTransmission; break;
                default:
                {
                    NarrowbandFilterSpec? nb = Spec.Narrowband(filter);
                    t = nb.HasValue ? nb.Value.PeakTransmission : Spec.LuminanceFilterPeakTransmission;
                    break;
                }
            }
            return t > 0.0 ? t : 1.0;
        }

        /// <summary>
        /// The instrument's total spectral response for this filter at this airmass: filter
        /// profile, optical throughput, detector QE curve and atmospheric extinction, ready to be
        /// integrated against any source's spectrum (see SystemBandpass).
        ///
        /// Built once per capture on the main thread and then read by the background pipeline for
        /// every source in the frame. The ND filter is deliberately NOT included: it is applied
        /// per source, because the resolved bodies and the star field pass through different
        /// transmission chains (a body's chain omits the star field's cloud term, and vice versa),
        /// and folding it in here would make it impossible to keep those apart.
        /// </summary>
        internal static SystemResponse SystemResponseForColour(CameraFilter filter)
            => BuildSystemResponse(filter, 1.0);

        private static SystemResponse BuildSystemResponse(CameraFilter filter, double airmass)
        {
            SpectralCurve filterCurve = FilterTransmissionCurve(filter);

            // A measured curve carries the filter's own transmission, so the published peak must
            // NOT be applied on top of it; that would count the filter twice.
            double transmission = filterCurve != null
                ? Spec.OpticsTransmission
                : FilterPeakTransmission(filter) * Spec.OpticsTransmission;

            return new SystemResponse(
                FilterCentralWavelengthMeters(filter),
                FilterBandwidthAngstrom(filter),
                transmission,
                filterCurve,
                Spec.QuantumEfficiencyCurve,
                Spec.QuantumEfficiency,
                airmass,
                Spec.SiteAltitudeMeters);
        }

        /// <summary>The instrument's measured curve for this filter position, or null when only published numbers exist and the top-hat applies.</summary>
        private static SpectralCurve FilterTransmissionCurve(CameraFilter filter)
        {
            switch (filter)
            {
                case CameraFilter.Red: return Spec.RedFilterCurve;
                case CameraFilter.Green: return Spec.GreenFilterCurve;
                case CameraFilter.Blue: return Spec.BlueFilterCurve;
                default: return null; // Luminance is unfiltered; H-alpha has no published curve here
            }
        }

        /// <summary>
        /// Full-well overflow: any pixel above FullWellValue spills the excess into its
        /// vertical neighbors (the CCD column/shift-register direction), which can themselves
        /// overflow in turn, producing the familiar bloom trail through a saturated star or
        /// planet limb instead of a hard-clipped blob. Operates in place, pre-clamp.
        /// </summary>
        private void ApplyBlooming(float[] raw, float fullWellElectrons)
        {
            int w = TextureWidth, h = TextureHeight;
            for (int iter = 0; iter < BloomingMaxIterations; iter++)
            {
                bool anyOverflow = false;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x;
                        float overflow = raw[i] - fullWellElectrons;
                        if (overflow <= 0f) continue;
                        anyOverflow = true;
                        raw[i] = fullWellElectrons;
                        float share = overflow * BloomingSpillFraction;
                        if (y > 0) raw[i - w] += share;
                        if (y < h - 1) raw[i + w] += share;
                    }
                }
                if (!anyOverflow) break;
            }
        }

        /// <summary>
        /// Charge-transfer smear along the vertical (readout) direction: walks each column
        /// top to bottom, capturing a fraction of every pixel's signal into a per-column trap
        /// state and releasing a fraction of what's trapped back into the next pixel down,
        /// the nc/nr structure of Short et al. (2010)'s CDM, simplified to constant per-row
        /// capture/release fractions (see CtiCaptureFraction/CtiReleaseFraction). Reads as a
        /// faint trailing streak below anything bright, the classic CTI signature.
        /// </summary>
        private void ApplyChargeTransferSmear(float[] raw)
        {
            int w = TextureWidth, h = TextureHeight;
            for (int x = 0; x < w; x++)
            {
                float trapped = 0f;
                for (int y = 0; y < h; y++)
                {
                    int i = y * w + x;
                    float captured = raw[i] * CtiCaptureFraction;
                    raw[i] -= captured;
                    trapped += captured;

                    float released = trapped * CtiReleaseFraction;
                    trapped -= released;
                    raw[i] += released;
                }
            }
        }

        /// <summary>
        /// Releases into this exposure whatever the surface traps have been holding, emptying each
        /// of the two populations by its own exponential over the exposure's duration.
        ///
        /// THE ELAPSED TIME IS THE EXPOSURE'S OWN, and the dead time between subs is not part of
        /// it. Charge released while the shutter is shut still ends up in the next frame read, so
        /// nothing is lost; what is lost is the DISTINCTION between a sequence taken back to back
        /// and one with gaps in it, and that distinction cannot be made because this pipeline has
        /// no cadence model to read a gap from. Stated here rather than left implicit, and stated
        /// in section 12. It biases in the safe direction: a residual is reported decaying no
        /// faster than it really does.
        /// </summary>
        private void ApplyPersistenceRelease(float[] raw, float exposureSeconds)
        {
            if (!Spec.HasPersistence) return;
            if (persistenceFastTrapped == null || persistenceSlowTrapped == null) return;
            if (!(exposureSeconds > 0f)) return;

            int n = raw.Length;
            if (persistenceFastTrapped.Length != n || persistenceSlowTrapped.Length != n) return;

            // The decay factors are the same for every pixel, so they are computed once rather
            // than n times: only the trapped charge varies across the array.
            double fastRemaining = Math.Exp(-exposureSeconds / Spec.PersistenceFastDecaySeconds);
            double slowRemaining = Math.Exp(-exposureSeconds / Spec.PersistenceSlowDecaySeconds);

            for (int i = 0; i < n; i++)
            {
                float fast = persistenceFastTrapped[i];
                float slow = persistenceSlowTrapped[i];
                if (fast <= 0f && slow <= 0f) continue;

                float fastLeft = (float)(fast * fastRemaining);
                float slowLeft = (float)(slow * slowRemaining);

                raw[i] += (fast - fastLeft) + (slow - slowLeft);
                persistenceFastTrapped[i] = fastLeft;
                persistenceSlowTrapped[i] = slowLeft;
            }
        }

        /// <summary>
        /// Releases into this exposure the persistence the infrared array's earlier stimulus is
        /// still producing, and ages that stimulus by the exposure's length.
        ///
        /// INTEGRATED OVER THE EXPOSURE, not sampled at its midpoint. The published model returns a
        /// RATE that falls as a power law with index near 1, so over a long exposure taken soon
        /// after a bright one the rate changes by a large factor from start to finish; taking the
        /// value at the middle would be wrong by a percent or so in exactly the case that matters.
        /// Core.HgCdTePersistence.IntegrateElectrons does the integral in closed form.
        /// </summary>
        private void ApplyHgCdTePersistenceRelease(float[] raw, float exposureSeconds)
        {
            if (!Spec.HasHgCdTePersistence) return;
            if (hgcdtePersistenceFluence == null) return;
            if (!(exposureSeconds > 0f)) return;

            int n = raw.Length;
            if (hgcdtePersistenceFluence.Length != n) return;

            for (int i = 0; i < n; i++)
            {
                float fluence = hgcdtePersistenceFluence[i];
                if (fluence <= 0f) continue;

                double from = hgcdtePersistenceElapsedSeconds[i];
                double to = from + exposureSeconds;

                raw[i] += (float)HgCdTePersistence.IntegrateElectrons(
                    fluence, from, to, hgcdtePersistenceStimulusSeconds[i]);

                hgcdtePersistenceElapsedSeconds[i] = (float)to;
            }
        }

        /// <summary>
        /// Records what this exposure leaves behind for the next one: the fluence each pixel
        /// reached and how long it sat there.
        ///
        /// THE NEW STIMULUS REPLACES THE OLD ONLY WHERE IT WOULD CAUSE MORE PERSISTENCE, which is
        /// the published pipeline's own rule and the reason this is a comparison rather than an
        /// assignment. Compared at a common reference delay rather than at each stimulus's own
        /// elapsed time, because "which causes more persistence" has to be asked of the same moment
        /// for the answer to mean anything; 1000 s is used, the delay the model's amplitude is
        /// normalised at.
        /// </summary>
        private void RecordHgCdTeStimulus(float[] raw, float exposureSeconds)
        {
            if (!Spec.HasHgCdTePersistence) return;

            int n = raw.Length;
            if (hgcdtePersistenceFluence == null || hgcdtePersistenceFluence.Length != n)
            {
                hgcdtePersistenceFluence = new float[n];
                hgcdtePersistenceStimulusSeconds = new float[n];
                hgcdtePersistenceElapsedSeconds = new float[n];
            }

            const double ReferenceDelaySeconds = 1000.0;

            for (int i = 0; i < n; i++)
            {
                double candidate = HgCdTePersistence.RateElectronsPerSecond(
                    raw[i], ReferenceDelaySeconds, exposureSeconds);

                double incumbent = hgcdtePersistenceFluence[i] > 0f
                    ? HgCdTePersistence.RateElectronsPerSecond(
                          hgcdtePersistenceFluence[i], ReferenceDelaySeconds,
                          hgcdtePersistenceStimulusSeconds[i])
                    : 0.0;

                if (candidate <= incumbent) continue;

                hgcdtePersistenceFluence[i] = raw[i];
                hgcdtePersistenceStimulusSeconds[i] = exposureSeconds;
                // The clock starts at the end of the stimulus exposure, so a following exposure
                // beginning immediately sees the model at t = 0. The integral's lower limit guards
                // the singularity there; see IntegrateElectrons.
                hgcdtePersistenceElapsedSeconds[i] = 0f;
            }
        }

        /// <summary>
        /// Takes into the surface traps what this exposure's well charge leaves behind, splitting it
        /// between the fast and the slow population in the ratio the device's two-exponential fit
        /// gives.
        ///
        /// Allocates the state arrays on first use rather than with the other maps, so that an
        /// instrument with no published amplitude carries no per-pixel cost at all: on this roster
        /// that is every one of them.
        /// </summary>
        private void ApplyPersistenceCapture(float[] raw)
        {
            if (!Spec.HasPersistence) return;

            int n = raw.Length;
            if (persistenceFastTrapped == null || persistenceFastTrapped.Length != n)
                persistenceFastTrapped = new float[n];
            if (persistenceSlowTrapped == null || persistenceSlowTrapped.Length != n)
                persistenceSlowTrapped = new float[n];

            double fullWell = FullWellElectrons;
            double threshold = Spec.PersistenceThresholdFractionOfFullWell;
            double fraction = Spec.PersistenceTrappedFraction;
            double density = Spec.PersistenceTrapDensityElectrons;
            double fastShare = Spec.PersistenceFastShare;

            for (int i = 0; i < n; i++)
            {
                double held = persistenceFastTrapped[i] + persistenceSlowTrapped[i];
                double captured = DetectorPersistence.Capture(
                    raw[i], fullWell, threshold, fraction, density, held);
                if (captured <= 0.0) continue;

                DetectorPersistence.Split(captured, fastShare, out double toFast, out double toSlow);
                persistenceFastTrapped[i] += (float)toFast;
                persistenceSlowTrapped[i] += (float)toSlow;

                // The charge is held, not duplicated: it leaves the well it was captured from.
                raw[i] -= (float)captured;
            }
        }

        /// <summary>
        /// Cosmic ray hits: a flat Poisson process over the exposure deposits short, randomly
        /// angled bright tracks (Pyxel's CosmiX/TARS approach, minus the angle model; see
        /// CosmicRayHitsPerSecond), distinct from the fixed hot-pixel map since a real muon/proton
        /// strike lands anywhere, at a random angle, on every exposure independently.
        /// </summary>
        private void ApplyCosmicRays(float[] raw, float exposureSeconds, System.Random rng)
        {
            int w = TextureWidth, h = TextureHeight;
            double expectedHits = CosmicRayHitsPerSecond * exposureSeconds;
            int hits = (int)SamplePoisson(rng, expectedHits);

            for (int n = 0; n < hits; n++)
            {
                int x0 = rng.Next(w);
                int y0 = rng.Next(h);
                double angle = rng.NextDouble() * 2.0 * Math.PI; // isotropic incidence in the sensor plane
                int length = CosmicRayMinTrackPx + rng.Next(CosmicRayMaxTrackPx - CosmicRayMinTrackPx);
                double dx = Math.Cos(angle), dy = Math.Sin(angle);

                // WHAT ONE EVENT DEPOSITS, and why it is not a fraction of full well.
                //
                // A cosmic ray leaves a measured amount of charge, and for these detectors it is
                // published: WFC3 IHB Sect. 5.4.10, "negligible events of less than 500 e- and a
                // median of ~1000 e-". Spread along a track of a few pixels that is a mark clearly
                // above read noise and nowhere near saturation, which is what a raw HST frame
                // actually looks like. The old full-well fraction put ~53,550 e- in every pixel of
                // every track, some 350 times the published event total, and turned a real and
                // correctly-counted population of cosmic rays into a screen of white worms.
                //
                // Instruments with no published figure keep the old behaviour rather than
                // borrowing HST's: a sea-level muon through a different depletion depth is not the
                // same event, and guessing it here would be inventing a measurement.
                double perEvent = Spec.CosmicRayElectronsPerEvent;
                float deposit = perEvent > 0.0
                    ? (float)(perEvent / Math.Max(1, length))
                    : CosmicRayDepositWellFraction * (float)FullWellElectrons;
                bool additive = perEvent > 0.0;

                for (int s = 0; s < length; s++)
                {
                    int x = x0 + (int)Math.Round(dx * s);
                    int y = y0 + (int)Math.Round(dy * s);
                    if (x < 0 || x >= w || y < 0 || y >= h) break;
                    int i = y * w + x;
                    // Charge ADDS to what the pixel already collected, and stops at full well:
                    // a strike does not erase the sky under it, and it cannot hold more than the
                    // well does. The old max() did neither.
                    if (additive) raw[i] = (float)Math.Min(raw[i] + deposit, FullWellElectrons);
                    else if (raw[i] < deposit) raw[i] = deposit;
                }
            }
        }

        /// <summary>
        /// A Poisson deviate, delegated to Core.NoiseSampler so that the implementation lives
        /// where a headless harness can reach it (see tools/photometry-roundtrip, which checks it
        /// against SciPy). Kept as a wrapper rather than replaced at every call site so that the
        /// detector chain still reads as one narrative.
        /// </summary>
        private static double SamplePoisson(System.Random rng, double lambda)
            => NoiseSampler.Poisson(rng, lambda);

        /// <summary>
        /// Third-order astigmatism: transverse blur scaling with the square of the normalized
        /// field radius, smeared radially outward from frame center, a simplified stand-in
        /// for the radially-elongated star image real astigmatism produces at one of its two
        /// focus positions in an off-axis RC/Ritchey-Chretien field. Zero at the target itself
        /// (centered by definition), worst for background stars near the corners.
        /// </summary>
        private void ApplyAstigmatismBlur(float[] plane)
        {
            int w = TextureWidth, h = TextureHeight;
            int n = w * h;
            float[] astigmatismScratch = EnsurePassScratch(n);
            Array.Copy(plane, astigmatismScratch, n);

            float cx = w / 2f, cy = h / 2f;
            float maxR = Mathf.Sqrt(cx * cx + cy * cy);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float rNorm = r / maxR;
                    float smear = AstigmatismStrengthPxAtCorner * rNorm * rNorm;
                    int steps = Mathf.CeilToInt(smear);
                    if (steps < 1) continue;

                    float nx = dx / Mathf.Max(1e-4f, r);
                    float ny = dy / Mathf.Max(1e-4f, r);
                    float sum = 0f;
                    for (int s = 0; s <= steps; s++)
                    {
                        int sx = Mathf.Clamp(x + Mathf.RoundToInt(nx * s), 0, w - 1);
                        int sy = Mathf.Clamp(y + Mathf.RoundToInt(ny * s), 0, h - 1);
                        sum += astigmatismScratch[sy * w + sx];
                    }
                    plane[y * w + x] = sum / (steps + 1);
                }
            }
        }

        /// <summary>Cloud coverage over KSC from EVE, or 0 if EVE isn't installed or has no cloud layer for the home body.</summary>
        internal static float ComputeCloudCoverage()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return 0f;

            Vector3d obsPos = ObservingPlatform.WorldPosition(home);
            Vector3d worldUp = (obsPos - home.position).normalized;
            Vector3 bodyFixedUp = home.bodyTransform != null
                ? home.bodyTransform.InverseTransformDirection((Vector3)worldUp)
                : (Vector3)worldUp;

            return EveCloudIntegration.SampleCoverage(home.bodyName, bodyFixedUp);
        }

        /// <summary>
        /// The sensor's known, fixed bad-pixel map (hot + dead indices combined), the same
        /// list a real calibration workflow would have from a dark-frame characterization,
        /// used by AstroImageStack to cosmetically correct each sub before stacking.
        /// </summary>
        public int[] GetDefectPixelIndices()
        {
            EnsureDefectMap();
            var combined = new int[hotPixelIndices.Length + deadPixelIndices.Length];
            hotPixelIndices.CopyTo(combined, 0);
            deadPixelIndices.CopyTo(combined, hotPixelIndices.Length);
            return combined;
        }

        /// <summary>
        /// The coronagraph's focal-plane mask, applied to the formed image.
        ///
        /// A hard opaque disc at the frame's own centre, which is where the instrument is pointed
        /// and therefore where the star it is occulting sits. Nothing else is done to the light
        /// here, and that is correct rather than incomplete: the OTHER half of a Lyot coronagraph,
        /// the pupil stop, has already acted, upstream and invisibly, by being the pupil this
        /// frame's point-spread function was computed from (see PupilApertureMeters).
        ///
        /// What a real observer measures as the peak attenuation R_coro is therefore not an input
        /// to this method but a CONSEQUENCE of it: block the core, and what is left is the halo at
        /// the mask rim. tools/coronagraph-tests measures that ratio on a rendered frame and
        /// compares it with ESO's published 110 to 3000, which is a check on the halo model rather
        /// than on this multiplication.
        /// </summary>
        private void ApplyCoronagraphMask(float[] signal, FrameComputeInputs inputs)
        {
            var mask = SelectedCoronagraphMask;
            if (!mask.HasValue || signal == null) return;

            int w = TextureWidth, h = TextureHeight;
            double platePerPixelMas = inputs.PlateScaleArcsec * 1000.0;
            if (!(platePerPixelMas > 0.0)) return;

            double cx = 0.5 * (w - 1), cy = 0.5 * (h - 1);
            double radiusPx = mask.Value.RadiusMas / platePerPixelMas;
            double radiusPx2 = radiusPx * radiusPx;
            float transmission = (float)mask.Value.SpotTransmission;

            int y0 = Math.Max(0, (int)Math.Floor(cy - radiusPx));
            int y1 = Math.Min(h - 1, (int)Math.Ceiling(cy + radiusPx));
            int x0 = Math.Max(0, (int)Math.Floor(cx - radiusPx));
            int x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + radiusPx));

            for (int y = y0; y <= y1; y++)
            {
                double dy = y - cy;
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - cx;
                    if (dx * dx + dy * dy <= radiusPx2) signal[row + x] *= transmission;
                }
            }

            // The Lyot stop's own throughput, which is a real loss of light and is applied to the
            // whole frame rather than only under the mask: everything in this image came through
            // the stop. ESO measure it as 0.91 times the stop's geometric transmission, the 0.91
            // being the share of blocked light that was diffracted rather than useful.
            float throughput = (float)CoronagraphThroughput;
            if (throughput < 1.0f)
                for (int i = 0; i < signal.Length; i++) signal[i] *= throughput;
        }

        /// <summary>
        /// Turns the smooth adaptive-optics halo into the speckle field it really is.
        ///
        /// WHY THIS RUNS WHENEVER THERE IS ADAPTIVE OPTICS, coronagraph or not. Speckles are what
        /// an AO-corrected point-spread function is made of; the coronagraph does not create them,
        /// it removes the core that was hiding them. Gating this on the mask would produce the
        /// absurdity of a frame that gets noisier when the star is blocked.
        ///
        /// TWO SEEDS, AND THE DIFFERENCE BETWEEN THEM IS THE PHYSICS. The static half is drawn
        /// from the instrument's own fixed serial seed mixed with the pointing, so every exposure
        /// of the same field carries the SAME frozen pattern: that is what makes a speckle
        /// indistinguishable from a companion in one frame and distinguishable across a rotating
        /// sequence. The temporal half is drawn from the exposure's own seed, so it is a fresh
        /// realisation each time and averages down within the exposure. If both came from the
        /// exposure's seed the speckles would be ordinary noise wearing a fixed pattern's name,
        /// and angular differential imaging would have nothing to remove.
        ///
        /// WHAT THIS DOES TO A RESOLVED BODY, stated because it is a real limitation. The
        /// modulation multiplies the whole signal plane, including an extended target's own disc.
        /// A real extended source averages over the speckles its own light produces, so its
        /// granularity is suppressed by roughly the number of resolution elements it covers, and
        /// this does not model that suppression. It is the right treatment for the point sources a
        /// coronagraph is pointed at and an overestimate of the granularity on a resolved disc
        /// (section 12).
        /// </summary>
        private void ApplySpeckleField(float[] signal, FrameComputeInputs inputs, ulong captureSeed)
        {
            if (signal == null) return;
            int actuators = Spec.AdaptiveOpticsActuatorsAcrossPupil;
            if (actuators < 2) return;

            int w = TextureWidth, h = TextureHeight;
            int n = w * h;
            if (n <= 0 || signal.Length < n) return;

            double platePerPixelMas = inputs.PlateScaleArcsec * 1000.0;
            if (!(platePerPixelMas > 0.0)) return;

            double wavelengthNm = FilterCentralWavelengthMeters(Filter) * 1e9;
            double lambdaOverD = SpeckleField.LambdaOverDMas(wavelengthNm, PupilApertureMeters);
            if (!(lambdaOverD > 0.0)) return;

            // A grain smaller than a pixel cannot be rendered, and pretending otherwise would
            // produce white noise wearing a speckle field's name. ZIMPOL samples lambda/D at about
            // eleven pixels, so this never fires there; it is a guard for a future instrument that
            // undersamples its own diffraction limit.
            if (lambdaOverD < platePerPixelMas) return;

            double windSpeed = DefaultSpeckleWindSpeedMetersPerSecond;

            double surviving = SpeckleField.SurvivingVarianceFraction(
                inputs.ExposureSeconds, PupilApertureMeters, windSpeed);

            // The realisation count that reproduces that surviving variance, inverted from it so
            // the two can never disagree: a field built from N realisations of the temporal part
            // has variance f + (1-f)/N, and N is what makes that equal what the timescales say.
            double f = SpeckleField.StaticVarianceFraction;
            double excess = Math.Max(1e-9, surviving - f);
            double realisations = Math.Max(1.0, (1.0 - f) / excess);

            if (speckleScratch == null || speckleScratch.Length != n) speckleScratch = new float[n];

            // The static seed carries the SENSOR and the POINTING and not the time: the same field
            // observed again on another night shows the same speckles, and a different field does
            // not. Rounded to a milliarcsecond of pointing, which is far finer than the speckles
            // themselves and coarse enough that a re-acquisition of the same target lands on the
            // same pattern.
            ulong staticSeed = Pcg32.MixSeed(
                SensorSerialSeed, inputs.TargetSeed, (long)Math.Round(wavelengthNm, 0));

            SpeckleField.BuildModulation(
                speckleScratch, w, h, platePerPixelMas, lambdaOverD,
                f, realisations, staticSeed, captureSeed);

            for (int i = 0; i < n; i++) signal[i] *= speckleScratch[i];

            lastSpeckleSurvivingVariance = surviving;
            lastSpeckleRealisations = realisations;
            lastSpeckleControlRadiusMas =
                SpeckleField.ControlRadiusMas(actuators, wavelengthNm, PupilApertureMeters);
        }

        /// <summary>
        /// Wind speed used for the atmospheric speckle lifetime when the observing conditions
        /// carry none: 4 m/s, the value Milli et al. (2016) report for the SPHERE sequence their
        /// decorrelation timescales are measured from, so the model runs at the conditions its
        /// own numbers were taken under rather than at an invented default.
        /// </summary>
        private const double DefaultSpeckleWindSpeedMetersPerSecond = 4.0;

        private float[] speckleScratch;
        private double lastSpeckleSurvivingVariance = 1.0;
        private double lastSpeckleRealisations = 1.0;
        private double lastSpeckleControlRadiusMas;

        /// <summary>Surviving speckle variance, realisation count and AO control radius of the last capture, for the FITS header.</summary>
        public double LastSpeckleSurvivingVariance => lastSpeckleSurvivingVariance;
        public double LastSpeckleRealisations => lastSpeckleRealisations;
        public double LastSpeckleControlRadiusMas => lastSpeckleControlRadiusMas;

        /// <summary>
        /// The sensor's flat field: what fraction of the light that entered the telescope each
        /// pixel actually converts, relative to the array's mean. Built once per instrument and
        /// binning, held as the DEVIATION FROM UNITY so that half precision costs nothing (see
        /// Core.SensorNonUniformity for that argument in full).
        ///
        /// ONE MAP FOR TWO PHYSICALLY SEPARATE TERMS, and that is not a shortcut: the optics'
        /// illumination falloff and the silicon's photo-response spread multiply, and their PRODUCT
        /// is precisely and only what a flat frame measures. Keeping them apart in storage would
        /// mean holding two maps to reconstruct a quantity that is never used except as one.
        ///
        /// Both components are properties of this instrument at this binning rather than of the
        /// exposure, which is why the map outlives the frame and is discarded with the buffers when
        /// the telescope or the binning changes.
        /// </summary>
        private void EnsureFlatFieldMap()
        {
            if (flatFieldMap != null) return;

            int width = TextureWidth, height = TextureHeight;
            int n = width * height;
            if (n <= 0) return;

            // A flat the observer actually took REPLACES the model rather than multiplying it.
            //
            // That is not a policy choice, it follows from what the two are. The parametric map
            // below is the product of the illumination falloff and the photo-response spread, and
            // the comment on this method says why they are stored as one: their product is
            // precisely and only what a flat frame measures. A real flat therefore already contains
            // both terms, plus the three that no model here can produce at all - tree rings, dust
            // motes and accessory vignetting (see Core.MeasuredFlatField). Multiplying the two
            // together would apply the modelled vignetting twice.
            if (TryLoadMeasuredFlatField(width, height, n)) return;

            // The photo-response spread belongs to the sensor's own pixel, and the pixel this frame
            // is made of may be several of them: the display binning on top of whatever the camera
            // already sums internally (see VisualTelescopeSpec.SensorNativePixelsPerSide).
            int nativePerSide = Math.Max(1, Spec.SensorNativePixelsPerSide) * Math.Max(1, BinningFactor);
            double prnuSigma = SensorNonUniformity.BinnedPhotoResponseSigma(
                Spec.PhotoResponseNonUniformity, nativePerSide);

            // The same fixed serial seed the defect map uses: one piece of silicon, one set of
            // blemishes, drawn on its own stream so neither map can shift the other.
            ushort[] prnu = SensorNonUniformity.BuildPhotoResponseMap(
                Pcg32.MixSeed(SensorSerialSeed), n, prnuSigma);

            // Geometry for the illumination term: where each pixel sits in the focal plane, in
            // metres from the optical axis, which is what the cosine-fourth law and any field stop
            // are both expressed in.
            double pixelPitchMetres = Spec.NativePixelSizeMeters * Math.Max(1, BinningFactor);
            double focalLengthMetres = Spec.FocalLengthMeters;
            double centreX = 0.5 * (width - 1);
            double centreY = 0.5 * (height - 1);

            var map = new ushort[n];
            for (int y = 0; y < height; y++)
            {
                double dy = (y - centreY) * pixelPitchMetres;
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    double dx = (x - centreX) * pixelPitchMetres;
                    double illumination = FocalPlaneIllumination.Factor(
                        dx, dy, focalLengthMetres,
                        Spec.FieldStopSquareArcmin, Spec.ImageCircleMillimetres);

                    int i = row + x;
                    double response = illumination * SensorNonUniformity.PhotoResponse(prnu, i);
                    map[i] = Float16.FromDouble(response - 1.0);
                }
            }

            flatFieldMap = map;
        }

        /// <summary>
        /// The detector's fringe map: how much more or less of the SKY each pixel records because
        /// its own silicon is a slightly different thickness from its neighbour's.
        ///
        /// APPLIED TO THE SKY AND NOT TO THE SCENE, which is the whole character of the effect
        /// rather than a shortcut. Fringing is an interference modulation of the detector's
        /// response, and how strongly it bites depends on the SPECTRUM of what is being detected:
        /// a source with isolated emission lines samples the modulation at a few phases and fringes
        /// hard, while a smooth continuum runs it through many turns and cancels itself. The night
        /// sky past 700 nm is a picket fence of OH bands and fringes at a percent; a star is a
        /// continuum and does not. tools/fringe-tests measures that difference at a factor of
        /// eleven on one bandwidth. It is also why a real observer defringes by subtracting a
        /// SCALED SKY FRAME rather than by dividing a flat.
        ///
        /// Built once per instrument, binning and FILTER, the filter being what decides how much of
        /// the modulation survives the passband integral.
        /// </summary>
        private void EnsureFringeMap()
        {
            if (fringeMap != null && fringeMapFilter == Filter) return;

            fringeMap = null;
            fringeMapFilter = Filter;

            double thickness = Spec.DetectorSiliconThicknessMicrons;
            double variation = Spec.DetectorThicknessVariationFraction;
            double scalePx = Spec.DetectorThicknessVariationScalePixels;
            if (double.IsNaN(thickness) || !(thickness > 0.0)) return;
            if (double.IsNaN(variation) || !(variation > 0.0)) return;
            if (double.IsNaN(scalePx) || !(scalePx > 0.0)) return;

            int width = TextureWidth, height = TextureHeight;
            int n = width * height;
            if (n <= 0) return;

            // The passband, as a top-hat of the filter's own published centre and width, which is
            // what the rest of this pipeline uses wherever a real measured curve is not carried.
            double centreNm = FilterCentralWavelengthMeters(Filter) * 1e9;
            double widthNm = FilterBandwidthAngstrom(Filter) * 0.1;
            if (!(centreNm > 0.0) || !(widthNm > 0.0)) return;

            double lo = Math.Max(AirglowTable.MinWavelengthNm, centreNm - 0.5 * widthNm);
            double hi = Math.Min(AirglowTable.MaxWavelengthNm, centreNm + 0.5 * widthNm);
            if (!(hi > lo)) return;

            // Nothing to compute if the band stops short of where silicon becomes transparent
            // enough to have a second surface. That is most of this roster's filters.
            if (hi <= FringeOnsetWavelengthNm) return;

            Func<double, double> sky = l => Airglow.LineDensityAtZenith(l) + Airglow.ContinuumDensityAtZenith(l);
            Func<double, double> response = l => (l >= lo && l <= hi) ? 1.0 : 0.0;

            // The thickness map: smooth, on the measured spatial scale, from the serial seed. Its
            // AMPLITUDE and its SCALE are Walsh et al.'s measurements; only this realisation is a
            // draw, exactly as the flat field's is.
            var thicknessMap = new float[n];
            SpeckleField.BuildModulation(
                thicknessMap, width, height,
                plateScaleMasPerPixel: 1.0, lambdaOverDMas: scalePx,
                staticVarianceFraction: 1.0 - 1e-9, realisations: 1.0,
                staticSeed: Pcg32.MixSeed(SensorSerialSeed, 0x46524E47L), temporalSeed: 1UL);

            var map = new ushort[n];
            // The map's own values have unit mean and unit variance, so scaling their deviation to
            // the published peak-to-peak fraction (four sigma of a unit-variance field spans it)
            // gives a thickness field of the measured amplitude.
            double thicknessSigma = thickness * variation / 4.0;

            for (int i = 0; i < n; i++)
            {
                double localThickness = thickness + (thicknessMap[i] - 1.0) * thicknessSigma;
                double path = Fringing.OpticalPathNm(localThickness, centreNm);
                double m = Fringing.Modulation(path, sky, response, lo, hi, AirglowTable.StepNm);
                map[i] = Float16.FromDouble(m - 1.0);
            }

            fringeMap = map;
        }

        /// <summary>Below this the absorption length in silicon is far shorter than any thinned layer and there is no second surface to interfere with. Walsh et al.'s own 774 nm flat showed no fringes at all.</summary>
        private const double FringeOnsetWavelengthNm = 774.0;

        private ushort[] fringeMap;
        private CameraFilter fringeMapFilter = (CameraFilter)(-1);

        /// <summary>This pixel's fringe factor on the sky: 1 where nothing is modelled.</summary>
        private float FringeAt(int index)
        {
            if (fringeMap == null || index < 0 || index >= fringeMap.Length) return 1f;
            return 1f + (float)Float16.ToDouble(fringeMap[index]);
        }

        /// <summary>The sensor's per-pixel readout offsets in electrons, built once per instrument and binning for the same reasons as the flat field above.</summary>
        private void EnsureOffsetFpnMap()
        {
            if (offsetFpnMap != null) return;
            int n = TextureWidth * TextureHeight;
            if (n <= 0) return;

            int nativePerSide = Math.Max(1, Spec.SensorNativePixelsPerSide) * Math.Max(1, BinningFactor);
            double sigma = SensorNonUniformity.BinnedOffsetSigmaElectrons(
                Spec.OffsetFixedPatternElectrons, nativePerSide);

            offsetFpnMap = SensorNonUniformity.BuildOffsetMap(Pcg32.MixSeed(SensorSerialSeed), n, sigma);
        }

        /// <summary>
        /// Where a measured flat is looked for, and what it has to be called.
        ///
        /// A flat belongs to ONE optical train at ONE filter at ONE binning, so all three are in
        /// the name. The dust that makes a real flat worth having sits on a filter or a window, so
        /// a flat taken through Luminance does not describe the Hydrogen-alpha path; and the
        /// binning has to match because the map is per frame pixel, not per sensor pixel.
        /// </summary>
        private string MeasuredFlatPath(int binning)
        {
            string camera = Sanitise(Spec.CameraName);
            string filter = Sanitise(Filter.ToString());
            return KSPUtil.ApplicationRootPath
                 + "GameData/ExoInstruments/PluginData/Flat_"
                 + camera + "_" + filter + "_bin" + binning + ".fits";
        }

        private static string Sanitise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            return sb.ToString();
        }

        /// <summary>
        /// Loads the observer's own flat, if there is one, and turns it into this frame's response
        /// map. Returns false when there is no file, which is the ordinary case and is silent.
        ///
        /// A FILE THAT EXISTS BUT CANNOT BE USED IS LOUD AND DOES NOT FALL BACK QUIETLY to the
        /// modelled flat. Someone who put a file there meant to calibrate against it, and silently
        /// substituting a parametric map would leave them reducing against a flat they think is
        /// theirs and is not. The message names the measured reason, from Core.MeasuredFlatField.
        /// </summary>
        private bool TryLoadMeasuredFlatField(int width, int height, int n)
        {
            int binning = Math.Max(1, BinningFactor);
            string path;
            try { path = MeasuredFlatPath(binning); }
            catch (Exception) { return false; }

            if (!System.IO.File.Exists(path)) return false;

            try
            {
                var image = FitsImageReader.Read(path);

                // The pedestal. Preferred from the file's own header, because the number that
                // matters is the one the OBSERVER's camera wrote and not this instrument's model:
                // PEDESTAL is what SharpCap and NINA write, BLKLEVEL what the SBIG-derived
                // vocabulary uses. Falling back on the modelled bias is stated in the log rather
                // than assumed silently, since a wrong pedestal biases every response toward unity.
                double k = Spec.ElectronsPerAduAtUnityGain;
                double bias = Spec.EffectiveBiasLevelAdu(k);
                string biasSource = "this instrument's modelled bias";
                foreach (string keyword in new[] { "PEDESTAL", "BLKLEVEL" })
                {
                    string card = image.Card(keyword);
                    if (card != null && double.TryParse(card.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double v))
                    {
                        bias = v;
                        biasSource = keyword + " from the file";
                        break;
                    }
                }

                var flat = MeasuredFlatField.Build(image, width, height, bias, AdcMaxCount, k);

                var map = new ushort[n];
                for (int i = 0; i < n; i++)
                    map[i] = Float16.FromDouble(flat.Response[i] - 1.0);
                flatFieldMap = map;

                Debug.Log("[ExoInstruments] Measured flat loaded from " + path
                        + " (" + flat.Summary + "; pedestal from " + biasSource
                        + "). The modelled flat is not applied on top of it.");

                if (flat.NoiseWarning)
                    Debug.LogWarning("[ExoInstruments] This flat's pixel-to-pixel scatter ("
                        + (100.0 * flat.ResponseSigma).ToString("F3") + " %) is close to its own"
                        + " shot-noise floor (" + (100.0 * flat.ShotNoiseFloor).ToString("F3")
                        + " %), so most of what it will stamp into every frame is this one file's"
                        + " random noise rather than the detector's response. Stack a master flat"
                        + " from several subs.");

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[ExoInstruments] The flat at " + path + " exists but cannot be"
                    + " used, and the modelled flat is NOT being silently substituted for it: "
                    + e.Message);
                return false;
            }
        }

        /// <summary>
        /// The fraction of the array's mean response this pixel has: 1 where nothing is modelled,
        /// 0 outside a field stop, and 1 +/- a few parts in a thousand elsewhere.
        /// </summary>
        private float FlatFieldAt(int index)
        {
            if (flatFieldMap == null || index < 0 || index >= flatFieldMap.Length) return 1f;
            return 1f + (float)Float16.ToDouble(flatFieldMap[index]);
        }

        /// <summary>
        /// The sensor's "serial number": the constant that makes its blemishes the same silicon in
        /// every session and on every machine. Shared by the defect map, the flat field and the
        /// offset map, because they are features of one physical device.
        /// </summary>
        private const long SensorSerialSeed = 20260721L;

        /// <summary>Builds the hot/dead pixel index lists once from a constant seed (same defects every session).</summary>
        private void EnsureDefectMap()
        {
            if (hotPixelIndices != null) return;
            // The defect map is a property of this particular piece of silicon, so it is drawn from
            // a fixed "serial number" seed and is the same every session. On its own stream, and on
            // Pcg32 rather than System.Random, so that it is also the same on every machine and
            // every .NET runtime; a bad pixel map that moved between platforms would make any
            // reference frame unusable as a comparison.
            var rng = new Pcg32(Pcg32.MixSeed(SensorSerialSeed), Pcg32.StreamDefectMap);
            int total = TextureWidth * TextureHeight;
            int hotCount = Mathf.Max(1, total / 3000);
            int deadCount = Mathf.Max(1, total / 6000);
            hotPixelIndices = new int[hotCount];
            for (int i = 0; i < hotCount; i++) hotPixelIndices[i] = rng.Next(total);
            deadPixelIndices = new int[deadCount];
            for (int i = 0; i < deadCount; i++) deadPixelIndices[i] = rng.Next(total);
        }

        /// <summary>Box-Muller Gaussian sample with the given sigma (mean 0).</summary>
        private static float NextGaussian(System.Random rng, float sigma)
            => (float)NoiseSampler.Gaussian(rng, sigma);

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);

        /// <summary>Signal a mono sensor records through the given filter (L = luminance, R/G/B = single channel, H-alpha = red).</summary>
        private static float FilterSignal(Color c, CameraFilter filter)
        {
            switch (filter)
            {
                case CameraFilter.Red:    return c.r;
                case CameraFilter.Green:  return c.g;
                case CameraFilter.Blue:   return c.b;
                case CameraFilter.HAlpha: return c.r; // H-alpha sits in the deep red
                // Which rendered channel a narrowband line falls in. The render is the only
                // source of spatial shading for a resolved body, and a line lands in whichever
                // channel covers its wavelength.
                case CameraFilter.OII:    return c.b;  // 372.7 nm, the blue edge
                case CameraFilter.OIII:   return c.g;  // 500.7 nm
                case CameraFilter.OI:
                case CameraFilter.NII:
                case CameraFilter.SII:    return c.r;  // 630 to 673 nm
                default:                  return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            }
        }

        public void Dispose()
        {
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }
            if (renderTexture != null)
            {
                renderTexture.Release();
                UnityEngine.Object.Destroy(renderTexture);
                renderTexture = null;
            }
            if (readbackTexture != null) { UnityEngine.Object.Destroy(readbackTexture); readbackTexture = null; }
            if (capturedTexture != null) { UnityEngine.Object.Destroy(capturedTexture); capturedTexture = null; }
            scaledSpaceCam = null;

            // Every resolution-sized scratch buffer/state must be rebuilt fresh at whatever
            // resolution EnsureSceneBuilt runs next at (native size change on a binning switch).
            pixelScratch = null;
            frameScratch = null;
            passScratch = null;
            displayScratch = null;
            rawScratch = null;
            signalScratch = null;
            smearLineScratch = null;
            hotPixelIndices = null;
            deadPixelIndices = null;
            flatFieldMap = null;
            offsetFpnMap = null;
            fringeMap = null;
            // Trapped charge belongs to one piece of silicon at one binning. Changing either means
            // the pixel it was held in no longer exists, so the sequence's memory is discarded with
            // the buffers rather than reinterpreted onto a different array.
            persistenceFastTrapped = null;
            persistenceSlowTrapped = null;
            hgcdtePersistenceFluence = null;
            hgcdtePersistenceStimulusSeconds = null;
            hgcdtePersistenceElapsedSeconds = null;
            lastCaptureSnapshot = null;
            hasLockedAim = false;
        }
    }

    /// <summary>A supernova as the frame needs it: where, how bright in the mod's V, and its measured spectrum at this phase.</summary>
    public struct RenderedSupernova
    {
        public string Key;
        public string HostName;
        public Core.SupernovaClass Class;
        public bool IsIIb;
        public double PhaseDays;
        public double RaDeg;
        public double DecDeg;
        public double VMagApparent;
        public double EBv;
        public Core.SpectralCurve Shape;
    }

    /// <summary>What one frame recorded about one supernova: enough for the discovery check and the logbook, nothing more.</summary>
    public struct SupernovaSighting
    {
        public string Key;
        public string HostName;
        public Core.SupernovaClass Class;
        public bool IsIIb;
        public double PhaseDays;
        public double RaDeg;
        public double DecDeg;
        public double VMagApparent;
        public double PredictedElectrons;

        /// <summary>Mean deposited signal per pixel around the event, dominated by the host near a nucleus. What it must stand out from.</summary>
        public double LocalBackgroundElectrons;

        /// <summary>Detection signal-to-noise from CcdEquation, with the host's light at the event included in the background.</summary>
        public double SignalToNoise;
        public double PixelX;
        public double PixelY;
        public double ExplosionUt;
    }
}
