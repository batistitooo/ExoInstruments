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

            if (!link.HasCommLink)
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
        }

        /// <summary>
        /// What the sky and the spacecraft are doing right now, for the selected telescope: the
        /// orbital analogue of the ground panel's "night, target at 42 degrees, airmass 1.5".
        /// </summary>
        private void DrawOrbitalStatusPanel(SpaceTelescopeLink link)
        {
            if (link == null || link.Instrument == null) return;

            GUILayout.Space(4);

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

        private static string DescribeControlMode(AttitudeControlMode mode)
        {
            switch (mode)
            {
                case AttitudeControlMode.MomentumExchange: return "reaction wheels";
                case AttitudeControlMode.ReactionControl: return "thruster limit cycle";
                default: return "uncontrolled";
            }
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
