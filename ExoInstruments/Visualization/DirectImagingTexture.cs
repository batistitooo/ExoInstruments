using System;
using UnityEngine;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// Simulated high-contrast image: log-stretched stellar PSF with its real diffraction
    /// rings, six spider spikes (ELT pupil), a wind-driven anisotropic speckle halo that fades
    /// as sqrt(t_eff), sky+detector background, hot pixels, and the planet as a faint PSF
    /// at real separation/contrast. All parameters deterministic per target so refreshes
    /// don't strobe. Star magnitude scales the whole starlight field; temperature false-colors it.
    ///
    /// The stellar and planetary profiles are the exact annular-pupil Airy pattern of the real
    /// ELT pupil, sampled through Core's RadialPsfProfile -- the same closed form the rest of the
    /// project convolves as a kernel (Core/OpticalPsf.cs). This class used to carry its own
    /// Gaussian core plus an invented ring envelope, which was a second and mutually inconsistent
    /// answer to a question Core already answered exactly. There is now one diffraction model.
    /// </summary>
    public static class DirectImagingTexture
    {
        private const double LogStretchDecades = 9.0;  // display maps intensities 1e-9..1 onto 0..1

        // Apparent magnitude at which the stellar PSF peak saturates the display
        // scale. Brighter stars clamp to 1; fainter ones dim as 10^(-0.4*dm),
        // floored so even the faintest chartable target still shows a core.
        private const double SaturationMagnitude = 3.0;
        private const double MinPeakScale = 3.0e-3;

        // Pointing offset: how far off-center the star may land, as a fraction
        // of the field of view per axis. With FOV >= 4.6x the separation, star
        // offset + separation stays inside ~0.38 FOV -- planet always in frame.
        private const double MaxPointingOffsetFovFraction = 0.12;

        // Spider spikes: 6 vanes (ELT), Gaussian angular profile, 1/r^2 falloff.
        //
        // NOT sourced, and deliberately left that way for now: the vane count is real (the ELT's
        // M2 sits on six arms), but the amplitude below is a display constant, not a computed one.
        // Deriving it means the two-dimensional Fourier transform of the real pupil including its
        // 0.5 m vanes, which is a different piece of machinery from the radially symmetric closed
        // form the rings come from. Flagged as an assumed value in TECHNICAL_REFERENCE §12 rather
        // than dressed up as physics.
        private const int SpikeCount = 6;
        private const double SpikeAmplitude = 4.0e-4;   // relative to PSF peak at 1 lambda/D
        private const double SpikeSigmaDeg = 1.3;

        // Wind-driven halo: two-lobed cos^2 modulation of the speckle amplitude.
        private const double WindHaloMin = 0.55;
        private const double WindHaloDepth = 0.9;       // modulation range: 0.55 .. 1.45

        // Sky + detector background, 1-sigma at 1 hour of effective integration,
        // in display units (fractions of a magnitude-3 star's peak). Does NOT
        // scale with the target's brightness -- that's the point: bright stars
        // tower over it, faint ones sit closer to it.
        private const double BackgroundSigma1Hr = 3.0e-9;

        private const int HotPixelCount = 14;

        /// <summary>
        /// Below this much accumulated on-sky integration the frame contains no
        /// starlight at all: the session just opened (typically in daytime, dome
        /// closed) and the detector has only its own readout noise to show.
        /// The star only appears once photons have actually been collected.
        /// </summary>
        public const double MinStarlightExposureSeconds = 1.0;

        // Readout-only frame: mean level and RMS of the displayed dark frame,
        // in display units. Visibly "camera noise", clearly not sky.
        private const float ReadoutNoiseFloor = 0.02f;
        private const float ReadoutNoiseAmplitude = 0.06f;

        /// <summary>Everything ComputePixels needs, packaged so it can run off the main thread with no live UnityEngine.Object access.</summary>
        public struct PixelResult
        {
            public Color[] Pixels;
            public double FovArcsec;
        }

        /// <summary>Pure computation — safe to call off the main thread. The 400×400 raster with per-pixel transcendentals is the mod's most expensive refresh; running it in a background Task (see ExoInstrumentsGUI's async refresh) keeps it off the game frame.</summary>
        public static PixelResult ComputePixels(StarTarget star, DirectImagingAssessment assessment, double effectiveExposureSeconds, int size)
        {
            double thetaDiff = assessment.DiffractionLimitArcsec;
            double lambdaOverD = thetaDiff / 1.22;

            bool placePlanet = assessment.HasRequiredData && assessment.SignalPresent;
            double separation = assessment.SeparationArcsec;

            // Field of view: wide enough to contain the planet plus the pointing
            // offset when its separation is known, else a generic close-in view.
            double fovArcsec = separation > 0
                ? Math.Max(4.6 * separation, 12.0 * lambdaOverD)
                : 12.0 * lambdaOverD;
            double arcsecPerPixel = fovArcsec / size;

            // Session just opened (dome closed, daytime): only readout noise and hot pixels — no photons yet.
            if (effectiveExposureSeconds < MinStarlightExposureSeconds)
            {
                var darkPixels = new Color[size * size];
                int readSeed = DeterministicSeed(star.Name + "#readout");
                for (int py = 0; py < size; py++)
                {
                    for (int px = 0; px < size; px++)
                    {
                        float v = ReadoutNoiseFloor + (float)HashNoise01(px, py, readSeed) * ReadoutNoiseAmplitude;
                        darkPixels[py * size + px] = new Color(v, v, v, 1f);
                    }
                }
                DrawHotPixels(darkPixels, size, DeterministicSeed(star.Name + "#hot"));
                return new PixelResult { Pixels = darkPixels, FovArcsec = fovArcsec };
            }

            // Speckle floor and background improve as sqrt(t_eff); the branch
            // above guarantees at least MinStarlightExposureSeconds here.
            double hours = Math.Max(effectiveExposureSeconds, MinStarlightExposureSeconds) / 3600.0;
            double sqrtHours = Math.Sqrt(hours);

            // The whole starlight field scales with apparent brightness.
            double peakScale = Math.Pow(10.0, -0.4 * (star.ApparentMagnitude - SaturationMagnitude));
            peakScale = Math.Min(1.0, Math.Max(MinPeakScale, peakScale));

            // False-color tints from measured temperatures.
            double starR = 1.0, starG = 1.0, starB = 1.0;
            bool hasStarColor = star.EffectiveTempK.HasValue && star.EffectiveTempK.Value > 0;
            if (hasStarColor)
            {
                StellarColor.BlackbodyRgb(star.EffectiveTempK.Value, out starR, out starG, out starB);
            }
            double planetR = 1.0, planetG = 0.3, planetB = 0.1;
            if (placePlanet && assessment.PlanetTempKUsed > 0)
            {
                StellarColor.BlackbodyRgb(assessment.PlanetTempKUsed, out planetR, out planetG, out planetB);
            }

            int seed = DeterministicSeed(star.Name);
            int bgSeed = DeterministicSeed(star.Name + "#bg");

            // Pointing offset: where the star actually landed on the detector.
            double starX = (Hash01(star.Name + "#offx") * 2.0 - 1.0) * MaxPointingOffsetFovFraction * fovArcsec;
            double starY = (Hash01(star.Name + "#offy") * 2.0 - 1.0) * MaxPointingOffsetFovFraction * fovArcsec;

            double paRadians = Hash01(star.Name + "#pa") * 2.0 * Math.PI;
            double planetX = starX + separation * Math.Cos(paRadians);
            double planetY = starY + separation * Math.Sin(paRadians);

            double spikeBaseRad = Hash01(star.Name + "#pupil") * (Math.PI / SpikeCount * 2.0);
            double windPaRad = Hash01(star.Name + "#wind") * Math.PI;

            // The instrument's real diffraction pattern, tabulated once for this frame's plate
            // scale. Built out to 1.2x the raster width so that the far corner of the frame is
            // still tabulated for a star at its maximum pointing offset and for a planet at its
            // maximum separation, rather than falling back on the profile's held last value.
            var psf = RadialPsfProfile.Build(
                DirectImagingSimulator.ApertureMeters,
                DirectImagingSimulator.ObstructionRatio,
                DirectImagingSimulator.WavelengthMeters,
                arcsecPerPixel,
                size * 1.2);

            double spikeSigmaRad = SpikeSigmaDeg * Math.PI / 180.0;
            double spikeSectorRad = Math.PI / SpikeCount * 2.0; // 60 deg between spikes

            var pixels = new Color[size * size];
            double half = size / 2.0;

            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    double xArc = (px - half) * arcsecPerPixel;
                    double yArc = (py - half) * arcsecPerPixel;

                    // Everything stellar is measured from the star's off-center position.
                    double dxs = xArc - starX;
                    double dys = yArc - starY;
                    double r = Math.Sqrt(dxs * dxs + dys * dys);

                    // Stellar PSF: the exact annular-pupil Airy pattern of the real ELT pupil,
                    // averaged over the pixel the way a detector integrates it. Core radius, ring
                    // radii and ring amplitudes are all consequences of D, the obstruction and
                    // lambda; none of them is a free parameter here any more.
                    double starIntensity = psf.AtArcsec(r);

                    double theta = Math.Atan2(dys, dxs);

                    // Spider spikes: Gaussian in azimuth around each of 6 ELT vane axes, 1/r^2 along them.
                    if (r > 0.7 * lambdaOverD)
                    {
                        double azOffset = (theta - spikeBaseRad) % spikeSectorRad;
                        if (azOffset < 0) azOffset += spikeSectorRad;
                        double dAz = Math.Min(azOffset, spikeSectorRad - azOffset);
                        if (dAz < 4.0 * spikeSigmaRad)
                        {
                            double along = lambdaOverD / r;
                            starIntensity += SpikeAmplitude * Math.Exp(-0.5 * dAz * dAz / (spikeSigmaRad * spikeSigmaRad)) * along * along;
                        }
                    }

                    // Speckle halo: fades as sqrt(t_eff) — what visually uncovers the planet over a session.
                    // cos^2 modulation = the classic AO wind-butterfly (residuals pile along the wind axis).
                    double floorHere = DirectImagingSimulator.SpeckleFloorAtSeparation(assessment.BaseFloor5Sigma1Hr, Math.Max(r, lambdaOverD));
                    double sigmaNow = floorHere / 5.0 / sqrtHours;
                    double windCos = Math.Cos(theta - windPaRad);
                    sigmaNow *= WindHaloMin + WindHaloDepth * windCos * windCos;
                    starIntensity += HashNoise01(px, py, seed) * sigmaNow * 2.0;

                    starIntensity *= peakScale;

                    // Sky + detector background: does not scale with the star.
                    starIntensity += HashNoise01(px, py, bgSeed) * BackgroundSigma1Hr / sqrtHours * 2.0;

                    double planetIntensity = 0.0;
                    if (placePlanet)
                    {
                        // Same instrument, same pupil, so the companion carries the same pattern --
                        // rings included. It had none under the old Gaussian, which is why a
                        // marginally resolved companion used to read as a featureless blob.
                        double dx = xArc - planetX;
                        double dy = yArc - planetY;
                        double rp = Math.Sqrt(dx * dx + dy * dy);
                        planetIntensity = assessment.ContrastRatio * peakScale * psf.AtArcsec(rp);
                    }

                    Color starColor = hasStarColor
                        ? MapIntensityTinted(starIntensity, starR, starG, starB)
                        : MapIntensityHeatRamp(starIntensity);
                    Color planetColor = planetIntensity > 0.0
                        ? MapIntensityTinted(planetIntensity, planetR, planetG, planetB)
                        : Color.clear;

                    // Additive: companion is extra light on top of the stellar field, as on a real detector.
                    pixels[py * size + px] = new Color(
                        Mathf.Clamp01(starColor.r + planetColor.r),
                        Mathf.Clamp01(starColor.g + planetColor.g),
                        Mathf.Clamp01(starColor.b + planetColor.b),
                        1f);
                }
            }

            DrawHotPixels(pixels, size, DeterministicSeed(star.Name + "#hot"));
            DrawDiffractionLimitRing(pixels, size,
                half + starX / arcsecPerPixel, half + starY / arcsecPerPixel,
                thetaDiff / arcsecPerPixel);

            return new PixelResult { Pixels = pixels, FovArcsec = fovArcsec };
        }

        /// <summary>
        /// Main-thread-only: uploads an already-computed pixel buffer (see
        /// ComputePixels) into a texture, reusing <paramref name="existing"/>
        /// when its size already matches.
        /// </summary>
        public static Texture2D ApplyToTexture(Color[] pixels, int size, Texture2D existing)
        {
            Texture2D tex = existing;
            if (tex == null || tex.width != size || tex.height != size)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Log stretch over LogStretchDecades decades, then black -> tint -> white:
        /// the source's blackbody color carries the midtones, hot cores burn to white.
        /// </summary>
        private static Color MapIntensityTinted(double intensity, double r, double g, double b)
        {
            float v = LogStretch(intensity);
            if (v < 0.5f)
            {
                float t = v / 0.5f;
                return new Color((float)r * t, (float)g * t, (float)b * t, 1f);
            }
            else
            {
                float t = (v - 0.5f) / 0.5f;
                return new Color(
                    (float)r + (1f - (float)r) * t,
                    (float)g + (1f - (float)g) * t,
                    (float)b + (1f - (float)b) * t,
                    1f);
            }
        }

        /// <summary>Neutral black-orange-white heat ramp (near-IR imaging convention) -- kept for stars with no measured color.</summary>
        private static Color MapIntensityHeatRamp(double intensity)
        {
            float v = LogStretch(intensity);
            if (v < 0.5f)
            {
                float t = v / 0.5f;
                return new Color(t * 0.9f, t * 0.35f, t * 0.05f, 1f); // black -> deep orange
            }
            else
            {
                float t = (v - 0.5f) / 0.5f;
                return new Color(0.9f + t * 0.1f, 0.35f + t * 0.65f, 0.05f + t * 0.95f, 1f); // deep orange -> white
            }
        }

        private static float LogStretch(double intensity)
        {
            double logI = Math.Log10(Math.Max(intensity, 1e-12));
            return Mathf.Clamp01((float)((logI + LogStretchDecades) / LogStretchDecades));
        }

        /// <summary>
        /// Uncorrected bad detector pixels: fixed positions per target (the same
        /// physical detector would put them at fixed positions per instrument, but
        /// per-target keeps each frame visually distinct). Rendered neutral white --
        /// they're electronics, not light, so no false-color tint.
        /// </summary>
        private static void DrawHotPixels(Color[] pixels, int size, int seed)
        {
            var hot = new Color(0.95f, 0.95f, 0.95f, 1f);
            for (int i = 0; i < HotPixelCount; i++)
            {
                int x = (int)(HashNoise01(i, 977, seed) * size);
                int y = (int)(HashNoise01(i, 1409, seed) * size);
                if (x < 0 || x >= size || y < 0 || y >= size) continue;
                pixels[y * size + x] = hot;
            }
        }

        /// <summary>Faint marker ring at the diffraction limit, centered on the star's actual position -- inside it, nothing is resolvable no matter the exposure.</summary>
        private static void DrawDiffractionLimitRing(Color[] pixels, int size, double centerX, double centerY, double radiusPixels)
        {
            var ringColor = new Color(0.3f, 0.6f, 0.7f, 1f);
            int steps = (int)(2.0 * Math.PI * radiusPixels * 2.0);
            for (int i = 0; i < steps; i++)
            {
                // Dashed: draw 2 of every 6 steps so the ring reads as a guide, not data.
                if (i % 6 >= 2) continue;
                double angle = 2.0 * Math.PI * i / steps;
                int x = (int)(centerX + radiusPixels * Math.Cos(angle));
                int y = (int)(centerY + radiusPixels * Math.Sin(angle));
                if (x < 0 || x >= size || y < 0 || y >= size) continue;
                pixels[y * size + x] = Color.Lerp(pixels[y * size + x], ringColor, 0.5f);
            }
        }

        private static int DeterministicSeed(string name)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in name) hash = hash * 31 + c;
                return Math.Abs(hash);
            }
        }

        /// <summary>Deterministic [0,1) from a string -- per-target angles and offsets.</summary>
        private static double Hash01(string name)
        {
            return (DeterministicSeed(name) % 100000) / 100000.0;
        }

        /// <summary>Deterministic per-pixel noise in [0,1) -- a fixed speckle pattern per target, so refreshes don't strobe.</summary>
        private static double HashNoise01(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2246822519);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return h / (double)uint.MaxValue;
            }
        }
    }
}
