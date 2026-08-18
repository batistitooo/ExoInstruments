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

Gaia's own measured counts say what a real one costs. Re-queried against gaiadr3.gaia_source
on 2026-08-07, cumulative, at this format's CURRENT 14 bytes/star:

    G < 12    3.09M stars     43 MB
    G < 13    7.37M stars    103 MB
    G < 14   16.8M stars     236 MB
    G < 15   36.9M stars     517 MB
    G < 16   78.0M stars     1.1 GB

An earlier version of this table was wrong in both columns and is worth naming, because a
catalogue that is a magnitude shallower than its label looks like a bug in the renderer
rather than a bug in a comment. Every magnitude was labelled one too low (the row reading
"G < 13  16.8M" is really G < 14), and every size assumed the version-2 record of 12
bytes/star, which the reddening column has since made 14. A file built at --gmax 13 holds
7,369,627 stars, which is the correct count for that cut and not a truncated download.

None of that can go in a mod download. It CAN sit on the disk of someone who wants it,
which is what this tool is for: run it once, drop the result in PluginData, and frames
get a real star field. Without it the sky behind a photographed body is simply empty,
which is at least honestly empty rather than misleadingly sparse.

CHOOSING A DEPTH
----------------
The whole file is held in RAM as five parallel arrays (RA, dec, V, B-V, E(B-V)) totalling
the same 14 bytes/star, so the table above is also the memory cost. Some guidance rather
than a recommendation, since it depends on the machine:

  * G < 13 (103 MB) is a safe first try and already ~3x Tycho-2's density.
  * G < 14 (236 MB) is the deepest most machines will want alongside KSP itself.
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
    python3 pack_gaia_catalog.py --gmax 13 --out GaiaStarCatalog.starcat --user YOUR_ESA_USERNAME

No third-party packages: the ESA archive speaks TAP, which is plain HTTP, so this runs on a
stock Python 3.

REGISTER FIRST, at https://cosmos.esa.int/web/gaia-users/register, and pass --user. This is
not a nicety. Anonymous access is the archive's degraded mode and hits a wall that neither
subdividing nor retrying gets past: measured on Gaia DR3, one source_id range whose COUNT
answers in 5 s, holding 2.6M rows of which 38,179 pass G < 13, fails its data fetch at 116 s
on EVERY attempt, while the range next to it -- 2.2M rows, 32,261 selected -- returns in 7 s.
Same size, same selectivity. The planner picks a scan for some ranges and the anonymous job
limit kills it before it finishes. A registered account raises that limit.

The password is never taken on the command line, which would put it in shell history: it is
prompted for without echo, or read from the GAIA_PASSWORD environment variable if you set one.

The sky is fetched in source_id slices, since one job cannot return tens of millions of rows.
Each range is COUNTED first (cheap, transfers nothing) and split until it fits, so dense sky
near the Galactic plane subdivides further than empty sky near the poles without any tuning.
Every completed range is cached to <out>.cache before being used, so a run that dies resumes
instead of starting over; delete that directory to start fresh.

    python3 pack_gaia_catalog.py --gmax 13 --out GaiaStarCatalog.starcat --cone 266.4 -29.0 1.0

restricts to a cone (RA, Dec, radius in degrees) instead, which is what the test in
tools/bandpass-wcs-tests uses and is a quick way to check the pipeline end to end.

    python3 pack_gaia_catalog.py --gmax 13 --out GaiaStarCatalog.starcat --from-cache

repacks from a cache a completed run already downloaded, with no network at all. That is the
mode to use after a change to this file, and it is separate from the resume above because the
cache stores only the LEAVES of the source_id subdivision: an ordinary re-run still has to ask
the archive for every COUNT before it can tell that a range was ever subdivided, which is hours
of queries to produce a file it already has the rows for. --from-cache first checks the cached
slices tile the whole sky and refuses if they do not, since a cache with a hole packs a sky
with a hole. Adding --cone to it cuts a small test catalogue out of the same rows.

    python3 pack_gaia_catalog.py --reindex old.starcat --out fixed.starcat

repairs the declination index of a catalogue that already exists, keeping every record exactly
as it is and changing only their order and the offset table. Every star carries its own
declination, so a file whose index is wrong can be rebuilt from itself in seconds, which beats
re-downloading hours of Gaia to recover numbers already on the disk. It is also the only way
back when the cache that built the file is gone or predates a column.

WHAT THE INDEX IS AND WHY IT IS CHECKED
---------------------------------------
Stars are stored sorted into 0.1 degree declination bands, and RenderedStarCatalog.Search reads
ONLY the bands the requested cone overlaps. That makes a wrong band index the quietest possible
failure: the file loads, reports its full star count, decodes every record correctly, and returns
nothing from every search, so the sky renders empty with no error logged anywhere and an empty
frame looks exactly like a genuinely empty field.

Version 3 shipped in that state. The reddening column was inserted into the middle of the tuple
the band sort read from, which moved the declination from index 4 to index 5 while the sort went
on reading index 4, so the catalogue was binned by REDDENING: 91 of 1800 bands held anything and
4.87 million stars, two thirds of the file, sat in the band at dec +89.9. The record for every
star was correct. Nothing threw. Every star field was empty.

So the index is now built in one place, checked against every star's own declination before
anything is written, and the packer refuses to write a file that fails. tools/bandpass-wcs-tests
additionally measures the density of a cone toward the Galactic centre on the installed
catalogue, which is the check that would have caught this the day it appeared.

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
import collections
import csv
import getpass
import http.client
import http.cookiejar
import io
import math
import os
import re
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
VERSION = 3

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

# Interstellar reddening, stored as an unsigned millimagnitude with a sentinel for "not
# estimated". Version 3 added it, and the reason is that everything else in this file
# writes OBSERVED photometry: Gaia's G and BP-RP are reddened, and nothing here deredden
# them. Downstream that left a hot star behind dust indistinguishable from a cool star, and
# the pipeline modelled it as the cool one. E(B-V) is what separates the two.
#
# It is Gaia's own estimate, not a dust map's. gspphot fits an atmosphere model to the
# star's own BP/RP spectrum and parallax and reports the extinction that fit implies, so it
# is per-source and needs no distance of ours. Where gspphot has no solution the field is
# the sentinel, the star is drawn exactly as version 2 drew it, and that is honest rather
# than filled in from a sight-line average that would be wrong for a foreground star.
EBV_UNKNOWN = 65535
EBV_MAX_MAG = 10.0   # anything above this is a fit failure rather than a sight line

# gspphot reports A_0, the monochromatic extinction at 547.7 nm, and E(BP-RP). A_0 is the
# closer of the two to A(V) and converting it needs only R_V: E(B-V) = A_0 / R_V with the
# Galactic average 3.1, the same value Core/InterstellarExtinction uses and the one every
# all-sky map is calibrated to.
GALACTIC_RV = 3.1

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


def reddening_milli(a0):
    """gspphot's A_0 as a packed E(B-V), or the sentinel when there is no usable estimate."""
    if a0 is None:
        return EBV_UNKNOWN
    ebv = a0 / GALACTIC_RV
    if not (0.0 <= ebv <= EBV_MAX_MAG):
        return EBV_UNKNOWN
    milli = int(round(ebv * 1000.0))
    return EBV_UNKNOWN if milli >= EBV_UNKNOWN else milli


# One star, NAMED rather than positional. It is named because the version-3 reddening column
# was inserted into the middle of a plain tuple, which silently moved dec_deg from index 4 to
# index 5 while the band sort still read index 4. The catalogue was then binned by REDDENING:
# 91 of 1800 bands held anything, 4.87M stars sat in the band at dec +89.9 (the E(B-V) sentinel),
# every cone search found nothing, and every star field rendered empty with no error anywhere.
# Field access by name cannot repeat that.
PackedStar = collections.namedtuple(
    "PackedStar", "ra_fixed dec_fixed v_milli bv_milli ebv_milli dec_deg")


def build_star(ra, dec, g, bp_rp, a0=None):
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
    return PackedStar(ra_fixed, dec_fixed, v_milli, bv_milli, reddening_milli(a0), dec)


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

# A G < 13 run makes several hundred jobs over hours, so it WILL meet a bad minute of network.
# Observed: a TCP connect that never completes (WinError 10060) part-way through a run, on a
# slice that then succeeds immediately on restart. That is the network, not the archive refusing
# anything, so it is handled here at the socket rather than by the per-range logic below -- which
# matters because the per-range retry only ever wrapped the data fetch, leaving the COUNT and the
# phase polling bare. Both of those are single points of failure for a multi-hour run.
#
# The body is read inside the retry, so a connection that dies mid-transfer is retried too rather
# than escaping as a short read. The timeout is per socket operation, not per call, so it bounds
# a hang without capping how long a large result may legitimately take to stream.
HTTP_TIMEOUT_S = 120
HTTP_ATTEMPTS = 6
HTTP_BACKOFF_SECONDS = 10


def transient(e):
    """True for failures worth repeating: the network, or a server saying 'not now'."""
    if isinstance(e, urllib.error.HTTPError):
        return e.code in (408, 429, 500, 502, 503, 504)
    return isinstance(e, (OSError, http.client.HTTPException))


def http_call(url, data=None, method=None):
    """
    One HTTP call, retried through transient failures; returns (final URL, body text).

    A 4xx is not retried: a bad query, or a session that has expired, says the same thing however
    many times it is asked. Re-POSTing a job whose response was lost can orphan a job server-side,
    which costs nothing and is cheaper than losing the run.
    """
    for attempt in range(HTTP_ATTEMPTS):
        try:
            request = urllib.request.Request(url, data=data, method=method)
            with _opener.open(request, timeout=HTTP_TIMEOUT_S) as r:
                return r.geturl(), r.read().decode()
        except Exception as e:
            if attempt == HTTP_ATTEMPTS - 1 or not transient(e):
                raise
            wait = HTTP_BACKOFF_SECONDS * (2 ** attempt)
            print(f"      network: {type(e).__name__}; retrying in {wait}s "
                  f"(attempt {attempt + 2}/{HTTP_ATTEMPTS})", flush=True)
            time.sleep(wait)


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
        http_call(TAP_LOGIN, data=body, method="POST")
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

    job, _ = http_call(TAP_ASYNC, data=body, method="POST")
    job = job.split("?")[0]

    # Progress is printed while polling. Without it a slice that legitimately takes minutes is
    # indistinguishable from a hang, which is exactly how this first looked.
    started = time.time()
    while True:
        phase = http_call(job + "/phase")[1].strip()
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
        raise RuntimeError(f"TAP job {phase}: {http_call(job + '/error')[1][:500]}")

    text = http_call(job + "/results/result")[1]
    return text if want_text else list(csv.DictReader(io.StringIO(text)))


# HEALPix level 12 index space, shifted the way Gaia defines source_id. Splitting this range
# splits the sky, because HEALPix is equal-area and source_id is ordered by it.
SOURCE_ID_MAX = 12 * (4 ** 12) * (2 ** 35)


# Rows per job. The archive refuses anonymous jobs above some undocumented size: measured on
# Gaia DR3, a range holding 245,910 rows fails server-side after ~116 s with a bare ERROR, while
# 12,937 rows returns in one second. This sits well inside the known-good side of that gap.
MAX_ROWS_PER_JOB = 50_000

# Retries per range, and the first back-off. These cover transient refusals; they do NOT cover
# the deterministic anonymous-access wall described in the header, which no retry gets past.
MAX_ATTEMPTS = 5
BACKOFF_SECONDS = 20


def cache_path(cache_dir, gmax, lo, hi):
    return os.path.join(cache_dir, f"g{gmax}_{lo}_{hi}.csv")


# --- The declination band index -------------------------------------------------------
# Every cone search stands on this. RenderedStarCatalog.Search reads ONLY the bands the
# requested cone's declination range overlaps, so a star filed under the wrong band is a
# star that no search can ever return, and a whole catalogue filed wrongly renders an empty
# sky. Nothing throws: the file loads, reports its full star count, and decodes every record
# correctly. That is why the index is both built and checked here, in one place.

def band_of(dec_deg):
    """The declination band a star belongs to. Takes DEGREES, and nothing else."""
    return min(DEC_BAND_COUNT - 1, max(0, int((dec_deg + 90.0) / DEC_BAND_WIDTH_DEG)))


def build_band_index(stars):
    """Orders the stars by band and returns the offset table the reader indexes with.

    Sorted by band, then by the raw fixed-point RA, which is monotonic in RA, so the runtime
    binary-searches the integers directly.

    The key carries the WHOLE record (s[:5] begins with ra_fixed, so the RA ordering is the same
    one) rather than stopping at the RA. Two stars can share a band and a fixed-point RA, and a
    key that stops there leaves their order to Python's stable sort, which means to the order the
    rows happened to arrive in: repacking the same stars from a different source then produces a
    file that differs in a couple of bytes for no reason anyone can see. Both files are correct,
    which is exactly what makes the difference expensive to chase. Sorting on the full record
    makes the output depend only on the set of stars, so a rebuild is byte-reproducible and a
    diff against a known-good catalogue means something.
    """
    stars.sort(key=lambda s: (band_of(s.dec_deg), s[:5]))

    counts = [0] * DEC_BAND_COUNT
    for s in stars:
        counts[band_of(s.dec_deg)] += 1

    band_start = [0] * (DEC_BAND_COUNT + 1)
    total = 0
    for b in range(DEC_BAND_COUNT):
        band_start[b] = total
        total += counts[b]
    band_start[DEC_BAND_COUNT] = total
    return band_start


def band_index_fault(stars, band_start):
    """Names what is wrong with a freshly built index, or returns None when it is sound.

    This is the exact check, not a heuristic: it re-derives every star's band from that star's
    own declination and compares it with the slot the index actually put it in. It is O(n) over
    data already in memory, which is nothing against the hours the download took, and it holds
    for a --cone build as well as an all-sky one.
    """
    if band_start[DEC_BAND_COUNT] != len(stars):
        return (f"the band offsets end at {band_start[DEC_BAND_COUNT]} but there are "
                f"{len(stars)} stars")

    band = 0
    previous_ra = -1
    for i, s in enumerate(stars):
        while band < DEC_BAND_COUNT - 1 and i >= band_start[band + 1]:
            band += 1
            previous_ra = -1
        belongs = band_of(s.dec_deg)
        if belongs != band:
            return (f"star {i} at dec {s.dec_deg:.4f} deg is indexed under band {band} "
                    f"(dec {-90.0 + band * DEC_BAND_WIDTH_DEG:.1f}) but belongs in band {belongs}. "
                    "No cone search would ever reach it")
        if s.ra_fixed < previous_ra:
            return f"star {i} breaks the RA ordering inside band {band}, so the binary search is invalid"
        previous_ra = s.ra_fixed
    return None


def header_band_fault(path):
    """The same question asked of a FINISHED file, from its header alone.

    Cheap enough to run on any .starcat before installing it: it reads the offset table and
    nothing else. It cannot re-derive the true bands without the records, so it asks the
    statistical question instead. A real all-sky catalogue fills essentially every band, since
    even the poles hold some stars, and no single 0.1 degree strip can hold a large share of the
    sky. The broken build failed both halves at once: 91 of 1800 bands populated, and 66% of the
    file in one band.

    Returns a sentence naming the problem, or None. Only meaningful for an ALL-SKY catalogue: a
    --cone build legitimately touches a handful of bands.
    """
    with open(path, "rb") as f:
        if f.read(len(MAGIC)) != MAGIC:
            return "not an ExoInstruments packed star catalogue"
        version, count = struct.unpack("<II", f.read(8))
        band_count, band_width = struct.unpack("<If", f.read(8))
        if count == 0 or band_count == 0:
            return None
        band_start = struct.unpack(f"<{band_count + 1}I", f.read(4 * (band_count + 1)))

    populated = sum(1 for b in range(band_count) if band_start[b + 1] > band_start[b])
    sizes = [(band_start[b + 1] - band_start[b], b) for b in range(band_count)]
    biggest, biggest_band = max(sizes)
    share = biggest / count
    if populated >= band_count // 4 and share < 0.10:
        return None

    return (f"the declination index is broken: {populated} of {band_count} bands hold anything, "
            f"and {biggest:,} stars ({share:.0%} of the file) sit in one band at dec "
            f"{-90.0 + biggest_band * band_width:.1f} deg. Every cone search reads only the bands "
            "its field overlaps, so this renders an EMPTY sky with no error, here and in the game. "
            "Rebuild it with tools/pack_gaia_catalog.py.")


def with_retries(what, action):
    """
    Repeat a whole TAP job through a refusal the archive may not repeat.

    This wraps the job, not the HTTP call: http_call already covers the socket, and what is left
    here is the archive killing a job server-side, which comes back as a COMPLETED-less phase, not
    as a network error. Both the count and the fetch go through it -- the count used to be bare,
    which is enough to end a run that has already spent hours getting there.
    """
    for attempt in range(MAX_ATTEMPTS):
        try:
            return action()
        except Exception:
            if attempt == MAX_ATTEMPTS - 1:
                raise
            wait = BACKOFF_SECONDS * (2 ** attempt)
            print(f"      {what} refused; backing off {wait}s and asking again "
                  f"(attempt {attempt + 2}/{MAX_ATTEMPTS})", flush=True)
            time.sleep(wait)


def fetch_range(gmax, lo, hi, columns, cache_dir, depth=0):
    """
    One source_id range: cached, counted, and RETRIED rather than subdivided on failure.

    Retrying rather than subdividing is the right shape for a transient refusal: subdividing
    doubles the number of jobs, which is the wrong response to a server that just said no.

    It does not rescue an anonymous session. Measured on Gaia DR3, a range whose COUNT answers
    in 5 s fails its fetch at 116 s on every attempt while its neighbour of almost identical
    size returns in 7 s, so the wall is deterministic per range, not load-dependent. Use --user.

    Counting first is kept because it costs one cheap job and avoids the ~120 s a genuinely
    oversized range burns before failing. Subdivision is kept only for ranges the count says are
    too big, which is its real purpose.
    """
    cached = cache_path(cache_dir, gmax, lo, hi)
    if os.path.exists(cached):
        fault = cache_columns_fault(cached)
        if fault:
            raise SystemExit(fault)
        with open(cached) as f:
            rows = list(csv.DictReader(f))
        print(f"      cached: {len(rows)} rows", flush=True)
        yield rows
        return

    if depth < 16:
        count_query = (f"SELECT COUNT(*) AS n FROM gaiadr3.gaia_source "
                       f"WHERE phot_g_mean_mag < {gmax} "
                       f"AND source_id >= {lo} AND source_id < {hi}")
        count = int(with_retries("count", lambda: tap_query(count_query, quiet=True))[0]["n"])
        if count == 0:
            open(cached, "w").write("ra,dec,phot_g_mean_mag,bp_rp,ag_gspphot\n")
            return
        if count > MAX_ROWS_PER_JOB:
            mid = (lo + hi) // 2
            yield from fetch_range(gmax, lo, mid, columns, cache_dir, depth + 1)
            yield from fetch_range(gmax, mid, hi, columns, cache_dir, depth + 1)
            return

    query = (f"SELECT {columns} FROM gaiadr3.gaia_source WHERE phot_g_mean_mag < {gmax} "
             f"AND source_id >= {lo} AND source_id < {hi}")
    text = with_retries("fetch", lambda: tap_query(query, want_text=True))
    with open(cached, "w") as f:   # written before yielding, so a crash still resumes
        f.write(text)
    yield list(csv.DictReader(io.StringIO(text)))


# Every column the CURRENT format needs. A cache written before one of these was added is not a
# usable cache: the missing column reads as empty for every row, which build_star turns into a
# legitimate "not measured" sentinel rather than an error, so the repack succeeds and quietly
# ships a catalogue with a whole column blank. That is not hypothetical. The cache left over from
# the version-2 build holds only ra, dec, phot_g_mean_mag and bp_rp, and repacking version 3 from
# it produced 7,369,627 stars with no reddening estimate at all, against 62.6% that carry one.
REQUIRED_COLUMNS = ("ra", "dec", "phot_g_mean_mag", "bp_rp", "ag_gspphot")


def cache_columns_fault(path):
    """Names a cached slice written before a column this format needs, or None when it is usable."""
    with open(path) as f:
        present = {c.strip() for c in f.readline().strip().split(",")}
    missing = [c for c in REQUIRED_COLUMNS if c not in present]
    if not missing:
        return None
    return (f"{os.path.basename(path)} was downloaded without {', '.join(missing)}, so it predates "
            f"catalogue version {VERSION}. Every row in it would pack as 'not measured' for that "
            "column, silently, and the result would look like a complete catalogue. Delete the "
            "cache directory and download again.")


def cached_ranges(cache_dir, gmax):
    """Every completed slice on disk, checked to tile the whole source_id space exactly.

    A cache with a hole in it packs a sky with a hole in it, which is the same silent failure
    as a bad band index: the file loads, reports a plausible count, and a wedge of sky is simply
    absent. So this verifies the ranges are contiguous from 0 to SOURCE_ID_MAX and refuses to
    proceed otherwise, rather than packing whatever happens to be there.
    """
    pattern = re.compile(r"^g" + re.escape(str(gmax)) + r"_(\d+)_(\d+)\.csv$")
    ranges = sorted((int(m.group(1)), int(m.group(2)), os.path.join(cache_dir, m.group(0)))
                    for m in (pattern.match(n) for n in os.listdir(cache_dir)) if m)
    if not ranges:
        raise SystemExit(f"--from-cache: no completed g{gmax} slices in {cache_dir}")

    covered = 0
    for lo, hi, name in ranges:
        if lo != covered:
            gap = "overlaps" if lo < covered else "leaves a gap before"
            raise SystemExit(f"--from-cache: {os.path.basename(name)} {gap} the slice before it. "
                             "The cache is not a complete tiling of the sky, so it would pack a "
                             "catalogue with a wedge missing. Finish the run online first.")
        covered = hi
    if covered != SOURCE_ID_MAX:
        raise SystemExit(f"--from-cache: the cache covers source_id up to {covered}, not "
                         f"{SOURCE_ID_MAX}. The download is incomplete; finish it online first.")

    for lo, hi, name in ranges:
        fault = cache_columns_fault(name)
        if fault:
            raise SystemExit(f"--from-cache: {fault}")
    return ranges


def within_cone(row, cone):
    """True when a cached row falls inside the requested cone."""
    ra_deg, dec_deg, radius = cone[0], cone[1], cone[2]
    try:
        ra, dec = float(row["ra"]), float(row["dec"])
    except (TypeError, ValueError):
        return False
    d0, d1 = math.radians(dec_deg), math.radians(dec)
    separation = math.sin(d0) * math.sin(d1) + math.cos(d0) * math.cos(d1) * \
        math.cos(math.radians(ra - ra_deg))
    return separation >= math.cos(math.radians(radius))


def fetch(gmax, cone, slices, cache_dir, from_cache=False):
    """Rows from the ESA archive, in source_id slices small enough for one job each."""
    columns = "ra, dec, phot_g_mean_mag, bp_rp, ag_gspphot"

    if from_cache:
        # No network at all. The cache already holds every row a completed run downloaded, so
        # rebuilding after a packer fix costs minutes rather than the hours the download did.
        # This is not a nicety: the resumable cache only stores the LEAVES of the subdivision,
        # so an ordinary re-run still has to ask the archive for every COUNT before it knows a
        # range was subdivided, and a rebuild that needs the network is a rebuild people skip.
        ranges = cached_ranges(cache_dir, gmax)
        print(f"  {len(ranges)} cached slices tiling the whole sky; no network")
        for i, (lo, hi, path) in enumerate(ranges):
            with open(path) as f:
                rows = list(csv.DictReader(f))
            if cone:
                rows = [r for r in rows if within_cone(r, cone)]
            print(f"  cached slice {i + 1}/{len(ranges)}: {len(rows)} rows", flush=True)
            yield rows
        return

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


def write_catalogue(out, stars, all_sky=True):
    """Indexes, checks and writes the catalogue. Returns a process exit status.

    The only writer of this format, so the index every reader depends on is built and checked in
    exactly one place.
    """
    band_start = build_band_index(stars)

    # Checked BEFORE anything is written, because an unwritten file is a visible failure and a
    # wrongly indexed one is not: it loads, counts right, decodes right, and renders nothing.
    fault = band_index_fault(stars, band_start)
    if fault:
        print(f"refusing to write {out}: {fault}", file=sys.stderr)
        return 1

    with open(out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<II", VERSION, len(stars)))
        f.write(struct.pack("<If", DEC_BAND_COUNT, DEC_BAND_WIDTH_DEG))
        f.write(struct.pack(f"<{DEC_BAND_COUNT + 1}I", *band_start))
        record = struct.Struct("<IiHhH")
        f.write(b"".join(
            record.pack(s.ra_fixed, s.dec_fixed, s.v_milli, s.bv_milli, s.ebv_milli)
            for s in stars))

    size_mb = os.path.getsize(out) / (1024 * 1024)
    no_colour = sum(1 for s in stars if s.bv_milli == BV_UNKNOWN)
    no_reddening = sum(1 for s in stars if s.ebv_milli == EBV_UNKNOWN)
    populated = sum(1 for b in range(DEC_BAND_COUNT) if band_start[b + 1] > band_start[b])
    biggest = max(band_start[b + 1] - band_start[b] for b in range(DEC_BAND_COUNT))
    print(f"{len(stars)} stars -> {out} ({size_mb:.1f} MB), {no_colour} without a colour index, "
          f"{no_reddening} without a reddening estimate")
    print(f"declination index: {populated} of {DEC_BAND_COUNT} bands populated, "
          f"largest band {biggest} stars")
    if all_sky:
        # The all-sky shape check as well, so this and setup_data.py agree on what a good file
        # looks like rather than each trusting a different rule.
        shape = header_band_fault(out)
        if shape:
            print(f"refusing to keep {out}: {shape}", file=sys.stderr)
            os.remove(out)
            return 1
    print("Copy it to <KSP>/GameData/ExoInstruments/PluginData/GaiaStarCatalog.starcat")
    return 0


def reindex(path, out):
    """Rewrites an existing catalogue with a correct declination index, keeping every record.

    WHY THIS IS WORTH A MODE. The band index is derived entirely from data already in the file:
    every record carries its own declination, so a catalogue whose index is wrong can be repaired
    from itself, exactly, in seconds. The alternative is re-downloading hours of Gaia to recover
    numbers that are already on the disk, and where the cache that produced the file is gone or
    predates a column, re-downloading does not even reproduce it.

    The records are untouched. Only their ORDER and the offset table change.

    Bands are recomputed from the DECODED declination rather than from the original catalogue's
    float, which is the value the reader itself bands by, so the index and the reader cannot
    disagree about a star sitting on a boundary.
    """
    with open(path, "rb") as f:
        if f.read(len(MAGIC)) != MAGIC:
            raise SystemExit(f"--reindex: {path} is not an ExoInstruments packed star catalogue")
        version, count = struct.unpack("<II", f.read(8))
        band_count, band_width = struct.unpack("<If", f.read(8))
        f.read(4 * (band_count + 1))     # the old index, which is exactly what is being replaced
        payload = f.read()

    if version > VERSION:
        raise SystemExit(f"--reindex: {path} is version {version}, newer than this packer writes")
    if band_count != DEC_BAND_COUNT or abs(band_width - DEC_BAND_WIDTH_DEG) > 1e-6:
        raise SystemExit(f"--reindex: {path} is banded {band_count} x {band_width} deg, not "
                         f"{DEC_BAND_COUNT} x {DEC_BAND_WIDTH_DEG}")

    record = struct.Struct("<IiHhH" if version >= 3 else "<IiHh")
    if len(payload) != count * record.size:
        raise SystemExit(f"--reindex: {path} holds {len(payload)} bytes of records, not the "
                         f"{count * record.size} its header claims. It is truncated.")

    stars = [PackedStar(r[0], r[1], r[2], r[3], r[4] if version >= 3 else EBV_UNKNOWN,
                        r[1] / DEC_SCALE)
             for r in record.iter_unpack(payload)]
    print(f"Reindexing {count} stars from {path} (version {version})")
    if version < VERSION:
        print(f"  version {version} carries no reddening column, so it is written as version "
              f"{VERSION} with every star's E(B-V) unmeasured, which is what it already was")
    return write_catalogue(out, stars, all_sky=True)


def main():
    p = argparse.ArgumentParser(description="Pack Gaia DR3 into ExoInstruments' star catalogue format.")
    p.add_argument("--gmax", type=float, help="faint limit in Gaia G (see the depth table in this file's docstring)")
    p.add_argument("--out", required=True, help="output .bin path")
    p.add_argument("--slices", type=int, default=48, help="source_id slices to split the query into; each is subdivided further if the archive refuses it")
    p.add_argument("--user", help="ESA archive username. Strongly recommended: anonymous access "
                                  "hits a job wall that no amount of retrying gets past. The "
                                  "password is prompted for, or read from GAIA_PASSWORD.")
    p.add_argument("--cache", help="directory for completed slices, so a restart resumes (default: <out>.cache)")
    p.add_argument("--cone", nargs=3, type=float, metavar=("RA", "DEC", "RADIUS"),
                   help="restrict to a cone in degrees, for testing the pipeline end to end")
    p.add_argument("--from-cache", action="store_true",
                   help="repack from an already-downloaded cache without touching the network. "
                        "Refuses to run unless the cached slices tile the whole sky.")
    p.add_argument("--reindex", metavar="CATALOGUE",
                   help="rebuild the declination index of an existing catalogue into --out, "
                        "keeping every record. Repairs a mis-indexed file without re-downloading.")
    args = p.parse_args()

    if args.reindex:
        return reindex(args.reindex, args.out)
    if args.gmax is None:
        p.error("--gmax is required unless --reindex is given")

    if args.from_cache:
        print("Repacking from the local cache; the ESA archive is not contacted.")
    elif args.user:
        login(args.user)
    else:
        print("No --user given, so this runs anonymously. Expect ranges that fail at ~2 minutes "
              "and cannot be retried past it; see this file's header.")

    print(f"Querying Gaia DR3 for G < {args.gmax}")
    stars = []
    cache_dir = args.cache or (args.out + ".cache")
    os.makedirs(cache_dir, exist_ok=True)
    print(f"Resumable cache: {cache_dir} (delete it to start over)")
    for table in fetch(args.gmax, args.cone, args.slices, cache_dir, args.from_cache):
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
            star = build_star(value("ra"), value("dec"), value("phot_g_mean_mag"),
                              value("bp_rp"), value("ag_gspphot"))
            if star:
                stars.append(star)
        print(f"    {len(stars)} cumulative", flush=True)

    if not stars:
        print("no usable rows returned", file=sys.stderr)
        return 1

    return write_catalogue(args.out, stars, all_sky=not args.cone)


if __name__ == "__main__":
    sys.exit(main())
