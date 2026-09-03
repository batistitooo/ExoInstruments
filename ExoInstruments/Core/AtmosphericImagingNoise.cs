using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Physics for the solar-system camera pipeline: atmospheric extinction (Bouguer's law)
    /// and CCD sensor noise (shot noise, dark current). Pure C#, no Unity dependency.
    /// Sensor numbers (full well, dark current, read noise) are NOT hardcoded here; callers
    /// pass in the active telescope's own VisualTelescopeSpec values, so this stays correct
    /// for whichever camera VisualTelescopeCatalog says is attached.
    /// Cloud cover is handled separately by EveCloudIntegration, no procedural fallback.
    /// </summary>
    public static class AtmosphericImagingNoise
    {
        // Broadband extinction at a decent mid-altitude site; 0.20 mag/airmass is a
        // representative average. Separate from ImagingObservingConditions (which only
        // gates whether imaging is allowed, not how bright the result looks).
        public const double ExtinctionMagPerAirmass = 0.20;

        /// <summary>Fraction of flux transmitted at the given airmass (1 at zenith, falling toward horizon).</summary>
        public static double ExtinctionTransmission(double airmass)
        {
            if (double.IsNaN(airmass)) return 0.0;
            if (airmass < 1.0) return 1.0;
            if (double.IsPositiveInfinity(airmass)) return 0.0;
            return Math.Pow(10.0, -0.4 * ExtinctionMagPerAirmass * (airmass - 1.0));
        }

        // Pressure scale height of the atmosphere: how the Rayleigh column above a site thins with altitude.
        private const double PressureScaleHeightMeters = 8000.0;

        // Angstrom (1929) turbidity exponent for the aerosol term. 1.3 is the standard continental-aerosol
        // value used throughout atmospheric optics.
        private const double AerosolAngstromExponent = 1.3;

        // Extinction is quoted in magnitudes but computed as an optical depth; 2.5/ln(10) converts between
        // them.
        private const double OpticalDepthToMagnitudes = 1.0857362;

        // Rayleigh-scattering optical depth of the full sea-level atmospheric column at wavelength lambda
        // (micrometres), from the standard parameterisation tau = 0.008569 * l^-4 * (1 + 0.0113 * l^-2 +
        // 0.00013 * l^-4) (Hansen & Travis 1974, Space Sci. Rev. 16, 527; the same form tabulated by Bucholtz
        // 1995, Appl. Opt. 34, 2765). At 550nm this gives 0.106 mag, the textbook sea-level Rayleigh extinction
        // at V.
        private static double RayleighOpticalDepthSeaLevel(double wavelengthMicrons)
        {
            double l2 = wavelengthMicrons * wavelengthMicrons;
            double l4 = l2 * l2;
            return 0.008569 / l4 * (1.0 + 0.0113 / l2 + 0.00013 / l4);
        }

        /// <summary>
        /// Extinction coefficient (magnitudes per airmass) at the given wavelength, for a site
        /// at the given altitude.
        ///
        /// Atmospheric extinction is strongly wavelength-dependent, since a real site loses
        /// roughly three times as much light at B as at I. A single grey coefficient makes
        /// every filter behave identically, which is wrong for exactly the multi-filter LRGB
        /// imaging this pipeline exists to do.
        ///
        /// The SHAPE is physics: Rayleigh scattering above (an exact function of wavelength and
        /// of the air column over the site), plus an aerosol term following the Angstrom
        /// lambda^-alpha turbidity law. The AMPLITUDE of the aerosol term is not invented
        /// either: it is whatever residual makes the total at Johnson V come out at this
        /// site's own measured ExtinctionMagPerAirmass, since aerosol loading is precisely the
        /// site-dependent part of extinction. Ozone's Chappuis band (~0.01-0.02 mag in the
        /// visible) is not modelled separately; it is absorbed into that same residual.
        /// </summary>
        public static double ExtinctionMagPerAirmassAt(double wavelengthMeters, double siteAltitudeMeters)
        {
            if (wavelengthMeters <= 0.0) return ExtinctionMagPerAirmass;

            double columnFraction = Math.Exp(-Math.Max(0.0, siteAltitudeMeters) / PressureScaleHeightMeters);
            double microns = wavelengthMeters * 1e6;
            double vMicrons = StellarPhotometry.JohnsonVWavelengthMeters * 1e6;

            double rayleigh = RayleighOpticalDepthSeaLevel(microns) * columnFraction * OpticalDepthToMagnitudes;
            double rayleighAtV = RayleighOpticalDepthSeaLevel(vMicrons) * columnFraction * OpticalDepthToMagnitudes;

            // Aerosol amplitude pinned so the total reproduces the site's own V coefficient.
            double aerosolAtV = Math.Max(0.0, ExtinctionMagPerAirmass - rayleighAtV);
            double aerosol = aerosolAtV * Math.Pow(vMicrons / microns, AerosolAngstromExponent);

            return rayleigh + aerosol;
        }

        /// <summary>Fraction of flux transmitted at the given airmass and wavelength: the per-filter form of ExtinctionTransmission.</summary>
        public static double ExtinctionTransmissionAt(double airmass, double wavelengthMeters, double siteAltitudeMeters)
        {
            if (double.IsNaN(airmass)) return 0.0;
            if (airmass < 1.0) return 1.0;
            if (double.IsPositiveInfinity(airmass)) return 0.0;
            double k = ExtinctionMagPerAirmassAt(wavelengthMeters, siteAltitudeMeters);
            return Math.Pow(10.0, -0.4 * k * (airmass - 1.0));
        }

        // Effective height of the dominant turbulent layer, used to project a source's angular size into a
        // linear size at that layer (see ScintillationExcessSigma). Same order of magnitude as the pressure
        // scale height above; both trace to where the bulk of the atmosphere (and its turbulence) actually
        // sits.
        private const double TurbulenceLayerHeightMeters = 8000.0;

        /// <summary>
        /// Scintillation sigma above the zenith value, for a given aperture/airmass/exposure.
        /// Lets the camera reuse the same Young formula as the transit photometers
        /// without going through the Transit-only API.
        ///
        /// angularDiameterRad is the imaged body's own apparent angular size (0 for a
        /// point source, e.g. a star). Young's formula alone models a point source: a
        /// resolved planetary disk spans many independent turbulent cells at once, and
        /// their intensity fluctuations average out across the disk the same way a
        /// larger telescope aperture averages them out over its own area (extended-source
        /// scintillation suppression, Dravins, Lindegren, Mezey &amp; Young 1997, "Atmospheric
        /// Intensity Scintillation of Stars I", PASP 109, 173). Modeled here by projecting
        /// the source's angular size to a linear size at the turbulent layer's height and
        /// combining it with the real aperture (root-sum-square, i.e. as an equivalent
        /// larger averaging aperture) before applying Young's formula; so a resolved
        /// planet scintillates far less than a point star through the same telescope,
        /// while angularDiameterRad=0 leaves star photometry exactly as before.
        /// </summary>
        public static double ScintillationExcessSigma(double apertureMeters, double siteAltitudeMeters, double airmass, double exposureSeconds, double angularDiameterRad = 0.0)
        {
            if (double.IsNaN(airmass) || double.IsInfinity(airmass) || airmass <= 1.0) return 0.0;
            double sourceSizeMeters = Math.Max(0.0, angularDiameterRad) * TurbulenceLayerHeightMeters;
            double effectiveApertureMeters = Math.Sqrt(apertureMeters * apertureMeters + sourceSizeMeters * sourceSizeMeters);
            double atZenith = AtmosphericNoise.YoungSigmaRaw(effectiveApertureMeters, siteAltitudeMeters, 1.0, exposureSeconds);
            double atAirmass = AtmosphericNoise.YoungSigmaRaw(effectiveApertureMeters, siteAltitudeMeters, airmass, exposureSeconds);
            return Math.Sqrt(Math.Max(0.0, atAirmass * atAirmass - atZenith * atZenith));
        }

        // Sensor noise no longer lives here. The imaging pipeline now carries real ELECTRON
        // counts rather than fractions of full well, so shot noise is drawn as a genuine Poisson
        // deviate on the electron count itself (SolarSystemCameraTexture.SamplePoisson, PTRS per
        // Hormann 1993) and dark current is simply a rate times an exposure. Both of the helpers
        // that used to approximate those in normalised units, ShotNoiseSigma and DarkCurrent,
        // are gone with the normalisation that made them necessary: sqrt(N) is only the width of
        // the Poisson distribution, not the distribution, and at the few electrons a faint sky
        // reaches the difference is measurable and the Gaussian goes negative.
    }
}
