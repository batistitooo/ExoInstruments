# Is this survey's data usable as a shape map?

`pack_galaxy_images.py` turns survey imagery into normalised shape maps, and every one of them is
multiplied by a catalogued flux and laid into a frame. That only means anything if the pixel values
are proportional to surface brightness. Nothing in a FITS header promises that, and two services
that both return "the same survey" can disagree about it.

These are the two tools that decide, and they are kept rather than run once, because the next survey
added to the packer has to pass them too.

## `check_transfer.py` — the transfer curve, against the survey's own stack

Compares a HiPS cutout against the Pan-STARRS stack image of the same sky, pixel by pixel. A linear
service traces a straight line whatever its units are; a compressed one bends, hardest exactly where
a galaxy's nucleus lives.

```
./env/bin/python check_transfer.py --ra 189.9976 --dec -11.6231 --arcmin 8
```

Run it over an object with real dynamic range. The Sombrero spans four decades of its own light,
which is what makes the bend unmistakable:

| service | curve (reference / service) | verdict |
|---|---|---|
| Pan-STARRS HiPS g | 39.7/7.07 · 201/173 · 1.04e3/1.01e3 · 2.62e4/2.63e4 · 1.49e5/1.42e5 | linear |
| Pan-STARRS HiPS r | 40.8/0.119 · 207/0.777 · 1.06e3/2.16 · 2.66e4/5.55 · 1.51e5/7.18 | **asinh** |
| Pan-STARRS HiPS i | same shape as r | **asinh** |
| Legacy DR10 g | 39.7/2.4e-4 · 201/0.028 · 1.04e3/0.133 · 2.62e4/2.28 · 1.49e5/11.7 | linear |
| Legacy DR10 r | 41.2/8.9e-4 · 209/0.061 · 1.07e3/0.298 · 2.68e4/5.32 · 6.43e4/10.9 | linear |

Five decades of flux compressed into a factor of sixty is an asinh transfer. Packed as a shape map
it would have flattened every nucleus and lifted every outskirt, and the result would still have
looked like a galaxy. So the packer does not use the Pan-STARRS r and i HiPS; it goes to the survey's
own stack service for those bands, and falls back to the g HiPS alone where the box is too large to
fetch at 0.25″.

## `check_linearity.py` — the same question against a catalogue

Aperture photometry on the image against Pan-STARRS catalogue magnitudes in the same band. A linear
image gives a straight line of slope one. This is the weaker of the two — with a few dozen stars the
scatter is a few tenths of a magnitude and a mild compression hides in it — but it needs no
reference image, so it is the one to reach for over a survey the stack service does not cover.

```
./env/bin/python check_linearity.py --ra 195.0 --dec 20.0 --fov 0.25 --gmin 16 --gmax 20
```

## What is NOT tested here

Clipping. A service can be perfectly linear and still return a flat plateau where the data was cut
off: Legacy DR10's r HiPS does exactly that over the Sombrero's nucleus, eleven pixels wide, which
moves no global statistic and takes 5.1 % of the galaxy's light down to 1.6 %. That one is caught
inside the packer, from the fact that real floating-point sky data essentially never repeats a value
exactly.
