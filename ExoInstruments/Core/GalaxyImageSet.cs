using System;
using System.Collections.Generic;
using System.IO;

namespace ExoInstruments.Core
{
    /// <summary>
    /// One galaxy's measured shape, in one band: a unit-total map of where its light is.
    /// </summary>
    public sealed class GalaxyImageBand
    {
        /// <summary>Effective wavelength of the survey filter the map was measured in, nanometres.</summary>
        public double WavelengthNm;
        /// <summary>Survey and filter, e.g. "DECam g", for the readout and the FITS header.</summary>
        public string Label;
        /// <summary>Row-major, Size x Size, summing to one over the whole map.</summary>
        public float[] Values;
    }

    /// <summary>
    /// A galaxy the image set has a real map for.
    ///
    /// The geometry is a tangent plane centred on the catalogued position, north up and east LEFT,
    /// which is the orientation the packer's own WCS carries; the renderer never assumes it, it
    /// projects four known corners through the frame's own projection and solves for the transform
    /// between them (see GalaxyImageRenderer).
    /// </summary>
    public sealed class GalaxyImage
    {
        public string Name;
        public double RaDeg;
        public double DecDeg;
        /// <summary>Pixels per side of the square map.</summary>
        public int Size;
        /// <summary>Arcseconds per map pixel.</summary>
        public double ScaleArcsec;
        public string SurveyId;
        /// <summary>Fraction of the map's pixels that were masked and filled (stars, uncovered corners).</summary>
        public double MaskedFraction;
        /// <summary>Fraction of the map's total flux inside the catalogued D25 ellipse.</summary>
        public double FluxInsideD25;
        /// <summary>
        /// Catalogued galaxies whose own light is already IN this map, because their centre falls
        /// inside its box. The renderer adds their flux to this one's normalisation and does not
        /// draw them separately; see GalaxyImageSet.Covers.
        /// </summary>
        public string[] Companions;
        public GalaxyImageBand[] Bands;

        /// <summary>Half the map's side, arcseconds. What the frame has to overlap for the map to be worth reading.</summary>
        public double HalfWidthArcsec => 0.5 * Size * ScaleArcsec;

        /// <summary>
        /// The map's own resolution limit, arcseconds. Compared against the instrument's plate
        /// scale this is the number that says whether the render is showing the survey's structure
        /// or an interpolation of it, and it is reported the same way DeepSkyObject.BeamsAcross
        /// reports what an emission map can resolve.
        /// </summary>
        public double SamplingArcsec => ScaleArcsec;

        /// <summary>
        /// Zero-based pixel the map's tangent point sits on.
        ///
        /// The packer writes CRPIX at Size/2 in the FITS convention, which is one-based, so the
        /// tangent point is at Size/2 - 1 here and NOT at (Size-1)/2. Half a pixel of disagreement
        /// is half a pixel of registration error against the star field, which at the RC20's
        /// native sampling is a fifth of an arcsecond.
        /// </summary>
        public double CentrePixel => Size / 2 - 1;

        /// <summary>
        /// Sky direction of a map pixel: the exact gnomonic deprojection about the catalogued
        /// position, north up and east left, which is the orientation the packer's WCS carries.
        ///
        /// Written as a vector construction rather than as the usual arctangent formulae because
        /// a tangent plane IS the set of directions c + xi*east + eta*north up to normalisation,
        /// which is both exact and free of the quadrant cases the closed forms need.
        /// </summary>
        public void MapPixelToRaDec(double u, double v, out double raDeg, out double decDeg)
        {
            double scaleRad = ScaleArcsec * (Math.PI / 180.0) / 3600.0;
            double xi = -(u - CentrePixel) * scaleRad;      // east is -u
            double eta = (v - CentrePixel) * scaleRad;      // north is +v

            double ra0 = RaDeg * Math.PI / 180.0, dec0 = DecDeg * Math.PI / 180.0;
            double cosRa = Math.Cos(ra0), sinRa = Math.Sin(ra0);
            double cosDec = Math.Cos(dec0), sinDec = Math.Sin(dec0);

            double cx = cosDec * cosRa, cy = cosDec * sinRa, cz = sinDec;
            double ex = -sinRa, ey = cosRa, ez = 0.0;
            double nx = -sinDec * cosRa, ny = -sinDec * sinRa, nz = cosDec;

            double dx = cx + xi * ex + eta * nx;
            double dy = cy + xi * ey + eta * ny;
            double dz = cz + xi * ez + eta * nz;

            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!(length > 0.0)) { raDeg = RaDeg; decDeg = DecDeg; return; }

            raDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (raDeg < 0.0) raDeg += 360.0;
            decDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dz / length))) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Surface brightness at a point in map pixels, bilinearly interpolated, for a passband of
        /// the given effective wavelength.
        ///
        /// WHY THE WAVELENGTH ENTERS. A galaxy's shape is not the same in every band: the arms are
        /// bluer than the disc they sit in, the bulge is redder than both, and a dust lane is
        /// darkest in the blue. The two stored maps are each normalised to unit total, so blending
        /// them by wavelength changes only WHERE the light is and never how much of it there is;
        /// the total stays the catalogue's whatever band is being simulated. Outside the two
        /// measured wavelengths the nearer map is used unchanged rather than extrapolated.
        /// </summary>
        public double Sample(double u, double v, double wavelengthNm)
        {
            // Taken once: the set evicts a map's pixels when another frame needs the room, and a
            // deposit in progress must not have the array pulled out from under it half way down a
            // row. Holding the reference keeps the frame consistent even if the entry is dropped.
            GalaxyImageBand[] bands = Bands;
            if (bands == null || bands.Length == 0) return 0.0;
            if (u < 0.0 || v < 0.0 || u > Size - 1 || v > Size - 1) return 0.0;

            int x0 = (int)u, y0 = (int)v;
            if (x0 > Size - 2) x0 = Size - 2;
            if (y0 > Size - 2) y0 = Size - 2;
            double fx = u - x0, fy = v - y0;
            int i00 = y0 * Size + x0, i10 = i00 + 1, i01 = i00 + Size, i11 = i01 + 1;

            ResolveBands(bands, wavelengthNm, out GalaxyImageBand a, out GalaxyImageBand b, out double t);

            double va = Bilinear(a.Values, i00, i10, i01, i11, fx, fy);
            if (b == null || t <= 0.0) return va;
            double vb = Bilinear(b.Values, i00, i10, i01, i11, fx, fy);
            return va + (vb - va) * t;
        }

        private static double Bilinear(float[] p, int i00, int i10, int i01, int i11, double fx, double fy)
        {
            double top = p[i00] + (p[i10] - p[i00]) * fx;
            double bottom = p[i01] + (p[i11] - p[i01]) * fx;
            return top + (bottom - top) * fy;
        }

        /// <summary>
        /// The two maps a wavelength falls between, and how far between them it is. Handles any
        /// number of bands (SDSS entries carry four); outside the measured range the nearest map
        /// is used unchanged rather than extrapolated.
        /// </summary>
        public void ResolveBands(GalaxyImageBand[] bands, double wavelengthNm,
                                 out GalaxyImageBand lower, out GalaxyImageBand upper, out double t)
        {
            lower = bands[0];
            upper = null;
            t = 0.0;
            if (bands.Length < 2) return;

            GalaxyImageBand lo = null, hi = null;
            for (int i = 0; i < bands.Length; i++)
            {
                GalaxyImageBand b = bands[i];
                if (b.WavelengthNm <= wavelengthNm && (lo == null || b.WavelengthNm > lo.WavelengthNm)) lo = b;
                if (b.WavelengthNm >= wavelengthNm && (hi == null || b.WavelengthNm < hi.WavelengthNm)) hi = b;
            }
            if (lo == null) { lower = hi; return; }
            if (hi == null || ReferenceEquals(hi, lo)) { lower = lo; return; }

            lower = lo;
            upper = hi;
            t = (wavelengthNm - lo.WavelengthNm) / (hi.WavelengthNm - lo.WavelengthNm);
        }
    }

    /// <summary>
    /// The packed shape maps: real survey imagery of the catalogued galaxies, keyed by catalogue name.
    ///
    /// WHY THIS EXISTS. A Sersic profile drawn from a total magnitude and an isophotal diameter is
    /// a smooth ellipse, and no relation in the literature turns four catalogued numbers into M51's
    /// arms, because those arms are M51's own and not a property of its Hubble type. The same
    /// problem was answered for nebulae by installing a finer survey rather than a prettier model
    /// (see EmissionPatchSet); this is that answer for galaxies. Built by
    /// tools/pack_galaxy_images.py, which is where the sourcing, the survey choice and the
    /// measured linearity check live.
    ///
    /// WHAT IT CONTRIBUTES, AND WHAT IT DOES NOT. Only the shape. Every map sums to one, so the
    /// brightness still comes from HyperLEDA's B_T through the same photometric chain a mapless
    /// galaxy uses, and a map can never make a galaxy brighter or fainter than the catalogue says.
    ///
    /// LOADED LAZILY, because it is not small: a thousand pixels a side in two bands is eight
    /// megabytes for one galaxy, and a frame needs the one or two it actually contains. The index
    /// (name, position, geometry) is read at load; the pixels are read from disk when a frame first
    /// asks for that galaxy and kept in a small cache after that.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public sealed class GalaxyImageSet
    {
        private static readonly byte[] Magic = { (byte)'E', (byte)'X', (byte)'O', (byte)'G', (byte)'I', (byte)'M', (byte)'G', (byte)'1' };
        private const int FormatVersion = 1;

        private sealed class Entry
        {
            public GalaxyImage Image;                  // metadata always, Bands filled on demand
            public long[] BandOffsets;                 // byte offset of each band's pixel block
            public double[] BandScales;
            public double[] BandWavelengths;
            public string[] BandLabels;
            public GalaxyImageBand[] Loaded;
        }

        private readonly Dictionary<string, Entry> byName =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> coveredBy =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<Entry> residentOrder = new LinkedList<Entry>();

        private string path;

        public bool IsLoaded => path != null;
        public int Count => byName.Count;
        public string Source { get; private set; }

        /// <summary>How many galaxies' pixels are held in memory at once. Four is a frame's worth several times over: a field rarely holds two mapped galaxies, and an evicted one is a disk read away.</summary>
        public int ResidentLimit { get; set; } = 4;

        public void Load(string filePath)
        {
            var index = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var covers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var stream = File.OpenRead(filePath))
            using (var reader = new BinaryReader(stream))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length) throw new InvalidDataException("not an ExoInstruments galaxy image set");
                for (int i = 0; i < Magic.Length; i++)
                    if (magic[i] != Magic[i]) throw new InvalidDataException("not an ExoInstruments galaxy image set");

                int version = reader.ReadInt32();
                if (version != FormatVersion) throw new InvalidDataException("unsupported galaxy image set version " + version);

                int count = reader.ReadInt32();
                if (count < 0 || count > 1_000_000) throw new InvalidDataException("implausible galaxy image count " + count);
                Source = ReadString(reader, 4096);

                for (int i = 0; i < count; i++)
                {
                    var image = new GalaxyImage { Name = ReadString(reader, 64) };
                    image.RaDeg = reader.ReadDouble();
                    image.DecDeg = reader.ReadDouble();
                    image.Size = reader.ReadInt32();
                    if (image.Size < 8 || image.Size > 16384) throw new InvalidDataException("implausible map size " + image.Size);
                    image.ScaleArcsec = reader.ReadDouble();
                    image.SurveyId = ReadString(reader, 64);
                    reader.ReadByte();                                  // photometric flag, always set by the packer
                    image.MaskedFraction = reader.ReadSingle();
                    image.FluxInsideD25 = reader.ReadSingle();

                    int companions = reader.ReadInt32();
                    if (companions < 0 || companions > 4096) throw new InvalidDataException("implausible companion count");
                    image.Companions = new string[companions];
                    for (int c = 0; c < companions; c++) image.Companions[c] = ReadString(reader, 64);

                    // Per entry, not per file: a galaxy too large for the stack service falls back
                    // to the one HiPS band measured linear, and carries one map instead of two.
                    int bandCount = reader.ReadInt32();
                    if (bandCount < 1 || bandCount > 8) throw new InvalidDataException("implausible band count " + bandCount);

                    var entry = new Entry
                    {
                        Image = image,
                        BandOffsets = new long[bandCount],
                        BandScales = new double[bandCount],
                        BandWavelengths = new double[bandCount],
                        BandLabels = new string[bandCount],
                    };

                    long planeBytes = (long)image.Size * image.Size * 2L;
                    for (int b = 0; b < bandCount; b++)
                    {
                        entry.BandWavelengths[b] = reader.ReadDouble();
                        entry.BandLabels[b] = ReadString(reader, 64);
                        entry.BandScales[b] = reader.ReadDouble();
                        entry.BandOffsets[b] = stream.Position;
                        stream.Seek(planeBytes, SeekOrigin.Current);
                    }

                    index[image.Name] = entry;
                    foreach (string companion in image.Companions)
                        covers[companion] = image.Name;
                }
            }

            byName.Clear();
            coveredBy.Clear();
            residentOrder.Clear();
            foreach (var kv in index) byName[kv.Key] = kv.Value;
            foreach (var kv in covers) coveredBy[kv.Key] = kv.Value;
            path = filePath;
        }

        private static string ReadString(BinaryReader reader, int limit)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > limit) throw new InvalidDataException("bad string length");
            return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        /// <summary>Every galaxy the set has a map for, so a harness can walk the file.</summary>
        public IEnumerable<string> Names => byName.Keys;

        /// <summary>Geometry and provenance for a galaxy, without reading a single pixel.</summary>
        public GalaxyImage Describe(string name)
        {
            if (path == null || name == null) return null;
            Entry entry;
            return byName.TryGetValue(name, out entry) ? entry.Image : null;
        }

        /// <summary>
        /// True when this galaxy's light is already inside ANOTHER galaxy's map, which is what a
        /// close companion is. Drawing it from its own entry as well would draw it twice.
        /// </summary>
        public bool IsCoveredByAnother(string name, out string owner)
        {
            owner = null;
            return path != null && name != null && coveredBy.TryGetValue(name, out owner);
        }

        /// <summary>
        /// The map with its pixels, read from disk on first use and cached. Null when there is none.
        ///
        /// Locked because this is the only mutable state the frame pipeline touches: the star and
        /// galaxy catalogues are read-only once loaded, while this reads from disk and reorders a
        /// cache, and the frame is built on a background task.
        /// </summary>
        public GalaxyImage Fetch(string name)
        {
            if (path == null || name == null) return null;
            lock (gate) return FetchLocked(name);
        }

        private readonly object gate = new object();

        private GalaxyImage FetchLocked(string name)
        {
            Entry entry;
            if (!byName.TryGetValue(name, out entry)) return null;
            if (entry.Loaded != null)
            {
                Touch(entry);
                return entry.Image;
            }

            int size = entry.Image.Size;
            int pixels = size * size;
            var bands = new GalaxyImageBand[entry.BandOffsets.Length];
            var buffer = new byte[pixels * 2];

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    for (int b = 0; b < bands.Length; b++)
                    {
                        stream.Seek(entry.BandOffsets[b], SeekOrigin.Begin);
                        int read = 0;
                        while (read < buffer.Length)
                        {
                            int got = stream.Read(buffer, read, buffer.Length - read);
                            if (got <= 0) return null;
                            read += got;
                        }

                        // Stored relative to the map's own peak, with a scale that makes the sum
                        // exactly one AFTER quantisation; see the packer for why float16 cannot
                        // hold a unit-total map directly.
                        double scale = entry.BandScales[b];
                        var values = new float[pixels];
                        for (int i = 0; i < pixels; i++)
                        {
                            ushort bits = (ushort)(buffer[2 * i] | (buffer[2 * i + 1] << 8));
                            values[i] = (float)(Float16.ToDouble(bits) * scale);
                        }
                        bands[b] = new GalaxyImageBand
                        {
                            WavelengthNm = entry.BandWavelengths[b],
                            Label = entry.BandLabels[b],
                            Values = values,
                        };
                    }
                }
            }
            catch (IOException)
            {
                return null;
            }

            entry.Loaded = bands;
            entry.Image.Bands = bands;
            Touch(entry);
            Evict();
            return entry.Image;
        }

        private void Touch(Entry entry)
        {
            residentOrder.Remove(entry);
            residentOrder.AddFirst(entry);
        }

        private void Evict()
        {
            while (residentOrder.Count > Math.Max(1, ResidentLimit))
            {
                LinkedListNode<Entry> last = residentOrder.Last;
                residentOrder.RemoveLast();
                last.Value.Loaded = null;
                last.Value.Image.Bands = null;
            }
        }
    }
}
