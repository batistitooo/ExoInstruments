# search-tests

The target search box, exercised headless against the real installed catalogues.

## Why

A catalogue search box is the kind of component that looks like it works from the first minute and
is wrong in ways nobody notices. The four failure modes this directory exists to rule out:

1. **It cannot find an object under a name the object is known by.** Nobody types `NGC0224`. They
   type M31, or Andromeda. Nobody types `alf Lyr`. They type Vega.
2. **It finds the wrong object because a substring matched.** `NGC 24` is a substring of `NGC 247`,
   `NGC 2400` and two hundred others; the mod's previous name filter returned all of them.
3. **It silently duplicates an object two catalogues both carry.** HyperLEDA and SIMBAD both know
   NGC 4594. One of them measured its isophotal diameter; the other knows it is the Sombrero. The
   list must show one target with both.
4. **It leaks, in career mode, the identity of a star the fog of war is hiding.** The cross-
   identification and IAU name tables are added to the index *after* the stars, which is exactly the
   shape of a bug that hands a withheld identity straight back.

Each of those is a section below. The harness compiles the shipped `Core` sources directly — the
same files the mod builds — so there is no test copy to drift.

## What it checks

| section | what must hold |
|---|---|
| 1. Designation keys | five spellings of NGC 224 give one key; NGC 24, 240 and 2400 stay three objects; a suffix (`NGC 4038A`) is not its parent; `Sh2-155` survives its hyphen; `M104` is **not** read as prefix `M1` + number 4 |
| 2. Every name | 17 objects found under a name other than the one their catalogue stores, including M13 — a globular cluster no installed catalogue carries at all |
| 3. Substrings | the exact designation ranks first for `NGC 24`, `NGC 300`, `IC 10`, `M 1`, while the longer ones stay reachable |
| 4. Duplicates | M31, M51, M81, M87, M104 each resolve to exactly one target, and that target is the row with the **measurements** |
| 5. Filters | `type:` covers the right kinds and accepts plurals; a misspelt filter is reported, not ignored; `in:Ori`, `in:Orion` and `in:Orionis` agree; `mag:<9` excludes unknown magnitudes; filters AND together |
| 6. Ranking | word search finds objects named for the word; an unsearched list is ordered by brightness |
| 7. Career fog | no star findable by its real name while unscanned, including through the IAU name table; findable by its provisional designation; galaxies and nebulae never fogged |

## Results

```
5507 planet rows, 9096 BSC stars -> 14441 targets
1452 galaxies from HyperLEDA (Makarov et al. 2014, A&A 570, A13)
index: 16125 targets, built in 109 ms
query: 2.3 ms each

ALL 49 CHECKS PASSED
```

The two timings are the reason the index is built on a background Task and a query is not: 109 ms
is a visible freeze on the game's runtime, and 2.3 ms is a keystroke.

## What this does NOT establish

- **Not the cross-identifications themselves.** That M31 is NGC 224 comes from SIMBAD through
  `tools/generate_deepsky_crossids.py`, and that Vega is HR 7001 from the IAU's own catalogue of
  star names through `tools/generate_star_names.py`. This harness checks that the mod *uses* them,
  not that they are right; the authority for that is the source.
- **Not the constellations.** `in:Ori` is only as good as `Core/Constellations.cs`, which
  `tools/constellation-tests` cross-validates against astropy.
- **Nothing about the interface.** Whether the panel lays out, whether a click points the telescope,
  whether the sky chart highlights what the search matched — all of that needs the game, and lives
  in `TESTING.md`.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
```

That uses the repo's own `PluginData`, which carries the star catalogues but not the galaxy
catalogue (nothing ships one). The four galaxy-dependent checks report as skipped. To run the full
set, point it at an install that has one:

```
dotnet run -p:Core=../../ExoInstruments/Core -- "<KSP>/GameData/ExoInstruments/PluginData"
```

Exit code 0 when every check passes.
