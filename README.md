# ExoInstruments: Exoplanet Astrophysics in Kerbal Space Program

*Extending the observational frontier of Kerbal astronomy beyond the Kerbol system, one photon-noise-limited light curve at a time.*

---

## License Summary

This project uses a proprietary license. It is not a Creative Commons license: redistribution of copies (even unmodified ones) is not permitted here, only personal use of the original. Full legal terms are in [LICENSE](./LICENSE).

## Overview

**ExoInstruments** is an independent mod for *Kerbal Space Program 1* that replaces the game's fictional science-experiment loop with a ground-based exoplanet survey built on real observational astrophysics. Rather than abstracting "science points" from generic biome scans, the mod asks the player to run an actual survey program: select a star from a real catalog, choose an instrument appropriate to its brightness and the physics of the detection method, and extract a signal from simulated, noise-limited data exactly the way an observational astronomer would.


## Key Features

- **Physically grounded stellar catalog.** Target stars are drawn from real astronomical data, each carrying its own apparent magnitude, spectral properties, and, where a companion is known, real orbital parameters.

<p align="center"><img src="images/StarChart1.png" alt="Star chart: catalog of available stars" width="420"></p>

- **Photon-noise-limited instrumental precision.** Every instrument's achievable precision scales with target magnitude via the same photon-noise relation used in the observational literature; fainter stars are always harder, no instrument cheats the trade-off.

- **Three independent detection pipelines.**
  - **Transit photometry**: box-least-squares-style period search over a simulated light curve, with moving-median detrending and a physical duty-cycle envelope against starspot false positives.
  - **Radial velocity**: recovering the host star's reflex Doppler wobble, including multi-planet systems resolved through iterative signal prewhitening.
  - **Direct imaging**: resolving a companion at its real angular separation and thermal contrast against the diffraction limit and a decaying speckle floor.

<p align="center"><img src="images/DirectImagery.png" alt="51 Peg b: direct-imaging frame of the star hosting the first-ever discovered exoplanet" width="300"></p>

- **Simultaneous multi-planet transit modeling.** Compact systems (TRAPPIST-1 style) superpose every transiting member on one light curve; the detector separates them by iterative in-transit masking, and a multi-planet campaign pays a jackpot bonus.

- **Transit Timing Variations (TTV).** Mutual gravitational perturbation shifts each transit early or late against a linear ephemeris (Lithwick, Xie & Wu 2012); the analysis re-fits the ephemeris from measured mid-transit times and searches the residuals for the perturber's signature; pure-gravity evidence of a companion even if it never transits.

- **The Rossiter-McLaughlin effect.** A transiting planet crossing its rotating star imprints a characteristic in-transit RV anomaly (Ohta, Taruya & Suto 2005), the real measurement of spin-orbit alignment; the one mechanic where the photometric ephemeris automatically schedules the spectroscopic campaign.

- **The Mün and Minmus as observing constraints.** Both moons occult targets outright and raise the sky background with a real separation-dependent brightness law (Krisciunas & Schaefer 1991); full-Mün nights push faint targets off the schedule the way they do at real observatories.

- **Observing-quality forecast heatmap.** A porkchop-style color calendar per target/instrument (rows = nights ahead, columns = time of night) folding in twilight, altitude, airmass scintillation, and lunar occultation/moonlight. The RC20's solar-system forecast additionally factors in real EVE cloud cover over KSC. Click any cell, or the "best window" button, to warp straight there.

- **BetterTimeWarp integration (soft dependency).** When [BetterTimeWarpContinued](https://github.com/linuxgurugamer/BetterTimeWarpContinued) is installed, every "Warp to..." button in the mod uses it to lift stock KSP's silent 100,000x warp cap; without it, everything falls back to stock behavior untouched.

<p align="center"><img src="images/wasp14ab-lightcurve.png" alt="WASP-14 Ab light curve: raw time series and phase-folded transit" width="520"></p>

- **Ground-based observing windows.** Every ground-based instrument only collects data when the Sun is below twilight and the target is above the telescope's altitude limit; real diurnal gaps and window-function aliases, the same artifact real BLS searches fight.

<p align="center"><img src="images/ObservationSchedule.png" alt="Observation schedule: live table of ideal observation time" width="480"></p>

- **Stellar activity as the true noise floor.** Every star carries persistent RV jitter (Wright 2005) and quasi-periodic starspot modulation (McQuillan et al. 2014; Basri et al. 2013) that the instruments have to see past.

- **Limb-darkened transit shapes.** Transits follow the small-planet approximation of Mandel & Agol (2002) with quadratic limb darkening interpolated against stellar temperature (Claret & Bloemen 2011); round-bottomed central transits, V-shaped grazing ones.

- **Atmospheric scintillation.** Ground-based photometry pays the Young (1967) airmass tax, scaled by each instrument's real aperture and site altitude.

- **Realistic stellar color.** Every star's tint comes from its own effective temperature through a real blackbody-to-sRGB mapping.

- **Solar-system amateur astrograph (RC20).** A separate, non-exoplanet instrument: a real live-rendered photo of any Kerbol-system body, clicked directly on the sky chart (planets and moons plot there at their real size/color, right alongside the stars). A genuine timed exposure (nothing renders until the shutter time you set has actually elapsed), with exposure, ISO, a filter wheel (L/R/G/B/Hα), manual focus, and optional autoguiding (off by default; without it, an un-recentered target drifts between shots exactly like an untracked real mount). Every frame runs through a full sensor noise chain - shot noise, dark current, read noise, a fixed hot/dead pixel map, atmospheric extinction and scintillation, seeing-driven blur that worsens with airmass - plus real cloud cover and haze read live from EVE (Environmental Visual Enhancements) when it's installed. Monochrome and grainy on purpose: a single unprocessed sensor frame, not a stretched, denoised final image.

- **RC20 image stacking.** Capture a series of subs per filter and combine them into one clean LRGB composite: optional centroid alignment between frames, robust sky-background subtraction, luminance-transfer color composition (R/G/B scaled by the deeper L stack, capped against noise blow-up), an optional Hα blend into the red channel, and a display-only asinh stretch to bring out faint stacked detail; the same reason real astrophotographers shoot many short exposures instead of one long one.

<p align="center">
  <img src="images/minmus-before-stack.png" alt="Minmus: single raw sub, before stacking" width="360">
  <img src="images/minmus-after-stack.png" alt="Minmus: composite after LRGB stacking" width="360">
</p>
<p align="center"><em>Minmus: a single raw L sub (left) vs. the stacked LRGB composite (right).</em></p>

- **A meaningful instrument-acquisition economy.** Career-mode progression gates each observatory behind an acquisition cost and a cumulative Science-earned threshold, so higher-precision instruments represent a genuine capital investment, not a flat tech tree.

- **Career-mode discovery loop ("fog of war").** A star's identity and catalog status stay hidden until actually observed. A large real background-star catalog is blended in as camouflage, so anything discovered is a genuinely real system.

- **Decluttered sky chart.** A density-aware thinning pass caps how many real hosts survive per sky cell, so a single dense survey field (Kepler-style) can't give away "something's here" before it's ever been scanned.

## The Telescope Fleet

Each instrument's reference precision and cadence are drawn directly from its own instrument paper (see in-code citations).

<p align="center"><img src="images/observatory-selection.png" alt="In-game observatory selection menu" width="460"></p>

| Instrument | Type | Detection Method | Relative Noise Level | Academic Role |
|---|---|---|---|---|
| **WASP** | Wide-field, small-aperture (200 mm lens) survey camera | Transit Photometry | High (~1000 ppm) | Entry-level, low-cost fog clearing on bright, easy targets |
| **SPECULOOS** | Four 1 m robotic telescopes (Paranal) | Transit Photometry | Low (~150 ppm) | The survey's photometric workhorse; tuned for small planets around ultra-cool dwarfs |
| **TESS** | Space-based, 10.5 cm aperture, all-sky survey | Transit Photometry | High (~1095 ppm) | Space-based photometry immune to atmospheric noise, gated behind a real capital threshold |
| **SOPHIE** | 1.93 m spectrograph (Observatoire de Haute-Provence) | Radial Velocity | Moderate (~2.0 m/s) | Entry point into spectroscopic RV detection |
| **HARPS** | 3.6 m ESO telescope (La Silla) | Radial Velocity | Low (~1.0 m/s) | Long-baseline RV workhorse; the field's historical benchmark |
| **ESPRESSO** | VLT-fed ultra-stable spectrograph | Radial Velocity | Ultra-Low (~0.15 m/s) | The RV path's capstone; sub-10 cm/s, resolving Earth-mass reflex signals |
| **ELT** | 39.3 m Extremely Large Telescope (Cerro Armazones) | Direct Imaging | Contrast-limited (~10⁻⁴ at 1 λ/D) | Flagship direct-imaging capability, independent of transit or RV geometry |
| **RC20** | PlaneWave 20" astrograph | Solar-System Photography | N/A; not an exoplanet detector | A real backyard-class scope: point-and-shoot photos of planets and moons in the Kerbol system |

## Future Roadmap

Not yet implemented in the current build:

- **Autoguiding as a paid career upgrade** for the RC20, rather than a free toggle.
- **Further real astrograph features** surveyed but not built: sensor binning, plate-solving, flat-frame calibration, meridian flip, dithering.
- **Naming rights & a discovery archive.** Player-named planets on confirmation, plus an auto-generated logbook entry (light curve, date, instrument) per detection.
- **Weather in the generic instrument forecast.** EVE cloud cover is already hooked into the RC20's solar-system forecast; extending it to the exoplanet-instrument heatmap (SPECULOOS, ELT, and the other ground-based facilities) is still open.
- **A proper in-world observatory building** at the KSC (Kerbal Konstructs), replacing the current toolbar-button placeholder.
- **Space-based telescope facilities**, modeled after concept missions like ESA's LIFE, with atmospheric/biosignature classification as a further scientific payoff.
- **Deeper catalog integration** and an **extended instrument roster** (more real-world facilities as further progression rungs).
- **Economy rebalance**; current career Funds/Science values are still placeholders pending playtesting.

## Acknowledgments & Scientific Inspiration

This project's detection pipelines and instrumental modeling are directly inspired by the historic 1995 discovery of 51 Pegasi b by Michel Mayor and Didier Queloz; the intellectual origin point for this entire codebase. 51 Pegasi's exact physical parameters are integrated directly into the mod's stellar catalog.

Special thanks to the following institutions at ETH Zürich whose research and vision fueled the logic of this mod:
*   **The Queloz Group**, for pioneering the radial velocity and transit precision standards modeled in this simulation.
*   **The Exoplanets and Habitability Group**, for their invaluable contributions to planetary detection and characterization.
*   More broadly, **The Center for Origin and Prevalence of Life (COPL)**, for inspiring the grander vision behind this project.

This repository is offered as an independent demonstration of scientific outreach and computational modeling.


> ### A Personal Note
>
> *"This project is my tribute to the human curiosity, that refuses to let us be lonely in the dark. My only hope is that this mod can spark a passion for astronomy, and perhaps inspire others to follow the path of scientific studies."*

