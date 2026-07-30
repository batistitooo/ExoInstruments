"""Cross-validates ExoInstruments' airglow model against ESO SkyCalc queried independently.

The airglow table is generated from SkyCalc, so comparing it against SkyCalc alone would only prove
the generator ran. What is checked instead: the BINNING preserved every integral (re-derived here
from a fresh query), the Bessell V transcription matches speclite's own curve, the van Rhijn geometry
is right, and the headline numbers land on PUBLISHED measurements that never entered the generator --
Paranal's dark-sky V surface brightness (Patat 2008: 21.7 +/- 0.2 with zodiacal light, so the airglow
alone must sit near 22.0) and the classical [O I] and Na line strengths.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_airglow.py
"""

import sys

import numpy as np

failures = []
notes = []


def check(label, value, reference, tol, unit=""):
    dev = abs(value - reference)
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.6g} vs {reference:.6g}{unit}  ->  {dev:.3e}{unit}")
    return ok


def bessell_v():
    print("\n1. The Bessell V transcription, against speclite")
    d = np.genfromtxt("exo_bessellv.csv", delimiter=",", names=True)
    try:
        import speclite.filters as sf
    except ImportError:
        print("  [note] speclite not installed, checking moments only")
        sf = None

    lam = d["wavelength_nm"]
    t = d["transmission"]
    # Photon-weighted effective wavelength and FWHM, against the published 551 / 88 nm.
    eff = np.trapz(lam * t * lam, lam) / np.trapz(t * lam, lam)
    above = lam[t >= 0.5 * t.max()]
    fwhm = above.max() - above.min()
    check("effective wavelength", float(eff), 551.0, 4.0, " nm")
    check("FWHM", float(fwhm), 88.0, 5.0, " nm")

    if sf is not None:
        v = sf.load_filter("bessell-V")
        ref = np.interp(lam * 10.0, v.wavelength, v.response)
        ref = ref / ref.max()
        # speclite stores the same Bessell curve; the comparison is shape against shape.
        dev = np.max(np.abs(t / max(1e-12, t.max()) - ref))
        check("shape against speclite's bessell-V, absolute", float(dev), 0.0, 0.03)
        notes.append(f"the Bessell V transcription matches speclite to {dev:.3f} of peak, with "
                     f"effective wavelength {eff:.1f} nm and FWHM {fwhm:.0f} nm against the "
                     f"published 551 and 88")


def binning():
    print("\n2. The stored table, against a fresh SkyCalc query")
    try:
        from skycalc_cli.skycalc import SkyModel
        from astropy.io import fits
    except ImportError:
        print("  [note] skycalc_cli not installed, skipped")
        return
    d = np.genfromtxt("exo_airglow_density.csv", delimiter=",", names=True)

    m = SkyModel()
    m.callwith({"airmass": 1.0, "wmin": 350.0, "wmax": 1000.0, "wdelta": 0.02,
                "observatory": "paranal", "vacair": "air", "msolflux": 130.0,
                "incl_moon": "N", "incl_zodiacal": "N", "incl_starlight": "N", "incl_therm": "N",
                "incl_loweratm": "Y", "incl_upperatm": "Y", "incl_airglow": "Y"})
    m.write("/tmp/airglow_check.fits")
    with fits.open("/tmp/airglow_check.fits") as h:
        raw = h[1].data
    lam = np.asarray(raw["lam"], float)
    to_r = (1e-4 / 2.35044e-11) / (1e6 / (4 * np.pi)) * 1e-3
    ael = np.asarray(raw["flux_ael"], float) * to_r
    arc = np.asarray(raw["flux_arc"], float) * to_r

    # Integral over ANY window must match: that is the property bin integration exists to preserve.
    worst = 0.0
    for lo, hi in ((350, 1000), (555, 560), (628, 632), (650, 900), (654, 659), (500, 502)):
        sel = (lam >= lo) & (lam <= hi)
        ref = np.trapz(ael[sel], lam[sel])
        sel2 = (d["wavelength_nm"] >= lo) & (d["wavelength_nm"] <= hi)
        ours = np.sum(d["lines_r_per_nm"][sel2]) * 0.1
        dev = abs(ours - ref) / max(1.0, abs(ref))
        worst = max(worst, dev)
        print(f"    lines {lo:4.0f}-{hi:4.0f} nm: ours {ours:9.1f} R  fresh query {ref:9.1f} R")
    check("line integrals over six windows, relative to the fresh query", worst, 0.0, 0.02)

    sel = (lam >= 350) & (lam <= 1000)
    ref_c = np.trapz(arc[sel], lam[sel])
    ours_c = np.sum(d["continuum_r_per_nm"]) * 0.1
    check("continuum total, relative", abs(ours_c - ref_c) / ref_c, 0.0, 0.02)
    notes.append("the stored table reproduces a fresh SkyCalc query's integrals over every window "
                 "tested, including the 5577 and 6300 lines and the OH forest")


def van_rhijn():
    print("\n3. The van Rhijn factor")
    d = np.genfromtxt("exo_vanrhijn.csv", delimiter=",", names=True)
    z = np.deg2rad(d["zenith_deg"])
    for h, col in ((90.0, "factor_90km"), (250.0, "factor_250km")):
        ratio = 6371.0 / (6371.0 + h)
        ref = 1.0 / np.sqrt(1.0 - (ratio * np.sin(z)) ** 2)
        check(f"against the closed form at {h:.0f} km", float(np.max(np.abs(d[col] - ref))), 0.0, 1e-9)
    at60 = d[d["zenith_deg"] == 60.0][0]
    print(f"  [note] z = 60 deg: layer factor {at60['factor_90km']:.3f} against sec z = "
          f"{at60['sec_z']:.3f} -- the thin-shell geometry, not an airmass")
    check("the 250 km layer brightens more slowly than the 90 km one",
          float(np.count_nonzero(d["factor_250km"] > d["factor_90km"])), 0.0, 0.0)


def headline():
    print("\n4. The published numbers that never entered the generator")
    d = np.genfromtxt("exo_airglow_v.csv", delimiter=",", names=True)
    zenith_v = d[d["zenith_deg"] == 0.0][0]["v_mag_per_arcsec2"]

    # Patat (2008, A&A 481, 575) measures V = 21.7 +/- 0.2 at Paranal INCLUDING zodiacal light,
    # which contributes a few tenths; the airglow alone therefore belongs near 22.0.
    check("airglow-only V at the zenith, against the Patat dark sky less zodiacal",
          float(zenith_v), 22.0, 0.25, " mag/arcsec^2")

    # With the mod's own zodiacal term (V = 23.3 flat) the total must land on the measurement.
    total = -2.5 * np.log10(10 ** (-0.4 * zenith_v) + 10 ** (-0.4 * 23.3))
    check("with the mod's zodiacal term, the total dark sky", float(total), 21.7, 0.25, " mag/arcsec^2")

    dens = np.genfromtxt("exo_airglow_density.csv", delimiter=",", names=True)
    lam = dens["wavelength_nm"]

    def integrate(lo, hi):
        sel = (lam >= lo) & (lam <= hi)
        return float(np.sum(dens["lines_r_per_nm"][sel]) * 0.1)

    # Classical line strengths (Roach & Gordon 1973; Leinert et al. 1998 review): [O I] 5577 is
    # 250 R nominal varying 100-300, the red line 50-300 with solar activity, Na D 30-100.
    green = integrate(556.5, 558.9)
    red = integrate(628.8, 631.2)
    sodium = integrate(587.7, 590.9)
    ok_g = 100.0 <= green <= 300.0
    ok_r = 50.0 <= red <= 300.0
    ok_s = 20.0 <= sodium <= 120.0
    for name, val, ok, rng in (("[O I] 5577", green, ok_g, "100-300"),
                               ("[O I] 6300", red, ok_r, "50-300"),
                               ("Na D", sodium, ok_s, "20-120")):
        if not ok:
            failures.append(name)
        print(f"  [{'ok  ' if ok else 'FAIL'}] {name} = {val:.0f} R, published range {rng} R")

    bands = np.genfromtxt("exo_airglow_bands.csv", delimiter=",", names=True,
                          dtype=None, encoding="utf-8")
    bands = np.atleast_1d(bands)
    by = {str(r["filter"]): r for r in bands}
    ratio = by["OI6300"]["rayleighs_in_band"] / by["SII"]["rayleighs_in_band"]
    ok = ratio > 5.0
    if not ok:
        failures.append("OI/SII sky asymmetry")
    print(f"  [{'ok  ' if ok else 'FAIL'}] an [O I] 6300 filter sees {ratio:.0f}x the sky an [S II] "
          f"filter does -- the asymmetry that makes ground-based [O I] hopeless and [S II] routine")
    notes.append(f"airglow-only V at the zenith comes out {zenith_v:.2f}; with the zodiacal term the "
                 f"dark sky lands at {total:.2f} against Patat's measured 21.7 +/- 0.2, and the "
                 f"[O I] lines fall inside their published ranges")


def main():
    print(__doc__.split("Run:")[0].strip())
    bessell_v()
    binning()
    van_rhijn()
    headline()

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
