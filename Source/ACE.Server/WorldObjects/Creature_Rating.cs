using System;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables.Wcids;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        // Ratings:
        // http://asheron.wikia.com/wiki/Rating

        private static float GetRatingMod(int rating)
        {
            if (rating == 0) return 1.0f;

            if (rating >= 0)
                return GetPositiveRatingMod(rating);
            else
                return GetNegativeRatingMod(-rating);
        }

        /// <summary>
        /// Returns a 1.xx rating modifier by default,
        /// or a 0.xx rating modifier if negative
        /// </summary>
        public static float GetPositiveRatingMod(int rating)
        {
            if (rating < 0) return GetNegativeRatingMod(-rating);

            // formula: (100 + rating) / 100 = 1.xx modifier
            var ratingMod = (100 + rating) / 100.0f;
            return ratingMod;
        }

        /// <summary>
        /// Returns a 0.xx rating modifier by default,
        /// or a 1.xx rating modifier if negative
        /// </summary>
        public static float GetNegativeRatingMod(int rating, bool allowBug = false)
        {
            if (rating < 0 && !allowBug)
                return GetPositiveRatingMod(-rating);

            if (allowBug)
            {
                // with the bug allowed for DRR reduction from void dots,
                // this method will produce unbalanced modifiers for negative ratings
                // as negative rating approaches -100, it ramps up in a curve to infinity, eventually getting a divide by 0 crash for -100
                // values less than -100 would produce negative multipliers, which would result in undefined behavior throughout the system,
                // such as negative damage numbers. even with the bug enabled, we still limit to -99 on the lower end to prevent system failure

                rating = Math.Max(rating, -99);
            }

            // formula: 100 / (100 + rating) = 0.xx modifier
            var ratingMod = 100.0f / (100 + rating);
            return ratingMod;
        }

        // - Damage rating - increases all damage done to a target, including critical hits
        // Formula: (100 + <total damage rating>) / 100 = 1.xx modifier to damage
        public float GetDamageRating(int damageRating)
        {
            return GetPositiveRatingMod(damageRating);
        }

        // - Critical damage rating - increases critical hit damage done to a target
        // Formula: (100 + <total critical damage rating>) / 100 = 1.xx modifier to critical damage
        public float GetCriticalDamageRating(int criticalDmgRating)
        {
            return GetPositiveRatingMod(criticalDmgRating);
        }

        // - Damage resistance rating - decreases the amount of all incoming damage, including critical hits
        // Formula: 100 / (100 + <total damage resistance rating>) = 0.xxx modifier to incoming damage
        public float GetDamageResistanceRating(int damageResistanceRating)
        {
            return GetNegativeRatingMod(damageResistanceRating);
        }

        // - Critical damage resistance rating - decreases critical hit damage received
        // Formula: 100 / (100 + <total critical damage resistance rating>) = 0.xxx modifier to critical hit damage received
        public float GetCriticalDamageResistanceRating(int criticalDmgResistanceRating)
        {
            return GetNegativeRatingMod(criticalDmgResistanceRating);
        }

        // - DoT resistance rating - decreases the effectiveness of damage over time (DoT) spells cast upon the user
        // Formula: 100 / (100 + <total DoT reduction rating> = 0.xxx modifier to outgoing incoming DoT attacks
        public float GetDamageOverTimeResistanceRating(int dotResistanceRating)
        {
            return GetNegativeRatingMod(dotResistanceRating);
        }

        // - Health drain resistance rating - decreases the effect of drain spells cast upon the target
        // Formula: 100 / (100 + <total health drain resistance rating>) = 0.xxx modifier to incoming health drains
        public float GetHealthDrainResistanceRating(int healthDrainResistRating)
        {
            return GetNegativeRatingMod(healthDrainResistRating);
        }

        // - Healing boost rating - increases the amount of health received from consumables, healing kits, and life magic
        // Formula: (100 + <total healing rating>) / 100 = 1.xx modifier to healing
        public float GetHealingBoostRating(int healingBoostRating)
        {
            return GetPositiveRatingMod(healingBoostRating);
        }

        // - Aetheria Surge rating - increases the chance that Aetheria will surge
        // Formula: (100 + <aetheria surge rating>) / 100 = 1.xx modifier to Aetheria Surges
        public float GetAetheriaSurgeRating(int aetheriaSurgeRating)
        {
            return GetPositiveRatingMod(aetheriaSurgeRating);
        }

        // - Mana charge rating - increases the amount of mana released from mana stones into magical items
        // Formula: (100 + <mana charge rating>) / 100 = 1.xx modifier to mana charges
        public float GetManaChargeRating(int manaChargeRating)
        {
            return GetPositiveRatingMod(manaChargeRating);
        }

        // - Mana reduction rating - decreases the amount of mana consumed in equipped magical items
        // Formula: 100 / (100 + <mana reduction rating>) = 0.xxx modifier to magical item mana consumed
        public float GetManaReductionRating(int manaReductionRating)
        {
            return GetNegativeRatingMod(manaReductionRating);
        }

        // - Damage reduction rating - debuff applied to decrease the effective damage rating of the target
        // Formula: 100 / (100 + <total damage reduction rating>) = 0.xxx modifier to outgoing damage
        public float GetDamageReductionRating(int damageReductionRating)
        {
            return GetNegativeRatingMod(damageReductionRating);
        }

        // - Healing reduction rating - debuff applied to decrease the effective healing rate of the target
        // Formula: 100 / (100 + <total healing reduction rating>) = 0.xxx modifier to healing
        public float GetHealingReductionRating(int healingReductionRating)
        {
            return GetNegativeRatingMod(healingReductionRating);
        }

        // - Damage resistance reduction rating - debuff applied to decrease the effective damage resistance rating of the target
        // Formula: 100 / (100 + <total damage resistance reduction rating>) = 0.xxx modifier to incoming damage
        public float GetDamageResistanceReductionRating(int damageResistReduceRating)
        {
            return GetNegativeRatingMod(damageResistReduceRating);
        }

        // - Player Killer damage rating - increases all damage done to other players, including critical hits
        // Formula: (100 + <total PK damage rating> / 100 = 1.xx modifier to PK damage
        public float GetPKDamageRating(int pkDamageRating)
        {
            return GetPositiveRatingMod(pkDamageRating);
        }

        // - Player Killer damage resistance rating - decreases all incoming damage done by other players, including critical hits
        // Formula: 100 / (100 + <total PK damage resistance rating>) = 0.xxx modifier to incoming PK damage
        public float GetPKDamageResistanceRating(int pkDamageResistRating)
        {
            return GetNegativeRatingMod(pkDamageResistRating);
        }

        /// <summary>
        /// Converts a 1.xx modifier to a +x rating,
        /// or a 0.xx modifier to a -x rating.
        /// </summary>
        public static int ModToRating(float mod)
        {
            if (mod >= 1.0f)
                return (int)Math.Round(mod * 100 - 100);
            else
                return (int)Math.Round(-100 / mod + 100);
        }

        /// <summary>
        /// Returns a modifier to an implicitly negative rating (ie. damage resistance)
        /// </summary>
        public static int NegativeModToRating(float mod)
        {
            return -ModToRating(mod);
        }

        /// <summary>
        /// Combines rating modifiers additively
        /// </summary>
        /// <param name="mods">A list of rating modifiers</param>
        public static float AdditiveCombine(params float[] mods)
        {
            int totalRating = 0;

            foreach (var mod in mods)
                totalRating += ModToRating(mod);

            return GetRatingMod(totalRating);
        }

        /// <summary>
        /// Creature-pet rating buff: while a player has a pet summoned, the pet grants a
        /// bonus to ONE rating (fixed per pet) equal to the pet's level (1..10). Returns 0
        /// for non-players, when no pet is out, or for a rating this pet doesn't grant.
        /// </summary>
        public int GetPetRatingBonus(Pet.PetRatingType type)
        {
            if (this is Player player && player.CurrentActivePet is Pet pet)
                return pet.GetOwnerRatingBonus(type);

            return 0;
        }

        public int GetDamageRating()
        {
            // get from base properties (monsters)?
            var damageRating = DamageRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.DamageRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearDamage);

            // weakness as negative damage rating?
            // TODO: this should be factored in as a separate weakness rating...
            var weaknessRating = EnchantmentManager.GetRating(PropertyInt.WeaknessRating);

            var augBonus = 0;
            var lumAugBonus = 0;

            if (this is Player player)
            {
                augBonus = player.AugmentationDamageBonus * 3;
                lumAugBonus = player.LumAugDamageRating;
            }

            // heritage / weapon type bonus factored in elsewhere?
            return damageRating + equipment + enchantments - weaknessRating + augBonus + lumAugBonus + GetPetRatingBonus(Pet.PetRatingType.Damage);
        }

        public int GetDamageResistRating(CombatType? combatType = null, bool directDamage = true)
        {

            // get from base properties (monsters)?
            var damageResistRating = DamageResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.DamageResistRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearDamageResist);

            // nether DoTs as negative DRR?
            // TODO: this should be factored in as a separate nether damage rating...
            var netherDotDamageRating = directDamage ? EnchantmentManager.GetNetherDotDamageRating() : 0;

            var augBonus = 0;
            var lumAugBonus = 0;
            var specBonus = 0;

            if (this is Player player)
            {
                augBonus = player.AugmentationDamageReduction * 3;
                lumAugBonus = player.LumAugDamageReductionRating;
                specBonus = GetSpecDefenseBonus(combatType);
            }

            return damageResistRating + equipment + enchantments - netherDotDamageRating + augBonus + lumAugBonus + specBonus + GetPetRatingBonus(Pet.PetRatingType.DamageReduction) + GetArmorTypeBonus(ArmorTypeBonus.DamageReduction);
        }

        public float GetDamageResistRatingMod(CombatType? combatType = null, bool directDamage = true)
        {

            var damageResistRating = GetDamageResistRating(combatType, directDamage);

            var allowBug = PropertyManager.GetBool("allow_negative_rating_curve").Item;

            return GetNegativeRatingMod(damageResistRating, allowBug);
        }

        public int GetSpecDefenseBonus(CombatType? combatType)
        {
            // https://asheron.fandom.com/wiki/Announcements_-_2013/02_-_Balance_of_Power

            // New bonus added to specialized defenses against damage of their respective attack type. (Applied in both PvE & PvP)

            // - Specialized Melee Defense skill now adds 1 Damage Rating Resist for every 60 pts against melee attacks
            // - Specialized Missile Defense skill now adds 1 Damage Rating Resist for every 50 pts against missile attacks
            // - Specialized Magic Defense skill now adds 1 Damage Rating Resist for every 50 pts against magic attacks

            // only applies to players
            if (combatType == null || !(this is Player player))
                return 0;

            var skill = GetDefenseSkill(combatType.Value);
            var creatureSkill = player.GetCreatureSkill(skill);

            // ensure defense skill is specialized
            if (creatureSkill.AdvancementClass != SkillAdvancementClass.Specialized)
                return 0;

            var divisor = skill == Skill.MeleeDefense ? 60 : 50;

            // floor?
            return (int)creatureSkill.Base / divisor;
        }

        public int GetCritRating()
        {
            // crit chance


            // get from base properties (monsters)?
            var critChanceRating = CritRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.CritRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearCrit);

            // augmentations
            var augBonus = 0;

            if (this is Player player)
                augBonus = player.AugmentationCriticalExpertise;

            return critChanceRating + enchantments + equipment + augBonus + GetPetRatingBonus(Pet.PetRatingType.CritChance);
        }

        public int GetCritDamageRating()
        {

            // get from base properties (monsters)?
            var critDamageRating = CritDamageRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.CritDamageRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearCritDamage);

            // augmentations
            var augBonus = 0;
            var lumAugBonus = 0;

            if (this is Player player)
            {
                augBonus = player.AugmentationCriticalPower * 3;
                lumAugBonus = player.LumAugCritDamageRating;
            }

            return critDamageRating + equipment + enchantments + augBonus + lumAugBonus + GetPetRatingBonus(Pet.PetRatingType.CritDamage);
        }

        public int GetCritResistRating()
        {
            // crit resist chance


            // get from base properties (monsters)?
            var critResistRating = CritResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.CritResistRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearCritResist);

            // no augs / lum augs?
            return critResistRating + enchantments + equipment;
        }

        public int GetCritDamageResistRating()
        {

            // get from base properties (monsters)?
            var critDamageResistRating = CritDamageResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.CritDamageResistRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearCritDamageResist);

            var lumAugBonus = 0;
            if (this is Player player)
                lumAugBonus = player.LumAugCritReductionRating;

            return critDamageResistRating + equipment + enchantments + lumAugBonus + GetPetRatingBonus(Pet.PetRatingType.CritDamageReduction) + GetArmorTypeBonus(ArmorTypeBonus.CritDamageReduction);
        }

        public int GetHealingBoostRating()
        {

            // get from base properties (monsters)?
            var healBoostRating = HealingBoostRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.HealingBoostRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearHealingBoost);

            var pvpPenalty = 0;
            var lumAugBonus = 0;
            if (this is Player player)
            {
                lumAugBonus = player.LumAugHealingRating;

                //if (Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM && player.PKTimerActive)
                //    pvpPenalty = -100;
            }

            return healBoostRating + equipment + enchantments + lumAugBonus + pvpPenalty;
        }

        public int GetHealingResistRating()
        {

            // debuff?
            var healResistRating = HealingResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.HealingResistRating);

            return healResistRating + enchantments;
        }

        public float GetHealingRatingMod()
        {

            var boostMod = GetPositiveRatingMod(GetHealingBoostRating());
            var resistMod = GetNegativeRatingMod(GetHealingResistRating());

            return boostMod * resistMod;
        }

        public int GetLifeResistRating()
        {

            // only affects health drain?
            // only cast by Sigil of Perserverance (Aetheria)?

            // get from base properties (monsters)?
            var lifeResistRating = LifeResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.LifeResistRating);

            return lifeResistRating + enchantments;
        }

        public float GetLifeResistRatingMod()
        {
            return GetNegativeRatingMod(GetLifeResistRating());
        }

        public int GetDotResistanceRating()
        {

            // get from base properties (monsters)?
            var dotResistRating = DotResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.DotResistRating);

            return dotResistRating + enchantments;
        }

        public int GetNetherResistRating()
        {

            // there is a property defined for this,
            // but does anything use this?

            // get from base properties (monsters)?
            var netherResistRating = NetherResistRating ?? 0;

            // additive enchantments
            var enchantments = EnchantmentManager.GetRating(PropertyInt.NetherResistRating);

            return netherResistRating + enchantments;
        }

        public int GetGearMaxHealth()
        {
            return GetEquippedItemsRatingSum(PropertyInt.GearMaxHealth);
        }

        public int GetGearMaxStamina()
        {
            return GetEquippedItemsRatingSum(PropertyInt.GearMaxStamina);
        }

        /// <summary>
        /// Gear max stamina/mana (Amber/Sapphire imbues) folded into the InitLevel sent to the client,
        /// since the client does not understand GearMaxStamina/GearMaxMana.
        /// </summary>
        public int GetGearVitalBonusForClient(PropertyAttribute2nd vital)
        {
            switch (vital)
            {
                case PropertyAttribute2nd.MaxStamina: return GetGearMaxStamina();
                case PropertyAttribute2nd.MaxMana: return GetGearMaxMana();
                default: return 0;
            }
        }

        public int GetGearMaxMana()
        {
            return GetEquippedItemsRatingSum(PropertyInt.GearMaxMana);
        }

        /// <summary>
        /// Sum of GearTinkeringSkill on all equipped items (flat +skill to the 4 tinkering skills).
        /// </summary>
        public int GetGearTinkeringSkill()
        {
            if (EquippedObjects == null || EquippedObjects.Count == 0)
                return 0;

            var total = 0;
            foreach (var wo in EquippedObjects.Values)
                total += wo.GetProperty(PropertyInt.GearTinkeringSkill) ?? 0;
            return total;
        }

        /// <summary>
        /// Sum of GearCraftingSkill on all equipped items (flat +skill to cooking/fletching/alchemy).
        /// </summary>
        public int GetGearCraftingSkill()
        {
            if (EquippedObjects == null || EquippedObjects.Count == 0)
                return 0;

            var total = 0;
            foreach (var wo in EquippedObjects.Values)
                total += wo.GetProperty(PropertyInt.GearCraftingSkill) ?? 0;
            return total;
        }

        /// <summary>
        /// Sum of GearMagicSkill on all equipped items (flat +skill to the 5 magic skills).
        /// </summary>
        public int GetGearMagicSkill()
        {
            if (EquippedObjects == null || EquippedObjects.Count == 0)
                return 0;

            var total = 0;
            foreach (var wo in EquippedObjects.Values)
                total += wo.GetProperty(PropertyInt.GearMagicSkill) ?? 0;
            return total;
        }

        /// <summary>
        /// Sum of GearCarryingCapacity on all equipped items (percentage points of burden reduction, e.g. 20 = burden becomes 80%).
        /// </summary>
        public int GetGearCarryingCapacity()
        {
            if (EquippedObjects == null || EquippedObjects.Count == 0)
                return 0;

            var total = 0;
            foreach (var wo in EquippedObjects.Values)
                total += wo.GetProperty(PropertyInt.GearCarryingCapacity) ?? 0;

            // prestige Titan's Crystal 6-piece: +20% burden bearing
            total += GetSetCrystalBurdenBonus();
            return total;
        }

        /// <summary>
        /// Sum of GearXpMod on all equipped items (additive fraction, e.g. 0.06 = +6%).
        /// </summary>
        public double GetEquippedGearXpMod()
        {
            if (EquippedObjects == null || EquippedObjects.Count == 0)
                return 0.0;

            double total = 0.0;
            foreach (var wo in EquippedObjects.Values)
                total += wo.GearXpMod ?? 0.0;
            return total;
        }

        public int GetPKDamageRating()
        {
            var pkDamageRating = PKDamageRating ?? 0;

            // additive enchantments?
            var enchantments = EnchantmentManager.GetRating(PropertyInt.PKDamageRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearPKDamageRating);

            return pkDamageRating + equipment + enchantments;
        }

        public int GetPKDamageResistRating()
        {
            var pkDamageResistRating = PKDamageResistRating ?? 0;

            // additive enchantments?
            var enchantments = EnchantmentManager.GetRating(PropertyInt.PKDamageResistRating);

            // equipment ratings
            var equipment = GetEquippedItemsRatingSum(PropertyInt.GearPKDamageResistRating);

            return pkDamageResistRating + equipment + enchantments;
        }

        public int GetGearPKDamageRating()
        {
            return GetEquippedItemsRatingSum(PropertyInt.GearPKDamageRating);
        }

        public int GetGearPKDamageResistRating()
        {
            return GetEquippedItemsRatingSum(PropertyInt.GearPKDamageResistRating);
        }

        public int GetItemManaReductionRating()
        {
            // only comes from luminance aug?
            var lumAugBonus = 0;

            if (this is Player player)
                lumAugBonus = player.LumAugItemManaUsage;

            return lumAugBonus;
        }

        public int GetManaChargeRating()
        {
            // only comes from luminance aug?
            var lumAugBonus = 0;

            if (this is Player player)
                lumAugBonus = player.LumAugItemManaGain;

            return lumAugBonus;
        }

        // ===== loot armor-type set bonuses (DD chart) ========================
        // Each equipped non-shield armor piece grants a flat +1 to one bonus,
        // based on its loot armor style. Pieces stack; tailoring does not change
        // the count (1 piece = +1 regardless of coverage).

        public enum ArmorTypeBonus
        {
            None                = 0,
            CritDamageReduction = 1,
            DamageReduction     = 2,
            MissileDefense      = 3,
            MagicSkills         = 4,
            MagicDefense        = 5,
            MeleeDefense        = 6,
        }

        /// <summary>
        /// Maps a loot armor style to the bonus it grants when equipped.
        /// </summary>
        public static ArmorTypeBonus GetBonusForArmorType(TreasureArmorType armorType)
        {
            switch (armorType)
            {
                // Crit Damage Reduction
                case TreasureArmorType.Alduressa:
                case TreasureArmorType.Amuli:
                case TreasureArmorType.Diforsa:
                case TreasureArmorType.Haebrean:
                case TreasureArmorType.Koujia:
                case TreasureArmorType.Lorica:
                case TreasureArmorType.Nariyid:
                case TreasureArmorType.Tenassa:
                    return ArmorTypeBonus.CritDamageReduction;

                // Damage Reduction
                case TreasureArmorType.Celdon:
                case TreasureArmorType.Platemail:
                    return ArmorTypeBonus.DamageReduction;

                // Missile Defense
                case TreasureArmorType.Chainmail:
                case TreasureArmorType.Scalemail:
                case TreasureArmorType.Yoroi:
                    return ArmorTypeBonus.MissileDefense;

                // Magic Skills
                case TreasureArmorType.Chiran:
                case TreasureArmorType.KnorrAcademy:
                case TreasureArmorType.Cloth:
                case TreasureArmorType.Overrobe:
                    return ArmorTypeBonus.MagicSkills;

                // Magic Defense
                case TreasureArmorType.Covenant:
                case TreasureArmorType.Olthoi:
                case TreasureArmorType.OlthoiCeldon:
                case TreasureArmorType.OlthoiAmuli:
                case TreasureArmorType.OlthoiKoujia:
                case TreasureArmorType.OlthoiAlduressa:
                case TreasureArmorType.OlthoiHeritage:
                    return ArmorTypeBonus.MagicDefense;

                // Melee Defense
                case TreasureArmorType.Leather:
                case TreasureArmorType.StuddedLeather:
                case TreasureArmorType.Sedgemail:
                    return ArmorTypeBonus.MeleeDefense;

                default:
                    return ArmorTypeBonus.None;
            }
        }

        /// <summary>
        /// Identify the loot armor style of an equipped piece, or None if it
        /// isn't armor / is a shield / can't be classified.
        /// </summary>
        public static ArmorTypeBonus GetArmorPieceBonus(WorldObject wo)
        {
            if (wo == null)
                return ArmorTypeBonus.None;

            // armor only for now — shields do not grant the set bonus
            if (wo.IsShield)
                return ArmorTypeBonus.None;

            // must be real armor or clothing-as-armor
            if (!wo.HasArmorLevel() && !wo.IsClothArmor)
                return ArmorTypeBonus.None;

            // named loot styles via ArmorWcids reverse map (loot only — quest,
            // vendor and unique pieces are intentionally NOT rated)
            if (ArmorWcids.TryGetValue((ACE.Server.Factories.Enum.WeenieClassName)wo.WeenieClassId, out var armorType))
                return GetBonusForArmorType(armorType);

            return ArmorTypeBonus.None;
        }

        // ===== shield innate bonuses (shield chart) ==========================
        // Each equipped shield grants a flat bonus based on its shield shape.
        // Tower Shield grants +2; every other shield grants +1. Shields stack
        // with armor-type bonuses of the same category.

        /// <summary>
        /// The innate bonus an equipped shield grants, by shield shape, and the
        /// +amount it grants (via out). Returns None / amount 0 for anything
        /// that isn't a recognized shield.
        /// </summary>
        public static ArmorTypeBonus GetShieldPieceBonus(WorldObject wo, out int amount)
        {
            amount = 0;

            if (wo == null || !wo.IsShield)
                return ArmorTypeBonus.None;

            var wcn = (ACE.Server.Factories.Enum.WeenieClassName)wo.WeenieClassId;

            switch (wcn)
            {
                case ACE.Server.Factories.Enum.WeenieClassName.buckler:
                    amount = 1; return ArmorTypeBonus.MeleeDefense;

                case ACE.Server.Factories.Enum.WeenieClassName.shieldkite:
                    amount = 1; return ArmorTypeBonus.MissileDefense;

                case ACE.Server.Factories.Enum.WeenieClassName.shieldkitelarge:
                    amount = 1; return ArmorTypeBonus.DamageReduction;

                case ACE.Server.Factories.Enum.WeenieClassName.shieldround:
                    amount = 1; return ArmorTypeBonus.CritDamageReduction;

                case ACE.Server.Factories.Enum.WeenieClassName.shieldroundlarge:
                    amount = 1; return ArmorTypeBonus.DamageReduction;

                case ACE.Server.Factories.Enum.WeenieClassName.shieldtower:
                    amount = 2; return ArmorTypeBonus.DamageReduction;

                case ACE.Server.Factories.Enum.WeenieClassName.shieldcovenant:
                    amount = 1; return ArmorTypeBonus.MagicDefense;

                case ACE.Server.Factories.Enum.WeenieClassName.ace37291_olthoishield:
                    amount = 1; return ArmorTypeBonus.MagicDefense;
            }

            // Loot shields only — quest/unique shields are intentionally NOT rated.
            return ArmorTypeBonus.None;
        }

        /// <summary>
        /// Sum of equipped bonuses for the given category: +1 per matching
        /// armor piece plus each matching shield's amount (Tower = +2). Players
        /// only; monsters get 0.
        /// </summary>
        public int GetArmorTypeBonus(ArmorTypeBonus bonus)
        {
            if (bonus == ArmorTypeBonus.None || !(this is Player))
                return 0;

            if (EquippedObjects == null || EquippedObjects.Count == 0)
                return 0;

            var total = 0;
            foreach (var wo in EquippedObjects.Values)
            {
                if (GetArmorPieceBonus(wo) == bonus)
                    total++;
                else if (GetShieldPieceBonus(wo, out var shieldAmt) == bonus)
                    total += shieldAmt;
            }
            return total;
        }

    }
}
