# spacecraft-tests

Headless cross-validation of the orbital half of the imaging pipeline (`TECHNICAL_REFERENCE.md`
§13), compiling `Core/` directly and running outside Unity and outside KSP.

```
dotnet run -c Release -p:Core=../../ExoInstruments/Core
```

Exit code 0 when every check passes, 1 otherwise.

## What it checks, and what it deliberately does not

Nothing here asserts that the code does what the code says. Every check is against **a figure
published by an observatory**, or against **a self-consistency identity between two independently
published quantities**. A test that only pins current behaviour would pass forever while the physics
drifted; these fail when the numbers stop matching their sources.

| Section | Against |
|---|---|
| 1. HST optics | The HST Primer's own three optics figures checked against each other (206265/(2.4 m × f/24) must be the published 3.58″/mm); the WFC3 focal length derived from the published pixel size and plate scale, and asserted *not* to be the OTA's bare f/24 |
| 2. Orbital visibility | The Primer's ~44 min occultation and its continuous-viewing-zone width, from geometry alone |
| 3. Zodiacal light | WFC3 IHB Table 9.4 reproduced at its own grid points, plus its stated symmetries, bounds and "within 1 magnitude" claim |
| 4. Earth-shine | SRW98's exponential meeting its own quoted plateau at the knee; its absolute level cross-checked against WFC3 Table 9.3 across two instruments a decade apart |
| 5. Delivered PSF | All ten rows of WFC3 IHB Table 6.7, rebuilt through the kernel and measured back |
| 6. Pupil | The reducibility contract against the closed-form Airy pattern; pad geometry from Tiny Tim's `wfc3_uvis1.pup` |
| 7. Pointing | The limit cycle's two regimes agreeing at their crossover; HST's published 0.008″ rms |
| 8. Aperture sampling | Equal-area sampling really giving equal area, which the blocked-fraction count depends on |
| 9. Telemetry | Frame volume as arithmetic on the published detector format |
| 10. Cosmic rays | The catalogue's event rate run in reverse to the published impacted-pixel fraction |

## Two findings this harness produced

Both are recorded in `TECHNICAL_REFERENCE.md` rather than worked around:

- **WFC3 IHB Table 6.7's 1100 nm row gives a delivered PSF narrower than a 2.4 m aperture's own
  diffraction limit** (0.089″ against 0.092″), which no telescope can deliver. It sits one row past
  the detector's published 200-1000 nm range, so it is the handbook's optical model run outside the
  band rather than a measurement. Excluded from the assertion; the pipeline correctly leaves that
  wavelength diffraction-limited.
- **The HST Primer's stated 500 km altitude and its stated 24° continuous viewing zone are not
  consistent with each other.** 500 km gives 22.0°; 24° implies 603 km. The two figures are from
  different epochs of the same decaying orbit. The harness prints the implied altitude alongside the
  check.
