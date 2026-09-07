using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// A summonable creature-clone that fights monsters for its owner.
    /// The hatched pet is spawned from the imprinted creature's own weenie (see
    /// PetDevice.SummonCreature), so it natively has that creature's attributes, health,
    /// skills, body-part damage and spells. Its combat power is then scaled by level via
    /// Pet.PetPowerFactor() (parent-equal at level 7). It uses the stock monster combat AI.
    /// </summary>
    public partial class CombatPet : Pet
    {
        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public CombatPet(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public CombatPet(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
        }

        public override bool? Init(Player player, PetDevice petDevice)
        {
            var success = base.Init(player, petDevice);

            if (success == null || !success.Value)
                return success;

            SetCombatMode(CombatMode.Melee);
            MonsterState = State.Awake;
            IsAwake = true;

            // Tolerance.Monster: the pet only hunts monsters -- it never targets players
            // (including its owner) or other pets. TargetingTactic Nearest|LastDamager: go
            // after the closest monster, or whatever hit it last.
            SetProperty(PropertyInt.Tolerance, (int)Tolerance.Monster);
            SetProperty(PropertyInt.TargetingTactic, (int)(TargetingTactic.Nearest | TargetingTactic.LastDamager));

            // copy ratings from pet device
            DamageRating = petDevice.GearDamage;
            DamageResistRating = petDevice.GearDamageResist;
            CritDamageRating = petDevice.GearCritDamage;
            CritDamageResistRating = petDevice.GearCritDamageResist;
            CritRating = petDevice.GearCrit;
            CritResistRating = petDevice.GearCritResist;

            // combat pets attack monsters of any faction; matching the owner's faction bits
            // keeps the pet from being aggroed as a faction enemy by the owner's allies.
            Faction1Bits = player.Faction1Bits;

            return true;
        }

        public override void Sleep()
        {
            // pets dont really go to sleep -- they keep scanning for new targets
            return;
        }
    }
}
