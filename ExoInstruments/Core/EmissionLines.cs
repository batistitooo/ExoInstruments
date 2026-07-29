using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The optical emission lines a nebula is photographed in, and what a given line surface
    /// brightness puts on a detector.
    ///
    /// WHY THIS IS NOT THE SAME PATH AS A STAR. Everything else in this pipeline integrates a
    /// CONTINUUM: a Planck curve, a solar spectrum, a flat one. A nebula in [S II] is not a
    /// continuum. Its flux arrives in lines a fraction of an Angstrom wide, so the quantity that
    /// matters is the system's throughput AT one wavelength, not an effective width across a band,
    /// and a 3 nm filter collects the same number of photons as a 30 nm one while admitting a
    /// tenth of the sky behind it. That asymmetry is the whole reason narrowband imaging works, and
    /// an effective-width model cannot express it.
    ///
    /// WAVELENGTHS are the standard AIR values at which these lines are catalogued and at which
    /// filters are specified, from NIST ASD and reproduced throughout Osterbrock &amp; Ferland (2006,
    /// "Astrophysics of Gaseous Nebulae and Active Galactic Nuclei", 2nd ed.). Air rather than
    /// vacuum because that is the convention every filter manufacturer and every narrowband
    /// observer works in, and mixing the two is a 1.7 Angstrom error -- half a nanometre-class
    /// filter's own tolerance.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class EmissionLines
    {
        /// <summary>
        /// One catalogued line: its air wavelength, its name, and whether it is a recombination or
        /// a collisionally excited (forbidden) transition. The distinction is not decoration -- the
        /// forbidden lines are the density and temperature diagnostics, and their ratios to the
        /// recombination lines are what a nebular analysis is made of.
        /// </summary>
        public struct Line
        {
            public string Name;
            public double WavelengthMeters;
            public bool Forbidden;

            public double WavelengthAngstrom => WavelengthMeters * 1e10;
            public double WavelengthNm => WavelengthMeters * 1e9;
        }

        private static Line L(string name, double angstrom, bool forbidden)
            => new Line { Name = name, WavelengthMeters = angstrom * 1e-10, Forbidden = forbidden };

        // --- Hydrogen recombination -----------------------------------------------------
        public static readonly Line HAlpha = L("H-alpha", 6562.80, false);
        public static readonly Line HBeta = L("H-beta", 4861.32, false);

        // --- Collisionally excited, the diagnostics -------------------------------------
        /// <summary>[O II] 3726/3729, the density-sensitive doublet. Blended by any filter, so it is carried as its own pair.</summary>
        public static readonly Line OII3726 = L("[O II] 3726", 3726.03, true);
        public static readonly Line OII3729 = L("[O II] 3729", 3728.82, true);

        /// <summary>[O III] 4959/5007, the pair whose ratio is fixed at 1:2.98 by atomic physics rather than by conditions.</summary>
        public static readonly Line OIII4959 = L("[O III] 4959", 4958.91, true);
        public static readonly Line OIII5007 = L("[O III] 5007", 5006.84, true);

        /// <summary>[O I] 6300/6364. Also the brightest terrestrial airglow line, which is why it is the hardest of these to image from the ground.</summary>
        public static readonly Line OI6300 = L("[O I] 6300", 6300.30, true);
        public static readonly Line OI6364 = L("[O I] 6364", 6363.78, true);

        /// <summary>[N II] 6548/6584. 6584 sits 21 Angstrom from H-alpha, so only a filter narrower than about 1 nm separates them.</summary>
        public static readonly Line NII6548 = L("[N II] 6548", 6548.05, true);
        public static readonly Line NII6584 = L("[N II] 6584", 6583.45, true);

        /// <summary>[S II] 6716/6731, the other density-sensitive doublet and the one most often imaged.</summary>
        public static readonly Line SII6716 = L("[S II] 6716", 6716.44, true);
        public static readonly Line SII6731 = L("[S II] 6731", 6730.82, true);

        public static readonly Line[] All =
        {
            OII3726, OII3729, HBeta, OIII4959, OIII5007,
            OI6300, OI6364, NII6548, HAlpha, NII6584, SII6716, SII6731,
        };

        /// <summary>
        /// Fixed ratio of the [O III] doublet, 5007/4959 = 2.98, set by the transition
        /// probabilities of a single upper level and therefore independent of density and
        /// temperature (Storey &amp; Zeippen 2000, MNRAS 312, 813). The same relation holds for
        /// [N II] 6584/6548 at 2.94.
        /// </summary>
        public const double OiiiDoubletRatio = 2.98;
        public const double NiiDoubletRatio = 2.94;

        // ------------------------------------------------------------------ the Rayleigh

        /// <summary>
        /// Photons per square centimetre per second per steradian in one rayleigh.
        ///
        /// The rayleigh is defined as a column emission rate of 10^6 photons cm^-2 s^-1 into 4 pi
        /// steradians (Hunten, Roach &amp; Chamberlain 1956, JATP 8, 345; Baker &amp; Romick 1976,
        /// Appl. Opt. 15, 1966), so as a surface brightness it is 10^6/(4 pi) per steradian. It is
        /// the unit every wide-field H-alpha survey publishes in -- WHAM, VTSS, SHASSA and the
        /// Finkbeiner (2003) composite -- which is why the conversion lives here rather than being
        /// done at each call site.
        /// </summary>
        public const double PhotonsPerCm2PerSecondPerSteradianPerRayleigh = 1.0e6 / (4.0 * Math.PI);

        /// <summary>Steradians in one square arcsecond.</summary>
        public const double SteradiansPerSquareArcsec =
            (Math.PI / (180.0 * 3600.0)) * (Math.PI / (180.0 * 3600.0));

        /// <summary>
        /// Electrons per pixel per second from a line of the given surface brightness.
        ///
        ///     N_e = I [R] * 10^6/(4 pi) * Omega_pixel [sr] * A [cm^2] * T_system(lambda)
        ///
        /// with T_system the dimensionless throughput at the LINE's wavelength -- filter, optics,
        /// detector and atmosphere, all sampled at one wavelength rather than integrated, because
        /// the line has no width worth integrating over. Multiply by exposure for electrons.
        ///
        /// Nothing here knows what the filter is. The caller passes the throughput, which is what
        /// makes this the same arithmetic whether the line lands in a 1 nm narrowband or in the
        /// wing of a broadband filter.
        /// </summary>
        public static double ElectronsPerPixelPerSecond(
            double surfaceBrightnessRayleighs,
            double plateScaleArcsecPerPixel,
            double apertureAreaCm2,
            double systemThroughput)
        {
            if (!(surfaceBrightnessRayleighs > 0.0)) return 0.0;
            if (!(plateScaleArcsecPerPixel > 0.0) || !(apertureAreaCm2 > 0.0)) return 0.0;
            if (!(systemThroughput > 0.0)) return 0.0;

            double pixelSolidAngleSr = plateScaleArcsecPerPixel * plateScaleArcsecPerPixel
                                     * SteradiansPerSquareArcsec;

            return surfaceBrightnessRayleighs * PhotonsPerCm2PerSecondPerSteradianPerRayleigh
                 * pixelSolidAngleSr * apertureAreaCm2 * systemThroughput;
        }

        /// <summary>
        /// The same for a source whose line flux is quoted as an integrated energy flux, which is
        /// how a compact object's lines are catalogued: erg per square centimetre per second.
        ///
        /// Photons rather than energy is what a detector counts, so the flux is divided by the
        /// photon energy hc/lambda first. Returns electrons per second over the whole source.
        /// </summary>
        public static double ElectronsPerSecondFromLineFlux(
            double lineFluxErgPerCm2PerSecond,
            double wavelengthMeters,
            double apertureAreaCm2,
            double systemThroughput)
        {
            if (!(lineFluxErgPerCm2PerSecond > 0.0) || !(wavelengthMeters > 0.0)) return 0.0;
            if (!(apertureAreaCm2 > 0.0) || !(systemThroughput > 0.0)) return 0.0;

            const double PlanckH = 6.62607015e-34;    // J s, SI defining constant
            const double SpeedOfLight = 2.99792458e8; // m/s, SI defining constant
            const double ErgPerJoule = 1.0e7;

            double photonEnergyErg = PlanckH * SpeedOfLight / wavelengthMeters * ErgPerJoule;
            double photonsPerCm2PerSecond = lineFluxErgPerCm2PerSecond / photonEnergyErg;

            return photonsPerCm2PerSecond * apertureAreaCm2 * systemThroughput;
        }
    }
}
