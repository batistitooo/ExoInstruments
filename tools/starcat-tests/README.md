# starcat-tests

The rendered star catalogue at the depth of all of Gaia DR3: 1,806,254,432 stars, 25.3 GB.

## Why

Every other harness that touches the star catalogue was written against files of a few hundred
megabytes, which the reader used to load whole into memory. The all-sky file cannot be loaded, so
`Core/RenderedStarCatalog.cs` maps it, reads records in blocks, and hands stars out one at a time;
`Core/TieredStarCatalog.cs` searches the magnitude tiers `tools/tier_star_catalog.py` cuts beside it.
Four things could go wrong silently, and each is a section:

1. **The mapped reader misses or invents a star.** A cone search that loses stars at a band edge or
   across 0h renders a plausible, thinner sky.
2. **A tier is not the main file.** A tier that drops or adds a star changes what a frame contains
   depending only on which tier served it.
3. **The tiers alone pass for the full depth.** `tools/get_sky_data_compact.py` installs the V13 to
   V19 tiers and no main file. The deepest tier is then the base, and a frame asking for V 22 must
   come back at V 19 saying the catalogue stopped it, not quietly shallower.
4. **A frame costs minutes.** A search reads every record inside the field whatever its magnitude
   limit, so a wide field on the all-sky file reads hundreds of millions of them.

The harness compiles the shipped `Core` sources directly (the same list as `bandpass-wcs-tests`), so
there is no test copy to drift.

## Run

```bash
cd tools/starcat-tests
dotnet build -c Release
dotnet bin/Release/net10.0/starcat.dll          # quick
mono bin/Release/net472/starcat.exe             # the runtime KSP uses, for the timings
```

It reads `EXOINSTRUMENTS_STARCAT`, or the catalogue installed in a Steam KSP. With none installed
only the committed fixture and the synthetic catalogue of section 3 are checked.

## What it checks

| section | what must hold |
|---|---|
| 1. Reader | on the committed fixture and on 21 cones of the installed file (both poles, both sides of 0h, the Galactic centre, 16 random), at three magnitude limits: the search returns exactly the stars a plain sequential read of the same declination bands finds, bit for bit in position, magnitude, colour and reddening; the streaming and list overloads agree; the candidate count is never below the result |
| 2. Tiers | every tier beside the catalogue loads and passes its own checks; 12 random cones per tier, at the cut and 1.3 mag brighter, return exactly the main file's stars; a RedCat 51 field at the Galactic centre is exact through the V13 tier; a search is served by the shallowest complete file; a BRITE field past the budget steps down whole to the deepest tier that fits |
| 3. Tiers without the main file | run twice: on a synthetic all-sky catalogue of 400,000 stars written to a temporary directory, which needs no data, and on a temporary directory of links named `GaiaStarCatalog.V13.starcat` to `GaiaStarCatalog.V19.starcat` pointing at the installed tiers (a SKIP line when any is absent). Either way: the V19 tier is the base, with tiers at V 13, 15, 17, `DepthVMag` 19 and none refused, while the main file declares no depth; V 16 is served by the V17 tier with exactly the main file's stars at V 16, on ten wide cones of the synthetic sky (1 to 8 degrees: the Galactic centre, both poles, 0h and six random) and on eleven of the installed data (the five fixed cones of section 1 and six random ones of 0.1 to 0.4 degrees); V 22 comes back at V 19 from the base with `LimitedByCatalogue`, holding the main file's stars to V 19; with the main file V 22 is unchanged; over two fields, three cone sizes and five limits under the budget, every plan steps down to the same tier as with the main file. The synthetic run also checks that a deepest tier that does not load leaves the next as the base; that an empty directory throws `FileNotFoundException`; that a main file linking to a file that has moved counts as missing, so the tiers stand in, and alone throws `FileNotFoundException` with the reason in `Report`; and that the base is the tier the others agree with: a stray V21 file holding the V 13 stars is refused, as is a V17 file holding them, and between two files that disagree with nothing else the deeper is kept. On the installed data, a link to `GaiaStarCatalog-G13.starcat` as the main file shows why the download scripts remove a foreign main: it refuses the V15, V17 and V19 tiers |
| 4. Cost | searches for three field sizes, two fields and four limits stay inside the 10 million record budget; streaming deposit with real photometry for three frames at the Galactic centre, one of them unguided with its budget cut by its trails |

## Results

Apple M5, the all-sky file with V13, V15, V17 and V19 tiers. All checks pass on Mono 6.12 and on
.NET 10. Timings are Mono's, since that is what KSP runs.

```
What a frame's star field costs, budget 10,000,000 records
        Galactic centre RC20       V<22.0  GaiaStarCatalog.starcat        to V 22.0                32,383 read       27,245 kept        2 ms
        Galactic centre RedCat 51  V<13.0  GaiaStarCatalog.V13.starcat    to V 13.0                12,691 read       12,487 kept        8 ms
        Galactic centre RedCat 51  V<22.0  GaiaStarCatalog.V19.starcat    to V 19.0 (budget)    3,485,629 read    3,428,405 kept      190 ms
        Galactic centre BRITE      V<22.0  GaiaStarCatalog.V15.starcat    to V 15.0 (budget)    2,235,008 read    2,229,533 kept      124 ms
        M51             BRITE      V<22.0  GaiaStarCatalog.starcat        to V 22.0             3,033,893 read    2,895,666 kept      173 ms
  PASS  no field reads past the budget once tiers are installed
        BRITE in orbit, 1 s, Galactic centre: 0 px trails, budget 10,000,000, GaiaStarCatalog.V15.starcat to V 15.0, 2,229,533 stars streamed, 1,700,310 deposited, 3.3 s (0.68 M stars/s)
        RedCat 51 guided, 300 s, Galactic centre: 0 px trails, budget 10,000,000, GaiaStarCatalog.V19.starcat to V 19.0, 3,428,405 stars streamed, 1,692,331 deposited, 3.8 s (0.91 M stars/s)
        RedCat 51 unguided, 60 s, Galactic centre: 199 px trails, budget 912,907, GaiaStarCatalog.V17.starcat to V 17.0, 687,968 stars streamed, 313,543 deposited, 5.9 s (0.12 M stars/s)
```

`dotnet bin/Release/net10.0/starcat.dll --cost` or `mono bin/Release/net472/starcat.exe --cost` runs
only this section.

### How it got there

Searching, each step measured under Mono on the same machine:

| | RedCat 51 at the Galactic centre, V 13 | BRITE at the Galactic centre, V 13 |
|---|---|---|
| read into arrays, as before | would need 25 GB of memory | would need 25 GB of memory |
| mapped, decoded field by field | 1.5 to 4 s | 42 to 52 s, 455,585,339 records |
| mapped, decoded in blocks | 90 ms | 16 s |
| with tiers | 8 ms, 12,691 records | 51 ms, 307,686 records |

Drawing. Streaming stars into the image first ran at 0.3 M stars/s under Mono: 59 s for the 18 M stars
of a RedCat 51 frame to V 22. A profile by stage put 4.1 µs of each star's 4.4 µs in the photometry,
where the one star in seven that has a reddening estimate but no usable temperature ran a spectral
quadrature of its own. `ReddenedResponseCache` now integrates that once per reddening bin. The
electrons of all 3.4 M stars in the field hash identically before and after on each runtime, and
`tools/reddening-tests` writes byte-identical files.

| per star, Mono 6.12, RedCat 51 field at V 19 | before | after |
|---|---|---|
| photometry | 4,120 ns | 99 ns |
| DepositStar, tracked | 4,400 ns | 490 ns |
| DepositStar, 0.2 degree trail | 6,150 ns | 2,000 ns |

The projection is 310 ns throughout. The budget then came down from 20 M to 10 M records, and is
divided by `StarFieldRenderer.RelativeDepositCost` for a trailed frame, which took the unguided frame
above from 32 s to 5.9 s.
