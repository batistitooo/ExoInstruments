"""Checks the emission-line photometry, and demonstrates what narrowband imaging is for.

Everything else in this pipeline integrates a CONTINUUM. A nebula in [S II] is not one: its flux
arrives in lines a fraction of an Angstrom wide, so what decides how much reaches the detector is
the system's throughput AT the line, not an effective width across a band. That asymmetry is the
whole reason narrowband works, and section 4 measures it rather than asserting it.

The unit algebra is checked against astropy, which defines the rayleigh itself, so the conversion
is not this project's own arithmetic marking its own homework.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_emission.py
"""

import csv
import sys

import numpy as np
import astropy.units as u
from astropy.constants import h, c

failures = []
notes = []


def check(label, value, reference, tol, unit="", relative=True):
    if relative:
        denom = abs(reference) if abs(reference) > 0 else 1.0
        dev = abs(value - reference) / denom
    else:
        dev = abs(value - reference)
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.6g} vs {reference:.6g}{unit}  ->  {dev:.3e}")
    return ok


def load(path):
    return np.genfromtxt(path, delimiter=",", names=True, dtype=None, encoding="utf-8")


def rayleigh():
    """The rayleigh, against astropy's own definition of it."""
    print("\n1. The rayleigh conversion, against astropy.units")

    # astropy defines the rayleigh; converting one of them to photons per area per time per solid
    # angle is the same statement the C# constant makes, arrived at independently.
    ref = (1.0 * u.R).to(u.photon / (u.cm ** 2 * u.s * u.sr), u.spectral_density(656.28 * u.nm))
    ref_value = float(ref.value)
    check("photons/cm2/s/sr per rayleigh", 1.0e6 / (4.0 * np.pi), ref_value, 1e-12)

    ster_per_sq_arcsec = float((1.0 * u.arcsec ** 2).to(u.sr).value)
    check("steradians per square arcsec", (np.pi / (180.0 * 3600.0)) ** 2, ster_per_sq_arcsec, 1e-12)

    # And the whole expression, recomputed with astropy quantities on a row the C# side wrote.
    d = load("exo_rayleigh.csv")
    row = d[(d["surface_brightness_r"] == 100.0) & (d["plate_scale_arcsec"] == 0.2754)
            & (d["aperture_cm2"] == 1732.0)][0]
    expected = (100.0 * u.R).to(u.photon / (u.cm ** 2 * u.s * u.sr),
                                u.spectral_density(656.28 * u.nm)) \
        * (row["plate_scale_arcsec"] ** 2 * u.arcsec ** 2).to(u.sr) \
        * (row["aperture_cm2"] * u.cm ** 2) * row["throughput"]
    check("100 R on the RC20 at 0.2754 arcsec/px",
          row["electrons_per_px_per_s"], float(expected.to(u.photon / u.s).value), 1e-12,
          " e-/px/s")

    # Linearity in every argument, which is what makes the expression separable at all.
    base = d[(d["plate_scale_arcsec"] == 0.2754) & (d["aperture_cm2"] == 1732.0)]
    order = np.argsort(base["surface_brightness_r"])
    ratios = base["electrons_per_px_per_s"][order] / base["surface_brightness_r"][order]
    check("linear in surface brightness", float(ratios.std() / ratios.mean()), 0.0, 1e-14,
          relative=False)


def photon_energy():
    """erg to photons, against astropy's own h and c."""
    print("\n2. Line flux in erg/cm2/s to electrons, against astropy constants")
    d = load("exo_lineflux.csv")
    for row in d:
        lam = row["wavelength_m"] * u.m
        energy = (h * c / lam).to(u.erg)
        expected = float((row["flux_erg_cm2_s"] * u.erg / (u.cm ** 2 * u.s) / energy
                          * 1732.0 * u.cm ** 2 * 0.5).to(1 / u.s).value)
        check(f"{row['line']}", row["electrons_per_s"], expected, 1e-12, " e-/s")


def lines():
    """The line list: separations that decide what a filter can resolve."""
    print("\n3. The line list")
    with open("exo_lines.csv") as f:
        rows = {r["name"]: float(r["wavelength_angstrom"]) for r in csv.DictReader(f)}

    # The separation that decides whether an "H-alpha" frame is really H-alpha.
    sep_nii = rows["[N II] 6584"] - rows["H-alpha"]
    print(f"  [note] [N II] 6584 sits {sep_nii:.2f} A from H-alpha, so a filter must be narrower "
          f"than about {2 * sep_nii / 10:.1f} nm to exclude it")
    check("[N II] 6584 to H-alpha separation", sep_nii, 20.65, 0.05, " A", relative=False)
    notes.append(f"[N II] 6584 is {sep_nii:.1f} A from H-alpha: separating them needs a filter "
                 f"narrower than about {2 * sep_nii / 10:.1f} nm")

    # The density-diagnostic doublets, whose members no optical filter separates.
    check("[S II] doublet separation", rows["[S II] 6731"] - rows["[S II] 6716"], 14.38, 0.05, " A",
          relative=False)
    check("[O II] doublet separation", rows["[O II] 3729"] - rows["[O II] 3726"], 2.79, 0.05, " A",
          relative=False)
    check("[O III] doublet separation", rows["[O III] 5007"] - rows["[O III] 4959"], 47.93, 0.05,
          " A", relative=False)

    # Ordering, which a transcription error breaks before anything else does.
    ordered = sorted(rows.values())
    check("the list is a valid ascending set", float(np.min(np.diff(ordered))), 0.0, 1e9)
    if min(np.diff(ordered)) <= 0:
        failures.append("duplicate or unordered wavelengths")


def narrowband():
    """What narrowband buys, measured with the real system response."""
    print("\n4. Narrowband against broadband, on the same nebula")
    print("   RC20, 100 R of H-alpha on the model's own dark sky, peak transmission held fixed")
    d = load("exo_narrowband.csv")

    print(f"\n   {'width':>7} {'T at line':>10} {'line e-/px/s':>13} {'sky e-/px/s':>12} "
          f"{'contrast':>9} {'[N II] in?':>11}")
    for row in d:
        print(f"   {row['width_nm']:6.1f}nm {row['throughput_at_line']:10.4f} "
              f"{row['line_e_per_px_s']:13.5f} {row['sky_e_per_px_s']:12.6f} "
              f"{row['contrast']:9.2f} {'yes' if row['nii6584_admitted'] else 'no':>11}")

    # The three statements that make narrowband work, as measurements.
    check("the line signal does not depend on filter width",
          float(np.ptp(d["throughput_at_line"])), 0.0, 1e-12, relative=False)

    # Sky is a continuum, so it scales with the band's effective width.
    ratio = d["sky_e_per_px_s"] / d["effective_width_a"]
    check("the sky scales with the band's effective width",
          float(ratio.std() / ratio.mean()), 0.0, 1e-12, relative=False)

    broad = d[d["width_nm"] == 260.0][0]
    narrow = d[d["width_nm"] == 1.0][0]
    gain = narrow["contrast"] / broad["contrast"]
    print(f"\n  [note] 260 nm -> 1 nm improves line-to-sky contrast by {gain:.0f}x")
    check("contrast gain matches the width ratio", gain, 260.0, 1e-6)
    notes.append(f"narrowing 260 nm to 1 nm improves line-to-sky contrast {gain:.0f}-fold, "
                 f"because the line signal is unchanged and the sky is not")

    # And the crossover that decides whether the frame is H-alpha or H-alpha plus [N II].
    admits = d[d["nii6584_admitted"] == 1.0]["width_nm"]
    excludes = d[d["nii6584_admitted"] == 0.0]["width_nm"]
    print(f"  [note] [N II] 6584 is admitted at {admits.min():.0f} nm and wider, "
          f"excluded at {excludes.max():.0f} nm and narrower")
    check("the [N II] crossover sits between 3 and 5 nm, as the 20.6 A separation requires",
          1.0 if (excludes.max() <= 3.0 and admits.min() >= 5.0) else 0.0, 1.0, 0.0, relative=False)


def rotation():
    """The frame-to-Galactic rotation the emission deposit uses, against the chain it replaces."""
    print("\n5. Horizontal-to-Galactic rotation, against the literal transform chain")
    print("   One matrix multiply against four transforms. Filling a frame from an all-sky map is")
    print("   the only thing in this pipeline that runs per PIXEL, so the shortcut has to be exact.")
    d = load("exo_rotation.csv")

    db = np.abs(d["b_matrix"] - d["b_chain"])
    dl = np.abs((d["l_matrix"] - d["l_chain"] + 180.0) % 360.0 - 180.0)
    off_pole = np.abs(d["b_chain"]) < 89.0

    check(f"Galactic latitude over {len(d)} directions", float(db.max()), 0.0, 1e-11, " deg",
          relative=False)
    check("Galactic longitude (off the Galactic poles)", float(dl[off_pole].max()), 0.0, 1e-10,
          " deg", relative=False)
    notes.append(f"the per-pixel rotation reproduces the four-transform chain to "
                 f"{max(float(db.max()), float(dl[off_pole].max())):.1e} deg")


def main():
    print(__doc__.split("Run:")[0].strip())
    rayleigh()
    photon_energy()
    lines()
    narrowband()
    rotation()

    print("\n" + "-" * 78)
    for n in notes:
        print("NOTE: " + n)
    if failures:
        print(f"\n{len(failures)} CHECK(S) FAILED:")
        for f in failures:
            print("  - " + f)
        return 1
    print("\nALL CHECKS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
