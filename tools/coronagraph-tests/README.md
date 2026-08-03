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
| ADI throughput calibration | worse | VIP measures throughput by injecting and recovering fake companions; ours is analytic with declared limits |
| Post-processing algorithms | worse | VIP implements PCA/KLIP, LOCI, LLSG, NMF, ANDROMEDA; this models median-subtraction ADI only |
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
