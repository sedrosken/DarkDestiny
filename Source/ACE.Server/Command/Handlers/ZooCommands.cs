using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// @zoo admin commands for the pet zoo (see PetZoo.cs). Room names are one word, e.g. 1, 2, basement1, roof.
    /// Rooms whose name starts with "roof" are where oversized creatures go.
    /// </summary>
    public static class ZooCommands
    {
        [CommandHandler("zoo", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 1,
            "Pet zoo admin tools.",
            "addroom <room> [radius] | radius <room> <meters> | clearroom <room> | info | bigradius <meters> | move <animal name> <room> | wakeup <animal name> on|off | rewards [player] | block <wcid> | unblock <wcid> | blocked | rebuildpool | remove <animal name> | respawn")]
        public static void HandleZoo(Session session, params string[] parameters)
        {
            var player = session?.Player;
            if (player == null)
                return;

            var cmd = parameters[0].ToLowerInvariant();
            var rest = parameters.Skip(1).ToArray();

            string msg;
            switch (cmd)
            {
                case "addroom":
                    if (rest.Length == 1)
                        msg = PetZoo.AddRoomPoint(player, rest[0].ToLowerInvariant(), null);
                    else if (rest.Length == 2 && float.TryParse(rest[1], out var ar) && ar >= 0)
                        msg = PetZoo.AddRoomPoint(player, rest[0].ToLowerInvariant(), ar);
                    else
                        msg = "Usage: @zoo addroom <room> [radius]";
                    break;
                case "radius":
                    msg = rest.Length == 2 && float.TryParse(rest[1], out var rr) && rr >= 0 ? PetZoo.SetRoomRadius(rest[0].ToLowerInvariant(), rr) : "Usage: @zoo radius <room> <meters>";
                    break;
                case "clearroom":
                    msg = rest.Length == 1 ? PetZoo.ClearRoom(rest[0].ToLowerInvariant()) : "Usage: @zoo clearroom <room>";
                    break;
                case "info":
                    msg = PetZoo.Info();
                    break;
                case "bigradius":
                    msg = rest.Length == 1 && float.TryParse(rest[0], out var r) && r > 0 ? PetZoo.SetBigRadius(r) : "Usage: @zoo bigradius <meters>";
                    break;
                case "move":
                    msg = rest.Length >= 2
                        ? PetZoo.MoveAnimal(string.Join(" ", rest[..^1]), rest[^1].ToLowerInvariant())
                        : "Usage: @zoo move <animal name> <room>";
                    break;
                case "wakeup":
                    msg = rest.Length >= 2 && (rest[^1].ToLowerInvariant() == "on" || rest[^1].ToLowerInvariant() == "off")
                        ? PetZoo.SetWakeOnApproach(string.Join(" ", rest[..^1]), rest[^1].ToLowerInvariant() == "on")
                        : "Usage: @zoo wakeup <animal name> on|off";
                    break;
                case "block":
                case "unblock":
                    msg = rest.Length == 1 && uint.TryParse(rest[0], out var bw) ? PetZoo.Block(bw, cmd == "block") : $"Usage: @zoo {cmd} <wcid>";
                    break;
                case "blocked":
                    msg = PetZoo.ListBlocked();
                    break;
                case "rebuildpool":
                    msg = PetZoo.RebuildPool();
                    break;
                case "rewards":
                    msg = PetZoo.ListRewards(rest.Length > 0 ? string.Join(" ", rest) : null);
                    break;
                case "remove":
                    msg = rest.Length >= 1 ? PetZoo.RemoveAnimal(string.Join(" ", rest)) : "Usage: @zoo remove <animal name>";
                    break;
                case "respawn":
                    msg = PetZoo.RespawnAll();
                    break;
                default:
                    msg = "Usage: @zoo addroom|radius|clearroom|info|bigradius|move|wakeup|rewards|block|unblock|blocked|rebuildpool|remove|respawn";
                    break;
            }

            player.SendMessage(msg);
        }
    }
}
