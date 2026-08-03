# Brighter-fatter, against the one measurement it is published for

§12.19 used to declare this effect not implemented on two grounds. Both were false.

It said the effect "needs per-sensor electrostatic-vertex calibration tables with no generic
published values" — but **Downing, Baade, Sinclaire, Deiries & Christen (2006, SPIE Orlando)**
measured it by spatial autocorrelation on ESO's own detectors and reported the numbers in prose.
And it said "none of these instruments do stellar photometry", which stopped being true when the
pipeline gained measured aperture photometry.

```
dotnet run -p:Core=../../ExoInstruments/Core -- --out .
```

## What is published

For an e2v CCD44-82 at ~90 ke⁻ in a flat field: nearest-neighbour correlation **1.4 % horizontally
and 2.2 % vertically**, **10 % summed** over all neighbours, and the stated consequence that the
summed correlation *"results in over estimating the gain of the system by 10%"*. The anisotropy is
structural: a pixel is bounded in x by channel stops and in y by the clock lines' electric fields.

## What this harness closes

| check | result |
|---|---|
| correlation → area coefficient | 7.8×10⁻⁸ /e⁻, the order every published coefficient sits at |
| charge conservation | 6.4×10⁻⁹ % |
| published correlations put in, measured back out of a simulated flat | 1.58 % / 2.30 % against 1.40 % / 2.20 % |
| control, no effect applied | 0.127 % (the estimator's own floor) |
| PTC gain bias vs `1 + Σ correlations` | 1.0165/1.0139, 1.0414/1.0386, 1.0759/1.0725 |
| ESO's summed 10 % | predicts their measured 10 % exactly |
| closed-form width growth vs measured | 0.99, 1.00, 1.00 |

## Two errors this found

**The textbook form does not conserve charge.** `Q'ᵢ = Qᵢ(1 + Σⱼ aᵢⱼ(Qⱼ − Qᵢ))` summed over a
neighbouring pair leaves −a(Qᵢ−Qⱼ)², negative definite, so the array loses charge in proportion to
its own variance. Measured as a 2×10⁻⁴ deficit before the algebra explained it. The conserving form
is a symmetric flux across each boundary, `F = a(Qᵢ−Qⱼ)(Qᵢ+Qⱼ)/2`.

**The width formula was missing a factor of two.** The measured growth sat at a ratio of exactly
**0.50** to `a·P/(2s²)`, at three brightnesses and to two decimals. That is not noise: integrating
the flux divergence `(a/2)∂²(Q²)/∂x²` against x² gives `Δ(s²) = a∫Q²/∫Q`, and for a 2-D Gaussian
`∫Q²/∫Q = P/2`, so `Δs/s = a·P/(4s²)`. The old form was the 1-D kernel argument without the 2-D
normalisation.

## What this does *not* establish

An amplitude for any instrument on this roster.

| device | on this roster | autocorrelation published |
|---|---|---|
| e2v CCD44-82 | no | **yes** (1.4 % / 2.2 % / 10 %) |
| MIT/LL CCID-20 (FORS2) | yes | no |
| Sony IMX492 (ASI294MM Pro) | yes | no |
| ZIMPOL CCD (SPHERE) | yes | no |

The same paper tested the CCID-20 and reports its autocorrelation nowhere. So every instrument here
carries `NaN` and the effect is off. The model waits for a number rather than being absent because
a number was believed not to exist.
