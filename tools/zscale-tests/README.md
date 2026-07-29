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
