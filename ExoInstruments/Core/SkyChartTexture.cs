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
        /// <summary>True only for the body currently selected as the photography target, the sole condition that draws its ring, so bodies don't look "search-highlighted" by default.</summary>
        public bool IsSelectedTarget;

        // --- Deep-sky points ----------------------------------------------
        // A nebula is not a point source and has no magnitude to size a disc
        // by, so it gets a cross scaled to its own apparent extent instead.
        public bool IsDeepSky;
        public DeepSkyObject DeepSky;
    }

    /// <summary>
    /// Camera state for the sky chart. Pan is expressed in the *raw* (unzoomed)
    /// dome-projection pixel space, the raw-space point that should sit at the
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
        // record at all (neither catalog star_teff nor a BSC B-V color),
        // neutral blue-white rather than a color that implies a spectral class.
        private static readonly Color UnknownTeffTint = new Color(0.85f, 0.85f, 0.92f, 1f);

        // Flat opaque ring (not alpha-blended): matches a real finder chart's ink circle rather
        // than the soft HUD-glow look the old semi-transparent version had.
        private static readonly Color HighlightRingColor = new Color(0.72f, 0.72f, 0.76f, 1f);

        // Brightness ramp: dim stars fade toward the sky rather than just shrinking, like the naked eye.
        private const double BrightReferenceMagnitude = -1.5; // ~Sirius → full brightness
        private const double FaintReferenceMagnitude = 12.0;  // display floor
        private const float MinBrightnessFraction = 0.16f;

        // Unclickable stars desaturate toward gray — same intent as the old flat color but keeps a hint of real tint.
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
        /// Marker radius in screen pixels; grows with zoom so stars are visibly
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
        /// Pure computation — safe to call from a background Task. Fills the full chart buffer
        /// (background, rings, all catalog points). searchActive gates the highlight ring: without
        /// it, an empty search box would draw a ring on every star — not the calm finder-chart look we want.
        /// Click hit-testing is unaffected; every point stays clickable when no search is active.
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
                    if (!p.IsBody && !p.IsDeepSky && !ShouldEmphasize(p)) PlotStar(pixels, width, height, p, view, false);
                foreach (var p in points)
                    if (!p.IsBody && !p.IsDeepSky && ShouldEmphasize(p)) PlotStar(pixels, width, height, p, view, true);
                foreach (var p in points)
                    if (p.IsDeepSky) PlotDeepSky(pixels, width, height, p, view);
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

        /// <summary>Nearest highlighted point within ComputeHitRadius. Dimmed (non-search-matching) points are never clickable. O(n) loop, called every frame for hover preview.</summary>
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

        // Deep-sky markers: a cross rather than a disc, because a nebula is not a point source and
        // a disc among the star discs would read as another star. Line emitters and reflection
        // nebulae are tinted apart; one shows in a narrowband filter and the other cannot.
        private static readonly Color EmissionMarkerColor = new Color(0.95f, 0.42f, 0.45f, 1f);
        private static readonly Color ReflectionMarkerColor = new Color(0.55f, 0.68f, 0.95f, 1f);
        private static readonly Color GalaxyMarkerColor = new Color(0.95f, 0.86f, 0.55f, 1f);

        /// <summary>
        /// Cross sized to the object's own apparent extent, so the chart says how much sky it
        /// covers as well as where it is, the difference between the 2 degree North America and
        /// the 25 arcsec Cat's Eye is the difference between a wide-field target and one that needs
        /// the CDK. Floored so the small ones stay clickable, capped so the big ones stay a marker.
        /// </summary>
        private static void PlotDeepSky(Color[] pixels, int width, int height, SkyChartPoint p, SkyChartView view)
        {
            Vector2 px = ProjectToPixel(p.AltitudeDeg, p.AzimuthDeg, width, height, view);

            // The dome projection puts the horizon at rMax and the zenith at the centre, so one
            // degree of sky is rMax/90 raw pixels; zoom scales that.
            double pxPerDeg = ComputeRMax(width, height) / 90.0 * view.Zoom;
            float arm = (float)Math.Max(4.0, Math.Min(40.0, p.DeepSky.MajorArcmin / 60.0 * pxPerDeg * 0.5));

            Color color = p.DeepSky.Kind == DeepSkyKind.ReflectionNebula ? ReflectionMarkerColor
                        : p.DeepSky.Kind == DeepSkyKind.Galaxy ? GalaxyMarkerColor
                        : EmissionMarkerColor;

            // A gap at the centre, so the cross frames the object instead of covering it.
            float gap = Math.Max(1.5f, arm * 0.35f);
            for (float d = gap; d <= arm; d += 0.5f)
            {
                SetPixel(pixels, width, height, px.x + d, px.y, color);
                SetPixel(pixels, width, height, px.x - d, px.y, color);
                SetPixel(pixels, width, height, px.x, px.y + d, color);
                SetPixel(pixels, width, height, px.x, px.y - d, color);
            }

            if (p.IsSelectedTarget)
                DrawHighlightRing(pixels, width, height, px.x, px.y, arm, view.Zoom);
        }

        private static void SetPixel(Color[] pixels, int width, int height, float x, float y, Color color)
        {
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            if (ix < 0 || ix >= width || iy < 0 || iy >= height) return;
            pixels[iy * width + ix] = color;
        }

        /// <summary>Nearest deep-sky marker within its own arm length, so a big nebula is as easy to click as it is to see.</summary>
        public static bool HitTestDeepSky(List<SkyChartPoint> points, int width, int height,
                                          SkyChartView view, int clickX, int clickY, out DeepSkyObject hit)
        {
            hit = default(DeepSkyObject);
            if (points == null) return false;
            bool found = false;
            double bestDistSq = double.MaxValue;
            double pxPerDeg = ComputeRMax(width, height) / 90.0 * view.Zoom;

            foreach (var p in points)
            {
                if (!p.IsDeepSky) continue;
                Vector2 px = ProjectToPixel(p.AltitudeDeg, p.AzimuthDeg, width, height, view);
                double arm = Math.Max(4.0, Math.Min(40.0, p.DeepSky.MajorArcmin / 60.0 * pxPerDeg * 0.5));
                double tolerance = Math.Max(ComputeHitRadius(view.Zoom), arm);
                double dx = px.x - clickX, dy = px.y - clickY;
                double distSq = dx * dx + dy * dy;
                if (distSq <= tolerance * tolerance && distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    hit = p.DeepSky;
                    found = true;
                }
            }
            return found;
        }

        private static void PlotStar(Color[] pixels, int width, int height, SkyChartPoint p, SkyChartView view, bool emphasize)
        {
            Vector2 px = ProjectToPixel(p.AltitudeDeg, p.AzimuthDeg, width, height, view);

            if (p.IsBody)
            {
                // Solar-system body: bigger disc, grows with zoom. Only the selected photography target gets the ring.
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
            if (emphasize || p.IsSelectedTarget)
            {
                DrawHighlightRing(pixels, width, height, px.x, px.y, radius, view.Zoom);
            }
        }

        /// <summary>Star color from blackbody temperature + apparent brightness, pre-composited against the sky background. Pre-compositing is required: GUI.DrawTexture would otherwise blend against the IMGUI panel box, not our own sky pixels.</summary>
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

        /// <summary>Thin flat outline just outside the star's fill, the interactivity cue now that fill color is the star's real hue, not a flat highlight color. Fully opaque (no alpha blend), matching a real finder chart's plain ink circle rather than a glowing HUD marker.</summary>
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

        /// <summary>
        /// Inverse of ProjectToPixel's raw (pre pan/zoom) stage: given a raw pixel position, the
        /// alt/az that would project there. Used by ExoInstrumentsGUI's marker decluttering (see
        /// BuildChartBodyPoints/DeclutterBodyPositions) to convert a small on-screen nudge back
        /// into real alt/az, so the same coordinates drive both rendering and hit-testing.
        /// </summary>
        public static void UnprojectRawPixel(double rawX, double rawY, int width, int height, out double altDeg, out double azDeg)
        {
            double rMax = ComputeRMax(width, height);
            double centerX = width / 2.0;
            double centerY = height / 2.0;
            double dx = rawX - centerX;
            double dy = rawY - centerY;
            double r = Math.Sqrt(dx * dx + dy * dy);
            azDeg = (Math.Atan2(dx, dy) * 180.0 / Math.PI + 360.0) % 360.0;
            altDeg = 90.0 * (1.0 - r / rMax);
        }

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

        /// <summary>
        /// The inverse: which altitude and azimuth a chart pixel sits at. Needed to point the
        /// telescope at empty sky rather than only at a marker.
        ///
        /// Returns false above the horizon ring, where the projection has no sky to name.
        /// </summary>
        public static bool TryScreenToAltAz(float screenX, float screenY, int width, int height,
                                            SkyChartView view, out double altDeg, out double azDeg)
        {
            altDeg = azDeg = 0.0;
            double centerX = width / 2.0;
            double centerY = height / 2.0;

            double rawX = (screenX - centerX) / Math.Max(1e-6f, view.Zoom) + view.Pan.x;
            double rawY = (screenY - centerY) / Math.Max(1e-6f, view.Zoom) + view.Pan.y;

            double dx = rawX - centerX;
            double dy = rawY - centerY;
            double r = Math.Sqrt(dx * dx + dy * dy);

            double rMax = ComputeRMax(width, height);
            if (rMax <= 0.0) return false;

            altDeg = 90.0 - 90.0 * r / rMax;
            if (altDeg < 0.0) return false;   // below the horizon ring: not sky

            azDeg = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            if (azDeg < 0.0) azDeg += 360.0;
            return true;
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