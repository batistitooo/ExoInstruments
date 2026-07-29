"""Writes a synthetic dust map in the packed format, with a pattern the reader can be checked against.

The value in each pixel is a known analytic function of that pixel's own Galactic latitude, so a
query at any sky position has an expected answer that does not depend on the map file at all. That
turns "does the reader find the right pixel" into an arithmetic check rather than a visual one, and
it exercises the same HEALPix indexing and Galactic transform a real map would.
"""

import struct
import sys

import numpy as np
import healpy as hp

MAGIC = b"EXODUST1"
VERSION = 2
NSIDE = 64


def pattern(b_deg):
    """A plane-concentrated column, cosec-like but bounded: 0.02 at the poles, 2.0 in the plane."""
    return 0.02 + 1.98 * np.exp(-np.abs(b_deg) / 8.0)


def main():
    npix = hp.nside2npix(NSIDE)
    lon, lat = hp.pix2ang(NSIDE, np.arange(npix), nest=False, lonlat=True)
    values = pattern(lat)

    counts = values.astype(np.float16)
    # One pixel deliberately NaN, so the no-measurement path is exercised too.
    counts[0] = np.nan

    source = "synthetic test pattern, exp(-|b|/8 deg)"
    encoded = source.encode("utf-8")
    with open("test_map.dustmap", "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<ii", VERSION, NSIDE))
        f.write(struct.pack("<B", 0))
        f.write(struct.pack("<i", len(encoded)))
        f.write(encoded)
        f.write(counts.astype("<f2").tobytes())

    print(f"wrote test_map.dustmap: nside {NSIDE}, {npix} pixels, "
          f"{hp.nside2resol(NSIDE, arcmin=True):.1f} arcmin")
    return 0


if __name__ == "__main__":
    sys.exit(main())
