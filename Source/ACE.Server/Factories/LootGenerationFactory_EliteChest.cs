using System;

using ACE.Common;
using ACE.Database.Models.World;
using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables;
using ACE.Server.WorldObjects;

namespace ACE.Server.Factories
{
    /// <summary>
    /// Custom "elite" treasure profile (ClassicACE custom content).
    /// Any treasure_death row whose treasure_Type == EliteChestTreasureType gets:
    ///   - magic items only, 40% weapon / 40% armor / 20% jewelry
    ///   - weapons at the top wield difficulty (melee 400 / missile 360 / caster 355)
    ///   - workmanship 5 or less
    ///   - 1 major cantrip (89%), 2 majors (10%), 3 majors (1%), never minor
    /// </summary>
    public static partial class LootGenerationFactory
    {
        public const uint EliteChestTreasureType = 9870001;

        private const int EliteMaxAttempts = 50;
        private const int EliteMaxWorkmanship = 5;

        // quality-mod floors for the damage/wield mutation scripts (tier 6, Infiltration)
        // the top wield band is the last 15% of the melee + elemental missile scripts, and the last 6% of the elemental caster script
        public const float EliteMeleeDamageQualityMod = 0.86f;
        public const float EliteMissileDamageQualityMod = 0.86f;
        public const float EliteCasterDamageQualityMod = 0.945f;

        public static bool IsEliteChestProfile(TreasureDeath profile)
        {
            return profile != null && profile.TreasureType == EliteChestTreasureType;
        }

        public static float EliteDamageQualityMod(TreasureDeath profile, float threshold)
        {
            if (!IsEliteChestProfile(profile))
                return profile.LootQualityMod;

            return Math.Max(profile.LootQualityMod, threshold);
        }

        private static int RollEliteNumCantrips()
        {
            var rng = ThreadSafeRandom.Next(0.0f, 1.0f);

            if (rng < 0.01f)
                return 3;
            if (rng < 0.11f)
                return 2;
            return 1;
        }

        private static TreasureItemType_Orig RollEliteItemType()
        {
            var rng = ThreadSafeRandom.Next(0.0f, 1.0f);

            if (rng < 0.4f)
                return TreasureItemType_Orig.Weapon;
            if (rng < 0.8f)
                return TreasureItemType_Orig.Armor;
            return TreasureItemType_Orig.Jewelry;
        }

        private static int GetEliteWieldTarget(TreasureRoll roll)
        {
            if (roll.IsMeleeWeapon) return 400;
            if (roll.IsMissileWeapon) return 360;
            if (roll.IsCaster) return 355;
            return 0;
        }

        private static WorldObject CreateEliteLootObject(TreasureDeath profile)
        {
            for (var attempt = 0; attempt < EliteMaxAttempts; attempt++)
            {
                var itemType = RollEliteItemType();

                var eliteProfile = new TreasureDeathExtended(profile)
                {
                    ForceTreasureItemType = itemType
                };
                eliteProfile.TreasureType = profile.TreasureType;

                var roll = RollWcid(eliteProfile, TreasureItemCategory.MagicItem);
                if (roll == null)
                    continue;

                var wo = CreateAndMutateWcid(eliteProfile, roll, true);
                if (wo == null)
                    continue;

                // weapons must land on the top wield difficulty
                if (itemType == TreasureItemType_Orig.Weapon)
                {
                    var target = GetEliteWieldTarget(roll);
                    if (target == 0 || wo.WieldDifficulty != target)
                        continue;
                }

                // every item needs at least one cantrip
                if (roll.Cantrips == null || roll.Cantrips.Count == 0)
                    continue;

                // workmanship 5 or less
                if (wo.ItemWorkmanship > EliteMaxWorkmanship)
                {
                    for (var i = 0; i < 20 && wo.ItemWorkmanship > EliteMaxWorkmanship; i++)
                        wo.ItemWorkmanship = WorkmanshipChance.Roll(profile.Tier, profile.LootQualityMod);

                    if (wo.ItemWorkmanship > EliteMaxWorkmanship)
                        wo.ItemWorkmanship = EliteMaxWorkmanship;
                }

                return wo;
            }

            log.Warn($"[LOOT] CreateEliteLootObject({profile.TreasureType}) - no valid item after {EliteMaxAttempts} attempts");
            return null;
        }
    }
}
