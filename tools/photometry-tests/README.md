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
| 3×10³ | 387.0 | 382.9 | 0.990 |
| 1×10⁴ | 399.4 | 390.8 | 0.978 |
| 3×10⁴ | 425.6 | 415.8 | 0.977 |
| 1×10⁵ | 501.2 | 487.6 | 0.973 |
| 3×10⁵ | 671.7 | 652.8 | 0.972 |

Consistently **2–3 % conservative**, never optimistic. The last term of the CCD equation — the noise
on the *background estimate*, entering `n_ap` times and reduced by the annulus size — is the one most
implementations drop; the annulus table shows it biting, with σ falling from 521 to 374 e⁻ as the
annulus grows from 208 to 4212 pixels on the same star.

## 3. Magnitudes in, magnitudes out

Five stars of known magnitude, placed at a known zero point, run through Poisson and read noise,
then detected, measured, fitted and calibrated:

```
zero point fitted from 5 stars: 23.9958 +/- 0.0051   (built with 24.0000)

known    recovered      error    residual   in sigma
 12.00      12.0005     0.0077      0.0005       0.07
 13.00      12.9959     0.0129     -0.0041      -0.32
 14.00      14.0176     0.0271      0.0176       0.65
 15.00      14.9629     0.0619     -0.0371      -0.60
 16.00      15.9464     0.1525     -0.0536      -0.35
```

**The loop closes**: worst residual 0.054 mag, every star inside 0.65 σ, and the zero point
recovered to 0.0042 mag.

The magnitude range is chosen so every star is *detected*. A star below the frame's own limit does
not come back with a large error bar — it does not come back at all, and a round trip that includes
one is testing the detection limit instead of the photometry.
