"""
Cross-validation of Core/PupilDiffraction against POPPY, for the pupil WITH its spider.

The radially symmetric case is handled by compare_poppy.py. This one is the harder claim: that
six 50 cm vanes across the ELT pupil produce diffraction spikes of the right brightness, in the
right direction, with the right falloff, from geometry alone.

ExoInstruments sums closed-form transforms (a disc transform for the annulus, a product of sinc
functions for each vane). POPPY rasterises the same pupil and propagates it numerically. No shared
code, no shared method.
"""
import csv
import numpy as np
import poppy

ARCSEC_PER_RAD = 180.0 * 3600.0 / np.pi
SAMPLES_PER_LOD = 16
R_MAX_LOD = 20.0


def read_meta(tag):
    with open(f"exo_{tag}_meta.csv") as f:
        return {r["key"]: float(r["value"]) for r in csv.DictReader(f)}


def read_cuts(tag):
    r, spike, between = [], [], []
    with open(f"exo_{tag}.csv") as f:
        for row in csv.DictReader(f):
            r.append(float(row["r_over_lod"]))
            spike.append(float(row["along_spike"]))
            between.append(float(row["between_spikes"]))
    return np.array(r), np.array(spike), np.array(between)


meta = read_meta("eltvanes")
D, eps, lam = meta["aperture_m"], meta["obstruction"], meta["wavelength_m"]
vanes, vane_w = int(meta["vane_count"]), meta["vane_width_m"]
lod_arcsec = lam / D * ARCSEC_PER_RAD

osys = poppy.OpticalSystem(npix=2048, oversample=2)
osys.add_pupil(poppy.CircularAperture(radius=D / 2.0))
osys.add_pupil(poppy.SecondaryObscuration(
    secondary_radius=eps * D / 2.0, n_supports=vanes, support_width=vane_w))
osys.add_detector(pixelscale=lod_arcsec / SAMPLES_PER_LOD,
                  fov_arcsec=2.0 * R_MAX_LOD * lod_arcsec)
psf = osys.calc_psf(wavelength=lam, normalize="first")
data = psf[0].data
scale = psf[0].header["PIXELSCL"] / lod_arcsec  # lambda/D per pixel

cy, cx = (np.array(data.shape) - 1) / 2.0
peak = data.max()


def poppy_cut(angle_deg, radii_lod):
    """Bilinear sample of POPPY's PSF along a ray, normalised to the on-axis peak."""
    a = np.deg2rad(angle_deg)
    out = []
    for rr in radii_lod:
        x = cx + rr / scale * np.cos(a)
        y = cy + rr / scale * np.sin(a)
        x0, y0 = int(np.floor(x)), int(np.floor(y))
        fx, fy = x - x0, y - y0
        v = (data[y0, x0] * (1 - fx) * (1 - fy) + data[y0, x0 + 1] * fx * (1 - fy)
             + data[y0 + 1, x0] * (1 - fx) * fy + data[y0 + 1, x0 + 1] * fx * fy)
        out.append(v / peak)
    return np.array(out)


r_exo, spike_exo, between_exo = read_cuts("eltvanes")

print()
print("=" * 82)
print(f"ELT pupil WITH spider   D = {D} m, obstruction = {eps:.4f}, "
      f"{vanes} vanes of {vane_w} m")
print(f"   vanes remove {meta['vane_obscuration_fraction']*100:.3f}% of the open pupil   "
      f"POPPY sampling = {1/scale:.0f} px per lambda/D")
print("=" * 82)

# Where does POPPY put the spikes? Independent of what ExoInstruments claims.
ring = 6.0
angles = np.arange(0, 180, 0.5)
vals = poppy_cut_ring = np.array([poppy_cut(a, [ring])[0] for a in angles])
brightest = angles[np.argmax(vals)]
print(f"\nPOPPY's brightest azimuth at {ring} lambda/D: {brightest:.1f} deg")
print(f"ExoInstruments places its spikes at 30 / 90 / 150 deg "
      f"(perpendicular to vanes at 0 / 60 / 120)")

print(f"\n{'along a SPIKE (30 deg)':<30}{'ExoInstruments':>16}{'POPPY':>14}{'ratio':>12}")
print("-" * 82)
radii = [2.0, 3.0, 4.0, 6.0, 8.0, 12.0, 16.0]
p_spike = poppy_cut(30.0, radii)
for rr, pv in zip(radii, p_spike):
    ev = float(np.interp(rr, r_exo, spike_exo))
    print(f"{'  r = ' + format(rr, '.0f') + ' lambda/D':<30}{ev:>16.4e}{pv:>14.4e}{pv/ev:>12.4f}")

print(f"\n{'BETWEEN spikes (0 deg)':<30}{'ExoInstruments':>16}{'POPPY':>14}{'ratio':>12}")
print("-" * 82)
p_btw = poppy_cut(0.0, radii)
for rr, pv in zip(radii, p_btw):
    ev = float(np.interp(rr, r_exo, between_exo))
    print(f"{'  r = ' + format(rr, '.0f') + ' lambda/D':<30}{ev:>16.4e}{pv:>14.4e}{pv/ev:>12.4f}")

# The single number that matters for the display: how much brighter the spike is than the
# surrounding ring background. That contrast is what a viewer actually sees.
print(f"\n{'spike / background contrast':<30}{'ExoInstruments':>16}{'POPPY':>14}{'ratio':>12}")
print("-" * 82)
for rr in (4.0, 6.0, 8.0, 12.0):
    e = float(np.interp(rr, r_exo, spike_exo)) / float(np.interp(rr, r_exo, between_exo))
    p = poppy_cut(30.0, [rr])[0] / poppy_cut(0.0, [rr])[0]
    print(f"{'  r = ' + format(rr, '.0f') + ' lambda/D':<30}{e:>16.1f}{p:>14.1f}{p/e:>12.4f}")
print()
