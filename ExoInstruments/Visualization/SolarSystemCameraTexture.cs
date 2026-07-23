using System;
using System.Linq;
using UnityEngine;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>Filter-wheel positions. A mono CCD shoots one filter at a time, so each is its own grayscale frame -- the filter just selects which of the rendered scene's channels (and how much throughput) forms the signal.</summary>
    public enum CameraFilter
    {
        Luminance, // clear/L: full luminance, maximum throughput
        Red,
        Green,
        Blue,
        HAlpha     // narrowband: red channel only, low throughput (needs longer exposure)
    }

    /// <summary>
    /// RC20 astrograph camera: clones KSP's scaled-space and galaxy cameras and
    /// points them at a solar-system body from KSC. Same technique as Tarsier Space
    /// Technology's TSTCameraModule, reimplemented here to avoid the dependency.
    /// Outputs a monochrome, noisy "single raw CCD frame" through a full physics
    /// pipeline (extinction, shot noise, seeing, EVE cloud cover, moon scattering,
    /// persistence ghosting, cosmic rays, full-well blooming, charge-transfer smear,
    /// astigmatism) -- see ProcessFrame for the per-effect citations.
    /// </summary>
    public class SolarSystemCameraTexture : IDisposable
    {
        public const int TextureWidth = 480;
        public const int TextureHeight = 480;

        private const string GalaxyCameraName = "GalaxyCamera";
        private const string ScaledSpaceCameraName = "Camera ScaledSpace";

        // Linear shutter gain per second — longer exposure lifts faint areas but blows highlights faster.
        private const float ExposureGainPerSecond = 12.0f;

        // FOV limits. 0.08 deg matches a Barlow-equipped RC20 at high power; 8 deg is wide field.
        public const float MinFovDeg = 0.08f;
        public const float MaxFovDeg = 8.0f;

        public const float MinExposureSeconds = 0.05f;
        public const float MaxExposureSeconds = 30.0f;

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
        private const float ReadNoiseSigmaValue = 0.02f;                // NOT amplified by gain (applied after the analog stage)

        // Reference flux for a full Mün at zenith: albedo * (radius/distance)^2.
        private const double MunReferenceFluxUnits = 0.12 * (200000.0 / 12000000.0) * (200000.0 / 12000000.0);

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
        // Through Matter" review; Grieder 2001, "Cosmic Rays at Earth"). Sensor area uses the
        // ZWO ASI294MM Pro's published 4.63 um pixel pitch (a real, commercially available
        // monochrome astronomy camera) applied to the 480x480 frame: side = 480*4.63e-4 cm =
        // 0.2222 cm, area = 0.04939 cm^2. Rate = 1 cm^-2 min^-1 * 0.04939 cm^2 / 60 s =
        // 8.23e-4 hits/s -- genuinely rare for a short amateur exposure, matching real amateur
        // imaging experience (cosmic ray hits are a long-exposure/large-sensor phenomenon).
        private const float CosmicRayHitsPerSecond = 8.23e-4f;
        private const int CosmicRayMinTrackPx = 2;
        private const int CosmicRayMaxTrackPx = 14;
        private const float CosmicRayDepositValue = 0.85f;

        // Persistence/ghosting: real default trap time constants and species proportions from
        // Pyxel's persistence model (pyxel/models/charge_collection/persistence.py), which are
        // themselves in the spirit of Fixsen, Offenberg, Hanisch et al. (2000, PASP 112, 1350)
        // for HgCdTe-style trap persistence. Pyxel's own model uses ONE time constant per
        // species for both capture (approach to an equilibrium fill level) and release, so this
        // does the same rather than inventing a separate capture-rate parameter: each species
        // relaxes exponentially toward an equilibrium level set by the current signal and that
        // species' share of total trap capacity (PersistenceProportions), using the same
        // Q(t)=Q0*exp(-t/tau) relation as the release side. Real time between two RC20 shots
        // can be minutes, where Pyxel's own small-step linearized form would blow up, so the
        // true exponential is used throughout instead of their per-tick linear approximation.
        private static readonly float[] PersistenceTauSeconds = { 1f, 10f, 100f, 1000f, 10000f };
        private static readonly float[] PersistenceProportions = { 0.307f, 0.175f, 0.188f, 0.136f, 0.194f };

        // Astigmatism: for a true Ritchey-Chretien (what the RC20 is, per Observatories.cs),
        // third-order coma is corrected to zero by the RC hyperbolic-mirror design itself --
        // that is the entire reason the RC form exists (Ritchey & Chretien 1922). The dominant
        // remaining off-axis third-order (Seidel) aberration for this telescope class is
        // astigmatism, whose transverse blur scales with the SQUARE of the field angle (Seidel
        // aberration theory: S_II/coma scales linearly with field, S_III/astigmatism
        // quadratically -- see Schroeder, "Astronomical Optics" 2nd ed. 2000, Ch. 6, or Rutten &
        // van Venrooij, "Telescope Optics"), directed radially outward from the optical axis.
        // The absolute amplitude depends on the telescope's actual optical prescription (focal
        // ratio, field curvature radius), which no published PlaneWave RC20 datasheet specifies
        // to the precision an aberration coefficient would need -- the radial-quadratic FORM is
        // the literature-sourced part; the pixel amplitude at the frame corner is a display
        // calibration, not a measured quantity, same category as e.g. ExposureGainPerSecond.
        private const float AstigmatismStrengthPxAtCorner = 3.0f;

        private bool builtOnce;
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
        private float[] rawScratch;
        private float[] astigmatismScratch;

        // Persistence trap state, one array per species (see PersistenceTauSeconds), surviving
        // between exposures so a bright target leaves a fading ghost in the next shot.
        private float[][] persistenceTraps;
        private double lastPersistenceUt = double.NaN;

        private Renderer[] skyboxRenderers;
        private ScaledSpaceFader[] scaledSpaceFaders;

        // Fixed hot/dead pixel map: a chip's defect pattern is persistent, so seeded
        // once from a constant, never from the target or UT.
        private int[] hotPixelIndices;
        private int[] deadPixelIndices;

        // RC20's real continuous gain control (replaces the old ISO-step abstraction).
        // 0.7 is the hardware minimum; 8.0 matches the old ISO-800 noise ceiling.
        public const float MinGain = 0.7f;
        public const float MaxGain = 8.0f;
        /// <summary>Field of view in degrees (zoom). Clamped to [MinFovDeg, MaxFovDeg].</summary>
        public float FovDeg { get; set; } = 3.0f;
        /// <summary>Exposure time in seconds. Drives brightness, noise, drift trailing, and how long BeginExposure takes.</summary>
        public float ExposureSeconds { get; set; } = 0.5f;
        /// <summary>When true, the frame is always sharp; when false, FocusOffset controls defocus blur.</summary>
        public bool Autofocus { get; set; } = true;
        /// <summary>Manual focus, [-1, 1]. 0 = sharp, magnitude = amount of defocus blur. Ignored when Autofocus is on.</summary>
        public float FocusOffset { get; set; } = 0f;
        /// <summary>Selected filter-wheel position.</summary>
        public CameraFilter Filter { get; set; } = CameraFilter.Luminance;
        /// <summary>Sensor gain, [MinGain, MaxGain]: higher = brighter + noisier.</summary>
        public float Gain { get; set; } = 1.0f;
        /// <summary>When true the mount tracks the sky (no drift). Off by default — a bare RC20 has no autoguider.</summary>
        public bool Autoguiding { get; set; } = false;

        // --- Timed-exposure capture state ----------------------------------
        private bool isCapturing;
        private float captureElapsed;
        private float captureDuration;
        private CelestialBody pendingTarget;

        /// <summary>True while a timed exposure is integrating (between BeginExposure and completion).</summary>
        public bool IsCapturing => isCapturing;
        /// <summary>0..1 progress through the current timed exposure.</summary>
        public float CaptureProgress => isCapturing && captureDuration > 0f ? Mathf.Clamp01(captureElapsed / captureDuration) : 0f;
        /// <summary>True once a timed exposure has completed and a finished photo is available.</summary>
        public bool HasCapturedPhoto { get; private set; }

        private Color[] lastCaptureSnapshot;

        /// <summary>False only if KSP's own scaled-space/galaxy cameras can't be found (should not happen on a stock install).</summary>
        public bool IsAvailable
        {
            get
            {
                if (!builtOnce) EnsureSceneBuilt();
                return available;
            }
        }

        /// <summary>The finished, timed-exposure photo (frozen at exposure completion), or null before the first BeginExposure completes.</summary>
        public Texture2D CapturedPhoto => capturedTexture;

        /// <summary>Last capture as a grayscale float[] (row-major, y-down), for AstroImageStack. Null before the first capture. Fresh copy every call.</summary>
        public float[] GetLastCaptureGray()
        {
            if (lastCaptureSnapshot == null) return null;
            var gray = new float[lastCaptureSnapshot.Length];
            for (int i = 0; i < gray.Length; i++) gray[i] = lastCaptureSnapshot[i].r;
            return gray;
        }

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
        /// Advances an in-progress timed exposure by deltaTime real seconds. When
        /// the exposure completes, renders the target once with the committed
        /// settings and freezes it as CapturedPhoto. No-op when not capturing.
        /// </summary>
        public void TickCapture(float deltaTime)
        {
            if (!isCapturing) return;
            captureElapsed += deltaTime;
            if (captureElapsed < captureDuration) return;

            isCapturing = false;
            RenderExposure(pendingTarget);
            lastCaptureSnapshot = (Color[])pixelScratch.Clone();

            if (outputTexture == null) return;
            if (capturedTexture == null)
            {
                capturedTexture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGB24, false);
            }
            capturedTexture.SetPixels(outputTexture.GetPixels());
            capturedTexture.Apply();
            HasCapturedPhoto = true;
        }

        /// <summary>Renders the target into readbackTexture, then processes it (filter/exposure/noise/focus/drift) into outputTexture.</summary>
        private void RenderExposure(CelestialBody targetBody)
        {
            if (targetBody == null) return;
            // Without autoguiding, the aim stays locked at the last UpdateAim position —
            // which is how the body drifts off-center if it moved since then.
            if (Autoguiding) UpdateAim(targetBody);
            RenderScene(targetBody);
            ProcessFrame(targetBody.flightGlobalsIndex, targetBody);
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

            RenderTexture activeRT = RenderTexture.active;
            RenderTexture.active = renderTexture;

            float fov = Mathf.Clamp(FovDeg, MinFovDeg, MaxFovDeg);
            AimCamera(galaxyCam, GalaxyCameraName, camPos, look, fov);
            galaxyCam.Render();

            // Force every body's scaled stand-in visible — KSP fades them by real-camera
            // distance, which has nothing to do with where our clone points.
            // Home body excepted: its stand-in would wrap the camera.
            foreach (ScaledSpaceFader fader in scaledSpaceFaders)
            {
                if (fader == null || fader.r == null) continue;
                if (home != null && fader.celestialBody == home) continue;
                fader.r.enabled = true;
            }

            // The matrix resets are critical: KSP's ScaledSpace camera carries a custom
            // view/projection matrix that CopyFrom inherits and silently overrides our transform.
            // Resetting them makes the clone's own transform authoritative.
            AimCamera(scaledSpaceCam, ScaledSpaceCameraName, camPos, look, fov);
            scaledSpaceCam.ResetWorldToCameraMatrix();
            scaledSpaceCam.ResetProjectionMatrix();
            scaledSpaceCam.clearFlags = CameraClearFlags.Depth;
            scaledSpaceCam.farClipPlane = 3e15f;
            scaledSpaceCam.Render();

            readbackTexture.ReadPixels(new Rect(0, 0, TextureWidth, TextureHeight), 0, 0);
            readbackTexture.Apply();
            RenderTexture.active = activeRT;
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
            if (builtOnce) return;
            builtOnce = true;

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
                renderTexture.Create();

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

        /// <summary>
        /// Converts the raw rendered frame into a monochrome CCD-frame look through the
        /// physics pipeline: filter throughput, atmospheric extinction, EVE cloud cover,
        /// sky glow (twilight + moon scattering + airglow + zodiacal), scintillation,
        /// shot/dark/read noise, persistence ghosting, cosmic ray hits, full-well blooming,
        /// charge-transfer smear, hot/dead pixels, optional drift trail, and combined
        /// defocus/seeing/astigmatism blur.
        /// </summary>
        private void ProcessFrame(int targetSeed, CelestialBody targetBody)
        {
            Color[] src = readbackTexture.GetPixels();
            EnsureDefectMap();

            int n = TextureWidth * TextureHeight;
            if (rawScratch == null || rawScratch.Length != n) rawScratch = new float[n];

            float exposureSeconds = Mathf.Clamp(ExposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            float isoGain = Mathf.Clamp(Gain, MinGain, MaxGain);
            float filterThroughput = FilterThroughput(Filter);

            double ut = Planetarium.GetUniversalTime();

            TryComputeAltitudeDeg(targetBody, out double targetAltDeg);
            double airmass = targetAltDeg > 0.0 ? ImagingObservingConditions.AirmassAt(targetAltDeg) : double.PositiveInfinity;
            float extinction = (float)AtmosphericImagingNoise.ExtinctionTransmission(airmass);

            double scintSigma = AtmosphericImagingNoise.ScintillationExcessSigma(
                Observatories.Rc20.ApertureMeters, Observatories.Rc20.SiteAltitudeMeters, airmass, exposureSeconds);

            bool haveSunAlt = TryComputeAltitudeDeg(Planetarium.fetch != null ? Planetarium.fetch.Sun : null, out double sunAltDeg);
            double twilightRamp = haveSunAlt
                ? Clamp01((sunAltDeg - AstronomicalTwilightSunAltitudeDeg) / (ImagingObservingConditions.TwilightSunAltitudeDeg - AstronomicalTwilightSunAltitudeDeg))
                : 0.0;
            double moonSkyExcess = ComputeMoonSkyExcess(targetBody);
            float skyBackground = (float)((TwilightSkyBackgroundRatePerSecond * twilightRamp
                                          + MoonGlowRatePerSecond * moonSkyExcess
                                          + AirglowBaselinePerSecond
                                          + ZodiacalBaselineRatePerSecond) * exposureSeconds * filterThroughput);

            // Single EVE sample for the whole frame: at 0.08–8 deg FOV, the cloud ray
            // barely moves from KSC's own position, so per-pixel variation would be invented.
            float coverage = ComputeCloudCoverage();

            AtmosphericImagingNoise.DarkCurrent(exposureSeconds, out double darkPedestalD, out double darkSigmaD);
            float darkPedestal = (float)darkPedestalD;
            float darkSigma = (float)darkSigmaD;

            // New RNG seed every exposure — read noise differs shot to shot, unlike the fixed defect map.
            System.Random rng = new System.Random(unchecked(targetSeed * 9973 + (int)(ut * 997.0) + 17));
            float scintJitter = 1f + NextGaussian(rng, (float)scintSigma);

            float cloudTransmission = 1f - coverage * (float)CloudMaxAttenuation;
            float haze = coverage * (float)CloudHazeRatePerSecond * exposureSeconds * filterThroughput;
            float exposureBase = exposureSeconds * ExposureGainPerSecond * filterThroughput * extinction * scintJitter * cloudTransmission;

            for (int py = 0; py < TextureHeight; py++)
            {
                int row = py * TextureWidth;
                for (int px = 0; px < TextureWidth; px++)
                {
                    int i = row + px;

                    float signal = FilterSignal(src[i], Filter);
                    float photon = signal * exposureBase;
                    float totalPhoton = photon + haze + skyBackground;

                    float shotSigma = (float)AtmosphericImagingNoise.ShotNoiseSigma(totalPhoton);
                    float combinedPreGainSigma = Mathf.Sqrt(shotSigma * shotSigma + darkSigma * darkSigma);

                    float preGainValue = totalPhoton + darkPedestal + NextGaussian(rng, combinedPreGainSigma);
                    float postGain = preGainValue * isoGain + NextGaussian(rng, ReadNoiseSigmaValue);

                    // Left unclamped here — blooming/CTI below need to see genuine
                    // above-full-well values before the sensor's own clipping applies.
                    rawScratch[i] = postGain;
                }
            }

            ApplyPersistence(rawScratch, ut);
            ApplyCosmicRays(rawScratch, exposureSeconds, rng);
            ApplyBlooming(rawScratch);
            ApplyChargeTransferSmear(rawScratch);

            for (int i = 0; i < n; i++)
            {
                float value = Mathf.Clamp01(rawScratch[i]);
                pixelScratch[i] = new Color(value, value, value, 1f);
            }

            // Diurnal drift: 360 deg per Kerbin rotation, converted to pixels by the FOV.
            if (!Autoguiding)
            {
                int driftPx = ComputeDriftPixels(exposureSeconds, targetBody);
                if (driftPx >= 1) ApplyHorizontalMotionBlur(pixelScratch, driftPx);
            }

            // Defocus + seeing + cloud haze all go through one blur pass to avoid double-blurring.
            float defocusBlur = Autofocus ? 0f : Mathf.Abs(FocusOffset) * MaxDefocusBlurPx;
            float seeingBlur = ComputeSeeingBlurPx(targetBody);
            float cloudBlur = coverage * CloudBlurPxMax;
            float blurRadius = defocusBlur + seeingBlur + cloudBlur;
            if (blurRadius >= 0.5f)
            {
                ApplyBoxBlur(pixelScratch, Mathf.RoundToInt(blurRadius));
            }

            // Field-dependent astigmatism, applied after the uniform blur so it reads as a distinct
            // off-axis smear rather than blending into the seeing/defocus radius.
            ApplyAstigmatismBlur(pixelScratch);

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
                pixelScratch[idx] = new Color(v, v, v, 1f);
            }
            foreach (int idx in deadPixelIndices)
            {
                pixelScratch[idx] = new Color(0f, 0f, 0f, 1f);
            }

            outputTexture.SetPixels(pixelScratch);
            outputTexture.Apply();
        }

        /// <summary>Altitude of a live body above KSC's horizon. Returns false if the home body or the body itself is unavailable.</summary>
        private static bool TryComputeAltitudeDeg(CelestialBody body, out double altDeg)
        {
            altDeg = 0.0;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || body == null) return false;

            Vector3d obsPos = home.GetWorldSurfacePosition(SkyCoordinates.KscLatitudeDeg, SkyCoordinates.KscLongitudeDeg, 100.0);
            Vector3d up = (obsPos - home.position).normalized;
            Vector3d toBody = (body.position - obsPos).normalized;
            altDeg = 90.0 - Vector3d.Angle(up, toBody);
            return true;
        }

        /// <summary>Blur from looking through Kerbin's own atmosphere — grows with airmass, sharply worse near the horizon.</summary>
        private float ComputeSeeingBlurPx(CelestialBody targetBody)
        {
            if (!TryComputeAltitudeDeg(targetBody, out double altDeg)) return 0f;
            if (altDeg <= 0.0) return MaxSeeingBlurPx; // shouldn't be capturable this low, but cap defensively

            double airmass = ImagingObservingConditions.AirmassAt(altDeg);
            if (double.IsInfinity(airmass) || double.IsNaN(airmass)) return MaxSeeingBlurPx;

            float excess = Mathf.Max(0f, (float)airmass - 1f);
            return Mathf.Min(MaxSeeingBlurPx, excess * SeeingBlurPxPerAirmass);
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

            Vector3d obsPos = home.GetWorldSurfacePosition(SkyCoordinates.KscLatitudeDeg, SkyCoordinates.KscLongitudeDeg, 100.0);
            Vector3d toTarget = targetBody != null ? (targetBody.position - obsPos) : Vector3d.zero;
            bool haveTarget = toTarget.sqrMagnitude > 1e-6;
            if (haveTarget) toTarget = toTarget.normalized;

            double kernelAtReference = MoonlightPollution.ScatteringKernel(30.0);
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

                total += (moonFlux / MunReferenceFluxUnits) * altitudeRamp * scatterWeight;
            }
            return total;
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

        /// <summary>
        /// Persistence ghosting: each trap species relaxes exponentially toward an equilibrium
        /// fill level set by the current signal and that species' share of trap capacity
        /// (PersistenceProportions), using the same real time constant (PersistenceTauSeconds)
        /// for both directions -- Pyxel's own persistence model uses one tau per species for
        /// both capture and release, so this does too rather than inventing a separate capture
        /// rate. When the equilibrium is above the trap's current level the trap fills (capture,
        /// pulling charge out of the pixel); when a previously bright frame's trap sits above the
        /// new, dimmer equilibrium it drains back into the pixel (release) -- the mechanism
        /// behind a fading afterimage bleeding into the next shot. A true exponential relaxation
        /// is used rather than Pyxel's own small-step linear form, since a real UT gap between
        /// two RC20 shots can be minutes rather than a simulation tick; it is also naturally
        /// bounded (converges toward the equilibrium at rate dt/tau), so unlike a fixed-rate
        /// capture with no cap, a fast capture batch can't accumulate an ever-growing ghost.
        /// </summary>
        private void ApplyPersistence(float[] raw, double ut)
        {
            int n = TextureWidth * TextureHeight;
            if (persistenceTraps == null)
            {
                persistenceTraps = new float[PersistenceTauSeconds.Length][];
                for (int k = 0; k < persistenceTraps.Length; k++) persistenceTraps[k] = new float[n];
                lastPersistenceUt = ut;
                return; // nothing accumulated yet on the very first exposure
            }

            double dt = Math.Max(0.0, ut - lastPersistenceUt);
            lastPersistenceUt = ut;

            for (int k = 0; k < persistenceTraps.Length; k++)
            {
                float[] trap = persistenceTraps[k];
                float approach = (float)(1.0 - Math.Exp(-dt / PersistenceTauSeconds[k]));
                float proportion = PersistenceProportions[k];
                for (int i = 0; i < n; i++)
                {
                    float equilibrium = raw[i] * proportion;
                    float delta = (equilibrium - trap[i]) * approach; // positive = net capture, negative = net release
                    trap[i] += delta;
                    raw[i] -= delta;
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

            Vector3d obsPos = home.GetWorldSurfacePosition(SkyCoordinates.KscLatitudeDeg, SkyCoordinates.KscLongitudeDeg, 100.0);
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

        /// <summary>Relative throughput per filter — luminance passes most, H-alpha least.</summary>
        private static float FilterThroughput(CameraFilter filter)
        {
            switch (filter)
            {
                case CameraFilter.Luminance: return 1.0f;
                case CameraFilter.Red:       return 0.5f;
                case CameraFilter.Green:     return 0.55f;
                case CameraFilter.Blue:      return 0.45f;
                case CameraFilter.HAlpha:    return 0.12f;
                default:                     return 1.0f;
            }
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
        private void ApplyHorizontalMotionBlur(Color[] buffer, int length)
        {
            if (length < 1) return;
            if (blurScratch == null || blurScratch.Length != buffer.Length)
                blurScratch = new Color[buffer.Length];
            int w = TextureWidth, h = TextureHeight;
            float inv = 1f / (length + 1);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int dx = 0; dx <= length; dx++)
                    {
                        int sx = Mathf.Clamp(x - dx, 0, w - 1);
                        sum += buffer[row + sx].r;
                    }
                    float v = sum * inv;
                    blurScratch[row + x] = new Color(v, v, v, 1f);
                }
            }
            Array.Copy(blurScratch, buffer, buffer.Length);
        }

        /// <summary>Separable box blur (horizontal then vertical), radius in pixels.</summary>
        private void ApplyBoxBlur(Color[] buffer, int radius)
        {
            if (radius < 1) return;
            if (blurScratch == null || blurScratch.Length != buffer.Length)
            {
                blurScratch = new Color[buffer.Length];
            }
            int w = TextureWidth, h = TextureHeight;
            float inv = 1f / (2 * radius + 1);

            // Horizontal pass: buffer -> blurScratch
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = Mathf.Clamp(x + dx, 0, w - 1);
                        sum += buffer[row + sx].r;
                    }
                    float v = sum * inv;
                    blurScratch[row + x] = new Color(v, v, v, 1f);
                }
            }
            // Vertical pass: blurScratch -> buffer
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    float sum = 0f;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int sy = Mathf.Clamp(y + dy, 0, h - 1);
                        sum += blurScratch[sy * w + x].r;
                    }
                    float v = sum * inv;
                    buffer[y * w + x] = new Color(v, v, v, 1f);
                }
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
            if (outputTexture != null) { UnityEngine.Object.Destroy(outputTexture); outputTexture = null; }
            if (capturedTexture != null) { UnityEngine.Object.Destroy(capturedTexture); capturedTexture = null; }
            galaxyCam = null;
            scaledSpaceCam = null;
        }
    }
}
