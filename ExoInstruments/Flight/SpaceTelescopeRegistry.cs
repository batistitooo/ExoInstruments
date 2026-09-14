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

        /// <summary>Part.persistentId of the part carrying this telescope. Unlike Vessel.id it tells two telescopes on one vessel apart, and it survives load, unload, save and docking.</summary>
        public uint PartPersistentId;

        /// <summary>Index among the telescope modules on that part; 0 on every shipped part.</summary>
        public int ModuleOrdinal;

        /// <summary>This telescope's identity, built from the two above by MakeKey.</summary>
        public string Key = "";

        public static string MakeKey(uint partPersistentId, int moduleOrdinal) =>
            partPersistentId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
            + moduleOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

        /// <summary>True when this telescope is sitting on a surface rather than flying over one.</summary>
        public bool IsLanded;

        /// <summary>True when the body it is standing on has an atmosphere. Meaningless in flight.</summary>
        public bool SurfaceBodyHasAtmosphere;

        /// <summary>Name of the body it is standing on, for the panel's own message.</summary>
        public string SurfaceBodyName = "";

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
        /// <summary>True when this telescope has a door at all; one that has none is never shut.</summary>
        public bool ApertureUncovered =>
            Instrument == null || Instrument.SpacePlatform == null
            || !Instrument.SpacePlatform.HasApertureDoor
            || ApertureDoorOpen;

        /// <summary>Whether this telescope is where its instrument needs to be, and null when it is.</summary>
        public string SitingProblem
        {
            get
            {
                SpacePlatformSpec platform = Instrument != null ? Instrument.SpacePlatform : null;
                if (platform == null) return null;

                if (platform.IsSurfaceObservatory)
                {
                    if (!IsLanded) return "not landed: this observatory has to stand on a surface";
                    if (platform.RequiresAirlessSurface && SurfaceBodyHasAtmosphere)
                        return string.IsNullOrEmpty(SurfaceBodyName)
                            ? "this atmosphere absorbs the band it works in"
                            : SurfaceBodyName + "'s atmosphere absorbs the band it works in";
                    return null;
                }

                return IsLanded ? "on the ground: this telescope has to be in flight" : null;
            }
        }

        public bool Operational =>
            Instrument != null
            && Instrument.SpacePlatform != null
            && SitingProblem == null
            && ApertureUncovered
            && ApertureObstruction.IsClear(BlockedApertureFraction)
            // An observatory standing on a surface is held still by the ground, not by wheels.
            && (Instrument.SpacePlatform.IsSurfaceObservatory
                || ControlMode != AttitudeControlMode.Uncontrolled)
            && ElectricCharge > 0.01;

        /// <summary>Why it is not operational, in one phrase, or null when it is.</summary>
        public string BlockingReason
        {
            get
            {
                if (Instrument == null || Instrument.SpacePlatform == null) return "instrument not configured";
                string siting = SitingProblem;
                if (siting != null) return siting;
                if (!ApertureUncovered) return "aperture door closed";
                if (!ApertureObstruction.IsClear(BlockedApertureFraction))
                    return string.IsNullOrEmpty(BlockingPartTitle)
                        ? string.Format("aperture blocked ({0:P0})", BlockedApertureFraction)
                        : string.Format("aperture blocked ({0:P0}) by {1}", BlockedApertureFraction, BlockingPartTitle);
                if (!Instrument.SpacePlatform.IsSurfaceObservatory
                    && ControlMode == AttitudeControlMode.Uncontrolled) return "no attitude control";
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
        /// The loaded telescope whose command this vessel is flying, or null. The rule is
        /// GroundStation.OutranksForAttitude's; this applies it to the live modules without allocating,
        /// for callers that run every frame.
        /// </summary>
        internal static ModuleExoSpaceTelescope LoadedAttitudeOwner(Vessel v)
        {
            ModuleExoSpaceTelescope best = null;
            for (int i = 0; i < loaded.Count; i++)
            {
                ModuleExoSpaceTelescope m = loaded[i];
                if (m == null || m.vessel != v || !m.pointingHoldEnabled) continue;
                if (best == null || GroundStation.OutranksForAttitude(
                        m.slewStartUt, PartIdOf(m), OrdinalOf(m),
                        best.slewStartUt, PartIdOf(best), OrdinalOf(best)))
                    best = m;
            }
            return best;
        }

        private static uint PartIdOf(ModuleExoSpaceTelescope module) =>
            module != null && module.part != null ? module.part.persistentId : 0u;

        // Index of this module among the telescope modules on its part, in the order KSP saves them.
        internal static int OrdinalOf(ModuleExoSpaceTelescope module)
        {
            if (module == null || module.part == null || module.part.Modules == null) return 0;
            int n = 0;
            for (int i = 0; i < module.part.Modules.Count; i++)
            {
                PartModule m = module.part.Modules[i];
                if (m == module) return n;
                if (m is ModuleExoSpaceTelescope) n++;
            }
            return 0;
        }

        internal static string KeyOf(ModuleExoSpaceTelescope module) =>
            SpaceTelescopeLink.MakeKey(PartIdOf(module), OrdinalOf(module));

        /// <summary>
        /// Every telescope in the save worth listing, in orbit or standing on a surface.
        ///
        /// Landed vessels used to be dropped here on the grounds that a telescope on the ground is
        /// not a space telescope. That was true while every instrument in the catalogue was meant
        /// to fly; a surface observatory is a real thing, and whether a particular one can work
        /// where it was put is a question for the instrument, not for this scan.
        /// </summary>
        public static List<SpaceTelescopeLink> FindAll()
        {
            var links = new List<SpaceTelescopeLink>();
            if (FlightGlobals.Vessels == null) return links;

            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel v = FlightGlobals.Vessels[i];
                if (v == null || v.state == Vessel.State.DEAD) continue;
                AddTelescopes(v, links, true);
            }
            return links;
        }

        /// <summary>
        /// Every telescope aboard one vessel, without the power and radio state FindAll fills in. What the
        /// ground station needs to treat a vessel as the one body it is: one attitude and one battery,
        /// however many telescopes it carries.
        /// </summary>
        public static List<SpaceTelescopeLink> OnVessel(Vessel v)
        {
            var links = new List<SpaceTelescopeLink>();
            if (v != null && v.state != Vessel.State.DEAD) AddTelescopes(v, links, false);
            return links;
        }

        // ONE LINK PER TELESCOPE MODULE, not per vessel. Stopping at the first one found hid every other
        // telescope aboard: a craft carrying WFPC2 and LORRI could only ever use whichever came first.
        private static void AddTelescopes(Vessel v, List<SpaceTelescopeLink> links, bool withVesselState)
        {
            int first = links.Count;
            if (v.loaded) AddLoaded(v, links);
            else AddProto(v, links);
            if (withVesselState && links.Count > first) FillVesselState(links, first, v);
        }

        private static void AddLoaded(Vessel v, List<SpaceTelescopeLink> links)
        {
            List<ModuleExoSpaceTelescope> found = v.FindPartModulesImplementing<ModuleExoSpaceTelescope>();
            if (found == null) return;

            for (int k = 0; k < found.Count; k++)
            {
                ModuleExoSpaceTelescope module = found[k];
                if (module == null || module.Instrument == null) continue;

                uint partId = PartIdOf(module);
                int ordinal = OrdinalOf(module);
                links.Add(new SpaceTelescopeLink
                {
                    Vessel = v,
                    VesselName = v.vesselName,
                    PartPersistentId = partId,
                    ModuleOrdinal = ordinal,
                    Key = SpaceTelescopeLink.MakeKey(partId, ordinal),
                    IsLanded = v.LandedOrSplashed,
                    SurfaceBodyHasAtmosphere = v.mainBody != null && v.mainBody.atmosphere,
                    SurfaceBodyName = v.mainBody != null ? v.mainBody.bodyName : "",
                    Instrument = module.Instrument,
                    AlternateInstrumentName = module.alternateInstrumentName ?? "",
                    Module = module,
                    ApertureDoorOpen = module.apertureDoorOpen,
                    BlockedApertureFraction = module.BlockedApertureFraction,
                    BlockingPartTitle = module.BlockingPartTitle,
                    ControlMode = module.ControlMode,
                    ControlTorqueNm = module.controlTorqueCached,
                    InertiaKgM2 = module.inertiaCached,
                });
            }
        }

        // The unloaded path: walk the protovessel's parts for our modules and read their persistent fields out of
        // the saved ConfigNodes.
        private static void AddProto(Vessel v, List<SpaceTelescopeLink> links)
        {
            if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) return;

            for (int i = 0; i < v.protoVessel.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot part = v.protoVessel.protoPartSnapshots[i];
                if (part == null || part.modules == null) continue;

                // Counted the way OrdinalOf counts the live modules: KSP builds a snapshot's module list by
                // walking Part.Modules, so the two orders agree.
                int nextOrdinal = 0;
                for (int j = 0; j < part.modules.Count; j++)
                {
                    ProtoPartModuleSnapshot m = part.modules[j];
                    if (m == null || m.moduleName != ModuleName) continue;
                    int ordinal = nextOrdinal++;

                    ConfigNode node = m.moduleValues;
                    if (node == null) continue;

                    // The node first, the part's prefab second. The node is authoritative when it
                    // has the field, which covers a part config overridden per vessel; the prefab
                    // covers every telescope saved before instrumentName became persistent, whose
                    // module node carries the measured state and not the instrument's name. The
                    // prefab always has it, because that is where the part config was read.
                    string instrumentName = node.GetValue("instrumentName");
                    if (string.IsNullOrEmpty(instrumentName)) instrumentName = PrefabInstrumentName(part, ordinal);

                    VisualTelescopeSpec spec = FindInstrument(instrumentName);
                    if (spec == null || spec.SpacePlatform == null) continue;

                    string alternateName = node.GetValue("alternateInstrumentName");
                    if (string.IsNullOrEmpty(alternateName)) alternateName = PrefabAlternateName(part, ordinal);

                    links.Add(new SpaceTelescopeLink
                    {
                        Vessel = v,
                        VesselName = v.vesselName,
                        PartPersistentId = part.persistentId,
                        ModuleOrdinal = ordinal,
                        Key = SpaceTelescopeLink.MakeKey(part.persistentId, ordinal),
                        IsLanded = v.LandedOrSplashed,
                        SurfaceBodyHasAtmosphere = v.mainBody != null && v.mainBody.atmosphere,
                        SurfaceBodyName = v.mainBody != null ? v.mainBody.bodyName : "",
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
                    });
                }
            }
        }

        // The instrument name off the part's prefab module, which is the part config as loaded. Null when the
        // prefab is unavailable, which is a part whose config failed to load and which cannot be observed
        // through anyway.
        private static string PrefabInstrumentName(ProtoPartSnapshot part, int ordinal)
        {
            ModuleExoSpaceTelescope prefab = PrefabModule(part, ordinal);
            return prefab != null ? prefab.instrumentName : null;
        }

        private static string PrefabAlternateName(ProtoPartSnapshot part, int ordinal)
        {
            ModuleExoSpaceTelescope prefab = PrefabModule(part, ordinal);
            return prefab != null ? prefab.alternateInstrumentName : null;
        }

        private static ModuleExoSpaceTelescope PrefabModule(ProtoPartSnapshot part, int ordinal)
        {
            if (part == null || part.partInfo == null || part.partInfo.partPrefab == null) return null;
            List<ModuleExoSpaceTelescope> prefabs =
                part.partInfo.partPrefab.FindModulesImplementing<ModuleExoSpaceTelescope>();
            return prefabs != null && ordinal >= 0 && ordinal < prefabs.Count ? prefabs[ordinal] : null;
        }

        // The parts of the state that come from the vessel rather than from the module: its power and its radio
        // link. Both work on an unloaded vessel, which is why they are not cached in the module the way the
        // geometry is. Measured once and shared by every telescope aboard, which draw on the same battery.
        private static void FillVesselState(List<SpaceTelescopeLink> links, int first, Vessel v)
        {
            GroundStation.TotalElectricCharge(v, out double charge, out double capacity);

            bool hasConnection = v.Connection != null;
            bool connected = hasConnection && v.Connection.IsConnected;
            double signal = hasConnection ? v.Connection.SignalStrength : 0.0;
            double bps = hasConnection ? AntennaBitsPerSecond(v) : 0.0;

            for (int i = first; i < links.Count; i++)
            {
                SpaceTelescopeLink link = links[i];
                link.ElectricCharge = charge;
                link.ElectricChargeCapacity = capacity;
                if (!hasConnection) continue;
                link.HasCommLink = connected;
                link.SignalStrength = signal;
                link.DownlinkBitsPerSecond = bps;
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
