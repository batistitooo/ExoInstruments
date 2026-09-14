#!/usr/bin/env python3
"""Publishes the sky data files as a GitHub release for the player download scripts. Maintainer only.

    python3 tools/publish_sky_data.py                      check, hash, write the release metadata
    python3 tools/publish_sky_data.py --upload --dry-run   the same, plus what an upload would do
    python3 tools/publish_sky_data.py --upload             create the release, upload what is missing

The files are read from the KSP install's GameData/ExoInstruments/PluginData (found the way
setup_data.py finds it), from --plugin-data, or from --file paths. They are only ever read, links
included. Each is checked field by field the way the mod reads it, down to its exact size. Every
star tier is also compared, over sampled declination bands, with the deepest star file present.
Then each file is hashed in one pass, whole and per part. A file larger than the part size
(1900 MiB, under GitHub's 2 GiB asset limit) is published as <name>.001, <name>.002 and so on; a
smaller one is a single asset under its own name.

--out receives sky-data-manifest.json, SHA256SUMS and NOTICE-sky-data.md. That NOTICE is the
repository's NOTICE-sky-data.md (or --notice) followed by a generated table of this release's files,
and the licence and credit of every file must appear in it. The manifest sha256 printed at the end is
the MANIFEST_SHA256 to pin in tools/sky_data_release.py.

A release must carry the five star files; the other products may be left out with --exclude.

--upload needs the GitHub CLI (gh), logged in with write access to the repository. Parts go up one
at a time, each written to a temporary file in a folder of its own that is deleted before the next,
so the extra disk used is one part. Assets already published with the same size and sha256 digest
are skipped, so an interrupted upload resumes by rerunning the same command. An asset published
with a different digest is refused unless --replace: players resume downloads against the pinned
manifest, so the assets of a tag must never change once a mod release pins it. The metadata goes up
last, then every asset's digest is checked against the manifest.

Standard library only.
"""

import argparse
import array
import glob
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile

TOOLS = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(TOOLS)

DEFAULT_TAG = "sky-data-1"
DEFAULT_REPO = "batistitooo/ExoInstruments"
MIN_MOD_VERSION = "0.5.0"
PART_SIZE = 1992294400   # 1900 MiB
SCHEMA = 1

MANIFEST_NAME = "sky-data-manifest.json"
SUMS_NAME = "SHA256SUMS"
NOTICE_NAME = "NOTICE-sky-data.md"
PARTS_PREFIX = "publish-sky-data-parts-"

CHUNK = 8 << 20
PLACEHOLDER = "TO CONFIRM"
MAX_NSIDE = 1 << 13          # Healpix.MaxNside
V_MAG_OFFSET = 2.0           # a record's v is (V + 2) * 1000, as tier_star_catalog.py cuts it
TIER_SAMPLE_BANDS = 8

# Written verbatim into the manifest. Each "; " separated piece must appear in the NOTICE source,
# and --upload refuses while any is still PLACEHOLDER.
PRODUCTS = {
    "stars": {"group": "stars", "magic": b"EXOSTAR1",
              "licence": "CC BY-NC 3.0 IGO", "credit": "ESA, Gaia DPAC"},
    "dust": {"group": "dust", "magic": b"EXODUST1",
             "licence": "CC0 1.0",
             "credit": "SFD 1998 Map of Galactic Dust, Finkbeiner, Schlegel & Davis, Harvard Dataverse"},
    # NOTICE-sky-data.md says both H-alpha products are built locally, not redistributed.
    "halpha": {"group": "halpha", "magic": b"EXOEMIS1",
               "licence": PLACEHOLDER, "credit": PLACEHOLDER},
    "patches": {"group": "halpha", "magic": b"EXOPTCH3",
                "licence": PLACEHOLDER, "credit": PLACEHOLDER},
    "galaxies": {"group": "galaxies", "magic": b"EXOGALX1",
                 "licence": "Publicly available for non-commercial purposes",
                 "credit": "We acknowledge the usage of the HyperLeda database (http://leda.univ-lyon1.fr)."},
    "images": {"group": "galaxies", "magic": b"EXOGIMG1",
               "licence": "CC BY 4.0 (Legacy Surveys), ODbL 1.0 (CDS HiPS) and each survey's terms; non-commercial use",
               "credit": "Legacy Surveys / D. Lang (Perimeter Institute); Pan-STARRS1; "
                         "Sloan Digital Sky Survey; The Dark Energy Survey Data Release 2; "
                         "hips2fits, a service provided by CDS"},
}

# Manifest order: star files shallowest first, then the other products.
FILES = [
    ("GaiaStarCatalog.V13.starcat", "stars", 13),
    ("GaiaStarCatalog.V15.starcat", "stars", 15),
    ("GaiaStarCatalog.V17.starcat", "stars", 17),
    ("GaiaStarCatalog.V19.starcat", "stars", 19),
    ("GaiaStarCatalog.starcat", "stars", None),
    ("DustMap.dustmap", "dust", None),
    ("HalphaMap.emission", "halpha", None),
    ("HalphaPatches.patchset", "patches", None),
    ("GalaxyCatalog.galcat", "galaxies", None),
    ("GalaxyImages.galimg", "images", None),
]
KNOWN = {name: (product, cut_v) for name, product, cut_v in FILES}
MAIN_STARS = "GaiaStarCatalog.starcat"
DEFAULT_VARIANT = "GaiaStarCatalog.V13.starcat"


def log(msg):
    print(f"[publish_sky_data] {msg}", flush=True)


def warn(msg):
    print(f"[publish_sky_data] WARNING: {msg}", flush=True)


def die(msg):
    sys.stdout.flush()
    print(f"[publish_sky_data] ERROR: {msg}", file=sys.stderr, flush=True)
    sys.exit(1)


def sha256_hex(data):
    return hashlib.sha256(data).hexdigest()


def part_layout(name, size, part_size):
    """(asset, offset, size) of each part. Only a file larger than one part is split."""
    if size <= part_size:
        return [(name, 0, size)]
    count = (size + part_size - 1) // part_size
    if count > 999:
        raise ValueError(f"{name} would need {count} parts, more than three digits can number")
    return [(f"{name}.{i + 1:03d}", i * part_size, min(part_size, size - i * part_size))
            for i in range(count)]


def same_path(a, b):
    return os.path.normcase(os.path.realpath(a)) == os.path.normcase(os.path.realpath(b))


# ---------------------------------------------------------------------------
# Finding and checking the files


class DataFile:
    def __init__(self, name, path, product, cut_v):
        self.name = name
        self.path = os.path.abspath(path)
        self.product = product
        self.cut_v = cut_v
        self.size = os.path.getsize(path)
        self.link = os.path.realpath(path) if os.path.islink(path) else None
        self.header = None
        self.sha256 = None
        self.parts = []


def product_keys(values):
    keys = []
    for value in values or []:
        for key in value.split(","):
            key = key.strip()
            if not key:
                continue
            if key == "all":
                keys.extend(PRODUCTS)
            elif key in PRODUCTS:
                keys.append(key)
            else:
                die(f"Unknown product {key!r}. Products: {', '.join(PRODUCTS)}.")
    return keys


def resolve_plugin_data(args):
    if args.plugin_data:
        path = os.path.abspath(os.path.expanduser(args.plugin_data))
    else:
        if TOOLS not in sys.path:
            sys.path.insert(0, TOOLS)
        from setup_data import resolve_ksp
        path = os.path.join(str(resolve_ksp(args.ksp)), "GameData", "ExoInstruments", "PluginData")
    if not os.path.isdir(path):
        die(f"{path} is not a directory.")
    return path


def collect_files(args, include):
    files = []
    if args.file:
        if args.plugin_data or args.ksp:
            die("Pass either --file paths or --plugin-data/--ksp, not both.")
        given = {}
        for path in args.file:
            name = os.path.basename(path)
            if name not in KNOWN:
                die(f"{path}: {name} is not a sky data file name. Names: {', '.join(KNOWN)}.")
            if name in given:
                die(f"{name} is given twice: {given[name]} and {path}.")
            if not os.path.isfile(path):
                die(f"{path} is not a file.")
            given[name] = path
        for name, product, cut_v in FILES:
            if name not in given:
                continue
            if product not in include:
                log(f"Leaving out {name}: product {product} is not included.")
                continue
            files.append(DataFile(name, given[name], product, cut_v))
    else:
        plugin_data = resolve_plugin_data(args)
        log(f"Reading {plugin_data}")
        missing = []
        for name, product, cut_v in FILES:
            if product not in include:
                continue
            path = os.path.join(plugin_data, name)
            if os.path.isfile(path):
                files.append(DataFile(name, path, product, cut_v))
            else:
                missing.append(f"{name} ({product}{', a broken link' if os.path.islink(path) else ''})")
        if missing:
            die(f"Missing from {plugin_data}: {'; '.join(missing)}. Build them with setup_data.py, "
                "or leave their products out, for example --exclude images.")
    if not files:
        die("Nothing to publish.")
    return files


class Fault(Exception):
    pass


class Walker:
    """Steps through a packed file as the mod's loader reads it, seeking past pixel data."""

    def __init__(self, handle, size):
        self.handle = handle
        self.size = size

    def take(self, n, what):
        data = self.handle.read(n)
        if len(data) < n:
            raise Fault(f"{self.size:,} bytes, which ends inside {what}")
        return data

    def unpack(self, fmt, what):
        return struct.unpack("<" + fmt, self.take(struct.calcsize("<" + fmt), what))

    def int32(self, what, low, high):
        value = self.unpack("i", what)[0]
        if not low <= value <= high:
            raise Fault(f"{what} is {value}, outside {low:,} to {high:,}")
        return value

    def version(self, *accepted):
        value = self.unpack("i", "the format version")[0]
        if value not in accepted:
            raise Fault(f"format version {value}, where a release takes "
                        f"{' or '.join(str(v) for v in accepted)}")

    def nside(self, what):
        value = self.unpack("i", what)[0]
        if not 0 < value <= MAX_NSIDE or value & (value - 1):
            raise Fault(f"{what} is {value}, not a HEALPix nside")
        return value

    def string(self, what, limit):
        self.take(self.int32(f"the length of {what}", 0, limit), what)

    def skip(self, n, what):
        if self.handle.tell() + n > self.size:
            raise Fault(f"{self.size:,} bytes, which ends inside {what}")
        self.handle.seek(n, os.SEEK_CUR)

    def end(self):
        extra = self.size - self.handle.tell()
        if extra:
            raise Fault(f"{extra:,} bytes follow the end of its data")


def walk_dust(w):
    w.version(2)
    pixels = 12 * w.nside("nside") ** 2
    w.take(1, "the pixel ordering")
    w.string("the source", 4096)
    w.skip(2 * pixels, f"its {pixels:,} pixels")
    w.end()


def walk_emission(w):
    w.version(2)
    pixels = 12 * w.nside("nside") ** 2
    w.take(1, "the pixel ordering")
    if not w.unpack("d", "the line wavelength")[0] > 0.0:
        raise Fault("its line wavelength is not positive")
    w.string("the line name", 256)
    w.string("the source", 4096)
    w.skip(2 * pixels, f"its {pixels:,} pixels")
    w.end()


def walk_patches(w):
    w.version(3)
    w.nside("nside")
    if w.take(1, "the pixel ordering") != b"\0":
        raise Fault("it is not RING ordered")
    if not w.unpack("d", "the line wavelength")[0] > 0.0:
        raise Fault("its line wavelength is not positive")
    w.string("the line name", 256)
    w.string("the source", 4096)
    count = w.int32("the patch count", 0, 100000)
    for i in range(count):
        what = f"patch {i + 1} of {count}"
        w.string(f"the name of {what}", 128)
        w.unpack("ddf", f"the centre of {what}")
        w.nside(f"the nside of {what}")
        runs = w.int32(f"the run count of {what}", 0, 10000000)
        cells, previous = 0, None
        for start, length in struct.iter_unpack("<ii", w.take(8 * runs, f"the runs of {what}")):
            if length <= 0 or (previous is not None and start <= previous):
                raise Fault(f"{what} has an empty or unsorted run")
            previous = start
            cells += length
        for plane in range(w.int32(f"the plane count of {what}", 1, 16)):
            w.string(f"a plane name of {what}", 64)
            w.unpack("d", f"a plane wavelength of {what}")
            w.skip(2 * cells, f"a plane of {what}")
    w.end()


def walk_galaxies(w):
    w.version(2)   # the loader still reads 1, which lacks the distance column supernovae need
    count = w.int32("the galaxy count", 0, 20000000)
    w.string("the source", 4096)
    for i in range(count):
        w.string(f"the name of galaxy {i + 1} of {count}", 64)
        w.skip(16 + 7 * 4 + 4 + 1, f"galaxy {i + 1} of {count}")
    w.end()


def walk_images(w):
    w.version(1)
    count = w.int32("the image count", 0, 1000000)
    w.string("the source", 4096)
    for i in range(count):
        what = f"image {i + 1} of {count}"
        w.string(f"the name of {what}", 64)
        w.unpack("dd", f"the position of {what}")
        side = w.int32(f"the map size of {what}", 8, 16384)
        w.unpack("d", f"the scale of {what}")
        w.string(f"the survey of {what}", 64)
        w.unpack("Bff", f"the photometry of {what}")
        for companion in range(w.int32(f"the companion count of {what}", 0, 4096)):
            w.string(f"a companion of {what}", 64)
        for band in range(w.int32(f"the band count of {what}", 1, 8)):
            w.unpack("d", f"a band wavelength of {what}")
            w.string(f"a band label of {what}", 64)
            w.unpack("d", f"a band scale of {what}")
            w.skip(2 * side * side, f"a band of {what}")
    w.end()


WALKERS = {"dust": walk_dust, "halpha": walk_emission, "patches": walk_patches,
           "galaxies": walk_galaxies, "images": walk_images}


def check_file(f):
    """Names what is wrong with a file, or returns None."""
    magic = PRODUCTS[f.product]["magic"]
    with open(f.path, "rb") as handle:
        if handle.read(len(magic)) != magic:
            return f"it does not start with {magic.decode()}, so it is not the format its name claims"
        if f.product == "stars":
            return check_star_catalogue(f, handle)
        try:
            WALKERS[f.product](Walker(handle, f.size))
        except Fault as fault:
            return str(fault)
    return None


def check_star_catalogue(f, handle):
    head = handle.read(16)
    if len(head) < 16:
        return "shorter than the 24 byte EXOSTAR1 header"
    version, count, bands = struct.unpack_from("<iii", head, 0)
    if version not in (2, 3):
        return f"catalogue version {version}, where readers accept 2 or 3"
    if count < 0 or bands <= 0:
        return f"its header claims {count} stars in {bands} bands"
    record = 14 if version == 3 else 12   # version 2 has no reddening column
    records_at = 24 + 4 * (bands + 1)
    need = records_at + record * count
    if f.size != need:
        return f"{f.size:,} bytes where {count:,} stars in {bands} bands need {need:,}"
    index = struct.unpack(f"<{bands + 1}I", handle.read(4 * (bands + 1)))
    if index[-1] != count or any(index[b] > index[b + 1] for b in range(bands)):
        return "its declination index is broken"
    f.header = {"version": version, "count": count, "bands": bands, "width": head[12:16],
                "index": index, "record": record, "records_at": records_at}
    if TOOLS not in sys.path:
        sys.path.insert(0, TOOLS)
    from pack_gaia_catalog import header_band_fault
    return header_band_fault(f.path)


def stars_by_depth(files):
    return sorted((f for f in files if f.product == "stars"),
                  key=lambda f: float("inf") if f.cut_v is None else f.cut_v)


def tier_faults(files):
    """Each star file must fit, band by band, inside the next deeper one, as the loader requires."""
    stars = stars_by_depth(files)
    faults = []
    for shallow, deep in zip(stars, stars[1:]):
        a, b = shallow.header, deep.header
        if (a["version"], a["bands"], a["width"]) != (b["version"], b["bands"], b["width"]):
            faults.append(f"{shallow.name}: its version, band count or band width differ from "
                          f"{deep.name}'s")
        elif a["count"] > b["count"] or (deep.cut_v is None and a["count"] >= b["count"]):
            faults.append(f"{shallow.name}: {a['count']:,} stars, not fewer than {deep.name}'s "
                          f"{b['count']:,}")
        elif any(a["index"][i + 1] - a["index"][i] > b["index"][i + 1] - b["index"][i]
                 for i in range(a["bands"])):
            faults.append(f"{shallow.name}: a declination band holds more stars than the same "
                          f"band of {deep.name}")
    return faults


def read_band(f, handle, band):
    h = f.header
    start, stop = h["index"][band], h["index"][band + 1]
    handle.seek(h["records_at"] + start * h["record"])
    data = handle.read((stop - start) * h["record"])
    if len(data) != (stop - start) * h["record"]:
        die(f"{f.path} got shorter while it was being read.")
    return data


def tier_record_faults(files):
    """Every tier must hold exactly the deepest file's stars down to its cut, in the same order.

    Compared on whole declination bands spread over the sky plus the fullest one, like
    tier_star_catalog.verify but without numpy. Needs the headers tier_faults accepted.
    """
    stars = stars_by_depth(files)
    if len(stars) < 2:
        return []
    deep, tiers = stars[-1], stars[:-1]
    h = deep.header
    sizes = [h["index"][b + 1] - h["index"][b] for b in range(h["bands"])]
    populated = [b for b, n in enumerate(sizes) if n]
    if not populated:
        return []
    count = min(TIER_SAMPLE_BANDS, len(populated))
    chosen = {populated[round(k * (len(populated) - 1) / max(1, count - 1))] for k in range(count)}
    chosen.add(max(populated, key=lambda b: sizes[b]))
    log(f"Comparing {len(chosen)} declination bands of {', '.join(f.name for f in tiers)} "
        f"with {deep.name}")

    record = h["record"]
    faults, failed = [], set()
    handles = {}
    try:
        for f in stars:
            handles[f.name] = open(f.path, "rb")
        for band in sorted(chosen):
            data = read_band(deep, handles[deep.name], band)
            v = array.array("H")
            v.frombytes(data)
            if sys.byteorder != "little":
                v.byteswap()
            v = v[4::record // 2]   # the u16 at byte 8 of each record
            records = memoryview(data)
            kept = None
            for f in reversed(tiers):   # deepest cut first, so each keeps a subset of the last
                limit = int(round((f.cut_v + V_MAG_OFFSET) * 1000.0))
                if kept is None:
                    kept = [i for i, mag in enumerate(v) if mag <= limit]
                else:
                    kept = [i for i in kept if v[i] <= limit]
                if f.name in failed:
                    continue
                expected = b"".join([records[i * record:(i + 1) * record] for i in kept])
                if read_band(f, handles[f.name], band) != expected:
                    failed.add(f.name)
                    faults.append(f"{f.name}: declination band {band} is not {deep.name}'s stars "
                                  f"at V {f.cut_v} or brighter, so it was cut from another catalogue")
    finally:
        for handle in handles.values():
            handle.close()
    return faults


def hash_file(f, part_size):
    """Hashes the whole file and each part's byte range in one read, copying nothing."""
    layout = part_layout(f.name, f.size, part_size)
    log(f"Hashing {f.name} ({f.size:,} bytes, {len(layout)} "
        f"{'part' if len(layout) == 1 else 'parts'})")
    whole = hashlib.sha256()
    buffer = memoryview(bytearray(CHUNK))
    f.parts = []
    with open(f.path, "rb") as handle:
        for number, (asset, offset, size) in enumerate(layout, 1):
            digest = hashlib.sha256()
            left = size
            while left:
                n = handle.readinto(buffer[:min(CHUNK, left)])
                if not n:
                    die(f"{f.path} got shorter while it was being read.")
                digest.update(buffer[:n])
                whole.update(buffer[:n])
                left -= n
            f.parts.append({"asset": asset, "offset": offset, "size": size,
                            "sha256": digest.hexdigest()})
            if len(layout) > 1:
                log(f"  {asset} ({number}/{len(layout)})")
        if handle.read(1):
            die(f"{f.path} grew while it was being read.")
    f.sha256 = whole.hexdigest()


# ---------------------------------------------------------------------------
# Release metadata


def manifest_bytes(files, args):
    names = [f.name for f in files]
    tiers = [f.name for f in files if f.product == "stars" and f.cut_v is not None]
    others = [f.name for f in files if f.product != "stars"]
    compact = tiers + others
    manifest = {
        "schema": SCHEMA,
        "release": args.tag,
        "repo": args.repo,
        "min_mod_version": args.min_mod_version,
        "part_size": args.part_size,
        "files": [{
            "name": f.name,
            "size": f.size,
            "sha256": f.sha256,
            "product": f.product,
            "group": PRODUCTS[f.product]["group"],
            "cut_v": f.cut_v,
            "licence": PRODUCTS[f.product]["licence"],
            "credit": PRODUCTS[f.product]["credit"],
            "parts": f.parts,
        } for f in files],
        "variants": {
            "default": [n for n in names if n == DEFAULT_VARIANT],
            "compact": compact,
            "complete": compact + [n for n in names if n == MAIN_STARS],
        },
    }
    return (json.dumps(manifest, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def read_notice_source(path):
    try:
        with open(path, "rb") as handle:
            return handle.read().decode("utf-8").replace("\r\n", "\n")
    except OSError as error:
        die(f"Cannot read the NOTICE source {path}: {error.strerror or error}. It is the hand "
            "written licence text every release carries; pass --notice if it lives elsewhere.")
    except UnicodeDecodeError:
        die(f"{path} is not UTF-8.")


def notice_faults(source, files):
    """What the manifest would claim that the NOTICE source does not say."""
    def flat(text):
        return " ".join(text.split()).casefold()

    text = flat(source)
    faults = []
    for f in files:
        if flat(f.name) not in text:
            faults.append(f"{f.name} is not named in it")
        for key in ("licence", "credit"):
            value = PRODUCTS[f.product][key]
            if value == PLACEHOLDER:
                continue
            for piece in value.split("; "):
                if flat(piece) not in text:
                    faults.append(f"the {key} of {f.product}, {piece!r}, is not in it")
    return list(dict.fromkeys(faults))


def notice_bytes(source, files, args):
    def cell(text):
        return str(text).replace("|", "\\|")

    lines = [
        source.rstrip("\n"),
        "",
        f"## Files in {args.tag}",
        "",
        f"For ExoInstruments {args.min_mod_version} or later.",
        "",
        "| File | Bytes | Licence | Credit |",
        "| :- | -: | :- | :- |",
    ]
    for f in files:
        info = PRODUCTS[f.product]
        lines.append(f"| {f.name} | {f.size:,} | {cell(info['licence'])} | {cell(info['credit'])} |")
    lines += [
        "",
        "### Downloading by hand",
        "",
        "Download the assets you want into GameData/ExoInstruments/PluginData. SHA256SUMS lists the "
        "sha256 of every asset and of every whole file.",
    ]
    split = [f for f in files if len(f.parts) > 1]
    if split:
        lines += [
            "",
            f"A file larger than {args.part_size:,} bytes is published in numbered parts. Join the "
            "parts in order, check the joined file against SHA256SUMS, then delete the parts.",
            "",
            "On macOS and Linux:",
            "",
        ]
        lines += [f"    cat {f.name}.[0-9][0-9][0-9] > {f.name}" for f in split]
        lines += ["", "On Windows, in Command Prompt:", ""]
        lines += [f"    copy /b {'+'.join(p['asset'] for p in f.parts)} {f.name}" for f in split]
    lines += [
        "",
        "To check the files: `sha256sum -c --ignore-missing SHA256SUMS` on Linux, "
        "`shasum -a 256 -c SHA256SUMS` on macOS (files you did not download are reported missing), "
        "or `certutil -hashfile <file> SHA256` on Windows.",
    ]
    return ("\n".join(lines) + "\n").encode("utf-8")


def sums_bytes(files, extra):
    lines = []
    for f in files:
        lines.append(f"{f.sha256}  {f.name}")
        if len(f.parts) > 1:
            lines += [f"{p['sha256']}  {p['asset']}" for p in f.parts]
    lines += [f"{digest}  {name}" for name, digest in extra]
    return ("\n".join(lines) + "\n").encode("ascii")


def write_atomic(path, data):
    temporary = path + ".tmp"
    with open(temporary, "wb") as handle:
        handle.write(data)
    os.replace(temporary, path)


def print_table(headers, rows, right=()):
    cells = [list(headers)] + [[str(c) for c in row] for row in rows]
    widths = [max(len(row[i]) for row in cells) for i in range(len(headers))]
    for row in cells:
        print("  ".join(c.rjust(w) if i in right else c.ljust(w)
                        for i, (c, w) in enumerate(zip(row, widths))).rstrip())
    sys.stdout.flush()


# ---------------------------------------------------------------------------
# GitHub, through gh


def run_gh(gh, arguments, capture=True):
    pipe = subprocess.PIPE if capture else None
    try:
        return subprocess.run([gh] + arguments, stdout=pipe, stderr=pipe,
                              encoding="utf-8", errors="replace")
    except OSError as error:
        die(f"Could not run {gh}: {error}")


def release_title(tag):
    match = re.fullmatch(r"sky-data-(\d+)", tag)
    return f"Sky data {match.group(1)}" if match else tag


def release_info(gh, args):
    """The release as gh sees it, or None when the tag has no release."""
    proc = run_gh(gh, ["release", "view", args.tag, "--repo", args.repo,
                       "--json", "databaseId,tagName,isDraft,isImmutable"])
    if proc.returncode == 0:
        try:
            return json.loads(proc.stdout)
        except ValueError:
            die(f"gh release view printed something that is not JSON: {proc.stdout[:200]!r}")
    if "not found" in (proc.stderr or "").lower():
        return None
    die(f"gh release view {args.tag} failed: {(proc.stderr or '').strip()}")


def remote_assets(gh, args, release):
    """Every asset on the release by name. Pages arrive as separate JSON arrays."""
    endpoint = f"repos/{args.repo}/releases/{release['databaseId']}/assets?per_page=100"
    proc = run_gh(gh, ["api", "--paginate", endpoint])
    if proc.returncode:
        die(f"gh api {endpoint} failed: {(proc.stderr or '').strip()}")
    decoder = json.JSONDecoder()
    text = proc.stdout or ""
    position = 0
    found = {}
    try:
        while True:
            while position < len(text) and text[position].isspace():
                position += 1
            if position >= len(text):
                break
            page, position = decoder.raw_decode(text, position)
            for item in page if isinstance(page, list) else [page]:
                found[item["name"]] = item
    except (ValueError, KeyError, TypeError):
        die(f"Could not read the asset list from gh api {endpoint}.")
    return found


def remote_status(asset, item):
    if item is None:
        return "missing"
    if item.get("state", "uploaded") != "uploaded":
        return "incomplete"
    if item.get("size") == asset["size"] and item.get("digest") == "sha256:" + asset["sha256"]:
        return "present"
    return "differs"


def describe_remote(item):
    if item is None:
        return "no such asset"
    return (f"{item.get('size')} bytes, digest {item.get('digest')}, "
            f"state {item.get('state', 'uploaded')}")


def remove_if_present(path):
    if os.path.lexists(path):
        os.unlink(path)


def write_part(asset, destination):
    """Copies one part's byte range, refusing it if it no longer hashes as it did."""
    digest = hashlib.sha256()
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_BINARY", 0)
    left = asset["size"]
    with open(asset["path"], "rb") as source, os.fdopen(os.open(destination, flags, 0o644), "wb") as out:
        source.seek(asset["offset"])
        while left:
            chunk = source.read(min(CHUNK, left))
            if not chunk:
                break
            digest.update(chunk)
            out.write(chunk)
            left -= len(chunk)
    if left or digest.hexdigest() != asset["sha256"]:
        die(f"{asset['path']} changed after it was hashed: {asset['name']} no longer matches. "
            "Rerun to hash it again.")


def upload_asset(gh, args, asset, parts_dir, clobber):
    """parts_dir is this run's own folder: nothing else is ever deleted from it."""
    # A whole file already carries its asset name, so it goes up without a copy.
    direct = (asset["offset"] == 0 and asset["size"] == os.path.getsize(asset["path"])
              and os.path.basename(asset["path"]) == asset["name"] and "#" not in asset["path"])
    temporary = None
    try:
        if direct:
            source = asset["path"]
        else:
            temporary = os.path.join(parts_dir, asset["name"])
            write_part(asset, temporary)
            source = temporary
        command = ["release", "upload", args.tag, source, "--repo", args.repo]
        if clobber:
            command.append("--clobber")
        proc = run_gh(gh, command, capture=False)
    finally:
        # Also on a failed copy (a full disk, Ctrl+C), which would otherwise leave a partial part.
        if temporary:
            remove_if_present(temporary)
    if proc.returncode:
        die(f"gh release upload failed for {asset['name']}. Rerun the same command to resume: "
            "assets already published are skipped.")


# ---------------------------------------------------------------------------


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ksp", metavar="DIR",
                        help="KSP directory to read GameData/ExoInstruments/PluginData from "
                             "(default: found like setup_data.py does)")
    parser.add_argument("--plugin-data", metavar="DIR",
                        help="read the files from this directory instead")
    parser.add_argument("--file", action="append", metavar="PATH",
                        help="publish this file, named as in the release (repeatable; replaces "
                             "the directory lookup)")
    parser.add_argument("--include", action="append", metavar="PRODUCTS",
                        help="comma separated products to publish: " + ", ".join(PRODUCTS)
                             + " (default: all)")
    parser.add_argument("--exclude", action="append", metavar="PRODUCTS",
                        help="products to leave out, for example halpha,patches or images")
    parser.add_argument("--tag", default=DEFAULT_TAG, help=f"release tag (default: {DEFAULT_TAG})")
    parser.add_argument("--repo", default=DEFAULT_REPO,
                        help=f"GitHub repository (default: {DEFAULT_REPO})")
    parser.add_argument("--min-mod-version", default=MIN_MOD_VERSION, metavar="VERSION",
                        help=f"oldest mod version the manifest accepts (default: {MIN_MOD_VERSION})")
    parser.add_argument("--notice", metavar="PATH", default=os.path.join(REPO_ROOT, NOTICE_NAME),
                        help="hand written licence text the release NOTICE starts with "
                             f"(default: {NOTICE_NAME} at the repository root)")
    parser.add_argument("--out", metavar="DIR",
                        help="where the manifest, SHA256SUMS and NOTICE are written "
                             "(default: exoinstruments-<tag> in the system temporary directory)")
    parser.add_argument("--temp-dir", metavar="DIR",
                        help="where a folder of its own is made for each part just before its "
                             "upload (default: --out)")
    parser.add_argument("--upload", action="store_true",
                        help="create the release if needed and upload every asset not yet published")
    parser.add_argument("--replace", action="store_true",
                        help="re-upload assets published with a different digest; only before "
                             "a mod release pins this tag")
    parser.add_argument("--dry-run", action="store_true",
                        help="print the plan and total bytes, write nothing; with --upload, only "
                             "read the release")
    parser.add_argument("--part-size", type=int, default=PART_SIZE, help=argparse.SUPPRESS)
    parser.add_argument("--allow-placeholders", action="store_true", help=argparse.SUPPRESS)
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    if args.part_size <= 0:
        die("The part size must be positive.")
    if not re.fullmatch(r"\d+(\.\d+)*", args.min_mod_version):
        die(f"{args.min_mod_version!r} is not a version such as {MIN_MOD_VERSION}.")

    include = set(product_keys(args.include)) if args.include else set(PRODUCTS)
    include -= set(product_keys(args.exclude))
    files = collect_files(args, include)
    uploading = args.upload and not args.dry_run

    out = os.path.abspath(args.out or os.path.join(tempfile.gettempdir(), f"exoinstruments-{args.tag}"))
    temp_base = os.path.abspath(args.temp_dir) if args.temp_dir else out
    notice_path = os.path.abspath(args.notice)
    data_dirs = {os.path.dirname(f.path) for f in files} | {os.path.dirname(os.path.realpath(f.path))
                                                            for f in files}
    for flag, folder in (("--out", out), ("--temp-dir", temp_base)):
        if any(same_path(folder, d) for d in data_dirs):
            die(f"{flag} {folder} holds the data files being published. Point it at a folder of "
                "its own.")
    if same_path(out, REPO_ROOT) or same_path(os.path.join(out, NOTICE_NAME), notice_path):
        die(f"--out {out} would overwrite the NOTICE source {notice_path}. Point it at a folder of "
            "its own.")
    notice_source = read_notice_source(notice_path)

    faults = [f"{f.name}: {fault}" for f in files for fault in [check_file(f)] if fault]
    if not faults:
        faults = tier_faults(files) or tier_record_faults(files)
    if faults:
        die("Not publishing:\n  " + "\n  ".join(faults))

    names = {f.name for f in files}
    absent = [name for name, product, _ in FILES if product == "stars" and name not in names]
    if absent:
        message = f"the release would lack {', '.join(absent)}, so its variants would be incomplete"
        if uploading:
            die(message + ". Every release carries all five star files.")
        warn(message + ".")
    left_out = [name for name, product, _ in FILES if product != "stars" and name not in names]
    if left_out:
        log(f"Not in this release: {', '.join(left_out)}.")
    unconfirmed = sorted({f.product for f in files
                          if PLACEHOLDER in (PRODUCTS[f.product]["licence"], PRODUCTS[f.product]["credit"])})
    if unconfirmed:
        message = (f"licence or credit is still {PLACEHOLDER} for {', '.join(unconfirmed)} "
                   "(PRODUCTS in publish_sky_data.py)")
        if uploading and not args.allow_placeholders:
            die(message + ". A published release cannot change its NOTICE.")
        warn(message + ".")
    mismatches = notice_faults(notice_source, files)
    if mismatches:
        message = (f"the NOTICE source {notice_path} does not match PRODUCTS: "
                   + "; ".join(mismatches))
        if uploading:
            die(message + ". Make them agree: a published release cannot change its NOTICE.")
        warn(message + ".")

    gh = None
    if args.upload:
        gh = shutil.which("gh")
        if not gh:
            die("gh, the GitHub CLI, is not on PATH: https://cli.github.com")

    for f in files:
        hash_file(f, args.part_size)
    print()
    print_table(["File", "Product", "Group", "Bytes", "Parts", "sha256"],
                [[f.name, f.product, PRODUCTS[f.product]["group"], f"{f.size:,}", len(f.parts),
                  f.sha256] for f in files], right=(3, 4))
    print()
    for f in files:
        if f.link:
            log(f"{f.name} was read through its link to {f.link}; neither was modified.")

    notice = notice_bytes(notice_source, files, args)
    manifest = manifest_bytes(files, args)
    manifest_sha = sha256_hex(manifest)
    sums = sums_bytes(files, [(NOTICE_NAME, sha256_hex(notice)), (MANIFEST_NAME, manifest_sha)])
    metadata = [(NOTICE_NAME, notice), (SUMS_NAME, sums), (MANIFEST_NAME, manifest)]
    if not args.dry_run:
        os.makedirs(out, exist_ok=True)
        for name, data in metadata:
            write_atomic(os.path.join(out, name), data)
        log(f"Wrote {', '.join(name for name, _ in metadata)} to {out}")

    # Upload order: data parts in manifest order, then the metadata with the manifest last.
    assets = [{"name": p["asset"], "size": p["size"], "sha256": p["sha256"], "path": f.path,
               "offset": p["offset"]} for f in files for p in f.parts]
    assets += [{"name": name, "size": len(data), "sha256": sha256_hex(data),
                "path": os.path.join(out, name), "offset": 0} for name, data in metadata]
    file_by_path = {f.path: f.name for f in files}

    def origin(asset):
        if asset["path"] in file_by_path:
            return f"{file_by_path[asset['path']]} at byte {asset['offset']:,}"
        return "generated"

    data_bytes = sum(f.size for f in files)
    all_bytes = sum(a["size"] for a in assets)
    summary = (f"{len(assets)} assets, {all_bytes:,} bytes ({all_bytes / 2 ** 30:.2f} GiB), "
               f"{data_bytes:,} of them in the {len(files)} data files.")

    if not args.upload:
        print_table(["Asset", "Bytes", "From"],
                    [[a["name"], f"{a['size']:,}", origin(a)] for a in assets], right=(1,))
        print()
        log(summary)
        if args.dry_run:
            log("Dry run: nothing was written.")
        log(f"Manifest sha256: {manifest_sha}")
        log(f'Pin it in tools/sky_data_release.py: MANIFEST_SHA256 = "{manifest_sha}"')
        return 0

    release = release_info(gh, args)
    remote = remote_assets(gh, args, release) if release else {}
    statuses = [remote_status(a, remote.get(a["name"])) for a in assets]
    labels = {"missing": "upload", "incomplete": "upload again (unfinished)",
              "present": "skip (published, digest matches)",
              "differs": ("replace (published digest differs)" if args.replace
                          else "REFUSED (published with another size or digest)")}
    print_table(["Asset", "Bytes", "From", "Action"],
                [[a["name"], f"{a['size']:,}", origin(a), labels[s]] for a, s in zip(assets, statuses)],
                right=(1,))
    print()
    extra = sorted(set(remote) - {a["name"] for a in assets})
    if extra:
        warn(f"published but not in this manifest, left alone: {', '.join(extra)}")
    refused = [a for a, s in zip(assets, statuses) if s == "differs" and not args.replace]
    todo = [(a, s) for a, s in zip(assets, statuses) if s != "present" and a not in refused]
    todo_bytes = sum(a["size"] for a, _ in todo)
    log(summary)
    log(f"To upload: {len(todo)} assets, {todo_bytes:,} bytes ({todo_bytes / 2 ** 30:.2f} GiB).")
    log(f"Manifest sha256: {manifest_sha}")
    if refused:
        die("Refusing to overwrite published assets whose size or digest differ from this "
            f"manifest: {', '.join(a['name'] for a in refused)}. Players resume downloads against "
            "the pinned manifest, so a tag's assets must not change: publish a new tag, or pass "
            "--replace if no mod release pins this one yet.")

    if args.dry_run:
        if release is None:
            log(f"Would create release {args.tag} in {args.repo} titled {release_title(args.tag)!r}.")
        log("Dry run: nothing was uploaded, created or written.")
        return 0

    if release is None:
        log(f"Creating release {args.tag} in {args.repo}.")
        proc = run_gh(gh, ["release", "create", args.tag, "--repo", args.repo,
                           "--title", release_title(args.tag),
                           "--notes-file", os.path.join(out, NOTICE_NAME), "--latest=false"],
                      capture=False)
        if proc.returncode:
            die("gh release create failed; nothing was uploaded.")
        release = release_info(gh, args)
        if release is None:
            die(f"gh created release {args.tag} but cannot find it.")
    if todo and release.get("isImmutable"):
        die(f"Release {args.tag} is immutable, so GitHub refuses new assets on it. Turn off "
            "immutable releases for the repository while uploading, or publish a new tag.")

    if todo:
        os.makedirs(temp_base, exist_ok=True)
        # Left by a run that was killed outright; only named here, since this run did not make them.
        for stale in sorted(glob.glob(os.path.join(glob.escape(temp_base), PARTS_PREFIX + "*"))):
            warn(f"{stale} is left from an earlier run; delete it to free its disk space.")
        parts_dir = tempfile.mkdtemp(prefix=PARTS_PREFIX, dir=temp_base)
        try:
            for number, (asset, status) in enumerate(todo, 1):
                log(f"[{number}/{len(todo)}] Uploading {asset['name']} ({asset['size']:,} bytes)")
                upload_asset(gh, args, asset, parts_dir, clobber=status != "missing")
                item = remote_assets(gh, args, release).get(asset["name"])
                if remote_status(asset, item) != "present":
                    die(f"{asset['name']} was uploaded but GitHub reports {describe_remote(item)}, "
                        f"not {asset['size']:,} bytes with sha256 {asset['sha256']}.")
        finally:
            try:
                os.rmdir(parts_dir)
            except OSError:
                warn(f"could not remove {parts_dir}.")

    remote = remote_assets(gh, args, release)
    bad = [f"{a['name']} ({describe_remote(remote.get(a['name']))})" for a in assets
           if remote_status(a, remote.get(a["name"])) != "present"]
    if bad:
        die("These assets do not match the manifest after uploading: " + "; ".join(bad))
    log(f"All {len(assets)} assets of {args.tag} match the manifest's sizes and sha256 digests.")
    if release.get("isDraft"):
        log(f"Release {args.tag} is a draft: publish it on GitHub when ready.")
    log(f'Pin it in tools/sky_data_release.py: MANIFEST_SHA256 = "{manifest_sha}"')
    return 0


if __name__ == "__main__":
    sys.exit(main())
