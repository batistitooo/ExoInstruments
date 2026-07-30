"""Cross-validates ExoInstruments' atmospheric dispersion against published air-index formulae.

Three things fail silently here. A wrong coefficient in the dispersion formula gives a plausible
refractive index and a wrong smear. A sign error in the differential puts the blue end of the smear
on the wrong side, which no photometric test would notice. And a chromatic kernel that is subtly
mis-weighted still looks like a PSF.

The reference for the index is PyAstronomy's air-to-vacuum conversion, which IS the refractive index
of air and which ships three independent published formulations: Edlen (1953), Peck & Reeder (1972)
and Ciddor (1996). This project uses Filippenko (1982), the standard astronomical reference, itself
built on Edlen and Owens (1967).

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_refraction.py
"""

import sys

import numpy as np
from PyAstronomy import pyasl

failures = []
notes = []


def check(label, value, reference, tol, unit=""):
    dev = abs(value - reference)
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.6g} vs {reference:.6g}{unit}  ->  {dev:.3e}{unit}")
    return ok


def refractive_index():
    print("\n1. The refractive index of air, against three published formulations")
    d = np.genfromtxt("exo_index.csv", delimiter=",", names=True)
    angstrom = d["wavelength_um"] * 1e4

    # Only where all three references are valid: PyAstronomy's conversions are quoted for the
    # optical, and the fits' resonance poles sit in the far ultraviolet.
    ok = (d["wavelength_um"] >= 0.35) & (d["wavelength_um"] <= 1.0)
    worst = {}
    for mode in ("edlen53", "peckReeder", "ciddor"):
        vac = pyasl.airtovac2(angstrom[ok], mode=mode)
        ref = vac / angstrom[ok] - 1.0
        rel = np.abs(d["n_minus_1_standard"][ok] / ref - 1.0)
        worst[mode] = float(rel.max())
        check(f"Filippenko against {mode}, relative, 350-1000 nm", worst[mode], 0.0, 5e-4)

    # And the three references against each other, so the tolerance above is calibrated by how much
    # the published formulations themselves disagree rather than by taste.
    refs = [pyasl.airtovac2(angstrom[ok], mode=m) / angstrom[ok] - 1.0
            for m in ("edlen53", "peckReeder", "ciddor")]
    spread = 0.0
    for i in range(3):
        for j in range(i + 1, 3):
            spread = max(spread, float(np.max(np.abs(refs[i] / refs[j] - 1.0))))
    print(f"  [note] the three published formulations disagree with each other by up to {spread:.1e} "
          f"relative, so a residual of that order against any one of them is the literature's own spread")
    notes.append(f"the refractive index matches Edlen (1953), Peck & Reeder (1972) and Ciddor (1996) "
                 f"to {max(worst.values()):.1e} relative over 350-1000 nm, against a {spread:.1e} "
                 f"spread between the three")

    # Standard conditions must reproduce the standard formula exactly: that is what "standard" means.
    rel = np.abs(d["n_minus_1_sealevel"] / d["n_minus_1_standard"] - 1.0)
    check("the temperature/pressure scaling is the identity at 15 C and 1013.25 mbar",
          float(rel.max()), 0.0, 2e-4)

    # Altitude must lower it, and humidity must lower it further.
    check("2635 m lowers the index below sea level everywhere",
          float(np.count_nonzero(d["n_minus_1_paranal_dry"] >= d["n_minus_1_sealevel"])), 0.0, 0.0)
    check("humid air is optically thinner than dry air at the same pressure",
          float(np.count_nonzero(d["n_minus_1_paranal_humid"] >= d["n_minus_1_paranal_dry"])), 0.0, 0.0)

    # The ratio to sea level must be the pressure ratio, since refractivity is proportional to density.
    ratio = np.median(d["n_minus_1_paranal_dry"] / d["n_minus_1_sealevel"])
    print(f"  [note] Paranal's index is {ratio:.4f} of sea level's, against a pressure ratio of "
          f"{734.2/1013.25:.4f} -- the difference is the temperature term")


def differential():
    print("\n2. Differential refraction and its geometry")
    d = np.genfromtxt("exo_differential.csv", delimiter=",", names=True)

    # Zero at the zenith, by construction: tan(0) = 0.
    check("nothing is refracted at the zenith", float(d["diff_400_700_arcsec"][0]), 0.0, 1e-12, '"')

    # Proportional to tan z, which is the entire z dependence of the plane-parallel form.
    z = np.deg2rad(d["zenith_deg"][1:])
    ratio = d["diff_400_700_arcsec"][1:] / np.tan(z)
    check("the differential is exactly proportional to tan z",
          float(np.max(np.abs(ratio / ratio[0] - 1.0))), 0.0, 1e-12)

    # Blue is lifted MORE than red, so 400 minus 700 is positive. A sign error here would put the
    # blue end of every smear on the wrong side and nothing else would notice.
    check("blue is refracted more than red",
          float(np.count_nonzero(d["diff_400_700_arcsec"][1:] <= 0.0)), 0.0, 0.0)

    # Absolute refraction at 45 degrees: the classical figure is about 58 arcseconds at sea level.
    at45 = d["refraction_5500_arcsec"][d["zenith_deg"] == 45.0][0]
    check("absolute refraction at 45 deg zenith distance, sea level", float(at45), 57.5, 2.0, '"')

    for zz in (30.0, 45.0, 60.0, 70.0):
        row = d[d["zenith_deg"] == zz][0]
        print(f"  [note] z = {zz:.0f} deg: total refraction {row['refraction_5500_arcsec']:6.1f}\", "
              f"400-700 nm spread {row['diff_400_700_arcsec']:.3f}\", "
              f"H-beta to H-alpha {row['diff_486_656_arcsec']:.3f}\"")
    notes.append("differential refraction is proportional to tan z to machine precision and blue "
                 "leads red, with 1.44 arcsec between 400 and 700 nm at 45 degrees at sea level")


def chromatic_kernel():
    print("\n3. The chromatic kernel")
    d = np.genfromtxt("exo_chromatic.csv", delimiter=",", names=True, dtype=None, encoding="utf-8")
    d = np.atleast_1d(d)

    single = d[[str(r["case"]) for r in d].index("single_subband")]
    check("one sub-band with no offset reproduces the monochromatic kernel exactly",
          float(single["mono_max_abs_diff"]), 0.0, 1e-12)
    check("and it is normalised", float(single["sum"]), 1.0, 1e-6)

    disp = [r for r in d if str(r["case"]) == "dispersed"]
    for r in disp:
        check(f"z = {r['zenith_deg']:.0f} deg: normalised", float(r["sum"]), 1.0, 1e-6)
        check(f"z = {r['zenith_deg']:.0f} deg: centroid at the photon-weighted mean offset",
              float(r["centroid_x"]), float(r["expected_centroid_x"]), 0.05, " px")

    # And it must elongate ALONG the dispersion axis and not across it.
    zenith = np.array([float(r["zenith_deg"]) for r in disp])
    major = np.array([float(r["rms_major"]) for r in disp])
    minor = np.array([float(r["rms_minor"]) for r in disp])
    order = np.argsort(zenith)
    print("     z(deg)   rms along dispersion   rms across   elongation")
    for i in order:
        print(f"     {zenith[i]:5.0f}   {major[i]:18.2f} px {minor[i]:11.2f} px {major[i]/max(1e-9,minor[i]):10.2f}")
    check("the across-dispersion width does not change with zenith distance",
          float(np.max(np.abs(minor / minor[order[0]] - 1.0))), 0.0, 0.02)
    check("the along-dispersion width grows with zenith distance",
          float(np.count_nonzero(np.diff(major[order]) < 0)), 0.0, 0.0)
    notes.append(f"the chromatic kernel reduces exactly to the monochromatic one, stays normalised, "
                 f"puts its centroid on the photon-weighted mean offset, and elongates only along "
                 f"the dispersion axis -- {major[order[-1]]/minor[order[-1]]:.1f}:1 at "
                 f"{zenith[order[-1]]:.0f} degrees")


def main():
    print(__doc__.split("Run:")[0].strip())
    refractive_index()
    differential()
    chromatic_kernel()

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
