using System;
using System.Collections.Generic;
using HarmonyLib;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using Sons.StatSystem;
using SonsSdk;
using SonsSdk.Networking;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// "Keep working": Kelvin's get-orders normally expire after a few minutes, then he takes a break and
    /// restarts later. With no_breaks on, work orders (drop here / fill holder / fill sled) no longer expire,
    /// so he only stops when there is nothing left to do (no items found, holders full). His energy/rest stats
    /// are also kept up while he works. Explicit player orders (follow, stay, take a break) are always respected.
    /// Also tracks the last order a PLAYER gave, so auto jobs never override "follow" or "stay here".
    /// </summary>
    internal static class NoBreaks
    {
        private static bool _patched;
        private static readonly Dictionary<IntPtr, float> OrigExpire = new();
        private static float _nextTick;
        private static bool _reportedExpire, _reportedStats;

        /// <summary>Set while this mod itself gives an order, so it is not mistaken for a player order.</summary>
        internal static bool GivingAutoOrder;

        /// <summary>Robby pointer -> order type the player gave last (Follow/StayHere/TakeBreak/Equip hold auto jobs).</summary>
        internal static readonly Dictionary<IntPtr, Robby.OrderType> PlayerHold = new();

        public static void Apply()
        {
            if (_patched) return;
            _patched = true;
            try
            {
                var h = new HarmonyLib.Harmony("livingsoldiers.nobreaks");
                var m1 = AccessTools.Method(typeof(Robby.RunningRobbyOrder), "IsCompleteByTime");
                if (m1 != null) h.Patch(m1, postfix: new HarmonyMethod(typeof(NoBreaks), nameof(CompleteByTimePostfix)));
                var m2 = AccessTools.Method(typeof(Robby), "OnGiveOrderServer");
                if (m2 != null) h.Patch(m2, postfix: new HarmonyMethod(typeof(NoBreaks), nameof(GiveOrderPostfix)));
                RLog.Msg($"{CharacterReplacement.Tag} No-breaks hooks: expire={m1 != null} orders={m2 != null}");
            }
            catch (Exception e)
            {
                RLog.Error($"{CharacterReplacement.Tag} No-breaks patch failed: {e.Message}");
            }
        }

        private static bool IsWorkDrop(Robby.DropLocationType t) =>
            t == Robby.DropLocationType.DropHere || t == Robby.DropLocationType.FillHolder || t == Robby.DropLocationType.FillLogSled;

        private static void CompleteByTimePostfix(Robby.RunningRobbyOrder __instance, Robby.DropLocationType __1, ref bool __result)
        {
            if (!__result || !Config.NoBreaks.Value || !IsWorkDrop(__1)) return;
            __result = false;
            if (!_reportedExpire)
            {
                _reportedExpire = true;
                string what = "?";
                try { what = OrderName(__instance._order); } catch { }
                RLog.Msg($"{CharacterReplacement.Tag} No-breaks: kept order '{what}' running past its time limit (logged once)");
            }
        }

        private static void GiveOrderPostfix(Robby __instance, Robby.OrderType __0)
        {
            if (GivingAutoOrder) return;
            try
            {
                IntPtr key = __instance.Pointer;
                switch (__0)
                {
                    case Robby.OrderType.Follow:
                    case Robby.OrderType.StayHere:
                    case Robby.OrderType.TakeBreak:
                    case Robby.OrderType.Equip:
                        PlayerHold[key] = __0;
                        break;
                    default:
                        PlayerHold.Remove(key); // a work order releases the hold
                        break;
                }
                RLog.Msg($"{CharacterReplacement.Tag} Player order for {__instance.gameObject.name}: {__0}");
            }
            catch { }
        }

        /// <summary>Order expiry on the shared order data (Kelvin + soldiers). Re-applied when the setting changes.</summary>
        public static void ApplyOrderData()
        {
            var seen = new HashSet<IntPtr>();
            foreach (VailActorTypeId type in new[] { CharacterReplacement.RobbyType, CharacterReplacement.PlayerRobbyType })
            {
                try
                {
                    VailActor prefab = ActorTools.GetPrefab(type);
                    Robby robby = prefab ? prefab.GetComponentInChildren<Robby>(true) : null;
                    RobbyOrderData data = robby ? robby._orderData : null;
                    if (!data || !seen.Add(data.Pointer) || data._getItemOrders == null) continue;
                    foreach (var o in data._getItemOrders)
                    {
                        if (o == null) continue;
                        if (!OrigExpire.TryGetValue(o.Pointer, out float orig)) { orig = o._expireTimeMinutes; OrigExpire[o.Pointer] = orig; }
                        float value = Config.NoBreaks.Value ? Math.Max(orig, 100000f) : orig;
                        if (Math.Abs(o._expireTimeMinutes - value) > 0.01f)
                        {
                            o._expireTimeMinutes = value;
                            RLog.Msg($"{CharacterReplacement.Tag} Order get {o._itemType}: time limit {orig} min -> {(Config.NoBreaks.Value ? "none" : value + " min")} (restartAfterRest={o._restartAfterExpire})");
                        }
                    }
                }
                catch (Exception e)
                {
                    RLog.Warning($"{CharacterReplacement.Tag} No-breaks order data for {type}: {e.Message}");
                }
            }
        }

        /// <summary>Keeps energy/rest of working companions up. Throttled, call often.</summary>
        public static void Tick()
        {
            if (!NetUtils.IsServer && NetUtils.IsMultiplayer) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextTick) return;
            _nextTick = now + 10f;
            if (!Config.NoBreaks.Value) return;
            foreach (var actor in Companions())
            {
                try
                {
                    if (!actor || !actor.gameObject.activeInHierarchy || actor.IsDead()) continue;
                    Robby robby = actor.GetComponentInChildren<Robby>(true);
                    if (!robby || robby._activeOrder == null) continue;
                    var sm = actor.GetStatsManager();
                    if (sm == null) continue;
                    Fill(sm.GetStat<EnergyStat>());
                    Fill(sm.GetStat<RestedStat>());
                    Fill(sm.GetStat<StaminaStat>());
                }
                catch (Exception e)
                {
                    if (!_reportedStats) { _reportedStats = true; RLog.Warning($"{CharacterReplacement.Tag} No-breaks stats: {e.Message}"); }
                }
            }
        }

        private static void Fill(Stat s)
        {
            if (s == null) return;
            float max = s.GetMax();
            if (s._currentValue < max * 0.9f) s.SetCurrentValue(max);
        }

        public static IEnumerable<VailActor> Companions()
        {
            var list = new List<VailActor>();
            try { foreach (var a in ActorTools.GetActors(CharacterReplacement.RobbyType)) if (a) list.Add(a); } catch { }
            try { foreach (var (a, _) in SoldierRegistry.All()) if (a && !list.Contains(a)) list.Add(a); } catch { }
            return list;
        }

        /// <summary>Stat readout for the log (to see what drains while he works).</summary>
        public static string StatLine(VailActor actor)
        {
            try
            {
                var sm = actor.GetStatsManager();
                if (sm == null) return "stats=-";
                return $"energy={Val(sm.GetStat<EnergyStat>())} rested={Val(sm.GetStat<RestedStat>())} stamina={Val(sm.GetStat<StaminaStat>())} health={Val(sm.GetStat<HealthStat>())}";
            }
            catch (Exception e) { return "stats=? " + e.Message; }
        }

        private static string OrderName(Robby.RobbyOrder o)
        {
            if (o == null) return "?";
            var g = o.TryCast<Robby.RobbyGetOrder>();
            if (g != null) return "get " + g._itemType;
            return o._itemName ?? "?";
        }

        private static string Val(Stat s) => s == null ? "-" : $"{s._currentValue:0}/{s.GetMax():0}";

        public static string OrderLine(VailActor actor)
        {
            try
            {
                Robby r = actor.GetComponentInChildren<Robby>(true);
                if (!r) return "order=-";
                var o = r._activeOrder;
                string order = o == null ? "none" : $"{OrderName(o._order)} ({o.MinutesElapsed:0.0} min, drop={o._dropLocationType})";
                string hold = PlayerHold.TryGetValue(r.Pointer, out var h) ? h.ToString() : "-";
                return $"order={order} waiting={r._waitingForOrders} follow={(r._followTransform ? r._followTransform.name : "-")} playerOrder={hold}";
            }
            catch (Exception e) { return "order=? " + e.Message; }
        }
    }
}
