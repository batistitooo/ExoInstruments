# flat-tests

Headless checks on `Core/FitsImageReader.cs` and `Core/MeasuredFlatField.cs`, plus a numerical
cross-validation of the reader against **astropy 6.0.1**, and a regression case on the flat field
map's **cache key**.

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

## F. One instrument, one binning, two filters

The only section here that is about a **cache key** rather than about a number, and the only one
whose subject lives outside `Core`.

`SolarSystemCameraTexture.EnsureFlatFieldMap` builds the flat field map once and holds it. It used
to decide whether it already had one like this:

```csharp
if (flatFieldMap != null) return;
```

which is keyed on the array existing and on nothing else. But the field it loads is chosen **per
filter**: `MeasuredFlatPath` puts the filter in the file name, because the dust motes and accessory
vignetting that are most of what makes a real flat worth having sit on the filter itself and move
when it is swapped. Changing filter on the same instrument at the same binning therefore went on
serving the **previous passband's** measured flat. Every frame after it was divided by a flat
belonging to a different light path, with no error and no log line, because a frame divided by the
wrong flat looks exactly like a frame divided by the right one.

`EnsureFringeMap`, a few lines below in the same file, has carried the filter in its key from the
start. The fix is that shape, unchanged:

```csharp
if (flatFieldMap != null && flatFieldMapFilter == Filter) return;

flatFieldMap = null;
flatFieldMapFilter = Filter;
```

### Why it survived a reading of the code

Because the *modelled* branch really is filter-blind, and it is the branch whose arithmetic is
written out in the method. A cosine-fourth falloff and a photo-response spread are geometry and
silicon; neither knows the passband, and from that code alone the conclusion "this map is not a
function of the filter" is sound. What makes it false is the line above it, `TryLoadMeasuredFlatField`,
which is a call: its body, and the `MeasuredFlatPath` that names the file per filter, sit about a
hundred and eighty lines further down the file. Both directions of the swap are wrong, and the
second is the easier one to miss:

| swap | what should happen | what happened |
|---|---|---|
| a filter with a flat → another filter with a flat | load the second one | kept the first passband's flat |
| a filter with a flat → a filter with none | fall back to the model | kept the first passband's flat |
| a filter with none → a filter with a flat | load it | never loaded the observer's own flat |

### What the section measures

Two master flats through one instrument at one binning, differing only in which filter was in the
way: the same silicon, the same tube, a different dust mote and a different filter cell. Both go to
disk as BITPIX 16 with `BZERO=32768` — what a real camera writes — and come back through the same
reader section B exists to defend.

| check | result |
|---|---|
| one flat per filter | `..._Luminance_bin2.fits` vs `..._HAlpha_bin2.fits` |
| the two filters' measured flats are different maps | RMS 4.65 %, worst pixel −20.6 % |
| a star under H-alpha's own mote, divided by the Luminance flat | **0.138 mag too faint** |
| a star under Luminance's mote, in an H-alpha frame | **0.177 mag too bright** |
| keyed on the filter, a filter change rebuilds | H-alpha's flat, after 2 builds |
| keyed on the array alone, it did not | still Luminance's flat, after 1 build |
| no flat on disk for the new filter | falls back to the model / kept the measured one |
| a flat on disk for the new filter | picks it up / never loaded it |
| the offset FPN map under two filters | bit-identical across 9216 pixels |

The two magnitude rows are the point of the section. A wrongly flat-fielded frame is not
*approximately* right: the wrong flat both fails to divide out the mote that **is** in the light
path and stamps in a false hole where the other filter's mote was, so one field yields errors of
both signs at once, at a fifth of a magnitude, on a photometry pipeline that reports no fault.

### The last row, and why it is here

`EnsureOffsetFpnMap` is still keyed on the array alone, and that is correct rather than the same
oversight left standing. It was **checked and not assumed**, because assuming exactly this is what
produced the fault above. `SensorNonUniformity.BuildOffsetMap` takes the serial seed, the pixel
count and a sigma that `BinnedOffsetSigmaElectrons` derives from the spec and the binning; the
filter reaches none of the three, and no measured-file path exists there to smuggle it in the way
`MeasuredFlatPath` does for the flat. The physics agrees with the call graph: offset fixed-pattern
noise is the readout chain's per-pixel zero level, the thing a bias frame measures with the shutter
shut. No light means no passband, so there is nothing for a filter to change.

### What this section cannot reach

`SolarSystemCameraTexture.cs` needs Unity and cannot be compiled headlessly, so the four lines of
the cache are **restated** in `FlatCache`, in both forms, and quoted verbatim above so the copy can
be checked against the original by eye. Reverting the guard in the mod would not turn this section
red.

What is *not* a copy is everything the guard decides over. The measured maps come from the real
`FitsImageReader` and `MeasuredFlatField`; the modelled map comes from the real
`FocalPlaneIllumination` and `SensorNonUniformity`; the offset maps come from the real
`BuildOffsetMap`. So the claims that survive independently of the restatement are the ones that
matter physically — that two filters name two files, that those files give two materially different
maps, that confusing them costs a fifth of a magnitude, and that the offset map is genuinely
filter-independent. Closing the last gap would mean moving the cache decision itself into `Core`,
the way `Core/FringeMap.cs` moved the fringe loop out of this file; that is a larger change than
the key, and it is not made here.

The instrument's numbers are deliberately **not** read from `Core.VisualTelescopeCatalog`. That file
names `SpectralCurve`, `FilterCurves`, `SystemBandpass`, `OpticalPsf` and five more, and pulling
fifty files into a FITS-and-flat harness to borrow a pixel pitch would couple this test to most of
the mod. Nothing in section F is a claim about the real ASI294MM Pro; the camera name is there
because it is a device that carries Luminance and H-alpha in one wheel, so the fault is reachable
on it. What the test needs is only that the camera, the binning and the array are the same for both
filters, so that every difference between the two maps is the filter's.
