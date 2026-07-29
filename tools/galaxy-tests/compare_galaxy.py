"""Cross-validates ExoInstruments' galaxy profile against SciPy and astropy.

A galaxy is drawn from four catalogued numbers and one profile. Every step between them fails
quietly: b_n slightly wrong scales every galaxy's flux through e^(b_n); the total-flux factor wrong
scales them all uniformly; the effective-radius solve landing on the wrong root gives the right flux
at the wrong SIZE; and a pixel integration that misses the nucleus loses light exactly where the
profile is steepest. None of those throws, and all of them still produce a picture of a galaxy.

References are the ones the formulae are defined by: SciPy's incomplete gamma functions, which is
what b_n IS the inverse of, and astropy's Sersic2D, written independently of this project.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_galaxy.py
"""

import sys

import numpy as np
from scipy.special import gammainc, gammaincinv, gammaln

failures = []
notes = []


def check(label, value, reference, tol, unit=""):
    dev = abs(value - reference)
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.8g} vs {reference:.8g}{unit}  ->  {dev:.3e}{unit}")
    return ok


def special_functions():
    print("\n1. Incomplete gamma and log gamma, against SciPy")
    d = np.genfromtxt("exo_gammafn.csv", delimiter=",", names=True)
    ref_p = gammainc(d["a"], d["x"])
    ref_lg = gammaln(d["a"])

    # Relative where the value is representable, absolute in the deep tail where it underflows.
    with np.errstate(divide="ignore", invalid="ignore"):
        rel = np.where(ref_p > 1e-12, np.abs(d["gammap"] / ref_p - 1.0), np.abs(d["gammap"] - ref_p))
    check(f"P(a,x) over {len(d)} points spanning a=0.2..30, x=1e-3..1e2", float(np.nanmax(rel)), 0.0, 1e-12)
    # lnGamma passes through zero at a = 1 and a = 2, so the comparison is relative where the
    # value is representable and absolute where it is not.
    lg_dev = np.where(np.abs(ref_lg) > 1e-8,
                      np.abs(d["loggamma_a"] / np.where(ref_lg == 0, 1, ref_lg) - 1.0),
                      np.abs(d["loggamma_a"] - ref_lg))
    check("log Gamma(a)", float(np.max(lg_dev)), 0.0, 1e-13)
    notes.append(f"the incomplete gamma matches SciPy to {np.nanmax(rel):.1e} over {len(d)} points")


def sersic():
    print("\n2. b_n and the total-flux factor")
    d = np.genfromtxt("exo_sersic.csv", delimiter=",", names=True)
    n = d["n"]

    # b_n is DEFINED by P(2n, b_n) = 1/2, so the reference is the inverse of that, not a series.
    ref_bn = gammaincinv(2 * n, 0.5)
    check(f"b_n over {len(d)} indices from 0.3 to 8, relative",
          float(np.max(np.abs(d["bn"] / ref_bn - 1.0))), 0.0, 1e-12)

    # And the series everyone quotes, reported rather than asserted -- this is what using it
    # instead of the exact inversion would have cost.
    series = 2 * n - 1.0 / 3.0 + 4.0 / (405 * n) + 46.0 / (25515 * n**2) + 131.0 / (1148175 * n**3)
    worst = float(np.max(np.abs(series / ref_bn - 1.0)))
    print(f"  [note] the Ciotti & Bertin series would be off by up to {worst:.2e} relative "
          f"(worst at n = {n[np.argmax(np.abs(series / ref_bn - 1.0))]:.2f}), which enters the flux through e^b_n")

    ref_factor = 2 * np.pi * n * np.exp(ref_bn + gammaln(2 * n) - 2 * n * np.log(ref_bn))
    check("total-flux factor 2*pi*n*e^b*Gamma(2n)/b^2n, relative",
          float(np.max(np.abs(d["total_flux_factor"] / ref_factor - 1.0))), 0.0, 1e-12)

    # Half the light inside R_e is the definition; the solve has to reproduce it.
    check("radius holding half the light is exactly R_e",
          float(np.max(np.abs(d["r_half"] - 1.0))), 0.0, 1e-9)

    notes.append(f"b_n matches SciPy's gammaincinv to {np.max(np.abs(d['bn'] / ref_bn - 1.0)):.1e} "
                 f"over 0.3 <= n <= 8")

    print("\n2b. The profile itself, against astropy's Sersic2D")
    p = np.genfromtxt("exo_profile.csv", delimiter=",", names=True)
    try:
        from astropy.modeling.models import Sersic2D
    except ImportError:
        print("  [note] astropy not installed, skipped")
        return
    worst_rel = 0.0
    for nn in np.unique(p["n"]):
        sub = p[p["n"] == nn]
        model = Sersic2D(amplitude=1.0, r_eff=1.0, n=float(nn), x_0=0.0, y_0=0.0, ellip=0.0, theta=0.0)
        ref = model(sub["r_over_re"], np.zeros_like(sub["r_over_re"]))
        # Ours is carried as a magnitude difference from mu_e; astropy's is an intensity ratio.
        ours = 10 ** (-0.4 * sub["mu_minus_mu_e"])
        worst_rel = max(worst_rel, float(np.max(np.abs(ours / ref - 1.0))))
    check("surface brightness against astropy Sersic2D, relative", worst_rel, 0.0, 1e-9)

    ref_enc = gammainc(2 * p["n"], gammaincinv(2 * p["n"], 0.5) * p["r_over_re"] ** (1.0 / p["n"]))
    check("enclosed fraction, absolute", float(np.max(np.abs(p["enclosed"] - ref_enc))), 0.0, 1e-12)


def effective_radius():
    print("\n3. The effective radius that reconciles the total magnitude with D25")
    d = np.genfromtxt("exo_re.csv", delimiter=",", names=True)
    solved = np.isfinite(d["re_arcsec"])

    # By construction: feed R_e back in and the isophote must land exactly on 25 mag/arcsec^2.
    check(f"mu(D25/2) comes back at 25.000 for all {int(solved.sum())} solved cases",
          float(np.max(np.abs(d["mu_at_r25"][solved] - 25.0))), 0.0, 1e-6, " mag")

    # The physical branch: the half-light radius must lie INSIDE the isophote that defines the edge.
    inside = d["re_arcsec"][solved] < d["r25_arcsec"][solved]
    check("every solution is on the compact branch, R_e < D25/2",
          float(np.count_nonzero(~inside)), 0.0, 0.0)

    # And the isophote must enclose most of the light, which is what "the galaxy's edge" means.
    enc = d["enclosed_at_r25"][solved]
    print(f"  [note] the D25 isophote encloses {enc.min()*100:.0f}% to {enc.max()*100:.0f}% "
          f"of the total light (median {np.median(enc)*100:.0f}%)")

    unsolved = int(np.count_nonzero(~solved))
    print(f"  [note] {unsolved} of {len(d)} grid combinations have no solution: the total magnitude "
          f"is too faint to reach 25 mag/arcsec^2 anywhere at that size, which is a real statement "
          f"about the pair and not a numerical failure")
    notes.append(f"the R_e solve reproduces mu = 25 at D25/2 to "
                 f"{np.max(np.abs(d['mu_at_r25'][solved] - 25.0)):.1e} mag on the compact branch")


def deposit():
    print("\n4. The renderer, against the analytic enclosed flux")
    d = np.genfromtxt("exo_deposit.csv", delimiter=",", names=True)

    rel = np.abs(d["deposited_e"] / d["analytic_enclosed"] - 1.0)
    worst = int(np.argmax(rel))
    check(f"deposited electrons vs total x enclosed fraction, {len(d)} shapes, relative",
          float(rel.max()), 0.0, 2e-3)
    print(f"  [note] worst at n = {d['n'][worst]:.0f}, R_e = {d['re_px'][worst]:.0f} px, "
          f"b/a = {d['axis_ratio'][worst]:.2f} -- the compact high-index case, where the most light "
          f"sits in the fewest pixels")

    # Centroid: the profile must land where it was asked to, at a sub-pixel offset. Split on the
    # same criterion as the axis ratio below -- a profile whose minor axis is under a pixel across
    # cannot have its centre recovered from a pixel-integrated image to better than a fraction of a
    # pixel, and neither could a real detector.
    off = np.hypot(d["centroid_x"], d["centroid_y"])
    resolved_c = d["re_px"] * d["axis_ratio"] >= 2.0
    check(f"centroid lands on the requested centre, {int(resolved_c.sum())} resolved shapes",
          float(off[resolved_c].max()), 0.0, 0.02, " px")
    print(f"  [note] median over all 81 shapes {np.median(off):.4f} px; where the minor axis spans "
          f"under two pixels it reaches {off[~resolved_c].max():.3f} px")

    # And the ellipse must have the axis ratio it was given. Second moments of a Sersic profile are
    # dominated by the wings, so the ratio is the test rather than either moment alone.
    measured = d["second_moment_minor"] / d["second_moment_major"]
    round_shapes = d["axis_ratio"] > 0.999
    check("a circular profile measures circular",
          float(np.max(np.abs(measured[round_shapes] - 1.0))), 0.0, 5e-3)

    # Split by whether the MINOR axis is resolved at all. At b/a = 0.25 with R_e = 3 px the profile
    # is 0.75 px across the short way, so the measured moment is inflated by the pixel grid itself
    # -- that is the detector, not the renderer, and a real instrument would record the same thing.
    rel_q = np.abs(measured / d["axis_ratio"] - 1.0)
    resolved = d["re_px"] * d["axis_ratio"] >= 2.0
    check(f"measured axis ratio matches the catalogued one, {int(resolved.sum())} resolved shapes",
          float(rel_q[resolved].max()), 0.0, 5e-3)
    print(f"  [note] where the minor axis spans under two pixels ({int((~resolved).sum())} shapes) "
          f"the grid inflates it by up to {rel_q[~resolved].max()*100:.0f}%, which is sampling and "
          f"not the profile")
    notes.append(f"the renderer conserves flux to {rel.max():.1e} and reproduces the requested "
                 f"axis ratio to {rel_q.max():.1e} over {len(d)} shapes")


def main():
    print(__doc__.split("Run:")[0].strip())
    special_functions()
    sersic()
    effective_radius()
    deposit()

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
