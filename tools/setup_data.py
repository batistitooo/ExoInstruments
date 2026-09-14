#!/usr/bin/env python3
"""Builds and installs every optional sky-survey file ExoInstruments can read, in one command.

    python3 GameData/ExoInstruments/tools/setup_data.py

That is the whole thing, and it takes no arguments. It finds your KSP install, builds a private
virtualenv, downloads what has to be downloaded, runs each packer, checks each result, and copies
it into <KSP>/GameData/ExoInstruments/PluginData/.

Nothing here is required to play. The mod works and photographs the solar system with none of
these files present, and each one you add turns on one more thing. This script exists so that
turning them all on is one command instead of a virtualenv, six scripts, a hand-fetched FITS file
and six copies.

WHAT IT BUILDS, and in what order. The order is not cosmetic: two of the products are built from
another product rather than from an archive, so the dependency is real.

    stars      GaiaStarCatalog.V13.starcat  Gaia DR3 to V 13, from the sky data release
    dust       DustMap.dustmap              SFD98, via the dustmaps package
    halpha     HalphaMap.emission           Finkbeiner (2003), via NASA LAMBDA
    galaxies   GalaxyCatalog.galcat         HyperLEDA
    patches    HalphaPatches.patchset       SHASSA, calibrated against halpha
    images     GalaxyImages.galimg          survey cutouts, driven by galaxies

WHAT IS OFF BY DEFAULT. `patches` and `images` are opt-in (--with patches,images or --with all).
Not because they are worth less, but because they cost a different order of magnitude: patches
downloads about 550 MB of survey cutouts, and images fetches survey cutouts for every galaxy in the
catalogue and runs for hours. The other four are a coffee break.

IDEMPOTENT ON PURPOSE. Anything already installed is left alone, so rerunning after an interruption
picks up where it stopped rather than redoing hours of work. --force rebuilds regardless, and
--only <keys> restricts the run to the products you name.

THE STAR FIELD. By default it is GaiaStarCatalog.V13.starcat (78 MB, stars to V 13), downloaded
from the ExoInstruments sky data release and checked against its sha256, with no account needed.
It is skipped when a GaiaStarCatalog.starcat or a release tier is already installed. For deeper
fields run get_sky_data_compact.py (to V 19) or get_sky_data_complete.py (every star); both then
run this script for halpha and patches, which the release cannot carry.

THE ESA ACCOUNT. --stars esa builds GaiaStarCatalog.starcat from the ESA archive to --gmax instead,
and that needs a login, because anonymous access to the Gaia archive hits a job wall that no amount
of retrying gets past. Registration is free at https://cosmos.esa.int/web/gaia-users/register.
Pass --gaia-user, set GAIA_USER, or answer the prompt; the password is prompted for or read from
GAIA_PASSWORD and is never taken on the command line. With no username at all the star field is
skipped and everything else still runs.

EVERY PRODUCT IS CHECKED BEFORE IT IS INSTALLED. Each packer prints its own named sanity checks as
it runs (M31 must come out 3.2 degrees across at B_T 4.4, and so on), and on top of that this
script refuses to install a file that does not begin with the magic number its format is supposed
to have. A truncated download or a half-written file is caught here rather than in the game.
"""

import argparse
import getpass
import hashlib
import http.client
import os
import platform
import subprocess
import sys
import urllib.request
from pathlib import Path

TOOLS = Path(__file__).resolve().parent

# KSP discovery lives in sky_data_release.py, so this script and the player scripts find KSP the
# same way. SHIPPED_IN_GAMEDATA also decides where the build work goes.
import sky_data_release
from sky_data_release import (DEFAULT_NAME, SHIPPED_IN_GAMEDATA, SkyDataError, install_local_file,
                              install_variant, plugin_data_dir, stars_installed)

# The one source file no archive will hand over programmatically on stable terms. LAMBDA's URLs
# have moved before, and the nside 512 map sitting next to it in the same directory is a
# plausible-looking wrong answer, so this download is pinned by digest rather than trusted by name.
# See pack_halpha_map.py for why the 1024 map specifically.
HALPHA_URL = "https://lambda.gsfc.nasa.gov/data/foregrounds/fink_halpha/Halpha_fwhm06_1024.fits"
HALPHA_SHA256 = "8daaf304acc1c320096a0c41667bc8a5ae272b4208d64e003b7d2c1ba9512936"


def log(msg):
    print(f"[setup_data] {msg}", flush=True)


def die(msg):
    print(f"[setup_data] ERROR: {msg}", file=sys.stderr, flush=True)
    sys.exit(1)


def resolve_ksp(explicit, log=log):
    """The engine's KSP discovery, ending with an error line rather than a traceback."""
    try:
        return sky_data_release.resolve_ksp(explicit, log)
    except SkyDataError as error:
        die(str(error))


# ---------------------------------------------------------------------------
# Choosing where to work


def resolve_work_dir(ksp):
    """The scratch directory: the virtualenv, the downloads, the packers' own resumable caches,
    and each product before it is checked and installed.

    It MUST NOT be inside GameData. KSP walks that whole tree at load, and a virtualenv is
    thousands of files; the SHASSA and galaxy-image caches are gigabytes more. So a shipped copy
    of this script works in a sibling of GameData, where the game never looks. A copy running out
    of a repository clone keeps working in tools/, which is where the packers' caches already live
    and where .gitignore already expects them.
    """
    return (ksp / "ExoInstruments-data-build") if SHIPPED_IN_GAMEDATA else TOOLS


# ---------------------------------------------------------------------------
# The virtualenv


def pip_packages(products):
    """What the packers of `products` import, each package once, in build order."""
    packages = []
    for product in products:
        packages += [p for p in product.packages if p not in packages]
    return packages


def venv_ready(python):
    """Whether the virtualenv's interpreter runs pip. An interrupted creation, or a Python without
    ensurepip, leaves an interpreter with no pip, which installing packages can never repair."""
    if not python.exists():
        return False
    try:
        return subprocess.call([str(python), "-m", "pip", "--version"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) == 0
    except OSError:
        return False


def ensure_venv(work, packages):
    """Builds the virtualenv once and installs `packages` into it, the ones this run's packers need.

    Deliberately a virtualenv rather than a --user install or the ambient interpreter: healpy and
    astropy pin versions against each other, and a player's system Python is not ours to touch.
    """
    # "setup_env", not "env": running out of a repository clone the work directory IS tools/,
    # where tools/env is a virtualenv checked into the repository for the older test harnesses.
    # Installing into that one would rewrite tracked files.
    venv = work / "setup_env"
    python = venv / ("Scripts/python.exe" if platform.system() == "Windows" else "bin/python")
    if not venv_ready(python):
        if venv.exists():
            log(f"Recreating virtualenv at {venv}, which has no working pip")
        else:
            log(f"Creating virtualenv at {venv}")
        venv.parent.mkdir(parents=True, exist_ok=True)
        # --clear, so what an interrupted attempt left behind is replaced rather than reused.
        subprocess.check_call([sys.executable, "-m", "venv", "--clear", str(venv)])
        # Only in a virtualenv we just created, never in one that already existed. An older
        # interpreter bundles a pip too old to resolve current wheels, but upgrading somebody
        # else's environment is not this script's business.
        subprocess.check_call([str(python), "-m", "pip", "install", "--quiet", "--upgrade", "pip"])
    log(f"Installing Python packages: {', '.join(packages)} (a few minutes the first time)")
    subprocess.check_call([str(python), "-m", "pip", "install", "--quiet", *packages])
    return python


# ---------------------------------------------------------------------------
# Downloads


def file_sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(url, dest, sha256=None):
    """Fetches to a .part file and only renames once the digest matches, so an interrupted
    download can never be mistaken for a complete one on the next run."""
    if dest.exists() and (sha256 is None or file_sha256(dest) == sha256):
        log(f"Already downloaded: {dest.name}")
        return dest
    dest.parent.mkdir(parents=True, exist_ok=True)
    partial = dest.with_suffix(dest.suffix + ".part")
    log(f"Downloading {url}")
    with urllib.request.urlopen(url) as response, open(partial, "wb") as out:
        total = int(response.headers.get("Content-Length") or 0)
        done = 0
        while True:
            chunk = response.read(1 << 20)
            if not chunk:
                break
            out.write(chunk)
            done += len(chunk)
            if total:
                print(f"\r  {done / 1e6:.0f} / {total / 1e6:.0f} MB", end="", flush=True)
    print()
    if sha256:
        actual = file_sha256(partial)
        if actual != sha256:
            partial.unlink()
            die(f"{dest.name} downloaded with digest {actual}, expected {sha256}. The archive's "
                "file has changed or the download was corrupted; it is not being installed.")
    partial.replace(dest)
    return dest


# ---------------------------------------------------------------------------
# The products


def run_packer(ctx, python, script, args, extra_env=None):
    """Runs a packer with the work directory as its cwd, which is where it writes its output and
    looks for its own resumable caches (*.starcat.cache, shassa_cache, galaxy_image_cache).

    extra_env is merged into this one child's environment only. That matters for the ESA password:
    putting it in our own os.environ would hand it to every other packer and to pip as well.
    """
    command = [str(python), str(TOOLS / script), *args]
    log("Running: " + " ".join(command))
    ctx.work.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, **extra_env) if extra_env else None
    subprocess.check_call(command, cwd=str(ctx.work), env=env)


def build_gaia(ctx):
    args = ["--gmax", str(ctx.gmax), "--out", "GaiaStarCatalog.starcat"]
    if ctx.gaia_user:
        args += ["--user", ctx.gaia_user]
    # The password reaches the packer through GAIA_PASSWORD, which is how pack_gaia_catalog.py
    # already expects it, and only ever in this child's environment. It is never an argument:
    # that would put it in the process list and in shell history.
    extra_env = {"GAIA_PASSWORD": ctx.gaia_password} if ctx.gaia_password else None
    # Stdlib only, so the ambient interpreter is enough and the virtualenv is not a prerequisite.
    run_packer(ctx, sys.executable, "pack_gaia_catalog.py", args, extra_env=extra_env)


def build_dust(ctx):
    run_packer(ctx, ctx.python, "pack_dust_map.py", ["--out", "DustMap.dustmap"])


def build_halpha(ctx):
    fits = download(HALPHA_URL, ctx.work / "downloads" / "Halpha_fwhm06_1024.fits", HALPHA_SHA256)
    run_packer(ctx, ctx.python, "pack_halpha_map.py",
               ["--input", str(fits), "--out", "HalphaMap.emission"])


def build_galaxies(ctx):
    run_packer(ctx, ctx.python, "pack_galaxy_catalog.py",
               ["--bmax", str(ctx.bmax), "--out", "GalaxyCatalog.galcat"])


def build_patches(ctx):
    run_packer(ctx, ctx.python, "pack_shassa_patches.py",
               ["--composite", str(ctx.plugin_data / "HalphaMap.emission"),
                "--out", "HalphaPatches.patchset"])


def build_images(ctx):
    run_packer(ctx, ctx.python, "pack_galaxy_images.py",
               ["--catalog", str(ctx.plugin_data / "GalaxyCatalog.galcat"),
                "--bmax", str(ctx.image_bmax), "--out", "GalaxyImages.galimg"])


def gaia_band_fault(path):
    """Names a broken declination index in a star catalogue, or returns None.

    WHY THE MAGIC NUMBER IS NOT ENOUGH HERE. Every other check in this script asks whether the
    file arrived intact. This one asks whether it is USABLE, which for this format is a different
    question: RenderedStarCatalog.Search reads only the declination bands the requested cone
    overlaps, so a catalogue whose stars are filed under the wrong bands loads cleanly, reports
    its full star count, decodes every record correctly, and returns nothing from every search.
    The sky renders empty, in the game and everywhere else, with no error logged anywhere, and an
    empty frame is indistinguishable from a genuinely empty field.

    That is not a hypothetical failure. A version-3 build of 7,369,627 stars shipped with 91 of
    1800 bands populated and 4.87 million stars, two thirds of the file, in the band at dec +89.9,
    because the reddening column was inserted into the middle of the tuple the band sort read
    from. The check costs one header read.
    """
    from pack_gaia_catalog import header_band_fault   # same directory; stdlib only
    return header_band_fault(path)


class Product:
    def __init__(self, key, filename, magic, summary, builder, default, needs=(), packages=(),
                 min_version=None, validate=None, download_bytes=0):
        self.key = key
        self.filename = filename
        self.magic = magic
        self.min_version = min_version
        self.summary = summary
        self.builder = builder
        self.default = default
        self.needs = needs
        # The pip packages its packer imports; none means it runs on the ambient interpreter.
        self.packages = packages
        # Roughly what a first build fetches, for the player scripts to announce; 0 when unmeasured.
        self.download_bytes = download_bytes
        # Optional deeper check, run on a freshly built file before it is installed and on an
        # already-installed one before it is skipped. Returns a sentence, or None when sound.
        self.validate = validate


# Per packer, so a run installs only what it builds: healpy has no Windows wheel and only dust
# needs it. astropy-healpix is NOT healpy, and the H-alpha packers need that one.
PRODUCTS = [
    Product("stars", "GaiaStarCatalog.starcat", b"EXOSTAR1",
            "the star field behind every photograph, from Gaia DR3",
            build_gaia, default=True, validate=gaia_band_fault),
    Product("dust", "DustMap.dustmap", b"EXODUST1",
            "interstellar reddening and the extinction readout, from SFD98",
            build_dust, default=True, packages=("numpy", "astropy", "healpy", "dustmaps")),
    Product("halpha", "HalphaMap.emission", b"EXOEMIS1",
            "diffuse H-alpha, [N II] and [S II] in narrowband, from Finkbeiner (2003)",
            build_halpha, default=True, packages=("numpy", "astropy", "astropy-healpix"),
            download_bytes=50_342_400),
    Product("galaxies", "GalaxyCatalog.galcat", b"EXOGALX1",
            "galaxies drawn from their measured shape and distance, from HyperLEDA",
            build_galaxies, default=True, min_version=2, packages=("requests",)),
    Product("patches", "HalphaPatches.patchset", b"EXOPTCH3",
            "high-resolution H-alpha, [O III] and [S II] patches, from SHASSA in the south and "
            "NSNS in the north (about 550 MB downloaded)",
            build_patches, default=False, needs=("halpha",),
            packages=("numpy", "scipy", "astropy", "astropy-healpix", "requests"),
            download_bytes=550_149_120),
    Product("images", "GalaxyImages.galimg", b"EXOGIMG1",
            "real survey imagery for the brightest galaxies (hours, gigabytes fetched)",
            build_images, default=False, needs=("galaxies",),
            packages=("numpy", "scipy", "astropy", "requests")),
]

BY_KEY = {p.key: p for p in PRODUCTS}


def install(product, ctx):
    """Moves a freshly built product into PluginData, refusing anything whose magic is wrong."""
    built = ctx.work / product.filename
    if not built.exists():
        die(f"{product.key}: the packer reported success but {built} does not exist.")
    with open(built, "rb") as handle:
        head = handle.read(len(product.magic))
    if head != product.magic:
        die(f"{product.key}: {built} does not start with {product.magic.decode()}, so it is "
            "truncated or is not the format it claims to be. It is not being installed.")
    if product.validate:
        fault = product.validate(built)
        if fault:
            die(f"{product.key}: {fault} It is not being installed.")
    target = ctx.plugin_data / product.filename
    # A linked catalogue, such as an all-sky file kept outside the install, is replaced as a link: copying onto
    # the link would overwrite the file it points at.
    if target.is_symlink():
        log(f"{target} is a link to {os.readlink(target)}; replacing the link, not the file it points at.")
    # Copied into staging and renamed over the target, so the game never reads half a file.
    try:
        install_local_file(built, ctx.plugin_data, product.filename)
    except SkyDataError as error:
        die(f"{product.key}: {error}")
    log(f"Installed {target} ({target.stat().st_size / 1e6:.1f} MB)")


# ---------------------------------------------------------------------------


class Context:
    pass


def parse_selection(args):
    """Turns --only / --with / --skip into the ordered list of products to build.

    Order comes from PRODUCTS, not from the command line, because patches and images are built
    from other products and have to follow them.
    """
    if args.only:
        chosen = {k.strip() for k in args.only.split(",") if k.strip()}
        unknown = chosen - set(BY_KEY)
        if unknown:
            die(f"Unknown product(s): {', '.join(sorted(unknown))}. Known: {', '.join(BY_KEY)}")
    else:
        chosen = {p.key for p in PRODUCTS if p.default}
        extra = {k.strip() for k in (args.with_ or "").split(",") if k.strip()}
        if "all" in extra:
            chosen = set(BY_KEY)
            extra.discard("all")
        unknown = extra - set(BY_KEY)
        if unknown:
            die(f"Unknown product(s) in --with: {', '.join(sorted(unknown))}. "
                f"Known: {', '.join(BY_KEY)}, or 'all'")
        chosen |= extra
    chosen -= {k.strip() for k in (args.skip or "").split(",") if k.strip()}
    return [p for p in PRODUCTS if p.key in chosen]


def main(argv=None):
    """Runs the build. argv defaults to sys.argv[1:]. Returns 0; every failure raises SystemExit."""
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ksp", help="KSP install directory (default: autodetected, or $KSP)")
    parser.add_argument("--with", dest="with_", default="",
                        help="also build these off-by-default products, comma separated, or "
                             "'all': " + ",".join(p.key for p in PRODUCTS if not p.default))
    parser.add_argument("--skip", default="", help="skip these products, comma separated")
    parser.add_argument("--only", help="build exactly these products, comma separated")
    parser.add_argument("--force", action="store_true",
                        help="rebuild even where the file is already installed")
    parser.add_argument("--stars", choices=("release", "esa"), default="release",
                        help="where the star field comes from: release downloads "
                             f"{DEFAULT_NAME} (78 MB, stars to V 13); esa builds "
                             "GaiaStarCatalog.starcat from the ESA archive to --gmax, with a free "
                             "ESA account (default: %(default)s)")
    parser.add_argument("--gaia-user", default=os.environ.get("GAIA_USER"),
                        help="ESA archive username for --stars esa (free: "
                             "https://cosmos.esa.int/web/gaia-users/register)")
    parser.add_argument("--gmax", type=float, default=13.0,
                        help="Gaia faint limit for --stars esa. Measured counts and file sizes, which cost disk "
                             "rather than memory: 12 is 3.1 M stars and 43 MB, 13 is 7.4 M and "
                             "103 MB, 14 is 16.8 M and 236 MB, 15 is 36.9 M and 517 MB "
                             "(default: %(default)s)")
    parser.add_argument("--bmax", type=float, default=15.0,
                        help="galaxy catalogue depth in B (default: %(default)s)")
    parser.add_argument("--image-bmax", type=float, default=11.0,
                        help="galaxy imagery depth in B; each step fainter is many more cutouts "
                             "to fetch (default: %(default)s)")
    parser.add_argument("--yes", action="store_true",
                        help="never prompt: replace star files without asking, and with --stars esa "
                             "skip the star field if no ESA username was supplied")
    args = parser.parse_args(argv)

    selected = parse_selection(args)
    if not selected:
        die("Nothing selected.")

    ctx = Context()
    try:
        ksp = resolve_ksp(args.ksp, log)
        ctx.plugin_data = plugin_data_dir(ksp)
    except SkyDataError as error:
        die(str(error))
    ctx.work = resolve_work_dir(ksp)
    ctx.gmax = args.gmax
    ctx.bmax = args.bmax
    ctx.image_bmax = args.image_bmax
    ctx.gaia_user = args.gaia_user

    # Everything already present is dropped here rather than inside the build loop, so the plan
    # printed below is the plan that actually runs.
    todo = []
    for product in selected:
        if product.key == "stars" and args.stars == "release":
            # A main file or a release tier already gives a sky, and needs no network to tell.
            installed = None if args.force else stars_installed(ctx.plugin_data)
            if not installed:
                todo.append(product)
                continue
            fault = product.validate(ctx.plugin_data / installed)
            if fault:
                log(f"{installed}: {fault}")
            log(f"Already installed, skipping: stars ({installed}).")
            continue
        target = ctx.plugin_data / product.filename
        if target.exists() and not args.force:
            # A file can be present, valid and still OUTDATED: the v1 galaxy catalogue predates
            # the distance-modulus column the supernova model needs. Version-aware products say
            # what the minimum acceptable on-disk version is, and an older file rebuilds.
            if product.min_version is not None:
                with open(target, "rb") as fh:
                    fh.read(len(product.magic))
                    version = int.from_bytes(fh.read(4), "little")
                if version < product.min_version:
                    log(f"{product.filename} is format v{version}, needs v{product.min_version} "
                        f"(the distance column); rebuilding.")
                    todo.append(product)
                    continue
            # A file can also be present, current and UNUSABLE. This is where that gets caught,
            # rather than only on the way in: someone who already installed a bad build would
            # otherwise be told "already installed" on every future run while the sky stayed
            # empty, which is the one outcome this script must not produce.
            if product.validate:
                fault = product.validate(target)
                if fault:
                    log(f"{product.filename}: {fault} Rebuilding.")
                    todo.append(product)
                    continue
            log(f"Already installed, skipping: {product.filename} "
                f"({target.stat().st_size / 1e6:.1f} MB). Use --force to rebuild.")
            continue
        todo.append(product)

    if not todo:
        log("Everything selected is already installed. Nothing to do.")
        return 0

    # A product built from another product needs that other one present, whether it was just built
    # or was already installed by a previous run.
    for product in todo:
        for need in product.needs:
            required = ctx.plugin_data / BY_KEY[need].filename
            if not required.exists() and BY_KEY[need] not in todo:
                die(f"{product.key} is built from {need}, which is neither installed nor "
                    f"selected. Add it: --with {need}")

    def release_stars(product):
        return product.key == "stars" and args.stars == "release"

    log("Will build: " + ", ".join(
        f"{p.key} ({DEFAULT_NAME} from the sky data release)" if release_stars(p)
        else f"{p.key} ({p.filename})" for p in todo))

    if args.stars == "esa" and any(p.key == "stars" for p in todo) and not ctx.gaia_user:
        if args.yes or not sys.stdin.isatty():
            log("No ESA username given, so the star field is skipped. Register free at "
                "https://cosmos.esa.int/web/gaia-users/register and rerun with --gaia-user.")
            todo = [p for p in todo if p.key != "stars"]
        else:
            print("\nThe star field needs a free ESA archive account (anonymous access hits a\n"
                  "job wall that no retry gets past). Register at\n"
                  "  https://cosmos.esa.int/web/gaia-users/register\n"
                  "Leave this blank to skip the star field and build everything else.\n")
            try:
                ctx.gaia_user = input("ESA archive username: ").strip() or None
            except (EOFError, KeyboardInterrupt):
                # Ctrl-D or Ctrl-C at the prompt means "not now", not "crash".
                print()
                ctx.gaia_user = None
            if not ctx.gaia_user:
                log("No username given, so the star field is skipped.")
                todo = [p for p in todo if p.key != "stars"]

    # Asked for up front rather than left to the packer's own prompt, so that a run started and
    # walked away from is not found hours later still blocked on a password prompt. Held in memory
    # for the one child that needs it, never written anywhere.
    ctx.gaia_password = os.environ.get("GAIA_PASSWORD")
    if args.stars == "esa" and ctx.gaia_user and not ctx.gaia_password and sys.stdin.isatty():
        try:
            ctx.gaia_password = getpass.getpass(
                f"ESA archive password for {ctx.gaia_user} (not echoed): ")
        except (EOFError, KeyboardInterrupt):
            print()
            die("No password given, so the ESA archive cannot be queried. "
                "Rerun, or set GAIA_PASSWORD.")

    if not todo:
        log("Nothing left to build.")
        return 0

    # Only what this run's packers import, so building halpha and patches never needs healpy.
    packages = pip_packages(todo)
    ctx.python = None
    if packages:
        try:
            ctx.python = ensure_venv(ctx.work, packages)
        except subprocess.CalledProcessError as error:
            die(f"Could not install the Python packages ({', '.join(packages)}): exit "
                f"{error.returncode}, for the reason printed above. Rerun once that is fixed.")
    log(f"Working in {ctx.work}")

    built, failed = [], []
    for product in todo:
        log(f"=== {product.key}: {product.summary}")
        if release_stars(product):
            # The release's V13 tier, checked against the pinned manifest; no ESA account.
            try:
                code = install_variant("default", ctx.plugin_data, assume_yes=args.yes, log=log)
            except KeyboardInterrupt:
                # Ctrl-C stops the whole run, not just the download.
                print()
                log("Stopped. Rerun to resume.")
                sys.exit(1)
            if code == 0:
                built.append(product.key)
            else:
                log("FAILED: stars. Rerun to resume, or build them with --stars esa.")
                failed.append(product.key)
            continue
        try:
            product.builder(ctx)
            install(product, ctx)
            built.append(product.key)
        except subprocess.CalledProcessError as error:
            # One product failing must not cost the others. A packer that dies halfway leaves its
            # own resumable cache behind, so rerunning picks that product up where it stopped.
            log(f"FAILED: {product.key} (exit {error.returncode}). Continuing with the rest.")
            failed.append(product.key)
        except (OSError, http.client.HTTPException, ValueError) as error:
            # An archive that cannot be reached, or cuts a download short, costs its own product only.
            log(f"FAILED: {product.key} ({error}). Continuing with the rest.")
            failed.append(product.key)

    print()
    log(f"Built and installed: {', '.join(built) if built else 'nothing'}")
    if failed:
        log(f"Failed (rerun to resume): {', '.join(failed)}")
    log(f"PluginData is now {ctx.plugin_data}")
    if SHIPPED_IN_GAMEDATA:
        # Only ever say this about the scratch directory we made ourselves. Run from a clone the
        # work directory IS tools/, and telling anyone to delete that would be telling them to
        # delete the packers.
        log(f"The build directory ({ctx.work}) can be deleted once you are happy with the result.")
    log("Start KSP and check the log: every file that loaded says so, with its provenance.")
    if failed:
        sys.exit(1)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        # main lets Ctrl-C through so an in-process caller stops too.
        print()
        log("Stopped. Rerun to resume.")
        sys.exit(1)
