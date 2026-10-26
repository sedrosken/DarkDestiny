using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public static class CompanionCommands
    {
        /// <summary>
        /// /giftcompanion <player name> -- gives one of your joke companions (and its remaining time) to an online player.
        /// Select the companion first to choose which one; otherwise the one with the most time left goes.
        /// </summary>
        [CommandHandler("giftcompanion", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Gives one of your companions to another online player.",
            "<player name>\nSelect a companion first to choose which one is given.")]
        public static void HandleGiftCompanion(Session session, params string[] parameters)
        {
            var player = session?.Player;
            if (player == null)
                return;

            Companions.TryGift(player, string.Join(" ", parameters).Trim());
        }

        /// <summary>
        /// /dismisscompanion -- sends one of your companions away (she doesn't take it well and attacks you).
        /// Select the companion first to choose which one; otherwise the one with the least time left goes.
        /// </summary>
        [CommandHandler("dismisscompanion", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Dismisses one of your companions. She will not be happy about it.",
            "\nSelect a companion first to choose which one is dismissed.")]
        public static void HandleDismissCompanion(Session session, params string[] parameters)
        {
            var player = session?.Player;
            if (player == null)
                return;

            Companions.TryDismiss(player);
        }
    }
}
