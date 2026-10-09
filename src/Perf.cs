using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Multiplayer;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Dedicated server diagnostics: server frame rate and hitches, process CPU/RAM, Bolt entity counts,
    /// per-player network stats (ping, bandwidth, send window, packet loss), disconnect details and the
    /// time spent inside this mod's own hooks. Logged every minute while players are online, /tmperf in chat.
    /// </summary>
    internal static class Perf
    {
        // ---- mod cost counters (calls / stopwatch ticks inside our own prefixes)
        public static long HolderCalls, HolderTicks, AttnCalls, AttnTicks, RobbyCalls, RobbyTicks, TreeCalls, TreeTicks, JobTicks, WatchTicks;

        private static readonly Dictionary<IntPtr, int> LostByConn = new();
        private static readonly Dictionary<IntPtr, string> ConnInfo = new(); // udp connection -> last stats line
        private static bool _running, _patched;

        // frame stats of the current window
        private static int _frames, _hitch50, _hitch200, _hitch1000;
        private static float _dtSum, _dtMax;
        private static double _lastWindowStart;
        private static TimeSpan _lastCpu;
        private static DateTime _lastCpuWall;
        private static long _lastModTicks;
        private static float _lastActorScan = -1000f;
        private static string _actorLine = "";
        public static string LastSummary { get; private set; } = "No measurement yet (runs only with players connected, every minute).";
        public static readonly List<string> LastPlayerLines = new();

        public static long Now => Stopwatch.GetTimestamp();
        public static void Add(ref long calls, ref long ticks, long start)
        {
            calls++;
            ticks += Stopwatch.GetTimestamp() - start;
        }

        public static void Start()
        {
            if (_running) return;
            _running = true;
            ApplyNetPatches();
            _lastWindowStart = Time.realtimeSinceStartupAsDouble;
            try { _lastCpu = Process.GetCurrentProcess().TotalProcessorTime; } catch { }
            _lastCpuWall = DateTime.UtcNow;
            FrameLoop().RunCoro();
            RLog.Msg($"{CharacterReplacement.Tag} [Perf] monitor started (cores={Environment.ProcessorCount})");
        }

        private static void ApplyNetPatches()
        {
            if (_patched) return;
            _patched = true;
            try
            {
                var h = new HarmonyLib.Harmony("livingsoldiers.perf");
                var lost = AccessTools.Method(typeof(BoltConnection), "PacketLost");
                if (lost != null) h.Patch(lost, postfix: new HarmonyMethod(typeof(Perf), nameof(PacketLostPostfix)));
                var disc = AccessTools.Method(typeof(BoltCore), "Udp_Disconnect");
                if (disc != null) h.Patch(disc, prefix: new HarmonyMethod(typeof(Perf), nameof(DisconnectPrefix)));
                RLog.Msg($"{CharacterReplacement.Tag} [Perf] net hooks: packetLost={lost != null} disconnect={disc != null}");
            }
            catch (Exception e)
            {
                RLog.Warning($"{CharacterReplacement.Tag} [Perf] net hooks failed: {e.Message}");
            }
        }

        private static void PacketLostPostfix(BoltConnection __instance)
        {
            try
            {
                IntPtr p = __instance.Pointer;
                LostByConn.TryGetValue(p, out int n);
                LostByConn[p] = n + 1;
            }
            catch { }
        }

        private static void DisconnectPrefix(UdpKit.UdpConnection __0)
        {
            try
            {
                string state = "?", rtt = "?", win = "?";
                try { state = __0.State.ToString(); } catch { }
                try { rtt = ((int)(__0.NetworkRtt * 1000f)).ToString(); } catch { }
                try { win = ((int)(__0.WindowFillRatio * 100f)).ToString(); } catch { }
                ConnInfo.TryGetValue(__0.Pointer, out string last);
                RLog.Warning($"{CharacterReplacement.Tag} [Perf] DISCONNECT state={state} rtt={rtt}ms window={win}% | last minute: {last ?? "-"} | server: {LastSummary}");
            }
            catch (Exception e)
            {
                RLog.Warning($"{CharacterReplacement.Tag} [Perf] disconnect log failed: {e.Message}");
            }
        }

        private static IEnumerator FrameLoop()
        {
            while (true)
            {
                try { Profiler.Tick(); } catch { }
                float dt = Time.unscaledDeltaTime;
                _frames++;
                _dtSum += dt;
                if (dt > _dtMax) _dtMax = dt;
                if (dt > 0.05f) _hitch50++;
                if (dt > 0.2f) _hitch200++;
                if (dt > 1f) _hitch1000++;

                double now = Time.realtimeSinceStartupAsDouble;
                if (now - _lastWindowStart >= 60.0)
                {
                    try { Report(now - _lastWindowStart); }
                    catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Perf] report failed: {e.Message}"); }
                    _lastWindowStart = now;
                    _frames = 0; _dtSum = 0; _dtMax = 0; _hitch50 = _hitch200 = _hitch1000 = 0;
                }
                yield return null;
            }
        }

        private static List<BoltEntity> Players()
        {
            var list = new List<BoltEntity>();
            try
            {
                var pe = MultiplayerUtilities._playerEntities;
                if (pe == null) return list;
                for (int i = 0; i < pe.Count; i++)
                {
                    var e = pe[i];
                    if (e && e.isAttached) list.Add(e);
                }
            }
            catch { }
            return list;
        }

        private static void Report(double window)
        {
            var players = Players();
            // CPU of the server process (share of ALL cores and in "cores used")
            double cpuPct = -1, coresUsed = -1;
            long ramMb = -1;
            try
            {
                var proc = Process.GetCurrentProcess();
                var cpu = proc.TotalProcessorTime;
                var wall = DateTime.UtcNow;
                double used = (cpu - _lastCpu).TotalSeconds;
                double span = (wall - _lastCpuWall).TotalSeconds;
                if (span > 0)
                {
                    coresUsed = used / span;
                    cpuPct = coresUsed / Environment.ProcessorCount * 100.0;
                }
                _lastCpu = cpu;
                _lastCpuWall = wall;
                ramMb = proc.WorkingSet64 / (1024 * 1024);
            }
            catch { }

            if (players.Count == 0) return; // idle server runs at 5 fps on purpose, nothing to learn

            float fps = window > 0 ? (float)(_frames / window) : 0;
            int target = Application.targetFrameRate;

            int entOk = -1, entFz = -1;
            try { entOk = BoltCore._entitiesOK.count; entFz = BoltCore._entitiesFZ.count; } catch { }

            long modTicks = HolderTicks + AttnTicks + RobbyTicks + TreeTicks + JobTicks + WatchTicks;
            double modMsPerSec = (modTicks - _lastModTicks) * 1000.0 / Stopwatch.Frequency / window;
            _lastModTicks = modTicks;

            if (Time.realtimeSinceStartup - _lastActorScan > 60f)
            {
                _lastActorScan = Time.realtimeSinceStartup;
                try
                {
                    var actors = UnityEngine.Object.FindObjectsOfType<VailActor>();
                    int robbies = 0;
                    foreach (var a in actors)
                    {
                        var t = a.TypeId;
                        if (t == CharacterReplacement.RobbyType || t == CharacterReplacement.PlayerRobbyType) robbies++;
                    }
                    _actorLine = $"AI actors in the world: {actors.Length} total, of which Kelvin/soldiers {robbies}";
                }
                catch (Exception e) { _actorLine = $"KI-Actors: ? ({e.Message})"; }
            }

            LastSummary = $"Server {fps:0} fps (target {target}), longest frame {_dtMax * 1000f:0} ms, hitches >50ms {_hitch50} >200ms {_hitch200} >1s {_hitch1000}, " +
                          $"CPU {cpuPct:0}% ({coresUsed:0.00} of {Environment.ProcessorCount} cores), RAM {ramMb} MB, Bolt-Entities {entOk} (+{entFz} frozen), Mod {modMsPerSec:0.00} ms/s";
            RLog.Msg($"{CharacterReplacement.Tag} [Perf] {LastSummary} | {_actorLine} | hooks: holder {HolderCalls}, attention {AttnCalls}, robby {RobbyCalls}, tree {TreeCalls}");

            LastPlayerLines.Clear();
            foreach (var e in players)
            {
                try
                {
                    ulong sid = MultiplayerUtilities.GetSteamId(e);
                    BoltConnection c = MultiplayerUtilities.GetConnectionFromSteamId(sid);
                    if (c == null) continue;
                    var udp = c.udpConnection;
                    int ping = (int)(c.PingNetwork * 1000f);
                    int rtt = udp != null ? (int)(udp.NetworkRtt * 1000f) : -1;
                    int win = udp != null ? (int)(udp.WindowFillRatio * 100f) : -1;
                    LostByConn.TryGetValue(c.Pointer, out int lost);
                    LostByConn[c.Pointer] = 0;
                    string sidShort = sid.ToString();
                    sidShort = sidShort.Length > 4 ? "..." + sidShort.Substring(sidShort.Length - 4) : sidShort;
                    string line = $"Player {sidShort}: ping {ping} ms (rtt {rtt}), send window {win}%, in {c.BitsPerSecondIn / 1000} kbit/s, out {c.BitsPerSecondOut / 1000} kbit/s, lost packets {lost}/min";
                    LastPlayerLines.Add(line);
                    if (udp != null) ConnInfo[udp.Pointer] = line;
                    RLog.Msg($"{CharacterReplacement.Tag} [Perf] {line}");
                }
                catch (Exception ex)
                {
                    RLog.Warning($"{CharacterReplacement.Tag} [Perf] player stats failed: {ex.Message}");
                }
            }
        }

        public static IEnumerable<string> ChatLines()
        {
            yield return LastSummary;
            if (!string.IsNullOrEmpty(_actorLine)) yield return _actorLine;
            foreach (var l in LastPlayerLines) yield return l;
        }
    }
}
