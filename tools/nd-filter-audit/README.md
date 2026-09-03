# nd-filter-audit

Whether the neutral-density roster is the right one, answered by computing what a bright resolved
target actually puts in a pixel.

## The report that prompted it

> On Jupiter or Saturn I am almost always on ND1000 and it is still over-exposed, but with the solar
> filter nothing is visible any more. There should be something in between, if that is realistic.

Both halves are reproduced exactly, and the audit says which part is the filter's fault and which is
not.

## Method

A resolved planet is an **extended source**, so what fills a pixel is its surface brightness, not its
integrated magnitude. That makes `SkyBrightnessModel.ElectronsPerPixelPerSecond`, the shipped
function the pipeline already uses for the night sky, which takes exactly a V surface brightness,
the correct and unmodified tool. Nothing is reimplemented: bandpass, throughput, QE curve, aperture
area, ADC and gain all come from the shipped `Core` and `VisualTelescopeCatalog`.

Target surface brightnesses are derived, not quoted, as `mu = V + 2.5 log10(pi a b)` with the real
oblate semi-diameters:

| target | V | semi-diameters (″) | area (□″) | mu (V/□″) |
|---|---|---|---|---|
| Sun | −26.74 | 959.63 | 2 893 060 | **−10.59** |
| Moon, full | −12.74 | 932.58 | 2 732 260 | **3.35** |
| Jupiter, opposition | −2.70 | 23.45 × 21.93 | 1616 | **5.32** |
| Saturn globe, opposition | +0.60 | 9.73 × 8.72 | 267 | **6.66** |
| Mars, opposition | −1.98 | 8.94 × 8.89 | 250 | **4.01** |
| Uranus, opposition | +5.57 | 1.83 × 1.79 | 10.3 | **8.10** |

Magnitudes are from **Mallama & Hilton (2018)**, *Astronomy and Computing* **25**, 10, "Computing
apparent planetary magnitudes for The Astronomical Almanac"
([arXiv:1808.01973](https://arxiv.org/abs/1808.01973)), the model The Astronomical Almanac itself
uses. Jupiter's mean opposition V of −2.70 is their value from both the semi-major-axis estimate and
the analysis of daily magnitudes (σ = 0.17). Saturn is taken from their Eq. 10 with the **globe-only**
`V1(0) = −8.95` that Mallama (2012) derived from the 1995 ring-plane crossing: their catalogue mean
opposition magnitude of +0.05 includes the rings, whose area is four times the globe's, so it is the
wrong number for a per-pixel surface brightness of the disk. The solar V is Willmer (2018), *ApJS*
**236**, 47. Radii are the IAU/IAG working-group values.

## What it found

**1. The filters themselves are right. The exposure is not.**

RC20, Jupiter at opposition, Luminance, zenith:

| configuration | e⁻/px/s | saturates at | time to saturate |
|---|---|---|---|
| 1×1, gain 1 | 1.60×10⁶ | 65 992 e⁻ (ADC) | **41.4 ms** |
| 4×4, gain 1 | 2.55×10⁷ | 65 992 e⁻ (ADC) | **2.58 ms** |
| 4×4, gain 8 | 2.55×10⁷ | 8 244 e⁻ (ADC) | **323 µs** |

At 1×1 and gain 1 the correct exposure for Jupiter is **10-20 ms with no filter at all**, which is
exactly what real planetary imaging does: lucky imaging acquires thousands of sub-30 ms frames and
stacks the sharpest, and no neutral-density filter appears anywhere in that workflow. The mod already
reproduces that regime correctly; its shortest exposure is 32 µs, three decades below what Jupiter
needs.

**The camera opens at 0.5 s and 4×4 binning.** That is 194× too long for Jupiter at 4×4, and it is
the whole of the problem: ND is being used to undo a default, not to solve a physical one.

**2. Binning costs dynamic range twice over, and that is real.** A 4×4 binned pixel merges 16 wells,
so the well grows to 1 056 000 e⁻, but on-chip binning sums charge ahead of one amplifier and one
converter, so the ADC ceiling stays at 65 992 e⁻. The signal rises 16× and the ceiling does not move.
Combined with gain 8, which divides the ceiling by another 8, the default configuration throws away a
**factor of 128** of headroom against 1×1 at gain 1. The code models this correctly and says so
explicitly in `DigitalSaturationElectrons`; nothing in the interface warns that it is happening.

**3. The gap between ND1000 and the solar film is real, and it is exactly where the report lands.**
RC20, 4×4, gain 8, Jupiter, at the default 0.5 s:

| filter | charge | fraction of full scale |
|---|---|---|
| ND1000 (OD 3.0) | 12 765 e⁻ | **saturated** (ceiling 8 244 e⁻) |
| solar film (OD 5.0) | 128 e⁻ | **0.2 %** |

A hundredfold step with nothing between it, the largest jump on the ladder by a factor of eight.

## What was changed

One stop added: **`Nd6300`, optical density 3.8**, labelled `OD3.8` in the filter row.

This is not an interpolation invented to fill a gap. It is **Baader's AstroSolar PHOTO Film**, a real
product: the same optically-treated carrier film as the OD 5.0 AstroSolar Safety Film, with the
density of the adjacent coatings reduced in a controlled fashion, sold expressly for digital solar
imaging at high magnification and short exposure, and expressly **not** for visual use at any
magnification, in combination with any other filter. Baader quote Strehl ratios of 94-96 % on
interferometric test, so it is genuinely a photographic-quality optic rather than a light blocker.
Sources: [AstroSolar technical information](https://astrosolar.com/en/information/about-astrosolar-solar-film/astrosolar-technical-info/),
[AstroSolar PHOTO Film OD 3.8](https://astrosolar.com/en/products/whitelight/astrosolar-photo-film-20x30cm-od-3-8/).

With it in place, the RC20/4×4/gain 8/Jupiter/0.5 s case resolves to `OD3.8` instead of jumping to the
solar film, and the Moon at 4×4 gain 1 does too.

## What was deliberately not changed

The default exposure of 0.5 s and the default 4×4 binning. Both are interface decisions with
consequences for every other target in the game, a 0.5 s default is roughly right for a faint moon
and wrong by two orders of magnitude for Jupiter, and picking between them is a design call, not a
physics one. What the audit establishes is that **no filter change can substitute for it**: the ND
ladder is being asked to compensate for a 194× exposure error, and it can only do that in 100× steps.

The honest fix, and what a real observer does, is the other way round: shorten the exposure, drop the
gain, unbin, and reach for ND only on the Sun.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
```

Writes `nd_filter_audit.txt` (the full report, every instrument × binning × gain × target) and
`nd_filter_audit.csv` (the same as data). No Python, no game.
