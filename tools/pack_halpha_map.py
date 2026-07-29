#!/usr/bin/env python3
"""Packs an all-sky H-alpha map into the binary Core/EmissionMap.cs reads.

WHICH MAP. Finkbeiner (2003, ApJS 146, 407) assembles WHAM, VTSS and SHASSA into one full-sky
composite in rayleighs, smoothed to 6 arcmin. It is the all-sky H-alpha map, and rayleighs are the
unit Core/EmissionLines converts from, so nothing is reinterpreted on the way in.

It is distributed by NASA's LAMBDA archive as a HEALPix FITS file. Point --input at a downloaded
copy; the script does not fetch it, because the archive's URLs move and a wrong file silently
producing a plausible sky is worse than an error.

    https://lambda.gsfc.nasa.gov/product/foreground/fg_halpha_get.html

RESOLUTION IS THE REAL LIMIT, more than for dust. 6 arcmin is 94 pixels across on the RedCat and
1300 on the RC20 behind its Barlow, so this renders real structure in a wide field and a smooth
glow at high magnification. Defaults to nside 1024 (3.4 arcmin), finer than the data, so nothing
of it is thrown away.

Run:
    python -m venv env && ./env/bin/pip install healpy numpy
    ./env/bin/python pack_halpha_map.py --input Halpha_fwhm06_1024.fits --out HalphaMap.emission
"""

import argparse
import struct
import sys

MAGIC = b"EXOEMIS1"
VERSION = 1

# Rayleighs per stored unit. 0.01 R resolves far below the survey's own noise and reaches 655 R,
# past the brightest diffuse emission in the plane; the brightest H II region cores saturate it and
# are clipped rather than wrapped.
SCALE_R_PER_UNIT = 0.01
UNKNOWN = 0xFFFF

# H-alpha, air wavelength, matching Core/EmissionLines.HAlpha.
LINE_NAME = "H-alpha"
LINE_WAVELENGTH_M = 6562.80e-10


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--input", required=True, help="HEALPix FITS map in rayleighs")
    parser.add_argument("--nside", type=int, default=1024)
    parser.add_argument("--out", default="HalphaMap.emission")
    parser.add_argument("--source", default="Finkbeiner (2003, ApJS 146, 407) WHAM/VTSS/SHASSA composite")
    args = parser.parse_args()

    import numpy as np
    import healpy as hp

    if args.nside <= 0 or args.nside & (args.nside - 1):
        raise SystemExit("nside must be a power of two")

    raw = hp.read_map(args.input, dtype=np.float64)
    native = hp.npix2nside(len(raw))
    print(f"read {args.input}: nside {native} ({hp.nside2resol(native, arcmin=True):.1f} arcmin)")

    if native != args.nside:
        # ud_grade is exact in both directions for HEALPix: down-grading averages the children of a
        # cell, up-grading replicates a cell into them. Neither invents structure.
        raw = hp.ud_grade(raw, args.nside, order_in="RING", order_out="RING")
        print(f"regridded to nside {args.nside} ({hp.nside2resol(args.nside, arcmin=True):.1f} arcmin)")

    counts = np.rint(raw / SCALE_R_PER_UNIT)
    bad = ~np.isfinite(raw) | (counts < 0)
    saturated = int((counts >= UNKNOWN).sum())
    counts = np.clip(counts, 0, UNKNOWN - 1).astype(np.uint16)
    counts[bad] = UNKNOWN

    name = LINE_NAME.encode("utf-8")
    source = args.source.encode("utf-8")
    with open(args.out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<ii", VERSION, args.nside))
        f.write(struct.pack("<B", 0))                   # 0 = RING
        f.write(struct.pack("<f", SCALE_R_PER_UNIT))
        f.write(struct.pack("<d", LINE_WAVELENGTH_M))
        f.write(struct.pack("<i", len(name)))
        f.write(name)
        f.write(struct.pack("<i", len(source)))
        f.write(source)
        f.write(counts.astype("<u2").tobytes())

    import os
    size_mb = os.path.getsize(args.out) / (1024 * 1024)
    print(f"{len(counts)} pixels -> {args.out} ({size_mb:.1f} MB), "
          f"{int(bad.sum())} without a value, {saturated} clipped at {(UNKNOWN - 1) * SCALE_R_PER_UNIT:.0f} R")
    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/")
    return 0


if __name__ == "__main__":
    sys.exit(main())
