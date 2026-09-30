using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Command.Handlers
{
    public static class SignalCommands
    {
        // @signal <message> - emits a LocalSignal from your character, exactly like a monster/NPC LocalSignal emote.
        // Listeners (e.g. Colo Room Coordinator) react as if a creature sent it. Stand near the listener.
        [CommandHandler("signal", AccessLevel.Envoy, CommandHandlerFlag.RequiresWorld, 1,
            "Emits a local signal from your character, as if an emote sent it.",
            "<signal message>  e.g. @signal ColoCritterSpawned")]
        public static void HandleSignal(Session session, params string[] parameters)
        {
            var player = session.Player;
            var message = string.Join(" ", parameters).Trim();

            if (string.IsNullOrEmpty(message))
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("Usage: @signal <message>", ChatMessageType.Broadcast));
                return;
            }

            if (player.CurrentLandblock == null)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("No current landblock - signal not sent.", ChatMessageType.Broadcast));
                return;
            }

            player.CurrentLandblock.EmitSignal(player, message);

            session.Network.EnqueueSend(new GameMessageSystemChat($"Signal sent: {message}", ChatMessageType.Broadcast));
        }

        // @colotrace [on|off] - toggles the [COLOTRACE] Colosseum debug logging (on by default)
        [CommandHandler("colotrace", AccessLevel.Envoy, CommandHandlerFlag.None, 0,
            "Toggles Colosseum trace logging ([COLOTRACE] lines in the server log).",
            "[on|off]")]
        public static void HandleColoTrace(Session session, params string[] parameters)
        {
            if (parameters.Length > 0 && parameters[0].Equals("on", System.StringComparison.OrdinalIgnoreCase))
                EmoteManager.ColoTrace = true;
            else if (parameters.Length > 0 && parameters[0].Equals("off", System.StringComparison.OrdinalIgnoreCase))
                EmoteManager.ColoTrace = false;
            else
                EmoteManager.ColoTrace = !EmoteManager.ColoTrace;

            CommandHandlerHelper.WriteOutputInfo(session, $"Colosseum trace is now {(EmoteManager.ColoTrace ? "ON" : "OFF")}.", ChatMessageType.Broadcast);
        }
    }
}
