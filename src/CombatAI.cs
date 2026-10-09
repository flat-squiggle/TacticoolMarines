using System;
using System.Collections.Generic;
using System.Globalization;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using SonsSdk;
using SonsSdk.Networking;
using TheForest.Utils;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Soldier combat. While a hostile actor is around, the soldier's own AI (Kelvin's: flee, cower, hide) is
    /// switched off and this takes over:
    ///   - spotting takes a moment (reaction time), he turns at human speed, raises the gun, settles, then fires
    ///   - fires in short bursts with pauses, reloads after a magazine, aim sways slightly
    ///   - hit chance depends on distance, recoil, target movement, his own movement (CombatHit)
    ///   - repositions: backs off from enemies that get close, falls back when badly hurt, takes cover, moves when
    ///     shot at, finds a spot with line of sight when the target hides, advances on far targets, shifts
    ///     position now and then instead of standing on one spot (CombatMove)
    ///   - squad: soldiers spread over the enemies, prefer enemies that threaten the player or a squadmate, join a
    ///     squadmate's fight even without seeing the enemy, hold fire when the player is in the line of fire
    /// When no enemy has been seen for a few seconds his own AI is switched back on.
    /// Runs where the AI runs (host / single player / dedicated server).
    /// </summary>
    internal static class CombatAI
    {
        private enum Phase { Fight, Move, Reload, Stagger }

        private sealed class Engagement
        {
            public VailActor Soldier;
            public VailActor Target;
            public Phase Phase = Phase.Fight;
            public bool ControllerOff;
            public bool SeesTarget;
            public bool Squad;
            public float StartedAt, LastSeen, ReleaseAfter;
            public Vector3 LastSeenPos;
            public float ReactUntil, AimReadyAt, NextShot, ReloadUntil, NextDecision, StaggerUntil, LastStagger = -99f, LastBackStep = -99f;
            public float StandSince, ShiftAfter, MovedAt, LastHurt, LastHurtMove = -99f, LastFallback = -99f, LastSightMove = -99f, LastRetreat = -99f;
            public int BurstLeft, BurstIndex, MagLeft;
            public bool FirstAfterAim = true;
            public bool FriendlyBlock;
            public bool CoverTried;
            public bool ReloadClip;
            public float Health = -1f, MaxHealth = -1f, NextHealthCheck;
            public IntPtr Told;
            public int Shots, Hits, Kills, Moves;
            public float Sway;
            public string Doing = "noticing an enemy";
            public int ReturnState;
            public Vector3 StartPos;
            public float PrevDist = -1f;
            public bool LookAtWasOff;
            public CombatOrders.Snap Orders;   // what he was doing before (follow, work order) - leash and return
        }


        private static readonly Dictionary<IntPtr, Engagement> Active = new();
        private static readonly List<VailActor> Hostiles = new();
        private static readonly Dictionary<IntPtr, (Vector3 pos, float time, float speed)> Track = new();
        private static readonly System.Random Rng = new();
        private static readonly Dictionary<(IntPtr, IntPtr), float> GiveUp = new();   // (soldier, enemy) -> ignore unseen until
        // Awareness 0..1 of each soldier for each enemy: grows while he sees or hears it (faster when close, in front
        // of him, moving, attacking someone, or when a comrade nearby is fighting it); he attacks at 1.
        private static readonly Dictionary<(IntPtr, IntPtr), float> Aware = new();
        private static readonly HashSet<(IntPtr, IntPtr)> AwareLogged = new();
        private static readonly Dictionary<IntPtr, float> Leashed = new();   // soldier -> only threats until (went too far)
        private static readonly Dictionary<IntPtr, (CombatOrders.Snap snap, float at)> LastOrders = new();
        private static float _lastAwareTick;
        private static readonly Dictionary<IntPtr, int> Mags = new();        // rounds left, kept between fights
        private static readonly Dictionary<IntPtr, int> FreeStates = new();  // animator state before a fight
        private static HashSet<int> _targetTypes;
        private static string _targetTypesFor;
        private static float _nextScan;
        private static bool _rotationWarned;
        private static readonly HashSet<string> Once = new();

        // ------------------------------------------------------------------ logging helpers

        internal static void Debug(string msg)
        {
            if (Config.CombatDebug.Value) RLog.Msg($"{CharacterReplacement.Tag} [Combat] [AI] {msg}");
        }

        internal static void DebugOnce(string key, string msg)
        {
            if (Once.Add(key)) RLog.Warning($"{CharacterReplacement.Tag} [Combat] [AI] {msg}");
        }

        private static float R(float a, float b) => a + (float)Rng.NextDouble() * (b - a);

        // ------------------------------------------------------------------ commands

        public static IEnumerable<string> Command(string mode, string rest)
        {
            var lines = new List<string>();
            var p = (rest ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (mode)
            {
                case "on":
                    Config.CombatEnabled.Value = true;
                    Save();
                    lines.Add("Combat ON.");
                    break;
                case "off":
                    Config.CombatEnabled.Value = false;
                    Save();
                    ReleaseAll();
                    lines.Add("Combat OFF - the soldiers behave like Kelvin again.");
                    break;
                case "debug":
                    Config.CombatDebug.Value = p.Length == 0 ? !Config.CombatDebug.Value : p[0] == "on" || p[0] == "1";
                    Save();
                    lines.Add($"Combat log {(Config.CombatDebug.Value ? "ON" : "OFF")}.");
                    break;
                case "set":
                    lines.Add(Set(p));
                    break;
                default:
                    lines.Add(Settings());
                    lines.Add(Settings2());
                    lines.Add(CombatMove.MethodInfo());
                    lines.Add(CombatHit.ModeInfo());
                    lines.Add($"Enemy types: {TargetTypeNames()}");
                    if (Active.Count == 0) lines.Add("No soldier is fighting right now.");
                    foreach (var e in Active.Values) lines.Add("  " + Line(e));
                    lines.Add($"Enemies near the soldiers (last scan): {Hostiles.Count}");
                    break;
            }
            foreach (var l in lines) RLog.Msg($"{CharacterReplacement.Tag} [Combat] [AI] {l}");
            return lines;
        }

        private const string SetUsage =
            "Usage: tmcombat set range|damage|rate|accuracy|los|move|movemode|cover|mindist|retreat|reaction|burst|mag|reload|hitmode|headshot <value>";

        private static string Set(string[] p)
        {
            if (p.Length < 2) return SetUsage;
            string key = p[0].ToLowerInvariant();
            string val = p[1].ToLowerInvariant();
            bool on = val == "on" || val == "1" || val == "true";
            bool isNum = float.TryParse(val.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float f);
            switch (key)
            {
                case "los": Config.CombatLineOfSight.Value = on; break;
                case "move": Config.CombatMove.Value = on; break;
                case "cover": Config.CombatCover.Value = on; break;
                case "footik": Config.CombatFootIk.Value = on; break;
                case "handsync": Config.CombatHandSync.Value = on; break;
                case "immortal": Config.SoldierImmortal.Value = on; break;
                case "soldierdamage": Config.SoldierDamage.Value = on; break;
                case "movemode":
                    if (val != "auto" && val != "move" && val != "pos" && val != "root") return "movemode: auto, move, pos or root";
                    Config.CombatMoveMode.Value = val; break;
                case "hitmode":
                    if (val != "impact" && val != "direct") return "hitmode: impact or direct";
                    Config.CombatHitMode.Value = val; break;
                default:
                    if (!isNum) return $"'{p[1]}' is not a number. {SetUsage}";
                    switch (key)
                    {
                        case "range": Config.CombatRange.Value = Mathf.Clamp(f, 5f, 200f); break;
                        case "damage": Config.CombatDamage.Value = Mathf.Clamp(f, 0f, 10000f); break;
                        case "downtime": Config.SoldierDownedTime.Value = Mathf.Clamp(f, 3f, 600f); break;
                        case "rate": Config.CombatFireInterval.Value = Mathf.Clamp(f, 0.2f, 10f); break;
                        case "accuracy": Config.CombatAccuracy.Value = Mathf.Clamp01(f); break;
                        case "mindist": Config.CombatMinDistance.Value = Mathf.Clamp(f, 0f, 30f); break;
                        case "retreat": Config.CombatRetreatHealth.Value = Mathf.Clamp01(f); break;
                        case "reaction": Config.CombatReaction.Value = Mathf.Clamp(f, 0f, 5f); break;
                        case "burst": Config.CombatBurst.Value = Mathf.Clamp((int)f, 1, 10); break;
                        case "mag": Config.CombatMagazine.Value = Mathf.Clamp((int)f, 0, 100); break;
                        case "reload": Config.CombatReloadTime.Value = Mathf.Clamp(f, 0.3f, 10f); break;
                        case "headshot": Config.CombatHeadshotChance.Value = Mathf.Clamp01(f); break;
                        default: return $"Unknown value '{key}'. {SetUsage}";
                    }
                    break;
            }
            Save();
            return Settings() + " | " + Settings2();
        }

        private static string Settings() =>
            $"Combat {(Config.CombatEnabled.Value ? "ON" : "OFF")} | range {Config.CombatRange.Value:0} m | damage {(Config.CombatDamage.Value > 0f ? Config.CombatDamage.Value.ToString("0.#") : "like the rifle")} x{Config.CombatDamageScale.Value:0.##} | " +
            $"time between shots {Config.CombatFireInterval.Value:0.##}s | hit chance {Config.CombatAccuracy.Value * 100f:0}% | sight check {(Config.CombatLineOfSight.Value ? "on" : "off")}";

        private static string Settings2() =>
            $"Repositioning {(Config.CombatMove.Value ? "on" : "off")} ({Config.CombatMoveMode.Value}) | cover {(Config.CombatCover.Value ? "on" : "off")} | " +
            $"back-off distance {Config.CombatMinDistance.Value:0.#} m | fall back below {Config.CombatRetreatHealth.Value * 100f:0}% | reaction {Config.CombatReaction.Value:0.##}s | " +
            $"burst up to {Config.CombatBurst.Value} | magazine {Config.CombatMagazine.Value} ({Config.CombatReloadTime.Value:0.#}s) | headshots {Config.CombatHeadshotChance.Value * 100f:0}% | " +
            $"log {(Config.CombatDebug.Value ? "on" : "off")}";

        private static void Save()
        {
            try { Config.Category.SaveToFile(); } catch { }
        }

        private static string Line(Engagement e)
        {
            string t = e.Target ? $"{e.Target.TypeId} in {Dist(e.Soldier, e.Target):0} m{(e.SeesTarget ? "" : " (not visible)")}" : "no target";
            string hp = e.MaxHealth > 0 ? $", health {e.Health / e.MaxHealth * 100f:0}%" : "";
            return $"{(e.Soldier ? e.Soldier.name : "?")}: {e.Doing} - {t}, {e.Shots} shots, {e.Hits} hits, {e.Kills} killed, {e.Moves} position changes, magazine {e.MagLeft}{hp}";
        }

        /// <summary>One line about this soldier's fight, or null when he is not fighting.</summary>
        internal static string Describe(VailActor s) =>
            s && Active.TryGetValue(s.Pointer, out var e) ? Line(e) : null;

        internal static bool IsEngaged(VailActor s) => s && Active.ContainsKey(s.Pointer);

        /// <summary>Where the soldier is aiming: the chest of the target he currently sees.</summary>
        internal static bool AimPoint(VailActor s, out Vector3 point)
        {
            point = Vector3.zero;
            if (!s || !Active.TryGetValue(s.Pointer, out var e) || !e.Target || !e.SeesTarget) return false;
            point = e.Target.transform.position + Vector3.up * 1.15f;
            return true;
        }

        // ------------------------------------------------------------------ main loop

        public static void Tick()
        {
            if (NetUtils.IsMultiplayer && !NetUtils.IsServer) return;
            try { CombatOrders.Watch(Time.time); } catch { }
            try { SoldierHealth.Tick(Time.time); } catch { }
            try { CaveFollow.Tick(); } catch (Exception e) { DebugOnce("cave" + e.Message, $"Cave: {e.Message}"); }
            if (!Config.CombatEnabled.Value)
            {
                if (Active.Count > 0) ReleaseAll();
                return;
            }
            float now = Time.time;
            if (now >= _nextScan)
            {
                _nextScan = now + 0.25f;
                try { ScanHostiles(now); AssignTargets(now); }
                catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [AI] Scan: {e.Message}"); }
            }
            try { CombatHit.Tick(); } catch { }
            try { CombatLook.PoseTick(); } catch { }
            if (Active.Count == 0) return;

            List<IntPtr> done = null;
            foreach (var kv in Active)
            {
                try
                {
                    if (!UpdateEngagement(kv.Value, now)) (done ??= new List<IntPtr>()).Add(kv.Key);
                }
                catch (Exception e)
                {
                    RLog.Warning($"{CharacterReplacement.Tag} [Combat] [AI] {e}");
                    (done ??= new List<IntPtr>()).Add(kv.Key);
                }
            }
            if (done != null) foreach (var k in done) Release(k);
        }

        // ------------------------------------------------------------------ enemies

        private static readonly HashSet<int> NeverTarget = new()
        {
            (int)VailActorTypeId.Robby, (int)VailActorTypeId.Virginia, (int)VailActorTypeId.Timmy, (int)VailActorTypeId.PlayerRobby,
        };

        private static HashSet<int> TargetTypes()
        {
            string s = Config.CombatTargets.Value ?? "";
            if (_targetTypes != null && _targetTypesFor == s) return _targetTypes;
            var set = new HashSet<int>();
            var remove = new HashSet<int>();
            foreach (var raw in s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                var into = set;
                if (part.Length > 1 && part[0] == '-' && part.IndexOf('-', 1) < 0) // "-18" or "-Moose" removes
                {
                    into = remove;
                    part = part.Substring(1);
                }
                if (part.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var cls in new[] { VailActorClassId.Cannibal, VailActorClassId.Creepy })
                    {
                        try { foreach (var t in ActorTools.GetActorTypesOfClass(cls)) into.Add((int)t); }
                        catch (Exception e) { DebugOnce("classes", $"Reading the enemy classes: {e.Message} - using the fixed list"); foreach (int i in Fallback()) into.Add(i); }
                    }
                    continue;
                }
                var range = part.Split('-');
                if (range.Length == 2 && int.TryParse(range[0], out int a) && int.TryParse(range[1], out int b))
                    for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) into.Add(i);
                else if (int.TryParse(part, out int single)) into.Add(single);
                else if (Enum.TryParse(part, true, out VailActorTypeId named)) into.Add((int)named);
            }
            foreach (int r in remove) set.Remove(r);
            foreach (int n in NeverTarget) set.Remove(n);
            if (set.Count == 0) foreach (int i in Fallback()) set.Add(i);
            _targetTypes = set;
            _targetTypesFor = s;
            RLog.Msg($"{CharacterReplacement.Tag} [Combat] [AI] enemy types ({s}): {TargetTypeNames()}");
            return set;
        }

        private static IEnumerable<int> Fallback()
        {
            foreach (var r in new[] { (11, 16), (18, 21), (23, 23), (25, 30), (38, 56), (58, 59) })
                for (int i = r.Item1; i <= r.Item2; i++) yield return i;
        }

        private static string TargetTypeNames()
        {
            if (_targetTypes == null) return "(not read yet)";
            var names = new List<string>();
            foreach (int t in _targetTypes) names.Add(Enum.IsDefined(typeof(VailActorTypeId), t) ? ((VailActorTypeId)t).ToString() : t.ToString());
            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names);
        }

        private static void ScanHostiles(float now)
        {
            Hostiles.Clear();
            foreach (int id in TargetTypes())
            {
                try
                {
                    foreach (var a in ActorTools.GetActors((VailActorTypeId)id))
                    {
                        if (a && a.gameObject.activeInHierarchy && !a.IsDead()) Hostiles.Add(a);
                    }
                }
                catch { }
            }
            // Speed of every enemy (for the hit chance against moving targets).
            var seen = new HashSet<IntPtr>();
            foreach (var h in Hostiles)
            {
                IntPtr k = h.Pointer;
                seen.Add(k);
                Vector3 p = h.transform.position;
                if (Track.TryGetValue(k, out var t) && now > t.time)
                    Track[k] = (p, now, CombatMove.Flat(p - t.pos).magnitude / (now - t.time));
                else Track[k] = (p, now, 0f);
            }
            if (Track.Count > Hostiles.Count + 16)
            {
                var old = new List<IntPtr>();
                foreach (var k in Track.Keys) if (!seen.Contains(k)) old.Add(k);
                foreach (var k in old) Track.Remove(k);
            }
        }

        private static float TargetSpeed(VailActor t) =>
            t && Track.TryGetValue(t.Pointer, out var v) ? v.speed : 0f;

        private static bool CanFight(VailActor soldier)
        {
            try
            {
                if (!soldier || !soldier.gameObject.activeInHierarchy || soldier.IsDead()) return false;
                Robby robby = soldier.GetComponentInChildren<Robby>(true);
                if (robby && robby._injuredState.ToString() != "None") return false; // still lying injured
                if (SoldierHealth.IsDowned(soldier)) return false;                     // down (the game's Dying state)
                return true;
            }
            catch { return false; }
        }

        private static bool Usable(VailActor target)
        {
            try { return target && target.gameObject.activeInHierarchy && !target.IsDead(); }
            catch { return false; }
        }

        private static float Dist(VailActor a, VailActor b) =>
            a && b ? Vector3.Distance(a.transform.position, b.transform.position) : float.MaxValue;

        /// <summary>The enemy is close to the player or to a soldier - he is the one to shoot first.</summary>
        private static bool Threatening(VailActor h, List<VailActor> soldiers)
        {
            Vector3 hp = h.transform.position;
            try { if (LocalPlayer.Transform && Vector3.Distance(LocalPlayer.Transform.position, hp) < 7f) return true; } catch { }
            foreach (var s in soldiers) if (s && Vector3.Distance(s.transform.position, hp) < 5f) return true;
            return false;
        }

        private static void AssignTargets(float now)
        {
            float dtA = Mathf.Clamp(now - _lastAwareTick, 0f, 0.5f);
            _lastAwareTick = now;
            float range = Config.CombatRange.Value;
            float squadRange = Config.CombatSquadRange.Value;
            var soldiers = new List<VailActor>();
            foreach (var (soldier, _) in SoldierRegistry.All())
            {
                if (!soldier) continue;
                if (CanFight(soldier)) soldiers.Add(soldier);
                else if (Active.ContainsKey(soldier.Pointer)) Release(soldier.Pointer);
            }

            var onTarget = new Dictionary<IntPtr, int>();
            foreach (var e in Active.Values)
                if (e.Target) onTarget[e.Target.Pointer] = onTarget.TryGetValue(e.Target.Pointer, out int n) ? n + 1 : 1;
            var threat = new Dictionary<IntPtr, bool>();
            foreach (var h in Hostiles) threat[h.Pointer] = Threatening(h, soldiers);

            foreach (var soldier in soldiers)
            {
                IntPtr key = soldier.Pointer;
                if (CaveFollow.IsBusy(soldier)) continue;
                Active.TryGetValue(key, out var e);
                if (e == null)
                {
                    RememberFreeState(soldier);
                    if (Config.CombatNoCower.Value && WantsToCower(soldier))
                    {
                        e = ReadyStance(soldier, key, now);
                        e.Orders = OrdersBefore(soldier, now);
                        Active[key] = e;
                    }
                }
                Vector3 sp = soldier.transform.position;
                CombatOrders.Snap snap = e?.Orders ?? OrdersBefore(soldier, now);
                Vector3 anchor = CombatOrders.Anchor(soldier, snap, out float leash, out bool follow);
                bool onlyThreats = Leashed.TryGetValue(key, out float lu) && now < lu;

                // Comrades fighting nearby: gunfire and shouting make him alert, and he learns where their enemy is.
                bool comradeFighting = false;
                var called = new HashSet<IntPtr>();
                foreach (var other in Active.Values)
                {
                    if (other.Soldier == null || other.Soldier.Pointer == key || !other.Target || (other.Shots == 0 && !other.SeesTarget)) continue;
                    if (Vector3.Distance(sp, other.Soldier.transform.position) > squadRange) continue;
                    comradeFighting = true;
                    if (other.SeesTarget) called.Add(other.Target.Pointer);
                }
                bool alert = e != null || comradeFighting;

                VailActor best = null;
                bool bestVisible = false, bestSquad = false;
                float bestScore = float.MaxValue;
                foreach (var h in Hostiles)
                {
                    if (!h) continue;
                    var ak = (key, h.Pointer);
                    bool current = e != null && e.Target && e.Target.Pointer == h.Pointer;
                    float d = Vector3.Distance(sp, h.transform.position);
                    Aware.TryGetValue(ak, out float aw);
                    if (d > range * (current ? 1.25f : 1f)) { Forget(ak, aw, dtA); continue; }
                    threat.TryGetValue(h.Pointer, out bool th);
                    // Leash: enemies far from the one he follows (or from his post) are not his business unless they attack.
                    if (!current && !th)
                    {
                        float fromAnchor = CombatMove.Flat(h.transform.position - anchor).magnitude;
                        if (onlyThreats || fromAnchor > leash + (follow ? 15f : range * 0.5f)) { Forget(ak, aw, dtA); continue; }
                    }
                    bool vis = Visible(soldier, h, current && now - e.LastSeen < 2f);
                    bool heard = d < Config.CombatHearing.Value;
                    bool call = called.Contains(h.Pointer);

                    if (current) aw = 1f;
                    else
                    {
                        float gain = AwarenessGain(soldier, h, d, vis, heard, th, alert, call);
                        aw = gain > 0f ? Mathf.Min(1f, aw + gain * dtA) : Mathf.Max(0f, aw - 0.15f * dtA);
                    }
                    if (aw <= 0f) { Aware.Remove(ak); AwareLogged.Remove(ak); }
                    else Aware[ak] = aw;
                    if (!current && aw < 1f)
                    {
                        if (aw >= 0.4f && AwareLogged.Add(ak))
                            Debug($"{soldier.name} becomes alert: {h.TypeId} at {d:0} m ({(vis ? "sees something" : heard ? "hears something" : "a comrade calls")})");
                        continue;
                    }

                    // Not seen: still a target if he hears it, a comrade nearby is fighting it, or he just lost sight of it.
                    if (!vis && !heard && !call && !(current && now - e.LastSeen < 8f)) continue;
                    // Hunted for a while without seeing him (again): give up on this one - he has to notice it anew.
                    if (!vis && current && now - e.LastSeen > 10f)
                    {
                        GiveUp[ak] = now + 15f;
                        Aware[ak] = 0.5f;
                        Debug($"{soldier.name}: has not seen {h.TypeId} for 10s, gives up on him");
                        continue;
                    }
                    if (GiveUp.TryGetValue(ak, out float until) && now < until && (!vis || d > range * 0.5f)) continue;
                    float score = d;
                    if (!vis) score += 15f;
                    int others = (onTarget.TryGetValue(h.Pointer, out int n) ? n : 0) - (current ? 1 : 0);
                    score += 8f * others;
                    if (th) score -= 12f;
                    if (current) score -= 6f; // do not flip between targets
                    if (score < bestScore) { bestScore = score; best = h; bestVisible = vis; bestSquad = !vis && call; }
                }

                if (best)
                {
                    bool squad = bestSquad;
                    if (e == null)
                    {
                        float react = Mathf.Max(0f, Config.CombatReaction.Value) * R(0.6f, 1.4f) + (squad ? 0.4f : 0f);
                        e = new Engagement
                        {
                            Soldier = soldier, StartedAt = now, ReactUntil = now + react, AimReadyAt = now + react,
                            MagLeft = Mags.TryGetValue(key, out int mag) ? mag : Math.Max(1, Config.CombatMagazine.Value), BurstLeft = NewBurst(),
                            ReturnState = FreeStates.TryGetValue(key, out int rs) ? rs : 0,
                            StandSince = now, ShiftAfter = R(7f, 14f), Sway = R(0f, 100f), ReleaseAfter = R(2.5f, 4.5f),
                            LastSeen = now, LastSeenPos = best.transform.position, Squad = squad, StartPos = soldier.transform.position,
                            Orders = snap,
                        };
                        Active[key] = e;
                        Debug($"{soldier.name} {(squad ? "helps a comrade against" : "attacks:")} {best.TypeId} at {Dist(soldier, best):0} m (reacts after {react:0.0}s{(follow ? $", following - stays within {leash:0} m of the player" : "")})");
                    }
                    else if (!e.Target || e.Target.Pointer != best.Pointer)
                    {
                        Debug($"{soldier.name} new target: {best.TypeId} at {Dist(soldier, best):0} m{(squad ? " (from a comrade)" : "")}");
                        e.FirstAfterAim = true;
                        e.AimReadyAt = Mathf.Max(e.AimReadyAt, now + R(0.5f, 0.9f));
                        if (e.Orders == null) e.Orders = snap;
                    }
                    if (e.Target) onTarget[e.Target.Pointer] = Math.Max(0, (onTarget.TryGetValue(e.Target.Pointer, out int c) ? c : 1) - 1);
                    e.Target = best;
                    onTarget[best.Pointer] = (onTarget.TryGetValue(best.Pointer, out int c2) ? c2 : 0) + 1;
                    e.Squad = squad;
                    e.SeesTarget = bestVisible;
                    if (bestVisible) { e.LastSeen = now; e.LastSeenPos = best.transform.position; }
                }
                else if (e != null)
                {
                    e.SeesTarget = false;
                    if (!Usable(e.Target)) e.Target = null;
                    if (now - e.LastSeen > e.ReleaseAfter && e.Phase != Phase.Move && e.Phase != Phase.Reload) Release(key);
                }
            }
            if (Aware.Count > 400) { Aware.Clear(); AwareLogged.Clear(); }
        }

        private static void Forget((IntPtr, IntPtr) ak, float aw, float dt)
        {
            if (aw <= 0f) return;
            aw -= 0.15f * dt;
            if (aw <= 0f) { Aware.Remove(ak); AwareLogged.Remove(ak); }
            else Aware[ak] = aw;
        }

        /// <summary>
        /// How fast (per second) a soldier becomes aware of an enemy. In plain view, 20 m away, straight ahead:
        /// 1 / combat_awareness. Closer is faster, to the side or behind him much slower; a moving enemy is noticed
        /// sooner; one attacking someone, or a comrade fighting nearby, makes it much faster.
        /// </summary>
        private static float AwarenessGain(VailActor s, VailActor h, float d, bool vis, bool heard, bool threat, bool alert, bool called)
        {
            float t20 = Config.CombatAwareness.Value;
            if (t20 <= 0.01f) return 1000f;
            float unit = 1f / t20;
            float g = 0f;
            if (vis)
            {
                g = unit * Mathf.Pow(20f / Mathf.Max(d, 5f), 1.5f);
                Vector3 to = CombatMove.Flat(h.transform.position - s.transform.position);
                float ang = to.sqrMagnitude > 0.01f ? Vector3.Angle(CombatMove.Flat(s.transform.forward), to) : 0f;
                // Behind him he only notices it by chance or from the corner of his eye; movement is noticed sooner.
                if (ang > 110f) g *= 0.3f;
                else if (ang > 60f) g *= 0.6f;
                if (TargetSpeed(h) > 1f) g *= 1.5f;
                if (d < 15f) g = Mathf.Max(g, unit);
            }
            if (heard) g += unit * 0.6f * (1f - d / Mathf.Max(1f, Config.CombatHearing.Value));
            if (called) g += unit * 0.8f;
            if (g <= 0f) return 0f;
            if (threat) g *= 4f;
            if (alert) g *= 2.5f;
            if (d < 4f) g = Mathf.Max(g, 5f);
            return g;
        }

        /// <summary>His order before the fight; a fight right after another keeps the one noted before the first.</summary>
        private static CombatOrders.Snap OrdersBefore(VailActor s, float now)
        {
            if (LastOrders.TryGetValue(s.Pointer, out var lo) && now - lo.at < 10f) return lo.snap;
            return CombatOrders.Take(s);
        }

        private static readonly int HashPointIntro = Animator.StringToHash("PointIntro");
        private static readonly int HashPointLoop = Animator.StringToHash("PointLoop");
        private static readonly int HashPointOutro = Animator.StringToHash("PointOutro");

        /// <summary>The animator state a soldier is in while not fighting (idle, walking ...), to return to after a fight.</summary>
        /// <summary>His own (Kelvin) AI is making him cower or back away - from the player's gunfire, an explosion ...</summary>
        private static bool WantsToCower(VailActor s)
        {
            try
            {
                Animator an = CombatLook.AnimatorOf(s);
                return an && (an.GetBool("Cower") || an.GetBool("BackAway"));
            }
            catch { return false; }
        }

        /// <summary>
        /// Instead of cowering: his own AI is switched off, he raises the rifle towards where the player is shooting
        /// (or where he was looking) and keeps watch for a few seconds; if an enemy is about the normal fight takes over.
        /// </summary>
        private static Engagement ReadyStance(VailActor s, IntPtr key, float now)
        {
            Vector3 look = s.transform.position + s.transform.forward * 15f;
            try
            {
                if (LocalPlayer.Transform && Vector3.Distance(LocalPlayer.Transform.position, s.transform.position) < 60f)
                    look = s.transform.position + CombatMove.Flat(LocalPlayer.Transform.forward).normalized * 15f;
            }
            catch { }
            try
            {
                Animator an = CombatLook.AnimatorOf(s);
                if (an) { an.SetBool("Cower", false); an.SetBool("BackAway", false); }
            }
            catch { }
            Debug($"{s.name} does not cower but takes a ready stance");
            return new Engagement
            {
                Soldier = s, StartedAt = now, LastSeen = now, LastSeenPos = look, ReleaseAfter = 3f, ReactUntil = now, AimReadyAt = now,
                MagLeft = Mags.TryGetValue(key, out int mag) ? mag : Math.Max(1, Config.CombatMagazine.Value), BurstLeft = NewBurst(),
                ReturnState = FreeStates.TryGetValue(key, out int rs) ? rs : 0, StartPos = s.transform.position,
                StandSince = now, ShiftAfter = 999f, Sway = R(0f, 100f), Doing = "ready",
            };
        }

        /// <summary>The animator state the soldier was in before the fight (full path hash), 0 if unknown.</summary>
        internal static int FreeStateOf(VailActor s) => s && FreeStates.TryGetValue(s.Pointer, out int h) ? h : 0;

        private static void RememberFreeState(VailActor s)
        {
            try
            {
                Animator an = CombatLook.AnimatorOf(s);
                if (!an || an.IsInTransition(0)) return;
                var info = an.GetCurrentAnimatorStateInfo(0);
                int h = info.shortNameHash;
                if (h == HashPointIntro || h == HashPointLoop || h == HashPointOutro) return;
                // never a hit / head-bump reaction as his "normal" state (it was replayed after every pose in caves)
                foreach (var ci in an.GetCurrentAnimatorClipInfo(0))
                    if (ci.clip && ci.clip.name.IndexOf("Hit", StringComparison.Ordinal) >= 0) return;
                FreeStates[s.Pointer] = info.fullPathHash;
            }
            catch { }
        }

        private static int NewBurst() => Rng.Next(1, Math.Max(1, Config.CombatBurst.Value) + 1);

        // ------------------------------------------------------------------ line of sight

        /// <summary>
        /// True if something solid (not an actor) lies on the line from-to. Actors never block: a bullet hitting
        /// another cannibal on the way is fine. atDist = distance of the first obstacle.
        /// </summary>
        internal static bool Blocked(Vector3 from, Vector3 to, VailActor ignore, VailActor target, out float atDist)
        {
            atDist = 0f;
            Vector3 dir = to - from;
            float len = dir.magnitude;
            if (len < 0.5f) return false;
            dir /= len;
            try
            {
                var hits = Physics.RaycastAll(from, dir, len, ~0, QueryTriggerInteraction.Ignore);
                float first = float.MaxValue;
                Collider blocker = null;
                foreach (var h in hits)
                {
                    Collider c = h.collider;
                    if (!c || h.distance >= first || h.distance > len - 0.4f) continue;
                    if (c.GetComponentInParent<VailActor>()) continue;
                    if (IsGunCopy(c.transform) || IsPlayer(c.transform)) continue;
                    // Pushers (VailBushPusher, layer "Pusher") bend bushes around actors - nothing solid.
                    if (IgnoredLayer(c.gameObject.layer)) continue;
                    // Twigs, grass, small props: a bullet or a look goes past them.
                    try { if (c.bounds.size.magnitude < 0.45f) continue; } catch { }
                    first = h.distance;
                    blocker = c;
                }
                if (first == float.MaxValue) return false;
                atDist = first;
                LastBlocker = DescribeCollider(blocker, first);
                return true;
            }
            catch { return false; }
        }

        /// <summary>The obstacle of the last Blocked() that returned true: object path, layer, distance (for the log).</summary>
        internal static string LastBlocker = "";

        private static string DescribeCollider(Collider c, float dist)
        {
            if (!c) return "?";
            string path = c.name;
            Transform t = c.transform.parent;
            for (int i = 0; t && i < 3; i++, t = t.parent) path = t.name + "/" + path;
            string layer = "";
            try { layer = LayerMask.LayerToName(c.gameObject.layer); } catch { }
            string kind = "";
            try { kind = c.GetIl2CppType().Name; } catch { }
            return $"{path} [{kind}, layer {c.gameObject.layer} {layer}, {c.bounds.size.magnitude:0.0} m big] at {dist:0.0} m";
        }

        private static readonly Dictionary<int, bool> LayerIgnored = new();

        private static bool IgnoredLayer(int layer)
        {
            if (LayerIgnored.TryGetValue(layer, out bool ig)) return ig;
            string n = "";
            try { n = LayerMask.LayerToName(layer) ?? ""; } catch { }
            ig = n.IndexOf("Pusher", StringComparison.OrdinalIgnoreCase) >= 0;
            LayerIgnored[layer] = ig;
            return ig;
        }

        private static bool IsPlayer(Transform t)
        {
            try { return LocalPlayer.Transform && (t == LocalPlayer.Transform || t.IsChildOf(LocalPlayer.Transform)); }
            catch { return false; }
        }

        private static bool IsGunCopy(Transform t)
        {
            for (; t; t = t.parent) if (t.name.StartsWith("LivingSoldiers_", StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool _gameSightBroken;
        private static readonly Dictionary<string, float> SightLogged = new();

        /// <summary>The game's own vision check (VailActor.CanSee, used by cannibals and Virginia). Null if unavailable.</summary>
        private static bool? GameSees(VailActor soldier, VailActor target, bool recently)
        {
            if (_gameSightBroken) return null;
            try
            {
                var stim = target._actorStimuli;
                if (!stim) return null;
                return soldier.CanSee(stim, recently);
            }
            catch (Exception ex)
            {
                _gameSightBroken = true;
                RLog.Warning($"{CharacterReplacement.Tag} [Combat] [AI] the game's sight check is not usable ({ex.Message}) - own line of sight only");
                return null;
            }
        }

        /// <summary>
        /// Can the soldier see the target? Within 3.5 m always. Otherwise either the game's vision or our own
        /// head-to-chest ray. When the two disagree, the obstacle our ray hit is logged (at most every 10 s per
        /// soldier and enemy type), so wrong blockers can be found.
        /// </summary>
        private static bool Visible(VailActor soldier, VailActor target, bool recently = false)
        {
            if (!Config.CombatLineOfSight.Value) return true;
            float d = Dist(soldier, target);
            if (d < 3.5f) return true;
            bool ray = VisibleFrom(soldier.transform.position, target);
            string blocker = ray ? "" : LastBlocker;
            bool? game = GameSees(soldier, target, recently);
            if (game.HasValue && game.Value != ray && Config.CombatDebug.Value)
            {
                string key = soldier.name + target.TypeId + game.Value;
                float now = Time.time;
                if (!SightLogged.TryGetValue(key, out float last) || now - last > 10f)
                {
                    SightLogged[key] = now;
                    Debug($"Sight {soldier.name} -> {target.TypeId} ({d:0} m): game {(game.Value ? "sees" : "does not see")}, line of sight {(ray ? "clear" : "blocked by " + blocker)}");
                }
            }
            return ray || game == true;
        }

        /// <summary>Can a soldier standing at pos see the target (head height to the target's chest)?</summary>
        internal static bool VisibleFrom(Vector3 pos, VailActor target)
        {
            if (!Config.CombatLineOfSight.Value || !target) return true;
            Vector3 to = target.transform.position + Vector3.up * 1.2f;
            Vector3 from = pos + Vector3.up * 1.55f;
            if (!Blocked(from, to, null, target, out _)) return true;
            // Low targets (crawling, small mutants): also try the hips.
            return !Blocked(from, target.transform.position + Vector3.up * 0.6f, null, target, out _);
        }

        /// <summary>The player or another soldier stands in (or right next to) the line of fire.</summary>
        private static bool FriendlyInLine(VailActor s, VailActor target)
        {
            Vector3 a = s.transform.position + Vector3.up * 1.4f;
            Vector3 b = target.transform.position + Vector3.up * 1.2f;
            try
            {
                if (LocalPlayer.Transform && NearSegment(LocalPlayer.Transform.position + Vector3.up * 1.1f, a, b, 0.9f)) return true;
            }
            catch { }
            foreach (var (o, _) in SoldierRegistry.All())
            {
                if (!o || o.Pointer == s.Pointer) continue;
                if (NearSegment(o.transform.position + Vector3.up * 1.1f, a, b, 0.7f)) return true;
            }
            return false;
        }

        private static bool NearSegment(Vector3 p, Vector3 a, Vector3 b, float radius)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / Mathf.Max(0.001f, ab.sqrMagnitude);
            if (t < 0.05f || t > 0.95f) return false;
            return Vector3.Distance(p, a + ab * t) < radius;
        }

        // ------------------------------------------------------------------ turning

        /// <summary>Turns the actor towards wantYaw at up to maxDegPerSec, easing in near the end. Returns the new yaw.</summary>
        internal static float TurnTowards(VailActor s, float wantYaw, float maxDegPerSec)
        {
            float curYaw = s.transform.eulerAngles.y;
            try { var ms = s._moveState; if (ms != null) curYaw = ms.Yaw(); } catch { }
            float delta = Mathf.Abs(Mathf.DeltaAngle(curYaw, wantYaw));
            float speed = Mathf.Clamp(delta * 5f, 40f, maxDegPerSec);
            float newYaw = Mathf.MoveTowardsAngle(curYaw, wantYaw, speed * Time.deltaTime);
            bool turned = false;
            // The actor writes its own rotation state back to the transform every frame (VailActor.MoveState), so
            // turning has to go through MoveState.SetRotation - the transform alone gets undone right away.
            try { var ms = s._moveState; if (ms != null) { ms.SetRotation(new Vector3(0f, newYaw, 0f), false); turned = true; } }
            catch (Exception ex)
            {
                if (!_rotationWarned) { _rotationWarned = true; RLog.Warning($"{CharacterReplacement.Tag} [Combat] [AI] SetRotation: {ex.Message}"); }
            }
            if (!turned) s.transform.rotation = Quaternion.Euler(0f, newYaw, 0f);
            try { s._goalRotation = Quaternion.Euler(0f, newYaw, 0f); } catch { }
            return newYaw;
        }

        // ------------------------------------------------------------------ one frame of fighting

        /// <summary>Returns false when the engagement should end.</summary>
        private static bool UpdateEngagement(Engagement e, float now)
        {
            VailActor s = e.Soldier;
            if (!s || !CanFight(s)) return false;

            if (!e.ControllerOff)
            {
                try { s.EnableController(false); } catch (Exception ex) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [AI] switching the AI off: {ex.Message}"); }
                e.ControllerOff = true;
                // His head/upper-body look-at would bend the arms after our gun fit; the aim correction does that job now.
                try { e.LookAtWasOff = s._forceDisableLookAt; s._forceDisableLookAt = true; } catch { }
            }
            try { s._hasMoveToTarget = false; s._moveToTarget = null; } catch { }

            if (e.Target && !Usable(e.Target))
            {
                if (CombatHit.Dead(e.Target)) Debug($"{s.name}: {e.Target.TypeId} is dead");
                e.Target = null;
                e.SeesTarget = false;
                e.LastSeen = Mathf.Min(e.LastSeen, now);
            }

            WatchHealth(e, now);

            // --- staggering from a hit: nothing else until the hit reaction has played
            if (e.Phase == Phase.Stagger)
            {
                if (now < e.StaggerUntil) { e.Doing = "hit"; return true; }
                e.Phase = Phase.Fight;
                e.FirstAfterAim = true;
                e.AimReadyAt = now + R(0.5f, 0.9f);
                e.NextDecision = now; // decide right away (back off, take cover)
            }

            // --- moving (the move plays its own run clips)
            if (e.Phase == Phase.Move)
            {
                var r = CombatMove.Tick(s);
                if (r == CombatMove.Result.Moving) return true;
                e.Phase = Phase.Fight;
                e.MovedAt = now;
                e.StandSince = now;
                e.FirstAfterAim = true;
                e.AimReadyAt = now + R(0.6f, 1.0f); // settle after moving
                e.NextDecision = now + 0.6f;
                e.Doing = "fighting";
            }

            Vector3 look = e.Target ? (e.SeesTarget ? e.Target.transform.position : e.LastSeenPos) : e.LastSeenPos;
            int pitch = Pitch(s, look);

            if (!e.Target)
            {
                // Nothing to shoot: keep the gun up a moment towards where the enemy was, then carry it at rest.
                if (e.Phase == Phase.Reload && now < e.ReloadUntil) return true;
                if (e.Phase == Phase.Reload) { e.Phase = Phase.Fight; Debug($"{s.name} has reloaded"); }
                // A lull: top up the magazine now rather than in the next fight.
                if (Config.CombatMagazine.Value > 0 && e.MagLeft < Config.CombatMagazine.Value * 0.6f && now - e.LastSeen > 0.8f)
                {
                    e.Phase = Phase.Reload;
                    e.MagLeft = Config.CombatMagazine.Value;
                    StartReload(e, now);
                    e.Doing = "reloading (lull in the fight)";
                    return true;
                }
                CombatLook.Pose(s, now - e.LastSeen < 1.5f ? CombatLook.AimClip(s, pitch) : CombatLook.HeldClip(s, 0), 0.3f);
                Vector3 toward = CombatMove.Flat(e.LastSeenPos - s.transform.position);
                if (toward.sqrMagnitude > 1f) TurnTowards(s, Quaternion.LookRotation(toward.normalized, Vector3.up).eulerAngles.y, 220f);
                if (e.Doing != "ready") e.Doing = "covering";
                return now - e.LastSeen <= e.ReleaseAfter;
            }

            // --- deciding where to be (not every frame)
            if (now >= e.NextDecision && e.Phase == Phase.Fight && Config.CombatMove.Value)
            {
                e.NextDecision = now + 0.4f;
                if (TryReposition(e, now)) return true;
            }

            // --- facing: the target, or where it was last seen; slight sway while aiming.
            // Up close he does not track every step of an enemy circling him (that looked like spinning on the
            // spot): small differences are ignored and he turns more slowly.
            Vector3 flat = CombatMove.Flat(look - s.transform.position);
            float dist = flat.magnitude;
            bool close = dist < 2.5f;
            float angle = 0f;
            if (flat.sqrMagnitude > 0.01f)
            {
                float wantYaw = Quaternion.LookRotation(flat.normalized, Vector3.up).eulerAngles.y;
                // Turn the body so the rifle - not the hips - points at the target (bladed stance of the aim clip).
                wantYaw -= CombatGun.AimYawOffset(s);
                float t = now + e.Sway;
                if (!close) wantYaw += Mathf.Sin(t * 0.9f) * 1.3f + Mathf.Sin(t * 2.3f) * 0.5f;
                float curYaw = s.transform.eulerAngles.y;
                try { var ms = s._moveState; if (ms != null) curYaw = ms.Yaw(); } catch { }
                angle = Mathf.Abs(Mathf.DeltaAngle(curYaw, wantYaw));
                if (!close || angle > 20f)
                {
                    float yaw = TurnTowards(s, wantYaw, close ? 120f : now < e.ReactUntil ? 200f : 300f);
                    angle = Mathf.Abs(Mathf.DeltaAngle(yaw, wantYaw));
                }
            }

            // --- reloading
            if (e.Phase == Phase.Reload)
            {
                if (now < e.ReloadUntil)
                {
                    if (!e.ReloadClip) CombatLook.Pose(s, CombatLook.HeldClip(s, 0), 0.25f);
                    e.Doing = "reloading";
                    return true;
                }
                e.Phase = Phase.Fight;
                e.ReloadClip = false;
                e.FirstAfterAim = true;
                e.AimReadyAt = now + R(0.6f, 1.0f); // shoulder the rifle again after reloading
                Debug($"{s.name} has reloaded");
            }

            // --- aiming and shooting (searching for an unseen enemy: rifle at the ready, not aimed at a wall)
            bool searching = !e.SeesTarget && now - e.LastSeen > 1.5f;
            string aim = searching ? CombatLook.HeldClip(s, 0) : CombatLook.AimClip(s, pitch);
            string pose = CombatLook.CurrentPose(s);
            // Do not cut a running shot short; FireOnce returns to the aim clip by itself.
            if (pose == null || (pose != aim && !(pose.Contains("Fire") && CombatLook.PoseAge(s) < 0.35f)))
            {
                bool fromAim = pose != null && (pose.Contains("Aim") || pose.Contains("Fire")) && !pose.Contains("Reload");
                // Raising the rifle and taking aim takes time; switching between aim clips (up/down) does not.
                if (CombatLook.Pose(s, aim, fromAim ? 0.15f : 0.3f) && !fromAim && !searching) e.AimReadyAt = Mathf.Max(e.AimReadyAt, now + R(0.8f, 1.4f));
            }
            bool ready = CombatLook.PoseSettled(s) || !CombatLook.AnimatorOf(s);
            e.Doing = now < e.ReactUntil ? "noticing an enemy" : e.SeesTarget ? "fighting" : "looking for the target";
            if (!e.SeesTarget || now < e.ReactUntil || now < e.AimReadyAt || now < e.NextShot || !ready) return true;
            if (angle > (close ? 30f : 12f)) return true;

            if (FriendlyInLine(s, e.Target))
            {
                e.NextShot = now + 0.4f;
                if (!e.FriendlyBlock) Debug($"{s.name} holds fire: friend in the line of fire");
                e.FriendlyBlock = true;
                return true;
            }
            e.FriendlyBlock = false;
            Fire(e, now, pitch);
            return true;
        }

        /// <summary>Aim up (+1) / straight (0) / down (-1): angle from his chest to the target's chest.</summary>
        private static int Pitch(VailActor s, Vector3 target)
        {
            Vector3 from = s.transform.position + Vector3.up * 1.4f;
            Vector3 to = target + Vector3.up * 1.1f;
            float h = CombatMove.Flat(to - from).magnitude;
            if (h < 0.5f) return 0;
            float deg = Mathf.Atan2(to.y - from.y, h) * Mathf.Rad2Deg;
            return deg > 22f ? 1 : deg < -22f ? -1 : 0;
        }

        private static void WatchHealth(Engagement e, float now)
        {
            if (now < e.NextHealthCheck) return;
            e.NextHealthCheck = now + 0.15f;
            float h = CombatHit.Health(e.Soldier);
            if (e.MaxHealth < 0f) { try { e.MaxHealth = e.Soldier.GetMaxHealth(); } catch { e.MaxHealth = 0f; } }
            if (e.Health >= 0f && h < e.Health - 0.5f)
            {
                float lost = e.Health - h;
                e.LastHurt = now;
                Debug($"{e.Soldier.name} is hit: -{lost:0} health ({(e.MaxHealth > 0 ? h / e.MaxHealth * 100f : 0f):0}% left)");
                // Being hit makes him react sooner, not stand there.
                e.ReactUntil = Mathf.Min(e.ReactUntil, now);
                e.NextDecision = Mathf.Min(e.NextDecision, now);
                Stagger(e, now, lost);
            }
            e.Health = h;
        }

        /// <summary>
        /// Hit reaction: a flinch with the gun for normal hits, a knock back for heavy ones (a fifth of his health or
        /// more). Interrupts aiming, moving and reloading; not again within a second so a flurry of blows does not lock him.
        /// </summary>
        private static void Stagger(Engagement e, float now, float lost)
        {
            if (now - e.LastStagger < 1.0f) return;
            VailActor s = e.Soldier;
            bool heavy = e.MaxHealth > 0f && lost >= e.MaxHealth * 0.2f;
            string clip = heavy
                ? CombatLook.First("PlayerAKnockBack", "PlayerAShotgunHitReact")
                : CombatLook.First("PlayerAShotgunHitReact", "PlayerAMacheteHitReact", "PlayerAKnockBack");
            if (clip == null || !CombatLook.Pose(s, clip, 0.08f, restart: true)) return;
            e.LastStagger = now;
            CombatMove.Stop(s, quiet: true);
            var c = CombatLook.Clip(clip);
            float len = c ? c.length : 0.6f;
            e.StaggerUntil = now + Mathf.Clamp(len * 0.85f, 0.35f, heavy ? 1.6f : 0.9f);
            if (e.Phase == Phase.Reload) e.ReloadClip = false; // the reload starts over after the hit
            e.Phase = Phase.Stagger;
            e.Doing = "hit";
            Debug($"{s.name} staggers ({clip}, {e.StaggerUntil - now:0.0}s)");
        }

        // ------------------------------------------------------------------ repositioning

        private static bool TryReposition(Engagement e, float now)
        {
            VailActor s = e.Soldier;
            Vector3 enemyPos = e.SeesTarget ? e.Target.transform.position : e.LastSeenPos;
            float d = CombatMove.Flat(enemyPos - s.transform.position).magnitude;
            float range = Config.CombatRange.Value;
            float hp = e.MaxHealth > 0f && e.Health >= 0f ? e.Health / e.MaxHealth : 1f;

            string why = null;
            CombatMove.Purpose purpose = CombatMove.Purpose.Shift;
            float prevDist = e.PrevDist;
            e.PrevDist = d;
            bool fleeing = prevDist > 0f && d > prevDist + 0.4f; // distance grew since the last decision (0.4 s)
            bool closing = prevDist > 0f && d < prevDist - 0.4f;  // he is coming - let him come
            Vector3 anchor = CombatOrders.Anchor(s, e.Orders, out float leash, out bool follow);
            float fromAnchor = CombatMove.Flat(s.transform.position - anchor).magnitude;

            // Too far from the one he follows (or from his post): break off and go back, unless the enemy is right on him.
            if (fromAnchor > leash + 4f && !(e.SeesTarget && d < 10f))
            {
                Debug($"{s.name}: {fromAnchor:0} m away from the {(follow ? "player" : "post")}, breaks off and turns back");
                Leashed[s.Pointer] = now + 8f;
                GiveUp[(s.Pointer, e.Target.Pointer)] = now + 15f;
                e.Target = null;
                e.SeesTarget = false;
                e.LastSeen = now - 99f;
                return false;
            }

            // Up close: a few quick steps back (still facing him), then shoot again. Each with a cooldown - a
            // charging cannibal is faster than a soldier, and one who only ever backs away never fires.
            if (d < 2.5f && e.SeesTarget && now - e.LastBackStep > 2.5f)
            {
                why = "enemy right in front of him, makes some distance"; purpose = CombatMove.Purpose.BackStep;
                e.LastBackStep = now;
            }
            else if (d < Config.CombatMinDistance.Value && d >= 2.5f && e.SeesTarget && now - e.LastRetreat > 5f)
            {
                why = "enemy too close, falls back"; purpose = CombatMove.Purpose.Retreat;
                e.LastRetreat = now;
            }
            else if (Config.CombatRetreatHealth.Value > 0f && hp < Config.CombatRetreatHealth.Value && now - e.LastFallback > 10f)
            {
                why = "badly wounded, falls back"; purpose = CombatMove.Purpose.Fallback;
                e.LastFallback = now;
            }
            else if (now - e.LastHurt < 1.5f && now - e.LastHurtMove > 5f && d >= 2.5f)
            {
                why = "under fire, changes position"; purpose = Config.CombatCover.Value ? CombatMove.Purpose.Cover : CombatMove.Purpose.Shift;
                e.LastHurtMove = now;
            }
            else if (!e.SeesTarget && now - e.LastSeen > 1.0f && now - e.LastSightMove > 3f)
            {
                why = "no sight, looks for a clear field of fire"; purpose = CombatMove.Purpose.Sight;
                e.LastSightMove = now;
            }
            else if (e.FriendlyBlock)
            {
                why = "friend in the line of fire, steps aside"; purpose = CombatMove.Purpose.Shift;
                e.FriendlyBlock = false;
            }
            else if (Config.CombatChase.Value && (e.SeesTarget || now - e.LastSeen < 3f) && ((d > range * 0.75f && !closing) || (d > 18f && fleeing && !follow)))
            {
                // (A soldier following someone lets a fleeing enemy go - he stays with the one he follows.)
                // Too far for a good shot, or getting away: run after him - but not further than the leash (from the
                // one he follows, or from where the fight started).
                if (fromAnchor > leash - 3f || CombatMove.Flat(enemyPos - anchor).magnitude > leash + range * 0.6f)
                {
                    GiveUp[(s.Pointer, e.Target.Pointer)] = now + 20f;
                    Debug($"{s.name}: {e.Target.TypeId} is too far away ({d:0} m), lets him go");
                    e.Target = null;
                    e.SeesTarget = false;
                    e.LastSeen = now - 2f;
                    return false;
                }
                why = fleeing ? "chases the fleeing enemy" : "moves up into firing range";
                purpose = CombatMove.Purpose.Chase;
            }
            else if (!e.CoverTried && Config.CombatCover.Value && d > 12f && e.SeesTarget)
            {
                e.CoverTried = true;
                if (!CombatMove.CoverAt(s.transform.position, enemyPos, e.Target)) { why = "takes cover"; purpose = CombatMove.Purpose.Cover; }
            }
            else if (now - e.StandSince > e.ShiftAfter && e.BurstIndex == 0 && now > e.NextShot - 0.3f)
            {
                e.ShiftAfter = R(7f, 14f);
                e.StandSince = now;
                if (Rng.NextDouble() < 0.5)
                {
                    why = "changes position";
                    purpose = Config.CombatCover.Value && Rng.NextDouble() < 0.6 ? CombatMove.Purpose.Cover : CombatMove.Purpose.Shift;
                }
            }
            if (why == null) return false;

            if (!CombatMove.FindSpot(s, e.Target, enemyPos, purpose, out Vector3 spot, out bool cover))
            {
                string reasons = CombatMove.RejectInfo();
                // Lost sight and nowhere better nearby: go after him, towards where he was last seen.
                if (purpose == CombatMove.Purpose.Sight && CombatMove.FindSpot(s, e.Target, enemyPos, CombatMove.Purpose.Approach, out spot, out cover))
                {
                    why = "no sight, goes to the last known position";
                    purpose = CombatMove.Purpose.Approach;
                }
                else
                {
                    // Cornered or no good spot: fight from here, try again a bit later.
                    e.NextDecision = now + 1.5f;
                    Debug($"{s.name}: {why} - but no suitable spot found, stays ({reasons})");
                    return false;
                }
            }
            Engagement en = e;
            Func<Vector3?> enemy = () => en.Target ? en.Target.transform.position : (Vector3?)null;
            // He faces the enemy only for a short side step; for any longer move he turns and runs (running
            // backwards or sideways for metres looks wrong) and turns back to fight on arrival.
            bool faceEnemy = purpose == CombatMove.Purpose.Shift && Vector3.Distance(spot, s.transform.position) < 3.2f;
            // The best spots in turn - the first one the path finder can reach.
            var tries = new List<(Vector3 pos, bool cover)>(CombatMove.Candidates);
            if (tries.Count == 0) tries.Add((spot, cover));
            // Never further from the one he follows / his post than the leash (getting closer is always fine).
            tries.RemoveAll(t => { float fa = CombatMove.Flat(t.pos - anchor).magnitude; return fa > leash && fa > fromAnchor; });
            if (tries.Count == 0)
            {
                e.NextDecision = now + 1.5f;
                Debug($"{s.name}: {why} - but only spots outside the leash ({leash:0} m)");
                return false;
            }
            bool started = false;
            foreach (var t in tries)
            {
                bool fe = faceEnemy && Vector3.Distance(t.pos, s.transform.position) < 3.2f;
                if (CombatMove.Start(s, t.pos, enemy, why + (t.cover ? " (cover)" : ""), fe)) { started = true; break; }
            }
            if (!started) { e.NextDecision = now + 1.5f; return false; }
            e.Phase = Phase.Move;
            e.Moves++;
            e.Doing = why;
            return true;
        }

        // ------------------------------------------------------------------ firing

        private static void Fire(Engagement e, float now, int pitch)
        {
            VailActor s = e.Soldier;
            VailActor t = e.Target;
            float d = Dist(s, t);
            Vector3 dir = (t.transform.position + Vector3.up * 1.2f - (s.transform.position + Vector3.up * 1.45f)).normalized;

            try { CombatLook.FireOnce(s, dir, pitch).RunCoro(); } catch { }
            if (!CombatGun.MuzzlePoint(s, dir, out Vector3 muzzle)) muzzle = s.transform.position + Vector3.up * 1.45f + dir * 0.9f;

            float chance = CombatHit.Chance(d, e.BurstIndex, e.FirstAfterAim, TargetSpeed(t), now - e.MovedAt < 1.5f);
            bool tell = e.Told != t.Pointer;
            var shot = CombatHit.Fire(s, t, muzzle, chance, tell, GameSees(s, t, true) == true);
            if (shot.Hit && tell) e.Told = t.Pointer;
            e.Shots++;
            if (shot.Hit) e.Hits++;
            bool killed = CombatHit.Dead(t);
            if (killed) e.Kills++;
            Debug($"{s.name} shoots at {t.TypeId} ({d:0.0} m{(pitch != 0 ? pitch > 0 ? ", upwards" : ", downwards" : "")}, chance {chance * 100f:0}%): " +
                  (shot.Hit ? $"hit {shot.Part}" : "miss") + (shot.Note != "" ? $" - {shot.Note}" : "") + (killed ? " - KILLED" : ""));

            e.FirstAfterAim = false;
            e.BurstIndex++;
            e.BurstLeft--;
            e.MagLeft--;
            float k = Config.CombatFireInterval.Value / 1.2f;
            if (Config.CombatMagazine.Value > 0 && e.MagLeft <= 0)
            {
                e.Phase = Phase.Reload;
                e.MagLeft = Config.CombatMagazine.Value;
                e.BurstLeft = NewBurst();
                e.BurstIndex = 0;
                StartReload(e, now);
            }
            else if (e.BurstLeft > 0)
            {
                // Re-acquire the sight picture after the recoil before the next shot of the burst.
                e.NextShot = now + R(0.6f, 0.9f) * k;
            }
            else
            {
                // Between bursts: check the target, breathe, aim again.
                e.BurstLeft = NewBurst();
                e.BurstIndex = 0;
                e.NextShot = now + R(1.5f, 3.0f) * k;
            }
        }

        /// <summary>
        /// Reload: the player's reload clip (it takes as long as the clip, after the shot clip has finished), or the
        /// gun lowered for combat_reload_time if there is no clip.
        /// </summary>
        private static void StartReload(Engagement e, float now)
        {
            DelayedReload(e).RunCoro();
            e.ReloadUntil = now + Config.CombatReloadTime.Value * R(0.9f, 1.15f) + 0.3f;
        }

        private static System.Collections.IEnumerator DelayedReload(Engagement e)
        {
            float until = Time.time + 0.3f; // let the last shot play
            while (Time.time < until) yield return null;
            if (e.Phase != Phase.Reload || !e.Soldier) yield break;
            float len = CombatLook.StartReload(e.Soldier);
            e.ReloadClip = len > 0f;
            if (e.ReloadClip) e.ReloadUntil = Time.time + Mathf.Clamp(len, 0.8f, 6f);
            Debug($"{e.Soldier.name} reloads ({e.ReloadUntil - Time.time:0.0}s{(e.ReloadClip ? "" : ", rifle lowered")})");
        }

        // ------------------------------------------------------------------ ending

        private static void Release(IntPtr key)
        {
            if (!Active.TryGetValue(key, out var e)) return;
            Active.Remove(key);
            VailActor s = e.Soldier;
            if (!s) return;
            try { CombatMove.Stop(s, quiet: true); } catch { }
            Mags[key] = e.MagLeft;
            try { CombatLook.EndPoses(s, e.ReturnState); } catch { }
            try { s._forceDisableLookAt = e.LookAtWasOff; } catch { }
            if (e.ControllerOff)
            {
                try { s.EnableController(true); } catch (Exception ex) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] [AI] switching the AI on: {ex.Message}"); }
            }
            if (e.Orders != null)
            {
                LastOrders[key] = (e.Orders, Time.time);
                CombatOrders.Restore(s, e.Orders);
            }
            RLog.Msg($"{CharacterReplacement.Tag} [Combat] [AI] {s.name} ends the fight after {Time.time - e.StartedAt:0}s " +
                     $"({e.Shots} shots, {e.Hits} hits, {e.Kills} killed, {e.Moves} position changes)");
        }

        /// <summary>Ends this soldier's fight now (he is taken over by cave following or is downed).</summary>
        internal static void ReleaseFor(VailActor s)
        {
            if (s && Active.ContainsKey(s.Pointer)) Release(s.Pointer);
        }

        private static void ReleaseAll()
        {
            foreach (var k in new List<IntPtr>(Active.Keys)) Release(k);
        }

        internal static void Clear()
        {
            Active.Clear();
            Hostiles.Clear();
            Track.Clear();
            CombatFeet.Clear();
            Mags.Clear();
            FreeStates.Clear();
            GiveUp.Clear();
            Aware.Clear();
            AwareLogged.Clear();
            Leashed.Clear();
            LastOrders.Clear();
            CombatOrders.Clear();
            CaveFollow.Clear();
            SightLogged.Clear();
            _gameSightBroken = false;
            CombatMove.Clear();
            CombatHit.Clear();
            _nextScan = 0;
            _targetTypes = null;
        }
    }
}
