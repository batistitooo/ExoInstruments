using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Assembles one transit-photometry measurement's real electron budget and hands it to the
    /// CCD equation: this is what turns a star, an instrument and an observing condition into the
    /// error bar on a light-curve point.
    ///
    /// It exists to put the transit half of this mod on the same physics as the imaging half. Both
    /// now start from the same integrated system response (SystemBandpass), the same zero-point
    /// photon flux (PhotonFluxModel), the same published sky surface brightnesses
    /// (SkyBrightnessModel) and the same atmospheric extinction, so an instrument's photometric
    /// precision follows from its hardware rather than from a fitted constant. What the two halves
    /// do NOT share is the noise realisation: the camera draws real Poisson deviates per pixel,
    /// while this collapses the same budget analytically into one sigma per exposure, which is the
    /// right level for a light curve of thousands of points.
    ///
    /// The budget, in the order it is built:
    ///
    ///   1. Effective photometric width W for this star's own spectrum through this instrument at
    ///      this airmass, from SystemResponse: the filter, the optics, the QE curve, the
    ///      atmosphere and the star's colour, integrated over the passband.
    ///   2. Total source electrons over the exposure, from the star's apparent magnitude
    ///      (PhotonFluxModel), through the real obstructed collecting area.
    ///   3. The photometric aperture: radius 0.68 FWHM (Howell 1989), its area in pixels, and the
    ///      fraction of the source's flux inside it.
    ///   4. Sky electrons per pixel, summed as real V surface brightnesses over airglow, zodiacal
    ///      light and moonlight, each attenuated the way its own origin requires.
    ///   5. Dark and read noise from the detector's own figures.
    ///   6. CcdEquation -> sigma_F/F.
    ///   7. Scintillation (Young 1967) added in quadrature, because it is an atmospheric transfer
    ///      effect and not a term of the CCD equation.
    ///
    /// NOTHING HERE RUNS WITHOUT SOURCED HARDWARE. TryEstimate returns false whenever the
    /// instrument carries no complete PhotometricDetector, and the caller keeps the empirical
    /// scaling it used before. See PhotometricDetector for why that is a hard gate rather than a
    /// set of defaults.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class TransitPhotometry
    {
        // Airmass beyond which the seeing power law is held flat. X = 6 is about 9.5 degrees of altitude,
        // already below where anyone observes and far below where the plane-parallel atmosphere the X^(3/5) law
        // assumes still holds. Same cap, for the same reason, as SolarSystemCameraTexture's MaxSeeingAirmass.
        private const double MaxSeeingAirmass = 6.0;

        // Wavelength every published seeing figure is referred to, and so the wavelength
        // MedianZenithSeeingArcsec is quoted at.
        private const double SeeingReferenceWavelengthNm = 500.0;

        /// <summary>
        /// The full breakdown behind one error bar, so the model can be inspected rather than
        /// merely trusted, the same reason SolarSystemCameraTexture publishes its own
        /// LastEffectiveWidthAngstrom, LastSkyBrightness and so on.
        /// </summary>
        public struct Budget
        {
            /// <summary>Effective photometric width for this star's spectrum at this airmass, Angstrom.</summary>
            public double EffectiveWidthAngstrom;
            /// <summary>Source electrons collected over the exposure, before the aperture loses any.</summary>
            public double TotalSourceElectrons;
            /// <summary>Source electrons inside the photometric aperture.</summary>
            public double ApertureSourceElectrons;
            /// <summary>Delivered PSF FWHM at this airmass and wavelength, arcsec.</summary>
            public double PsfFwhmArcsec;
            /// <summary>Pixels in the photometric aperture.</summary>
            public double AperturePixels;
            /// <summary>Sky electrons per pixel over the exposure.</summary>
            public double SkyElectronsPerPixel;
            /// <summary>Summed sky surface brightness actually used, V mag/arcsec^2.</summary>
            public double SkyVMagPerArcsec2;
            /// <summary>Dark electrons per pixel over the exposure.</summary>
            public double DarkElectronsPerPixel;
            /// <summary>Signal-to-noise from the CCD equation alone, before scintillation.</summary>
            public double SignalToNoise;
            /// <summary>Fractional flux sigma from the CCD equation alone.</summary>
            public double PhotometricSigma;
            /// <summary>Young (1967) scintillation sigma at this airmass and exposure.</summary>
            public double ScintillationSigma;
            /// <summary>The two above in quadrature, the light-curve point's error bar.</summary>
            public double TotalSigma;
        }

        /// <summary>
        /// The error bar on one exposure, as a fractional flux uncertainty, or false when this
        /// instrument has no sourced detector to compute one from.
        ///
        /// planetRadiusMeters feeds the van Rhijn path-lengthening of the airglow term and is the
        /// observer's own world's radius. Passing 0 (the default, for callers with no CelestialBody
        /// in reach) drops that enhancement while extinction still dims the same term, so the
        /// airglow contribution is then under-estimated away from zenith, by a factor approaching
        /// 1.9 at airmass 2 for an Earth-sized world. Supply it wherever it is available.
        /// </summary>
        public static bool TryEstimate(
            StarTarget star, InstrumentSpec instrument,
            double airmass, double moonSkyExcess,
            out Budget budget,
            double planetRadiusMeters = 0.0)
        {
            budget = default(Budget);
            if (star == null || instrument == null) return false;

            PhotometricDetector detector = instrument.Detector;
            if (detector == null || !detector.IsComplete(instrument.IsSpaceBased)) return false;

            // Open-shutter time of one integration, and how many are co-added into this point.
            // Signal, sky and dark accumulate over the product; read noise does not (see below).
            double exposureSeconds = detector.ExposureSeconds ?? instrument.CadenceSeconds;
            int integrations = Math.Max(1, detector.IntegrationsPerMeasurement ?? 1);
            double openShutterSeconds = exposureSeconds * integrations;
            if (!(openShutterSeconds > 0.0)) return false;

            // A space instrument has no air to look through, so it also has no airmass; a ground
            // one is clamped at the horizon rather than allowed to run to infinity.
            double x = instrument.IsSpaceBased ? 1.0 : ClampAirmass(airmass);

            double teffK = star.EffectiveTempK ?? 0.0;   // 0 = colour unknown, integrate flat
            double areaCm2 = detector.EffectiveApertureAreaCm2();
            if (!(areaCm2 > 0.0)) return false;

            SystemResponse response = ResponseFor(detector, instrument, x);
            if (response == null) return false;

            // --- 1-2. Source ------------------------------------------------
            budget.EffectiveWidthAngstrom = instrument.IsSpaceBased
                ? response.EffectiveWidthAngstromForTemperatureNoExtinction(teffK)
                : response.EffectiveWidthAngstromForTemperature(teffK);

            budget.TotalSourceElectrons = PhotonFluxModel.CollectedElectrons(
                star.ApparentMagnitude, budget.EffectiveWidthAngstrom, areaCm2, openShutterSeconds);
            if (!(budget.TotalSourceElectrons > 0.0)) return false;

            // --- 3. Photometric aperture ------------------------------------
            budget.PsfFwhmArcsec = DeliveredFwhmArcsec(detector, instrument, x);
            if (!(budget.PsfFwhmArcsec > 0.0)) return false;

            double plateScale = detector.PlateScaleArcsecPerPixel.Value;
            double apertureRadiusArcsec;
            if (detector.PhotometricApertureRadiusPixels.HasValue)
            {
                // The survey's own published aperture, for an instrument whose sampling makes the
                // seeing-derived optimum meaningless.
                apertureRadiusArcsec = detector.PhotometricApertureRadiusPixels.Value * plateScale;
            }
            else
            {
                apertureRadiusArcsec = CcdEquation.OptimalApertureRadiusInFwhm * budget.PsfFwhmArcsec;
            }

            budget.AperturePixels = CcdEquation.AperturePixels(apertureRadiusArcsec, plateScale);

            // Encircled energy at whichever radius was chosen, in units of this PSF's own FWHM.
            double enclosed = CcdEquation.GaussianEnclosedEnergy(apertureRadiusArcsec / budget.PsfFwhmArcsec);
            budget.ApertureSourceElectrons = budget.TotalSourceElectrons * enclosed;

            // --- 4. Sky -----------------------------------------------------
            budget.SkyElectronsPerPixel = SkyElectronsPerPixel(
                detector, instrument, response, x, moonSkyExcess, planetRadiusMeters,
                openShutterSeconds, areaCm2, out budget.SkyVMagPerArcsec2);

            // --- 5. Detector ------------------------------------------------
            budget.DarkElectronsPerPixel = detector.DarkCurrentElectronsPerSecond.Value * openShutterSeconds;

            // Read noise is the one term that does NOT scale with open-shutter time: it is paid
            // per readout, so a point co-added from n reads carries n times the VARIANCE, i.e.
            // sqrt(n) times the noise. Passing the effective figure keeps CcdEquation a statement
            // of the published equation rather than of this pipeline's stacking scheme.
            double effectiveReadNoise = detector.ReadNoiseElectrons.Value * Math.Sqrt(integrations);

            // --- 6. The equation --------------------------------------------
            double backgroundPixels = budget.AperturePixels * CcdEquation.BackgroundToApertureAreaRatio;
            budget.SignalToNoise = CcdEquation.SignalToNoise(
                budget.ApertureSourceElectrons,
                budget.AperturePixels,
                backgroundPixels,
                budget.SkyElectronsPerPixel,
                budget.DarkElectronsPerPixel,
                effectiveReadNoise,
                detector.GainElectronsPerAdu);

            if (!(budget.SignalToNoise > 0.0)) return false;
            budget.PhotometricSigma = 1.0 / budget.SignalToNoise;

            // --- 7. Scintillation -------------------------------------------
            // The FULL Young sigma, not the excess above zenith that AtmosphericNoise returns for
            // the empirical path. That subtraction exists only because the fitted
            // ReferencePrecision already contained typical-conditions scintillation; the CCD
            // equation contains none at all, so the whole term belongs here.
            // Over the total open-shutter time, not one integration: Young's 1/sqrt(2t) already IS
            // the averaging-down of an incoherent process, and n independent exposures of t
            // seconds average exactly as one of n*t would (scintillation decorrelates on
            // millisecond timescales, far below any exposure modelled here).
            budget.ScintillationSigma = instrument.IsSpaceBased
                ? 0.0
                : AtmosphericNoise.YoungSigmaRaw(
                      detector.ApertureMeters.Value, instrument.SiteAltitudeMeters, x, openShutterSeconds);

            budget.TotalSigma = Math.Sqrt(
                budget.PhotometricSigma * budget.PhotometricSigma
              + budget.ScintillationSigma * budget.ScintillationSigma);

            return budget.TotalSigma > 0.0 && !double.IsNaN(budget.TotalSigma)
                                           && !double.IsInfinity(budget.TotalSigma);
        }

        // ---------------------------------------------------------------- Sky

        // Sky electrons in one pixel over the exposure, summed from the published surface brightnesses in
        // SkyBrightnessModel and attenuated per term. This mirrors SolarSystemCameraTexture.GatherSkyBackground
        // deliberately, so the transit and imaging halves agree about how bright the night sky is. The terms
        // are summed in two spectral groups because they do not share a spectrum: moonlight and zodiacal light
        // are sunlight scattered off something and carry the solar shape, while airglow is atmospheric line
        // emission with no continuum this pipeline could integrate and is integrated flat. Twilight is not
        // included: a transit run is scheduled inside the observing window ImagingObservingConditions defines,
        // which already requires the Sun below astronomical twilight, where SkyBrightnessModel's own twilight
        // term returns no contribution anyway.
        private static double SkyElectronsPerPixel(
            PhotometricDetector detector, InstrumentSpec instrument, SystemResponse response,
            double airmass, double moonSkyExcess, double planetRadiusMeters,
            double exposureSeconds, double areaCm2, out double summedVMagPerArcsec2)
        {
            double plateScale = detector.PlateScaleArcsecPerPixel.Value;

            if (instrument.IsSpaceBased)
            {
                // Above the atmosphere there is no airglow and nothing to attenuate: what remains
                // is the zodiacal light, which is where it was all along.
                double fluxZodiacal = Math.Pow(10.0, -0.4 * SkyBrightnessModel.ZodiacalVMagPerArcsec2);
                summedVMagPerArcsec2 = SkyBrightnessModel.FluxToMagPerArcsec2(fluxZodiacal);
                return SkyBrightnessModel.ElectronsPerPixelPerSecond(
                           summedVMagPerArcsec2, plateScale, response, areaCm2, 1.0,
                           SourceSpectra.SolarPhotosphereTemperatureK)
                     * exposureSeconds;
            }

            double wavelengthMeters = detector.FilterCentralWavelengthNm.Value * 1e-9;
            double transmission = AtmosphericImagingNoise.ExtinctionTransmissionAt(
                airmass, wavelengthMeters, instrument.SiteAltitudeMeters);

            // Plane-parallel: X = sec(z), so the zenith angle the van Rhijn function needs follows
            // from the airmass the caller already has.
            double zenithAngleDeg = Math.Acos(Math.Min(1.0, 1.0 / Math.Max(1.0, airmass))) * 180.0 / Math.PI;

            // Airglow: emitted INSIDE the atmosphere, so the van Rhijn path lengthening brightens
            // it toward the horizon while extinction over the same path dims it. Both apply.
            double fluxFlat = Math.Pow(10.0, -0.4 * SkyBrightnessModel.DarkSkyZenithVMagPerArcsec2)
                            * SkyBrightnessModel.AirglowVanRhijnFactor(zenithAngleDeg, planetRadiusMeters)
                            * transmission;

            // Zodiacal light originates outside the atmosphere: simply attenuated by it.
            double fluxSolar = Math.Pow(10.0, -0.4 * SkyBrightnessModel.ZodiacalVMagPerArcsec2) * transmission;

            // Moonlight is sunlight scattered WITHIN the atmosphere, so the extinction along the
            // line of sight is already inside the measured surface brightness Krisciunas & Schaefer
            // calibrated against, and is not applied a second time.
            fluxSolar = SkyBrightnessModel.AddMagnitude(
                fluxSolar, SkyBrightnessModel.MoonlightVMagPerArcsec2(moonSkyExcess));

            summedVMagPerArcsec2 = SkyBrightnessModel.FluxToMagPerArcsec2(fluxFlat + fluxSolar);

            // The response is used in its no-extinction form because transmission has been applied
            // per term above; folding one factor into all of them would erase the distinction.
            double perSecond =
                  SkyBrightnessModel.ElectronsPerPixelPerSecond(
                      SkyBrightnessModel.FluxToMagPerArcsec2(fluxFlat),
                      plateScale, response, areaCm2, 1.0, 0.0)
                + SkyBrightnessModel.ElectronsPerPixelPerSecond(
                      SkyBrightnessModel.FluxToMagPerArcsec2(fluxSolar),
                      plateScale, response, areaCm2, 1.0,
                      SourceSpectra.SolarPhotosphereTemperatureK);

            return perSecond * exposureSeconds;
        }

        // ---------------------------------------------------------------- PSF

        // Delivered PSF FWHM (arcsec) at this airmass and this filter's wavelength. Two standard Kolmogorov
        // scalings, both following from r0 proportional to lambda^(6/5) cos(z)^(3/5) (Roddier 1981, Progress in
        // Optics 19, 281) and FWHM = 0.98 lambda / r0: FWHM(X, lambda) = FWHM(zenith, 500nm) * X^(3/5) *
        // (lambda / 500nm)^(-1/5) The wavelength term is small but has the right sign and is free: a red band
        // sees a sharper image through the same air than a blue one, which is why it is applied rather than
        // dropped. The diffraction core is NOT convolved in here; for every ground instrument this model
        // applies to it is far inside the seeing disc; so for a space instrument the detector's own delivered
        // figure is used directly instead.
        private static double DeliveredFwhmArcsec(PhotometricDetector detector, InstrumentSpec instrument, double airmass)
        {
            if (instrument.IsSpaceBased) return detector.DeliveredPsfFwhmArcsec.Value;

            double zenithFwhm = detector.MedianZenithSeeingArcsec.Value;
            double wavelengthNm = detector.FilterCentralWavelengthNm.Value;

            return zenithFwhm
                 * Math.Pow(airmass, 3.0 / 5.0)
                 * Math.Pow(wavelengthNm / SeeingReferenceWavelengthNm, -1.0 / 5.0);
        }

        private static double ClampAirmass(double airmass)
        {
            if (double.IsNaN(airmass) || airmass < 1.0) return 1.0;
            return Math.Min(MaxSeeingAirmass, airmass);
        }

        // ------------------------------------------------- Response caching

        // Airmass grid the system response is tabulated on. Building one response costs a 160-entry colour
        // table of 64-node Simpson quadratures, under a millisecond, but a light curve asks for thousands of
        // samples, so it is built once per grid point and interpolated between. The effective width varies
        // smoothly and monotonically with airmass (it is an integral of 10^(-0.4 k(lambda) X)), so linear
        // interpolation on a 0.1 grid is accurate to well under a tenth of a percent, far inside the 0.03 mag
        // scatter of the Gaia photometric transformations feeding the catalogue in the first place.
        private const double AirmassGridStep = 0.1;

        private static readonly Dictionary<PhotometricDetector, SystemResponse[]> responseGrids
            = new Dictionary<PhotometricDetector, SystemResponse[]>();
        private static readonly object cacheLock = new object();

        // The system response for this detector at (or bracketing) the given airmass. Returns the grid point at
        // or below the requested airmass rather than interpolating two SystemResponse objects, which cannot be
        // blended: what interpolates cleanly is the scalar effective width they produce, and at a 0.1 grid step
        // the difference between the two is smaller than the quantities either one is built from. A space
        // instrument needs one response only, since it looks through no air.
        private static SystemResponse ResponseFor(PhotometricDetector detector, InstrumentSpec instrument, double airmass)
        {
            int index = instrument.IsSpaceBased
                ? 0
                : (int)Math.Round((ClampAirmass(airmass) - 1.0) / AirmassGridStep);
            if (index < 0) index = 0;

            lock (cacheLock)
            {
                SystemResponse[] grid;
                if (!responseGrids.TryGetValue(detector, out grid))
                {
                    int count = (int)Math.Round((MaxSeeingAirmass - 1.0) / AirmassGridStep) + 1;
                    grid = new SystemResponse[count];
                    responseGrids[detector] = grid;
                }
                if (index >= grid.Length) index = grid.Length - 1;

                if (grid[index] == null)
                {
                    double gridAirmass = 1.0 + index * AirmassGridStep;
                    grid[index] = new SystemResponse(
                        detector.FilterCentralWavelengthNm.Value * 1e-9,
                        detector.FilterWidthNm.Value * 10.0,          // nm -> Angstrom
                        detector.OpticsTransmission.Value,
                        detector.FilterTransmissionCurve,
                        detector.QuantumEfficiencyCurve,
                        detector.QuantumEfficiency ?? 0.0,
                        gridAirmass,
                        instrument.SiteAltitudeMeters);
                }
                return grid[index];
            }
        }
    }
}
