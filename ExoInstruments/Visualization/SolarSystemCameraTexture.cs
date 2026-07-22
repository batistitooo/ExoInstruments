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
    /// pipeline (extinction, shot noise, seeing, EVE cloud cover).
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
        /// sky glow (twilight + moon + airglow), scintillation, shot/dark/read noise,
        /// hot/dead pixels, optional drift trail, and combined defocus/seeing blur.
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

                    float value = Mathf.Clamp01(postGain);
                    pixelScratch[i] = new Color(value, value, value, 1f);
                }
            }

            // Defect overlay: second pass over a handful of indices avoids a per-pixel branch in the main loop.
            foreach (int idx in hotPixelIndices)
            {
                float v = Mathf.Clamp01(0.9f + NextGaussian(rng, 0.05f));
                pixelScratch[idx] = new Color(v, v, v, 1f);
            }
            foreach (int idx in deadPixelIndices)
            {
                pixelScratch[idx] = new Color(0f, 0f, 0f, 1f);
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
        /// Sky-glow from the home body's moons, in "1.0 = full Mün at zenith" units.
        /// Uses live 3D positions (not the RA/Dec catalog frame). Uniform glow over
        /// the whole frame — no directional scattering. A moon being imaged is excluded.
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
