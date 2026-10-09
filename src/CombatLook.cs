using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using SonsSdk;
using TheForest.Utils;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Combat look experiment. The soldiers' own AI already points at enemies (group "Point At Enemy",
    /// animator bool "PointAtEnemies", clips RobbyPointIntro/Loop/Outro). This swaps those three clips for
    /// the multiplayer player's gun clips (the ones remote players are shown with), so pointing becomes
    /// raising and aiming a gun. Host-side only: other players still see the normal pointing.
    ///   tmcombat look rifle|shotgun|pistol   apply to all standing soldiers
    ///   tmcombat look off                    restore the original animations
    ///   tmcombat look custom <intro> <loop> <outro> <fire>  any clips from the player animator
    ///   tmcombat aim [seconds]               force the nearest soldier into the pose (with shots), logs what plays
    /// No damage yet - this only tests whether the clips fit the soldier body.
    /// </summary>
    internal static class CombatLook
    {
        private const string SourceController = "PlayerAControllerNetwork";
        private const string PointIntro = "RobbyPointIntro";
        private const string PointLoop = "RobbyPointLoop";
        private const string PointOutro = "RobbyPointOutro";

        private sealed class WeaponSet
        {
            public string Intro, Loop, Outro, Fire;
            public WeaponSet(string intro, string loop, string outro, string fire) { Intro = intro; Loop = loop; Outro = outro; Fire = fire; }
        }

        private static readonly Dictionary<string, WeaponSet> Sets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rifle"] = new WeaponSet("PlayerARifleHeldToAimMP", "PlayerARifleIdleAimMP", "PlayerARifleAimToHeldMP", "PlayerARifleFireMP"),
            // No aim-to-idle clip for these two in the player animator, so the outro just holds the aim pose.
            ["shotgun"] = new WeaponSet("PlayerAShotgunIdleToAimMP", "PlayerAShotgunAimIdleMP", "PlayerAShotgunAimIdleMP", "PlayerAShotgunAimFireMP"),
            ["pistol"] = new WeaponSet("PlayerAPistolIdleToAimMP", "PlayerAPistolAimIdleMP", "PlayerAPistolAimIdleMP", "PlayerAPistolAimFireMP"),
        };

        private static Dictionary<string, AnimationClip> _clips;
        private static readonly Dictionary<IntPtr, RuntimeAnimatorController> Originals = new();
        private static readonly Dictionary<IntPtr, (AnimatorOverrideController aoc, WeaponSet set)> Applied = new();
        private static bool _previewRunning;

        public static string CurrentWeapon { get; private set; } = "off";

        // ------------------------------------------------------------------ commands

        // ------------------------------------------------------------------ automatic use (called from CombatGun.Tick)

        private static readonly int HashIntro = Animator.StringToHash("PointIntro");
        private static readonly int HashLoop = Animator.StringToHash("PointLoop");
        private static readonly int HashOutro = Animator.StringToHash("PointOutro");
        private static readonly HashSet<IntPtr> Failed = new();

        /// <summary>Applies the configured gun animation to a soldier that does not have it yet.</summary>
        internal static void EnsureApplied(VailActor actor)
        {
            if (!actor) return;
            string w = (Config.CombatAnim.Value ?? "").Trim().ToLowerInvariant();
            if (w == "" || w == "off") return;
            IntPtr key = actor.Pointer;
            if (Applied.ContainsKey(key) || Failed.Contains(key)) return;
            if (!Sets.TryGetValue(w, out var set)) return;
            if (_clips == null || _clips.Count == 0)
            {
                if (!LoadClips(new List<string>())) return;
            }
            if (Apply(actor, set, out string err)) CurrentWeapon = w;
            else
            {
                Failed.Add(key);
                RLog.Warning($"{CharacterReplacement.Tag} [Combat] weapon animation not possible for {actor.name}: {err}");
            }
        }

        /// <summary>True while the soldier raises, holds or lowers his gun (the point-at-enemy states).</summary>
        internal static bool IsAiming(VailActor actor)
        {
            Animator an = AnimatorOf(actor);
            if (!an) return false;
            try
            {
                var cur = an.GetCurrentAnimatorStateInfo(0);
                int h = cur.shortNameHash;
                if (h == HashIntro || h == HashLoop || h == HashOutro) return true;
                if (an.IsInTransition(0))
                {
                    int n = an.GetNextAnimatorStateInfo(0).shortNameHash;
                    if (n == HashIntro || n == HashLoop) return true;
                }
            }
            catch { }
            return false;
        }

        internal static void Clear()
        {
            Originals.Clear();
            Applied.Clear();
            Failed.Clear();
            _clips = null;
            _previewRunning = false;
            Poses.Clear();
            _reloadClip = null;
            _reloadSearched = false;
            CurrentWeapon = "off";
        }

        // ------------------------------------------------------------------ core

        private static bool LoadClips(List<string> lines)
        {
            if (_clips != null && _clips.Count > 0) return true;
            _clips = new Dictionary<string, AnimationClip>();
            try
            {
                RuntimeAnimatorController src = null;
                foreach (var c in Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>())
                {
                    if (c && c.name == SourceController) { src = c; break; }
                }
                if (!src) { lines.Add($"{SourceController} is not loaded."); return false; }
                foreach (var clip in src.animationClips)
                {
                    if (clip && !_clips.ContainsKey(clip.name)) _clips[clip.name] = clip;
                }
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] {_clips.Count} clips loaded from {SourceController}");
                return true;
            }
            catch (Exception e)
            {
                lines.Add($"Loading clips failed: {e.Message}");
                _clips = null;
                return false;
            }
        }

        internal static Animator AnimatorOf(VailActor actor)
        {
            Animator an = null;
            try { an = actor._animator; } catch { }
            if (!an) { try { an = actor.GetComponentInChildren<Animator>(true); } catch { } }
            return an;
        }

        private static bool Apply(VailActor actor, WeaponSet set, out string error)
        {
            error = null;
            if (!actor) { error = "no actor"; return false; }
            Animator an = AnimatorOf(actor);
            if (!an) { error = "no animator"; return false; }
            try
            {
                IntPtr key = actor.Pointer;
                if (!Originals.TryGetValue(key, out RuntimeAnimatorController original))
                {
                    original = an.runtimeAnimatorController;
                    if (!original) { error = "animator has no controller"; return false; }
                    Originals[key] = original;
                }

                var aoc = new AnimatorOverrideController(original);
                aoc[PointIntro] = _clips[set.Intro];
                aoc[PointLoop] = _clips[set.Loop];
                aoc[PointOutro] = _clips[set.Outro];
                an.runtimeAnimatorController = aoc;
                Applied[key] = (aoc, set);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                RLog.Warning($"{CharacterReplacement.Tag} [Combat] swapping the animation of {actor.name}: {e}");
                return false;
            }
        }

        // ------------------------------------------------------------------ poses (any player clip on the soldier)

        // The soldier's animator has no states for a rifle carry, strafing or staggering, but its two point states
        // (PointIntro, PointLoop) can play any clip through the override controller. Pose() puts the wanted clip into
        // the slot that is NOT playing and cross-fades to it, so poses blend instead of snapping. PointIntro leaves for
        // PointLoop by itself when its clip ends; PoseTick() therefore copies the clip into PointLoop as well once the
        // blend into PointIntro is done, so that automatic switch shows the same clip.

        private sealed class PoseState
        {
            public string Clip;
            public int Slot;
            public float Since, Blend;
            public bool Mirrored;
        }

        private static readonly Dictionary<IntPtr, PoseState> Poses = new();
        private static readonly HashSet<string> MissingLogged = new();

        internal static bool HasClip(string name) => name != null && _clips != null && _clips.ContainsKey(name);

        internal static AnimationClip Clip(string name) => HasClip(name) ? _clips[name] : null;

        /// <summary>Clips that did not come from the player controller (his own Kelvin clips, for tests).</summary>
        internal static readonly HashSet<string> ForeignClips = new();

        /// <summary>Makes more clips playable as poses (by name). Returns how many were new.</summary>
        internal static int RegisterClips(IEnumerable<AnimationClip> clips)
        {
            if (_clips == null && !LoadClips(new List<string>())) return 0;
            int n = 0;
            foreach (var c in clips)
            {
                if (!c || _clips.ContainsKey(c.name)) continue;
                _clips[c.name] = c;
                ForeignClips.Add(c.name);
                n++;
            }
            return n;
        }

        /// <summary>The clip name with "Up"/"Down" before the trailing "MP" if that variant exists (aiming at height).</summary>
        internal static string Variant(string name, int pitch)
        {
            if (name == null || pitch == 0 || !name.EndsWith("MP", StringComparison.Ordinal)) return name;
            string v = name.Substring(0, name.Length - 2) + (pitch > 0 ? "UpMP" : "DownMP");
            return HasClip(v) ? v : name;
        }

        internal static string AimClip(VailActor a, int pitch) => Applied.TryGetValue(a.Pointer, out var e) ? Variant(e.set.Loop, pitch) : null;
        internal static string FireClip(VailActor a, int pitch) => Applied.TryGetValue(a.Pointer, out var e) ? Variant(e.set.Fire, pitch) : null;

        /// <summary>Rifle carried at rest, matching the weapon set ("...IdleAim..." becomes "...IdleHeld...").</summary>
        internal static string HeldClip(VailActor a, int pitch)
        {
            if (!Applied.TryGetValue(a.Pointer, out var e)) return null;
            string held = e.set.Loop.Replace("IdleAim", "IdleHeld").Replace("AimIdle", "IdleHeld");
            return HasClip(held) ? Variant(held, pitch) : Variant(e.set.Loop, pitch);
        }

        /// <summary>First existing clip of the list (logs once if none exists).</summary>
        internal static string First(params string[] names)
        {
            foreach (var n in names) if (HasClip(n)) return n;
            string key = string.Join("|", names);
            if (MissingLogged.Add(key)) RLog.Warning($"{CharacterReplacement.Tag} [Combat] none of these clips exists: {string.Join(", ", names)}");
            return null;
        }

        /// <summary>Forget the pose record (after an animator Rebind the states are reset; the next Pose() enters again).</summary>
        internal static void ForgetPose(VailActor a) { if (a) Poses.Remove(a.Pointer); }

        internal static string CurrentPose(VailActor a) => a && Poses.TryGetValue(a.Pointer, out var p) ? p.Clip : null;

        /// <summary>True once the pose has blended in.</summary>
        internal static bool PoseSettled(VailActor a) =>
            a && Poses.TryGetValue(a.Pointer, out var p) && Time.time >= p.Since + p.Blend;

        internal static float PoseAge(VailActor a) =>
            a && Poses.TryGetValue(a.Pointer, out var p) ? Time.time - p.Since : 0f;

        /// <summary>Plays clip on the soldier with a cross-fade. restart = play it again even if it is already playing.</summary>
        internal static bool Pose(VailActor a, string clip, float blend = 0.2f, bool restart = false)
        {
            if (!a || clip == null || !Applied.TryGetValue(a.Pointer, out var entry) || !entry.aoc) return false;
            if (!_clips.TryGetValue(clip, out var c)) return false;
            Animator an = AnimatorOf(a);
            if (!an) return false;
            Poses.TryGetValue(a.Pointer, out var ps);
            try
            {
                an.SetBool("PointAtEnemies", true);
                int cur = an.GetCurrentAnimatorStateInfo(0).shortNameHash;
                if (an.IsInTransition(0)) cur = an.GetNextAnimatorStateInfo(0).shortNameHash;
                bool inSlot = cur == HashLoop || cur == HashIntro;
                if (ps != null && ps.Clip == clip && !restart && inSlot) return true;
                int target = inSlot && cur == HashLoop ? HashIntro : HashLoop;
                entry.aoc[target == HashLoop ? PointLoop : PointIntro] = c;
                an.CrossFadeInFixedTime(target, blend, 0, 0f);
                Poses[a.Pointer] = new PoseState { Clip = clip, Slot = target, Since = Time.time, Blend = blend };
                return true;
            }
            catch (Exception e)
            {
                if (MissingLogged.Add("pose" + e.Message)) RLog.Warning($"{CharacterReplacement.Tag} [Combat] Pose {clip}: {e.Message}");
                return false;
            }
        }

        /// <summary>Every frame: a pose sitting in PointIntro gets mirrored into PointLoop (see above).</summary>
        internal static void PoseTick()
        {
            if (Poses.Count == 0) return;
            float now = Time.time;
            List<IntPtr> gone = null;
            foreach (var kv in Poses)
            {
                var p = kv.Value;
                if (p.Mirrored || p.Slot != HashIntro || now < p.Since + p.Blend + 0.05f) continue;
                if (!Applied.TryGetValue(kv.Key, out var entry) || !entry.aoc) { (gone ??= new List<IntPtr>()).Add(kv.Key); continue; }
                try { if (_clips.TryGetValue(p.Clip, out var c)) entry.aoc[PointLoop] = c; } catch { }
                p.Mirrored = true;
            }
            if (gone != null) foreach (var k in gone) Poses.Remove(k);
        }

        /// <summary>
        /// Back to the soldier's own animations: restores the point clips and cross-fades straight to the state he was
        /// in before the fight (returnState, a full path hash), or lowers the gun through the aim-to-held outro.
        /// </summary>
        internal static void EndPoses(VailActor a, int returnState = 0)
        {
            if (!a) return;
            Poses.Remove(a.Pointer);
            if (!Applied.TryGetValue(a.Pointer, out var entry) || !entry.aoc) return;
            try
            {
                entry.aoc[PointLoop] = _clips[entry.set.Loop];
                entry.aoc[PointIntro] = _clips[entry.set.Intro];
                Animator an = AnimatorOf(a);
                if (an)
                {
                    an.SetBool("PointAtEnemies", false);
                    bool done = false;
                    if (returnState != 0)
                    {
                        try
                        {
                            if (an.HasState(0, returnState)) { an.CrossFadeInFixedTime(returnState, 0.4f, 0, 0f); done = true; }
                        }
                        catch { }
                    }
                    if (!done)
                    {
                        int cur = an.GetCurrentAnimatorStateInfo(0).shortNameHash;
                        if (cur == HashIntro || cur == HashLoop) an.CrossFadeInFixedTime(HashOutro, 0.25f, 0, 0f);
                    }
                }
            }
            catch { }
        }

        /// <summary>Does the current pose hold the gun in the hands (rifle/shotgun/pistol clips)? known = a pose is active.</summary>
        internal static bool PoseHoldsGun(VailActor a, out bool known)
        {
            known = false;
            if (!a || !Poses.TryGetValue(a.Pointer, out var p)) return false;
            known = true;
            string c = p.Clip;
            return c.Contains("Rifle") || c.Contains("Shotgun") || c.Contains("Pistol");
        }

        /// <summary>One shot: plays the fire clip once, then the aim clip again.</summary>
        public static IEnumerator FireOnce(VailActor actor) => FireOnce(actor, actor ? actor.transform.forward : Vector3.forward);

        /// <summary>One shot in direction dir (muzzle flash, sound, recoil, fire clip; aims up/down with the shot).</summary>
        public static IEnumerator FireOnce(VailActor actor, Vector3 dir, int pitch = 0)
        {
            if (actor)
            {
                try { CombatFx.Shot(actor, dir); } catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] shot effect: {e.Message}"); }
                try { CombatGun.Kick(actor, dir); } catch { }
            }
            if (!actor || !Applied.ContainsKey(actor.Pointer)) yield break;
            string fire = FireClip(actor, pitch), aim = AimClip(actor, pitch);
            if (!Pose(actor, fire, 0.05f, restart: true)) yield break;
            float len = Clip(fire) ? Clip(fire).length : 0.3f;
            float until = Time.time + Mathf.Clamp(len * 0.9f, 0.12f, 0.8f);
            while (Time.time < until) yield return null;
            // Only go back to aiming if nothing else (reload, stagger, moving) took over meanwhile.
            if (actor && CurrentPose(actor) == fire) Pose(actor, aim, 0.12f);
        }

        // ------------------------------------------------------------------ reloading

        private static string _reloadClip;
        private static bool _reloadSearched;

        /// <summary>
        /// Reload clip of the current weapon set from the player animator (e.g. a "...Rifle...Reload...MP" clip), or null.
        /// Searched once; logged so the choice can be checked.
        /// </summary>
        private static string ReloadClip(WeaponSet set)
        {
            if (_reloadSearched) return _reloadClip;
            _reloadSearched = true;
            if (_clips == null) return null;
            string weapon = set.Loop.Contains("Rifle") ? "Rifle" : set.Loop.Contains("Shotgun") ? "Shotgun" : set.Loop.Contains("Pistol") ? "Pistol" : "";
            var found = new List<string>();
            foreach (var k in _clips.Keys)
                if (k.IndexOf("Reload", StringComparison.OrdinalIgnoreCase) >= 0 && (weapon == "" || k.IndexOf(weapon, StringComparison.OrdinalIgnoreCase) >= 0))
                    found.Add(k);
            found.Sort((a, b) =>
            {
                int sa = (a.EndsWith("MP") ? 0 : 2) + (a.IndexOf("Aim", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1);
                int sb = (b.EndsWith("MP") ? 0 : 2) + (b.IndexOf("Aim", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1);
                return sa != sb ? sa.CompareTo(sb) : string.CompareOrdinal(a, b);
            });
            _reloadClip = found.Count > 0 ? found[0] : null;
            RLog.Msg($"{CharacterReplacement.Tag} [Combat] reload clips: {(found.Count == 0 ? "none - soldiers lower the rifle to reload" : string.Join(", ", found))}" +
                     (_reloadClip != null ? $" -> using {_reloadClip}" : ""));
            return _reloadClip;
        }

        /// <summary>Plays the reload clip. Returns its length in seconds, 0 if there is none.</summary>
        internal static float StartReload(VailActor actor)
        {
            if (!actor || !Applied.TryGetValue(actor.Pointer, out var entry)) return 0f;
            string name = ReloadClip(entry.set);
            if (name == null || !Pose(actor, name, 0.2f, restart: true)) return 0f;
            return Clip(name) ? Clip(name).length : 0f;
        }


        private static readonly string[] StateGuesses =
        {
            "RobbyPointLoop", "RobbyPointIntro", "RobbyPointOutro", "PointLoop", "PointIntro", "PointOutro",
            "Point", "PointAtEnemies", "PointAtEnemy", "Point At Enemy", "Point At Enemies", "PointStanding"
        };

        /// <summary>Which of the guessed point state names exist on which layer (short name or "Layer.Name").</summary>
        private static List<(int layer, int hash, string label)> FindPointStates(Animator an)
        {
            var found = new List<(int, int, string)>();
            try
            {
                for (int l = 0; l < an.layerCount; l++)
                {
                    string ln = an.GetLayerName(l);
                    foreach (var g in StateGuesses)
                    {
                        int h1 = Animator.StringToHash(g);
                        int h2 = Animator.StringToHash(ln + "." + g);
                        if (an.HasState(l, h1)) found.Add((l, h1, $"L{l}:{g}"));
                        else if (an.HasState(l, h2)) found.Add((l, h2, $"L{l}:{ln}.{g}"));
                    }
                }
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Combat] state search: {e.Message}"); }
            return found;
        }

    }
}
