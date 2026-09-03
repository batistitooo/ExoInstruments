using System;
using System.Reflection;
using UnityEngine;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// Forces a body's scaled-space textures to be resident before the telescope renders it.
    /// Soft dependency on Kopernicus via reflection, builds and runs without it, and does
    /// nothing when it isn't installed. API verified by decompiling Kopernicus.dll.
    ///
    /// Why this is necessary: large planet packs (Real Solar System above all) ship scaled-space
    /// textures far too big to keep resident for every body at once, so Kopernicus loads and
    /// unloads them ON DEMAND, driven by Unity's OnBecameVisible/OnBecameInvisible on each
    /// body's own renderer. That visibility is decided by the cameras the game knows about,
    /// and this mod's telescope renders through its own cloned, off-screen cameras, which
    /// Kopernicus has no reason to account for.
    ///
    /// The result is a body that is unloaded precisely while being photographed: the mesh is
    /// still there, so it draws, but with no colour map bound it comes out as a black disc with
    /// a lit rim. It is intermittent by nature; it depends on whether the body happened to be
    /// visible to a real camera recently, which makes it look like almost anything else:
    /// resolution-dependent, filter-dependent, or triggered by alt-tabbing (which is really just
    /// OnBecameInvisible firing on focus loss). None of those were the cause.
    ///
    /// ScaledSpaceOnDemand.LoadTextures() is public and synchronous; it pumps its own loader
    /// coroutine to completion before returning, so calling it immediately before the render
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
        /// Requests that body's scaled-space textures (and those of anything sharing its field,
        /// its own moons) be resident, and reports whether they ALREADY WERE.
        ///
        /// **The return value is the point.** The original version of this returned void, on the
        /// documented belief that "ScaledSpaceOnDemand.LoadTextures() is public and synchronous;
        /// it pumps its own loader coroutine to completion before returning", so that calling it
        /// immediately before the render guaranteed the texture was bound. A KSP.log from a real
        /// session says otherwise:
        ///
        ///     16:27:28.875  [ExoInstruments] Kopernicus on-demand ... detected
        ///     16:27:29.204  [OD] Loaded texture Jupiter_Dummy.dds
        ///
        /// 329 ms between the call and the load, and elsewhere in the same log 1.9 s between an
        /// unload and the matching reload. The load is DEFERRED, so rendering in the same
        /// synchronous block draws a body whose colour map is not bound yet: a black disc with a
        /// lit rim, intermittent, because a body that happened to be resident already needs no
        /// load and comes out perfectly.
        ///
        /// Returning residency lets the caller wait a frame instead of rendering into that gap.
        ///
        /// No-op returning true without Kopernicus, or for a body it doesn't manage on demand:
        /// in both cases there is nothing to wait for.
        /// </summary>
        public static bool EnsureScaledSpaceTexturesLoaded(CelestialBody body)
        {
            EnsureInitialized();
            if (!available || body == null) return true;

            bool resident = LoadFor(body);

            // A moon of the target sits in the same frame at any realistic field of view, and is
            // unloaded by exactly the same mechanism, a Galilean moon rendering as a black dot
            // beside a correctly-rendered Jupiter is the same bug, just less obvious.
            if (body.orbitingBodies != null)
            {
                // Deliberately not short-circuited: every moon must have its load REQUESTED even
                // once one is known to be missing, or waiting would restart the queue each frame.
                for (int i = 0; i < body.orbitingBodies.Count; i++)
                    resident &= LoadFor(body.orbitingBodies[i]);
            }
            return resident;
        }

        // Requests this one body's textures; true when they were already resident.
        private static bool LoadFor(CelestialBody body)
        {
            if (body == null || body.scaledBody == null) return true;

            try
            {
                Component demand = body.scaledBody.GetComponent(scaledSpaceOnDemandType);
                if (demand == null) return true; // not an on-demand body (small pack, or textures kept resident)

                if (isLoadedField != null && isLoadedField.GetValue(demand) is bool loaded && loaded) return true;

                loadTexturesMethod.Invoke(demand, null);
                return false; // requested, not yet resident
            }
            catch (Exception e)
            {
                // NOT disabled for the session any more. A single transient failure used to set
                // available = false permanently, silently removing the protection from every
                // later capture in that session; the failure mode is "it worked, then it
                // stopped", which is far harder to diagnose than a warning per occurrence.
                Debug.LogWarning($"[ExoInstruments] Could not force-load scaled-space textures for {body.bodyName}: "
                               + e.Message);
                return true; // nothing to wait for; do not stall the capture on a body that throws
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
                    Debug.Log("[ExoInstruments] Kopernicus on-demand scaled-space loading not detected; "
                            + "planet textures are assumed to stay resident.");
                    return;
                }

                loadTexturesMethod = scaledSpaceOnDemandType.GetMethod("LoadTextures",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                isLoadedField = scaledSpaceOnDemandType.GetField("isLoaded", BindingFlags.Public | BindingFlags.Instance);

                if (loadTexturesMethod == null)
                {
                    Debug.LogWarning("[ExoInstruments] Kopernicus found but ScaledSpaceOnDemand.LoadTextures() is missing; "
                                   + "its layout changed since this was verified; on-demand integration disabled.");
                    return;
                }

                available = true;
                Debug.Log("[ExoInstruments] Kopernicus on-demand scaled-space loading detected; "
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
