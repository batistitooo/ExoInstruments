using System;
using System.Collections.Generic;
using System.IO;

namespace ExoInstruments.Core
{
    /// <summary>The five supernova classes the template set carries. Order matches the packed file.</summary>
    public enum SupernovaClass
    {
        Ia,
        Ibc,
        IIP,
        IIL,
        IIn,
    }

    /// <summary>
    /// One class's spectral time series: a measured spectrum at every phase, plus the two
    /// magnitude tracks the packer precomputed from it.
    ///
    /// The photometry convention is exactly the stellar one. BOffset anchors the light curve in
    /// real Bessell B, so a peak absolute magnitude drawn from Richardson et al. (2014) applies
    /// directly; VAnchor bridges to the magnitude the mod calls V (photon density at 5556 A
    /// against PhotonFluxModel's own zero point), so PhotonFluxModel.CollectedElectrons prices a
    /// supernova and a catalogue star on one scale; Shape is the photon-density spectrum
    /// normalised to 1 at 5556 A, which is the Planck curve's seat in SystemResponse's integral
    /// with a measurement sitting in it instead.
    /// </summary>
    public sealed class SupernovaTemplate
    {
        public SupernovaClass Class;

        /// <summary>Days since explosion for each stored epoch, ascending.</summary>
        public double[] PhaseDays;

        /// <summary>B(phase) - B(peak), magnitudes. Zero at the template's own B peak.</summary>
        public float[] BOffsetMag;

        /// <summary>V_mod(phase) - B(peak), magnitudes.</summary>
        public float[] VAnchorMag;

        /// <summary>Wavelength grid: WavelengthMinA + i * WavelengthStepA, Angstrom.</summary>
        public double WavelengthMinA;
        public double WavelengthStepA;
        public int WavelengthCount;

        /// <summary>[phase * WavelengthCount + i], photon density normalised to 1 at 5556 A.</summary>
        public float[] Shape;

        /// <summary>Days from explosion to the template's B peak.</summary>
        public double PeakPhaseDays
        {
            get
            {
                int best = 0;
                for (int i = 1; i < BOffsetMag.Length; i++)
                    if (BOffsetMag[i] < BOffsetMag[best]) best = i;
                return PhaseDays[best];
            }
        }

        public double LastPhaseDays => PhaseDays[PhaseDays.Length - 1];

        /// <summary>
        /// How far below the template's B peak the extrapolation is allowed to carry the event
        /// before the model declares itself over. A horizon of validity, not physics: at twelve
        /// magnitudes down nothing in the roster separates the source from its host, and the
        /// frozen spectrum (see BOffsetAt) is by then years stale.
        /// </summary>
        public const double ExtrapolationFloorMag = 12.0;

        /// <summary>
        /// The measured decline rate at the template's end, mag/day: the slope of its own last
        /// segment. What the extrapolation continues, rather than a rate from elsewhere.
        /// </summary>
        public double FinalSlopeMagPerDay
        {
            get
            {
                int n = PhaseDays.Length;
                if (n < 2) return 0.0;
                double span = PhaseDays[n - 1] - PhaseDays[n - 2];
                return span > 0.0 ? (BOffsetMag[n - 1] - BOffsetMag[n - 2]) / span : 0.0;
            }
        }

        /// <summary>
        /// Days from explosion to the end of the model: the measured span, then the linear-in-
        /// magnitude extrapolation down to ExtrapolationFloorMag below peak. Radioactive decay is
        /// exponential, so linear magnitudes past the template is the tail's own functional form
        /// continued at the template's own final rate; a template that ends while still
        /// brightening (none does) would get no extrapolation at all.
        /// </summary>
        public double ActiveDays
        {
            get
            {
                double slope = FinalSlopeMagPerDay;
                if (!(slope > 1e-6)) return LastPhaseDays;
                double remaining = ExtrapolationFloorMag - BOffsetMag[BOffsetMag.Length - 1];
                return remaining > 0.0 ? LastPhaseDays + remaining / slope : LastPhaseDays;
            }
        }

        /// <summary>
        /// B(phase) - B(peak): interpolated inside the template, then extrapolated at the
        /// template's own final slope out to ActiveDays. The extrapolation is grey (every band
        /// declines at the B rate) and the spectrum freezes at the last measured epoch, both
        /// stated in section 12: past the data, the decline is the tail's measured rate and the
        /// colours are the last measurement held, not a model of nebular evolution.
        /// </summary>
        public double BOffsetAt(double phaseDays)
        {
            return Track(BOffsetMag, phaseDays);
        }

        /// <summary>V_mod(phase) - B(peak), same domain and same extrapolation as BOffsetAt.</summary>
        public double VAnchorAt(double phaseDays)
        {
            return Track(VAnchorMag, phaseDays);
        }

        private double Track(float[] track, double phaseDays)
        {
            if (phaseDays > LastPhaseDays)
            {
                if (phaseDays > ActiveDays) return double.PositiveInfinity;
                return track[track.Length - 1] + FinalSlopeMagPerDay * (phaseDays - LastPhaseDays);
            }
            return Interpolate(track, phaseDays, double.PositiveInfinity);
        }

        /// <summary>
        /// The spectrum at a phase, as the photon-density shape SystemResponse integrates,
        /// interpolated between the two bracketing epochs; past the last epoch the shape is the
        /// last measurement held while the magnitude tracks extrapolate. Null before the
        /// explosion and past ActiveDays.
        /// </summary>
        public SpectralCurve ShapeAt(double phaseDays)
        {
            if (PhaseDays.Length == 0 || phaseDays < PhaseDays[0] || phaseDays > ActiveDays)
                return null;
            if (phaseDays > LastPhaseDays) phaseDays = LastPhaseDays;   // frozen past the data

            Locate(phaseDays, out int lo, out int hi, out double t);

            var wavelengthsNm = new double[WavelengthCount];
            var values = new double[WavelengthCount];
            int a = lo * WavelengthCount, b = hi * WavelengthCount;
            for (int i = 0; i < WavelengthCount; i++)
            {
                wavelengthsNm[i] = (WavelengthMinA + i * WavelengthStepA) * 0.1;
                values[i] = Shape[a + i] + (Shape[b + i] - Shape[a + i]) * t;
            }
            return new SpectralCurve(wavelengthsNm, values);
        }

        private double Interpolate(float[] track, double phaseDays, double outside)
        {
            if (PhaseDays.Length == 0 || phaseDays < PhaseDays[0] || phaseDays > LastPhaseDays)
                return outside;
            Locate(phaseDays, out int lo, out int hi, out double t);
            return track[lo] + (track[hi] - track[lo]) * t;
        }

        private void Locate(double phaseDays, out int lo, out int hi, out double t)
        {
            lo = 0;
            for (int i = 1; i < PhaseDays.Length; i++)
            {
                if (PhaseDays[i] <= phaseDays) lo = i;
                else break;
            }
            hi = Math.Min(lo + 1, PhaseDays.Length - 1);
            double span = PhaseDays[hi] - PhaseDays[lo];
            t = span > 0.0 ? (phaseDays - PhaseDays[lo]) / span : 0.0;
        }
    }

    /// <summary>
    /// The packed template file, written by tools/pack_supernova_templates.py, shipped in
    /// PluginData. Pure C#: the harness loads it from a path exactly as the game does.
    /// </summary>
    public sealed class SupernovaTemplateSet
    {
        private static readonly byte[] Magic =
        {
            (byte)'E', (byte)'X', (byte)'O', (byte)'S', (byte)'N', (byte)'T', (byte)'P', (byte)'1',
        };

        private readonly Dictionary<SupernovaClass, SupernovaTemplate> byClass =
            new Dictionary<SupernovaClass, SupernovaTemplate>();

        public string Source { get; private set; }

        public SupernovaTemplate Get(SupernovaClass cls)
        {
            return byClass.TryGetValue(cls, out SupernovaTemplate t) ? t : null;
        }

        public static SupernovaTemplateSet Load(string path)
        {
            var set = new SupernovaTemplateSet();
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length) throw new InvalidDataException("not a supernova template set");
                for (int i = 0; i < Magic.Length; i++)
                    if (magic[i] != Magic[i]) throw new InvalidDataException("not a supernova template set");

                int version = reader.ReadInt32();
                if (version != 1) throw new InvalidDataException("unsupported template version " + version);

                int count = reader.ReadInt32();
                set.Source = ReadString(reader);

                for (int k = 0; k < count; k++)
                {
                    string name = ReadString(reader);
                    var template = new SupernovaTemplate
                    {
                        Class = (SupernovaClass)Enum.Parse(typeof(SupernovaClass), name, false),
                    };

                    int phases = reader.ReadInt32();
                    if (phases <= 0 || phases > 100000) throw new InvalidDataException("implausible phase count");
                    template.PhaseDays = new double[phases];
                    for (int i = 0; i < phases; i++) template.PhaseDays[i] = reader.ReadDouble();

                    template.WavelengthCount = reader.ReadInt32();
                    if (template.WavelengthCount <= 0 || template.WavelengthCount > 1000000)
                        throw new InvalidDataException("implausible wavelength count");
                    template.WavelengthMinA = reader.ReadDouble();
                    template.WavelengthStepA = reader.ReadDouble();

                    template.BOffsetMag = ReadFloats(reader, phases);
                    template.VAnchorMag = ReadFloats(reader, phases);

                    int n = phases * template.WavelengthCount;
                    template.Shape = new float[n];
                    byte[] raw = reader.ReadBytes(n * 2);
                    if (raw.Length != n * 2) throw new InvalidDataException("truncated template");
                    for (int i = 0; i < n; i++)
                        template.Shape[i] = (float)Float16.ToDouble(BitConverter.ToUInt16(raw, i * 2));

                    set.byClass[template.Class] = template;
                }
            }
            return set;
        }

        private static float[] ReadFloats(BinaryReader reader, int count)
        {
            var v = new float[count];
            for (int i = 0; i < count; i++) v[i] = reader.ReadSingle();
            return v;
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 4096) throw new InvalidDataException("implausible string length");
            return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
    }
}
