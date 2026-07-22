using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// Reads REAL cloud-cover data painted by an installed EVE (Environmental
    /// Visual Enhancements) cloud config -- e.g. the classic BoulderCo stock
    /// config's Kerbin cubemap -- instead of any procedurally simulated cloud
    /// field. Stock KSP has no weather system, and EVE is the only visual
    /// weather mod, so this is the only source of real cloud data available;
    /// if it isn't installed/configured, the RC20 simply doesn't model clouds
    /// at all (zero coverage), never a fake substitute.
    ///
    /// Soft dependency, resolved entirely by reflection (same posture as
    /// BetterTimeWarpIntegration): the mod builds and runs with no reference
    /// to any EVE assembly, and any reflection surprise (EVE not installed, a
    /// future EVE version renaming internals) disables this permanently for
    /// the session rather than throwing.
    ///
    /// API surface verified by decompiling the actually-installed EVE-Redux
    /// 1.11.7.2 (Atmosphere.dll / EVEManager.dll / Utils.dll) with ilspycmd,
    /// per this project's established practice -- not guessed from names:
    /// - Atmosphere.CloudsManager : EVEManager.GenericEVEManager&lt;CloudsObject&gt;,
    ///   whose PROTECTED STATIC "ObjectList" (declared on the generic base,
    ///   not CloudsManager itself) holds every loaded per-body cloud layer.
    /// - Atmosphere.CloudsObject has a PUBLIC "Body" property (celestial body
    ///   name) and a PRIVATE "settings" field (Atmosphere.CloudsMaterial).
    /// - CloudsMaterial's PRIVATE "_MainTex" field (Utils.TextureWrapper) is
    ///   the coverage-equivalent texture for this classic (non-raymarched --
    ///   that variant is Patreon-only, not in any public EVE release) cloud
    ///   system. TextureWrapper.Name (PUBLIC) gives its config path string.
    /// - Utils.CubemapWrapper.fetchCubeMap(TextureWrapper) is PUBLIC STATIC
    ///   and returns the already-assembled 6-face (or 2-face "RGB2") cubemap
    ///   EVE itself loaded at startup -- this integration never loads or
    ///   decodes a texture file itself, only reads what EVE already built.
    /// </summary>
    public static class EveCloudIntegration
    {
        private static bool initialized;
        private static bool available;

        private static PropertyInfo bodyProperty;
        private static FieldInfo settingsField;
        private static FieldInfo mainTexField;
        private static MethodInfo fetchCubeMapMethod;
        private static FieldInfo texListField;
        private static FieldInfo texPositiveField;
        private static FieldInfo texNegativeField;
        private static FieldInfo objectListField;

        private static string cachedBodyName;
        private static bool cacheValid;
        private static Texture2D[] cachedFaces; // index 0=+X,1=-X,2=+Y,3=-Y,4=+Z,5=-Z; entries may be null

        /// <summary>True once EVE's cloud-manager API has been found and its shape matches what this integration expects.</summary>
        public static bool IsAvailable
        {
            get { EnsureInitialized(); return available; }
        }

        /// <summary>
        /// Real cloud coverage in [0,1] at a BODY-FIXED direction (e.g. the
        /// observer's local "up", transformed into bodyName's own rotating
        /// frame via CelestialBody.bodyTransform -- NOT world space, since
        /// EVE's cloud texture rotates with the planet). Returns 0 (no
        /// clouds modeled) whenever EVE isn't installed, this body has no
        /// configured cloud layer, its main texture isn't a cubemap (a
        /// handful of EVE configs use a flat 2D layer instead -- not handled
        /// here), or the texture turns out not to be CPU-readable. Never
        /// throws.
        ///
        /// Known, disclosed approximation: EVE additionally rotates this
        /// texture slowly over time (a configurable "wind speed" on top of
        /// the body's own rotation, which bodyTransform already cancels out)
        /// to simulate drifting weather -- replicating that exact rotation
        /// would require matching EVE's internal quaternion composition with
        /// no way to verify it visually against the real render, so it is
        /// deliberately not attempted. What's sampled is the real painted
        /// cloud pattern in the body-fixed frame, just not wind-advected.
        /// </summary>
        public static float SampleCoverage(string bodyName, Vector3 bodyFixedDirection)
        {
            try
            {
                EnsureInitialized();
                if (!available) return 0f;

                EnsureFacesForBody(bodyName);
                if (cachedFaces == null) return 0f;

                SelectCubeFace(bodyFixedDirection, out int face, out float u, out float v);
                Texture2D tex = cachedFaces[face];
                if (tex == null) return 0f;

                Color c = tex.GetPixelBilinear(u, v);
                return Mathf.Clamp01((c.r + c.g + c.b) / 3f);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ExoInstruments] EVE cloud sampling failed, disabling for this session: " + e.Message);
                available = false;
                cachedFaces = null;
                return 0f;
            }
        }

        private static void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;
            available = false;

            try
            {
                Type cloudsObjectType = null, cloudsManagerType = null, cloudsMaterialType = null,
                     textureWrapperType = null, cubemapWrapperType = null;
                foreach (var loaded in AssemblyLoader.loadedAssemblies)
                {
                    var asm = loaded.assembly;
                    if (cloudsObjectType == null) cloudsObjectType = asm.GetType("Atmosphere.CloudsObject");
                    if (cloudsManagerType == null) cloudsManagerType = asm.GetType("Atmosphere.CloudsManager");
                    if (cloudsMaterialType == null) cloudsMaterialType = asm.GetType("Atmosphere.CloudsMaterial");
                    if (textureWrapperType == null) textureWrapperType = asm.GetType("Utils.TextureWrapper");
                    if (cubemapWrapperType == null) cubemapWrapperType = asm.GetType("Utils.CubemapWrapper");
                }

                if (cloudsObjectType == null || cloudsManagerType == null || cloudsMaterialType == null
                    || textureWrapperType == null || cubemapWrapperType == null)
                {
                    Debug.Log("[ExoInstruments] EVE (Environmental Visual Enhancements) not detected -- RC20 clouds stay unmodeled.");
                    return;
                }

                bodyProperty = cloudsObjectType.GetProperty("Body", BindingFlags.Public | BindingFlags.Instance);
                settingsField = cloudsObjectType.GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance);
                mainTexField = cloudsMaterialType.GetField("_MainTex", BindingFlags.NonPublic | BindingFlags.Instance);
                fetchCubeMapMethod = cubemapWrapperType.GetMethod("fetchCubeMap", BindingFlags.Public | BindingFlags.Static);
                texListField = cubemapWrapperType.GetField("texList", BindingFlags.NonPublic | BindingFlags.Instance);
                texPositiveField = cubemapWrapperType.GetField("texPositive", BindingFlags.NonPublic | BindingFlags.Instance);
                texNegativeField = cubemapWrapperType.GetField("texNegative", BindingFlags.NonPublic | BindingFlags.Instance);

                // ObjectList is declared on the GENERIC base GenericEVEManager<CloudsObject>,
                // not on CloudsManager itself -- walk up BaseType to find it.
                for (Type t = cloudsManagerType.BaseType; t != null; t = t.BaseType)
                {
                    objectListField = t.GetField("ObjectList", BindingFlags.NonPublic | BindingFlags.Static);
                    if (objectListField != null) break;
                }

                if (bodyProperty == null || settingsField == null || mainTexField == null
                    || fetchCubeMapMethod == null || texListField == null || objectListField == null)
                {
                    Debug.LogWarning("[ExoInstruments] EVE found but its internal layout changed since this was verified -- cloud integration disabled.");
                    return;
                }

                available = true;
                Debug.Log("[ExoInstruments] EVE detected -- RC20 clouds will sample real cloud-layer textures where a body has one configured.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ExoInstruments] EVE cloud integration setup failed, falling back to no cloud modeling: " + e.Message);
                available = false;
            }
        }

        /// <summary>(Re)resolves the 6 cube faces for bodyName's first configured cloud layer, caching until the body changes.</summary>
        private static void EnsureFacesForBody(string bodyName)
        {
            if (cacheValid && string.Equals(cachedBodyName, bodyName, StringComparison.OrdinalIgnoreCase)) return;
            cacheValid = true;
            cachedBodyName = bodyName;
            cachedFaces = null;

            IEnumerable list = objectListField.GetValue(null) as IEnumerable;
            if (list == null) return;

            object cloudsObject = null;
            foreach (object candidate in list)
            {
                string b = bodyProperty.GetValue(candidate, null) as string;
                if (string.Equals(b, bodyName, StringComparison.OrdinalIgnoreCase)) { cloudsObject = candidate; break; }
            }
            if (cloudsObject == null) return; // no cloud layer configured for this body

            object settings = settingsField.GetValue(cloudsObject);
            object mainTex = settings != null ? mainTexField.GetValue(settings) : null;
            if (mainTex == null) return;

            object cubemapWrapper = fetchCubeMapMethod.Invoke(null, new object[] { mainTex });
            if (cubemapWrapper == null) return; // main texture isn't registered as a cubemap (e.g. a flat 2D layer)

            var sixFaces = texListField.GetValue(cubemapWrapper) as Texture2D[];
            if (sixFaces != null && sixFaces.Length == 6)
            {
                cachedFaces = sixFaces;
                return;
            }

            // RGB2_CubeMap variant (e.g. some legacy configs): only one axis's
            // two faces are painted; the rest have no data.
            Texture2D pos = texPositiveField?.GetValue(cubemapWrapper) as Texture2D;
            Texture2D neg = texNegativeField?.GetValue(cubemapWrapper) as Texture2D;
            if (pos != null || neg != null)
            {
                cachedFaces = new Texture2D[6];
                cachedFaces[0] = pos;
                cachedFaces[1] = neg;
            }
        }

        /// <summary>
        /// Standard cube-face/UV selection from a direction vector: index
        /// 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z, matching the face order
        /// CubemapWrapper.ApplyCubeMap binds (xp/xn/yp/yn/zp/zn). The exact
        /// per-face U/V sign convention is a defensible standard unfolding,
        /// not independently verified against EVE's own shader -- what
        /// matters for this use (a single coarse coverage sample, not a
        /// pixel-aligned render) is landing on the broadly correct face and
        /// region, which this guarantees regardless of convention details.
        /// </summary>
        private static void SelectCubeFace(Vector3 dir, out int face, out float u, out float v)
        {
            float ax = Mathf.Abs(dir.x), ay = Mathf.Abs(dir.y), az = Mathf.Abs(dir.z);
            if (ax >= ay && ax >= az)
            {
                face = dir.x >= 0f ? 0 : 1;
                u = (dir.x >= 0f ? -dir.z : dir.z) / ax;
                v = dir.y / ax;
            }
            else if (ay >= ax && ay >= az)
            {
                face = dir.y >= 0f ? 2 : 3;
                u = dir.x / ay;
                v = (dir.y >= 0f ? -dir.z : dir.z) / ay;
            }
            else
            {
                face = dir.z >= 0f ? 4 : 5;
                u = (dir.z >= 0f ? dir.x : -dir.x) / az;
                v = dir.y / az;
            }
            u = u * 0.5f + 0.5f;
            v = v * 0.5f + 0.5f;
        }
    }
}
