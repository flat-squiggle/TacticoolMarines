using System;
using System.Collections.Generic;
using System.Linq;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using TheForest.Utils;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Diagnostics for the soldier model playing Kelvin's own animations (development):
    ///   tmcombat clips [filter]   clips of the soldier's own (Kelvin) controller; they become stageable by name
    ///   tmcombat bind [all]       bones of the staged (or nearest) soldier that are away from the model's bind pose
    ///   tmcombat orders           order / follow state of every soldier
    ///   tmcombat order follow     all soldiers follow the local player (like /tmfollow)
    /// </summary>
    internal static class CombatDiag
    {

        /// <summary>Clips of the soldier's original controller (Kelvin's), not our player clips.</summary>
        internal static List<AnimationClip> OwnClips(Animator an)
        {
            var list = new List<AnimationClip>();
            var seen = new HashSet<string>();
            try
            {
                RuntimeAnimatorController c = an.runtimeAnimatorController;
                var aoc = c ? c.TryCast<AnimatorOverrideController>() : null;
                if (aoc && aoc.runtimeAnimatorController) c = aoc.runtimeAnimatorController;
                if (c) foreach (var clip in c.animationClips) if (clip && seen.Add(clip.name)) list.Add(clip);
            }
            catch (Exception e) { RLog.Warning($"{CharacterReplacement.Tag} [Clips] {e.Message}"); }
            return list;
        }

        internal struct Dev { public string path; public float cm, deg; public bool head; public Transform bone; }

        internal static string Path(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (; t && t != root; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            // the last three are enough to tell bones apart
            return string.Join("/", parts.Skip(Math.Max(0, parts.Count - 3)));
        }

    }
}
