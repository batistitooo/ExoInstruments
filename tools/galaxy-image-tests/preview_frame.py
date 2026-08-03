#!/usr/bin/env python3
"""Renders the dumped deposits as a picture, so the difference can be looked at rather than argued.

NOT the shipped pipeline. The deposit itself IS the shipped code (`DumpGalaxyImage` calls
`GalaxyImageRenderer` and `GalaxyRenderer`); what this adds on top is a Gaussian of the site seeing,
a sky background, Poisson noise and a display stretch, all written here for the picture. It is a
comparison of the two SHAPE models under the same everything else, not a prediction of what a
capture will look like.

Run:
    ./env/bin/python preview_frame.py exo_frame_0.32.bin exo_sersic_0.32.bin --out compare.png
"""

import argparse
import struct
import sys

import numpy as np


def read_frame(path):
    d = open(path, "rb").read()
    w, h = struct.unpack_from("<ii", d, 0)
    fov, ra, dec, total, deposited = struct.unpack_from("<ddddd", d, 8)
    plane = np.frombuffer(d, dtype="<f4", count=w * h, offset=48 + 64 + 64).reshape(h, w)
    return {"w": w, "h": h, "fov": fov, "total": total, "deposited": deposited,
            "plane": np.array(plane, dtype=np.float64)}


def observe(plane, fov_deg, width, seeing_arcsec, sky_e, read_noise, rng):
    from scipy.ndimage import gaussian_filter
    scale = fov_deg * 3600.0 / width
    sigma_px = (seeing_arcsec / 2.3548) / scale
    blurred = gaussian_filter(plane, max(sigma_px, 0.3))
    frame = rng.poisson(np.clip(blurred + sky_e, 0, None)).astype(np.float64)
    frame += rng.normal(0.0, read_noise, frame.shape)
    return frame - sky_e


def stretch(frame, lo_pct=20.0, hi_pct=99.85):
    lo = np.percentile(frame, lo_pct)
    hi = np.percentile(frame, hi_pct)
    if hi <= lo:
        hi = lo + 1.0
    x = np.clip((frame - lo) / (hi - lo), 0.0, 1.0)
    return np.arcsinh(10.0 * x) / np.arcsinh(10.0)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("frames", nargs="+")
    p.add_argument("--out", default="compare.png")
    p.add_argument("--seeing", type=float, default=1.5, help="arcsec FWHM")
    p.add_argument("--sky", type=float, default=1967.0, help="electrons per pixel")
    p.add_argument("--read-noise", type=float, default=1.2)
    p.add_argument("--seed", type=int, default=7)
    args = p.parse_args()

    from PIL import Image
    rng = np.random.default_rng(args.seed)

    panels = []
    for path in args.frames:
        f = read_frame(path)
        observed = observe(f["plane"], f["fov"], f["w"], args.seeing, args.sky,
                           args.read_noise, rng)
        panels.append((stretch(observed) * 255).astype(np.uint8)[::-1])
        print("%s: %.1f%% of the total flux landed on the sensor"
              % (path, 100.0 * f["deposited"] / f["total"]))

    gap = 8
    height = max(p.shape[0] for p in panels)
    width = sum(p.shape[1] for p in panels) + gap * (len(panels) - 1)
    canvas = np.zeros((height, width), dtype=np.uint8)
    x = 0
    for panel in panels:
        canvas[:panel.shape[0], x:x + panel.shape[1]] = panel
        x += panel.shape[1] + gap
    Image.fromarray(canvas).save(args.out)
    print("wrote", args.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
