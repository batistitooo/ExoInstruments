# galsim-crossvalidation

The second numerical cross-validation of this project against an established scientific code, and
the first one to reach the atmosphere.

`tools/poppy-crossvalidation` settled the diffraction term. It could not touch the seeing, because
POPPY has no Kolmogorov model — and seeing is the larger term by an order of magnitude for every
instrument in this roster except SPHERE. This directory closes that gap against **GalSim 2.8.4**
(Rowe et al. 2015, *Astronomy and Computing* 10, 121), the image-simulation engine behind the LSST
DESC and Euclid shear pipelines.

## What is compared, and why it is a real check

GalSim computes the long-exposure Kolmogorov PSF from the same Fried (1966) transfer function
`exp[-3.44 (λf/r0)^(5/3)]`, but by a different route: a Hankel transform tabulated once at build
time in C++, against `Core/OpticalPsf.AtmosphericIntensity`'s per-sample adaptive Simpson quadrature
in C#. Its optical term is an FFT-propagated sampled pupil, against `AiryIntensity`'s closed form.
No shared code, no shared method.

Everything is compared at **matched r0**, not matched FWHM. The two are related by a constant the
codes do not share, and comparing at matched FWHM would fold that convention into what should be a
physics measurement. The constant is measured separately, on both sides.

Four instruments, at their real shipped parameters read from `VisualTelescopeCatalog` rather than
restated: RedCat 51, RC20, CDK1000 and FORS2/UT1. SPHERE is excluded — its PSF is the two-component
AO core-plus-halo, and GalSim has no AO residual model to compare it against.

## Results

**The atmospheric term is right.** Grid-free, at matched r0:

| comparator | result |
|---|---|
| peak-normalised deviation, core (θ < 2 λ/r0) | 2.3×10⁻⁴ |
| wing ratio, 5–20 λ/r0 | ≤ 3.8 % |
| wing power-law index, this code vs GalSim | −3.680 vs −3.706 (0.69 %) |
| wing index vs the asymptotic −11/3 | 0.38 % |

And on the kernel grid, as `BuildSeeingHaloKernel` actually builds it:

| instrument | max peak-normalised deviation | encircled energy, 0.5–1.6 FWHM |
|---|---|---|
| RedCat 51 | 2.5×10⁻⁴ | ≤ 0.094 % |
| RC20 | 4.5×10⁻⁴ | ≤ 0.007 % |
| CDK1000 | 6.0×10⁻⁴ | ≤ 0.013 % |
| FORS2 | 7.7×10⁻⁴ | ≤ 0.063 % |

**Chromatic scaling agrees exactly.** Kolmogorov turbulence gives r0 ∝ λ^(6/5) and seeing FWHM ∝
λ^(−1/5). `ComputeGroundSeeingFwhmArcsec` applies that exponent by hand; GalSim derives it from r0.
Across 400–800 nm the two agree to **0.0000 %** at every wavelength.

## Two defects this found, and fixed

**1. The atmospheric quadrature failed in the wings.** `AtmosphericIntensity` integrated with a
fixed 512 Simpson steps. The integrand oscillates as `J0(ρu)`, so the number of oscillations grows
linearly with ρ — with how far into the wings the profile is asked about — and a fixed step count
runs out of resolution. Measured against a high-order adaptive quadrature of the same integral:

| θ (λ/r0) | 512-step result vs exact |
|---|---|
| 5 | +0.3 % |
| 8 | +4.5 % |
| 12 | +46 % |
| 20 | **×10.2** |

The true θ^(−11/3) Kolmogorov wing came out as θ^(−2.18). That is not a small error in a faint
place: the seeing halo is what aperture photometry integrates, and a wing an order of magnitude too
bright puts light in the sky annulus that is not there. Fixed by setting the resolution *per
oscillation* (24 points) rather than per unit interval; the fitted wing index is now −3.68.

**2. The Fried constant disagreed with this code's own profile.** `FriedParameterMeters` inverted
`FWHM = 0.98 λ/r0` (Roddier's round figure), while the exact profile evaluated three lines below has
its half-power point at ρ = 3.0648, i.e. `FWHM = 0.97554 λ/r0`. A telescope told to deliver
Paranal's 0.72″ therefore delivered 0.7167″ — 0.45 % narrow, systematically, on every instrument.
The constant is now measured from the profile by bisection, the same way `AiryFwhmArcsec` already
bisects the real Airy pattern instead of quoting the 1.028 λ/D rule. GalSim's independently
tabulated value is 0.975863: the two agree to **0.033 %**.

## One defect this found, and did not fix

The full kernel still disagrees with GalSim, and section 4 of the harness attributes all of it to
how `BuildKernel` samples the **diffraction** term. Encircled energy within 0.5 FWHM:

| instrument | px per FWHM | mod | point-sampled | pixel-integrated | sampling | truncation | **total** |
|---|---|---|---|---|---|---|---|
| RedCat 51 | 1.17 | 0.861 | 0.743 | 0.543 | +37.0 % | +15.8 % | **+58.7 %** |
| RC20 | 9.24 | 0.453 | 0.429 | 0.428 | +0.4 % | +5.5 % | **+5.9 %** |
| CDK1000 | 7.51 | 0.454 | 0.425 | 0.422 | +0.5 % | +6.9 % | **+7.5 %** |
| FORS2 | 5.44 | 0.410 | 0.403 | 0.398 | +1.1 % | +1.8 % | **+3.0 %** |

Two independent causes, neither a difference of opinion about physics:

- **Point sampling instead of pixel integration.** `OpticalPsf.SampleRadial` evaluates the profile
  at pixel *centres*; a detector integrates over the pixel's area. Harmless where the PSF is well
  sampled, and dominant on the RedCat, whose PSF is 1.17 pixels wide. `Core.RadialPsfProfile` —
  used by the high-contrast display, and validated for exactly this in `tools/bandpass-wcs-tests` —
  *does* pixel-average. The camera's kernel does not use it.
- **The diffraction term's support.** `BuildKernel` gives it `RadiusFor(airyFwhm)`, three times the
  Airy FWHM. An Airy pattern's θ^(−3) wings carry real flux far past that, and truncating then
  renormalising moves it into the core.

Both make the PSF too concentrated, so the **aperture correction is optimistic** — by 3 % on FORS2
and by 59 % on the RedCat. That propagates straight into `CcdEquation`, whose encircled-energy term
sets every predicted photometric uncertainty.

This is left as a finding rather than fixed here because unlike the two above it is not a
self-contained numerical bug: it changes the kernel budget and the cost of every capture, and every
rendered frame with it, so it needs the in-game checks in `TESTING.md` that this machine cannot run.

## What this does NOT establish

- **Long-exposure Kolmogorov only.** No finite outer scale: real seeing follows von Kármán, and
  Paranal's L0 ≈ 22 m narrows the delivered FWHM on an 8.2 m aperture by of order 10 %. GalSim ships
  `VonKarman`, so the day that lands this harness measures it with no new infrastructure.
- **One wavelength per filter.** Both sides are monochromatic here, as the mod is. GalSim's
  chromatic objects would be the way to check a bandpass-integrated PSF, and there is nothing yet
  to check against.
- **No wavefront error.** GalSim's `OpticalPSF` takes Zernike aberrations and the mod has none, so
  the RC20's astigmatism-in-pixels stand-in is untouched here (roadmap item 8).
- **Nothing downstream of the kernel.** Not the detector, not the sky, not the photometry.
- Agreement does not prove both are right, only that they do not share an error.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core          # writes exo_*.csv from the shipped Core
python -m venv env && ./env/bin/pip install galsim numpy scipy
./env/bin/python compare_galsim.py
```

Exit code 0 when every check passes, 1 otherwise. **It currently exits 1**, on the 17 checks that
make up the one unfixed finding above. That is deliberate: a harness tuned to pass would have
recorded a 59 % aperture-correction error as an accepted tolerance.

Verified against GalSim 2.8.4.
