#!/usr/bin/env python3
"""Is a survey cutout LINEAR in flux? Measured against Gaia, not assumed.

WHY THIS EXISTS. A shape map is only a shape map if the pixel values are proportional to surface
brightness. Several of the image services that serve survey data do not promise that: a HiPS can be
generated from data that has already been through an asinh or logarithmic transfer, and nothing in
the FITS header says so. Packing such an image would flatten every galaxy's core and lift its
outskirts, and the result would still look like a galaxy.

The test needs no zero point and no exposure time. Gaia DR3 gives real magnitudes for the stars in
the field; aperture photometry on the image gives instrumental ones. If the image is linear the two
are related by a straight line of slope one, whatever the units are. An asinh-compressed image bends
away from it at the bright end, which is exactly where a galaxy's nucleus lives.

Run:
    ./env/bin/python check_linearity.py --ra 202.4696 --dec 47.1952 --fov 0.15
"""

import argparse
import io
import math
import sys

import numpy as np
import requests

HIPS2FITS = "https://alasky.cds.unistra.fr/hips-image-services/hips2fits"
VIZIER_TAP = "https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync"

CANDIDATES = [
    "CDS/P/DESI-Legacy-Surveys/DR10/g",
    "CDS/P/DESI-Legacy-Surveys/DR10/r",
    "CDS/P/PanSTARRS/DR1/g",
    "CDS/P/PanSTARRS/DR1/r",
    "CDS/P/PanSTARRS/DR1/i",
    "CDS/P/DSS2/blue",
    "CDS/P/DSS2/red",
]


def fetch(hips, ra, dec, fov, size):
    from astropy.io import fits
    r = requests.get(HIPS2FITS, params={
        "hips": hips, "width": size, "height": size, "fov": fov, "projection": "TAN",
        "coordsys": "icrs", "ra": ra, "dec": dec, "rotation_angle": 0.0, "format": "fits"},
        timeout=600)
    r.raise_for_status()
    return fits.open(io.BytesIO(r.content))[0]


def gaia(ra, dec, radius, gmax, column="gmag", table="II/349/ps1"):
    """Reference magnitudes in the SAME band as the image, so no colour term enters the scatter.

    Gaia's G is a single very broad band; comparing it against a g-band image leaves half a
    magnitude of colour term, which is larger than the non-linearity being looked for. The
    Pan-STARRS1 catalogue (Chambers et al. 2016) carries grizy on the same system as the images.
    """
    adql = ("SELECT RAJ2000, DEJ2000, %s FROM \"%s\" WHERE 1=CONTAINS("
            "POINT('ICRS',RAJ2000,DEJ2000),CIRCLE('ICRS',%.6f,%.6f,%.6f)) AND %s < %.1f"
            % (column, table, ra, dec, radius, column, gmax))
    r = requests.get(VIZIER_TAP, params={"request": "doQuery", "lang": "ADQL",
                                         "format": "csv", "query": adql}, timeout=600)
    r.raise_for_status()
    out = []
    for line in r.text.splitlines()[1:]:
        p = line.split(",")
        if len(p) >= 3:
            try:
                out.append((float(p[0]), float(p[1]), float(p[2])))
            except ValueError:
                pass
    return out


def measure(hdu, stars, aperture_arcsec=4.0):
    from astropy.wcs import WCS
    data = np.array(hdu.data, dtype=np.float64)
    w = WCS(hdu.header)
    ny, nx = data.shape
    scale = abs(hdu.header["CDELT1"]) * 3600.0
    rpx = aperture_arcsec / scale
    yy, xx = np.mgrid[0:ny, 0:nx]

    sky = np.nanmedian(data)
    rows = []
    for (sra, sdec, g) in stars:
        x, y = w.all_world2pix(sra, sdec, 0)
        x, y = float(x), float(y)
        if not (rpx * 4 < x < nx - rpx * 4 and rpx * 4 < y < ny - rpx * 4):
            continue
        d2 = (xx - x) ** 2 + (yy - y) ** 2
        inside = d2 < rpx * rpx
        annulus = (d2 > (2.5 * rpx) ** 2) & (d2 < (4.0 * rpx) ** 2)
        if not annulus.any():
            continue
        local = np.nanmedian(data[annulus])
        flux = np.nansum(data[inside] - local)
        if flux > 0:
            rows.append((g, -2.5 * math.log10(flux)))
    return rows


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--ra", type=float, required=True)
    p.add_argument("--dec", type=float, required=True)
    p.add_argument("--fov", type=float, default=0.15)
    p.add_argument("--size", type=int, default=1024)
    p.add_argument("--gmax", type=float, default=19.5)
    p.add_argument("--gmin", type=float, default=15.0, help="brighter than this is likely saturated")
    args = p.parse_args()

    print("\n%-38s %5s %8s %8s %8s" % ("survey band", "stars", "slope", "scatter", "curvature"))
    cache = {}
    for hips in CANDIDATES:
        band = hips.rstrip("/").split("/")[-1]
        column = {"g": "gmag", "r": "rmag", "i": "imag",
                  "blue": "gmag", "red": "rmag"}.get(band, "rmag")
        if column not in cache:
            cache[column] = [s for s in gaia(args.ra, args.dec, args.fov * 0.45, args.gmax, column)
                             if s[2] > args.gmin]
            print("  %s: %d catalogue stars between %.1f and %.1f"
                  % (column, len(cache[column]), args.gmin, args.gmax))
        stars = cache[column]
        try:
            hdu = fetch(hips, args.ra, args.dec, args.fov, args.size)
        except Exception as exc:                                    # noqa: BLE001
            print("%-38s  fetch failed: %s" % (hips, exc))
            continue
        rows = measure(hdu, stars)
        if len(rows) < 8:
            print("%-38s  only %d usable stars" % (hips, len(rows)))
            continue
        g = np.array([r[0] for r in rows])
        m = np.array([r[1] for r in rows])
        # A straight line first, then the quadratic term. On a linear image the slope is one and
        # the curvature is consistent with zero; a compressed one bends.
        a, b = np.polyfit(g, m, 1)
        resid = m - (a * g + b)
        q = np.polyfit(g, m, 2)[0]
        print("%-38s %5d %8.3f %8.3f %8.4f%s"
              % (hips.split("/P/")[-1], len(rows), a, resid.std(), q,
                 "" if abs(a - 1.0) < 0.06 and abs(q) < 0.01 else "   <-- NOT LINEAR"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
