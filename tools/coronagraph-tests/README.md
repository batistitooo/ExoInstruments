# Does the high-contrast chain reproduce the instrument ESO measured?

`Core/Coronagraph.cs`, `Core/SpeckleField.cs`, `Core/AngularDifferentialImaging.cs` and
`Core/ContrastCurve.cs` together claim to describe SPHERE/ZIMPOL well enough that a detection limit
computed from a simulated frame means something. That claim is checkable three independent ways,
and this harness does all three.

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core -- --out .
./env/bin/python compare_vip.py
```

The venv needs `vip_hci` and `scipy`. It and the generated frame (`exo_*.bin`) are git-ignored.

## 1. Against numbers ESO published that were not used to build the model

**The Lyot stop's transmission.** Its three dimensions are read from Schmid et al. (2018) Table 9
in millimetres of an internal pupil image and scaled to the telescope by one number from the same
table. The *same* table separately publishes the stop's geometric transmission, which is a
different measurement of the same object. Computing it from the scaled dimensions gives **74.7 %**
against ESO's published **72.6 %**. Three independently read dimensions reproducing a fourth
published number to 2.1 points means the table has been read correctly.

The stop turns an 8.2 m aperture with a 14.0 % central obstruction into a **7.42 m aperture with a
22.2 % one**, with spider vanes six times wider. That is a different point-spread function, not a
brightness correction, and it is why `SolarSystemCameraTexture` now builds its PSF from the pupil
the light last passed through rather than from the telescope's own.

**The speckle ring.** The AO control radius is computed from SAXO's 41×41 actuator count as
(N/2)·λ/D, which at 626 nm on 8.2 m is **323 mas**. Schmid et al. report the observed speckle ring
at **0.3 to 0.4 arcsec**. Nothing about the ring was an input.

**The mask attenuations** reproduce the paper's prose spans exactly, because they are ratios of its
own Table 8 counts: CLC-S-WF 111–150 against a quoted "110–150", CLC-M-WF 307–601 against
"300–600", CLC-XL-WF 1064–2894 against "1000–3000".

## 2. Against the statistics the physics demands

| check | result |
|---|---|
| Modified Rician mean and variance, 2 M draws, four static fractions | matches the closed form to 0.2 % |
| Averaging *n* realisations divides the variance by *n* | exact to 1 % at n = 4, 16, 64, 256 |
| The temporal decomposition sums to one | 0.713 + 0.059 + 0.228 |
| Atmospheric lifetime 0.6 D/v at 3–4 m/s | 1.64 s and 1.23 s, against Milli et al.'s "≤ 1.6 s" |
| An hour of exposure still carries the whole static term | 0.7131 against a floor of 0.713 |
| Student t threshold → Gaussian 5 for a large sample | 5.0035 at 10 000 elements |

The survival table is the point of the whole file:

| exposure | surviving speckle variance |
|---|---|
| 1 s | 1.0000 |
| 10 s | 0.7617 |
| 60 s | 0.7211 |
| 1 hour | 0.7131 |

Integrating for an hour instead of a minute removes **1 %** of the speckle noise. That is the wall,
and it is why ADI exists.

## 3. Against VIP

VIP (`vip_hci`) is the package high-contrast papers compute their detection limits with, and its
`contrast_curve` implements the Mawet et al. (2014) small-sample correction directly.
`compare_vip.py` runs both on the same pixels.

| comparison | verdict | detail |
|---|---|---|
| Small-sample threshold | **equal** | our Student t quantile, built on a continued-fraction incomplete beta, reproduces SciPy's `t.ppf` at a tail probability of 2.87e-7 to **4.4e-8 relative** |
| Annulus noise estimator | **equal** | median ratio **1.029** over 14 annuli, scatter 0.027 — same construction, different aperture phase |
| Contrast curve end to end | **equal** | agrees to **0.095 mag at worst** over 14 separations |
| ADI throughput calibration | **equal** | both inject a companion at fixed S/N and recover it after the reduction; ours is §D3 below, and it is what bounded the analytic form |
| Post-processing algorithms | worse | VIP implements PCA/KLIP, LOCI, LLSG, NMF, ANDROMEDA; this runs median-subtraction ADI and nothing else |
| Forward instrument model | better | VIP starts from a cube it is given; this produces the cube |

## Why the small-sample penalty is the headline

"Five sigma" is not five sigma close to the star, and a curve computed without the correction
overstates the achieved contrast by more than a magnitude exactly where the instrument was built to
look:

| separation | resolution elements | threshold | penalty |
|---|---|---|---|
| 1 λ/D | 6 | 34.40 σ | **6.88×** |
| 2 λ/D | 12 | 10.68 σ | 2.14× |
| 5 λ/D | 31 | 6.42 σ | 1.28× |
| 20 λ/D | 125 | 5.30 σ | 1.06× |
| 100 λ/D | 628 | 5.06 σ | 1.01× |


---

# ADI, run rather than parameterised

`SelfSubtractionThroughput` was an **analytic form** — a declared shape with the right limits,
checked against one published data point from a *three-frame* median. `AngularDifferentialImaging`
now carries the reduction itself, so the throughput can be measured the way VIP measures it: inject
a companion of known flux, reduce, recover it.

Injected at a **fixed signal-to-noise** (VIP's `fc_snr`, default 100), because a median is not
linear: how much of a companion it absorbs depends on how far that companion stands above what it is
medianed with.

## The measured curve

150 mas (6.8 λ/D), 21 frames, one resolution element of travel costing 8.39°:

| rotation | arc (λ/D) | **measured** | analytic n/(n+1) |
|---|---|---|---|
| 1° | 0.12 | **0.018** | 0.106 |
| 3° | 0.36 | **0.051** | 0.263 |
| 6° | 0.71 | **0.105** | 0.417 |
| 12° | 1.43 | **0.290** | 0.588 |
| 30° | 3.57 | **0.842** | 0.781 |
| 90° | 10.72 | **0.979** | 0.915 |

The declared form is **substantially too optimistic below ~3 λ/D of arc** and slightly pessimistic
above it. §12 item 66 is rewritten accordingly.

**A variable the form does not have:** at a fixed 12° of rotation the throughput still depends on
frame count — 0.514 at 3, 0.391 at 7, 0.323 at 15, 0.271 at 31. The direction contradicts the
obvious guess. Reported rather than fitted.

## What the reduction buys

| separation | one frame | stacked ×21 | ADI | gain |
|---|---|---|---|---|
| 100 mas | 2.82×10⁻³ | 2.90×10⁻³ | **5.51×10⁻⁵** | 4.30 mag |
| 150 mas | 1.99×10⁻³ | 2.05×10⁻³ | **3.03×10⁻⁵** | 4.58 mag |
| 220 mas | 3.93×10⁻⁴ | 3.81×10⁻⁴ | **9.98×10⁻⁶** | 3.95 mag |
| 300 mas | 2.16×10⁻⁴ | 2.16×10⁻⁴ | **4.98×10⁻⁶** | 4.09 mag |

**Stacking 21 frames does nothing** — the third column is the first — and ADI buys four magnitudes.
That is the 71 %-static speckle field made a measurement rather than an assertion.

## Two things caught by failing

A companion injected beyond the frame's half-width (346 mas) is outside the detector; the first
version reported a throughput of exactly zero at 500 and 700 mas and thought it had measured
something. And sweeping *separation* rather than *rotation* confounds the variable under test with
the halo profile — the arc length is what the form is a claim about.
