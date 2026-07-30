# The sky's own emission lines

The night sky is not a continuum. Above 85 km the atmosphere glows: the [O I] green line at 557.7 nm,
the [O I] red doublet at 630.0/636.4, the sodium D pair, and from 650 nm upward a dense forest of OH
Meinel bands. ESO's model puts **11148 rayleighs into lines** between 350 and 1000 nm against 5290 in
the residual continuum: most of the dark sky is lines, and a narrowband filter either sits on one
or it does not.

The mod's sky used to be a flat 21.7 mag/arcsec^2 through every filter. That made an [O I] 6300 frame
and an [S II] frame look equally easy, and they are not:

| band | sky it sees | of which lines |
|---|---|---|
| Luminance 265 nm | 6.9 R/nm-equiv | 36% |
| H-alpha 7 nm | 9.6 R | 48% |
| **[O I] 6300, 3 nm** | **57.4 R** | **91%** |
| [S II] 3 nm | 5.2 R | 6% |
| [O III] 3 nm | 3.5 R | 1% |

An [O I] filter stares at **11x** the sky an [S II] filter does, in the very line it is trying to
image, the real reason nobody images [O I] from the ground while [S II] is routine.

## Where the numbers come from

`tools/generate_airglow_table.py` queries **ESO's SkyCalc sky model** (Noll et al. 2012, A&A 543,
A92; Jones et al. 2013, A&A 560, A91), whose airglow component rests on the flux-calibrated Paranal
sky spectra of Hanuschik (2003) and Patat (2008). Solar radio flux 130 sfu, mid-cycle, and stated:
the red line varies by a factor of several across the solar cycle.

The 0.02 nm model grid is **bin-integrated** onto 0.1 nm, an average that preserves the integral
over every window exactly, where resampling would move flux and smear the narrow lines. Lines and
continuum are stored separately because they scale differently with zenith distance.

## Zenith scaling: van Rhijn, not sec z

The emitting gas is a **layer**, not a slab, so its slant column grows as the van Rhijn (1921)
function of the shell geometry: 1.92 at z = 60 deg against sec z = 2.00, and 4.19 against 5.76 at 80.
The [O I] red doublet forms in the F region near 250 km, far above the 90 km of everything else, and
carries its own materially different factor (1.73 at 60 deg).

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core
python3 -m venv env && ./env/bin/pip install numpy speclite skycalc_cli astropy
./env/bin/python compare_airglow.py
```

## Results

The table is generated from SkyCalc, so comparing it to SkyCalc alone would only prove the generator
ran. What is checked instead:

| | |
|---|---|
| stored table vs a **fresh** SkyCalc query, six windows | 2.6e-6 relative |
| Bessell V transcription vs speclite's own curve | 1e-16 of peak |
| van Rhijn vs the closed form | 8e-15 |
| **airglow-only V at the zenith** | **22.09**, vs Patat's 21.7 measured *with* zodiacal |
| **total dark sky with the mod's zodiacal term** | **21.78 vs the measured 21.7 +/- 0.2** |
| [O I] 5577 | 191 R, published range 100-300 |
| [O I] 6300 | 151 R, published range 50-300 |
| Na D | 49 R, published range 20-120 |

The last five are the ones that matter: they are **published measurements that never entered the
generator**, so the chain from ESO's spectra through the binning, the Bessell V band and the mod's
own photometric zero point is closed against reality rather than against itself.
