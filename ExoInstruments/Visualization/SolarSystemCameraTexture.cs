using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>Filter-wheel positions. A mono CCD shoots one filter at a time, so each is its own grayscale frame -- the filter just selects which of the rendered scene's channels (and how much throughput) forms the signal.</summary>
    /// <summary>
    /// Display transfer function applied when a finished frame is turned into something the eye
    /// can read. DISPLAY ONLY -- the science path (GetLastCaptureFullPrecision, the FITS export
    /// and everything AstroImageStack stacks) always receives the untouched linear signal, which
    /// is the same separation every real observing tool keeps between its viewer and its data.
    ///
    /// No astronomical image is looked at linearly. A resolved planetary disk puts almost all of
    /// its pixels into a narrow bright range, so real surface contrast -- a few percent of the
    /// local level -- occupies a handful of the 256 levels an 8-bit display has and is invisible,
    /// even though the data holds it perfectly. Every real viewer (DS9, PixInsight, IRAF, ESO's
    /// Reflex) therefore offers exactly this choice of stretch.
    /// </summary>
    public enum DisplayStretch
    {
        /// <summary>Raw linear signal, faithful to the detector but the hardest to read on a bright extended source.</summary>
        Linear,
        /// <summary>Logarithmic, DS9's own formulation y = log(a*x + 1) / log(a + 1) with its default a = 1000 (Joye &amp; Mandel 2003, ADASS XII, the SAOImage DS9 paper). Strong lift of faint detail; compresses the bright end hard.</summary>
        Log,
        /// <summary>Inverse hyperbolic sine, the astronomical standard from Lupton et al. 2004 (PASP 116, 133, "Preparing Red-Green-Blue Images from CCD Data"). Linear near zero and logarithmic far from it, so it lifts faint structure without crushing bright regions the way a pure log does -- what SDSS's own imagery uses.</summary>
        Asinh
    }

    public enum CameraFilter
    {
        Luminance, // clear/L: full luminance, maximum throughput
        Red,
        Green,
        Blue,
        HAlpha     // narrowband: red channel only, low throughput (needs longer exposure)
    }

    /// <summary>
    /// Neutral-density filter slot, real optical-density stops used by real astrophotographers
    /// on targets too bright for exposure/gain alone to handle (Kerbin's compressed-scale solar
    /// system puts nearby moons in exactly that regime -- Mun sits only a few magnitudes fainter
    /// than Kerbol itself). OD/transmission values: Nd8/Nd64/Nd1000 are the standard photographic
    /// ND stops (OD 0.9/1.8/3.0, transmission = 10^-OD); Nd100000 matches the real optical density
    /// of a Baader AstroSolar safety film / Thousand Oaks solar filter (OD ~5.0), the real
    /// accessory class used for direct imaging of the brightest object in the sky.
    /// </summary>
    public enum NdFilterStop
    {
        None,
        Nd8,
        Nd64,
        Nd1000,
        Nd100000
    }

    /// <summary>
    /// RC20 astrograph camera: clones KSP's scaled-space and galaxy cameras and
    /// points them at a solar-system body from KSC. Same technique as Tarsier Space
    /// Technology's TSTCameraModule, reimplemented here to avoid the dependency.
    /// Outputs a monochrome, noisy "single raw CCD frame" through a full physics
    /// pipeline (extinction, shot noise, seeing, EVE cloud cover, moon scattering,
    /// cosmic rays, full-well blooming, charge-transfer smear, astigmatism) -- see
    /// ProcessFrame for the per-effect citations.
    /// </summary>
    public class SolarSystemCameraTexture : IDisposable
    {
        // All optics/sensor identity constants (aperture, focal length, native resolution,
        // pixel pitch, QE, full well, exposure/gain range, ...) live in VisualTelescopeCatalog,
        // not here -- this class is the rendering pipeline for whichever spec it's pointed at.
        // Mutable (not readonly): the player can switch instruments from the Observatory dropdown
        // in the GUI (ExoInstrumentsGUI.SelectObservatory), which re-derives every optics/sensor-
        // driven quantity below from whichever VisualTelescopeSpec is now active. See
        // SetActiveTelescope for the switch itself, and builtSpec/EnsureSceneBuilt for how the
        // render targets and scratch buffers get rebuilt at the new instrument's resolution.
        private static VisualTelescopeSpec Spec = VisualTelescopeCatalog.Rc20;

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
        /// with an exposure-time calculator when changing instruments -- without it, a exposure
        /// tuned for the RC20's 0.51m aperture carried straight over to the VLT's 8.2m one
        /// (~258x the collecting area) blows every pixel far past full well, and the pipeline's
        /// per-column blooming (ApplyBlooming, real CCD physics, only ever spills vertically)
        /// turns the saturated body into a tall white bar instead of a photo. Then clamped into
        /// the new instrument's real exposure range.
        ///
        /// Forces Autoguiding on for a spec with AlwaysAutoguided (a real research telescope like
        /// the VLT has no bare/unguided mode) -- otherwise Autoguiding, being a plain player-set
        /// toggle, would silently carry over whatever the player last chose on the RC20/CDK1000,
        /// including off.
        ///
        /// Does NOT discard an already-captured photo or stacked subs -- those belong to the
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

            FovDeg = MaxFovDeg;
            ExposureSeconds = Mathf.Clamp(ExposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            Gain = Mathf.Clamp(Gain, MinGain, MaxGain);
            if (spec.AlwaysAutoguided) Autoguiding = true;
            if (Array.IndexOf(spec.AvailableFilters, Filter) < 0) Filter = CameraFilter.Luminance;
        }

        /// <summary>Real effective light-collecting area (m^2): full aperture minus the real secondary-mirror obstruction. Shared by SetActiveTelescope's exposure rescaling and RealApertureAreaCm2's per-frame photon-flux calc -- same physical quantity, different units for each caller's convenience.</summary>
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
        /// Pixel binning factor (1=native resolution, 2/3/4 = NxN binning) -- the real technique
        /// astrophotography acquisition software (SharpCap, NINA) offers for exactly this
        /// trade-off (resolution vs. processing cost/noise). Changing this rebuilds the
        /// camera's textures and scratch buffers on the next capture.
        /// </summary>
        public static int BinningFactor { get; set; } = 4;

        public static int TextureWidth => NativeTextureWidth / BinningFactor;
        public static int TextureHeight => NativeTextureHeight / BinningFactor;

        /// <summary>Real (binned) pixel pitch in microns -- for FITS XPIXSZ/YPIXSZ header keywords.</summary>
        public static double PixelSizeMicrons => NativePixelSizeMeters * BinningFactor * 1e6;

        /// <summary>Real focal length in mm -- for the FITS FOCALLEN header keyword.</summary>
        public static double FocalLengthMm => RealFocalLengthMeters * 1000.0;

        /// <summary>
        /// Real full well AT THE CURRENT BINNING, in electrons -- for FITS header info and the
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

        /// <summary>Real plate scale at the current binning: arcsec per (binned) pixel, from the telescope's real focal length and the sensor's real pixel pitch. Public because it's the single number that decides whether a target is resolvable at all -- real acquisition software (SharpCap, NINA, ESO's own ETCs) all put it front and center for exactly that reason.</summary>
        public static float PlateScaleArcsecPerPixel
        {
            get
            {
                float pixelSizeMeters = NativePixelSizeMeters * BinningFactor;
                float plateScaleRad = pixelSizeMeters / RealFocalLengthMeters;
                return plateScaleRad * (180f / Mathf.PI) * 3600f;
            }
        }

        /// <summary>Native (no-accessory) field of view across the sensor's long axis -- the "wide" end of the zoom range.</summary>
        public static float MaxFovDeg => (TextureWidth * PlateScaleArcsecPerPixel) / 3600f;

        /// <summary>Field of view with a real Barlow -- the "high power" end of the zoom range.</summary>
        public static float MinFovDeg => MaxFovDeg / BarlowFactor;

        private const string GalaxyCameraName = "GalaxyCamera";
        private const string ScaledSpaceCameraName = "Camera ScaledSpace";

        // Real filter bandwidths in Angstrom, matching FilterThroughput's ratios: L covers the
        // whole ~420-685nm visible band; R/G/B each get an even third (modern "1:1:1 balanced"
        // CMOS LRGB filter design); H-alpha is a real ~7nm (70 Angstrom) narrowband filter.
        private static double LuminanceBandwidthAngstrom => Spec.LuminanceBandwidthAngstrom;

        /// <summary>Real sensor exposure range -- see VisualTelescopeCatalog for sourcing.</summary>
        public static float MinExposureSeconds => Spec.MinExposureSeconds;
        public static float MaxExposureSeconds => Spec.MaxExposureSeconds;

        private const float MaxDefocusBlurPx = 7.0f;
        private const float SeeingBlurPxPerAirmass = 1.4f;
        private const float MaxSeeingBlurPx = 6.0f;

        // Sky brightness ramps from -12 deg (capture cutoff) down to astronomical twilight at -18 deg.
        private const double AstronomicalTwilightSunAltitudeDeg = -18.0;
        private const double TwilightSkyBackgroundRatePerSecond = 0.30; // at the -12 deg threshold
        private const double MoonGlowRatePerSecond = 0.02;              // full Mün at zenith
        private const double AirglowBaselinePerSecond = 0.004;          // always-present night-sky glow
        internal const double CloudMaxAttenuation = 0.85;                // thick cloud, never 100% opaque
        private const double CloudHazeRatePerSecond = 0.25;             // veiling scattered light off cloud base
        private const float CloudBlurPxMax = 2.0f;
        // NOT amplified by gain (applied after the analog stage) -- the active telescope's real
        // read noise (a fixed per-readout-event electron figure, unaffected by binning) as a
        // fraction of the CURRENT BINNED full well (this class's own FullWellElectrons, not
        // Spec.FullWellElectrons directly) -- the same sensor anchor used for shot and
        // dark-current noise. Binning genuinely reduces read noise's relative significance in a
        // real sensor (same read noise electrons, now a smaller slice of a bigger binned well),
        // which this correctly reflects.
        private static float ReadNoiseSigmaValue => (float)(Spec.ReadNoiseElectrons / FullWellElectrons);

        /// <summary>
        /// Reference flux the moonlight term is expressed in: the home world's own brightest moon,
        /// full and at its mean distance, as albedo * (radius/distance)^2. So MoonSkyExcess = 1
        /// always means "this system's full moon overhead", whatever system that is.
        ///
        /// Derived at runtime rather than hardcoded, because the same absolute number means very
        /// different things on different home worlds. Stock's Mün is a 200km body only 12,000km
        /// away; the real Moon is 1737km at 384,400km, and the ratio of those two fluxes is a
        /// factor of ~13.6. A constant calibrated on one makes moonlight roughly an order of
        /// magnitude wrong on the other -- and it is a real observational effect worth getting
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

        // Zodiacal light, relative to AirglowBaselinePerSecond via the real Pogson magnitude-
        // ratio relation (already how MoonlightPollution converts magnitudes to flux elsewhere
        // in this mod): Leinert et al. (1998, A&AS 127, 1) give V=23.3 mag/arcsec^2 zodiacal
        // light at the ecliptic pole (its faintest, most conservative value); Patat (2003, A&A
        // 400, 1183) gives V~21.7 mag/arcsec^2 for typical new-moon zenith dark-sky brightness
        // at a dark site (dominated by airglow, the same phenomenon AirglowBaselinePerSecond
        // represents). ratio = 10^(-0.4*(23.3-21.7)) = 10^-0.64 = 0.229, so zodiacal = 0.229 *
        // AirglowBaselinePerSecond. The mod has no real ecliptic geometry for Kerbol, so this
        // stays a fixed baseline rather than a position-dependent term.
        private const double ZodiacalBaselineRatePerSecond = AirglowBaselinePerSecond * 0.229;

        // Full-well overflow ("blooming"): Pyxel only hard-clips at full well and ships no
        // redistribution model to port. Real CCD full-well overflow is described in Janesick
        // (2001, "Scientific Charge-Coupled Devices", SPIE Press) as charge spilling along the
        // column (parallel/vertical shift-register) direction; absent a specific device's
        // anti-blooming-gate asymmetry data, the textbook default is a charge-conserving,
        // symmetric split between the two vertical neighbors -- 0.5 to each means all of the
        // excess is conserved, none invented or discarded.
        private const float FullWellValue = 1.0f;
        private const float BloomingSpillFraction = 0.5f;

        // Numerical convergence cap for the cascading overflow above (a spilled-over pixel can
        // itself overflow into the next), not a physical quantity -- same role as the 50-
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
        // pixel pitch -- the physical exposed area doesn't change with binning, only how pixels
        // are grouped on readout, so this is computed from the native resolution regardless of
        // the camera's current BinningFactor). A property, not a cached field: it must re-read
        // Spec every call so a telescope switch with a different sensor recomputes the real
        // rate instead of silently keeping the old instrument's.
        private static float CosmicRayHitsPerSecond => ComputeCosmicRayHitsPerSecond();
        private const int CosmicRayMinTrackPx = 2;
        private const int CosmicRayMaxTrackPx = 14;
        private const float CosmicRayDepositValue = 0.85f;

        // Astigmatism: the radial-quadratic FORM (Seidel aberration theory: S_II/coma scales
        // linearly with field, S_III/astigmatism quadratically -- see Schroeder, "Astronomical
        // Optics" 2nd ed. 2000, Ch. 6, or Rutten & van Venrooij, "Telescope Optics") is the same
        // for every two-mirror astrograph in this pipeline, so it's applied here regardless of
        // which telescope is active; the PEAK amplitude at the frame corner is instrument-
        // specific (VisualTelescopeSpec.AstigmatismStrengthPxAtCorner) since it depends on that
        // telescope's own optical prescription and how completely its design cancels off-axis
        // aberrations -- see each catalog entry's own comment for its sourcing.
        private static float AstigmatismStrengthPxAtCorner => Spec.AstigmatismStrengthPxAtCorner;

        private bool builtOnce;
        private int builtBinningFactor = -1;
        private VisualTelescopeSpec builtSpec;
        private bool available;

        private GameObject root;
        private Camera galaxyCam;
        private Camera scaledSpaceCam;
        private RenderTexture renderTexture;
        private Texture2D readbackTexture;
        private Texture2D outputTexture;
        private Texture2D capturedTexture;
        private Color[] pixelScratch;
        private Color[] blurScratch;
        private Color[] frameScratch;
        private float[] psfPlaneScratch;
        private float[] psfHaloScratch;
        private Color[] displayScratch;

        /// <summary>
        /// Display transfer function for finished frames. Affects only what is shown and the PNG
        /// quick-look; the FITS export and the stacking path always get the linear signal.
        /// </summary>
        public static DisplayStretch Stretch { get; set; } = DisplayStretch.Asinh;

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
        /// Radius ceiling for the seeing-halo kernel. Larger than the core's because the halo is
        /// genuinely that wide (Paranal's 0.65" is 361 px across at ZIMPOL's unbinned scale), but
        /// still bounded: the transform size, and so the cost of every capture, grows with it.
        /// Truncating a halo's far wings and renormalising leaves its FWHM untouched -- only the
        /// faint outermost flux is redistributed, and at these radii that flux is already a flat
        /// pedestal across the whole field.
        /// </summary>
        private const int MaxHaloKernelRadiusPx = 256;

        // Built PSF, cached on everything it depends on (see EnsurePsfKernels).
        private VisualTelescopeSpec psfCacheSpec;
        private CameraFilter psfCacheFilter;
        private double psfCachePlateScale = -1.0;
        private double psfCacheAtmosphericFwhm = -1.0;
        private double psfCacheDefocusRadius = -1.0;
        private float[] psfCacheCore;
        private int psfCacheCoreRadius;
        private float psfCacheCoreWeight = 1f;
        private float[] psfCacheHalo;
        private int psfCacheHaloRadius;
        private double psfCacheDiffractionFwhm;
        private float[] rawScratch;
        private float[] astigmatismScratch;
        private float[] rowPrefixScratch;

        private Renderer[] skyboxRenderers;
        private ScaledSpaceFader[] scaledSpaceFaders;

        // Fixed hot/dead pixel map: a chip's defect pattern is persistent, so seeded
        // once from a constant, never from the target or UT.
        private int[] hotPixelIndices;
        private int[] deadPixelIndices;

        /// <summary>Real continuous gain control range -- see VisualTelescopeCatalog for sourcing.</summary>
        public static float MinGain => Spec.MinGain;
        public static float MaxGain => Spec.MaxGain;
        /// <summary>Field of view in degrees (zoom). Clamped to [MinFovDeg, MaxFovDeg]. Defaults to
        /// the wide end (MaxFovDeg) -- the old flat 3.0 default predates the real derived FOV
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

        /// <summary>Real optical-density transmission (10^-OD) for each ND filter stop -- see NdFilterStop for sourcing.</summary>
        public static double NdFilterTransmission(NdFilterStop stop)
        {
            switch (stop)
            {
                case NdFilterStop.Nd8: return Math.Pow(10.0, -0.9);
                case NdFilterStop.Nd64: return Math.Pow(10.0, -1.8);
                case NdFilterStop.Nd1000: return Math.Pow(10.0, -3.0);
                case NdFilterStop.Nd100000: return Math.Pow(10.0, -5.0);
                default: return 1.0;
            }
        }
        /// <summary>Sensor gain, [MinGain, MaxGain]: higher = brighter + noisier.</summary>
        public float Gain { get; set; } = 1.0f;
        /// <summary>When true the mount tracks the sky (no drift). Off by default — a bare RC20 has no autoguider.</summary>
        public bool Autoguiding { get; set; } = false;

        // --- Timed-exposure capture state ----------------------------------
        private bool isCapturing;
        private float captureElapsed;
        private float captureDuration;
        private CelestialBody pendingTarget;

        // --- Background processing state (the heavy per-pixel physics pipeline runs off the
        // main thread once the exposure's integration time has elapsed -- see GatherFrameInputs
        // /ComputeFramePixels/PollProcessTask) ------------------------------
        private Task<Color[]> processTask;
        private bool isProcessing;

        /// <summary>True while a timed exposure is integrating (between BeginExposure and completion).</summary>
        public bool IsCapturing => isCapturing;
        /// <summary>0..1 progress through the current timed exposure.</summary>
        public float CaptureProgress => isCapturing && captureDuration > 0f ? Mathf.Clamp01(captureElapsed / captureDuration) : 0f;
        /// <summary>True while the captured frame's noise/effects pipeline is running on a background task, after the exposure's integration time has elapsed but before the photo is ready.</summary>
        public bool IsProcessing => isProcessing;
        /// <summary>True once a timed exposure has completed and a finished photo is available.</summary>
        public bool HasCapturedPhoto { get; private set; }

        private Color[] lastCaptureSnapshot;

        /// <summary>False only if KSP's own scaled-space/galaxy cameras can't be found (should not happen on a stock install).</summary>
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
        /// sensor's clipping was applied -- i.e. genuinely blown-out, their real surface contrast
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
        /// <summary>Why the last capture failed, or null if it succeeded. Shown in the panel -- see PollProcessTask.</summary>
        public string LastProcessingError { get; private set; }

        /// <summary>True when the graphics device refused the render target at the current binning -- every capture at this resolution will be garbage until the player bins down.</summary>
        public bool RenderTargetRefused => renderTextureRefused;
        private bool renderTextureRefused;

        /// <summary>
        /// The untouched Unity render behind the last capture, before any of this pipeline's
        /// physics. Diagnostic only, and the one measurement that cleanly attributes a bad frame:
        /// if this is already wrong the fault is in the game's own scaled-space rendering or in
        /// how the clone cameras are set up, and no change to the optical or sensor model can
        /// recover detail that was never drawn; if this is right and the finished frame is not,
        /// the fault is downstream and reproducible offline. Written out only when the player
        /// asks for it (see the Diagnostics toggle) -- it costs a second full-resolution PNG.
        /// </summary>
        public Texture2D RawRenderFrame => readbackTexture;

        public float LastScintillationFactor => lastScintillationFactor;
        public float LastScintillationSigma => lastScintillationSigma;
        private float lastScintillationFactor = 1f;
        private float lastScintillationSigma;

        /// <summary>Atmospheric FWHM (arcsec) fed to the PSF for the last capture -- the residual left by adaptive optics, or the plain ground-based seeing figure. 0 means diffraction-limited.</summary>
        public double LastAtmosphericFwhmArcsec { get; private set; }

        /// <summary>The instrument's own diffraction-limited FWHM (arcsec) at the current filter's wavelength, computed from its real annular pupil -- the hard floor no observing condition can beat.</summary>
        public double LastDiffractionFwhmArcsec { get; private set; }

        /// <summary>Last capture as a grayscale float[] (row-major, y-down), for AstroImageStack. Null before the first capture. Fresh copy every call.</summary>
        public float[] GetLastCaptureGray()
        {
            if (lastCaptureSnapshot == null) return null;
            var gray = new float[lastCaptureSnapshot.Length];
            for (int i = 0; i < gray.Length; i++) gray[i] = lastCaptureSnapshot[i].r;
            return gray;
        }

        /// <summary>
        /// Last capture at full float precision, straight from the physics pipeline -- NOT
        /// CapturedPhoto/GetPixels(), which round-trips through an 8-bit RGB24 Texture2D and
        /// destroys nearly all of the real, physically-computed noise (shot/dark/read noise
        /// live at a small fraction of full well, far below 1/255). FITS export needs this
        /// full-precision source to actually be the 16-bit file it claims to be.
        /// </summary>
        public Color[] GetLastCaptureFullPrecision() => lastCaptureSnapshot != null ? (Color[])lastCaptureSnapshot.Clone() : null;

        /// <summary>Starts a timed exposure on targetBody. Nothing renders until the exposure completes.</summary>
        public void BeginExposure(CelestialBody targetBody)
        {
            if (!IsAvailable) return;
            pendingTarget = targetBody;
            isCapturing = true;
            captureElapsed = 0f;
            captureDuration = Mathf.Clamp(ExposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            HasCapturedPhoto = false;
        }

        /// <summary>Cancels any in-progress exposure and discards the finished photo. Releases the locked aim so the next shot re-centers.</summary>
        public void DiscardCapturedPhoto()
        {
            HasCapturedPhoto = false;
            isCapturing = false;
            hasLockedAim = false;
            // Doesn't stop the background Task itself (no cancellation token), but nulling the
            // reference makes PollProcessTask ignore its result once it does finish.
            processTask = null;
            isProcessing = false;
        }

        /// <summary>Marks the photo as consumed without releasing the locked aim. Used between stacking subs — the natural drift is what alignment is supposed to correct.</summary>
        public void ConsumeCapturedPhoto()
        {
            HasCapturedPhoto = false;
        }

        /// <summary>Cancels an in-progress timed exposure without producing a photo.</summary>
        public void CancelExposure()
        {
            isCapturing = false;
        }

        /// <summary>
        /// Advances an in-progress timed exposure by deltaTime real seconds. When the
        /// exposure's integration time elapses, renders the target and kicks the noise/effects
        /// pipeline off onto a background Task (see PollProcessTask); when already processing,
        /// polls that task for completion instead. No-op when neither capturing nor processing.
        /// </summary>
        public void TickCapture(float deltaTime)
        {
            if (isProcessing)
            {
                PollProcessTask();
                return;
            }

            if (!isCapturing) return;
            captureElapsed += deltaTime;
            if (captureElapsed < captureDuration) return;

            isCapturing = false;
            RenderExposure(pendingTarget);
        }

        /// <summary>Renders the target into readbackTexture (main thread), gathers every input the physics pipeline needs, then kicks that pipeline off onto a background Task.</summary>
        private void RenderExposure(CelestialBody targetBody)
        {
            if (targetBody == null) return;
            // Without autoguiding, the aim stays locked at the last UpdateAim position —
            // which is how the body drifts off-center if it moved since then.
            if (Autoguiding) UpdateAim(targetBody);
            RenderScene(targetBody);

            FrameComputeInputs inputs = GatherFrameInputs(targetBody);
            isProcessing = true;
            processTask = Task.Run(() => ComputeFramePixels(inputs));
        }

        /// <summary>Checks the background frame-processing Task; once complete, uploads the result to the output/captured textures and snapshots it for AstroImageStack -- the only parts that must happen on the main thread.</summary>
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

            pixelScratch = processTask.Result;
            processTask = null;
            isProcessing = false;

            // The snapshot is taken from the LINEAR pipeline output, before any display transfer
            // function -- it is what the FITS export and AstroImageStack consume, and stretching
            // it would corrupt every downstream measurement.
            lastCaptureSnapshot = (Color[])pixelScratch.Clone();

            HasCapturedPhoto = true;
            UploadDisplayTextures();
        }

        /// <summary>
        /// Rebuilds the on-screen/preview textures from the stored linear capture through the
        /// current display stretch. Separate from PollProcessTask so changing the stretch
        /// re-renders the existing frame instead of forcing a new exposure -- the same way a real
        /// viewer restretches what is already on screen.
        /// </summary>
        public void UploadDisplayTextures()
        {
            if (lastCaptureSnapshot == null || outputTexture == null) return;

            int n = lastCaptureSnapshot.Length;
            if (displayScratch == null || displayScratch.Length != n) displayScratch = new Color[n];
            for (int i = 0; i < n; i++)
            {
                float v = ApplyDisplayStretch(lastCaptureSnapshot[i].r);
                displayScratch[i] = new Color(v, v, v, 1f);
            }

            outputTexture.SetPixels(displayScratch);
            outputTexture.Apply();

            if (capturedTexture == null)
            {
                capturedTexture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGB24, false);
            }
            capturedTexture.SetPixels(displayScratch);
            capturedTexture.Apply();
        }

        /// <summary>
        /// Display transfer function -- see the DisplayStretch enum for what each mode is and
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

        /// <summary>Locks the camera aim on targetBody's current position. A capture always renders through the locked aim, so without autoguiding the body can drift off-center between shots.</summary>
        public void UpdateAim(CelestialBody targetBody)
        {
            if (targetBody == null || targetBody.scaledBody == null) return;
            Camera liveScaledSpace = FindCameraByName(ScaledSpaceCameraName);
            CelestialBody home = FlightGlobals.GetHomeBody();
            Vector3 camPos = liveScaledSpace != null
                ? liveScaledSpace.transform.position
                : (home != null && home.scaledBody != null ? home.scaledBody.transform.position : Vector3.zero);

            Vector3 toTarget = targetBody.scaledBody.transform.position - camPos;
            if (toTarget.sqrMagnitude < 1e-6f) return; // degenerate: observer coincides with the target's scaled position

            lockedCamPos = camPos;
            lockedLook = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            hasLockedAim = true;
        }

        /// <summary>
        /// Renders one frame through the locked aim into readbackTexture. Called once at
        /// exposure completion — no live preview. Works entirely in KSP's scaled-space frame
        /// using the game's own scaledBody transforms, so no coordinate conversion is needed.
        /// </summary>
        private void RenderScene(CelestialBody targetBody)
        {
            if (targetBody == null || !IsAvailable) return;
            if (targetBody.scaledBody == null) return; // no scaled stand-in -- nothing to frame
            if (!hasLockedAim) UpdateAim(targetBody); // first-ever shot on this target: always start centered

            CelestialBody home = FlightGlobals.GetHomeBody();
            Vector3 camPos = lockedCamPos;
            Quaternion look = lockedLook;

            // Deliberately NOT touching Sun.Instance's rotation the way Tarsier does —
            // mutating that global object bleeds a color shift into the live scene.
            // Sun parallax from KSC is ~0.05 deg (negligible). May need revisiting for
            // distant bodies like Jool, but only with a technique that can't affect the game view.

            // Large planet packs unload a body's scaled-space textures when no camera the GAME
            // knows about can see it -- and the telescope's cameras are clones it doesn't know
            // about. Photographing an unloaded body draws its mesh with no colour map: a black
            // disc with a lit rim. Force the target (and its moons, which share the field)
            // resident first. No-op without Kopernicus.
            KopernicusOnDemandIntegration.EnsureScaledSpaceTexturesLoaded(targetBody);

            // A RenderTexture's contents are volatile: Unity documents them as lost on graphics-
            // device events, fullscreen transitions among them -- which is what alt-tabbing is.
            // A texture whose backing surface was released reports IsCreated() false and must be
            // re-created before anything renders into it.
            if (renderTexture != null && !renderTexture.IsCreated()) renderTexture.Create();

            RenderTexture activeRT = RenderTexture.active;
            RenderTexture.active = renderTexture;

            float fov = Mathf.Clamp(FovDeg, MinFovDeg, MaxFovDeg);

            // The galaxy camera needs the same matrix resets as the scaled-space one below, for
            // the same reason: CopyFrom inherits the live camera's own projection, and setting
            // fieldOfView afterwards does not override an explicitly-set matrix. Without the
            // reset the star field and skybox are rendered at the GAME's wide field instead of
            // the telescope's, i.e. a hugely magnified patch of sky smeared behind the target.
            // KSP itself keeps the two cameras' fields in lockstep (ScaledCamera.SetFoV sets
            // fieldOfView on both), so treating them differently here was never right.
            // Force every body's scaled stand-in visible — KSP fades them by real-camera
            // distance, which has nothing to do with where our clone points.
            //
            // The home body is the exception, and it must be switched OFF rather than merely
            // left alone. The clone sits at the home body's own scaled position, i.e. INSIDE
            // its scaled stand-in, so a stand-in left enabled is rendered as a shell wrapped
            // around the camera: a large smooth coloured gradient across the frame, brightest
            // where the shell is lit, with a curved terminator running through it. Skipping it
            // only avoided switching it on; whether it was already on was left to KSP's own
            // distance fade, whose thresholds are set per body and differ between planet packs
            // -- so the same code could look clean on one install and produce a coloured wash on
            // another. Restored afterwards, so the live scene is unaffected.
            ScaledSpaceFader homeFader = null;
            bool homeFaderWasEnabled = false;
            foreach (ScaledSpaceFader fader in scaledSpaceFaders)
            {
                if (fader == null || fader.r == null) continue;
                if (home != null && fader.celestialBody == home)
                {
                    homeFader = fader;
                    homeFaderWasEnabled = fader.r.enabled;
                    fader.r.enabled = false;
                    continue;
                }
                fader.r.enabled = true;
            }

            // Rendered TWICE, and only the second pass is read back.
            //
            // The first capture after the window regains focus was reliably wrong -- a black disc
            // with a lit rim where the planet should be, i.e. its geometry drawn without its
            // surface texture -- while every subsequent capture from the same setup was correct.
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
            for (int pass = 0; pass < 2; pass++)
            {
                AimCamera(galaxyCam, GalaxyCameraName, camPos, look, fov);
                galaxyCam.ResetWorldToCameraMatrix();
                galaxyCam.ResetProjectionMatrix();
                galaxyCam.Render();

                // The matrix resets are critical: KSP's ScaledSpace camera carries a custom
                // view/projection matrix that CopyFrom inherits and silently overrides our
                // transform. Resetting them makes the clone's own transform authoritative.
                AimCamera(scaledSpaceCam, ScaledSpaceCameraName, camPos, look, fov);
                scaledSpaceCam.ResetWorldToCameraMatrix();
                scaledSpaceCam.ResetProjectionMatrix();
                scaledSpaceCam.clearFlags = CameraClearFlags.Depth;
                scaledSpaceCam.farClipPlane = 3e15f;
                scaledSpaceCam.Render();
            }

            readbackTexture.ReadPixels(new Rect(0, 0, TextureWidth, TextureHeight), 0, 0);
            readbackTexture.Apply();
            RenderTexture.active = activeRT;

            // Hand the home body's stand-in back exactly as it was found -- the live scene draws
            // through it and must not be left switched off by a capture.
            if (homeFader != null && homeFader.r != null) homeFader.r.enabled = homeFaderWasEnabled;
        }

        /// <summary>Copies the live camera settings onto the clone, then sets position/rotation/FOV.</summary>
        private void AimCamera(Camera clone, string liveCameraName, Vector3 pos, Quaternion rot, float fovDeg)
        {
            ResetCameraFromLive(clone, liveCameraName);
            clone.transform.position = pos;
            clone.transform.rotation = rot;
            clone.fieldOfView = fovDeg;
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
            if (builtOnce) Dispose(); // binning or active telescope changed since the last build -- tear down and rebuild at the new resolution
            builtOnce = true;
            builtBinningFactor = BinningFactor;
            builtSpec = Spec;

            try
            {
                Camera liveGalaxy = FindCameraByName(GalaxyCameraName);
                Camera liveScaledSpace = FindCameraByName(ScaledSpaceCameraName);
                if (liveGalaxy == null || liveScaledSpace == null)
                {
                    Debug.LogWarning("[ExoInstruments] Could not find KSP's galaxy/scaled-space cameras -- solar-system camera disabled.");
                    available = false;
                    return;
                }

                root = new GameObject("ExoInstrumentsSolarSystemCamera");

                // 24-bit depth + explicit sRGB, explicit .Create() — mirrors Tarsier's setup.
                renderTexture = new RenderTexture(TextureWidth, TextureHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "ExoInstrumentsSolarSystemCameraRT"
                };
                // Create() reports whether the graphics device actually granted the surface. At
                // the largest instrument's native resolution this is a 4096x4128 ARGB32 target
                // with a 24-bit depth buffer -- roughly 340 MB of VRAM for this one texture, on
                // top of whatever the game already holds. A refusal here is silent otherwise: the
                // camera still "renders", and the readback returns whatever was in memory.
                if (!renderTexture.Create())
                {
                    Debug.LogError($"[ExoInstruments] The graphics device refused a {TextureWidth}x{TextureHeight} "
                                 + $"({(double)TextureWidth * TextureHeight / 1e6:F1} Mpx) render target. Use a higher binning factor.");
                    renderTextureRefused = true;
                }
                else renderTextureRefused = false;

                var galaxyObj = new GameObject("ExoInstrumentsGalaxyCamClone");
                galaxyObj.transform.parent = root.transform; // explicit zero below — parent alone doesn't reset world position
                galaxyObj.transform.localPosition = Vector3.zero;
                galaxyObj.transform.localRotation = Quaternion.identity;
                galaxyCam = galaxyObj.AddComponent<Camera>();
                galaxyCam.CopyFrom(liveGalaxy);
                galaxyCam.targetTexture = renderTexture;
                galaxyCam.depth = 17; // same relative depth Tarsier uses for its galaxy-cam clone
                galaxyCam.enabled = false;

                var scaledSpaceObj = new GameObject("ExoInstrumentsScaledSpaceCamClone");
                scaledSpaceObj.transform.parent = root.transform;
                scaledSpaceObj.transform.localPosition = Vector3.zero;
                scaledSpaceObj.transform.localRotation = Quaternion.identity;
                scaledSpaceCam = scaledSpaceObj.AddComponent<Camera>();
                scaledSpaceCam.CopyFrom(liveScaledSpace);
                scaledSpaceCam.targetTexture = renderTexture;
                scaledSpaceCam.depth = 18; // one above galaxyCam, same relative ordering Tarsier uses
                scaledSpaceCam.enabled = false;

                readbackTexture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGB24, false);
                outputTexture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGB24, false);
                pixelScratch = new Color[TextureWidth * TextureHeight];

                skyboxRenderers = (UnityEngine.Object.FindObjectsOfType(typeof(Renderer)) as Renderer[])
                    ?.Where(r => r.name == "XP" || r.name == "XN" || r.name == "YP" || r.name == "YN" || r.name == "ZP" || r.name == "ZN")
                    .ToArray() ?? new Renderer[0];
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

        /// <summary>Plain-data snapshot of everything ComputeFramePixels needs -- gathered on the main thread (it touches CelestialBody/Unity APIs), then handed to a background Task that touches none of that, mirroring the StartImagingRefresh/PollImagingRenderTask pattern used elsewhere in this mod.</summary>
        private struct FrameComputeInputs
        {
            public Color[] Src;
            public int TargetSeed;
            public double Ut;
            public float ExposureSeconds;
            public float IsoGain;
            public CameraFilter Filter;
            public float Extinction;
            public double ScintSigma;
            public double TwilightRamp;
            public double MoonSkyExcess;
            public float CloudCoverage;
            public double TotalElectrons;
            // PSF ingredients rather than a finished kernel. Building the kernel is pure C# that
            // touches nothing Unity-owned, so it belongs on the background side of this boundary
            // -- doing it here would stall the main thread at the moment the player presses
            // Capture, which is exactly when a stall is most visible.
            public double PlateScaleArcsec;
            /// <summary>Plain ground-based seeing (arcsec), already resolved from the target's airmass on the main thread. Ignored when the instrument has adaptive optics.</summary>
            public double SeeingFwhmArcsec;
            public double DefocusDiscRadiusPx;
            public int DriftPx;
        }

        /// <summary>
        /// Gathers every CelestialBody/Unity-API-touching input ComputeFramePixels needs, on
        /// the main thread. Real photon-flux calibration: the imaged body's actual apparent
        /// magnitude (real albedo/radius/Sun-distance/observer-distance/phase-angle, Lambertian
        /// phase law -- see PhotonFluxModel) converted into real electrons collected through
        /// the RC20's real aperture/obstruction/QE/filter-bandwidth/exposure/extinction.
        /// </summary>
        private FrameComputeInputs GatherFrameInputs(CelestialBody targetBody)
        {
            Color[] src = readbackTexture.GetPixels();
            EnsureDefectMap();

            float exposureSeconds = Mathf.Clamp(ExposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            float isoGain = Mathf.Clamp(Gain, MinGain, MaxGain);

            double ut = Planetarium.GetUniversalTime();

            TryComputeAltitudeDeg(targetBody, out double targetAltDeg);
            double airmass = targetAltDeg > 0.0 ? ImagingObservingConditions.AirmassAt(targetAltDeg) : double.PositiveInfinity;
            float extinction = (float)AtmosphericImagingNoise.ExtinctionTransmission(airmass);

            double angularDiameterRad = ComputeAngularDiameterRad(targetBody);
            double scintSigma = AtmosphericImagingNoise.ScintillationExcessSigma(
                Spec.ApertureMeters, Spec.SiteAltitudeMeters, airmass, exposureSeconds, angularDiameterRad);

            bool haveSunAlt = TryComputeAltitudeDeg(Planetarium.fetch != null ? Planetarium.fetch.Sun : null, out double sunAltDeg);
            double twilightRamp = haveSunAlt
                ? Clamp01((sunAltDeg - AstronomicalTwilightSunAltitudeDeg) / (ImagingObservingConditions.TwilightSunAltitudeDeg - AstronomicalTwilightSunAltitudeDeg))
                : 0.0;
            double moonSkyExcess = ComputeMoonSkyExcess(targetBody);
            float coverage = ComputeCloudCoverage();

            double totalElectrons = ComputeCollectedElectrons(targetBody, extinction, exposureSeconds);

            // Cloud cover degrades the delivered image quality on top of the clear-sky term;
            // folded into the atmospheric FWHM (an angular quantity) rather than added as a
            // separate pixel count, so it scales correctly with plate scale and binning. Only
            // the plain ground-based term is resolved here, because only it needs the target's
            // altitude; the adaptive-optics solve is pure arithmetic and happens off-thread.
            double seeingFwhmArcsec = ComputeGroundSeeingFwhmArcsec(targetBody)
                                    + coverage * CloudBlurPxMax * PlateScaleArcsecPerPixel;
            double defocusDiscRadiusPx = Autofocus ? 0.0 : Mathf.Abs(FocusOffset) * MaxDefocusBlurPx;
            int driftPx = Autoguiding ? 0 : ComputeDriftPixels(exposureSeconds, targetBody);

            return new FrameComputeInputs
            {
                Src = src,
                TargetSeed = targetBody.flightGlobalsIndex,
                Ut = ut,
                ExposureSeconds = exposureSeconds,
                IsoGain = isoGain,
                Filter = Filter,
                Extinction = extinction,
                ScintSigma = scintSigma,
                TwilightRamp = twilightRamp,
                MoonSkyExcess = moonSkyExcess,
                CloudCoverage = coverage,
                TotalElectrons = totalElectrons,
                PlateScaleArcsec = PlateScaleArcsecPerPixel,
                SeeingFwhmArcsec = seeingFwhmArcsec,
                DefocusDiscRadiusPx = defocusDiscRadiusPx,
                DriftPx = driftPx,
            };
        }

        /// <summary>
        /// Builds (or reuses) the instrument's PSF for this frame. Runs on the background pipeline
        /// thread -- none of it touches Unity or KSP state.
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
                // computed separately from the pupil -- solve for the residual that reproduces it.
                atmosphericFwhm = OpticalPsf.AtmosphericFwhmForDelivered(
                    Spec.AdaptiveOpticsFwhmArcsec, inputs.PlateScaleArcsec,
                    Spec.ApertureMeters, Spec.SecondaryObstructionFraction, wavelength);
            }
            else
            {
                atmosphericFwhm = inputs.SeeingFwhmArcsec;
            }

            bool reusable = psfCacheSpec == Spec
                         && psfCacheFilter == inputs.Filter
                         && psfCachePlateScale == inputs.PlateScaleArcsec
                         && psfCacheAtmosphericFwhm == atmosphericFwhm
                         && psfCacheDefocusRadius == inputs.DefocusDiscRadiusPx;

            if (!reusable)
            {
                psfCacheCore = OpticalPsf.BuildKernel(
                    inputs.PlateScaleArcsec, Spec.ApertureMeters, Spec.SecondaryObstructionFraction,
                    wavelength, atmosphericFwhm, inputs.DefocusDiscRadiusPx, out psfCacheCoreRadius);

                // A real adaptive-optics PSF is two-component: a corrected core carrying the
                // system's Strehl ratio, plus the wide halo of everything it failed to correct.
                psfCacheHalo = null;
                psfCacheHaloRadius = 0;
                psfCacheCoreWeight = 1f;
                if (hasAo && Spec.AdaptiveOpticsStrehlRatio > 0.0 && Spec.AdaptiveOpticsHaloSeeingFwhmArcsec > 0.0)
                {
                    psfCacheCoreWeight = Mathf.Clamp01((float)Spec.AdaptiveOpticsStrehlRatio);
                    psfCacheHalo = OpticalPsf.BuildSeeingHaloKernel(
                        inputs.PlateScaleArcsec, Spec.AdaptiveOpticsHaloSeeingFwhmArcsec,
                        wavelength, MaxHaloKernelRadiusPx, out psfCacheHaloRadius);
                }

                psfCacheSpec = Spec;
                psfCacheFilter = inputs.Filter;
                psfCachePlateScale = inputs.PlateScaleArcsec;
                psfCacheAtmosphericFwhm = atmosphericFwhm;
                psfCacheDefocusRadius = inputs.DefocusDiscRadiusPx;

                psfCacheDiffractionFwhm = OpticalPsf.AiryFwhmArcsec(
                    Spec.ApertureMeters, Spec.SecondaryObstructionFraction, wavelength);
            }

            core = psfCacheCore;
            coreRadius = psfCacheCoreRadius;
            coreWeight = psfCacheCoreWeight;
            halo = psfCacheHalo;
            haloRadius = psfCacheHaloRadius;

            // Diagnostics, read on the main thread after the task completes.
            LastAppliedBlurRadiusPx = psfCacheCoreRadius;
            LastAtmosphericFwhmArcsec = atmosphericFwhm;
            LastDiffractionFwhmArcsec = psfCacheDiffractionFwhm;
        }

        /// <summary>
        /// Converts the raw rendered frame into a monochrome CCD-frame look through the
        /// physics pipeline: filter throughput, atmospheric extinction, EVE cloud cover,
        /// sky glow (twilight + moon scattering + airglow + zodiacal), scintillation,
        /// shot/dark/read noise, cosmic ray hits, full-well blooming,
        /// charge-transfer smear, hot/dead pixels, optional drift trail, and combined
        /// defocus/seeing/astigmatism blur. Pure C#/array math only -- no CelestialBody or
        /// UnityEngine.Object API touches -- so this runs on a background Task; only the
        /// FrameComputeInputs gather step and the final texture upload need the main thread.
        /// </summary>
        private Color[] ComputeFramePixels(FrameComputeInputs inputs)
        {
            Color[] src = inputs.Src;
            float filterThroughput = FilterThroughput(inputs.Filter);

            int n = TextureWidth * TextureHeight;
            if (rawScratch == null || rawScratch.Length != n) rawScratch = new float[n];

            // Reused, not freshly allocated per capture. A Color is 16 bytes, so at the largest
            // instrument's native resolution (4096x4128 = 16.9 Mpx) this single array is 270 MB;
            // allocating it again every exposure churned that much through the large-object heap
            // per shot, on top of the several other frame-sized buffers this pipeline already
            // holds. The result is handed straight to PollProcessTask on the main thread and
            // copied out there, so one buffer is enough.
            if (frameScratch == null || frameScratch.Length != n) frameScratch = new Color[n];
            Color[] pixels = frameScratch;

            float skyBackground = (float)((TwilightSkyBackgroundRatePerSecond * inputs.TwilightRamp
                                          + MoonGlowRatePerSecond * inputs.MoonSkyExcess
                                          + AirglowBaselinePerSecond
                                          + ZodiacalBaselineRatePerSecond) * inputs.ExposureSeconds * filterThroughput);

            // Deliberately the NATIVE (unbinned) Spec.FullWellElectrons here, paired with the
            // native per-physical-pixel DarkCurrentElectronsPerSecond -- both real electron
            // quantities scale by BinningFactor^2 together in a real binned pixel, so the
            // resulting pedestal/sigma FRACTION (what DarkCurrent actually returns) comes out
            // identical either way; using the raw per-pixel numbers is just simpler than
            // multiplying both sides by the same factor for no change in the answer.
            AtmosphericImagingNoise.DarkCurrent(inputs.ExposureSeconds, Spec.FullWellElectrons, Spec.DarkCurrentElectronsPerSecond, out double darkPedestalD, out double darkSigmaD);
            float darkPedestal = (float)darkPedestalD;
            float darkSigma = (float)darkSigmaD;

            // New RNG seed every exposure — read noise differs shot to shot, unlike the fixed defect map.
            System.Random rng = new System.Random(unchecked(inputs.TargetSeed * 9973 + (int)(inputs.Ut * 997.0) + 17));
            float scintJitter = ScintillationMultiplier(rng, inputs.ScintSigma);
            lastScintillationFactor = scintJitter;
            lastScintillationSigma = (float)inputs.ScintSigma;

            float cloudTransmission = 1f - inputs.CloudCoverage * (float)CloudMaxAttenuation;
            float haze = inputs.CloudCoverage * (float)CloudHazeRatePerSecond * inputs.ExposureSeconds * filterThroughput;

            // Unity's own rendered pixel values (src[]) keep supplying the real spatial shading
            // (terminator, limb, craters from the game's own 3D lighting) -- only the ABSOLUTE
            // scale of that shading is recalibrated to match the physically-derived total
            // electron count (inputs.TotalElectrons), so noise/saturation/SNR are all anchored
            // to real physics rather than an invented flat exposure multiplier.
            //
            // Calibrating against THIS filter's own rendered sum (e.g. sum of src[].r for the
            // Red filter) would force every filter's stack to the same total electron budget --
            // TotalElectrons is the same physical value for R/G/B (one body-wide albedo, split
            // into equal thirds, see ComputeCollectedElectrons), so that erases the body's real
            // per-channel color balance (a green-dominant body like Jool would have its R and B
            // channels artificially boosted to match G's total, then LRGB-composited into
            // whatever arbitrary hue survives the per-pixel contrast differences -- not Jool's
            // actual color). Calibrating every filter against the SAME reference -- the frame's
            // luminance-weighted sum, matching FilterSignal's own Luminance formula -- instead
            // scales each channel by its real relative share of that luminance, so R:G:B keeps
            // the body's true color ratio through calibration and into the later luminance-
            // transfer step in AstroImageStack.ComposeLRGB (which already assumes R/G/B carry
            // real relative color, not independently-normalized ones).
            double totalRenderedLuminance = 0.0;
            for (int i = 0; i < n; i++) totalRenderedLuminance += FilterSignal(src[i], CameraFilter.Luminance);

            float calibratedSignalPerUnit = totalRenderedLuminance > 1e-6
                ? (float)((inputs.TotalElectrons / FullWellElectrons) / totalRenderedLuminance)
                : 0f;

            for (int i = 0; i < n; i++)
            {
                float signal = FilterSignal(src[i], inputs.Filter);
                float photon = signal * calibratedSignalPerUnit * scintJitter * cloudTransmission;
                float totalPhoton = photon + haze + skyBackground;

                float shotSigma = (float)AtmosphericImagingNoise.ShotNoiseSigma(totalPhoton, FullWellElectrons);
                float combinedPreGainSigma = Mathf.Sqrt(shotSigma * shotSigma + darkSigma * darkSigma);

                float preGainValue = totalPhoton + darkPedestal + NextGaussian(rng, combinedPreGainSigma);
                float postGain = preGainValue * inputs.IsoGain + NextGaussian(rng, ReadNoiseSigmaValue);

                // Left unclamped here — blooming/CTI below need to see genuine
                // above-full-well values before the sensor's own clipping applies.
                rawScratch[i] = postGain;
            }

            ApplyCosmicRays(rawScratch, inputs.ExposureSeconds, rng);
            ApplyBlooming(rawScratch);
            ApplyChargeTransferSmear(rawScratch);

            // Saturation census BEFORE the clamp below throws the over-full-well information away
            // -- once clipped, a blown pixel is indistinguishable from a legitimately bright one.
            int saturated = 0;
            for (int i = 0; i < n; i++) if (rawScratch[i] >= 1f) saturated++;
            lastSaturatedFraction = n > 0 ? (float)saturated / n : 0f;

            for (int i = 0; i < n; i++)
            {
                float value = Mathf.Clamp01(rawScratch[i]);
                pixels[i] = new Color(value, value, value, 1f);
            }

            // Diurnal drift: 360 deg per Kerbin rotation, converted to pixels by the FOV.
            if (inputs.DriftPx >= 1) ApplyHorizontalMotionBlur(pixels, inputs.DriftPx);

            // The instrument's real PSF -- diffraction off its own annular pupil, convolved with
            // the Kolmogorov atmosphere and any defocus (see OpticalPsf). One convolution, so
            // nothing is blurred twice.
            EnsurePsfKernels(inputs, out float[] psfCore, out int psfRadius,
                             out float psfCoreWeight, out float[] psfHalo, out int psfHaloRadius);
            ApplyPsf(pixels, psfCore, psfRadius, psfCoreWeight, psfHalo, psfHaloRadius);

            // Field-dependent astigmatism, applied after the PSF so it reads as a distinct
            // off-axis smear rather than blending into the on-axis profile.
            ApplyAstigmatismBlur(pixels);

            // Defect overlay last: hot/dead pixels are a detector read-out artifact, not an
            // optical one, so they shouldn't be softened by the seeing/defocus/astigmatism blur the
            // way real scene light is. This is genuinely raw, uncorrected sensor output --
            // the same raw frame is what AstroImageStack.AddSub receives and cosmetically
            // corrects using this same known, fixed defect map before it ever gets aligned
            // and stacked (see AstroImageStack.CosmeticCorrect), the same order real
            // calibration pipelines (PixInsight, IRAF/ccdproc, ESO Reflex) use: raw frame ->
            // bad-pixel-map correction -> registration -> stacking.
            foreach (int idx in hotPixelIndices)
            {
                float v = Mathf.Clamp01(0.9f + NextGaussian(rng, 0.05f));
                pixels[idx] = new Color(v, v, v, 1f);
            }
            foreach (int idx in deadPixelIndices)
            {
                pixels[idx] = new Color(0f, 0f, 0f, 1f);
            }

            return pixels;
        }

        /// <summary>
        /// Per-exposure scintillation: the fractional intensity fluctuation the atmosphere
        /// imposes on the target's light, drawn once per frame.
        ///
        /// Drawn from a LOG-NORMAL distribution, not an additive Gaussian. Scintillation is a
        /// multiplicative modulation of an intensity, and an intensity cannot be negative --
        /// atmospheric turbulence redistributes starlight, it does not remove more than all of
        /// it. Real scintillation is in fact measured to be approximately log-normal (Dravins,
        /// Lindegren, Mezey &amp; Young 1997, the same series this pipeline's Young formula and
        /// extended-source suppression already come from), so this is the physically correct
        /// distribution rather than a defensive clamp bolted onto the wrong one.
        ///
        /// The previous form, 1 + N(0, sigma), was unbounded below. Because this factor scales
        /// the TARGET's signal but not the sky background added after it, a single unlucky draw
        /// at large sigma did not merely dim the frame -- it INVERTED it: the target went
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
        private static bool TryComputeAltitudeDeg(CelestialBody body, out double altDeg)
        {
            altDeg = 0.0;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || body == null) return false;

            Vector3d obsPos = ObservatorySite.WorldPosition(home);
            Vector3d up = (obsPos - home.position).normalized;
            Vector3d toBody = (body.position - obsPos).normalized;
            altDeg = 90.0 - Vector3d.Angle(up, toBody);
            return true;
        }

        /// <summary>
        /// Blur from looking through Kerbin's own atmosphere. For a plain (non-AO) instrument
        /// this grows with airmass, sharply worse near the horizon, the same way real seeing
        /// does. An instrument with real adaptive optics (VisualTelescopeSpec.AdaptiveOpticsFwhmArcsec,
        /// e.g. SPHERE/ZIMPOL) instead returns its own real, roughly airmass-independent
        /// corrected FWHM converted to pixels at the CURRENT plate scale -- real AO actively
        /// cancels atmospheric distortion in front of the wavefront sensor, rather than just
        /// blurring the image, so unlike the plain model it isn't a fixed pixel count: it scales
        /// correctly with zoom/binning because it's derived from a real arcsec figure, not a
        /// pixel one.
        /// </summary>
        private double ComputeGroundSeeingFwhmArcsec(CelestialBody targetBody)
        {
            // An instrument with adaptive optics doesn't use this path at all -- its atmospheric
            // term is the residual left after correction, solved for in EnsurePsfKernels.
            if (Spec.AdaptiveOpticsFwhmArcsec > 0.0) return 0.0;

            if (!TryComputeAltitudeDeg(targetBody, out double altDeg)) return 0.0;

            // The plain ground-based model stays calibrated exactly as before -- its airmass
            // response is unchanged -- but is expressed as the ANGLE it always physically was.
            // Seeing is a property of the atmosphere, not of the sensor, so quoting it in pixels
            // made it wrongly depend on the plate scale and binning; converting once here at the
            // current plate scale preserves the existing behaviour while letting OpticalPsf work
            // in the units the Kolmogorov model actually needs.
            float blurPx;
            if (altDeg <= 0.0) blurPx = MaxSeeingBlurPx; // shouldn't be capturable this low, but cap defensively
            else
            {
                double airmass = ImagingObservingConditions.AirmassAt(altDeg);
                if (double.IsInfinity(airmass) || double.IsNaN(airmass)) blurPx = MaxSeeingBlurPx;
                else blurPx = Mathf.Min(MaxSeeingBlurPx, Mathf.Max(0f, (float)airmass - 1f) * SeeingBlurPxPerAirmass);
            }
            return blurPx * PlateScaleArcsecPerPixel;
        }

        /// <summary>Real central wavelength (metres) of the filter currently in the wheel -- the lambda in lambda/D. Falls back to Luminance for a position this instrument doesn't physically carry.</summary>
        private static double FilterCentralWavelengthMeters(CameraFilter filter)
        {
            double nm;
            switch (filter)
            {
                case CameraFilter.Red:    nm = Spec.RedCentralWavelengthNm; break;
                case CameraFilter.Green:  nm = Spec.GreenCentralWavelengthNm; break;
                case CameraFilter.Blue:   nm = Spec.BlueCentralWavelengthNm; break;
                case CameraFilter.HAlpha: nm = Spec.HAlphaCentralWavelengthNm; break;
                default:                  nm = Spec.LuminanceCentralWavelengthNm; break;
            }
            if (nm <= 0.0) nm = Spec.LuminanceCentralWavelengthNm;
            return nm * 1e-9;
        }

        /// <summary>
        /// Convolves the frame with the instrument's PSF. The pipeline is monochrome by this
        /// point (every pixel carries one value in all three channels), so this works on a
        /// single plane rather than three -- a third of the transform work for an identical result.
        /// </summary>
        private void ApplyPsf(Color[] pixels, float[] kernel, int radius,
                              float coreWeight, float[] haloKernel, int haloRadius)
        {
            if (kernel == null || radius < 1) return;

            int n = pixels.Length;
            if (psfPlaneScratch == null || psfPlaneScratch.Length != n) psfPlaneScratch = new float[n];
            for (int i = 0; i < n; i++) psfPlaneScratch[i] = pixels[i].r;

            bool hasHalo = haloKernel != null && haloRadius >= 1 && coreWeight < 0.999f;

            // Convolution is linear, so a PSF that is the sum of two components can be applied as
            // the weighted sum of two convolutions -- exactly equivalent to convolving once with
            // the combined kernel, but it lets each component be sized to its own scale instead
            // of forcing the compact core to carry the halo's enormous support.
            float[] haloPlane = null;
            if (hasHalo)
            {
                if (psfHaloScratch == null || psfHaloScratch.Length != n) psfHaloScratch = new float[n];
                Array.Copy(psfPlaneScratch, psfHaloScratch, n);
                haloPlane = psfHaloScratch;
                FourierConvolution.Convolve(haloPlane, TextureWidth, TextureHeight, haloKernel, haloRadius);
            }

            FourierConvolution.Convolve(psfPlaneScratch, TextureWidth, TextureHeight, kernel, radius);

            for (int i = 0; i < n; i++)
            {
                float v = hasHalo
                    ? coreWeight * psfPlaneScratch[i] + (1f - coreWeight) * haloPlane[i]
                    : psfPlaneScratch[i];
                v = Mathf.Clamp01(v);
                pixels[i] = new Color(v, v, v, 1f);
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
        private static double ComputeMoonSkyExcess(CelestialBody targetBody)
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (home == null || sun == null || home.orbitingBodies == null) return 0.0;

            Vector3d obsPos = ObservatorySite.WorldPosition(home);
            Vector3d toTarget = targetBody != null ? (targetBody.position - obsPos) : Vector3d.zero;
            bool haveTarget = toTarget.sqrMagnitude > 1e-6;
            if (haveTarget) toTarget = toTarget.normalized;

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
        /// converted via the real RC20 aperture/obstruction/QE/filter-bandwidth/extinction
        /// chain (PhotonFluxModel.CollectedElectrons). Zero if any required geometry is missing.
        /// </summary>
        private double ComputeCollectedElectrons(CelestialBody targetBody, float extinctionTransmission, float exposureSeconds)
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (home == null || sun == null || targetBody == null) return 0.0;

            Vector3d obsPos = ObservatorySite.WorldPosition(home);
            double distanceToObserverMeters = (targetBody.position - obsPos).magnitude;
            double distanceToSunMeters = (targetBody.position - sun.position).magnitude;
            if (distanceToObserverMeters < 1.0 || distanceToSunMeters < 1.0) return 0.0;

            Vector3d toSunFromBody = (sun.position - targetBody.position).normalized;
            Vector3d toObserverFromBody = (obsPos - targetBody.position).normalized;
            double phaseAngleRad = Vector3d.Angle(toSunFromBody, toObserverFromBody) * Math.PI / 180.0;

            double magnitude = PhotonFluxModel.ApparentMagnitude(
                targetBody.albedo, targetBody.Radius, distanceToSunMeters, distanceToObserverMeters, phaseAngleRad);

            double bandwidthAngstrom = FilterBandwidthAngstrom(Filter);
            double apertureAreaCm2 = RealApertureAreaCm2();
            double combinedTransmission = extinctionTransmission * NdFilterTransmission(NdFilter);

            return PhotonFluxModel.CollectedElectrons(
                magnitude, bandwidthAngstrom, apertureAreaCm2, Spec.QuantumEfficiency, exposureSeconds, combinedTransmission);
        }

        /// <summary>
        /// Angular diameter (radians) of targetBody as seen from KSC right now -- feeds
        /// AtmosphericImagingNoise.ScintillationExcessSigma's extended-source suppression
        /// (a resolved planetary disk, unlike a star, isn't a point source). Small-angle
        /// approximation (2*radius/distance), which is fine at solar-system distances.
        /// </summary>
        /// <summary>targetBody's apparent diameter in arcsec as seen from KSC right now -- paired with PlateScaleArcsecPerPixel this is what decides how many pixels across the disk actually lands on, i.e. whether any surface detail is resolvable in principle.</summary>
        public static double AngularDiameterArcsec(CelestialBody targetBody)
            => ComputeAngularDiameterRad(targetBody) * (180.0 / Math.PI) * 3600.0;

        private static double ComputeAngularDiameterRad(CelestialBody targetBody)
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || targetBody == null) return 0.0;

            Vector3d obsPos = ObservatorySite.WorldPosition(home);
            double distanceMeters = (targetBody.position - obsPos).magnitude;
            if (distanceMeters < 1.0) return 0.0;

            return 2.0 * targetBody.Radius / distanceMeters;
        }

        /// <summary>Real effective collecting area (cm^2): full aperture minus the real secondary-mirror obstruction.</summary>
        private static double RealApertureAreaCm2() => EffectiveApertureAreaM2(Spec) * 1.0e4; // m^2 -> cm^2

        /// <summary>Real cosmic-ray hit rate: sea-level flux (~1/cm^2/min) over the sensor's real, native (binning-independent) physical silicon area.</summary>
        private static float ComputeCosmicRayHitsPerSecond()
        {
            const double sealevelFluxPerCm2PerMinute = 1.0;
            double sideXCm = NativeTextureWidth * NativePixelSizeMeters * 100.0;
            double sideYCm = NativeTextureHeight * NativePixelSizeMeters * 100.0;
            double areaCm2 = sideXCm * sideYCm;
            return (float)(sealevelFluxPerCm2PerMinute * areaCm2 / 60.0);
        }

        /// <summary>Real filter bandwidth in Angstrom for the active telescope's own real filter set (VisualTelescopeSpec) -- each filter's real bandwidth, not a fraction of Luminance, since a research instrument's R/G/B are each their own named filter with their own published FWHM (unlike an amateur LRGB wheel, where an even split is the real design -- see VisualTelescopeCatalog.Rc20's own comment).</summary>
        private static double FilterBandwidthAngstrom(CameraFilter filter)
        {
            switch (filter)
            {
                case CameraFilter.Red:    return Spec.RedBandwidthAngstrom;
                case CameraFilter.Green:  return Spec.GreenBandwidthAngstrom;
                case CameraFilter.Blue:   return Spec.BlueBandwidthAngstrom;
                case CameraFilter.HAlpha: return Spec.HAlphaBandwidthAngstrom;
                default:                  return LuminanceBandwidthAngstrom;
            }
        }

        /// <summary>
        /// Full-well overflow: any pixel above FullWellValue spills the excess into its
        /// vertical neighbors (the CCD column/shift-register direction), which can themselves
        /// overflow in turn -- producing the familiar bloom trail through a saturated star or
        /// planet limb instead of a hard-clipped blob. Operates in place, pre-clamp.
        /// </summary>
        private void ApplyBlooming(float[] raw)
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
                        float overflow = raw[i] - FullWellValue;
                        if (overflow <= 0f) continue;
                        anyOverflow = true;
                        raw[i] = FullWellValue;
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
        /// state and releasing a fraction of what's trapped back into the next pixel down --
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
        /// Cosmic ray hits: a flat Poisson process over the exposure deposits short, randomly
        /// angled bright tracks (Pyxel's CosmiX/TARS approach, minus the angle model -- see
        /// CosmicRayHitsPerSecond), distinct from the fixed hot-pixel map since a real muon/proton
        /// strike lands anywhere, at a random angle, on every exposure independently.
        /// </summary>
        private void ApplyCosmicRays(float[] raw, float exposureSeconds, System.Random rng)
        {
            int w = TextureWidth, h = TextureHeight;
            double expectedHits = CosmicRayHitsPerSecond * exposureSeconds;
            int hits = SamplePoisson(rng, expectedHits);

            for (int n = 0; n < hits; n++)
            {
                int x0 = rng.Next(w);
                int y0 = rng.Next(h);
                double angle = rng.NextDouble() * 2.0 * Math.PI; // isotropic incidence in the sensor plane
                int length = CosmicRayMinTrackPx + rng.Next(CosmicRayMaxTrackPx - CosmicRayMinTrackPx);
                double dx = Math.Cos(angle), dy = Math.Sin(angle);

                for (int s = 0; s < length; s++)
                {
                    int x = x0 + (int)Math.Round(dx * s);
                    int y = y0 + (int)Math.Round(dy * s);
                    if (x < 0 || x >= w || y < 0 || y >= h) break;
                    int i = y * w + x;
                    if (raw[i] < CosmicRayDepositValue) raw[i] = CosmicRayDepositValue;
                }
            }
        }

        /// <summary>Knuth's algorithm: exact Poisson sample, fine for the small lambda cosmic rays use.</summary>
        private static int SamplePoisson(System.Random rng, double lambda)
        {
            if (lambda <= 0.0) return 0;
            double l = Math.Exp(-lambda);
            int k = 0;
            double p = 1.0;
            do
            {
                k++;
                p *= rng.NextDouble();
            } while (p > l);
            return k - 1;
        }

        /// <summary>
        /// Third-order astigmatism: transverse blur scaling with the square of the normalized
        /// field radius, smeared radially outward from frame center -- a simplified stand-in
        /// for the radially-elongated star image real astigmatism produces at one of its two
        /// focus positions in an off-axis RC/Ritchey-Chretien field. Zero at the target itself
        /// (centered by definition), worst for background stars near the corners.
        /// </summary>
        private void ApplyAstigmatismBlur(Color[] buffer)
        {
            int w = TextureWidth, h = TextureHeight;
            int n = w * h;
            if (astigmatismScratch == null || astigmatismScratch.Length != n) astigmatismScratch = new float[n];
            for (int i = 0; i < n; i++) astigmatismScratch[i] = buffer[i].r;

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
                    float v = sum / (steps + 1);
                    buffer[y * w + x] = new Color(v, v, v, 1f);
                }
            }
        }

        /// <summary>Cloud coverage over KSC from EVE, or 0 if EVE isn't installed or has no cloud layer for the home body.</summary>
        internal static float ComputeCloudCoverage()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return 0f;

            Vector3d obsPos = ObservatorySite.WorldPosition(home);
            Vector3d worldUp = (obsPos - home.position).normalized;
            Vector3 bodyFixedUp = home.bodyTransform != null
                ? home.bodyTransform.InverseTransformDirection((Vector3)worldUp)
                : (Vector3)worldUp;

            return EveCloudIntegration.SampleCoverage(home.bodyName, bodyFixedUp);
        }

        /// <summary>
        /// The sensor's known, fixed bad-pixel map (hot + dead indices combined) -- the same
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

        /// <summary>Builds the hot/dead pixel index lists once from a constant seed (same defects every session).</summary>
        private void EnsureDefectMap()
        {
            if (hotPixelIndices != null) return;
            const int SensorSerialSeed = 20260721;
            var rng = new System.Random(SensorSerialSeed);
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
        {
            if (sigma <= 0f) return 0f;
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return (float)(z * sigma);
        }

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);

        /// <summary>
        /// Relative sky-glow/haze throughput per filter, against Luminance -- a narrower filter
        /// passes proportionally less of the (per-second, full-bandwidth-implied) sky background
        /// just as it passes proportionally less of the target's own signal, so this is derived
        /// directly from each filter's real bandwidth (VisualTelescopeSpec, see
        /// FilterBandwidthAngstrom) rather than a separately-tuned set of ratios -- one real
        /// number feeds both, instead of two figures that could silently drift apart.
        /// </summary>
        private static float FilterThroughput(CameraFilter filter)
        {
            if (filter == CameraFilter.Luminance) return 1.0f;
            return (float)(FilterBandwidthAngstrom(filter) / LuminanceBandwidthAngstrom);
        }

        /// <summary>Signal a mono sensor records through the given filter (L = luminance, R/G/B = single channel, H-alpha = red).</summary>
        private static float FilterSignal(Color c, CameraFilter filter)
        {
            switch (filter)
            {
                case CameraFilter.Red:    return c.r;
                case CameraFilter.Green:  return c.g;
                case CameraFilter.Blue:   return c.b;
                case CameraFilter.HAlpha: return c.r; // H-alpha sits in the deep red
                default:                  return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            }
        }

        /// <summary>Drift trail length in pixels for an untracked exposure. Capped so even a 30 s sub never fills the whole frame.</summary>
        private int ComputeDriftPixels(float exposureSeconds, CelestialBody targetBody)
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            double rotationPeriod = home != null && home.rotationPeriod > 0 ? home.rotationPeriod : 21600.0;
            double driftDegPerSec = 360.0 / rotationPeriod;      // sidereal rate, tied to Kerbin's spin
            double driftDeg = driftDegPerSec * exposureSeconds;  // 1 real second treated as 1 sky-second
            float fov = Mathf.Clamp(FovDeg, MinFovDeg, MaxFovDeg);
            double pxPerDeg = TextureWidth / (double)fov;
            int px = (int)(driftDeg * pxPerDeg);
            return Mathf.Clamp(px, 0, TextureWidth / 3);
        }

        /// <summary>Horizontal motion blur — the classic untracked star-trail smear.</summary>
        /// <summary>
        /// Horizontal-only sliding-window (prefix-sum) box blur, edge-clamped -- O(w) per
        /// row regardless of the blur length, instead of the naive O(w*length) resampling a
        /// per-pixel loop over each offset would cost. Needed once the sensor is real
        /// resolution: ComputeDriftPixels' length can reach into the hundreds of pixels, and a
        /// naive per-offset sum at that length, times millions of pixels, is the single most
        /// expensive pass in the whole frame.
        /// </summary>
        private void ApplyHorizontalMotionBlur(Color[] buffer, int length)
        {
            if (length < 1) return;
            if (blurScratch == null || blurScratch.Length != buffer.Length)
                blurScratch = new Color[buffer.Length];
            int w = TextureWidth, h = TextureHeight;
            if (rowPrefixScratch == null || rowPrefixScratch.Length < w + 1) rowPrefixScratch = new float[w + 1];
            float inv = 1f / (length + 1);

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                rowPrefixScratch[0] = 0f;
                for (int x = 0; x < w; x++) rowPrefixScratch[x + 1] = rowPrefixScratch[x] + buffer[row + x].r;

                for (int x = 0; x < w; x++)
                {
                    // Window [x-length, x], each sample individually edge-clamped -- matches the
                    // original per-offset Mathf.Clamp behavior exactly (edge pixels repeat).
                    int clampedLo = Math.Max(0, x - length);
                    int leftPadCount = Math.Max(0, length - x);
                    float innerSum = rowPrefixScratch[x + 1] - rowPrefixScratch[clampedLo];
                    float sum = innerSum + leftPadCount * buffer[row].r;
                    float v = sum * inv;
                    blurScratch[row + x] = new Color(v, v, v, 1f);
                }
            }
            Array.Copy(blurScratch, buffer, buffer.Length);
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
            if (outputTexture != null) { UnityEngine.Object.Destroy(outputTexture); outputTexture = null; }
            if (capturedTexture != null) { UnityEngine.Object.Destroy(capturedTexture); capturedTexture = null; }
            galaxyCam = null;
            scaledSpaceCam = null;

            // Every resolution-sized scratch buffer/state must be rebuilt fresh at whatever
            // resolution EnsureSceneBuilt runs next at (native size change on a binning switch).
            pixelScratch = null;
            blurScratch = null;
            frameScratch = null;
            psfPlaneScratch = null;
            psfHaloScratch = null;
            displayScratch = null;
            rawScratch = null;
            astigmatismScratch = null;
            rowPrefixScratch = null;
            hotPixelIndices = null;
            deadPixelIndices = null;
            lastCaptureSnapshot = null;
            hasLockedAim = false;
        }
    }
}
