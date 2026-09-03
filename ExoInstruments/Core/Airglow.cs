using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The night sky's own emission, which is what a narrowband filter is really fighting.
    ///
    /// WHY A CONTINUUM IS NOT ENOUGH. The sky is not smooth. Above about 85 km the atmosphere glows:
    /// the [O I] green line at 557.7 nm, the [O I] red doublet at 630.0 and 636.4, the sodium D pair,
    /// and from 650 nm upward a dense forest of OH Meinel bands that dominates the whole red end.
    /// ESO's model puts 11148 rayleighs into those lines between 350 and 1000 nm against 5290 in the
    /// residual continuum; so most of the dark sky is LINES, and a narrowband filter either sits on
    /// one of them or it does not.
    ///
    /// That distinction decides what can be observed. An [O I] 6300 filter looks straight at 151
    /// rayleighs of airglow in the same line it is trying to measure, which is why nobody images
    /// [O I] from the ground; an H-alpha filter at 656.3 sits in a relatively clear window between OH
    /// bands, which is why everybody images H-alpha. A sky modelled as a continuum makes those two
    /// look equally easy, and they are not.
    ///
    /// AIRMASS, and why it is not sec z. The emitting gas is a LAYER at a finite height, not a slab
    /// extending to infinity, so its column through a slanted line of sight grows more slowly than
    /// the air's. The van Rhijn (1921) function is the geometry of a thin shell:
    ///
    ///     I(z) / I(0) = [ 1 - (R / (R + h))^2 sin^2 z ]^(-1/2)
    ///
    /// At 60 degrees from the zenith that gives 1.92 against sec z = 2.00, and by 80 degrees 4.19
    /// against 5.76, a 27% difference, in the direction that makes low-altitude observing less bad
    /// than a plain airmass scaling suggests.
    ///
    /// The table is generated from ESO's SkyCalc rather than typed; see tools/generate_airglow_table.py
    /// and tools/airglow-tests.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class Airglow
    {
        // Mean radius of the Earth, kilometres. IUGG mean radius; the van Rhijn function needs only the ratio
        // to the layer height.
        private const double EarthRadiusKm = 6371.0;

        /// <summary>
        /// Height of the main airglow layer, kilometres. The OH Meinel bands, the [O I] green line
        /// and the sodium layer all sit within a few kilometres of 90: OH peaks near 87 km, O I
        /// 557.7 near 96, Na near 92 (Roach &amp; Gordon 1973, "The Light of the Night Sky"). One
        /// height for all three is worth a percent in the van Rhijn factor and saves carrying a
        /// height per wavelength for a distinction nothing in this pipeline could measure.
        /// </summary>
        public const double MainLayerHeightKm = 90.0;

        /// <summary>
        /// Height of the [O I] red doublet's layer, kilometres. The red lines come from dissociative
        /// recombination of O2+ in the F region, far above the rest of the airglow, so their van
        /// Rhijn factor is materially different, 1.73 against 1.92 at 60 degrees from the zenith.
        /// </summary>
        public const double RedLineLayerHeightKm = 250.0;

        /// <summary>The [O I] red doublet, air wavelengths in nanometres, which get the higher layer.</summary>
        public const double RedLine1Nm = 630.030;
        public const double RedLine2Nm = 636.378;
        // Half-width in nanometres within which a wavelength counts as part of the red doublet.
        private const double RedLineWindowNm = 0.6;

        /// <summary>
        /// The van Rhijn (1921) factor: how much brighter a thin emitting shell at height h looks at
        /// zenith distance z than at the zenith.
        /// </summary>
        public static double VanRhijnFactor(double zenithDistanceDeg, double layerHeightKm)
        {
            double z = Math.Max(0.0, Math.Min(89.9, zenithDistanceDeg)) * Math.PI / 180.0;
            double ratio = EarthRadiusKm / (EarthRadiusKm + Math.Max(1.0, layerHeightKm));
            double s = Math.Sin(z);
            double inside = 1.0 - ratio * ratio * s * s;
            return inside > 1e-9 ? 1.0 / Math.Sqrt(inside) : 1.0 / Math.Sqrt(1e-9);
        }

        /// <summary>Spectral density of the airglow lines at the zenith, rayleighs per nanometre.</summary>
        public static double LineDensityAtZenith(double wavelengthNm) => Sample(AirglowTable.LineDensity, wavelengthNm);

        /// <summary>Spectral density of the airglow continuum at the zenith, rayleighs per nanometre.</summary>
        public static double ContinuumDensityAtZenith(double wavelengthNm) => Sample(AirglowTable.ContinuumDensity, wavelengthNm);

        private static double Sample(float[] table, double wavelengthNm)
        {
            if (wavelengthNm < AirglowTable.MinWavelengthNm || wavelengthNm >= AirglowTable.MaxWavelengthNm)
                return 0.0;
            int i = (int)((wavelengthNm - AirglowTable.MinWavelengthNm) / AirglowTable.StepNm);
            if (i < 0 || i >= table.Length) return 0.0;
            // No interpolation: the entries are bin AVERAGES, so the value that belongs to a
            // wavelength is its own bin's, and interpolating between bin averages would smear the
            // narrow lines the binning exists to preserve.
            return table[i];
        }

        /// <summary>
        /// Electrons per pixel per second the airglow puts on the detector through a given response.
        ///
        /// Integrated over the table's own bins, each scaled by its own van Rhijn factor, which is
        /// where the red doublet's different layer height enters, and by the system's throughput at
        /// that wavelength. Returns the same units as EmissionLines.ElectronsPerPixelPerSecond, and
        /// uses it, so the sky and a nebula arrive on one flux scale.
        /// </summary>
        public static double ElectronsPerPixelPerSecond(
            SystemResponse response, double plateScaleArcsecPerPixel, double apertureAreaCm2,
            double zenithDistanceDeg)
        {
            if (response == null || !(plateScaleArcsecPerPixel > 0.0) || !(apertureAreaCm2 > 0.0)) return 0.0;

            double mainFactor = VanRhijnFactor(zenithDistanceDeg, MainLayerHeightKm);
            double redFactor = VanRhijnFactor(zenithDistanceDeg, RedLineLayerHeightKm);

            double total = 0.0;
            int n = AirglowTable.LineDensity.Length;
            for (int i = 0; i < n; i++)
            {
                double lambdaNm = AirglowTable.MinWavelengthNm + (i + 0.5) * AirglowTable.StepNm;
                double lineDensity = AirglowTable.LineDensity[i];
                double continuumDensity = i < AirglowTable.ContinuumDensity.Length
                    ? AirglowTable.ContinuumDensity[i] : 0.0;
                if (lineDensity <= 0.0 && continuumDensity <= 0.0) continue;

                double throughput = response.ThroughputAt(lambdaNm * 1e-9);
                if (!(throughput > 0.0)) continue;

                double factor = IsRedDoublet(lambdaNm) ? redFactor : mainFactor;
                double rayleighs = (lineDensity * factor + continuumDensity * mainFactor)
                                 * AirglowTable.StepNm;
                total += EmissionLines.ElectronsPerPixelPerSecond(
                    rayleighs, plateScaleArcsecPerPixel, apertureAreaCm2, throughput);
            }
            return total;
        }

        /// <summary>
        /// Airglow surface brightness in the band, rayleighs, for the readout. The quantity to
        /// compare a nebula's own surface brightness against, which is the comparison that decides
        /// whether a narrowband frame is worth taking.
        /// </summary>
        public static double RayleighsInBand(SystemResponse response, double zenithDistanceDeg,
                                             out double lineShare)
        {
            lineShare = 0.0;
            if (response == null) return 0.0;

            double mainFactor = VanRhijnFactor(zenithDistanceDeg, MainLayerHeightKm);
            double redFactor = VanRhijnFactor(zenithDistanceDeg, RedLineLayerHeightKm);
            double lines = 0.0, continuum = 0.0, weight = 0.0;

            int n = AirglowTable.LineDensity.Length;
            for (int i = 0; i < n; i++)
            {
                double lambdaNm = AirglowTable.MinWavelengthNm + (i + 0.5) * AirglowTable.StepNm;
                double throughput = response.ThroughputAt(lambdaNm * 1e-9);
                if (!(throughput > 0.0)) continue;
                double factor = IsRedDoublet(lambdaNm) ? redFactor : mainFactor;
                lines += AirglowTable.LineDensity[i] * factor * throughput * AirglowTable.StepNm;
                if (i < AirglowTable.ContinuumDensity.Length)
                    continuum += AirglowTable.ContinuumDensity[i] * mainFactor * throughput * AirglowTable.StepNm;
                weight += throughput * AirglowTable.StepNm;
            }
            double total = lines + continuum;
            lineShare = total > 0.0 ? lines / total : 0.0;

            // Divided by the throughput-weighted width, so the answer is the surface brightness the
            // band SEES rather than the product of brightness and bandwidth.
            return weight > 0.0 ? total / weight * BandNormalisation : 0.0;
        }

        // Multiplies the throughput-weighted mean density back into a brightness. One, because dividing the
        // integral by the integral of the throughput already leaves a density, and the caller wants the total
        // in the band: the factor is here so the intent is written down rather than implied by the absence of
        // one.
        private const double BandNormalisation = 1.0;

        // ------------------------------------------------------------------ V surface brightness

        // Johnson-Cousins V passband, Bessell (1990, PASP 102, 1181) Table 2, 470-700 nm at 10 nm, normalised
        // to unit peak. Tabulated here because the airglow's V surface brightness is a band integral and the
        // mod's monochromatic V zero point cannot take one alone. The transcription is not trusted:
        // tools/airglow-tests compares it point by point against the speclite package's own Bessell V curve,
        // and its effective wavelength and width against the published 551 and 88 nm.
        private static readonly double[] BessellV =
        {
            0.000, 0.030, 0.163, 0.458, 0.780, 0.967, 1.000, 0.973,
            0.898, 0.792, 0.684, 0.574, 0.461, 0.359, 0.270, 0.197,
            0.135, 0.081, 0.045, 0.025, 0.017, 0.013, 0.009, 0.000,
        };
        private const double BessellVMinNm = 470.0;
        private const double BessellVStepNm = 10.0;

        /// <summary>The V transmission at a wavelength, linearly interpolated. Zero outside the band.</summary>
        public static double JohnsonVTransmission(double wavelengthNm)
        {
            double pos = (wavelengthNm - BessellVMinNm) / BessellVStepNm;
            int i = (int)Math.Floor(pos);
            if (i < 0 || i >= BessellV.Length - 1) return 0.0;
            double f = pos - i;
            return BessellV[i] * (1.0 - f) + BessellV[i + 1] * f;
        }

        // Steradians in one square arcsecond.
        private const double SrPerArcsec2 = 2.35044305391e-11;

        /// <summary>
        /// The airglow's V surface brightness at a zenith distance, magnitudes per square arcsecond.
        ///
        /// Defined the way an ETC defines it: the airglow's photon flux through the Bessell V band,
        /// against a zero-magnitude reference of PhotonFluxModel.ZeroMagPhotonFluxPerAngstrom taken
        /// flat across the same band, the identical convention the rest of this mod's photometry
        /// anchors on, so the sky and the sources it competes with share one scale. The classical
        /// dark-sky figure this must reproduce is V = 21.7 (Patat 2008 measures 21.7 +/- 0.2 at
        /// Paranal), and tools/airglow-tests asserts it does.
        /// </summary>
        public static double VBandMagPerArcsec2(double zenithDistanceDeg)
        {
            double mainFactor = VanRhijnFactor(zenithDistanceDeg, MainLayerHeightKm);
            double redFactor = VanRhijnFactor(zenithDistanceDeg, RedLineLayerHeightKm);

            // Photons through the band per second per cm^2 per arcsec^2. The table is rayleighs per
            // nanometre; one rayleigh is 1e6/(4 pi) photons/s/cm^2/sr.
            const double rayleighToPhotons = 1.0e6 / (4.0 * Math.PI) * SrPerArcsec2;
            double photons = 0.0, bandWidthAngstrom = 0.0;
            int n = AirglowTable.LineDensity.Length;
            for (int i = 0; i < n; i++)
            {
                double lambdaNm = AirglowTable.MinWavelengthNm + (i + 0.5) * AirglowTable.StepNm;
                double v = JohnsonVTransmission(lambdaNm);
                if (!(v > 0.0)) continue;
                double factor = IsRedDoublet(lambdaNm) ? redFactor : mainFactor;
                double continuumDensity = i < AirglowTable.ContinuumDensity.Length
                    ? AirglowTable.ContinuumDensity[i] : 0.0;
                double rayleighsPerNm = AirglowTable.LineDensity[i] * factor
                                      + continuumDensity * mainFactor;
                photons += rayleighsPerNm * rayleighToPhotons * v * AirglowTable.StepNm;
            }
            for (double l = BessellVMinNm; l <= 700.0; l += 1.0)
                bandWidthAngstrom += JohnsonVTransmission(l) * 10.0;

            double reference = PhotonFluxModel.ZeroMagPhotonFluxPerAngstrom * bandWidthAngstrom;
            return photons > 0.0 && reference > 0.0
                ? -2.5 * Math.Log10(photons / reference)
                : double.PositiveInfinity;
        }

        private static bool IsRedDoublet(double wavelengthNm)
            => Math.Abs(wavelengthNm - RedLine1Nm) < RedLineWindowNm
            || Math.Abs(wavelengthNm - RedLine2Nm) < RedLineWindowNm;
    }
}
