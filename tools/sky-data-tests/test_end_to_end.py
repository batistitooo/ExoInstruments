"""Both player scripts against a local fault injecting release server and a fake KSP tree."""

import contextlib
import io
import json
import os
import runpy
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import types
import unittest
from pathlib import Path
from unittest import mock

import player_fixture as fx
import sky_data_release as sdr

TOOLS = fx.TOOLS
SCRIPTS = {"compact": TOOLS / "get_sky_data_compact.py",
           "complete": TOOLS / "get_sky_data_complete.py"}
V13, V15, V17, V19 = (f"GaiaStarCatalog.V{c}.starcat" for c in fx.CUTS)
MAIN = fx.MAIN
NO_PROXY = {"no_proxy": "127.0.0.1,localhost", "NO_PROXY": "127.0.0.1,localhost"}
SYMLINKS = fx.can_symlink()


class SyntheticRelease(unittest.TestCase):
    def test_release_is_valid(self):
        release = fx.build_release()
        manifest = sdr.validate_manifest(json.loads(release.manifest_bytes), release.manifest_sha256)
        self.assertGreaterEqual(len(manifest.by_name[MAIN]["parts"]), 4)
        self.assertGreaterEqual(len(manifest.by_name[V19]["parts"]), 3)
        self.assertEqual(len(manifest.by_name[V13]["parts"]), 1)
        from pack_gaia_catalog import header_band_fault
        with tempfile.TemporaryDirectory() as folder:
            for name in (V13, V15, V17, V19, MAIN):
                data = release.files[name]
                count = int.from_bytes(data[12:16], "little")
                self.assertEqual(len(data), 24 + 4 * 1801 + 14 * count, name)
                path = os.path.join(folder, name)
                with open(path, "wb") as handle:
                    handle.write(data)
                self.assertIsNone(header_band_fault(path), name)


class EndToEnd(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.release = fx.build_release()

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="sky-data-e2e-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.ksp, self.plugin = fx.make_ksp(self.tmp / "KSP")
        self.server = fx.ReleaseServer(self.release.assets)
        self.addCleanup(self.server.close)
        # This release carries every product, so nothing may be built locally (test_local_builds.py).
        patches = [mock.patch.dict(os.environ, NO_PROXY),
                   mock.patch.object(sdr, "run_local_builds",
                                     side_effect=AssertionError("no local build expected"))]
        patches += [mock.patch.object(sdr, name, value) for name, value in
                    (("RETRY_DELAY", 0.01), ("RETRY_MAX_DELAY", 0.05), ("SOCKET_TIMEOUT", 5.0))]
        for patcher in patches:
            patcher.start()
            self.addCleanup(patcher.stop)
        self.output = ""

    # Running

    def run_script(self, variant, *extra, yes=True, sha256=None):
        script = str(SCRIPTS[variant])
        argv = [script, "--ksp", str(self.ksp), "--base-url", self.server.base_url,
                "--manifest-sha256", sha256 or self.release.manifest_sha256]
        argv += ["--yes"] if yes else []
        argv += list(extra)
        out = io.StringIO()
        with mock.patch.object(sys, "argv", argv), mock.patch.object(sys, "stdin", io.StringIO("")), \
                contextlib.redirect_stdout(out):
            try:
                runpy.run_path(script, run_name="__main__")
                code = 0
            except SystemExit as stop:
                code = stop.code or 0
        self.output = out.getvalue()
        return code

    def run_default(self):
        out = io.StringIO()
        with mock.patch.object(sys, "stdin", io.StringIO("")), contextlib.redirect_stdout(out):
            code = sdr.install_variant("default", self.plugin, assume_yes=True,
                                       base_url=self.server.base_url,
                                       manifest_sha256=self.release.manifest_sha256)
        self.output = out.getvalue()
        return code

    # Checks

    def assert_installed(self, names, folder=None):
        folder = Path(folder or self.plugin)
        for name in names:
            path = folder / name
            self.assertTrue(path.is_file() and not path.is_symlink(), name)
            self.assertEqual(fx.sha(path.read_bytes()), self.release.sha(name), name)

    def assert_no_staging(self):
        self.assertFalse(os.path.lexists(self.plugin / sdr.STAGING_NAME))

    def receipt(self):
        return json.loads((self.plugin / sdr.RECEIPT_NAME).read_text())

    def star_files(self):
        return sorted(n for n in os.listdir(self.plugin) if sdr.is_star_name(n))

    # Fresh installs and transitions

    def test_fresh_compact(self):
        self.assertEqual(self.run_script("compact"), 0, self.output)
        compact = self.release.variant("compact")
        self.assert_installed(compact)
        self.assertFalse(os.path.lexists(self.plugin / MAIN))
        self.assert_no_staging()
        receipt = self.receipt()
        self.assertEqual(sorted(receipt["files"]), sorted(compact))
        for name in compact:
            self.assertEqual(receipt["files"][name]["sha256"], self.release.sha(name))
        self.assertEqual(sorted(self.server.github_assets()), sorted(self.release.assets_of(compact)))
        self.assertIn("Stars: to V 19", self.output)

    def test_fresh_complete(self):
        self.assertEqual(self.run_script("complete"), 0, self.output)
        complete = self.release.variant("complete")
        self.assert_installed(complete)
        self.assert_no_staging()
        requested = self.server.github_assets()
        self.assertEqual(sorted(requested), sorted(self.release.assets_of(complete)))
        # Shallowest tier first, the main file after every tier.
        order = [requested.index(self.release.parts(n)[0]["asset"]) for n in (V13, V15, V17, V19, MAIN)]
        self.assertEqual(order, sorted(order))

    def test_compact_then_complete_fetches_only_main(self):
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.server.clear_log()
        self.assertEqual(self.run_script("complete"), 0, self.output)
        self.assertEqual(self.server.github_assets(), self.release.assets_of([MAIN]))
        self.assert_installed(self.release.variant("complete"))

    def test_complete_then_compact_downloads_nothing(self):
        self.assertEqual(self.run_script("complete"), 0, self.output)
        self.server.clear_log()
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertEqual(self.server.github_assets(), [])
        self.assertFalse(os.path.lexists(self.plugin / MAIN))
        self.assert_installed(self.release.variant("compact"))
        self.assertNotIn(MAIN, self.receipt()["files"])

    def test_default_then_compact(self):
        self.assertEqual(self.run_default(), 0, self.output)
        self.assertEqual(self.star_files(), [V13])
        self.assert_installed([V13])
        self.server.clear_log()
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertNotIn(V13, self.server.github_assets())
        self.assert_installed(self.release.variant("compact"))
        self.server.clear_log()
        self.assertEqual(self.run_default(), 0, self.output)
        self.assertEqual(self.server.github_assets(), [])

    def test_foreign_main_file(self):
        foreign = os.urandom(50_000)
        (self.plugin / MAIN).write_bytes(foreign)
        (self.plugin / "GaiaStarCatalog-G13.starcat").write_bytes(b"old build")
        self.assertEqual(self.run_default(), 0, self.output)
        self.assertEqual(self.server.github_assets(), [])

        self.assertEqual(self.run_script("compact", yes=False), 2, self.output)
        self.assertIn("--yes", self.output)
        self.assertEqual((self.plugin / MAIN).read_bytes(), foreign)
        self.assertEqual(sorted(os.listdir(self.plugin)), ["GaiaStarCatalog-G13.starcat", MAIN])
        self.assertEqual(self.server.github_assets(), [])

        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertFalse(os.path.lexists(self.plugin / MAIN))
        self.assert_installed(self.release.variant("compact"))
        self.assertEqual((self.plugin / "GaiaStarCatalog-G13.starcat").read_bytes(), b"old build")
        self.assertIn("GaiaStarCatalog-G13.starcat", self.output)

    def test_foreign_main_replaced_by_complete(self):
        (self.plugin / MAIN).write_bytes(os.urandom(len(self.release.files[MAIN])))
        self.assertEqual(self.run_script("complete"), 0, self.output)
        self.assert_installed(self.release.variant("complete"))

    @unittest.skipUnless(SYMLINKS, "symbolic links are not available")
    def test_verified_link_kept(self):
        target = self.tmp / "Studio" / "GaiaAllSky.starcat"
        target.parent.mkdir()
        target.write_bytes(self.release.files[MAIN])
        os.symlink(target, self.plugin / MAIN)
        self.assertEqual(self.run_script("complete"), 0, self.output)
        self.assertTrue((self.plugin / MAIN).is_symlink())
        self.assertEqual(os.readlink(self.plugin / MAIN), str(target))
        self.assertTrue(set(self.release.assets_of([MAIN])).isdisjoint(self.server.github_assets()))
        self.assert_installed([V13, V15, V17, V19])
        self.assertEqual(self.receipt()["files"][MAIN]["linked_to"], str(target))

        self.server.clear_log()
        self.assertEqual(self.run_script("complete"), 0, self.output)
        self.assertEqual(self.server.github_assets(), [])
        self.assertIn("Nothing to do", self.output)

    @unittest.skipUnless(SYMLINKS, "symbolic links are not available")
    def test_unverified_link_replaced_target_untouched(self):
        target = self.tmp / "Studio" / "Reindexed.starcat"
        target.parent.mkdir()
        other = os.urandom(len(self.release.files[MAIN]))
        target.write_bytes(other)
        os.symlink(target, self.plugin / MAIN)
        self.assertEqual(self.run_script("complete"), 0, self.output)
        self.assertFalse((self.plugin / MAIN).is_symlink())
        self.assert_installed(self.release.variant("complete"))
        self.assertEqual(target.read_bytes(), other)

    @unittest.skipUnless(SYMLINKS, "symbolic links are not available")
    def test_links_in_staging_and_linked_plugin_data(self):
        real = self.tmp / "OtherDrive" / "PluginData"
        real.mkdir(parents=True)
        self.plugin.rmdir()
        os.symlink(real, self.plugin, target_is_directory=True)
        victim = self.tmp / "victim.bin"
        victim.write_bytes(b"do not touch")
        (real / sdr.STAGING_NAME).mkdir()
        os.symlink(victim, real / sdr.STAGING_NAME / (V13 + ".part"))
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertEqual(victim.read_bytes(), b"do not touch")
        self.assert_installed(self.release.variant("compact"), folder=real)

        outside = self.tmp / "outside"
        outside.mkdir()
        (outside / (MAIN + ".part")).write_bytes(b"keep me")
        os.symlink(outside, real / sdr.STAGING_NAME, target_is_directory=True)
        self.assertEqual(self.run_script("complete"), 0, self.output)
        self.assertEqual((outside / (MAIN + ".part")).read_bytes(), b"keep me")
        self.assertEqual(sorted(os.listdir(outside)), [MAIN + ".part"])
        self.assert_installed([MAIN], folder=real)

    # Interruptions and faults

    def test_resume_after_killed_download(self):
        # The shipped copy, run with no --ksp, has to find KSP from its own location.
        shipped = self.ksp / "GameData" / "ExoInstruments" / "tools"
        shipped.mkdir()
        for name in ("sky_data_release.py", "get_sky_data_compact.py", "setup_data.py"):
            shutil.copy2(TOOLS / name, shipped / name)
        parts = self.release.parts(V17)
        self.assertGreaterEqual(len(parts), 2)
        stalled = parts[1]["asset"]
        self.server.add_fault(stalled, "stall", at=1000, seconds=120)
        env = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")
        env.pop("KSP", None)
        process = subprocess.Popen(
            [sys.executable, str(shipped / "get_sky_data_compact.py"), "--yes",
             "--base-url", self.server.base_url, "--manifest-sha256", self.release.manifest_sha256],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, env=env,
            cwd=str(self.tmp))
        partial = self.plugin / sdr.STAGING_NAME / (V17 + ".part")
        expected = parts[1]["offset"] + 1000
        deadline = time.monotonic() + 60
        try:
            while time.monotonic() < deadline and process.poll() is None:
                if partial.exists() and partial.stat().st_size >= expected:
                    break
                time.sleep(0.05)
        finally:
            process.kill()
            output = process.communicate()[0].decode("utf-8", "replace")
        self.assertEqual(partial.stat().st_size, expected, output)
        self.assertIn(f"Found KSP at {os.path.realpath(self.ksp)}", output)
        self.assert_installed([V13, V15])

        self.server.clear_log()
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertIn("resuming", self.output)
        requested = self.server.github_assets()
        for asset in self.release.assets_of([V13, V15]) + [parts[0]["asset"]]:
            self.assertNotIn(asset, requested)
        self.assertEqual(self.server.cdn_ranges(stalled), ["bytes=1000-"])
        self.assert_installed(self.release.variant("compact"))
        self.assert_no_staging()

    def test_corrupted_part_refetched(self):
        bad = self.release.parts(V19)[1]["asset"]
        self.server.add_fault(bad, "flip", at=4321)
        self.assertEqual(self.run_script("compact"), 0, self.output)
        requested = self.server.github_assets()
        self.assertEqual(requested.count(bad), 2)
        for asset in self.release.assets_of(self.release.variant("compact")):
            if asset != bad:
                self.assertEqual(requested.count(asset), 1, asset)
        self.assert_installed(self.release.variant("compact"))

    def test_expired_redirect(self):
        self.server.add_fault(V13, "expire")
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertEqual(self.server.github_assets().count(V13), 2)
        self.assertEqual([r["status"] for r in self.server.cdn_requests(V13)], [403, 206])
        self.assert_installed(self.release.variant("compact"))

    def test_dropped_ignored_range_and_stalled_connections(self):
        first = self.release.parts(V15)[0]["asset"]
        self.server.add_fault(first, "drop", at=5000)
        self.server.add_fault(first, "ignore_range", skip=1)
        self.server.add_fault("DustMap.dustmap", "stall", at=100, seconds=10)
        with mock.patch.object(sdr, "SOCKET_TIMEOUT", 0.5):
            self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertEqual(self.server.cdn_ranges(first), ["bytes=0-", "bytes=5000-"])
        self.assertEqual([r["status"] for r in self.server.cdn_requests(first)], [206, 200])
        self.assertEqual(self.server.cdn_ranges("DustMap.dustmap"), ["bytes=0-", "bytes=100-"])
        self.assert_installed(self.release.variant("compact"))

    def test_part_changed_after_verification_is_the_only_one_refetched(self):
        parts = self.release.parts(V19)
        self.assertGreaterEqual(len(parts), 3)
        data = bytearray(self.release.files[V19][:parts[-1]["offset"]])
        data[10] ^= 0xFF
        staging = self.plugin / sdr.STAGING_NAME
        staging.mkdir()
        (staging / (V19 + ".part")).write_bytes(bytes(data))
        entry = self.release.entries[V19]
        (staging / (V19 + ".part.json")).write_text(json.dumps({
            "release": sdr.RELEASE, "manifest_sha256": self.release.manifest_sha256, "name": V19,
            "size": entry["size"], "sha256": entry["sha256"], "verified_parts": len(parts) - 1}))
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertIn("checking each part again", self.output)
        fetched = [a for a in self.server.github_assets() if a.startswith(V19)]
        self.assertEqual(fetched, [parts[-1]["asset"], parts[0]["asset"]])
        self.assert_installed(self.release.variant("compact"))
        self.assert_no_staging()

    def test_manifest_disagreeing_with_its_parts_fails_cleanly(self):
        manifest = json.loads(self.release.manifest_bytes)
        next(f for f in manifest["files"] if f["name"] == V15)["sha256"] = "0" * 64
        body = json.dumps(manifest).encode("utf-8")
        self.server.assets[sdr.MANIFEST_ASSET] = body
        self.assertEqual(self.run_script("compact", sha256=fx.sha(body)), 1, self.output)
        self.assertIn(f"{V15} does not match the release after downloading", self.output)
        self.assert_installed([V13])
        self.assertFalse(os.path.lexists(self.plugin / V15))
        self.assertEqual(os.listdir(self.plugin / sdr.STAGING_NAME), [])
        requested = self.server.github_assets()
        for asset in self.release.assets_of([V15]):
            self.assertEqual(requested.count(asset), 1, asset)

    def test_ctrl_c_stops_with_a_message(self):
        with mock.patch.object(sdr.Transfer, "run", side_effect=KeyboardInterrupt):
            self.assertEqual(self.run_script("compact"), 1, self.output)
        self.assertIn("Stopped. Rerun to resume.", self.output)

    # Reading only what matters

    @contextlib.contextmanager
    def spy_hashing(self):
        hashed = []
        real = sdr.file_sha256

        def spy(path, name, size, log):
            hashed.append(name)
            return real(path, name, size, log)
        with mock.patch.object(sdr, "file_sha256", spy):
            yield hashed

    @unittest.skipUnless(SYMLINKS, "symbolic links are not available")
    def test_compact_does_not_read_a_main_it_removes(self):
        target = self.tmp / "Studio" / "GaiaAllSky.starcat"
        target.parent.mkdir()
        target.write_bytes(self.release.files[MAIN])
        os.symlink(target, self.plugin / MAIN)
        with self.spy_hashing() as hashed:
            self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertEqual(hashed, [])
        self.assertFalse(os.path.lexists(self.plugin / MAIN))
        self.assertEqual(target.read_bytes(), self.release.files[MAIN])
        self.assert_installed(self.release.variant("compact"))

    def test_default_reads_only_what_it_needs(self):
        for name in (V13, V15, V17, "DustMap.dustmap"):
            (self.plugin / name).write_bytes(self.release.files[name])
        with self.spy_hashing() as hashed:
            self.assertEqual(self.run_default(), 0, self.output)
        self.assertEqual(hashed, [V13])
        self.assertEqual(self.server.github_assets(), [])

        (self.plugin / MAIN).write_bytes(b"EXOSTAR1 own build")
        (self.plugin / sdr.RECEIPT_NAME).unlink()
        with self.spy_hashing() as hashed:
            self.assertEqual(self.run_default(), 0, self.output)
        self.assertEqual(hashed, [])
        self.assertEqual(self.server.github_assets(), [])

    # Windows junctions and permissions

    @unittest.skipUnless(SYMLINKS, "symbolic links are not available")
    def test_junction_at_staging_is_not_followed(self):
        # lstat reports a junction as a folder; a link reported the same way stands in for one.
        outside = self.tmp / "Documents"
        outside.mkdir()
        (outside / "keep.bin").write_bytes(b"keep me")
        junction = os.path.join(os.path.realpath(self.plugin), sdr.STAGING_NAME)
        os.symlink(outside, junction, target_is_directory=True)
        real_lstat = os.lstat

        def lstat(path, *args, **kwargs):
            st = real_lstat(path, *args, **kwargs)
            if os.fspath(path) == junction and stat.S_ISLNK(st.st_mode):
                return types.SimpleNamespace(st_mode=stat.S_IFDIR | 0o755, st_size=0,
                                             st_mtime_ns=st.st_mtime_ns,
                                             st_reparse_tag=sdr.IO_REPARSE_TAG_MOUNT_POINT)
            return st
        with mock.patch.object(os, "lstat", lstat):
            self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertEqual(os.listdir(outside), ["keep.bin"])
        self.assertEqual((outside / "keep.bin").read_bytes(), b"keep me")
        self.assert_installed(self.release.variant("compact"))
        self.assert_no_staging()

    @unittest.skipIf(os.name == "nt" or (hasattr(os, "geteuid") and os.geteuid() == 0),
                     "needs POSIX permissions and a user other than root")
    def test_read_only_plugin_data(self):
        os.chmod(self.plugin, 0o555)
        self.addCleanup(os.chmod, self.plugin, 0o755)
        self.assertEqual(self.run_script("compact"), 1, self.output)
        self.assertIn("read only", self.output)
        self.assertEqual(os.listdir(self.plugin), [])
        self.assertEqual(self.server.github_assets(), [])

    # Refusals before anything is written

    def test_manifest_hash_mismatch_aborts_before_writing(self):
        self.assertEqual(self.run_script("compact", sha256="0" * 64), 1, self.output)
        self.assertIn("not the one this mod build expects", self.output)
        self.assertEqual(os.listdir(self.plugin), [])
        self.assertEqual([r["asset"] for r in self.server.requests], [sdr.MANIFEST_ASSET] * 2)

    def test_old_mod_version_refused(self):
        fx.write_version(self.plugin.parent, "0.4.1")
        self.assertEqual(self.run_script("complete"), 1, self.output)
        self.assertIn("0.5.0", self.output)
        self.assertEqual(os.listdir(self.plugin), [])
        self.assertEqual(self.server.github_assets(), [])

    def test_low_disk_refused_before_writing(self):
        (self.plugin / "GaiaStarCatalog-G13.starcat").write_bytes(b"old build")
        usage = shutil.disk_usage(str(self.tmp))._replace(free=1000)
        with mock.patch.object(sdr.shutil, "disk_usage", return_value=usage):
            self.assertEqual(self.run_script("compact"), 1, self.output)
        self.assertIn("Not enough disk space", self.output)
        self.assertEqual(os.listdir(self.plugin), ["GaiaStarCatalog-G13.starcat"])
        self.assertEqual(self.server.github_assets(), [])

    def test_dry_run_changes_nothing(self):
        self.assertEqual(self.run_script("compact", "--dry-run", yes=False), 0, self.output)
        self.assertIn("nothing was changed", self.output)
        self.assertEqual(os.listdir(self.plugin), [])
        compact_assets = self.release.assets_of(self.release.variant("compact"))
        self.assertEqual(sorted(self.server.github_assets()), sorted(compact_assets))
        for asset in compact_assets:
            self.assertEqual(self.server.cdn_ranges(asset), ["bytes=0-0"])


if __name__ == "__main__":
    unittest.main()
