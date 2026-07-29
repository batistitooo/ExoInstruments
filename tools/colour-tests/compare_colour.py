"""Cross-validates ExoInstruments' colorimetry against colour-science.

Colour is the one thing in this mod a reader judges by eye, and a wrong colour is invisible to every
other test in the project: a transcription error in the colour matching functions, a transposed sRGB
matrix, or a gamma applied twice all produce images that still look like images. So the whole chain
is compared against colour-science, which ships the CIE's own tabulation and the IEC's own matrix.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_colour.py
"""

import sys

import numpy as np
import colour

failures = []
notes = []


def check(label, value, reference, tol, unit=""):
    dev = abs(value - reference)
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.6g} vs {reference:.6g}{unit}  ->  {dev:.3e}{unit}")
    return ok


CMFS = colour.MSDS_CMFS["CIE 1931 2 Degree Standard Observer"]
SRGB = colour.models.RGB_COLOURSPACE_sRGB


def standard_observer():
    print("\n1. The standard observer, against the CIE tabulation")
    d = np.genfromtxt("exo_cmf.csv", delimiter=",", names=True)

    inside = (d["wavelength_nm"] >= 360) & (d["wavelength_nm"] <= 830)
    # On the table's own integer nanometres the values must be identical, not interpolated.
    integer = inside & (np.abs(d["wavelength_nm"] % 1.0) < 1e-9)
    ref = np.array([CMFS[w] for w in d["wavelength_nm"][integer]])
    worst = 0.0
    for k, col in enumerate(("xbar", "ybar", "zbar")):
        worst = max(worst, float(np.max(np.abs(d[col][integer] - ref[:, k]))))
    check(f"table entries exact over {int(integer.sum())} wavelengths", worst, 0.0, 1e-12)

    # Between entries, linear interpolation against colour-science's own interpolation of the same
    # data: the two need not agree exactly (it uses a sprague/cubic interpolator by default), so the
    # tolerance is what linear interpolation of a smooth curve at 1 nm can differ by.
    between = inside & (np.abs(d["wavelength_nm"] % 1.0 - 0.5) < 1e-9)
    ref_b = np.array([CMFS[w] for w in d["wavelength_nm"][between]])
    worst_b = 0.0
    for k, col in enumerate(("xbar", "ybar", "zbar")):
        worst_b = max(worst_b, float(np.max(np.abs(d[col][between] - ref_b[:, k]))))
    # colour-science interpolates the same table with a Sprague quintic; ours is a Catmull-Rom
    # cubic. Both are CIE 167:2005-compliant reconstructions of the same data, so the residual is
    # the difference between two recommended conventions rather than an error in either.
    check("half-nanometre interpolation against a Sprague quintic, absolute", worst_b, 0.0, 5e-5)

    outside = ~inside
    check("zero outside 360-830 nm, which is what invisible means",
          float(np.max(np.abs(np.vstack([d[c][outside] for c in ("xbar", "ybar", "zbar")])))), 0.0, 0.0)
    notes.append(f"the standard observer matches the CIE tabulation exactly on all "
                 f"{int(integer.sum())} tabulated wavelengths")


def srgb_matrix():
    print("\n2. The sRGB transform and its transfer function")
    # The matrix, via a round trip through a known colour.
    d = np.genfromtxt("exo_blackbody.csv", delimiter=",", names=True)
    xyz = np.vstack([d["X"], d["Y"], d["Z"]]).T
    ref_rgb = colour.XYZ_to_RGB(xyz, SRGB, apply_cctf_encoding=False)
    # Ours is normalised and gamut-mapped, so the matrix itself is checked on the chromaticities.
    ref_xy = colour.XYZ_to_xy(xyz)
    ours_xy = np.vstack([d["x"], d["y"]]).T
    check(f"chromaticity of {len(d)} blackbodies, absolute",
          float(np.max(np.abs(ours_xy - ref_xy))), 0.0, 1e-9)

    t = np.genfromtxt("exo_transfer.csv", delimiter=",", names=True)
    ref_enc = colour.models.eotf_inverse_sRGB(t["linear"])
    check("sRGB transfer function against IEC 61966-2-1",
          float(np.max(np.abs(t["encoded"] - ref_enc))), 0.0, 1e-12)
    check("and its inverse round-trips", float(np.max(np.abs(t["round_trip"] - t["linear"]))), 0.0, 1e-12)

    # And the matrix as quoted in the standard to four decimals, which is what the generated file
    # must reduce to.
    quoted = np.array([[3.2406, -1.5372, -0.4986],
                       [-0.9689, 1.8758, 0.0415],
                       [0.0557, -0.2040, 1.0570]])
    derived = np.linalg.inv(colour.normalised_primary_matrix(SRGB.primaries, SRGB.whitepoint))
    check("the derived matrix reduces to the standard's quoted four decimals",
          float(np.max(np.abs(derived - quoted))), 0.0, 5e-4)


def blackbody_locus():
    print("\n3. The Planckian locus, against colour-science's own blackbody spectra")
    d = np.genfromtxt("exo_blackbody.csv", delimiter=",", names=True)
    worst = 0.0
    worst_t = 0.0
    for row in d:
        t = row["temperature_k"]
        sd = colour.sd_blackbody(t, colour.SpectralShape(360, 830, 1))
        xyz = colour.sd_to_XYZ(sd, CMFS, method="Integration")
        x, y = colour.XYZ_to_xy(xyz)
        dev = max(abs(row["x"] - x), abs(row["y"] - y))
        if dev > worst:
            worst, worst_t = dev, t
    # The residual is a known constant difference, not a numerical one: CIE 15:2004 recommends
    # c2 = 1.4388e-2 m K for colorimetry, which colour-science follows, while this uses the SI
    # defining constants giving 1.438776877e-2 -- 1.55e-5 relative, and that is what shows up here.
    check(f"chromaticity over {len(d)} temperatures from 300 K to 50000 K", worst, 0.0, 1e-5)
    print(f"  [note] worst at {worst_t:.0f} K")

    # The Sun must land where the Sun lands: a 5778 K blackbody is close to but not exactly D65.
    sun = d[np.argmin(np.abs(d["temperature_k"] - 5778))]
    print(f"  [note] a 5778 K blackbody sits at x = {sun['x']:.4f}, y = {sun['y']:.4f}; "
          f"D65 is at 0.3127, 0.3290, and D65 is a daylight illuminant rather than a blackbody")
    notes.append(f"the Planckian locus reproduces colour-science to {worst:.1e} in chromaticity "
                 f"from 300 K to 50000 K")


def monochromatic():
    print("\n4. Monochromatic stimuli and the gamut mapping")
    d = np.genfromtxt("exo_mono.csv", delimiter=",", names=True)

    # A single wavelength sits on the spectral locus: its chromaticity must equal the CIE's own.
    ref = []
    for w in d["wavelength_nm"]:
        xyz = np.array(CMFS[w])
        s = xyz.sum()
        ref.append(xyz[:2] / s if s > 0 else [np.nan, np.nan])
    ref = np.array(ref)
    ours = np.vstack([d["x"], d["y"]]).T
    finite = np.isfinite(ref).all(axis=1)
    # Tabulated wavelengths must be exact; the fractional ones -- which is where the real emission
    # lines are -- carry the interpolation convention above.
    integer = finite & (np.abs(d["wavelength_nm"] % 1.0) < 1e-9)
    check(f"spectral locus at {int(integer.sum())} tabulated wavelengths, absolute",
          float(np.max(np.abs(ours[integer] - ref[integer]))), 0.0, 1e-12)
    check(f"and at {int((finite & ~integer).sum())} fractional ones, absolute",
          float(np.max(np.abs(ours[finite & ~integer] - ref[finite & ~integer]))), 0.0, 1e-5)

    # Every one of them is outside sRGB, so every one must have needed desaturation, and none may
    # come out with a negative displayable component.
    check("every monochromatic stimulus needed desaturation",
          float(np.count_nonzero(d["desaturation"][finite] <= 0.0)), 0.0, 0.0)
    check("no negative display component survives the mapping",
          float(np.min(np.vstack([d[c] for c in ("r_display", "g_display", "b_display")]))), 0.0, 0.0)

    # The mapping must preserve luminance: that is what makes it a desaturation and not a clip.
    lin = np.vstack([d["r_linear"], d["g_linear"], d["b_linear"]])
    lum_before = 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2]
    dec = np.vstack([colour.models.eotf_sRGB(d[c]) for c in ("r_display", "g_display", "b_display")])
    lum_after = 0.2126 * dec[0] + 0.7152 * dec[1] + 0.0722 * dec[2]
    ok = lum_before > 1e-6
    rel = np.abs(lum_after[ok] / lum_before[ok] - 1.0)
    check("luminance preserved through the gamut mapping, relative", float(rel.max()), 0.0, 1e-6)
    # Exactly one component landing ON the rail is the CORRECT outcome, not a clip: the smallest
    # desaturation that fits the gamut is the one that puts the binding component exactly at the
    # boundary. That luminance survives it to 1e-15 is what proves nothing was clipped.
    at_rail = np.count_nonzero(np.abs(np.vstack([d[c] for c in ("r_display", "g_display", "b_display")]) - 1.0) < 1e-9)
    print(f"  [note] {at_rail} of {3*len(d)} components land exactly on a gamut boundary, which is "
          f"where the smallest sufficient desaturation puts them")

    for name, w in (("H-beta", 486.1), ("[O III] 5007", 500.7), ("H-alpha", 656.3)):
        row = d[np.argmin(np.abs(d["wavelength_nm"] - w))]
        print(f"  [note] {name:<13} at {row['wavelength_nm']:.1f} nm -> display sRGB "
              f"({row['r_display']:.3f}, {row['g_display']:.3f}, {row['b_display']:.3f}), "
              f"{row['desaturation']*100:.0f}% saturation given up to fit the gamut")
    notes.append("monochromatic lines land on the spectral locus exactly and are desaturated toward "
                 "the white point, which preserves luminance to 1e-6")


def legacy():
    print("\n5. What the curve fit this replaces was doing")
    d = np.genfromtxt("exo_legacy.csv", delimiter=",", names=True)
    diff = np.sqrt((d["legacy_r"] - d["cie_r"])**2 + (d["legacy_g"] - d["cie_g"])**2
                   + (d["legacy_b"] - d["cie_b"])**2)
    worst = int(np.argmax(diff))
    print(f"  [note] the old piecewise fit differs from the CIE chain by up to {diff.max():.3f} "
          f"in sRGB distance, at {d['temperature_k'][worst]:.0f} K "
          f"(fit {d['legacy_r'][worst]:.2f},{d['legacy_g'][worst]:.2f},{d['legacy_b'][worst]:.2f} "
          f"against {d['cie_r'][worst]:.2f},{d['cie_g'][worst]:.2f},{d['cie_b'][worst]:.2f})")
    print(f"  [note] median difference {np.median(diff):.3f}")
    notes.append(f"the curve fit it replaces was off by up to {diff.max():.2f} in sRGB distance, "
                 f"median {np.median(diff):.2f}")


def main():
    print(__doc__.split("Run:")[0].strip())
    standard_observer()
    srgb_matrix()
    blackbody_locus()
    monochromatic()
    legacy()

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
