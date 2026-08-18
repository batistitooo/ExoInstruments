#!/usr/bin/env python3
"""Is a candidate H-alpha survey CALIBRATED, and is it linear? Measured, not taken on trust.

The mod renders diffuse emission in rayleighs, from Finkbeiner (2003)'s WHAM-calibrated all-sky
composite with SHASSA (Gaustad et al. 2001) fine structure over the south. Both are published and
both state the unit. A candidate that claims rayleighs can therefore be checked directly: smoothed
to the reference's own beam, the two must agree pixel for pixel with slope one. That is the same
test pack_shassa_patches.py already applies to SHASSA, run against a new survey instead.

Why this is the right test rather than a linearity fit against stars. A continuum-subtracted
narrowband map has no stars left in it by construction, so the star-photometry test used for the
galaxy shape maps has nothing to measure. What CAN be measured is the one thing that matters here:
does a rayleigh in this survey equal a rayleigh in the published one?

Run:
    ./env/bin/python check_calibration.py
    ./env/bin/python check_calibration.py --survey simg.de/P/NSNS/DR0_2/oiii --reference none
"""

import argparse
import io
import math
import sys

import numpy as np
import requests

HIPS2FITS = "https://alasky.cds.unistra.fr/hips-image-services/hips2fits"

# Published references, both stating rayleighs.
FINKBEINER = "CDS/P/Finkbeiner"      # Finkbeiner 2003, ApJS 146, 407: WHAM/VTSS/SHASSA composite
SHASSA = "CDS/P/SHASSA/H"            # Gaustad et al. 2001, PASP 113, 1326
VTSS = "CDS/P/VTSS/HaCC"             # Dennison, Simonetti & Topasna 1998, PASA 15, 147

FINKBEINER_BEAM_ARCMIN = 6.0

# Fields chosen to span declination, galactic latitude and brightness: bright plane, faint high
# latitude, north and south, so a calibration that only holds where the signal is strong fails.
FIELDS = [
    ("Veil (Cygnus loop)",      312.70,  31.20),
    ("North America nebula",    314.75,  44.37),
    ("California nebula",        60.90,  36.42),
    ("Rosette",                  98.00,   4.95),
    ("Orion",                    83.82,  -5.39),
    ("Lagoon",                  271.00, -24.38),
    ("Carina",                  161.25, -59.87),
    ("high latitude north",     195.00,  60.00),
    ("high latitude south",      30.00, -60.00),
    ("anticentre faint",         75.00,  10.00),
]


def grab(hips, ra, dec, fov_deg, size):
    from astropy.io import fits
    r = requests.get(HIPS2FITS, params={
        "hips": hips, "width": size, "height": size, "fov": fov_deg, "projection": "TAN",
        "coordsys": "icrs", "ra": ra, "dec": dec, "rotation_angle": 0.0, "format": "fits"},
        timeout=900)
    r.raise_for_status()
    return np.array(fits.open(io.BytesIO(r.content))[0].data, dtype=np.float64)


def smooth_to(image, pixel_arcmin, target_beam_arcmin):
    """Gaussian-smooth to the reference's beam, so the two are compared at one resolution."""
    from scipy.ndimage import gaussian_filter
    if target_beam_arcmin <= pixel_arcmin:
        return image
    fwhm_px = target_beam_arcmin / pixel_arcmin
    return gaussian_filter(image, fwhm_px / (2.0 * math.sqrt(2.0 * math.log(2.0))))


def regress(x, y, sigma=3.0, rounds=3):
    """Slope through the origin plus offset, sigma-clipped. Rayleighs against rayleighs."""
    keep = np.isfinite(x) & np.isfinite(y)
    a = b = float("nan")
    for _ in range(rounds):
        if keep.sum() < 20:
            return float("nan"), float("nan"), 0, float("nan")
        a, b = np.polyfit(x[keep], y[keep], 1)
        resid = y - (a * x + b)
        s = resid[keep].std()
        if not (s > 0):
            break
        keep = keep & (np.abs(resid - np.median(resid[keep])) < sigma * s)
    r = np.corrcoef(x[keep], y[keep])[0, 1]
    return a, b, int(keep.sum()), r


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--survey", default="simg.de/P/NSNS/DR0_2/halpha")
    p.add_argument("--reference", default=FINKBEINER)
    p.add_argument("--reference-beam", type=float, default=FINKBEINER_BEAM_ARCMIN)
    p.add_argument("--survey-pixel-arcsec", type=float, default=6.44)
    p.add_argument("--fov", type=float, default=2.0)
    p.add_argument("--size", type=int, default=400)
    args = p.parse_args()

    print(f"\ncandidate: {args.survey}")
    print(f"reference: {args.reference}, beam {args.reference_beam}'")
    print(f"{'field':24} {'N':>6} {'slope':>8} {'offset':>9} {'r':>7}   verdict")
    print("-" * 78)

    slopes = []
    for name, ra, dec in FIELDS:
        try:
            cand = grab(args.survey, ra, dec, args.fov, args.size)
            ref = grab(args.reference, ra, dec, args.fov, args.size)
        except Exception as exc:                                    # noqa: BLE001
            print(f"{name:24}  fetch failed: {exc}")
            continue
        if not np.isfinite(cand).any():
            print(f"{name:24}  not covered by the candidate")
            continue

        pixel_arcmin = args.fov * 60.0 / args.size
        cand_s = smooth_to(np.nan_to_num(cand, nan=np.nanmedian(cand)),
                           pixel_arcmin, args.reference_beam)
        # Both now at the reference's resolution; compare only where both are real.
        m = np.isfinite(ref) & np.isfinite(cand) & (ref > 0.0)
        a, b, n, r = regress(ref[m].ravel(), cand_s[m].ravel())
        ok = abs(a - 1.0) < 0.25 and r > 0.8
        slopes.append(a)
        print(f"{name:24} {n:6d} {a:8.3f} {b:9.2f} {r:7.3f}   {'ok' if ok else '<-- OFF'}")

    if slopes:
        arr = np.array([s for s in slopes if np.isfinite(s)])
        print(f"\nslope over {len(arr)} fields: median {np.median(arr):.3f}, "
              f"scatter {arr.std():.3f}")
        print("A survey calibrated in the same unit gives slope 1. A survey in its own arbitrary")
        print("units gives a consistent slope that is not 1 (rescalable). A survey that is not")
        print("linear gives a slope that CHANGES from a bright field to a faint one.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
