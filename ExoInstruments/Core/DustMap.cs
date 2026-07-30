using System;
using System.IO;

namespace ExoInstruments.Core
{
    /// <summary>
    /// An all-sky reddening map: E(B-V) integrated through the whole Galaxy along a sight line.
    ///
    /// WHAT IT IS FOR, AND WHAT IT IS NOT FOR. This is the TOTAL column, so it applies to something
    /// beyond all the dust, an external galaxy, or a quasar. It does NOT apply to a catalogue
    /// star: a star sits inside the Galaxy with an unknown fraction of the column in front of it,
    /// and Gaia's gspphot already publishes a per-source estimate that needs no distance of ours
    /// (see RenderedStar.ReddeningEBv). Using this for a star would over-redden every foreground
    /// one, by up to the whole column.
    ///
    /// Its other job is simply to be reported. Extinction toward the field is a number a real
    /// observer records with a frame, and the FITS header carries it.
    ///
    /// FORMAT. HEALPix, Galactic coordinates, one IEEE 754 half float per pixel. Half rather than
    /// a scaled integer because SFD98 spans 0.00037 to 135 magnitudes and no fixed-point scale
    /// covers both ends; see Float16. At the map's own 6.1 arcmin resolution the whole sky is
    /// nside 1024 and 24 MB, so it loads whole and needs no block index. tools/pack_dust_map.py writes it.
    ///
    /// NOTHING SHIPS, for the same reason the star catalogue ships nothing: the map is a published
    /// dataset with its own licence and its own download. With no file installed every query
    /// returns NaN and every caller treats that as "not known" rather than as zero.
    ///
    /// Pure C# apart from the file read.
    /// </summary>
    public sealed class DustMap
    {
        private static readonly byte[] Magic = { (byte)'E', (byte)'X', (byte)'O', (byte)'D', (byte)'U', (byte)'S', (byte)'T', (byte)'1' };
        private const int FormatVersion = 2;

        private ushort[] pixels;   // IEEE 754 binary16, see Float16
        private int nside;
        private bool nested;

        public bool IsLoaded => pixels != null;
        public int Nside => nside;
        public double ResolutionArcmin => IsLoaded ? Healpix.PixelResolutionDeg(nside) * 60.0 : 0.0;

        /// <summary>Provenance string from the file, so a frame can say which map it was measured against.</summary>
        public string Source { get; private set; }

        public void Load(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                for (int i = 0; i < Magic.Length; i++)
                    if (magic.Length != Magic.Length || magic[i] != Magic[i])
                        throw new InvalidDataException("not an ExoInstruments packed dust map");

                int version = reader.ReadInt32();
                if (version != FormatVersion) throw new InvalidDataException("unsupported dust map version " + version);

                int n = reader.ReadInt32();
                if (!Healpix.IsValidNside(n)) throw new InvalidDataException("bad nside " + n);
                bool isNested = reader.ReadByte() != 0;

                int sourceLength = reader.ReadInt32();
                if (sourceLength < 0 || sourceLength > 4096) throw new InvalidDataException("bad provenance length");
                string source = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(sourceLength));

                long count = Healpix.PixelCount(n);
                if (count > int.MaxValue) throw new InvalidDataException("map too large to hold");
                var values = new ushort[count];
                for (long i = 0; i < count; i++) values[i] = reader.ReadUInt16();

                pixels = values;
                nside = n;
                nested = isNested;
                Source = source;
            }
        }

        /// <summary>
        /// Total Galactic E(B-V) toward an equatorial position, magnitudes. NaN when no map is
        /// loaded or the map has no value there.
        ///
        /// Bilinearly interpolated between the four surrounding pixels, which is what a map
        /// smoothed to a beam (SFD98's is 6.1 arcmin) is a sampling of; see
        /// Healpix.InterpolationWeights. Taking the containing pixel instead would make the
        /// reddening jump discontinuously between two stars a few arcmin apart, structure the
        /// survey does not contain.
        /// </summary>
        public double ReddeningAt(double raDeg, double decDeg)
        {
            if (pixels == null) return double.NaN;

            GalacticCoordinates.EquatorialToGalactic(raDeg, decDeg, out double l, out double b);
            Healpix.InterpolationWeightsDegrees(nside, l, b, scratchPixels, scratchWeights);

            // NaN in the file means the source map had no measurement there. Such a pixel is
            // dropped and the rest reweighted rather than counted as zero reddening, which would
            // darken the rim of every gap.
            double sum = 0.0, weight = 0.0;
            for (int i = 0; i < 4; i++)
            {
                long p = scratchPixels[i];
                if (p < 0 || p >= pixels.Length) continue;
                if (nested) p = Healpix.RingToNested(nside, p);
                double v = Float16.ToDouble(pixels[p]);
                if (double.IsNaN(v)) continue;
                sum += scratchWeights[i] * v;
                weight += scratchWeights[i];
            }
            return weight > 0.0 ? sum / weight : double.NaN;
        }

        private readonly long[] scratchPixels = new long[4];
        private readonly double[] scratchWeights = new double[4];

        /// <summary>Total extinction at V toward a position, magnitudes: A(V) = R_V E(B-V).</summary>
        public double ExtinctionAtV(double raDeg, double decDeg, double rv = InterstellarExtinction.MilkyWayRv)
        {
            double ebv = ReddeningAt(raDeg, decDeg);
            return double.IsNaN(ebv) ? double.NaN : InterstellarExtinction.AvFromReddening(ebv, rv);
        }
    }
}
