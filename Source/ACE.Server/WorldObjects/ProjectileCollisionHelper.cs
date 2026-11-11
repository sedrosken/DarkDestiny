using System;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Helper class for arrows / bolts / thrown weapons
    /// outside of the WorldObject hierarchy
    /// </summary>
    public static class ProjectileCollisionHelper
    {
        public static void OnCollideObject(WorldObject worldObject, WorldObject target)
        {
            if (!worldObject.PhysicsObj.is_active()) return;

            //Console.WriteLine($"Projectile.OnCollideObject - {WorldObject.Name} ({WorldObject.Guid}) -> {target.Name} ({target.Guid})");

            if (worldObject.ProjectileTarget == null || worldObject.ProjectileTarget != target)
            {
                //Console.WriteLine("Unintended projectile target! (should be " + ProjectileTarget.Guid.Full.ToString("X8") + " - " + ProjectileTarget.Name + ")");
                OnCollideEnvironment(worldObject);
                return;
            }

            // take damage
            var sourceCreature = worldObject.ProjectileSource as Creature;
            var sourcePlayer = worldObject.ProjectileSource as Player;
            var targetCreature = target as Creature;

            DamageEvent damageEvent = null;

            if (targetCreature != null && targetCreature.IsAlive)
            {
                if (sourcePlayer != null)
                {
                    sourcePlayer.EndSneaking();

                    // player damage monster or player
                    damageEvent = sourcePlayer.DamageTarget(targetCreature, worldObject);

                    if (damageEvent != null && damageEvent.HasDamage)
                    {
                        worldObject.EnqueueBroadcast(new GameMessageSound(worldObject.Guid, Sound.Collision, 1.0f));

                        if(!(target is Player) && !damageEvent.Evaded && (worldObject.WeenieType == WeenieType.Missile || worldObject.WeenieType == WeenieType.Ammunition) && Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM)
                        {
                            var rng = ThreadSafeRandom.NextInterval(0);
                            if (rng < 0.25) // 25% chance the ammo will be preserved
                            {
                                int currentAmount;
                                if (targetCreature.ammoHitWith.TryGetValue(worldObject.WeenieClassId, out currentAmount))
                                    currentAmount++;
                                else
                                    currentAmount = 1;
                                targetCreature.ammoHitWith[worldObject.WeenieClassId] = currentAmount;
                            }
                        }

                        // Obsidian "Arrow Split" imbue: on a damaging player missile hit, if the
                        // launcher (bow/crossbow/atlatl) or a thrown weapon carries the split imbue,
                        // deal one extra hit to a creature standing next to the struck target.
                        // Intentionally NOT ruleset-gated (this server runs Infiltration, not CustomDM).
                        // Prestige Marksman's Crystal 6-piece adds +1 split target (stacks with the imbue).
                        var splitSource = worldObject.ProjectileLauncher ?? worldObject;
                        var arrowSplitTargets = 0;
                        if (splitSource != null && (splitSource.GetProperty(PropertyInt.ArrowSplitImbue) ?? 0) == 1)
                            arrowSplitTargets++;
                        arrowSplitTargets += sourcePlayer.GetSetCrystalArrowSplitBonus();

                        if (arrowSplitTargets > 0
                            && (worldObject.WeenieType == WeenieType.Missile || worldObject.WeenieType == WeenieType.Ammunition))
                        {
                            foreach (var splitTarget in sourcePlayer.GetProjectileSplitTargets(targetCreature, arrowSplitTargets))
                                sourcePlayer.DamageTarget(splitTarget, worldObject);
                        }
                    }
                }
                else if (sourceCreature is CombatPet missilePet)
                {
                    // creature pet system: a pet's thrown/fired missile -- same treatment as a pet
                    // melee hit (Monster_Melee): never hurts players, never hurts other pets outside
                    // a /pet battle, scaled by pet level, "/pet damage" readout, and the struck
                    // monster is provoked into fighting the pet. Doesn't depend on the pet's
                    // AttackTarget still being set when the projectile lands.
                    if (targetCreature is Player || !missilePet.CanDamage(targetCreature))
                    {
                        // pass harmlessly
                    }
                    else
                    {
                        damageEvent = DamageEvent.CalculateDamage(missilePet, targetCreature, worldObject);

                        targetCreature.OnAttackReceived(missilePet, CombatType.Missile, damageEvent.IsCritical, damageEvent.Evaded);

                        if (damageEvent.HasDamage)
                        {
                            var petDmg = damageEvent.Damage * missilePet.PetPowerFactor();

                            targetCreature.TakeDamage(missilePet, damageEvent.DamageType, petDmg);
                            Pet.TryReportPetDamage(missilePet, targetCreature, petDmg, damageEvent.DamageType, damageEvent.IsCritical, worldObject.Name);
                            missilePet.EmitSplatter(targetCreature, petDmg);

                            if (missilePet.GetCreatureSkill(Skill.DirtyFighting).AdvancementClass >= SkillAdvancementClass.Trained)
                                missilePet.FightDirty(targetCreature, damageEvent.Weapon);
                        }
                        else
                            targetCreature.OnEvade(missilePet, CombatType.Missile);

                        missilePet.PetOnAttackMonster(targetCreature);
                    }
                }
                else if (sourceCreature != null && sourceCreature.AttackTarget != null)
                {
                    // todo: clean this up
                    var targetPlayer = sourceCreature.AttackTarget as Player;

                    damageEvent = DamageEvent.CalculateDamage(sourceCreature, targetCreature, worldObject);

                    targetCreature.OnAttackReceived(sourceCreature, CombatType.Missile, damageEvent.IsCritical, damageEvent.Evaded);

                    if (targetPlayer != null)
                    {
                        // monster damage player
                        if (damageEvent.HasDamage)
                        {
                            targetPlayer.TakeDamage(sourceCreature, damageEvent);

                            // blood splatter?

                            if (damageEvent.ShieldMod != 1.0f)
                            {
                                var shieldSkill = targetPlayer.GetCreatureSkill(Skill.Shield);
                                Proficiency.OnSuccessUse(targetPlayer, shieldSkill, shieldSkill.Current);   // ??
                            }

                            // handle Dirty Fighting
                            if (sourceCreature.GetCreatureSkill(Skill.DirtyFighting).AdvancementClass >= SkillAdvancementClass.Trained)
                                sourceCreature.FightDirty(targetPlayer, damageEvent.Weapon);
                        }
                        else
                            targetPlayer.OnEvade(sourceCreature, CombatType.Missile);
                    }
                    else
                    {
                        // monster damage pet
                        if (damageEvent.HasDamage)
                        {
                            targetCreature.TakeDamage(sourceCreature, damageEvent.DamageType, damageEvent.Damage);

                            // blood splatter?

                            // handle Dirty Fighting
                            if (sourceCreature.GetCreatureSkill(Skill.DirtyFighting).AdvancementClass >= SkillAdvancementClass.Trained)
                                sourceCreature.FightDirty(targetCreature, damageEvent.Weapon);
                        }

                        if (!(targetCreature is CombatPet))
                        {
                            // faction mobs and foetype
                            sourceCreature.MonsterOnAttackMonster(targetCreature);
                        }
                    }
                }

                // handle target procs
                if (damageEvent != null && damageEvent.HasDamage)
                {
                    bool threadSafe = true;

                    if (LandblockManager.CurrentlyTickingLandblockGroupsMultiThreaded)
                    {
                        // Ok... if we got here, we're likely in the parallel landblock physics processing.
                        if (worldObject.CurrentLandblock == null || sourceCreature.CurrentLandblock == null || targetCreature.CurrentLandblock == null || worldObject.CurrentLandblock.CurrentLandblockGroup != sourceCreature.CurrentLandblock.CurrentLandblockGroup || sourceCreature.CurrentLandblock.CurrentLandblockGroup != targetCreature.CurrentLandblock.CurrentLandblockGroup)
                            threadSafe = false;
                    }

                    if (threadSafe)
                        // This can result in spell projectiles being added to either sourceCreature or targetCreature landblock.
                        // worldObject is hitting targetCreature, so they should almost always be in the same landblock
                        worldObject.TryProcEquippedItems(sourceCreature, targetCreature, false, worldObject.ProjectileLauncher);
                    else
                    {
                        // sourceCreature and creatureTarget are now in different landblock groups.
                        // What has likely happened is that sourceCreature sent a projectile toward creatureTarget. Before impact, sourceCreature was teleported away.
                        // To perform this fully thread safe, we would enqueue the work onto worldManager.
                        // WorldManager.EnqueueAction(new ActionEventDelegate(() => sourceCreature.TryProcEquippedItems(targetCreature, false)));
                        // But, to keep it simple, we will just ignore it and not bother with TryProcEquippedItems for this particular impact.
                    }
                }
            }

            worldObject.CurrentLandblock?.RemoveWorldObject(worldObject.Guid, showError: !worldObject.PhysicsObj.entering_world);
            worldObject.PhysicsObj.set_active(false);

            worldObject.HitMsg = true;
        }

        public static void OnCollideEnvironment(WorldObject worldObject)
        {
            if (!worldObject.PhysicsObj.is_active()) return;

            // do not send 'Your missile attack hit the environment' messages to player,
            // if projectile is still in the process of spawning into world.
            if (worldObject.PhysicsObj.entering_world)
                return;

            //Console.WriteLine($"Projectile.OnCollideEnvironment({WorldObject.Name} - {WorldObject.Guid})");

            worldObject.CurrentLandblock?.RemoveWorldObject(worldObject.Guid, showError: !worldObject.PhysicsObj.entering_world);
            worldObject.PhysicsObj.set_active(false);

            if (worldObject.ProjectileSource is Player player)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("Your missile attack hit the environment.", ChatMessageType.Broadcast));
            }
            else if (worldObject.ProjectileSource is Creature creature)
            {
                creature.MonsterProjectile_OnCollideEnvironment();
            }

            worldObject.HitMsg = true;
        }
    }
}
