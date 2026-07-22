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
    /// A ground-based "amateur astrophotography" camera: a real live-rendered
    /// photo of a solar-system body, taken by cloning KSP's own scaled-space and
    /// galaxy-skybox cameras (the same technique Tarsier Space Technology uses
    /// for its telescope parts -- see its TSTCameraModule -- reimplemented here
    /// so this mod has no dependency on it) and pointing the clone at the
    /// target from the observatory's fixed KSC location.
    ///
    /// Deliberately does NOT clone KSP's near-range camera ("Camera 00"), unlike
    /// a vessel-mounted camera would need to: every solar-system target is, by
    /// definition, far enough from a fixed KSC site to already be rendered
    /// through KSP's own scaled-space stand-ins rather than full-detail nearby
    /// geometry. That is a real limitation (no crater-level Mun close-ups from
    /// the ground) but also an honest one: a real backyard telescope doesn't
    /// resolve craters either, only a spacecraft in near orbit does. What the
    /// scaled-space camera *does* give for free, correctly, or free: real
    /// current position, phase (day/night terminator), relative brightness, and
    /// axial tilt/rotation of every body, and the real background starfield
    /// behind it.
    ///
    /// The color frame is then desaturated and grained on the CPU (same
    /// Texture2D-pixel-buffer idiom as every other display in this mod) into a
    /// monochrome, noisy "real sensor" look -- the RC20-astrograph aesthetic
    /// Baptiste asked for -- rather than the clean color preview Tarsier shows.
    /// </summary>
    public class SolarSystemCameraTexture : IDisposable
    {
        public const int TextureWidth = 480;
        public const int TextureHeight = 480;

        private const string GalaxyCameraName = "GalaxyCamera";
        private const string ScaledSpaceCameraName = "Camera ScaledSpace";

        // Linear "shutter" gain per second of exposure. A real single, unstacked
        // exposure off a narrow-dynamic-range CCD has no forgiveness: the lit
        // terminator blows straight to white while the shadow side crushes to
        // black, nothing gently rolled off. Longer exposure => proportionally
        // more light => the highlights blow out sooner and the faint side lifts.
        private const float ExposureGainPerSecond = 12.0f;

        // Zoom / field-of-view limits. Smaller FOV = more zoom (longer focal
        // length). ~3 deg frames the Mun as a comfortable disc; ~8 deg is a wide
        // field showing several bodies' worth of sky. The old 0.5 deg floor was
        // unrealistically wide for an RC20: real planetary imaging with this
        // class of scope runs a Barlow/Powermate behind the native f/8, pushing
        // effective focal length (and so max zoom) several times higher --
        // 0.08 deg matches that kind of high-power planetary rig instead of
        // just the bare native focal length.
        public const float MinFovDeg = 0.08f;
        public const float MaxFovDeg = 8.0f;

        public const float MinExposureSeconds = 0.05f;
        public const float MaxExposureSeconds = 30.0f;

        // Manual-focus blur: |FocusOffset| of 1 is fully racked out of focus.
        private const float MaxDefocusBlurPx = 7.0f;

        // Atmospheric seeing (looking up through KSC's own sky): blur per unit
        // of airmass above zenith, capped so a very-low target never turns to
        // total mush.
        private const float SeeingBlurPxPerAirmass = 1.4f;
        private const float MaxSeeingBlurPx = 6.0f;

        // --- Atmosphere / sky-glow / cloud tuning (see AtmosphericImagingNoise
        //     for the reusable physics; these constants calibrate that physics
        //     to THIS renderer's existing brightness scale, same reasoning as
        //     ExposureGainPerSecond above) --------------------------------------
        // Sky brightness never truly reaches astronomical darkness in this
        // model until well past the -12 deg nautical-twilight capture cutoff
        // (ImagingObservingConditions.TwilightSunAltitudeDeg) -- the ramp
        // bottoms out at -18 (astronomical twilight), matching real practice.
        private const double AstronomicalTwilightSunAltitudeDeg = -18.0;
        private const double TwilightSkyBackgroundRatePerSecond = 0.30; // at the -12 deg capture threshold
        private const double MoonGlowRatePerSecond = 0.02;              // a full Mün at zenith
        private const double AirglowBaselinePerSecond = 0.004;          // faint natural night-sky glow, always present
        private const double CloudMaxAttenuation = 0.85;                // thick cloud blocks up to 85% of the target's light, never fully
        private const double CloudHazeRatePerSecond = 0.25;             // scattered light off cloud base -- the "veiling glow" real astrophotographers hate
        private const float CloudBlurPxMax = 2.0f;                      // thin cloud/haze also softens the frame a little
        private const float ReadNoiseSigmaValue = 0.02f;                // fixed electronic noise floor, NOT amplified by gain (real sensors read this after the analog gain stage)

        // Reference used to normalize a moon's glow contribution to "1.0 unit"
        // for a full Mün at zenith: albedo * (radius/distance)^2, same shape
        // MoonlightPollution's photometric reference uses.
        private const double MunReferenceFluxUnits = 0.12 * (200000.0 / 12000000.0) * (200000.0 / 12000000.0);

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

        private Renderer[] skyboxRenderers;
        private ScaledSpaceFader[] scaledSpaceFaders;

        // Fixed sensor defect map (hot/dead pixels): a real CCD/CMOS's defect
        // pattern is a persistent physical property of THAT chip, not
        // something that changes between shots or targets -- so this is
        // seeded once from a constant, never from targetSeed or UT.
        private int[] hotPixelIndices;
        private int[] deadPixelIndices;

        // Sensor gain: brighter but noisier, exactly like a real camera's gain
        // setting (this replaces the old ISO-step abstraction with the RC20's
        // actual continuous gain control). 0.7 is the RC20's real minimum gain
        // per Peter -- it corresponds to an extremely low ISO -- and 8.0 keeps
        // the same noisy ceiling the old ISO-800 step had.
        public const float MinGain = 0.7f;
        public const float MaxGain = 8.0f;

        // --- User-adjustable settings (a robotic scope: nothing renders until
        //     you commit these and press Capture) ----------------------------
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
        public float Gain { get; set; } = 1.0f; // unity gain -- the middle-ground default
        /// <summary>When true, the mount tracks the sky during the exposure (no drift trailing). When false, diurnal drift smears the frame proportional to exposure time.</summary>
        // Off by default: a bare RC20 has no autoguider. Real tracking is a
        // career-progression upgrade to unlock later (see memory), not a free
        // starting capability.
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

        /// <summary>
        /// Starts a timed exposure of ExposureSeconds on targetBody. Nothing is
        /// rendered until the exposure completes (a robotic scope shows no live
        /// preview -- you commit the settings, expose, then the frame appears).
        /// </summary>
        public void BeginExposure(CelestialBody targetBody)
        {
            if (!IsAvailable) return;
            pendingTarget = targetBody;
            isCapturing = true;
            captureElapsed = 0f;
            captureDuration = Mathf.Clamp(ExposureSeconds, MinExposureSeconds, MaxExposureSeconds);
            HasCapturedPhoto = false;
        }

        /// <summary>Drops any finished photo and cancels any in-progress exposure (e.g. after switching target). Also releases the locked aim, so the next shot on the new target starts centered.</summary>
        public void DiscardCapturedPhoto()
        {
            HasCapturedPhoto = false;
            isCapturing = false;
            hasLockedAim = false;
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

            if (outputTexture == null) return;
            if (capturedTexture == null)
            {
                capturedTexture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGB24, false);
            }
            capturedTexture.SetPixels(outputTexture.GetPixels());
            capturedTexture.Apply();
            HasCapturedPhoto = true;
        }

        /// <summary>Renders the target scene into readbackTexture, then processes it (filter/exposure/ISO/noise/focus/drift) into outputTexture.</summary>
        private void RenderExposure(CelestialBody targetBody)
        {
            if (targetBody == null) return;
            // Autoguiding re-centers on the real body position before every
            // shot -- that's what tracking means. Without it, RenderScene keeps
            // using whatever aim was last locked (see UpdateAim/hasLockedAim),
            // which is exactly how the body ends up off-center if it moved
            // since the last update.
            if (Autoguiding) UpdateAim(targetBody);
            RenderScene(targetBody);
            ProcessFrame(targetBody.flightGlobalsIndex, targetBody);
        }

        private bool hasLockedAim;
        private Vector3 lockedCamPos;
        private Quaternion lockedLook;

        /// <summary>
        /// Recomputes and stores the camera's aim (position + look direction)
        /// from targetBody's CURRENT live position. This is the only place the
        /// aim actually changes -- a capture always renders through whatever
        /// aim is currently locked, not a freshly-recomputed one, so a body
        /// that moves between two exposures without the aim being refreshed
        /// (no autoguiding, no manual re-center) genuinely drifts off-center in
        /// the second photo, the way an untracked real mount would.
        /// </summary>
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
        /// Renders one frame through the CURRENTLY LOCKED aim (see UpdateAim)
        /// into readbackTexture (raw color). Called once, at exposure
        /// completion -- there is no live preview.
        ///
        /// Everything here is done in KSP's SCALED-SPACE frame, using the game's
        /// own scaledBody transforms -- not the flight/local world frame -- so
        /// there is no hand-rolled coordinate conversion to get wrong. Every
        /// celestial body has a persistent miniature "scaled" stand-in
        /// (body.scaledBody), and KSP's own "Camera ScaledSpace" already lives
        /// in that frame at the observer's position; we clone it and only
        /// change where it looks.
        /// </summary>
        private void RenderScene(CelestialBody targetBody)
        {
            if (targetBody == null || !IsAvailable) return;
            if (targetBody.scaledBody == null) return; // no scaled stand-in -- nothing to frame
            if (!hasLockedAim) UpdateAim(targetBody); // first-ever shot on this target: always start centered

            CelestialBody home = FlightGlobals.GetHomeBody();
            Vector3 camPos = lockedCamPos;
            Quaternion look = lockedLook;

            // NOTE: deliberately NOT touching Sun.Instance's rotation the way
            // Tarsier's TSTCameraModule.draw() does. That mutates a GLOBAL
            // object which also drives the real visible scene's lighting --
            // even with an immediate restore, per-frame mutation of shared
            // state leaked a visible color shift into the background (see
            // Baptiste's report). It's also unnecessary here: at Mun's
            // distance from Kerbin (~12,000 km) vs. Kerbin's distance from the
            // Sun (~13.6 billion km), the real parallax in the Sun's apparent
            // direction is ~0.05 deg -- invisible. The earlier "fully lit"
            // look was very likely just Mun's actual orbital phase at that
            // moment, not a lighting bug. This may need revisiting for bodies
            // where the parallax is NOT negligible (e.g. Duna, Jool), but only
            // with a technique that can't bleed into the live game view.

            RenderTexture activeRT = RenderTexture.active;
            RenderTexture.active = renderTexture;

            // Galaxy skybox first -- establishes the black background + faint
            // background stars the scaled bodies then composite on top of.
            float fov = Mathf.Clamp(FovDeg, MinFovDeg, MaxFovDeg);
            AimCamera(galaxyCam, GalaxyCameraName, camPos, look, fov);
            galaxyCam.Render();

            // Force every OTHER body's miniature scaled-space stand-in visible.
            // KSP fades each one in/out based on the REAL camera's distance,
            // which has nothing to do with where our clone is pointed, so a
            // target that's currently faded out would otherwise be missing. The
            // home body is the deliberate exception: we're effectively standing
            // at its scaled position, so its stand-in would just wrap the camera.
            foreach (ScaledSpaceFader fader in scaledSpaceFaders)
            {
                if (fader == null || fader.r == null) continue;
                if (home != null && fader.celestialBody == home) continue;
                fader.r.enabled = true;
            }

            // Scaled bodies composited over the galaxy (clear depth only).
            // The two matrix resets are essential and were the whole bug: KSP's
            // ScaledSpace camera carries a custom view/projection matrix that
            // CopyFrom inherits, and it silently overrode our transform so the
            // clone kept looking wherever KSP's camera pointed (empty sky above
            // KSC) no matter how we aimed. Resetting them makes the transform
            // authoritative again.
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

        /// <summary>
        /// Clones the live camera's current settings onto <paramref name="clone"/>,
        /// then forces the world transform + FOV we want. Position/rotation must
        /// be set AFTER CopyFrom (which doesn't touch the transform but whose
        /// inherited matrices are separately reset by the caller for the scaled
        /// cam).
        /// </summary>
        private void AimCamera(Camera clone, string liveCameraName, Vector3 pos, Quaternion rot, float fovDeg)
        {
            ResetCameraFromLive(clone, liveCameraName);
            clone.transform.position = pos;
            clone.transform.rotation = rot;
            clone.fieldOfView = fovDeg;
        }

        /// <summary>
        /// Copies the live camera's current settings (culling mask, clip planes,
        /// clear flags, ...) onto the clone, then restores the clone's own
        /// render target -- the one property that must survive the copy. The
        /// caller (AimCamera) sets transform + fov afterward; the scaled cam
        /// also resets its matrices there, since CopyFrom inherits KSP's custom
        /// view/projection matrix which would otherwise override the transform.
        /// </summary>
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

                // Matched to Tarsier's TSTCameraModule.setupRenderTexture()
                // exactly: 24-bit depth + explicit sRGB read/write, and an
                // explicit .Create() rather than relying on Unity's lazy
                // auto-creation the first time the RT is bound as a target.
                renderTexture = new RenderTexture(TextureWidth, TextureHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "ExoInstrumentsSolarSystemCameraRT"
                };
                renderTexture.Create();

                var galaxyObj = new GameObject("ExoInstrumentsGalaxyCamClone");
                // Explicit zero, not just parenting: Transform.parent preserves
                // *world* position across reparenting by default, which would
                // leave this clone sitting wherever the origin the child was
                // created at happened to be, instead of glued to root's position.
                galaxyObj.transform.parent = root.transform;
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
        /// Turns the raw rendered frame into the monochrome, harsh-contrast look
        /// of a single unprocessed amateur-CCD exposure through the selected
        /// filter, and through Kerbin's own sky:
        /// - Filter: a mono sensor shoots one filter at a time, so each filter
        ///   is its own grayscale frame -- it selects which rendered channel(s)
        ///   form the signal and the filter's light throughput (narrowband H-alpha
        ///   collects far less, so it comes out dimmer for the same exposure).
        /// - Extinction: Bouguer's-law atmospheric dimming (AtmosphericImagingNoise),
        ///   airmass-dependent -- a low target is genuinely fainter, not just blurrier.
        /// - Clouds: KSP has no real weather to read, so cloud cover is a
        ///   deterministic procedural field (AtmosphericImagingNoise.CloudCoverage)
        ///   drifting with time -- it attenuates the target, adds a scattered-light
        ///   veiling haze, and softens the frame a little, all spatially patchy.
        /// - Sky glow: twilight (ramped between the -12 deg capture cutoff and
        ///   -18 deg astronomical twilight) plus scattered moonlight plus a tiny
        ///   airglow baseline, all accumulating with exposure time exactly like
        ///   the target's own light does.
        /// - Scintillation: one frame-wide flux flicker per exposure (Young 1967,
        ///   the same real formula the transit photometers use), not per-pixel --
        ///   real turbulence modulates the whole incoming beam coherently.
        /// - Sensor noise: real Poisson-shaped photon shot noise (grows as
        ///   sqrt(signal), so shadows are proportionally noisier than highlights),
        ///   exposure-proportional dark current with its own shot noise (an
        ///   uncooled amateur sensor genuinely warms up on long subs), and a
        ///   constant read-noise floor that Gain does NOT amplify (matching how
        ///   modern sensors apply analog gain before the read-noise stage).
        /// - Defect map: a handful of fixed hot/dead pixels, seeded once from a
        ///   constant (this camera's own persistent chip defects), not from the
        ///   target or time -- the same pixels misbehave in every photo.
        /// - Drift: when Autoguiding is off, diurnal sky drift (tied to Kerbin's
        ///   rotation) smears the frame horizontally, proportional to exposure --
        ///   the classic untracked star-trail.
        /// - Focus: when Autofocus is off, a separable box blur scaled by
        ///   |FocusOffset|, combined with seeing and cloud-haze softening.
        /// </summary>
        private void ProcessFrame(int targetSeed, CelestialBody targetBody)
        {
            Color[] src = readbackTexture.GetPixels();
            EnsureDefectMap();

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
            double moonGlowUnits = ComputeMoonGlow(targetBody);
            float skyBackground = (float)((TwilightSkyBackgroundRatePerSecond * twilightRamp
                                          + MoonGlowRatePerSecond * moonGlowUnits
                                          + AirglowBaselinePerSecond) * exposureSeconds * filterThroughput);

            // Real cloud coverage from an installed EVE cloud config, if any --
            // no procedural stand-in. A single sample for the whole frame: at
            // this camera's FOV (0.08-8 deg) and the cloud shell's altitude
            // vs. Kerbin's radius, the ray's actual piercing point through the
            // clouds barely differs from KSC's own position, so per-pixel
            // spatial variation would be fabricated precision this data can't
            // really support.
            float coverage = ComputeCloudCoverage();

            AtmosphericImagingNoise.DarkCurrent(exposureSeconds, out double darkPedestalD, out double darkSigmaD);
            float darkPedestal = (float)darkPedestalD;
            float darkSigma = (float)darkSigmaD;

            // A new draw every exposure (real sensor read noise differs shot to
            // shot) -- unlike the defect map below, which is fixed per-camera.
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

                    float value = Mathf.Clamp01(postGain);
                    pixelScratch[i] = new Color(value, value, value, 1f);
                }
            }

            // Fixed sensor defect overlay: cheap second pass over a handful of
            // indices rather than a per-pixel branch in the hot main loop.
            foreach (int idx in hotPixelIndices)
            {
                float v = Mathf.Clamp01(0.9f + NextGaussian(rng, 0.05f));
                pixelScratch[idx] = new Color(v, v, v, 1f);
            }
            foreach (int idx in deadPixelIndices)
            {
                pixelScratch[idx] = new Color(0f, 0f, 0f, 1f);
            }

            // Diurnal drift trailing (only when NOT autoguiding). The sky sweeps
            // 360 deg per Kerbin rotation; over the exposure that is a small
            // angle, scaled into pixels by the field of view.
            if (!Autoguiding)
            {
                int driftPx = ComputeDriftPixels(exposureSeconds, targetBody);
                if (driftPx >= 1) ApplyHorizontalMotionBlur(pixelScratch, driftPx);
            }

            // Defocus (manual focus racked away from sharp) + atmospheric
            // seeing (turbulence in KSC's own sky, not the target's) + a touch
            // of cloud/haze softening all combine into one blur pass rather
            // than risking a visibly double-blurred image from separate passes.
            float defocusBlur = Autofocus ? 0f : Mathf.Abs(FocusOffset) * MaxDefocusBlurPx;
            float seeingBlur = ComputeSeeingBlurPx(targetBody);
            float cloudBlur = coverage * CloudBlurPxMax;
            float blurRadius = defocusBlur + seeingBlur + cloudBlur;
            if (blurRadius >= 0.5f)
            {
                ApplyBoxBlur(pixelScratch, Mathf.RoundToInt(blurRadius));
            }

            outputTexture.SetPixels(pixelScratch);
            outputTexture.Apply();
        }

        /// <summary>
        /// Altitude of any live body (target or Sun) above KSC's horizon, using
        /// the same world-position trig throughout this class. False when the
        /// home body or the body itself isn't available.
        /// </summary>
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

        /// <summary>
        /// Atmospheric seeing blur from looking UP THROUGH KERBIN'S OWN
        /// atmosphere at the target -- not the target's atmosphere, which has
        /// nothing to do with our image quality. Grows with airmass exactly
        /// like the transit photometers' scintillation term: mild near the
        /// zenith, sharply worse as the target sits low over the horizon.
        /// </summary>
        private float ComputeSeeingBlurPx(CelestialBody targetBody)
        {
            if (!TryComputeAltitudeDeg(targetBody, out double altDeg)) return 0f;
            if (altDeg <= 0.0) return MaxSeeingBlurPx; // shouldn't be capturable this low, but cap defensively

            double airmass = ImagingObservingConditions.AirmassAt(altDeg);
            if (double.IsInfinity(airmass) || double.IsNaN(airmass)) return MaxSeeingBlurPx;

            // Airmass 1 (zenith) -> no extra blur; grows with (X-1), same shape
            // as the photometric scintillation excess term.
            float excess = Mathf.Max(0f, (float)airmass - 1f);
            return Mathf.Min(MaxSeeingBlurPx, excess * SeeingBlurPxPerAirmass);
        }

        /// <summary>
        /// Ambient sky-glow contribution from the home body's natural
        /// satellites, in "1.0 = a full Mün at zenith" units. Uses the same
        /// live-3D-position trig as the rest of this class (not the catalog
        /// RA/Dec frame MoonlightPollution uses for star photometry -- this
        /// camera looks at live solar-system bodies). Separation from the
        /// target is deliberately not modeled: this is whole-sky ambient glow
        /// added uniformly to the frame, not a directional scattering lobe.
        /// A moon currently being imaged doesn't glow at itself.
        /// </summary>
        private static double ComputeMoonGlow(CelestialBody targetBody)
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (home == null || sun == null || home.orbitingBodies == null) return 0.0;

            double total = 0.0;
            foreach (CelestialBody moon in home.orbitingBodies)
            {
                if (moon == null || moon == targetBody) continue;
                if (!TryComputeAltitudeDeg(moon, out double altDeg) || altDeg <= 0.0) continue;

                Vector3d toSunFromMoon = (sun.position - moon.position).normalized;
                Vector3d toHomeFromMoon = (home.position - moon.position).normalized;
                double phaseAngleDeg = Vector3d.Angle(toSunFromMoon, toHomeFromMoon);
                double illuminated = (1.0 + Math.Cos(phaseAngleDeg * Math.PI / 180.0)) / 2.0;

                double distance = Math.Max(1.0, (moon.position - home.position).magnitude);
                double sizeRatio = moon.Radius / distance;
                double moonFlux = Math.Max(0.0, moon.albedo) * illuminated * sizeRatio * sizeRatio;
                double altitudeRamp = Math.Min(1.0, altDeg / 10.0);

                total += (moonFlux / MunReferenceFluxUnits) * altitudeRamp;
            }
            return total;
        }

        /// <summary>
        /// Real cloud coverage over KSC right now, from an installed EVE
        /// cloud config (EveCloudIntegration) -- 0 (no cloud effect at all,
        /// not "confirmed clear") whenever EVE isn't installed, the home body
        /// has no configured cloud layer, or the read fails for any reason.
        /// Uses KSC's own local "up", transformed into the home body's
        /// BODY-FIXED frame (bodyTransform) since EVE's cloud texture rotates
        /// with the planet -- see EveCloudIntegration's doc for the one
        /// disclosed approximation this involves (wind-drift rotation not
        /// replicated).
        /// </summary>
        private static float ComputeCloudCoverage()
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

        /// <summary>Builds the fixed hot/dead pixel index lists once, from a constant seed -- see the field doc.</summary>
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

        /// <summary>Relative light throughput of each filter -- luminance passes the most, narrowband H-alpha the least (so it needs a longer exposure to reach the same brightness).</summary>
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

        /// <summary>Signal a mono sensor records for a pixel through the given filter: luminance for L, the single channel for R/G/B, the red channel for H-alpha.</summary>
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

        /// <summary>
        /// Drift trail length in pixels for an untracked exposure: the sky turns
        /// 360 deg / Kerbin-rotation-period, so over the (game-time-equivalent)
        /// exposure it sweeps a small angle, converted to pixels by how many
        /// pixels one degree of FOV spans. Capped so a 30 s shot smears heavily
        /// but never fills the whole frame.
        /// </summary>
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

        /// <summary>Horizontal motion blur (averages each pixel with the `length` to its left) -- the untracked star-trail smear.</summary>
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

        /// <summary>Separable (horizontal then vertical) box blur over the grayscale scratch buffer, radius in pixels.</summary>
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
