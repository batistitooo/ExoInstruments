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

MAGIC = b"EXOPTCH3"
VERSION = 3

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

# Residual past which the regression is judged ill-conditioned and the calibration falls back to
# conserving the composite's own mean. Set above the residuals SHASSA's own patches reach against
# the same reference (6.9% Lagoon, 11.3% Rosette) with margin, so the fallback only fires where the
# fit has genuinely failed rather than where the data is merely noisy.
MaxRegressionResidual = 0.25

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


HIPS2FITS = "https://alasky.cds.unistra.fr/hips-image-services/hips2fits"

# The northern half of the sky, which SHASSA does not reach. Continuum-subtracted H-alpha, stated
# linear and calibrated to rayleighs against WHAM, at 6.44 arcsec against SHASSA's 47.
#
# ITS ABSOLUTE SCALE IS NOT TAKEN ON TRUST EITHER, and does not need to be: the calibration below
# regresses every cutout against the published composite at the composite's own beam, so a source
# only has to be LINEAR and correctly SHAPED. Measured against VTSS (Dennison, Simonetti & Topasna
# 1998) over the fields both cover, NSNS correlates at r = 0.95 to 0.998 while its absolute scale
# runs 1.1 to 1.6 times VTSS's: the shape is right, the scale is what the regression supplies.
# tools/emission-surveys/check_calibration.py is that measurement.
#
# It has no refereed publication, unlike SHASSA, Finkbeiner and VTSS; section 12 records that, and
# the CC-BY-NC-SA licence is why nothing is redistributed and setup_data.py fetches it per player.
NSNS_HALPHA = "simg.de/P/NSNS/DR0_2/halpha"
NSNS_PIXEL_ARCMIN = 6.44 / 60.0

# THE OTHER TWO LINES, AND WHY THEY GET H-ALPHA'S SCALE FACTOR RATHER THAN ONE OF THEIR OWN.
#
# NSNS carries [O III] 5007 and [S II] 6716/31 over the same footprint, from the same instrument,
# reduced through the same pipeline and calibrated against the same WHAM reference. There is no
# published all-sky [O III] or [S II] composite to flux-match them against, the way H-alpha is
# matched against Finkbeiner. Fitting each band independently against SOMETHING ELSE would be
# worse than not fitting it: it would break the one thing the survey does measure, the RATIO
# between its own bands at one position.
#
# So the H-alpha flux-conservation factor is applied unchanged to all three planes. What that
# preserves is exactly right: the absolute scale comes from the published composite through
# H-alpha, and the line ratios come from NSNS's own three measurements. Nothing is invented.
#
# ALL THREE BANDS ARE ON ONE STATED SCALE, which is why applying H-alpha's fitted factor to the
# other two is sound rather than a guess. The DR0.2 registry records say so: H-alpha is
# "background-corrected and intensity-calibrated to Rayleighs using WHAM data", [O III] and [S II]
# are "linear intensity and full dynamic range in Rayleighs". The fitted factor is the
# NSNS-to-Finkbeiner zero-point transfer, and since numerator and denominator both take it, the
# survey's own line ratio is preserved exactly. Section 12 records what remains unverified: that
# ratio has not been checked against flux-calibrated imaging photometry of any object.
# (display name, wavelength, HiPS id, cache tag). The cache tag is explicit rather than derived
# from the name: deriving it stripped both bracketed names down to the same string, so [O III] and
# [S II] shared one cache file and the second band silently read the first.
NSNS_LINES = [
    ("H-alpha",      6562.80e-10, "simg.de/P/NSNS/DR0_2/halpha", ""),
    ("[O III] 5007", 5006.84e-10, "simg.de/P/NSNS/DR0_2/oiii",   "_oiii"),
    ("[S II] 6716",  6716.44e-10, "simg.de/P/NSNS/DR0_2/sii",    "_sii"),
]


def fetch_hips(hips, ra, dec, size_deg, pixels, retries=3):
    """One cutout from a HiPS, on the same tangent plane SkyView returns."""
    import requests
    params = {"hips": hips, "width": pixels, "height": pixels, "fov": size_deg,
              "projection": "TAN", "coordsys": "icrs", "ra": ra, "dec": dec,
              "rotation_angle": 0.0, "format": "fits"}
    for attempt in range(retries):
        try:
            r = requests.get(HIPS2FITS, params=params, timeout=900)
            r.raise_for_status()
            if r.content[:6] != b"SIMPLE":
                raise RuntimeError(f"hips2fits returned something that is not FITS "
                                   f"({r.content[:200]!r})")
            return r.content
        except Exception as e:                                   # noqa: BLE001
            if attempt == retries - 1:
                raise
            print(f"    retry {attempt + 1} after {type(e).__name__}: {e}")
            time.sleep(5 * (attempt + 1))


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
    p.add_argument("--nsns-nside", type=int, default=8192,
                   help="resolution for the northern NSNS patches; 8192 is 25.8 arcsec, four times "
                        "finer than the southern grid and still four times coarser than NSNS itself")
    p.add_argument("--cache", default="shassa_cache",
                   help="directory for the downloaded cutouts, so a rerun does not refetch")
    p.add_argument("--out", default="HalphaPatches.patchset")
    p.add_argument("--only", help="build just the patches whose name contains this, for a quick test")
    p.add_argument("--max-pixels", type=int, default=3000,
                   help="cap per cutout side; NSNS at 6.44 arcsec would otherwise ask for tens of "
                        "thousands across a wide patch")
    args = p.parse_args()

    import numpy as np
    import astropy.units as u
    from astropy.io import fits
    from astropy.wcs import WCS
    from astropy.stats import sigma_clipped_stats
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
    print(f"southern patches (SHASSA, 47 arcsec beam) at nside {args.nside} "
          f"({resol_arcmin(args.nside) * 60:.1f} arcsec sampling)")
    print(f"northern patches (NSNS, 6.44 arcsec) at nside {args.nsns_nside} "
          f"({resol_arcmin(args.nsns_nside) * 60:.1f} arcsec sampling)\n")

    os.makedirs(args.cache, exist_ok=True)
    patches = []
    skipped = []

    for name, ra, dec, size in CATALOGUE:
        if args.only and args.only.lower() not in name.lower():
            continue
        radius = patch_radius_deg(size)

        # WHICHEVER SURVEY REACHES IT. SHASSA is southern and stopped at +15, which left thirteen
        # of the twenty-seven catalogued nebulae, and every famous northern one among them (the
        # Veil, the North America, the Heart and Soul, the Crab), rendering from the composite's
        # 6 arcmin beam alone: broad blobs where the object is filaments.
        northern = dec + radius > SHASSA_DEC_LIMIT
        source = NSNS_HALPHA if northern else "shassa cc"
        native_arcmin = NSNS_PIXEL_ARCMIN if northern else 0.8

        # RESOLUTION PER PATCH, SET BY THE SURVEY THAT SUPPLIED IT. nside 4096 is 0.86 arcmin,
        # which matches SHASSA's 47 arcsec beam: storing a southern patch any finer would resample
        # the same information into four times the bytes. NSNS is 6.44 arcsec, so 4096 threw away a
        # factor of eight -- and a 0.86 arcmin cell is seven pixels across on a RedCat 51, which is
        # why the northern nebulae rendered as soft lumps. Northern patches go to args.nsns_nside.
        patch_nside = args.nsns_nside if northern else args.nside
        pixels = int(round(2 * radius * 60.0 / native_arcmin))
        pixels = max(64, min(args.max_pixels, pixels))

        # THE BANDS THIS PATCH WILL CARRY. A northern patch gets all three NSNS lines; a southern
        # one gets SHASSA, which is H-alpha only. Both are fetched on the same tangent plane and
        # the same grid, so the planes are pixel-aligned by construction and the ratio between
        # them at a point is the survey's own.
        bands = NSNS_LINES if northern else [(LINE_NAME, LINE_WAVELENGTH_M, "shassa cc", "")]
        blobs = []
        for line_name, _, hips, tag in bands:
            cache = os.path.join(args.cache, name.replace(" ", "_") + tag + ".fits")
            if os.path.exists(cache):
                blobs.append(open(cache, "rb").read())
                continue
            print(f"  fetching {name} {line_name}: {2*radius:.1f} deg at {pixels} px from "
                  + ("NSNS" if northern else "SHASSA"))
            blob = (fetch_hips(hips, ra, dec, 2 * radius, pixels) if northern
                    else fetch_skyview(ra, dec, 2 * radius, pixels))
            open(cache, "wb").write(blob)
            blobs.append(blob)

        with fits.open(io.BytesIO(blobs[0])) as hdul:
            image = np.asarray(hdul[0].data, dtype=np.float64)
            wcs = WCS(hdul[0].header)

        # The other lines, on the same grid. A band that fails to arrive drops out rather than
        # taking the patch down with it: one measured line is better than none.
        extra = []
        for i in range(1, len(bands)):
            try:
                with fits.open(io.BytesIO(blobs[i])) as hdul:
                    plane = np.asarray(hdul[0].data, dtype=np.float64)
                if plane.shape != image.shape:
                    print(f"    {bands[i][0]}: grid {plane.shape} != {image.shape}, dropped")
                    continue
                extra.append((bands[i][0], bands[i][1], plane))
            except Exception as exc:                             # noqa: BLE001
                print(f"    {bands[i][0]}: not retrieved ({exc})")

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
        method = "regression"

        # A REGRESSION NEEDS THE REFERENCE TO HAVE STRUCTURE TO FIT AGAINST, and over a filamentary
        # object the composite at 6 arcmin has almost none: the Veil's east patch fitted at a 54%
        # residual and a scale five times its neighbour's, on a reference that is nearly flat there.
        # A slope measured on a featureless reference is not a measurement.
        #
        # Where that happens the calibration falls back to CONSERVING THE PUBLISHED FLUX: scale the
        # candidate so its mean over the patch, at the composite's own beam, equals the composite's
        # mean over the same area. That is not a fit with fewer parameters, it is a conservation
        # constraint, exact by construction and well conditioned however smooth the reference is.
        # The structure then comes entirely from the candidate, which is what it was chosen for,
        # and the total surface brightness stays the published one.
        # NSNS ALWAYS takes this path, not only when the residual is bad. Its slope against the
        # composite scatters by 40% field to field (tools/emission-surveys measured 0.89 to 1.98
        # over twelve fields), so two adjacent patches of one object fitted independently come out
        # at different brightnesses and the seam between them shows. Flux conservation gives both
        # the same rule and the published total; its own scale then lands near 1.0 R per unit,
        # which is NSNS's stated rayleigh calibration recovered rather than assumed.
        if northern or resid > MaxRegressionResidual:
            mean_ref = float(np.mean(b[keep]))
            mean_cand = float(np.mean(a[keep]))
            if mean_cand > 1e-9:
                scale = mean_ref / mean_cand
                offset = 0.0
                # The residual against a featureless reference is NOT the diagnostic here: the
                # structure is deliberately the candidate's. What has to hold is flux conservation,
                # which is exact, so the ratio of means is reported instead.
                resid = abs(float(np.mean((scale * a)[keep]) / max(1e-9, mean_ref)) - 1.0)
                method = "flux-matched"

        print(f"  {name:<24} scale {scale:9.4f} R per unit, offset {offset:8.2f} R, "
              f"residual {resid*100:5.1f}% at 6' over {keep.mean()*100:.0f}% of the area kept "
              f"({method})")

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
        cells = HEALPix(nside=patch_nside, order="ring").cone_search_lonlat(
            centre_gal.l, centre_gal.b, radius * u.deg)
        cl, cb = healpix_to_lonlat(cells, patch_nside, order="ring")

        # cone_search_lonlat returns every cell the disc TOUCHES; healpy's query_disc, as it was
        # called here, returned only those whose centre falls inside it. Keeping the centre test
        # explicit means the patch covers exactly the radius it records, rather than a ring of
        # half-covered cells wider than the crossfade taper expects.
        keep = SkyCoord(l=centre_gal.l, b=centre_gal.b, frame="galactic").separation(
            SkyCoord(l=cl, b=cb, frame="galactic")) <= radius * u.deg
        cells, cl, cb = cells[keep], cl[keep], cb[keep]

        eq = SkyCoord(l=cl, b=cb, frame="galactic").icrs
        cx, cy = wcs.world_to_pixel(eq)
        inside = (cx >= 0) & (cx <= nx - 1) & (cy >= 0) & (cy <= ny - 1)
        cells = cells[inside]
        ix = np.clip(np.round(cx[inside]).astype(int), 0, nx - 1)
        iy = np.clip(np.round(cy[inside]).astype(int), 0, ny - 1)

        # THE CELL'S VALUE IS THE MEAN OVER THE CUTOUT PIXELS INSIDE IT, not the one pixel nearest
        # its centre. A cell is 25.8 arcsec and an NSNS pixel is 6.44, so a point sample kept one
        # pixel in sixteen and discarded the rest: that is aliasing, not downsampling, and it lets
        # the survey's pixel noise through at full amplitude while dropping the structure that
        # should have averaged it down. Surface brightness on a coarser grid MEANS the average over
        # the cell, so that is what gets stored.
        order_cells = np.argsort(cells)
        sorted_cells = cells[order_cells]
        py, pxx = np.mgrid[0:ny, 0:nx]
        pix_sky = wcs.pixel_to_world(pxx.ravel(), py.ravel()).galactic
        owner = HEALPix(nside=patch_nside, order="ring").lonlat_to_healpix(pix_sky.l, pix_sky.b)
        slot = np.searchsorted(sorted_cells, owner)
        np.clip(slot, 0, len(sorted_cells) - 1, out=slot)
        owned = sorted_cells[slot] == owner
        ncell = len(sorted_cells)

        def cell_mean(field, fallback_ix, fallback_iy):
            """Mean of the cutout over each cell; the nearest pixel where a cell caught none."""
            flat = field.ravel()
            ok = owned & np.isfinite(flat)
            total_per = np.bincount(slot[ok], weights=flat[ok], minlength=ncell)
            count_per = np.bincount(slot[ok], minlength=ncell)
            out = np.where(count_per > 0, total_per / np.maximum(count_per, 1), np.nan)
            empty = count_per == 0
            if np.any(empty):
                out[empty] = field[fallback_iy[order_cells][empty], fallback_ix[order_cells][empty]]
            return out

        values = cell_mean(total, ix, iy)
        cells = sorted_cells
        order = np.arange(ncell)                              # cell_mean already returns cell order

        # THE OTHER LINES THROUGH THE SAME GEOMETRY. Each extra plane shares H-alpha's
        # multiplicative scale (see NSNS_LINES) but carries its own zero, and is crossfaded to ZERO
        # rather than to the composite -- there is no published [O III] or [S II] all-sky map to fall back on, and
        # fading to H-alpha's composite would put hydrogen light in an oxygen frame. Outside the
        # patch the reader finds no plane for the line and the renderer falls back on the derived
        # ratio, exactly as it does today.
        planes = [(LINE_NAME, LINE_WAVELENGTH_M, values[order].astype(np.float16))]
        for line_name, line_wl, plane in extra:
            # EACH PLANE ON ITS OWN ZERO, not H-alpha's. The fitted offset is the pedestal that
            # brings NSNS's H-alpha onto the composite's zero point, and it is a property of the
            # H-alpha map. Only H-alpha is described as background-corrected; [O III] and [S II] sit
            # on a residual level of their own, which runs from -0.5 R over the Veil to +17 R over
            # the Crescent. Adding H-alpha's pedestal to them subtracted the wrong number and is what
            # drove 57% of the plane below zero.
            #
            # The plane's own sigma-clipped median is its sky. A constant across a 2.6 degree cutout
            # is not structure the patch can resolve either way, and leaving it in would wash the
            # whole field in a flat glow that is far more likely a continuum-subtraction residual
            # than diffuse oxygen. The MULTIPLICATIVE scale stays H-alpha's, which preserves the
            # survey's line ratio exactly; see NSNS_LINES.
            _, sky_level, _ = sigma_clipped_stats(plane[np.isfinite(plane)], sigma=3.0, maxiters=5)
            cal = scale * (plane - sky_level)

            # NO CLAMP. Half of a background-subtracted map is negative through noise alone, and
            # flooring it at zero rectifies that noise into a positive bias, replaces the field with
            # a wall of identical zeros, and -- because the reader treated a zero as "not measured"
            # -- collapsed the C1 reconstruction onto its C0 fallback, ruling a visible lattice
            # across every [O III] frame. A negative here means "consistent with no emission"; it is
            # carried honestly and the deposit declines to turn it into photons.
            cal = np.where(np.isfinite(cal), cal, np.nan)
            faded = taper * cal
            v = cell_mean(faded, ix, iy)
            below = int(np.count_nonzero(v < 0.0))
            print(f"    {line_name}: sky {sky_level:+.3f} raw units, "
                  f"{below / max(1, len(v)) * 100:.0f}% of cells below it (noise, kept signed)")
            planes.append((line_name, line_wl, v[order].astype(np.float16)))

        if len(planes) > 1:
            names = ", ".join(n for n, _, _ in planes[1:])
            print(f"    carries {names} on H-alpha's calibration")

        patches.append((name, ra, dec, radius, patch_nside, cells[order], planes))

    if skipped:
        print("\nskipped (outside SHASSA's footprint or not covered):")
        for name, why in skipped:
            print(f"  {name:<24} {why}")

    write_patchset(args.out, args.nside, patches, np)
    total_cells = sum(len(c) * len(p) for _, _, _, _, _, c, p in patches)
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
    costs a few hundred integers instead of an index per value.

    Version 2 carries N PLANES PER PATCH rather than one. The runs are shared: every plane of a
    patch is sampled on the same cells in the same order, so the geometry is written once and the
    values follow plane by plane. A patch that only has H-alpha writes one plane, which is what
    every southern SHASSA patch does, so the format costs nothing where there is nothing to add.
    """
    with open(path, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<ii", VERSION, nside))   # the default, and what southern patches use
        f.write(struct.pack("<B", 0))
        f.write(struct.pack("<d", LINE_WAVELENGTH_M))
        for text in (LINE_NAME,
                     "SHASSA (Gaustad et al. 2001, PASP 113, 1326) south and NSNS DR0.2 "
                     "(S. Ziegenbalg, CC-BY-NC-SA) north, both on the Finkbeiner (2003) "
                     "composite's calibration; forbidden lines share H-alpha's scale"):
            blob = text.encode("utf-8")
            f.write(struct.pack("<i", len(blob)))
            f.write(blob)
        f.write(struct.pack("<i", len(patches)))

        for name, ra, dec, radius, patch_nside, cells, planes in patches:
            blob = name.encode("utf-8")[:128]
            f.write(struct.pack("<i", len(blob)))
            f.write(blob)
            f.write(struct.pack("<ddf", ra, dec, radius))
            f.write(struct.pack("<i", patch_nside))   # v3: resolution is the patch's, not the set's

            breaks = np.flatnonzero(np.diff(cells) != 1)
            starts = np.concatenate(([0], breaks + 1))
            ends = np.concatenate((breaks + 1, [len(cells)]))
            f.write(struct.pack("<i", len(starts)))
            for s, e in zip(starts, ends):
                f.write(struct.pack("<ii", int(cells[s]), int(e - s)))

            f.write(struct.pack("<i", len(planes)))
            for line_name, line_wl, values in planes:
                nb = line_name.encode("utf-8")[:64]
                f.write(struct.pack("<i", len(nb)))
                f.write(nb)
                f.write(struct.pack("<d", line_wl))
                f.write(values.astype("<f2").tobytes())


if __name__ == "__main__":
    sys.exit(main())
