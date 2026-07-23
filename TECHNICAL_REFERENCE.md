# ExoInstruments — Technical Reference

Exhaustive technical record of every mechanic, formula, data source, and known simplification in the mod, kept as source material for the scientific paper. Kept inside the repo (`TECHNICAL_REFERENCE.md`) so `git pull` carries it between machines — no separate file to lose track of.

**How to keep this current:** when a mechanic changes, update the matching section here in the same commit. The README is player-facing marketing copy and drifts out of sync on purpose (readability over precision); this document is the opposite — precision over readability, so treat any conflict between the two as this document being right.

---

## 1. Architecture overview

- **KSP1 mod**, C#/.NET Framework 4.72, Unity-hosted, `AddonScenario`/`ScenarioModule` for persistence.
- **Layering**: `Core/` = pure C#, no Unity dependency, all the actual physics/math/data-model code, unit-testable in principle. `Visualization/` = Unity-dependent texture rendering (reads Core outputs, produces `Color[]`/`Texture2D`). `Session/` = per-campaign game-loop objects (tick forward in UT, accumulate samples). `ExoInstrumentsGUI.cs` = the single large IMGUI window (~4150 lines) gluing everything together. Root-level files (`BetterTimeWarpIntegration.cs`, `ObservatoryBuilding.cs`, `ExoInstrumentsScenario.cs`) are cross-cutting integrations.
- **Three independent detection pipelines** (`DetectionMethod` enum): `Transit`, `RadialVelocity`, `DirectImaging`, plus a fourth non-exoplanet mode `SolarSystemPhotography` (the RC20) that reuses the instrument-economy scaffolding but none of the detection-science fields.
- **No real KSP star system used for astrophysics**: the star catalog (real RA/Dec, real exoplanet.eu/BSC data) is projected onto Kerbin's sky using an *arbitrary* zero-point convention (`SkyCoordinates.cs`) — Kerbin's rotation sweeps the meridian around the real sky, four times faster than Earth's, with no physical relationship between the two. This is a deliberate, foundational simplification everything else builds on.
- **Deterministic-by-hash design**: many "random" per-star properties (stellar activity level, rotation period, spot phase, RM spin-orbit angle, direct-imaging pointing offset) are not stored — they're derived from an FNV-1a (or similar) hash of the star's identity string, so the same star always gets the same synthetic properties across sessions without needing save-file bloat.

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

- **PSF**: Gaussian core (`σ = 0.45·λ/D`, "approximating the Airy core width, same order of approximation as the rest of the mod, not exact Bessel math") + an ad hoc ring envelope beyond 1λ/D: `0.017·(1.63λ/D/r)³·cos²(π·(r/(λ/D)-1.63))` standing in for the true Airy pattern.
- **Spider spikes**: 6-vane (ELT pupil) diffraction spikes, 60° apart, azimuthal Gaussian (σ=1.3°) × 1/r² radial falloff, amplitude `4e-4` relative to peak at 1λ/D.
- **Speckle halo**: noise floor from `DirectImagingSimulator.SpeckleFloorAtSeparation`, improving as `1/(5·sqrt(hours))`, modulated by a `cos²` "wind-butterfly" asymmetry (a real documented AO-residual phenomenon) between `0.55×` and `1.45×`.
- **Background**: fixed `3e-9` at 1hr (√t-improving), explicitly independent of target brightness.
- **Planet PSF**: same Gaussian core, scaled by `ContrastRatio·peakScale`, added additively "as on a real detector."
- **Deterministic per-target randomness**: pointing offset (≤12% FOV/axis) and position angle from name-hashes — same frame layout every refresh for a given target, not re-rolled.
- **Color mapping**: log-stretch over 9 decades (1e-9..1), tinted by the star's blackbody color when Teff is known, else a fixed black→orange→white "near-IR convention" ramp.
- 14 fixed hot pixels (target-hash-seeded — "the same physical detector would put them at fixed positions per instrument, but per-target keeps each frame visually distinct," an explicit, deliberate departure from physical fidelity for visual variety).

---

## 5. Atmosphere, sky, and ground-based observing conditions

### 5.1 Airmass & twilight (`ImagingObservingConditions.cs`)

- **Airmass**: plane-parallel `1/sin(altitude)` (secant of zenith angle). "Accurate to <1% above 20°, which is our telescope floor" — no refraction/curved-atmosphere correction.
- **Twilight cutoff**: `IsNight` when Sun altitude `< -12°` (nautical twilight).
- **Telescope altitude floor**: `20°` (`MinTelescopeAltitudeDeg`); RC20's own capture gate uses a separate, lower `0°` geometric-horizon threshold (see §7).
- **Efficiency**: `1/airmass²` when observable, else 0 — "airmass weighting: SNR² accumulates at 1/X², so one hour at X=2 ≈ 15 min at zenith."
- **Sun's declination fixed at 0°** — stock KSP bodies have no axial tilt, so no seasons; no orbit on record defaults to permanent night (degenerate-save fallback).
- Space-based instruments bypass all of this: synthetic always-observable snapshot (`SunAlt=-90, TargetAlt=90, Airmass=1, Efficiency=1`).

### 5.2 Atmospheric scintillation (`AtmosphericNoise.cs`, `AtmosphericImagingNoise.cs`)

**Young (1967)** formula, reused identically by the transit photometers and the RC20 camera:
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

## 7. RC20 solar-system astrograph

Non-exoplanet instrument (`DetectionMethod.SolarSystemPhotography`) — point-and-shoot photography of any Kerbol-system body, clicked directly on the sky chart. `SolarSystemCameraTexture.cs` clones KSP's own galaxy/scaled-space cameras (same technique as Tarsier Space Technology's TSTCameraModule) and runs the frame through a full noise pipeline.

### 7.1 Optics / atmosphere

- **Extinction**: Bouguer's law, same as §5.2, `k=0.20 mag/airmass`.
- **Scintillation**: Young (1967), same formula as §5.2, using the RC20's own 0.51m aperture / 560m site altitude (ETH Zürich).
- **Seeing blur**: grows linearly with `(airmass-1)`, capped at 6px.
- **Defocus**: manual, only when autofocus is off.
- **Astigmatism** (not coma): the RC20 is a true Ritchey-Chrétien (per `Observatories.cs`), and a real RC's whole reason for existing is that its hyperbolic mirror pair cancels third-order coma (**Ritchey & Chrétien 1922**) — giving it coma would misrepresent the optical design it's named after. The dominant remaining off-axis Seidel aberration for an RC is astigmatism, whose transverse blur scales with the *square* of the field angle (coma would scale linearly — **Schroeder, *Astronomical Optics* 2nd ed. 2000, ch. 6**; Rutten & van Venrooij, *Telescope Optics*), smeared radially outward from frame center. Zero at the centered target, worst for background stars near the corners. The radial-quadratic *form* is literature-sourced; the pixel amplitude at the frame corner has no published PlaneWave RC20 optical-prescription number to derive from, so it's a display calibration constant, not a measured one (same category as e.g. the exposure gain constant).

### 7.2 Clouds (EVE integration — `Visualization/EveCloudIntegration.cs`)

Reflection-based soft dependency on **EVE-Redux** (API "verified by decompiling EVE-Redux 1.11.7.2 with ilspycmd"). Samples the real installed cloud-layer cubemap texture for the home body at KSC's zenith direction (a fixed body-frame vector — narrow FOV means the exact viewing direction barely matters). Returns 0 if EVE isn't installed or no cloud layer is configured; **no procedural fallback**. Known approximation: EVE's own wind-drift texture animation isn't replicated (a static sample). Coverage feeds: `cloudTransmission = 1 - coverage·0.85` (never fully opaque), plus a haze/veiling term and up to 2px of cloud-driven blur.

### 7.3 Sky background (multi-component)

```
skyBackground = (twilightRamp·0.30 + moonSkyExcess·0.02 + airglow(0.004) + zodiacal(0.000916)) · exposure · filterThroughput
```
- **Twilight**: ramps from the -12° capture cutoff down to -18° astronomical twilight.
- **Moon scattering**: uses the *real* Krisciunas & Schaefer (1991) kernel from §5.3 (`MoonlightPollution.ScatteringKernel`), weighted by the true angular separation between the imaged body and each moon.
- **Airglow**: fixed always-present baseline.
- **Zodiacal light**: derived from the airglow baseline via the real Pogson magnitude-ratio relation, not an independently invented constant: **Leinert et al. (1998, A&AS 127, 1)** give V=23.3 mag/arcsec² zodiacal light at the ecliptic pole (its faintest tabulated value); **Patat (2003, A&A 400, 1183)** gives V≈21.7 mag/arcsec² for typical new-moon zenith dark-sky brightness (dominated by airglow — the same phenomenon the airglow constant represents). `ratio = 10^(-0.4·(23.3-21.7)) = 0.229`, so `zodiacal = 0.229 · airglow`. The mod has no real ecliptic geometry for Kerbol, so this stays a fixed baseline rather than a position-dependent term.

### 7.4 Solar-system-body observing forecast (`ExoInstrumentsGUI.ComputeBodyForecast`)

Separate from the generic heatmap (§5.4) — this one is **not** renormalized per refresh (a moving planet's best cell constantly enters/exits the visible window; renormalizing would recolor the whole map every tick instead of letting bands scroll). `Quality = (1/airmass²)·cloudTransmission`, an absolute [0,1] scale. Cloud coverage sampled once (current EVE reading) and applied uniformly to every future cell — a deliberate "clouds persist" approximation, since EVE has no forecastable weather model. Bodies use a `0°` geometric-horizon gate (matches the live camera's own capture gate), not the science-instrument `20°` floor.

### 7.5 Sensor noise chain

- **Shot noise**: `σ = 0.55·sqrt(signal)` — abstract [0,1] units, not real photon counts, but the shape (sqrt scaling, shadows noisier than highlights) is real.
- **Dark current**: pedestal + `0.55·sqrt(darkUnits)` noise, `darkUnits = 0.01/s · exposure`.
- **Read noise**: fixed Gaussian σ=0.02, applied *after* the ISO gain stage (not amplified by gain — matches real sensor electronics).
- **Hot/dead pixels**: fixed defect map, seeded once from a constant (`20260721`), same defects every session — 1-in-3000 hot, 1-in-6000 dead. Applied *after* all blur (a read-out-stage defect, not an optical one — shouldn't be softened by seeing/defocus/astigmatism blur the way real scene light is).
- **Full-well blooming**: Pyxel's own model only hard-clips (`min(pixel, fwc)`, no redistribution — verified against their source, `pyxel/models/charge_collection/full_well.py`), so this follows real CCD device physics instead: excess charge above full well (`1.0`) spills along the column/shift-register direction, split symmetrically between the two vertical neighbors — a charge-conserving 50/50 split, the textbook default absent device-specific anti-blooming-gate data (**Janesick 2001, *Scientific Charge-Coupled Devices*, SPIE Press**). Cascades up to 4 iterations (a numerical convergence cap, not a physical quantity — same role as the 50-iteration cap on the Kepler-equation solver elsewhere in this codebase).
- **Charge-transfer smear / CTI**: simplified single-trap-species version of the `nc`/`nr` capture/release structure from **Short et al. (2010)**'s CDM model (Pyxel's `pyxel/models/charge_transfer/cdm.py`). Capture fraction (`1e-4`/row) is calibrated against real measured charge-transfer inefficiency: a fresh CCD sits near `1e-6`/transfer, while HST's ACS/WFC at severely radiation-damaged end-of-life reaches `~1e-4-1e-3`/transfer (**Massey, Stoughton & Rhodes 2010, PASP 122, 1035**) — `1e-4` sits at that damaged-device ceiling, the conservative end of the real range for a healthy amateur sensor. Release fraction (`35%`/row) represents the fast-trap species real CDM models always include alongside slow traps — a trap whose release time is comparable to the transfer period empties within the first few pixels, the short visible trail seen below bright sources in real frames.
- **Cosmic ray hits**: flat Poisson process, isotropic incidence angle, random 2–14px track length, deposits a bright streak. Rate is *derived*, not tuned: sea-level cosmic-ray (mostly muon) flux ≈ 1 cm⁻² min⁻¹ (**Particle Data Group, "Passage of Particles Through Matter" review**; **Grieder 2001, *Cosmic Rays at Earth***), applied to a real, commercially available sensor's pixel pitch — the **ZWO ASI294MM Pro's published 4.63 μm** — over the 480×480 frame: side `= 480×4.63e-4 cm = 0.2222 cm`, area `= 0.04939 cm²`, rate `= 1 cm⁻²min⁻¹ × 0.04939 cm² / 60s = 8.23e-4 hits/s` — genuinely rare for a short exposure, matching real amateur-imaging experience (cosmic ray hits are mainly a long-exposure/large-sensor phenomenon). Pyxel's own CosmiX/TARS angle model is an unimplemented stub in their shipped source, so isotropic sampling here is no less physical than upstream.
- **Persistence/ghosting**: real default trap time constants and species proportions from Pyxel's persistence model (`τ = {1,10,100,1000,10000}s`, proportions `{0.307,0.175,0.188,0.136,0.194}`, in the spirit of **Fixsen, Offenberg, Hanisch et al. 2000, PASP 112, 1350**). Each species relaxes exponentially toward an equilibrium fill level set by the current signal and its proportion, using the *same* τ for both capture and release — matching Pyxel's own one-τ-per-species structure, rather than a separately invented capture rate. A true exponential `Q(t)=Q₀·exp(-Δt/τ)` replaces Pyxel's small-step linear form (which would diverge over the multi-minute real-time gaps between manual RC20 shots); the relaxation is naturally self-bounded (it converges toward the equilibrium), so no arbitrary trap-capacity cap is needed either.
- **ISO gain**: amplifies signal + pre-gain noise together; does not touch read noise.
- **Filter throughput**: L=1.0, R=0.5, G=0.55, B=0.45, Hα=0.12 (narrowband, needs much longer exposure).
- **Diurnal drift**: without autoguiding, horizontal motion blur proportional to Kerbin's sidereal rotation rate over the exposure — an untracked mount's classic star-trail smear.

**Explicitly rejected**: GalSim's brighter-fatter effect (`SiliconSensor`) — its real formula needs per-sensor electrostatic-vertex calibration tables (e2v/ITL-specific) with no generic published values, and the RC20 doesn't do stellar photometry (its targets are extended solar-system bodies, not point-source stars), so the effect's main visual payoff (saturated star-core broadening) barely applies.

### 7.6 Image stacking (`Visualization/AstroImageStack.cs`)

- Per-filter stacks of up to 30 subs, centroid-based alignment (brightness-weighted, falls back to frame center if nothing exceeds threshold), robust sky-background subtraction (trimmed-median of a 20px border band, trims brightest 15% first to reject limb/hot-pixel contamination).
- **Cosmetic correction (bad-pixel-map)**: every sub is corrected *before* alignment using the sensor's known, fixed hot/dead pixel map — each defect pixel is replaced by the mean of its immediate orthogonal neighbors (excluding any neighbor that's itself a known defect). This is the standard professional calibration step real pipelines run before registration/stacking (PixInsight's `CosmeticCorrection` process, IRAF/ccdproc's `fixpix`, ESO Reflex bad-pixel handling). Doing it before alignment matters: a fixed sensor defect co-added with per-sub alignment shifts would otherwise scatter into a cloud of artifacts at different composite positions instead of being corrected once at its one true location.
- **LRGB composition**: luminance transfer — `R/G/B *= min(4.0, L_stack/rgbLuminance)` — capped to stop noise blow-up at near-zero background. Optional Hα boost into the red channel.
- **Display-only asinh stretch** (never applied to stored data): `arcsinh(k·v)/arcsinh(k)`, `k=5`.
- **Lucky imaging**: each filter's subs ranked by a **variance-of-Laplacian sharpness score** (**Pech-Pacheco et al. 2000**, "Diatom autofocusing in brightfield microscopy" — the top-performing general-purpose focus operator in the **Pertuz, Puig & Garcia 2013** survey), computed over the central 60% of the frame (mirroring AutoStakkert!'s alignment-point "quality box" concept for real planetary lucky imaging, since the RC20 always centers its aim there) with the sharpest-magnitude 2% of Laplacian values trimmed before the variance is taken (robust against an isolated cosmic-ray hit or hot pixel masquerading as a sharp frame — the same trimmed-statistic idiom the background estimator already uses). Only the sharpest 30% of subs are kept before alignment/averaging (mid-range of the 1–60% practical range in the lucky-imaging literature — **Fried 1978** for the underlying theory, **Baldwin et al. 2001** for practical frame-selection fractions). Always forces alignment on when active.
  - *Note on the prior implementation*: an earlier version scored sharpness by raw peak-pixel value. Since blooming, cosmic rays, and hot pixels can all saturate a single pixel anywhere in the frame regardless of actual seeing, that metric was inadvertently selecting artifact-contaminated frames as "sharpest" — corrected to variance-of-Laplacian, which measures genuine local contrast rather than any single pixel's value.

---

## 8. Visualization layer

### 8.1 Sky chart (`Core/SkyChartTexture.cs`)

Zenith-centered planisphere: `r = Rmax·(90-alt)/90` (linear zenith-distance projection, not true stereographic/gnomonic), `x=cx+r·sinAz`, `y=cy+r·cosAz` (north up). Marker brightness ramps from mag -1.5 (Sirius-bright, full) to mag 12 (display floor, 16% minimum) via linear interpolation on magnitude. Star color from `StellarColor.BlackbodyRgb` alpha-blended toward background by brightness; non-highlighted stars desaturated 55% toward gray and dimmed 40% during an active search. Reference altitude rings at 0/20/40/60°, cardinal cross overlay. Since a recent session change, both stars and solar-system bodies now share the same `0°` horizon gate (previously stars used a separate 10° cutoff — unified for consistency with the RC20's own capture gate).

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
| RC20 | SolarSystemPhotography | n/a | n/a | n/a | 0.51m | 560m (ETH Zürich) | 15,000 | 5 | 50 | 0.0 (no science economy)

Citations: SPECULOOS — Gillon et al. 2018. WASP — Pollacco et al. 2006. TESS — Ricker et al. 2015. HARPS — Mayor et al. 2003. ESPRESSO — Pepe et al. 2021. SOPHIE — Perruchot et al. 2008; Bouchy et al. 2009. ELT — Gilmozzi & Spyromilio 2007 (telescope), Kasper et al. 2021 (contrast). RC20 — PlaneWave Instruments RC20 (commercial 20" astrograph).

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
- **Kerbal Konstructs** (`ObservatoryBuilding.cs`): places a static building at KSC (currently a placeholder cube model) that opens the observatory GUI on click. Idempotent placement (matches existing instance by model name OR position, union not intersection, robust to a documented KK bug where Group reassignment corrupts the instance's internal name lookup and can silently drop it from `GetAllStatics()`). Adds a `BoxCollider` at runtime since the exported mesh carries none.

---

## 12. Consolidated list of deliberate simplifications

*(Every one of these is worth a line in a paper's Methods/Limitations section — collected here so nothing gets missed.)*

1. **No real physical link between the star catalog's RA/Dec and Kerbin's sky** — an arbitrary zero-point convention, not real astrometry (`SkyCoordinates.cs`).
2. **No weather simulation** in the generic (exoplanet-instrument) observing forecast — only the RC20's own forecast factors in real EVE cloud cover, and even that assumes clouds persist unchanged into the future (no forecastable weather model exists to query).
3. **`PrecisionExponent = 0.2` uniform across every instrument** — real magnitude-precision scaling varies by instrument/detector; this is one simplified relation for all.
4. **Career-economy numbers (unlock cost, science threshold, scan cost, reward multiplier, all of `ScienceRewards.cs`) are explicitly unvalidated placeholders** pending playtesting — only their relative ordering is a real design constraint.
5. **Kerbin has no axial tilt** — the Sun's declination is fixed at 0°, so there are no seasons and no day-length variation.
6. **Single-harmonic RV fit underestimates semi-amplitude on eccentric orbits** (real power leaks into higher harmonics) — period recovery stays accurate, amplitude runs low.
7. **BLS transit search has no false-alarm probability calibration** — SNR is relative confidence only, not a statistically calibrated detection significance.
8. **TTV/RM models are order-of-magnitude, single-dominant-perturber approximations** — only the strongest near-resonant pair is modeled; higher-order and secular effects are absent.
9. **Direct-imaging PSF is a Gaussian + ad hoc ring term**, not a true Airy/Bessel diffraction pattern; speckle/background noise is uniform pseudo-noise, not physically-derived photon statistics.
10. **RC20 sensor noise chain operates in abstract [0,1] units**, not real photon/electron counts — every effect's *functional form* is real (Poisson shot noise, CDM capture/release structure, Pyxel's persistence relaxation, real CTI/cosmic-ray/zodiacal magnitudes converted via the Pogson relation), but a handful of remaining amplitude constants (dark current rate, exposure gain, astigmatism's pixel amplitude at the frame corner) have no real photon-count analog to derive from and are display/gameplay calibration, not measured quantities — flagged individually in §7.
11. **CTI is a simplified single-trap-species model**; Pyxel's own real CDM (which this is adapted from) uses full SRH capture physics in real electron counts across multiple trap species.
12. **Cosmic ray incidence angle is isotropic-sampled**, not derived from a real particle angular-distribution model (matches the fact that Pyxel's own shipped angle model is an unimplemented stub) — though the *rate* is now a real derived quantity (sea-level muon flux over a cited real sensor's pixel area, see §7.5).
13. **Zodiacal light is a fixed baseline constant**, not position/season-dependent (no real ecliptic geometry exists for Kerbol in this mod) — though its *magnitude relative to airglow* is now derived from two real cited surface-brightness measurements via the Pogson relation, not independently invented.
14. **Habitable-zone polynomial fits are only valid 2600K–7200K** — no extrapolation outside that range (returns null instead).
15. **BSC5-derived decoy stars carry no mass/radius/distance data** (pre-Hipparcos catalog, no such columns) — always treated as "unknown," never invented.
16. **Transit duration (T14) assumes a circular orbit** — no eccentricity term, despite eccentricity being tracked elsewhere in the same star's data.
17. **All positional/name catalog matching uses small-angle and string-heuristic approximations** — ambiguous matches are refused (flagged for review) rather than guessed, but the matching itself isn't a rigorous spherical-trigonometry/fuzzy-matching pipeline.
18. **Deterministic hash-based "randomness"** for stellar activity level, rotation period, spot phase, RM spin-orbit angle, and direct-imaging pointing/position-angle — reproducible per star, not drawn from any measured distribution beyond the *range* the draw is confined to.
19. **RC20 brighter-fatter effect was deliberately not implemented** — GalSim's real model needs sensor-specific electrostatic calibration tables with no generic published values, and the effect's main payoff (point-source core broadening) barely applies to the RC20's extended solar-system targets anyway.
20. **RC20 lucky-imaging sharpness's scoring window (60% central region) and outlier-trim fraction (2%) are algorithmic engineering choices**, not measured quantities — the *operator itself* (variance of Laplacian) and the *keep fraction* (30%, literature range 1–60%) are literature-sourced, same distinction as other robust-statistics parameters already in the codebase (e.g. the sky-background trim fraction).

---

## 13. Bibliography (papers/formulas actually cited in-source)

- Ballesteros, F. J. (2012). "New insights into black bodies." *EPL* 97, 34008. — B-V→Teff relation.
- Bouchy, F. et al. (2009). SOPHIE spectrograph characterization.
- Claret, A. & Bloemen, S. (2011). Quadratic limb-darkening coefficient tables.
- Cumming, A., Marcy, G. W. & Butler, R. P. (1999). RV semi-amplitude formalism (with Lovis & Fischer 2010 below).
- Gillon, M. et al. (2018). SPECULOOS survey description.
- Gilmozzi, R. & Spyromilio, J. (2007). ELT (39.3m, Cerro Armazones) description.
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
- Fried, D. L. (1978). "Probability of getting a lucky short-exposure image through turbulence." *JOSA* 68(12). Lucky-imaging selection theory.
- Baldwin, J. E. et al. (2001). Practical lucky-imaging frame-selection fractions.
- Short, A. et al. (2010). Charge Distortion Model (CDM) for CCD charge-transfer inefficiency, as implemented in the Pyxel detector-simulation framework.
- Leinert, C. et al. (1998). *A&AS* 127, 1. Zodiacal light surface-brightness reference tables.
- Patat, F. (2003). *A&A* 400, 1183. New-moon zenith night-sky brightness measurements (Paranal).
- Janesick, J. R. (2001). *Scientific Charge-Coupled Devices*. SPIE Press. CCD full-well overflow/blooming physics.
- Massey, R., Stoughton, C. & Rhodes, J. et al. (2010). *PASP* 122, 1035. Measured HST ACS/WFC charge-transfer inefficiency after radiation damage.
- Particle Data Group. "Passage of Particles Through Matter" (Review of Particle Physics, cosmic-ray section). Sea-level cosmic-ray muon flux.
- Grieder, P. K. F. (2001). *Cosmic Rays at Earth*. Sea-level cosmic-ray flux reference.
- Ritchey, G. W. & Chrétien, H. (1922). Original Ritchey-Chrétien telescope description (coma-free hyperbolic two-mirror design).
- Schroeder, D. J. (2000). *Astronomical Optics*, 2nd ed. Ch. 6. Seidel third-order aberration field-dependence (coma linear, astigmatism quadratic in field angle).
- Rutten, H. & van Venrooij, M. (2002). *Telescope Optics*. Cassegrain/Ritchey-Chrétien aberration theory.
- Fixsen, D. J., Offenberg, J. D., Hanisch, R. J. et al. (2000). *PASP* 112, 1350. HgCdTe-detector persistence/trap-decay behavior, as implemented in the Pyxel detector-simulation framework.
- Pech-Pacheco, J. L. et al. (2000). "Diatom autofocusing in brightfield microscopy: a comparative study." Variance-of-Laplacian sharpness/focus operator.
- Pertuz, S., Puig, D. & Garcia, M. A. (2013). "Analysis of focus measure operators for shape-from-focus: a comparative study." Survey validating variance-of-Laplacian as a top-performing focus operator.

Cosmetic (bad-pixel-map) correction before registration/stacking follows the standard professional calibration workflow used by PixInsight's `CosmeticCorrection` process, IRAF/ccdproc's `fixpix`, and ESO Reflex pipeline bad-pixel handling.

*Reverse-engineering note*: `EveCloudIntegration.cs`'s API was verified by decompiling EVE-Redux 1.11.7.2 with ilspycmd (not a paper, but a real methodological citation worth keeping for the paper's methods section).

---

*Generated as a living document alongside the codebase. If a section here and the code disagree, trust the code and fix this file — that's the whole point of keeping it.*
