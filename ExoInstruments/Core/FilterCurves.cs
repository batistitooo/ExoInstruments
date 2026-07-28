using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Real measured filter transmission curves, as published by the observatory that owns the
    /// instrument.
    ///
    /// WHAT THIS REPLACES. Until now every filter in the roster was a TOP-HAT: a rectangle of the
    /// filter's published FWHM at its published central wavelength, scaled by its published peak
    /// transmission. That is the honest treatment when nothing else is published, and it is still
    /// what the amateur LRGB set gets. But ESO measured FORS2's filters IN THE INSTRUMENT and
    /// publishes the results as ASCII tables, so for those three there is no reason to keep
    /// guessing a shape.
    ///
    /// WHY THE SHAPE MATTERS, given the pipeline already integrates across the band. Three
    /// separate reasons, none of which a top-hat can express:
    ///
    ///   * The equivalent width is not the FWHM. A real filter has sloped shoulders that a
    ///     rectangle does not, and wings a rectangle cuts off dead.
    ///   * The colour term is weighted by the filter's actual shape, so a star's spectrum is
    ///     sampled where the filter really transmits rather than uniformly across a box.
    ///   * Extinction and QE both vary across the band, so WHERE inside the band the filter
    ///     passes light changes how much atmosphere and how much detector it sees.
    ///
    /// Source: ESO, FORS2 filter transmission curves
    /// (www.eso.org/sci/facilities/paranal/instruments/fors/inst/Filters/curves.html), which
    /// states that "the transmission curves for many of the FORS interference filters have been
    /// measured within the instruments". Tables sampled at 10 nm from 330 to 1200 nm.
    ///
    /// Pure C# with no Unity dependency, like the rest of Core.
    /// </summary>
    public static class FilterCurves
    {
        /// <summary>
        /// FORS2 Bessell B, as ESO measured it in the instrument. Peak transmission 0.6871 at
        /// 420 nm, half-power points at 380 and 470 nm.
        ///
        /// The full 330-1200 nm range is kept rather than trimmed to the passband, because the
        /// red leak is real: 0.77% of this filter's integrated transmission sits more than
        /// 100 nm beyond its red half-power point, rising again towards 1200 nm the way every
        /// real interference filter does. Whether that leak reaches the detector is then the
        /// QE curve's business, not the filter's, and integrating the product is the only way to
        /// find out. Trimming here would answer the question by assumption.
        /// </summary>
        public static readonly SpectralCurve Fors2B = new SpectralCurve(
            new double[] { 330, 340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870, 880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070, 1080, 1090, 1100, 1110, 1120, 1130, 1140, 1150, 1160, 1170, 1180, 1190, 1200 },
            new double[] { 0.016797, 0.012331, 0.0031752, 0.046573, 0.19619, 0.36812, 0.58278, 0.62079, 0.666, 0.68712, 0.67617, 0.61893, 0.61987, 0.54592, 0.36373, 0.20609, 0.08629, 0.038661, 0.011195, 0.0020501, 0.00022604, 0.0001354, 0.0002221, 0.00048807, 0.00030331, 2.8667e-05, 1.206e-06, 6.6411e-06, 2.4165e-05, 6.7202e-05, 5.6622e-05, 2.3653e-05, 4.6223e-05, 7.8478e-07, 6.6682e-07, 4.6503e-05, 9.9436e-05, 0.00035542, 0.00024775, 2.4813e-05, 8.4638e-05, 8.3241e-05, 8.8353e-05, 2.3092e-05, 1.1576e-07, 5.1105e-06, 9.9371e-07, 4.9834e-05, 3.1579e-05, 4.3558e-05, 2.3189e-05, 7.7518e-05, 2.2903e-05, 6.9809e-07, 3.4943e-08, 1.0071e-05, 4.1296e-06, 2.4082e-05, 2.5671e-05, 1.2574e-05, 5.9477e-06, 7.7431e-05, 3.5905e-05, 3.2014e-05, 1.7551e-06, 2.0342e-05, 6.0733e-06, 5.6147e-06, 4.0879e-05, 5.5933e-05, 2.9911e-05, 2.9161e-07, 9.1056e-07, 6.5889e-05, 7.7732e-05, 7.8684e-05, 0.00026086, 8.9144e-05, 0.00082658, 0.000336, 2.6806e-06, 0.00013354, 4.496e-05, 0.004392, 0.006942, 7.9577e-06, 0.0010688, 0.065956 });

        /// <summary>
        /// FORS2 Bessell V, as ESO measured it in the instrument. Peak transmission 0.8887 at
        /// 530 nm, half-power points at 500 and 600 nm.
        ///
        /// The full 330-1200 nm range is kept rather than trimmed to the passband, because the
        /// red leak is real: 1.34% of this filter's integrated transmission sits more than
        /// 100 nm beyond its red half-power point, rising again towards 1200 nm the way every
        /// real interference filter does. Whether that leak reaches the detector is then the
        /// QE curve's business, not the filter's, and integrating the product is the only way to
        /// find out. Trimming here would answer the question by assumption.
        /// </summary>
        public static readonly SpectralCurve Fors2V = new SpectralCurve(
            new double[] { 330, 340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870, 880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070, 1080, 1090, 1100, 1110, 1120, 1130, 1140, 1150, 1160, 1170, 1180, 1190, 1200 },
            new double[] { 0.0095766, 0.014773, 0.014203, 0.0027326, 0.0012195, 0.00086154, 0.0028846, 0.00086833, 1.1384e-05, 0.0002689, 0.00031464, 0.00039771, 6.2938e-05, 4.1079e-05, 0.00019389, 0.02469, 0.35023, 0.64513, 0.80892, 0.87114, 0.88868, 0.88482, 0.86219, 0.8221, 0.76557, 0.68987, 0.54889, 0.48988, 0.38367, 0.28791, 0.20163, 0.13236, 0.080913, 0.045447, 0.025816, 0.012696, 0.0057779, 0.0022389, 0.0010143, 0.00034963, 0.00011749, 6.2018e-05, 3.3786e-05, 6.3691e-05, 1.8705e-05, 3.8913e-08, 8.3982e-06, 1.7009e-05, 3.0189e-05, 1.6431e-08, 1.7561e-05, 9.3903e-05, 1.4077e-05, 5.5922e-06, 4.0757e-05, 4.5992e-05, 2.3205e-05, 9.9067e-06, 2.9084e-05, 1.4982e-05, 3.6621e-05, 4.2949e-05, 5.1221e-06, 1.5037e-06, 1.6178e-05, 2.1877e-06, 1.2077e-05, 1.4198e-05, 1.9912e-05, 3.4272e-05, 4.5399e-06, 4.5997e-05, 1e-05, 5.1256e-05, 9.4361e-05, 8.415e-05, 1.3347e-06, 7.4598e-08, 1.9565e-05, 0.00016687, 0.00016937, 0.00093447, 0.00047366, 0.00079907, 0.00035824, 0.00059682, 0.048824, 0.15978 });

        /// <summary>
        /// FORS2 Bessell R, as ESO measured it in the instrument. Peak transmission 0.8555 at
        /// 600 nm, half-power points at 580 and 720 nm.
        ///
        /// The full 330-1200 nm range is kept rather than trimmed to the passband, because the
        /// red leak is real: 3.21% of this filter's integrated transmission sits more than
        /// 100 nm beyond its red half-power point, rising again towards 1200 nm the way every
        /// real interference filter does. Whether that leak reaches the detector is then the
        /// QE curve's business, not the filter's, and integrating the product is the only way to
        /// find out. Trimming here would answer the question by assumption.
        /// </summary>
        public static readonly SpectralCurve Fors2R = new SpectralCurve(
            new double[] { 330, 340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470, 480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670, 680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870, 880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070, 1080, 1090, 1100, 1110, 1120, 1130, 1140, 1150, 1160, 1170, 1180, 1190, 1200 },
            new double[] { 0.0059885, 0.0072414, 0.0028996, 0.0038948, 0.0027527, 0.0023059, 0.00077273, 0.0007691, 0.00058977, 0.00033026, 0.00017525, 0.00027279, 2.4499e-05, 0.00010601, 9.2978e-05, 3.0476e-05, 5.8116e-05, 1.1962e-05, 5.6162e-05, 2.7011e-05, 2.6198e-05, 8.2467e-06, 1.7993e-05, 0.020066, 0.42567, 0.73668, 0.78143, 0.85554, 0.8422, 0.83286, 0.8077, 0.78145, 0.74944, 0.71792, 0.67971, 0.64612, 0.59965, 0.63158, 0.5135, 0.46855, 0.38978, 0.37206, 0.3247, 0.28535, 0.24317, 0.20908, 0.17446, 0.1473, 0.11983, 0.099622, 0.080223, 0.067198, 0.054905, 0.0441, 0.034946, 0.027833, 0.022, 0.017553, 0.013923, 0.011103, 0.0087907, 0.0071249, 0.0056757, 0.0046198, 0.0037605, 0.0030467, 0.0025401, 0.0021453, 0.0017657, 0.0015278, 0.0012991, 0.0011041, 0.00092254, 0.00073711, 0.00060636, 0.0005724, 0.00037883, 0.00057122, 2.3292e-06, 0.0005246, 0.00079201, 0.00052928, 0.00091329, 0.001254, 0.0024746, 0.012077, 0.0095727, 0.080132 });

    }
}
