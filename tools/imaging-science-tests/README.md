# imaging-science-tests

What a photograph of a world is worth, and the four properties that stop it being farmable.

## Why

The astrophotography half of the mod paid no Science at all. Adding a reward to a pipeline whose
minimum exposure is 32 microseconds is the kind of thing that is broken by default: the shutter is
not a limit, so anything paid per FRAME is paid at whatever rate the player can click.

So the design is a pair of ratchets, and these are the properties that have to hold:

```
ratchet     re-shooting an identical frame pays exactly zero, at every rung
field clip  a camera is never paid for detail it has no pixels to record
sampling    binning can never PAY: it costs resolution or it changes nothing
bounded     the whole programme is a few per cent of the tree, not the tree
```

`Core/ImagingScience.cs` is pure arithmetic and `Core/ScienceRewards.cs` is constants, so all of it
runs headless with no game and no Unity.

## Run

```
dotnet run -c Release -p:Core=../../ExoInstruments/Core
```
