using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// One supernova: where, when, what kind, and how bright at peak. Everything else follows
    /// from the template.
    /// </summary>
    public struct SupernovaEvent
    {
        /// <summary>Catalogue name of the host galaxy.</summary>
        public string HostName;

        /// <summary>Stable identity across sessions: host name plus explosion time, to the second.</summary>
        public string Key => HostName + ":" + ((long)ExplosionUt).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Universal time of the explosion (template phase zero), seconds.</summary>
        public double ExplosionUt;

        public SupernovaClass Class;

        /// <summary>True for the 12 per cent of SNe II that are IIb: rendered with the Ibc template (a IIb sheds its hydrogen within days) at IIb's own peak magnitude.</summary>
        public bool IsIIb;

        /// <summary>Peak absolute B magnitude, drawn from the class's measured distribution.</summary>
        public double PeakAbsoluteBMag;

        /// <summary>Position on the sky, resolved from the host's own light distribution.</summary>
        public double RaDeg;
        public double DecDeg;

        /// <summary>Days since explosion at the given UT. Negative before it.</summary>
        public double PhaseDaysAt(double ut) => (ut - ExplosionUt) / 86400.0;
    }

    /// <summary>
    /// Where supernovae come from and when: the occurrence model, deterministic from one seed.
    ///
    /// RATES. Li et al. (2011), MNRAS 412, 1473 ("LOSS III"), Table 4: rates in SNuB (SNe per
    /// century per 10^10 L_B_sun) for a fiducial galaxy of L_B0 = 2x10^10 L_sun, by Hubble type,
    /// with the rate-size relation SNuB(L) = SNuB(L0) * (L/L0)^RSS from the same table. The
    /// galaxy's L_B comes from its catalogued B_T and distance modulus with M_B_sun = +5.44
    /// (Willmer 2018, ApJS 236, 47). A galaxy with no distance modulus hosts nothing: its
    /// luminosity is unknown, and so would be everything about any event in it.
    ///
    /// SUBTYPES. Within SNe II, the volume-limited fractions of Li et al. (2011), MNRAS 412,
    /// 1441 ("LOSS II"): II-P 70%, II-L 10%, IIb 12%, IIn 9% (renormalised over their own sum).
    /// Within Ibc the same paper measures Ic as 54% of the class; the remainder is drawn as Ib.
    ///
    /// PEAK MAGNITUDES. Richardson et al. (2014), AJ 147, 118: mean peak M_B and its standard
    /// deviation per class, as observed. AS OBSERVED is what makes host extinction free of double
    /// counting: those distributions already contain it, so no separate host-extinction term is
    /// drawn, and only the FOREGROUND Galactic reddening from the dust map is applied on top.
    ///
    /// DETERMINISM. Events are a pure function of (save seed, host name, 200-year block index):
    /// the same save always has the same supernovae, nothing is persisted but the seed, and a
    /// reload cannot reroll history. The block length only has to exceed the longest template
    /// span so an event never straddles more than one boundary unnoticed; queries check the
    /// neighbouring block for events still shining across it.
    /// </summary>
    public static class Supernovae
    {
        /// <summary>Seconds per 200-year block of event generation, in Earth days as every timescale here (nuclear decay does not care whose calendar it is).</summary>
        public const double BlockSeconds = 200.0 * 365.25 * 86400.0;

        private const double SolarAbsoluteBMag = 5.44;   // Willmer 2018, ApJS 236, 47 (Johnson B)

        /// <summary>
        /// Brightest absolute B a row may claim and still be treated as a galaxy.
        ///
        /// The brightest cD galaxies reach about M_B = -23; brighter than that, a catalogued
        /// "galaxy" whose photometry says -27 is an ACTIVE NUCLEUS, and its B_T is quasar light
        /// rather than the stellar population Li's SNuB is per unit of. The shipped catalogue
        /// carries three (SDSS J102724.35+413820.2 at M_B = -27.8 among them), and unbounded they
        /// produced 650 supernovae per century each: a thousand of the catalogue's three thousand,
        /// concentrated in three objects nobody can resolve. Half a magnitude of headroom past the
        /// physical limit, then the row hosts nothing.
        /// </summary>
        private const double BrightestPlausibleGalaxyAbsoluteB = -23.5;

        /// <summary>
        /// Luminosity span the rate-size relation is extrapolated over, in units of L_B0.
        ///
        /// Li et al. fit RSS over their own sample; the relation is a power law with no physics
        /// forbidding its continuation, but a rate quoted 100x outside the calibrated range is an
        /// extrapolation and not a measurement. The factor of ten each way covers the sample and
        /// is where the clamp bites, which section 12 records.
        /// </summary>
        private const double RateSizeSpan = 10.0;
        private const double FiducialL10 = 2.0;          // Li 2011 Table 4: L_B0 = 2x10^10 L_sun
        private const double RssIa = -0.23;              // Li 2011 Table 4, RSS_B per class
        private const double RssIbc = -0.27;
        private const double RssII = -0.27;

        // Li et al. 2011 Table 4, SNuB for the fiducial galaxy, by Hubble bin: Ia, Ibc, II.
        // Bins map onto de Vaucouleurs T with the RC3 type codes (E: T<-2.5; S0: -2.5..0.5;
        // Sa/Sab: 0.5..2.5; Sb: 2.5..3.5; Sbc: 3.5..4.5; Sc: 4.5..6.5; Scd/Sd: 6.5..8.5;
        // Sm/Irr above). The E-Ia entry is 0.305; the E-II and Irr-Ia entries are published
        // upper limits consistent with zero and carried as zero.
        private static readonly double[][] SnuB =
        {
            //            Ia     Ibc    II
            new[] { 0.305, 0.018, 0.000 },   // E
            new[] { 0.282, 0.038, 0.022 },   // S0
            new[] { 0.271, 0.244, 0.296 },   // Sab
            new[] { 0.217, 0.239, 0.334 },   // Sb
            new[] { 0.198, 0.274, 0.557 },   // Sbc
            new[] { 0.200, 0.279, 0.730 },   // Sc
            new[] { 0.165, 0.177, 0.677 },   // Scd
            new[] { 0.000, 0.287, 0.382 },   // Irr
        };

        // Richardson et al. 2014, AJ 147, 118: mean peak M_B and sigma, as observed.
        private static readonly double[] PeakMeanMag = { -19.25, -17.45, -17.66, -16.99, -16.75, -17.98, -18.53 };
        private static readonly double[] PeakSigmaMag = { 0.50, 1.12, 1.18, 0.92, 0.98, 0.86, 1.36 };
        private enum PeakRow { Ia = 0, Ib = 1, Ic = 2, IIb = 3, IIP = 4, IIL = 5, IIn = 6 }

        // Li et al. 2011 (LOSS II) volume-limited fractions within SNe II, renormalised.
        private const double FractionIIP = 0.70;
        private const double FractionIIL = 0.10;
        private const double FractionIIb = 0.12;
        private const double FractionIIn = 0.09;

        /// <summary>Ic as a fraction of SNe Ibc (Li et al. 2011, LOSS II: "SNe Ic are the most abundant SNe Ibc, 54% of all").</summary>
        private const double FractionIcOfIbc = 0.54;

        private static int HubbleBin(double t)
        {
            if (double.IsNaN(t)) return 3;   // unknown type: Sb, the middle of the spirals
            if (t < -2.5) return 0;
            if (t < 0.5) return 1;
            if (t < 2.5) return 2;
            if (t < 3.5) return 3;
            if (t < 4.5) return 4;
            if (t < 6.5) return 5;
            if (t < 8.5) return 6;
            return 7;
        }

        /// <summary>B luminosity in units of 10^10 L_sun, or NaN without a distance modulus.</summary>
        public static double LuminosityL10(in Galaxy g)
        {
            if (double.IsNaN(g.DistanceModulusMag) || double.IsNaN(g.TotalBMag)) return double.NaN;
            double absoluteB = g.TotalBMag - g.DistanceModulusMag;
            if (absoluteB < BrightestPlausibleGalaxyAbsoluteB) return double.NaN;   // an AGN, not a galaxy
            return Math.Pow(10.0, -0.4 * (absoluteB - SolarAbsoluteBMag)) / 1e10;
        }

        /// <summary>
        /// Expected supernovae per CENTURY in this galaxy, by class: SNuB(L) * L10, with the
        /// rate-size relation applied. Zeros without a distance.
        /// </summary>
        public static void RatePerCentury(in Galaxy g, out double ia, out double ibc, out double ii)
        {
            ia = ibc = ii = 0.0;
            double l10 = LuminosityL10(in g);
            if (double.IsNaN(l10) || l10 <= 0.0) return;

            double[] row = SnuB[HubbleBin(g.MorphologicalType)];

            // The rate-size factor is evaluated on the CLAMPED ratio; the luminosity it multiplies
            // is not, so a galaxy twice as bright still hosts more supernovae. Only the empirical
            // correction stops being extrapolated past the range it was fitted over.
            double ratio = Math.Min(RateSizeSpan, Math.Max(1.0 / RateSizeSpan, l10 / FiducialL10));
            ia = row[0] * Math.Pow(ratio, RssIa) * l10;
            ibc = row[1] * Math.Pow(ratio, RssIbc) * l10;
            ii = row[2] * Math.Pow(ratio, RssII) * l10;
        }

        /// <summary>
        /// Every supernova whose explosion falls in the given block, deterministically. Positions
        /// are NOT resolved here (that needs the host's light map); ResolvePosition does it.
        /// </summary>
        public static List<SupernovaEvent> EventsInBlock(long saveSeed, in Galaxy g, long blockIndex)
        {
            var events = new List<SupernovaEvent>();
            RatePerCentury(in g, out double ia, out double ibc, out double ii);
            double perBlock = (ia + ibc + ii) * (BlockSeconds / (100.0 * 365.25 * 86400.0));
            if (!(perBlock > 0.0)) return events;

            var rng = new Pcg32(Mix(saveSeed, g.Name, blockIndex));
            int count = (int)NoiseSampler.Poisson(rng, perBlock);

            for (int i = 0; i < count; i++)
            {
                double when = (blockIndex + rng.NextDouble()) * BlockSeconds;
                double pick = rng.NextDouble() * (ia + ibc + ii);

                var e = new SupernovaEvent
                {
                    HostName = g.Name,
                    ExplosionUt = when,
                };

                if (pick < ia)
                {
                    e.Class = SupernovaClass.Ia;
                    e.PeakAbsoluteBMag = Draw(rng, PeakRow.Ia);
                }
                else if (pick < ia + ibc)
                {
                    e.Class = SupernovaClass.Ibc;
                    e.PeakAbsoluteBMag = Draw(rng, rng.NextDouble() < FractionIcOfIbc ? PeakRow.Ic : PeakRow.Ib);
                }
                else
                {
                    double sub = rng.NextDouble() * (FractionIIP + FractionIIL + FractionIIb + FractionIIn);
                    if (sub < FractionIIP)
                    {
                        e.Class = SupernovaClass.IIP;
                        e.PeakAbsoluteBMag = Draw(rng, PeakRow.IIP);
                    }
                    else if (sub < FractionIIP + FractionIIL)
                    {
                        e.Class = SupernovaClass.IIL;
                        e.PeakAbsoluteBMag = Draw(rng, PeakRow.IIL);
                    }
                    else if (sub < FractionIIP + FractionIIL + FractionIIb)
                    {
                        e.Class = SupernovaClass.Ibc;   // the IIb: stripped within days, Ibc spectrum
                        e.IsIIb = true;
                        e.PeakAbsoluteBMag = Draw(rng, PeakRow.IIb);
                    }
                    else
                    {
                        e.Class = SupernovaClass.IIn;
                        e.PeakAbsoluteBMag = Draw(rng, PeakRow.IIn);
                    }
                }

                events.Add(e);
            }
            return events;
        }

        /// <summary>
        /// Every supernova still inside its template span at the given UT, over this galaxy.
        /// Checks the previous block too, so an event exploding just before a boundary is not
        /// lost while it still shines.
        /// </summary>
        public static List<SupernovaEvent> ActiveAt(long saveSeed, in Galaxy g, double ut,
                                                    double longestTemplateDays)
        {
            var active = new List<SupernovaEvent>();
            long block = (long)Math.Floor(ut / BlockSeconds);
            for (long b = block - 1; b <= block; b++)
            {
                if (b < 0) continue;
                foreach (SupernovaEvent e in EventsInBlock(saveSeed, in g, b))
                {
                    double phase = e.PhaseDaysAt(ut);
                    if (phase >= 0.0 && phase <= longestTemplateDays) active.Add(e);
                }
            }
            return active;
        }

        /// <summary>
        /// Puts the event on the sky, sampling the host's own light.
        ///
        /// Core-collapse supernovae come from massive stars and trace the star-forming light (the
        /// bluest measured plane: Anderson et al. 2012, MNRAS 424, 1372 measure exactly this
        /// association); Ia trace the older population, taken as the reddest plane. Without a map
        /// the Sersic profile is sampled instead, which is the same light model the frame draws.
        /// Deterministic: the sampler runs on its own stream seeded by the event key.
        /// </summary>
        public static SupernovaEvent ResolvePosition(SupernovaEvent e, in Galaxy g, GalaxyImage map)
        {
            var rng = new Pcg32(Mix(0x534e504f53L, e.Key, 0));
            bool youngPopulation = e.Class != SupernovaClass.Ia;

            double offsetEastArcsec, offsetNorthArcsec;
            if (map != null && map.Size >= 8)
            {
                SampleMap(rng, map, youngPopulation, out double u, out double v);
                map.MapPixelToRaDec(u, v, out double ra, out double dec);
                e.RaDeg = ra;
                e.DecDeg = dec;
                return e;
            }

            // Sersic sampling: radius from the enclosed-fraction inverse, angle uniform, squashed
            // by the axis ratio and rotated to the position angle (east of north).
            double n = g.SersicIndex > 0.0 ? g.SersicIndex : 2.0;
            double reArcsec = SersicProfile.EffectiveRadiusFromIsophote(
                g.TotalBMag, g.SemiMajorArcsec, 25.0, n);
            if (double.IsNaN(reArcsec) || reArcsec <= 0.0) reArcsec = g.SemiMajorArcsec * 0.5;

            double radius = SersicProfile.RadiusForEnclosedFraction(rng.NextDouble(), n) * reArcsec;
            double theta = rng.NextDouble() * 2.0 * Math.PI;
            double major = radius * Math.Cos(theta);
            double minor = radius * Math.Sin(theta) * Math.Max(0.05, g.AxisRatio);

            double paRad = g.PositionAngleDeg * Math.PI / 180.0;
            offsetNorthArcsec = major * Math.Cos(paRad) - minor * Math.Sin(paRad);
            offsetEastArcsec = major * Math.Sin(paRad) + minor * Math.Cos(paRad);

            e.DecDeg = g.DecDeg + offsetNorthArcsec / 3600.0;
            double cosDec = Math.Cos(e.DecDeg * Math.PI / 180.0);
            e.RaDeg = g.RaDeg + (Math.Abs(cosDec) > 1e-6 ? offsetEastArcsec / 3600.0 / cosDec : 0.0);
            return e;
        }

        /// <summary>Rejection sampling over a light plane. The plane sums to one and its peak bounds the density, so acceptance is exact.</summary>
        private static void SampleMap(Pcg32 rng, GalaxyImage map, bool youngPopulation,
                                      out double u, out double v)
        {
            GalaxyImageBand[] bands = map.Bands;
            GalaxyImageBand band = bands[0];
            for (int i = 1; i < bands.Length; i++)
            {
                bool bluer = bands[i].WavelengthNm < band.WavelengthNm;
                if (youngPopulation == bluer) band = bands[i];
            }

            float peak = 0f;
            float[] p = band.Values;
            for (int i = 0; i < p.Length; i++) if (p[i] > peak) peak = p[i];
            if (peak <= 0f) { u = map.Size / 2.0; v = map.Size / 2.0; return; }

            for (int attempt = 0; attempt < 20000; attempt++)
            {
                double x = rng.NextDouble() * (map.Size - 1);
                double y = rng.NextDouble() * (map.Size - 1);
                int i = (int)y * map.Size + (int)x;
                if (rng.NextDouble() * peak <= p[i]) { u = x; v = y; return; }
            }
            u = map.Size / 2.0;
            v = map.Size / 2.0;
        }

        private static double Draw(Pcg32 rng, PeakRow row)
        {
            return PeakMeanMag[(int)row] + NoiseSampler.Gaussian(rng, PeakSigmaMag[(int)row]);
        }

        /// <summary>FNV-1a over the parts, so the stream depends on the save, the host and the block and nothing else.</summary>
        private static ulong Mix(long seed, string name, long block)
        {
            ulong h = 14695981039346656037UL;
            void Byte(byte b) { h ^= b; h *= 1099511628211UL; }
            for (int i = 0; i < 8; i++) Byte((byte)(seed >> (8 * i)));
            foreach (char c in name) { Byte((byte)c); Byte((byte)(c >> 8)); }
            for (int i = 0; i < 8; i++) Byte((byte)(block >> (8 * i)));
            return h;
        }
    }
}
