"""
Cross-validation of ExoInstruments' annular-pupil diffraction PSF against POPPY.

Same pupil, same wavelength, same normalisation. ExoInstruments computes the pattern from the
closed form (Born & Wolf obstructed-aperture case); POPPY propagates a sampled pupil numerically
(matrix Fourier transform). They share no code and no method, so agreement is a real check.

Comparators are all dimensionless and truncation-matched, so neither code's array size or
quadrature range can flatter it:
  * radial intensity profile normalised to its own on-axis peak
  * encircled energy normalised to the energy inside the same outer radius in both codes
  * core FWHM and first-null radius, both in lambda/D
"""
import csv
import numpy as np
import poppy
import astropy.units as u

ARCSEC_PER_RAD = 180.0 * 3600.0 / np.pi
CASES = [
    ("elt", "ELT, H band"),
    ("rc20", "RC20, Luminance"),
    ("clear", "ELT pupil, obstruction removed"),
]
R_MAX_LOD = 40.0  # outer radius both codes are normalised inside


def read_exo(tag):
    meta = {}
    with open(f"exo_{tag}_meta.csv") as f:
        for row in csv.DictReader(f):
            meta[row["key"]] = float(row["value"])
    r, intensity, ee = [], [], []
    with open(f"exo_{tag}.csv") as f:
        for row in csv.DictReader(f):
            r.append(float(row["r_over_lod"]))
            intensity.append(float(row["intensity_norm"]))
            ee.append(float(row["encircled_energy"]))
    return meta, np.array(r), np.array(intensity), np.array(ee)


def poppy_psf(D, eps, lam, samples_per_lod=16):
    """POPPY's PSF for the same annular pupil, on a detector fine enough to resolve the rings."""
    lod_arcsec = lam / D * ARCSEC_PER_RAD
    pixelscale = lod_arcsec / samples_per_lod
    fov = 2.0 * R_MAX_LOD * lod_arcsec  # full width

    osys = poppy.OpticalSystem(npix=1024, oversample=4)
    osys.add_pupil(poppy.CircularAperture(radius=D / 2.0))
    if eps > 0:
        osys.add_pupil(poppy.SecondaryObscuration(
            secondary_radius=eps * D / 2.0, n_supports=0))
    osys.add_detector(pixelscale=pixelscale, fov_arcsec=fov)

    psf = osys.calc_psf(wavelength=lam, normalize="first")
    # calc_psf returns the oversampled and the detector-sampled extensions; take the finest.
    hdu = psf[0]
    scale = hdu.header["PIXELSCL"] / lod_arcsec  # lambda/D per pixel
    return hdu.data, scale


def radial_profile(data, scale):
    """Azimuthally averaged profile and cumulative energy, in lambda/D units."""
    cy, cx = (np.array(data.shape) - 1) / 2.0
    y, x = np.indices(data.shape)
    r = np.hypot(x - cx, y - cy) * scale

    order = np.argsort(r.ravel())
    r_sorted = r.ravel()[order]
    v_sorted = data.ravel()[order]

    # Encircled energy: cumulative sum of the actual pixel values.
    ee = np.cumsum(v_sorted)

    # Profile: mean value in narrow annuli.
    nbins = 2000
    edges = np.linspace(0, R_MAX_LOD, nbins + 1)
    idx = np.digitize(r_sorted, edges) - 1
    prof = np.full(nbins, np.nan)
    for b in range(nbins):
        m = idx == b
        if m.any():
            prof[b] = v_sorted[m].mean()
    centres = 0.5 * (edges[:-1] + edges[1:])
    return centres, prof, r_sorted, ee


def interp(x, xs, ys):
    return float(np.interp(x, xs, ys))


def measure_fwhm(centres, prof):
    peak = np.nanmax(prof[:50])
    half = 0.5 * peak
    for i in range(1, len(prof)):
        if not np.isnan(prof[i]) and prof[i] <= half:
            x0, x1 = centres[i - 1], centres[i]
            y0, y1 = prof[i - 1], prof[i]
            return 2.0 * (x0 + (y0 - half) / (y0 - y1) * (x1 - x0))
    return float("nan")


def measure_first_null(centres, prof):
    p = np.nan_to_num(prof, nan=np.inf)
    for i in range(2, len(p) - 1):
        if p[i] < p[i - 1] and p[i] <= p[i + 1] and centres[i] > 0.3:
            return centres[i]
    return float("nan")


print()
for tag, label in CASES:
    meta, r_exo, i_exo, ee_exo = read_exo(tag)
    D, eps, lam = meta["aperture_m"], meta["obstruction"], meta["wavelength_m"]

    data, scale = poppy_psf(D, eps, lam)
    centres, prof, r_sorted, ee_p = radial_profile(data, scale)

    peak_p = np.nanmax(prof[:50])
    prof_n = prof / peak_p

    # Normalise both encircled-energy curves inside the same outer radius.
    ee_p_ref = interp(R_MAX_LOD, r_sorted, ee_p)
    ee_exo_ref = interp(R_MAX_LOD, r_exo, ee_exo)

    print("=" * 78)
    print(f"{label}   D = {D} m, obstruction = {eps:.4f}, lambda = {lam * 1e9:.1f} nm")
    print(f"   lambda/D = {meta['lambda_over_d_arcsec'] * 1000:.4f} mas"
          f"   POPPY sampling = {1 / scale:.1f} px per lambda/D")
    print("=" * 78)

    fw_p = measure_fwhm(centres, prof)
    fn_p = measure_first_null(centres, prof)
    print(f"{'quantity':<34}{'ExoInstruments':>16}{'POPPY':>14}{'diff':>12}")
    print("-" * 78)
    print(f"{'core FWHM  [lambda/D]':<34}{meta['fwhm_over_lod']:>16.5f}{fw_p:>14.5f}"
          f"{(fw_p / meta['fwhm_over_lod'] - 1) * 100:>11.3f}%")
    print(f"{'first null [lambda/D]':<34}{meta['first_null_over_lod']:>16.5f}{fn_p:>14.5f}"
          f"{(fn_p / meta['first_null_over_lod'] - 1) * 100:>11.3f}%")

    print(f"\n{'encircled energy within r':<34}{'ExoInstruments':>16}{'POPPY':>14}{'diff':>12}")
    print("-" * 78)
    for rr in (1.0, 1.5, 2.0, 3.0, 5.0, 10.0, 20.0):
        a = interp(rr, r_exo, ee_exo) / ee_exo_ref
        b = interp(rr, r_sorted, ee_p) / ee_p_ref
        print(f"{'  r = ' + format(rr, '.1f') + ' lambda/D':<34}{a:>16.5f}{b:>14.5f}{(b - a) * 100:>11.3f}%")

    print(f"\n{'intensity / peak at r':<34}{'ExoInstruments':>16}{'POPPY':>14}{'diff':>12}")
    print("-" * 78)
    for rr in (0.5, 1.0, 1.5, 2.0, 3.0, 5.0, 8.0):
        a = interp(rr, r_exo, i_exo)
        b = interp(rr, centres, prof_n)
        d = (b - a) / a * 100 if a > 1e-12 else float("nan")
        print(f"{'  r = ' + format(rr, '.1f') + ' lambda/D':<34}{a:>16.3e}{b:>14.3e}{d:>11.2f}%")
    print()
