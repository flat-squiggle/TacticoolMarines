using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using SonsSdk.Networking;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Foot placement for the player clips the soldiers play in a fight. Those clips are made for flat ground (the
    /// player has no foot IK in them), so on slopes and dunes a foot sinks into the ground. After the animation each
    /// frame: the hips are lowered so the lower planted foot reaches the ground, then each leg that is still below
    /// the ground is bent (two bone IK: thigh, shin, foot) so the foot stands on it. Only while a combat pose plays;
    /// his own animations are left alone. Config combat_foot_ik.
    /// </summary>
    internal static class CombatFeet
    {
        private sealed class Legs
        {
            public Transform Hips;
            public CombatGun.ArmChain Left, Right;   // Upper = thigh, Fore = shin, Hand = foot
            public float HipOffset;
        }

        private const float Ankle = 0.09f;          // foot bone above the sole
        private static readonly Dictionary<IntPtr, Legs> Cache = new();
        private static readonly HashSet<IntPtr> Missing = new();
        private static readonly HashSet<string> Logged = new();

        // Measured 2026-10-07: the player run clips move the model root (the soldier model's animator object) down
        // by 0.30 m - the body sank and the feet were 0.2 m in the ground ("kneeling"). In combat poses the model root
        // is held at the height it has outside them.
        private static readonly Dictionary<IntPtr, float> RestY = new();
        // Also measured: while he moves, his own movement code lowers the model's parent (VisualRoot) by ~0.3 m
        // (his own locomotion is made for that; the player clips are not). Held at its resting height as well.
        private static readonly Dictionary<IntPtr, float> RestParentY = new();

        internal static void LateTick()
        {
            if (NetUtils.IsDedicatedServer) return;
            foreach (var (a, _) in SoldierRegistry.All())
            {
                if (!a) continue;
                Animator an = CombatLook.AnimatorOf(a);
                if (!an) continue;
                Transform mr = an.transform;
                Transform vr = mr.parent;
                if (CombatLook.CurrentPose(a) == null)
                {
                    RestY[a.Pointer] = mr.localPosition.y;
                    if (vr && !CombatMove.IsMoving(a)) RestParentY[a.Pointer] = vr.localPosition.y;
                    RestoreCrouch(a);
                    continue;
                }
                if (vr && vr != a.transform && RestParentY.TryGetValue(a.Pointer, out float py))
                {
                    Vector3 vp = vr.localPosition;
                    if (Mathf.Abs(vp.y - py) > 0.001f) vr.localPosition = new Vector3(vp.x, py, vp.z);
                }
                // His own auto-crouch (low ceiling checks - the bush pusher capsule above his head counts) lowers
                // the model while a standing player clip plays: he looked like he was kneeling or climbing.
                NoCrouch(a);
                if (RestY.TryGetValue(a.Pointer, out float ry))
                {
                    Vector3 lp = mr.localPosition;
                    if (Mathf.Abs(lp.y - ry) > 0.001f) mr.localPosition = new Vector3(lp.x, ry, lp.z);
                }
                if (!Config.CombatFootIk.Value) continue;
                try { Place(a); }
                catch (Exception e) { if (Logged.Add(e.Message)) RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Feet] {e.Message}"); }
            }
        }

        private static Legs Find(VailActor a)
        {
            if (Cache.TryGetValue(a.Pointer, out var l)) return l;
            if (Missing.Contains(a.Pointer)) return null;
            Animator an = CombatLook.AnimatorOf(a);
            Transform root = an ? an.transform : a.transform;
            var byName = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) byName.TryAdd(t.name, t);
            CombatGun.ArmChain Leg(string side)
            {
                if (!byName.TryGetValue(side + "UpLeg", out var up) || !byName.TryGetValue(side + "Leg", out var shin) || !byName.TryGetValue(side + "Foot", out var foot)) return null;
                if (!foot.IsChildOf(shin) || !shin.IsChildOf(up)) return null;
                return new CombatGun.ArmChain { Upper = up, Fore = shin, Hand = foot };
            }
            byName.TryGetValue("Hips", out var hips);
            l = new Legs { Hips = hips, Left = Leg("Left"), Right = Leg("Right") };
            if (l.Left == null || l.Right == null || !hips)
            {
                Missing.Add(a.Pointer);
                RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Feet] {a.name}: hip/leg bones not found - no foot placement");
                return null;
            }
            Cache[a.Pointer] = l;
            if (Logged.Add("found")) RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Feet] foot placement active: Hips, {l.Left.Upper.name} > {l.Left.Fore.name} > {l.Left.Hand.name} (and right)");
            return l;
        }

        private static void Place(VailActor a)
        {
            Legs l = Find(a);
            if (l == null) return;
            float rootY = a.transform.position.y;
            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.1f);

            // How far each foot is from where it should stand (+ = in the ground, - = above it).
            bool okL = Need(l.Left.Hand, out float dL), okR = Need(l.Right.Hand, out float dR);
            // Only feet near the ground count for lowering the hips (a foot swinging forward is meant to be up).
            bool plantedL = okL && l.Left.Hand.position.y - rootY < 0.3f;
            bool plantedR = okR && l.Right.Hand.position.y - rootY < 0.3f;
            // Running: the stride stays as it is (lowering the hips into every gap between rocks made him look like
            // he was kneeling or climbing); standing: the hips may sink a little so a foot reaches lower ground.
            string pose = CombatLook.CurrentPose(a) ?? "";
            bool running = pose.Contains("Run") || pose.Contains("Walk");
            float drop = 0f;
            if (!running)
            {
                if (plantedL) drop = Mathf.Min(drop, dL);
                if (plantedR) drop = Mathf.Min(drop, dR);
                drop = Mathf.Max(drop, -0.2f);
            }
            l.HipOffset = Mathf.MoveTowards(l.HipOffset, drop, dt * 1.5f);
            if (Mathf.Abs(l.HipOffset) > 0.001f) l.Hips.position += Vector3.up * l.HipOffset;

            // Feet still in the ground: bend the leg so the foot stands on it.
            // Only a foot that is a little in the ground (a slope, a step) is lifted; deeper means a crack or the edge
            // of a rock, where lifting would give a high knee.
            if (okL && Need(l.Left.Hand, out dL) && dL > 0.005f && dL < 0.3f) l.Left.Solve(l.Left.Hand, l.Left.Hand.position + Vector3.up * dL);
            if (okR && Need(l.Right.Hand, out dR) && dR > 0.005f && dR < 0.3f) l.Right.Solve(l.Right.Hand, l.Right.Hand.position + Vector3.up * dR);
        }

        /// <summary>Ground height below the foot (ignoring actors) compared with the foot: d = wanted foot height - foot height.</summary>
        private static bool Need(Transform foot, out float d)
        {
            d = 0f;
            Vector3 p = foot.position;
            // Only a step above the foot: anything higher is an obstacle, not ground to stand on.
            var hits = Physics.RaycastAll(p + Vector3.up * 0.35f, Vector3.down, 1.2f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            float gy = 0f;
            foreach (var h in hits)
            {
                if (!h.collider || h.distance >= best) continue;
                if (h.collider.GetComponentInParent<VailActor>()) continue;
                if (OurObject(h.collider.transform)) continue;
                best = h.distance;
                gy = h.point.y;
            }
            if (best == float.MaxValue) return false;
            d = gy + Ankle - p.y;
            return true;
        }

        private static readonly Dictionary<IntPtr, bool> CanCrouchBefore = new();

        private static void NoCrouch(VailActor a)
        {
            try
            {
                if (!CanCrouchBefore.ContainsKey(a.Pointer)) CanCrouchBefore[a.Pointer] = a._canCrouch;
                a._canCrouch = false;
                if (a._isCrouched) { a._isCrouched = false; a.SetCrouchedCollision(false); }
            }
            catch { }
        }

        /// <summary>His own crouching back on (when a combat pose ends).</summary>
        internal static void RestoreCrouch(VailActor a)
        {
            if (!a || !CanCrouchBefore.TryGetValue(a.Pointer, out bool c)) return;
            CanCrouchBefore.Remove(a.Pointer);
            try { a._canCrouch = c; } catch { }
        }

        internal static bool OurObject(Transform t)
        {
            for (; t; t = t.parent) if (t.name.StartsWith("LivingSoldiers_", StringComparison.Ordinal)) return true;
            return false;
        }

        internal static void Clear()
        {
            RestY.Clear();
            RestParentY.Clear();
            CanCrouchBefore.Clear();
            Cache.Clear();
            Missing.Clear();
        }
    }
}
