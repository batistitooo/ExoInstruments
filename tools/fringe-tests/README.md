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
