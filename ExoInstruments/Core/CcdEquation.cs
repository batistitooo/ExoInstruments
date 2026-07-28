using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The CCD equation: the signal-to-noise ratio of an aperture-photometry measurement, derived
    /// from the real electron budget of the observation.
    ///
    /// WHAT THIS REPLACES. Photometric precision used to be a one-parameter empirical scaling
    /// (InstrumentSpec.EstimatePrecision):
    ///
    ///     sigma(m) = sigma_ref * 10^(k * (m - m_ref))
    ///
    /// with k = 0.2 for every instrument in the roster. That relation is the pure photon-noise
    /// limit and nothing else: 10^(0.2 dm) is exactly 1/sqrt(flux ratio), so it asserts that every
    /// instrument is source-shot-noise-limited at every magnitude. Three consequences, all of
    /// which a real light curve shows and this one could not:
    ///
    ///   * There is no sky. A faint star's noise is dominated by the background it sits on, not by
    ///     its own photons, and that background is what makes moonlight, twilight and a poor site
    ///     matter. The single exponent cannot bend toward the sky-limited sigma ~ 10^(0.4 dm)
    ///     slope, so faint-end precision is optimistic by whatever the sky contributes.
    ///   * There is no read noise or dark current, so short exposures and long ones behave the
    ///     same way per unit time, and the read-noise-limited regime that ends every real
    ///     high-cadence run simply does not exist.
    ///   * Nothing depends on the aperture, the plate scale, the throughput or the detector, so
    ///     two instruments with the same sigma_ref are indistinguishable however different their
    ///     hardware is. The imaging half of this codebase already models all of that in real
    ///     electrons (SystemBandpass, SkyBrightnessModel, SolarSystemCameraTexture); the transit
    ///     half did not, and the two halves disagreed about what an instrument is.
    ///
    /// WHAT IT DOES INSTEAD. The CCD equation as published by Merline &amp; Howell (1995,
    /// Experimental Astronomy 6, 163, "A realistic model for point-sources imaged on array
    /// detectors: The model and initial results"), and reproduced as equation 4.9 of Howell
    /// (2006, "Handbook of CCD Astronomy", 2nd ed., CUP):
    ///
    ///     S/N = N_star / sqrt( N_star + n_pix * (1 + n_pix/n_B) * (N_S + N_D + N_R^2 + G^2 sigma_f^2) )
    ///
    ///   N_star  total electrons from the source inside the photometric aperture
    ///   n_pix   pixels in that aperture
    ///   n_B     pixels in the background region the sky level is estimated from
    ///   N_S     sky electrons per pixel over the exposure
    ///   N_D     dark electrons per pixel over the exposure
    ///   N_R     read noise, electrons per pixel per readout
    ///   G       gain, electrons per ADU; sigma_f the A/D converter's own quantisation error
    ///
    /// Each term is a variance in electrons, added because the processes are independent. The
    /// numerator is the signal; the (1 + n_pix/n_B) factor is Merline &amp; Howell's, and accounts
    /// for the fact that the sky level is not known but MEASURED, from a finite number of
    /// background pixels, so its own uncertainty propagates into every aperture pixel. It tends
    /// to 1 as the background region grows, which is the limit most textbook statements of the
    /// equation quote.
    ///
    /// The fractional uncertainty on the measured flux -- which is what a light curve point's
    /// error bar is -- is exactly the reciprocal, sigma_F/F = 1/(S/N).
    ///
    /// WHAT THIS FILE DOES NOT MODEL, deliberately, because each is either a separate published
    /// model or has no defensible value here:
    ///
    ///   * Scintillation. It is not a detector term and does not belong in this equation; it is
    ///     added in quadrature by the caller from Young (1967), already implemented in
    ///     AtmosphericNoise.
    ///   * Flat-field / PRNU residuals, which for a bright target on the ground are the dominant
    ///     systematic and are simply absent from this codebase (there is no flat-field model at
    ///     all). Precision on bright stars is therefore still optimistic, now for one identifiable
    ///     reason rather than for three.
    ///   * Detector non-linearity approaching full well, and any correlated ("red") noise.
    ///
    /// Pure C# with no Unity dependency and no instrument knowledge: everything is passed in, so
    /// this file can be exercised against the published equation on its own.
    /// </summary>
    public static class CcdEquation
    {
        /// <summary>
        /// Photometric aperture radius that maximises signal-to-noise, in units of the PSF's
        /// FWHM: 0.68 (Howell 1989, PASP 101, 616, "Two-dimensional aperture photometry:
        /// signal-to-noise ratio of point-source observations and optimal data-extraction
        /// techniques").
        ///
        /// This is the optimum for a source whose noise is dominated by the background, which is
        /// the regime the optimum is worth having in: a larger aperture then admits sky faster
        /// than it admits signal. For a bright, source-limited star the true optimum is wider,
        /// tending to "all of the flux" as the background vanishes, so 0.68 is conservative there
        /// rather than wrong -- it discards 28% of the source photons that a wider aperture would
        /// keep. Exposed as a parameter for that reason.
        /// </summary>
        public const double OptimalApertureRadiusInFwhm = 0.68;

        /// <summary>
        /// Sky annulus geometry, as multiples of the photometric aperture radius. Inner 2x, outer
        /// 3x is the ordinary choice in aperture photometry: far enough out that the source's own
        /// wings are negligible, wide enough to measure the sky well.
        ///
        /// These are a DATA-REDUCTION convention, not physical constants, and they enter only
        /// through Merline &amp; Howell's (1 + n_pix/n_B) factor. The derived area ratio is
        /// (3^2 - 2^2) = 5, so n_B = 5 n_pix and the factor is 1.2 -- a 20% inflation of the
        /// background variance, from the sky level being estimated rather than known.
        /// </summary>
        public const double SkyAnnulusInnerRadiusInAperture = 2.0;
        public const double SkyAnnulusOuterRadiusInAperture = 3.0;

        /// <summary>Ratio of background-region area to aperture area implied by the annulus above.</summary>
        public static double BackgroundToApertureAreaRatio =>
            SkyAnnulusOuterRadiusInAperture * SkyAnnulusOuterRadiusInAperture
          - SkyAnnulusInnerRadiusInAperture * SkyAnnulusInnerRadiusInAperture;

        /// <summary>FWHM of a Gaussian in units of its standard deviation: 2*sqrt(2*ln 2).</summary>
        public const double FwhmPerSigma = 2.3548200450309493;

        /// <summary>
        /// Fraction of a point source's total flux falling inside a circular aperture of the given
        /// radius, expressed in FWHM, for a circular Gaussian PSF:
        ///
        ///     E(r) = 1 - exp( -r^2 / (2 sigma^2) ),   sigma = FWHM / (2 sqrt(2 ln 2))
        ///
        /// which is the standard result for a two-dimensional Gaussian and is what every published
        /// exposure-time calculator uses for the encircled-energy factor.
        ///
        /// THE GAUSSIAN IS AN ASSUMPTION, and the only one in this file. It is a good one in the
        /// regime it is applied to here -- a long exposure through the atmosphere, where the PSF
        /// is seeing-dominated and the diffraction core is far inside it -- and a poor one in the
        /// wings, since the real long-exposure Kolmogorov profile falls as theta^(-11/3) and so
        /// carries more flux outside any given radius than a Gaussian does. At the default 0.68
        /// FWHM aperture this returns 0.7226, and the true Kolmogorov figure is somewhat lower.
        ///
        /// The exact number is computable from what this codebase already has: OpticalPsf builds
        /// the real annular-pupil-convolved-with-Kolmogorov kernel, and its encircled energy could
        /// be integrated directly. That is left as a refinement rather than done here, so that
        /// this file stays a statement of the published equation alone.
        /// </summary>
        public static double GaussianEnclosedEnergy(double apertureRadiusInFwhm)
        {
            if (!(apertureRadiusInFwhm > 0.0)) return 0.0;
            double radiusInSigma = apertureRadiusInFwhm * FwhmPerSigma;
            return 1.0 - Math.Exp(-0.5 * radiusInSigma * radiusInSigma);
        }

        /// <summary>
        /// Pixels enclosed by a circular aperture of the given angular radius at the given plate
        /// scale. Floored at 1: an aperture smaller than a pixel still reads one pixel out.
        /// </summary>
        public static double AperturePixels(double apertureRadiusArcsec, double plateScaleArcsecPerPixel)
        {
            if (!(apertureRadiusArcsec > 0.0) || !(plateScaleArcsecPerPixel > 0.0)) return 0.0;
            double radiusPx = apertureRadiusArcsec / plateScaleArcsecPerPixel;
            return Math.Max(1.0, Math.PI * radiusPx * radiusPx);
        }

        /// <summary>
        /// Signal-to-noise ratio of the aperture measurement -- the equation itself, with every
        /// quantity in electrons.
        ///
        /// gainElectronsPerAdu may be null, which drops the G^2 sigma_f^2 digitisation term. That
        /// omission is not an approximation of unknown size: for an ideal converter the
        /// quantisation error is uniform over one ADU, so sigma_f^2 = 1/12 ADU^2 and the term is
        /// G^2/12 electrons^2. Against a read noise of 6 e- (36 e-^2) a gain of 1.5 e-/ADU
        /// contributes 0.19 e-^2, half a percent of it. The term only matters for a converter
        /// coarse enough that G is comparable to N_R, which no scientific CCD is.
        ///
        /// Returns 0 when there is no signal or the noise is not computable, which the caller
        /// must treat as "no measurement" rather than as infinite precision.
        /// </summary>
        public static double SignalToNoise(
            double starElectronsInAperture,
            double aperturePixels,
            double backgroundPixels,
            double skyElectronsPerPixel,
            double darkElectronsPerPixel,
            double readNoiseElectrons,
            double? gainElectronsPerAdu)
        {
            if (!(starElectronsInAperture > 0.0) || !(aperturePixels > 0.0)) return 0.0;

            // Per-pixel variance of everything that is not the source: sky shot noise, dark shot
            // noise, the amplifier's read noise, and the converter's quantisation.
            double perPixelVariance =
                  Math.Max(0.0, skyElectronsPerPixel)
                + Math.Max(0.0, darkElectronsPerPixel)
                + readNoiseElectrons * readNoiseElectrons;

            if (gainElectronsPerAdu.HasValue && gainElectronsPerAdu.Value > 0.0)
            {
                double g = gainElectronsPerAdu.Value;
                perPixelVariance += g * g / 12.0;
            }

            // Merline & Howell's factor for the sky level being measured from n_B pixels rather
            // than known exactly. backgroundPixels <= 0 means "sky known exactly", i.e. factor 1.
            double backgroundEstimationFactor = backgroundPixels > 0.0
                ? 1.0 + aperturePixels / backgroundPixels
                : 1.0;

            double variance = starElectronsInAperture
                            + aperturePixels * backgroundEstimationFactor * perPixelVariance;
            if (!(variance > 0.0)) return 0.0;

            return starElectronsInAperture / Math.Sqrt(variance);
        }

        /// <summary>
        /// The same measurement expressed as the fractional flux uncertainty a light-curve point
        /// carries, sigma_F/F = 1/(S/N). Returns +Infinity when there is no usable measurement,
        /// so a caller that sums in quadrature is not silently handed a zero.
        /// </summary>
        public static double RelativeFluxSigma(
            double starElectronsInAperture,
            double aperturePixels,
            double backgroundPixels,
            double skyElectronsPerPixel,
            double darkElectronsPerPixel,
            double readNoiseElectrons,
            double? gainElectronsPerAdu)
        {
            double snr = SignalToNoise(starElectronsInAperture, aperturePixels, backgroundPixels,
                                       skyElectronsPerPixel, darkElectronsPerPixel,
                                       readNoiseElectrons, gainElectronsPerAdu);
            return snr > 0.0 ? 1.0 / snr : double.PositiveInfinity;
        }
    }
}
