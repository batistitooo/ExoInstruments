"""Cross-validates ExoInstruments' HEALPix indexing and Galactic transform.

Every all-sky dust, H-alpha and CO map is a HEALPix array in Galactic coordinates. Reading one from
a catalogue position means composing two transforms, and both fail silently: a wrong pixel returns a
plausible number from the wrong part of the sky, and a wrong Galactic frame puts the plane at the
wrong angle. Neither throws.

References are the ones the maps themselves are made with: healpy, which wraps the HEALPix C++
library of Gorski et al. (2005), and astropy's Galactic frame.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_mapindex.py
"""

import sys

import numpy as np
import healpy as hp
from astropy.coordinates import SkyCoord
import astropy.units as u

failures = []
notes = []


def check(label, value, reference, tol, unit=""):
    dev = abs(value - reference)
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.6g} vs {reference:.6g}{unit}  ->  {dev:.3e}{unit}")
    return ok


def healpix():
    print("\n1. HEALPix pixel indexing, against healpy")
    d = np.genfromtxt("exo_healpix.csv", delimiter=",", names=True)

    total_ring = total_nest = 0
    for nside in np.unique(d["nside"]).astype(int):
        sub = d[d["nside"] == nside]
        ring_ref = hp.ang2pix(nside, sub["theta_rad"], sub["phi_rad"], nest=False)
        nest_ref = hp.ang2pix(nside, sub["theta_rad"], sub["phi_rad"], nest=True)

        ring_bad = int(np.count_nonzero(sub["ring"].astype(np.int64) != ring_ref))
        nest_bad = int(np.count_nonzero(sub["nested"].astype(np.int64) != nest_ref))
        total_ring += ring_bad
        total_nest += nest_bad

        print(f"  [{'ok  ' if ring_bad == 0 and nest_bad == 0 else 'FAIL'}] "
              f"nside {nside:5d} ({hp.nside2resol(nside, arcmin=True):8.2f} arcmin, "
              f"{len(sub)} directions): {ring_bad} ring, {nest_bad} nested mismatches")
        if ring_bad or nest_bad:
            failures.append(f"HEALPix nside {nside}")

    if total_ring == 0 and total_nest == 0:
        notes.append(f"HEALPix exact over {len(d)} directions at 8 resolutions, "
                     f"RING and NESTED, including the |z| = 2/3 cap boundary and both poles")

    # Pixels are equal-area by construction; the resolution the mod reports must be healpy's.
    print("\n2. Pixel geometry")
    for nside in (64, 256, 2048):
        check(f"nside {nside}: pixel count", float(12 * nside * nside), float(hp.nside2npix(nside)), 0.0)
        check(f"nside {nside}: resolution (deg)",
              np.degrees(np.sqrt(4 * np.pi / (12 * nside * nside))),
              hp.nside2resol(nside) * 180.0 / np.pi, 1e-12, " deg")


def interpolation():
    """The four-pixel bilinear read, against healpy's get_interp_weights.

    A beam-smoothed map has to be interpolated rather than sampled pixel by pixel, and getting it
    subtly wrong is invisible: the sky still looks like a sky. So both halves are checked -- which
    four pixels, and with what weight -- against the reference implementation of the same scheme.
    """
    print("\n1b. Bilinear interpolation weights, against healpy")
    d = np.genfromtxt("exo_interp.csv", delimiter=",", names=True)

    worst_w = 0.0
    total_pix_bad = 0
    for nside in np.unique(d["nside"]).astype(int):
        sub = d[d["nside"] == nside]
        pix_ref, wgt_ref = hp.get_interp_weights(nside, sub["theta_rad"], sub["phi_rad"], nest=False)
        pix_ours = np.vstack([sub[f"p{i}"] for i in range(4)]).astype(np.int64)
        wgt_ours = np.vstack([sub[f"w{i}"] for i in range(4)])

        # Order is not part of the contract, so both sides are sorted by pixel before comparing.
        # A weight has to travel with its own pixel, so the same permutation is applied to both.
        order_ref = np.argsort(pix_ref, axis=0)
        order_ours = np.argsort(pix_ours, axis=0)
        pr = np.take_along_axis(pix_ref, order_ref, axis=0)
        po = np.take_along_axis(pix_ours, order_ours, axis=0)
        wr = np.take_along_axis(np.asarray(wgt_ref), order_ref, axis=0)
        wo = np.take_along_axis(wgt_ours, order_ours, axis=0)

        pix_bad = int(np.count_nonzero(pr != po))
        w_dev = float(np.abs(wr - wo).max())
        total_pix_bad += pix_bad
        worst_w = max(worst_w, w_dev)

        sum_dev = float(np.abs(wgt_ours.sum(axis=0) - 1.0).max())
        ok = pix_bad == 0 and w_dev < 1e-12 and sum_dev < 1e-12
        if not ok:
            failures.append(f"interpolation nside {nside}")
        print(f"  [{'ok  ' if ok else 'FAIL'}] nside {nside:5d} ({len(sub)} directions): "
              f"{pix_bad} wrong pixels, weights to {w_dev:.2e}, sum to 1 within {sum_dev:.2e}")

    if total_pix_bad == 0:
        notes.append(f"interpolation matches healpy's get_interp_weights exactly over {len(d)} "
                     f"directions -- same four pixels, weights to {worst_w:.1e}")


def galactic():
    print("\n3. Galactic coordinates, against astropy")
    d = np.genfromtxt("exo_galactic.csv", delimiter=",", names=True)

    ref = SkyCoord(ra=d["ra_deg"] * u.deg, dec=d["dec_deg"] * u.deg, frame="icrs").galactic
    dl = np.abs((d["l_deg"] - ref.l.deg + 180.0) % 360.0 - 180.0)
    db = np.abs(d["b_deg"] - ref.b.deg)

    # Longitude is degenerate at the Galactic poles, where every l names the same direction.
    off_pole = np.abs(d["b_deg"]) < 89.0
    check(f"latitude, max deviation over {len(d)} directions", db.max(), 0.0, 2e-4, " deg")
    check("longitude, max deviation (off the Galactic poles)", dl[off_pole].max(), 0.0, 2e-4, " deg")

    print("\n4. The inverse, which a map cell needs to be reported back as a sky position")
    dra = np.abs((d["ra_back_deg"] - d["ra_deg"] + 180.0) % 360.0 - 180.0)
    ddec = np.abs(d["dec_back_deg"] - d["dec_deg"])
    off_cel_pole = np.abs(d["dec_deg"]) < 89.0
    # Loosest tolerance here, and the reason is arithmetic rather than physics: the round trip goes
    # through asin twice, which loses relative precision as its argument approaches +-1, i.e. at the
    # poles. 1e-8 deg is 36 microarcseconds, four orders below any map pixel.
    check("declination round trip", ddec.max(), 0.0, 1e-8, " deg")
    check("right ascension round trip (off the celestial poles)",
          dra[off_cel_pole].max(), 0.0, 1e-10, " deg")

    print("\n5. Named sight lines, where a sign error is unmistakable")
    # Sgr A* must land within a few arcminutes of the Galactic origin -- it is a few arcminutes
    # from it in reality, which is itself the check that the frame is the IAU one and not a fit.
    i = 0
    l0, b0 = d["l_deg"][i], d["b_deg"][i]
    sep = np.hypot(((l0 + 180) % 360 - 180) * np.cos(np.radians(b0)), b0)
    print(f"  [note] Sgr A* lands at l = {l0:.4f}, b = {b0:.4f}, {sep * 60:.1f} arcmin from the origin")
    check("Sgr A* is within a degree of the Galactic origin", sep, 0.0, 1.0, " deg")
    notes.append(f"Sgr A* at l = {l0:.4f} deg, b = {b0:.4f} deg")

    check("the north Galactic pole maps to b = +90", d["b_deg"][1], 90.0, 1e-6, " deg")
    check("the south Galactic pole maps to b = -90", d["b_deg"][2], -90.0, 1e-6, " deg")


def float16():
    """Every half-float encoding, against numpy's own float16."""
    print("\n6. IEEE 754 binary16 decode, against numpy, over all 65536 encodings")
    d = np.genfromtxt("exo_float16.csv", delimiter=",", names=True)
    bits = d["bits"].astype(np.uint16)
    ref = bits.view(np.float16).astype(np.float64)
    got = d["value"]

    finite = np.isfinite(ref)
    check(f"finite values ({finite.sum()} of {len(ref)})",
          float(np.abs(got[finite] - ref[finite]).max()), 0.0, 0.0)
    check("NaN encodings decode to NaN",
          float(np.count_nonzero(np.isnan(got[np.isnan(ref)]) != True)), 0.0, 0.0)
    check("infinite encodings decode to infinity",
          float(np.count_nonzero(got[np.isinf(ref)] != ref[np.isinf(ref)])), 0.0, 0.0)
    notes.append("the half-float decode is exact over all 65536 encodings, including subnormals, "
                 "NaN and both infinities")


def dust_map():
    """The packed map format and the query, end to end against the pattern that was written."""
    print("\n7. DustMap: read back the synthetic map and query it")
    d = np.genfromtxt("exo_mapquery.csv", delimiter=",", names=True)
    if len(d) == 0:
        print("  [note] no test_map.dustmap; run make_test_map.py first")
        return

    # The pattern make_test_map.py wrote, recomputed here from the queried position alone. The
    # map file is not consulted, so this checks the pixel lookup rather than the file's contents.
    expected = 0.02 + 1.98 * np.exp(-np.abs(d["b_deg"]) / 8.0)
    known = np.isfinite(d["ebv"])

    # A query returns the value of the pixel it lands in, while the expectation is evaluated at the
    # queried direction, so the residual is the pattern's own variation across one pixel. Bounding
    # it by a flat tolerance would be wrong: the pattern is steepest in the Galactic plane and flat
    # at the poles. The bound is therefore the gradient itself, times one pixel.
    dev = np.abs(d["ebv"][known] - expected[known])
    resol_deg = np.degrees(np.sqrt(4 * np.pi / (12 * 64 * 64)))
    gradient = (1.98 / 8.0) * np.exp(-np.abs(d["b_deg"][known]) / 8.0)
    bound = gradient * resol_deg + 1e-4          # plus the write quantisation
    worst = float((dev / bound).max())
    check(f"residual within one pixel of the pattern's own gradient, {known.sum()} directions",
          worst, 0.0, 1.0, " pixel-gradients")
    check("median residual, i.e. the typical sub-pixel variation",
          float(np.median(dev)), 0.0, 5e-3, " mag")
    check("A(V) is exactly R_V E(B-V)",
          float(np.abs(d["av"][known] / d["ebv"][known] - 3.1).max()), 0.0, 1e-12)
    notes.append(f"the packed map round-trips: {known.sum()} of {len(d)} directions carry a value, "
                 f"median residual {np.median(dev) * 1000:.1f} mmag against the written pattern")


def real_map():
    """The whole chain against dustmaps, on the real sky, if a real map has been built."""
    import os
    if not os.path.exists("exo_realmap.csv"):
        print("\n8. Real map: none built next door, skipped")
        print("   Build one with tools/pack_dust_map.py to run this.")
        return

    print("\n8. A real packed map, against dustmaps on the real sky")
    d = np.genfromtxt("exo_realmap.csv", delimiter=",", names=True)

    import glob
    import astropy.units as u
    import healpy as hp
    from dustmaps.config import config
    from dustmaps.sfd import SFDQuery
    try:
        query = SFDQuery()
    except Exception:                                       # noqa: BLE001
        # tools/pack_dust_map.py fetched SFD into its own environment; point at that copy rather
        # than downloading 128 MB a second time.
        found = glob.glob("../*/lib/*/site-packages/dustmaps/data/sfd/SFD_dust_4096_ngp.fits")
        if not found:
            print("  [note] SFD data not found in any sibling environment, skipped")
            return
        config["data_dir"] = os.path.dirname(os.path.dirname(found[0]))
        query = SFDQuery()
    ref = np.asarray(query(SkyCoord(ra=d["ra_deg"] * u.deg, dec=d["dec_deg"] * u.deg,
                                    frame="icrs")), dtype=float) * 0.86

    # THE REFERENCE HAS TO BE REBUILT THE SAME WAY THE MAP WAS. The packer stored SFD sampled at
    # each HEALPix pixel's own centre as a half float; the reader interpolates bilinearly between
    # four of those. So the reference is that same operation done independently in Python: healpy
    # picks the four pixels and weights, dustmaps supplies each pixel centre's value, and the two
    # are combined. Comparing against dustmaps queried directly at the sight line instead would
    # measure the resampling onto HEALPix, which is a real effect but not what this check is for --
    # it is reported separately below.
    coords = SkyCoord(ra=d["ra_deg"] * u.deg, dec=d["dec_deg"] * u.deg, frame="icrs").galactic
    pix, wgt = hp.get_interp_weights(1024, coords.l.deg, coords.b.deg, nest=False, lonlat=True)
    cl, cb = hp.pix2ang(1024, pix.ravel(), nest=False, lonlat=True)
    vals = np.asarray(query(SkyCoord(l=cl * u.deg, b=cb * u.deg, frame="galactic")),
                      dtype=float) * 0.86
    # Through float16 and back, because that is the precision the file carries.
    vals = vals.astype(np.float16).astype(float).reshape(pix.shape)
    ref = (np.asarray(wgt) * vals).sum(axis=0)

    known = np.isfinite(d["ebv"]) & np.isfinite(ref) & (ref > 0)
    rel = np.abs(d["ebv"][known] / ref[known] - 1.0)

    # Half-float precision is 4.9e-4 relative, and nothing else stands between the two.
    check(f"interpolated reddening, {known.sum()} real sight lines, relative",
          float(rel.max()), 0.0, 5e-4)
    check("nothing lost: every direction carries a value",
          float(np.count_nonzero(~np.isfinite(d["ebv"]))), 0.0, 0.0)

    # And the resampling, reported rather than asserted: it is the price of a HEALPix grid and it
    # is concentrated exactly where the dust is steepest.
    interp = np.asarray(query(SkyCoord(ra=d["ra_deg"] * u.deg, dec=d["dec_deg"] * u.deg,
                                       frame="icrs")), dtype=float) * 0.86
    ok = np.isfinite(interp) & (interp > 0) & known
    resample = np.abs(d["ebv"][ok] / interp[ok] - 1.0)
    v = d["ebv"][known]
    print(f"  [note] range over the sample: {v.min():.5f} to {v.max():.2f} mag, "
          f"median {np.median(v):.5f}")
    print(f"  [note] resampling onto HEALPix at 3.4 arcmin, then interpolating back: "
          f"median {np.median(resample) * 100:.2f}%, worst {resample.max() * 100:.0f}% "
          f"(in the plane, where SFD is steepest)")
    notes.append(f"the real packed map reproduces an independently built bilinear read of SFD to "
                 f"{rel.max():.1e} relative over {known.sum()} sight lines, with no pixel lost")


def emission_map():
    """The emission format, which shares the dust layout but adds the line it belongs to."""
    import os
    print("\n7b. EmissionMap: the same round trip, plus the line header")
    if not os.path.exists("exo_emissionquery.csv"):
        print("  [note] no test_map.emission; run make_test_map.py first")
        return
    d = np.genfromtxt("exo_emissionquery.csv", delimiter=",", names=True)
    expected = 0.5 + 8000.0 * np.exp(-np.abs(d["b_deg"]) / 4.0)
    known = np.isfinite(d["rayleighs"])

    resol_deg = np.degrees(np.sqrt(4 * np.pi / (12 * 64 * 64)))
    gradient = (8000.0 / 4.0) * np.exp(-np.abs(d["b_deg"][known]) / 4.0)
    dev = np.abs(d["rayleighs"][known] - expected[known])
    # Half float is relative, so the floor is 4.9e-4 of the value rather than a fixed step.
    bound = gradient * resol_deg + 4.9e-4 * expected[known]
    check(f"residual within one pixel of the pattern's gradient, {known.sum()} directions",
          float((dev / bound).max()), 0.0, 1.0, " pixel-gradients")
    notes.append(f"the emission format round-trips over five decades, 0.5 to 8000 R, "
                 f"with no clipping at either end")


def main():
    print(__doc__.split("Run:")[0].strip())
    healpix()
    interpolation()
    galactic()
    float16()
    dust_map()
    emission_map()
    real_map()

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
