using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Animation.PlayerControl;
using Sons.Areas;
using TheForest.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CharacterReplacement
{
    /// <summary>
    /// Soldiers following the player into caves (the game stops Kelvin-type companions at the cave mouth).
    ///   Entering: when the player goes into a cave, every soldier following him within 40 m walks to the entrance
    ///   spot the player used (the entrance sequence's alignment point), turns the same way, plays the same entrance
    ///   animation the game shows other players (PlayerAEnterCaveB) and comes out at the matching point inside.
    ///   Inside: his own AI is off; he follows on the game's path finding (combat moves), and where that cannot reach
    ///   the player (ropes, crawl spaces, water) he is put just behind the player while the player is not looking.
    ///   Leaving: soldiers near the exit play the exit animation (exitCave1), the others are put behind the player
    ///   outside out of sight; his own AI (and its follow) takes over again.
    /// Config cave_follow. Fights inside run as usual (the combat AI switches his AI off itself).
    /// </summary>
    internal static class CaveFollow
    {
        private enum Step { WalkToEntrance, Entering, Inside, WalkToExit, Exiting }

        private sealed class Job
        {
            public Step Step;
            public Vector3 From, To;          // alignment point outside / inside (entering) or inside / outside (exiting)
            public Quaternion FromRot;
            public float Until, NextCheck, LastProgress, BestDist = float.MaxValue;
            public bool WasEngaged, Stuck, HeightLogged, HasFloor, FollowOnExit;
            public float NextTally;
            public float StandSince = -1f, NextFidget, FidgetUntil;

            public readonly Dictionary<string, int> Clips = new();
            public int CatchUps;
            public Vector3 LastFloor;
        }

        private const string EnterClip = "PlayerAEnterCaveB";
        private const string ExitClip = "exitCave1";
        private static readonly Dictionary<IntPtr, Job> Jobs = new();
        private static bool _wasInCave;
        private static float _nextAdopt;
        // The way the player went in the cave (a point about every metre): soldiers walk along it - caves are barely on
        // the nav graphs of Kelvin-type actors, straight lines run into rocks and ledges - and catch up onto it.
        private static readonly List<Vector3> Trail = new();
        private static float _next;

        internal static bool IsBusy(VailActor a) => a && Jobs.TryGetValue(a.Pointer, out var j) && j.Step != Step.Inside;
        internal static bool IsInside(VailActor a) => a && Jobs.ContainsKey(a.Pointer);

        internal static void Tick()
        {
            if (!Config.CaveFollow.Value) { if (Jobs.Count > 0) ReleaseAll("switched off"); return; }
            Transform p = LocalPlayer.Transform;
            if (!p) return;
            float now = Time.time;
            bool inCave = false;
            try { inCave = CaveEntranceManager.IsInCaves; } catch { }

            if (inCave && !_wasInCave) OnPlayerEntered(p);
            else if (!inCave && _wasInCave) OnPlayerLeft(p);
            _wasInCave = inCave;
            if (inCave && now >= _nextAdopt) { _nextAdopt = now + 2f; Adopt(p); }
            if (inCave) Record(p); else if (Trail.Count > 0) Trail.Clear();

            if (Jobs.Count == 0) return;
            var done = new List<IntPtr>();
            foreach (var kv in Jobs)
            {
                VailActor a = Find(kv.Key);
                if (!a || !a.gameObject.activeInHierarchy || a.IsDead()) { done.Add(kv.Key); continue; }
                Tally(a, kv.Value, now);
                // In a cave (marked in), whatever he does - following, walking out, fighting: keep him on the floor.
                if (kv.Value.Step != Step.Entering && !CombatMove.IsVaulting(a) && InCave(a)) { try { HoldOnFloor(a, kv.Value); } catch { } }
                try { if (!Step1(a, kv.Value, p, now)) done.Add(kv.Key); }
                catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Cave] {a.name}: {e.Message}"); done.Add(kv.Key); }
            }
            foreach (var k in done)
            {
                var a = Find(k);
                bool follow = Jobs.TryGetValue(k, out var jj) && jj.FollowOnExit;
                Jobs.Remove(k);
                if (!a) continue;
                Free(a);
                if (follow) GiveFollow(a, p);
            }
        }

        // ------------------------------------------------------------------ entering / leaving

        private static void OnPlayerEntered(Transform p)
        {
            if (!Nearest(p.position, CaveEntranceCutscene.PathingDirection.Entering, 20f, out var outside, out var outsideRot)) { RLog.Msg($"{CharacterReplacement.Tag} [Cave] player in the cave, but no entrance found within 20 m"); return; }
            if (!Nearest(outside, CaveEntranceCutscene.PathingDirection.Exiting, 8f, out var inside, out _)) inside = outside + outsideRot * Vector3.forward * 3f;
            foreach (var (a, _) in SoldierRegistry.All())
            {
                if (!Eligible(a) || Vector3.Distance(a.transform.position, outside) > 40f) continue;
                Take(a);
                Jobs[a.Pointer] = new Job { Step = Step.WalkToEntrance, From = outside, FromRot = outsideRot, To = inside, LastProgress = Time.time };
                Go(a, outside, "goes to the cave entrance");
            }
            RLog.Msg($"{CharacterReplacement.Tag} [Cave] player enters the cave at {outside} - {Jobs.Count} soldier(s) follow");
        }

        private static void OnPlayerLeft(Transform p)
        {
            if (Jobs.Count == 0) return;
            bool exitFound = Nearest(p.position, CaveEntranceCutscene.PathingDirection.Exiting, 20f, out var inside, out var insideRot);
            Vector3 outside = p.position;
            if (exitFound && !Nearest(inside, CaveEntranceCutscene.PathingDirection.Entering, 8f, out outside, out _)) outside = p.position;
            foreach (var kv in new List<KeyValuePair<IntPtr, Job>>(Jobs))
            {
                VailActor a = Find(kv.Key);
                if (!a) continue;
                var j = kv.Value;
                if (exitFound && Vector3.Distance(a.transform.position, inside) < 30f)
                {
                    j.Step = Step.WalkToExit; j.From = inside; j.FromRot = insideRot; j.To = outside; j.LastProgress = Time.time; j.BestDist = float.MaxValue;
                    Go(a, inside, "goes to the cave exit");
                }
                else { Mark(a, p.position, false); CatchUp(a, p, true); bool f = kv.Value.FollowOnExit; Jobs.Remove(kv.Key); Free(a); if (f) GiveFollow(a, p); }
            }
        }

        /// <summary>
        /// The player is in a cave and a soldier following him is not looked after (a save loaded inside the cave -
        /// his own AI then ran on the spot - or he came in some other way): take him over. Already in the cave
        /// (within 50 m): marked in, kept where he is; anywhere else: put behind the player out of sight.
        /// </summary>
        private static void Adopt(Transform p)
        {
            AreaMask area = AreaMask.None;
            try { area = CaveEntranceManager.CurrentAreaMask; } catch { }
            foreach (var (a, _) in SoldierRegistry.All())
            {
                if (!a || !a.gameObject.activeInHierarchy || a.IsDead() || Jobs.ContainsKey(a.Pointer)) continue;
                if (SoldierHealth.IsDowned(a) || CombatAI.IsEngaged(a)) continue;
                float d = Vector3.Distance(a.transform.position, p.position);
                bool following = CombatOrders.Following(a, out _);
                // In the cave with him (a save loaded inside: the follow order is not saved) - or following him.
                // (after loading, the game may have put him on the terrain right above the cave: height does not matter)
                bool withHim = CombatMove.Flat(a.transform.position - p.position).magnitude < 60f;
                if (!(following && d < 150f) && !withHim) continue;
                Take(a);
                MarkArea(a, area, "takeover");
                var j = new Job { Step = Step.Inside, NextCheck = Time.time + 1f, FollowOnExit = !following };
                Jobs[a.Pointer] = j;
                bool near = d < 50f && Mathf.Abs(a.transform.position.y - p.position.y) < 3f;
                if (near) Place(a, Ground(a.transform.position + Vector3.up * 1f), a.transform.rotation);
                else CatchUp(a, p, true);
                CombatAI.Debug($"{a.name}: taken over in the cave ({d:0} m from the player{(near ? "" : ", moved behind him")})");
            }
        }

        // ------------------------------------------------------------------ one soldier, one step

        private static bool Step1(VailActor a, Job j, Transform p, float now)
        {
            if (CombatAI.IsEngaged(a) || SoldierHealth.IsDowned(a)) { j.WasEngaged = true; return true; } // the fight / being down comes first
            if (j.WasEngaged) { j.WasEngaged = false; try { a.EnableController(false); } catch { } } // the fight switched his AI back on
            switch (j.Step)
            {
                case Step.WalkToEntrance:
                case Step.WalkToExit:
                {
                    bool entering = j.Step == Step.WalkToEntrance;
                    float d = CombatMove.Flat(a.transform.position - j.From).magnitude;
                    if (d < j.BestDist - 0.5f) { j.BestDist = d; j.LastProgress = now; }
                    if (CombatMove.IsMoving(a)) CombatMove.Tick(a);
                    // There, or stuck for 4 s (then he is put there - the player is going through himself).
                    if (d > 1.2f && now - j.LastProgress < 4f)
                    {
                        if (!CombatMove.IsMoving(a) && now >= j.NextCheck) { j.NextCheck = now + 1f; Go(a, j.From, entering ? "goes to the cave entrance" : "goes to the cave exit"); }
                        return true;
                    }
                    CombatMove.Stop(a, quiet: true);
                    Place(a, j.From, j.FromRot);
                    string clip = entering ? EnterClip : ExitClip;
                    var c = CombatLook.Clip(clip);
                    CombatLook.EnsureApplied(a);
                    bool played = c && CombatLook.Pose(a, clip, 0.25f, restart: true);
                    j.Until = now + (played ? Mathf.Clamp(c.length, 1f, 8f) : 0.5f);
                    j.Step = entering ? Step.Entering : Step.Exiting;
                    CombatAI.Debug($"{a.name} {(entering ? "kriecht in die Hoehle" : "verlaesst die Hoehle")} ({(played ? clip : "ohne Animation")})");
                    return true;
                }
                case Step.Entering:
                case Step.Exiting:
                {
                    Place(a, j.From, j.FromRot); // the clip plays on the spot
                    if (now < j.Until) return true;
                    CombatLook.EndPoses(a, CombatAI.FreeStateOf(a));
                    // In (or out of) the cave first, then placed - else the first frame lifts him onto the terrain.
                    Mark(a, j.From, j.Step == Step.Entering);
                    Vector3 to = Ground(j.To);
                    Place(a, to, j.FromRot);
                    if (j.Step == Step.Exiting) return false; // outside: his own AI again
                    j.Step = Step.Inside;
                    j.NextCheck = now + 1f;
                    return true;
                }
                case Step.Inside:
                {
                    if (CombatMove.IsMoving(a))
                    {
                        j.StandSince = -1f;
                        var r = CombatMove.Tick(a);
                        if (r == CombatMove.Result.Moving) return true;
                        if (r == CombatMove.Result.Failed) j.Stuck = true; // a wall / ledge he cannot pass
                    }
                    // Standing: the rifle-at-rest pose (else the last run clip keeps playing - "running on the spot").
                    if (!CombatMove.IsMoving(a)) Idle(a, j, now);
                    if (now < j.NextCheck) return true;
                    j.NextCheck = now + 1f;
                    float d = Vector3.Distance(a.transform.position, p.position);
                    float dy = a.transform.position.y - p.position.y;
                    if (Mathf.Abs(dy) > 3f && !j.HeightLogged) { j.HeightLogged = true; CombatAI.Debug($"{a.name}: in the cave {dy:0.0} m above/below the player (moving {CombatMove.IsMoving(a)})"); }
                    if (d < 5f) { j.Stuck = false; return true; }
                    // Stuck, or far behind: catch up where the player does not see it. Otherwise walk to him.
                    if ((j.Stuck && d > 7f) || d > 25f) { CatchUp(a, p, false); j.Stuck = false; return true; }
                    // to 3 m short of the player on his own side (not "behind the player": that may be the far side)
                    if (!j.Stuck && !FollowTrail(a) && !Go(a, p.position + CombatMove.Flat(a.transform.position - p.position).normalized * 3f, "follows in the cave")) j.Stuck = true;
                    return true;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Standing: his own idle (rifle on the back) - our poses ended and his speed value at 0, else the
        /// last run clip / run speed kept him running on the spot.</summary>
        // Standing in a cave: the player's plain standing loop, kept in our pose system. Leaving the pose system for
        // his own idle replayed an idle variant on every stop, and coming back into it flashed the rifle-raise.
        private const string StandClip = "idleNoWobble";

        /// <summary>Which clip he plays, sampled 4x a second (the status shows it: rifle-raise or head-bump = a bug).</summary>
        private static void Tally(VailActor a, Job j, float now)
        {
            if (now < j.NextTally) return;
            j.NextTally = now + 0.25f;
            try
            {
                Animator an = CombatLook.AnimatorOf(a);
                if (!an) return;
                foreach (var ci in an.GetCurrentAnimatorClipInfo(0))
                    if (ci.clip && ci.weight > 0.3f) { j.Clips.TryGetValue(ci.clip.name, out int n); j.Clips[ci.clip.name] = n + 1; }
            }
            catch { }
        }

        // His own idle variants, played now and then while he stands (not right when he stops).
        private static readonly string[] Fidgets = { "IdleLookVar1", "IdleScratchHead", "idleCalm2", "IdleSwatFly", "IdleCalm" };

        private static void Idle(VailActor a, Job j, float now)
        {
            if (CombatLook.CurrentPose(a) == null) RememberIdle(a);
            CombatLook.EnsureApplied(a);
            CombatMove.StandStill(a);
            if (j.StandSince < 0f) { j.StandSince = now; j.NextFidget = now + UnityEngine.Random.Range(8f, 20f); j.FidgetUntil = 0f; }
            if (j.FidgetUntil > 0f)
            {
                if (now < j.FidgetUntil) return;
                j.FidgetUntil = 0f;
                j.NextFidget = now + UnityEngine.Random.Range(10f, 25f);
            }
            else if (now >= j.NextFidget)
            {
                string f = Fidgets[UnityEngine.Random.Range(0, Fidgets.Length)];
                var c = CombatLook.Clip(f);
                if (c && CombatLook.Pose(a, f, 0.4f, restart: true)) { j.FidgetUntil = now + Mathf.Clamp(c.length, 2f, 12f) - 0.4f; return; }
                j.NextFidget = now + 15f;
            }
            if (CombatLook.HasClip(StandClip)) { if (CombatLook.CurrentPose(a) != StandClip) CombatLook.Pose(a, StandClip, 0.35f); }
            else if (CombatLook.CurrentPose(a) != null) CombatLook.EndPoses(a, IdleState(a));
        }


        private static readonly Dictionary<IntPtr, int> IdleStates = new();
        private static readonly int[] IdleNames = { Animator.StringToHash("Idle"), Animator.StringToHash("IdleCalm"), Animator.StringToHash("Base Layer.Idle") };

        /// <summary>Notes the state he is in when it plays an idle clip (no pose of ours, not a hit/bump reaction).</summary>
        private static void RememberIdle(VailActor a)
        {
            try
            {
                Animator an = CombatLook.AnimatorOf(a);
                if (!an || an.IsInTransition(0)) return;
                foreach (var ci in an.GetCurrentAnimatorClipInfo(0))
                    if (ci.clip && ci.clip.name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        IdleStates[a.Pointer] = an.GetCurrentAnimatorStateInfo(0).fullPathHash;
                        return;
                    }
            }
            catch { }
        }

        private static int IdleState(VailActor a)
        {
            if (IdleStates.TryGetValue(a.Pointer, out int h)) return h;
            try
            {
                Animator an = CombatLook.AnimatorOf(a);
                if (an) foreach (int n in IdleNames) if (an.HasState(0, n)) return n;
            }
            catch { }
            return 0;
        }

        private static void Record(Transform p)
        {
            Vector3 pos = p.position;
            if (Trail.Count > 0 && Vector3.Distance(Trail[Trail.Count - 1], pos) < 0.9f) return;
            // only points on a floor (not while falling or jumping)
            Vector3 g = Ground(pos + Vector3.up * 0.5f);
            if (Mathf.Abs(g.y - pos.y) > 0.6f) return;
            Trail.Add(g);
            if (Trail.Count > 500) Trail.RemoveRange(0, 100);
        }

        /// <summary>Index of the trail point nearest to him (within 6 m, about the same height), -1 if none.</summary>
        private static int NearestOnTrail(Vector3 pos)
        {
            int best = -1; float bd = 6f;
            for (int i = 0; i < Trail.Count; i++)
            {
                if (Mathf.Abs(Trail[i].y - pos.y) > 2f) continue;
                float d = Vector3.Distance(Trail[i], pos);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>Walks along the player's trail from the point nearest to him to a few metres short of the player.</summary>
        private static bool FollowTrail(VailActor a)
        {
            int from = NearestOnTrail(a.transform.position);
            int to = Trail.Count - 3;
            if (from < 0 || to <= from) return false;
            var path = Trail.GetRange(from + 1, to - from);
            if (path.Count == 0) return false;
            CombatLook.EnsureApplied(a);
            return CombatMove.Start(a, path[path.Count - 1], null, "follows in the cave (trail)", false, true, true, path);
        }

        /// <summary>A soldier that came out of the cave with the player follows him (as if told so).</summary>
        private static void GiveFollow(VailActor a, Transform p)
        {
            var r = CombatOrders.RobbyOf(a);
            if (!r) return;
            try { r.OnGiveOrderServer(Sons.Gameplay.Robby.OrderType.Follow, 0, 0, p); } catch { }
        }

        private static bool Eligible(VailActor a)
        {
            if (!a || !a.gameObject.activeInHierarchy || a.IsDead() || Jobs.ContainsKey(a.Pointer)) return false;
            if (SoldierHealth.IsDowned(a)) return false;
            return CombatOrders.Following(a, out _);
        }

        /// <summary>His own (Kelvin) AI off - it would walk him back out of the cave.</summary>
        private static void Take(VailActor a)
        {
            CombatAI.ReleaseFor(a);
            try { a.EnableController(false); } catch { }
            // his own (Kelvin) clips - idle variants, jumps - become playable as poses
            try { Animator an = CombatLook.AnimatorOf(a); if (an) CombatLook.RegisterClips(CombatDiag.OwnClips(an)); } catch { }
        }

        private static void Free(VailActor a)
        {
            try { CombatMove.Stop(a, quiet: true); } catch { }
            try { CombatLook.EndPoses(a, IdleState(a) != 0 ? IdleState(a) : CombatAI.FreeStateOf(a)); } catch { }
            try { a.EnableController(true); } catch { }
        }

        /// <summary>Walks there - on the path finding where it reaches, else in a straight line (caves are barely on
        /// the nav graphs of Kelvin-type actors; walls stop him and the stuck check ends the move).</summary>
        private static bool Go(VailActor a, Vector3 to, string why)
        {
            CombatLook.EnsureApplied(a);
            bool cave = InCave(a);
            // In a cave he is moved directly (walls do not stop that): only along a line with nothing solid on it.
            if (cave && !Clear(a.transform.position, to)) return false;
            return CombatMove.Start(a, to, null, why, false, straightIfUnreachable: true, forcePos: cave);
        }

        private static bool Clear(Vector3 from, Vector3 to)
        {
            foreach (float h in new[] { 0.6f, 1.4f })
            {
                Vector3 a = from + Vector3.up * h, b = to + Vector3.up * h;
                foreach (var hit in Physics.RaycastAll(a, (b - a).normalized, (b - a).magnitude, ~0, QueryTriggerInteraction.Ignore))
                    if (hit.collider && !hit.collider.GetComponentInParent<VailActor>() && !CombatFeet.OurObject(hit.collider.transform)) return false;
            }
            return true;
        }

        /// <summary>Puts him just behind the player where the player cannot see it happen (or right away if forced).</summary>
        private static void CatchUp(VailActor a, Transform p, bool force)
        {
            Camera cam = Camera.main;
            // Best: a point of the player's own trail 3-12 m behind him that the camera does not see (walkable floor).
            for (int i = Trail.Count - 4; i >= 0 && i >= Trail.Count - 14; i--)
            {
                Vector3 t = Trail[i];
                float dp = Vector3.Distance(t, p.position);
                if (dp < 3f) continue;
                if (!force && cam && Vector3.Angle(cam.transform.forward, t - cam.transform.position) < 70f
                    && !CombatAI.Blocked(cam.transform.position, t + Vector3.up * 1.2f, a, a, out _)) continue;
                CombatMove.Stop(a, quiet: true);
                Place(a, t, Quaternion.LookRotation(CombatMove.Flat(p.position - t).normalized + Vector3.forward * 0.001f, Vector3.up));
                CombatAI.Debug($"{a.name} catches up in the cave (onto the player's trail, {dp:0} m behind him)");
                if (Jobs.TryGetValue(a.Pointer, out var cj)) cj.CatchUps++;
                return;
            }
            Vector3 behind = p.position - CombatMove.Flat(p.forward).normalized * 3f;
            if (!force && cam)
            {
                Vector3 toS = a.transform.position - cam.transform.position;
                bool seen = Vector3.Angle(cam.transform.forward, toS) < 60f && !CombatAI.Blocked(cam.transform.position, a.transform.position + Vector3.up * 1.2f, a, a, out _);
                Vector3 toB = behind - cam.transform.position;
                if (seen || Vector3.Angle(cam.transform.forward, toB) < 70f) return; // try again in a second
            }
            CombatMove.Stop(a, quiet: true);
            Place(a, Ground(behind), Quaternion.LookRotation(CombatMove.Flat(p.forward).normalized, Vector3.up));
            CombatAI.Debug($"{a.name} catches up in the cave (moved behind the player)");
            if (Jobs.TryGetValue(a.Pointer, out var cj2)) cj2.CatchUps++;
        }

        private static void Place(VailActor a, Vector3 pos, Quaternion rot)
        {
            float yaw = rot.eulerAngles.y;
            try { var ms = a._moveState; if (ms != null) { ms.SetPosition(pos); ms.SetRotation(new Vector3(0f, yaw, 0f), false); } } catch { }
            a.transform.position = pos;
            a.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        /// <summary>The floor right below (the nearest hit - RaycastAll is not sorted; in a cave the first entry can be
        /// the terrain above or below it).</summary>
        private static Vector3 Ground(Vector3 p)
        {
            float best = float.MaxValue;
            Vector3 g = p;
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 1.2f, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!h.collider || h.distance >= best || h.collider.GetComponentInParent<VailActor>() || CombatFeet.OurObject(h.collider.transform)) continue;
                if (LocalPlayer.Transform && h.collider.transform.IsChildOf(LocalPlayer.Transform)) continue; // the player's own colliders
                best = h.distance; g = h.point;
            }
            return g;
        }

        private static bool Nearest(Vector3 near, CaveEntranceCutscene.PathingDirection dir, float within, out Vector3 pos, out Quaternion rot)
        {
            pos = near; rot = Quaternion.identity;
            float best = within;
            bool found = false;
            foreach (var c in Resources.FindObjectsOfTypeAll<CaveEntranceCutscene>())
            {
                try
                {
                    if (!c || !c.gameObject.scene.IsValid() || c._direction != dir) continue;
                    Transform t = c._alignmentTransform ? c._alignmentTransform : c.transform;
                    float d = Vector3.Distance(near, t.position);
                    if (d >= best) continue;
                    best = d; pos = t.position; rot = t.rotation; found = true;
                }
                catch { }
            }
            return found;
        }

        private static readonly Dictionary<IntPtr, float> AboveTerrain = new();

        /// <summary>
        /// His mover does not stand on cave floors (measured: with the terrain lift off he fell 20-40 m through it).
        /// Every frame inside: onto the floor right below him, falling speed cancelled; if he dropped through, back to
        /// the last good spot. (The gravity setting is shared by all Kelvin-type actors, so it is left alone.)
        /// </summary>
        private static void HoldOnFloor(VailActor a, Job j)
        {
            Vector3 pos = a.transform.position;
            float best = float.MaxValue; Vector3 floor = pos; bool found = false;
            foreach (var h in Physics.RaycastAll(pos + Vector3.up * 1.5f, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!h.collider || h.distance >= best || h.collider.GetComponentInParent<VailActor>() || CombatFeet.OurObject(h.collider.transform)) continue;
                best = h.distance; floor = h.point; found = true;
            }
            Vector3 want;
            if (found) { want = floor; j.LastFloor = floor; j.HasFloor = true; }
            else if (j.HasFloor) want = new Vector3(j.LastFloor.x, j.LastFloor.y, j.LastFloor.z);
            else return;
            try { var ms = a._moveState; if (ms != null) { ms._fallingVelocity = Vector3.zero; ms._onGround = true; } } catch { }
            if (Mathf.Abs(pos.y - want.y) < 0.03f && found) return;
            if (!found) want = j.LastFloor; else want = new Vector3(pos.x, want.y, pos.z);
            try { var ms = a._moveState; if (ms != null) ms.SetPosition(want); } catch { }
            a.transform.position = want;
        }

        private static bool InCave(VailActor a)
        {
            try { return a.IsInCave(); } catch { return false; }
        }

        private static void Mark(VailActor a, Vector3 near, bool entering)
        {
            CaveEntrance best = null;
            float bd = 15f;
            foreach (var c in Resources.FindObjectsOfTypeAll<CaveEntrance>())
            {
                if (!c || !c.gameObject.scene.IsValid()) continue;
                float d = Vector3.Distance(c.transform.position, near);
                if (d < bd) { bd = d; best = c; }
            }
            AreaMask area = best ? best._areaId : AreaMask.None;
            if (entering && area == AreaMask.None) { try { area = CaveEntranceManager.CurrentAreaMask; } catch { } }
            MarkArea(a, entering ? area : AreaMask.None, best ? best.name : "-");
        }

        /// <summary>In a cave (area set, terrain lift off) or out of it (both restored).</summary>
        private static void MarkArea(VailActor a, AreaMask area, string why)
        {
            bool entering = area != AreaMask.None;
            // His mover keeps him a minimum distance above the terrain - in a cave (below the terrain) that put him
            // on the surface 11 m above the player. Per actor: switched off inside, restored outside.
            try
            {
                var ms = a._moveState;
                if (ms != null)
                {
                    if (entering) { if (!AboveTerrain.ContainsKey(a.Pointer)) AboveTerrain[a.Pointer] = ms._minDistAboveTerrain; ms._minDistAboveTerrain = -10000f; }
                    else if (AboveTerrain.TryGetValue(a.Pointer, out float v)) { ms._minDistAboveTerrain = v; AboveTerrain.Remove(a.Pointer); }
                }
            }
            catch (Exception e) { CombatAI.Debug($"{a.name}: terrain distance: {e.Message}"); }
            // The entrance trigger only handles the player; an actor's area lives on his world-simulation record.
            try
            {
                var sim = a.GetWorldSimActor();
                if (sim != null) sim.SetAreaMask(area);
            }
            catch (Exception e) { CombatAI.Debug($"{a.name}: setting the area: {e.Message}"); }
            string nowArea = "?", inCave = "?";
            try { nowArea = a.AreaMask.ToString(); inCave = a.IsInCave().ToString(); } catch { }
            CombatAI.Debug($"{a.name}: cave {(entering ? "entered" : "left")} ({why}) - area now {nowArea}, in cave {inCave}");
        }

        private static VailActor Find(IntPtr key)
        {
            foreach (var (a, _) in SoldierRegistry.All()) if (a && a.Pointer == key) return a;
            return null;
        }

        private static void ReleaseAll(string why)
        {
            foreach (var k in new List<IntPtr>(Jobs.Keys)) { var a = Find(k); if (a) Free(a); }
            Jobs.Clear();
            RLog.Msg($"{CharacterReplacement.Tag} [Cave] cave following ended ({why})");
        }

        internal static void Clear() { Jobs.Clear(); _wasInCave = false; Trail.Clear(); }
    }
}
