# Does the calibration chain calibrate?

A flat field and a bias frame are worth taking only if two things are true: what they measure is
really in the light frames, and dividing or subtracting them really takes it out. Neither is
self-evident from reading the code, because both are statements about the COMPOSITION of stages
written far apart: a map built once per sensor, a multiplication applied before a Poisson draw, an
addition applied before a converter.

This harness closes that loop numerically, then puts the result beside ESA's Pyxel.

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core -- --out .
./env/bin/python compare_pyxel.py
```

The venv needs `pyxel-sim` and `numpy`. Both generated dumps (`exo_*.bin`) and the venv are
git-ignored; the small CSVs are not, so the tables below can be diffed between runs.

## What the C# side establishes

| section | what it settles |
|---|---|
| 1 | The maps carry the catalogue's published numbers, and their mean is zero rather than nearly zero. |
| 2 | Binning divides the photo-response spread by n and multiplies the offset spread by n, so their product is invariant. |
| 3 | The illumination geometry, per instrument, from each one's own focal length and format. |
| 4 | The linearity model inverts itself to machine precision, so a reduction built on the inverse undoes exactly what the forward model did. |
| 5 | ESO's own FORS2 bias QC1 decomposition, run on a simulated bias, returns the numbers that went in. |
| 6 | A stack of lights, reduced with masters, lands on the photon-noise floor; with the flat step omitted it does not. |

Two results are worth reading even when everything passes.

**FORS2 is 62% illuminated.** The MIT mosaic spans 8.5 arcminutes and the MOS unit stops the field
at 6.8, so more than a third of the detector never sees the sky. It is the only instrument on the
roster whose detector is larger than its illuminated field, and the only one where the flat is
dominated by the optics rather than by the silicon.

**The ASI294MM Pro is quantisation-limited in a bias frame.** Section 5 recovers 0.413 ADU of
read-out noise where the catalogue's 1.2 e- is only 0.298 ADU, because the converter's own
truncation contributes 1/sqrt(12) = 0.289 ADU on top. The model is right and the camera is being
run at a gain where the read noise does not span a count. That is a real operating regime with a
real remedy (more gain), and it is worth knowing that this catalogue entry pairs ZWO's high-gain
read noise with a conversion factor derived at unity gain; the two are quoted at opposite ends of
the camera's gain range.

## What the comparison against Pyxel establishes

Pyxel (pyxel-sim, European Space Agency) is an open, published, actively maintained end-to-end
detector simulation framework, and the reference this pipeline's own comments already measure
themselves against. `compare_pyxel.py` runs both implementations on the same numbers and computes
the same statistic on both with the same code. Every verdict it prints is derived from a
measurement made in the script; none is asserted.

At the time of writing, on the subset of effects both implement: **6 better, 1 equal, 2 worse**.

Ahead on:

* **PRNU parameterisation.** Ours has unit mean to 2e-9 and its parameter is the sensor's published
  EMVA 1288 figure. Pyxel's parametric path builds `QE * (1 + lognormal(sigma))`, whose mean is
  about `2 * QE`: asking it for a 0.62% spread and applying it multiplies the frame by 2.00. Its
  `fixed_pattern_noise_factor` is also not a relative sigma, so reaching a datasheet's number
  requires solving for it.
* **PRNU under binning.** The roster's amateur camera is a 2x2 hardware bin of its sensor, so a
  figure quoted against the wrong pixel is wrong by a factor of two. Pyxel has no binning law.
* **Offset FPN.** Pyxel's `dc_offset` adds one DC voltage to the whole array; its per-pixel fixed
  patterns come from `nghxrg`, which is specific to HxRG infrared arrays. A CCD bias frame with no
  spatial structure is a constant.
* **Vignetting.** Pyxel's `illumination` places a uniform, rectangular or elliptic patch. Ours
  computes cos^4 from each instrument's published focal length, which is what makes a 250 mm
  astrograph lose 0.43% at the corner where a 24 m one loses 0.0006%.
* **Non-linearity usability.** One published deviation in, curve out, exact inverse supplied.
* **Closing the loop.** Pyxel is a forward simulator and ships no reduction path.

Behind on:

* **Measured PRNU maps.** `fixed_pattern_noise(filename=...)` loads a real per-pixel flat. Nothing
  here can. No measured map is published for any detector on this roster, so nothing is lost today,
  but the capability is absent.
* **Non-linearity generality.** Pyxel fits an arbitrary polynomial and models the physical
  mechanism for MCT arrays. This is a single quadratic.

Level on:

* **Digitisation.** Both clip and both truncate, and they agree exactly on 201 levels spanning the
  full range.

None of this is a claim about the two codebases. Pyxel is a general framework covering detector
families this mod has no instrument for, and most of what it offers has no counterpart here and is
not counted against either side.
