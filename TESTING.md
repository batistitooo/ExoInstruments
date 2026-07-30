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
- [ ] **15.3** **NOT M42.** Both all-sky H-alpha surveys carry a detector bleed streak through it (
      31% of one cutout row, a bright horizontal spike), and its patch rim disagrees with the base
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
      **4 files** -- `..._Ha_stack.fits/.png` and `..._SII_stack.fits/.png`. Each FITS must carry its
      OWN band in `FILTER`/`WAVELNTH`/`BANDWID`, the stack's total in `EXPTIME`, and `NSTACK = 10`.
- [ ] **20.4** **Every sub.** Same stack exported this way gives **40 files** -- 20 FITS and 20 PNG,
      numbered `_Ha_sub001` .. `_Ha_sub010`, `_SII_sub001` .. Each carries its own single exposure in
      `EXPTIME` and `NSTACK = 1`.
- [ ] **20.5** **The subs are unregistered.** Open two `sub` FITS from an unguided series in an
      external viewer: the field must be offset between them. That is the point -- the registration is
      left to the external package. The `_stack` frames are registered.
- [ ] **20.6** **The PNGs are quick looks, the FITS are the data.** A per-sub PNG is stretched the
      same way the live preview is (zscale + the selected curve); the FITS beside it is linear ADU.
- [ ] **20.7** **Export with no composed image** but with subs held: `Composite` should say to compose
      first, while `One per filter` and `Every sub` must still work.
