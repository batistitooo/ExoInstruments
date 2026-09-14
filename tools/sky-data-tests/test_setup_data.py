"""setup_data.py: the release star field, its link safe install() and KSP discovery."""

import contextlib
import io
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import player_fixture as fx
import setup_data
import sky_data_release as sdr

V13 = sdr.DEFAULT_NAME
MAIN = fx.MAIN
SYMLINKS = fx.can_symlink()
NO_PROXY = {"no_proxy": "127.0.0.1,localhost", "NO_PROXY": "127.0.0.1,localhost"}
POSIX_PERMISSIONS = os.name != "nt" and not (hasattr(os, "geteuid") and os.geteuid() == 0)


class SetupDataStars(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.release = fx.build_release()

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="sky-data-setup-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.ksp, self.plugin = fx.make_ksp(self.tmp / "KSP")
        self.server = fx.ReleaseServer(self.release.assets)
        self.addCleanup(self.server.close)
        self.venv = mock.Mock(return_value="python")
        patches = [mock.patch.dict(os.environ, NO_PROXY),
                   mock.patch.object(sdr, "BASE_URL", self.server.base_url),
                   mock.patch.object(sdr, "MANIFEST_SHA256", self.release.manifest_sha256),
                   mock.patch.object(sdr, "RETRY_DELAY", 0.01),
                   mock.patch.object(sdr, "RETRY_MAX_DELAY", 0.05),
                   mock.patch.object(sdr, "SOCKET_TIMEOUT", 5.0),
                   mock.patch.object(setup_data, "ensure_venv", self.venv),
                   mock.patch.object(setup_data, "resolve_work_dir", return_value=self.tmp / "work")]
        for patcher in patches:
            patcher.start()
            self.addCleanup(patcher.stop)
        # Restored by the patch.dict above.
        for name in ("GAIA_USER", "GAIA_PASSWORD", "KSP"):
            os.environ.pop(name, None)
        self.output = ""

    def run_setup(self, *args):
        argv = ["--ksp", str(self.ksp)] + list(args)
        out = io.StringIO()
        with mock.patch.object(sys, "stdin", io.StringIO("")), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            try:
                code = setup_data.main(argv)
            except SystemExit as stop:
                code = stop.code or 0
        self.output = out.getvalue()
        return code

    def assert_release_v13(self):
        path = self.plugin / V13
        self.assertTrue(path.is_file() and not path.is_symlink())
        self.assertEqual(fx.sha(path.read_bytes()), self.release.sha(V13))

    def test_fresh_install_then_skip(self):
        self.assertEqual(self.run_setup("--only", "stars"), 0, self.output)
        self.assert_release_v13()
        self.assertEqual(sorted(n for n in os.listdir(self.plugin) if sdr.is_star_name(n)), [V13])
        self.assertEqual(self.server.github_assets(), [V13])
        self.assertNotIn("ESA archive", self.output)
        self.venv.assert_not_called()

        self.server.clear_log()
        self.assertEqual(self.run_setup("--only", "stars"), 0, self.output)
        self.assertIn(f"Already installed, skipping: stars ({V13}).", self.output)
        self.assertEqual(self.server.requests, [])

        # --force asks the release again, which finds V13 already in place.
        self.assertEqual(self.run_setup("--only", "stars", "--force"), 0, self.output)
        self.assertIn("Nothing to do", self.output)
        self.assertEqual(self.server.github_assets(), [])
        self.assert_release_v13()

    def test_existing_main_is_kept(self):
        # Any sound catalogue stands in for an old G13 build.
        own = self.release.files[V13]
        (self.plugin / MAIN).write_bytes(own)
        self.assertEqual(self.run_setup("--only", "stars"), 0, self.output)
        self.assertIn(f"Already installed, skipping: stars ({MAIN}).", self.output)
        self.assertEqual(self.server.requests, [])

        self.assertEqual(self.run_setup("--only", "stars", "--force"), 0, self.output)
        self.assertIn(f"{MAIN} is installed, so no star tier is added.", self.output)
        self.assertEqual(self.server.github_assets(), [])
        self.assertEqual(os.listdir(self.plugin), [MAIN])
        self.assertEqual((self.plugin / MAIN).read_bytes(), own)

    def test_esa_build_leaves_the_release_alone(self):
        builder, install = mock.Mock(), mock.Mock()
        with mock.patch.object(setup_data.BY_KEY["stars"], "builder", builder), \
                mock.patch.object(setup_data, "install", install), \
                mock.patch.dict(os.environ, {"GAIA_PASSWORD": "secret"}):
            code = self.run_setup("--only", "stars", "--stars", "esa", "--gaia-user", "someone")
        self.assertEqual(code, 0, self.output)
        builder.assert_called_once()
        install.assert_called_once()
        self.assertEqual(self.server.requests, [])
        self.assertEqual(os.listdir(self.plugin), [])

    def test_declined_replacement_is_a_failure(self):
        other = os.urandom(len(self.release.files[V13]))
        (self.plugin / V13).write_bytes(other)
        self.assertEqual(self.run_setup("--only", "stars"), 1, self.output)
        self.assertIn("Rerun with --yes", self.output)
        self.assertIn("FAILED: stars", self.output)
        self.assertEqual((self.plugin / V13).read_bytes(), other)
        self.assertEqual(self.server.github_assets(), [])

        self.assertEqual(self.run_setup("--only", "stars", "--yes"), 0, self.output)
        self.assert_release_v13()

    def test_ctrl_c_stops_the_whole_run(self):
        dust = mock.Mock()
        with mock.patch.object(sdr.Transfer, "run", side_effect=KeyboardInterrupt), \
                mock.patch.object(setup_data.BY_KEY["dust"], "builder", dust), \
                mock.patch.object(setup_data, "install", mock.Mock()):
            code = self.run_setup("--only", "stars,dust", "--yes")
        self.assertEqual(code, 1, self.output)
        self.assertIn("Stopped. Rerun to resume.", self.output)
        dust.assert_not_called()

    @unittest.skipUnless(POSIX_PERMISSIONS, "needs POSIX permissions and a user other than root")
    def test_read_only_plugin_data(self):
        os.chmod(self.plugin, 0o555)
        self.addCleanup(os.chmod, self.plugin, 0o755)
        self.assertEqual(self.run_setup("--only", "stars"), 1, self.output)
        self.assertIn("read only", self.output)
        self.assertIn("FAILED: stars", self.output)
        self.assertEqual(os.listdir(self.plugin), [])


class SetupDataInstall(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="sky-data-install-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.ksp, self.plugin = fx.make_ksp(self.tmp / "KSP")
        self.ctx = setup_data.Context()
        self.ctx.work = self.tmp / "work"
        self.ctx.work.mkdir()
        self.ctx.plugin_data = self.plugin
        self.product = setup_data.BY_KEY["dust"]
        self.name = self.product.filename
        self.output = ""

    def install(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            try:
                setup_data.install(self.product, self.ctx)
                code = 0
            except SystemExit as stop:
                code = stop.code
        self.output = out.getvalue()
        return code

    @unittest.skipUnless(SYMLINKS, "symbolic links are not available")
    def test_link_is_replaced_and_its_target_kept(self):
        built = b"EXODUST1" + os.urandom(1000)
        (self.ctx.work / self.name).write_bytes(built)
        target = self.tmp / "Studio" / self.name
        target.parent.mkdir()
        target.write_bytes(b"EXODUST1 kept elsewhere")
        os.symlink(target, self.plugin / self.name)
        st = os.stat(target)
        sdr.save_receipt(str(self.plugin), {"release": sdr.RELEASE, "files": {
            self.name: sdr.receipt_record(st.st_size, st.st_mtime_ns, "0" * 64, str(target))}})

        self.assertEqual(self.install(), 0, self.output)
        self.assertIn("replacing the link", self.output)
        installed = self.plugin / self.name
        self.assertFalse(installed.is_symlink())
        self.assertEqual(installed.read_bytes(), built)
        self.assertEqual(target.read_bytes(), b"EXODUST1 kept elsewhere")
        # A source build is not the release's file, so the receipt no longer vouches for it.
        self.assertEqual(sdr.load_receipt(str(self.plugin))["files"], {})
        self.assertEqual(sorted(os.listdir(self.plugin)), sorted([sdr.RECEIPT_NAME, self.name]))

    def test_wrong_magic_is_refused(self):
        (self.ctx.work / self.name).write_bytes(b"NOTDUST1" + bytes(100))
        (self.plugin / self.name).write_bytes(b"EXODUST1 old")
        self.assertEqual(self.install(), 1, self.output)
        self.assertIn("does not start with EXODUST1", self.output)
        self.assertEqual((self.plugin / self.name).read_bytes(), b"EXODUST1 old")
        self.assertEqual(os.listdir(self.plugin), [self.name])


class SetupDataDiscovery(unittest.TestCase):
    def test_same_discovery_with_a_plain_error(self):
        for name in ("plugin_data_dir", "SHIPPED_IN_GAMEDATA"):
            self.assertIs(getattr(setup_data, name), getattr(sdr, name))
        with tempfile.TemporaryDirectory() as ksp:
            os.mkdir(os.path.join(ksp, "GameData"))
            self.assertEqual(setup_data.resolve_ksp(ksp), sdr.resolve_ksp(ksp))
            err = io.StringIO()
            # publish_sky_data.py calls it too, and expects an error line rather than a traceback.
            with contextlib.redirect_stderr(err), self.assertRaises(SystemExit) as stop:
                setup_data.resolve_ksp(os.path.join(ksp, "missing"))
        self.assertEqual(stop.exception.code, 1)
        self.assertIn("[setup_data] ERROR:", err.getvalue())
        self.assertIn("is not a KSP install", err.getvalue())


if __name__ == "__main__":
    unittest.main()
