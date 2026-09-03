# psf-cost, what the PSF kernel costs, and what a cheaper one is worth

Baptiste's report, 2026-08-07: a galaxy photograph through the orbital Hubble at 4×4 binning took
about seven minutes on a Mac and about one on a desktop PC. The shipped per-stage log answered
where the time was without any guessing:

```
Reduced a 1024x1025 frame (binning 4) in 424504 ms on 9 worker(s): render readout 44 ms,
galaxies 625 ms, smear 0 ms, stars + emission 10764 ms, PSF kernel 410724 ms,
PSF convolution 481 ms, coronagraph + speckles 624 ms, detector 1239 ms
```

**96.7 per cent of the exposure was building the kernel.** Not convolving with it, building it.

## Why it got expensive

`f387724` gave the diffraction term the whole 128 px kernel budget on any pupil with a spider,
and it was right to: a spike is faint structure reaching far out, so how far it can be seen is set
by the source's brightness against the sky, not by the width of the core it comes from. Tying the
two together had made Hubble's spikes invisible.

What did not follow was the cost model. That support is 257×257 = 66049 pixels, each a midpoint
average over up to 12×12 nodes, so **one sub-band kernel is 9.5 million evaluations of
`PupilDiffraction.Intensity`**, each of which is five Bessel functions and fifteen other
transcendentals. A capture built twelve of those for the passband, and, before this, twelve more
inside `GaussianFwhmForDelivered`, which built a full kernel per bisection step and then read one
row of it. At 1×1 that solve does not exit early, so it built **three hundred** of them.

The cost also runs the wrong way with binning: the node count is set by how many λ/D ring periods
a pixel spans, so a 4×4 pixel needs 12×12 nodes where a 1×1 pixel needs 4×4. Coarser binning made
a smaller frame out of a nine-times-more-expensive kernel.

## What changed

Four things, in decreasing order of what they were worth. **Three are exact and one makes the
kernel more accurate than it was.** None of them is an approximation traded for speed; the one
that would have been was measured and rejected, and is recorded at the bottom.

1. **The FWHM solvers build only the support they read.** `GaussianFwhmForDelivered` and
   `AtmosphericFwhmForDelivered` measure a half-power crossing on one row. Normalisation is a
   single scale factor, so it divides out of that ratio, and the convolutions after the
   diffraction term reach only their own radius, so a row sample is complete as soon as the
   support extends past the crossing by that reach. The bound is sized from the width being
   solved for and *checked*: if the crossing did not fall inside it, the full kernel is built.
2. **The sampler folds the grid on symmetries the pupil proves.** `|A|²` is even for any real
   pupil, always, so half the grid is a copy. A pupil symmetric about both axes and both
   diagonals, four vanes at 0° and 90°, no pads, which is every ground instrument here, leaves
   one octant determining the pattern. `PupilDiffraction` works out which reflections it has from
   its own vane angles and pad positions and reports them; Hubble's three pads sit at 120° and
   break all of them, so it keeps the central symmetry alone.
3. **Kernel terms are composed through a transform above a work budget** rather than by direct
   sum, the convolution theorem, agreeing with the sum it replaces to 3.4e-14 of the peak. See
   the next section; this is the largest single saving and only the ground instruments pay it.
4. **The few dozen pixels holding the light are sampled far better than before**, at sixteen
   nodes per ring period and a ceiling of 48 rather than four and 12. This is not a saving at
   all; it is a cost, taken deliberately, because it is what makes the finished kernel more
   accurate than the one it replaces. It applies to 81 pixels of 66049, so it is nearly free.

## The other half, which only the ground instruments pay

Hubble has no atmosphere, so its kernel is the diffraction grid and a small Gaussian. A ground
instrument's is that grid **convolved with a Kolmogorov profile**, and `OpticalPsf.Convolve` did
that as a direct sum, O(ra²·rb²). That was fine while the diffraction term was a handful of taps.
At the full budget it is a 257×257 grid against a 183×183 profile: **2.2 billion multiply-adds per
sub-band, twelve sub-bands per capture.**

`tools/capture-profile` did not see it, because it was passing `vaneCount = 0` and timing
`OpticalPsf`'s radial path, a kernel the shipped RC20 has not built since the visual roster's PSF
learned about spiders. Given the instrument's real spider, its own numbers say:

| RC20, M51 from OHP | before | after |
|---|---:|---:|
| PSF kernel, 4×4 | 8855 ms | **251 ms** |
| whole reduction, 4×4 | 9502 ms | **809 ms** |
| PSF kernel, 1×1 | 13916 ms | **260 ms** |
| whole reduction, 1×1 | 15736 ms | **1293 ms** |

The fix is `FourierConvolution.ConvolveKernels`, used above a work budget and falling back to the
direct sum below it so the compact kernels keep their exact answer. `--convolve` checks the two
routes against each other on the shapes a PSF is made of:

```
 ra   rb  rOut       direct ms   transform ms   max|d| / peak   sum ratio
 16   12    28               2              4       2,01E-015   1,000000000000
 64   48   112             211             12       7,79E-015   1,000000000000
128   91   128            2574             15       3,28E-014   1,000000000000
128  128   128            4594             28       3,36E-014   1,000000000000
```

`tools/capture-profile --determinism` still reports every parallel stage identical at one worker
and at nine, at both binnings and on the Horsehead.

## Measured

.NET 10, Apple M5, 9 workers, best of one, HST OTA + WFC3/UVIS, the PSF stage of one capture
(twelve delivered-width solves, then the twelve-sub-band kernel):

| binning | solve before | kernel before | total before | total after | |
|---|---:|---:|---:|---:|---:|
| 1×1 | 15838 ms | 366 ms | **16204 ms** | **406 ms** | 40× |
| 2×2 | 2065 ms | 1406 ms | **3471 ms** | **794 ms** | 4.4× |
| 4×4 | 5236 ms | 4245 ms | **9480 ms** | **1969 ms** | 4.8× |

At 4×4 that is the 411 s Baptiste measured coming back as about **85 s**, and the whole reduction
from 424 s to roughly 100 s. The 1×1 column is the one to look at twice: that is the case where
the solve ran its full bisection, and 16 s under .NET is on the order of a quarter of an hour
under KSP's Mono.

Kernel build, whole roster at 4×4, against a converged reference (four times the node count, no
fold, no taper) built here from `PupilDiffraction` directly so it inherits none of the sampling
decisions it judges:

| pupil | before | after | | max\|d\|/peak before → after | arm before → after | diagonal before → after |
|---|---:|---:|---:|---|---|---|
| RC20 | 239 ms | 71 ms | 3.4× | 1.15e-2 → **3.44e-4** | 0.9 % → **0.6 %** | 0.8 % → 0.8 % |
| CDK1000 | 321 ms | 47 ms | 6.8× | 8.16e-3 → **3.18e-4** | 1.7 % → **0.8 %** | 0.5 % → **0.4 %** |
| FORS2 | 1464 ms | 187 ms | 7.8× | 8.23e-2 → **2.39e-3** | 119.6 % → **85.9 %** | 405 % → **13.0 %** |
| SPHERE | 60 ms | 12 ms | 5.0× | 1.62e-2 → **9.35e-4** | 5.0 % → **3.7 %** | 3.6 % → 3.5 % |
| WFC3/UVIS | 3738 ms | 1474 ms | 2.5× | 1.27e-3 → **9.30e-5** | 0.4 % → 0.4 % | 0.4 % → **0.1 %** |
| WFC3/IR | 4887 ms | 1988 ms | 2.5× | 8.02e-4 → **6.53e-5** | 0.2 % → 0.2 % | 0.2 % → 0.2 % |

`tools/spacecraft-tests` sees the same thing from the other side. It reproduces WFC3 IHB Table 6.7
by measuring the finished kernel's own FWHM, and one of its nine rows moved: **1000 nm, 0.0883316″
→ 0.0891814″**. Sampling the same central row at rising node counts says which is right, the
measurement converges from below to **0.0892373″**, so the old number was 1.0 % low and the new one
is 0.06 % low, sixteen times closer. It sits marginally further from the handbook's 0.084″ ± 0.006,
which is the correct outcome and not a regression: HST is diffraction-limited past about 900 nm, and
the harness's own note on the 1100 nm row already says the table's long-wavelength entries fall
below the aperture's limit.

So the kernel is cheaper **and** closer to the converged answer, and not only at the peak: every
column above is better than or equal to the one it replaces, on every instrument. Nothing was
traded.

## What was measured and rejected

Two savings were real, and neither is in the mod, because both trade accuracy for time and this is
an instrument simulator. Both are recorded with their numbers so that nobody has to rediscover
them, and so that the decision can be revisited on evidence rather than on taste.

**Halving the node count in the wings: 5.6× on Hubble's kernel, rejected.** Beyond 16 px the
kernel that is actually convolved is the weighted sum over twelve sub-bands, and a ring at 400 λ/D
moves more than a hundred periods across the passband, so the sum has already averaged the rings
away and a finer average per band resolves structure that cancels. Measured, the argument holds
where it claims to: `max|d|/peak` is unmoved (9.3e-5 against 9.6e-5), because the far wings carry
almost none of the weight. What it costs is the **diffraction spikes**, whose relative error goes
from 0.4 % to 1.8 % on WFC3/UVIS and from 0.2 % to 4.9 % on WFC3/IR. That bound is empirical
rather than derived, and the spikes are the reason the support was widened to 257×257 in the first
place, so the wings keep their full node count. Hubble's kernel is 2268 ms rather than 405 ms at
4×4 as a result.

**Tabulating the pupil transform: 1.82×, rejected.** `PupilDiffraction.Intensity` costs ~19
transcendental calls, and replacing the disc transforms, the sinc factors and the pad phases with
linearly interpolated tables takes it from 185 ns to 94 ns, for 5.3 MB of tables and a 0.155 %
error on the pattern. That is five orders of magnitude above the 6.7e-16 at which the vane-free
pupil currently reproduces `OpticalPsf.AiryIntensity`, which is the reducibility standard
`tools/bandpass-wcs-tests` holds this class to. What was kept is the exact part: Hubble's three
mirror pads share a radius, so they share one Bessel evaluation.

Two **exact** speedups remain unspent, if the time is ever wanted without giving anything up. The
twelve sub-bands are parallelised one band per worker, so nine workers run them in two rounds at
about 67 % occupancy; flattening that to (band, row) work items would recover roughly 1.5× and
each output cell is still written by exactly one worker, which is the rule in `Core/ParallelWork.cs`.
And the radial part of the amplitude, the annulus and the pads, which depend only on |u|, could
be tabulated with **cubic** interpolation at about 80 samples per ring period for an error near
1e-9, three orders below the accuracy standard the linear table failed; that is the same
tabulate-and-interpolate discipline `SampleRadial` already uses, applied where it is still exact
enough to pass the reducibility check.

## What it does not fix

**FORS2 binned 4×4 has aliased wings, and had them before.** Its pixel spans eighteen ring
periods, so the grid's ceiling of 12 nodes is less than one node per period and the spike arms are
sampling noise either way (85.9 % against the reference now, 119.6 % before). Sampling that
honestly needs about 72 nodes per axis over the whole grid, thirty-six times the work, to recover
structure carrying 1e-8 of the light. The core is fixed, 8.23e-2 of the peak down to 2.13e-3,
because that is where the light is and where the high node cap now applies.

## Run

```
dotnet run -c Release -p:Core=../../ExoInstruments/Core
```

| option | what it does |
|---|---|
| `--symmetry` | check every reflection the sampler folds on against the pattern itself, on every pupil |
| `--solve` | replay each bounded FWHM solve against a full-support bisection, over the roster × 3 binnings × 12 sub-bands |
| `--convolve` | check the transform-based kernel convolution against the direct sum it replaces |
| `--bin N` | report one binning instead of 1, 2 and 4 (repeatable) |
| `--pupil NAME` | restrict to one instrument, for iterating |

`--symmetry`, `--solve` and `--convolve` exit non-zero on failure.

`--solve` is slow on purpose: the reference side builds exactly the full-support kernels the
shipped path now avoids. Expect several minutes.

## Two notes on what "agrees" means here

**The solve agrees to within its own last bisection step, not bit for bit.** The measurement reads
a float32 kernel that `Normalise` has divided by its own total, and a smaller support has a
different total, so the two rows differ in the last bits of float32, about 1e-7 relative. Where a
bisection midpoint sits within that of the target the comparison can fall the other way, and the
answer lands one step of the bracket away: 24 halvings, so ~5e-8 arcsec against a width of order an
arcsecond. Neither answer is the more correct one.

**The mirror symmetries hold to 1e-10, and the fold is exact anyway.** `Math.Cos(pi/2)` is 6.1e-17
rather than 0, so a vane nominally along y carries an x-component of that size and the reflected
intensity differs in the tenth decimal. The fold does not inherit that: it computes one octant and
copies it, so the finished kernel is exactly symmetric, which is the pupil's real property. A pupil
that genuinely lacks the symmetry disagrees by four orders of magnitude, not by 1e-10, which is
what the check is there to separate.
