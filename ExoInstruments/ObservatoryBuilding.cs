using System;
using System.Collections.Generic;
using UnityEngine;
using KerbalKonstructs;
using KerbalKonstructs.Core;
using ExoInstruments.Core;

namespace ExoInstruments
{
    /// <summary>
    /// Places a clickable Kerbal Konstructs static building at the KSC and wires
    /// its click event to open the observatory window -- the in-world alternative
    /// to the AppLauncher toolbar button. The model is currently a placeholder
    /// cube (ExoInstrumentsObservatory.mu); swapping it for a real building later
    /// needs no code changes since it's referenced by name only.
    /// </summary>
    public class ObservatoryBuilding
    {
        private const string ModelName = "ExoInstrumentsObservatory";
        private const string GroupName = "ExoInstruments";
        private const string BodyName = "Kerbin";

        // Converted from the "ExoInstruments" KK group's Position-panel offset
        // (X=32.5497, Z=-0.7167, relative to the group center at lat=-0.12512,
        // lon=-74.62276, heading=15.377 deg) into absolute lat/long.
        private const double LatitudeDeg = -0.126015;
        private const double LongitudeDeg = -74.619785;
        private const float AltitudeMeters = 20.1824f;
        private const float RotationDeg = 0f;
        private const double PositionToleranceDeg = 0.001; // ~100m -- close enough to be "this instance", far enough not to catch KSC's other buildings.

        private readonly Action _openObservatoryWindow;

        public ObservatoryBuilding(Action openObservatoryWindow)
        {
            _openObservatoryWindow = openObservatoryWindow;
        }

        public void Setup()
        {
            EnsurePlaced();
            API.RegisterOnStaticClicked(OnStaticClicked);
        }

        public void Teardown()
        {
            API.UnRegisterOnStaticClicked(OnStaticClicked);
        }

        /// <summary>
        /// Places the building once and keeps exactly one instance. Never reassigns the KK Group —
        /// doing so corrupts the internal name lookup ("SetNewName: cannot find at least ourself"),
        /// which drops the instance from GetAllStatics() and causes a duplicate on the next load.
        /// Match by model + position, not Group, to stay robust.
        /// </summary>
        private void EnsurePlaced()
        {
            var candidates = FindOwnInstances();

            StaticInstance instance;
            if (candidates.Count == 0)
            {
                string uuid = API.PlaceStatic(ModelName, BodyName, LatitudeDeg, LongitudeDeg, AltitudeMeters, RotationDeg, false, GroupName, null);
                if (string.IsNullOrEmpty(uuid))
                {
                    Debug.LogWarning("[ExoInstruments] Failed to place the Observatory building via Kerbal Konstructs.");
                    return;
                }

                instance = API.getStaticInstanceByUUID(uuid);
            }
            else
            {
                instance = candidates[0];
                for (int i = 1; i < candidates.Count; i++)
                {
                    API.RemoveStatic(candidates[i].UUID);
                }
            }

            if (instance == null) return;

            // KK's own "enable colliders" flag doesn't persist between sessions either,
            // and disabled colliders don't get hit by KK's click raycast even though our
            // fallback BoxCollider (below) exists on the mesh -- force it on every load.
            instance.ToggleAllColliders(true);

            EnsureClickable(instance);
        }

        /// <summary>Matches by model config name, and separately by position -- either check missing the instance a corrupted Group reassignment can produce is enough to still find it (and avoid placing a duplicate), and the union covers stray duplicates left over from earlier sessions.</summary>
        private static List<StaticInstance> FindOwnInstances()
        {
            var result = new List<StaticInstance>();
            var all = StaticDatabase.GetAllStatics();
            if (all == null) return result;

            foreach (var candidate in all)
            {
                if (candidate == null || candidate.CelestialBody == null || candidate.CelestialBody.bodyName != BodyName) continue;

                bool matchesConfig = candidate.configUrl != null && candidate.configUrl.name == ModelName;
                bool matchesPosition = Math.Abs(candidate.RefLatitude - LatitudeDeg) < PositionToleranceDeg &&
                                        Math.Abs(candidate.RefLongitude - LongitudeDeg) < PositionToleranceDeg;

                if (matchesConfig || matchesPosition)
                {
                    result.Add(candidate);
                }
            }

            return result;
        }

        /// <summary>The exported .mu has never carried a Collider component, so KK's click raycast has nothing to hit. Adds a BoxCollider sized from the renderer bounds instead of depending on the mesh export.</summary>
        private static void EnsureClickable(StaticInstance instance)
        {
            var meshObject = API.GetGameObject(instance.UUID);
            if (meshObject == null) return;

            if (meshObject.GetComponentInChildren<Collider>(true) != null) return;

            var renderer = meshObject.GetComponentInChildren<Renderer>(true);
            if (renderer == null) return;

            var target = renderer.gameObject;
            var box = target.AddComponent<BoxCollider>();
            box.center = target.transform.InverseTransformPoint(renderer.bounds.center);
            var lossyScale = target.transform.lossyScale;
            box.size = new Vector3(
                renderer.bounds.size.x / Mathf.Max(lossyScale.x, 0.0001f),
                renderer.bounds.size.y / Mathf.Max(lossyScale.y, 0.0001f),
                renderer.bounds.size.z / Mathf.Max(lossyScale.z, 0.0001f));
        }

        private void OnStaticClicked(StaticInstance instance)
        {
            if (instance == null) return;

            bool matchesConfig = instance.configUrl != null && instance.configUrl.name == ModelName;
            bool matchesPosition = Math.Abs(instance.RefLatitude - LatitudeDeg) < PositionToleranceDeg &&
                                    Math.Abs(instance.RefLongitude - LongitudeDeg) < PositionToleranceDeg;

            if (!matchesConfig && !matchesPosition) return;

            _openObservatoryWindow();
        }
    }
}
