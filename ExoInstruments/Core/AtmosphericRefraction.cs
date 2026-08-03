using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Atmospheric refraction and its wavelength dependence, which is what turns a star at low
    /// altitude into a tiny spectrum.
    ///
    /// WHY THIS MATTERS AND WHY IT IS NOT COSMETIC. Air has a refractive index a few parts in ten
    /// thousand above one, and that index is a function of wavelength, the same dispersion that
    /// makes a prism work. So the atmosphere lifts a star toward the zenith by an angle that depends
    /// on colour: blue light is bent more than red. Between 400 and 700 nm at 45 degrees from the
    /// zenith the difference is about an arcsecond, which at the RC20's 0.069 arcsec pixels is
    /// fifteen pixels of smear along the direction toward the zenith. Every professional survey
    /// pipeline models it (it is a first-order astrometric and photometric systematic, not a detail),
    /// and it is the reason instruments carry atmospheric dispersion correctors.
    ///
    /// THE REFRACTIVE INDEX is Filippenko (1982, PASP 94, 715) eq. 1-3, the standard reference for
    /// astronomical use, itself built on Edlen (1953) and Owens (1967):
    ///
    ///     (n-1) 10^6 = 64.328 + 29498.1/(146 - s^2) + 255.4/(41 - s^2)      dry air, 15 C, 760 mmHg
    ///
    /// with s = 1/lambda in inverse micrometres, then scaled to the site's own temperature and
    /// pressure and corrected for water vapour, which LOWERS the index (water is lighter than air).
    ///
    /// THE REFRACTION ANGLE is R = (n-1) tan z, the plane-parallel result. That form is exact in the
    /// limit of a thin atmosphere and drifts about a percent by 60 degrees from the zenith, growing
    /// beyond, but what this class exists to compute is the DIFFERENCE between two wavelengths, and
    /// the higher-order terms it omits scale every wavelength almost identically, so they cancel in
    /// the difference to far better than that. The absolute refraction is reported for completeness;
    /// the differential is the quantity used.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class AtmosphericRefraction
    {
        /// <summary>
        /// Refractivity, (n - 1), of standard air: dry, 15 degrees C, 760 mmHg, at a wavelength in
        /// micrometres. Filippenko (1982) eq. 1.
        /// </summary>
        public static double RefractivityStandard(double wavelengthMicrons)
        {
            if (!(wavelengthMicrons > 0.0)) return 0.0;
            double sigma = 1.0 / wavelengthMicrons;
            double s2 = sigma * sigma;

            // The two resonance denominators are what make air dispersive; they are poles of the
            // fit, and a wavelength short enough to approach one is outside its validity.
            double d1 = 146.0 - s2;
            double d2 = 41.0 - s2;
            if (!(d1 > 1.0) || !(d2 > 1.0)) return double.NaN;

            return (64.328 + 29498.1 / d1 + 255.4 / d2) * 1e-6;
        }

        /// <summary>
        /// Refractivity at a site's own temperature, pressure and humidity. Filippenko (1982)
        /// eq. 2 and 3.
        /// </summary>
        /// <param name="wavelengthMicrons">Wavelength in micrometres.</param>
        /// <param name="temperatureCelsius">Air temperature.</param>
        /// <param name="pressureMillibar">Atmospheric pressure at the site.</param>
        /// <param name="waterVapourPressureMillibar">Partial pressure of water vapour. Zero for dry air.</param>
        public static double Refractivity(double wavelengthMicrons, double temperatureCelsius,
                                          double pressureMillibar, double waterVapourPressureMillibar)
        {
            double standard = RefractivityStandard(wavelengthMicrons);
            if (double.IsNaN(standard)) return double.NaN;

            // Filippenko's expressions are in mmHg, so the pressures convert first.
            double p = pressureMillibar * MillibarToMmHg;
            double f = Math.Max(0.0, waterVapourPressureMillibar) * MillibarToMmHg;
            double t = temperatureCelsius;

            double scaled = standard * p * (1.0 + (1.049 - 0.0157 * t) * 1e-6 * p)
                          / (720.883 * (1.0 + 0.003661 * t));

            // Water vapour reduces the index: a water molecule is lighter than the average air
            // molecule it displaces, so humid air is optically thinner than dry air at the same
            // pressure. The correction is itself dispersive.
            double sigma = 1.0 / wavelengthMicrons;
            double vapour = (0.0624 - 0.000680 * sigma * sigma) / (1.0 + 0.003661 * t) * f * 1e-6;

            return scaled - vapour;
        }

        /// <summary>Millibars (hectopascals) to millimetres of mercury.</summary>
        public const double MillibarToMmHg = 760.0 / 1013.25;

        /// <summary>
        /// Refraction angle in arcseconds: how much higher than its true position a source appears.
        /// R = (n - 1) tan z, the plane-parallel result; see the class summary for its validity and
        /// for why the differential is far more accurate than the absolute.
        /// </summary>
        public static double RefractionArcsec(double refractivity, double zenithDistanceDeg)
        {
            if (double.IsNaN(refractivity)) return double.NaN;
            double z = Math.Max(0.0, Math.Min(89.9, zenithDistanceDeg)) * Math.PI / 180.0;
            return refractivity * Math.Tan(z) * 180.0 * 3600.0 / Math.PI;
        }

        /// <summary>
        /// Differential refraction between two wavelengths, arcseconds: positive when the first is
        /// lifted MORE, which for shorter wavelengths it is. This is the quantity that smears a
        /// star's image, and it points toward the zenith.
        /// </summary>
        public static double DifferentialRefractionArcsec(
            double wavelengthMicrons, double referenceWavelengthMicrons,
            double zenithDistanceDeg, double temperatureCelsius,
            double pressureMillibar, double waterVapourPressureMillibar)
        {
            double a = Refractivity(wavelengthMicrons, temperatureCelsius, pressureMillibar, waterVapourPressureMillibar);
            double b = Refractivity(referenceWavelengthMicrons, temperatureCelsius, pressureMillibar, waterVapourPressureMillibar);
            if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
            double z = Math.Max(0.0, Math.Min(89.9, zenithDistanceDeg)) * Math.PI / 180.0;
            return (a - b) * Math.Tan(z) * 180.0 * 3600.0 / Math.PI;
        }

        // ------------------------------------------------------------------ site conditions

        /// <summary>
        /// Pressure at an altitude, millibars, from the ICAO Standard Atmosphere's troposphere layer:
        /// a linear temperature lapse of 6.5 K/km from 15 C at sea level, hydrostatic equilibrium,
        /// dry air. Real observatories publish their own mean pressure; this is what to use when they
        /// do not, and it is a standard rather than a guess.
        /// </summary>
        public static double StandardPressureMillibar(double altitudeMeters)
        {
            const double p0 = 1013.25;          // mbar at sea level
            const double t0 = 288.15;           // K
            const double lapse = 0.0065;        // K/m
            const double exponent = 5.25588;    // g M / (R L), the ISA troposphere exponent
            double h = Math.Max(0.0, Math.Min(11000.0, altitudeMeters));
            return p0 * Math.Pow(1.0 - lapse * h / t0, exponent);
        }

        /// <summary>Temperature at an altitude, degrees C, from the same standard atmosphere.</summary>
        public static double StandardTemperatureCelsius(double altitudeMeters)
        {
            double h = Math.Max(0.0, Math.Min(11000.0, altitudeMeters));
            return 15.0 - 0.0065 * h;
        }

        /// <summary>
        /// Saturation vapour pressure of water over liquid, millibars, from the Buck (1981, J. Appl.
        /// Meteorol. 20, 1527) equation, the form meteorology uses, accurate to 0.1% from -20 to
        /// +50 C, rather than the Magnus form usually quoted.
        /// </summary>
        public static double SaturationVapourPressureMillibar(double temperatureCelsius)
        {
            double t = temperatureCelsius;
            return 6.1121 * Math.Exp((18.678 - t / 234.5) * t / (257.14 + t));
        }

        /// <summary>Partial pressure of water vapour from a relative humidity in 0..1.</summary>
        public static double WaterVapourPressureMillibar(double temperatureCelsius, double relativeHumidity)
            => SaturationVapourPressureMillibar(temperatureCelsius)
             * Math.Max(0.0, Math.Min(1.0, relativeHumidity));

        /// <summary>
        /// Median relative humidity at a good observing site. Dome-opening criteria at Paranal and
        /// La Silla put the limit near 80% and the median well below it; 0.3 is the value ESO's own
        /// astroclimate pages report for Paranal, and it is used as the default for every site
        /// because none of them publishes one alongside the seeing figures this mod already takes.
        /// The effect is small either way: at 45 degrees zenith distance, moving from dry air to 30%
        /// humidity changes the 400-to-700 nm differential refraction by under 0.5%.
        /// </summary>
        public const double DefaultRelativeHumidity = 0.3;

        /// <summary>
        /// Splits a passband into sub-bands with their photon weights and their dispersion offsets,
        /// ready for OpticalPsf.BuildChromaticKernel.
        ///
        /// The weight of a sub-band is how many PHOTONS arrive in it: the source's spectrum times
        /// the system's throughput, integrated across the sub-band. That is the weighting the sum of
        /// monochromatic images actually has, and it is why a red star's dispersion smear is shorter
        /// than a blue star's; most of its photons are at the long end, where refraction is weaker.
        ///
        /// The offset is along the direction toward the zenith, which the caller supplies as a unit
        /// vector in pixel space, since that direction depends on the projection and on the mount.
        /// </summary>
        /// <param name="response">The instrument's own throughput, which decides what reaches the detector.</param>
        /// <param name="sourceSpectrum">Photon rate per unit wavelength, any scale. Null for a flat one.</param>
        /// <param name="zenithUnitX">Unit vector in PIXEL space pointing toward the zenith.</param>
        public static ChromaticSubBand[] SplitPassband(
            SystemResponse response,
            Func<double, double> sourceSpectrum,
            double minWavelengthMeters, double maxWavelengthMeters, int subBandCount,
            double zenithDistanceDeg, double plateScaleArcsecPerPixel,
            double zenithUnitX, double zenithUnitY,
            double referenceWavelengthMeters,
            double temperatureCelsius, double pressureMillibar, double waterVapourPressureMillibar)
        {
            if (response == null || subBandCount < 1) return null;
            if (!(maxWavelengthMeters > minWavelengthMeters) || !(plateScaleArcsecPerPixel > 0.0)) return null;

            var bands = new ChromaticSubBand[subBandCount];
            double step = (maxWavelengthMeters - minWavelengthMeters) / subBandCount;
            for (int i = 0; i < subBandCount; i++)
            {
                // Midpoint of the sub-band, and its weight from a few samples across it: the
                // throughput of a real filter has structure on the scale of its own edges, so one
                // sample at the centre would miss the roll-off entirely.
                double lo = minWavelengthMeters + i * step;
                double centre = lo + 0.5 * step;
                const int inner = 4;
                double weight = 0.0;
                for (int j = 0; j < inner; j++)
                {
                    double l = lo + (j + 0.5) * step / inner;
                    double throughput = response.ThroughputAt(l);
                    if (!(throughput > 0.0)) continue;
                    double s = sourceSpectrum != null ? sourceSpectrum(l) : 1.0;
                    if (!(s > 0.0)) continue;
                    weight += s * throughput;
                }
                weight *= step / inner;

                double offsetArcsec = DifferentialRefractionArcsec(
                    centre * 1e6, referenceWavelengthMeters * 1e6, zenithDistanceDeg,
                    temperatureCelsius, pressureMillibar, waterVapourPressureMillibar);
                double offsetPx = double.IsNaN(offsetArcsec) ? 0.0 : offsetArcsec / plateScaleArcsecPerPixel;

                bands[i] = new ChromaticSubBand
                {
                    WavelengthMeters = centre,
                    Weight = weight,
                    OffsetX = offsetPx * zenithUnitX,
                    OffsetY = offsetPx * zenithUnitY,
                };
            }
            return bands;
        }
    }

    /// <summary>One slice of a passband: where it is, how many photons it carries, and how far the atmosphere has moved it.</summary>
    public struct ChromaticSubBand
    {
        public double WavelengthMeters;
        /// <summary>Photon weight. Need not be normalised.</summary>
        public double Weight;
        /// <summary>Dispersion offset in pixels, already resolved into the frame's axes.</summary>
        public double OffsetX;
        public double OffsetY;

        /// <summary>
        /// Gaussian broadening to apply to THIS sub-band's kernel, arcsec FWHM: residual
        /// wavefront error plus pointing excursion (see OpticalPsf.BuildKernel).
        ///
        /// Per sub-band rather than one figure for the whole passband, because the wavefront
        /// half of it is not achromatic. A fixed surface error of a given physical depth is a
        /// larger fraction of a wave in the blue than in the red, so it costs more image quality
        /// there; HST's own published delivered widths turn over near 500 nm and climb again to
        /// 0.083 arcsec at 200 nm, and holding one figure across the band would erase that.
        /// Zero leaves this sub-band purely diffraction-limited.
        /// </summary>
        public double GaussianFwhmArcsec;
    }
}
