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
VERSION = 2

# E(B-V) as an IEEE 754 half float, which is what Core/Float16.cs reads.
#
# Version 1 stored a fixed-point count of 1e-4 magnitudes, which saturated at 6.5535. SFD98 reaches
# 135.25 magnitudes in the inner plane, so that version silently marked 48615 pixels "no value" --
# every one of them at |b| below a degree, i.e. exactly the dust worth having. No fixed-point scale
# spans 0.00037 to 135 magnitudes in 16 bits; a half float's precision is relative, 4.9e-4 of the
# value everywhere, which is 3e-5 mag at the median sight line and 0.07 at the worst.
SATURATION_LIMIT = 65504.0    # largest finite binary16

# Where to get SFD98 when dustmaps' own fetch cannot.
#
# dustmaps.sfd.fetch() downloads the two maps from Harvard Dataverse by DOI, and Dataverse now sits
# behind an AWS WAF that answers a plain HTTP client with 202 and an empty body
# (x-amzn-waf-action: challenge). dustmaps then parses that empty body as JSON and dies with
# JSONDecodeError, which is what a player sees. dustmaps 1.0.14 is the current release, so there is
# no upstream fix to wait for.
#
# These are the same two files, from the SDSS public mirror that the original IDL dust_getval has
# pointed at for twenty years. The packer's own sanity checks below still have to pass on whatever
# arrives, and a map packed from this source is byte-identical to one packed from Dataverse.
SFD_MIRROR = "https://svn.sdss.org/public/data/sdss/catalogs/dust/trunk/maps/"
SFD_MIRROR_BYTES = 67115520   # both poles, 4096x4096 float32 plus header


def fetch_sfd_from_mirror(quiet=False):
    """Puts SFD_dust_4096_{ngp,sgp}.fits where dustmaps expects to find them."""
    import os
    import urllib.request
    from dustmaps.std_paths import data_dir

    target = os.path.join(data_dir(), "sfd")
    os.makedirs(target, exist_ok=True)
    for pole in ("ngp", "sgp"):
        name = f"SFD_dust_4096_{pole}.fits"
        path = os.path.join(target, name)
        if os.path.exists(path) and os.path.getsize(path) == SFD_MIRROR_BYTES:
            continue
        if not quiet:
            print(f"fetching {name} from the SDSS mirror (64 MB)...", flush=True)
        urllib.request.urlretrieve(SFD_MIRROR + name, path)
        size = os.path.getsize(path)
        if size != SFD_MIRROR_BYTES:
            raise SystemExit(f"{name} came back {size} bytes, expected {SFD_MIRROR_BYTES}")


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
            try:
                fetch_sfd()
            except Exception as exc:                        # noqa: BLE001
                if not quiet:
                    print(f"dustmaps' own download failed ({exc}); using the mirror", flush=True)
                fetch_sfd_from_mirror(quiet)
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

    packed = np.empty(npix, dtype=np.float16)

    # In chunks: the whole sky at nside 1024 is 12.6 M coordinates, and building one SkyCoord of
    # that size is a large transient allocation for no gain.
    chunk = 1 << 20
    for start in range(0, npix, chunk):
        stop = min(start + chunk, npix)
        # RING here, and the header says so; the reader honours whichever the file declares.
        lon, lat = hp.pix2ang(nside, np.arange(start, stop), nest=False, lonlat=True)
        coords = SkyCoord(l=lon * u.deg, b=lat * u.deg, frame="galactic")

        values = np.asarray(query(coords), dtype=float) * recalibration
        # A negative reddening is a fit artefact rather than a measurement, and is recorded as
        # "no value" rather than clipped to zero, which would claim a transparent sight line.
        values = np.where(np.isfinite(values) & (values >= 0.0), values, np.nan)
        packed[start:stop] = values.astype(np.float16)

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

    import numpy as np

    packed, source = build(args.map, args.nside, args.quiet)
    encoded = source.encode("utf-8")

    with open(args.out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<ii", VERSION, args.nside))
        f.write(struct.pack("<B", 0))                       # 0 = RING
        f.write(struct.pack("<i", len(encoded)))
        f.write(encoded)
        f.write(packed.astype("<f2").tobytes())

    import os
    size_mb = os.path.getsize(args.out) / (1024 * 1024)
    finite = np.isfinite(packed.astype(float))
    unknown = int((~finite).sum())
    v = packed.astype(float)[finite]
    over = int((v > SATURATION_LIMIT).sum())
    print(f"{len(packed)} pixels -> {args.out} ({size_mb:.1f} MB), {unknown} without a value, "
          f"{over} beyond the format's range")
    print(f"E(B-V) range: {v.min():.5f} to {v.max():.2f} mag, median {float(np.median(v)):.5f}")
    print(f"source: {source}")
    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/")
    return 0


if __name__ == "__main__":
    sys.exit(main())
