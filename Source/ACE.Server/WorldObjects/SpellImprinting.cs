using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Factories.Tables;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Dark Destiny: Spell Imprinting
    ///  1. Infused Quill + Level VII scroll -> Spell Imprint (school must be specialized, Level VIII debuff check = 425 base)
    ///  2. Spell Imprint + loot-generated item -> spell added to item (Level VII debuff check = 325 base)
    /// Called from Player.HandleActionUseWithTarget before recipe / built-in spell handling.
    /// </summary>
    public static class SpellImprinting
    {
        public const uint QuillInfliction    = 38770; // Life or War imprints (casters only)
        public const uint QuillIntrospection = 38771; // Creature and Life imprints
        public const uint QuillBenevolence   = 38774; // Item imprints

        public const uint ImprintWcid = 9871116; // Spell Imprint weenie

        public const int CreateBaseSkill = 425; // Level VIII debuff
        public const int ApplyBaseSkill  = 325; // Level VII debuff

        // Success % curve, skill points above base -> chance
        private static readonly (int diff, double chance)[] Curve =
        {
            (0, 0.50), (5, 0.57), (10, 0.67), (15, 0.74), (20, 0.80), (25, 0.85), (30, 0.89),
            (35, 0.92), (40, 0.94), (45, 0.96), (50, 0.97), (55, 0.98), (60, 0.99), (76, 1.00)
        };

        /// <summary>
        /// Above base: follows the curve, linearly between points.
        /// Below base: mirrors the curve downward (-5 = 43%, -76 or lower = 0%).
        /// </summary>
        public static double GetChance(int skill, int baseSkill)
        {
            var diff = skill - baseSkill;
            if (diff < 0)
                return 1.0 - GetChance(baseSkill - diff, baseSkill);

            for (var i = 1; i < Curve.Length; i++)
            {
                if (diff <= Curve[i].diff)
                {
                    var (d0, c0) = Curve[i - 1];
                    var (d1, c1) = Curve[i];
                    return c0 + (c1 - c0) * (diff - d0) / (double)(d1 - d0);
                }
            }
            return 1.0;
        }

        public static bool IsInfusedQuill(uint wcid) => wcid == QuillInfliction || wcid == QuillIntrospection || wcid == QuillBenevolence;

        public static bool QuillAllowsSchool(uint quillWcid, MagicSchool school)
        {
            switch (quillWcid)
            {
                case QuillIntrospection: return school == MagicSchool.CreatureEnchantment || school == MagicSchool.LifeMagic;
                case QuillBenevolence:   return school == MagicSchool.ItemEnchantment;
                case QuillInfliction:    return school == MagicSchool.LifeMagic || school == MagicSchool.WarMagic;
            }
            return false;
        }

        /// <summary>
        /// Returns TRUE if this use was a Spell Imprinting interaction (handled here, use-done already sent)
        /// </summary>
        public static bool TryHandleUse(Player player, WorldObject source, WorldObject target)
        {
            if (IsInfusedQuill(source.WeenieClassId) && target.WeenieType == WeenieType.Scroll)
            {
                CreateImprint(player, source, target);
                player.SendUseDoneEvent();
                return true;
            }

            if (source.WeenieClassId == ImprintWcid)
            {
                ApplyImprint(player, source, target);
                player.SendUseDoneEvent();
                return true;
            }

            return false;
        }

        private static void Msg(Player player, string msg)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Craft));
        }

        private static string SchoolName(MagicSchool school)
        {
            switch (school)
            {
                case MagicSchool.WarMagic: return "War Magic";
                case MagicSchool.LifeMagic: return "Life Magic";
                case MagicSchool.CreatureEnchantment: return "Creature Enchantment";
                case MagicSchool.ItemEnchantment: return "Item Enchantment";
            }
            return school.ToString();
        }

        private static int GetSpellLevel(Spell spell)
        {
            var level = SpellLevelProgression.GetSpellLevel((SpellId)spell.Id);
            return level > 0 ? level : (int)spell.Level;
        }

        // ---------------- Step 2: quill + scroll -> imprint ----------------

        private static void CreateImprint(Player player, WorldObject quill, WorldObject scroll)
        {
            if (!RecipeManager.VerifyUse(player, quill, scroll, true))
            {
                Msg(player, "Both the quill and the scroll must be in your inventory.");
                return;
            }

            if (scroll.SpellDID == null)
            {
                Msg(player, $"The {scroll.Name} has no spell to imprint.");
                return;
            }

            var spell = new Spell(scroll.SpellDID.Value);
            if (spell.NotFound)
            {
                Msg(player, $"The {scroll.Name} has no spell to imprint.");
                return;
            }

            if (GetSpellLevel(spell) != 7)
            {
                Msg(player, "Only Level VII scrolls can be imprinted.");
                return;
            }

            var school = spell.School;
            if (!QuillAllowsSchool(quill.WeenieClassId, school))
            {
                Msg(player, $"The {quill.Name} cannot imprint {spell.Name}.");
                return;
            }

            var skill = player.GetCreatureSkill(school);
            if (skill == null || skill.AdvancementClass != SkillAdvancementClass.Specialized)
            {
                Msg(player, $"You must be specialized in {SchoolName(school)} to imprint {spell.Name}.");
                return;
            }

            var chance = GetChance((int)skill.Current, CreateBaseSkill);
            var success = ThreadSafeRandom.Next(0.0f, 1.0f) < chance;

            // both materials are used up either way
            if (!player.TryConsumeFromInventoryWithNetworking(quill, 1) || !player.TryConsumeFromInventoryWithNetworking(scroll, 1))
                return;

            if (!success)
            {
                Msg(player, $"You fail to imprint {spell.Name}. The quill and scroll are destroyed. ({chance * 100:0}% chance)");
                return;
            }

            var imprint = WorldObjectFactory.CreateNewWorldObject(ImprintWcid);
            if (imprint == null)
            {
                Msg(player, "Spell Imprint item is missing from the database.");
                return;
            }

            imprint.SpellDID = spell.Id;
            imprint.Name = $"Imprint of {spell.Name}";
            imprint.SetProperty(PropertyInt.SpellImprintQuill, (int)quill.WeenieClassId);

            // borrow the scroll's icon so each imprint is recognizable
            var scrollIcon = scroll.GetProperty(PropertyDataId.Icon);
            if (scrollIcon.HasValue && scrollIcon != 0)
                imprint.IconId = scrollIcon.Value;

            if (!player.TryCreateInInventoryWithNetworking(imprint))
            {
                imprint.Destroy();
                Msg(player, "You have no room for the imprint!");
                return;
            }

            Msg(player, $"You successfully imprint {spell.Name}. ({chance * 100:0}% chance)");
        }

        // ---------------- Step 3: imprint + item -> spell on item ----------------

        private static bool IsArmorClothJewelry(WorldObject t) =>
            t.ItemType == ItemType.Armor || t.ItemType == ItemType.Clothing || t.ItemType == ItemType.Jewelry;

        private static bool IsWeapon(WorldObject t) =>
            t.ItemType == ItemType.MeleeWeapon || t.ItemType == ItemType.MissileWeapon || t.ItemType == ItemType.Caster;

        private static bool IsEligible(WorldObject target, Spell spell, int quillWcid)
        {
            var school = spell.School;

            // harmful Creature / Life spells (debuffs) would hit the wearer if worn -> casters only, as the castable spell
            if (spell.IsHarmful && (school == MagicSchool.CreatureEnchantment || school == MagicSchool.LifeMagic))
                return target.ItemType == ItemType.Caster;

            switch (school)
            {
                case MagicSchool.WarMagic:
                    return target.ItemType == ItemType.Caster;
                case MagicSchool.CreatureEnchantment:
                    return IsArmorClothJewelry(target);
                case MagicSchool.LifeMagic:
                    if (quillWcid == QuillInfliction)
                        return target.ItemType == ItemType.Caster;
                    return IsArmorClothJewelry(target) || target.ItemType == ItemType.Caster;
                case MagicSchool.ItemEnchantment:
                    return target.ItemType == ItemType.Armor || IsWeapon(target);
            }
            return false;
        }

        private static void ApplyImprint(Player player, WorldObject imprint, WorldObject target)
        {
            if (!RecipeManager.VerifyUse(player, imprint, target, true))
            {
                Msg(player, "Both the imprint and the item must be in your inventory, and the item can't be equipped.");
                return;
            }

            if (imprint.SpellDID == null)
                return;

            var spell = new Spell(imprint.SpellDID.Value);
            if (spell.NotFound)
                return;

            if (target.ItemWorkmanship == null)
            {
                Msg(player, $"Imprints can only be applied to loot-generated items.");
                return;
            }

            if (target.GetProperty(PropertyInt.SpellImprinted) != null)
            {
                Msg(player, $"The {target.NameWithMaterial} already carries an imprint.");
                return;
            }

            var school = spell.School;
            var quillWcid = imprint.GetProperty(PropertyInt.SpellImprintQuill) ?? 0;

            if (!IsEligible(target, spell, quillWcid))
            {
                Msg(player, $"{spell.Name} cannot be imprinted onto the {target.NameWithMaterial}.");
                return;
            }

            // casters get War / Life / harmful Creature imprints as their castable built-in spell (replaces any existing one)
            var castable = target.ItemType == ItemType.Caster && (school == MagicSchool.WarMagic || school == MagicSchool.LifeMagic
                || (school == MagicSchool.CreatureEnchantment && spell.IsHarmful));

            SpellTransferScroll.InjectSpellData data = default;
            if (!castable)
            {
                // validate only (nothing is changed yet)
                data = SpellTransferScroll.InjectSpell(target, (SpellId)spell.Id, true, true, true);
                if (data.Result != SpellTransferScroll.InjectSpellResult.ReadyToProceed)
                {
                    switch (data.Result)
                    {
                        case SpellTransferScroll.InjectSpellResult.TargetAlreadyContainsSpell:
                            Msg(player, $"The {target.NameWithMaterial} already contains {spell.Name}."); break;
                        case SpellTransferScroll.InjectSpellResult.TargetContainsStrongerSpell:
                            Msg(player, $"The {target.NameWithMaterial} already contains {data.SpellOnItem.Name}, which is stronger than {spell.Name}."); break;
                        case SpellTransferScroll.InjectSpellResult.TargetContainsEquivalentSpell:
                            Msg(player, $"The {target.NameWithMaterial} already contains {data.SpellOnItem.Name}, which is equivalent to {spell.Name}."); break;
                        default:
                            Msg(player, $"The {target.NameWithMaterial} cannot contain {spell.Name}."); break;
                    }
                    return;
                }
            }

            var skill = player.GetCreatureSkill(school);
            var chance = GetChance((int)(skill?.Current ?? 0), ApplyBaseSkill);
            var success = ThreadSafeRandom.Next(0.0f, 1.0f) < chance;

            if (!player.TryConsumeFromInventoryWithNetworking(imprint, 1))
                return;

            if (!success)
            {
                Msg(player, $"You fail to apply the imprint of {spell.Name}. The imprint is destroyed. ({chance * 100:0}% chance)");
                return;
            }

            if (castable)
                ApplyCastable(target, spell);
            else
                SpellTransferScroll.ContinueInjectSpell(data);

            target.SetProperty(PropertyInt.SpellImprinted, (int)spell.Id);

            player.EnqueueBroadcast(new GameMessageUpdateObject(target));

            Msg(player, $"You successfully imprint {spell.Name} onto the {target.NameWithMaterial}. ({chance * 100:0}% chance)");
        }

        /// <summary>
        /// Makes the imprinted spell the caster's built-in castable spell, same setup loot generation uses.
        /// </summary>
        private static void ApplyCastable(WorldObject target, Spell spell)
        {
            target.SpellDID = spell.Id;

            var castableMod = CasterSlotSpells.IsOrb(target) ? 5.0f : 2.5f;
            target.ItemManaCost = (int)(spell.BaseMana * castableMod);
            target.ItemUseable = Usable.SourceWieldedTargetRemoteNeverWalk;

            if (ConfigManager.Config.Server.WorldRuleset == Ruleset.CustomDM)
            {
                target.CooldownId = 1001;
                target.CooldownDuration = 10;
            }

            // non-magical casters have no mana pool yet
            var newMaxMana = (int)spell.BaseMana * 15;
            if (newMaxMana > (target.ItemMaxMana ?? 0))
            {
                target.ItemMaxMana = newMaxMana;
                target.ItemCurMana = newMaxMana;
                target.ManaRate = LootGenerationFactory.CalculateManaRate(LootGenerationFactory.GetMaxBaseMana(target));
            }

            var allSpells = target.Biota.GetKnownSpellsIds(target.BiotaDatabaseLock).Select(i => (SpellId)i).ToList();
            allSpells.Add((SpellId)spell.Id);
            if (target.ProcSpell.HasValue && target.ProcSpell != 0)
                allSpells.Add((SpellId)target.ProcSpell);

            var lifeCreature = new List<SpellId>();
            var cantrips = new List<SpellId>();
            foreach (var id in allSpells)
            {
                if (id == (SpellId)spell.Id) continue;
                var s = new Spell(id);
                if (s.NotFound) continue;
                if (s.IsCantrip) cantrips.Add(id);
                else if (s.School == MagicSchool.CreatureEnchantment || s.School == MagicSchool.LifeMagic) lifeCreature.Add(id);
            }

            LootGenerationFactory.CalculateSpellcraft(target, allSpells, true, out var minSpellcraft, out var maxSpellcraft, out var rolledSpellcraft, out _);
            LootGenerationFactory.CalculateArcaneLore(target, allSpells, lifeCreature, cantrips, minSpellcraft, maxSpellcraft, rolledSpellcraft, true, out _, out _, out _);

            if (!target.UiEffects.HasValue)
                target.UiEffects = UiEffects.Magical;

            target.LongDesc = LootGenerationFactory.GetLongDesc(target);
        }
    }
}
