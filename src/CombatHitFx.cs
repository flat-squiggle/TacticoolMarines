using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Weapon;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// What a hit or a miss looks like, using the game's own effects (so it matches the player's gun):
    ///   hit:  blood spray (ImpactEffectManager.SpawnBlood, replicated to co-op), blood on the body part
    ///         (CharacterBloodRevealProxy.ApplyBlood), the enemy's stagger build-up (StaggerController.OnHitNode) and
    ///         its hit-reaction animator parameters (_hitAdditiveParam, _hitProjectileParam, body part, front/back)
    ///   miss: an impact puff where the bullet lands (ImpactEffectManager.SpawnSurfaceImpactObject: dirt, rock, wood ...)
    /// Each step is tried on its own; what works and what fails is logged once.
    /// </summary>
    internal static class CombatHitFx
    {
        private static readonly System.Random Rng = new();
        private static readonly HashSet<string> Logged = new();
        private static readonly Dictionary<long, AnimatorControllerParameterType> ParamTypes = new();
        private static readonly List<(Animator an, int id, AnimatorControllerParameterType type, float at)> Resets = new();

        private static void Once(string key, string msg, bool warn = false)
        {
            if (!Logged.Add(key)) return;
            if (warn) RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Hit] {msg}");
            else RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Hit] {msg}");
        }

        // ------------------------------------------------------------------ hit

        internal static void Hit(VailActor target, DamageNode node, string part, Vector3 point, Vector3 dir, float damage)
        {
            if (!target) return;
            var done = new List<string>();

            try
            {
                ImpactEffectManager.SpawnBlood(point, Quaternion.LookRotation(-dir), dir * 2f, true);
                done.Add("blood spatter");
            }
            catch (Exception e) { Once("blood", $"Blood spatter: {e.Message}", true); }

            try
            {
                var proxy = target.GetComponentInChildren<CharacterBloodRevealProxy>(true);
                if (proxy)
                {
                    proxy.ApplyBlood(part);
                    done.Add("blood on the body");
                }
                else Once("noproxy" + target.TypeId, $"{target.TypeId} has no CharacterBloodRevealProxy");
            }
            catch (Exception e) { Once("reveal", $"Blood on the body: {e.Message}", true); }

            try
            {
                var sc = target._staggerController;
                if (sc && node) { sc.OnHitNode(node, damage); done.Add("stagger damage"); }
            }
            catch (Exception e) { Once("stagger", $"Stagger: {e.Message}", true); }

            try
            {
                Animator an = CombatLook.AnimatorOf(target);
                if (an)
                {
                    int n = 0;
                    n += Pulse(an, target._hitAdditiveParam) ? 1 : 0;
                    n += Pulse(an, target._hitProjectileParam) ? 1 : 0;
                    n += Pulse(an, target._hitRifleParam) ? 1 : 0;
                    string p = (part ?? "").ToLowerInvariant();
                    if (p.Contains("head")) n += Pulse(an, target._hitHeadParam) ? 1 : 0;
                    else if (p.Contains("arm")) n += Pulse(an, target._hitArmParam) ? 1 : 0;
                    else if (p.Contains("leg")) n += Pulse(an, target._hitLegParam) ? 1 : 0;
                    else if (p.Contains("stomach")) n += Pulse(an, target._hitStomachParam) ? 1 : 0;
                    else n += Pulse(an, target._hitBodyParam) ? 1 : 0;
                    bool fromFront = Vector3.Dot(target.transform.forward, -dir) > 0f;
                    n += Pulse(an, fromFront ? target._hitFrontBodyParam : target._hitBackBodyParam) ? 1 : 0;
                    if (n > 0) done.Add($"{n} hit animation values");
                }
            }
            catch (Exception e) { Once("params", $"Hit animation: {e.Message}", true); }

            Once("hitfx" + target.TypeId, $"Hit feedback on {target.TypeId}: {(done.Count == 0 ? "nothing" : string.Join(", ", done))}");
        }

        /// <summary>Sets a hit parameter the way its type wants it (trigger, or on for a moment then off again).</summary>
        private static bool Pulse(Animator an, VailActor.AnimParam p)
        {
            if (!p.Exists) return false;
            AnimatorControllerParameterType type = TypeOf(an, p.Id);
            switch (type)
            {
                case AnimatorControllerParameterType.Trigger: an.SetTrigger(p.Id); return true;
                case AnimatorControllerParameterType.Bool: an.SetBool(p.Id, true); break;
                case AnimatorControllerParameterType.Int: an.SetInteger(p.Id, 1); break;
                case AnimatorControllerParameterType.Float: an.SetFloat(p.Id, 1f); break;
                default: return false;
            }
            Resets.Add((an, p.Id, type, Time.time + 0.15f));
            return true;
        }

        private static AnimatorControllerParameterType TypeOf(Animator an, int id)
        {
            long key = ((long)an.GetInstanceID() << 32) ^ (uint)id;
            if (ParamTypes.TryGetValue(key, out var t)) return t;
            t = (AnimatorControllerParameterType)0;
            try
            {
                foreach (var par in an.parameters)
                    if (par.nameHash == id) { t = par.type; break; }
            }
            catch { }
            ParamTypes[key] = t;
            return t;
        }

        /// <summary>Every frame: switch pulsed parameters off again.</summary>
        internal static void Tick()
        {
            if (Resets.Count == 0) return;
            float now = Time.time;
            for (int i = Resets.Count - 1; i >= 0; i--)
            {
                var r = Resets[i];
                if (now < r.at) continue;
                Resets.RemoveAt(i);
                try
                {
                    if (!r.an) continue;
                    if (r.type == AnimatorControllerParameterType.Bool) r.an.SetBool(r.id, false);
                    else if (r.type == AnimatorControllerParameterType.Int) r.an.SetInteger(r.id, 0);
                    else if (r.type == AnimatorControllerParameterType.Float) r.an.SetFloat(r.id, 0f);
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------ miss

        /// <summary>A miss: the bullet flies on with some spread and kicks up whatever it lands on.</summary>
        internal static void Miss(Vector3 muzzle, Vector3 aimAt, VailActor shooter)
        {
            try
            {
                Vector3 dir = (aimAt - muzzle).normalized;
                // 1-4 degrees off, any direction
                Vector3 side = Vector3.Cross(dir, Vector3.up).normalized;
                if (side.sqrMagnitude < 0.5f) side = Vector3.right;
                float ang = 1f + (float)Rng.NextDouble() * 3f;
                float rot = (float)Rng.NextDouble() * 360f;
                Vector3 off = Quaternion.AngleAxis(rot, dir) * side;
                dir = Quaternion.AngleAxis(ang, Vector3.Cross(dir, off)) * dir;
                Impact(muzzle, dir, 80f);
            }
            catch (Exception e) { Once("miss", $"Miss impact: {e.Message}", true); }
        }

        /// <summary>Impact puff at the first solid surface (not an actor) along the ray.</summary>
        internal static void Impact(Vector3 from, Vector3 dir, float maxDist)
        {
            var hits = Physics.RaycastAll(from, dir, maxDist, ~0, QueryTriggerInteraction.Ignore);
            RaycastHit best = default;
            float bestD = float.MaxValue;
            foreach (var h in hits)
            {
                if (!h.collider || h.distance >= bestD) continue;
                if (h.collider.GetComponentInParent<VailActor>()) continue;
                if (h.collider.transform.name.StartsWith("LivingSoldiers_", StringComparison.Ordinal)) continue;
                best = h;
                bestD = h.distance;
            }
            if (bestD == float.MaxValue) return;
            bool ok = ImpactEffectManager.SpawnSurfaceImpactObject(best.transform, best.point, Quaternion.LookRotation(best.normal), Vector3.zero, true);
            if (!ok) ImpactEffectManager.SpawnSurfaceImpactObject(ImpactEffectManager.ParticleType.Dirt, best.point, Quaternion.LookRotation(best.normal), Vector3.zero, true);
            Once("missfx", $"Miss impacts: on '{best.collider.name}' {(ok ? "matching the surface" : "as dust")}");
        }

        internal static void Clear()
        {
            Resets.Clear();
            ParamTypes.Clear();
        }
    }
}
