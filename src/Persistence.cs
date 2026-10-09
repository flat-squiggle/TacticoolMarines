using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RedLoader;
using Sons.Gameplay;
using SonsSdk;
using UnityEngine;

namespace CharacterReplacement
{
    public class SoldierSaveEntry
    {
        public string Name;
        public int Kind;
        public float X, Y, Z;
        public bool Revived;
        public bool Dead;
    }

    public class SoldierSaveData
    {
        public int Version = 1;
        public List<SoldierSaveEntry> Soldiers = new();
    }

    /// <summary>Stores soldier positions / state in the savegame (RedLoader mod save data).</summary>
    public class SoldierPersistence : ICustomSaveable<SoldierSaveData>
    {
        public static readonly SoldierPersistence Instance = new();
        private static readonly Dictionary<string, SoldierSaveEntry> Loaded = new();
        private static int _debugCounter;

        public string Name => "LivingSoldiers"; // kept from before the rename to Tacticool Marines: existing saves
        public bool IncludeInPlayerSave => false;

        public static void Register()
        {
            try { SonsSaveTools.Register(Instance); }
            catch (Exception e) { RLog.Error($"{CharacterReplacement.Tag} Registering save data failed: {e}"); }
        }

        public static void ClearLoaded()
        {
            Loaded.Clear();
            _debugCounter = 0;
        }

        public static bool TryGet(string name, out SoldierSaveEntry entry) => Loaded.TryGetValue(name, out entry);

        public static IEnumerable<SoldierSaveEntry> Extras() => Loaded.Values.Where(e => e.Name.StartsWith("Debug_")).ToList();

        public static string NextDebugName()
        {
            string n;
            do { n = "Debug_" + (++_debugCounter); } while (Loaded.ContainsKey(n) || SoldierSpots.Has(n));
            return n;
        }

        public SoldierSaveData Save()
        {
            var data = new SoldierSaveData();
            if (!CharacterReplacement.SetupDone)
            {
                // Autosave right after loading, before the soldiers exist: keep what was loaded.
                data.Soldiers.AddRange(Loaded.Values);
                return data;
            }
            foreach (var spot in SoldierSpots.All())
            {
                bool alive = spot.Actor;
                Vector3 p = alive ? spot.Actor.transform.position : spot.LastPosition;
                bool revived = spot.Extra || CharacterReplacement.IsRevived(spot.Name);
                data.Soldiers.Add(new SoldierSaveEntry
                {
                    Name = spot.Name, Kind = (int)spot.Kind, X = p.x, Y = p.y, Z = p.z,
                    Revived = revived,
                    Dead = spot.Dead || (!alive && revived),
                });
            }
            RLog.Msg($"{CharacterReplacement.Tag} Saved {data.Soldiers.Count} soldiers");
            return data;
        }

        public void Load(SoldierSaveData obj)
        {
            Loaded.Clear();
            if (obj?.Soldiers == null) return;
            foreach (var e in obj.Soldiers)
            {
                if (!string.IsNullOrEmpty(e?.Name)) Loaded[e.Name] = e;
            }
            RLog.Msg($"{CharacterReplacement.Tag} Loaded {Loaded.Count} soldiers from save ({Loaded.Values.Count(e => e.Revived && !e.Dead)} revived, {Loaded.Values.Count(e => e.Dead)} dead)");
        }
    }

    /// <summary>
    /// Kelvin's "get logs" order searches trees with a radius hard-coded in native code.
    /// This prefix scales that radius (only while a "get" order runs, clear-area orders keep their own radius).
    /// </summary>
    internal static class TreeRangePatch
    {
        private static bool _patched;
        private static readonly HashSet<float> Reported = new();

        public static void Apply()
        {
            if (_patched) return;
            _patched = true;
            try
            {
                var h = new HarmonyLib.Harmony("livingsoldiers.treerange");
                var m1 = AccessTools.Method(typeof(Robby), "FindChopTreeStimuli");
                var m2 = AccessTools.Method(typeof(Robby), "LogPickupsNearby");
                if (m1 != null) h.Patch(m1, prefix: new HarmonyMethod(typeof(TreeRangePatch), nameof(ChopPrefix)));
                if (m2 != null) h.Patch(m2, prefix: new HarmonyMethod(typeof(TreeRangePatch), nameof(PickupPrefix)));
                RLog.Msg($"{CharacterReplacement.Tag} Tree search range patch active ({(m1 != null)}/{(m2 != null)})");
            }
            catch (Exception e)
            {
                RLog.Error($"{CharacterReplacement.Tag} Tree range patch failed: {e.Message}");
            }
        }

        private static bool IsGetOrder(Robby robby)
        {
            try
            {
                var order = robby._activeOrder?._order;
                return order != null && order.TryCast<Robby.RobbyGetOrder>() != null;
            }
            catch { return false; }
        }

        private static void ChopPrefix(Robby __instance, ref float checkRadius)
        {
            long t0 = Perf.Now;
            try { Chop(__instance, ref checkRadius); }
            finally { Perf.Add(ref Perf.TreeCalls, ref Perf.TreeTicks, t0); }
        }

        private static void Chop(Robby __instance, ref float checkRadius)
        {
            float mul = Config.OrderRangeMultiplier.Value;
            if (Math.Abs(mul - 1f) < 0.01f || !IsGetOrder(__instance)) return;
            if (Reported.Add(checkRadius)) RLog.Msg($"{CharacterReplacement.Tag} Tree search radius {checkRadius} -> {checkRadius * mul}");
            checkRadius *= mul;
        }

        private static void PickupPrefix(Robby __instance, ref float radius)
        {
            long t0 = Perf.Now;
            try { Pickup(__instance, ref radius); }
            finally { Perf.Add(ref Perf.TreeCalls, ref Perf.TreeTicks, t0); }
        }

        private static void Pickup(Robby __instance, ref float radius)
        {
            float mul = Config.OrderRangeMultiplier.Value;
            if (Math.Abs(mul - 1f) < 0.01f || !IsGetOrder(__instance)) return;
            radius *= mul;
        }
    }
}
