# Sky-field physics harness

Headless checks for the pure-`Core/` physics behind the rendered star field: the gnomonic
projection, stellar photometry and colour terms, sky surface brightness, wavelength-dependent
extinction, flux conservation in the source renderer, and the Tycho-2 cone search.

It compiles the `Core/` sources directly (they carry no Unity or KSP dependency, which is the
point of keeping them pure) so none of this needs the game running.

```bash
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- ../../ExoInstruments/PluginData/GaiaStarCatalog.bin
```

The catalogue argument is optional; without it the catalogue and end-to-end sections are
skipped and only the analytic checks run. Exit code is non-zero if any check fails.
