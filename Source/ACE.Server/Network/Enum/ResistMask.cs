using System;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Network.Enum
{
    [Flags]
    public enum ResistMask
    {
        ResistSlash         = 0x1,
        ResistPierce        = 0x2,
        ResistBludgeon      = 0x4,
        ResistFire          = 0x8,
        ResistCold          = 0x10,
        ResistAcid          = 0x20,
        ResistElectric      = 0x40,
        ResistHealthBoost   = 0x80,
        ResistStaminaDrain  = 0x100,
        ResistStaminaBoost  = 0x200,
        ResistManaDrain     = 0x400,
        ResistManaBoost     = 0x800,
        ManaConversionMod   = 0x1000,
        ElementalDamageMod  = 0x2000,
        ResistNether        = 0x4000
    };

    public static class ResistMaskHelper
    {
        public static ResistMask GetHighlightMask(WorldObject weapon)
        {
            ResistMask highlightMask = 0;

            var wielder = weapon.Wielder;

            if (wielder != null)
            {
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Slash) != 1.0f)
                    highlightMask |= ResistMask.ResistSlash;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Pierce) != 1.0f)
                    highlightMask |= ResistMask.ResistPierce;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Bludgeon) != 1.0f)
                    highlightMask |= ResistMask.ResistBludgeon;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Fire) != 1.0f)
                    highlightMask |= ResistMask.ResistFire;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Cold) != 1.0f)
                    highlightMask |= ResistMask.ResistCold;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Electric) != 1.0f)
                    highlightMask |= ResistMask.ResistElectric;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Health) != 1.0f)      // ??
                    highlightMask |= ResistMask.ResistHealthBoost;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Stamina) != 1.0f)
                    highlightMask |= ResistMask.ResistStaminaDrain;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Stamina) != 1.0f)
                    highlightMask |= ResistMask.ResistStaminaBoost;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Mana) != 1.0f)
                    highlightMask |= ResistMask.ResistManaDrain;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Mana) != 1.0f)
                    highlightMask |= ResistMask.ResistManaBoost;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Nether) != 1.0f)
                    highlightMask |= ResistMask.ResistNether;
            }

            // ManaConversionMod and ElementalDamageMod are only needed for weapons
            var manaConversionMod = GetManaConversionMod(weapon);

            if (manaConversionMod != 1.0f)
                highlightMask |= ResistMask.ManaConversionMod;

            var elementalDamageMod = GetElementalDamageBonus(weapon);

            if (elementalDamageMod != 0.0f)
                highlightMask |= ResistMask.ElementalDamageMod;


            return highlightMask;
        }

        public static ResistMask GetColorMask(WorldObject weapon)
        {
            ResistMask colorMask = 0;

            var wielder = weapon.Wielder;

            if (wielder != null)
            {
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Slash) > 1.0f)
                    colorMask |= ResistMask.ResistSlash;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Pierce) > 1.0f)
                    colorMask |= ResistMask.ResistPierce;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Bludgeon) > 1.0f)
                    colorMask |= ResistMask.ResistBludgeon;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Fire) > 1.0f)
                    colorMask |= ResistMask.ResistFire;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Cold) > 1.0f)
                    colorMask |= ResistMask.ResistCold;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Electric) > 1.0f)
                    colorMask |= ResistMask.ResistElectric;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Health) > 1.0f)      // ??
                    colorMask |= ResistMask.ResistHealthBoost;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Stamina) > 1.0f)
                    colorMask |= ResistMask.ResistStaminaDrain;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Stamina) > 1.0f)
                    colorMask |= ResistMask.ResistStaminaBoost;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Mana) > 1.0f)
                    colorMask |= ResistMask.ResistManaDrain;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Mana) > 1.0f)
                    colorMask |= ResistMask.ResistManaBoost;
                if (wielder.EnchantmentManager.GetResistanceMod(DamageType.Nether) > 1.0f)
                    colorMask |= ResistMask.ResistNether;
            }

            // ManaConversionMod and ElementalDamageMod are only needed for weapons
            var manaConversionMod = GetManaConversionMod(weapon);

            if (manaConversionMod > 1.0f)
                colorMask |= ResistMask.ManaConversionMod;

            var elementalDamageMod = GetElementalDamageBonus(weapon);

            if (elementalDamageMod > 0.0f)
                colorMask |= ResistMask.ElementalDamageMod;

            return colorMask;
        }

        public static float GetManaConversionMod(WorldObject weapon)
        {
            if (weapon == null)
                return 1.0f;

            var manaConversionMod = WeaponEnchantments.GetManaConvMod(weapon, weapon.IsEnchantable ? weapon.Wielder : null);

            return manaConversionMod;
        }

        public static float GetElementalDamageBonus(WorldObject weapon)
        {
            if (weapon == null)
                return 0.0f;

            var elementalDamageMod = WeaponEnchantments.GetElementalDamageMod(weapon, weapon.IsEnchantable ? weapon.Wielder : null);

            return elementalDamageMod;
        }
    }
}
