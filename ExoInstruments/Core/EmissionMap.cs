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
    /// arcmin, which is 94 pixels across on the RedCat and 1300 on the RC20 with its Barlow -- so
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
                var values = new ushort[count];
                for (long i = 0; i < count; i++) values[i] = reader.ReadUInt16();

                pixels = values;
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

        /// <summary>Line surface brightness toward a Galactic direction, rayleighs. NaN where the map has no value.</summary>
        public double RayleighsAtGalactic(double lDeg, double bDeg)
        {
            if (pixels == null) return double.NaN;
            long pixel = nested
                ? Healpix.SphericalDegreesToNested(nside, lDeg, bDeg)
                : Healpix.SphericalDegreesToRing(nside, lDeg, bDeg);
            if (pixel < 0 || pixel >= pixels.Length) return double.NaN;
            return Float16.ToDouble(pixels[pixel]);
        }

        /// <summary>The same for an equatorial direction.</summary>
        public double RayleighsAt(double raDeg, double decDeg)
        {
            GalacticCoordinates.EquatorialToGalactic(raDeg, decDeg, out double l, out double b);
            return RayleighsAtGalactic(l, b);
        }

        /// <summary>
        /// The HEALPix cell a direction falls in, so a caller filling a whole frame can skip the
        /// value lookup while consecutive pixels stay in one cell. At 6 arcmin a cell spans at
        /// least 94 frame pixels, so that is nearly always.
        /// </summary>
        public long CellAtGalactic(double lDeg, double bDeg)
        {
            if (pixels == null) return -1;
            return nested
                ? Healpix.SphericalDegreesToNested(nside, lDeg, bDeg)
                : Healpix.SphericalDegreesToRing(nside, lDeg, bDeg);
        }

        /// <summary>Value of a cell obtained from CellAtGalactic. NaN for an unknown cell.</summary>
        public double RayleighsInCell(long cell)
        {
            if (pixels == null || cell < 0 || cell >= pixels.Length) return double.NaN;
            return Float16.ToDouble(pixels[cell]);
        }
    }
}
