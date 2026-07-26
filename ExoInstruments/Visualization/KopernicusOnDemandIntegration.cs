using System;
using System.Reflection;
using UnityEngine;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// Forces a body's scaled-space textures to be resident before the telescope renders it.
    /// Soft dependency on Kopernicus via reflection -- builds and runs without it, and does
    /// nothing when it isn't installed. API verified by decompiling Kopernicus.dll.
    ///
    /// Why this is necessary: large planet packs (Real Solar System above all) ship scaled-space
    /// textures far too big to keep resident for every body at once, so Kopernicus loads and
    /// unloads them ON DEMAND, driven by Unity's OnBecameVisible/OnBecameInvisible on each
    /// body's own renderer. That visibility is decided by the cameras the game knows about --
    /// and this mod's telescope renders through its own cloned, off-screen cameras, which
    /// Kopernicus has no reason to account for.
    ///
    /// The result is a body that is unloaded precisely while being photographed: the mesh is
    /// still there, so it draws, but with no colour map bound it comes out as a black disc with
    /// a lit rim. It is intermittent by nature -- it depends on whether the body happened to be
    /// visible to a real camera recently -- which makes it look like almost anything else:
    /// resolution-dependent, filter-dependent, or triggered by alt-tabbing (which is really just
    /// OnBecameInvisible firing on focus loss). None of those were the cause.
    ///
    /// ScaledSpaceOnDemand.LoadTextures() is public and synchronous -- it pumps its own loader
    /// coroutine to completion before returning -- so calling it immediately before the render
    /// guarantees the texture is bound by the time the camera draws.
    /// </summary>
    public static class KopernicusOnDemandIntegration
    {
        private static bool initialized;
        private static bool available;

        private static Type scaledSpaceOnDemandType;
        private static MethodInfo loadTexturesMethod;
        private static FieldInfo isLoadedField;

        /// <summary>True once Kopernicus's on-demand scaled-space loader has been found and its shape matches what this integration expects.</summary>
        public static bool IsAvailable
        {
            get { EnsureInitialized(); return available; }
        }

        /// <summary>
        /// Makes sure body's scaled-space textures (and those of anything sharing its field --
        /// its own moons) are loaded. No-op without Kopernicus, or for a body it doesn't manage
        /// on demand. Safe to call every capture: it checks the loader's own isLoaded flag first,
        /// so an already-resident body costs a field read.
        /// </summary>
        public static void EnsureScaledSpaceTexturesLoaded(CelestialBody body)
        {
            EnsureInitialized();
            if (!available || body == null) return;

            LoadFor(body);

            // A moon of the target sits in the same frame at any realistic field of view, and is
            // unloaded by exactly the same mechanism -- a Galilean moon rendering as a black dot
            // beside a correctly-rendered Jupiter is the same bug, just less obvious.
            if (body.orbitingBodies != null)
            {
                for (int i = 0; i < body.orbitingBodies.Count; i++) LoadFor(body.orbitingBodies[i]);
            }
        }

        private static void LoadFor(CelestialBody body)
        {
            if (body == null || body.scaledBody == null) return;

            try
            {
                Component demand = body.scaledBody.GetComponent(scaledSpaceOnDemandType);
                if (demand == null) return; // not an on-demand body (small pack, or textures kept resident)

                if (isLoadedField != null && isLoadedField.GetValue(demand) is bool loaded && loaded) return;

                loadTexturesMethod.Invoke(demand, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ExoInstruments] Could not force-load scaled-space textures for {body.bodyName}, "
                               + "disabling on-demand integration for this session: " + e.Message);
                available = false;
            }
        }

        private static void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;
            available = false;

            try
            {
                foreach (var loaded in AssemblyLoader.loadedAssemblies)
                {
                    scaledSpaceOnDemandType = loaded.assembly.GetType("Kopernicus.OnDemand.ScaledSpaceOnDemand");
                    if (scaledSpaceOnDemandType != null) break;
                }

                if (scaledSpaceOnDemandType == null)
                {
                    Debug.Log("[ExoInstruments] Kopernicus on-demand scaled-space loading not detected -- "
                            + "planet textures are assumed to stay resident.");
                    return;
                }

                loadTexturesMethod = scaledSpaceOnDemandType.GetMethod("LoadTextures",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                isLoadedField = scaledSpaceOnDemandType.GetField("isLoaded", BindingFlags.Public | BindingFlags.Instance);

                if (loadTexturesMethod == null)
                {
                    Debug.LogWarning("[ExoInstruments] Kopernicus found but ScaledSpaceOnDemand.LoadTextures() is missing -- "
                                   + "its layout changed since this was verified; on-demand integration disabled.");
                    return;
                }

                available = true;
                Debug.Log("[ExoInstruments] Kopernicus on-demand scaled-space loading detected -- "
                        + "target textures will be forced resident before each capture.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ExoInstruments] Kopernicus on-demand integration setup failed: " + e.Message);
                available = false;
            }
        }
    }
}
