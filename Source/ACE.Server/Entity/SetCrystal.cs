using System;

using log4net;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Prestige set crystals (sold at the prestige vendor). A crystal is a CraftTool weenie carrying
    /// PropertyInt.SetCrystal (= SetCrystalType). Used on a loot-generated item it imbues that item
    /// with the crystal: one crystal per item, independent of salvage imbues.
    ///
    /// Chance = 33% base (Heroic 10%) + Item Tinkering bonus (trained/spec only: +11% per 100 skill, max +33%)
    ///          + 5% per imbue augmentation + 5% from a full Acrobat's (Dexterous) set.
    /// Failure destroys only the crystal; the item is never harmed.
    /// </summary>
    public static class SetCrystal
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const float BaseChance = 0.33f;
        public const float HeroicBaseChance = 0.10f;
        public const float SkillBonusPerPoint = 0.0011f;   // 100 skill = +11%
        public const float MaxSkillBonus = 0.33f;           // 300 skill = +33%

        public static SetCrystalType GetCrystalType(WorldObject wo)
        {
            return (SetCrystalType)(wo.GetProperty(PropertyInt.SetCrystal) ?? 0);
        }

        public static bool IsSetCrystal(WorldObject wo)
        {
            if (wo == null || wo.WeenieType != WeenieType.CraftTool)
                return false;

            var type = (int)GetCrystalType(wo);
            return type >= (int)SetCrystalType.Guardian && type <= (int)SetCrystalType.HeroicDestroyer;
        }

        public static bool IsHeroic(SetCrystalType type) => type == SetCrystalType.HeroicGuardian || type == SetCrystalType.HeroicDestroyer;

        public static double GetChance(Player player, SetCrystalType type, out float skillBonus)
        {
            var chance = IsHeroic(type) ? HeroicBaseChance : BaseChance;

            skillBonus = 0.0f;
            var itemTink = player.GetCreatureSkill(Skill.ItemTinkering);
            if (itemTink != null && itemTink.AdvancementClass >= SkillAdvancementClass.Trained)
                skillBonus = Math.Min(MaxSkillBonus, itemTink.Current * SkillBonusPerPoint);

            chance += skillBonus;

            if (player.AugmentationBonusImbueChance > 0)
                chance += player.AugmentationBonusImbueChance * 0.05f;

            chance += player.GetSetCrystalCraftingBonus();

            return Math.Clamp(chance, 0.0f, 1.0f);
        }

        /// <summary>
        /// Returns null if the crystal can be applied to the target, else the reason it can't.
        /// </summary>
        public static string VerifyTarget(Player player, WorldObject source, WorldObject target)
        {
            if (source == target)
                return $"You can't use the {source.Name} on itself.";

            if (player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory) == null)
                return $"The {source.Name} must be in your inventory.";

            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) == null)
                return $"The {target.NameWithMaterial} must be in your pack (not equipped).";

            var type = GetCrystalType(source);

            bool validType;
            string typeDesc;
            switch (type)
            {
                case SetCrystalType.HeroicGuardian:
                    validType = (target.ItemType & ItemType.Armor) != 0;
                    typeDesc = "armor or shields";
                    break;
                case SetCrystalType.HeroicDestroyer:
                    validType = (target.ItemType & ItemType.WeaponOrCaster) != 0;
                    typeDesc = "weapons";
                    break;
                default:
                    validType = (target.ItemType & (ItemType.Armor | ItemType.Clothing | ItemType.Jewelry)) != 0;
                    typeDesc = "armor, clothing or jewelry";
                    break;
            }

            if (!validType)
                return $"The {source.Name} can only be applied to {typeDesc}.";

            if (target.Workmanship == null)
                return $"The {source.Name} can only be applied to loot-generated items.";

            var existing = (SetCrystalType)(target.GetProperty(PropertyInt.SetCrystal) ?? 0);
            if (existing != SetCrystalType.None)
                return $"The {target.NameWithMaterial} is already imbued with a {Creature.GetSetCrystalName(existing)}.";

            return null;
        }

        public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)
        {
            if (player.IsBusy)
            {
                player.SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            var allowCraftInCombat = PropertyManager.GetBool("allow_combat_mode_crafting").Item;

            if (!allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                player.SendUseDoneEvent(WeenieError.YouMustBeInPeaceModeToTrade);
                return;
            }

            var error = VerifyTarget(player, source, target);
            if (error != null)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(error, ChatMessageType.Craft));
                player.SendUseDoneEvent();
                return;
            }

            var type = GetCrystalType(source);
            var crystalName = Creature.GetSetCrystalName(type);
            var chance = GetChance(player, type, out var skillBonus);

            // chance-of-success dialog (respects the player's crafting dialog option)
            if (!confirmed && player.GetCharacterOption(CharacterOption.UseCraftingChanceOfSuccessDialog))
            {
                var msg = $"Applying {crystalName} to {target.NameWithMaterial}.\n\n";
                msg += $"You determine that you have a {(chance * 100).Round()} percent chance to succeed.";
                if (skillBonus > 0)
                    msg += $"\n{(skillBonus * 100).Round()} percent is due to your Item Tinkering skill.";
                if (player.AugmentationBonusImbueChance > 0)
                    msg += $"\n{player.AugmentationBonusImbueChance * 5} percent is due to your augmentation.";
                msg += "\n\nIf this fails, only the crystal is destroyed.";

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_CraftInteration(player.Guid, source.Guid, target.Guid), msg))
                    player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);
                else
                    player.SendUseDoneEvent();
                return;
            }

            var actionChain = new ActionChain();
            var nextUseTime = 0.0f;

            player.IsBusy = true;

            if (allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
                actionChain.AddDelaySeconds(stanceTime);
                nextUseTime += stanceTime;
            }

            var motionCommand = MotionCommand.ClapHands;
            var currentStance = player.CurrentMotionState.Stance;
            var clapTime = !confirmed ? Physics.Animation.MotionTable.GetAnimationLength(player.MotionTableId, currentStance, motionCommand) : 0.0f;

            if (!confirmed)
            {
                actionChain.AddAction(player, () => player.SendMotionAsCommands(motionCommand, currentStance));
                actionChain.AddDelaySeconds(clapTime);
                nextUseTime += clapTime;
            }

            actionChain.AddAction(player, () =>
            {
                // re-verify
                var reError = VerifyTarget(player, source, target);
                if (reError != null)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(reError, ChatMessageType.Craft));
                    return;
                }

                // recompute (skill / augs / set could have changed while the dialog was open)
                var finalChance = GetChance(player, type, out _);

                var roll = ThreadSafeRandom.Next(0.0f, 1.0f);
                var success = roll < finalChance;

                log.Info($"[SETCRYSTAL] Player = {player.Name}; Crystal = {crystalName}; Target = {target.Name}; Chance = {finalChance}; Roll = {roll}; Success = {success}");

                if (success)
                    ApplyCrystal(player, type, target);

                // the crystal is consumed either way
                player.TryConsumeFromInventoryWithNetworking(source, 1);

                if (success)
                    player.EnqueueBroadcast(new GameMessageSystemChat($"{player.Name} successfully imbues the {target.NameWithMaterial} with a {crystalName}.", ChatMessageType.Craft), WorldObject.LocalBroadcastRange, ChatMessageType.Craft);
                else
                    player.EnqueueBroadcast(new GameMessageSystemChat($"{player.Name} fails to imbue the {target.NameWithMaterial}. The {crystalName} shatters.", ChatMessageType.Craft), WorldObject.LocalBroadcastRange, ChatMessageType.Craft);
            });

            actionChain.AddAction(player, () =>
            {
                if (!confirmed)
                    player.SendUseDoneEvent();
                player.IsBusy = false;
            });

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        private static void ApplyCrystal(Player player, SetCrystalType type, WorldObject target)
        {
            target.SetProperty(PropertyInt.SetCrystal, (int)type);

            if (type == SetCrystalType.HeroicGuardian)
            {
                target.GearDamageResist = (target.GearDamageResist ?? 0) + 10;
                target.GearCritDamageResist = (target.GearCritDamageResist ?? 0) + 10;
            }
            else if (type == SetCrystalType.HeroicDestroyer)
            {
                target.GearDamage = (target.GearDamage ?? 0) + 10;
                target.GearCritDamage = (target.GearCritDamage ?? 0) + 10;
            }

            // the classic client can't show ratings / custom props, so describe the crystal in text
            var line = Creature.GetSetCrystalItemDesc(type);
            if (line != null)
                target.LongDesc = string.IsNullOrEmpty(target.LongDesc) ? line : target.LongDesc + "\n" + line;

            player.EnqueueBroadcast(new GameMessageUpdateObject(target));

            // client moves an updated item to the first slot; mirror it server side (same as RecipeManager.UpdateObj)
            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) != null)
                player.MoveItemToFirstContainerSlot(target);
        }
    }
}
