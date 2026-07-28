#!/usr/bin/env python3
"""
Packs Gaia DR3 into the same compact binary RenderedStarCatalog reads, as an OPTIONAL
replacement for the shipped Tycho-2 catalogue.

WHY NOTHING SHIPS
-----------------
The mod ships NO rendered star catalogue at all. It used to ship Tycho-2, 29.3 MB for
2.5M stars complete to V~11.5, which is 61.9 stars/deg^2: about 4 real stars in an RC20
frame where a real 30 s sub holds hundreds. That is not a star field, it is a rounding
error, and carrying 29.3 MB to deliver it was the worst of both worlds. The choice is now
a real star field or none.

Gaia's own measured counts say what a real one costs (queried from gaiadr3.gaia_source,
cumulative, at this format's 12 bytes/star):

    G < 13   16.8M stars    202 MB
    G < 14   36.9M stars    443 MB
    G < 15   78.0M stars    935 MB
    G < 16  157.7M stars    1.9 GB
    G < 18  577.2M stars    6.9 GB

None of that can go in a mod download. It CAN sit on the disk of someone who wants it,
which is what this tool is for: run it once, drop the result in PluginData, and frames
get a real star field. Without it the sky behind a photographed body is simply empty,
which is at least honestly empty rather than misleadingly sparse.

CHOOSING A DEPTH
----------------
The whole file is held in RAM as four parallel arrays, so the table above is also the
memory cost. Some guidance rather than a recommendation, since it depends on the machine:

  * G < 13 (202 MB) is a safe first try and already ~7x Tycho-2's density.
  * G < 14 (443 MB) is the deepest most machines will want alongside KSP itself.
  * G < 15 and beyond are for people who know what their RAM is doing.

Search cost does NOT scale with catalogue size: the format is banded by declination and
binary-searched in RA, so a cone search touches only the stars near the field. What DOES
scale is the number of stars rendered per frame, which is the point of doing this.

PHOTOMETRY
----------
Gaia measures G, G_BP and G_RP; the mod works in Johnson V and B-V throughout. The
conversion uses Gaia's OWN published relations, DR3 documentation Table 5.9, the same
polynomials Core/GaiaPhotometry.cs applies at runtime:

    G - V         = -0.02704 + 0.01424*x - 0.2156*x^2 + 0.01426*x^3,  x = G_BP - G_RP
    G_BP - G_RP   = -0.03298 + 1.259*y   - 0.1155*y^2 + 0.0364*y^3,   y = B - V

valid for -0.5 < x < 5.0 (Table 5.10), scatter 0.03017 mag. B-V is obtained by inverting
the second polynomial numerically, because Gaia publishes only that direction. A star with
no measured colour keeps its G as V and is flagged as colourless rather than given an
invented colour.

PROPER MOTIONS are dropped: the finest plate scale modelled here is ~0.03 arcsec/px and
Gaia's proper motions would move a typical field star by one pixel every few years of
in-game time.

USAGE
-----
    python3 pack_gaia_catalog.py --gmax 13 --out GaiaStarCatalog.starcat

No third-party packages: the ESA archive speaks TAP, which is plain HTTP, so this runs on a
stock Python 3.

The sky is fetched in source_id slices, because a single anonymous job cannot return tens of
millions of rows. Each range is COUNTED first (cheap, transfers nothing) and split until it
fits, so dense sky near the Galactic plane subdivides further than empty sky near the poles
without any tuning.

BE PATIENT, AND EXPECT TO RETRY. The anonymous archive is not only size-limited but appears
to throttle: ranges that returned in one second when tried in isolation failed after ~112 s
when run back to back. A full G < 13 pack is therefore best measured in hours rather than
minutes, and may need to be restarted. Two ways to make that better, neither done here:
  * An ESA archive account raises the anonymous limits substantially. The TAP endpoint takes
    a login; adding that here means the tool prompting for your own credentials.
  * Caching each completed slice to disk so a restart resumes rather than begins again.

    python3 pack_gaia_catalog.py --gmax 13 --out GaiaStarCatalog.starcat --cone 266.4 -29.0 1.0

restricts to a cone (RA, Dec, radius in degrees) instead, which is what the test in
tools/bandpass-wcs-tests uses and is a quick way to check the pipeline end to end.

Then copy the result to:
    <KSP>/GameData/ExoInstruments/PluginData/GaiaStarCatalog.starcat

The .starcat extension is deliberate and must not be changed to .bin. Kopernicus walks
GameData and tries to read every *.bin it finds as a scaled-space mesh; a real KSP.log
shows it doing exactly that to this mod's old catalogue:

    [Kopernicus] Could not load '.../ExoInstruments/PluginData/RenderedStarCatalog.bin'
    [Kopernicus] Loaded '.../ParallaxContinued/Models/ScaledMesh.bin'

Harmless at 30 MB. Not harmless when the file is 200-450 MB and gets read at every
startup before failing.
That is the only star catalogue the renderer looks for. See the README.
"""

import argparse
import csv
import getpass
import http.cookiejar
import io
import math
import os
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# --- The packed format ----------------------------------------------------------------
# These used to be imported from the Tycho-2 packer, which was the only other writer of
# this format. That packer is gone, so they live here now, and this file is the single
# definition of the format Core/RenderedStarCatalog.cs reads. Keep VERSION in step with
# RenderedStarCatalog.FormatVersion.
MAGIC = b"EXOSTAR1"
VERSION = 2

# Positions are stored as fixed-point integers over the full turn rather than as float32
# degrees. A float32 near RA = 360 deg has an ULP of 2.1e-5 deg = 0.077 arcsec, which is a
# fourteenth of a pixel on the RC20 but FORTY-THREE pixels at SPHERE/ZIMPOL's ~1.8 mas
# plate scale, where the same star would land in a visibly wrong place on the
# highest-resolution instrument in the roster. Fixed point over 32 bits gives a uniform
# 360/2^32 = 8.4e-8 deg = 0.3 mas everywhere, six times finer than ZIMPOL's own pixel, at
# exactly the same four bytes.
RA_SCALE = 2 ** 32 / 360.0
DEC_SCALE = 2 ** 32 / 180.0

DEC_BAND_WIDTH_DEG = 0.1
DEC_BAND_COUNT = int(round(180.0 / DEC_BAND_WIDTH_DEG))

# V magnitude is stored as an unsigned millimagnitude offset by this much, so the
# brightest real star (Sirius, V = -1.46) still lands on a positive value.
V_MAG_OFFSET = 2.0
BV_UNKNOWN = -32768  # sentinel: this star has no Gaia colour, so its B-V is unknown

# --- Gaia DR3 Table 5.9, exactly as published ---------------------------------------
G_MINUS_V = (-0.02704, 0.01424, -0.2156, 0.01426)     # in (G_BP - G_RP)
BPRP_FROM_BV = (-0.03298, 1.259, -0.1155, 0.0364)     # in (B - V)
BPRP_MIN, BPRP_MAX = -0.5, 5.0


def poly(coefficients, x):
    result, power = 0.0, 1.0
    for c in coefficients:
        result += c * power
        power *= x
    return result


def g_minus_v(bp_rp):
    """Gaia's published G - V, clamped rather than extrapolated outside its validity range."""
    return poly(G_MINUS_V, max(BPRP_MIN, min(BPRP_MAX, bp_rp)))


def b_minus_v(bp_rp):
    """B-V by inverting Gaia's published (BP-RP)(B-V) polynomial; None when out of range."""
    lo, hi = -0.4, 2.0
    f_lo = poly(BPRP_FROM_BV, lo) - bp_rp
    f_hi = poly(BPRP_FROM_BV, hi) - bp_rp
    if f_lo * f_hi > 0.0:
        return None
    for _ in range(60):
        mid = 0.5 * (lo + hi)
        f_mid = poly(BPRP_FROM_BV, mid) - bp_rp
        if f_lo * f_mid <= 0.0:
            hi, f_hi = mid, f_mid
        else:
            lo, f_lo = mid, f_mid
    return 0.5 * (lo + hi)


def build_star(ra, dec, g, bp_rp):
    """One packed record, or None when the row carries no usable position/magnitude."""
    if ra is None or dec is None or g is None:
        return None
    if not (-90.0 <= dec <= 90.0):
        return None

    if bp_rp is None:
        # No colour measured: G is used as V unconverted and the colour is flagged.
        # Honest rather than tidy -- G and V differ by up to 1.5 mag for a red star,
        # so this is a real error bar on that star, not a rounding choice.
        #
        # Reading the archive's CSV directly is what makes this correct. An earlier version
        # went through astroquery, whose tables expose a missing bp_rp as a MASKED value --
        # and float() on a masked entry returns the fill sitting under the mask, not NaN.
        # A NaN guard therefore let it through, and 7 stars of 923 in the test cone were
        # given a colour that had never been measured. An empty CSV field cannot do that.
        v, bv_milli = g, BV_UNKNOWN
    else:
        v = g - g_minus_v(bp_rp)
        bv = b_minus_v(bp_rp)
        bv_milli = BV_UNKNOWN if bv is None else max(-32767, min(32767, int(round(bv * 1000.0))))

    v_milli = int(round((v + V_MAG_OFFSET) * 1000.0))
    if not (0 <= v_milli <= 65535):
        return None

    ra_fixed = int(round((ra % 360.0) * RA_SCALE)) % (2 ** 32)
    dec_fixed = max(-(2 ** 31), min(2 ** 31 - 1, int(round(dec * DEC_SCALE))))
    return (ra_fixed, dec_fixed, v_milli, bv_milli, dec)


# --- ESA archive access, with no third-party dependency -------------------------------
# The Gaia archive speaks TAP, which is plain HTTP: POST a query, poll a phase, GET a CSV.
# astroquery wraps that nicely but is not in a stock Python, and asking someone to install
# a scientific stack to download a star catalogue is a bad trade for three HTTP calls.

TAP_ASYNC = "https://gea.esac.esa.int/tap-server/tap/async"
TAP_LOGIN = "https://gea.esac.esa.int/tap-server/login"

# One opener for the whole run, so the session cookie a login sets is carried by every later
# request. Anonymous use goes through the same opener with no cookie.
_opener = urllib.request.build_opener(
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def login(username):
    """
    Authenticate against the ESA archive.

    WHY THIS IS WORTH THE TROUBLE. Anonymous access is deliberately the degraded mode: limited
    rows, a job wall around two minutes, no server-side storage. Measured against Gaia DR3, that
    wall is not about size and cannot be retried away -- one source_id range whose COUNT answers
    in 5 seconds, holding 2.6M rows of which 38,179 pass G < 13, fails its fetch at 116 s on
    every attempt, while the adjacent range of 2.2M rows returns in 7 s. The planner picks a
    scan for some ranges and the job is killed before it finishes. A registered account raises
    the limits this runs into.

    The password is NEVER taken on the command line: that would put it in shell history. It comes
    from the GAIA_PASSWORD environment variable if you set one, otherwise from a prompt that does
    not echo.
    """
    password = os.environ.get("GAIA_PASSWORD")
    if not password:
        password = getpass.getpass(f"ESA archive password for {username} (not echoed): ")

    body = urllib.parse.urlencode({"username": username, "password": password}).encode()
    try:
        with _opener.open(urllib.request.Request(TAP_LOGIN, data=body, method="POST")) as r:
            if r.status not in (200, 303):
                raise RuntimeError(f"login returned HTTP {r.status}")
    except urllib.error.HTTPError as e:
        raise SystemExit(f"ESA archive login failed for '{username}': HTTP {e.code}. "
                         "Check the username and password, and that the account is activated "
                         "(registration sends a confirmation mail).") from None
    finally:
        del password

    print(f"Logged in to the ESA archive as {username}.")


def tap_query(adql, timeout_s=3600, quiet=False, want_text=False):
    """Run one ADQL query as an async TAP job and return its rows as dicts."""
    body = urllib.parse.urlencode({
        "REQUEST": "doQuery", "LANG": "ADQL", "FORMAT": "csv",
        "PHASE": "RUN", "QUERY": adql,
    }).encode()

    with _opener.open(urllib.request.Request(TAP_ASYNC, data=body, method="POST")) as r:
        job = r.geturl().split("?")[0]

    # Progress is printed while polling. Without it a slice that legitimately takes minutes is
    # indistinguishable from a hang, which is exactly how this first looked.
    started = time.time()
    while True:
        with _opener.open(job + "/phase") as r:
            phase = r.read().decode().strip()
        elapsed = time.time() - started
        if phase in ("COMPLETED", "ERROR", "ABORTED"):
            if not quiet or phase != "COMPLETED":
                print(f"      {phase.lower()} after {elapsed:.0f}s", flush=True)
            break
        if elapsed > timeout_s:
            raise RuntimeError(f"TAP job {job} still {phase} after {timeout_s}s")
        if not quiet:
            print(f"      {phase.lower()}... {elapsed:.0f}s", end="\r", flush=True)
        time.sleep(3)

    if phase != "COMPLETED":
        with _opener.open(job + "/error") as r:
            raise RuntimeError(f"TAP job {phase}: {r.read().decode()[:500]}")

    with _opener.open(job + "/results/result") as r:
        text = r.read().decode()
    return text if want_text else list(csv.DictReader(io.StringIO(text)))


# HEALPix level 12 index space, shifted the way Gaia defines source_id. Splitting this range
# splits the sky, because HEALPix is equal-area and source_id is ordered by it.
SOURCE_ID_MAX = 12 * (4 ** 12) * (2 ** 35)


# Rows per job. The archive refuses anonymous jobs above some undocumented size: measured on
# Gaia DR3, a range holding 245,910 rows fails server-side after ~116 s with a bare ERROR, while
# 12,937 rows returns in one second. This sits well inside the known-good side of that gap.
MAX_ROWS_PER_JOB = 50_000

# Retries per range, and the first back-off. The archive throttles rather than rejecting on
# size, so the answer to a refusal is to wait and ask for the same thing again.
MAX_ATTEMPTS = 5
BACKOFF_SECONDS = 20


def cache_path(cache_dir, gmax, lo, hi):
    return os.path.join(cache_dir, f"g{gmax}_{lo}_{hi}.csv")


def fetch_range(gmax, lo, hi, columns, cache_dir, depth=0):
    """
    One source_id range: cached, counted, and RETRIED rather than subdivided on failure.

    Retrying rather than subdividing is the correction that matters. The archive's refusals are
    not about size -- a range that failed inside a long run returned 32,261 rows in 14 s when
    tried again in isolation minutes later. It throttles. Subdividing a refused range therefore
    does exactly the wrong thing: it doubles the number of jobs against a server that is already
    saying "too many", and the run spirals into ever smaller pieces, each still refused. Backing
    off and asking again for the SAME range is what actually works.

    Counting first is kept, because it costs one cheap job and avoids the ~120 s a genuinely
    oversized range burns before failing. Subdivision is kept only for ranges the count says are
    too big, which is its real purpose.
    """
    cached = cache_path(cache_dir, gmax, lo, hi)
    if os.path.exists(cached):
        with open(cached) as f:
            rows = list(csv.DictReader(f))
        print(f"      cached: {len(rows)} rows", flush=True)
        yield rows
        return

    if depth < 16:
        count = int(tap_query(f"SELECT COUNT(*) AS n FROM gaiadr3.gaia_source "
                              f"WHERE phot_g_mean_mag < {gmax} "
                              f"AND source_id >= {lo} AND source_id < {hi}", quiet=True)[0]["n"])
        if count == 0:
            open(cached, "w").write("ra,dec,phot_g_mean_mag,bp_rp\n")
            return
        if count > MAX_ROWS_PER_JOB:
            mid = (lo + hi) // 2
            yield from fetch_range(gmax, lo, mid, columns, cache_dir, depth + 1)
            yield from fetch_range(gmax, mid, hi, columns, cache_dir, depth + 1)
            return

    query = (f"SELECT {columns} FROM gaiadr3.gaia_source WHERE phot_g_mean_mag < {gmax} "
             f"AND source_id >= {lo} AND source_id < {hi}")
    for attempt in range(MAX_ATTEMPTS):
        try:
            text = tap_query(query, want_text=True)
            with open(cached, "w") as f:   # written before yielding, so a crash still resumes
                f.write(text)
            yield list(csv.DictReader(io.StringIO(text)))
            return
        except Exception:
            if attempt == MAX_ATTEMPTS - 1:
                raise
            wait = BACKOFF_SECONDS * (2 ** attempt)
            print(f"      refused; backing off {wait}s and asking again "
                  f"(attempt {attempt + 2}/{MAX_ATTEMPTS})", flush=True)
            time.sleep(wait)


def fetch(gmax, cone, slices, cache_dir):
    """Rows from the ESA archive, in source_id slices small enough for one job each."""
    columns = "ra, dec, phot_g_mean_mag, bp_rp"
    if cone:
        ra, dec, radius = cone
        print(f"  cone {ra} {dec} r={radius} deg, G < {gmax}", flush=True)
        yield tap_query(f"SELECT {columns} FROM gaiadr3.gaia_source WHERE phot_g_mean_mag < {gmax} "
                        f"AND 1=CONTAINS(POINT('ICRS',ra,dec),CIRCLE('ICRS',{ra},{dec},{radius}))")
        return

    # Sliced on SOURCE_ID rather than declination. source_id encodes the star's HEALPix cell in
    # its high bits and is the table's primary key, so a source_id range is a contiguous index
    # range the archive seeks straight to. A declination range is not: dec is a plain column, so
    # every slice re-scans the table looking for it.
    for i in range(slices):
        lo = SOURCE_ID_MAX * i // slices
        hi = SOURCE_ID_MAX * (i + 1) // slices
        print(f"  slice {i + 1}/{slices}", flush=True)
        yield from fetch_range(gmax, lo, hi, columns, cache_dir)


def main():
    p = argparse.ArgumentParser(description="Pack Gaia DR3 into ExoInstruments' star catalogue format.")
    p.add_argument("--gmax", type=float, required=True, help="faint limit in Gaia G (see the depth table in this file's docstring)")
    p.add_argument("--out", required=True, help="output .bin path")
    p.add_argument("--slices", type=int, default=48, help="source_id slices to split the query into; each is subdivided further if the archive refuses it")
    p.add_argument("--user", help="ESA archive username. Strongly recommended: anonymous access "
                                  "hits a job wall that no amount of retrying gets past. The "
                                  "password is prompted for, or read from GAIA_PASSWORD.")
    p.add_argument("--cache", help="directory for completed slices, so a restart resumes (default: <out>.cache)")
    p.add_argument("--cone", nargs=3, type=float, metavar=("RA", "DEC", "RADIUS"),
                   help="restrict to a cone in degrees, for testing the pipeline end to end")
    args = p.parse_args()

    if args.user:
        login(args.user)
    else:
        print("No --user given, so this runs anonymously. Expect ranges that fail at ~2 minutes "
              "and cannot be retried past it; see this file's header.")

    print(f"Querying Gaia DR3 for G < {args.gmax}")
    stars = []
    cache_dir = args.cache or (args.out + ".cache")
    os.makedirs(cache_dir, exist_ok=True)
    print(f"Resumable cache: {cache_dir} (delete it to start over)")
    for table in fetch(args.gmax, args.cone, args.slices, cache_dir):
        for row in table:
            def value(name):
                v = row.get(name)
                if v is None or v == "":
                    return None
                try:
                    f = float(v)
                except ValueError:
                    return None
                return None if f != f else f   # NaN-safe
            star = build_star(value("ra"), value("dec"), value("phot_g_mean_mag"), value("bp_rp"))
            if star:
                stars.append(star)
        print(f"    {len(stars)} cumulative", flush=True)

    if not stars:
        print("no usable rows returned", file=sys.stderr)
        return 1

    def band_of(dec):
        return min(DEC_BAND_COUNT - 1, max(0, int((dec + 90.0) / DEC_BAND_WIDTH_DEG)))

    # Sorted by band, then by the raw fixed-point RA, which is monotonic in RA, so the
    # runtime binary-searches the integers directly.
    stars.sort(key=lambda s: (band_of(s[4]), s[0]))

    band_start = [0] * (DEC_BAND_COUNT + 1)
    counts = [0] * DEC_BAND_COUNT
    for s in stars:
        counts[band_of(s[4])] += 1
    total = 0
    for b in range(DEC_BAND_COUNT):
        band_start[b] = total
        total += counts[b]
    band_start[DEC_BAND_COUNT] = total

    with open(args.out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<II", VERSION, len(stars)))
        f.write(struct.pack("<If", DEC_BAND_COUNT, DEC_BAND_WIDTH_DEG))
        f.write(struct.pack(f"<{DEC_BAND_COUNT + 1}I", *band_start))
        record = struct.Struct("<IiHh")
        f.write(b"".join(record.pack(s[0], s[1], s[2], s[3]) for s in stars))

    size_mb = os.path.getsize(args.out) / (1024 * 1024)
    no_colour = sum(1 for s in stars if s[3] == BV_UNKNOWN)
    print(f"{len(stars)} stars -> {args.out} ({size_mb:.1f} MB), {no_colour} without a colour index")
    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/GaiaStarCatalog.starcat")
    return 0


if __name__ == "__main__":
    sys.exit(main())
