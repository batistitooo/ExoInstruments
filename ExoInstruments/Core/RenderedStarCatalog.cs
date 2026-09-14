using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExoInstruments.Core
{
    /// <summary>One catalogue star as the imaging pipeline needs it: where it is, how bright it is, and what colour.</summary>
    public struct RenderedStar
    {
        public double RaDeg;
        public double DecDeg;
        /// <summary>Johnson V apparent magnitude.</summary>
        public double VMag;
        /// <summary>
        /// Johnson B-V colour index, or NaN when the catalogue has no colour for this star. OBSERVED, so it
        /// carries the star's reddening; see ReddeningEBv.
        /// </summary>
        public double ColorIndexBV;

        /// <summary>
        /// Interstellar reddening toward this star, or NaN when the catalogue has none.
        ///
        /// Gaia's own gspphot estimate, not a sight-line average: it comes from fitting an
        /// atmosphere model to the star's own BP/RP spectrum and parallax, so it applies to this
        /// star at its own distance. NaN means the fit had no solution, and the photometry then
        /// behaves exactly as it did before the column existed.
        /// </summary>
        public double ReddeningEBv;

        /// <summary>
        /// Electrons already computed by the caller, or zero for the ordinary case. A transient's
        /// electrons come from its own measured spectrum through the spectrum overload of the
        /// bandpass, which the per-star callback cannot express; carrying the result here lets a
        /// supernova ride the SAME deposit path as every star, trails included, instead of a
        /// duplicate one.
        /// </summary>
        public double FixedElectrons;

        public bool HasColor => !double.IsNaN(ColorIndexBV);

        public bool HasReddening => !double.IsNaN(ReddeningEBv);
    }

    /// <summary>
    /// The star catalogue that gets DRAWN into a photograph, as opposed to the Bright Star
    /// Catalogue the exoplanet instruments hunt through.
    ///
    /// These are two different jobs and they want two different catalogues. The exoplanet side
    /// wants a short list a player can plausibly work through, which is why it uses the BSC's
    /// 9110 naked-eye stars and why that choice is deliberately left alone. A rendered frame
    /// wants completeness over a small solid angle: at 0.22 BSC stars per square degree, a
    /// 0.07 deg^2 frame contains one BSC star about once in every 65 exposures, which is why
    /// the sky came out empty. A Gaia DR3 catalogue built with tools/pack_gaia_catalog.py carries
    /// thousands of stars per square degree, so a real and correctly-placed star field lands in
    /// every frame. Nothing here touches the detection pipeline.
    ///
    /// NOTHING SHIPS. The catalogue is user-supplied, because the useful depths cannot be
    /// distributed: Gaia's own counts put G &lt; 14 at 236 MB and G &lt; 16 at 1.1 GB in this
    /// format, and every source in DR3 at 25.3 GB. A Tycho-2 file used to ship and was the worst
    /// of both worlds, 29.3 MB carried to deliver about four stars per RC20 frame. With no file
    /// installed the sky behind a photographed body is simply empty, which is honest rather than
    /// misleadingly sparse.
    ///
    /// The file is memory mapped rather than read, so its size is paid in disk, not in memory: only
    /// the band index stays resident, and the operating system pages in the bands a search touches.
    /// A search reads every record inside the field whatever its magnitude cut, which for a wide
    /// field on the all-sky file is a great many; TieredStarCatalog is what bounds that.
    ///
    /// Pure C# apart from the file access, with no Unity or KSP types, so a search can run on the
    /// background imaging thread.
    /// </summary>
    public sealed class RenderedStarCatalog : IDisposable
    {
        private static readonly byte[] Magic = { (byte)'E', (byte)'X', (byte)'O', (byte)'S', (byte)'T', (byte)'A', (byte)'R', (byte)'1' };

        // Must match tools/pack_gaia_catalog.py, which writes the file.
        private const int FormatVersion = 3;

        // Version 2 files still load; they simply carry no reddening column, and every star reads as "not
        // estimated".
        private const int OldestSupportedVersion = 2;

        private const double VMagOffset = 2.0;
        private const short BvUnknown = -32768;
        private const ushort EbvUnknown = 65535;

        // Positions are fixed point over a full turn, not float32 degrees. A float32 near RA = 360 deg resolves
        // only 0.077 arcsec, which is harmless at the RC20's 1.1 arcsec/px but is forty-three pixels at
        // SPHERE/ZIMPOL's ~1.8 mas plate scale. Fixed point gives a uniform 360/2^32 = 0.3 mas everywhere for
        // the same four bytes, and the raw integers stay monotonic in RA so the binary search runs on them
        // directly.
        private const double RaDegPerUnit = 360.0 / 4294967296.0;
        private const double DecDegPerUnit = 180.0 / 4294967296.0;

        // Records copied out of the mapping per block while scanning. Reading field by field through the
        // accessor was ten to twenty times slower under Mono, which is what KSP runs.
        private const int BlockRecords = 65536;

        private MemoryMappedFile mapping;
        private MemoryMappedViewAccessor view;
        private long recordsOffset;
        private int recordBytes;
        private bool hasReddening;
        private int count;
        private uint[] bandStart;
        private int bandCount;
        private double bandWidthDeg;

        /// <summary>Number of stars held. Zero when no catalogue file was loaded.</summary>
        public int Count => count;

        /// <summary>True once a catalogue has been loaded successfully.</summary>
        public bool IsLoaded => count > 0 && view != null;

        /// <summary>Number of declination bands in the index.</summary>
        public int BandCount => bandCount;

        /// <summary>Width of one declination band, as the file stores it.</summary>
        public double BandWidthDeg => bandWidthDeg;

        /// <summary>Stars filed under one declination band.</summary>
        public int StarsInBand(int band) => (int)(bandStart[band + 1] - bandStart[band]);

        /// <summary>
        /// Maps the packed catalogue. Throws on a malformed file so the caller can log it and carry on
        /// without a star field, rather than rendering from half-read data.
        /// </summary>
        public void Load(string path)
        {
            // Records are decoded in the byte order they were written; a big-endian host would read the
            // binary search's keys reversed.
            if (!BitConverter.IsLittleEndian)
                throw new InvalidDataException("packed star catalogues are little-endian and this is a big-endian machine");

            Dispose();

            long fileLength;
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                for (int i = 0; i < Magic.Length; i++)
                {
                    if (magic.Length != Magic.Length || magic[i] != Magic[i])
                        throw new InvalidDataException("not an ExoInstruments packed star catalogue");
                }

                int version = reader.ReadInt32();
                if (version < OldestSupportedVersion || version > FormatVersion)
                    throw new InvalidDataException("unsupported catalogue version " + version);
                bool reddening = version >= 3;

                int stars = reader.ReadInt32();
                int bands = reader.ReadInt32();
                double width = reader.ReadSingle();
                if (stars < 0 || bands <= 0 || width <= 0.0)
                    throw new InvalidDataException("catalogue header is out of range");

                var starts = new uint[bands + 1];
                for (int i = 0; i <= bands; i++) starts[i] = reader.ReadUInt32();

                // Reading the records used to catch both faults for free; decoding by computed offset does not.
                int bytesPerRecord = reddening ? 14 : 12;
                long need = stream.Position + (long)stars * bytesPerRecord;
                if (stream.Length < need)
                    throw new InvalidDataException(
                        $"catalogue is truncated: {stars:N0} stars need {need:N0} bytes and the file is {stream.Length:N0}");
                for (int i = 0; i < bands; i++)
                {
                    if (starts[i] > starts[i + 1])
                        throw new InvalidDataException("catalogue band index is not monotonic");
                }
                if (starts[bands] > (uint)stars)
                    throw new InvalidDataException("catalogue band index runs past the last star");

                recordsOffset = stream.Position;
                fileLength = stream.Length;
                recordBytes = bytesPerRecord;
                hasReddening = reddening;
                bandStart = starts;
                bandCount = bands;
                bandWidthDeg = width;
                count = stars;
            }

            if (count == 0) return;
            try
            {
                mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                view = mapping.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Releases the mapping. Safe to call twice, and on a catalogue never loaded.</summary>
        public void Dispose()
        {
            view?.Dispose();
            mapping?.Dispose();
            view = null;
            mapping = null;
            count = 0;
        }

        /// <summary>
        /// Every star within radiusDeg of the given direction and brighter than
        /// faintestVMag, appended to results.
        /// </summary>
        public void Search(double centreRaDeg, double centreDecDeg, double radiusDeg,
                           double faintestVMag, List<RenderedStar> results)
        {
            if (results == null) return;
            Search(centreRaDeg, centreDecDeg, radiusDeg, faintestVMag, results.Add);
        }

        /// <summary>
        /// Every star within radiusDeg of the given direction and brighter than faintestVMag, handed to
        /// visit one at a time, so a field of tens of millions of stars never has to be held at once.
        ///
        /// Scans only the declination bands the cone touches. The RA half-width of the cone
        /// grows as 1/cos(dec), since a cone of fixed angular radius spans more hours of RA the
        /// closer it sits to a pole, and it degenerates entirely over the pole itself, where the
        /// whole band is taken instead of trying to bracket an RA range that wraps.
        /// </summary>
        public void Search(double centreRaDeg, double centreDecDeg, double radiusDeg,
                           double faintestVMag, Action<RenderedStar> visit)
        {
            if (!IsLoaded || visit == null || radiusDeg <= 0.0) return;

            List<int> ranges = CandidateRanges(centreRaDeg, centreDecDeg, radiusDeg);
            int longest = 0;
            for (int i = 0; i < ranges.Count; i += 2) longest = Math.Max(longest, ranges[i + 1] - ranges[i]);
            if (longest == 0) return;
            byte[] block = new byte[Math.Min(longest, BlockRecords) * recordBytes];

            double cosRadius = Math.Cos(radiusDeg * Math.PI / 180.0);
            double centreDecRad = centreDecDeg * Math.PI / 180.0;
            double sinCentreDec = Math.Sin(centreDecRad);
            double cosCentreDec = Math.Cos(centreDecRad);
            ushort faintestMilli = ToMagMilli(faintestVMag);

            // Held for the whole scan, so a Dispose on another thread cannot unmap the pages under it.
            SafeMemoryMappedViewHandle handle = view.SafeMemoryMappedViewHandle;
            bool referenced = false;
            try
            {
                handle.DangerousAddRef(ref referenced);
                long firstRecord = handle.DangerousGetHandle().ToInt64() + view.PointerOffset + recordsOffset;
                for (int i = 0; i < ranges.Count; i += 2)
                {
                    ScanRange(firstRecord, ranges[i], ranges[i + 1], block,
                              sinCentreDec, cosCentreDec, centreRaDeg, cosRadius, faintestMilli, visit);
                }
            }
            finally
            {
                if (referenced) handle.DangerousRelease();
            }
        }

        /// <summary>
        /// Records a search of this cone reads, whatever its magnitude cut: its cost, found by the binary
        /// searches alone.
        /// </summary>
        public long CountCandidates(double centreRaDeg, double centreDecDeg, double radiusDeg)
        {
            if (!IsLoaded || radiusDeg <= 0.0) return 0;
            List<int> ranges = CandidateRanges(centreRaDeg, centreDecDeg, radiusDeg);
            long total = 0;
            for (int i = 0; i < ranges.Count; i += 2) total += ranges[i + 1] - ranges[i];
            return total;
        }

        // Record ranges [lo, hi) that can hold a star inside the cone, as consecutive pairs. The RA and
        // declination bracketing only narrows the candidates; ScanRange decides membership.
        private List<int> CandidateRanges(double centreRaDeg, double centreDecDeg, double radiusDeg)
        {
            var ranges = new List<int>();

            int firstBand = BandOf(centreDecDeg - radiusDeg);
            int lastBand = BandOf(centreDecDeg + radiusDeg);

            double cosRadius = Math.Cos(radiusDeg * Math.PI / 180.0);
            double centreDecRad = centreDecDeg * Math.PI / 180.0;
            double sinCentreDec = Math.Sin(centreDecRad);
            double cosCentreDec = Math.Cos(centreDecRad);

            for (int band = firstBand; band <= lastBand; band++)
            {
                int lo = (int)bandStart[band];
                int hi = (int)bandStart[band + 1];
                if (hi <= lo) continue;

                // RA half-width of the cone at this band's declination. Near a pole the
                // denominator vanishes and every RA is inside the cone, so the whole band is
                // scanned rather than bracketed.
                double bandDec = WidestRaDeclinationInBand(band, centreDecDeg);
                double cosBandDec = Math.Cos(bandDec * Math.PI / 180.0);
                double raHalfWidthDeg = 180.0;
                if (cosBandDec > 1e-6)
                {
                    double arg = (cosRadius - sinCentreDec * Math.Sin(bandDec * Math.PI / 180.0))
                               / (cosCentreDec * cosBandDec);
                    if (arg > 1.0) continue;             // no RA at this declination is close enough
                    if (arg > -1.0) raHalfWidthDeg = Math.Acos(arg) * 180.0 / Math.PI;
                }

                if (raHalfWidthDeg >= 180.0)
                {
                    AddRange(ranges, lo, hi);
                    continue;
                }

                double raLo = centreRaDeg - raHalfWidthDeg;
                double raHi = centreRaDeg + raHalfWidthDeg;
                if (raLo < 0.0 || raHi >= 360.0)
                {
                    // The RA window straddles 0h, so it is two ranges in a catalogue sorted on
                    // [0, 360). Both are found by the same binary search on the wrapped bounds.
                    AddRange(ranges, lo, UpperBound(lo, hi, ToRaFixed(raHi)));
                    AddRange(ranges, LowerBound(lo, hi, ToRaFixed(raLo)), hi);
                }
                else
                {
                    AddRange(ranges, LowerBound(lo, hi, ToRaFixed(raLo)), UpperBound(lo, hi, ToRaFixed(raHi)));
                }
            }
            return ranges;
        }

        private static void AddRange(List<int> ranges, int lo, int hi)
        {
            if (hi <= lo) return;
            ranges.Add(lo);
            ranges.Add(hi);
        }

        // Exact angular test on a bracketed range. Records are copied out a block at a time and decoded
        // little-endian by hand, which is what the file is.
        private void ScanRange(long firstRecord, int lo, int hi, byte[] block,
                               double sinCentreDec, double cosCentreDec,
                               double centreRaDeg, double cosRadius, ushort faintestMilli,
                               Action<RenderedStar> visit)
        {
            int perBlock = block.Length / recordBytes;
            for (int first = lo; first < hi; first += perBlock)
            {
                int n = Math.Min(perBlock, hi - first);
                Marshal.Copy(new IntPtr(firstRecord + (long)first * recordBytes), block, 0, n * recordBytes);

                for (int o = 0, end = n * recordBytes; o < end; o += recordBytes)
                {
                    ushort vMagMilli = (ushort)(block[o + 8] | block[o + 9] << 8);
                    if (vMagMilli > faintestMilli) continue;

                    double starRaDeg = (uint)(block[o] | block[o + 1] << 8 | block[o + 2] << 16 | block[o + 3] << 24) * RaDegPerUnit;
                    double starDecDeg = (block[o + 4] | block[o + 5] << 8 | block[o + 6] << 16 | block[o + 7] << 24) * DecDegPerUnit;
                    double decRad = starDecDeg * Math.PI / 180.0;
                    double deltaRa = (starRaDeg - centreRaDeg) * Math.PI / 180.0;
                    double cosSeparation = sinCentreDec * Math.Sin(decRad)
                                         + cosCentreDec * Math.Cos(decRad) * Math.Cos(deltaRa);
                    if (cosSeparation < cosRadius) continue;

                    short bvMilli = (short)(block[o + 10] | block[o + 11] << 8);
                    ushort ebvMilli = hasReddening ? (ushort)(block[o + 12] | block[o + 13] << 8) : EbvUnknown;

                    visit(new RenderedStar
                    {
                        RaDeg = starRaDeg,
                        DecDeg = starDecDeg,
                        VMag = vMagMilli / 1000.0 - VMagOffset,
                        ColorIndexBV = bvMilli == BvUnknown ? double.NaN : bvMilli / 1000.0,
                        ReddeningEBv = ebvMilli == EbvUnknown ? double.NaN : ebvMilli / 1000.0,
                    });
                }
            }
        }

        // RA of one record, read in place: the binary search wants nothing else.
        private uint RaFixedAt(int i) => view.ReadUInt32(recordsOffset + (long)i * recordBytes);

        private int LowerBound(int lo, int hi, uint ra)
        {
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (RaFixedAt(mid) < ra) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private int UpperBound(int lo, int hi, uint ra)
        {
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (RaFixedAt(mid) <= ra) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // Degrees to the file's fixed-point RA units, wrapping the full turn.
        private static uint ToRaFixed(double raDeg)
        {
            double wrapped = raDeg % 360.0;
            if (wrapped < 0.0) wrapped += 360.0;
            double units = wrapped / RaDegPerUnit;
            return units >= 4294967295.0 ? 4294967295u : (uint)units;
        }

        private int BandOf(double dec)
        {
            int b = (int)((dec + 90.0) / bandWidthDeg);
            return b < 0 ? 0 : (b >= bandCount ? bandCount - 1 : b);
        }

        // The declination inside this band at which the search cone spans the most right ascension, which is
        // what the RA bracket must be computed from if it is not to exclude stars the cone really contains.
        // That declination is the one CLOSEST TO THE CONE'S OWN CENTRE, because a cone's RA extent is widest at
        // its centre declination and shrinks to zero at its northern and southern extremes. This previously
        // returned the band edge nearest the EQUATOR, on the reasoning that the RA half-width grows as
        // 1/cos(dec). That reasoning holds for the small-angle approximation radius/cos(dec), but not for the
        // exact relation the search actually uses, cos(radius) = sin(dec0)sin(dec) + cos(dec0)cos(dec)cos(dRA)
        // where proximity to dec0 dominates. For every band on the equator side of the cone centre the two
        // choices disagree, and the equator-nearest edge is the FARTHEST from the centre, so it produced the
        // narrowest bracket exactly where the widest was needed. The effect was a thin crescent of stars
        // silently dropped at the edge of every search cone. It went unnoticed while the catalogue then shipped
        // put about four stars in a frame; it surfaced immediately against Gaia, where a 0.3 degree cone holds
        // 923 stars and 8 of them went missing.
        private double WidestRaDeclinationInBand(int band, double centreDecDeg)
        {
            double low = -90.0 + band * bandWidthDeg;
            double high = low + bandWidthDeg;
            return centreDecDeg < low ? low : (centreDecDeg > high ? high : centreDecDeg);
        }

        /// <summary>A V magnitude in the file's stored units, as the magnitude cut compares them.</summary>
        public static ushort ToMagMilli(double vMag)
        {
            double milli = (vMag + VMagOffset) * 1000.0;
            if (milli <= 0.0) return 0;
            return milli >= 65535.0 ? (ushort)65535 : (ushort)milli;
        }

    }
}
