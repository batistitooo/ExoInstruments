#!/usr/bin/env python3
"""Downloads ExoInstruments sky data from its GitHub release into PluginData.

The engine behind get_sky_data_compact.py, get_sky_data_complete.py and the default star
catalogue of setup_data.py. The player scripts then build what the release cannot carry by running
setup_data.py in the same process. Standard library only, Python 3.9 or newer.

What it guarantees:
  * the manifest is pinned by its sha256, and every part and every file is checked against it;
  * a final name only ever changes through os.replace from a staging file beside it;
  * nothing is written through a link: a matching link is kept, any other link is removed and
    the file it points at is left alone;
  * an interrupted download resumes where it stopped;
  * only the star catalogue names, the release's other files, .sky-data/ and the receipt are
    touched. Everything else in PluginData is left alone.
"""

import argparse
import errno
import fnmatch
import hashlib
import http.client
import json
import os
import platform
import re
import shutil
import stat
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = "batistitooo/ExoInstruments"
RELEASE = "sky-data-1"
BASE_URL = f"https://github.com/{REPO}/releases/download/{RELEASE}/"
MANIFEST_ASSET = "sky-data-manifest.json"
# The sha256 of the manifest bytes this mod build accepts, set when the release is published.
MANIFEST_SHA256 = "824f8c8524411e5f4429fbe1ca4cb324b625fe6ecfe97a37f6a6d33640e8d10e"
PART_SIZE = 1992294400
SCHEMA = 1

MAIN_NAME = "GaiaStarCatalog.starcat"
DEFAULT_NAME = "GaiaStarCatalog.V13.starcat"
TIER_RE = re.compile(r"GaiaStarCatalog\.V(\d+(?:\.\d+)?)\.starcat")
RECEIPT_NAME = "sky-data-receipt.json"
STAGING_NAME = ".sky-data"
VERSION_FILE = "ExoInstruments.version"

# The other products in install order, as (file name, product, group). Files in one group were
# built from each other, so a group is replaced as a whole or not at all.
PRODUCTS = (
    ("DustMap.dustmap", "dust", "dust"),
    ("HalphaMap.emission", "halpha", "halpha"),
    ("HalphaPatches.patchset", "patches", "halpha"),
    ("GalaxyCatalog.galcat", "galaxies", "galaxies"),
    ("GalaxyImages.galimg", "images", "galaxies"),
)
PRODUCT_BY_NAME = {name: (product, group) for name, product, group in PRODUCTS}
DATA_EXTENSIONS = (".starcat", ".dustmap", ".emission", ".patchset", ".galcat", ".galimg")
VARIANTS = ("default", "compact", "complete")

CHUNK = 1 << 20
MANIFEST_LIMIT = 4 << 20
SOCKET_TIMEOUT = 60.0
RETRY_DELAY = 2.0
RETRY_MAX_DELAY = 60.0
NETWORK_TRIES = 8
HASH_TRIES = 3
MARGIN_MIN = 256 << 20
LOUD_HASH = 256 << 20
USER_AGENT = f"ExoInstruments-sky-data/{RELEASE}"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_DECLINED = 2

O_BINARY = getattr(os, "O_BINARY", 0)
O_NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)
HEX64 = re.compile(r"[0-9a-f]{64}")
CONTENT_RANGE = re.compile(r"bytes (\d+)-(\d+)/(\d+)")
# The reparse tag of a Windows junction or mounted folder.
IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003

TOOLS = Path(__file__).resolve().parent

# Whether this is the copy shipped inside the installed mod rather than one in a clone of the
# repository. It decides where KSP is (no guessing needed).
SHIPPED_IN_GAMEDATA = (TOOLS.parent.name == "ExoInstruments"
                       and TOOLS.parent.parent.name == "GameData")


class SkyDataError(Exception):
    """Stops a run with a message meant for the player."""

    def __init__(self, message, code=EXIT_FAILED):
        super().__init__(message)
        self.code = code


def say(message):
    print(message, flush=True)


# ---------------------------------------------------------------------------
# Finding KSP


def candidate_ksp_dirs():
    """Where to look for KSP, most likely first.

    The first candidate is not a guess at all: these scripts ship inside
    GameData/ExoInstruments/tools/, so when a player runs the copy that came with the mod, the KSP
    directory is three levels up and no platform heuristic is involved. The Steam defaults below
    are the fallback for running it out of a clone of the repository.
    """
    if SHIPPED_IN_GAMEDATA:
        yield TOOLS.parent.parent.parent
    for library in steam_libraries():
        yield library / "steamapps" / "common" / "Kerbal Space Program"
    # Non-Steam installs, and the store builds that do not register a Steam library at all.
    home = Path.home()
    if platform.system() == "Windows":
        for root in (Path("C:/"), Path("D:/"), Path("E:/")):
            yield root / "Games" / "Kerbal Space Program"
            yield root / "Kerbal Space Program"
    else:
        yield home / "Kerbal Space Program"


def steam_roots():
    """Where Steam itself is installed, per platform."""
    home = Path.home()
    system = platform.system()
    if system == "Darwin":
        yield home / "Library" / "Application Support" / "Steam"
    elif system == "Windows":
        # The registry is the authoritative answer and costs nothing to ask for; winreg exists
        # only on Windows, hence the local import.
        try:
            import winreg
            for hive, key in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam"),
                              (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam")):
                try:
                    with winreg.OpenKey(hive, key) as handle:
                        value = winreg.QueryValueEx(handle, "SteamPath")[0]
                        if value:
                            yield Path(value)
                except OSError:
                    continue
        except ImportError:
            pass
        yield Path("C:/Program Files (x86)/Steam")
    else:
        yield home / ".steam" / "steam"
        yield home / ".local" / "share" / "Steam"


def steam_libraries():
    """Every Steam library folder, read from Steam's own libraryfolders.vdf.

    Hardcoding drive letters was the previous approach and it is wrong in the common case: on
    Windows a second drive holding the games is normal, and on any platform a player can put a
    library anywhere. Steam already keeps the list, so it is asked rather than guessed.

    The file is Valve's KeyValues format. Only the "path" entries are wanted, and they are the
    one thing in it whose shape has survived every version of the format, so a regex over the
    quoted values is both sufficient and immune to the nesting changing again.
    """
    seen = set()
    for root in steam_roots():
        if root in seen or not root.is_dir():
            continue
        seen.add(root)
        yield root
        # Steam has kept this file in both places across versions, so both are tried.
        for vdf in (root / "steamapps" / "libraryfolders.vdf",
                    root / "config" / "libraryfolders.vdf"):
            if not vdf.is_file():
                continue
            try:
                text = vdf.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            for match in re.finditer(r'"path"\s*"([^"]+)"', text):
                path = Path(match.group(1).replace("\\\\", "\\"))
                if path not in seen:
                    seen.add(path)
                    yield path


def looks_like_ksp(path):
    """A GameData directory is the thing that actually matters, so that is what is checked."""
    return (path / "GameData").is_dir()


def resolve_ksp(explicit, log=say):
    if explicit:
        path = Path(explicit).expanduser()
        if not looks_like_ksp(path):
            raise SkyDataError(f"{path} does not contain a GameData directory, so it is not a KSP install.")
        return path
    env = os.environ.get("KSP")
    if env:
        path = Path(env).expanduser()
        if not looks_like_ksp(path):
            raise SkyDataError(f"$KSP points at {path}, which has no GameData directory.")
        return path
    for path in candidate_ksp_dirs():
        if looks_like_ksp(path):
            log(f"Found KSP at {path}")
            return path
    raise SkyDataError("Could not find a KSP install. Pass --ksp /path/to/Kerbal Space Program, or set $KSP.")


def plugin_data_dir(ksp):
    """Where every finished product lands."""
    mod = Path(ksp) / "GameData" / "ExoInstruments"
    if not mod.is_dir():
        raise SkyDataError(f"ExoInstruments is not installed at {mod}. Install the mod first (CKAN, or "
                           "unzip the release over the KSP folder), then rerun this.")
    target = mod / "PluginData"
    target.mkdir(parents=True, exist_ok=True)
    return target


# ---------------------------------------------------------------------------
# Small pure helpers


def human(n):
    """Bytes in decimal units, as the README quotes sizes."""
    if n >= 1e9:
        return f"{n / 1e9:.2f} GB"
    if n >= 1e6:
        return f"{n / 1e6:.1f} MB"
    if n >= 1e3:
        return f"{n / 1e3:.0f} KB"
    return f"{n} B"


def _is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def parse_version(text):
    """A dotted version as a tuple of ints, or None."""
    if not isinstance(text, str) or not re.fullmatch(r"\d+(\.\d+)*", text.strip()):
        return None
    return tuple(int(x) for x in text.strip().split("."))


def version_older(installed, required):
    a, b = parse_version(installed), parse_version(required)
    width = max(len(a), len(b))
    return a + (0,) * (width - len(a)) < b + (0,) * (width - len(b))


def read_mod_version(path):
    """VERSION from the mod's KSP-AVC file, as a dotted string."""
    try:
        with open(path, "rb") as handle:
            data = json.loads(handle.read().decode("utf-8-sig"))
        version = data["VERSION"]
        if isinstance(version, dict):
            version = ".".join(str(version.get(k, 0)) for k in ("MAJOR", "MINOR", "PATCH", "BUILD"))
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        version = None
    if parse_version(version) is None:
        raise SkyDataError(f"Could not read the ExoInstruments version from {path}. "
                           "Install or update the mod, then rerun.")
    return version.strip()


def is_star_name(name):
    """The names the star loader reads: the main file and every GaiaStarCatalog.V*.starcat."""
    return name == MAIN_NAME or fnmatch.fnmatchcase(name, "GaiaStarCatalog.V*.starcat")


def tier_cut(name):
    match = TIER_RE.fullmatch(name)
    return float(match.group(1)) if match else None


def split_parts(name, size, part_size=PART_SIZE):
    """(asset, offset, size) of each release asset that makes up a file."""
    if size <= part_size:
        return [(name, 0, size)]
    count = -(-size // part_size)
    return [(f"{name}.{i + 1:03d}", i * part_size, min(part_size, size - i * part_size))
            for i in range(count)]


def parse_content_range(value):
    """(first, last, total) from a Content-Range header, or None."""
    match = CONTENT_RANGE.fullmatch((value or "").strip())
    if not match:
        return None
    first, last, total = (int(g) for g in match.groups())
    if first > last or last >= total:
        return None
    return first, last, total


def header_int(value):
    if value is None:
        return None
    try:
        return int(value)
    except ValueError:
        return -1


def range_verdict(status, content_range, content_length, have, size):
    """How to use the answer to `Range: bytes=<have>-` for a part of this size.

    "append" when it carries exactly the bytes asked for, "restart" when it is the whole part
    (a server that ignored the range), None when it should be retried.
    """
    if status == 206:
        if parse_content_range(content_range) != (have, size - 1, size):
            return None
        if content_length is not None and content_length != size - have:
            return None
        return "append"
    if status == 200:
        return "restart" if content_length in (None, size) else None
    return None


# ---------------------------------------------------------------------------
# The manifest


class Manifest:
    def __init__(self, data, sha256):
        self.data = data
        self.sha256 = sha256
        self.release = data["release"]
        self.min_mod_version = data["min_mod_version"]
        self.part_size = data["part_size"]
        self.files = data["files"]
        self.by_name = {entry["name"]: entry for entry in self.files}
        self.variants = data["variants"]


def validate_manifest(data, sha256):
    """Checks a parsed manifest against schema 1 and returns it as a Manifest."""
    def fail(why):
        raise SkyDataError(f"The release manifest is not valid: {why}.")

    if not isinstance(data, dict) or data.get("schema") != SCHEMA:
        fail("unknown schema")
    if data.get("release") != RELEASE or data.get("repo") != REPO:
        fail("it names another release")
    if parse_version(data.get("min_mod_version")) is None:
        fail("bad min_mod_version")
    part_size = data.get("part_size")
    if not _is_int(part_size) or part_size <= 0:
        fail("bad part_size")
    files = data.get("files")
    if not isinstance(files, list) or not files:
        fail("no files")

    names = set()
    for entry in files:
        if not isinstance(entry, dict):
            fail("a file entry is not an object")
        name = entry.get("name")
        known = isinstance(name, str) and (
            name == MAIN_NAME or tier_cut(name) is not None or name in PRODUCT_BY_NAME)
        if not known:
            fail(f"unexpected file name {name!r}")
        if name in names:
            fail(f"{name} is listed twice")
        names.add(name)
        size = entry.get("size")
        if not _is_int(size) or size <= 0:
            fail(f"bad size for {name}")
        if not isinstance(entry.get("sha256"), str) or not HEX64.fullmatch(entry["sha256"]):
            fail(f"bad sha256 for {name}")
        for key in ("licence", "credit"):
            if not isinstance(entry.get(key), str):
                fail(f"no {key} for {name}")
        cut = entry.get("cut_v")
        if name in PRODUCT_BY_NAME:
            product, group = PRODUCT_BY_NAME[name]
            if entry.get("product") != product or entry.get("group") != group or cut is not None:
                fail(f"wrong product for {name}")
        else:
            if entry.get("product") != "stars" or entry.get("group") != "stars":
                fail(f"wrong product for {name}")
            if name == MAIN_NAME:
                if cut is not None:
                    fail(f"{name} has a cut_v")
            elif (isinstance(cut, bool) or not isinstance(cut, (int, float))
                  or float(cut) != tier_cut(name)):
                fail(f"cut_v of {name} does not match its name")

        expected = split_parts(name, size, part_size)
        parts = entry.get("parts")
        if len(expected) > 999 or not isinstance(parts, list) or len(parts) != len(expected):
            fail(f"wrong parts for {name}")
        for part, (asset, offset, length) in zip(parts, expected):
            if (not isinstance(part, dict) or part.get("asset") != asset
                    or not _is_int(part.get("offset")) or part["offset"] != offset
                    or not _is_int(part.get("size")) or part["size"] != length
                    or not isinstance(part.get("sha256"), str) or not HEX64.fullmatch(part["sha256"])):
                fail(f"wrong parts for {name}")

    variants = data.get("variants")
    if not isinstance(variants, dict):
        fail("no variants")
    for key in VARIANTS:
        listed = variants.get(key)
        if (not isinstance(listed, list) or not all(isinstance(n, str) and n in names for n in listed)
                or len(set(listed)) != len(listed)):
            fail(f"bad {key} variant")
    tiers = {n for n in names if tier_cut(n) is not None}
    others = {n for n in names if n in PRODUCT_BY_NAME}
    if variants["default"] != [DEFAULT_NAME]:
        fail("bad default variant")
    if set(variants["compact"]) != tiers | others:
        fail("bad compact variant")
    if MAIN_NAME not in names or set(variants["complete"]) != tiers | others | {MAIN_NAME}:
        fail("bad complete variant")
    return Manifest(data, sha256)


# ---------------------------------------------------------------------------
# Files on disk


def fsync_dir(path):
    if os.name == "nt":
        return
    try:
        fd = os.open(path, os.O_RDONLY)
    except OSError:
        return
    try:
        os.fsync(fd)
    except OSError:
        pass
    finally:
        os.close(fd)


def is_link(st):
    """True for a symbolic link, and for a Windows junction, which lstat reports as a folder."""
    return (stat.S_ISLNK(st.st_mode)
            or getattr(st, "st_reparse_tag", 0) == IO_REPARSE_TAG_MOUNT_POINT)


def remove_entry(path):
    """Removes a file or a link without following it. False when nothing was there."""
    try:
        st = os.lstat(path)
    except FileNotFoundError:
        return False
    link = is_link(st)
    if stat.S_ISDIR(st.st_mode) and not link:
        raise SkyDataError(f"{path} is a folder. Move it away and rerun.")
    try:
        os.unlink(path)
    except (IsADirectoryError, PermissionError):
        # A link to a folder on Windows goes with rmdir, which still leaves its target alone.
        if not link:
            raise
        os.rmdir(path)
    return True


def write_atomic(path, data):
    temp = path + ".tmp"
    remove_entry(temp)
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | O_NOFOLLOW | O_BINARY, 0o644)
    with os.fdopen(fd, "wb") as handle:
        handle.write(data)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temp, path)


def ensure_staging(root):
    """The staging folder inside the real PluginData, so os.replace never crosses a drive."""
    path = os.path.join(root, STAGING_NAME)
    try:
        st = os.lstat(path)
    except FileNotFoundError:
        st = None
    if st is not None and (is_link(st) or not stat.S_ISDIR(st.st_mode)):
        remove_entry(path)
        st = None
    if st is None:
        os.mkdir(path)
    return path


def remove_empty_staging(root):
    path = os.path.join(root, STAGING_NAME)
    try:
        st = os.lstat(path)
        if stat.S_ISDIR(st.st_mode) and not is_link(st):
            os.rmdir(path)
    except OSError:
        pass


def local_error(error, name):
    """A local write failure as a message for the player."""
    if error.errno == errno.EFBIG or getattr(error, "winerror", None) == 223:
        return SkyDataError(f"This drive cannot hold {name}: its format stops at 4 GB (FAT32 or "
                            "similar). Put KSP on a drive formatted as NTFS, APFS or ext4.")
    if error.errno == errno.ENOSPC:
        return SkyDataError("The disk is full. Free some space and rerun; the download so far is kept.")
    return SkyDataError(f"Could not write {name}: {error.strerror or error}. Rerun to resume.")


def file_sha256(path, name, size, log):
    if size >= LOUD_HASH:
        log(f"Checking {name} ({human(size)})")
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(CHUNK), b""):
            digest.update(chunk)
    return digest.hexdigest()


# ---------------------------------------------------------------------------
# The receipt: what this script installed or checked, so reruns need not hash 33 GB again


def receipt_record(size, mtime_ns, sha256, linked_to):
    return {"size": size, "mtime_ns": mtime_ns, "sha256": sha256, "linked_to": linked_to}


def record_matches(record, size, mtime_ns, linked_to):
    return (record is not None and record["size"] == size and record["mtime_ns"] == mtime_ns
            and record["linked_to"] == linked_to)


def load_receipt(root):
    """The receipt, or an empty one when it is missing or unreadable."""
    try:
        with open(os.path.join(root, RECEIPT_NAME), "rb") as handle:
            data = json.loads(handle.read().decode("utf-8"))
    except (OSError, ValueError):
        data = None
    receipt = {"release": None, "files": {}}
    if not isinstance(data, dict):
        return receipt
    if isinstance(data.get("release"), str):
        receipt["release"] = data["release"]
    records = data.get("files")
    if isinstance(records, dict):
        for name, record in records.items():
            if (isinstance(record, dict) and _is_int(record.get("size"))
                    and _is_int(record.get("mtime_ns")) and isinstance(record.get("sha256"), str)
                    and (record.get("linked_to") is None or isinstance(record.get("linked_to"), str))):
                receipt["files"][name] = receipt_record(record["size"], record["mtime_ns"],
                                                        record["sha256"], record.get("linked_to"))
    return receipt


def save_receipt(root, receipt):
    body = {"release": receipt.get("release"),
            "files": {name: receipt["files"][name] for name in sorted(receipt["files"])}}
    write_atomic(os.path.join(root, RECEIPT_NAME), (json.dumps(body, indent=2) + "\n").encode("utf-8"))


# ---------------------------------------------------------------------------
# Inventory


class Found:
    """What sits at one owned name in PluginData. kind is "file", "link" or "other"."""

    def __init__(self, name, kind, size=0, mtime_ns=0, link_target=None, target_ok=True,
                 verified=False, recorded=False):
        self.name = name
        self.kind = kind
        self.size = size
        self.mtime_ns = mtime_ns
        self.link_target = link_target
        self.target_ok = target_ok
        self.verified = verified
        self.recorded = recorded


class StagingItem:
    """One entry of .sky-data. filename None stands for .sky-data itself when it is no folder."""

    def __init__(self, filename, kind, size=0, target=None, resumable=False):
        self.filename = filename
        self.kind = kind
        self.size = size
        self.target = target
        self.resumable = resumable


def inspect_name(root, name, entry, record, log, check=True):
    """What sits at one name. Hashed only when `check` and no receipt record vouches for it."""
    path = os.path.join(root, name)
    try:
        st = os.lstat(path)
    except FileNotFoundError:
        return None
    link_target = None
    if is_link(st):
        link_target = os.readlink(path)
        try:
            st = os.stat(path)
        except OSError:
            st = None
        if st is None or not stat.S_ISREG(st.st_mode):
            return Found(name, "link", link_target=link_target, target_ok=False)
        kind = "link"
    elif stat.S_ISREG(st.st_mode):
        kind = "file"
    else:
        return Found(name, "other")
    recorded = record_matches(record, st.st_size, st.st_mtime_ns, link_target)
    verified = False
    if entry is not None and st.st_size == entry["size"]:
        if recorded:
            verified = record["sha256"] == entry["sha256"]
        elif check:
            verified = file_sha256(path, name, st.st_size, log) == entry["sha256"]
    return Found(name, kind, st.st_size, st.st_mtime_ns, link_target, True, verified, recorded)


def gives_sky(item):
    """A main file or a link to one: the default set then adds nothing."""
    return item is not None and item.kind != "other" and item.target_ok


def names_to_check(manifest, variant, main):
    """The names whose content can change the plan. Anything else is never hashed."""
    if variant == "default":
        return set() if gives_sky(main) else {DEFAULT_NAME}
    # A compact run removes the main file whatever it holds, so it is not read.
    return set(manifest.variants[variant])


def take_inventory(root, manifest, receipt, log, variant):
    """Owned names present in PluginData, and the data-like files the game never reads."""
    try:
        listing = os.listdir(root)
    except FileNotFoundError:
        listing = []
    names = set(manifest.by_name) | set(PRODUCT_BY_NAME) | {n for n in listing if is_star_name(n)}
    main = inspect_name(root, MAIN_NAME, manifest.by_name.get(MAIN_NAME),
                        receipt["files"].get(MAIN_NAME), log, check=False)
    check = names_to_check(manifest, variant, main)
    found = {}
    for name in sorted(names):
        if name == MAIN_NAME and name not in check:
            item = main
        else:
            item = inspect_name(root, name, manifest.by_name.get(name), receipt["files"].get(name),
                                log, check=name in check)
        if item is not None:
            found[name] = item
    extras = sorted(n for n in listing
                    if n not in names and any(ext in n for ext in DATA_EXTENSIONS))
    return found, extras


def read_journal(path):
    try:
        with open(path, "rb") as handle:
            data = json.loads(handle.read().decode("utf-8"))
    except (OSError, ValueError):
        return None
    return data if isinstance(data, dict) else None


def journal_fits(journal, manifest, entry):
    return (journal is not None and entry is not None
            and journal.get("release") == manifest.release
            and journal.get("manifest_sha256") == manifest.sha256
            and journal.get("name") == entry["name"]
            and journal.get("sha256") == entry["sha256"]
            and _is_int(journal.get("verified_parts"))
            and 0 <= journal["verified_parts"] <= len(entry["parts"]))


def inspect_staging(root, manifest):
    path = os.path.join(root, STAGING_NAME)
    try:
        st = os.lstat(path)
    except FileNotFoundError:
        return []
    link = is_link(st)
    if link or not stat.S_ISDIR(st.st_mode):
        return [StagingItem(None, "link" if link else "file", 0 if link else st.st_size)]
    items = []
    for filename in sorted(os.listdir(path)):
        try:
            est = os.lstat(os.path.join(path, filename))
        except FileNotFoundError:
            continue
        if is_link(est):
            items.append(StagingItem(filename, "link"))
            continue
        if stat.S_ISDIR(est.st_mode):
            items.append(StagingItem(filename, "dir"))
            continue
        if filename.endswith(".part.json"):
            kind, target = "journal", filename[:-len(".part.json")]
        elif filename.endswith(".part"):
            kind, target = "part", filename[:-len(".part")]
        else:
            kind, target = "other", None
        item = StagingItem(filename, kind, est.st_size, target)
        if target is not None:
            journal = read_journal(os.path.join(path, target + ".part.json"))
            item.resumable = journal_fits(journal, manifest, manifest.by_name.get(target))
        items.append(item)
    return items


# ---------------------------------------------------------------------------
# The plan


class Removal:
    def __init__(self, parts, size, reason, link_target=None, ask=False):
        self.parts = parts          # path parts under PluginData
        self.size = size            # bytes freed; 0 for a link
        self.reason = reason
        self.link_target = link_target
        self.ask = ask

    @property
    def display(self):
        return "/".join(self.parts)


class Download:
    def __init__(self, entry, replaces=None, ask=False):
        self.entry = entry
        self.resumable = 0
        self.replaces = replaces
        self.ask = ask


class Plan:
    def __init__(self, variant):
        self.variant = variant
        self.keep = []
        self.downloads = []
        self.removals = []
        self.notes = []
        self.extras = []
        self.blocked = []

    def download_bytes(self):
        return sum(d.entry["size"] for d in self.downloads)

    def resumable_bytes(self):
        return sum(d.resumable for d in self.downloads)

    def freed_bytes(self):
        return sum(r.size for r in self.removals)

    def questions(self):
        lines = []
        for r in self.removals:
            if not r.ask:
                continue
            if r.link_target is not None:
                lines.append(f"remove the link {r.display} (the file it points to, {r.link_target}, is kept)")
            else:
                lines.append(f"delete {r.display} ({human(r.size)}), which this script did not install")
        for d in self.downloads:
            if d.ask and d.replaces is not None:
                lines.append(f"replace {d.entry['name']} ({human(d.replaces.size)}), which does not "
                             "match the release")
        return lines


def install_order(names):
    """Shallowest star tier first and the main file last, then the other products by group."""
    rank = {name: i for i, (name, _, _) in enumerate(PRODUCTS)}

    def key(name):
        if is_star_name(name):
            cut = tier_cut(name)
            return (0, cut is None, cut or 0.0, name)
        return (1, False, float(rank.get(name, len(rank))), name)
    return sorted(names, key=key)


def _removal(item, reason):
    if item.kind == "link":
        return Removal((item.name,), 0, reason, link_target=item.link_target, ask=True)
    return Removal((item.name,), item.size, reason, ask=not item.recorded)


def make_plan(manifest, variant, found, staging=(), extras=()):
    """Decides what to keep, download and remove. Pure: it reads nothing from disk."""
    plan = Plan(variant)
    plan.extras = list(extras)
    by_name = manifest.by_name
    wanted = list(manifest.variants[variant])

    if variant == "default":
        # A main file already gives a sky, and a verified tier already nests inside both variants.
        main = found.get(MAIN_NAME)
        tiers = [found[n] for n in install_order(by_name)
                 if tier_cut(n) is not None and n in found and found[n].verified]
        if gives_sky(main):
            plan.keep.append(main)
            plan.notes.append(f"{MAIN_NAME} is installed, so no star tier is added.")
            wanted = []
        elif tiers:
            plan.keep.extend(tiers)
            wanted = []
    else:
        for name in install_order(n for n in found if is_star_name(n) and n not in wanted):
            item = found[name]
            if item.kind == "other":
                plan.blocked.append(name)
            elif variant == "complete":
                plan.notes.append(f"{name} is not in this release and is left alone; the game "
                                  "checks it against the main file.")
            elif name == MAIN_NAME:
                plan.removals.append(_removal(item, "not part of the compact set"))
            else:
                plan.removals.append(_removal(
                    item, "not in this release, and without a main file it would set the star depth"))

    own_builds = {}
    for name, (_, group) in PRODUCT_BY_NAME.items():
        if name not in by_name and name in found:
            own_builds.setdefault(group, []).append(name)

    for name in install_order(wanted):
        entry = by_name[name]
        item = found.get(name)
        if item is None:
            plan.downloads.append(Download(entry))
        elif item.kind == "other":
            plan.blocked.append(name)
        elif item.verified:
            plan.keep.append(item)
        elif entry["group"] in own_builds:
            plan.notes.append(f"{name} differs from the release but is left alone: it goes with "
                              f"your own {', '.join(own_builds[entry['group']])}.")
        elif item.kind == "link" or name == MAIN_NAME:
            # A mismatched main makes the loader refuse every tier, so it goes before they arrive.
            plan.removals.append(_removal(item, "does not match the release"))
            plan.downloads.append(Download(entry))
        else:
            plan.downloads.append(Download(entry, replaces=item, ask=not item.recorded))

    queued = {d.entry["name"]: d for d in plan.downloads}
    for item in staging:
        if item.filename is None:
            plan.removals.append(Removal((STAGING_NAME,), item.size, "not a folder"))
            continue
        if item.kind == "dir":
            continue
        download = queued.get(item.target)
        if download is not None and item.resumable:
            if item.kind == "part":
                download.resumable = min(item.size, download.entry["size"])
            continue
        if variant == "default" and item.target != DEFAULT_NAME:
            continue
        plan.removals.append(Removal((STAGING_NAME, item.filename), 0 if item.kind == "link" else item.size,
                                     "left over from an earlier download"))
    return plan


def disk_need(plan):
    """Bytes the downloads still need, margin included, and bytes the removals free first."""
    todo = plan.download_bytes() - plan.resumable_bytes()
    need = todo + max(todo // 100, MARGIN_MIN) if todo > 0 else 0
    return need, plan.freed_bytes()


def has_room(need, free, freed):
    return need <= free + freed


def print_plan(plan, manifest, root, log):
    log(f"Sky data {manifest.release}, {plan.variant} set, in {root}")
    if plan.keep:
        log("Keep:")
        for item in plan.keep:
            link = f", link to {item.link_target}" if item.kind == "link" else ""
            log(f"  {item.name} ({human(item.size)}{link})")
    if plan.downloads:
        resumable = plan.resumable_bytes()
        already = f", {human(resumable)} of it already downloaded" if resumable else ""
        log(f"Download {human(plan.download_bytes())}{already}:")
        for download in plan.downloads:
            log(f"  {download.entry['name']} ({human(download.entry['size'])})")
    if plan.removals:
        freed = plan.freed_bytes()
        log(f"Remove, freeing {human(freed)}:" if freed else "Remove:")
        for removal in plan.removals:
            what = f"link to {removal.link_target}" if removal.link_target is not None else human(removal.size)
            log(f"  {removal.display} ({what}): {removal.reason}")
    for note in plan.notes:
        log(note)
    if plan.extras:
        log("Left alone, the game does not read them:")
        for name in plan.extras:
            log(f"  {name}")


# ---------------------------------------------------------------------------
# Network


class BadResponse(Exception):
    pass


def describe(error):
    if isinstance(error, urllib.error.HTTPError):
        return "the download link expired" if error.code == 403 else f"HTTP {error.code}"
    if isinstance(error, urllib.error.URLError):
        return str(error.reason)
    return str(error) or type(error).__name__


def request(url, first_byte=None):
    headers = {"User-Agent": USER_AGENT}
    if first_byte is not None:
        headers["Range"] = f"bytes={first_byte}-"
    return urllib.request.Request(url, headers=headers)


def fetch_manifest(opener, base_url, pinned, log):
    url = base_url + MANIFEST_ASSET
    error = None
    for attempt in range(3):
        if attempt:
            time.sleep(min(RETRY_MAX_DELAY, RETRY_DELAY * 2 ** (attempt - 1)))
        try:
            with opener.open(request(url), timeout=SOCKET_TIMEOUT) as response:
                body = response.read(MANIFEST_LIMIT + 1)
            break
        except urllib.error.HTTPError as failure:
            failure.close()
            if failure.code == 404:
                raise SkyDataError(f"The sky data release {RELEASE} was not found at {url}.")
            error = failure
        except (OSError, http.client.HTTPException) as failure:
            if "CERTIFICATE_VERIFY_FAILED" in str(failure):
                raise SkyDataError("Python could not check GitHub's certificate. With Python from "
                                   "python.org on macOS, run Install Certificates.command from its "
                                   "folder, then rerun.")
            error = failure
    else:
        raise SkyDataError(f"Could not download the release manifest ({describe(error)}). "
                           "Check the connection and rerun.")
    if len(body) > MANIFEST_LIMIT:
        raise SkyDataError("The release manifest is too large.")
    digest = hashlib.sha256(body).hexdigest()
    if digest != pinned:
        raise SkyDataError("The release manifest is not the one this mod build expects, so nothing "
                           "was changed. Update the mod and rerun.")
    try:
        data = json.loads(body.decode("utf-8"))
    except ValueError:
        raise SkyDataError("The release manifest is not valid JSON.")
    return validate_manifest(data, digest)


def probe_assets(opener, base_url, downloads, log):
    """Asks for the first byte of every asset a download needs, to check it exists at its size."""
    problems = []
    count = 0
    for download in downloads:
        for part in download.entry["parts"]:
            count += 1
            probe = request(base_url + part["asset"])
            probe.add_header("Range", "bytes=0-0")
            try:
                with opener.open(probe, timeout=SOCKET_TIMEOUT) as response:
                    parsed = parse_content_range(response.headers.get("Content-Range"))
                    ok = response.status == 206 and parsed is not None and parsed[2] == part["size"]
            except (OSError, http.client.HTTPException):
                ok = False
            if not ok:
                problems.append(part["asset"])
    if problems:
        log(f"Checked {count} release files; these are missing or the wrong size: {', '.join(problems)}")
    else:
        log(f"Checked {count} release files: all present at the expected sizes.")
    return not problems


class Progress:
    def __init__(self, total, done):
        self.total = total
        self.shown = 0.0
        self.printed = False
        try:
            self.live = sys.stdout.isatty()
        except (AttributeError, ValueError):
            self.live = False
        self.update(done)

    def update(self, done):
        now = time.monotonic()
        if self.live and now - self.shown >= 0.5:
            self.shown = now
            self.printed = True
            print(f"\r  {human(done)} of {human(self.total)}    ", end="", flush=True)

    def finish(self):
        if self.printed:
            self.printed = False
            print(flush=True)


class Transfer:
    """Downloads one file's parts into its staging file, resuming, then installs it."""

    def __init__(self, root, manifest, entry, base_url, opener, log):
        self.root = root
        self.manifest = manifest
        self.entry = entry
        self.base_url = base_url
        self.opener = opener
        self.log = log
        self.part_path = os.path.join(root, STAGING_NAME, entry["name"] + ".part")
        self.journal_path = self.part_path + ".json"
        self.handle = None
        self.file_hasher = None
        self.progress = None
        self.received = 0
        self.in_place = False

    def run(self):
        """Returns the receipt record of the installed file."""
        entry = self.entry
        ensure_staging(self.root)
        journal = read_journal(self.journal_path)
        if not journal_fits(journal, self.manifest, entry):
            journal = None
        self.handle = self.open_part(resume=journal is not None)
        try:
            if journal is None:
                journal = {"release": self.manifest.release, "manifest_sha256": self.manifest.sha256,
                           "name": entry["name"], "size": entry["size"], "sha256": entry["sha256"],
                           "verified_parts": 0}
                self.save_journal(journal)
            length = os.fstat(self.handle.fileno()).st_size
            if length > entry["size"]:
                self.truncate(entry["size"])
                length = entry["size"]
            parts = entry["parts"]
            verified = min(journal["verified_parts"],
                           sum(1 for p in parts if p["offset"] + p["size"] <= length))
            resuming = f", resuming after {human(length)}" if length else ""
            self.log(f"Downloading {entry['name']} ({human(entry['size'])}{resuming})")
            # Hashed on the way in when starting from zero; a resumed file is hashed once at the end.
            self.file_hasher = hashlib.sha256() if length == 0 else None
            self.progress = Progress(entry["size"], length)
            digest = self.fill(journal, verified, length)
            if digest != entry["sha256"]:
                # A part verified earlier changed on disk: check every part again and fetch only
                # the ones that no longer match.
                self.log(f"{entry['name']} does not match the release; checking each part again.")
                journal["verified_parts"] = 0
                self.save_journal(journal)
                self.file_hasher = None
                digest = self.fill(journal, 0, entry["size"])
        finally:
            if self.progress is not None:
                self.progress.finish()
            self.handle.close()
        if digest != entry["sha256"]:
            remove_entry(self.part_path)
            remove_entry(self.journal_path)
            raise SkyDataError(f"{entry['name']} does not match the release after downloading. "
                               "Rerun to fetch it again.")
        return self.install()

    def fill(self, journal, verified, length):
        """Makes each part from `verified` on match its sha256, then returns the file's sha256."""
        entry = self.entry
        parts = entry["parts"]
        for index in range(verified, len(parts)):
            part = parts[index]
            end = part["offset"] + part["size"]
            have = max(0, min(length - part["offset"], part["size"]))
            hasher = hashlib.sha256()
            if have:
                self.hash_range(part["offset"], have, hasher)
            if have < part["size"] or hasher.hexdigest() != part["sha256"]:
                if have == part["size"]:
                    have, hasher = 0, hashlib.sha256()
                # A bad part with bytes after it is rewritten in place, so the later parts stay.
                self.in_place = length > end
                if not self.in_place:
                    self.truncate(part["offset"] + have)
                try:
                    self.fetch_part(part, have, hasher)
                finally:
                    self.in_place = False
                length = max(length, end)
            # The journal only vouches for bytes already on disk.
            self.sync()
            journal["verified_parts"] = index + 1
            self.save_journal(journal)
        self.progress.finish()
        if self.file_hasher is not None:
            return self.file_hasher.hexdigest()
        if entry["size"] >= LOUD_HASH:
            self.log(f"Checking {entry['name']}")
        hasher = hashlib.sha256()
        self.hash_range(0, entry["size"], hasher)
        return hasher.hexdigest()

    def open_part(self, resume):
        try:
            st = os.lstat(self.part_path)
        except FileNotFoundError:
            st = None
        # A link or anything else at the staging path is removed, never opened.
        if st is not None and (not resume or not stat.S_ISREG(st.st_mode)):
            remove_entry(self.part_path)
            st = None
        flags = os.O_RDWR | O_BINARY | O_NOFOLLOW
        try:
            if st is None:
                fd = os.open(self.part_path, flags | os.O_CREAT | os.O_EXCL, 0o644)
            else:
                fd = os.open(self.part_path, flags)
        except OSError as error:
            raise local_error(error, self.entry["name"])
        return os.fdopen(fd, "r+b", buffering=0)

    def save_journal(self, journal):
        write_atomic(self.journal_path, json.dumps(journal).encode("utf-8"))

    def truncate(self, size):
        try:
            self.handle.truncate(size)
        except OSError as error:
            raise local_error(error, self.entry["name"])

    def sync(self):
        try:
            os.fsync(self.handle.fileno())
        except OSError as error:
            raise local_error(error, self.entry["name"])

    def write(self, offset, data):
        try:
            self.handle.seek(offset)
            view = memoryview(data)
            while view:
                view = view[self.handle.write(view):]
        except OSError as error:
            raise local_error(error, self.entry["name"])

    def hash_range(self, offset, length, hasher):
        self.handle.seek(offset)
        while length:
            chunk = self.handle.read(min(CHUNK, length))
            if not chunk:
                raise SkyDataError(f"{self.part_path} is shorter than expected. Rerun.")
            hasher.update(chunk)
            length -= len(chunk)

    def restart(self, part, snapshot):
        if not self.in_place:
            self.truncate(part["offset"])
        if snapshot is not None:
            self.file_hasher = snapshot.copy()
        return 0, hashlib.sha256()

    def fetch_part(self, part, have, hasher):
        """Fetches one part from byte `have` on, until its bytes match its sha256."""
        asset, size, offset = part["asset"], part["size"], part["offset"]
        snapshot = self.file_hasher.copy() if self.file_hasher is not None else None
        network_failures = 0
        hash_failures = 0
        while True:
            if have == size:
                if hasher.hexdigest() == part["sha256"]:
                    return
                hash_failures += 1
                if hash_failures >= HASH_TRIES:
                    raise SkyDataError(f"{asset} failed its checksum {HASH_TRIES} times. Rerun later.")
                self.log(f"{asset} did not match its checksum; downloading it again.")
                have, hasher = self.restart(part, snapshot)
                continue
            received, error, response = 0, None, None
            try:
                # Asked of github.com every time: the signed link it redirects to expires.
                response = self.opener.open(request(self.base_url + asset, have), timeout=SOCKET_TIMEOUT)
                verdict = range_verdict(response.status, response.headers.get("Content-Range"),
                                        header_int(response.headers.get("Content-Length")), have, size)
                if verdict is None:
                    raise BadResponse(f"unexpected answer {response.status} "
                                      f"{response.headers.get('Content-Range')}")
                if verdict == "restart" and have:
                    have, hasher = self.restart(part, snapshot)
                while have < size:
                    try:
                        chunk = response.read1(min(CHUNK, size - have))
                    except (OSError, http.client.HTTPException) as failure:
                        error = failure
                        break
                    if not chunk:
                        error = BadResponse("the connection closed early")
                        break
                    self.write(offset + have, chunk)
                    hasher.update(chunk)
                    if self.file_hasher is not None:
                        self.file_hasher.update(chunk)
                    have += len(chunk)
                    received += len(chunk)
                    self.progress.update(offset + have)
            except urllib.error.HTTPError as failure:
                failure.close()
                if failure.code == 404:
                    raise SkyDataError(f"The release has no file named {asset}.")
                if failure.code == 416 and have:
                    have, hasher = self.restart(part, snapshot)
                error = failure
            except (BadResponse, OSError, http.client.HTTPException, ValueError) as failure:
                error = failure
            finally:
                if response is not None:
                    response.close()
            self.received += received
            if have == size:
                continue
            network_failures = 1 if received else network_failures + 1
            if network_failures > NETWORK_TRIES:
                raise SkyDataError(f"Downloading {asset} keeps failing ({describe(error)}). "
                                   "Rerun to resume.")
            time.sleep(min(RETRY_MAX_DELAY, RETRY_DELAY * 2 ** (network_failures - 1)))

    def install(self):
        name = self.entry["name"]
        final = os.path.join(self.root, name)
        try:
            st = os.lstat(final)
        except FileNotFoundError:
            st = None
        if st is not None and is_link(st):
            remove_entry(final)
        elif st is not None and not stat.S_ISREG(st.st_mode):
            raise SkyDataError(f"{name} in PluginData is not a file. Move it away and rerun.")
        os.replace(self.part_path, final)
        fsync_dir(self.root)
        remove_entry(self.journal_path)
        st = os.stat(final)
        return receipt_record(st.st_size, st.st_mtime_ns, self.entry["sha256"], None)


# ---------------------------------------------------------------------------
# Running a variant


def confirm(questions, log):
    log("This will:")
    for question in questions:
        log(f"  {question}")
    if sys.stdin is None or not sys.stdin.isatty():
        log("Rerun with --yes to allow this.")
        return False
    try:
        answer = input("Go ahead? [y/N] ")
    except (EOFError, KeyboardInterrupt):
        print()
        return False
    return answer.strip().lower() in ("y", "yes")


def record_checked(root, receipt, manifest, plan, found):
    """Drops stale receipt records and records verified files, so the next run skips hashing."""
    files = {name: record for name, record in receipt["files"].items()
             if name in found and found[name].recorded}
    for item in plan.keep:
        entry = manifest.by_name.get(item.name)
        if entry is not None and item.verified:
            files[item.name] = receipt_record(item.size, item.mtime_ns, entry["sha256"], item.link_target)
    if files != receipt["files"]:
        receipt["files"] = files
        receipt["release"] = manifest.release
        save_receipt(root, receipt)


def star_summary(root):
    try:
        names = os.listdir(root)
    except OSError:
        names = []
    if MAIN_NAME in names:
        return f"Stars: {MAIN_NAME}, with no depth limit."
    tiers = sorted((tier_cut(n), n) for n in names if tier_cut(n) is not None)
    if tiers:
        return f"Stars: to V {tiers[-1][0]:g}, from {tiers[-1][1]}."
    return "Stars: none installed."


def join_names(names):
    names = list(names)
    return names[0] if len(names) == 1 else ", ".join(names[:-1]) + " and " + names[-1]


def local_line(products):
    """Which products are built on this computer, why, and roughly what that costs."""
    import setup_data
    size = sum(setup_data.BY_KEY[key].download_bytes for key in products)
    amount = f"{size / 1e9:.1f} GB" if size >= 1e9 else f"{size / 1e6:.0f} MB"
    # Measured on a fresh install: patches spend about an hour fetching cutouts, halpha about a minute.
    run = "up to an hour to run" if "patches" in products else "a few minutes to run"
    cost = f"about {amount} to download the first time, and {run}" if size else "a while to run"
    whose = "its" if len(products) == 1 else "their"
    return (f"{join_names(products)} on this computer, as {whose} survey terms do not allow "
            f"redistribution: {cost}")


def local_products(manifest):
    """The setup_data.py products this release does not carry, in the order it builds them."""
    import setup_data  # it imports this module, so not at the top
    return [p.key for p in setup_data.PRODUCTS
            if p.key != "stars" and p.filename not in manifest.by_name]


def not_installed(products, root):
    """The products whose file is not in PluginData."""
    import setup_data
    return [key for key in products
            if not os.path.isfile(os.path.join(root, setup_data.BY_KEY[key].filename))]


def build_here(products, builder, root, log):
    """Runs builder(products) after the downloads. Returns 0, or 1 naming what is still missing."""
    if not products:
        return EXIT_OK
    log(f"Building {local_line(products)}.")
    try:
        code = builder(products)
    except Exception as error:
        # Ctrl-C is not an Exception, so it still stops the whole run.
        log(f"Building stopped on an unexpected error: {type(error).__name__}: {error}")
        code = EXIT_FAILED
    missing = not_installed(products, root)
    if code == EXIT_OK and not missing:
        return EXIT_OK
    log(f"FAILED: {join_names(missing or products)} could not be built on this computer. "
        "The downloads are kept; rerun to resume.")
    return EXIT_FAILED


def run_local_builds(products, ksp):
    """Runs setup_data.py in this process. Returns its exit code; Ctrl-C passes through."""
    import setup_data
    try:
        return setup_data.main(["--only", ",".join(products), "--ksp", str(ksp), "--yes"]) or EXIT_OK
    except SystemExit as stop:
        return EXIT_OK if stop.code in (None, 0) else EXIT_FAILED


def install_variant(variant, plugin_data, assume_yes=False, dry_run=False, base_url=None,
                    manifest_sha256=None, log=say, local_builder=None):
    """Brings PluginData to one variant of the release. Returns 0 done, 1 failed, 2 declined.

    local_builder(products), when given, builds what the release does not carry once the
    downloads are in, and returns an exit code. Ctrl-C is not caught here, so a caller such as
    setup_data.py stops too.
    """
    try:
        return _install_variant(variant, plugin_data, assume_yes, dry_run, base_url,
                                manifest_sha256, log, local_builder)
    except SkyDataError as error:
        log(str(error))
        return error.code
    except PermissionError as error:
        log(f"Could not change {error.filename or 'a file'}: it is in use or read only. Close KSP "
            "and ExoInstruments Studio, then rerun; downloads so far are kept.")
        return EXIT_FAILED
    except OSError as error:
        log(f"Stopped: {error}. Rerun to resume.")
        return EXIT_FAILED


def _install_variant(variant, plugin_data, assume_yes, dry_run, base_url, manifest_sha256, log,
                     local_builder=None):
    if variant not in VARIANTS:
        raise ValueError(f"unknown variant {variant}")
    plugin_data = Path(plugin_data)
    root = os.path.realpath(str(plugin_data))
    installed = read_mod_version(plugin_data.parent / VERSION_FILE)
    pinned = (manifest_sha256 or MANIFEST_SHA256).lower()
    if not HEX64.fullmatch(pinned):
        raise SkyDataError("This build of ExoInstruments has no sky data release pinned yet.")
    base_url = base_url or BASE_URL
    if not base_url.endswith("/"):
        base_url += "/"
    opener = urllib.request.build_opener()

    manifest = fetch_manifest(opener, base_url, pinned, log)
    if version_older(installed, manifest.min_mod_version):
        raise SkyDataError(f"This data needs ExoInstruments {manifest.min_mod_version} or newer, and "
                           f"{installed} is installed. Update the mod first: older versions show "
                           "no stars with it.")
    # Only what is still missing, so a rerun announces no build that setup_data.py would skip.
    local = not_installed(local_products(manifest), root) if local_builder and variant != "default" else []

    receipt = load_receipt(root)
    found, extras = take_inventory(root, manifest, receipt, log, variant)
    plan = make_plan(manifest, variant, found, inspect_staging(root, manifest), extras)
    print_plan(plan, manifest, root, log)
    if plan.blocked:
        raise SkyDataError(f"{plan.blocked[0]} in PluginData is not a file or a link. "
                           "Move it away and rerun.")

    need, freed = disk_need(plan)
    free = shutil.disk_usage(root).free
    if dry_run:
        if need:
            enough = "" if has_room(need, free, freed) else ", which is not enough"
            log(f"Disk: {human(need)} needed, {human(free + freed)} available{enough}.")
        ok = probe_assets(opener, base_url, plan.downloads, log) if plan.downloads else True
        if local:
            log(f"Would then build {local_line(local)}.")
        log("Dry run: nothing was changed.")
        return EXIT_OK if ok else EXIT_FAILED

    if not plan.downloads and not plan.removals:
        record_checked(root, receipt, manifest, plan, found)
        if local:
            log("Nothing to download: every file of the release is already in place.")
        else:
            log("Nothing to do: every file is already in place.")
        log(star_summary(root))
        return build_here(local, local_builder, root, log)
    if not has_room(need, free, freed):
        raise SkyDataError(f"Not enough disk space: {human(need)} needed, {human(free + freed)} "
                           "available. Nothing was changed.")
    questions = plan.questions()
    if questions and not assume_yes and not confirm(questions, log):
        log("Nothing was changed.")
        return EXIT_DECLINED

    record_checked(root, receipt, manifest, plan, found)
    for removal in plan.removals:
        remove_entry(os.path.join(root, *removal.parts))
        if len(removal.parts) == 1 and removal.parts[0] in receipt["files"]:
            del receipt["files"][removal.parts[0]]
            save_receipt(root, receipt)
    received = 0
    for download in plan.downloads:
        transfer = Transfer(root, manifest, download.entry, base_url, opener, log)
        receipt["files"][download.entry["name"]] = transfer.run()
        receipt["release"] = manifest.release
        save_receipt(root, receipt)
        received += transfer.received
        log(f"Installed {download.entry['name']}")
    remove_empty_staging(root)
    log(f"Done: downloaded {human(received)}, freed {human(freed)}.")
    log(star_summary(root))
    code = build_here(local, local_builder, root, log)
    if code == EXIT_OK:
        log("Start KSP: its log names the star catalogue it loaded.")
    return code


# ---------------------------------------------------------------------------
# For setup_data.py


def install_local_file(source, plugin_data, name):
    """Installs a locally built file: copied into staging, then os.replace, never through a link."""
    root = os.path.realpath(str(plugin_data))
    try:
        staging = ensure_staging(root)
        temp = os.path.join(staging, name + ".local")
        remove_entry(temp)
        fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | O_NOFOLLOW | O_BINARY, 0o644)
        with os.fdopen(fd, "wb") as out, open(source, "rb") as src:
            shutil.copyfileobj(src, out, CHUNK)
            out.flush()
            os.fsync(out.fileno())
        final = os.path.join(root, name)
        try:
            if is_link(os.lstat(final)):
                remove_entry(final)
        except FileNotFoundError:
            pass
        os.replace(temp, final)
        fsync_dir(root)
        # A source build is not the release's file, so the receipt must not vouch for it.
        receipt = load_receipt(root)
        if name in receipt["files"]:
            del receipt["files"][name]
            save_receipt(root, receipt)
        remove_empty_staging(root)
    except OSError as error:
        raise local_error(error, name)


def stars_installed(plugin_data):
    """The star file that already gives a sky: a main file, or a tier the receipt vouches for."""
    root = os.path.realpath(str(plugin_data))
    if os.path.isfile(os.path.join(root, MAIN_NAME)):
        return MAIN_NAME
    receipt = load_receipt(root)
    for name in install_order(n for n in receipt["files"] if tier_cut(n) is not None):
        path = os.path.join(root, name)
        try:
            link_target = os.readlink(path) if is_link(os.lstat(path)) else None
            st = os.stat(path)
        except OSError:
            continue
        if record_matches(receipt["files"][name], st.st_size, st.st_mtime_ns, link_target):
            return name
    return None


def main(variant, argv=None, description=None):
    """Command line entry of the player scripts."""
    parser = argparse.ArgumentParser(description=description,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ksp", help="the KSP folder (default: found automatically, or $KSP)")
    parser.add_argument("--yes", action="store_true",
                        help="do not ask before removing or replacing files")
    parser.add_argument("--dry-run", action="store_true",
                        help="show what would change and check the release, changing nothing")
    parser.add_argument("--base-url", help=argparse.SUPPRESS)
    parser.add_argument("--manifest-sha256", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    if sys.version_info < (3, 9):
        say("This needs Python 3.9 or newer.")
        return EXIT_FAILED
    try:
        ksp = resolve_ksp(args.ksp)
        plugin_data = plugin_data_dir(ksp)
    except SkyDataError as error:
        say(str(error))
        return error.code
    try:
        return install_variant(variant, plugin_data, assume_yes=args.yes, dry_run=args.dry_run,
                               base_url=args.base_url, manifest_sha256=args.manifest_sha256,
                               local_builder=lambda products: run_local_builds(products, ksp))
    except KeyboardInterrupt:
        print()
        say("Stopped. Rerun to resume.")
        return EXIT_FAILED


if __name__ == "__main__":
    sys.exit("Run get_sky_data_compact.py or get_sky_data_complete.py instead.")
