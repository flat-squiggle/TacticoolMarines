using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Ai.Vail.StimuliTypes;
using Sons.Gameplay;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Scales how far Kelvin (and the soldiers) look for item holders (log holders, stick/stone holders, sleds ...).
    /// Only affects Robby-type actors and only holder stimuli; every other AI search stays untouched.
    /// </summary>
    internal static class HolderRangePatch
    {
        private static bool _patched;
        private static Il2CppSystem.Type[] _structureTypes;
        private static readonly System.Collections.Generic.Dictionary<IntPtr, bool> TypeCache = new();
        private static readonly string[] StructureWords = { "Holder", "Sled", "Build", "Structure", "Repair", "Sharpen", "Maintain", "Shelter" };
        private static bool _reportedActor, _reportedClosest, _reportedRobby;

        public static void Apply()
        {
            if (_patched) return;
            _patched = true;
            try
            {
                var h = new HarmonyLib.Harmony("livingsoldiers.holderrange");
                var t = typeof(Il2CppSystem.Type);
                var m1 = AccessTools.Method(typeof(VailActor), "IsStimuliInRange", new[] { typeof(Vector3), typeof(float), t, typeof(bool) });
                var m2 = AccessTools.Method(typeof(VailActor), "GetClosestValidStimuliInRange", new[] { typeof(Vector3), typeof(float), t });
                var m3 = AccessTools.Method(typeof(Robby), "IsStimuliInRange", new[] { typeof(string), typeof(float) });
                if (m1 != null) h.Patch(m1, prefix: new HarmonyMethod(typeof(HolderRangePatch), nameof(ActorInRangePrefix)));
                if (m2 != null) h.Patch(m2, prefix: new HarmonyMethod(typeof(HolderRangePatch), nameof(ActorClosestPrefix)));
                if (m3 != null) h.Patch(m3, prefix: new HarmonyMethod(typeof(HolderRangePatch), nameof(RobbyInRangePrefix)));
                RLog.Msg($"{CharacterReplacement.Tag} Holder search range patch active ({m1 != null}/{m2 != null}/{m3 != null})");
            }
            catch (Exception e)
            {
                RLog.Error($"{CharacterReplacement.Tag} Holder range patch failed: {e.Message}");
            }
        }

        private static float Mul => Config.HolderRangeMultiplier.Value;

        /// <summary>Short distances are "have I arrived / am I close enough to act" checks and stay untouched,
        /// only real search ranges are scaled.</summary>
        private const float MinSearchRange = 10f;

        private static readonly System.Collections.Generic.Dictionary<IntPtr, bool> RobbyCache = new();

        private static bool IsRobbyActor(VailActor actor)
        {
            IntPtr p = actor.Pointer;
            if (RobbyCache.TryGetValue(p, out bool r)) return r;
            var id = actor.TypeId;
            r = id == CharacterReplacement.RobbyType || id == CharacterReplacement.PlayerRobbyType;
            if (RobbyCache.Count > 5000) RobbyCache.Clear();
            RobbyCache[p] = r;
            return r;
        }

        /// <summary>Holders, log sleds, build sites (blueprints/shelters), player structures (repair, sharpen), maintain areas.</summary>
        private static bool IsHolderType(Il2CppSystem.Type type)
        {
            if (type == null) return false;
            try
            {
                if (TypeCache.TryGetValue(type.Pointer, out bool cached)) return cached;
                _structureTypes ??= new[]
                {
                    Il2CppType.Of<ItemHolderStimuli>(),
                    Il2CppType.Of<BuildActionStimuli>(),
                    Il2CppType.Of<PlayerStructureStimuli>(),
                    Il2CppType.Of<StructureSharpenStimuli>(),
                    Il2CppType.Of<RobbyRepairStimuli>(),
                    Il2CppType.Of<RobbyMaintainAreaStimuli>(),
                };
                bool r = false;
                foreach (var t in _structureTypes)
                {
                    if (t != null && (type.Pointer == t.Pointer || type.IsSubclassOf(t))) { r = true; break; }
                }
                TypeCache[type.Pointer] = r;
                return r;
            }
            catch { return false; }
        }

        private static void ActorInRangePrefix(VailActor __instance, ref float maxDistance, Il2CppSystem.Type stimuliType)
        {
            long t0 = Perf.Now;
            try { ActorInRange(__instance, ref maxDistance, stimuliType); }
            finally { Perf.Add(ref Perf.HolderCalls, ref Perf.HolderTicks, t0); }
        }

        private static void ActorInRange(VailActor __instance, ref float maxDistance, Il2CppSystem.Type stimuliType)
        {
            if (Math.Abs(Mul - 1f) < 0.01f || maxDistance < MinSearchRange || !IsRobbyActor(__instance) || !IsHolderType(stimuliType)) return;
            if (!_reportedActor) { _reportedActor = true; RLog.Msg($"{CharacterReplacement.Tag} Structure check ({stimuliType?.Name}) range {maxDistance} -> {maxDistance * Mul}"); }
            maxDistance *= Mul;
        }

        private static void ActorClosestPrefix(VailActor __instance, ref float maxDistance, Il2CppSystem.Type stimuliType)
        {
            long t0 = Perf.Now;
            try { ActorClosest(__instance, ref maxDistance, stimuliType); }
            finally { Perf.Add(ref Perf.HolderCalls, ref Perf.HolderTicks, t0); }
        }

        private static void ActorClosest(VailActor __instance, ref float maxDistance, Il2CppSystem.Type stimuliType)
        {
            if (Math.Abs(Mul - 1f) < 0.01f || maxDistance < MinSearchRange || !IsRobbyActor(__instance) || !IsHolderType(stimuliType)) return;
            if (!_reportedClosest) { _reportedClosest = true; RLog.Msg($"{CharacterReplacement.Tag} Structure search ({stimuliType?.Name}) range {maxDistance} -> {maxDistance * Mul}"); }
            maxDistance *= Mul;
        }

        private static void RobbyInRangePrefix(string stimuliTypeStr, ref float maxRange)
        {
            if (Math.Abs(Mul - 1f) < 0.01f || maxRange < MinSearchRange || string.IsNullOrEmpty(stimuliTypeStr)) return;
            bool match = false;
            foreach (var w in StructureWords)
            {
                if (stimuliTypeStr.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
            }
            if (!match) return;
            if (!_reportedRobby) { _reportedRobby = true; RLog.Msg($"{CharacterReplacement.Tag} Structure ({stimuliTypeStr}) range {maxRange} -> {maxRange * Mul}"); }
            maxRange *= Mul;
        }
    }
}
