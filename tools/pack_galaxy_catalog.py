#!/usr/bin/env python3
"""Packs a galaxy catalogue into the binary Core/GalaxyCatalog.cs reads.

WHICH CATALOGUE. HyperLEDA (Makarov et al. 2014, A&A 570, A13), the homogenised compilation that
carries, for every galaxy it knows, the four measured quantities a render needs: total B magnitude,
the diameter of the 25 B-mag/arcsec^2 isophote, the axis ratio of that isophote, and its position
angle. RC3 (de Vaucouleurs et al. 1991) is the classical source for the same parameters and is
folded into HyperLEDA, so taking HyperLEDA is taking RC3 plus everything measured since.

Nothing here is a model. The profile shape comes from the morphological type in
Core/GalaxyCatalog.SersicIndexForType, and the half-light radius is then FORCED by the magnitude
and the isophote together (Core/SersicProfile.EffectiveRadiusFromIsophote) rather than assumed.

UNITS, which is where this catalogue bites. HyperLEDA stores
  * al2000 in HOURS and de2000 in degrees, both J2000;
  * logd25 = log10 of D25 expressed in units of 0.1 arcmin, so D25 [arcmin] = 10**logd25 / 10;
  * logr25 = log10(major/minor), so the axis ratio b/a is 10**-logr25;
  * pa in degrees east of north;
  * t on the de Vaucouleurs scale, -6 (E) to 10 (Im).
Getting logd25 wrong by the factor of ten makes every galaxy ten times too large and still looks
like a catalogue, which is why the conversion is spelled out here and checked below.

Run, fetching from HyperLEDA's own SQL endpoint:
    python -m venv env && ./env/bin/pip install numpy requests
    ./env/bin/python pack_galaxy_catalog.py --bmax 15.0 --out GalaxyCatalog.galcat

or from a CSV exported by hand from http://atlas.obs-hp.fr/hyperleda/ :
    ./env/bin/python pack_galaxy_catalog.py --input leda.csv --out GalaxyCatalog.galcat

Copy the result to <KSP>/GameData/ExoInstruments/PluginData/.
"""

import argparse
import csv
import io
import math
import os
import struct
import sys

MAGIC = b"EXOGALX1"
VERSION = 1

HYPERLEDA_URL = "http://atlas.obs-hp.fr/hyperleda/fG.cgi"

# Only galaxies (objtype G), with the four quantities the render cannot do without. A row missing
# any of them is dropped rather than filled in: a galaxy with no measured size has no shape, and
# inventing one is exactly what this file exists not to do.
REQUIRED = ("al2000", "de2000", "bt", "logd25", "logr25", "pa")


def fetch_hyperleda(bmax):
    import requests
    sql = (f"objtype='G' and bt is not null and bt<={bmax:g} "
           "and logd25 is not null and logr25 is not null and pa is not null")
    params = {
        "n": "meandata",
        "c": "o",
        "of": "1,leda",
        "nra": "l",
        "nakd": "1",
        "d": "objname,al2000,de2000,bt,vt,logd25,logr25,pa,t",
        "sql": sql,
        "ob": "de2000",
        "a": "csv[|]",
    }
    print(f"querying HyperLEDA for bt <= {bmax} ...")
    r = requests.get(HYPERLEDA_URL, params=params, timeout=600)
    r.raise_for_status()
    text = r.text
    # The CGI wraps its table in HTML; the payload starts at the header line.
    start = text.find("objname")
    if start < 0:
        raise SystemExit("HyperLEDA returned no table -- check the query, or use --input with a "
                         "CSV exported from http://atlas.obs-hp.fr/hyperleda/")
    body = text[start:]
    end = body.find("<")
    if end > 0:
        body = body[:end]
    return body


def parse_rows(text):
    sniff = "|" if "|" in text.splitlines()[0] else ","
    reader = csv.DictReader(io.StringIO(text), delimiter=sniff)
    reader.fieldnames = [f.strip() for f in reader.fieldnames]
    rows = []
    for row in reader:
        rows.append({k.strip(): (v.strip() if isinstance(v, str) else v) for k, v in row.items()})
    return rows


def number(row, key):
    v = row.get(key)
    if v is None or v == "":
        return float("nan")
    try:
        return float(v)
    except ValueError:
        return float("nan")


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--input", help="CSV exported from HyperLEDA; omit to query the archive")
    p.add_argument("--bmax", type=float, default=15.0,
                   help="faintest total B magnitude to keep (default 15, about 130k galaxies)")
    p.add_argument("--out", default="GalaxyCatalog.galcat")
    p.add_argument("--source", default="HyperLEDA (Makarov et al. 2014, A&A 570, A13)")
    args = p.parse_args()

    text = open(args.input).read() if args.input else fetch_hyperleda(args.bmax)
    rows = parse_rows(text)
    print(f"{len(rows)} rows")

    out = []
    dropped = 0
    for row in rows:
        vals = {k: number(row, k) for k in REQUIRED}
        if any(math.isnan(v) for v in vals.values()):
            dropped += 1
            continue
        bt = vals["bt"]
        if not (bt <= args.bmax):
            dropped += 1
            continue

        ra = vals["al2000"] * 15.0            # hours -> degrees
        dec = vals["de2000"]
        d25 = 10.0 ** vals["logd25"] / 10.0   # 0.1 arcmin units -> arcmin
        axis = 10.0 ** (-vals["logr25"])      # logr25 = log10(major/minor)
        pa = vals["pa"] % 180.0
        t = number(row, "t")
        vt = number(row, "vt")
        bv = bt - vt if not math.isnan(vt) else float("nan")

        if not (0.0 < d25 < 600.0) or not (0.0 < axis <= 1.0000001):
            dropped += 1
            continue

        out.append((row.get("objname", "").strip(), ra, dec, bt, bv, d25, min(1.0, axis), pa, t))

    out.sort(key=lambda g: g[2])
    print(f"{len(out)} kept, {dropped} dropped for a missing or impossible value")

    # A few named galaxies, printed so a units error cannot pass silently: M31 must come out at
    # about 3.2 degrees across and B_T 4.4, M87 at 8 arcmin and 9.6.
    for name in ("NGC0224", "NGC0598", "NGC4486", "NGC5194", "NGC1068"):
        for g in out:
            if g[0].replace(" ", "").upper() == name:
                print(f"  {g[0]:>10}: RA {g[1]:9.4f}  Dec {g[2]:+8.4f}  B_T {g[3]:5.2f}  "
                      f"D25 {g[5]:7.2f}'  b/a {g[6]:.3f}  PA {g[7]:5.1f}  T {g[8]:+.1f}")
                break

    from_type = 0
    with open(args.out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<ii", VERSION, len(out)))
        src = args.source.encode("utf-8")
        f.write(struct.pack("<i", len(src)))
        f.write(src)
        for name, ra, dec, bt, bv, d25, axis, pa, t in out:
            nb = name.encode("utf-8")[:64]
            f.write(struct.pack("<i", len(nb)))
            f.write(nb)
            f.write(struct.pack("<dd", ra, dec))
            # Sersic index left at the type-derived value, flagged as not measured: HyperLEDA
            # publishes no profile fit. A future source that does can set both here.
            n_sersic = sersic_index_for_type(t)
            from_type += 1
            f.write(struct.pack("<fffffff", bt, bv, d25, axis, pa, t if not math.isnan(t) else 0.0,
                                n_sersic))
            f.write(struct.pack("<B", 0))

    size = os.path.getsize(args.out) / (1024 * 1024)
    print(f"wrote {args.out} ({size:.1f} MB), {from_type} Sersic indices set from morphological type")
    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/")
    return 0


def sersic_index_for_type(t):
    """Mirror of Core/GalaxyCatalog.SersicIndexForType -- see there for the sourcing."""
    if math.isnan(t):
        return 1.0
    if t <= -4.0:
        return 4.0
    if t <= -1.0:
        return 3.0
    if t <= 0.5:
        return 2.0
    if t <= 2.5:
        return 1.5
    return 1.0


if __name__ == "__main__":
    sys.exit(main())
