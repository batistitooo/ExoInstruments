# infrared-tests

Headless checks on the HgCdTe chain: `Core/HgCdTePersistence.cs`, `Core/InfraredArray.cs`, and the
sourcing of `VisualTelescopeCatalog.HubbleWfc3Ir`.

```
dotnet run -p:Core=../../ExoInstruments/Core
```

Must print `ALL CHECKS PASSED`. No Unity, no KSP, no game running. The full account of what is being
modelled is `TECHNICAL_REFERENCE.md` §13.75.

## The rule this harness follows

**Nothing is asserted against the code's own output.** Every check is either a transcription check
against a published table, or an assertion against a *prose statement* in the report that published
the model. The persistence section is the clearest case: ISR 2015-15 reads four trends out of its own
Table 2 and quotes two amplitudes and a decay slope in the surrounding text, and those seven
statements are what the interpolated model is tested against — not against values this code produced
earlier.

## A. The Fermi persistence model (ISR 2015-15)

Table 2 transcribed, first and last rows locked, then the report's own four trends checked **through
the interpolator**, which is what the model actually evaluates:

| the report says | measured |
|---|---|
| A increases with stimulus exposure time | 0.251 → 0.328 e⁻/s |
| x₀ decreases with it | 97196 → 73500 e⁻, monotonic |
| α decreases with it | 0.206 → 0.126, monotonic |
| γ decreases with it | 1.269 → 0.921, monotonic |

Trend 1 is checked at the **endpoints** rather than as monotonicity, because the published fit itself
wobbles: A goes 0.282 → 0.280 between 99 s and 149 s, and 0.329 → 0.328 between 1102 s and 1402 s.
Asserting monotonicity there would be asserting something about the data that is not true.

Then the numbers quoted in prose: ~0.3 e⁻/s at 1000 s for a 10⁵ e⁻ fluence, ~0.03 e⁻/s at 10⁴ s, a
decay slope "of approximately −1" (measured −0.98), and "very little persistence (< 0.05 e/s) below a
fluence of 30,000 e ... beyond about 500 s". Plus: the Fermi knee makes the rise near saturation
steeper than proportional, and the parameters are **clamped** outside the table's 49–1402 s span
rather than extrapolated — a linear extrapolation of γ reaches zero at a finite exposure time, which
would be persistence that never decays.

## B. Integrating the rate over an exposure

The model returns a **rate**, so what a following exposure collects is its integral.
`IntegrateElectrons` does that in closed form.

| check | result |
|---|---|
| closed form vs a 200 000-step Simpson quadrature | 8.3×10⁻¹⁶ relative |
| additive over consecutive intervals | 0 |
| the γ = 1 logarithmic branch joins the general branch | continuous to 10⁻⁴ |
| **midpoint sampling instead** | **14.9 % wrong** |

That last row is why the closed form exists. γ sits near 1 across the whole table, so over a long
exposure taken soon after a bright one the rate changes by a large factor from start to finish.

## C. Interpixel capacitance (ISR 2011-10)

The kernel is checked cell by cell against Table 2, for its **published 0.9985 sum** (it is *not*
renormalised to 1 — that sum is the report's own), and for the anisotropy the report resolves:
identical above and below, identical left and right, the two pairs differing.

Then three behavioural checks:

- **A point source spreads by exactly the kernel.** This is the check that caught a real bug: the
  coupling was first written as a correlation rather than a convolution, which flips the kernel. On a
  kernel this nearly symmetric that is the 0.0001 between the left and right couplings — invisible in
  a frame, wrong in principle, and caught by asserting the response cell by cell.
- **A uniform frame stays uniform**, which is what proves the edges replicate rather than zero-pad.
  Zero-padding would darken the border by the coupling fraction and put a one-pixel ring around every
  image.
- **Agreement with an independent device.** Seshadri et al. (2008) measured a very similar HgCdTe
  array by resetting individual pixels: 1.4–1.55 % adjacent, 0.13 % corner, against this kernel's
  1.27–1.64 % and 0.11 %.

## D. Count-rate non-linearity (ISR 2019-01)

Slope and uncertainty locked at the published 0.75 % ± 0.06 % per dex. Zero correction at the anchor
by construction; exactly one slope per decade in either direction; and **3 % over four decades**,
which is the span the report itself names between standard stars and faint, sky-dominated targets.
Totality is checked too: zero in gives zero out, no negative flux at any input, and a `NaN` slope
leaves the rate untouched rather than poisoning the frame.

## E. Ramp read noise

Both published anchors reproduced exactly (20.0 e⁻ at 2 reads, 12.0 e⁻ at 15), monotonic and bracketed
in between, clamped beyond NSAMP = 15 rather than extrapolated toward zero, and consistent with the
20.2–21.4 e⁻ the handbook quotes separately for CDS alone.

## F. The shipped catalogue entry

Checks the **shipped** spec, not a restatement of it.

- Its `Name` is unique, or the flight module could not resolve a saved telescope through it.
- **Everything upstream of the channel-select mechanism is identical to the shipped UVIS entry** —
  aperture, obstruction, spider, and every platform constraint. A divergence there would be a
  transcription error, not a design choice.
- The detector against the handbook: 1014² and not 1024² (the outer rim is reference pixels), 18 µm,
  78 000 e⁻, 0.048 e⁻/s, 145 K, and the measured four-quadrant gain mean of 2.2515 rather than the
  commanded 2.5.
- The plate scale recovered from the derived focal length lands on the geometric mean of the two
  measured axes, and the field it implies (129.6″) falls between the handbook's own 123″ and 136″.
- Nothing is multiplied on top of the measured end-to-end throughput.
- The four wide filters at their published pivots, **no H-alpha slot** (the line is at 656 nm and this
  channel starts above 900 nm), and every band beyond the CIE observer's 830 nm red end — which is
  why composites cannot claim to be true colour.
- Persistence is **on**, uniquely on this roster; it does *not* also carry the CCD surface-trap model;
  and **no CCD on the roster carries infrared-array physics**.
