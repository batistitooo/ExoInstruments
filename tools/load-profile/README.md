# load-profile, what the Space Center waits for

The first visit to the Space Center froze on the loading screen for about 80 s. The KSP.log of
2026-09-13 put 75 s of it in `ExoInstrumentsGUI.Start`, on the main thread: `LoadEmissionPatches`
took 60 s and `MergeWithBackgroundStars` 13 s.

This harness runs those loads the way `Start` does, on the installed data files. It times each
step and records everything they produce, so a faster version can be shown to produce **the same
bytes**.

## What it records

- **Patches:** every plane of every patch after `RejectOutliers` and again after
  `CalibrateAgainst`, plus `RejectedCells` and `CalibratedCells`.
- **Merge:** every counter, `MatchLog`, `AmbiguousNameKeys`, `UnmatchedBrightHosts`, the merged
  list in order with where each entry came from (object identity), and every `StarTarget`
  property, doubles as raw bits.

`--dump <dir>` writes these; `--compare <dir>` checks them byte for byte, prints the first
differing cells or lines, and exits 1. Compare within one runtime only: `merge.txt` legitimately
differs between runtimes (libm digits in derived properties, the current culture in `MatchLog`).

## What it found

Cold first run, 9 workers, nothing else running, the shipped data:

| runtime | RejectOutliers | CalibrateAgainst | StarCatalogMerger.Merge | peak memory |
|---|---:|---:|---:|---:|
| **Unity Mono 5.11, KSP's own** | 42739 → **1382** ms | 7938 → **761** ms | 12346 → **134** ms | 1.75 GB → **281 MB** |
| Mono 6.12 x86_64 | 37500 → 1368 ms | 3784 → 817 ms | 9141 → 158 ms | 1.67 GB → 240 MB |
| .NET 10 arm64 | 8036 → 789 ms | 819 → 379 ms | 857 → 49 ms | 2.65 GB → 402 MB |

Where the time went:

- **RejectOutliers** built, per patch, a dense pixel table over the patch's whole ring span (145 MB
  for the largest nside 8192 patch) and neighbourhood lists grown through a `List<int>`. It then
  ran six rounds that sorted up to 49 values twice for every cell, one patch per worker, so the two
  414k-cell patches finished alone.
- **CalibrateAgainst** ran two dictionary-based smoothings over the same 31x31 beam window.
- **Merge** spent 8.9 of its 9.1 s in the positional fallback, which compared each of 2176
  unmatched hosts with all 9096 BSC stars.

## What changed

The results are identical; only how they are computed differs.

- `EmissionPatchSet.RejectOutliers` repairs one patch at a time with its cells split across the
  workers. Neighbourhoods come from a run lookup, with the latitude terms computed once per ring
  (new internal helpers in `Healpix`). Medians come from a selection rather than a sort, and are
  skipped when the floor alone keeps the cell. After the first round only cells whose own value
  or a neighbour's changed are judged again.
- `CalibrateAgainst` numbers composite cells as array slots and runs both smoothings in one pass.
- `StarCatalogMerger` searches a declination band instead of the whole catalogue, in list order so
  a tie still goes to the later star, and normalises each host name once. `StarNames.Normalize`
  skips regexes that cannot match.
- Patches with run tables the packer never writes still take the original code. A failing patch no
  longer stops the others: the failures come out together, as they did from the old parallel loop.

## How exactness was checked

- The original code's outputs are matched byte for byte on all three runtimes, at 1 worker and at
  the default.
- Differential fuzzing compiled the original files next to the new ones and compared them on about
  20,000 synthetic patch sets, 900 million HEALPix lookups and over 120,000 merges. The inputs
  covered NaN, infinities, subnormals, poles, RA wrap, malformed run tables, 1 to 64 workers, and
  the tr-TR culture. The only differences found were in which exceptions a malformed file reports.
- Deliberately broken versions of the new code were caught by that fuzzing, so it is not blind.

## Running

.NET 10 is quickest for iterating; Mono is what KSP runs. Dump from the unchanged code first, then
compare after the change, one dump per runtime.

```
dotnet run -c Release -- --dump /tmp/golden-net
dotnet run -c Release -- --compare /tmp/golden-net

./build-mono.sh
mono bin/mono/loadprof.exe --compare /tmp/golden-mono
```

On KSP's own runtime, build against the game's mscorlib, then run through `unity.sh`, which embeds
the game's `libmonobdwgc` in a small native host (`unityhost.c`, built on first use):

```
KSP_MANAGED="$HOME/Library/Application Support/Steam/steamapps/common/Kerbal Space Program/KSP.app/Contents/Resources/Data/Managed" \
    ./build-mono.sh ../../ExoInstruments/Core bin/unity
./unity.sh bin/unity/loadprof.exe --compare /tmp/golden-unity
```

Options: `--only patches|merge`, `--workers N`, `--repeat N` (checks runs agree), `--data <dir>`
for a PluginData directory other than the installed one.
