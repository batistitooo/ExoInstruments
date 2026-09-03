# photometry-roundtrip

Stars of known magnitude go in, a frame comes out, and **photutils** has to get the magnitudes back.

Every other harness under `tools/` checks one mechanism against a reference. This one checks that the
mechanisms **compose**. A zero point that disagrees with the electron counts, a PSF that quietly
loses flux, a gain applied twice, an aperture correction taken from the wrong profile, none of
those is visible to a test that looks at one stage alone, and all of them show up here.

It also settles a disagreement the codebase names about itself. `Core/CcdEquation.cs` says outright
that the transit half and the imaging half "disagreed about what an instrument is". Section 3 renders
128 independent noise realisations of one field, measures the real scatter of real aperture
photometry, and compares it against the precision `CcdEquation` predicts for that same observation.

## What is real and what is replicated

Real, called unmodified from the shipped `Core`: `PhotonFluxModel`, `SystemBandpass`,
`SkyBrightnessModel`, `DarkCurrentModel`, `OpticalPsf.BuildKernel`, `Pcg32`, `NoiseSampler`, and the
instrument parameters from `VisualTelescopeCatalog`.

Replicated: the last four lines of `RunDetectorChain` (pedestal, divide by K, floor, clip), because
that method lives in the Unity layer. They are four lines of arithmetic, written out in full, and
every constant they use comes from `VisualTelescopeSpec`.

Deliberately absent: cosmic rays, hot and dead pixels, blooming and charge-transfer smear. Each is a
localised artefact whose job is to damage pixels; photometry of a clean field is what establishes the
chain, and the artefacts have their own checks in `TESTING.md`.

`Core/NoiseSampler.cs` is new. The Poisson and Gaussian deviates used to be private statics inside
`SolarSystemCameraTexture`, which is Unity-dependent, so the two most consequential numerical
routines in the pipeline were the only ones no harness could reach. Moving them to `Core` changes no
behaviour (the call sites delegate) and makes them testable.

## Results

Configuration: RC20 + ASI294MM Pro, 60 s, 0.2754″/px, seeing 2.45″, sky 21.7 V/□″, gain 1, 1×1.

**1. The noise deviates are the distributions they claim to be.** `NoiseSampler.Poisson` against
SciPy's exact pmf by binned chi-square, at λ = 0.05, 0.5, 2, **9.9, 10.0, 10.1**, 50, 1000 and
150 000, the three middle values bracket `PtrsThreshold`, where the sampler switches from Knuth's
product method to Hörmann's PTRS, and where a bug would hide without ever throwing. Sample mean
within 0.7 % and variance within 0.8 % of λ everywhere; every chi-square p-value in [0.23, 0.90].
The Gaussian passes Kolmogorov-Smirnov at p = 0.43 with σ recovered to 0.11 %.

**2. The magnitudes come back.**

| check | result |
|---|---|
| median residual, colour term removed | **1.4 mmag** |
| residual spread across 7 magnitudes | 7.0 mmag |
| recovered-vs-injected slope | 1.00067 |
| PSF flux conservation in the total-flux aperture | 0.99867 |

The colour term is not fitted out; it is predicted. `MAGZERO` is quoted for a **flat** source
spectrum, the injected stars are solar, and the two effective widths (1805.2 Å flat, 1721.9 Å solar)
give an offset of exactly **+0.0513 mag**. Removing that predicted value leaves 1.4 mmag.

**3. `CcdEquation` is right; its aperture correction is not.**

| V | measured σ | predicted (as shipped) | ratio | predicted (real encircled energy) | ratio |
|---|---|---|---|---|---|
| 12 | 0.000764 | 0.000718 | 1.063 | 0.000788 | 0.969 |
| 13 | 0.001321 | 0.001140 | 1.159 | 0.001252 | 1.055 |
| 14 | 0.001839 | 0.001814 | 1.014 | 0.001994 | 0.922 |
| 15 | 0.002960 | 0.002904 | 1.019 | 0.003197 | 0.926 |
| 16 | 0.005828 | 0.004714 | 1.236 | 0.005214 | 1.118 |
| 17 | 0.008687 | 0.007898 | 1.100 | 0.008820 | 0.985 |
| 18 | 0.017573 | 0.014074 | 1.249 | 0.015983 | 1.099 |
| 19 | 0.030907 | 0.027546 | 1.122 | 0.031946 | 0.967 |

As shipped, the equation is **11.1 % optimistic** in the median. Substitute the encircled energy
measured from the mod's own PSF and it comes to **0.977**, inside the 6.3 % sampling error that 128
realisations allow, and flat from the source-limited to the sky-limited end (spread 0.195 against a
4σ allowance of 0.25).

So the published Merline & Howell equation is implemented correctly and the electron budget the
imaging half renders from is the same one the transit half predicts with. The single error is the
aperture correction:

> **`CcdEquation.GaussianEnclosedEnergy` returns 0.7225 at its 0.68-FWHM aperture. The real
> Airy-convolved-Kolmogorov kernel puts 0.6000 there, the Gaussian is 20.4 % optimistic.**

`CcdEquation.cs` flags this itself as the file's only assumption and says the exact number "is
computable from what this codebase already has". It is: this is the number, and it is worth more than
the 20 % suggests, because the Gaussian's error is one-sided. A real long-exposure PSF follows
Kolmogorov's θ^(−11/3) wing, which carries more flux outside any radius than a Gaussian does, so the
Gaussian will always overstate what an aperture holds.

Two caveats on the 0.6000, both in the same direction:

- It is measured from the **mod's own kernel**, which `tools/galsim-crossvalidation` shows is itself
  about 5.5 % too core-concentrated on this instrument. The true figure is lower still.
- It is specific to this plate scale and seeing. The right fix is to integrate `OpticalPsf`'s kernel
  rather than to hard-code 0.60.

## One thing this harness had to get right to be fair

The background is the annulus **mean**, not its median. Merline & Howell's `(1 + n_pix/n_B)` factor
is derived for a sky level obtained by *averaging* n_B pixels; the median of the same pixels has π/2
times the variance. Measuring with a median and comparing against the equation attributes a 57 %
inflation of the background term to the pipeline when it belongs to the estimator. It matters
doubly here because the annulus values are coarsely quantised, the sky is 8.7 ADU per pixel at
K = 4.03 e⁻/ADU, and the median of a coarsely quantised sample is itself nearly quantised, while
the mean of 575 of them is not. Using the median moved the V = 18 point from 1.10 to 1.39.

## What this does NOT establish

- **One instrument, one configuration.** RC20, Luminance, zenith, 60 s, gain 1, 1×1. The other three
  instruments and the binned and high-gain regimes are not covered.
- **No flat field**, because the pipeline has none. Pixel response non-uniformity is the dominant
  systematic on a bright star in real ground-based photometry, so the bright end here is optimistic
  for a reason this harness cannot measure.
- **A clean field.** No cosmic rays, defects, blooming or charge-transfer smear.
- **Nothing about the sky model's absolute level, nor the zero point's absolute scale.** The zero
  point is checked for *self-consistency*, the frame agrees with the header that describes it. Whether
  948 photons/cm²/s/Å at 5556 Å is the right anchor is a separate question, and `synphot` against the
  CALSPEC Vega spectrum is the tool for it.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
python -m venv env && ./env/bin/pip install numpy scipy astropy photutils
./env/bin/python roundtrip.py
```

Exit code 0 when every check passes, 1 otherwise. Writes 128 frames (64 MB) as raw little-endian
uint16, plus `truth.csv`, `meta.csv` and `ccd_equation_prediction.csv`; all are gitignored.

Verified against photutils 1.11.0, astropy 6.0.1, SciPy 1.13.1.
