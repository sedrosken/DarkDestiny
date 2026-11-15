using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Server.WorldObjects.Managers
{
    /// <summary>
    /// Combines weapon enchantments (Blood Drinker cast on the weapon, a weapon's innate spells)
    /// with wielder auras (the same buffs from jewelry / clothing / armor, or aura spells on the wielder)
    /// so that only the top spell in each spell category counts, across both registries.
    ///
    /// Previously each registry picked its own top layer and the results were added together,
    /// so Blood Drinker VI on the weapon + Blood Drinker VI aura from a bracelet would double up.
    ///
    /// The selection order matches PropertiesEnchantmentRegistryExtensions.GetEnchantmentsTopLayer*:
    /// highest PowerLevel wins, and at equal power a timed (cast) buff beats a gear (Duration -1) buff.
    /// </summary>
    public static class WeaponEnchantments
    {
        private const EnchantmentTypeFlags FloatAdditive = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive;

        /// <summary>
        /// Weapon buff categories that can exist on both the weapon and its wielder.
        /// Shared with the equip path (Creature.CastsOnWielder) and the surpass / refresh messaging (EnchantmentManager.Add).
        /// </summary>
        public static bool IsWeaponBuffCategory(SpellCategory category)
        {
            switch (category)
            {
                case SpellCategory.AttackModRaising:            // Heart Seeker
                case SpellCategory.DamageRaising:               // Blood Drinker
                case SpellCategory.DefenseModRaising:           // Defender
                case SpellCategory.WeaponTimeRaising:           // Swift Killer
                case SpellCategory.ManaConversionModRaising:    // Hermetic Link
                case SpellCategory.SpellDamageRaising:          // Spirit Drinker
                    return true;
            }
            return false;
        }

        /// <summary>
        /// For a weapon, returns its wielder; for a creature, returns its primary weapon / wand.
        /// This is the 'other' registry whose weapon buffs compete with this one. Returns null if there isn't one.
        /// </summary>
        public static WorldObject GetLinkedRegistryOwner(WorldObject wo)
        {
            if (wo is Creature creature)
            {
                var weapon = creature.GetEquippedWeapon() ?? creature.GetEquippedWand();

                return weapon != null && weapon.IsEnchantable ? weapon : null;
            }

            if (wo.Wielder != null && wo.IsEnchantable && (wo.ItemType & ItemType.WeaponOrCaster) != 0)
                return wo.Wielder;

            return null;
        }

        /// <summary>
        /// Returns the winning enchantment in each spell category, across the weapon and wielder registries,
        /// for any of the statModKeys (ie. PropertyInt.Damage + PropertyInt.WeaponAuraDamage)
        /// </summary>
        /// <param name="wielder">null if wielder auras shouldn't apply (ie. unenchantable weapon)</param>
        private static List<PropertiesEnchantmentRegistry> GetTopLayer(WorldObject weapon, WorldObject wielder, EnchantmentTypeFlags statModType, bool excludeSkill, params uint[] statModKeys)
        {
            var all = new List<PropertiesEnchantmentRegistry>();

            foreach (var wo in new[] { weapon, wielder })
            {
                // HasEnchantments is cached, so most calls on unbuffed weapons / monsters stay cheap
                if (wo?.EnchantmentManager == null || !wo.EnchantmentManager.HasEnchantments)
                    continue;

                foreach (var statModKey in statModKeys)
                {
                    var enchantments = wo.EnchantmentManager.GetEnchantments_TopLayer(statModType, statModKey);

                    if (enchantments == null)
                        continue;

                    all.AddRange(excludeSkill ? enchantments.Where(e => (e.StatModType & EnchantmentTypeFlags.Skill) == 0) : enchantments);
                }
            }

            return all.GroupBy(e => e.SpellCategory)
                .Select(g => g.OrderByDescending(e => e.PowerLevel)
                    .ThenByDescending(e => e.Duration != -1)            // at equal power, timed (cast) buffs beat gear (equip) buffs
                    .ThenByDescending(e => System.Math.Abs(e.StatModValue))
                    .First())
                .ToList();
        }

        private static float Sum(List<PropertiesEnchantmentRegistry> enchantments)
        {
            var modifier = 0.0f;
            foreach (var enchantment in enchantments)
                modifier += enchantment.StatModValue;
            return modifier;
        }

        private static float Product(List<PropertiesEnchantmentRegistry> enchantments)
        {
            var modifier = 1.0f;
            foreach (var enchantment in enchantments)
                modifier *= enchantment.StatModValue;
            return modifier;
        }

        /// <summary>
        /// Blood Drinker / Blood Loather
        /// </summary>
        public static int GetDamageBonus(WorldObject weapon, WorldObject wielder)
        {
            var enchantments = GetTopLayer(weapon, wielder, EnchantmentTypeFlags.Additive, true, (uint)PropertyInt.Damage, (uint)PropertyInt.WeaponAuraDamage);

            var modifier = 0;
            foreach (var enchantment in enchantments)
                modifier += (int)enchantment.StatModValue;
            return modifier;
        }

        /// <summary>
        /// DamageMod for bow / crossbow
        /// </summary>
        public static float GetDamageMod(WorldObject weapon, WorldObject wielder)
        {
            return Sum(GetTopLayer(weapon, wielder, FloatAdditive, false, (uint)PropertyFloat.DamageMod));
        }

        public static float GetVarianceMod(WorldObject weapon, WorldObject wielder)
        {
            return Product(GetTopLayer(weapon, wielder, EnchantmentTypeFlags.Multiplicative, false, (uint)PropertyFloat.DamageVariance));
        }

        /// <summary>
        /// Heart Seeker / Turn Blade
        /// </summary>
        public static float GetAttackMod(WorldObject weapon, WorldObject wielder)
        {
            return Sum(GetTopLayer(weapon, wielder, FloatAdditive, false, (uint)PropertyFloat.WeaponOffense, (uint)PropertyFloat.WeaponAuraOffense));
        }

        /// <summary>
        /// Defender / Lure Blade
        /// </summary>
        public static float GetDefenseMod(WorldObject weapon, WorldObject wielder)
        {
            return Sum(GetTopLayer(weapon, wielder, FloatAdditive, false, (uint)PropertyFloat.WeaponDefense, (uint)PropertyFloat.WeaponAuraDefense));
        }

        /// <summary>
        /// Swift Killer / Leaden Weapon (additive part only, multiplicative speed mods stay weapon-only)
        /// </summary>
        public static int GetWeaponSpeedMod(WorldObject weapon, WorldObject wielder)
        {
            var enchantments = GetTopLayer(weapon, wielder, EnchantmentTypeFlags.Additive, true, (uint)PropertyInt.WeaponTime, (uint)PropertyInt.WeaponAuraSpeed);

            var modifier = 0;
            foreach (var enchantment in enchantments)
                modifier += (int)enchantment.StatModValue;
            return modifier;
        }

        /// <summary>
        /// Hermetic Link / Hermetic Void
        /// </summary>
        public static float GetManaConvMod(WorldObject weapon, WorldObject wielder)
        {
            return Product(GetTopLayer(weapon, wielder, EnchantmentTypeFlags.Multiplicative, false, (uint)PropertyFloat.ManaConversionMod, (uint)PropertyFloat.WeaponAuraManaConv));
        }

        /// <summary>
        /// Spirit Drinker / Spirit Loather
        /// </summary>
        public static float GetElementalDamageMod(WorldObject weapon, WorldObject wielder)
        {
            return Sum(GetTopLayer(weapon, wielder, FloatAdditive, false, (uint)PropertyFloat.ElementalDamageMod, (uint)PropertyFloat.WeaponAuraElemental));
        }
    }
}
