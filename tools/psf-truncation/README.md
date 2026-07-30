# Where a PSF kernel stops, and what it leaves behind

A truncated kernel that is renormalised to unit sum conserves flux exactly, so no photometry test
can see the truncation. What it cannot conserve is the **surface brightness at the boundary**: the
profile drops from its last sampled value to zero in one pixel. Around a bright enough source that
step is a visible edge, and because the kernel is stored as a square array it is a square edge.

This harness measures the two numbers that decide whether a support is big enough: the enclosed
energy inside it, and the profile value at its rim as a fraction of the peak, for every instrument
in the roster, and then checks the frame-wide kernel that replaced the worst case.

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core
```

No Python side: the Kolmogorov profile itself is already cross-validated against GalSim in
`tools/galsim-crossvalidation` (2.3e-4). What this adds is the radial integral of it, whose
normalisation is analytic rather than numerical; the profile is the order-zero Hankel transform of
Fried's OTF, that transform is self-reciprocal, so its integral over the plane is exactly 2*pi.

## What it found

**SPHERE's adaptive-optics seeing halo, at the old 256 px cap.** The kernel was 513x513 in a
1024x1024 frame (half the frame width), and stopped 1.28 seeing-FWHM out, where the profile is
still **3.1e-2 of its peak**. Enclosed energy inside the inscribed circle 90.4%, out to the square's
corners 95.7%, so where the kernel ended depended on azimuth as well. Pushing that step below the
read noise of a tenth-magnitude star needs about 10 FWHM, i.e. a 3985 px kernel: unreachable.

Replaced by `FourierConvolution.RadialKernelSpectrum`, a kernel laid out across the whole padded
frame. It truncates at a lag no two sensor pixels can span, so nothing detectable is left out.
Measured against `OpticalPsf.AtmosphericIntensity` in absolute per-pixel fractions; normalisation
under test as well as shape; it agrees to **4e-6 in the core and 3e-4 at 500 px**, holds 99.485% of
a source's flux (the rest falls at offsets larger than the sensor), and costs 463 ms to prepare, once
per settings change, plus 750 ms to apply.

Sampling the transfer function straight onto the grid would be one transform cheaper and is wrong:
it yields the *aliased* kernel, sum over m of PSF(lag + mN), which re-injects wing flux that should
have left the sensor on the far side. Measured at 3.5e-2 of the profile at 500 px, a hundred times
worse than the real-space route.

**The core kernels, at the old 48 px ceiling.** Not a faint place to stop either:

| instrument | radius then | rim value then | radius now | rim value now |
|---|---|---|---|---|
| RedCat 51 | 2 px | 5.9e-4 | 2 px | 5.9e-4 |
| RC20 | 48 px (1.3 FWHM) | **1.8e-2** | 128 px (3.5 FWHM) | 2.3e-4 |
| CDK1000 | 48 px (1.7 FWHM) | **6.3e-3** | 128 px (4.4 FWHM) | 9.7e-5 |
| FORS2 | 38 px | 1.2e-6 | 116 px (10.1 FWHM) | 1.4e-8 |
| SPHERE core | 48 px | 3.1e-6 | 88 px (12.7 FWHM) | 1.5e-9 |

Three changes together: the ceiling raised to 128, the atmospheric component sized by where its
profile actually gets faint (1e-4 of peak, which the profile itself says is 9.87 FWHM) instead of by
a multiple of its FWHM, and the support clipped to a circle so where the kernel ends no longer
depends on azimuth. The transform cost rises about 1.5x; a wider kernel needs a larger tile, but
proportionally fewer of them.

## Overlap-add tiling

Added while chasing a report of visible squares on a nebula. Every other check in this project
measures *kernels*, and a tiling bug is the one that leaves the kernel perfect while laying a grid
of seams over the frame at the tile pitch, which on a smooth, faint, hard-stretched subject is the
only thing in the picture with edges. The tile pitch is `n - k + 1`, about 58 px on the RedCat at
4x4 and 108 px at 1x1, so seams would be roughly twice as coarse on screen at 4x4 for the same
displayed size, which is what the report described.

`FourierConvolution.Convolve` is compared against a literal O(K^2) convolution of the same image and
kernel, on a smooth gradient plus one bright point:

| kernel radius | worst absolute | worst relative |
|---|---|---|
| 1 px | 1.3e-2 | 2.3e-6 |
| 4 px | 2.9e-3 | 2.3e-6 |
| 16 px | 2.3e-3 | 3.4e-6 |
| 48 px | 5.3e-3 | 6.3e-6 |
| 128 px | 5.5e-3 | 5.6e-6 |

Float round-off, no seams. The tiling is not the cause.

## Negative taps

A point-spread function is an intensity and cannot be negative anywhere. The atmospheric term is
recovered by numerically Hankel-transforming Fried's OTF, and a quadrature of an oscillating
integrand rings: far out in the wings, where the true profile is 1e-4 of its peak or less, the
residual oscillation could in principle exceed the value and take a sample below zero. Convolving a
bright star with a kernel holding negative taps puts **dark patches** around it, which is the one
artefact that cannot be mistaken for physics -- no optical system removes light from the sky.

The check matters more since the kernel ceiling went from 48 px to 128: the kernel now reaches four
times further into exactly that regime. Over all five instruments at 1x1, 2x2 and 4x4:

**0 negative taps out of 66049**, worst case, and the profile itself stays positive out to
rho = 400 (64 lambda/r0). The quadrature's step count follows rho, which is what keeps it converged
that far; see `OpticalPsf.SamplesPerOscillation`.


## Dynamic range, and the tile you could see

The overlap-add check above compares a smooth gradient of about 1000 with one point of 50000: a
range of 50. **A real 120 s narrowband sub is nothing like that.** Its sky sits at about 32
electrons and a bright star's core is millions, a range of 10^5 or more, and the transform was
carried in single precision. 24 bits of mantissa dominated by one enormous value leaves very little
for everything else *in the same tile*, and the round-off is not random: it is coherent across the
tile, so it appears as a rectangle exactly one tile across rather than as noise.

Measured on a frame with a 32-electron sky, kernel radius 2 (the RedCat's own at 1x1, tile 60 px):

| star (e-) | dynamic range | worst error, single | as a fraction of sky | double |
|---|---|---|---|---|
| 1e4 | 3.1e2 | 0.000 | 0.0% | 0.000 |
| 1e6 | 3.1e4 | 0.036 | 0.1% | 0.000 |
| 1e7 | 3.1e5 | 0.379 | 1.2% | 0.000 |
| 1e8 | 3.1e6 | 3.570 | **11.2%** | 0.000 |

This was found from a real frame, not from the harness: a 4x120 s H-alpha stack of the Horsehead
field showed dark rectangles 58 to 64 pixels tall beside the brightest stars, identical in all eight
subs and in both filters, where a fit of `sub = sky + k x map` predicted 8 ADU and the frame held 5.
The tile is 60.

Both transforms now run in **double precision**. The tiled path and the frame-wide one both
improve: the residual against a direct convolution falls from 6e-6 to 1.2e-7 relative (which is now
just the final cast back to float), and the frame-wide kernel's agreement with the analytic profile
goes from 3.15e-4 to **2.96e-6**, a hundredfold. The cost is about 1.8x on the transform.
