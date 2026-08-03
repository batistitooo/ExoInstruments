using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Why twice the light does not give exactly twice the counts.
    ///
    /// THE MECHANISM. Charge is read out by measuring the voltage it produces on the sense node,
    /// and that node's capacitance is itself a function of the voltage across it. So the
    /// charge-to-voltage conversion has a slope that changes as the well fills, the output falls
    /// progressively below the straight line through the origin, and the departure grows with
    /// signal. Janesick (2001, "Scientific Charge-Coupled Devices") treats this as the output
    /// amplifier's defining departure from ideal; it is a property of the READOUT, which is why it
    /// is applied to the charge after transfer and before the read noise rather than to the photon
    /// count.
    ///
    /// WHY IT IS NOT COSMETIC. Non-linearity is the one detector effect that survives every
    /// calibration frame in the standard set. A bias removes the pedestal, a dark removes the
    /// thermal signal, a flat removes the response ratio; none of them touches a curvature that
    /// depends on how full the well is, because each calibration frame sits at its own signal level
    /// and carries its own curvature. It is corrected, when it is corrected, by inverting a measured
    /// curve, which is what Correct below is for. Left uncorrected it biases exactly the bright
    /// stars a photometric zero point is measured from, so it propagates into every magnitude in the
    /// frame rather than staying with the pixels that produced it.
    ///
    /// THE FORM. One quadratic term:
    ///
    ///     measured(Q) = Q * (1 - d * Q / Q_fullwell)
    ///
    /// so that d is exactly the relative deviation from linearity at full well and nothing else has
    /// to be chosen. A second-order polynomial is not a simplification adopted here for convenience:
    /// it is the form ESO's own detector-monitoring recipe fits, which derives QC.LIN.EFF by fitting
    /// a second-order polynomial to flux against exposure time and quoting the normalised difference
    /// between it and the linear prediction at a stated flux level.
    ///
    /// WHAT THE PUBLISHED NUMBER MEANS, stated because the source is ambiguous and the ambiguity
    /// should not be hidden inside a constant. The FORS2 user manual's Table 2.9 heads its column
    /// "linearity (up to full well; % RMS)" and gives the MIT mosaic (-0.9,-0.5) at high gain and
    /// (1.8, 2.1) at low gain, per chip. Read as an RMS residual about a fitted line those numbers
    /// would imply a compression of about 24% at full well, which no scientific CCD has and which
    /// the same manual contradicts by promising the converter saturates first. Read as the signed
    /// relative deviation from linearity over the range up to full well, which is what the QC1
    /// parameter of the same name measures, 1.8% is an ordinary figure for a device of this class.
    /// The second reading is the one used. The manual does not state which end of the range the
    /// sign refers to, so the magnitude is taken and applied as COMPRESSION, the direction sense-node
    /// capacitance produces.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class DetectorLinearity
    {
        /// <summary>
        /// The charge the readout reports for a real charge Q, given the device's relative deviation
        /// from linearity at full well.
        ///
        /// Monotonic over the physical range: the derivative 1 - 2*d*Q/Q_fw stays positive for every
        /// d below 0.5, which every published figure is by two orders of magnitude. Beyond full well
        /// the quadratic would eventually turn over, so the argument is clamped there; a real pixel
        /// beyond full well has already bloomed, and this pipeline applies blooming before it gets
        /// here.
        /// </summary>
        public static double Measured(double electrons, double fullWellElectrons, double deviationAtFullWell)
        {
            if (!(deviationAtFullWell > 0.0) || double.IsNaN(deviationAtFullWell)) return electrons;
            if (!(fullWellElectrons > 0.0)) return electrons;
            if (!(electrons > 0.0)) return electrons;

            double x = electrons / fullWellElectrons;
            if (x > 1.0) x = 1.0;
            return electrons * (1.0 - deviationAtFullWell * x);
        }

        /// <summary>
        /// The inverse: the real charge that produced a reported one. This is the correction a
        /// reduction pipeline applies, and it exists here so that the correction and the effect can
        /// never drift apart, being one quadratic solved in both directions.
        ///
        /// Solving m = Q - d*Q^2/Q_fw for Q gives Q = Q_fw * (1 - sqrt(1 - 4*d*m/Q_fw)) / (2*d),
        /// taking the root that tends to m as d tends to 0. The discriminant goes negative only
        /// above the curve's own maximum, which lies beyond full well for every real d, and is
        /// clamped rather than thrown for the pixels blooming has already ruined.
        /// </summary>
        public static double Correct(double measuredElectrons, double fullWellElectrons, double deviationAtFullWell)
        {
            if (!(deviationAtFullWell > 0.0) || double.IsNaN(deviationAtFullWell)) return measuredElectrons;
            if (!(fullWellElectrons > 0.0)) return measuredElectrons;
            if (!(measuredElectrons > 0.0)) return measuredElectrons;

            double discriminant = 1.0 - 4.0 * deviationAtFullWell * measuredElectrons / fullWellElectrons;
            if (discriminant < 0.0) discriminant = 0.0;
            return fullWellElectrons * (1.0 - Math.Sqrt(discriminant)) / (2.0 * deviationAtFullWell);
        }
    }
}
