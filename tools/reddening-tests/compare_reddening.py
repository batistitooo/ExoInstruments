"""Checks the reddened-star spectrum, and measures what it corrects.

THE ERROR IT CORRECTS. A catalogue colour is an OBSERVED colour. Gaia measures reddened photometry
and GaiaPhotometry converts it without dereddening anything, so a hot star behind dust and an
intrinsically cool star arrive at the photometry indistinguishable -- and the pipeline modelled both
as the cool one, integrating a Planck curve at a temperature the star does not have. Given E(B-V)
the two separate: deredden the colour to get the real photosphere, and put the extinction curve into
the integrand as a shape.

WHY IT IS NOT DOUBLE COUNTING, stated as a testable claim rather than an argument. The bandpass
integrand is normalised at Johnson V, so the observed magnitude sets the flux and the integrand
carries only a shape. The extinction factor is written normalised at V too, so it is exactly 1
there. Section 2 checks that it is exactly 1, and section 3 checks that with no reddening estimate
the result is bit-identical to what the pipeline produced before.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_reddening.py
"""

import sys
import warnings

import numpy as np

warnings.filterwarnings("ignore", message="x has no units")
from dust_extinction.parameter_averages import F99

RV = 3.1
JOHNSON_V_M = 5556e-10   # StellarPhotometry.JohnsonVWavelengthMeters

failures = []
notes = []


def check(label, value, reference, tol, unit=""):
    dev = abs(value - reference)
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.6g} vs {reference:.6g}{unit}  ->  {dev:.3e}{unit}")
    return ok


def load(path):
    return np.genfromtxt(path, delimiter=",", names=True, dtype=None, encoding="utf-8")


def transmission():
    """The new factor, against dust_extinction."""
    print("\n1. The normalised extinction factor, against dust_extinction")
    d = load("exo_reddening_integrand_fors2.csv")

    model = F99(Rv=RV)
    x = 1e-6 / d["wavelength_m"]
    lo, hi = model.x_range
    keep = (x >= lo) & (x <= hi)

    k_lambda = np.asarray(model(x[keep]), dtype=float)
    k_v = float(model(1e-6 / JOHNSON_V_M))
    ref = 10.0 ** (-0.4 * RV * d["ebv"][keep] * (k_lambda - k_v))

    # Relative, because the factor spans two orders of magnitude across the band: it suppresses
    # the blue and enhances the red, both relative to V.
    dev = float(np.abs(d["normalised_transmission"][keep] / ref - 1.0).max())
    check(f"max relative deviation over {keep.sum()} samples at two reddenings", dev, 0.0, 2e-4)

    # The direction, stated rather than implied: blue is suppressed relative to V, red is enhanced.
    blue = d[(d["ebv"] == 2.0) & (np.abs(d["wavelength_m"] - 400e-9) < 3e-9)]
    red = d[(d["ebv"] == 2.0) & (np.abs(d["wavelength_m"] - 800e-9) < 3e-9)]
    print(f"  [note] at E(B-V) = 2: 400 nm keeps {float(blue['normalised_transmission'][0]):.4f} of its "
          f"V-relative flux, 800 nm keeps {float(red['normalised_transmission'][0]):.3f}")
    check("blue is suppressed relative to V",
          1.0 if float(blue["normalised_transmission"][0]) < 1.0 else 0.0, 1.0, 0.0)
    check("red is enhanced relative to V",
          1.0 if float(red["normalised_transmission"][0]) > 1.0 else 0.0, 1.0, 0.0)


def anchor():
    """The V normalisation, which is the no-double-counting claim."""
    print("\n2. The factor is exactly 1 at Johnson V, at every reddening")
    for tag in ("rc20", "fors2"):
        d = load(f"exo_reddening_anchor_{tag}.csv")
        worst = float(np.abs(d["transmission_at_v"] - 1.0).max())
        check(f"{tag}: max departure from 1 at V over {len(d)} reddenings", worst, 0.0, 0.0)

    notes.append("the extinction factor is exactly 1 at V, so the observed magnitude still sets the "
                 "flux and nothing is attenuated twice")


def inert_without_an_estimate():
    """With no reddening the new path must reproduce the old one bit for bit."""
    print("\n3. With E(B-V) = 0 the result is unchanged")
    for tag in ("rc20", "fors2"):
        d = load(f"exo_reddening_error_{tag}.csv")
        zero = d[d["ebv"] == 0.0]
        worst = float(np.abs(zero["ratio"] - 1.0).max())
        check(f"{tag}: max departure from 1 over {len(zero)} temperatures", worst, 0.0, 1e-15)


def magnitude_of_the_error():
    """What the correction is worth, which is the point of doing it."""
    print("\n4. What the old model got wrong")
    print("   Rows where the observed colour stays inside Ballesteros' published range, so the")
    print("   comparison is between two real temperatures rather than against a fallback.")

    for tag, band in (("rc20", "2600 A Luminance"), ("fors2", "7700 A unfiltered")):
        d = load(f"exo_reddening_error_{tag}.csv")
        inrange = (d["observed_bv"] >= -0.5) & (d["observed_bv"] <= 2.5) & (d["ebv"] > 0)
        sub = d[inrange]
        worst_i = int(np.argmax(np.abs(sub["mag_error"])))
        print(f"\n   {tag} ({band}), {inrange.sum()} usable cases")
        print(f"     median |error| {np.median(np.abs(sub['mag_error'])) * 1000:6.1f} mmag")
        print(f"     worst  |error| {abs(sub['mag_error'][worst_i]) * 1000:6.1f} mmag "
              f"at Teff = {sub['intrinsic_teff_k'][worst_i]:.0f} K, E(B-V) = {sub['ebv'][worst_i]:.2f}")
        notes.append(f"{tag}: the old model is wrong by up to "
                     f"{abs(sub['mag_error'][worst_i]) * 1000:.0f} mmag on a star whose observed "
                     f"colour is still in range")

    # The physical expectation, which is the check that the numbers are not noise: a wider band
    # gives a shape error more room, so FORS2 must be worse than the RC20 at matched conditions.
    rc = load("exo_reddening_error_rc20.csv")
    fo = load("exo_reddening_error_fors2.csv")
    sel = lambda d: d[(d["ebv"] == 1.0) & (d["intrinsic_teff_k"] == 8000)]["mag_error"][0]
    print(f"\n  [note] 8000 K star at E(B-V) = 1: RC20 {sel(rc) * 1000:.0f} mmag, "
          f"FORS2 {sel(fo) * 1000:.0f} mmag")
    check("a wider band suffers more, as a shape error must",
          1.0 if abs(sel(fo)) > abs(sel(rc)) else 0.0, 1.0, 0.0)


def cache():
    """The per-frame memo's quantisation, and whether the sharing actually works."""
    print("\n5. The per-frame cache")
    d = load("exo_reddening_cache.csv")
    rel = np.abs(d["width_cached_a"] / d["width_exact_a"] - 1.0)
    check(f"max relative quantisation error over {len(d)} random stars", float(rel.max()), 0.0, 5e-3)
    bins = len(np.unique(np.round(d["ebv"] / 0.01).astype(int)))
    print(f"  [note] one sight line, E(B-V) = {d['ebv'].mean():.2f} +- {d['ebv'].std():.3f}, "
          f"{len(d)} stars over 3000-30000 K: {bins} reddening bins, "
          f"{int(d['evaluations'][-1])} quadratures, then every further star is a table lookup")
    notes.append(f"a one-sight-line field of {len(d)} stars needs {bins} reddening tables; "
                 f"the per-star cost after that is two interpolations")


def main():
    print(__doc__.split("Run:")[0].strip())
    transmission()
    anchor()
    inert_without_an_estimate()
    magnitude_of_the_error()
    cache()

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
