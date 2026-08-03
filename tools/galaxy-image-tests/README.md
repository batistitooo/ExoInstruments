# Measured galaxy shapes: the map, the transform, and the flux

A galaxy drawn from a Sersic profile is a smooth ellipse. A galaxy drawn from a packed survey image
has arms, a dust lane and knots, and it is right for a completely different set of reasons, all of
which fail silently:

* the transform between the map's tangent plane and the frame's can be **mirrored**, and a mirrored
  galaxy is still a galaxy;
* it can be **affine** where the exact relation between two tangent planes is projective, which is
  right at the centre of the field and wrong at the edge of a large one;
* the **flux** can fail to be conserved when the map's pixels and the frame's are different sizes,
  which is a galaxy at the wrong brightness rather than in the wrong place;
* a frame pixel covering many map pixels can be **point sampled**, which aliases the arms into
  something that still looks like structure.

So the C# side deposits a real packed map through the shipped renderer and dumps everything, and the
Python side rebuilds all of it independently with astropy's own WCS machinery.

## Run

```
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- <GalaxyImages.galimg> NGC5194
./env/bin/python compare_image.py <GalaxyImages.galimg> NGC5194
```

The venv is the one in `tools/galaxy-images` (`numpy scipy astropy`); `env` here is a symlink to it.

## What is checked

| | against |
|---|---|
| every band sums to one | the normalisation contract that keeps the photometry the catalogue's |
| map pixel to sky | astropy `WCS` built independently for the same tangent plane |
| frame pixel to map pixel | an independent gnomonic projection and deprojection in numpy |
| what an affine fit would have cost | the same four corners, least squares |
| deposited flux | the same resampling, supersampled independently |
| the deposit pixel by pixel | the same, relative to the frame's peak |

## What it established

For M51's Pan-STARRS map (1024 px at 1.285″), on frames of 3°, 1°, 0.32° and 0.08°, the last two
being narrower than the map:

| | result |
|---|---|
| band sums | 1.000000000 |
| map deprojection vs astropy | 8.6e-11 arcsec |
| transform vs independent projection | ≤ 1.6e-6 map px |
| flux ratio, shipped vs independent | 1.00000 to 1.00001 |
| per-pixel difference (99th percentile) | ≤ 7e-10 of the peak |
| **flux conservation, frame containing the whole map** | **100.014 % and 100.021 %** |

The last row is the one that matters for photometry: a map is normalised to unit total, so a frame
that contains all of it must collect all of the galaxy's catalogued flux, and it does to two parts
in ten thousand. On frames narrower than the map the deposit falls to 82.9 % and 45.7 % of the
total, which is the light that genuinely fell outside the sensor.

The affine comparison put the error of the approximation at 0.02 map pixels for this 22′ map, i.e.
negligible at this size; it grows with the square of the map's angular extent, which is why the
exact projective form is solved rather than approximated.

The harness deliberately uses a sensor basis whose east runs to the RIGHT while the stored maps run
east to the LEFT, so a transform that quietly dropped the mirror would fail the second row.
