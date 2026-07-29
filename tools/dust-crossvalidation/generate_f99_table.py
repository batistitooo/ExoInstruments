"""Generates Core/Fitzpatrick99Table.cs from the published F99 extinction law.

WHY A TABLE AND NOT A FORMULA. Fitzpatrick (1999, PASP 111, 63) does not define its optical and
near-infrared curve in closed form. It defines it as a CUBIC SPLINE through published anchor points
-- three in the infrared, four in the optical, and two more in the ultraviolet taken from the
Fitzpatrick & Massa (1990) parameterisation evaluated at 2700 and 2600 Angstrom. Reimplementing that
means reimplementing a particular spline construction and matching its knot placement exactly, which
is a way to introduce an error that looks like physics. Tabulating the reference implementation on a
grid finer than any structure the curve contains is not an approximation of the law; it is the law,
sampled.

This follows the pattern Core/FilterCurves.cs already sets for ESO's measured FORS2 transmissions:
a published curve carried as data, with its provenance in the file, rather than a shape assumed.

THE GRID. Uniform in wavenumber x = 1/lambda (inverse microns), because that is the variable the law
is a spline in and the one its structure is smooth in. 0.30 to 3.40 covers 294 nm to 3.33 microns,
which contains every filter in FilterCurves and stops short of the 2175 Angstrom bump at x = 4.6 --
see InterstellarExtinction for why that boundary is a refusal rather than an omission.

R_V is tabulated too, over 2.0 to 6.0. All-sky dust maps are calibrated at 3.1, but Schlafly et al.
(2016, ApJ 821, 78) map R_V variation across the sky, so the axis exists for the day that lands.
tools/dust-crossvalidation measures the interpolation error on both axes.

Run:
    ./env/bin/python generate_f99_table.py
"""

import numpy as np
from dust_extinction.parameter_averages import F99

X_MIN, X_MAX, X_STEP = 0.30, 3.40, 0.01
RV_VALUES = [round(2.0 + 0.1 * i, 1) for i in range(41)]   # 2.0 to 6.0 step 0.1

HEADER = '''using System;

namespace ExoInstruments.Core
{{
    /// <summary>
    /// Fitzpatrick (1999, PASP 111, 63) A(lambda)/A(V), tabulated.
    ///
    /// GENERATED FILE. Produced by tools/dust-crossvalidation/generate_f99_table.py from the
    /// dust_extinction package's reference implementation of the published law; do not edit by
    /// hand, regenerate. That directory's harness checks this table against the same reference,
    /// so a drift between them is caught rather than assumed away.
    ///
    /// WHY IT IS A TABLE. F99 defines its optical and near-infrared curve as a cubic spline through
    /// published anchor points, not as a formula. Reimplementing the spline construction would be a
    /// way to introduce an error that looks like physics; sampling the law on a grid finer than any
    /// structure it contains is the law, not an approximation of it. Same treatment, and the same
    /// reason, as FilterCurves carrying ESO's measured transmissions rather than assuming top-hats.
    ///
    /// The grid is uniform in wavenumber x = 1/lambda (inverse microns), which is the variable the
    /// spline is built in. Interpolation is bilinear in (x, R_V), and its error is measured rather
    /// than asserted: see the harness.
    ///
    /// Generated from dust_extinction {version}, grid x = {xmin} to {xmax} step {xstep} inverse
    /// microns, R_V = {rvlist}.
    /// </summary>
    internal static class Fitzpatrick99Table
    {{
        /// <summary>Wavenumber of the first column, inverse microns.</summary>
        private const double XMin = {xmin};

        /// <summary>Wavenumber step between columns, inverse microns.</summary>
        private const double XStep = {xstep};

        /// <summary>Number of wavenumber columns.</summary>
        private const int XCount = {xcount};

        /// <summary>R_V values the rows are tabulated at. Ascending, and not evenly spaced: 3.1 is included exactly because it is the Galactic average every dust map is calibrated to.</summary>
        private static readonly double[] RvValues = {{ {rvarray} }};

        /// <summary>
        /// A(lambda)/A(V), row-major as [R_V index * XCount + x index].
        ///
        /// Flat rather than jagged on purpose: this is read once per source per capture on a
        /// background thread, and a flat array keeps that one bounds-checked indexing operation
        /// rather than two dereferences.
        /// </summary>
        private static readonly double[] Kappa =
        {{
{table}
        }};

        /// <summary>
        /// A(lambda)/A(V) at the given wavelength and R_V, bilinear in (x, R_V).
        ///
        /// Clamps in R_V rather than extrapolating: outside 2.0 to 6.0 there is no observed
        /// Galactic sight line to extrapolate toward. Returns 0 outside the wavelength grid, which
        /// InterstellarExtinction reads as "not modelled" -- see the range discussion there.
        /// </summary>
        internal static double Evaluate(double wavelengthMeters, double rv)
        {{
            if (!(wavelengthMeters > 0.0)) return 0.0;
            double x = 1.0e-6 / wavelengthMeters;

            double column = (x - XMin) / XStep;
            if (column < 0.0 || column > XCount - 1) return 0.0;

            int x0 = (int)Math.Floor(column);
            if (x0 >= XCount - 1) x0 = XCount - 2;
            double fx = column - x0;

            // R_V row, clamped at both ends.
            //
            // Interpolated in 1/R_V rather than in R_V, and that is physics rather than taste:
            // an extinction law's R_V dependence is written a(x) + b(x)/R_V, so at fixed
            // wavelength it is a straight line in 1/R_V and a curve in R_V. The harness measures
            // the difference on this grid.
            int r0 = 0;
            while (r0 < RvValues.Length - 2 && rv > RvValues[r0 + 1]) r0++;
            double invLo = 1.0 / RvValues[r0];
            double span = 1.0 / RvValues[r0 + 1] - invLo;
            double fr = span != 0.0 ? (1.0 / rv - invLo) / span : 0.0;
            if (fr < 0.0) fr = 0.0; else if (fr > 1.0) fr = 1.0;

            int baseLo = r0 * XCount + x0;
            int baseHi = baseLo + XCount;

            double lo = Kappa[baseLo] + fx * (Kappa[baseLo + 1] - Kappa[baseLo]);
            double hi = Kappa[baseHi] + fx * (Kappa[baseHi + 1] - Kappa[baseHi]);
            return lo + fr * (hi - lo);
        }}
    }}
}}
'''


def main():
    import dust_extinction

    x = np.arange(X_MIN, X_MAX + 0.5 * X_STEP, X_STEP)
    rows = []
    for rv in RV_VALUES:
        model = F99(Rv=rv)
        # dust_extinction takes wavenumbers in 1/micron directly.
        k = model(x / 1.0)
        rows.append(np.asarray(k, dtype=float))

    lines = []
    for rv, k in zip(RV_VALUES, rows):
        lines.append(f"            // R_V = {rv}")
        for i in range(0, len(k), 6):
            chunk = ", ".join(f"{v:.10g}" for v in k[i:i + 6])
            lines.append(f"            {chunk},")

    text = HEADER.format(
        version=dust_extinction.__version__,
        xmin=f"{X_MIN:.10g}", xmax=f"{X_MAX:.10g}", xstep=f"{X_STEP:.10g}",
        xcount=len(x),
        rvlist=", ".join(str(r) for r in RV_VALUES),
        rvarray=", ".join(f"{r:.10g}" for r in RV_VALUES),
        table="\n".join(lines).rstrip(","),
    )

    out = "../../ExoInstruments/Core/Fitzpatrick99Table.cs"
    with open(out, "w") as f:
        f.write(text)
    print(f"wrote {out}: {len(RV_VALUES)} x {len(x)} = {len(RV_VALUES) * len(x)} values")


if __name__ == "__main__":
    main()
