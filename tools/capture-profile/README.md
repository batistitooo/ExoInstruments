# capture-profile, where a capture's seconds go

Baptiste's report, 2026-07-31: a galaxy photograph on the RC20 at **4×4 binning** took thirty
seconds or more, and 1×1 was not worth attempting.

At 4×4 the RC20's frame is 1036×705 = 0.73 Mpx. Thirty seconds is therefore not "a big frame",
and reading the code was not going to say which stage owned the time; it had already suggested
the wrong one. This harness times the shipped stages at the shipped parameters, on the real
installed data files, so the answer is measured.

It also checks the thing that the fixes depend on: several stages now run across cores, and a
stage allowed to do that has to return **the same frame bit for bit** however many workers it had,
or the seed recorded in the FITS header stops reproducing the exposure.

## What it times

The scene is an M51 portrait from the Observatoire de Haute-Provence, an hour east of the
meridian, through the RC20's Barlow and its L filter, the case that was slow.

Everything is the shipped Core except the two frame-sized loops that live in the Unity layer
(`SolarSystemCameraTexture.DepositEmissionField` and its detector chain), which cannot be compiled
headless and are reproduced here call for call against the same Core entry points.

## What it found

Single-threaded, .NET 10, idle machine, best of five:

| stage | 4×4 before | 4×4 after | 1×1 before | 1×1 after |
|---|---:|---:|---:|---:|
| emission fill (11.7 M samples) | 1744 | 190 | 2185 | 241 |
| galaxy deposit | 16 | 5 | 66 | 26 |
| PSF kernel, 12 sub-bands | 68 | 25 | 281 | 78 |
| PSF convolution | 137 | 41 | 1292 | 257 |
| detector chain | 47 | 24 | 343 | 353 |
| **total, ms** | **2012** | **284** | **4166** | **954** |

The finding that mattered: **the diffuse-emission fill was 87 % of a 4×4 capture**, and its cost
does not fall with binning at all. It takes one sample per NATIVE pixel, 11.7 million on this
sensor whatever the observer chose, because the average over the native sub-pixels is the
integral the sensor performs. Inside each sample, `Float16.ToDouble` was calling `Math.Pow` to
raise two to an integer power, sixteen times per sample through the interpolation stencil, i.e.
187 million transcendental calls per exposure for an answer that is one of thirty-two constants.

KSP runs Mono rather than .NET 10 and was itself holding three of this machine's ten cores while
the first measurements were taken, which is where the thirty seconds came from; the ratios above
are what carries over.

## Running

```
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- [dataDir] [binning] [options]
```

`dataDir` defaults to the installed `GameData/ExoInstruments/PluginData`; `binning` to 4.

| option | what it does |
|---|---|
| `--repeat N` | time each stage N times and report the fastest (default 3) |
| `--workers N` | pin the worker count instead of using the machine's |
| `--target ra,dec` | point somewhere else (degrees). `85.24,-2.46` is the Horsehead, the field to exercise the high-resolution patch layer on |
| `--determinism` | run every parallel stage at one worker and at the machine's count, and compare bit for bit |
| `--accuracy` | check the tiled FFT convolution against the direct double-precision sum |

`--determinism` and `--accuracy` exit non-zero on failure.

The timings report the **fastest** of the repeats, not the mean. This machine normally has KSP
itself running while a capture is timed, so a slow run measures how much of the machine the game
happened to be holding; the mean of a contended benchmark measures the contention.

## A correction, 2026-08-07

**This harness was timing a kernel the mod does not build.** It passed `vaneCount = 0` to
`BuildChromaticKernel`, which takes `OpticalPsf`'s radial path, and the shipped RC20 has not taken
that path since the visual roster's PSF learned about spiders. The stage this harness exists to
time was the one stage it got wrong, and the difference is not small: with the RC20's real four
1.5 mm vanes the diffraction term is sampled in two dimensions over the whole 257x257 support and
then convolved with the atmospheric profile, which was **8855 ms of a 9502 ms reduction**.

Fixed here, and the cost fixed in `Core`, see `tools/psf-cost`, which exists because of it. This
harness's own numbers, once it is asking for the right kernel:

| RC20, M51 from OHP | before | after |
|---|---:|---:|
| PSF kernel, 4x4 | 8855 ms | **251 ms** |
| whole reduction, 4x4 | 9502 ms | **809 ms** |
| PSF kernel, 1x1 | 13916 ms | **260 ms** |
| whole reduction, 1x1 | 15736 ms | **1293 ms** |

The lesson is the one this directory was built on and had to learn twice: a harness that
reproduces the pipeline "call for call" is only worth what its arguments are, and those drift.

## What this does NOT establish

- **It is not the whole capture.** The Unity render, the readback, the display stretch and the
  stacker are all on the main thread and are not timed here. The shipped code now logs its own
  per-stage breakdown once per exposure (`SolarSystemCameraTexture.LastStageTimings`, shown in the
  capture readout), which is the measurement that includes them.
- **It is .NET, not Mono.** Absolute numbers under KSP are several times larger.
- **Determinism is checked, not proved.** It compares one scene at one binning. Run it at both
  binnings and on `--target 85.24,-2.46` as well: that field overlaps two SHASSA patches, and the
  patch lookup is the one place carrying per-worker mutable state (a run cursor into the patch),
  so it is where sharing between workers would actually go wrong. What makes the
  claim hold in general is the rule the stages are written to (see `Core/ParallelWork.cs`): write
  only to per-element storage, or accumulate per row and sum the rows afterwards in row order.
