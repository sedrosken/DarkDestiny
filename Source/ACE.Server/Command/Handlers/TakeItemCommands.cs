using System.Globalization;
using System.Linq;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public static class TakeItemCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [CommandHandler("takeitem", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 1,
            "Moves an item (by guid) out of an online player's inventory or equipment into your own inventory.",
            "<guid> [player name]\n" +
            "guid accepts 0x800202FF, 800202FF, 2147615487, or the signed form UB prints (-2147351809).\n" +
            "If a player name is given, only that player is searched; otherwise all online players are searched.")]
        public static void HandleTakeItem(Session session, params string[] parameters)
        {
            var admin = session.Player;

            if (!TryParseGuid(parameters[0], out var guid))
            {
                Chat(session, $"Could not parse '{parameters[0]}' as a guid.");
                return;
            }

            // Optional player name (may contain spaces)
            Player[] candidates;

            if (parameters.Length > 1)
            {
                var name = string.Join(" ", parameters.Skip(1));
                var target = PlayerManager.GetOnlinePlayer(name);

                if (target == null)
                {
                    Chat(session, $"{name} is not online. For offline characters, move the item in ace_shard instead.");
                    return;
                }
                candidates = new[] { target };
            }
            else
                candidates = PlayerManager.GetAllOnline().ToArray();

            Player source = null;
            WorldObject item = null;
            bool wasEquipped = false;

            foreach (var player in candidates)
            {
                var found = player.FindObject(guid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems, out _, out _, out var equipped);

                if (found != null)
                {
                    source = player;
                    item = found;
                    wasEquipped = equipped;
                    break;
                }
            }

            if (item == null)
            {
                Chat(session, $"0x{guid:X8} was not found in any online player's inventory or equipment. If the owner is offline, move it in ace_shard.");
                return;
            }

            if (source == admin)
            {
                Chat(session, $"{item.Name} (0x{guid:X8}) is already yours.");
                return;
            }

            var itemName = item.Name;
            var sourceName = source.Name;

            // Remove on the source player's thread, then add on the admin's thread.
            var chain = new ActionChain();

            chain.AddAction(source, () =>
            {
                bool removed;

                if (wasEquipped)
                    removed = source.TryDequipObjectWithNetworking(item.Guid, out _, Player.DequipObjectAction.GiveItem);
                else
                    removed = source.TryRemoveFromInventoryWithNetworking(item.Guid, out _, Player.RemoveFromInventoryAction.GiveItem);

                if (!removed)
                {
                    Chat(session, $"Failed to remove {itemName} (0x{guid:X8}) from {sourceName}.");
                    return;
                }

                // Make the item vanish from the source client
                source.Session?.Network.EnqueueSend(new GameMessageDeleteObject(item));

                var addChain = new ActionChain();
                if (wasEquipped)
                    addChain.AddDelaySeconds(0.5f);
                addChain.AddAction(admin, () =>
                {
                    // Admin override: skip attuned/unique/give-permission checks, just force it in.
                    if (admin.TryCreateInInventoryWithNetworking(item))
                    {
                        Chat(session, $"Took {itemName} (0x{guid:X8}) from {sourceName}{(wasEquipped ? " (was equipped)" : "")}.");
                        log.Info($"[TAKEITEM] {admin.Name} took {itemName} (0x{guid:X8}) from {sourceName}{(wasEquipped ? " (was equipped)" : "")}");
                        PlayerManager.BroadcastToAuditChannel(admin, $"{admin.Name} took {itemName} (0x{guid:X8}) from {sourceName}.");
                        return;
                    }

                    // Couldn't fit it on the admin: give it back so it isn't lost
                    Chat(session, $"Could not add {itemName} (0x{guid:X8}) to your inventory (full/burden?). Returning it to {sourceName}.");

                    var returnChain = new ActionChain();
                    returnChain.AddAction(source, () =>
                    {
                        if (!source.TryCreateInInventoryWithNetworking(item))
                        {
                            log.Warn($"[TAKEITEM] {itemName} (0x{guid:X8}) could not be returned to {sourceName}; dropping on {admin.Name}'s landblock.");
                            item.Location = new Position(admin.Location);
                            item.Location.LandblockId = new LandblockId(item.Location.GetCell());
                            LandblockManager.AddObject(item);
                        }
                    });
                    returnChain.EnqueueChain();
                });
                addChain.EnqueueChain();
            });

            chain.EnqueueChain();
        }

        private static bool TryParseGuid(string input, out uint guid)
        {
            guid = 0;
            var s = input.Trim();

            // Explicit hex
            if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out guid);

            // Signed form as printed by UB / some client logs
            if (s.StartsWith("-"))
            {
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
                    return false;
                guid = unchecked((uint)signed);
                return true;
            }

            // Up to 8 chars that are valid hex: treat as hex (what /getinfo shows). Real decimal guids are 10 digits.
            if (s.Length <= 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out guid))
                return true;

            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out guid);
        }

        private static void Chat(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }
    }
}
