using System;

using log4net;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Workmanship Reduction Tools (Apprentice/Journeyman/Expert/Master = -1/-2/-3/-4).
    /// A Gem weenie carrying PropertyInt.WorkmanshipReduction (10043) = grades removed.
    /// Always succeeds; floor = 1; loot-generated weapons/casters/armor/shields only; tool consumed.
    /// </summary>
    public static class WorkmanshipTool
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const int MinWorkmanship = 1;

        public static int GetReduction(WorldObject wo) => wo?.GetProperty(PropertyInt.WorkmanshipReduction) ?? 0;

        public static bool IsWorkmanshipTool(WorldObject wo) => GetReduction(wo) > 0;

        public static string VerifyTarget(Player player, WorldObject source, WorldObject target)
        {
            if (source == target)
                return $"You can't use the {source.Name} on itself.";

            if (player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory) == null)
                return $"The {source.Name} must be in your inventory.";

            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) == null)
                return $"The {target.NameWithMaterial} must be in your pack (not equipped).";

            if ((target.ItemType & (ItemType.WeaponOrCaster | ItemType.Armor)) == 0)
                return $"The {source.Name} can only be used on weapons or armor.";

            // loot-generated items only (quest items have no ItemWorkmanship)
            if (target.ItemWorkmanship == null || target.Workmanship == null)
                return $"The {source.Name} can only be used on loot-generated items.";

            if (Math.Round(target.Workmanship.Value) <= MinWorkmanship)
                return $"The {target.NameWithMaterial} is already at the lowest workmanship.";

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

            var oldW = (int)Math.Round(target.Workmanship.Value);
            var newW = Math.Max(MinWorkmanship, oldW - GetReduction(source));

            // yes/no dialog (respects the player's crafting dialog option)
            if (!confirmed && player.GetCharacterOption(CharacterOption.UseCraftingChanceOfSuccessDialog))
            {
                var msg = $"Lower the workmanship of {target.NameWithMaterial} from {oldW} to {newW}?\n\nThis cannot be undone.";
                var confirm = new Confirmation_Custom(player.Guid, () => UseObjectOnTarget(player, source, target, true));

                if (!player.ConfirmationManager.EnqueueSend(confirm, msg))
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
            var clapTime = Physics.Animation.MotionTable.GetAnimationLength(player.MotionTableId, currentStance, motionCommand);

            actionChain.AddAction(player, () => player.SendMotionAsCommands(motionCommand, currentStance));
            actionChain.AddDelaySeconds(clapTime);
            nextUseTime += clapTime;

            actionChain.AddAction(player, () =>
            {
                var reError = VerifyTarget(player, source, target);
                if (reError != null)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(reError, ChatMessageType.Craft));
                    return;
                }

                var curW = (int)Math.Round(target.Workmanship.Value);
                var finalW = Math.Max(MinWorkmanship, curW - GetReduction(source));

                // Workmanship setter writes ItemWorkmanship (x NumItemsInMaterial)
                target.Workmanship = finalW;
                player.UpdateProperty(target, PropertyInt.ItemWorkmanship, target.ItemWorkmanship);

                player.TryConsumeFromInventoryWithNetworking(source, 1);

                log.Info($"[WORKMANSHIPTOOL] Player = {player.Name}; Tool = {source.Name}; Target = {target.Name}; {curW} -> {finalW}");

                player.EnqueueBroadcast(new GameMessageSystemChat($"{player.Name} lowers the workmanship of the {target.NameWithMaterial} from {curW} to {finalW}.", ChatMessageType.Craft), WorldObject.LocalBroadcastRange, ChatMessageType.Craft);

                if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) != null)
                    player.MoveItemToFirstContainerSlot(target);
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
    }
}
