# flat-tests

Headless checks on `Core/FitsImageReader.cs` and `Core/MeasuredFlatField.cs`, plus a numerical
cross-validation of the reader against **astropy 6.0.1**.

```
dotnet run -p:Core=../../ExoInstruments/Core -- --out .
../env/bin/python compare_astropy.py
```

The first must print `ALL CHECKS PASSED`, the second `EVERY FILE AGREES WITH ASTROPY EXACTLY`.

## Why there are two halves

The C# harness writes the FITS files itself and reads them back. That catches a great deal, but it
cannot catch the one failure that matters most: **a reader and a writer that share a
misunderstanding of the format agree with each other perfectly.** `astropy.io.fits` is the
reference implementation the field reads FITS with, so agreement with it is evidence about the
standard rather than about internal consistency. Same argument as `tools/poppy-crossvalidation` and
`tools/galsim-crossvalidation` make for the optics.

The Python side reads the same twelve files and compares against `expected.csv`, which holds
whatever the C# reader decoded. Every one agrees to **exactly zero**, including the `BLANK` pixel,
which has to come out undefined on both sides rather than as a number.

## What the C# side checks

**A. Every BITPIX the standard defines** (8, 16, 32, 64, −32, −64) round-trips exactly, on a
17×11 image chosen so the 2880-byte block padding has to be handled rather than stumbled over. Two
encodings of the same values are then compared against each other, so a decoding fault shows up as
a disagreement rather than as an absolute error.

**B. BZERO/BSCALE.** This is the trap the reader exists to avoid. Unsigned 16-bit data is stored in
FITS as signed shorts with `BZERO=32768`, which is what essentially every astronomy camera writes;
a reader that ignores it reads a 60000-count flat as −5536 and every calibration built on it is
wrong by the pedestal. Also a non-unit `BSCALE`, and `BLANK` becoming `NaN` rather than a very
large number.

**C. What is refused rather than guessed**, each with the reason in the message: a non-conforming
`SIMPLE`, a data cube, an undefined `BITPIX`, a truncated data segment (refused, not zero-filled), a
missing mandatory keyword, a zero `BSCALE`, a file that is not there.

**D. Header parsing corner cases.** A slash inside a quoted string is not the start of a comment, so
a `FILTER` card reading `'Ha 3nm / OIII'` survives intact; a doubled quote is one literal quote; and
`COMMENT`/`HISTORY` are not indexed as if they were keyword-value cards.

**E. The flat itself.**

| check | result |
|---|---|
| pedestal removed, a 10 % low pixel reads as 10 % low | 0.9000 |
| pedestal left in, the same pixel understates the deficit | 0.9131 instead of 0.90 |
| an average pixel sits at unity | 1.0000 |
| a saturated pixel is held at unity, not read as a high response | 1.0000 |
| a flat of the wrong size is refused, not resampled | refused |
| a frame at or below the pedestal is refused | refused |
| a single noisy sub raises the noise warning | 0.700 % high-frequency against a 0.707 % floor |
| a clean master flat does not | 0.157 % |

The second row is the reason the bias level is a **required** input rather than an optional one:
the pedestal pulls every response ratio toward unity, and nothing in the file says whether it has
already been removed.

The last two rows are why the noise diagnostic uses the **high-frequency** scatter (from differences
between horizontally adjacent pixels, divided by √2) and not the whole-array sigma. Vignetting,
tree rings and a dust mote's penumbra are smooth across one pixel and drop out of an adjacent-pair
difference; uncorrelated shot noise does not. An earlier version compared the whole-array sigma
against the shot-noise floor and flagged a *clean* flat as noisy, because a clean flat also has a
low sigma. The test is now `HighFrequencySigma >= 0.7 x ShotNoiseFloor`, which rests on the fact
that a single sub cannot scatter below its own shot-noise floor: one that does was combined from
several, which is exactly what a master flat is.
