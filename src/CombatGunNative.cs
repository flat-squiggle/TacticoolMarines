using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Items.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CharacterReplacement
{
    /// <summary>
    /// The gun the way the game itself holds it. The soldier's skeleton has the player's bone names and plays the
    /// player's third-person (multiplayer) clips, so the game already knows how a rifle sits in his
    /// "RightHandWeapon" socket: that is how a remote player's held rifle is shown. This finds such a held rifle
    /// object in memory and copies it, with its exact pose relative to the socket, into each soldier's socket.
    /// Search order (all candidates are logged):
    ///   1. BoltHeldItem objects (remote players' third-person held items) below a RightHandWeapon socket
    ///   2. ItemRenderable objects below a RightHandWeapon socket (any rig with the player's socket)
    ///   3. the rifle item's HeldPrefab (ItemData.HeldPrefab), its root placed on the socket
    /// The copy keeps only renderers and the game's ItemRenderable loader (which streams the real mesh in);
    /// weapon scripts, colliders and network components are removed. Config combat_gun_model = "auto" uses this,
    /// with the old model (GetDownSonRifleRig, fitted by hand and IK) as the fallback.
    /// </summary>
    internal static partial class CombatGun
    {
        private const string Socket = "RightHandWeapon";

        private sealed class NativeCandidate
        {
            public GameObject Go;
            public string Source, Item, Path;
            public int ItemId;
            public Transform SocketT;
            public int Renderers;
            public int Rank;
        }

        private static bool _native;                 // soldiers carry the game's own held rifle
        private static Vector3 _nPos, _nScale = Vector3.one;
        private static Quaternion _nRot = Quaternion.identity;
        private static string _nativeFrom = "";
        private static bool _nativeSignKnown;
        private static int _signVote;
        private static Transform _nativeSocket;
        private static readonly Dictionary<IntPtr, float> AimYaw = new();

        /// <summary>Degrees the aiming rifle points to the right of his body's forward direction (0 if unknown).</summary>
        internal static float AimYawOffset(VailActor a) => 0f; // the upper body turns instead (see FitNativeHand)
        // The source rig's hand socket relative to its hand bone. The soldier's own RightHandWeapon sits elsewhere on
        // his hand (custom model: other offset and rotation), so the player's socket is rebuilt on his RightHand.
        private static Vector3 _sockPos;
        private static Quaternion _sockRot = Quaternion.identity;
        private static bool _sockKnown;
        private const string PlayerSocketName = "LivingSoldiers_PlayerSocket";
        // On the player rig RightHandWeapon is ParentConstrained to RightHand/RightHandWeaponConstraint, which the
        // clips can animate per pose. The same transform, by the same name, is created on the soldier's hand so the
        // clips drive it there too (the animator is rebound once for that).
        private const string ConstraintName = "RightHandWeaponConstraint";
        private static Vector3 _conPos;
        private static Quaternion _conRot = Quaternion.identity;
        private static bool _conKnown;
        private static readonly HashSet<IntPtr> Rebound = new();
        // Tested 2026-10-07: with the gun on RightHandWeaponConstraint the rifle is turned round (stock forward); the
        // constraint is inactive for the rifle in the game. Kept switchable for other items.
        private const bool UseConstraint = false;
        private static readonly Dictionary<IntPtr, Transform> ConstraintOf = new();

        /// <summary>The player's hand socket rebuilt on this soldier's hand bone (created on first use).</summary>
        internal static Transform PlayerSocket(VailActor actor)
        {
            Transform own = RightHand(actor);
            if (!own) return null;
            if (!_sockKnown) return own;
            Transform hand = own.parent;
            if (!hand) return own;
            if (_conKnown)
            {
                Transform con = hand.Find(ConstraintName);
                if (!con)
                {
                    con = new GameObject(ConstraintName).transform;
                    con.SetParent(hand, false);
                    con.localPosition = _conPos;
                    con.localRotation = _conRot;
                    con.localScale = Vector3.one;
                }
                ConstraintOf[actor.Pointer] = con;
                if (Rebound.Add(actor.Pointer))
                {
                    // Bind the clips' curves to the new transform. Rebind resets the animator; the pose states are
                    // entered again by whoever plays them next.
                    try { var an = CombatLook.AnimatorOf(actor); if (an) an.Rebind(); } catch { }
                    CombatLook.ForgetPose(actor);
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] {actor.name}: {ConstraintName} put on the hand, animator rebound");
                }
                return con;
            }
            Transform s = hand.Find(PlayerSocketName);
            if (!s)
            {
                s = new GameObject(PlayerSocketName).transform;
                s.SetParent(hand, false);
            }
            s.localPosition = _sockPos;
            s.localRotation = _sockRot;
            s.localScale = Vector3.one;
            return s;
        }

        internal static bool NativeActive => _native;

        /// <summary>Root of the rig the held rifle was found on (PlayerNetwork), for the reference rig.</summary>
        internal static Transform NativeSourceRoot => _nativeSocket ? _nativeSocket.root : null;

        private static string ItemName(int id)
        {
            try { var d = ItemDatabaseManager.ItemById(id); return d != null ? d.Name ?? "" : ""; }
            catch { return ""; }
        }

        private static Transform SocketAbove(Transform t)
        {
            for (Transform p = t; p && p != p.root; p = p.parent)
                if (p.name == Socket) return p;
            return null;
        }

        private static string PathOf(Transform t)
        {
            string s = t.name;
            int n = 0;
            for (Transform p = t.parent; p && n < 6; p = p.parent, n++) s = p.name + "/" + s;
            return s;
        }

        private static bool IsRifle(string name) =>
            name.IndexOf("rifle", StringComparison.OrdinalIgnoreCase) >= 0 &&
            name.IndexOf("ammo", StringComparison.OrdinalIgnoreCase) < 0 &&
            name.IndexOf("scope", StringComparison.OrdinalIgnoreCase) < 0 &&
            name.IndexOf("dart", StringComparison.OrdinalIgnoreCase) < 0;

        private static bool OurOwn(Transform t)
        {
            for (Transform p = t; p; p = p.parent)
                if (p.name.StartsWith("LivingSoldier", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Every held rifle object the game has in memory, best first. Logs all of them when log is set.</summary>
        private static List<NativeCandidate> NativeCandidates(List<string> log)
        {
            var list = new List<NativeCandidate>();
            var seen = new HashSet<IntPtr>();

            void Add(GameObject go, string source, int id)
            {
                if (!go || !seen.Add(go.Pointer) || OurOwn(go.transform)) return;
                string item = ItemName(id);
                if (!IsRifle(item) && !IsRifle(go.name)) return;
                Transform sock = SocketAbove(go.transform);
                int rend = 0;
                try { rend = go.GetComponentsInChildren<Renderer>(true).Length; } catch { }
                var c = new NativeCandidate { Go = go, Source = source, ItemId = id, Item = item, Path = PathOf(go.transform), SocketT = sock, Renderers = rend };
                // third-person remote-player hold first, then any socket hold; something visible beats a placeholder
                c.Rank = (source == "BoltHeldItem" ? 0 : source == "ItemRenderable" ? 10 : 20) + (sock ? 0 : 50) + (rend > 0 ? 0 : 5);
                list.Add(c);
            }

            try { foreach (var b in Resources.FindObjectsOfTypeAll<BoltHeldItem>()) if (b) Add(b.gameObject, "BoltHeldItem", SafeId(() => b.Id)); }
            catch (Exception e) { log?.Add($"BoltHeldItem search: {e.Message}"); }
            try { foreach (var r in Resources.FindObjectsOfTypeAll<ItemRenderable>()) if (r) Add(r.gameObject, "ItemRenderable", SafeId(() => r.ItemId)); }
            catch (Exception e) { log?.Add($"ItemRenderable search: {e.Message}"); }

            // The rifle's own held prefab (made for the player's hand socket).
            try
            {
                foreach (var d in ItemDatabaseManager.Items)
                {
                    if (d == null || !IsRifle(d.Name ?? "")) continue;
                    log?.Add($"Item {d.Id} '{d.Name}': HeldPrefab {(d.HeldPrefab ? d.HeldPrefab.name : "-")}, PropPrefab {(d.PropPrefab ? d.PropPrefab.name : "-")}");
                    if (d.HeldPrefab) Add(d.HeldPrefab.gameObject, "HeldPrefab", d.Id);
                }
            }
            catch (Exception e) { log?.Add($"Item database: {e.Message}"); }

            list.Sort((a, b) => a.Rank.CompareTo(b.Rank));
            if (log != null)
            {
                log.Add($"{list.Count} held rifle objects in memory:");
                foreach (var c in list)
                    log.Add($"  [{c.Source}] {c.Path} - item {c.ItemId} '{c.Item}', {(c.SocketT ? "on " + Socket : "without a hand socket")}, {c.Renderers} Renderer");
            }
            return list;
        }

        private static int SafeId(Func<int> f) { try { return f(); } catch { return 0; } }

        /// <summary>Picks the best candidate at start (combat_gun_model = auto). False = none, use the fallback model.</summary>
        private static bool FindNative()
        {
            var lines = new List<string>();
            var list = NativeCandidates(lines);
            foreach (var l in lines) RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] {l}");
            return list.Count > 0 && TryUseNative(list[0], null);
        }

        private static bool TryUseNative(NativeCandidate c, List<string> lines)
        {
            try
            {
                // Pose relative to the hand socket; a held prefab without a socket is made for the socket at its root.
                _nativeSocket = c.SocketT;
                _sockKnown = false;
                _conKnown = false;
                Transform conSrc = c.SocketT && c.SocketT.parent ? c.SocketT.parent.Find(ConstraintName) : null;
                if (conSrc && UseConstraint)
                {
                    _conPos = conSrc.localPosition;
                    _conRot = conSrc.localRotation;
                    _conKnown = true;
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] source {ConstraintName}: pos {_conPos.x:0.####} {_conPos.y:0.####} {_conPos.z:0.####} rot {_conRot.eulerAngles.x:0.#} {_conRot.eulerAngles.y:0.#} {_conRot.eulerAngles.z:0.#}");
                }
                if (c.SocketT && c.SocketT.parent && c.SocketT.parent.name.Contains("RightHand"))
                {
                    _sockPos = c.SocketT.localPosition;
                    _sockRot = c.SocketT.localRotation;
                    _sockKnown = true;
                }
                if (c.SocketT)
                {
                    Transform s = c.SocketT, g = c.Go.transform;
                    _nPos = s.InverseTransformPoint(g.position);
                    _nRot = Quaternion.Inverse(s.rotation) * g.rotation;
                    Vector3 ls = s.lossyScale, gs = g.lossyScale;
                    _nScale = new Vector3(ls.x != 0 ? gs.x / ls.x : 1f, ls.y != 0 ? gs.y / ls.y : 1f, ls.z != 0 ? gs.z / ls.z : 1f);
                }
                else
                {
                    _nPos = Vector3.zero;
                    _nRot = Quaternion.identity;
                    _nScale = Vector3.one;
                }

                RemoveAll();
                if (_template) Object.Destroy(_template);
                _template = StrippedCopy(c.Go);
                _templateFor = "auto";
                _native = true;
                _nativeSignKnown = false;
                _analysed = false;
                _nativeFrom = $"[{c.Source}] {c.Path}";
                string msg = $"Gun held as in the game: {_nativeFrom} (item {c.ItemId} '{c.Item}'), player socket on the hand {(_sockKnown ? $"{_sockPos.x:0.###} {_sockPos.y:0.###} {_sockPos.z:0.###} / {_sockRot.eulerAngles.x:0.#} {_sockRot.eulerAngles.y:0.#} {_sockRot.eulerAngles.z:0.#}" : "unknown")}, pose at the socket {_nPos.x:0.###} {_nPos.y:0.###} {_nPos.z:0.###} / {_nRot.eulerAngles.x:0.#} {_nRot.eulerAngles.y:0.#} {_nRot.eulerAngles.z:0.#}";
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] {msg}");
                lines?.Add(msg);
                return true;
            }
            catch (Exception e)
            {
                RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] taking over the game's held object: {e.Message}");
                lines?.Add("Taking over failed: " + e.Message);
                _native = false;
                return false;
            }
        }

        private static readonly string[] KeepScripts = { "ItemRenderable", "LOD" };

        /// <summary>A copy below an inactive holder (so no script wakes up), keeping only what draws the gun.</summary>
        private static GameObject StrippedCopy(GameObject src)
        {
            if (!_holder)
            {
                _holder = new GameObject("LivingSoldiers_GunTemplates");
                _holder.SetActive(false);
                Object.DontDestroyOnLoad(_holder);
            }
            GameObject go = Object.Instantiate(src, _holder.transform, false);
            go.name = "LivingSoldiers_NativeGun";
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            var removed = new List<string>();
            // Several passes: components that others depend on can only go after those.
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (!c) continue;
                    string n;
                    try { n = c.GetIl2CppType().Name; } catch { continue; }
                    if (n == "Transform" || n.Contains("Renderer") || n == "MeshFilter") continue;
                    bool keep = false;
                    foreach (var k in KeepScripts) if (n.Contains(k)) keep = true;
                    if (keep) continue;
                    try { Object.DestroyImmediate(c); if (pass == 0 || !removed.Contains(n)) removed.Add(n); } catch { }
                }
            }
            if (removed.Count > 0) RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] removed from the copy: {string.Join(", ", removed)}");
            return go;
        }

        /// <summary>
        /// The game shows held items at world scale 1 (the rifle mesh is about 1 unit = 1 m long), not at the hand
        /// socket's scale (0.4 on this rig): the copy below the socket gets the inverse of the socket's scale.
        /// </summary>
        internal static Vector3 NativeLocalScale(Transform parent)
        {
            if (!parent) return Vector3.one;
            Vector3 ps = parent.lossyScale;
            return new Vector3(ps.x != 0 ? 1f / ps.x : 1f, ps.y != 0 ? 1f / ps.y : 1f, ps.z != 0 ? 1f / ps.z : 1f);
        }

        // ------------------------------------------------------------------ every frame (native gun)

        /// <summary>In the hand: exactly the game's pose in the socket, plus recoil and the aim turn of the upper body.</summary>
        private static void FitNativeHand(GunState st)
        {
            FindBones(st);
            Transform gun = st.Gun.transform;
            gun.localPosition = _nPos;
            gun.localRotation = _nRot;
            gun.localScale = NativeLocalScale(gun.parent);
            if (!st.NativeReady) TryAnalyseNative(st);
            string pose = CombatLook.CurrentPose(st.Actor);
            bool aiming = pose != null && (pose.Contains("Aim") || pose.Contains("Fire")) && !pose.Contains("Reload");
            if (_analysed && aiming && st.Actor.transform)
            {
                // In the game's own hold the muzzle points the way he faces. Checked on every aiming frame (a frame
                // before the right hand sync took effect once fixed the wrong end); a change needs ~0.5 s of evidence.
                Vector3 axis = gun.rotation * Axis(_barrelAxis);
                float d = Vector3.Dot(axis, st.Actor.transform.forward);
                _signVote = Mathf.Clamp(_signVote + (d >= 0f ? 1 : -1), -30, 30);
                float want = _signVote >= 0 ? 1f : -1f;
                if (!_nativeSignKnown || (want != _barrelSign && Mathf.Abs(_signVote) >= 30))
                {
                    _barrelSign = want;
                    _nativeSignKnown = true;
                    ApplyAxes();
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] game gun: {FitInfo()}");
                }
            }
            if (!(_analysed && _nativeSignKnown)) return;
            if (!st.ChainsSearched) FindChains(st);
            Transform socket = gun.parent;

            // How far the clip's own rifle line is turned from where his body faces (a bladed stance: the rifle points
            // well to the right of the hips). The fight turns his body by this so the rifle, held as the game holds
            // it, points at the target; only the rest is corrected on the gun below.
            if (aiming)
            {
                Vector3 raw = Quaternion.Inverse(st.Actor.transform.rotation) * (gun.rotation * BarrelLocal);
                float yaw = Mathf.Atan2(raw.x, raw.z) * Mathf.Rad2Deg;
                AimYaw.TryGetValue(st.Actor.Pointer, out float prev);
                AimYaw[st.Actor.Pointer] = Mathf.Abs(prev) < 0.001f ? yaw : Mathf.LerpAngle(prev, yaw, 0.1f);
            }

            // Aiming: the game's hold stays as it is in the hand; the upper body turns (at most 45 degrees, like a
            // shooter twisting at the waist) so the barrel points at the target. The gun, both arms and the head go
            // with the spine.
            if (aiming) AimCorrection(st, gun, 25f);
            else st.AimFix = Quaternion.Slerp(st.AimFix, Quaternion.identity, 0.3f);

            // Recoil kick along the barrel.
            float k = KickAmount(st);
            if (k > 0f)
            {
                Vector3 bw = gun.rotation * BarrelLocal;
                gun.position -= bw * (0.05f * k);
                gun.rotation = Quaternion.AngleAxis(-4f * k, gun.rotation * Vector3.Cross(UpLocal, BarrelLocal)) * gun.rotation;
            }

            // Left hand onto the fore-end (the game does this with IK targets as well). Not while reloading - the
            // reload clip takes the left hand to the magazine.
            bool rifleClip = pose != null && !pose.Contains("Reload");
            // With the right hand synced the clip already puts the left hand on the gun; IK only without the sync.
            bool needIk = !Config.CombatHandSync.Value;
            st.IkWeight = Mathf.MoveTowards(st.IkWeight, needIk && rifleClip && st.LChain != null && st.L && Config.CombatGunIk.Value ? 1f : 0f, Time.deltaTime * 6f);
            if (st.IkWeight > 0f && st.LChain != null && st.L)
            {
                // The gun is a child of the right hand, which the left arm does not move: its pose is final here.
                HandOnto(st.LChain, st.L, gun.TransformPoint(ForendLocal), Quaternion.identity, st.IkWeight);
            }
        }

        /// <summary>Measure the copy once its mesh is there (the game's loader may stream it in after a moment).</summary>
        private static void Preload(GunState st)
        {
            if (_native && !st.NativeReady) TryAnalyseNative(st);
        }

        private static void TryAnalyseNative(GunState st)
        {
            if (st.Gun.GetComponentsInChildren<MeshFilter>(true).Length == 0 && st.Gun.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0)
            {
                st.NativeWait += Time.deltaTime;
                // The game's loader does not stream the model in by itself: load its renderable asset ourselves - right
                // when the copy is made (was: 1.5 s after it first went into the hand, so the rifle appeared late).
                if (!st.NativeLoadAsked)
                {
                    st.NativeLoadAsked = true;
                    int asked = 0;
                    foreach (var ir in st.Gun.GetComponentsInChildren<ItemRenderable>(true))
                    {
                        try
                        {
                            var ar = ir.GetCurrentRenderable();
                            if (ar != null && ar.RuntimeKeyIsValid()) { ar.InstantiateAsync(ir.transform, false); asked++; }
                        }
                        catch (Exception e) { if (FitLogged.Add("addr" + e.Message)) RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] loading the model: {e.Message}"); }
                    }
                    if (FitLogged.Add("addrasked")) RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] game gun without a model - {asked} model(s) requested through Addressables");
                }
                if (st.NativeWait > 6f && FitLogged.Add("nativeempty"))
                    RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] the game gun still shows no model after 6 s ({_nativeFrom}) - 'tmcombat gun native' lists all candidates, combat_gun_model = GetDownSonRifleRig uses the old model");
                return;
            }
            // Per gun: the model is there - remove its colliders; measure the model once for all.
            st.NativeReady = true;
            StripPhysics(st.Gun);
            if (!_analysed) Analyse(st.Gun, useConfig: false);
        }

        /// <summary>
        /// The model the game streams in may bring its pickup colliders. On a soldier they hang about his legs and
        /// his ground checks and the foot placement read them as rocks (he "climbed" over nothing): remove them.
        /// </summary>
        internal static void StripPhysics(GameObject go)
        {
            int n = 0;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) { if (c) { Object.Destroy(c); n++; } }
            foreach (var r in go.GetComponentsInChildren<Rigidbody>(true)) { if (r) { Object.Destroy(r); n++; } }
            if (n > 0 && FitLogged.Add("strip" + n)) RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] {n} colliders/rigidbodies removed from the rifle model");
        }
    }
}
