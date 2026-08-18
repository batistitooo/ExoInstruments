#!/usr/bin/env python3
"""Packs the Nugent supernova spectral templates into the binary the mod ships.

WHAT A TEMPLATE IS. A supernova is a point source whose spectrum evolves over weeks; each Nugent
template is that evolution as measured: a full spectrum (1000-25000 Angstrom) at every phase, one
file per SN class, format `time(days since explosion)  lambda(A)  f_lambda(arbitrary)`. A spectrum
per phase is what makes every filter come out right by integration, narrowband H-alpha included,
instead of carrying one light curve per band.

WHAT THE PACKER PRECOMPUTES, so the game does no spectral calibration at all:

  * per phase, the Bessell-B magnitude offset from the template's own B peak (passband from the
    SVO Filter Profile Service). The game anchors the peak at an absolute M_B drawn from
    Richardson et al. (2014) and adds the distance modulus.
  * per phase, vAnchor = V_mod(phase) - B(peak): the bridge from that B anchor to the magnitude
    the MOD calls V, which is photon density at 5556 A against its own zero point of 948
    photons/cm^2/s/A (PhotonFluxModel.ZeroMagPhotonFluxPerAngstrom). Physical units enter once,
    here: the Vega B zero point f0_B = 6.32e-9 erg/cm^2/s/A (Bessell, Castelli & Plez 1998,
    Table A2) and hc. Using the mod's own 948 rather than a nominal Vega photon flux is what
    keeps a supernova and a catalogue star on one flux scale.
  * per phase, the spectrum as photon density normalised to 1 at 5556 A, decimated to the stored
    grid: exactly the shape SystemResponse integrates for a star, with the Planck curve replaced
    by the measured one.

Templates (https://c3.lbl.gov/nugent/nugent_templates.html):
  Ia    sn1a_flux.v1.2    Nugent, Kim & Perlmutter (2002), PASP 114, 803 (stretch = 1.0)
  Ibc   sn1bc_flux.v1.1   Levan et al. (2005); light curve Hamuy et al. (2002), AJ 124, 417
  IIP   sn2p_flux.v1.2    Gilliland, Nugent & Phillips (1999), ApJ 521, 30; Baron et al. (2004)
  IIL   sn2l_flux.v1.2    Gilliland, Nugent & Phillips (1999), ApJ 521, 30
  IIn   sn2n_flux.v2.1    Gilliland, Nugent & Phillips (1999); SN 1999el, Di Carlo et al. (2002)

Run:
    ./env/bin/python pack_supernova_templates.py \
        --out ../ExoInstruments/PluginData/SupernovaTemplates.sntpl
"""

import argparse
import gzip
import io
import math
import os
import struct
import sys

import numpy as np

NUGENT_BASE = "https://c3.lbl.gov/nugent/templates/"
SVO_BESSELL_B = ("http://svo2.cab.inta-csic.es/theory/fps/getdata.php"
                 "?format=ascii&id=Generic/Bessell.B")

TEMPLATES = [
    ("Ia",  "sn1a_flux.v1.2.dat.gz"),
    ("Ibc", "sn1bc_flux.v1.1.dat.gz"),
    ("IIP", "sn2p_flux.v1.2.dat.gz"),
    ("IIL", "sn2l_flux.v1.2.dat.gz"),
    ("IIn", "sn2n_flux.v2.1.dat.gz"),
]

MAGIC = b"EXOSNTP1"
VERSION = 1

# Stored spectral grid: 20 A steps over the range the roster's detectors reach (WFC3/UVIS blue
# edge 2000 A, WFC3/IR red edge 17000 A). The native grid is 10 A; 20 keeps H-alpha structure.
GRID_MIN_A = 1000.0
GRID_MAX_A = 17000.0
GRID_STEP_A = 20.0

JOHNSON_V_A = 5556.0
VEGA_B_ZERO_ERG = 6.32e-9        # erg/cm^2/s/A at B = 0; Bessell, Castelli & Plez 1998, Table A2
MOD_V_ZERO_PHOTONS = 948.0       # photons/cm^2/s/A at V_mod = 0; the mod's own zero point
HC_ERG_A = 6.62607015e-27 * 2.99792458e10 * 1e8   # h*c in erg*Angstrom


def fetch(url, cache_dir="sn_template_cache"):
    import requests
    os.makedirs(cache_dir, exist_ok=True)
    name = "bessell_b_svo.dat" if "svo2" in url else url.split("/")[-1]
    path = os.path.join(cache_dir, name)
    if os.path.exists(path) and os.path.getsize(path) > 100:
        return open(path, "rb").read()
    print("fetching", url)
    r = requests.get(url, timeout=300)
    r.raise_for_status()
    open(path, "wb").write(r.content)
    return r.content


def load_template(raw):
    """phases (days), wavelengths (A), flux grid [phase, wave] (f_lambda, arbitrary unit)."""
    text = gzip.decompress(raw).decode("ascii") if raw[:2] == b"\x1f\x8b" else raw.decode("ascii")
    data = np.loadtxt(io.StringIO(text))
    phases = np.unique(data[:, 0])
    waves = np.unique(data[:, 1])
    flux = np.zeros((len(phases), len(waves)))
    pi = {p: i for i, p in enumerate(phases)}
    wi = {w: i for i, w in enumerate(waves)}
    for t, w, f in data:
        flux[pi[t], wi[w]] = f
    return phases, waves, flux


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--out", default="SupernovaTemplates.sntpl")
    args = p.parse_args()

    raw = fetch(SVO_BESSELL_B)
    rows = [l.split() for l in raw.decode("ascii", "replace").splitlines()
            if l.strip() and not l.lstrip().startswith("#")]
    arr = np.array([[float(r[0]), float(r[1])] for r in rows if len(r) >= 2])
    bw, bt = arr[:, 0], arr[:, 1]
    print("Bessell B: %d points, %.0f-%.0f A" % (len(bw), bw.min(), bw.max()))

    grid = np.arange(GRID_MIN_A, GRID_MAX_A + GRID_STEP_A / 2, GRID_STEP_A)

    out = io.BytesIO()
    out.write(MAGIC)
    out.write(struct.pack("<ii", VERSION, len(TEMPLATES)))
    src = ("Nugent spectral templates (c3.lbl.gov/nugent): Nugent, Kim & Perlmutter 2002 PASP "
           "114, 803 (Ia); Levan et al. 2005 (Ibc); Gilliland, Nugent & Phillips 1999 ApJ 521, "
           "30 (II); Di Carlo et al. 2002 (IIn). B anchor: Bessell B (SVO FPS), f0_B from "
           "Bessell, Castelli & Plez 1998.")
    sb = src.encode("utf-8")
    out.write(struct.pack("<i", len(sb)))
    out.write(sb)

    for name, filename in TEMPLATES:
        phases, waves, flux = load_template(fetch(NUGENT_BASE + filename))
        t = np.interp(waves, bw, bt, left=0.0, right=0.0)
        t_norm = np.trapz(t, waves)

        # Band-mean flux through Bessell B per phase, in the template's own unit.
        mean_b = np.array([np.trapz(flux[i] * t, waves) / t_norm for i in range(len(phases))])
        ok = mean_b > 0.0
        phases, flux, mean_b = phases[ok], flux[ok], mean_b[ok]

        bmag = -2.5 * np.log10(mean_b)
        peak = int(np.argmin(bmag))
        b_offset = bmag - bmag[peak]

        # vAnchor(phase) = V_mod(phase) - B(peak). With the template scaled so its B peak sits at
        # absolute magnitude M_B, the absolute flux unit is s = f0_B*10^(-0.4*M_B)/mean_b[peak];
        # the photon density the mod's V anchors on is s*f(5556)*lambda/hc, so M_B cancels:
        #   vAnchor = -2.5 log10( f0_B * (f5556/mean_b[peak]) * lambda/(hc) / 948 )
        f5556 = np.array([np.interp(JOHNSON_V_A, waves, flux[i]) for i in range(len(phases))])
        f5556 = np.maximum(f5556, 1e-300)
        photon_factor = VEGA_B_ZERO_ERG * JOHNSON_V_A / HC_ERG_A / MOD_V_ZERO_PHOTONS
        v_anchor = -2.5 * np.log10(f5556 / mean_b[peak] * photon_factor)

        shape = np.zeros((len(phases), len(grid)), dtype=np.float32)
        for i in range(len(phases)):
            photon = flux[i] * waves
            at_v = np.interp(JOHNSON_V_A, waves, photon)
            if at_v <= 0.0:
                at_v = max(photon.max(), 1e-300)
            shape[i] = np.interp(grid, waves, photon / at_v, left=0.0, right=0.0)

        nb = name.encode("ascii")
        out.write(struct.pack("<i", len(nb)))
        out.write(nb)
        out.write(struct.pack("<i", len(phases)))
        out.write(struct.pack("<%dd" % len(phases), *phases))
        out.write(struct.pack("<i", len(grid)))
        out.write(struct.pack("<dd", GRID_MIN_A, GRID_STEP_A))
        out.write(struct.pack("<%df" % len(phases), *b_offset.astype(np.float32)))
        out.write(struct.pack("<%df" % len(phases), *v_anchor.astype(np.float32)))
        out.write(shape.astype("<f2").tobytes())

        pk = phases[peak]
        print("%-4s %3d phases (day %.0f..%.0f, B peak day %.0f), rise %.2f mag, "
              "+30d decline %.2f mag, vAnchor(peak) %.3f"
              % (name, len(phases), phases[0], phases[-1], pk,
                 b_offset[0], float(np.interp(pk + 30, phases, b_offset)), v_anchor[peak]))

    data = out.getvalue()
    with open(args.out, "wb") as f:
        f.write(data)
    print("wrote %s (%.1f MB)" % (args.out, len(data) / 1e6))
    return 0


if __name__ == "__main__":
    sys.exit(main())
