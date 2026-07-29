#!/usr/bin/env python3
"""Packs an all-sky reddening map into the binary Core/DustMap.cs reads.

WHICH MAP, AND WHY. Schlegel, Finkbeiner & Davis (1998, ApJ 500, 525) inferred E(B-V) across the
whole sky from far-infrared dust emission measured by COBE/DIRBE and IRAS/ISSA, at 6.1 arcmin. It is
still the reference all-sky reddening map. Schlafly & Finkbeiner (2011, ApJ 737, 103) recalibrated
it against the colours of 260000 SDSS stars and found it overestimates by 14%, so the standard use
of SFD98 today is with their 0.86 factor, which `dustmaps` applies for you.

Planck's GNILC map (Planck Collaboration Int. XLVIII 2016) is the alternative, at 5 arcmin. Pass
--map planck for it. Both are the TOTAL Galactic column, which is what this file is for -- a star
inside the Galaxy needs a per-source estimate instead, and Gaia publishes one (see
tools/pack_gaia_catalog.py).

RESOLUTION. Defaults to nside 1024, 3.4 arcmin, which is finer than either source map's own beam
and makes the whole sky 24 MB. Going finer stores interpolation, not data.

Run:
    python -m venv env && ./env/bin/pip install dustmaps healpy numpy astropy
    ./env/bin/python pack_dust_map.py --out DustMap.dustmap

Then copy it to <KSP>/GameData/ExoInstruments/PluginData/.
"""

import argparse
import struct
import sys

MAGIC = b"EXODUST1"
VERSION = 1

# E(B-V) as an unsigned 16-bit count of this many magnitudes. 1e-4 mag resolves far below any
# map's own uncertainty and still reaches 6.55 mag, past the most obscured sight line in the plane.
SCALE_MAG_PER_UNIT = 1.0e-4
UNKNOWN = 0xFFFF


def build(map_name, nside, quiet=False):
    import numpy as np
    import healpy as hp
    import astropy.units as u
    from astropy.coordinates import SkyCoord

    if map_name == "sfd":
        from dustmaps.sfd import SFDQuery, fetch as fetch_sfd
        try:
            query = SFDQuery()
        except Exception:                                   # noqa: BLE001
            if not quiet:
                print("fetching SFD98 (about 150 MB, once)...", flush=True)
            fetch_sfd()
            query = SFDQuery()
        # dustmaps' SFDQuery returns SFD's own E(B-V); Schlafly & Finkbeiner's recalibration is
        # the 0.86 factor, applied here rather than left to the reader.
        recalibration = 0.86
        source = ("SFD98 (Schlegel, Finkbeiner & Davis 1998, ApJ 500, 525) "
                  "x0.86 (Schlafly & Finkbeiner 2011, ApJ 737, 103)")
    elif map_name == "planck":
        from dustmaps.planck import PlanckQuery, fetch as fetch_planck
        try:
            query = PlanckQuery()
        except Exception:                                   # noqa: BLE001
            if not quiet:
                print("fetching Planck GNILC (about 1.6 GB, once)...", flush=True)
            fetch_planck()
            query = PlanckQuery()
        recalibration = 1.0
        source = "Planck GNILC (Planck Collaboration Int. XLVIII 2016, A&A 596, A109)"
    else:
        raise SystemExit(f"unknown map {map_name!r}")

    npix = hp.nside2npix(nside)
    if not quiet:
        print(f"{map_name}: nside {nside}, {npix} pixels, "
              f"{hp.nside2resol(nside, arcmin=True):.2f} arcmin", flush=True)

    packed = np.empty(npix, dtype=np.uint16)

    # In chunks: the whole sky at nside 1024 is 12.6 M coordinates, and building one SkyCoord of
    # that size is a large transient allocation for no gain.
    chunk = 1 << 20
    for start in range(0, npix, chunk):
        stop = min(start + chunk, npix)
        # RING here, and the header says so; the reader honours whichever the file declares.
        lon, lat = hp.pix2ang(nside, np.arange(start, stop), nest=False, lonlat=True)
        coords = SkyCoord(l=lon * u.deg, b=lat * u.deg, frame="galactic")

        values = np.asarray(query(coords), dtype=float) * recalibration
        counts = np.rint(values / SCALE_MAG_PER_UNIT)
        bad = ~np.isfinite(values) | (counts < 0) | (counts >= UNKNOWN)
        counts = np.clip(counts, 0, UNKNOWN - 1).astype(np.uint16)
        counts[bad] = UNKNOWN
        packed[start:stop] = counts

        if not quiet:
            print(f"  {stop}/{npix}", flush=True)

    return packed, source


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--map", default="sfd", choices=("sfd", "planck"))
    parser.add_argument("--nside", type=int, default=1024)
    parser.add_argument("--out", default="DustMap.dustmap")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    if args.nside <= 0 or args.nside & (args.nside - 1):
        raise SystemExit("nside must be a power of two")

    packed, source = build(args.map, args.nside, args.quiet)
    encoded = source.encode("utf-8")

    with open(args.out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<ii", VERSION, args.nside))
        f.write(struct.pack("<B", 0))                       # 0 = RING
        f.write(struct.pack("<f", SCALE_MAG_PER_UNIT))
        f.write(struct.pack("<i", len(encoded)))
        f.write(encoded)
        f.write(packed.astype("<u2").tobytes())

    import os
    size_mb = os.path.getsize(args.out) / (1024 * 1024)
    unknown = int((packed == UNKNOWN).sum())
    print(f"{len(packed)} pixels -> {args.out} ({size_mb:.1f} MB), {unknown} without a value")
    print(f"source: {source}")
    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/")
    return 0


if __name__ == "__main__":
    sys.exit(main())
