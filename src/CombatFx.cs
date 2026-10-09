using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FMOD.Studio;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Weapon;
using SonsSdk;
using SonsSdk.Networking;
using TheForest.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CharacterReplacement
{
    /// <summary>
    /// Shot effects for the soldiers.
    /// Sound: an FMOD event path (config combat_fx_event). If empty, the game's loaded sound banks are listed and the
    /// most gunshot-like event is chosen and saved. When the player has a gun equipped, its own gunshot event
    /// (RangedWeaponController._gunShotAudioEvent) replaces an automatically chosen one, also saved.
    /// Flash: the muzzle flash prefab of a player gun, copied once it has been in memory (equipping a gun once per
    /// session is enough); until then a flash built from the crash-site flare (its light and sparks).
    ///   tmcombat fx                 status: sound, flash, sources
    ///   tmcombat fx events [word]   gunshot-like sound events (all matching ones go to the log)
    ///   tmcombat fx event &lt;nr|path&gt; use this sound
    ///   tmcombat fx test            play the sound at the player
    ///   tmcombat fx &lt;nr&gt;           use the effects of player gun number &lt;nr&gt;
    ///   tmcombat fx on | off
    /// Host / single player only (a dedicated server has no sound or graphics).
    /// </summary>
    internal static class CombatFx
    {
        private sealed class Source
        {
            public string Name;
            public string Event;
            public GameObject Flash;
        }

        private static List<Source> _sources;
        private static bool _enabled = true;
        private static bool _soundWarned, _flashWarned;
        private static readonly string[] Preference = { "rifle", "shotgun", "revolver", "pistol" };

        private static GameObject _holder;
        private static GameObject _flash;           // copied muzzle flash of a player gun
        private static string _flashFrom;
        private static GameObject _fallbackFlash;   // built from the flare
        private static bool _fallbackSearched;
        private static string _autoEvent;           // what we picked ourselves (may be replaced by a real gun's event)
        private static bool _manualEvent;
        private static bool _weaponEvent;
        private static List<string> _events;
        private static List<(string path, int score)> _gunEvents;
        private static float _nextWeaponScan;

        // ------------------------------------------------------------------ commands

        private static void Save()
        {
            try { Config.Category.SaveToFile(); } catch { }
        }

        // ------------------------------------------------------------------ every frame (host)

        /// <summary>Every 20 s: if the player has a gun in memory, take its sound and muzzle flash.</summary>
        public static void Tick()
        {
            if (NetUtils.IsDedicatedServer) return;
            float now = Time.time;
            if (now < _nextWeaponScan) return;
            _nextWeaponScan = now + 20f;
            // Read the sound banks now (outside a fight) rather than on the first shot - there are thousands of events.
            if (string.IsNullOrEmpty(Config.CombatFxEvent.Value) && _enabled) { try { EventPath(); } catch { } }
            if (_flash && (_manualEvent || _weaponEvent)) return;
            try
            {
                Discover();
                Source best = Best(_sources);
                if (best != null) UseSource(best, manual: false);
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [FX] weapon search: {e.Message}"); }
        }

        private static Source Best(List<Source> list)
        {
            if (list == null || list.Count == 0) return null;
            foreach (var word in Preference)
            {
                var s = list.FirstOrDefault(x => x.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 && x.Event != null && x.Flash)
                        ?? list.FirstOrDefault(x => x.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
                if (s != null) return s;
            }
            return list[0];
        }

        private static void UseSource(Source s, bool manual)
        {
            if (s.Event != null)
            {
                string cur = Config.CombatFxEvent.Value ?? "";
                // A real gun's own sound beats anything picked from the banks (also from an earlier session),
                // but not a choice made by hand in this session.
                _weaponEvent = true;
                if (cur != s.Event && (manual || !_manualEvent))
                {
                    Config.CombatFxEvent.Value = s.Event;
                    if (manual) _manualEvent = true;
                    Save();
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [FX] shot sound copied from '{s.Name}' and saved: {s.Event}");
                }
            }
            if (s.Flash && (manual || !_flash))
            {
                CacheFlash(s.Flash, s.Name);
            }
        }

        // ------------------------------------------------------------------ one shot

        /// <summary>Muzzle flash + gunshot sound for one shot of this soldier in direction dir.</summary>
        public static void Shot(VailActor soldier, Vector3 dir)
        {
            if (!_enabled || !soldier || NetUtils.IsDedicatedServer) return;
            if (dir.sqrMagnitude < 0.001f) dir = soldier.transform.forward;
            dir.Normalize();
            if (!CombatGun.MuzzlePoint(soldier, dir, out Vector3 muzzle))
                muzzle = soldier.transform.position + Vector3.up * 1.45f + dir * 0.9f;

            SpawnFlash(muzzle, dir);
            string ev = EventPath();
            if (ev != null) Play(ev, muzzle);
        }

        private static bool Play(string ev, Vector3 pos)
        {
            if (string.IsNullOrEmpty(ev)) return false;
            try
            {
                if (!FMOD_StudioSystem.TryGetInstance(out FMOD_StudioSystem fmod, false) || fmod == null) return false;
                fmod.PlayOneShot(ev, pos);
                return true;
            }
            catch (Exception e)
            {
                if (!_soundWarned) { _soundWarned = true; RLog.Warning($"{CharacterReplacement.Tag} [Combat] [FX] playing the sound ({ev}): {e.Message}"); }
                return false;
            }
        }

        // ------------------------------------------------------------------ sound events from the banks

        private static string EventPath()
        {
            string ev = (Config.CombatFxEvent.Value ?? "").Trim();
            if (ev != "") return ev;
            var list = GunEvents();
            if (list.Count == 0) return null;
            _autoEvent = list[0].path;
            Config.CombatFxEvent.Value = _autoEvent;
            Save();
            RLog.Msg($"{CharacterReplacement.Tag} [Combat] [FX] shot sound chosen automatically: {_autoEvent} (others: tmcombat fx events)");
            return _autoEvent;
        }

        /// <summary>Every event path of every loaded bank (needs the strings bank, which the game loads).</summary>
        private static List<string> AllEvents()
        {
            if (_events != null && _events.Count > 0) return _events;
            var list = new List<string>();
            try
            {
                if (!FMOD_StudioSystem.TryGetInstance(out FMOD_StudioSystem fmod, false) || fmod == null) return list;
                FMOD.Studio.System sys = fmod.System;
                sys.getBankList(out Il2CppStructArray<Bank> banks);
                if (banks == null) return list;
                foreach (Bank bank in banks)
                {
                    try
                    {
                        bank.getEventList(out Il2CppStructArray<EventDescription> evs);
                        if (evs == null) continue;
                        foreach (EventDescription ev in evs)
                        {
                            try
                            {
                                ev.getPath(out string path);
                                if (!string.IsNullOrEmpty(path)) list.Add(path);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [FX] {banks.Length} sound banks, {list.Count} events read");
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [FX] reading the sound banks: {e.Message}"); }
            list.Sort(StringComparer.Ordinal);
            _events = list;
            return list;
        }

        private static readonly string[] ShotWords = { "shot", "shoot", "fire", "bang", "gunshot" };
        private static readonly string[] Avoid =
        {
            "reload", "empty", "dry", "equip", "aim", "pickup", "pick_up", "impact", "whiz", "whizz", "casing", "shell", "bolt",
            "click", "holster", "draw", "/ui", "menu", "craft", "ricochet", "/hit", "magazine", "cock", "foley", "swap", "inspect",
            "campfire", "fireplace", "fire_", "burn", "flame", "torch", "firework", "flare", "loop", "ambience", "ambient", "music",
        };

        private static int Score(string path)
        {
            string l = path.ToLowerInvariant();
            int weapon = l.Contains("rifle") ? 30 : l.Contains("gun") ? 15 : l.Contains("pistol") || l.Contains("revolver") ? 10 : l.Contains("shotgun") ? 5 : 0;
            if (weapon == 0) return -1;
            if (!ShotWords.Any(w => l.Contains(w))) return -1;
            if (Avoid.Any(w => l.Contains(w))) return -1;
            int score = weapon;
            string last = l.Substring(l.LastIndexOf('/') + 1);
            if (ShotWords.Any(w => last.Contains(w))) score += 8;
            if (l.Contains("player")) score += 3;
            if (l.Contains("distant") || l.Contains("far")) score -= 6;
            return score;
        }

        private static List<(string path, int score)> GunEvents()
        {
            if (_gunEvents != null && _gunEvents.Count > 0) return _gunEvents;
            var all = AllEvents();
            var list = new List<(string path, int score)>();
            foreach (var p in all)
            {
                int s = Score(p);
                if (s > 0) list.Add((p, s));
            }
            list.Sort((a, b) => a.score != b.score ? b.score.CompareTo(a.score) : string.CompareOrdinal(a.path, b.path));
            _gunEvents = list;
            if (all.Count > 0)
            {
                // Everything weapon-related goes to the log once, so a better event can be picked by hand.
                int n = 0;
                foreach (var p in all)
                {
                    string l = p.ToLowerInvariant();
                    if (!(l.Contains("gun") || l.Contains("rifle") || l.Contains("pistol") || l.Contains("revolver") || l.Contains("weapon") || l.Contains("bullet"))) continue;
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [FX] [Sound] {p}");
                    if (++n >= 300) break;
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ muzzle flash

        private static GameObject Holder()
        {
            if (!_holder)
            {
                _holder = new GameObject("LivingSoldiers_FxTemplates");
                _holder.SetActive(false);
                Object.DontDestroyOnLoad(_holder);
            }
            return _holder;
        }

        private static void CacheFlash(GameObject prefab, string from)
        {
            try
            {
                if (_flash) Object.Destroy(_flash);
                _flash = Object.Instantiate(prefab, Holder().transform, false);
                _flash.name = "LivingSoldiers_Flash";
                _flashFrom = from;
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [FX] muzzle flash copied from '{from}' ({prefab.name})");
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [FX] copying the muzzle flash: {e.Message}"); }
        }

        /// <summary>A flash made from the crash-site flare: its point light and its sparks, as one short burst.</summary>
        private static GameObject FallbackFlash()
        {
            if (_fallbackFlash || _fallbackSearched) return _fallbackFlash;
            _fallbackSearched = true;
            try
            {
                Light light = null;
                foreach (var l in Resources.FindObjectsOfTypeAll<Light>())
                {
                    if (!l || !l.transform.parent) continue;
                    if (l.transform.parent.name == "FlareFX") { light = l; break; }
                }
                ParticleSystem sparks = null;
                foreach (var ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
                {
                    if (ps && ps.name == "Sparks") { sparks = ps; break; }
                }
                if (!light && !sparks)
                {
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [FX] no substitute muzzle flash (no flare loaded)");
                    return null;
                }
                var root = new GameObject("LivingSoldiers_FlashFallback");
                root.transform.SetParent(Holder().transform, false);
                if (light)
                {
                    var lc = Object.Instantiate(light.gameObject, root.transform, false);
                    lc.name = "FlashLight";
                    lc.transform.localPosition = Vector3.zero;
                    foreach (Transform child in lc.transform) Object.Destroy(child.gameObject);
                }
                if (sparks)
                {
                    var sc = Object.Instantiate(sparks.gameObject, root.transform, false);
                    sc.name = "FlashSparks";
                    sc.transform.localPosition = Vector3.zero;
                    sc.transform.localScale = Vector3.one * 0.35f;
                    foreach (Transform child in sc.transform) Object.Destroy(child.gameObject);
                    var ps = sc.GetComponent<ParticleSystem>();
                    if (ps)
                    {
                        var main = ps.main;
                        main.loop = false;
                        main.playOnAwake = false;
                        var em = ps.emission;
                        em.enabled = false;
                    }
                }
                _fallbackFlash = root;
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [FX] substitute muzzle flash built from the flare (light {(bool)light}, sparks {(bool)sparks})");
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [FX] substitute muzzle flash: {e.Message}"); }
            return _fallbackFlash;
        }

        private static void SpawnFlash(Vector3 muzzle, Vector3 dir)
        {
            try
            {
                bool real = _flash;
                GameObject template = real ? _flash : FallbackFlash();
                if (!template) return;
                GameObject fx = Object.Instantiate(template, muzzle, Quaternion.LookRotation(dir, Vector3.up));
                fx.SetActive(true);
                if (real)
                {
                    foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>(true)) ps.Play(true);
                    foreach (var l in fx.GetComponentsInChildren<Light>(true)) l.enabled = true;
                    Object.Destroy(fx, 1.5f);
                }
                else
                {
                    foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>(true)) ps.Emit(14);
                    FlickLight(fx).RunCoro();
                    Object.Destroy(fx, 1.2f);
                }
            }
            catch (Exception e)
            {
                if (!_flashWarned) { _flashWarned = true; RLog.Warning($"{CharacterReplacement.Tag} [Combat] [FX] muzzle flash: {e.Message}"); }
            }
        }

        private static IEnumerator FlickLight(GameObject fx)
        {
            float until = Time.time + 0.06f;
            while (fx && Time.time < until) yield return null;
            if (!fx) yield break;
            foreach (var l in fx.GetComponentsInChildren<Light>(true)) l.enabled = false;
        }

        // ------------------------------------------------------------------ finding the player's guns

        private static void Discover()
        {
            var list = new List<Source>();
            var seen = new HashSet<string>();
            try
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<RangedWeaponController>())
                {
                    if (!c) continue;
                    string name = c.gameObject.name;
                    string ev = null;
                    GameObject flash = null;
                    try { ev = c._gunShotAudioEvent; } catch { }
                    try
                    {
                        var mf = c._muzzleFlashPrefab;
                        if (mf != null && mf) flash = mf.gameObject;
                    }
                    catch { }
                    if (string.IsNullOrEmpty(ev) && !flash) continue;
                    if (!seen.Add(name + "|" + ev + "|" + (flash ? flash.name : ""))) continue;
                    list.Add(new Source { Name = name, Event = string.IsNullOrEmpty(ev) ? null : ev, Flash = flash });
                }
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [FX] searching for weapons: {e.Message}"); }
            _sources = list.OrderBy(s => s.Name).ToList();
        }

        internal static void Clear()
        {
            _sources = null;
            _events = null;
            _gunEvents = null;
            _nextWeaponScan = 0f;
            _fallbackSearched = false;
            if (_fallbackFlash) Object.Destroy(_fallbackFlash);
            _fallbackFlash = null;
            // The copied gun flash lives in a DontDestroyOnLoad holder and stays valid for the next world.
        }
    }
}
