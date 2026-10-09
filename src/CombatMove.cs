using System;
using System.Collections.Generic;
using Pathfinding;
using RedLoader;
using Sons.Ai.Vail;
using TheForest.Utils;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Movement for soldiers in combat (their own AI is switched off while they fight, so nothing else moves them).
    /// He plays the player's directional run clips (forward, back, left, right) through CombatLook.Pose, keeps
    /// facing the enemy while moving (strafing / backing away), and is carried along the ground by us.
    /// How he gets carried is measured on the first moves, because it cannot be known in advance:
    ///   move  - VailActor.MoveState.Move (the actor's own mover, handles ground and collisions)
    ///   pos   - set the position ourselves, snapped to the ground
    ///   root  - the run clip's root motion already moves him; we add nothing
    /// The result is logged and kept for the session. Config combat_move_mode = auto|move|pos|root.
    /// Also finds positions: away from an enemy, with line of sight, behind cover, towards the player.
    /// </summary>
    internal static class CombatMove
    {
        internal enum Result { Moving, Arrived, Failed }
        internal enum Purpose { Retreat, Sight, Shift, Advance, Fallback, Cover, BackStep, Approach, Chase }
        private enum Carry { Move, Pos, Root }

        private sealed class Move
        {
            public VailActor Actor;
            public Vector3 Goal;
            public Func<Vector3?> Enemy;
            public bool FaceEnemy;
            public List<Vector3> Path;
            public float OwnSpeedSet;
            public bool ForcePos;
            public float VaultStart, VaultUntil, VaultTop, NextVaultCheck;
            public Vector3 VaultFrom, VaultTo;   // set the position directly (caves: the actor's own mover keeps him on the terrain above)
            public int OwnBad;
            public int Wp;
            public float Started, Deadline;
            public Vector3 LastPos, ProgressPos;
            public float ProgressAt;
            public float Travelled, Expected;
            public bool Calibrated;
            public string Why, Clip;
        }

        private static readonly Dictionary<IntPtr, Move> Moves = new();
        private static readonly Dictionary<string, float> ClipSpeeds = new();
        private static Carry _carry = Carry.Move;
        private static int _carryConfirmed;
        private static bool _carryLogged;
        private static readonly System.Random Rng = new();

        // Player clip names (third person, the ones remote players are shown with).
        private const string RunF = "PlayerARunForwardMP", RunB = "PlayerARunBackMP", RunL = "PlayerARunLeftMP", RunR = "PlayerARunRightMP";
        private const float DefaultRunSpeed = 3.6f;

        // ------------------------------------------------------------------ status

        internal static string MethodInfo()
        {
            string forced = Forced();
            var speeds = new List<string>();
            foreach (var kv in ClipSpeeds) speeds.Add($"{kv.Key.Replace("PlayerA", "").Replace("MP", "")} {kv.Value:0.0} m/s");
            return $"Movement: {(forced == null ? $"auto, currently {Name(_carry)}{(_carryConfirmed > 0 ? " (measured)" : " (not measured yet)")}" : "forced " + forced)}" +
                   (speeds.Count > 0 ? $" | run clips: {string.Join(", ", speeds)}" : "");
        }

        private static string Name(Carry c) => c == Carry.Move ? "move" : c == Carry.Pos ? "pos" : "root";

        private static string Forced()
        {
            string f = (Config.CombatMoveMode.Value ?? "auto").Trim().ToLowerInvariant();
            return f == "move" || f == "pos" || f == "root" ? f : null;
        }

        private static Carry CurrentCarry()
        {
            string f = Forced();
            return f == "move" ? Carry.Move : f == "pos" ? Carry.Pos : f == "root" ? Carry.Root : _carry;
        }

        internal static bool IsMoving(VailActor a) => a && Moves.ContainsKey(a.Pointer);

        /// <summary>Speed of a run clip: its own root motion speed if it has one, else a typical jog speed.</summary>
        private static float ClipSpeed(string clip)
        {
            if (clip == null) return DefaultRunSpeed;
            if (ClipSpeeds.TryGetValue(clip, out float v)) return v;
            float speed = 0f;
            try
            {
                var c = CombatLook.Clip(clip);
                if (c) speed = c.averageSpeed.magnitude;
            }
            catch { }
            if (speed < 0.5f || speed > 9f) speed = DefaultRunSpeed;
            ClipSpeeds[clip] = speed;
            RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Move] Clip {clip}: {speed:0.00} m/s");
            return speed;
        }

        // ------------------------------------------------------------------ moving

        /// <summary>
        /// Starts a move to goal. faceEnemy = keep facing the enemy (short side steps, strafe clips); otherwise he
        /// turns and runs where he goes.
        /// </summary>
        public static bool Start(VailActor a, Vector3 goal, Func<Vector3?> enemy, string why, bool faceEnemy = false, bool straightIfUnreachable = false, bool forcePos = false, List<Vector3> givenPath = null)
        {
            if (!a) return false;
            Stop(a, quiet: true);
            float now = Time.time;
            Vector3 pos = a.transform.position;

            // The game's own path finding (A* Pathfinding Project, the graphs Kelvin and the cannibals walk on).
            List<Vector3> path = null;
            float length = Flat(goal - pos).magnitude;
            bool unreachable = false;
            if (givenPath != null && givenPath.Count > 0)
            {
                // A path we know is walkable (caves: the way the player went).
                path = new List<Vector3> { pos };
                path.AddRange(givenPath);
                goal = path[path.Count - 1];
                length = 0f;
                for (int i = 1; i < path.Count; i++) length += Vector3.Distance(path[i - 1], path[i]);
            }
            else if (NavPath(a, pos, goal, out path, out unreachable))
            {
                goal = path[path.Count - 1];
                length = 0f;
                for (int i = 1; i < path.Count; i++) length += Vector3.Distance(path[i - 1], path[i]);
                // A detour of more than three times the straight distance is not a "short reposition".
                if (length > Mathf.Max(6f, Flat(goal - pos).magnitude * 3f)) { CombatAI.Debug($"{a.name}: path ({why}) too long ({length:0} m) - dropped"); return false; }
            }
            else if (unreachable && !straightIfUnreachable)
            {
                CombatAI.Debug($"{a.name}: target ({why}) not reachable according to path finding");
                return false;
            }
            Moves[a.Pointer] = new Move
            {
                Actor = a, Goal = goal, Enemy = enemy, Why = why, FaceEnemy = faceEnemy, Path = path, Wp = 1, ForcePos = forcePos,
                Started = now, Deadline = now + 1.5f + length / 2f,
                LastPos = pos, ProgressPos = pos, ProgressAt = now,
            };
            CombatAI.Debug($"{a.name} moves ({why}): {length:0.0} m{(path != null ? $", {path.Count} waypoints" : ", straight line")}");
            return true;
        }

        // ------------------------------------------------------------------ vaulting

        private static readonly Dictionary<IntPtr, int> Vaults = new();
        private static readonly HashSet<IntPtr> OwnClipsRegistered = new();

        internal static bool IsVaulting(VailActor a) => a && Moves.TryGetValue(a.Pointer, out var m) && m.VaultUntil > 0f;
        internal static int VaultCount(VailActor a) => a && Vaults.TryGetValue(a.Pointer, out int n) ? n : 0;

        /// <summary>
        /// Something knee-high in the way with room above it and floor beyond (a rock, a ledge, a log): he jumps -
        /// Kelvin's own clips (climb up when the far side is higher, vault over otherwise), moved over it in an arc.
        /// </summary>
        private static bool TryVault(VailActor a, Move m, Vector3 dir, float now)
        {
            Vector3 pos = a.transform.position;
            if (!Hit(pos + Vector3.up * 0.45f, dir, 1.1f, out float at)) return false;   // nothing low in the way
            if (at < 0.15f) return false;
            if (Hit(pos + Vector3.up * 2.0f, dir, at + 1.2f, out _)) return false;       // no room above: a wall, not jumpable
            Vector3 beyond = pos + dir * (at + 0.9f);
            if (!FloorAt(beyond, pos.y + 2.4f, 5f, out Vector3 land)) return false;
            float dy = land.y - pos.y;
            if (dy > 1.9f || dy < -2.5f) return false;
            if (dy < 0.25f && dy > -0.25f && at > 0.9f) return false; // a thin pole far ahead: the path goes round it
            EnsureOwnClips(a);
            string clip = dy > 0.5f ? "RobbyJumpUpMediumWalk" : "RobbyJumpOverBigWalk";
            if (!CombatLook.HasClip(clip)) clip = CombatLook.HasClip("RobbyJumpWallLow") ? "RobbyJumpWallLow" : null;
            var c = clip != null ? CombatLook.Clip(clip) : null;
            if (clip != null) CombatLook.Pose(a, clip, 0.15f, restart: true);
            m.Clip = null; // the run clip is picked again after the jump
            float dur = c ? Mathf.Clamp(c.length * 0.8f, 0.7f, 1.8f) : 0.8f;
            m.VaultFrom = pos; m.VaultTo = land; m.VaultStart = now; m.VaultUntil = now + dur;
            m.VaultTop = Mathf.Max(pos.y, land.y) + 0.6f;
            m.Deadline += dur + 1f;
            m.ProgressAt = now + dur;
            Vaults[a.Pointer] = VaultCount(a) + 1;
            try { var ms = a._moveState; if (ms != null) ms.SetRotation(new Vector3(0f, Quaternion.LookRotation(dir, Vector3.up).eulerAngles.y, 0f), false); } catch { }
            CombatAI.Debug($"{a.name} jumps {(dy > 0.5f ? "up" : "over")} ({clip ?? "without animation"}, {dy:+0.0;-0.0} m, {dur:0.0}s) - {m.Why}");
            return true;
        }

        private static void VaultStep(VailActor a, Move m, float now)
        {
            float t = Mathf.Clamp01((now - m.VaultStart) / Mathf.Max(0.1f, m.VaultUntil - m.VaultStart));
            Vector3 p = Vector3.Lerp(m.VaultFrom, m.VaultTo, t);
            p.y = Mathf.Lerp(Mathf.Lerp(m.VaultFrom.y, m.VaultTo.y, t), m.VaultTop, Mathf.Sin(t * Mathf.PI));
            try { var ms = a._moveState; if (ms != null) { ms.SetPosition(p); ms._fallingVelocity = Vector3.zero; } } catch { }
            a.transform.position = p;
            m.LastPos = p; m.ProgressPos = p; m.ProgressAt = now;
            if (t >= 1f) m.VaultUntil = 0f;
        }

        /// <summary>His own (Kelvin) clips - the jumps - playable as poses.</summary>
        private static void EnsureOwnClips(VailActor a)
        {
            Animator an = CombatLook.AnimatorOf(a);
            if (!an) return;
            if (!OwnClipsRegistered.Add(an.Pointer)) return;
            try { CombatLook.RegisterClips(CombatDiag.OwnClips(an)); } catch { }
        }

        private static bool Hit(Vector3 from, Vector3 dir, float len, out float at)
        {
            at = len;
            bool any = false;
            foreach (var h in Physics.RaycastAll(from, dir, len, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!h.collider || h.collider.GetComponentInParent<VailActor>() || CombatFeet.OurObject(h.collider.transform)) continue;
                if (LocalPlayer.Transform && h.collider.transform.IsChildOf(LocalPlayer.Transform)) continue;
                if (h.distance < at) at = h.distance;
                any = true;
            }
            return any;
        }

        private static bool FloorAt(Vector3 p, float fromY, float len, out Vector3 floor)
        {
            floor = p;
            float best = float.MaxValue; bool found = false;
            foreach (var h in Physics.RaycastAll(new Vector3(p.x, fromY, p.z), Vector3.down, len, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!h.collider || h.distance >= best || h.collider.GetComponentInParent<VailActor>() || CombatFeet.OurObject(h.collider.transform)) continue;
                if (LocalPlayer.Transform && h.collider.transform.IsChildOf(LocalPlayer.Transform)) continue;
                best = h.distance; floor = h.point; found = true;
            }
            return found;
        }

        private static bool WallAhead(Vector3 pos, Vector3 step)
        {
            Vector3 dir = step.normalized;
            float len = step.magnitude + 0.35f;
            foreach (float h in new[] { 0.7f, 1.4f })
                foreach (var hit in Physics.RaycastAll(pos + Vector3.up * h, dir, len, ~0, QueryTriggerInteraction.Ignore))
                    if (hit.collider && !hit.collider.GetComponentInParent<VailActor>() && !CombatFeet.OurObject(hit.collider.transform)) return true;
            return false;
        }

        private static bool _navBroken;
        private static bool _navLogged;

        /// <summary>
        /// A path from - to on the game's nav graphs (only the graphs this actor may use). False with unreachable =
        /// the path finder answered "no way there"; false without = path finding not available (straight line then).
        /// </summary>
        internal static bool NavPath(VailActor a, Vector3 from, Vector3 to, out List<Vector3> points, out bool unreachable)
        {
            points = null;
            unreachable = false;
            if (_navBroken) return false;
            try
            {
                if (!AstarPath.active) return false;
                ABPath p = ABPath.Construct(from, to, null);
                try
                {
                    GraphMask mask = a.GetNavGraphMask();
                    if (mask.value != 0)
                    {
                        var nn = new NNConstraint();
                        nn.constrainWalkability = true;
                        nn.walkable = true;
                        nn.graphMask = mask;
                        p.nnConstraint = nn;
                    }
                }
                catch { }
                AstarPath.StartPath(p, true);
                AstarPath.BlockUntilCalculated(p);
                var vp = p.vectorPath;
                if (p.error || vp == null || vp.Count < 2)
                {
                    unreachable = true;
                    if (!_navLogged) { _navLogged = true; RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Move] path finding: no path ({p.errorLog})"); }
                    return false;
                }
                points = new List<Vector3>(vp.Count);
                for (int i = 0; i < vp.Count; i++) points.Add(vp[i]);
                if (Flat(points[points.Count - 1] - to).magnitude > 2.5f) { unreachable = true; points = null; return false; }
                if (!_navLogged) { _navLogged = true; RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Move] the game's path finding works ({points.Count} waypoints)"); }
                return true;
            }
            catch (Exception e)
            {
                _navBroken = true;
                RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Move] the game's path finding is not usable ({e.Message}) - straight paths");
                return false;
            }
        }

        internal static bool NavAvailable => !_navBroken;

        public static void Stop(VailActor a, bool quiet = false)
        {
            if (!a || !Moves.TryGetValue(a.Pointer, out var m)) return;
            Moves.Remove(a.Pointer);
            if (m.Clip == OwnRun) SetSpeedParam(a, 0f);
            if (!quiet) CombatAI.Debug($"{a.name} stops");
        }

        /// <summary>One frame of the soldier's move.</summary>
        public static Result Tick(VailActor a)
        {
            if (!a || !Moves.TryGetValue(a.Pointer, out var m)) return Result.Failed;
            float now = Time.time;
            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.1f);
            Vector3 pos = a.transform.position;
            m.Travelled += Flat(pos - m.LastPos).magnitude;
            m.LastPos = pos;
            Vector3 to = Flat(m.Goal - pos);
            float left = to.magnitude;

            if (left < 0.45f) { Finish(m, true); return Result.Arrived; }
            // Steer towards the next waypoint of the path (the goal itself on a straight move).
            Vector3 next = m.Goal;
            if (m.Path != null)
            {
                while (m.Wp < m.Path.Count - 1 && Flat(m.Path[m.Wp] - pos).magnitude < 0.7f) m.Wp++;
                next = m.Path[Mathf.Min(m.Wp, m.Path.Count - 1)];
            }
            Vector3 toNext = Flat(next - pos);
            if (toNext.sqrMagnitude < 0.0001f) toNext = to;
            if (now > m.Deadline)
            {
                Finish(m, m.Travelled > 1f);
                return m.Travelled > 1f ? Result.Arrived : Result.Failed;
            }
            Vector3 dir = toNext.normalized;

            // Jumping onto / over something knee-high in the way (caves and fights alike).
            if (m.VaultUntil > 0f) { VaultStep(a, m, now); return Result.Moving; }
            if (!m.FaceEnemy && Config.SoldierVault.Value && now >= m.NextVaultCheck)
            {
                m.NextVaultCheck = now + 0.2f;
                if (TryVault(a, m, dir, now)) return Result.Moving;
            }

            // Facing: where he goes, or towards the enemy for a short side step.
            Vector3? enemy = m.FaceEnemy ? m.Enemy?.Invoke() : null;
            Vector3 face = dir;
            if (enemy.HasValue)
            {
                Vector3 e = Flat(enemy.Value - pos);
                if (e.sqrMagnitude > 0.25f) face = e.normalized;
            }
            float faceYaw = Quaternion.LookRotation(face, Vector3.up).eulerAngles.y;
            float yaw = CombatAI.TurnTowards(a, faceYaw, m.FaceEnemy ? 400f : 540f);

            // Clip by the move direction relative to where he faces.
            float rel = Mathf.DeltaAngle(yaw, Quaternion.LookRotation(dir, Vector3.up).eulerAngles.y);

            // Turning to run: first turn on the spot (rifle held), then run - no backwards run while turning.
            if (!m.FaceEnemy && Mathf.Abs(rel) > 60f)
            {
                // (only in a fight with the rifle held; just following, he turns in the pose he is in - the rifle
                // flashed up at the start of every move in caves)
                string held = m.Enemy != null ? CombatLook.HeldClip(a, 0) : null;
                if (held != null && m.Clip == null) { CombatLook.Pose(a, held, 0.15f); m.Clip = held; }
                m.ProgressAt = now; // turning is progress
                return Result.Moving;
            }
            string clip;
            if (!m.FaceEnemy && UseOwnRun())
            {
                // His own locomotion: leave the pose states for the state he walks in and drive its speed value.
                clip = OwnRun;
                if (m.Clip != OwnRun)
                {
                    CombatLook.EndPoses(a, CombatAI.FreeStateOf(a));
                    m.Clip = OwnRun;
                }
                else CheckOwnRun(a, m);
            }
            else
            {
                clip = Mathf.Abs(rel) <= 50f ? RunF : Mathf.Abs(rel) >= 130f ? RunB : rel > 0f ? RunR : RunL;
                if (!CombatLook.HasClip(clip)) clip = CombatLook.First(RunF, "PlayerAWalkMP");
                if (clip != m.Clip)
                {
                    CombatLook.Pose(a, clip, m.Clip == null ? 0.2f : 0.15f);
                    m.Clip = clip;
                }
            }

            // Carry him.
            float speed = Mathf.Min(clip == OwnRun ? OwnRunSpeed : ClipSpeed(clip), left / dt);
            if (clip == OwnRun) { SetSpeedParam(a, speed); m.OwnSpeedSet = speed; }
            Vector3 step = dir * speed * dt;
            Carry carry = m.ForcePos ? Carry.Pos : CurrentCarry();
            if (carry != Carry.Root) m.Expected += step.magnitude;
            else m.Expected += (clip == OwnRun ? OwnRunSpeed : ClipSpeed(clip)) * dt;
            try
            {
                if (carry == Carry.Move)
                {
                    var ms = a._moveState;
                    if (ms != null) ms.Move(step + Vector3.down * 3f * dt, a, dt);
                }
                else if (carry == Carry.Pos)
                {
                    // Caves (ForcePos): the direct move would walk through rock - stop at a wall (the stuck check ends the move).
                    if (m.ForcePos && step.sqrMagnitude > 1e-6f && WallAhead(pos, step)) step = Vector3.zero;
                    Vector3 np = pos + step;
                    if (GroundBelow(np, out Vector3 g)) np.y = g.y;
                    var ms = a._moveState;
                    if (ms != null) ms.SetPosition(np);
                    a.transform.position = np;
                }
            }
            catch (Exception e) { CombatAI.DebugOnce("carry" + carry, $"Moving ({Name(carry)}): {e.Message}"); }
            KeepOnGround(a, dt);

            Calibrate(m, now, carry);

            // No progress for a while: blocked by something.
            if (now - m.ProgressAt > 1.0f)
            {
                if (Flat(pos - m.ProgressPos).magnitude < 0.3f && m.Calibrated)
                {
                    CombatAI.Debug($"{a.name}: cannot get further ({m.Why})");
                    Finish(m, m.Travelled > 1f);
                    return m.Travelled > 1f ? Result.Arrived : Result.Failed;
                }
                m.ProgressPos = pos;
                m.ProgressAt = now;
            }
            return Result.Moving;
        }

        /// <summary>
        /// The mover does not follow the ground reliably downhill or over sand dunes (he sank in to the knees): put
        /// his feet back on the ground below him. Sunk in: straight up. Floating up to 0.8 m: down at falling speed.
        /// </summary>
        private const string OwnRun = "(own run)";
        private const float OwnRunSpeed = 4.0f;
        private static bool _ownBroken;

        private static bool UseOwnRun() =>
            !_ownBroken && !string.Equals((Config.CombatRunAnim.Value ?? "own").Trim(), "player", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// His own locomotion only works if nothing else resets the speed value we set: read it back the next frame.
        /// Reset five times in a row = his own code owns it - use the player run clips for the rest of the session.
        /// </summary>
        private static void CheckOwnRun(VailActor a, Move m)
        {
            if (m.OwnSpeedSet < 1f) return;
            try
            {
                Animator an = CombatLook.AnimatorOf(a);
                var p = a._speedFloatParam;
                if (!an || !p.Exists) { _ownBroken = true; return; }
                float now = an.GetFloat(p.Id);
                if (now < m.OwnSpeedSet * 0.3f) m.OwnBad++;
                else m.OwnBad = 0;
                if (m.OwnBad >= 5)
                {
                    _ownBroken = true;
                    m.Clip = null; // the next frame picks a player run clip
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Move] own run animation: his speed value is overwritten ({now:0.0} instead of {m.OwnSpeedSet:0.0}) - using the player run clips");
                }
                else if (FirstOwnOk && m.OwnBad == 0)
                {
                    FirstOwnOk = false;
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Move] own run animation active (speed value {now:0.0} is kept)");
                }
            }
            catch { _ownBroken = true; }
        }

        private static bool FirstOwnOk = true;

        /// <summary>His animator's movement speed value (VailActor._speedFloatParam), which drives his locomotion.</summary>
        internal static void StandStill(VailActor a) => SetSpeedParam(a, 0f);

        private static void SetSpeedParam(VailActor a, float speed)
        {
            try
            {
                Animator an = CombatLook.AnimatorOf(a);
                if (!an) return;
                var p = a._speedFloatParam;
                if (p.Exists) an.SetFloat(p.Id, speed);
            }
            catch { }
        }

        private static void KeepOnGround(VailActor a, float dt)
        {
            try
            {
                Vector3 p = a.transform.position;
                if (!GroundBelow(p, out Vector3 g)) return;
                float diff = g.y - p.y;
                if (Mathf.Abs(diff) < 0.03f || diff < -0.8f) return;
                if (diff < 0f) diff = Mathf.Max(diff, -6f * dt);
                Vector3 np = new Vector3(p.x, p.y + diff, p.z);
                var ms = a._moveState;
                if (ms != null) ms.SetPosition(np);
                a.transform.position = np;
                if (Mathf.Abs(diff) > 0.15f) CombatAI.DebugOnce("ground", $"{a.name}: ground height corrected by {diff:0.00} m (further corrections not reported)");
            }
            catch { }
        }

        /// <summary>
        /// After half a second of moving, compare how far he got with how far we pushed him. Much less: our push
        /// does nothing, try the next way. Much more: the clip's root motion moves him as well, stop pushing.
        /// </summary>
        private static void Calibrate(Move m, float now, Carry used)
        {
            if (m.Calibrated || now - m.Started < 0.5f || m.Expected < 0.6f) return;
            m.Calibrated = true;
            if (Forced() != null) return;
            float ratio = m.Travelled / m.Expected;
            Carry next = used;
            if (ratio < 0.35f) next = used == Carry.Move ? Carry.Pos : used == Carry.Root ? Carry.Move : Carry.Pos;
            else if (ratio > 1.7f && used != Carry.Root) next = Carry.Root;
            if (next != used) { _carry = next; _carryConfirmed = 0; }
            else _carryConfirmed++;
            if (next != used || !_carryLogged)
            {
                _carryLogged = true;
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Move] {m.Actor.name}: {m.Travelled:0.00} m moved, expected {m.Expected:0.00} m with '{Name(used)}' " +
                         $"(factor {ratio:0.00}){(next != used ? $" - switching to '{Name(next)}'" : " - fits")}");
            }
        }

        private static void Finish(Move m, bool ok)
        {
            Moves.Remove(m.Actor.Pointer);
            if (m.Clip == OwnRun) SetSpeedParam(m.Actor, 0f);
            CombatAI.Debug($"{m.Actor.name}: {(ok ? "am Ziel" : "Ziel nicht erreicht")} ({m.Why}, {m.Travelled:0.0} m)");
        }

        internal static void Clear()
        {
            Moves.Clear();
            ClipSpeeds.Clear();
            _navBroken = false;
            _navLogged = false;
            _ownBroken = false;
            FirstOwnOk = true;
        }

        // ------------------------------------------------------------------ finding positions

        internal static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        private static int _rejGround, _rejPath, _rejEnemy, _rejSquad, _rejSight;

        /// <summary>The best candidates of the last FindSpot, best first (position, has cover).</summary>
        internal static readonly List<(Vector3 pos, bool cover)> Candidates = new();

        /// <summary>
        /// A spot for the purpose, or false. Candidates are sampled around the soldier; each must be on the ground,
        /// reachable in a straight line at chest height, not too close to the enemy or to other soldiers.
        /// Cover = something solid between the enemy and the spot at body height while the head still sees him.
        /// </summary>
        public static bool FindSpot(VailActor s, VailActor enemy, Vector3 enemyPos, Purpose purpose, out Vector3 spot, out bool cover)
        {
            spot = Vector3.zero;
            cover = false;
            if (!s) return false;
            Vector3 sp = s.transform.position;
            Vector3 away = Flat(sp - enemyPos);
            if (away.sqrMagnitude < 0.01f) away = -s.transform.forward;
            away.Normalize();
            float enemyDist = Flat(sp - enemyPos).magnitude;
            float minDist = Config.CombatMinDistance.Value;
            float range = Config.CombatRange.Value;
            _rejGround = _rejPath = _rejEnemy = _rejSquad = _rejSight = 0;
            var scored = new List<(float score, Vector3 pos, bool cover)>();

            Vector3 player = Vector3.zero;
            bool havePlayer = false;
            try { if (LocalPlayer.Transform) { player = LocalPlayer.Transform.position; havePlayer = true; } } catch { }

            float best = float.MinValue;
            bool found = false;
            int samples = purpose == Purpose.Cover || purpose == Purpose.Sight ? 24 : 16;
            for (int i = 0; i < samples; i++)
            {
                float angle, dist;
                switch (purpose)
                {
                    case Purpose.BackStep: angle = Rand(-50f, 50f); dist = Rand(4f, 6.5f); break;
                    case Purpose.Approach: angle = 180f + Rand(-30f, 30f); dist = Mathf.Clamp(enemyDist - 4f, 2f, 8f); break;
                    case Purpose.Chase: angle = 180f + Rand(-20f, 20f); dist = Mathf.Clamp(enemyDist - 12f, 3f, 14f); break;
                    case Purpose.Retreat: angle = Rand(-70f, 70f); dist = Rand(4f, 8f); break;
                    case Purpose.Fallback: angle = Rand(-40f, 40f); dist = Rand(6f, 11f); break;
                    case Purpose.Shift: angle = (i % 2 == 0 ? 1 : -1) * Rand(60f, 110f); dist = Rand(2f, 4.5f); break;
                    case Purpose.Advance: angle = 180f + Rand(-35f, 35f); dist = Mathf.Clamp(enemyDist - range * 0.6f, 4f, 14f); break;
                    case Purpose.Cover: angle = Rand(-150f, 150f); dist = Rand(2f, 8f); break;
                    default: angle = Rand(-180f, 180f); dist = Rand(2f, 7f); break; // Sight
                }
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * away;
                if (purpose == Purpose.Fallback && havePlayer)
                {
                    Vector3 toPlayer = Flat(player - sp);
                    if (toPlayer.magnitude > 3f) dir = Quaternion.Euler(0f, Rand(-30f, 30f), 0f) * toPlayer.normalized;
                    dist = Mathf.Min(dist, Mathf.Max(3f, toPlayer.magnitude - 2f));
                }
                Vector3 cand = sp + dir * dist;
                if (!Ground(cand, sp.y, out cand)) { _rejGround++; continue; }
                // With the game's path finding the way is checked when the move starts; without it, straight only.
                if (!NavAvailable && !StraightPathFree(s, sp, cand)) { _rejPath++; continue; }

                float candEnemy = Flat(cand - enemyPos).magnitude;
                bool awayMove = purpose == Purpose.BackStep || purpose == Purpose.Retreat;
                // Backing away must increase the distance; other moves must not end up closer than the minimum.
                bool closer = purpose == Purpose.Advance || purpose == Purpose.Approach || purpose == Purpose.Chase;
                if (awayMove ? candEnemy < enemyDist + 1f : candEnemy < Mathf.Min(minDist, enemyDist) && !closer) { _rejEnemy++; continue; }
                if (purpose == Purpose.Approach && candEnemy < 3f) { _rejEnemy++; continue; }
                if (purpose == Purpose.Chase && candEnemy < 6f) { _rejEnemy++; continue; }
                if (candEnemy > range * 1.1f && purpose != Purpose.Fallback) { _rejEnemy++; continue; }
                if (TooCloseToSquad(s, cand)) { _rejSquad++; continue; }

                bool sees = !enemy || CombatAI.VisibleFrom(cand, enemy);
                bool coverHere = purpose != Purpose.BackStep && CoverAt(cand, enemyPos, enemy);
                if (purpose == Purpose.Sight && !sees) { _rejSight++; continue; }
                if (purpose == Purpose.Cover && (!coverHere || !sees)) { _rejSight++; continue; }

                float score;
                switch (purpose)
                {
                    case Purpose.BackStep:
                    case Purpose.Retreat: score = candEnemy; break;
                    case Purpose.Fallback: score = havePlayer ? -Flat(cand - player).magnitude : candEnemy; break;
                    case Purpose.Advance: score = -Mathf.Abs(candEnemy - range * 0.6f); break;
                    case Purpose.Approach: score = -candEnemy; break;
                    case Purpose.Chase: score = -Mathf.Abs(candEnemy - 12f); break;
                    case Purpose.Shift: score = 0f; break;
                    default: score = -dist; break;
                }
                if (sees) score += 6f;
                if (coverHere && Config.CombatCover.Value) score += 5f;
                score += Rand(0f, 1.5f); // no two soldiers pick the exact same spot
                scored.Add((score, cand, coverHere));
                if (score > best) { best = score; spot = cand; cover = coverHere; found = true; }
            }
            scored.Sort((x, y) => y.score.CompareTo(x.score));
            Candidates.Clear();
            for (int i = 0; i < scored.Count && i < 4; i++) Candidates.Add((scored[i].pos, scored[i].cover));
            return found;
        }

        /// <summary>Why the last FindSpot found nothing (counts per reason).</summary>
        internal static string RejectInfo() =>
            $"ground {_rejGround}, path blocked {_rejPath}, enemy distance {_rejEnemy}, comrades {_rejSquad}, no sight/cover {_rejSight}";

        private static float Rand(float a, float b) => a + (float)Rng.NextDouble() * (b - a);

        /// <summary>
        /// Highest solid ground (not an actor) below p, looking from <paramref name="above"/> metres over p. For his feet
        /// this is only a step high (0.5 m): a shelter roof or a log at chest height is an obstacle, not ground
        /// (looking from 1.8 m once put him on top of a shelter - the "climbing over nothing").
        /// </summary>
        private static bool GroundBelow(Vector3 p, out Vector3 ground, float above = 0.5f)
        {
            ground = p;
            try
            {
                var hits = Physics.RaycastAll(p + Vector3.up * above, Vector3.down, above + 2.5f, ~0, QueryTriggerInteraction.Ignore);
                float bestD = float.MaxValue;
                bool any = false;
                foreach (var h in hits)
                {
                    if (!h.collider || h.distance >= bestD) continue;
                    if (h.collider.GetComponentInParent<VailActor>()) continue;
                    if (CombatFeet.OurObject(h.collider.transform)) continue;
                    bestD = h.distance;
                    ground = h.point;
                    any = true;
                }
                return any;
            }
            catch { return false; }
        }

        /// <summary>Ground below the point within 2 m height of the soldier (rocks count as ground).</summary>
        private static bool Ground(Vector3 p, float refY, out Vector3 ground)
        {
            if (!GroundBelow(new Vector3(p.x, refY, p.z), out ground, 2.3f)) return false;
            return Mathf.Abs(ground.y - refY) <= 2f;
        }

        /// <summary>Nothing solid in the way at chest height (a straight run must be possible; low rocks are stepped over).</summary>
        private static bool StraightPathFree(VailActor s, Vector3 from, Vector3 to) =>
            !CombatAI.Blocked(from + Vector3.up * 1.3f, to + Vector3.up * 1.3f, s, null, out _);

        /// <summary>Something solid close in front of the spot (seen from the enemy) at crouch/chest height.</summary>
        internal static bool CoverAt(Vector3 spot, Vector3 enemyPos, VailActor enemy)
        {
            Vector3 from = enemyPos + Vector3.up * 1.2f;
            Vector3 to = spot + Vector3.up * 0.9f;
            if (!CombatAI.Blocked(from, to, null, enemy, out float atDist)) return false;
            float total = Vector3.Distance(from, to);
            return total - atDist < 3f; // the obstacle is near the soldier, not near the enemy
        }

        private static bool TooCloseToSquad(VailActor self, Vector3 p)
        {
            foreach (var (a, _) in SoldierRegistry.All())
            {
                if (!a || a.Pointer == self.Pointer) continue;
                Vector3 other = Moves.TryGetValue(a.Pointer, out var m) ? m.Goal : a.transform.position;
                if (Flat(other - p).magnitude < 2f) return true;
            }
            return false;
        }
    }
}
