using System.Linq;

using ACE.Database;
using ACE.Entity.Enum;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Creature pet system: weapon helpers for combat pets. Lives in the Creature partial so it
    /// can reach the private weapon-switch state in Monster_Missile.cs.
    /// </summary>
    partial class Creature
    {
        // when a combat pet last had a target (for dropping back to peace stance when idle)
        private double petLastFightTime;

        // how long a pet must be idle before it lowers its weapon
        private const double PetPeaceDelay = 4.0;

        /// <summary>
        /// Combat pet with nothing to fight: after PetPeaceDelay seconds, go back to peace
        /// stance, and re-arm firstUpdate so the next fight raises the attack stance again.
        /// </summary>
        private void PetTryPeaceStance(double currentUnixTime)
        {
            if (CurrentMotionState.Stance == MotionStance.NonCombat || IsAnimating || IsSwitchingWeapons || IsSwitchWeaponsPending)
                return;
            if (currentUnixTime - petLastFightTime < PetPeaceDelay)
                return;

            SetCombatMode(CombatMode.NonCombat);
            CurrentAttackType = null;
            firstUpdate = true;
        }

        /// <summary>
        /// True if the creature is carrying (not wielding) a ranged weapon it could actually use:
        /// a thrown weapon, or a launcher with matching ammo.
        /// </summary>
        public bool PetHasUsableRangedInInventory()
        {
            foreach (var item in Inventory.Values)
            {
                if (item.WeenieType == WeenieType.Missile)
                    return true;

                if (item.WeenieType == WeenieType.MissileLauncher
                    && Inventory.Values.Any(a => a.WeenieType == WeenieType.Ammunition && a.AmmoType == item.AmmoType))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Ask a combat pet to put its ranged weapon away and fight in melee (with a melee weapon
        /// if it carries one, otherwise unarmed). No-op if it's already in melee.
        /// </summary>
        public void PetRequestMelee()
        {
            if (!IsRanged || IsSwitchingWeapons || IsSwitchWeaponsPending)
                return;

            TrySwitchToMeleeAttack();
        }

        /// <summary>
        /// Ask a combat pet to swap to a ranged weapon it carries. Returns false if it has none.
        /// </summary>
        public bool PetRequestMissile()
        {
            if (IsRanged)
                return true;

            if (!PetHasUsableRangedInInventory())
                return false;

            if (IsSwitchingWeapons || IsSwitchWeaponsPending)
                return true;

            HasRangedWeapon = true;   // stale flag otherwise -- only refreshed by GetMonsterInventory
            TrySwitchToMissileAttack();
            return true;
        }

        /// <summary>
        /// A monster's wielded weapons come from a random wielded-treasure roll, and a pet is a
        /// FRESH clone on every summon -- so a creature that is "usually" armed can come out
        /// empty-handed (or with a different weapon each summon). If this creature's wielded
        /// treasure table can give a weapon and the roll gave none, re-roll a few times.
        /// </summary>
        public void PetEnsureWeapon()
        {
            if (GetEquippedWeapon() != null || WieldedTreasure == null)
                return;

            var tableHasWeapon = WieldedTreasure.Any(t =>
            {
                var w = DatabaseManager.World.GetCachedWeenie(t.WeenieClassId);
                return w != null && (w.WeenieType == WeenieType.MeleeWeapon || w.WeenieType == WeenieType.Missile
                    || w.WeenieType == WeenieType.MissileLauncher || w.WeenieType == WeenieType.Caster);
            });
            if (!tableHasWeapon)
                return;

            for (var tries = 0; tries < 10 && GetEquippedWeapon() == null; tries++)
            {
                GenerateWieldedTreasure();
                EquipInventoryItems();   // non-broadcast wield: we are still pre-EnterWorld
            }
        }
    }
}
