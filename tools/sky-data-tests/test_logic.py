"""Pure logic of sky_data_release.py: manifest, parts, plans, disk, receipt, ranges. No network."""

import copy
import hashlib
import json
import os
import shutil
import stat
import sys
import tempfile
import types
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

import sky_data_release as sdr  # noqa: E402

MAIN = "GaiaStarCatalog.starcat"
V13, V15, V17, V19 = (f"GaiaStarCatalog.V{c}.starcat" for c in (13, 15, 17, 19))
V21 = "GaiaStarCatalog.V21.starcat"
DUST, HALPHA, PATCHES, GALAXIES, IMAGES = (
    "DustMap.dustmap", "HalphaMap.emission", "HalphaPatches.patchset", "GalaxyCatalog.galcat",
    "GalaxyImages.galimg")
PRODUCTS = [DUST, HALPHA, PATCHES, GALAXIES, IMAGES]
TIERS = [V13, V15, V17, V19]

# The sizes in the release contract.
SIZES = {V13: 78_027_576, V15: 387_980_450, V17: 1_640_959_044, V19: 5_976_401_666,
         MAIN: 25_287_569_276, DUST: 25_165_949, HALPHA: 25_165_923, PATCHES: 14_605_549,
         GALAXIES: 962_496, IMAGES: 451_875_243}
PART = 1_992_294_400


def fake_sha(text):
    return hashlib.sha256(text.encode()).hexdigest()


def contract_manifest(without=()):
    files = []
    for name, size in SIZES.items():
        if name in without:
            continue
        count = -(-size // PART) if size > PART else 1
        parts = []
        for i in range(count):
            asset = name if count == 1 else f"{name}.{i + 1:03d}"
            parts.append({"asset": asset, "offset": i * PART, "size": min(PART, size - i * PART),
                          "sha256": fake_sha(asset)})
        star = name in TIERS or name == MAIN
        product, group = ("stars", "stars") if star else sdr.PRODUCT_BY_NAME[name]
        cut = int(name.split(".V")[1].split(".")[0]) if name in TIERS else None
        files.append({"name": name, "size": size, "sha256": fake_sha(name), "product": product,
                      "group": group, "cut_v": cut, "licence": "CC BY-NC 3.0 IGO",
                      "credit": "ESA, Gaia DPAC", "parts": parts})
    others = [n for n in PRODUCTS if n not in without]
    return {"schema": 1, "release": "sky-data-1", "repo": "batistitooo/ExoInstruments",
            "min_mod_version": "0.5.0", "part_size": PART, "files": files,
            "variants": {"default": [V13], "compact": TIERS + others,
                         "complete": TIERS + [MAIN] + others}}


MANIFEST = sdr.validate_manifest(contract_manifest(), "a" * 64)


def ok(name, recorded=True, link_to=None):
    """A file or link matching the release."""
    return sdr.Found(name, "link" if link_to else "file", SIZES[name], 1, link_to, True, True,
                     recorded and not link_to)


def foreign(name, size=103_182_006, recorded=False, link_to=None):
    return sdr.Found(name, "link" if link_to else "file", size, 1, link_to, True, False, recorded)


def inventory(*items):
    return {item.name: item for item in items}


def part(target, size, resumable=True):
    return [sdr.StagingItem(target + ".part", "part", size, target, resumable),
            sdr.StagingItem(target + ".part.json", "journal", 180, target, resumable)]


def downloads(plan):
    return [d.entry["name"] for d in plan.downloads]


def removals(plan):
    return [r.display for r in plan.removals]


class PartLayout(unittest.TestCase):
    def test_main_file_is_thirteen_parts(self):
        parts = sdr.split_parts(MAIN, SIZES[MAIN], sdr.PART_SIZE)
        self.assertEqual(sdr.PART_SIZE, PART)
        self.assertEqual(len(parts), 13)
        self.assertEqual([p[0] for p in parts], [f"{MAIN}.{i:03d}" for i in range(1, 14)])
        self.assertEqual([p[1] for p in parts], [i * PART for i in range(13)])
        self.assertEqual([p[2] for p in parts[:12]], [PART] * 12)
        self.assertEqual(parts[-1][2], 1_380_036_476)
        self.assertEqual(sum(p[2] for p in parts), SIZES[MAIN])

    def test_v19_is_three_parts(self):
        parts = sdr.split_parts(V19, SIZES[V19], sdr.PART_SIZE)
        self.assertEqual([p[0] for p in parts], [f"{V19}.001", f"{V19}.002", f"{V19}.003"])
        self.assertEqual([p[1] for p in parts], [0, PART, 2 * PART])
        self.assertEqual([p[2] for p in parts], [PART, PART, 1_991_812_866])

    def test_smaller_files_are_one_asset_named_like_the_file(self):
        for name in (V13, V15, V17, DUST, IMAGES):
            self.assertEqual(sdr.split_parts(name, SIZES[name], PART), [(name, 0, SIZES[name])])
        self.assertEqual(sdr.split_parts(V13, PART, PART), [(V13, 0, PART)])
        self.assertEqual(sdr.split_parts(V13, PART + 1, PART), [(V13 + ".001", 0, PART), (V13 + ".002", PART, 1)])


class ManifestValidation(unittest.TestCase):
    def test_contract_manifest_is_valid(self):
        self.assertEqual(MANIFEST.variants["default"], [V13])
        self.assertEqual(set(MANIFEST.variants["compact"]), set(TIERS + PRODUCTS))
        self.assertEqual(set(MANIFEST.variants["complete"]), set(TIERS + PRODUCTS + [MAIN]))
        self.assertEqual(len(MANIFEST.by_name[MAIN]["parts"]), 13)

    def test_optional_products_may_be_absent(self):
        manifest = sdr.validate_manifest(contract_manifest(without=(PATCHES, IMAGES)), "b" * 64)
        self.assertNotIn(IMAGES, manifest.variants["compact"])

    def assert_rejected(self, mutate):
        data = contract_manifest()
        mutate(data)
        with self.assertRaises(sdr.SkyDataError):
            sdr.validate_manifest(data, "c" * 64)

    def entry(self, data, name):
        return next(f for f in data["files"] if f["name"] == name)

    def test_rejections(self):
        cases = {
            "schema": lambda d: d.update(schema=2),
            "release": lambda d: d.update(release="sky-data-2"),
            "part size flag": lambda d: d.update(part_size=True),
            "min version": lambda d: d.update(min_mod_version="soon"),
            "offset": lambda d: self.entry(d, MAIN)["parts"][1].update(offset=PART + 1),
            "asset name": lambda d: self.entry(d, MAIN)["parts"][0].update(asset=MAIN + ".1"),
            "last part size": lambda d: self.entry(d, MAIN)["parts"][-1].update(size=1),
            "missing part": lambda d: self.entry(d, V19)["parts"].pop(),
            "split small file": lambda d: self.entry(d, V17)["parts"][0].update(asset=V17 + ".001"),
            "sha case": lambda d: self.entry(d, V13).update(sha256=fake_sha(V13).upper()),
            "part sha": lambda d: self.entry(d, V13)["parts"][0].update(sha256="xyz"),
            "path name": lambda d: self.entry(d, V13).update(name="../" + V13),
            "unknown name": lambda d: self.entry(d, DUST).update(name="Other.dustmap"),
            "duplicate": lambda d: d["files"].append(copy.deepcopy(self.entry(d, V13))),
            "cut mismatch": lambda d: self.entry(d, V13).update(cut_v=14),
            "main cut": lambda d: self.entry(d, MAIN).update(cut_v=25),
            "product": lambda d: self.entry(d, DUST).update(group="halpha"),
            "default": lambda d: d["variants"].update(default=[V15]),
            "compact with main": lambda d: d["variants"]["compact"].append(MAIN),
            "compact missing": lambda d: d["variants"]["compact"].remove(DUST),
            "complete missing main": lambda d: d["variants"]["complete"].remove(MAIN),
            "no licence": lambda d: self.entry(d, V13).pop("licence"),
        }
        for label, mutate in cases.items():
            with self.subTest(label):
                self.assert_rejected(mutate)


class Plans(unittest.TestCase):
    def plan(self, variant, found=(), staging=(), manifest=MANIFEST):
        return sdr.make_plan(manifest, variant, inventory(*found), staging)

    def test_empty_default(self):
        plan = self.plan("default")
        self.assertEqual(downloads(plan), [V13])
        self.assertEqual(plan.removals, [])

    def test_empty_compact(self):
        plan = self.plan("compact")
        self.assertEqual(downloads(plan), TIERS + PRODUCTS)
        self.assertEqual(plan.removals, [])
        self.assertEqual(plan.download_bytes(), sum(SIZES[n] for n in TIERS + PRODUCTS))

    def test_empty_complete_installs_main_after_tiers(self):
        self.assertEqual(downloads(self.plan("complete")), TIERS + [MAIN] + PRODUCTS)

    def test_default_to_compact(self):
        plan = self.plan("compact", [ok(V13)])
        self.assertEqual([k.name for k in plan.keep], [V13])
        self.assertEqual(downloads(plan), [V15, V17, V19] + PRODUCTS)
        self.assertEqual(plan.removals, [])

    def test_compact_to_complete_downloads_only_main(self):
        plan = self.plan("complete", [ok(n) for n in TIERS + PRODUCTS])
        self.assertEqual(downloads(plan), [MAIN])
        self.assertEqual(plan.download_bytes(), SIZES[MAIN])
        self.assertEqual(plan.removals, [])

    def test_complete_to_compact_removes_main_only(self):
        plan = self.plan("compact", [ok(n) for n in TIERS + PRODUCTS + [MAIN]])
        self.assertEqual(downloads(plan), [])
        self.assertEqual(removals(plan), [MAIN])
        self.assertFalse(plan.removals[0].ask)
        self.assertEqual(plan.freed_bytes(), SIZES[MAIN])
        self.assertEqual(plan.questions(), [])

    def test_partial_compact_resumes(self):
        plan = self.plan("compact", [ok(V13), ok(V15)], part(V17, 1_000_000_000))
        self.assertEqual(downloads(plan), [V17, V19] + PRODUCTS)
        self.assertEqual(plan.downloads[0].resumable, 1_000_000_000)
        self.assertEqual(plan.removals, [])

    def test_partial_complete_staging(self):
        found = [ok(n) for n in TIERS + PRODUCTS]
        staging = part(MAIN, 5_000_000_000)
        compact = self.plan("compact", found, staging)
        self.assertEqual(removals(compact), [f".sky-data/{MAIN}.part", f".sky-data/{MAIN}.part.json"])
        self.assertFalse(any(r.ask for r in compact.removals))
        complete = self.plan("complete", found, staging)
        self.assertEqual(downloads(complete), [MAIN])
        self.assertEqual(complete.downloads[0].resumable, 5_000_000_000)
        self.assertEqual(complete.removals, [])

    def test_other_release_staging_is_removed(self):
        staging = part(V19, 3_000_000, resumable=False) + [sdr.StagingItem("junk.tmp", "other", 10)]
        for variant in ("compact", "complete"):
            plan = self.plan(variant, [], staging)
            self.assertEqual(removals(plan), [f".sky-data/{V19}.part", f".sky-data/{V19}.part.json",
                                              ".sky-data/junk.tmp"])
            self.assertEqual(plan.downloads[3].resumable, 0)
        self.assertEqual(self.plan("default", [], staging).removals, [])

    def test_staging_path_that_is_not_a_folder_is_removed(self):
        plan = self.plan("default", [], [sdr.StagingItem(None, "link")])
        self.assertEqual(removals(plan), [".sky-data"])

    def test_g13_main(self):
        g13 = foreign(MAIN)
        default = self.plan("default", [g13])
        self.assertEqual(downloads(default), [])
        self.assertEqual([k.name for k in default.keep], [MAIN])
        compact = self.plan("compact", [g13])
        self.assertEqual(removals(compact), [MAIN])
        self.assertTrue(compact.removals[0].ask)
        self.assertEqual(downloads(compact), TIERS + PRODUCTS)
        complete = self.plan("complete", [g13])
        self.assertEqual(removals(complete), [MAIN])
        self.assertEqual(downloads(complete), TIERS + [MAIN] + PRODUCTS)
        self.assertEqual(len(complete.questions()), 1)

    def test_verified_link(self):
        link = ok(MAIN, link_to="/Studio/GaiaAllSky.starcat")
        complete = self.plan("complete", [link] + [ok(n) for n in TIERS])
        self.assertIn(link, complete.keep)
        self.assertEqual(downloads(complete), PRODUCTS)
        compact = self.plan("compact", [link])
        self.assertEqual(removals(compact), [MAIN])
        removal = compact.removals[0]
        self.assertTrue(removal.ask)
        self.assertEqual((removal.size, removal.link_target), (0, "/Studio/GaiaAllSky.starcat"))
        self.assertEqual(compact.freed_bytes(), 0)
        self.assertIn("/Studio/GaiaAllSky.starcat", compact.questions()[0])

    def test_unverified_link(self):
        link = foreign(MAIN, size=5, link_to="/elsewhere/other.starcat")
        plan = self.plan("complete", [link])
        self.assertEqual(removals(plan), [MAIN])
        self.assertTrue(plan.removals[0].ask)
        self.assertIn(MAIN, downloads(plan))

    def test_default_replaces_unverified_v13_link(self):
        plan = self.plan("default", [foreign(V13, size=5, link_to="/x")])
        self.assertEqual((removals(plan), downloads(plan)), ([V13], [V13]))

    def test_extra_v21_tier(self):
        extra = foreign(V21, size=20_000_000_000, recorded=False)
        compact = self.plan("compact", [extra])
        self.assertEqual(removals(compact), [V21])
        self.assertTrue(compact.removals[0].ask)
        complete = self.plan("complete", [extra])
        self.assertEqual(complete.removals, [])
        self.assertTrue(any(V21 in note for note in complete.notes))

    def test_replacement_asks_only_when_not_recorded(self):
        recorded = self.plan("compact", [foreign(V13, recorded=True)])
        self.assertEqual(downloads(recorded)[0], V13)
        self.assertFalse(recorded.downloads[0].ask)
        self.assertEqual(recorded.questions(), [])
        own = self.plan("compact", [foreign(V13)])
        self.assertTrue(own.downloads[0].ask)
        self.assertEqual(len(own.questions()), 1)

    def test_group_with_own_build_is_left_alone(self):
        manifest = sdr.validate_manifest(contract_manifest(without=(PATCHES,)), "d" * 64)
        plan = self.plan("compact", [foreign(HALPHA), foreign(PATCHES)], manifest=manifest)
        self.assertNotIn(HALPHA, downloads(plan))
        self.assertEqual(plan.removals, [])
        self.assertTrue(any(HALPHA in n and PATCHES in n for n in plan.notes))

    def test_product_missing_from_release_leaves_the_plan_alone(self):
        # The player scripts build it after the downloads, so the plan says nothing about it.
        manifest = sdr.validate_manifest(contract_manifest(without=(IMAGES,)), "e" * 64)
        plan = self.plan("compact", manifest=manifest)
        self.assertEqual(plan.notes, [])
        self.assertEqual(downloads(plan), TIERS + [DUST, HALPHA, PATCHES, GALAXIES])

    def test_default_keeps_any_verified_tier(self):
        plan = self.plan("default", [ok(V15)])
        self.assertEqual(downloads(plan), [])
        self.assertEqual([k.name for k in plan.keep], [V15])

    def test_folder_at_an_owned_name_blocks(self):
        self.assertEqual(self.plan("compact", [sdr.Found(V13, "other")]).blocked, [V13])
        self.assertEqual(self.plan("compact", [sdr.Found(V21, "other")]).blocked, [V21])

    def test_old_mod_version(self):
        self.assertTrue(sdr.version_older("0.4.1", "0.5.0"))
        self.assertFalse(sdr.version_older("0.5.0", "0.5.0"))
        self.assertFalse(sdr.version_older("0.5", "0.5.0"))
        self.assertFalse(sdr.version_older("0.10.0", "0.5.0"))
        self.assertTrue(sdr.version_older("0.4.9.9", "0.5"))


class LocalBuilds(unittest.TestCase):
    def test_products_the_release_lacks_in_setup_data_order(self):
        self.assertEqual(sdr.local_products(MANIFEST), [])
        manifest = sdr.validate_manifest(contract_manifest(without=(HALPHA, PATCHES)), "e" * 64)
        self.assertEqual(sdr.local_products(manifest), ["halpha", "patches"])
        # setup_data.py builds galaxies before patches, the reverse of the release's order.
        manifest = sdr.validate_manifest(contract_manifest(without=(PATCHES, GALAXIES)), "f" * 64)
        self.assertEqual(sdr.local_products(manifest), ["galaxies", "patches"])

    def test_names(self):
        self.assertEqual(sdr.join_names(["halpha"]), "halpha")
        self.assertEqual(sdr.join_names(["halpha", "patches"]), "halpha and patches")
        self.assertEqual(sdr.join_names(["dust", "halpha", "patches"]), "dust, halpha and patches")

    def test_line_says_why_and_what_is_downloaded(self):
        why = "survey terms do not allow redistribution"
        self.assertEqual(sdr.local_line(["halpha", "patches"]),
                         f"halpha and patches on this computer, as their {why}: "
                         "about 600 MB to download the first time, and up to an hour to run")
        self.assertEqual(sdr.local_line(["patches"]),
                         f"patches on this computer, as its {why}: "
                         "about 550 MB to download the first time, and up to an hour to run")
        self.assertIn(f"as its {why}: about 50 MB to download", sdr.local_line(["halpha"]))
        # A product with no measured download says only that it takes a while.
        self.assertTrue(sdr.local_line(["galaxies"]).endswith(f"as its {why}: a while to run"))

    def test_only_products_missing_from_plugin_data(self):
        root = tempfile.mkdtemp(prefix="sky-data-local-")
        self.addCleanup(shutil.rmtree, root, True)
        Path(root, HALPHA).write_bytes(b"EXOEMIS1")
        os.mkdir(os.path.join(root, PATCHES))
        self.assertEqual(sdr.not_installed(["halpha", "patches"], root), ["patches"])
        self.assertEqual(sdr.not_installed([], root), [])


class Disk(unittest.TestCase):
    def test_margin_resumable_and_freed(self):
        plan = sdr.make_plan(MANIFEST, "complete", inventory(*[ok(n) for n in TIERS + PRODUCTS]),
                             part(MAIN, 5_000_000_000))
        need, freed = sdr.disk_need(plan)
        todo = SIZES[MAIN] - 5_000_000_000
        self.assertEqual(need, todo + (256 << 20))
        self.assertEqual(freed, 0)

        plan = sdr.make_plan(MANIFEST, "complete", inventory(foreign(MAIN)))
        need, freed = sdr.disk_need(plan)
        total = sum(SIZES.values())
        self.assertEqual(need, total + total // 100)
        self.assertEqual(freed, 103_182_006)
        self.assertTrue(sdr.has_room(need, need - freed, freed))
        self.assertFalse(sdr.has_room(need, need - freed - 1, freed))

    def test_nothing_to_download_needs_nothing(self):
        plan = sdr.make_plan(MANIFEST, "compact", inventory(*[ok(n) for n in TIERS + PRODUCTS + [MAIN]]))
        self.assertEqual(sdr.disk_need(plan), (0, SIZES[MAIN]))


class Receipt(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="sky-data-receipt-")
        self.addCleanup(shutil.rmtree, self.root, True)

    def test_round_trip(self):
        receipt = {"release": "sky-data-1", "files": {
            V13: sdr.receipt_record(78_027_576, 1_700_000_000_123_456_789, fake_sha(V13), None),
            MAIN: sdr.receipt_record(SIZES[MAIN], 5, fake_sha(MAIN), "/Studio/GaiaAllSky.starcat")}}
        sdr.save_receipt(self.root, receipt)
        self.assertEqual(sdr.load_receipt(self.root), receipt)
        self.assertEqual(sorted(os.listdir(self.root)), [sdr.RECEIPT_NAME])

    def test_bad_receipts(self):
        self.assertEqual(sdr.load_receipt(self.root), {"release": None, "files": {}})
        path = os.path.join(self.root, sdr.RECEIPT_NAME)
        with open(path, "w") as handle:
            handle.write("{not json")
        self.assertEqual(sdr.load_receipt(self.root)["files"], {})
        with open(path, "w") as handle:
            json.dump({"release": 3, "files": {V13: {"size": "big"}, V15: {
                "size": 1, "mtime_ns": 2, "sha256": "x", "linked_to": None}}}, handle)
        self.assertEqual(sdr.load_receipt(self.root), {"release": None, "files": {
            V15: sdr.receipt_record(1, 2, "x", None)}})

    def test_stars_installed(self):
        self.assertIsNone(sdr.stars_installed(self.root))
        path = os.path.join(self.root, V15)
        with open(path, "wb") as handle:
            handle.write(b"tier")
        st = os.stat(path)
        receipt = {"release": "sky-data-1", "files": {V15: sdr.receipt_record(4, st.st_mtime_ns + 1, "s", None)}}
        sdr.save_receipt(self.root, receipt)
        self.assertIsNone(sdr.stars_installed(self.root))
        receipt["files"][V15]["mtime_ns"] = st.st_mtime_ns
        sdr.save_receipt(self.root, receipt)
        self.assertEqual(sdr.stars_installed(self.root), V15)
        with open(os.path.join(self.root, MAIN), "wb") as handle:
            handle.write(b"main")
        self.assertEqual(sdr.stars_installed(self.root), MAIN)

    def test_mod_version_file(self):
        path = os.path.join(self.root, "ExoInstruments.version")
        with open(path, "w") as handle:
            json.dump({"VERSION": {"MAJOR": 0, "MINOR": 5, "PATCH": 0, "BUILD": 0}}, handle)
        self.assertEqual(sdr.read_mod_version(path), "0.5.0.0")
        with open(path, "w") as handle:
            json.dump({"VERSION": "0.4.1"}, handle)
        self.assertEqual(sdr.read_mod_version(path), "0.4.1")
        os.unlink(path)
        with self.assertRaises(sdr.SkyDataError):
            sdr.read_mod_version(path)


class Ranges(unittest.TestCase):
    def test_parse_content_range(self):
        self.assertEqual(sdr.parse_content_range("bytes 10-25/33690884"), (10, 25, 33690884))
        for bad in (None, "", "bytes */100", "bytes 5-4/10", "bytes 0-10/10", "items 0-1/2"):
            self.assertIsNone(sdr.parse_content_range(bad), bad)

    def test_verdicts(self):
        self.assertEqual(sdr.range_verdict(206, "bytes 100-999/1000", 900, 100, 1000), "append")
        self.assertEqual(sdr.range_verdict(206, "bytes 100-999/1000", None, 100, 1000), "append")
        self.assertIsNone(sdr.range_verdict(206, "bytes 0-999/1000", 1000, 100, 1000))
        self.assertIsNone(sdr.range_verdict(206, "bytes 100-999/2000", 900, 100, 1000))
        self.assertIsNone(sdr.range_verdict(206, "bytes 100-499/1000", 400, 100, 1000))
        self.assertIsNone(sdr.range_verdict(206, "bytes 100-999/1000", 5, 100, 1000))
        self.assertEqual(sdr.range_verdict(200, None, 1000, 100, 1000), "restart")
        self.assertEqual(sdr.range_verdict(200, None, None, 0, 1000), "restart")
        self.assertIsNone(sdr.range_verdict(200, None, 999, 100, 1000))
        self.assertIsNone(sdr.range_verdict(416, "bytes */1000", 0, 1000, 1000))


class Inventory(unittest.TestCase):
    def test_only_names_that_change_the_plan_are_hashed(self):
        main = ok(MAIN)
        self.assertEqual(sdr.names_to_check(MANIFEST, "default", main), set())
        self.assertEqual(sdr.names_to_check(MANIFEST, "default", None), {V13})
        broken = sdr.Found(MAIN, "link", link_target="/gone", target_ok=False)
        self.assertEqual(sdr.names_to_check(MANIFEST, "default", broken), {V13})
        self.assertEqual(sdr.names_to_check(MANIFEST, "default", sdr.Found(MAIN, "other")), {V13})
        self.assertEqual(sdr.names_to_check(MANIFEST, "compact", main), set(TIERS + PRODUCTS))
        self.assertEqual(sdr.names_to_check(MANIFEST, "complete", None), set(TIERS + PRODUCTS + [MAIN]))

    def test_junction_counts_as_a_link(self):
        ns = types.SimpleNamespace
        self.assertTrue(sdr.is_link(ns(st_mode=stat.S_IFLNK | 0o777)))
        self.assertTrue(sdr.is_link(ns(st_mode=stat.S_IFDIR | 0o755, st_reparse_tag=0xA0000003)))
        self.assertFalse(sdr.is_link(ns(st_mode=stat.S_IFDIR | 0o755, st_reparse_tag=0)))
        # A OneDrive placeholder is a reparse point too, but a real file.
        self.assertFalse(sdr.is_link(ns(st_mode=stat.S_IFREG | 0o644, st_reparse_tag=0x9000001A)))
        self.assertFalse(sdr.is_link(os.lstat(__file__)))


if __name__ == "__main__":
    unittest.main()
