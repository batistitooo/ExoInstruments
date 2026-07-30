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

WHY NOT healpy. healpy publishes no Windows wheel and its source build needs a C toolchain plus
cfitsio, so on Windows this script could not run at all. Everything it was used for here is
indexing arithmetic -- an ordering swap, a parent/child average, a pixel count -- which
astropy-healpix provides and ships as a Windows wheel. The FITS read is a plain binary table that
astropy.io.fits reads directly. Nothing about the map's values changes: see the module tests in
tools/dustmap-tests, which check this indexing against healpy's own answers.

Run:
    python -m venv env && ./env/bin/pip install numpy astropy astropy-healpix
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


def read_healpix_map(path):
    """Reads a HEALPix FITS map, returning (values, nside, ordering).

    Returns the map exactly as the file stores it -- no ordering conversion here, because the
    caller regrids in NESTED and writes in RING, and doing the swap once at the end is both
    cheaper and easier to check than doing it twice.

    HEALPix FITS stores the map in the first column of the first binary table. Two layouts exist:
    one element per row (this file: 12582912 rows x 1 column), and the older 1024-element-per-row
    chunking. Ravelling the column handles both, and the pixel count is then checked against the
    header's NSIDE rather than inferred, so a truncated or mislabelled file fails here instead of
    producing a sky that is wrong by a rotation.
    """
    import numpy as np
    from astropy.io import fits

    with fits.open(path) as hdul:
        if len(hdul) < 2 or not hasattr(hdul[1], "columns"):
            raise SystemExit(f"{path}: no binary table in HDU 1, so this is not a HEALPix map")
        hdu = hdul[1]
        header = hdu.header

        ordering = str(header.get("ORDERING", "")).strip().upper()
        if ordering not in ("RING", "NESTED"):
            raise SystemExit(f"{path}: ORDERING is {ordering!r}, expected RING or NESTED. "
                             "Refusing to guess -- the wrong guess scrambles the sky into "
                             "something that still looks like a sky.")

        nside = header.get("NSIDE")
        if nside is None:
            raise SystemExit(f"{path}: no NSIDE in the header")
        nside = int(nside)

        # INDXSCHM = EXPLICIT means the table carries a pixel-index column and covers only part of
        # the sky; this packer writes a full-sky array and has no way to represent that.
        indxschm = str(header.get("INDXSCHM", "IMPLICIT")).strip().upper()
        if indxschm not in ("IMPLICIT", ""):
            raise SystemExit(f"{path}: INDXSCHM is {indxschm!r}; only full-sky IMPLICIT maps are "
                             "supported")

        column = hdu.columns.names[0]
        values = np.asarray(hdu.data[column]).ravel().astype(np.float64)

        unit = str(header.get("TUNIT1", "")).strip()
        if unit and unit not in ("R", "Rayleigh", "rayleigh", "rayleighs"):
            print(f"warning: TUNIT1 is {unit!r}, not rayleighs. Core/EmissionLines converts from "
                  "rayleighs, so a different unit will render at the wrong brightness.")

    expected = 12 * nside * nside
    if values.size != expected:
        raise SystemExit(f"{path}: header says NSIDE {nside} ({expected} pixels) but the table "
                         f"holds {values.size}")

    return values, nside, ordering


def to_ordering(values, nside, source_ordering, target_ordering):
    """Reorders a full-sky map between RING and NESTED indexing.

    A HEALPix map is an array indexed by pixel number, and the two orderings number the same
    pixels differently. So the value that belongs at output index i is the input value for
    whichever input index names the same patch of sky, which is a gather through the index
    conversion -- out[i] = values[convert(i)] -- not a scatter.
    """
    import numpy as np
    # astropy-healpix 2.0 keeps these in .core rather than re-exporting them at the top level.
    from astropy_healpix.core import nested_to_ring, ring_to_nested

    if source_ordering == target_ordering:
        return values

    index = np.arange(values.size, dtype=np.int64)
    if target_ordering == "RING":
        return values[ring_to_nested(index, nside)]
    return values[nested_to_ring(index, nside)]


def ud_grade_nested(values, nside_in, nside_out):
    """Regrids a NESTED map, exactly as healpy's ud_grade does in both directions.

    NESTED ordering is what makes this arithmetic rather than interpolation: a cell's four children
    are the four consecutive indices below it, so at any depth the descendants of one parent are one
    contiguous block. Down-grading is therefore the mean of each block and up-grading is each value
    repeated across its block. Neither invents structure.

    The mean ignores non-finite children rather than propagating them, so one blank pixel does not
    blank its parent; a parent whose children are all blank stays blank.
    """
    import numpy as np

    if nside_in == nside_out:
        return values

    if nside_out < nside_in:
        ratio = (nside_in // nside_out) ** 2
        blocks = values.reshape(-1, ratio)
        good = np.isfinite(blocks)
        count = good.sum(axis=1)
        total = np.where(good, blocks, 0.0).sum(axis=1)
        with np.errstate(invalid="ignore", divide="ignore"):
            return np.where(count > 0, total / np.maximum(count, 1), np.nan)

    ratio = (nside_out // nside_in) ** 2
    return np.repeat(values, ratio)


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
    from astropy_healpix import nside_to_pixel_resolution

    def resol_arcmin(nside):
        return nside_to_pixel_resolution(nside).to_value("arcmin")

    raw, native, ordering = read_healpix_map(args.input)
    print(f"read {args.input}: nside {native} ({resol_arcmin(native):.1f} arcmin), {ordering}")

    if args.nside == 0:
        args.nside = native
    elif args.nside < 0 or args.nside & (args.nside - 1):
        raise SystemExit("nside must be a power of two, or 0 to keep the input's own")

    # Regrid in NESTED, where a parent's children are contiguous, then hand the result to the
    # single ordering swap below. The packed format declares RING, and both LAMBDA files are stored
    # NESTED, so that swap is the one that has to be right.
    nested = to_ordering(raw, native, ordering, "NESTED")
    if native != args.nside:
        nested = ud_grade_nested(nested, native, args.nside)
        print(f"regridded to nside {args.nside} ({resol_arcmin(args.nside):.1f} arcmin)")
    ring = to_ordering(nested, args.nside, "NESTED", "RING")

    # A negative surface brightness is a subtraction artefact, not a measurement.
    values = np.where(np.isfinite(ring) & (ring >= 0.0), ring, np.nan)
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

    # The named check the README promises: the Galactic plane must be the bright part of the sky.
    # An ordering mistake survives every check above -- the value range and the median are
    # invariant under a permutation of the pixels -- and shows up only when brightness is asked
    # for by position.
    from astropy_healpix import healpix_to_lonlat
    import astropy.units as u
    lon, lat = healpix_to_lonlat(np.arange(packed.size, dtype=np.int64), args.nside, order="ring")
    b = lat.to_value(u.deg)
    vals = packed.astype(float)
    plane = np.nanmedian(vals[np.abs(b) < 5.0])
    poles = np.nanmedian(vals[np.abs(b) > 60.0])
    print(f"Galactic plane |b|<5: {plane:.2f} R, poles |b|>60: {poles:.2f} R, "
          f"ratio {plane / poles:.1f}x")
    if not (plane > poles):
        raise SystemExit("the Galactic plane is not brighter than the poles, so the pixel ordering "
                         "is wrong. Refusing to write a scrambled sky -- check ORDERING.")

    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/")
    return 0


if __name__ == "__main__":
    sys.exit(main())
