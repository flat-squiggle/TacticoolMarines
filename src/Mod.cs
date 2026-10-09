// Tacticool Marines – host-only / dedicated server build
// Based on "Living Soldiers" v1.0.0 by Toni Macaroni (reconstructed from the released DLL for personal use).
// Only the host (or single player) needs this mod:
//  - soldiers are spawned on the host and attached to Bolt, so the vanilla game replicates them to everyone
//  - by default they are spawned as "PlayerRobby" (Kelvin AI with a player body), which vanilla clients
//    can render with a player head/skin/clothing; falls back to plain Kelvin if that prefab is missing
//  - no custom network packets are sent, so players without RedLoader are not affected
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Versioning;
using BundleData;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using Sons.Gameplay.GPS;
using Sons.Save;
using Sons.Wearable.Armour;
using Sons.Wearable.Clothing;
using SonsSdk;
using SonsSdk.Attributes;
using SonsSdk.Networking;
using Sons.Multiplayer;
using SUI;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;
using Color = System.Drawing.Color;
using GameState = Sons.Save.GameState;
using Object = UnityEngine.Object;

[assembly: TargetFramework(".NETCoreApp,Version=v6.0", FrameworkDisplayName = ".NET 6.0")]

namespace CharacterReplacement
{
    public enum SoldierKind : byte { Tactical = 0, Jianyu = 1 }

    public class CharacterReplacement : SonsMod
    {
        public const string Tag = "[TacticoolMarines]";
        internal const VailActorTypeId RobbyType = (VailActorTypeId)9;
        internal const VailActorTypeId PlayerRobbyType = (VailActorTypeId)100;
        private const int RaceCount = 8;

        private static readonly System.Random Rng = new();
        private static bool _setupDone;
        internal static bool SetupDone => _setupDone && SoldierSpots.All().Count > 0;
        private static bool _serverRoutineRunning;

        // Positions of the static dying soldiers (from a client log). Used when the scene objects
        // are not present, e.g. on a dedicated server.
        private static readonly (string name, Vector3 pos)[] KnownSoldierSpots =
        {
            ("DyingTacticalB001", new Vector3(-531.02185f, 72.591f, -1589.4661f)),
            ("DyingTacticalB000", new Vector3(800.997f, 227.59117f, 977.59f)),
            ("DyingTacticalB002", new Vector3(62.204f, 87.76f, 1245.337f)),
            ("DyingTacticalA001", new Vector3(1053.444f, 143.004f, 1048.477f)),
            ("DyingTacticalA008", new Vector3(-1258.917f, 212.39f, -596.763f)),
            ("DyingTacticalA009", new Vector3(-1724.549f, 89.074f, 59.463f)),
            ("DyingTacticalA003", new Vector3(1467.533f, 178.381f, -650.254f)),
            ("DyingTacticalA002", new Vector3(1486.594f, 83.011f, 65.542f)),
            ("DyingTacticalA005", new Vector3(-45.864746f, 16.764235f, 1457.0334f)),
            ("DyingTacticalA000", new Vector3(-701.758f, 102.929f, 446.321f)),
            ("DyingTacticalA007", new Vector3(1285.102f, 227.972f, -726.405f)),
        };

        private static bool IsDedicated => NetUtils.IsDedicatedServer;

        protected override void OnInitializeMod()
        {
            Config.Init();
            SoldierPersistence.Register();
            TreeRangePatch.Apply();
            HolderRangePatch.Apply();
            NoBreaks.Apply();
            SoldierHealth.Apply();
            SoldierAdopt.Apply();
            Profiler.Apply();
            SdkEvents.OnWorldExited.Subscribe(() => { SoldierPersistence.ClearLoaded(); AutoJobs.Clear(); SoldierAdopt.Clear(); CombatGun.Clear(); CombatLook.Clear(); CombatAI.Clear(); CombatFx.Clear(); CombatDummy.Clear(); ModelRig.Clear(); CombatGunLight.Clear(); SoldierHealth.Clear(); _setupDone = false; });
            if (!IsDedicated) GlobalEvents.OnLateUpdate.Subscribe(() => { if (_setupDone) { ModelRig.LateTick(); CombatDummy.SyncHands(); CombatFeet.LateTick(); CombatGun.LateTick(); } });
            if (!IsDedicated) SdkEvents.OnInWorldUpdate.Subscribe(() => { if (_setupDone) { AutoJobs.Tick(); DeadCleanup.Tick(); NoBreaks.Tick(); CombatGun.Tick(); CombatAI.Tick(); CombatFx.Tick(); } });
            if (IsDedicated)
            {
                // OnSdkInitialized is not guaranteed on a dedicated server, so register everything here.
                RLog.Msg($"{Tag} Dedicated server mode (no models/UI loaded)");
                ServerPatches.Apply();
                ServerCommands.RegisterCommand("tmhelp", PlayerRoles.Admin, args =>
                {
                    foreach (var line in Help.Lines(args.HasArgs ? args.Args[0] : null)) Reply(args.SteamId, line);
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmcommands", PlayerRoles.Admin, args =>
                {
                    foreach (var line in Help.Lines(args.HasArgs ? args.Args[0] : null)) Reply(args.SteamId, line);
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tminfo", PlayerRoles.Admin, args => { InfoCommand(); ReplyStatus(args.SteamId); return ServerCommands.EExecutionResult.Success; });
                ServerCommands.RegisterCommand("tmspawn", PlayerRoles.Admin, SpawnForPlayerCommand);
                ServerCommands.RegisterCommand("tmrange", PlayerRoles.Admin, args =>
                {
                    if (!args.TryGetFloat(0, out float r)) { Reply(args.SteamId, $"Current: search x{Config.OrderRangeMultiplier.Value}, clearing x{Config.ClearRadiusMultiplier.Value}, structures x{Config.HolderRangeMultiplier.Value}. Usage: /tmrange <search> [clearing] [structures]"); return ServerCommands.EExecutionResult.Success; }
                    float c = args.TryGetFloat(1, out float cc) ? cc : Config.ClearRadiusMultiplier.Value;
                    float hm = args.TryGetFloat(2, out float hh) ? hh : Config.HolderRangeMultiplier.Value;
                    SetRange(r, c, hm);
                    Reply(args.SteamId, $"Search x{Config.OrderRangeMultiplier.Value}, clearing x{Config.ClearRadiusMultiplier.Value}, structures x{Config.HolderRangeMultiplier.Value} (saved)");
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmbuild", PlayerRoles.Admin, args => AllOrderCommand(args, Robby.OrderType.Build, "Build"));
                ServerCommands.RegisterCommand("tmmaintain", PlayerRoles.Admin, args => AllOrderCommand(args, Robby.OrderType.MaintainBase, "Maintain base"));
                ServerCommands.RegisterCommand("tmclear", PlayerRoles.Admin, args => AllOrderCommand(args, Robby.OrderType.ClearArea, "Clear area"));
                ServerCommands.RegisterCommand("tmget", PlayerRoles.Admin, args => AllOrderCommand(args, Robby.OrderType.Get, "Fetch"));
                ServerCommands.RegisterCommand("tmfollow", PlayerRoles.Admin, args => AllOrderCommand(args, Robby.OrderType.Follow, "Follow"));
                ServerCommands.RegisterCommand("tmstay", PlayerRoles.Admin, args => AllOrderCommand(args, Robby.OrderType.StayHere, "Stay here"));
                ServerCommands.RegisterCommand("tmstruct", PlayerRoles.Admin, args =>
                {
                    if (args.TryGetFloat(0, out float sm))
                        SetRange(Config.OrderRangeMultiplier.Value, Config.ClearRadiusMultiplier.Value, sm);
                    Reply(args.SteamId, $"Structure range (holders, sleds, build sites, repairs): x{Config.HolderRangeMultiplier.Value}. Usage: /tmstruct <factor>, e.g. /tmstruct 4");
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmjobs", PlayerRoles.Admin, args =>
                {
                    if (args.HasArgs)
                    {
                        string a0 = args.Args[0].ToLowerInvariant();
                        if (a0 == "off") Config.AutoJobsEnabled.Value = false;
                        else if (a0 == "on") Config.AutoJobsEnabled.Value = true;
                        else if (a0 == "for" && args.Args.Length > 1) Config.AutoJobsFor.Value = args.Args[1].ToLowerInvariant();
                        else if (a0 == "idle" && args.TryGetFloat(1, out float sec)) Config.AutoJobsIdleSeconds.Value = Math.Max(10f, sec);
                        else if (a0 == "holder" && args.TryGetFloat(1, out float hr)) Config.AutoJobsHolderRadius.Value = Math.Clamp(hr, 5f, 300f);
                        else if (a0 == "player" && args.TryGetFloat(1, out float pr)) Config.AutoJobsPlayerRadius.Value = Math.Clamp(pr, 10f, 2000f);
                        else Config.AutoJobs.Value = string.Join(",", args.Args);
                        try { Config.Category.SaveToFile(); } catch { }
                    }
                    string jobs = (Config.AutoJobsEnabled.Value ? "ON" : "OFF") + " (" + (string.IsNullOrEmpty(Config.AutoJobs.Value) ? "-" : Config.AutoJobs.Value) + ")";
                    Reply(args.SteamId, $"Auto jobs: {jobs} | for: {Config.AutoJobsFor.Value} | after {Config.AutoJobsIdleSeconds.Value}s idle | holder <= {Config.AutoJobsHolderRadius.Value} m, player <= {Config.AutoJobsPlayerRadius.Value} m");
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmbreaks", PlayerRoles.Admin, args =>
                {
                    if (args.HasArgs)
                    {
                        string a0 = args.Args[0].ToLowerInvariant();
                        if (a0 == "on") Config.NoBreaks.Value = true;
                        else if (a0 == "off") Config.NoBreaks.Value = false;
                        try { Config.Category.SaveToFile(); } catch { }
                        NoBreaks.ApplyOrderData();
                    }
                    Reply(args.SteamId, $"No breaks (they keep working): {(Config.NoBreaks.Value ? "ON" : "OFF")}. Usage: /tmbreaks on|off");
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmclean", PlayerRoles.Admin, args =>
                {
                    if (args.HasArgs)
                    {
                        string a0 = args.Args[0].ToLowerInvariant();
                        if (a0 == "on") Config.RemoveDeadSoldiers.Value = true;
                        else if (a0 == "off") Config.RemoveDeadSoldiers.Value = false;
                        else if (args.TryGetFloat(0, out float sec)) Config.RemoveDeadAfterSeconds.Value = Math.Clamp(sec, 0f, 3600f);
                        else if (a0 == "now") DeadCleanup.Tick(force: true);
                        try { Config.Category.SaveToFile(); } catch { }
                    }
                    var (bodies, removed) = DeadCleanup.Status();
                    Reply(args.SteamId, $"Remove dead soldiers: {(Config.RemoveDeadSoldiers.Value ? "ON" : "OFF")} after {Config.RemoveDeadAfterSeconds.Value:0}s | bodies now: {bodies} | removed: {removed}. Usage: /tmclean on|off|now|<seconds>");
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmremove", PlayerRoles.Admin, args =>
                {
                    if (!args.HasArgs)
                    {
                        foreach (var line in RemoveSoldiers.List(args.SteamId)) Reply(args.SteamId, line);
                        return ServerCommands.EExecutionResult.Success;
                    }
                    string a0 = args.Args[0].ToLowerInvariant();
                    if (a0 == "all")
                    {
                        Reply(args.SteamId, RemoveSoldiers.RemoveAll(args.SteamId));
                    }
                    else if (a0 == "dupes")
                    {
                        Reply(args.SteamId, RemoveSoldiers.RemoveSurplus(args.SteamId, args.Args.Length > 1 && args.Args[1].ToLowerInvariant() == "force"));
                    }
                    else if (int.TryParse(a0, out int n))
                    {
                        Reply(args.SteamId, RemoveSoldiers.RemoveCount(n));
                    }
                    else
                    {
                        RemoveSoldiers.RemoveByName(args.Args[0], out string msg);
                        Reply(args.SteamId, msg);
                    }
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmprofile", PlayerRoles.Admin, args =>
                {
                    foreach (var line in Profiler.ChatLines()) Reply(args.SteamId, line);
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmperf", PlayerRoles.Admin, args =>
                {
                    foreach (var line in Perf.ChatLines()) Reply(args.SteamId, line);
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmcombat", PlayerRoles.Admin, args =>
                {
                    foreach (var line in CombatInspect.Run(args.ArgsAsString())) Reply(args.SteamId, line);
                    return ServerCommands.EExecutionResult.Success;
                });
                ServerCommands.RegisterCommand("tmlook", PlayerRoles.Admin, args => { LookCommand(args.ArgsAsString()); Reply(args.SteamId, "Looks of all soldiers re-rolled"); return ServerCommands.EExecutionResult.Success; });
                SdkEvents.OnGameActivated.Subscribe(StartServerSetup);
                SdkEvents.OnWorldExited.Subscribe(() => { _setupDone = false; _serverRoutineRunning = false; SoldierRegistry.Clear(); });
            }
        }

        protected override void OnSdkInitialized()
        {
            if (IsDedicated)
            {
                return;
            }
            CharacterBundle.LoadFromFile(DataPath / "character");
            SettingsRegistry.CreateSettings(this, null, typeof(Config));
        }

        protected override void OnGameStart()
        {
            if (IsDedicated)
            {
                // OnInWorldUpdate needs a local player, which a dedicated server does not have.
                StartServerSetup();
                return;
            }
            SoldierRegistry.Clear();
            _setupDone = false;
            SdkEvents.OnInWorldUpdate.Subscribe(OnFirstInWorld, 0, unsubscribeOnFirstInvocation: true);
        }

        private void StartServerSetup()
        {
            if (_serverRoutineRunning || _setupDone) return;
            _serverRoutineRunning = true;
            RLog.Msg($"{Tag} Server: waiting for world simulation");
            ServerSetupRoutine().RunCoro();
        }

        private IEnumerator ServerSetupRoutine()
        {
            // Realtime waiting: an idle dedicated server may run with timeScale 0, so WaitForSeconds would never finish.
            float start = Time.realtimeSinceStartup;
            float nextReport = start;
            while (true)
            {
                bool sim = VailWorldSimulation.TryGetInstance(out _);
                bool prefab = sim && (bool)ActorTools.GetPrefab(RobbyType);
                if (sim && prefab) break;
                float now = Time.realtimeSinceStartup;
                if (now >= nextReport)
                {
                    RLog.Msg($"{Tag} Server: waiting (worldSim={sim}, robbyPrefab={prefab}, timeScale={Time.timeScale})");
                    nextReport = now + 15f;
                }
                if (now - start > 300f)
                {
                    RLog.Error($"{Tag} World simulation not ready after 5 minutes, soldiers not spawned");
                    _serverRoutineRunning = false;
                    yield break;
                }
                yield return null;
            }
            // Wait well past the end of save loading ("Dedicated server loaded"), loading may reset actors.
            RLog.Msg($"{Tag} Server: world ready (timeScale={Time.timeScale}), spawning in 45s");
            float until = Time.realtimeSinceStartup + 45f;
            while (Time.realtimeSinceStartup < until) yield return null;
            OnFirstInWorld();
            _serverRoutineRunning = false;
            ServerWatchdog().RunCoro();
        }

        /// <summary>Logs soldier state regularly and respawns soldiers that vanished before being revived.</summary>
        private IEnumerator ServerWatchdog()
        {
            float start = Time.realtimeSinceStartup;
            float next = start + 60f;
            Perf.Start();
            long dummy = 0;
            while (_setupDone)
            {
                long t0 = Perf.Now;
                AutoJobs.Tick();
                DeadCleanup.Tick();
                NoBreaks.Tick();
                CombatAI.Tick();
                Perf.Add(ref dummy, ref Perf.JobTicks, t0);
                if (Time.realtimeSinceStartup >= next)
                {
                    float elapsed = Time.realtimeSinceStartup - start;
                    next = Time.realtimeSinceStartup + (elapsed < 600f ? 60f : 300f);
                    long t1 = Perf.Now;
                    try { WatchdogTick(); } catch (Exception e) { RLog.Error($"{Tag} Watchdog: {e}"); }
                    Perf.Add(ref dummy, ref Perf.WatchTicks, t1);
                }
                yield return null;
            }
        }

        private static void WatchdogTick()
        {
            int alive = 0, attached = 0, respawned = 0;
            foreach (var spot in SoldierSpots.All())
            {
                if (spot.Dead) continue;
                if (spot.Actor)
                {
                    alive++;
                    spot.LastPosition = spot.Actor.transform.position;
                    BoltEntity be = GetEntity(spot.Actor);
                    if (be && be.isAttached) attached++;
                    continue;
                }
                bool revived = spot.Extra || IsRevived(spot.Name);
                if (revived)
                {
                    spot.Dead = true; // revived soldiers that disappear were most likely killed – leave them
                    RLog.Msg($"{Tag} Watchdog: {spot.Name} is gone (probably killed)");
                    continue;
                }
                RLog.Warning($"{Tag} Watchdog: soldier {spot.Name} vanished before being revived, respawning");
                SpawnSoldierAt(spot.Name, spot.Position, null, false);
                respawned++;
            }
            RLog.Msg($"{Tag} Watchdog: {alive} alive, {attached} networked, {respawned} respawned");
            InfoCommand();
        }

        internal static IEnumerator RealtimeDelay(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        // ---------------------------------------------------------------- setup

        private void OnFirstInWorld()
        {
            if (_setupDone) return;
            _setupDone = true;
            try
            {
                if (NetUtils.IsClient)
                {
                    // In this build the host does everything. A client that also has the mod only hides
                    // the static dying soldiers so it does not see them twice.
                    RLog.Msg(Color.Orange, $"{Tag} Joined as client: soldiers come from the host");
                    RemoveStaticDyingSoldiers();
                    return;
                }
                HostSetup();
            }
            catch (Exception e)
            {
                RLog.Error($"{Tag} Setup failed: {e}");
            }
        }

        private void HostSetup()
        {
            OrderRange.Apply(Config.OrderRangeMultiplier.Value, Config.ClearRadiusMultiplier.Value);
            NoBreaks.ApplyOrderData();
            RLog.Msg($"{Tag} Setting up chars! (multiplayer={NetUtils.IsMultiplayer}, server={NetUtils.IsServer}, dedicated={NetUtils.IsDedicatedServer}, playerModel={Config.UsePlayerModel.Value})");

            if (Config.AddJianyu.Value && !NetUtils.IsDedicatedServer)
            {
                NamedIntData jianyuId = GameState.GetOrCreate("Jianyu_actorid", -1, true);
                if (jianyuId.SaveValue == -1 || !TryGetCompanion(jianyuId.SaveValue, out VailActor jianyu))
                {
                    jianyu = SpawnCompanion(LocalPlayer.Transform.position + LocalPlayer.Transform.forward * 2f, LocalPlayer.Transform.rotation);
                }
                SetupSoldier(jianyu, SoldierKind.Jianyu);
                jianyuId.SetValue(jianyu.UniqueId);
            }

            var dyingSoldiers = Resources.FindObjectsOfTypeAll<DyingTacticoolController>();
            if (dyingSoldiers.Length == 0)
            {
                RLog.Msg($"{Tag} No dying soldier objects in scene, using known positions");
                SpawnSavedExtras();
                foreach (var (spotName, spotPos) in KnownSoldierSpots)
                {
                    SpawnSoldierAt(spotName, spotPos, null);
                }
                RemoveStoryProps();
                return;
            }

            SpawnSavedExtras();

            foreach (DyingTacticoolController dying in dyingSoldiers)
            {
                // In co-op the static dying soldier stays visible for players without the mod,
                // so put the living one next to it instead of inside it.
                Vector3 pos = dying.transform.position;
                if (NetUtils.IsMultiplayer) pos += dying.transform.right * 1.2f;
                SpawnSoldierAt(dying.name, pos, dying);
            }

            RemoveStoryProps();
        }

        /// <summary>Restores soldiers that were spawned by command (not tied to a crash site).</summary>
        private static void SpawnSavedExtras()
        {
            foreach (var e in SoldierPersistence.Extras())
            {
                if (e.Dead) { SoldierSpots.Set(e.Name, new Vector3(e.X, e.Y, e.Z), null, (SoldierKind)e.Kind, true).Dead = true; continue; }
                try
                {
                    Vector3 pos = new Vector3(e.X, e.Y + 0.3f, e.Z);
                    // The game saves and restores the actor itself. Spawning a new one each load (as before) left the
                    // old one standing there as a plain Kelvin - one more per extra soldier and load. So: his id is kept
                    // in the save like the crash-site soldiers', and he is taken over again (or waited for).
                    NamedIntData idData = GameState.GetOrCreate(e.Name + "_actorid", -1, true);
                    VailActor actor = null;
                    if (idData.SaveValue > 0 && !TryGetCompanion(idData.SaveValue, out _) && WorldCleanup.SimActorExists(idData.SaveValue))
                    {
                        SoldierSpots.Set(e.Name, pos, null, (SoldierKind)e.Kind, true);
                        SoldierAdopt.Expect(idData.SaveValue, e.Name, pos);
                        RLog.Msg($"{Tag} Extra soldier {e.Name}: {idData.SaveValue} is still in the world simulation, waiting for him");
                        continue;
                    }
                    if (idData.SaveValue > 0 && TryGetCompanion(idData.SaveValue, out actor))
                        RLog.Msg($"{Tag} Took over saved extra soldier {e.Name} ({actor.name}, {actor.UniqueId})");
                    else
                    {
                        actor = SpawnCompanion(pos, Quaternion.identity);
                        RLog.Msg($"{Tag} Restored extra soldier {e.Name} at {pos} (new actor)");
                    }
                    actor.name = "LivingSoldier_" + e.Name;
                    SetupSoldier(actor, (SoldierKind)e.Kind);
                    SoldierSpots.Set(e.Name, pos, actor, (SoldierKind)e.Kind, true);
                    idData.SetValue(actor.UniqueId);
                }
                catch (Exception ex)
                {
                    RLog.Error($"{Tag} Restoring {e.Name} failed: {ex}");
                }
            }
        }

        internal static void SpawnExtraSoldier(Vector3 pos, Quaternion rot)
        {
            string name = SoldierPersistence.NextDebugName();
            VailActor actor = SpawnCompanion(pos, rot);
            actor.name = "LivingSoldier_" + name;
            SetupSoldier(actor, SoldierKind.Tactical);
            SoldierSpots.Set(name, pos, actor, SoldierKind.Tactical, true);
            try { GameState.GetOrCreate(name + "_actorid", -1, true).SetValue(actor.UniqueId); } catch { }
        }

        private static void SpawnSoldierAt(string name, Vector3 position, DyingTacticoolController dying, bool reuseSaved = true)
        {
            try
            {
                NamedIntData idData = GameState.GetOrCreate(name + "_actorid", -1, true);
                RLog.Msg($"{Tag} Found dying tactical {name} ({position.x} {position.y} {position.z})");

                Vector3 spawnPos = position;
                if (SoldierPersistence.TryGet(name, out var saved))
                {
                    if (saved.Dead)
                    {
                        RLog.Msg($"{Tag} {name} died in this save, not spawning");
                        SoldierSpots.Set(name, position, null, SoldierKind.Tactical).Dead = true;
                        if (dying) dying.gameObject.TryDestroy();
                        return;
                    }
                    if (saved.Revived && reuseSaved)
                    {
                        spawnPos = new Vector3(saved.X, saved.Y + 0.3f, saved.Z);
                        RLog.Msg($"{Tag} {name} was revived, restoring at saved position {spawnPos}");
                    }
                }

                VailActor robby = null;
                if (reuseSaved && idData.SaveValue > 0 && !TryGetCompanion(idData.SaveValue, out _)
                    && WorldCleanup.SimActorExists(idData.SaveValue))
                {
                    // He exists, he is just too far away to be a real object right now. Spawning a second one
                    // here is exactly what filled the world with duplicates - so wait for him instead.
                    RLog.Msg($"{Tag} {name}: soldier {idData.SaveValue} is still in the world simulation, waiting for him instead of spawning a new one");
                    SoldierSpots.Set(name, position, null, SoldierKind.Tactical);
                    SoldierAdopt.Expect(idData.SaveValue, name, position);
                    if (dying) dying.gameObject.TryDestroy();
                    return;
                }
                if (!reuseSaved || idData.SaveValue == -1 || !TryGetCompanion(idData.SaveValue, out robby))
                {
                    robby = SpawnCompanion(spawnPos, Quaternion.identity);
                    robby.name = "LivingSoldier_" + name;
                    RLog.Msg($"{Tag} Created new soldier ({robby.name}) at {position}");
                }
                else
                {
                    RLog.Msg($"{Tag} Possessing existing soldier ({robby.name}, {robby.UniqueId})");
                }

                bool alreadyRevived = IsRevived(name);
                SetupSoldier(robby, SoldierKind.Tactical, alreadyRevived);
                idData.SetValue(robby.UniqueId);
                SoldierSpots.Set(name, position, robby, SoldierKind.Tactical);

                var injuredObjects = new List<GameObject>();
                if (dying && !IsDedicated)
                {
                    injuredObjects.AddRange(TakeFlare(dying, robby.transform.position, alreadyRevived));
                }

                SoldierController controller = robby.gameObject.AddComponent<SoldierController>();
                controller.Init(name, injuredObjects);

                if (dying) dying.gameObject.TryDestroy();
            }
            catch (Exception e)
            {
                RLog.Error($"{Tag} Spawning soldier {name} failed: {e}");
            }
        }

        /// <summary>Takes over a soldier that the world simulation just turned back into a real actor.</summary>
        internal static void AdoptExisting(string spotName, Vector3 home, VailActor actor)
        {
            try
            {
                if (!actor) return;
                if (string.IsNullOrEmpty(actor.name) || actor.name.IndexOf("LivingSoldier_", StringComparison.Ordinal) < 0)
                    actor.name = "LivingSoldier_" + spotName;
                bool extra = spotName.StartsWith("Debug_", StringComparison.Ordinal);
                SetupSoldier(actor, SoldierKind.Tactical, extra || IsRevived(spotName));
                try { GameState.GetOrCreate(spotName + "_actorid", -1, true).SetValue(actor.UniqueId); } catch { }
                SoldierSpots.Set(spotName, home, actor, SoldierKind.Tactical, extra);
                if (!actor.gameObject.GetComponent<SoldierController>())
                    actor.gameObject.AddComponent<SoldierController>().Init(spotName, new List<GameObject>());
                RLog.Msg($"{Tag} Took over existing soldier {spotName} (id {actor.UniqueId})");
            }
            catch (Exception e)
            {
                RLog.Warning($"{Tag} Taking over {spotName} failed: {e.Message}");
            }
        }

        private static bool _flareTreeLogged;

        /// <summary>
        /// Writes the object tree of a crash site to the log once, with the components that matter for the
        /// flare. Without it we are guessing at names - this shows what is actually there.
        /// </summary>
        private static void LogFlareTree(DyingTacticoolController dying)
        {
            if (_flareTreeLogged) return;
            _flareTreeLogged = true;
            try
            {
                Transform root = dying.transform;
                RLog.Msg($"{Tag} ---- object tree of the wreck '{dying.name}' ----");
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    int depth = 0;
                    for (Transform p = t; p && p != root; p = p.parent) depth++;
                    var bits = new List<string>();
                    if (t.GetComponent<ParticleSystem>()) bits.Add("particles");
                    if (t.GetComponent<Light>()) bits.Add("light");
                    if (t.GetComponent<AudioSource>()) bits.Add("Audio");
                    var r = t.GetComponent<Renderer>();
                    if (r) bits.Add("Renderer" + (r.enabled ? "" : "(off)"));
                    if (t.GetComponent<ParentConstraint>()) bits.Add("ParentConstraint");
                    string extra = bits.Count > 0 ? "  [" + string.Join(", ", bits) + "]" : "";
                    RLog.Msg($"{Tag}   {new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (inactive)")}{extra}");
                }
                RLog.Msg($"{Tag} ---- end of object tree ----");
            }
            catch (Exception e) { RLog.Warning($"{Tag} object tree could not be written: {e.Message}"); }
        }

        /// <summary>
        /// Takes the visual markers off the crash site instead of copying them. Two separate objects matter:
        /// "FlareLit" is the burning flare with its sparks, and "DeadTactiSmoke" is the smoke column that can
        /// be seen from far away - it hangs next to the flare, not below it, which is why taking only the
        /// flare left the site invisible from a distance. Both are detached, so they survive the crash site
        /// being removed, and both are started explicitly.
        /// </summary>
        private static List<GameObject> TakeFlare(DyingTacticoolController dying, Vector3 fallbackPos, bool revived)
        {
            var taken = new List<GameObject>();
            try
            {
                LogFlareTree(dying);
                Transform root = dying.transform;
                var candidates = new List<Transform>();

                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t == root) continue;
                    string n = t.name;
                    bool interesting = n.IndexOf("Flare", StringComparison.OrdinalIgnoreCase) >= 0
                                    || n.IndexOf("Smoke", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!interesting) continue;
                    // "FlareTarget" is just a bone on the hand - only objects that actually show something.
                    if (!t.GetComponentInChildren<ParticleSystem>(true) && !t.GetComponentInChildren<Renderer>(true)) continue;
                    // Skip anything already contained in another candidate.
                    bool nested = false;
                    foreach (var c in candidates)
                    {
                        for (Transform p = t.parent; p; p = p.parent) { if (p == c) { nested = true; break; } }
                        if (nested) break;
                    }
                    if (!nested) candidates.Add(t);
                }

                foreach (var t in candidates)
                {
                    try
                    {
                        Vector3 pos = t.position;
                        Quaternion rot = t.rotation;
                        t.SetParent(null, true);
                        var pc = t.GetComponent<ParentConstraint>();
                        if (pc) Object.Destroy(pc);
                        t.SetPositionAndRotation(pos, rot);
                        t.gameObject.SetActive(!revived);
                        if (!revived) LightFlare(t.gameObject);
                        taken.Add(t.gameObject);
                        RLog.Msg($"{Tag} '{t.name}' copied from the wreck (particle systems={t.GetComponentsInChildren<ParticleSystem>(true).Length})");
                    }
                    catch (Exception e) { RLog.Warning($"{Tag} '{t.name}' could not be copied: {e.Message}"); }
                }
                if (taken.Count == 0) RLog.Warning($"{Tag} no flare/smoke objects found at the wreck {dying.name}");
            }
            catch (Exception e)
            {
                RLog.Warning($"{Tag} flare could not be copied: {e.Message}");
            }
            return taken;
        }

        /// <summary>Switches on everything below the flare and starts the particle systems by hand.</summary>
        internal static void LightFlare(GameObject flare)
        {
            if (!flare) return;
            try
            {
                foreach (Transform t in flare.GetComponentsInChildren<Transform>(true))
                    if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                foreach (var r in flare.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                foreach (var l in flare.GetComponentsInChildren<Light>(true)) l.enabled = true;
                foreach (var ps in flare.GetComponentsInChildren<ParticleSystem>(true))
                {
                    try
                    {
                        // Der Rauch lief kurz und hoerte dann auf: das Partikelsystem ist ein einmaliger
                        // Durchlauf, und das Skript, das es am Leben hielt, sass auf dem Wrack, das wir
                        // danach entfernen. Also selbst auf Dauerschleife stellen.
                        var main = ps.main;
                        main.loop = true;
                        main.playOnAwake = true;
                        var em = ps.emission; em.enabled = true;
                        ps.Clear(true);
                        ps.Play(true);
                    }
                    catch { }
                }
            }
            catch (Exception e) { RLog.Warning($"{Tag} lighting the flare failed: {e.Message}"); }
        }

        /// <summary>Model swap (host only, optional), locator icon, network attach, player look.</summary>
        private static void SetupSoldier(VailActor actor, SoldierKind kind, bool showOnMap = true)
        {
            bool isPlayerRobby = actor.TypeId == PlayerRobbyType;

            // The custom model only fits Kelvin's rig. On PlayerRobby the player body stays bound to the
            // original rig (T-pose when that rig is disabled) and the animator controller does not match,
            // so PlayerRobby always keeps the game's own model – the host then sees the same as everyone else.
            if (Config.HostCustomModel.Value && !isPlayerRobby && !IsDedicated && CharacterBundle.Bundle)
            {
                GameObject model = kind == SoldierKind.Jianyu
                    ? CharacterBundle.InstantiateJianyu()
                    : CharacterBundle.InstantiateTacticalsoldier();
                InitChar(model, actor);
            }

            SetLocatorIcons(actor);
            SetLocatorVisible(actor, showOnMap);
            if (IsDedicated) ServerPatches.Register(actor);
            EnsureNetworked(actor);
            SoldierRegistry.Add(actor, kind);

            if (isPlayerRobby && Config.RandomizeLook.Value)
            {
                ApplyLookDelayed(actor, Rng.Next(RaceCount)).RunCoro();
            }
        }

        private static VailActor SpawnCompanion(Vector3 position, Quaternion rotation)
        {
            VailActorTypeId type = Config.UsePlayerModel.Value ? PlayerRobbyType : RobbyType;
            VailActor prefab = ActorTools.GetPrefab(type);
            if (!prefab && type != RobbyType)
            {
                RLog.Warning($"{Tag} PlayerRobby prefab not available, falling back to Kelvin");
                prefab = ActorTools.GetPrefab(RobbyType);
            }
            VailActor actor = prefab.gameObject.InstantiateAndGet<VailActor>(false);
            actor.SetPositionAndRotation(position, rotation, false);
            return actor;
        }

        private static bool TryGetCompanion(int uniqueId, out VailActor result)
        {
            foreach (VailActorTypeId type in new[] { PlayerRobbyType, RobbyType })
            {
                foreach (VailActor actor in ActorTools.GetActors(type))
                {
                    if (actor.UniqueId == uniqueId)
                    {
                        result = actor;
                        return true;
                    }
                }
            }
            result = null;
            return false;
        }

        private static void RemoveStaticDyingSoldiers()
        {
            foreach (DyingTacticoolController dying in Resources.FindObjectsOfTypeAll<DyingTacticoolController>())
            {
                dying.gameObject.TryDestroy();
            }
            RemoveStoryProps();
        }

        private static void RemoveStoryProps()
        {
            Scene scene = SceneManager.GetSceneByName("SonsStorySpots");
            if (!scene.IsValid()) return;
            foreach (GameObject go in scene.GetRootGameObjects())
            {
                string n = go.name;
                if (n == "CampingCot (3)" || n.StartsWith("TacticoolCaucasianPoserRig"))
                {
                    go.TryDestroy();
                }
            }
        }

        /// <summary>Shows or hides the map marker. Soldiers that are still lying injured must not be on the map yet.</summary>
        internal static void SetLocatorVisible(VailActor actor, bool visible)
        {
            try
            {
                if (!actor) return;
                GPSLocator locator = actor.GetComponentInChildren<GPSLocator>(true);
                if (!locator) return;
                try { locator.SetActive(visible); } catch { }
                try { locator.Enable(visible); } catch { }
            }
            catch (Exception e)
            {
                RLog.Warning($"{Tag} switching the map icon failed: {e.Message}");
            }
        }

        private static void SetLocatorIcons(VailActor actor)
        {
            try
            {
                if (!CharacterBundle.Bundle) return;
                GPSLocator locator = actor.GetComponentInChildren<GPSLocator>(true);
                if (!locator) return;
                locator._locatorIcons = CharacterBundle.Soldierlocatoricons;
                locator.TrySetIconId(0);
            }
            catch (Exception e)
            {
                RLog.Warning($"{Tag} Locator icon failed: {e.Message}");
            }
        }

        // ---------------------------------------------------------- networking

        internal static BoltEntity GetEntity(VailActor actor)
        {
            if (!actor) return null;
            BoltEntity be = actor.GetComponent<BoltEntity>();
            if (!be) be = actor.GetComponentInChildren<BoltEntity>(true);
            if (!be) be = actor.GetComponentInParent<BoltEntity>();
            return be;
        }

        private static void EnsureNetworked(VailActor actor)
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return;
            try
            {
                BoltEntity be = GetEntity(actor);
                RLog.Msg($"{Tag} [MP] {actor.name}: BoltEntity={(be ? be.name : "none")} attached={(be ? be.isAttached : false)} active={actor.gameObject.activeInHierarchy}");
                if (!be || be.isAttached) return;

                actor.AttachIfNeeded();
                if (!be.isAttached)
                {
                    RLog.Msg($"{Tag} [MP] AttachIfNeeded did not attach, using BoltNetwork.Attach");
                    BoltNetwork.Attach(be.gameObject);
                }
                RLog.Msg(Color.LightGreen, $"{Tag} [MP] {actor.name} attached={be.isAttached} id={(be.isAttached ? be.networkId.PackedValue.ToString() : "-")}");
            }
            catch (Exception e)
            {
                RLog.Error($"{Tag} [MP] Attaching {actor.name} failed: {e}");
            }
        }

        // ------------------------------------------------------------ player look

        private static IEnumerator ApplyLookDelayed(VailActor actor, int race)
        {
            float until = Time.realtimeSinceStartup + 0.5f;
            while (Time.realtimeSinceStartup < until) yield return null;
            ApplyLook(actor, race);
        }

        internal static void ApplyLook(VailActor actor, int race)
        {
            if (!actor) return;
            try
            {
                Robby robby = actor.GetComponentInChildren<Robby>(true);
                if (!robby)
                {
                    RLog.Warning($"{Tag} {actor.name} has no Robby component, look not applied");
                    return;
                }

                var clothing = new Il2CppSystem.Collections.Generic.List<int>();
                PlayerClothingSystem playerClothing = actor.GetComponentInChildren<PlayerClothingSystem>(true);
                if ((!playerClothing || playerClothing._defaultClothing == null || playerClothing._defaultClothing.Count == 0) && !IsDedicated && LocalPlayer.GameObject)
                {
                    playerClothing = LocalPlayer.GameObject.GetComponentInChildren<PlayerClothingSystem>(true);
                }
                if (playerClothing && playerClothing._defaultClothing != null)
                {
                    foreach (var piece in playerClothing._defaultClothing)
                    {
                        if (piece) clothing.Add(piece.ItemId);
                    }
                }

                var armour = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(Math.Max(0, PlayerArmourSystem.MaxWearableSlots));
                robby.SetPlayerRaceClothingAndArmour(race, clothing, armour);
                RLog.Msg($"{Tag} Look applied to {actor.name}: race={race} clothing={clothing.Count} armourSlots={armour.Length}");
            }
            catch (Exception e)
            {
                RLog.Error($"{Tag} Applying look to {actor.name} failed: {e}");
            }
        }

        // ------------------------------------------------------------- commands

        [DebugCommand("adddebugsoldier")]
        private void AddDebugSoldier()
        {
            if (NetUtils.IsClient)
            {
                RLog.Msg($"{Tag} adddebugsoldier only works for the host");
                return;
            }
            SpawnExtraSoldier(SonsTools.GetPositionInFrontOfPlayer(2f, 2f), Quaternion.identity);
        }

        /// <summary>Sends a chat line only to the player with this steam id.</summary>
        private static void Reply(ulong steamId, string message)
        {
            try
            {
                MultiplayerUtilities.GetConnectionAndEntity(steamId, out BoltConnection conn, out BoltEntity entity);
                if (!entity) return;
                NetUtils.SendChatMessage(entity.networkId, "[Tacticool Marines] " + message, "orange", conn);
            }
            catch (Exception e)
            {
                RLog.Warning($"{Tag} Chat reply failed: {e.Message}");
            }
        }

        /// <summary>Short status for the chat: counts and the nearest soldier.</summary>
        private static void ReplyStatus(ulong steamId)
        {
            BoltEntity player = MultiplayerUtilities.GetEntityFromSteamId(steamId);
            Vector3 pp = player ? player.transform.position : Vector3.zero;
            int total = 0, injured = 0, up = 0, networked = 0;
            string nearest = "-";
            float best = float.MaxValue;
            foreach (var (actor, _) in SoldierRegistry.All())
            {
                if (!actor) continue;
                total++;
                Robby rb = actor.GetComponentInChildren<Robby>(true);
                if (rb && rb._injuredState.ToString() == "None") up++; else injured++;
                BoltEntity be = GetEntity(actor);
                if (be && be.isAttached) networked++;
                if (player)
                {
                    float d = Vector3.Distance(pp, actor.transform.position);
                    if (d < best) { best = d; nearest = $"{Mathf.RoundToInt(d)} m"; }
                }
            }
            Reply(steamId, $"{total} soldiers ({injured} injured, {up} on their feet), {networked} networked. Nearest: {nearest}");
        }

        /// <summary>Server chat command /tmspawn – spawns a standing soldier 2 m in front of the player who typed it.</summary>
        private static ServerCommands.EExecutionResult SpawnForPlayerCommand(ServerCommands.CommandArgs args)
        {
            try
            {
                BoltEntity player = MultiplayerUtilities.GetEntityFromSteamId(args.SteamId);
                if (!player)
                {
                    RLog.Warning($"{Tag} /tmspawn: no player entity for {args.SteamId}");
                    return ServerCommands.EExecutionResult.CommandFailed;
                }
                Transform tr = player.transform;
                Vector3 pos = tr.position + tr.forward * 2f + Vector3.up * 0.5f;
                SpawnExtraSoldier(pos, Quaternion.LookRotation(-tr.forward));
                RLog.Msg($"{Tag} /tmspawn: soldier spawned for {args.SteamId} at {pos}");
                Reply(args.SteamId, "Soldier spawned");
                return ServerCommands.EExecutionResult.Success;
            }
            catch (Exception e)
            {
                RLog.Error($"{Tag} /tmspawn failed: {e}");
                return ServerCommands.EExecutionResult.CommandFailed;
            }
        }

        /// <summary>/tmbuild, /tmmaintain, /tmclear, /tmget, /tmfollow, /tmstay - one order for all soldiers.</summary>
        private static ServerCommands.EExecutionResult AllOrderCommand(ServerCommands.CommandArgs args, Robby.OrderType type, string label)
        {
            try
            {
                BoltEntity player = MultiplayerUtilities.GetEntityFromSteamId(args.SteamId);
                if (!player) { Reply(args.SteamId, "Your player was not found"); return ServerCommands.EExecutionResult.CommandFailed; }

                bool needsChoice = type == Robby.OrderType.Build || type == Robby.OrderType.MaintainBase ||
                                   type == Robby.OrderType.ClearArea || type == Robby.OrderType.Get || type == Robby.OrderType.StayHere;
                int sub = 0, sub2 = 0;
                string name = label;
                if (needsChoice)
                {
                    var choices = Orders.ListNames(type);
                    string arg = args.HasArgs ? args.Args[0] : null;
                    if (arg == null && choices.Count > 1)
                    {
                        Reply(args.SteamId, $"{label}: please choose -> {string.Join(", ", choices)}");
                        return ServerCommands.EExecutionResult.Success;
                    }
                    sub = Orders.Resolve(type, arg, out name);
                    if (sub < 0)
                    {
                        Reply(args.SteamId, $"{label}: '{arg}' not found. Possible: {string.Join(", ", choices)}");
                        return ServerCommands.EExecutionResult.Success;
                    }
                    // Get-orders need a drop location; 3 = fill holders, otherwise the second argument
                    if (type == Robby.OrderType.Get) sub2 = args.Args.Length > 1 && int.TryParse(args.Args[1], out int d) ? d : 3;
                }

                int given = Orders.GiveToAll(type, sub, sub2, player.transform, out int skipped);
                Reply(args.SteamId, $"{label} ({name}): {given} soldier(s) sent, {skipped} skipped (injured/dead)");
                return ServerCommands.EExecutionResult.Success;
            }
            catch (Exception e)
            {
                RLog.Error($"{Tag} {label} command failed: {e}");
                return ServerCommands.EExecutionResult.CommandFailed;
            }
        }

        internal static bool IsRevived(string soldierName) =>
            GameState.GetOrCreate(soldierName + "_revived", false, true).SaveValueBool;

        internal static void SetRange(float range, float clear, float holder)
        {
            Config.OrderRangeMultiplier.Value = Math.Clamp(range, 0.25f, 10f);
            Config.ClearRadiusMultiplier.Value = Math.Clamp(clear, 0.25f, 5f);
            Config.HolderRangeMultiplier.Value = Math.Clamp(holder, 0.25f, 10f);
            try { Config.Category.SaveToFile(); } catch (Exception e) { RLog.Warning($"{Tag} Saving config failed: {e.Message}"); }
            OrderRange.Apply(Config.OrderRangeMultiplier.Value, Config.ClearRadiusMultiplier.Value);
            NoBreaks.ApplyOrderData();
        }

        /// <summary>tmrange [range] [clear] – console version of the range setting.</summary>
        [DebugCommand("tmrange")]
        private static void RangeCommand(string args)
        {
            var parts = (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r))
            {
                RLog.Msg($"{Tag} range x{Config.OrderRangeMultiplier.Value}, clear radius x{Config.ClearRadiusMultiplier.Value}, holders x{Config.HolderRangeMultiplier.Value}. Usage: tmrange <range> [clear] [holders]");
                return;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var fs = System.Globalization.NumberStyles.Float;
            float c = parts.Length > 1 && float.TryParse(parts[1], fs, inv, out float cc) ? cc : Config.ClearRadiusMultiplier.Value;
            float hm = parts.Length > 2 && float.TryParse(parts[2], fs, inv, out float hh) ? hh : Config.HolderRangeMultiplier.Value;
            SetRange(r, c, hm);
        }

        /// <summary>tminfo – dump prefab and soldier state to the log.</summary>
        [DebugCommand("tminfo")]
        private static void InfoCommand()
        {
            foreach (VailActorTypeId type in new[] { RobbyType, PlayerRobbyType })
            {
                VailActor prefab = ActorTools.GetPrefab(type);
                if (!prefab)
                {
                    RLog.Msg($"{Tag} prefab {type}: missing");
                    continue;
                }
                RLog.Msg($"{Tag} prefab {type}: {prefab.name} bolt={(bool)prefab.GetComponentInChildren<BoltEntity>(true)} robby={(bool)prefab.GetComponentInChildren<Robby>(true)} " +
                         $"clothingSys={(bool)prefab.GetComponentInChildren<PlayerClothingSystem>(true)} armourSys={(bool)prefab.GetComponentInChildren<PlayerArmourSystem>(true)} " +
                         $"raceSys={(bool)prefab.GetComponentInChildren<Sons.Wearable.Race.PlayerRaceSystem>(true)}");
            }
            foreach (var (actor, kind) in SoldierRegistry.All())
            {
                if (!actor) continue;
                BoltEntity be = GetEntity(actor);
                Robby rb = actor.GetComponentInChildren<Robby>(true);
                RLog.Msg($"{Tag} soldier {actor.name} kind={kind} type={actor.TypeId} uid={actor.UniqueId} active={actor.gameObject.activeInHierarchy} injured={(rb ? rb._injuredState.ToString() : "?")} dead={actor.IsDead()} attached={(be ? be.isAttached : false)} " +
                         $"id={(be && be.isAttached ? be.networkId.PackedValue.ToString() : "-")} pos={actor.transform.position}");
                if (rb && rb._injuredState.ToString() == "None")
                    RLog.Msg($"{Tag}   {actor.name}: {NoBreaks.OrderLine(actor)} {NoBreaks.StatLine(actor)}");
            }
            try
            {
                foreach (var k in ActorTools.GetActors(RobbyType))
                {
                    if (!k || k.name.StartsWith("LivingSoldier")) continue;
                    RLog.Msg($"{Tag} kelvin {k.name} active={k.gameObject.activeInHierarchy} dead={k.IsDead()} pos={k.transform.position} {NoBreaks.OrderLine(k)} {NoBreaks.StatLine(k)}");
                }
            }
            catch (Exception e) { RLog.Warning($"{Tag} kelvin info: {e.Message}"); }
        }

        /// <summary>tmcombat status|on|off|debug|set - combat settings from the console (F1).</summary>
        [DebugCommand("tmcombat")]
        private static void CombatCommand(string args)
        {
            CombatInspect.Run(args);
        }

        /// <summary>tmlook [0-7] – re-apply a player look (race index, random if empty) to all soldiers.</summary>
        [DebugCommand("tmlook")]
        private static void LookCommand(string args)
        {
            bool fixedRace = int.TryParse(args, out int race);
            foreach (var (actor, _) in SoldierRegistry.All())
            {
                if (actor) ApplyLook(actor, fixedRace ? race : Rng.Next(RaceCount));
            }
        }

        // ------------------------------------------------------------- model swap

        /// <summary>Swaps the visual model. Validates first so a failure leaves the actor untouched.</summary>
        internal static bool InitChar(GameObject customChar, VailActor actor)
        {
            try
            {
                Animator oldAnimator = actor._animator;
                var inventory = actor._inventoryManager;
                Robby robby = actor.GetComponentInChildren<Robby>();
                var oldEvents = oldAnimator ? oldAnimator.GetComponent<Ashkatchap.AnimatorEvents.AnimatorEvent>() : null;
                Transform tr = customChar.transform;
                Transform pickup = inventory && inventory._pickupParent ? TransformDeepChildExtension.FindDeepChild(tr, inventory._pickupParent.name) : null;

                if (!oldAnimator || !inventory || !robby || !oldEvents || !pickup || actor._lookAt == null)
                {
                    RLog.Warning($"{Tag} {actor.name}: rig not compatible with custom model (animator={(bool)oldAnimator} inventory={(bool)inventory} robby={(bool)robby} events={(bool)oldEvents} pickup={(bool)pickup}), keeping game model");
                    Object.Destroy(customChar);
                    return false;
                }

                SkinInitializer.InitializeSkins(customChar);
                tr.SetParent(oldAnimator.transform.parent, false);

                Animator newAnimator = customChar.GetComponent<Animator>();
                newAnimator.runtimeAnimatorController = oldAnimator.runtimeAnimatorController;
                actor._animator = newAnimator;
                oldAnimator.gameObject.SetActive(false);

                var newEvents = customChar.GetComponent<Ashkatchap.AnimatorEvents.AnimatorEvent>();
                newEvents.events = oldEvents.events;
                newEvents.eventsById = oldEvents.eventsById;
                newEvents._disableMissingEventErrors = false;
                customChar.GetComponent<FootstepAnimEvents>()._actorSource = actor;
                actor._animEvents = customChar.GetComponent<VailActorAnimEvents>();

                var lookAt = customChar.GetComponent<RootMotion.FinalIK.LookAtIK>();
                actor._lookAt._lookAtIk = lookAt;
                lookAt.solver.target = actor._lookAt._lookAtIkTarget;

                inventory._animator = newAnimator;
                inventory._pickupParent = pickup;
                inventory._carryAttachments = customChar.GetComponentsInChildren<Sons.Ai.Vail.Inventory.CarryAttachments>(true).ToIl2CppList();

                robby._robbyAnimator = newAnimator;
                robby._robbyRightHand = pickup;

                actor._isInitialized = false;
                actor.Initialize();

                if (customChar.name.StartsWith("TacticalSoldier"))
                {
                    var look = SoldierLook.For(actor);
                    SoldierLook.Apply(tr, look);
                    RLog.Msg($"{Tag} {actor.name} look: {look}");
                }
                return true;
            }
            catch (Exception e)
            {
                RLog.Error($"{Tag} Model swap on {actor.name} failed: {e}");
                return false;
            }
        }
    }

    internal static class SoldierSpots
    {
        internal class Spot
        {
            public string Name;
            public Vector3 Position;
            public Vector3 LastPosition;
            public VailActor Actor;
            public SoldierKind Kind;
            public bool Extra;
            public bool Dead;
        }

        private static readonly Dictionary<string, Spot> Spots = new();

        public static void Clear() => Spots.Clear();

        public static bool Has(string name) => Spots.ContainsKey(name);

        public static Spot Set(string name, Vector3 position, VailActor actor, SoldierKind kind, bool extra = false)
        {
            var spot = new Spot { Name = name, Position = position, LastPosition = actor ? actor.transform.position : position, Actor = actor, Kind = kind, Extra = extra };
            Spots[name] = spot;
            return spot;
        }

        public static List<Spot> All() => new(Spots.Values);
    }

    internal static class SoldierRegistry
    {
        private static readonly List<(VailActor actor, SoldierKind kind)> Entries = new();
        private static readonly HashSet<IntPtr> Pointers = new();

        /// <summary>Fast check for hot paths (damage patches run for every actor).</summary>
        public static bool IsSoldier(VailActor a) => a != null && Pointers.Contains(a.Pointer);

        public static void Clear()
        {
            Entries.Clear();
            Pointers.Clear();
            SoldierSpots.Clear();
            DeadCleanup.Clear();
            ServerPatches.Clear();
        }

        public static void Add(VailActor actor, SoldierKind kind)
        {
            Entries.Add((actor, kind));
            if (actor) Pointers.Add(actor.Pointer);
        }

        public static List<(VailActor actor, SoldierKind kind)> All()
        {
            Entries.RemoveAll(e => !e.actor);
            return new List<(VailActor, SoldierKind)>(Entries);
        }
    }
}
