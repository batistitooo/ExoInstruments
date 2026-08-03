using System;
using System.Collections.Generic;
using System.IO;

namespace ExoInstruments.Core
{
    /// <summary>
    /// One catalogued galaxy, with everything needed to draw it and nothing that has to be guessed.
    /// </summary>
    public struct Galaxy
    {
        public string Name;
        public double RaDeg;
        public double DecDeg;

        /// <summary>Total apparent B magnitude, extrapolated to infinity the way RC3 and HyperLEDA define it.</summary>
        public double TotalBMag;
        /// <summary>B-V colour, or NaN when the catalogue has none.</summary>
        public double ColourBv;

        /// <summary>Major-axis diameter of the 25 B-mag/arcsec^2 isophote, arcminutes.</summary>
        public double D25Arcmin;
        /// <summary>Minor over major axis of that isophote.</summary>
        public double AxisRatio;
        /// <summary>Position angle of the major axis, degrees east of north.</summary>
        public double PositionAngleDeg;

        /// <summary>de Vaucouleurs numerical morphological type T: -6 elliptical through 10 irregular.</summary>
        public double MorphologicalType;
        /// <summary>Sersic index. Measured where the catalogue carries one, otherwise set from the type; see IsSersicIndexMeasured.</summary>
        public double SersicIndex;
        /// <summary>False when SersicIndex came from the morphological type rather than from a profile fit.</summary>
        public bool IsSersicIndexMeasured;

        public double SemiMajorArcsec => D25Arcmin * 60.0 * 0.5;

        /// <summary>
        /// Mean B surface brightness inside the D25 ellipse, magnitudes per square arcsecond: the
        /// total magnitude spread over the isophote's own area.
        ///
        /// This is the one combination of the catalogued numbers that can be sanity-checked without
        /// any model, because it is bounded by what the isophote MEANS. A galaxy whose edge is
        /// defined at 25 mag/arcsec^2 cannot average many magnitudes brighter than that inside it;
        /// over the shipped catalogue the first and ninety-ninth percentiles are 20.45 and 24.34.
        /// </summary>
        public double MeanSurfaceBrightnessB
        {
            get
            {
                double a = SemiMajorArcsec, b = a * AxisRatio;
                double area = Math.PI * a * b;
                return area > 0.0 ? TotalBMag + 2.5 * Math.Log10(area) : double.NaN;
            }
        }

        /// <summary>V from B and the catalogued colour, so the photometry chain has the band it anchors on. NaN colour leaves V unknown.</summary>
        public double TotalVMag => double.IsNaN(ColourBv) ? double.NaN : TotalBMag - ColourBv;
    }

    /// <summary>
    /// The galaxies, as a packed binary catalogue.
    ///
    /// WHAT A GALAXY NEEDS THAT A STAR DOES NOT. A star is a point source: a position and a
    /// magnitude, and the instrument's PSF decides the rest. A galaxy is resolved by every
    /// instrument in this roster; M31 is 3 degrees long, and even a distant one is arcseconds
    /// across against SPHERE's 3.6 mas pixels; so it needs a SHAPE as well, and the shape has to
    /// come from measurement rather than from an artist. Four catalogued quantities are enough:
    /// the total magnitude, the isophotal diameter D25, the axis ratio and the position angle.
    /// Those plus a profile (see SersicProfile) determine the surface brightness at every point.
    ///
    /// WHY D25 AND NOT A HALF-LIGHT RADIUS. D25 is what the wide-field catalogues actually measure
    /// and publish for tens of thousands of galaxies; half-light radii from profile fits exist for
    /// far fewer and only over the surveys that fitted them. The conversion is not a fudge either:
    /// the total magnitude and the isophotal diameter over-determine a two-parameter profile, so
    /// R_e follows from them exactly (SersicProfile.EffectiveRadiusFromIsophote) with no free
    /// constant. Where a catalogue does carry a fitted index, the packer stores it and the flag
    /// says so.
    ///
    /// NOTHING SHIPS. tools/pack_galaxy_catalog.py builds one from HyperLEDA.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public sealed class GalaxyCatalog
    {
        private static readonly byte[] Magic = { (byte)'E', (byte)'X', (byte)'O', (byte)'G', (byte)'A', (byte)'L', (byte)'X', (byte)'1' };
        private const int FormatVersion = 1;

        private Galaxy[] galaxies;
        private double[] decSorted;   // declinations of `galaxies`, ascending, for the cone search
        private double largestSemiMajorDeg;

        public bool IsLoaded => galaxies != null;
        public int Count => galaxies != null ? galaxies.Length : 0;
        public string Source { get; private set; }

        /// <summary>
        /// Rows dropped at load because their magnitude and their size cannot both be right; see
        /// the check in Load. Reported rather than silent, because dropping catalogue rows is
        /// something the log should say out loud.
        /// </summary>
        public int RejectedAsImplausible { get; private set; }

        /// <summary>
        /// Mean surface brightness inside D25, in B mag/arcsec^2, at which a catalogue row stops
        /// being a galaxy and becomes an error.
        ///
        /// The isophote that defines D25 is drawn at 25 mag/arcsec^2, and the light inside it
        /// averages a few magnitudes brighter than that: 20.45 at the first percentile of the
        /// shipped catalogue, 22.64 at the median. 18 leaves two and a half magnitudes of margin
        /// below anything real and still catches the entries that are seven magnitudes out.
        /// </summary>
        public const double ImplausibleSurfaceBrightnessB = 18.0;

        public void Load(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length)
                    throw new InvalidDataException("not an ExoInstruments packed galaxy catalogue");
                for (int i = 0; i < Magic.Length; i++)
                    if (magic[i] != Magic[i])
                        throw new InvalidDataException("not an ExoInstruments packed galaxy catalogue");

                int version = reader.ReadInt32();
                if (version != FormatVersion) throw new InvalidDataException("unsupported galaxy catalogue version " + version);

                int count = reader.ReadInt32();
                if (count < 0 || count > 20_000_000) throw new InvalidDataException("implausible galaxy count " + count);
                string source = ReadString(reader, 4096);

                var accepted = new List<Galaxy>(count);
                int rejected = 0;
                for (int i = 0; i < count; i++)
                {
                    var g = new Galaxy
                    {
                        Name = ReadString(reader, 64),
                        RaDeg = reader.ReadDouble(),
                        DecDeg = reader.ReadDouble(),
                        TotalBMag = reader.ReadSingle(),
                        ColourBv = reader.ReadSingle(),
                        D25Arcmin = reader.ReadSingle(),
                        AxisRatio = reader.ReadSingle(),
                        PositionAngleDeg = reader.ReadSingle(),
                        MorphologicalType = reader.ReadSingle(),
                        SersicIndex = reader.ReadSingle(),
                    };
                    g.IsSersicIndexMeasured = reader.ReadByte() != 0;

                    // A row whose magnitude and size cannot both be right is dropped rather than
                    // drawn. This is not hypothetical: HyperLEDA at B <= 13 carries one entry,
                    // PGC 779349, at B_T 8.30 in a D25 of 0.34 arcmin, i.e. a mean surface
                    // brightness of 13.6 mag/arcsec^2 where the catalogue's own first percentile is
                    // 20.45. Nothing that size is that bright; it renders as a saturated white disc
                    // several arcseconds across with a bleed down its column, and it sits on the sky
                    // chart looking like one of the brightest galaxies in the sky.
                    if (!(g.MeanSurfaceBrightnessB > ImplausibleSurfaceBrightnessB))
                    {
                        rejected++;
                        continue;
                    }
                    accepted.Add(g);
                }

                Galaxy[] list = accepted.ToArray();

                // The packer writes in ascending declination; sorting here as well costs nothing on
                // a catalogue this size and means a hand-edited file cannot silently break the
                // cone search's bisection.
                Array.Sort(list, (a, b) => a.DecDeg.CompareTo(b.DecDeg));
                var decs = new double[list.Length];
                for (int i = 0; i < list.Length; i++) decs[i] = list[i].DecDeg;

                largestSemiMajorDeg = 0.0;
                for (int i = 0; i < list.Length; i++)
                {
                    double half = list[i].D25Arcmin / 120.0;
                    if (half > largestSemiMajorDeg) largestSemiMajorDeg = half;
                }

                galaxies = list;
                decSorted = decs;
                Source = source;
                RejectedAsImplausible = rejected;
            }
        }

        private static string ReadString(BinaryReader reader, int limit)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > limit) throw new InvalidDataException("bad string length");
            return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        /// <summary>
        /// Every galaxy whose CENTRE lies within radiusDeg of a direction, plus those whose centre
        /// lies outside it but whose own extent reaches in; a two-degree galaxy half a degree off
        /// the edge still lights up the frame, and dropping it would put a straight edge across the
        /// image where its disk was cut off.
        /// </summary>
        public List<Galaxy> Search(double raDeg, double decDeg, double radiusDeg, double magnitudeLimit)
        {
            var found = new List<Galaxy>();
            if (galaxies == null) return found;

            // The largest object in the catalogue decides how far outside the cone to look; it is
            // measured once at load rather than per search, which would be a full scan per frame.
            double searchDeg = Math.Min(180.0, radiusDeg + largestSemiMajorDeg);

            int lo = LowerBound(decSorted, decDeg - searchDeg);
            int hi = LowerBound(decSorted, decDeg + searchDeg);

            double cosD = Math.Cos(decDeg * Math.PI / 180.0);
            double sinD = Math.Sin(decDeg * Math.PI / 180.0);

            for (int i = lo; i < hi && i < galaxies.Length; i++)
            {
                Galaxy g = galaxies[i];
                if (!(g.TotalBMag <= magnitudeLimit)) continue;

                double gd = g.DecDeg * Math.PI / 180.0;
                double dRa = (g.RaDeg - raDeg) * Math.PI / 180.0;
                double cosSep = sinD * Math.Sin(gd) + cosD * Math.Cos(gd) * Math.Cos(dRa);
                double sepDeg = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosSep))) * 180.0 / Math.PI;

                if (sepDeg <= radiusDeg + g.D25Arcmin / 120.0) found.Add(g);
            }
            return found;
        }

        /// <summary>
        /// One galaxy by its catalogue designation. Needed by the shape-map layer: a map that
        /// swallows a close companion is normalised to the SUM of the catalogued fluxes, so the
        /// companion's own entry has to be findable by the name the map stored.
        /// </summary>
        public bool TryGetByName(string name, out Galaxy galaxy)
        {
            galaxy = default(Galaxy);
            if (galaxies == null || string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < galaxies.Length; i++)
            {
                if (string.Equals(galaxies[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    galaxy = galaxies[i];
                    return true;
                }
            }
            return false;
        }

        private static int LowerBound(double[] sorted, double value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid] < value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Sersic index implied by a de Vaucouleurs morphological type, for the catalogue entries
        /// that carry no fitted one.
        ///
        /// Not an interpolation anyone invented: the two anchors are the two classical profile
        /// laws, de Vaucouleurs (1948) R^(1/4) for spheroids, which is n = 4, and Freeman (1970)
        /// exponential for disks, which is n = 1. Lenticulars sit between because they are
        /// structurally between; Graham &amp; Worley (2008, MNRAS 388, 1708) find bulge-to-total
        /// ratios falling monotonically from S0 through Sd, and a single-component fit to a
        /// two-component galaxy returns an index that tracks that ratio.
        ///
        /// Stated as a step function of type rather than a smooth fit, because a smooth fit would
        /// claim a precision the underlying relation does not have; its scatter at fixed type is
        /// about a factor of two in n.
        /// </summary>
        public static double SersicIndexForType(double morphologicalType)
        {
            if (double.IsNaN(morphologicalType)) return 1.0;
            if (morphologicalType <= -4.0) return 4.0;   // E
            if (morphologicalType <= -1.0) return 3.0;   // E-S0
            if (morphologicalType <= 0.5) return 2.0;    // S0-S0/a
            if (morphologicalType <= 2.5) return 1.5;    // Sa-Sab
            return 1.0;                                   // Sb and later, and irregulars
        }
    }
}
