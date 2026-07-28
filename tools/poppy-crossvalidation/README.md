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

## The vaned pupil (`compare_vanes.py`)

The harder claim, and the one that removed three invented constants from the display: that six
50 cm vanes across the ELT pupil produce spikes of the right brightness, in the right direction,
with the right falloff, from geometry alone. `Core/PupilDiffraction` sums closed-form transforms
(a disc transform for the annulus, a product of sinc functions per vane); POPPY rasterises the same
pupil and propagates it numerically.

**Direction, established independently.** POPPY's brightest azimuth at 6 λ/D is 150°, one of the
three axes `PupilDiffraction` predicts (30 / 90 / 150). Both put the spikes *perpendicular* to the
vanes, which is the direction a long thin obscuration diffracts into and the easiest thing in this
whole model to get 90° wrong.

**Brightness**, along a spike and between spikes:

| radius | along spike | between spikes |
|---|---|---|
| 2 λ/D | 0.16 % | 0.27 % |
| 4 λ/D | 0.18 % | 0.21 % |
| 8 λ/D | 0.10 % | 0.04 % |
| 12 λ/D | 0.02 % | 0.04 % |
| 16 λ/D | 0.11 % | 0.36 % |

Two radii disagree by more (77 % at 3 λ/D along the spike, 9 % at 6 λ/D between spikes). Both sit
**on nulls**, where the intensity drops to 1e-5 and 1e-6 of peak: POPPY's finite pupil sampling
cannot reach the true depth of a zero, so the percentage there measures POPPY's grid, not a
disagreement about physics.

**Spike-to-background contrast**, the quantity a viewer actually sees, agrees to 0.1 % at 4, 8 and
12 λ/D.

## What this does NOT establish

- **Only the diffraction term.** Not the Kolmogorov atmospheric transfer function (POPPY does not
  model it natively), not the pixel averaging of `RadialPsfProfile` (checked separately, against a
  brute-force square-pixel average, in `tools/bandpass-wcs-tests`), and nothing in the detector chain.
- **The vanes are modelled as spanning the open annulus only**, so they neither overlap at the
  centre nor double-subtract the region the secondary already blocks. A real spider does converge
  on the secondary, which sits inside the obstruction and is therefore already dark. POPPY draws
  its supports the same way, so this comparison does not test that choice.
- **The vane width itself is a literature value, not a measurement made here.** 50 cm is what
  Schwartz et al. (2018) state in prose; METIS phase D simulations quote 54 cm and at least one
  published pupil figure is drawn at 40 cm. Spike brightness scales as the vane area squared, so
  that spread is a factor 1.8 on an effect of order 1e-4 of the peak.
- Agreement does not prove both are right, only that they do not share an error. Sharing neither
  code nor method is the most a cross-validation can offer.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core          # writes exo_*.csv from the shipped Core
python -m venv env && ./env/bin/pip install poppy matplotlib
./env/bin/python compare_poppy.py                     # radially symmetric pupils
./env/bin/python compare_vanes.py                     # the pupil with its spider
./env/bin/python plot_poppy.py                        # writes psf_exo_vs_poppy.png
```

Verified against POPPY 1.1.1.

## Next

POPPY ships `ZernikeWFE`. When wavefront error in Zernike polynomials lands (roadmap item 8, which
would remove the last unsourced optical constant in the project, the RC20's astigmatism in pixels),
this same protocol validates it immediately with no new infrastructure.
