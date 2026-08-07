using System;
using System.Collections.Generic;
using UnityEngine;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// One chart point, ready to render. Star and deep-sky points are built ONCE per session
    /// (their equatorial positions never change; the chart is inertial) and mutated only in
    /// their display flags; solar-system body points are rebuilt each refresh from the active
    /// observer's true position.
    ///
    /// THREADING CONTRACT. The persistent star/deep-sky list is read concurrently by the
    /// background refresh raster and the main-thread drag raster. The main thread may rewrite a
    /// point to change IsHighlighted/IsSelectedTarget while either raster reads: every other
    /// field is rewritten with identical bytes, so a torn read can only mix old and new values
    /// of the flag bytes themselves, which draws one marker one frame early or late and nothing
    /// else. Occlusion flags deliberately live OUTSIDE the struct (a parallel byte array swapped
    /// by reference) so the background task never writes the shared list at all.
    /// </summary>
    public struct SkyChartPoint
    {
        public StarTarget Target;
        public double RaDeg;
        public double DecDeg;
        public bool IsHighlighted; // matches the current search filter; only highlighted points are clickable

        // --- Solar-system body points -------------------------------------
        public bool IsBody;
        /// <summary>Marker radius when the body's true disc is too small to draw, px at zoom 1. A star's own size: the body is only saying "I am here".</summary>
        public float BodyMarkerRadiusPx;
        /// <summary>The body's true angular radius from the observer, degrees. Drawn as its real disc whenever that beats the marker.</summary>
        public double BodyAngularRadiusDeg;
        /// <summary>True when the body lies in front of the host body's disc (a transiting moon): drawn above the overlay instead of below it.</summary>
        public bool BodyInFront;
        /// <summary>Sun direction in the disc's local frame: x along +RA, y along +Dec, z toward the observer. Drives the terminator on a real-size disc.</summary>
        public Vector3 BodySunLocal;
        public Color BodyColor;
        /// <summary>True only for the body currently selected as the photography target, the sole condition that draws its ring.</summary>
        public bool IsSelectedTarget;

        // --- Deep-sky points ----------------------------------------------
        public bool IsDeepSky;
        public DeepSkyObject DeepSky;

        // --- Cached render data -------------------------------------------
        // Function of the point alone, not of zoom/pan/time; filled once by Prepare. The raster
        // pays an affine transform and a fill per point, nothing else.
        internal Vector2 RawPos;          // full-sky projection before zoom/pan
        internal Color32 NormalColor;     // star: unemphasized. body/deep-sky: undimmed.
        internal Color32 AltColor;        // star: search-emphasized. body/deep-sky: search-dimmed.
        internal float BaseMarkerRadius;  // star disc radius at zoom 1
        internal float DeepSkyArmRaw;     // deep-sky cross arm in raw pixels, before zoom and clamping
        internal Vector2 JDec;            // raw px per arc-degree of growing declination (local Jacobian column)
        internal Vector2 JRa;             // raw px per arc-degree of growing right ascension
        internal bool Prepared;
    }

    /// <summary>
    /// Camera state for the sky chart. Pan is the raw-space point that should sit at the centre
    /// of the viewport; zoom 1 fits the whole sky.
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
    /// Renders the full-sky equatorial chart into a pixel buffer: the classic 2:1 all-sky oval
    /// (Hammer projection, see SkyChartProjection for the equations and the parity), equator
    /// along the long axis, poles top and bottom. Star positions are inertial; what moves on
    /// the chart is the observer's own geometry: the body markers and the occlusion overlay.
    /// Unity-dependent by design - kept separate from Core.
    /// </summary>
    public static class SkyChartTexture
    {
        private static readonly Color BackgroundColor = new Color(0.05f, 0.05f, 0.08f, 1f);
        private static readonly Color EquatorColor = new Color(0.3f, 0.3f, 0.35f, 0.8f);
        private static readonly Color GridColor = new Color(0.2f, 0.2f, 0.24f, 0.5f);

        private static readonly Color UnknownTeffTint = new Color(0.85f, 0.85f, 0.92f, 1f);
        private static readonly Color HighlightRingColor = new Color(0.72f, 0.72f, 0.76f, 1f);

        private const double BrightReferenceMagnitude = -1.5;
        private const double FaintReferenceMagnitude = 12.0;
        private const float MinBrightnessFraction = 0.16f;

        private const float DimmedDesaturation = 0.55f;
        private const float DimmedBrightnessFactor = 0.6f;
        private const float MinHighlightedBrightnessFraction = 0.75f;

        // Declination rings; the celestial equator is drawn brighter, the anchor the eye needs.
        private static readonly double[] DeclinationRingsDeg = { -60.0, -30.0, 0.0, 30.0, 60.0 };
        private const double RaSpokeStepDeg = 30.0;

        private static readonly Color32 BackgroundColor32 = BackgroundColor;
        private static readonly Color32 EquatorColor32 = EquatorColor;
        private static readonly Color32 GridColor32 = GridColor;
        private static readonly Color32 HighlightRingColor32 = HighlightRingColor;

        /// <summary>Cull margin for marker-sized points; real-size body discs carry their own extent.</summary>
        private const float MaxMarkerExtentPx = 48f;

        /// <summary>Pulls a raw-space point inside the sky ellipse (with a small margin); identity when it already is. Used to keep pan and declutter nudges on the map.</summary>
        public static Vector2 ClampToSkyEllipse(Vector2 raw, int width, int height, float marginPx)
        {
            SkyChartProjection.EllipseHalfAxes(width, height, out double a, out double b);
            a = Math.Max(1.0, a - marginPx);
            b = Math.Max(1.0, b - marginPx);
            double dx = raw.x - width / 2.0;
            double dy = raw.y - height / 2.0;
            double k = Math.Sqrt(dx * dx / (a * a) + dy * dy / (b * b));
            if (k <= 1.0) return raw;
            return new Vector2((float)(width / 2.0 + dx / k), (float)(height / 2.0 + dy / k));
        }

        private static float ComputeMarkerRadius(float baseRadius, float zoom)
        {
            return Mathf.Min(baseRadius + (zoom - 1f) * 0.9f, 9f);
        }

        private static double ComputeHitRadius(float zoom)
        {
            return Math.Max(8.0, 4.0 + (zoom - 1.0) * 1.2);
        }

        /// <summary>
        /// A buffer of the right size for <see cref="ComputePixels"/>, reusing <paramref name="existing"/>
        /// when it already fits. The caller owns the buffer: the background refresh task and the
        /// main thread's drag render can be in flight at the same time, and each needs a buffer
        /// nobody else is writing (allocating per render was measured as GC hitches under drag).
        /// </summary>
        public static Color32[] EnsureBuffer(Color32[] existing, int width, int height)
        {
            int needed = width * height;
            return existing != null && existing.Length == needed ? existing : new Color32[needed];
        }

        /// <summary>
        /// Fills each point's cached render data (projection, colours, marker size, local axes),
        /// none of which depends on zoom, pan or time. Idempotent; a Prepared point costs one
        /// branch. Runs on whichever thread rasters first, so it stays free of
        /// UnityEngine.Object calls.
        /// </summary>
        public static void Prepare(List<SkyChartPoint> points, int width, int height)
        {
            if (points == null) return;
            double pxPerDeg = SkyChartProjection.RawPixelsPerDegree(width, height);

            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (p.Prepared) continue;

                SkyChartProjection.ProjectRaw(p.RaDeg, p.DecDeg, width, height,
                                              out double rx, out double ry);
                p.RawPos = new Vector2((float)rx, (float)ry);

                SkyChartProjection.LocalBasis(p.RaDeg, p.DecDeg, width, height,
                                              out double jDecX, out double jDecY,
                                              out double jRaX, out double jRaY);
                p.JDec = new Vector2((float)jDecX, (float)jDecY);
                p.JRa = new Vector2((float)jRaX, (float)jRaY);

                if (p.IsBody)
                {
                    p.NormalColor = p.BodyColor;
                    p.AltColor = Dim(p.BodyColor);
                }
                else if (p.IsDeepSky)
                {
                    Color32 c = p.DeepSky.Kind == DeepSkyKind.ReflectionNebula ? ReflectionMarkerColor
                              : p.DeepSky.Kind == DeepSkyKind.Galaxy ? GalaxyMarkerColor
                              : EmissionMarkerColor;
                    p.NormalColor = c;
                    p.AltColor = Dim(c);
                    // Nominal (map-centre) scale; the cross is a glyph, clamped at raster time anyway.
                    p.DeepSkyArmRaw = (float)(p.DeepSky.MajorArcmin / 60.0 * pxPerDeg * 0.5);
                }
                else
                {
                    p.NormalColor = ComputeStarDisplayColor(p.Target, false);
                    p.AltColor = ComputeStarDisplayColor(p.Target, true);
                    p.BaseMarkerRadius = p.Target.ApparentMagnitude < 6.0 ? 2.5f : 1.6f;
                }

                p.Prepared = true;
                points[i] = p;
            }
        }

        /// <summary>
        /// Rasters the full chart. Pure computation, safe on a background Task while no other
        /// thread writes <paramref name="pixels"/>.
        ///
        /// Draw order is the physical depth order: grid, then everything on the celestial sphere
        /// (stars, deep sky), then body discs sitting BEHIND the host body's limb, then the
        /// occlusion overlay (the host body itself, its glare), then bodies in front of it and
        /// all marker-mode bodies. A star behind the host is not drawn at all
        /// (<paramref name="staticOccluded"/>), which is also what makes it unclickable.
        /// </summary>
        public static void ComputePixels(List<SkyChartPoint> staticPoints, byte[] staticOccluded,
                                         List<SkyChartPoint> bodyPoints,
                                         int width, int height, SkyChartView view, bool searchActive,
                                         byte[] overlayRgba, Color32[] pixels)
        {
            FillBackground(pixels, width);
            DrawReferenceGrid(pixels, width, height, view);

            float zoom = view.Zoom;
            float offsetX = width / 2f - view.Pan.x * zoom;
            float offsetY = height / 2f - view.Pan.y * zoom;

            if (staticPoints != null)
            {
                Prepare(staticPoints, width, height);
                bool haveFlags = staticOccluded != null && staticOccluded.Length == staticPoints.Count;

                bool ShouldEmphasize(SkyChartPoint q) => searchActive && q.IsHighlighted;
                for (int i = 0; i < staticPoints.Count; i++)
                {
                    if (haveFlags && staticOccluded[i] != 0) continue;
                    var p = staticPoints[i];
                    if (!p.IsDeepSky && !ShouldEmphasize(p)) PlotStar(pixels, width, height, p, zoom, offsetX, offsetY, false);
                }
                for (int i = 0; i < staticPoints.Count; i++)
                {
                    if (haveFlags && staticOccluded[i] != 0) continue;
                    var p = staticPoints[i];
                    if (!p.IsDeepSky && ShouldEmphasize(p)) PlotStar(pixels, width, height, p, zoom, offsetX, offsetY, true);
                }
                for (int i = 0; i < staticPoints.Count; i++)
                {
                    if (haveFlags && staticOccluded[i] != 0) continue;
                    var p = staticPoints[i];
                    if (p.IsDeepSky) PlotDeepSky(pixels, width, height, p, zoom, offsetX, offsetY, searchActive && !p.IsHighlighted);
                }
            }

            if (bodyPoints != null)
            {
                Prepare(bodyPoints, width, height);
                for (int i = 0; i < bodyPoints.Count; i++)
                {
                    var p = bodyPoints[i];
                    if (!p.BodyInFront)
                        PlotBody(pixels, width, height, p, zoom, offsetX, offsetY, searchActive && !p.IsHighlighted);
                }
            }

            CompositeOverlay(pixels, width, height, view, overlayRgba);

            if (bodyPoints != null)
            {
                for (int i = 0; i < bodyPoints.Count; i++)
                {
                    var p = bodyPoints[i];
                    if (p.BodyInFront)
                        PlotBody(pixels, width, height, p, zoom, offsetX, offsetY, searchActive && !p.IsHighlighted);
                }
            }
        }

        /// <summary>
        /// Blends the raw-space occlusion overlay through the view's affine transform: per screen
        /// pixel, an inverse affine (two multiplies), a bilinear fetch and a source-over blend.
        /// No trigonometry: this runs on the main thread during a drag. Bilinear sampling is what
        /// keeps the limb readable at zoom 15, where one raw pixel spans 15 screen pixels.
        /// </summary>
        private static void CompositeOverlay(Color32[] pixels, int width, int height,
                                             SkyChartView view, byte[] overlay)
        {
            if (overlay == null || overlay.Length != width * height * 4) return;

            float zoom = Math.Max(1e-6f, view.Zoom);
            float invZoom = 1f / zoom;
            float baseX = -width / 2f * invZoom + view.Pan.x;
            float baseY = -height / 2f * invZoom + view.Pan.y;

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                float ry = y * invZoom + baseY;
                int iy0 = Mathf.FloorToInt(ry);
                float fy = ry - iy0;
                if (iy0 < -1 || iy0 >= height) continue;

                for (int x = 0; x < width; x++)
                {
                    float rx = x * invZoom + baseX;
                    int ix0 = Mathf.FloorToInt(rx);
                    if (ix0 < -1 || ix0 >= width) continue;
                    float fx = rx - ix0;

                    int sr = 0, sg = 0, sb = 0, sa = 0;
                    Accumulate(overlay, width, height, ix0, iy0, (1f - fx) * (1f - fy), ref sr, ref sg, ref sb, ref sa);
                    Accumulate(overlay, width, height, ix0 + 1, iy0, fx * (1f - fy), ref sr, ref sg, ref sb, ref sa);
                    Accumulate(overlay, width, height, ix0, iy0 + 1, (1f - fx) * fy, ref sr, ref sg, ref sb, ref sa);
                    Accumulate(overlay, width, height, ix0 + 1, iy0 + 1, fx * fy, ref sr, ref sg, ref sb, ref sa);
                    if (sa == 0) continue;

                    Color32 dst = pixels[row + x];
                    int inv = 255 - sa;
                    pixels[row + x] = new Color32(
                        (byte)((sr + dst.r * inv) / 255),
                        (byte)((sg + dst.g * inv) / 255),
                        (byte)((sb + dst.b * inv) / 255),
                        (byte)Math.Min(255, sa + dst.a * inv / 255));
                }
            }
        }

        /// <summary>One bilinear tap, accumulated premultiplied so the weighted sum blends in one pass.</summary>
        private static void Accumulate(byte[] overlay, int width, int height, int ix, int iy, float w,
                                       ref int sr, ref int sg, ref int sb, ref int sa)
        {
            if (w <= 0f || ix < 0 || ix >= width || iy < 0 || iy >= height) return;
            int o = (iy * width + ix) * 4;
            byte a = overlay[o + 3];
            if (a == 0) return;
            float wa = w * a;
            sr += (int)(overlay[o] * wa / 255f);
            sg += (int)(overlay[o + 1] * wa / 255f);
            sb += (int)(overlay[o + 2] * wa / 255f);
            sa += (int)wa;
        }

        private static bool IsOffScreen(float sx, float sy, int width, int height, float extent)
        {
            return sx < -extent || sx > width + extent || sy < -extent || sy > height + extent;
        }

        /// <summary>Main-thread-only: uploads a computed pixel buffer into a texture (SetPixels32: the buffer is already in RGBA32 layout).</summary>
        public static Texture2D ApplyToTexture(Color32[] pixels, int width, int height, Texture2D existing)
        {
            Texture2D tex = existing;
            if (tex == null || tex.width != width || tex.height != height)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        /// <summary>
        /// Nearest highlighted, unoccluded point within ComputeHitRadius. A star behind the host
        /// body is not drawn and is not clickable; the search list still reaches it. O(n) in raw
        /// space, transforming the one cursor position instead of every point.
        /// </summary>
        public static StarTarget HitTest(List<SkyChartPoint> points, byte[] occluded,
                                         int width, int height, SkyChartView view, int clickX, int clickY)
        {
            if (points == null) return null;
            StarTarget best = null;
            float zoom = Math.Max(1e-6f, view.Zoom);
            Vector2 rawClick = ScreenToRaw(clickX, clickY, width, height, view);
            double rawTolerance = ComputeHitRadius(view.Zoom) / zoom;
            double bestDistSq = rawTolerance * rawTolerance;
            bool haveFlags = occluded != null && occluded.Length == points.Count;

            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (!p.IsHighlighted) continue;
                if (haveFlags && occluded[i] != 0) continue;
                Vector2 raw = RawOf(p, width, height);
                double dx = raw.x - rawClick.x;
                double dy = raw.y - rawClick.y;
                double distSq = dx * dx + dy * dy;
                if (distSq <= bestDistSq)
                {
                    bestDistSq = distSq;
                    best = p.Target;
                }
            }
            return best;
        }

        private static readonly Color32 EmissionMarkerColor = new Color(0.95f, 0.42f, 0.45f, 1f);
        private static readonly Color32 ReflectionMarkerColor = new Color(0.55f, 0.68f, 0.95f, 1f);
        private static readonly Color32 GalaxyMarkerColor = new Color(0.95f, 0.86f, 0.55f, 1f);

        private static void PlotDeepSky(Color32[] pixels, int width, int height, SkyChartPoint p,
                                        float zoom, float offsetX, float offsetY, bool dimmed)
        {
            float sx = p.RawPos.x * zoom + offsetX;
            float sy = p.RawPos.y * zoom + offsetY;
            if (IsOffScreen(sx, sy, width, height, MaxMarkerExtentPx)) return;

            float arm = Mathf.Clamp(p.DeepSkyArmRaw * zoom, 4f, 40f);
            Color32 color = dimmed ? p.AltColor : p.NormalColor;

            float gap = Math.Max(1.5f, arm * 0.35f);
            for (float d = gap; d <= arm; d += 0.5f)
            {
                SetPixel(pixels, width, height, sx + d, sy, color);
                SetPixel(pixels, width, height, sx - d, sy, color);
                SetPixel(pixels, width, height, sx, sy + d, color);
                SetPixel(pixels, width, height, sx, sy - d, color);
            }

            if (p.IsSelectedTarget)
                DrawHighlightRing(pixels, width, height, sx, sy, arm, zoom);
        }

        private static Color32 Dim(Color32 color)
        {
            Color c = color;
            float grey = c.grayscale;
            return new Color(Mathf.Lerp(c.r, grey, DimmedDesaturation) * DimmedBrightnessFactor,
                             Mathf.Lerp(c.g, grey, DimmedDesaturation) * DimmedBrightnessFactor,
                             Mathf.Lerp(c.b, grey, DimmedDesaturation) * DimmedBrightnessFactor,
                             c.a);
        }

        private static void SetPixel(Color32[] pixels, int width, int height, float x, float y, Color32 color)
        {
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            if (ix < 0 || ix >= width || iy < 0 || iy >= height) return;
            pixels[iy * width + ix] = color;
        }

        public static bool HitTestDeepSky(List<SkyChartPoint> points, byte[] occluded,
                                          int width, int height,
                                          SkyChartView view, int clickX, int clickY, out DeepSkyObject hit)
        {
            hit = default(DeepSkyObject);
            if (points == null) return false;
            bool found = false;
            double bestDistSq = double.MaxValue;
            float zoom = Math.Max(1e-6f, view.Zoom);
            Vector2 rawClick = ScreenToRaw(clickX, clickY, width, height, view);
            double pxPerDeg = SkyChartProjection.RawPixelsPerDegree(width, height);
            bool haveFlags = occluded != null && occluded.Length == points.Count;

            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (!p.IsDeepSky) continue;
                if (haveFlags && occluded[i] != 0) continue;
                Vector2 raw = RawOf(p, width, height);
                double armRaw = p.Prepared
                    ? p.DeepSkyArmRaw
                    : p.DeepSky.MajorArcmin / 60.0 * pxPerDeg * 0.5;
                double arm = Math.Max(4.0, Math.Min(40.0, armRaw * zoom));
                double tolerance = Math.Max(ComputeHitRadius(view.Zoom), arm) / zoom;
                double dx = raw.x - rawClick.x, dy = raw.y - rawClick.y;
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

        private static void PlotStar(Color32[] pixels, int width, int height, SkyChartPoint p,
                                     float zoom, float offsetX, float offsetY, bool emphasize)
        {
            float sx = p.RawPos.x * zoom + offsetX;
            float sy = p.RawPos.y * zoom + offsetY;
            if (IsOffScreen(sx, sy, width, height, MaxMarkerExtentPx)) return;

            float radius = ComputeMarkerRadius(p.BaseMarkerRadius, zoom);
            DrawFilledCircle(pixels, width, height, sx, sy, radius, emphasize ? p.AltColor : p.NormalColor);
            if (emphasize || p.IsSelectedTarget)
            {
                DrawHighlightRing(pixels, width, height, sx, sy, radius, zoom);
            }
        }

        /// <summary>
        /// A solar-system body: its real disc whenever the true angular size wins over the
        /// marker at this zoom, a star-sized dot otherwise. The real disc is drawn as the exact
        /// local footprint of the spherical cap: each pixel offset is pulled back to arc
        /// coordinates through the inverse of the projection's local Jacobian
        /// (SkyChartProjection.LocalBasis; Hammer shears and scales anisotropically off-centre),
        /// and shaded with its real terminator from the Sun's direction.
        /// </summary>
        private static void PlotBody(Color32[] pixels, int width, int height, SkyChartPoint p,
                                     float zoom, float offsetX, float offsetY, bool dimmed)
        {
            float sx = p.RawPos.x * zoom + offsetX;
            float sy = p.RawPos.y * zoom + offsetY;

            // Screen-space Jacobian columns of one arc-degree along Dec and RA at this body.
            float jdx = p.JDec.x * zoom, jdy = p.JDec.y * zoom;
            float jrx = p.JRa.x * zoom, jry = p.JRa.y * zoom;
            float alpha = (float)p.BodyAngularRadiusDeg;
            // The disc's largest screen extent is bounded by alpha times the Jacobian's norm.
            float jNorm = Mathf.Sqrt(jdx * jdx + jdy * jdy + jrx * jrx + jry * jry);
            float realExtent = alpha * jNorm;
            float markerRadius = Mathf.Min(ComputeMarkerRadius(p.BodyMarkerRadiusPx, zoom), 9f);

            if (realExtent < markerRadius)
            {
                if (IsOffScreen(sx, sy, width, height, MaxMarkerExtentPx)) return;
                DrawFilledCircle(pixels, width, height, sx, sy, markerRadius,
                                 dimmed ? p.AltColor : p.NormalColor);
                if (p.IsSelectedTarget)
                    DrawHighlightRing(pixels, width, height, sx, sy, markerRadius, zoom);
                return;
            }

            float extent = realExtent + 2f;
            if (IsOffScreen(sx, sy, width, height, extent)) return;

            float det = jdx * jry - jdy * jrx;
            if (Mathf.Abs(det) < 1e-9f) return;
            float invDet = 1f / det;

            Color32 tint = dimmed ? p.AltColor : p.NormalColor;
            Vector3 sun = p.BodySunLocal;

            int minX = Math.Max(0, Mathf.FloorToInt(sx - extent));
            int maxX = Math.Min(width - 1, Mathf.CeilToInt(sx + extent));
            int minY = Math.Max(0, Mathf.FloorToInt(sy - extent));
            int maxY = Math.Min(height - 1, Mathf.CeilToInt(sy + extent));
            float invAlpha = 1f / alpha;

            for (int y = minY; y <= maxY; y++)
            {
                int rowOffset = y * width;
                float oy = y - sy;
                for (int x = minX; x <= maxX; x++)
                {
                    float ox = x - sx;
                    // (ox,oy) = u*JDec + v*JRa solved for the arc offsets (u,v), in degrees;
                    // u pairs with JDec so u is the Dec-arc offset.
                    float u = (ox * jry - oy * jrx) * invDet;
                    float v = (oy * jdx - ox * jdy) * invDet;
                    float nDec = u * invAlpha;
                    float nRa = v * invAlpha;
                    float rho2 = nDec * nDec + nRa * nRa;
                    if (rho2 > 1f) continue;

                    float nLos = Mathf.Sqrt(1f - rho2);
                    float lit = nRa * sun.x + nDec * sun.y + nLos * sun.z;
                    float dayBlend = Mathf.SmoothStep(0f, 1f, (lit + 0.05f) / 0.20f);
                    float lambert = 0.30f + 0.70f * Mathf.Max(0f, lit);
                    float shade = 0.045f + (lambert - 0.045f) * dayBlend;

                    pixels[rowOffset + x] = new Color32(
                        (byte)(tint.r * shade), (byte)(tint.g * shade), (byte)(tint.b * shade), 255);
                }
            }

            if (p.IsSelectedTarget)
                DrawHighlightRing(pixels, width, height, sx, sy, extent, zoom);
        }

        private static Color ComputeStarDisplayColor(StarTarget target, bool highlighted)
        {
            float r, g, b;
            if (target.EffectiveTempK.HasValue)
            {
                BlackbodyTint(target.EffectiveTempK.Value, out r, out g, out b);
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

        // Blackbody tint memo: one 471-sample Planck integral per distinct temperature per
        // session, keyed at 1 K (measured to move the tint by at most 0.13 of an 8-bit level).
        private const double TintQuantumK = 1.0;
        private static readonly Dictionary<int, Color> blackbodyTintCache = new Dictionary<int, Color>();

        private static void BlackbodyTint(double teffK, out float r, out float g, out float b)
        {
            int bucket = (int)Math.Round(Math.Max(1.0, teffK) / TintQuantumK);
            Color tint;
            lock (blackbodyTintCache)
            {
                if (!blackbodyTintCache.TryGetValue(bucket, out tint))
                {
                    StellarColor.BlackbodyRgb(bucket * TintQuantumK, out double rd, out double gd, out double bd);
                    tint = new Color((float)rd, (float)gd, (float)bd, 1f);
                    blackbodyTintCache[bucket] = tint;
                }
            }
            r = tint.r; g = tint.g; b = tint.b;
        }

        private static float ComputeApparentBrightnessFraction(double magnitude)
        {
            double t = (FaintReferenceMagnitude - magnitude) / (FaintReferenceMagnitude - BrightReferenceMagnitude);
            t = Math.Min(1.0, Math.Max(0.0, t));
            return (float)(MinBrightnessFraction + (1.0 - MinBrightnessFraction) * t);
        }

        private static void DrawHighlightRing(Color32[] pixels, int width, int height, float cx, float cy, float innerRadius, float zoom)
        {
            float thickness = Mathf.Max(1f, 0.6f + (zoom - 1f) * 0.12f);
            float ringInner = innerRadius + 0.8f;
            float ringOuter = ringInner + thickness;
            float innerSq = ringInner * ringInner;
            float outerSq = ringOuter * ringOuter;

            int minX = Math.Max(0, Mathf.FloorToInt(cx - ringOuter));
            int maxX = Math.Min(width - 1, Mathf.CeilToInt(cx + ringOuter));
            int minY = Math.Max(0, Mathf.FloorToInt(cy - ringOuter));
            int maxY = Math.Min(height - 1, Mathf.CeilToInt(cy + ringOuter));

            for (int y = minY; y <= maxY; y++)
            {
                int row = y * width;
                float dy = y - cy;
                float dySq = dy * dy;
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - cx;
                    float distSq = dx * dx + dySq;
                    if (distSq < innerSq || distSq > outerSq) continue;
                    pixels[row + x] = HighlightRingColor32;
                }
            }
        }

        private static void DrawFilledCircle(Color32[] pixels, int width, int height, float cx, float cy, float radius, Color32 color)
        {
            int minX = Math.Max(0, Mathf.FloorToInt(cx - radius));
            int maxX = Math.Min(width - 1, Mathf.CeilToInt(cx + radius));
            int minY = Math.Max(0, Mathf.FloorToInt(cy - radius));
            int maxY = Math.Min(height - 1, Mathf.CeilToInt(cy + radius));
            float radiusSq = radius * radius;

            for (int y = minY; y <= maxY; y++)
            {
                int row = y * width;
                float dy = y - cy;
                float dySq = dy * dy;
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - cx;
                    if (dx * dx + dySq <= radiusSq)
                    {
                        pixels[row + x] = color;
                    }
                }
            }
        }

        /// <summary>
        /// Public wrapper over the projection + view transform, so overlays and hit lists land at
        /// exactly the same place as the baked points. Texture-pixel space (origin bottom-left, y up).
        /// </summary>
        public static Vector2 ProjectEquatorialToScreen(double raDeg, double decDeg, int width, int height, SkyChartView view)
        {
            SkyChartProjection.ProjectRaw(raDeg, decDeg, width, height, out double rx, out double ry);
            return new Vector2(((float)rx - view.Pan.x) * view.Zoom + width / 2f,
                               ((float)ry - view.Pan.y) * view.Zoom + height / 2f);
        }

        /// <summary>Raw pixel back to RA/Dec, for the marker decluttering that nudges in raw space.</summary>
        public static void UnprojectRawToEquatorial(double rawX, double rawY, int width, int height,
                                                    out double raDeg, out double decDeg)
        {
            if (!SkyChartProjection.TryUnprojectRaw(rawX, rawY, width, height, out raDeg, out decDeg))
            {
                raDeg = 0.0;
                decDeg = -90.0;
            }
        }

        private static Vector2 RawOf(SkyChartPoint p, int width, int height)
        {
            if (p.Prepared) return p.RawPos;
            SkyChartProjection.ProjectRaw(p.RaDeg, p.DecDeg, width, height, out double rx, out double ry);
            return new Vector2((float)rx, (float)ry);
        }

        private static Vector2 ScreenToRaw(float screenX, float screenY, int width, int height, SkyChartView view)
        {
            float zoom = Math.Max(1e-6f, view.Zoom);
            return new Vector2((screenX - width / 2f) / zoom + view.Pan.x,
                               (screenY - height / 2f) / zoom + view.Pan.y);
        }

        /// <summary>
        /// Which RA/Dec a chart pixel sits at. Needed to point the telescope at empty sky rather
        /// than only at a marker. False only outside the sky disc (past the south-pole rim).
        /// </summary>
        public static bool TryScreenToEquatorial(float screenX, float screenY, int width, int height,
                                                 SkyChartView view, out double raDeg, out double decDeg)
        {
            Vector2 raw = ScreenToRaw(screenX, screenY, width, height, view);
            return SkyChartProjection.TryUnprojectRaw(raw.x, raw.y, width, height, out raDeg, out decDeg);
        }

        // --- Reference grid ---------------------------------------------------
        // Declination rings and RA spokes, fixed in raw space; only the view transform moves
        // them, so their sample points are projected once per texture size.
        private static int gridCacheWidth = -1, gridCacheHeight = -1;
        private static Vector2[] gridRawPoints;
        private static Color32[] gridColors;
        private static readonly object gridCacheLock = new object();

        private static void EnsureGridCache(int width, int height)
        {
            lock (gridCacheLock)
            {
                if (gridCacheWidth == width && gridCacheHeight == height && gridRawPoints != null) return;

                var pts = new List<Vector2>();
                var cols = new List<Color32>();

                foreach (double decDeg in DeclinationRingsDeg)
                {
                    Color32 color = decDeg == 0.0 ? EquatorColor32 : GridColor32;
                    for (double raDeg = 0; raDeg < 360.0; raDeg += 0.25)
                    {
                        SkyChartProjection.ProjectRaw(raDeg, decDeg, width, height, out double x, out double y);
                        pts.Add(new Vector2((float)x, (float)y));
                        cols.Add(color);
                    }
                }

                // RA meridians, kept clear of the poles so they do not pile up into a blob.
                for (double raDeg = 0.0; raDeg < 360.0; raDeg += RaSpokeStepDeg)
                {
                    for (double decDeg = -84.0; decDeg <= 84.0; decDeg += 0.5)
                    {
                        SkyChartProjection.ProjectRaw(raDeg, decDeg, width, height, out double x, out double y);
                        pts.Add(new Vector2((float)x, (float)y));
                        cols.Add(GridColor32);
                    }
                }

                // The ellipse rim: the map's own edge (RA 0h on both sides), drawn like the old
                // chart drew its horizon, so the sky visibly ends where it ends.
                SkyChartProjection.EllipseHalfAxes(width, height, out double haw, out double hah);
                for (double t = 0.0; t < 360.0; t += 0.2)
                {
                    double tr = t * Math.PI / 180.0;
                    pts.Add(new Vector2((float)(width / 2.0 + haw * Math.Cos(tr)),
                                        (float)(height / 2.0 + hah * Math.Sin(tr))));
                    cols.Add(EquatorColor32);
                }

                gridRawPoints = pts.ToArray();
                gridColors = cols.ToArray();
                gridCacheWidth = width;
                gridCacheHeight = height;
            }
        }

        private static void DrawReferenceGrid(Color32[] pixels, int width, int height, SkyChartView view)
        {
            EnsureGridCache(width, height);
            Vector2[] raw = gridRawPoints;
            Color32[] colors = gridColors;
            if (raw == null) return;

            float zoom = view.Zoom;
            float offsetX = width / 2f - view.Pan.x * zoom;
            float offsetY = height / 2f - view.Pan.y * zoom;

            for (int i = 0; i < raw.Length; i++)
            {
                int x = (int)(raw[i].x * zoom + offsetX);
                int y = (int)(raw[i].y * zoom + offsetY);
                SetPixelSafe(pixels, width, height, x, y, colors[i]);
            }
        }

        private static void FillBackground(Color32[] pixels, int width)
        {
            int seed = Math.Min(width, pixels.Length);
            for (int i = 0; i < seed; i++) pixels[i] = BackgroundColor32;

            int filled = seed;
            while (filled < pixels.Length)
            {
                int n = Math.Min(filled, pixels.Length - filled);
                Array.Copy(pixels, 0, pixels, filled, n);
                filled += n;
            }
        }

        private static void SetPixelSafe(Color32[] pixels, int width, int height, int x, int y, Color32 color)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            pixels[y * width + x] = color;
        }
    }
}
