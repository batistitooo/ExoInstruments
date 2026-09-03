# pointing-tests

The transforms the telescope aims with, cross-validated against **IAU SOFA** (via `pyerfa`) and
**astropy**.

## Why

The visual telescopes could only aim at `CelestialBody` transforms, so nothing on the sky that is
not a planet could be photographed. Aiming at a catalogue position instead runs the star field's own
chain **backwards**:

```
RA/Dec  ->  altitude/azimuth        SkyCoordinates.EquatorialToHorizontal
        ->  (north, east, up)       SkyVector.FromHorizontal
        ->  world direction         SolarSystemCameraTexture.TryEquatorialDirection
```

A sign error anywhere in that produces a telescope that points somewhere plausible and wrong, and
the frame still comes out full of stars, just the wrong ones. This directory is what rules that out.

## The reference is SOFA, not a rearrangement

`pyerfa` wraps ERFA, the IAU SOFA library. `erfa.hd2ae` and `erfa.ae2hd` are the standard
hour-angle/declination ↔ azimuth/elevation pair, implemented independently in C, and they measure
azimuth **north through east**, the same convention this codebase uses. `erfa.s2c` gives the
direction cosines. Agreement is evidence about the transformation, not about a shared derivation.

Astropy's full `AltAz` frame is deliberately **not** the reference: it applies precession, nutation,
aberration, polar motion and refraction, none of which this mod models, so a disagreement there
would measure the corrections rather than the trigonometry. Astropy is used for the one thing it is
the right reference for, parsing what a catalogue writes.

## Results

| check | result |
|---|---|
| altitude vs `erfa.hd2ae`, over 13 104 geometries | **1.3×10⁻¹³ deg** |
| azimuth vs `erfa.hd2ae` | 2.8×10⁻¹³ deg |
| declination vs `erfa.ae2hd` | 3.4×10⁻¹³ deg |
| right ascension vs `erfa.ae2hd` | 1.2×10⁻¹² deg |
| the mod's own RA/Dec → alt/az → RA/Dec round trip | **1.5×10⁻¹² deg (5.5 µas)** |
| direction cosines vs `erfa.s2c` | ≤ 8.9×10⁻¹⁶ |
| every direction is a unit vector | 1.1×10⁻¹⁶ |

The geometries span seven latitudes including both poles and the roster's real sites (Paranal
−24.6°, Palomar +33.4°, OHP +43.9°), six local sidereal times, 24 right ascensions and 13
declinations from −88° to +88°. The pole is included on purpose: it is where an implementation that
steps in right ascension and divides by `cos(dec)` falls apart, and where the azimuth of the round
trip is legitimately degenerate, so azimuth is compared away from the zenith and right ascension
away from the pole, and altitude and declination carry those cases on their own.

**Three checks are about signs rather than tolerances**: due north on the horizon is `+north`, due
east is `+east`, the zenith is `+up`. A basis with east and west swapped passes every norm check
ever written and points the telescope at the wrong half of the sky.

**Parsing** matches astropy exactly on ten real targets in four notations (sexagesimal with spaces,
with unit letters, colon-separated, and decimal degrees), and refuses three malformed inputs rather
than guessing: an unparseable field, a declination beyond ±90°, and sexagesimal minutes ≥ 60. What
the mod prints parses back to what it printed, to the 1″ its format carries.

## What this does NOT establish

- **No astrometric corrections.** No precession, nutation, aberration, parallax or refraction. The
  star catalogue and the aim share one frame, which is what matters for the two to line up, but that
  frame is not ICRS-at-epoch.
- **The world basis is not tested here.** `TryBuildSiteBasis` reads KSP's own
  `GetWorldSurfacePosition`, so it needs the game. What is tested is everything that basis is
  composed with, which is where sign errors live.
- **Nothing downstream.** Not the projection onto the sensor, not the render.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
python -m venv env && ./env/bin/pip install numpy pyerfa astropy
./env/bin/python compare_pointing.py
```

Exit code 0 when every check passes. Verified against pyerfa 2.0.1.5 and astropy 6.0.1.
