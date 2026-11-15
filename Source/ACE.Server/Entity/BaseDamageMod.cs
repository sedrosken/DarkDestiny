using System;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Entity
{
    public class BaseDamageMod
    {
        public BaseDamage BaseDamage;

        public float DamageBonus   = 0.0f;    // blood drinker
        public float DamageMod     = 1.0f;    // for missile launchers (+113% yumis = 2.13)
        public float VarianceMod   = 1.0f;

        public int ElementalBonus = 0;

        public float MaxDamage
        {
            get
            {
                var maxDamage = (BaseDamage.MaxDamage + DamageBonus + ElementalBonus) * DamageMod;

                if (BaseDamage.MaxDamage >= 0)
                    maxDamage = Math.Max(0, maxDamage);
                else
                    maxDamage = Math.Min(0, maxDamage);

                return maxDamage;   
            }
        }

        public float MinDamage => MaxDamage * (1.0f - BaseDamage.Variance * VarianceMod);

        public Range Range => new Range(MinDamage, MaxDamage);

        public BaseDamageMod(BaseDamage baseDamage)
        {
            BaseDamage = baseDamage;
        }

        public BaseDamageMod(BaseDamage baseDamage, Creature wielder, WorldObject weapon)
        {
            BaseDamage = baseDamage;

            if (weapon == null)
                return;

            // factor in wielder auras for enchantable weapons
            // weapon buffs + wielder auras: only the top spell per category counts across both
            var auraSource = weapon.IsEnchantable ? wielder : null;

            DamageBonus += WeaponEnchantments.GetDamageBonus(weapon, auraSource);
            VarianceMod *= WeaponEnchantments.GetVarianceMod(weapon, auraSource);

            DamageMod = (float)(weapon.GetProperty(PropertyFloat.DamageMod) ?? 1.0f) + WeaponEnchantments.GetDamageMod(weapon, auraSource);
        }
    }
}
