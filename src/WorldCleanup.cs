using System;
using System.Collections.Generic;
using System.Linq;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using GameState = Sons.Save.GameState;
using SonsSdk;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// The game keeps actors that are far away from every player as "world simulation actors" - they are not
    /// real objects in the scene, but they are saved and come back. Looking only at the real actors therefore
    /// gives a wrong picture: a soldier can be perfectly alive and still not be findable that way.
    ///
    /// That is what produced duplicates: on every server start the mod looked for its soldiers among the real
    /// actors, found none (nobody was near them yet) and spawned a fresh set on top of the saved ones.
    /// This class provides the honest view over BOTH lists and can clean up the surplus.
    /// </summary>
    internal static class WorldCleanup
    {
        public static bool TrySim(out VailWorldSimulation sim)
        {
            sim = null;
            try { return VailWorldSimulation.TryGetInstance(out sim) && sim; }
            catch { return false; }
        }

        private static bool IsCompanionType(VailActorTypeId t) =>
            t == CharacterReplacement.RobbyType || t == CharacterReplacement.PlayerRobbyType;

        /// <summary>All Kelvin-type actors known to the world simulation (real ones included).</summary>
        public static List<WorldSimActor> AllCompanions()
        {
            var list = new List<WorldSimActor>();
            if (!TrySim(out var sim)) return list;
            try
            {
                var all = sim.GetAllActors();
                if (all == null) return list;
                for (int i = 0; i < all.Length; i++)
                {
                    var a = all[i];
                    if (a == null) continue;
                    try
                    {
                        if (a._removed) continue;
                        if (!IsCompanionType(a.TypeId)) continue;
                        list.Add(a);
                    }
                    catch { }
                }
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} World scan failed: {e.Message}"); }
            return list;
        }

        public static bool SimActorExists(int uniqueId)
        {
            if (uniqueId <= 0) return false;
            foreach (var a in AllCompanions())
            {
                try { if (a.UniqueId == uniqueId) return true; } catch { }
            }
            return false;
        }

        /// <summary>Unique ids this mod considers its own (one per soldier spot, from the savegame).</summary>
        public static HashSet<int> KnownIds()
        {
            var keep = new HashSet<int>();
            foreach (var spot in SoldierSpots.All())
            {
                try
                {
                    var d = GameState.GetOrCreate(spot.Name + "_actorid", -1, true);
                    if (d != null && d.SaveValue > 0) keep.Add(d.SaveValue);
                }
                catch { }
                try { if (spot.Actor) keep.Add(spot.Actor.UniqueId); } catch { }
            }
            return keep;
        }

        /// <summary>
        /// The story Kelvin exists from the moment the world is created, so among all Kelvin-type actors he
        /// carries the lowest unique id - ids are handed out in order. Used to never delete him by accident.
        /// </summary>
        public static int StoryKelvinId()
        {
            int best = int.MaxValue;
            foreach (var a in AllCompanions())
            {
                try { if (a.UniqueId > 0 && a.UniqueId < best) best = a.UniqueId; } catch { }
            }
            return best == int.MaxValue ? -1 : best;
        }

        public static (int total, int known, int surplus) Count()
        {
            var all = AllCompanions();
            var known = KnownIds();
            int kelvin = StoryKelvinId();
            int k = 0, s = 0;
            foreach (var a in all)
            {
                int id;
                try { id = a.UniqueId; } catch { continue; }
                if (id == kelvin) continue;
                if (known.Contains(id)) k++; else s++;
            }
            return (all.Count, k, s);
        }

        /// <summary>Removes one world simulation actor together with its real object, if it has one.</summary>
        public static bool Remove(WorldSimActor a)
        {
            if (a == null) return false;
            try
            {
                VailActor real = null;
                try { real = VailActorManager.FindActiveActor(a); } catch { }
                if (real)
                {
                    try { VailActorManager.RemoveActor(real); }
                    catch { try { real.gameObject.TryDestroy(); } catch { } }
                }
                if (TrySim(out var sim)) sim.RemoveActor(a);
                else a.SetRemoved();
                return true;
            }
            catch (Exception e)
            {
                RLog.Warning($"{CharacterReplacement.Tag} Removing world actor failed: {e.Message}");
                return false;
            }
        }

        /// <summary>Removes the duplicates: every Kelvin-type actor that is neither the story Kelvin nor a soldier this mod knows.</summary>
        public static int RemoveSurplus()
        {
            var known = KnownIds();
            int kelvin = StoryKelvinId();
            int n = 0;
            foreach (var a in AllCompanions())
            {
                int id;
                try { id = a.UniqueId; } catch { continue; }
                if (id == kelvin || known.Contains(id)) continue;
                if (Remove(a)) n++;
            }
            if (n > 0) RLog.Msg($"{CharacterReplacement.Tag} Removed {n} surplus Kelvin/soldier actors from the world simulation");
            return n;
        }

        /// <summary>Removes every soldier, the story Kelvin always stays.</summary>
        public static int RemoveAllCompanions()
        {
            int kelvin = StoryKelvinId();
            int n = 0;
            foreach (var a in AllCompanions())
            {
                int id;
                try { id = a.UniqueId; } catch { continue; }
                if (id == kelvin) continue;
                if (Remove(a)) n++;
            }
            foreach (var spot in SoldierSpots.All())
            {
                spot.Actor = null;
                spot.Dead = true;
                try { GameState.GetOrCreate(spot.Name + "_actorid", -1, true).SetValue(-1); } catch { }
            }
            RLog.Msg($"{CharacterReplacement.Tag} Removed all {n} soldiers, story Kelvin (id {kelvin}) kept");
            return n;
        }
    }
}
