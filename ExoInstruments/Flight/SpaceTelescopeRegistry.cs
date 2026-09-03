using System;
using System.Collections.Generic;
using ExoInstruments.Core;
using UnityEngine;

namespace ExoInstruments.Flight
{
    /// <summary>
    /// One orbiting telescope, described uniformly whether its vessel is loaded or not.
    ///
    /// This is what the observatory GUI and the imaging pipeline consume. Neither has any
    /// business knowing whether the spacecraft happens to be in physics range, and the whole
    /// point of the ground-operations mode is that it usually is not.
    /// </summary>
    public sealed class SpaceTelescopeLink
    {
        public Vessel Vessel;
        public string VesselName;

        /// <summary>The catalogue instrument this telescope carries.</summary>
        public VisualTelescopeSpec Instrument;

        /// <summary>Catalogue name of the other channel, when the part declares one. Empty otherwise.</summary>
        public string AlternateInstrumentName = "";

        /// <summary>The live module, or null when the vessel is unloaded. Everything below works either way.</summary>
        public ModuleExoSpaceTelescope Module;

        /// <summary>
        /// The saved module node when the vessel is unloaded; null when Module is the authority
        /// instead. Carried because the ground station WRITES this state as well as reading it: a
        /// repoint commanded at an unloaded telescope has to persist across the scene change.
        /// </summary>
        public ProtoPartModuleSnapshot ProtoModule;

        public bool ApertureDoorOpen;
        public double BlockedApertureFraction;
        public string BlockingPartTitle;
        public AttitudeControlMode ControlMode;
        public double ControlTorqueNm;
        public double InertiaKgM2;

        /// <summary>Electric charge available on the vessel, in KSP's units.</summary>
        public double ElectricCharge;

        /// <summary>Total battery capacity, same units. What the charge is a fraction of, for the panel's readout and the ledger's ceiling.</summary>
        public double ElectricChargeCapacity;

        /// <summary>True when the vessel has a working CommNet link back to the space centre.</summary>
        public bool HasCommLink;

        /// <summary>Link signal strength, 0-1, and the raw antenna data rate in bits/s.</summary>
        public double SignalStrength;
        public double DownlinkBitsPerSecond;

        /// <summary>True when the aperture is open, unobstructed, powered and pointable: everything except the sky geometry.</summary>
        public bool Operational =>
            Instrument != null
            && Instrument.SpacePlatform != null
            && ApertureDoorOpen
            && ApertureObstruction.IsClear(BlockedApertureFraction)
            && ControlMode != AttitudeControlMode.Uncontrolled
            && ElectricCharge > 0.01;

        /// <summary>Why it is not operational, in one phrase, or null when it is.</summary>
        public string BlockingReason
        {
            get
            {
                if (Instrument == null || Instrument.SpacePlatform == null) return "instrument not configured";
                if (!ApertureDoorOpen) return "aperture door closed";
                if (!ApertureObstruction.IsClear(BlockedApertureFraction))
                    return string.IsNullOrEmpty(BlockingPartTitle)
                        ? string.Format("aperture blocked ({0:P0})", BlockedApertureFraction)
                        : string.Format("aperture blocked ({0:P0}) by {1}", BlockedApertureFraction, BlockingPartTitle);
                if (ControlMode == AttitudeControlMode.Uncontrolled) return "no attitude control";
                if (ElectricCharge <= 0.01) return "no electric charge";
                return null;
            }
        }

        /// <summary>
        /// True when this telescope can be COMMANDED from the space centre, as opposed to only
        /// while the player is flying it.
        ///
        /// The distinction is the real one: a telescope with no radio is not a broken telescope,
        /// it is a telescope an astronaut has to be next to. Commanding it needs a link; taking
        /// the exposure needs only power and a clear aperture.
        /// </summary>
        public bool CanOperateRemotely => Operational && HasCommLink;

        /// <summary>Observer position, metres, relative to the centre of the body it orbits.</summary>
        public SkyVector PositionFromHostBody()
        {
            if (Vessel == null || Vessel.mainBody == null) return new SkyVector(0, 0, 0);
            Vector3d p = Vessel.GetWorldPos3D() - Vessel.mainBody.position;
            return new SkyVector(p.x, p.y, p.z);
        }

        /// <summary>Unit normal of the vessel's orbital plane, for the visibility window.</summary>
        public SkyVector OrbitNormal()
        {
            if (Vessel == null || Vessel.orbit == null) return new SkyVector(0, 0, 0);
            Vector3d n = Vessel.orbit.GetOrbitNormal();
            double m = n.magnitude;
            if (!(m > 0.0)) return new SkyVector(0, 0, 0);
            return new SkyVector(n.x / m, n.y / m, n.z / m);
        }

        public double OrbitPeriodSeconds =>
            Vessel != null && Vessel.orbit != null ? Vessel.orbit.period : 0.0;
    }

    /// <summary>
    /// Finds every space telescope in the save, loaded or not.
    ///
    /// WHY IT SCANS PROTOVESSELS AND DOES NOT JUST KEEP A LIST OF MODULES. A PartModule only
    /// exists while its vessel is loaded. The telescope the player wants to use from the space
    /// centre is, by construction, not loaded: they are standing in the observatory and it is in
    /// orbit somewhere. So the authoritative source has to be the save's own vessel list, with
    /// the module's persistent fields read out of the protovessel; the live module, when there is
    /// one, only supplies fresher values for the same fields.
    ///
    /// This is also why ModuleExoSpaceTelescope persists its measured obstruction and control
    /// authority: those are the two things that cannot be recomputed without colliders and a
    /// physics frame, and they are exactly the two the ground-operations mode has to know.
    /// </summary>
    public static class SpaceTelescopeRegistry
    {
        private const string ModuleName = "ModuleExoSpaceTelescope";

        private static readonly List<ModuleExoSpaceTelescope> loaded = new List<ModuleExoSpaceTelescope>();

        internal static void Register(ModuleExoSpaceTelescope module)
        {
            if (module != null && !loaded.Contains(module)) loaded.Add(module);
        }

        internal static void Unregister(ModuleExoSpaceTelescope module)
        {
            loaded.Remove(module);
        }

        /// <summary>
        /// Every telescope in the save that is in a state worth listing: on a vessel that is in
        /// orbit (or at least not landed or splashed, since a telescope on the ground is not a
        /// space telescope) and that carries the module.
        /// </summary>
        public static List<SpaceTelescopeLink> FindAll()
        {
            var links = new List<SpaceTelescopeLink>();
            if (FlightGlobals.Vessels == null) return links;

            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel v = FlightGlobals.Vessels[i];
                if (v == null || v.state == Vessel.State.DEAD) continue;
                if (v.LandedOrSplashed) continue;

                SpaceTelescopeLink link = v.loaded ? FromLoaded(v) : FromProto(v);
                if (link != null) links.Add(link);
            }
            return links;
        }

        private static SpaceTelescopeLink FromLoaded(Vessel v)
        {
            ModuleExoSpaceTelescope module = null;
            for (int i = 0; i < loaded.Count; i++)
            {
                if (loaded[i] == null || loaded[i].vessel != v) continue;
                module = loaded[i];
                break;
            }
            if (module == null)
            {
                List<ModuleExoSpaceTelescope> found = v.FindPartModulesImplementing<ModuleExoSpaceTelescope>();
                if (found == null || found.Count == 0) return null;
                module = found[0];
            }
            if (module.Instrument == null) return null;

            var link = new SpaceTelescopeLink
            {
                Vessel = v,
                VesselName = v.vesselName,
                Instrument = module.Instrument,
                AlternateInstrumentName = module.alternateInstrumentName ?? "",
                Module = module,
                ApertureDoorOpen = module.apertureDoorOpen,
                BlockedApertureFraction = module.BlockedApertureFraction,
                BlockingPartTitle = module.BlockingPartTitle,
                ControlMode = module.ControlMode,
                ControlTorqueNm = module.controlTorqueCached,
                InertiaKgM2 = module.inertiaCached,
            };
            FillVesselState(link, v);
            return link;
        }

        // The unloaded path: walk the protovessel's parts for our module and read its persistent fields out of
        // the saved ConfigNode.
        private static SpaceTelescopeLink FromProto(Vessel v)
        {
            if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) return null;

            for (int i = 0; i < v.protoVessel.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot part = v.protoVessel.protoPartSnapshots[i];
                if (part == null || part.modules == null) continue;

                for (int j = 0; j < part.modules.Count; j++)
                {
                    ProtoPartModuleSnapshot m = part.modules[j];
                    if (m == null || m.moduleName != ModuleName) continue;

                    ConfigNode node = m.moduleValues;
                    if (node == null) continue;

                    // The node first, the part's prefab second. The node is authoritative when it
                    // has the field, which covers a part config overridden per vessel; the prefab
                    // covers every telescope saved before instrumentName became persistent, whose
                    // module node carries the measured state and not the instrument's name. The
                    // prefab always has it, because that is where the part config was read.
                    string instrumentName = node.GetValue("instrumentName");
                    if (string.IsNullOrEmpty(instrumentName)) instrumentName = PrefabInstrumentName(part);

                    VisualTelescopeSpec spec = FindInstrument(instrumentName);
                    if (spec == null || spec.SpacePlatform == null) continue;

                    string alternateName = node.GetValue("alternateInstrumentName");
                    if (string.IsNullOrEmpty(alternateName)) alternateName = PrefabAlternateName(part);

                    var link = new SpaceTelescopeLink
                    {
                        Vessel = v,
                        VesselName = v.vesselName,
                        Instrument = spec,
                        AlternateInstrumentName = alternateName ?? "",
                        Module = null,
                        ProtoModule = m,
                        ApertureDoorOpen = ReadBool(node, "apertureDoorOpen", false),
                        BlockedApertureFraction = ReadDouble(node, "blockedApertureFractionCached", 0.0),
                        BlockingPartTitle = node.GetValue("blockingPartCached"),
                        ControlMode = ReadControlMode(node),
                        ControlTorqueNm = ReadDouble(node, "controlTorqueCached", 0.0),
                        InertiaKgM2 = ReadDouble(node, "inertiaCached", 0.0),
                    };
                    FillVesselState(link, v);
                    return link;
                }
            }
            return null;
        }

        // The instrument name off the part's prefab module, which is the part config as loaded. Null when the
        // prefab is unavailable, which is a part whose config failed to load and which cannot be observed
        // through anyway.
        private static string PrefabInstrumentName(ProtoPartSnapshot part)
        {
            ModuleExoSpaceTelescope prefab = PrefabModule(part);
            return prefab != null ? prefab.instrumentName : null;
        }

        private static string PrefabAlternateName(ProtoPartSnapshot part)
        {
            ModuleExoSpaceTelescope prefab = PrefabModule(part);
            return prefab != null ? prefab.alternateInstrumentName : null;
        }

        private static ModuleExoSpaceTelescope PrefabModule(ProtoPartSnapshot part)
        {
            if (part == null || part.partInfo == null || part.partInfo.partPrefab == null) return null;
            List<ModuleExoSpaceTelescope> prefabs =
                part.partInfo.partPrefab.FindModulesImplementing<ModuleExoSpaceTelescope>();
            return prefabs != null && prefabs.Count > 0 ? prefabs[0] : null;
        }

        // The parts of the state that come from the vessel rather than from the module: its power and its radio
        // link. Both work on an unloaded vessel, which is why they are not cached in the module the way the
        // geometry is.
        private static void FillVesselState(SpaceTelescopeLink link, Vessel v)
        {
            GroundStation.TotalElectricCharge(v, out double charge, out double capacity);
            link.ElectricCharge = charge;
            link.ElectricChargeCapacity = capacity;

            if (v.Connection != null)
            {
                link.HasCommLink = v.Connection.IsConnected;
                link.SignalStrength = v.Connection.SignalStrength;
                link.DownlinkBitsPerSecond = AntennaBitsPerSecond(v);
            }
        }

        // The vessel's best antenna data rate, bits per second. KSP publishes antenna performance as packetSize
        // (in Mits, its own unit) per packetInterval (seconds), so the rate is one divided by the other; the
        // "Mit" is a megabit and the conversion is the game's own. The BEST antenna is used rather than the
        // sum, because a downlink runs over one link at a time.
        private static double AntennaBitsPerSecond(Vessel v)
        {
            double best = 0.0;

            if (v.loaded)
            {
                List<ModuleDataTransmitter> transmitters = v.FindPartModulesImplementing<ModuleDataTransmitter>();
                if (transmitters == null) return 0.0;
                for (int i = 0; i < transmitters.Count; i++)
                {
                    ModuleDataTransmitter t = transmitters[i];
                    if (t == null || !(t.packetInterval > 0f)) continue;
                    double bps = t.packetSize / t.packetInterval * 1.0e6;
                    if (bps > best) best = bps;
                }
                return best;
            }

            if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) return 0.0;
            for (int i = 0; i < v.protoVessel.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot p = v.protoVessel.protoPartSnapshots[i];
                if (p == null || p.partInfo == null || p.partInfo.partPrefab == null) continue;

                // The prefab carries the antenna's specs; the snapshot only carries what changed.
                List<ModuleDataTransmitter> prefabs =
                    p.partInfo.partPrefab.FindModulesImplementing<ModuleDataTransmitter>();
                if (prefabs == null) continue;
                for (int j = 0; j < prefabs.Count; j++)
                {
                    ModuleDataTransmitter t = prefabs[j];
                    if (t == null || !(t.packetInterval > 0f)) continue;
                    double bps = t.packetSize / t.packetInterval * 1.0e6;
                    if (bps > best) best = bps;
                }
            }
            return best;
        }

        private static AttitudeControlMode ReadControlMode(ConfigNode node)
        {
            string s = node.GetValue("controlModeCached");
            if (string.IsNullOrEmpty(s)) return AttitudeControlMode.Uncontrolled;
            try
            {
                return (AttitudeControlMode)Enum.Parse(typeof(AttitudeControlMode), s, true);
            }
            catch (ArgumentException)
            {
                return AttitudeControlMode.Uncontrolled;
            }
        }

        private static bool ReadBool(ConfigNode node, string key, bool fallback)
        {
            string s = node.GetValue(key);
            return !string.IsNullOrEmpty(s) && bool.TryParse(s, out bool v) ? v : fallback;
        }

        private static double ReadDouble(ConfigNode node, string key, double fallback)
        {
            string s = node.GetValue(key);
            return !string.IsNullOrEmpty(s)
                && double.TryParse(s, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? v : fallback;
        }

        internal static VisualTelescopeSpec FindInstrument(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            VisualTelescopeSpec[] all = VisualTelescopeCatalog.All;
            for (int i = 0; i < all.Length; i++)
                if (string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase)) return all[i];
            return null;
        }
    }
}
