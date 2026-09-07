using ACE.Common;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories.Tables;
using ACE.Server.WorldObjects;

namespace ACE.Server.Factories
{
    public static partial class LootGenerationFactory
    {
        private static float RollWeaponSpeedMod(TreasureDeath treasureDeath)
        {
            var qualityLevel = QualityChance.Roll(treasureDeath);

            if (qualityLevel == 0)
                return 1.0f;    // no bonus

            var rng = (float)ThreadSafeRandom.Next(-0.025f, 0.025f);

            // min/max range: 67.5% - 100%
            var weaponSpeedMod = 1.0f - (qualityLevel * 0.025f + rng);

            //Console.WriteLine($"WeaponSpeedMod: {weaponSpeedMod}");

            return weaponSpeedMod;
        }

        private static float ApplyQualityModToExtraMutationChance(float chance, float lootQualityMod)
        {
            return chance * (1 + lootQualityMod);
        }

        private static bool RollCrushingBlow(TreasureDeath treasureDeath, WorldObject wo)
        {
            if (Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
                return false;

            var chance = ExtraMutationEffects.GetCrushingBlowChanceForTier(treasureDeath.Tier);
            chance = ApplyQualityModToExtraMutationChance(chance, treasureDeath.LootQualityMod);
            if (chance > ThreadSafeRandom.Next(0.0f, 1.0f))
            {
                float amount;
                if (wo.IsCaster)
                    amount = 1.5f;
                else
                    amount = 2.0f;

                wo.CriticalMultiplier = amount;
                wo.IconOverlayId = 0x06005EBC;
                return true;
            }
            else
                return false;
        }

        private static bool RollBitingStrike(TreasureDeath treasureDeath, WorldObject wo)
        {
            if (Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
                return false;

            var chance = ExtraMutationEffects.GetBitingStrikeChanceForTier(treasureDeath.Tier);
            chance = ApplyQualityModToExtraMutationChance(chance, treasureDeath.LootQualityMod);
            if (chance > ThreadSafeRandom.Next(0.0f, 1.0f))
            {
                float amount;
                if (wo.IsCaster)
                    amount = 0.10f;
                else
                    amount = 0.15f;
                wo.CriticalFrequency = amount;
                wo.IconOverlayId = 0x06005EBD;
                return true;
            }
            else
                return false;
        }

        private static bool RollSlayer(TreasureDeath treasureDeath, WorldObject wo)
        {
            if (Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
                return false;

            var chance = SlayerTypeChance.GetSlayerChanceForTier(treasureDeath.Tier);
            chance = ApplyQualityModToExtraMutationChance(chance, treasureDeath.LootQualityMod);
            if (chance > ThreadSafeRandom.Next(0.0f, 1.0f))
            {
                wo.SlayerCreatureType = SlayerTypeChance.Roll(treasureDeath);
                wo.SlayerDamageBonus = 1.5f;
                wo.IconOverlayId = 0x06005EC0;
                return true;
            }
            return false;
        }

        private static bool RollArmorCleaving(TreasureDeath treasureDeath, WorldObject wo)
        {
            if (Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
                return false;

            var chance = ExtraMutationEffects.GetArmorCleavingChanceForTier(treasureDeath.Tier);
            chance = ApplyQualityModToExtraMutationChance(chance, treasureDeath.LootQualityMod);
            if (chance > ThreadSafeRandom.Next(0.0f, 1.0f))
            {
                // 0.0 = ignore 100% of armor AL.
                wo.IgnoreArmor = 0.625f; // Equivalent to -75 at 200 AL armor.
                wo.IconOverlayId = 0x06005EBF;
                return true;
            }
            return false;
        }

        private static bool RollShieldCleaving(TreasureDeath treasureDeath, WorldObject wo)
        {
            if (Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
                return false;

            if (wo.IsTwoHanded)
                return false; // Two-handed weapons area always shield cleaving so this is unnecessary.

            var chance = ExtraMutationEffects.GetShieldCleavingChanceForTier(treasureDeath.Tier);
            chance = ApplyQualityModToExtraMutationChance(chance, treasureDeath.LootQualityMod);
            if (chance > ThreadSafeRandom.Next(0.0f, 1.0f))
            {
                // 1.0 = ignore 100% of shield AL.
                wo.IgnoreShield = 0.50f; // Equivalent of Brittlemail V for 300 AL shields.
                wo.IconOverlayId = 0x06005EC2;
                wo.Name = $"{wo.Name} of Shield Cleaving";
                return true;
            }
            return false;
        }

        private static bool RollResistanceCleaving(TreasureDeath treasureDeath, WorldObject wo)
        {
            if (Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
                return false;

            var chance = ExtraMutationEffects.GetResistanceCleavingChanceForTier(treasureDeath.Tier);
            chance = ApplyQualityModToExtraMutationChance(chance, treasureDeath.LootQualityMod);
            if (chance > ThreadSafeRandom.Next(0.0f, 1.0f))
            {
                wo.ResistanceModifierType = DamageType.Elemental;
                wo.ResistanceModifier = 0.6f; // This is used as the multiplier in the rending formula.
                wo.IconOverlayId = 0x06005EC1;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        //  Built-in weapon special properties (Infiltration).
        //  Each loot weapon gets ONE guaranteed, type-appropriate built-in,
        //  rolled fresh at spawn. Replaces the old ~2% random block of
        //  RollShieldCleaving/RollArmorCleaving/RollBitingStrike/
        //  RollCrushingBlow/RollSlayer. Not gated on CustomDM, so it runs
        //  under Infiltration.
        // ------------------------------------------------------------------
        private static void ApplyBuiltInWeaponProperty(TreasureDeath treasureDeath, WorldObject wo)
        {
            // Thrown weapons always get the Crippling Blow imbue, whatever their base skill.
            if (wo.IsThrownWeapon || wo.WeaponSkill == Skill.ThrownWeapon)
            {
                SetNaturalImbue(wo, ImbuedEffectType.CripplingBlow);
                return;
            }

            switch (wo.WeaponSkill)
            {
                case Skill.Sword:
                case Skill.Spear:
                case Skill.Bow:
                case Skill.Crossbow:
                case Skill.MissileWeapons:   // bows/crossbows reporting the unified missile skill
                    RollCleaveBiteCrush(wo); // 1 of 3: armor cleaving | biting strike | crushing blow
                    break;

                case Skill.Axe:
                    RollBiteCrush(wo);       // 1 of 2: biting strike | crushing blow
                    break;

                case Skill.Mace:
                    SetCrushingBlow(wo);
                    SetProc(wo, 1290, selfTargeted: true, rate: 0.03f);    // Mana to Health on self, 3%
                    break;

                case Skill.Dagger:
                    SetNaturalImbue(wo, ImbuedEffectType.CriticalStrike);
                    break;

                case Skill.Staff:
                    SetBitingStrike(wo);
                    wo.AbsorbMagicDamage = 0.10f;                          // built-in 10% magic absorb
                    break;

                case Skill.UnarmedCombat:
                    SetBitingStrike(wo);
                    SetProc(wo, 1249, selfTargeted: false, rate: 0.03f);   // Drain Stamina on target, 3%
                    break;
            }
        }

        private static void RollCleaveBiteCrush(WorldObject wo)
        {
            switch (ThreadSafeRandom.Next(0, 2))
            {
                case 0: SetArmorCleaving(wo); break;
                case 1: SetBitingStrike(wo); break;
                default: SetCrushingBlow(wo); break;
            }
        }

        private static void RollBiteCrush(WorldObject wo)
        {
            if (ThreadSafeRandom.Next(0, 1) == 0)
                SetBitingStrike(wo);
            else
                SetCrushingBlow(wo);
        }

        private static void SetBitingStrike(WorldObject wo)
        {
            wo.CriticalFrequency = (float)ThreadSafeRandom.Next(0.15f, 0.40f);
        }

        private static void SetCrushingBlow(WorldObject wo)
        {
            wo.CriticalMultiplier = (float)ThreadSafeRandom.Next(1.5f, 4.0f);
        }

        private static void SetArmorCleaving(WorldObject wo)
        {
            // IgnoreArmor: 0.0 = ignore 100% of armor AL, 1.0 = ignore none.
            wo.IgnoreArmor = (float)ThreadSafeRandom.Next(0.0f, 0.8f);
        }

        private static void SetProc(WorldObject wo, uint spellId, bool selfTargeted, float rate)
        {
            wo.ProcSpell = spellId;
            wo.ProcSpellRate = rate;
            wo.ProcSpellSelfTargeted = selfTargeted;
        }

        // Natural imbue (Dagger = Critical Strike, Thrown = Crippling Blow).
        // Stored in the SECOND imbue slot so the first slot stays free for a player
        // tinker-imbue — the two then coexist instead of the player one replacing it.
        // Combat reads every slot (Weapon.HasImbuedEffect -> GetImbuedEffects), and
        // the imbue recipe's "already imbued" check only looks at slot 1, so this
        // never blocks tinkering. No icon underlay/overlay is set — that visual is
        // reserved for player-tinkered imbues.
        private static void SetNaturalImbue(WorldObject wo, ImbuedEffectType effect)
        {
            wo.SetProperty(PropertyInt.ImbuedEffect2, (int)effect);
        }
    }
}
