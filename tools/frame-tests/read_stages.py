#!/usr/bin/env python3
"""Reads the pipeline stage dumps and names the stage that introduced an artefact.

The mod writes one file per stage of the signal plane when the stage-dump toggle is on. This walks
them in order and reports, for a region under investigation, what each stage did to it -- so the
stage that creates a deficit is identified by its name rather than by elimination.

Run:
    ./env/bin/python read_stages.py --dir "<KSP>/Screenshots/ExoInstruments/stages" \
        --x 2100 2150 --y 1600 1740
"""

import argparse
import glob
import os
import struct
import sys

import numpy as np


def read(path):
    with open(path, "rb") as f:
        w, h = struct.unpack("<ii", f.read(8))
        (mean,) = struct.unpack("<d", f.read(8))
        data = np.frombuffer(f.read(4 * w * h), dtype="<f4").reshape(h, w)
    return data, mean


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dir", required=True)
    p.add_argument("--x", nargs=2, type=int, default=[2100, 2150])
    p.add_argument("--y", nargs=2, type=int, default=[1600, 1740])
    p.add_argument("--step", type=int, default=10)
    args = p.parse_args()

    files = sorted(glob.glob(os.path.join(args.dir, "stage*.bin")))
    if not files:
        raise SystemExit(f"no stage dumps in {args.dir}")

    x0, x1 = args.x
    ys = list(range(args.y[0], args.y[1], args.step))
    print(f"column x {x0}-{x1}, electrons, one row per stage\n")
    print("  stage".ljust(22) + "".join(f"{y:>7d}" for y in ys))

    previous = None
    for path in files:
        data, mean = read(path)
        name = os.path.basename(path).replace(".bin", "")
        row = [data[y, x0:x1].mean() for y in ys]
        print("  " + name.ljust(20) + "".join(f"{v:7.1f}" for v in row))

        # The stage that MATTERS is the one that changes the profile's SHAPE, not its level.
        if previous is not None:
            a, b = np.array(previous), np.array(row)
            if a.std() > 1e-9 and b.std() > 1e-9:
                flat_before = a.std() / max(1e-9, a.mean())
                flat_after = b.std() / max(1e-9, b.mean())
                if flat_after > 3 * flat_before + 0.05:
                    print(f"      ^^^ this stage introduced the structure: relative spread "
                          f"{flat_before:.3f} -> {flat_after:.3f}")
        previous = row
    return 0


if __name__ == "__main__":
    sys.exit(main())
