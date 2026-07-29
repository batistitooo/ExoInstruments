# Where black and white are

A display transfer curve -- log, asinh -- decides how the range between black and white is
distributed. It does **not** decide where black and white are, and on an astronomical frame that is
the larger question by far.

40 s on the Elephant's Trunk with the RedCat puts about 73 electrons in the brightest pixel: 18 ADU
of a 16383-count converter. Map the converter's whole range to the display and the entire subject
occupies **0.4 of 255 display levels**. No curve applied afterwards recovers contrast that was never
allocated, and the result is a uniform grey fog -- which is what the frame genuinely looked like
before this existed.

zscale (Tody 1986, SPIE 627, 733) is how IRAF, DS9 and their descendants answer it, and it is not a
percentile clip: the samples are sorted, a line is fitted to them against their rank with iterative
rejection, and the limits come from extrapolating the **sky's own slope** across the pixel count.
Most pixels on an astronomical frame are sky, so the middle of that sorted array is a long shallow
stretch whose slope measures the noise; the sources are the steep tail and the rejection removes
them. That is why one saturated star cannot flatten the image, which is exactly what a maximum- or
high-percentile-based clip does.

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core
python3 -m venv env && ./env/bin/pip install numpy astropy
./env/bin/python compare_zscale.py
```

## Results

Against `astropy.visualization.ZScaleInterval`, the same algorithm implemented independently:

| frame | agreement |
|---|---|
| faint nebula on a sky pedestal | 0.00% of the displayed span |
| star field with saturated stars | 0.16% |
| flat field | 0.00% |
| bright planet | 0.05% |
| linear gradient | 0.00% |

On the faint frame it is a **697x stretch**: the subject goes from 0.4 of 255 display levels to all
255. On the star field the white point stays at 0.0026 of full scale while the frame reaches 1.0 --
the saturated stars are rejected from the fit rather than setting the scale.

## The other half: an extended subject

zscale finds the sky beautifully, and sets the white point from the sky's own noise on the
assumption that **sources are a small minority of pixels**. A nebula filling a third of the frame
breaks that outright. On a 40 s exposure of M42 the emission spans 34 to 5116 rayleighs; zscale's
limits stop at 329, so an eighth of the frame clips to flat white and the nebula becomes a
featureless polygon -- the shape of an iso-contour rather than of a nebula.

The two halves of the question therefore get different answers. The **black** point still comes from
zscale. The **white** point comes from the 99.5th percentile of a block-MEDIAN copy of the frame:
a median over 64 pixels is untouched by anything covering fewer than 32 of them, so a star vanishes
completely while a nebula, which fills the block, is unchanged. The white point ends up set by the
brightest *extended* structure and never by a star -- which is right on physical grounds, since a
stretch exists to show structure and a point source has none, and is also what every real
astrophotograph does.

A block *mean* is not enough: it still carries a saturated star divided by the block area, which on
a star field set the white point seven times too high and compressed the sky again.

| frame | zscale alone clips | extended-source limits clip |
|---|---|---|
| faint nebula | 0.0% | 0.0% |
| star field | 0.0% | 0.0% (identical limits) |
| **bright nebula** | **12.1%** | **0.6%** |
| bright planet | 4.2% | 1.8% |
| linear gradient | 9.8% | 1.0% |
