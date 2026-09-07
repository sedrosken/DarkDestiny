using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Gated leveling for custom trinkets (prototype WCID 9870775, and ~10 more later).
    ///
    /// A trinket "opts in" simply by carrying PropertyInt.GatedTrinketTier
    /// (value = highest tier unlocked: 0 = locked, 3 = L3 unlocked, 5 = maxed).
    /// No WCID list -- any equipped item with that property is gated.
    ///
    /// The XP CURVE is 100% stock: ItemXpStyle.ScalesWithLevel at ItemBaseXp = 250,000,000
    /// doubles each level, giving exactly 250M / 500M / 1B / 2B / 4B per level.
    /// This class only enforces the two UNLOCK GATES on top of that curve; the level-up
    /// itself is handled by Player.TryUnlockGatedTrinket so it reuses the stock ding.
    /// </summary>
    public static class GatedTrinket
    {
        // Cumulative ItemTotalXp needed to REACH each level under the stock curve above:
        // L1 = 250M, L2 = 750M, L3 = 1.75B, L4 = 3.75B, L5 = 7.75B.
        public const ulong Level3Threshold = 1_750_000_000;
        public const ulong Level5Threshold = 7_750_000_000;

        // XP freezes one below the next locked threshold until that tier is unlocked,
        // so the bar sits ~full (999,999,999 into L2 / 3,999,999,999 into L4).
        public const ulong Tier0Cap = Level3Threshold - 1; // 1,749,999,999  (held at L2)
        public const ulong Tier3Cap = Level5Threshold - 1; // 7,749,999,999  (held at L4)
        public const ulong Tier5Cap = Level5Threshold;     // 7,750,000,000  (maxed, no more XP)

        /// <summary>True if this item uses the gated-trinket system.</summary>
        public static bool IsGated(WorldObject item)
        {
            return item != null && item.GetProperty(PropertyInt.GatedTrinketTier) != null;
        }

        public static int GetTier(WorldObject item)
        {
            return item.GetProperty(PropertyInt.GatedTrinketTier) ?? 0;
        }

        /// <summary>Max ItemTotalXp this trinket may currently hold, given its unlock tier.</summary>
        public static ulong GetXpCap(WorldObject item)
        {
            switch (GetTier(item))
            {
                case 5:  return Tier5Cap;
                case 3:  return Tier3Cap;
                default: return Tier0Cap;
            }
        }

        /// <summary>
        /// Given the item's current total XP and a proposed gain, returns the portion of
        /// that gain the gate actually allows (0 once the item is sitting at its cap).
        /// </summary>
        public static ulong AllowedXpGain(WorldObject item, ulong currentTotalXp, ulong proposedGain)
        {
            var cap = GetXpCap(item);
            if (currentTotalXp >= cap)
                return 0;

            var room = cap - currentTotalXp;
            return proposedGain > room ? room : proposedGain;
        }

        /// <summary>
        /// Resolves the next unlock step for a gated item: the tier value to write and the
        /// ItemTotalXp threshold that dings the newly unlocked level. False if already maxed.
        /// </summary>
        public static bool TryGetNextTier(WorldObject item, out int nextTier, out ulong threshold)
        {
            switch (GetTier(item))
            {
                case 0:  nextTier = 3; threshold = Level3Threshold; return true;
                case 3:  nextTier = 5; threshold = Level5Threshold; return true;
                default: nextTier = GetTier(item); threshold = 0; return false;
            }
        }

        // ----- Per-level persistent gear ratings -----
        private static readonly Dictionary<uint, (PropertyInt Rating, int[] Curve)[]> RatingsByWcid =
            new Dictionary<uint, (PropertyInt, int[])[]>
        {
            // Varicci's (Sword) -- Damage + Damage Reduction
            { 9870775u, new[]
                {
                    (PropertyInt.GearDamage,       new[] { 3, 3, 6, 10, 15, 21 }),
                    (PropertyInt.GearDamageResist, new[] { 3, 3, 6, 10, 15, 21 }),
                }
            },
            // Lilitha's (Bow) -- Damage + Damage Reduction
            { 9870784u, new[]
                {
                    (PropertyInt.GearDamage,       new[] { 3, 3, 6, 10, 15, 21 }),
                    (PropertyInt.GearDamageResist, new[] { 3, 3, 6, 10, 15, 21 }),
                }
            },
            // Ilservian's (War Magic) -- Damage + Crit Damage
            { 9870787u, new[]
                {
                    (PropertyInt.GearDamage,     new[] { 3, 3, 6, 10, 15, 21 }),
                    (PropertyInt.GearCritDamage, new[] { 3, 3, 6, 10, 15, 21 }),
                }
            },
            // Krau-Li's (Mace) -- Damage Reduction + Crit Damage Reduction
            { 9870790u, new[]
                {
                    (PropertyInt.GearDamageResist,     new[] { 3, 3, 6, 10, 15, 21 }),
                    (PropertyInt.GearCritDamageResist, new[] { 3, 3, 6, 10, 15, 21 }),
                }
            },
            // Oswald's (Dagger) -- Crit Chance + Crit Damage
            { 9870780u, new[]
                {
                    (PropertyInt.GearCrit,       new[] { 2, 2, 4, 6, 8, 10 }),
                    (PropertyInt.GearCritDamage, new[] { 2, 2, 4, 7, 11, 16 }),
                }
            },
            // Jade's (Spear) -- Damage
            { 9870781u, new[]
                {
                    (PropertyInt.GearDamage, new[] { 6, 6, 12, 19, 27, 36 }),
                }
            },
            // Adja's (Life Magic) -- Damage Reduction + Crit Damage Reduction
            { 9870788u, new[]
                {
                    (PropertyInt.GearDamageResist,     new[] { 3, 3, 6, 10, 15, 21 }),
                    (PropertyInt.GearCritDamageResist, new[] { 3, 3, 6, 10, 15, 21 }),
                }
            },
            // Thorsten's (Axe) -- Crit Damage
            { 9870789u, new[]
                {
                    (PropertyInt.GearCritDamage, new[] { 6, 6, 12, 19, 27, 36 }),
                }
            },
            // The Arbalest's (Crossbow) -- Crit Damage
            { 9870785u, new[]
                {
                    (PropertyInt.GearCritDamage, new[] { 6, 6, 12, 19, 27, 36 }),
                }
            },
            // Palenqual's (Staff) -- Vitality (gear half; Magic Defense handled below)
            { 9870782u, new[]
                {
                    (PropertyInt.GearMaxHealth, new[] { 3, 3, 6, 10, 15, 21 }),
                }
            },
            // Jojii's (Unarmed) -- Damage Reduction (gear half; Melee Defense handled below)
            { 9870783u, new[]
                {
                    (PropertyInt.GearDamageResist, new[] { 3, 3, 6, 10, 15, 21 }),
                }
            },
            // Asmolum's (Thrown) -- Damage (gear half; Missile Defense handled below)
            { 9870786u, new[]
                {
                    (PropertyInt.GearDamage, new[] { 3, 3, 6, 10, 15, 21 }),
                }
            },
        };

        private static int RatingForLevel(int[] table, int level)
        {
            if (level < 0) level = 0;
            if (level >= table.Length) level = table.Length - 1;
            return table[level];
        }

        /// <summary>
        /// Stamps the trinket's persistent gear ratings to match its current ItemLevel, per
        /// that trinket's configured curves. Safe to call on any item (no-op if the WCID has
        /// no configured ratings). Call whenever the level changes.
        /// </summary>
        public static void ApplyRatings(WorldObject trinket)
        {
            if (trinket == null)
                return;

            // defense-skill trinkets refresh their wearer skill buff on every level change
            ApplyDefenseSkill(trinket, trinket.Wielder as Creature);

            if (!RatingsByWcid.TryGetValue(trinket.WeenieClassId, out var ratings))
                return;

            var level = trinket.ItemLevel ?? 0;

            foreach (var r in ratings)
                trinket.SetProperty(r.Rating, RatingForLevel(r.Curve, level));

            trinket.SaveBiotaToDatabase();
        }

        // ----- Per-level defense-skill buff (Magic / Melee / Missile Defense) -----
        private static readonly Dictionary<uint, (Skill Skill, int[] Curve)> DefenseSkillByWcid =
            new Dictionary<uint, (Skill, int[])>
        {
            { 9870782u, (Skill.MagicDefense,   new[] { 1, 3, 6, 10, 15, 21 }) }, // Palenqual's (Staff)
            { 9870783u, (Skill.MeleeDefense,   new[] { 1, 3, 6, 10, 15, 21 }) }, // Jojii's (Unarmed)
            { 9870786u, (Skill.MissileDefense, new[] { 1, 3, 6, 10, 15, 21 }) }, // Asmolum's (Thrown)
        };

        // Real spell IDs written by MakeCustomSpells / the SQL:
        //   Melee Defense   6341-6346  (+1/+3/+6/+10/+15/+21)
        //   Missile Defense 6347-6352
        //   Magic Defense   6353-6358
        // Curve index matches ItemLevel (0..5).
        private static int DefenseSpellId(Skill skill, int level)
        {
            if (level < 0) level = 0;
            if (level > 5) level = 5;

            return skill switch
            {
                Skill.MeleeDefense   => 6341 + level,
                Skill.MissileDefense => 6347 + level,
                Skill.MagicDefense   => 6353 + level,
                _                    => 0
            };
        }

        /// <summary>
        /// Stamps/refreshes the trinket's defense-skill buff on the wearer to match its ItemLevel.
        /// No-op if the WCID has no defense curve or the trinket is not worn.
        /// </summary>
        public static void ApplyDefenseSkill(WorldObject trinket, Creature wielder)
        {
            if (trinket == null || wielder == null)
                return;

            if (!DefenseSkillByWcid.TryGetValue(trinket.WeenieClassId, out var def))
                return;

            var level     = trinket.ItemLevel ?? 0;
            var spellId   = DefenseSpellId(def.Skill, level);
            var magnitude = RatingForLevel(def.Curve, level);

            if (spellId == 0)
                return;

            System.Console.WriteLine($"[DefSkill] {def.Skill} lvl {level} id {spellId} mag {magnitude} -> {wielder.Name}");

            // Remove any tier this trinket may have applied, then cast the current one.
            // Standard item-spell path -> goes through EnchantmentManager, updates the
            // skill, and pushes the enchantment to the client live (no relog needed).
            for (int i = 0; i <= 5; i++)
                wielder.RemoveItemSpell(trinket, (uint)DefenseSpellId(def.Skill, i));

            wielder.CreateItemSpell(trinket, (uint)spellId);
        }

        /// <summary>Removes the trinket's defense-skill buff from the wearer (call on unequip).</summary>
        public static void RemoveDefenseSkill(WorldObject trinket, Creature wielder)
        {
            if (trinket == null || wielder == null)
                return;

            if (!DefenseSkillByWcid.TryGetValue(trinket.WeenieClassId, out var def))
                return;

            for (int i = 0; i <= 5; i++)
                wielder.RemoveItemSpell(trinket, (uint)DefenseSpellId(def.Skill, i));
        }

        /// <summary>
        /// For the item ID/appraisal panel: if this trinket carries a defense-skill buff,
        /// reports the skill it raises, the magnitude at the trinket's CURRENT ItemLevel,
        /// and the matching tier spell id to list on the panel. False for any other item.
        /// </summary>
        public static bool TryGetDefenseDisplay(WorldObject trinket, out Skill skill, out int magnitude, out uint spellId)
        {
            skill     = Skill.None;
            magnitude = 0;
            spellId   = 0;

            if (trinket == null || !DefenseSkillByWcid.TryGetValue(trinket.WeenieClassId, out var def))
                return false;

            var level = trinket.ItemLevel ?? 0;

            skill     = def.Skill;
            magnitude = RatingForLevel(def.Curve, level);
            spellId   = (uint)DefenseSpellId(def.Skill, level);

            return spellId != 0;
        }
    }
}
