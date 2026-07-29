# emission-tests

Emission-line photometry: the physics a nebula needs, which is not the physics a star needs.

## Why it is a separate path

Everything else in this pipeline integrates a **continuum** — a Planck curve, a solar spectrum, a
flat one — and asks `SystemResponse` for an *effective width* across the band. A nebula in [S II] is
not a continuum. Its flux arrives in lines a fraction of an Ångström wide, so the quantity that
decides how much reaches the detector is the throughput **at** the line, not a width across a band.

That asymmetry is the entire reason narrowband imaging works, and no effective-width model can
express it: a 3 nm filter collects exactly the same line photons as a 30 nm one while admitting a
tenth of the sky behind it.

## What it establishes

**The unit algebra is astropy's, not this project's own.** The rayleigh is defined by astropy, so
the conversion is checked against it rather than against itself:

| check | result |
|---|---|
| photons/cm²/s/sr per rayleigh | **0** |
| steradians per square arcsec | 0 |
| 100 R on the RC20 at 0.2754″/px, whole expression | 0 |
| linear in surface brightness | 1.1×10⁻¹⁶ |
| erg/cm²/s → e⁻/s, all 12 lines, vs astropy's h and c | **0** |

**The line list has the separations that decide what a filter can resolve.** Air wavelengths, from
NIST ASD and reproduced throughout Osterbrock & Ferland (2006):

| pair | separation |
|---|---|
| [N II] 6584 to Hα | **20.65 Å** |
| [S II] 6731 − 6716 | 14.38 Å |
| [O III] 5007 − 4959 | 47.93 Å |
| [O II] 3729 − 3726 | 2.79 Å |

Air rather than vacuum because that is the convention every filter manufacturer and every narrowband
observer works in; mixing the two is a 1.7 Å error, half a nanometre-class filter's own tolerance.

**Narrowband, measured rather than asserted.** RC20, 100 R of Hα — the order a bright Galactic H II
region reaches in the WHAM survey — on the model's own dark sky, peak transmission held fixed so the
comparison is about bandwidth alone:

| width | T at line | line e⁻/px/s | sky e⁻/px/s | contrast | [N II] admitted |
|---|---|---|---|---|---|
| 260 nm | 0.6471 | 0.01590 | 0.4377 | 0.04 | yes |
| 30 nm | 0.6471 | 0.01590 | 0.0505 | 0.31 | yes |
| 7 nm | 0.6471 | 0.01590 | 0.0118 | 1.35 | yes |
| 5 nm | 0.6471 | 0.01590 | 0.0084 | 1.89 | yes |
| **3 nm** | 0.6471 | 0.01590 | 0.0051 | 3.15 | **no** |
| 1 nm | 0.6471 | 0.01590 | 0.0017 | **9.44** | no |

Three statements, each a measurement:

- **The line signal does not depend on filter width.** Spread across eight filters: 0.
- **The sky scales with the band's effective width.** Ratio's coefficient of variation: 1.6×10⁻¹⁶.
- **Contrast therefore improves exactly as the width shrinks.** 260 nm → 1 nm gives a **260-fold**
  gain, matching the width ratio to 7×10⁻¹⁴.

And the crossover that decides whether an "Hα" frame is really Hα: **[N II] 6584 is admitted at 5 nm
and excluded at 3 nm**, which is what a 20.65 Å separation requires. An Hα image taken through a
5 nm filter contains [N II]; through a 3 nm filter it does not. That is not a modelling choice, it is
the arithmetic of the separation, and it is why separating those two lines needs a filter narrower
than about 4 nm.

## What this does NOT establish

- **No line source exists yet.** The photometry is here; nothing in the game emits a line. A nebula
  needs either a surface-brightness map (the Finkbeiner 2003 Hα composite is the all-sky one, in
  rayleighs) or a modelled H II region.
- **No narrowband filter positions in the roster.** `CameraFilter` still carries L/R/G/B/Hα. The
  comparison above builds its filters directly, which is what let it hold peak transmission fixed;
  real ones have their own published transmissions.
- **Top-hat filters.** No measured narrowband curve is digitised here, so the filters are the
  published FWHM and peak transmission — the same treatment `FilterCurves` replaced for FORS2's
  broadband set, and the same thing that should happen to these.
- **No line ratios, no diagnostics.** [S II]/Hα and [O III]/Hβ are analysis of finished frames, not
  simulation, and nothing here computes them.
- **[O I] 6300 has an airglow problem this does not model as a line.** `SkyBrightnessModel` carries
  airglow as a broadband continuum; 6300 Å is one of its brightest discrete lines, so a real [O I]
  narrowband frame fights a sky that is bright in exactly that filter. Modelling that needs the
  airglow spectrum, not just its integrated brightness.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
python -m venv env && ./env/bin/pip install numpy astropy
./env/bin/python compare_emission.py
```

Exit code 0 when every check passes. Verified against astropy 6.0.1.
