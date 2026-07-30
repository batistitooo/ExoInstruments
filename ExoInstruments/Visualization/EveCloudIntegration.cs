using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// Reads real cloud cover from an installed EVE (Environmental Visual Enhancements)
    /// cloud config via reflection. Soft dependency — builds and runs without EVE;
    /// returns 0 when EVE isn't installed. Never falls back to procedural clouds.
    /// API verified by decompiling EVE-Redux 1.11.7.2 with ilspycmd.
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
        /// Cloud coverage [0,1] at a body-fixed direction (EVE's texture rotates with the
        /// planet, so pass the direction in bodyTransform space, not world space). Returns 0
        /// when EVE isn't installed, no cloud layer is configured for the body, or the read
        /// fails. Known approximation: EVE's wind-drift rotation isn't replicated.
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
                    Debug.Log("[ExoInstruments] EVE (Environmental Visual Enhancements) not detected; RC20 clouds stay unmodeled.");
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
                // not on CloudsManager itself; walk up BaseType to find it.
                for (Type t = cloudsManagerType.BaseType; t != null; t = t.BaseType)
                {
                    objectListField = t.GetField("ObjectList", BindingFlags.NonPublic | BindingFlags.Static);
                    if (objectListField != null) break;
                }

                if (bodyProperty == null || settingsField == null || mainTexField == null
                    || fetchCubeMapMethod == null || texListField == null || objectListField == null)
                {
                    Debug.LogWarning("[ExoInstruments] EVE found but its internal layout changed since this was verified, cloud integration disabled.");
                    return;
                }

                available = true;
                Debug.Log("[ExoInstruments] EVE detected; RC20 clouds will sample real cloud-layer textures where a body has one configured.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ExoInstruments] EVE cloud integration setup failed, falling back to no cloud modeling: " + e.Message);
                available = false;
            }
        }

        /// <summary>Loads (and caches) the 6 cubemap faces for the body's first cloud layer.</summary>
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

        /// <summary>Standard cube-face/UV selection from a direction vector: index 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z.</summary>
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
