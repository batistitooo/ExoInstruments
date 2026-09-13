# delivered-psf-tests

Headless checks on how a published delivered PSF width becomes the kernel's Gaussian term:
`OpticalPsf.GaussianFwhmForDelivered`, and the `PsfWidthPlane` each shipped catalogue entry files its
width under.

```
dotnet run -c Release -p:Core=../../ExoInstruments/Core
```

Must print `ALL CHECKS PASSED`. About a second. No Unity, no KSP.

## The rule this harness follows

**Nothing is asserted against the code's own output.** Every width is compared with a published
figure, and it is read back through a reference built here: the pupil's pattern from
`PupilDiffraction` at points, 48 samples per λ/D, convolved with one analytic filter (the Gaussian
and, where there is one, the pixel, as a difference of error functions). It shares no code with the
solve or with the kernel builder. `tools/spacecraft-tests` section 5 reads the same Table 6.7 rows
back through `BuildKernel` instead, so the two routes cross-check each other.

## What it was written to catch

A published width means something only in the plane it was measured in. WFC3 IHB Table 6.7 is
captioned "pre-pixelation". The solve used to match it on the finished kernel at the frame's own plate
scale, which is already integrated over the **binned** pixel, so it counted the pixel as optics and
the answer moved with binning. Measured before the fix, against the same kind of reference:

| WFC3/UVIS | Gaussian 1x1 | optics 1x1 | Gaussian 4x4 | optics 4x4 |
|---|---:|---:|---:|---:|
| 200 nm, table 0.083″ | 0.0705″ | 0.0761″ (−8.3 %) | 0 | 0.0167″ (−80 %) |
| 500 nm, table 0.067″ | 0.0501″ | 0.0644″ (−3.9 %) | 0 | 0.0419″ (−37 %) |
| 800 nm, table 0.074″ | 0.0326″ | 0.0723″ (−2.3 %) | 0 | 0.0670″ (−9.5 %) |
| 1000 nm, table 0.084″ | 0 | 0.0837″ | 0 | 0.0837″ |

At 4x4, the default binning, the diffraction kernel alone already measured 0.160 to 0.172″ on its
central row, wider than every row of the table, so the solve returned zero everywhere and Hubble
imaged at its bare diffraction limit. ACS/HRC behaved the same way: 0.0402″ at 1x1, whose image at
native pixels came out 0.0614″ against 0.0665″ published, and zero at 4x4.

## A. What each shipped width is

| entry | source | plane |
|---|---|---|
| WFC3/UVIS | IHB Table 6.7, "pre-pixelation" | before pixelation |
| WFC3/IR | IHB Table 7.5, "before pixelation" | before pixelation |
| ACS/WFC | ACS IHB Sect. 5.6.6, 0.10 to 0.13″ in F550M | native pixels |
| ACS/HRC | ACS IHB Sect. 5.6.6, 0.060 to 0.073″ in F550M | native pixels |
| LORRI | Weaver et al. 2020 Sect. 3.1, (2.06, 2.65) px from 1x1 flight frames | native pixels |

The two WFC3 tables give each width in pixels and in arcsec. If both columns are one optical width,
every row must admit a single plate scale within the columns' rounding, and they do: 0.03998 to
0.04004″/px for Table 6.7 and 0.12792 to 0.12813″/px for Table 7.5.

The ACS reading is an inference. Sect. 5.6.6 attributes the WFC's field variation to CCD charge
diffusion in the same paragraph that gives both ranges, and 0.10 to 0.13″ is two to 2.6 WFC pixels,
far wider than the optics. It does not say outright that the pixel is in the HRC figure.

## B. Table 6.7 before pixelation

| nm | table | Gaussian | optics | image, 1x1 | image, 4x4 |
|---:|---:|---:|---:|---:|---:|
| 200 | 0.0830 | 0.0775 | 0.0830 | 0.0877 | 0.1622 |
| 300 | 0.0750 | 0.0655 | 0.0750 | 0.0805 | 0.1615 |
| 400 | 0.0700 | 0.0579 | 0.0700 | 0.0767 | 0.1618 |
| 500 | 0.0670 | 0.0527 | 0.0670 | 0.0740 | 0.1629 |
| 600 | 0.0670 | 0.0479 | 0.0670 | 0.0731 | 0.1651 |
| 700 | 0.0700 | 0.0436 | 0.0700 | 0.0750 | 0.1679 |
| 800 | 0.0740 | 0.0371 | 0.0739 | 0.0781 | 0.1699 |
| 900 | 0.0780 | 0.0247 | 0.0780 | 0.0814 | 0.1703 |
| 1000 | 0.0840 | 0.0093 | 0.0840 | 0.0870 | 0.1692 |

The optics reproduce every in-band row to 0.08 %, against a tolerance of 0.25 %, a third of the
table's own rounding at 0.067″. The image columns are the width a fit to stars at every sub-pixel
phase converges on, and both are wider than the table, which is what the caption says a fit to the
pixelated PSF will give. The Gaussian no longer depends on binning at all.

## C. Widths measured in the image

Solved on the profile integrated over one **native** pixel, whatever the frame is binned to:
ACS/WFC 0.1149″ (−0.06 %), ACS/HRC 0.0664″ (−0.16 %), LORRI 2.3894″ (−0.02 %).

## D. Why the solve cannot look through the binned pixel

The regression, as an assertion against Table 6.7: at 4x4 the diffraction kernel alone measures
0.1613″ at 500 nm against the table's 0.067″, while the diffraction it would leave is 0.0418″, 37.6 %
narrower than the row.

## What is not checked

- **WFPC2/PC1.** Its 0.088″ is Casertano et al. (2000) Sect. 4.2's Gaussian fit to stars in the
  drizzled HDF-South mosaics (PIXFRAC 0.8, 0.0399″ output pixels). That is neither plane: it contains a
  drizzle kernel this pipeline does not have, and the entry also carries a charge-diffusion kernel
  applied at readout, which a width measured on images already contains.
- **Pointing.** The camera adds the pointing excursion in quadrature after the solve. A width measured
  on flight frames already holds whatever jitter those frames had.
- **WFC3/IR's coupling.** Table 7.5's model includes interpixel capacitance, and the entry applies its
  measured IPC kernel again at readout.
- **Sampling.** The solve samples at 12 per λ/D. At 24 the worst residual above falls from 0.16 % to
  0.05 %, at 3.5 times the cost (4 ms against 14 ms per solve on the vaned, padded HST pupil).
