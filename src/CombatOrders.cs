using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using SonsSdk;
using TheForest.Utils;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// What a soldier was doing before a fight (following a player, a work order) and getting him back to it after.
    /// His own AI is switched off for the fight; switching it on again does not always bring the order back, so the
    /// order is noted when the fight starts and given again if it is gone afterwards. Also the leash: a following
    /// soldier fights near the one he follows instead of running after every enemy.
    /// </summary>
    internal static class CombatOrders
    {
        internal sealed class Snap
        {
            public bool Following;
            public Transform Follow;
            public Robby.OrderType? Type;
            public int Index = -1;
            public int Drop;
            public string Name;
            public Vector3 Where;
        }

        private static readonly Dictionary<IntPtr, Robby> Robbies = new();

        internal static Robby RobbyOf(VailActor a)
        {
            if (!a) return null;
            if (Robbies.TryGetValue(a.Pointer, out var r) && r) return r;
            try { r = a.GetComponentInChildren<Robby>(true); } catch { r = null; }
            if (r) Robbies[a.Pointer] = r;
            return r;
        }

        /// <summary>True if he follows someone (player order Follow, or his follow target set without a work order).</summary>
        internal static bool Following(VailActor a, out Transform target)
        {
            target = null;
            Robby r = RobbyOf(a);
            if (!r) return false;
            try
            {
                target = r._followTransform;
                bool hold = NoBreaks.PlayerHold.TryGetValue(r.Pointer, out var h) && h == Robby.OrderType.Follow;
                if (hold && !target) target = LocalPlayer.Transform;
                return target && (hold || r._activeOrder == null);
            }
            catch { return false; }
        }

        internal static Snap Take(VailActor a)
        {
            var s = new Snap { Where = a ? a.transform.position : Vector3.zero };
            Robby r = RobbyOf(a);
            if (!r) return s;
            try
            {
                s.Following = Following(a, out s.Follow);
                var running = r._activeOrder;
                var o = running != null ? running._order : null;
                if (o != null)
                {
                    s.Type = TypeOf(o);
                    s.Drop = (int)running._dropLocationType;
                    s.Name = Orders.Name(o);
                    var data = r._orderData;
                    var list = data && s.Type.HasValue ? Orders.OrdersOf(data, s.Type.Value) : null;
                    if (list != null) for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].Pointer == o.Pointer) { s.Index = i; break; }
                }
            }
            catch (Exception e) { CombatAI.DebugOnce("ordersnap" + e.Message, $"Remembering the order: {e.Message}"); }
            return s;
        }

        private static Robby.OrderType? TypeOf(Robby.RobbyOrder o)
        {
            if (o.TryCast<Robby.RobbyGetOrder>() != null) return Robby.OrderType.Get;
            if (o.TryCast<Robby.RobbyBuildOrder>() != null) return Robby.OrderType.Build;
            if (o.TryCast<Robby.RobbyClearOrder>() != null) return Robby.OrderType.ClearArea;
            if (o.TryCast<Robby.RobbyMaintainOrder>() != null) return Robby.OrderType.MaintainBase;
            if (o.TryCast<Robby.RobbyStayOrder>() != null) return Robby.OrderType.StayHere;
            return null;
        }

        /// <summary>
        /// Where his fight is tied to: the one he follows (live), else where the fight started. radius = how far from
        /// there he may go.
        /// </summary>
        internal static Vector3 Anchor(VailActor a, Snap s, out float radius, out bool follow)
        {
            follow = false;
            radius = Config.CombatLeash.Value;
            if (s == null) return a ? a.transform.position : Vector3.zero;
            if (s.Following && Config.CombatFollowLeash.Value > 0f)
            {
                Transform t = s.Follow;
                if (!t) Following(a, out t);
                if (t)
                {
                    follow = true;
                    radius = Config.CombatFollowLeash.Value;
                    return t.position;
                }
            }
            if (s.Type == Robby.OrderType.StayHere) radius = Mathf.Min(radius, Mathf.Max(8f, Config.CombatFollowLeash.Value));
            return s.Where;
        }

        /// <summary>After the fight: give the order again if his own AI lost it.</summary>
        internal static void Restore(VailActor a, Snap s)
        {
            if (!a || s == null || (!s.Following && !s.Type.HasValue)) return;
            if (!Config.CombatResumeOrders.Value) return;
            RestoreLater(a, s).RunCoro();
        }

        private static System.Collections.IEnumerator RestoreLater(VailActor a, Snap s)
        {
            // Let his own AI start up again first; it may well carry on by itself.
            float until = Time.time + 1.0f;
            while (Time.time < until) yield return null;
            if (!a || CombatAI.IsEngaged(a)) yield break;
            Robby r = RobbyOf(a);
            if (!r) yield break;
            try
            {
                if (r._injuredState.ToString() != "None") yield break;
                Transform from = s.Follow ? s.Follow : LocalPlayer.Transform;
                if (s.Following)
                {
                    if (Following(a, out var t) && t && r._followTransform) { CombatAI.Debug($"{a.name} keeps following after the fight ({Describe(a)})"); yield break; }
                    if (!from) yield break;
                    Give(r, Robby.OrderType.Follow, 0, 0, from);
                    CombatAI.Debug($"{a.name} follows again after the fight ({Describe(a)})");
                }
                else if (s.Type.HasValue)
                {
                    var running = r._activeOrder;
                    if (running != null && running._order != null) { CombatAI.Debug($"{a.name} carries on after the fight: {Describe(a)}"); yield break; }
                    if (s.Index < 0 || !from) yield break;
                    int sub2 = s.Type == Robby.OrderType.Get ? s.Drop : 0;
                    Give(r, s.Type.Value, s.Index, sub2, from);
                    CombatAI.Debug($"{a.name} resumes his work order: {s.Type} {s.Name} ({Describe(a)})");
                }
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [AI] restoring the order of {a.name}: {e.Message}"); }
        }

        private static void Give(Robby r, Robby.OrderType type, int sub, int sub2, Transform from)
        {
            // Our own order: the player's hold (follow / stay) stays as it was.
            NoBreaks.GivingAutoOrder = true;
            try { r.OnGiveOrderServer(type, sub, sub2, from); }
            finally { NoBreaks.GivingAutoOrder = false; }
        }

        internal static string Describe(VailActor a)
        {
            Robby r = RobbyOf(a);
            if (!r) return "no Robby";
            try
            {
                string hold = NoBreaks.PlayerHold.TryGetValue(r.Pointer, out var h) ? h.ToString() : "-";
                var running = r._activeOrder;
                string act = running != null && running._order != null ? $"{TypeOf(running._order)}:{Orders.Name(running._order)}" : "-";
                string follow = r._followTransform ? r._followTransform.name : "-";
                string ov = "-";
                try { ov = r._activeOverride ? r._activeOverride.name : "-"; } catch { }
                string dist = LocalPlayer.Transform ? $"{Vector3.Distance(LocalPlayer.Transform.position, a.transform.position):0} m from the player" : "";
                return $"hold {hold}, order {act}, follows {follow}, override {ov}, waiting {r._waitingForOrders}, fighting {(CombatAI.IsEngaged(a) ? "yes" : "no")}, {dist}";
            }
            catch (Exception e) { return e.Message; }
        }

        // ------------------------------------------------------------------ follow watchdog

        // Playtest 1.30: after a fight the follow order and its override were still set, yet the soldier stood still
        // (26 m behind the player a minute later). So the state is not trusted: a soldier the player told to follow
        // who is far from him and not getting closer gets the follow order again.
        private static readonly Dictionary<IntPtr, (float dist, float since, float lastGiven)> Watched = new();
        private static float _nextWatch;

        internal static void Watch(float now)
        {
            if (now < _nextWatch || !Config.CombatResumeOrders.Value) return;
            _nextWatch = now + 1f;
            Transform player = LocalPlayer.Transform;
            if (!player) return;
            foreach (var (a, _) in SoldierRegistry.All())
            {
                if (!a) continue;
                IntPtr key = a.Pointer;
                Robby r = RobbyOf(a);
                bool hold = r && NoBreaks.PlayerHold.TryGetValue(r.Pointer, out var h) && h == Robby.OrderType.Follow;
                if (!hold || !a.gameObject.activeInHierarchy || CombatAI.IsEngaged(a) || CaveFollow.IsInside(a)) { Watched.Remove(key); continue; }
                try { if (r._injuredState.ToString() != "None") { Watched.Remove(key); continue; } } catch { }
                float d = Vector3.Distance(player.position, a.transform.position);
                Watched.TryGetValue(key, out var w);
                // Close enough, too far to be a stuck follow (boat, glider, teleport), or getting closer: fine.
                if (d < 12f || d > 80f || (w.since > 0f && d < w.dist - 1f) || w.since == 0f)
                {
                    Watched[key] = (d, now, w.lastGiven);
                    continue;
                }
                if (now - w.since < 5f || now - w.lastGiven < 15f) continue;
                try
                {
                    Give(r, Robby.OrderType.Follow, 0, 0, player);
                    CombatAI.Debug($"{a.name} is not following ({d:0} m from the player, no closer for {now - w.since:0}s) - follow order given again");
                }
                catch (Exception e) { CombatAI.DebugOnce("followwatch" + e.Message, $"Giving the follow order again: {e.Message}"); }
                Watched[key] = (d, now, now);
            }
        }

        internal static void Clear() { Robbies.Clear(); Watched.Clear(); }
    }
}
