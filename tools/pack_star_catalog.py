#!/usr/bin/env python3
"""
Packs the Tycho-2 catalogue into the compact binary RenderedStarCatalog reads.

WHY TYCHO-2, and why it is NOT the Bright Star Catalogue the mod already ships:
the BSC (V/50, 9110 stars to V~6.5) exists to give the exoplanet instruments a
sparse, hand-searchable target list, and that is exactly what it stays. It is
useless as a *rendered* star field: 9110 stars over 41253 deg^2 is 0.22
stars/deg^2, so an RC20 frame (0.068 deg^2) contains 0.015 of them, one frame
in 65 shows a single star. Tycho-2 (Hog et al. 2000, A&A 355, L27) carries
2,539,913 stars, 99% complete to V=11.0, with BT/VT photometry and ICRS
positions, which is 61 stars/deg^2 and a few real, correctly-placed stars in
every frame. Everything fainter than Tycho-2's V~11.5 limit is not modelled yet
and would need a Galactic star-count model rather than a bigger catalogue.

Source (regenerate with the same URLs if the packed file is lost):
    https://cdsarc.cds.unistra.fr/ftp/I/259/tyc2.dat.00.gz ... tyc2.dat.19.gz
    https://cdsarc.cds.unistra.fr/ftp/I/259/suppl_1.dat.gz
Supplement 1 is included because it holds the bright Hipparcos/Tycho-1 stars
that the main catalogue omits; without it the brightest stars in the sky are
missing from the rendered field.

Photometry is converted to the Johnson system at pack time using the
transformation the Tycho-2 ReadMe itself prescribes (ESA SP-1200 Vol 1 Sect 1.3):
    V   = VT - 0.090*(BT-VT)
    B-V = 0.850*(BT-VT)
so the runtime works in the same Johnson V / B-V system as StellarColor and the
rest of the mod, rather than carrying a second photometric system around.

Proper motions are deliberately dropped: Tycho-2's are of order 10 mas/yr, and
the plate scale of the finest instrument modelled here is ~0.03 arcsec/px, so
they would move a star by one pixel every three years of in-game time.

Usage:  python3 pack_star_catalog.py <dir with tyc2.dat.*.gz> <output .bin>
"""

import gzip
import struct
import sys
import os
import glob

# 0.1 deg declination bands. The runtime cone search reads only the bands its
# field of view touches, so band height sets how much of the file a search
# scans: 0.1 deg keeps a typical frame under a few thousand candidate stars
# while costing only 1801 offsets of header.
DEC_BAND_WIDTH_DEG = 0.1
DEC_BAND_COUNT = int(round(180.0 / DEC_BAND_WIDTH_DEG))

MAGIC = b"EXOSTAR1"
VERSION = 2

# Positions are stored as fixed-point integers over the full turn rather than as
# float32 degrees. A float32 near RA = 360 deg has an ULP of 2.1e-5 deg = 0.077
# arcsec, which is a fourteenth of a pixel on the RC20 but FORTY-THREE pixels at
# SPHERE/ZIMPOL's ~1.8 mas plate scale, where the same star would land in a visibly
# wrong place on the highest-resolution instrument in the catalogue. Fixed point
# over 32 bits gives a uniform 360/2^32 = 8.4e-8 deg = 0.3 mas everywhere, six
# times finer than ZIMPOL's own pixel, at exactly the same four bytes.
RA_SCALE = 2 ** 32 / 360.0
DEC_SCALE = 2 ** 32 / 180.0

# V magnitude is stored as an unsigned millimagnitude offset by this much, so the
# brightest real star (Sirius, V = -1.46) still lands on a positive value.
V_MAG_OFFSET = 2.0
BV_UNKNOWN = -32768  # sentinel: this star has no BT, so its colour is unknown


def parse_float(line, start, end):
    """Fixed-width field -> float, or None when blank (the catalogue's 'missing')."""
    field = line[start - 1:end].strip()
    if not field:
        return None
    try:
        return float(field)
    except ValueError:
        return None


def read_main_catalog(path):
    """tyc2.dat: mean ICRS position at J2000, falling back to the observed position."""
    stars = []
    with gzip.open(path, "rt", encoding="ascii", errors="replace") as f:
        for line in f:
            if len(line) < 130:
                continue
            ra = parse_float(line, 16, 27)
            dec = parse_float(line, 29, 40)
            if ra is None or dec is None:
                # pflag 'X': no mean position. The observed Tycho-2 position is
                # still a real measurement, so the star is kept rather than dropped.
                ra = parse_float(line, 153, 164)
                dec = parse_float(line, 166, 177)
            bt = parse_float(line, 111, 116)
            vt = parse_float(line, 124, 129)
            star = build_star(ra, dec, bt, vt)
            if star:
                stars.append(star)
    return stars


def read_supplement(path):
    """suppl_1.dat: bright Hipparcos/Tycho-1 stars absent from the main catalogue."""
    stars = []
    with gzip.open(path, "rt", encoding="ascii", errors="replace") as f:
        for line in f:
            if len(line) < 108:
                continue
            star = build_star(parse_float(line, 16, 27), parse_float(line, 29, 40),
                              parse_float(line, 84, 89), parse_float(line, 97, 102))
            if star:
                stars.append(star)
    return stars


def build_star(ra, dec, bt, vt):
    """One packed record, or None when the row carries no usable position/magnitude."""
    if ra is None or dec is None or vt is None:
        return None
    if not (-90.0 <= dec <= 90.0):
        return None

    if bt is None:
        # No BT: V cannot be corrected off VT and the colour is genuinely unknown.
        # VT is within ~0.1 mag of V for a typical field star, so it is used as-is
        # and the colour is flagged rather than invented.
        v, bv_milli = vt, BV_UNKNOWN
    else:
        bt_vt = bt - vt
        v = vt - 0.090 * bt_vt
        bv_milli = max(-32767, min(32767, int(round(0.850 * bt_vt * 1000.0))))

    v_milli = int(round((v + V_MAG_OFFSET) * 1000.0))
    if not (0 <= v_milli <= 65535):
        return None

    ra_fixed = int(round((ra % 360.0) * RA_SCALE)) % (2 ** 32)
    dec_fixed = max(-(2 ** 31), min(2 ** 31 - 1, int(round(dec * DEC_SCALE))))
    return (ra_fixed, dec_fixed, v_milli, bv_milli, dec)


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 1
    src_dir, out_path = sys.argv[1], sys.argv[2]

    stars = []
    for path in sorted(glob.glob(os.path.join(src_dir, "tyc2.dat.*.gz"))):
        stars.extend(read_main_catalog(path))
        print(f"  {os.path.basename(path)}: {len(stars)} cumulative")

    suppl = os.path.join(src_dir, "suppl_1.dat.gz")
    if os.path.exists(suppl):
        before = len(stars)
        stars.extend(read_supplement(suppl))
        print(f"  supplement 1: +{len(stars) - before}")

    # Sorted by declination band, then by RA inside the band, so the runtime can
    # binary-search RA within each band its field of view overlaps.
    def band_of(dec):
        return min(DEC_BAND_COUNT - 1, max(0, int((dec + 90.0) / DEC_BAND_WIDTH_DEG)))

    # Sorted by band, then by the raw fixed-point RA, which is monotonic in RA, so
    # the runtime binary-searches the integers directly.
    stars.sort(key=lambda s: (band_of(s[4]), s[0]))

    band_start = [0] * (DEC_BAND_COUNT + 1)
    counts = [0] * DEC_BAND_COUNT
    for s in stars:
        counts[band_of(s[4])] += 1
    total = 0
    for b in range(DEC_BAND_COUNT):
        band_start[b] = total
        total += counts[b]
    band_start[DEC_BAND_COUNT] = total

    with open(out_path, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<II", VERSION, len(stars)))
        f.write(struct.pack("<If", DEC_BAND_COUNT, DEC_BAND_WIDTH_DEG))
        f.write(struct.pack(f"<{DEC_BAND_COUNT + 1}I", *band_start))
        record = struct.Struct("<IiHh")
        f.write(b"".join(record.pack(s[0], s[1], s[2], s[3]) for s in stars))

    size_mb = os.path.getsize(out_path) / (1024 * 1024)
    no_colour = sum(1 for s in stars if s[3] == BV_UNKNOWN)
    print(f"{len(stars)} stars -> {out_path} ({size_mb:.1f} MB), "
          f"{no_colour} without a colour index")
    return 0


if __name__ == "__main__":
    sys.exit(main())
