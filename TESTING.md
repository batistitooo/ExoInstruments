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
      `fixed at -120 C, cryogenic detector, not an observer control`.
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

- [ ] **4.5** `Save flat (screen)` on the **RedCat 51**, then open it. The frame
      must be brightest at the centre and about **0.43 % fainter at the corners**
      — the cosine-fourth falloff of a 250 mm focal length across a 23.2 mm
      diagonal. Repeat on the **RC20**: the same measurement must come out at
      0.0022 %, i.e. flat to the eye. Two instruments, one camera, and the
      difference is entirely the optics.

- [ ] **4.6** `Save flat (screen)` on **FORS2**. Roughly a **third of the frame
      must read as dark**: ESO's MOS unit stops the field at 6.8 arcmin while
      the detector spans 8.5. The edge must be sharp, not a gradient — the stop
      sits in the focal plane. This is the most visible single consequence of
      the calibration work and the fastest way to see it has taken effect.

- [ ] **4.7** Take a flat on the RC20, then a second one without changing
      anything. Divide one by the other. The result must be flat to the photon
      noise: the fixed pattern is *identical* between them, which is what makes
      it removable. If the two flats differ by more than their shot noise, the
      maps are being redrawn per exposure and no calibration is possible.

- [ ] **4.8** Switch binning from 1x1 to 4x4 and repeat 4.7. Nothing about the
      *procedure* changes, but the sensor's non-uniformity does: PRNU falls by
      four and the offset spread rises by four (§7.51). The headless harness
      checks the law numerically; this checks the game path honours it.

The headless companion to all of the above needs no game running:

```
cd tools/calibration-tests
dotnet run -p:Core=../../ExoInstruments/Core -- --out .
./env/bin/python compare_pyxel.py
```

The first must print `ALL CHECKS PASSED`; the second prints the model-by-model
comparison against ESA's Pyxel.

The FITS reader and the measured-flat path have their own harness, with an
astropy cross-validation as the second half:

```
cd tools/flat-tests
dotnet run -p:Core=../../ExoInstruments/Core -- --out .
../env/bin/python compare_astropy.py
```

The first must print `ALL CHECKS PASSED`, the second `EVERY FILE AGREES WITH
ASTROPY EXACTLY`. To check the in-game path, drop a flat at
`GameData/ExoInstruments/PluginData/Flat_<camera>_<filter>_bin<N>.fits` and take
a capture: the log must say the measured flat was loaded and that the modelled
one is not applied on top of it. A deliberately corrupt file must produce an
error naming the reason and must NOT silently fall back to the modelled flat.

The infrared chain (WFC3/IR) has its own harness, which asserts the published
models against the prose statements of the reports that published them:

```
cd tools/infrared-tests
dotnet run -p:Core=../../ExoInstruments/Core
```

Must print `ALL CHECKS PASSED`. In-game, build the orbital telescope with
`instrumentName = Hubble Space Telescope (OTA/IR)` in the part config and check
that: the filter list offers Luminance/Red/Green/Blue and **no** H-alpha; a
colour composite reports that no colorimetric transform is available and is
labelled false colour; and a long exposure taken straight after a heavily
saturated one shows a persistence afterglow that the same exposure taken cold
does not. That last one is the only visible persistence on this roster.

Persistence has its own headless harness, which also guards the sourcing of the
roster's persistence parameters against a number arriving without a citation:

```
cd tools/persistence-tests
dotnet run -p:Core=../../ExoInstruments/Core
```

Must print `ALL PASS`. There is no in-game check to pair with it: the effect is
off on every instrument on the roster, none having a published amplitude
(§7.5166), so the frame path is inert and nothing is visible to look at.

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
- [ ] **10.2** Smoke-test the packer on a cone before committing to a full run:

      python3 tools/pack_gaia_catalog.py --gmax 13 --cone 83.822 -5.391 1.0 --out /tmp/test.starcat

      Verified against the archive on 2026-07-29: 456 sources at G < 13 in that cone, 259 of them
      with `ag_gspphot`. The packer reproduced both exactly, so it neither drops a source nor
      invents an estimate. A different count means the archive changed, not the packer.
- [ ] **10.3** Rebuild the full catalogue (version 3, with `ag_gspphot`). The version 2 `<out>.cache`
      is NOT reusable, so this is a full re-fetch.
- [ ] **10.3b** With version 3 installed, a field in the Galactic plane should show its stars change
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
      contribute NOTHING; the deposit is gated on the filter's own passband.
- [ ] **11.5** The observing panel reports the mean surface brightness in rayleighs, and an exported
      FITS carries `LINEBRIT` and `LINE`.
- [ ] **11.6** **Cost.** This is the only per-pixel source in the pipeline. Time a capture in `Ha`
      at 4x4 and at 1x1 with the map installed, and compare against `Luminance` at the same binning,
      which does not run it at all. If 1x1 is unacceptable, that is a real finding and belongs in
      the README rather than being hidden by sampling the map more coarsely.
- [ ] **11.7** At high magnification the map should look like a smooth gradient rather than
      structure: 6 arcmin is 1300 pixels behind the RC20's Barlow. That is the data's limit and it
      must not be mistaken for a rendering bug.

## 12. The PSF's edge, and the nebula markers

- [ ] **12.1** **The square is gone.** On SPHERE/ZIMPOL, point at a bright star and expose until it
      saturates. There must be no square, no rectangle and no ring around it: the halo now runs to
      the edge of the frame and fades. The measurements it replaces are in
      `tools/psf-truncation/README.md`; the old kernel stopped where the profile was still 3.1e-2
      of its peak, which is what drew the edge.
- [ ] **12.2** Same test on the RC20 and the CDK1000 at 1x1, on a bright star. The old 48 px kernel
      stopped at 1.8e-2 and 6.3e-3 of peak respectively, so a saturated star showed a 97 px square.
      It should now be a smooth falloff.
- [ ] **12.3** **Cost.** The first capture after changing instrument, filter, binning or seeing
      rebuilds the halo spectrum, which measured 463 ms on ZIMPOL. Later captures with the same
      settings must not pay it again; time two captures in a row.
- [ ] **12.4** **The nebulae are on the chart.** Crosses, sized to the object's own extent and
      tinted red for line emitters and blue for reflection nebulae. Hovering names the object and
      says what it is and how big; clicking points the telescope at it. Check IC 1396 (2.8 deg) is a
      far larger cross than NGC 7635 (15').
- [ ] **12.5** **The blocks are gone.** Photograph the Rosette or M42 on the RedCat in `Ha` with a
      log stretch. The nebula must be a smooth gradient, not a mosaic of flat squares 54 pixels
      across. What remains is the 6 arcmin beam of the survey itself, which is real.
- [ ] **12.6** **What "nothing appeared" was.** The observing panel now reports the brightest pixel
      of the diffuse emission in electrons and as a fraction of full well. On the RedCat, M42 in
      `Ha` at 30 s is about 67 e-, i.e. 0.1% of the well and 17 ADU of 16383, correct, and
      invisible in a linear stretch. Check the readout matches roughly what the frame shows, and
      that the RC20 reports far less for the same object (a 55x finer plate scale collects 3000x
      less light per pixel from an extended source).

## 12b. Known open item

There is no flat-field model. Pixel response non-uniformity is the dominant
systematic in ground-based photometry of a bright target, and its absence makes
the computed precision optimistic. It is absent rather than approximated because
no manufacturer or observatory publishes a non-uniformity figure for the specific
detectors in this roster. Consequence for testing: the calibration chain is
incomplete, so bias and dark can be produced and subtracted but there is no flat
frame to divide by.

## 13. Galaxies and the forbidden lines

- [ ] **13.1** **The catalogue loads.** `KSP.log` must carry
      `Galaxy catalogue: <n> galaxies, HyperLEDA (Makarov et al. 2014, A&A 570, A13)`. Without it,
      photographs simply have no galaxies and everything else is unchanged.
- [ ] **13.2** **They are on the chart**, as yellow crosses down to B = 11, sized to their own
      extent. Hovering names the object and says "galaxy" plus its size.
- [ ] **13.3** **M31 at the RedCat.** Click it on the chart, `Luminance`, a few minutes total. It is
      3.2 degrees across against a 4.4 x 3.0 degree field, so it should nearly fill the frame, be
      clearly elongated at the catalogued position angle, and have a bright nucleus. Check the
      readout line `Galaxies in frame: <n> drawn`.
- [ ] **13.4** **The same galaxy at the CDK1000.** The field is 11' x 7.5', so M31's disk covers it
      entirely and the frame should be a smooth gradient with no edge; the search deliberately
      includes galaxies whose centre lies outside the field.
- [ ] **13.5** **An elliptical against a spiral**, e.g. M87 against M101. The elliptical's light must
      be far more concentrated (Sersic n = 4 against n = 1) at a similar total magnitude.
- [ ] **13.6** **[N II] comes with H-alpha.** Photograph M42 or the Rosette in `Ha` on the RedCat.
      The readout must now say which lines the filter admitted; for a 7 nm filter that is
      `[N II] 6548, H-alpha, [N II] 6584`, and report the temperature the ratios were taken at.
- [ ] **13.7** **The [S II] position works.** The same target in `SII` should show the nebula at
      roughly a third of its H-alpha brightness, and nothing at all in `OIII`, `OII` or `OI`, which
      are not derived from an H-alpha map on purpose.
- [ ] **13.8** **The faint gas is redder in [N II].** Compare the reported temperature between a
      bright H II region core and a faint high-latitude field: it must rise as the H-alpha
      brightness falls, which is what makes [N II]/H-alpha rise with it.

## 14. Where black and white are

- [ ] **14.1** **The Elephant's Trunk again.** RedCat, `Ha`, 4x4, 40 s, log. It used to be uniform
      grey fog because the subject occupied 0.4 of 255 display levels. With the new
      `Auto black/white points` toggle on (it is on by default), the same frame should show the
      nebula's gradient across the full display range.
- [ ] **14.2** The readout under the stretch buttons reports the limits, e.g.
      `showing 0.106% to 0.249% of full scale, i.e. a 697x stretch`.
- [ ] **14.3** **Turn it off** and the old behaviour must come back exactly, which is the check that
      it is a viewer control and nothing upstream changed.
- [ ] **14.4** **A saturated star must not flatten the frame.** Photograph a bright star field. With
      auto scaling on, the sky and faint stars stay visible: the white point is set by the sky's own
      noise, not by the brightest pixel.
- [ ] **14.5** **A bright extended subject must not clip.** M42 on the RedCat in `Ha`. The nebula
      must show a gradient with a bright core, not a flat white polygon with straight edges: pure
      zscale put its white point at 329 R against a 5116 R peak and blew out an eighth of the frame.
      The limits readout should now show a white point near the peak.
- [ ] **14.6** **A bright planet must still look right.** Jupiter at a sensible exposure fills the
      converter's range on its own, so the limits should stay wide and the disk should not be blown
      out by the auto scaling.

## 15. The high-resolution patch layer

- [ ] **15.1** **It loads.** `KSP.log` must carry
      `Emission patches: <n> regions at nside 4096 (0.86 arcmin), SHASSA ... on the Finkbeiner ...`.
      Without the file every field is drawn at 6' and nothing else changes.
- [ ] **15.2** **The Horsehead.** Point at `IC 434` (the emission ridge, not `B 33`) with the RC20 or
      the CDK1000 in `Ha`. The frame must show the ridge with structure and the dark cloud beside it,
      not a smooth gradient. The readout must say
      `from the IC 434 Horsehead high-resolution patch at 0.86' sampling`.
- [ ] **15.3** **NOT M42.** Both all-sky H-alpha surveys carry a detector bleed streak through it
      (31% of one cutout row, a bright horizontal spike), and its patch rim disagrees with the base
      map by 392%. The packer warns about it. Use the Lagoon (rim 10%), the Horsehead (8%), the Flame
      (7%) or the Eagle (7%) instead.
- [ ] **15.3b** **M42 at the RedCat.** Same check. The patch covers 1.1 degrees, the RedCat's field is
      4.4 x 3.0, so the frame does NOT fit inside the patch and must fall back to the all-sky map;
      the readout should say so. Use the RC20 or CDK1000 to get inside the patch.
- [ ] **15.4** **A target with no patch**: IC 1396 at +57 degrees is outside SHASSA, must report
      `from the all-sky map at 3.44' sampling (6' beam); no patch covers this field`.
- [ ] **15.5** **No seam.** Frame a patch edge with an instrument whose field is small enough to sit
      just inside, then just outside. The two frames should differ in detail, never show a step.
- [ ] **15.6** **Cost.** Time a capture inside a patch against one outside at the same binning. The
      patch lookup keeps a run cursor, so it should be within a few percent, not multiples.

## 16. Colour as colorimetry

- [ ] **16.1** **Star tints changed subtly.** The chart's and the frames' star colours now come from
      Planck's law through the CIE 1931 standard observer instead of a curve fit. Cool stars are the
      visible difference: a 1500 K point is now a deeper orange-red, not the old pure red.
- [ ] **16.2** **The composite selector.** The stacking box now offers True colour / HOO / SHO
      instead of the "Ha blend strength" slider. Capture R, G and B series of a bright star field
      and compose in True colour: star colours should look like star colours, and the report line
      must state the fitted transform's residuals.
- [ ] **16.3** **A nebula core must hold its colour.** Stack Ha+OIII on M8's core (SHASSA patch
      installed) in HOO. As total integration grows, the core must stay salmon-red rather than
      washing to white; the stretch now applies to luminance only.
- [ ] **16.4** **SPHERE cannot do true colour** (no blue filter exists) and the composite must say
      so rather than produce something.

## 17. Atmospheric dispersion

- [ ] **17.1** **The readout.** Any capture shows `Atmospheric dispersion: X" across this filter at
      z = N deg = M px toward the zenith`. At the zenith it reads ~0; it grows as tan z.
- [ ] **17.2** **Stars at low altitude are tiny spectra.** RC20, `Luminance`, a bright star below
      30 degrees altitude: the PSF should be visibly elongated (~20 px at z = 60), and the same shot
      in `Ha` (7 nm) should be nearly round; the smear scales with the passband width.
- [ ] **17.3** **SPHERE stays sharp.** The same low-altitude shot on SPHERE must show almost no
      elongation, and the readout must say "after the instrument's dispersion corrector".
- [ ] **17.4** **Direction check.** The elongation must point along the line from the target toward
      the zenith in the frame, on an alt-az frame that is "up", rotated by the field rotation.

## 18. The sky's own emission lines

- [ ] **18.1** **The readout.** Any capture shows `Airglow in this band: X R (Y% sky emission
      lines...)`. Luminance at the zenith reads a few R-per-band with ~36% lines.
- [ ] **18.2** **[O I] 6300 is hopeless and now says why.** Select the OI filter: the airglow
      readout should show ~10x the [S II] figure, nearly all lines, the sky glowing in the very
      line the filter isolates.
- [ ] **18.3** **The sky brightens toward the horizon** more slowly than sec z (van Rhijn): compare
      the airglow readout at the zenith and at 60 degrees altitude difference; the ratio should be
      ~1.9, not 2.0.
- [ ] **18.4** **The dark sky is still 21.7.** The `Sky` figure on a moonless night at the zenith
      should read close to 21.7-21.8 mag/arcsec^2, now derived from ESO's measured spectrum plus
      the zodiacal term rather than asserted.

## 19. Registration and the pipelined series

- [ ] **19.1** **The staircase is gone.** RedCat, `Ha`, 8 x 60 s on the Horsehead with **Align subs
      ON**, compose in SHO. The old build registered on the brightness centroid, which on a nebula
      filling the field moved randomly by tens of pixels and stacked the subs as offset rectangles
      with a ragged black border. The composite must now be clean to its edges.
- [ ] **19.2** The report line must read `Registered on the recorded pointing, worst shift N px;
      every sub covers X% of the frame`. With autoguiding ON the shift should be ~0 px and coverage
      100%; with it OFF the shift grows with the series length and coverage drops a few percent.
- [ ] **19.3** **The series pipelines.** Start a series at 120 s and watch the progress line: while a
      frame is being reduced it must say `(reducing the previous frame while this one integrates)`,
      and the wall-clock time for N subs should be about N x 120 s rather than N x (120 s + reduction).
- [ ] **19.4** **Nothing is dropped or double-counted.** A series of 8 must end with exactly 8 subs
      on that filter, and the progress counter must reach 8/8.
- [ ] **19.5** **Cancel mid-series** leaves the subs already collected and stops cleanly.

## 20. Exporting a stack

- [ ] **20.1** **The folder.** Any export writes into
      `KSP/Screenshots/ExoInstruments/<Target>_<Camera>_<timestamp>/`, created on demand. Two sessions
      on the same object with different instruments must land in different folders.
- [ ] **20.2** **Composite** writes 2 files: the colour PNG as displayed, and a FITS of its
      luminance. The filenames carry the palette (TrueColour / HOO / SHOHubble).
- [ ] **20.3** **One per filter.** Capture 10 x `Ha` and 10 x `SII`, compose, then export: exactly
      **4 files**: `..._Ha_stack.fits/.png` and `..._SII_stack.fits/.png`. Each FITS must carry its
      OWN band in `FILTER`/`WAVELNTH`/`BANDWID`, the stack's total in `EXPTIME`, and `NSTACK = 10`.
- [ ] **20.4** **Every sub.** Same stack exported this way gives **40 files**: 20 FITS and 20 PNG,
      numbered `_Ha_sub001` .. `_Ha_sub010`, `_SII_sub001` .. Each carries its own single exposure in
      `EXPTIME` and `NSTACK = 1`.
- [ ] **20.5** **The subs are unregistered.** Open two `sub` FITS from an unguided series in an
      external viewer: the field must be offset between them. That is the point; the registration is
      left to the external package. The `_stack` frames are registered.
- [ ] **20.6** **The PNGs are quick looks, the FITS are the data.** A per-sub PNG is stretched the
      same way the live preview is (zscale + the selected curve); the FITS beside it is linear ADU.
- [ ] **20.7** **Export with no composed image** but with subs held: `Composite` should say to compose
      first, while `One per filter` and `Every sub` must still work.
- [ ] **20.8** **Each sub carries its own WCS.** Open a `_sub` FITS in Siril: `Image Information ->
      Astrometry` must already be populated, and the annotation and coordinate-grid options must be
      available without running a plate solve first. The `_stack` and composite FITS must show no
      astrometric solution at all, which is deliberate (§7.7.1).
- [ ] **20.9** **The pointing is not off by one exposure.** Export every sub of an UNGUIDED series of
      8 and read `CRVAL1` down the numbered files: it must walk monotonically in the direction the
      sky turned, and `sub001`'s value must match the pointing that sub was actually taken at, not
      `sub002`'s. This is the pipelining hazard: the next exposure is gathered before the previous
      one is collected, so a live read of the camera's pointing lands one frame late.

## 15. Dynamic range in the convolution

- [ ] **15.1** **The dark rectangles are gone.** Repeat the exposure that showed them: RedCat 1x1,
      `Ha`, 120 s, on a field with a bright star (05h41m00s -02d12'17\" has Alnitak). Save the sub as
      FITS and check that no region 58-64 px across sits several ADU below its surroundings.
- [ ] **15.2** The same in `SII`, since the defect appeared in both filters at the same pixels.
- [ ] **15.3** **Nothing else moved.** A star's measured magnitude must be unchanged: the transform
      is more precise, not different. Photometry on the same star before and after should agree far
      inside the noise.
- [ ] **15.4** **Cost.** The transform now runs in double precision, about 1.8x the time. Time a
      capture at 1x1 on the RedCat and at 4x4; if either has become unreasonable that is a real
      finding and belongs in the README.

## 16. The stage dump

- [ ] **16.1** Turn on **"Diagnostics: dump every pipeline stage"** in the observing panel, take ONE
      exposure of the field under investigation, then turn it off. It writes one frame-sized file
      per stage to `Screenshots/ExoInstruments/stages/`, about 47 MB each at 1x1, ten stages.
- [ ] **16.2** Read them back:
      `./env/bin/python tools/frame-tests/read_stages.py --dir "<KSP>/Screenshots/ExoInstruments/stages" --x 2100 2150 --y 1600 1740`
      It prints the profile down the column after every stage and flags the one whose relative
      spread jumps, which is the stage that introduced the structure.
- [ ] **16.3** Delete the dumps afterwards; they are not small.

## 17. Continuum-subtraction residuals in the patches

- [ ] **17.1** Photograph the Horsehead field (05h41m00s -02d12'17\") on the RedCat at 1x1 in `Ha`,
      120 s. The black discs centred on the brightest stars must be gone: 795 patch cells of 542,673
      are non-positive SHASSA subtraction residuals, and at 0.86 arcmin a 2x2 clump of them is a
      27-pixel disc.
- [ ] **17.2** No grey patches with staircase edges either. The first attempt at this fell through to
      the base map, which is a different source at a fifteen times coarser beam, and put a 13-pixel
      staircase around every residual over 34,409 pixels. The residuals are now filled from their
      own neighbours instead, so the fine structure runs continuously across them.
- [ ] **17.2b** `KSP.log` reports the count: `... | 795 cells filled from their neighbours:
      continuum-subtraction residuals, not measurements`.
- [ ] **17.3** Same in `SII`, where the residuals appeared identically.
- [ ] **17.4** The other patches carry them too: IC 2177 Seagull has 362 such cells, the worst of
      the fourteen. Worth one frame.

## 21. Galaxies with a real shape

Every check here needs `GalaxyImages.galimg` installed in `PluginData`; build it with
`tools/pack_galaxy_images.py` (see §7.15). Without it nothing below applies and galaxies stay
smooth ellipses, which is what §13 already covers.

- [ ] **21.1** **The maps load.** `KSP.log` must carry
      `Galaxy shape maps: <n> galaxies, shape maps from: ...` naming the surveys. With no file it
      says so instead, and points at the packer.
- [ ] **21.2** **M51 on the RC20**, 4x4, 300 s, `Luminance`. It must show **two spiral arms and the
      companion NGC 5195 north of it**, not a smooth ellipse. This is the whole point of the layer:
      the same frame before it was a featureless smudge peaking at 22 sigma.
- [ ] **21.3** **The readout says where the shape came from**: `... from measured survey imagery
      sampled at X"/px against this frame's Y"/px`. When X is more than about 1.5 Y it also says
      what scale is interpolated rather than measured.
- [ ] **21.4** **The companion is not drawn twice.** NGC 5195 has its own catalogue entry and its own
      chart marker, but it is inside M51's map: there must be exactly one of it, at the right
      brightness, with the tidal bridge between the two.
- [ ] **21.5** **No black discs.** Foreground stars are cut out of the map and filled from their
      surroundings; if any fill shows as a dark disc on an arm, the local inpainting has regressed
      to the azimuthal median (see §7.15).
- [ ] **21.6** **No ghost stars either.** The stars in the frame come from the star catalogue, and
      the map's own foreground stars were removed, so a star must not appear twice, once sharp and
      once soft.
- [ ] **21.7** **The total brightness is still the catalogue's.** Photograph a galaxy with a map and
      one without at similar B_T and D25; their integrated signal must be comparable. A map
      contributes shape only, so it can never change how bright a galaxy is.
- [ ] **21.8** **Colour is real.** Take `Red` and `Blue` subs of a face-on spiral with a two-band map
      and compose them: the arms must come out bluer than the bulge, because the two maps were
      measured in different bands and the renderer interpolates between them.
- [ ] **21.9** **The orientation is right.** Compare any mapped galaxy against a published image at
      the same orientation: a mirrored transform is the failure this is looking for, and it is the
      one that still looks like a galaxy. `tools/galaxy-image-tests` checks it numerically.
- [ ] **21.10** **A galaxy with no map still works.** Nothing about the fallback changed; a frame
      mixing the two must draw both, and the readout must count them separately.

## 22. The target search panel

The search itself is covered headless by `tools/search-tests` (49 checks, including that `M31`
finds the HyperLEDA row for NGC 224 and that career fog is not leaked through the name tables).
Everything below is what needs the game: the panel, the click, and the sky chart reacting.

- [ ] **22.1** Open the observatory with no session running. The right column shows **Find a
      target** with a count of targets in it (about 16 000 with the optional catalogues installed,
      about 14 700 without a galaxy catalogue). If it says "Building the target index..." it must
      resolve within a second or two, and must never freeze the game while it does — the build runs
      on a background task.

- [ ] **22.2** There is **no filter box above the sky chart** any more. The chart's caption reads
      "Click anything on the chart to point at it, or search for it by name on the right."

- [ ] **22.3** Type `M31`. Exactly one result, labelled `M 31 (Andromeda)`, described as a galaxy
      with its D25 and its constellation, sourced to HyperLEDA. Now type `NGC0224`, then `NGC 224`,
      then `Andromeda`: the same single result each time.

- [ ] **22.4** Type `Vega`. The result is the Bright Star Catalogue's `alf Lyr`. This is the IAU
      proper-name table attaching to a catalogue star by HR number; if it comes back as a separate
      "IAU Catalog of Star Names" entry instead, the join failed.

- [ ] **22.5** Type `M13`. It resolves, as a globular cluster, sourced to SIMBAD. There is no
      cluster catalogue in this mod at all — this target exists only because the cross-identification
      table carries a position for it.

- [ ] **22.6** Type `NGC 24`. **NGC 24 is first**, not NGC 247 or NGC 2400. Those are further down
      the list, which is the other half of the check: the longer designations must still be
      reachable.

- [ ] **22.7** Click a result. The telescope points at it, the row gains its `>` marker, and the
      left column shows the target card. For a catalogue star it must also become the selected star,
      so the detection instruments can be started on it.

- [ ] **22.8** With a search running, look at the **sky chart**. Matches are drawn bright and
      ringed; everything else — stars, nebula crosses, galaxy crosses, planet discs — is dimmed. A
      dimmed star is not clickable, which is deliberate and is the pre-existing behaviour.
      Clear the search: everything returns to normal brightness and full clickability.

- [ ] **22.9** Press the **galaxy** filter button. The box gains `type:galaxy`, the list shows only
      galaxies, and the chart lights up *every* matching galaxy, not just the hundred and twenty the
      list draws. Press it again: the token is removed from the box. Type `type:galaxy` by hand and
      the button must show as pressed.

- [ ] **22.10** Type `type:nebula in:Ori`. Only nebulae in Orion. Then `in:Orion` and `in:Orionis`:
      identical results. Then `mag:<9` on its own with the galaxy filter: only galaxies brighter
      than B = 9, and none of unknown brightness.

- [ ] **22.11** Type `alt:>30`. Only targets currently at least 30 degrees up; every row's altitude
      readout agrees. Warp a few hours and watch the list change on its own — the altitudes and this
      filter refresh on the sky chart's own once-a-second cadence.

- [ ] **22.12** Type `type:nebulla`. The panel says so explicitly and lists the filter words it does
      understand. It must not silently ignore the word (which would widen the search) or treat it as
      a name (which would empty it).

- [ ] **22.13** Type `Duna` (or any body of the installed planet pack). It resolves as a planet with
      its radius, and clicking it points the telescope the same way clicking its disc on the chart
      does. Moons resolve as "moon of <planet>".

- [ ] **22.14** **Career only.** In a fresh career, type `Vega` or `51 Peg`. No catalogue star is
      returned under its real name. Type an unscanned star's provisional designation as the chart
      shows it (`Unscanned J2257+2046`) and it is found. Now complete a scan of a star and search
      its real name: it is found, because the index rebuilds on reveal. Galaxies and nebulae are
      findable throughout.

- [ ] **22.15** Resize the game window, including narrow. The panel's results area grows and shrinks
      with the column; the filter buttons stay on one row.

## 23. Capture speed, and that speeding it up changed nothing

A galaxy photograph on the RC20 at 4×4 binning used to take thirty seconds or more. §7.061 of the
technical reference has what that was and what was done. These checks are about the two things that
could have gone wrong: the frame changing, and the game stuttering.

Headless first, from `tools/capture-profile`:

```
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- "" 4 --determinism
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- "" 1 --determinism
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- "" 4 --target 85.24,-2.46 --determinism
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- "" 4 --accuracy
```

- [ ] **23.1** All four exit zero. `--determinism` must report every stage **identical**, not
      close: the emission fill, the galaxy deposit, the PSF kernel and the convolution each run at
      one worker and at the machine's full count, and a single differing value is a failure. It is
      the property the exposure's recorded seed rests on.

- [ ] **23.2** `--accuracy` holds the tiled transform to the direct double-precision sum. The worst
      residual must stay under an ulp of the convolution's own peak, i.e. under what storing the
      result as a float costs anyway.

Then in game:

- [ ] **23.3** Photograph a galaxy on the RC20 at 4×4, L filter. The capture readout now carries a
      **Reduction:** line with the total and the per-stage breakdown, and the same line appears once
      in the log. It should read a few hundred milliseconds to a couple of seconds, not tens of
      seconds. Note which stage leads — that is the answer to "why is this slow" and no longer needs
      a harness to obtain.

- [ ] **23.4** Same shot at 1×1. It is slower, but the **emission fill barely changes** while
      everything else grows: that stage samples once per native pixel whatever the binning, which is
      what makes the sensor's integral right. If it scales with the frame instead, the sub-pixel
      loop has been broken.

- [ ] **23.5** Watch the game's frame rate while a capture reduces. It may dip; it must not freeze.
      One core is deliberately left for KSP (`Core/ParallelWork.cs`), so the reduction never takes
      the whole machine.

- [ ] **23.6** Shoot the same target twice without moving anything, and compare the two FITS files'
      `EXPSEED`. Same seed, same frame, pixel for pixel — including on a machine with a different
      core count, which is the point of 23.1.

- [ ] **23.7** Photograph a field with a bright nebula in it (the Horsehead, through H-alpha) and
      one at high Galactic latitude with none. The nebula field must still show the SHASSA patch's
      structure at its own 0.86 arcmin cells, and the empty field must still show the faint diffuse
      glow rather than nothing: the skipped work is empty *tiles of the convolution*, never a map
      sample.

## 24. The orbital telescope

`tools/spacecraft-tests` covers the physics of §13 headlessly: the visibility geometry, the
zodiacal grid, the earthshine relation, the delivered-PSF table, the limit cycle and the frame
volume, each against a published figure rather than against current behaviour. None of it can
exercise a vessel. Everything below needs KSP, a launch, and in most cases the player standing in
the observatory while the telescope is somewhere else entirely.

Build the **Orbital Astrophysics Observatory** part (Science category, `experimentalScience` node)
onto a probe with an antenna and power, and put it in orbit. Its reaction wheels are part of the
part; nothing else about attitude needs adding, which is deliberate (see the part config).

### The vehicle

- [ ] **24.1** **The part's info panel** in the VAB reads the catalogue, not the config: aperture
      2.40 m, plate scale **0.0396"/px**, pointing stability **0.008" rms**, solar avoidance
      **62.5 deg**, bright-limb avoidance **20.0 deg**, and a frame volume of **268.8 Mbit**. If
      any of those is zero the catalogue lookup failed and the part will not observe at all.

- [ ] **24.2** **The door is a gate, not an animation.** `Open aperture door` in the PAW, and the
      status line goes from `aperture door closed` to `ready, fine pointing`. Watch it *during*
      the clip: the state must stay closed for the whole transit and flip only when the clip
      finishes. A door part way across the pupil is an obstruction of unknown shape, which is
      precisely what the pipeline cannot model.

- [ ] **24.3** **The obstruction check names the part.** Bolt a solar panel, a battery, anything,
      in front of the tube. The status line must read `blocked NN% by <part title>`, and it must
      name the part covering the *most* pupil rather than the first one the rays hit. Move it and
      the reading must return to clear within a second — the check runs at 1 Hz, not per frame.
      The telescope's own tube and door must never count: a correctly built telescope reads 0%.

- [ ] **24.4** **Attitude authority comes from what you built.** With the wheels active the mode
      is `ready, fine pointing`. Disable them and enable RCS: `ready, thruster pointing`. Disable
      both: `no attitude control`, and no observation is possible at all — not a degraded one.

### From the ground

- [ ] **24.5** **The unloaded path.** This is the one that has already been wrong once. Go to the
      space centre with the telescope in orbit and open the observatory: *Orbital Observatory
      (Space Telescope)* must be **selectable**, and the spacecraft picker must list the vessel by
      name with `ready` beside it. A vessel is unloaded whenever you are not flying it, which is
      the whole point of this mode, so a telescope that only appears while you fly it means the
      protovessel read failed. Check `saves/<save>/persistent.sfs`: the
      `ModuleExoSpaceTelescope` node must carry `instrumentName`, `apertureDoorOpen` and
      `controlModeCached`. Without `instrumentName` the registry cannot tell which instrument the
      vessel carries and finds nothing.

- [ ] **24.6** **The two permissions are different.** Fly the telescope: it is commandable with no
      antenna at all. Return to the space centre: it must now read
      `no antenna link: fly the spacecraft, or give it an antenna`. Give it an antenna and the
      readout must show the frame volume and the downlink time — **538 s** on a 500 kbit/s
      Communotron 16, **134 s** on a 2 Mbit/s relay, and twice that at 50% signal.

- [ ] **24.7** **Switching spacecraft discards the photograph**, exactly as switching between two
      ground telescopes does; switching to the *same* one must not. Hold a stack, wait through
      several of the two-second rescans, and confirm the stack survives: the list is rebuilt from
      the save each time and the comparison is by vessel id, not by object reference.

### What the sky allows

- [ ] **24.8** **The avoidance angles refuse, they do not degrade.** Point within **62.5 deg** of
      the Sun, then within **20 deg** of the sunlit limb, then within **9 deg** of a moon. Each
      must block the observation and name the constraint. Then point within 10 deg of the **dark**
      limb: that one must be *allowed*, down to **7.6 deg**. The asymmetry is the whole model —
      scattered sunlight on one side, guidance margin on the other.

- [ ] **24.9** **The orbital window.** The panel reports the occulted fraction of each orbit and
      the longest uninterrupted exposure. Dial an exposure longer than that window: it must warn
      that the planet will cut it off. This is the only orbital constraint that silently ruins a
      frame instead of refusing it.

- [ ] **24.10** **The continuous viewing zone.** Point at something within ~22 deg of your orbit's
      own pole: `Continuous viewing zone: never occulted from this orbit.` An equatorial target
      from the same orbit must report a non-zero occulted fraction. Geometry alone, so a target
      that is never occulted from a low orbit at *any* declination is a sign the orbit normal came
      back zero.

### What the frame looks like

- [ ] **24.11** **The PSF is identical in every frame ever taken.** Photograph the same star twice,
      hours apart, and measure the FWHM. It must not move — there is no atmosphere to vary. On the
      ground the same test gives a different number every time.

- [ ] **24.12** **And it is not the diffraction limit.** WFC3 IHB Table 6.7: **0.067"** at 500 and
      600 nm, rising to **0.070"** at 400 nm and **0.083"** at 200 nm. The climb back into the UV
      is real — it is the primary's mid-frequency polishing figure, and it is why HST is
      diffraction-limited nowhere in this band. A 2.4 m aperture at 500 nm would give 0.044".

- [ ] **24.13** **The near-ultraviolet exists only here.** The Blue filter is **F438W**. No ground
      instrument in this roster carries a filter below 420 nm, so this is the only part of the mod
      where that light is collected at all.

- [ ] **24.14** **The sky is about 1.6 magnitudes darker**, and for a different reason: airglow is
      something an atmosphere does. The readout's sky figure must be zodiacal plus scattered
      planet light, and the zodiacal term must say whether it is published or extrapolated.

- [ ] **24.15** **Cosmic rays, which look like a bug and are not.** The white worms across a long
      exposure are real and there are supposed to be a lot of them. The detector is 4096 x 4102 px
      at 15 um, i.e. **37.8 cm^2**, and the catalogue carries **110 events/min/cm^2** against 1.0
      at Haute-Provence and 7.7 at Paranal — that factor of a hundred is what having no atmosphere
      costs. So 69 events/s, and a **200 s** exposure lays down about 13 900 tracks of 2-13 px,
      touching **~0.6%** of the frame. Each deposits 0.85 of a 63 000 e- well, which is why they
      read out saturated.

      The check that matters is at **1800 s**, where the same arithmetic gives **5.6%** against
      the WFC3 IHB's published "5% to 9% per chip in SAA-free orbits" (Sect. 5.4.10). The event
      rate was derived backwards from that figure, so this is the figure it has to return.

- [ ] **24.16** **They are cosmic rays and not something else.** Three discriminators, because
      three different bugs would also draw bright marks. A **dark** at the same exposure must show
      them too, since the shutter has nothing to do with it. Their orientation must be **random**:
      tracks that all run the same way are pointing drift, which is a different fault entirely.
      And two exposures with nothing changed must place them on **exactly the same pixels**, since
      they are drawn from the exposure's own recorded seed; change the exposure or the target and
      they must redistribute. Moving when nothing changed means the seeding is broken.

- [ ] **24.17** **Stacking is the answer, the same as it is in reality.** Shoot 5 x 200 s rather
      than 1 x 1000 s and compose. The worms must be gone: they fall in different pixels in each
      sub, which is exactly what the sigma-clipped combine rejects. This is why real HST programmes
      split their orbits, and it is the most satisfying single demonstration that the detector
      chain is being simulated rather than decorated.

- [ ] **24.18** **The limit cycle, on thrusters.** Disable the reaction wheels, enable RCS, and
      expose. The pointing readout must switch to `thruster limit cycle`, the rms must rise far
      above the 0.008" the wheels give, and the equivalent PSF blur must grow with it. Stars must
      visibly streak. This is the least-exercised model in §13: the deadband and minimum pulse are
      declared design values with nothing to source them to, so what is being checked here is that
      the regime is *visible at the instrument's own plate scale* — neither invisible nor
      catastrophic.

## 24c. The PSF kernel's cost, and that cutting it changed nothing that matters

A galaxy photograph through the orbital Hubble at 4×4 took **424 s**, of which **411 s was building
the PSF kernel** — the per-stage log from §23 said so directly, which is what it is for. §7.113 of
the technical reference has the four changes and `tools/psf-cost/README.md` has the measurements.

Headless first:

```
cd tools/psf-cost
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- --symmetry
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- --convolve
dotnet run -c Release -p:Core=../../ExoInstruments/Core -- --solve
dotnet run -c Release -p:Core=../../ExoInstruments/Core
```

- [ ] **24c.1** `--symmetry` exits zero. It checks the reflections the sampler folds its grid on
      against the pattern itself: every pupil must be exactly even, and a pupil that *claims* the
      axis or diagonal mirrors must have them to 1e-9. The two Hubble entries must claim neither —
      their three mirror pads sit at 120° and break both — and if one ever starts claiming them,
      the fold will mirror its spikes into quadrants they do not belong in.

- [ ] **24c.1b** `--convolve` exits zero. Composing a PSF's terms goes through
      `FourierConvolution.ConvolveKernels` above a work budget and the direct sum below it; the two
      must agree to about 1e-14 of the peak, which is double precision and not a tolerance to
      loosen.

- [ ] **24c.2** `--solve` exits zero. It replays every bounded delivered-width solve against a
      full-support bisection over the roster, three binnings and twelve sub-bands. Expect several
      minutes: the reference side deliberately builds the kernels the shipped path now avoids.

- [ ] **24c.3** The default report. **Every** column must be better than or equal to the
      `untapered` one beside it — that column is what the kernel was before, so a single number
      worse than its neighbour means accuracy was traded, which is the one thing these changes
      must not do. `max|d|/peak` should be in the 1e-4 range for every pupil except FORS2 at 4×4,
      whose wings are aliased and were before (its pixel spans eighteen ring periods); its
      **core** must stay near 2e-3, not the 8e-2 it was.

- [ ] **24c.4** `tools/spacecraft-tests` still reproduces WFC3 IHB Table 6.7. One row moved when
      the core sampling improved — 1000 nm, 0.0883″ → 0.0892″, toward the converged 0.08924″ — and
      no other should.

Then in game:

- [ ] **24c.4b** Photograph a galaxy on the **RC20** at 4×4 as well. The ground instruments pay a
      second cost Hubble does not: their kernel is the diffraction grid convolved with a Kolmogorov
      profile, which was a direct 257×257 against 183×183 sum. `tools/capture-profile` puts the
      whole reduction at 809 ms where it was 9502 ms; in game it must be seconds, not minutes.

- [ ] **24c.5** Photograph a galaxy through the orbital telescope at 4×4. The **Reduction:** line
      must show `PSF kernel` in the tens of seconds, not the hundreds, and it must no longer be the
      largest stage — `stars + emission` should lead.

- [ ] **24c.6** The same shot at 1×1. This is the case that was worst: the delivered-width solve
      used to run its full bisection there and build three hundred kernels. It must now be *faster*
      than 4×4 for this stage, not slower.

- [ ] **24c.7** A bright star in the same frame. The diffraction spikes must still be there and
      still cross the whole kernel. They are what the 257×257 support exists for, and the wings are
      where the sampling was cut.

- [ ] **24c.8** Switch to the WFC3/IR channel and check the zoom slider. It must have a real range.
      The IR entry was missing `BarlowFactor`, so `MinFovDeg` was `MaxFovDeg / 0` = infinity and
      `Mathf.Clamp` pinned every capture's field of view to it.

## 24d. Operating the telescope from the ground

The three checks §24b used to list as open items were the same hole seen from three sides:
`ApplySpaceTelescopePointing` began with a null check on the PartModule, and a PartModule only
exists while its vessel is loaded, which from the observatory it never is. So choosing a target
moved the camera and nothing else: the spacecraft did not turn, the repoint took no time, and the
battery never moved. `Flight/GroundStation.cs` is what receives a command aimed at an unloaded
vessel; §13.14 of the technical reference has the model.

Headless first:

```
cd tools/slew-tests && dotnet run -c Release -p:Core=../../ExoInstruments/Core
```

64 checks: the published 6 deg/min against the Primer's own "about one hour to go full circle";
the scale transplant (a real-scale install must leave every published figure at exactly 1x, and a
90 deg repoint must cost the same fraction of a Kerbin orbit as HST's does of Earth's);
the two branches of the rest-to-rest profile meeting at the crossover; `FractionOfAngleCovered`
against a numerical integration of the rate profile it is the integral of; Vallado's eclipse
fraction against the mod's (which delegates to `OrbitalVisibility`, so this is what catches a wrong
delegation) and against HST's published ~36 min occultation; and `Advance`/`EnduranceSeconds`
agreeing that advancing by the endurance lands on the reserve.

Then in game, with a telescope in orbit and the observatory panel open at the space centre:

- [ ] **24d.1 Nothing happens without a radio.** Launch the telescope with **no antenna**. It must
      appear in the spacecraft list (it is not broken, it is out of reach) with
      `no antenna link: fly the spacecraft, or give it an antenna` beside it, and clicking a target
      must not start a slew. Add an antenna and the same click must work.

- [ ] **24d.1b The CommNet setting is respected.** Start a save with **CommNet disabled** in the
      difficulty options. The antenna-less telescope must now be commandable: with the setting off
      `Vessel.Connection` is null on every vessel, and reading the link state off it used to report
      every telescope in the game as unreachable with no message that explained why.

- [ ] **24d.2 The repoint takes real time.** Click a target on the far side of the sky. The panel
      must read `Slewing, N min to go` with a progress bar, then `Acquiring guide stars`, then
      `On target and guiding`. At the shipped part's numbers a 90 degree repoint is about 15
      minutes of mission time plus 6.5 of acquisition; warp through it and it must resolve at the
      right instant, not on the wall clock. **It must not cost half an orbit**: the published
      6 deg/min is transplanted through the universe's time scale (§13.65), so on stock Kerbin a
      90 deg repoint is about 4.6 min plus 2.0 of acquisition against a ~29 min orbit. A target
      clear of the limb when clicked must still be clear when the telescope arrives.

- [ ] **24d.2b The blue box does not walk off the star.** Point at a *catalogue* target (a star,
      not a planet), then warp through the whole slew watching the chart. The commanded box must
      stay exactly on the star's marker for the entire manoeuvre. It used to drift: the command was
      stored as the world vector the coordinates mapped to at the click, while the star is redrawn
      from its RA/Dec every frame, so the two came apart and the gap grew with time.

- [ ] **24d.2c Board it mid-slew and it must behave.** Start a large repoint, then fly out to the
      telescope while it is still turning. It must rotate smoothly onto the target and hold, not
      tumble. Two separate faults did this: the command was composed against the root part's
      transform while SAS holds it against `vessel.ReferenceTransform` (test it on a satellite whose
      probe core is NOT the root and not aligned with it, which is where they differ), and the roll
      was left undefined by `FromToRotation`, whose axis vanishes at 180 degrees. Also give it
      manual rotation input while the hold is on: the spacecraft must obey you, not fight you.

- [ ] **24d.3 The chart shows both.** During the slew the sky chart must carry an amber crosshair
      where the boresight is, a blue open box where it is going, and a dotted arc between them.
      The crosshair must move along that arc and turn green when it arrives. The arc is a great
      circle, so on the all-sky oval it should visibly *bend*; a straight line on screen would mean
      the projection is being ignored.

- [ ] **24d.4 You may shoot mid-slew, and it must come out wrong in the right way.** The Capture
      button stays enabled for the whole manoeuvre, and the panel says how far the frame will
      streak at the current rate. Take one: it must be a smeared frame of whatever the boresight is
      sweeping over, NOT a clean frame of the target the telescope has not reached yet. Two things
      make that true and both are worth checking separately: the exposure re-aims at the boresight's
      own coordinates (so the field is the wrong patch of sky), and the slew rate goes into the
      pointing budget as drift (so it streaks). A long exposure early in a big repoint should be
      almost unrecognisable.

- [ ] **24d.5 Turning costs charge.** Watch the battery figure across a large repoint. It must
      fall, and it must fall at the vessel's own reaction wheels' rate (0.75 EC/s for the shipped
      part) on top of the bus draw. Then fly out to the same telescope and slew it by hand with
      SAS: KSP's own wheel billing must take it down at the same rate, because the two have to
      cost the same or the cheap way to move a telescope is to drive out to it.

- [ ] **24d.6 A repoint it cannot finish is refused.** Build one with the telescope, a probe core
      and an antenna and **no solar panels**, and let the battery run down. A large repoint must be
      refused with `not enough charge: the N min slew draws X EC/s and the battery runs out after
      M min`, and the spacecraft must not move. With panels fitted the same command must be
      accepted, because the affordability test is whether the battery outlasts the manoeuvre at the
      *net* rate, not whether it could pay the whole bill up front.

- [ ] **24d.7 The exposure is priced too.** Set a long exposure. The panel must state its cost in
      EC, and when the battery cannot cover it the Capture button must be disabled saying so
      rather than producing a frame.

- [ ] **24d.8 The battery recharges while nobody is watching.** Note the charge, leave the
      observatory, warp several days, and come back. It must have gone **up** toward capacity, at
      the panels' rate scaled by the sunlit fraction of the orbit the panel prints. Stock KSP
      simulates nothing on an unloaded vessel, so without this the first two checks above would be
      a one-way trip to a dead telescope.

- [ ] **24d.9 And it is not billed twice.** Fly the telescope by hand for a while under warp, then
      return to the observatory. The battery must be whatever the game's own simulation left it at,
      not that minus another pass of the ground ledger over the same hour.

- [ ] **24d.10 Re-entering the observatory does not re-command.** Pick a target, leave the space
      centre scene, come back. The telescope must still be on target, not starting the same slew
      again and paying for it a second time.

- [ ] **24d.11 Retargeting mid-slew charges for what is left.** Start a large repoint, and half way
      through click a target near where the boresight has got to. The new slew must be short,
      because it starts from where the vehicle actually is and not from either endpoint.

- [ ] **24d.12 Switching spacecraft commands the new one.** With two telescopes in orbit and a
      target selected, switch between them in the spacecraft list. The one just selected must slew
      to the target rather than sitting wherever it was last sent.

## 24b. Known open items

One thing is declared and not wired, and it will not fail a check above because nothing checks it.
It is listed so it is not mistaken for working:

- **The downlink is a readout only.** The frame volume and the transfer time are computed and
  displayed correctly; nothing waits for them, queues behind them, or is prevented by them. Note
  that this is now the *only* one left of the three that used to be here: electric charge and the
  pointing hold are both wired, and §24d is how they are checked.

## 25. The full-sky chart, and the planet that is the mask

The target-selection chart was rebuilt from a KSC-zenith dome into a full-sky equatorial chart
whose observer is whatever platform is selected, with occlusion drawn as the occluding body
itself at its true angular size. The geometry has a headless harness; the rendering and the
interaction need eyes in game.

Headless first:

    cd tools/skychart-tests && dotnet run -p:Core=../../ExoInstruments/Core

386 checks: the oval's landmark points; the projection round trip (rim included); the parity of the new projection against the old dome's
(known-good in game) local north/east handedness at sites and sky points in both hemispheres,
which is the check that catches a mirrored sky; the finite-distance
occlusion classification (a target IN FRONT of the occluder on the same sight line is Clear, the
partial band is `alpha_occ ± alpha_target`); the closed-form Hammer Jacobian against finite
differences; the 100 m horizon-dip identity; the rendered
terminator's illuminated disc fraction against the Lambert-sphere `(1 + cos i)/2` at three phase
angles; and that the limb glow is brighter on the sunlit limb than the dark one.

In game, in one pass:

1. Ground instrument selected: the chart is the whole sky as the classic 2:1 all-sky oval
   (Hammer), celestial equator along the long axis, poles top and bottom, RA 0h on the rims.
   Half of it is under Kerbin's ground hemisphere; the ground is lit on the day side
   and dark at night, and its edge (the horizon) sweeps across the chart as Kerbin rotates:
   time-warp and watch the mask move while the stars stand still. That is the intended inversion
   of the old chart, where stars swept and the horizon stood still.
2. Stars behind the ground are not drawn, not hovered, not clickable; the search list still
   finds them and reports "N deg below horizon".
3. Select the orbital telescope while it is over the far side of Kerbin from KSC. This is the
   scenario that used to deadlock (everything the old KSC-dome chart showed was exactly the sky
   Kerbin blocked from the spacecraft). Now: Kerbin's disc sits on the chart at ~126 degrees
   across from LKO, everything outside it is selectable, and the disc carries the real
   terminator. Verify the limb glow is visibly wider and warmer on the sunlit limb, and the Sun
   wears its avoidance halo.
4. The Mun from LKO: a star-sized dot at zoom 1 that becomes a real disc with a correct phase as
   you zoom in. If the Mun happens to be transiting Kerbin's disc, it draws ON TOP of it (it is
   in front); if it is behind Kerbin, it is simply absent.
5. Clicking inside Kerbin's disc from orbit selects Kerbin itself as the photography target.
   Clicking the ground hemisphere from KSC does nothing.
6. Jool's moons still declutter into a clickable ring while they are dots; a body drawn at real
   size is never nudged.
7. Orbital status panel, occulted target: "Next window in m:ss", counting down, and roughly
   consistent with the occulted-fraction line next to it. While observable with a finite window:
   "Window closes in". Inside solar avoidance it says the season is the timescale instead of
   giving a countdown.
8. Search rows in orbit read "N deg off the limb" or "occulted, N deg inside the limb";
   `alt:>30` filters on that clearance. On the ground the wording and the filter are unchanged.
9. The ground forecast heatmaps are gone while a space telescope is selected (their model is
   airmass and night) and back when a ground instrument is.
10. Drag and zoom stay smooth with the overlay composited (the drag path gained array lookups
    only, no trigonometry); zoom 15 on the limb shows a soft edge, not a staircase.
11. Ground capture gating is untouched: a target the chart shows just above the true (dipped)
    horizon but below 0 degrees of altitude is still refused by the RC20 gate, the deliberate
    1-degree band documented in TECHNICAL_REFERENCE §8.1.
