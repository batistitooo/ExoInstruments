# Rebuilding a capture's background outside the game

When a frame shows an artefact, the question is which STAGE put it there. Answering that by
elimination costs a game launch per hypothesis and gets it wrong most of the time. This rebuilds the
capture's diffuse background stage by stage, calling the shipped Core code at every step, and dumps
each stage so a comparison against the real FITS can be made after each one rather than only at the
end.

The stars are deliberately not reproduced: they need the Gaia catalogue and the whole photometric
chain, and the artefact under investigation is in the diffuse background, which a real frame lets us
isolate by masking every source above 40 ADU.

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core
```

Edit the constants at the top of `DumpFrame.cs` for a different pointing or instrument. Each stage
is written to `frame_<stage>.bin` as `int width, int height, float[]`.

## What it established

For a 121.8 s H-alpha capture of the Horsehead field with the RedCat at 1x1, down the column where
a real frame showed a factor-4 dip in the background:

| stage | column x 2100-2150, ADU, y = 1600 to 1720 |
|---|---|
| emission deposit | 7.2 7.3 7.3 7.4 7.4 7.5 7.3 |
| after the PSF | 7.2 7.3 7.3 7.4 7.4 7.5 7.3 |
| after sky and dark | 8.7 8.8 8.8 8.8 8.9 8.9 8.8 |
| **the real frame** | **13 12 8 3 3 15 13** |

Flat, with no NaN and no zero readings over 11.7 million pixels. So the deposit, the projection, the
Galactic rotation, the HEALPix interpolation, the line ratios and the PSF convolution together do
not produce the artefact.

That leaves exactly two stages unreproduced here: the **star deposit** and the **detector chain**
(blooming, charge-transfer smear, shot and read noise, the converter). Those are next.
