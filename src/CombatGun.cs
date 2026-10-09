using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using SonsSdk;
using SonsSdk.Networking;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CharacterReplacement
{
    /// <summary>
    /// Gun models for the soldiers. A clean copy (meshes + materials only, no scripts) of a loaded game model is
    /// given to every soldier automatically: in the right hand while he aims, slung on the back otherwise.
    /// All values live in the config (combat_gun_*), the commands change them live and save them:
    ///   tmcombat gun list [word]       list loaded weapon models
    ///   tmcombat gun <nr>              use model number <nr> from the last list
    ///   tmcombat gun pos|rot x y z     gun in the hand (aiming)
    ///   tmcombat gun back pos|rot x y z  gun on the back
    ///   tmcombat gun scale s           size
    ///   tmcombat gun idle back|hide|hand   where the gun is while not aiming
    ///   tmcombat gun info | off | on
    /// Visual only, host only (a dedicated server renders nothing, so it is skipped there).
    /// </summary>
    internal static partial class CombatGun
    {
        private static readonly string[] WeaponWords = { "shotgun", "pistol", "rifle", "revolver", "gun", "weapon", "crossbow" };
        private static readonly string[] SkipWords = { "LivingSoldiers_", "Holster", "Ammo", "Bullet", "Shell", "Casing", "Projectile", "Icon", "Magazine", "Rack" };
        private const string DefaultModel = "GetDownSonRifleRig";
        internal const string AutoModel = "auto";

        private sealed class Candidate
        {
            public GameObject Root;
            public string Label;
            public int Parts;
            public int Verts;
        }

        private sealed class GunState
        {
            public VailActor Actor;
            public GameObject Gun;
            public string Mode = "";
            public Transform R, L, Spine;
            public bool BonesSearched;
            public bool HasLocal;
            public Vector3 LocalPos;
            public Quaternion LocalRot = Quaternion.identity;
            public Quaternion AimFix = Quaternion.identity;
            public float KickAt = -99f;
            public float AimBlend;
            public Transform Arm;
            public bool ArmSearched;
            public bool ChainsSearched;
            public ArmChain RChain, LChain;
            public float IkWeight;
            public float NativeWait;
            public bool NativeLoadAsked;
            public bool NativeReady;
        }

        private static List<Candidate> _cands;
        private static GameObject _holder;
        private static GameObject _template;
        private static string _templateFor = "";
        private static readonly Dictionary<IntPtr, GunState> Attached = new();
        private static float _nextScan, _nextFindTry;
        private static int _findTries;
        private static bool _bundleLogged;

        // ------------------------------------------------------------------ config values

        private static (Vector3 pos, Vector3 rot) Parse(string s)
        {
            var pos = Vector3.zero;
            var rot = Vector3.zero;
            try
            {
                var halves = (s ?? "").Split(';');
                if (halves.Length > 0) pos = Vec(halves[0], pos);
                if (halves.Length > 1) rot = Vec(halves[1], rot);
            }
            catch { }
            return (pos, rot);
        }

        private static Vector3 Vec(string s, Vector3 fallback)
        {
            var p = s.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3 || !TryF(p[0], out float x) || !TryF(p[1], out float y) || !TryF(p[2], out float z)) return fallback;
            return new Vector3(x, y, z);
        }

        private static bool TryF(string s, out float f) =>
            float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out f);

        private static void Save()
        {
            try { Config.Category.SaveToFile(); } catch { }
            foreach (var st in Attached.Values) st.Mode = ""; // force re-placement with the new values
        }

        private static string Idle => (Config.CombatGunIdle.Value ?? "back").Trim().ToLowerInvariant();

        // ------------------------------------------------------------------ automatic: called every frame

        public static void Tick()
        {
            if (NetUtils.IsDedicatedServer) return;
            float now = Time.time;
            if (now >= _nextScan)
            {
                _nextScan = now + 2f;
                try { Scan(now); } catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] Scan: {e.Message}"); }
            }
            if (Attached.Count == 0) return;

            List<IntPtr> gone = null;
            foreach (var kv in Attached)
            {
                var st = kv.Value;
                if (!st.Actor || !st.Gun) { (gone ??= new List<IntPtr>()).Add(kv.Key); continue; }
                string want;
                bool dead = false;
                try { dead = st.Actor.IsDead(); } catch { }
                if (dead) want = "hide";
                else
                {
                    // In a fight the current pose decides: rifle clips hold the gun, running/staggering clips do not
                    // (the arms swing - a gun glued to the hand would flail around), so it goes on the back.
                    bool holds = CombatLook.PoseHoldsGun(st.Actor, out bool known);
                    if (known) want = holds ? "hand" : Idle == "hide" ? "hide" : "back";
                    else if (CombatAI.IsEngaged(st.Actor) && CombatMove.IsMoving(st.Actor)) want = Idle == "hide" ? "hide" : "back";
                    else if (CombatLook.IsAiming(st.Actor)) want = "hand";
                    else want = Idle == "hand" ? "hand" : Idle == "hide" ? "hide" : "back";
                }
                // Load and measure the model from the start (not first when he aims): ready in the hand at once.
                if (!st.NativeReady) Preload(st);
                try { CombatGunLight.Tick(kv.Key, st.Actor, st.Gun, st.Mode == "hand", st.NativeReady); } catch { }
                // On the back only once the model is measured - place it again when that happened meanwhile.
                if (want != st.Mode || (want == "back" && !st.Gun.activeSelf && _analysed)) Place(st, want);
            }
            if (gone != null) foreach (var k in gone) { if (Attached.TryGetValue(k, out var st) && st.Gun) Object.Destroy(st.Gun); Attached.Remove(k); }
        }

        private static void Scan(float now)
        {
            var soldiers = SoldierRegistry.All();
            foreach (var (actor, _) in soldiers) CombatLook.EnsureApplied(actor);

            string model = (Config.CombatGunModel.Value ?? "").Trim();
            if (model == "") { if (Attached.Count > 0) RemoveAll(); return; }

            if (model == "auto" && (!_template || _templateFor != "auto"))
            {
                if (now < _nextFindTry) return;
                if (!FindNative())
                {
                    _findTries++;
                    _nextFindTry = now + 20f;
                    if (_findTries < 3) return; // the held items may still be loading
                    RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] no held rifle object of the game found - using {DefaultModel} (fitted by hand)");
                    GameObject fb = FindModel(DefaultModel);
                    if (!fb) return;
                    SetTemplate(fb, DefaultModel);
                    _templateFor = "auto";
                }
            }
            else if (model != "auto" && (!_template || _templateFor != model))
            {
                if (now < _nextFindTry) return;
                GameObject src = FindModel(model);
                if (!src)
                {
                    _findTries++;
                    _nextFindTry = now + (_findTries < 5 ? 15f : 120f);
                    if (_findTries <= 3) RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] model '{model}' not loaded yet, trying again later");
                    return;
                }
                SetTemplate(src, model);
            }

            foreach (var (actor, _) in soldiers)
            {
                if (!actor || Attached.ContainsKey(actor.Pointer)) continue;
                GameObject gun = Object.Instantiate(_template);
                gun.name = "LivingSoldiers_Gun";
                gun.SetActive(false);
                var nst = new GunState { Actor = actor, Gun = gun };
                Attached[actor.Pointer] = nst;
                Preload(nst); // ask for the model right away
            }
        }

        internal static void StripPhysicsPublic(GameObject go) => StripPhysics(go);

        /// <summary>Test output: where his rifle is and whether it is shown.</summary>
        internal static string Describe(VailActor a)
        {
            if (!a || !Attached.TryGetValue(a.Pointer, out var st) || !st.Gun) return "no rifle";
            return $"rifle {st.Mode ?? "-"}, visible {st.Gun.activeInHierarchy}, model loaded {st.NativeReady}";
        }

        private static void Place(GunState st, string mode)
        {
            st.Mode = mode;
            if (mode == "hide") { st.Gun.SetActive(false); return; }

            Transform parent = mode == "hand" ? (_native ? PlayerSocket(st.Actor) : RightHand(st.Actor)) : Back(st.Actor);
            if (!parent) { st.Gun.SetActive(false); return; }
            var (pos, rot) = Parse(mode == "hand" ? Config.CombatGunHand.Value : Config.CombatGunBack.Value);

            // Parented so it follows the bone; the exact position and rotation are fitted every frame after the
            // animation (LateTick). Until the first fit it keeps the last fitted pose relative to the hand.
            Transform t = st.Gun.transform;
            t.SetParent(parent, false);
            if (_native)
            {
                // The game's own pose in the hand socket (on the back the fit takes over once the gun is measured).
                t.localPosition = mode == "hand" ? _nPos : Vector3.zero;
                t.localRotation = mode == "hand" ? _nRot : Quaternion.identity;
                t.localScale = NativeLocalScale(parent);
                if (mode == "back" && !_analysed) { st.Gun.SetActive(false); return; }
            }
            else
            {
                if (mode == "hand" && st.HasLocal) { t.localPosition = st.LocalPos; t.localRotation = st.LocalRot; }
                else { t.localPosition = Vector3.zero; t.localRotation = Quaternion.identity; }
                t.localScale = Vector3.one * Config.CombatGunScale.Value;
            }
            st.Gun.SetActive(true);
        }

        // ------------------------------------------------------------------ shooting helpers

        /// <summary>Muzzle of the gun: the fitted muzzle point, else the far side of the gun's bounds along dir.</summary>
        internal static bool MuzzlePoint(VailActor actor, Vector3 dir, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!actor || !Attached.TryGetValue(actor.Pointer, out var st) || !st.Gun || !st.Gun.activeInHierarchy) return false;
            if (_analysed && st.Mode == "hand") { pos = st.Gun.transform.TransformPoint(MuzzleLocal); return true; }
            bool any = false;
            Bounds b = default;
            foreach (var r in st.Gun.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!any) return false;
            Vector3 ext = b.extents;
            float reach = Mathf.Abs(ext.x * dir.x) + Mathf.Abs(ext.y * dir.y) + Mathf.Abs(ext.z * dir.z);
            pos = b.center + dir * reach;
            return true;
        }

        /// <summary>Short recoil kick of the gun model (applied by the fit while it is in the hand).</summary>
        internal static void Kick(VailActor actor, Vector3 dir)
        {
            if (!actor || !Attached.TryGetValue(actor.Pointer, out var st) || !st.Gun || st.Mode != "hand") return;
            st.KickAt = Time.time;
        }

        private static Transform RightHand(VailActor actor)
        {
            try
            {
                Robby robby = actor.GetComponentInChildren<Robby>(true);
                if (robby && robby._robbyRightHand) return robby._robbyRightHand;
            }
            catch { }
            try
            {
                Animator an = CombatLook.AnimatorOf(actor);
                if (an && an.isHuman) return an.GetBoneTransform(HumanBodyBones.RightHand);
            }
            catch { }
            return null;
        }

        private static readonly HashSet<string> LoggedBack = new();

        /// <summary>
        /// Upper back bone. The tactical model is not a Unity "humanoid" rig, so the humanoid lookup usually fails:
        /// then walk up from the right hand (hand - forearm - upper arm - shoulder - spine) to the first bone named
        /// spine/chest. Last resort is the model root, so the gun never just disappears.
        /// </summary>
        private static Transform Back(VailActor actor)
        {
            Transform found = null;
            string how = "";
            Animator an = null;
            try { an = CombatLook.AnimatorOf(actor); } catch { }
            try
            {
                if (an && an.isHuman)
                {
                    foreach (var bone in new[] { HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine })
                    {
                        Transform b = an.GetBoneTransform(bone);
                        if (b) { found = b; how = "Humanoid " + bone; break; }
                    }
                }
            }
            catch { }
            if (!found)
            {
                Transform hand = RightHand(actor);
                for (Transform t = hand ? hand.parent : null; t; t = t.parent)
                {
                    string n = t.name.ToLowerInvariant();
                    if (n.Contains("spine") || n.Contains("chest")) { found = t; how = "name, above the hand"; break; }
                }
            }
            if (!found && an) { found = an.transform; how = "model root (fallback)"; }
            if (found && LoggedBack.Add(found.name + how))
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] back bone: '{found.name}' ({how})");
            return found;
        }

        // ------------------------------------------------------------------ model handling

        private static bool Matches(string text, IEnumerable<string> words)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var w in words)
                if (text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static Mesh MeshOf(Renderer r)
        {
            try
            {
                var smr = r.TryCast<SkinnedMeshRenderer>();
                if (smr) return smr.sharedMesh;
                var mr = r.TryCast<MeshRenderer>();
                if (mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    return mf ? mf.sharedMesh : null;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Fast path: an active object with that name. Slow path: scan all loaded renderers once.</summary>
        private static GameObject FindModel(string name)
        {
            try
            {
                GameObject go = GameObject.Find(name);
                if (go && go.GetComponentsInChildren<Renderer>(true).Length > 0) return go;
            }
            catch { }
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!r || !MeshOf(r)) continue;
                GameObject g = r.gameObject;
                if (g.name == name) return g;
                Transform parent = g.transform.parent;
                if (parent && parent.name == name) return parent.gameObject;
            }
            return null;
        }

        private static void SetTemplate(GameObject source, string name)
        {
            RemoveAll();
            if (_template) Object.Destroy(_template);
            _template = BuildFromRoot(source);
            _templateFor = name;
            _native = false;
            Analyse(_template);
            _findTries = 0;
            RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] model '{name}' ready ({_template.GetComponentsInChildren<MeshRenderer>(true).Length} parts)");
        }

        private static GameObject BuildFromRoot(GameObject root)
        {
            if (!_holder)
            {
                _holder = new GameObject("LivingSoldiers_GunTemplates");
                _holder.SetActive(false);
                Object.DontDestroyOnLoad(_holder);
            }
            var copy = new GameObject("LivingSoldiers_Gun_" + root.name);
            copy.transform.SetParent(_holder.transform, false);

            Transform rt = root.transform;
            Quaternion invRot = Quaternion.Inverse(rt.rotation);
            Vector3 rootScale = rt.lossyScale;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r) continue;
                Mesh m = MeshOf(r);
                if (!m) continue;
                Transform t = r.transform;
                var part = new GameObject(r.gameObject.name);
                part.transform.SetParent(copy.transform, false);
                part.transform.localPosition = rt.InverseTransformPoint(t.position);
                part.transform.localRotation = invRot * t.rotation;
                Vector3 ls = t.lossyScale;
                part.transform.localScale = new Vector3(
                    rootScale.x != 0 ? ls.x / rootScale.x : 1f,
                    rootScale.y != 0 ? ls.y / rootScale.y : 1f,
                    rootScale.z != 0 ? ls.z / rootScale.z : 1f);
                part.AddComponent<MeshFilter>().sharedMesh = m;
                part.AddComponent<MeshRenderer>().sharedMaterials = r.sharedMaterials;
            }
            return copy;
        }

        private static int RemoveAll()
        {
            int n = 0;
            foreach (var st in Attached.Values)
            {
                if (!st.Gun) continue;
                Object.Destroy(st.Gun);
                n++;
            }
            Attached.Clear();
            return n;
        }

        internal static void Clear()
        {
            RemoveAll();
            if (_template) Object.Destroy(_template);
            _template = null;
            _templateFor = "";
            _cands = null;
            _findTries = 0;
            _nextFindTry = 0;
            _nextScan = 0;
        }

        // ------------------------------------------------------------------ searching (tmcombat gun list)

        private static List<string> Find(string word)
        {
            var lines = new List<string>();
            LogBundleOnce(lines);

            string[] words = string.IsNullOrEmpty(word) ? WeaponWords : new[] { word };
            var byRoot = new Dictionary<IntPtr, Candidate>();
            int scanned = 0;
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!r) continue;
                scanned++;
                Mesh m = MeshOf(r);
                if (!m) continue;
                GameObject go = r.gameObject;
                Transform parent = go.transform.parent;
                string text = go.name + " " + m.name + " " + (parent ? parent.name : "");
                if (!Matches(text, words) || Matches(text, SkipWords)) continue;

                GameObject root = parent && Matches(parent.name, words) && !Matches(parent.name, SkipWords) ? parent.gameObject : go;
                if (!byRoot.TryGetValue(root.Pointer, out var c))
                {
                    c = new Candidate { Root = root, Label = root.name };
                    byRoot[root.Pointer] = c;
                }
                c.Parts++;
                try { c.Verts += m.vertexCount; } catch { }
            }

            // Identical models (same name and detail) only once.
            _cands = byRoot.Values
                .GroupBy(c => c.Label + "|" + c.Verts)
                .Select(g => g.First())
                .OrderBy(c => c.Label).ThenByDescending(c => c.Verts).ToList();

            lines.Add($"{scanned} loaded models searched, {_cands.Count} gun candidates{(word != null ? $" for '{word}'" : "")}:");
            for (int i = 0; i < _cands.Count && i < 80; i++)
                lines.Add($"  {i}: {_cands[i].Label} - {_cands[i].Parts} parts, {_cands[i].Verts} vertices");
            if (_cands.Count > 80) lines.Add($"  ... {_cands.Count - 80} more - narrow it down with a search word");
            lines.Add(_cands.Count == 0 ? "Nothing found - the gun has to be loaded right now (e.g. in the inventory)." : "Use with: tmcombat gun <number>");
            return lines;
        }

        private static void LogBundleOnce(List<string> lines)
        {
            if (_bundleLogged) return;
            _bundleLogged = true;
            try
            {
                var bundle = BundleData.CharacterBundle.Bundle;
                if (!bundle) return;
                var all = new List<string>();
                foreach (var n in bundle.GetAllAssetNames()) all.Add(n);
                var guns = all.Where(n => Matches(n, WeaponWords)).ToList();
                lines.Add($"Mod bundle: {all.Count} files, of which guns: {(guns.Count == 0 ? "none" : string.Join(", ", guns))}");
            }
            catch { }
        }
    }
}
