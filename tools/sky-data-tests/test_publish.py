"""Tests for tools/publish_sky_data.py with synthetic files, a tiny part size and a fake gh.

    python3 -m unittest test_publish -v        (from tools/sky-data-tests)

No network: the fake gh keeps the release in a JSON file and logs every call.
"""

import argparse
import errno
import hashlib
import json
import os
import random
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
TOOL = os.path.join(TOOLS, "publish_sky_data.py")
sys.path.insert(0, TOOLS)
sys.dont_write_bytecode = True
import publish_sky_data as publish  # noqa: E402

TAG = "sky-data-1"
REPO = "batistitooo/ExoInstruments"
PART = 400
MAIN = "GaiaStarCatalog.starcat"

# name: (product, group, cut_v)
EXPECTED = {
    "GaiaStarCatalog.V13.starcat": ("stars", "stars", 13),
    "GaiaStarCatalog.V15.starcat": ("stars", "stars", 15),
    "GaiaStarCatalog.V17.starcat": ("stars", "stars", 17),
    "GaiaStarCatalog.V19.starcat": ("stars", "stars", 19),
    "GaiaStarCatalog.starcat": ("stars", "stars", None),
    "DustMap.dustmap": ("dust", "dust", None),
    "HalphaMap.emission": ("halpha", "halpha", None),
    "HalphaPatches.patchset": ("patches", "halpha", None),
    "GalaxyCatalog.galcat": ("galaxies", "galaxies", None),
    "GalaxyImages.galimg": ("images", "galaxies", None),
}
NAMES = list(EXPECTED)
TIERS = NAMES[:4]
OTHERS = NAMES[5:]

FAKE_GH = r'''
import hashlib, json, os, re, sys

state_path = os.environ["FAKE_GH_STATE"]
with open(state_path, encoding="utf-8") as handle:
    state = json.load(handle)
args = sys.argv[1:]
entry = {"argv": args}


def save():
    with open(state_path, "w", encoding="utf-8") as handle:
        json.dump(state, handle, indent=1)


def finish(code, out="", err=""):
    with open(os.environ["FAKE_GH_LOG"], "a", encoding="utf-8") as handle:
        handle.write(json.dumps(entry) + "\n")
    sys.stdout.write(out)
    sys.stderr.write(err)
    sys.exit(code)


def option(name):
    return args[args.index(name) + 1] if name in args else None


release = state.get("release")
if args[:2] == ["release", "view"]:
    if option("--repo") != state["repo"] or not release or args[2] != release["tag"]:
        finish(1, err="release not found\n")
    finish(0, out=json.dumps({"databaseId": release["id"], "tagName": release["tag"],
                              "isDraft": False, "isImmutable": False}) + "\n")

if args[:2] == ["release", "create"]:
    if release:
        finish(1, err="a release with the same tag name already exists\n")
    with open(option("--notes-file"), encoding="utf-8") as handle:
        notes = handle.read()
    state["release"] = {"id": 42, "tag": args[2], "notes": notes, "assets": []}
    save()
    finish(0)

if args[:2] == ["release", "upload"]:
    if not release or args[2] != release["tag"] or option("--repo") != state["repo"]:
        finish(1, err="release not found\n")
    rest, paths, i = args[3:], [], 0
    while i < len(rest):
        if rest[i] == "--repo":
            i += 2
            continue
        if not rest[i].startswith("--"):
            paths.append(rest[i])
        i += 1
    fail_after = os.environ.get("FAKE_GH_FAIL_AFTER")
    for path in paths:
        if fail_after is not None and state.get("uploads", 0) >= int(fail_after):
            save()
            finish(1, err="connection reset by peer\n")
        name = os.path.basename(path)
        if any(a["name"] == name for a in release["assets"]) and "--clobber" not in args:
            save()
            finish(1, err="asset under the same name already exists\n")
        with open(path, "rb") as handle:
            data = handle.read()
        folder = os.path.dirname(os.path.abspath(path))
        entry.setdefault("uploads", []).append(
            {"name": name, "dir": folder, "dir_entries": sorted(os.listdir(folder))})
        release["assets"] = [a for a in release["assets"] if a["name"] != name]
        release["assets"].append({"id": 1000 + state.get("uploads", 0), "name": name,
                                  "size": len(data), "state": "uploaded",
                                  "digest": "sha256:" + hashlib.sha256(data).hexdigest()})
        state["uploads"] = state.get("uploads", 0) + 1
    save()
    finish(0)

if args[:1] == ["api"]:
    endpoint = [a for a in args[1:] if not a.startswith("--")][0]
    match = re.fullmatch(r"repos/(.+)/releases/(\d+)/assets\?per_page=100", endpoint)
    if not match or match.group(1) != state["repo"] or not release \
            or int(match.group(2)) != release["id"]:
        finish(1, err="gh: Not Found (HTTP 404)\n")
    # Two assets a page, printed back to back as real gh does without --slurp.
    assets = release["assets"]
    pages = [assets[i:i + 2] for i in range(0, len(assets), 2)] or [[]]
    if "--paginate" not in args:
        pages = pages[:1]
    finish(0, out="".join(json.dumps(page) for page in pages) + "\n")

finish(2, err="fake gh: unsupported command " + " ".join(args) + "\n")
'''


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read(path):
    with open(path, "rb") as handle:
        return handle.read()


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(data)


def filler(seed, size):
    block = hashlib.sha256(seed.encode()).digest()
    return (block * (size // len(block) + 1))[:size]


def milli(cut):
    return int(round((cut + 2.0) * 1000))


def star_catalogue(bands, version=3):
    """bands: the record bytes of each declination band."""
    index = [0]
    for band in bands:
        index.append(index[-1] + len(band))
    return (b"EXOSTAR1" + struct.pack("<iiif", version, index[-1], len(bands), 0.1)
            + struct.pack(f"<{len(bands) + 1}I", *index) + b"".join(b"".join(band) for band in bands))


def star_files(version=3, seed="sky"):
    """Twenty bands of five stars at V 12 to 20 in shuffled order; each tier keeps what its cut keeps.

    V13 is 388 bytes (one part at 400), the rest split. Returns {name: bytes} and the main's bands.
    """
    rng = random.Random(seed)
    bands = []
    for _ in range(20):
        mags = [12, 14, 16, 18, 20]
        rng.shuffle(mags)
        band = []
        for mag in mags:
            record = struct.pack("<IiHh", rng.getrandbits(32), rng.randint(-2 ** 31, 2 ** 31 - 1),
                                 milli(mag), rng.randint(-500, 2000))
            band.append(record + struct.pack("<H", rng.getrandbits(16)) if version == 3 else record)
        bands.append(band)
    files = {MAIN: star_catalogue(bands, version)}
    for name in TIERS:
        cut = milli(EXPECTED[name][2])
        files[name] = star_catalogue([[r for r in band if struct.unpack_from("<H", r, 8)[0] <= cut]
                                      for band in bands], version)
    return files, bands


def string(text):
    data = text.encode("utf-8")
    return struct.pack("<i", len(data)) + data


def padded(size, build):
    """build(source), its source string grown so the file is exactly size bytes."""
    shortest = len(build(""))
    assert size >= shortest, (size, shortest)
    return build("s" * (size - shortest))


def dust_map(size, nside=2):
    return padded(size, lambda source: b"EXODUST1" + struct.pack("<iiB", 2, nside, 0) + string(source)
                  + filler("dust", 24 * nside * nside))


def emission_map(size, nside=2):
    return padded(size, lambda source: b"EXOEMIS1" + struct.pack("<iiBd", 2, nside, 0, 6.5628e-7)
                  + string("H-alpha") + string(source) + filler("halpha", 24 * nside * nside))


def patch_set(size):
    def build(source):
        data = (b"EXOPTCH3" + struct.pack("<iiBd", 3, 4, 0, 6.5628e-7) + string("H-alpha")
                + string(source) + struct.pack("<i", 2))
        for i, runs in enumerate(([(10, 3), (20, 2)], [(5, 4)])):
            cells = sum(n for _, n in runs)
            data += (string(f"patch {i}") + struct.pack("<ddfii", 10.0 * i, -20.0, 1.5, 4, len(runs))
                     + b"".join(struct.pack("<ii", s, n) for s, n in runs))
            planes = 2 - i
            data += struct.pack("<i", planes)
            for q in range(planes):
                data += (string("[N II]" if q else "H-alpha") + struct.pack("<d", 6.5628e-7)
                         + filler(f"patch {i} {q}", 2 * cells))
        return data
    return padded(size, build)


def galaxy_catalogue(size, version=2):
    def build(source):
        data = b"EXOGALX1" + struct.pack("<ii", version, 2) + string(source)
        for name in ("NGC0224", "NGC0598"):
            data += string(name) + struct.pack("<dd7f", 10.7, 41.3, 4.4, 0.9, 190.0, 0.3, 35.0, 3.0, 2.0)
            data += (struct.pack("<f", 24.4) if version >= 2 else b"") + b"\0"
        return data
    return padded(size, build)


def galaxy_images(size, side=8):
    def build(source):
        data = b"EXOGIMG1" + struct.pack("<ii", 1, 1) + string(source)
        data += (string("NGC0224") + struct.pack("<ddid", 10.68, 41.27, side, 0.5) + string("ls-dr10")
                 + struct.pack("<Bffi", 1, 0.01, 0.9, 1) + string("NGC0221") + struct.pack("<i", 2))
        for band in range(2):
            data += (struct.pack("<d", 4.8e-7 + band * 1e-7) + string("gr"[band])
                     + struct.pack("<d", 1.0) + filler(f"image {band}", 2 * side * side))
        return data
    return padded(size, build)


def other_files():
    return {
        "DustMap.dustmap": dust_map(300),
        "HalphaMap.emission": emission_map(PART),           # exactly one part
        "HalphaPatches.patchset": patch_set(2 * PART),       # exactly two
        "GalaxyCatalog.galcat": galaxy_catalogue(150),
        "GalaxyImages.galimg": galaxy_images(1000),
    }


def make_plugin_data(plugin, elsewhere):
    data = star_files()[0]
    data.update(other_files())
    os.makedirs(plugin)
    linked = False
    for name in NAMES:
        content = data[name]
        if name == MAIN:
            # The all-sky file kept outside the install, as on the maintainer's machine.
            target = os.path.join(elsewhere, "GaiaAllSky.starcat")
            write(target, content)
            try:
                os.symlink(target, os.path.join(plugin, name))
                linked = True
                continue
            except (OSError, NotImplementedError):
                pass
        write(os.path.join(plugin, name), content)
    return linked


def notice_source(leave_out=()):
    """A hand written NOTICE stand in stating every name and every filled in licence and credit."""
    lines = ["# Sky data: licences and credits", ""]
    lines += [f"- `{name}`" for name in NAMES]
    for key, info in publish.PRODUCTS.items():
        for field in ("licence", "credit"):
            if info[field] not in leave_out:
                lines += ["", f"{key} {field}: {info[field]}"]
    return ("\n".join(lines) + "\n").encode("utf-8")


def install_fake_gh(folder):
    os.makedirs(folder)
    script = os.path.join(folder, "fake_gh.py")
    with open(script, "w", encoding="utf-8") as handle:
        handle.write(FAKE_GH)
    if os.name == "nt":
        with open(os.path.join(folder, "gh.cmd"), "w") as handle:
            handle.write(f'@echo off\r\n"{sys.executable}" "{script}" %*\r\n')
    else:
        launcher = os.path.join(folder, "gh")
        with open(launcher, "w") as handle:
            handle.write(f'#!/bin/sh\nexec "{sys.executable}" "{script}" "$@"\n')
        os.chmod(launcher, 0o755)


def snapshot(folder):
    result = {}
    for name in sorted(os.listdir(folder)):
        path = os.path.join(folder, name)
        info = os.lstat(path)
        result[name] = (os.path.islink(path), os.readlink(path) if os.path.islink(path) else None,
                        info.st_mtime_ns, sha(read(path)))
    return result


class FailingFile:
    """Wraps a part being written so its third write raises."""

    def __init__(self, inner, error):
        self.inner = inner
        self.error = error
        self.writes = 0

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.inner.close()
        return False

    def write(self, data):
        self.writes += 1
        if self.writes == 3:
            raise self.error
        return self.inner.write(data)


class PublishTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="publish-sky-data-")
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.plugin = os.path.join(self.tmp, "PluginData")
        self.out = os.path.join(self.tmp, "out")
        self.parts = os.path.join(self.tmp, "parts")
        self.bin = os.path.join(self.tmp, "bin")
        self.state = os.path.join(self.tmp, "gh-state.json")
        self.gh_log = os.path.join(self.tmp, "gh-log.jsonl")
        self.notice = os.path.join(self.tmp, "repo", publish.NOTICE_NAME)
        write(self.notice, notice_source())
        self.linked = make_plugin_data(self.plugin, os.path.join(self.tmp, "elsewhere"))
        install_fake_gh(self.bin)
        self.set_release(None)

    def set_release(self, release):
        with open(self.state, "w", encoding="utf-8") as handle:
            json.dump({"repo": REPO, "release": release}, handle)

    def release_state(self):
        with open(self.state, encoding="utf-8") as handle:
            return json.load(handle)["release"]

    def run_tool(self, *extra, env_extra=None, out=None, temp_dir=None, notice=None):
        env = dict(os.environ)
        env["PATH"] = self.bin + os.pathsep + env.get("PATH", "")
        env["FAKE_GH_STATE"] = self.state
        env["FAKE_GH_LOG"] = self.gh_log
        env["PYTHONDONTWRITEBYTECODE"] = "1"
        env.pop("FAKE_GH_FAIL_AFTER", None)
        env.update(env_extra or {})
        command = [sys.executable, TOOL, "--plugin-data", self.plugin, "--out", out or self.out,
                   "--temp-dir", temp_dir or self.parts, "--notice", notice or self.notice,
                   "--part-size", str(PART)] + list(extra)
        return subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              universal_newlines=True, env=env, timeout=300)

    def gh_calls(self):
        if not os.path.exists(self.gh_log):
            return []
        with open(self.gh_log, encoding="utf-8") as handle:
            return [json.loads(line) for line in handle if line.strip()]

    def clear_gh_log(self):
        if os.path.exists(self.gh_log):
            os.unlink(self.gh_log)

    def manifest(self, out=None):
        return json.loads(read(os.path.join(out or self.out, publish.MANIFEST_NAME)).decode("utf-8"))

    def expected_assets(self, manifest):
        return ([p["asset"] for f in manifest["files"] for p in f["parts"]]
                + [publish.NOTICE_NAME, publish.SUMS_NAME, publish.MANIFEST_NAME])

    def uploads(self, calls):
        return [u for c in calls for u in c.get("uploads", [])]

    def assert_no_writes_to_github(self, calls):
        for call in calls:
            self.assertNotIn(call["argv"][:2], (["release", "upload"], ["release", "create"]),
                             call["argv"])

    def assert_parts_dir_clean(self, folder=None):
        folder = folder or self.parts
        self.assertFalse(os.path.isdir(folder) and os.listdir(folder),
                         "temporary part files were left behind")

    def assert_published_matches(self, manifest, out=None):
        published = {a["name"]: a for a in self.release_state()["assets"]}
        for entry in manifest["files"]:
            for part in entry["parts"]:
                self.assertEqual(published[part["asset"]]["size"], part["size"])
                self.assertEqual(published[part["asset"]]["digest"], "sha256:" + part["sha256"])
        for name in (publish.NOTICE_NAME, publish.SUMS_NAME, publish.MANIFEST_NAME):
            self.assertEqual(published[name]["digest"],
                             "sha256:" + sha(read(os.path.join(out or self.out, name))))

    def check(self, name, data):
        """publish.check_file on data written under name."""
        path = os.path.join(self.tmp, "check", name)
        write(path, data)
        product, _, cut_v = EXPECTED[name]
        with mock.patch.object(publish, "log"):
            return publish.check_file(publish.DataFile(name, path, product, cut_v))

    # -----------------------------------------------------------------------

    def test_part_layout_matches_the_release_sizes(self):
        self.assertEqual(publish.PART_SIZE, 1992294400)
        main = publish.part_layout(MAIN, 25287569276, publish.PART_SIZE)
        self.assertEqual(len(main), 13)
        self.assertEqual(main[0], (MAIN + ".001", 0, 1992294400))
        self.assertEqual(main[-1], (MAIN + ".013", 12 * 1992294400, 1380036476))
        v19 = publish.part_layout("GaiaStarCatalog.V19.starcat", 5976401666, publish.PART_SIZE)
        self.assertEqual([p[0] for p in v19], ["GaiaStarCatalog.V19.starcat.00%d" % i for i in (1, 2, 3)])
        self.assertEqual(v19[-1][2], 1991812866)
        self.assertEqual(publish.part_layout("GaiaStarCatalog.V17.starcat", 1640959044, publish.PART_SIZE),
                         [("GaiaStarCatalog.V17.starcat", 0, 1640959044)])
        self.assertEqual(publish.part_layout("x", publish.PART_SIZE, publish.PART_SIZE),
                         [("x", 0, publish.PART_SIZE)])

    def test_manifest_and_parts(self):
        before = snapshot(self.plugin)
        result = self.run_tool()
        self.assertEqual(result.returncode, 0, result.stderr)
        raw = read(os.path.join(self.out, publish.MANIFEST_NAME))
        manifest = json.loads(raw.decode("utf-8"))
        self.assertIn(sha(raw), result.stdout)

        self.assertEqual(list(manifest), ["schema", "release", "repo", "min_mod_version",
                                          "part_size", "files", "variants"])
        self.assertEqual([manifest[k] for k in ("schema", "release", "repo", "min_mod_version",
                                                "part_size")], [1, TAG, REPO, "0.5.0", PART])
        self.assertEqual([f["name"] for f in manifest["files"]], NAMES)
        for entry in manifest["files"]:
            name = entry["name"]
            data = read(os.path.join(self.plugin, name))
            self.assertEqual(list(entry), ["name", "size", "sha256", "product", "group", "cut_v",
                                           "licence", "credit", "parts"])
            self.assertEqual((entry["product"], entry["group"], entry["cut_v"]), EXPECTED[name])
            self.assertEqual((entry["size"], entry["sha256"]), (len(data), sha(data)))
            self.assertEqual((entry["licence"], entry["credit"]),
                             (publish.PRODUCTS[entry["product"]]["licence"],
                              publish.PRODUCTS[entry["product"]]["credit"]))
            parts = entry["parts"]
            if len(data) > PART:
                self.assertEqual([p["asset"] for p in parts],
                                 ["%s.%03d" % (name, i + 1) for i in range(-(-len(data) // PART))])
            else:
                self.assertEqual([p["asset"] for p in parts], [name])
            for i, part in enumerate(parts):
                self.assertEqual(list(part), ["asset", "offset", "size", "sha256"])
                self.assertEqual(part["offset"], i * PART)
                self.assertEqual(part["size"], min(PART, len(data) - part["offset"]))
                self.assertEqual(part["sha256"], sha(data[part["offset"]:part["offset"] + part["size"]]))
        split = {f["name"]: len(f["parts"]) for f in manifest["files"]}
        self.assertEqual(split["GaiaStarCatalog.V13.starcat"], 1)
        self.assertEqual(split["HalphaMap.emission"], 1)       # exactly one part size
        self.assertEqual(split["HalphaPatches.patchset"], 2)   # exactly two
        self.assertEqual(split[MAIN], 4)

        compact = TIERS + OTHERS
        self.assertEqual(manifest["variants"], {"default": ["GaiaStarCatalog.V13.starcat"],
                                                "compact": compact, "complete": compact + [MAIN]})

        # The NOTICE asset is the hand written source, then the generated table.
        notice = read(os.path.join(self.out, publish.NOTICE_NAME))
        self.assertTrue(notice.startswith(read(self.notice)))
        table = notice[len(read(self.notice)):].decode("utf-8")
        self.assertIn(f"## Files in {TAG}", table)
        for name in NAMES:
            self.assertIn(f"| {name} |", table)
        self.assertNotIn("\u2014", table)
        self.assertNotIn(" -- ", table)
        self.assertEqual(read(self.notice), notice_source())

        self.assertEqual(snapshot(self.plugin), before)
        if self.linked:
            self.assertIn("read through its link", result.stdout)

    def test_sha256sums_verify_with_a_stdlib_rehash(self):
        result = self.run_tool()
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest = self.manifest()
        # The release as a downloader sees it: every asset, plus every joined file.
        release = os.path.join(self.tmp, "release")
        for entry in manifest["files"]:
            data = read(os.path.join(self.plugin, entry["name"]))
            write(os.path.join(release, entry["name"]), data)
            for part in entry["parts"]:
                write(os.path.join(release, part["asset"]),
                      data[part["offset"]:part["offset"] + part["size"]])
        for name in (publish.NOTICE_NAME, publish.MANIFEST_NAME):
            shutil.copyfile(os.path.join(self.out, name), os.path.join(release, name))

        raw = read(os.path.join(self.out, publish.SUMS_NAME))
        self.assertNotIn(b"\r", raw)
        self.assertTrue(raw.endswith(b"\n"))
        listed = {}
        for line in raw.decode("ascii").splitlines():
            match = re.fullmatch(r"([0-9a-f]{64})  (\S+)", line)
            self.assertIsNotNone(match, line)
            self.assertNotIn(match.group(2), listed)
            listed[match.group(2)] = match.group(1)
        self.assertEqual(set(listed), set(NAMES) | set(self.expected_assets(manifest))
                         - {publish.SUMS_NAME})
        for name, digest in listed.items():
            self.assertEqual(sha(read(os.path.join(release, name))), digest, name)
        for entry in manifest["files"]:
            joined = b"".join(read(os.path.join(release, p["asset"])) for p in entry["parts"])
            self.assertEqual(sha(joined), listed[entry["name"]])

    def test_upload_creates_the_release_and_uploads_every_asset_once(self):
        result = self.run_tool("--upload", "--allow-placeholders")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        manifest = self.manifest()
        calls = self.gh_calls()

        creates = [c["argv"] for c in calls if c["argv"][:2] == ["release", "create"]]
        self.assertEqual(creates, [["release", "create", TAG, "--repo", REPO, "--title", "Sky data 1",
                                    "--notes-file", os.path.join(self.out, publish.NOTICE_NAME),
                                    "--latest=false"]])
        self.assertEqual(self.release_state()["notes"],
                         read(os.path.join(self.out, publish.NOTICE_NAME)).decode("utf-8"))

        uploads = self.uploads(calls)
        self.assertEqual([u["name"] for u in uploads], self.expected_assets(manifest))
        self.assert_published_matches(manifest)

        # Split parts went through one folder the tool made inside --temp-dir, alone, and are gone.
        through_temp = [u for u in uploads
                        if os.path.normcase(os.path.realpath(os.path.dirname(u["dir"])))
                        == os.path.normcase(os.path.realpath(self.parts))]
        split_parts = [p["asset"] for f in manifest["files"] if len(f["parts"]) > 1 for p in f["parts"]]
        self.assertEqual(sorted(u["name"] for u in through_temp), sorted(split_parts))
        self.assertEqual(len({u["dir"] for u in through_temp}), 1)
        self.assertTrue(os.path.basename(through_temp[0]["dir"]).startswith(publish.PARTS_PREFIX))
        for upload in through_temp:
            self.assertEqual(upload["dir_entries"], [upload["name"]])
        self.assert_parts_dir_clean()
        self.assertIn("match the manifest", result.stdout)

    def test_rerun_after_an_interrupted_upload_uploads_only_what_is_missing(self):
        first = self.run_tool("--upload", "--allow-placeholders", env_extra={"FAKE_GH_FAIL_AFTER": "5"})
        self.assertNotEqual(first.returncode, 0)
        self.assertIn("Rerun", first.stderr)
        self.assert_parts_dir_clean()
        published = [a["name"] for a in self.release_state()["assets"]]
        self.assertEqual(len(published), 5)

        self.clear_gh_log()
        second = self.run_tool("--upload", "--allow-placeholders")
        self.assertEqual(second.returncode, 0, second.stdout + second.stderr)
        manifest = self.manifest()
        calls = self.gh_calls()
        self.assertFalse([c for c in calls if c["argv"][:2] == ["release", "create"]])
        self.assertEqual([u["name"] for u in self.uploads(calls)],
                         [a for a in self.expected_assets(manifest) if a not in published])
        self.assert_published_matches(manifest)
        self.assert_parts_dir_clean()

        self.clear_gh_log()
        third = self.run_tool("--upload", "--allow-placeholders")
        self.assertEqual(third.returncode, 0, third.stderr)
        self.assertEqual(self.uploads(self.gh_calls()), [])

    def test_a_published_asset_with_another_digest_is_refused(self):
        self.assertEqual(self.run_tool().returncode, 0)
        manifest = self.manifest()
        main = next(f for f in manifest["files"] if f["name"] == MAIN)
        v13 = manifest["files"][0]
        wrong = main["parts"][1]
        self.set_release({"id": 42, "tag": TAG, "notes": "", "assets": [
            {"id": 1, "name": v13["name"], "size": v13["size"], "state": "uploaded",
             "digest": "sha256:" + v13["sha256"]},
            {"id": 2, "name": wrong["asset"], "size": wrong["size"], "state": "uploaded",
             "digest": "sha256:" + "0" * 64},
        ]})
        before = self.release_state()

        result = self.run_tool("--upload", "--allow-placeholders")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(wrong["asset"], result.stderr)
        self.assertIn("--replace", result.stderr)
        self.assert_no_writes_to_github(self.gh_calls())
        self.assertEqual(self.release_state(), before)

        self.clear_gh_log()
        replaced = self.run_tool("--upload", "--allow-placeholders", "--replace")
        self.assertEqual(replaced.returncode, 0, replaced.stdout + replaced.stderr)
        calls = [c["argv"] for c in self.gh_calls() if c["argv"][:2] == ["release", "upload"]]
        clobbered = [argv for argv in calls if "--clobber" in argv]
        self.assertEqual(len(clobbered), 1)
        self.assertEqual(os.path.basename(clobbered[0][3]), wrong["asset"])
        self.assertNotIn(v13["name"], [os.path.basename(argv[3]) for argv in calls])
        self.assert_published_matches(manifest)

    def test_dry_run_makes_no_upload_calls_and_writes_nothing(self):
        result = self.run_tool("--dry-run")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.gh_calls(), [])

        result = self.run_tool("--upload", "--dry-run")
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.gh_calls()
        self.assertTrue(calls, "the dry run should still read the release")
        self.assert_no_writes_to_github(calls)
        self.assertFalse(os.path.exists(self.out))
        self.assertFalse(os.path.exists(self.parts))
        data_bytes = sum(os.path.getsize(os.path.join(self.plugin, n)) for n in NAMES)
        self.assertIn(f"{data_bytes:,}", result.stdout)
        self.assertIn("Would create release", result.stdout)
        self.assertIsNone(self.release_state())

    # Temporary files and folders

    def test_a_temp_dir_or_out_among_the_data_files_is_refused(self):
        before = snapshot(self.plugin)
        for flags in ({"temp_dir": self.plugin}, {"out": self.plugin}):
            with self.subTest(**flags):
                result = self.run_tool("--upload", "--allow-placeholders", **flags)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("holds the data files", result.stderr)
                self.assertEqual(self.gh_calls(), [])
                self.assertEqual(snapshot(self.plugin), before)
                self.assertIsNone(self.release_state())

    def test_temp_dir_equal_to_out_keeps_the_metadata(self):
        result = self.run_tool("--upload", "--allow-placeholders", temp_dir=self.out)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(sorted(os.listdir(self.out)),
                         sorted([publish.MANIFEST_NAME, publish.NOTICE_NAME, publish.SUMS_NAME]))
        self.assert_published_matches(self.manifest())

    def test_files_in_the_temp_dir_the_tool_did_not_make_are_left_alone(self):
        stranger = os.path.join(self.parts, MAIN + ".002")
        stale = os.path.join(self.parts, publish.PARTS_PREFIX + "old")
        write(stranger, b"not the tool's")
        write(os.path.join(stale, MAIN + ".003"), b"left by a killed run")
        result = self.run_tool("--upload", "--allow-placeholders")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(stale, result.stdout)
        self.assertEqual(read(stranger), b"not the tool's")
        self.assertEqual(sorted(os.listdir(self.parts)), sorted([os.path.basename(stranger),
                                                                  os.path.basename(stale)]))
        self.assert_published_matches(self.manifest())

    def test_a_failed_copy_leaves_no_partial_part(self):
        data = filler("source", 3 * PART)
        source = os.path.join(self.tmp, "source.starcat")
        write(source, data)
        parts_dir = os.path.join(self.tmp, "own-parts")
        os.makedirs(parts_dir)
        good = {"name": MAIN + ".002", "path": source, "offset": PART, "size": PART,
                "sha256": sha(data[PART:2 * PART])}
        args = argparse.Namespace(tag=TAG, repo=REPO)
        real_fdopen = os.fdopen
        cases = [("interrupted", KeyboardInterrupt(), good),
                 ("disk full", OSError(errno.ENOSPC, "No space left on device"), good),
                 ("changed since hashing", None, dict(good, sha256="0" * 64))]
        for label, error, asset in cases:
            with self.subTest(label):
                opened = []

                def fdopen(*a, **k):
                    handle = real_fdopen(*a, **k)
                    if error is None:
                        return handle
                    opened.append(FailingFile(handle, error))
                    return opened[-1]

                with mock.patch.object(publish, "CHUNK", 100), \
                        mock.patch.object(publish.os, "fdopen", fdopen), \
                        mock.patch.object(publish, "run_gh", side_effect=AssertionError("gh ran")), \
                        mock.patch.object(publish, "die", side_effect=SystemExit(1)):
                    with self.assertRaises(SystemExit if error is None else type(error)):
                        publish.upload_asset("gh", args, asset, parts_dir, clobber=False)
                if error is not None:
                    self.assertEqual(opened[0].writes, 3, "the copy should have started")
                self.assertEqual(os.listdir(parts_dir), [])

    # Refusals before anything is hashed

    def test_upload_refuses_placeholder_licences_before_calling_gh(self):
        if not any(publish.PLACEHOLDER in (p["licence"], p["credit"]) for p in publish.PRODUCTS.values()):
            self.skipTest("every licence and credit is filled in")
        result = self.run_tool("--upload")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(publish.PLACEHOLDER, result.stderr)
        self.assertEqual(self.gh_calls(), [])

    def test_a_release_without_the_halpha_products_needs_no_placeholder(self):
        rest = [p for key, p in publish.PRODUCTS.items() if key not in ("halpha", "patches")]
        if any(publish.PLACEHOLDER in (p["licence"], p["credit"]) for p in rest):
            self.skipTest("a product other than halpha and patches is still a placeholder")
        result = self.run_tool("--upload", "--exclude", "halpha,patches")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        manifest = self.manifest()
        names = [n for n in NAMES if EXPECTED[n][0] not in ("halpha", "patches")]
        self.assertEqual([f["name"] for f in manifest["files"]], names)
        self.assertEqual(manifest["variants"]["complete"], TIERS + [n for n in names if n in OTHERS] + [MAIN])
        self.assert_published_matches(manifest)

    def test_upload_refuses_a_notice_that_does_not_state_a_licence(self):
        stars = publish.PRODUCTS["stars"]["licence"]
        if stars == publish.PLACEHOLDER:
            self.skipTest("the star licence is still a placeholder")
        other = os.path.join(self.tmp, "other", publish.NOTICE_NAME)
        write(other, notice_source(leave_out=(stars,)))
        result = self.run_tool("--upload", "--allow-placeholders", notice=other)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(stars, result.stderr)
        self.assertEqual(self.gh_calls(), [])

        result = self.run_tool("--dry-run", notice=other)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("WARNING", result.stdout)

    def test_out_over_the_notice_source_is_refused(self):
        result = self.run_tool(out=os.path.dirname(self.notice))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("NOTICE source", result.stderr)
        self.assertEqual(read(self.notice), notice_source())

    def test_upload_refuses_a_release_missing_a_star_file(self):
        result = self.run_tool("--upload", "--allow-placeholders", "--include", "dust,halpha,galaxies")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("GaiaStarCatalog.V13.starcat", result.stderr)
        self.assertEqual(self.gh_calls(), [])

    def test_leaving_out_images(self):
        os.unlink(os.path.join(self.plugin, "GalaxyImages.galimg"))
        result = self.run_tool()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--exclude", result.stderr)

        result = self.run_tool("--exclude", "images")
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest = self.manifest()
        self.assertNotIn("GalaxyImages.galimg", [f["name"] for f in manifest["files"]])
        self.assertEqual(manifest["variants"]["compact"], TIERS + OTHERS[:-1])

    # File checks

    def test_a_star_catalogue_of_the_wrong_size_is_refused(self):
        with open(os.path.join(self.plugin, "GaiaStarCatalog.V19.starcat"), "ab") as handle:
            handle.write(b"\0")
        result = self.run_tool("--dry-run")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("GaiaStarCatalog.V19.starcat", result.stderr)
        self.assertIn("need", result.stderr)

    def test_a_tier_deeper_than_the_next_is_refused(self):
        bands = star_files()[1]
        write(os.path.join(self.plugin, "GaiaStarCatalog.V15.starcat"),
              star_catalogue([band[:4] for band in bands]))
        result = self.run_tool("--dry-run")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("GaiaStarCatalog.V15.starcat", result.stderr)

    def test_a_tier_cut_from_another_catalogue_is_refused(self):
        path = os.path.join(self.plugin, "GaiaStarCatalog.V17.starcat")
        data = bytearray(read(path))
        data[24 + 4 * 21] ^= 0xFF   # the first star's right ascension; header and counts still agree
        write(path, bytes(data))
        result = self.run_tool("--dry-run")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("GaiaStarCatalog.V17.starcat: declination band 0", result.stderr)
        self.assertIn("another catalogue", result.stderr)

    def test_tier_records_are_compared_in_version_2_and_without_a_main_file(self):
        for version in (2, 3):
            with self.subTest(version=version):
                data, bands = star_files(version, seed=f"v{version}")
                files = []
                for name in NAMES[:5]:
                    path = os.path.join(self.tmp, f"v{version}", name)
                    write(path, data[name])
                    files.append(publish.DataFile(name, path, "stars", EXPECTED[name][2]))
                with mock.patch.object(publish, "log"):
                    self.assertEqual([publish.check_file(f) for f in files], [None] * 5)
                    self.assertEqual(publish.tier_faults(files), [])
                    self.assertEqual(publish.tier_record_faults(files), [])
                    self.assertEqual(publish.tier_record_faults(files[:4]), [])

                    # The last band is always sampled: swap V15's two stars there.
                    last = bands[-1]
                    cut = milli(15)
                    kept = [r for r in last if struct.unpack_from("<H", r, 8)[0] <= cut]
                    tampered = data["GaiaStarCatalog.V15.starcat"][:-2 * len(kept[0])] + kept[1] + kept[0]
                    write(files[1].path, tampered)
                    self.assertIsNone(publish.check_file(files[1]))
                    self.assertEqual(publish.tier_faults(files), [])
                    faults = publish.tier_record_faults(files)
                    self.assertEqual(len(faults), 1)
                    self.assertIn("GaiaStarCatalog.V15.starcat: declination band 19", faults[0])
                    self.assertEqual(len(publish.tier_record_faults(files[:4])), 1)

    def test_a_file_with_the_wrong_magic_is_refused(self):
        write(os.path.join(self.plugin, "DustMap.dustmap"), emission_map(300))
        result = self.run_tool("--dry-run")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("EXODUST1", result.stderr)

    def test_a_truncated_dust_map_is_refused(self):
        path = os.path.join(self.plugin, "DustMap.dustmap")
        write(path, read(path)[:20])
        result = self.run_tool("--dry-run")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("DustMap.dustmap: 20 bytes", result.stderr)

    def test_every_product_is_checked_down_to_its_exact_size(self):
        files = other_files()
        for name, data in files.items():
            with self.subTest(name, case="valid"):
                self.assertIsNone(self.check(name, data))
            with self.subTest(name, case="one byte short"):
                self.assertIn("ends inside", self.check(name, data[:-1]))
            with self.subTest(name, case="one byte long"):
                self.assertIn("1 bytes follow", self.check(name, data + b"\0"))
            with self.subTest(name, case="only the magic"):
                self.assertIn("ends inside", self.check(name, data[:8]))

        with self.subTest("galaxy catalogue version 1"):
            self.assertIn("format version 1", self.check("GalaxyCatalog.galcat", galaxy_catalogue(150, 1)))
        with self.subTest("dust map with a bad nside"):
            data = bytearray(files["DustMap.dustmap"])
            data[12:16] = struct.pack("<i", 3)
            self.assertIn("not a HEALPix nside", self.check("DustMap.dustmap", bytes(data)))
        with self.subTest("patch set with an unsorted run"):
            data = files["HalphaPatches.patchset"]
            runs = struct.pack("<iiii", 10, 3, 20, 2)
            self.assertIn(runs, data)
            unsorted = data.replace(runs, struct.pack("<iiii", 20, 3, 10, 2), 1)
            self.assertIn("unsorted run", self.check("HalphaPatches.patchset", unsorted))
        with self.subTest("image set with more pixels than its size says"):
            data = bytearray(files["GalaxyImages.galimg"])
            side = struct.pack("<i", 8)
            at = data.index(struct.pack("<dd", 10.68, 41.27)) + 16
            self.assertEqual(bytes(data[at:at + 4]), side)
            data[at:at + 4] = struct.pack("<i", 9)
            # The walk then reads pixels as the next field, so any refusal will do.
            self.assertIsNotNone(self.check("GalaxyImages.galimg", bytes(data)))


if __name__ == "__main__":
    unittest.main()
