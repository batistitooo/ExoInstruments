"""Cross-validates ExoInstruments' zscale against astropy's ZScaleInterval.

A display transfer curve decides how the range between black and white is distributed. It does not
decide where black and white are, and on an astronomical frame that is the larger question: a faint
nebula spans a fraction of a percent of the converter's range, and mapping the full range to the
display buries it. zscale (Tody 1986) is what IRAF, DS9 and every descendant use to answer it, so
the reference is the same algorithm as implemented independently in astropy.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_zscale.py
"""

import struct
import sys

import numpy as np
from astropy.visualization import ZScaleInterval

failures = []


def load(name):
    with open(f"frame_{name}.bin", "rb") as f:
        (n,) = struct.unpack("<i", f.read(4))
        return np.frombuffer(f.read(4 * n), dtype="<f4")


def main():
    print(__doc__.split("Run:")[0].strip())
    rows = np.genfromtxt("exo_zscale.csv", delimiter=",", names=True, dtype=None, encoding="utf-8")
    rows = np.atleast_1d(rows)

    print("\n1. Black and white points, against astropy")
    interval = ZScaleInterval(nsamples=1000, contrast=0.25, max_reject=0.5,
                              min_npixels=5, krej=2.5, max_iterations=5)
    worst = 0.0
    for row in rows:
        name = str(row["name"])
        frame = load(name)
        ref_lo, ref_hi = interval.get_limits(frame)
        span = max(1e-30, abs(ref_hi - ref_lo))
        d_lo = abs(row["black"] - ref_lo) / span
        d_hi = abs(row["white"] - ref_hi) / span
        dev = max(d_lo, d_hi)
        worst = max(worst, dev)
        ok = dev < 0.02
        if not ok:
            failures.append(name)
        print(f"  [{'ok  ' if ok else 'FAIL'}] {name:<15} ours [{row['black']:.6g}, {row['white']:.6g}]  "
              f"astropy [{ref_lo:.6g}, {ref_hi:.6g}]  ->  {dev*100:.2f}% of the span")

    print("\n2. What it buys on the faint frame")
    frame = load("faint_nebula")
    row = rows[[str(r["name"]) for r in rows].index("faint_nebula")]
    span = row["white"] - row["black"]
    print(f"  [note] the frame occupies {frame.min():.6f} to {frame.max():.6f} of full scale")
    print(f"  [note] zscale shows {row['black']:.6f} to {row['white']:.6f}, a {1/span:.0f}x stretch")
    print(f"  [note] without it the subject spans {(frame.max()-frame.min())*255:.1f} of 255 display "
          f"levels; with it, the full 255")

    print("\n3. A saturated star must not set the white point")
    frame = load("star_field")
    row = rows[[str(r["name"]) for r in rows].index("star_field")]
    ok = row["white"] < 0.5
    if not ok:
        failures.append("star_field white point")
    print(f"  [{'ok  ' if ok else 'FAIL'}] frame reaches {frame.max():.3f}; white point stays at "
          f"{row['white']:.4f}, set by the sky's own noise rather than by the brightest pixel")

    print("\n" + "-" * 78)
    print(f"NOTE: zscale matches astropy to {worst*100:.2f}% of the displayed span over "
          f"{len(rows)} frames")
    if failures:
        print(f"\n{len(failures)} CHECK(S) FAILED:")
        for f in failures:
            print("  - " + f)
        return 1
    print("\nALL CHECKS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
