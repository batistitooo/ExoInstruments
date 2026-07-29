"""Cross-validates ExoInstruments' pointing transforms against IAU SOFA and astropy.

WHY THIS EXISTS. The visual telescopes used to aim only at CelestialBody transforms, so nothing on
the sky that is not a planet could be photographed. Aiming at a catalogue position instead runs the
star field's own transform BACKWARDS -- equatorial to horizontal, horizontal to a direction in the
observatory's (north, east, up) basis, that direction onto the world basis -- and a sign error
anywhere in that chain produces a telescope that points somewhere plausible and wrong.

THE REFERENCE IS SOFA, not a rearrangement of the same formulae. pyerfa wraps ERFA, which is the
IAU SOFA library: erfa.hd2ae and erfa.ae2hd are the standard hour-angle/declination to
azimuth/elevation pair, implemented independently in C, and they use azimuth measured north through
east -- the same convention this codebase uses. Agreement is therefore evidence about the
transformation rather than about a shared derivation.

Deliberately NOT compared against astropy's full AltAz frame: that applies precession, nutation,
aberration, polar motion and refraction, none of which this mod models, so a disagreement there
would measure the corrections rather than the trigonometry. astropy IS used for the one thing it is
the right reference for -- parsing what a catalogue writes.

Run:
    dotnet run -p:Core=../../ExoInstruments/Core
    ./env/bin/python compare_pointing.py

Exit code 0 when every check passes, 1 otherwise.
"""

import csv
import sys

import numpy as np
import erfa
from astropy.coordinates import SkyCoord
import astropy.units as u

DEG = np.pi / 180.0

failures = []
notes = []


def check(label, value, reference, tol, unit="", relative=False):
    if relative:
        denom = abs(reference) if abs(reference) > 0 else 1.0
        dev = abs(value - reference) / denom
    else:
        dev = abs(value - reference)
    ok = dev <= tol
    if not ok:
        failures.append(label)
    print(f"  [{'ok  ' if ok else 'FAIL'}] {label}: {value:.6g} vs {reference:.6g}{unit}  ->  {dev:.3e}{unit}")
    return ok


def wrap180(x):
    return (x + 180.0) % 360.0 - 180.0


def equatorial():
    """RA/Dec to altitude and azimuth, against SOFA's hd2ae."""
    print("\n1. Equatorial to horizontal, against IAU SOFA (erfa.hd2ae)")
    d = np.genfromtxt("exo_equatorial.csv", delimiter=",", names=True)

    ha = (d["lst_deg"] - d["ra_deg"]) * DEG
    az_ref, el_ref = erfa.hd2ae(ha, d["dec_deg"] * DEG, d["latitude_deg"] * DEG)
    az_ref = np.degrees(az_ref) % 360.0
    el_ref = np.degrees(el_ref)

    alt_dev = np.abs(d["alt_deg"] - el_ref).max()
    az_dev = np.abs(wrap180(d["az_deg"] - az_ref))
    # Azimuth is undefined at the zenith and degenerate near it, so it is compared where it means
    # something; the altitude check covers the pole cases on its own.
    away_from_zenith = np.abs(d["alt_deg"]) < 89.9
    az_dev = az_dev[away_from_zenith].max()

    check(f"altitude, max deviation over {len(d)} geometries", alt_dev, 0.0, 1e-11, " deg")
    check(f"azimuth, max deviation ({away_from_zenith.sum()} away from the zenith)",
          az_dev, 0.0, 1e-11, " deg")

    print("\n2. And back: horizontal to equatorial, against SOFA's ae2hd")
    ha_ref, dec_ref = erfa.ae2hd(d["az_deg"] * DEG, d["alt_deg"] * DEG, d["latitude_deg"] * DEG)
    ra_ref = (d["lst_deg"] - np.degrees(ha_ref)) % 360.0
    dec_ref = np.degrees(dec_ref)

    # The round trip is degenerate at the pole, where every right ascension names the same point.
    off_pole = np.abs(d["dec_deg"]) < 89.0
    check("declination, max deviation", np.abs(d["dec_back_deg"] - dec_ref).max(), 0.0, 1e-11, " deg")
    check("right ascension, max deviation (off the pole)",
          np.abs(wrap180(d["ra_back_deg"] - ra_ref))[off_pole].max(), 0.0, 1e-11, " deg")

    print("\n3. The mod's own round trip, which is what the aim and the star field must share")
    ra_close = np.abs(wrap180(d["ra_back_deg"] - d["ra_deg"]))[off_pole].max()
    dec_close = np.abs(d["dec_back_deg"] - d["dec_deg"]).max()
    check("RA recovered from its own altitude/azimuth", ra_close, 0.0, 1e-10, " deg")
    check("Dec recovered from its own altitude/azimuth", dec_close, 0.0, 1e-10, " deg")
    notes.append(f"the equatorial round trip closes to {max(ra_close, dec_close):.1e} deg, "
                 f"{max(ra_close, dec_close) * 3.6e6:.1e} mas")


def basis():
    """The (north, east, up) direction cosines the aim is built from."""
    print("\n4. Horizontal to direction cosines, and back")
    d = np.genfromtxt("exo_basis.csv", delimiter=",", names=True)

    alt = d["alt_deg"] * DEG
    az = d["az_deg"] * DEG
    # SOFA's s2c on (azimuth, elevation) gives the same triple in the same order, which is the
    # independent statement: north is x, east is y, up is z, azimuth measured north through east.
    ref = erfa.s2c(az, alt)

    check("north component", np.abs(d["north"] - ref[..., 0]).max(), 0.0, 1e-14)
    check("east component", np.abs(d["east"] - ref[..., 1]).max(), 0.0, 1e-14)
    check("up component", np.abs(d["up"] - ref[..., 2]).max(), 0.0, 1e-14)

    norm = np.sqrt(d["north"] ** 2 + d["east"] ** 2 + d["up"] ** 2)
    check("every direction is a unit vector", np.abs(norm - 1.0).max(), 0.0, 1e-15)

    # The signs, stated as facts rather than left to the tolerance: due north on the horizon is
    # +north, due east is +east, the zenith is +up. A basis with east and west swapped passes every
    # norm check ever written and points the telescope at the wrong half of the sky.
    def at(alt_deg, az_deg):
        i = np.argmin((d["alt_deg"] - alt_deg) ** 2 + (d["az_deg"] - az_deg) ** 2)
        return d["north"][i], d["east"][i], d["up"][i]

    n, e, up = at(0.0, 0.0)
    check("due north on the horizon is +north", n, 1.0, 1e-12)
    n, e, up = at(0.0, 90.0)
    check("due east on the horizon is +east", e, 1.0, 1e-12)
    n, e, up = at(90.0, 0.0)
    check("the zenith is +up", up, 1.0, 1e-12)


def parsing():
    """Coordinate entry, against astropy's own parser."""
    print("\n5. Coordinate parsing, against astropy SkyCoord")
    with open("exo_parsing.csv") as f:
        rows = list(csv.DictReader(f))

    for row in rows:
        label = row["label"]
        ok = row["ok"] == "1"
        expect_failure = label in ("unparseable RA", "declination out of range", "minutes out of range")

        if expect_failure:
            check(f"rejects {label}", 0.0 if not ok else 1.0, 0.0, 0.0)
            continue

        if not ok:
            failures.append(f"failed to parse {label}")
            print(f"  [FAIL] {label}: refused a coordinate astropy accepts")
            continue

        ra_text, dec_text = row["ra_text"], row["dec_text"]
        unit = (u.hourangle, u.deg) if any(c in ra_text for c in " :h") else (u.deg, u.deg)
        ref = SkyCoord(ra_text, dec_text, unit=unit)

        dra = abs(wrap180(float(row["ra_deg"]) - ref.ra.deg))
        ddec = abs(float(row["dec_deg"]) - ref.dec.deg)
        check(f"{label}: RA", dra, 0.0, 1e-9, " deg")
        check(f"{label}: Dec", ddec, 0.0, 1e-9, " deg")

    # And the formatting round trip: what the mod prints must parse back to what it printed.
    print("\n6. Formatting round trip")
    worst = 0.0
    for row in rows:
        if row["ok"] != "1" or not row["formatted"]:
            continue
        ref = SkyCoord(row["formatted"], unit=(u.hourangle, u.deg))
        worst = max(worst,
                    abs(wrap180(float(row["ra_deg"]) - ref.ra.deg)),
                    abs(float(row["dec_deg"]) - ref.dec.deg))
    # Formatted to 0.1 s in RA and 1" in Dec, so the round trip closes to the printed precision.
    check("what it prints parses back to what it printed", worst, 0.0, 1.0 / 3600.0, " deg")


def main():
    print(__doc__.split("Run:")[0].strip())
    equatorial()
    basis()
    parsing()

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
