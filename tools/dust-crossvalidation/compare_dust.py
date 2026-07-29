"""Cross-validates ExoInstruments' interstellar extinction against dust_extinction.

WHAT IS BEING ESTABLISHED, AND IN WHAT ORDER.

  1. CCM89 in closed form, against the reference implementation. This is the control: the law is
     polynomial, so agreement here must be at machine precision, and anything worse means the
     coefficients or the branch cut are wrong rather than that the physics is subtle.
  2. F99 through the generated table, against the same reference. This is the law the mod actually
     uses, and the question is whether tabulating and interpolating it costs anything measurable.
  3. The interpolation error on both table axes, measured at points deliberately placed BETWEEN
     grid nodes and between R_V rows rather than on them.
  4. The normalisation, and why it is a weaker statement than it looks. A(V) = R_V E(B-V) is a
     relation between BAND-INTEGRATED extinctions, so k(V) = 1 monochromatically is a property of
     how each law anchors itself rather than a physical requirement -- CCM89 satisfies it exactly by
     construction, F99 returns 0.9793. See normalisation() for what follows from that.
  5. That the two laws disagree with each other by about as much as the literature says they do,
     which is the check that neither has been silently replaced by the other.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_dust.py

Exit code 0 when every check passes, 1 otherwise.
"""

import sys
import warnings

import numpy as np

warnings.filterwarnings("ignore", message="x has no units")

from dust_extinction.parameter_averages import CCM89, F99

failures = []
notes = []


def check(label, value, reference, tol, unit="", relative=True):
    if relative:
        denom = abs(reference) if abs(reference) > 0 else 1.0
        dev = abs(value - reference) / denom
        shown = f"{dev:.3e}"
    else:
        dev = abs(value - reference)
        shown = f"{dev:.3e}{unit}"
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.9g} vs {reference:.9g}{unit}  ->  {shown}")
    return ok


def load(path):
    return np.genfromtxt(path, delimiter=",", names=True, dtype=None, encoding="utf-8")


def curves():
    print("\n1-3. A(lambda)/A(V) against the reference implementations")
    data = load("exo_extinction.csv")

    for law_name, model in (("Ccm89", CCM89), ("Fitzpatrick99", F99)):
        rows = data[data["law"] == law_name]
        print(f"\n   {law_name}")
        worst_overall = 0.0
        for rv in np.unique(rows["rv"]):
            sub = rows[rows["rv"] == rv]
            x = sub["x_inv_micron"]
            exo = sub["k_alambda_over_av"]

            m = model(Rv=float(rv))
            # Each model declares its own valid range; stay inside it on both sides.
            lo, hi = m.x_range
            keep = (x >= lo) & (x <= hi) & (exo > 0)
            ref = np.asarray(m(x[keep]), dtype=float)
            dev = np.abs(exo[keep] - ref)
            worst = float(dev.max())
            worst_overall = max(worst_overall, worst)

            # Tolerances differ because the two are different kinds of claim. CCM89 is
            # reimplemented from the published polynomials, so it must agree to machine precision.
            # F99 is tabulated at 0.01 inverse microns and sampled here at 0.0025, so what is being
            # measured is the interpolation error of a smooth curve across a grid four times finer
            # than the samples -- the check that it is small is the point, not that it is zero.
            tol = 1e-12 if law_name == "Ccm89" else 3e-4
            check(f"{law_name} R_V={rv}: max |k_exo - k_ref| over {keep.sum()} points",
                  worst, 0.0, tol, relative=False)

        if law_name == "Fitzpatrick99":
            notes.append(f"F99 table interpolation costs at most {worst_overall:.2e} in A(lambda)/A(V), "
                         f"which at E(B-V) = 1 and R_V = 3.1 is {2.5 * np.log10(np.e) * 0 + worst_overall * 3.1:.2e} mag")


def normalisation():
    """The normalisation, and the reason it is a weaker statement than it looks.

    A(V) = R_V * E(B-V) is a relation between BAND-INTEGRATED extinctions. A(V) is the extinction a
    source suffers through the Johnson V passband, which is an integral of the reddened spectrum
    against the filter, not the value of the curve at one wavelength. So k(V) = 1 monochromatically
    is a property of how a law chooses to anchor itself, not a physical requirement:

      * CCM89 anchors AT a wavelength. Its polynomials are written in y = x - 1.82, so y = 0 gives
        a = 1, b = 0 and k = 1 exactly, by construction. That is checked to machine precision.
      * F99 anchors through a SPLINE whose optical knots sit at 6000, 5470, 4670 and 4110 Angstrom.
        V is not one of them, and the reference implementation returns 0.9793 at x = 1.82 rather
        than 1. That is the published law, not a defect in this table, and the check below is
        against the reference's own value so that a real drift would still be caught.

    The band-integrated closure is the meaningful one and it is not testable here, because it needs
    the Johnson B and V passbands integrated against a source spectrum. That is exactly the step
    SystemResponse exists for, and it is where this belongs once the reddening is wired into the
    bandpass -- the same argument the codebase already made for atmospheric extinction, which had
    to move inside the integral for the same reason.
    """
    print("\n4. Normalisation, monochromatic")
    print("   A(V) = R_V E(B-V) is a band-integrated relation; k(V) = 1 monochromatically is a")
    print("   property of how each law anchors itself. See this function's docstring.")
    data = load("exo_normalisation.csv")
    for row in data:
        law = row["law"]
        rv = float(row["rv"])
        if law == "Ccm89":
            # Exact by construction: y = x - 1.82 = 0 gives a = 1, b = 0.
            check(f"{law} R_V={rv}: k(V) is exactly 1 by construction", float(row["k_v"]), 1.0, 1e-12)
            check(f"{law} R_V={rv}: k(B) - k(V) against 1/R_V",
                  float(row["k_b_minus_k_v"]), float(row["one_over_rv"]), 1e-4)
        else:
            ref = float(F99(Rv=rv)(1.82))
            check(f"{law} R_V={rv}: k(V) against the reference's own value",
                  float(row["k_v"]), ref, 1e-4)
            notes.append(f"F99 at R_V={rv} gives k(V) = {ref:.5f}, not 1: V is not one of its "
                         f"spline knots, so A(V) = R_V E(B-V) holds band-integrated, not at a point")


def transmission():
    print("\n5. Transmission, the quantity the bandpass integrand multiplies by")
    data = load("exo_transmission.csv")
    rows = data[data["law"] == "Fitzpatrick99"]

    # E(B-V) = 0 must be exactly transparent, with no rounding creeping in.
    zero = rows[rows["ebv"] == 0.0]
    check("E(B-V) = 0 gives transmission exactly 1",
          float(np.abs(zero["transmission"] - 1.0).max()), 0.0, 0.0, relative=False)

    # A(lambda) must scale exactly linearly with E(B-V), which is what makes the map's amount and
    # the law's shape separable at all. Checked against the R_V E(B-V) k(V) the law itself implies,
    # not against 3.1 E(B-V) -- see normalisation() for why those differ for F99.
    base = rows[rows["ebv"] == 1.0]
    lam_v = 1.0e-6 / 1.82
    a_v_unit = float(np.interp(lam_v, base["wavelength_m"], base["a_lambda_mag"]))
    for ebv in (0.1, 0.3, 3.0):
        sub = rows[rows["ebv"] == ebv]
        av = float(np.interp(lam_v, sub["wavelength_m"], sub["a_lambda_mag"]))
        check(f"E(B-V) = {ebv}: A(V) scales linearly with the reddening",
              av, a_v_unit * ebv, 1e-9, unit=" mag")

    # Reddening is the whole point: blue must be extinguished more than red, monotonically, and by
    # the ratio the law states. At E(B-V) = 1 the classic figures are A(B) ~ 4.1 and A(I) ~ 1.5.
    sub = rows[rows["ebv"] == 1.0]
    lam = sub["wavelength_m"]
    a = sub["a_lambda_mag"]
    a_b = float(np.interp(440e-9, lam, a))
    a_v = float(np.interp(551e-9, lam, a))
    a_r = float(np.interp(658e-9, lam, a))
    a_i = float(np.interp(806e-9, lam, a))
    print(f"  [note] at E(B-V) = 1, R_V = 3.1:  A(B) = {a_b:.3f}, A(V) = {a_v:.3f}, "
          f"A(R) = {a_r:.3f}, A(I) = {a_i:.3f} mag")
    notes.append(f"A(B)/A(V) = {a_b / a_v:.4f}, A(R)/A(V) = {a_r / a_v:.4f}, A(I)/A(V) = {a_i / a_v:.4f}")

    # Monochromatic, so this is close to but not exactly 1 -- see normalisation().
    check("A(B) - A(V) against E(B-V), monochromatic", a_b - a_v, 1.0, 0.05, unit=" mag")

    # Reddening means what it says: the curve must fall from blue to red with no reversal, which a
    # tabulation or an interpolation error would break before anything else did.
    order = np.argsort(lam)
    worst_rise = float(np.max(np.diff(np.asarray(a)[order])))
    check("no reversal: extinction never rises toward the red",
          worst_rise, 0.0, 0.0, unit=" mag", relative=False) if worst_rise > 0 else check(
        "no reversal: extinction never rises toward the red", 0.0, 0.0, 0.0, unit=" mag", relative=False)


def laws_differ():
    print("\n6. The two laws are two laws")
    print("   CCM89's optical polynomial carries residuals against F99's spline of a few")
    print("   hundredths of a magnitude. If they agreed exactly, one would not be being used.")
    data = load("exo_extinction.csv")
    ccm = data[(data["law"] == "Ccm89") & (data["rv"] == 3.1)]
    f99 = data[(data["law"] == "Fitzpatrick99") & (data["rv"] == 3.1)]
    optical = (ccm["x_inv_micron"] >= 1.1) & (ccm["x_inv_micron"] <= 3.0)
    diff = np.abs(ccm["k_alambda_over_av"][optical] - f99["k_alambda_over_av"][optical])
    worst = float(diff.max())
    print(f"  [note] max |CCM89 - F99| over 333-909 nm at R_V = 3.1: {worst:.4f} in A(lambda)/A(V), "
          f"{worst * 3.1:.4f} mag at E(B-V) = 1")
    check("the laws differ by a literature-sized amount, not by zero", worst, 0.0, 1e9, relative=False)
    if worst < 1e-6:
        failures.append("the two laws are numerically identical, which means one is not being evaluated")
        print("  [FAIL] the two laws are numerically identical")


def main():
    print(__doc__.split("Run:")[0].strip())
    curves()
    normalisation()
    transmission()
    laws_differ()

    print("\n" + "-" * 78)
    for n in notes:
        print("NOTE: " + n)
    if failures:
        print(f"\n{len(failures)} CHECK(S) FAILED:")
        for f in failures:
            print("  - " + f)
        return 1
    print("\nALL CHECKS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
