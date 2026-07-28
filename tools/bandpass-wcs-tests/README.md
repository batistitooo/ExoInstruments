# bandpass-wcs-tests

Headless verification of three pieces of `Core/`:

- **`SystemBandpass.cs`** — the integrated system response that replaced the grey-band photometry
  (`W = FWHM x QE_peak x T(lambda_c)`) with an integral of the source spectrum against the real
  filter, optical throughput, detector QE curve and wavelength-dependent extinction.
- **`FitsWcs.cs`** — the TAN world coordinate system written into exported FITS frames, measured
  from the same `GnomonicProjection` that places the stars in the image.
- **`RadialPsfProfile.cs`** — the exact annular-pupil diffraction pattern as a sampled radial
  profile, which replaced the Gaussian core and invented ring envelope the high-contrast imaging
  display used to synthesise its own PSF from.
- **`PupilDiffraction.cs`** — the full two-dimensional pattern of a real pupil, vanes included,
  which replaced the three invented constants that used to draw the display's diffraction spikes.

No Unity, no KSP, no game. Everything under test is pure `Core/` C#.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
```

Exit code 0 when every check passes, 1 otherwise, so it drops straight into CI.

`Stub.cs` restates the `CameraFilter` enum, which lives in the Unity-dependent `Visualization`
layer but carries no Unity dependency itself. That is deliberate and worth knowing: it lets this
harness compile the **real** `VisualTelescopeCatalog`, so the throughput and filter figures it
checks are the ones the mod ships rather than a second copy free to drift from them.

## What the 55 checks establish

**The new photometry is a generalisation of the old one, not a replacement.** With a flat source
spectrum, a grey QE and no atmosphere, the integral reproduces `FWHM x QE` exactly (to 1e-12), and
the electron counts agree with `PhotonFluxModel.CollectedElectronsGreyBand` to the same precision.
A filter centred on Johnson V has no colour term at all, which it must not, since that is where the
magnitude normalisation is applied. On a narrow band the integral reproduces the two-wavelength
`StellarPhotometry.ColorTerm` it subsumes to 0.015% across 2800-20000 K, the residual being the
colour table's own interpolation.

**The colour term now falls out of the integral** with the right sense: a hot star is blue-dominated
and an M dwarf red-dominated by a factor of three, and reflected sunlight sits between the two,
which is the spectrum every photographed planet in the frame is integrated with.

**Throughput does what the sources say.** Two aluminium surfaces at 0.87 give 0.757; FORS2 at UT1's
Cassegrain focus beats SPHERE at UT3's Nasmyth focus (0.757 against 0.520, the difference being one
extra mirror and ZIMPOL's published 79% beam splitter); one extra aluminium mirror costs 13% of the
light, as Ma & Cai state outright. The consequence is checked too: the RC20's limiting magnitude
becomes 0.30 mag shallower and SPHERE's 0.71 mag, which is the point of adding the term.

**The QE curve matters as much as the throughput.** FORS2's b_HIGH filter sits at 440 nm where its
detector is at 58%, not the 86% peak, and using the peak overstated that band by 1.33x. At the peak
itself curve and scalar agree to 0.4%, which confirms the difference is the curve's shape and not a
normalisation error. The curve holds flat outside its measured range instead of extrapolating, and
rejects an out-of-order or single-point table rather than silently repairing it.

**Extinction had to move inside the integral.** On FORS2's 7700 Angstrom unfiltered band,
integrating differs from sampling the central wavelength by 5.5%; blue loses 26.6% at airmass 2
against red's 12.2%.

**The WCS describes the image it ships with.** `CRVAL` is the boresight's own RA/Dec, `CRPIX`
carries the half-pixel offset between the renderer's convention (pixel `i` spans `[i, i+1)`) and
FITS's (integers on pixel centres), and the CD matrix reproduces the instrument's plate scale. The
decisive check is a round trip: deproject each of the frame's centre and four corners through an
**independently written** inverse TAN (the textbook relations, not a rearrangement of `FitsWcs`),
then ask the pipeline's own projection where that direction lands. It closes to 5e-9 arcsec across
the whole sensor. A field centred exactly on the celestial pole is checked separately, because that
is where an implementation stepping in right ascension divides by `cos(dec)` and falls apart.

**The imaging display's PSF is now the same physics as everything else's.** The profile's encircled
energy reproduces the closed form `2·(1 − J0² − J1²)` (Born & Wolf) to 4×10⁻⁸ across the core and
the first five rings, which tests the intensity and the quadrature together at every radius rather
than the shape near the peak. An unobstructed pupil puts its first null on the textbook
`1.22·λ/D`; the ELT's real 11.1 m obstruction moves it inward to `1.124·λ/D` (9.44 mas in H band)
and narrows the core from 8.64 to 8.28 mas, the classic signature of an annular pupil.

**Pixel averaging is reducible, and demonstrably second-order.** The pixel average departs from
point sampling by 4×10⁻⁷ of the peak at a plate scale of `λ/D`/1000, and halving the pixel divides
that departure by 4.00 twice over, which is the convergence order an area average must have and
which a coincidentally small number would not show. Against a brute-force two-dimensional average
of a real square pixel — written independently, in the test file — the profile agrees to 7×10⁻⁴ of
the peak across plate scales from 0.1 to 4 `λ/D` per pixel, about a twentieth of one of the
display's 256 levels. The crossover between the two-dimensional and radial regimes leaves a step no
larger than that residual, so it adds nothing to the error budget.

**And the averaging is what the pattern needs, not a smoothing.** At a coarse plate scale,
consecutive point samples land on ring maxima and nulls and differ by up to 59.8×; the averaged
profile varies by at most 4.28× over the same pixels, a 14× reduction in swing, while still falling
as steeply as the real `θ⁻³` envelope. The tabulated profile carries the same integrated light as
direct evaluation to 0.34% over a full 400 px raster, and the peak pixel dilutes monotonically as
the plate scale coarsens — recovering 0.9989 of the point peak at 0.05 `λ/D` per pixel and holding
0.077 of it at 4 `λ/D` per pixel, which is detector physics rather than a modelling loss.

**Spikes and rings now come from one pupil.** With its vanes removed, `PupilDiffraction` reproduces
the published closed-form annular pattern to **7.8×10⁻¹⁶** of peak over the core and twenty rings,
and is azimuthally flat to 3.3×10⁻¹⁶ — two independent routes to the same physics agreeing to
machine precision. With them in place, on-axis intensity is exactly 1, the vanes remove the
3.789 % of the open pupil their real geometry removes, and the spikes land **perpendicular** to the
vanes that cast them, standing 9.6×10⁶ above the faintest azimuth at 6 λ/D. The simulator's
diffraction limit is now its own pupil's first null, 9.440 mas, rather than the unobstructed
Rayleigh criterion's 10.245 mas.

## Note on the sibling harness

`tools/skyfield-tests` covers the star-field geometry and catalogue. Its committed copy still calls
the pre-electrons API (`PointSource.SignalFraction`, a `FullWell` argument to `DepositStars`) and so
does not build as committed; it also stubs out `VisualTelescopeSpec` and therefore cannot check any
catalogue value. These checks were kept separate rather than merged into it for that reason.
