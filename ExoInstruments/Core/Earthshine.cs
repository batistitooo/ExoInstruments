using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Sunlight scattered off the host planet into the telescope's baffle: the background term
    /// that has no ground-based counterpart at all, and the one that decides how close to the
    /// bright limb an orbiting telescope can usefully point.
    ///
    /// WHY IT IS NOT SIMPLY "DON'T POINT AT THE PLANET". The planet is not in the field of view
    /// at any of the angles that matter here; the target is tens of degrees clear of the limb
    /// and would be photographed against black sky. What arrives is light that entered the tube
    /// off-axis and scattered off the baffles, and it does not fall off gently. Measured on
    /// STIS, the background is flat down to about 25 degrees from the sunlit limb and then
    /// climbs EXPONENTIALLY, reaching forty times the dark-sky level by 14 degrees. That cliff,
    /// not geometric occultation, is what sets the usable observing window on most orbits, and
    /// it is why every space observatory publishes a bright-limb avoidance angle.
    ///
    /// TWO SOURCES, EACH USED FOR WHAT IT MEASURED.
    ///
    ///   * The SHAPE is Shaw, Reinhart &amp; Wilson (1998), STScI Instrument Science Report STIS
    ///     98-21, "Scattered Light from the Earth Limb Measured with the STIS CCD". Their fit,
    ///     quoted verbatim, is C_BG = 3.4564 x 10^(-0.06564 alpha) electrons/s/pixel with alpha
    ///     the bright Earth limb angle in degrees, valid below the roughly 25 degree knee; above
    ///     it they measure the background "fairly constant, at ~0.075 electrons/s/pixel", against
    ///     a dark-limb level "nearly constant ... with a value near 0.033 electrons/s/pixel"
    ///     which they attribute to zodiacal light.
    ///
    ///   * The ABSOLUTE LEVEL and the wavelength dependence are the WFC3 Instrument Handbook
    ///     (Cycle 24) Table 9.3, whose earth-shine column is tabulated below unchanged. That
    ///     column is quoted for a specific geometry, stated in Section 9.7.1: "the limb angle is
    ///     approximately 24 degrees when the HST is aligned toward its orbit pole (i.e., the
    ///     centre of the CVZ). The earth-shine contribution shown in Figure 9.1 and Table 9.3
    ///     corresponds to this position." So the table pins the curve at 24 degrees and SRW98
    ///     supplies its slope, and neither source is asked for something it did not measure.
    ///
    /// THE CONSISTENCY CHECK BETWEEN THEM IS RUN, NOT ASSUMED. Converting SRW98's own count rate
    /// at 24 degrees through their stated PHOTFLAM and plate scale gives a surface flux within
    /// about 25 per cent of the handbook's table at V, on two different instruments a decade
    /// apart; tools/spacecraft-tests asserts it. Their exponential also meets their own quoted
    /// plateau at the knee to within 5 per cent, which is the internal check on the fit.
    ///
    /// WHAT IS KNOWN TO BE UNDERSTATED. ACS ISR 2003-05 ("ACS Background Light vs. Bright Earth
    /// Limb Angle") measured the same effect on ACS and found it rises FASTER than SRW98's STIS
    /// fit below about 16 degrees, by roughly a factor of three at the extreme. This model uses
    /// the STIS slope, so inside 16 degrees it is optimistic by up to that factor. That region
    /// lies inside every published bright-limb avoidance angle in this mod's roster, so no
    /// instrument here can legally observe in it; the error is real, bounded, signed, and
    /// unreachable in normal operation.
    ///
    /// Pure C# with no Unity dependency, like the rest of Core.
    /// </summary>
    public static class Earthshine
    {
        /// <summary>
        /// Limb angle the tabulated spectrum below is quoted at, degrees (WFC3 IHB Sect. 9.7.1:
        /// the pointing toward the orbit pole, at the centre of the continuous viewing zone).
        /// </summary>
        public const double ReferenceLimbAngleDeg = 24.0;

        /// <summary>
        /// SRW98's exponential coefficient: the background scales as 10^(-0.06564 alpha) below
        /// the knee. Carried as their fitted number, not rounded.
        /// </summary>
        public const double LimbAngleDecadesPerDegree = 0.06564;

        /// <summary>
        /// Angle above which SRW98 measure the bright-limb background to stop falling, degrees.
        ///
        /// They flag this themselves as unexpected: "The constancy of the background at large
        /// limb angles on the bright side of the Earth is somewhat puzzling: we would have
        /// expected the background to continue to decline with increasing angle from the bright
        /// limb, and approach that observed in Earth shadow as the limb angle approached the
        /// LOW-SKY limit of 40 degrees." The plateau is what was measured, so the plateau is what
        /// is modelled; the alternative is to substitute an expectation for a measurement.
        /// </summary>
        public const double PlateauLimbAngleDeg = 25.0;

        /// <summary>
        /// WFC3 IHB Table 9.3 wavelength grid, Angstrom. Note the step changes from 500 to 1000
        /// Angstrom at 6000; the grid is used as published rather than resampled.
        /// </summary>
        private static readonly double[] WavelengthAngstrom =
        {
            2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 7000,
            8000, 9000, 10000, 11000, 12000, 13000, 14000, 15000, 16000, 17000,
        };

        /// <summary>
        /// WFC3 IHB Table 9.3, earth-shine column, erg cm^-2 s^-1 Angstrom^-1 arcsec^-2, at the
        /// 24 degree reference limb angle. The steep fall below 3000 Angstrom is real and is the
        /// reason the near-UV is the one band where pointing near the bright limb costs little.
        /// </summary>
        private static readonly double[] EarthshineFluxDensity =
        {
            7.69e-22, 1.53e-21, 1.43e-19, 8.33e-19, 1.66e-18, 2.59e-18, 2.63e-18, 2.55e-18,
            2.42e-18, 1.95e-18, 1.56e-18, 1.23e-18, 9.97e-19, 8.02e-19, 6.65e-19, 5.58e-19,
            4.70e-19, 3.97e-19, 3.35e-19, 2.79e-19,
        };

        /// <summary>
        /// WFC3 IHB Table 9.3, zodiacal column, same units and grid: the handbook's "high sky"
        /// zodiacal case.
        ///
        /// Not used by the sky model, which takes its zodiacal light from ZodiacalLight's
        /// angle-resolved table instead. It is carried because it is the only published
        /// zodiacal SPECTRUM in this reference set, and the harness uses it to check that
        /// treating the zodiacal light as solar-coloured, which is what the rest of the pipeline
        /// assumes for every scattered-sunlight term, actually reproduces its measured shape.
        /// </summary>
        public static SpectralCurve HighZodiacalSpectrum() => new SpectralCurve(
            ToNanometres(WavelengthAngstrom),
            new[]
            {
                7.94e-20, 3.83e-19, 1.63e-18, 2.72e-18, 3.12e-18, 4.97e-18, 5.07e-18, 5.17e-18,
                5.14e-18, 4.48e-18, 3.82e-18, 3.18e-18, 2.70e-18, 2.26e-18, 1.94e-18, 1.68e-18,
                1.46e-18, 1.26e-18, 1.09e-18, 9.27e-19,
            });

        /// <summary>The tabulated earth-shine spectrum at the reference limb angle, as a curve in nm.</summary>
        public static SpectralCurve ReferenceSpectrum() =>
            new SpectralCurve(ToNanometres(WavelengthAngstrom), (double[])EarthshineFluxDensity.Clone());

        /// <summary>
        /// How much brighter or fainter the earth-shine is at limb angle
        /// <paramref name="limbAngleDeg"/> than at the 24 degree reference, as a flux ratio.
        ///
        /// Zero for a dark limb: SRW98 measure the dark-limb background as flat and equal to the
        /// Earth-shadow level, which they attribute to zodiacal light, so there is no scattered
        /// planet light left in it to model. Zero also once the target is behind the disk, where
        /// there is no observation to contaminate.
        /// </summary>
        public static double LimbAngleFactor(double limbAngleDeg, bool limbIsSunlit)
        {
            if (!limbIsSunlit) return 0.0;
            if (limbAngleDeg <= 0.0) return 0.0;

            double alpha = Math.Min(limbAngleDeg, PlateauLimbAngleDeg);
            return Math.Pow(10.0, -LimbAngleDecadesPerDegree * (alpha - ReferenceLimbAngleDeg));
        }

        /// <summary>
        /// V surface brightness of the earth-shine at a given limb angle, magnitudes per square
        /// arcsecond, on the same convention the rest of this mod's sky terms use: the source's
        /// photon flux through the Bessell V band against a flat
        /// PhotonFluxModel.ZeroMagPhotonFluxPerAngstrom reference across the same band (see
        /// Airglow.VBandMagPerArcsec2, which this deliberately mirrors so the two sky terms are
        /// summable).
        ///
        /// +Infinity when there is no earth-shine, which AddMagnitude treats as contributing
        /// nothing.
        /// </summary>
        public static double VMagPerArcsec2(double limbAngleDeg, bool limbIsSunlit, double hostScaling)
        {
            double factor = LimbAngleFactor(limbAngleDeg, limbIsSunlit) * Math.Max(0.0, hostScaling);
            if (!(factor > 0.0)) return double.PositiveInfinity;

            double photons = 0.0, bandWidthAngstrom = 0.0;
            // Integrate on a 1 nm grid across the V band, the same step Airglow uses, sampling
            // the tabulated spectrum by interpolation. hc/lambda converts the tabulated energy
            // flux density to a photon flux density at each wavelength.
            for (double nm = 470.0; nm <= 700.0; nm += 1.0)
            {
                double v = Airglow.JohnsonVTransmission(nm);
                if (!(v > 0.0)) continue;
                double fluxDensity = SampleFluxDensity(nm * 10.0);      // per Angstrom
                double photonEnergyErg = PlanckConstantErgSeconds * SpeedOfLightCmPerSecond / (nm * 1e-7);
                photons += fluxDensity / photonEnergyErg * v * 10.0;    // 1 nm = 10 Angstrom
                bandWidthAngstrom += v * 10.0;
            }

            double reference = PhotonFluxModel.ZeroMagPhotonFluxPerAngstrom * bandWidthAngstrom;
            if (!(photons > 0.0) || !(reference > 0.0)) return double.PositiveInfinity;
            return -2.5 * Math.Log10(photons * factor / reference);
        }

        /// <summary>
        /// How this host body's scattered light compares with Earth's, as a flux ratio, so the
        /// measured Earth numbers can be carried to whatever body the telescope is actually
        /// orbiting. Exactly 1 when the host is Earth at 1 AU, which is the case under Real
        /// Solar System.
        ///
        /// Two factors, both geometry and both signed the obvious way:
        ///
        ///   * How brightly the limb shines: proportional to the body's albedo and to the solar
        ///     irradiance it receives, i.e. albedo / distance_au^2, against Earth's own.
        ///   * How much of it there is to scatter: proportional to the solid angle the body
        ///     subtends, against the solid angle Earth subtends from HST's own 500 km orbit,
        ///     which is the geometry SRW98's curve was measured in.
        ///
        /// THE SECOND FACTOR IS A FIRST-ORDER CORRECTION AND IS LABELLED AS ONE. What actually
        /// sets the scattered signal is the integral of the limb's surface brightness against the
        /// baffle's own off-axis rejection function, and no rejection function is published for
        /// any instrument here. Scaling by solid angle is exact only in the limit where the body
        /// is small compared with the angular scale on which the rejection varies. It has the
        /// right sign and the right order everywhere, and it matters least where it is least
        /// trustworthy: a telescope far enough from its host for the approximation to be poor is
        /// a telescope whose earth-shine is negligible against the zodiacal light anyway.
        /// </summary>
        public static double HostBodyScaling(double bodyAlbedo, double bodyRadiusMeters,
                                             double observerDistanceFromCentreMeters,
                                             double bodyDistanceToSunMeters)
        {
            if (!(bodyAlbedo > 0.0) || !(bodyRadiusMeters > 0.0)
                || !(observerDistanceFromCentreMeters > bodyRadiusMeters)
                || !(bodyDistanceToSunMeters > 0.0)) return 0.0;

            double distanceAu = bodyDistanceToSunMeters / PhotonFluxModel.AuMeters;
            double brightness = (bodyAlbedo / EarthGeometricAlbedo) / (distanceAu * distanceAu);

            // Solid angle of a sphere of angular radius rho is 2 pi (1 - cos rho); the ratio of
            // two such is taken rather than the small-angle square, because from 500 km the Earth
            // is nowhere near a small angle (its angular radius is about 68 degrees).
            double rho = Math.Asin(Math.Min(1.0, bodyRadiusMeters / observerDistanceFromCentreMeters));
            double solidAngle = 1.0 - Math.Cos(rho);
            double rhoRef = Math.Asin(EarthRadiusMeters / (EarthRadiusMeters + HstOrbitAltitudeMeters));
            double solidAngleRef = 1.0 - Math.Cos(rhoRef);

            return brightness * (solidAngle / solidAngleRef);
        }

        /// <summary>
        /// Earth's geometric albedo, 0.434, and its volumetric mean radius, 6371.0 km: NASA
        /// Goddard's Earth Fact Sheet. The geometric albedo rather than the Bond albedo of 0.306,
        /// because the quantity that matters here is how bright the disk looks from nearly the
        /// same direction as the Sun, which is what the geometric albedo is defined as, and
        /// because it is the same albedo convention PhotonFluxModel.ApparentMagnitude already
        /// uses for a body's own brightness.
        /// </summary>
        public const double EarthGeometricAlbedo = 0.434;
        public const double EarthRadiusMeters = 6371000.0;

        /// <summary>HST's orbit altitude, 500 km (HST Primer: Orbital Constraints). The geometry SRW98's curve was measured in.</summary>
        public const double HstOrbitAltitudeMeters = 500000.0;

        private const double PlanckConstantErgSeconds = 6.62607015e-27;
        private const double SpeedOfLightCmPerSecond = 2.99792458e10;

        /// <summary>Log-linear interpolation of the tabulated spectrum; the values span four decades, so interpolating them linearly would be wrong by a lot between the widely spaced UV points.</summary>
        private static double SampleFluxDensity(double angstrom)
        {
            var w = WavelengthAngstrom;
            var f = EarthshineFluxDensity;
            if (angstrom <= w[0]) return f[0];
            if (angstrom >= w[w.Length - 1]) return f[f.Length - 1];
            for (int i = 0; i < w.Length - 1; i++)
            {
                if (angstrom > w[i + 1]) continue;
                double t = (angstrom - w[i]) / (w[i + 1] - w[i]);
                return Math.Exp(Math.Log(f[i]) * (1.0 - t) + Math.Log(f[i + 1]) * t);
            }
            return f[f.Length - 1];
        }

        private static double[] ToNanometres(double[] angstrom)
        {
            var nm = new double[angstrom.Length];
            for (int i = 0; i < angstrom.Length; i++) nm[i] = angstrom[i] * 0.1;
            return nm;
        }
    }
}
