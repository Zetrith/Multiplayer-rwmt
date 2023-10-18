using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Patches;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    [EarlyPatch]
    [HarmonyPatch]
    public static class DeferredStackTracing
    {
        public static int ignoreTraces;
        public static long maxTraceDepth;
        public static int randCalls;

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Value));
            yield return AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Int));
            //yield return AccessTools.Method(typeof(Thing), nameof(Thing.DeSpawn));
        }

        public static int acc;

        public static void Postfix()
        {
            if (Native.LmfPtr == 0) return;
            if (!ShouldAddStackTraceForDesyncLog()) return;

            var logItem = StackTraceLogItemRaw.GetFromPool();
            var trace = logItem.raw;
            int hash = 0;
            int depth = DeferredStackTracingImpl.TraceImpl(trace, ref hash);

            Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(logItem, depth, hash);

            acc++;
        }

        public static bool ShouldAddStackTraceForDesyncLog()
        {
            if (Multiplayer.Client == null) return false;
            if (Multiplayer.settings.desyncTracingMode == DesyncTracingMode.None) return false;
            if (Multiplayer.game == null) return false;

            // Only log if debugging enabled in Host Server menu
            if (!Multiplayer.game.gameComp.logDesyncTraces) return false;

            if (Rand.stateStack.Count > 1) return false;
            if (Multiplayer.IsReplay) return false;

            if (!Multiplayer.Ticking && !Multiplayer.ExecutingCmds) return false;

            return ignoreTraces == 0;
        }
    }

    [HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.WildAnimalSpawnerTick))]
    static class WildAnimalSpawnerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Postfix() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.WildPlantSpawnerTick))]
    static class WildPlantSpawnerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Postfix() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick))]
    static class SteadyEnvironmentEffectsTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Postfix() => DeferredStackTracing.ignoreTraces--;
    }

    /*[HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellForWorker))]
    static class FindBestStorageCellTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Postfix() => DeferredStackTracing.ignoreTraces--;
    }*/

    [HarmonyPatch(typeof(IntermittentSteamSprayer), nameof(IntermittentSteamSprayer.SteamSprayerTick))]
    static class SteamSprayerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Postfix() => DeferredStackTracing.ignoreTraces--;
    }
}
