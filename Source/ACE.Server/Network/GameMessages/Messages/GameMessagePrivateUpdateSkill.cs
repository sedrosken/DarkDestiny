using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessagePrivateUpdateSkill : GameMessage
    {
        public GameMessagePrivateUpdateSkill(WorldObject worldObject, CreatureSkill creatureSkill)
        : base(GameMessageOpcode.PrivateUpdateSkill, GameMessageGroup.UIQueue, 37)
        {
            Writer.Write(worldObject.Sequences.GetNextSequence(Sequence.SequenceType.UpdateSkill, creatureSkill.Skill));

            ushort adjustPP = 1;            // If this is not 0, it appears to trigger the initLevel to be treated as extra XP applied to the skill

            Writer.Write((uint)creatureSkill.Skill);
            Writer.Write(creatureSkill.Ranks);
            Writer.Write(adjustPP);
            Writer.Write((uint)creatureSkill.AdvancementClass);
            Writer.Write(creatureSkill.ExperienceSpent);

            // Client rebuilds the displayed skill from attributes + InitLevel + Ranks.
            // Armor-type set bonuses, gear bonuses, and the Melee/Missile/Magic mastery
            // augmentations only exist in CreatureSkill.Current on the server, so fold
            // them into the InitLevel sent on the wire so the skill panel matches combat.
            var initLevel = creatureSkill.InitLevel;
            if (worldObject is Player player)
            {
                initLevel += (uint)GetArmorTypeSkillBonus(player, creatureSkill.Skill);
                initLevel += (uint)GetGearSkillBonus(player, creatureSkill.Skill);
                initLevel += (uint)GetSkilledMasteryBonus(player, creatureSkill.Skill);
            }

            Writer.Write(initLevel);
            Writer.Write(creatureSkill.PropertiesSkill.ResistanceAtLastCheck);
            Writer.Write(creatureSkill.PropertiesSkill.LastUsedTime);
        }

        private static int GetArmorTypeSkillBonus(Player player, Skill skill)
        {
            switch (skill)
            {
                case Skill.MeleeDefense:
                    return player.GetArmorTypeBonus(Creature.ArmorTypeBonus.MeleeDefense);
                case Skill.MissileDefense:
                    return player.GetArmorTypeBonus(Creature.ArmorTypeBonus.MissileDefense);
                case Skill.MagicDefense:
                    return player.GetArmorTypeBonus(Creature.ArmorTypeBonus.MagicDefense);
                case Skill.LifeMagic:
                case Skill.WarMagic:
                case Skill.CreatureEnchantment:
                case Skill.ItemEnchantment:
                case Skill.VoidMagic:
                    return player.GetArmorTypeBonus(Creature.ArmorTypeBonus.MagicSkills);
                default:
                    return 0;
            }
        }

        private static int GetGearSkillBonus(Player player, Skill skill)
        {
            switch (skill)
            {
                case Skill.ItemTinkering:
                case Skill.WeaponTinkering:
                case Skill.ArmorTinkering:
                case Skill.MagicItemTinkering:
                    return player.GetGearTinkeringSkill();
                case Skill.Cooking:
                case Skill.Fletching:
                case Skill.Alchemy:
                    return player.GetGearCraftingSkill();
                case Skill.CreatureEnchantment:
                case Skill.ItemEnchantment:
                case Skill.LifeMagic:
                case Skill.VoidMagic:
                case Skill.WarMagic:
                    return player.GetGearMagicSkill();
                default:
                    return 0;
            }
        }

        // Mirrors CreatureSkill.GetAugBonus_Base()'s Melee/Missile/Magic branch (+10 per stack),
        // so the client-displayed skill matches the Current value used in DamageEvent combat math.
        private static int GetSkilledMasteryBonus(Player player, Skill skill)
        {
            if (player.AugmentationSkilledMelee > 0 && Player.MeleeSkills.Contains(skill))
                return player.AugmentationSkilledMelee * 10;
            if (player.AugmentationSkilledMissile > 0 && Player.MissileSkills.Contains(skill))
                return player.AugmentationSkilledMissile * 10;
            if (player.AugmentationSkilledMagic > 0 && Player.MagicSkills.Contains(skill))
                return player.AugmentationSkilledMagic * 10;

            return 0;
        }
    }
}
