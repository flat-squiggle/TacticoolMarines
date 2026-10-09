using System;
using System.Collections.Generic;
using HarmonyLib;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Dedicated server only: the player body of PlayerRobby (head, held item renderers) is not loaded on a
    /// server, which makes facial expressions and held-item checks throw every frame for our soldiers.
    /// These prefixes skip those purely visual updates for our soldiers only.
    /// </summary>
    internal static class ServerPatches
    {
        private static readonly HashSet<IntPtr> Attentions = new();
        private static readonly HashSet<IntPtr> Robbies = new();
        private static bool _patched;

        public static void Apply()
        {
            if (_patched) return;
            _patched = true;
            var harmony = new HarmonyLib.Harmony("livingsoldiers.server");
            TryPatch(harmony, AccessTools.Method(typeof(Attention), "UpdateExpressions"), nameof(SkipAttention));
            TryPatch(harmony, AccessTools.Method(typeof(Robby), "ServerCheckHeldItemVisibility"), nameof(SkipRobby));
        }

        private static void TryPatch(HarmonyLib.Harmony harmony, System.Reflection.MethodInfo target, string prefix)
        {
            try
            {
                if (target == null) { RLog.Warning($"{CharacterReplacement.Tag} Patch target for {prefix} not found"); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ServerPatches), prefix));
                RLog.Msg($"{CharacterReplacement.Tag} Patched {target.DeclaringType?.Name}.{target.Name}");
            }
            catch (Exception e)
            {
                RLog.Error($"{CharacterReplacement.Tag} Patching {prefix} failed: {e.Message}");
            }
        }

        public static void Register(VailActor actor)
        {
            if (!actor) return;
            foreach (var a in actor.GetComponentsInChildren<Attention>(true)) Attentions.Add(a.Pointer);
            foreach (var r in actor.GetComponentsInChildren<Robby>(true)) Robbies.Add(r.Pointer);
        }

        public static void Clear()
        {
            Attentions.Clear();
            Robbies.Clear();
        }

        private static bool SkipAttention(Attention __instance)
        {
            long t0 = Perf.Now;
            bool run = !Attentions.Contains(__instance.Pointer);
            Perf.Add(ref Perf.AttnCalls, ref Perf.AttnTicks, t0);
            return run;
        }

        private static bool SkipRobby(Robby __instance)
        {
            long t0 = Perf.Now;
            bool run = !Robbies.Contains(__instance.Pointer);
            Perf.Add(ref Perf.RobbyCalls, ref Perf.RobbyTicks, t0);
            return run;
        }
    }
}
