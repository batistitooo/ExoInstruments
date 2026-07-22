using System;
using System.Collections.Generic;
using System.Linq;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Cosmetic declutter pass: caps how many planet hosts survive in each coarse sky cell.
    /// Without it the Kepler field (~115 deg²) forms an obvious clump that breaks fog-of-war.
    /// Selection within an over-full cell is by a stable hash of CatalogKey — deterministic
    /// and independent of file order, so a star's presence never changes between sessions.
    /// </summary>
    public static class CatalogDensityThinner
    {
        private const double DefaultCellSizeDeg = 4.0;
        private const int DefaultMaxPerCell = 6;

        public static List<StarTarget> Thin(List<StarTarget> targets, double cellSizeDeg = DefaultCellSizeDeg, int maxPerCell = DefaultMaxPerCell)
        {
            var withoutCoords = new List<StarTarget>();
            var cells = new Dictionary<(int raCell, int decCell), List<StarTarget>>();

            foreach (var t in targets)
            {
                if (!t.RaDeg.HasValue || !t.DecDeg.HasValue)
                {
                    withoutCoords.Add(t);
                    continue;
                }
                var key = CellKey(t.RaDeg.Value, t.DecDeg.Value, cellSizeDeg);
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<StarTarget>();
                list.Add(t);
            }

            var kept = new List<StarTarget>(withoutCoords);
            foreach (var cellTargets in cells.Values)
            {
                if (cellTargets.Count <= maxPerCell)
                {
                    kept.AddRange(cellTargets);
                    continue;
                }
                kept.AddRange(cellTargets
                    .OrderBy(t => StableHash(t.CatalogKey ?? t.Name))
                    .Take(maxPerCell));
            }
            return kept;
        }

        private static (int, int) CellKey(double raDeg, double decDeg, double cellSizeDeg)
        {
            int raCell = (int)Math.Floor(raDeg / cellSizeDeg);
            int decCell = (int)Math.Floor(decDeg / cellSizeDeg);
            return (raCell, decCell);
        }

        // FNV-1a: cheap, stable across runs/platforms, good-enough distribution for this cosmetic use.
        private static uint StableHash(string s)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in s) hash = (hash ^ c) * 16777619;
                return hash;
            }
        }
    }
}
