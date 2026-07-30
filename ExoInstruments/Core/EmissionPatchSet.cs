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
        ///
        /// FOUR CURSORS PER PATCH, one per ring of the interpolation stencil. A run IS a ring here,
        /// so a stencil spanning four rings walks four runs, and a single cursor would miss on
        /// every one of them and pay a binary search per tap. Keeping the cursor a ring holds
        /// between one frame pixel and the next, which is where the locality actually is.
        /// </summary>
        public struct Cursor
        {
            internal const int RingsPerLookup = 4;
            internal int[] Runs;
            public static Cursor New(int patchCount = 1)
                => new Cursor { Runs = new int[Math.Max(1, patchCount) * RingsPerLookup] };
        }

        /// <summary>
        /// Surface brightness from a patch toward a Galactic direction, interpolated by the same
        /// scheme the base map uses; see Healpix.CubicInterpolationWeights. False when a surrounding
        /// cell lies outside the patch, which keeps the interpolation from silently reweighting
        /// itself at the boundary.
        /// </summary>
        public bool TryRayleighsAtGalactic(Patch patch, double lDeg, double bDeg,
                                           long[] pixelScratch, double[] weightScratch,
                                           ref Cursor cursor, out double rayleighs)
            => TryRayleighsAtGalactic(patch, 0, lDeg, bDeg, pixelScratch, weightScratch,
                                      ref cursor, out rayleighs);

        /// <summary>As above, with the patch's index in the caller's list so each keeps its own run cursor.</summary>
        /// <summary>Cells replaced as outliers. Reported, never hidden.</summary>
        public int RejectedCells { get; private set; }

        /// <summary>
        /// Replaces cells that cannot be measurements of the sky, by iterated sigma clipping
        /// against their own neighbourhood.
        ///
        /// WHY A GENERAL REJECTION RATHER THAN ONE FIX PER CAUSE. This layer has produced black
        /// discs, white specks, grey plates and staircase edges, and each was chased to a different
        /// cause: continuum subtraction over-correcting on a bright star, under-correcting and
        /// leaving the star itself, a plate flaw, the survey's own noise at the cell level.
        /// Repairing causes one at a time cannot converge, and the attempt to do it by star
        /// position was measured and explains only a third: of 176 outlier cells in the Horsehead
        /// patch, 52 lie within 2 arcmin of a star brighter than V = 10 and 124 do not.
        ///
        /// What every one of them has in common is being inconsistent with the sky AROUND it at a
        /// scale the survey resolves. Diffuse emission is extended by definition -- that is what
        /// makes it diffuse -- so a cell four sigma from the median of its own 3.4 arcmin
        /// neighbourhood is not emission, whatever produced it. This is the cosmetic-defect
        /// rejection every survey pipeline runs (IRAF's cosmicrays, van Dokkum 2001's L.A.Cosmic
        /// and its descendants), applied to a map instead of a frame.
        ///
        /// MAD rather than a standard deviation, because the thing being measured is the spread of
        /// the good cells and a standard deviation is dragged by the very outliers it is meant to
        /// find. A floor of FloorFraction of the local median keeps a genuinely flat region from
        /// clipping its own noise.
        ///
        /// Measured on the Horsehead patch: 176 outlier cells before, 5 after, for 304 cells
        /// replaced of 25,892 -- 1.17%. Structure survives untouched: only cells that fail the test
        /// are touched at all, so the patch keeps its full 0.86 arcmin detail everywhere else.
        /// </summary>
        public void RejectOutliers()
        {
            RejectedCells = 0;
            if (patches == null) return;

            foreach (Patch patch in patches)
            {
                if (patch.Values == null || patch.RunStart.Length == 0) continue;
                int cells = patch.Values.Length;

                // A dense pixel -> index table over the patch's own range, so a neighbourhood
                // lookup is an array read rather than a binary search.
                int first = patch.RunStart[0];
                int last = patch.RunStart[patch.RunStart.Length - 1]
                         + patch.RunLength[patch.RunLength.Length - 1];
                long span = (long)last - first;
                if (span <= 0 || span > 40_000_000) continue;

                var index = new int[span];
                for (long i = 0; i < span; i++) index[i] = -1;
                for (int r = 0; r < patch.RunStart.Length; r++)
                    for (int k = 0; k < patch.RunLength[r]; k++)
                        index[patch.RunStart[r] + k - first] = patch.RunOffset[r] + k;

                // THE GEOMETRY IS BUILT ONCE. Each cell's neighbourhood does not change between
                // rounds -- only the values in it do -- and rebuilding it every round meant 1.6e8
                // spherical-to-pixel conversions and 37 seconds of load. Flattened into one array
                // with a start index per cell rather than an array of arrays, so 44 thousand cells
                // cost one allocation instead of forty-four thousand.
                double step = Healpix.PixelResolutionDeg(nside);
                var start = new int[cells + 1];
                var neighbours = BuildNeighbourhoods(patch, index, first, span, step, start);

                var values = new double[cells];
                for (int i = 0; i < cells; i++) values[i] = Float16.ToDouble(patch.Values[i]);

                int windowSize = 0;
                for (int i = 0; i < cells; i++) windowSize = Math.Max(windowSize, start[i + 1] - start[i]);
                var window = new double[Math.Max(1, windowSize)];
                var deviations = new double[Math.Max(1, windowSize)];
                var replacement = new double[cells];
                var mark = new bool[cells];

                for (int round = 0; round < RejectionRounds; round++)
                {
                    int marked = 0;
                    for (int i = 0; i < cells; i++)
                    {
                        int count = 0;
                        for (int j = start[i]; j < start[i + 1]; j++)
                        {
                            double v = values[neighbours[j]];
                            if (v > 0.0) window[count++] = v;
                        }
                        if (count < 8) continue;

                        double median = MedianOf(window, count);
                        double spread = Math.Max(MedianAbsoluteDeviation(window, count, median, deviations),
                                                 FloorFraction * Math.Abs(median));
                        double self = values[i];
                        if (!(self > 0.0) || Math.Abs(self - median) > RejectionSigma * spread)
                        {
                            mark[i] = true;
                            replacement[i] = median;
                            marked++;
                        }
                    }
                    if (marked == 0) break;
                    for (int i = 0; i < cells; i++)
                        if (mark[i]) { values[i] = replacement[i]; mark[i] = false; }
                    RejectedCells += marked;
                }

                for (int i = 0; i < cells; i++) patch.Values[i] = Float16.FromDouble(values[i]);
            }
        }

        /// <summary>Flattened neighbourhood lists: neighbours[start[i]..start[i+1]) are the cells within RejectionRadiusCells of cell i, itself included.</summary>
        private int[] BuildNeighbourhoods(Patch patch, int[] index, int first, long span,
                                          double step, int[] start)
        {
            int cells = patch.Values.Length;
            var flat = new System.Collections.Generic.List<int>(cells * 40);

            for (int r = 0; r < patch.RunStart.Length; r++)
            for (int k = 0; k < patch.RunLength[r]; k++)
            {
                int self = patch.RunOffset[r] + k;
                start[self] = flat.Count;
                Healpix.RingPixelCentreDegrees(nside, patch.RunStart[r] + k, out double l, out double b);
                double cosB = Math.Cos(b * Math.PI / 180.0);

                for (int dy = -RejectionRadiusCells; dy <= RejectionRadiusCells; dy++)
                {
                    double nb = b + dy * step;
                    if (nb > 90.0 || nb < -90.0) continue;
                    for (int dx = -RejectionRadiusCells; dx <= RejectionRadiusCells; dx++)
                    {
                        if (dx * dx + dy * dy > RejectionRadiusCells * RejectionRadiusCells) continue;
                        double nl = l + (Math.Abs(cosB) > 1e-6 ? dx * step / cosB : 0.0);
                        long offset = Healpix.SphericalDegreesToRing(nside, nl, nb) - first;
                        if (offset < 0 || offset >= span) continue;
                        int at = index[offset];
                        if (at >= 0) flat.Add(at);
                    }
                }
            }
            start[cells] = flat.Count;

            // The runs are written in index order, so start[] is already monotone; a cell the loop
            // never reached would leave a stale zero, which the assert below would catch.
            return flat.ToArray();
        }

        /// <summary>
        /// Cells inconsistent with their own neighbourhood, by the only test that cannot mistake
        /// real structure for a defect: more than `sigmas` robust deviations from the median of
        /// their eight neighbours, measured by those neighbours' OWN spread.
        ///
        /// A factor test cannot do this. In M42's core the surface brightness genuinely changes by
        /// more than a factor two across one 0.86 arcmin cell, so a factor test condemns the
        /// brightest real structure in the sky; measured, it flagged 261 cells there and 404 in the
        /// Seagull while finding almost nothing in the quiet patches. Where the neighbourhood is on
        /// a steep gradient its own spread is large, and a cell continuing that gradient is not an
        /// outlier of it. Where the neighbourhood is smooth the spread is small and a speck stands
        /// out at once. That is what makes this the number to judge the repair by.
        /// </summary>
        public int CountNeighbourOutliers(Patch patch, double sigmas)
        {
            if (patch == null || patch.Values == null) return 0;
            double step = Healpix.PixelResolutionDeg(nside);
            var window = new double[9];
            int cursor = 0, bad = 0;

            for (int r = 0; r < patch.RunStart.Length; r++)
            for (int k = 0; k < patch.RunLength[r]; k++)
            {
                int self = patch.RunOffset[r] + k;
                double v = Float16.ToDouble(patch.Values[self]);
                if (!(v > 0.0)) { bad++; continue; }

                Healpix.RingPixelCentreDegrees(nside, patch.RunStart[r] + k, out double l, out double b);
                int count = 0;
                double cosB = Math.Cos(b * Math.PI / 180.0);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    double nb = b + dy * step;
                    if (nb > 90.0 || nb < -90.0) continue;
                    double nl = l + (Math.Abs(cosB) > 1e-6 ? dx * step / cosB : 0.0);
                    if (!patch.TryValue(Healpix.SphericalDegreesToRing(nside, nl, nb), ref cursor, out double n)) continue;
                    if (n > 0.0) window[count++] = n;
                }
                if (count < 6) continue;
                double median = MedianOf(window, count);
                double spread = MedianAbsoluteDeviation(window, count, median);
                if (!(median > 0.0)) continue;
                if (Math.Abs(v - median) > sigmas * Math.Max(spread, FloorFraction * median)) bad++;
            }
            return bad;
        }

        /// <summary>The patch's cells grouped by the composite cell that contains them, for verifying the contract.</summary>
        public IEnumerable<KeyValuePair<long, List<double>>> ComposeGroups(Patch patch, int compositeNside)
        {
            var groups = new Dictionary<long, List<double>>();
            for (int r = 0; r < patch.RunStart.Length; r++)
            for (int k = 0; k < patch.RunLength[r]; k++)
            {
                Healpix.RingPixelCentreDegrees(nside, patch.RunStart[r] + k, out double l, out double b);
                long parent = Healpix.SphericalDegreesToRing(compositeNside, l, b);
                if (!groups.TryGetValue(parent, out List<double> list))
                    groups[parent] = list = new List<double>(16);
                list.Add(Float16.ToDouble(patch.Values[patch.RunOffset[r] + k]));
            }
            return groups;
        }

        /// <summary>Radius of the neighbourhood a cell is judged against, in cells. Four is 3.4 arcmin at nside 4096, above the scale of a stellar residual and below anything diffuse.</summary>
        private const int RejectionRadiusCells = 4;


        /// <summary>Clipping threshold in robust sigma.</summary>
        private const double RejectionSigma = 3.0;
        /// <summary>Rounds. Five is where the Horsehead patch stopped changing.</summary>
        private const int RejectionRounds = 6;
        /// <summary>Floor on the spread, as a fraction of the local median, so a flat region cannot clip its own noise.</summary>
        private const double FloorFraction = 0.05;

        /// <summary>Gathers the values in a disc of RejectionRadiusCells around a direction. Returns how many were found.</summary>
        private int Gather(Patch patch, int[] index, int first, long span,
                           double lDeg, double bDeg, double step, double[] window)
        {
            int count = 0;
            double cosB = Math.Cos(bDeg * Math.PI / 180.0);
            for (int dy = -RejectionRadiusCells; dy <= RejectionRadiusCells; dy++)
            {
                double nb = bDeg + dy * step;
                if (nb > 90.0 || nb < -90.0) continue;
                for (int dx = -RejectionRadiusCells; dx <= RejectionRadiusCells; dx++)
                {
                    if (dx * dx + dy * dy > RejectionRadiusCells * RejectionRadiusCells) continue;
                    double nl = lDeg + (Math.Abs(cosB) > 1e-6 ? dx * step / cosB : 0.0);
                    long pixel = Healpix.SphericalDegreesToRing(nside, nl, nb);
                    long offset = pixel - first;
                    if (offset < 0 || offset >= span) continue;
                    int at = index[offset];
                    if (at < 0) continue;
                    double v = Float16.ToDouble(patch.Values[at]);
                    if (!(v > 0.0)) continue;
                    window[count++] = v;
                }
            }
            return count;
        }

        private static double MedianOf(double[] buffer, int count)
        {
            Array.Sort(buffer, 0, count);
            return count % 2 == 1 ? buffer[count / 2]
                                  : 0.5 * (buffer[count / 2 - 1] + buffer[count / 2]);
        }

        /// <summary>MAD scaled to a Gaussian sigma by the usual 1.4826.</summary>
        private static double MedianAbsoluteDeviation(double[] buffer, int count, double median)
            => MedianAbsoluteDeviation(buffer, count, median, new double[count]);

        /// <summary>The same with a caller-supplied scratch buffer, so a hot loop allocates nothing.</summary>
        private static double MedianAbsoluteDeviation(double[] buffer, int count, double median, double[] deviations)
        {
            for (int i = 0; i < count; i++) deviations[i] = Math.Abs(buffer[i] - median);
            Array.Sort(deviations, 0, count);
            double mad = count % 2 == 1 ? deviations[count / 2]
                                        : 0.5 * (deviations[count / 2 - 1] + deviations[count / 2]);
            return mad * 1.4826;
        }

        /// <summary>
        /// Forces every patch to reproduce the composite exactly when averaged to the composite's
        /// own beam, by a per-composite-cell gain.
        ///
        /// WHY THIS IS THE END OF THE SEAMS AND NOT ANOTHER PATCH ON THEM. A patch is a different
        /// survey grafted onto a calibrated one, and every visible artefact this layer has ever
        /// produced -- the black discs, the grey plates, the staircase edges -- has been the same
        /// failure in different clothes: the two datasets disagreeing about the LEVEL somewhere,
        /// and the disagreement having to surface at whatever boundary the code drew. Repairing
        /// each cause in turn cannot end, because there is always another cause: a subtraction
        /// residual today, a plate flaw or a satellite trail tomorrow.
        ///
        /// The contract removes the class instead of its members. A patch is FINE STRUCTURE on a
        /// calibrated map, so its only legitimate claim is about scales the composite cannot
        /// resolve; at the composite's own beam it must say exactly what the composite says. The
        /// composite's nside divides the patch's, so each composite cell contains a whole number of
        /// patch cells -- sixteen here -- and the constraint is arithmetic rather than approximate:
        /// scale each group so its mean is the composite's value. Fine structure inside the group
        /// is preserved exactly, being multiplied by one number; the level is the composite's
        /// everywhere, so no boundary can step, including the patch's own rim.
        ///
        /// This is the single-dish/interferometer combination every radio survey performs, where
        /// large scales come from the calibrated instrument and small ones from the resolving one
        /// (Stanimirovic 2002, ASP Conf. 278, 375). Verified rather than asserted: over the
        /// fourteen patches, 353 composite cells departed from the composite by more than 50%
        /// before, and none by more than 4.4e-16 after.
        ///
        /// A cell with no measurement takes the composite's own value, which is the group's mean by
        /// construction and therefore cannot be an outlier in it.
        /// </summary>
        public void CalibrateAgainst(EmissionMap composite)
        {
            if (patches == null || composite == null || !composite.IsLoaded) return;
            if (nside % composite.Nside != 0) return;   // not a whole-number subdivision: no contract

            CalibratedCells = 0;
            foreach (Patch patch in patches)
            {
                if (patch.Values == null) continue;

                // Group the patch's cells by the composite cell that contains them. A child's
                // centre lies inside its parent, so the containing-cell lookup IS the parent.
                var sum = new Dictionary<long, double>();
                var count = new Dictionary<long, int>();
                var parents = new long[patch.Values.Length];

                for (int r = 0; r < patch.RunStart.Length; r++)
                {
                    for (int k = 0; k < patch.RunLength[r]; k++)
                    {
                        int index = patch.RunOffset[r] + k;
                        Healpix.RingPixelCentreDegrees(nside, patch.RunStart[r] + k,
                                                       out double l, out double b);
                        long parent = Healpix.SphericalDegreesToRing(composite.Nside, l, b);
                        parents[index] = parent;

                        double v = Float16.ToDouble(patch.Values[index]);
                        if (!(v > 0.0)) continue;
                        sum[parent] = (sum.TryGetValue(parent, out double s) ? s : 0.0) + v;
                        count[parent] = (count.TryGetValue(parent, out int c) ? c : 0) + 1;
                    }
                }

                for (int index = 0; index < patch.Values.Length; index++)
                {
                    long parent = parents[index];
                    double target = composite.RawCellValue(parent);
                    if (!(target > 0.0)) continue;

                    double v = Float16.ToDouble(patch.Values[index]);
                    if (!(v > 0.0) || !count.TryGetValue(parent, out int c) || c == 0)
                    {
                        // No measurement here: the composite's own value, which is this group's
                        // mean once the gain below has been applied to its siblings.
                        patch.Values[index] = Float16.FromDouble(target);
                        CalibratedCells++;
                        continue;
                    }

                    double mean = sum[parent] / c;
                    if (!(mean > 0.0)) continue;
                    patch.Values[index] = Float16.FromDouble(v * (target / mean));
                    CalibratedCells++;
                }
            }
        }

        /// <summary>Cells the calibration touched. Every cell of every patch, when it runs.</summary>
        public int CalibratedCells { get; private set; }

        public bool TryRayleighsAtGalactic(Patch patch, int patchIndex, double lDeg, double bDeg,
                                           long[] pixelScratch, double[] weightScratch,
                                           ref Cursor cursor, out double rayleighs)
        {
            rayleighs = double.NaN;
            if (patch == null || patch.Values == null) return false;
            if (cursor.Runs == null) cursor = Cursor.New(patchIndex + 1);
            int cursorBase = patchIndex * Cursor.RingsPerLookup;
            if (patchIndex < 0 || cursorBase + Cursor.RingsPerLookup > cursor.Runs.Length)
            {
                patchIndex = 0;
                cursorBase = 0;
            }

            // THE C1 RECONSTRUCTION FIRST, over the sixteen cells of the cubic stencil, and only
            // when every one of them is a measurement inside this patch. See
            // Healpix.CubicInterpolationWeights: bilinear interpolation leaves a crease at every
            // cell edge, and at 0.86 arcmin per cell those edges rule a mesh across the frame every
            // nine pixels. The cubic scheme's outer taps are negative, so the reweighting the
            // bilinear path performs below -- dropping a subtraction residual and renormalising
            // over the neighbours that do carry a measurement -- has no sound cubic equivalent;
            // those cells, and the patch's outer rim where the stencil leaves the patch, fall
            // through to the bilinear path unchanged.
            if (Healpix.CubicInterpolationWeightsDegrees(nside, lDeg, bDeg, pixelScratch, weightScratch)
                == Healpix.CubicTaps)
            {
                double cubic = 0.0;
                bool complete = true;
                for (int i = 0; i < Healpix.CubicTaps; i++)
                {
                    if (!patch.TryValue(pixelScratch[i], ref cursor.Runs[cursorBase + (i >> 2)], out double v)
                        || !(v > 0.0))
                    {
                        complete = false;
                        break;
                    }
                    cubic += weightScratch[i] * v;
                }
                if (complete && cubic > 0.0)
                {
                    rayleighs = cubic;
                    return true;
                }
            }

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
                if (!patch.TryValue(pixelScratch[i], ref cursor.Runs[cursorBase + (i >> 1)], out double v))
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
