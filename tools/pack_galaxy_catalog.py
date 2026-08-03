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
REQUIRED = ("al2000", "de2000", "bt", "logd25", "logr25")


def fetch_hyperleda(bmax):
    import requests
    # Deliberately NOT filtering on pa: a round isophote has no measurable position angle and
    # HyperLEDA leaves it blank, so requiring one here silently drops the roundest galaxies --
    # M87 among them. The row filter below handles a missing angle properly.
    sql = (f"objtype='G' and bt is not null and bt<={bmax:g} "
           "and logd25 is not null and logr25 is not null")
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
    return r.text


def strip_comments(text):
    """HyperLEDA prefixes its table with a commented query description and separator rules.

    Every one of those lines starts with '#', including a per-column description block that
    repeats the column NAMES -- so locating the header by searching for a column name finds the
    description instead, and taking everything after it yields three rows of nonsense. Dropping
    the comment lines first is both simpler and unambiguous.
    """
    lines = [ln for ln in text.splitlines() if ln.strip() and not ln.lstrip().startswith("#")]
    if not lines:
        raise SystemExit("HyperLEDA returned no table -- check the query, or use --input with a "
                         "CSV exported from http://atlas.obs-hp.fr/hyperleda/")
    return "\n".join(lines)


def parse_rows(text):
    text = strip_comments(text)
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
                   help="faintest total B magnitude to keep (15 gives 15732 galaxies in 0.9 MB, "
                        "13 gives 1454 in 82 KB)")
    p.add_argument("--out", default="GalaxyCatalog.galcat")
    p.add_argument("--source", default="HyperLEDA (Makarov et al. 2014, A&A 570, A13)")
    args = p.parse_args()

    text = open(args.input).read() if args.input else fetch_hyperleda(args.bmax)
    rows = parse_rows(text)
    print(f"{len(rows)} rows")

    out = []
    dropped = 0
    implausible = []
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
        # A round isophote has no measurable position angle, and HyperLEDA leaves it blank rather
        # than inventing one. Zero is then not a guess but the only meaningful value; for an
        # elongated galaxy a missing angle IS a gap, and the row is dropped.
        pa_raw = number(row, "pa")
        if math.isnan(pa_raw):
            if 10.0 ** (-vals["logr25"]) < 0.9:
                dropped += 1
                continue
            pa_raw = 0.0
        pa = pa_raw % 180.0
        t = number(row, "t")
        vt = number(row, "vt")
        bv = bt - vt if not math.isnan(vt) else float("nan")

        if not (0.0 < d25 < 600.0) or not (0.0 < axis <= 1.0000001):
            dropped += 1
            continue

        # A row whose magnitude and size cannot both be right. The isophote that defines D25 is
        # drawn at 25 mag/arcsec^2, so the light inside it averages a few magnitudes brighter than
        # that and no more: over the B <= 13 catalogue the first percentile is 20.45 and the median
        # 22.64. HyperLEDA nonetheless carries PGC 779349 at B_T 8.30 in 0.34 arcmin, i.e. 13.6
        # mag/arcsec^2, seven magnitudes out; drawn, it is a saturated white disc sitting on the sky
        # chart among the brightest galaxies in the sky. Mirrors
        # Core/GalaxyCatalog.ImplausibleSurfaceBrightnessB, which drops the same rows at load so an
        # already-installed catalogue is safe too.
        semi_major = d25 * 60.0 * 0.5
        area = math.pi * semi_major * semi_major * min(1.0, axis)
        if area > 0.0 and bt + 2.5 * math.log10(area) < 18.0:
            implausible.append((row.get("objname", "").strip(), bt, d25,
                                bt + 2.5 * math.log10(area)))
            dropped += 1
            continue

        out.append((row.get("objname", "").strip(), ra, dec, bt, bv, d25, min(1.0, axis), pa, t))

    out.sort(key=lambda g: g[2])
    print(f"{len(out)} kept, {dropped} dropped for a missing or impossible value")
    for (nm, bt, d25, mu) in implausible:
        print(f"  dropped {nm}: B_T {bt:.2f} in D25 {d25:.2f}' is {mu:.2f} B-mag/arcsec^2 inside "
              f"the isophote, which no galaxy is")

    # A few named galaxies, printed so a units error cannot pass silently. Measured values from a
    # real run: M31 at B_T 4.29, D25 177.8' (2.96 deg), b/a 0.392, PA 35; M87 at 9.65, 7.11', 0.938.
    # Getting logd25 wrong by its factor of ten would show here immediately.
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
