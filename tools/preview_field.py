#!/usr/bin/env python3
"""Renders an installed all-sky map through an instrument's exact geometry, outside the game.

WHY THIS EXISTS. When a frame shows something odd, there are two very different places it can come
from: the DATA the map holds toward that direction, or the PIPELINE that turns it into electrons.
This reads the packed map with the same projection, plate scale and sensor size the camera uses and
writes a PNG of nothing but the map. Anything visible here is in the survey; anything visible in
the game but not here is in the pipeline.

Deliberately NOT a physics model: no PSF, no noise, no detector. It is the map, projected.

Run:
    ./env/bin/python preview_field.py --ra 05:41:00 --dec -02:12:17 --instrument redcat --binning 1

Add --zoom to also write a 1:1 crop of the centre, which is where a pixel-scale artefact shows.
"""

import argparse
import struct
import sys
import zlib

import numpy as np

# Plate scale in arcsec per NATIVE pixel, and native sensor size, matching
# Core/VisualTelescopeCatalog. Kept here rather than parsed out of the C# so that a disagreement
# between the two is visible as a disagreement rather than silently inherited.
INSTRUMENTS = {
    # name:      (pixel_m,  focal_m, barlow, width, height)
    "redcat":    (4.63e-6,  0.250,   1.0,    4144, 2822),
    "rc20":      (4.63e-6,  3.454,   1.0,    4144, 2822),
    "cdk1000":   (4.63e-6,  6.000,   1.0,    4144, 2822),
    "fors2":     (15.0e-6,  49.09,   1.0,    2048, 2048),
    "sphere":    (15.0e-6,  1718.7,  1.0,    2048, 2048),
}

ARCSEC_PER_RAD = 180.0 * 3600.0 / np.pi


def sexagesimal(text, hours):
    """'05:41:00' or '05h41m00s' or a plain decimal degree value."""
    t = text.strip().replace("h", ":").replace("m", ":").replace("s", "")
    t = t.replace("d", ":").replace("'", ":").replace('"', "")
    parts = [p for p in t.split(":") if p not in ("", "+")]
    if len(parts) == 1:
        return float(parts[0])
    sign = -1.0 if parts[0].strip().startswith("-") else 1.0
    values = [abs(float(p)) for p in parts]
    deg = values[0] + (values[1] if len(values) > 1 else 0.0) / 60.0 \
        + (values[2] if len(values) > 2 else 0.0) / 3600.0
    return sign * deg * (15.0 if hours else 1.0)


def read_emission_map(path):
    with open(path, "rb") as f:
        magic = f.read(8)
        if magic != b"EXOEMIS1":
            raise SystemExit(f"{path} is not a packed emission map")
        version, nside = struct.unpack("<ii", f.read(8))
        nested = struct.unpack("<B", f.read(1))[0]
        (wavelength,) = struct.unpack("<d", f.read(8))
        n, = struct.unpack("<i", f.read(4)); name = f.read(n).decode()
        n, = struct.unpack("<i", f.read(4)); source = f.read(n).decode()
        values = np.frombuffer(f.read(), dtype="<f2").astype(np.float64)
    return values, nside, bool(nested), name, source


def project(ra0, dec0, scale_arcsec, w, h):
    """Gnomonic TAN, the projection Core/GnomonicProjection implements."""
    x = (np.arange(w) - w / 2 + 0.5) * scale_arcsec / 3600.0
    y = (np.arange(h) - h / 2 + 0.5) * scale_arcsec / 3600.0
    X, Y = np.meshgrid(np.deg2rad(x), np.deg2rad(y))
    r = np.hypot(X, Y)
    c = np.arctan(r)
    d0, a0 = np.deg2rad(dec0), np.deg2rad(ra0)
    with np.errstate(invalid="ignore", divide="ignore"):
        dec = np.arcsin(np.cos(c) * np.sin(d0) + np.where(r > 0, Y * np.sin(c) * np.cos(d0) / r, 0.0))
        ra = a0 + np.arctan2(X * np.sin(c), r * np.cos(d0) * np.cos(c) - Y * np.sin(d0) * np.sin(c))
    return np.where(r > 0, ra, a0), np.where(r > 0, dec, d0)


def write_png(path, gray):
    h, w = gray.shape
    raw = b"".join(b"\x00" + gray[y].tobytes() for y in range(h))

    def chunk(tag, data):
        payload = tag + data
        return struct.pack(">I", len(data)) + payload + struct.pack(">I", zlib.crc32(payload) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 0, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(raw, 9)))
        f.write(chunk(b"IEND", b""))


def siril_autostretch(a, shadow_clip=-2.8, target_background=0.25):
    """Siril's own screen transfer function: a midtone transfer curve anchored on the median and
    the MAD, which is what the user is actually looking through. Far harder than a percentile log,
    and therefore the honest stretch to hunt a faint artefact under."""
    median = np.median(a)
    mad = np.median(np.abs(a - median)) * 1.4826
    lo = max(a.min(), median + shadow_clip * mad)
    x = np.clip((a - lo) / max(1e-12, a.max() - lo), 0.0, 1.0)

    def mtf(v, m):
        return ((m - 1.0) * v) / ((2.0 * m - 1.0) * v - m)

    target, lo_m, hi_m = np.median(x), 1e-6, 0.5
    for _ in range(60):
        mid = 0.5 * (lo_m + hi_m)
        if mtf(target, mid) < target_background:
            hi_m = mid
        else:
            lo_m = mid
    return mtf(x, 0.5 * (lo_m + hi_m))


def stretch(values, mode):
    if mode == "autostretch":
        finite = np.isfinite(values)
        return (siril_autostretch(np.where(finite, values, np.nanmedian(values))) * 255).astype(np.uint8)
    finite = np.isfinite(values)
    v = np.where(finite, values, 0.0)
    lo, hi = np.percentile(v[finite], [0.5, 99.9]) if finite.any() else (0.0, 1.0)
    v = np.clip((v - lo) / max(1e-12, hi - lo), 0.0, 1.0)
    if mode == "log":
        v = np.log10(1.0 + 1000.0 * v) / np.log10(1001.0)
    elif mode == "asinh":
        v = np.arcsinh(v / 0.02) / np.arcsinh(1.0 / 0.02)
    return (v * 255).astype(np.uint8)


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--map", default=None, help="packed .emission file; defaults to the installed one")
    p.add_argument("--ra", required=True, help="right ascension, e.g. 05:41:00")
    p.add_argument("--dec", required=True, help="declination, e.g. -02:12:17")
    p.add_argument("--instrument", default="redcat", choices=sorted(INSTRUMENTS))
    p.add_argument("--binning", type=int, default=1)
    p.add_argument("--stretch", default="log", choices=["linear", "log", "asinh", "autostretch"])
    p.add_argument("--creases", action="store_true",
                   help="also write the Laplacian, which isolates the interpolation's facet edges")
    p.add_argument("--zoom", action="store_true", help="also write a 1:1 crop of the centre")
    p.add_argument("--out", default="field")
    args = p.parse_args()

    import healpy as hp

    path = args.map or (
        "/Users/baptiste/Library/Application Support/Steam/steamapps/common/"
        "Kerbal Space Program/GameData/ExoInstruments/PluginData/HalphaMap.emission")
    values, nside, nested, line, source = read_emission_map(path)

    pixel_m, focal_m, barlow, native_w, native_h = INSTRUMENTS[args.instrument]
    scale = pixel_m * args.binning / (focal_m * barlow) * ARCSEC_PER_RAD
    w, h = native_w // args.binning, native_h // args.binning

    ra0 = sexagesimal(args.ra, hours=True)
    dec0 = sexagesimal(args.dec, hours=False)

    print(f"{line} map, nside {nside} ({hp.nside2resol(nside, arcmin=True):.2f} arcmin), {source}")
    print(f"{args.instrument} at {args.binning}x{args.binning}: {w} x {h} px, {scale:.4f} arcsec/px, "
          f"field {w*scale/3600:.2f} x {h*scale/3600:.2f} deg")
    print(f"pointing RA {ra0:.5f} deg  Dec {dec0:+.5f} deg")
    print(f"one map cell is {hp.nside2resol(nside, arcmin=True)*60/scale:.0f} frame pixels across")

    ra, dec = project(ra0, dec0, scale, w, h)
    theta, phi = hp.rotator.Rotator(coord=["C", "G"])(np.pi / 2 - dec.ravel(), ra.ravel())

    interp = hp.get_interp_val(values, theta, phi, nest=nested).reshape(h, w)
    nearest = values[hp.ang2pix(nside, theta, phi, nest=nested)].reshape(h, w)

    print(f"surface brightness over the field: {np.nanmin(interp):.1f} to {np.nanmax(interp):.1f} R, "
          f"median {np.nanmedian(interp):.1f} R")
    missing = int(np.count_nonzero(~np.isfinite(nearest)))
    print(f"pixels the map has no value for: {missing}")

    # A local minimum surrounded by much brighter gas is what a dark blotch IS, so it is measured
    # rather than eyeballed: how far below its own surroundings the darkest cells sit.
    from scipy.ndimage import uniform_filter
    smooth = uniform_filter(np.nan_to_num(interp), size=max(3, int(600 / scale)))
    with np.errstate(invalid="ignore", divide="ignore"):
        deficit = np.where(smooth > 0, interp / smooth, 1.0)
    print(f"darkest cell relative to its own surroundings: {np.nanmin(deficit)*100:.0f}% "
          f"({int(np.count_nonzero(deficit < 0.5))} pixels below half)")

    for tag, arr in (("interp", interp), ("nearest", nearest)):
        write_png(f"{args.out}_{tag}.png", stretch(arr, args.stretch))
        print(f"wrote {args.out}_{tag}.png")

    if args.creases:
        # Bilinear interpolation is continuous but its DERIVATIVE is not, so cell boundaries leave
        # creases. The Laplacian isolates them, and the worst step between adjacent pixels says
        # whether any of it is visible at all against 255 display levels.
        v = siril_autostretch(np.nan_to_num(interp))
        lap = np.abs(4 * v[1:-1, 1:-1] - v[:-2, 1:-1] - v[2:, 1:-1] - v[1:-1, :-2] - v[1:-1, 2:])
        step = np.percentile(np.abs(np.diff(v, axis=1)), 99.99) * 255
        print(f"worst step between adjacent pixels under Siril's own autostretch: "
              f"{step:.2f} of 255 display levels")
        write_png(f"{args.out}_creases.png",
                  (np.clip(lap / max(1e-12, np.percentile(lap, 99.9)), 0, 1) * 255).astype(np.uint8))
        print(f"wrote {args.out}_creases.png")

    if args.zoom:
        cw, ch = min(1000, w), min(700, h)
        y0, x0 = (h - ch) // 2, (w - cw) // 2
        write_png(f"{args.out}_zoom.png", stretch(interp[y0:y0+ch, x0:x0+cw], args.stretch))
        print(f"wrote {args.out}_zoom.png (1:1 crop of the centre, {cw} x {ch})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
