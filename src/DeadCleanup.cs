using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using SonsSdk;
using SonsSdk.Networking;
using TheForest.Utils;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Removes the bodies of killed soldiers after a grace period (so dropped items can still be picked up),
    /// marks them as dead in the save so they are not respawned. Only touches soldiers spawned by this mod,
    /// never the story Kelvin or Virginia.
    /// </summary>
    internal static class DeadCleanup
    {
        private static float _nextTick;
        private static readonly Dictionary<string, float> DeadSince = new();
        public static int RemovedTotal { get; private set; }

        public static void Tick(bool force = false)
        {
            if (!NetUtils.IsServer && NetUtils.IsMultiplayer) return;
            float now = Time.realtimeSinceStartup;
            if (!force && now < _nextTick) return;
            _nextTick = now + 10f;
            float delay = Mathf.Max(0f, Config.RemoveDeadAfterSeconds.Value);
            if (!force && !Config.RemoveDeadSoldiers.Value) return;

            foreach (var spot in SoldierSpots.All())
            {
                VailActor actor = spot.Actor;
                if (!actor) { DeadSince.Remove(spot.Name); continue; }
                // Safety: only soldiers that were standing (revived or extra). A never-revived soldier lies injured,
                // which must not be mistaken for dead.
                if (!spot.Extra && !CharacterReplacement.IsRevived(spot.Name)) continue;
                bool dead;
                try { dead = actor.IsDead(); } catch { continue; }
                if (!dead) { DeadSince.Remove(spot.Name); continue; }

                if (!DeadSince.TryGetValue(spot.Name, out float since))
                {
                    DeadSince[spot.Name] = now;
                    spot.Dead = true; // never respawn a killed soldier
                    RLog.Msg($"{CharacterReplacement.Tag} {spot.Name} died, body will be removed in {delay:0}s");
                    if (!force) continue;
                    since = now;
                }
                if (!force && now - since < delay) continue;
                Remove(spot);
            }
        }

        /// <summary>Removes a soldier on command (alive or dead) and marks it so it never respawns.</summary>
        public static void RemoveSpot(SoldierSpots.Spot spot, string reason)
        {
            if (spot == null) return;
            RLog.Msg($"{CharacterReplacement.Tag} {spot.Name}: {reason}");
            Remove(spot);
        }

        private static void Remove(SoldierSpots.Spot spot)
        {
            VailActor actor = spot.Actor;
            if (!actor) return;
            string name = actor.name;
            try
            {
                GameObject deadObj = null;
                try { deadObj = actor._deadObject; } catch { }
                VailActorManager.RemoveActor(actor);
                if (deadObj) deadObj.TryDestroy();
            }
            catch (Exception e)
            {
                RLog.Warning($"{CharacterReplacement.Tag} RemoveActor failed for {name}: {e.Message}, destroying directly");
            }
            // Fallback in case the manager did not take it (still active next frame is fine too, checked on next tick)
            try
            {
                if (actor && actor.gameObject.activeInHierarchy)
                {
                    BoltEntity be = actor.GetComponentInChildren<BoltEntity>(true);
                    if (be && be.isAttached && BoltNetwork.isServer) BoltNetwork.Destroy(be.gameObject);
                    else actor.gameObject.TryDestroy();
                }
            }
            catch (Exception e)
            {
                RLog.Warning($"{CharacterReplacement.Tag} Destroying {name} failed: {e.Message}");
            }
            spot.Actor = null;
            spot.Dead = true;
            DeadSince.Remove(spot.Name);
            RemovedTotal++;
            RLog.Msg($"{CharacterReplacement.Tag} Removed body of dead soldier {spot.Name}");
        }

        public static void Clear() => DeadSince.Clear();

        /// <summary>Counts for chat output: (dead bodies still in world, removed this session).</summary>
        public static (int bodies, int removed) Status()
        {
            int bodies = 0;
            foreach (var spot in SoldierSpots.All())
            {
                try { if (spot.Actor && spot.Actor.IsDead()) bodies++; } catch { }
            }
            return (bodies, RemovedTotal);
        }
    }
}
