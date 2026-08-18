# rv-tests

The radial-velocity semi-amplitude, checked against the catalogue's own published `k` column.

## Why

Every other check on the RV chain compares the mod against itself. The detector recovers the
period the simulator injected; the amplitude comes back out because it was put in. A wrong K
passes all of them, because K is both the question and the answer.

The catalogue publishes `k`: somebody's fitted semi-amplitude, with its error bar, measured on
the same star the mod is simulating. It is the one independent number available, and until this
harness existed nothing read it. The loader dropped the column.

It found a real bug on the first comparison. `StarTarget.EstimatedRvSemiAmplitudeMps` computes
the standard mass function, whose mass term is `Mp*sin(i)`, but the loader stored
`mass ?? mass_sini` in a single field and preferred the **true** mass wherever the catalogue had
one. A true mass read as a minimum mass inflates K by `1/sin(i)`.

```
51 Peg b     published 55.77 +/- 0.15 m/s
             from mass_sini = 0.46 Mjup     56.66 m/s    1.6% off
             from mass      = 0.61 Mjup     75.13 m/s   34.7% off
```

`RvSimulator` feeds that amplitude into every generated measurement, so it was never a display
problem: the simulated data itself carried the wrong reflex.

## What it checks

| section | what must hold |
|---|---|
| 1. Columns | `mass`, `mass_sini`, `k` and its error bar all survive the load, and no published K in the file fails to reach a target |
| 2. 51 Peg b | the shipped formula lands within 3% of the published K; the true mass lands 35% away |
| 3. The catalogue | on the 208 entries carrying both masses and a published K, median error 2.7%; on the 78 where the two masses differ by more than 10%, 3.8% against 44.4%, and grossly wrong (>50%) on 9 entries against 32 |
| 4. The projection | where only a true mass exists, `mass*sin(i)` reproduces the catalogue's own `mass_sini` to a median 0.27%, lowers the median error against the published K from 1.47% to 1.07%, and is refused on a malformed inclination (Wolf 503 b carries `i = -2`) |
| 5. The injected data | a simulated 120-day HARPS campaign on 51 Peg, fitted back by the mod's own detector, returns K = 56.6 m/s against the published 55.8; the uncorrected signal returns 75.0 and no analysis could have recovered the literature value from it |
| 6. Scope | a target whose catalogue row says nothing about geometry is bit-identical to before the fix, and the 755 projected targets within 2.6 deg of edge-on all move by less than 1% |

Section 4's precedence is also pinned on constructed targets, so all four branches of
`RvMinimumMassJupiter` stay covered even if no shipped entry exercises one.

## What this does NOT establish

- **Not the catalogue.** `k`, `mass` and `mass_sini` are exoplanet.eu's, each from its own paper,
  and the file contradicts itself in places: 3 entries pair an inclination near 90 with a
  `mass_sini` well below the true mass. The two of those carrying a published K side with
  `mass_sini` (HIP 56640 b: 52.5 m/s predicted against 53.4 published, the true mass gives 96.8),
  which is why the measured minimum mass wins even where it looks wrong.
- **Not the detector.** Section 5 uses `RvDetector` to fit the injected signal back, but the
  known single-harmonic bias on eccentric orbits is section 3.2 of `TECHNICAL_REFERENCE.md`, not
  something measured here. 51 Peg b has e = 0.007.
- **Not the noise model.** Instrument precision and stellar jitter set the error bar on the
  recovered K, and neither is validated by comparing an amplitude against a catalogue.

## Results

```
5507 targets loaded, 2212 rows carry a published K
ALL 34 CHECKS PASSED
```

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
```

Uses the repo's own `PluginData`. Pass an installed `PluginData` directory as the first argument
to run against a different catalogue export. Exit code 0 when every check passes.
