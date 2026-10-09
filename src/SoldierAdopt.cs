using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RedLoader;
using Sons.Ai.Vail;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// A soldier that is far away from every player lives only in the world simulation. The mod must not
    /// spawn a replacement for him - instead it notes his id here and picks him up again at the moment the
    /// game turns him back into a real actor (when a player comes close).
    /// </summary>
    internal static class SoldierAdopt
    {
        private static bool _patched;
        private static readonly Dictionary<int, (string name, Vector3 home)> Pending = new();

        public static int PendingCount => Pending.Count;

        public static void Expect(int uniqueId, string name, Vector3 home)
        {
            if (uniqueId <= 0 || string.IsNullOrEmpty(name)) return;
            Pending[uniqueId] = (name, home);
        }

        public static void Clear() => Pending.Clear();

        public static void Apply()
        {
            if (_patched) return;
            _patched = true;
            try
            {
                var h = new HarmonyLib.Harmony("livingsoldiers.adopt");
                int n = 0;
                foreach (var m in typeof(VailWorldSimulation).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "ConvertToRealActor") continue;
                    try { h.Patch(m, postfix: new HarmonyMethod(typeof(SoldierAdopt), nameof(ConvertedPostfix))); n++; }
                    catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} Adopt patch skipped one overload: {e.Message}"); }
                }
                RLog.Msg($"{CharacterReplacement.Tag} Adopt hook active ({n} overloads)");
            }
            catch (Exception e)
            {
                RLog.Error($"{CharacterReplacement.Tag} Adopt patch failed: {e.Message}");
            }
        }

        private static void ConvertedPostfix(WorldSimActor __0, VailActor __result)
        {
            if (Pending.Count == 0 || __result == null) return;
            try
            {
                int id = __result.UniqueId;
                if (id <= 0 && __0 != null) id = __0.UniqueId;
                if (!Pending.TryGetValue(id, out var info)) return;
                Pending.Remove(id);
                CharacterReplacement.AdoptExisting(info.name, info.home, __result);
            }
            catch (Exception e)
            {
                RLog.Warning($"{CharacterReplacement.Tag} Adopting a soldier failed: {e.Message}");
            }
        }
    }
}
