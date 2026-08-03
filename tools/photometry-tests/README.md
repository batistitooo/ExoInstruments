# Does a magnitude that goes in come back out?

Every other harness here checks a forward model against a measurement. This one checks the forward
model against **its own inverse**, which is the only test that can catch an error of *assembly*: a
zero point that disagrees with the electron counts, a PSF that does not conserve flux, a gain
applied twice. Each is invisible to a test that looks at one stage, and fatal to every number the
pipeline reports.

```
dotnet run -p:Core=../../ExoInstruments/Core -- --out .
```

## 1. The aperture

On a noiseless frame, the aperture sum must reproduce the analytic enclosed energy of a Gaussian,
`1 - exp(-r²/2σ²)`:

| aperture | measured / analytic |
|---|---|
| 0.5 FWHM (r = 2 px) | 0.957 |
| 1.0 FWHM | 1.0035 |
| 1.5 FWHM | 0.9996 |
| 2.0 FWHM | 1.00000 |

The departure at small radii is **discretisation, not error**: pixel-centre membership approximates
a circle by a jagged one whose area is wrong by of order the pixels within half a pixel of its edge,
a fraction going as 1/r. 4.3 % at r = 2, 0.35 % at r = 4, 0.05 % at r = 6 — that law. Centre
membership is kept because it is photutils' own default, and matching the reference is worth more
here than sub-pixel weighting.

The background is recovered exactly and the centroid finds a source placed at (60.300, 59.700) to
better than 0.02 px.

## 2. Is the error bar honest?

A measured flux without an error bar cannot be compared with anything, and an error bar cannot be
checked on one measurement. Measured here by repeating the same star in 400 independent noise
realisations and comparing the scatter with the sigma predicted for one of them:

| flux (e⁻) | predicted σ | measured scatter | ratio |
|---|---|---|---|
| 3×10³ | 369.8 | 382.9 | 1.035 |
| 1×10⁴ | 382.6 | 390.8 | 1.021 |
| 3×10⁴ | 409.7 | 415.8 | 1.015 |
| 1×10⁵ | 487.7 | 487.6 | 1.000 |
| 3×10⁵ | 661.7 | 652.8 | 0.987 |

**This table found a bug.** It first read 0.990 down to 0.972 — a flat 2–3 % over-prediction that
looked like harmless conservatism and was not: `sigma_bkg` is *measured from the annulus* and
therefore already carries the read noise, so the textbook's separate `n_ap·sigma_read²` term was
counting the amplifier twice. The textbook writes it separately because it assumes a *known*
background. An error bar wrong in the safe direction is still wrong, because every detection
significance downstream is wrong by the same factor.

What is left after the fix straddles unity with a residual trend: **+3.5 % at the faint end, −1.3 %
at the bright**. That is centroid jitter, and it is not in the equation. At low signal-to-noise the
refitted centroid wanders between realisations, so the aperture moves and captures a varying
fraction of the flux — extra variance the formula does not model, and which vanishes once the
centroid is well determined.

The last term of the CCD equation — the noise on the *background estimate*, entering `n_ap` times
and reduced by the annulus size — is the one most implementations drop; the annulus table shows it
biting, with σ falling from 509 to 356 e⁻ as the annulus grows from 208 to 4212 pixels on the same
star.

## 3. Magnitudes in, magnitudes out

Five stars of known magnitude, placed at a known zero point, run through Poisson and read noise,
then detected, measured, fitted and calibrated:

```
zero point fitted from 5 stars: 23.9959 +/- 0.0047   (built with 24.0000)

known    recovered      error    residual   in sigma
 12.00      12.0006     0.0072      0.0006       0.08
 13.00      12.9959     0.0118     -0.0041      -0.35
 14.00      14.0177     0.0240      0.0177       0.74
 15.00      14.9629     0.0542     -0.0371      -0.68
 16.00      15.9464     0.1332     -0.0536      -0.40
```

**The loop closes**: worst residual 0.054 mag, every star inside 0.74 σ, and the zero point
recovered to 0.0041 mag.

The magnitude range is chosen so every star is *detected*. A star below the frame's own limit does
not come back with a large error bar — it does not come back at all, and a round trip that includes
one is testing the detection limit instead of the photometry.


---

# Does a pointing that goes in come back out?

The astrometric half of the same argument. A WCS written from the **commanded** pointing describes
intent; one fitted to where the stars actually landed describes the result.

The truth is constructed (tangent point, plate scale 0.125″/px, rotation 17.5°, unflipped), stars
are projected through it, the pixels are perturbed with known centroid error, and the fitter is
handed only pixels and sky.

| check | result |
|---|---|
| projection inverts itself | 3.2×10⁻¹⁰ px, 3.4×10⁻¹¹ arcsec |
| exact recovery, tangent point offered **81.8″ wrong** | map agrees with truth to 4.3×10⁻⁵ ″ frame-wide |
| centroid noise 10 → 160 stars | pointing error 0.0033″ → 0.0014″ → 0.00071″, as 1/√N |
| one mismatched pair, no clipping | 0.68″ error — the fit is wrecked |
| the same with 3σ clipping | found and rejected, clean answer recovered |
| ambiguous match | refused, not guessed |

## Two things this found by failing first

**The rotation is not the truth's, and that is correct.** Fitting with a tangent point an arcminute
away returns 17.4938° against 17.5°. A CD matrix is expressed against its own tangent point's local
north, and meridians converge: predicted difference Δα·sin δ = 22.49″, measured **22.48″**. The right
invariant is that the two WCSs are the same *map*, not that their matrices match.

**The cosine rule cannot measure a small separation.** Every pointing-error check bottomed out at
exactly 3.07×10⁻³ arcsec in three sections until the floor was recognised as the *formula's*: near
zero, `cos θ ≈ 1 − θ²/2` is resolved to ~10⁻¹⁶, so θ is recovered only to 3 mas. Fixed with the
haversine, which also removed a floor on the 160-star pointing error that had looked like the fit
refusing to improve.

## The matching tolerance is a trade

It must exceed the pointing error or nothing matches; it must stay inside the field's mean separation
or sources acquire rivals and are refused. Sixty stars over 2048 px (264 px apart on average),
4″ pointing error:

| tolerance | matched | ambiguous |
|---|---|---|
| 3″ | 2 | 0 |
| 6″ | 49 | 11 |
| 10″ | 39 | 21 |
| 20″ | 13 | 47 |
