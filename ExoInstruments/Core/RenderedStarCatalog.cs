using System;
using System.Collections.Generic;
using System.IO;

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
    /// distributed: Gaia's own counts put G &lt; 14 at 443 MB and G &lt; 16 at 1.9 GB in this
    /// format. A Tycho-2 file used to ship and was the worst of both worlds, 29.3 MB carried to
    /// deliver about four stars per RC20 frame. With no file installed the sky behind a
    /// photographed body is simply empty, which is honest rather than misleadingly sparse.
    ///
    /// Loaded once and held; a cone search reads only the declination bands the field of view
    /// actually overlaps, so search cost tracks the field rather than the catalogue.
    ///
    /// Pure C# apart from the file read, with no Unity or KSP types, so a search can run on the
    /// background imaging thread.
    /// </summary>
    public sealed class RenderedStarCatalog
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

        private uint[] raFixed;
        private int[] decFixed;
        private ushort[] vMagMilli;
        private short[] bvMilli;
        private ushort[] ebvMilli;
        private uint[] bandStart;
        private int bandCount;
        private double bandWidthDeg;

        /// <summary>Number of stars held. Zero when no catalogue file was loaded.</summary>
        public int Count => raFixed != null ? raFixed.Length : 0;

        /// <summary>True once a catalogue has been loaded successfully.</summary>
        public bool IsLoaded => Count > 0;

        /// <summary>
        /// Reads the packed catalogue. Throws on a malformed file so the caller can log it and
        /// carry on without a star field, rather than rendering from half-read data.
        /// </summary>
        public void Load(string path)
        {
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
                bool hasReddening = version >= 3;

                int count = reader.ReadInt32();
                bandCount = reader.ReadInt32();
                bandWidthDeg = reader.ReadSingle();
                if (count < 0 || bandCount <= 0 || bandWidthDeg <= 0.0)
                    throw new InvalidDataException("catalogue header is out of range");

                bandStart = new uint[bandCount + 1];
                for (int i = 0; i <= bandCount; i++) bandStart[i] = reader.ReadUInt32();

                raFixed = new uint[count];
                decFixed = new int[count];
                vMagMilli = new ushort[count];
                bvMilli = new short[count];
                ebvMilli = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    raFixed[i] = reader.ReadUInt32();
                    decFixed[i] = reader.ReadInt32();
                    vMagMilli[i] = reader.ReadUInt16();
                    bvMilli[i] = reader.ReadInt16();
                    ebvMilli[i] = hasReddening ? reader.ReadUInt16() : EbvUnknown;
                }
            }
        }

        /// <summary>
        /// Every star within radiusDeg of the given direction and brighter than
        /// faintestVMag, appended to results.
        ///
        /// Scans only the declination bands the cone touches. The RA half-width of the cone
        /// grows as 1/cos(dec), since a cone of fixed angular radius spans more hours of RA the
        /// closer it sits to a pole, and it degenerates entirely over the pole itself, where the
        /// whole band is taken instead of trying to bracket an RA range that wraps.
        /// </summary>
        public void Search(double centreRaDeg, double centreDecDeg, double radiusDeg,
                           double faintestVMag, List<RenderedStar> results)
        {
            if (!IsLoaded || results == null || radiusDeg <= 0.0) return;

            int firstBand = BandOf(centreDecDeg - radiusDeg);
            int lastBand = BandOf(centreDecDeg + radiusDeg);

            double cosRadius = Math.Cos(radiusDeg * Math.PI / 180.0);
            double centreDecRad = centreDecDeg * Math.PI / 180.0;
            double sinCentreDec = Math.Sin(centreDecRad);
            double cosCentreDec = Math.Cos(centreDecRad);
            ushort faintestMilli = ToMagMilli(faintestVMag);

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
                    ScanRange(lo, hi, sinCentreDec, cosCentreDec, centreRaDeg, cosRadius, faintestMilli, results);
                    continue;
                }

                double raLo = centreRaDeg - raHalfWidthDeg;
                double raHi = centreRaDeg + raHalfWidthDeg;
                if (raLo < 0.0 || raHi >= 360.0)
                {
                    // The RA window straddles 0h, so it is two ranges in a catalogue sorted on
                    // [0, 360). Both are found by the same binary search on the wrapped bounds.
                    ScanRange(lo, UpperBound(lo, hi, ToRaFixed(raHi)), sinCentreDec, cosCentreDec, centreRaDeg, cosRadius, faintestMilli, results);
                    ScanRange(LowerBound(lo, hi, ToRaFixed(raLo)), hi, sinCentreDec, cosCentreDec, centreRaDeg, cosRadius, faintestMilli, results);
                }
                else
                {
                    ScanRange(LowerBound(lo, hi, ToRaFixed(raLo)), UpperBound(lo, hi, ToRaFixed(raHi)),
                              sinCentreDec, cosCentreDec, centreRaDeg, cosRadius, faintestMilli, results);
                }
            }
        }

        // Exact angular test on a bracketed range; the RA/declination bracketing above only narrows the
        // candidates, it doesn't decide membership.
        private void ScanRange(int lo, int hi, double sinCentreDec, double cosCentreDec,
                               double centreRaDeg, double cosRadius, ushort faintestMilli,
                               List<RenderedStar> results)
        {
            for (int i = lo; i < hi; i++)
            {
                if (vMagMilli[i] > faintestMilli) continue;

                double starRaDeg = raFixed[i] * RaDegPerUnit;
                double starDecDeg = decFixed[i] * DecDegPerUnit;
                double decRad = starDecDeg * Math.PI / 180.0;
                double deltaRa = (starRaDeg - centreRaDeg) * Math.PI / 180.0;
                double cosSeparation = sinCentreDec * Math.Sin(decRad)
                                     + cosCentreDec * Math.Cos(decRad) * Math.Cos(deltaRa);
                if (cosSeparation < cosRadius) continue;

                results.Add(new RenderedStar
                {
                    RaDeg = starRaDeg,
                    DecDeg = starDecDeg,
                    VMag = vMagMilli[i] / 1000.0 - VMagOffset,
                    ColorIndexBV = bvMilli[i] == BvUnknown ? double.NaN : bvMilli[i] / 1000.0,
                    ReddeningEBv = ebvMilli[i] == EbvUnknown ? double.NaN : ebvMilli[i] / 1000.0,
                });
            }
        }

        private int LowerBound(int lo, int hi, uint ra)
        {
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (raFixed[mid] < ra) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private int UpperBound(int lo, int hi, uint ra)
        {
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (raFixed[mid] <= ra) lo = mid + 1; else hi = mid;
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

        private static ushort ToMagMilli(double vMag)
        {
            double milli = (vMag + VMagOffset) * 1000.0;
            if (milli <= 0.0) return 0;
            return milli >= 65535.0 ? (ushort)65535 : (ushort)milli;
        }

    }
}
