using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace ExoInstruments
{
    /// <summary>
    /// Routes all "Warp to..." calls through BetterTimeWarpContinued's fastest rate set
    /// when the jump is long enough to benefit. Soft dependency via reflection.
    /// Restores the player's original rate table when the warp ends — leaving a sparse
    /// custom set in place broke ordinary short warps (they'd overshoot with no fine
    /// deceleration steps).
    /// </summary>
    public static class BetterTimeWarpIntegration
    {
        /// <summary>Below this threshold, stock 100,000x is fast enough — no need to swap rate sets.</summary>
        private const double EngageThresholdSeconds = 216000.0; // 10 Kerbin days

        private static bool initialized;
        private static bool available;

        private static FieldInfo instanceField;
        private static FieldInfo customWarpsField;
        private static MethodInfo setWarpRatesMethod;
        private static MemberInfo ratesMember;    // TimeWarpRates.Rates : float[]
        private static MemberInfo physicsMember;  // TimeWarpRates.Physics : bool

        // Pending restore of the rate table that was active before this
        // integration last swapped it in for a long warp; see PollRestore.
        private static float[] pendingRestoreRates;
        private static bool warpObservedActive;

        /// <summary>Warp to targetUt, using BetterTimeWarp's fastest rate set when the span justifies it.</summary>
        public static void WarpTo(double targetUt)
        {
            if (TimeWarp.fetch == null) return;

            double span = targetUt - Planetarium.GetUniversalTime();
            if (span > EngageThresholdSeconds)
            {
                TryEngageFastestRates();
            }
            // WarpTo's two extra params are a real-time pacing window (not a rate cap,
            // as the names imply). Stock defaults let it pick the fastest rate with sane
            // pacing; the actual speed ceiling is the warpRates table, already raised above.
            TimeWarp.fetch.WarpTo(targetUt);
        }

        /// <summary>
        /// Call once per frame — restores the original rate table when the warp ends.
        /// Waits for at least one active frame before restoring, so it doesn't fire
        /// before the game has ramped up off rate index 0.
        /// </summary>
        public static void PollRestore()
        {
            if (pendingRestoreRates == null || TimeWarp.fetch == null) return;

            if (TimeWarp.CurrentRateIndex > 0)
            {
                warpObservedActive = true;
                return;
            }
            if (!warpObservedActive) return; // hasn't ramped up yet, not finished, never started

            TimeWarp.fetch.warpRates = pendingRestoreRates;
            pendingRestoreRates = null;
            warpObservedActive = false;
        }

        /// <summary>Installs BetterTimeWarp's fastest non-physics rate set; 0 if unavailable or nothing faster. Falls back to stock permanently on any reflection failure.</summary>
        private static double TryEngageFastestRates()
        {
            try
            {
                EnsureInitialized();
                if (!available) return 0.0;

                object btw = instanceField.GetValue(null);
                if (btw == null) return 0.0; // scene without the addon alive

                var currentRates = TimeWarp.fetch.warpRates;
                if (currentRates == null || currentRates.Length == 0) return 0.0;

                object bestSet = null;
                double bestTop = MaxOf(currentRates);
                foreach (object set in (IEnumerable)customWarpsField.GetValue(btw))
                {
                    if ((bool)ReadMember(physicsMember, set)) continue;
                    var rates = (float[])ReadMember(ratesMember, set);
                    if (rates == null || rates.Length != currentRates.Length) continue; // SetWarpRates silently rejects length mismatches
                    double top = MaxOf(rates);
                    if (top > bestTop)
                    {
                        bestTop = top;
                        bestSet = set;
                    }
                }

                if (bestSet != null)
                {
                    // Don't overwrite pendingRestoreRates if a restore is already pending —
                    // currentRates would already be our substitute, not the player's original.
                    if (pendingRestoreRates == null)
                    {
                        pendingRestoreRates = (float[])currentRates.Clone();
                        warpObservedActive = false;
                    }
                    setWarpRatesMethod.Invoke(btw, new object[] { bestSet, true }); // true = show BetterTimeWarp's own notification
                }
                return bestTop;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ExoInstruments] BetterTimeWarp integration failed, falling back to stock warp rates: " + e.Message);
                available = false; // don't retry a broken reflection surface every warp
                return 0.0;
            }
        }

        private static void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;
            available = false;

            Type btwType = null;
            foreach (var loaded in AssemblyLoader.loadedAssemblies)
            {
                btwType = loaded.assembly.GetType("BetterTimeWarp.BetterTimeWarp");
                if (btwType != null) break;
            }
            if (btwType == null)
            {
                Debug.Log("[ExoInstruments] BetterTimeWarpContinued not detected; scheduled warps stay at the stock 100,000x ceiling.");
                return;
            }

            instanceField = btwType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            customWarpsField = btwType.GetField("customWarps", BindingFlags.Public | BindingFlags.Instance);
            setWarpRatesMethod = btwType.GetMethod("SetWarpRates", BindingFlags.Public | BindingFlags.Instance);
            if (instanceField == null || customWarpsField == null || setWarpRatesMethod == null)
            {
                Debug.LogWarning("[ExoInstruments] BetterTimeWarp found but its API surface changed, integration disabled.");
                return;
            }

            Type ratesType = setWarpRatesMethod.GetParameters()[0].ParameterType;
            ratesMember = FieldOrProperty(ratesType, "Rates");
            physicsMember = FieldOrProperty(ratesType, "Physics");
            if (ratesMember == null || physicsMember == null)
            {
                Debug.LogWarning("[ExoInstruments] BetterTimeWarp found but TimeWarpRates layout changed, integration disabled.");
                return;
            }

            available = true;
            Debug.Log("[ExoInstruments] BetterTimeWarpContinued detected; scheduled warps may exceed the stock rate ceiling.");
        }

        private static MemberInfo FieldOrProperty(Type type, string name)
        {
            MemberInfo m = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return m ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        }

        private static object ReadMember(MemberInfo member, object target)
        {
            var field = member as FieldInfo;
            return field != null ? field.GetValue(target) : ((PropertyInfo)member).GetValue(target, null);
        }

        private static double MaxOf(float[] values)
        {
            double max = 0.0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > max) max = values[i];
            }
            return max;
        }
    }
}
