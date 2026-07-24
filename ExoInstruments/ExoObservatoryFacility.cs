using System;
using System.Linq;
using System.Text;
using UnityEngine;
using KSP.UI.Screens.DebugToolbar;

namespace ExoInstruments
{
    /// <summary>
    /// Builds a real, upgradeable KSC facility for the observatory, following the
    /// same pipeline ResearchBodies uses for its own Observatory:
    ///
    /// 1. At PSystemSpawn (once per game session, before any scene renders), clone
    ///    an existing real Upgradeables.UpgradeableFacility (Research &amp; Development's)
    ///    rather than raw-AddComponent one -- the clone already has a non-null
    ///    UpgradeLevels array by the time its own Awake() runs, so we sidestep
    ///    whatever null-array assumptions the stock Awake() makes and only replace
    ///    UpgradeLevels afterwards, once the clone is already alive. This makes the
    ///    level (and its persistence across saves, via UpgradeableObject's own
    ///    id/level bookkeeping) a genuine stock facility, not a hand-rolled one.
    /// 2. At the first SPACECENTER scene load (once real stock buildings exist to
    ///    borrow a tooltip prefab from), attach a SpaceCenterBuilding-derived
    ///    component. That base class is what stock KSC buildings (VAB, R&amp;D, ...)
    ///    use for hover highlight (HighLightBuilding), the info tooltip, and click
    ///    routing -- all for free, not re-implemented here.
    ///
    /// Skipped relative to ResearchBodies' pipeline: registering a
    /// PSystemSetup.SpaceCenterFacility entry (their building-picker/camera-fly-to
    /// integration -- not required for glow/tooltip/click/upgrade, which come from
    /// UpgradeableFacility + SpaceCenterBuilding directly) and DestructibleBuilding
    /// wreck/collapse wiring (keyed to child-object names baked into their .mu
    /// export, which ours doesn't share).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.PSystemSpawn, true)]
    public class ExoObservatoryFacility : MonoBehaviour
    {
        // Shared by _facility.name, the SpaceCenterBuilding's facilityName, and
        // the PSystemSetup.SpaceCenterFacility registry entry below -- all three
        // must match, since SpaceCenterBuilding resolves its facility by looking
        // this name up in PSystemSetup, not just by walking up its parent chain.
        private const string FacilityName = "ExoObservatory";
        private const string FacilityId = "SpaceCenter/ExoObservatory";
        private const string Level1ModelUrl = "ExoInstruments/Parts/ExoObservatoryLVL1";
        private const float Level1Cost = 30000f;
        // TODO: reintroduce ExoObservatoryLVL2 as a second UpgradeLevel once
        // level 1 is confirmed visible in-game.

        // Same local offset ResearchBodies uses for its own KSC Observatory,
        // relative to the PQSCity's "SpaceCenter" child transform.
        private static readonly Vector3 LocalOffset = new Vector3(-246.6104f, 24.34455f, -216.4676f);

        // Detailed trace logging requested for diagnosing the click/highlight
        // issue -- deliberately a different prefix from the mod's normal
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

        /// <summary>
        /// Injects a PSystemSetup.SpaceCenterFacility entry named FacilityName --
        /// SpaceCenterBuilding.SetupFacility() looks a building's facility up by
        /// this name via PSystemSetup.GetSpaceCenterFacility() and aborts its
        /// whole setup coroutine (colliders, renderers, highlight) if it's
        /// missing, which is what silently kept the observatory invisible.
        /// </summary>
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

        /// <summary>Clones a real UpgradeableFacility and gives it our two levels. Runs once, before any scene renders.</summary>
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
                // SpaceCenter above) exists -- calling it any earlier logged
                // "Cannot find facility named 'KSC/SpaceCenter/ExoObservatory' on
                // pqs 'Kerbin'" and left facilityTransform/facilityPQS/hostBody null.
                _facilityEntry?.Setup(Resources.FindObjectsOfTypeAll<PQS>());
                if (_facilityEntry != null)
                {
                    LogDebug($"PSystemSetup.SpaceCenterFacility entry after Setup(): facilityTransform={(_facilityEntry.facilityTransform != null ? _facilityEntry.facilityTransform.name : "NULL")}, facilityPQS={(_facilityEntry.facilityPQS != null ? "set" : "NULL")}, hostBody={(_facilityEntry.hostBody != null ? _facilityEntry.hostBody.name : "NULL")}.");
                }

                // Instantiate() already ran Awake() synchronously on the clone, which
                // spawned R&D's own facilityPrefab for whatever level it was cloned at
                // -- that's the ghost "second R&D building" that showed up at our
                // position. Get rid of it before swapping in our own levels, then
                // force a respawn so our level 0 model actually appears (replacing
                // UpgradeLevels alone doesn't trigger one).
                _facility.CurrentLevel?.Despawn();

                // ResearchBodies keeps its level prefab templates activeSelf=true (so
                // Spawn()'s Instantiate() clone comes out already active -- Spawn()
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
                // fields, which only get populated by Setup(host) -- normally called
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
                    : "BuildFacility() done, but CurrentLevel.facilityInstance is NULL -- SetLevel()/Spawn() did not produce a visible instance.");
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
            prefab.transform.SetParent(templateHolder, true); // worldPositionStays -- keep the pose we just instantiated it at.
            prefab.SetActive(true);
            DontDestroyOnLoad(prefab);

            var levelText = ScriptableObject.CreateInstance<KSCUpgradeableLevelText>();
            levelText.facility = SpaceCenterFacility.TrackingStation; // stock enum can't be extended -- ResearchBodies uses the same filler value.
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
        // own transform -- unknown good value yet, needs tuning against the
        // observatory's actual footprint once it's visible in-game. ResearchBodies'
        // own equivalent (groundBaseOffset) is (15, -9, -10), tuned for their
        // building; that's just a starting-point guess here, not measured for ours.
        private static readonly Vector3 GroundBaseOffset = new Vector3(15f, -9f, -10f);

        /// <summary>
        /// Clones the ground patch mesh every stock KSC facility sits on (a flat,
        /// terrain-blending base) from Mission Control's own prefab, since it
        /// isn't a separate asset on disk anywhere -- it's baked into the
        /// compiled KSC scene. Same technique ResearchBodies uses for its own
        /// Observatory: find Mission Control's UpgradeableFacility in memory,
        /// grab the child tagged "KSC_Mission_Control_Grounds" off its level-0
        /// facilityPrefab, and clone that.
        /// </summary>
        private void AddGroundBase(GameObject targetPrefab, Transform attachPosition)
        {
            var missionControl = Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>()
                .FirstOrDefault(f => f.name == "MissionControl");
            if (missionControl == null || missionControl.UpgradeLevels == null || missionControl.UpgradeLevels.Length == 0)
            {
                LogDebug("WARNING: AddGroundBase: MissionControl facility/levels not found -- no ground base added.");
                return;
            }

            var mcPrefab = missionControl.UpgradeLevels[0].facilityPrefab;
            if (mcPrefab == null)
            {
                LogDebug("WARNING: AddGroundBase: MissionControl level-0 facilityPrefab is null -- no ground base added.");
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
                LogDebug("WARNING: AddGroundBase: no child tagged 'KSC_Mission_Control_Grounds' found under MissionControl's level-0 prefab -- no ground base added.");
                return;
            }

            var groundInstance = Instantiate(groundSource, attachPosition.position, attachPosition.rotation);
            groundInstance.name = "ExoObservatoryGroundBase";
            groundInstance.transform.SetParent(targetPrefab.transform, true);
            groundInstance.transform.localPosition = GroundBaseOffset;
            LogDebug($"AddGroundBase: cloned '{groundSource.name}' from MissionControl, parented under '{targetPrefab.name}' at localPosition={GroundBaseOffset}.");
        }

        /// <summary>Once real stock buildings exist in the scene (so we have a tooltip prefab to borrow), attach the clickable/hoverable component. Idempotent -- runs at most once.</summary>
        private void HandleLevelWasLoaded(GameScenes scene)
        {
            if (scene != GameScenes.SPACECENTER) return;
            if (_facility == null || _building != null) return;

            LogDebug("HandleLevelWasLoaded(SPACECENTER) starting -- attaching ExoObservatoryBuilding.");

            try
            {
                var buildingGo = new GameObject("ExoObservatoryBuilding");
                buildingGo.transform.SetParent(_facility.transform, false);

                // Real KSC scenery (terrain, stock buildings) sits on the
                // "Local Scenery" layer, not "Default" -- our diagnostic probe's
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
                    LogDebug("WARNING: layer 'Local Scenery' not found by LayerMask.NameToLayer -- leaving buildingGo on its default layer.");
                }

                _building = buildingGo.AddComponent<ExoObservatoryBuilding>();
                // Must match _facility.name -- SpaceCenterBuilding looks up its
                // facility by this string, not just by walking up the parent chain
                // ("Cannot find a facility of name 'Observatory'" was logged when
                // these two didn't match).
                _building.facilityName = _facility.name;
                _building.buildingInfoName = "ExoInstruments Observatory";
                _building.buildingDescription = "Select an instrument and start observing exoplanet hosts.";
                LogDebug($"ExoObservatoryBuilding component added to '{buildingGo.name}' (layer={LayerMask.LayerToName(buildingGo.layer)}), facilityName='{_building.facilityName}'.");

                // buildingRenderers is a plain public field with no auto-discovery --
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
                        LogDebug("WARNING: no renderer bounds found -- manual BoxCollider NOT added.");
                    }
                }
                else
                {
                    Debug.LogWarning("[ExoInstruments] Observatory facilityInstance not found; building will have no collider/renderers wired up.");
                    LogDebug("ERROR: _facility.CurrentLevel.facilityInstance is NULL -- no renderers/collider can be wired up.");
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
                // SetupColliders()) -- this is the ground truth for whether
                // anything is actually raycastable at the observatory's location.
                var allColliders = _facility.GetComponentsInChildren<Collider>(true);
                LogDebug($"Collider inventory under '{_facility.name}': {allColliders.Length} found.");
                foreach (var c in allColliders)
                {
                    LogDebug($"  - {c.GetType().Name} on '{GetPath(c.transform)}' layer={LayerMask.LayerToName(c.gameObject.layer)} enabled={c.enabled} isTrigger={c.isTrigger} bounds.center={c.bounds.center} bounds.size={c.bounds.size}");
                }

                // Independent raycast/hover/click probe -- doesn't rely on
                // SpaceCenterBuilding's own (private, unverifiable from outside)
                // hover/click machinery, so it tells us the ground truth: is
                // there anything raycastable here at all, and does Unity's mouse
                // picking actually find it.
                var probe = buildingGo.AddComponent<ExoObservatoryDebugProbe>();
                probe.Init(_facility.transform, _building);
                LogDebug("ExoObservatoryDebugProbe attached -- will log hover/click raycast results.");

                RegisterTuningConsoleCommands();
                SetupDomeRotation();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{DebugPrefix} EXCEPTION in HandleLevelWasLoaded(): {ex}");
                throw;
            }
        }

        // Degrees/second for the sanity-check spin -- purely a "does this even
        // move independently" test, not the real sky-tracking speed.
        private const string DomeChildName = "Corps2";
        private const float DomeTestSpinDegPerSec = 15f;

        /// <summary>
        /// Finds the dome's own child node (named "Corps2" in the current .mu --
        /// "Corps1" is the static base) inside the spawned model and attaches a
        /// slow constant spin, purely to confirm it moves independently of the
        /// rest of the telescope body before wiring up real sky-tracking (which
        /// needs a currently-observed target to point at, see SkyCoordinates.cs).
        /// </summary>
        private void SetupDomeRotation()
        {
            var facilityInstance = _facility.CurrentLevel?.facilityInstance;
            if (facilityInstance == null)
            {
                LogDebug("WARNING: SetupDomeRotation: facilityInstance is null, cannot find the dome.");
                return;
            }

            Transform dome = FindChildRecursive(facilityInstance.transform, DomeChildName);
            if (dome == null)
            {
                LogDebug($"WARNING: SetupDomeRotation: no child named '{DomeChildName}' found under '{facilityInstance.name}'. Children present: [{string.Join(", ", facilityInstance.GetComponentsInChildren<Transform>(true).Select(t => t.name))}]");
                return;
            }

            // Don't guess which of the dome's own local axes is "vertical" --
            // the Fusion/Blender/Unity export chain's Z-up-vs-Y-up handling
            // means it isn't necessarily local Y. Compare all three local axes
            // (in world space, via Transform.up/right/forward) against the
            // real vertical at this spot on Kerbin (radially outward from the
            // planet's center -- world Y alone isn't exactly it either, though
            // it's very close this near the equator) and spin around whichever
            // one lines up best.
            Vector3 worldUp = (dome.position - FlightGlobals.GetHomeBody().position).normalized;
            float dotUp = Vector3.Dot(dome.up, worldUp);
            float dotRight = Vector3.Dot(dome.right, worldUp);
            float dotForward = Vector3.Dot(dome.forward, worldUp);
            LogDebug($"Dome local-axis alignment with world-up: up·up={dotUp:F3}, right·up={dotRight:F3}, forward·up={dotForward:F3}.");

            Vector3 spinAxisLocal;
            string axisName;
            float absUp = Mathf.Abs(dotUp), absRight = Mathf.Abs(dotRight), absForward = Mathf.Abs(dotForward);
            if (absRight >= absUp && absRight >= absForward) { spinAxisLocal = Vector3.right; axisName = "right (local X)"; }
            else if (absForward >= absUp && absForward >= absRight) { spinAxisLocal = Vector3.forward; axisName = "forward (local Z)"; }
            else { spinAxisLocal = Vector3.up; axisName = "up (local Y)"; }

            var rotator = dome.gameObject.AddComponent<ExoObservatoryDomeRotator>();
            rotator.DegreesPerSecond = DomeTestSpinDegPerSec;
            rotator.LocalAxis = spinAxisLocal;
            LogDebug($"SetupDomeRotation: found '{GetPath(dome)}', spinning it at {DomeTestSpinDegPerSec} deg/s around its local {axisName} axis (auto-detected as closest to true vertical).");
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

        /// <summary>
        /// The stock "[BuildingPicker]: Facility Name Mismatch" error means
        /// BuildingPicker.faciltyInfos has no entry for our facility name --
        /// clone the ResearchAndDevelopment entry's sprite set rather than
        /// requiring our own 4-quadrant icon art, and append it.
        /// </summary>
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

            // Exact name doesn't matter -- any existing entry's sprite set is a
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

        /// <summary>
        /// Live scale/rotation tuning via the debug console (Alt+F12 -> Console,
        /// or backtick), so scale/orientation can be dialed in with a rebuild+
        /// restart-free feedback loop instead of re-exporting the .mu each time.
        /// Neither persists -- re-run after each KSP restart until the numbers
        /// are baked back into the .mu's own transform.
        /// </summary>
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
                "ExoInstruments: exoobs_scale <factor> -- live-rescale the observatory model.");

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
                "ExoInstruments: exoobs_rotate <degreesY> -- live-rotate the observatory model around Y.");

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

        /// <summary>Same lookup ResearchBodies uses: scan every PQSCity in the scene for one that has a "SpaceCenter" child -- only the KSC's does.</summary>
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
    /// routing for free -- see ExoObservatoryFacility for how this gets wired to
    /// a real Upgradeables.UpgradeableFacility parent.
    /// </summary>
    public class ExoObservatoryBuilding : SpaceCenterBuilding
    {
        /// <summary>Static because this building outlives any single SpaceCentre-scene addon instance (it's DontDestroyOnLoad); ExoInstrumentsGUI subscribes/unsubscribes each scene load.</summary>
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
        // SpaceCenterBuilding's own private OnMouseDown/OnMouseUp -- which
        // *also* receive those same messages -- never end up calling OnClicked()
        // or OnContextMenuSpawn(). Whatever internal gate/tap-gesture check is
        // stopping that isn't visible from outside the compiled assembly, so
        // rather than keep guessing at its private logic, the probe drives
        // these real stock methods itself from the native messages we know work.
        // OnLeftClick()/OnRightClick() are SpaceCenterBuilding's own public
        // entry points -- calling these instead of the raw OnClicked()/
        // OnContextMenuSpawn() overrides directly lets the base class set up
        // whatever supporting state (dismiss-safety, position tracking) it
        // normally does before invoking them itself. Calling OnContextMenuSpawn()
        // directly produced a dialog that closed itself almost immediately.
        public void TriggerClick() => OnLeftClick();
        public void TriggerContextMenu() => OnRightClick();
    }

    /// <summary>
    /// Independent diagnostic: every frame, raycasts from the active camera
    /// through the mouse position across ALL layers and logs what it hits,
    /// specifically around hover state changes and mouse clicks. This doesn't
    /// feed into SpaceCenterBuilding's own (private) hover/click logic at all --
    /// it exists purely to answer, from the outside, whether a collider is even
    /// present and reachable by a raycast at the observatory's screen position,
    /// since SpaceCenterBuilding gave no errors yet produced no visible effect.
    /// </summary>
    public class ExoObservatoryDebugProbe : MonoBehaviour
    {
        private const string Prefix = "[Exoplanets]";
        private Transform _root;
        private ExoObservatoryBuilding _target;
        private bool _wasHovering;
        private bool _loggedNoCamera;
        private float _nextStateLogTime;

        public void Init(Transform root, ExoObservatoryBuilding target)
        {
            _root = root;
            _target = target;
        }

        private void Start()
        {
            LogState("Start()");
        }

        private void Update()
        {
            // Periodic heartbeat of the base component's own state -- if
            // enabled ever goes false, Unity stops delivering it OnMouseDown/Up
            // entirely (silently, no error), which would explain hits being
            // detected by this probe's own raycast but OnClicked() never firing.
            if (Time.unscaledTime >= _nextStateLogTime)
            {
                _nextStateLogTime = Time.unscaledTime + 3f;
                LogState("heartbeat");
            }

            Camera cam = Camera.main;
            if (cam == null) cam = Camera.allCameras.FirstOrDefault(c => c.isActiveAndEnabled);
            if (cam == null)
            {
                if (!_loggedNoCamera)
                {
                    _loggedNoCamera = true;
                    Debug.Log($"{Prefix} No active camera found (Camera.main is null and no other active camera) -- cannot raycast under the mouse.");
                }
                return;
            }

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            bool didHit = Physics.Raycast(ray, out RaycastHit hitInfo, 50000f, ~0, QueryTriggerInteraction.Collide);
            bool hoveringUs = didHit && _root != null && hitInfo.collider.transform.IsChildOf(_root);

            if (hoveringUs != _wasHovering)
            {
                _wasHovering = hoveringUs;
                Debug.Log(hoveringUs
                    ? $"{Prefix} hover ENTER on '{hitInfo.collider.gameObject.name}' (camera='{cam.name}', layer={LayerMask.LayerToName(hitInfo.collider.gameObject.layer)}, distance={hitInfo.distance:F1}m)"
                    : $"{Prefix} hover EXIT");
            }

            if (Input.GetMouseButtonDown(0)) LogClick("LEFT", cam, didHit, hitInfo, hoveringUs);
            if (Input.GetMouseButtonDown(1)) LogClick("RIGHT", cam, didHit, hitInfo, hoveringUs);
        }

        private void LogState(string when)
        {
            if (_target == null)
            {
                Debug.Log($"{Prefix} [{when}] no SpaceCenterBuilding target reference.");
                return;
            }
            Debug.Log($"{Prefix} [{when}] SpaceCenterBuilding state: enabled={_target.enabled}, gameObject.activeInHierarchy={_target.gameObject.activeInHierarchy}, Operational={_target.Operational}, InView={_target.InView}, Facility={(_target.Facility != null ? _target.Facility.name : "NULL")}.");
        }

        private static void LogClick(string button, Camera cam, bool didHit, RaycastHit hitInfo, bool hoveringUs)
        {
            if (!didHit)
            {
                Debug.Log($"{Prefix} {button} click: raycast from camera '{cam.name}' hit NOTHING under the mouse (mousePos={Input.mousePosition}).");
                return;
            }
            Debug.Log($"{Prefix} {button} click: raycast hit '{hitInfo.collider.gameObject.name}' (layer={LayerMask.LayerToName(hitInfo.collider.gameObject.layer)}), belongsToObservatory={hoveringUs}, distance={hitInfo.distance:F1}m, camera='{cam.name}'.");
        }

        // Unity's OWN native mouse-picking -- confirmed reliable (fires every
        // session, many times) even though SpaceCenterBuilding's own private
        // OnMouseDown/OnMouseUp (which receive the exact same messages) never
        // end up calling OnClicked()/OnContextMenuSpawn(). Rather than keep
        // guessing at that private logic, drive the real stock methods
        // (HighLightBuilding, TriggerClick/TriggerContextMenu) directly from
        // here -- still the authentic stock glow/dialog, just triggered from a
        // path we've proven works instead of SpaceCenterBuilding's own.
        private void OnMouseEnter()
        {
            Debug.Log($"{Prefix} [native Unity] OnMouseEnter() fired on '{name}' -- driving HighLightBuilding(true).");
            _target?.HighLightBuilding(true);
        }

        private void OnMouseExit()
        {
            Debug.Log($"{Prefix} [native Unity] OnMouseExit() fired on '{name}' -- driving HighLightBuilding(false).");
            _target?.HighLightBuilding(false);
        }

        private void OnMouseDown()
        {
            Debug.Log($"{Prefix} [native Unity] OnMouseDown() fired on '{name}'.");
        }

        private void OnMouseUp()
        {
            Debug.Log($"{Prefix} [native Unity] OnMouseUp() fired on '{name}'.");
            // Confirmed via [Exoplanets] logs: on this (old, KSP1-era) Unity
            // version OnMouseDown/OnMouseUp only ever fire for the LEFT button
            // -- several right-clicks (seen via this probe's own manual
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
            // up -- matching it to the same release-triggered pattern as left
            // click fixes that.
            if (Input.GetMouseButtonUp(1))
            {
                Debug.Log($"{Prefix} [native Unity] OnMouseOver() saw a right-click release on '{name}' -- driving TriggerContextMenu() directly.");
                _target?.TriggerContextMenu();
            }
        }
    }

    /// <summary>
    /// Sanity-check spin for the dome, independent of the rest of the model.
    /// Placeholder for real sky-tracking (see SkyCoordinates.EquatorialToHorizontal)
    /// -- once that's wired up this constant-speed spin gets replaced by a
    /// Quaternion.LookRotation towards the current target's computed direction.
    /// </summary>
    public class ExoObservatoryDomeRotator : MonoBehaviour
    {
        public float DegreesPerSecond;
        public Vector3 LocalAxis = Vector3.up;

        private void Update()
        {
            transform.Rotate(LocalAxis, DegreesPerSecond * Time.deltaTime, Space.Self);
        }
    }
}
