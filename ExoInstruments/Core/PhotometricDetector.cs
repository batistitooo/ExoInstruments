using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The real optics-and-detector description one photometric instrument needs before the CCD
    /// equation can be applied to it (see CcdEquation, TransitPhotometry).
    ///
    /// WHY EVERY FIELD IS NULLABLE, AND WHY THE BLOCK IS ALL-OR-NOTHING.
    ///
    /// The empirical scaling this replaces needs two numbers per instrument and can be written
    /// down from a survey's headline precision figure. The CCD equation needs eleven, and each one
    /// is a measured property of a specific piece of hardware. A missing entry cannot be filled in
    /// with something plausible: an invented throughput or read noise does not degrade the result
    /// gracefully, it produces a precision that looks physical, carries units, and is wrong by an
    /// unknown factor -- which is strictly worse than the honest empirical relation, because it
    /// cannot be recognised as an estimate.
    ///
    /// So a field left null means UNSOURCED, not zero. IsComplete is false while any required
    /// field is unsourced, TransitPhotometry then declines to run, and LightCurveSimulator falls
    /// back to InstrumentSpec.EstimatePrecision unchanged. MissingFields() names exactly what is
    /// absent, so filling a block in is a matter of looking up a listed quantity rather than
    /// discovering which one was wrong.
    ///
    /// Every populated field must carry its source in Citation. This is the same rule
    /// VisualTelescopeCatalog already follows for the imaging instruments.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public sealed class PhotometricDetector
    {
        // --- Collecting optics ----------------------------------------------

        /// <summary>Primary aperture diameter, metres.</summary>
        public double? ApertureMeters { get; set; }

        /// <summary>
        /// Central obstruction as a diameter ratio (secondary / primary). Legitimately 0 for a
        /// refractor or a camera lens, which is exactly why it is nullable: 0 is a real sourced
        /// value here, and null means the figure has not been looked up.
        /// </summary>
        public double? CentralObstructionFraction { get; set; }

        /// <summary>
        /// Throughput of everything between the sky and the detector that has no published
        /// wavelength dependence: mirror coatings, correctors, relay optics and the filter's own
        /// peak transmission, pre-multiplied. Same convention as
        /// SystemResponse's greyOpticsTransmission and VisualTelescopeSpec.OpticsTransmission.
        /// </summary>
        public double? OpticsTransmission { get; set; }

        // --- Detector -------------------------------------------------------

        /// <summary>Plate scale, arcsec per pixel, at the binning the instrument actually observes in.</summary>
        public double? PlateScaleArcsecPerPixel { get; set; }

        /// <summary>
        /// Open-shutter time of ONE integration, seconds. Optional: null falls back to the
        /// instrument's CadenceSeconds.
        ///
        /// The distinction is not pedantry, and reading the survey papers is what forced it.
        /// InstrumentSpec.CadenceSeconds is how often a light-curve POINT is produced, which for
        /// SuperWASP is about 600 s per field revisit while the shutter is only open for 30 s of
        /// it (Pollacco et al. 2006). Feeding the cadence to the CCD equation as if it were
        /// exposure time would credit that instrument with twenty times the photons it collects.
        /// </summary>
        public double? ExposureSeconds { get; set; }

        /// <summary>
        /// How many separate integrations are co-added into one light-curve point. Optional:
        /// null or 1 means the point is a single read.
        ///
        /// This exists because TESS's 2-minute data is a stack of sixty 2-second images (Ricker
        /// et al. 2015; Sullivan et al. 2015), and read noise is incurred on EVERY one of them.
        /// Signal, sky and dark accumulate over the total open-shutter time, but read-noise
        /// VARIANCE adds per read, so the effective read noise entering the CCD equation is
        /// N_R * sqrt(n) rather than N_R. Treating a 60-read stack as one long exposure would
        /// understate its read noise by a factor of 7.7.
        /// </summary>
        public int? IntegrationsPerMeasurement { get; set; }

        /// <summary>
        /// Photometric aperture radius in PIXELS, when the survey publishes the one it actually
        /// uses. Optional: null derives the radius from the PSF instead, at
        /// CcdEquation.OptimalApertureRadiusInFwhm.
        ///
        /// Needed for any badly undersampled instrument, where the seeing-derived optimum is
        /// meaningless: SuperWASP's plate scale is 13.7 arcsec/pixel against sub-arcsecond seeing,
        /// so its aperture is set by the pixel grid and its own reduction choices, not by the
        /// atmosphere.
        /// </summary>
        public double? PhotometricApertureRadiusPixels { get; set; }

        /// <summary>
        /// Detector quantum efficiency across the observing band. A scalar, which is the honest
        /// treatment when only a headline figure is published; SystemResponse accepts a measured
        /// SpectralCurve instead where one exists (see FilterCurves for how the imaging side does
        /// it), and QuantumEfficiencyCurve below carries it when it does.
        /// </summary>
        public double? QuantumEfficiency { get; set; }

        /// <summary>
        /// Measured QE curve, if one has been digitised for this detector. When present it
        /// supersedes the scalar above and the band is integrated against the real curve.
        /// Optional: null simply means the scalar is used, and is not an unsourced field.
        /// </summary>
        public SpectralCurve QuantumEfficiencyCurve { get; set; }

        /// <summary>Read noise, electrons per pixel per readout.</summary>
        public double? ReadNoiseElectrons { get; set; }

        /// <summary>Dark current, electrons per pixel per second, at the instrument's real operating temperature.</summary>
        public double? DarkCurrentElectronsPerSecond { get; set; }

        /// <summary>
        /// Conversion gain, electrons per ADU. Genuinely OPTIONAL rather than unsourced-if-null:
        /// it enters only the digitisation term of the CCD equation, which is G^2/12 electrons^2
        /// and provably negligible against any scientific detector's read noise (see
        /// CcdEquation.SignalToNoise). Null omits that term.
        /// </summary>
        public double? GainElectronsPerAdu { get; set; }

        // --- Observing band -------------------------------------------------

        /// <summary>Central wavelength of the observing filter, nanometres.</summary>
        public double? FilterCentralWavelengthNm { get; set; }

        /// <summary>Filter FWHM, nanometres.</summary>
        public double? FilterWidthNm { get; set; }

        /// <summary>
        /// Measured filter transmission curve, if one has been digitised. Supersedes the top-hat
        /// implied by the two figures above, exactly as on the imaging side. Optional.
        /// </summary>
        public SpectralCurve FilterTransmissionCurve { get; set; }

        // --- Site -----------------------------------------------------------

        /// <summary>
        /// Median zenith seeing FWHM at the site, arcsec, referred to 500 nm as every published
        /// seeing figure is. Required for a ground instrument, and must be left null for a
        /// space-based one, where the delivered image quality comes from the optics instead.
        /// </summary>
        public double? MedianZenithSeeingArcsec { get; set; }

        /// <summary>
        /// Delivered PSF FWHM in arcsec for a SPACE-BASED instrument, where there is no seeing and
        /// the figure is a property of the optics. Required for space, null for ground.
        /// </summary>
        public double? DeliveredPsfFwhmArcsec { get; set; }

        // --- Provenance -----------------------------------------------------

        /// <summary>Where every populated number above comes from. Required: an uncited block is not a sourced one.</summary>
        public string Citation { get; set; }

        /// <summary>
        /// Names every required field that has not been sourced, for the given platform. Empty
        /// when the block is usable.
        /// </summary>
        public List<string> MissingFields(bool isSpaceBased)
        {
            var missing = new List<string>();

            RequirePositive(missing, ApertureMeters, nameof(ApertureMeters));
            RequireNonNegativeFraction(missing, CentralObstructionFraction, nameof(CentralObstructionFraction));
            RequirePositive(missing, OpticsTransmission, nameof(OpticsTransmission));
            RequirePositive(missing, PlateScaleArcsecPerPixel, nameof(PlateScaleArcsecPerPixel));
            RequirePositive(missing, FilterCentralWavelengthNm, nameof(FilterCentralWavelengthNm));
            RequirePositive(missing, FilterWidthNm, nameof(FilterWidthNm));
            RequirePositive(missing, ReadNoiseElectrons, nameof(ReadNoiseElectrons));

            // Dark current may legitimately be sourced AS zero (a cold space detector), so only
            // null -- never 0 -- counts as unsourced here.
            if (!DarkCurrentElectronsPerSecond.HasValue || DarkCurrentElectronsPerSecond.Value < 0.0)
                missing.Add(nameof(DarkCurrentElectronsPerSecond));

            // The curve supersedes the scalar, so only one of the two is required.
            if (QuantumEfficiencyCurve == null)
                RequirePositive(missing, QuantumEfficiency, nameof(QuantumEfficiency));

            if (isSpaceBased)
                RequirePositive(missing, DeliveredPsfFwhmArcsec, nameof(DeliveredPsfFwhmArcsec));
            else
                RequirePositive(missing, MedianZenithSeeingArcsec, nameof(MedianZenithSeeingArcsec));

            if (string.IsNullOrEmpty(Citation)) missing.Add(nameof(Citation));

            return missing;
        }

        /// <summary>True when every required field is sourced and the CCD equation can be applied.</summary>
        public bool IsComplete(bool isSpaceBased) => MissingFields(isSpaceBased).Count == 0;

        /// <summary>Effective light-collecting area in cm^2: full aperture less the central obstruction.</summary>
        public double EffectiveApertureAreaCm2()
        {
            if (!ApertureMeters.HasValue || !CentralObstructionFraction.HasValue) return 0.0;
            double radiusCm = ApertureMeters.Value * 100.0 * 0.5;
            double eps = Math.Max(0.0, Math.Min(0.95, CentralObstructionFraction.Value));
            return Math.PI * radiusCm * radiusCm * (1.0 - eps * eps);
        }

        private static void RequirePositive(List<string> missing, double? value, string name)
        {
            if (!value.HasValue || !(value.Value > 0.0)) missing.Add(name);
        }

        private static void RequireNonNegativeFraction(List<string> missing, double? value, string name)
        {
            if (!value.HasValue || value.Value < 0.0 || value.Value >= 1.0) missing.Add(name);
        }
    }
}
