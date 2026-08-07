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

    stars      GaiaStarCatalog.starcat   Gaia DR3, via the ESA archive
    dust       DustMap.dustmap           SFD98, via the dustmaps package
    halpha     HalphaMap.emission        Finkbeiner (2003), via NASA LAMBDA
    galaxies   GalaxyCatalog.galcat      HyperLEDA
    patches    HalphaPatches.patchset    SHASSA, calibrated against halpha
    images     GalaxyImages.galimg       survey cutouts, driven by galaxies

WHAT IS OFF BY DEFAULT. `patches` and `images` are opt-in (--with patches,images or --with all).
Not because they are worth less, but because they cost a different order of magnitude: patches
downloads about 2.3 GB of SHASSA fields, and images fetches survey cutouts for every galaxy in the
catalogue and runs for hours. The other four are a coffee break, except the star field, which is a
long download that resumes if interrupted.

IDEMPOTENT ON PURPOSE. Anything already installed is left alone, so rerunning after an interruption
picks up where it stopped rather than redoing hours of work. --force rebuilds regardless, and
--only <keys> restricts the run to the products you name.

THE ESA ACCOUNT. The star field is the one step that needs a login, because anonymous access to the
Gaia archive hits a job wall that no amount of retrying gets past. Registration is free at
https://cosmos.esa.int/web/gaia-users/register. Pass --gaia-user, set GAIA_USER, or answer the
prompt; the password is prompted for or read from GAIA_PASSWORD and is never taken on the command
line. With no username at all the star field is skipped and everything else still runs.

EVERY PRODUCT IS CHECKED BEFORE IT IS INSTALLED. Each packer prints its own named sanity checks as
it runs (M31 must come out 3.2 degrees across at B_T 4.4, and so on), and on top of that this
script refuses to install a file that does not begin with the magic number its format is supposed
to have. A truncated download or a half-written file is caught here rather than in the game.
"""

import argparse
import getpass
import hashlib
import os
import platform
import shutil
import subprocess
import sys
import urllib.request
from pathlib import Path

TOOLS = Path(__file__).resolve().parent

# Whether this is the copy shipped inside the installed mod rather than one in a clone of the
# repository. It decides both where KSP is (no guessing needed) and where the build work goes.
SHIPPED_IN_GAMEDATA = (TOOLS.parent.name == "ExoInstruments"
                       and TOOLS.parent.parent.name == "GameData")

# The one source file no archive will hand over programmatically on stable terms. LAMBDA's URLs
# have moved before, and the nside 512 map sitting next to it in the same directory is a
# plausible-looking wrong answer, so this download is pinned by digest rather than trusted by name.
# See pack_halpha_map.py for why the 1024 map specifically.
HALPHA_URL = "https://lambda.gsfc.nasa.gov/data/foregrounds/fink_halpha/Halpha_fwhm06_1024.fits"
HALPHA_SHA256 = "8daaf304acc1c320096a0c41667bc8a5ae272b4208d64e003b7d2c1ba9512936"

# Union of what the packers import. astropy-healpix is NOT healpy: pack_halpha_map.py and
# pack_shassa_patches.py import astropy_healpix while pack_dust_map.py imports healpy, and both
# have to be here. Installing only healpy is how the H-alpha step used to die on an ImportError.
PIP_PACKAGES = ["numpy", "scipy", "astropy", "astropy-healpix", "healpy", "requests", "dustmaps"]


def log(msg):
    print(f"[setup_data] {msg}", flush=True)


def die(msg):
    print(f"[setup_data] ERROR: {msg}", file=sys.stderr, flush=True)
    sys.exit(1)


# ---------------------------------------------------------------------------
# Finding KSP, and choosing where to work


def candidate_ksp_dirs():
    """Where to look for KSP, most likely first.

    The first candidate is not a guess at all: this script ships inside
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
    import re
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


def resolve_ksp(explicit):
    if explicit:
        path = Path(explicit).expanduser()
        if not looks_like_ksp(path):
            die(f"{path} does not contain a GameData directory, so it is not a KSP install.")
        return path
    env = os.environ.get("KSP")
    if env:
        path = Path(env).expanduser()
        if not looks_like_ksp(path):
            die(f"$KSP points at {path}, which has no GameData directory.")
        return path
    for path in candidate_ksp_dirs():
        if looks_like_ksp(path):
            log(f"Found KSP at {path}")
            return path
    die("Could not find a KSP install. Pass --ksp /path/to/Kerbal Space Program, or set $KSP.")


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


def plugin_data_dir(ksp):
    """Where every finished product lands."""
    mod = ksp / "GameData" / "ExoInstruments"
    if not mod.is_dir():
        die(f"ExoInstruments is not installed at {mod}. Install the mod first (CKAN, or unzip "
            "the release over the KSP folder), then rerun this.")
    target = mod / "PluginData"
    target.mkdir(parents=True, exist_ok=True)
    return target


# ---------------------------------------------------------------------------
# The virtualenv


def ensure_venv(work):
    """Builds the virtualenv once and installs the union of every packer's requirements into it.

    Deliberately a virtualenv rather than a --user install or the ambient interpreter: healpy and
    astropy pin versions against each other, and a player's system Python is not ours to touch.
    """
    # "setup_env", not "env": running out of a repository clone the work directory IS tools/,
    # where tools/env is a virtualenv checked into the repository for the older test harnesses.
    # Installing into that one would rewrite tracked files.
    venv = work / "setup_env"
    python = venv / ("Scripts/python.exe" if platform.system() == "Windows" else "bin/python")
    if not python.exists():
        log(f"Creating virtualenv at {venv}")
        venv.parent.mkdir(parents=True, exist_ok=True)
        subprocess.check_call([sys.executable, "-m", "venv", str(venv)])
        # Only in a virtualenv we just created, never in one that already existed. An older
        # interpreter bundles a pip too old to resolve current wheels, but upgrading somebody
        # else's environment is not this script's business.
        subprocess.check_call([str(python), "-m", "pip", "install", "--quiet", "--upgrade", "pip"])
    log("Installing Python packages (first run only, a few minutes)")
    subprocess.check_call([str(python), "-m", "pip", "install", "--quiet", *PIP_PACKAGES])
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


class Product:
    def __init__(self, key, filename, magic, summary, builder, default, needs=(), needs_venv=True):
        self.key = key
        self.filename = filename
        self.magic = magic
        self.summary = summary
        self.builder = builder
        self.default = default
        self.needs = needs
        self.needs_venv = needs_venv


PRODUCTS = [
    Product("stars", "GaiaStarCatalog.starcat", b"EXOSTAR1",
            "the star field behind every photograph, from Gaia DR3",
            build_gaia, default=True, needs_venv=False),
    Product("dust", "DustMap.dustmap", b"EXODUST1",
            "interstellar reddening and the extinction readout, from SFD98",
            build_dust, default=True),
    Product("halpha", "HalphaMap.emission", b"EXOEMIS1",
            "diffuse H-alpha, [N II] and [S II] in narrowband, from Finkbeiner (2003)",
            build_halpha, default=True),
    Product("galaxies", "GalaxyCatalog.galcat", b"EXOGALX1",
            "galaxies drawn from their measured shape, from HyperLEDA",
            build_galaxies, default=True),
    Product("patches", "HalphaPatches.patchset", b"EXOPTCH1",
            "high-resolution H-alpha patches, from SHASSA (about 2.3 GB downloaded)",
            build_patches, default=False, needs=("halpha",)),
    Product("images", "GalaxyImages.galimg", b"EXOGIMG1",
            "real survey imagery for the brightest galaxies (hours, gigabytes fetched)",
            build_images, default=False, needs=("galaxies",)),
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
    target = ctx.plugin_data / product.filename
    shutil.copy2(built, target)
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


def main():
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
    parser.add_argument("--gaia-user", default=os.environ.get("GAIA_USER"),
                        help="ESA archive username (free: "
                             "https://cosmos.esa.int/web/gaia-users/register)")
    parser.add_argument("--gmax", type=float, default=13.0,
                        help="Gaia faint limit. Measured counts, which are also the RAM cost "
                             "while playing: 12 is 3.1 M stars and 43 MB, 13 is 7.4 M and "
                             "103 MB, 14 is 16.8 M and 236 MB, 15 is 36.9 M and 517 MB "
                             "(default: %(default)s)")
    parser.add_argument("--bmax", type=float, default=15.0,
                        help="galaxy catalogue depth in B (default: %(default)s)")
    parser.add_argument("--image-bmax", type=float, default=11.0,
                        help="galaxy imagery depth in B; each step fainter is many more cutouts "
                             "to fetch (default: %(default)s)")
    parser.add_argument("--yes", action="store_true",
                        help="never prompt; skip the star field if no ESA username was supplied")
    args = parser.parse_args()

    selected = parse_selection(args)
    if not selected:
        die("Nothing selected.")

    ctx = Context()
    ksp = resolve_ksp(args.ksp)
    ctx.plugin_data = plugin_data_dir(ksp)
    ctx.work = resolve_work_dir(ksp)
    ctx.gmax = args.gmax
    ctx.bmax = args.bmax
    ctx.image_bmax = args.image_bmax
    ctx.gaia_user = args.gaia_user

    # Everything already present is dropped here rather than inside the build loop, so the plan
    # printed below is the plan that actually runs.
    todo = []
    for product in selected:
        target = ctx.plugin_data / product.filename
        if target.exists() and not args.force:
            log(f"Already installed, skipping: {product.filename} "
                f"({target.stat().st_size / 1e6:.1f} MB). Use --force to rebuild.")
            continue
        todo.append(product)

    if not todo:
        log("Everything selected is already installed. Nothing to do.")
        return

    # A product built from another product needs that other one present, whether it was just built
    # or was already installed by a previous run.
    for product in todo:
        for need in product.needs:
            required = ctx.plugin_data / BY_KEY[need].filename
            if not required.exists() and BY_KEY[need] not in todo:
                die(f"{product.key} is built from {need}, which is neither installed nor "
                    f"selected. Add it: --with {need}")

    log("Will build: " + ", ".join(f"{p.key} ({p.filename})" for p in todo))

    if any(p.key == "stars" for p in todo) and not ctx.gaia_user:
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
    if ctx.gaia_user and not ctx.gaia_password and sys.stdin.isatty():
        try:
            ctx.gaia_password = getpass.getpass(
                f"ESA archive password for {ctx.gaia_user} (not echoed): ")
        except (EOFError, KeyboardInterrupt):
            print()
            die("No password given, so the ESA archive cannot be queried. "
                "Rerun, or set GAIA_PASSWORD.")

    if not todo:
        log("Nothing left to build.")
        return

    ctx.python = ensure_venv(ctx.work) if any(p.needs_venv for p in todo) else None
    log(f"Working in {ctx.work}")

    built, failed = [], []
    for product in todo:
        log(f"=== {product.key}: {product.summary}")
        try:
            product.builder(ctx)
            install(product, ctx)
            built.append(product.key)
        except subprocess.CalledProcessError as error:
            # One product failing must not cost the others. A packer that dies halfway leaves its
            # own resumable cache behind, so rerunning picks that product up where it stopped.
            log(f"FAILED: {product.key} (exit {error.returncode}). Continuing with the rest.")
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


if __name__ == "__main__":
    main()
