"""Measures ExoInstruments' own synthetic frames with photutils, and asks whether the pipeline gets
back the magnitudes it put in -- and whether its scatter is the one CcdEquation predicts.

WHY THIS IS DIFFERENT FROM THE OTHER HARNESSES. Every other check in tools/ compares one mechanism
against a reference. This one checks that the mechanisms COMPOSE. A zero point that disagrees with
the electron counts, a PSF that quietly loses flux, a gain applied twice, an aperture correction
taken from the wrong profile -- none of those is visible to a test that looks at one stage alone,
and all of them show up here.

THE THIRD SECTION IS THE POINT. CcdEquation.cs says outright that the imaging half and the transit
half of this codebase "disagreed about what an instrument is". Section 3 renders 64 noise
realisations of one field, measures the real scatter of real aperture photometry, and compares it
with the precision CcdEquation predicts for the same observation. Agreement means the two halves
have been reconciled; disagreement localises which of them is wrong.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python roundtrip.py

Exit code 0 when every check passes, 1 otherwise.
"""

import csv
import glob
import math
import sys

import numpy as np
from scipy import stats

from photutils.aperture import CircularAperture, CircularAnnulus, ApertureStats, aperture_photometry

failures = []
notes = []


def check(label, value, reference, tol, unit="", relative=True):
    if relative:
        denom = abs(reference) if abs(reference) > 0 else 1.0
        dev = abs(value - reference) / denom
        shown = f"{dev * 100:.4f}%"
    else:
        dev = abs(value - reference)
        shown = f"{dev:.4g}{unit}"
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.6g} vs {reference:.6g}{unit}  ->  {shown}")
    return ok


def read_meta(path="meta.csv"):
    out = {}
    for row in csv.reader(open(path)):
        if len(row) != 2 or row[0] == "key":
            continue
        try:
            out[row[0]] = float(row[1])
        except ValueError:
            out[row[0]] = row[1]
    return out


META = read_meta()
TRUTH = np.genfromtxt("truth.csv", delimiter=",", names=True)
PRED = np.genfromtxt("ccd_equation_prediction.csv", delimiter=",", names=True)
FRAMES = sorted(glob.glob("frames/frame_*.u16"))
SIZE = int(META["size_px"])


def load_frame(path):
    """One frame in electrons. The pedestal is not subtracted here: the local sky annulus removes it
    along with the sky, which is what a real reduction does and what makes the bias level
    unmeasurable from a science frame alone."""
    adu = np.fromfile(path, dtype="<u2").reshape(SIZE, SIZE).astype(float)
    return adu * META["electrons_per_adu"]


# --------------------------------------------------------------- 1. the noise deviates themselves

def noise_samplers():
    """Core.NoiseSampler against SciPy.

    These are the two most consequential numerical routines in the pipeline and, until they were
    moved out of the Unity layer, the only ones no harness could reach. Every electron count and
    every noise figure the mod reports rests on them being exactly the claimed distributions.
    """
    print("\n1. Core.NoiseSampler against SciPy")
    print("   Poisson by chi-square on the pmf, Gaussian by Kolmogorov-Smirnov.")
    print("   The 9.9 / 10.0 / 10.1 means bracket PtrsThreshold, where the algorithm switches.")

    data = np.loadtxt("noise_samples_poisson.csv", delimiter=",", skiprows=1)
    for lam in np.unique(data[:, 0]):
        s = data[data[:, 0] == lam, 1]

        # Mean and variance both equal lambda for a Poisson, and a wrong sampler usually breaks
        # one without the other.
        check(f"Poisson lambda={lam:g}: sample mean", s.mean(), lam, 0.01)
        check(f"Poisson lambda={lam:g}: sample variance", s.var(ddof=1), lam, 0.03)

        # And the shape. Binned chi-square against the exact pmf, with the tails pooled so no
        # bin is under-populated; at very large lambda the counts are effectively continuous and
        # a KS test against the normal limit is the meaningful statement instead.
        if lam <= 1000.0:
            lo, hi = int(max(0, lam - 6 * math.sqrt(lam) - 4)), int(lam + 6 * math.sqrt(lam) + 8)
            edges = np.arange(lo, hi + 2)
            observed, _ = np.histogram(s, bins=edges)
            expected = stats.poisson.pmf(edges[:-1], lam) * len(s)
            keep = expected >= 5
            observed, expected = observed[keep], expected[keep]
            expected = expected * observed.sum() / expected.sum()
            chi2 = ((observed - expected) ** 2 / expected).sum()
            p = 1.0 - stats.chi2.cdf(chi2, df=len(observed) - 1)
            check(f"Poisson lambda={lam:g}: chi-square p-value", p, 0.5, 0.499, relative=False)
        else:
            z = (s - lam) / math.sqrt(lam)
            p = stats.kstest(z, "norm").pvalue
            check(f"Poisson lambda={lam:g}: KS p-value against its normal limit", p, 0.5,
                  0.499, relative=False)

    g = np.loadtxt("noise_samples_gaussian.csv", delimiter=",", skiprows=1)[:, 1]
    check("Gaussian: sample mean", g.mean(), 0.0, 0.01, relative=False)
    check("Gaussian: sample sigma", g.std(ddof=1), 1.2, 0.01)
    check("Gaussian: KS p-value", stats.kstest(g / 1.2, "norm").pvalue, 0.5, 0.499, relative=False)


# ------------------------------------------------------------------------- 2. do the magnitudes
#                                                                              come back?

def photometry(frame, radius_px, positions):
    """Aperture sum minus a local background, in the 2r-3r annulus CcdEquation itself assumes.

    The background is the annulus MEAN, not its median, and the distinction is not stylistic.
    Merline & Howell's (1 + n_pix/n_B) factor is derived for a sky level estimated by averaging n_B
    pixels; the median of the same pixels has variance pi/2 times larger, so measuring with a median
    and comparing against the equation would attribute a 57% inflation of the background term to the
    pipeline when it belongs to the estimator. It also matters at these levels because the annulus
    values are coarsely quantised -- the sky is 8.7 ADU per pixel at K = 4.03 e-/ADU -- and the
    median of a coarsely quantised sample is itself nearly quantised, while the mean of 575 of them
    is not.
    """
    ap = CircularAperture(positions, r=radius_px)
    ann = CircularAnnulus(positions, r_in=2.0 * radius_px, r_out=3.0 * radius_px)
    bkg = ApertureStats(frame, ann).mean
    raw = aperture_photometry(frame, ap)["aperture_sum"].value
    return raw - bkg * ap.area


def round_trip():
    print("\n2. Injected magnitudes, recovered by photutils")

    scale = META["plate_scale_arcsec_px"]
    fwhm_px = META["kernel_fwhm_arcsec"] / scale
    positions = np.column_stack((TRUTH["x_px"], TRUTH["y_px"]))

    # The aperture that holds the whole star BY CONSTRUCTION: the kernel is normalised to unit sum
    # over its own support, and beyond that radius it is exactly zero. So this measures total flux
    # without needing an aperture correction, which is what makes the zero-point test below a test
    # of the zero point rather than of the PSF profile.
    wide_r = META["total_flux_radius_px"]

    # Averaged over every realisation, so the encircled-energy measurement below is limited by the
    # PSF rather than by one frame's noise.
    stack = np.mean([load_frame(f) for f in FRAMES], axis=0)
    wide_clean = photometry(stack, wide_r, positions)
    narrow_r = META["ccd_aperture_radius_arcsec"] / scale
    narrow_clean = photometry(stack, narrow_r, positions)

    ee_measured = float(np.median(narrow_clean / wide_clean))
    ee_gaussian = META["ccd_enclosed_energy"]
    print(f"  [note] encircled energy at {META['ccd_aperture_radius_arcsec'] / META['kernel_fwhm_arcsec']:.2f} FWHM:"
          f" measured {ee_measured:.4f}, CcdEquation's Gaussian {ee_gaussian:.4f}"
          f" ({100 * (ee_gaussian / ee_measured - 1):+.1f}%)")
    notes.append(f"CcdEquation's Gaussian encircled energy is {100 * (ee_gaussian / ee_measured - 1):+.1f}% "
                 f"against the real Airy-convolved-Kolmogorov kernel ({ee_gaussian:.4f} vs {ee_measured:.4f})")

    # Instrumental magnitude through the pipeline's own zero point, which is quoted for a FLAT
    # source spectrum. The injected stars are solar, so a colour term separates the two, and its
    # size is predicted exactly by the two effective widths.
    adu_per_second = wide_clean / META["electrons_per_adu"] / META["exptime_s"]
    recovered = -2.5 * np.log10(adu_per_second) + META["magzero"]
    residual = recovered - TRUTH["v_mag"]

    colour = 2.5 * math.log10(META["effective_width_flat_A"] / META["effective_width_solar_A"])
    print(f"  [note] expected flat-vs-solar colour term: {colour:+.4f} mag")

    check("zero point: median residual, colour term removed",
          float(np.median(residual)) - colour, 0.0, 0.005, unit=" mag", relative=False)
    check("zero point: residual spread across 7 magnitudes",
          float(np.ptp(residual)), 0.0, 0.02, unit=" mag", relative=False)

    # A linear fit of recovered against injected: the slope is the one number that would expose a
    # flux non-linearity anywhere in the chain.
    slope = np.polyfit(TRUTH["v_mag"], recovered, 1)[0]
    check("zero point: recovered-vs-injected slope", slope, 1.0, 2e-3)

    # Flux conservation through the PSF: the wide aperture should hold essentially the whole star.
    total_electrons = TRUTH["electrons_total"]
    captured = wide_clean / total_electrons
    check("PSF flux conservation in a 4-FWHM aperture", float(np.median(captured)), 1.0, 0.02)

    return ee_measured, fwhm_px, positions


# ------------------------------------------------- 3. does the scatter match the CCD equation?

def scatter_vs_ccd_equation(ee_measured, fwhm_px, positions):
    print("\n3. Measured photometric scatter against CcdEquation")
    print(f"   {len(FRAMES)} independent noise realisations of one field, measured at the aperture")
    print("   CcdEquation itself chooses (0.68 FWHM), with its own 2r-3r sky annulus.")

    scale = META["plate_scale_arcsec_px"]
    narrow_r = META["ccd_aperture_radius_arcsec"] / scale

    fluxes = np.array([photometry(load_frame(f), narrow_r, positions) for f in FRAMES])
    measured_sigma = fluxes.std(axis=0, ddof=1) / fluxes.mean(axis=0)

    # The standard error on a standard deviation from N samples is sigma/sqrt(2(N-1)), which is
    # 8.9% at 64 realisations. Tolerances below are set from that, not from taste.
    se = 1.0 / math.sqrt(2.0 * (len(FRAMES) - 1))
    print(f"   sampling error on each measured sigma: {100 * se:.1f}% at {len(FRAMES)} realisations")

    print(f"\n   {'V':>5} {'measured sigma':>15} {'predicted':>12} {'ratio':>8} {'corrected':>11} {'ratio':>8}")
    ratios_shipped, ratios_corrected = [], []
    for i, v in enumerate(PRED["v_mag"]):
        pred = PRED["predicted_relative_sigma"][i]

        # The same equation with the MEASURED encircled energy in place of its Gaussian one. This
        # separates "is the equation right" from "is its aperture-correction assumption right".
        corrected = recompute_sigma(TRUTH["electrons_total"][i] * ee_measured)

        ratios_shipped.append(measured_sigma[i] / pred)
        ratios_corrected.append(measured_sigma[i] / corrected)
        print(f"   {v:5.1f} {measured_sigma[i]:15.6f} {pred:12.6f} {measured_sigma[i] / pred:8.3f}"
              f" {corrected:11.6f} {measured_sigma[i] / corrected:8.3f}")

    check("median measured/predicted ratio, CcdEquation as shipped",
          float(np.median(ratios_shipped)), 1.0, 3 * se)
    check("median measured/predicted ratio, with the real encircled energy",
          float(np.median(ratios_corrected)), 1.0, 3 * se)
    check("trend of the ratio across 7 magnitudes (sky-limited to source-limited)",
          float(np.ptp(ratios_corrected)), 0.0, 4 * se, relative=False)


def recompute_sigma(star_electrons_in_aperture):
    """CcdEquation.RelativeFluxSigma, re-evaluated here with a different signal term.

    Reimplemented rather than re-dumped from C# on purpose: it is the published Merline & Howell
    equation, and writing it independently means section 3 compares the mod against the equation
    rather than against itself.
    """
    n_pix = META["ccd_aperture_pixels"]
    n_b = META["ccd_background_pixels"]
    per_pixel = (META["sky_electrons_per_px"] + META["dark_electrons_per_px"]
                 + META["read_noise_e"] ** 2 + META["electrons_per_adu"] ** 2 / 12.0)
    variance = star_electrons_in_aperture + n_pix * (1.0 + n_pix / n_b) * per_pixel
    return math.sqrt(variance) / star_electrons_in_aperture


def main():
    print(__doc__.split("Run:")[0].strip())
    print(f"\n{META['instrument']} + {META['camera']}, {META['exptime_s']:.0f} s, "
          f"{META['plate_scale_arcsec_px']:.4f} arcsec/px, seeing {META['seeing_fwhm_arcsec']:.2f} arcsec, "
          f"sky {META['sky_vmag_arcsec2']:.1f} mag/arcsec2")

    noise_samplers()
    ee, fwhm_px, positions = round_trip()
    scatter_vs_ccd_equation(ee, fwhm_px, positions)

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
