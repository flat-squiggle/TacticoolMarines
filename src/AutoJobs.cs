using System;
using System.Collections.Generic;
using System.Linq;
using RedLoader;
using Il2CppInterop.Runtime;
using Sons.Ai.Vail;
using Sons.Ai.Vail.StimuliTypes;
using Sons.Gameplay;
using Sons.Multiplayer;
using SonsSdk;
using SonsSdk.Networking;
using TheForest.Utils;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Gives idle companions (no active order, not injured, not waiting at the notepad) a job on their own:
    /// the vanilla "get X" order with "fill holder" as drop location, rotating through the configured items.
    /// Runs only where the AI runs (host / dedicated server).
    /// </summary>
    internal static class AutoJobs
    {
        private const int DropFillHolder = 3; // Robby.DropLocationType.FillHolder
        private static readonly Dictionary<IntPtr, float> IdleSince = new();
        private static readonly Dictionary<IntPtr, int> NextJob = new();
        private static readonly Dictionary<IntPtr, float> PausedUntil = new();
        private static readonly Dictionary<IntPtr, int> Failures = new();
        private static readonly Dictionary<IntPtr, float> OrderGivenAt = new();
        private static Il2CppSystem.Collections.Generic.List<Il2CppSystem.Type> _holderTypes;
        private static float _nextTick;

        public static void Clear()
        {
            IdleSince.Clear();
            NextJob.Clear();
            PausedUntil.Clear();
            Failures.Clear();
            OrderGivenAt.Clear();
        }

        public static List<string> Jobs() =>
            (Config.AutoJobs.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(j => j.Trim()).ToList();

        /// <summary>Call regularly (every frame is fine, it throttles itself).</summary>
        public static void Tick()
        {
            if (!NetUtils.IsServer && NetUtils.IsMultiplayer) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextTick) return;
            _nextTick = now + 5f;

            if (!Config.AutoJobsEnabled.Value) return;
            var jobs = Jobs();
            if (jobs.Count == 0) return;
            string who = (Config.AutoJobsFor.Value ?? "soldiers").ToLowerInvariant();

            try
            {
                if (who == "soldiers" || who == "all")
                {
                    foreach (var (actor, _) in SoldierRegistry.All()) Consider(actor, jobs, now);
                }
                if (who == "kelvin" || who == "all")
                {
                    foreach (var actor in ActorTools.GetActors(CharacterReplacement.RobbyType))
                    {
                        if (actor && !actor.name.StartsWith("LivingSoldier")) Consider(actor, jobs, now);
                    }
                }
            }
            catch (Exception e)
            {
                RLog.Error($"{CharacterReplacement.Tag} AutoJobs: {e.Message}");
            }
        }

        private static void Consider(VailActor actor, List<string> jobs, float now)
        {
            if (!actor || !actor.gameObject.activeInHierarchy || actor.IsDead()) return;
            Robby robby = actor.GetComponentInChildren<Robby>(true);
            if (!robby) return;
            IntPtr key = actor.Pointer;

            bool injured = robby._injuredState.ToString() != "None";
            // A player told this companion to follow / stay / rest: never override that with an auto job.
            if (NoBreaks.PlayerHold.ContainsKey(robby.Pointer)) { IdleSince.Remove(key); OrderGivenAt.Remove(key); return; }
            bool busy = robby._activeOrder != null || robby._waitingForOrders || injured;

            // An auto order that ends within a few seconds was refused / could not be done -> count as failure.
            if (OrderGivenAt.TryGetValue(key, out float givenAt))
            {
                if (busy && now - givenAt > 20f) { OrderGivenAt.Remove(key); Failures[key] = 0; }
                else if (!busy && now - givenAt <= 20f)
                {
                    OrderGivenAt.Remove(key);
                    int f = (Failures.TryGetValue(key, out int ff) ? ff : 0) + 1;
                    Failures[key] = f;
                    if (f >= 3)
                    {
                        PausedUntil[key] = now + 600f;
                        Failures[key] = 0;
                        RLog.Msg($"{CharacterReplacement.Tag} AutoJobs: {actor.name} failed 3 times, pausing 10 min");
                    }
                }
                else if (!busy) OrderGivenAt.Remove(key);
            }

            if (busy)
            {
                IdleSince.Remove(key);
                return;
            }
            if (PausedUntil.TryGetValue(key, out float until) && now < until) return;
            if (!IdleSince.TryGetValue(key, out float since))
            {
                IdleSince[key] = now;
                return;
            }
            if (now - since < Math.Max(10f, Config.AutoJobsIdleSeconds.Value)) return;

            RobbyOrderData data = robby._orderData;
            if (!data || data._getItemOrders == null) return;

            // Leash: only work near a player (i.e. usually at the base) ...
            Vector3 pos = actor.transform.position;
            Transform player = NearestPlayer(pos, out float playerDist);
            if (!player || playerDist > Config.AutoJobsPlayerRadius.Value) return;

            // ... and only if there is a holder close by, otherwise he would wander off collecting into nothing.
            var holders = HoldersNear(pos, Config.AutoJobsHolderRadius.Value);
            if (holders.Count == 0)
            {
                PausedUntil[key] = now + 120f;
                return;
            }

            int start = NextJob.TryGetValue(key, out int n) ? n : 0;
            var skipped = new List<string>();
            for (int attempt = 0; attempt < jobs.Count; attempt++)
            {
                string job = jobs[(start + attempt) % jobs.Count];
                int index = -1;
                for (int i = 0; i < data._getItemOrders.Count; i++)
                {
                    if (string.Equals(data._getItemOrders[i]._itemType, job, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                }
                if (index < 0) { skipped.Add($"{job}: unknown item"); continue; }
                string itemType = data._getItemOrders[index]._itemType;
                var matching = holders.Where(h => SafeIsItemType(h, itemType) || SafeIsItemType(h, job)).ToList();
                if (matching.Count == 0) { skipped.Add($"{job}: no holder"); continue; }
                // Validate() is the check the AI itself uses for a holder target (e.g. not full). Skip items whose
                // holders are all rejected, unless the check itself is unavailable.
                int valid = matching.Count(h => SafeValidate(h, actor));
                if (valid == 0) { skipped.Add($"{job}: {matching.Count} holder(s) full/not accepting"); continue; }

                NoBreaks.GivingAutoOrder = true;
                try { robby.OnGiveOrderServer(Robby.OrderType.Get, index, DropFillHolder, player); }
                finally { NoBreaks.GivingAutoOrder = false; }
                NextJob[key] = (start + attempt + 1) % jobs.Count;
                IdleSince.Remove(key);
                OrderGivenAt[key] = now;
                RLog.Msg($"{CharacterReplacement.Tag} AutoJobs: {actor.name} -> get {job} (order #{index}) into holder ({valid}/{matching.Count} holders accept)");
                return;
            }
            // Nothing to do right now (e.g. all holders full): wait a minute before checking again.
            PausedUntil[key] = now + 60f;
            RLog.Msg($"{CharacterReplacement.Tag} AutoJobs: {actor.name} nothing to do ({string.Join("; ", skipped)}), next check in 60s");
        }

        private static bool SafeValidate(ItemHolderStimuli h, VailActor actor)
        {
            try { return h && h.Validate(actor); } catch { return true; }
        }

        private static bool SafeIsItemType(ItemHolderStimuli h, string type)
        {
            try { return h && h.IsItemType(type); } catch { return false; }
        }

        private static List<ItemHolderStimuli> HoldersNear(Vector3 pos, float radius)
        {
            var result = new List<ItemHolderStimuli>();
            try
            {
                if (_holderTypes == null)
                {
                    _holderTypes = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Type>();
                    _holderTypes.Add(Il2CppType.Of<ItemHolderStimuli>());
                }
                var found = Stimuli.GetStimuliInRadius(pos, radius, _holderTypes);
                if (found == null) return result;
                foreach (var s in found)
                {
                    var h = s ? s.TryCast<ItemHolderStimuli>() : null;
                    if (h && !h.IsLogSled) result.Add(h);
                }
            }
            catch (Exception e)
            {
                RLog.Warning($"{CharacterReplacement.Tag} AutoJobs holder search failed: {e.Message}");
            }
            return result;
        }

        private static Transform NearestPlayer(Vector3 pos, out float bestDist)
        {
            Transform best = null;
            bestDist = float.MaxValue;
            try
            {
                var list = MultiplayerUtilities._playerEntities;
                if (list != null)
                {
                    foreach (var e in list)
                    {
                        if (!e) continue;
                        float d = Vector3.Distance(pos, e.transform.position);
                        if (d < bestDist) { bestDist = d; best = e.transform; }
                    }
                }
            }
            catch { }
            if (!best && !NetUtils.IsDedicatedServer && LocalPlayer.Transform)
            {
                best = LocalPlayer.Transform;
                bestDist = Vector3.Distance(pos, best.position);
            }
            return best;
        }
    }
}
