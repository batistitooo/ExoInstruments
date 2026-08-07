using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The host body's cap on the sky chart, drawn as the body itself: the exact spherical cap it
    /// occults (angular radius asin(R/d) from the observer's true position), shaded by ray-sphere
    /// intersection so the disc carries its real day/night terminator, plus the published
    /// avoidance angles rendered as light: a glow hugging the limb out to the bright/dark limb
    /// avoidance angle, wider and warmer on the sunlit side, because scattered sunlight is the
    /// physical reason those angles exist.
    /// </summary>
    public struct OverlayHost
    {
        public bool HasBody;
        /// <summary>Unit direction from the observer to the body's centre, equatorial frame.</summary>
        public SkyVector Direction;
        public double AngularRadiusDeg;
        /// <summary>Unit direction from the body toward the Sun (parallel-ray lighting).</summary>
        public SkyVector SunDirection;
        public byte TintR, TintG, TintB;
        /// <summary>Boresight-to-limb avoidance angles, degrees; zero draws no glow (a ground site has none published).</summary>
        public double SunlitLimbGlowDeg;
        public double DarkLimbGlowDeg;
    }

    /// <summary>A radial halo: full strength at InnerDeg, fading to nothing at OuterDeg. Used for the solar avoidance cone and moon avoidance rings.</summary>
    public struct OverlayGlow
    {
        public SkyVector Direction;
        public double InnerDeg;
        public double OuterDeg;
        public byte R, G, B;
        /// <summary>Peak opacity at InnerDeg, 0-255.</summary>
        public byte PeakAlpha;
    }

    /// <summary>
    /// Renders the occlusion overlay into a raw-space RGBA byte buffer (Color32 layout), on the
    /// background refresh task. The raster then samples it bilinearly through the same affine
    /// view transform as everything else, so the main-thread drag path pays array lookups and no
    /// trigonometry. Raw space is fixed, so the per-pixel sky directions are computed once per
    /// buffer size and cached, leaving the per-refresh render as dot products.
    /// </summary>
    public static class SkyChartOverlayRenderer
    {
        private const double DegToRad = Math.PI / 180.0;

        // Per-pixel unit sky directions for the fixed raw grid; zero vector = outside the sky disc.
        private static float[] dirX, dirY, dirZ;
        private static int dirWidth = -1, dirHeight = -1;
        private static readonly object dirLock = new object();

        public static byte[] EnsureBuffer(byte[] existing, int width, int height)
        {
            int needed = width * height * 4;
            return existing != null && existing.Length == needed ? existing : new byte[needed];
        }

        private static void EnsureDirections(int width, int height)
        {
            lock (dirLock)
            {
                if (dirWidth == width && dirHeight == height && dirX != null) return;
                int n = width * height;
                var dx = new float[n];
                var dy = new float[n];
                var dz = new float[n];
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        if (SkyChartProjection.TryUnprojectRaw(x, y, width, height,
                                                               out double ra, out double dec))
                        {
                            SkyVector d = SkyChartProjection.DirectionFromEquatorial(ra, dec);
                            dx[row + x] = (float)d.X;
                            dy[row + x] = (float)d.Y;
                            dz[row + x] = (float)d.Z;
                        }
                    }
                }
                dirX = dx; dirY = dy; dirZ = dz;
                dirWidth = width; dirHeight = height;
            }
        }

        public static void Render(byte[] rgba, int width, int height,
                                  in OverlayHost host, OverlayGlow[] glows)
        {
            Array.Clear(rgba, 0, width * height * 4);
            EnsureDirections(width, height);
            float[] dx = dirX, dy = dirY, dz = dirZ;

            bool hasHost = host.HasBody && host.AngularRadiusDeg > 0.0;
            double alphaRad = host.AngularRadiusDeg * DegToRad;
            double cosAlpha = Math.Cos(alphaRad);
            double sinAlpha = Math.Sin(alphaRad);
            // Widest angular reach of the host's disc plus its glow, as a cheap reject.
            double hostReachCos = hasHost
                ? Math.Cos(Math.Min(180.0, host.AngularRadiusDeg
                    + Math.Max(host.SunlitLimbGlowDeg, host.DarkLimbGlowDeg)) * DegToRad)
                : 2.0;

            double cx = host.Direction.X, cy = host.Direction.Y, cz = host.Direction.Z;
            double sx = host.SunDirection.X, sy = host.SunDirection.Y, sz = host.SunDirection.Z;

            int glowCount = glows != null ? glows.Length : 0;

            for (int i = 0, n = width * height; i < n; i++)
            {
                float ux = dx[i], uy = dy[i], uz = dz[i];
                if (ux == 0f && uy == 0f && uz == 0f) continue;

                // Premultiplied accumulation: light adds, the disc stays opaque under it.
                double pr = 0, pg = 0, pb = 0, pa = 0;

                if (hasHost)
                {
                    double cosSep = ux * cx + uy * cy + uz * cz;
                    if (cosSep > hostReachCos)
                    {
                        if (cosSep >= cosAlpha)
                        {
                            ShadeDisc(cosSep, ux, uy, uz, in host, sinAlpha,
                                      out double r, out double g, out double b);
                            pr = r; pg = g; pb = b; pa = 1.0;
                        }
                        else
                        {
                            AddLimbGlow(cosSep, ux, uy, uz, in host, alphaRad, sinAlpha, cosAlpha,
                                        ref pr, ref pg, ref pb, ref pa);
                        }
                    }
                }

                for (int gi = 0; gi < glowCount; gi++)
                {
                    OverlayGlow glow = glows[gi];
                    double cosSep = ux * glow.Direction.X + uy * glow.Direction.Y + uz * glow.Direction.Z;
                    double sepDeg = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosSep))) / DegToRad;
                    if (sepDeg >= glow.OuterDeg || glow.OuterDeg <= glow.InnerDeg) continue;
                    double t = Math.Max(0.0, (sepDeg - glow.InnerDeg) / (glow.OuterDeg - glow.InnerDeg));
                    double fall = (1.0 - t) * (1.0 - t);
                    double a = glow.PeakAlpha / 255.0 * fall;
                    pr = Math.Min(1.0, pr + glow.R / 255.0 * a);
                    pg = Math.Min(1.0, pg + glow.G / 255.0 * a);
                    pb = Math.Min(1.0, pb + glow.B / 255.0 * a);
                    pa = 1.0 - (1.0 - pa) * (1.0 - a);
                }

                if (pa <= 0.0) continue;
                int o = i * 4;
                // Straight-alpha bytes, colour un-premultiplied for the raster's src-over blend.
                rgba[o] = (byte)(Math.Min(1.0, pr / pa) * 255.0 + 0.5);
                rgba[o + 1] = (byte)(Math.Min(1.0, pg / pa) * 255.0 + 0.5);
                rgba[o + 2] = (byte)(Math.Min(1.0, pb / pa) * 255.0 + 0.5);
                rgba[o + 3] = (byte)(Math.Min(1.0, pa) * 255.0 + 0.5);
            }
        }

        /// <summary>
        /// Lambert-shaded sphere point under this pixel: the near intersection of the sight line
        /// with the body (unit observer distance, radius sin(alpha) - the geometry is scale-free),
        /// its outward normal, and the Sun. The terminator falls where the normal turns from the
        /// Sun, exactly where the real one is.
        /// </summary>
        private static void ShadeDisc(double cosSep, double ux, double uy, double uz,
                                      in OverlayHost host, double sinAlpha,
                                      out double r, out double g, out double b)
        {
            double radius = sinAlpha;
            double sinSep2 = Math.Max(0.0, 1.0 - cosSep * cosSep);
            double inside = Math.Max(0.0, radius * radius - sinSep2);
            double t = cosSep - Math.Sqrt(inside);

            double nx = (t * ux - host.Direction.X) / radius;
            double ny = (t * uy - host.Direction.Y) / radius;
            double nz = (t * uz - host.Direction.Z) / radius;

            double lit = nx * host.SunDirection.X + ny * host.SunDirection.Y + nz * host.SunDirection.Z;
            double dayBlend = SmoothStep((lit + 0.05) / 0.20);
            double lambert = 0.30 + 0.70 * Math.Max(0.0, lit);
            double shade = 0.045 + (lambert - 0.045) * dayBlend;

            r = host.TintR / 255.0 * shade;
            g = host.TintG / 255.0 * shade;
            b = host.TintB / 255.0 * shade;
        }

        /// <summary>
        /// The glow past the limb, out to the avoidance angle for the limb the pixel is actually
        /// nearest: the tangent point is constructed (same construction as
        /// OrbitalVisibility.NearestLimbIsSunlit) and its illumination blends the wide warm
        /// sunlit-limb glow into the narrow cool dark-limb one across the terminator.
        /// </summary>
        private static void AddLimbGlow(double cosSep, double ux, double uy, double uz,
                                        in OverlayHost host, double alphaRad, double sinAlpha, double cosAlpha,
                                        ref double pr, ref double pg, ref double pb, ref double pa)
        {
            double sepRad = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosSep)));
            double limbDeg = (sepRad - alphaRad) / DegToRad;

            // In-plane unit vector from the body centre direction toward this pixel.
            double px = ux - cosSep * host.Direction.X;
            double py = uy - cosSep * host.Direction.Y;
            double pz = uz - cosSep * host.Direction.Z;
            double pm = Math.Sqrt(px * px + py * py + pz * pz);
            if (pm < 1e-9) return;
            px /= pm; py /= pm; pz /= pm;

            // Outward normal of the limb point nearest this pixel: sin(alpha) along the outward
            // observer direction (-centre), cos(alpha) along the in-plane vector.
            double nx = -sinAlpha * host.Direction.X + cosAlpha * px;
            double ny = -sinAlpha * host.Direction.Y + cosAlpha * py;
            double nz = -sinAlpha * host.Direction.Z + cosAlpha * pz;
            double lit = nx * host.SunDirection.X + ny * host.SunDirection.Y + nz * host.SunDirection.Z;
            double sunlitBlend = Clamp01(lit * 4.0 + 0.5);

            double glowDeg = host.DarkLimbGlowDeg + (host.SunlitLimbGlowDeg - host.DarkLimbGlowDeg) * sunlitBlend;
            if (glowDeg <= 0.0 || limbDeg >= glowDeg) return;

            double fall = 1.0 - limbDeg / glowDeg;
            fall *= fall;
            // Warm scattered sunlight against a cool starlit limb.
            double cr = 1.00 * sunlitBlend + 0.45 * (1.0 - sunlitBlend);
            double cg = 0.93 * sunlitBlend + 0.55 * (1.0 - sunlitBlend);
            double cb = 0.80 * sunlitBlend + 0.75 * (1.0 - sunlitBlend);
            double peak = 0.80 * sunlitBlend + 0.35 * (1.0 - sunlitBlend);
            double a = peak * fall;

            pr = Math.Min(1.0, pr + cr * a);
            pg = Math.Min(1.0, pg + cg * a);
            pb = Math.Min(1.0, pb + cb * a);
            pa = 1.0 - (1.0 - pa) * (1.0 - a);
        }

        private static double SmoothStep(double t)
        {
            t = Clamp01(t);
            return t * t * (3.0 - 2.0 * t);
        }

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);
    }
}
