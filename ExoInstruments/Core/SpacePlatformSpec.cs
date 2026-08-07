using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Everything about an instrument that only exists because it is in orbit rather than on a
    /// mountain: what it may point at, how steadily it holds that pointing, what it costs to run,
    /// and how much data it produces.
    ///
    /// WHY IT IS A SEPARATE OBJECT AND NOT MORE FIELDS ON VisualTelescopeSpec. A ground telescope
    /// has none of these properties, not "zero" of them: it has no solar avoidance angle, no
    /// aperture door, no attitude control system and no downlink, and giving it a zeroed-out one
    /// of each would say something false about it. VisualTelescopeSpec.SpacePlatform is null for
    /// every ground instrument in the roster, and null is what the pipeline branches on. It is
    /// the same reason AdaptiveOpticsFwhmArcsec is zero rather than 1e9 for a seeing-limited
    /// telescope: absence and a very large value are not the same claim.
    ///
    /// EVERY ANGLE HERE IS AN OPERATIONS CONSTRAINT, NOT AN IMAGE-QUALITY PREFERENCE. Real
    /// observatories publish these as hard limits in their proposal instructions because
    /// violating them can damage the instrument or the spacecraft, not merely spoil a frame.
    /// They are treated the same way here: an observation inside an avoidance angle does not
    /// produce a bad photograph, it does not happen.
    /// </summary>
    public sealed class SpacePlatformSpec
    {
        /// <summary>The spacecraft this instrument flies on, for the FITS TELESCOP keyword and the UI.</summary>
        public string PlatformName;

        // --- Pointing constraints --------------------------------------------------------

        /// <summary>
        /// Closest the boresight may come to the Sun, degrees.
        ///
        /// HST's is 50 degrees historically and 62.5 degrees under the reduced-gyro operations
        /// current since 2024; the HST Primer (Pointing, Orientation, and Roll Constraints)
        /// states "The target-to-sun angle at the time of observation must be greater than
        /// 62.5 degrees." The tighter, current figure is used, and the WFC3 handbook's zodiacal
        /// table marks its own solar-avoidance cells at 50 degrees, which is why the sky model
        /// has published values right up to this limit and no further.
        /// </summary>
        public double SunAvoidanceAngleDeg;

        /// <summary>
        /// Closest the boresight may come to the SUNLIT limb of the host body, degrees.
        ///
        /// This is the constraint that actually shapes an orbit's observing window, and it is
        /// set by scattered light rather than by safety: see Earthshine for the measured
        /// exponential rise inside it. STScI's standard bright Earth avoidance for HST is 20
        /// degrees, with a non-standard 25 degrees available at a cost in observing time (WFC3
        /// Instrument Handbook Sect. 7.9.5).
        /// </summary>
        public double BrightLimbAvoidanceAngleDeg;

        /// <summary>
        /// Closest the boresight may come to the DARK limb of the host body, degrees. Much
        /// smaller than the bright one, because the constraint there is geometry and stray
        /// starlight rather than scattered sunlight; SRW98 measure the dark-limb background as
        /// flat at the Earth-shadow level all the way in.
        /// </summary>
        public double DarkLimbAvoidanceAngleDeg;

        /// <summary>Closest the boresight may come to a natural satellite of the host body, degrees.</summary>
        public double MoonAvoidanceAngleDeg;

        // --- Pointing performance --------------------------------------------------------

        /// <summary>
        /// The pointing stability floor this instrument achieves with its attitude control
        /// working, arcsec rms.
        ///
        /// HST: "This pointing control method was designed to keep telescope jitter below 0.007
        /// arcsec rms, but the current performance has jitter of 0.008 arcsec rms" (HST Primer,
        /// Optical Performance, Guiding Performance, and Observing Efficiency). The ACHIEVED
        /// figure is used rather than the design one; a mod claiming the design number would be
        /// claiming a performance the telescope has not had since its gyros aged.
        /// </summary>
        public double PointingJitterArcsecRms;

        /// <summary>
        /// Attitude deadband the spacecraft's own controller holds to when it has nothing but
        /// on-off thrusters, arcsec. Feeds PointingStability's limit-cycle model.
        ///
        /// This is a property of the CONTROLLER, not of the telescope, and no observatory
        /// publishes one because no observatory points a telescope this way. The value carried
        /// here is the mod's own attitude-hold deadband, chosen so that the limit cycle is
        /// visible at the instrument's own plate scale rather than invisible or catastrophic;
        /// see ModuleExoSpaceTelescope, which is what actually holds to it. It is declared as a
        /// design value, not sourced, because there is nothing to source it to.
        /// </summary>
        public double ThrusterDeadbandArcsec = 30.0;

        /// <summary>
        /// Shortest attitude-control pulse the mod's controller commands, seconds. Sets the rate
        /// increment per pulse and so the limit-cycle period (see PointingStability). Same
        /// status as the deadband above: a design value of this implementation.
        /// </summary>
        public double MinimumControlPulseSeconds = 0.05;

        // --- Optics that are only knowable for a real flown telescope --------------------

        /// <summary>
        /// The instrument's own published DELIVERED point-spread-function width against
        /// wavelength, arcsec FWHM, before pixelation.
        ///
        /// WHY A TABLE AND NOT A CALCULATION. A perfect 2.4 m aperture at 500 nm gives a 0.044
        /// arcsec core. HST delivers 0.067. The difference is the primary's residual polishing
        /// figure, the mid-frequency zonal errors the WFC3 handbook names directly, and there is
        /// no way to compute it from an aperture and an obstruction: it is a property of one
        /// individual mirror that was measured after it flew. So it is carried as the measured
        /// table it is, and OpticalPsf.GaussianFwhmForDelivered inverts it into the broadening
        /// the kernel needs, exactly as the AO instruments' published widths are already
        /// inverted.
        ///
        /// Null means no such table is published, in which case the instrument is treated as
        /// diffraction-limited, which is a claim about the optics that should only be made where
        /// it is true.
        /// </summary>
        public SpectralCurve DeliveredPsfFwhmArcsec;

        // --- Aperture door ----------------------------------------------------------------

        /// <summary>
        /// True for a telescope with an aperture door that must be opened before it can observe
        /// and that is closed to protect the optics.
        ///
        /// HST has one, and its purpose is not dust: the door is the bright-object protection of
        /// last resort, closing to shield the optics if the spacecraft ever loses attitude
        /// control and risks pointing at the Sun. That is why it is modelled as a hard gate on
        /// observing rather than as a cosmetic animation.
        /// </summary>
        public bool HasApertureDoor = true;

        // --- Slewing -----------------------------------------------------------------------

        /// <summary>
        /// Fastest this spacecraft is allowed to rotate, degrees per second.
        ///
        /// A published ceiling, not a derived one: a real observatory's rate limit is set by its
        /// rate gyroscopes' measuring range and the guidance system's ability to track its own
        /// attitude while moving, not by how hard its wheels can push. Zero leaves the manoeuvre
        /// torque-limited throughout. Transplanted through SlewDynamics.UniverseTimeScale.
        /// </summary>
        public double MaxSlewRateDegPerSecond;

        /// <summary>
        /// Time between arriving at an attitude and being able to expose through it: guide-star
        /// acquisition and settling. Separate from the manoeuvre because it does not scale with the
        /// angle, which is why real programmes group targets that sit near each other.
        /// </summary>
        public double GuideStarAcquisitionSeconds;

        // --- Consumables -------------------------------------------------------------------

        /// <summary>
        /// Electric charge the instrument draws while an exposure is running, units per second,
        /// in KSP's own resource units.
        ///
        /// KSP's ElectricCharge is not a physical unit and has no conversion to watts anywhere
        /// in the game, so this cannot be sourced to a real spacecraft's power budget: HST's
        /// real load is in watts and there is nothing to convert it to. It is a game-balance
        /// number, and it is labelled as one here rather than dressed up with a citation.
        /// </summary>
        public double ExposureElectricChargePerSecond = 2.0;

        /// <summary>Electric charge drawn while merely powered and holding attitude, units per second.</summary>
        public double IdleElectricChargePerSecond = 0.35;

        // --- Data --------------------------------------------------------------------------

        /// <summary>
        /// Bits per pixel this instrument's readout produces on the downlink, which is its ADC
        /// depth: the frame goes down as the converter produced it, and any compression would
        /// have to be sourced to a real onboard codec.
        /// </summary>
        public int DownlinkBitsPerPixel = 16;

        /// <summary>
        /// Number of detector pixels one full-frame readout produces. Separate from the imaging
        /// sensor size in VisualTelescopeSpec because a real instrument's frame can be a mosaic
        /// of detectors read out together: WFC3/UVIS is two 2051x4096 CCDs, and both come down.
        /// </summary>
        public long FullFramePixels;

        /// <summary>Bits one full frame occupies on the downlink.</summary>
        public double FullFrameBits => Math.Max(0, FullFramePixels) * Math.Max(1, DownlinkBitsPerPixel);
    }
}
