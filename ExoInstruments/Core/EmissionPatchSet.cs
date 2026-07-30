using System;
using System.Collections.Generic;
using System.IO;

namespace ExoInstruments.Core
{
    /// <summary>
    /// High-resolution patches of one emission line, over the sky where a finer survey exists.
    ///
    /// WHY THIS LAYER EXISTS. The all-sky H-alpha composite has a 6 arcmin beam, and every structure
    /// that makes a nebula recognisable is finer than that: the Horsehead spans 1.3 beams, M42's
    /// Trapezium 0.8, the filaments in IC 1396A 0.3. No processing recovers them, because the
    /// information is not in the file. A finer survey does exist over part of the sky; SHASSA
    /// (Gaustad et al. 2001, PASP 113, 1326) images everything south of +15 degrees at 0.8 arcmin,
    /// and at that beam the Horsehead spans 10 elements instead of 1.3.
    ///
    /// WHY PATCHES AND NOT A FINER ALL-SKY MAP. Resolution is only worth storing where there is
    /// something to resolve. The whole sky at 0.86 arcmin is 201 million cells, 403 MB, of which the
    /// overwhelming majority carries diffuse background that 6 arcmin already describes perfectly
    /// well. Four degrees around each catalogued object is 78 thousand cells apiece, about 5 MB for
    /// the entire catalogue, eighty times smaller for the same result on every target anyone
    /// points at. Outside a patch the base map answers, which is the same layered arrangement every
    /// real survey archive uses.
    ///
    /// STORAGE is run-length by HEALPix ring. In RING ordering a disc on the sky cuts each ring in
    /// one contiguous stretch of pixels, so a patch is a few hundred runs rather than a pixel index
    /// per value, which would otherwise cost three times as much as the values themselves.
    ///
    /// The patch carries TOTAL surface brightness, not a correction: the packer folds the composite
    /// in and apodises the fine structure to zero across the patch's outer margin, so a patch joins
    /// the base map continuously and reproduces it exactly when smoothed back to 6 arcmin. What
    /// SHASSA supplies is the structure; the absolute calibration stays the composite's. See
    /// tools/pack_shassa_patches.py.
    ///
    /// NOTHING SHIPS. Pure C#, no Unity dependency.
    /// </summary>
    public sealed class EmissionPatchSet
    {
        private static readonly byte[] Magic = { (byte)'E', (byte)'X', (byte)'O', (byte)'P', (byte)'T', (byte)'C', (byte)'H', (byte)'1' };
        private const int FormatVersion = 1;

        /// <summary>One patch: a disc of sky stored as run-length rows of HEALPix pixels.</summary>
        public sealed class Patch
        {
            public string Name;
            public double CentreRaDeg;
            public double CentreDecDeg;
            public double RadiusDeg;

            internal int[] RunStart;    // HEALPix pixel of each run's first cell
            internal int[] RunLength;
            internal int[] RunOffset;   // index into Values
            internal ushort[] Values;

            // Unit vector of the centre and the cosine of the radius, so the covering test is one
            // dot product rather than an inverse trigonometric function.
            internal double Cx, Cy, Cz, CosRadius;

            /// <summary>Cells the patch holds.</summary>
            public int CellCount => Values != null ? Values.Length : 0;

            internal bool TryValue(long pixel, ref int cursor, out double rayleighs)
            {
                rayleighs = double.NaN;
                if (RunStart == null || pixel < 0 || pixel > int.MaxValue) return false;
                int p = (int)pixel;

                // The cached run, then its neighbours, then a binary search.
                int i = cursor;
                if (i >= 0 && i < RunStart.Length && p >= RunStart[i] && p - RunStart[i] < RunLength[i])
                    return Read(i, p, out rayleighs);

                int lo = 0, hi = RunStart.Length - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (RunStart[mid] <= p) { found = mid; lo = mid + 1; } else hi = mid - 1;
                }
                if (found < 0) return false;
                if (p - RunStart[found] >= RunLength[found]) return false;
                cursor = found;
                return Read(found, p, out rayleighs);
            }

            private bool Read(int run, int pixel, out double rayleighs)
            {
                rayleighs = Float16.ToDouble(Values[RunOffset[run] + (pixel - RunStart[run])]);
                return !double.IsNaN(rayleighs);
            }
        }

        private Patch[] patches;
        private int nside;
        private bool nested;

        public bool IsLoaded => patches != null && patches.Length > 0;
        public int PatchCount => patches != null ? patches.Length : 0;
        public int Nside => nside;
        public double ResolutionArcmin => IsLoaded ? Healpix.PixelResolutionDeg(nside) * 60.0 : 0.0;
        public double LineWavelengthMeters { get; private set; }
        public string LineName { get; private set; }
        public string Source { get; private set; }

        /// <summary>The patches themselves, so a caller can cone-search a star catalogue over each.</summary>
        public IEnumerable<Patch> Patches
        {
            get
            {
                if (patches == null) yield break;
                foreach (Patch p in patches) yield return p;
            }
        }

        /// <summary>Names of the patches, for the load message.</summary>
        public IEnumerable<string> PatchNames
        {
            get
            {
                if (patches == null) yield break;
                foreach (Patch p in patches) yield return p.Name;
            }
        }

        public void Load(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length) throw new InvalidDataException("not an ExoInstruments emission patch set");
                for (int i = 0; i < Magic.Length; i++)
                    if (magic[i] != Magic[i]) throw new InvalidDataException("not an ExoInstruments emission patch set");

                int version = reader.ReadInt32();
                if (version != FormatVersion) throw new InvalidDataException("unsupported patch set version " + version);

                int n = reader.ReadInt32();
                if (!Healpix.IsValidNside(n)) throw new InvalidDataException("bad nside " + n);
                bool isNested = reader.ReadByte() != 0;
                if (isNested) throw new InvalidDataException("patch sets are RING ordered");

                double wavelength = reader.ReadDouble();
                if (!(wavelength > 0.0)) throw new InvalidDataException("bad line wavelength");
                string lineName = ReadString(reader, 256);
                string source = ReadString(reader, 4096);

                int patchCount = reader.ReadInt32();
                if (patchCount < 0 || patchCount > 100000) throw new InvalidDataException("implausible patch count " + patchCount);

                var list = new Patch[patchCount];
                for (int i = 0; i < patchCount; i++)
                {
                    var patch = new Patch
                    {
                        Name = ReadString(reader, 128),
                        CentreRaDeg = reader.ReadDouble(),
                        CentreDecDeg = reader.ReadDouble(),
                        RadiusDeg = reader.ReadSingle(),
                    };

                    int runCount = reader.ReadInt32();
                    if (runCount < 0 || runCount > 10_000_000) throw new InvalidDataException("implausible run count");
                    patch.RunStart = new int[runCount];
                    patch.RunLength = new int[runCount];
                    patch.RunOffset = new int[runCount];

                    // Two passes: the runs' geometry, then their values in one bulk read. Writing
                    // them interleaved would force a read call per run.
                    long total = 0;
                    for (int r = 0; r < runCount; r++)
                    {
                        patch.RunStart[r] = reader.ReadInt32();
                        patch.RunLength[r] = reader.ReadInt32();
                        if (patch.RunLength[r] <= 0) throw new InvalidDataException("empty run");
                        if (r > 0 && patch.RunStart[r] <= patch.RunStart[r - 1])
                            throw new InvalidDataException("patch runs are not sorted by pixel");
                        patch.RunOffset[r] = (int)total;
                        total += patch.RunLength[r];
                        if (total > int.MaxValue) throw new InvalidDataException("patch too large");
                    }
                    patch.Values = EmissionMap.ReadHalfFloats(reader, (int)total);

                    double ra = patch.CentreRaDeg * Math.PI / 180.0;
                    double dec = patch.CentreDecDeg * Math.PI / 180.0;
                    patch.Cx = Math.Cos(dec) * Math.Cos(ra);
                    patch.Cy = Math.Cos(dec) * Math.Sin(ra);
                    patch.Cz = Math.Sin(dec);
                    patch.CosRadius = Math.Cos(patch.RadiusDeg * Math.PI / 180.0);
                    list[i] = patch;
                }

                patches = list;
                nside = n;
                nested = isNested;
                LineWavelengthMeters = wavelength;
                LineName = lineName;
                Source = source;
            }
        }

        private static string ReadString(BinaryReader reader, int limit)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > limit) throw new InvalidDataException("bad string length");
            return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        /// <summary>
        /// The patch nearest an equatorial direction that OVERLAPS a field of the given radius, or
        /// null. Resolved once per frame rather than per pixel: patch centres are degrees apart, so
        /// asking again for every pixel would be a hundred dot products for an answer that does not
        /// change across a frame.
        ///
        /// OVERLAP, NOT CONTAINMENT. This used to demand that the whole field fit inside the patch,
        /// so a wide-field instrument fell back to the base map entirely; the RedCat's 2.7 degree
        /// half-diagonal against M42's 1.13 degree patch meant the one shot that shows the whole
        /// nebula got none of the resolution. Containment was over-cautious: the per-pixel lookup
        /// already falls through to the base map wherever the patch has no cell, and the packer
        /// apodises the patch's fine structure to zero across its outer margin precisely so that
        /// the two agree there. So a frame can straddle the edge and join continuously, which is
        /// what the taper was built for.
        /// </summary>
        public Patch FindCoveringPatch(double raDeg, double decDeg, double fieldRadiusDeg)
        {
            List<Patch> all = FindOverlappingPatches(raDeg, decDeg, fieldRadiusDeg);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>
        /// EVERY patch a field overlaps, nearest first.
        ///
        /// A wide field routinely covers more than one. The Horsehead, the Flame and M42 sit inside
        /// three degrees of each other, so a single RedCat frame spans all three patches; returning
        /// only the nearest would render one of them at 0.86 arcmin and the other two at the base
        /// map's 6, in the same picture, for no reason. The per-pixel lookup already falls through
        /// patch by patch, so a caller can simply try each in turn.
        /// </summary>
        public List<Patch> FindOverlappingPatches(double raDeg, double decDeg, double fieldRadiusDeg)
        {
            var found = new List<Patch>();
            if (patches == null) return found;
            double ra = raDeg * Math.PI / 180.0, dec = decDeg * Math.PI / 180.0;
            double x = Math.Cos(dec) * Math.Cos(ra), y = Math.Cos(dec) * Math.Sin(ra), z = Math.Sin(dec);

            var cosines = new List<double>();
            foreach (Patch p in patches)
            {
                double cos = x * p.Cx + y * p.Cy + z * p.Cz;
                double reach = Math.Cos(Math.Min(180.0, p.RadiusDeg + Math.Max(0.0, fieldRadiusDeg))
                                        * Math.PI / 180.0);
                if (cos < reach) continue;
                int at = cosines.Count;
                while (at > 0 && cosines[at - 1] < cos) at--;
                cosines.Insert(at, cos);
                found.Insert(at, p);
            }
            return found;
        }

        /// <summary>
        /// Per-caller lookup state, so a background frame fill keeps its own run cursor, one per
        /// patch, since a frame may draw from several and they have unrelated run tables.
        /// </summary>
        public struct Cursor
        {
            internal int[] Runs;
            public static Cursor New(int patchCount = 1)
                => new Cursor { Runs = new int[Math.Max(1, patchCount)] };
        }

        /// <summary>
        /// Surface brightness from a patch toward a Galactic direction, bilinearly interpolated over
        /// the same four surrounding pixels the base map uses; see Healpix.InterpolationWeights.
        /// False when any of the four lies outside the patch, which keeps the interpolation from
        /// silently reweighting itself at the boundary.
        /// </summary>
        public bool TryRayleighsAtGalactic(Patch patch, double lDeg, double bDeg,
                                           long[] pixelScratch, double[] weightScratch,
                                           ref Cursor cursor, out double rayleighs)
            => TryRayleighsAtGalactic(patch, 0, lDeg, bDeg, pixelScratch, weightScratch,
                                      ref cursor, out rayleighs);

        /// <summary>As above, with the patch's index in the caller's list so each keeps its own run cursor.</summary>
        /// <summary>How many cells were repaired because they are continuum-subtraction residuals rather than measurements. Reported, never hidden.</summary>
        public int MaskedCellsFilled { get; private set; }

        /// <summary>Brightest star the repair considers, and the radius it masks around one of that magnitude. See RepairSubtractionResiduals.</summary>
        public const double ResidualStarMagnitudeLimit = 4.5;

        /// <summary>
        /// Removes the continuum-subtraction residuals, using the stars that CAUSE them.
        ///
        /// WHY BY STAR AND NOT BY VALUE. SHASSA removes stellar continuum by scaling an off-band
        /// image and subtracting it from the H-alpha one (Gaustad et al. 2001, PASP 113, 1326).
        /// The scaling is one number for a whole field, so it cannot be right for every stellar
        /// colour at once, and on the brightest stars it misses -- too much subtracted leaves a
        /// hole, too little leaves the star itself. Both signs occur and neither is emission.
        ///
        /// Thresholding on value cannot tell those from real structure: an H II region has genuine
        /// knots five times its local median, and a dark globule genuinely goes to a tenth of it.
        /// The residuals are distinguishable by their CAUSE, which is a catalogued object at a
        /// known position. Measured on the Horsehead patch: 154 cells depart from their neighbours
        /// by more than a factor 2.5 either way, 43 of them within 5 arcmin of Alnitak and the
        /// nearest 0.5 arcmin from it, while sigma Ori at V = 3.8, Alnilam at V = 1.69 forty
        /// arcmin outside the patch, and HD 37903 at V = 7.83 have none between them.
        ///
        /// So a cell is repaired only where BOTH hold: it departs from its own neighbours by more
        /// than ResidualContrast, and it lies within the masking radius of a star bright enough to
        /// produce one. Real structure fails the second test wherever it is, and a residual fails
        /// neither.
        ///
        /// THE RADIUS scales as the square root of the star's flux, which is what the residual's
        /// own extent does: the subtraction error at a given radius is a fixed FRACTION of the
        /// stellar profile there, so the radius at which it drops below the sky's own noise grows
        /// as the square root of the total. Anchored on the one star that produced measurable
        /// residuals, 10 arcmin at V = 1.77, and cut off at V = 4.5 where the radius falls to
        /// 1.8 arcmin, about two cells.
        ///
        /// Filled from the surviving neighbours afterwards -- same survey, same calibration, same
        /// resolution, so no seam -- and the count is reported at load. Nothing is claimed to have
        /// been measured there.
        /// </summary>
        public void RepairSubtractionResiduals(IList<double> starRaDeg, IList<double> starDecDeg,
                                               IList<double> starVMag)
        {
            MaskedCellsFilled = 0;
            if (patches == null || starRaDeg == null) return;

            int marked = 0;
            foreach (Patch patch in patches)
            {
                if (patch.Values == null) continue;
                var suspect = new List<int>();
                int cursor = 0;

                for (int s = 0; s < starRaDeg.Count; s++)
                {
                    double v = starVMag[s];
                    if (!(v <= ResidualStarMagnitudeLimit)) continue;

                    double radiusDeg = ResidualRadiusDeg(v);
                    if (!WithinPatch(patch, starRaDeg[s], starDecDeg[s], radiusDeg)) continue;

                    GalacticCoordinates.EquatorialToGalactic(starRaDeg[s], starDecDeg[s],
                                                             out double gl, out double gb);
                    MarkResidualsAround(patch, gl, gb, radiusDeg, ref cursor, suspect);
                }

                if (suspect.Count == 0) continue;
                marked += suspect.Count;
                foreach (int index in suspect) patch.Values[index] = NoValue;
                FillMasked(patch, suspect);
            }
            MaskedCellsFilled = marked;
        }

        /// <summary>A half-float NaN: the map's own "no measurement here".</summary>
        private const ushort NoValue = 0x7E00;

        /// <summary>Masking radius for a star of the given V magnitude, degrees. See RepairSubtractionResiduals for the scaling and its anchor.</summary>
        public static double ResidualRadiusDeg(double vMag)
            => (10.0 / 60.0) * Math.Pow(10.0, -0.2 * (vMag - 1.77));

        /// <summary>How far a cell must depart from its own neighbours, either way, before a star's presence is allowed to condemn it.</summary>
        private const double ResidualContrast = 2.5;

        private static bool WithinPatch(Patch patch, double raDeg, double decDeg, double marginDeg)
        {
            double ra = raDeg * Math.PI / 180.0, dec = decDeg * Math.PI / 180.0;
            double x = Math.Cos(dec) * Math.Cos(ra), y = Math.Cos(dec) * Math.Sin(ra), z = Math.Sin(dec);
            double cosSep = x * patch.Cx + y * patch.Cy + z * patch.Cz;
            double sep = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosSep))) * 180.0 / Math.PI;
            return sep <= patch.RadiusDeg + marginDeg;
        }

        /// <summary>Marks every cell within radiusDeg of a Galactic position whose value departs from its own neighbours by more than ResidualContrast.</summary>
        private void MarkResidualsAround(Patch patch, double lDeg, double bDeg, double radiusDeg,
                                         ref int cursor, List<int> suspect)
        {
            double step = Healpix.PixelResolutionDeg(nside);
            int reach = (int)Math.Ceiling(radiusDeg / step);
            double cosB = Math.Cos(bDeg * Math.PI / 180.0);

            for (int dy = -reach; dy <= reach; dy++)
            {
                double nb = bDeg + dy * step;
                if (nb > 90.0 || nb < -90.0) continue;
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (dx * dx + dy * dy > reach * reach) continue;
                    double nl = lDeg + (Math.Abs(cosB) > 1e-6 ? dx * step / cosB : 0.0);
                    long pixel = Healpix.SphericalDegreesToRing(nside, nl, nb);
                    if (!TryOffset(patch, pixel, ref cursor, out int index)) continue;

                    double value = Float16.ToDouble(patch.Values[index]);
                    double median = NeighbourMedian(patch, nl, nb, step, ref cursor);
                    if (double.IsNaN(median) || !(median > 0.0)) continue;

                    bool residual = !(value > 0.0)
                                 || value > ResidualContrast * median
                                 || value < median / ResidualContrast;
                    if (residual && !suspect.Contains(index)) suspect.Add(index);
                }
            }
        }

        /// <summary>Median of the eight surrounding cells, which is what a single cell has to be judged against.</summary>
        private double NeighbourMedian(Patch patch, double lDeg, double bDeg, double step, ref int cursor)
        {
            var around = new List<double>(8);
            double cosB = Math.Cos(bDeg * Math.PI / 180.0);
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                double nb = bDeg + dy * step;
                if (nb > 90.0 || nb < -90.0) continue;
                double nl = lDeg + (Math.Abs(cosB) > 1e-6 ? dx * step / cosB : 0.0);
                if (!patch.TryValue(Healpix.SphericalDegreesToRing(nside, nl, nb), ref cursor, out double v)) continue;
                if (v > 0.0) around.Add(v);
            }
            if (around.Count == 0) return double.NaN;
            around.Sort();
            return around[around.Count / 2];
        }

        /// <summary>Fills masked cells from the neighbours that survive, iterating from the rim inwards so a cluster closes.</summary>
        private void FillMasked(Patch patch, List<int> masked)
        {
            int cursor = 0;
            var remaining = new List<int>(masked);
            double step = Healpix.PixelResolutionDeg(nside);

            for (int pass = 0; pass < 8 && remaining.Count > 0; pass++)
            {
                var next = new List<int>();
                var updates = new List<KeyValuePair<int, double>>();
                foreach (int index in remaining)
                {
                    long pixel = PixelForOffset(patch, index);
                    if (pixel < 0) continue;
                    Healpix.RingPixelCentreDegrees(nside, pixel, out double l, out double b);
                    double m = NeighbourMedian(patch, l, b, step, ref cursor);
                    if (double.IsNaN(m)) next.Add(index);
                    else updates.Add(new KeyValuePair<int, double>(index, m));
                }
                // Applied after the pass, so a cell filled this pass cannot seed another in the
                // same one, which would make the result depend on the iteration order.
                foreach (var u in updates) patch.Values[u.Key] = Float16.FromDouble(u.Value);
                if (updates.Count == 0) break;
                remaining = next;
            }
        }

        /// <summary>Index into a patch's value array for a HEALPix pixel, or false when the patch does not cover it.</summary>
        private static bool TryOffset(Patch patch, long pixel, ref int cursor, out int index)
        {
            index = -1;
            if (patch.RunStart == null || pixel < 0 || pixel > int.MaxValue) return false;
            int p = (int)pixel;
            int lo = 0, hi = patch.RunStart.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (patch.RunStart[mid] <= p) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (found < 0 || p - patch.RunStart[found] >= patch.RunLength[found]) return false;
            cursor = found;
            index = patch.RunOffset[found] + (p - patch.RunStart[found]);
            return true;
        }

        /// <summary>The HEALPix pixel a position in a patch's packed value array belongs to.</summary>
        private static long PixelForOffset(Patch patch, int offset)
        {
            for (int r = 0; r < patch.RunStart.Length; r++)
            {
                if (offset < patch.RunOffset[r] || offset >= patch.RunOffset[r] + patch.RunLength[r]) continue;
                return patch.RunStart[r] + (offset - patch.RunOffset[r]);
            }
            return -1;
        }

        public bool TryRayleighsAtGalactic(Patch patch, int patchIndex, double lDeg, double bDeg,
                                           long[] pixelScratch, double[] weightScratch,
                                           ref Cursor cursor, out double rayleighs)
        {
            rayleighs = double.NaN;
            if (patch == null || patch.Values == null) return false;
            if (cursor.Runs == null) cursor = Cursor.New(patchIndex + 1);
            if (patchIndex < 0 || patchIndex >= cursor.Runs.Length) patchIndex = 0;

            Healpix.InterpolationWeightsDegrees(nside, lDeg, bDeg, pixelScratch, weightScratch);

            // A NON-POSITIVE VALUE IS NOT A MEASUREMENT OF ZERO EMISSION. SHASSA is a
            // continuum-subtracted survey: an off-band image is scaled and subtracted from the
            // H-alpha one to remove stellar continuum, and at a bright star that subtraction
            // over-corrects and drives the residual to zero or below (Gaustad et al. 2001,
            // PASP 113, 1326, Sect. 4). 795 cells of 542,673 across the fourteen patches are such
            // residuals, in discs on the brightest stars.
            //
            // They are MASKED, not handed back to the base map. Two reasons, and the second is the
            // one that matters. First, a masked cell surrounded by good ones is fully covered by
            // them: reweighting the interpolation over the neighbours that do carry a measurement
            // is the same operation EmissionMap already performs for a gap, and it keeps the
            // patch's own fine structure right up to the residual's edge. Second, falling through
            // to the base map SWITCHES DATA SOURCE mid-frame, and the two do not agree at the
            // patch's own resolution -- SHASSA resolves filaments a 6 arcmin beam averages away --
            // so the handover shows as a step, and at 0.86 arcmin per cell that step is a 13-pixel
            // staircase. Trading a black disc for a grey one with a jagged edge is not a fix.
            double sum = 0.0, weight = 0.0;
            for (int i = 0; i < 4; i++)
            {
                if (!patch.TryValue(pixelScratch[i], ref cursor.Runs[patchIndex], out double v))
                    return false;                       // outside the patch: that IS the base map's job
                if (!(v > 0.0)) continue;               // a subtraction residual: no measurement here
                sum += weightScratch[i] * v;
                weight += weightScratch[i];
            }

            // Only when every neighbour is a residual -- the core of a disc around a very bright
            // star -- is there nothing left to interpolate from, and the base map takes over.
            if (!(weight > 0.0)) return false;

            rayleighs = sum / weight;
            return true;
        }
    }
}
