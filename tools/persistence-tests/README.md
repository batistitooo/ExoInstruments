# persistence-tests

Headless checks on `Core/DetectorPersistence.cs` and, in section D, on the **sourcing** of the
shipped roster's persistence parameters.

```
dotnet run -p:Core=../../ExoInstruments/Core
```

No Unity, no KSP, no game running. Exit status is 0 when every check passes.

## What is being checked, and why each check exists

Persistence is the residual **surface** image: charge held at the silicon-oxide interface after a
saturated exposure and released thermally into the exposures that follow. It is the only effect in
the detector chain that depends on what was observed *before*, and the only one that makes the
pipeline hold state between frames. The full account is `TECHNICAL_REFERENCE.md` §7.5166.

**A. Capture has the shape the measurements have.** Every published measurement reports residual
images following *saturated* sources and reports nothing below that, so capture is thresholded
rather than proportional, a proportional model would put a ghost under every source in the frame.
And the interface has a finite density of states, so capture saturates: a pixel driven to 100× full
well does not trap 100× the charge, which is what the WFPC2 handbook's behaviour after exactly such
an overexposure requires. Both are asserted, along with the degenerate cases returning zero rather
than a `NaN` that would propagate into a frame.

**B. Release is exact under any split of the interval.** This is the section that justifies an
implementation decision, so it measures both sides of it. A single exponential composes with itself:
releasing over 300 s in one step equals releasing over 100 s three times, to **0.00×10⁰** relative.
A *sum* of two exponentials does not compose, and the same test run against a one-array two-term
decay law misses by **2.3×10⁻¹**. That is the whole reason the trap state is two arrays rather than
one number with a two-term law: the one-array form is correct only at the cadence its fit was
measured at and has no defined state after a partial decay.

**C. The published reference points are reproduced.** Two real measurements exist, neither of them on
a detector in this roster:

| source | measurement | model |
|---|---|---|
| WFPC2 IHB §4.5 | residual images disappear within 1000 s at −70 °C | 0.92 % still held |
| WFPC2 IHB §4.5 | nothing measurable half an hour after a 100× overexposure | 0.037 % still held |
| arXiv:2502.05418 (e2v CCD250) | well over a hundred seconds to dissipate | 27 % still held at 150 s |

The first two catch a decay pair that clears too slowly; the third is the opposite comparator and
catches one that clears too fast. The reference constants themselves are locked, so changing them
has to be deliberate.

**D. The roster's sourcing.** This section reads the **shipped** `VisualTelescopeCatalog`, not a
restatement of it, which is why the project file compiles all of `Core` rather than naming files
individually. It asserts that the effect is off on all six instruments, and that the two reasons for
being off stay distinct:

- **WFC3/UVIS is measured absent.** ISR WFC3 2005-10 obtained dark images following highly saturated
  PSF images specifically to look for persistence in the CCDs and found none significant. That is a
  result, not an absence of one, so it is carried as its own flag rather than as another `NaN`, and
  the harness asserts exactly one instrument carries it and that it carries no amplitude beside it.
- **Everything else is unpublished.** The IMX492 (a pinned-photodiode CMOS, whose architecture makes
  lag small by construction, but an architectural expectation is not a measurement), FORS2's MIT/LL
  CCID-20, and ZIMPOL's CCDs.

A parameter appearing on any instrument without a citation should break section D. That is what it is
for: the model is here and waiting for a number, and this is the check that stops a plausible-looking
number arriving without a source.

## What this harness does not do

It does not exercise the frame-level wiring in `Visualization/SolarSystemCameraTexture.cs`
(`ApplyPersistenceRelease` / `ApplyPersistenceCapture`), which needs the Unity-side texture pipeline.
That path is inert on the shipped roster in any case, since no instrument enables the effect.
