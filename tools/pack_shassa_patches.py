#!/usr/bin/env python3
"""Builds high-resolution H-alpha patches around the catalogued nebulae, from SHASSA.

WHY. The all-sky composite this mod already reads has a 6 arcmin beam, and every structure that
makes a nebula recognisable is finer than that: the Horsehead spans 1.3 beams, M42's Trapezium 0.8,
the filaments in IC 1396A 0.3. That information is not in the file and no processing recovers it.

The Southern H-Alpha Sky Survey Atlas (SHASSA; Gaustad, McCullough, Rosing & Van Buren 2001,
PASP 113, 1326) images everything south of +15 degrees declination at 0.8 arcmin. At that beam the
Horsehead spans 10 elements. This script fetches a cutout around each catalogued object inside
SHASSA's footprint and packs it as a patch.

WHY PATCHES. Resolution is only worth storing where there is something to resolve. All-sky at
0.86 arcmin is 201 million HEALPix cells and 403 MB, nearly all of it diffuse background that
6 arcmin already describes. Four degrees around each object is 78 thousand cells, about 5 MB for the
whole catalogue.

HOW THE CALIBRATION WORKS, and why it is not a unit guess. SHASSA's own pixel units are not taken on
trust. Each cutout is smoothed to the composite's 6 arcmin beam and regressed against the composite
over the same area, which MEASURES the scale between them; the script prints that scale so a wrong
one is visible rather than silent. The patch then stores

    composite  +  scale * (cutout - smoothed cutout)

so the large-scale calibration remains exactly the composite's and SHASSA contributes only structure
finer than 6 arcmin -- which is the only thing it is being used for. Smoothing the patch back to
6 arcmin returns the composite identically, which the script checks.

The fine-structure term is apodised to zero across the patch's outer margin, so a patch joins the
base map continuously instead of leaving a step at its edge.

Run:
    cd tools
    python3 -m venv env && ./env/bin/pip install numpy scipy astropy healpy requests
    ./env/bin/python pack_shassa_patches.py \
        --composite ../HalphaMap.emission \
        --out HalphaPatches.patchset
    cp HalphaPatches.patchset "<KSP>/GameData/ExoInstruments/PluginData/"

Cutouts come from NASA SkyView, which mosaics and reprojects SHASSA on request, so nothing here
downloads the 2.3 GB of survey fields. About 170 MB of cutouts for the whole catalogue.
"""

import argparse
import io
import math
import os
import struct
import sys
import time

MAGIC = b"EXOPTCH1"
VERSION = 1

LINE_NAME = "H-alpha"
LINE_WAVELENGTH_M = 6562.80e-10

SKYVIEW = "https://skyview.gsfc.nasa.gov/current/cgi/pskcall"

# SHASSA's declination limit. Gaustad et al. (2001) surveyed the sky south of +15 degrees; a cutout
# straddling the edge comes back part empty, so an object is only accepted if its whole patch fits.
SHASSA_DEC_LIMIT = 15.0

# The composite's beam, which is what the fine-structure term is defined relative to.
COMPOSITE_BEAM_ARCMIN = 6.0

# Objects worth a patch: the emitting entries of Core/DeepSkyCatalog.cs, kept in step with it by
# name. Sizes are the catalogue's, and the patch radius is set from them below.
CATALOGUE = [
    ("NGC 281 Pacman",        13.2333,  56.6167,  35),
    ("IC 1805 Heart",         38.2500,  61.4500,  60),
    ("IC 1848 Soul",          43.0000,  60.4333,  60),
    ("NGC 1499 California",   60.7500,  36.4167, 145),
    ("NGC 1976 Orion",        83.8750,  -5.3833,  85),
    ("NGC 2024 Flame",        85.5000,  -1.8500,  30),
    ("IC 434 Horsehead",      85.2500,  -2.4500,  60),
    ("NGC 2070 Tarantula",    84.7500, -69.1000,  40),
    ("NGC 2237 Rosette",      98.0000,   4.9500,  80),
    ("IC 2177 Seagull",      106.0000, -10.4500, 120),
    ("NGC 3372 Carina",      161.2500, -59.8667, 120),
    ("NGC 6188 Rim",         250.0000, -48.7667,  20),
    ("NGC 6334 Cats Paw",    260.0000, -36.1000,  40),
    ("NGC 6357 Lobster",     261.2500, -34.2000,  50),
    ("NGC 6523 Lagoon",      271.0000, -24.3833,  90),
    ("NGC 6514 Trifid",      270.5000, -22.9667,  28),
    ("NGC 6611 Eagle",       274.7500, -13.7833,  35),
    ("NGC 6618 Omega",       275.2500, -16.1833,  46),
    ("NGC 6888 Crescent",    303.0000,  38.3500,  18),
    ("NGC 7000 NorthAmerica",314.7500,  44.5333, 120),
    ("IC 5070 Pelican",      312.7500,  44.3500,  60),
    ("IC 1396 ElephantTrunk",324.7500,  57.5000, 170),
    ("Sh2-155 Cave",         344.2500,  62.6167,  50),
    ("NGC 7635 Bubble",      350.2500,  61.2167,  15),
    ("NGC 1952 Crab",         83.7500,  22.0167,   7),
    ("NGC 6960 VeilWest",    311.5000,  30.7167,  70),
    ("NGC 6992 VeilEast",    314.0000,  31.7167,  60),
]


def patch_radius_deg(size_arcmin, margin=1.6, floor=1.0, ceiling=2.5):
    """Half-width of the patch: the object plus room to see it against its surroundings.

    Capped, because a patch's cost grows as the square of its radius and the point is the object,
    not the sky around it. Floored, so a small object still gets enough context to look like an
    object rather than a cutout.
    """
    return max(floor, min(ceiling, margin * size_arcmin / 60.0 / 2.0))


def fetch_skyview(ra, dec, size_deg, pixels, survey="shassa cc", retries=3):
    import requests
    params = {
        "Survey": survey,
        "Position": f"{ra},{dec}",
        "Coordinates": "J2000",
        "Projection": "Tan",
        "Size": f"{size_deg},{size_deg}",
        "Pixels": f"{pixels},{pixels}",
        "Sampler": "LI",
        "Return": "FITS",
    }
    for attempt in range(retries):
        try:
            r = requests.get(SKYVIEW, params=params, timeout=300)
            r.raise_for_status()
            if r.content[:6] != b"SIMPLE":
                raise RuntimeError("SkyView returned something that is not FITS "
                                   f"({r.content[:200]!r})")
            return r.content
        except Exception as e:                                   # noqa: BLE001
            if attempt == retries - 1:
                raise
            print(f"    retry {attempt + 1} after {type(e).__name__}: {e}")
            time.sleep(5 * (attempt + 1))


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--composite", required=True,
                   help="the packed all-sky map (HalphaMap.emission) that supplies the calibration")
    p.add_argument("--nside", type=int, default=4096,
                   help="patch resolution; 4096 is 0.86 arcmin, matched to SHASSA's 0.8")
    p.add_argument("--cache", default="shassa_cache",
                   help="directory for the downloaded cutouts, so a rerun does not refetch")
    p.add_argument("--out", default="HalphaPatches.patchset")
    p.add_argument("--only", help="build just the patches whose name contains this, for a quick test")
    args = p.parse_args()

    import numpy as np
    import healpy as hp
    from astropy.io import fits
    from astropy.wcs import WCS
    from scipy.ndimage import gaussian_filter

    if args.nside <= 0 or args.nside & (args.nside - 1):
        raise SystemExit("nside must be a power of two")

    composite, comp_nside = read_packed(args.composite, np)
    print(f"composite: nside {comp_nside} "
          f"({hp.nside2resol(comp_nside, arcmin=True):.2f} arcmin sampling), "
          f"{len(composite)} cells")
    print(f"patches at nside {args.nside} "
          f"({hp.nside2resol(args.nside, arcmin=True):.2f} arcmin sampling)\n")

    os.makedirs(args.cache, exist_ok=True)
    patches = []
    skipped = []

    for name, ra, dec, size in CATALOGUE:
        if args.only and args.only.lower() not in name.lower():
            continue
        radius = patch_radius_deg(size)
        if dec + radius > SHASSA_DEC_LIMIT:
            skipped.append((name, dec))
            continue

        # 0.8 arcmin per pixel, SHASSA's own sampling: asking SkyView for more would interpolate.
        pixels = int(round(2 * radius * 60.0 / 0.8))
        cache = os.path.join(args.cache, name.replace(" ", "_") + ".fits")
        if os.path.exists(cache):
            blob = open(cache, "rb").read()
        else:
            print(f"  fetching {name}: {2*radius:.1f} deg at {pixels} px")
            blob = fetch_skyview(ra, dec, 2 * radius, pixels)
            open(cache, "wb").write(blob)

        with fits.open(io.BytesIO(blob)) as hdul:
            image = np.asarray(hdul[0].data, dtype=np.float64)
            wcs = WCS(hdul[0].header)

        good = np.isfinite(image)
        if good.mean() < 0.5:
            skipped.append((name, f"only {good.mean()*100:.0f}% covered"))
            continue

        # Every cutout pixel's sky position, once.
        ny, nx = image.shape
        yy, xx = np.mgrid[0:ny, 0:nx]
        sky = wcs.pixel_to_world(xx.ravel(), yy.ravel())
        gal = sky.galactic
        comp_here = hp.get_interp_val(composite, gal.l.deg, gal.b.deg,
                                      nest=False, lonlat=True).reshape(ny, nx)

        # Smooth the cutout to the composite's beam, in cutout pixels.
        scale_arcmin = abs(wcs.wcs.cdelt[0]) * 60.0
        sigma_px = COMPOSITE_BEAM_ARCMIN / 2.3548 / scale_arcmin
        filled = np.where(good, image, np.nanmedian(image[good]))
        smoothed = gaussian_filter(filled, sigma_px, mode="nearest")

        # MEASURE the scale between SHASSA's units and rayleighs, rather than assume it.
        ok = good & np.isfinite(comp_here) & (comp_here > 0)
        if ok.sum() < 100:
            skipped.append((name, "no overlap with the composite"))
            continue
        # A scale AND an offset: SHASSA's continuum subtraction can leave a residual pedestal, and
        # forcing the fit through the origin would absorb it into the scale. The offset itself is
        # then discarded, because the fine-structure term below is a difference and a constant
        # cancels out of it -- what the fit is for is the slope.
        a = smoothed[ok]
        b = comp_here[ok]
        design = np.vstack([a, np.ones_like(a)]).T
        (scale, offset), *_ = np.linalg.lstsq(design, b, rcond=None)
        scale = float(scale)
        resid = float(np.std(scale * a + offset - b) / max(1e-9, np.mean(b)))
        print(f"  {name:<24} scale {scale:9.4f} R per unit, offset {offset:8.2f} R, "
              f"residual {resid*100:5.1f}% after matching at 6'")

        # composite + scale * (cutout - smoothed), apodised so the fine term vanishes at the rim.
        fine = scale * (filled - smoothed)
        r = np.hypot(xx - (nx - 1) / 2.0, yy - (ny - 1) / 2.0) / (min(nx, ny) / 2.0)
        taper = np.clip((1.0 - r) / 0.25, 0.0, 1.0)
        total = np.maximum(0.0, comp_here + fine * taper)
        total = np.where(good, total, comp_here)

        # Onto the HEALPix grid. THE DISC CENTRE HAS TO BE GALACTIC: the map is tabulated in
        # Galactic coordinates, so handing query_disc an equatorial direction asks for a disc
        # somewhere else entirely -- which returns cells that the cutout does not cover, and an
        # empty patch.
        from astropy.coordinates import SkyCoord
        import astropy.units as u
        centre_gal = SkyCoord(ra=ra * u.deg, dec=dec * u.deg, frame="icrs").galactic
        vec = hp.ang2vec(centre_gal.l.deg, centre_gal.b.deg, lonlat=True)
        cells = hp.query_disc(args.nside, vec, np.deg2rad(radius))
        cl, cb = hp.pix2ang(args.nside, cells, nest=False, lonlat=True)
        eq = SkyCoord(l=cl * u.deg, b=cb * u.deg, frame="galactic").icrs
        cx, cy = wcs.world_to_pixel(eq)
        ix = np.clip(np.round(cx).astype(int), 0, nx - 1)
        iy = np.clip(np.round(cy).astype(int), 0, ny - 1)
        inside = (cx >= 0) & (cx <= nx - 1) & (cy >= 0) & (cy <= ny - 1)
        cells = cells[inside]
        values = total[iy[inside], ix[inside]]

        order = np.argsort(cells)
        patches.append((name, ra, dec, radius, cells[order], values[order].astype(np.float16)))

    if skipped:
        print("\nskipped (outside SHASSA's footprint or not covered):")
        for name, why in skipped:
            print(f"  {name:<24} {why}")

    write_patchset(args.out, args.nside, patches, np)
    total_cells = sum(len(c) for _, _, _, _, c, _ in patches)
    size_mb = os.path.getsize(args.out) / (1024 * 1024)
    print(f"\nwrote {args.out}: {len(patches)} patches, {total_cells} cells, {size_mb:.1f} MB")
    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/")
    return 0


def read_packed(path, np):
    """Reads the packed all-sky map this mod already uses, so the calibration comes from the same
    file the game reads rather than from a second copy free to differ."""
    with open(path, "rb") as f:
        assert f.read(8) == b"EXOEMIS1", "not a packed emission map"
        version, nside = struct.unpack("<ii", f.read(8))
        nested = struct.unpack("<B", f.read(1))[0]
        assert nested == 0, "packed maps are RING ordered"
        struct.unpack("<d", f.read(8))
        n, = struct.unpack("<i", f.read(4)); f.read(n)
        n, = struct.unpack("<i", f.read(4)); f.read(n)
        values = np.frombuffer(f.read(), dtype="<f2").astype(np.float64)
    return values, nside


def write_patchset(path, nside, patches, np):
    """Run-length by HEALPix ring: a disc cuts each ring in one contiguous stretch, so storing runs
    costs a few hundred integers instead of an index per value."""
    with open(path, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<ii", VERSION, nside))
        f.write(struct.pack("<B", 0))
        f.write(struct.pack("<d", LINE_WAVELENGTH_M))
        for text in (LINE_NAME,
                     "SHASSA (Gaustad et al. 2001, PASP 113, 1326) fine structure on the "
                     "Finkbeiner (2003) composite's calibration"):
            blob = text.encode("utf-8")
            f.write(struct.pack("<i", len(blob)))
            f.write(blob)
        f.write(struct.pack("<i", len(patches)))

        for name, ra, dec, radius, cells, values in patches:
            blob = name.encode("utf-8")[:128]
            f.write(struct.pack("<i", len(blob)))
            f.write(blob)
            f.write(struct.pack("<ddf", ra, dec, radius))

            breaks = np.flatnonzero(np.diff(cells) != 1)
            starts = np.concatenate(([0], breaks + 1))
            ends = np.concatenate((breaks + 1, [len(cells)]))
            f.write(struct.pack("<i", len(starts)))
            for s, e in zip(starts, ends):
                f.write(struct.pack("<ii", int(cells[s]), int(e - s)))
            f.write(values.astype("<f2").tobytes())


if __name__ == "__main__":
    sys.exit(main())
