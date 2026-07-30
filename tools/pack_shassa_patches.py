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
over the same area -- robustly, with sigma clipping, so a local artefact in either cannot drag the
fit -- which MEASURES the linear relation between them. The patch then stores a CROSSFADE:

    patch = taper * (scale * cutout + offset)  +  (1 - taper) * composite

pure calibrated SHASSA through the middle, the composite at the rim, blended across the outer
quarter of the radius so a patch joins the base map with no step.

WHY NOT AN ADDITIVE HIGH-PASS. The first version of this stored
composite + scale*(cutout - smoothed), the standard way to graft fine structure onto a calibrated
low-resolution map. It is only valid where the two datasets AGREE at the low resolution, and around
M42 -- the brightest H-alpha source in the sky -- they do not: the composite carries a saturation
artefact from the survey images it mosaics, a ridge 10 arcmin wide and 1.5 degrees long, that SHASSA
does not have. The high-pass term then went strongly negative beside the bright core, clipped at
zero, and put black lobes either side of it: 255 cells of the M42 patch were zeroed where the
composite reads 87 to 1671 rayleighs.

The crossfade cannot do that. Both terms are positive, neither is a difference, and the middle of
the patch is SHASSA's own image -- which also means the composite's artefact is simply absent there,
rather than preserved and then decorated with fine structure.

Run:
    cd tools
    python3 -m venv env && ./env/bin/pip install numpy scipy astropy astropy-healpix requests
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

# Fraction of the patch radius the crossfade back to the all-sky map is spread over. Wide, because
# the crossfade's job is to be invisible: a sharp handover shows as a ring wherever the two datasets
# differ at all, and they always differ a little.
CrossfadeFraction = 0.40

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


def bleed_fraction(image, np):
    """Fraction of the cutout occupied by a detector bleed streak.

    Survey images of the brightest H II regions carry charge trails: a bright core saturates a CCD
    column and spills along a ROW, leaving a narrow horizontal spike orders of magnitude above its
    immediate neighbours. It is real in the data and no processing removes it, so the only useful
    thing is to say which patches have one.

    Detected by contrast against the VERTICAL neighbourhood rather than by row statistics. A row
    percentile does not work: a bleed is one or two rows out of hundreds, and a row that already
    contains a bright nebula has a high percentile anyway -- which is why the first version of this
    scored M42, whose streak is plainly visible, as clean. Comparing each pixel against the median of
    the pixels a few rows above and below it in the same column isolates exactly the feature that is
    narrow in y and extended in x.
    """
    d = np.where(np.isfinite(image), image, np.nan)
    ny, nx = d.shape
    offsets = [-8, -7, -6, -5, 5, 6, 7, 8]
    stack = np.full((len(offsets), ny, nx), np.nan)
    for k, o in enumerate(offsets):
        lo, hi = max(0, o), min(ny, ny + o)
        stack[k, lo - min(0, o) if o < 0 else 0: (hi - o) if o > 0 else ny] = d[lo:hi]
    neighbour = np.nanmedian(stack, axis=0)
    with np.errstate(divide="ignore", invalid="ignore"):
        contrast = d / np.where(np.abs(neighbour) > 1e-9, neighbour, np.nan)
    hot = np.nan_to_num(contrast) > 3.0
    # A bleed row is hot across a large part of its width; a star is hot in one column.
    row_fraction = hot.mean(axis=1)
    return float(row_fraction.max()), float(hot[row_fraction > 0.10].sum() / d.size)


def patch_radius_deg(size_arcmin, margin=2.4, floor=1.3, ceiling=2.6):
    """Half-width of the patch: the object, plus room for the crossfade to land on agreement.

    The margin is not cosmetic. The patch hands back to the all-sky composite across its outer
    annulus, so that annulus has to sit where the two datasets AGREE -- otherwise the crossfade
    reintroduces whatever the composite gets wrong, as a gradient fading in toward the rim. Around
    M42 the composite's saturation artefact is a ridge 1.5 degrees long, so a 1.13 degree patch put
    its own rim inside the artefact and handed back 247 rayleighs of disagreement. Wider rims land
    outside it.

    Capped, because a patch's cost grows as the square of its radius; floored, so a small object
    still gets enough sky around it to read as an object rather than a cutout.
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
    import astropy.units as u
    from astropy.io import fits
    from astropy.wcs import WCS
    from astropy_healpix import nside_to_pixel_resolution
    from scipy.ndimage import gaussian_filter

    # astropy-healpix rather than healpy, for the reason given in pack_halpha_map.py: healpy has no
    # Windows wheel, and everything used here is indexing arithmetic that astropy-healpix does.
    def resol_arcmin(nside):
        return nside_to_pixel_resolution(nside).to_value(u.arcmin)

    if args.nside <= 0 or args.nside & (args.nside - 1):
        raise SystemExit("nside must be a power of two")

    composite, comp_nside = read_packed(args.composite, np)
    print(f"composite: nside {comp_nside} "
          f"({resol_arcmin(comp_nside):.2f} arcmin sampling), "
          f"{len(composite)} cells")
    print(f"patches at nside {args.nside} "
          f"({resol_arcmin(args.nside):.2f} arcmin sampling)\n")

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
        from astropy_healpix import interpolate_bilinear_lonlat
        comp_here = interpolate_bilinear_lonlat(gal.l, gal.b, composite,
                                                order="ring").reshape(ny, nx)

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
        # forcing the fit through the origin would absorb it into the scale. Both are kept, because
        # the patch now stores the calibrated image itself rather than a difference.
        #
        # ROBUSTLY, by sigma clipping. Around a bright object the two datasets can disagree over a
        # localised region -- the composite's M42 artefact is the case that forced this -- and a
        # plain least squares lets that region set the calibration for the whole patch.
        a = smoothed[ok]
        b = comp_here[ok]
        keep = np.ones(a.shape, dtype=bool)
        scale = offset = 0.0
        for _ in range(5):
            design = np.vstack([a[keep], np.ones(keep.sum())]).T
            (scale, offset), *_ = np.linalg.lstsq(design, b[keep], rcond=None)
            r = b - (scale * a + offset)
            sigma = 1.4826 * np.median(np.abs(r[keep] - np.median(r[keep])))
            if not (sigma > 0):
                break
            new_keep = np.abs(r - np.median(r[keep])) < 3.0 * sigma
            if new_keep.sum() < 100 or np.array_equal(new_keep, keep):
                break
            keep = new_keep
        scale = float(scale)
        offset = float(offset)
        resid = float(np.std((scale * a + offset - b)[keep]) / max(1e-9, np.mean(b[keep])))
        print(f"  {name:<24} scale {scale:9.4f} R per unit, offset {offset:8.2f} R, "
              f"residual {resid*100:5.1f}% at 6' over {keep.mean()*100:.0f}% of the area kept")

        # The crossfade. Calibrated SHASSA through the middle, the composite at the rim.
        calibrated = scale * filled + offset

        # A NON-POSITIVE VALUE IS NOT A MEASUREMENT OF ZERO EMISSION, and clamping it to zero -- as
        # this used to -- turns a survey artefact into sky brightness. SHASSA is continuum
        # subtracted: an off-band image is scaled and removed from the H-alpha one, and at a bright
        # star that subtraction over-corrects and drives the residual to zero or below (Gaustad et
        # al. 2001, PASP 113, 1326, Sect. 4). Clamped, what survives is a disc of exact zeros
        # centred on every bright star in the patch, which renders as a black hole in the middle of
        # a nebula -- discs 20 to 33 pixels across on a 120 s frame of the Horsehead.
        #
        # NaN instead, so the reader declines to answer there and falls through to the composite,
        # whose 6 arcmin beam is far too coarse to carry a stellar residual. Same rule the base
        # map's own packer already applies to negative pixels.
        clipped = int(np.count_nonzero(calibrated <= 0.0))
        calibrated = np.where(calibrated > 0.0, calibrated, np.nan)
        r = np.hypot(xx - (nx - 1) / 2.0, yy - (ny - 1) / 2.0) / (min(nx, ny) / 2.0)
        taper = np.clip((1.0 - r) / CrossfadeFraction, 0.0, 1.0)
        total = taper * calibrated + (1.0 - taper) * comp_here
        total = np.where(good, total, comp_here)

        # Both terms are positive, so the result must be. Reported anyway: a negative would mean the
        # fitted offset had overwhelmed the signal, which is worth knowing rather than hiding.
        if clipped:
            print(f"    {clipped} cutout pixels ({clipped / calibrated.size * 100:.2f}%) sat below "
                  f"the fitted offset and were floored at zero")
        worst_row, bleed_area = bleed_fraction(image, np)
        if worst_row > 0.15:
            print(f"    WARNING: a detector bleed streak crosses {worst_row * 100:.0f}% of one row "
                  f"({bleed_area * 100:.2f}% of the cutout). It is in the survey data and nothing "
                  f"here can remove it -- this object will render with a bright horizontal spike.")

        rim = r > 0.97
        if np.any(rim):
            # A rim pixel where the patch stores NaN is one SHASSA declined to answer for, and the
            # reader falls through to the composite there -- so the joint is exact by construction,
            # not unknown. Scoring those as zero disagreement is what makes this check mean
            # anything: taper is 0 at the rim and 0 * NaN is NaN, so a plain max over the rim came
            # back NaN for every patch and the check silently measured nothing.
            seam_map = np.abs((total - comp_here)[rim])
            fell_through = int(np.count_nonzero(~np.isfinite(seam_map)))
            seam = float(np.max(np.where(np.isfinite(seam_map), seam_map, 0.0)))
            rel = seam / max(1.0, float(np.median(comp_here[rim])))
            if fell_through:
                print(f"    {fell_through} of {rim.sum()} rim pixels carry no SHASSA value and "
                      f"fall through to the base map")
            print(f"    rim agreement with the base map: {seam:.1f} R worst "
                  f"({rel * 100:.1f}% of the composite there)")

        # Onto the HEALPix grid. THE DISC CENTRE HAS TO BE GALACTIC: the map is tabulated in
        # Galactic coordinates, so handing query_disc an equatorial direction asks for a disc
        # somewhere else entirely -- which returns cells that the cutout does not cover, and an
        # empty patch.
        from astropy.coordinates import SkyCoord
        from astropy_healpix import HEALPix, healpix_to_lonlat
        centre_gal = SkyCoord(ra=ra * u.deg, dec=dec * u.deg, frame="icrs").galactic
        cells = HEALPix(nside=args.nside, order="ring").cone_search_lonlat(
            centre_gal.l, centre_gal.b, radius * u.deg)
        cl, cb = healpix_to_lonlat(cells, args.nside, order="ring")

        # cone_search_lonlat returns every cell the disc TOUCHES; healpy's query_disc, as it was
        # called here, returned only those whose centre falls inside it. Keeping the centre test
        # explicit means the patch covers exactly the radius it records, rather than a ring of
        # half-covered cells wider than the crossfade taper expects.
        keep = SkyCoord(l=centre_gal.l, b=centre_gal.b, frame="galactic").separation(
            SkyCoord(l=cl, b=cb, frame="galactic")) <= radius * u.deg
        cells, cl, cb = cells[keep], cl[keep], cb[keep]

        eq = SkyCoord(l=cl, b=cb, frame="galactic").icrs
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
