using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers
{
    public static class PetCommands
    {
        /// <summary>
        /// Admin/test command: grants XP directly to the currently summoned pet, so pet
        /// leveling can be tested without farming kills. Pets otherwise only earn XP from
        /// monsters they personally damage.
        /// </summary>
        [CommandHandler("petxp", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Grants XP to your currently summoned pet (for testing pet leveling).",
            "<amount>\nExample: /petxp 100000000  (one pet level)")]
        public static void HandlePetXp(Session session, params string[] parameters)
        {
            if (session?.Player == null)
                return;

            if (!long.TryParse(parameters[0], out var amount) || amount <= 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Usage: /petxp <positive amount>");
                return;
            }

            if (session.Player.CurrentActivePet == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "You have no pet summoned.");
                return;
            }

            session.Player.GrantPetXP(amount);
            CommandHandlerHelper.WriteOutputInfo(session, $"Granted {amount:N0} XP to your pet.");
        }
    }
}
