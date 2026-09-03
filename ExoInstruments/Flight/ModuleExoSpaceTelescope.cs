using System;
using System.Collections.Generic;
using ExoInstruments.Core;
using UnityEngine;

namespace ExoInstruments.Flight
{
    /// <summary>
    /// The flight-side half of a space telescope: the part the player builds onto a satellite,
    /// and everything about that satellite which decides whether an exposure is possible.
    ///
    /// WHAT THIS MODULE IS RESPONSIBLE FOR, and what it deliberately is not. It answers four
    /// questions about one vehicle at one instant, and it answers them from the vehicle's real
    /// state rather than from flags:
    ///
    ///   1. Is the aperture door open? A telescope's door is not decoration. HST's exists as
    ///      bright-object protection of last resort, to close over the optics if the spacecraft
    ///      ever loses attitude control while the Sun is in reach, and no observation is taken
    ///      through a closed one.
    ///   2. Is anything in front of the aperture? Answered by casting rays across the real open
    ///      annulus of the pupil into the real vessel geometry (see ApertureObstruction), not by
    ///      checking an attachment rule. A player who bolts a solar panel over the tube gets told
    ///      which part is in the way.
    ///   3. Can the vehicle hold an attitude, and how well? Answered by looking at what the
    ///      player actually built: momentum-exchange devices give fine pointing, thrusters give a
    ///      limit cycle, nothing gives no observation at all (see PointingStability).
    ///   4. Is there power, and is there a link? The first gates any exposure; the second gates
    ///      only whether the exposure can be commanded from the ground.
    ///
    /// It does NOT take the photograph. That is SolarSystemCameraTexture's job and it is the same
    /// pipeline the ground instruments use; this module supplies the observer's position, its
    /// pointing budget and its permission to shoot, and the physics downstream neither knows nor
    /// cares that the telescope is on a vessel.
    /// </summary>
    public class ModuleExoSpaceTelescope : PartModule
    {
        /// <summary>
        /// Name in VisualTelescopeCatalog of the instrument this part carries. Set in the part
        /// config, so a second telescope part is a config and a catalogue entry rather than a
        /// second module.
        ///
        /// PERSISTED, unlike an ordinary config-driven field, because SpaceTelescopeRegistry has
        /// to read it off an UNLOADED vessel and KSP writes only persistent fields into a
        /// protovessel's module node. Without it the ground-operations path could not tell which
        /// instrument a saved telescope carried, and so found none at all.
        /// </summary>
        [KSPField(isPersistant = true)]
        public string instrumentName = "Hubble Space Telescope (OTA)";

        /// <summary>
        /// Catalogue name of the OTHER channel this instrument can send the beam to, or empty for
        /// a single-channel telescope. WFC3's Channel Select Mechanism is a mirror that feeds the
        /// UVIS CCDs or the IR array, never both, and switching is a normal on-orbit operation.
        /// Persistent for the same reason instrumentName is.
        /// </summary>
        [KSPField(isPersistant = true)]
        public string alternateInstrumentName = "";

        /// <summary>
        /// Name of the transform whose +Z axis is the optical boresight and whose position is the
        /// centre of the entrance pupil. The occlusion rays start here and the pointing solver
        /// aims this.
        /// </summary>
        [KSPField]
        public string boresightTransformName = "boresight";

        /// <summary>
        /// Where the entrance pupil sits along the part's own +Y axis, metres from the part
        /// origin, used only when the model carries no boresight transform of its own.
        ///
        /// It matters because the occlusion rays start here: begun at the part origin they would
        /// start half-way down the tube, and anything mounted alongside the telescope's own
        /// midpoint would be missed. Set it to the open end of the tube.
        /// </summary>
        [KSPField]
        public float apertureOffsetMeters = 0f;

        /// <summary>
        /// Transform carrying the aperture door's Animation component, and the two clip names on
        /// it.
        ///
        /// TWO NAMED CLIPS RATHER THAN ONE PLAYED BACKWARDS, when the model supplies them. A door
        /// is not necessarily symmetric in time: closing can take a different path from opening,
        /// and a model that supplies two clips is telling you that. A model with only one clip
        /// wants the component's default clip played forwards and backwards instead, and gets it
        /// either by leaving these names empty or simply by not carrying a clip under either name;
        /// the lookup falls back on its own. The shipped model is a one-clip model.
        ///
        /// A part with no animation at all counts as permanently open. That is a telescope
        /// without a door, not a door stuck shut, and it is the right reading for a bare optical
        /// tube.
        /// </summary>
        [KSPField]
        public string animationTransformName = "";
        [KSPField]
        public string animationClipNameOpen = "open";
        [KSPField]
        public string animationClipNameClose = "close";

        /// <summary>Door state, persisted so an UNLOADED vessel can still be asked whether it is open (see SpaceTelescopeRegistry).</summary>
        [KSPField(isPersistant = true)]
        public bool apertureDoorOpen;

        // The next four fields exist for one reason: an unloaded vessel has no colliders to cast
        // rays into and no module instance to ask, and the ground-operations mode has the
        // telescope unloaded by definition. Each is measured while the vessel IS loaded and
        // persisted, so the answer is the last real measurement rather than an assumption. None
        // of them can change while a vessel is unloaded: its geometry is fixed and so is its
        // hardware, which is exactly what makes caching them legitimate here and would not make
        // it legitimate for anything that moves.

        /// <summary>Blocked fraction of the pupil, last measured while loaded.</summary>
        [KSPField(isPersistant = true)]
        public double blockedApertureFractionCached;

        /// <summary>Title of the part blocking it, last measured while loaded.</summary>
        [KSPField(isPersistant = true)]
        public string blockingPartCached = "";

        /// <summary>Attitude control mode, last evaluated while loaded, stored by name.</summary>
        [KSPField(isPersistant = true)]
        public string controlModeCached = "Uncontrolled";

        /// <summary>Control torque and inertia, last evaluated while loaded, for the unloaded limit-cycle estimate.</summary>
        [KSPField(isPersistant = true)]
        public double controlTorqueCached;
        [KSPField(isPersistant = true)]
        public double inertiaCached;

        /// <summary>Whether the player has commanded this telescope to hold its observing attitude.</summary>
        [KSPField(isPersistant = true)]
        public bool pointingHoldEnabled;

        /// <summary>Boresight target, stored as a unit vector in the body-centred inertial frame, so a hold survives a scene change.</summary>
        [KSPField(isPersistant = true)]
        public string pointingTarget = "";

        /// <summary>
        /// Name of the solar-system body being followed, when the target is one, so the direction
        /// can be recomputed rather than frozen. Empty for a catalogue position; see
        /// TryResolvePointingDirection for why the two cases cannot share one stored vector.
        /// </summary>
        [KSPField(isPersistant = true)]
        public string pointingTargetBody = "";

        /// <summary>
        /// A catalogue target's own coordinates, when that is what was commanded. NaN otherwise.
        ///
        /// Each kind of target is kept in the frame it is invariant in: a body by NAME because it
        /// moves, a catalogue position by RA/DEC because that is what does not. Storing a star as
        /// the world vector its coordinates mapped to at the click made the commanded chart marker
        /// visibly drift off the star, which is redrawn from RA/Dec every frame.
        /// </summary>
        [KSPField(isPersistant = true)]
        public double pointingTargetRaDeg = double.NaN;
        [KSPField(isPersistant = true)]
        public double pointingTargetDecDeg = double.NaN;

        // The slew clock. All four are persistent and all four are read and written by
        // GroundStation, which is the only thing that owns them: a repoint commanded from the
        // observatory is a manoeuvre with a start, a duration and an origin, and none of that can
        // live in a PartModule's memory because the vessel it belongs to is unloaded while it runs.

        /// <summary>Boresight direction the current manoeuvre started from, as a world unit vector.</summary>
        [KSPField(isPersistant = true)]
        public string slewFromDirection = "";

        /// <summary>
        /// Where the boresight was really pointing, recorded while the vessel is loaded.
        ///
        /// A repoint is priced and timed on the angle from where the telescope is now, and an
        /// unloaded vessel has nothing to ask. Without this the first command after launch was a
        /// zero-degree slew however far it asked to go. Legitimate to persist for the same reason
        /// the cached geometry above is: an unloaded vessel's attitude does not change.
        /// </summary>
        [KSPField(isPersistant = true)]
        public string lastBoresightDirection = "";

        /// <summary>Universal time the manoeuvre was commanded at.</summary>
        [KSPField(isPersistant = true)]
        public double slewStartUt;

        /// <summary>Planned rest-to-rest rotation time, seconds (see SlewDynamics).</summary>
        [KSPField(isPersistant = true)]
        public double slewManoeuvreSeconds;

        /// <summary>Guide-star acquisition after it, seconds.</summary>
        [KSPField(isPersistant = true)]
        public double slewAcquisitionSeconds;

        /// <summary>
        /// Universal time this vessel's power ledger was last brought up to.
        ///
        /// Persisted because the gap it measures is a gap in which the game itself simulated
        /// nothing: an unloaded vessel's batteries do not move in stock KSP, so the only record of
        /// how long they have been left alone is this timestamp. See GroundStation.Advance.
        /// </summary>
        [KSPField(isPersistant = true)]
        public double powerLedgerUt;

        [KSPField(guiActive = true, guiName = "Telescope", guiActiveEditor = false)]
        public string statusLine = "";

        // Rays cast across the pupil per obstruction check. A hundred samples resolves an obstruction covering
        // one per cent of the aperture, which is the tolerance ApertureObstruction works to, and costs a
        // hundred raycasts a second at the once-a-second cadence below. Raising it buys resolution the
        // tolerance does not use.
        private const int ApertureSampleCount = 100;

        // How far the occlusion rays reach, metres. Long enough to clear any vessel a player will build and
        // short enough that it never reaches another vessel, a station or the ground; an obstruction two
        // hundred metres away is not on this spacecraft.
        private const float ApertureRayLengthMeters = 200f;

        // Obstruction and control checks run at 1 Hz rather than per frame: nothing they measure changes faster
        // than that, and both walk the whole vessel.
        private const float StateRefreshIntervalSeconds = 1.0f;

        private Transform boresight;
        private Animation doorAnimation;
        private float lastStateRefresh = -999f;

        // The door's Animation component: on the named transform when the config gives one, and otherwise
        // anywhere in the model. The search falls back to the whole model rather than giving up, because a part
        // config that names no transform is not necessarily a part with no door; it may simply be a model with
        // one animation and nothing to disambiguate. Returning null means the model genuinely has no animation,
        // which the door logic reads as "no door".
        private Animation FindDoorAnimation()
        {
            if (!string.IsNullOrEmpty(animationTransformName))
            {
                Transform t = part.FindModelTransform(animationTransformName);
                if (t != null)
                {
                    Animation a = t.GetComponent<Animation>() ?? t.GetComponentInChildren<Animation>();
                    if (a != null) return a;

                    Debug.LogWarning("[ExoInstruments] ModuleExoSpaceTelescope: transform '"
                                   + animationTransformName + "' carries no Animation component; "
                                   + "searching the rest of the model.");
                }
            }
            return part.FindModelAnimators().Length > 0 ? part.FindModelAnimators()[0] : null;
        }

        // --- Cached vessel state, refreshed on the interval above --------------------------

        private double blockedApertureFraction;
        private string blockingPartTitle;
        private AttitudeControlMode controlMode = AttitudeControlMode.Uncontrolled;
        private double availableTorqueNm;
        private double vesselInertiaKgM2;

        /// <summary>The instrument this part carries, resolved once from the catalogue.</summary>
        public VisualTelescopeSpec Instrument { get; private set; }

        /// <summary>Its orbital platform block. Null only if a config names a ground instrument, which is a config error.</summary>
        public SpacePlatformSpec Platform => Instrument?.SpacePlatform;

        /// <summary>Fraction of the open pupil currently blocked by the vessel's own structure.</summary>
        public double BlockedApertureFraction => blockedApertureFraction;

        /// <summary>Title of the part doing the blocking, for the message that tells the player what to move. Null when the aperture is clear.</summary>
        public string BlockingPartTitle => blockingPartTitle;

        /// <summary>True when the aperture is open and unobstructed.</summary>
        public bool ApertureClear => apertureDoorOpen && ApertureObstruction.IsClear(blockedApertureFraction);

        /// <summary>How this vehicle is holding its attitude right now.</summary>
        public AttitudeControlMode ControlMode => controlMode;

        /// <summary>World position of the entrance pupil, or the part's own position if the model has no boresight transform.</summary>
        public Vector3d PupilWorldPosition =>
            boresight != null ? (Vector3d)boresight.position : (Vector3d)part.transform.position;

        /// <summary>World-space unit vector along the optical axis.</summary>
        public Vector3d BoresightWorldDirection =>
            boresight != null ? (Vector3d)boresight.forward : (Vector3d)part.transform.up;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            Instrument = FindInstrument(instrumentName);
            if (Instrument == null)
            {
                Debug.LogError("[ExoInstruments] ModuleExoSpaceTelescope: no telescope named '"
                             + instrumentName + "' in VisualTelescopeCatalog; this part will not observe.");
            }
            else if (Instrument.SpacePlatform == null)
            {
                Debug.LogError("[ExoInstruments] ModuleExoSpaceTelescope: '" + instrumentName
                             + "' is a ground instrument and carries no SpacePlatform; this part will not observe.");
                Instrument = null;
            }

            UpdateChannelEvent();

            boresight = part.FindModelTransform(boresightTransformName);
            if (boresight == null) boresight = CreateBoresightTransform();

            doorAnimation = FindDoorAnimation();
            ApplyDoorPoseImmediately();
            UpdateDoorEvents();
            SyncDoorState();

            if (HighLogic.LoadedSceneIsFlight) SpaceTelescopeRegistry.Register(this);
        }

        public void OnDestroy()
        {
            SpaceTelescopeRegistry.Unregister(this);
        }

        // Builds the boresight as an empty child of the part's model, when the model does not supply one. A
        // part config cannot declare a transform, and a composed part built out of stock models has whatever
        // transforms those models happened to ship with, none of them named for an optical axis. Rather than
        // make the module depend on a bespoke model, it makes the transform it needs: at apertureOffsetMeters
        // along the part's +Y, which is the stack axis, oriented so its +Z looks along that axis. A model that
        // DOES carry a boresight transform overrides this entirely, and that is the path a purpose-built
        // telescope model should take, since only the model knows where its own pupil is.
        private Transform CreateBoresightTransform()
        {
            Transform parent = part.FindModelTransform("model") ?? part.transform;

            var go = new GameObject(boresightTransformName + "_generated");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            // Placed and aimed in the PART's frame rather than the model's, because the model may
            // carry its own scale or rotation from the MODEL node and the optical axis is a
            // property of the part, not of whatever mesh was hung on it.
            go.transform.position = part.transform.position + part.transform.up * apertureOffsetMeters;
            go.transform.rotation = Quaternion.LookRotation(part.transform.up, part.transform.forward);
            return go.transform;
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (!HighLogic.LoadedSceneIsFlight || Instrument == null) return;

            SyncDoorState();

            if (Time.time - lastStateRefresh >= StateRefreshIntervalSeconds)
            {
                lastStateRefresh = Time.time;
                RefreshApertureObstruction();
                RefreshAttitudeAuthority();
                CacheAttitudeAuthority();
            }

            if (pointingHoldEnabled) DrivePointing();

            statusLine = BuildStatusLine();
        }

        /// <summary>
        /// The instrument's standing load, drawn while the vessel is loaded.
        ///
        /// Bills for the instrument being powered, which nothing charged for until now. It does NOT
        /// bill for rotation: KSP's own ModuleReactionWheel already debits its RESOURCE rate in
        /// proportion to the torque commanded. GroundStation charges the same rate for a slew at an
        /// UNLOADED vessel, where nothing else would. The exposure is debited whole at the shutter
        /// by TryBillExposure.
        ///
        /// FixedUpdate, not Update: this is a rate against game time, and under warp a frame's
        /// worth of real time would make the load vanish exactly when the player uses it most.
        /// </summary>
        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || Instrument == null || Platform == null) return;
            if (vessel == null || !vessel.loaded || vessel.packed) return;

            double draw = Platform.IdleElectricChargePerSecond * TimeWarp.fixedDeltaTime;
            if (draw > 0.0) part.RequestResource(ElectricChargeId, draw);

            // Keeps the ground ledger's clock current while the game is doing the accounting.
            // Advance bills whatever universal time has passed since this stamp, on the premise
            // that KSP simulated nothing across it, which holds only while unloaded. Without this,
            // an hour flown by hand was billed a second time on the next visit to the observatory.
            powerLedgerUt = Planetarium.GetUniversalTime();

            Vector3d bore = BoresightWorldDirection;
            if (bore.sqrMagnitude > 1e-12)
            {
                Vector3d u = bore.normalized;
                lastBoresightDirection = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                                       "{0:R},{1:R},{2:R}", u.x, u.y, u.z);
            }
        }

        // ------------------------------------------------------------------ aperture door

        // Whether the door is fully out of the light path. Open means FULLY open, not "has started opening": a
        // door part way across the pupil is an obstruction of unknown shape, which is precisely what this
        // pipeline cannot model (see ApertureObstruction). So the state is false for the whole of the transit
        // and becomes true when the clip has finished. A part with no animation counts as permanently open; see
        // animationTransformName.
        private void SyncDoorState()
        {
            if (doorAnimation == null) { apertureDoorOpen = true; return; }
            if (doorAnimation.isPlaying) { apertureDoorOpen = false; return; }
            apertureDoorOpen = doorCommandedOpen;
        }

        // Snaps the door mesh to the pose doorCommandedOpen says it is already in, playing nothing. WHY THIS
        // HAS TO EXIST. Nothing else in the module ever writes the door's pose; the clips are only ever played
        // in response to a player command. So at OnStart the mesh sits in whatever pose it was exported in,
        // which is right exactly once: a freshly placed part whose door was authored closed. Every other entry
        // into a scene got it wrong. a vessel loaded from a save with the door open came back visually shut,
        // and the next "Close" would play the shut animation on an already-shut door an Animation component
        // with Play Automatically ran its clip on load and left the door open while this module still believed
        // it closed, so the first "Open" snapped it shut for one frame before animating Both are the same
        // omission and this fixes both. It also makes the module independent of how the model was authored:
        // whatever pose the mesh arrives in, the saved state wins. Sample() rather than letting the clip run,
        // because this is a correction of the model's pose and not a door movement; the player must never see
        // it happen.
        private void ApplyDoorPoseImmediately()
        {
            if (doorAnimation == null) return;

            // Kill anything Play Automatically started before this ran, or Sample() below would be
            // overwritten on the very next frame by a clip still advancing.
            doorAnimation.Stop();

            // A two-clip model states each pose as the END of its own clip, so the pose wanted is
            // that clip at normalizedTime 1. A one-clip model states both poses as the two ends of
            // the single clip, which is the same convention PlayDoorClip reads.
            string named = doorCommandedOpen ? animationClipNameOpen : animationClipNameClose;
            string clipName = null;
            float time;

            if (!string.IsNullOrEmpty(named) && doorAnimation.GetClip(named) != null)
            {
                clipName = named;
                time = 1f;
            }
            else
            {
                clipName = doorAnimation.clip != null ? doorAnimation.clip.name : null;
                time = doorCommandedOpen ? 1f : 0f;
            }

            if (string.IsNullOrEmpty(clipName)) return;

            AnimationState st = doorAnimation[clipName];
            if (st == null) return;

            st.enabled = true;
            st.speed = 0f;
            st.normalizedTime = time;
            doorAnimation.Sample();
            st.enabled = false;
        }

        /// <summary>What the player last commanded, which is what the door settles to once its clip finishes.</summary>
        [KSPField(isPersistant = true)]
        public bool doorCommandedOpen;

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Open aperture door", active = true)]
        public void OpenApertureDoor()
        {
            doorCommandedOpen = true;
            PlayDoorClip(animationClipNameOpen, forward: true);
            UpdateDoorEvents();
        }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Close aperture door", active = false)]
        public void CloseApertureDoor()
        {
            doorCommandedOpen = false;
            PlayDoorClip(animationClipNameClose, forward: false);
            UpdateDoorEvents();
        }

        [KSPAction("Toggle aperture door")]
        public void ToggleApertureDoorAction(KSPActionParam param)
        {
            if (doorCommandedOpen) CloseApertureDoor(); else OpenApertureDoor();
        }

        // Plays one of the door's clips. When the model supplies a named clip for this direction it is played
        // forwards, which is what a two-clip model means. When it does not, the component's default clip is
        // played forwards to open and backwards to close, which is what a one-clip model means. Both cases are
        // the model telling the module how it was authored.
        private void PlayDoorClip(string clipName, bool forward)
        {
            if (doorAnimation == null) return;

            if (!string.IsNullOrEmpty(clipName) && doorAnimation.GetClip(clipName) != null)
            {
                doorAnimation[clipName].speed = 1f;
                doorAnimation.Play(clipName);
                return;
            }

            string fallback = doorAnimation.clip != null ? doorAnimation.clip.name : null;
            if (string.IsNullOrEmpty(fallback)) return;

            AnimationState state = doorAnimation[fallback];
            state.speed = forward ? 1f : -1f;
            // Starting a reverse play from time 0 would run off the front of the clip and finish
            // instantly; it has to start from the end.
            if (!doorAnimation.isPlaying) state.normalizedTime = forward ? 0f : 1f;
            doorAnimation.Play(fallback);
        }

        private void UpdateDoorEvents()
        {
            bool hasDoor = doorAnimation != null;
            Events["OpenApertureDoor"].active = hasDoor && !doorCommandedOpen;
            Events["CloseApertureDoor"].active = hasDoor && doorCommandedOpen;
        }

        // ------------------------------------------------------------------ obstruction

        // Casts a ray from every sample point across the open pupil along the boresight, and records what
        // fraction is stopped by this vessel's own parts. Hits on THIS part are ignored: the telescope's own
        // tube and its own door surround the pupil by construction, and counting them would report every
        // correctly built telescope as blocked. Hits on any other part are real obstructions whoever put them
        // there.
        private void RefreshApertureObstruction()
        {
            blockedApertureFraction = 0.0;
            blockingPartTitle = null;
            if (Instrument == null) return;

            Vector3 origin = (Vector3)PupilWorldPosition;
            Vector3 direction = (Vector3)BoresightWorldDirection;
            if (direction.sqrMagnitude < 1e-6f) return;
            direction.Normalize();

            // Two axes across the pupil, any pair perpendicular to the boresight: which pair does
            // not matter, since the sample pattern is rotationally symmetric by construction.
            Vector3 right = Vector3.Cross(direction, Vector3.up);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(direction, Vector3.right);
            right.Normalize();
            Vector3 up = Vector3.Cross(right, direction);

            double[] offsets = ApertureObstruction.SampleOffsets(
                Instrument.ApertureMeters, Instrument.SecondaryObstructionFraction, ApertureSampleCount);

            int blocked = 0;
            var counts = new Dictionary<string, int>();
            for (int i = 0; i < ApertureSampleCount; i++)
            {
                Vector3 p = origin + right * (float)offsets[2 * i] + up * (float)offsets[2 * i + 1];
                if (!Physics.Raycast(p, direction, out RaycastHit hit, ApertureRayLengthMeters, PartLayerMask))
                    continue;

                Part hitPart = FlightGlobals.GetPartUpwardsCached(hit.collider.gameObject);
                if (hitPart == null || hitPart == part) continue;

                blocked++;
                string title = hitPart.partInfo != null ? hitPart.partInfo.title : hitPart.name;
                counts.TryGetValue(title, out int n);
                counts[title] = n + 1;
            }

            blockedApertureFraction = ApertureObstruction.BlockedFraction(blocked, ApertureSampleCount);

            // Name the worst offender rather than the first one hit: with several parts in the
            // way, the one covering most of the pupil is the one worth moving.
            int best = 0;
            foreach (KeyValuePair<string, int> kv in counts)
                if (kv.Value > best) { best = kv.Value; blockingPartTitle = kv.Key; }

            blockedApertureFractionCached = blockedApertureFraction;
            blockingPartCached = blockingPartTitle ?? "";
        }

        // Layer 0 only: KSP puts vessel parts on the Default layer, while terrain (15) and scaled-space bodies
        // (10) live elsewhere. Casting against those would report the planet the telescope is looking past as
        // an obstruction, which is a real constraint but a completely different one, handled analytically by
        // OrbitalVisibility rather than by raycasting a scaled-space sphere.
        private static int PartLayerMask => 1 << 0;

        // ------------------------------------------------------------------ attitude

        // Works out how, and whether, this vehicle can hold an attitude, from the hardware on it. The order is
        // the order of preference a real spacecraft has, and for the same reason: momentum-exchange devices
        // point finely and cost only power, thrusters point coarsely and cost propellant, so nothing that has
        // wheels uses thrusters to hold still.
        private void RefreshAttitudeAuthority()
        {
            controlMode = AttitudeControlMode.Uncontrolled;
            availableTorqueNm = 0.0;
            vesselInertiaKgM2 = 0.0;
            if (vessel == null) return;

            vesselInertiaKgM2 = EstimateInertiaKgM2();

            double wheelTorque = 0.0;
            List<ModuleReactionWheel> wheels = vessel.FindPartModulesImplementing<ModuleReactionWheel>();
            if (wheels != null)
            {
                for (int i = 0; i < wheels.Count; i++)
                {
                    ModuleReactionWheel w = wheels[i];
                    if (w == null || w.wheelState != ModuleReactionWheel.WheelState.Active) continue;
                    // The smallest of the three axes: a telescope has to be pointed in every
                    // direction, so the axis with the least authority is the one that decides
                    // whether it can be held.
                    double t = Math.Min(w.PitchTorque, Math.Min(w.YawTorque, w.RollTorque));
                    if (t > 0.0) wheelTorque += t * w.authorityLimiter / 100.0;
                }
            }

            if (wheelTorque > 0.0 && HasElectricCharge())
            {
                controlMode = AttitudeControlMode.MomentumExchange;
                availableTorqueNm = wheelTorque;
                return;
            }

            double rcsThrust = 0.0;
            List<ModuleRCS> thrusters = vessel.FindPartModulesImplementing<ModuleRCS>();
            if (thrusters != null)
            {
                for (int i = 0; i < thrusters.Count; i++)
                {
                    ModuleRCS r = thrusters[i];
                    if (r == null || !r.rcsEnabled || !r.moduleIsEnabled) continue;
                    rcsThrust += r.thrusterPower;
                }
            }

            if (rcsThrust > 0.0 && vessel.ActionGroups[KSPActionGroup.RCS])
            {
                controlMode = AttitudeControlMode.ReactionControl;
                // Torque is thrust times moment arm; the arm is taken as the vessel's own bounding
                // radius, which is what the thrusters are mounted out at on any real design.
                availableTorqueNm = rcsThrust * Math.Max(0.5, VesselRadiusMeters());
            }
        }

        // Writes the measured authority into the persistent fields the unloaded path reads.
        private void CacheAttitudeAuthority()
        {
            controlModeCached = controlMode.ToString();
            controlTorqueCached = availableTorqueNm;
            inertiaCached = vesselInertiaKgM2;
        }

        // A vessel's moment of inertia about its worst axis, kg m^2. Taken as the solid-sphere figure 2/5 M R^2
        // on the vessel's total mass and bounding radius. That is an approximation and it is stated as one: a
        // real spacecraft's inertia tensor depends on where every part sits, and KSP computes one, but it is
        // not exposed on an unloaded vessel and this same number has to be available in both cases. The error
        // is bounded on both sides by the two extreme mass distributions, a point mass at the centre (0) and a
        // thin shell (2/3 M R^2), and the solid sphere sits between them. It enters only the limit-cycle rate,
        // which is itself a coarse regime.
        private double EstimateInertiaKgM2()
        {
            if (vessel == null) return 0.0;
            double massKg = vessel.GetTotalMass() * 1000.0;
            double r = VesselRadiusMeters();
            return 0.4 * massKg * r * r;
        }

        private double VesselRadiusMeters()
        {
            if (vessel == null || vessel.parts == null || vessel.parts.Count == 0) return 1.0;
            Vector3 centre = vessel.CoMD;
            float maxSq = 0f;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null) continue;
                float d = (p.transform.position - centre).sqrMagnitude;
                if (d > maxSq) maxSq = d;
            }
            return Math.Max(0.5, Math.Sqrt(maxSq));
        }

        private bool HasElectricCharge()
        {
            if (part == null) return false;
            part.GetConnectedResourceTotals(ElectricChargeId, out double amount, out double _);
            return amount > 0.01;
        }

        private static int ElectricChargeId => PartResourceLibrary.Instance.GetDefinition("ElectricCharge").id;

        // Commands the vessel's autopilot to put the boresight on the stored target. The rotation handed to SAS
        // is exact rather than iterative: the shortest rotation taking the boresight's current world direction
        // onto the target direction, applied to the vessel's current attitude, IS the attitude at which the
        // boresight is on target. SAS then flies to it with whatever authority the vessel has, which is
        // precisely the behaviour this model wants, since the difference between a vessel that gets there
        // smoothly and one that hunts around the target is the difference the imaging pipeline is going to
        // measure.
        private void DrivePointing()
        {
            if (vessel == null || vessel.Autopilot == null || vessel.Autopilot.SAS == null) return;

            // A PACKED VESSEL CANNOT BE TURNED. KSP stops integrating attitude on rails, which is
            // where the active vessel goes above 4x warp, so SAS does nothing and the manoeuvre
            // would never finish: warping through a slew from inside the spacecraft stalled it
            // forever. The ground station's clock is still running, so the honest thing is to let
            // the modelled manoeuvre carry it and take the attitude back when physics resumes.
            if (!vessel.loaded || vessel.packed)
            {
                hasCommandedRotation = false;   // whatever was locked is stale by the time we return
                return;
            }
            if (!TryResolvePointingDirection(out Vector3d targetDirection)) return;

            Transform reference = vessel.ReferenceTransform;
            Transform bore = boresight;
            if (reference == null || bore == null || targetDirection.sqrMagnitude < 1e-9) return;

            // The player's hands win. Holding an attitude against someone actively steering is a
            // fight neither side can win, and from the cockpit it reads as the spacecraft refusing
            // to respond.
            if (PlayerIsSteering()) return;

            // STABILITY ASSIST SPECIFICALLY, and re-asserted rather than only switched on when the
            // autopilot is off. VesselAutopilot.Enable sets sas.lockedMode = (mode == StabilityAssist),
            // and VesselSAS only drives toward lockedRotation while lockedMode is true. A player who
            // boards with SAS already on in Prograde or Target leaves the autopilot Enabled, so the
            // old check skipped the call, lockedMode stayed false, and every LockRotation below was
            // written to a field nothing read: the telescope sat there doing nothing.
            if (!vessel.Autopilot.Enabled || vessel.Autopilot.Mode != VesselAutopilot.AutopilotMode.StabilityAssist)
            {
                vessel.Autopilot.Enable(VesselAutopilot.AutopilotMode.StabilityAssist);
                hasCommandedRotation = false;   // Enable resets the lock; re-issue it below
            }

            // WHAT SAS ACTUALLY COMPARES AGAINST: VesselSAS holds lockedRotation against
            // vessel.ReferenceTransform.rotation, the control point, not against
            // vessel.transform.rotation, the root part. The two differ by whatever fixed rotation
            // sits between them, so on a satellite whose probe core is not the root and not aligned
            // with it, SAS drove to an offset attitude, this method recomputed from that wrong
            // attitude next frame, and the vessel chased its own error in circles.
            Quaternion boresightRelativeToControl = Quaternion.Inverse(reference.rotation) * bore.rotation;

            // AND THE ROLL HAS TO BE DEFINED. Quaternion.FromToRotation(boresight, target) is *a*
            // rotation carrying one vector onto the other and says nothing about roll: its axis is
            // boresight x target, so the attitude depends on where the boresight is at this instant,
            // and recomputing per frame moved the commanded attitude every frame. That cross product
            // also VANISHES when the two are antiparallel, leaving a repoint near 180 degrees with a
            // numerically arbitrary axis free to flip between frames: the vessel tumbled.
            //
            // Planetarium.Zup.Z is the celestial pole, which rotates with neither the planet nor the
            // vehicle, so the command is a function of the target alone and holds still. It also
            // comes out north-up, the convention the frames state their position angles in.
            Vector3d up = Planetarium.Zup.Z;
            Vector3d t = targetDirection.normalized;
            if (Math.Abs(Vector3d.Dot(up, t)) > 0.999) up = Planetarium.Zup.X;   // target on the pole

            Quaternion boresightTarget = Quaternion.LookRotation((Vector3)t, (Vector3)up);
            Quaternion commanded = boresightTarget * Quaternion.Inverse(boresightRelativeToControl);

            // Re-issued only when it has really moved. The command is stable by construction now,
            // so for a catalogue target this settles to one value and stops; a body target drifts
            // slowly and re-issues as it does.
            if (hasCommandedRotation && Quaternion.Angle(commandedRotation, commanded) < 0.01f) return;

            commandedRotation = commanded;
            hasCommandedRotation = true;
            vessel.Autopilot.SAS.LockRotation(commanded);
        }

        private Quaternion commandedRotation = Quaternion.identity;
        private bool hasCommandedRotation;

        // True while the player is giving rotation input. KSP's own SAS uses a 0.05 threshold on its control-
        // detection, and this matches it rather than inventing a second one.
        private bool PlayerIsSteering()
        {
            if (vessel == null || vessel.ctrlState == null) return false;
            const float Threshold = 0.05f;
            return Math.Abs(vessel.ctrlState.pitch) > Threshold
                || Math.Abs(vessel.ctrlState.yaw) > Threshold
                || Math.Abs(vessel.ctrlState.roll) > Threshold;
        }

        /// <summary>Angle between the boresight and the commanded target, degrees. NaN when nothing is commanded.</summary>
        public double PointingErrorDeg()
        {
            if (!pointingHoldEnabled || !TryResolvePointingDirection(out Vector3d target)) return double.NaN;
            Vector3d bore = BoresightWorldDirection;
            if (bore.sqrMagnitude < 1e-9) return double.NaN;
            return Vector3d.Angle(bore, target);
        }

        // The world direction to hold on this tick. A BODY IS RE-RESOLVED, A CATALOGUE POSITION IS NOT, and the
        // difference is parallax. A star is at infinity: the direction to it does not change as the telescope
        // goes round its orbit, so the vector commanded at the moment the player clicked is still the right one
        // an orbit later. A planet is not: hold the vector that pointed at Jupiter half an orbit ago and
        // Jupiter is no longer in the field. Freezing both would have made solar-system photography quietly
        // drift off target while the readout claimed the hold was good.
        private bool TryResolvePointingDirection(out Vector3d direction)
        {
            direction = Vector3d.zero;

            if (!string.IsNullOrEmpty(pointingTargetBody))
            {
                if (vessel == null || FlightGlobals.Bodies == null) return false;
                CelestialBody body = FlightGlobals.Bodies.Find(b => b != null && b.bodyName == pointingTargetBody);
                if (body == null) return false;
                Vector3d toBody = body.position - vessel.CoMD;
                if (toBody.sqrMagnitude < 1e-6) return false;
                direction = toBody.normalized;
                return true;
            }

            // A catalogue position, resolved through the camera's own equatorial chain rather than
            // from a stored vector, so the direction the vehicle is driven to and the place the
            // chart draws the target are one computation. See pointingTargetRaDeg.
            if (!double.IsNaN(pointingTargetRaDeg) && !double.IsNaN(pointingTargetDecDeg))
            {
                return Visualization.SolarSystemCameraTexture.TryEquatorialDirection(
                    pointingTargetRaDeg, pointingTargetDecDeg, Planetarium.GetUniversalTime(), out direction);
            }

            if (!TryParseDirection(pointingTarget, out direction)) return false;
            direction = direction.normalized;
            return true;
        }

        // NO CommandPointing HERE ANY MORE, deliberately. There used to be two of them on this
        // module, and they wrote pointingHoldEnabled and pointingTarget without touching the slew
        // clock beside them, so a repoint issued through one arrived with slewStartUt still
        // holding the previous manoeuvre's timestamp: an arbitrarily large slew that GroundStation
        // then read as long finished, free, and instantaneous. The whole point of
        // TelescopeCommandState is that these fields are one record with one writer, and
        // GroundStation.CommandDirection / CommandBody are it.

        /// <summary>
        /// Swaps the beam to the other channel. Everything from the detector inwards changes
        /// (filters, plate scale, noise, exposure range); the imaging side reacts through
        /// SetActiveTelescope when the registry republishes the link.
        /// </summary>
        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Switch camera", active = false)]
        public void SwitchChannel()
        {
            VisualTelescopeSpec target = FindInstrument(alternateInstrumentName);
            if (target == null || target.SpacePlatform == null) return;

            string previous = instrumentName;
            instrumentName = alternateInstrumentName;
            alternateInstrumentName = previous;
            Instrument = target;
            UpdateChannelEvent();
        }

        private void UpdateChannelEvent()
        {
            VisualTelescopeSpec alt = FindInstrument(alternateInstrumentName);
            bool has = alt != null && alt.SpacePlatform != null;
            Events["SwitchChannel"].active = has;
            if (has)
                Events["SwitchChannel"].guiName =
                    "Switch camera to " + (string.IsNullOrEmpty(alt.CameraName) ? alt.Name : alt.CameraName);
        }

        [KSPEvent(guiActive = true, guiName = "Release pointing hold", active = true)]
        public void ReleasePointing()
        {
            pointingHoldEnabled = false;
        }

        private static bool TryParseDirection(string s, out Vector3d direction)
        {
            direction = Vector3d.zero;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split(',');
            if (parts.Length != 3) return false;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out double x)) return false;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, ci, out double y)) return false;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, ci, out double z)) return false;
            direction = new Vector3d(x, y, z);
            return direction.sqrMagnitude > 1e-12;
        }

        // ------------------------------------------------------------------ readout

        /// <summary>
        /// The pointing budget for an exposure of the given length on this vehicle, right now.
        ///
        /// The measured body rate is handed in whenever the vessel is loaded, because then KSP is
        /// integrating the real attitude motion and there is nothing to model: what the control
        /// system achieved is observable. On an unloaded vessel the attitude is frozen and
        /// unobservable, so the analytic path runs instead.
        /// </summary>
        public PointingBudget EvaluatePointing(double exposureSeconds, double slewRateArcsecPerSecond = 0.0)
        {
            var inputs = new PointingInputs
            {
                ResidualDriftArcsecPerSecond = slewRateArcsecPerSecond,
                Mode = controlMode,
                ExposureSeconds = exposureSeconds,
                InstrumentJitterArcsecRms = Platform != null ? Platform.PointingJitterArcsecRms : 0.0,
                DeadbandArcsec = Platform != null ? Platform.ThrusterDeadbandArcsec : 0.0,
                MinimumPulseSeconds = Platform != null ? Platform.MinimumControlPulseSeconds : 0.0,
                ControlTorqueNm = availableTorqueNm,
                InertiaKgM2 = vesselInertiaKgM2,
            };

            if (vessel != null && vessel.loaded && !vessel.packed)
            {
                double radPerSec = vessel.angularVelocity.magnitude;
                inputs.HasMeasuredRate = true;
                inputs.MeasuredRateArcsecPerSecond = radPerSec * (180.0 / Math.PI) * 3600.0;
            }

            return PointingStability.Evaluate(in inputs);
        }

        private string BuildStatusLine()
        {
            if (Instrument == null) return "instrument not configured";
            if (!apertureDoorOpen) return "aperture door closed";
            if (!ApertureObstruction.IsClear(blockedApertureFraction))
                return string.Format("blocked {0:P0}{1}", blockedApertureFraction,
                                     blockingPartTitle != null ? " by " + blockingPartTitle : "");
            switch (controlMode)
            {
                case AttitudeControlMode.MomentumExchange: return "ready, fine pointing";
                case AttitudeControlMode.ReactionControl: return "ready, thruster pointing";
                default: return "no attitude control";
            }
        }

        private static VisualTelescopeSpec FindInstrument(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            VisualTelescopeSpec[] all = VisualTelescopeCatalog.All;
            for (int i = 0; i < all.Length; i++)
                if (string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase)) return all[i];
            return null;
        }

        public override string GetInfo()
        {
            VisualTelescopeSpec spec = FindInstrument(instrumentName);
            if (spec == null || spec.SpacePlatform == null) return "Instrument not found in catalogue.";

            SpacePlatformSpec p = spec.SpacePlatform;
            return string.Format(
                "Aperture: {0:F2} m\n" +
                "Plate scale: {1:F4} \"/px\n" +
                "Pointing stability: {2:F3} \" rms\n" +
                "Solar avoidance: {3:F1} deg\n" +
                "Bright-limb avoidance: {4:F1} deg\n" +
                "Frame: {5}\n\n" +
                "Requires a clear aperture, an open door, attitude control, and electric charge. " +
                "Operating it from the space centre also requires a working antenna link.",
                spec.ApertureMeters,
                spec.NativePixelSizeMeters / spec.FocalLengthMeters * (180.0 / Math.PI) * 3600.0,
                p.PointingJitterArcsecRms,
                p.SunAvoidanceAngleDeg,
                p.BrightLimbAvoidanceAngleDeg,
                TelemetryBudget.DescribeBits(p.FullFrameBits));
        }
    }
}
