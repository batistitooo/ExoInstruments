using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// How bright the night sky itself is, in the unit the quantity is actually measured and
    /// published in: V magnitudes per square arcsecond.
    ///
    /// This replaces a set of per-second, per-pixel rates that had no physical unit attached.
    /// The unit matters for more than tidiness. Sky background is what sets the noise floor a
    /// faint star has to climb out of, and expressed per pixel it silently depended on the plate
    /// scale, so binning the sensor, fitting a Barlow, or switching from the RC20 to the VLT
    /// changed the sky's apparent brightness, which is not a thing that happens. Surface
    /// brightness is a property of the sky; how many electrons it puts in a pixel is a property
    /// of the instrument, and the two only meet in ElectronsPerPixelPerSecond below.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class SkyBrightnessModel
    {
        /// <summary>
        /// Dark-sky zenith V surface brightness at a good site with no Moon: 21.7 mag/arcsec^2
        /// (Patat 2003, A&amp;A 400, 1183, "UBVRI night sky brightness at ESO-Paranal"). Dominated
        /// by airglow, which is why AirglowVanRhijnFactor applies to this term.
        /// </summary>
        public const double DarkSkyZenithVMagPerArcsec2 = 21.7;

        /// <summary>
        /// Zodiacal light at the ecliptic pole, its faintest value: V = 23.3 mag/arcsec^2
        /// (Leinert et al. 1998, A&amp;AS 127, 1). Carried as a separate term from the airglow
        /// because it originates outside the atmosphere and so is attenuated by extinction
        /// rather than enhanced by the van Rhijn path length.
        ///
        /// Held at the polar value everywhere: the zodiacal cloud's brightness depends on
        /// ecliptic latitude and solar elongation, and while an ecliptic plane is well defined
        /// in any KSP system, the cloud's own density distribution is a Solar System measurement
        /// with no counterpart to read from the game. The faintest published value is the
        /// conservative choice.
        /// </summary>
        public const double ZodiacalVMagPerArcsec2 = 23.3;

        /// <summary>
        /// V surface brightness with a full Moon at the reference separation, ~18.7
        /// mag/arcsec^2, about 3 magnitudes above the dark-sky value. Krisciunas &amp; Schaefer
        /// (1991, PASP 103, 1033) model the moonlit sky brightness in full; their scattering
        /// kernel is already implemented in MoonlightPollution and supplies the separation
        /// dependence, so only its normalisation is needed here.
        /// </summary>
        public const double FullMoonVMagPerArcsec2 = 18.7;

        /// <summary>
        /// Height of the emitting airglow layer. The green OI 557.7nm line and the OH Meinel
        /// bands both originate near 90 km (Roach &amp; Gordon 1973, "The Light of the Night Sky").
        /// </summary>
        public const double AirglowLayerHeightMeters = 90000.0;

        /// <summary>
        /// Solar depression at which astronomical twilight ends; by definition, the point at
        /// which scattered sunlight drops below the natural airglow.
        /// </summary>
        public const double AstronomicalTwilightSunAltitudeDeg = -18.0;

        /// <summary>
        /// Rate at which the twilight sky brightens as the Sun climbs back toward the horizon,
        /// in V magnitudes per degree of solar altitude, through the astronomical twilight
        /// range. Patat et al. (2006, A&amp;A 455, 385, "The twilight sky at ESO-Paranal") measure
        /// this curve; 0.6 mag/deg is a straight-line approximation to it across the -18 to
        /// -12 deg span this pipeline allows imaging in, not a fit to the whole twilight.
        /// </summary>
        public const double TwilightMagPerDegree = 0.6;

        /// <summary>
        /// The van Rhijn (1921) function: how much longer the line of sight through a thin
        /// emitting shell at height h becomes at zenith angle z, relative to straight up.
        ///
        ///     I(z)/I(0) = 1 / sqrt(1 - (R/(R+h))^2 * sin^2(z))
        ///
        /// Airglow is emitted INSIDE the atmosphere, so unlike a star it gets brighter toward
        /// the horizon rather than fainter, because there is more emitting column along the
        /// path. In practice the enhancement is largely cancelled by the extinction over that
        /// same path, which is why the caller applies both and neither alone.
        /// </summary>
        public static double AirglowVanRhijnFactor(double zenithAngleDeg, double planetRadiusMeters)
        {
            if (planetRadiusMeters <= 0.0) return 1.0;
            double ratio = planetRadiusMeters / (planetRadiusMeters + AirglowLayerHeightMeters);
            double sinZ = Math.Sin(Math.Min(90.0, Math.Max(0.0, zenithAngleDeg)) * Math.PI / 180.0);
            double denom = 1.0 - ratio * ratio * sinZ * sinZ;
            if (denom <= 1e-6) return 1.0 / Math.Sqrt(1e-6);
            return 1.0 / Math.Sqrt(denom);
        }

        /// <summary>
        /// Twilight's own contribution as a V surface brightness, or +Infinity (i.e. no
        /// contribution) once the Sun is far enough down. See TwilightMagPerDegree.
        /// </summary>
        public static double TwilightVMagPerArcsec2(double sunAltitudeDeg)
        {
            if (sunAltitudeDeg <= AstronomicalTwilightSunAltitudeDeg) return double.PositiveInfinity;
            double degreesAboveAstronomical = sunAltitudeDeg - AstronomicalTwilightSunAltitudeDeg;
            return DarkSkyZenithVMagPerArcsec2 - TwilightMagPerDegree * degreesAboveAstronomical;
        }

        /// <summary>
        /// Moonlight's contribution as a V surface brightness, from the "1.0 = full moon at the
        /// reference separation" excess the camera already computes with the Krisciunas &amp;
        /// Schaefer kernel. Returns +Infinity when there is no moonlight at all.
        /// </summary>
        public static double MoonlightVMagPerArcsec2(double moonSkyExcess)
        {
            if (moonSkyExcess <= 0.0) return double.PositiveInfinity;
            // Pogson: scaling a flux by moonSkyExcess shifts the magnitude by -2.5*log10 of it.
            return FullMoonVMagPerArcsec2 - 2.5 * Math.Log10(moonSkyExcess);
        }

        /// <summary>Adds a surface brightness to a running total held as a flux ratio. +Infinity contributes nothing.</summary>
        public static double AddMagnitude(double fluxSum, double magPerArcsec2)
        {
            if (double.IsPositiveInfinity(magPerArcsec2) || double.IsNaN(magPerArcsec2)) return fluxSum;
            return fluxSum + Math.Pow(10.0, -0.4 * magPerArcsec2);
        }

        /// <summary>Converts a summed flux ratio back to a V surface brightness. +Infinity when nothing was added.</summary>
        public static double FluxToMagPerArcsec2(double fluxSum)
        {
            if (fluxSum <= 0.0) return double.PositiveInfinity;
            return -2.5 * Math.Log10(fluxSum);
        }

        /// <summary>
        /// Electrons a sky of the given V surface brightness deposits in one pixel per second,
        /// through the given filter on the given telescope.
        ///
        /// The same photometric chain a point source goes through (PhotonFluxModel), applied to
        /// the flux inside one pixel's solid angle, so the sky and the stars sitting on it are
        /// on one flux scale by construction, which is what makes a computed signal-to-noise
        /// ratio mean anything. No colour term is applied: unlike a star the night sky is not a
        /// continuum source (airglow is line emission, moonlight is reflected sunlight), and the
        /// flat-in-band approximation PhotonFluxModel already documents is the standard
        /// exposure-time-calculator treatment.
        /// </summary>
        public static double ElectronsPerPixelPerSecond(
            double vMagPerArcsec2, double plateScaleArcsecPerPixel,
            double filterBandwidthAngstrom, double apertureAreaCm2,
            double quantumEfficiency, double transmission)
        {
            if (double.IsPositiveInfinity(vMagPerArcsec2) || double.IsNaN(vMagPerArcsec2)) return 0.0;
            double pixelSolidAngleArcsec2 = plateScaleArcsecPerPixel * plateScaleArcsecPerPixel;
            if (pixelSolidAngleArcsec2 <= 0.0) return 0.0;

            double perArcsec2 = PhotonFluxModel.CollectedElectrons(
                vMagPerArcsec2, filterBandwidthAngstrom, apertureAreaCm2,
                quantumEfficiency, 1.0, transmission);

            return perArcsec2 * pixelSolidAngleArcsec2;
        }
    }
}
