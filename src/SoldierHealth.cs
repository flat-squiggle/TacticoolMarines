using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using TheForest.Utils;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Soldiers taking damage, being downed and getting up again.
    ///   - Compatibility with the ImmortalCompanions mod: it blocks VailActor.ReceivedDamage for every Kelvin-type actor,
    ///     and the soldiers are Kelvin-type actors - so they took no damage and showed no hit reactions at all. Its
    ///     check is skipped for our soldiers (it still protects the real Kelvin and Virginia); config soldier_damage.
    ///   - soldier_immortal: the game's own DamageActor(damage, preventDeath) is called with preventDeath for soldiers,
    ///     and ForceDeath is turned into being downed. They still take damage.
    ///   - Downed: at no health the soldier goes into Kelvin's own injured state (falls, lies injured, the player can
    ///     help him up as with Kelvin); after soldier_downed_time seconds he gets up by himself with
    ///     soldier_getup_health of his health, and heals slowly while not fighting.
    /// </summary>
    internal static class SoldierHealth
    {
        private static bool _patched;

        internal static void Apply()
        {
            if (_patched) return;
            _patched = true;
            var h = new HarmonyLib.Harmony("livingsoldiers.health");
            try
            {
                var damage = AccessTools.Method(typeof(VailActor), nameof(VailActor.DamageActor));
                if (damage != null) h.Patch(damage, prefix: new HarmonyMethod(typeof(SoldierHealth), nameof(DamageActorPrefix)));
                var force = AccessTools.Method(typeof(VailActor), nameof(VailActor.ForceDeath));
                if (force != null) h.Patch(force, prefix: new HarmonyMethod(typeof(SoldierHealth), nameof(ForceDeathPrefix)));
                var rd = AccessTools.Method(typeof(VailActor), nameof(VailActor.ReceivedDamage));
                if (rd != null) h.Patch(rd, prefix: new HarmonyMethod(typeof(SoldierHealth), nameof(LogHitPrefix)) { priority = HarmonyLib.Priority.Last },
                    postfix: new HarmonyMethod(typeof(SoldierHealth), nameof(LogHitPostfix)));
                RLog.Msg($"{CharacterReplacement.Tag} [Health] damage hooks: DamageActor={damage != null} ForceDeath={force != null}");
            }
            catch (Exception e) { RLog.Error($"{CharacterReplacement.Tag} [Health] patch failed: {e.Message}"); }
            _harmony = h;
            TryCompat();
        }

        private static HarmonyLib.Harmony _harmony;
        private static bool _compatDone;
        private static int _compatTries;

        /// <summary>ImmortalCompanions may load after this mod: tried again from the tick until found (or given up).</summary>
        private static void TryCompat()
        {
            if (_compatDone || _harmony == null || _compatTries++ > 20) return;
            // Patching their small check method had no effect (measured: hits still did nothing - .NET inlines it into
            // the patched method). So their patch is taken off VailActor.ReceivedDamage and ours does the same job:
            // the real Kelvin and Virginia stay immune exactly as their settings say, our soldiers are let through.
            try
            {
                Type cfg = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    cfg = asm.GetType("ImmortalCompanions.Config", false);
                    if (cfg != null) break;
                }
                if (cfg == null) return;
                _icToggle = cfg.GetProperty("ModToggle", BindingFlags.Public | BindingFlags.Static);
                _icKelvin = cfg.GetProperty("ImmuneKelvin", BindingFlags.Public | BindingFlags.Static);
                _icVirginia = cfg.GetProperty("ImmuneVirginia", BindingFlags.Public | BindingFlags.Static);
                var target = AccessTools.Method(typeof(VailActor), nameof(VailActor.ReceivedDamage));
                var info = HarmonyLib.Harmony.GetPatchInfo(target);
                if (info == null) return; // their patch is not applied yet
                bool theirs = false;
                foreach (var pr in info.Prefixes) if (pr.owner == "ImmortalCompanions") theirs = true;
                if (!theirs) return;
                _harmony.Unpatch(target, HarmonyPatchType.Prefix, "ImmortalCompanions");
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(SoldierHealth), nameof(ReceivedDamagePrefix)));
                _compatDone = true;
                RLog.Msg($"{CharacterReplacement.Tag} [Health] ImmortalCompanions found: its damage block now applies only to the real Kelvin/Virginia (by its own settings); the soldiers follow soldier_damage / soldier_immortal");
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Health] ImmortalCompanions compatibility: {e.Message}"); }
        }

        private static PropertyInfo _icToggle, _icKelvin, _icVirginia;

        private static bool IcValue(PropertyInfo p)
        {
            try
            {
                object entry = p?.GetValue(null);
                return entry != null && (bool)entry.GetType().GetProperty("Value").GetValue(entry);
            }
            catch { return false; }
        }

        /// <summary>ImmortalCompanions' rule, minus our soldiers: no damage at all for the real Kelvin (9) / Virginia (10).</summary>
        private static bool ReceivedDamagePrefix(VailActor __instance)
        {
            try
            {
                if (SoldierRegistry.IsSoldier(__instance)) return Config.SoldierDamage.Value;
                if (!IcValue(_icToggle)) return true;
                int t = (int)__instance.TypeId;
                if (t == 9) return !IcValue(_icKelvin);
                if (t == 10) return !IcValue(_icVirginia);
                return true;
            }
            catch { return true; }
        }

        // Every hit on a soldier in the log: what came in and what it did.
        private static void LogHitPrefix(VailActor __instance, out float __state)
        {
            __state = -1f;
            try
            {
                if (!SoldierRegistry.IsSoldier(__instance)) return;
                __state = __instance.GetHealth();
            }
            catch { }
        }

        private static void LogHitPostfix(VailActor __instance, ImpactType impactType, int subImpactType, float amount, bool fromPlayer, bool hitFromBehind, float __state)
        {
            if (__state < 0f) return;
            try
            {
                Robby r = CombatOrders.RobbyOf(__instance);
                CombatAI.Debug($"{__instance.name} hit: {impactType}/{subImpactType} {amount:0.#} {(fromPlayer ? "by the player " : "")}{(hitFromBehind ? "from behind " : "")}- health {__state:0} -> {__instance.GetHealth():0}, state {(r ? r._injuredState.ToString() : "?")}, dead {__instance.IsDead()}");
            }
            catch { }
        }

        private static void DamageActorPrefix(VailActor __instance, ref bool preventDeath)
        {
            try { if (Config.SoldierImmortal.Value && SoldierRegistry.IsSoldier(__instance)) preventDeath = true; }
            catch { }
        }

        private static bool ForceDeathPrefix(VailActor __instance)
        {
            try
            {
                if (!Config.SoldierImmortal.Value || !SoldierRegistry.IsSoldier(__instance)) return true;
                RLog.Msg($"{CharacterReplacement.Tag} [Health] {__instance.name}: should die (ForceDeath) - immortal, downed instead");
                Down(__instance);
                return false;
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------ downed / getting up

        private static readonly Dictionary<IntPtr, float> DownedSince = new();

        // Measured 2026-10-08: when a Kelvin-type actor's health bottoms out (the game keeps him at 1), the game itself
        // sets the animator bool "Dying": he falls (RobbyFallInjured) and lies injured (RobbyInjured, layer 2) - the
        // quick down the player can revive. When his health comes back up, Dying goes off and he plays the short
        // RobbyGetUpInjured. Robby.SetInjuredState(Injured) is a different thing: the long start-of-game rolling pose
        // (RobbyInPain) - not used any more.
        private static readonly int DyingHash = Animator.StringToHash("Dying");

        internal static bool IsDowned(VailActor a)
        {
            try
            {
                Animator an = CombatLook.AnimatorOf(a);
                return an && an.GetBool(DyingHash);
            }
            catch { return false; }
        }

        /// <summary>Puts him down the game's way: health to the floor, the game does the rest.</summary>
        internal static void Down(VailActor a)
        {
            try
            {
                if (!a || IsDowned(a)) return;
                float h = a.GetHealth();
                if (h > 1f) a.DamageActor(h - 1f, true);
            }
            catch (Exception e) { CombatAI.DebugOnce("down" + e.Message, $"Downing: {e.Message}"); }
        }

        private static void GetUp(VailActor a, string why)
        {
            try
            {
                float max = a.GetMaxHealth();
                float want = Mathf.Max(max * Mathf.Clamp01(Config.SoldierGetUpHealth.Value), 30f);
                float h = a.GetHealth();
                if (h < want) Heal(a, want - h);
                CombatAI.Debug($"{a.name} gets up again ({why}, {a.GetHealth():0}/{max:0} health)");
            }
            catch (Exception e) { CombatAI.DebugOnce("getup" + e.Message, $"Getting up: {e.Message}"); }
        }

        /// <summary>Health up by amount: the game's own DamageActor with negative damage (checked in game).</summary>
        private static void Heal(VailActor a, float amount)
        {
            if (amount <= 0f) return;
            float before = a.GetHealth();
            try { a.DamageActor(-amount, true); } catch { }
            if (a.GetHealth() <= before + 0.01f) CombatAI.DebugOnce("heal", $"Healing through DamageActor(-x) does not work ({before:0} -> {a.GetHealth():0})");
        }

        // ------------------------------------------------------------------ test command

        internal static string Describe(VailActor a)
        {
            try
            {
                Robby r = CombatOrders.RobbyOf(a);
                string st = r ? r._injuredState.ToString() : "?";
                return $"damage enabled {a._damageEnabled}, health {a.GetHealth():0}/{a.GetMaxHealth():0}, downed {IsDowned(a)}, Robby state {st}, dead {a.IsDead()}, fighting {(CombatAI.IsEngaged(a) ? "yes" : "no")}";
            }
            catch (Exception e) { return e.Message; }
        }

        private static float _nextCompat;

        private static float _nextHealth;

        private static float _setupSeenAt = -1f;
        private static bool _dupesChecked;

        /// <summary>
        /// Once per loaded world, a minute after the soldiers are set up: Kelvin-type actors that are neither the story
        /// Kelvin nor a soldier this mod knows are leftovers (extra soldiers were respawned on every load before 1.38.1,
        /// each load left the old one standing as a plain Kelvin) - removed. Config auto_remove_duplicates.
        /// </summary>
        private static void CheckDuplicates(float now)
        {
            if (_dupesChecked || !Config.AutoRemoveDuplicates.Value) return;
            if (!CharacterReplacement.SetupDone) { _setupSeenAt = -1f; return; }
            if (_setupSeenAt < 0f) { _setupSeenAt = now; return; }
            if (now - _setupSeenAt < 60f) return;
            _dupesChecked = true;
            try
            {
                var (total, known, surplus) = WorldCleanup.Count();
                if (known == 0) { RLog.Msg($"{CharacterReplacement.Tag} Duplicate Kelvins: no managed soldiers known - nothing removed (safety)"); return; }
                if (surplus == 0) return;
                int n = WorldCleanup.RemoveSurplus();
                RLog.Msg($"{CharacterReplacement.Tag} Duplicate Kelvins: {n} left-over ones removed ({known} soldiers and the story Kelvin stay)");
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} checking for duplicate Kelvins: {e.Message}"); }
        }

        internal static void Tick(float now)
        {
            CheckDuplicates(now);
            if (!_compatDone && now >= _nextCompat) { _nextCompat = now + 5f; TryCompat(); }
            if (now < _nextHealth) return;
            _nextHealth = now + 0.2f;
            foreach (var (a, _) in SoldierRegistry.All())
            {
                if (!a || !a.gameObject.activeInHierarchy) continue;
                try
                {
                    if (a.IsDead()) { DownedSince.Remove(a.Pointer); continue; }
                    // Kelvin-type actors come with the game's damage switch off.
                    if (Config.SoldierDamage.Value && !a._damageEnabled) a.SetDamageEnabled(true);
                    bool down = IsDowned(a);
                    if (!DownedSince.TryGetValue(a.Pointer, out float since))
                    {
                        if (!down) continue;
                        DownedSince[a.Pointer] = now;
                        CombatAI.ReleaseFor(a);
                        CombatAI.Debug($"{a.name} goes down injured ({a.GetHealth():0} health) - gets up after {Config.SoldierDownedTime.Value:0}s if nobody helps him");
                        continue;
                    }
                    if (!down) { DownedSince.Remove(a.Pointer); CombatAI.Debug($"{a.name} is back on his feet ({a.GetHealth():0} health)"); continue; }
                    if (now - since >= Mathf.Max(3f, Config.SoldierDownedTime.Value)) { GetUp(a, "by himself"); DownedSince[a.Pointer] = now + 5f; }
                }
                catch { }
            }
        }

        internal static void Clear() { DownedSince.Clear(); _dupesChecked = false; _setupSeenAt = -1f; }
    }
}
