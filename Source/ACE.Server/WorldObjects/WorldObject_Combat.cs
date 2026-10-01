using System.Collections.Generic;
using System.Linq;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class WorldObject
    {
        /// <summary>
        /// Determines if WorldObject can damage a target via PlayerKillerStatus
        /// </summary>
        /// <returns>null if no errors, else pk error list</returns>
        public virtual List<WeenieErrorWithString> CheckPKStatusVsTarget(WorldObject target, Spell spell)
        {
            // no restrictions here
            // player attacker restrictions handled in override
            return null;
        }

        /// <summary>
        /// Tries to proc any relevant items for the attack
        /// </summary>
        public void TryProcEquippedItems(WorldObject attacker, Creature target, bool selfTarget, WorldObject weapon, DamageEvent damageEvent = null)
        {
            // handle procs directly on this item -- ie. phials
            // this could also be monsters with the proc spell directly on the creature
            if (HasProc && ProcSpellSelfTargeted == selfTarget)
            {
                // projectile
                // monster
                TryProcItem(attacker, target, selfTarget);
            }

            // handle proc spells for weapon
            // this could be a melee weapon, or a missile launcher
            // for thrown weapons (ie. phials), weapon is ProjectileLauncher -- the still-equipped
            // original of the same weenie as this flying projectile clone, carrying the identical
            // ProcSpell already fired above. Skip it here so thrown weapons don't proc twice per hit.
            if (weapon != null && weapon != this && weapon.WeenieClassId != WeenieClassId
                && weapon.HasProc && weapon.ProcSpellSelfTargeted == selfTarget)
            {
                // weapon
                weapon.TryProcItem(attacker, target, selfTarget);
            }

            if (attacker != this && attacker.HasProc && attacker.ProcSpellSelfTargeted == selfTarget)
            {
                // handle special case -- missile projectiles from monsters w/ a proc directly on the mob
                // monster
                attacker.TryProcItem(attacker, target, selfTarget);
            }

            // handle procs on equipped items -- aetheria, plus any custom
            // trinket/jewelry/gem that carries its own ProcSpell (e.g. WCID 9870775).
            // Original stock code gated this to Aetheria.IsAetheria() only; widened so
            // any worn item with a proc fires, while the ProcSpellSelfTargeted == selfTarget
            // check still routes self-buffs to the wielder and offensive procs to the target.
            if (attacker is Creature wielder)
            {
                var equippedProcItems = wielder.EquippedObjects.Values.Where(i => i.HasProc && i.ProcSpellSelfTargeted == selfTarget);

                foreach (var procItem in equippedProcItems)
                    procItem.TryProcItem(attacker, target, selfTarget);
            }
        }
    }
}
