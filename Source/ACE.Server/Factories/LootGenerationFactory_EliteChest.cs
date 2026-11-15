using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables;
using ACE.Server.WorldObjects;

namespace ACE.Server.Factories
{
    /// <summary>
    /// Custom "elite" treasure profile (ClassicACE custom content).
    /// Any treasure_death row whose treasure_Type == EliteChestTreasureType gets:
    ///   - magic items only, 40% weapon / 30% armor / 10% clothing / 20% jewelry
    ///   - weapons: 70% melee / 15% missile / 15% caster, at the top wield difficulty (melee 400 / missile 360 / caster 355)
    ///   - armor: 20-40 AL above the max that piece could normally roll at tier 6
    ///   - workmanship 3-5
    ///   - 1 major cantrip (89%), 2 majors (10%), 3 majors (1%), never minor
    ///   - a bane cantrip is always paired with at least one non-bane cantrip
    ///   - no allegiance rank requirements
    ///   - once per chest: independent 1/1000 rolls on each bonus entry (EliteBonusEntries)
    /// </summary>
    public static partial class LootGenerationFactory
    {
        public const uint EliteChestTreasureType = 9870001;

        private const int EliteMaxAttempts = 50;

        // quality-mod floors for the damage/wield mutation scripts (tier 6, Infiltration)
        // the top wield band is the last 15% of the melee + elemental missile scripts, and the last 6% of the elemental caster script
        public const float EliteMeleeDamageQualityMod = 0.86f;
        public const float EliteMissileDamageQualityMod = 0.86f;
        public const float EliteCasterDamageQualityMod = 0.945f;

        // ---------- bonus rolls (1 in EliteBonusOdds each, rolled once per chest) ----------

        private const int EliteBonusOdds = 1000;

        private static readonly uint[] EliteCommissaryWcids =
        {
            9871145, 9871146, 9871147, 9871148, 9871149, 9871150, 9871151,
            9871152, 9871153, 9871154, 9871155, 9871156, 9871157,
        };

        private static readonly uint[] EliteFoolproofWcids =
        {
            30094, 30095, 30096, 30097, 30098, 30099, 30100,
            30101, 30102, 30103, 30104, 30105, 30106,
        };

        // add new bonus entries here: (list of wcids to pick one from at random, stack size)
        private static readonly List<(uint[] wcids, int stackSize)> EliteBonusEntries = new List<(uint[], int)>()
        {
            ( new uint[] { 9870917 }, 100 ),   // Prestige Medal x100
            ( new uint[] { 9871098 }, 200 ),   // Blackjack Chip x200
            ( EliteCommissaryWcids,     1 ),   // random Prestige Commissary Officer item
            ( EliteFoolproofWcids,      1 ),   // random foolproof salvage
            ( new uint[] { 20630 },   100 ),   // Trade Note (250,000) x100
        };

        // ---------- bane cantrips ----------

        private static readonly HashSet<SpellId> EliteBaneCantrips = new HashSet<SpellId>()
        {
            SpellId.CANTRIPACIDBANE1,
            SpellId.CANTRIPBLUDGEONINGBANE1,
            SpellId.CANTRIPFLAMEBANE1,
            SpellId.CANTRIPFROSTBANE1,
            SpellId.CANTRIPPIERCINGBANE1,
            SpellId.CANTRIPSLASHINGBANE1,
            SpellId.CANTRIPSTORMBANE1,
        };

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
                return TreasureItemType_Orig.Weapon;     // 40%
            if (rng < 0.7f)
                return TreasureItemType_Orig.Armor;      // 30%
            if (rng < 0.8f)
                return TreasureItemType_Orig.Clothing;   // 10%
            return TreasureItemType_Orig.Jewelry;        // 20%
        }

        private static TreasureWeaponType RollEliteWeaponType()
        {
            var rng = ThreadSafeRandom.Next(0.0f, 1.0f);

            if (rng < 0.70f)
                return TreasureWeaponType.MeleeWeapon;
            if (rng < 0.85f)
                return TreasureWeaponType.MissileWeapon;
            return TreasureWeaponType.Caster;
        }

        private static int RollEliteWorkmanship()
        {
            var rng = ThreadSafeRandom.Next(0.0f, 1.0f);

            if (rng < 1.0f / 3.0f)
                return 3;
            if (rng < 2.0f / 3.0f)
                return 4;
            return 5;
        }

        private static int GetEliteWieldTarget(TreasureRoll roll)
        {
            if (roll.IsMeleeWeapon) return 400;
            if (roll.IsMissileWeapon) return 360;
            if (roll.IsCaster) return 355;
            return 0;
        }

        /// <summary>
        /// The highest AL this piece could roll at tier 6 (Infiltration ArmorLevel scripts)
        /// </summary>
        private static int GetEliteMaxArmorLevel(WorldObject wo, TreasureRoll roll)
        {
            var scriptName = GetMutationScript_ArmorLevel(wo, roll) ?? "";

            if (scriptName.EndsWith("covenant_armor_level.txt"))
                return 500;                             // 470 + Random(0, 30)
            if (scriptName.EndsWith("covenant_shield_level.txt"))
                return 450;                             // 350 + Random(0, 100)
            if (scriptName.EndsWith("shield_level.txt"))
                return roll.BaseArmorLevel + 125;       // +120 + Random(-5, 5)

            return roll.BaseArmorLevel + 165;           // armor_level.txt: +160 + Random(-5, 5)
        }

        private static void EnsureEliteNonBaneCantrip(WorldObject wo, TreasureDeath profile, TreasureRoll roll, HashSet<SpellId> cantrips)
        {
            var hasBane = false;
            var hasNonBane = false;

            foreach (var cantrip in cantrips)
            {
                if (EliteBaneCantrips.Contains(cantrip))
                    hasBane = true;
                else
                    hasNonBane = true;
            }

            if (!hasBane || hasNonBane)
                return;

            for (var i = 0; i < 30; i++)
            {
                var cantrip = RollCantrip(wo, profile, roll);

                if (cantrip != SpellId.Undef && !EliteBaneCantrips.Contains(cantrip) && cantrips.Add(cantrip))
                    return;
            }
        }

        private static void AddEliteBonusItems(List<WorldObject> loot)
        {
            foreach (var (wcids, stackSize) in EliteBonusEntries)
            {
                if (ThreadSafeRandom.Next(0.0f, 1.0f) >= 1.0f / EliteBonusOdds)
                    continue;

                var wcid = wcids[(int)(ThreadSafeRandom.Next(0.0f, 1.0f) * wcids.Length) % wcids.Length];

                var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (wo == null)
                {
                    log.Warn($"[LOOT] AddEliteBonusItems - failed to create wcid {wcid}");
                    continue;
                }

                if (stackSize > 1)
                    wo.SetStackSize(Math.Min(stackSize, (int)(wo.MaxStackSize ?? 1)));

                loot.Add(wo);
            }
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

                if (itemType == TreasureItemType_Orig.Weapon)
                    eliteProfile.ForceWeaponType = RollEliteWeaponType();

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

                // armor: 20-40 above the normal tier 6 max
                if (roll.HasArmorLevel(wo))
                    wo.ArmorLevel = GetEliteMaxArmorLevel(wo, roll) + (int)(ThreadSafeRandom.Next(0.0f, 1.0f) * 21) + 20;

                wo.ItemWorkmanship = RollEliteWorkmanship();

                return wo;
            }

            log.Warn($"[LOOT] CreateEliteLootObject({profile.TreasureType}) - no valid item after {EliteMaxAttempts} attempts");
            return null;
        }
    }
}
