"""Products the release cannot carry, built by setup_data.py once a player script's downloads are in."""

import ast
import contextlib
import http.client
import importlib.util
import io
import os
import platform
import runpy
import shutil
import subprocess
import sys
import sysconfig
import tempfile
import unittest
import urllib.error
from pathlib import Path
from unittest import mock

import player_fixture as fx
import setup_data
import sky_data_release as sdr

TOOLS = fx.TOOLS
SCRIPTS = {"compact": TOOLS / "get_sky_data_compact.py",
           "complete": TOOLS / "get_sky_data_complete.py"}
HALPHA, PATCHES = "HalphaMap.emission", "HalphaPatches.patchset"
LOCAL = ["halpha", "patches"]
H_ALPHA_PACKAGES = ["numpy", "astropy", "astropy-healpix", "scipy", "requests"]
NO_PROXY = {"no_proxy": "127.0.0.1,localhost", "NO_PROXY": "127.0.0.1,localhost"}

# The pip name of every third party module a packer may import.
PIP_NAMES = {"numpy": "numpy", "scipy": "scipy", "astropy": "astropy",
             "astropy_healpix": "astropy-healpix", "healpy": "healpy", "requests": "requests",
             "dustmaps": "dustmaps"}
PACKERS = {"stars": "pack_gaia_catalog.py", "dust": "pack_dust_map.py",
           "halpha": "pack_halpha_map.py", "galaxies": "pack_galaxy_catalog.py",
           "patches": "pack_shassa_patches.py", "images": "pack_galaxy_images.py"}


def imported_modules(path):
    """Top level names of every absolute import in a script, including those inside functions."""
    names = set()
    for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
        if isinstance(node, ast.Import):
            names.update(alias.name.split(".")[0] for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and not node.level and node.module:
            names.add(node.module.split(".")[0])
    return names


def in_standard_library(name):
    if name in sys.builtin_module_names:
        return True
    spec = importlib.util.find_spec(name)
    if spec is None or not spec.origin or "site-packages" in spec.origin:
        return False
    if spec.origin in ("built-in", "frozen"):
        return True
    stdlib = os.path.realpath(sysconfig.get_paths()["stdlib"])
    return os.path.realpath(spec.origin).startswith(stdlib)


class Packages(unittest.TestCase):
    def packages(self, *keys):
        return setup_data.pip_packages([setup_data.BY_KEY[key] for key in keys])

    def test_halpha_and_patches_install_no_healpy(self):
        packages = self.packages("halpha", "patches")
        self.assertEqual(packages, H_ALPHA_PACKAGES)
        self.assertNotIn("healpy", packages)
        self.assertNotIn("dustmaps", packages)

    def test_dust_brings_healpy_and_dustmaps(self):
        packages = self.packages("dust", "halpha")
        self.assertEqual(packages, ["numpy", "astropy", "healpy", "dustmaps", "astropy-healpix"])
        everything = setup_data.pip_packages(setup_data.PRODUCTS)
        self.assertEqual(sorted(everything), sorted(set(PIP_NAMES.values())))

    def test_star_field_and_galaxies(self):
        self.assertEqual(self.packages("stars"), [])
        self.assertEqual(self.packages("galaxies"), ["requests"])

    def test_each_product_lists_what_its_packer_imports(self):
        self.assertEqual(set(PACKERS), set(setup_data.BY_KEY))
        for key, script in PACKERS.items():
            with self.subTest(key):
                modules = imported_modules(TOOLS / script)
                unplaced = sorted(m for m in modules - set(PIP_NAMES) if not in_standard_library(m))
                self.assertEqual(unplaced, [], f"{script} imports a module missing from PIP_NAMES")
                packages = setup_data.BY_KEY[key].packages
                self.assertEqual(sorted(packages), sorted(PIP_NAMES[m] for m in modules if m in PIP_NAMES))


class EnsureVenv(unittest.TestCase):
    def setUp(self):
        self.work = Path(tempfile.mkdtemp(prefix="sky-data-venv-"))
        self.addCleanup(shutil.rmtree, self.work, True)
        scripts = "Scripts/python.exe" if platform.system() == "Windows" else "bin/python"
        self.venv = self.work / "setup_env"
        self.python = self.venv / scripts
        self.calls, self.checks = [], []

    def ensure(self, packages, pip_runs=None):
        """ensure_venv with its commands recorded. pip_runs fakes the pip check; None runs it."""
        def record(command, **options):
            self.calls.append([str(part) for part in command])
            # Never write through a real virtualenv's link to its base interpreter.
            if "venv" in command and not os.path.lexists(self.python):
                self.python.parent.mkdir(parents=True, exist_ok=True)
                self.python.write_bytes(b"")

        def check(command, **options):
            self.checks.append([str(part) for part in command])
            return 0 if pip_runs else 1
        with contextlib.ExitStack() as stack:
            stack.enter_context(mock.patch.object(setup_data.subprocess, "check_call", side_effect=record))
            if pip_runs is not None:
                stack.enter_context(mock.patch.object(setup_data.subprocess, "call", side_effect=check))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            return setup_data.ensure_venv(self.work, packages)

    def created(self, packages):
        python = str(self.python)
        return [[sys.executable, "-m", "venv", "--clear", str(self.venv)],
                [python, "-m", "pip", "install", "--quiet", "--upgrade", "pip"],
                [python, "-m", "pip", "install", "--quiet"] + packages]

    def empty_interpreter(self):
        self.python.parent.mkdir(parents=True)
        self.python.write_bytes(b"")

    def test_new_virtualenv_gets_only_the_packages_asked_for(self):
        self.assertEqual(self.ensure(H_ALPHA_PACKAGES, pip_runs=True), self.python)
        self.assertEqual(self.checks, [])
        self.assertEqual(self.calls, self.created(H_ALPHA_PACKAGES))

    def test_existing_virtualenv_is_reused(self):
        self.empty_interpreter()
        self.ensure(["requests"], pip_runs=True)
        self.assertEqual(self.checks, [[str(self.python), "-m", "pip", "--version"]])
        self.assertEqual(self.calls, [[str(self.python), "-m", "pip", "install", "--quiet", "requests"]])

    def test_virtualenv_whose_pip_fails_is_created_again(self):
        self.empty_interpreter()
        self.ensure(["requests"], pip_runs=False)
        self.assertEqual(self.calls, self.created(["requests"]))

    def test_interpreter_that_cannot_start_is_replaced(self):
        # Running an empty file raises OSError on every platform.
        self.empty_interpreter()
        self.ensure(["requests"])
        self.assertEqual(self.calls, self.created(["requests"]))

    def test_virtualenv_left_without_pip_is_created_again(self):
        # What an interrupted creation leaves: a working interpreter with no pip. Needs no network.
        subprocess.check_call([sys.executable, "-m", "venv", "--without-pip", str(self.venv)])
        with mock.patch.dict(os.environ):
            for name in ("PYTHONPATH", "PYTHONHOME"):
                os.environ.pop(name, None)
            self.assertTrue(self.python.exists())
            self.assertNotEqual(subprocess.call([str(self.python), "-m", "pip", "--version"],
                                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL), 0)
            self.ensure(["requests"])
        self.assertEqual(self.calls, self.created(["requests"]))


class Response(io.BytesIO):
    """An archive's answer to urlopen: a Content-Length, then an empty body or `error` on reading."""

    def __init__(self, length, error=None):
        super().__init__(b"")
        self.headers = {"Content-Length": length}
        self.error = error

    def read(self, size=-1):
        if self.error is not None:
            raise self.error
        return super().read(size)


class FakeSetupData:
    """setup_data with pip and the H-alpha packers recorded; a packer writes a file with the right magic."""

    def fake_setup_data(self, errors=None):
        errors = errors or {}
        self.venv = mock.Mock(return_value="python")
        self.built = []
        patchers = [mock.patch.object(setup_data, "ensure_venv", self.venv),
                    mock.patch.object(setup_data, "resolve_work_dir", return_value=self.tmp / "work")]
        for key in LOCAL:
            product = setup_data.BY_KEY[key]
            patchers.append(mock.patch.object(product, "builder",
                                              self.packer(product, errors.get(key))))
        for patcher in patchers:
            patcher.start()
            self.addCleanup(patcher.stop)

    def packer(self, product, error):
        def build(ctx):
            self.built.append((product.key, ctx.plugin_data))
            if error is not None:
                raise error
            ctx.work.mkdir(parents=True, exist_ok=True)
            (ctx.work / product.filename).write_bytes(product.magic + os.urandom(64))
        return build

    def break_halpha_download(self, response):
        """The real halpha builder, with the archive answering `response`."""
        for patcher in (mock.patch.object(setup_data.BY_KEY["halpha"], "builder", setup_data.build_halpha),
                        mock.patch.object(setup_data.urllib.request, "urlopen", return_value=response)):
            patcher.start()
            self.addCleanup(patcher.stop)

    def make_tmp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="sky-data-local-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.ksp, self.plugin = fx.make_ksp(self.tmp / "KSP")
        self.output = ""


class SetupDataInProcess(FakeSetupData, unittest.TestCase):
    """setup_data.main(argv) called the way the player scripts call it."""

    def setUp(self):
        self.make_tmp()

    def run_setup(self, only="halpha,patches"):
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            try:
                code = setup_data.main(["--only", only, "--ksp", str(self.ksp), "--yes"])
            except SystemExit as stop:
                code = stop.code
        self.output = out.getvalue()
        return code

    def test_builds_in_order_with_only_their_packages(self):
        self.fake_setup_data()
        self.assertEqual(self.run_setup("patches,halpha"), 0, self.output)
        self.assertEqual(self.built, [("halpha", self.plugin), ("patches", self.plugin)])
        self.venv.assert_called_once_with(self.tmp / "work", H_ALPHA_PACKAGES)
        for name in (HALPHA, PATCHES):
            self.assertTrue((self.plugin / name).is_file(), name)

        self.venv.reset_mock()
        self.built.clear()
        self.assertEqual(self.run_setup(), 0, self.output)
        self.assertIn("Everything selected is already installed", self.output)
        self.venv.assert_not_called()
        self.assertEqual(self.built, [])

    def test_a_failed_packer_raises_system_exit(self):
        self.fake_setup_data({"halpha": subprocess.CalledProcessError(3, "pack_halpha_map.py")})
        self.assertEqual(self.run_setup(), 1, self.output)
        self.assertIn("FAILED: halpha (exit 3)", self.output)
        self.assertIn("Failed (rerun to resume): halpha", self.output)

    def test_an_unreachable_download_costs_only_its_product(self):
        self.fake_setup_data({"halpha": urllib.error.URLError("no route to host")})
        self.assertEqual(self.run_setup(), 1, self.output)
        self.assertIn("FAILED: halpha (<urlopen error no route to host>)", self.output)
        self.assertEqual([key for key, _ in self.built], LOCAL)
        self.assertTrue((self.plugin / PATCHES).is_file())

    def test_a_download_cut_short_costs_only_its_product(self):
        self.fake_setup_data()
        self.break_halpha_download(Response("50342400", http.client.IncompleteRead(b"")))
        self.assertEqual(self.run_setup(), 1, self.output)
        self.assertIn("FAILED: halpha (IncompleteRead(0 bytes read)). Continuing with the rest.", self.output)
        self.assertFalse((self.plugin / HALPHA).exists())
        self.assertTrue((self.plugin / PATCHES).is_file())

    def test_a_malformed_response_costs_only_its_product(self):
        self.fake_setup_data()
        self.break_halpha_download(Response("many"))
        self.assertEqual(self.run_setup(), 1, self.output)
        self.assertIn("FAILED: halpha (invalid literal for int() with base 10: 'many'). "
                      "Continuing with the rest.", self.output)
        self.assertTrue((self.plugin / PATCHES).is_file())

    def test_a_pip_failure_is_an_error_line(self):
        self.fake_setup_data()
        self.venv.side_effect = subprocess.CalledProcessError(1, ["pip"])
        self.assertEqual(self.run_setup(), 1, self.output)
        self.assertIn("[setup_data] ERROR: Could not install", self.output)
        self.assertEqual(self.built, [])


class PlayerScripts(FakeSetupData, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bare = fx.build_release(without=(HALPHA, PATCHES))
        cls.full = fx.build_release()

    def setUp(self):
        self.make_tmp()
        patchers = [mock.patch.dict(os.environ, NO_PROXY)]
        patchers += [mock.patch.object(sdr, name, value) for name, value in
                     (("RETRY_DELAY", 0.01), ("RETRY_MAX_DELAY", 0.05), ("SOCKET_TIMEOUT", 5.0))]
        for patcher in patchers:
            patcher.start()
            self.addCleanup(patcher.stop)
        self.serve(self.bare)
        self.calls = []

    def serve(self, release):
        self.release = release
        self.server = fx.ReleaseServer(release.assets)
        self.addCleanup(self.server.close)

    def run_script(self, variant, *extra):
        script = str(SCRIPTS[variant])
        argv = [script, "--ksp", str(self.ksp), "--base-url", self.server.base_url,
                "--manifest-sha256", self.release.manifest_sha256, "--yes"] + list(extra)
        out = io.StringIO()
        with mock.patch.object(sys, "argv", argv), mock.patch.object(sys, "stdin", io.StringIO("")), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            try:
                runpy.run_path(script, run_name="__main__")
                code = 0
            except SystemExit as stop:
                code = stop.code or 0
        self.output = out.getvalue()
        return code

    def record_builds(self, install=LOCAL, code=0, error=None):
        """Stands in for run_local_builds, noting what PluginData held when it was called."""
        self.build_install, self.build_code, self.build_error = install, code, error

        def run(products, ksp):
            self.calls.append((list(products), Path(ksp), self.snapshot()))
            if self.build_error is not None:
                raise self.build_error
            for key in self.build_install:
                product = setup_data.BY_KEY[key]
                (self.plugin / product.filename).write_bytes(product.magic + os.urandom(64))
            return self.build_code
        patcher = mock.patch.object(sdr, "run_local_builds", side_effect=run)
        patcher.start()
        self.addCleanup(patcher.stop)

    def snapshot(self):
        return {name: fx.sha((self.plugin / name).read_bytes())
                for name in os.listdir(self.plugin) if (self.plugin / name).is_file()}

    def assert_released(self, names, snapshot=None):
        snapshot = self.snapshot() if snapshot is None else snapshot
        for name in names:
            self.assertEqual(snapshot.get(name), self.release.sha(name), name)

    def check_builds_after_downloads(self, variant):
        self.record_builds()
        self.assertEqual(self.run_script(variant), 0, self.output)
        self.assertEqual(len(self.calls), 1, self.output)
        products, ksp, before = self.calls[0]
        self.assertEqual((products, ksp), (LOCAL, self.ksp))
        self.assert_released(self.release.variant(variant), before)
        self.assertNotIn(HALPHA, before)
        line = ("Building halpha and patches on this computer, as their survey terms do not allow "
                "redistribution: about 600 MB to download the first time, and up to an hour to run.")
        self.assertIn(line, self.output)
        self.assertLess(self.output.index("Done: downloaded"), self.output.index(line))
        self.assertTrue(self.output.rstrip().endswith("Start KSP: its log names the star catalogue it loaded."))

    # The builder recorded

    def test_compact_builds_what_the_release_lacks_after_downloading(self):
        self.check_builds_after_downloads("compact")

    def test_complete_builds_what_the_release_lacks_after_downloading(self):
        self.check_builds_after_downloads("complete")

    def test_nothing_is_built_when_the_release_carries_everything(self):
        self.serve(self.full)
        self.record_builds()
        for variant in ("compact", "complete"):
            self.assertEqual(self.run_script(variant), 0, self.output)
            self.assertNotIn("on this computer", self.output)
            self.assert_released(self.release.variant(variant))
        self.assertEqual(self.calls, [])

    def test_dry_run_lists_the_builds_without_running_them(self):
        self.record_builds()
        self.assertEqual(self.run_script("compact", "--dry-run"), 0, self.output)
        self.assertEqual(self.calls, [])
        self.assertIn("Would then build halpha and patches on this computer", self.output)
        self.assertIn("Dry run: nothing was changed.", self.output)
        self.assertEqual(os.listdir(self.plugin), [])

    def test_a_failed_build_keeps_the_downloads(self):
        self.record_builds(install=["halpha"], code=1)
        self.assertEqual(self.run_script("compact"), 1, self.output)
        compact = self.release.variant("compact")
        self.assert_released(compact)
        self.assertTrue((self.plugin / HALPHA).is_file())
        self.assertEqual(self.output.rstrip().splitlines()[-1],
                         "FAILED: patches could not be built on this computer. "
                         "The downloads are kept; rerun to resume.")
        self.assertNotIn("Start KSP", self.output)

        # A rerun downloads nothing and builds only what is still missing.
        self.server.clear_log()
        self.build_install, self.build_code = ["patches"], 0
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertEqual(self.server.github_assets(), [])
        self.assertIn("Nothing to download", self.output)
        self.assertIn("Building patches on this computer, as its survey terms do not allow "
                      "redistribution: about 550 MB to download the first time, and up to an hour to run.",
                      self.output)
        self.assertEqual([call[0] for call in self.calls], [LOCAL, ["patches"]])
        self.assert_released(compact)

    def test_a_rerun_announces_no_build_already_done(self):
        self.record_builds()
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.server.clear_log()
        self.assertEqual(self.run_script("compact"), 0, self.output)
        self.assertIn("Nothing to do: every file is already in place.", self.output)
        self.assertNotIn("on this computer", self.output)
        self.assertEqual(self.run_script("compact", "--dry-run"), 0, self.output)
        self.assertNotIn("on this computer", self.output)
        self.assertIn("Dry run: nothing was changed.", self.output)
        self.assertEqual(self.server.github_assets(), [])
        self.assertEqual(len(self.calls), 1)

    def test_dry_run_and_build_skip_what_is_already_installed(self):
        product = setup_data.BY_KEY["halpha"]
        (self.plugin / HALPHA).write_bytes(product.magic + os.urandom(64))
        self.record_builds(install=["patches"])
        self.assertEqual(self.run_script("complete", "--dry-run"), 0, self.output)
        self.assertIn("Would then build patches on this computer, as its survey terms", self.output)
        self.assertNotIn("halpha", self.output)
        self.assertEqual(self.calls, [])
        self.assertEqual(self.run_script("complete"), 0, self.output)
        self.assertEqual([call[0] for call in self.calls], [["patches"]])

    def test_a_build_that_raises_nothing_but_installs_nothing_names_every_product(self):
        self.record_builds(install=[], code=0)
        self.assertEqual(self.run_script("complete"), 1, self.output)
        self.assertIn("FAILED: halpha and patches could not be built on this computer.", self.output)
        self.assert_released(self.release.variant("complete"))

    def test_ctrl_c_during_a_build_stops_the_run(self):
        self.record_builds(error=KeyboardInterrupt())
        self.assertEqual(self.run_script("compact"), 1, self.output)
        self.assertTrue(self.output.rstrip().endswith("Stopped. Rerun to resume."), self.output)
        self.assertNotIn("FAILED", self.output)
        self.assert_released(self.release.variant("compact"))

    # setup_data.py itself, in process

    def test_setup_data_runs_in_process_with_the_resolved_ksp(self):
        self.fake_setup_data()
        self.assertEqual(self.run_script("complete"), 0, self.output)
        self.assertEqual(self.built, [("halpha", self.plugin), ("patches", self.plugin)])
        self.venv.assert_called_once_with(self.tmp / "work", H_ALPHA_PACKAGES)
        for key in LOCAL:
            product = setup_data.BY_KEY[key]
            self.assertTrue((self.plugin / product.filename).read_bytes().startswith(product.magic))
        self.assert_released(self.release.variant("complete"))
        self.assertIn("[setup_data] Built and installed: halpha, patches", self.output)

    def test_a_failed_packer_is_named(self):
        self.fake_setup_data({"patches": subprocess.CalledProcessError(1, "pack_shassa_patches.py")})
        self.assertEqual(self.run_script("compact"), 1, self.output)
        self.assertIn("FAILED: patches could not be built on this computer.", self.output)
        self.assertTrue((self.plugin / HALPHA).is_file())
        self.assert_released(self.release.variant("compact"))

    def test_a_download_cut_short_inside_setup_data_is_named(self):
        self.fake_setup_data()
        self.break_halpha_download(Response("50342400", http.client.IncompleteRead(b"")))
        self.assertEqual(self.run_script("compact"), 1, self.output)
        self.assertIn("[setup_data] FAILED: halpha (IncompleteRead(0 bytes read))", self.output)
        self.assertNotIn("Traceback", self.output)
        self.assertEqual(self.output.rstrip().splitlines()[-1],
                         "FAILED: halpha could not be built on this computer. "
                         "The downloads are kept; rerun to resume.")
        self.assertTrue((self.plugin / PATCHES).is_file())
        self.assert_released(self.release.variant("compact"))

    def test_an_unexpected_error_in_a_build_is_named(self):
        self.fake_setup_data({"halpha": RuntimeError("the cache is unreadable")})
        self.assertEqual(self.run_script("complete"), 1, self.output)
        self.assertIn("Building stopped on an unexpected error: RuntimeError: the cache is unreadable",
                      self.output)
        self.assertNotIn("Traceback", self.output)
        self.assertEqual(self.output.rstrip().splitlines()[-1],
                         "FAILED: halpha and patches could not be built on this computer. "
                         "The downloads are kept; rerun to resume.")
        self.assert_released(self.release.variant("complete"))

    def test_ctrl_c_inside_setup_data_stops_everything(self):
        self.fake_setup_data({"halpha": KeyboardInterrupt()})
        self.assertEqual(self.run_script("compact"), 1, self.output)
        self.assertTrue(self.output.rstrip().endswith("Stopped. Rerun to resume."), self.output)
        self.assertEqual([key for key, _ in self.built], ["halpha"])
        self.assertNotIn("FAILED", self.output)
        self.assert_released(self.release.variant("compact"))


if __name__ == "__main__":
    unittest.main()
