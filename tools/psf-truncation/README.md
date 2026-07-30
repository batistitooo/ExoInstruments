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
