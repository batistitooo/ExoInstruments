# ExoInstruments — Technical Reference

Exhaustive technical record of every mechanic, formula, data source, and known simplification in the mod, kept as source material for the scientific paper. Kept inside the repo (`TECHNICAL_REFERENCE.md`) so `git pull` carries it between machines — no separate file to lose track of.

**How to keep this current:** when a mechanic changes, update the matching section here in the same commit. The README is player-facing marketing copy and drifts out of sync on purpose (readability over precision); this document is the opposite — precision over readability, so treat any conflict between the two as this document being right.

---

## 1. Architecture overview

- **KSP1 mod**, C#/.NET Framework 4.72, Unity-hosted, `AddonScenario`/`ScenarioModule` for persistence.
- **Layering**: `Core/` = pure C#, no Unity dependency, all the actual physics/math/data-model code, unit-testable in principle. `Visualization/` = Unity-dependent texture rendering (reads Core outputs, produces `Color[]`/`Texture2D`). `Session/` = per-campaign game-loop objects (tick forward in UT, accumulate samples). `ExoInstrumentsGUI.cs` = the single large IMGUI window (~4150 lines) gluing everything together. Root-level files (`BetterTimeWarpIntegration.cs`, `ObservatoryBuilding.cs`, `ExoInstrumentsScenario.cs`) are cross-cutting integrations.
- **Three independent detection pipelines** (`DetectionMethod` enum): `Transit`, `RadialVelocity`, `DirectImaging`, plus a fourth non-exoplanet mode `SolarSystemPhotography` (RC20/CDK1000/VLT FORS2/VLT SPHERE) that reuses the instrument-economy scaffolding but none of the detection-science fields.
- **Telescope catalog** (`Core/VisualTelescopeCatalog.cs`): every optics/sensor constant the `SolarSystemPhotography` rendering pipeline uses (aperture, focal length, native resolution, pixel pitch, QE, full well, read/dark noise, exposure/gain range, per-filter bandwidth and central wavelength, astigmatism amplitude, adaptive-optics FWHM/Strehl/halo) lives in a `VisualTelescopeSpec`, not hardcoded in `SolarSystemCameraTexture.cs`. `InstrumentSpec.VisualTelescope` (`Core/Observatories.cs`) links a career-economy row to its spec; picking that row in the Observatory dropdown calls `SolarSystemCameraTexture.SetActiveTelescope`, which re-derives every downstream quantity from the new spec.
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

Four fully real, cited instruments, switchable from the Observatory dropdown (`InstrumentSpec.VisualTelescope` links each career-economy row to its spec; picking a row calls `SolarSystemCameraTexture.SetActiveTelescope`):

- **RC20** — PlaneWave RC20 (f/6.8, 0.51m aperture, 39% linear secondary obstruction — planewave.eu product page) with a **ZWO ASI294MM Pro** camera (4144×2822 native resolution, 4.63μm pixels, 66,000 e⁻ full well, 1.2 e⁻ read noise, 0.0022 e⁻/s/pixel dark current at -20°C, ~90% peak QE — zwoastro.com/product/asi294). 4× Barlow for the tight end of the zoom range. No autoguider by default.
- **CDK1000** — PlaneWave CDK1000 (1.0m aperture, f/6, 6000mm focal length, 47% central obstruction of the primary mirror — planewave.com product page; the same optical tube sold as part of the "PW1000" 1-meter observatory system, a real unit of which was installed at Palomar Observatory in 2024 for MIT's WINTER project). Same ZWO camera and 4× Barlow as the RC20.
- **VLT FORS2** — the real Very Large Telescope, Unit Telescope 1 "Antu", Paranal (8.2m aperture, 2635m altitude, the same site already used for ESPRESSO). Real FORS2 imager: a mosaic of two MIT/Lincoln-Lab CCID20 CCDs (eso.org FORS2 User Manual), 15μm pixels, 0.126"/pixel real intrinsic plate scale (equivalent focal length 24.556m, back-derived from that published scale), 150,000 e⁻ full well (the CCID20 chip's own real spec, Cuillandre et al. 1999 CFHT/ESO CCD-workshop technical note; FORS2's own manual doesn't restate a full-well figure for the shared chip), 0.7 e⁻/ADU real gain and 1.89 e⁻ read noise (FORS2's own "100kHz,2×2,high" readout mode), 0.25s real minimum exposure. M2 secondary mirror 1.116m diameter (eso.org M2 Unit page) gives a 13.6% obstruction fraction. A real 2× High-Resolution collimator (1233mm SR / 616mm HR focal length, ratio 2.001) stands in for the tight end of the zoom range in place of an invented amateur Barlow. Fixed gain (a real research CCD has no ISO-like control). Always autoguided (§7.011). QE 86% peak (600nm; the real published curve is 400nm 58%, 500nm 74%, 600nm 86%, 700nm 83%, 800nm 66%, 900nm 39%). Real filters: b_HIGH (429nm/88nm FWHM) as Blue, v_HIGH (554nm/111nm FWHM) as Green, R_SPECIAL (655nm/165nm FWHM) as Red, Hα+83 (656.3nm/61Å FWHM) as HAlpha; Luminance uses the CCD's own quoted 330-1100nm sensitivity range as a genuine unfiltered/clear exposure (FORS2 has no dedicated amateur-style L filter). Astigmatism 0px: FORS2/the VLT Cassegrain is real and well-corrected, but no published optical prescription gives a field-dependent astigmatism coefficient to the precision this pipeline's display model would need.
- **VLT SPHERE** — same VLT, Unit Telescope 3 "Melipal", carrying the real SPHERE/ZIMPOL extreme-adaptive-optics imaging polarimeter (Schmid et al. 2018, *A&A* 619, A9, "SPHERE/ZIMPOL high resolution polarimetric imager. I."). Real f/221 system, equivalent focal length 1718.7m (back-derived from ZIMPOL's own published 3.6 mas/pixel plate scale at its standard 2×2-on-chip-binned mode with the real 15μm native pixel; `BinningFactor=1` reproduces ZIMPOL's real unbinned 1.8 mas/pixel mode, `BinningFactor=2` reproduces its real standard 3.6 mas/pixel mode exactly, no separate Barlow exists for this instrument). Cross-check: at native pixel count this gives a computed field of view of ~3.49", matching ZIMPOL's own real published 3.6"×3.6" field within rounding. Real CCD, Table 4 of the cited paper: 640,000 e⁻ full well, 20 e⁻ read noise, 0.2 e⁻/s/pixel dark current, 1.1s minimum integration time, 95% peak QE (600nm; 90% at 700nm, 65% at 800nm). Same shared VLT M2 obstruction fraction as FORS2. §7.011 adaptive optics: real ~25 mas achieved FWHM (Strehl ~40% in I-band, good conditions), independently corroborated by a second source giving 22-28 mas across V/R/I. Real filters: V (554nm/80.6nm FWHM) as Green, N_R (646nm/57nm FWHM) as Red, B_Ha (655.6nm/5.5nm FWHM, the broader of ZIMPOL's two real Hα filters, the narrower N_Ha at 0.97nm being too narrow for a simple single-exposure capture) as HAlpha. ZIMPOL genuinely has no real blue broadband filter (its filter set targets red/near-IR reflected-light and circumstellar-disk science) — Blue is simply absent from this instrument's filter wheel (§7.012) rather than a made-up number standing in for a filter that doesn't exist. Luminance uses ZIMPOL's own quoted 500-900nm working spectral regime. Astigmatism 0px, well-justified by the field size alone: ZIMPOL's real field of view is only 3.6"×3.6", far too narrow for off-axis aberration to grow to any meaningful amplitude.

### 7.0 Real photon-flux signal model (`Core/PhotonFluxModel.cs`)

The imaged body's brightness is no longer an invented flat exposure multiplier — it is the body's real apparent magnitude, converted through the active telescope's real optics/sensor chain into real electrons.

**Apparent magnitude** (standard planetary H-G-system flux-ratio formalism):
```
phi(alpha) = [sin(alpha) + (π-alpha)·cos(alpha)] / π        (Lambertian-sphere phase law, Russell 1916)
fluxRatio  = albedo · (R_AU/d_obs_AU)² / d_sun_AU² · phi(alpha)
m_body     = -26.74 - 2.5·log10(fluxRatio)
```
`-26.74` is the Sun's real V-band apparent magnitude at 1 AU. `albedo`/`R` are the live `CelestialBody`'s own real fields; `d_sun`/`d_obs`/`alpha` (phase angle) come from live 3D positions (Sun, body, KSC observer), the same `Vector3d.Angle` pattern `ComputeMoonSkyExcess` already used. This is a genuine improvement over the `(1+cosθ)/2` half-phase approximation used elsewhere (§7.3) — the real phase-integral form, not a cosine stand-in.

**Real electrons collected**:
```
N_electrons = 948 photons/cm²/s/Å · 10^(-0.4·m_body) · filterBandwidthÅ · apertureAreaCm² · QE · exposureSeconds · extinctionTransmission
```
`948 photons/cm²/s/Å` is the real V-band zero-magnitude photon flux density (Vega calibration, standard photometric reference). `apertureAreaCm²` is the active telescope's own real aperture minus its own real secondary obstruction (`EffectiveApertureAreaM2`, shared with §7.011's exposure rescaling). `QE` and `filterBandwidthÅ` per filter are each the active telescope's own real, published values (§7.00) — not a single pipeline-wide assumption anymore. The RC20/CDK1000's amateur LRGB wheel is the one case with no published per-channel bandwidth of its own, so R/G/B there keep an even-third-of-Luminance split (modern "1:1:1 balanced" CMOS LRGB filter design) and HAlpha keeps a real ~7nm narrowband figure; FORS2 and SPHERE use each of their own real named filters' own real bandwidth instead (§7.00).

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
- `AdaptiveOpticsHaloSeeingFwhmArcsec` — **0.65"**, Paranal's own published median seeing (the same site FORS2 observes from). The halo is by definition the light SAXO failed to gather, so it is the uncorrected seeing profile.
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

### 7.09 LRGB color calibration

`ComputeFramePixels`' calibration step (§7.1) converts each captured filter's raw rendered signal into real electrons by matching its sum to a physically-derived total (`ComputeCollectedElectrons`). That total is the same body-wide albedo split into equal thirds for R/G/B (no per-wavelength albedo data exists to do otherwise), so calibrating each filter against *its own* rendered sum forced every one of R/G/B to that same total regardless of the body's actual color, silently erasing it: a green-dominant body like Jool got its naturally-dim R and B channels boosted to match G's total, and the LRGB composite (`AstroImageStack.ComposeLRGB`) ended up showing whatever arbitrary hue survived the remaining per-pixel contrast differences between the three equalized channels, not the body's real color.

Fixed by calibrating every filter (R/G/B/Hα) against the same shared reference, the frame's luminance-weighted sum (`FilterSignal`'s own `Luminance` formula, `0.2126r+0.7152g+0.0722b`), instead of each filter's own channel sum. Each channel is then scaled by its real relative share of that luminance, so R:G:B keeps the body's true color ratio through calibration and into `ComposeLRGB`'s luminance-transfer step, which already assumes it's getting real relative color rather than three independently-normalized channels. The `Luminance` filter's own calibration is unchanged (it already used this same formula for its own sum).

### 7.1 Optics / atmosphere

- **Extinction**: Bouguer's law, same as §5.2, `k=0.20 mag/airmass`, every instrument.
- **Scintillation**: Young (1967), same formula as §5.2, using the active telescope's own real aperture and site altitude (§7.00); extended-source-suppressed per §7.08.
- **Seeing**: for a plain (non-AO) instrument, grows linearly with `(airmass-1)`, capped at the equivalent of 6px. Now expressed as an **angle** (arcsec) rather than a pixel count — seeing is a property of the atmosphere, not of the sensor, so quoting it in pixels made it wrongly depend on plate scale and binning. The airmass response is unchanged; only the unit is corrected, and the conversion happens once at the current plate scale before it reaches the PSF builder. For an AO instrument this term is replaced by the two-component model of §7.013.
- **Defocus**: manual, only when autofocus is off, every instrument. Modelled as the geometrical blur disc of the defocused cone — uniformly illuminated, antialiased at its rim — and convolved into the PSF (§7.11) rather than applied as a separate pass. A flat-topped kernel is physically correct *here specifically*: its transfer function's zeros are why a genuinely defocused image shows contrast reversals.
- **Astigmatism** (not coma), per instrument (`VisualTelescopeSpec.AstigmatismStrengthPxAtCorner`): the radial-quadratic *falloff* (transverse blur scaling with the *square* of the field angle, smeared radially outward from frame center, zero at the centered target and worst near the corners) is the same Seidel-aberration physics for any two-mirror astrograph (coma would scale linearly instead — **Schroeder, *Astronomical Optics* 2nd ed. 2000, ch. 6**; Rutten & van Venrooij, *Telescope Optics*), but the *peak amplitude* depends on how completely each real design cancels off-axis aberrations:
  - **RC20** (3.0px): a true Ritchey-Chrétien (per `Observatories.cs`), and a real RC's whole reason for existing is that its hyperbolic mirror pair cancels third-order coma (**Ritchey & Chrétien 1922**) — giving it coma would misrepresent the optical design it's named after — but astigmatism is the dominant remaining off-axis aberration. No published PlaneWave RC20 optical-prescription number gives the amplitude to the precision needed, so the pixel figure is a display calibration constant, not a measured one.
  - **CDK1000** (0px): PlaneWave's own product page states the Corrected Dall-Kirkham design is "free of off-axis coma, astigmatism, and field curvature" — its corrector cancels both third-order aberrations a bare Dall-Kirkham would have, not just coma the way an RC does. Taking the manufacturer's own flat-field claim at face value, rather than inventing a nonzero residual with no published number behind it.
  - **VLT FORS2** (0px): a real, well-corrected two-mirror Cassegrain system, but no published VLT optical prescription gives a field-dependent astigmatism coefficient to the precision this pipeline's display model would need.
  - **VLT SPHERE** (0px): ZIMPOL's real field of view is only 3.6"×3.6", far too narrow for off-axis astigmatism to grow to any meaningful amplitude regardless of the telescope's own prescription — justified by the field size alone, not just the "no published coefficient" reasoning the other zero entries use.

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

### 7.12 Display transfer function (`SolarSystemCameraTexture.DisplayStretch`)

Selectable **Linear / Log / Asinh**, applied when a finished frame is turned into something the eye can read. **Display only**: `GetLastCaptureFullPrecision`, the FITS export (§7.7) and everything `AstroImageStack` consumes always receive the untouched linear signal — the same separation between viewer and data that every real observing tool keeps. Changing the mode restretches the frame already on screen instead of forcing a new exposure.

No astronomical image is looked at linearly. A resolved planetary disk puts almost all of its pixels into a narrow bright range, so real surface contrast — a few percent of the local level — occupies a handful of the 256 levels an 8-bit display has and is invisible, even though the data holds it perfectly. This is why a physically correct PSF can still produce a frame that reads as featureless: the limitation is the viewer, not the optics. Every real viewer (DS9, PixInsight, IRAF, ESO Reflex) offers exactly this choice.

- **Log** — DS9's own formulation `y = log(a·x + 1) / log(a + 1)` at its default `a = 1000` (Joye & Mandel 2003, ADASS XII, the SAOImage DS9 paper). Strongest lift of faint detail; compresses the bright end hard.
- **Asinh** (default) — Lupton et al. 2004, *PASP* 116, 133, "Preparing Red-Green-Blue Images from CCD Data", the transfer function SDSS's own imagery uses. Linear near zero and logarithmic beyond, so faint structure lifts without crushing bright regions the way a pure log does. The softening parameter (0.02 of full scale) places the turnover just above this pipeline's real noise floor, so genuine faint structure is lifted while the noise itself is not amplified into visible grain.

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

All three noise terms are anchored to the active telescope's own real full well (§7.00: 66,000 e⁻ for the ZWO ASI294MM Pro on RC20/CDK1000, 150,000 e⁻ for FORS2's real CCID20, 640,000 e⁻ for SPHERE's real ZIMPOL CCD), not independently tuned coefficients: `AtmosphericImagingNoise.ShotNoiseSigma(signal, fullWellElectrons) = sqrt(signal/fullWellElectrons)`, a zero-free-parameter consequence of Poisson statistics once a real full well is chosen. `AtmosphericImagingNoise.cs` itself carries no sensor-specific numbers at all; every call site passes in the active spec's own real figures.

- **Shot noise**: `σ = sqrt(signal/fullWellElectrons)`, `signal` the real-photon-flux-calibrated fraction from §7.0, `fullWellElectrons` the current *binned* full well (§7.05).
- **Dark current**: pedestal + same Poisson-scaled noise, using the active telescope's own real per-pixel dark-current rate (§7.00: 0.0022 e⁻/s at -20°C for the ZWO camera, 0.2 e⁻/s at -120°C for FORS2, 0.2 e⁻/s for SPHERE) expressed as a full-well fraction; native (unbinned) full well throughout, per the cancellation noted in §7.05.
- **Read noise**: fixed Gaussian σ = the active telescope's own real read-noise electrons (§7.00: 1.2 e⁻ ZWO, 1.89 e⁻ FORS2, 20 e⁻ SPHERE) over the current *binned* full well, applied *after* the gain stage (not amplified by gain — matches real sensor electronics). A real research CCD's gain (FORS2, SPHERE) is a fixed hardware conversion factor rather than a player-adjustable ISO, so `MinGain == MaxGain == 1.0` on those two entries.
- **Hot/dead pixels**: fixed defect map, seeded once from a constant (`20260721`), same defects every session — 1-in-3000 hot, 1-in-6000 dead. Applied *after* all blur (a read-out-stage defect, not an optical one — shouldn't be softened by seeing/defocus/astigmatism blur the way real scene light is).
- **Full-well blooming**: Pyxel's own model only hard-clips (`min(pixel, fwc)`, no redistribution — verified against their source, `pyxel/models/charge_collection/full_well.py`), so this follows real CCD device physics instead: excess charge above full well (`1.0`) spills along the column/shift-register direction, split symmetrically between the two vertical neighbors — a charge-conserving 50/50 split, the textbook default absent device-specific anti-blooming-gate data (**Janesick 2001, *Scientific Charge-Coupled Devices*, SPIE Press**). Cascades up to 4 iterations (a numerical convergence cap, not a physical quantity — same role as the 50-iteration cap on the Kepler-equation solver elsewhere in this codebase).
- **Charge-transfer smear / CTI**: simplified single-trap-species version of the `nc`/`nr` capture/release structure from **Short et al. (2010)**'s CDM model (Pyxel's `pyxel/models/charge_transfer/cdm.py`). Capture fraction (`1e-4`/row) is calibrated against real measured charge-transfer inefficiency: a fresh CCD sits near `1e-6`/transfer, while HST's ACS/WFC at severely radiation-damaged end-of-life reaches `~1e-4-1e-3`/transfer (**Massey, Stoughton & Rhodes 2010, PASP 122, 1035**) — `1e-4` sits at that damaged-device ceiling, the conservative end of the real range for a healthy amateur sensor. Release fraction (`35%`/row) represents the fast-trap species real CDM models always include alongside slow traps — a trap whose release time is comparable to the transfer period empties within the first few pixels, the short visible trail seen below bright sources in real frames.
- **Cosmic ray hits**: flat Poisson process, isotropic incidence angle, random 2–14px track length, deposits a bright streak. Rate is *derived*, not tuned: sea-level cosmic-ray (mostly muon) flux ≈ 1 cm⁻² min⁻¹ (**Particle Data Group, "Passage of Particles Through Matter" review**; **Grieder 2001, *Cosmic Rays at Earth***), applied to the active telescope's own real, native, binning-independent physical silicon area (the exposed area doesn't change with pixel binning, only how physical pixels are grouped on readout) — a property, not a cached value, since a telescope switch with a different sensor must recompute it rather than keep the previous instrument's rate. RC20 example: side X `= 4144×4.63e-4 cm = 1.919 cm`, side Y `= 2822×4.63e-4 cm = 1.307 cm`, area `= 2.507 cm²`, rate `= 1 cm⁻²min⁻¹ × 2.507 cm² / 60s ≈ 0.0418 hits/s`. Pyxel's own CosmiX/TARS angle model is an unimplemented stub in their shipped source, so isotropic sampling here is no less physical than upstream.
- **Persistence/ghosting: removed.** An earlier version ported Pyxel's default persistence trap model (time constants and species proportions in the spirit of **Fixsen, Offenberg, Hanisch et al. 2000, PASP 112, 1350**), but that model is tuned for HgCdTe near-infrared detectors, a technology known for pronounced image latency — not the ASI294MM Pro's actual sensor, a back-illuminated Sony CMOS (IMX492), a technology whose main advantage over CCD/IR arrays is negligible image lag. There is no published persistence measurement for this real device to source a correct trap-capacity fraction from, and the ported proportions summed to exactly 1.0 (a Pyxel-internal weighting for decomposing an IR array's ghost signal among trap species), which is not a bound on the fraction of the *current* frame that gets subtracted. Treating them as such produced literal runaway darkening: repeated same-target exposures fed the slower (τ=100–10000s) species enough elapsed real-world time between manual shots to keep approaching their share of the signal on every subsequent capture, with no equilibrium below near-total signal loss. Removed rather than re-tuned, since no real number exists to re-tune it to.
- **Gain**: amplifies signal + pre-gain noise together; does not touch read noise. Continuously player-adjustable on RC20/CDK1000 (real ZWO EGain range); fixed at 1.0 on FORS2/SPHERE (§7.5 intro).
- **Filter throughput** (sky-glow/haze scaling relative to Luminance): derived directly from each filter's own real bandwidth ratio against Luminance (`FilterBandwidthAngstrom(filter)/LuminanceBandwidthAngstrom`) rather than a separately-tuned set of constants, so a narrower filter passes proportionally less sky background just as it passes proportionally less of the target's own signal — the same real bandwidth numbers feed both the noise chain and the photon-flux calculation (§7.0), instead of two figures that could silently drift apart.
- **Diurnal drift**: without autoguiding, horizontal motion blur proportional to Kerbin's sidereal rotation rate over the exposure — an untracked mount's classic star-trail smear. Zero on FORS2/SPHERE, where autoguiding is forced on (§7.011).

**Explicitly rejected**: GalSim's brighter-fatter effect (`SiliconSensor`) — its real formula needs per-sensor electrostatic-vertex calibration tables (e2v/ITL-specific) with no generic published values, and none of these instruments do stellar photometry (their targets are extended solar-system bodies, not point-source stars), so the effect's main visual payoff (saturated star-core broadening) barely applies to any of them.

### 7.6 Image stacking (`Visualization/AstroImageStack.cs`)

- Per-filter stacks of up to 30 subs, centroid-based alignment (brightness-weighted, falls back to frame center if nothing exceeds threshold), robust sky-background subtraction (trimmed-median of a 20px border band, trims brightest 15% first to reject limb/hot-pixel contamination).
- **Cosmetic correction (bad-pixel-map)**: every sub is corrected *before* alignment using the sensor's known, fixed hot/dead pixel map — each defect pixel is replaced by the mean of its immediate orthogonal neighbors (excluding any neighbor that's itself a known defect). This is the standard professional calibration step real pipelines run before registration/stacking (PixInsight's `CosmeticCorrection` process, IRAF/ccdproc's `fixpix`, ESO Reflex bad-pixel handling). Doing it before alignment matters: a fixed sensor defect co-added with per-sub alignment shifts would otherwise scatter into a cloud of artifacts at different composite positions instead of being corrected once at its one true location.
- **LRGB composition**: luminance transfer — `R/G/B *= min(4.0, L_stack/rgbLuminance)` — capped to stop noise blow-up at near-zero background. Optional Hα boost into the red channel.
- **Display-only asinh stretch** (never applied to stored data): `arcsinh(k·v)/arcsinh(k)`, `k=5`.
- **Lucky imaging**: each filter's subs ranked by a **variance-of-Laplacian sharpness score** (**Pech-Pacheco et al. 2000**, "Diatom autofocusing in brightfield microscopy" — the top-performing general-purpose focus operator in the **Pertuz, Puig & Garcia 2013** survey), computed over the central 60% of the frame (mirroring AutoStakkert!'s alignment-point "quality box" concept for real planetary lucky imaging, since the RC20 always centers its aim there) with the sharpest-magnitude 2% of Laplacian values trimmed before the variance is taken (robust against an isolated cosmic-ray hit or hot pixel masquerading as a sharp frame — the same trimmed-statistic idiom the background estimator already uses). Only the sharpest 30% of subs are kept before alignment/averaging (mid-range of the 1–60% practical range in the lucky-imaging literature — **Fried 1978** for the underlying theory, **Baldwin et al. 2001** for practical frame-selection fractions). Always forces alignment on when active.
  - *Note on the prior implementation*: an earlier version scored sharpness by raw peak-pixel value. Since blooming, cosmic rays, and hot pixels can all saturate a single pixel anywhere in the frame regardless of actual seeing, that metric was inadvertently selecting artifact-contaminated frames as "sharpest" — corrected to variance-of-Laplacian, which measures genuine local contrast rather than any single pixel's value.

### 7.7 Real FITS export (`Visualization/FitsWriter.cs`)

"Save Photo"/"Save composite" now write a real 16-bit FITS file alongside the PNG preview — the actual format a real telescope+camera setup would produce, not a proprietary/simplified one. Standards-conformant: 80-byte header cards, 2880-byte block padding (header and data), big-endian data regardless of host byte order, and the standard `BZERO=32768`/`BSCALE=1` convention for representing unsigned 16-bit data in FITS's native signed-16-bit (`BITPIX=16`) format. Header keywords match real acquisition-software conventions (SharpCap/NINA/MaximDL): `EXPTIME`, `XPIXSZ`/`YPIXSZ` (real binned pixel pitch), `EGAIN` (full well ÷ 65536), `FOCALLEN`, `GAIN`, `FILTER`, `OBJECT`, `DATE-OBS` — all sourced live from the active telescope's own real spec (§7.00), so a FORS2 or SPHERE frame carries that instrument's own real focal length and full well, not the RC20's.

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
| RC20 | SolarSystemPhotography | n/a | n/a | n/a | 0.51m | 560m (ETH Zürich) | 15,000 | 5 | 50 | 0.0 (no science economy)
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

1. **No real physical link between the star catalog's RA/Dec and Kerbin's sky** — an arbitrary zero-point convention, not real astrometry (`SkyCoordinates.cs`).
2. **No weather simulation** in the generic (exoplanet-instrument) observing forecast — only the solar-system-photography forecast (any of the four instruments, §7.4) factors in real EVE cloud cover, and even that assumes clouds persist unchanged into the future (no forecastable weather model exists to query).
3. **`PrecisionExponent = 0.2` uniform across every instrument** — real magnitude-precision scaling varies by instrument/detector; this is one simplified relation for all.
4. **Career-economy numbers (unlock cost, science threshold, scan cost, reward multiplier, all of `ScienceRewards.cs`) are explicitly unvalidated placeholders** pending playtesting — only their relative ordering is a real design constraint.
5. **Kerbin has no axial tilt** — the Sun's declination is fixed at 0°, so there are no seasons and no day-length variation.
6. **Single-harmonic RV fit underestimates semi-amplitude on eccentric orbits** (real power leaks into higher harmonics) — period recovery stays accurate, amplitude runs low.
7. **BLS transit search has no false-alarm probability calibration** — SNR is relative confidence only, not a statistically calibrated detection significance.
8. **TTV/RM models are order-of-magnitude, single-dominant-perturber approximations** — only the strongest near-resonant pair is modeled; higher-order and secular effects are absent.
9. **Direct-imaging PSF is a Gaussian + ad hoc ring term**, not a true Airy/Bessel diffraction pattern; speckle/background noise is uniform pseudo-noise, not physically-derived photon statistics.
10. **Every solar-system-photography instrument's sensor noise chain is anchored to real electron counts** (a real full well, read noise, and dark current, and a real photon-flux-calibrated signal — §7.0/§7.5), not abstract units — the remaining unanchored constant per instrument is astigmatism's pixel amplitude at the frame corner where a nonzero value is used (RC20 only; no published optical prescription specifies it to the needed precision), flagged individually in §7.1.
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
- Dravins, D., Lindegren, L., Mezey, E. & Young, A. T. (1997). "Atmospheric Intensity Scintillation of Stars I." *PASP* 109, 173. Extended-source (resolved-disk) scintillation suppression relative to a point source.
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

*Reverse-engineering note*: `EveCloudIntegration.cs`'s API was verified by decompiling EVE-Redux 1.11.7.2 with ilspycmd (not a paper, but a real methodological citation worth keeping for the paper's methods section).

---

*Generated as a living document alongside the codebase. If a section here and the code disagree, trust the code and fix this file — that's the whole point of keeping it.*
