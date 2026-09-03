using System;
using System.Linq;
using System.Text;
using UnityEngine;
using KSP.UI.Screens.DebugToolbar;
using ExoInstruments.Core;

namespace ExoInstruments
{
    /// <summary>
    /// Builds a real, upgradeable KSC facility for the observatory, following the
    /// same pipeline ResearchBodies uses for its own Observatory:
    ///
    /// 1. At PSystemSpawn (once per game session, before any scene renders), clone
    ///    an existing real Upgradeables.UpgradeableFacility (Research &amp; Development's)
    ///    rather than raw-AddComponent one; the clone already has a non-null
    ///    UpgradeLevels array by the time its own Awake() runs, so we sidestep
    ///    whatever null-array assumptions the stock Awake() makes and only replace
    ///    UpgradeLevels afterwards, once the clone is already alive. This makes the
    ///    level (and its persistence across saves, via UpgradeableObject's own
    ///    id/level bookkeeping) a genuine stock facility, not a hand-rolled one.
    /// 2. At the first SPACECENTER scene load (once real stock buildings exist to
    ///    borrow a tooltip prefab from), attach a SpaceCenterBuilding-derived
    ///    component. That base class is what stock KSC buildings (VAB, R&amp;D, ...)
    ///    use for hover highlight (HighLightBuilding), the info tooltip, and click
    ///    routing, all for free, not re-implemented here.
    ///
    /// Skipped relative to ResearchBodies' pipeline: registering a
    /// PSystemSetup.SpaceCenterFacility entry (their building-picker/camera-fly-to
    /// integration, not required for glow/tooltip/click/upgrade, which come from
    /// UpgradeableFacility + SpaceCenterBuilding directly) and DestructibleBuilding
    /// wreck/collapse wiring (keyed to child-object names baked into their .mu
    /// export, which ours doesn't share).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.PSystemSpawn, true)]
    public class ExoObservatoryFacility : MonoBehaviour
    {
        // Shared by _facility.name, the SpaceCenterBuilding's facilityName, and
        // the PSystemSetup.SpaceCenterFacility registry entry below; all three
        // must match, since SpaceCenterBuilding resolves its facility by looking
        // this name up in PSystemSetup, not just by walking up its parent chain.
        private const string FacilityName = "ExoObservatory";
        private const string FacilityId = "SpaceCenter/ExoObservatory";
        // THE MODEL'S TEXTURE IS ONE FLAT WHITE PIXEL BLOCK, AND THAT IS ON PURPOSE.
        //
        // Four of its meshes carry a real KSP shader: the tower (WallTexture, _Color 0.774 grey)
        // and the dome with both door leaves (Door, _Color 0.887 near-white). The other 240
        // materials are untextured Unity Standard, straight off the OBJ import. So the whole
        // silhouette of the building is those four meshes, and their look is the _Color tint
        // alone: grey concrete tower, white dome, which is what a real observatory looks like.
        //
        // It cannot be anything else yet, because the meshes have no UV layout. Their UVs are
        // raw object-space coordinates running -624..+471, so any pattern would tile several
        // hundred times across a single panel. ExoObservatoryFlatWhite.png is therefore the only
        // map that can be correct here: white, so _Color reads exactly as authored, and flat, so
        // the tiling has nothing to alias. Unwrap the meshes first, then a real texture can drop
        // in under the same filename with no change to the .mu.
        //
        // What it replaced was a stray "Wipe Pattern - Diagonal.png" from TextMesh Pro's example
        // assets, which never shipped and which KSP logged as a missing texture on every load.
        // `python3 tools/dump_mu.py` checks every texture a model names against the files beside
        // it and exits non-zero when one is absent.
        private const string Level1ModelUrl = "ExoInstruments/Parts/ExoObservatoryLVL1";
        private const float Level1Cost = 30000f;
        // TODO: reintroduce ExoObservatoryLVL2 as a second UpgradeLevel once
        // level 1 is confirmed visible in-game.

        // Same local offset ResearchBodies uses for its own KSC Observatory,
        // relative to the PQSCity's "SpaceCenter" child transform.
        private static readonly Vector3 LocalOffset = new Vector3(-246.6104f, 24.34455f, -216.4676f);

        // Detailed trace logging requested for diagnosing the click/highlight
        // issue, deliberately a different prefix from the mod's normal
        // "[ExoInstruments]" warnings, so `grep "[Exoplanets]"` isolates just
        // this trace from the rest of KSP.log.
        private const string DebugPrefix = "[Exoplanets]";
        private static void LogDebug(string message) => Debug.Log($"{DebugPrefix} {message}");

        private static ExoObservatoryFacility _instance;

        private PSystemSetup.SpaceCenterFacility _facilityEntry;
        private Upgradeables.UpgradeableFacility _facility;
        private ExoObservatoryBuilding _building;

        private void Awake()
        {
            if (_instance != null)
            {
                Destroy(this);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            RegisterSpaceCenterFacility();
            PSystemManager.Instance.OnPSystemReady.Add(BuildFacility);
            GameEvents.onLevelWasLoaded.Add(HandleLevelWasLoaded);
        }

        // Injects a PSystemSetup.SpaceCenterFacility entry named FacilityName;
        // SpaceCenterBuilding.SetupFacility() looks a building's facility up by this name via
        // PSystemSetup.GetSpaceCenterFacility() and aborts its whole setup coroutine (colliders, renderers,
        // highlight) if it's missing, which is what silently kept the observatory invisible.
        private void RegisterSpaceCenterFacility()
        {
            var trackingStation = PSystemSetup.Instance.GetSpaceCenterFacility("TrackingStation");
            if (trackingStation == null)
            {
                Debug.LogWarning("[ExoInstruments] Could not find the TrackingStation facility entry to borrow a pqsName from; observatory facility not registered.");
                return;
            }

            _facilityEntry = new PSystemSetup.SpaceCenterFacility
            {
                name = FacilityName,
                facilityName = "",
                facilityTransformName = "KSC/SpaceCenter/" + FacilityName,
                pqsName = trackingStation.pqsName,
                spawnPoints = new PSystemSetup.SpaceCenterFacility.SpawnPoint[0],
            };

            var facilities = PSystemSetup.Instance.SpaceCenterFacilities;
            var newFacilities = new PSystemSetup.SpaceCenterFacility[facilities.Length + 1];
            Array.Copy(facilities, newFacilities, facilities.Length);
            newFacilities[facilities.Length] = _facilityEntry;
            PSystemSetup.Instance.SpaceCenterFacilities = newFacilities;
        }

        // Clones a real UpgradeableFacility and gives it our two levels. Runs once, before any scene renders.
        private void BuildFacility()
        {
            PSystemManager.Instance.OnPSystemReady.Remove(BuildFacility);
            LogDebug("BuildFacility() starting.");

            try
            {
                Transform spaceCenterTransform = FindKscSpaceCenterTransform();
                if (spaceCenterTransform == null)
                {
                    Debug.LogWarning("[ExoInstruments] Could not find the KSC's PQSCity/SpaceCenter transform; observatory facility not built.");
                    LogDebug("ERROR: FindKscSpaceCenterTransform() returned null. Aborting.");
                    return;
                }
                LogDebug($"KSC SpaceCenter transform found: '{spaceCenterTransform.name}' at world position {spaceCenterTransform.position}.");

                var rndTemplate = Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>()
                    .FirstOrDefault(f => f.name == "ResearchAndDevelopment");
                if (rndTemplate == null)
                {
                    Debug.LogWarning("[ExoInstruments] Could not find the ResearchAndDevelopment facility to clone; observatory facility not built.");
                    LogDebug("ERROR: ResearchAndDevelopment UpgradeableFacility template not found. Aborting.");
                    return;
                }
                LogDebug("ResearchAndDevelopment template found, will clone it.");

                GameObject level1Model = GameDatabase.Instance.GetModel(Level1ModelUrl);
                if (level1Model == null)
                {
                    Debug.LogWarning($"[ExoInstruments] Could not load observatory model '{Level1ModelUrl}'; observatory facility not built.");
                    LogDebug($"ERROR: GameDatabase.Instance.GetModel('{Level1ModelUrl}') returned null. Aborting.");
                    return;
                }
                LogDebug($"Model loaded: '{Level1ModelUrl}' -> GameObject '{level1Model.name}'.");

                _facility = Instantiate(rndTemplate, spaceCenterTransform.position, spaceCenterTransform.rotation);
                _facility.name = FacilityName;
                _facility.id = FacilityId;
                _facility.transform.NestToParent(spaceCenterTransform);
                _facility.transform.localPosition = LocalOffset;
                LogDebug($"Facility clone created: '{_facility.name}' (id='{_facility.id}'), parent='{_facility.transform.parent?.name}', localPosition={_facility.transform.localPosition}, worldPosition={_facility.transform.position}.");

                // Setup(pqsArray) resolves facilityTransformName ("KSC/SpaceCenter/
                // ExoObservatory") by walking the actual PQS hierarchy, so it has to
                // run AFTER _facility (named "ExoObservatory", nested under
                // SpaceCenter above) exists; calling it any earlier logged
                // "Cannot find facility named 'KSC/SpaceCenter/ExoObservatory' on
                // pqs 'Kerbin'" and left facilityTransform/facilityPQS/hostBody null.
                _facilityEntry?.Setup(Resources.FindObjectsOfTypeAll<PQS>());
                if (_facilityEntry != null)
                {
                    LogDebug($"PSystemSetup.SpaceCenterFacility entry after Setup(): facilityTransform={(_facilityEntry.facilityTransform != null ? _facilityEntry.facilityTransform.name : "NULL")}, facilityPQS={(_facilityEntry.facilityPQS != null ? "set" : "NULL")}, hostBody={(_facilityEntry.hostBody != null ? _facilityEntry.hostBody.name : "NULL")}.");
                }

                // Instantiate() already ran Awake() synchronously on the clone, which
                // spawned R&D's own facilityPrefab for whatever level it was cloned at;
                // that's the ghost "second R&D building" that showed up at our
                // position. Get rid of it before swapping in our own levels, then
                // force a respawn so our level 0 model actually appears (replacing
                // UpgradeLevels alone doesn't trigger one).
                _facility.CurrentLevel?.Despawn();

                // ResearchBodies keeps its level prefab templates activeSelf=true (so
                // Spawn()'s Instantiate() clone comes out already active; Spawn()
                // itself never calls SetActive on it) but parks them under a GameObject
                // it then sets inactive, so the templates themselves don't render as
                // stray duplicates before anything spawns them. Same trick here.
                var templateHolder = new GameObject("ExoObservatoryTemplates");
                DontDestroyOnLoad(templateHolder);

                var level1 = BuildLevel(level1Model, Level1Cost, "Level 1", _facility.transform, templateHolder.transform);
                AddGroundBase(level1.facilityPrefab, _facility.transform);
                templateHolder.SetActive(false);

                _facility.UpgradeLevels = new[] { level1 };

                DontDestroyOnLoad(_facility.gameObject);

                // SetLevel()/Spawn() reads each UpgradeLevel's private p0/s0/r0/host
                // fields, which only get populated by Setup(host), normally called
                // internally the first time the ORIGINAL (R&D) array was set up.
                // Our replacement levels never went through that, so without this
                // call Spawn() NREs on a null host.
                _facility.SetupLevels();

                // Clamp in case a prior save persisted a FacilityLevel index that was
                // only valid for R&D's own (longer) UpgradeLevels array.
                int levelToShow = Mathf.Clamp(_facility.FacilityLevel, 0, _facility.UpgradeLevels.Length - 1);
                LogDebug($"Calling SetLevel({levelToShow}) (FacilityLevel was {_facility.FacilityLevel}, {_facility.UpgradeLevels.Length} level(s) available).");
                _facility.SetLevel(levelToShow);

                var spawned = _facility.CurrentLevel?.facilityInstance;
                LogDebug(spawned != null
                    ? $"BuildFacility() done. Spawned instance: '{spawned.name}', activeSelf={spawned.activeSelf}, activeInHierarchy={spawned.activeInHierarchy}, worldPosition={spawned.transform.position}."
                    : "BuildFacility() done, but CurrentLevel.facilityInstance is NULL; SetLevel()/Spawn() did not produce a visible instance.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{DebugPrefix} EXCEPTION in BuildFacility(): {ex}");
                throw;
            }
        }

        private static Upgradeables.UpgradeableObject.UpgradeLevel BuildLevel(GameObject model, float cost, string label, Transform hostTransform, Transform templateHolder)
        {
            var prefab = Instantiate(model, hostTransform.position, hostTransform.rotation);
            prefab.name = "ExoObservatory_" + label.Replace(" ", "");
            prefab.transform.SetParent(templateHolder, true); // worldPositionStays; keep the pose we just instantiated it at.
            prefab.SetActive(true);
            DontDestroyOnLoad(prefab);

            var levelText = ScriptableObject.CreateInstance<KSCUpgradeableLevelText>();
            levelText.facility = SpaceCenterFacility.TrackingStation; // stock enum can't be extended; ResearchBodies uses the same filler value.
            levelText.textBase = label;
            levelText.linePrefix = "* ";

            return new Upgradeables.UpgradeableObject.UpgradeLevel
            {
                facilityPrefab = prefab,
                levelCost = cost,
                levelText = levelText,
                levelStats = new Upgradeables.KSCFacilityLevelText
                {
                    facility = SpaceCenterFacility.TrackingStation,
                    textBase = label,
                    linePrefix = "* ",
                },
            };
        }

        // Local offset for the cloned ground patch, relative to the facility's
        // own transform, unknown good value yet, needs tuning against the
        // observatory's actual footprint once it's visible in-game. ResearchBodies'
        // own equivalent (groundBaseOffset) is (15, -9, -10), tuned for their
        // building; that's just a starting-point guess here, not measured for ours.
        private static readonly Vector3 GroundBaseOffset = new Vector3(15f, -9f, -10f);

        // Clones the ground patch mesh every stock KSC facility sits on (a flat, terrain-blending base) from
        // Mission Control's own prefab, since it isn't a separate asset on disk anywhere; it's baked into the
        // compiled KSC scene. Same technique ResearchBodies uses for its own Observatory: find Mission
        // Control's UpgradeableFacility in memory, grab the child tagged "KSC_Mission_Control_Grounds" off its
        // level-0 facilityPrefab, and clone that.
        private void AddGroundBase(GameObject targetPrefab, Transform attachPosition)
        {
            var missionControl = Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>()
                .FirstOrDefault(f => f.name == "MissionControl");
            if (missionControl == null || missionControl.UpgradeLevels == null || missionControl.UpgradeLevels.Length == 0)
            {
                LogDebug("WARNING: AddGroundBase: MissionControl facility/levels not found, no ground base added.");
                return;
            }

            var mcPrefab = missionControl.UpgradeLevels[0].facilityPrefab;
            if (mcPrefab == null)
            {
                LogDebug("WARNING: AddGroundBase: MissionControl level-0 facilityPrefab is null, no ground base added.");
                return;
            }

            GameObject groundSource = null;
            foreach (Transform child in mcPrefab.transform)
            {
                if (child.CompareTag("KSC_Mission_Control_Grounds"))
                {
                    groundSource = child.gameObject;
                    break;
                }
            }
            if (groundSource == null)
            {
                LogDebug("WARNING: AddGroundBase: no child tagged 'KSC_Mission_Control_Grounds' found under MissionControl's level-0 prefab, no ground base added.");
                return;
            }

            var groundInstance = Instantiate(groundSource, attachPosition.position, attachPosition.rotation);
            groundInstance.name = "ExoObservatoryGroundBase";
            groundInstance.transform.SetParent(targetPrefab.transform, true);
            groundInstance.transform.localPosition = GroundBaseOffset;
            LogDebug($"AddGroundBase: cloned '{groundSource.name}' from MissionControl, parented under '{targetPrefab.name}' at localPosition={GroundBaseOffset}.");
        }

        // Once real stock buildings exist in the scene (so we have a tooltip prefab to borrow), attach the
        // clickable/hoverable component. Idempotent; runs at most once.
        private void HandleLevelWasLoaded(GameScenes scene)
        {
            if (scene != GameScenes.SPACECENTER) return;
            if (_facility == null || _building != null) return;

            LogDebug("HandleLevelWasLoaded(SPACECENTER) starting, attaching ExoObservatoryBuilding.");

            try
            {
                var buildingGo = new GameObject("ExoObservatoryBuilding");
                buildingGo.transform.SetParent(_facility.transform, false);

                // Real KSC scenery (terrain, stock buildings) sits on the
                // "Local Scenery" layer, not "Default"; our diagnostic probe's
                // own raycast (all layers) was hitting our collider fine on every
                // click, yet SpaceCenterBuilding's OnClicked()/OnContextMenuSpawn()
                // never fired, which points at its internal picking being
                // layer-filtered to whatever real buildings use. Match it.
                int sceneryLayer = LayerMask.NameToLayer("Local Scenery");
                if (sceneryLayer >= 0)
                {
                    buildingGo.layer = sceneryLayer;
                    LogDebug($"Set '{buildingGo.name}' layer to 'Local Scenery' ({sceneryLayer}).");
                }
                else
                {
                    LogDebug("WARNING: layer 'Local Scenery' not found by LayerMask.NameToLayer, leaving buildingGo on its default layer.");
                }

                _building = buildingGo.AddComponent<ExoObservatoryBuilding>();
                // Must match _facility.name; SpaceCenterBuilding looks up its
                // facility by this string, not just by walking up the parent chain
                // ("Cannot find a facility of name 'Observatory'" was logged when
                // these two didn't match).
                _building.facilityName = _facility.name;
                _building.buildingInfoName = "ExoInstruments Observatory";
                _building.buildingDescription = "Select an instrument and start observing exoplanet hosts.";
                LogDebug($"ExoObservatoryBuilding component added to '{buildingGo.name}' (layer={LayerMask.LayerToName(buildingGo.layer)}), facilityName='{_building.facilityName}'.");

                // buildingRenderers is a plain public field with no auto-discovery;
                // stock buildings have it wired up by hand in the Unity Editor,
                // pointing at their own mesh renderers. Ours has to be populated in
                // code: without it SetupColliders()/hover-highlight have nothing to
                // work with, which is why the building was visible but inert (no
                // glow, no click, no tooltip). The actual mesh lives on the spawned
                // facilityInstance, a sibling of buildingGo, not a child of it.
                var facilityInstance = _facility.CurrentLevel?.facilityInstance;
                if (facilityInstance != null)
                {
                    _building.buildingRenderers = facilityInstance.GetComponentsInChildren<MeshRenderer>(true);
                    LogDebug($"facilityInstance='{facilityInstance.name}' (active={facilityInstance.activeInHierarchy}): found {_building.buildingRenderers.Length} MeshRenderer(s).");

                    // Belt-and-suspenders: also give buildingGo itself a collider
                    // sized to the mesh's world bounds, in case SetupColliders()
                    // needs one already present on this object rather than adding
                    // its own. OnMouseDown/Enter/Exit only fire on the GameObject
                    // that owns the hit collider, so this has to sit on buildingGo,
                    // not on the (differently-parented) mesh instance.
                    var bounds = new Bounds(facilityInstance.transform.position, Vector3.zero);
                    bool any = false;
                    foreach (var r in _building.buildingRenderers)
                    {
                        if (any) bounds.Encapsulate(r.bounds);
                        else { bounds = r.bounds; any = true; }
                    }
                    if (any)
                    {
                        var box = buildingGo.AddComponent<BoxCollider>();
                        box.center = buildingGo.transform.InverseTransformPoint(bounds.center);
                        var lossyScale = buildingGo.transform.lossyScale;
                        box.size = new Vector3(
                            bounds.size.x / Mathf.Max(lossyScale.x, 0.0001f),
                            bounds.size.y / Mathf.Max(lossyScale.y, 0.0001f),
                            bounds.size.z / Mathf.Max(lossyScale.z, 0.0001f));
                        LogDebug($"Manual BoxCollider added on '{buildingGo.name}': worldBounds center={bounds.center}, size={bounds.size}; local center={box.center}, size={box.size}.");
                    }
                    else
                    {
                        LogDebug("WARNING: no renderer bounds found, manual BoxCollider NOT added.");
                    }
                }
                else
                {
                    Debug.LogWarning("[ExoInstruments] Observatory facilityInstance not found; building will have no collider/renderers wired up.");
                    LogDebug("ERROR: _facility.CurrentLevel.facilityInstance is NULL; no renderers/collider can be wired up.");
                }

                var existing = Resources.FindObjectsOfTypeAll<SpaceCenterBuilding>()
                    .FirstOrDefault(b => b != _building && b.tooltipPrefab != null);
                if (existing != null)
                {
                    _building.tooltipPrefab = Instantiate(existing.tooltipPrefab);
                    _building.TooltipPrefabType = Instantiate(existing.TooltipPrefabType);
                    LogDebug($"Tooltip prefab borrowed from existing SpaceCenterBuilding '{existing.name}'.");
                }
                else
                {
                    Debug.LogWarning("[ExoInstruments] No existing SpaceCenterBuilding tooltip prefab found to borrow; observatory will have no hover tooltip.");
                    LogDebug("WARNING: no existing SpaceCenterBuilding with a non-null tooltipPrefab was found in the scene.");
                }

                RegisterBuildingPicker();

                // Full collider inventory under the whole facility, regardless of
                // where it came from (ours, or SpaceCenterBuilding's own
                // SetupColliders()); this is the ground truth for whether
                // anything is actually raycastable at the observatory's location.
                var allColliders = _facility.GetComponentsInChildren<Collider>(true);
                LogDebug($"Collider inventory under '{_facility.name}': {allColliders.Length} found.");
                foreach (var c in allColliders)
                {
                    LogDebug($"  - {c.GetType().Name} on '{GetPath(c.transform)}' layer={LayerMask.LayerToName(c.gameObject.layer)} enabled={c.enabled} isTrigger={c.isTrigger} bounds.center={c.bounds.center} bounds.size={c.bounds.size}");
                }

                // Drives glow/click/right-click; see ExoObservatoryInputRelay for why
                // this doesn't just rely on SpaceCenterBuilding's own click routing.
                var inputRelay = buildingGo.AddComponent<ExoObservatoryInputRelay>();
                inputRelay.Init(_building);
                LogDebug("ExoObservatoryInputRelay attached.");

                RegisterTuningConsoleCommands();
                SetupTelescopeTracking();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{DebugPrefix} EXCEPTION in HandleLevelWasLoaded(): {ex}");
                throw;
            }
        }

        // Names baked into ExoObservatoryLVL1.mu's own hierarchy (Parts/ExoObservatoryLVL1.mu):
        // dome/Telescope both spin in azimuth around CenterVerticalAxis together;
        // altitude_sensitive (nested inside Telescope) additionally tips in altitude
        // around CenterHorizontalAxis. lever/concrete_base_telescope/base/doorleft/
        // doorright aren't touched here; they either move with the whole facility
        // or (the doors) get their own open/close animation later.
        private const string DomeName = "dome";
        private const string TelescopeName = "Telescope";
        private const string AltitudeSensitiveName = "altitude_sensitive";
        private const string CenterVerticalAxisName = "CenterVerticalAxis";
        private const string CenterHorizontalAxisName = "CenterHorizontalAxis";
        private const string LocalAnimationName = "LocalAnimation";
        private const string DomeOpenClipName = "domeOpen";

        // Finds the dome/telescope/pivot nodes inside the spawned model and attaches
        // ExoObservatoryTelescopeTracker, which continuously points them at
        // ExoObservatoryTelescopeTracker.TrackedBody (set from ExoInstrumentsGUI whenever the RC20 photography
        // target changes). Degrades gracefully: azimuth tracking needs dome+Telescope+CenterVerticalAxis;
        // altitude tracking additionally needs altitude_sensitive+CenterHorizontalAxis; missing pieces just get
        // skipped (logged), not a hard failure.
        private void SetupTelescopeTracking()
        {
            var facilityInstance = _facility.CurrentLevel?.facilityInstance;
            if (facilityInstance == null)
            {
                LogDebug("WARNING: SetupTelescopeTracking: facilityInstance is null.");
                return;
            }

            Transform root = facilityInstance.transform;
            Transform dome = FindChildRecursive(root, DomeName);
            Transform telescope = FindChildRecursive(root, TelescopeName);
            Transform altitudeSensitive = FindChildRecursive(root, AltitudeSensitiveName);
            Transform centerVertical = FindChildRecursive(root, CenterVerticalAxisName);
            Transform centerHorizontal = FindChildRecursive(root, CenterHorizontalAxisName);
            Transform localAnimation = FindChildRecursive(root, LocalAnimationName);

            LogDebug($"SetupTelescopeTracking: dome={(dome != null ? "found" : "MISSING")}, Telescope={(telescope != null ? "found" : "MISSING")}, " +
                     $"altitude_sensitive={(altitudeSensitive != null ? "found" : "MISSING")}, CenterVerticalAxis={(centerVertical != null ? "found" : "MISSING")}, " +
                     $"CenterHorizontalAxis={(centerHorizontal != null ? "found" : "MISSING")}, LocalAnimation={(localAnimation != null ? "found" : "MISSING")}.");

            // Don't require the Animation component to sit exactly on LocalAnimation;
            // search the whole model for any Animation component carrying the
            // "domeOpen" clip, so a differently-placed component (e.g. directly on
            // doorright, or on the model root) still gets found instead of silently
            // doing nothing.
            Animation domeAnimation = null;
            var allAnimations = facilityInstance.GetComponentsInChildren<Animation>(true);
            LogDebug($"Animation components found anywhere in the model: {allAnimations.Length}.");
            foreach (var anim in allAnimations)
            {
                var clipNames = new System.Collections.Generic.List<string>();
                foreach (AnimationState state in anim) clipNames.Add(state.name);
                LogDebug($"  - Animation on '{GetPath(anim.transform)}': clips=[{string.Join(", ", clipNames)}].");
                if (domeAnimation == null && anim[DomeOpenClipName] != null) domeAnimation = anim;
            }
            if (domeAnimation == null)
            {
                LogDebug($"WARNING: no Animation component anywhere in the model has a clip named '{DomeOpenClipName}'; door animation won't play. " +
                         "Check in Unity that the Animation component's Animations list actually includes this clip, and that the model was re-exported after adding it.");
            }
            else
            {
                LogDebug($"Using Animation component on '{GetPath(domeAnimation.transform)}' for '{DomeOpenClipName}'.");
            }

            if (dome == null || telescope == null || centerVertical == null)
            {
                LogDebug($"WARNING: SetupTelescopeTracking: missing dome/Telescope/CenterVerticalAxis, no tracking set up. Children present: [{string.Join(", ", root.GetComponentsInChildren<Transform>(true).Select(t => t.name))}]");
                return;
            }

            var tracker = facilityInstance.AddComponent<ExoObservatoryTelescopeTracker>();
            tracker.Init(dome, telescope, altitudeSensitive, centerVertical, localAnimation, domeAnimation, DomeOpenClipName);
        }

        private static Transform FindChildRecursive(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform child in root)
            {
                var found = FindChildRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // The stock "[BuildingPicker]: Facility Name Mismatch" error means BuildingPicker.faciltyInfos has no
        // entry for our facility name; clone the ResearchAndDevelopment entry's sprite set rather than
        // requiring our own 4-quadrant icon art, and append it.
        private void RegisterBuildingPicker()
        {
            var picker = FindObjectOfType<KSP.UI.Screens.SpaceCenter.BuildingPicker>();
            if (picker == null)
            {
                LogDebug("WARNING: RegisterBuildingPicker: no BuildingPicker found in scene.");
                return;
            }
            LogDebug($"BuildingPicker.faciltyInfos ({picker.faciltyInfos?.Length ?? 0} entries): [{string.Join(", ", (picker.faciltyInfos ?? new KSP.UI.Screens.SpaceCenter.BuildingPicker.FacilityUIInfo[0]).Select(f => f?.name ?? "<null>"))}]");

            if (picker.faciltyInfos != null && picker.faciltyInfos.Any(f => f != null && f.name == FacilityName))
            {
                LogDebug($"BuildingPicker already has an entry for '{FacilityName}'.");
                return;
            }

            // Exact name doesn't matter; any existing entry's sprite set is a
            // valid placeholder icon, we just need SOME non-null ButtonSprites.
            var template = picker.faciltyInfos?.FirstOrDefault(f => f != null && f.spriteSet != null);
            if (template == null)
            {
                LogDebug("WARNING: RegisterBuildingPicker: no existing entry with a non-null spriteSet to clone.");
                return;
            }

            var newInfo = new KSP.UI.Screens.SpaceCenter.BuildingPicker.FacilityUIInfo
            {
                name = FacilityName,
                spriteSet = template.spriteSet,
                scBuilding = _building,
            };

            var newArray = picker.faciltyInfos.Concat(new[] { newInfo }).ToArray();
            picker.faciltyInfos = newArray;
            LogDebug($"BuildingPicker: added entry for '{FacilityName}' ({newArray.Length} total), cloned sprite set from '{template.name}'.");

            bool constructed = picker.ConstructBuildingList();
            LogDebug($"BuildingPicker.ConstructBuildingList() returned {constructed}.");
        }

        // Live scale/rotation tuning via the debug console (Alt+F12 -> Console, or backtick), so
        // scale/orientation can be dialed in with a rebuild+ restart-free feedback loop instead of re-exporting
        // the .mu each time. Neither persists; re-run after each KSP restart until the numbers are baked back
        // into the .mu's own transform.
        private void RegisterTuningConsoleCommands()
        {
            DebugScreenConsole.AddConsoleCommand(
                "exoobs_scale",
                arg =>
                {
                    var inst = _facility?.CurrentLevel?.facilityInstance;
                    if (inst == null) { LogDebug("exoobs_scale: no facilityInstance to scale."); return; }
                    if (float.TryParse(arg, out float s))
                    {
                        inst.transform.localScale = Vector3.one * s;
                        LogDebug($"exoobs_scale: set localScale to {s} (was asking for uniform scale).");
                    }
                    else
                    {
                        LogDebug($"exoobs_scale: current localScale={inst.transform.localScale}. Usage: exoobs_scale <factor>");
                    }
                },
                "ExoInstruments: exoobs_scale <factor>: live-rescale the observatory model.");

            DebugScreenConsole.AddConsoleCommand(
                "exoobs_rotate",
                arg =>
                {
                    var inst = _facility?.CurrentLevel?.facilityInstance;
                    if (inst == null || _facility == null) { LogDebug("exoobs_rotate: no facilityInstance to rotate."); return; }
                    if (float.TryParse(arg, out float y))
                    {
                        inst.transform.rotation = _facility.transform.rotation * Quaternion.Euler(0f, y, 0f);
                        LogDebug($"exoobs_rotate: set Y rotation offset to {y} degrees (relative to the facility's own rotation).");
                    }
                    else
                    {
                        LogDebug($"exoobs_rotate: current rotation.eulerAngles={inst.transform.rotation.eulerAngles}. Usage: exoobs_rotate <degreesY>");
                    }
                },
                "ExoInstruments: exoobs_rotate <degreesY>: live-rotate the observatory model around Y.");

            LogDebug("Registered tuning console commands: exoobs_scale <factor>, exoobs_rotate <degreesY>.");
        }

        private static string GetPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null)
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return sb.ToString();
        }

        // Same lookup ResearchBodies uses: scan every PQSCity in the scene for one that has a "SpaceCenter"
        // child; only the KSC's does.
        private static Transform FindKscSpaceCenterTransform()
        {
            var pqsCities = Resources.FindObjectsOfTypeAll<PQSCity>();
            foreach (var city in pqsCities)
            {
                if (city == null) continue;
                Transform spaceCenter = city.gameObject.transform.Find("SpaceCenter");
                if (spaceCenter != null) return spaceCenter;
            }
            return null;
        }
    }

    /// <summary>
    /// The observatory's clickable KSC building. Inheriting SpaceCenterBuilding
    /// (the same base class every stock facility building uses) is what gives us
    /// the real hover glow (HighLightBuilding), the info tooltip, and click
    /// routing for free; see ExoObservatoryFacility for how this gets wired to
    /// a real Upgradeables.UpgradeableFacility parent.
    /// </summary>
    public class ExoObservatoryBuilding : SpaceCenterBuilding
    {
        /// <summary>
        /// Static because this building outlives any single SpaceCentre-scene addon instance (it's
        /// DontDestroyOnLoad); ExoInstrumentsGUI subscribes/unsubscribes each scene load.
        /// </summary>
        public static event Action Clicked;

        public override bool IsOpen()
        {
            return HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.SCENARIO_NON_RESUMABLE;
        }

        protected override void OnClicked()
        {
            Debug.Log("[Exoplanets] ExoObservatoryBuilding.OnClicked() fired (base SpaceCenterBuilding click routing reached us).");
            HighLightBuilding(false);
            Clicked?.Invoke();
        }

        protected override KSP.UI.AnchoredDialog OnContextMenuSpawn()
        {
            Debug.Log("[Exoplanets] ExoObservatoryBuilding.OnContextMenuSpawn() fired (right-click routing reached us).");
            var dialog = base.OnContextMenuSpawn();
            Debug.Log($"[Exoplanets] OnContextMenuSpawn() returned {(dialog != null ? "a dialog" : "NULL")}.");
            return dialog;
        }

        // Public wrappers so ExoObservatoryDebugProbe (a different component,
        // on the same GameObject) can invoke these protected members directly.
        // Confirmed via [Exoplanets] logs: Unity's native OnMouseDown/Up/Enter/
        // Exit fire reliably on this object (many times, every session), but
        // SpaceCenterBuilding's own private OnMouseDown/OnMouseUp, which
        // *also* receive those same messages, never end up calling OnClicked()
        // or OnContextMenuSpawn(). Whatever internal gate/tap-gesture check is
        // stopping that isn't visible from outside the compiled assembly, so
        // rather than keep guessing at its private logic, the probe drives
        // these real stock methods itself from the native messages we know work.
        // OnLeftClick()/OnRightClick() are SpaceCenterBuilding's own public
        // entry points, calling these instead of the raw OnClicked()/
        // OnContextMenuSpawn() overrides directly lets the base class set up
        // whatever supporting state (dismiss-safety, position tracking) it
        // normally does before invoking them itself. Calling OnContextMenuSpawn()
        // directly produced a dialog that closed itself almost immediately.
        public void TriggerClick() => OnLeftClick();
        public void TriggerContextMenu() => OnRightClick();
    }

    /// <summary>
    /// Drives the observatory's click/hover behaviour from Unity's native mouse
    /// messages (OnMouseEnter/Exit/Down/Up/Over), which, confirmed by extensive
    /// testing, fire reliably here even though SpaceCenterBuilding's own private
    /// OnMouseDown/OnMouseUp (which receive the exact same messages) never end up
    /// calling OnClicked()/OnContextMenuSpawn() on their own. Rather than rely on
    /// that private logic, this drives the real stock methods (HighLightBuilding,
    /// TriggerClick/TriggerContextMenu) directly, still the authentic stock
    /// glow/dialog, just triggered from a path proven to work.
    /// </summary>
    public class ExoObservatoryInputRelay : MonoBehaviour
    {
        private const string Prefix = "[Exoplanets]";
        private ExoObservatoryBuilding _target;

        public void Init(ExoObservatoryBuilding target)
        {
            _target = target;
        }

        // Unity's OWN native mouse-picking, confirmed reliable (fires every
        // session, many times) even though SpaceCenterBuilding's own private
        // OnMouseDown/OnMouseUp (which receive the exact same messages) never
        // end up calling OnClicked()/OnContextMenuSpawn(). Rather than keep
        // guessing at that private logic, drive the real stock methods
        // (HighLightBuilding, TriggerClick/TriggerContextMenu) directly from
        // here, still the authentic stock glow/dialog, just triggered from a
        // path we've proven works instead of SpaceCenterBuilding's own.
        private void OnMouseEnter() => _target?.HighLightBuilding(true);

        private void OnMouseExit() => _target?.HighLightBuilding(false);

        private void OnMouseDown()
        {
            Debug.Log($"{Prefix} [native Unity] OnMouseDown() fired on '{name}'.");
        }

        private void OnMouseUp()
        {
            Debug.Log($"{Prefix} [native Unity] OnMouseUp() fired on '{name}'.");
            // Confirmed via [Exoplanets] logs: on this (old, KSP1-era) Unity
            // version OnMouseDown/OnMouseUp only ever fire for the LEFT button;
            // several right-clicks (seen via this probe's own manual
            // Input.GetMouseButtonDown(1) polling in Update()) produced zero
            // native OnMouseUp calls. Left click is handled here; right click
            // is handled below via OnMouseOver(), which fires every frame
            // regardless of button.
            if (Input.GetMouseButtonUp(0))
            {
                Debug.Log($"{Prefix} Driving TriggerClick() directly.");
                _target?.TriggerClick();
            }
        }

        private void OnMouseOver()
        {
            // Left click (GetMouseButtonUp(0) above, on release) opens a
            // persistent window fine. Right click triggered on the PRESS
            // (GetMouseButtonDown) closed itself the instant the button came
            // up, matching it to the same release-triggered pattern as left
            // click fixes that.
            if (Input.GetMouseButtonUp(1))
            {
                Debug.Log($"{Prefix} [native Unity] OnMouseOver() saw a right-click release on '{name}', driving TriggerContextMenu() directly.");
                _target?.TriggerContextMenu();
            }
        }
    }

    /// <summary>
    /// Sanity-check spin for the dome, independent of the rest of the model.
    /// Placeholder for real sky-tracking (see SkyCoordinates.EquatorialToHorizontal);
    /// once that's wired up this constant-speed spin gets replaced by a
    /// Quaternion.LookRotation towards the current target's computed direction.
    /// </summary>
    /// <summary>
    /// Points the telescope model at ExoObservatoryTelescopeTracker.TrackedBody (set
    /// from ExoInstrumentsGUI whenever the RC20 photography target changes; null when
    /// idle; the rig just sits at its authored rest pose then).
    ///
    /// Two-axis gimbal, matching the .mu's own hierarchy (Parts/ExoObservatoryLVL1.mu):
    /// dome and Telescope both spin in azimuth together around CenterVerticalAxis;
    /// altitude_sensitive (nested inside Telescope, so it automatically inherits the
    /// azimuth spin) additionally tips in altitude around CenterHorizontalAxis.
    /// CenterHorizontalAxis is expected to be parented under Telescope/dome (so it
    /// rides along with azimuth) but NOT under altitude_sensitive itself (which would
    /// make the altitude pivot move with the very thing it's supposed to pivot).
    ///
    /// Every update resets each rigged part to its captured rest pose and reapplies
    /// the FULL current azimuth/altitude via Transform.RotateAround (rather than
    /// incrementally rotating frame to frame), so there's no drift and the rig always
    /// reflects the real current sky position, not an accumulated approximation of it.
    /// </summary>
    public class ExoObservatoryTelescopeTracker : MonoBehaviour
    {
        /// <summary>
        /// Set from ExoInstrumentsGUI wherever the photography target changes. Null = idle/parked, or a fixed
        /// sky position (see TrackedAltDeg).
        /// </summary>
        public static CelestialBody TrackedBody;

        /// <summary>Where a fixed equatorial target currently sits, when TrackedBody is null. Refreshed by the GUI; null = nothing to track.</summary>
        public static double? TrackedAltDeg;
        public static double? TrackedAzDeg;

        // No azimuth/altitude calibration constants: aiming is measured against the
        // tube's own current orientation each frame (see Update), so the rig's rest
        // pose and axis signs cancel out on their own. Earlier versions carried an
        // AzimuthOffsetDeg and an AltitudeOffsetDeg here, plus an axis sign flag;
        // each was fitted to one observed symptom and broke another.

        // The tube's optical (pointing) axis expressed in altitude_sensitive's own local frame. Model-specific
        // to ExoObservatoryLVL1.mu; re-measure if the tube is re-exported. Identified from logged altitudes of
        // all three local axes while the tilt was driven by a known angle: forward stayed pinned at altitude 0
        // (that's the trunnion), right tracked the applied angle 1:1, and up tracked 90-minus-that. Azimuth
        // cannot tell right from up here; every axis in the tilt plane shares the same azimuth, which is why an
        // earlier reading of "-right, because its azimuth matches" was wrong. Altitude discriminates: aiming
        // -right at a target at the zenith left the visible barrel on the horizon (90 - 90), so the barrel lies
        // in the up direction. It is not exactly up, though: logged at rest, up sits dead on the local vertical
        // (dot 1.000), which would put the barrel at the zenith when parked, but the barrel is modelled at 54
        // deg above the horizon (measured in Blender), i.e. mounted TubeOpticalTiltFromUpDeg away from up
        // inside the tilt plane, toward the side the scope looks out of. Aiming up itself therefore left a
        // constant pointing error of about that size across the whole track. Flip the sign of
        // TubeOpticalTiltFromUpDeg if the tube ends up off by twice this in the other direction.
        private const float TubeOpticalTiltFromUpDeg = 36f;
        private static readonly Vector3 TubeOpticalAxisLocal =
            Quaternion.AngleAxis(TubeOpticalTiltFromUpDeg, Vector3.forward) * Vector3.up;

        private Transform _dome, _telescope, _altitudeSensitive, _centerVertical, _localAnimation;
        private Vector3 _domeRestPos, _telescopeRestPos, _altRestLocalPos, _localAnimationRestPos;
        private Quaternion _domeRestRot, _telescopeRestRot, _altRestLocalRot, _localAnimationRestRot;
        private Vector3 _verticalAxisWorld;

        // DIAGNOSTIC: set true to bypass real altitude tracking and instead spin the
        // tube through a continuous 0-360 deg loop around the dome-derived axis, so
        // the axis placement/pivot can be visually checked in-game. Set back to
        // false once the real axis is confirmed good.
        private const bool AltitudeDiagnosticSweep = false;
        private const float DiagnosticSweepDegPerSec = 20f;
        private float _diagnosticSweepDeg;

        // The trunnion (altitude hinge) axis in world space: perpendicular to both the local vertical and the
        // tube's current optical axis, which is the definition of an alt-az mount's altitude axis. Computed
        // rather than picked from the dome's own local axes: the earlier "whichever local axis is most
        // perpendicular to vertical" rule is ambiguous; two of the dome's three axes satisfy it equally, so the
        // tie-break was arbitrary. It happened to pick the pointing axis itself, leaving the optical axis
        // exactly parallel to the supposed hinge (logged dot = -1.000), which made the altitude projection
        // degenerate and froze the tube at rest. The cross product has no such ambiguity, and rides along with
        // the azimuth swing for free since the optical axis does.
        private Vector3 CurrentAltitudeAxisWorld()
        {
            Vector3 axis = Vector3.Cross(_verticalAxisWorld, OpticalAxisWorld());
            // Degenerate only if the tube points straight up/down, where azimuth and
            // trunnion coincide anyway; fall back to any horizontal dome axis.
            return axis.sqrMagnitude < 1e-8f ? _dome.up : axis.normalized;
        }

        private Animation _domeAnimation;
        private string _domeOpenClipName;
        private bool? _doorsOpen; // null = not yet set (forces the first Update to actually trigger Play)

        public void Init(Transform dome, Transform telescope, Transform altitudeSensitive, Transform centerVertical, Transform localAnimation, Animation domeAnimation, string domeOpenClipName)
        {
            _dome = dome;
            _telescope = telescope;
            _altitudeSensitive = altitudeSensitive;
            _centerVertical = centerVertical;
            _localAnimation = localAnimation;
            _domeAnimation = domeAnimation;
            _domeOpenClipName = domeOpenClipName;

            _domeRestPos = dome.position;
            _domeRestRot = dome.rotation;
            _telescopeRestPos = telescope.position;
            _telescopeRestRot = telescope.rotation;
            if (altitudeSensitive != null)
            {
                _altRestLocalPos = altitudeSensitive.localPosition;
                _altRestLocalRot = altitudeSensitive.localRotation;
            }
            // LocalAnimation (the doors) isn't a child of dome in the current .mu, so it
            // doesn't automatically inherit dome's azimuth spin; rotate it explicitly,
            // same pivot/axis, right alongside dome and Telescope every Update.
            if (localAnimation != null)
            {
                _localAnimationRestPos = localAnimation.position;
                _localAnimationRestRot = localAnimation.rotation;
            }

            CelestialBody home = FlightGlobals.GetHomeBody();
            Vector3 worldVertical = Vector3.up;
            if (home != null)
            {
                Vector3d verticalD = (Vector3d)centerVertical.position - home.position;
                worldVertical = ((Vector3)verticalD).normalized;
            }
            _verticalAxisWorld = ChooseClosestAxis(centerVertical, worldVertical, out _);
        }

        // Picks whichever of t's own local axes is closest to targetWorldDir, sign-matched so positive
        // RotateAround angles go the expected way.
        private static Vector3 ChooseClosestAxis(Transform t, Vector3 targetWorldDir, out string axisName)
        {
            float dotUp = Vector3.Dot(t.up, targetWorldDir);
            float dotRight = Vector3.Dot(t.right, targetWorldDir);
            float dotForward = Vector3.Dot(t.forward, targetWorldDir);
            float absUp = Mathf.Abs(dotUp), absRight = Mathf.Abs(dotRight), absForward = Mathf.Abs(dotForward);
            if (absRight >= absUp && absRight >= absForward) { axisName = "right"; return t.right * Mathf.Sign(dotRight == 0f ? 1f : dotRight); }
            if (absForward >= absUp && absForward >= absRight) { axisName = "forward"; return t.forward * Mathf.Sign(dotForward == 0f ? 1f : dotForward); }
            axisName = "up"; return t.up * Mathf.Sign(dotUp == 0f ? 1f : dotUp);
        }

        private void Update()
        {
            double altDeg = 0.0, azDeg = 0.0;
            bool haveTarget;
            if (TrackedBody != null)
            {
                haveTarget = TryComputeAltAz(TrackedBody, out altDeg, out azDeg) && altDeg > 0.0;
            }
            else
            {
                haveTarget = TrackedAltDeg.HasValue && TrackedAzDeg.HasValue && TrackedAltDeg.Value > 0.0;
                if (haveTarget) { altDeg = TrackedAltDeg.Value; azDeg = TrackedAzDeg.Value; }
            }

            // Always rebuild from the authored rest pose, then aim from there.
            _dome.position = _domeRestPos; _dome.rotation = _domeRestRot;
            _telescope.position = _telescopeRestPos; _telescope.rotation = _telescopeRestRot;
            if (_localAnimation != null)
            {
                _localAnimation.position = _localAnimationRestPos;
                _localAnimation.rotation = _localAnimationRestRot;
            }
            if (_altitudeSensitive != null)
            {
                _altitudeSensitive.localPosition = _altRestLocalPos;
                _altitudeSensitive.localRotation = _altRestLocalRot;
            }

            float azimuth = 0f, altitude = 0f;

            if (AltitudeDiagnosticSweep && _altitudeSensitive != null)
            {
                _diagnosticSweepDeg = (_diagnosticSweepDeg + DiagnosticSweepDegPerSec * Time.deltaTime) % 360f;
                _altitudeSensitive.RotateAround(_centerVertical.position, CurrentAltitudeAxisWorld(), _diagnosticSweepDeg);
            }
            else if (haveTarget && TryGetTargetDirection(TrackedBody, out Vector3 targetDir))
            {
                // Rather than converting the target to an az/alt pair and hoping the
                // rig's rest pose, axis signs and zero points all line up (every one
                // of those was a separate calibration constant, and each fix for one
                // broke another), measure the angle actually needed to bring the
                // tube's optical axis onto the target and rotate by exactly that.
                // Self-correcting: no rest-pose offset, and immune to the chosen
                // axis's sign (flipping the axis flips both the measured angle and
                // the rotation direction, which cancel).
                Vector3 pivot = _centerVertical.position;

                // Azimuth: swing dome/telescope/doors about the vertical until the
                // optical axis, seen from directly above, bears on the target.
                azimuth = SignedAngleAbout(OpticalAxisWorld(), targetDir, _verticalAxisWorld);
                _dome.RotateAround(pivot, _verticalAxisWorld, azimuth);
                _telescope.RotateAround(pivot, _verticalAxisWorld, azimuth);
                if (_localAnimation != null) _localAnimation.RotateAround(pivot, _verticalAxisWorld, azimuth);

                // Altitude: tip the tube about the trunnion, which the azimuth swing
                // above has already carried into its correct orientation.
                if (_altitudeSensitive != null)
                {
                    Vector3 trunnion = CurrentAltitudeAxisWorld();
                    altitude = SignedAngleAbout(OpticalAxisWorld(), targetDir, trunnion);
                    _altitudeSensitive.RotateAround(pivot, trunnion, altitude);
                }
            }

            SetDoorsOpen(haveTarget);
        }

        // The tube's optical (pointing) axis in world space right now.
        private Vector3 OpticalAxisWorld()
        {
            Transform t = _altitudeSensitive != null ? _altitudeSensitive : _telescope;
            return t.TransformDirection(TubeOpticalAxisLocal);
        }

        // The rotation about <paramref name="axis"/> that best takes <paramref name="from"/> onto <paramref
        // name="to"/>, both projected onto the plane the axis is normal to, since only that component is
        // reachable by rotating about it. 0 when either projection degenerates (direction parallel to the
        // axis).
        private static float SignedAngleAbout(Vector3 from, Vector3 to, Vector3 axis)
        {
            Vector3 f = Vector3.ProjectOnPlane(from, axis);
            Vector3 t = Vector3.ProjectOnPlane(to, axis);
            if (f.sqrMagnitude < 1e-8f || t.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.SignedAngle(f.normalized, t.normalized, axis);
        }

        private static bool TryGetTargetDirection(CelestialBody body, out Vector3 dir)
        {
            dir = Vector3.forward;
            if (body == null || !TryGetLocalFrame(out Vector3d obsPos, out _, out _, out _)) return false;
            Vector3d d = body.position - obsPos;
            if (d.sqrMagnitude < 1e-6) return false;
            dir = ((Vector3)d).normalized;
            return true;
        }

        private static bool TryGetLocalFrame(out Vector3d obsPos, out Vector3d up, out Vector3d north, out Vector3d east)
        {
            obsPos = default; up = default; north = default; east = default;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return false;
            obsPos = ObservatorySite.WorldPosition(home);
            up = (obsPos - home.position).normalized;
            Vector3d spinAxis = ((Vector3d)home.transform.up).normalized;
            east = Vector3d.Cross(spinAxis, up).normalized;
            north = Vector3d.Cross(up, east).normalized;
            return true;
        }

        private static void DirectionToAltAz(Vector3d dir, Vector3d up, Vector3d north, Vector3d east, out double altDeg, out double azDeg)
        {
            dir = dir.normalized;
            altDeg = 90.0 - Vector3d.Angle(up, dir);
            double e = Vector3d.Dot(dir, east);
            double n = Vector3d.Dot(dir, north);
            azDeg = (Math.Atan2(e, n) * 180.0 / Math.PI + 360.0) % 360.0;
        }

        // Plays the "domeOpen" legacy Animation clip forward to open, or the same clip in reverse (negative
        // speed, starting from its end) to close, same trick real KSP facility door animations use, no separate
        // "close" clip needed. Only actually calls Play() on a state change, not every frame.
        private void SetDoorsOpen(bool open)
        {
            if (_domeAnimation == null || string.IsNullOrEmpty(_domeOpenClipName)) return;
            if (_doorsOpen.HasValue && _doorsOpen.Value == open) return;
            _doorsOpen = open;

            var state = _domeAnimation[_domeOpenClipName];
            if (state == null)
            {
                Debug.LogWarning($"[Exoplanets] TelescopeTracker: Animation clip '{_domeOpenClipName}' not found, can't {(open ? "open" : "close")} the doors.");
                return;
            }

            state.speed = open ? 1f : -1f;
            state.time = open ? 0f : state.length;
            _domeAnimation.Play(_domeOpenClipName);
            Debug.Log($"[Exoplanets] TelescopeTracker: playing '{_domeOpenClipName}' {(open ? "forward (opening)" : "in reverse (closing)")}.");
        }

        // Same alt/az convention as ExoInstrumentsGUI.TryComputeBodyAltAz: azimuth from North, clockwise
        // through East; altitude negative below the horizon.
        private static bool TryComputeAltAz(CelestialBody body, out double altDeg, out double azDeg)
        {
            altDeg = 0.0; azDeg = 0.0;
            if (body == null || !TryGetLocalFrame(out Vector3d obsPos, out Vector3d up, out Vector3d north, out Vector3d east)) return false;

            Vector3d toBody = (body.position - obsPos).normalized;
            DirectionToAltAz(toBody, up, north, east, out altDeg, out azDeg);
            return true;
        }
    }
}
