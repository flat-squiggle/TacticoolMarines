using System;
using System.Collections.Generic;
using System.Globalization;
using RedLoader;
using Sons.Ai.Vail;
using SonsSdk.Networking;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Fits the gun model to the soldier every frame after the animation (LateUpdate), instead of a fixed offset in
    /// the hand bone - a fixed offset only suits one clip, and the soldier now plays many.
    /// In the hands: the barrel points from the right hand towards the left hand (both hold the gun in every rifle
    /// clip), the grip sits in the right hand, the top of the gun faces up. While aiming, the upper body is turned a
    /// little (at most 25°) so the barrel really points at the target. In clips where the left hand leaves the gun
    /// (reloading) the last fitted pose relative to the right hand is kept.
    /// On the back: diagonally across the back, muzzle up over the left shoulder, in the soldier's own frame.
    /// The model's axes (which way is the barrel, which way is up, where is the grip) come from its bounds; the
    /// barrel's direction (muzzle or stock end) is decided on the first aim, from the old fixed rotation that held it
    /// the wrong way round, and saved in combat_gun_fit. tmcombat gun flip / upflip / grip change it.
    /// </summary>
    internal static partial class CombatGun
    {
        // Rotation used in the hand bone up to 1.21; it held the muzzle backwards in the aim clip.
        private static readonly Quaternion OldHandRotation = Quaternion.Euler(-45f, 0f, -135f);

        private static bool _analysed;
        private static Bounds _local;            // template space, unscaled
        private static int _barrelAxis = 2, _upAxis = 1;
        private static float _barrelSign = 1f, _upSign = 1f, _grip = 0.3f;
        private static bool _signKnown;
        private static Vector3 BarrelLocal = Vector3.forward, UpLocal = Vector3.up, GripLocal, CenterLocal, MuzzleLocal, ButtLocal, ForendLocal;
        private static readonly HashSet<string> FitLogged = new();

        private static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
        private static float Comp(Vector3 v, int i) => i == 0 ? v.x : i == 1 ? v.y : v.z;
        private static string AxisName(int i, float sign) => (sign < 0 ? "-" : "+") + (i == 0 ? "x" : i == 1 ? "y" : "z");

        internal static string FitInfo() =>
            !_analysed ? "Axes: not determined yet"
                       : $"Axes: barrel {AxisName(_barrelAxis, _barrelSign)}, up {AxisName(_upAxis, _upSign)}, grip {_grip:0.00}{(_signKnown ? "" : " (barrel direction is determined on the first aim)")}";

        // ------------------------------------------------------------------ model axes

        private static void Analyse(GameObject tpl, bool useConfig = true)
        {
            _analysed = false;
            try
            {
                bool any = false;
                Bounds b = default;
                Matrix4x4 toRoot = tpl.transform.worldToLocalMatrix;
                var meshes = new List<(Mesh m, Transform t)>();
                foreach (var mf in tpl.GetComponentsInChildren<MeshFilter>(true)) if (mf && mf.sharedMesh) meshes.Add((mf.sharedMesh, mf.transform));
                foreach (var sm in tpl.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (sm && sm.sharedMesh) meshes.Add((sm.sharedMesh, sm.transform));
                foreach (var (m, mt) in meshes)
                {
                    Matrix4x4 mat = toRoot * mt.localToWorldMatrix;
                    Bounds mb = m.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        Vector3 p = mat.MultiplyPoint3x4(c);
                        if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                        else b.Encapsulate(p);
                    }
                }
                if (!any) return;
                _local = b;
                Vector3 size = b.size;
                int longest = 0;
                for (int i = 1; i < 3; i++) if (Comp(size, i) > Comp(size, longest)) longest = i;
                int second = -1;
                for (int i = 0; i < 3; i++) if (i != longest && (second < 0 || Comp(size, i) > Comp(size, second))) second = i;
                _barrelAxis = longest;
                _upAxis = second;
                // A model's pivot usually sits at the grip, nearer the stock: the barrel side reaches further out.
                float cb = Comp(b.center, longest), cu = Comp(b.center, second);
                _barrelSign = cb < 0f ? -1f : 1f;
                _upSign = cu < 0f ? -1f : 1f; // most of the gun is above the grip
                _grip = 0.22f;
                _signKnown = false;
                if (useConfig) ReadFitConfig();
                ApplyAxes();
                _analysed = true;
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] model size {size.x:0.000} x {size.y:0.000} x {size.z:0.000}, centre {b.center.x:0.000} {b.center.y:0.000} {b.center.z:0.000} - {FitInfo()}");
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] measuring the model: {e.Message}"); }
        }

        private static void ReadFitConfig()
        {
            string s = (Config.CombatGunFit.Value ?? "auto").Trim().ToLowerInvariant();
            if (s == "" || s == "auto") return;
            var p = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && ParseAxis(p[0], out int ba, out float bs) && ParseAxis(p[1], out int ua, out float us) && ba != ua)
            {
                _barrelAxis = ba; _barrelSign = bs; _upAxis = ua; _upSign = us;
                _signKnown = true;
            }
            if (p.Length >= 3 && float.TryParse(p[2].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float g))
                _grip = Mathf.Clamp01(g);
        }

        private static bool ParseAxis(string s, out int axis, out float sign)
        {
            axis = 0; sign = 1f;
            if (s.Length != 2 || (s[0] != '+' && s[0] != '-')) return false;
            sign = s[0] == '-' ? -1f : 1f;
            axis = s[1] == 'x' ? 0 : s[1] == 'y' ? 1 : s[1] == 'z' ? 2 : -1;
            return axis >= 0;
        }

        private static void SaveFit()
        {
            Config.CombatGunFit.Value = $"{AxisName(_barrelAxis, _barrelSign)} {AxisName(_upAxis, _upSign)} {_grip.ToString("0.00", CultureInfo.InvariantCulture)}";
            try { Config.Category.SaveToFile(); } catch { }
        }

        private static void ApplyAxes()
        {
            BarrelLocal = Axis(_barrelAxis) * _barrelSign;
            UpLocal = Axis(_upAxis) * _upSign;
            Vector3 c = _local.center;
            float len = Comp(_local.size, _barrelAxis);
            float height = Comp(_local.size, _upAxis);
            // Centre line of the gun, then along it from the stock end; the grip hangs a little below the barrel.
            Vector3 stockEnd = c - BarrelLocal * (len * 0.5f);
            CenterLocal = c;
            GripLocal = stockEnd + BarrelLocal * (len * _grip) - UpLocal * (height * 0.35f);
            MuzzleLocal = c + BarrelLocal * (len * 0.5f);
            ButtLocal = stockEnd - UpLocal * (height * 0.15f); // the butt plate sits a little below the bore line
            ForendLocal = stockEnd + BarrelLocal * (len * 0.62f) - UpLocal * (height * 0.3f); // support hand under the fore-end
            foreach (var st in Attached.Values) st.HasLocal = false;
        }

        // ------------------------------------------------------------------ every frame, after the animation

        internal static void LateTick()
        {
            if (Attached.Count == 0 || NetUtils.IsDedicatedServer) return;
            if (!_analysed && !_native) return;
            foreach (var st in Attached.Values)
            {
                if (!st.Actor || !st.Gun || !st.Gun.activeSelf) continue;
                try
                {
                    if (st.Mode == "hand") { if (_native) FitNativeHand(st); else FitHand(st); }
                    else if (st.Mode == "back" && _analysed) FitBack(st);
                }
                catch (Exception e)
                {
                    if (FitLogged.Add("err" + e.Message)) RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Gun] fitting: {e.Message}");
                }
            }
        }

        private static void FindBones(GunState st)
        {
            if (st.BonesSearched && st.R) return;
            st.BonesSearched = true;
            st.R = RightHand(st.Actor);
            st.Spine = Back(st.Actor);
            st.L = LeftHandOf(st.Actor, st.R);
            string key = (st.R ? st.R.name : "-") + "|" + (st.L ? st.L.name : "-");
            if (FitLogged.Add(key))
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] Hands: right '{(st.R ? st.R.name : "-")}', left '{(st.L ? st.L.name : "not found")}', upper body '{(st.Spine ? st.Spine.name : "-")}'");
        }

        private static readonly string[] FingerWords = { "index", "thumb", "finger", "pinky", "ring", "middle", "mid", "weapon", "helper", "twist" };

        /// <summary>The left hand: the right hand's name mirrored, else any bone named left + hand.</summary>
        private static Transform LeftHandOf(VailActor actor, Transform right)
        {
            Transform root = CombatLook.AnimatorOf(actor) ? CombatLook.AnimatorOf(actor).transform : actor.transform;
            var all = root.GetComponentsInChildren<Transform>(true);
            if (right)
            {
                string n = right.name;
                foreach (var cand in new[] { n.Replace("Right", "Left"), n.Replace("right", "left"), n.Replace("R_", "L_"), n.Replace("_R", "_L"), n.Replace(".R", ".L"), n.Replace("RHand", "LHand") })
                {
                    if (cand == n) continue;
                    foreach (var t in all) if (t.name == cand) return t;
                }
            }
            Transform best = null;
            foreach (var t in all)
            {
                string l = t.name.ToLowerInvariant();
                if (!l.Contains("left") || !l.Contains("hand")) continue;
                bool finger = false;
                foreach (var w in FingerWords) if (l.Contains(w)) finger = true;
                if (finger) continue;
                if (!best || t.name.Length < best.name.Length) best = t;
            }
            return best;
        }

        private static float KickAmount(GunState st)
        {
            float k = (Time.time - st.KickAt) / 0.14f;
            return k >= 1f || k < 0f ? 0f : 1f - k;
        }

        // ------------------------------------------------------------------ the gun in the hands

        // Aiming: the butt sits in the right shoulder pocket and the barrel runs through the left (support) hand, the
        // way a rifle is shouldered - this keeps the stock out of the head whatever the clip does with the right hand.
        // Other rifle clips (carried at rest): grip in the right hand, barrel towards the left hand.
        // The two are blended so raising and lowering the rifle does not jump. Clips where the left hand leaves the
        // gun (reloading) keep the last fitted pose relative to the right hand.
        // combat_gun_offset moves/turns the result in the gun's own frame (tmcombat gun nudge / turn).

        private static Vector3 _offPos, _offRot;
        private static string _offFor;

        private static void LoadOffset()
        {
            string v = Config.CombatGunOffset.Value ?? "";
            if (v == _offFor) return;
            _offFor = v;
            (_offPos, _offRot) = Parse(v);
        }

        private static readonly string[] ArmSkip = { "fore", "lower", "hand", "helper", "twist", "weapon", "finger" };

        /// <summary>The right upper arm (its origin is the shoulder joint), found by walking up from the right hand.</summary>
        private static Transform UpperArm(Transform hand)
        {
            var chain = new List<string>();
            Transform found = null;
            for (Transform t = hand ? hand.parent : null; t && chain.Count < 8; t = t.parent)
            {
                chain.Add(t.name);
                string l = t.name.ToLowerInvariant();
                string pl = t.parent ? t.parent.name.ToLowerInvariant() : "";
                bool skip = false;
                foreach (var w in ArmSkip) if (l.Contains(w)) skip = true;
                if (!skip && (l.Contains("upperarm") || l.Contains("arm"))) { found = t; break; }
                if (pl.Contains("shoulder") || pl.Contains("clavicle") || pl.Contains("collar")) { found = t; break; }
            }
            if (FitLogged.Add("arm" + string.Join("/", chain)))
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] arm chain from the hand: {string.Join(" > ", chain)} - shoulder: '{(found ? found.name : "not found")}'");
            return found;
        }

        private static void FitHand(GunState st)
        {
            FindBones(st);
            if (!st.R) return;
            LoadOffset();
            if (!st.ArmSearched) { st.ArmSearched = true; st.Arm = UpperArm(st.R); }
            if (!st.ChainsSearched) FindChains(st);
            Transform gun = st.Gun.transform;
            string pose = CombatLook.CurrentPose(st.Actor);
            bool reload = pose != null && pose.Contains("Reload");
            bool aiming = pose != null && (pose.Contains("Aim") || pose.Contains("Fire")) && !reload;

            Vector3 r = st.R.position;
            bool haveL = st.L && pose != null && !reload;
            Vector3 l = haveL ? st.L.position : Vector3.zero;
            float handGap = haveL ? (l - r).magnitude : 0f;
            if (haveL && (handGap < 0.12f || handGap > 0.95f)) haveL = false;
            bool ik = Config.CombatGunIk.Value && st.RChain != null && st.LChain != null;
            st.IkWeight = Mathf.MoveTowards(st.IkWeight, haveL && ik ? 1f : 0f, Time.deltaTime * 6f);
            if (!haveL)
            {
                // Left hand off the gun (or no rifle clip): keep the last fitted pose relative to the right hand.
                if (st.HasLocal) { gun.localPosition = st.LocalPos; gun.localRotation = st.LocalRot; }
                ApplyKick(st, gun);
                return;
            }
            if (!_signKnown) Calibrate(st, (l - r).normalized);

            // While aiming: turn the upper body towards the target first (at most 25 degrees), so the arms do not
            // have to reach round the body. The bones below the spine move with it.
            bool haveAim = CombatAI.AimPoint(st.Actor, out Vector3 aimAt);
            if (aiming && haveAim && st.Spine) TurnSpine(st, aimAt);
            else st.AimFix = Quaternion.Slerp(st.AimFix, Quaternion.identity, 0.3f);
            r = st.R.position;
            l = st.L.position;

            st.AimBlend = Mathf.MoveTowards(st.AimBlend, aiming && st.Arm ? 1f : 0f, Time.deltaTime * 5f);
            Vector3 dirAnim = (l - r).normalized;    // the gun line the clip was made for
            Vector3 dir = dirAnim;
            float scale = gun.lossyScale.x;
            Vector3 origin;                          // where the gun's own origin goes
            Vector3 upHint = Vector3.up;
            {
                Vector3 up0 = Vector3.ProjectOnPlane(upHint, dir).normalized;
                Quaternion q0 = Quaternion.LookRotation(dir, up0) * Quaternion.Inverse(Quaternion.LookRotation(BarrelLocal, UpLocal));
                origin = r - q0 * (GripLocal * scale);
            }
            if (st.AimBlend > 0f && st.Arm)
            {
                Vector3 pocket = Pocket(st);
                // With IK the barrel points straight at the target and the hands follow; without IK the gun has
                // to stay on the left hand.
                Vector3 dirAim = ik && haveAim ? (aimAt - pocket).normalized : (l - pocket).normalized;
                dir = Vector3.Slerp(dirAnim, dirAim, st.AimBlend).normalized;
                Vector3 up0 = Vector3.ProjectOnPlane(upHint, dir).normalized;
                Quaternion q0 = Quaternion.LookRotation(dir, up0) * Quaternion.Inverse(Quaternion.LookRotation(BarrelLocal, UpLocal));
                Vector3 fromGrip = r - q0 * (GripLocal * scale);
                Vector3 fromButt = pocket - q0 * (ButtLocal * scale);
                origin = Vector3.Lerp(fromGrip, fromButt, st.AimBlend);
            }

            Vector3 up = Vector3.ProjectOnPlane(upHint, dir);
            if (up.sqrMagnitude < 1e-4f) up = st.Actor.transform.forward;
            Quaternion frame = Quaternion.LookRotation(dir, up.normalized);
            Quaternion q = frame * Quaternion.Euler(_offRot) * Quaternion.Inverse(Quaternion.LookRotation(BarrelLocal, UpLocal));
            Vector3 gunPos = origin + frame * _offPos;

            // Kick: the gun moves back and up, the hands go with it (they are solved onto the kicked gun).
            float k = KickAmount(st);
            if (k > 0f)
            {
                gunPos -= dir * (0.06f * k);
                q = Quaternion.AngleAxis(-5f * k, frame * Vector3.right) * q;
            }

            if (st.IkWeight > 0f)
            {
                // Hands onto the gun: right hand to the grip, left hand to the fore-end. The wrists keep the clip's
                // grip, turned by however much the gun was turned away from the clip's gun line.
                Quaternion turn = Quaternion.FromToRotation(dirAnim, dir);
                Vector3 gripW = gunPos + q * (GripLocal * scale);
                Vector3 foreW = gunPos + q * (ForendLocal * scale);
                HandOnto(st.RChain, st.R, gripW, turn, st.IkWeight);
                HandOnto(st.LChain, st.L, foreW, turn, st.IkWeight);
            }

            // The gun is a child of the right hand socket, which the IK just moved: set its world pose last.
            gun.rotation = q;
            gun.position = gunPos;
            st.LocalPos = gun.localPosition;
            st.LocalRot = gun.localRotation;
            st.HasLocal = true;
        }

        /// <summary>
        /// Puts the weapon socket below the hand onto point: the wrist gets its final rotation (the clip's, turned
        /// with the gun), then the arm is solved so the hand bone lands where that rotation puts the socket on point.
        /// </summary>
        private static void HandOnto(ArmChain arm, Transform socket, Vector3 point, Quaternion turn, float weight)
        {
            Transform hand = arm.Hand;
            Quaternion handRot = hand.rotation;
            Quaternion want = Quaternion.Slerp(handRot, turn * handRot, weight);
            Vector3 offset = socket.position - hand.position;                      // socket relative to the hand, now
            Vector3 offsetWanted = want * (Quaternion.Inverse(handRot) * offset);   // ... with the final wrist rotation
            Vector3 handTarget = Vector3.Lerp(hand.position, point - offsetWanted, weight);
            arm.Solve(hand, handTarget);
            hand.rotation = want;
        }

        /// <summary>The right shoulder pocket: a little in from the shoulder joint towards the chest, a little forward.</summary>
        private static Vector3 Pocket(GunState st)
        {
            Vector3 shoulder = st.Arm.position;
            Vector3 chest = st.Spine ? st.Spine.position : st.Actor.transform.position + Vector3.up * 1.3f;
            return shoulder + (chest - shoulder) * 0.25f + st.Actor.transform.forward * 0.04f;
        }

        /// <summary>Turns the spine (at most 25 degrees, smoothed) so the line shoulder - left hand points at the target.</summary>
        private static void TurnSpine(GunState st, Vector3 aimAt)
        {
            if (!st.Arm || !st.L) return;
            Vector3 pocket = Pocket(st);
            Vector3 have = (st.L.position - pocket).normalized;
            Vector3 want = (aimAt - pocket).normalized;
            Quaternion fix = Quaternion.FromToRotation(have, want);
            fix.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            if (axis.sqrMagnitude < 0.5f) return;
            fix = Quaternion.AngleAxis(Mathf.Clamp(ang, -25f, 25f), axis);
            st.AimFix = Quaternion.Slerp(st.AimFix, fix, 0.3f);
            st.Spine.rotation = st.AimFix * st.Spine.rotation;
        }

        // ------------------------------------------------------------------ two bone IK

        /// <summary>
        /// An arm for IK: upper arm, forearm and hand bones; the end that has to reach the target is the weapon
        /// socket below the hand. Rotating the upper arm and forearm carries the hand and the socket along.
        /// </summary>
        internal sealed class ArmChain
        {
            public Transform Upper, Fore, Hand;

            /// <summary>
            /// Classic analytic two bone IK (law of cosines, as in Unity's animation jobs sample): bend the elbow so
            /// shoulder - tip has the target's length, swing the upper arm onto the target, then turn the arm about
            /// the shoulder - target line so the elbow points where the clip had it.
            /// </summary>
            public void Solve(Transform tip, Vector3 target)
            {
                Vector3 aPos = Upper.position, bPos = Fore.position, cPos = tip.position;
                Vector3 hint = bPos; // the clip's elbow position keeps the natural bend direction
                Vector3 ab = bPos - aPos, bc = cPos - bPos, ac = cPos - aPos, at = target - aPos;
                float abLen = ab.magnitude, bcLen = bc.magnitude;
                if (abLen < 1e-4f || bcLen < 1e-4f) return;
                float acLen = ac.magnitude;
                float atLen = Mathf.Clamp(at.magnitude, Mathf.Abs(abLen - bcLen) + 1e-3f, abLen + bcLen - 1e-3f);

                float oldAngle = TriangleAngle(acLen, abLen, bcLen);
                float newAngle = TriangleAngle(atLen, abLen, bcLen);
                Vector3 axis = Vector3.Cross(ab, bc);
                if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(hint - aPos, bc);
                if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(at, bc);
                if (axis.sqrMagnitude < 1e-8f) axis = Vector3.up;
                axis.Normalize();
                float half = 0.5f * (oldAngle - newAngle);
                float sin = Mathf.Sin(half), cos = Mathf.Cos(half);
                Fore.rotation = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, cos) * Fore.rotation;

                ac = tip.position - aPos;
                if (ac.sqrMagnitude > 1e-8f && at.sqrMagnitude > 1e-8f)
                    Upper.rotation = Quaternion.FromToRotation(ac, at) * Upper.rotation;

                // Keep the elbow on the clip's side of the shoulder - target line.
                Vector3 acN = (tip.position - aPos).normalized;
                Vector3 abProj = Vector3.ProjectOnPlane(Fore.position - aPos, acN);
                Vector3 ahProj = Vector3.ProjectOnPlane(hint - aPos, acN);
                if (abProj.sqrMagnitude > 1e-6f && ahProj.sqrMagnitude > 1e-6f)
                    Upper.rotation = Quaternion.FromToRotation(abProj, ahProj) * Upper.rotation;
            }

            private static float TriangleAngle(float aLen, float aLen1, float aLen2)
            {
                float c = Mathf.Clamp((aLen1 * aLen1 + aLen2 * aLen2 - aLen * aLen) / (2f * aLen1 * aLen2), -1f, 1f);
                return Mathf.Acos(c);
            }
        }

        private static readonly string[] ChainSkip = { "twist", "helper", "roll", "weapon", "socket", "attach" };

        /// <summary>Hand, forearm, upper arm: the next three real bones above the weapon socket.</summary>
        private static ArmChain ChainFrom(Transform socket)
        {
            if (!socket) return null;
            var bones = new List<Transform>();
            for (Transform t = socket.parent; t && bones.Count < 3; t = t.parent)
            {
                string l = t.name.ToLowerInvariant();
                bool skip = false;
                foreach (var w in ChainSkip) if (l.Contains(w)) skip = true;
                if (!skip) bones.Add(t);
            }
            if (bones.Count < 3) return null;
            // The three must be a parent chain (hand under forearm under upper arm).
            if (!bones[0].IsChildOf(bones[1]) || !bones[1].IsChildOf(bones[2])) return null;
            return new ArmChain { Hand = bones[0], Fore = bones[1], Upper = bones[2] };
        }

        private static void FindChains(GunState st)
        {
            st.ChainsSearched = true;
            st.RChain = ChainFrom(st.R);
            st.LChain = ChainFrom(st.L);
            string Desc(ArmChain c) => c == null ? "not found" : $"{c.Upper.name} > {c.Fore.name} > {c.Hand.name}";
            string key = "ik" + Desc(st.RChain) + Desc(st.LChain);
            if (FitLogged.Add(key))
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] IK arms: right {Desc(st.RChain)}, left {Desc(st.LChain)}" +
                         (st.RChain == null || st.LChain == null ? " - without IK (gun aligned to the hand)" : ""));
        }

        /// <summary>Turns the upper body (at most 25 degrees) so the barrel really points at the target.</summary>
        private static void AimCorrection(GunState st, Transform gun, float maxDeg = 25f)
        {
            if (!st.Spine || !CombatAI.AimPoint(st.Actor, out Vector3 aimAt))
            {
                st.AimFix = Quaternion.Slerp(st.AimFix, Quaternion.identity, 0.3f);
                return;
            }
            Vector3 barrel = gun.rotation * BarrelLocal;
            Vector3 origin = gun.TransformPoint(CenterLocal);
            Vector3 want = (aimAt - origin).normalized;
            Quaternion fix = Quaternion.FromToRotation(barrel, want);
            fix.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            if (axis.sqrMagnitude < 0.5f) return;
            // The animation resets the spine every frame, so this measures the full error each frame; the smoothing
            // only keeps the correction from jumping.
            fix = Quaternion.AngleAxis(Mathf.Clamp(ang, -maxDeg, maxDeg), axis);
            st.AimFix = Quaternion.Slerp(st.AimFix, fix, 0.3f);
            st.Spine.rotation = st.AimFix * st.Spine.rotation;
        }

        private static void ApplyKick(GunState st, Transform gun)
        {
            float k = KickAmount(st);
            if (k <= 0f) return;
            Vector3 fwd = gun.rotation * BarrelLocal;
            Vector3 right = gun.rotation * Vector3.Cross(UpLocal, BarrelLocal);
            gun.position -= fwd * (0.06f * k);
            gun.rotation = Quaternion.AngleAxis(-5f * k, right) * gun.rotation;
        }

        /// <summary>
        /// The first aim decides which end is the muzzle: the model axis that pointed forward under the old fixed hand
        /// rotation is the muzzle (1.22 assumed the opposite and had to be flipped in game).
        /// </summary>
        private static void Calibrate(GunState st, Vector3 handDir)
        {
            Vector3 axis = Axis(_barrelAxis);
            Vector3 oldWorld = st.R.rotation * (OldHandRotation * axis);
            float dot = Vector3.Dot(oldWorld.normalized, handDir);
            _barrelSign = dot > 0f ? 1f : -1f;
            _signKnown = true;
            ApplyAxes();
            SaveFit();
            RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Gun] barrel direction determined (the old hold pointed the {(dot > 0 ? "+" : "-")}axis forward): {FitInfo()} - wrong way round? tmcombat gun flip");
        }

        private static void FitBack(GunState st)
        {
            FindBones(st);
            Transform anchor = st.Spine ? st.Spine : st.Actor.transform;
            Transform a = st.Actor.transform;
            Transform gun = st.Gun.transform;
            var (offPos, offRot) = Parse(Config.CombatGunBack.Value);
            // Diagonally across the back, muzzle up over the left shoulder, top of the gun facing away from the body.
            Vector3 dir = (a.up * 0.8f - a.right * 0.6f).normalized;
            Quaternion frame = Quaternion.LookRotation(dir, -a.forward);
            Quaternion q = frame * Quaternion.Euler(offRot) * Quaternion.Inverse(Quaternion.LookRotation(BarrelLocal, UpLocal));
            float scale = gun.lossyScale.x;
            Vector3 center = anchor.position - a.forward * 0.2f + a.right * offPos.x + a.up * offPos.y + a.forward * offPos.z;
            gun.rotation = q;
            gun.position = center - q * (CenterLocal * scale);
        }
    }
}
