using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using SonsSdk.Networking;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Fixes for the soldier model playing Kelvin's own animations (measured 2026-10-07 against Kelvin's rig, which is
    /// still on the actor, switched off):
    ///   - Hands: the model's hands rest ~33 degrees turned about the forearm compared with Kelvin's (right 36, left
    ///     31), so his clips showed the backs of the hands to the front. After each animation frame the hands are
    ///     turned by (Kelvin bind)^-1 * (model bind), measured relative to ForeArmTwistNew3. Both rigs hang the right
    ///     hand below an extra RightForeArmTwistNew4, which is held in the model's bind pose (it sat ~175 degrees off).
    ///     Only for his own clips; in combat poses (player clips) CombatDummy.SyncHands sets the right hand.
    ///   - Face: 17 of his clips turn the jaw ~120 degrees off this model's bind pose (mouth torn through the mask);
    ///     face bones are held in their bind pose.
    /// Config model_fix_hand, combat_face_lock. Test switches: tmcombat handfix on|off|test.
    /// </summary>
    internal static class ModelRig
    {
        private sealed class Fix
        {
            public Animator Animator;
            public Transform Hand, LeftHand;
            // Kelvin's clips are made for Kelvin's hand bones, whose resting orientation differs from this model's:
            // the same rotation value turns this model's hand another way. Correction = Kelvin bind^-1 * model bind.
            public Quaternion RightFix = Quaternion.identity, LeftFix = Quaternion.identity;
            public bool LeftNeedsFix;
            public readonly List<(Transform bone, Quaternion rot, Vector3 pos)> Hold = new();
        }

        private static readonly Dictionary<IntPtr, Fix> Fixed = new();
        private static readonly HashSet<IntPtr> Skip = new();
        private static float _nextCheck;


        internal static void LateTick()
        {
            if (NetUtils.IsDedicatedServer || (!Config.ModelFixHand.Value && !Config.CombatFaceLock.Value)) return;
            float now = Time.time;
            bool check = now >= _nextCheck;
            if (check) _nextCheck = now + 1f;
            foreach (var (a, _) in SoldierRegistry.All())
            {
                if (!a) continue;
                if (Fixed.TryGetValue(a.Pointer, out var f))
                {
                    Animator an = CombatLook.AnimatorOf(a);
                    if (an && f.Animator && an.Pointer == f.Animator.Pointer)
                    {
                        Apply(a, f);
                        continue;
                    }
                    Fixed.Remove(a.Pointer); // model swapped: look again
                }
                if (!check || Skip.Contains(a.Pointer)) continue;
                // Rebind resets the animator: only while he is not in one of our poses.
                if (CombatLook.CurrentPose(a) != null || CombatAI.IsEngaged(a)) continue;
                try { Repair(a); }
                catch (Exception e)
                {
                    Skip.Add(a.Pointer);
                    RLog.Warning($"{CharacterReplacement.Tag} [Model] fixing the hand of {a.name}: {e.Message}");
                }
            }
        }

        private static void Apply(VailActor a, Fix f)
        {
            string pose = CombatLook.CurrentPose(a);
            bool own = pose == null || CombatLook.ForeignClips.Contains(pose);
            // Hand as his (Kelvin) animation left it, before the twist bone is put back (that would carry the hand).
            Quaternion right = f.Hand ? f.Hand.rotation : Quaternion.identity;
            foreach (var h in f.Hold)
                if (h.bone) { h.bone.localRotation = h.rot; h.bone.localPosition = h.pos; }
            if (!own || !UseKelvinFix) { if (f.Hand) f.Hand.rotation = right; return; }
            // Kelvin's clips are made for his hand bones, whose resting orientation differs from this model's.
            if (f.Hand) { f.Hand.rotation = right * f.RightFix * TestTwist; LastSet[a.Pointer] = (f.Hand, f.Hand.rotation, right); }
            if (f.LeftNeedsFix && f.LeftHand) f.LeftHand.localRotation = f.LeftHand.localRotation * f.LeftFix;
        }

        private static void Repair(VailActor a)
        {
            Animator an = CombatLook.AnimatorOf(a);
            if (!an || !an.gameObject.activeInHierarchy) return;
            var byName = new Dictionary<string, Transform>();
            foreach (var t in an.GetComponentsInChildren<Transform>(true)) byName.TryAdd(t.name, t);
            var bind = BindWorld(an.transform);
            var f = new Fix { Animator = an };
            foreach (string side in new[] { "Right", "Left" })
            {
                if (!byName.TryGetValue(side + "ForeArmTwistNew4", out var t4) || !t4.parent) continue;
                // The extra twist bone: nothing animates it, hold it in its bind pose.
                if (bind.TryGetValue(t4.parent.Pointer, out var bp) && bind.TryGetValue(t4.Pointer, out var b4))
                {
                    Matrix4x4 l4 = bp.inverse * b4;
                    f.Hold.Add((t4, l4.rotation, (Vector3)l4.GetColumn(3)));
                }
            }
            // Face: 17 of Kelvin's clips (talking, eating, emotes, the note pad, resting ...) turn the jaw bone ~120
            // degrees from this model's bind pose - the mouth tore through the mask. The soldier wears a mask, so the
            // face bones are simply held in their bind pose. Config combat_face_lock.
            if (Config.CombatFaceLock.Value && byName.TryGetValue("Head", out var head))
            {
                foreach (var t in head.GetComponentsInChildren<Transform>(true))
                {
                    if (t == head || !t.parent || !IsFaceBone(t.name)) continue;
                    if (bind.TryGetValue(t.parent.Pointer, out var bpar) && bind.TryGetValue(t.Pointer, out var bt))
                    {
                        Matrix4x4 l = bpar.inverse * bt;
                        f.Hold.Add((t, l.rotation, (Vector3)l.GetColumn(3)));
                    }
                }
            }
            MoveHitZones(a, an, byName);
            MoveTools(a, an);
            InflateVest(a, an);
            if (Config.ModelFixHand.Value) KelvinFix(a, an, byName, bind, f);
            if (!f.Hand && f.Hold.Count == 0) { Skip.Add(a.Pointer); return; }
            Fixed[a.Pointer] = f;
        }

        /// <summary>
        /// The hit zones (DamageNode + collider per body part) stay on Kelvin's original body, which is hidden and
        /// switched off after the model swap - measured: all 11 inactive, so nothing could hit a soldier (player's
        /// axe, cannibal clubs). They are moved onto the soldier model's bone of the same name, keeping their offset.
        /// </summary>
        private static void MoveHitZones(VailActor a, Animator an, Dictionary<string, Transform> mine)
        {
            int moved = 0, missing = 0;
            foreach (var dn in a.GetComponentsInChildren<Sons.Weapon.DamageNode>(true))
            {
                try
                {
                    Transform t = dn.transform;
                    if (t.IsChildOf(an.transform)) continue;
                    if (!t.parent || !mine.TryGetValue(t.parent.name, out var bone)) { missing++; continue; }
                    t.SetParent(bone, false);
                    t.gameObject.SetActive(true);
                    foreach (var c in t.GetComponentsInChildren<Collider>(true)) c.enabled = true;
                    moved++;
                }
                catch { missing++; }
            }
            if (moved + missing > 0)
                RLog.Msg($"{CharacterReplacement.Tag} [Model] {a.name}: {moved} hit zones moved onto the soldier model{(missing > 0 ? $", {missing} without a matching bone" : "")}");
        }

        /// <summary>
        /// Kelvin's held tools (axe for chopping, repair tool) hang on the hand of his original body, which is hidden
        /// after the model swap - he chopped with an empty hand. Moved onto the soldier model's weapon socket with the
        /// same pose; the game still switches them on and off.
        /// </summary>
        private static void MoveTools(VailActor a, Animator an)
        {
            var r = CombatOrders.RobbyOf(a);
            if (!r || !r._robbyRightHand) return;
            int moved = 0;
            foreach (var go in new[] { r._axeHeld, r._repairToolHeld })
            {
                try
                {
                    if (!go || go.transform.IsChildOf(an.transform)) continue;
                    Transform t = go.transform;
                    Vector3 lp = t.localPosition; Quaternion lr = t.localRotation; Vector3 ls = t.localScale;
                    t.SetParent(r._robbyRightHand, false);
                    t.localPosition = lp; t.localRotation = lr; t.localScale = ls;
                    moved++;
                }
                catch { }
            }
            if (moved > 0) RLog.Msg($"{CharacterReplacement.Tag} [Model] {a.name}: {moved} tool(s) (axe, repair tool) attached to the soldier model's hand");
        }

        /// <summary>
        /// The jacket showed through the back of the vest. The meshes cannot be edited at runtime (not readable), so the
        /// vest is skinned to its own helper bones instead: one child per bone, scaled up a little (model_vest_inflate)
        /// and moved back a little (model_vest_back, metres) - the vest sits just outside the jacket, moving as before.
        /// </summary>
        private static void InflateVest(VailActor a, Animator an)
        {
            float s = Config.ModelVestInflate.Value;
            float depth = Config.ModelVestDepth.Value;
            if (s <= 1.0001f && depth <= 1.0001f) return;
            Transform geo = an.transform.Find("GEO");
            var smr = geo ? geo.Find("body_armor")?.GetComponent<SkinnedMeshRenderer>() : null;
            if (!smr) return;
            var bones = smr.bones;
            if (bones == null || bones.Length == 0 || (bones[0] && bones[0].name.StartsWith("LivingSoldiers_Vest"))) return;
            Vector3 center = VestCenter(an, smr);
            var fresh = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>(bones.Length);
            for (int i = 0; i < bones.Length; i++)
            {
                Transform b = bones[i];
                if (!b) { fresh[i] = b; continue; }
                var child = new GameObject("LivingSoldiers_Vest_" + b.name).transform;
                child.SetParent(b, false);
                SetVestHelper(child, center, an.transform.forward, s, depth);
                fresh[i] = child;
            }
            smr.bones = fresh;
            if (smr.rootBone)
            {
                for (int i = 0; i < bones.Length; i++) if (bones[i] == smr.rootBone) { smr.rootBone = fresh[i]; break; }
            }
            RLog.Msg($"{CharacterReplacement.Tag} [Model] {a.name}: vest slightly wider (sides x{s:0.###}, front/back x{depth:0.###}) - the jacket no longer shows through");
        }

        /// <summary>Middle of the torso: the vest's own bounds centre (in the pose he is in).</summary>
        private static Vector3 VestCenter(Animator an, SkinnedMeshRenderer smr)
        {
            Vector3 c = smr.bounds.center;
            return c;
        }

        /// <summary>
        /// The helper bone scales the vest around the middle of the torso (not around the bone, which sits near the
        /// back - that grew the vest only at the front): "depth" along the body's front/back axis, "side" otherwise.
        /// </summary>
        private static void SetVestHelper(Transform child, Vector3 centerWorld, Vector3 forwardWorld, float side, float depth)
        {
            Transform bone = child.parent;
            Vector3 c = bone.InverseTransformPoint(centerWorld);
            // the bone's local axis that is closest to the body's front/back direction
            Vector3 fl = bone.InverseTransformDirection(forwardWorld).normalized;
            int k = Mathf.Abs(fl.x) >= Mathf.Abs(fl.y) && Mathf.Abs(fl.x) >= Mathf.Abs(fl.z) ? 0 : Mathf.Abs(fl.y) >= Mathf.Abs(fl.z) ? 1 : 2;
            Vector3 sc = new Vector3(side, side, side);
            sc[k] = depth;
            child.localRotation = Quaternion.identity;
            child.localScale = sc;
            child.localPosition = c - Vector3.Scale(sc, c); // the torso centre stays where it is
        }

        /// <summary>Switch for tests (tmcombat handfix): apply the Kelvin-to-model hand correction.</summary>
        internal static bool UseKelvinFix = true;
        internal static Quaternion TestTwist = Quaternion.identity;
        internal static readonly Dictionary<IntPtr, (Transform hand, Quaternion set, Quaternion before)> LastSet = new();

        /// <summary>
        /// Compares the hands' bind pose (relative to TwistNew3) of this model with Kelvin's own rig, which is still
        /// on the actor (switched off). Kelvin's clips write rotations meant for his bones.
        /// </summary>
        private static void KelvinFix(VailActor a, Animator an, Dictionary<string, Transform> mine, Dictionary<IntPtr, Matrix4x4> bind, Fix f)
        {
            Transform kelvin = null;
            foreach (var t in a.GetComponentsInChildren<Transform>(true))
                if (t.name == "RobbyRig" && !t.IsChildOf(an.transform)) { kelvin = t; break; }
            if (!kelvin) { RLog.Msg($"{CharacterReplacement.Tag} [Model] {a.name}: Kelvin's skeleton not found"); return; }
            var kb = BindWorld(kelvin);
            var kn = new Dictionary<string, Transform>();
            foreach (var t in kelvin.GetComponentsInChildren<Transform>(true)) kn.TryAdd(t.name, t);
            foreach (string side in new[] { "Right", "Left" })
            {
                // Measured relative to ForeArmTwistNew3 (both rigs have it); on the right the hand hangs below the
                // extra TwistNew4 in both rigs, so this covers twist bone and hand together.
                string refName = side + "ForeArmTwistNew3";
                if (!kn.TryGetValue(side + "Hand", out var kh) || !kn.TryGetValue(refName, out var kr)) continue;
                if (!mine.TryGetValue(side + "Hand", out var mh) || !mine.TryGetValue(refName, out var mr)) continue;
                if (!kb.TryGetValue(kh.Pointer, out var kw) || !kb.TryGetValue(kr.Pointer, out var krw)) continue;
                if (!bind.TryGetValue(mh.Pointer, out var mw) || !bind.TryGetValue(mr.Pointer, out var mrw)) continue;
                Quaternion K = (krw.inverse * kw).rotation;
                Quaternion M = (mrw.inverse * mw).rotation;
                Quaternion fix = Quaternion.Inverse(K) * M;
                float ang = Quaternion.Angle(K, M);
                RLog.Msg($"{CharacterReplacement.Tag} [Model] {a.name}: {side}Hand rest pose relative to {refName}: Kelvin {K.eulerAngles} / model {M.eulerAngles} = {ang:0} degrees (path Kelvin {kh.parent.name}, model {mh.parent.name})");
                if (side == "Right") { f.Hand = mh; f.RightFix = fix; }
                else { f.LeftHand = mh; f.LeftFix = fix; f.LeftNeedsFix = ang > 3f && kh.parent.name == mh.parent.name; }
            }
        }

        private static readonly string[] FaceWords = { "Jaw", "Lip", "Mouth", "Tongue", "Teeth", "Cheek", "Brow", "Eyelid", "Chin" };

        private static bool IsFaceBone(string n)
        {
            foreach (var w in FaceWords) if (n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>World matrix of every skinned bone at bind time (from the meshes' bind poses).</summary>
        internal static Dictionary<IntPtr, Matrix4x4> BindWorld(Transform root)
        {
            var res = new Dictionary<IntPtr, Matrix4x4>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                try
                {
                    var mesh = smr.sharedMesh;
                    if (!mesh) continue;
                    var bones = smr.bones;
                    var bp = mesh.bindposes;
                    if (bones == null || bp == null || bones.Length != bp.Length) continue;
                    Matrix4x4 m = smr.transform.localToWorldMatrix;
                    for (int i = 0; i < bones.Length; i++)
                        if (bones[i] && !res.ContainsKey(bones[i].Pointer)) res[bones[i].Pointer] = m * bp[i].inverse;
                }
                catch { }
            }
            return res;
        }

        internal static void Clear()
        {
            Fixed.Clear();
            Skip.Clear();
        }
    }
}
