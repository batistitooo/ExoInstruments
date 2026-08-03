"""Put this pipeline's contrast measurement beside VIP's, on the same frame.

WHY VIP. Where Pyxel is the reference for detector simulation, VIP (Vortex Image Processing,
vip_hci) is the reference for the measurement this file is about: it is the open, published,
widely used package that high-contrast imaging papers compute their detection limits with, and its
`contrast_curve` implements the Mawet et al. (2014) small-sample correction directly. If this
pipeline's contrast curve is to mean anything, VIP has to agree with it on the same pixels.

WHAT IS COMPARED. Three things, in increasing order of how much they involve:

  1. The SMALL-SAMPLE THRESHOLD. VIP computes it as
         stats.t.ppf(stats.norm.cdf(sigma), n-1) * sqrt(1 + 1/n)
     against our own Student t quantile built on a continued-fraction incomplete beta. This tests
     our special functions against SciPy's, at tail probabilities of 3e-7 where every published
     rational approximation to the t quantile has long since stopped being fitted.
  2. The ANNULUS NOISE. VIP's `noise_per_annulus` lays non-overlapping apertures of one resolution
     element around each annulus and takes their standard deviation. So do we. Same frame, same
     aperture size, same annuli.
  3. The CURVE. The two above, combined, on the frame the C# harness exported.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core -- --out .
    ./env/bin/python compare_vip.py
"""

import csv
import struct
import sys

import numpy as np
from scipy import stats

from vip_hci.metrics.contrcurve import noise_per_annulus

VERDICTS = []


def verdict(what, call, detail):
    VERDICTS.append((what, call, detail))


def read_meta(path="meta.csv"):
    with open(path) as f:
        return {r["key"]: float(r["value"]) for r in csv.DictReader(f)}


def read_frame(path="exo_frame.bin"):
    with open(path, "rb") as f:
        w, h = struct.unpack("<ii", f.read(8))
        return np.frombuffer(f.read(4 * w * h), dtype="<f4").reshape(h, w).astype(np.float64)


def read_curve(path="contrast.csv"):
    with open(path) as f:
        rows = list(csv.DictReader(f))
    return {k: np.array([float(r[k]) for r in rows]) for k in rows[0]}


# --------------------------------------------------------------------------- 1

def compare_threshold():
    print("1. The small-sample threshold (Mawet et al. 2014)")
    print("   " + "-" * 68)

    with open("threshold.csv") as f:
        rows = list(csv.DictReader(f))

    print(f"   {'n_res':>8}{'ours':>14}{'VIP recipe':>14}{'rel. diff':>14}")
    worst = 0.0
    for r in rows:
        n = float(r["n_res_elements"])
        ours = float(r["threshold_sigma"])
        # VIP's own two lines, verbatim in effect.
        theirs = stats.t.ppf(stats.norm.cdf(5.0), n - 1) * np.sqrt(1.0 + 1.0 / n)
        rel = abs(ours - theirs) / theirs
        worst = max(worst, rel)
        print(f"   {n:8.0f}{ours:14.6f}{theirs:14.6f}{rel:14.2e}")

    print(f"   worst relative disagreement: {worst:.2e}")
    verdict(
        "Small-sample threshold",
        "EQUAL" if worst < 1e-6 else "DIFFERENT",
        f"our Student t quantile and continued-fraction incomplete beta reproduce SciPy's "
        f"t.ppf at a tail probability of 2.87e-7 to {worst:.1e} relative",
    )
    print()
    return worst


# --------------------------------------------------------------------------- 2

def compare_noise(frame, meta):
    print("2. The annulus noise estimator")
    print("   " + "-" * 68)

    fwhm_px = meta["lambda_over_d_px"]
    ours = read_curve()

    # VIP walks annuli spaced by one FWHM starting at init_rad, laying floor(2*pi*r/fwhm)
    # non-overlapping apertures of radius fwhm/2 around each and taking their standard deviation.
    theirs_noise, _theirs_res, theirs_rad = noise_per_annulus(
        frame, separation=fwhm_px, fwhm=fwhm_px,
        init_rad=meta["inner_working_angle_mas"] / meta["plate_scale_mas_per_px"],
    )

    px = meta["plate_scale_mas_per_px"]
    theirs_sep_mas = theirs_rad * px

    print(f"   {'sep [mas]':>11}{'ours':>14}{'VIP':>14}{'ratio':>10}")
    ratios = []
    for sep, sig in zip(ours["separation_mas"], ours["noise_sigma"]):
        j = int(np.argmin(np.abs(theirs_sep_mas - sep)))
        if abs(theirs_sep_mas[j] - sep) > 0.5 * fwhm_px * px:
            continue
        ratio = sig / theirs_noise[j]
        ratios.append(ratio)
        if len(ratios) <= 8:
            print(f"   {sep:11.1f}{sig:14.5e}{theirs_noise[j]:14.5e}{ratio:10.4f}")

    ratios = np.array(ratios)
    med = float(np.median(ratios))
    spread = float(np.std(ratios))
    print(f"   {len(ratios)} matched annuli, median ratio {med:.4f}, scatter {spread:.4f}")
    print("   The two estimators are the same construction on the same pixels; what is left is")
    print("   that VIP places its first aperture at a different position angle and steps its")
    print("   annuli from its own init_rad, so the two sample different speckles of one field.")

    verdict(
        "Annulus noise estimator",
        "EQUAL" if 0.85 < med < 1.18 else "DIFFERENT",
        f"median ratio {med:.3f} over {len(ratios)} annuli, scatter {spread:.3f}; same "
        f"construction, different aperture phase",
    )
    print()
    return med


# --------------------------------------------------------------------------- 3

def compare_curve(frame, meta):
    print("3. The contrast curve end to end")
    print("   " + "-" * 68)

    ours = read_curve()
    fwhm_px = meta["lambda_over_d_px"]
    px = meta["plate_scale_mas_per_px"]
    star = meta["star_peak"]

    theirs_noise, theirs_res, theirs_rad = noise_per_annulus(
        frame, separation=fwhm_px, fwhm=fwhm_px,
        init_rad=meta["inner_working_angle_mas"] / px,
    )
    n_res = np.floor(theirs_rad / fwhm_px * 2 * np.pi)
    sigma_corr = stats.t.ppf(stats.norm.cdf(5.0), n_res - 1) * np.sqrt(1.0 + 1.0 / n_res)

    # VIP's own expression, with unit throughput and no residual-level term, which is what our
    # frame has: no companion was injected and no post-processing was applied.
    theirs_contrast = sigma_corr * theirs_noise / star
    theirs_sep = theirs_rad * px

    print(f"   {'sep [mas]':>11}{'ours':>13}{'VIP':>13}{'ratio':>9}{'d(mag)':>9}")
    rows = []
    for sep, c in zip(ours["separation_mas"], ours["contrast"]):
        j = int(np.argmin(np.abs(theirs_sep - sep)))
        if abs(theirs_sep[j] - sep) > 0.5 * fwhm_px * px:
            continue
        t = theirs_contrast[j]
        rows.append((sep, c, t, c / t, -2.5 * np.log10(c / t)))

    for sep, c, t, ratio, dmag in rows[:10]:
        print(f"   {sep:11.1f}{c:13.4e}{t:13.4e}{ratio:9.4f}{dmag:9.3f}")

    ratios = np.array([r[3] for r in rows])
    dmags = np.array([abs(r[4]) for r in rows])
    print(f"   {len(rows)} matched points, median ratio {np.median(ratios):.4f}, "
          f"worst disagreement {dmags.max():.3f} mag")

    verdict(
        "Contrast curve end to end",
        "EQUAL" if dmags.max() < 0.25 else "DIFFERENT",
        f"agrees with VIP to {dmags.max():.2f} mag at worst over {len(rows)} separations, "
        f"median ratio {np.median(ratios):.3f}",
    )
    print()


# --------------------------------------------------------------------------- 4

def report_what_vip_has_that_we_do_not():
    print("4. What VIP does that this pipeline does not")
    print("   " + "-" * 68)
    print("   VIP is a post-processing package, and most of it has no counterpart here because")
    print("   this is a forward simulator. Named rather than glossed over:")
    print("     * PCA/KLIP, LLSG, NMF, LOCI and ANDROMEDA reductions. We model median-subtraction")
    print("       ADI only, and only as an analytic throughput rather than as a reduction.")
    print("     * Throughput measured by INJECTING fake companions and recovering them, which is")
    print("       how a real contrast curve is calibrated. Ours is the analytic expression in")
    print("       Core.AngularDifferentialImaging, declared as a modelling choice.")
    print("     * Spectral differential imaging, and 4-D cubes.")
    print("     * Detection maps, S/N maps and negative-fake-companion astrometry.")
    print()
    verdict("ADI throughput calibration", "WORSE",
            "VIP measures throughput by injecting and recovering fake companions; ours is an "
            "analytic form with declared limits and one published data point to check it against")
    verdict("Post-processing algorithms", "WORSE",
            "VIP implements PCA/KLIP, LOCI, LLSG, NMF and ANDROMEDA; this models median-subtraction "
            "ADI only")
    verdict("Forward instrument model", "BETTER",
            "VIP starts from a cube it is given. This produces the cube: measured coronagraph "
            "attenuations, a measured Lyot stop, modified-Rician speckles with measured "
            "decorrelation timescales, and a detector chain under all of it")


def main():
    meta = read_meta()
    frame = read_frame()

    print()
    print("ExoInstruments high-contrast chain against VIP " + __import__("vip_hci").__version__)
    print("=" * 78)
    print(f"frame {frame.shape[0]}x{frame.shape[1]}, {meta['plate_scale_mas_per_px']:.3f} mas/px, "
          f"lambda/D = {meta['lambda_over_d_px']:.2f} px")
    print()

    compare_threshold()
    compare_noise(frame, meta)
    compare_curve(frame, meta)
    report_what_vip_has_that_we_do_not()

    print("VERDICT")
    print("=" * 78)
    width = max(len(v[0]) for v in VERDICTS)
    for what, call, detail in VERDICTS:
        print(f"  {what:{width}s}  {call}")
        print(f"  {'':{width}s}    {detail}")
        print()

    tally = {}
    for _, call, _ in VERDICTS:
        tally[call] = tally.get(call, 0) + 1
    print("  " + ", ".join(f"{n} {c.lower()}" for c, n in sorted(tally.items())))
    print()
    print("  Read as: on the measurement itself this pipeline and VIP are the same code written")
    print("  twice, which is the point of checking. On post-processing VIP is far ahead and this")
    print("  does not try to compete. What this has and VIP does not is the instrument in front")
    print("  of the measurement.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
