"""Cross-validates the constellation lookup against astropy.

WHY THIS EXISTS. Naming the constellation a target sits in looks like a lookup and is really a
frame change. Delporte's boundaries are rectangles only in the mean equinox of B1875, which is a
BESSELIAN equinox of the old fundamental system; a J2000 position has to cross both the 125 years
and the FK5-to-FK4 system difference to get there. Skipping either leaves a lookup that is right
in the middle of every constellation and wrong along every edge, which is to say wrong exactly
where the answer is interesting, and never obviously wrong at all.

THREE REFERENCES, because they check different things.

1. astropy's FK4NoETerms(equinox=B1875) frame, for the frame change on its own. This is the same
   two-step chain the C# implements (Murray's FK4/FK5 rotation and Newcomb's precession), so
   agreement here is agreement on the arithmetic, to machine precision, not on the physics.

2. astropy's get_constellation, for the answer. It is NOT the same chain: it precesses with the
   modern IAU 2006 model to the Julian date of B1875 instead of going through FK4, which its own
   docstring calls "plenty sufficient for constellations". The two therefore genuinely disagree by
   a few arcseconds, and the point of this check is to MEASURE that: every disagreement must be
   a position within a few arcseconds of a boundary, and there must be no disagreement anywhere
   else. A systematic error would show up as disagreements in constellation interiors.

3. Roman's own eight worked examples from the VI/42 ReadMe, which are stated at equinox 1950 and
   so exercise the Newcomb precession and the table scan with the author's own expected answers.

Plus one structural check with no external reference: all 88 constellations must be reachable. A
lost or mis-sorted record in the table silently makes some constellation unreturnable, and no
comparison against a position finds that unless it happens to land there.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_constellations.py

Exit code 0 when every check passes, 1 otherwise.
"""

import csv
import sys
import warnings

import numpy as np
import astropy.units as u
from astropy.coordinates import FK4NoETerms, PrecessedGeocentric, SkyCoord, get_constellation
from astropy.time import Time
from astropy.utils.exceptions import AstropyWarning

failures = []


def report(label, ok, detail):
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {detail}")


def read(path):
    with open(path) as handle:
        return list(csv.DictReader(handle))


def separation_arcsec(ra1, dec1, ra2, dec2):
    """Angular separation, degrees in and arcseconds out, without building SkyCoords per row.

    Vector form rather than the spherical cosine rule: this comparison is looking for
    milliarcsecond agreement, and arccos of a dot product near one loses half its significant
    figures there, so identical positions come out as a spurious few milliarcseconds.
    """
    d = np.radians
    v1 = np.array([np.cos(d(dec1)) * np.cos(d(ra1)), np.cos(d(dec1)) * np.sin(d(ra1)), np.sin(d(dec1))])
    v2 = np.array([np.cos(d(dec2)) * np.cos(d(ra2)), np.cos(d(dec2)) * np.sin(d(ra2)), np.sin(d(dec2))])
    cross = np.linalg.norm(np.cross(v1.T, v2.T), axis=-1)
    dot = np.sum(v1 * v2, axis=0)
    return np.degrees(np.arctan2(cross, dot)) * 3600.0


def check_frame():
    """The J2000 -> B1875 frame change, against astropy's own FK4NoETerms."""
    print("\n1. J2000 to the mean equinox of B1875, against astropy FK4NoETerms")
    rows = read("exo_frame.csv")
    ra = np.array([float(r["ra_j2000_deg"]) for r in rows])
    dec = np.array([float(r["dec_j2000_deg"]) for r in rows])
    mine_ra = np.array([float(r["ra_b1875_deg"]) for r in rows])
    mine_dec = np.array([float(r["dec_b1875_deg"]) for r in rows])

    with warnings.catch_warnings():
        warnings.simplefilter("ignore", AstropyWarning)
        reference = SkyCoord(ra=ra * u.deg, dec=dec * u.deg, frame="fk5", equinox="J2000").transform_to(
            FK4NoETerms(equinox=Time("B1875", scale="tt"))
        )

    sep = separation_arcsec(mine_ra, mine_dec, reference.ra.deg, reference.dec.deg)
    worst = np.nanmax(sep)
    report(
        "position after the frame change",
        worst < 1e-3,
        f"worst of {len(rows)} grid points {worst:.3e} arcsec",
    )

    # The frame change is not a no-op: say by how much, so a build that accidentally short-circuits
    # it (returning J2000 unchanged) cannot pass by agreeing with itself.
    moved = separation_arcsec(ra, dec, mine_ra, mine_dec)
    report(
        "the frame change actually moves positions",
        np.median(moved) > 1000.0,
        f"median displacement {np.median(moved) / 3600.0:.3f} deg over 125 years",
    )


def check_roman():
    """Roman's own worked examples, at equinox 1950."""
    print("\n2. The worked examples published in the VI/42 ReadMe")
    rows = read("exo_roman.csv")
    wrong = [r for r in rows if r["got"] != r["expected"]]
    report(
        "all eight reproduce",
        not wrong,
        "8/8 match" if not wrong else f"{len(wrong)} disagree: {wrong}",
    )


def check_lookup():
    """The constellation itself, against astropy's get_constellation."""
    print("\n3. Constellation of a J2000 position, against astropy get_constellation")
    rows = read("exo_lookup.csv")
    ra = np.array([float(r["ra_j2000_deg"]) for r in rows])
    dec = np.array([float(r["dec_j2000_deg"]) for r in rows])
    mine = np.array([r["abbreviation"] for r in rows])

    with warnings.catch_warnings():
        warnings.simplefilter("ignore", AstropyWarning)
        coords = SkyCoord(ra=ra * u.deg, dec=dec * u.deg, frame="icrs")
        theirs = np.array(get_constellation(coords, short_name=True))

    disagree = mine != theirs
    count = int(disagree.sum())
    fraction = count / len(rows)
    report(
        "agreement over the grid",
        fraction < 2e-3,
        f"{len(rows) - count} of {len(rows)} agree ({100 * (1 - fraction):.3f}%)",
    )

    # How far apart the two B1875 realisations are in the first place. This is the budget: a
    # disagreement further from a boundary than this is not explained by the frame difference and
    # would mean something is actually wrong with the table or the scan.
    budget = frame_realisation_gap()
    print(f"       the two B1875 realisations themselves differ by up to {budget:.1f} arcsec")

    # Every disagreement must be a position sitting on a boundary. Distance to the boundary is
    # measured by asking how far the position can be nudged before the answer changes: a
    # disagreement in the interior of a constellation would survive a large nudge.
    if count:
        idx = np.flatnonzero(disagree)
        worst = np.nanmax(boundary_distance(ra[idx], dec[idx]))
        report(
            "every disagreement is explained by the frame difference",
            worst <= budget,
            f"furthest of {count} disagreements is {worst:.1f} arcsec from the nearest boundary,"
            f" against a {budget:.1f} arcsec budget",
        )
    else:
        report("every disagreement is explained by the frame difference", True, "no disagreements at all")

    print(f"\n4. All 88 constellations reachable")
    found = set(mine)
    expected = set(np.unique(theirs)) | found
    missing = sorted(expected - found)
    report(
        "every constellation appears in the grid",
        len(found) == 88 and not missing,
        f"{len(found)} distinct constellations returned"
        + (f", missing {missing}" if missing else ""),
    )


def frame_realisation_gap():
    """By how much astropy's own two routes to "B1875" disagree, in arcseconds.

    get_constellation precesses with the modern IAU 2006 model to the Julian date of B1875;
    FK4NoETerms goes through the FK4 system the boundaries were actually drawn in. Both are
    astropy's, so this measures the frame difference and nothing about this codebase.
    """
    rng = np.random.default_rng(20250731)
    ra = rng.uniform(0.0, 360.0, 2000)
    dec = np.degrees(np.arcsin(rng.uniform(-1.0, 1.0, 2000)))
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", AstropyWarning)
        icrs = SkyCoord(ra=ra * u.deg, dec=dec * u.deg, frame="icrs")
        fk4 = icrs.transform_to(FK4NoETerms(equinox=Time("B1875")))
        precessed = icrs.transform_to(PrecessedGeocentric(equinox="B1875"))
    return float(np.max(separation_arcsec(fk4.ra.deg, fk4.dec.deg, precessed.ra.deg, precessed.dec.deg)))


def boundary_distance(ra, dec):
    """How far each position can be moved before the lookup changes its answer.

    Bisection on a small circle around the point, using astropy's boundaries as a stand-in for our
    own (the two differ by arcseconds, which is the scale being measured, so this is an UPPER bound
    on the distance to a boundary, exactly the direction that makes the check strict).
    """
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", AstropyWarning)
        distances = np.zeros(len(ra))
        for i in range(len(ra)):
            centre = SkyCoord(ra=ra[i] * u.deg, dec=dec[i] * u.deg, frame="icrs")
            here = get_constellation(centre, short_name=True)
            lo, hi = 0.0, 60.0
            for _ in range(20):
                mid = 0.5 * (lo + hi)
                offsets = centre.directional_offset_by(
                    np.arange(0, 360, 30) * u.deg, mid * u.arcsec
                )
                if np.all(np.array(get_constellation(offsets, short_name=True)) == here):
                    lo = mid
                else:
                    hi = mid
            distances[i] = lo
    return distances


def main():
    check_frame()
    check_roman()
    check_lookup()

    print()
    if failures:
        print(f"FAILED: {len(failures)} check(s): {', '.join(failures)}")
        return 1
    print("ALL CHECKS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
