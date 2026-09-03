# dustmap-tests

The two transforms that stand between a catalogue position and a value in an all-sky map, validated
against **healpy** and **astropy**.

Every all-sky dust, Hα and CO map is a HEALPix array in Galactic coordinates: SFD98, Planck GNILC,
the Finkbeiner Hα composite, the Green and Edenhofer 3D extinction cubes. Reading one means
composing

```
RA/Dec  ->  Galactic l/b        GalacticCoordinates
        ->  HEALPix pixel       Healpix
```

and both fail **silently**. A wrong pixel returns a perfectly plausible number from the wrong part
of the sky; a wrong Galactic frame puts the plane at the wrong angle across the field. Neither
throws, and neither is obvious in a rendered frame.

## Results

**HEALPix is exact.** Zero mismatches against `healpy.ang2pix` over **32 240 directions**, in both
RING and NESTED, at eight resolutions:

| nside | 1 | 2 | 4 | 16 | 64 | 256 | 1024 | 4096 |
|---|---|---|---|---|---|---|---|---|
| resolution | 58.6° | 29.3° | 14.7° | 3.66° | 55.0′ | 13.7′ | 3.44′ | 0.86′ |
| mismatches | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

Directions are drawn uniformly on the sphere (uniform in `z`, not a lattice, a lattice can sit on
pixel boundaries at every nside and miss the branch cuts entirely), plus the boundaries the scheme
is piecewise across: the cap/band transition at `|z| = 2/3` approached from both sides, both poles,
and `φ` at each quadrant edge. Pixel counts and resolutions match `healpy` exactly.

Getting there found one real thing. The nested index was first derived by converting a RING index,
and that conversion was wrong at every nside above 4 while RING itself was exact. Nested numbering
is a Morton code of the pixel's position *within its base face*, so the face and the two in-face
coordinates fall straight out of the same projection RING uses, computing them directly is both
shorter and correct, where going through RING means inverting that numbering first.

**The Galactic frame is the IAU one.**

| check | result |
|---|---|
| latitude vs astropy, 6007 directions | 3.1×10⁻⁶ deg |
| longitude vs astropy (off the Galactic poles) | 8.0×10⁻⁵ deg |
| declination round trip | 3.3×10⁻¹⁰ deg |
| right ascension round trip (off the celestial poles) | 8.5×10⁻¹³ deg |
| **Sgr A\* lands at** | **l = 0.0000°, b = 0.0000°** |

The pole is the ICRS realisation of the IAU 1958 frame from the Hipparcos documentation (ESA 1997,
SP-1200, Vol. 1, Sect. 1.5.3): α_NGP = 192.85948°, δ_NGP = 27.12825°, l_NCP = 122.93192°. The
residual against astropy is 3×10⁻⁶ deg because astropy carries more digits of the same constants,
which is 0.01″, a thousandth of the finest map pixel anyone publishes.

Sgr A\* landing on the Galactic origin to 2×10⁻⁶ deg is the check that the frame is the IAU
definition rather than a fit: the definition is *what places it there*.

Three checks are about signs rather than tolerances, Sgr A\* at the origin, and both Galactic poles
at exactly ±90°, because a frame with a flipped longitude sense passes every round-trip test ever
written and puts the Galactic plane sweeping the wrong way across the sky.

**The packed map format round-trips.** `make_test_map.py` writes a synthetic map whose value is a
known analytic function of Galactic latitude, `0.02 + 1.98 exp(-|b|/8°)`, and the C# side reads it
back and queries 3000 random directions. The expectation is recomputed from the *queried position*
alone, so the map file is never consulted: this checks the pixel lookup, not the file's contents.

| check | result |
|---|---|
| median residual against the written pattern | **0.9 mmag** |
| worst residual, as a fraction of one pixel's own gradient | 0.81 |
| `A(V)` against `R_V E(B-V)` | 0 |

The worst residual is bounded by the pattern's gradient times one pixel rather than by a flat
tolerance, because a query returns the value of the pixel it lands in while the expectation is
evaluated at the exact direction. The pattern is steepest in the Galactic plane and flat at the
poles, so a flat tolerance would be too tight in one place and meaningless in the other.

**A real map, against `dustmaps` on the real sky.** When `tools/pack_dust_map.py` has built one,
the harness reads it and queries 4000 random sight lines:

| check | result |
|---|---|
| reddening at the pixel centre, relative | **4.8×10⁻⁴** |
| directions with no value | **0** |

4.8×10⁻⁴ is the half float's own precision, so nothing else stands between the packer, the format,
the HEALPix lookup and the Galactic transform. The comparison is made at the **pixel centre**
because that is what the packer stored; querying `dustmaps` at an arbitrary direction interpolates
its native Lambert grid instead, which measures the resampling rather than the format. That
resampling is reported separately: median **0.90 %**, worst 25 %, concentrated in the plane where
SFD's gradient is steepest and below its own 6.1′ beam.

**The half-float decode is exact** over all 65536 encodings, including subnormals, both infinities
and every NaN.

### One bug this found

The first version of the format stored fixed-point counts of 10⁻⁴ magnitudes, saturating at 6.5535.
SFD98 reaches **135.25 magnitudes** in the inner plane, so that version silently marked 48 615
pixels "no value", every one at |b| below a degree, which is to say exactly the dust worth having.
A query toward the Galactic centre returned NaN.

No fixed-point scale spans 0.00037 to 135 magnitudes in 16 bits: any scale fine enough for the poles
saturates in the plane, and any scale coarse enough for the plane quantises the poles to nothing.
A half float's precision is *relative*, 4.9×10⁻⁴ of the value everywhere, in the same two bytes.

## What this does NOT establish

- **The synthetic pattern checks the format; the real map checks the chain.** The synthetic pattern checks the format and the lookup; whether
  SFD98 or Planck GNILC say the right thing about the real sky is their business, not this
  harness's.
- **Direction to pixel only.** The inverse (pixel centre to direction) and neighbour queries are not
  implemented, because a map *reader* does not need them.
- **No interpolation.** Nearest-pixel lookup. Bilinear interpolation over the four neighbours is
  what `healpy.get_interp_val` does and is a separate thing to validate when it lands.
- **No proper motion or epoch.** The Galactic transform is a fixed rotation of ICRS, which is what
  every dust map assumes of its own grid.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
python -m venv env && ./env/bin/pip install numpy astropy healpy
./env/bin/python compare_mapindex.py
```

Exit code 0 when every check passes. Verified against healpy 1.17.3 and astropy 6.0.1.
