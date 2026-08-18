# galaxy-pair-tests

The mutually-covering galaxy pair, end to end against the shipped data.

The shipped `GalaxyImages.galimg` holds M51 as two maps that each swallowed the other:
NGC5194's map lists NGC5195 as a companion and NGC5195's lists NGC5194. Both claims are true
(each map's pixels contain the other galaxy's light, and each map is normalised to the sum of
the two catalogued fluxes), but the camera's deposit loop used to skip any galaxy covered by a
map present in the frame, with no tie-break for the mutual case: both members were skipped and
the pair was absent from the photograph.

The fix lets exactly one member of a mutual pair deposit, the brighter catalogued total (name
order settling a dead heat), whose map total already folds the companion's flux in. The
selection loop lives in the Unity layer (`SolarSystemCameraTexture.DepositGalaxies`) and cannot
be compiled headless, so, as in `tools/capture-profile`, it is reproduced here call for call
against the same Core entry points; if the loop in the camera changes, the copy in
`CheckGalaxyPair.cs` has to follow it.

What is checked: the shipped data still holds the mutual pair (so the tie-break is
load-bearing); the camera's own cone search of the M51 field sees both members; the old
selection deposits neither (the bug, reproduced); the fixed selection deposits exactly one,
the brighter, independent of search order; the winner's electron total folds in every
companion's catalogued flux; and the winner's map lands those electrons on a frame that
contains it.

    dotnet run -c Release -p:Core=../../ExoInstruments/Core -- [dataDir]

`dataDir` defaults to the installed `PluginData` directory and must hold
`GalaxyCatalog.galcat` and `GalaxyImages.galimg`. Must print `ALL CHECKS PASSED`.
