namespace ExoInstruments.Core
{
    /// <summary>
    /// Static feasibility of imaging one target with one instrument -- everything
    /// that doesn't depend on integration time. Computed once at observation start
    /// (or for the pre-observation info card) by DirectImagingSimulator.Assess.
    /// </summary>
    public class DirectImagingAssessment
    {
        /// <summary>False when the catalog lacks a field the physics needs; see MissingDataReason.</summary>
        public bool HasRequiredData { get; set; }
        public string MissingDataReason { get; set; }

        /// <summary>False for retracted entries: the campaign runs, but no planet exists to image -- same teaching case as the transit path.</summary>
        public bool SignalPresent { get; set; }

        public double SeparationArcsec { get; set; }
        public double DiffractionLimitArcsec { get; set; }
        public bool Resolvable { get; set; }               // separation > diffraction limit

        public double ContrastRatio { get; set; }           // planet/star flux ratio at 1.6 um
        public double PlanetTempKUsed { get; set; }
        public bool PlanetTempFromCatalog { get; set; }      // false = fell back to irradiation equilibrium estimate

        /// <summary>5-sigma contrast floor at the planet's separation after 1 hour, for this star's magnitude.</summary>
        public double SpeckleFloor5Sigma1Hr { get; set; }

        /// <summary>Base floor at 1 lambda/D after 1 hour (magnitude-scaled) -- the texture renderer scales it across the field.</summary>
        public double BaseFloor5Sigma1Hr { get; set; }
    }

    /// <summary>Outcome of an imaging campaign: the assessment plus what the accumulated exposure achieved.</summary>
    public class DirectImagingResult
    {
        public DirectImagingAssessment Assessment { get; set; }
        /// <summary>Effective on-sky integration (zenith-equivalent, airmass-weighted) -- see ImagingObservationSession.</summary>
        public double ExposureSeconds { get; set; }
        public double Snr { get; set; }
        public bool Detected { get; set; }
    }
}
