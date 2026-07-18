using System;
using System.Collections.Generic;
using UnityEngine;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// "Eyepiece mode" (roadmap): a pure-vibes view of the sky field around a
    /// selected catalog star -- real magnitudes, real colors (same blackbody
    /// mapping as the sky chart and the ELT frame), real relative positions.
    /// No names, no numbers, no science overlay: just the field, inside a
    /// circular field stop like a real eyepiece.
    ///
    /// Zero new physics by design: positions come straight from catalog RA/Dec
    /// through a standard gnomonic (tangent-plane) projection -- the correct
    /// projection for a narrow telescope field -- and the view is fixed on the
    /// celestial sphere, so it needs no refresh loop, no time dependence, no
    /// observing-conditions gating. Deliberately fog-of-war-safe: everything
    /// rendered (position, brightness, color) is directly observable data; no
    /// identity is drawn for any star.
    ///
    /// Pure computation + main-thread upload split, same contract as
    /// SkyChartTexture: ComputePixels touches no UnityEngine.Object API. Upload
    /// reuses SkyChartTexture.ApplyToTexture.
    /// </summary>
    public static class EyepieceTexture
    {
        // Outside the field stop: true black, like looking past the field lens.
        private static readonly Color SurroundColor = new Color(0f, 0f, 0f, 1f);
        // Sky inside the field: not pure black -- a long-exposure-adapted eye
        // through an eyepiece sees a very dark, faintly luminous background.
        private static readonly Color FieldBackgroundColor = new Color(0.012f, 0.014f, 0.024f, 1f);
        private static readonly Color FieldStopRimColor = new Color(0.28f, 0.28f, 0.30f, 1f);

        private static readonly Color UnknownTeffTint = new Color(0.85f, 0.85f, 0.92f, 1f);

        // Same display ramp family as the sky chart: linear in magnitude between
        // a bright and a faint reference. Linear-in-magnitude IS logarithmic in
        // flux -- the standard tone compression of every astro image (the true
        // flux ratio between mag -1.5 and 12 is ~250,000:1, unrenderable on any
        // display). The eyepiece is a telescope view, so the faint end reaches
        // the catalog's depth rather than the naked eye's.
        private const double BrightReferenceMagnitude = -1.5;
        private const double FaintReferenceMagnitude = 12.0;
        private const float MinBrightnessFraction = 0.30f;
        // Gamma < 1 lifts the mid-range the way a stretched astro image does --
        // without it the log ramp alone leaves most stars looking dull.
        private const float BrightnessGamma = 0.65f;

        // Stars render as a Gaussian point-spread function, not flat discs --
        // that's what "shining" actually is on any real image: the seeing disc /
        // scattered-light profile. Peak amplitude deliberately exceeds 1.0 for
        // bright stars so the additive clamp overexposes the core to pure white
        // while the wings keep the blackbody tint -- exactly how a bright star
        // looks on a CCD frame (saturated white core, colored fringe).
        private const float MaxPsfAmplitude = 2.6f;
        private const float MinPsfAmplitude = 0.55f;
        private const float MaxPsfSigmaPx = 3.4f;   // bright star: wide seeing/scatter spread
        private const float MinPsfSigmaPx = 0.75f;  // faint star: near-pixel point
        private const float PsfExtentSigmas = 3.5f; // draw out to this many sigmas

        public static Color[] ComputePixels(List<StarTarget> catalog, StarTarget center, double fovDeg, int size)
        {
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = SurroundColor;

            float cx = size / 2f;
            float cy = size / 2f;
            float fieldRadius = size / 2f - 6f;

            FillFieldDisc(pixels, size, cx, cy, fieldRadius);

            if (catalog != null && center != null && center.RaDeg.HasValue && center.DecDeg.HasValue)
            {
                double ra0 = center.RaDeg.Value * Math.PI / 180.0;
                double dec0 = center.DecDeg.Value * Math.PI / 180.0;
                double sinDec0 = Math.Sin(dec0);
                double cosDec0 = Math.Cos(dec0);

                // Gnomonic plate scale: the field-stop radius corresponds to
                // half the requested field of view, r_px = scale * tan(theta).
                double scale = fieldRadius / Math.Tan(fovDeg * 0.5 * Math.PI / 180.0);

                foreach (var star in catalog)
                {
                    if (!star.RaDeg.HasValue || !star.DecDeg.HasValue) continue;

                    double ra = star.RaDeg.Value * Math.PI / 180.0;
                    double dec = star.DecDeg.Value * Math.PI / 180.0;
                    double dRa = ra - ra0;
                    double sinDec = Math.Sin(dec);
                    double cosDec = Math.Cos(dec);
                    double cosDRa = Math.Cos(dRa);

                    // Angular distance from field center via the projection's own
                    // denominator; anything approaching 90 deg away is far outside
                    // any sane eyepiece field and would blow the tangent up.
                    double cosc = sinDec0 * sinDec + cosDec0 * cosDec * cosDRa;
                    if (cosc < 0.5) continue;

                    double xProj = cosDec * Math.Sin(dRa) / cosc;
                    double yProj = (cosDec0 * sinDec - sinDec0 * cosDec * cosDRa) / cosc;

                    // North up, East left -- the astronomical sky-view convention
                    // (a chart of the celestial sphere seen from inside), which is
                    // why printed finder charts mark E on the left. Texture rows
                    // grow upward (row 0 = bottom), so +dec maps to +y directly.
                    float px = cx - (float)(xProj * scale);
                    float py = cy + (float)(yProj * scale);

                    float distFromCenter = Vector2.Distance(new Vector2(px, py), new Vector2(cx, cy));
                    if (distFromCenter > fieldRadius + MaxPsfSigmaPx * PsfExtentSigmas) continue;

                    float brightness = ComputeBrightnessFraction(star.ApparentMagnitude);
                    float amplitude = Mathf.Lerp(MinPsfAmplitude, MaxPsfAmplitude, brightness);
                    float sigma = Mathf.Lerp(MinPsfSigmaPx, MaxPsfSigmaPx, brightness);
                    Color tint = StarTint(star);

                    DrawStarPsf(pixels, size, px, py, amplitude, sigma, tint, cx, cy, fieldRadius);
                }
            }

            DrawFieldStopRim(pixels, size, cx, cy, fieldRadius);
            return pixels;
        }

        private static Color StarTint(StarTarget star)
        {
            if (star.EffectiveTempK.HasValue)
            {
                StellarColor.BlackbodyRgb(star.EffectiveTempK.Value, out double r, out double g, out double b);
                return new Color((float)r, (float)g, (float)b, 1f);
            }
            return UnknownTeffTint;
        }

        private static float ComputeBrightnessFraction(double magnitude)
        {
            double t = (FaintReferenceMagnitude - magnitude) / (FaintReferenceMagnitude - BrightReferenceMagnitude);
            t = Math.Min(1.0, Math.Max(0.0, t));
            t = Math.Pow(t, BrightnessGamma);
            return (float)(MinBrightnessFraction + (1.0 - MinBrightnessFraction) * t);
        }

        private static void FillFieldDisc(Color[] pixels, int size, float cx, float cy, float radius)
        {
            float radiusSq = radius * radius;
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - radius));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + radius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - radius));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + radius));
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    if (dx * dx + dy * dy <= radiusSq)
                        pixels[y * size + x] = FieldBackgroundColor;
                }
            }
        }

        /// <summary>
        /// Gaussian PSF, additively blended: light adds, so overlapping stars
        /// brighten each other and a bright star's core clamps to pure white
        /// (amplitude > 1) while its wings keep the blackbody tint. Clipped to
        /// the field stop so no star paints outside it. Pixels stay alpha 1
        /// (pre-composited), same rationale as the sky chart.
        /// </summary>
        private static void DrawStarPsf(Color[] pixels, int size, float px, float py, float amplitude, float sigma, Color tint, float cx, float cy, float fieldRadius)
        {
            float extent = sigma * PsfExtentSigmas;
            float invTwoSigmaSq = 1f / (2f * sigma * sigma);
            float fieldRadiusSq = fieldRadius * fieldRadius;
            int minX = Mathf.Max(0, Mathf.FloorToInt(px - extent));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(px + extent));
            int minY = Mathf.Max(0, Mathf.FloorToInt(py - extent));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(py + extent));

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float fx = x - cx;
                    float fy = y - cy;
                    if (fx * fx + fy * fy > fieldRadiusSq) continue;

                    float dx = x - px;
                    float dy = y - py;
                    float intensity = amplitude * Mathf.Exp(-(dx * dx + dy * dy) * invTwoSigmaSq);
                    if (intensity < 0.004f) continue;

                    int idx = y * size + x;
                    Color existing = pixels[idx];
                    pixels[idx] = new Color(
                        Mathf.Min(1f, existing.r + tint.r * intensity),
                        Mathf.Min(1f, existing.g + tint.g * intensity),
                        Mathf.Min(1f, existing.b + tint.b * intensity),
                        1f);
                }
            }
        }

        private static void DrawFieldStopRim(Color[] pixels, int size, float cx, float cy, float fieldRadius)
        {
            float inner = fieldRadius;
            float outerR = fieldRadius + 1.6f;
            float innerSq = inner * inner;
            float outerSq = outerR * outerR;
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - outerR));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + outerR));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - outerR));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + outerR));
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;
                    if (distSq < innerSq || distSq > outerSq) continue;
                    pixels[y * size + x] = FieldStopRimColor;
                }
            }
        }
    }
}
