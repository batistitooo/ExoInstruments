#!/usr/bin/env python3
"""Is a survey cutout LINEAR in flux? Measured against real star magnitudes, not assumed.

A shape map is only a shape map if pixel values are proportional to surface brightness. Several
image services serve data that has been through an asinh or log transfer, and nothing in the FITS
header says so. Packed, such an image flattens every galaxy's core and lifts its outskirts, and
still looks like a galaxy.

The test needs no zero point: aperture photometry on the cutout against catalogue magnitudes must
be a straight line of slope one. A compressed image bends away at the bright end, exactly where a
nucleus lives.

The reference is the Gaia DR3 Synthetic Photometry Catalogue (I/360, Gaia Collaboration 2023):
SDSS-system ugriz synthesised from BP/RP spectra. One all-sky, star-only reference for every
candidate, where the previous Pan-STARRS reference stopped at Dec -30 and included galaxies,
which is what buried the verdict under a magnitude of scatter.

Run (a field inside DESI, SDSS and PS1; add --ra/--dec for others, e.g. Dec -55 for DES):
    ./env/bin/python check_linearity.py
"""

import argparse
import io
import math
import sys
import time

import numpy as np
import requests

HIPS2FITS = "https://alasky.cds.unistra.fr/hips-image-services/hips2fits"
VIZIER_TAP = "https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync"
GSPC = "I/360/syntphot"

CANDIDATES = [
    "CDS/P/DESI-Legacy-Surveys/DR10/g",
    "CDS/P/DESI-Legacy-Surveys/DR10/r",
    "CDS/P/SDSS9/u",
    "CDS/P/SDSS9/g",
    "CDS/P/SDSS9/r",
    "CDS/P/SDSS9/i",
    "CDS/P/SDSS9/z",
    "CDS/P/DES-DR2/g",
    "CDS/P/DES-DR2/r",
    "CDS/P/DES-DR2/i",
    "CDS/P/PanSTARRS/DR1/g",
    "CDS/P/PanSTARRS/DR1/r",
    "CDS/P/PanSTARRS/DR1/i",
    "CDS/P/DSS2/blue",      # photographic: expected to fail, kept as the control
    "CDS/P/DSS2/red",
]

# Survey band letter -> GSPC column. DSS plates are closest to g and r. Columns are quoted in
# ADQL because the table also carries Johnson "Rmag"/"Imag"/"Umag" and bare names are ambiguous.
COLUMN = {"u": "umag", "g": "gmag", "r": "rmag", "i": "imag", "z": "zmag",
          "blue": "gmag", "red": "rmag"}


def fetch(hips, ra, dec, fov, size):
    """Identical request to the packer's fetch_fits, so a verdict here holds there."""
    from astropy.io import fits
    r = requests.get(HIPS2FITS, params={
        "hips": hips, "width": size, "height": size, "fov": fov, "projection": "TAN",
        "coordsys": "icrs", "ra": ra, "dec": dec, "rotation_angle": 0.0, "format": "fits"},
        timeout=600)
    r.raise_for_status()
    return fits.open(io.BytesIO(r.content))[0]


def reference(ra, dec, radius, gmax, retries=3):
    """Every star once, with all five SDSS-system magnitudes, so each band and its colour term
    come from the same query."""
    adql = ("SELECT RA_ICRS, DE_ICRS, \"umag\", \"gmag\", \"rmag\", \"imag\", \"zmag\" "
            "FROM \"%s\" WHERE 1=CONTAINS("
            "POINT('ICRS',RA_ICRS,DE_ICRS),CIRCLE('ICRS',%.6f,%.6f,%.6f)) AND \"gmag\" < %.1f"
            % (GSPC, ra, dec, radius, gmax))
    for attempt in range(retries):
        r = requests.get(VIZIER_TAP, params={"request": "doQuery", "lang": "ADQL",
                                             "format": "csv", "query": adql}, timeout=600)
        if r.status_code == 200:
            break
        time.sleep(3.0 * (attempt + 1))
    r.raise_for_status()
    out = []
    for line in r.text.splitlines()[1:]:
        p = line.split(",")
        if len(p) >= 7:
            try:
                mags = {"umag": float(p[2]) if p[2] else np.nan, "gmag": float(p[3]),
                        "rmag": float(p[4]) if p[4] else np.nan,
                        "imag": float(p[5]) if p[5] else np.nan,
                        "zmag": float(p[6]) if p[6] else np.nan}
                out.append((float(p[0]), float(p[1]), mags))
            except ValueError:
                pass
    return out


def isolated(stars, min_sep_arcsec=12.0):
    """Drop any star with a catalogued neighbour inside the sky annulus."""
    keep = []
    lim = (min_sep_arcsec / 3600.0) ** 2
    for i, (ra, dec, mags) in enumerate(stars):
        c = math.cos(math.radians(dec))
        ok = True
        for j, (ra2, dec2, _) in enumerate(stars):
            if i == j:
                continue
            if ((ra - ra2) * c) ** 2 + (dec - dec2) ** 2 < lim:
                ok = False
                break
        if ok:
            keep.append((ra, dec, mags))
    return keep


def measure(hdu, stars, aperture_arcsec=4.0):
    from astropy.wcs import WCS
    data = np.array(hdu.data, dtype=np.float64)
    nan_fraction = float(np.isnan(data).mean())
    if nan_fraction > 0.6:
        return None, nan_fraction
    w = WCS(hdu.header)
    ny, nx = data.shape
    scale = abs(hdu.header["CDELT1"]) * 3600.0
    rpx = aperture_arcsec / scale
    yy, xx = np.mgrid[0:ny, 0:nx]

    rows = []
    for (sra, sdec, mags) in stars:
        x, y = w.all_world2pix(sra, sdec, 0)
        x, y = float(x), float(y)
        if not (rpx * 4 < x < nx - rpx * 4 and rpx * 4 < y < ny - rpx * 4):
            continue
        d2 = (xx - x) ** 2 + (yy - y) ** 2
        inside = d2 < rpx * rpx
        annulus = (d2 > (2.5 * rpx) ** 2) & (d2 < (4.0 * rpx) ** 2)
        if not annulus.any() or np.isnan(data[inside]).any():
            continue
        local = np.nanmedian(data[annulus])
        flux = np.nansum(data[inside] - local)
        if flux > 0:
            rows.append((mags, -2.5 * math.log10(flux)))
    return rows, nan_fraction


def clipped_fit(g, colour, m, sigma=3.0, rounds=2):
    """m = a*mag + c*(g-r) + b, sigma-clipped; curvature of the residual; bootstrap errors.

    The colour term is not optional: the reference is SDSS-system, DECam and Pan-STARRS bands
    differ from it by a colour-dependent offset, and faint field stars are systematically redder,
    so without it the offset masquerades as curvature. The bootstrap is not optional either: at
    forty stars a fixed curvature threshold flags noise, and the real discriminator, an asinh
    transfer, fails on slope alone (0.4-0.6 against 1.0)."""
    A = np.column_stack([g, colour, np.ones(len(g))])
    keep = np.isfinite(colour)
    coef = None
    for _ in range(rounds):
        coef, *_ = np.linalg.lstsq(A[keep], m[keep], rcond=None)
        resid = m - A @ coef
        keep = keep & (np.abs(resid - np.median(resid[keep])) < sigma * resid[keep].std())
    resid = m - A @ coef
    q = np.polyfit(g[keep], resid[keep], 2)[0]

    rng = np.random.default_rng(0)
    idx = np.where(keep)[0]
    boot_a, boot_q = [], []
    for _ in range(200):
        j = rng.choice(idx, len(idx))
        c2, *_ = np.linalg.lstsq(A[j], m[j], rcond=None)
        boot_a.append(c2[0])
        boot_q.append(np.polyfit(g[j], (m - A @ c2)[j], 2)[0])
    return coef[0], resid[keep].std(), q, int(keep.sum()), np.std(boot_a), np.std(boot_q)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--ra", type=float, default=180.0)
    p.add_argument("--dec", type=float, default=25.0)
    p.add_argument("--fov", type=float, default=0.15)
    p.add_argument("--size", type=int, default=1024)
    p.add_argument("--gmax", type=float, default=19.0)
    p.add_argument("--gmin", type=float, default=15.0, help="brighter than this is likely saturated")
    p.add_argument("--hips", default="", help="test one extra HiPS id as well")
    args = p.parse_args()

    candidates = CANDIDATES + ([args.hips] if args.hips else [])
    print("\n%-34s %5s %14s %8s %16s" % ("survey band", "stars", "slope", "scatter", "curvature"))
    cache = {}
    for hips in candidates:
        band = hips.rstrip("/").split("/")[-1]
        column = COLUMN.get(band, "rmag")
        if "stars" not in cache:
            allstars = reference(args.ra, args.dec, args.fov * 0.45, args.gmax)
            cache["stars"] = isolated(allstars)
            print("  %d stars with gmag < %.1f, %d isolated"
                  % (len(allstars), args.gmax, len(cache["stars"])))
        stars = [s for s in cache["stars"]
                 if np.isfinite(s[2].get(column, np.nan)) and s[2][column] > args.gmin]
        try:
            hdu = fetch(hips, args.ra, args.dec, args.fov, args.size)
        except Exception as exc:                                    # noqa: BLE001
            print("%-34s  fetch failed: %s" % (hips, exc))
            continue
        rows, nan_fraction = measure(hdu, stars)
        if rows is None:
            print("%-34s  not covered here (%.0f%% blank)" % (hips.split("/P/")[-1], nan_fraction * 100))
            continue
        if len(rows) < 20:
            print("%-34s  only %d usable stars" % (hips.split("/P/")[-1], len(rows)))
            continue
        g = np.array([r[0][column] for r in rows])
        # Colour matched to the band: g-r brackets the blue bands, r-i the red ones. A red band
        # fitted with g-r keeps a residual SED term that reads as curvature.
        if column in ("imag", "zmag"):
            colour = np.array([r[0]["rmag"] - r[0]["imag"] for r in rows])
        else:
            colour = np.array([r[0]["gmag"] - r[0]["rmag"] for r in rows])
        m = np.array([r[1] for r in rows])
        a, scatter, q, kept, sa, sq = clipped_fit(g, colour, m)
        bad = abs(a - 1.0) > max(0.06, 3 * sa) or abs(q) > max(0.012, 3 * sq)
        print("%-34s %5d  %6.3f+-%.3f %7.3f  %7.4f+-%.4f%s"
              % (hips.split("/P/")[-1], kept, a, sa, scatter, q, sq,
                 "   <-- NOT LINEAR" if bad else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
