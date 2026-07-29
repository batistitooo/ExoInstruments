# Manual test checklist

The `Core` layer is covered by the headless harnesses under `tools/`, which need
no game running. Everything below needs KSP, because it exercises the Unity
render, the GUI, or the interaction between them.

Build and deploy:

```
dotnet build ExoInstruments\ExoInstruments.csproj -c Release
```

The output path in `ExoInstruments.csproj.user` writes straight into
`GameData/ExoInstruments/Plugins/`, so a successful build is a deployed build.

---

## 1. Regression, after the frame memory rework

The capture pipeline changed from four full-frame `Color[]` planes to `float[]`,
the display texture is written as raw bytes, and the Unity render is released
before the optics. None of it changes a computed value, so these must show no
difference at all.

- [ ] **1.1** Capture at 4x4 on the RC20. The image must look identical to
      before the change. Any visible difference is a bug, not an improvement.
- [ ] **1.2** LRGB stacking. Run a batch of 3 or 4 subs per filter and compose.
      This is the most exposed path: `GetLastCaptureGray()` changed
      implementation. A composite that is black, inverted, or wrongly coloured
      points straight at it.
- [ ] **1.3** No grey floor. The bias pedestal is added to the data and
      subtracted for display. If the preview has a uniform grey background where
      it used to be black, the display subtraction is wrong.
- [ ] **1.4** Change the display stretch (linear / log / asinh) on an existing
      capture. It must restretch without needing a new exposure.

## 2. Detector temperature and the cooler

- [ ] **2.1** Select the RC20. A `Cooler` slider appears under `Gain`, with a
      live `e-/px/s` readout beside it.
- [ ] **2.2** Move it from -20 C to 0 C. The dark current readout must rise by a
      factor of about 8.2, and from -20 C to +11.8 C by about 25.
- [ ] **2.3** Select VLT FORS2. The slider is replaced by
      `fixed at -120 C -- cryogenic detector, not an observer control`.
- [ ] **2.4** Switch RC20 -> FORS2 -> RC20. The setpoint resets to the
      instrument's own operating temperature rather than carrying over.

## 3. Hot pixels

Hot pixels are now a multiple of the dark current applied in the charge domain,
not a fixed value stamped after digitisation.

- [ ] **3.1** Capture at 1 s, then at 300 s, same target and gain. Hot pixels
      must be nearly invisible in the first and obvious in the second. They used
      to look identical at any exposure.
- [ ] **3.2** Raise the cooler setpoint to 0 C and repeat the 300 s capture. The
      hot pixels must get visibly worse.

## 4. Calibration frames

Buttons live under the capture controls. They are deliberately not gated on
`canExpose`: the shutter stays closed, so neither night nor target altitude
applies.

- [ ] **4.1** `Save bias frame`, then check it:

      python tools\check_calibration_frames.py "<KSP>\Screenshots\ExoInstruments_bias_*.fits"

      Expect `ALL CHECKS PASSED`. The script reads the frame's own header and
      works out what the pixels must do, so no numbers need to be known in
      advance.

- [ ] **4.2** `Save dark (...)` at the same exposure and gain as a light frame,
      then check it the same way. On FORS2 the dark will look almost like a bias:
      that is correct, and it is why ESO cools that detector to -120 C.

- [ ] **4.3** **The test that validates the whole chain.** Capture a light frame
      at 300 s, save it, then save a dark at 300 s without changing gain,
      binning or cooler setpoint. Then:

      python tools\check_calibration_frames.py --subtract "<light>.fits" "<dark>.fits"

      The hot pixels must disappear. This was impossible before, because hot
      pixels were painted on after digitisation and no dark could remove them.

- [ ] **4.4** Deliberately mismatch the exposures and rerun `--subtract`. The
      script must refuse, reporting the `EXPTIME` mismatch.

## 5. FITS header

- [ ] **5.1** Export a raw sub and confirm the header carries `MAGZERO`,
      `BIASLVL`, `RANDSEED` and `CREATOR`.
- [ ] **5.2** `MAGZERO` should be plausible, roughly 24 to 27 for the RC20 in
      Luminance.
- [ ] **5.3** Read noise from a bias frame is only measurable at **gain 4 or
      above** on the ASI294. At gain 1 the conversion is 4.03 e-/ADU and the read
      noise is 0.30 ADU, below one converter count, so a measurement there
      returns quantisation rather than the amplifier. The checker says so itself.

## 6. Memory and high resolution

- [ ] **6.1** The binning row now reports the estimated cost per capture. At 4x4
      on FORS2 it should read about 59 MB, at 2x2 about 238 MB, at 1x1 about
      951 MB.
- [ ] **6.2** FORS2 at 1x1 previously needed 2193 MB and could close the game
      outright, with no exception and no log entry. Restart KSP first for a clean
      heap, then try 1x1 and capture.
- [ ] **6.3** If the target is missing from a 1x1 frame, read the line under the
      binning control. `the scene render came back empty` means the render
      produced nothing and the body is genuinely absent from the data;
      `X e- from the target, rendered luminance sum Y` with both non-zero means
      the body did reach the signal plane and the fault is further down.
- [ ] **6.4** Check Alt+F12 for `[ExoInstruments] The graphics device refused a
      ... render target`.

## 7. The OD 3.8 filter stop

`tools/nd-filter-audit` found the ND ladder's largest gap, a hundredfold step from ND1000 to the
solar film, and added `Nd6300` (Baader AstroSolar PHOTO Film, OD 3.8) to fill it. That directory's
README has the numbers; these are the in-game checks it implies.

- [ ] **7.1** A fifth toggle, `OD3.8`, appears between `ND1000` and `Solar`. Selecting it takes and
      holds, and the row still fits at the default window width.
- [ ] **7.2** On a bright target, `OD3.8` is visibly dimmer than `ND1000` and visibly brighter than
      `Solar`. If it looks identical to either, the transmission is not reaching the source.
- [ ] **7.3** `MAGZERO` in an exported header shifts by 3.8 mag between no filter and `OD3.8`, since
      the zero point carries the ND transmission.
- [ ] **7.4** The case the stop exists for: RC20, 4x4, high gain, on a bright planet at the default
      0.5 s. `ND1000` should over-expose and `Solar` should read near zero; `OD3.8` should land
      between them.

## 8. PSF changes from the GalSim cross-validation

`tools/galsim-crossvalidation` fixed two numerical defects in `OpticalPsf`. Both change the kernel,
so both need to be looked at rather than assumed.

- [ ] **8.1** The delivered seeing is now 0.45% wider (the Fried constant is measured from the
      profile instead of quoted as 0.98). Too small to see; what must be checked is that captures
      still look normal and no exception appears in Alt+F12 on the first exposure after load, since
      the constant is now computed in a static initialiser.
- [ ] **8.2** The atmospheric quadrature now follows rho, so a kernel at a large radius costs more
      to build. Watch the first capture after switching filter or binning (which invalidates the PSF
      cache): the halo path on SPHERE builds the largest kernel in the game.

## 9. Pointing at anything on the sky

The visual telescopes now aim at a `SkyTarget`: a body, or a fixed right ascension and declination.
`tools/pointing-tests` validates the trigonometry against IAU SOFA to 5.5 microarcseconds, but the
world basis it composes with comes from KSP and only the game can exercise it.

- [ ] **9.1** Click a star on the sky chart. It gets a selection ring, the telescope slews, and the
      right panel shows its name rather than "select a body".
- [ ] **9.2** Capture on that star. The star must land at the CENTRE of the frame. This is the check
      that matters: if the world basis has east and west swapped, the field will be full of the
      wrong stars and still look plausible.
- [ ] **9.3** Enter M42's coordinates by hand (`05 35 17.3` / `-05 23 28`) and capture. Compare the
      field against any star atlas at the same scale.
- [ ] **9.4** Enter the same position in decimal degrees (`83.822` / `-5.391`). Identical frame.
- [ ] **9.5** The observatory model itself must slew for a fixed target, not just for a body: watch
      the dome and tube while switching from a planet to a star.
- [ ] **9.6** Point at something near the celestial pole and something near the horizon, and check
      the reported altitude and azimuth against the sky chart's own marker.
- [ ] **9.7** With autoguiding OFF, two captures minutes apart on a fixed target must show the field
      drifting, and the star trails must run the same way they do on a planet.
- [ ] **9.8** The observing forecast now covers fixed targets. Its band pattern should be a clean
      sidereal repeat, unlike a planet's slowly-shifting one.
- [ ] **9.9** Exported FITS: `OBJECT` carries the target name, and the file name is sanitised.

## 10. Reddening, the dust map, and the narrowband wheel

`tools/reddening-tests`, `tools/dustmap-tests` and `tools/emission-tests` cover the physics
headlessly. These are the parts only the game exercises.

- [ ] **10.1** With the OLD (version 2) catalogue still installed, captures must be unchanged. The
      reddening path is inert without an estimate, and the harness proves that to 2e-16, so any
      visible difference is a wiring bug rather than the new physics.
- [ ] **10.2** Rebuild the catalogue with `tools/pack_gaia_catalog.py` (it now writes version 3 and
      pulls `ag_gspphot`). It reports how many stars have no reddening estimate; toward the plane
      most should have one, at high latitude many will not.
- [ ] **10.3** With version 3 installed, a field in the Galactic plane should show its stars change
      brightness slightly. Alt+F12 for exceptions on the first capture after the swap.
- [ ] **10.4** Build a dust map with `tools/pack_dust_map.py` and install it. The observing panel
      gains a line reporting E(B-V) and A(V) toward the field; point at the Galactic centre and at
      the north Galactic pole and check the two differ by more than an order of magnitude.
- [ ] **10.5** Export a FITS with the map installed: `EBV` and `AV` are present with the map's
      provenance in a `COMMENT`. Without the map installed, both keywords must be ABSENT rather
      than zero.
- [ ] **10.6** The RC20, CDK1000 and RedCat filter rows gain `OIII` and `SII`. FORS2 does not, and
      that is deliberate until its ESO narrowband figures are entered.
- [ ] **10.7** Capture the same target in `Ha`, `OIII` and `SII`. All three are 70 Angstrom wide, so
      a continuum source should come out at similar brightness; what must differ is the sky, which
      is far darker than in Luminance.
- [ ] **10.8** LRGB stacking still composes with the new positions present in the enum.

## 11. Diffuse line emission

`tools/emission-tests` covers the photometry and the per-pixel rotation headlessly. These need the
game and an installed map.

- [ ] **11.1** With no emission map installed, nothing changes anywhere. The deposit returns before
      touching a pixel.
- [ ] **11.2** Build one with `tools/pack_halpha_map.py` from the Finkbeiner composite and install
      it. Alt+F12 should report the line, the nside and the provenance at load.
- [ ] **11.3** Point the RedCat at a bright Galactic H II region and capture in `Ha`. The gas must
      appear. Capture the same field in `Luminance`: the gas should be far weaker against the sky,
      which is the whole point of the narrowband filter.
- [ ] **11.4** Capture in `OIII` and `SII`. Those filters do not contain H-alpha, so the map must
      contribute NOTHING -- the deposit is gated on the filter's own passband.
- [ ] **11.5** The observing panel reports the mean surface brightness in rayleighs, and an exported
      FITS carries `LINEBRIT` and `LINE`.
- [ ] **11.6** **Cost.** This is the only per-pixel source in the pipeline. Time a capture in `Ha`
      at 4x4 and at 1x1 with the map installed, and compare against `Luminance` at the same binning,
      which does not run it at all. If 1x1 is unacceptable, that is a real finding and belongs in
      the README rather than being hidden by sampling the map more coarsely.
- [ ] **11.7** At high magnification the map should look like a smooth gradient rather than
      structure: 6 arcmin is 1300 pixels behind the RC20's Barlow. That is the data's limit and it
      must not be mistaken for a rendering bug.

## 12. Known open item

There is no flat-field model. Pixel response non-uniformity is the dominant
systematic in ground-based photometry of a bright target, and its absence makes
the computed precision optimistic. It is absent rather than approximated because
no manufacturer or observatory publishes a non-uniformity figure for the specific
detectors in this roster. Consequence for testing: the calibration chain is
incomplete, so bias and dark can be produced and subtracted but there is no flat
frame to divide by.
