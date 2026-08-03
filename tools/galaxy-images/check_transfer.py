#!/usr/bin/env python3
"""The transfer curve of a HiPS cutout against the survey's own stack image.

A shape map is only a shape map if the pixel values are proportional to surface brightness, and a
HiPS carries no header saying whether they are. Aperture photometry against a catalogue answers the
question in principle but at 0.3 mag of scatter it takes hundreds of stars to see a mild
compression. Comparing the SAME SKY pixel by pixel against the survey's own stack answers it with a
million points and no photometry at all: if the HiPS is linear the relation is a straight line
whatever the units, and if it has been through an asinh or a log it bends, hard, exactly where a
galaxy's nucleus lives.

The reference is the Pan-STARRS1 stack served by ps1images.stsci.edu, which is the survey's own
pipeline output.

Run:
    ./env/bin/python check_transfer.py --ra 195.0 --dec 20.0
"""

import argparse
import io
import sys

import numpy as np
import requests

HIPS2FITS = "https://alasky.cds.unistra.fr/hips-image-services/hips2fits"
PS1_FILENAMES = "https://ps1images.stsci.edu/cgi-bin/ps1filenames.py"
PS1_CUTOUT = "https://ps1images.stsci.edu/cgi-bin/fitscut.cgi"

HIPS_BANDS = {
    "g": ["CDS/P/PanSTARRS/DR1/g", "CDS/P/DESI-Legacy-Surveys/DR10/g"],
    "r": ["CDS/P/PanSTARRS/DR1/r", "CDS/P/DESI-Legacy-Surveys/DR10/r"],
    "i": ["CDS/P/PanSTARRS/DR1/i"],
}


def ps1_stack(ra, dec, band, size_px):
    from astropy.io import fits
    r = requests.get(PS1_FILENAMES, params={"ra": ra, "dec": dec, "filters": band}, timeout=300)
    r.raise_for_status()
    lines = r.text.splitlines()
    if len(lines) < 2:
        raise SystemExit("no Pan-STARRS stack covers %.4f %+.4f" % (ra, dec))
    filename = lines[1].split()[7]
    c = requests.get(PS1_CUTOUT, params={"ra": ra, "dec": dec, "size": size_px,
                                         "format": "fits", "red": filename}, timeout=600)
    c.raise_for_status()
    return fits.open(io.BytesIO(c.content))[0]


def hips_cutout(hips, ra, dec, fov, size):
    from astropy.io import fits
    r = requests.get(HIPS2FITS, params={
        "hips": hips, "width": size, "height": size, "fov": fov, "projection": "TAN",
        "coordsys": "icrs", "ra": ra, "dec": dec, "rotation_angle": 0.0, "format": "fits"},
        timeout=600)
    r.raise_for_status()
    return fits.open(io.BytesIO(r.content))[0]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--ra", type=float, default=195.0)
    p.add_argument("--dec", type=float, default=20.0)
    p.add_argument("--arcmin", type=float, default=6.0)
    args = p.parse_args()

    from astropy.wcs import WCS

    native_px = int(args.arcmin * 60.0 / 0.25)
    fov = args.arcmin / 60.0

    for band, hips_list in HIPS_BANDS.items():
        try:
            native = ps1_stack(args.ra, args.dec, band, native_px)
        except Exception as exc:                                    # noqa: BLE001
            print("%s: no reference stack (%s)" % (band, exc))
            continue
        nat = np.array(native.data, dtype=np.float64)
        wnat = WCS(native.header)

        for hips in hips_list:
            try:
                hdu = hips_cutout(hips, args.ra, args.dec, fov, 512)
            except Exception as exc:                                # noqa: BLE001
                print("%-42s fetch failed: %s" % (hips, exc))
                continue
            img = np.array(hdu.data, dtype=np.float64)
            w = WCS(hdu.header)
            ny, nx = img.shape
            yy, xx = np.mgrid[0:ny, 0:nx]
            ra_deg, dec_deg = w.all_pix2world(xx, yy, 0)
            xn, yn = wnat.all_world2pix(ra_deg, dec_deg, 0)
            xi = np.round(xn).astype(int)
            yi = np.round(yn).astype(int)
            ok = (xi >= 0) & (yi >= 0) & (xi < nat.shape[1]) & (yi < nat.shape[0])
            ok &= np.isfinite(img)
            ref = np.full(img.shape, np.nan)
            ref[ok] = nat[yi[ok], xi[ok]]
            ok &= np.isfinite(ref)
            if np.count_nonzero(ok) < 10000:
                print("%-42s only %d overlapping pixels" % (hips, np.count_nonzero(ok)))
                continue

            a, b = ref[ok], img[ok]
            # The reference stack carries its own sky pedestal; the comparison is of SHAPE, so both
            # are put on a common zero at their own sky median before the curve is measured.
            a = a - np.median(a)
            b = b - np.median(b)

            # The transfer curve: the median HiPS value in bins of reference value, spanning the
            # sky up to the brightest unsaturated pixels.
            hi = np.percentile(a, 99.99)
            edges = np.concatenate([[-np.inf], np.geomspace(max(hi * 1e-4, 1e-6), hi, 12)])
            xs, ys = [], []
            for lo, up in zip(edges[1:-1], edges[2:]):
                sel = (a >= lo) & (a < up)
                if np.count_nonzero(sel) > 50:
                    xs.append(np.median(a[sel]))
                    ys.append(np.median(b[sel]))
            xs, ys = np.array(xs), np.array(ys)
            print("   curve:", " ".join("%.3g/%.3g" % (u, v) for u, v in zip(xs, ys)))
            if len(xs) < 5:
                print("%-42s not enough dynamic range" % hips)
                continue

            # A power law through the curve: y ~ x^gamma. Linear data gives gamma = 1; an asinh or
            # a square root gives 0.5 at the bright end.
            good = (xs > 0) & (ys > 0)
            gamma = np.polyfit(np.log10(xs[good]), np.log10(ys[good]), 1)[0] if good.sum() >= 3 \
                else float("nan")
            verdict = "linear" if abs(gamma - 1.0) < 0.08 else "NOT LINEAR (gamma %.2f)" % gamma
            print("%-42s %d pts, dynamic range %.3g, gamma %.3f  -> %s"
                  % (hips.split("/P/")[-1], np.count_nonzero(ok), hi, gamma, verdict))
    return 0


if __name__ == "__main__":
    sys.exit(main())
