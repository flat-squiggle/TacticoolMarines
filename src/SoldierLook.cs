using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using TheForest.Utils;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Per-soldier variety for the tactical soldier model: a uniform colour scheme (tint on the HDRP/Lit base colour
    /// of jacket, trousers, armour, helmet ...) and gear (helmet, sunglasses, balaclava, body armour). The model is
    /// built from separate meshes under GEO, each with its own material, so every soldier gets his own material copies.
    /// The choice is made from the soldier's saved unique id, so he keeps his look across saves.
    /// Config look_scheme (random / off / a scheme name) and look_gear (random / off).
    /// Test: tmcombat variant [scheme] [helmet|nohelmet] [glasses|noglasses] [mask|nomask] [armor|noarmor].
    /// </summary>
    internal static class SoldierLook
    {
        internal sealed class Scheme
        {
            public string Name;
            public Color Cloth, Armor, Gear;   // tints: jacket + trousers, body armour + helmet, gloves + boots
            public Scheme(string n, Color cloth, Color armor, Color gear) { Name = n; Cloth = cloth; Armor = armor; Gear = gear; }
        }

        private static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        internal static readonly Scheme[] Schemes =
        {
            new("standard", C(1f, 1f, 1f),       C(1f, 1f, 1f),       C(1f, 1f, 1f)),
            new("woodland", C(0.62f, 0.78f, 0.45f), C(0.55f, 0.62f, 0.40f), C(0.75f, 0.70f, 0.55f)),
            new("desert",   C(1.55f, 1.22f, 0.80f), C(1.35f, 1.12f, 0.80f), C(1.20f, 1.02f, 0.78f)),  // > 1 brightens (tint multiplies the dark camo)
            // black: vest and helmet black, jacket and trousers dark grey (all black was too dark to read)
            new("black",    C(0.85f, 0.85f, 0.87f), C(0.33f, 0.33f, 0.35f), C(0.40f, 0.40f, 0.40f)),
            new("urban",    C(1.45f, 1.45f, 1.40f), C(0.70f, 0.70f, 0.72f), C(1.10f, 1.10f, 1.10f)),
        };

        internal struct Look
        {
            public int Scheme;
            public bool Helmet, Glasses, Mask, Armor;
            public override string ToString() =>
                $"{Schemes[Scheme].Name}, helmet {(Helmet ? "yes" : "no")}, glasses {(Glasses ? "yes" : "no")}, mask {(Mask ? "yes" : "no")}, vest {(Armor ? "yes" : "no")}";
        }

        // Original base colours of the bundle's shared materials (tints multiply these).
        private static readonly Dictionary<string, Color> BaseColors = new();
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        /// <summary>His look, the same every time for the same soldier.</summary>
        internal static Look For(VailActor a)
        {
            int seed = 17;
            try { seed = a.UniqueId; } catch { }
            var rng = new System.Random(seed * 7919 + 101);
            var look = new Look { Scheme = 0, Helmet = true, Glasses = Config.SolderGlasses.Value, Mask = true, Armor = true };
            string s = (Config.LookScheme.Value ?? "random").Trim().ToLowerInvariant();
            int fixedScheme = Array.FindIndex(Schemes, x => x.Name == s);
            look.Scheme = fixedScheme >= 0 ? fixedScheme : s == "random" ? rng.Next(Schemes.Length) : 0;
            if ((Config.LookGear.Value ?? "random").Trim().ToLowerInvariant() == "random")
            {
                // Balaclava always on; helmet and vest each likely, never both missing.
                look.Helmet = rng.NextDouble() < 0.8;
                look.Glasses = rng.NextDouble() < 0.5;
                look.Armor = rng.NextDouble() < 0.8;
                if (!look.Helmet && !look.Armor) { if (rng.NextDouble() < 0.5) look.Helmet = true; else look.Armor = true; }
            }
            return look;
        }

        /// <summary>Applies the look to a tactical soldier model (the root with GEO below it).</summary>
        internal static void Apply(Transform model, Look look)
        {
            if (!model) return;
            Transform geo = model.Find("GEO");
            if (!geo) return;
            var sc = Schemes[Mathf.Clamp(look.Scheme, 0, Schemes.Length - 1)];
            Set(geo, "TacticalArmorHeadHelmetMesh", look.Helmet);
            Set(geo, "sunglasses", look.Glasses);
            Set(geo, "mask", look.Mask);
            Set(geo, "body_armor", look.Armor);
            Tint(geo, "jacket", sc.Cloth);
            Tint(geo, "pants", sc.Cloth);
            Tint(geo, "body_armor", sc.Armor);
            Tint(geo, "TacticalArmorHeadHelmetMesh", sc.Armor);
            Tint(geo, "gloves", sc.Gear);
            Tint(geo, "boots", sc.Gear);
            // The black scheme gets a dark balaclava too; the others keep the original.
            Tint(geo, "mask", sc.Name == "black" ? C(0.6f, 0.6f, 0.6f) : C(1f, 1f, 1f));
        }

        private static void Set(Transform geo, string part, bool on)
        {
            Transform t = geo.Find(part);
            if (t) t.gameObject.SetActive(on);
        }

        private static void Tint(Transform geo, string part, Color tint)
        {
            Transform t = geo.Find(part);
            Renderer r = t ? t.GetComponent<Renderer>() : null;
            if (!r || !r.sharedMaterial) return;
            try
            {
                string key = r.sharedMaterial.name.Replace(" (Instance)", "");
                if (!BaseColors.TryGetValue(key, out Color baseCol))
                {
                    baseCol = r.sharedMaterial.HasProperty(BaseColorId) ? r.sharedMaterial.GetColor(BaseColorId) : Color.white;
                    BaseColors[key] = baseCol;
                }
                Material m = r.material; // this soldier's own copy
                m.SetColor(BaseColorId, new Color(baseCol.r * tint.r, baseCol.g * tint.g, baseCol.b * tint.b, baseCol.a));
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Look] {part}: {e.Message}"); }
        }

        internal static void Clear() => BaseColors.Clear();
    }
}
