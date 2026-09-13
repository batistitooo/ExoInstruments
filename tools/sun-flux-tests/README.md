# Sun flux and the reference distance

Reflected sunlight in KSP is scaled so the Sun delivers the real solar constant at the orbit of the
home world's star-orbiting ancestor, corrected for Physics.cfg's `solarLuminosityAtHome`, rather than
at the IAU astronomical unit. Kerbin sits 11 times closer to Kerbol than the AU, so dividing by the AU
made every body 121 times too bright.

These checks cover `PhotonFluxModel.SunApparentMagnitude`, the parent walk in
`PhotonFluxModel.HomeStarOrbitMeters` (stock Kerbin, a moon home world, RSS Earth, and a chain that
never reaches the star), and `PhotonFluxModel.SunReferenceDistanceMeters`, with pinned magnitudes so
a reference that drifts fails.

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core
```
