using System;
using System.Collections.Generic;
using ExoInstruments.Core;
using UnityEngine;

namespace ExoInstruments.Flight
{
    /// <summary>
    /// Operating a telescope that nobody is flying.
    ///
    /// A PartModule exists only while its vessel is loaded, and every command the observatory
    /// issues goes to a vessel that is not: the player is at the space centre and the telescope is
    /// over the far side of the planet. So the module cannot be what receives the command, and
    /// until this file existed nothing was. Choosing a target moved the camera and nothing else.
    ///
    /// A command costs three things, all enforced here:
    ///
    ///   a radio link   no antenna with a path home means no command. Not a broken telescope; one
    ///                  somebody has to fly out to.
    ///   time           a rest-to-rest manoeuvre at the vehicle's own torque, capped by the
    ///                  published slew rate, plus guide-star acquisition. See SlewDynamics.
    ///   energy         the wheels at their own published draw for the manoeuvre, and the exposure
    ///                  for its own duration.
    ///
    /// And the battery has to recharge, or the other two are a trap: KSP simulates nothing on an
    /// unloaded vessel, so this file runs the whole ledger. See OrbitalPowerBudget.
    ///
    /// Everything is keyed on universal time, never real time: the player operates this through the
    /// warp. State lives in the telescope's own module node, written to the live module when there
    /// is one and to the protovessel's ConfigNode when there is not.
    /// </summary>
    public static class GroundStation
    {
        /// <summary>
        /// Whether the game is enforcing CommNet at all.
        ///
        /// With the setting off, Vessel.Connection is null on every vessel, so reading link state
        /// off it reports every telescope as unreachable and the orbital half of the mod becomes
        /// unusable with no message explaining why. The setting is the player's answer to whether
        /// radio range is a constraint; it is not this file's business to overrule it.
        /// </summary>
        public static bool CommNetEnforced
        {
            get
            {
                if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Parameters == null) return false;
                return HighLogic.CurrentGame.Parameters.Difficulty.EnableCommNet;
            }
        }

        /// <summary>
        /// True when commands can reach this spacecraft from the ground: it has an antenna with a
        /// path home, or the save is not playing with CommNet at all.
        /// </summary>
        public static bool HasCommandPath(SpaceTelescopeLink link)
        {
            if (link == null || link.Vessel == null) return false;
            if (!CommNetEnforced) return true;
            return link.HasCommLink;
        }

        // ---------------------------------------------------------------- commanding

        /// <summary>
        /// Points the telescope at a bare world direction. The fallback: anything on the chart
        /// should go through CommandEquatorial, and a body through CommandBody.
        /// </summary>
        public static GroundCommandResult CommandDirection(SpaceTelescopeLink link, Vector3d worldDirection,
                                                           out string message)
        {
            return Command(link, worldDirection, null, double.NaN, double.NaN, out message);
        }

        /// <summary>Points the telescope at a body, whose direction is re-resolved as the vehicle moves.</summary>
        public static GroundCommandResult CommandBody(SpaceTelescopeLink link, CelestialBody body,
                                                      out string message)
        {
            if (body == null) { message = "no target"; return GroundCommandResult.NoTarget; }
            if (!TryDirectionToBody(link, body, out Vector3d dir))
            {
                message = "cannot resolve the direction to " + body.bodyName;
                return GroundCommandResult.NoTarget;
            }
            return Command(link, dir, body, double.NaN, double.NaN, out message);
        }

        /// <summary>
        /// Points the telescope at a catalogue position, stored as the RA/Dec it is.
        ///
        /// CommandDirection freezes a world vector, and a frozen vector and a star redrawn each
        /// frame from its RA/Dec disagree by however far the world-to-equatorial mapping has moved:
        /// in game the commanded marker visibly drifted off the star. Stored as coordinates, the
        /// marker and the star are the same two numbers and cannot come apart.
        /// </summary>
        public static GroundCommandResult CommandEquatorial(SpaceTelescopeLink link, double raDeg, double decDeg,
                                                            out string message)
        {
            if (double.IsNaN(raDeg) || double.IsNaN(decDeg))
            {
                message = "no target";
                return GroundCommandResult.NoTarget;
            }
            if (!Visualization.SolarSystemCameraTexture.TryEquatorialDirection(
                    raDeg, decDeg, Planetarium.GetUniversalTime(), out Vector3d dir))
            {
                message = "cannot resolve that sky position from here";
                return GroundCommandResult.NoTarget;
            }
            return Command(link, dir, null, raDeg, decDeg, out message);
        }

        private static GroundCommandResult Command(SpaceTelescopeLink link, Vector3d worldDirection,
                                                   CelestialBody body, double raDeg, double decDeg,
                                                   out string message)
        {
            message = null;

            if (link == null || link.Vessel == null) { message = "no telescope selected"; return GroundCommandResult.NoTarget; }
            if (worldDirection.sqrMagnitude < 1e-12) { message = "no target"; return GroundCommandResult.NoTarget; }

            // Bring the ledger up to now BEFORE pricing the command, or a slew is priced against a
            // battery reading from whenever the player last looked at this telescope.
            Advance(link);

            if (!HasCommandPath(link))
            {
                message = "no antenna link: fly the spacecraft, or give it an antenna";
                return GroundCommandResult.NoLink;
            }

            if (link.ControlMode == AttitudeControlMode.Uncontrolled)
            {
                message = "no attitude control: this vehicle cannot be pointed";
                return GroundCommandResult.NoAttitudeControl;
            }

            Vector3d target = worldDirection.normalized;
            TelescopeCommandState state = TelescopeCommandState.Read(link);

            // Where the boresight is NOW is where the slew starts from, and mid-slew that is
            // somewhere between the last two targets rather than at either of them. Retargeting
            // half way through a manoeuvre is a thing operators really do and it has to charge for
            // the angle actually left to cover, not for the one from the abandoned destination.
            Vector3d from = CurrentDirection(link, in state);
            double angleDeg = from.sqrMagnitude > 1e-12 ? Vector3d.Angle(from, target) : 0.0;

            SlewProfile profile = Plan(link, angleDeg);
            if (double.IsInfinity(profile.ManoeuvreSeconds))
            {
                message = "no control torque: this vehicle cannot be slewed";
                return GroundCommandResult.NoAttitudeControl;
            }

            // Can it FINISH the manoeuvre, not can it pay up front: a repoint is minutes of
            // sunlight as well as minutes of wheels. The charge is taken by the ledger as the slew
            // runs (see Advance), not here.
            double endurance = OrbitalPowerBudget.EnduranceSeconds(
                link.ElectricCharge, ReserveChargeUnits(link),
                ElectricChargeGenerationPerSecond(link.Vessel), SunlitOrbitFraction(link),
                SlewDrawPerSecond(link) + IdleDrawPerSecond(link));

            if (endurance < profile.ManoeuvreSeconds)
            {
                message = string.Format(
                    "not enough charge: the {0:F0} min slew draws {1:F2} EC/s and the battery runs out after {2:F0} min",
                    profile.ManoeuvreSeconds / 60.0,
                    SlewDrawPerSecond(link) + IdleDrawPerSecond(link),
                    endurance / 60.0);
                return GroundCommandResult.InsufficientCharge;
            }

            state.HasCommand = true;
            state.TargetBodyName = body != null ? body.bodyName : "";
            state.TargetRaDeg = raDeg;
            state.TargetDecDeg = decDeg;
            state.CommandedDirection = target;
            state.FromDirection = from.sqrMagnitude > 1e-12 ? from : target;
            state.SlewStartUt = Planetarium.GetUniversalTime();
            state.ManoeuvreSeconds = profile.ManoeuvreSeconds;
            state.AcquisitionSeconds = profile.AcquisitionSeconds;
            state.Write(link);

            message = null;
            return GroundCommandResult.Accepted;
        }

        /// <summary>
        /// The manoeuvre this vehicle would fly for a given angle, from its own measured control
        /// authority and its instrument's published limits.
        /// </summary>
        public static SlewProfile Plan(SpaceTelescopeLink link, double angleDeg)
        {
            SpacePlatformSpec platform = link != null && link.Instrument != null ? link.Instrument.SpacePlatform : null;

            // Both published figures are transplanted through the universe's own time scale; see
            // SlewDynamics.UniverseTimeScale for why copying them across literally makes a
            // ninety-degree repoint cost half an orbit on a body a tenth of Earth's size.
            double scale = UniverseTimeScale();

            return SlewDynamics.Compute(
                angleDeg,
                link != null ? link.ControlTorqueNm : 0.0,
                link != null ? link.InertiaKgM2 : 0.0,
                platform != null ? platform.MaxSlewRateDegPerSecond * scale : 0.0,
                platform != null ? platform.GuideStarAcquisitionSeconds / scale : 0.0);
        }

        /// <summary>
        /// This save's time scale against the one the published slew figures were measured in,
        /// from the home body's own radius and gravitational parameter. One on a real-scale install.
        /// </summary>
        public static double UniverseTimeScale()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return 1.0;
            return SlewDynamics.UniverseTimeScale(home.Radius, home.gravParameter);
        }

        /// <summary>
        /// What a manoeuvre costs, gross of what the panels make while it runs. For the readout;
        /// the affordability test uses the net rate.
        ///
        /// Billed at the vessel's own wheels' published draw, the number KSP itself bills them at
        /// when loaded, so a slew commanded from the ground costs what the same slew flown by hand
        /// costs. Thrusters cost propellant instead; see SlewPropellantKg.
        /// </summary>
        public static double SlewChargeUnits(SpaceTelescopeLink link, in SlewProfile profile)
        {
            if (link == null || link.ControlMode != AttitudeControlMode.MomentumExchange) return 0.0;
            return SlewDynamics.ReactionWheelChargeUnits(in profile, ReactionWheelChargePerSecond(link.Vessel));
        }

        /// <summary>
        /// Charge the attitude control draws while a manoeuvre is running, units per second. Zero under
        /// thruster control, which spends propellant instead.
        /// </summary>
        public static double SlewDrawPerSecond(SpaceTelescopeLink link)
        {
            if (link == null || link.ControlMode != AttitudeControlMode.MomentumExchange) return 0.0;
            return ReactionWheelChargePerSecond(link.Vessel);
        }

        /// <summary>The instrument's standing bus load, units per second.</summary>
        public static double IdleDrawPerSecond(SpaceTelescopeLink link)
        {
            SpacePlatformSpec platform = link != null && link.Instrument != null ? link.Instrument.SpacePlatform : null;
            return platform != null ? platform.IdleElectricChargePerSecond : 0.0;
        }

        /// <summary>Propellant a thruster-controlled manoeuvre spends, kg. Zero under wheel control.</summary>
        public static double SlewPropellantKg(SpaceTelescopeLink link, in SlewProfile profile)
        {
            if (link == null || link.ControlMode != AttitudeControlMode.ReactionControl) return 0.0;

            double arm = ThrusterMomentArmMeters(link);
            double impulse = SlewDynamics.ThrusterImpulseNewtonSeconds(
                link.InertiaKgM2, profile.PeakRateDegPerSecond, arm);
            return SlewDynamics.PropellantMassKg(impulse, ThrusterSpecificImpulseSeconds(link.Vessel));
        }

        /// <summary>
        /// Charge that has to survive a manoeuvre: enough to keep the bus alive through the
        /// acquisition after it. Not padding. A spacecraft that spends its last charge slewing
        /// arrives pointed perfectly at a target it has no power left to photograph. Derived from
        /// the instrument's own idle draw over its own acquisition time, not a flat margin.
        /// </summary>
        public static double ReserveChargeUnits(SpaceTelescopeLink link)
        {
            SpacePlatformSpec platform = link != null && link.Instrument != null ? link.Instrument.SpacePlatform : null;
            if (platform == null) return 0.0;
            return platform.IdleElectricChargePerSecond * Math.Max(0.0, platform.GuideStarAcquisitionSeconds);
        }

        /// <summary>
        /// Charge one exposure costs, over and above the bus load the ledger is already billing.
        ///
        /// The instrument's draw ALONE, deliberately: IdleElectricChargePerSecond is charged
        /// continuously by Advance for every second the spacecraft exists, so adding it here again
        /// would bill the exposure's minutes twice.
        /// </summary>
        public static double ExposureChargeUnits(SpaceTelescopeLink link, double exposureSeconds)
        {
            SpacePlatformSpec platform = link != null && link.Instrument != null ? link.Instrument.SpacePlatform : null;
            if (platform == null || !(exposureSeconds > 0.0)) return 0.0;
            return platform.ExposureElectricChargePerSecond * exposureSeconds;
        }

        /// <summary>
        /// Bills an exposure about to start, and says whether the battery covered it.
        ///
        /// Charged up front rather than integrated: the exposure's clock runs on real time inside
        /// SolarSystemCameraTexture while this ledger runs on universal time, so a long exposure
        /// under warp would otherwise be billed for a quite different duration than it integrated
        /// for. Debiting at the shutter is the only treatment where the frame and its price agree.
        /// </summary>
        public static bool TryBillExposure(SpaceTelescopeLink link, double exposureSeconds, out string message)
        {
            message = null;
            if (link == null || link.Vessel == null) return false;

            Advance(link);

            double cost = ExposureChargeUnits(link, exposureSeconds);
            if (!(cost > 0.0)) return true;

            if (!OrbitalPowerBudget.CanAfford(link.ElectricCharge, cost, 0.0))
            {
                message = string.Format("not enough charge: the exposure needs {0:F1} EC, {1:F1} EC on board",
                                        cost, link.ElectricCharge);
                return false;
            }

            DrawElectricCharge(link.Vessel, cost);
            link.ElectricCharge = Math.Max(0.0, link.ElectricCharge - cost);
            return true;
        }

        /// <summary>
        /// Switches a two-channel instrument to its other detector, through the same command gate
        /// as a repoint. On an unloaded vessel the swap is written into the protovessel node; the
        /// registry's next rescan republishes the link with the new spec, and the imaging side
        /// reacts through SetActiveTelescope.
        /// </summary>
        public static bool TrySwitchChannel(SpaceTelescopeLink link, out string message)
        {
            message = null;
            if (link == null || link.Vessel == null) { message = "no telescope selected"; return false; }

            bool flyingIt = HighLogic.LoadedSceneIsFlight
                         && FlightGlobals.ActiveVessel != null
                         && FlightGlobals.ActiveVessel.id == link.Vessel.id;
            if (!flyingIt && !HasCommandPath(link))
            {
                message = "no antenna link: fly the spacecraft, or give it an antenna";
                return false;
            }

            if (link.Module != null)
            {
                link.Module.SwitchChannel();
                return true;
            }

            ConfigNode node = link.ProtoModule != null ? link.ProtoModule.moduleValues : null;
            if (node == null) { message = "telescope state unavailable"; return false; }

            string current = node.GetValue("instrumentName");
            string alternate = node.GetValue("alternateInstrumentName");
            if (string.IsNullOrEmpty(alternate)) alternate = link.AlternateInstrumentName;
            if (string.IsNullOrEmpty(current)) current = link.Instrument != null ? link.Instrument.Name : "";

            VisualTelescopeSpec target = SpaceTelescopeRegistry.FindInstrument(alternate);
            if (target == null || target.SpacePlatform == null)
            {
                message = "this telescope has no second channel";
                return false;
            }

            if (!node.SetValue("instrumentName", alternate, false)) node.AddValue("instrumentName", alternate);
            if (!node.SetValue("alternateInstrumentName", current, false)) node.AddValue("alternateInstrumentName", current);
            return true;
        }

        // ---------------------------------------------------------------- readout

        /// <summary>
        /// Where the telescope is pointed, where it was told to point, and how far through getting
        /// there it is.
        ///
        /// The measured attitude wins when there is one, the same rule PointingStability applies to
        /// the body rate: a loaded vessel's boresight is observable. Unloaded, it is interpolated
        /// along the profile, which is not a fudge, because during a slew the boresight genuinely
        /// is between the two attitudes.
        /// </summary>
        public static PointingReadout Readout(SpaceTelescopeLink link)
        {
            var r = new PointingReadout();
            if (link == null || link.Vessel == null) return r;

            TelescopeCommandState state = TelescopeCommandState.Read(link);
            r.HasCommand = state.HasCommand;
            if (!state.HasCommand) return r;

            r.CommandedDirection = ResolveCommandedDirection(link, in state);
            r.CurrentDirection = CurrentDirection(link, in state);
            r.CommandedRaDeg = state.TargetRaDeg;
            r.CommandedDecDeg = state.TargetDecDeg;
            r.ErrorDeg = r.CurrentDirection.sqrMagnitude > 1e-12 && r.CommandedDirection.sqrMagnitude > 1e-12
                ? Vector3d.Angle(r.CurrentDirection, r.CommandedDirection) : double.NaN;

            double elapsed = Planetarium.GetUniversalTime() - state.SlewStartUt;
            double manoeuvre = state.ManoeuvreSeconds;
            double total = manoeuvre + state.AcquisitionSeconds;

            if (elapsed < manoeuvre)
            {
                SlewProfile running = RebuildProfile(link, in state);
                r.SlewRateDegPerSecond = SlewDynamics.RateDegPerSecondAt(in running, elapsed);
            }
            r.SlewProgress = total > 0.0 ? Clamp01(elapsed / total) : 1.0;
            r.SecondsRemaining = Math.Max(0.0, total - elapsed);
            r.AcquisitionSeconds = state.AcquisitionSeconds;
            r.AcquisitionRemaining = Math.Min(r.SecondsRemaining, state.AcquisitionSeconds);
            r.ToleranceDeg = OnTargetToleranceDeg(link);

            if (elapsed < manoeuvre) r.Phase = GroundPointingPhase.Slewing;
            else if (elapsed < total) r.Phase = GroundPointingPhase.Acquiring;
            else r.Phase = GroundPointingPhase.OnTarget;

            // A LOADED VESSEL IS JUDGED ON WHERE IT REALLY IS, not on the clock. Its attitude is
            // being integrated by the game against whatever else is acting on it, and it can be
            // off target with the clock expired (SAS lost it, the player took manual control) or
            // on target early. Either way the frame is decided by the boresight, so the gate is.
            if (link.Module != null && r.Phase == GroundPointingPhase.OnTarget
                && !double.IsNaN(r.ErrorDeg) && r.ErrorDeg > r.ToleranceDeg)
            {
                r.Phase = GroundPointingPhase.Acquiring;
            }

            r.Settled = r.Phase == GroundPointingPhase.OnTarget;
            return r;
        }

        /// <summary>
        /// How far off the boresight may be and still have the target on the detector: half the
        /// short side of the field of view. Derived, not chosen: "on target" for an imager means
        /// the source lands on silicon. WFC3/UVIS comes out at the 81 arcsec half-field STScI
        /// publishes for its 162 arcsec square field.
        /// </summary>
        public static double OnTargetToleranceDeg(SpaceTelescopeLink link)
        {
            VisualTelescopeSpec spec = link != null ? link.Instrument : null;
            if (spec == null || !(spec.FocalLengthMeters > 0.0) || !(spec.NativePixelSizeMeters > 0.0))
                return 0.05;

            int shortSide = Math.Min(Math.Max(1, spec.NativeSensorWidthPx), Math.Max(1, spec.NativeSensorHeightPx));
            double halfExtentMeters = 0.5 * shortSide * spec.NativePixelSizeMeters;
            return Math.Atan(halfExtentMeters / spec.FocalLengthMeters) * (180.0 / Math.PI);
        }

        // The direction the boresight is on right now, measured when possible and interpolated when not.
        private static Vector3d CurrentDirection(SpaceTelescopeLink link, in TelescopeCommandState state)
        {
            if (link.Module != null)
            {
                Vector3d bore = link.Module.BoresightWorldDirection;
                if (bore.sqrMagnitude > 1e-12) return bore.normalized;
            }

            // Not commanded is not unknown: it is pointing wherever the player left it, which is
            // the angle a first repoint has to be priced on. Without this the first command after
            // launch was a zero-degree slew however far it asked to go.
            if (!state.HasCommand)
                return state.LastBoresight.sqrMagnitude > 1e-12 ? state.LastBoresight.normalized : Vector3d.zero;

            Vector3d to = ResolveCommandedDirection(link, in state);
            if (to.sqrMagnitude < 1e-12) return Vector3d.zero;

            Vector3d from = state.FromDirection.sqrMagnitude > 1e-12 ? state.FromDirection.normalized : to;
            if (!(state.ManoeuvreSeconds > 0.0)) return to;

            SlewProfile profile = RebuildProfile(link, in state);
            double elapsed = Planetarium.GetUniversalTime() - state.SlewStartUt;
            return Slerp(from, to, SlewDynamics.FractionOfAngleCovered(in profile, elapsed));
        }

        // The manoeuvre currently in flight, rebuilt from what was stored when it was commanded. Not replanned
        // from the angle: the vehicle's torque may have changed since (a wheel switched off, a stage dropped),
        // and the slew running is the one that was planned, not the one that would be planned now. Only the
        // peak rate has to be recovered, by solving the stored duration for it.
        private static SlewProfile RebuildProfile(SpaceTelescopeLink link, in TelescopeCommandState state)
        {
            Vector3d to = ResolveCommandedDirection(link, in state);
            Vector3d from = state.FromDirection.sqrMagnitude > 1e-12 ? state.FromDirection.normalized : to;

            var profile = new SlewProfile
            {
                AngleDeg = from.sqrMagnitude > 1e-12 && to.sqrMagnitude > 1e-12 ? Vector3d.Angle(from, to) : 0.0,
                AccelerationDegPerSecond2 = SlewDynamics.AngularAccelerationDegPerSecond2(
                    link.ControlTorqueNm, link.InertiaKgM2),
                ManoeuvreSeconds = state.ManoeuvreSeconds,
            };

            double alpha = profile.AccelerationDegPerSecond2;
            if (!(alpha > 0.0) || !(profile.ManoeuvreSeconds > 0.0)) return profile;

            double triangularPeak = Math.Sqrt(alpha * profile.AngleDeg);
            double triangularTime = 2.0 * Math.Sqrt(profile.AngleDeg / alpha);
            if (profile.ManoeuvreSeconds > triangularTime * 1.000001)
            {
                // t = theta/w + w/alpha solved for w, taking the root below the triangular peak.
                double b = profile.ManoeuvreSeconds * alpha;
                double disc = b * b - 4.0 * alpha * profile.AngleDeg;
                profile.PeakRateDegPerSecond = disc > 0.0 ? 0.5 * (b - Math.Sqrt(disc)) : triangularPeak;
                profile.RateLimited = true;
            }
            else profile.PeakRateDegPerSecond = triangularPeak;

            return profile;
        }

        // The direction currently commanded: recomputed for a body, held fixed for a catalogue position.
        private static Vector3d ResolveCommandedDirection(SpaceTelescopeLink link, in TelescopeCommandState state)
        {
            if (!string.IsNullOrEmpty(state.TargetBodyName))
            {
                // Named into a local first: an `in` parameter cannot be captured by the lambda,
                // and Find is the only way to look a body up by name.
                string bodyName = state.TargetBodyName;
                CelestialBody body = FlightGlobals.Bodies != null
                    ? FlightGlobals.Bodies.Find(b => b != null && b.bodyName == bodyName) : null;
                if (body != null && TryDirectionToBody(link, body, out Vector3d d)) return d;
                return Vector3d.zero;
            }

            // Re-resolved from the coordinates, not read back from a vector frozen at the click.
            // See CommandEquatorial.
            if (!double.IsNaN(state.TargetRaDeg) && !double.IsNaN(state.TargetDecDeg)
                && Visualization.SolarSystemCameraTexture.TryEquatorialDirection(
                       state.TargetRaDeg, state.TargetDecDeg, Planetarium.GetUniversalTime(), out Vector3d eq))
                return eq;

            return state.CommandedDirection;
        }

        private static bool TryDirectionToBody(SpaceTelescopeLink link, CelestialBody body, out Vector3d direction)
        {
            direction = Vector3d.zero;
            if (link == null || link.Vessel == null || body == null) return false;
            Vector3d to = body.position - link.Vessel.GetWorldPos3D();
            if (to.sqrMagnitude < 1e-6) return false;
            direction = to.normalized;
            return true;
        }

        // Great-circle interpolation between two directions. Not a lerp: a lerp between two unit vectors
        // crosses the chord rather than the sphere, so it moves fastest in the middle and would put the
        // boresight off the arc a real eigenaxis rotation traverses.
        private static Vector3d Slerp(Vector3d from, Vector3d to, double t)
        {
            double dot = Vector3d.Dot(from, to);
            if (dot > 1.0) dot = 1.0; else if (dot < -1.0) dot = -1.0;
            double omega = Math.Acos(dot);
            if (omega < 1e-9) return to;

            double sinOmega = Math.Sin(omega);
            double a = Math.Sin((1.0 - t) * omega) / sinOmega;
            double b = Math.Sin(t * omega) / sinOmega;
            Vector3d v = from * a + to * b;
            return v.sqrMagnitude > 1e-12 ? v.normalized : to;
        }

        // ---------------------------------------------------------------- power ledger

        /// <summary>
        /// Brings the battery up to the present across however much universal time has passed.
        ///
        /// Safe and cheap to call as often as the caller likes: the interval comes from a stored
        /// timestamp, so a second call in the same instant advances by zero.
        ///
        /// A loaded vessel is left alone, because the game is already simulating it properly and
        /// this would bill it twice. The timestamp is still carried forward so the ledger does not
        /// catch up on that time once the vessel unloads.
        /// </summary>
        public static void Advance(SpaceTelescopeLink link)
        {
            if (link == null || link.Vessel == null) return;

            TelescopeCommandState state = TelescopeCommandState.Read(link);
            double now = Planetarium.GetUniversalTime();

            if (double.IsNaN(state.PowerLedgerUt) || state.PowerLedgerUt <= 0.0 || state.PowerLedgerUt > now)
            {
                state.PowerLedgerUt = now;
                state.Write(link);
                return;
            }

            double elapsed = now - state.PowerLedgerUt;
            state.PowerLedgerUt = now;
            state.Write(link);

            if (link.Vessel.loaded || !(elapsed > 0.0)) return;

            double generation = ElectricChargeGenerationPerSecond(link.Vessel);
            double sunlit = SunlitOrbitFraction(link);
            double idle = IdleDrawPerSecond(link);

            TotalElectricCharge(link.Vessel, out double charge, out double capacity);

            // The slew is spent here, not at the command: billing it the instant it was ordered
            // would ignore the sunlight the spacecraft flies through while performing it. The
            // wheels' draw covers the seconds of this interval overlapping the manoeuvre.
            double slewStart = state.SlewStartUt;
            double manoeuvre = state.ManoeuvreSeconds;
            double slewEnd = state.HasCommand && manoeuvre > 0.0 && !double.IsInfinity(manoeuvre)
                ? slewStart + manoeuvre : slewStart;
            double overlap = Math.Max(0.0, Math.Min(now, slewEnd) - Math.Max(now - elapsed, slewStart));
            if (overlap > elapsed) overlap = elapsed;

            // The slewing part first, then the rest. The order only matters at the clamps, and
            // this is the order it happened in for the ordinary case of a command just issued.
            double updated = charge;
            if (overlap > 0.0)
                updated = OrbitalPowerBudget.Advance(updated, capacity, generation, sunlit,
                                                     idle + SlewDrawPerSecond(link), overlap);
            if (elapsed - overlap > 0.0)
                updated = OrbitalPowerBudget.Advance(updated, capacity, generation, sunlit,
                                                     idle, elapsed - overlap);

            double delta = updated - charge;
            if (Math.Abs(delta) > 1e-9)
            {
                if (delta > 0.0) AddElectricCharge(link.Vessel, delta);
                else DrawElectricCharge(link.Vessel, -delta);
            }
            link.ElectricCharge = updated;
        }

        /// <summary>
        /// Fraction of the orbit spent in sunlight. One when the geometry is unavailable (an
        /// escape trajectory, no orbit): permanent sunlight is the right way to be wrong there.
        /// </summary>
        public static double SunlitOrbitFraction(SpaceTelescopeLink link)
        {
            if (link == null || link.Vessel == null) return 1.0;
            CelestialBody host = link.Vessel.mainBody;
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (host == null || sun == null || host == sun) return 1.0;
            Orbit orbit = link.Vessel.orbit;
            if (orbit == null || !(orbit.semiMajorAxis > 0.0)) return 1.0;

            Vector3d toSun = sun.position - host.position;
            if (toSun.sqrMagnitude < 1e-6) return 1.0;

            SkyVector normal = link.OrbitNormal();
            double beta = OrbitalPowerBudget.BetaAngleDeg(normal, new SkyVector(toSun.x, toSun.y, toSun.z));

            // The semi-major axis rather than the instantaneous radius: the eclipse fraction is an
            // orbit-averaged quantity and feeding it a radius from one point of an eccentric orbit
            // would make the answer depend on when the ledger happened to be run.
            double eclipsed = OrbitalPowerBudget.EclipsedOrbitFraction(orbit.semiMajorAxis, host.Radius, beta);
            return Clamp01(1.0 - eclipsed);
        }

        /// <summary>
        /// Everything that makes charge, units per second at full sun: deployed panels scaled for
        /// distance, plus generators.
        ///
        /// The prefab supplies the rates because a protovessel snapshot carries only what CHANGED
        /// from it, which for a panel is its deploy state and not its output. Same split
        /// SpaceTelescopeRegistry uses for the antenna.
        /// </summary>
        public static double ElectricChargeGenerationPerSecond(Vessel v)
        {
            if (v == null) return 0.0;

            double solar = 0.0, generators = 0.0;

            if (v.loaded)
            {
                List<ModuleDeployableSolarPanel> panels = v.FindPartModulesImplementing<ModuleDeployableSolarPanel>();
                if (panels != null)
                    for (int i = 0; i < panels.Count; i++)
                    {
                        ModuleDeployableSolarPanel p = panels[i];
                        if (p == null || p.resourceName != "ElectricCharge") continue;
                        if (p.deployState != ModuleDeployablePart.DeployState.EXTENDED) continue;
                        solar += p.chargeRate;
                    }

                List<ModuleGenerator> gens = v.FindPartModulesImplementing<ModuleGenerator>();
                if (gens != null)
                    for (int i = 0; i < gens.Count; i++) generators += GeneratorChargeRate(gens[i]);
            }
            else
            {
                if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) return 0.0;

                for (int i = 0; i < v.protoVessel.protoPartSnapshots.Count; i++)
                {
                    ProtoPartSnapshot part = v.protoVessel.protoPartSnapshots[i];
                    if (part == null || part.partInfo == null || part.partInfo.partPrefab == null) continue;

                    List<ModuleDeployableSolarPanel> prefabPanels =
                        part.partInfo.partPrefab.FindModulesImplementing<ModuleDeployableSolarPanel>();
                    if (prefabPanels != null)
                        for (int j = 0; j < prefabPanels.Count; j++)
                        {
                            ModuleDeployableSolarPanel p = prefabPanels[j];
                            if (p == null || p.resourceName != "ElectricCharge") continue;
                            if (!ProtoPanelExtended(part, p)) continue;
                            solar += p.chargeRate;
                        }

                    List<ModuleGenerator> prefabGens =
                        part.partInfo.partPrefab.FindModulesImplementing<ModuleGenerator>();
                    if (prefabGens != null)
                        for (int j = 0; j < prefabGens.Count; j++) generators += GeneratorChargeRate(prefabGens[j]);
                }
            }

            return solar * SolarFluxMultiplier(v) + generators;
        }

        // A protovessel panel's deploy state, falling back to the prefab's. A fixed panel is authored EXTENDED
        // and never changes, so the fallback is right for it.
        private static bool ProtoPanelExtended(ProtoPartSnapshot part, ModuleDeployableSolarPanel prefab)
        {
            if (part.modules != null)
            {
                for (int k = 0; k < part.modules.Count; k++)
                {
                    ProtoPartModuleSnapshot m = part.modules[k];
                    if (m == null || m.moduleName != prefab.moduleName || m.moduleValues == null) continue;
                    string s = m.moduleValues.GetValue("deployState");
                    if (string.IsNullOrEmpty(s)) break;
                    return string.Equals(s, "EXTENDED", StringComparison.OrdinalIgnoreCase);
                }
            }
            return prefab.deployState == ModuleDeployablePart.DeployState.EXTENDED;
        }

        private static double GeneratorChargeRate(ModuleGenerator g)
        {
            if (g == null || g.resHandler == null || g.resHandler.outputResources == null) return 0.0;
            if (!g.isAlwaysActive && !g.generatorIsActive) return 0.0;

            double rate = 0.0;
            for (int i = 0; i < g.resHandler.outputResources.Count; i++)
            {
                ModuleResource r = g.resHandler.outputResources[i];
                if (r != null && r.name == "ElectricCharge") rate += r.rate;
            }
            return rate;
        }

        // Inverse-square scaling of a panel's rated output against the distance it is rated at, which for KSP
        // is the home body's orbit. Plain 1/r^2 rather than the panel's powerCurve, because only some parts
        // carry one while the falloff applies to all of them.
        private static double SolarFluxMultiplier(Vessel v)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (sun == null || home == null || home.orbit == null || !(home.orbit.semiMajorAxis > 0.0)) return 1.0;

            double distance = (v.GetWorldPos3D() - sun.position).magnitude;
            if (!(distance > 1.0)) return 1.0;

            double ratio = home.orbit.semiMajorAxis / distance;
            return ratio * ratio;
        }

        // ---------------------------------------------------------------- resources

        /// <summary>Electric charge and battery capacity on the vessel, loaded or not.</summary>
        public static void TotalElectricCharge(Vessel v, out double amount, out double capacity)
        {
            amount = 0.0;
            capacity = 0.0;
            if (v == null) return;

            if (v.loaded)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null || p.Resources == null) continue;
                    for (int r = 0; r < p.Resources.Count; r++)
                    {
                        PartResource res = p.Resources[r];
                        if (res == null || res.resourceName != "ElectricCharge") continue;
                        amount += res.amount;
                        capacity += res.maxAmount;
                    }
                }
                return;
            }

            if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) return;
            for (int i = 0; i < v.protoVessel.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot p = v.protoVessel.protoPartSnapshots[i];
                if (p == null || p.resources == null) continue;
                for (int r = 0; r < p.resources.Count; r++)
                {
                    ProtoPartResourceSnapshot res = p.resources[r];
                    if (res == null || res.resourceName != "ElectricCharge") continue;
                    amount += res.amount;
                    capacity += res.maxAmount;
                }
            }
        }

        /// <summary>
        /// Takes charge off the vessel, spread across its batteries in proportion to what each
        /// holds. Proportionally because KSP's ElectricCharge flow mode is ALL_VESSEL and drains
        /// them together; emptying one cell first would leave a save no player action could make.
        /// </summary>
        public static void DrawElectricCharge(Vessel v, double units)
        {
            if (v == null || !(units > 0.0)) return;

            if (v.loaded)
            {
                var cells = new List<PartResource>();
                double total = 0.0;
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null || p.Resources == null) continue;
                    for (int r = 0; r < p.Resources.Count; r++)
                    {
                        PartResource res = p.Resources[r];
                        if (res == null || res.resourceName != "ElectricCharge" || !(res.amount > 0.0)) continue;
                        cells.Add(res);
                        total += res.amount;
                    }
                }
                if (!(total > 0.0)) return;
                double take = Math.Min(units, total);
                for (int i = 0; i < cells.Count; i++)
                    cells[i].amount = Math.Max(0.0, cells[i].amount - take * (cells[i].amount / total));
                return;
            }

            if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) return;

            var protoCells = new List<ProtoPartResourceSnapshot>();
            double protoTotal = 0.0;
            for (int i = 0; i < v.protoVessel.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot p = v.protoVessel.protoPartSnapshots[i];
                if (p == null || p.resources == null) continue;
                for (int r = 0; r < p.resources.Count; r++)
                {
                    ProtoPartResourceSnapshot res = p.resources[r];
                    if (res == null || res.resourceName != "ElectricCharge" || !(res.amount > 0.0)) continue;
                    protoCells.Add(res);
                    protoTotal += res.amount;
                }
            }
            if (!(protoTotal > 0.0)) return;

            double drawn = Math.Min(units, protoTotal);
            for (int i = 0; i < protoCells.Count; i++)
                SetProtoResource(protoCells[i],
                    Math.Max(0.0, protoCells[i].amount - drawn * (protoCells[i].amount / protoTotal)));
        }

        /// <summary>Puts charge back, spreading it across whatever room the batteries have left.</summary>
        public static void AddElectricCharge(Vessel v, double units)
        {
            if (v == null || !(units > 0.0)) return;

            if (v.loaded)
            {
                var cells = new List<PartResource>();
                double room = 0.0;
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null || p.Resources == null) continue;
                    for (int r = 0; r < p.Resources.Count; r++)
                    {
                        PartResource res = p.Resources[r];
                        if (res == null || res.resourceName != "ElectricCharge") continue;
                        double free = res.maxAmount - res.amount;
                        if (!(free > 0.0)) continue;
                        cells.Add(res);
                        room += free;
                    }
                }
                if (!(room > 0.0)) return;
                double give = Math.Min(units, room);
                for (int i = 0; i < cells.Count; i++)
                {
                    double free = cells[i].maxAmount - cells[i].amount;
                    cells[i].amount = Math.Min(cells[i].maxAmount, cells[i].amount + give * (free / room));
                }
                return;
            }

            if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) return;

            var protoCells = new List<ProtoPartResourceSnapshot>();
            double protoRoom = 0.0;
            for (int i = 0; i < v.protoVessel.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot p = v.protoVessel.protoPartSnapshots[i];
                if (p == null || p.resources == null) continue;
                for (int r = 0; r < p.resources.Count; r++)
                {
                    ProtoPartResourceSnapshot res = p.resources[r];
                    if (res == null || res.resourceName != "ElectricCharge") continue;
                    double free = res.maxAmount - res.amount;
                    if (!(free > 0.0)) continue;
                    protoCells.Add(res);
                    protoRoom += free;
                }
            }
            if (!(protoRoom > 0.0)) return;

            double given = Math.Min(units, protoRoom);
            for (int i = 0; i < protoCells.Count; i++)
            {
                double free = protoCells[i].maxAmount - protoCells[i].amount;
                SetProtoResource(protoCells[i],
                    Math.Min(protoCells[i].maxAmount, protoCells[i].amount + given * (free / protoRoom)));
            }
        }

        // Writes a protovessel resource amount. THE FIELD IS WHAT PERSISTS, which is the opposite of the
        // obvious guess. ProtoPartResourceSnapshot carries both an `amount` field and a `resourceValues` node,
        // inviting the assumption that the node is the save. ProtoPartSnapshot.Save makes a FRESH RESOURCE node
        // and hands it to ProtoPartResourceSnapshot.Save, which copies resourceValues in and then overwrites
        // amount/maxAmount/flowState from the fields. UpdateConfigNodeAmounts is still called for the reverse
        // direction, since other code paths read the node.
        private static void SetProtoResource(ProtoPartResourceSnapshot res, double amount)
        {
            if (res == null) return;
            res.amount = amount;
            res.UpdateConfigNodeAmounts();

            // Keep the live PartResource in step when there is one: a vessel can be unloaded and
            // still have its parts around for a frame during a scene transition.
            if (res.resourceRef != null) res.resourceRef.amount = amount;
        }

        // ---------------------------------------------------------------- vessel hardware

        /// <summary>
        /// The wheels' total draw at full torque, units per second, off their own RESOURCE nodes.
        /// Zero for wheels a part config declares free to run, which is legitimate.
        /// </summary>
        public static double ReactionWheelChargePerSecond(Vessel v)
        {
            double rate = 0.0;
            if (v == null) return 0.0;

            if (v.loaded)
            {
                List<ModuleReactionWheel> wheels = v.FindPartModulesImplementing<ModuleReactionWheel>();
                if (wheels == null) return 0.0;
                for (int i = 0; i < wheels.Count; i++)
                {
                    if (wheels[i] == null || wheels[i].wheelState != ModuleReactionWheel.WheelState.Active) continue;
                    rate += WheelChargeRate(wheels[i]);
                }
                return rate;
            }

            if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) return 0.0;
            for (int i = 0; i < v.protoVessel.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot p = v.protoVessel.protoPartSnapshots[i];
                if (p == null || p.partInfo == null || p.partInfo.partPrefab == null) continue;
                List<ModuleReactionWheel> prefabs = p.partInfo.partPrefab.FindModulesImplementing<ModuleReactionWheel>();
                if (prefabs == null) continue;
                for (int j = 0; j < prefabs.Count; j++) rate += WheelChargeRate(prefabs[j]);
            }
            return rate;
        }

        private static double WheelChargeRate(ModuleReactionWheel w)
        {
            if (w == null || w.resHandler == null || w.resHandler.inputResources == null) return 0.0;
            double rate = 0.0;
            for (int i = 0; i < w.resHandler.inputResources.Count; i++)
            {
                ModuleResource r = w.resHandler.inputResources[i];
                if (r != null && r.name == "ElectricCharge") rate += r.rate;
            }
            return rate;
        }

        // Moment arm the thrusters act at: the same bounding radius ModuleExoSpaceTelescope used to turn thrust
        // into torque, so the two agree by construction.
        private static double ThrusterMomentArmMeters(SpaceTelescopeLink link)
        {
            if (link == null || !(link.ControlTorqueNm > 0.0)) return 1.0;

            double thrust = 0.0;
            Vessel v = link.Vessel;
            if (v != null && v.loaded)
            {
                List<ModuleRCS> thrusters = v.FindPartModulesImplementing<ModuleRCS>();
                if (thrusters != null)
                    for (int i = 0; i < thrusters.Count; i++)
                        if (thrusters[i] != null && thrusters[i].rcsEnabled) thrust += thrusters[i].thrusterPower;
            }
            return thrust > 0.0 ? Math.Max(0.5, link.ControlTorqueNm / thrust) : 1.0;
        }

        // The best specific impulse among the vessel's thrusters, seconds; KSP's stock monopropellant default
        // when none can be read.
        private static double ThrusterSpecificImpulseSeconds(Vessel v)
        {
            const double StockMonopropVacuumIsp = 240.0;
            if (v == null || !v.loaded) return StockMonopropVacuumIsp;

            List<ModuleRCS> thrusters = v.FindPartModulesImplementing<ModuleRCS>();
            if (thrusters == null) return StockMonopropVacuumIsp;

            double best = 0.0;
            for (int i = 0; i < thrusters.Count; i++)
            {
                ModuleRCS r = thrusters[i];
                if (r == null || r.atmosphereCurve == null) continue;
                double isp = r.atmosphereCurve.Evaluate(0f);
                if (isp > best) best = isp;
            }
            return best > 0.0 ? best : StockMonopropVacuumIsp;
        }

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);
    }

    /// <summary>Why a ground command was or was not accepted. Each failure names something the player did and can undo.</summary>
    public enum GroundCommandResult
    {
        Accepted,
        NoTarget,
        NoLink,
        NoAttitudeControl,
        InsufficientCharge,
    }

    /// <summary>Where a commanded repoint has got to.</summary>
    public enum GroundPointingPhase
    {
        /// <summary>Nothing commanded.</summary>
        Idle,
        /// <summary>Rotating toward the target.</summary>
        Slewing,
        /// <summary>Arrived at the attitude, guide stars not yet locked.</summary>
        Acquiring,
        /// <summary>Pointed and guiding: the shutter may open.</summary>
        OnTarget,
    }

    /// <summary>What the panel needs to draw the pointing, and what the capture gate needs to allow one.</summary>
    public struct PointingReadout
    {
        public bool HasCommand;

        /// <summary>Unit world direction the boresight is on now: measured on a loaded vessel, interpolated along the slew profile otherwise.</summary>
        public Vector3d CurrentDirection;

        /// <summary>Unit world direction it has been told to hold.</summary>
        public Vector3d CommandedDirection;

        /// <summary>
        /// The commanded catalogue position, when the target is one; NaN for a body or a raw
        /// direction. The chart draws its marker from THESE rather than from CommandedDirection,
        /// so the marker and the star it sits on are literally the same two numbers.
        /// </summary>
        public double CommandedRaDeg;
        public double CommandedDecDeg;

        /// <summary>Angle between the two, degrees. NaN when nothing is commanded.</summary>
        public double ErrorDeg;

        /// <summary>How far off the boresight may be and still have the target on the detector.</summary>
        public double ToleranceDeg;

        public GroundPointingPhase Phase;

        /// <summary>How fast the vehicle is turning right now, deg/s. Zero unless a manoeuvre is running; what smears a frame taken during one.</summary>
        public double SlewRateDegPerSecond;

        /// <summary>Fraction of the manoeuvre and acquisition elapsed, 0 to 1.</summary>
        public double SlewProgress;

        /// <summary>Universal seconds until the telescope is on target and guiding.</summary>
        public double SecondsRemaining;

        /// <summary>Of that, how much is guide-star acquisition rather than turning, and the total acquisition this repoint costs.</summary>
        public double AcquisitionRemaining;
        public double AcquisitionSeconds;

        /// <summary>True when an exposure may start: pointed, and guiding.</summary>
        public bool Settled;
    }

    /// <summary>
    /// The commanded state of one telescope, read from and written to whichever copy of the module
    /// exists: the live PartModule when loaded, the protovessel's ConfigNode when not.
    ///
    /// One struct rather than a branch at every call site. Two backends and ten fields is how one
    /// ends up forgotten in a path nobody tests, which for an unloaded vessel means a command that
    /// appears to work and is gone at the next scene change.
    /// </summary>
    public struct TelescopeCommandState
    {
        public bool HasCommand;
        public Vector3d CommandedDirection;
        public Vector3d FromDirection;
        public string TargetBodyName;
        public double TargetRaDeg;
        public double TargetDecDeg;
        public double SlewStartUt;
        public double ManoeuvreSeconds;
        public double AcquisitionSeconds;
        public double PowerLedgerUt;

        /// <summary>Where the boresight really was, last time the vessel was loaded. The origin a first repoint is measured from.</summary>
        public Vector3d LastBoresight;

        public static TelescopeCommandState Read(SpaceTelescopeLink link)
        {
            var s = new TelescopeCommandState
            {
                TargetBodyName = "",
                TargetRaDeg = double.NaN,
                TargetDecDeg = double.NaN,
            };
            if (link == null) return s;

            ModuleExoSpaceTelescope m = link.Module;
            if (m != null)
            {
                s.HasCommand = m.pointingHoldEnabled;
                s.TargetBodyName = m.pointingTargetBody ?? "";
                s.TargetRaDeg = m.pointingTargetRaDeg;
                s.TargetDecDeg = m.pointingTargetDecDeg;
                s.CommandedDirection = ParseDirection(m.pointingTarget);
                s.FromDirection = ParseDirection(m.slewFromDirection);
                s.SlewStartUt = m.slewStartUt;
                s.ManoeuvreSeconds = m.slewManoeuvreSeconds;
                s.AcquisitionSeconds = m.slewAcquisitionSeconds;
                s.PowerLedgerUt = m.powerLedgerUt;
                s.LastBoresight = ParseDirection(m.lastBoresightDirection);
                return s;
            }

            ConfigNode node = link.ProtoModule != null ? link.ProtoModule.moduleValues : null;
            if (node == null) return s;

            s.HasCommand = ReadBool(node, "pointingHoldEnabled");
            s.TargetBodyName = node.GetValue("pointingTargetBody") ?? "";
            s.TargetRaDeg = ReadDouble(node, "pointingTargetRaDeg", double.NaN);
            s.TargetDecDeg = ReadDouble(node, "pointingTargetDecDeg", double.NaN);
            s.CommandedDirection = ParseDirection(node.GetValue("pointingTarget"));
            s.FromDirection = ParseDirection(node.GetValue("slewFromDirection"));
            s.SlewStartUt = ReadDouble(node, "slewStartUt");
            s.ManoeuvreSeconds = ReadDouble(node, "slewManoeuvreSeconds");
            s.AcquisitionSeconds = ReadDouble(node, "slewAcquisitionSeconds");
            s.PowerLedgerUt = ReadDouble(node, "powerLedgerUt");
            s.LastBoresight = ParseDirection(node.GetValue("lastBoresightDirection"));
            return s;
        }

        public void Write(SpaceTelescopeLink link)
        {
            if (link == null) return;

            ModuleExoSpaceTelescope m = link.Module;
            if (m != null)
            {
                m.pointingHoldEnabled = HasCommand;
                m.pointingTargetBody = TargetBodyName ?? "";
                m.pointingTargetRaDeg = TargetRaDeg;
                m.pointingTargetDecDeg = TargetDecDeg;
                m.pointingTarget = string.IsNullOrEmpty(TargetBodyName) ? FormatDirection(CommandedDirection) : "";
                m.slewFromDirection = FormatDirection(FromDirection);
                m.slewStartUt = SlewStartUt;
                m.slewManoeuvreSeconds = ManoeuvreSeconds;
                m.slewAcquisitionSeconds = AcquisitionSeconds;
                m.powerLedgerUt = PowerLedgerUt;
                return;
            }

            ConfigNode node = link.ProtoModule != null ? link.ProtoModule.moduleValues : null;
            if (node == null) return;

            Set(node, "pointingHoldEnabled", HasCommand ? "True" : "False");
            Set(node, "pointingTargetBody", TargetBodyName ?? "");
            Set(node, "pointingTargetRaDeg", TargetRaDeg.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Set(node, "pointingTargetDecDeg", TargetDecDeg.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Set(node, "pointingTarget", string.IsNullOrEmpty(TargetBodyName) ? FormatDirection(CommandedDirection) : "");
            Set(node, "slewFromDirection", FormatDirection(FromDirection));
            Set(node, "slewStartUt", SlewStartUt.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Set(node, "slewManoeuvreSeconds", ManoeuvreSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Set(node, "slewAcquisitionSeconds", AcquisitionSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Set(node, "powerLedgerUt", PowerLedgerUt.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            // The boresight record is written back on the unloaded path only. While the vessel is
            // loaded the module is measuring it every physics frame and is the authority; writing
            // a remembered value over a measured one is the one way this field can go wrong.
            Set(node, "lastBoresightDirection", FormatDirection(LastBoresight));
        }

        private static void Set(ConfigNode node, string key, string value)
        {
            if (!node.SetValue(key, value, false)) node.AddValue(key, value);
        }

        private static string FormatDirection(Vector3d d)
        {
            if (d.sqrMagnitude < 1e-12) return "";
            Vector3d u = d.normalized;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                 "{0:R},{1:R},{2:R}", u.x, u.y, u.z);
        }

        private static Vector3d ParseDirection(string s)
        {
            if (string.IsNullOrEmpty(s)) return Vector3d.zero;
            string[] parts = s.Split(',');
            if (parts.Length != 3) return Vector3d.zero;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out double x)) return Vector3d.zero;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, ci, out double y)) return Vector3d.zero;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, ci, out double z)) return Vector3d.zero;
            var v = new Vector3d(x, y, z);
            return v.sqrMagnitude > 1e-12 ? v : Vector3d.zero;
        }

        private static bool ReadBool(ConfigNode node, string key)
        {
            string s = node.GetValue(key);
            return !string.IsNullOrEmpty(s) && bool.TryParse(s, out bool v) && v;
        }

        private static double ReadDouble(ConfigNode node, string key, double fallback = 0.0)
        {
            string s = node.GetValue(key);
            return !string.IsNullOrEmpty(s)
                && double.TryParse(s, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? v : fallback;
        }
    }
}
