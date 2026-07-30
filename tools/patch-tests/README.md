# Making "no artefacts" a number

This layer has produced black discs, white specks, grey plates and staircase edges, and each was
chased to a different cause: continuum subtraction over-correcting on a bright star, under-correcting
and leaving the star itself, a plate flaw, the survey's own noise at the cell level. **Repairing
causes one at a time does not converge.** The attempt to do it by star position was measured and
explains only a third: of 176 outlier cells in the Horsehead patch, 52 lie within 2 arcmin of a star
brighter than V = 10 and 124 do not.

So the repair stopped asking what made a cell wrong and started asking whether it can be sky at all.

## Two steps

**1. Outlier rejection.** A cell more than three robust sigma from the median of its own 3.4 arcmin
neighbourhood is inconsistent with the sky around it at a scale the survey resolves, and diffuse
emission is by definition not that. Iterated to convergence, MAD rather than standard deviation
because a standard deviation is dragged by the very outliers it is meant to find. This is the
cosmetic-defect rejection every survey pipeline runs (IRAF `cosmicrays`; van Dokkum 2001's
L.A.Cosmic and its descendants), applied to a map instead of a frame.

**2. The contract.** A patch is *fine structure on a calibrated map*, so its only legitimate claim
is about scales the composite cannot resolve; at the composite's own beam it must say exactly what
the composite says. The composite's nside divides the patch's, so each composite cell contains a
whole number of patch cells -- sixteen -- and the constraint is arithmetic rather than approximate:
scale each group so its mean is the composite's value. Fine structure inside the group survives
exactly, being multiplied by one number; the level is the composite's everywhere, so **no boundary
can step, including the patch's own rim**. This is the single-dish/interferometer combination every
radio survey performs (Stanimirovic 2002, ASP Conf. 278, 375).

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core
```

## Results over the fourteen installed patches

| | before | after |
|---|---|---|
| cells inconsistent with their neighbourhood (5 sigma, robust) | 2245 | 827 |
| composite cells departing from the contract by >50% | 353 | 0 |
| worst departure from the contract | -- | 4.1e-4 |
| cells rejected | | 10,544 of 542,673 (1.9%) |
| cost at load | | 4.4 s to reject, 0.15 s to calibrate |

**The 827 that remain are the survey's own noise, not artefacts.** A robust 5-sigma cut on real,
positively-skewed data finds about a tenth of a percent by construction; tightening the rejection
does not reduce it, and neither does judging cells against an annulus rather than a disc (which was
tried and gave 827 against 729). What settles it is the render: the black discs and white specks are
gone from the map at Siril's own autostretch, with no noise and no PSF to hide behind.

The geometry of each cell's neighbourhood is built **once** and reused across rounds. Rebuilding it
every round meant 1.6e8 spherical-to-pixel conversions and 37 seconds of load; it is now 4.4 s.
Render cost is unchanged -- the lookup is one interpolated read per frame pixel whatever the map's
resolution, 226 ns at nside 256 and 237 ns at nside 8192.
