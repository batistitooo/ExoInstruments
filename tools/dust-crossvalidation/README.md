# dust-crossvalidation

The extinction **law**, the first layer of the dust pipeline, and the one everything else multiplies
by. Cross-validated against `dust_extinction` 1.5, the astropy-affiliated reference implementation.

An extinction curve splits into an **amount** and a **shape**:

```
A(lambda) = A(V) k(lambda),    A(V) = R_V E(B-V),    k(lambda) = A(lambda)/A(V)
```

The amount, `E(B-V)`, is a property of the sight line and comes from a dust map. The shape is a
property of the grains, parameterised by `R_V = A(V)/E(B-V)`. This directory establishes the shape.

## Two laws, deliberately

| | |
|---|---|
| **CCM89**, Cardelli, Clayton & Mathis (1989), *ApJ* **345**, 245 | closed-form polynomials in `x = 1/lambda`. Exactly implementable, nothing to interpolate. Kept as the **control**. |
| **F99**, Fitzpatrick (1999), *PASP* **111**, 63 | better in the optical, where every instrument in the roster works. Not closed-form: a cubic spline through published anchor points, so it is carried as a generated table. **The default.** |

Reimplementing F99's spline construction and matching its knot placement would be a way to introduce
an error that looks like physics. Sampling the reference implementation on a grid finer than any
structure the curve contains is not an approximation of the law; it is the law, sampled. Same
treatment, and the same reason, as `FilterCurves` carrying ESO's measured transmissions rather than
assuming top-hats. `generate_f99_table.py` produces `Core/Fitzpatrick99Table.cs`; the harness then
checks that table against the same reference, so a drift between them is caught rather than assumed
away.

## Results

**CCM89 is exact.** Against the reference over 1199 wavenumbers × 6 values of `R_V`:

| R_V | 2.0 | 2.6 | 3.1 | 3.85 | 4.4 | 5.5 |
|---|---|---|---|---|---|---|
| max abs deviation | 2.2e-15 | 1.8e-15 | 1.6e-15 | 1.1e-15 | 1.1e-15 | 8.9e-16 |

Getting there found one real thing: CCM89's optical polynomials are published for `1.1 <= x < 3.3`,
and `x = 3.3` is where the paper hands over **to** the ultraviolet branch, not the last point of the
optical one. An inclusive bound there is a single point of disagreement with every other
implementation, worth 1.8e-4, and machine precision everywhere else.

**The F99 table costs 2.6e-5** in `A(lambda)/A(V)`, i.e. **8.1e-5 mag** at `E(B-V) = 1`, measured at
wavenumbers four times finer than the grid and at `R_V` values deliberately placed *between* rows.

Getting there found a second thing. Interpolating in `R_V` cost 8.6e-3 at `R_V = 3.85`; interpolating
in **1/R_V** costs 2.6e-5 on the same grid, two and a half orders of magnitude better. That is not a
tuning choice: every published law is written `a(x) + b(x)/R_V`, so at fixed wavelength the curve is
a straight line in `1/R_V` and a curve in `R_V`. Interpolating in the wrong variable is what left
the error.

**The reddening behaves.** At `E(B-V) = 1`, `R_V = 3.1`, F99:

| band | 440 nm (B) | 551 nm (V) | 658 nm (R) | 806 nm (I) |
|---|---|---|---|---|
| A(lambda) | 4.059 | 3.024 | 2.348 | 1.693 mag |
| A/A(V) | 1.3423 | 1 | 0.7765 | 0.5598 |

`A(lambda)` scales with `E(B-V)` to machine precision, which is what makes the map's amount and the
law's shape separable at all, and the curve never rises toward the red.

**The two laws are two laws.** Over 333-909 nm at `R_V = 3.1` they differ by up to 0.062 in
`A(lambda)/A(V)`, which is **0.19 mag at `E(B-V) = 1`**, the literature-sized residual between
CCM89's optical polynomial and F99's spline, and the reason F99 is the default. A harness where they
agreed exactly would be one where only one of them was being evaluated.

## The one subtlety worth knowing

**F99 does not return exactly 1 at V.** The reference implementation gives `k(V) = 0.9793` at
`x = 1.82` for `R_V = 3.1`. That is the published law, not a defect in the table: F99's optical
spline knots sit at 6000, 5470, 4670 and 4110 Å, and V is not one of them.

It is not a problem either, because `A(V) = R_V E(B-V)` is a relation between **band-integrated**
extinctions. `A(V)` is what a source loses through the Johnson V passband, an integral of the
reddened spectrum against the filter, not the value of the curve at one wavelength. Monochromatic
`k(V) = 1` is a property of how a law chooses to anchor itself; CCM89 anchors at a wavelength and
satisfies it by construction, F99 anchors through a spline and does not.

The band-integrated closure is the meaningful one and it is **not testable here**, because it needs
the Johnson B and V passbands integrated against a source spectrum. That is exactly what
`SystemResponse` is for, and it is where this check belongs once the reddening is wired into the
bandpass, the same argument this codebase already made for atmospheric extinction, which had to
move inside the integral for the same reason.

## What this does NOT establish

- **The shape only.** No dust map, no `E(B-V)` for any sight line, nothing about how much dust is
  actually in front of anything.
- **Optical and near-infrared only**, 294 nm to 3.33 µm. The 2175 Å bump sits at `x = 4.6`, outside;
  `RelativeExtinction` returns 0 rather than extrapolating into it, and a UV instrument would need
  the Fitzpatrick & Massa (1990) parameterisation added.
- **One R_V per sight line.** Which is all any published all-sky map supports; Schlafly et al. (2016)
  map R_V variation, and the table's second axis exists for the day that lands.
- **Extinction, not scattering into the beam.** A reflection nebula is a source term, not an
  attenuation.

## Running

```
./env/bin/python generate_f99_table.py     # regenerates Core/Fitzpatrick99Table.cs
dotnet run -p:Core=../../ExoInstruments/Core
./env/bin/python compare_dust.py
```

```
python -m venv env && ./env/bin/pip install numpy scipy astropy dust_extinction
```

Exit code 0 when every check passes. Verified against dust_extinction 1.5.
