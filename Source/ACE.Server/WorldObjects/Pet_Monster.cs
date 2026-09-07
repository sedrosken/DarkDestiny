using ACE.Entity.Enum;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Handles pets provoking monsters
    /// </summary>
    partial class CombatPet
    {
        /// <summary>
        /// Disabled: pets no longer proactively wake nearby monsters onto themselves.
        /// Monsters ignore the pet unless it attacks them (see Monster_Awareness and
        /// PetOnAttackMonster below). Still called each tick from Monster_Tick -- kept as a
        /// no-op so that call site doesn't need to change.
        /// </summary>
        public void PetCheckMonsters()
        {
        }

        /// <summary>
        /// Called when a combat pet attacks a monster. The struck monster wakes and retaliates
        /// against the pet, and is flagged as a retaliate target so it keeps treating the pet
        /// as a valid target (monsters otherwise skip pets entirely in their target scans).
        /// </summary>
        public void PetOnAttackMonster(Creature monster)
        {
            monster.AddRetaliateTarget(this);

            if (monster.MonsterState == State.Idle && (monster.Tolerance & PlayerCombatPet_RetaliateExclude) == 0)
            {
                monster.AttackTarget = this;
                monster.WakeUp();
            }
        }
    }
}
