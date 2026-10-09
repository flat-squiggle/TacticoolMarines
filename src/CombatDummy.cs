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
    /// Reference for the gun hold: a stripped copy of the game's remote-player rig (PlayerNetwork) - bones, meshes and
    /// the item locators only, no network or gameplay scripts - placed next to the staged soldier and posed with the
    /// same clip (AnimationClip.SampleAnimation every frame), with the rifle loaded into its own RifleHeldLocator.
    /// It shows how the game's own rig holds the rifle in that clip.
    ///   tmcombat dummy [clip|aim|held|fire|reload]   tmcombat dummy off
    /// </summary>
    internal static class CombatDummy
    {
        // ------------------------------------------------------------------ right hand sync
        // Measured 2026-10-07: in the same clip frame every bone of the soldier matches the player rig exactly except
        // the right hand (off by 106 degrees aiming, 44 carrying): his hand hangs below an extra twist bone
        // (RightForeArmTwistNew4), so the clips' right hand curve never reaches it. A hidden copy of the player rig is
        // posed with the soldier's clip and time every frame, and its right hand rotation (relative to the rig) is put
        // on the soldier's right hand. Then the game's own rifle hold fits exactly. Config combat_hand_sync.

        private static GameObject _ref;
        private static Transform _refRoot, _refHand, _refSocket;
        private static bool _refFailed;
        private static readonly Dictionary<IntPtr, (Transform root, Transform hand)> SoldierHands = new();

        /// <summary>Every frame after the animation, before the gun and feet: fix each soldier's right hand.</summary>
        internal static void SyncHands()
        {
            if (!Config.CombatHandSync.Value || _refFailed) return;
            foreach (var (a, _) in SoldierRegistry.All())
            {
                if (!a) continue;
                string pose = CombatLook.CurrentPose(a);
                if (pose == null) continue;
                AnimationClip clip = CombatLook.Clip(pose);
                if (!clip || CombatLook.ForeignClips.Contains(pose)) continue;
                if (!_ref && !BuildRef()) return;
                try
                {
                    if (!SoldierHands.TryGetValue(a.Pointer, out var h) || !h.hand)
                    {
                        Animator sa = CombatLook.AnimatorOf(a);
                        if (!sa) continue;
                        Transform hand = null;
                        foreach (var x in sa.GetComponentsInChildren<Transform>(true)) if (x.name == "RightHand") { hand = x; break; }
                        if (!hand) continue;
                        h = (sa.transform, hand);
                        SoldierHands[a.Pointer] = h;
                    }
                    Animator an = CombatLook.AnimatorOf(a);
                    float t = 0f;
                    try { t = (an.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f) * clip.length; } catch { }
                    clip.SampleAnimation(_refRoot.gameObject, t);
                    Quaternion rel = Quaternion.Inverse(_refRoot.rotation) * _refHand.rotation;
                    h.hand.rotation = h.root.rotation * rel;
                    // The clips also animate the weapon socket below the hand (RightHandWeapon) - per clip and frame.
                    // Measured: 114 degrees between its rest pose and the aim clip. Copy it onto the rebuilt socket.
                    if (_refSocket)
                    {
                        Transform sock = h.hand.Find("LivingSoldiers_PlayerSocket");
                        if (sock)
                        {
                            sock.localPosition = _refSocket.localPosition;
                            sock.localRotation = _refSocket.localRotation;
                        }
                    }
                }
                catch (Exception e)
                {
                    _refFailed = true;
                    RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] hand sync switched off: {e.Message}");
                    return;
                }
            }
        }

        private static bool BuildRef()
        {
            Transform src = CombatGun.NativeSourceRoot;
            if (!src) return false;
            try
            {
                var lines = new List<string>();
                GameObject go = StrippedRig(src, "LivingSoldiers_HandReference", lines);
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                go.transform.position = new Vector3(0f, -2000f, 0f);
                Animator an = go.GetComponentInChildren<Animator>(true);
                _refRoot = an ? an.transform : go.transform;
                foreach (var x in go.GetComponentsInChildren<Transform>(true)) if (x.name == "RightHand") { _refHand = x; break; }
                if (_refHand) _refSocket = _refHand.Find("RightHandWeapon");
                if (!_refHand) { _refFailed = true; Object.Destroy(go); RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] hand sync: no RightHand in the player rig"); return false; }
                _ref = go;
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] hand sync active (reference '{src.name}', {lines[0]})");
                return true;
            }
            catch (Exception e)
            {
                _refFailed = true;
                RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] hand sync: {e.Message}");
                return false;
            }
        }

        /// <summary>A copy of a rig with only bones, meshes, animator and item locators left (no scripts run).</summary>
        private static GameObject StrippedRig(Transform src, string name, List<string> lines)
        {
            var holder = new GameObject(name + "Holder");
            holder.SetActive(false);
            Object.DontDestroyOnLoad(holder);
            GameObject go = Object.Instantiate(src.gameObject, holder.transform, false);
            go.name = name;
            int removed = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (!c) continue;
                    string n;
                    try { n = c.GetIl2CppType().Name; } catch { continue; }
                    bool keep = false;
                    foreach (var k in Keep) if (n == k || n.StartsWith(k)) keep = true;
                    if (keep) continue;
                    try { Object.DestroyImmediate(c); removed++; } catch { }
                }
            }
            go.transform.SetParent(null, false);
            Object.DontDestroyOnLoad(go);
            Object.Destroy(holder);
            var an = go.GetComponentInChildren<Animator>(true);
            if (an) an.enabled = false;
            go.SetActive(true);
            lines.Add($"{removed} scripts removed");
            return go;
        }

        // ------------------------------------------------------------------ visible reference (test only)

        private static GameObject _dummy;
        private static Animator _anim;
        private static AnimationClip _clip;
        private static readonly string[] Keep = { "Transform", "Animator", "SkinnedMeshRenderer", "MeshRenderer", "MeshFilter", "LODGroup", "ItemRenderable" };

        private static float _nextMeasure;
        private static readonly Dictionary<string, Transform> RefBones = new();

        internal static void Clear()
        {
            Remove();
            if (_ref) Object.Destroy(_ref);
            _ref = null;
            _refFailed = false;
            SoldierHands.Clear();
        }

        internal static void Remove()
        {
            if (_dummy) Object.Destroy(_dummy);
            _dummy = null;
            _clip = null;
        }
    }
}
