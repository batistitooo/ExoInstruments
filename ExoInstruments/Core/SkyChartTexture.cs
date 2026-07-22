using System;
using System.Collections.Generic;
using UnityEngine;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// One catalog target plus its already-computed horizontal position and
    /// highlight state, ready to render. Built by the GUI layer each refresh:
    /// SkyCoordinates.ComputeLocalMeridianRaDeg once, then TryComputeHorizontal
    /// per visible target, IsHighlighted set from the current search filter.
    /// </summary>
    public struct SkyChartPoint
    {
        public StarTarget Target;
        public double AltitudeDeg;
        public double AzimuthDeg;
        public bool IsHighlighted; // matches the current search filter; only highlighted points are clickable

        // --- Solar-system body points -------------------------------------
        // A body is plotted through the same pipeline as a star, just with a
        // bigger marker sized to the body and its own color (bodies have no
        // stellar effective temperature). Identity/selection is handled in the
        // GUI layer, so nothing KSP-specific leaks into this struct.
        public bool IsBody;
        public float BodyMarkerRadiusPx;
        public Color BodyColor;
        /// <summary>True only for the body currently selected as the photography target -- the sole condition that draws its ring, so bodies don't look "search-highlighted" by default.</summary>
        public bool IsSelectedTarget;
    }

    /// <summary>
    /// Camera state for the sky chart. Pan is expressed in the *raw* (unzoomed)
    /// dome-projection pixel space -- the raw-space point that should sit at the
    /// center of the viewport. Zoom 1 = whole sky fits the texture (the old
    /// fixed behavior); higher values zoom in, panning around at that scale.
    /// </summary>
    public struct SkyChartView
    {
        public float Zoom;
        public Vector2 Pan;

        public static SkyChartView Default(int width, int height)
        {
            return new SkyChartView { Zoom = 1f, Pan = new Vector2(width / 2f, height / 2f) };
        }
    }

    /// <summary>
    /// Renders a zenith-centered dome/planisphere projection into a Texture2D
    /// scatter plot, same pixel-buffer approach as LightCurveTexture. North up,
    /// East right, zenith at center, horizon at the edge (before zoom/pan).
    /// Unity-dependent by design — kept separate from Core.
    /// </summary>
    public static class SkyChartTexture
    {
        private static readonly Color BackgroundColor = new Color(0.05f, 0.05f, 0.08f, 1f);
        private static readonly Color HorizonColor = new Color(0.3f, 0.3f, 0.35f, 0.8f);
        private static readonly Color GridColor = new Color(0.2f, 0.2f, 0.24f, 0.5f);

        // Fallback tint for the rare star with no effective temperature on
        // record at all (neither catalog star_teff nor a BSC B-V color) --
        // neutral blue-white rather than a color that implies a spectral class.
        private static readonly Color UnknownTeffTint = new Color(0.85f, 0.85f, 0.92f, 1f);

        // Ring drawn around clickable (search-matched) stars so interactivity
        // stays unambiguous even though the fill color is now the star's real
        // hue, not a flat highlight color. Flat, fully opaque, matte gray --
        // the old semi-transparent alpha-blended ring read as a soft glow/halo
        // ("game HUD" look); a real finder chart circles its target star with a
        // plain solid ink ring, so this is drawn as a direct pixel replace, not
        // a blend, per the roadmap's "flat, matte-colored... calmer" ask.
        private static readonly Color HighlightRingColor = new Color(0.72f, 0.72f, 0.76f, 1f);

        // Perceived-brightness display heuristic (not a real flux-ratio
        // computation -- same order of approximation as ComputeMarkerRadius's
        // magnitude<6 size threshold below): a linear ramp between a bright and
        // a faint reference magnitude, so dim stars visually fade toward the
        // night sky instead of just shrinking, the way they do to the naked eye.
        private const double BrightReferenceMagnitude = -1.5; // ~Sirius -- maps to full brightness
        private const double FaintReferenceMagnitude = 12.0;  // display floor -- catalog goes fainter, but the map compresses there
        private const float MinBrightnessFraction = 0.16f;

        // Non-highlighted (unclickable) points fade further and desaturate
        // toward gray so they read as background clutter -- same intent as the
        // old flat DimmedPointColor, but keeping a hint of real color instead of
        // erasing it, per Baptiste's "faithful color/opacity" request.
        private const float DimmedDesaturation = 0.55f;
        private const float DimmedBrightnessFactor = 0.6f;
        private const float MinHighlightedBrightnessFraction = 0.75f;

        // Reference rings, in degrees of altitude. 0 = horizon (drawn brighter).
        private static readonly double[] AltitudeRingsDeg = { 0.0, 20.0, 40.0, 60.0 };

        public static float ComputeRMax(int width, int height)
        {
            return (float)(Math.Min(width, height) / 2.0 - 4.0);
        }

        /// <summary>
        /// Marker radius in screen pixels -- grows with zoom so stars are visibly
        /// bigger (not just further apart) at higher zoom. Capped so it never
        /// becomes a blob.
        /// </summary>
        private static float ComputeMarkerRadius(double magnitude, float zoom)
        {
            float baseRadius = magnitude < 6.0 ? 2.5f : 1.6f;
            return Mathf.Min(baseRadius + (zoom - 1f) * 0.9f, 9f);
        }

        /// <summary>
        /// Click/hover tolerance in screen pixels. Generous baseline (8px) for
        /// cursor imprecision, growing with zoom to match the bigger markers.
        /// </summary>
        private static double ComputeHitRadius(float zoom)
        {
            return Math.Max(8.0, 4.0 + (zoom - 1.0) * 1.2);
        }

        /// <summary>
        /// Pure computation: fills a fresh Color[] buffer for the whole chart --
        /// background, reference rings, and every catalog point (a real catalog
        /// runs into the thousands once background stars are merged in). Touches
        /// nothing Unity considers main-thread-only (Color/Mathf are plain managed
        /// structs), so it's safe to call from a background Task (see
        /// ExoInstrumentsGUI's async refresh) instead of stalling the frame that's
        /// rendering the game for this loop's actual run time.
        ///
        /// <paramref name="searchActive"/> distinguishes "no search typed, so every
        /// point counts as a click target" (SkyChartPoint.IsHighlighted is true for
        /// everything in that case, by MatchesFilter's own empty-string rule) from
        /// an actual name match. Only an actual match should draw the ring / get the
        /// brightness floor -- without this flag, an empty search box painted a
        /// visible ring on literally every star on the chart, a blanket-halo look
        /// far from the "calm finder chart" this rendering aims for. Click/hover
        /// hit-testing (HitTest, elsewhere) is unaffected either way -- every point
        /// stays clickable when no search is active, only the visual cue is gated.
        /// </summary>
        public static Color[] ComputePixels(List<SkyChartPoint> points, int width, int height, SkyChartView view, bool searchActive)
        {
            var pixels = new Color[width * height];
            FillBackground(pixels);

            DrawReferenceGrid(pixels, width, height, view);

            if (points != null)
            {
                bool ShouldEmphasize(SkyChartPoint p) => searchActive && p.IsHighlighted;
                // de-emphasized stars first, then emphasized (search-matched)
                // stars, then solar-system bodies on top so their bigger discs
                // are never buried under the star field.
                foreach (var p in points)
                    if (!p.IsBody && !ShouldEmphasize(p)) PlotStar(pixels, width, height, p, view, false);
                foreach (var p in points)
                    if (!p.IsBody && ShouldEmphasize(p)) PlotStar(pixels, width, height, p, view, true);
                foreach (var p in points)
                    if (p.IsBody) PlotStar(pixels, width, height, p, view, false);
            }

            return pixels;
        }

        /// <summary>
        /// Main-thread-only: uploads an already-computed pixel buffer (see
        /// ComputePixels) into a texture, reusing <paramref name="existing"/>
        /// when its size already matches.
        /// </summary>
        public static Texture2D ApplyToTexture(Color[] pixels, int width, int height, Texture2D existing)
        {
            Texture2D tex = existing;
            if (tex == null || tex.width != width || tex.height != height)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Finds the highlighted point nearest a click/hover position, within
        /// ComputeHitRadius. Dimmed (non-matching) points are never clickable,
        /// by design -- narrows candidates the same way the search filter
        /// narrows the list view. Also used every frame for hover preview, not
        /// just on click -- it's a cheap O(n) loop, no texture work involved.
        /// </summary>
        public static StarTarget HitTest(List<SkyChartPoint> points, int width, int height, SkyChartView view, int clickX, int clickY)
        {
            if (points == null) return null;
            StarTarget best = null;
            double hitRadius = ComputeHitRadius(view.Zoom);
            double bestDistSq = hitRadius * hitRadius;

            foreach (var p in points)
            {
                if (!p.IsHighlighted) continue;
                Vector2 px = ProjectToPixel(p.AltitudeDeg, p.AzimuthDeg, width, height, view);
                double dx = px.x - clickX;
                double dy = px.y - clickY;
                double distSq = dx * dx + dy * dy;
                if (distSq <= bestDistSq)
                {
                    bestDistSq = distSq;
                    best = p.Target;
                }
            }
            return best;
        }

        private static void PlotStar(Color[] pixels, int width, int height, SkyChartPoint p, SkyChartView view, bool emphasize)
        {
            Vector2 px = ProjectToPixel(p.AltitudeDeg, p.AzimuthDeg, width, height, view);

            if (p.IsBody)
            {
                // A solar-system body: a bigger disc than any star, sized to the
                // body (BodyMarkerRadiusPx, precomputed from its real radius),
                // growing a little with zoom like the stars do. Only the
                // currently-selected photography target gets the ring -- every
                // other body is just its plain colored disc.
                float bodyRadius = Mathf.Min(p.BodyMarkerRadiusPx + (view.Zoom - 1f) * 0.9f, 20f);
                DrawFilledCircle(pixels, width, height, px.x, px.y, bodyRadius, p.BodyColor);
                if (p.IsSelectedTarget)
                {
                    DrawHighlightRing(pixels, width, height, px.x, px.y, bodyRadius, view.Zoom);
                }
                return;
            }

            float radius = ComputeMarkerRadius(p.Target.ApparentMagnitude, view.Zoom);
            Color color = ComputeStarDisplayColor(p.Target, emphasize);
            DrawFilledCircle(pixels, width, height, px.x, px.y, radius, color);
            if (emphasize)
            {
                DrawHighlightRing(pixels, width, height, px.x, px.y, radius, view.Zoom);
            }
        }

        /// <summary>
        /// A star's chart color, roughly the way it would actually look through
        /// a telescope: real hue from its effective temperature (the same
        /// blackbody mapping the ELT frame uses, StellarColor.BlackbodyRgb), real
        /// brightness from apparent magnitude. Pre-composited against the
        /// night-sky background in this CPU pixel buffer (not left as alpha for
        /// the GPU to blend at draw time) -- GUI.DrawTexture would otherwise
        /// blend a translucent pixel against whatever's behind the whole chart
        /// rect on screen (the IMGUI panel box), not against our own painted sky
        /// elsewhere in this same texture.
        /// </summary>
        private static Color ComputeStarDisplayColor(StarTarget target, bool highlighted)
        {
            float r, g, b;
            if (target.EffectiveTempK.HasValue)
            {
                StellarColor.BlackbodyRgb(target.EffectiveTempK.Value, out double rd, out double gd, out double bd);
                r = (float)rd; g = (float)gd; b = (float)bd;
            }
            else
            {
                r = UnknownTeffTint.r; g = UnknownTeffTint.g; b = UnknownTeffTint.b;
            }

            float brightness = ComputeApparentBrightnessFraction(target.ApparentMagnitude);

            if (highlighted)
            {
                brightness = Mathf.Max(brightness, MinHighlightedBrightnessFraction);
            }
            else
            {
                float gray = (r + g + b) / 3f;
                r = Mathf.Lerp(r, gray, DimmedDesaturation);
                g = Mathf.Lerp(g, gray, DimmedDesaturation);
                b = Mathf.Lerp(b, gray, DimmedDesaturation);
                brightness *= DimmedBrightnessFactor;
            }

            Color trueColor = new Color(r, g, b, 1f);
            Color blended = Color.Lerp(BackgroundColor, trueColor, brightness);
            return new Color(blended.r, blended.g, blended.b, 1f);
        }

        private static float ComputeApparentBrightnessFraction(double magnitude)
        {
            double t = (FaintReferenceMagnitude - magnitude) / (FaintReferenceMagnitude - BrightReferenceMagnitude);
            t = Math.Min(1.0, Math.Max(0.0, t));
            return (float)(MinBrightnessFraction + (1.0 - MinBrightnessFraction) * t);
        }

        /// <summary>Thin flat outline just outside the star's fill -- the interactivity cue now that fill color is the star's real hue, not a flat highlight color. Fully opaque (no alpha blend), matching a real finder chart's plain ink circle rather than a glowing HUD marker.</summary>
        private static void DrawHighlightRing(Color[] pixels, int width, int height, float cx, float cy, float innerRadius, float zoom)
        {
            float thickness = Mathf.Max(1f, 0.6f + (zoom - 1f) * 0.12f);
            float ringInner = innerRadius + 0.8f;
            float ringOuter = ringInner + thickness;
            float innerSq = ringInner * ringInner;
            float outerSq = ringOuter * ringOuter;

            int minX = Mathf.FloorToInt(cx - ringOuter);
            int maxX = Mathf.CeilToInt(cx + ringOuter);
            int minY = Mathf.FloorToInt(cy - ringOuter);
            int maxY = Mathf.CeilToInt(cy + ringOuter);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;
                    if (distSq < innerSq || distSq > outerSq) continue;
                    SetPixelSafe(pixels, width, height, x, y, HighlightRingColor);
                }
            }
        }

        private static void DrawFilledCircle(Color[] pixels, int width, int height, float cx, float cy, float radius, Color color)
        {
            int minX = Mathf.FloorToInt(cx - radius);
            int maxX = Mathf.CeilToInt(cx + radius);
            int minY = Mathf.FloorToInt(cy - radius);
            int maxY = Mathf.CeilToInt(cy + radius);
            float radiusSq = radius * radius;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    if (dx * dx + dy * dy <= radiusSq)
                    {
                        SetPixelSafe(pixels, width, height, x, y, color);
                    }
                }
            }
        }

        /// <summary>
        /// Public wrapper over the internal projection so overlays (e.g. the
        /// photography-mode planet markers drawn in IMGUI on top of the chart)
        /// land at exactly the same place as the baked star points, honoring the
        /// same zoom/pan. Returns texture-pixel space (origin bottom-left, y up),
        /// the same space HitTest / the GUI's local coords use.
        /// </summary>
        public static Vector2 ProjectAltAzToScreen(double altDeg, double azDeg, int width, int height, SkyChartView view)
            => ProjectToPixel(altDeg, azDeg, width, height, view);

        /// <summary>r = Rmax*(90-alt)/90 in raw space, then (raw - pan)*zoom + center for the view transform.</summary>
        private static Vector2 ProjectToPixel(double altDeg, double azDeg, int width, int height, SkyChartView view)
        {
            double rMax = ComputeRMax(width, height);
            double r = rMax * (90.0 - altDeg) / 90.0;
            double azRad = azDeg * Math.PI / 180.0;

            double centerX = width / 2.0;
            double centerY = height / 2.0;
            double rawX = centerX + r * Math.Sin(azRad);
            double rawY = centerY + r * Math.Cos(azRad); // North (az=0) points up, in raw space

            double screenX = (rawX - view.Pan.x) * view.Zoom + centerX;
            double screenY = (rawY - view.Pan.y) * view.Zoom + centerY;

            return new Vector2((float)screenX, (float)screenY);
        }

        private static void DrawReferenceGrid(Color[] pixels, int width, int height, SkyChartView view)
        {
            foreach (double altDeg in AltitudeRingsDeg)
            {
                Color color = altDeg <= 0.0 ? HorizonColor : GridColor;
                for (double azDeg = 0; azDeg < 360.0; azDeg += 0.5)
                {
                    Vector2 p = ProjectToPixel(altDeg, azDeg, width, height, view);
                    SetPixelSafe(pixels, width, height, (int)p.x, (int)p.y, color);
                }
            }

            // Cardinal cross through the zenith: N-S (az 0/180) and E-W (az 90/270),
            // each drawn from a bit below the horizon out to the zenith for a clean line.
            DrawRadialLine(pixels, width, height, 0.0, view);
            DrawRadialLine(pixels, width, height, 90.0, view);
        }

        private static void DrawRadialLine(Color[] pixels, int width, int height, double azDeg, SkyChartView view)
        {
            for (double alt = -10.0; alt <= 90.0; alt += 0.5)
            {
                Vector2 p1 = ProjectToPixel(alt, azDeg, width, height, view);
                SetPixelSafe(pixels, width, height, (int)p1.x, (int)p1.y, GridColor);

                Vector2 p2 = ProjectToPixel(alt, azDeg + 180.0, width, height, view);
                SetPixelSafe(pixels, width, height, (int)p2.x, (int)p2.y, GridColor);
            }
        }

        private static void FillBackground(Color[] pixels)
        {
            for (int i = 0; i < pixels.Length; i++) pixels[i] = BackgroundColor;
        }

        private static void SetPixelSafe(Color[] pixels, int width, int height, int x, int y, Color color)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            pixels[y * width + x] = color;
        }

    }
}