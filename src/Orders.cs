using System;
using System.Collections.Generic;
using System.Linq;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using Sons.Multiplayer;
using SonsSdk;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Chat commands that give the same order to every soldier at once (build, maintain base, clear area,
    /// follow, stay, get items). Uses the vanilla order system, exactly like the notepad in the game.
    /// </summary>
    internal static class Orders
    {
        internal static RobbyOrderData Data()
        {
            foreach (VailActorTypeId type in new[] { CharacterReplacement.PlayerRobbyType, CharacterReplacement.RobbyType })
            {
                try
                {
                    VailActor prefab = ActorTools.GetPrefab(type);
                    Robby robby = prefab ? prefab.GetComponentInChildren<Robby>(true) : null;
                    RobbyOrderData data = robby ? robby._orderData : null;
                    if (data) return data;
                }
                catch { }
            }
            return null;
        }

        public static List<string> ListNames(Robby.OrderType type)
        {
            var result = new List<string>();
            RobbyOrderData data = Data();
            if (!data) return result;
            var list = OrdersOf(data, type);
            if (list == null) return result;
            for (int i = 0; i < list.Count; i++) result.Add($"{i}={Name(list[i])}");
            return result;
        }

        private static Il2CppSystem.Collections.Generic.List<Robby.RobbyOrder> AsBase<T>(Il2CppSystem.Collections.Generic.List<T> list) where T : Robby.RobbyOrder
        {
            if (list == null) return null;
            var res = new Il2CppSystem.Collections.Generic.List<Robby.RobbyOrder>();
            for (int i = 0; i < list.Count; i++) res.Add(list[i]);
            return res;
        }

        internal static Il2CppSystem.Collections.Generic.List<Robby.RobbyOrder> OrdersOf(RobbyOrderData data, Robby.OrderType type)
        {
            switch (type)
            {
                case Robby.OrderType.Build: return AsBase(data._buildItemOrders);
                case Robby.OrderType.MaintainBase: return AsBase(data._maintainOrders);
                case Robby.OrderType.ClearArea: return AsBase(data._clearAreaOrders);
                case Robby.OrderType.Get: return AsBase(data._getItemOrders);
                case Robby.OrderType.StayHere: return AsBase(data._stayOrders);
                default: return null;
            }
        }

        internal static string Name(Robby.RobbyOrder o)
        {
            if (o == null) return "?";
            try
            {
                var g = o.TryCast<Robby.RobbyGetOrder>();
                if (g != null && !string.IsNullOrEmpty(g._itemType)) return g._itemType;
                var b = o.TryCast<Robby.RobbyBuildOrder>();
                if (b != null && !string.IsNullOrEmpty(b._buildStimuli)) return $"{o._itemName}/{b._buildStimuli}";
            }
            catch { }
            return string.IsNullOrEmpty(o._itemName) ? "?" : o._itemName;
        }

        /// <summary>Resolves "2" or a (partial) name to an index in the order list.</summary>
        public static int Resolve(Robby.OrderType type, string arg, out string name)
        {
            name = "?";
            RobbyOrderData data = Data();
            var list = data ? OrdersOf(data, type) : null;
            if (list == null || list.Count == 0) return -1;
            if (string.IsNullOrEmpty(arg)) { name = Name(list[0]); return 0; }
            if (int.TryParse(arg, out int idx) && idx >= 0 && idx < list.Count) { name = Name(list[idx]); return idx; }
            for (int i = 0; i < list.Count; i++)
            {
                string n = Name(list[i]);
                if (n.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0) { name = n; return i; }
            }
            return -1;
        }

        /// <summary>Gives one order to every living, standing soldier. Returns how many got it.</summary>
        public static int GiveToAll(Robby.OrderType type, int subOrder, int subOrder2, Transform fromPlayer, out int skipped)
        {
            int given = 0;
            skipped = 0;
            foreach (var (actor, _) in SoldierRegistry.All())
            {
                try
                {
                    if (!actor || !actor.gameObject.activeInHierarchy || actor.IsDead()) { skipped++; continue; }
                    Robby robby = actor.GetComponentInChildren<Robby>(true);
                    if (!robby) { skipped++; continue; }
                    if (robby._injuredState.ToString() != "None") { skipped++; continue; } // still lying injured
                    robby.OnGiveOrderServer(type, subOrder, subOrder2, fromPlayer);
                    given++;
                    RLog.Msg($"{CharacterReplacement.Tag} Order to {actor.name}: {type} #{subOrder}/{subOrder2}");
                }
                catch (Exception e)
                {
                    skipped++;
                    RLog.Warning($"{CharacterReplacement.Tag} Order to {(actor ? actor.name : "?")} failed: {e.Message}");
                }
            }
            return given;
        }
    }
}
