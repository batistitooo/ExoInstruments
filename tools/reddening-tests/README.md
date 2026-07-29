# reddening-tests

What a star's **observed** colour actually means, and what the pipeline got wrong by taking it at
face value.

## The error

A catalogue colour is an observed colour. `pack_gaia_catalog.py` reads Gaia's measured `G` and
`BP−RP`, and `GaiaPhotometry` converts them with Gaia's own published relations — **nothing is
dereddened anywhere**. So a hot star behind two magnitudes of dust and an intrinsically cool star
arrive at the photometry indistinguishable, and `StellarPhotometry.CollectedElectrons` modelled both
as the cool one: it fed the observed `B−V` to Ballesteros' relation and integrated a Planck curve at
a temperature the star does not have.

Those two objects have the same `B−V` by construction and genuinely different spectra. One is a
smooth Planck curve peaking in the red; the other is a steeply blue Planck curve with the dust curve
carved out of it. Across a wide band they integrate to different effective widths, so the electron
count is wrong by whatever that difference is.

## What it costs

Measured on rows where the observed colour stays inside Ballesteros' published `−0.5 < B−V < 2.5`,
so the comparison is between two real temperatures rather than against a fallback:

| instrument | band | median error | worst error |
|---|---|---|---|
| RC20 | 2600 Å Luminance | 7.6 mmag | 166 mmag |
| FORS2 | 7700 Å unfiltered | **56.5 mmag** | **807 mmag** |

An 8000 K star at `E(B−V) = 1`: **−29 mmag on the RC20, −295 mmag on FORS2**. The wider band suffers
ten times more, which is the check that these are physics and not noise — a *shape* error needs
band to act on, and FORS2's unfiltered position is three times wider.

Beyond `E(B−V) ≈ 2` the observed colour leaves Ballesteros' range entirely, the old path falls back
to a flat spectrum, and the error runs to several magnitudes. Those rows are excluded from the table
above because they measure a fallback rather than a temperature.

## Why this is not double counting

Stated as a testable claim rather than an argument. The bandpass integrand is **normalised at
Johnson V**, so the observed magnitude sets the flux and the integrand carries only a shape. The
extinction factor is written normalised at V too:

```
10^(-0.4 R_V E(B-V) [k(lambda) - k(V)])
```

which is exactly 1 at V by construction. Nothing is attenuated — the observed magnitude already
contains the dimming — and this only stops the reddening from being mistaken for a cool photosphere.

| check | result |
|---|---|
| the factor at Johnson V, over 8 reddenings × 2 instruments | **exactly 1** (0.0, not a tolerance) |
| with `E(B−V) = 0`, ratio to the old path over 6 temperatures | 2.2×10⁻¹⁶ |
| the factor itself, vs `dust_extinction` F99, 402 samples | 7.4×10⁻⁵ relative |

The second row is the one that matters for safety: **with no reddening estimate the result is
bit-identical to what the pipeline produced before**, so a catalogue carrying no reddening column
behaves exactly as it always did.

At `E(B−V) = 2`, 400 nm keeps 0.073 of its V-relative flux and 800 nm keeps 10.5 — suppressed in the
blue, enhanced in the red, both relative to V. That is reddening, and its direction is checked
rather than assumed.

## The per-frame cache

`SystemResponse.EffectiveWidthAngstromForReddenedStar` runs a quadrature per call, because putting a
reddening axis on the colour table would multiply its build cost by the number of nodes on that axis
— 48 ms per capture on the main thread against under a millisecond today. `ReddenedResponseCache`
shares the quadratures across a frame instead: one sight line, so its stars sit behind much the same
dust.

**Both axes had to be interpolated, and the harness is how that was found.** Rounding the
temperature to its bin costs 3.3 % in effective width; rounding the reddening costs 0.6 %. Each is
as large as or larger than the error the whole path exists to remove. Interpolating both leaves
**7.1×10⁻⁵**.

A realistic field — 400 stars over 3000–30000 K at `E(B−V) = 0.50 ± 0.03` — needs **11 reddening
tables and 5885 quadratures**, after which every further star is two interpolations.

## What this does NOT establish

- **No reddening estimate is wired in yet.** The path exists and is inert without one:
  `RenderedStar` carries no `E(B−V)` column, so nothing in the game takes it today. Gaia DR3's own
  astrophysical-parameters pipeline publishes one per source, which is where it should come from;
  a sight-line dust map is the fallback for a catalogue that carries none.
- **One extinction law, one R_V.** F99 at R_V = 3.1, which is what every all-sky map is calibrated
  to. `InterstellarExtinction` carries the R_V axis; nothing varies it per sight line.
- **Blackbodies, not stellar atmospheres.** Ballesteros' relation and a Planck curve, as before.
  Dereddening improves *which* blackbody, not the fact that it is one.
- **Ballesteros' range is not extended.** A dereddened colour outside `−0.5 < B−V < 2.5` returns
  "unknown" and the flat-spectrum fallback applies, which is the honest behaviour: an
  over-corrected colour means the reddening estimate is wrong, not that a temperature should be
  invented.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
python -m venv env && ./env/bin/pip install numpy scipy astropy dust_extinction
./env/bin/python compare_reddening.py
```

Exit code 0 when every check passes. Verified against dust_extinction 1.5.
