using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Items.Core;
using Sons.Weapon;
using UnityEngine;

namespace CharacterReplacement
{
    /// <summary>
    /// Whether one shot hits, and what the hit does.
    /// Hit chance: combat_accuracy, worse with distance, for the first shot after raising the gun, for later shots
    /// in a burst (recoil) and against a moving target. A shot is aimed at a body part (head / torso / limb);
    /// if something solid is between the muzzle and that part, the shot hits the obstacle instead.
    /// Damage, hit method "impact": a real bullet impact on the body part's DamageNode (ProjectileImpactData, the same
    /// data a player's bullet carries), so the game applies its own body part multipliers, hit reactions and blood.
    /// After an impact the enemy is told who shot him (TriggerOnHitEventStimuli), so he turns on the soldier.
    /// If an impact does not lower the enemy's health, the damage is applied directly (DamageActor) instead, and
    /// after three such misses the method switches to "direct" for the session.
    /// </summary>
    internal static class CombatHit
    {
        internal sealed class Shot
        {
            public float Chance;
            public bool Hit;
            public string Part = "-";
            public string Note = "";
        }

        private sealed class Pending
        {
            public VailActor Target;
            public float HealthBefore;
            public float Damage;
            public float CheckAt;
            public string Shooter;
        }

        private static readonly System.Random Rng = new();
        private static readonly List<Pending> Checks = new();
        private static readonly Dictionary<IntPtr, List<DamageNode>> NodeCache = new();
        private static int _impactFails, _impactWorks;
        private static bool _impactOff;
        private static readonly HashSet<string> Logged = new();

        internal static string ModeInfo()
        {
            string mode = UseImpact() ? "impact" : "direct";
            if (_impactOff) mode += " (impact did not work, switched automatically)";
            return $"Hit method {mode}, ReceivedDamage worked {_impactWorks}x, errors {_impactFails}x{(_reactOnly ? ", reaction only (damage applied directly)" : "")}";
        }

        private static bool UseImpact() =>
            !_impactOff && string.Equals((Config.CombatHitMode.Value ?? "impact").Trim(), "impact", StringComparison.OrdinalIgnoreCase);

        /// <summary>Hit chance for this shot.</summary>
        internal static float Chance(float dist, int shotInBurst, bool firstAfterAim, float targetSpeed, bool shooterMoved)
        {
            float range = Mathf.Max(10f, Config.CombatRange.Value);
            float c = Config.CombatAccuracy.Value;
            c *= Mathf.Lerp(1.15f, 0.55f, Mathf.InverseLerp(5f, range, dist)); // close is easy, far is hard
            if (firstAfterAim) c *= 0.8f;                                        // not settled yet
            c *= 1f - 0.08f * Mathf.Max(0, shotInBurst);                         // recoil in a burst
            if (targetSpeed > 1.5f) c *= Mathf.Lerp(1f, 0.7f, Mathf.InverseLerp(1.5f, 6f, targetSpeed));
            if (shooterMoved) c *= 0.85f;
            return Mathf.Clamp(c, 0.05f, 0.97f);
        }

        /// <summary>Resolves one shot from muzzle at target. Applies the damage on a hit.</summary>
        internal static Shot Fire(VailActor shooter, VailActor target, Vector3 muzzle, float chance, bool tellTarget, bool gameSees = false)
        {
            var r = new Shot { Chance = chance };
            DamageNode node = PickNode(target, out string part);
            r.Part = part;
            Vector3 aimAt = node ? NodeCenter(node) : target.transform.position + Vector3.up * 1.2f;

            // Something in the way? Then the bullet ends there. Not checked at point-blank range: there the muzzle
            // can already be past the enemy, and nothing fits between them anyway.
            bool pointBlank = CombatMove.Flat(target.transform.position - shooter.transform.position).magnitude < 2.2f;
            if (!pointBlank && CombatAI.Blocked(muzzle, aimAt, shooter, target, out float blockedAt))
            {
                // The game itself says he can see the enemy: treat the obstacle as partial cover, not a wall.
                if (gameSees) { chance *= 0.7f; r.Chance = chance; r.Note = "partial cover (" + CombatAI.LastBlocker + ") - "; }
                else
                {
                    r.Note = "obstacle in the way: " + CombatAI.LastBlocker;
                    try { CombatHitFx.Impact(muzzle, (aimAt - muzzle).normalized, blockedAt + 0.5f); } catch { }
                    return r;
                }
            }
            if (Rng.NextDouble() > chance)
            {
                r.Note += "missed";
                CombatHitFx.Miss(muzzle, aimAt, shooter);
                return r;
            }
            r.Hit = true;

            float damage = Damage(node, out int projectileType);
            float before = Health(target);
            bool reacted = false;
            if (UseImpact() && node)
            {
                try
                {
                    // The game's own "this actor was damaged" entry: hit node, bullet impact, the shooter's stimuli
                    // (so the enemy knows who shot him), damage, direction. It plays the hit reaction.
                    Vector3 from = pointBlank ? shooter.transform.position + Vector3.up * 1.4f : muzzle;
                    Vector3 dir = (aimAt - from).normalized;
                    var stim = shooter._actorStimuli;
                    // No sneak-attack bonus for soldiers (x5 from behind on most cannibals) - that is the player's.
                    const bool behind = false;
                    target.ReceivedDamage(node, ImpactType.Projectile, projectileType, stim, damage, false, false, behind, dir);
                    reacted = true;
                    float after = Health(target);
                    if (after < before - 0.01f || Dead(target)) { _impactWorks++; r.Note += $"impact {before - after:0} damage"; }
                    else if (_reactOnly)
                    {
                        target.DamageActor(damage, false);
                        r.Note += $"hit reaction, {damage:0} damage directly";
                    }
                    else Checks.Add(new Pending { Target = target, HealthBefore = before, Damage = damage, CheckAt = Time.time + 0.25f, Shooter = shooter.name });
                }
                catch (Exception e)
                {
                    LogOnce("impact", $"ReceivedDamage failed, using direct damage: {e.Message}");
                    reacted = false;
                    _impactFails++;
                    if (_impactFails >= 3 && _impactWorks == 0) SwitchToDirect();
                }
            }
            if (!reacted)
            {
                try
                {
                    target.DamageActor(damage, false);
                    r.Note += $"{damage:0} damage (direct)";
                }
                catch (Exception e) { LogOnce("direct", $"DamageActor: {e.Message}"); }

                if (tellTarget)
                {
                    try
                    {
                        var stim = shooter._actorStimuli;
                        if (stim) target.TriggerOnHitEventStimuli(stim);
                    }
                    catch (Exception e) { LogOnce("stimuli", $"Alerting the target to the shooter: {e.Message}"); }
                }
            }
            try
            {
                Vector3 from = pointBlank ? shooter.transform.position + Vector3.up * 1.4f : muzzle;
                CombatHitFx.Hit(target, node, r.Part, aimAt, (aimAt - from).normalized, damage);
            }
            catch (Exception e) { LogOnce("fx", $"Hit effects: {e.Message}"); }
            return r;
        }

        private static bool _reactOnly;
        private static int _noEffect;

        /// <summary>
        /// Called every frame: hits that did not lower health get their damage applied directly. If that keeps
        /// happening, ReceivedDamage only plays the reaction - from then on the damage is applied right away with it.
        /// </summary>
        internal static void Tick()
        {
            CombatHitFx.Tick();
            if (Checks.Count == 0) return;
            float now = Time.time;
            for (int i = Checks.Count - 1; i >= 0; i--)
            {
                var p = Checks[i];
                if (now < p.CheckAt) continue;
                Checks.RemoveAt(i);
                if (!p.Target) continue;
                if (Dead(p.Target) || Health(p.Target) < p.HealthBefore - 0.01f) { _impactWorks++; continue; }
                _noEffect++;
                try { p.Target.DamageActor(p.Damage, false); } catch { }
                if (_noEffect >= 2 && _impactWorks == 0 && !_reactOnly)
                {
                    _reactOnly = true;
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Hit] ReceivedDamage takes no health - the game plays the hit reaction, the damage is applied directly");
                }
            }
        }

        private static void SwitchToDirect()
        {
            if (_impactOff) return;
            _impactOff = true;
            RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Hit] Real impacts do not work - direct damage from now on for this session");
        }

        internal static float Health(VailActor a)
        {
            try { return a.GetHealth(); } catch { return 0f; }
        }

        internal static bool Dead(VailActor a)
        {
            try { return !a || a.IsDead(); } catch { return true; }
        }

        // ------------------------------------------------------------------ damage like the player's rifle

        private static float _rifleDamage = -1f;
        private static int _rifleType = (int)ImpactProjectileType.HeavyBullet;

        /// <summary>
        /// Damage of one hit. combat_damage 0 (default) = the player's rifle: the rifle ammo's muzzle damage (read from
        /// the game, 100) times the body part's projectile multiplier (regular cannibals: torso x2, head x200, limbs
        /// x0.8). The enemy's own bullet resistance and the hit-from-behind bonus are applied by the game. Any other
        /// value = that flat damage per hit. combat_damage_scale multiplies either.
        /// </summary>
        internal static float Damage(DamageNode node, out int projectileType)
        {
            projectileType = (int)ImpactProjectileType.Bullet;
            float scale = Mathf.Max(0f, Config.CombatDamageScale.Value);
            if (Config.CombatDamage.Value > 0f) return Config.CombatDamage.Value * scale;
            if (_rifleDamage < 0f)
            {
                _rifleDamage = 100f;
                try
                {
                    var rifle = ItemDatabaseManager.ItemById(361);
                    var ammo = ItemDatabaseManager.ItemById(387);
                    var pi = ammo != null && ammo.Ammo != null ? ammo.Ammo.ProjectileInfo : null;
                    if (pi != null && pi.muzzleDamage > 0f)
                    {
                        _rifleDamage = pi.muzzleDamage * (rifle != null && rifle.AmmoDamageMult > 0f ? rifle.AmmoDamageMult : 1f);
                        _rifleType = (int)pi.ProjectileType;
                    }
                    RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Hit] damage like the player's rifle: {_rifleDamage:0} per bullet ({(ImpactProjectileType)_rifleType}) times the hit zone");
                }
                catch (Exception e) { LogOnce("rifledmg", $"Rifle damage not readable ({e.Message}) - using 100"); }
            }
            projectileType = _rifleType;
            float mult = 1f;
            try { if (node) mult = node._damageMultiplierProjectile; } catch { }
            return _rifleDamage * mult * scale;
        }

        // ------------------------------------------------------------------ body parts

        private static List<DamageNode> Nodes(VailActor target)
        {
            if (NodeCache.TryGetValue(target.Pointer, out var list) && list.Count > 0 && list[0]) return list;
            list = new List<DamageNode>();
            try
            {
                foreach (var n in target.GetComponentsInChildren<DamageNode>(true))
                    if (n && n.gameObject.activeInHierarchy) list.Add(n);
            }
            catch { }
            NodeCache[target.Pointer] = list;
            if (Logged.Add("nodes" + target.TypeId))
            {
                var names = new List<string>();
                foreach (var n in list) names.Add(NodeName(n));
                RLog.Msg($"{CharacterReplacement.Tag} [Combat] [Hit] hit zones {target.TypeId}: {(names.Count == 0 ? "none" : string.Join(", ", names))}");
            }
            return list;
        }

        private static string NodeName(DamageNode n)
        {
            string s = null;
            try { s = n._name; } catch { }
            if (string.IsNullOrEmpty(s)) { try { s = n.gameObject.name; } catch { s = "?"; } }
            return s;
        }

        private static readonly string[] HeadWords = { "head", "neck" };
        private static readonly string[] TorsoWords = { "spine", "chest", "torso", "body", "pelvis", "hip", "stomach", "back" };

        private static DamageNode PickNode(VailActor target, out string part)
        {
            part = "body";
            var nodes = Nodes(target);
            if (nodes.Count == 0) return null;
            double roll = Rng.NextDouble();
            string[] want = roll < Config.CombatHeadshotChance.Value ? HeadWords : roll < 0.85 ? TorsoWords : null;
            var match = new List<DamageNode>();
            if (want != null)
            {
                foreach (var n in nodes)
                {
                    string nm = NodeName(n).ToLowerInvariant();
                    foreach (var w in want) if (nm.Contains(w)) { match.Add(n); break; }
                }
            }
            else
            {
                foreach (var n in nodes)
                {
                    string nm = NodeName(n).ToLowerInvariant();
                    bool core = false;
                    foreach (var w in HeadWords) if (nm.Contains(w)) core = true;
                    foreach (var w in TorsoWords) if (nm.Contains(w)) core = true;
                    if (!core) match.Add(n);
                }
            }
            if (match.Count == 0) match = nodes;
            DamageNode pick = match[Rng.Next(match.Count)];
            part = NodeName(pick);
            return pick;
        }

        private static Vector3 NodeCenter(DamageNode n)
        {
            try
            {
                Collider c = n.GetCollider;
                if (c) return c.bounds.center;
            }
            catch { }
            return n.transform.position;
        }

        private static void LogOnce(string key, string msg)
        {
            if (Logged.Add(key)) RLog.Warning($"{CharacterReplacement.Tag} [Combat] [Hit] {msg}");
        }

        internal static void Clear()
        {
            Checks.Clear();
            NodeCache.Clear();
            CombatHitFx.Clear();
            _reactOnly = false;
            _noEffect = 0;
        }
    }
}
