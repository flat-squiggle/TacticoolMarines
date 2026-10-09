using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Items.Core;
using Sons.Weapon;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CharacterReplacement
{
    /// <summary>
    /// The game's flashlight attachment (item 378 "FlashlightMod", the one the player puts on pistol, shotgun and
    /// rifle) on the soldiers' rifles. Our rifle copy is the game's own rifle model with its rail and mod locators and
    /// its WeaponMods component, so the mod is applied the game's way (WeaponMods.ApplyMod); if that refuses, the
    /// mod's model is put on the rail locator directly. The light is on only in the dark (night or a cave) while the
    /// rifle is in his hands. Config soldier_flashlight (auto / on / off).
    /// </summary>
    internal static class CombatGunLight
    {
        private const int FlashlightModItem = 378;
        private const int RifleRailModItem = 383;

        private sealed class Lamp
        {
            public GameObject Gun;
            public GameObject Mod;                      // our flashlight attachment (null when off or failed)
            public readonly List<Light> Lights = new(); // every light on the rifle copy, ours or the game's
            public bool Failed, Mounted;
            public bool? On;
            public float NextCheck;
        }

        private static readonly Dictionary<IntPtr, Lamp> Lamps = new();
        private static WeaponMod _flashMod, _railMod;
        private static bool _modsSearched;

        internal static void Tick(IntPtr key, VailActor actor, GameObject gun, bool inHand, bool ready)
        {
            if (!actor || !gun || !ready) return;
            string mode = Config.RifleFlashlight.Value ? "auto" : "off";
            if (!Lamps.TryGetValue(key, out var lamp) || lamp.Gun != gun)
            {
                lamp = new Lamp { Gun = gun };
                Lamps[key] = lamp;
            }
            if (mode != "off" && !lamp.Mounted && !lamp.Failed) { lamp.Mounted = true; Attach(actor, lamp); }
            if (mode == "off" && lamp.Mod) { Object.Destroy(lamp.Mod); lamp.Mod = null; lamp.Mounted = false; lamp.On = null; }
            // Lit only while the rifle is in his hands - never on his back. Any light the rifle copy brings or gets
            // later (the game's own WeaponMods can add the player's attachments) is collected and switched the same way.
            bool want = mode != "off" && inHand && (mode == "on" || Dark(actor));
            float now = Time.time;
            if (now >= lamp.NextCheck)
            {
                lamp.NextCheck = now + 1f;
                lamp.Lights.Clear();
                foreach (var l in gun.GetComponentsInChildren<Light>(true)) if (l) lamp.Lights.Add(l);
                foreach (var l in lamp.Lights) if (l.enabled != want) { l.enabled = want; lamp.On = null; }
            }
            if (lamp.On == want) return;
            lamp.On = want;
            foreach (var l in lamp.Lights) if (l) l.enabled = want;
        }

        /// <summary>Night (20:00 - 6:30) or in a cave.</summary>
        internal static bool Dark(VailActor a)
        {
            try { if (a.IsInCave()) return true; } catch { }
            try
            {
                float h = Sons.Environment.TimeOfDayHolder.GetTimeOfDay().ToHours(false, false) % 24f;
                return h < 6.5f || h >= 20f;
            }
            catch { return false; }
        }

        private static void FindMods()
        {
            if (_modsSearched) return;
            _modsSearched = true;
            try
            {
                foreach (var data in Resources.FindObjectsOfTypeAll<WeaponModsData>())
                {
                    if (!data || data.WeaponMods == null) continue;
                    foreach (var m in data.WeaponMods)
                    {
                        if (m == null) continue;
                        if (m.ModItemId == FlashlightModItem && _flashMod == null) _flashMod = m;
                        if (m.ModItemId == RifleRailModItem && _railMod == null) _railMod = m;
                    }
                }
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Light] reading the weapon attachments: {e.Message}"); }
            RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Light] attachment data: flashlight {(_flashMod != null ? $"found ({_flashMod.AttachesToSlot}, needs {_flashMod.RequiredSlot})" : "not found")}, rifle rail {(_railMod != null ? "found" : "not found")}");
        }

        private static void Attach(VailActor actor, Lamp lamp)
        {
            FindMods();
            GameObject modObj = null;
            string how = "";
            try
            {
                var wm = lamp.Gun.GetComponentInChildren<WeaponMods>(true);
                if (wm != null && _flashMod != null)
                {
                    if (_railMod != null && !wm.HasMod(RifleRailModItem)) { try { wm.ApplyMod(_railMod); } catch { } }
                    bool ok = false;
                    try { ok = wm.ApplyMod(_flashMod); } catch (Exception e) { how = "ApplyMod: " + e.Message; }
                    if (ok) { modObj = wm.GetAttachedModObject(_flashMod); how = "through the game's weapon attachments"; }
                }
                if (!modObj && _flashMod != null && _flashMod.ModPrefab)
                {
                    // put the mod's model on the rail locator ourselves
                    Transform loc = null;
                    foreach (var t in lamp.Gun.GetComponentsInChildren<Transform>(true))
                        if (t.name == "RailModLocator") { loc = t; break; }
                    if (loc)
                    {
                        modObj = Object.Instantiate(_flashMod.ModPrefab, loc, false);
                        how = "directly on the rail";
                    }
                }
            }
            catch (Exception e) { how = e.Message; }
            if (!modObj) { lamp.Failed = true; RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Light] {actor.name}: flashlight could not be attached ({how})"); return; }
            CombatGun.StripPhysicsPublic(modObj);
            // UL_FastGI (the game's light manager on the spot light) switches lights near the camera back on - it
            // lit the nearest soldier's lamp on his back. The lamp is switched only by us.
            foreach (var c in modObj.GetComponentsInChildren<Component>(true))
                if (c && c.GetIl2CppType().Name.Contains("FastGI")) Object.DestroyImmediate(c);
            lamp.Mod = modObj;
            int n = 0;
            foreach (var l in modObj.GetComponentsInChildren<Light>(true)) { l.enabled = false; n++; }
            modObj.SetActive(true);
            lamp.NextCheck = 0f;
            RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Light] {actor.name}: flashlight on the rifle ({how}, {n} light(s))");
        }

        internal static string Describe(VailActor a)
        {
            if (!a || !Lamps.TryGetValue(a.Pointer, out var l)) return "no lamp";
            return l.Failed ? "lamp failed" : $"lamp {(l.On == true ? "on" : "off")} ({l.Lights.Count} light(s)), dark {Dark(a)}";
        }

        internal static void Clear() { Lamps.Clear(); _modsSearched = false; _flashMod = null; _railMod = null; }
    }
}
