# ExoInstruments: Exoplanet Astrophysics in Kerbal Space Program

*Extending the observational frontier of Kerbal astronomy beyond the Kerbol system, one photon-noise-limited light curve at a time.*

---

## License Summary

This project uses a proprietary license. It is not a Creative Commons license: redistribution of copies (even unmodified ones) is not permitted here, only personal use of the original. Full legal terms are in [LICENSE](./LICENSE).

**Bundled assets.** The orbital telescope's model, `ExoInstruments/Parts/OrbitalObservatory/model.mu`,
is this project's own: modelled in Fusion, exported through PartTools, and covered by the terms above
like everything else. It replaced a placeholder — Tarsier Space Technology's Deep Space Telescope,
Copyright (c) 2013 tobyb121, carried under that mod's MIT licence with the licence text beside it —
and neither that model, its texture, nor its licence file is part of this mod any more. None of
Tarsier's source code was ever used.

## Overview

**ExoInstruments** is an independent mod for *Kerbal Space Program 1* that replaces the game's fictional science-experiment loop with a ground-based exoplanet survey built on real observational astrophysics. Rather than abstracting "science points" from generic biome scans, the mod asks the player to run an actual survey program: select a star from a real catalog, choose an instrument appropriate to its brightness and the physics of the detection method, and extract a signal from simulated, noise-limited data exactly the way an observational astronomer would.


## Key Features

- **Physically grounded stellar catalog.** Target stars are drawn from real astronomical data, each carrying its own apparent magnitude, spectral properties, and, where a companion is known, real orbital parameters.

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

- **Solar-system astrograph, four real instruments.** A separate, non-exoplanet instrument mode: a real live-rendered photo of any Kerbol-system body, clicked directly on the sky chart (planets and moons plot there at their real size/color, right alongside the stars). Every optics/sensor constant driving the physics (aperture, focal length, native resolution, pixel pitch, quantum efficiency, full well, read/dark noise, exposure and gain range, per-filter bandwidth, off-axis astigmatism, adaptive-optics correction) is drawn from a per-instrument spec rather than hardcoded, so switching telescopes from the Observatory menu changes the actual physics, not just a label. The imaged body's real apparent magnitude (real albedo, radius, Sun distance, phase angle, Lambertian phase law) is converted through a real photon-flux/aperture/QE/filter-bandwidth chain into real electrons, so noise, saturation, and signal-to-noise all follow real photon statistics rather than an invented brightness scale. A genuine timed exposure (nothing renders until the shutter time you set has actually elapsed), with exposure, gain, a filter wheel matching whichever real filters that telescope actually has, manual focus, and autoguiding. Every frame runs through a full sensor noise chain modeled on real detector-simulation and CCD physics literature: shot noise, dark current, read noise (correctly rescaled for the current pixel-binning factor), a fixed hot/dead pixel map, atmospheric extinction and scintillation, seeing-driven blur (or, for an adaptive-optics instrument, its own real corrected resolution instead), full-well blooming (Janesick 2001), charge-transfer smear (Short et al. 2010's CDM model), a real sea-level cosmic-ray flux (Particle Data Group), and a sky background built from real published surface brightnesses in V magnitudes per square arcsecond (airglow at Paranal's measured 21.7 mag/arcsec² with the van Rhijn 1921 zenith-angle law, zodiacal light per Leinert et al. 1998, lunar scattering per Krisciunas & Schaefer 1991, and twilight per Patat et al. 2006), with wavelength-dependent extinction so a blue filter really does lose more light than a red one; plus real cloud cover and haze read live from EVE (Environmental Visual Enhancements) when it's installed. A selectable neutral-density filter (real photographic ND stops through a real ND 5.0 solar-filter-grade option) handles the extreme brightness of Kerbin-system moons, which sit far closer to their star than any real moon does. Monochrome and grainy on purpose: a single unprocessed sensor frame, not a stretched, denoised final image; saved as both PNG and a real 16-bit FITS file with standard acquisition-software header keywords, the same format a real telescope and camera setup would actually write.

- **The instrument roster.** Each one modeled end to end on a real, specific, named telescope and detector, not an invented gameplay scale:
  - **RC20**: a PlaneWave RC20 (f/6.8, 0.51m aperture, real secondary obstruction) with a ZWO ASI294MM Pro camera (native 4144x2822 resolution, real full well, read noise, dark current, quantum efficiency); no autoguider by default.
  - **CDK1000**: a PlaneWave CDK1000 (1.0m aperture, f/6, real 47% central obstruction), the same optical tube PlaneWave actually installed at Palomar Observatory in 2024 for MIT's WINTER project, on the same ZWO camera as the RC20.
  - **VLT FORS2**: the real Very Large Telescope, Unit Telescope 1 "Antu", 8.2m, carrying FORS2's real imager: a mosaic of two MIT/Lincoln-Lab CCID20 CCDs at their own real published plate scale, full well, gain, and read noise; always autoguided, since a real 8.2m research telescope has no unguided operating mode.
  - **VLT SPHERE**: the same VLT, Unit Telescope 3 "Melipal", carrying the real SPHERE/ZIMPOL extreme-adaptive-optics imaging polarimeter. Where FORS2 is limited by ordinary atmospheric seeing no matter the mirror size, SPHERE's SAXO adaptive-optics system corrects that turbulence in real time, reaching a real, published resolution around 25 milliarcseconds, tens of times finer. The tradeoff is real too: ZIMPOL's actual field of view is barely 3.6 arcseconds wide, and it has no blue filter at all.

- **A real star field behind every photograph.** A photograph's sky is no longer empty. The frame is built the way professional image simulators build one (GalSim, SkyMaker, ESA's Pyxel): as a sum of sources, each carrying its own independently computed flux, summed on one plane before the telescope's optics and the sensor's noise are applied — instead of one rendered image scaled to the target's brightness, under which nothing but the target could ever have had a correct brightness. Stars come from a **Gaia DR3** catalogue you build yourself (see below; nothing ships, and without one the sky is simply empty), placed by a real gnomonic tangent-plane projection (the TAN projection of the FITS standard) built from the telescope's own pointing, so they land where they actually are relative to the planet you are photographing. Each one's colour is real: its catalogue B-V gives its temperature, and its brightness is carried into whichever filter is fitted across that temperature's own spectrum, so a hot blue star and a cool orange one photograph differently through an LRGB set. Moons and planets too small for the optics to resolve are drawn through the same path from their own real apparent magnitude, which is how a giant planet's moons appear as points of light beside it. Without an autoguider the sky rotates under the instrument during the exposure and everything trails — along the true direction for your observatory's latitude, curving, with stars near the frame edge trailing further than those at its centre, because the sky's own rotation is applied rather than the image being smeared sideways.

  *Note: the exoplanet detection pipeline is untouched. It keeps searching the small Bright Star Catalogue on purpose, so finding a transit stays a tractable hunt; the rendered star catalogue exists only to fill in what a camera sees.*

- **RC20 image stacking.** Capture a series of subs per filter and combine them into one clean LRGB composite: cosmetic (bad-pixel-map) correction before alignment (the same calibration step real pipelines like PixInsight and IRAF/ccdproc run before registration), optional centroid alignment between frames, robust sky-background subtraction, luminance-transfer color composition (R/G/B scaled by the deeper L stack, capped against noise blow-up), an optional Hα blend into the red channel, and a display-only asinh stretch to bring out faint stacked detail; the same reason real astrophotographers shoot many short exposures instead of one long one. An optional **lucky imaging** mode keeps only the sharpest subs (ranked by a real variance-of-Laplacian focus metric, Pech-Pacheco et al. 2000) before stacking, following the same selective-frame principle real lucky imaging uses to beat atmospheric seeing (Fried 1978).

<p align="center">
  <img src="images/minmus-before-stack.png" alt="Minmus: single raw sub, before stacking" width="360">
  <img src="images/minmus-after-stack.png" alt="Minmus: composite after LRGB stacking" width="360">
</p>
<p align="center"><em>Minmus: a single raw L sub (left) vs. the stacked LRGB composite (right).</em></p>

- **A meaningful instrument-acquisition economy.** Career-mode progression gates each observatory behind an acquisition cost and a cumulative Science-earned threshold, so higher-precision instruments represent a genuine capital investment, not a flat tech tree.

- **Career-mode discovery loop ("fog of war").** A star's identity and catalog status stay hidden until actually observed. A large real background-star catalog is blended in as camouflage, so anything discovered is a genuinely real system.

- **A target search engine, not a name filter.** The right-hand half of the target-selection view is a search box over *everything the telescope can point at* — the planets and moons of whatever planet pack is installed, the whole star catalogue, the nebulae, every galaxy in the installed catalogue, and every Messier and named NGC/IC object — about sixteen thousand targets in a stock install with the optional catalogues. Type a name in any form it is written in and the list narrows as you type: `M31`, `NGC 224`, `NGC0224` and `Andromeda` all find one entry, `Vega` finds the Bright Star Catalogue's `alf Lyr`, and `M13` finds a globular cluster that no catalogue in this mod carries at all. Matching is on canonical **designations**, not substrings, so `NGC 24` returns NGC 24 and not the two hundred designations it is a substring of. Filter by what a thing is (`type:galaxy`, `type:nebula`, `type:cluster`, or the one-click buttons), by where it is (`in:Ori`, `in:Orion`, `in:Orionis` — the real IAU boundary, see below), by how bright (`mag:<9`) and by whether it is up right now (`alt:>30`). Every result carries its type, magnitude, apparent size, constellation, coordinates, current altitude, **and which catalogue it came from** — two rows in one list can be measured to entirely different standards, and you are entitled to know which is which before spending a night on one. Clicking a result points the telescope; the sky chart on the left simultaneously lights up every match and steps everything else back, so the list and the chart are two views of one search.

<p align="center"><img src="images/StarChart1.png" alt="Star chart: catalog of available stars" width="420"></p>

- **The IAU constellations, done properly.** Every fixed target knows which of the 88 constellations it lies in, and the search can filter on it. This is not a lookup table of approximate regions: Delporte's boundaries (adopted by the IAU in 1928, published 1930, unchanged since) are lines of constant right ascension and declination **in the mean equinox of B1875 and in no other frame**, so a J2000 catalogue position is carried there through the real chain — Murray (1989)'s FK5-to-FK4 rotation including its rotating-system term, then Newcomb's precession — before Roman (1987)'s ordered scan of the boundary arcs. `tools/constellation-tests` reproduces astropy's own FK4 transform to **3 nanoarcseconds**, reproduces all eight worked examples published with the boundary catalogue, and shows that the 0.04% of a quarter-million-point grid where it disagrees with astropy's `get_constellation` are all closer to a boundary than astropy's own two routes to "B1875" are to each other.

- **Names from the bodies that assign them.** Cross-identifications (M31 = NGC 224 = the Andromeda Galaxy) come from **SIMBAD**, which maintains them from the literature; star proper names come from the **IAU Catalog of Star Names**, the list the IAU Working Group on Star Names actually approves, rather than from the folklore that half the "traditional" star names in circulation are. Both are pulled by generators under `tools/`, not typed from memory.

- **Decluttered sky chart.** A density-aware thinning pass caps how many real hosts survive per sky cell, so a single dense survey field (Kepler-style) can't give away "something's here" before it's ever been scanned. Solar-system bodies get their own decluttering too: a planet and its own moons often land on nearly the same point of sky from Kerbin, so overlapping markers are nudged apart into a small ring, just far enough that each one stays individually clickable at any zoom level.

- **A real, upgradeable KSC Observatory building.** Not a toolbar button bolted onto stock scenery, but a genuine facility built on the same stock systems the VAB or Astronaut Complex use; real hover highlighting, a real right-click facility dialog, and a real funds-gated upgrade path. Its telescope continuously points at whatever target is currently being observed, using the same real altitude/azimuth conversion the rest of the mod already relies on, so the rig's orientation reflects the target's actual position in Kerbin's sky, not a cosmetic animation.

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
| **CDK1000** | PlaneWave CDK1000, 1.0 m Corrected Dall-Kirkham (Palomar-class) | Solar-System Photography | N/A; not an exoplanet detector | Research-grade step up from the RC20: nearly four times the light-collecting area |
| **VLT FORS2** | Real VLT Unit Telescope 1, 8.2 m, real FORS2 imager | Solar-System Photography | N/A; not an exoplanet detector | The actual Very Large Telescope, pointed at the neighborhood instead of a distant galaxy |
| **VLT SPHERE** | Real VLT Unit Telescope 3, 8.2 m, real SPHERE/ZIMPOL adaptive optics | Solar-System Photography | N/A; not an exoplanet detector | Same VLT, extreme adaptive optics: real ~25 mas resolution instead of ordinary seeing |
| **Orbital Observatory** | HST's 2.4 m OTA with WFC3/UVIS — *a part you launch yourself* | Solar-System Photography | N/A; not an exoplanet detector | The near-ultraviolet the atmosphere blocks outright, an identical PSF in every frame, and a sky with no airglow in it |

## Building a space telescope

Every other instrument above is somebody else's telescope, whose time you buy. This one does not
exist until you launch it.

**The part.** *Orbital Astrophysics Observatory*, in the Science category. Its model is the mod's own:
a bare optical-tube assembly modelled for this part, which replaced the Tarsier Space Technology
placeholder that stood in for it while there was no bespoke mesh.
It carries the telescope, its aperture door, and its own reaction wheels. It appears in the observatory's instrument list only
once you have one in orbit; there is no Funds price, because the cost is the part, the launch, and
building a spacecraft that can actually hold a target still.

**What it needs to work.** Four things, all checked, all reported by name when they fail:

1. **The aperture door open.** Toggle it in flight or by action group. It is a hard gate, not an
   animation: HST's own door exists to close over the optics if attitude control is ever lost with
   the Sun in reach.
2. **A clear view down the tube.** The mod casts a hundred rays across the telescope's real open
   pupil into your actual vessel geometry. Mount a solar panel, an antenna or a fairing over the
   open end and the panel tells you which part is in the way. There is no partial credit: a
   partially blocked pupil is not a telescope collecting less light, it is a telescope with a
   different point-spread function, and the honest answer is that observatories do not take science
   frames through their own structure.
3. **Attitude control it can hold a target with.** This is the one that actually shows up in the
   photograph. Reaction wheels hold the boresight at a *point*, and what is left is HST's published
   0.008″ jitter, a fifth of a pixel. Thrusters cannot: an on-off thruster has no small setting, so
   the vehicle drifts across a deadband and gets pulsed back, forever — the standard limit cycle,
   and at this plate scale it is **hundreds of pixels** of smear. A telescope pointed on RCS alone
   does not take a slightly worse photograph, it takes a streak. The part ships with wheels for
   exactly this reason; add more if the spacecraft is heavy.
4. **Electric charge.**

**Where you operate it from.** Flying the spacecraft, you are there: power and a clear aperture are
enough. From the observatory at the space centre, every command and every returned frame goes over
a radio, so it also needs **an antenna with a working link**. Without one it still works — you just
have to go and fly it. The frame is 32 MB of real data, which is 9 minutes on a Communotron 16 and
2 on a relay dish, so the antenna you picked is a real decision.

**What it is good at, and what it is not.** It is not the biggest telescope in the list and it does
not out-resolve the VLT: at 2.4 m it has under a twelfth of the VLT's collecting area, and its
delivered 0.067″ core is nearly three times coarser than SPHERE's adaptive optics. What it has is

- **the near-ultraviolet**, down to 200 nm, which ozone closes on the ground completely and which no
  mirror size or mountain buys back;
- **a point-spread function that is identical in every frame ever taken**, because there is no
  atmosphere to vary;
- **a sky about 1.6 magnitudes darker**, because airglow is something an atmosphere does.

**And constraints no ground telescope has.** The planet occults most targets for part of every
orbit; you cannot point within 62.5° of the Sun, 20° of the sunlit limb, 7.6° of the dark limb or
9° of a moon; and the observing window is finite, so an exposure longer than it gets cut off. The
panel shows all of it, including how long the current orbit will actually let you integrate for. A
target near your orbital pole falls in the **continuous viewing zone** and is never occulted at all.


## Solar-System Observing Guide

The right instrument for a target isn't the biggest one — it's whichever one actually *frames* the body without either overflowing the field (empty magnification) or shrinking it to a handful of pixels. The tables below are computed straight from each instrument's real aperture, focal length, and sensor (§7.00/§7.11 of the [technical reference](./TECHNICAL_REFERENCE.md)), not eyeballed.

**Reading the tables:** "px @ tight zoom" is the target's diameter in pixels once fully zoomed in (real Barlow/HR-collimator factor where the instrument has one); "% of frame" is that diameter against the sensor's own long axis at that zoom. A target well over 100% has genuinely overflowed the field — you're looking at a crop, not the whole disk.

### Stock Kerbol system (from Kerbin)

| Target | Apparent size | Best instrument | Zoom | Notes |
|---|---|---|---|---|
| **Mün** | ~6875″ (1.9°) | *(none)* | — | Too close for any instrument here — it overflows every field by 20×+. This is a naked-eye/map-view target, not a telescope one. |
| **Minmus** | ~527″ (8.8′) | RC20 / CDK1000 | **Wide** (no Barlow) | Also overflows at tight zoom; frames nicely (~46% of the wide field) with the Barlow backed out. |
| Eve | 76.7″ | CDK1000 | Tight | 46% of frame, 1926 px across. Genuinely bright (thick, reflective cloud deck) — watch the live saturation readout and dial in an ND filter if it clips. |
| Jool | 44.9″ | CDK1000 | Tight | 27% of frame, 1127 px — best balance of framing and light. FORS2 gives more light-collecting area but a wider tight-zoom field, so it frames Jool smaller (17%). |
| Duna | 18.5″ | CDK1000 | Tight | 11% of frame, 466 px — enough to show real surface contrast. |
| Moho | 12.4″ | CDK1000 | Tight | 8% of frame, 311 px — small and dim; needs a real exposure, not a snapshot. |
| Ike | 7.5″ | CDK1000 | Tight | 5% of frame — modest on every ground scope; overflows SPHERE's 3.7″ field instead of fitting it. |
| **Dres** | 2.1″ | **VLT SPHERE** | Tight (fixed) | 57% of frame, 1161 px. The dwarf-planet-class bodies (Dres, Eeloo, Gilly) are exactly SPHERE's niche — see the diffraction/AO math in §7.11. |
| **Eeloo** | 1.6″ | **VLT SPHERE** | Tight (fixed) | 44% of frame, 906 px. |
| **Gilly** | 1.4″ | **VLT SPHERE** | Tight (fixed) | 39% of frame, 797 px — Eve's tiny moon is unresolvable anywhere else. |
| **Vall** | 2.2″ | **VLT SPHERE** | Tight (fixed) | 61% of frame, 1247 px. |
| **Laythe** | 3.7″ | **VLT SPHERE** | Tight (fixed) | ~101% of frame — fills it almost exactly. |
| **Tylo** | 4.5″ | **VLT SPHERE** | Tight (fixed) | 122% — a slight crop, still the best option by far. |
| Bop | 0.5″ | VLT SPHERE | Tight (fixed) | Only 13% of frame, but that's still 271 px — SPHERE's fine plate scale (1.8 mas/px) resolves it where every other instrument gives single-digit pixels. |
| Pol | 0.3″ | VLT SPHERE | Tight (fixed) | The hardest real target in the roster: 183 px, 9% of frame. |

The pattern above isn't a coincidence: SPHERE dominates every small/icy/rocky body (Jool's moons, the dwarf planets) exactly the way the real VLT/SPHERE dominates that same class of target in actual observing programs — the mod converges on the real instrument's real niche because the optics feeding it are real, not because anyone tuned it to do so.

### Real Solar System (RSS, from Earth)

Distances and magnitudes at *best* elongation/opposition — actual framing on any given night will be worse than the table shows; check the in-game `disk X" = Y px` diagnostic line for the real value at the time.

| Target | Best diameter | Best instrument | ND filter needed (FORS2, min exposure) | Notes |
|---|---|---|---|---|
| Moon | 1800″ | RC20 / CDK1000 | ND100000 | Overflows every field at tight zoom (600%+) — shoot it wide. |
| Venus | 66″ | RC20 / CDK1000 | ND1000 | 23–40% of frame on the amateur scopes. |
| **Saturn (+rings)** | 46″ | **VLT FORS2** | **ND64** | The best VLT FORS2 target by far: same resolved detail as Jupiter, 12× less saturation. |
| Jupiter | 49.9″ | VLT FORS2 | ND1000 | Good detail (77 resolution elements) but needs the stronger ND — this is what "impossible with ND8" looks like. |
| Mars | 25.1″ | VLT FORS2 | ND1000 | 39 resolution elements at opposition; far fewer near conjunction (3.5″). |
| Mercury | 13.0″ | VLT FORS2 | ND100000 | Small and close to the Sun — a genuinely hard real target, same as in life. |
| **Neptune** | 2.4″ | **VLT SPHERE** | none | The best SPHERE target: 65% of the 3.7″ field, ~473 s exposure for good SNR. |
| Ganymede | 1.72″ | VLT SPHERE | none | 47% of frame, ~13 s exposure. |
| Callisto | 1.58″ | VLT SPHERE | none | 43% of frame, ~30 s exposure. |
| Io / Europa | 1.05–1.22″ | VLT SPHERE | none | 28–33% of frame, ~9 s exposure. |
| Titan | 0.90″ | VLT SPHERE | none | 24% of frame, ~96 s (Saturn's haze makes it faint per unit area). |
| Ceres / Vesta | 0.60–0.70″ | VLT SPHERE | none | 16–19% of frame, 3–15 s. |
| Pluto | 0.11″ | VLT SPHERE | none | 3% of frame — the hardest real target in the whole mod. |
| Uranus | 3.8″ | *either* | ND64 (FORS2) | Right at SPHERE's field edge (103%) — FORS2 also works, with a stronger filter. |

**RC20/CDK1000 on real-solar-system targets:** at their real minimum exposure (32 µs), even Jupiter or the Moon barely register through a 0.51–1.0 m amateur aperture at real interplanetary distances — saturation isn't the risk, under-exposure is. Raise exposure and gain rather than reaching for an ND filter, and use the live diagnostic line to dial it in.

### Memory cost per capture

A capture is monochrome — one value per pixel — but the pipeline currently stores it duplicated three-fold in `Color` buffers along the way. Numbers below are exact, computed from every frame-sized buffer the pipeline actually allocates:

| Config | Megapixels | Managed heap | GPU textures | **Total** |
|---|---|---|---|---|
| FORS2, 1×1 | 16.91 | 1226 MB | 419 MB | **1645 MB** |
| FORS2, 2×2 | 4.23 | 306 MB | 105 MB | 411 MB |
| FORS2, 4×4 | 1.06 | 77 MB | 26 MB | 103 MB |
| RC20 / CDK1000, 1×1 | 11.69 | 848 MB | 290 MB | 1138 MB |
| RC20 / CDK1000, 4×4 | 0.73 | 53 MB | 18 MB | 71 MB |
| SPHERE, 1×1 | 4.19 | 304 MB | 104 MB | 408 MB |
| SPHERE, 4×4 | 0.26 | 19 MB | 6 MB | 26 MB |

**2×2 is the practical default** on any instrument: it keeps memory well under a gigabyte even on FORS2 while still resolving several hundred pixels across a well-framed target — more than the seeing/diffraction limit can usually deliver anyway (see §7.11). Reach for 1×1 only when you specifically need the extra pixels and have the headroom for it.

## Data files: what to install

**Nothing but the plugin ships.** Every sky survey this mod reads is someone else's published data,
often hundreds of megabytes, and vendoring it would be both a licensing question and a download
nobody asked for. Each one is optional and independent: with none of them installed the instruments
work and photograph the solar system, and each file you add turns on one more thing.

All of them are built by a script in `tools/` and copied to
`<KSP>/GameData/ExoInstruments/PluginData/`.

| File | Size | Source | What it turns on | Section |
|---|---|---|---|---|
| `GaiaStarCatalog.starcat` | 88 MB at G ≤ 13 | Gaia DR3, via the ESA archive | The star field in every photograph | [below](#the-star-field-is-user-supplied-build-it-from-gaia-dr3) |
| `DustMap.dustmap` | 24 MB | SFD98 via `dustmaps` | Interstellar reddening, and the extinction readout | [below](#optional-the-interstellar-dust-map) |
| `HalphaMap.emission` | 24 MB | Finkbeiner (2003) via NASA LAMBDA | Diffuse Hα, [N II] and [S II] in narrowband | [below](#optional-the-h-alpha-emission-map) |
| `GalaxyCatalog.galcat` | 0.9 MB at B ≤ 15 | HyperLEDA | Galaxies, drawn from their measured shape | [below](#optional-the-galaxy-catalogue) |

Each script prints named sanity checks as it runs — M31 must come out 3.2° across at B_T 4.4, Sgr A*
must land at Galactic (0, 0) — so a units error or a wrong file fails loudly instead of producing a
plausible sky. If a script says nothing looks familiar, stop and check the input.

### All four, in order

Set `KSP` to your install and run these from the repository root. The sections further down explain
what each one is and why; this is the whole install.

```bash
KSP="$HOME/Library/Application Support/Steam/steamapps/common/Kerbal Space Program"
cd tools
python3 -m venv env
./env/bin/pip install numpy scipy astropy healpy requests dustmaps
```

**1. The star field.** Needs a free ESA archive account
(<https://cosmos.esa.int/web/gaia-users/register>); the password is prompted for, never taken on the
command line. No packages needed — the archive speaks plain HTTP. Hours, but it resumes if
interrupted. Try the one-minute cone first:

```bash
python3 pack_gaia_catalog.py --gmax 13 --cone 83.822 -5.391 1.0 --out /tmp/test.starcat
python3 pack_gaia_catalog.py --gmax 13 --out GaiaStarCatalog.starcat --user YOUR_ESA_USERNAME
cp GaiaStarCatalog.starcat "$KSP/GameData/ExoInstruments/PluginData/"
```

**2. The dust map.** Fetches SFD98 (~150 MB) on first run.

```bash
./env/bin/python pack_dust_map.py --out DustMap.dustmap
cp DustMap.dustmap "$KSP/GameData/ExoInstruments/PluginData/"
```

**3. The Hα map.** The 49 MB source file is downloaded by hand on purpose: the archive's URLs move,
and a wrong file silently producing a plausible sky is worse than an error. **Take the nside 1024
file, not the 512** — see the section below for why.

```bash
curl -O https://lambda.gsfc.nasa.gov/data/foregrounds/fink_halpha/Halpha_fwhm06_1024.fits
./env/bin/python pack_halpha_map.py --input Halpha_fwhm06_1024.fits --out HalphaMap.emission
cp HalphaMap.emission "$KSP/GameData/ExoInstruments/PluginData/"
```

**4. The galaxies.** Queries HyperLEDA directly and takes about a minute. `--bmax 13` gives 1454
galaxies in 82 KB and is plenty for the RedCat; `--bmax 15` gives 15 732 in 0.9 MB. It prints M31,
M33, M87, M51 and M77 with their parameters as it finishes — M31 must come out at **B_T 4.29, D25
177.8′, b/a 0.392, PA 35°**, and if it does not, stop rather than install the result.

```bash
./env/bin/python pack_galaxy_catalog.py --bmax 13.0 --out GalaxyCatalog.galcat
cp GalaxyCatalog.galcat "$KSP/GameData/ExoInstruments/PluginData/"
```

Then start KSP and check the log. Every file that loaded says so, with its own provenance:

```
[ExoInstruments] Rendered star field: 7369627 Gaia DR3 stars loaded.
[ExoInstruments] Dust map: nside 1024 (3.4 arcmin), SFD98 ... x0.86 ...
[ExoInstruments] Emission map: H-alpha at nside 1024 (3.4 arcmin), Finkbeiner ...
[ExoInstruments] Galaxy catalogue: 4812 galaxies, HyperLEDA (Makarov et al. 2014, A&A 570, A13)
```

A file that is missing says that instead, and names the script that builds it. Nothing fails
silently, and nothing is required.

## The star field is user-supplied: build it from Gaia DR3

**No star catalogue ships with the mod.** Without one, the sky behind a photographed body is empty.
Building one is a single command and gives you a real, correctly-placed, correctly-coloured star
field in every frame.

### Why nothing ships

The mod used to ship **Tycho-2**: 29.3 MB for 2.5 million stars complete to about V = 11.5. That is
61.9 stars/deg², so an RC20 frame held roughly **four** stars where a real 30-second sub holds
hundreds. It was the worst of both worlds, 29.3 MB carried to deliver a sky that still looked empty.

A real star field means Gaia, and Gaia's own counts say what that weighs at the 14 bytes/star
this format uses:

| Faint limit | Stars | File, and RAM while playing |
|---|---|---|
| G < 13 | 7.4 M | **103 MB** |
| G < 14 | 16.8 M | **236 MB** |
| G < 15 | 36.9 M | **517 MB** |
| G < 16 | 78.0 M | **1.1 GB** |
| G < 18 | 304.9 M | **4.3 GB** |

Counts are `SELECT COUNT(*) FROM gaiadr3.gaia_source WHERE phot_g_mean_mag < N`, asked of the
archive rather than estimated. An earlier version of this table was shifted by one magnitude and
its `G < 18` row was nearly double the real count.

Memory is exactly 14 bytes/star. That was 12 before the reddening column landed (format version 3),
so a catalogue costs a sixth more than it used to.

None of that belongs in a mod download. On your own disk it is fine. So the choice is a real star
field or an honestly empty one, rather than a heavy download that delivers neither.

### Building it

```
python3 tools/pack_gaia_catalog.py --gmax 13 --out GaiaStarCatalog.starcat --user YOUR_ESA_USERNAME
```

**Try a cone first.** One command, under a minute, and it tells you the whole chain works before you
commit to a run measured in hours:

```
python3 tools/pack_gaia_catalog.py --gmax 13 --cone 83.822 -5.391 1.0 --out /tmp/test.starcat
```

That is a 1° cone on the Orion Nebula. It should report a few hundred stars, a handful without a
colour index, and roughly half without a reddening estimate — toward Orion the archive has
`ag_gspphot` for 57% of sources brighter than G = 13, and the packer neither invents the rest nor
drops them.

**Register a free ESA archive account first** (<https://cosmos.esa.int/web/gaia-users/register>) and
pass it with `--user`. This is not optional in practice. Anonymous access is the archive's degraded
mode, and it hits a wall that retrying cannot get past: measured on Gaia DR3, one source_id range
whose row count answers in 5 seconds fails its data fetch at 116 s on every single attempt, while
the range next to it, of almost the same size, returns in 7 s. The query planner picks a scan for
some ranges and the job is killed before it finishes.

The password is never taken on the command line, which would put it in your shell history: the tool
prompts for it without echo, or reads `GAIA_PASSWORD` if you prefer to set it yourself.

No packages to install: the ESA archive speaks plain HTTP, so this runs on the Python 3 that
already ships with macOS and most Linux distributions.

**It is still not instant.** The tool counts each range before fetching it, splits any that is too
big, retries a refused one with backoff, and caches every completed range to `<out>.cache` so a run
that dies resumes instead of starting over. Leave it going and come back to it.

Then copy the result to:

```
<KSP>/GameData/ExoInstruments/PluginData/GaiaStarCatalog.starcat
```

The `.starcat` extension matters: Kopernicus reads every `*.bin` in GameData as a scaled-space
mesh, and would try that on a 100-500 MB catalogue at every startup. That is the only star
catalogue the renderer looks for. The log line on startup tells you whether
it found one. To go back to an empty sky, delete or rename the file.

### Choosing a depth

The whole catalogue is held in memory, so the table above is also the RAM cost, **on top of KSP
itself**. `G < 13` is a safe first try and already about three times the star count of the Tycho-2
file this replaces, at a far deeper limit; `G < 15` is as deep as most machines will want. Beyond
that, know what your RAM is doing.

Search cost does *not* grow with catalogue size (the format is banded in declination and
binary-searched in right ascension, so a frame only ever touches the stars near it). What does grow
is how many stars get drawn per frame, which is the entire point, and that has been measured too:
the worst realistic case in the whole roster is the RedCat 51's 13.2 deg² field at `G < 15` toward
the Galactic plane, 43 000 stars in one unguided frame with 54-pixel trails, which deposits in
**8 ms**. The instrument's PSF convolution over the same frame costs 552 ms, so the star field is
not the expensive part and never becomes it.

### What you actually get

A 0.3° cone toward the Galactic centre at `G < 15` holds **3264 stars/deg²**: about **220 stars in a
single RC20 frame**.

Photometry is converted from Gaia's G and G_BP − G_RP to the Johnson V and B−V the rest of the mod
works in, using **Gaia's own published relations** (DR3 documentation Table 5.9). This matters more
than it sounds: a heavily reddened bulge star has `G − V = −3.56`, so a `G < 15` cut reaches
V = 18.6 for the reddest stars. Stars with no measured Gaia colour keep G as V and are flagged as
colourless rather than given an invented colour.

Proper motions are dropped: at the finest plate scale modelled here they would move a typical field
star by one pixel every few years of in-game time.

### Interstellar reddening (format version 3)

Gaia's photometry is **observed** photometry: `G` and `G_BP − G_RP` already carry whatever dust sits
in front of the star, and nothing in the conversion above deredden them. That used to leave a hot
star behind two magnitudes of dust indistinguishable from an intrinsically cool one, and the
photometry modelled both as the cool one — integrating a Planck curve at a temperature the star does
not have. On FORS2's 7700 Å band that is worth a median 57 mmag and up to 807 mmag; on the RC20's
2600 Å band, 8 and 166 mmag. Ten times worse on the wider band, because a *shape* error needs band
to act on.

Version 3 carries a per-star `E(B−V)` so the two separate: deredden the colour for the real
photosphere, and put the extinction curve into the bandpass integral as a shape. **Nothing is
attenuated twice** — the integrand is normalised at Johnson V, the extinction factor is written
normalised at V too and is exactly 1 there, so the observed magnitude still sets the flux and only
its distribution across the band changes. `tools/reddening-tests` checks that "exactly", not to a
tolerance.

The estimate is **Gaia's own**: `gspphot` fits an atmosphere model to each source's BP/RP spectrum
and parallax and reports the extinction that fit implies, so it is per-source and needs no distance.
Where `gspphot` has no solution the star is drawn exactly as version 2 drew it, which is honest
rather than filled in from a sight-line average that would be wrong for a foreground star.

**Version 2 files still load.** They simply have no reddening column, every star reads as "not
estimated", and the result is bit-identical to before. Rebuilding is worth doing when convenient,
not urgent — and note that a version 2 run's `<out>.cache` is **not reusable**, because it was
fetched without the `ag_gspphot` column.

## Optional: the interstellar dust map

Total Galactic reddening along the line of sight, reported with every frame. **Nothing ships.**

```
cd tools
python3 -m venv env && ./env/bin/pip install dustmaps healpy numpy astropy
./env/bin/python pack_dust_map.py --out DustMap.dustmap
cp DustMap.dustmap "<KSP>/GameData/ExoInstruments/PluginData/"
```

The first run fetches **SFD98** (Schlegel, Finkbeiner & Davis 1998, ApJ 500, 525), about 150 MB,
once. It is applied with the 0.86 recalibration of Schlafly & Finkbeiner (2011, ApJ 737, 103), which
is the standard use of SFD98 today. `--map planck` uses Planck's GNILC map instead (5′ rather than
6.1′, but a 1.6 GB fetch). At nside 1024 — finer than either source map's own beam — the whole sky
is 24 MB and loads in one block.

**This is the TOTAL column**, so it describes what lies beyond the whole Galaxy. It is deliberately
never applied to a catalogue star: a star sits *inside* the Galaxy with an unknown fraction of the
column in front of it, and using this on one would over-redden every foreground star by up to the
whole column. That is what the per-star `gspphot` estimate above is for.

What it does instead is get reported. The observing panel states `E(B−V)` and `A(V)` toward the
field, and an exported FITS carries `EBV` and `AV` with the map's provenance in a `COMMENT`. Both
are **omitted** rather than zero-filled when no map is installed: a missing keyword says "not
measured", a zero would say "no dust", and only one of those is true.

## Optional: the H-alpha emission map

Diffuse ionised gas, which is what the narrowband filters exist for. **Nothing ships.**

Download the composite from NASA's LAMBDA archive — the script deliberately does not fetch it, since
the archive's URLs move and a wrong file silently producing a plausible sky is worse than an error:

<https://lambda.gsfc.nasa.gov/product/foreground/fg_halpha_get.html>

```
curl -O https://lambda.gsfc.nasa.gov/data/foregrounds/fink_halpha/Halpha_fwhm06_1024.fits
cd tools
python3 -m venv env && ./env/bin/pip install healpy numpy
./env/bin/python pack_halpha_map.py --input ../Halpha_fwhm06_1024.fits --out HalphaMap.emission
cp HalphaMap.emission "<KSP>/GameData/ExoInstruments/PluginData/"
```

That file is 49 MB, HEALPix nside 1024, and is the **Finkbeiner (2003, ApJS 146, 407)** composite of
WHAM, VTSS and SHASSA, already published in rayleighs — the unit the photometry converts from, so
nothing is reinterpreted on the way in. Packed it becomes 24 MB, reproducing the source to 0.0488%,
which is exactly half-float precision and therefore the whole cost of storing it.

**Take the nside 1024 file, not the nside 512 one the same page offers.** They are the same product:
degrade the 1024 to 512 and it matches the native 512 file to 0.8% in the median. But the map's beam
is 6′ FWHM and nside 512 gives **6.87′ pixels, coarser than the beam itself** — undersampled by a
factor 2.3 against Nyquist. The two disagree by 7.3% at the 90th percentile and the 512 loses 130 R
off the brightest peak in the sky. nside 1024's 3.44′ pixels sample a 6′ beam properly. The packer
keeps whatever nside the input has: going finer would store interpolation rather than data.

**6 arcmin is the data's limit**, not the renderer's. That is 94 pixels across on the RedCat and
1300 on the RC20 behind its Barlow, so the map shows real structure in a wide field and a smooth
glow at high magnification. No published all-sky Hα map does better.

It is deposited **only** when the active filter admits at least one line, so a frame that cannot see
gas costs nothing. That gating is the same `ThroughputAt` call that supplies the coefficient, so the
two cannot disagree.

### What the other lines do

The map measures Hα, but a filter rarely admits Hα alone — a 7 nm filter centred on it also passes
[N II] 6548 and 6584, twenty angstroms away. Those, and the [S II] doublet, are **derived from the
physics that sets them** rather than from a ratio picked to look right.

Hα is a *recombination* line: its emissivity is fixed by how many protons recombine and falls only
slowly with temperature. [N II] and [S II] are *collisionally excited* — an electron has to be
knocked about 2 eV up — so they carry `exp(−E/kT)` and rise steeply with it. The ratio between them
is therefore a **thermometer**, and that is exactly how it is used observationally: Madsen, Reynolds
& Haffner (2006, ApJ 652, 401) measure the warm ionised medium's temperature by inverting these
expressions. The emissivity ratios are Haffner, Reynolds & Tufte (1999, ApJ 523, 223) eq. 1–2, with
gas-phase N/H = 7.5 × 10⁻⁵ and S/H = 1.86 × 10⁻⁵.

Nitrogen needs no ionisation correction — charge exchange with hydrogen locks N⁺/N to H⁺/H — which
is why [N II]/Hα is the cleaner thermometer. Sulphur has no such lock, so S⁺/S stays explicit; it is
obtained the way the papers obtain it, from the observed [S II]/[N II], which is nearly independent
of temperature because the two lines sit within 2% of the same excitation energy.

The one modelled step is the temperature itself, interpolated logarithmically between two measured
anchors: **6500 K at 1000 R** (a classical H II region, which cools efficiently) and **10 000 K at
1 R** (faint high-latitude gas). That reproduces the WIM's most robust observed property — bright
nebulae are Hα-dominated, faint diffuse gas is [N II]-rich — and the frame reports the temperature
it used, because it is the one number here that is a model rather than a measurement.

`tools/emission-tests` checks the result against the published values at both ends: [N II]/Hα comes
out **0.26 at 6000 K** (H II regions are measured at 0.15–0.35) and **0.73 at 8000 K** (the WIM at
0.3–0.9), with [S II]/[N II] flat to 9% across 6000–10 000 K while [N II]/Hα moves 432% — the
signature of a temperature gradient rather than an abundance one.

**[O III] 5007, [O II] 3727 and [O I] 6300 are deliberately not synthesised.** [O III] needs O⁺⁺,
which needs photons above 35 eV; the diffuse gas is lit by Lyman continuum that leaked out of H II
regions and is far too soft to make much of it, so [O III] does **not** track Hα — it is strong in
planetary nebulae, supernova remnants and a few hot cores, and weak everywhere between. [O I] traces
the neutral boundary rather than the ionised gas. Deriving either from an Hα map would be inventing
a sky, so those filter positions stay empty until a survey of their own is installed.

## The resolution limit, and what was done about it

This is the mod's largest known limitation, and it is worth stating precisely because it is a
property of the available data rather than of the code.

The Finkbeiner composite has a **6 arcmin beam**. Everything that makes a nebula recognisable in a
photograph is finer than that:

| structure | size | beams across | what a frame shows |
|---|---|---|---|
| Horsehead, the head itself | 3′ | **0.5** | nothing |
| M42 Trapezium / Huygens region | 5′ | **0.8** | nothing |
| Horsehead, whole silhouette | 8′ | **1.3** | nothing |
| Filaments in IC 1396A | 2′ | **0.3** | nothing |
| M42's wings and dark lanes | 10′ | 1.7 | a smudge |
| Elephant's Trunk IC 1396A | 20′ | 3.3 | a smudge |
| Rosette pillars | 10′ | 1.7 | a smudge |
| Rosette ring | 80′ | 13.3 | its outline |
| M42 as a whole | 85′ | 14.2 | its outline |
| North America | 120′ | 20.0 | its outline |
| IC 1396 as a whole | 170′ | 28.3 | its outline |

Only the last four render as shapes, and none of the detail inside them does. A real astrophotograph
works at 2 arcseconds — **180 times finer** — which is why the pictures look different. No display
setting, stretch or stacking recovers information the file does not contain.

Two further consequences worth knowing:

* **A dark nebula cannot be rendered at any resolution from an emission map.** What defines the
  Horsehead is the *absence* of light where dust blocks the emission behind it, and the map holds
  only what is emitted. `Core/DeepSkyCatalog` marks dark nebulae as such and the sky chart says so.
* Around the very brightest object in the sky the composite carries a visible **artefact**: a ridge
  about 10′ wide and 1.5° long through M42, whereas M42 is a roughly round 85′ × 60′ nebula. It is
  in the published file — reading it with `healpy` directly reproduces it — and is most likely a
  saturation bleed in the survey images the composite mosaics.

### The patch layer

The one thing that helps is finer data, and it exists over part of the sky. **SHASSA** (Gaustad,
McCullough, Rosing & Van Buren 2001, PASP 113, 1326) images everything south of **+15° declination**
at **0.8 arcmin** — 7.5× finer, at which the Horsehead spans 10 elements instead of 1.3.

```
cd tools
python3 -m venv env && ./env/bin/pip install numpy scipy astropy healpy requests
./env/bin/python pack_shassa_patches.py \
    --composite "<KSP>/GameData/ExoInstruments/PluginData/HalphaMap.emission" \
    --out HalphaPatches.patchset
cp HalphaPatches.patchset "<KSP>/GameData/ExoInstruments/PluginData/"
```

Cutouts come from **NASA SkyView**, which mosaics and reprojects SHASSA on request, so nothing has to
download the survey's 2.3 GB of fields.

**Why patches and not a finer all-sky map.** Resolution is only worth storing where there is
something to resolve. All-sky at 0.86′ is 201 million HEALPix cells and **403 MB**, nearly all of it
diffuse background that 6′ already describes perfectly. A degree or two around each catalogued object
is about **5 MB for the whole catalogue** — eighty times smaller for the same result on every target
anyone actually points at. Outside a patch the all-sky map answers, which is the layered arrangement
every real survey archive uses. The frame reports which layer it came from and at what sampling.

**The calibration is measured, not assumed.** SHASSA's own pixel units are not taken on trust. Each
cutout is smoothed to the composite's 6′ beam and regressed against the composite over the same area,
which *measures* the scale between them and prints it. The patch then stores

    composite  +  scale × (cutout − smoothed cutout)

so the absolute calibration stays exactly the composite's and SHASSA contributes only structure finer
than 6′, which is the only thing it is being used for. Smoothing a patch back to 6′ returns the
composite. The fine term is apodised to zero across the patch's outer margin, so a patch joins the
base map continuously instead of leaving a step. Measured residual after matching at 6′: about 20%,
which is the uncertainty on the *amplitude* of the fine structure and is reported per patch.

**What it does not fix, and one thing it exposes.** Two of these objects are unusable at any
resolution: both all-sky Hα surveys carry a **detector bleed streak** through M42; a saturated
core spills charge along a CCD row, leaving a bright horizontal spike across 31% of one row of the
cutout, and Carina and the Tarantula have milder versions. It is in the published data and nothing
here removes it, so the packer detects it (by contrast against the vertical neighbourhood, since a
row percentile cannot separate a one-row trail from a row that merely crosses a bright nebula) and
warns per patch. The per-patch **rim agreement** with the base map is printed too, and it is the
number that says which targets are worth pointing at: the Horsehead, Flame and Eagle join within 8%,
the Lagoon within 10%, the Rosette within 26%, while M42's rim disagrees by **392%**.

SHASSA stops at +15°, so IC 1396 (+57°), North America (+44°), the Heart and
Soul, the Bubble and the Cave stay at 6′. VTSS covers the northern plane at 1.6′ and is the obvious
next step. And even at 0.8′ these are survey images: M42's Trapezium spans 6 elements rather than
0.8, which is a nebula rather than a smudge, but it is not a two-arcsecond astrophotograph and will
not look like one.

### Looking at a field without the game

`tools/preview_field.py` projects an installed map through an instrument's exact geometry and
writes a PNG of nothing but the map -- no PSF, no noise, no detector:

```
./env/bin/python preview_field.py --ra 05:41:00 --dec="-02:12:17" --instrument redcat --binning 1 --zoom
```

It exists to split one question in two. When a frame shows something odd, it either comes from the
DATA the survey holds toward that direction or from the pipeline that turns it into electrons.
Anything visible in the preview is in the survey; anything visible in the game but not in the
preview is in the pipeline. It also prints how many frame pixels one map cell spans, which is the
number that identifies a cell-scale artefact.

### Continuum-subtraction residuals, and how they are removed

SHASSA removes stellar continuum by scaling an off-band image and subtracting it from the H-alpha
one (Gaustad et al. 2001, PASP 113, 1326). The scaling is one number for a whole field, so it cannot
be right for every stellar colour at once, and on the brightest stars it misses: **too much
subtracted leaves a hole, too little leaves the star itself**. Both signs occur and neither is
emission.

**Thresholding on value cannot tell those from real structure** -- an H II region has genuine knots
five times its local median and a dark globule genuinely reaches a tenth of it. The residuals are
distinguishable by their *cause*, which is a catalogued object at a known position. Measured on the
Horsehead patch: 154 cells depart from their neighbours by more than a factor 2.5 either way, **43 of
them within 5 arcmin of Alnitak** and the nearest 0.5 arcmin from it, while sigma Ori at V = 3.80,
Alnilam at V = 1.69 and HD 37903 at V = 7.83 have **none between them**.

So a cell is repaired only where both hold: it departs from its own neighbours by more than a factor
2.5, **and** it lies within the masking radius of a star bright enough to produce a residual. Real
structure fails the second test wherever it is. The radius scales as the square root of the stellar
flux -- the subtraction error at a given radius is a fixed fraction of the stellar profile there, so
the radius at which it falls below the sky's noise grows as sqrt(flux) -- anchored at 10 arcmin for
V = 1.77 and cut off at V = 4.5, where it is 1.8 arcmin, about two cells. The star positions are the
Gaia catalogue's own, so a residual and the star that made it cannot end up in different places.

Repaired cells are then filled from the neighbours that survive: same survey, same calibration, same
resolution, so there is no seam. The load message reports the count and nothing is claimed to have
been measured there.

### Which H-alpha survey to install, per target

Resolution is **free at render time**. The deposit is one interpolated lookup per frame pixel
whatever the map's resolution, measured over a full 4144 x 2822 frame:

| map resolution | cell size | per lookup |
|---|---|---|
| nside 256 | 824 arcsec | 226 ns |
| nside 1024 | 206 arcsec | 233 ns |
| nside 4096 | 51.5 arcsec | 233 ns |
| nside 8192 | 25.8 arcsec | 237 ns |

A thirty-two-fold increase in resolution costs **5%**. Storage grows; time does not.

What limits you is coverage, not cost. The deep narrow-band surveys are Galactic-plane surveys:

| survey | resolution | coverage | continuum |
|---|---|---|---|
| **IPHAS** DR2 (Drew et al. 2005; Barentsen et al. 2014) | 0.33"/px | 29 < l < 215, \|b\| < 5, north | photometric r, i per star |
| **VPHAS+** DR4 (Drew et al. 2014) | 0.21"/px | southern plane, \|b\| < 5 | photometric u, g, r, i per star |
| **SHS** (Parker et al. 2005, MNRAS 362, 689) | 0.67"/px | dec < +2, \|b\| < 10 | film, flux-calibrated to SHASSA |
| **SHASSA** (Gaustad et al. 2001) | 0.8'/px | dec < +15, all-sky | one scaling per field |
| **VTSS** (Dennison et al. 1998) | 1.6'/px | dec > -15 | one scaling per field |

IPHAS and VPHAS+ are the ones that **eliminate the cause**: they image r and i alongside H-alpha, so
the continuum is removed per star with its own measured colour rather than by one scaling for a
whole field. Where they reach, the residuals do not exist.

Against the fourteen patches this project ships positions for:

* **Ten are in the plane** and covered at sub-arcsecond resolution -- the Rosette by IPHAS; the
  Seagull, Carina, Rim, Cat's Paw, Lobster, Lagoon, Trifid, Eagle and Omega by VPHAS+ and SHS. That
  is a factor 150 to 230 finer than SHASSA, and no subtraction residuals.
* **Four are not**: M42 at b = -19.3, the Flame at -16.3, the Horsehead at -16.8 and the Tarantula
  at -31.7. Every deep survey stops at \|b\| < 10. For those, **SHASSA at 0.8 arcmin is the best
  data that exists**, and its residuals have to be repaired rather than out-resolved.

## Optional: the galaxy catalogue

**Nothing ships.** Galaxies are rendered from their own measured shape, so the catalogue supplies
four quantities per object: total B magnitude, the diameter of the 25 B-mag/arcsec² isophote (D25),
the axis ratio of that isophote, and its position angle.

```
cd tools
python3 -m venv env && ./env/bin/pip install numpy requests
./env/bin/python pack_galaxy_catalog.py --bmax 15.0 --out GalaxyCatalog.galcat
cp GalaxyCatalog.galcat "<KSP>/GameData/ExoInstruments/PluginData/"
```

The packer queries **HyperLEDA** (Makarov et al. 2014, A&A 570, A13) directly. If the archive is
unreachable, export the same columns by hand from <http://atlas.obs-hp.fr/hyperleda/> and pass
`--input leda.csv`. `--bmax 15` keeps 15 732 galaxies in 0.9 MB; `--bmax 13` keeps 1454 in 82 KB and is
plenty for the RedCat.

HyperLEDA is the homogenised compilation that folds in RC3 (de Vaucouleurs et al. 1991), the
classical source for exactly these parameters, plus everything measured since.

**Why D25 and not a half-light radius.** D25 is what the wide-field catalogues actually measure for
tens of thousands of galaxies; fitted half-light radii exist for far fewer. The conversion costs
nothing in rigour: a total magnitude and an isophotal diameter **over-determine** a two-parameter
profile, so the half-light radius follows from the two together with no free constant. The profile
shape comes from the morphological type — de Vaucouleurs (1948) R^(1/4) for spheroids, Freeman
(1970) exponential for disks, which are Sérsic n = 4 and n = 1 — and where a catalogue carries a
fitted index the packer stores it and flags it as measured.

Where the two are inconsistent (a galaxy too faint in total to reach 25 mag/arcsec² anywhere at its
quoted size) there is no solution, and the renderer keeps the **size**, which is what the frame
shows, rather than the isophote, which it does not.

Photometry goes down the same path as a catalogue star, with the Galactic foreground extinction
applied in full — a galaxy sits behind the whole column, which is the one case the dust map's total
reddening applies to without qualification.

The packer prints five named galaxies as it finishes, so a units error cannot pass silently: M31
must come out at B_T 4.29 and D25 177.8′ (2.96°), M87 at 7.11′ and b/a 0.938.

Galaxies are drawn as crosses on the sky chart down to B = 11, sized to their own extent; the camera
has no such cut and draws whatever clears the frame's noise floor. `tools/galaxy-tests` validates the
profile against SciPy and astropy: b_n to 4 × 10⁻¹⁵, the surface brightness against astropy's
`Sersic2D` to 1.2 × 10⁻¹³, the deposited flux to 4.5 × 10⁻⁴ over 81 shapes.

## Optional: real galaxy imagery, so a galaxy is not a smooth ellipse

A Sérsic profile fitted to four catalogued numbers is an ellipse of light, and that is all it can
ever be. Measured over the 156 galaxies the sky chart plots, on the RC20 at 4×4 in a 300 s sub: M31
peaks at **10.4σ** above the sky and reaches 3σ only out to a third of its catalogued radius, M51 at
21.9σ. That is why a spiral reads as nothing — its light is spread perfectly evenly. None of them
has arms, a dust lane or a knot, and **no published relation puts them back**: those are properties
of the individual galaxy, not of its Hubble type.

So the structure comes from a photograph of that galaxy:

```
cd tools
python3 -m venv galaxy-images/env
./galaxy-images/env/bin/pip install numpy scipy astropy requests
./galaxy-images/env/bin/python pack_galaxy_images.py \
    --catalog "<KSP>/GameData/ExoInstruments/PluginData/GalaxyCatalog.galcat" \
    --bmax 11 --out GalaxyImages.galimg
cp GalaxyImages.galimg "<KSP>/GameData/ExoInstruments/PluginData/"
```

Sources are the **DESI Legacy Imaging Surveys DR10** (Dey et al. 2019) and **Pan-STARRS1 DR1**
(Chambers et al. 2016), in two bands so the shape can be interpolated to the filter actually fitted:
the arms come out bluer than the bulge because they were measured that way.

**Only the shape is taken.** Every map is normalised to unit total flux, so brightness still comes
from HyperLEDA's B_T through the same photometric chain a mapless galaxy uses. A survey's zero point
never enters the render, and installing maps cannot make a galaxy brighter than the catalogue says.

Three things the packer does that are worth knowing about:

* **It measures each service's linearity instead of trusting it.** The Pan-STARRS *r* and *i* HiPS
  turn out to be asinh-scaled, with nothing in the header saying so; packed as shape maps they would
  have flattened every nucleus and still looked like galaxies. `galaxy-images/check_transfer.py`
  measures the transfer curve against the survey's own stack, and those two bands are not used.
* **Foreground stars are removed by Gaia DR3 astrometry**, not by how they look, because Gaia also
  detects a nearby galaxy's own clusters and H II regions — deleting those would delete the
  structure the whole layer exists for. Only sources with parallax or proper motion significant at
  3σ are cut, and the holes are filled from their surroundings.
* **A close companion is kept, not masked.** M51 and NGC 5195 arrive as the survey saw them, bridge
  included, normalised to the sum of their catalogued fluxes, and the companion is not drawn twice.

At B ≤ 11 that is 146 galaxies of the 156 on the chart, in 273 MB, loaded lazily: only the maps a
frame actually contains are read from disk. The three with nothing at all (the SMC, NGC 4945,
NGC 2997) are far south of Pan-STARRS' limit and outside the Legacy footprint. The giants are the ones the cap bites: M31's 4.7° box is stored at
16.7″ per map pixel by default, and `--giant-pixels 4096` puts it at 4.2″ for 32 MB more.

The sky chart says on hover whether a galaxy has real imagery and at what sampling, and the capture
readout says which of the two each galaxy in the frame came from, so the picture never claims more
resolution than the data behind it. `tools/galaxy-image-tests` checks the whole chain against an independent astropy
reprojection: flux conservation lands at 100.02 %, geometry at 1.6 × 10⁻⁶ map pixels.

## Colour, dispersion, and the sky's own lines

Three deeper layers of realism, each validated against an independent professional reference
(see `tools/colour-tests`, `tools/refraction-tests`, `tools/airglow-tests`):

**Colour is colorimetry, not channel assignment.** A red filter is not the display's red primary, so
composing colour by feeding band counts into R, G and B makes the colours depend on the filter set
rather than on the sky. The mod now carries the full CIE 1931 chain: the standard observer table
generated from `colour-science`, the exact IEC sRGB transform, gamut mapping that desaturates instead
of clipping so hue survives, and fits each instrument's own 3x3 colour matrix from its real filter
curves, the same construction as a raw converter's, with the residual measured and reported (typical
star: 0.017 in CIE xy for the ZWO top-hats, 0.009 for FORS2's measured curves). Emission lines are an
order of magnitude worse for any broadband set, which is *why* narrowband imaging uses stated
palettes: HOO and SHO are labelled conventions here and skip the colorimetry. The composite stretches
**luminance only**, carrying chromaticity through untouched, so a nebula's core keeps its measured
colour instead of washing to white.

**The atmosphere is a prism.** Air's refractive index (Filippenko 1982, checked against three
published formulations to the literature's own spread of 6e-5) depends on wavelength, so a star at
z = 45 deg is smeared over 1.35" between 400 and 700 nm, twenty RC20 pixels, three hundred ZIMPOL
pixels. The PSF is now built across the passband: each sub-band's kernel at its own wavelength
(Airy scale, seeing's lambda^(-1/5), dispersion offset), summed with photon weights, exactly a
chromatic PSF, since convolution is linear. SPHERE carries its real dispersion corrector (Beuzit et
al. 2019) at a stated 5% residual.

**The night sky is mostly lines.** ESO's measured sky model (Noll et al. 2012, on the Hanuschik 2003
Paranal spectra) replaces the flat 21.7 mag/arcsec^2: 11148 R of [O I], Na and OH-forest lines
against 5290 R of continuum, scaled by the van Rhijn shell geometry (with the [O I] red doublet on
its own 250 km layer). An [O I] 6300 filter now sees **11x** the sky an [S II] filter does (the
real reason ground-based [O I] imaging is hopeless), and pushed through the Bessell V band and the
mod's own zero point, the dark sky comes out **V = 21.78** against Patat's measured 21.7 +/- 0.2, a
number that never entered the model.

## Future Roadmap

Not yet implemented in the current build:

- **Autoguiding as a paid career upgrade** for the RC20, rather than a free toggle.
- **Further real astrograph features** surveyed but not built: plate-solving, flat-frame calibration, meridian flip, dithering.
- **A faint population without the download.** Building a Gaia catalogue solves depth for anyone willing to spend the disk and the RAM, but a player who installs nothing still gets an empty sky. Generating a statistical faint population from a Galactic star-count model (Bahcall & Soneira; Besançon; TRILEGAL) would give a plausible field at zero download — the same approach UFig uses, and the natural step before **observing galaxies**, which the same sum-of-sources architecture supports by adding Sérsic profiles as another source type.
- **Naming rights & a discovery archive.** Player-named planets on confirmation, plus an auto-generated logbook entry (light curve, date, instrument) per detection.
- **Weather in the generic instrument forecast.** EVE cloud cover is already hooked into the RC20's solar-system forecast; extending it to the exoplanet-instrument heatmap (SPECULOOS, ELT, and the other ground-based facilities) is still open.
- **Two additional KSC observatory buildings**, each a different real telescope type, planned as further additions alongside the current one.
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

