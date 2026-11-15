using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Factories.Entity;

namespace ACE.Server.Factories.Tables
{
    /// <summary>
    /// Infiltration: cantrip table for TreasureItemType_Orig.Clothing (shirts, pants, robes, cloth caps / gloves / shoes, etc.)
    /// Clothing can roll just about any wearable cantrip. Item-armor cantrips (Impenetrability / banes) are only
    /// added when the piece actually has an ArmorLevel, since they do nothing on clothing without AL.
    /// Not populated for other rulesets (RollCantrip only uses this table on Infiltration).
    /// </summary>
    public static class ClothingCantrips
    {
        private static readonly List<(SpellId spellId, float weight)> baseCantrips = new List<(SpellId, float)>();

        // only valid on pieces with ArmorLevel > 0
        private static readonly List<(SpellId spellId, float weight)> armorLevelCantrips = new List<(SpellId, float)>();

        private static ChanceTable<SpellId> clothingCantrips = new ChanceTable<SpellId>(ChanceTableType.Weight);

        private static ChanceTable<SpellId> clothingArmorLevelCantrips = new ChanceTable<SpellId>(ChanceTableType.Weight);

        static ClothingCantrips()
        {
            if (Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.Infiltration)
                return;

            baseCantrips.AddRange(new List<(SpellId, float)>()
            {
                // attributes
                ( SpellId.CANTRIPSTRENGTH1,                    1.0f ),
                ( SpellId.CANTRIPENDURANCE1,                   1.0f ),
                ( SpellId.CANTRIPCOORDINATION1,                1.0f ),
                ( SpellId.CANTRIPQUICKNESS1,                   1.0f ),
                ( SpellId.CANTRIPFOCUS1,                       1.0f ),
                ( SpellId.CANTRIPWILLPOWER1,                   1.0f ),

                // vital regeneration
                ( SpellId.CANTRIPHEALTHGAIN1,                  1.0f ),
                ( SpellId.CANTRIPSTAMINAGAIN1,                 1.0f ),
                ( SpellId.CANTRIPMANAGAIN1,                    1.0f ),

                // weapon skills
                ( SpellId.CANTRIPLIGHTWEAPONSAPTITUDE1,        1.0f ), // CANTRIPAXEAPTITUDE1
                ( SpellId.CANTRIPFINESSEWEAPONSAPTITUDE1,      1.0f ), // CANTRIPDAGGERAPTITUDE1
                ( SpellId.CANTRIPMACEAPTITUDE1,                1.0f ),
                ( SpellId.CANTRIPSPEARAPTITUDE1,               1.0f ),
                ( SpellId.CANTRIPSTAFFAPTITUDE1,               1.0f ),
                ( SpellId.CANTRIPHEAVYWEAPONSAPTITUDE1,        1.0f ), // CANTRIPSWORDAPTITUDE1
                ( SpellId.CANTRIPUNARMEDAPTITUDE1,             1.0f ),
                ( SpellId.CANTRIPMISSILEWEAPONSAPTITUDE1,      1.0f ), // CANTRIPBOWAPTITUDE1
                ( SpellId.CANTRIPCROSSBOWAPTITUDE1,            1.0f ),
                ( SpellId.CANTRIPTHROWNAPTITUDE1,              1.0f ),

                // defense
                ( SpellId.CANTRIPIMPREGNABILITY1,              1.0f ),
                ( SpellId.CANTRIPINVULNERABILITY1,             1.0f ),
                ( SpellId.CANTRIPMAGICRESISTANCE1,             1.0f ),

                // magic schools
                ( SpellId.CANTRIPCREATUREENCHANTMENTAPTITUDE1, 1.0f ),
                ( SpellId.CANTRIPITEMENCHANTMENTAPTITUDE1,     1.0f ),
                ( SpellId.CANTRIPLIFEMAGICAPTITUDE1,           1.0f ),
                ( SpellId.CANTRIPWARMAGICAPTITUDE1,            1.0f ),

                // protections (creature buffs, work without AL)
                ( SpellId.CANTRIPARMOR1,                       1.0f ),
                ( SpellId.CANTRIPACIDWARD1,                    1.0f ),
                ( SpellId.CANTRIPBLUDGEONINGWARD1,             1.0f ),
                ( SpellId.CANTRIPFLAMEWARD1,                   1.0f ),
                ( SpellId.CANTRIPFROSTWARD1,                   1.0f ),
                ( SpellId.CANTRIPPIERCINGWARD1,                1.0f ),
                ( SpellId.CANTRIPSLASHINGWARD1,                1.0f ),
                ( SpellId.CANTRIPSTORMWARD1,                   1.0f ),

                // combat specials
                ( SpellId.CantripDualWieldAptitude1,           1.0f ),
                ( SpellId.CantripDirtyFightingProwess1,        1.0f ),
                ( SpellId.CantripRecklessnessProwess1,         1.0f ),
                ( SpellId.CantripShieldAptitude1,              1.0f ),
                ( SpellId.CantripSneakAttackProwess1,          1.0f ),

                // misc / trade skills
                ( SpellId.CANTRIPALCHEMICALPROWESS1,           0.5f ),
                ( SpellId.CANTRIPARCANEPROWESS1,               0.5f ),
                ( SpellId.CANTRIPARMOREXPERTISE1,              0.5f ),
                ( SpellId.CANTRIPCOOKINGPROWESS1,              0.5f ),
                ( SpellId.CANTRIPDECEPTIONPROWESS1,            0.5f ),
                ( SpellId.CANTRIPFEALTY1,                      0.5f ),
                ( SpellId.CANTRIPFLETCHINGPROWESS1,            0.5f ),
                ( SpellId.CANTRIPHEALINGPROWESS1,              0.5f ),
                ( SpellId.CANTRIPITEMEXPERTISE1,               0.5f ),
                ( SpellId.CANTRIPJUMPINGPROWESS1,              0.5f ),
                ( SpellId.CANTRIPLEADERSHIP1,                  0.5f ),
                ( SpellId.CANTRIPLOCKPICKPROWESS1,             0.5f ),
                ( SpellId.CANTRIPMAGICITEMEXPERTISE1,          0.5f ),
                ( SpellId.CANTRIPMANACONVERSIONPROWESS1,       0.5f ),
                ( SpellId.CANTRIPMONSTERATTUNEMENT1,           0.25f ),
                ( SpellId.CANTRIPPERSONATTUNEMENT1,            0.25f ),
                ( SpellId.CantripSalvaging1,                   0.5f ),
                ( SpellId.CANTRIPSPRINT1,                      0.5f ),
                ( SpellId.CANTRIPWEAPONEXPERTISE1,             0.5f ),
            });

            armorLevelCantrips.AddRange(new List<(SpellId, float)>()
            {
                ( SpellId.CANTRIPIMPENETRABILITY1,             1.0f ),
                ( SpellId.CANTRIPACIDBANE1,                    1.0f ),
                ( SpellId.CANTRIPBLUDGEONINGBANE1,             1.0f ),
                ( SpellId.CANTRIPFLAMEBANE1,                   1.0f ),
                ( SpellId.CANTRIPFROSTBANE1,                   1.0f ),
                ( SpellId.CANTRIPPIERCINGBANE1,                1.0f ),
                ( SpellId.CANTRIPSLASHINGBANE1,                1.0f ),
                ( SpellId.CANTRIPSTORMBANE1,                   1.0f ),
            });

            clothingCantrips = new ChanceTable<SpellId>(ChanceTableType.Weight);
            clothingCantrips.AddRange(baseCantrips);

            clothingArmorLevelCantrips = new ChanceTable<SpellId>(ChanceTableType.Weight);
            clothingArmorLevelCantrips.AddRange(baseCantrips);
            clothingArmorLevelCantrips.AddRange(armorLevelCantrips);
        }

        public static SpellId Roll(bool hasArmorLevel)
        {
            var table = hasArmorLevel ? clothingArmorLevelCantrips : clothingCantrips;

            if (table.Count == 0)
                return SpellId.Undef;

            return table.Roll();
        }

        public static List<SpellId> GetSpellIdList()
        {
            return baseCantrips.Select(i => i.spellId).Concat(armorLevelCantrips.Select(i => i.spellId)).ToList();
        }
    }
}
