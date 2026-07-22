using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace ExoInstruments
{
    /// <summary>
    /// Every "Warp to ..." button in the mod goes through here instead of
    /// calling TimeWarp.fetch.WarpTo directly, for one reason: stock WarpTo
    /// only ever picks a rate from TimeWarp.fetch.warpRates -- the player's
    /// current rate table -- so even someone who installed a faster warp mod
    /// never gets past whatever ceiling that table already has on our
    /// scheduled warps. Long observation campaigns (weeks of in-game nights)
    /// are exactly where that ceiling hurts. (WarpTo's own two extra
    /// parameters, decompiled below, turned out to be a real-time pacing
    /// window, not a rate cap -- not what they look like from the names.)
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
    /// visible) and lift rateCap to that set's ceiling, then restore the
    /// table that was active before we touched it (see PollRestore) once this
    /// warp ends -- installing a custom set with sparse, coarse rate steps and
    /// leaving it in place broke every *ordinary* warp afterward: stock WarpTo
    /// leans on graduated intermediate rate steps to decelerate cleanly onto
    /// its target, and a custom set built as a handful of very high, widely
    /// spaced tiers has no fine steps left to ramp down through, so small
    /// warps after a single long one started overshooting and stuttering
    /// ("incoherent warp speed" -- reported after clicking a forecast cell
    /// far enough out to engage this path). Restoring afterward keeps the
    /// benefit for the long jump without leaving the player's whole warp
    /// experience on a coarser table than the one they had.
    /// </summary>
    public static class BetterTimeWarpIntegration
    {
        /// <summary>Only reach for the faster rate set when the jump is at least this long -- below it, stock 100,000x crosses the span in about two real seconds.</summary>
        private const double EngageThresholdSeconds = 216000.0; // 10 Kerbin days

        private static bool initialized;
        private static bool available;

        private static FieldInfo instanceField;
        private static FieldInfo customWarpsField;
        private static MethodInfo setWarpRatesMethod;
        private static MemberInfo ratesMember;    // TimeWarpRates.Rates : float[]
        private static MemberInfo physicsMember;  // TimeWarpRates.Physics : bool

        // Pending restore of the rate table that was active before this
        // integration last swapped it in for a long warp -- see PollRestore.
        private static float[] pendingRestoreRates;
        private static bool warpObservedActive;

        /// <summary>
        /// The one entry point: warp to targetUt, through BetterTimeWarp's
        /// fastest applicable rate set when the span justifies it, plain stock
        /// otherwise (or when BetterTimeWarp isn't installed).
        /// </summary>
        public static void WarpTo(double targetUt)
        {
            if (TimeWarp.fetch == null) return;

            double span = targetUt - Planetarium.GetUniversalTime();
            if (span > EngageThresholdSeconds)
            {
                TryEngageFastestRates();
            }
            // Decompiled the real WarpTo(UT, maxTimeWarping, minTimeWarping):
            // those two args are NOT a rate ceiling/floor at all (an earlier
            // fix here assumed that, from the parameter names alone, and was
            // still wrong). They're a real-world PACING window in seconds
            // (stock defaults 8.0 / 2.5): internally it picks the fastest
            // rate from TimeWarp.fetch.warpRates such that the warp itself
            // plays out in roughly that many real seconds, and if the whole
            // jump would take under maxTimeWarping seconds even at 1x, it
            // declines to warp at all (returns rate 1 unconditionally).
            // Passing our computed rateCap (~100,000) into that slot meant
            // ANY jump under ~100,000 seconds (~27.7h) short-circuited to
            // "too small to bother" and silently stayed at 1x -- e.g. every
            // multi-hour heatmap click. The actual speed ceiling was always
            // the warpRates table itself, which TryEngageFastestRates above
            // already raises for long jumps; stock defaults here just pick
            // the fastest rate in whatever table is active with sane pacing.
            TimeWarp.fetch.WarpTo(targetUt);
        }

        /// <summary>
        /// Call once per frame (e.g. from a MonoBehaviour Update) so a rate
        /// table swapped in for one long warp gets put back once that warp is
        /// no longer running. Waits for at least one frame of the warp
        /// actually being active first, so it doesn't restore on the same
        /// frame WarpTo was issued, before the game has ramped the rate index
        /// up off zero.
        /// </summary>
        public static void PollRestore()
        {
            if (pendingRestoreRates == null || TimeWarp.fetch == null) return;

            if (TimeWarp.CurrentRateIndex > 0)
            {
                warpObservedActive = true;
                return;
            }
            if (!warpObservedActive) return; // hasn't ramped up yet -- not finished, never started

            TimeWarp.fetch.warpRates = pendingRestoreRates;
            pendingRestoreRates = null;
            warpObservedActive = false;
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
                    // Keep whatever table was genuinely active before any swap
                    // of ours -- if a restore from an earlier long warp is
                    // still pending, currentRates is already OUR substitute,
                    // not the player's original, and must not overwrite it.
                    if (pendingRestoreRates == null)
                    {
                        pendingRestoreRates = (float[])currentRates.Clone();
                        warpObservedActive = false;
                    }
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
