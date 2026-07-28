# poppy-crossvalidation

The first numerical cross-validation of this project against an established scientific code.

`TECHNICAL_REFERENCE.md` compares this mod's *design choices* against GalSim, Pyxel and POPPY in
several places. It did not, anywhere, compare a *number*. This directory does, for one mechanism:
the annular-pupil diffraction pattern of `Core/OpticalPsf.cs`.

## What is compared, and why it is a real check

**POPPY** (Perrin et al., the engine under WebbPSF) propagates a numerically sampled pupil to a
focal plane by matrix Fourier transform. `Core/OpticalPsf.AiryIntensity` evaluates the closed form
for an obstructed circular aperture (Born & Wolf). The two share **no code and no method**, so
agreement is evidence about the physics rather than about a shared implementation.

Three pupils, so the agreement cannot be a coincidence of one configuration:

| case | D | obstruction | wavelength |
|---|---|---|---|
| `elt` | 39.3 m | 0.2824 (ESO's 11.1 m) | 1.6 µm, H band |
| `rc20` | 0.51 m | 0.39 | 552.5 nm, Luminance |
| `clear` | 39.3 m | 0 | 1.6 µm |

Every comparator is dimensionless and truncation-matched, normalised inside the same 40 λ/D outer
radius in both codes, so neither one's array size or quadrature range can flatter it.

## Results

**Encircled energy**, the comparator that is insensitive to truncation on both sides:

| radius | ELT, H | RC20, L | ELT unobstructed |
|---|---|---|---|
| 1 λ/D | 0.005 % | 0.005 % | 0.018 % |
| 2 λ/D | 0.001 % | 0.001 % | 0.002 % |
| 5 λ/D | 0.003 % | 0.003 % | 0.002 % |
| 20 λ/D | 0.001 % | 0.001 % | 0.001 % |

**Core FWHM**: −0.204 % (ELT), −0.132 % (RC20), +0.013 % (unobstructed).

**Radial intensity profile**: below 0.2 % everywhere except at the ring nulls, where the intensity
passes through zero and a percentage of it is meaningless.

**First null**: the reported 0.5–0.8 % spread is an artifact of *this harness*, not a disagreement.
POPPY's profile is binned at 0.02 λ/D, and the three values it returns (1.21000, 1.13000, 1.07000)
land exactly on bin centres. At the resolution of the measurement the two codes agree.

## What this does NOT establish

- **Only the diffraction term.** Not the Kolmogorov atmospheric transfer function (POPPY does not
  model it natively), not the pixel averaging of `RadialPsfProfile` (checked separately, against a
  brute-force square-pixel average, in `tools/bandpass-wcs-tests`), and nothing in the detector chain.
- **No spider vanes** (`n_supports=0`, deliberately). This mod has no pupil-transform spider model;
  its six spikes are a display term with an assumed amplitude, recorded in §12 entry 9a. Comparing
  them here would compare POPPY's physics against a drawing.
- Agreement does not prove both are right, only that they do not share an error. Sharing neither
  code nor method is the most a cross-validation can offer.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core          # writes exo_*.csv from the shipped Core
python -m venv env && ./env/bin/pip install poppy matplotlib
./env/bin/python compare_poppy.py                     # prints the comparison tables
./env/bin/python plot_poppy.py                        # writes psf_exo_vs_poppy.png
```

Verified against POPPY 1.1.1.

## Next

POPPY ships `ZernikeWFE`. When wavefront error in Zernike polynomials lands (roadmap item 8, which
would remove the last unsourced optical constant in the project, the RC20's astigmatism in pixels),
this same protocol validates it immediately with no new infrastructure.
