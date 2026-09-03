using System;
using System.IO;

namespace ExoInstruments.Core
{
    /// <summary>
    /// An all-sky map of one emission line's surface brightness, in rayleighs.
    ///
    /// The Galaxy's diffuse ionised gas is the source narrowband imaging exists for, and it has
    /// been surveyed: WHAM, VTSS and SHASSA between them cover the whole sky in H-alpha, and
    /// Finkbeiner (2003, ApJS 146, 407) assembles them into one composite in rayleighs, which is
    /// the unit EmissionLines converts from.
    ///
    /// SAME PACKED FORMAT AS DustMap, deliberately: HEALPix in Galactic coordinates, one IEEE 754
    /// half float per pixel and a provenance string. The only difference is what the numbers mean
    /// and which line they belong to, and both are in the header. Half rather than a scaled
    /// integer for the same reason as the dust map: diffuse H-alpha runs from under a rayleigh at
    /// the poles to thousands in an H II region core, and no fixed-point scale covers both.
    ///
    /// RESOLUTION IS A REAL LIMIT HERE, more than it is for dust. The composite is smoothed to 6
    /// arcmin, which is 94 pixels across on the RedCat and 1300 on the RC20 with its Barlow; so
    /// this renders real structure in a wide field and a smooth glow at high magnification. That is
    /// the data's limit, not the renderer's, and it is why the header carries the resolution.
    ///
    /// NOTHING SHIPS. tools/pack_halpha_map.py builds one.
    /// </summary>
    public sealed class EmissionMap
    {
        private static readonly byte[] Magic = { (byte)'E', (byte)'X', (byte)'O', (byte)'E', (byte)'M', (byte)'I', (byte)'S', (byte)'1' };
        private const int FormatVersion = 2;

        private ushort[] pixels;   // IEEE 754 binary16, see Float16
        private int nside;
        private bool nested;

        public bool IsLoaded => pixels != null;
        public int Nside => nside;

        /// <summary>
        /// One cell's own value, not interpolated, by RING index. NaN outside the map or where it
        /// has no measurement.
        ///
        /// Interpolation is what a RENDER wants; a calibration wants the cell itself, because the
        /// quantity being matched is the mean over that cell's own area.
        /// </summary>
        public double RawCellValue(long ringPixel)
        {
            if (pixels == null || ringPixel < 0 || ringPixel >= pixels.Length) return double.NaN;
            long p = nested ? Healpix.RingToNested(nside, ringPixel) : ringPixel;
            if (p < 0 || p >= pixels.Length) return double.NaN;
            return Float16.ToDouble(pixels[p]);
        }
        public double ResolutionArcmin => IsLoaded ? Healpix.PixelResolutionDeg(nside) * 60.0 : 0.0;

        /// <summary>Air wavelength of the line this map measures, metres.</summary>
        public double LineWavelengthMeters { get; private set; }

        /// <summary>Name of that line, and where the map came from.</summary>
        public string LineName { get; private set; }
        public string Source { get; private set; }

        public void Load(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                for (int i = 0; i < Magic.Length; i++)
                    if (magic.Length != Magic.Length || magic[i] != Magic[i])
                        throw new InvalidDataException("not an ExoInstruments packed emission map");

                int version = reader.ReadInt32();
                if (version != FormatVersion) throw new InvalidDataException("unsupported emission map version " + version);

                int n = reader.ReadInt32();
                if (!Healpix.IsValidNside(n)) throw new InvalidDataException("bad nside " + n);
                bool isNested = reader.ReadByte() != 0;

                double wavelength = reader.ReadDouble();
                if (!(wavelength > 0.0)) throw new InvalidDataException("bad line wavelength");

                string lineName = ReadString(reader, 256);
                string source = ReadString(reader, 4096);

                long count = Healpix.PixelCount(n);
                if (count > int.MaxValue) throw new InvalidDataException("map too large to hold");
                ushort[] values = ReadHalfFloats(reader, (int)count);

                pixels = values;
                nside = n;
                nested = isNested;
                LineWavelengthMeters = wavelength;
                LineName = lineName;
                Source = source;
            }
        }

        // Reads count little-endian half floats in bulk. A BinaryReader.ReadUInt16 per value is one virtual
        // call and one bounds check each, and at nside 1024 that is 12.6 million of them; at nside 4096 it
        // would be 201 million, which is the difference between a load the player does not notice and one that
        // stalls the scene change. Reading into a byte buffer and reinterpreting is the same bytes in the same
        // order. Byte order is asserted rather than assumed: the format is little-endian, and every platform
        // KSP runs on is too, so a big-endian host would silently read a scrambled sky.
        internal static ushort[] ReadHalfFloats(BinaryReader reader, int count)
        {
            if (!BitConverter.IsLittleEndian)
                throw new InvalidDataException("packed maps are little-endian and this platform is not");

            var values = new ushort[count];
            const int chunkValues = 1 << 16;
            var buffer = new byte[chunkValues * 2];
            int read = 0;
            while (read < count)
            {
                int want = Math.Min(chunkValues, count - read);
                int got = 0;
                while (got < want * 2)
                {
                    int n = reader.Read(buffer, got, want * 2 - got);
                    if (n <= 0) throw new EndOfStreamException("packed map ended early");
                    got += n;
                }
                Buffer.BlockCopy(buffer, 0, values, read * 2, want * 2);
                read += want;
            }
            return values;
        }

        private static string ReadString(BinaryReader reader, int limit)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > limit) throw new InvalidDataException("bad string length");
            return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        /// <summary>
        /// Line surface brightness toward a Galactic direction, rayleighs, interpolated between the
        /// surrounding pixels; see Healpix.CubicInterpolationWeights for why the reconstruction has
        /// to be C1 and not merely continuous. NaN where the map has no value.
        ///
        /// Not thread-safe: a per-call scratch pair would allocate once per frame pixel. Callers
        /// filling a frame use RayleighsAtGalactic(l, b, scratch) with their own buffers.
        /// </summary>
        public double RayleighsAtGalactic(double lDeg, double bDeg)
            => RayleighsAtGalactic(lDeg, bDeg, sharedPixels, sharedWeights);

        private readonly long[] sharedPixels = new long[Healpix.CubicTaps];
        private readonly double[] sharedWeights = new double[Healpix.CubicTaps];

        /// <summary>Scratch buffers a caller can allocate once and reuse across a whole frame.</summary>
        public static void AllocateScratch(out long[] pixelScratch, out double[] weightScratch)
        {
            pixelScratch = new long[Healpix.CubicTaps];
            weightScratch = new double[Healpix.CubicTaps];
        }

        /// <summary>As above, using caller-supplied scratch so a frame-filling loop allocates nothing.</summary>
        public double RayleighsAtGalactic(double lDeg, double bDeg, long[] pixelScratch, double[] weightScratch)
        {
            if (pixels == null) return double.NaN;

            // THE C1 RECONSTRUCTION FIRST, and only where the whole stencil carries data. Its outer
            // taps are negative, so dropping some of them and renormalising over the rest is not a
            // weighted mean any more: it can put more than the total weight on the survivors and
            // amplify whatever is left. A gap therefore falls through to the bilinear scheme below,
            // whose weights are all positive, so reweighting it IS still a mean. The seam that
            // leaves is a crease along the edge of a gap, where the map already has an edge.
            int taps = Healpix.CubicInterpolationWeightsDegrees(nside, lDeg, bDeg, pixelScratch, weightScratch);
            if (taps == Healpix.CubicTaps)
            {
                double cubic = 0.0;
                bool complete = true;
                for (int i = 0; i < taps; i++)
                {
                    long p = pixelScratch[i];
                    if (p < 0 || p >= pixels.Length) { complete = false; break; }
                    if (nested) p = Healpix.RingToNested(nside, p);
                    double v = Float16.ToDouble(pixels[p]);
                    if (double.IsNaN(v)) { complete = false; break; }
                    cubic += weightScratch[i] * v;
                }
                if (complete) return cubic;
            }

            Healpix.InterpolationWeightsDegrees(nside, lDeg, bDeg, pixelScratch, weightScratch);

            // A pixel with no value must not be treated as zero: that would eat into its
            // neighbours' contribution and darken the edge of every gap. Renormalising over the
            // ones that do have a value is the same answer the survey's own coverage supports.
            double sum = 0.0, weight = 0.0;
            for (int i = 0; i < 4; i++)
            {
                long p = pixelScratch[i];
                if (p < 0 || p >= pixels.Length) continue;
                if (nested) p = Healpix.RingToNested(nside, p);
                double v = Float16.ToDouble(pixels[p]);
                if (double.IsNaN(v)) continue;
                sum += weightScratch[i] * v;
                weight += weightScratch[i];
            }
            return weight > 0.0 ? sum / weight : double.NaN;
        }

        /// <summary>The same for an equatorial direction.</summary>
        public double RayleighsAt(double raDeg, double decDeg)
        {
            GalacticCoordinates.EquatorialToGalactic(raDeg, decDeg, out double l, out double b);
            return RayleighsAtGalactic(l, b);
        }
    }
}
