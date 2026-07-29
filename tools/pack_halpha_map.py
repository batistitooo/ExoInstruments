#!/usr/bin/env python3
"""Packs an all-sky H-alpha map into the binary Core/EmissionMap.cs reads.

WHICH MAP. Finkbeiner (2003, ApJS 146, 407) assembles WHAM, VTSS and SHASSA into one full-sky
composite in rayleighs, smoothed to 6 arcmin. It is the all-sky H-alpha map, and rayleighs are the
unit Core/EmissionLines converts from, so nothing is reinterpreted on the way in.

It is distributed by NASA's LAMBDA archive as a HEALPix FITS file, in rayleighs (TUNIT1 = R). Point
--input at a downloaded copy; the script does not fetch it, because the archive's URLs move and a
wrong file silently producing a plausible sky is worse than an error.

    https://lambda.gsfc.nasa.gov/data/foregrounds/fink_halpha/Halpha_fwhm06_1024.fits

TAKE THE NSIDE 1024 FILE, not the nside 512 one the same page offers. They are the same product --
the 1024 degraded to 512 matches the native 512 file to 0.8% in the median -- but the map's beam is
6 arcmin FWHM and nside 512 gives 6.87 arcmin pixels, coarser than the beam itself. That
undersamples the map by a factor of 2.3 against Nyquist and smears real structure: the two disagree
by 7.3% at the 90th percentile, and the 512 loses 130 R off the brightest peak in the sky. nside
1024's 3.44 arcmin pixels sample a 6 arcmin beam properly.

By default the input's own nside is kept: regridding is never free of doubt, and going finer than
the file stores interpolation rather than data. The 6 arcmin beam is 94 pixels across on the RedCat
and 1300 on the RC20 behind its Barlow, so the map renders real structure in a wide field and a
smooth glow at high magnification. That is the data's limit and no all-sky H-alpha map does better.

Run:
    python -m venv env && ./env/bin/pip install healpy numpy
    ./env/bin/python pack_halpha_map.py --input Halpha_fwhm06_1024.fits --out HalphaMap.emission
"""

import argparse
import struct
import sys

MAGIC = b"EXOEMIS1"
VERSION = 2

# Rayleighs as an IEEE 754 half float, matching the dust map and for the same reason: diffuse
# H-alpha runs from under a rayleigh at the poles to thousands in an H II region core, and a
# fixed-point scale fine enough for one saturates on the other. A half float's precision is
# relative, 4.9e-4 of the value everywhere.

# H-alpha, air wavelength, matching Core/EmissionLines.HAlpha.
LINE_NAME = "H-alpha"
LINE_WAVELENGTH_M = 6562.80e-10


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--input", required=True, help="HEALPix FITS map in rayleighs")
    parser.add_argument("--nside", type=int, default=0,
                        help="regrid to this nside; 0 (the default) keeps the input's own")
    parser.add_argument("--out", default="HalphaMap.emission")
    parser.add_argument("--source", default="Finkbeiner (2003, ApJS 146, 407) WHAM/VTSS/SHASSA composite")
    args = parser.parse_args()

    import numpy as np
    import healpy as hp

    # nest=False is explicit rather than left to the default: both LAMBDA files are stored NESTED,
    # the packed format declares RING, and a silent ordering flip would scramble the sky into
    # something that still looks like a sky.
    raw = hp.read_map(args.input, dtype=np.float64, nest=False)
    native = hp.npix2nside(len(raw))
    print(f"read {args.input}: nside {native} ({hp.nside2resol(native, arcmin=True):.1f} arcmin)")

    if args.nside == 0:
        args.nside = native
    elif args.nside < 0 or args.nside & (args.nside - 1):
        raise SystemExit("nside must be a power of two, or 0 to keep the input's own")

    if native != args.nside:
        # ud_grade is exact in both directions for HEALPix: down-grading averages the children of a
        # cell, up-grading replicates a cell into them. Neither invents structure.
        raw = hp.ud_grade(raw, args.nside, order_in="RING", order_out="RING")
        print(f"regridded to nside {args.nside} ({hp.nside2resol(args.nside, arcmin=True):.1f} arcmin)")

    # A negative surface brightness is a subtraction artefact, not a measurement.
    values = np.where(np.isfinite(raw) & (raw >= 0.0), raw, np.nan)
    packed = values.astype(np.float16)

    name = LINE_NAME.encode("utf-8")
    source = args.source.encode("utf-8")
    with open(args.out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<ii", VERSION, args.nside))
        f.write(struct.pack("<B", 0))                   # 0 = RING
        f.write(struct.pack("<d", LINE_WAVELENGTH_M))
        f.write(struct.pack("<i", len(name)))
        f.write(name)
        f.write(struct.pack("<i", len(source)))
        f.write(source)
        f.write(packed.astype("<f2").tobytes())

    import os
    size_mb = os.path.getsize(args.out) / (1024 * 1024)
    finite = np.isfinite(packed.astype(float))
    v = packed.astype(float)[finite]
    print(f"{len(packed)} pixels -> {args.out} ({size_mb:.1f} MB), "
          f"{int((~finite).sum())} without a value")
    print(f"surface brightness range: {v.min():.3f} to {v.max():.1f} R, median {float(np.median(v)):.3f}")
    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/")
    return 0


if __name__ == "__main__":
    sys.exit(main())
