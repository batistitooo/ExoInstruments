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

Aperture photometry on the service's own cutouts against the Gaia DR3 Synthetic Photometry
Catalogue (I/360): all-sky, star-only, SDSS-system ugriz. A linear image gives a straight line of
slope one; an asinh transfer fails on slope alone (0.42-0.59). It needs no reference image, so it
is the one that works over surveys the stack service does not cover, and it is what admitted SDSS
DR9 g/r/i/z and DES DR2 g/r into the packer and kept SDSS u and DES i out.

```
./env/bin/python check_linearity.py --ra 130 --dec 10 --fov 0.3 --gmin 16
```

Three lessons paid for while rebuilding it, kept so they are not paid again:

* The old reference (the Pan-STARRS catalogue) stopped at Dec -30 and included galaxies, which
  buried every verdict under a magnitude of scatter. A reference for this job must be star-only.
* The fit must carry a colour term (g-r for blue bands, r-i for red). The reference is SDSS-system;
  DECam and Pan-STARRS bands differ from it by a colour-dependent offset, faint field stars are
  systematically redder, and without the term the offset masquerades as curvature: every non-SDSS
  survey failed, including the two the packer demonstrably relies on.
* At forty stars a fixed curvature threshold flags noise. Verdicts are bootstrap-sigma-aware; the
  photographic DSS2 still fails everywhere, which is the control that says the test kept its teeth.

One ADQL trap: I/360 carries both "rmag" (SDSS) and "Rmag" (Johnson), and unquoted ADQL column
names are case-insensitive, so a bare rmag is ambiguous and the server answers HTTP 400. Quote the
identifiers.

## What is NOT tested here

Clipping. A service can be perfectly linear and still return a flat plateau where the data was cut
off: Legacy DR10's r HiPS does exactly that over the Sombrero's nucleus, eleven pixels wide, which
moves no global statistic and takes 5.1 % of the galaxy's light down to 1.6 %. That one is caught
inside the packer, from the fact that real floating-point sky data essentially never repeats a value
exactly.
