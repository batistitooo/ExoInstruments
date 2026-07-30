# ExoInstruments — Technical Reference

Exhaustive technical record of every mechanic, formula, data source, and known simplification in the mod, kept as source material for the scientific paper. Kept inside the repo (`TECHNICAL_REFERENCE.md`) so `git pull` carries it between machines — no separate file to lose track of.

**How to keep this current:** when a mechanic changes, update the matching section here in the same commit. The README is player-facing marketing copy and drifts out of sync on purpose (readability over precision); this document is the opposite — precision over readability, so treat any conflict between the two as this document being right.

---

## 1. Architecture overview

- **KSP1 mod**, C#/.NET Framework 4.72, Unity-hosted, `AddonScenario`/`ScenarioModule` for persistence.
- **Layering**: `Core/` = pure C#, no Unity dependency, all the actual physics/math/data-model code, unit-testable in principle. `Visualization/` = Unity-dependent texture rendering (reads Core outputs, produces `Color[]`/`Texture2D`). `Session/` = per-campaign game-loop objects (tick forward in UT, accumulate samples). `ExoInstrumentsGUI.cs` = the single large IMGUI window (~4150 lines) gluing everything together. Root-level files (`BetterTimeWarpIntegration.cs`, `ObservatoryBuilding.cs`, `ExoInstrumentsScenario.cs`) are cross-cutting integrations.
- **Three independent detection pipelines** (`DetectionMethod` enum): `Transit`, `RadialVelocity`, `DirectImaging`, plus a fourth non-exoplanet mode `SolarSystemPhotography` (RedCat 51/RC20/CDK1000/VLT FORS2/VLT SPHERE) that reuses the instrument-economy scaffolding but none of the detection-science fields.
- **Telescope catalog** (`Core/VisualTelescopeCatalog.cs`): every optics/sensor constant the `SolarSystemPhotography` rendering pipeline uses (aperture, focal length, native resolution, pixel pitch, QE, full well, read/dark noise, exposure/gain range, per-filter bandwidth and central wavelength, astigmatism amplitude, adaptive-optics FWHM/Strehl/halo) lives in a `VisualTelescopeSpec`, not hardcoded in `SolarSystemCameraTexture.cs`. `InstrumentSpec.VisualTelescope` (`Core/Observatories.cs`) links a career-economy row to its spec; picking that row in the Observatory dropdown calls `SolarSystemCameraTexture.SetActiveTelescope`, which re-derives every downstream quantity from the new spec.
- **No real KSP star system used for astrophysics**: the star catalog (real RA/Dec, real exoplanet.eu/BSC data) is projected onto the home world's sky using an *arbitrary* zero-point convention (`SkyCoordinates.cs`) — on stock, Kerbin's rotation sweeps the meridian around the real sky, four times faster than Earth's, with no physical relationship between the two. This is a deliberate, foundational simplification everything else builds on. On a pack that models the real solar system the *rate* becomes real for free (see §1.1).
- **Home-world agnostic** (§1.1): nothing in the observing model assumes stock Kerbin. Observer position, body spin, and orbital geometry are all read from the running game.
- **Deterministic-by-hash design**: many "random" per-star properties (stellar activity level, rotation period, spot phase, RM spin-orbit angle, direct-imaging pointing offset) are not stored — they're derived from an FNV-1a (or similar) hash of the star's identity string, so the same star always gets the same synthetic properties across sessions without needing save-file bloat.

---

## 1.1 Home world and observatory site (`ObservatorySite.cs`)

The mod does not assume stock Kerbin anywhere in its observing model, and does not depend on any planet pack being installed. Everything that could be home-world-specific is read from the running game:

| Quantity | Source |
|---|---|
| Observer latitude / longitude | `SpaceCenter.Instance.Latitude/.Longitude` |
| Home body spin (period, initial rotation) | `FlightGlobals.GetHomeBody()` |
| Home body orbit (places the Sun) | `home.orbit` (LAN + argPe, period, epoch, mean anomaly) |
| Moonlight reference flux | brightest moon of `home.orbitingBodies`, at its own semi-major axis |

**Observer position.** Previously hardcoded to stock KSC's latitude −0.0972°. This is the single input that most changes the sky: at the equator the entire celestial sphere is reachable and everything rises perpendicular to the horizon; at Cape Canaveral's 28.6° N — where Real Solar System puts the space centre — the south celestial pole is permanently below the horizon, northern targets become circumpolar, and airmass at a given hour angle differs for every declination. Hardcoding one latitude silently produced the wrong sky for anyone not on stock.

`SpaceCenter.Instance` is KSP's own space-centre object; verified by decompiling `Assembly-CSharp` that `SpaceCenter.Start()` sets its latitude/longitude from `cb.GetLatitudeAndLongitude(transform.position)` — i.e. from the actual space-centre transform on the actual host body. A pack that relocates or replaces the space centre is therefore picked up automatically, with no per-pack special casing. Resolution is cached once obtained (the object only populates its coordinates in scenes where it exists, and the space centre does not move during a game); the stock coordinates remain as a fallback if it is never available. The resolved site is shown in the target-selection panel.

Harness-verified against textbook astronomy at both latitudes: maximum altitude reproduces `90 − |lat − dec|` exactly (Vega 79.82° vs 79.82° predicted at 28.6° N); Polaris is circumpolar at Cape Canaveral (minimum altitude 27.9°) and not at stock KSC (−0.8°); the Small Magellanic Cloud region never rises at Cape Canaveral (−11.4°) while reaching 17.3° from stock KSC.

**Moonlight reference.** `MoonSkyExcess = 1` means "this system's full moon overhead", and the reference flux `albedo·(radius/distance)²` is now derived at runtime from the home body's brightest moon rather than hardcoded to stock's Mün. The same constant means very different things on different home worlds: Mün is a 200 km body only 12,000 km away, the real Moon is 1737 km at 384,400 km, and the two fluxes differ by a factor of ~13.6 — a constant calibrated on one leaves moonlight about an order of magnitude wrong on the other. Lunar phase is the single biggest driver of usable dark time at any real site, so this is worth getting right. A home world with no moons yields zero lunar pollution rather than a division by zero.

**Sidereal meaning.** On stock, the RA zero point is arbitrary by construction (§1). On a pack modelling the real solar system the same arithmetic acquires real meaning for free: the home body's rotation period becomes a real sidereal day, and because such packs define their inertial frame to be the real one — that is how they place bodies on real orbital elements — the meridian angle tracks genuine local sidereal time. Whether it also agrees with a particular skybox replacement's own orientation is that skybox's business and is not claimed here.

**Body colours.** `BodyMarkerColor` covers the real solar system's body names alongside the stock ones, matched by name rather than gated on which pack is installed — a name that isn't present simply never matches, and anything in neither list falls back to neutral grey.

**What does not need changing.** The scale-driven parts adapt on their own because they were already derived from real physics rather than tuned constants: apparent magnitudes come from real albedo/radius/distance geometry (§7.0), so the extreme overbrightness of stock's compressed system (Mün at magnitude −22.5, §7.07) simply does not arise on real distances and the ND filters become optional rather than mandatory; plate scale, field of view and the PSF (§7.11) are properties of the instrument, not the sky; and the seeing/airmass model keys off the target's computed altitude.

---

## 2. Detection physics — Transit photometry

### 2.1 Photon-noise precision scaling (`InstrumentSpec.EstimatePrecision`)

```
precision(mag) = ReferencePrecision * 10^(PrecisionExponent * (mag - ReferenceMagnitude))
```

One relation for every instrument regardless of method (ppm for Transit, m/s for RadialVelocity, contrast for DirectImaging) — aperture/optics/detector differences live in `ReferencePrecision`/`ReferenceMagnitude`, not the exponent. `PrecisionExponent = 0.2` for every instrument in the current roster (a uniform simplification — see §9).

### 2.2 Light curve synthesis (`LightCurveSimulator.cs`)

Flux at time t: `baseline(spot modulation) - transit_dip(t) + noise`.

**Transit dip** — Mandel & Agol (2002) small-planet approximation with quadratic limb darkening:
- `p = sqrt(TransitDepthPpm / 1e6)` (Rp/R*)
- Phase: `phase = (ut/periodSeconds + PlanetPhaseOffset01) mod 1`, centered to `(-0.5, 0.5]`
- Chord position: `x = (a/R*) * sin(2π·phaseCentered)`, `z = sqrt(x² + b²)` (b = impact parameter)
- **Full occultation** (`z ≤ 1-p`): `dip = p² · I(μ)/⟨I⟩`, `μ = sqrt(1-z²)`
- **Ingress/egress** (`1-p < z < 1+p`): `dip = (overlap area / πp²) · p² · I(μ_edge)/⟨I⟩`, circle-overlap area via the standard two-circle intersection formula
- Limb intensity: `I(μ) = 1 - u1(1-μ) - u2(1-μ)²`; `⟨I⟩ = 1 - u1/3 - u2/6`
- **Limb-darkening coefficients** `u1,u2`: linearly interpolated from a 6-point Teff grid `{3500,4500,5000,5800,6500,7500} K` with matching `u1={0.56,0.57,0.51,0.41,0.31,0.22}`, `u2={0.19,0.16,0.20,0.26,0.30,0.32}` — a coarse digitization of **Claret & Bloemen (2011)** tables, solar values as fallback for unknown Teff.
- Fallback when `a/R*` or `b` is missing: box-model duration cutoff (`|phaseCentered| ≤ halfDurationPhase → depth, else 0`), using `EstimatedTransitDurationHours` (§2.5).
- Multi-planet systems superpose every transiting member's dip on one shared stellar baseline (`GenerateSystemFluxAtTime`), each independently phase-shifted by its own TTV term (§2.6) if present.

**Noise** (`TotalNoiseSigma`, added in quadrature):
```
σ_total = sqrt(σ_instrument² + σ_scintillation² + σ_moonlight²)
```
- `σ_instrument = EstimatePrecision(mag) / 1e6`
- `σ_scintillation`: Young (1967) — see §5.2
- `σ_moonlight`: Krisciunas & Schaefer (1991) scattering — see §5.3

### 2.3 Stellar activity as noise floor (`StellarActivity.cs`)

Every star gets a persistent `ActivityFactor` = log-uniform draw in `[0.5, 2.5]` from `Hash01(star, "activity")`, applied to both RV jitter and spot amplitude (an active star is loud in both).

- **RV jitter** (m/s, Teff-banded baseline × ActivityFactor): F(≥6250K)=3.5, G(≥5250K)=1.8, K(≥4000K)=1.2, M(<4000K)=2.2.
- **Rotation period** (days, Teff-banded uniform range × Kepler rotation catalog shape): F 4–18d, G 12–35d (Sun ≈25d), K 20–50d, M 25–90d.
- **Spot amplitude** (ppm): log-uniform in `[120, 1200]` × ActivityFactor — "the range Kepler observed."
- **Spot flux modulation**: fundamental + half-amplitude first harmonic (two-spot-group shape): `amplitude·(sin(ω t+φ1) + 0.5·sin(2ω t+φ2))`.

No real spectroscopic/photometric activity indicator is used anywhere — the catalog carries none, so this is 100% synthetic per-star noise, deterministic but not measured.

### 2.4 Transit detection (`TransitDetector.cs`) — simplified BLS

Box-Least-Squares (Kovacs et al. 2002 style). No false-alarm calibration, no ingress/egress shape modeling — SNR is relative confidence only.

- **Period grid**: uniform in *frequency* (not period) — `df = TypicalTransitDuty(0.03) / (FrequencyOverSampling(2.0) × baseline)`, capped to `[200, 3000]` steps. This is the standard BLS/astropy convention: a transit's phase drift across the baseline must stay under a fraction of its own duration.
- **Phase bins**: adaptive, `bins = periodSeconds / (medianCadence/4)`, clamped `[50, 320]`.
- **Duty envelope**: `duty_max = min(0.15, 0.23 · P_days^(-2/3))` — Kepler's third law argument (T14 ~ P^(1/3), period ~ P, so duty shrinks as P^(-2/3)); rejects e.g. a 54-hour "transit" on a 15-day period (the classic starspot-rotation false positive).
- **Box slide**: for each trial (period, width), slide the box across all phase-bin starts, compute `SNR = (meanOut - meanIn) / (σ/sqrt(nIn))`; requires `nIn, nOut ≥ 12` (`MinInTransitPoints`).
- **Fine refinement**: 200-step local re-search around the coarse-grid best period (coarse-grid fractional error otherwise creates coherent residuals that fool multi-planet prewhitening).
- **Multi-planet search** (`DetectMultiple`): iterative — detrend once (moving-median, 12h bins, tracks starspot rotation but not few-hour transits), then repeatedly detect strongest box → mask its in-transit points (± 0.6 box-durations margin, phase-wrapped) → re-search residual, up to 4 planets or until nothing clears `SnrThreshold=8`.
- **Detection threshold**: `Snr ≥ 8.0` default; requires ≥20 samples.

### 2.5 Transit geometry (`StarTarget.cs`)

- `TransitDepthPpm = (Rp·R⊕/km / (R*·R☉/km))² × 1e6`
- `EstimatedSemiMajorAxisAU`: measured if present, else Kepler's 3rd law `a = (M*[M☉]·P[yr]²)^(1/3)` (planet mass neglected in total mass).
- `ScaledSemiMajorAxis (a/R*) = a[AU]·215.032 / R*[R☉]`
- `ImpactParameter`: measured if present, else `b = (a/R*)·cos(i)`.
- `EstimatedTransitDurationHours (T14)`: `duration_days = (P/π)·asin(sqrt(1-b²) / (a/R*))` — textbook circular-orbit approximation, used because exoplanet.eu exports no duration column directly.
- `IsTransiting`: requires `|b| < 1 + Rp/R*`.
- `TransitProbability = R*/a = 1/(a/R*)`.

### 2.6 Transit Timing Variations (`TransitTimingVariations.cs`)

Sinusoidal TTV near a first-order j:(j-1) mean-motion resonance.

- Checks `j = 2..5` (2:1 through 5:4); nearest resonance found by minimizing `Δ = (P_out/P_in)·(j-1)/j - 1`.
- `|Δ|` floored at `0.01` (exact resonance would diverge; real dynamics saturate into libration there).
- **Amplitude**: `A_sec = P_transiter_sec · (m_perturber/M*) / (π · j^(2/3) · |Δ|)`, capped at `2%` of the transiter's own period and at `0.75×` its transit duration (beyond that the sinusoidal approximation and the linear-ephemeris fold both break down).
- **Super-period**: `P_ttv = 1 / |j/P_out - (j-1)/P_in|` (days).
- Only the single strongest perturber pair is modeled per transiting planet (one dominant near-resonant pair is the typical observed regime); non-transiting/unknown-mass perturbers contribute nothing.
- **Measurement side** (`Analyze`): per-epoch mid-transit time via chi² template scan over a shift grid (60 steps), 1σ from the Δχ²=1 rise; requires ≥5 in-transit points/epoch and ≥6 measured epochs. Linear ephemeris re-fit and subtracted (O–C always means "observed minus a *fresh* linear fit," not the coarse BLS period) before a 400-step sinusoid search over super-periods `[4×P_transit, O-C baseline]`. Detection threshold SNR ≥ 5.

### 2.7 Rossiter–McLaughlin effect (`RossiterMcLaughlin.cs`)

- `v·sin(i*) = 2π·R*[m] / RotationPeriod[s]` (sin i* = 1 assumed — unknowable, standard default).
- Spin-orbit angle λ: persistent per-planet hash draw — 70% near-aligned (±20° uniform), 30% uniformly misaligned in (-180°,180°] — matching the observed hot-Jupiter obliquity distribution shape.
- **Instantaneous anomaly**: `dV = -dip · v·sini · (x·cosλ + y·sinλ)` where x is the in-transit chord position and y = impact parameter. Always present in the physics during any transit of any planet, whether the observer scheduled for it or not — folded automatically into `RvSimulator.GenerateSystemVelocityAtTime`.
- **Measurement** (`Fit`): linear regression of prewhitened RV residuals against two regressors `g1=-dip·x`, `g2=-dip·y` (2×2 normal equations); `vsini=sqrt(c1²+c2²)`, `λ=atan2(c2,c1)`. Requires the photometric ephemeris (regressors are pure transit geometry) and ≥8 in-transit epochs. Detection threshold SNR ≥ 5.

**Citation**: Ohta, Taruya & Suto (2005) for the effect; the fit approach is the mod's own linear-regression simplification of the real spectral-line-distortion measurement.

---

## 3. Detection physics — Radial velocity

### 3.1 Signal (`RvSimulator.cs`)

Full Keplerian reflex velocity:
```
v(t) = K · (cos(ν+ω) + e·cos(ω))
```
- `K` = `StarTarget.EstimatedRvSemiAmplitudeMps`:
  ```
  K = 28.4329 · (Mp·sini/Mjup) · ((M*+Mp)/M☉)^(-2/3) · (P/yr)^(-1/3) · (1-e²)^(-1/2)
  ```
  Constant `28.4329 m/s` cited to **Lovis & Fischer (2010)**, eq. 2 (also Cumming et al. 1999) — the K for `Mp·sini=1 Mjup, Mtotal=1 M☉, P=1yr, e=0`. Depends only on `Mp·sini`, so unlike the transit path, no inclination measurement is needed.
- Mean anomaly: `M = 2π·((ut/P + PlanetPhaseOffset01) mod 1)`.
- Eccentric anomaly `E`: Newton-Raphson solve of Kepler's equation `M = E - e·sinE`, ≤50 iterations, tolerance 1e-10, starting guess `E₀=M` (good for e<~0.8).
- True anomaly: `ν = 2·atan(sqrt((1+e)/(1-e))·tan(E/2))`.
- Multi-planet systems superpose linearly, plus the RM anomaly of any transiting member currently in transit (`RossiterMcLaughlin.SystemAnomalyMps`).

**Noise**: `σ = sqrt(σ_instrument² + σ_jitter²)` (§2.3 for jitter). No scintillation term (a spectrograph measures line positions, not flux). Per-epoch reported uncertainty is instrument-only; jitter shows up only as excess residual scatter, matching real survey practice.

### 3.2 RV detection (`RvDetector.cs`) — simplified Lomb-Scargle

Fits `v(t) = A·cos(ωt) + B·sin(ωt) + C` per trial period via 3×3 normal equations (Cramer's rule).

- **Known bias** (explicitly documented): single-harmonic fit underestimates K on eccentric orbits (real power leaks into 2nd/3rd harmonics). Period recovery stays accurate; amplitude runs low with eccentricity.
- **Guards against phantom detections**:
  - Determinant near-singular check, scaled to `n³/4` (an absolute floor misses ill-conditioned fits at large n).
  - `MaxPlausibleAmplitudeFactor = 8×` the data's own σ — rejects amplitudes no real signal could produce.
  - Cadence-alias skip: periods within 3% of any integer multiple (up to 6×) of the median sampling cadence are skipped — `sin(ωt_i)` vanishes at every sample there, producing phantom high-SNR fits (verified case: 51 Peg b sim, real SNR~110 vs phantom-at-2×-cadence SNR~33).
- **Search**: `effectiveMaxPeriodDays = min(maxPeriodDays, baseline/2)` (longer periods aren't constrained); 2000-step coarse grid + local fine refinement (200 steps) around the best coarse period.
- **Multi-planet** (`DetectMultiple`): iterative prewhitening — detect strongest, subtract the fitted sinusoid, repeat on residuals up to 4 planets. `LikelyHarmonicOfPeriodDays` flags (not proves) a candidate within 5% of a 1:1–3:1 integer ratio of a prior detection's period (explicitly: "a flag for the report, not proof either way — real RV surveys face the same call").
- **Detection threshold**: SNR ≥ 8.0 default; requires ≥10 samples.

---

## 4. Detection physics — Direct imaging

### 4.1 Feasibility & contrast (`DirectImagingSimulator.cs`)

Modeled at H-band (1.6 μm) on a 39.3m aperture (ELT-class).

- **Diffraction limit**: `θ = 1.22·λ/D` (Rayleigh criterion) → `DiffractionLimitArcsec`.
- **Separation**: `a[AU]/distance[pc]` (small-angle approx); `Resolvable = separation > θ_diff`.
- **Planet temperature**: catalog `PlanetTempK` if measured, else zero-redistribution equilibrium estimate:
  ```
  Teq = Teff · sqrt(R*[AU] / 2a) · (1-A)^0.25,  A(Bond albedo) = 0.3 assumed
  ```
- **Contrast ratio**: Planck-function ratio at 1.6μm × radius-ratio²:
  ```
  ContrastRatio = [B(Teq)/B(Teff)] · (Rp[R⊕]/(R*[R☉]·109.2))²
  ```
  `B_ratio = (exp(x*)-1)/(exp(xp)-1)`, `x = hc/(λ·k·T)` → `8995.9/T` at 1.6μm; overflow-guarded (`xPlanet>700 → 0`).
- **Speckle floor** (5σ, 1hr): base floor at 1λ/D from `instrument.EstimatePrecision(mag)`; beyond the diffraction limit it improves quadratically with separation: `floor(θ) = max(1e-8, base·(θ_diff/θ)²)`.
- **SNR**: `5 · (Contrast/SpeckleFloor) · sqrt(hours)` — improves as √time, standard photon-noise-limited scaling.
- **Detection threshold**: SNR ≥ 5.0 (`DetectionSnrThreshold`).

**Citation**: order-of-magnitude figures per **Kasper et al. 2021 (PCS/ELT)** — raw ~1e-4 at small separations, deep post-processed limits approaching 1e-8; the game uses the single representative `1e-4` as the modeled floor, not the deeper post-processed number. Telescope facts: Gilmozzi & Spyromilio (2007).

### 4.2 Simulated detector frame (`Visualization/DirectImagingTexture.cs`)

The mod's most expensive per-pixel render. Builds a full synthetic AO-imaging frame:

- **PSF**: the **diffraction pattern of the real ELT pupil**, annular aperture and six support vanes together, computed by `Core/PupilDiffraction` (§7.112). Core radius, ring radii, ring amplitudes, spike direction, spike brightness and spike falloff are all consequences of D, the obstruction, the vane geometry and λ. There is no free parameter left. §7.111 covers the pixel-averaging that makes the pattern samplable on a raster whose plate scale varies by two orders of magnitude between targets.
- **Spider spikes**: no longer drawn. They fall out of the same pupil transform as the rings (§7.112), from the ELT's real six 50 cm vanes. The three constants that used to produce them (`4e-4` amplitude at 1λ/D, azimuthal Gaussian σ=1.3°, 1/r² falloff) are gone, and with them the last free parameter in the frame's optics.
- **Speckle halo**: noise floor from `DirectImagingSimulator.SpeckleFloorAtSeparation`, improving as `1/(5·sqrt(hours))`, modulated by a `cos²` "wind-butterfly" asymmetry (a real documented AO-residual phenomenon) between `0.55×` and `1.45×`.
- **Background**: fixed `3e-9` at 1hr (√t-improving), explicitly independent of target brightness.
- **Planet PSF**: the same exact profile, scaled by `ContrastRatio·peakScale`, added additively as on a real detector. The companion now carries its own diffraction rings, which it did not under the Gaussian; a marginally resolved companion used to read as a featureless blob.
- **Deterministic per-target randomness**: pointing offset (≤12% FOV/axis) and position angle from name-hashes — same frame layout every refresh for a given target, not re-rolled.
- **Color mapping**: log-stretch over 9 decades (1e-9..1), tinted by the star's blackbody color when Teff is known, else a fixed black→orange→white "near-IR convention" ramp.
- 14 fixed hot pixels (target-hash-seeded — "the same physical detector would put them at fixed positions per instrument, but per-target keeps each frame visually distinct," an explicit, deliberate departure from physical fidelity for visual variety).

---

## 5. Atmosphere, sky, and ground-based observing conditions

### 5.1 Airmass & twilight (`ImagingObservingConditions.cs`)

- **Airmass**: plane-parallel `1/sin(altitude)` (secant of zenith angle). "Accurate to <1% above 20°, which is our telescope floor" — no refraction/curved-atmosphere correction.
- **Twilight cutoff**: `IsNight` when Sun altitude `< -12°` (nautical twilight).
- **Telescope altitude floor**: `20°` (`MinTelescopeAltitudeDeg`); the solar-system-photography capture gate (any of the four instruments) uses a separate, lower `0°` geometric-horizon threshold (see §7).
- **Efficiency**: `1/airmass²` when observable, else 0 — "airmass weighting: SNR² accumulates at 1/X², so one hour at X=2 ≈ 15 min at zenith."
- **Sun's declination fixed at 0°** — stock KSP bodies have no axial tilt, so no seasons; no orbit on record defaults to permanent night (degenerate-save fallback).
- Space-based instruments bypass all of this: synthetic always-observable snapshot (`SunAlt=-90, TargetAlt=90, Airmass=1, Efficiency=1`).

### 5.2 Atmospheric scintillation (`AtmosphericNoise.cs`, `AtmosphericImagingNoise.cs`)

**Young (1967)** formula, reused identically by the transit photometers and the solar-system camera:
```
σ_scint = 0.09 · D[cm]^(-2/3) · X^(7/4) · exp(-h[m]/8000) · (2t[s])^(-1/2)
```
D=aperture, X=airmass, h=site altitude, t=exposure. For the science instruments only the *excess* above the zenith value is added in quadrature (`ReferencePrecision` already bakes in typical-conditions/zenith scintillation): `σ_excess = sqrt(σ(X)² - σ(1)²)`. Zero for space-based or non-transit instruments. Extinction (Bouguer's law): `transmission = 10^(-0.4·k·(X-1))`, `k=0.20 mag/airmass`.

### 5.3 Lunar pollution (`MoonlightPollution.cs`)

**Krisciunas & Schaefer (1991)** forward-scattering kernel:
```
Kernel(ρ) = 10^5.36 · (1.06 + cos²ρ) + 10^(6.15 - ρ/40)
```
ρ = angular separation (deg) from the moon, floored at 0.5° (kernel diverges into the moon's own disk; occultation handles that region separately). Per-moon contribution:
```
excess = (moonFlux/refMoonFlux) · (Kernel(ρ)/Kernel(30°)) · altitudeRamp
moonFlux = albedo · illuminated_fraction · (R_moon/distance)²
```
Illuminated fraction from solar elongation: `(1-cos(elongation))/2`. Altitude ramp: fully counted above 10°, linear below. Aggregate: `moonSkyFactor = sqrt(Σ excess)` (added in quadrature as a noise term, not linearly). Occultation (moon disk fully blocking the target) is separate geometry — checked even for a new moon, since a dark disk still blocks light. Only affects transit photometry; RV and imaging are immune (§2.2, §3.1).

### 5.4 Observing forecast heatmap (`ObservingForecast.cs`, `Visualization/ForecastTexture.cs`)

Grid: rows = nights (one body rotation each), columns = time-of-night slots. Per cell quality:
- Not observable → 0
- Transit: `(idealSigma/actualSigma)²` (idealSigma = zenith-moonless noise floor)
- DirectImaging: raw `1/airmass²` efficiency
- RadialVelocity: flat `1.0` when observable (no RV-specific quality model — sky conditions are irrelevant to spectroscopy in this mod)

Normalized so the single best upcoming cell = 1.0 (deliberately relative — an absolute scale would render a scintillation-dominated pairing as a flat, useless map). **No weather** in this generic forecast (only the RC20's own solar-system-body forecast factors in real EVE cloud cover — see §7.4). Rendered as a porkchop-plot gradient (dark red→orange→yellow→green→deep blue, worst→best), with `sqrt(q)` applied before color-mapping to keep a barely-open window visually distinct from the closed floor.

---

## 6. Star catalog pipeline

### 6.1 Sources

- **exoplanet.eu** CSV/TSV export → `ExoplanetCSVLoader.cs`. Columns consumed: `star_mass, star_radius, star_distance, mag_v/i/j/h/k (fallback chain), orbital_period, radius, ra, dec, star_name/alternate names, planet_status, mass/mass_sini, semi_major_axis, eccentricity, inclination, impact_parameter, omega, star_teff, temp_measured (preferred) / temp_calculated`. Rows missing star mass/radius/distance, or orbital period ≤0, are skipped (counted). Deterministic transit phase offset from a Java-style string hash of the planet name (no real epoch exists in the export).
- **Yale Bright Star Catalogue** (BSC5, V/50, Hoffleit & Warren 1991, ~9110 stars complete to V~6.5) via a VizieR TSV query → `BackgroundStarCatalogLoader.cs`. Fixed-width name-field parsing (Flamsteed/Bayer/constellation). No mass/radius/distance data at all (all zeroed — treated as "unknown" everywhere downstream). Teff derived photometrically from B-V color (§6.4), not spectroscopy.

### 6.2 Merge & deduplication (`StarCatalogMerger.cs`)

Layered matching, strongest evidence first, so no real exoplanet host is duplicated as an anonymous decoy:
1. **HD number** cross-match (extracted via regex from all designation strings).
2. **HR number** cross-match.
3. **Normalized name-key** match (`StarNames.Normalize`) — refused (not guessed) if ambiguous (one key → multiple BSC candidates), logged for manual review.
4. **Positional fallback** (20 arcsec tolerance — "absorbs exoplanet.eu's RA rounding (~13 arcsec drift on 51 Peg) while staying below resolved binary separations, verified against 83 Leo A/B at 27 arcsec"), skipped for non-primary components (names ending "b"/"c"/"d") which must match by their own HD, not the primary's BSC entry.
Bright unmatched hosts (V≤6.7, BSC's nominal completeness+stragglers) are flagged (`UnmatchedBrightHosts`) rather than silently trusted. Teff backfilled from BSC's color-derived value when the exoplanet.eu entry lacks one (documented case: HR 8799).

### 6.3 Density thinning / "fog of war" declutter (`CatalogDensityThinner.cs`)

Purely cosmetic: caps stars per 4°×4° sky cell at 6, so a dense survey field (Kepler, ~115 deg²) doesn't visually clump and give away "something's here" before it's scanned. Selection within an over-full cell is by ascending FNV-1a hash of `CatalogKey` — deterministic, independent of file/load order, so a star's presence never flickers between sessions.

### 6.4 Stellar color / temperature (`StellarColor.cs`)

- **Teff from B-V**: `T = 4600·(1/(0.92·BV+1.7) + 1/(0.92·BV+0.62))` — **Ballesteros (2012)**, calibrated so B-V=0.65 gives the Sun's 5778K. Valid for `-0.5 ≤ BV ≤ 2.5`; "good to a few percent on the main sequence."
- **Blackbody RGB** for display only (a real H-band frame is monochromatic — this encodes the *measured* color temperature for the player): piecewise fit to the Planckian locus (Tanner Helland's algorithm / M. Charity's blackbody data), valid ~1000–40000K.
- **Spectral class** from standard main-sequence Teff thresholds: O≥30000, B 10000–30000, A 7500–10000, F 6000–7500, G 5200–6000, K 3700–5200, M<3700.

### 6.5 Habitable zone (`HabitableZoneCalculator.cs`)

**Kopparapu et al. (2013, ApJ 765,131, erratum-corrected)** + **Kopparapu et al. (2014, ApJL 787,L29)** for the 1 M⊕ runaway-greenhouse coefficients. Only valid for `2600K ≤ Teff ≤ 7200K` (returns null outside, no extrapolation). Four boundaries (Recent Venus / Runaway Greenhouse / Maximum Greenhouse / Early Mars), each a degree-4 polynomial in `T* = Teff-5780`:
```
Seff = Seff_sun + a·T* + b·T*² + c·T*³ + d·T*⁴
d[AU] = sqrt((L/L☉) / Seff),  L/L☉ = (R/R☉)²·(Teff/5778)⁴
```

### 6.6 Star naming (`StarNames.cs`)

Full Greek-letter canonicalization table (all spelling variants → one 3-letter token), two-word and single-word Latin genitive constellation tables, HTML-entity decoding (handles exoplanet.eu's `&ouml;`-style names), HD/HR regex extraction, and an IAU-style truncated (not rounded) coordinate-based provisional designation generator (`J`+HHMM±DDMM) for unidentified sources.

---

## 7. Solar-system astrograph pipeline

Non-exoplanet instrument (`DetectionMethod.SolarSystemPhotography`) — point-and-shoot photography of any Kerbol-system body, clicked directly on the sky chart. `SolarSystemCameraTexture.cs` clones KSP's own galaxy/scaled-space cameras (same technique as Tarsier Space Technology's TSTCameraModule) and runs the frame through a full radiometric pipeline. The pipeline itself is instrument-agnostic: every optics/sensor constant it reads comes from a `VisualTelescopeSpec` (§7.00), not a hardcoded number, so the physics below applies identically regardless of which of the four real instruments is active. Numbers quoted in this section as examples default to the RC20 unless another instrument is named explicitly.

### 7.00 Telescope catalog (`Core/VisualTelescopeCatalog.cs`)

Five fully real, cited instruments, switchable from the Observatory dropdown (`InstrumentSpec.VisualTelescope` links each career-economy row to its spec; picking a row calls `SolarSystemCameraTexture.SetActiveTelescope`):

- **RedCat 51** — William Optics RedCat 51 (51mm aperture, f/4.9, 250mm focal length, Petzval quadruplet FPL-53 objective, flat corrected field over a 45mm image circle — williamoptics.com product page). Same ZWO ASI294MM Pro camera as the RC20 (one camera swapped between tubes, as amateur astrophotography actually works), no Barlow. The catalogue's only **wide-field** entry, and the only one that resolves nothing and covers everything: 4.40°×2.99°, against the RC20's 0.32°×0.22°. Deliberately undersampled at 3.82"/px unbinned versus 2.5" seeing — the defining trade of a wide-field astrograph, not a defect. See §7.12 for why the star field needs it.
- **RC20** — PlaneWave RC20 (f/6.8, 0.51m aperture, 39% linear secondary obstruction — planewave.eu product page) with a **ZWO ASI294MM Pro** camera (4144×2822 native resolution, 4.63μm pixels, 66,000 e⁻ full well, 1.2 e⁻ read noise, 0.0022 e⁻/s/pixel dark current at -20°C, ~90% peak QE — zwoastro.com/product/asi294). 4× Barlow for the tight end of the zoom range. No autoguider by default.
- **CDK1000** — PlaneWave CDK1000 (1.0m aperture, f/6, 6000mm focal length, 47% central obstruction of the primary mirror — planewave.com product page; the same optical tube sold as part of the "PW1000" 1-meter observatory system, a real unit of which was installed at Palomar Observatory in 2024 for MIT's WINTER project). Same ZWO camera and 4× Barlow as the RC20.
- **VLT FORS2** — the real Very Large Telescope, Unit Telescope 1 "Antu", Paranal (8.2m aperture, 2635m altitude, the same site already used for ESPRESSO). Real FORS2 imager: a mosaic of two MIT/Lincoln-Lab CCID20 CCDs (eso.org FORS2 User Manual), 15μm pixels, 0.126"/pixel real intrinsic plate scale (equivalent focal length 24.556m, back-derived from that published scale), 150,000 e⁻ full well (the CCID20 chip's own real spec, Cuillandre et al. 1999 CFHT/ESO CCD-workshop technical note; FORS2's own manual doesn't restate a full-well figure for the shared chip), 0.7 e⁻/ADU real gain and 1.89 e⁻ read noise (FORS2's own "100kHz,2×2,high" readout mode), 0.25s real minimum exposure. M2 secondary mirror 1.116m diameter (eso.org M2 Unit page) gives a 13.6% obstruction fraction. A real 2× High-Resolution collimator (1233mm SR / 616mm HR focal length, ratio 2.001) stands in for the tight end of the zoom range in place of an invented amateur Barlow. Fixed gain (a real research CCD has no ISO-like control). Always autoguided (§7.011). QE 86% peak (600nm; the real published curve is 400nm 58%, 500nm 74%, 600nm 86%, 700nm 83%, 800nm 66%, 900nm 39%). Real filters, from ESO's current FORS Filter Specifications page: b_HIGH+113 (440nm/103.5nm FWHM) as Blue, v_HIGH+114 (557nm/123.5nm FWHM) as Green, R_SPECIAL+76 (655nm/165nm FWHM) as Red, Hα+83 (656.3nm/6.1nm FWHM, peak transmission 0.70 in the SR collimator) — the Blue and Green figures **correct** earlier values in this document (429nm/88nm and 554nm/111nm), which were the standard Bessell B and V figures rather than FORS2's own b_HIGH and v_HIGH: same passband names, different real filters as HAlpha; Luminance uses the CCD's own quoted 330-1100nm sensitivity range as a genuine unfiltered/clear exposure (FORS2 has no dedicated amateur-style L filter). Astigmatism 0px: FORS2/the VLT Cassegrain is real and well-corrected, but no published optical prescription gives a field-dependent astigmatism coefficient to the precision this pipeline's display model would need.
- **VLT SPHERE** — same VLT, Unit Telescope 3 "Melipal", carrying the real SPHERE/ZIMPOL extreme-adaptive-optics imaging polarimeter (Schmid et al. 2018, *A&A* 619, A9, "SPHERE/ZIMPOL high resolution polarimetric imager. I."). Real f/221 system, equivalent focal length 1718.7m (back-derived from ZIMPOL's own published 3.6 mas/pixel plate scale at its standard 2×2-on-chip-binned mode with the real 15μm native pixel; `BinningFactor=1` reproduces ZIMPOL's real unbinned 1.8 mas/pixel mode, `BinningFactor=2` reproduces its real standard 3.6 mas/pixel mode exactly, no separate Barlow exists for this instrument). Cross-check: at native pixel count this gives a computed field of view of ~3.49", matching ZIMPOL's own real published 3.6"×3.6" field within rounding. Real CCD, Table 4 of the cited paper: 640,000 e⁻ full well, 20 e⁻ read noise, 0.2 e⁻/s/pixel dark current, 1.1s minimum integration time, 95% peak QE (600nm; 90% at 700nm, 65% at 800nm). Same shared VLT M2 obstruction fraction as FORS2. §7.011 adaptive optics: real ~25 mas achieved FWHM (Strehl ~40% in I-band, good conditions), independently corroborated by a second source giving 22-28 mas across V/R/I. Real filters: V (554nm/80.6nm FWHM) as Green, N_R (646nm/57nm FWHM) as Red, B_Ha (655.6nm/5.5nm FWHM, the broader of ZIMPOL's two real Hα filters, the narrower N_Ha at 0.97nm being too narrow for a simple single-exposure capture) as HAlpha. ZIMPOL genuinely has no real blue broadband filter (its filter set targets red/near-IR reflected-light and circumstellar-disk science) — Blue is simply absent from this instrument's filter wheel (§7.012) rather than a made-up number standing in for a filter that doesn't exist. Luminance uses ZIMPOL's own quoted 500-900nm working spectral regime. Astigmatism 0px, well-justified by the field size alone: ZIMPOL's real field of view is only 3.6"×3.6", far too narrow for off-axis aberration to grow to any meaningful amplitude.

### 7.001 Optical throughput

How much of the light entering the aperture reaches the detector. Previously absent entirely, which is why every instrument in the roster reached ~1.5 mag deeper than a real one of the same size. Split into separately-published factors rather than one lumped efficiency, so each can be sourced or declared unmodelled on its own; the aperture obstruction is *not* here, since it already lives in `EffectiveApertureAreaM2` where the collecting area belongs.

**Mirror train.** `throughput = r^N`, with `N` the number of reflecting surfaces and `r` the per-surface reflectivity — the same form as **Ma & Cai, "Scientific performance analysis of the SYZ telescope design vs. the RC telescope design"** (MNRAS; arXiv:1708.01257) §4.2 Eq. 3, whose obstruction term `(1 - ε²)` this pipeline already applied. That paper also supplies the value: aluminium is "about 90%" in the 300-1000 nm range when fresh and "will degrade from 90% to about 87% after 1 year and to 84% after two years (**Magrath 1997**)", from which the authors "take the reflectivity of aluminum coating for the full optical wavelength range as **87%** during a 2-year lifetime". 0.87 is used throughout for that reason: an operating figure over a realistic re-coating cycle, not a laboratory best case on the day of coating. Independently consistent with ESO's own measurement of the VLT coating, which **Ettlinger, Giordano & Schneermann (1999, The Messenger 97, 4-8, "Performance of the VLT Mirror Coating Unit")** place between **Bennett et al.'s (1963, JOSA 53, 1089)** fresh and aged evaporated-aluminium samples across 300-2500 nm. Deliberately grey, because the source quotes it as a band average over the full optical range; resolving it in wavelength would mean inventing a curve the citation does not give.

**`N` is a property of where the instrument sits, not of the telescope.** FORS2 is at UT1's **Cassegrain** focus (ESO's own caption for image `eso9857a`: "FORS at VLT UT1 Cassegrain focus"), so its path is M1 → M2 and `N=2`. SPHERE is on UT3's **Nasmyth** platform, picking up the M3 flat as well, so `N=3`. The same 8.2 m telescope therefore delivers measurably different throughput to the two instruments — 0.757 against 0.659 before any instrument optics, which is exactly the "an extra mirror yields 13% extra light loss with Al coating" the same paper states outright.

| Instrument | Mirrors | r^N | Relay | Filter peak | Total optics |
|---|---|---|---|---|---|
| RedCat 51 | 0 (refractor) | 1.0 | 1.0 *(unmodelled)* | 1.0 *(unpublished)* | 1.000 |
| RC20 | 2 | 0.757 | 1.0 *(unmodelled)* | 1.0 *(unpublished)* | 0.757 |
| CDK1000 | 2 | 0.757 | 1.0 *(unmodelled)* | 1.0 *(unpublished)* | 0.757 |
| VLT FORS2 | 2 (Cassegrain) | 0.757 | 1.0 *(unmodelled)* | 1.0 broadband, **0.70 Hα** | 0.757 / 0.530 at Hα |
| VLT SPHERE | 3 (Nasmyth) | 0.659 | **0.79** (zw.BS) | 1.0 *(unpublished)* | 0.520 |

**1.0 means not modelled, not lossless**, and it is the honest value wherever nothing is published. Two entries are real:

- **SPHERE's grey zonal beam splitter** transmits "about 79% of the light to ZIMPOL and 21% to the WFS" (**Schmid et al. 2018** §2) — an extreme-AO system must spend a fifth of its light sensing the wavefront it corrects, which is a real cost of the correction rather than an inefficiency. Polarimetric mode, which the same paper says costs a further factor 0.85, is not simulated; this pipeline images.
- **FORS2's H_Alpha+83 peak transmission, 0.70** in the standard-resolution collimator (0.76 in HR), from ESO's FORS Filter Specifications page. The only published filter peak transmission anywhere in the roster.

**Detector QE curves** (`Core/SpectralCurve.cs`), interpolated linearly between published points and held **flat** outside the measured range rather than extrapolated (a QE curve extrapolated linearly off its red end reaches zero, then negative, at wavelengths the detector is demonstrably still sensitive at):

- **FORS2**: ESO's own published curve for the MIT/LL CCID20 mosaic — 400 nm 58%, 500 nm 74%, 600 nm 86%, 700 nm 83%, 800 nm 66%, 900 nm 39%.
- **SPHERE/ZIMPOL**: Schmid et al. 2018 — "about 0.95, 0.90 and 0.65 at λ = 600 nm, 700 nm and 800 nm respectively." Three points is all the paper gives.
- **ZWO ASI294MM Pro** (RedCat 51 / RC20 / CDK1000): **no curve**. ZWO publishes only a 90% peak, so the peak is used flat across the band, which overstates every filter away from it. Recorded as such rather than papered over with a borrowed curve from a different sensor — a measured curve for a *sibling* Sony back-illuminated CMOS exists (Alarcón et al. 2023, on the IMX455/IMX411) but normalising another chip's shape to this one's peak would be an assumption dressed as a measurement.

### 7.0 Real photon-flux signal model (`Core/PhotonFluxModel.cs`, `Core/SystemBandpass.cs`)

The imaged body's brightness is no longer an invented flat exposure multiplier — it is the body's real apparent magnitude, converted through the active telescope's real optics/sensor chain into real electrons.

**Apparent magnitude** (standard planetary H-G-system flux-ratio formalism):
```
phi(alpha) = [sin(alpha) + (π-alpha)·cos(alpha)] / π        (Lambertian-sphere phase law, Russell 1916)
fluxRatio  = albedo · (R_AU/d_obs_AU)² / d_sun_AU² · phi(alpha)
m_body     = -26.74 - 2.5·log10(fluxRatio)
```
`-26.74` is the Sun's real V-band apparent magnitude at 1 AU. `albedo`/`R` are the live `CelestialBody`'s own real fields; `d_sun`/`d_obs`/`alpha` (phase angle) come from live 3D positions (Sun, body, KSC observer), the same `Vector3d.Angle` pattern `ComputeMoonSkyExcess` already used. This is a genuine improvement over the `(1+cosθ)/2` half-phase approximation used elsewhere (§7.3) — the real phase-integral form, not a cosine stand-in.

**Real electrons collected** (`Core/SystemBandpass.cs`, `Core/SpectralCurve.cs`):
```
N_electrons = 948 photons/cm²/s/Å · 10^(-0.4·m_body) · W · apertureAreaCm² · exposureSeconds
W = ∫ [φ(λ)/φ(λ_V)] · T_filter(λ) · T_optics · QE(λ) · T_atm(λ,X) · T_nd  dλ        [Å]
```
`948 photons/cm²/s/Å` is the real V-band zero-magnitude photon flux density (Vega calibration, standard photometric reference), used as what it is — a *monochromatic* density at 5556 Å, fixing the absolute scale of the source's spectrum at the one wavelength its magnitude is defined at. `apertureAreaCm²` is the active telescope's own real aperture minus its own real secondary obstruction (`EffectiveApertureAreaM2`, shared with §7.011's exposure rescaling).

Everything spectral is carried by **`W`, the effective photometric width**: the system's total response integrated over the passband, which is what synphot, GalSim's `Bandpass` and every real exposure-time calculator compute. This replaced a product of scalars — one rectangular bandwidth, one *peak* QE, one extinction sample at the central wavelength, and no optical throughput at all — that was wrong in three ways, all in the same direction:

- **Peak QE across a whole band.** FORS2's own published curve runs 58% at 400 nm against 86% at its 600 nm peak, so a b_HIGH exposure was credited with **1.33×** the electrons it really collects (harness-measured).
- **Extinction at λ_c alone.** The unfiltered Luminance position is 7700 Å wide on FORS2 and the coefficient varies threefold across it (k_B 0.38 vs k_R 0.13, §7.3), so one sample cannot represent the band: integrating differs from sampling the centre by **5.5%** at airmass 2.
- **No throughput term at all.** Every photon entering the aperture was collected, so the limiting magnitude came out ~1.5 mag deeper than a real instrument of the same aperture reaches.

**Reducible by construction.** For a flat source spectrum, a grey QE and a transparent atmosphere the integral collapses to `W = FWHM · QE · T` and reproduces the previous model *exactly* — asserted in the harness to 1 part in 10¹², against `PhotonFluxModel.CollectedElectronsGreyBand`, which is retained for no other purpose. The new model is therefore a provable generalisation of the old one rather than a different model that resembles it.

**The filter profile is not invented.** Only published numbers are used: a top-hat of the filter's own published FWHM at its own published central wavelength, scaled by its published peak transmission. A top-hat's equivalent width *is* its FWHM, so the normalisation reproduces the published figure by construction. Real interference filters are flat-topped with steep edges, so this is also the right first-order shape; what the integral buys is everything *else* being resolved across the band, not a pretend filter profile.

**Source spectra.** A star: a Planck spectrum at the Teff derived from its catalogue B-V by Ballesteros (2012) — so the **colour term is no longer a separate multiplier** but a consequence of the same integral that gives the electron count, exactly 1 at 5556 Å and only ever interpolating away from a real measurement (harness: agrees with the superseded two-wavelength `ColorTerm` to 0.015% over 2800-20000 K on a narrow band). A star with no catalogue colour is integrated flat rather than at a guessed temperature. A planet or moon: the **Sun's** spectrum, because that is what reflected sunlight is — a Planck spectrum at 5772 K, the nominal solar effective temperature fixed by **IAU 2015 Resolution B3** (Prša et al. 2016, AJ 152, 41). The reflecting surface is treated as grey, since a KSP `CelestialBody` carries one albedo and no wavelength dependence to read.

**Cost.** `W` depends on the source only through its temperature, so it is tabulated on a 160-point log-spaced Teff grid when the response is built (once per capture, well under a millisecond) and interpolated per star — the same tabulate-once trick `OpticalPsf` uses for its radial profiles (§7.11), and for the same reason: the alternative is a fresh 64-node quadrature per star, hundreds of times per wide-field frame. 160 entries rather than 48 is set by accuracy, not taste: at 48 the table reproduced the directly-integrated colour term to only 0.14%.

**Per-filter bandwidths.** The RC20/CDK1000/RedCat 51's amateur LRGB wheel is the one case with no published per-channel bandwidth of its own, so R/G/B there keep an even-third-of-Luminance split (modern "1:1:1 balanced" CMOS LRGB filter design) and HAlpha keeps a real ~7nm narrowband figure; FORS2 and SPHERE use each of their own real named filters' own real bandwidth instead (§7.00).

**Calibration, not replacement, of the rendered image**: Unity's own rendered pixel values keep supplying the real spatial shading (terminator, limb, craters — the game's own 3D lighting), summed once per exposure (`Σ FilterSignal`) and used only to redistribute the physically-derived `N_electrons` across pixels in the same proportions the render already has: `signalFraction(pixel) = renderedValue(pixel) · (N_electrons/BinnedFullWellElectrons) / Σ renderedValue`. Only the *absolute scale* is recalibrated — noise, saturation, and SNR now all follow from real photon statistics rather than an invented gain constant.

### 7.011 Telescope switching, exposure rescaling, and locked autoguiding (`SolarSystemCameraTexture.SetActiveTelescope`)

Switching the active telescope re-derives every downstream optics/sensor quantity from the new spec and rebuilds the render targets/scratch buffers on the next capture (tracked by `builtSpec`, the same mechanism `BinningFactor` changes already used). Three things are actively corrected, not just re-pointed:

- **Exposure rescaling**: the player's current exposure time is multiplied by the ratio of the old and new telescope's real effective collecting area (`EffectiveApertureAreaM2`, aperture squared minus obstruction), the same recalculation a real astronomer does with an exposure-time calculator when changing instruments. Without it, an exposure tuned for the RC20's 0.51m aperture carried straight to the VLT's 8.2m one (~258× the collecting area) blows every pixel far past full well, and the per-column blooming pass (§7.5) turns the saturated body into a tall white bar instead of a photo. The rescaled value is then clamped into the new instrument's real exposure range.
- **Forced autoguiding**: a spec with `AlwaysAutoguided` (both VLT instruments) forces the player's `Autoguiding` property on and locks the GUI toggle. A real 8.2m research telescope, and doubly so one running active adaptive optics, has no real bare/unguided operating mode the way an amateur RC20/CDK1000 genuinely might; leaving the toggle player-controlled would let whatever the player last chose on the RC20 (commonly off) silently carry over and reintroduce diurnal-drift trailing a real VLT never shows.
- **Filter reset**: if the previously selected filter isn't in the new instrument's `AvailableFilters` (§7.012), it resets to Luminance.

### 7.012 Per-instrument filter availability (`VisualTelescopeSpec.AvailableFilters`)

The filter wheel only offers the filters a given instrument really has. RC20/CDK1000/VLT FORS2 carry all five (Luminance/Red/Green/Blue/HAlpha); VLT SPHERE omits Blue, since ZIMPOL genuinely has no real broadband blue filter in its filter set (§7.00). `BlueBandwidthAngstrom` is left at 0 on that entry and is unreachable through the GUI.

### 7.013 Adaptive optics (`VisualTelescopeSpec.AdaptiveOpticsFwhmArcsec`, `AdaptiveOpticsStrehlRatio`, `AdaptiveOpticsHaloSeeingFwhmArcsec`)

Every ground-based instrument before SPHERE is seeing-limited: real atmospheric turbulence blurs the image to Paranal's typical ~0.6-1" regardless of aperture. A real extreme-adaptive-optics system instead actively cancels that turbulence ahead of the wavefront sensor, and the result is **not** a narrower blur of the same shape — it is a genuinely two-component point-spread function:

```
PSF_AO = S · (diffraction-limited core)  +  (1 - S) · (uncorrected seeing halo)
```

- `AdaptiveOpticsStrehlRatio` — SPHERE/SAXO's real published Strehl, **0.40 in I band** (Schmid et al. 2018), the fraction of the light the correction actually concentrates into the core.
- `AdaptiveOpticsHaloSeeingFwhmArcsec` — **0.72"**, Paranal's own published median seeing (the same site FORS2 observes from). The halo is by definition the light SAXO failed to gather, so it is the uncorrected seeing profile. *(Previously 0.65", which cited ESO but was not ESO's published figure; corrected to the 50% percentile ESO actually gives on its Paranal astroclimate page, and now shared with `ZenithSeeingFwhmArcsec` — the halo and the seeing term must be the same sky.)*
- `AdaptiveOpticsFwhmArcsec` — **25 mas**, the real delivered core resolution.

The delivered figure is *not* fed into the atmospheric term directly, because the diffraction pattern is now computed independently from the telescope's own pupil (§7.11) and doing so would double-count it. `OpticalPsf.AtmosphericFwhmForDelivered` instead solves numerically, by bisection on the real kernel, for the atmospheric residual that makes the finished core measure exactly 25 mas. Quadrature subtraction is not sufficient: it is exact only for Gaussians, and neither an Airy pattern nor a Kolmogorov profile is one — both carry much heavier wings, and the naive subtraction leaves a core measuring ~29 mas against the published 25.

Both components are applied as separate convolutions and summed with weights `S` and `1-S`. Convolution is linear, so this is exactly equivalent to convolving once with the combined kernel, but it lets each component be sized to its own scale rather than forcing the compact core to carry the halo's enormous support (0.65" is 361 px across at ZIMPOL's unbinned 1.8 mas plate scale). The halo kernel is capped at a 256 px radius and renormalised; truncating a profile's far wings leaves its FWHM untouched and only redistributes the faint outermost flux, which at those radii is already a flat pedestal across the whole field.

Why the two-component form matters, rather than a single profile of the correct total width: collapsing them gets the FWHM right but puts far too much light at *intermediate* scales, which is exactly where a resolved planetary disk's surface detail lives. Measured on a sine-pattern transfer test, the single-profile and two-component forms retain the same *ratio* of fine-to-coarse contrast — the correction is not a sharpness gain — but the two-component form distributes the light physically correctly: a sharp core on a diffuse background, which is what a real AO frame looks like, rather than uniformly smeared structure.

### 7.05 Real sensor resolution and binning

`TextureWidth`/`TextureHeight` are the active telescope's own real native sensor resolution (§7.00) divided by a selectable binning factor (`SolarSystemCameraTexture.BinningFactor`, 1×1/2×2/3×3/4×4), the same trade-off real acquisition software (SharpCap, NINA) exposes for exactly this resolution-vs-processing-cost problem. Defaults to 4×4 for playability; 1×1 native is available at real cost. Changing binning (or switching to an instrument with a different native resolution) tears down and rebuilds the camera's textures/scratch buffers on the next capture. Field of view is derived from the active telescope's own real focal length and the real (binned) pixel pitch: `MaxFovDeg` is the native (no-accessory) field across the sensor's long axis, `MinFovDeg` is that divided by the telescope's own real "tight zoom" factor, whether that's an amateur Barlow (RC20/CDK1000, real 4×) or a real named accessory of the instrument itself (FORS2's real HR collimator, 2×; SPHERE has none, 1×, since its own native/standard binning modes already span its real documented plate-scale range, §7.00).

**Binned full well** (`SolarSystemCameraTexture.FullWellElectrons`): real on-chip/charge-domain binning combines `BinningFactor²` physical pixels' charge into one before it's ever read out, so a binned pixel's real saturation capacity scales by `BinningFactor²`, not the native per-pixel datasheet figure applied unchanged. Getting this wrong at high binning on a huge-aperture instrument (observed case: the VLT at 4×4) makes every pixel look saturated far too early, and the blooming pass (§7.5) then turns that into a large white smear instead of the correctly-exposed frame. Shot noise and the calibrated-signal-per-unit scaling (§7.0) both use this binned figure; dark current intentionally still uses the native (unbinned) full well paired with the native per-physical-pixel dark-current rate, since both real electron quantities scale by `BinningFactor²` together in a real binned pixel and the resulting pedestal/sigma *fraction* comes out identical either way.

Two things had to be fixed to make real resolution actually tractable:
- **Sliding-window (prefix-sum) blur**: the drift-trail smear was naive per-offset resampling, `O(w·h·length)` — at real resolution, with length scaling alongside the frame width, this risked far worse than the ~50× pixel-count increase alone. Rewritten as an edge-clamped prefix-sum sliding window, `O(w·h)` regardless of length. (The optical/atmospheric blur that also used this trick has since been replaced outright by a real PSF convolution, §7.11.)
- **Memory-aware stacking cap**: a fixed 30-subs/filter cap was ~330MB worst-case at the old resolution; unchanged at native 4144×2822 it would reach ~7GB across 5 filters. `AstroImageStack.MaxSubsPerFilter` is now derived from a fixed ~1GB total memory budget divided across 5 filters, shrinking automatically as resolution goes up (floor of 3, ceiling of 30).
- **Lucky-imaging sharpness scoring** subsamples its region (targeting ~50,000 samples regardless of resolution) rather than sorting every pixel — sharpness/focus is a spatially broad property, so a representative stride is statistically equivalent and orders of magnitude cheaper at multi-megapixel resolution.

### 7.06 Background processing

The per-pixel physics pipeline (shot/dark/read noise, cosmic rays, blooming, CTI, PSF construction and convolution, defects — all pure C# array math, no Unity/KSP API touches) now runs on a background `Task`, following the same `StartImagingRefresh`/`PollImagingRenderTask` "gather on main thread → compute off-thread → upload on main thread" pattern already used for the direct-imaging/sky-chart/forecast renders elsewhere in this mod. Only the Unity camera render (`Camera.Render`, `ReadPixels`) and the final texture upload remain on the main thread; everything CelestialBody/Unity-API-dependent (magnitude, positions, cloud cover, seeing) is gathered into a plain-data struct before the background task starts.

### 7.07 Exposure range, ND filter, and Kerbin-scale overbrightness

`MinExposureSeconds`/`MaxExposureSeconds` are the active telescope's own real exposure range (§7.00), not an arbitrary floor: 32µs-2000s for the ZWO ASI294MM Pro (RC20/CDK1000, zwoastro.com datasheet), 0.25s-3600s for FORS2 (real 0.25s minimum full-frame imaging time; no published real maximum, since a professional CCD isn't electronically capped the way a consumer camera is, only practically limited by sky background/cosmic-ray accumulation, so 3600s/1 hour is used as a deliberate, coherent design choice matching standard real observatory practice of capping a single sub around that length and reaching longer total integration by stacking, which `AstroImageStack` §7.6 already does), 1.1s-3600s for SPHERE (real 1.1s minimum integration time, Table 4 of the ZIMPOL paper; same 3600s design choice as FORS2, for the same reasoning). The exposure slider maps drag position to `log10(seconds)` rather than a linear scale, the same convention real acquisition tools (SharpCap, FireCapture) use across a range spanning multiple decades.

Even at the RC20's real minimum, nearby KSP moons can still fully saturate every pixel. This is a real consequence of KSP's compressed-scale solar system, not a bug: Kerbin orbits Kerbol at ~0.09 AU (vs Earth's real 1 AU) and Mün orbits Kerbin at only 12,000km (vs the real Moon's 384,000km). Feeding real photometric constants through that geometry puts Mün's apparent magnitude at closest approach around **-22.5** — only ~4 magnitudes fainter than Kerbol itself, and roughly 10,000× brighter than the real full Moon (-12.7). No real camera's exposure/gain range is built for a target that close in brightness to its own star, and gain in particular can't help — real analog gain only ever amplifies above a sensor's native conversion gain, it has no headroom to attenuate below it. The real-world answer to "target near-star-bright" is optical attenuation, so `NdFilterStop` (`SolarSystemCameraTexture.cs`) adds a selectable neutral-density filter using standard photographic ND stops (`Nd8`/`Nd64`/`Nd1000`, OD 0.9/1.8/3.0, transmission `10^-OD`) plus a real ND 5.0 solar-filter-grade option (`Nd100000`, matching Baader AstroSolar safety film / Thousand Oaks solar filter optical density), multiplied into the transmission term ahead of `PhotonFluxModel.CollectedElectrons`.

### 7.08 Extended-source scintillation suppression

Young's scintillation formula (§5.2, §7.1) models a point source. Applied unmodified to a resolved solar-system body, a single per-exposure random draw (re-seeded every shot, sub-second precision) multiplied the *entire frame's* brightness at once; at low altitude/short exposure the sigma this formula produces is large enough that an unlucky draw could black out or blow out a whole photo, then come back "clear" seconds later on the next shot's independent draw. That flicker is physically wrong: real planets, unlike stars, don't scintillate anywhere near that hard, because a resolved disk spans many independent turbulent cells at once and their fluctuations average out across it, the same spatial-averaging mechanism a larger telescope aperture already gets credit for in Young's own `D^(-2/3)` term (**Dravins, Lindegren, Mezey & Young 1997, "Atmospheric Intensity Scintillation of Stars I", *PASP* 109, 173**).

`AtmosphericImagingNoise.ScintillationExcessSigma` now takes the imaged body's own angular diameter (`SolarSystemCameraTexture.ComputeAngularDiameterRad`, small-angle `2·radius/distance`) and projects it to a linear size at an assumed 8000m dominant-turbulence-layer height (same order of magnitude as the pressure scale height already used for the site-altitude term, §5.2), then combines that with the active telescope's own real aperture (§7.00) in quadrature (`sqrt(D² + sourceSize²)`) before applying Young's formula, exactly as if the telescope's own aperture were that much larger. A resolved planet ends up scintillating far less than a point star through the same scope; passing `angularDiameterRad=0` (a star) reproduces the original point-source formula exactly, so transit photometry (§5.2, `AtmosphericNoise.cs`, a separate class, untouched by this) is unaffected.

**Distribution bug fixed**: the per-exposure draw itself (`ScintillationMultiplier`, `SolarSystemCameraTexture.cs`) was `1 + N(0, σ)` — an additive Gaussian applied as a multiplier on the target's signal, but not on the sky background added after it. At the sigma this pipeline produces at real airmass (routinely σ > 1 at low altitude), that draw goes negative on a measurable fraction of exposures: 16% of the time at σ=1, 31% at σ=2 (verified by 400,000-draw Monte Carlo). A negative multiplier applied to the target but not the background doesn't merely dim the frame — it *inverts* it, since `target × negative` clips to black while `sky + haze` (both strictly positive) still saturates white. A bright planet came out as a black disc on a blown-out field.

Scintillation is a multiplicative modulation of an intensity, and an intensity cannot go negative — turbulence redistributes light, it never removes more than all of it. Real scintillation is in fact measured to be approximately **log-normal** (same Dravins, Lindegren, Mezey & Young 1997 series already cited above), so the fix is the physically correct distribution rather than a clamp bolted onto the wrong one: `X = exp(−s²/2 + s·Z)`, `Z ~ N(0,1)`, `s = sqrt(ln(1+σ²))`. This has unit mean and relative standard deviation exactly σ — the same first two moments the old formula targeted, so for `σ → 0` the two are indistinguishable and ordinary observing conditions are unaffected. Verified: 0.00% negative draws at every tested σ up to 2.0, mean and relative SD matching the target to three decimal places.

### 7.09 LRGB color calibration

`ComputeFramePixels`' calibration step (§7.1) converts each captured filter's raw rendered signal into real electrons by matching its sum to a physically-derived total (`ComputeCollectedElectrons`). That total is the same body-wide albedo split into equal thirds for R/G/B (no per-wavelength albedo data exists to do otherwise), so calibrating each filter against *its own* rendered sum forced every one of R/G/B to that same total regardless of the body's actual color, silently erasing it: a green-dominant body like Jool got its naturally-dim R and B channels boosted to match G's total, and the LRGB composite (`AstroImageStack.ComposeLRGB`) ended up showing whatever arbitrary hue survived the remaining per-pixel contrast differences between the three equalized channels, not the body's real color.

Fixed by calibrating every filter (R/G/B/Hα) against the same shared reference, the frame's luminance-weighted sum (`FilterSignal`'s own `Luminance` formula, `0.2126r+0.7152g+0.0722b`), instead of each filter's own channel sum. Each channel is then scaled by its real relative share of that luminance, so R:G:B keeps the body's true color ratio through calibration and into `ComposeLRGB`'s luminance-transfer step, which already assumes it's getting real relative color rather than three independently-normalized channels. The `Luminance` filter's own calibration is unchanged (it already used this same formula for its own sum).

### 7.1 Optics / atmosphere

- **Extinction**: Bouguer's law, same as §5.2, `k=0.20 mag/airmass`, every instrument.
- **Scintillation**: Young (1967), same formula as §5.2, using the active telescope's own real aperture and site altitude (§7.00); extended-source-suppressed per §7.08.
- **Seeing**: for a plain (non-AO) instrument, the site's own published median seeing at zenith (`VisualTelescopeSpec.ZenithSeeingFwhmArcsec`, referred to 500 nm), scaled to the frame's actual airmass and filter:

  ```
  FWHM = ε₀ · X^(3/5) · (λ / 500 nm)^(-1/5)
  ```

  Both exponents are the standard Kolmogorov result, not fits: `r₀ ∝ cos(z)^(3/5)` and `r₀ ∝ λ^(6/5)`, combined with `FWHM = 0.98·λ/r₀`. `X` is capped at 6 (~9.5° altitude), below which the plane-parallel atmosphere the law assumes no longer holds. The λ term means the blue channel of an LRGB set is genuinely softer than the red through the same air. For an AO instrument this term is replaced by the two-component model of §7.013.

  Per-site values, each from a published measurement of that site and nothing else:

  | Instrument | Site | ε₀ (500 nm) | Source |
  |---|---|---|---|
  | RedCat 51 | OHP, 650 m | 2.5" | Schmitt et al. 2024, *A&A* 687, A198 — "a median seeing (for OHP) of 2.5 arcsec" |
  | RC20 | OHP, 650 m | 2.5" | Schmitt et al. 2024, *A&A* 687, A198 — "a median seeing (for OHP) of 2.5 arcsec" |
  | CDK1000 | Palomar, 1712 m | 1.16" | Cenko et al. 2006, *PASP* 118, 1396 — "~1.1″ in R-band" (summer, P60), referred to 500 nm via the λ^(-1/5) term above |
  | FORS2 / SPHERE | Paranal, 2635 m | 0.72" | ESO Paranal astroclimate page — "The 50% percentile is 0.72″ FWHM" |

  *Two prior defects this replaced.* The model was `(airmass − 1) × 1.4 px`, capped at 6 px. First, it returned **exactly zero at the zenith**: an overhead target got no atmospheric blur at all, leaving a diffraction-limited disk — FORS2 resolving Jupiter at 0.017" from the ground, and the 20" RC20 rendering a limb sharper than any real telescope has recorded. Seeing is the atmosphere's turbulence; looking straight up traverses less of it, not none, and zenith is precisely where a site's median figure is quoted. Second, it was built in **pixels** and converted at the end, so the same sky delivered four times the angular blur at binning 4 as at binning 1. The model above is in angles throughout.
- **Defocus**: manual, only when autofocus is off, every instrument. Modelled as the geometrical blur disc of the defocused cone — uniformly illuminated, antialiased at its rim — and convolved into the PSF (§7.11) rather than applied as a separate pass. A flat-topped kernel is physically correct *here specifically*: its transfer function's zeros are why a genuinely defocused image shows contrast reversals.
- **Astigmatism** (not coma), per instrument (`VisualTelescopeSpec.AstigmatismStrengthPxAtCorner`): the radial-quadratic *falloff* (transverse blur scaling with the *square* of the field angle, smeared radially outward from frame center, zero at the centered target and worst near the corners) is the same Seidel-aberration physics for any two-mirror astrograph (coma would scale linearly instead — **Schroeder, *Astronomical Optics* 2nd ed. 2000, ch. 6**; Rutten & van Venrooij, *Telescope Optics*), but the *peak amplitude* depends on how completely each real design cancels off-axis aberrations:
  - **RC20** (3.0px): a true Ritchey-Chrétien (per `Observatories.cs`), and a real RC's whole reason for existing is that its hyperbolic mirror pair cancels third-order coma (**Ritchey & Chrétien 1922**) — giving it coma would misrepresent the optical design it's named after — but astigmatism is the dominant remaining off-axis aberration. No published PlaneWave RC20 optical-prescription number gives the amplitude to the precision needed, so the pixel figure is a display calibration constant, not a measured one.
  - **CDK1000** (0px): PlaneWave's own product page states the Corrected Dall-Kirkham design is "free of off-axis coma, astigmatism, and field curvature" — its corrector cancels both third-order aberrations a bare Dall-Kirkham would have, not just coma the way an RC does. Taking the manufacturer's own flat-field claim at face value, rather than inventing a nonzero residual with no published number behind it.
  - **VLT FORS2** (0px): a real, well-corrected two-mirror Cassegrain system, but no published VLT optical prescription gives a field-dependent astigmatism coefficient to the precision this pipeline's display model would need.
  - **VLT SPHERE** (0px): ZIMPOL's real field of view is only 3.6"×3.6", far too narrow for off-axis astigmatism to grow to any meaningful amplitude regardless of the telescope's own prescription — justified by the field size alone, not just the "no published coefficient" reasoning the other zero entries use.

### 7.015 Gaia to Johnson-Cousins (`Core/GaiaPhotometry.cs`)

Everything downstream of the star catalogue works in **Johnson V and B-V**: the magnitude normalisation (948 photons/cm²/s/Å at V), the colour term, the rendered star field. Gaia measures **G, G_BP and G_RP**. Any use of Gaia data has to cross that boundary, and crossing it by assuming `G = V` would be wrong by **1.54 mag** for a star at BP-RP = 3.

The coefficients are Gaia's own, from the DR3 documentation's "Photometric relationships with other photometric systems" (Table 5.9):

```
G − V = −0.02704 + 0.01424·x − 0.2156·x² + 0.01426·x³ ,   x = G_BP − G_RP
```

valid over `−0.5 < x < 5.0` (Table 5.10) with a residual scatter of **0.03017 mag**. Nothing is fitted, adjusted or extrapolated by this project.

- **Outside the validity range the colour is clamped, not extrapolated.** A cubic fitted over −0.5 to 5.0 diverges fast beyond it, and an extrapolated value would be this project inventing photometry Gaia did not publish. Same choice `SpectralCurve` makes outside a measured QE curve's range.
- **B-V is obtained by inverting Gaia's published `(BP−RP)(B−V)` polynomial numerically**, because Gaia publishes only that direction. Rather than fit a new one, the published polynomial is inverted by bisection, so the only quantity used is still Gaia's. A colour the relation cannot produce returns **NaN**, which callers must treat as "no colour known" rather than substituting a default.

**Harness verification** (6 checks): the polynomial reproduces the Sun's own `G − V = −0.14` (independently known from V = −26.76 and G = −26.90) to within the relation's own scatter, giving −0.1525; a red star's offset is 1.54 mag, which is why the transformation exists; clamping holds beyond the range instead of diverging; V↔G inverts exactly; the colour inversion closes on itself to 2×10⁻¹⁶; and an out-of-range colour returns NaN rather than a plausible default.

### 7.016 The star catalogue is user-supplied (`tools/pack_gaia_catalog.py`)

**Nothing ships.** A Tycho-2 catalogue used to: 29.3 MB for 2.5M stars complete to V ≈ 11.5, which is 61.9 stars/deg², about four stars in an RC20 frame where a real 30 s sub holds hundreds. That was the worst of both worlds, 29.3 MB carried to deliver a sky that still read as empty, so it has been removed outright. The choice is now a real star field or an honestly empty one.

§12 entry 28 used to record that limit as a modelling gap and state that closing it "needs a Galactic star-count model generating a statistical faint population, not a larger catalogue". **That conclusion was about what can be SHIPPED, and it is separable from what can be USED.**

Gaia's own measured counts, queried from `gaiadr3.gaia_source`, at this format's 12 bytes/star:

| Limit | Stars | File / RAM |
|---|---|---|
| G < 13 | 16.8 M | 202 MB |
| G < 14 | 36.9 M | 443 MB |
| G < 15 | 78.0 M | 935 MB |
| G < 16 | 157.7 M | 1.9 GB |
| G < 18 | 577.2 M | 6.9 GB |

None of that can ship. All of it can sit on a user's disk, so `pack_gaia_catalog.py` builds it on demand and the loader reads `GaiaStarCatalog.starcat`, logging a pointer to the tool when it finds none.

**Building one needs a free ESA archive account**, passed as `--user`. Anonymous access is the archive's degraded mode and hits a wall that neither subdividing nor retrying gets past: measured on Gaia DR3, one `source_id` range whose `COUNT` answers in 5 s, holding 2.6M rows of which 38,179 pass `G < 13`, fails its data fetch at **116 s on every attempt**, while the range beside it (2.2M rows, 32,261 selected) returns in **7 s**. Same size, same selectivity; the planner picks a scan for some ranges and the job limit kills it. The tool counts each range before fetching, splits any the count says is too big, retries transient refusals with backoff, and caches every completed range so a run that dies resumes. The password is never accepted on the command line; it is prompted for without echo or read from `GAIA_PASSWORD`.

The archive is queried over plain HTTP with no third-party package, so the tool runs on a stock Python 3. It reads the raw CSV rather than going through astroquery, and that is a correctness matter as well as a dependency one: astroquery exposes a missing `bp_rp` as a *masked* value, and `float()` on a masked entry returns the fill under the mask rather than NaN, so a NaN guard let 7 stars of 923 in the test cone through carrying a colour that had never been measured. `pack_gaia_catalog.py` is now the single definition of the packed format that `RenderedStarCatalog.cs` reads; its `VERSION` must be kept in step with `RenderedStarCatalog.FormatVersion`.

**Photometry** crosses from Gaia's system to the Johnson V / B-V everything else uses, by Gaia's own published relations (§7.015). Measured on a real packed cone toward the Galactic centre: a `G < 15` cut yields V from 9.03 to **18.53**, because a heavily reddened bulge star at BP-RP = 5 has `G − V = −3.56`. Stars with no Gaia colour keep G as V and are flagged colourless rather than given an invented one; 778 of 923 in that cone carry a colour. The archive CSV is read directly rather than through astroquery for exactly this reason: astroquery exposes a missing `bp_rp` as a *masked* value, and `float()` on a masked entry returns the fill under the mask rather than NaN, so a NaN guard let 7 of those 923 through with a colour that had never been measured.

**Density delivered**: 3264 stars/deg² in that cone against Tycho-2's 61.9 all-sky, so about 220 stars in an RC20 frame instead of four.

**Search cost does not scale with catalogue size.** The format is banded in declination and binary-searched in RA, so a cone search touches only the stars near the field. What scales is the rendering.

**Why the extension is `.starcat` and not `.bin`.** Kopernicus walks GameData and tries to read every `*.bin` it finds as a scaled-space mesh. A real KSP.log shows it doing that to this mod's own catalogue, immediately before succeeding on a real one:

```
[Kopernicus] Could not load '.../ExoInstruments/PluginData/RenderedStarCatalog.bin'
[Kopernicus] Loaded  '.../ParallaxContinued/Models/ScaledMesh.bin'
```

Harmless while the file was 30 MB. Not harmless at the 202-443 MB a useful Gaia build weighs, which Kopernicus would read at every startup before failing on it.

**Cost, measured rather than assumed.** Removing the shipped catalogue multiplied the sources in a frame by fifty and more, on a path that had never run with more than about four stars in it. All three costs were measured on the real code:

| | Measured |
|---|---|
| Deposition, worst realistic frame (RedCat 51, 13.2 deg², 43 084 stars, 54 px trails, unguided) | **8 ms** |
| Load, G < 13 / G < 14 / G < 16 | **2.0 s / 4.4 s / 18.8 s** |
| Memory | **12 bytes/star exactly**, so the file size is the RAM cost |

The star field is not the expensive part of a capture and does not become it: the PSF convolution over the same frame is 552 ms (§7.11), roughly seventy times the deposition. Load is a one-time cost paid at scene entry, and it scales linearly, so the depth table in the README is also a startup-time table. Guarded by a harness check that fails if the worst frame ever exceeds 2 s.

#### A pre-existing search bug this surfaced

`RenderedStarCatalog.Search` brackets each declination band's RA range from a single declination inside that band. It used the band edge **nearest the equator**, on the reasoning that the RA half-width grows as `1/cos(dec)`. That reasoning holds for the small-angle approximation `radius/cos(dec)` but not for the exact relation the search actually uses,

```
cos(radius) = sin(dec₀)·sin(dec) + cos(dec₀)·cos(dec)·cos(ΔRA)
```

where proximity to `dec₀` dominates: a cone's RA extent is widest at **its own centre declination** and shrinks to zero at its extremes. For every band on the equator side of the cone centre the two choices disagree, and the equator-nearest edge is the farthest from the centre, so it produced the narrowest bracket exactly where the widest was needed.

The effect was a thin crescent of stars silently dropped at the edge of every search cone. It was invisible at Tycho-2's four stars per frame; against a Gaia catalogue a 0.3° cone lost **8 of 923**. Fixed by bracketing from the band edge nearest the cone centre, and guarded by a harness regression check on that exact fixture.

### 7.02 Real measured filter curves (`Core/FilterCurves.cs`)

Every filter in the roster was a **top-hat**: a rectangle of its published FWHM at its published central wavelength, scaled by its published peak transmission. That remains the honest treatment when nothing else exists, and it is still what the amateur LRGB set and the H-alpha positions get. But ESO measured FORS2's filters *in the instrument* and publishes the tables, so for those three there is no reason to keep guessing a shape.

**Source**: ESO, FORS2 filter transmission curves (`.../fors/inst/Filters/curves.html`), which states that "the transmission curves for many of the FORS interference filters have been measured within the instruments". `M_BESS_B`, `M_BESS_V`, `M_BESS_R`, sampled at 10 nm from 330 to 1200 nm.

| Filter | Peak T | at | Half-power points |
|---|---|---|---|
| Bessell B | 0.6871 | 420 nm | 380-470 nm |
| Bessell V | 0.8887 | 530 nm | 500-600 nm |
| Bessell R | 0.8555 | 600 nm | 580-720 nm |

**What the shape changes, given the pipeline already integrates across the band.** Three things a rectangle cannot express: the equivalent width is not the FWHM once the shoulders slope; the colour term is weighted by where the filter really transmits rather than uniformly across a box; and since extinction and QE both vary across the band, *where* inside the band the light passes changes how much atmosphere and how much detector it sees.

Measured against the top-hat each filter would otherwise get:

| Filter | Top-hat W | Curve W | Change | Colour term (3500 K / 20000 K) |
|---|---|---|---|---|
| Bessell B | 347.2 Å | 303.0 Å | **−12.7 %** | 0.235 → 0.193 |
| Bessell V | 669.8 Å | 601.2 Å | **−10.2 %** | 1.021 → 0.998 |
| Bessell R | 891.2 Å | 853.8 Å | −4.2 % | 2.244 → 2.332 |

The B band's colour term moves by 18 %, which is the part that matters: it is the difference between an M dwarf and a hot star as this instrument records them.

**The full 330-1200 nm range is kept rather than trimmed to the passband**, because the red leak is real: **0.77 % (B), 1.34 % (V) and 3.21 % (R)** of each filter's integrated transmission sits more than 100 nm beyond its red half-power point, rising again towards 1200 nm the way every real interference filter does. Whether that leak reaches the detector is the QE curve's business, not the filter's. Measured: the CCD's own QE curve **suppresses 61 %** of R's integrated 900-1200 nm leak. Trimming the curve would have answered that question by assumption; integrating the product answers it.

**Reducibility**: `SystemResponse` takes the curve as an optional argument, and `null` takes the top-hat path unchanged. Fed a literal rectangle, the curve path reproduces the top-hat to **0.26 %**, the residual being Simpson's rule crossing the rectangle's two discontinuities at arbitrary node positions rather than any difference in model.

*Careful with double-counting*: a measured curve carries the filter's own transmission, so the published peak must not be applied on top of it. `BuildSystemResponse` drops `FilterPeakTransmission` whenever a curve is present.

**Not done**: SPHERE/ZIMPOL keeps top-hats. Its filter curves were not located in this pass, so its three positions stay on published FWHM and peak (§12).

### 7.11 Instrument point-spread function (`Core/OpticalPsf.cs`, `Core/FourierConvolution.cs`)

The frame is convolved with the instrument's real PSF, computed from first principles. Both files are pure C# with no Unity dependency, like the rest of `Core/`, so they are exercised by a standalone harness against published reference values.

**Diffraction — exact, from the pupil.** The Fraunhofer pattern of the telescope's own *annular* pupil (circular aperture with its real central obstruction), in closed form (Born & Wolf, *Principles of Optics*, obstructed-aperture case):

```
I(x)/I(0) = { [ 2·J1(x)/x − ε²·2·J1(εx)/(εx) ] / (1 − ε²) }² ,   x = π·D·θ/λ
```

Real Airy rings, and the real effect of the secondary on them. `J0`/`J1` via the standard Abramowitz & Stegun 9.4 polynomial approximations (error < 5×10⁻⁸ — a numerical method for a special function, not a physical approximation). The core FWHM is found by bisection on the exact profile rather than quoted from the usual `1.028·λ/D` rule, which only holds for an *unobstructed* aperture; every telescope in the roster has a secondary that narrows the core and moves energy into the rings.

**Wavelength now matters.** Because the whole pattern scales as `λ/D`, `VisualTelescopeSpec` carries a real *central wavelength* per filter position alongside the bandwidths — ZIMPOL V 554 / N_R 646 / B_Hα 655.6 nm, FORS2 Bessell B 429 / V 554 / R 655 nm, the amateur LRGB set by even thirds of its real 420–685 nm band. A single instrument-wide wavelength would erase a real, measurable effect: the same telescope genuinely resolves finer through blue than through red (RC20: 0.179" vs 0.247").

**Atmosphere — exact Kolmogorov, no fitted profile.** Fried's long-exposure atmospheric transfer function `T(f) = exp[−3.44·(λf/r₀)^(5/3)]` is the 5/3-power structure function of Kolmogorov turbulence and nothing more. It has no closed-form real-space counterpart, so the PSF is recovered by numerically Hankel-transforming it (a radially symmetric 2D Fourier transform is a zeroth-order Hankel transform) — exact up to quadrature error rather than a shape assumption. `r₀` follows from the seeing FWHM by the standard `FWHM = 0.98·λ/r₀`.

**Why this replaced a box blur.** The previous model applied a uniform square blur. A box kernel's transfer function is a sinc, which has **zeros and negative lobes**: it does not merely soften an image, it annihilates detail outright at some spatial frequencies and *inverts contrast* at others. Mid-scale structure — crater-sized features on a resolved disk — sits squarely in that range, so the box blur destroyed far more real detail than its nominal width implied, and did so unphysically. Every profile here has a monotonically decreasing transfer function with no zeros in the passband. Two further defects were fixed alongside it: the AO FWHM was being passed as a box *radius* when `2r+1` pixels were averaged, silently doubling every AO instrument's published resolution; and the seeing figure was carried in pixels (see §7.1).

**Convolution cost.** A real PSF is radially symmetric but **not separable**, so it cannot be applied as a horizontal pass then a vertical one. Applied directly, a kernel a few tens of pixels across costs `O(W·H·K²)` — of order 10¹⁰ operations on a multi-megapixel frame, minutes per exposure. `FourierConvolution` uses overlap-add over FFT tiles (iterative radix-2 Cooley-Tukey), `O(W·H·log N)`, which is an exact restructuring of linear convolution rather than an approximation of it. Measured: **552 ms** for a 2048×2048 frame with a 97×97 core kernel, **2.4 s** for the full two-component AO pass. Outside the frame the image is treated as zero rather than edge-clamped — beyond the sensor there is sky, and in these frames the sky is black; edge-clamping would smear the border pixel outwards and invent flux the detector never collected. Since the pipeline is monochrome by this stage, the transform runs on a single plane.

**The one approximation.** Airy wings extend formally to infinity, so any finite implementation truncates somewhere (professional simulation codes included). Kernels are cut where intensity falls below ~10⁻⁴ of peak, subject to a radius ceiling, and renormalised to unit sum so truncation costs no flux. This is a computational bound, not a physical assumption.

**Kernel construction cost.** Both profiles depend on radius alone, so they are evaluated on a fine 1D radial lookup table (4 samples/px, linear interpolation) rather than once per grid pixel. This is not an optimisation for its own sake: the atmospheric profile costs a 512-step quadrature with a Bessel evaluation per step, so a 256 px-radius halo kernel meant 263,169 quadratures — of order 10⁸ special-function evaluations, measured at **~5.5 s**, which stalled the main thread at the exact moment the player pressed Capture. Tabulating brings it to **31 ms** for a difference far below the kernel's own truncation. Construction also moved to the background pipeline (§7.06) — it touches nothing Unity-owned — and is cached on the instrument, filter, plate scale, atmospheric FWHM and defocus, so a stacking batch pays for the PSF once rather than once per sub. The AO residual solve (24 trial kernels, ~460 ms) sits behind that same cache.

**Harness verification** (20 checks, all passing): `J0`/`J1` against published values; unobstructed Airy FWHM reproducing `1.028·λ/D` (0.11673" vs 0.11662"); first null at `1.22·λ/D`; obstruction narrowing the core; SPHERE's diffraction FWHM at 700 nm = **17.93 mas**, below its 25 mas delivered figure as it must be; RC20's 0.2128" against its Dawes limit of 0.2275"; `r₀` = 11.12 cm for 1" seeing; kernel normalisation, symmetry and central peak; FFT convolution of a delta reproducing the kernel to 1.5×10⁻⁸; flux conserved across overlap-add tiles to 0.0000%; a uniform field staying uniform (no tile seams) to 1.4×10⁻⁶.

### 7.111 The same PSF, sampled instead of convolved (`Core/RadialPsfProfile.cs`)

§7.11 gives the PSF as a **kernel**, to convolve into a frame that already contains sources. The high-contrast imaging display (§4.2) has the opposite problem: it *synthesises* its frame point by point around a known source position, so it needs the PSF as a **function of angular offset**, evaluated per pixel. Until this class existed it answered that need with a Gaussian core (`σ = 0.45·λ/D`) plus an invented ring envelope, while `OpticalPsf` was computing the exact pattern a few files away. Two mutually inconsistent answers to a question already answered exactly. `RadialPsfProfile` draws its intensity from `OpticalPsf.AiryIntensity`, so **there is now one diffraction model in the project**.

**The pupil constant this required.** `DirectImagingSimulator` knew only `D = 39.3 m`. A diffraction pattern needs the obstruction too, and inventing one was not an option. ESO's own E-ELT optics page states the segmented primary "has a diameter of approximately 39 m" with "a **11.1 m central obstruction**" (the filled primary running from an inner radius of 5.5 m to an outer 18.5 m). The ratio is formed against the 39.3 m the class already uses everywhere else so the pupil stays internally consistent: **ε = 11.1/39.3 = 0.2824**, against 0.2846 on ESO's rounded 39 m, a 0.8% difference far below anything the pattern shows.

**Why the pattern cannot simply be point-sampled.** The Airy pattern oscillates with a radial period of about λ/D. The imaging display's field of view is set by the *target's* planet separation, so its plate scale spans two orders of magnitude across the catalogue: a close-in target gives 33 px per λ/D, a companion at 1″ gives **less than one pixel per ring**. Point sampling there does not merely look wrong, it aliases — consecutive pixels land at random on ring maxima and nulls, and the same physical pattern renders as arbitrary structure that changes with the field of view. Measured on the real pattern: consecutive point samples 1.5 λ/D apart differ by up to **59.8×**, against **4.28×** for the averaged profile, which is a 14× reduction in swing while still falling as steeply as the real θ⁻³ envelope.

A detector does not sample intensity at a point; it integrates it over the pixel's area. So the averaging is not a smoothing applied to make the raster behave — it is the missing physics, and it is what makes the display's appearance a function of the optics rather than of the raster.

**What a radial table must hold.** One value per radius, so the right quantity is the mean over the *ring* of pixels at that radius. Those pixels sit at every orientation relative to the detector grid, hence: the average of the intensity over a square pixel, itself averaged over the pixel's orientation about the source. Two regimes, split at 6 px:

- **Within 6 px** the intensity varies steeply across the pixel in *both* directions and the two-dimensional average is evaluated as such (midpoint rule, 32 nodes minimum per axis, 8 orientations across the square's 45° symmetry sector). No one-dimensional reduction is faithful here: taking only the radial extent overstates the peak by up to **11% of it**, because the disc of radius p/2 it averages over is 21% smaller than the pixel really there.
- **Beyond 6 px** the pixel subtends a narrow angle at the source, the intensity is effectively constant along its azimuthal extent, and the average collapses to `∫I(r)·r dr / ∫r dr` over `[θ−p/2, θ+p/2]`. This is the narrow-angle *limit* of the same integral, not a different model.

The split is what makes it affordable: the two-dimensional form costs O(n²) evaluations per table entry against O(n), and 6 px is 48 table entries whatever the plate scale, against the several thousand a full table holds. Measured on a 3842-entry table (480 px at 8 samples/px): **23 ms as shipped**, of which the radial portion is 2.3 ms and the 48 two-dimensional entries are the rest — against **2146 ms** if every entry went through the two-dimensional form. Built once per frame on the background pipeline, not per pixel.

**Harness verification** (14 checks, all passing):

- **Encircled energy against the closed form.** `∫I(r)·r dr = 2·[1 − J0²(x) − J1²(x)]` for an unobstructed pupil (Born & Wolf), reproduced to **4.3×10⁻⁸** at x = 1, 3.83, 7.02, 10.17 and 20. This tests intensity and quadrature together at every radius, which a shape check near the core cannot.
- **The obstruction's real signature.** Unobstructed first null on the textbook `1.2197·λ/D`; the ELT's own moves inward to `1.124·λ/D` (**9.44 mas** in H band), and the core narrows from 8.64 to **8.28 mas**.
- **Reducibility, and its order.** The pixel average departs from point sampling by **4.4×10⁻⁷** of the peak at λ/D per 1000, and halving the pixel divides that departure by **4.00 twice over** — the second-order convergence an area average must have, which a coincidentally small number would not show.
- **The model against a real square pixel.** Against a brute-force two-dimensional average written independently in the test file, the profile agrees to **6.7×10⁻⁴ of the peak** across plate scales from 0.1 to 4 λ/D per pixel. On the display's nine-decade log stretch that is about **a twentieth of one of its 256 levels**. The step at the 6 px crossover is 9.5×10⁻⁴, no larger than that residual and so adding nothing to the error budget (it is 1.9×10⁻³ if the crossover is placed at 3 px).
- **The table as the display uses it.** Interpolation carries the same integrated light as direct evaluation to **0.34%** over a full 400 px raster; the peak pixel dilutes monotonically as the plate scale coarsens, recovering 0.9989 of the point peak at 0.05 λ/D per pixel and holding 0.077 of it at 4 λ/D per pixel.

**The diffraction limit was reconciled with the pupil.** `DirectImagingSimulator.DiffractionLimitArcsec` used to return `1.22·λ/D`, the first null of an *unobstructed* circular aperture. The ELT is not one: its 11.1 m secondary pushes the first null inward to **1.124·λ/D**, or 9.44 mas in H band against 10.24 mas. The simulator disagreed with its own optics by 8.4 %, and the frame showed it, since the guide ring labelled "the diffraction limit" sat visibly outside the first dark ring the pattern actually had. It now returns the real first null, found on the exact profile by `RadialPsfProfile.FirstNullRad` and computed once. **This narrows `Resolvable`'s threshold, so a companion between 9.44 and 10.24 mas that previously read as unresolvable now resolves.** The speckle floor was decoupled at the same time: it scales against λ/D, the speckle field's own grid spacing, rather than against the first null, which is a property of the core and not of the halo. The two were the same quantity only while the diffraction limit was a fixed multiple of λ/D.

### 7.112 The real pupil, rings and spikes from one transform (`Core/PupilDiffraction.cs`)

§7.11 and §7.111 handle the radially symmetric part of the pattern exactly. A spider is not radially symmetric, so no radial profile can carry it, and the imaging display drew its six spikes from **three invented constants**: an amplitude of `4e-4` of peak at 1λ/D, an azimuthal Gaussian of σ=1.3°, and a 1/r² falloff. Three free parameters standing in for something the pupil determines outright.

**The calculation, and why it needs no free parameter.** The far-field amplitude is the Fourier transform of the pupil's transmission. Transmission is a sum of simple shapes and the transform is linear, so the amplitude is a sum of closed forms:

- A filled disc of radius `a` transforms to `π·a²·2J1(2πau)/(2πau)`; the annulus is the outer disc minus the obstruction disc.
- A rectangle transforms to a product of sinc functions. Each vane is a rectangle of width `w` spanning the open annulus radially, so it carries a phase from sitting off centre. Vanes on opposite sides carry conjugate phases and their sum is real, which is why six vanes reduce to three cosine terms:

```
A_vane_pair = 2·w·L · sinc(π·w·u_perp) · sinc(π·L·u_par) · cos(2π·d·u_par)
```

with `L` the vane's radial length, `d` its midpoint radius, and `u = θ/λ` resolved along and across the vane. Intensity is `|A_total|²` normalised by the on-axis value, which is just the pupil's **open area**, `πR² − πR_in² − n·w·L`.

**Why the spikes run perpendicular to the vanes.** A long thin bar transforms to something narrow along the bar and wide across it, because `sinc(π·L·u_par)` with `L = 14.1 m` is narrow while `sinc(π·w·u_perp)` with `w = 0.5 m` is broad. With vane axes at 0°/60°/120° the spikes fall at 90°/150°/30°. Getting this backwards produces a plausible-looking frame rotated 90° from reality, so the harness asserts it.

**Sourcing.** **Schwartz et al. 2018** (AO4ELT5, *"Sensing and control of segmented mirrors with a pyramid wavefront sensor in the presence of spiders"*) states it in prose: "The secondary mirror unit of the European Extremely Large Telescope (ELT) is supported by **six 50-cm wide spiders**, providing the necessary stiffness to the structure while minimising the obstruction of the beam." ESO's own main-structure page independently confirms the count: the M2 crown "is connected to the top ring by means of **six beams**, forming the 'spider'". The width is not perfectly settled in the literature — METIS phase D simulations quote 54 cm, and at least one published pupil figure is drawn at 40 cm. 50 cm is used because it is stated in prose by an ESO-co-authored paper rather than read off a diagram; spike brightness scales as the vane area squared, so the 40–54 cm spread is a factor 1.8 on an effect of order 1e-4 of the peak. Recorded in §12 rather than hidden.

The vanes are modelled as spanning **only the open annulus**, from the obstruction's edge outward, so they neither overlap each other at the centre nor double-subtract the region the secondary already blocks. A real spider does converge on the secondary, which sits inside the obstruction and is therefore already dark.

**Harness verification** (8 checks, all passing):

- **Reducibility, to machine precision.** With the vanes removed, the pupil transform reproduces the published closed-form annular pattern (§7.11) to **7.8×10⁻¹⁶** of peak over 0–20 λ/D — two independent routes to the same physics, one via the difference of two disc transforms and one via Born & Wolf's obstructed-aperture form. Azimuthal spread without vanes: **3.3×10⁻¹⁶**, i.e. exactly flat, as a circular pupil must be.
- **Normalisation is geometry.** The vanes remove **3.789 %** of the open pupil, matching `n·w·L / (π(R²−R_in²))` to 1e-12, and on-axis intensity is exactly 1.
- **Spike direction and contrast.** Spikes land perpendicular to the vanes; along one at 6 λ/D they stand **9.6×10⁶×** above the faintest azimuth.
- **Pixel averaging** over the vaned pupil still reduces to point sampling at λ/D per 500.

**Cross-validated against POPPY** (`tools/poppy-crossvalidation/compare_vanes.py`). Same pupil, no shared code or method. POPPY's brightest azimuth at 6 λ/D is 150°, one of the three axes this model predicts, established independently. Intensity along a spike agrees to **0.16 % / 0.18 % / 0.10 % / 0.02 % / 0.11 %** at 2/4/8/12/16 λ/D, and between spikes to 0.27 %/0.21 %/0.04 %/0.04 %/0.36 %. Spike-to-background contrast, the quantity a viewer actually sees, agrees to **0.1 %** at 4, 8 and 12 λ/D. Two radii disagree by more (77 % at 3 λ/D, 9 % at 6 λ/D) and both sit **on nulls**, where the intensity falls to 1e-5–1e-6 of peak and POPPY's finite pupil sampling cannot reach a zero's true depth; the percentage there measures POPPY's grid, not a disagreement.

**How wrong the discarded constant was.** Along a spike at 6 λ/D the real vanes give `3.06e-4` of peak against `1.17e-5` without them, a factor **26**. The old constant asserted `4e-4` at 1 λ/D falling as 1/r², which is `1.11e-5` at that radius — it under-predicted the spike by a factor of 28, and put it in the wrong place.

### 7.113 Spider vanes on the visual roster (`VisualTelescopeSpec.SpiderVaneCount/WidthMeters`)

The visual roster's PSF kernel was radially symmetric by construction, so **none of these five telescopes could show a diffraction spike** however real its spider. `OpticalPsf.BuildKernel` now takes the vane geometry and samples `PupilDiffraction` (§7.112) in two dimensions when there is one; the atmospheric and defocus terms are unaffected and stay radial. With no vanes it takes the radial path and is bit-for-bit the previous behaviour, which the harness asserts tap by tap.

**Sourcing, per instrument.**

| Instrument | Vanes | Width | Basis |
|---|---|---|---|
| RedCat 51 | 0 | — | A Petzval **refractor**: no secondary, so no spider. A fact about the design. |
| RC20 | 0 | — | Real spider, **no published vane width**. Declared, not guessed (§12). |
| CDK1000 | 0 | — | Same. |
| FORS2 (UT1) | 4 | 4.1 cm | See below. |
| SPHERE (UT3) | 4 | 4.1 cm | Same telescope structure. |

The VLT width comes from the scaled pupil masks the coronagraphy literature cuts for laboratory work. **Martinez et al. (2011)** describe theirs: the VLT pupil at Φ=3 mm "is designed with the central obscuration scaled to 0.47 mm ± 0.002 mm (14% linear ratio) and the spider-vane thickness is 15 µm ± 4 µm". At this telescope's 8.2 m that is **4.1 ± 1.1 cm**.

**The scaling validates itself.** The same paper's E-ELT mask uses 40 µm vanes on a 29% obscuration. Scaled to 39.3 m that gives **52 cm**, against the **50 cm** Schwartz et al. (2018) state in prose for the real ELT (§7.112): a 4 % agreement on an independent telescope. That is what justifies applying the same scaling to the VLT mask rather than treating a laboratory mask as a loose analogy.

The **count** is weaker evidence than the width, and is flagged as such: ESO's technical prose says only that M2 is held "by means of metallic beams called spiders" without giving a number, so four is read from the telescope's own structure rather than quoted. Listed in §12.

**What this actually changes, which is less than it sounds.** A spike can only be drawn if the plate scale resolves the diffraction pattern at all. Measured across the roster at 554 nm:

| Instrument | Plate scale | Airy FWHM | px per FWHM | Spikes |
|---|---|---|---|---|
| RedCat 51 | 3820 mas | 2306 mas | 0.60 | no secondary |
| RC20 | 275.4 mas | 213.3 mas | 0.77 | no width published |
| CDK1000 | 159.2 mas | 105.5 mas | 0.66 | no width published |
| FORS2 | 126.0 mas | 14.19 mas | 0.11 | **below one pixel** |
| SPHERE/ZIMPOL | 1.80 mas | 14.19 mas | **7.88** | **yes** |

**ZIMPOL is the only instrument in the roster whose plate scale resolves its own diffraction pattern.** FORS2 sits at 0.11 px per Airy FWHM and is thoroughly seeing-limited, so its spider is real but its spikes fall far below one pixel; the model draws them anyway and the sampling erases them, which is the physically correct outcome rather than a shortcut. The amateur instruments are all undersampled by a factor around 1.3-1.7 at native resolution.

The VLT spider also removes only **1.1 %** of the open pupil against the ELT's 3.8 %, and spike brightness scales as the vane area squared, so VLT spikes are intrinsically about an order of magnitude fainter than the ELT's relative to the peak.

**Harness verification** (3 checks): the vaneless path is bit-identical to the previous kernel across all 2401 taps; the vaned kernel still sums to 1.000000005; and at a ring 18 px out it carries **227×** azimuthal contrast against the vaneless kernel's 5.9×. *(That 5.9 is itself a sampling artifact worth recording: sampling a ring at rounded pixel centres makes the sampled radius wobble by half a pixel, and on the steep flank of a diffraction ring that alone produced a 3.11× spread in a kernel that is exactly radially symmetric. The check interpolates bilinearly for that reason.)*

**Truncation.** Spikes formally run across the whole frame while the kernel is bounded by `MaxKernelRadiusPx`. The kernel carries them only within its own support and is renormalised as always, so no flux is lost but the far spike wings are not drawn. Same computational bound the Airy wings already have (§7.11).

### 7.12 Display transfer function (`SolarSystemCameraTexture.DisplayStretch`)

Selectable **Linear / Log / Asinh**, applied when a finished frame is turned into something the eye can read. **Display only**: `GetLastCaptureFullPrecision`, the FITS export (§7.7) and everything `AstroImageStack` consumes always receive the untouched linear signal — the same separation between viewer and data that every real observing tool keeps. Changing the mode restretches the frame already on screen instead of forcing a new exposure.

No astronomical image is looked at linearly. A resolved planetary disk puts almost all of its pixels into a narrow bright range, so real surface contrast — a few percent of the local level — occupies a handful of the 256 levels an 8-bit display has and is invisible, even though the data holds it perfectly. This is why a physically correct PSF can still produce a frame that reads as featureless: the limitation is the viewer, not the optics. Every real viewer (DS9, PixInsight, IRAF, ESO Reflex) offers exactly this choice.

- **Log** — DS9's own formulation `y = log(a·x + 1) / log(a + 1)` at its default `a = 1000` (Joye & Mandel 2003, ADASS XII, the SAOImage DS9 paper). Strongest lift of faint detail; compresses the bright end hard.
- **Asinh** (default) — Lupton et al. 2004, *PASP* 116, 133, "Preparing Red-Green-Blue Images from CCD Data", the transfer function SDSS's own imagery uses. Linear near zero and logarithmic beyond, so faint structure lifts without crushing bright regions the way a pure log does. The softening parameter (0.02 of full scale) places the turnover just above this pipeline's real noise floor, so genuine faint structure is lifted while the noise itself is not amplified into visible grain.

### 7.13 Scaled-space rendering fidelity (`SolarSystemCameraTexture.RenderScene`, `KopernicusOnDemandIntegration.cs`)

Four independent defects in the render path, all invisible on a stock install and all surfaced by testing under Real Solar System — each produced a symptom that looked like a different kind of bug (colour banding, a black disc, first-capture-after-alt-tab corruption) until traced to source.

**Home body's own scaled-space stand-in was left enabled.** The clone camera sits at the home body's own scaled-space position — i.e. *inside* its stand-in sphere. The visibility loop skipped turning the home body's fader *on* (it's already showing in the live scene) but never turned it *off*, so if the live scene happened to have it enabled, the capture rendered it as a shell wrapped around the camera: a large smooth coloured gradient with a curved terminator running through the frame. The threshold that decides whether it's on at a given moment (`ScaledSpaceFader.fadeStart`/`fadeEnd`) is set per body and differs between planet packs, so the bug was latent on stock (where it stayed off) and became visible under RSS (different thresholds). Fixed by explicitly disabling the home fader before rendering and restoring its exact prior state afterward, so the live scene is never affected by a capture.

**Galaxy camera never had its projection matrix reset.** `AimCamera`'s `CopyFrom(liveCamera)` inherits the live camera's own projection matrix, which silently overrides a `fieldOfView` set afterward. The scaled-space camera already had `ResetWorldToCameraMatrix()`/`ResetProjectionMatrix()` for exactly this reason; the galaxy camera (stars/skybox) did not, so the star field was rendered at the game's own wide field regardless of the telescope's real zoom. Verified by decompiling `Assembly-CSharp`: stock KSP's own `ScaledCamera.SetFoV()` sets `fieldOfView` on both cameras together, so treating them differently here was never correct. The scaled-space pass is also rendered **twice per capture, discarding the first pass** — see the next paragraph for why.

**…and correcting it showed that the galaxy camera should not be rendered at all.** With the projection finally at the telescope's own field, the painted sky cube is magnified past any possible use. The cube is 4096 px across a 90° face, i.e. **1.32′ per texel**, against these fields:

| Instrument | Field | Texels across the frame | Magnification |
|---|---|---|---|
| FORS2 + HR collimator | 4.3′ | 3.3 | ×1256 |
| FORS2 | 8.6′ | 6.5 | ×628 |
| RC20 + 4× Barlow | 4.8′ | 3.6 | ×1149 |
| RC20 | 19.0′ | 14.4 | ×287 |
| RedCat 51 | 263.8′ | 200 | ×21 |

A FORS2 frame is a bilinear interpolation of roughly six texels blown up 628×: vast smooth blobs, which the 8-bit render target (`ARGB32`/`RGB24`) then slices into hard-edged contour bands the moment a non-linear display stretch lifts the bottom of the range. The banding is diagnostic — one 8-bit step is 0.4 % grey under Linear but **4.2 % under Asinh and 23 % under Log**, which is exactly why the artifact appeared under stretch and vanished under Linear. The straight edges follow too: iso-contours of a bilinearly interpolated field are straight within each texel cell.

Magnification aside, a painted texture has no photometric meaning and does not belong in a radiometrically calibrated frame — it would be folded into the target's own electron budget (§7.011) and scaled by that target's brightness, which is why the exposure needed to reveal it differed from planet to planet. It also double-counts, painting stars on top of the real catalogue field this pipeline now draws itself. The pass and its camera clone are removed; the scaled-space camera clears to solid black, and the background is supplied entirely by `SkyBrightnessModel` in real V surface brightness.

**`RenderTexture` contents are volatile.** Unity documents a `RenderTexture`'s backing surface as not guaranteed to survive graphics-device events, fullscreen transitions among them — i.e. alt-tabbing. `renderTexture.IsCreated()` is now checked and the surface recreated if the device released it.

**Kopernicus's on-demand scaled-space texture loading doesn't know about this mod's cameras.** Large planet packs (RSS foremost) ship scaled-space textures far too large to keep every body resident at once, so Kopernicus's `ScaledSpaceOnDemand` component loads/unloads them driven by Unity's `OnBecameVisible`/`OnBecameInvisible` on each body's own renderer — decided by the cameras the *game* knows about. This mod's telescope renders through its own off-screen clone cameras, which Kopernicus has no way to account for, so a body could be unloaded precisely while being photographed: its mesh still draws (geometry doesn't depend on the texture), but with no colour map bound it renders as a black disc with a lit limb. Being demand-driven, the failure is inherently intermittent — it depends on whether the body happened to be visible to a *real* camera recently — which is why it presented as filter-dependent, alt-tab-dependent, and resolution-dependent in testing before a saved raw-render diagnostic frame (a temporary, since-removed debug aid) isolated it to the texture, not the pipeline.

Fixed by `Visualization/KopernicusOnDemandIntegration.cs`, a soft dependency on Kopernicus via reflection (same pattern as `EveCloudIntegration.cs`, §7.2) — builds and runs without it. API verified by decompiling `Kopernicus.dll`: `ScaledSpaceOnDemand.LoadTextures()` is public and **synchronous** — it pumps its own loader coroutine to completion before returning — so calling it immediately before `RenderScene` guarantees the texture is bound by the time the camera draws. Called on the target *and* its moons (a Galilean moon rendering as a black dot beside a correctly-textured Jupiter is the same bug, just easier to miss). Checks the component's own `isLoaded` field first, so an already-resident body costs one reflected field read.

Both camera passes are rendered twice per capture (only the second is read back) as a general-purpose warm-up: the first pass makes Kopernicus's/Unity's demand loading explicit, so whatever it triggers completes before the frame that counts, rather than requiring a state machine that has to enumerate every event (focus loss, scene load, camera visibility) that can invalidate a GPU resource.

**Failures are now surfaced, not just logged.** `PollProcessTask`'s catch previously only wrote to `Debug.LogError` — a failed background task (e.g. an allocation failure on a large frame) looked identical to a corrupted image in the panel, with no way to tell the difference without opening the debug console. `SolarSystemCameraTexture.LastProcessingError` is now shown directly in the capture panel, with a dedicated message for `OutOfMemoryException` naming the frame size and suggesting a higher binning factor. `RenderTexture.Create()`'s own return value (previously discarded) is checked the same way: a refused allocation is logged and flagged via `RenderTargetRefused` instead of silently reading back whatever was already in the buffer.

**Memory footprint, measured per pixel** (relevant background for the failures above): the pipeline is monochrome — one real value per pixel — but stores it duplicated three-fold in 16-byte `Color` buffers along the way rather than 4-byte `float`. Per pixel: `src`/`frameScratch`/`lastCaptureSnapshot`/`displayScratch` at 16 B each (64 B), `rawScratch`/`psfPlaneScratch`/FFT accumulator at 4 B each (12 B), for **76 B/px managed heap**; plus `renderTexture` (ARGBHalf + 24-bit depth, 12 B), `readbackTexture` (RGBAHalf, GPU + readable CPU copy, 16 B), `outputTexture`/`capturedTexture` (RGB24, GPU + CPU each, 12 B) for **40 B/px** of textures — **116 B/px total**. At FORS2's native 4096×4128 (16.9 Mpx) that is **1961 MB per capture**; at RC20/CDK1000's native 4144×2822 (11.7 Mpx), **1357 MB**.

**Why the capture is half-float** (`renderTexture` + `readbackTexture`; the display textures stay 8-bit, since a monitor is). The rendered scene supplies all of the frame's spatial structure and the physics then multiplies the whole plane by one calibration factor, so quantising the render quantises the photograph. sRGB-encoded ARGB32 resolves 3295:1 — **8.8 magnitudes** — which is less than a single frame routinely spans: Jupiter at V=−2.5 beside a Galilean moon at V=5.0 is a real 1000:1 ratio, putting that moon on **3.3 quantisation levels** with its limb, phase and shading destroyed before the optics are applied. Any non-linear display stretch then slices what remains into contour bands, the same mechanism that exposed the painted sky cube's texels (§7.10). Cost is **+14 B/px, ~14 %**; at the 2×2 binning the observing guide recommends, +59 MB. Falls back to the 8-bit target, with a logged warning, on a device without `ARGBHalf` support.

*What this does **not** claim.* Half float removes quantisation; it does not make the values linear radiance. KSP renders in **Gamma colour space**, so its shader output is display-referred, and no inverse transform recovers true radiance from it — in gamma space the lighting is itself computed on encoded albedos, so raising the result to 2.2 would darken the terminator without justification rather than linearise anything. The absolute scale is unaffected (calibration normalises the frame to a physically computed electron total, §7.011), but the *relative* shading within a disk inherits the game's gamma-space lighting. This is inherent to building on KSP's renderer and is recorded here as a known limitation, not worked around. `frameScratch` is now reused across captures rather than freshly allocated each time (was churning ~270 MB/shot at FORS2 1×1 through the large-object heap), and every frame-sized buffer is released in `Dispose()` (three of them, including the PSF halo scratch, previously were not, so they survived a binning change). See the README's Solar-System Observing Guide for the per-config table and the practical recommendation (2×2 as the default balance).

### 7.14 Rendered star field and frame geometry (`Core/GnomonicProjection.cs`, `RenderedStarCatalog.cs`, `StellarPhotometry.cs`, `StarFieldRenderer.cs`)

Before this, a photograph's sky was empty. The only "stars" a frame ever contained were hot pixels and cosmic rays, and the reason was structural rather than cosmetic: the frame was built by taking one rendered image and scaling it so its total matched the **target body's** electron count (§7.0). Under that scheme nothing except the target can have a correct brightness, because there is one scale factor and it belongs to the target — a star drawn into the render would be scaled by the planet's budget rather than its own.

The frame is now built as a **sum of sources**, the way every serious image simulator works (**GalSim** — Rowe et al. 2015, *Astron. Comput.* 10, 121; **SkyMaker** — Bertin 2009; ESA's **Pyxel**): each source carries its own independently computed flux, and they are summed on one plane before any optics or noise are applied.

**Order of operations in `ComputeFramePixels`** — this is the substantive change:

1. **Signal plane** (fractions of full well): the rendered bodies, scaled to their real electron count, plus every point source deposited at its own sub-pixel position.
2. **Optics**: one PSF convolution (§7.11) plus off-axis astigmatism, acting on the **signal**.
3. **Sky**: a real surface brightness (§7.3), uniform across the frame. Convolving a constant field with a unit-sum kernel returns it unchanged, so adding it after the PSF is exact and saves a transform.
4. **Detector**: shot noise, dark current, gain, read noise, cosmic rays, blooming, CTI, defects.

The previous version convolved the PSF *after* drawing noise. Blurring a noise field correlates neighbouring pixels and shrinks its variance, so the frame's measured signal-to-noise ratio no longer matched the physics that produced it, and no photometry or stacking done on it could be trusted. Optics blur light; they cannot blur the readout that happens afterwards.

**Frame geometry.** The projection is the **gnomonic (tangent-plane)** one a flat focal plane physically performs, which FITS calls TAN (**Calabretta & Greisen 2002, A&A 395, 1077**, §5.1.3). It is built from the camera's **own three axes**, not from an assumed orientation, so the star field and the rendered planet share one geometry by construction. The chain is: the telescope's aim is a real direction in the game's world → the observatory's local north/east/up basis turns it into altitude and azimuth → `SkyCoordinates.HorizontalToEquatorial` (new, the exact inverse of the existing forward transform, verified to 3e-12° over 200,000 random round trips) turns those into the RA/Dec the catalogue is indexed by. The local basis is read from KSP's own latitude/longitude convention by asking the home body where a point slightly north and slightly east of the observatory is, rather than from cross products of a rotation axis — Unity's left-handed frame makes the sign of such a product easy to get backwards and impossible to notice, and this form simply cannot be wrong about which way east is. Nothing in it is tied to a particular home world or planet pack.

**Field-of-view bug fixed along the way.** `Camera.fieldOfView` is Unity's **vertical** field; every field of view in this class is quoted across the sensor's long axis, because that is how a telescope's field is quoted and how the zoom range is derived from the real focal length. Assigning one to the other rendered the scene at the sensor's aspect ratio too wide — 1.47× on the RC20's 4144×2822 chip — so a body's size in the frame did not match the plate scale the same class reports for the FITS header, and no star drawn at its real position could have lined up with it. **Bodies now appear 1.47× larger at the same zoom setting than in previous builds.**

**Catalogue: user-built from Gaia, and why not the BSC.** The Bright Star Catalogue stays exactly as it is and the detection pipeline is untouched — it is deliberately small so that hunting a transit remains a tractable game. It is simply the wrong catalogue for *rendering*: 9110 stars over 41253 deg² is 0.22 stars/deg², so an RC20 frame (0.068 deg²) contains 0.015 of them, one frame in 65 showing a single star. The rendered field is supplied instead by a **Gaia DR3** catalogue the user builds (§7.016), with photometry converted to Johnson V and B-V at pack time by Gaia's own published relations (§7.015). A shipped **Tycho-2** file (Høg et al. 2000, A&A 355, L27; 2,557,476 stars, 61.9 stars/deg², 4.2 per RC20 frame) filled this role until it was removed: at four stars per frame it did not deliver a star field, and 29.3 MB was a high price for that.

**Field of view, not catalogue depth alone, is what decides whether a photograph has stars in it.** For a fixed catalogue density, how many land on the sensor is pure geometry, and the long-focus instruments lose that geometry badly. Measured below at the 61.9 stars/deg² of the Tycho-2 file this used to ship, which is also the floor a shallow Gaia build reproduces:

| Configuration | Focal length | Field | Expected stars | P(frame with none) |
|---|---|---|---|---|
| RC20 + 4× Barlow | 13 872 mm | 0.08° × 0.05° | **0.26** | **77 %** |
| RC20 native | 3 468 mm | 0.32° × 0.22° | 4.2 | 1.4 % |
| RedCat 51 | 250 mm | 4.40° × 2.99° | **816** | ~0 |

So a planetary frame at full zoom containing no stars at all is the *expected* result, three times in four, and no exposure time can change it: the pipeline reaches V≈25 in 20 s while the catalogue stops at V≈11.5, so the extra depth falls into an empty magnitude range. This is also why a deeper catalogue is not the answer — reaching one star per zoomed frame needs 234 stars/deg², i.e. 9.7 M sources and a 116 MB file at this format's 12 bytes/star, and five stars needs 579 MB. **The wide-field instrument solves it for free**, using the catalogue already shipped.

*(Real planetary imaging behaves the same way: a genuine Jupiter frame has a black, starless sky, because the field is tiny and the exposures are milliseconds. On the RC20, Jupiter at opposition saturates in ~35 ms.)*

Packed by `tools/pack_gaia_catalog.py` (12 bytes/star), indexed in 0.1° declination bands so a cone search reads only the bands the field overlaps: 200 one-degree searches take 2 ms. Positions are **fixed point over a full turn**, not float32 degrees — a float32 near RA = 360° resolves only 0.077 arcsec, harmless at the RC20's 1.1"/px but **forty-three pixels** at SPHERE/ZIMPOL's 1.8 mas plate scale; fixed point gives a uniform 0.3 mas everywhere for the same four bytes, and the raw integers stay monotonic in RA so the binary search runs on them directly. Proper motions are dropped deliberately (a typical field star's is ~10 mas/yr, one ZIMPOL pixel per three years of in-game time).

**Photometry per filter (`StellarPhotometry.cs`).** The catalogue gives one number, Johnson V at 5556 Å; the instrument may be looking through a blue filter or a 7nm Hα one. Treating V as if it applied to whichever filter is fitted would make every star the same colour, which is the single thing that makes a synthetic star field look synthetic. The V magnitude is instead transported to the filter's passband across a Planck spectrum at the star's effective temperature, derived from the catalogue B-V by the **Ballesteros (2012, EPL 97, 34008)** relation already used elsewhere in this mod. The colour term is a *ratio* of photon spectral densities at the two central wavelengths, so at 5556 Å it is exactly 1 and the result reduces to the measured magnitude — the model only interpolates away from a real measurement, never replaces it. A star with no catalogue B-V (3642 of them) gets **no** colour term rather than an assumed one.

**Deposition (`StarFieldRenderer.cs`).** Sources are deposited as bare sub-pixel-positioned delta functions, bilinearly split across the four pixels they fall between; the PSF is convolved over the whole plane afterwards, which is exactly equivalent to drawing the PSF at each source position for a fraction of the cost. Rounding to the nearest pixel centre would lay the whole field on a visible lattice. Each star is projected **twice**, at the start and end of the exposure, through the same fixed sensor geometry against a sky that has rotated in between — which is what produces curved trails and field rotation for free (§7.5). Sources below the frame's own noise floor (sky shot + dark + read noise, scaled by 5%) are not drawn, and the same criterion sets the catalogue search's limiting magnitude, so a short exposure reads only bright stars while a long one pulls in everything the catalogue holds.

**Other solar-system bodies in the field.** A body whose apparent diameter is under 2 pixels is a point of light that the renderer draws as at most a dim sub-pixel speck with no correct brightness; it now goes through the same deposition path as a star, with its real apparent magnitude from §7.0 — which is how the moons of a giant planet show up as points beside it in a real photograph. A body large enough to be resolved is left to the renderer and instead counted in the electron budget the rendered image is calibrated against, so nothing is counted twice. That budget is now the **sum over every resolved body in frame** rather than the target alone: with the old target-only figure, a moon sharing the frame stole part of the target's budget and neither came out at its real brightness.

**Known gap.** With no catalogue installed there is no star field at all, and a shallow build still stops well above what a 30 s RC20 sub reaches. Giving a player who installs nothing a plausible field needs a **Galactic star-count model** (Bahcall & Soneira 1980; Besançon, Robin et al. 2003; TRILEGAL, Girardi et al. 2005) generating a statistical faint population from the luminosity function and disk/halo density laws, not a bigger catalogue — this is exactly what UFig (Bergé et al. 2013) does, and it is the same source-list architecture, so it is additive. *(The second gap recorded here previously — that `PhotonFluxModel` carried no optical throughput term at all — is closed: see §7.001 and §7.0. The RC20's limiting magnitude is now 0.30 mag shallower and SPHERE's 0.71 mag, and the remaining unmodelled losses are enumerated per instrument in §7.001's table rather than absent wholesale.)*

Validated headless by `tools/skyfield-tests/` (26 checks: projection scale and centring against the optics, colour-term limits, sky-model unit scaling, extinction ordering, flux conservation under trailing and clipping, catalogue density against the published total, pole and RA=0h wrap handling, and an end-to-end tracked/untracked comparison on a real patch of sky).

### 7.2 Clouds (EVE integration — `Visualization/EveCloudIntegration.cs`)

Reflection-based soft dependency on **EVE-Redux** (API "verified by decompiling EVE-Redux 1.11.7.2 with ilspycmd"). Samples the real installed cloud-layer cubemap texture for the home body at KSC's zenith direction (a fixed body-frame vector — narrow FOV means the exact viewing direction barely matters). Returns 0 if EVE isn't installed or no cloud layer is configured; **no procedural fallback**. Known approximation: EVE's own wind-drift texture animation isn't replicated (a static sample). Coverage feeds two effects, both of them things cloud physically does: `cloudTransmission = 1 - coverage·0.85` (never fully opaque), and the veiling term of §7.3.

**A third, cloud-driven blur, was removed.** It added up to 2 **pixels** of FWHM, so the same overcast sky delivered four times the angular blur at binning 4 as at binning 1 — the same unit defect the seeing term was rewritten to eliminate (§7.1). Correcting the unit would only have relocated the problem: no published coefficient relates cloud cover to delivered FWHM, because it is not an optical mechanism. Cloud attenuates and cloud veils; poor seeing and cloud are correlated symptoms of unsettled weather, not one causing the other. The term was an invented constant standing in for a mechanism that does not exist, and there is no sourced value to replace it with.

### 7.3 Sky background (`Core/SkyBrightnessModel.cs`)

Every term is now a **real V surface brightness in mag/arcsec²**, summed as flux and converted to electrons per pixel through the same photometric chain as the sources sitting on it (§7.0). This replaced a set of per-second, per-pixel rates (`twilight 0.30 + moon 0.02 + airglow 0.004 + zodiacal 0.000916`) that carried no physical unit, could not be checked against any published sky-brightness measurement, and silently depended on the plate scale — so binning the sensor or fitting a Barlow changed how bright the night sky was.

- **Airglow / dark-sky baseline**: 21.7 mag/arcsec² at the zenith (**Patat 2003, A&A 400, 1183**, "UBVRI night sky brightness at ESO-Paranal"). Because airglow is emitted *inside* the atmosphere it brightens toward the horizon rather than dimming: the **van Rhijn (1921)** function `I(z)/I(0) = 1/sqrt(1 - (R/(R+h))²sin²z)` with the emitting layer at h = 90 km (the OI 557.7nm and OH Meinel bands — **Roach & Gordon 1973, *The Light of the Night Sky***). Extinction over that same path is applied too; the two largely cancel, which is why the observed zenith-angle dependence of airglow is weak.
- **Zodiacal light**: 23.3 mag/arcsec² at the ecliptic pole, its faintest tabulated value (**Leinert et al. 1998, A&AS 127, 1**). Extra-atmospheric, so it is attenuated by extinction rather than enhanced by van Rhijn. Held at the polar value: the cloud's real brightness distribution is a Solar System measurement with no counterpart to read from the game.
- **Moonlight**: 18.7 mag/arcsec² for a full moon at the reference separation, ~3 magnitudes above dark sky. The separation and phase dependence is the real **Krisciunas & Schaefer (1991)** kernel already implemented in `MoonlightPollution.ScatteringKernel` (§5.3); only its normalisation lives here.
- **Twilight**: linear in magnitudes at 0.6 mag per degree of solar altitude between -18° and -12°, anchored so it vanishes exactly at -18° (which is the definition of astronomical twilight: where scattered sunlight drops below the natural airglow). **Patat et al. 2006, A&A 455, 385**, "The twilight sky at ESO-Paranal", measure the full curve; the straight line is an approximation to it across the restricted span this pipeline allows imaging in, and is flagged as such in-source.
**Spectral treatment.** The sky is summed in two groups and integrated separately (§7.0), because its terms do not share a spectrum. Three of the four are sunlight scattered off something — the zodiacal dust cloud, the Moon, and the daytime atmosphere itself — so they genuinely carry the solar spectral shape (5772 K). Airglow does not: it is atmospheric line emission with no continuum shape this pipeline could integrate, so it is integrated flat and assumes nothing. Summing all four and integrating once would have forced one spectrum on all of them. The sky is also now subject to the same optical throughput and QE curve as the sources sitting on it, which it must be for a computed SNR to mean anything.

- **Cloud veiling**: a gain on the sky already present (×(1 + coverage·2.0) at full coverage) rather than an independent source, because cloud is lit from below by exactly that scattered light. The pipeline has no ground-light model to derive an absolute cloud brightness from, and this is the one term whose amplitude is not from a published measurement — it is marked as such in-source.

**Wavelength-dependent extinction** (`AtmosphericImagingNoise.ExtinctionMagPerAirmassAt`) replaced the single grey 0.20 mag/airmass coefficient, which made every filter of an LRGB set lose identical light. The *shape* is physics: Rayleigh optical depth `τ = 0.008569·λ⁻⁴·(1 + 0.0113λ⁻² + 0.00013λ⁻⁴)` with λ in μm (**Hansen & Travis 1974, Space Sci. Rev. 16, 527**; tabulated by **Bucholtz 1995, Appl. Opt. 34, 2765**), scaled by the air column over the site (`exp(-h/8000m)`), plus an aerosol term following the **Ångström (1929)** λ⁻¹·³ turbidity law. The aerosol *amplitude* is not invented either: it is whatever residual makes the total at Johnson V come out at the site's own measured coefficient, since aerosol loading is precisely the site-dependent part of extinction. Ozone's Chappuis band (~0.01-0.02 mag in the visible) is absorbed into that residual rather than modelled separately. Result at sea level: k_B 0.38, k_V 0.20, k_R 0.13 mag/airmass.

### 7.4 Solar-system-body observing forecast (`ExoInstrumentsGUI.ComputeBodyForecast`)

Separate from the generic heatmap (§5.4) — this one is **not** renormalized per refresh (a moving planet's best cell constantly enters/exits the visible window; renormalizing would recolor the whole map every tick instead of letting bands scroll). `Quality = (1/airmass²)·cloudTransmission`, an absolute [0,1] scale. Cloud coverage sampled once (current EVE reading) and applied uniformly to every future cell — a deliberate "clouds persist" approximation, since EVE has no forecastable weather model. Bodies use a `0°` geometric-horizon gate (matches the live camera's own capture gate), not the science-instrument `20°` floor.

### 7.5 Sensor chain — electrons, then ADU

**The pipeline carries electrons.** Every quantity from the calibrated signal plane to the converter is a real charge in electrons; nothing is normalised until the finished counts are divided by the ADC range for display. This replaced a pipeline that worked in *fractions of full well*, a normalisation that quietly made several effects unphysical: blooming spilled charge across an invented `1.0` threshold rather than a real well, CTI captured a fraction of a dimensionless number, the ADC did not exist at all, and the exported FITS carried display values with an `EGAIN` derived as `fullwell/65536` — a keyword that described nothing. In electrons each of those becomes the quantity a detector engineer would recognise, and the exported frame reduces like an observed one.

**Digitisation, and the two different saturation limits.** Charge is divided by the instrument's real conversion factor `K` (e⁻/ADU), truncated to an integer count, and clipped at the converter's top code `2^AdcBits − 1`. That makes the *digital* saturation `K·(2^bits − 1)` electrons — a limit distinct from the physical full well, and frequently the one that bites first. ESO's FORS2 manual states it outright: **"none of the CCDs will saturate before reaching the numerical truncation limits (65535 adu)"** (VLT-MAN-ESO-13100-1543). A pipeline expressed in fractions of full well cannot represent this at all, having only one ceiling and the wrong one.

| Instrument | Full well (e⁻) | K (e⁻/ADU) | ADC | ADC saturates at | Effective limit | Limited by |
|---|---|---|---|---|---|---|
| ASI294MM Pro (RedCat 51 / RC20 / CDK1000) | 66,000 | 4.029 | 14-bit | 66,000 e⁻ | 66,000 e⁻ | well *(exactly matched)* |
| FORS2 (MIT mosaic) | 150,000 | 1.25 | 16-bit | 81,919 e⁻ | **81,919 e⁻** | **ADC** |
| SPHERE/ZIMPOL | 640,000 | 10.5 | 16-bit | 688,118 e⁻ | 640,000 e⁻ | well |

Sourcing: FORS2's `K = 1.25 e⁻/ADU` is Table 2.8 of the ESO manual, MIT chip 1, **200 kHz readout — the imaging mode** (the 100 kHz / high-gain column is spectroscopic). ZIMPOL's `10.5 e⁻/ADU` is Schmid et al. 2018 Table 4; its bit depth is not stated there, but 16 is the only value consistent with it, since `10.5 × 65535 = 688,100 e⁻` sits just above the documented 640,000 e⁻ well, exactly how a matched CCD chain is specified (14 bits would truncate at 172,000 e⁻ and discard three quarters of the well). The ZWO camera publishes a 14-bit ADC and a 66,000 e⁻ well but no `K`, so `K = 66,000/16,383 = 4.029 e⁻/ADU` is **derived** from those two published numbers as the condition that the well fills the converter range at unity gain.

*Binning caveat, stated rather than hidden*: the digital ceiling is deliberately **not** scaled by binning, on the model that charge is summed on-chip ahead of one amplifier and one converter — true of a CCD. This makes a hard-binned sensor digitally saturation-limited (FORS2 at 4×4: a 2,400,000 e⁻ binned well against an unchanged 81,919 e⁻ converter, ADC-limited by a factor 29). CMOS sensors that bin by summing already-digitised values behave differently, and the ASI294MM's binned modes are not modelled to that level.

**Two corrections to FORS2's detector figures**, both from the current ESO manual against values previously in this file: read noise **1.89 → 3.8 e⁻** (Table 2.8, MIT chip 1, 200 kHz) and dark current **3.0 → 2.1 e⁻/px/h** (Table 2.9, MIT chip 1, −120 °C). The superseded numbers appear to come from an older manual revision.

- **Shot noise**: a genuine Poisson deviate on the electron count, not a Gaussian of matching width. Below a mean of 10 this uses Knuth's product method (exact); above it, the transformed rejection method **PTRS (Hörmann 1993, *Insurance: Mathematics and Economics* 12, 39)**, a rejection sampler whose accepted values are exactly Poisson distributed at cost independent of the mean — the same algorithm behind NumPy's `poisson`, and therefore behind GalSim's and Pyxel's shot noise. The factorial term uses `log Γ` by the **Lanczos (1964)** g=7, n=9 approximation. *Why it matters*: the previous `σ = sqrt(N)` Gaussian is only the Poisson distribution's width, and at the few electrons a faint sky or short dark reaches it is both the wrong distribution and unbounded below. *Why it was mandatory*: Knuth's method alone is O(λ) and needs `exp(−λ)`, so at the 150,000 e⁻ a real well holds it would run 150,000 iterations per pixel against an exponential that has already underflowed to zero, and never terminate. Verified over five decades of λ (5 → 150,000): sample mean and variance both reproduce λ.
- **Dark current**: the instrument's own measured rate × exposure × binned pixel area, in electrons, folded into the same Poisson draw as the signal and the sky — which is correct, since dark charge and photo-charge are the same counting process on the same well.
- **Read noise**: Gaussian σ = the instrument's real read-noise electrons, added in the **charge domain ahead of the converter**, which is where an output amplifier physically sits.
- **Hot/dead pixels**: fixed defect map, seeded once from a constant (`20260721`), same defects every session — 1-in-3000 hot, 1-in-6000 dead. Applied *after* all blur (a read-out-stage defect, not an optical one — shouldn't be softened by seeing/defocus/astigmatism blur the way real scene light is).
- **Full-well blooming**: Pyxel's own model only hard-clips (`min(pixel, fwc)`, no redistribution — verified against their source, `pyxel/models/charge_collection/full_well.py`), so this follows real CCD device physics instead: excess charge above the instrument's real binned full well **in electrons** spills along the column/shift-register direction, split symmetrically between the two vertical neighbors — a charge-conserving 50/50 split, the textbook default absent device-specific anti-blooming-gate data (**Janesick 2001, *Scientific Charge-Coupled Devices*, SPIE Press**). Cascades up to 4 iterations (a numerical convergence cap, not a physical quantity — same role as the 50-iteration cap on the Kepler-equation solver elsewhere in this codebase).
- **Charge-transfer smear / CTI**: simplified single-trap-species version of the `nc`/`nr` capture/release structure from **Short et al. (2010)**'s CDM model (Pyxel's `pyxel/models/charge_transfer/cdm.py`). Capture fraction (`1e-4`/row) is calibrated against real measured charge-transfer inefficiency: a fresh CCD sits near `1e-6`/transfer, while HST's ACS/WFC at severely radiation-damaged end-of-life reaches `~1e-4-1e-3`/transfer (**Massey, Stoughton & Rhodes 2010, PASP 122, 1035**) — `1e-4` sits at that damaged-device ceiling, the conservative end of the real range for a healthy amateur sensor. Release fraction (`35%`/row) represents the fast-trap species real CDM models always include alongside slow traps — a trap whose release time is comparable to the transfer period empties within the first few pixels, the short visible trail seen below bright sources in real frames.
- **Cosmic ray hits**: flat Poisson process, isotropic incidence angle, random 2–14px track length, deposits a bright streak. Rate is now **per instrument**. FORS2 and SPHERE use ESO's own *measured* rate on the MIT mosaic at Paranal, **7.7 events min⁻¹ cm⁻²** (FORS2 manual Table 2.9) — nearly eight times the sea-level flux, which is what 2.6 km of altitude does to the cosmic-ray rate, and a good illustration of why one global constant was wrong. The ZWO-camera instruments keep the sea-level muon flux ≈ 1 cm⁻² min⁻¹ (**Particle Data Group, Cosmic Rays review**; **Grieder 2001, *Cosmic Rays at Earth***), flagged in-source as a floor rather than a measurement since their sites sit at 650 m and 1712 m and no rate is published for them. Applied to the active telescope's own real, native, binning-independent physical silicon area (the exposed area doesn't change with pixel binning, only how physical pixels are grouped on readout) — a property, not a cached value, since a telescope switch with a different sensor must recompute it rather than keep the previous instrument's rate. RC20 example: side X `= 4144×4.63e-4 cm = 1.919 cm`, side Y `= 2822×4.63e-4 cm = 1.307 cm`, area `= 2.507 cm²`, rate `= 1 cm⁻²min⁻¹ × 2.507 cm² / 60s ≈ 0.0418 hits/s`. Pyxel's own CosmiX/TARS angle model is an unimplemented stub in their shipped source, so isotropic sampling here is no less physical than upstream.
- **Persistence/ghosting: removed.** An earlier version ported Pyxel's default persistence trap model (time constants and species proportions in the spirit of **Fixsen, Offenberg, Hanisch et al. 2000, PASP 112, 1350**), but that model is tuned for HgCdTe near-infrared detectors, a technology known for pronounced image latency — not the ASI294MM Pro's actual sensor, a back-illuminated Sony CMOS (IMX492), a technology whose main advantage over CCD/IR arrays is negligible image lag. There is no published persistence measurement for this real device to source a correct trap-capacity fraction from, and the ported proportions summed to exactly 1.0 (a Pyxel-internal weighting for decomposing an IR array's ghost signal among trap species), which is not a bound on the fraction of the *current* frame that gets subtracted. Treating them as such produced literal runaway darkening: repeated same-target exposures fed the slower (τ=100–10000s) species enough elapsed real-world time between manual shots to keep approaching their share of the signal on every subsequent capture, with no equilibrium below near-total signal loss. Removed rather than re-tuned, since no real number exists to re-tune it to.
- **Gain**: sets the conversion factor actually used, `K(g) = K₁/g` — a higher gain means fewer electrons per count, which is what an amplifier ahead of a fixed converter does. Continuously player-adjustable on the ZWO-camera instruments (real EGain range); fixed at 1.0 on FORS2/SPHERE, whose gain is a hardware conversion factor and not an ISO-like control.
- **FITS export is now genuinely calibratable**: the file contains the detector's own ADU counts, unaltered, with `EGAIN` = the real `K`, plus `FULLWELL`, `ADCBITS`, `SATURATE` and `BUNIT='adu'`. Previously it wrote a normalised display frame rescaled to 65535 alongside an `EGAIN` computed as `fullwell/65536`, so the counts had been through a stretch and a renormalisation that no keyword described. The stacked LRGB composite is a *processed* product and is written with `BUNIT=''`, no `EGAIN`, and a `HISTORY` card saying so — quoting a conversion factor for it would be a lie.
- **Diurnal drift**: without autoguiding, the sky rotates under a fixed instrument during the exposure and every source draws a streak. This is no longer a horizontal-only smear of the finished image: the sky's own rotation is applied to the frame geometry (§7.14), so trails run in the true direction for the observatory's latitude and hour angle, they curve, and field rotation makes stars near the frame edge trail further than those near its centre. The rendered scene is smeared along the same vector by a flux-conserving sliding-window sum over rasterised lines (O(pixels) regardless of trail length; light that runs off an edge is gone rather than clamped back in, since a body drifting out of frame really does leave). Zero on FORS2/SPHERE, where autoguiding is forced on (§7.011). Note that on a world with a 6 h day the sky sweeps four times faster than Earth's: even a 1 s unguided sub trails ~54 px at the RC20's binned plate scale.

**Explicitly rejected**: GalSim's brighter-fatter effect (`SiliconSensor`) — its real formula needs per-sensor electrostatic-vertex calibration tables (e2v/ITL-specific) with no generic published values, and none of these instruments do stellar photometry (their targets are extended solar-system bodies, not point-source stars), so the effect's main visual payoff (saturated star-core broadening) barely applies to any of them.

### 7.6 Image stacking (`Visualization/AstroImageStack.cs`)

- Per-filter stacks of up to 30 subs, centroid-based alignment (brightness-weighted, falls back to frame center if nothing exceeds threshold), robust sky-background subtraction (trimmed-median of a 20px border band, trims brightest 15% first to reject limb/hot-pixel contamination).
- **Cosmetic correction (bad-pixel-map)**: every sub is corrected *before* alignment using the sensor's known, fixed hot/dead pixel map — each defect pixel is replaced by the mean of its immediate orthogonal neighbors (excluding any neighbor that's itself a known defect). This is the standard professional calibration step real pipelines run before registration/stacking (PixInsight's `CosmeticCorrection` process, IRAF/ccdproc's `fixpix`, ESO Reflex bad-pixel handling). Doing it before alignment matters: a fixed sensor defect co-added with per-sub alignment shifts would otherwise scatter into a cloud of artifacts at different composite positions instead of being corrected once at its one true location.
- **LRGB composition**: luminance transfer — `R/G/B *= min(4.0, L_stack/rgbLuminance)` — capped to stop noise blow-up at near-zero background. Optional Hα boost into the red channel.
- **Display-only asinh stretch** (never applied to stored data): `arcsinh(k·v)/arcsinh(k)`, `k=5`.
- **Lucky imaging**: each filter's subs ranked by a **variance-of-Laplacian sharpness score** (**Pech-Pacheco et al. 2000**, "Diatom autofocusing in brightfield microscopy" — the top-performing general-purpose focus operator in the **Pertuz, Puig & Garcia 2013** survey), computed over the central 60% of the frame (mirroring AutoStakkert!'s alignment-point "quality box" concept for real planetary lucky imaging, since the RC20 always centers its aim there) with the sharpest-magnitude 2% of Laplacian values trimmed before the variance is taken (robust against an isolated cosmic-ray hit or hot pixel masquerading as a sharp frame — the same trimmed-statistic idiom the background estimator already uses). Only the sharpest 30% of subs are kept before alignment/averaging (mid-range of the 1–60% practical range in the lucky-imaging literature — **Fried 1978** for the underlying theory, **Baldwin et al. 2001** for practical frame-selection fractions). Always forces alignment on when active.
  - *Note on the prior implementation*: an earlier version scored sharpness by raw peak-pixel value. Since blooming, cosmic rays, and hot pixels can all saturate a single pixel anywhere in the frame regardless of actual seeing, that metric was inadvertently selecting artifact-contaminated frames as "sharpest" — corrected to variance-of-Laplacian, which measures genuine local contrast rather than any single pixel's value.

### 7.7 Real FITS export (`Visualization/FitsWriter.cs`, `Core/FitsWcs.cs`)

#### 7.7.1 World coordinate system

The pipeline already performed the exact gnomonic projection FITS calls TAN, built from the camera's own three axes (§7.14), and already knew the boresight's RA/Dec, because that is how the star catalogue is searched. **None of it reached the exported file.** A FITS frame without a WCS is a picture: it opens, and nothing can say where it points, so it cannot be plate-solved, cross-matched against a catalogue, stacked by coordinate, or handed to astropy, DS9, SExtractor or photutils for anything positional. The information existed and was discarded at the last step.

Written per **Calabretta & Greisen (2002, A&A 395, 1077)**, with **Greisen & Calabretta (2002, A&A 395, 1061)** for the keyword conventions: `CTYPE1/2 = 'RA---TAN'/'DEC--TAN'`, `CRVAL1/2`, `CRPIX1/2`, `CD1_1..CD2_2`, plus `CUNIT1/2`, `EQUINOX`, `RADESYS`, `SECPIX1/2` and sexagesimal `OBJCTRA`/`OBJCTDEC`.

**The CD matrix is measured from the projection, not re-derived.** Computing it afresh from the focal length and the observatory's latitude would be a second, independent implementation of the geometry, free to disagree with the one that actually placed the stars — and a WCS that disagrees with its own image is worse than none. Instead: step a small distance east and north of the boresight *in the tangent plane* (not in right ascension, which divides by cos δ and loses all meaning at the pole), ask the same `GnomonicProjection` where those directions land, and invert the 2×2 Jacobian. Whatever orientation, handedness or field rotation the camera had is captured automatically, because the same object answers both questions.

Two subtleties, both of which were real defects caught by the harness:

- **The half-pixel convention.** The renderer's pixel `i` spans `[i, i+1)` and is centred at `i+0.5` (`StarFieldRenderer.Splat`); FITS puts pixel centres on integers starting at 1. `CRPIX` carries the offset. Getting this wrong puts every plate solve half a pixel out and is invisible.
- **The Jacobian step size.** The usual finite-difference tension does not apply, because the map being differentiated is *exactly* linear — `ξ = (d·right)/(d·boresight)` then an affine scaling — so any step returns the same Jacobian up to rounding, and a larger step is strictly better. The step is set by the one place precision bites: recovering a declination from a direction vector goes through `asin(z)` with `z` within 10⁻¹⁴ of 1 at the pole, where the derivative is of order 10⁷ and double precision buys only ~3×10⁻⁸ degrees. At a 10⁻⁵° step that is a 0.3% scale error, and it showed up exactly there — a field centred on the celestial pole came back with its plate scale 0.44% wrong while every other pointing was right to 3×10⁻⁶. At 10⁻³° the pole is as accurate as anywhere else.

**Verified by round trip** (`tools/bandpass-wcs-tests`): deproject the frame's centre and four corners through an *independently written* inverse TAN (the textbook relations, not a rearrangement of `FitsWcs`), then ask the pipeline's own projection where that direction lands. It closes to **5×10⁻⁹ arcsec** across the whole sensor. A field centred exactly on the pole is checked separately.

**Two deliberate omissions.**

- **The stacked composite gets no WCS at all.** Every sub is registered on the target body's own centroid (§7.6), and the body moves against the stars between subs, so no single pointing describes the stack: the planet is aligned and the field around it is not. Writing the last sub's WCS would hand a plate solve or a cross-match a pointing wrong by however far the body travelled, silently. Same standard as the omitted `EGAIN` on that product.
- **An unguided frame's WCS describes the exposure's start**, matching `DATE-OBS`, since the sky turns during the exposure and a single WCS can only describe one instant of it. A `HISTORY` card says so outright, because a plate solve of a trailed frame will not converge and the reason belongs in the file rather than being inferred.

The RA/Dec zero point remains the arbitrary convention of §1/§12.1 on stock. The WCS is internally exact and self-consistent — a source at a given catalogue position lands where the header says — but on stock the frame is not tied to the real sky.

#### 7.7.2 Instrument, site and conditions

`TELESCOP`, `INSTRUME`, `OBSERVAT`, `IMAGETYP`, `SITELAT`/`SITELONG`/`SITEELEV`, `APTDIA`, `XBINNING`/`YBINNING`, `RDNOISE`, `DARKCURR`, `CCD-TEMP` — the FITS standard's own reserved names where they exist, and the SBIG-derived vocabulary SharpCap, NINA, MaxIm DL and PixInsight all read where they do not. `Name` goes to `TELESCOP` and `CameraName` to `INSTRUME`, which is the distinction those two keywords are for and which this roster genuinely needs: one ZWO camera is shared between three tubes, exactly as amateur astrophotography works.

Conditions are recorded because they are **irrecoverable**: a reduction can re-derive a plate scale from the WCS, but nothing downstream can reconstruct the airmass, seeing or sky brightness a particular exposure was taken through. `AIRMASS`, `SEEING`, `DIFFLIM`, `SKYMAG` (in the V mag/arcsec² the sky model publishes in), `WAVELNTH`, `BANDWID`.

`SEEING` and `DIFFLIM` are written as **two** keywords rather than one "delivered FWHM", because the pipeline knows those two and combining them would need an addition rule exact for neither an Airy pattern nor a Kolmogorov profile — the same reason `OpticalPsf` solves for the atmospheric residual by bisection instead of subtracting in quadrature (§7.013).

Finally, `THROUGHP` (the grey optical throughput) and `PHOTWIDT` (the effective photometric width for a flat spectrum, §7.0) make the frame's photometry **reproducible from the header alone**: with those, the aperture area and `EXPTIME`, a reader can recompute what electron count any apparent magnitude should have produced instead of taking the frame on trust.

*Standards fix made alongside*: `HISTORY` was being written as a value card (`HISTORY = 'text'`). The standard gives commentary keywords the name in columns 1-8 and free text from column 9, with **no `= ` value indicator**; a conforming parser rejects or mangles the value form.

#### 7.7.3 Data and formatting

"Save Photo"/"Save composite" now write a real 16-bit FITS file alongside the PNG preview — the actual format a real telescope+camera setup would produce, not a proprietary/simplified one. Standards-conformant: 80-byte header cards, 2880-byte block padding (header and data), big-endian data regardless of host byte order, and the standard `BZERO=32768`/`BSCALE=1` convention for representing unsigned 16-bit data in FITS's native signed-16-bit (`BITPIX=16`) format. Header keywords match real acquisition-software conventions (SharpCap/NINA/MaximDL): `EXPTIME`, `XPIXSZ`/`YPIXSZ` (real binned pixel pitch), `EGAIN` (the real conversion factor K, §7.5), `FOCALLEN`, `GAIN`, `FILTER`, `OBJECT`, `DATE-OBS` — all sourced live from the active telescope's own real spec (§7.00), so a FORS2 or SPHERE frame carries that instrument's own real focal length and full well, not the RC20's.

**Precision bug fixed**: both save paths originally read pixel data back from an 8-bit `RGB24` `Texture2D` (`CapturedPhoto`/`stackedCompositeTexture`) via `GetPixels()`. Since real per-pixel noise (shot/dark/read noise) lives at a fraction of full well far below `1/255`, that 8-bit round-trip silently crushed nearly all of it to exactly 0 before it ever reached the "16-bit" FITS file — a raw single-sub frame looked implausibly smooth (a rendered-looking terrain gradient, flat black background with only isolated hot-pixel/cosmic-ray outliers, no visible grain), because the noise generated by the physics pipeline never survived being displayed and saved. Both save paths now source pixel data from the full-precision `Color[]`/`float[]` computed by the physics pipeline directly (`SolarSystemCameraTexture.GetLastCaptureFullPrecision()` for single shots, a cached full-precision composite array from `AstroImageStack.ComposeLRGB` for stacks) — the 8-bit textures remain, display-only, for the in-game preview.

---

## 8. Visualization layer

### 8.1 Sky chart (`Core/SkyChartTexture.cs`)

Zenith-centered planisphere: `r = Rmax·(90-alt)/90` (linear zenith-distance projection, not true stereographic/gnomonic), `x=cx+r·sinAz`, `y=cy+r·cosAz` (north up). Marker brightness ramps from mag -1.5 (Sirius-bright, full) to mag 12 (display floor, 16% minimum) via linear interpolation on magnitude. Star color from `StellarColor.BlackbodyRgb` alpha-blended toward background by brightness; non-highlighted stars desaturated 55% toward gray and dimmed 40% during an active search. Reference altitude rings at 0/20/40/60°, cardinal cross overlay. Both stars and solar-system bodies share the same `0°` horizon gate, matching the live camera's own capture gate.

**Body-marker decluttering** (`ExoInstrumentsGUI.DeclutterBodyPositions`, called from `BuildChartBodyPoints`): a moon and its parent planet routinely project to nearly the same screen point in KSP's compressed-scale solar system, stacking their dots and making them impossible to click apart. Overlapping markers (detected via the *unzoomed* raw projection, so the fix holds at any zoom level: the projection scales linearly with zoom, so a separation wide enough to click at zoom=1 only gets easier at higher zoom) are grouped by a union-find over disc-overlap distance, then arranged evenly on a small circle around their shared position, sized so adjacent markers clear each other's click radius (target spacing 18px at zoom=1, capped at a 40px ring radius for a large cluster). The adjustment is baked into real alt/az via `SkyChartTexture.UnprojectRawPixel` (the inverse of the forward projection), so the rendered dot and the click hit-test always agree — every other use of a body's real position (capture aim, tracking, physics) is untouched.

### 8.2 Light curve / RV curve textures (`Visualization/LightCurveTexture.cs`, `RvCurveTexture.cs`)

Both render raw (time-series) and phase-folded scatter plots with error bars. Phase-fold bin uncertainty is **formal error propagation** (`sqrt(Σσᵢ²)/n`, standard error of the mean assuming Gaussian uncorrelated per-sample noise), not empirical bin scatter — worth flagging as a modeling assumption. Transit uses a fixed 100 bins (valid for its hundreds-to-thousands-of-points cadences); RV uses an *adaptive* bin count (`clamp(N/3, 8, 100)`) since RV's much sparser 6–8h-cadence sessions would otherwise spread ~0.3 points/bin across a fixed 100 and get filtered to a blank plot — with a raw-scatter fallback if even the adaptive floor comes up empty.

### 8.3 Forecast texture — see §5.4 and §7.4.

### 8.4 GUI structure (`ExoInstrumentsGUI.cs`, ~4150 lines)

Single IMGUI window, two-column layout. Left column: star-chart/target selection, or an active-session summary card. Right column: dispatches by which session object is non-null (`session`=transit, `rvSession`, `imagingSession`) or `photographySessionActive`, each with its own plot/report panel (`DrawTransitObservation`+`DrawTtvSection`, `DrawRvObservation`+`DrawRmSchedulingLine`+`DrawRmSection`, `DrawImagingObservation`+`DrawImagingFrame`, `DrawSolarSystemCameraView`+`DrawCameraControls`+`DrawStackingControls`). Fog-of-war (career mode) adds `DrawHiddenTargetInfoCard`/`DrawDecoyInfoCard`/`DrawCareerScanOutcome`, gated on KSP's stock game-mode flag rather than a bespoke mod setting. Forecast heatmaps (`DrawForecastPanel`, `DrawPhotographyForecastPanel`) are separate panels feeding off §5.4/§7.4 respectively.

---

## 9. Instrument roster & career economy

### 9.1 Full stat table (`Core/Observatories.cs`)

All `PrecisionExponent = 0.2`. Career-economy fields (unlock cost, science threshold, scan cost, reward multiplier) are **explicitly marked PLACEHOLDER in the source** — "balance à valider avec Baptiste"; only their *relative ordering* (bigger investment → bigger payoff) is a real constraint, not the absolute numbers.

| Instrument | Method | RefMag | RefPrecision | Cadence | Aperture | Site alt | Unlock Funds | Sci threshold | Scan cost | Reward ×
|---|---|---|---|---|---|---|---|---|---|---|
| SPECULOOS | Transit | 9.5 | 150 ppm | 30s | 1.0m | 2490m (Paranal) | 0 (default) | 0 | 500 | 1.0
| WASP | Transit | 9.5 | 1000 ppm | 600s | 0.111m | 2400m (La Palma) | 10,000 | 0 | 250 | 1.0
| TESS | Transit | 10.0 | 1095 ppm | 120s | space-based | — | 300,000 | 100 | 2,500 | 2.0
| HARPS | RV | 9.5 | 1.0 m/s | 6h | 3.6m | 2400m (La Silla) | 200,000 | 120 | 3,500 | 2.5
| ESPRESSO | RV | 8.0 | 0.15 m/s | 8h | 8.2m | 2635m (Paranal) | 900,000 | 400 | 8,000 | 4.0
| SOPHIE | RV | 8.0 | 2.0 m/s | 6h | 1.93m | 650m (OHP) | 60,000 | 30 | 1,500 | 1.5
| ELT | DirectImaging | 6.0 | 1e-4 contrast | 3600s | 39.3m | 3046m (Cerro Armazones) | 4,000,000 | 900 | 25,000 | 6.0
| RedCat51 | SolarSystemPhotography | n/a | n/a | n/a | 0.051m | 650m (OHP) | 0 (default) | 0 | 20 | 0.0 (no science economy)
| RC20 | SolarSystemPhotography | n/a | n/a | n/a | 0.51m | 650m (OHP) | 15,000 | 5 | 50 | 0.0 (no science economy)
| CDK1000 | SolarSystemPhotography | n/a | n/a | n/a | 1.0m | 1712m (Palomar) | 60,000 | 20 | 120 | 0.0 (no science economy)
| VLT FORS2 | SolarSystemPhotography | n/a | n/a | n/a | 8.2m | 2635m (Paranal, UT1) | 250,000 | 80 | 400 | 0.0 (no science economy)
| VLT SPHERE | SolarSystemPhotography | n/a | n/a | n/a | 8.2m | 2635m (Paranal, UT3) | 300,000 | 100 | 450 | 0.0 (no science economy)

Citations: SPECULOOS — Gillon et al. 2018. WASP — Pollacco et al. 2006. TESS — Ricker et al. 2015. HARPS — Mayor et al. 2003. ESPRESSO — Pepe et al. 2021. SOPHIE — Perruchot et al. 2008; Bouchy et al. 2009. ELT — Gilmozzi & Spyromilio 2007 (telescope), Kasper et al. 2021 (contrast). RC20 — PlaneWave Instruments RC20 (commercial 20" astrograph). CDK1000 — PlaneWave Instruments CDK1000/PW1000 product page; a real unit installed at Palomar Observatory in 2024 for MIT's WINTER project. VLT FORS2 — eso.org FORS2 User Manual and Standard Filters page; Wittman et al. 1998 and Cuillandre et al. 1999 for the shared MIT/LL CCID20 chip. VLT SPHERE — Schmid et al. 2018, *A&A* 619, A9 ("SPHERE/ZIMPOL high resolution polarimetric imager. I.").

CDK1000/VLT FORS2/VLT SPHERE unlock economy fields are placeholders in the same sense and to the same degree as every other career-economy number in this table — only their relative ordering (each a real optical/physical step up over the one before, priced accordingly) is a design constraint.

### 9.2 Science economy (`Core/ScienceRewards.cs`) — ALL PLACEHOLDER

- First scan of any star (any outcome, including decoys): **5** Science.
- Confirmed real detection (one-time per host, on top of first-scan): **40** Science, × instrument's `ScienceRewardMultiplier`.
- Stellar characterization (measurable Teff via imaging, even without a planet): **10** Science flat.
- TTV detection: **25** Science flat. RM measurement: **30** Science flat.
- Multi-planet jackpot: `+0.5×base` per extra planet, additive not compounded (3 planets → `(1+2×0.5)×base`, not `base³`).

Source header: "Shape is fixed: any scan pays a little, a real detection pays more, nothing pays twice" — the *shape* is a design decision, the *numbers* are unvalidated.

### 9.3 Save persistence (`ExoInstrumentsScenario.cs`)

Fog-of-war and unlock state as `HashSet<string>` "claim once" gates, keyed by `StarTarget.CatalogKey` (never object reference or list index — the catalog rebuilds from CSV every launch). `TotalScienceEarned` is a mod-internal counter deliberately decoupled from KSP's stock R&D Science balance ("using the stock balance as the unlock gate would let spending Science on unrelated parts lock the player back out of an instrument they'd already earned the right to buy").

---

## 10. Session / campaign mechanics

- **`Session/ObservationSession.cs`** (transit): ticks forward in UT, samples `LightCurveSimulator.GenerateSystemFluxAtTime` at instrument cadence when observable, sub-cadence-searches (`max(60s, cadence/8)`) through unobservable daytime stretches. Capped at 20,000 steps/tick to avoid stalling on long time-warp jumps (leftover work carries to next frame). Space-based (TESS) always observes; ground instruments get the full day/night/altitude/moon gate. Note: on Kerbin's 6h day, sampling isn't rigidly gridded — a slot landing in daytime slides forward in sub-cadence steps so a cadence commensurate with the day length can't lock every slot into daylight.
- **`Session/RvObservationSession.cs`**: same architecture, plus a "Rossiter-McLaughlin burst" mode — cadence drops to 600s (10min) inside a window spanning mid-transit ± one full duration for designated planets, and the per-tick step is clamped so an 8h cadence can never leap over and skip an entire 3h transit window.
- **`Session/ImagingObservationSession.cs`**: doesn't accumulate discrete samples — accumulates `EffectiveExposureSeconds = ∫(1/airmass²)dt` over observable intervals (midpoint-rule numeric integration, 120s steps — chosen so twilight-window edges are mislocated by at most ~2min on Kerbin's fast 6h day). Same integrator drives both retrospective accounting and forward ETA prediction (`PredictUtForEffectiveExposure`, `PredictNextObservableUt`), since conditions are a pure deterministic function of UT (no stochastic weather in the generic forecast).

---

## 11. Cross-cutting integrations

- **BetterTimeWarpContinued** (soft dependency, `BetterTimeWarpIntegration.cs`): reflectively swaps in the fastest non-physics warp-rate table for scheduled warps longer than 10 Kerbin days (216,000s), restores the player's original table afterward (waits for the warp to actually ramp up off index 0 before allowing restore, so it doesn't undo the swap prematurely). Falls back to stock behavior untouched if not installed or if reflection fails (permanently disables itself for the session on any exception).
- **EVE-Redux** — see §7.2.
- **Observatory facility** (`ExoObservatoryFacility.cs`): a real, upgradeable KSC building rather than a scenery placeholder, built on the same stock facility systems (`Upgradeables.UpgradeableFacility`, `SpaceCenterBuilding`) every stock building at the KSC uses. The one piece of actual physics involved: its telescope model is continuously aimed at whatever target is currently tracked, using the same real altitude/azimuth conversion already used elsewhere in the mod (`ExoInstrumentsGUI.TryComputeBodyAltAz`), so the rig's orientation reflects the target's real position in Kerbin's sky rather than a scripted animation. Placement, model rigging, and pivot calibration are asset/engineering concerns, not modeled physics, and are out of scope for this reference.

---

## 12. Consolidated list of deliberate simplifications

*(Every one of these is worth a line in a paper's Methods/Limitations section — collected here so nothing gets missed.)*

1. **No real physical link between the star catalog's RA/Dec and the home world's sky** — an arbitrary zero-point convention, not real astrometry (`SkyCoordinates.cs`). The observer's *position* and the body's *rotation rate* are real and read from the game (§1.1), so a pack modelling the real solar system gets a real sidereal rate and a real latitude; only the zero point stays a convention, and no agreement with any particular skybox replacement is claimed.
2. **No weather simulation** in the generic (exoplanet-instrument) observing forecast — only the solar-system-photography forecast (any of the four instruments, §7.4) factors in real EVE cloud cover, and even that assumes clouds persist unchanged into the future (no forecastable weather model exists to query).
3. **`PrecisionExponent = 0.2` uniform across every instrument** — real magnitude-precision scaling varies by instrument/detector; this is one simplified relation for all.
4. **Career-economy numbers (unlock cost, science threshold, scan cost, reward multiplier, all of `ScienceRewards.cs`) are explicitly unvalidated placeholders** pending playtesting — only their relative ordering is a real design constraint.
5. **Kerbin has no axial tilt** — the Sun's declination is fixed at 0°, so there are no seasons and no day-length variation.
6. **Single-harmonic RV fit underestimates semi-amplitude on eccentric orbits** (real power leaks into higher harmonics) — period recovery stays accurate, amplitude runs low.
7. **BLS transit search has no false-alarm probability calibration** — SNR is relative confidence only, not a statistically calibrated detection significance.
8. **TTV/RM models are order-of-magnitude, single-dominant-perturber approximations** — only the strongest near-resonant pair is modeled; higher-order and secular effects are absent.
9. **The direct-imaging frame's speckle and background are uniform pseudo-noise**, not physically-derived photon statistics. Each pixel's speckle term is a uniform deviate on `[0,1)` scaled by twice the local contrast floor, so it has the wrong distribution, a non-zero mean, and no photon noise on top. Real AO speckle intensity follows a modified Rician (**Aime & Soummer 2004**; **Soummer et al. 2007**) tending to a Gamma distribution as an exposure averages independent realisations, with the number of those set by the AO decorrelation time. Closing this needs that decorrelation time sourced for a real instrument, and is tracked as roadmap item 10 rather than approximated here.
   *(The optics half of this entry is closed. The frame now computes the diffraction pattern of the real ELT pupil, rings and spikes together, with no free parameter — §7.111, §7.112. What remains is the noise.)*
   - **9f.** **SPHERE/ZIMPOL's filters are still top-hats** of their published FWHM and peak, while FORS2's three broadband filters now carry ESO's measured curves. Not a modelling choice: the ZIMPOL curves were simply not located, and the top-hat is the correct treatment until they are (§7.02).
   - **9d.** The **RC20 and CDK1000 have real spider vanes that are not modelled**, because PlaneWave publishes no vane width and spike brightness scales as the vane area squared: guessing the width would be guessing the effect. Both are set to zero vanes and declared, the same treatment the CDK1000's astigmatism already gets (§7.113).
   - **9e.** The **VLT vane count of four is read from the telescope's structure, not quoted**: ESO's technical prose describes "metallic beams called spiders" without giving a number. The vane *width* is on firmer ground, being derived from published scaled pupil masks by a scaling that reproduces the ELT's independently published figure to 4 % (§7.113).
   - **9a.** The vane **width** is a literature value with real spread: 50 cm per Schwartz et al. (2018), against 54 cm in METIS phase D simulations and 40 cm in at least one published pupil figure. Spike brightness scales as the vane area squared, so that spread is a factor 1.8 on an effect of order 1e-4 of peak (§7.112).
   - **9b.** The profile's average over a pixel's azimuthal extent is exact within 6 px of the source and taken in its narrow-angle limit beyond, agreeing with a brute-force average over a real square pixel to **6.7×10⁻⁴ of peak intensity** across every plate scale the display produces (§7.111).
10. **Every solar-system-photography instrument's sensor noise chain is anchored to real electron counts** (a real full well, read noise, and dark current, and a real photon-flux-calibrated signal — §7.0/§7.5), not abstract units — the remaining unanchored constant per instrument is astigmatism's pixel amplitude at the frame corner where a nonzero value is used (RC20 only; no published optical prescription specifies it to the needed precision), flagged individually in §7.1. (Optical throughput, absent entirely when this line was written, is now modelled where published and enumerated where not — see §7.001 and items 31-33 below.)
11. **CTI is a simplified single-trap-species model**; Pyxel's own real CDM (which this is adapted from) uses full SRH capture physics in real electron counts across multiple trap species.
12. **Cosmic ray incidence angle is isotropic-sampled**, not derived from a real particle angular-distribution model (matches the fact that Pyxel's own shipped angle model is an unimplemented stub) — though the *rate* is now a real derived quantity (sea-level muon flux over a cited real sensor's pixel area, see §7.5).
13. **Zodiacal light is a fixed baseline constant**, not position/season-dependent (no real ecliptic geometry exists for Kerbol in this mod) — though its *magnitude relative to airglow* is now derived from two real cited surface-brightness measurements via the Pogson relation, not independently invented.
14. **Habitable-zone polynomial fits are only valid 2600K–7200K** — no extrapolation outside that range (returns null instead).
15. **BSC5-derived decoy stars carry no mass/radius/distance data** (pre-Hipparcos catalog, no such columns) — always treated as "unknown," never invented.
16. **Transit duration (T14) assumes a circular orbit** — no eccentricity term, despite eccentricity being tracked elsewhere in the same star's data.
17. **All positional/name catalog matching uses small-angle and string-heuristic approximations** — ambiguous matches are refused (flagged for review) rather than guessed, but the matching itself isn't a rigorous spherical-trigonometry/fuzzy-matching pipeline.
18. **Deterministic hash-based "randomness"** for stellar activity level, rotation period, spot phase, RM spin-orbit angle, and direct-imaging pointing/position-angle — reproducible per star, not drawn from any measured distribution beyond the *range* the draw is confined to.
19. **The solar-system astrograph's brighter-fatter effect was deliberately not implemented** — GalSim's real model needs sensor-specific electrostatic calibration tables with no generic published values, and the effect's main payoff (point-source core broadening) barely applies to any of these instruments' extended solar-system targets anyway.
20. **The solar-system astrograph's lucky-imaging sharpness scoring window (60% central region) and outlier-trim fraction (2%) are algorithmic engineering choices**, not measured quantities — the *operator itself* (variance of Laplacian) and the *keep fraction* (30%, literature range 1–60%) are literature-sourced, same distinction as other robust-statistics parameters already in the codebase (e.g. the sky-background trim fraction).
21. **The real photon-flux magnitude model (§7.0) treats a KSP `CelestialBody.albedo` as a rigorous geometric albedo** in the H-G planetary-magnitude sense, and its `.Radius` as a perfect sphere — the best available real input data from the game itself, but not a measured planetary albedo in the astronomical sense.
22. **The Lambertian phase law (Russell 1916) assumes a perfectly diffuse, uniform-albedo sphere** — real solar-system bodies (especially airless, cratered ones) show real deviations from this (opposition surge, limb darkening/brightening) that this mod doesn't model.
23. **SPHERE's adaptive-optics resolution (§7.013) is applied as a fixed real "good conditions" FWHM regardless of current airmass** — the cited papers report a range (25 mas headline, 22-28 mas across bands), and real AO correction does degrade somewhat at higher airmass, but no published relation exists to model that degradation, so the headline figure is used unconditionally rather than left unmodeled or invented.
24. **Real on-chip/charge-domain pixel binning is assumed** for the binned-full-well scaling in §7.05 — the real capacity gain from combining N×N physical pixels depends on where in the readout chain the summation happens (charge-domain vs. digital), and this mod doesn't distinguish between the two for any of its four instruments.
25. **Telescope-switch exposure rescaling (§7.011) assumes the player wants equivalent total collected light**, the same assumption a real exposure-time calculator makes — it does not know the imaged body's actual real-time brightness, so a target that's genuinely much brighter/fainter than whatever the previous instrument was pointed at can still land outside a comfortable exposure after rescaling.
26. **VLT SPHERE's Blue filter slot is omitted rather than approximated** (§7.012) — ZIMPOL genuinely has no real broadband blue filter, so rather than reuse a nearby real filter's numbers under a misleading label, that filter position simply isn't offered for this instrument.
27. **Solar-system body marker decluttering (§8.1) is a chart-display convenience, not real astrometry** — the small on-screen separation it introduces between an overlapping planet and its moons has no physical meaning and is never used by anything outside chart rendering and click hit-testing.

28. **No rendered star field ships at all** — the catalogue is user-built from Gaia DR3 (§7.016), because the useful depths cannot be distributed (202 MB at G<13, 1.9 GB at G<16). A player who installs nothing photographs an empty sky. Giving them a plausible field at zero download needs a statistical population from a Galactic star-count model — a real 30 s RC20 sub reaches several magnitudes fainter, so a frame holds about five real stars where a real one would hold hundreds. Closing this needs a Galactic star-count model generating a statistical faint population (Bahcall & Soneira 1980; Besançon; TRILEGAL), not a larger catalogue (§7.14).
29. **Cloud veiling is a gain on the existing sky (×3 at full coverage), not an absolute cloud brightness** — the pipeline has no ground-light model to derive one from, and scaling the term whose light the cloud is actually reflecting is preferable to inventing an absolute figure (§7.3).
30. **The twilight brightness gradient (0.6 mag per degree of solar altitude) is a straight-line approximation** to Patat et al. (2006)'s measured curve over the -18° to -12° span, not a fit to it (§7.3).
31. **Optical throughput is modelled only where a figure is published**, and the unmodelled factors are enumerated per instrument in §7.001's table rather than lumped into one fudge: the RedCat 51's whole refractive glass path, every instrument's relay/corrector/Barlow train, FORS2's collimator+camera relay, and every broadband filter's peak transmission except FORS2's Hα. Each of those sits at 1.0, which means *not modelled*, not lossless — so the limiting magnitudes remain optimistic, by less than before but not by zero.
32. **Mirror reflectivity is a single grey band-averaged figure (0.87)** applied to every instrument alike, because the source (Ma & Cai; Magrath 1997) quotes it as a band average over the full optical range. Real coatings have a wavelength-dependent curve and a real observatory's mirrors sit at a definite point in their own re-coating cycle; neither is resolved here. PlaneWave's optional enhanced coatings are not modelled at all, no measured curve being published for them.
33. **No published QE curve exists for the ZWO ASI294MM Pro**, so the RedCat 51 / RC20 / CDK1000 use its 90% peak flat across every band, which overstates each filter away from the peak. A measured curve for a sibling Sony back-illuminated CMOS exists (Alarcón et al. 2023) but normalising another chip's shape to this one's peak would be an assumption presented as a measurement (§7.001).
34. **Filter passbands are top-hats of the published FWHM at the published central wavelength**, scaled by the published peak transmission where one exists. The equivalent width is therefore exactly the published figure, and no unmeasured profile shape is imposed — but a real interference filter's finite edge slope and out-of-band leakage are not modelled (§7.0).
35. **The V magnitude's own definition is a monochromatic normalisation at 5556 Å**, not an integral over the Johnson V transmission curve as the photometric system properly requires. Closing this needs the V passband tabulated (Bessell 1990) and its integrated photon zero point; the residual is a small colour-dependent error in the zero point itself, and is recorded rather than approximated (§7.0).
36. **A photographed body's reflectance is grey.** Its spectrum is integrated as the Sun's (5772 K, IAU 2015 Resolution B3), which is real physics for reflected sunlight, but a KSP `CelestialBody` carries one albedo with no wavelength dependence to read, so the reddening or bluing a real planetary surface imposes on reflected light is absent (§7.0).
37. **Airglow is integrated with a flat spectrum** because it is line emission (OI 557.7nm, OH Meinel bands) and no continuum shape could stand for it; the other three sky terms are integrated as scattered sunlight, which they are (§7.3).
38. **The exported WCS is exact and self-consistent but tied to the arbitrary RA zero point** of §12.1 on stock: a source at a given catalogue position lands where the header says, and the frame plate-solves against itself, but the pointing is not the real sky's unless the installed pack models the real solar system (§7.7.1).
39. **A stacked composite carries no WCS** (§7.7.1), because centroid registration on a moving body aligns the target and not the field, so no single pointing describes the stack.
40. **The sky background's extinction is still evaluated at the filter's central wavelength**, not integrated across the band as the sources' now is: its four terms are each attenuated differently (airglow inside the atmosphere, zodiacal light outside it, moonlight and twilight already measured through it) and preserving that distinction was judged more important than resolving the residual in wavelength (§7.3).

### 12.1 The nebula-morphology limit, and the layer built for it

**This is the largest known limitation of the imaging path, and it is a property of the available
data rather than of the code.** It is written out at length because it is the one thing a reader of
the pretty pictures will notice first.

The all-sky H-alpha map (`Core/EmissionMap.cs`, packed by `tools/pack_halpha_map.py`) is the
**Finkbeiner (2003, ApJS 146, 407)** WHAM/VTSS/SHASSA composite, whose beam is **6 arcmin**. Its
HEALPix sampling at nside 1024 is 3.44′, finer than the beam, so the sampling is not the limit — the
beam is. Every structure that makes a nebula recognisable is finer than it:

| structure | size | beams | rendered |
|---|---|---|---|
| Horsehead (B33) head | 3′ | 0.5 | not at all |
| M42 Trapezium | 5′ | 0.8 | not at all |
| Horsehead silhouette | 8′ | 1.3 | not at all |
| IC 1396A filaments | 2′ | 0.3 | not at all |
| M42 wings, dark lanes | 10′ | 1.7 | a smudge |
| IC 1396A (the Trunk) | 20′ | 3.3 | a smudge |
| M42 whole / Rosette ring | 85′ / 80′ | 14 / 13 | outline only |
| North America / IC 1396 whole | 120′ / 170′ | 20 / 28 | outline only |

A real astrophotograph works at ~2″, **180× finer**. `Core/DeepSkyObject.BeamsAcross` computes the
figure and the sky chart reports it on hover, so a player is told before spending the exposure.

Two consequences that no resolution would remove:

* **A dark nebula cannot come out of an emission map at all.** What defines the Horsehead is the
  absence of light where dust blocks emission behind it; the map holds only what is emitted. B33 is a
  separate `DeepSkyKind.DarkNebula` entry and the chart says the installed data cannot show it. This
  was a real error before: IC 434, the emission ridge, was labelled as the Horsehead.
* **The composite carries a visible artefact around M42**, the brightest H-alpha source in the sky: a
  ridge ~10′ wide and ~1.5° long, where M42 is a roughly round 85′ × 60′ nebula. Reading the file
  directly with `healpy` reproduces it, so it is in the published data — most plausibly a saturation
  bleed in the survey images the composite mosaics. Not corrected, because correcting it would mean
  inventing what is underneath.

**The layer built for it.** `Core/EmissionPatchSet.cs` adds high-resolution patches over the sky where
a finer survey exists, built by `tools/pack_shassa_patches.py` from **SHASSA** (Gaustad, McCullough,
Rosing & Van Buren 2001, PASP 113, 1326) — 0.8′ over everything south of +15° declination, at which
the Horsehead spans 10 elements rather than 1.3.

Design points, each of which was a decision with an alternative:

* **Patches, not a finer all-sky map.** All-sky at nside 4096 (0.86′) is 201 million cells and 403 MB,
  nearly all diffuse background 6′ already describes. A degree or two per catalogued object is ~5 MB
  for the whole catalogue. Outside a patch the base map answers.
* **Run-length by HEALPix ring.** In RING order a disc cuts each ring in one contiguous stretch, so a
  patch is a few hundred runs rather than a 4-byte pixel index per 2-byte value, which would have
  cost three times the values themselves.
* **The covering patch is resolved once per frame**, not per pixel: a frame is arcminutes across and
  cannot span two patches. Lookup within a patch keeps a run cursor, so consecutive frame pixels along
  a row cost one comparison rather than a binary search.
* **A frame straddling a patch edge falls back entirely to the base map** — a visible loss of detail,
  never a seam.
* **The calibration is measured, not assumed.** SHASSA's pixel units are not taken on trust: each
  cutout is smoothed to 6′ and regressed against the composite, which measures the scale and prints
  it. The patch stores `composite + scale × (cutout − smoothed cutout)`, so the absolute calibration
  stays the composite's and SHASSA contributes only sub-6′ structure. Smoothing a patch back to 6′
  returns the composite. Residual after matching: ~20%, which is the uncertainty on the fine
  structure's *amplitude*, reported per patch.
* **The fine term is apodised to zero across the patch's outer margin**, so a patch joins the base map
  continuously.

Validated in `tools/emission-tests`: the shipped reader covers exactly the directions the file does
over 4000 test positions, and its values agree with an independent read to 5.3×10⁻³ relative — which
is the Galactic transform's own 3×10⁻⁶ deg accuracy acting on the gradient across a 51″ cell, half
float precision alone being 4.9×10⁻⁴.

**What remains open.** SHASSA stops at +15°, so IC 1396, North America, the Heart, the Soul, the
Bubble and the Cave stay at 6′; **VTSS** at 1.6′ over the northern plane is the obvious next layer and
is not built. And even at 0.8′ these are survey images: the result is a nebula rather than a smudge,
but it is not a two-arcsecond astrophotograph and does not claim to be.

### 12.2 Colorimetry, chromatic PSF, and the airglow spectrum

Added in the carte-blanche pass; each has its own harness and README with the full numbers.

**`Core/Colorimetry.cs` + `Core/CieColourMatchingTable.cs` (generated)**: the CIE 1931 2-degree
observer at 1 nm and the primary-derived IEC sRGB matrix, emitted by `tools/generate_cie_table.py`
from colour-science and compared back against it (table exact to 2e-15; Planckian locus to 3.6e-6 in
xy, which is the CIE-15-vs-SI c2 convention, documented). Gamut handling desaturates toward the white
point in BOTH directions (negative components, and components above one at the triple's luminance);
the harness proves luminance survives to 3e-15 and that nothing is clipped. `StellarColor.BlackbodyRgb`
now delegates here; the old Helland fit is kept only as a measured comparison (worst 0.085 sRGB).

**`Core/ColourCalibration.cs`**: per-instrument 3x3 band-to-XYZ matrix, least squares over
blackbodies (1500-40000 K, log-spaced, unit-luminance normalised) plus four nebular line combs,
continuum weighted 4:1. The harness control is an ideal colorimeter: bands proportional to
xbar/lambda, divided by lambda because tristimulus integrates energy while detectors count photons,
 which fits to 1.5e-8 and makes the real residuals interpretable. `Visualization/ColourComposite.cs`
replaces ComposeLRGB: true colour through the matrix with luminance-only stretch (L channel scaled by
median ratio when present), or labelled HOO/SHO palettes with no colorimetric claim.

**`Core/AtmosphericRefraction.cs`**: Filippenko (1982) refractivity with the site's ICAO-standard
pressure/temperature and Buck (1981) vapour pressure; differential refraction exactly prop. tan z;
`SplitPassband` produces photon-weighted sub-bands with per-band dispersion offsets.
`OpticalPsf.BuildChromaticKernel` sums per-sub-band kernels (Airy at its own lambda, seeing scaled by
lambda^(-1/5)) at their offsets with bilinear placement; one sub-band with no offset is bit-identical
to the monochromatic kernel. The zenith direction in pixel space is obtained by projecting the zenith
through the frame's own projection, so mount geometry and parity are inherited rather than derived.
`VisualTelescopeSpec.HasAtmosphericDispersionCorrector` (SPHERE only; Beuzit et al. 2019) scales the
offsets by a stated 5% residual. The kernel cache keys on zenith distance and direction.

**`Core/Airglow.cs` + `Core/AirglowTable.cs` (generated)**: ESO SkyCalc airglow (lines and residual
continuum separately) bin-integrated to 0.1 nm by `tools/generate_airglow_table.py`; van Rhijn shell
factors with the [O I] red doublet on a 250 km layer; Bessell (1990) V transcription (identical to
speclite to 1e-16) for the V surface brightness, which lands on Patat's measured dark sky. The flat
airglow term and its scalar van Rhijn factor are retired from the visual-camera path
(`GatherSkyBackground`); the transit instruments still use the old scalar model.

**`OpticalPsf.Normalise`**: the circular support clip is now gated on the measured energy in the
square-minus-circle annulus (budget 1e-3): wide seeing kernels keep the isotropic edge, compact
kernels keep the full square that GalSim validated. This fixed a 17% encircled-energy regression the
unconditional clip had introduced on the RedCat.

**Generated-file discipline**, learned the hard way: generators write to a file via `--out`, never to
stdout; the skycalc client prints an informational note on stdout, and shell redirection silently
made it the first line of a .cs file.

## 13. Bibliography (papers/formulas actually cited in-source)

- Ballesteros, F. J. (2012). "New insights into black bodies." *EPL* 97, 34008. — B-V→Teff relation.
- Bouchy, F. et al. (2009). SOPHIE spectrograph characterization.
- Claret, A. & Bloemen, S. (2011). Quadratic limb-darkening coefficient tables.
- Cumming, A., Marcy, G. W. & Butler, R. P. (1999). RV semi-amplitude formalism (with Lovis & Fischer 2010 below).
- Gillon, M. et al. (2018). SPECULOOS survey description.
- Gilmozzi, R. & Spyromilio, J. (2007). ELT (39.3m, Cerro Armazones) description.
- Gaia Collaboration, Gaia DR3 documentation, "Photometric relationships with other photometric systems", Tables 5.9 and 5.10. The `G − V` and `(G_BP − G_RP)(B − V)` polynomials, their validity range and their 0.03017 mag scatter (§7.015).
- ESO, FORS2 filter transmission curves (www.eso.org/sci/facilities/paranal/instruments/fors/inst/Filters/curves.html). "The transmission curves for many of the FORS interference filters have been measured within the instruments" — the Bessell B/V/R tables the FORS2 bandpass is integrated over (§7.02).
- Martinez, P. et al. (2011). "Band-Limited Coronagraphs using a halftone-dot process: II." arXiv:1111.6956. Scaled VLT and E-ELT pupil masks: the VLT pupil at Φ=3 mm has "the central obscuration scaled to 0.47 mm ± 0.002 mm (14% linear ratio) and the spider-vane thickness is 15 µm ± 4 µm" — the source of the VLT's 4.1 cm vanes (§7.113). Its E-ELT mask independently reproduces the ELT's published 50 cm vanes to 4 %.
- Schwartz, N., Sauvage, J.-F., Correia, C., Petit, C., Quiros-Pacheco, F., Fusco, T., Dohlen, K., El Hadi, K., Thatte, N., Clarke, F., Paufique, J. & Vernet, J. (2018). "Sensing and control of segmented mirrors with a pyramid wavefront sensor in the presence of spiders." *AO4ELT5*. The ELT's secondary "is supported by six 50-cm wide spiders" — the vane count and width the direct-imaging spikes are computed from (§7.112).
- ESO, "The ELT's main structure" (elt.eso.org/telescope/structure/). The M2 crown "is connected to the top ring by means of six beams, forming the 'spider'" — independent confirmation of the vane count.
- ESO, "E-ELT Optics" (www.eso.org/sci/facilities/eelt/telescope/mirrors/). The segmented primary "has a diameter of approximately 39 m" with "a 11.1 m central obstruction", filled from an inner radius of 5.5 m to an outer 18.5 m — the pupil obstruction ratio ε = 0.2824 the direct-imaging diffraction pattern is computed from (§7.111).
- Kasper, M. et al. (2021). PCS/ELT direct-imaging contrast targets.
- Kopparapu, R. K. et al. (2013, erratum-corrected). *ApJ* 765, 131. Habitable-zone boundary flux polynomials.
- Kopparapu, R. K. et al. (2014). *ApJL* 787, L29. 1 M⊕ runaway-greenhouse HZ coefficients.
- Kovacs, G., Zucker, S. & Mazeh, T. (2002). Box-Least-Squares transit search algorithm.
- Krisciunas, K. & Schaefer, B. E. (1991). Moonlight sky-brightness scattering model.
- Lovis, C. & Fischer, D. (2010). "Radial Velocity Techniques for Exoplanets," eq. 2 — the `K=28.4329` constant.
- Mandel, K. & Agol, E. (2002). Analytic transit light curve model (small-planet approximation used here).
- Mayor, M. et al. (2003). HARPS spectrograph description.
- Ohta, Y., Taruya, A. & Suto, Y. (2005). Rossiter-McLaughlin effect analytic formalism.
- Pepe, F. et al. (2021). ESPRESSO spectrograph description.
- Perruchot, S. et al. (2008). SOPHIE spectrograph description.
- Pollacco, D. L. et al. (2006). SuperWASP survey description.
- Ricker, G. R. et al. (2015). TESS mission description.
- Young, A. T. (1967). "Photometric error analysis VI: confirmation of Reiger's theory of scintillation." *AJ* 72, 747. Atmospheric scintillation formula.
- Dravins, D., Lindegren, L., Mezey, E. & Young, A. T. (1997). "Atmospheric Intensity Scintillation of Stars I." *PASP* 109, 173. Extended-source (resolved-disk) scintillation suppression relative to a point source.
- Fried, D. L. (1978). "Probability of getting a lucky short-exposure image through turbulence." *JOSA* 68(12). Lucky-imaging selection theory.
- Baldwin, J. E. et al. (2001). Practical lucky-imaging frame-selection fractions.
- Short, A. et al. (2010). Charge Distortion Model (CDM) for CCD charge-transfer inefficiency, as implemented in the Pyxel detector-simulation framework.
- Leinert, C. et al. (1998). *A&AS* 127, 1. Zodiacal light surface-brightness reference tables.
- Patat, F. (2003). *A&A* 400, 1183. New-moon zenith night-sky brightness measurements (Paranal).
- Patat, F. et al. (2006). *A&A* 455, 385. Twilight sky brightness vs. solar depression (Paranal).
- van Rhijn, P. J. (1921). Zenith-angle dependence of emission from a thin atmospheric layer.
- Roach, F. E. & Gordon, J. L. (1973). *The Light of the Night Sky*. Airglow emitting-layer height.
- Hansen, J. E. & Travis, L. D. (1974). *Space Sci. Rev.* 16, 527. Rayleigh optical depth vs. wavelength.
- Bucholtz, A. (1995). *Appl. Opt.* 34, 2765. Tabulated Rayleigh scattering coefficients.
- Ångström, A. (1929). Aerosol turbidity λ^-α law.
- Calabretta, M. R. & Greisen, E. W. (2002). *A&A* 395, 1077. FITS world coordinate systems; the TAN (gnomonic) projection, and the CTYPE/CRVAL/CRPIX/CD keyword set written by `FitsWriter` (§7.7.1).
- Greisen, E. W. & Calabretta, M. R. (2002). *A&A* 395, 1061. FITS world-coordinate keyword conventions (Paper I of the same pair).
- Ma, D. & Cai, Z. "Scientific performance analysis of the SYZ telescope design vs. the RC telescope design." *MNRAS*; arXiv:1708.01257, §4.2. Mirror-train throughput as `r^N·(1-ε²)`, and the 87% band-averaged aluminium reflectivity over a 2-year re-coating cycle used for every mirror in the roster (§7.001).
- Magrath, B. (1997). Aluminium coating reflectivity degradation with time (90% fresh → 87% after 1 year → 84% after 2), as cited by Ma & Cai above.
- Ettlinger, E., Giordano, P. & Schneermann, M. (1999). *The Messenger* 97, 4-8. "Performance of the VLT Mirror Coating Unit" — ESO's own absolute reflectance measurement of the VLT aluminium coating, placed between Bennett et al.'s fresh and aged samples across 300-2500 nm.
- Bennett, H. E., Silver, M. & Ashley, E. J. (1963). *JOSA* 53, 1089. "Infrared Reflectance of Aluminum Evaporated in Ultra-High Vacuum" — the fresh/aged evaporated-aluminium reflectance standard ESO's measurement above is referred to.
- Prša, A. et al. (2016). *AJ* 152, 41. "Nominal values for selected solar and planetary quantities" (IAU 2015 Resolution B3) — the Sun's nominal effective temperature, 5772 K, used as the spectral shape of every reflected-sunlight source in the frame (§7.0).
- eso.org FORS Filter Specifications page: real b_HIGH+113 (440nm/103.5nm), v_HIGH+114 (557nm/123.5nm), R_SPECIAL+76 (655nm/165nm) and H_Alpha+83 (656.3nm/6.1nm, peak transmission 0.70 SR / 0.76 HR) figures (§7.001).
- eso.org FORS2 detector QE curve (`sci/php/optdet/instruments/fors2`): the six-point measured curve for the MIT/LL CCID20 mosaic, integrated across each passband (§7.001).
- Bessell, M. S. (1990). *PASP* 102, 1181. Johnson-Cousins passband definitions — cited for what is *not* done: the V zero point remains a monochromatic normalisation rather than an integral over this curve (§12.35).
- Rowe, B. T. P. et al. (2015). *Astron. Comput.* 10, 121. GalSim — image simulation as a sum of independently-fluxed sources.
- Bertin, E. (2009). SkyMaker — synthetic astronomical image generation.
- Janesick, J. R. (2001). *Scientific Charge-Coupled Devices*. SPIE Press. CCD full-well overflow/blooming physics.
- Massey, R., Stoughton, C. & Rhodes, J. et al. (2010). *PASP* 122, 1035. Measured HST ACS/WFC charge-transfer inefficiency after radiation damage.
- Particle Data Group. "Passage of Particles Through Matter" (Review of Particle Physics, cosmic-ray section). Sea-level cosmic-ray muon flux.
- Grieder, P. K. F. (2001). *Cosmic Rays at Earth*. Sea-level cosmic-ray flux reference.
- Ritchey, G. W. & Chrétien, H. (1922). Original Ritchey-Chrétien telescope description (coma-free hyperbolic two-mirror design).
- Schroeder, D. J. (2000). *Astronomical Optics*, 2nd ed. Ch. 6. Seidel third-order aberration field-dependence (coma linear, astigmatism quadratic in field angle).
- Rutten, H. & van Venrooij, M. (2002). *Telescope Optics*. Cassegrain/Ritchey-Chrétien aberration theory.
- Pech-Pacheco, J. L. et al. (2000). "Diatom autofocusing in brightfield microscopy: a comparative study." Variance-of-Laplacian sharpness/focus operator.
- Pertuz, S., Puig, D. & Garcia, M. A. (2013). "Analysis of focus measure operators for shape-from-focus: a comparative study." Survey validating variance-of-Laplacian as a top-performing focus operator.
- Russell, H. N. (1916). Lambertian-sphere phase-integral law used for the solar-system astrograph's real apparent-magnitude model (§7.0).
- ZWO official ASI294MM Pro datasheet (zwoastro.com/product/asi294): real full well, read noise, dark current, pixel pitch, native resolution, peak QE — the sensor anchor for the RC20/CDK1000 noise/resolution model.
- PlaneWave Instruments / PlaneWave Europe RC20 product pages (planewave.com, planewave.eu): real f/6.8 focal ratio, focal length, and secondary-mirror obstruction.
- PlaneWave Instruments CDK1000/PW1000 product page (planewave.com): real 1.0m aperture, f/6 focal ratio, 47% central obstruction.
- eso.org FORS2 User Manual and FORS2 Standard Filters page: real FORS2 plate scale, filter set and bandwidths, exposure range.
- eso.org VLT Unit Telescope M2 Unit page: real M2 secondary-mirror diameter (1.116m), shared by both VLT instruments' obstruction fraction.
- Wittman, D. et al. (1998). *SPIE* 3355, 598. "Characterization and optimization of MIT Lincoln Laboratories CCID20 CCDs" — the chip family used in FORS2's real detector.
- Cuillandre, J.-C. et al. (1999). CFH12K/ESO CCD workshop technical note. Real CCID20 full-well capacity (150,000 e⁻), the figure used for FORS2 since its own manual doesn't restate one for the shared chip.
- Schmid, H. M. et al. (2018). *A&A* 619, A9. "SPHERE/ZIMPOL high resolution polarimetric imager. I. System overview, PSF parameters, coronagraphy, and polarimetry." Real ZIMPOL detector Table 4 (full well, read noise, dark current, minimum exposure), plate scale, filter set, and achieved adaptive-optics resolution.
- Standard V-band photometric zero point (Vega calibration): 948 photons/cm²/s/Å — a standard reference value in observational photometry/exposure-time-calculator literature.
- Real Sun apparent V-band magnitude at 1 AU (-26.74) — standard astronomical constant.

Cosmetic (bad-pixel-map) correction before registration/stacking follows the standard professional calibration workflow used by PixInsight's `CosmeticCorrection` process, IRAF/ccdproc's `fixpix`, and ESO Reflex pipeline bad-pixel handling. FITS export (§7.7) follows the FITS standard's own conventions (80-byte cards, 2880-byte blocks, BZERO/BSCALE unsigned-16-bit representation) and real acquisition-software header keyword conventions (SharpCap, NINA, MaximDL).

*Reverse-engineering note*: `EveCloudIntegration.cs`'s API was verified by decompiling EVE-Redux 1.11.7.2 with ilspycmd (not a paper, but a real methodological citation worth keeping for the paper's methods section). `KopernicusOnDemandIntegration.cs`'s API (§7.13) was verified the same way against Kopernicus 1.12.1.247; the reflection lookup fails soft (logs and disables itself for the session) if a future Kopernicus release renames or restructures `ScaledSpaceOnDemand`, the same fallback behaviour as the EVE integration.

---

*Generated as a living document alongside the codebase. If a section here and the code disagree, trust the code and fix this file — that's the whole point of keeping it.*
