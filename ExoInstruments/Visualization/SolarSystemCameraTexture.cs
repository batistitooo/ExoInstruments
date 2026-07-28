using System;
using System.Collections.Generic;
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
    /// RC20 astrograph camera: clones KSP's scaled-space camera and
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

            // A cooler setpoint belongs to the camera that was on the telescope, not to the
            // observer: carrying -30 C from a TEC-cooled ZWO onto FORS2's cryogenic detector would
            // be meaningless, and the two instruments' reachable ranges do not even overlap.
            ResetCoolerSetpoint();
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

        /// <summary>
        /// Bytes of MANAGED memory this pipeline holds per pixel of the frame, counted from the
        /// buffers it actually allocates rather than estimated:
        ///
        ///   float[] at 4 bytes each  -- pixelScratch, frameScratch, lastCaptureSnapshot,
        ///                               rawScratch, signalScratch, lastAduFrame,
        ///                               passScratch                                = 28
        ///   byte[]                   -- displayScratch, three bytes for an RGB24 texture =  3
        ///   float[] transient        -- FourierConvolution's overlap-add accumulator =  4
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
        /// This exists because the cost is quartic in the binning factor -- halving the binning
        /// quadruples the pixel count -- and nothing told the player. At its native resolution the
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

        /// <summary>Largest count this camera's real ADC can output: 2^AdcBits - 1.</summary>
        public static int AdcMaxCount => (1 << Math.Max(1, Spec.AdcBits)) - 1;

        /// <summary>Real conversion factor in electrons per ADU at the current gain setting. Gain amplifies the signal ahead of the converter, so a higher gain means FEWER electrons per count.</summary>
        public static double ElectronsPerAdu(double gainMultiplier)
            => Spec.ElectronsPerAduAtUnityGain / Math.Max(1e-6, gainMultiplier);

        /// <summary>
        /// Charge at which the ADC's top count is reached. Deliberately NOT scaled by binning:
        /// on-chip binning sums charge ahead of one amplifier and one converter, so the digital
        /// ceiling stays put in ADU while the well below it grows -- which is why binning a
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
        /// the numerical truncation limits (65535 adu)" -- its 150,000 e- well is never reached,
        /// because at K = 1.25 e-/ADU the converter tops out at 81,919 e- first. A pipeline
        /// carrying fractions of full well cannot represent that at all: it has only one ceiling,
        /// and it is the wrong one.
        /// </summary>
        public static double SaturationElectrons(double gainMultiplier)
            => Math.Min(FullWellElectrons, DigitalSaturationElectrons(gainMultiplier));

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

        private const string ScaledSpaceCameraName = "Camera ScaledSpace";

        // Real filter bandwidths in Angstrom, matching FilterThroughput's ratios: L covers the
        // whole ~420-685nm visible band; R/G/B each get an even third (modern "1:1:1 balanced"
        // CMOS LRGB filter design); H-alpha is a real ~7nm (70 Angstrom) narrowband filter.
        private static double LuminanceBandwidthAngstrom => Spec.LuminanceBandwidthAngstrom;

        /// <summary>Real sensor exposure range -- see VisualTelescopeCatalog for sourcing.</summary>
        public static float MinExposureSeconds => Spec.MinExposureSeconds;
        public static float MaxExposureSeconds => Spec.MaxExposureSeconds;

        private const float MaxDefocusBlurPx = 7.0f;

        /// <summary>
        /// Airmass at which the seeing power law stops growing. X = 6 is about 9.5 degrees
        /// altitude -- already below where anyone would image, and far below where the
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
        // NOT amplified by gain (applied after the analog stage) -- the active telescope's real
        // read noise (a fixed per-readout-event electron figure, unaffected by binning) as a
        // fraction of the CURRENT BINNED full well (this class's own FullWellElectrons, not
        // Spec.FullWellElectrons directly) -- the same sensor anchor used for shot and
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

        // Zodiacal light and the dark-sky airglow baseline now live in SkyBrightnessModel,
        // as the published V surface brightnesses they are (Leinert et al. 1998; Patat 2003).

        // Full-well overflow ("blooming"): Pyxel only hard-clips at full well and ships no
        // redistribution model to port. Real CCD full-well overflow is described in Janesick
        // (2001, "Scientific Charge-Coupled Devices", SPIE Press) as charge spilling along the
        // column (parallel/vertical shift-register) direction; absent a specific device's
        // anti-blooming-gate asymmetry data, the textbook default is a charge-conserving,
        // symmetric split between the two vertical neighbors -- 0.5 to each means all of the
        // excess is conserved, none invented or discarded.
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
        /// <summary>
        /// Charge a cosmic-ray track leaves in a pixel, as a fraction of the physical full well.
        /// A minimum-ionising particle crossing the full depletion depth deposits far more than
        /// a well can hold, which is why real cosmic rays read out saturated; 0.85 leaves them
        /// just short of it so they stay distinguishable from a genuinely blown pixel.
        /// </summary>
        private const float CosmicRayDepositWellFraction = 0.85f;

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
        /// It is a Color[] over the whole frame -- 270 MB on the largest instrument at native
        /// resolution -- and it is read exactly once, at the very start, before the expensive
        /// optics. Carried in the struct it stayed reachable from the task's closure for the whole
        /// capture, holding that memory across the convolution that needs it most.
        /// </summary>
        private Color[] pendingSrc;

        // The pipeline is MONOCHROME end to end -- every write was new Color(v, v, v, 1f) and
        // every read was .r -- so these carried four copies of one number plus a constant alpha,
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
        /// leftovers. Three separate planes cost three times the memory for no benefit -- 135 MB of
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
        /// of its ingredients were already being computed -- the integrated system response, the
        /// real obstructed collecting area, the conversion gain -- and never combined into the one
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
        /// LastTargetElectrons is what the physics computed the body should deliver -- aperture,
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

        /// <summary>Returns the setpoint to the instrument's own published operating temperature -- the one its catalogued dark current was measured at, so the model is back on its calibration point.</summary>
        public static void ResetCoolerSetpoint() => detectorTemperatureOverrideCelsius = double.NaN;

        private static double ClampToCoolerRange(double celsius)
        {
            if (double.IsNaN(celsius)) return celsius;
            if (!Spec.HasAdjustableCooler) return Spec.DetectorTemperatureCelsius;
            return Math.Max(Spec.CoolerMinimumTemperatureCelsius,
                            Math.Min(Spec.CoolerMaximumTemperatureCelsius, celsius));
        }

        /// <summary>Charge at which the last capture stopped responding -- the smaller of the physical well and the converter's ceiling.</summary>
        public double LastSaturationElectrons => lastSaturationElectrons;
        private double lastSaturationElectrons;

        /// <summary>
        /// The last capture as the detector's own ADU counts -- the calibratable data product.
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
        /// <summary>
        /// Where the last capture actually pointed, as a FITS world coordinate system. Invalid
        /// (and therefore not written to the header) when the field geometry could not be
        /// resolved -- outside the scenes where the observatory site is available, for instance.
        /// </summary>
        public Core.FitsWcs LastWcs { get; private set; }

        /// <summary>Airmass the last capture was taken through; +Infinity if the target was below the horizon.</summary>
        public double LastAirmass { get; private set; }

        /// <summary>
        /// The effective photometric width (Angstrom) the last capture was calibrated with, for a
        /// flat source spectrum -- the single number that turns an apparent magnitude into
        /// electrons through this instrument at this airmass (see SystemBandpass). Recorded in the
        /// exported header because with it, the aperture area and the exposure time, a reader can
        /// reproduce this frame's photometry exactly rather than having to trust it.
        /// </summary>
        public double LastEffectiveWidthAngstrom { get; private set; }

        /// <summary>Central wavelength (nm) of the fitted filter.</summary>
        public double ActiveFilterCentralWavelengthNm => FilterCentralWavelengthMeters(Filter) * 1e9;

        /// <summary>Published FWHM (nm) of the fitted filter.</summary>
        public double ActiveFilterBandwidthNm => FilterBandwidthAngstrom(Filter) * 0.1;

        /// <summary>True when the last capture ran unguided long enough for the sky to turn under the sensor, so its sources are trailed and its WCS describes only the exposure's start.</summary>
        public bool LastFrameTrailed { get; private set; }

        public double LastAtmosphericFwhmArcsec { get; private set; }

        /// <summary>The instrument's own diffraction-limited FWHM (arcsec) at the current filter's wavelength, computed from its real annular pupil -- the hard floor no observing condition can beat.</summary>
        public double LastDiffractionFwhmArcsec { get; private set; }

        /// <summary>
        /// Last capture at full float precision, straight from the physics pipeline -- NOT
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

            // The exposure has elapsed, but the target's scaled-space textures may not be bound.
            // Kopernicus unloads them when no camera IT knows about can see the body, and this
            // telescope renders through clones it does not know about; the reload it then
            // performs on request is DEFERRED, not synchronous (see
            // KopernicusOnDemandIntegration for the log that establishes this). Rendering into
            // that gap draws the body's geometry with no colour map: a black disc with a lit rim.
            //
            // So the capture waits for residency rather than racing it. Bounded, because a body
            // whose loader never flips its own isLoaded flag must not hang the shutter forever --
            // after the cap the frame is taken regardless, which is exactly the old behaviour.
            if (!KopernicusOnDemandIntegration.EnsureScaledSpaceTexturesLoaded(pendingTarget))
            {
                if (++textureWaitFrames <= MaxTextureWaitFrames) return;

                Debug.LogWarning($"[ExoInstruments] Scaled-space textures for {pendingTarget?.bodyName} did not "
                               + $"become resident within {MaxTextureWaitFrames} frames; capturing anyway. "
                               + "The body may render without its colour map.");
            }

            textureWaitFrames = 0;
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

            // The snapshot is taken from the LINEAR pipeline output, before any display transfer
            // function -- it is what the FITS export and AstroImageStack consume, and stretching
            // it would corrupt every downstream measurement.
            if (lastCaptureSnapshot == null || lastCaptureSnapshot.Length != pixelScratch.Length)
                lastCaptureSnapshot = new float[pixelScratch.Length];
            Array.Copy(pixelScratch, lastCaptureSnapshot, pixelScratch.Length);

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
            if (lastCaptureSnapshot == null) return;

            int n = lastCaptureSnapshot.Length;
            if (capturedTexture == null || capturedTexture.width != TextureWidth || capturedTexture.height != TextureHeight)
            {
                if (capturedTexture != null) UnityEngine.Object.Destroy(capturedTexture);
                capturedTexture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGB24, false);
            }

            // Straight into the texture's own raw bytes rather than through a Color[] staging
            // array. The destination is RGB24 -- three bytes a pixel, which is all a monitor can
            // show and all this path ever claimed to carry -- so a 16-byte Color per pixel was
            // buying nothing on the way there. On the largest instrument at native resolution that
            // staging array alone was 270 MB.
            //
            // Nothing is lost that was not already being lost: this is the DISPLAY path, and the
            // linear full-precision frame it is built from stays untouched in lastCaptureSnapshot
            // for the FITS export and the stacker.
            if (displayScratch == null || displayScratch.Length != n * 3) displayScratch = new byte[n * 3];
            for (int i = 0; i < n; i++)
            {
                float v = ApplyDisplayStretch(lastCaptureSnapshot[i]);
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
            // Every fader touched is recorded and put back, not just the home body's. A capture
            // is an observation and must leave the game exactly as it found it: forcing every
            // body's stand-in on and walking away leaves the live scene drawing stand-ins KSP had
            // deliberately faded out. That relied on ScaledSpaceFader re-deciding the flag on its
            // own next frame -- probably true, never verified, and not something a capture should
            // be betting the player's scene on.
            if (faderRestoreBuffer == null || faderRestoreBuffer.Length != scaledSpaceFaders.Length)
                faderRestoreBuffer = new bool[scaledSpaceFaders.Length];

            for (int i = 0; i < scaledSpaceFaders.Length; i++)
            {
                ScaledSpaceFader fader = scaledSpaceFaders[i];
                if (fader == null || fader.r == null) continue;

                faderRestoreBuffer[i] = fader.r.enabled;
                fader.r.enabled = !(home != null && fader.celestialBody == home);
            }

            // KSP's galaxy camera is NOT rendered, and that is deliberate.
            //
            // It draws the game's painted sky cube, and a telescope cannot use it. The cube is
            // 4096 pixels across a 90-degree face, i.e. 1.32 arcmin per texel, while FORS2's
            // field is 8.6 arcmin: the frame covers about six texels and magnifies them 628x.
            // What reaches the sensor is therefore not a sky but a bilinear interpolation of a
            // handful of texels -- vast smooth blobs, which the 8-bit render target then slices
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
            // brightness, by SkyBrightnessModel -- airglow (Patat 2003), zodiacal light (Leinert
            // et al. 1998), moonlight (Krisciunas & Schaefer 1991) and twilight (Patat et al.
            // 2006) -- and added after the optics, where a uniform sky belongs.
            //
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
                    // belongs in it -- airglow, zodiacal light, moonlight, twilight, and every
                    // catalogue star -- is added later by the physics, in real surface brightness.
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

                // Hand every stand-in back exactly as it was found -- the live scene draws
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
            if (builtOnce) Dispose(); // binning or active telescope changed since the last build -- tear down and rebuild at the new resolution
            builtOnce = true;
            builtBinningFactor = BinningFactor;
            builtSpec = Spec;

            try
            {
                Camera liveScaledSpace = FindCameraByName(ScaledSpaceCameraName);
                if (liveScaledSpace == null)
                {
                    Debug.LogWarning("[ExoInstruments] Could not find KSP's scaled-space camera -- solar-system camera disabled.");
                    available = false;
                    return;
                }

                root = new GameObject("ExoInstrumentsSolarSystemCamera");

                // Half-float capture, not 8-bit.
                //
                // The rendered scene supplies every bit of spatial structure this pipeline has --
                // the belts, the terminator, the limb darkening, and any companion sharing the
                // frame -- and the physics then multiplies the whole plane by a single
                // calibration factor. Quantising it first therefore quantises the finished
                // photograph, and 8 bits is nowhere near enough for the range a real frame holds:
                // sRGB-encoded ARGB32 resolves 3295:1, i.e. 8.8 magnitudes, so Jupiter at V=-2.5
                // and a Galilean moon at V=5.0 (a real 1000:1 ratio) put that moon on 3.3
                // quantisation levels. Its limb, its phase and its shading are gone before the
                // optics are even applied, and any non-linear display stretch then slices what
                // remains into visible contour bands -- the same mechanism that made the painted
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
                // which is exactly the intent -- a float target needs no encoding to hold range.
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
                // target with a 24-bit depth buffer -- 12 bytes per pixel, so about 203 MB of
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
                // Unity applies no colour conversion on the way through -- the readback is a
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

        /// <summary>Plain-data snapshot of everything ComputeFramePixels needs -- gathered on the main thread (it touches CelestialBody/Unity APIs), then handed to a background Task that touches none of that, mirroring the StartImagingRefresh/PollImagingRenderTask pattern used elsewhere in this mod.</summary>
        private struct FrameComputeInputs
        {
            public int TargetSeed;
            public double Ut;
            public float ExposureSeconds;
            public float IsoGain;
            public CameraFilter Filter;
            public double ScintSigma;
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

            // --- Sky field ------------------------------------------------------------
            /// <summary>Where each direction on the sky lands on the sensor, built from the camera's own axes (see BuildFieldGeometry).</summary>
            public GnomonicProjection Projection;
            /// <summary>Catalogue stars whose light reaches the sensor this exposure, already cone-searched on the main thread.</summary>
            public List<RenderedStar> Stars;
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
            /// <summary>The instrument's integrated spectral response for this filter and airmass -- optics, filter, QE curve and extinction in one object (see SystemBandpass). Built on the main thread, read-only thereafter.</summary>
            public SystemResponse Response;
            public double ApertureAreaCm2;
            /// <summary>Atmospheric extinction at the fitted filter's own wavelength: extinction alone, no ND filter, no cloud.</summary>
            public double CloudTransmission;
            /// <summary>
            /// Transmission a catalogue star loses OUTSIDE the spectral response: cloud and the ND
            /// filter. Atmospheric extinction is deliberately not here -- it is wavelength
            /// dependent and now lives inside Response's integral, so including it again would
            /// attenuate every source twice.
            /// </summary>
            public double StarNonAtmosphericTransmission;

            // --- Diurnal drift, as a real vector on the sensor ---------------------------
            /// <summary>Pixel displacement of the field centre over the exposure. Zero when the mount tracks.</summary>
            public double DriftPixelX;
            public double DriftPixelY;
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
            // Handed to the background pass through a field rather than through the inputs struct,
            // so that pass can drop it as soon as it has read it -- see pendingSrc.
            pendingSrc = readbackTexture.GetPixels();
            EnsureDefectMap();

            float exposureSeconds = Mathf.Clamp(ExposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            float isoGain = Mathf.Clamp(Gain, MinGain, MaxGain);

            double ut = Planetarium.GetUniversalTime();

            TryComputeAltitudeDeg(targetBody, out double targetAltDeg);
            double airmass = targetAltDeg > 0.0 ? ImagingObservingConditions.AirmassAt(targetAltDeg) : double.PositiveInfinity;
            // Extinction at the fitted filter's own wavelength: a real site is far more
            // transparent in the red than in the blue, so a single grey coefficient made every
            // filter of an LRGB set lose exactly the same light, which they do not.
            float extinction = (float)AtmosphericImagingNoise.ExtinctionTransmissionAt(
                airmass, FilterCentralWavelengthMeters(Filter), Spec.SiteAltitudeMeters);

            double angularDiameterRad = ComputeAngularDiameterRad(targetBody);
            double scintSigma = AtmosphericImagingNoise.ScintillationExcessSigma(
                Spec.ApertureMeters, Spec.SiteAltitudeMeters, airmass, exposureSeconds, angularDiameterRad);

            // The Sun's real altitude, handed straight to the sky model; twilight brightness is
            // a measured function of solar depression, not a normalised ramp between two limits.
            bool haveSunAlt = TryComputeAltitudeDeg(Planetarium.fetch != null ? Planetarium.fetch.Sun : null, out double sunAltDeg);
            double moonSkyExcess = ComputeMoonSkyExcess(targetBody);
            float coverage = ComputeCloudCoverage();

            // The instrument's whole spectral response for this filter and airmass, integrated
            // once here and then reused by every source in the frame -- bodies, stars and sky
            // alike, which is what keeps them on one flux scale (see SystemBandpass).
            SystemResponse response = BuildSystemResponse(Filter, airmass);
            LastAirmass = airmass;
            LastEffectiveWidthAngstrom = response.EffectiveWidthAngstromFlat;

            double totalElectrons = ComputeCollectedElectrons(targetBody, response, 1.0, exposureSeconds);

            // Seeing is the site's own atmospheric term and nothing else. Cloud cover used to add
            // a blur here, and no longer does, for two independent reasons.
            //
            // It was quoted in PIXELS (a fixed 2px scaled by the plate scale), so the same
            // overcast sky delivered four times the angular blur at binning 4 as at binning 1 --
            // exactly the defect this function was rewritten to remove from the seeing term.
            //
            // And correcting the unit would only have moved the problem: there is no published
            // coefficient relating cloud cover to delivered FWHM, because it is not an optical
            // mechanism. Cloud ATTENUATES, and that is modelled -- CloudTransmission removes up
            // to CloudMaxAttenuation of every source's flux, from EVE's real cloud texture
            // sampled at the observatory's own zenith -- and cloud VEILS, which is modelled too
            // (see CloudVeilingSkyGain). Bad seeing and cloud are correlated symptoms of unsettled
            // weather, not one causing the other, so a blur term here would have been an invented
            // constant standing in for a mechanism that does not exist.
            //
            // Only the plain ground-based term is resolved here, because only it needs the
            // target's airmass; the adaptive-optics solve is pure arithmetic and happens
            // off-thread.
            double seeingFwhmArcsec = ComputeGroundSeeingFwhmArcsec(airmass);
            double defocusDiscRadiusPx = Autofocus ? 0.0 : Mathf.Abs(FocusOffset) * MaxDefocusBlurPx;

            var inputs = new FrameComputeInputs
            {
                TargetSeed = targetBody.flightGlobalsIndex,
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
            };

            // A star is a point source, so it gets none of the extended-source scintillation
            // suppression a resolved disk enjoys; it is the same reason a planet looks steady to the
            // naked eye while a star of the same brightness twinkles.
            inputs.PointSourceScintSigma = AtmosphericImagingNoise.ScintillationExcessSigma(
                Spec.ApertureMeters, Spec.SiteAltitudeMeters, airmass, exposureSeconds, 0.0);

            GatherSkyBackground(ref inputs, targetAltDeg, sunAltDeg, haveSunAlt, coverage);
            GatherSkyField(ref inputs, targetBody, exposureSeconds, airmass);

            return inputs;
        }

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
            // the four are sunlight scattered off something -- the zodiacal dust cloud, the Moon,
            // and the daytime atmosphere itself -- so they genuinely carry the solar spectral
            // shape. Airglow does not: it is atmospheric line emission (the OI 557.7nm line and
            // the OH Meinel bands), with no continuum shape this pipeline could integrate, so it
            // is integrated flat and assumes nothing. Summing all four and integrating once would
            // have forced one spectrum on all of them.

            // Airglow is emitted inside the atmosphere: the van Rhijn path lengthening brightens
            // it toward the horizon while extinction over the same path dims it, and the two
            // largely cancel, which is why both are applied and neither alone.
            double fluxFlat = Math.Pow(10.0, -0.4 * SkyBrightnessModel.DarkSkyZenithVMagPerArcsec2)
                            * SkyBrightnessModel.AirglowVanRhijnFactor(zenithAngleDeg, planetRadius)
                            * transmission;

            // Zodiacal light originates outside the atmosphere, so it is simply attenuated by it.
            double fluxSolar = Math.Pow(10.0, -0.4 * SkyBrightnessModel.ZodiacalVMagPerArcsec2) * transmission;

            // Moonlight and twilight are both sunlight scattered WITHIN the atmosphere, so the
            // extinction along the line of sight is already part of the measured surface
            // brightness the model is calibrated against and is not applied again.
            fluxSolar = SkyBrightnessModel.AddMagnitude(fluxSolar, SkyBrightnessModel.MoonlightVMagPerArcsec2(inputs.MoonSkyExcess));
            if (haveSunAlt) fluxSolar = SkyBrightnessModel.AddMagnitude(fluxSolar, SkyBrightnessModel.TwilightVMagPerArcsec2(sunAltDeg));

            // Cloud veiling: cloud scatters ground and sky light back down, which is why an
            // overcast night sky is brighter than a clear one rather than darker. Modelled as a
            // multiplier on the sky that is already there, since that light is its source -- so it
            // applies to both groups alike.
            double veiling = 1.0 + cloudCoverage * CloudVeilingSkyGain;
            fluxFlat *= veiling;
            fluxSolar *= veiling;

            // The response is used without extinction here and the transmission above is applied
            // per term instead, since each of the four is attenuated differently.
            double area = RealApertureAreaCm2();
            double nd = NdFilterTransmission(NdFilter);
            double perSecond =
                SkyBrightnessModel.ElectronsPerPixelPerSecond(
                    SkyBrightnessModel.FluxToMagPerArcsec2(fluxFlat),
                    inputs.PlateScaleArcsec, inputs.Response, area, nd, 0.0)
              + SkyBrightnessModel.ElectronsPerPixelPerSecond(
                    SkyBrightnessModel.FluxToMagPerArcsec2(fluxSolar),
                    inputs.PlateScaleArcsec, inputs.Response, area, nd,
                    SourceSpectra.SolarPhotosphereTemperatureK);

            inputs.SkyElectronsPerPixel = perSecond * inputs.ExposureSeconds;
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
        private void GatherSkyField(ref FrameComputeInputs inputs, CelestialBody targetBody,
                                    float exposureSeconds, double airmass)
        {
            inputs.HaveFieldGeometry = false;
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

            inputs.Stars = SearchStarCatalog(inputs, projection, meridianRaDeg, latitudeDeg);
            inputs.UnresolvedBodies = GatherUnresolvedBodies(inputs, targetBody, projection, exposureSeconds);
            inputs.TotalElectrons = ComputeSceneElectrons(inputs, targetBody, projection, exposureSeconds);
        }

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
        private double ComputeSceneElectrons(FrameComputeInputs inputs, CelestialBody targetBody,
                                             GnomonicProjection projection, float exposureSeconds)
        {
            // Extinction is inside inputs.Response, so only the cloud term is handed over here.
            double bodyTransmission = inputs.CloudTransmission;
            double total = ComputeCollectedElectrons(targetBody, inputs.Response, bodyTransmission, exposureSeconds);
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

        /// <summary>Sky surface brightness (V mag/arcsec^2) behind the last capture: the number a real observer would quote for the conditions. Higher is darker.</summary>
        public double LastSkyBrightnessVMagPerArcsec2 { get; private set; }

        /// <summary>Number of catalogue stars actually drawn into the last capture.</summary>
        public int LastStarsDrawn => lastStarsDrawnInternal;

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

            latitudeDeg = ObservatorySite.LatitudeDeg;
            double longitudeDeg = ObservatorySite.LongitudeDeg;
            double elevation = ObservatorySite.SiteElevationMeters;

            Vector3d observer = ObservatorySite.WorldPosition(home);
            Vector3d up = (observer - home.position).normalized;
            if (up.sqrMagnitude < 0.5) return false;

            // Step in latitude/longitude and see which way the world moves. At the poles the
            // northward step would run over the top, so it is taken southward and negated.
            const double StepDeg = 0.01;
            bool nearNorthPole = latitudeDeg + StepDeg > 90.0;
            Vector3d northProbe = home.GetWorldSurfacePosition(
                nearNorthPole ? latitudeDeg - StepDeg : latitudeDeg + StepDeg, longitudeDeg, elevation) - observer;
            if (nearNorthPole) northProbe = -northProbe;
            Vector3d eastProbe = home.GetWorldSurfacePosition(latitudeDeg, longitudeDeg + StepDeg, elevation) - observer;

            Vector3d north = Orthonormalize(northProbe, up);
            Vector3d east = Orthonormalize(eastProbe, up);
            if (north.sqrMagnitude < 0.5 || east.sqrMagnitude < 0.5) return false;

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
        /// actually drawn at -- including the optical throughput, which makes this figure
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
        private List<PointSource> GatherUnresolvedBodies(FrameComputeInputs inputs, CelestialBody targetBody,
                                                         GnomonicProjection projection, float exposureSeconds)
        {
            var sources = new List<PointSource>();
            if (FlightGlobals.Bodies == null) return sources;

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

            Vector3d observer = ObservatorySite.WorldPosition(home);
            Vector3d toBody = body.position - observer;
            if (toBody.sqrMagnitude < 1.0) return false;
            toBody = toBody.normalized;

            if (Vector3d.Dot(toBody, siteUp) <= 0.0) return false; // below the observatory's horizon

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
                    wavelength, atmosphericFwhm, inputs.DefocusDiscRadiusPx,
                    Spec.SpiderVaneCount, Spec.SpiderVaneWidthMeters, out psfCacheCoreRadius);

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
            // native per-physical-pixel DarkCurrentElectronsPerSecond -- both real electron
            // quantities scale by BinningFactor^2 together in a real binned pixel, so the
            // resulting pedestal/sigma FRACTION (what DarkCurrent actually returns) comes out
            // identical either way; using the raw per-pixel numbers is just simpler than
            // multiplying both sides by the same factor for no change in the answer.
            // A binned pixel collects the dark current of every physical pixel it merges, so the
            // rate scales with the binned area. In electrons, like everything else here.
            // Dark current at the detector's ACTUAL temperature, scaled from its published rate at
            // its own published operating temperature by the depletion-generation law (Janesick
            // 2001; Varshni 1967 band gap -- see Core.DarkCurrentModel). While the two agree, which
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
            // That was impossible before -- System.Random's sequence for a seed is not part of
            // .NET's contract and has changed between runtimes -- and it is what any regression
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
            // Unity render came back empty -- a refused render target, a readback that produced
            // nothing, a scaled-space body that was not drawn -- then this sum is zero, the factor
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
            float sceneScale = calibratedSignalPerUnit * scintJitter * cloudTransmission;
            for (int i = 0; i < n; i++) signal[i] *= sceneScale;

            // The rendered scene is a snapshot at one instant; an unguided mount lets the sky
            // slide across the sensor during the exposure, so the whole scene draws a streak
            // along the real drift vector rather than the horizontal-only smear assumed before.
            // Negated: the rendered snapshot is the END of the exposure, so the scene's streak
            // extends backwards from where it was drawn, the same way each star's does.
            ApplyLinearSmear(signal, -inputs.DriftPixelX, -inputs.DriftPixelY);

            // Then everything unresolved. Stars are point sources, so they carry the point-source
            // scintillation rather than the resolved disk's much quieter figure.
            lastStarsDrawnInternal = 0;
            if (inputs.HaveFieldGeometry)
            {
                float starScint = ScintillationMultiplier(rngScint, inputs.PointSourceScintSigma);
                DepositSkyField(signal, inputs, starScint);
            }

            // --- 2. Optics -------------------------------------------------------------
            // The instrument's real PSF: diffraction off its own annular pupil, convolved with
            // the Kolmogorov atmosphere and any defocus (see OpticalPsf). One convolution over
            // the whole signal plane, so a star and the planet beside it get the same optics and
            // nothing is blurred twice.
            EnsurePsfKernels(inputs, out float[] psfCore, out int psfRadius,
                             out float psfCoreWeight, out float[] psfHalo, out int psfHaloRadius);
            ApplyPsf(signal, psfCore, psfRadius, psfCoreWeight, psfHalo, psfHaloRadius);

            // Field-dependent astigmatism, applied after the PSF so it reads as a distinct
            // off-axis smear rather than blending into the on-axis profile.
            ApplyAstigmatismBlur(signal);

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

            lastSaturatedFraction = chain.SaturatedFraction;
            lastElectronsPerAdu = chain.ElectronsPerAdu;
            lastSaturationElectrons = chain.SaturationElectrons;
            lastBiasLevelAdu = chain.BiasLevelAdu;

            // The zero point, so the exported frame can actually be turned back into magnitudes:
            //   m = -2.5 log10(ADU/s) + ZP,   ZP = 2.5 log10(F0 * W * A * T_nd / K)
            // which is just the pipeline's own photometry equation solved for m. Quoted for a FLAT
            // source spectrum, as a zero point always is -- a real star's own colour enters through
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

            // No defect overlay here any more. Hot and dead pixels are applied in the charge domain
            // (ApplyPixelDefects, above) where they physically originate, so by this point they
            // have already been through blooming, charge transfer, read noise and digitisation like
            // every other pixel -- which is what makes them removable by a dark frame and a bad
            // pixel map instead of being permanent marks on the data.
            //
            // The frame stays genuinely raw and uncorrected: AstroImageStack.AddSub still receives
            // it and cosmetically corrects it against the same fixed defect map before aligning and
            // stacking, the order real calibration pipelines (PixInsight, IRAF/ccdproc, ESO Reflex)
            // use -- raw frame -> bad-pixel-map correction -> registration -> stacking.

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
        /// it was produced by the same chain, and a second implementation of the chain -- however
        /// carefully written -- is free to drift from the first the moment either is edited.
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

            // Charge collection. Poisson, not a Gaussian of matching width: photon arrival IS a
            // counting process, and the two only agree once the count is large. At the few
            // electrons per pixel a faint sky or a short dark reaches, a Gaussian goes negative
            // and is measurably the wrong distribution -- the same reason GalSim and Pyxel both
            // draw real Poisson deviates here.
            for (int i = 0; i < n; i++)
            {
                double sceneElectrons = signal != null ? signal[i] : 0.0;
                double meanElectrons = Math.Max(0.0, sceneElectrons + skyElectrons + darkElectrons);
                raw[i] = (float)SamplePoisson(rng, meanElectrons);
            }

            // The sensor's own defects, applied HERE -- in the charge domain, alongside the dark
            // current they are made of -- rather than stamped over the finished counts after
            // digitisation, which is what this pipeline used to do.
            //
            // A hot pixel is not a bright dot the readout paints on. It is a pixel whose depletion
            // region generates charge at a multiple of the array's rate because of a bulk lattice
            // defect; Widenhorn et al. (2002) show it is precisely the depletion component, the one
            // that dominates in a cooled detector, that varies from pixel to pixel. Three things
            // follow that the old treatment got wrong: the defect now GROWS WITH EXPOSURE TIME (a
            // 1-second sub shows a faint speck where a 300-second one shows a blown pixel, instead
            // of both showing the same near-full-scale dot), it responds to detector temperature
            // through the same law as the rest of the dark current, and -- the point of all of it --
            // subtracting a dark frame REMOVES it, which is the entire reason an observer takes one.
            //
            // A dead pixel is the converse: no photo response at all, so it collects no signal and
            // no sky, but its silicon still generates dark charge like any other pixel. It reads
            // near the pedestal rather than at exactly zero, and a flat frame is what identifies it.
            ApplyPixelDefects(raw, signal, skyElectrons, darkElectrons, isoGain, rng);

            // Charge-domain effects, in the order the silicon applies them and now on real
            // electron counts against a real well, so the thresholds mean something.
            ApplyCosmicRays(raw, exposureSeconds, rngCosmic);
            ApplyBlooming(raw, (float)FullWellElectrons);
            ApplyChargeTransferSmear(raw);

            // Readout: the amplifier's own noise is added in electrons, ahead of the converter,
            // which is where it physically enters.
            float readNoiseElectrons = (float)Spec.ReadNoiseElectrons;
            for (int i = 0; i < n; i++)
                raw[i] += NextGaussian(rngRead, readNoiseElectrons);

            // Digitisation: charge divided by the real conversion factor K, truncated to an integer
            // count the way an ADC actually works, and clipped at the converter's own top code --
            // which for FORS2 arrives well before its full well ever does.
            var result = new DetectorChainResult
            {
                ElectronsPerAdu = ElectronsPerAdu(isoGain),
                SaturationElectrons = SaturationElectrons(isoGain),
            };
            int adcMax = AdcMaxCount;

            // The bias pedestal, added ahead of the converter exactly where the readout electronics
            // add it. Without one, the clip at zero below removed the negative half of the read
            // noise wherever a pixel's total charge sat within a read noise of zero -- biasing it
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
        }

        /// <summary>
        /// A calibration frame, in the detector's own ADU -- the shutter-closed companion to a
        /// science exposure, and what makes one reducible.
        ///
        /// It runs the same RunDetectorChain a science frame does, with no scene light and no sky,
        /// so what it records is exactly what a real bias or dark records: the pedestal, the read
        /// noise, and (for a dark) the dark current with its hot pixels and whatever cosmic rays
        /// arrived. Subtracting a dark of matching exposure and temperature from a light frame
        /// removes all of it, which is now true of this pipeline in the way it is true of a real
        /// one -- it was not while hot pixels were stamped on after digitisation.
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

            DetectorChainResult chain = RunDetectorChain(
                null, 0f, darkElectrons, exposureUsedSeconds, Gain, seed, raw, null);

            biasLevelAdu = chain.BiasLevelAdu;
            lastCalibrationSeed = seed;
            lastCalibrationElectronsPerAdu = chain.ElectronsPerAdu;
            lastCalibrationSaturationElectrons = chain.SaturationElectrons;
            lastCalibrationDarkPerSecond = darkPerSecond;
            return raw;
        }

        /// <summary>Seed, conversion factor, saturation and dark rate of the last calibration frame -- the header fields its FITS export needs, kept apart from the science frame's own.</summary>
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
            // at -20 C and at ambient -- which is the opposite of what warming a sensor does.
            double referenceBinnedDarkPerSecond =
                Spec.DarkCurrentElectronsPerSecond * BinningFactor * BinningFactor;

            double hotMultiplier = DarkCurrentModel.HotPixelDarkMultiplier(
                referenceBinnedDarkPerSecond, Spec.MaxExposureSeconds, SaturationElectrons(isoGain));

            foreach (int idx in hotPixelIndices)
            {
                if (idx < 0 || idx >= raw.Length) continue;
                double sceneElectrons = signal != null ? signal[idx] : 0.0;   // null = shutter closed
                double mean = Math.Max(0.0, sceneElectrons + skyElectrons + darkElectrons * hotMultiplier);
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
        /// Blur from looking through the home world's own atmosphere: the site's real seeing,
        /// at the airmass and wavelength this frame is actually being taken at.
        ///
        /// This is the term that decides what a ground-based image looks like. It is NOT a small
        /// correction on top of diffraction -- for every instrument in the catalog it is three to
        /// ten times larger than the telescope's own Airy FWHM, which is precisely why the whole
        /// profession describes these telescopes as seeing-limited.
        ///
        /// Two things it must not do, both of which the previous model did:
        ///
        ///   * It must not vanish at the zenith. The old form was (airmass - 1) * k, i.e. zero
        ///     blur for anything overhead, leaving a perfectly sharp diffraction-limited disk --
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
        /// FWHM = 0.98*lambda/r0, so FWHM goes as X^(3/5) -- the relation every site-monitoring
        /// paper uses to reduce DIMM measurements to zenith.
        ///
        /// Wavelength scaling comes from the same two relations: r0 goes as lambda^(6/5), so the
        /// delivered FWHM goes as lambda^(-1/5). Modest, but real and free: the blue channel of
        /// an LRGB set is genuinely softer than the red one through the same air, which is why
        /// planetary imagers stack far more blue frames to get a usable one.
        ///
        /// An instrument with real adaptive optics (VisualTelescopeSpec.AdaptiveOpticsFwhmArcsec,
        /// e.g. SPHERE/ZIMPOL) never takes this path -- SAXO cancels the wavefront distortion in
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

            if (inputs.Stars != null && inputs.Stars.Count > 0)
            {
                SystemResponse response = inputs.Response;
                double area = inputs.ApertureAreaCm2;
                double exposure = inputs.ExposureSeconds;
                double transmission = inputs.StarNonAtmosphericTransmission * scintillation;

                drawn = StarFieldRenderer.DepositStars(
                    signal, TextureWidth, TextureHeight,
                    inputs.Stars, inputs.Projection,
                    inputs.StartMeridianRaDeg, inputs.EndMeridianRaDeg,
                    inputs.ObserverLatitudeDeg,
                    inputs.SignalCutoffElectrons,
                    star => StellarPhotometry.CollectedElectrons(
                        star.VMag, star.ColorIndexBV, response, area, exposure, transmission));
            }

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
            // rasterised lines have |slope| <= 1 and together cover every pixel exactly once --
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
            bool hasHalo = haloKernel != null && haloRadius >= 1 && coreWeight < 0.999f;

            // Convolution is linear, so a PSF that is the sum of two components can be applied as
            // the weighted sum of two convolutions -- exactly equivalent to convolving once with
            // the combined kernel, but it lets each component be sized to its own scale instead
            // of forcing the compact core to carry the halo's enormous support.
            float[] haloPlane = null;
            if (hasHalo)
            {
                haloPlane = EnsurePassScratch(n);
                Array.Copy(plane, haloPlane, n);
                FourierConvolution.Convolve(haloPlane, TextureWidth, TextureHeight, haloKernel, haloRadius);
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
        /// converted through the instrument's integrated spectral response -- aperture and
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

            Vector3d obsPos = ObservatorySite.WorldPosition(home);
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
            double fluxPerCm2PerMinute = Spec.CosmicRayEventsPerMinutePerCm2;
            double sideXCm = NativeTextureWidth * NativePixelSizeMeters * 100.0;
            double sideYCm = NativeTextureHeight * NativePixelSizeMeters * 100.0;
            double areaCm2 = sideXCm * sideYCm;
            return (float)(fluxPerCm2PerMinute * areaCm2 / 60.0);
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
        /// Peak transmission of the fitted filter. A non-positive value means the instrument's
        /// maker publishes no figure for that filter, in which case the loss is left unmodelled
        /// (1.0) rather than invented -- see VisualTelescopeSpec's own field comment.
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
                default:                  t = Spec.LuminanceFilterPeakTransmission; break;
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
        private static SystemResponse BuildSystemResponse(CameraFilter filter, double airmass)
        {
            SpectralCurve filterCurve = FilterTransmissionCurve(filter);

            // A measured curve carries the filter's own transmission, so the published peak must
            // NOT be applied on top of it -- that would count the filter twice.
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
        /// overflow in turn -- producing the familiar bloom trail through a saturated star or
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
            int hits = (int)SamplePoisson(rng, expectedHits);

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
                    float deposit = CosmicRayDepositWellFraction * (float)FullWellElectrons;
                    if (raw[i] < deposit) raw[i] = deposit;
                }
            }
        }

        /// <summary>Knuth's algorithm: exact Poisson sample, fine for the small lambda cosmic rays use.</summary>
        private static double SamplePoisson(System.Random rng, double lambda)
        {
            if (!(lambda > 0.0)) return 0.0;

            // Knuth's product method: exact, and the cheapest thing available while the mean is
            // small. It is O(lambda) and needs exp(-lambda), so it is confined to the range
            // where both are harmless -- at a mean of 150,000 electrons it would run 150,000
            // iterations per pixel against an exp() that has already underflowed to zero, and
            // never terminate.
            if (lambda < PtrsThreshold)
            {
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

            // Above that, the transformed rejection method PTRS (Hormann 1993, "The transformed
            // rejection method for generating Poisson random variables", Insurance: Mathematics
            // and Economics 12, 39). This is a genuine Poisson generator, not a normal
            // approximation: it is a rejection sampler whose accepted values are exactly Poisson
            // distributed, at constant cost independent of the mean. It is the same algorithm
            // NumPy uses above its own threshold, and therefore the one behind GalSim's and
            // Pyxel's shot noise.
            double smu = Math.Sqrt(lambda);
            double b = 0.931 + 2.53 * smu;
            double a = -0.059 + 0.02483 * b;
            double invAlpha = 1.1239 + 1.1328 / (b - 3.4);
            double vr = 0.9277 - 3.6224 / (b - 2.0);

            while (true)
            {
                double u = rng.NextDouble() - 0.5;
                double v = rng.NextDouble();
                double us = 0.5 - Math.Abs(u);

                double k = Math.Floor((2.0 * a / us + b) * u + lambda + 0.43);
                if (us >= 0.07 && v <= vr) return k;
                if (k < 0.0 || (us < 0.013 && v > us)) continue;

                if (Math.Log(v * invAlpha / (a / (us * us) + b))
                    <= k * Math.Log(lambda) - lambda - LogGamma(k + 1.0))
                    return k;
            }
        }

        /// <summary>Mean above which SamplePoisson switches from Knuth's method to PTRS. Hormann's own paper recommends 10; NumPy uses the same value.</summary>
        private const double PtrsThreshold = 10.0;

        /// <summary>
        /// log(Gamma(x)) for x &gt; 0, by the Lanczos approximation (Lanczos 1964, "A precision
        /// approximation of the gamma function", SIAM J. Numer. Anal. B 1, 86) with the g=7,
        /// n=9 coefficient set. Accurate to about 15 significant digits over the range PTRS
        /// needs, which is the factorial term of the Poisson mass function.
        /// </summary>
        private static double LogGamma(double x)
        {
            double sum = LanczosCoefficients[0];
            for (int i = 1; i < LanczosCoefficients.Length; i++) sum += LanczosCoefficients[i] / (x + i - 1.0);

            double t = x + 6.5; // x + g - 0.5, with g = 7
            return 0.5 * Math.Log(2.0 * Math.PI) + (x - 0.5) * Math.Log(t) - t + Math.Log(sum);
        }

        private static readonly double[] LanczosCoefficients =
        {
            0.99999999999980993, 676.5203681218851, -1259.1392167224028,
            771.32342877765313, -176.61502916214059, 12.507343278686905,
            -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7
        };

        /// <summary>
        /// Third-order astigmatism: transverse blur scaling with the square of the normalized
        /// field radius, smeared radially outward from frame center -- a simplified stand-in
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
            // The defect map is a property of this particular piece of silicon, so it is drawn from
            // a fixed "serial number" seed and is the same every session. On its own stream, and on
            // Pcg32 rather than System.Random, so that it is also the same on every machine and
            // every .NET runtime -- a bad pixel map that moved between platforms would make any
            // reference frame unusable as a comparison.
            const long SensorSerialSeed = 20260721L;
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
        {
            if (sigma <= 0f) return 0f;
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return (float)(z * sigma);
        }

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
            lastCaptureSnapshot = null;
            hasLockedAim = false;
        }
    }
}
