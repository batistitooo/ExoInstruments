"""Test fixtures: a synthetic sky data release, a fault injecting Range server and a fake KSP tree."""

import hashlib
import http.server
import json
import math
import os
import random
import re
import struct
import sys
import tempfile
import threading
import time
import urllib.parse
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

import sky_data_release as sdr  # noqa: E402

PART_SIZE = 64 * 1024
MAIN = "GaiaStarCatalog.starcat"
CUTS = (13, 15, 17, 19)
BAND_COUNT = 1800
BAND_WIDTH = 0.1
HEADER = struct.Struct("<8sIIIf")
RECORD = struct.Struct("<IiHhH")

# Stand ins for the other products: right magic and version, random payload.
PRODUCT_FILES = (
    ("DustMap.dustmap", b"EXODUST1", 1, 40_000),
    ("HalphaMap.emission", b"EXOEMIS1", 1, 40_000),
    ("HalphaPatches.patchset", b"EXOPTCH3", 1, 20_000),
    ("GalaxyCatalog.galcat", b"EXOGALX1", 2, 9_000),
    ("GalaxyImages.galimg", b"EXOGIMG1", 1, 150_000),
)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def make_stars(count, seed):
    """Sorted (band, ra, dec, v, bv, ebv) rows, uniform on the sky, more of them faint."""
    rng = random.Random(seed)
    stars = []
    for _ in range(count):
        ra = rng.uniform(0.0, 360.0)
        dec = math.degrees(math.asin(rng.uniform(-0.9999, 0.9999)))
        v = 4.0 + 17.0 * rng.random() ** (1.0 / 3.0)
        band = min(BAND_COUNT - 1, max(0, int((dec + 90.0) / BAND_WIDTH)))
        stars.append((band, int(ra * 2 ** 32 / 360.0) & 0xFFFFFFFF, int(dec * 2 ** 32 / 180.0),
                      int(round((v + 2.0) * 1000)), rng.randint(-300, 2000), rng.randint(0, 500)))
    stars.sort()
    return stars


def catalogue_bytes(stars):
    """An EXOSTAR1 version 3 file with its declination band index."""
    counts = [0] * BAND_COUNT
    for star in stars:
        counts[star[0]] += 1
    starts = [0]
    for c in counts:
        starts.append(starts[-1] + c)
    return b"".join([HEADER.pack(b"EXOSTAR1", 3, len(stars), BAND_COUNT, BAND_WIDTH),
                     struct.pack(f"<{BAND_COUNT + 1}I", *starts),
                     b"".join(RECORD.pack(*s[1:]) for s in stars)])


class Release:
    def __init__(self, files, manifest, assets):
        self.files = files
        self.manifest = manifest
        self.assets = assets
        self.manifest_bytes = assets[sdr.MANIFEST_ASSET]
        self.manifest_sha256 = sha(self.manifest_bytes)
        self.entries = {entry["name"]: entry for entry in manifest["files"]}

    def variant(self, name):
        return list(self.manifest["variants"][name])

    def parts(self, name):
        return self.entries[name]["parts"]

    def assets_of(self, names):
        return [part["asset"] for name in names for part in self.parts(name)]

    def sha(self, name):
        return self.entries[name]["sha256"]


def build_release(stars=20_000, seed=7, part_size=PART_SIZE, without=()):
    rng = random.Random(seed)
    rows = make_stars(stars, seed)
    files = {}
    for cut in CUTS:
        files[f"GaiaStarCatalog.V{cut}.starcat"] = catalogue_bytes(
            [s for s in rows if s[3] <= (cut + 2) * 1000])
    files[MAIN] = catalogue_bytes(rows)
    for name, magic, version, size in PRODUCT_FILES:
        if name not in without:
            files[name] = magic + struct.pack("<I", version) + rng.randbytes(size - 12)

    entries, assets = [], {}
    for name, data in files.items():
        count = -(-len(data) // part_size) if len(data) > part_size else 1
        parts = []
        for i in range(count):
            chunk = data[i * part_size:(i + 1) * part_size]
            asset = name if count == 1 else f"{name}.{i + 1:03d}"
            assets[asset] = chunk
            parts.append({"asset": asset, "offset": i * part_size, "size": len(chunk), "sha256": sha(chunk)})
        star = sdr.is_star_name(name)
        product, group = ("stars", "stars") if star else sdr.PRODUCT_BY_NAME[name]
        cut = sdr.tier_cut(name)
        entries.append({"name": name, "size": len(data), "sha256": sha(data), "product": product,
                        "group": group, "cut_v": int(cut) if cut is not None else None,
                        "licence": "CC BY-NC 3.0 IGO" if star else "test", "credit": "ESA, Gaia DPAC",
                        "parts": parts})
    tiers = [f"GaiaStarCatalog.V{cut}.starcat" for cut in CUTS]
    others = [name for name in files if not sdr.is_star_name(name)]
    manifest = {"schema": 1, "release": sdr.RELEASE, "repo": sdr.REPO, "min_mod_version": "0.5.0",
                "part_size": part_size, "files": entries,
                "variants": {"default": [tiers[0]], "compact": tiers + others,
                             "complete": tiers + [MAIN] + others}}
    assets[sdr.MANIFEST_ASSET] = json.dumps(manifest, indent=2).encode("utf-8")
    sums = [f"{sha(data)}  {asset}\n" for asset, data in assets.items()]
    sums += [f"{sha(data)}  {name}\n" for name, data in files.items() if name not in assets]
    assets["SHA256SUMS"] = "".join(sums).encode("utf-8")
    assets["NOTICE-sky-data.md"] = b"# Sky data licences\n\nTest release.\n"
    return Release(files, manifest, assets)


def make_ksp(root, version="0.5.0"):
    """A KSP folder holding only what the scripts look at."""
    mod = Path(root) / "GameData" / "ExoInstruments"
    (mod / "PluginData").mkdir(parents=True)
    write_version(mod, version)
    return Path(root), mod / "PluginData"


def write_version(mod, version):
    (Path(mod) / "ExoInstruments.version").write_text(json.dumps(
        {"NAME": "ExoInstruments", "VERSION": version, "KSP_VERSION_MIN": "1.8.0",
         "KSP_VERSION_MAX": "1.12.5"}, indent=1))


def can_symlink():
    with tempfile.TemporaryDirectory() as folder:
        try:
            os.symlink(os.path.join(folder, "target"), os.path.join(folder, "link"))
            return True
        except (OSError, NotImplementedError):
            return False


class Fault:
    """kind: expire (redirect already expired), ignore_range (200), drop, flip or stall at byte `at`."""

    def __init__(self, asset, kind, at=0, skip=0, seconds=30.0):
        self.asset = asset
        self.kind = kind
        self.at = at
        self.skip = skip
        self.seconds = seconds
        self.used = False


class QuietServer(http.server.ThreadingHTTPServer):
    daemon_threads = True
    block_on_close = False

    def handle_error(self, request, client_address):
        pass


class ReleaseServer:
    """github.com answers 302 to a signed CDN link that expires; the CDN serves bytes with Range."""

    def __init__(self, assets, ttl=3600.0):
        self.assets = dict(assets)
        self.ttl = ttl
        self.lock = threading.Lock()
        self.requests = []
        self.faults = []
        self.release_stalls = threading.Event()
        self.github = QuietServer(("127.0.0.1", 0), self._handler(self.serve_redirect))
        self.cdn = QuietServer(("127.0.0.1", 0), self._handler(self.serve_bytes))
        for server in (self.github, self.cdn):
            threading.Thread(target=server.serve_forever, daemon=True).start()

    @property
    def base_url(self):
        return (f"http://127.0.0.1:{self.github.server_address[1]}/{sdr.REPO}/releases/download/"
                f"{sdr.RELEASE}/")

    def close(self):
        self.release_stalls.set()
        for server in (self.github, self.cdn):
            server.shutdown()
            server.server_close()

    def add_fault(self, asset, kind, **options):
        with self.lock:
            self.faults.append(Fault(asset, kind, **options))

    def take_fault(self, asset, kinds):
        with self.lock:
            for fault in self.faults:
                if fault.used or fault.asset != asset or fault.kind not in kinds:
                    continue
                if fault.skip:
                    fault.skip -= 1
                    continue
                fault.used = True
                return fault
        return None

    def record(self, **fields):
        with self.lock:
            self.requests.append(fields)

    def clear_log(self):
        with self.lock:
            self.requests = []

    def github_assets(self):
        """Assets asked of github.com, the manifest aside, in order."""
        return [r["asset"] for r in self.requests
                if r["server"] == "github" and r["asset"] != sdr.MANIFEST_ASSET]

    def cdn_requests(self, asset):
        return [r for r in self.requests if r["server"] == "cdn" and r["asset"] == asset]

    def cdn_ranges(self, asset):
        return [r["range"] for r in self.cdn_requests(asset)]

    @staticmethod
    def _handler(serve):
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                serve(self)
        return Handler

    def serve_redirect(self, handler):
        prefix = f"/{sdr.REPO}/releases/download/{sdr.RELEASE}/"
        path = urllib.parse.urlsplit(handler.path).path
        asset = urllib.parse.unquote(path[len(prefix):]) if path.startswith(prefix) else None
        if asset not in self.assets:
            self.record(server="github", asset=asset, range=None, status=404)
            handler.send_error(404)
            return
        expires = time.time() + self.ttl
        if self.take_fault(asset, ("expire",)):
            expires = time.time() - 1.0
        location = (f"http://127.0.0.1:{self.cdn.server_address[1]}/signed/"
                    f"{urllib.parse.quote(asset)}?se={expires:.3f}&sig=test")
        self.record(server="github", asset=asset, range=handler.headers.get("Range"), status=302)
        handler.send_response(302)
        handler.send_header("Location", location)
        handler.send_header("Content-Length", "0")
        handler.end_headers()

    def serve_bytes(self, handler):
        split = urllib.parse.urlsplit(handler.path)
        asset = urllib.parse.unquote(split.path[len("/signed/"):])
        wanted = handler.headers.get("Range")
        data = self.assets.get(asset)
        if data is None:
            self.record(server="cdn", asset=asset, range=wanted, status=404)
            handler.send_error(404)
            return
        try:
            expires = float(urllib.parse.parse_qs(split.query)["se"][0])
        except (KeyError, ValueError):
            expires = 0.0
        if time.time() > expires:
            self.record(server="cdn", asset=asset, range=wanted, status=403)
            handler.send_error(403)
            return
        status, start, end = 200, 0, len(data) - 1
        match = re.fullmatch(r"bytes=(\d+)-(\d*)", (wanted or "").strip())
        if match and not self.take_fault(asset, ("ignore_range",)):
            start = int(match.group(1))
            if start >= len(data):
                self.record(server="cdn", asset=asset, range=wanted, status=416)
                handler.send_response(416)
                handler.send_header("Content-Range", f"bytes */{len(data)}")
                handler.send_header("Content-Length", "0")
                handler.end_headers()
                return
            if match.group(2):
                end = min(end, int(match.group(2)))
            status = 206
        body = data[start:end + 1]
        fault = self.take_fault(asset, ("drop", "flip", "stall"))
        self.record(server="cdn", asset=asset, range=wanted, status=status,
                    fault=fault.kind if fault else None)
        handler.send_response(status)
        if status == 206:
            handler.send_header("Content-Range", f"bytes {start}-{end}/{len(data)}")
        handler.send_header("Accept-Ranges", "bytes")
        handler.send_header("Content-Length", str(len(body)))
        handler.end_headers()
        try:
            if fault is None:
                handler.wfile.write(body)
            elif fault.kind == "flip":
                spoiled = bytearray(body)
                spoiled[min(fault.at, len(spoiled) - 1)] ^= 0xFF
                handler.wfile.write(bytes(spoiled))
            elif fault.kind == "drop":
                handler.wfile.write(body[:fault.at])
                handler.close_connection = True
            else:
                handler.wfile.write(body[:fault.at])
                handler.wfile.flush()
                self.release_stalls.wait(fault.seconds)
                handler.wfile.write(body[fault.at:])
        except OSError:
            pass
