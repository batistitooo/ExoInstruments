using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// SPHERE's visual coronagraph: what it hides, and what it leaves behind.
    ///
    /// WHY AN INSTRUMENT WITHOUT ONE IS NOT SPHERE. Until this file existed the roster's
    /// extreme-AO instrument was modelled as a telescope with a very good Strehl ratio: a narrow
    /// core on a wide halo, sourced correctly from Schmid et al. (2018) but describing an
    /// instrument nobody built. SPHERE exists to image things a hundred thousand times fainter than
    /// the star beside them, and it does that with two components acting in series, neither of
    /// which is a Strehl ratio:
    ///
    ///   1. A FOCAL-PLANE MASK that blocks the stellar core. On its own this achieves little; the
    ///      light removed from the core reappears as diffraction elsewhere.
    ///   2. A LYOT PUPIL STOP downstream, which is where the suppression actually happens. The
    ///      mask converts the star's light into a bright ring around the pupil edge and around the
    ///      central obstruction, and the stop is what throws that ring away.
    ///
    /// THIS FILE IS A LOOKUP, NOT A MODEL, and that is deliberate. ESO measured both stages on the
    /// real instrument and published the numbers (Schmid et al. 2018, A&A 619, A9, Tables 8 and 9),
    /// so the attenuations below are ratios of THEIR measured counts rather than a diffraction
    /// calculation of ours. A first-principles Lyot propagation would be a second opinion about an
    /// instrument that has already been measured, and where it disagreed it would be wrong.
    ///
    /// WHAT REMAINS AFTER BOTH STAGES is the thing high-contrast imaging is actually limited by,
    /// and it is not diffraction and not photon noise: it is the speckle field. See
    /// Core.SpeckleField, which this file hands off to.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class Coronagraph
    {
        /// <summary>
        /// One focal-plane mask of SPHERE's visual coronagraph.
        ///
        /// The attenuations are ratios of ESO's own measured peak counts (Schmid et al. 2018,
        /// Table 8), normalised the way that table is: a non-coronagraphic PSF scaled to 10^6
        /// counts in a 3 arcsec aperture, against which each mask's coronagraphic image reports its
        /// own peak. The clear stop NC_WF reads 7983 counts in R_PRIM and 7813 in I_PRIM, so a
        /// mask reading 72 and 52 has attenuated the peak by 110.9 and 150.2 respectively.
        ///
        /// That per-filter difference is real and worth keeping rather than averaging away: a Lyot
        /// coronagraph of fixed physical radius subtends more resolution elements at a shorter
        /// wavelength, so the same mask suppresses better in the red than in the visible. The
        /// paper's own prose quotes the small mask as "R_coro = 110 - 150", which is exactly the
        /// span between these two filters and is how it is reproduced here.
        /// </summary>
        public struct Mask
        {
            /// <summary>ESO's own name for the mask, written to the FITS header.</summary>
            public string Name;

            /// <summary>Radius of the opaque Lyot spot in milliarcseconds.</summary>
            public double RadiusMas;

            /// <summary>Peak attenuation measured in ZIMPOL's R_PRIM filter (Schmid et al. 2018, Table 8).</summary>
            public double PeakAttenuationRPrim;

            /// <summary>Peak attenuation measured in ZIMPOL's I_PRIM filter.</summary>
            public double PeakAttenuationIPrim;

            /// <summary>
            /// Transmission of the Lyot spot itself. Zero for an opaque mask; the astrometric mask
            /// CLC-MT-WF is deliberately not opaque, at "a transmission of about 0.1%, so that the
            /// central star can be seen in the science image as a faint emission peak inside the
            /// spot shadow", which is what makes it the mask to use when the star's position has to
            /// be measured rather than merely hidden.
            /// </summary>
            public double SpotTransmission;

            /// <summary>
            /// Whether the mask is suspended on wires rather than deposited on a plate, and the
            /// consequence either way. ESO: the small masks are "a metallic coating deposited on
            /// transparent plates with the disadvantage that dust particles on the plate are
            /// visible in the recorded images", while the larger ones are suspended, where "dust
            /// are no problem ... but the suspension spiders, which have a full width of 40 um =
            /// 34 mas, can be an important issue for the observing strategy and the data
            /// reduction". Carried as a fact about the mask; neither the dust nor the wires is
            /// rendered, because the dust pattern is a property of one particular October and the
            /// wire position angle is not published.
            /// </summary>
            public bool SuspendedOnWires;
        }

        /// <summary>Full width of a suspension wire on the larger masks, in milliarcseconds (Schmid et al. 2018: "40 um = 34 mas").</summary>
        public const double SuspensionWireWidthMas = 34.0;

        /// <summary>
        /// The classical Lyot coronagraphs of SPHERE's visual channel, in the wide-field setting
        /// ZIMPOL's imaging modes use.
        ///
        /// CLC-M-WF and CLC-MT-WF share a radius and differ in everything that matters
        /// operationally: the T is the astrometric variant, whose partly transmitting spot lets the
        /// star be located and which carries no suspension wires. The four-quadrant phase masks
        /// (4QPM1 at 666 nm, 4QPM2 at 823 nm) are a different family entirely, achromatic over only
        /// a narrow band, and are not modelled: ESO publishes their design wavelengths but no
        /// attenuation curve, and a 4QPM's attenuation away from its design wavelength is the whole
        /// of its behaviour.
        /// </summary>
        public static readonly Mask[] VisualMasks =
        {
            new Mask { Name = "CLC-S-WF",  RadiusMas = 46.5,
                       PeakAttenuationRPrim = 7983.0 / 72.0,  PeakAttenuationIPrim = 7813.0 / 52.0,
                       SpotTransmission = 0.0,   SuspendedOnWires = false },
            new Mask { Name = "CLC-M-WF",  RadiusMas = 77.5,
                       PeakAttenuationRPrim = 7983.0 / 26.0,  PeakAttenuationIPrim = 7813.0 / 13.0,
                       SpotTransmission = 0.0,   SuspendedOnWires = true },
            new Mask { Name = "CLC-MT-WF", RadiusMas = 77.5,
                       PeakAttenuationRPrim = 7983.0 / 26.0,  PeakAttenuationIPrim = 7813.0 / 13.0,
                       SpotTransmission = 0.001, SuspendedOnWires = false },
            new Mask { Name = "CLC-L-WF",  RadiusMas = 155.0,
                       PeakAttenuationRPrim = 7983.0 / 31.0,  PeakAttenuationIPrim = 7813.0 / 11.0,
                       SpotTransmission = 0.0,   SuspendedOnWires = true },
            new Mask { Name = "CLC-XL-WF", RadiusMas = 538.0,
                       PeakAttenuationRPrim = 7983.0 / 7.5,   PeakAttenuationIPrim = 7813.0 / 2.7,
                       SpotTransmission = 0.0,   SuspendedOnWires = true },
        };

        /// <summary>Peak count of the clear (non-coronagraphic) stop NC_WF in the ct_n6 normalisation, per filter (Schmid et al. 2018, Table 8). The denominators of every attenuation above.</summary>
        public const double ClearPeakCountsRPrim = 7983.0;
        public const double ClearPeakCountsIPrim = 7813.0;

        /// <summary>Central wavelengths of the two filters the attenuations are measured in, nm. R_PRIM and I_PRIM of ZIMPOL's own filter set.</summary>
        public const double RPrimWavelengthNm = 626.0;
        public const double IPrimWavelengthNm = 790.0;

        /// <summary>The mask of that name, or null if the instrument does not carry it.</summary>
        public static Mask? Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < VisualMasks.Length; i++)
                if (string.Equals(VisualMasks[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return VisualMasks[i];
            return null;
        }

        /// <summary>
        /// Peak attenuation of a mask at an arbitrary wavelength, interpolated in 1/lambda between
        /// the two filters ESO measured it in.
        ///
        /// LINEAR IN 1/LAMBDA rather than in lambda, because that is the variable the physics is
        /// linear in: a mask of fixed angular radius rho spans rho/(lambda/D) resolution elements,
        /// and it is that count, not the wavelength, that sets how much of the core it removes.
        /// Interpolating in wavelength would put the curve on the wrong side of its own two
        /// anchors everywhere between them.
        ///
        /// Held flat outside the measured span rather than extrapolated. ZIMPOL's own range runs
        /// from about 500 to 900 nm while these two filters sit at 626 and 790, so the ends are
        /// genuinely unmeasured, and a power law fitted through two points would be a guess with a
        /// slope.
        /// </summary>
        public static double PeakAttenuation(Mask mask, double wavelengthNm)
        {
            if (!(wavelengthNm > 0.0)) return mask.PeakAttenuationRPrim;

            double x = 1.0 / wavelengthNm;
            double xR = 1.0 / RPrimWavelengthNm;
            double xI = 1.0 / IPrimWavelengthNm;

            if (x >= xR) return mask.PeakAttenuationRPrim;   // bluer than R_PRIM
            if (x <= xI) return mask.PeakAttenuationIPrim;   // redder than I_PRIM

            double f = (x - xI) / (xR - xI);
            return mask.PeakAttenuationIPrim + f * (mask.PeakAttenuationRPrim - mask.PeakAttenuationIPrim);
        }

        /// <summary>
        /// The Lyot pupil stop's geometry, expressed as the telescope pupil it leaves behind.
        ///
        /// WHY THIS IS RETURNED AS A PUPIL AND NOT AS A TRANSMISSION. The stop is not an attenuator;
        /// it is an aperture, and the instrument's point-spread function is the diffraction pattern
        /// of whatever aperture the light last passed through. Undersizing the outer edge and
        /// oversizing the central obstruction changes the PSF's shape, not merely its brightness:
        /// the first dark ring moves outward, the core widens, and the diffraction rings the mask
        /// scattered are the ones this removes. Handing the caller an effective aperture,
        /// obstruction and vane width lets the existing pupil-diffraction code (Core.PupilDiffraction)
        /// compute that PSF with nothing new to learn.
        ///
        /// The numbers are Schmid et al. (2018) Table 9, which gives the coronagraphic pupil as
        /// 5.97 mm across with a 0.896 mm central hole and 0.037 mm spider, and STOPB1_2 (the
        /// default in 2015) as 5.4 mm across with a 1.2 mm hole and 0.18 mm spider vanes. Scaling
        /// by the telescope's own 8.2 m over that 5.97 mm gives the figures below.
        ///
        /// The scaling validates itself against a number in the same table that was not used to
        /// derive it: the stop's published geometric transmission is 72.6%, and the area of the
        /// annulus these figures describe, less its four vanes, is 73.8% of the telescope's. A 1.2
        /// point agreement on a quantity computed from three independently read dimensions is a
        /// good check that the table has been read correctly.
        /// </summary>
        public struct LyotStop
        {
            public string Name;
            /// <summary>Outer diameter of the transmitted pupil, in metres at the telescope.</summary>
            public double ApertureMeters;
            /// <summary>Central obstruction as a fraction of the outer diameter above.</summary>
            public double ObstructionFraction;
            /// <summary>Width of the stop's own spider vanes, in metres at the telescope. Wider than the telescope's, because they must hide them.</summary>
            public double SpiderVaneWidthMeters;
            /// <summary>Geometric transmission relative to the unstopped telescope pupil, as ESO publish it.</summary>
            public double GeometricTransmission;
        }

        /// <summary>Diameter of the coronagraphic pupil image inside SPHERE, mm (Schmid et al. 2018, Table 9), and the telescope aperture it stands for.</summary>
        public const double CoronagraphicPupilMillimetres = 5.97;
        public const double TelescopeApertureMeters = 8.2;

        /// <summary>Millimetres of the internal pupil image to metres of telescope aperture.</summary>
        private static double ToTelescopeMeters(double millimetres)
            => millimetres * (TelescopeApertureMeters / CoronagraphicPupilMillimetres);

        /// <summary>
        /// STOPB1_2, the default pupil stop of ZIMPOL's pupil-stabilised observations in 2015 and
        /// the one every coronagraphic measurement quoted in this file was taken through.
        ///
        /// The "B" family hides the telescope spiders as well as the pupil rims, which is what
        /// pupil-stabilised observing requires; the "_2" variants carry extra blockers for the
        /// scattered light from the deformable mirror's dead actuators.
        /// </summary>
        public static readonly LyotStop StopB1_2 = new LyotStop
        {
            Name = "STOPB1_2",
            ApertureMeters = ToTelescopeMeters(5.4),               // 7.417 m
            ObstructionFraction = 1.2 / 5.4,                       // 0.2222, against the telescope's own 0.140
            SpiderVaneWidthMeters = ToTelescopeMeters(0.18),       // 0.247 m, against the telescope's 0.041
            GeometricTransmission = 0.726,
        };

        /// <summary>
        /// What fraction of the light entering the telescope reaches the detector through the
        /// pupil stop, as against the stop's purely geometric transmission.
        ///
        /// These are two different numbers and ESO measure both. The stop removes 72.6% of the
        /// pupil's AREA, but the light it removes is not average light: it is concentrated at the
        /// rims, where the diffracted light sits. From the pupil images the paper decomposes the
        /// blocked light as 5.0% diffracted at the rims, 1.5% in the eight brightest dead-actuator
        /// maxima, and 2.5% scattered outside the aperture and inside the central hole, and states
        /// the resulting relation as approximately 0.91 T_geom.
        ///
        /// That 0.91 is the whole argument for a Lyot stop: it throws away a quarter of the pupil
        /// and only 9% of the useful light, because the quarter it throws away is where the star's
        /// diffracted light went.
        /// </summary>
        public const double StopThroughputOverGeometric = 0.91;

        /// <summary>Fraction of the entering light that reaches the detector through the given stop.</summary>
        public static double Throughput(LyotStop stop)
            => StopThroughputOverGeometric * stop.GeometricTransmission;

        /// <summary>
        /// Transmission of the focal-plane mask at an angular separation from the star, as a
        /// fraction.
        ///
        /// A hard edge, because a classical Lyot spot IS a hard edge: an opaque metallic disc
        /// deposited on a plate or suspended on wires. Its softening in a real image comes from the
        /// PSF that is convolved with it afterwards, not from the mask, and this pipeline already
        /// convolves one.
        /// </summary>
        public static double MaskTransmission(Mask mask, double separationMas)
            => separationMas < mask.RadiusMas ? mask.SpotTransmission : 1.0;

        /// <summary>
        /// The smallest separation at which this mask can be used to look for something, in
        /// milliarcseconds: its own radius.
        ///
        /// Stated as a method rather than left implicit because it is the number that decides
        /// whether an observation is possible at all, and because it is the axis along which the
        /// five masks trade against each other. CLC-S-WF reaches in to 46.5 mas and attenuates by
        /// about 130; CLC-XL-WF attenuates by 1000 to 3000 and cannot see anything inside 538 mas.
        /// Choosing between them IS the observation.
        /// </summary>
        public static double InnerWorkingAngleMas(Mask mask) => mask.RadiusMas;
    }
}
