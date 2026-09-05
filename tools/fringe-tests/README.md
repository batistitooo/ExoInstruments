# One model, two ESO measurements

Walsh et al. (2008) measured **7.0 % peak-to-peak** fringing on the FORS2 MIT mosaic at 956 nm with
a monochromator. ESO's FORS2 user manual states that in z_Gunn **imaging** the amplitude is
*"below 1%"*. Both are true, they describe the same detector, and what separates them is an integral
over a passband weighted by the night sky's own line spectrum.

```
dotnet run -p:Core=../../ExoInstruments/Core -- --out .
```

## What it establishes

**The layer, twice.** A fringe period of 2.9 nm at 950 nm implies a 43.4 µm silicon layer; Downing
et al. (2006) state the CCID-20 is 40 µm thick. Two papers, two methods, **8.5 %**.

**Walsh's curve, returned unchanged**, and no fringes below 774 nm, which is a measurement (their
774 nm flat showed none) as much as physics.

**All three of the manual's prose statements, from one integral:**

| passband | model | manual |
|---|---|---|
| I_BESS | 0.057 % | "hardly visible" |
| z_Gunn | 0.659 % | "below 1%" |
| 1 nm slit at 956 nm | 5.97 % | "of the order of 5%" |

**And the result that took two failing tests to reach:** neither the real sky nor a smooth one
washes out monotonically with bandwidth. The smooth one follows the **sinc envelope** of a top-hat
against a 3.19 nm fringe period, collapsing to 0.185 % at a 3 nm band and reviving to 0.461 % at
10 nm. The real one adds its OH bands, which sample the cosine at particular phases rather than
averaging over it, **11 times harder at that same bandwidth**. Fringing has no safe bandwidth, and
that is why it is measured per filter rather than predicted.

## The map, and what dividing it across cores may not change

Sections 1 to 3 are about optics. Section 4 is not: it is about `Core/FringeMap.cs`, the loop that
builds a whole detector's map, and about the two things claimed for it when that loop was moved out
of the renderer.

That move was argued for on the grounds that **a frame-sized loop in the Unity layer is a loop no
harness can compile**, so every harness that wants it must reproduce it, and a reproduction is only
worth what its arguments are. The argument was right and the follow-through was missing: until this
section existed, nothing under `tools/` referenced `FringeMap` at all. The equivalence figures for
the move lived in a commit message, and a commit message does not re-run.

### What is checked

| check | result |
|---|---|
| the frame divides into more than one 4096-pixel block | 10 blocks |
| at one worker, the serial branch is the one taken | `Worthwhile` false |
| at 2, 3 and 7 workers, the parallel branch is taken | `Worthwhile` true |
| the map at 2 workers against the serial map | 0 differing half words of 40000 |
| the map at 3 workers | 0 differing half words |
| the map at 7 workers | 0 differing half words |
| the two spellings of the optical path | agree on 40000 of 40000 |
| the shipped map against the per-pixel form it replaced | agree on 40000 of 40000 |
| a passband below the 774 nm onset | cannot fringe, map is exact zeros |
| an empty frame | empty map |
| a null passband or thickness field | refused, not dereferenced |

The frame size is not decoration. `Build` divides the work into 4096-pixel blocks, so a frame of one
block or less is a single block, which `Parallel.For` hands to one worker: the comparison would be
serial against serial and would pass whatever the code did. The band width is not decoration either,
for the same reason from the other side: `ParallelWork.Worthwhile` gates on pixels times samples, so
a narrow band on a small frame takes the serial branch twice over. Both are asserted rather than
assumed, which is what the first three rows of the table are.

### Which check has the teeth

Two rows look alike and are not. **The two spellings of the optical path** are compared against each
other in the harness, so it is an algebraic statement about association order and nothing inside
`FringeMap` can make it fail. **The shipped map against the per-pixel form** compares `Build`'s
actual output against `Fringing.OpticalPathNm`, which is the per-pixel form the loop replaced and is
still shipped unchanged, so that is the row that goes red if the hoisted refractive index ever stops
agreeing with it.

Confirmed rather than assumed: perturbing the index by one part in ten million, in a scratch copy of
`Core`, left the first row green and turned the second red on **17927 of 40000** pixels. A test
that cannot fail is not a test, and the two were worth separating for exactly that reason.

Every comparison is an **equality and not a tolerance**, which is the same argument the map's own
`(1 + x) - 1` makes: adding one snaps `x` onto the 2^-53 grid before the subtraction takes it off
again, and the map has always stored the snapped value. Storing the unsnapped `x` would be slightly
more accurate and therefore wrong, because it would turn every row above into an approximation. What
this pipeline is for is that a recorded seed reproduces its frame, so a path that moved by one ulp
has moved the frame.
