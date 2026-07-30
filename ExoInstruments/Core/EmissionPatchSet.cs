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
    /// information is not in the file. A finer survey does exist over part of the sky -- SHASSA
    /// (Gaustad et al. 2001, PASP 113, 1326) images everything south of +15 degrees at 0.8 arcmin --
    /// and at that beam the Horsehead spans 10 elements instead of 1.3.
    ///
    /// WHY PATCHES AND NOT A FINER ALL-SKY MAP. Resolution is only worth storing where there is
    /// something to resolve. The whole sky at 0.86 arcmin is 201 million cells, 403 MB, of which the
    /// overwhelming majority carries diffuse background that 6 arcmin already describes perfectly
    /// well. Four degrees around each catalogued object is 78 thousand cells apiece, about 5 MB for
    /// the entire catalogue -- eighty times smaller for the same result on every target anyone
    /// points at. Outside a patch the base map answers, which is the same layered arrangement every
    /// real survey archive uses.
    ///
    /// STORAGE is run-length by HEALPix ring. In RING ordering a disc on the sky cuts each ring in
    /// one contiguous stretch of pixels, so a patch is a few hundred runs rather than a pixel index
    /// per value -- which would otherwise cost three times as much as the values themselves.
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
        /// so a wide-field instrument fell back to the base map entirely -- the RedCat's 2.7 degree
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
        /// Per-caller lookup state, so a background frame fill keeps its own run cursor -- one per
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
        /// the same four surrounding pixels the base map uses -- see Healpix.InterpolationWeights.
        /// False when any of the four lies outside the patch, which keeps the interpolation from
        /// silently reweighting itself at the boundary.
        /// </summary>
        public bool TryRayleighsAtGalactic(Patch patch, double lDeg, double bDeg,
                                           long[] pixelScratch, double[] weightScratch,
                                           ref Cursor cursor, out double rayleighs)
            => TryRayleighsAtGalactic(patch, 0, lDeg, bDeg, pixelScratch, weightScratch,
                                      ref cursor, out rayleighs);

        /// <summary>As above, with the patch's index in the caller's list so each keeps its own run cursor.</summary>
        public bool TryRayleighsAtGalactic(Patch patch, int patchIndex, double lDeg, double bDeg,
                                           long[] pixelScratch, double[] weightScratch,
                                           ref Cursor cursor, out double rayleighs)
        {
            rayleighs = double.NaN;
            if (patch == null || patch.Values == null) return false;
            if (cursor.Runs == null) cursor = Cursor.New(patchIndex + 1);
            if (patchIndex < 0 || patchIndex >= cursor.Runs.Length) patchIndex = 0;

            Healpix.InterpolationWeightsDegrees(nside, lDeg, bDeg, pixelScratch, weightScratch);
            double sum = 0.0;
            for (int i = 0; i < 4; i++)
            {
                if (!patch.TryValue(pixelScratch[i], ref cursor.Runs[patchIndex], out double v))
                    return false;
                sum += weightScratch[i] * v;
            }
            rayleighs = sum;
            return true;
        }
    }
}
