using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Prestige set crystals. Crystal type is stored in PropertyInt.SetCrystal on both the crystal
    /// item and the armor/clothing/jewelry it was imbued into. Set count = number of EQUIPPED items
    /// imbued with the same crystal (max 6, Dedication/Ascetic max 9).
    /// </summary>
    public enum SetCrystalType
    {
        None            = 0,
        Guardian        = 1,    // Defender's set
        Wizard          = 2,    // Wise set
        Sorcerer        = 3,    // Adept set
        Marksman        = 4,    // Archer's set
        Ascetic         = 5,    // Dedication set
        Acrobat         = 6,    // Dexterous set
        Titan           = 7,    // Hearty set
        Warrior         = 8,    // Soldier's set
        Rogue           = 9,    // Swift set
        Gaerlan         = 10,   // Elemental set
        Diemos          = 11,   // Foundation set
        HeroicGuardian  = 12,   // not a set: +10 DR / +10 CDR on armor or shield
        HeroicDestroyer = 13,   // not a set: +10 D / +10 CD on weapon or caster
    }

    partial class Creature
    {
        public const int SetCrystalMaxSetType = 11;

        // index = SetCrystalType (1-11), value = number of equipped items with that crystal
        private readonly int[] setCrystalCounts = new int[SetCrystalMaxSetType + 1];

        private void AddItemToSetCrystalCache(WorldObject wo)
        {
            var type = wo.GetProperty(PropertyInt.SetCrystal) ?? 0;
            if (type >= 1 && type <= SetCrystalMaxSetType)
                setCrystalCounts[type]++;
        }

        private void RemoveItemFromSetCrystalCache(WorldObject wo)
        {
            var type = wo.GetProperty(PropertyInt.SetCrystal) ?? 0;
            if (type >= 1 && type <= SetCrystalMaxSetType && setCrystalCounts[type] > 0)
                setCrystalCounts[type]--;
        }

        public static string GetSetCrystalName(SetCrystalType type)
        {
            switch (type)
            {
                case SetCrystalType.Guardian: return "Guardian's Crystal";
                case SetCrystalType.Wizard: return "Wizard's Crystal";
                case SetCrystalType.Sorcerer: return "Sorcerer's Crystal";
                case SetCrystalType.Marksman: return "Marksman's Crystal";
                case SetCrystalType.Ascetic: return "Ascetic's Crystal";
                case SetCrystalType.Acrobat: return "Acrobat's Crystal";
                case SetCrystalType.Titan: return "Titan's Crystal";
                case SetCrystalType.Warrior: return "Warrior's Crystal";
                case SetCrystalType.Rogue: return "Rogue's Crystal";
                case SetCrystalType.Gaerlan: return "Gaerlan's Crystal";
                case SetCrystalType.Diemos: return "Diemos' Crystal";
                case SetCrystalType.HeroicGuardian: return "Heroic Guardian Crystal";
                case SetCrystalType.HeroicDestroyer: return "Heroic Destroyer Crystal";
                default: return "Crystal";
            }
        }

        public static string GetSetName(SetCrystalType type)
        {
            switch (type)
            {
                case SetCrystalType.Guardian: return "Defender's Set";
                case SetCrystalType.Wizard: return "Wise Set";
                case SetCrystalType.Sorcerer: return "Adept Set";
                case SetCrystalType.Marksman: return "Archer's Set";
                case SetCrystalType.Ascetic: return "Dedication Set";
                case SetCrystalType.Acrobat: return "Dexterous Set";
                case SetCrystalType.Titan: return "Hearty Set";
                case SetCrystalType.Warrior: return "Soldier's Set";
                case SetCrystalType.Rogue: return "Swift Set";
                case SetCrystalType.Gaerlan: return "Elemental Set";
                case SetCrystalType.Diemos: return "Foundation Set";
                default: return null;
            }
        }

        /// <summary>
        /// Text appended to an item's description when the crystal is imbued.
        /// </summary>
        public static string GetSetCrystalItemDesc(SetCrystalType type)
        {
            const string tiers = "Set 2: +3, Set 3: +5, Set 4: +10, Set 5: +20, Set 6: +30";
            const string pctTiers = "Set 2: +3%, Set 3: +6%, Set 4: +9%, Set 5: +12%, Set 6: +15%";

            switch (type)
            {
                case SetCrystalType.Guardian: return $"Defender's Set: +Magic Defense, Melee Defense, Missile Defense ({tiers}). 6 pieces: +5% chance to negate critical hits.";
                case SetCrystalType.Wizard: return $"Wise Set: +Focus, Self, Mana Conversion, Maximum Mana, % Mana Regeneration ({tiers}). 6 pieces: +10% Magic Absorb.";
                case SetCrystalType.Sorcerer: return $"Adept Set: +Creature Enchantment, Item Enchantment, Life Magic, War Magic ({tiers}). 6 pieces: +1 Spell Chain target.";
                case SetCrystalType.Marksman: return $"Archer's Set: +Bow, Crossbow, Thrown Weapons, Assess Person ({tiers}). 6 pieces: +1 Arrow Split target.";
                case SetCrystalType.Ascetic: return "Dedication Set: +Strength, Endurance, Coordination, Quickness, Focus, Self (Set 2: +3, Set 3: +6, Set 4: +9, Set 5: +12, Set 6: +15, Set 7: +18, Set 8: +21, Set 9: +24).";
                case SetCrystalType.Acrobat: return $"Dexterous Set: +Coordination, Quickness, Jump, Maximum Stamina, % Stamina Regeneration ({tiers}). 6 pieces: +5% to all crafting interactions.";
                case SetCrystalType.Titan: return $"Hearty Set: +Endurance, Strength, Healing, Maximum Health, % Health Regeneration ({tiers}). 6 pieces: +20% Burden Bearing.";
                case SetCrystalType.Warrior: return $"Soldier's Set: +Axe, Mace, Spear, Sword, Dirty Fighting, Recklessness, Shield ({tiers}). 6 pieces: +1 Cleave target.";
                case SetCrystalType.Rogue: return $"Swift Set: +Dagger, Staff, Unarmed Combat, Deception, Dual Wield, Lockpick, Run, Sneak Attack ({tiers}). 6 pieces: +15% Critical Chance.";
                case SetCrystalType.Gaerlan: return $"Elemental Set: +Acid, Cold, Fire, Lightning Protection ({pctTiers}).";
                case SetCrystalType.Diemos: return $"Foundation Set: +Slash, Pierce, Bludgeon Protection, +Natural Armor ({pctTiers}).";
                case SetCrystalType.HeroicGuardian: return "Heroic Guardian Crystal: +10 Damage Reduction Rating, +10 Critical Damage Reduction Rating.";
                case SetCrystalType.HeroicDestroyer: return "Heroic Destroyer Crystal: +10 Damage Rating, +10 Critical Damage Rating.";
                default: return null;
            }
        }

        public static int GetSetCrystalMaxPieces(SetCrystalType type) => type == SetCrystalType.Ascetic ? 9 : 6;

        /// <summary>
        /// Number of equipped items carrying this set crystal, capped at the set's max pieces. Players only.
        /// </summary>
        public int GetSetCrystalCount(SetCrystalType type)
        {
            if (!(this is Player))
                return 0;

            var t = (int)type;
            if (t < 1 || t > SetCrystalMaxSetType)
                return 0;

            return Math.Min(setCrystalCounts[t], GetSetCrystalMaxPieces(type));
        }

        public bool HasFullSetCrystal(SetCrystalType type) => GetSetCrystalCount(type) >= 6;

        /// <summary>
        /// The +X amount for the set's listed stats at the current piece count.
        /// Standard sets: 2=+3, 3=+5, 4=+10, 5=+20, 6=+30.
        /// Ascetic (Dedication), Gaerlan, Diemos: +3 per piece past the first (2=+3 ... 6=+15, Ascetic 9=+24).
        /// </summary>
        public int GetSetCrystalAmount(SetCrystalType type)
        {
            var n = GetSetCrystalCount(type);
            if (n < 2)
                return 0;

            switch (type)
            {
                case SetCrystalType.Ascetic:
                case SetCrystalType.Gaerlan:
                case SetCrystalType.Diemos:
                    return 3 * (n - 1);

                default:
                    switch (n)
                    {
                        case 2: return 3;
                        case 3: return 5;
                        case 4: return 10;
                        case 5: return 20;
                        default: return 30;
                    }
            }
        }

        public int GetSetCrystalSkillBonus(Skill skill)
        {
            if (!(this is Player))
                return 0;

            switch (skill)
            {
                case Skill.MeleeDefense:
                case Skill.MissileDefense:
                case Skill.MagicDefense:
                    return GetSetCrystalAmount(SetCrystalType.Guardian);

                case Skill.ManaConversion:
                    return GetSetCrystalAmount(SetCrystalType.Wizard);

                case Skill.CreatureEnchantment:
                case Skill.ItemEnchantment:
                case Skill.LifeMagic:
                case Skill.WarMagic:
                    return GetSetCrystalAmount(SetCrystalType.Sorcerer);

                case Skill.Bow:
                case Skill.Crossbow:
                case Skill.ThrownWeapon:
                case Skill.AssessPerson:
                    return GetSetCrystalAmount(SetCrystalType.Marksman);

                case Skill.Jump:
                    return GetSetCrystalAmount(SetCrystalType.Acrobat);

                case Skill.Healing:
                    return GetSetCrystalAmount(SetCrystalType.Titan);

                case Skill.Axe:
                case Skill.Mace:
                case Skill.Spear:
                case Skill.Sword:
                case Skill.DirtyFighting:
                case Skill.Recklessness:
                case Skill.Shield:
                    return GetSetCrystalAmount(SetCrystalType.Warrior);

                case Skill.Dagger:
                case Skill.Staff:
                case Skill.UnarmedCombat:
                case Skill.Deception:
                case Skill.DualWield:
                case Skill.Lockpick:
                case Skill.Run:
                case Skill.SneakAttack:
                    return GetSetCrystalAmount(SetCrystalType.Rogue);

                default:
                    return 0;
            }
        }

        public int GetSetCrystalAttributeBonus(PropertyAttribute attribute)
        {
            if (!(this is Player))
                return 0;

            var total = GetSetCrystalAmount(SetCrystalType.Ascetic);

            switch (attribute)
            {
                case PropertyAttribute.Strength:
                case PropertyAttribute.Endurance:
                    total += GetSetCrystalAmount(SetCrystalType.Titan);
                    break;
                case PropertyAttribute.Coordination:
                case PropertyAttribute.Quickness:
                    total += GetSetCrystalAmount(SetCrystalType.Acrobat);
                    break;
                case PropertyAttribute.Focus:
                case PropertyAttribute.Self:
                    total += GetSetCrystalAmount(SetCrystalType.Wizard);
                    break;
                default:
                    return 0;
            }
            return total;
        }

        /// <summary>
        /// Flat +max vital: Titan = Health, Acrobat = Stamina, Wizard = Mana.
        /// </summary>
        public int GetSetCrystalVitalBonus(PropertyAttribute2nd vital)
        {
            if (!(this is Player))
                return 0;

            switch (vital)
            {
                case PropertyAttribute2nd.MaxHealth: return GetSetCrystalAmount(SetCrystalType.Titan);
                case PropertyAttribute2nd.MaxStamina: return GetSetCrystalAmount(SetCrystalType.Acrobat);
                case PropertyAttribute2nd.MaxMana: return GetSetCrystalAmount(SetCrystalType.Wizard);
                default: return 0;
            }
        }

        /// <summary>
        /// Regeneration speed bonus as a fraction (0.30 = +30%): Titan = Health, Acrobat = Stamina, Wizard = Mana.
        /// </summary>
        public float GetSetCrystalRegenBonus(PropertyAttribute2nd vital)
        {
            return GetSetCrystalVitalBonus(vital) * 0.01f;
        }

        /// <summary>
        /// Protection bonus as a fraction (0.15 = +15%): Gaerlan = acid/cold/fire/lightning, Diemos = slash/pierce/bludgeon.
        /// </summary>
        public float GetSetCrystalProtectionBonus(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.Acid:
                case DamageType.Cold:
                case DamageType.Fire:
                case DamageType.Electric:
                    return GetSetCrystalAmount(SetCrystalType.Gaerlan) * 0.01f;
                case DamageType.Slash:
                case DamageType.Pierce:
                case DamageType.Bludgeon:
                    return GetSetCrystalAmount(SetCrystalType.Diemos) * 0.01f;
                default:
                    return 0.0f;
            }
        }

        /// <summary>Diemos: +% armor level on each body part (fraction).</summary>
        public float GetSetCrystalNaturalArmorBonus() => GetSetCrystalAmount(SetCrystalType.Diemos) * 0.01f;

        // ===== 6-piece specials =====

        /// <summary>Guardian 6: chance an incoming critical hit becomes a normal hit.</summary>
        public float GetSetCrystalNegateCritChance() => HasFullSetCrystal(SetCrystalType.Guardian) ? 0.05f : 0.0f;

        /// <summary>Wizard 6: multiplier on incoming spell damage (0.9 = 10% absorbed).</summary>
        public float GetSetCrystalMagicAbsorbMod() => HasFullSetCrystal(SetCrystalType.Wizard) ? 0.90f : 1.0f;

        /// <summary>Sorcerer 6: extra Spell Chain targets.</summary>
        public int GetSetCrystalSpellChainBonus() => HasFullSetCrystal(SetCrystalType.Sorcerer) ? 1 : 0;

        /// <summary>Marksman 6: extra Arrow Split targets.</summary>
        public int GetSetCrystalArrowSplitBonus() => HasFullSetCrystal(SetCrystalType.Marksman) ? 1 : 0;

        /// <summary>Acrobat 6: added to all crafting success chances (incl. tinkering and crystal imbues).</summary>
        public float GetSetCrystalCraftingBonus() => HasFullSetCrystal(SetCrystalType.Acrobat) ? 0.05f : 0.0f;

        /// <summary>Titan 6: percentage points of burden reduction (same units as GearCarryingCapacity).</summary>
        public int GetSetCrystalBurdenBonus() => HasFullSetCrystal(SetCrystalType.Titan) ? 20 : 0;

        /// <summary>Warrior 6: extra Cleave targets on melee attacks.</summary>
        public int GetSetCrystalCleaveBonus() => HasFullSetCrystal(SetCrystalType.Warrior) ? 1 : 0;

        /// <summary>Rogue 6: added straight onto critical chance (melee, missile, spells).</summary>
        public float GetSetCrystalCritChanceBonus() => HasFullSetCrystal(SetCrystalType.Rogue) ? 0.15f : 0.0f;
    }

    partial class Player
    {
        /// <summary>
        /// Called after an item carrying PropertyInt.SetCrystal is equipped or unequipped.
        /// Refreshes the client panels the set touches and reports the new piece count.
        /// </summary>
        public void OnSetCrystalItemEquipChanged(WorldObject item)
        {
            var type = (SetCrystalType)(item.GetProperty(PropertyInt.SetCrystal) ?? 0);

            var setName = GetSetName(type);
            if (setName == null)
                return; // heroic crystals: handled by the gear rating cache

            // attributes (client derives skills + vitals from these)
            foreach (var attr in Attributes.Values)
                Session.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(this, attr));

            // vitals (clamp current down if max dropped)
            foreach (var vital in new[] { Health, Stamina, Mana })
            {
                if (vital.Current > vital.MaxValue)
                    vital.Current = vital.MaxValue;
                Session.Network.EnqueueSend(new GameMessagePrivateUpdateVital(this, vital));
            }

            // skills
            foreach (var skill in Skills.Values)
            {
                if (GetSetCrystalSkillBonus(skill.Skill) != 0 || IsSetCrystalSkill(type, skill.Skill))
                    Session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(this, skill));
            }

            if (type == SetCrystalType.Titan)
                SendEffectiveCarryingCapacityAugs();

            var count = GetSetCrystalCount(type);
            var amount = GetSetCrystalAmount(type);
            var pct = type == SetCrystalType.Gaerlan || type == SetCrystalType.Diemos ? "%" : "";

            var msg = $"{setName}: {count} of {GetSetCrystalMaxPieces(type)} pieces equipped";
            msg += amount > 0 ? $" (+{amount}{pct})." : " (no bonus yet - 2 pieces needed).";
            if (count >= 6 && type != SetCrystalType.Ascetic && type != SetCrystalType.Gaerlan && type != SetCrystalType.Diemos)
                msg += " 6-piece bonus active!";

            SendMessage(msg, ChatMessageType.Broadcast);
        }

        private static bool IsSetCrystalSkill(SetCrystalType type, Skill skill)
        {
            // used to also refresh skills when a set DROPS to 0 bonus
            switch (type)
            {
                case SetCrystalType.Guardian: return skill == Skill.MeleeDefense || skill == Skill.MissileDefense || skill == Skill.MagicDefense;
                case SetCrystalType.Wizard: return skill == Skill.ManaConversion;
                case SetCrystalType.Sorcerer: return skill == Skill.CreatureEnchantment || skill == Skill.ItemEnchantment || skill == Skill.LifeMagic || skill == Skill.WarMagic;
                case SetCrystalType.Marksman: return skill == Skill.Bow || skill == Skill.Crossbow || skill == Skill.ThrownWeapon || skill == Skill.AssessPerson;
                case SetCrystalType.Acrobat: return skill == Skill.Jump;
                case SetCrystalType.Titan: return skill == Skill.Healing;
                case SetCrystalType.Warrior: return skill == Skill.Axe || skill == Skill.Mace || skill == Skill.Spear || skill == Skill.Sword || skill == Skill.DirtyFighting || skill == Skill.Recklessness || skill == Skill.Shield;
                case SetCrystalType.Rogue: return skill == Skill.Dagger || skill == Skill.Staff || skill == Skill.UnarmedCombat || skill == Skill.Deception || skill == Skill.DualWield || skill == Skill.Lockpick || skill == Skill.Run || skill == Skill.SneakAttack;
                default: return false;
            }
        }
    }
}
