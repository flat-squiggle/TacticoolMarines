using System;
using System.Collections;
using System.Reflection;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using Sons.Gameplay.GPS;
using Sons.Save;
using GameState = Sons.Save.GameState;
using SonsSdk;
using SonsSdk.Attributes;
using UnityEngine;
using Color = System.Drawing.Color;
using Object = UnityEngine.Object;

namespace CharacterReplacement
{
    public static class Config
    {
        public static ConfigCategory Category { get; private set; }
        [SettingsUiHeader("Soldiers")]
        public static ConfigEntry<bool> AddJianyu { get; private set; }
        public static ConfigEntry<bool> UsePlayerModel { get; private set; }
        public static ConfigEntry<bool> RandomizeLook { get; private set; }
        public static ConfigEntry<string> LookScheme { get; private set; }
        public static ConfigEntry<string> LookGear { get; private set; }
        public static ConfigEntry<bool> SolderGlasses { get; private set; }
        public static ConfigEntry<bool> RifleFlashlight { get; private set; }
        public static ConfigEntry<bool> CaveFollow { get; private set; }
        public static ConfigEntry<bool> SoldierVault { get; private set; }

        [SettingsUiHeader("Health")]
        public static ConfigEntry<bool> SoldierDamage { get; private set; }
        public static ConfigEntry<bool> SoldierImmortal { get; private set; }
        public static ConfigEntry<float> SoldierDownedTime { get; private set; }
        public static ConfigEntry<float> SoldierGetUpHealth { get; private set; }
        public static ConfigEntry<bool> RemoveDeadSoldiers { get; private set; }
        public static ConfigEntry<float> RemoveDeadAfterSeconds { get; private set; }

        [SettingsUiHeader("Combat")]
        public static ConfigEntry<bool> CombatEnabled { get; private set; }
        public static ConfigEntry<float> CombatRange { get; private set; }
        public static ConfigEntry<float> CombatDamageScale { get; private set; }
        public static ConfigEntry<float> CombatAccuracy { get; private set; }
        public static ConfigEntry<float> CombatHeadshotChance { get; private set; }
        public static ConfigEntry<float> CombatFireInterval { get; private set; }
        public static ConfigEntry<int> CombatBurst { get; private set; }
        public static ConfigEntry<int> CombatMagazine { get; private set; }
        public static ConfigEntry<float> CombatReloadTime { get; private set; }
        public static ConfigEntry<float> CombatReaction { get; private set; }
        public static ConfigEntry<float> CombatAwareness { get; private set; }
        public static ConfigEntry<float> CombatHearing { get; private set; }
        public static ConfigEntry<bool> CombatMove { get; private set; }
        public static ConfigEntry<bool> CombatCover { get; private set; }
        public static ConfigEntry<float> CombatMinDistance { get; private set; }
        public static ConfigEntry<float> CombatRetreatHealth { get; private set; }
        public static ConfigEntry<bool> CombatChase { get; private set; }
        public static ConfigEntry<float> CombatLeash { get; private set; }
        public static ConfigEntry<float> CombatFollowLeash { get; private set; }
        public static ConfigEntry<float> CombatSquadRange { get; private set; }
        public static ConfigEntry<string> CombatGunIdle { get; private set; }

        [SettingsUiHeader("Work")]
        public static ConfigEntry<bool> NoBreaks { get; private set; }
        public static ConfigEntry<float> OrderRangeMultiplier { get; private set; }
        public static ConfigEntry<float> ClearRadiusMultiplier { get; private set; }
        public static ConfigEntry<float> HolderRangeMultiplier { get; private set; }
        public static ConfigEntry<bool> AutoJobsEnabled { get; private set; }
        public static ConfigEntry<string> AutoJobs { get; private set; }
        public static ConfigEntry<string> AutoJobsFor { get; private set; }
        public static ConfigEntry<float> AutoJobsIdleSeconds { get; private set; }
        public static ConfigEntry<float> AutoJobsHolderRadius { get; private set; }
        public static ConfigEntry<float> AutoJobsPlayerRadius { get; private set; }

        [SettingsUiHeader("Debug")]
        public static ConfigEntry<bool> CombatDebug { get; private set; }

        // Not in the in-game settings: fixes that are always wanted and values tuned during development.
        // They stay in CharacterReplacement.cfg for troubleshooting.
        [SettingsUiIgnore]
        public static ConfigEntry<bool> HostCustomModel { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatAnim { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<float> CombatDamage { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<bool> CombatLineOfSight { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatTargets { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatGunModel { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatGunHand { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatGunBack { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<float> CombatGunScale { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatGunFit { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatGunOffset { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<bool> CombatGunIk { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<bool> CombatHandSync { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatMoveMode { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatHitMode { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatFxEvent { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<string> CombatRunAnim { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<bool> CombatFootIk { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<bool> CombatNoCower { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<bool> CombatResumeOrders { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<bool> CombatFaceLock { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<bool> ModelFixHand { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<bool> AutoRemoveDuplicates { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<float> ModelVestInflate { get; private set; }
        [SettingsUiIgnore]
        public static ConfigEntry<float> ModelVestDepth { get; private set; }

        public static void Init()
        {
            // Settings file TacticoolMarines.cfg, section [TacticoolMarines] (the settings panel shows the section
            // identifier as its title). Until 1.40 the file was CharacterReplacement.cfg (the original mod's name):
            // its values are taken over once.
            try
            {
                string file = ConfigSystem.GetFilePath("TacticoolMarines.cfg");
                string old = ConfigSystem.GetFilePath("CharacterReplacement.cfg");
                if (!System.IO.File.Exists(file) && System.IO.File.Exists(old))
                {
                    string text = System.IO.File.ReadAllText(old)
                        .Replace("[CharacterReplacement]", "[TacticoolMarines]").Replace("[LivingSoldiers]", "[TacticoolMarines]");
                    System.IO.File.WriteAllText(file, text);
                    RLog.Msg("Tacticool Marines: settings taken over from CharacterReplacement.cfg");
                }
            }
            catch (Exception e) { RLog.Warning("Tacticool Marines: taking over the old settings file: " + e.Message); }
            Category = ConfigSystem.CreateFileCategory("TacticoolMarines", "Tacticool Marines", "TacticoolMarines.cfg");
            AddJianyu = Category.CreateEntry("add_jianyu", true, "Add Jianyu", "If Jianyu should be part of the squad");
            SolderGlasses = Category.CreateEntry("soldier_glasses", true, "Soldier Glasses", "If soldiers should wear sun glasses");
            UsePlayerModel = Category.CreateEntry("use_player_model", true, "Player look for co-op", "Spawn soldiers as player-looking companions (PlayerRobby) so players without the mod see soldiers instead of Kelvin clones");
            RandomizeLook = Category.CreateEntry("randomize_look", true, "Random faces", "Give every soldier a random player head/skin (only with player look)");
            OrderRangeMultiplier = Category.CreateEntry("order_range_multiplier", 2f, "Order search range", "Multiplier for how far Kelvin and the soldiers search for things to collect (logs, sticks, stones ...). 1 = game default");
            ClearRadiusMultiplier = Category.CreateEntry("clear_radius_multiplier", 1f, "Clear area radius", "Multiplier for the radius of 'clear area' orders. 1 = game default");
            HolderRangeMultiplier = Category.CreateEntry("holder_range_multiplier", 2f, "Structure search range", "Multiplier for how far Kelvin and the soldiers look for structures: holders/containers, log sleds, build sites, structures to repair/maintain. 1 = game default");
            AutoJobsEnabled = Category.CreateEntry("auto_jobs_enabled", false, "Auto jobs enabled", "Let idle companions collect items into holders on their own (off by default)");
            AutoJobs = Category.CreateEntry("auto_jobs", "Log,Stick,Rock", "Auto jobs", "Items idle companions collect on their own into holders (comma separated: Log, Stick, Rock, Stone, Berries, Fish ...). Empty = off");
            AutoJobsFor = Category.CreateEntry("auto_jobs_for", "soldiers", "Auto jobs for", "Who works on their own: soldiers, kelvin, all, none");
            AutoJobsIdleSeconds = Category.CreateEntry("auto_jobs_idle_seconds", 60f, "Idle time before auto job", "Seconds a companion must be idle before starting a job on its own");
            AutoJobsHolderRadius = Category.CreateEntry("auto_jobs_holder_radius", 40f, "Auto jobs: holder radius", "Only start an auto job if a matching holder is within this many meters of the companion");
            NoBreaks = Category.CreateEntry("no_breaks", true, "Keep working (no breaks)", "Kelvin and the soldiers keep working until there is nothing left to do instead of taking a break after a few minutes. Player orders (follow, stay, take a break) are still obeyed");
            RemoveDeadSoldiers = Category.CreateEntry("remove_dead_soldiers", true, "Remove dead soldiers", "Remove the bodies of killed soldiers (saves performance). Never touches the story Kelvin/Virginia");
            RemoveDeadAfterSeconds = Category.CreateEntry("remove_dead_after_seconds", 120f, "Remove dead after (s)", "Seconds a dead soldier stays before the body is removed (time to pick up dropped items)");
            AutoJobsPlayerRadius = Category.CreateEntry("auto_jobs_player_radius", 150f, "Auto jobs: player radius", "Only start an auto job if a player is within this many meters (keeps them at the base)");
            HostCustomModel = Category.CreateEntry("host_custom_model", true, "Custom model for host", "Only when player look is off: the host sees the mod's own soldier model on the Kelvin clones. Players without the mod always see the game's model.");
            CombatEnabled = Category.CreateEntry("combat_enabled", true, "Soldiers fight", "Soldiers shoot at hostile cannibals and mutants nearby instead of running away");
            CombatRange = Category.CreateEntry("combat_range", 40f, "Combat range", "How far (m) soldiers engage enemies");
            CombatDamage = Category.CreateEntry("combat_damage", 0f, "Damage per hit", "0 = like the player's rifle (its ammo damage times the body part hit: torso x2, head x200, limbs x0.8 on regular cannibals); any other value = that flat damage per hit");
            CombatDamageScale = Category.CreateEntry("combat_damage_scale", 1f, "Damage scale", "Multiplies the soldiers' damage (1 = same as the player's rifle)");
            if (Migrate("migrated_1_31") && Math.Abs(CombatDamage.Value - 25f) < 0.01f) CombatDamage.Value = 0f; // old flat default -> player rifle
            CombatFireInterval = Category.CreateEntry("combat_fire_interval", 1.5f, "Seconds between shots", "Time between two shots of one soldier (scales the pauses within and between bursts)");
            if (Migrate("migrated_1_34_fire") && Math.Abs(CombatFireInterval.Value - 1.2f) < 0.001f) CombatFireInterval.Value = 1.5f; // old default, a bit slower now
            CombatAccuracy = Category.CreateEntry("combat_accuracy", 0.75f, "Hit chance", "Chance (0-1) that a shot hits");
            CombatLineOfSight = Category.CreateEntry("combat_line_of_sight", true, "Need line of sight", "Only shoot at enemies the soldier can see");
            CombatTargets = Category.CreateEntry("combat_targets", "auto", "Enemy types", "Actor type ids (or names) soldiers shoot at. 'auto' = every type the game classes as cannibal or creepy (mutant), minus Kelvin/Virginia/Timmy. Can be mixed: 'auto,-18' or 'auto,Moose'");
            // The old default was a hand-written guess of the same list; move it to the game's own classification.
            if (CombatTargets.Value == "11-16,18-21,23,25-30,38-56,58,59") CombatTargets.Value = "auto";
            CombatAnim = Category.CreateEntry("combat_anim", "rifle", "Combat animation", "Gun animation the soldiers use when they aim (rifle, shotgun, pistol, off). Taken from the multiplayer player animations");
            CombatGunModel = Category.CreateEntry("combat_gun_model", "auto", "Gun model", "auto = the rifle exactly as the game holds it in the player's hand socket (found in memory, see tmcombat gun native), with GetDownSonRifleRig fitted by hand as fallback. Or the name of a loaded model (tmcombat gun list). Empty = no gun");
            if (CombatGunModel.Value == "GetDownSonRifleRig") CombatGunModel.Value = "auto"; // 1.26: the game's own hold first
            CombatGunHand = Category.CreateEntry("combat_gun_hand", "0 0 0; 0 0 0", "Gun in hand", "Not used since 1.24 (the gun is fitted to shoulder and hands every frame; see combat_gun_offset)");
            // Until 1.21 this was a fixed rotation in the hand bone; the gun is now fitted between the hands.
            if (CombatGunHand.Value == "0 0 0; -45 0 -135") CombatGunHand.Value = "0 0 0; 0 0 0";
            CombatGunOffset = Category.CreateEntry("combat_gun_offset", "0 0 0; 0 0 0", "Gun offset", "Fine offset of the gun fitted in the hands, in the gun's own frame (x right, y up, z towards the muzzle): position (m) and rotation (degrees) 'x y z; x y z'. tmcombat gun nudge / turn change it live");
            CombatHandSync = Category.CreateEntry("combat_hand_sync", true, "Right hand like the player", "In combat clips his right hand is turned exactly like the player rig's (his model's hand sits under an extra bone, so the clips miss it). Needed for the game's own rifle hold");
            CombatGunIk = Category.CreateEntry("combat_gun_ik", true, "Hands on the gun (IK)", "Bend the arms so the hands hold the gun (right hand on the grip, left hand on the fore-end) and the barrel points at the target while aiming. Off = the gun is fitted to where the clip puts the hands");
            CombatGunFit = Category.CreateEntry("combat_gun_fit", "auto", "Gun axes", "Which axis of the gun model is the barrel (towards the muzzle) and which is up, plus where the grip sits along the gun (0 = stock end, 1 = muzzle): e.g. '+z +y 0.3'. auto = worked out on the first aim and saved. Change with tmcombat gun flip / upflip / grip");
            // 1.24: grip moved nearer the stock (0.30 put the stock behind the head)
            if (CombatGunFit.Value.EndsWith(" 0.30")) CombatGunFit.Value = CombatGunFit.Value.Substring(0, CombatGunFit.Value.Length - 5) + " 0.22";
            CombatGunBack = Category.CreateEntry("combat_gun_back", "0 0 0; 0 0 0", "Gun on back", "Fine offset of the gun slung across the back, in the soldier's frame (x right, y up, z forward): position (m) and rotation (degrees) 'x y z; x y z'");
            CombatGunScale = Category.CreateEntry("combat_gun_scale", 2.5f, "Gun size", "Size of the gun model");
            CombatGunIdle = Category.CreateEntry("combat_gun_idle", "back", "Gun when not aiming", "Where the gun is outside combat: back, hide or hand (his own animations do not hold a rifle, so in the hand it hangs at an odd angle)");
            if (CombatGunIdle.Value == "hand" && CombatGunModel.Value == "auto" && CombatHandSyncMigrated()) CombatGunIdle.Value = "back";
            CombatMove = Category.CreateEntry("combat_move", true, "Move in combat", "Soldiers reposition during a fight: back off from close enemies, find a spot with line of sight, take cover, advance on far targets");
            CombatMoveMode = Category.CreateEntry("combat_move_mode", "auto", "Combat movement method", "How a soldier is carried while he plays a run clip in combat. auto = measured on the first moves. move = the actor's own mover, pos = set position on the ground, root = the clip's root motion");
            if (CombatMoveMode.Value == "nav" || CombatMoveMode.Value == "anim" || CombatMoveMode.Value == "slide") CombatMoveMode.Value = "auto"; // values of 1.20.0
            CombatCover = Category.CreateEntry("combat_cover", true, "Use cover", "Prefer positions with a tree, rock or wall between the soldier and the enemy");
            CombatMinDistance = Category.CreateEntry("combat_min_distance", 6f, "Back off distance", "Soldiers back away from enemies closer than this (m)");
            CombatRetreatHealth = Category.CreateEntry("combat_retreat_health", 0.3f, "Retreat at health", "Below this share of health (0-1) a soldier falls back towards the player while still shooting. 0 = never");
            CombatReaction = Category.CreateEntry("combat_reaction", 0.6f, "Reaction time", "Average seconds between spotting an enemy and the first shot");
            CombatBurst = Category.CreateEntry("combat_burst", 2, "Shots per burst", "Most shots fired in one burst before a short pause");
            if (CombatBurst.Value == 3) CombatBurst.Value = 2; // old default, too trigger-happy
            CombatMagazine = Category.CreateEntry("combat_magazine", 10, "Magazine size", "Shots before reloading. 0 = never reload");
            CombatReloadTime = Category.CreateEntry("combat_reload_time", 2.5f, "Reload time", "Seconds a reload takes");
            CombatHitMode = Category.CreateEntry("combat_hit_mode", "impact", "Hit method", "impact = real bullet impact (hit reactions, blood, body part damage, the enemy turns on the shooter). direct = plain damage. impact falls back to direct by itself if it does not work");
            CombatHeadshotChance = Category.CreateEntry("combat_headshot_chance", 0.15f, "Headshot share", "Share of hits aimed at the head (0-1). Only with hit method impact");
            CombatFxEvent = Category.CreateEntry("combat_fx_event", "", "Gunshot sound", "FMOD event path of the gunshot. Empty = found automatically from the game's sound banks (tmcombat fx events)");
            CombatRunAnim = Category.CreateEntry("combat_run_anim", "player", "Running animation in combat", "player = the player's multiplayer run clips with foot placement; own = his own (Kelvin) locomotion (1.26 test: climbs over obstacles that are not there)");
            if (CombatRunAnim.Value == "own") CombatRunAnim.Value = "player"; // 1.26 default did not work out
            CombatFootIk = Category.CreateEntry("combat_foot_ik", true, "Foot placement", "In combat poses the hips are lowered and the legs bent so the feet stand on slopes instead of sinking in");
            CombatNoCower = Category.CreateEntry("combat_no_cower", true, "No cowering", "When his own AI wants to cower or back away (gunfire, explosions), he takes a ready stance with the rifle for a few seconds instead");
            CombatChase = Category.CreateEntry("combat_chase", true, "Chase enemies", "Soldiers run after enemies that flee or are too far for a good shot");
            CombatLeash = Category.CreateEntry("combat_leash", 40f, "Chase distance", "How far (m) from where the fight started a soldier chases before he lets the enemy go");
            CombatHearing = Category.CreateEntry("combat_hearing", 12f, "Hearing range", "Enemies closer than this (m) are noticed even without line of sight; the soldier turns to them and looks for a clear shot");
            CombatAwareness = Category.CreateEntry("combat_awareness", 1.5f, "Time to notice", "Seconds a soldier needs to notice an enemy in plain view 20 m away before he attacks (closer = faster, far or to the side = slower, an enemy attacking someone = much faster). 0 = at once (old behaviour)");
            CombatFollowLeash = Category.CreateEntry("combat_follow_leash", 18f, "Follow leash", "A soldier following a player stays within this distance (m) of him in a fight and does not go after enemies further away. 0 = off");
            CombatSquadRange = Category.CreateEntry("combat_squad_range", 20f, "Squad call range", "A soldier helps a fighting comrade only within this distance (m); he still has to see or hear the enemy himself to keep fighting");
            CombatResumeOrders = Category.CreateEntry("combat_resume_orders", true, "Resume orders after a fight", "After a fight the soldier goes back to following or to his work order");
            CombatFaceLock = Category.CreateEntry("combat_face_lock", true, "Keep face bones still", "Bones of the soldier's face that his own (Kelvin) animations would pull out of place are held in their resting pose");
            ModelFixHand = Category.CreateEntry("model_fix_hand", true, "Fix right hand", "The soldier model's right hand hangs below an extra twist bone no animation moves, so the wrist is twisted and no animation reaches the hand; this hangs it where the left one is");
            LookScheme = Category.CreateEntry("look_scheme", "random", "Uniform colours", "random = each soldier his own colour scheme (kept across saves), off = all standard, or one for all: standard, woodland, desert, black, urban");
            LookGear = Category.CreateEntry("look_gear", "random", "Gear variety", "random = helmet, sunglasses, balaclava and body armour differ per soldier; off = all the same (sunglasses from soldier_glasses)");
            SoldierDamage = Category.CreateEntry("soldier_damage", true, "Soldiers take damage", "Soldiers are hurt by enemies (with hit reactions). Also with the ImmortalCompanions mod, which then only protects the real Kelvin and Virginia");
            SoldierImmortal = Category.CreateEntry("soldier_immortal", true, "Soldiers cannot die", "Soldiers still take damage, but at no health they are downed instead of killed and get up again later");
            SoldierDownedTime = Category.CreateEntry("soldier_downed_time", 30f, "Downed time", "Seconds a downed soldier lies injured before he gets up by himself (the player can help him up sooner)");
            SoldierGetUpHealth = Category.CreateEntry("soldier_getup_health", 0.35f, "Health after getting up", "Share of his health (0-1) a soldier has when he gets up after being downed");
            ModelVestInflate = Category.CreateEntry("model_vest_inflate2", 1.02f, "Vest size (sides)", "The body armour vest is drawn this much bigger around the middle of the torso, sideways and up/down (1 = off)");
            ModelVestDepth = Category.CreateEntry("model_vest_depth", 1.08f, "Vest size (front/back)", "The vest is drawn this much bigger front-to-back around the middle of the torso, so the jacket shows through neither its front nor its back");
            AutoRemoveDuplicates = Category.CreateEntry("auto_remove_duplicates", true, "Remove left-over Kelvins", "A minute after loading, Kelvin-type characters that are neither the story Kelvin nor one of the mod's soldiers are removed (leftovers of an old bug that respawned extra soldiers on every load)");
            RifleFlashlight = Category.CreateEntry("rifle_flashlight", false, "Rifle flashlight", "The game's flashlight attachment on the soldiers' rifles. It lights up in the dark (night, caves) while the rifle is in his hands, never on his back");
            SoldierVault = Category.CreateEntry("soldier_vault", true, "Jump over obstacles", "When the mod moves a soldier (fights, caves) and something knee-high is in the way with room above and floor beyond, he climbs onto or vaults over it with Kelvin's own jump animations");
            CaveFollow = Category.CreateEntry("cave_follow", true, "Follow into caves", "Soldiers following the player go into caves with him (same entrance animation), follow him inside and come out with him");
            CombatDebug = Category.CreateEntry("combat_debug", false, "Detailed combat log", "Write every combat decision (spot, move, cover, reload, hit) to the log - for bug reports");
            SetupUi();
        }

        /// <summary>
        /// How the entries show in the game's mod settings: choices as pickers (a string entry with options), every
        /// slider with a real range (RedLoader's default is 0-10), clearer names. Values that are not a listed
        /// choice (older versions, hand edits) are moved to the nearest one.
        /// </summary>
        private static void SetupUi()
        {
            void Pick(ConfigEntry<string> e, string fallback, params string[] options)
            {
                e.SetOptions(options);
                string v = (e.Value ?? "").Trim().ToLowerInvariant();
                e.Value = Array.IndexOf(options, v) >= 0 ? v : fallback;
            }
            void Name(ConfigEntry e, string name, string desc = null) { e.DisplayName = name; if (desc != null) e.Description = desc; }

            if ((LookScheme.Value ?? "").Trim().ToLowerInvariant() == "off") LookScheme.Value = "standard";
            Pick(LookScheme, "random", "random", "standard", "woodland", "desert", "black", "urban");
            Name(LookScheme, "Uniform colours", "random = every soldier his own colour scheme (kept across saves), or one scheme for all");
            if ((LookGear.Value ?? "").Trim().ToLowerInvariant() == "off") LookGear.Value = "same";
            Pick(LookGear, "random", "random", "same");
            Name(LookGear, "Gear variety", "random = helmet, sunglasses and body armour differ per soldier; same = all wear the full kit (sunglasses as set below)");
            Name(SolderGlasses, "Sunglasses", "Soldiers wear sunglasses (only when gear variety is 'same')");
            Name(AddJianyu, "Add Jianyu", "Jianyu joins the squad");
            if ((CombatGunIdle.Value ?? "").Trim().ToLowerInvariant() == "hand") CombatGunIdle.Value = "back";
            Pick(CombatGunIdle, "back", "back", "hide");
            Name(CombatGunIdle, "Rifle when not fighting", "back = slung across the back; hide = not shown");
            Pick(AutoJobsFor, "soldiers", "soldiers", "kelvin", "all", "none");

            SoldierDownedTime.SetRange(5f, 120f);
            SoldierGetUpHealth.SetRange(0.1f, 1f);
            RemoveDeadAfterSeconds.SetRange(10f, 600f);
            CombatRange.SetRange(10f, 100f);
            CombatDamageScale.SetRange(0.1f, 5f);
            CombatAccuracy.SetRange(0.1f, 1f);
            CombatHeadshotChance.SetRange(0f, 1f);
            CombatFireInterval.SetRange(0.3f, 5f);
            CombatBurst.SetRange(1f, 6f);
            CombatMagazine.SetRange(0f, 30f);
            CombatReloadTime.SetRange(0.5f, 6f);
            CombatReaction.SetRange(0f, 3f);
            CombatAwareness.SetRange(0f, 5f);
            CombatHearing.SetRange(0f, 40f);
            CombatMinDistance.SetRange(0f, 20f);
            CombatRetreatHealth.SetRange(0f, 1f);
            CombatLeash.SetRange(5f, 100f);
            CombatFollowLeash.SetRange(0f, 60f);
            CombatSquadRange.SetRange(0f, 60f);
            OrderRangeMultiplier.SetRange(0.5f, 5f);
            ClearRadiusMultiplier.SetRange(0.5f, 5f);
            HolderRangeMultiplier.SetRange(0.5f, 5f);
            AutoJobsIdleSeconds.SetRange(5f, 600f);
            AutoJobsHolderRadius.SetRange(5f, 200f);
            AutoJobsPlayerRadius.SetRange(10f, 500f);
            Name(CombatDamageScale, "Damage", "Multiplies the soldiers' damage. 1 = the same as the player's rifle");
            Name(CombatAccuracy, "Hit chance", "Chance (0.1-1) that a shot hits");
            Name(CombatHeadshotChance, "Headshot share", "Share (0-1) of hits aimed at the head");
            Name(CombatRetreatHealth, "Fall back at health", "Below this share of health (0-1) a soldier falls back towards the player while still shooting. 0 = never");
            Name(SoldierImmortal, "Soldiers cannot die", "At no health a soldier is downed instead of killed and gets up again later");
            Name(RemoveDeadSoldiers, "Remove dead soldiers", "Only when soldiers can die: remove their bodies after a while (saves performance). Never touches the story Kelvin/Virginia");
            Name(SoldierDownedTime, "Downed time (s)");
            Name(RemoveDeadAfterSeconds, "Remove dead after (s)");
            Name(CombatRange, "Combat range (m)");
            Name(CombatReloadTime, "Reload time (s)");
            Name(CombatReaction, "Reaction time (s)");
            Name(CombatAwareness, "Time to notice (s)");
            Name(CombatHearing, "Hearing range (m)");
            Name(CombatMinDistance, "Back off distance (m)");
            Name(CombatLeash, "Chase distance (m)");
            Name(CombatFollowLeash, "Follow leash (m)");
            Name(CombatSquadRange, "Squad call range (m)");
            Name(AutoJobsIdleSeconds, "Idle time before auto job (s)");
            Name(AutoJobsHolderRadius, "Auto jobs: holder radius (m)");
            Name(AutoJobsPlayerRadius, "Auto jobs: player radius (m)");
        }

        // one-time move of combat_gun_idle from hand to back with 1.28 (the native gun is only right in combat clips)
        private static bool CombatHandSyncMigrated()
        {
            var flag = Category.CreateEntry("migrated_1_28", false, "internal", "internal");
            if (flag.Value) return false;
            flag.Value = true;
            return true;
        }

        private static bool Migrate(string id)
        {
            var flag = Category.CreateEntry(id, false, "internal", "internal");
            if (flag.Value) return false;
            flag.Value = true;
            return true;
        }

        public static void OnSettingsUiClosed()
        {
        }
    }

    public static class SkinInitializer
    {
        private static readonly int SubsurfaceMaskMapId = Shader.PropertyToID("_SubsurfaceMaskMap");
        private static readonly int ThicknessMapId = Shader.PropertyToID("_ThicknessMap");

        public static void InitializeSkins(GameObject root)
        {
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                foreach (Material mat in renderer.sharedMaterials)
                {
                    if (mat.GetTexture(SubsurfaceMaskMapId) || mat.GetTexture(ThicknessMapId))
                    {
                        MaterialTools.SetDiffusionProfile(mat, MaterialTools.EDiffusionProfile.HumanSkinA);
                    }
                }
            }
        }
    }

    [RegisterTypeInIl2Cpp]
    public class SoldierController : MonoBehaviour
    {
        public static System.Collections.Generic.List<SoldierController> Soldiers = new();

        private VailActor _actor;
        private Robby _robby;
        private string _dyingSoldierName;
        private NamedIntData _isRevivedState;
        private System.Collections.Generic.List<GameObject> _injuredObjects;

        public bool IsRevived => _isRevivedState.SaveValueBool;

        private void Awake()
        {
            _actor = GetComponent<VailActor>();
            _robby = GetComponentInChildren<Robby>();
            InitializationCoro().RunCoro();
            Soldiers.Add(this);
            _actor.PreActionCallbacks += (Il2CppSystem.Action<VailActor, Group, IStimuli, Thought, Priority>)
                new Action<VailActor, Group, IStimuli, Thought, Priority>(PreAction);
        }

        private void PreAction(VailActor actor, Group group, IStimuli stimuli, Thought thought, Priority priority)
        {
            if (group._name != "Robby Recover Injured") return;
            RLog.Msg(Color.Orange, "Recovered " + _dyingSoldierName);
            _isRevivedState.SetValue(true);
            // Only now does he belong on the map.
            CharacterReplacement.SetLocatorVisible(_actor, true);
            if (_injuredObjects == null) return;
            foreach (GameObject go in _injuredObjects)
            {
                if (go) go.SetActive(false);
            }
        }

        public void Init(string soldierName, System.Collections.Generic.List<GameObject> injuredObjects = null)
        {
            if (!string.IsNullOrEmpty(_dyingSoldierName)) return;
            _dyingSoldierName = soldierName;
            _isRevivedState = GameState.GetOrCreate(_dyingSoldierName + "_revived", false, true);
            _injuredObjects = injuredObjects;
        }

        private void OnDestroy()
        {
            RLog.Msg($"{CharacterReplacement.Tag} Soldier object {_dyingSoldierName} destroyed (revived={(_isRevivedState != null && _isRevivedState.SaveValueBool)})");
            Soldiers.Remove(this);
        }

        private IEnumerator InitializationCoro()
        {
            float until = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < until) yield return null;
            bool revived = _isRevivedState.SaveValueBool;
            if (!revived)
            {
                _robby.SetInjuredState((Robby.InjuredState)1);
            }
            CharacterReplacement.SetLocatorVisible(_actor, revived);
            if (_injuredObjects == null) yield break;
            foreach (GameObject go in _injuredObjects)
            {
                if (!go) continue;
                bool shouldBeOn = !revived;
                if (go.activeSelf != shouldBeOn) go.SetActive(shouldBeOn);
                // A particle system that was switched off and on again does not necessarily start
                // running by itself - that is why the sound played but the red smoke stayed away.
                if (shouldBeOn) CharacterReplacement.LightFlare(go);
            }
            if (!revived) FlareKeepAlive().RunCoro();
            yield break;
        }

        /// <summary>
        /// Keeps the flare burning. Something on the crash site used to drive it, and that object is gone
        /// once the mod has taken the soldier over - without this the smoke dies after a few seconds.
        /// </summary>
        private IEnumerator FlareKeepAlive()
        {
            while (this && _injuredObjects != null && !_isRevivedState.SaveValueBool)
            {
                foreach (GameObject go in _injuredObjects)
                {
                    if (!go) continue;
                    if (!go.activeSelf) go.SetActive(true);
                    bool dead = true;
                    foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        if (ps.isPlaying) { dead = false; break; }
                    }
                    if (dead) CharacterReplacement.LightFlare(go);
                }
                float until = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < until) yield return null;
            }
        }
    }
}

namespace BundleData
{
    public static class CharacterBundle
    {
        private static GameObject _jianyu;
        private static GPSLocatorIcons _soldierlocatoricons;
        private static GameObject _tacticalsoldier;

        public static AssetBundle Bundle { get; private set; }

        public static GameObject Jianyu
        {
            get
            {
                if (!_jianyu)
                {
                    _jianyu = Bundle.LoadAsset("jianyu").Cast<GameObject>();
                    _jianyu.hideFlags = (HideFlags)61;
                }
                return _jianyu;
            }
        }

        public static GPSLocatorIcons Soldierlocatoricons
        {
            get
            {
                if (!_soldierlocatoricons)
                {
                    _soldierlocatoricons = Bundle.LoadAsset("soldierlocatoricons").Cast<GPSLocatorIcons>();
                    _soldierlocatoricons.hideFlags = (HideFlags)61;
                }
                return _soldierlocatoricons;
            }
        }

        public static GameObject Tacticalsoldier
        {
            get
            {
                if (!_tacticalsoldier)
                {
                    _tacticalsoldier = Bundle.LoadAsset("tacticalsoldier").Cast<GameObject>();
                    _tacticalsoldier.hideFlags = (HideFlags)61;
                }
                return _tacticalsoldier;
            }
        }

        public static void LoadFromFile(string path)
        {
            Bundle = AssetBundle.LoadFromFile(path);
        }

        public static GameObject InstantiateJianyu() => Object.Instantiate(Jianyu);
        public static GameObject InstantiateTacticalsoldier() => Object.Instantiate(Tacticalsoldier);
    }
}
