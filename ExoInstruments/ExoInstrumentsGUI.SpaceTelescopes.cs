using System;
using System.Collections.Generic;
using ExoInstruments.Core;
using ExoInstruments.Flight;
using ExoInstruments.Visualization;
using UnityEngine;

namespace ExoInstruments
{
    /// <summary>
    /// The observatory panel's orbital half: choosing which spacecraft to observe through, and
    /// telling the player why it will or will not work.
    ///
    /// WHY IT IS ITS OWN FILE AND ITS OWN SELECTOR. Every other instrument in the roster is a
    /// single fixed thing: pick "VLT UT1 + FORS2" and there is exactly one of it, standing on
    /// exactly one mountain, and nothing about it can be in the wrong state. A space telescope is
    /// a class of instrument that the player may own zero, one or several of, each on a different
    /// spacecraft, in a different orbit, in a different state of repair. So selecting the
    /// instrument is not enough; a second choice has to be made, and the reasons a particular
    /// spacecraft cannot be used are things the player did and can undo, which means they have to
    /// be stated rather than merely refused.
    /// </summary>
    public partial class ExoInstrumentsGUI
    {
        /// <summary>
        /// The live panel, so the static unlock predicate can ask it whether the player owns a
        /// telescope. Set in Awake and cleared in OnDestroy; null outside the space centre scene,
        /// which every caller handles.
        /// </summary>
        internal static ExoInstrumentsGUI Instance { get; private set; }

        internal void BindInstance() => Instance = this;
        internal void UnbindInstance() { if (Instance == this) Instance = null; }

        /// <summary>Vessel id of the telescope currently selected, so the choice survives the list being rebuilt.</summary>
        private Guid selectedTelescopeId = Guid.Empty;

        /// <summary>Rebuilt on a timer rather than per frame: it walks every vessel in the save.</summary>
        private List<SpaceTelescopeLink> cachedTelescopes;
        private float telescopeScanTime = -999f;
        private const float TelescopeScanIntervalSeconds = 2.0f;

        /// <summary>Every orbital telescope in the save, rescanned at most every couple of seconds.</summary>
        private List<SpaceTelescopeLink> AvailableTelescopes
        {
            get
            {
                if (cachedTelescopes == null || Time.realtimeSinceStartup - telescopeScanTime > TelescopeScanIntervalSeconds)
                {
                    cachedTelescopes = SpaceTelescopeRegistry.FindAll();
                    telescopeScanTime = Time.realtimeSinceStartup;

                    // Every telescope, not only the selected one: a ledger that advanced only for
                    // the one in use would make the way to keep a telescope charged be to stop
                    // using it. Catch-up is by stored timestamp, so this stays cheap.
                    for (int i = 0; i < cachedTelescopes.Count; i++) GroundStation.Advance(cachedTelescopes[i]);
                }
                return cachedTelescopes;
            }
        }

        /// <summary>The telescope the player has selected, or null.</summary>
        private SpaceTelescopeLink SelectedTelescope
        {
            get
            {
                if (selectedTelescopeId == Guid.Empty) return null;
                List<SpaceTelescopeLink> all = AvailableTelescopes;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Vessel != null && all[i].Vessel.id == selectedTelescopeId) return all[i];
                return null;
            }
        }

        /// <summary>
        /// True when the player owns at least one telescope in orbit. This, and not a Funds
        /// price, is what unlocks the orbital instrument row (see Observatories.OrbitalObservatory).
        /// </summary>
        private bool HasAnyOrbitalTelescope => AvailableTelescopes.Count > 0;

        /// <summary>
        /// Whether an orbital instrument may be commanded from where the player currently is.
        ///
        /// THE TWO CASES ARE GENUINELY DIFFERENT and this is the requirement that made the
        /// telemetry model worth having. Flying the spacecraft, the player is there: the exposure
        /// needs power and a clear aperture and nothing else. Sitting in the observatory at the
        /// space centre, every command and every returned frame has to travel over a radio link,
        /// so a telescope with no antenna, or one whose antenna currently has no path home, can
        /// be looked at in the vessel list and not used.
        /// </summary>
        private bool CanCommand(SpaceTelescopeLink link, out string reason)
        {
            reason = null;
            if (link == null) { reason = "no telescope selected"; return false; }

            if (!link.Operational) { reason = link.BlockingReason; return false; }

            bool flyingIt = HighLogic.LoadedSceneIsFlight
                         && FlightGlobals.ActiveVessel != null
                         && link.Vessel != null
                         && FlightGlobals.ActiveVessel.id == link.Vessel.id;
            if (flyingIt) return true;

            // GroundStation, not link.HasCommLink, because a save with CommNet switched off has a
            // null Connection on every vessel and would report every telescope as unreachable. The
            // setting is the player's answer to whether radio range is a constraint at all.
            if (!GroundStation.HasCommandPath(link))
            {
                reason = "no antenna link: fly the spacecraft, or give it an antenna";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The spacecraft picker, drawn under the observatory selector whenever the selected
        /// instrument is an orbital one.
        /// </summary>
        private void DrawSpacecraftSelector()
        {
            List<SpaceTelescopeLink> all = AvailableTelescopes;

            GUILayout.Space(4);
            GUILayout.Label("Spacecraft:");

            if (all.Count == 0)
            {
                GUILayout.Label("No telescope in orbit. Build one with the Orbital Astrophysics "
                              + "Observatory part and launch it.");
                ApplySelectedTelescope(null);
                return;
            }

            // Default to the first usable one rather than to the first one: on a save with a
            // dead telescope still in orbit, defaulting to the wreck is not helpful.
            if (SelectedTelescope == null)
            {
                SpaceTelescopeLink best = null;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Operational) { best = all[i]; break; }
                selectedTelescopeId = (best ?? all[0]).Vessel.id;
            }

            for (int i = 0; i < all.Count; i++)
            {
                SpaceTelescopeLink link = all[i];
                if (link.Vessel == null) continue;

                bool isCurrent = link.Vessel.id == selectedTelescopeId;
                CanCommand(link, out string reason);

                GUILayout.BeginHorizontal();
                string label = (isCurrent ? "> " : "  ") + link.VesselName;
                if (GUILayout.Button(label, GUILayout.Height(22)))
                {
                    selectedTelescopeId = link.Vessel.id;
                }
                GUILayout.FlexibleSpace();
                GUILayout.Label(reason ?? "ready");
                GUILayout.EndHorizontal();
            }

            ApplySelectedTelescope(SelectedTelescope);
            DrawOrbitalStatusPanel(SelectedTelescope);
        }

        /// <summary>
        /// Points the imaging pipeline at the chosen spacecraft. Idempotent, so it is safe to
        /// call from the draw loop.
        /// </summary>
        private void ApplySelectedTelescope(SpaceTelescopeLink link)
        {
            SpaceTelescopeLink current = ObservingPlatform.ActiveSpaceTelescope;

            // COMPARED BY VESSEL, NOT BY REFERENCE. The list is rebuilt from the save every couple
            // of seconds, so the same telescope arrives as a fresh object each rescan; comparing
            // references would see a change every time and throw away the player's photograph and
            // their whole stack twice a minute. What matters is whether the SPACECRAFT changed.
            Guid currentId = current != null && current.Vessel != null ? current.Vessel.id : Guid.Empty;
            Guid newId = link != null && link.Vessel != null ? link.Vessel.id : Guid.Empty;

            if (currentId != newId)
            {
                // The frame in hand belongs to whichever telescope took it: a different aperture,
                // plate scale and orbit make it a different instrument, exactly as switching
                // between two ground telescopes does. Same treatment as SelectObservatory gives.
                solarSystemCamera.DiscardCapturedPhoto();
                ClearAstroStack();
            }

            // The link object itself is always refreshed even when the vessel is unchanged: it
            // carries the position, power and link state, all of which move.
            ObservingPlatform.SetSpaceTelescope(link);

            // THE SPACECRAFT DECIDES WHICH INSTRUMENT IS IMAGING, NOT THE DROPDOWN.
            //
            // Two selections used to set this independently and nothing reconciled them. The
            // observatory row set SolarSystemCameraTexture.Spec, and it is hardwired to
            // HubbleWfc3Uvis; the part config set SpaceTelescopeLink.Instrument, from its own
            // instrumentName. They agree only as long as the roster holds one orbital row, which
            // is an accident of the current catalogue and not a fact anything checked. Fly a part
            // declaring "Hubble Space Telescope (OTA/IR)" and the frame was still computed with
            // the UVIS CCDs' QE curve, read noise, full well and cosmic-ray rate, silently: the
            // whole point of carrying a second entry for the IR channel is that everything from
            // the detector inwards differs, so the wrong one is not a small error.
            //
            // The hardware in orbit is the authority. SetActiveTelescope early-returns on an
            // unchanged spec, so this stays safe to call from the draw loop.
            if (link != null && link.Instrument != null)
                solarSystemCamera.SetActiveTelescope(link.Instrument);

            // Selecting a target commands whichever telescope was active then; switching leaves
            // the new one pointed wherever it was last sent. Guarded on the vessel actually
            // changing because this runs from the draw loop, and AlreadyCommanded makes it
            // idempotent anyway.
            if (currentId != newId && link != null && selectedPhotographyTarget.HasTarget)
                ApplySpaceTelescopePointing();
        }

        /// <summary>
        /// What the sky and the spacecraft are doing right now, for the selected telescope: the
        /// orbital analogue of the ground panel's "night, target at 42 degrees, airmass 1.5".
        /// </summary>
        private void DrawOrbitalStatusPanel(SpaceTelescopeLink link)
        {
            if (link == null || link.Instrument == null) return;

            GUILayout.Space(4);

            // The power state comes first and is not gated on having a target: a telescope whose
            // panels do not cover its bus load is one to fix rather than to plan with.
            DrawPowerReadout(link);
            DrawPointingReadout(link);

            if (!solarSystemCamera.TryBuildOrbitalConditions(selectedPhotographyTarget, out SpaceConditionsSnapshot c))
            {
                GUILayout.Label("Select a target to see the observing constraints.");
                return;
            }

            GUILayout.Label(c.Observable
                ? "Target observable."
                : "Cannot observe: " + (c.BlockingConstraint ?? "constraint violated"));

            GUILayout.Label(string.Format(
                "Limb angle {0:F1} deg ({1}), Sun {2:F1} deg",
                c.Host.LimbAngleDeg, c.Host.LimbIsSunlit ? "sunlit" : "dark", c.SunAngleDeg));

            GUILayout.Label(string.Format(
                "Sky {0:F2} mag/arcsec^2 (zodiacal {1:F2}{2})",
                c.SkyVMagPerArcsec2, c.ZodiacalVMagPerArcsec2,
                c.ZodiacalIsPublished ? "" : ", extrapolated"));

            if (!double.IsNaN(c.OccultedOrbitFraction))
            {
                if (c.OccultedOrbitFraction <= 0.0)
                {
                    GUILayout.Label("Continuous viewing zone: never occulted from this orbit.");
                }
                else
                {
                    GUILayout.Label(string.Format(
                        "Occulted {0:P0} of each orbit; longest uninterrupted exposure {1}.",
                        c.OccultedOrbitFraction, FormatDuration(c.MaxContiguousExposureSeconds)));
                }
            }

            // When the orbit is what blocks (or will block) the target, say when that changes:
            // the exact predicate root-found along the vessel's own Keplerian orbit. The solar
            // avoidance cone is excluded on purpose; it moves with the season, not the orbit.
            if (c.InsideSunAvoidance)
            {
                GUILayout.Label("Inside solar avoidance: this clears with the season, not with the orbit.");
            }
            else
            {
                // Root-finding 192 samples along the orbit is not per-OnGUI-pass work: the
                // change time is a physical instant, so it is computed once and counted down.
                double nowUt = Planetarium.GetUniversalTime();
                Guid vesselId = link.Vessel != null ? link.Vessel.id : Guid.Empty;
                bool stale = double.IsNaN(visibilityChangeUt)
                    || visibilityChangeTarget != selectedPhotographyTarget
                    || visibilityChangeVesselId != vesselId
                    || visibilityChangeWasObservable != c.Observable
                    || nowUt >= visibilityChangeUt
                    || nowUt < visibilityChangeComputedUt;
                if (stale)
                {
                    visibilityChangeUt = TryComputeVisibilityChangeSeconds(link, selectedPhotographyTarget,
                            c.Observable, out double secondsUntilChange)
                        ? nowUt + secondsUntilChange : double.NaN;
                    visibilityChangeComputedUt = nowUt;
                    visibilityChangeTarget = selectedPhotographyTarget;
                    visibilityChangeVesselId = vesselId;
                    visibilityChangeWasObservable = c.Observable;
                }
                if (!double.IsNaN(visibilityChangeUt))
                {
                    GUILayout.Label(c.Observable
                        ? string.Format("Window closes in {0}.", FormatDuration(visibilityChangeUt - nowUt))
                        : string.Format("Next window in {0}.", FormatDuration(visibilityChangeUt - nowUt)));
                }
            }

            // The exposure the player has dialled in, against the window the orbit allows. This
            // is the one constraint that silently ruins a frame rather than refusing it, so it
            // is called out rather than left to be inferred from two numbers.
            double exposure = solarSystemCamera.ExposureSeconds;
            if (!double.IsInfinity(c.MaxContiguousExposureSeconds)
                && exposure > c.MaxContiguousExposureSeconds)
            {
                GUILayout.Label(string.Format(
                    "Exposure of {0} is longer than the window: the planet will cut it off.",
                    FormatDuration(exposure)));
            }

            PointingBudget p = solarSystemCamera.LastPointingBudget;
            if (p.TotalArcsecRms > 0.0)
            {
                GUILayout.Label(string.Format(
                    "Pointing {0:F3}\" rms ({1}), blurring the PSF by {2:F3}\".",
                    p.TotalArcsecRms, DescribeControlMode(p.Mode), p.EquivalentFwhmArcsec));
            }

            SpacePlatformSpec platform = link.Instrument.SpacePlatform;
            if (platform != null && link.DownlinkBitsPerSecond > 0.0)
            {
                double seconds = TelemetryBudget.DownlinkSeconds(
                    platform.FullFrameBits, link.DownlinkBitsPerSecond, link.SignalStrength);
                GUILayout.Label(string.Format(
                    "Frame {0}, downlink {1} at {2:P0} signal.",
                    TelemetryBudget.DescribeBits(platform.FullFrameBits),
                    FormatDuration(seconds), link.SignalStrength));
            }
        }

        /// <summary>
        /// Where the telescope is looking, where it was told to look, and how long until the two
        /// agree. Both coordinates rather than just the error, because "off by 43 degrees" says
        /// nothing about which way the spacecraft is facing.
        /// </summary>
        private void DrawPointingReadout(SpaceTelescopeLink link)
        {
            PointingReadout r = GroundStation.Readout(link);
            if (!r.HasCommand)
            {
                GUILayout.Label("Boresight: nothing commanded. Click a target to slew the spacecraft onto it.");
                return;
            }

            if (ObservingPlatform.TryGetEquatorialFrame(Planetarium.GetUniversalTime(),
                    out ObservingPlatform.EquatorialFrameSnapshot frame))
            {
                frame.WorldToEquatorial(r.CurrentDirection, out double raNow, out double decNow);
                frame.WorldToEquatorial(r.CommandedDirection, out double raCmd, out double decCmd);
                GUILayout.Label(string.Format("Boresight {0}   commanded {1}",
                                              FormatRaDec(raNow, decNow), FormatRaDec(raCmd, decCmd)));
            }

            // The acquisition has to be unmistakable. It begins the moment the boresight ARRIVES,
            // so the marker snaps onto the target and a countdown keeps running, which reads as a
            // stuck timer unless the readout says in as many words that the turning is over and
            // what is left is the guidance locking on.
            string phase;
            switch (r.Phase)
            {
                case GroundPointingPhase.Slewing:
                    phase = string.Format("Slewing, {0} of turning left", FormatDuration(r.SecondsRemaining - r.AcquisitionRemaining));
                    break;
                case GroundPointingPhase.Acquiring:
                    phase = string.Format("Pointed. Not turning any more: the fine guidance sensors are locking "
                                        + "onto their guide stars, {0} left of the {1} that takes.",
                                          FormatDuration(r.SecondsRemaining), FormatDuration(r.AcquisitionSeconds));
                    break;
                default:
                    phase = "On target and guiding.";
                    break;
            }

            GUILayout.Label(double.IsNaN(r.ErrorDeg)
                ? phase
                : string.Format("{0}  Off by {1}, field half-width {2}.",
                                phase, FormatAngle(r.ErrorDeg), FormatAngle(r.ToleranceDeg)));

            // The shutter is not locked during a repoint, so say what a frame taken now comes out
            // as. The streak is exposure times rate, the same product the pipeline convolves with.
            if (r.SlewRateDegPerSecond > 0.0)
            {
                double streakDeg = r.SlewRateDegPerSecond * solarSystemCamera.ExposureSeconds;
                GUILayout.Label(string.Format(
                    "Turning at {0}/s: you can still shoot, but a {1} exposure will streak {2}.",
                    FormatAngle(r.SlewRateDegPerSecond), FormatExposure(solarSystemCamera.ExposureSeconds),
                    FormatAngle(streakDeg)));
            }

            if (r.Phase != GroundPointingPhase.OnTarget) DrawProgressBar(r.SlewProgress);
        }

        /// <summary>
        /// The battery, what the pending exposure would cost it, and how long it lasts. The panel
        /// half that turns "the capture button is greyed out" into a number the player can act on.
        /// </summary>
        private void DrawPowerReadout(SpaceTelescopeLink link)
        {
            SpacePlatformSpec platform = link.Instrument.SpacePlatform;
            if (platform == null) return;

            double generation = GroundStation.ElectricChargeGenerationPerSecond(link.Vessel);
            double sunlit = GroundStation.SunlitOrbitFraction(link);
            double idle = GroundStation.IdleDrawPerSecond(link);
            double slewDraw = GroundStation.SlewDrawPerSecond(link);
            double net = generation * sunlit - idle;

            GUILayout.Label(string.Format(
                "Battery {0:F0}/{1:F0} EC. Panels {2:F2} EC/s over {3:P0} of the orbit in sunlight, "
              + "bus draw {4:F2} EC/s: {5}{6:F2} EC/s net.",
                link.ElectricCharge, link.ElectricChargeCapacity,
                generation, sunlit, idle, net >= 0.0 ? "+" : "", net));

            if (net < 0.0)
            {
                double endurance = OrbitalPowerBudget.EnduranceSeconds(
                    link.ElectricCharge, 0.0, generation, sunlit, idle);
                GUILayout.Label(string.Format(
                    "Losing charge: flat in {0} unless it gets more panels.", FormatDuration(endurance)));
            }

            // Stated even when affordable: knowing a repoint across the sky is most of a battery
            // is the difference between planning an observing run and finding out afterwards.
            if (slewDraw > 0.0)
            {
                SlewProfile halfSky = GroundStation.Plan(link, 90.0);
                GUILayout.Label(string.Format(
                    "Slewing draws {0:F2} EC/s on top: a 90 deg repoint is {1} and {2:F0} EC.",
                    slewDraw, FormatDuration(halfSky.ManoeuvreSeconds),
                    GroundStation.SlewChargeUnits(link, in halfSky)));
            }

            double exposureCost = GroundStation.ExposureChargeUnits(link, solarSystemCamera.ExposureSeconds);
            if (exposureCost > 0.0)
            {
                GUILayout.Label(string.Format(
                    "This exposure costs {0:F1} EC{1}",
                    exposureCost,
                    exposureCost > link.ElectricCharge ? ", which the battery cannot cover." : "."));
            }
        }

        /// <summary>
        /// Draws the boresight and its target on the sky chart, with the arc between them.
        ///
        /// The chart is where the player picks the target, so it is where they look to see whether
        /// the telescope got there. The arc is worth drawing because the shortest path across an
        /// all-sky projection is not a straight line on screen.
        ///
        /// Orbital observers only: a ground telescope's mount is already tracked by the observatory
        /// model in the scene behind the window.
        /// </summary>
        void DrawBoresightOverlay(Rect chartRect)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!ObservingPlatform.IsSpaceBased) return;

            SpaceTelescopeLink link = ObservingPlatform.ActiveSpaceTelescope;
            PointingReadout r = GroundStation.Readout(link);
            if (!r.HasCommand) return;

            if (!ObservingPlatform.TryGetEquatorialFrame(Planetarium.GetUniversalTime(),
                    out ObservingPlatform.EquatorialFrameSnapshot frame)) return;

            var view = new SkyChartView { Zoom = skyChartZoom, Pan = skyChartPan };
            Color previous = GUI.color;

            // The arc first, so the markers sit on top of it.
            if (r.Phase != GroundPointingPhase.OnTarget && r.CurrentDirection.sqrMagnitude > 1e-12)
            {
                GUI.color = new Color(1f, 0.85f, 0.35f, 0.55f);
                const int ArcSamples = 24;
                for (int i = 1; i < ArcSamples; i++)
                {
                    Vector3d p = GreatCircleSample(r.CurrentDirection, r.CommandedDirection, i / (double)ArcSamples);
                    if (TryChartPoint(frame, view, chartRect, p, out Vector2 dot))
                        GUI.DrawTexture(new Rect(dot.x - 1f, dot.y - 1f, 2f, 2f), Texture2D.whiteTexture);
                }
            }

            // Where it should be: an open box, the same shape the chart already uses for a
            // selection, in the cool colour the panel uses for commanded things.
            //
            // Plotted from the coordinates when there are any: projecting the commanded RA/Dec is
            // the identical call the chart makes for the star, so the box lands exactly on it.
            // Going through a world vector and back added a round trip that drifted.
            bool haveCommanded = !double.IsNaN(r.CommandedRaDeg) && !double.IsNaN(r.CommandedDecDeg)
                ? TryChartPointEquatorial(view, chartRect, r.CommandedRaDeg, r.CommandedDecDeg, out Vector2 commanded)
                : TryChartPoint(frame, view, chartRect, r.CommandedDirection, out commanded);
            if (haveCommanded)
            {
                GUI.color = new Color(0.45f, 0.85f, 1f, 0.95f);
                DrawOpenBox(commanded, 9f);
            }

            // Where it is: a crosshair, warm while it is still moving and green once it is
            // guiding, because "may I take the picture yet" is the question this answers.
            if (r.CurrentDirection.sqrMagnitude > 1e-12
                && TryChartPoint(frame, view, chartRect, r.CurrentDirection, out Vector2 current))
            {
                GUI.color = r.Phase == GroundPointingPhase.OnTarget
                    ? new Color(0.45f, 1f, 0.55f, 0.95f)     // guiding
                    : r.Phase == GroundPointingPhase.Acquiring
                        ? new Color(1f, 0.95f, 0.55f, 0.95f) // arrived, locking on
                        : new Color(1f, 0.75f, 0.25f, 0.95f);// still turning
                DrawCrosshair(current, 7f);
            }

            GUI.color = previous;
        }

        /// <summary>A direction's place on the chart, in IMGUI screen coordinates, or false when it falls outside the drawn rect.</summary>
        private static bool TryChartPoint(ObservingPlatform.EquatorialFrameSnapshot frame, SkyChartView view,
                                          Rect chartRect, Vector3d worldDirection, out Vector2 point)
        {
            point = Vector2.zero;
            if (worldDirection.sqrMagnitude < 1e-12) return false;

            frame.WorldToEquatorial(worldDirection, out double raDeg, out double decDeg);
            return TryChartPointEquatorial(view, chartRect, raDeg, decDeg, out point);
        }

        /// <summary>The same, for a position already in equatorial coordinates: the chart's own projection with nothing in front of it.</summary>
        private static bool TryChartPointEquatorial(SkyChartView view, Rect chartRect,
                                                    double raDeg, double decDeg, out Vector2 point)
        {
            Vector2 proj = SkyChartTexture.ProjectEquatorialToScreen(raDeg, decDeg, SkyChartWidth, SkyChartHeight, view);

            // The texture's origin is bottom-left and IMGUI's is top-left, the same flip
            // TryHitBodyMarker applies so that a click and a marker agree.
            point = new Vector2(chartRect.x + proj.x, chartRect.y + (SkyChartHeight - proj.y));
            return chartRect.Contains(point);
        }

        private static void DrawCrosshair(Vector2 c, float radius)
        {
            const float Thickness = 1.5f;
            const float Gap = 2.5f;
            GUI.DrawTexture(new Rect(c.x - radius, c.y - Thickness * 0.5f, radius - Gap, Thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(c.x + Gap, c.y - Thickness * 0.5f, radius - Gap, Thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(c.x - Thickness * 0.5f, c.y - radius, Thickness, radius - Gap), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(c.x - Thickness * 0.5f, c.y + Gap, Thickness, radius - Gap), Texture2D.whiteTexture);
        }

        private static void DrawOpenBox(Vector2 c, float half)
        {
            const float T = 1.5f;
            GUI.DrawTexture(new Rect(c.x - half, c.y - half, half * 2f, T), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(c.x - half, c.y + half - T, half * 2f, T), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(c.x - half, c.y - half, T, half * 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(c.x + half - T, c.y - half, T, half * 2f), Texture2D.whiteTexture);
        }

        /// <summary>
        /// A point along the great circle joining two directions: the same interpolation
        /// GroundStation slews along, so the arc is the path the boresight really takes.
        /// </summary>
        private static Vector3d GreatCircleSample(Vector3d from, Vector3d to, double t)
        {
            Vector3d a = from.normalized, b = to.normalized;
            double dot = Vector3d.Dot(a, b);
            if (dot > 1.0) dot = 1.0; else if (dot < -1.0) dot = -1.0;
            double omega = Math.Acos(dot);
            if (omega < 1e-9) return b;
            double sin = Math.Sin(omega);
            Vector3d v = a * (Math.Sin((1.0 - t) * omega) / sin) + b * (Math.Sin(t * omega) / sin);
            return v.sqrMagnitude > 1e-12 ? v.normalized : b;
        }

        /// <summary>A plain filled bar. IMGUI has no progress widget and the stacking panel draws its own the same way.</summary>
        private static void DrawProgressBar(double fraction)
        {
            Rect r = GUILayoutUtility.GetRect(220f, 10f, GUILayout.Width(220), GUILayout.Height(10));
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.15f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(0.4f, 0.8f, 1f, 0.95f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01((float)fraction), r.height), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        /// <summary>Sexagesimal right ascension and degrees of declination, the way a finding chart states a position.</summary>
        private static string FormatRaDec(double raDeg, double decDeg)
        {
            double raHours = ((raDeg % 360.0) + 360.0) % 360.0 / 15.0;
            int h = (int)raHours;
            double minutes = (raHours - h) * 60.0;
            int m = (int)minutes;
            double s = (minutes - m) * 60.0;

            char sign = decDeg < 0.0 ? '-' : '+';
            double ad = Math.Abs(decDeg);
            int d = (int)ad;
            double arcminutes = (ad - d) * 60.0;
            int am = (int)arcminutes;
            double asec = (arcminutes - am) * 60.0;

            return string.Format("{0:00}h{1:00}m{2:00.0}s {3}{4:00}d{5:00}'{6:00}\"", h, m, s, sign, d, am, asec);
        }

        /// <summary>An angle in whichever unit keeps it readable: degrees down to arcminutes down to arcseconds.</summary>
        private static string FormatAngle(double deg)
        {
            if (double.IsNaN(deg)) return "-";
            if (deg >= 1.0) return string.Format("{0:F2} deg", deg);
            if (deg >= 1.0 / 60.0) return string.Format("{0:F1}'", deg * 60.0);
            return string.Format("{0:F1}\"", deg * 3600.0);
        }

        private static string DescribeControlMode(AttitudeControlMode mode)
        {
            switch (mode)
            {
                case AttitudeControlMode.MomentumExchange: return "reaction wheels";
                case AttitudeControlMode.ReactionControl: return "thruster limit cycle";
                default: return "uncontrolled";
            }
        }

        // Cache for the visibility-change instant (see DrawOrbitalStatusPanel).
        private double visibilityChangeUt = double.NaN;
        private double visibilityChangeComputedUt = double.NaN;
        private SkyTarget visibilityChangeTarget;
        private Guid visibilityChangeVesselId;
        private bool visibilityChangeWasObservable;

        /// <summary>
        /// Seconds until the target's orbital visibility flips, found on the exact geometry:
        /// the observer propagated along its own Keplerian orbit (getRelativePositionAtUT, the
        /// same propagation the game itself uses between SOI changes), the host, Sun and moons
        /// propagated with GetBodyPositionAtUt, the same occultation/limb/moon predicate the
        /// live gate applies, sampled over one orbit and bisected to the second.
        /// </summary>
        private bool TryComputeVisibilityChangeSeconds(SpaceTelescopeLink link, SkyTarget target,
                                                       bool currentlyObservable, out double seconds)
        {
            seconds = 0.0;
            if (link == null || link.Vessel == null || !target.HasTarget) return false;
            Orbit orbit = link.Vessel.orbit;
            CelestialBody host = link.Vessel.mainBody;
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (orbit == null || host == null || sun == null) return false;
            double period = orbit.period;
            if (!(period > 0.0) || double.IsInfinity(period)) return false;

            SpacePlatformSpec platform = link.Instrument != null ? link.Instrument.SpacePlatform : null;

            Vector3d fixedTargetDir = Vector3d.zero;
            if (!target.IsBody)
            {
                if (solarSystemCamera == null
                    || !solarSystemCamera.TryResolveWorldDirection(target, out fixedTargetDir)) return false;
            }
            else if (target.Body == null || target.Body == host)
            {
                return false;
            }

            double nowUt = Planetarium.GetUniversalTime();

            bool ObservableAt(double ut)
            {
                Vector3d hostPos = GetBodyPositionAtUt(host, ut);
                Vector3d observer = hostPos + ConvertOrbitVectorToWorld(orbit.getRelativePositionAtUT(ut));
                Vector3d dir = target.IsBody
                    ? (GetBodyPositionAtUt(target.Body, ut) - observer).normalized
                    : fixedTargetDir;

                Vector3d sunPos = GetBodyPositionAtUt(sun, ut);
                LimbGeometry limb = OrbitalVisibility.EvaluateLimb(
                    ObservingPlatform.ToSkyPosition(observer - hostPos),
                    ObservingPlatform.ToSky(dir), host.Radius,
                    ObservingPlatform.ToSky(sunPos - hostPos));
                if (limb.Occulted) return false;
                if (platform != null)
                {
                    double avoidance = limb.LimbIsSunlit
                        ? platform.BrightLimbAvoidanceAngleDeg : platform.DarkLimbAvoidanceAngleDeg;
                    if (limb.LimbAngleDeg < avoidance) return false;

                    if (platform.MoonAvoidanceAngleDeg > 0.0 && host.orbitingBodies != null)
                    {
                        foreach (CelestialBody moon in host.orbitingBodies)
                        {
                            if (target.IsBody && moon == target.Body) continue;
                            Vector3d toMoon = GetBodyPositionAtUt(moon, ut) - observer;
                            double dist = toMoon.magnitude;
                            if (dist < 1.0) continue;
                            double edge = OrbitalVisibility.SeparationDeg(
                                    ObservingPlatform.ToSky(dir), ObservingPlatform.ToSky(toMoon))
                                - OrbitalVisibility.AngularRadiusDeg(moon.Radius, dist);
                            if (edge < platform.MoonAvoidanceAngleDeg) return false;
                        }
                    }
                }
                return true;
            }

            const int Samples = 192;
            double step = period / Samples;
            double previousUt = nowUt;
            for (int i = 1; i <= Samples; i++)
            {
                double ut = nowUt + i * step;
                if (ObservableAt(ut) != currentlyObservable)
                {
                    double lo = previousUt, hi = ut;
                    for (int k = 0; k < 24 && hi - lo > 1.0; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (ObservableAt(mid) != currentlyObservable) hi = mid; else lo = mid;
                    }
                    seconds = hi - nowUt;
                    return seconds > 0.0;
                }
                previousUt = ut;
            }
            return false;
        }

        private static string FormatDuration(double seconds)
        {
            if (double.IsInfinity(seconds)) return "unlimited";
            if (double.IsNaN(seconds) || seconds < 0.0) return "-";
            if (seconds < 60.0) return string.Format("{0:F0} s", seconds);
            if (seconds < 3600.0) return string.Format("{0:F0} min", seconds / 60.0);
            return string.Format("{0:F1} h", seconds / 3600.0);
        }
    }
}
