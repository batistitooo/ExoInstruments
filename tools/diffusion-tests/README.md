# diffusion-tests

Headless checks on CCD charge diffusion: `Core/ChargeDiffusion.cs`, the step in
`SolarSystemCameraTexture.RunDetectorChain` that applies it, and WFPC2/PC1's kernel and delivered PSF
width in `VisualTelescopeCatalog`.

```
dotnet run -p:Core=../../ExoInstruments/Core
```

Must print `ALL CHECKS PASSED`. No Unity, no KSP.

## The rule this harness follows

Same as `tools/infrared-tests`: **nothing is asserted against the code's own output.** Every check is a
transcription of a published kernel or table, a statement a source makes in prose, or an independent
computation of the same quantity (a native-pixel simulation, a per-electron Monte Carlo).

## Where it sits in the chain

Charge diffusion moves real photoelectrons, so it acts on the expected electrons **before** the Poisson
draw, and therefore before saturation, blooming, CTI and the readout-side IPC step. Interpixel capacitance
moves no charge and stays at readout. The sky is not diffused: it is uniform, and section D shows a
uniform field comes out unchanged.

## A. The kernel against Tiny Tim

`wfpc2pc1.pup`, "WFPC2 CCD Pixel Scattering Function (estimates charge diffusion)", cell by cell. It sums
to 1, and it is symmetric, which a "Gaussian kernel of 3 x 3 pixels" (Tiny Tim User's Guide v6.3) has to be.

## B. The handbook's account of the same pinhole test

The WFPC2 Instrument Handbook, Sect. 5.4, prints **a different kernel** from Tiny Tim's:

|        | Tiny Tim           | Handbook Sect. 5.4      |
|--------|--------------------|-------------------------|
| top    | 0.0125 0.05 0.0125 | 0.016 0.067 0.016       |
| middle | 0.05   0.75 0.05   | 0.080 0.635 0.080       |
| bottom | 0.0125 0.05 0.0125 | 0.015 0.078 0.015       |
| sum    | 1                  | 1.002, as printed       |

Both describe the flight-spare pinhole test, where a centred pinhole left "only about 70%" in its pixel,
and the two kernels straddle that figure. Each document also quotes a Gaussian jitter for the effect, and
a centred point jittered by that RMS should leave the same share in its pixel as the kernel does:

| jitter | source | share left in the pixel | its own kernel |
|---|---|---|---|
| 18 mas | handbook | 63.0 % | 63.5 % |
| 14 mas | Tiny Tim guide | 80.3 % | 75.0 % |

So the handbook's 18 mas belongs to the handbook's kernel, not to Tiny Tim's. The old catalogue comment
paired Tiny Tim's kernel with 18 mas through second moments; the pinhole test measures the centred share,
and on that measure the pairing does not hold. The shipped kernel is Tiny Tim's.

## C. Binning

`BinningFactor` defaults to 4, but diffusion happens at native pixels. `ForBinning` averages the kernel
over a uniformly lit binned pixel, and is checked against an independent route: light one binned pixel
at native resolution, diffuse it there, bin the result. The asymmetric handbook kernel is run through the
same check, so a flipped axis cannot hide behind a symmetric kernel. At 4 x 4 the Tiny Tim centre rises
from 75.0 % to 92.8 %.

## D. Applying it to a frame

A point source spreads by exactly the kernel and the right way up (checked with the asymmetric kernel);
an interior source keeps all its charge; a uniform field is unchanged, edges included.

## E. Why it acts before the shot-noise draw

A 256 x 256 field at 50 e- per pixel, every electron moved individually by the kernel's probabilities:

| route | variance / mean |
|---|---|
| electrons diffused one by one | 0.996 (Poisson is 1) |
| Poisson draw, then counts convolved | 0.5746, against the sum of squared cells 0.5731 |

Convolving drawn counts would leave shot noise 24 % too low. Diffusing the mean and then drawing is
exactly Poisson, which is what the chain does.

## F. The shipped WFPC2/PC1 entry

PC1 carries Tiny Tim's kernel and no IPC kernel. Its plate scale matches the pup file's 0.04555 arcsec
pixel, the scale the kernel is defined on. Any diffusion kernel on the roster must be a 3 x 3 on a CCD
that conserves charge, because the chain silently skips any other shape.

## G. PC1's delivered width against the handbook's model PSFs

`SpacePlatform.DeliveredPsfFwhmArcsec` is the optics' width **before pixelation**. PC1 used to carry a flat
0.088 arcsec, which is Casertano et al. (2000, AJ 120, 2747) Sect. 4.2: the best-fitting Gaussian of stars
in the drizzled HDF-South PC images. That is an image-plane width, so it already held the pixel, the
jitter and the charge diffusion the chain adds. With diffusion in the chain the finished PSF came out at
103.5 mas at 555 nm, against the 88 it was measured as. Setting the field to null would have gone wrong
the other way, because the handbook's model PSFs peak far below diffraction limited.

The Cycle 12 handbook's Table 5.3 gives, for a star near a pixel centre, the central pixel's share of
the 5 x 5 flux. It gives it for a model PSF "with the observed wavefront errors and pixel response
function", and for the diffraction-limited case. The model carries Sect. 5.4's kernel and no jitter. The
catalogue now holds the widths that reproduce the model's peak relative to diffraction limited:

| λ | Table 5.3 model / diffraction limited | shipped width | diffraction alone |
|---|---|---|---|
| 200 nm | 25.0 / 62.9 | 0.059 arcsec | 0.017 arcsec |
| 400 nm | 26.1 / 49.4 | 0.049 arcsec | 0.033 arcsec |
| 600 nm | 20.7 / 31.6 | 0.054 arcsec | 0.049 arcsec |
| 800 nm | 15.4 / 22.5 | 0.070 arcsec | 0.065 arcsec |

The check runs the other way from the fit. It converts each shipped width into a Gaussian by its own
inversion on a grid nine times finer than the pixel, independent of `GaussianFwhmForDelivered`. It then
builds PC1's pixel-integrated PSF, applies the handbook's kernel, and asserts the peak ratio against the
table to 2 % (the table's 0.1 rounding and the widths' 1 mas rounding). The widths are narrowest in the blue
and climb both ways, the shape WFC3/UVIS's measured Table 6.7 has behind the same mirror.

**Not asserted:** the absolute peaks. For diffraction alone `OpticalPsf` gives 57 %, 40 % and 29 % at 400,
600 and 800 nm, where the table gives 49 %, 32 % and 23 %. The table's star is only near the centre (its
left and right neighbours differ) and the offset is not published, so only the ratio, which both columns
share, is checked.
