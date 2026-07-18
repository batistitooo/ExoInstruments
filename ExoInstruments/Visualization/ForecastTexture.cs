using System;
using UnityEngine;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// Renders an ObservingForecast grid as a heatmap: one row per night (top =
    /// tonight), one column per time-of-day slot. A continuous porkchop-plot-style
    /// gradient (dark red -> orange -> yellow -> green -> blue, worst to best)
    /// runs across the whole grid rather than stopping at the observable
    /// boundary: unobservable cells get the coldest, darkest red on the same
    /// scale -- a deliberate, readable "closed" color instead of a near-black
    /// void that could pass for a rendering gap. Unity-dependent by design,
    /// same split as the other texture classes: Core computes, this class only
    /// paints.
    /// </summary>
    public static class ForecastTexture
    {
        // Low-to-high quality gradient, porkchop-plot convention (delta-v plots
        // run the same ramp the other direction: red field = infeasible, deep
        // blue = the best transfer). Interpolated as one continuous ramp so the
        // "closed" cells read as the bottom of the same scale, not a separate palette.
        private static readonly Color[] GradientStops =
        {
            new Color(0.55f, 0.09f, 0.08f, 1f),  // red -- worst / floor
            new Color(0.83f, 0.40f, 0.10f, 1f),  // orange
            new Color(0.88f, 0.78f, 0.20f, 1f),  // yellow
            new Color(0.30f, 0.68f, 0.42f, 1f),  // green
            new Color(0.22f, 0.52f, 0.80f, 1f),  // cyan-blue
            new Color(0.32f, 0.30f, 0.78f, 1f),  // deep blue/violet -- best
        };

        // Unobservable cells: same hue as the gradient floor, darkened further --
        // reads as "off the bottom of the scale, deliberately", not a void.
        private static readonly Color UnobservableColor = new Color(
            GradientStops[0].r * 0.45f, GradientStops[0].g * 0.45f, GradientStops[0].b * 0.45f, 1f);

        private static readonly Color RowSeparatorColor = new Color(0.02f, 0.02f, 0.03f, 1f);

        /// <summary>
        /// Rasterizes the grid into a pixel buffer (pure math, safe off the main
        /// thread). Row 0 of the forecast (tonight) is drawn at the TOP of the
        /// image; the pixel array is bottom-up as Unity expects.
        /// </summary>
        public static Color[] ComputePixels(ObservingForecast.ForecastResult forecast, int width, int height)
        {
            var pixels = new Color[width * height];
            int rows = forecast.Rows;
            int cols = forecast.Columns;
            float rowHeight = (float)height / rows;

            for (int py = 0; py < height; py++)
            {
                // Flip: top pixel row = forecast row 0 (tonight).
                int row = (int)((height - 1 - py) / rowHeight);
                if (row >= rows) row = rows - 1;

                // 1-pixel separator line at each row boundary keeps nights readable.
                bool separator = py + 1 < height && (int)((height - 2 - py) / rowHeight) != row;

                for (int px = 0; px < width; px++)
                {
                    int col = (int)((double)px / width * cols);
                    if (col >= cols) col = cols - 1;
                    pixels[py * width + px] = separator
                        ? RowSeparatorColor
                        : MapQuality(forecast.Quality01[row * cols + col]);
                }
            }
            return pixels;
        }

        /// <summary>Grid cell under an image-local pixel (x right, y DOWN from the top -- IMGUI convention); false when out of bounds.</summary>
        public static bool TryHitCell(ObservingForecast.ForecastResult forecast, int width, int height, float x, float yDown, out int row, out int col)
        {
            row = col = 0;
            if (x < 0 || x >= width || yDown < 0 || yDown >= height) return false;
            row = (int)(yDown / height * forecast.Rows);
            col = (int)(x / width * forecast.Columns);
            if (row >= forecast.Rows) row = forecast.Rows - 1;
            if (col >= forecast.Columns) col = forecast.Columns - 1;
            return true;
        }

        private static Color MapQuality(double quality01)
        {
            if (quality01 <= 0.0) return UnobservableColor;
            float q = (float)Math.Min(1.0, quality01);
            // Perceptual-ish ramp: sqrt lifts the low end so a barely-open
            // window is still visibly distinct from the gradient floor.
            q = Mathf.Sqrt(q);
            return SampleGradient(q);
        }

        /// <summary>Continuous interpolation across GradientStops -- N-1 evenly spaced segments spanning [0, 1].</summary>
        private static Color SampleGradient(float t)
        {
            t = Mathf.Clamp01(t);
            int segments = GradientStops.Length - 1;
            float scaled = t * segments;
            int i = Mathf.Min(segments - 1, (int)scaled);
            float local = scaled - i;
            return Color.Lerp(GradientStops[i], GradientStops[i + 1], local);
        }

        /// <summary>Uploads a pixel buffer into a reusable Texture2D -- same idiom as SkyChartTexture.ApplyToTexture.</summary>
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
    }
}
