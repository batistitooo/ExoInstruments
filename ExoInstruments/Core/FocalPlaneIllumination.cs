using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// How much of the light that entered the telescope reaches each part of the detector, as a
    /// fraction of what reaches the centre.
    ///
    /// THE OTHER HALF OF A FLAT FIELD. SensorNonUniformity supplies the part that belongs to the
    /// silicon; this supplies the part that belongs to the OPTICS, and on a real instrument the
    /// optical part is usually the larger of the two. They are separate files because they are
    /// separate physics with separate lifetimes: PRNU is fixed to a sensor and travels with it
    /// between tubes, while illumination is fixed to a tube and changes the moment the camera is
    /// moved to another one. On this roster that is not a hypothetical, since one ASI294MM Pro is
    /// shared between the RC20, the CDK1000 and the RedCat.
    ///
    /// TWO TERMS, both computed rather than tabulated, from numbers each instrument already
    /// publishes.
    ///
    /// 1. THE COSINE-FOURTH LAW. Off-axis irradiance falls as cos^4(theta), theta being the field
    ///    angle subtended at the exit pupil, and the exponent is four because three independent
    ///    cosines and one inverse square coincide: the exit pupil is seen foreshortened by cos, the
    ///    image element is tilted by cos, and the extra path length costs cos^2. This is the
    ///    standard result of radiometric optics (Kingslake, "Optics in Photography", and Smith,
    ///    "Modern Optical Engineering", both derive it in this form); it is geometry, so it needs
    ///    no per-device measurement, only the focal length and where the pixel sits.
    ///
    ///    It is also, on this roster, SMALL, and saying so is the point rather than an apology for
    ///    the model. At the RedCat's 250mm the sensor's corner sits 2.657 degrees off axis and
    ///    loses 0.45%; at the RC20's 3468mm the same corner is 0.19 degrees off axis and loses
    ///    0.0045%. A wide-field astrograph and a long-focus one are genuinely different instruments
    ///    here, by two orders of magnitude, and the first of those two figures is comparable to the
    ///    sensor's own PRNU while the second is a hundred times below it.
    ///
    /// 2. A FIELD STOP, where the instrument publishes one. This is not a gradient but an EDGE: an
    ///    aperture in or near the focal plane admits light inside it and none outside, so the
    ///    illumination goes to zero over a distance set by how far the stop sits from the detector
    ///    rather than falling off smoothly. FORS2 is the instrument on this roster that has one and
    ///    publishes it, and it bites: ESO's manual states the field of view "is restricted by the
    ///    MOS unit in the focal plane of the unit telescope to about 6.8x6.8 arcminutes" in the
    ///    standard-resolution collimator, while the MIT mosaic at 0.125 arcsec per unbinned pixel
    ///    spans 8.5 arcminutes. Roughly a third of the detector's area is therefore outside the
    ///    illuminated field and reads only dark, and any photometry that ignores that is measuring
    ///    a corner of the chip that never saw the sky.
    ///
    /// WHAT IS NOT MODELLED, listed in section 12 rather than invented:
    ///
    ///   * The exact TWO-DIMENSIONAL shape of the FORS2 stop. ESO publishes it as a figure
    ///     (Appendix G of the user manual) rather than as a formula, and records that the two CCDs
    ///     of the mosaic are mounted 33 arcsec off the optical axis so the true pattern is not
    ///     centred on the detector. Modelled here as a square stop centred on the frame, which is
    ///     the manual's own "6.8x6.8 arcminutes" read literally.
    ///   * DUST MOTES. Out-of-focus shadows of dust on the filter or window are the most
    ///     recognisable feature of a real amateur flat, and they are also the least predictable:
    ///     their number, size and position are a property of one particular night's optical
    ///     surfaces, not of the instrument. Nothing publishable exists to source them from, so
    ///     there are none here rather than invented ones.
    ///   * ACCESSORY VIGNETTING. Undersized filters, a narrow drawtube or an off-axis guider are
    ///     what actually produce the deep corners in most real amateur flats. None of them is part
    ///     of an instrument's published specification, so none is modelled, and the consequence is
    ///     stated plainly: the flats this pipeline produces for the three amateur tubes are far
    ///     flatter than the ones their real owners take, because the modelled optics are only the
    ///     optics the manufacturer specifies.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class FocalPlaneIllumination
    {
        /// <summary>
        /// Relative irradiance at a point r metres off the optical axis in the focal plane, for a
        /// system of the given focal length: cos^4(atan(r/f)).
        ///
        /// Written as the algebraically equal 1/(1+(r/f)^2)^2 rather than as a cosine of an
        /// arctangent, which is the same number without two transcendental calls per pixel and
        /// without their rounding.
        /// </summary>
        public static double CosineFourth(double offAxisMetres, double focalLengthMetres)
        {
            if (!(focalLengthMetres > 0.0)) return 1.0;
            double t = offAxisMetres / focalLengthMetres;
            double s = 1.0 + t * t;
            return 1.0 / (s * s);
        }

        /// <summary>
        /// Whether a point sits inside a square field stop of the given angular side, expressed in
        /// the focal plane of a system of the given focal length.
        ///
        /// Square rather than round because that is what ESO publishes for the one instrument here
        /// that has a stop ("6.8x6.8 arcminutes"), and a round stop of the same figure would remove
        /// light from the edge midpoints that the manual says is there.
        ///
        /// The transition is a hard edge. A stop IN the focal plane casts a geometrically sharp
        /// shadow; the penumbra of a real one is set by its distance from the detector, and the
        /// FORS2 manual places the MOS unit in the focal plane itself, so there is nothing to
        /// soften it with that would not be invented.
        /// </summary>
        public static bool InsideSquareFieldStop(
            double xMetres, double yMetres, double focalLengthMetres, double stopSideArcmin)
        {
            if (!(stopSideArcmin > 0.0) || double.IsNaN(stopSideArcmin)) return true;
            if (!(focalLengthMetres > 0.0)) return true;

            double halfSideMetres = 0.5 * focalLengthMetres * Math.Tan(ArcminToRadians(stopSideArcmin));
            return Math.Abs(xMetres) <= halfSideMetres && Math.Abs(yMetres) <= halfSideMetres;
        }

        /// <summary>
        /// Whether a point sits inside a circular image circle of the given diameter in millimetres, the form
        /// the amateur tubes on this roster publish theirs in.
        /// </summary>
        public static bool InsideImageCircle(double xMetres, double yMetres, double imageCircleMillimetres)
        {
            if (!(imageCircleMillimetres > 0.0) || double.IsNaN(imageCircleMillimetres)) return true;
            double radiusMetres = 0.5e-3 * imageCircleMillimetres;
            return xMetres * xMetres + yMetres * yMetres <= radiusMetres * radiusMetres;
        }

        /// <summary>
        /// The full illumination factor at one pixel: the cosine-fourth falloff inside whatever
        /// stops the instrument has, and zero outside them.
        ///
        /// x and y are measured from the optical axis in metres in the focal plane. Both stop
        /// parameters may be NaN, which means the instrument publishes none and the whole detector
        /// is illuminated; that is the case for every instrument here except FORS2.
        /// </summary>
        public static double Factor(
            double xMetres, double yMetres, double focalLengthMetres,
            double stopSideArcmin, double imageCircleMillimetres)
        {
            if (!InsideSquareFieldStop(xMetres, yMetres, focalLengthMetres, stopSideArcmin)) return 0.0;
            if (!InsideImageCircle(xMetres, yMetres, imageCircleMillimetres)) return 0.0;

            double r = Math.Sqrt(xMetres * xMetres + yMetres * yMetres);
            return CosineFourth(r, focalLengthMetres);
        }

        private static double ArcminToRadians(double arcmin) => arcmin * (Math.PI / (180.0 * 60.0));
    }
}
