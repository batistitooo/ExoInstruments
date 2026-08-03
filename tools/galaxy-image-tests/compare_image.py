#!/usr/bin/env python3
"""Checks the shipped shape-map renderer against an independent reprojection.

Everything here is rebuilt from the packed file with astropy's own WCS machinery and numpy, with no
reference to how the C# does it: the deprojection of a map pixel into a sky direction, the frame's
own gnomonic projection, the transform between the two tangent planes, and the resampling. A
mirrored transform, an affine approximation standing in for a projective one, or flux that is not
conserved when the two pixel grids differ in size all show up as a disagreement rather than as a
picture that still looks like a galaxy.

Run, after DumpGalaxyImage has written its files:
    ./env/bin/python compare_image.py <GalaxyImages.galimg> <name>
"""

import glob
import math
import struct
import sys

import numpy as np


def read_galimg(path):
    f = open(path, "rb")

    def i32():
        return struct.unpack("<i", f.read(4))[0]

    def f64():
        return struct.unpack("<d", f.read(8))[0]

    def s():
        return f.read(i32()).decode("utf-8")

    assert f.read(8) == b"EXOGIMG1", "not a packed galaxy image set"
    version, count = struct.unpack("<ii", f.read(8))
    source = s()
    entries = {}
    for _ in range(count):
        name = s()
        ra, dec = struct.unpack("<dd", f.read(16))
        n = i32()
        scale = f64()
        survey = s()
        f.read(1)
        masked, inside = struct.unpack("<ff", f.read(8))
        companions = [s() for _ in range(i32())]
        bands = i32()
        planes = []
        for _ in range(bands):
            wl = f64()
            label = s()
            band_scale = f64()
            data = np.frombuffer(f.read(n * n * 2), dtype="<f2").astype(np.float64) * band_scale
            planes.append({"wavelength": wl, "label": label, "values": data.reshape(n, n)})
        entries[name] = {"ra": ra, "dec": dec, "size": n, "scale": scale, "survey": survey,
                         "masked": masked, "inside": inside, "companions": companions,
                         "planes": planes}
    return source, entries


def read_frame(path):
    d = open(path, "rb").read()
    w, h = struct.unpack_from("<ii", d, 0)
    fov, ra, dec, total, deposited = struct.unpack_from("<ddddd", d, 8)
    h8 = struct.unpack_from("<8d", d, 48)
    corners = struct.unpack_from("<8d", d, 48 + 64)
    plane = np.frombuffer(d, dtype="<f4", count=w * h, offset=48 + 64 + 64).reshape(h, w)
    return {"w": w, "h": h, "fov": fov, "ra": ra, "dec": dec, "total": total,
            "deposited": deposited, "homography": h8, "corners": corners, "plane": plane}


def unit(v):
    return v / np.linalg.norm(v, axis=0)


def basis(ra_deg, dec_deg):
    """Centre, east and north unit vectors of a tangent plane, in equatorial cartesian."""
    ra, dec = math.radians(ra_deg), math.radians(dec_deg)
    c = np.array([math.cos(dec) * math.cos(ra), math.cos(dec) * math.sin(ra), math.sin(dec)])
    e = np.array([-math.sin(ra), math.cos(ra), 0.0])
    n = np.array([-math.sin(dec) * math.cos(ra), -math.sin(dec) * math.sin(ra), math.cos(dec)])
    return c, e, n


def frame_directions(frame):
    """Sky direction of every frame pixel centre, rebuilt from the harness's stated geometry.

    The harness builds its sensor basis as up = north and right = up x boresight, which comes out
    as +east: its frames therefore run east to the RIGHT, while the stored maps run east to the
    LEFT. That mirror is deliberate, and it is exactly what a transform solved from four
    correspondences has to survive.
    """
    c, e, n = basis(frame["ra"], frame["dec"])
    right, up = e, n
    tan_half_w = math.tan(0.5 * math.radians(frame["fov"]))
    tan_half_h = tan_half_w * frame["h"] / frame["w"]

    ys, xs = np.mgrid[0:frame["h"], 0:frame["w"]]
    xi = ((xs + 0.5) / (0.5 * frame["w"]) - 1.0) * tan_half_w
    eta = ((ys + 0.5) / (0.5 * frame["h"]) - 1.0) * tan_half_h
    d = (c[:, None, None] + xi[None, :, :] * right[:, None, None]
         + eta[None, :, :] * up[:, None, None])
    return unit(d)


def frame_directions_offset(frame, offs_x, offs_y):
    """As frame_directions, but sampling a point offset inside each pixel."""
    c, e, n = basis(frame["ra"], frame["dec"])
    tan_half_w = math.tan(0.5 * math.radians(frame["fov"]))
    tan_half_h = tan_half_w * frame["h"] / frame["w"]
    ys, xs = np.mgrid[0:frame["h"], 0:frame["w"]]
    xi = ((xs + 0.5 + offs_x) / (0.5 * frame["w"]) - 1.0) * tan_half_w
    eta = ((ys + 0.5 + offs_y) / (0.5 * frame["h"]) - 1.0) * tan_half_h
    d = (c[:, None, None] + xi[None, :, :] * e[:, None, None]
         + eta[None, :, :] * n[:, None, None])
    return unit(d)


def sky_to_map(directions, entry):
    """Frame directions to map pixels, by the gnomonic projection of the map's own tangent plane."""
    c, e, n = basis(entry["ra"], entry["dec"])
    w = np.einsum("i...,i->...", directions, c)
    xi = np.einsum("i...,i->...", directions, e) / w
    eta = np.einsum("i...,i->...", directions, n) / w
    scale_rad = math.radians(entry["scale"] / 3600.0)
    centre = entry["size"] // 2 - 1
    u = centre - xi / scale_rad          # east is -u
    v = centre + eta / scale_rad         # north is +v
    return u, v


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    source, entries = read_galimg(sys.argv[1])
    name = sys.argv[2]
    if name not in entries:
        print("no map for", name)
        return 1
    entry = entries[name]
    print("%s: %d px at %.3f arcsec, %s" % (name, entry["size"], entry["scale"], entry["survey"]))

    failures = 0

    # 1. Every band sums to one. That contract is what keeps the photometry the catalogue's.
    for plane in entry["planes"]:
        total = plane["values"].sum()
        ok = abs(total - 1.0) < 2e-6
        failures += 0 if ok else 1
        print("  %-12s sum %.9f  %s" % (plane["label"], total, "PASS" if ok else "FAIL"))

    # 2. The deprojection of map pixels, against astropy's own WCS for the same tangent plane.
    from astropy.wcs import WCS
    w = WCS(naxis=2)
    n = entry["size"]
    w.wcs.crpix = [n / 2.0, n / 2.0]
    w.wcs.cdelt = [-entry["scale"] / 3600.0, entry["scale"] / 3600.0]
    w.wcs.crval = [entry["ra"], entry["dec"]]
    w.wcs.ctype = ["RA---TAN", "DEC--TAN"]

    corners = np.loadtxt("exo_corners.csv", delimiter=",", skiprows=1)
    ra_ref, dec_ref = w.all_pix2world(corners[:, 0], corners[:, 1], 0)
    dra = (corners[:, 2] - ra_ref) * np.cos(np.radians(dec_ref)) * 3600.0
    ddec = (corners[:, 3] - dec_ref) * 3600.0
    worst = float(np.max(np.hypot(dra, ddec)))
    ok = worst < 1e-3
    failures += 0 if ok else 1
    print("  map deprojection vs astropy WCS: worst %.2e arcsec  %s" % (worst, "PASS" if ok else "FAIL"))

    # 3. The transform, and the deposit, per frame.
    for path in sorted(glob.glob("exo_frame_*.bin")):
        frame = read_frame(path)
        directions = frame_directions(frame)
        u_ref, v_ref = sky_to_map(directions, entry)

        ys, xs = np.mgrid[0:frame["h"], 0:frame["w"]]
        px, py = xs + 0.5, ys + 0.5
        h0, h1, h2, h3, h4, h5, h6, h7 = frame["homography"]
        den = h6 * px + h7 * py + 1.0
        u_shipped = (h0 * px + h1 * py + h2) / den
        v_shipped = (h3 * px + h4 * py + h5) / den

        inside = (u_ref > 0) & (u_ref < entry["size"] - 1) & (v_ref > 0) & (v_ref < entry["size"] - 1)
        if not inside.any():
            print("  %s: the map does not land on the frame" % path)
            continue
        offset = float(np.max(np.hypot(u_shipped[inside] - u_ref[inside],
                                       v_shipped[inside] - v_ref[inside])))
        ok = offset < 1e-3
        failures += 0 if ok else 1
        print("  %s transform vs independent projection: worst %.2e map px  %s"
              % (path, offset, "PASS" if ok else "FAIL"))

        # An affine transform fitted to the same four corners, to show what the projective one is
        # worth: this is the error that would have been shipped had the corners been used to derive
        # a rotation and a scale instead.
        cx = np.array(frame["corners"][0::2])
        cy = np.array(frame["corners"][1::2])
        last = entry["size"] - 1
        mu = np.array([0.0, last, 0.0, last])
        mv = np.array([0.0, 0.0, last, last])
        A = np.column_stack([cx, cy, np.ones(4)])
        au, *_ = np.linalg.lstsq(A, mu, rcond=None)
        av, *_ = np.linalg.lstsq(A, mv, rcond=None)
        u_affine = au[0] * px + au[1] * py + au[2]
        v_affine = av[0] * px + av[1] * py + av[2]
        affine_error = float(np.max(np.hypot(u_affine[inside] - u_ref[inside],
                                             v_affine[inside] - v_ref[inside])))
        print("      (an affine fit to the same corners would be off by %.2f map px, %.1f arcsec)"
              % (affine_error, affine_error * entry["scale"]))

        # 4. The deposit itself: the same resampling done here, and the totals compared.
        from scipy.ndimage import map_coordinates
        plane = entry["planes"][0]["values"]
        # Jacobian by finite differences on the independent projection, so it is not the shipped
        # analytic one being checked against itself.
        du_dx = np.gradient(u_ref, axis=1)
        du_dy = np.gradient(u_ref, axis=0)
        dv_dx = np.gradient(v_ref, axis=1)
        dv_dy = np.gradient(v_ref, axis=0)
        jac = np.abs(du_dx * dv_dy - du_dy * dv_dx)

        # A frame pixel of this field can cover tens of map pixels, and point sampling one of them
        # is not what the light does. The reference integrates over the pixel too, with its own
        # supersampling on its own projection, so what is compared is two integrations rather than
        # an integration against a sample.
        steps = int(min(8, max(1, math.ceil(math.sqrt(float(np.median(jac[inside])))))))
        accum = np.zeros_like(u_ref)
        for sy in range(steps):
            for sx in range(steps):
                sub = dict(frame)
                offs_x = (sx + 0.5) / steps - 0.5
                offs_y = (sy + 0.5) / steps - 0.5
                d = frame_directions_offset(frame, offs_x, offs_y)
                uu, vv = sky_to_map(d, entry)
                accum += map_coordinates(plane, [np.clip(vv, 0, entry["size"] - 1),
                                                 np.clip(uu, 0, entry["size"] - 1)],
                                         order=1, mode="constant", cval=0.0)
        sampled = accum / (steps * steps)
        expected = np.where(inside, sampled * jac * frame["total"], 0.0)

        shipped_total = float(frame["plane"].sum())
        ratio = shipped_total / max(expected.sum(), 1e-30)
        ok = abs(ratio - 1.0) < 0.02
        failures += 0 if ok else 1
        print("      flux: shipped %.6e, independent %.6e, ratio %.5f  %s"
              % (shipped_total, expected.sum(), ratio, "PASS" if ok else "FAIL"))

        # Where the two disagree pixel by pixel, relative to the frame's own peak. Supersampling
        # against a single-point interpolation cannot agree exactly on a resolved galaxy; what
        # matters is that the disagreement stays at the interpolation's own level.
        peak = float(frame["plane"].max())
        if peak > 0:
            diff = np.abs(frame["plane"] - expected) / peak
            print("      per-pixel difference: median %.2e, 99th %.2e, worst %.2e of the peak"
                  % (np.median(diff), np.percentile(diff, 99), diff.max()))

    print("%d check(s) failed" % failures)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
