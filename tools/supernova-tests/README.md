# supernova-tests

The supernova model against the papers it is built from: templates, rates, photometry, and the
deterministic occurrence process.

## Two modes beyond the checks

```
# what the model gives a player, on the shipped catalogue
EXO_GALCAT=<KSP>/GameData/ExoInstruments/PluginData/GalaxyCatalog.galcat \
    dotnet run -c Release -p:Core=../../ExoInstruments/Core -- --census

# every event one save will ever see, in order
EXO_GALCAT=... dotnet run -c Release -p:Core=../../ExoInstruments/Core -- \
    --forecast <supernovaSeed> --ut <current UT>
```

`--census` measures the balance (18 events/year sky-wide, 3.2 brighter than V 16 at any instant,
the best wide-field pointings) and is what caught the AGN rows of section 12 item 98. `--forecast`
reads a save's future without playing it: the seed is `supernovaSeed` in the `ExoInstrumentsScenario`
node of `saves/<name>/persistent.sfs`, written the first time the observatory panel opens. That is
how TESTING 26.2 gets checked without waiting years of game time for a random event.

## Run

```
dotnet run -c Release -p:Core=../../ExoInstruments/Core
```

Loads the shipped `PluginData/SupernovaTemplates.sntpl` (pass a path as the first argument to
test another build of it).

## What is checked, and against what

| Section | Checked against |
| --- | --- |
| 1 | The templates' own published light-curve properties: the stretch-1 Ia rise (19.5 d, Riess et al. 1999) and Delta m15 (1.1, Phillips 1993); deceleration onto the 56Co tail above the 2.5 log10(e)/111.3 d trapping floor; II-P vs II-L decline ratio (Patat et al. 1994 measure the classes apart in B); H-alpha in emission in the II-P and absent from the Ia (Filippenko 1997: that IS the classification). |
| 2 | Photometric identities: a narrow band at the 5556 A anchor must reproduce the flat width exactly (the shape is normalised there); a Fitzpatrick screen must return its own E(B-V) through the integral; the packed B and V tracks must agree with the Ia's near-zero B-V at maximum. |
| 3 | Li et al. 2011 Table 4, cell for cell, through the public API: fiducial rates times two at twice-fiducial luminosity, the rate-size exponents to 1e-6, the elliptical hosting Ia only, and the paper's own Milky Way estimate (2.84 +- 0.60 per century) from the published MW luminosity. |
| 4 | The process: identical seeds give identical histories; the empirical block mean matches the Poisson intensity; class shares match the rate shares; the drawn peak magnitudes reproduce Richardson et al. 2014's mean and dispersion. |
| 5 | Positions: deterministic, and distributed at the host's own catalogued scale. |
| 1 (suite) | The extrapolation past the template: continuous at the boundary, the template's own final slope held exactly, the spectrum frozen at the last epoch, a hard stop at the declared 12-mag floor. The linear-in-magnitude form is the radioactive tail's own (exponential decay); the reserves are section 12 item 95's. |

## The two checks that were wrong before the data was

The first version demanded a B-band plateau of the II-P and the late-tail slope of a mid-transition
Ia. Both failures were the TEST's: the plateau is a V/R phenomenon (in B a II-P declines, just far
slower than a II-L), and at +40..70 days past maximum an Ia is still decelerating onto the tail, so
its slope legitimately sits between the post-max rate and the 56Co floor. The checks now assert
what the sources actually say.
