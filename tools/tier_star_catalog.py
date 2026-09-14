#!/usr/bin/env python3
"""Cuts magnitude tiers out of a packed star catalogue, so a wide field reads about what it draws.

WHY

A cone search reads every record inside the field whatever its magnitude cut, because the file is
sorted by position and not by brightness. That is free on a G < 13 catalogue and ruinous on the
all-sky Gaia DR3 one (1.8 billion stars, 25.3 GB): a BRITE field toward the Galactic centre reads
456 million records to keep 15 million at V 17, which took 16 seconds and 6 GB of reads.

WHAT A TIER IS

An ordinary EXOSTAR1 catalogue holding exactly the main file's stars at or brighter than its cut, in
the same order, under the same declination bands. Any reader loads it, and searching it down to its
cut returns the same stars as the main file. Core/TieredStarCatalog.cs finds tiers by name beside
the main file (GaiaStarCatalog.V17.starcat next to GaiaStarCatalog.starcat) and searches the
shallowest one deep enough for the frame.

USAGE

    python3 tools/tier_star_catalog.py <KSP>/GameData/ExoInstruments/PluginData/GaiaStarCatalog.starcat

writes V13, V15, V17 and V19 tiers beside it. Measured on the all-sky file those hold 5.57, 27.7,
117.2 and 426.9 million stars: 78 MB, 388 MB, 1.64 GB and 5.98 GB, in one 33 s pass over the file.

    --cuts 13,15,17     other cuts (V, at most three decimals)
    --force             rebuild tiers that already exist

Needs numpy. Every tier is checked against the main file before it replaces anything.
"""

import argparse
import os
import shutil
import struct
import sys
import time

try:
    import numpy as np
except ImportError:
    sys.exit("numpy is needed: run this with a Python that has it, for example the one setup_data.py "
             "builds, or `python3 -m pip install numpy`.")

MAGIC = b"EXOSTAR1"
HEADER_BYTES = 24
V_MAG_OFFSET = 2.0


def record_dtype(version):
    names, formats, offsets = ["ra", "dec", "v", "bv"], ["<u4", "<i4", "<u2", "<i2"], [0, 4, 8, 10]
    if version >= 3:
        names.append("ebv")
        formats.append("<u2")
        offsets.append(12)
    return np.dtype({"names": names, "formats": formats, "offsets": offsets,
                     "itemsize": 14 if version >= 3 else 12})


class Catalogue:
    def __init__(self, path):
        with open(path, "rb") as f:
            head = f.read(HEADER_BYTES)
            if len(head) < HEADER_BYTES or head[:8] != MAGIC:
                sys.exit(f"{path}: not an EXOSTAR1 catalogue")
            self.version, self.count, self.bands = struct.unpack_from("<iii", head, 8)
            if self.version not in (2, 3):
                sys.exit(f"{path}: unsupported catalogue version {self.version}")
            # Copied as bytes: the reader bands by the float32 exactly as stored.
            self.width_bytes = head[20:24]
            self.index = np.frombuffer(f.read(4 * (self.bands + 1)), dtype="<u4").astype(np.int64)
        self.path = path
        self.dtype = record_dtype(self.version)
        self.records_at = HEADER_BYTES + 4 * (self.bands + 1)
        size = os.path.getsize(path)
        need = self.records_at + self.count * self.dtype.itemsize
        if size != need:
            sys.exit(f"{path}: {size} bytes where {self.count} stars need {need}")
        if self.index[-1] != self.count or np.any(np.diff(self.index) < 0):
            sys.exit(f"{path}: its declination index is broken")

    def band(self, handle, b):
        lo, hi = int(self.index[b]), int(self.index[b + 1])
        handle.seek(self.records_at + lo * self.dtype.itemsize)
        records = np.fromfile(handle, dtype=self.dtype, count=hi - lo)
        if records.size != hi - lo:
            sys.exit(f"{self.path}: short read in band {b}")
        return records


def milli(cut):
    return int(round((cut + V_MAG_OFFSET) * 1000.0))


def label(cut):
    return f"{cut:.3f}".rstrip("0").rstrip(".")


def estimate_fractions(cat, cuts, blocks=512, per_block=8192):
    """Share of stars under each cut, from blocks spread evenly through the file."""
    starts = np.linspace(0, max(0, cat.count - per_block), blocks).astype(np.int64)
    v = []
    with open(cat.path, "rb") as f:
        for s in starts:
            f.seek(cat.records_at + int(s) * cat.dtype.itemsize)
            v.append(np.fromfile(f, dtype=cat.dtype, count=per_block)["v"])
    v = np.concatenate(v)
    return {cut: float((v <= milli(cut)).mean()) for cut in cuts}


def verify(cat, tier_path, cut, sample_bands=64):
    """Re-reads a written tier: header, index, size, and whole bands against the main file."""
    tier = Catalogue(tier_path)
    if tier.version != cat.version or tier.bands != cat.bands or tier.width_bytes != cat.width_bytes:
        return "its header does not match the main catalogue's"
    sizes = np.diff(tier.index)
    if np.any(sizes > np.diff(cat.index)):
        return "a band holds more stars than the main catalogue's"
    populated = np.nonzero(np.diff(cat.index))[0]
    chosen = set(populated[np.linspace(0, populated.size - 1, min(sample_bands, populated.size)).astype(int)])
    chosen.add(int(np.argmax(np.diff(cat.index))))
    with open(cat.path, "rb") as main, open(tier_path, "rb") as cut_file:
        for b in sorted(chosen):
            expected = cat.band(main, b)
            expected = expected[expected["v"] <= milli(cut)]
            got = tier.band(cut_file, b)
            if expected.tobytes() != got.tobytes():
                return f"band {b} differs from the main catalogue's stars at V {label(cut)} or brighter"
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("catalogue", help="the packed .starcat to cut tiers from")
    parser.add_argument("--cuts", default="13,15,17,19", help="V cuts, comma separated (default: %(default)s)")
    parser.add_argument("--force", action="store_true", help="rebuild tiers that already exist")
    args = parser.parse_args()

    try:
        cuts = sorted({float(c) for c in args.cuts.split(",") if c.strip()})
    except ValueError:
        sys.exit(f"--cuts must be numbers, got {args.cuts!r}")
    if not cuts or any(round(c, 3) != c or not -2.0 < c < 60.0 for c in cuts):
        sys.exit("each cut must be a V magnitude between -2 and 60 with at most three decimals")

    cat = Catalogue(args.catalogue)
    # Beside the path as given, not its target, so a linked catalogue gets its tiers where it is read.
    directory = os.path.dirname(os.path.abspath(args.catalogue))
    stem = os.path.splitext(os.path.basename(args.catalogue))[0]
    print(f"{args.catalogue}: {cat.count:,} stars in {cat.bands} bands, format v{cat.version}")
    if cat.count < 50_000_000:
        print("A catalogue this shallow searches fast without tiers; cutting them anyway.")

    todo = []
    for cut in cuts:
        final = os.path.join(directory, f"{stem}.V{label(cut)}.starcat")
        if os.path.exists(final) and not args.force:
            print(f"exists, skipped (--force rebuilds): {final}")
            continue
        todo.append((cut, final))
    if not todo:
        return

    fractions = estimate_fractions(cat, [c for c, _ in todo])
    need = sum(HEADER_BYTES + 4 * (cat.bands + 1) + fractions[c] * cat.count * cat.dtype.itemsize for c, _ in todo)
    free = shutil.disk_usage(directory).free
    for cut, final in todo:
        print(f"  V {label(cut):>6}: about {fractions[cut] * cat.count / 1e6:,.1f} M stars, "
              f"{fractions[cut] * cat.count * cat.dtype.itemsize / 1e9:.2f} GB -> {os.path.basename(final)}")
    if need * 1.05 > free:
        sys.exit(f"About {need / 1e9:.1f} GB is needed and {free / 1e9:.1f} GB is free in {directory}.")

    outputs = []
    for cut, final in todo:
        part = final + ".part"
        handle = open(part, "wb")
        handle.write(MAGIC + struct.pack("<iii", cat.version, 0, cat.bands) + cat.width_bytes)
        handle.write(bytes(4 * (cat.bands + 1)))
        outputs.append({"cut": cut, "milli": milli(cut), "final": final, "part": part, "handle": handle,
                        "counts": np.zeros(cat.bands, dtype=np.int64)})

    started = time.time()
    done = 0
    with open(cat.path, "rb") as source:
        for b in range(cat.bands):
            n = int(cat.index[b + 1] - cat.index[b])
            if n:
                records = cat.band(source, b)
                v = records["v"]
                for out in outputs:
                    kept = records[v <= out["milli"]]
                    kept.tofile(out["handle"])
                    out["counts"][b] = kept.size
            done += n
            if b % 90 == 89 or b == cat.bands - 1:
                elapsed = time.time() - started
                eta = elapsed * (cat.count - done) / max(1, done)
                print(f"  band {b + 1:>4}/{cat.bands}, {done / cat.count:6.1%} of stars, "
                      f"{elapsed:5.0f} s elapsed, about {eta:4.0f} s left", flush=True)

    failed = False
    for out in outputs:
        counts = out["counts"]
        index = np.concatenate([[0], np.cumsum(counts)]).astype("<u4")
        handle = out["handle"]
        handle.seek(12)
        handle.write(struct.pack("<i", int(counts.sum())))
        handle.seek(HEADER_BYTES)
        handle.write(index.tobytes())
        handle.close()
        fault = verify(cat, out["part"], out["cut"])
        if fault:
            print(f"REFUSED {os.path.basename(out['final'])}: {fault}. Left as {out['part']}.")
            failed = True
            continue
        os.replace(out["part"], out["final"])
        print(f"wrote {out['final']}: {int(counts.sum()):,} stars, {os.path.getsize(out['final']) / 1e9:.2f} GB")
    if failed:
        sys.exit(1)


if __name__ == "__main__":
    main()
