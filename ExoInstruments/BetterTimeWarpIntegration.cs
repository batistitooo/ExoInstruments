using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace ExoInstruments
{
    /// <summary>
    /// Every "Warp to ..." button in the mod goes through here instead of
    /// calling TimeWarp.fetch.WarpTo directly, for one reason: stock WarpTo
    /// carries a hidden rateCap parameter defaulting to 100,000x, so even a
    /// player who installed a faster warp mod never gets past stock speed on
    /// our scheduled warps. Long observation campaigns (weeks of in-game
    /// nights) are exactly where that ceiling hurts.
    ///
    /// Soft dependency on BetterTimeWarpContinued (linuxgurugamer), resolved
    /// by reflection so the mod builds and runs without it:
    /// - BetterTimeWarp.BetterTimeWarp.Instance.customWarps holds the player's
    ///   warp-rate sets (Name, Rates[], Physics);
    /// - SetWarpRates(TimeWarpRates, bool) installs a set into
    ///   TimeWarp.fetch.warpRates -- which is the table stock WarpTo draws from.
    ///
    /// For a long enough warp we install the fastest non-physics set on offer
    /// (BetterTimeWarp posts its own on-screen message, so the switch is
    /// visible, and the player keeps the faster set afterwards -- that is the
    /// point of having installed the mod) and lift rateCap to that set's
    /// ceiling. Short hops stay on stock behavior: flipping the player's rate
    /// set to cross two hours would be churn, not service.
    /// </summary>
    public static class BetterTimeWarpIntegration
    {
        /// <summary>Only reach for the faster rate set when the jump is at least this long -- below it, stock 100,000x crosses the span in about two real seconds.</summary>
        private const double EngageThresholdSeconds = 216000.0; // 10 Kerbin days

        private const float StockRateCap = 100000f;
        private const float WarpToMaxSubstep = 20f; // stock default

        private static bool initialized;
        private static bool available;

        private static FieldInfo instanceField;
        private static FieldInfo customWarpsField;
        private static MethodInfo setWarpRatesMethod;
        private static MemberInfo ratesMember;    // TimeWarpRates.Rates : float[]
        private static MemberInfo physicsMember;  // TimeWarpRates.Physics : bool

        /// <summary>
        /// The one entry point: warp to targetUt, through BetterTimeWarp's
        /// fastest applicable rate set when the span justifies it, plain stock
        /// otherwise (or when BetterTimeWarp isn't installed).
        /// </summary>
        public static void WarpTo(double targetUt)
        {
            if (TimeWarp.fetch == null) return;

            float rateCap = StockRateCap;
            double span = targetUt - Planetarium.GetUniversalTime();
            if (span > EngageThresholdSeconds)
            {
                double engaged = TryEngageFastestRates();
                if (engaged > StockRateCap) rateCap = (float)engaged;
            }
            TimeWarp.fetch.WarpTo(targetUt, WarpToMaxSubstep, rateCap);
        }

        /// <summary>
        /// Installs the fastest compatible non-physics rate set BetterTimeWarp
        /// knows about and returns its top rate; 0 when BetterTimeWarp is
        /// absent, not ready, or has nothing faster than what's already loaded.
        /// Never throws: any reflection surprise (mod updated, member renamed)
        /// logs once and permanently falls back to stock.
        /// </summary>
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
                    // SetWarpRates silently refuses length mismatches -- skip
                    // sets it would refuse rather than "engaging" a no-op.
                    if (rates == null || rates.Length != currentRates.Length) continue;
                    double top = MaxOf(rates);
                    if (top > bestTop)
                    {
                        bestTop = top;
                        bestSet = set;
                    }
                }

                if (bestSet != null)
                {
                    // message: true -- BetterTimeWarp's own screen message is
                    // how the player learns their rate set just changed.
                    setWarpRatesMethod.Invoke(btw, new object[] { bestSet, true });
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
                Debug.Log("[ExoInstruments] BetterTimeWarpContinued not detected -- scheduled warps stay at the stock 100,000x ceiling.");
                return;
            }

            instanceField = btwType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            customWarpsField = btwType.GetField("customWarps", BindingFlags.Public | BindingFlags.Instance);
            setWarpRatesMethod = btwType.GetMethod("SetWarpRates", BindingFlags.Public | BindingFlags.Instance);
            if (instanceField == null || customWarpsField == null || setWarpRatesMethod == null)
            {
                Debug.LogWarning("[ExoInstruments] BetterTimeWarp found but its API surface changed -- integration disabled.");
                return;
            }

            Type ratesType = setWarpRatesMethod.GetParameters()[0].ParameterType;
            ratesMember = FieldOrProperty(ratesType, "Rates");
            physicsMember = FieldOrProperty(ratesType, "Physics");
            if (ratesMember == null || physicsMember == null)
            {
                Debug.LogWarning("[ExoInstruments] BetterTimeWarp found but TimeWarpRates layout changed -- integration disabled.");
                return;
            }

            available = true;
            Debug.Log("[ExoInstruments] BetterTimeWarpContinued detected -- scheduled warps may exceed the stock rate ceiling.");
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
