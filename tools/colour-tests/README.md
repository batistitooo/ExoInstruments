# Colour, done as colorimetry

Colour is the one thing in this mod a reader judges by eye, and a wrong colour is invisible to every
other test in the project: a transcription error in the colour matching functions, a transposed sRGB
matrix, or a gamma applied twice all produce images that still look like images.

## What was replaced

Two separate approximations, both of which produced plausible pictures:

1. **A star's tint** came from a piecewise log/power fit to somebody's plot of the Planckian locus
   (Helland 2012), valid over a limited temperature range and applicable to nothing but blackbodies.
   It differed from the real chain by up to 0.085 in sRGB distance, median 0.018.
2. **The colour composite** fed the red filter's electron count into the display's red primary, the
   green filter's into green, and so on, then added an operator-chosen fraction of the H-alpha frame
   to red. A red filter is not the sRGB red primary: it is the source's spectrum integrated against
   filter x optics x quantum efficiency x atmosphere, which is a completely different weighting
   function from the CIE x-bar. The colours therefore depended on the filter set rather than on the
   sky, and the H-alpha blend was an artist's knob with no physical meaning.

## The chain now

    X = Int S(lambda) xbar(lambda) dlambda,  and Y, Z likewise    CIE 1931 2-degree observer
    (R,G,B)_linear = M . (X,Y,Z)                                  IEC 61966-2-1, D65
    (R,G,B)_display = transfer((R,G,B)_linear)                     the standard's own piecewise curve

with the observer table and the matrix **generated** from colour-science rather than typed
(`tools/generate_cie_table.py`), and every step compared back against it.

For a multi-filter frame, `Core/ColourCalibration` fits the 3x3 transform from the instrument's own
band responses to tristimulus values, over a training set of blackbodies and nebular line spectra,
the same construction as a raw converter's colour matrix.

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core
python3 -m venv env && ./env/bin/pip install numpy colour-science
./env/bin/python compare_colour.py
```

## Results

| | agreement |
|---|---|
| standard observer, 471 tabulated wavelengths | exact (2e-15) |
| half-nanometre interpolation vs a Sprague quintic | 3.5e-5 of peak |
| sRGB transfer function | exact (1e-16) |
| Planckian locus, 300 K to 50000 K | 3.6e-6 in chromaticity |
| spectral locus, tabulated wavelengths | exact (4e-16) |
| luminance through the gamut mapping | 3e-15 relative |

The Planckian residual is a **known constant difference, not a numerical one**: CIE 15:2004 recommends
c2 = 1.4388e-2 m K for colorimetry, which colour-science follows, while this uses the SI defining
constants giving 1.438776877e-2. That is 1.55e-5 relative and it is where the 3.6e-6 comes from. The
SI value is used because the same Planck function is integrated elsewhere for photometry, and two
different Planck constants in one codebase would be worse than either.

## The gamut

A pure emission line is a monochromatic stimulus: it lies **on** the spectral locus and therefore
outside every real set of display primaries. It is desaturated toward the white point rather than
clipped, which preserves hue and luminance and gives up only saturation, the one attribute the
display genuinely cannot reproduce. H-alpha at 656.3 nm gives up 62% of its saturation to fit sRGB.

Both directions are handled. A colour can also need **more** of a primary than the display can emit
at that luminance; a saturated red at Y = 0.3 asks for a linear R of 1.4, and handling only the
negative side is what shifts a bright H-alpha nebula's hue as the exposure lengthens, its red channel
pinning at 1 while green and blue stay put.

## The instruments' own limits

The fit's control is an **ideal colorimeter**: three bands proportional to `xbar(lambda)/lambda`.
Divided by wavelength, because tristimulus values are integrals of *energy* against the observer
while a detector counts *photons*; so a photon-counting instrument with x-bar-shaped filters is not
a colorimeter, and getting that wrong left the control at 2.4% rms and made every real residual
uninterpretable. Corrected, the control fits to **1.5e-8**, which proves the machinery.

Against that floor, the real filter sets:

| instrument | continuum xy, median | continuum xy, worst | emission lines, worst |
|---|---|---|---|
| ideal colorimeter | 2e-9 | 9e-9 | 6e-8 |
| ZWO ASI294MM Pro (RedCat, RC20, CDK1000) | 0.0165 | 0.0756 | 0.32 |
| FORS2 (ESO measured curves) | 0.0086 | 0.0580 | 0.22 |
| SPHERE/ZIMPOL | no blue filter exists, cannot make true colour | | |

A just-noticeable chromaticity difference is about 0.002, so a typical star's colour through 88 nm
top-hat filters carries several JND, and FORS2's measured curves do twice as well as the top-hats.

**Emission lines are an order of magnitude worse, and that is the real result.** Broadband RGB cannot
measure the colour of a pure line: [O III] at 500.7 nm falls in the gap between the green and blue
passbands, so almost no light from it is collected and no matrix recovers a colour from a measurement
that was not made. That is why narrowband imaging uses stated palettes instead of claiming true
colour, and why this mod's HOO and SHO modes are labelled conventions and skip the colorimetry
entirely.
