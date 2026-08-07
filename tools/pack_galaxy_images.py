#!/usr/bin/env python3
"""Packs REAL survey imagery of the catalogued galaxies into the shape maps Core/GalaxyImageSet.cs reads.

WHY THIS EXISTS. A galaxy drawn from a Sersic profile is a smooth ellipse and nothing else: no
bulge standing above its disc, no arms, no dust lane, no star-forming knots. That is not a defect
of the code, it is what four catalogued numbers (total magnitude, D25, axis ratio, position angle)
can say. Every structure a photograph of a galaxy is recognised by is finer than those four numbers,
and there is no relation in the literature that puts it back, because the structure is not a
function of the type: M51's arms are M51's, not "an Sbc's".

So the structure comes from a measurement of that galaxy, the same way tools/pack_shassa_patches.py
answers the nebula-morphology limit with a finer survey instead of with a prettier model.

WHAT IS TAKEN FROM THE SURVEY, AND WHAT IS NOT. Only the SHAPE. Each map is normalised to unit
total flux, so the survey contributes the distribution of the light and nothing else; the absolute
calibration stays HyperLEDA's B_T and the global colour stays its B-V, exactly as they do for a
galaxy with no map. This is deliberate and it is the same rule the SHASSA patches follow (the
composite keeps the calibration, SHASSA contributes only structure). It also means the survey's own
zero point, its exposure time and its photometric system never enter the render, so a map from
Pan-STARRS and a map from the Legacy Surveys are interchangeable and neither can shift a galaxy's
brightness.

TWO BANDS, because morphology is wavelength dependent and the instrument is not monochromatic.
Dust lanes are darker in the blue, arms are bluer than the disc they sit in, a bulge is redder than
both. One map would put the g-band arms in an I-band frame. Two normalised maps let the renderer
interpolate the SHAPE to its own passband's effective wavelength while the total stays the
catalogue's, so a red filter really does see a redder galaxy.

SOURCES, in the order they are tried:
  * DESI Legacy Imaging Surveys DR10 (Dey et al. 2019, AJ 157, 168) -- deepest, sky-subtracted,
    calibrated in nanomaggies, ~20000 deg^2 including the far south. Retrieved through the CDS
    hips2fits service, which reprojects onto the exact tangent plane we store, at any size.
  * Pan-STARRS1 DR1 (Chambers et al. 2016, arXiv:1612.05560) -- the 3pi survey, everything north
    of declination -30, from the survey's OWN stack cutouts (ps1images.stsci.edu) resampled here.
    The stacks come in 0.4 degree skycells and a cutout is served from ONE of them, so every cell
    the box touches is fetched and merged; M51's box came back 65 per cent empty before that.
  * The Pan-STARRS g HiPS alone, for boxes past --ps1-native-max-arcmin. The stack service serves
    0.25" pixels and nothing coarser, so a half-degree box is a gigabyte per cell per band. One band
    means the renderer uses the same shape at every wavelength rather than inventing a colour
    structure it never measured.

AND WHY NOT THE PAN-STARRS HiPS, WHICH WOULD HAVE BEEN SIMPLER. Because its pixel values are not
all linear in flux, and nothing in the FITS header says so. tools/galaxy-images/check_transfer.py
measures the transfer curve of each service against the survey's own stack over the Sombrero, whose
own light spans four decades:

    Pan-STARRS HiPS g   39.7/7.07  201/173  1.04e3/1.01e3  2.62e4/2.63e4  1.49e5/1.42e5   linear
    Pan-STARRS HiPS r   40.8/0.119  207/0.777  1.06e3/2.16  2.66e4/5.55  1.51e5/7.18     asinh
    Legacy DR10 g       39.7/2.4e-4  201/0.028  1.04e3/0.133  2.62e4/2.28  1.49e5/11.7   linear
    Legacy DR10 r       41.2/8.9e-4  209/0.061  1.07e3/0.298  2.68e4/5.32  6.43e4/10.9   linear

The r and i Pan-STARRS HiPS compress five decades of flux into a factor of sixty, which is an asinh
transfer. Packed as a shape map it would have flattened every nucleus and lifted every outskirt,
and the result would still have looked like a galaxy. So those two are not used, and the check is
kept as a tool rather than as a note, because the next survey added here has to pass it too.

DSS2 is not used at all: a photographic plate is not linear in flux by construction, and no
correction restores what its characteristic curve compressed.

AND CLIPPED DATA IS REFUSED TOO, which is a different failure from a non-linear one and is not
caught by the same test. The Legacy DR10 r HiPS returns a flat plateau of exactly 10.0 over the
Sombrero's nucleus while the rest of the cutout runs to 19.3: eleven pixels, far too few to move any
global statistic, and enough to take the central five arcseconds from 5.1 per cent of the galaxy's
light down to 1.6. The test is that real floating-point sky data essentially never repeats a value
exactly, so a repeated value among the brightest percentile is a clip. A second, much looser check
compares the two bands on the light in the core, but it cannot be the one that catches this: a dusty
edge-on galaxy really does hide its core in the blue, and at a factor of 2.5 it rejected Centaurus A,
whose 2.65 is 1.06 mag of differential extinction across a real dust lane.

WHAT IS REMOVED FROM THE IMAGE, AND WHY EACH REMOVAL IS SAFE.
  * NOT foreground stars, by default. A star left in costs a second draw from the star catalogue
    and its own flux out of the normalised budget, both bounded and point-like; masking it costs
    an inpainted patch, and no inpainting can invent the galaxy behind a big mask. The patches
    read as discs drawn on the image, which is worse than every star. --max-stars N restores the
    old behaviour (Gaia DR3 astrometry, 3 sigma parallax or proper motion, so a galaxy's own
    H II regions are never touched).
  * Other CATALOGUED galaxies whose disc falls in the box, since each is drawn from its own entry.
  * A residual sky pedestal, measured well outside the D25 isophote.
Everything removed is inpainted from what surrounds it, never with zero, so a mask cannot open a
hole in the disc: a Gaussian-weighted average of the surviving neighbours, widened until the hole is
covered, plus the image's own noise so the patch does not read as a flat disc; the elliptical
azimuthal median answers only where nothing local survives. The flux fraction each removal accounts
for is measured and written into the file.

Every survey band here is MEASURED LINEAR before being trusted, by
galaxy-images/check_linearity.py: aperture photometry on the service's own cutouts against the
Gaia DR3 synthetic photometry catalogue, with a colour term. That is what excludes the Pan-STARRS
r/i HiPS (asinh, slope 0.42-0.51 where a linear image gives 1.0), SDSS u and DES i.

Run (the defaults are the shipped configuration; setup_data.py calls this with no extra flags):
    python3 -m venv env && ./env/bin/pip install numpy scipy astropy photutils requests
    ./env/bin/python pack_galaxy_images.py --catalog GalaxyCatalog.galcat --bmax 11 \
        --out GalaxyImages.galimg

Copy the result to <KSP>/GameData/ExoInstruments/PluginData/.
"""

import argparse
import io
import math
import os
import struct
import sys
import time

import warnings

import numpy as np

warnings.filterwarnings("ignore")

CATALOG_MAGIC = b"EXOGALX1"
MAGIC = b"EXOGIMG1"
VERSION = 1

HIPS2FITS = "https://alasky.cds.unistra.fr/hips-image-services/hips2fits"
VIZIER_TAP = "https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync"

# Effective wavelengths are the surveys' own published values: Dey et al. (2019) for the DECam
# filters of the Legacy Surveys, Tonry et al. (2012, ApJ 750, 99) Table 3 for the Pan-STARRS1
# system, and the plate/filter combinations for DSS2. They are what the renderer interpolates in,
# so a wrong one tilts the colour of every galaxy it draws.
PS1_FILENAMES = "https://ps1images.stsci.edu/cgi-bin/ps1filenames.py"
PS1_CUTOUT = "https://ps1images.stsci.edu/cgi-bin/fitscut.cgi"
PS1_PIXEL_ARCSEC = 0.25
PS1_MAX_PIXELS = 6000

SURVEYS = [
    {
        "id": "legacy-dr10",
        "name": "DESI Legacy Imaging Surveys DR10 (Dey et al. 2019)",
        "provider": "hips",
        "bands": [("CDS/P/DESI-Legacy-Surveys/DR10/g", 473.0, "DECam g"),
                  ("CDS/P/DESI-Legacy-Surveys/DR10/r", 642.0, "DECam r")],
        "max_fov_deg": 90.0,
    },
    {
        "id": "ps1-dr1",
        "name": "Pan-STARRS1 DR1 (Chambers et al. 2016)",
        "provider": "ps1",
        "bands": [("g", 481.0, "PS1 g"), ("r", 617.0, "PS1 r")],
        "max_fov_deg": PS1_MAX_PIXELS * PS1_PIXEL_ARCSEC / 3600.0,
    },
    {
        # Both measured linear by galaxy-images/check_linearity.py against the Gaia DR3 synthetic
        # photometry reference, with the colour term (slopes 1.010/1.004). The i band is excluded:
        # slope 0.930 on 25 stars establishes nothing. Southern: catches DECam holes below PS1's
        # Dec -30 limit, NGC1566 among them.
        "id": "des-dr2",
        "name": "Dark Energy Survey DR2 (Abbott et al. 2021)",
        "provider": "hips",
        "bands": [("CDS/P/DES-DR2/g", 473.0, "DES g"),
                  ("CDS/P/DES-DR2/r", 642.0, "DES r")],
        "max_fov_deg": 90.0,
    },
    {
        # Four measured-linear bands (same protocol; slopes 0.991-1.014, u fails and is excluded),
        # so the colour structure is sampled at four wavelengths instead of two. Northern.
        "id": "sdss9",
        "name": "SDSS DR9 (Ahn et al. 2012)",
        "provider": "hips",
        "bands": [("CDS/P/SDSS9/g", 477.0, "SDSS g"),
                  ("CDS/P/SDSS9/r", 623.1, "SDSS r"),
                  ("CDS/P/SDSS9/i", 762.5, "SDSS i"),
                  ("CDS/P/SDSS9/z", 913.4, "SDSS z")],
        "max_fov_deg": 90.0,
    },
    {
        # One band, and deliberately so. The stack service serves 0.25" pixels and nothing coarser,
        # so a half-degree box is a gigabyte of download per skycell per band; past the size set by
        # --ps1-native-max-arcmin the g-band HiPS answers instead. It is the ONE Pan-STARRS HiPS
        # measured linear (see the module docstring), and a single band means the renderer uses the
        # same shape at every wavelength rather than inventing a colour structure it never measured.
        "id": "ps1-dr1-g-hips",
        "name": "Pan-STARRS1 DR1 g (Chambers et al. 2016), single band",
        "provider": "hips",
        "bands": [("CDS/P/PanSTARRS/DR1/g", 481.0, "PS1 g")],
        "max_fov_deg": 90.0,
    },
]


# --------------------------------------------------------------------------------------------
# The packed galaxy catalogue, read back so the image set is keyed to exactly the catalogue in use
# --------------------------------------------------------------------------------------------

class Reader:
    def __init__(self, data):
        self.d = data
        self.i = 0

    def take(self, n):
        b = self.d[self.i:self.i + n]
        if len(b) != n:
            raise SystemExit("truncated catalogue")
        self.i += n
        return b

    def i32(self):
        return struct.unpack("<i", self.take(4))[0]

    def f32(self):
        return struct.unpack("<f", self.take(4))[0]

    def f64(self):
        return struct.unpack("<d", self.take(8))[0]

    def u8(self):
        return struct.unpack("<B", self.take(1))[0]

    def string(self):
        return self.take(self.i32()).decode("utf-8")


def read_catalog(path):
    r = Reader(open(path, "rb").read())
    if r.take(8) != CATALOG_MAGIC:
        raise SystemExit(path + " is not an ExoInstruments packed galaxy catalogue")
    if r.i32() != 1:
        raise SystemExit("unsupported galaxy catalogue version")
    count = r.i32()
    source = r.string()
    out = []
    for _ in range(count):
        g = {"name": r.string(), "ra": r.f64(), "dec": r.f64(), "bt": r.f32(), "bv": r.f32(),
             "d25": r.f32(), "axis": r.f32(), "pa": r.f32(), "t": r.f32(), "n": r.f32()}
        r.u8()
        out.append(g)
    return source, out


# --------------------------------------------------------------------------------------------
# Retrieval
# --------------------------------------------------------------------------------------------

def fetch_fits(session, hips, ra, dec, fov_deg, size_px, cache_dir, retries=3):
    """One band, reprojected onto our own tangent plane by the server. Cached on disk."""
    from astropy.io import fits

    key = "%s_%.5f_%+.5f_%.5f_%d.fits" % (hips.replace("/", "-"), ra, dec, fov_deg, size_px)
    path = os.path.join(cache_dir, key) if cache_dir else None
    if path and os.path.exists(path) and os.path.getsize(path) > 2880:
        return fits.open(path)[0]

    params = {
        "hips": hips, "width": size_px, "height": size_px, "fov": fov_deg,
        "projection": "TAN", "coordsys": "icrs", "ra": ra, "dec": dec,
        "rotation_angle": 0.0, "format": "fits",
    }
    last = None
    for attempt in range(retries):
        try:
            r = session.get(HIPS2FITS, params=params, timeout=600)
            if r.status_code != 200:
                last = "HTTP %d" % r.status_code
                time.sleep(2.0 * (attempt + 1))
                continue
            if path:
                with open(path, "wb") as f:
                    f.write(r.content)
            return fits.open(io.BytesIO(r.content))[0]
        except Exception as exc:                                    # noqa: BLE001
            last = str(exc)
            time.sleep(2.0 * (attempt + 1))
    print("      fetch failed (%s): %s" % (last, hips))
    return None


def target_wcs(ra, dec, fov_deg, size_px):
    """The tangent plane every map is stored on, built to match hips2fits exactly.

    Same convention as the service returns for the other provider: CRPIX at N/2, north up, east
    left, TAN. Written out here rather than copied from a returned header so the two paths cannot
    drift apart and put one survey's maps half a pixel off the other's.
    """
    from astropy.wcs import WCS
    w = WCS(naxis=2)
    w.wcs.crpix = [size_px / 2.0, size_px / 2.0]
    w.wcs.cdelt = [-fov_deg / size_px, fov_deg / size_px]
    w.wcs.crval = [ra, dec]
    w.wcs.ctype = ["RA---TAN", "DEC--TAN"]
    return w


def fetch_ps1(session, band, ra, dec, fov_deg, size_px, cache_dir, retries=3):
    """One band from Pan-STARRS' own stack cutouts, resampled onto our tangent plane.

    The survey's service is used rather than the HiPS because the HiPS is asinh-scaled in r and i
    (see the module docstring). The cost is that the resampling is ours: the stack arrives at
    0.25"/pixel on the skycell's own WCS, is block-averaged down to about our sampling so that
    shrinking it cannot alias, and is then interpolated onto our grid.
    """
    from astropy.io import fits
    from astropy.wcs import WCS
    from scipy.ndimage import map_coordinates

    native_px = int(math.ceil(fov_deg * 3600.0 / PS1_PIXEL_ARCSEC))
    if native_px > PS1_MAX_PIXELS:
        return None
    native_px = max(native_px, 32)

    # SKYCELLS, and why one cutout is not enough. Pan-STARRS is stacked into 0.4 degree skycells,
    # and a cutout is served from ONE of them: a box that crosses an edge comes back with the rest
    # of it blank. M51's own box came back 65 per cent empty that way, which looks exactly like a
    # galaxy the survey never observed. So every cell the box touches is fetched and they are
    # merged on our grid.
    target = target_wcs(ra, dec, fov_deg, size_px)
    yy, xx = np.mgrid[0:size_px, 0:size_px]
    sky_ra, sky_dec = target.all_pix2world(xx, yy, 0)

    half = fov_deg * 0.5
    cosdec = max(1e-6, math.cos(math.radians(dec)))
    probes = [(ra + dx * half / cosdec, dec + dy * half)
              for dy in (-0.85, 0.0, 0.85) for dx in (-0.85, 0.0, 0.85)]

    filenames = []
    for (pra, pdec) in probes:
        try:
            r = session.get(PS1_FILENAMES, params={"ra": pra, "dec": pdec, "filters": band},
                            timeout=300)
            r.raise_for_status()
        except Exception:                                           # noqa: BLE001
            continue
        for line in r.text.splitlines()[1:]:
            parts = line.split()
            if len(parts) > 7 and parts[7] not in filenames:
                filenames.append(parts[7])
    if not filenames:
        return None

    target_scale = fov_deg * 3600.0 / size_px
    accumulated = np.full((size_px, size_px), np.nan)

    for filename in filenames:
        cell = filename.rstrip("/").split("/")[-1]
        key = "ps1_%s_%.5f_%+.5f_%d_%s" % (band, ra, dec, native_px, cell)
        path = os.path.join(cache_dir, key) if cache_dir else None
        hdu = None
        if path and os.path.exists(path) and os.path.getsize(path) > 2880:
            try:
                hdu = fits.open(path)[0]
            except Exception:                                       # noqa: BLE001
                hdu = None
        if hdu is None:
            for attempt in range(retries):
                try:
                    c = session.get(PS1_CUTOUT,
                                    params={"ra": ra, "dec": dec, "size": native_px,
                                            "format": "fits", "red": filename}, timeout=900)
                    c.raise_for_status()
                    if path:
                        with open(path, "wb") as f:
                            f.write(c.content)
                    hdu = fits.open(io.BytesIO(c.content))[0]
                    break
                except Exception:                                   # noqa: BLE001
                    time.sleep(2.0 * (attempt + 1))
        if hdu is None or hdu.data is None:
            continue

        data = np.array(hdu.data, dtype=np.float64)
        source = WCS(hdu.header)

        # Block-average before shrinking. Sampling a 0.25" image straight onto a 1" grid would read
        # one native pixel in sixteen and alias every star and every knot.
        block = max(1, int(target_scale / PS1_PIXEL_ARCSEC))
        if block > 1:
            ny, nx = data.shape
            ny, nx = (ny // block) * block, (nx // block) * block
            if ny < block or nx < block:
                continue
            reduced = np.nanmean(
                data[:ny, :nx].reshape(ny // block, block, nx // block, block), axis=(1, 3))
        else:
            reduced = data

        sx, sy = source.all_world2pix(sky_ra, sky_dec, 0)
        # Block averaging moves the pixel grid: the centre of a block of `block` native pixels
        # starting at index 0 sits at (block-1)/2 in native coordinates.
        sx = (sx - (block - 1) * 0.5) / block
        sy = (sy - (block - 1) * 0.5) / block

        good = np.isfinite(reduced)
        out = map_coordinates(np.nan_to_num(reduced, nan=0.0), [sy, sx], order=1,
                              mode="constant", cval=0.0)
        # A pixel that drew on any missing native pixel is missing too, so coverage is never
        # invented by the interpolation.
        valid = map_coordinates(good.astype(np.float64), [sy, sx], order=1,
                                mode="constant", cval=0.0)
        out = np.where(valid > 0.999, out, np.nan)
        accumulated = np.where(np.isfinite(accumulated), accumulated, out)
        if np.isfinite(accumulated).all():
            break

    if not np.isfinite(accumulated).any():
        return None

    header = target.to_header()
    header["CDELT1"] = -fov_deg / size_px
    header["CDELT2"] = fov_deg / size_px
    return fits.PrimaryHDU(data=accumulated.astype(np.float32), header=header)


def fetch_gaia_foreground(session, ra, dec, radius_deg, mag_limit, max_stars, cache):
    """Gaia DR3 sources that are FOREGROUND STARS, by their own astrometry.

    A galaxy's brightest clusters and H II regions are in Gaia too, and removing them would remove
    the very structure this file exists to keep. The discriminator is parallax or proper motion
    significant at 3 sigma, which an extragalactic source does not have.
    """
    key = ("gaia_%.5f_%+.5f_%.5f_%.1f_%d" % (ra, dec, radius_deg, mag_limit, max_stars))
    if key in cache:
        return cache[key]

    # BOUNDED, brightest first. The box of a nearby giant is degrees across and the query behind it
    # is then millions of rows: the unbounded form simply never returned over the SMC. Taking the
    # brightest N removes the stars that would actually stand out of the galaxy and be drawn twice,
    # and leaves the faint tail in the map as an unresolved background, which is reported.
    adql = ("SELECT TOP %d RAJ2000, DEJ2000, Gmag, Plx, e_Plx, pmRA, e_pmRA, pmDE, e_pmDE "
            "FROM \"I/355/gaiadr3\" WHERE 1=CONTAINS(POINT('ICRS',RAJ2000,DEJ2000),"
            "CIRCLE('ICRS',%.6f,%.6f,%.6f)) AND Gmag < %.2f ORDER BY Gmag"
            % (max_stars, ra, dec, radius_deg, mag_limit))
    try:
        r = session.get(VIZIER_TAP, params={"request": "doQuery", "lang": "ADQL",
                                            "format": "csv", "query": adql}, timeout=600)
        r.raise_for_status()
    except Exception as exc:                                        # noqa: BLE001
        print("      Gaia query failed (%s); no stars masked" % exc)
        cache[key] = []
        return []

    stars = []
    lines = r.text.splitlines()
    for line in lines[1:]:
        parts = line.split(",")
        if len(parts) < 9:
            continue

        def num(i):
            try:
                return float(parts[i])
            except ValueError:
                return float("nan")

        sra, sdec, g = num(0), num(1), num(2)
        plx, eplx = num(3), num(4)
        pmra, epmra, pmde, epmde = num(5), num(6), num(7), num(8)
        if not np.isfinite(sra) or not np.isfinite(sdec):
            continue

        parallactic = np.isfinite(plx) and np.isfinite(eplx) and eplx > 0 and plx / eplx > 3.0
        moving = False
        if np.isfinite(pmra) and np.isfinite(pmde) and np.isfinite(epmra) and np.isfinite(epmde) \
                and epmra > 0 and epmde > 0:
            chi2 = (pmra / epmra) ** 2 + (pmde / epmde) ** 2
            moving = chi2 > 9.0
        if parallactic or moving:
            stars.append((sra, sdec, g if np.isfinite(g) else 21.0))

    cache[key] = stars
    return stars


# --------------------------------------------------------------------------------------------
# Processing
# --------------------------------------------------------------------------------------------

def elliptical_radius(shape, centre, axis_ratio, pa_deg):
    """Semi-major-axis radius, in pixels, of the isophote through each pixel.

    North is +y and east is -x in the stored maps (a TAN projection with no rotation), and the
    position angle is measured east of north, which is why the major axis unit vector is
    (-sin PA, cos PA) rather than (cos PA, sin PA).
    """
    ny, nx = shape
    y, x = np.mgrid[0:ny, 0:nx]
    dx = x - centre[0]
    dy = y - centre[1]
    pa = math.radians(pa_deg)
    ux, uy = -math.sin(pa), math.cos(pa)
    along = dx * ux + dy * uy
    across = -dx * uy + dy * ux
    q = max(1e-3, min(1.0, axis_ratio))
    return np.sqrt(along * along + (across / q) ** 2)


def azimuthal_median(image, radius, valid, nbins):
    """The galaxy's own median surface brightness as a function of elliptical radius.

    Used to fill everything that is removed. Filling with zero would open a hole in the disc, and
    filling with a global sky level would open one wherever the galaxy is brighter than the sky.
    """
    rmax = float(radius.max())
    edges = np.linspace(0.0, rmax, nbins + 1)
    idx = np.clip(np.digitize(radius, edges) - 1, 0, nbins - 1)
    profile = np.zeros(nbins)
    good = valid & np.isfinite(image)
    for b in range(nbins):
        sel = (idx == b) & good
        profile[b] = np.median(image[sel]) if np.count_nonzero(sel) >= 8 else np.nan
    # A bin with too few unmasked pixels inherits its neighbours rather than a guess.
    ok = np.isfinite(profile)
    if not ok.any():
        return np.zeros_like(image)
    centres = 0.5 * (edges[:-1] + edges[1:])
    profile = np.interp(centres, centres[ok], profile[ok])
    return np.interp(radius, centres, profile)


def measure_star_radius(image, model, cx, cy, max_radius, noise):
    """How far out a star still stands above the galaxy underneath it.

    Measured rather than predicted from the magnitude: the image is in whatever units the survey
    delivers, and a relation between a Gaia G and a peak in those units would have to be calibrated
    per survey and per band. The profile is walked outward until the annulus median falls to the
    galaxy's own level, which needs no calibration at all.
    """
    ny, nx = image.shape
    y0, y1 = max(0, int(cy - max_radius) - 1), min(ny, int(cy + max_radius) + 2)
    x0, x1 = max(0, int(cx - max_radius) - 1), min(nx, int(cx + max_radius) + 2)
    if y1 - y0 < 3 or x1 - x0 < 3:
        return 0.0
    sub = image[y0:y1, x0:x1]
    submodel = model[y0:y1, x0:x1]
    yy, xx = np.mgrid[y0:y1, x0:x1]
    rr = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)

    for r in range(1, int(max_radius) + 1):
        sel = (rr >= r) & (rr < r + 1)
        if not sel.any():
            break
        excess = np.median(sub[sel] - submodel[sel])
        if excess < noise:
            return float(r)

    # THE EDGE WAS NEVER FOUND, WHICH IS NOT THE SAME AS A STAR THIS BIG, and returning max_radius
    # said it was. The walk stops when the annulus median falls to the galaxy's own level, and on a
    # structured galaxy it often never does: a spiral arm, a dust lane or a bright knot keeps the
    # median above the smooth azimuthal model all the way out. The star then masked the full
    # max-star-radius disc, 30 arcsec by default, which on a 1 arcsec/px map is a 60 arcsec hole
    # punched through the galaxy and filled flat. Those are the big smooth discs visible either
    # side of NGC5247 in game.
    #
    # Declining is the safe answer. A star left in the map costs its own flux out of the galaxy's
    # budget and a second draw from the star catalogue, both bounded and both local to a point
    # source; masking a 60 arcsec disc of real galaxy and inpainting it is neither.
    return 0.0


def process_band(image, header, galaxy, others, companions, stars, args, report):
    """One band, from the survey's pixels to a unit-total shape map."""
    from astropy.wcs import WCS

    image = np.array(image, dtype=np.float64)
    ny, nx = image.shape
    wcs = WCS(header)
    # The tangent point, from the header rather than from the array's middle: hips2fits puts CRPIX
    # at N/2, which in zero-based pixels is N/2 - 1 and not (N-1)/2. Half a pixel of centring error
    # is half a pixel of everything downstream, including the position angle this file measures
    # back as a check.
    centre = (float(header["CRPIX1"]) - 1.0, float(header["CRPIX2"]) - 1.0)

    scale_arcsec = abs(header["CDELT1"]) * 3600.0 if "CDELT1" in header \
        else abs(header["CD1_1"]) * 3600.0
    semi_major_px = (galaxy["d25"] * 60.0 * 0.5) / scale_arcsec

    radius = elliptical_radius(image.shape, centre, galaxy["axis"], galaxy["pa"])
    finite = np.isfinite(image)

    # COVERAGE IS JUDGED ON THE GALAXY, NOT ON THE BOX. Pan-STARRS is served in 0.4 degree skycells
    # and a box that crosses one edge comes back with a missing corner, which says nothing about
    # whether the galaxy itself was observed. What has to be complete is the isophote and a little
    # beyond it; a missing corner of empty sky is filled by the same azimuthal model that fills a
    # masked star, and is reported.
    inner = radius < args.coverage_radii * semi_major_px
    report["nan_fraction"] = float(1.0 - finite.mean())
    report["nan_inside"] = float(1.0 - finite[inner].mean()) if inner.any() else 1.0
    if report["nan_inside"] > args.max_nan_inside or report["nan_fraction"] > args.max_nan:
        return None

    image = np.where(finite, image, 0.0)

    # CLIPPED DATA, which is the one defect that survives every other check in this file.
    #
    # The Legacy DR10 r HiPS returns, at some orders and some positions, a flat plateau of exactly
    # 10.0 over the Sombrero's nucleus while the rest of the cutout runs to 19.3. Nothing says so,
    # the image still looks like a galaxy, and packed it would have removed the nucleus: the
    # Sombrero's central 5 arcsec came out holding 1.6 per cent of the light in r against 5.1 in g,
    # for the same galaxy.
    #
    # The detector needs no threshold on the values themselves. Real sky data, in floating point,
    # essentially never repeats a value exactly; a clip does nothing else. So the brightest
    # percentile is checked for an exactly-repeated value, and a band that has one is refused rather
    # than packed, which falls through to the next survey.
    bright = image[image > np.percentile(image, 99.0)]
    if bright.size > 100:
        values, counts = np.unique(bright, return_counts=True)
        worst = int(counts.max())
        report["repeated_bright_value"] = worst
        if worst > 8:
            report["clipped"] = True
            return None

    # 1. Residual sky, from well outside the isophote. Both the Legacy and the Pan-STARRS HiPS are
    #    already background subtracted, so this is a pedestal check as much as a subtraction; DSS2
    #    is not subtracted at all and needs it.
    sky_region = (radius > args.sky_inner * semi_major_px) & finite
    if np.count_nonzero(sky_region) > 100:
        values = image[sky_region]
        for _ in range(5):
            med, sd = np.median(values), np.std(values)
            keep = np.abs(values - med) < 3.0 * sd
            if keep.all() or np.count_nonzero(keep) < 50:
                break
            values = values[keep]
        sky = float(np.median(values))
        noise = float(np.std(values))
    else:
        sky, noise = 0.0, float(np.std(image))
    image -= sky
    report["sky"] = sky
    report["noise"] = noise

    masked = np.zeros(image.shape, dtype=bool)

    # 2. Catalogued galaxies whose centre falls OUTSIDE the box but whose disc reaches into it.
    #    Those are drawn from their own entry and their own map, so their light cannot stay here.
    #    A companion whose centre is INSIDE the box is not masked at all; see `companions`.
    for other in others:
        try:
            ox, oy = wcs.all_world2pix(other["ra"], other["dec"], 0)
        except Exception:                                           # noqa: BLE001
            continue
        if not (np.isfinite(ox) and np.isfinite(oy)):
            continue
        orad = elliptical_radius(image.shape, (float(ox), float(oy)), other["axis"], other["pa"])
        masked |= orad < (other["d25"] * 60.0 * 0.5) / scale_arcsec

    # 3. Foreground stars, at the radius where each stops standing above the galaxy.
    protect = [(centre[0], centre[1], args.nucleus_protect * semi_major_px)]
    for c in companions:
        try:
            cxp, cyp = wcs.all_world2pix(c["ra"], c["dec"], 0)
            protect.append((float(cxp), float(cyp),
                            args.nucleus_protect * (c["d25"] * 30.0) / scale_arcsec))
        except Exception:                                           # noqa: BLE001
            pass

    model = azimuthal_median(image, radius, ~masked & finite, args.profile_bins)
    yy, xx = np.mgrid[0:ny, 0:nx]
    star_pixels = 0
    for (sra, sdec, gmag) in stars:
        try:
            sx, sy = wcs.all_world2pix(sra, sdec, 0)
        except Exception:                                           # noqa: BLE001
            continue
        sx, sy = float(sx), float(sy)
        if not (0 <= sx < nx and 0 <= sy < ny):
            continue
        # No nucleus is ever masked: a bright nucleus can carry an astrometric solution of its own,
        # and removing it would replace a galaxy's centre with its own outskirts.
        if any(math.hypot(sx - px, sy - py) < pr for (px, py, pr) in protect):
            continue
        r = measure_star_radius(image, model, sx, sy, args.max_star_radius / scale_arcsec,
                                max(noise, 1e-12))
        if r <= 0.0:
            continue
        masked |= ((xx - sx) ** 2 + (yy - sy) ** 2) < (r + 1.0) ** 2
        star_pixels += 1
    report["stars_masked"] = star_pixels
    report["masked_fraction"] = float(masked.mean())

    # 4. Everything removed is replaced by what surrounds it.
    #
    #    The first version filled every hole with the elliptical azimuthal median at its radius,
    #    and that is wrong on a spiral: the median at a given radius is the INTERARM level, so a
    #    star sitting on an arm was replaced by a disc of interarm sky and the map came out with
    #    black holes punched through its arms. The fill is now local, a Gaussian-weighted average
    #    of the unmasked pixels around the hole, widened until the hole is covered. The azimuthal
    #    model answers where nothing local survives, and is blended in toward the MIDDLE of a large
    #    hole, where the widened average has degenerated into a flat plateau; see below.
    model = azimuthal_median(image, radius, ~masked & finite, args.profile_bins)
    holes = masked | ~finite
    filled = np.where(holes, np.nan, image)
    if holes.any():
        from scipy.ndimage import gaussian_filter, distance_transform_edt
        known = (~holes).astype(np.float64)
        values = np.where(holes, 0.0, image)
        sigma0 = max(1.0, args.max_star_radius / scale_arcsec / 4.0)
        sigma = sigma0
        for _ in range(6):
            num = gaussian_filter(values, sigma)
            den = gaussian_filter(known, sigma)
            estimate = np.where(den > 1e-3, num / np.maximum(den, 1e-30), np.nan)
            still = ~np.isfinite(filled)
            filled = np.where(still & np.isfinite(estimate), estimate, filled)
            if np.isfinite(filled).all():
                break
            sigma *= 2.0
        filled = np.where(np.isfinite(filled), filled, model)

        # DEEP INSIDE A BIG HOLE THE LOCAL AVERAGE KNOWS NOTHING, so it is blended out there in
        # favour of the galaxy's own elliptical profile.
        #
        # Each doubling above widens the average over four times the area, so by the time a mask
        # tens of arcseconds across has closed, every pixel in its middle has been handed the same
        # mean of a large annulus: a flat plateau with an arc for an edge, which is what a bright
        # star on NGC5247's outskirts left behind. The azimuthal model has no local detail either,
        # but it does have the one thing that matters at that size, the radial falloff, so the patch
        # sits on the galaxy's own gradient instead of cutting a step across it.
        #
        # This is NOT a return to filling everything with the model, which is the version the
        # comment above records as wrong: near the rim of a hole the local average carries the arm
        # or dust lane the star was sitting on, and that is exactly where it is kept. The weight is
        # the distance to the nearest surviving pixel, so a small mask is filled locally end to end
        # and only the middle of a large one reaches the model.
        depth = distance_transform_edt(holes)
        blend = np.clip(depth / max(3.0 * sigma0, 1e-9), 0.0, 1.0)
        filled = np.where(holes, (1.0 - blend) * filled + blend * model, filled)

        # AND THE FILL CARRIES THE SURVEY'S OWN SCATTER, because a hole that is merely smooth is
        # still a visible hole. Each doubling above widens the average over four times the area, so
        # a mask that only closes after several of them comes back flat to a part in tens, and a
        # flat patch on a galaxy full of structure reads as a disc drawn on the image whatever its
        # level. The noise is zero-mean, so it costs the map nothing in flux, and it is the noise
        # measured off this very image rather than an invented amplitude.
        rng = np.random.default_rng(0)
        filled = np.where(holes, filled + rng.normal(0.0, noise, filled.shape), filled)

    # 5. The survey's OWN NOISE is not this galaxy's light, and it must not be packed as if it
    #    were. A unit-total map spreads whatever is in the box over a million pixels, so a sky that
    #    is merely noisy contributes a real fraction of the total: on the Pan-STARRS r band of M51
    #    it took the map's peak down by a factor of sixty against the g band, purely because the
    #    noise floor had been clipped positive and summed.
    #
    #    So each pixel is shrunk toward the elliptical model by its own significance, which is the
    #    Wiener weight w = S^2/(S^2 + k) for a residual measured at signal-to-noise S. Where the
    #    galaxy is bright the weight is one and the survey's pixels pass through untouched, arms,
    #    dust lanes and knots included; where only noise remains the weight falls to zero and the
    #    smooth model answers. The significance is measured on the residual SMOOTHED to the scale
    #    set by --denoise-scale, because a single pixel of a faint arm is not significant while the
    #    arm is.
    from scipy.ndimage import gaussian_filter

    residual = filled - model
    sigma_px = max(0.5, args.denoise_scale / scale_arcsec)
    smoothed = gaussian_filter(residual, sigma_px)
    # Noise of the smoothed residual: a Gaussian filter of width sigma divides white noise by
    # 2*sqrt(pi)*sigma in quadrature-equivalent terms.
    smoothed_noise = max(noise / (2.0 * math.sqrt(math.pi) * sigma_px), 1e-30)
    significance = smoothed / smoothed_noise
    weight = significance ** 2 / (significance ** 2 + args.denoise_k ** 2)
    filled = model + weight * residual
    report["denoise_kept"] = float(weight.mean())

    # 6. Negative pixels are noise below the sky, not negative light. After the shrinkage the only
    #    ones left are inside genuinely detected structure (a dust lane against a bright arm).
    filled = np.clip(filled, 0.0, None)

    # 7. Apodise to zero over the outer margin so the map joins the sky continuously instead of
    #    ending on a step, exactly as the emission patches do.
    edge = np.maximum(np.abs(xx - centre[0]), np.abs(yy - centre[1]))
    half = 0.5 * (min(nx, ny) - 1)
    taper_start = (1.0 - args.taper) * half
    if args.taper > 0.0:
        t = np.clip((edge - taper_start) / max(1e-9, half - taper_start), 0.0, 1.0)
        filled = filled * (0.5 * (1.0 + np.cos(math.pi * t)))

    total = float(filled.sum())
    if not (total > 0.0):
        return None
    unit = filled / total

    # 8. Diagnostics. The measured ellipse is the end-to-end check on the geometry: the second
    #    moments of the packed map must reproduce the catalogued axis ratio and position angle, and
    #    they cannot unless the WCS, the north/east convention and the position angle convention
    #    all agree. A mirrored map shows up here as a position angle reflected about zero.
    report["flux_inside_d25"] = float(unit[radius < semi_major_px].sum())
    # Enclosed flux in the core, which is what a clipped nucleus takes away and what the bands are
    # compared on afterwards.
    report["flux_in_core"] = float(unit[radius < max(3.0, 0.05 * semi_major_px)].sum())
    peak_yx = np.unravel_index(np.argmax(unit), unit.shape)
    report["peak_offset_arcsec"] = float(
        math.hypot(peak_yx[1] - centre[0], peak_yx[0] - centre[1]) * scale_arcsec)

    inner = radius < semi_major_px
    w = unit * inner
    wsum = w.sum()
    if wsum > 0:
        mx = float((w * xx).sum() / wsum)
        my = float((w * yy).sum() / wsum)
        cxx = float((w * (xx - mx) ** 2).sum() / wsum)
        cyy = float((w * (yy - my) ** 2).sum() / wsum)
        cxy = float((w * (xx - mx) * (yy - my)).sum() / wsum)
        tr, det = cxx + cyy, cxx * cyy - cxy * cxy
        disc = max(0.0, 0.25 * tr * tr - det)
        lam1, lam2 = 0.5 * tr + math.sqrt(disc), 0.5 * tr - math.sqrt(disc)
        report["measured_axis_ratio"] = math.sqrt(max(lam2, 0.0) / lam1) if lam1 > 0 else float("nan")
        # Position angle east of north, i.e. from +y toward -x, which is the catalogue's convention.
        ang = 0.5 * math.atan2(2.0 * cxy, cxx - cyy)
        major = (math.cos(ang), math.sin(ang))
        report["measured_pa"] = math.degrees(math.atan2(-major[0], major[1])) % 180.0

    return unit


# --------------------------------------------------------------------------------------------
# Packing
# --------------------------------------------------------------------------------------------

def to_float16_plane(unit_map):
    """Store the map relative to its own peak, which is what keeps float16 in its normal range.

    A unit-total map of a thousand pixels a side holds values around 1e-6, which is below float16's
    smallest normal value; stored that way the outskirts would quantise to a handful of levels.
    Divided by the peak the values run from 1 down, and the scale that turns them back into a unit
    total is recomputed AFTER quantisation so the sum is exactly one however the rounding fell.
    """
    peak = float(unit_map.max())
    if not (peak > 0.0):
        return None, 0.0
    quantised = (unit_map / peak).astype(np.float16)
    total = float(np.sum(quantised.astype(np.float64)))
    if not (total > 0.0):
        return None, 0.0
    return quantised, 1.0 / total


def write_string(f, text):
    b = text.encode("utf-8")
    f.write(struct.pack("<i", len(b)))
    f.write(b)


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--catalog", default="GalaxyCatalog.galcat",
                   help="the packed catalogue the maps are keyed to")
    p.add_argument("--out", default="GalaxyImages.galimg")
    p.add_argument("--bmax", type=float, default=11.0,
                   help="faintest total B magnitude to fetch a map for (11 is the sky chart's own cut)")
    p.add_argument("--max-count", type=int, default=0, help="stop after this many galaxies (0 = all)")
    p.add_argument("--only", default="", help="comma-separated names, for testing one object")
    p.add_argument("--box-factor", type=float, default=2.0,
                   help="box side in units of D25; 2 puts the isophote at half the half-width")
    p.add_argument("--target-pixel-arcsec", type=float, default=1.0,
                   help="stored sampling where the size cap allows it")
    p.add_argument("--max-pixels", type=int, default=1024, help="cap on the stored grid per side")
    p.add_argument("--min-pixels", type=int, default=128)
    p.add_argument("--giant-pixels", type=int, default=0,
                   help="raised cap for the few objects larger than --giant-arcmin; 4096 puts M31 "
                        "at 4.2 arcsec per map pixel instead of 16.7, for 32 MB of file")
    p.add_argument("--giant-arcmin", type=float, default=30.0,
                   help="D25 past which --giant-pixels applies")
    p.add_argument("--star-mag-limit", type=float, default=19.0,
                   help="faintest Gaia G a foreground star is removed at")
    p.add_argument("--max-star-radius", type=float, default=30.0, help="arcsec")
    p.add_argument("--max-stars", type=int, default=0,
                   help="most foreground stars removed from one map, brightest first; "
                        "0 masks none and leaves the survey's own pixels alone")
    p.add_argument("--nucleus-protect", type=float, default=0.05,
                   help="fraction of the semi-major axis around the centre no star mask may touch")
    p.add_argument("--sky-inner", type=float, default=1.4,
                   help="inner edge of the sky region, in semi-major axes")
    p.add_argument("--profile-bins", type=int, default=96)
    p.add_argument("--max-core-ratio", type=float, default=5.0,
                   help="largest disagreement allowed between two bands on the light in the core; "
                        "a dusty edge-on galaxy legitimately reaches 2.65 (Centaurus A)")
    p.add_argument("--denoise-scale", type=float, default=2.0,
                   help="arcsec; the scale the residual's significance is measured on")
    p.add_argument("--denoise-k", type=float, default=2.0,
                   help="signal-to-noise at which a pixel is kept at half weight")
    p.add_argument("--taper", type=float, default=0.06, help="fraction of the half-width apodised")
    p.add_argument("--max-nan", type=float, default=0.5,
                   help="reject a band with more than this fraction of the BOX outside the survey")
    p.add_argument("--max-nan-inside", type=float, default=0.005,
                   help="reject a band with more than this fraction of the GALAXY not observed")
    p.add_argument("--coverage-radii", type=float, default=1.2,
                   help="semi-major axes out to which coverage has to be complete")
    p.add_argument("--ps1-native-max-arcmin", type=float, default=16.0,
                   help="largest box fetched from the Pan-STARRS stack service, which serves only "
                        "0.25 arcsec pixels; larger boxes fall back to its g-band HiPS")
    p.add_argument("--cache", default="galaxy_image_cache")
    p.add_argument("--dry-run", action="store_true", help="report the budget and fetch nothing")
    args = p.parse_args()

    import requests

    source, catalog = read_catalog(args.catalog)
    print("catalogue: %d galaxies, %s" % (len(catalog), source))

    only = [s.strip().replace(" ", "").upper() for s in args.only.split(",") if s.strip()]
    if only:
        selected = [g for g in catalog if g["name"].replace(" ", "").upper() in only]
    else:
        selected = [g for g in catalog if g["bt"] <= args.bmax]
    selected.sort(key=lambda g: g["bt"])
    if args.max_count > 0:
        selected = selected[:args.max_count]

    def grid_for(g):
        """Stored grid for one galaxy, and hence its sampling.

        The cap is what limits the giants: M31's box is 4.7 degrees, so at 1024 pixels it is stored
        at 16.7 arcsec per pixel, which the RC20 out-resolves fifteen times over. --giant-pixels
        raises the cap for objects past --giant-arcmin only, which is three or four galaxies in the
        whole catalogue, so the file grows by tens of megabytes rather than by a factor.
        """
        side_arcsec = args.box_factor * g["d25"] * 60.0
        n = int(round(side_arcsec / args.target_pixel_arcsec))
        cap = args.max_pixels
        if args.giant_pixels > 0 and g["d25"] >= args.giant_arcmin:
            cap = max(cap, args.giant_pixels)
        return max(args.min_pixels, min(cap, n))

    budget = sum(grid_for(g) ** 2 * 2 * 2 for g in selected)
    print("%d galaxies selected, %.1f MB of maps at the current grid settings"
          % (len(selected), budget / 1024 / 1024))
    if args.dry_run:
        for g in selected[:20]:
            n = grid_for(g)
            print("  %-12s B_T %5.2f  D25 %6.2f'  grid %4d  %.2f\"/px"
                  % (g["name"], g["bt"], g["d25"], n,
                     args.box_factor * g["d25"] * 60.0 / n))
        return 0

    if args.cache:
        os.makedirs(args.cache, exist_ok=True)
    session = requests.Session()
    gaia_cache = {}

    entries = []
    for i, g in enumerate(selected):
        n = grid_for(g)
        side_deg = args.box_factor * g["d25"] / 60.0
        scale = side_deg * 3600.0 / n
        print("[%d/%d] %s  B_T %.2f  D25 %.2f'  grid %d at %.2f\"/px"
              % (i + 1, len(selected), g["name"], g["bt"], g["d25"], n, scale))

        # Catalogued neighbours near this box, split by where their CENTRE falls.
        #
        # A companion inside the box is SWALLOWED, not masked. Masking M51's companion left an
        # elliptical hole across M51's own northern arm, because the two overlap on the sky and no
        # mask can separate them; and the bridge between them belongs to neither entry. So the map
        # keeps the pair as the survey saw it, the renderer normalises it to the SUM of the
        # catalogued fluxes, and the companion is not drawn a second time from its own entry.
        #
        # A galaxy whose centre is outside the box is masked as before: only a piece of it is here,
        # so its own entry is the one that can draw it whole.
        near = [o for o in catalog
                if o is not g
                and abs(o["dec"] - g["dec"]) < side_deg
                and abs((o["ra"] - g["ra"] + 180.0) % 360.0 - 180.0)
                * math.cos(math.radians(g["dec"])) < side_deg]
        half = 0.5 * side_deg
        companions, others = [], []
        for o in near:
            dd = o["dec"] - g["dec"]
            dr = ((o["ra"] - g["ra"] + 180.0) % 360.0 - 180.0) * math.cos(math.radians(g["dec"]))
            inside = abs(dd) < half * 0.9 and abs(dr) < half * 0.9
            (companions if inside else others).append(o)

        # NO STARS MASKED AT ALL when --max-stars is 0, and that is a supported choice rather than
        # a degenerate one. Masking a star means inpainting the hole, and no inpainting can invent
        # the galaxy that was behind it: on a big mask the fill is a smooth patch, which reads as a
        # disc drawn on the image and is far more obviously wrong than the star would have been.
        # Left in, a star costs its own flux out of the galaxy's normalised budget and a second
        # draw from the star catalogue. Both are bounded, both are point-like, and both look like
        # a star rather than like a defect.
        stars = fetch_gaia_foreground(session, g["ra"], g["dec"], side_deg * 0.75,
                                      args.star_mag_limit, args.max_stars, gaia_cache) \
                if args.max_stars > 0 else []

        chosen = None
        for survey in SURVEYS:
            if side_deg > survey["max_fov_deg"]:
                continue
            if survey["provider"] == "ps1" and side_deg * 60.0 > args.ps1_native_max_arcmin:
                continue
            # A BAND THAT FAILS DOES NOT CONDEMN THE SURVEY. The clip check rejects one band, not
            # the sky: six southern galaxies lost their maps entirely because their Legacy r was
            # clipped and nothing else covers them, while their g band was perfectly good. So a
            # survey is kept as long as ONE band survives, and the entry then carries one map and
            # the renderer uses the same shape at every wavelength, which is the honest thing to do
            # when the colour structure was never measured.
            planes, reports, ok = [], [], True
            for (hips, wavelength, label) in survey["bands"]:
                hdu = (fetch_fits(session, hips, g["ra"], g["dec"], side_deg, n, args.cache)
                       if survey["provider"] == "hips"
                       else fetch_ps1(session, hips, g["ra"], g["dec"], side_deg, n, args.cache))
                if hdu is None:
                    print("      %s: not retrieved" % label)
                    continue
                report = {}
                plane = process_band(hdu.data, hdu.header, g, others, companions, stars,
                                     args, report)
                if plane is None:
                    if report.get("clipped"):
                        reason = ("clipped: %d of its brightest pixels hold one identical value, "
                                  "so the data has been cut off" % report["repeated_bright_value"])
                    elif (report.get("nan_inside", 1) > args.max_nan_inside
                          or report.get("nan_fraction", 1) > args.max_nan):
                        # Coverage is a property of the SURVEY at this position, not of the band,
                        # so there is nothing to gain by asking it for the other one.
                        print("      %s: not covered (%.0f%% of the galaxy, %.0f%% of the box "
                              "missing)" % (label, 100 * report.get("nan_inside", 1),
                                            100 * report.get("nan_fraction", 1)))
                        planes = []
                        break
                    else:
                        reason = "no usable flux"
                    print("      %s: %s" % (label, reason))
                    continue
                planes.append((plane, wavelength, label))
                reports.append(report)
            ok = len(planes) > 0
            # THE BANDS ARE COMPARED AGAINST EACH OTHER as a last-resort guard, and the threshold is
            # loose on purpose. A dusty edge-on galaxy really does hide its core in the blue:
            # Centaurus A's dust lane puts 0.124 per cent of the light in its core in g against
            # 0.329 in r, a factor of 2.65, which is 1.06 mag of differential extinction and is
            # exactly right. So this cannot be the test that catches a damaged band -- at 2.5 it
            # rejected Centaurus A -- and the clip detector above is. What is left here only catches
            # a disagreement past any plausible extinction.
            if ok and len(planes) > 1:
                cores = [r["flux_in_core"] for r in reports]
                lo, hi = min(cores), max(cores)
                if hi > 0.0 and (lo <= 0.0 or hi / lo > args.max_core_ratio):
                    # Past that ratio the fainter core is not extinction, it is a hole: NGC 7793
                    # came back with 0.000 per cent of its light in the core in one band against
                    # 2.740 in the other. So the damaged band is dropped and the good one kept,
                    # rather than the galaxy losing its map over it.
                    keep = int(max(range(len(cores)), key=lambda i: cores[i]))
                    print("      %s: the bands disagree on the core, %.3f%% against %.3f%% of the "
                          "light inside it; keeping %s and dropping the other"
                          % (survey["id"], 100 * lo, 100 * hi, planes[keep][2]))
                    planes = [planes[keep]]
                    reports = [reports[keep]]

            if ok:
                # A full set of bands settles it. A PARTIAL survey is remembered but the later
                # ones still get their turn: NGC1566's Legacy g is clipped while DES right behind
                # it has both bands clean, and stopping at the first partial answer would keep the
                # worse map. Ties in band count go to the earlier survey.
                if len(planes) == len(survey["bands"]):
                    chosen = (survey, planes, reports)
                    break
                if chosen is None or len(planes) > len(chosen[1]):
                    chosen = (survey, planes, reports)

        if chosen is not None and len(chosen[1]) < len(chosen[0]["bands"]):
            print("      %s: keeping %d band of %d, so the same shape answers at every "
                  "wavelength" % (chosen[0]["id"], len(chosen[1]), len(chosen[0]["bands"])))
        if chosen is None:
            print("      no survey covers it; the analytic profile stays in charge")
            continue

        survey, planes, reports = chosen
        r0 = reports[0]
        print("      %s: %d stars masked (%.1f%% of pixels), %.0f%% of the pixels kept by the "
              "shrinkage, %.1f%% of the flux inside D25"
              % (survey["id"], r0["stars_masked"], 100.0 * r0["masked_fraction"],
                 100.0 * r0["denoise_kept"], 100.0 * r0["flux_inside_d25"]))
        print("      shape check: b/a %.3f measured vs %.3f catalogued, PA %.1f vs %.1f, "
              "peak %.1f\" off centre"
              % (r0.get("measured_axis_ratio", float("nan")), g["axis"],
                 r0.get("measured_pa", float("nan")), g["pa"], r0["peak_offset_arcsec"]))
        if companions:
            print("      swallows " + ", ".join(c["name"] for c in companions))

        quantised = []
        for (plane, wavelength, label) in planes:
            data, scale_factor = to_float16_plane(plane)
            if data is None:
                quantised = []
                break
            quantised.append((data, scale_factor, wavelength, label))
        if not quantised:
            continue

        entries.append({
            "galaxy": g, "n": n, "scale_arcsec": scale, "survey": survey,
            "planes": quantised, "reports": reports,
            "companions": [c["name"] for c in companions],
        })

    if not entries:
        raise SystemExit("nothing packed")

    with open(args.out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<ii", VERSION, len(entries)))
        write_string(f, "shape maps from: " + ", ".join(
            sorted({e["survey"]["name"] for e in entries})))
        for e in entries:
            g = e["galaxy"]
            write_string(f, g["name"])
            f.write(struct.pack("<dd", g["ra"], g["dec"]))
            f.write(struct.pack("<i", e["n"]))
            f.write(struct.pack("<d", e["scale_arcsec"]))
            write_string(f, e["survey"]["id"])
            f.write(struct.pack("<B", 1))
            f.write(struct.pack("<ff", e["reports"][0]["masked_fraction"],
                                e["reports"][0]["flux_inside_d25"]))
            # The catalogued galaxies this map already contains. The renderer normalises to the sum
            # of their fluxes and skips their own entries, so nothing is drawn twice.
            f.write(struct.pack("<i", len(e["companions"])))
            for name in e["companions"]:
                write_string(f, name)
            f.write(struct.pack("<i", len(e["planes"])))
            for (data, scale_factor, wavelength, label) in e["planes"]:
                f.write(struct.pack("<d", wavelength))
                write_string(f, label)
                f.write(struct.pack("<d", scale_factor))
                f.write(data.astype("<f2").tobytes())

    size = os.path.getsize(args.out) / (1024 * 1024)
    print("wrote %s (%.1f MB), %d galaxies with real morphology of %d asked for"
          % (args.out, size, len(entries), len(selected)))

    # What ended up where, because "156 galaxies" says nothing about whether the maps are any good.
    by_survey = {}
    for e in entries:
        key = (e["survey"]["id"], len(e["planes"]))
        by_survey[key] = by_survey.get(key, 0) + 1
    for (survey_id, bands), n in sorted(by_survey.items()):
        print("  %4d from %-16s in %d band%s" % (n, survey_id, bands, "" if bands == 1 else "s"))

    coarse = sorted(entries, key=lambda e: -e["scale_arcsec"])[:5]
    print("  coarsest sampling: " + ", ".join(
        "%s %.1f\"/px" % (e["galaxy"]["name"], e["scale_arcsec"]) for e in coarse))
    print("  those are the ones an instrument out-resolves; the capture readout says so in game")
    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/")
    return 0


if __name__ == "__main__":
    sys.exit(main())
