using System;

using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public static class SalvageBagCommands
    {
        /// <summary>
        /// Creates a full, real salvage bag (same WCID + same properties the salvaging code produces).
        /// Usage: /salvagebag <material name or number> [workmanship 1-10, default 10] [items salvaged, default 10]
        /// </summary>
        [CommandHandler("salvagebag", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Creates a full 100-unit salvage bag, built exactly like real salvaging.",
            "<material name or number> [workmanship 1-10, default 10] [items salvaged, default 10]\nex: /salvagebag lavenderjade 10\n    /salvagebag greengarnet 7.5 20")]
        public static void HandleSalvageBag(Session session, params string[] parameters)
        {
            var player = session.Player;

            void Msg(string text) => session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            // material: accept enum name (spaces/case ignored) or number
            var matText = parameters[0].Replace(" ", "").Replace("_", "").Replace("'", "");
            if (!Enum.TryParse(matText, true, out MaterialType material) || !Enum.IsDefined(typeof(MaterialType), material))
            {
                Msg($"Unknown material '{parameters[0]}'. Use the material name (ex: lavenderjade, greengarnet, turquoise) or its number.");
                return;
            }

            if (!Player.MaterialSalvage.TryGetValue((int)material, out var wcid) || wcid == 0)
            {
                Msg($"{material} has no salvage bag.");
                return;
            }

            float workmanship = 10.0f;
            if (parameters.Length > 1 && (!float.TryParse(parameters[1], out workmanship) || workmanship < 1.0f || workmanship > 10.0f))
            {
                Msg("Workmanship must be a number from 1 to 10.");
                return;
            }

            int numItems = 10;
            if (parameters.Length > 2 && (!int.TryParse(parameters[2], out numItems) || numItems < 1 || numItems > 1000))
            {
                Msg("Items salvaged must be a whole number from 1 to 1000.");
                return;
            }

            var bag = WorldObjectFactory.CreateNewWorldObject((uint)wcid);
            if (bag == null)
            {
                Msg($"Could not create salvage bag WCID {wcid} for {material}.");
                return;
            }

            // mirror Player.GetSalvageBag + TryAddSalvage (Player_Crafting.cs)
            var maxStructure = bag.MaxStructure ?? 100;
            bag.Structure = (ushort)maxStructure;
            bag.NumItemsInMaterial = numItems;
            bag.ItemWorkmanship = (int)Math.Round(workmanship * numItems);   // Workmanship = ItemWorkmanship / NumItemsInMaterial
            bag.Name = $"Salvage ({bag.Structure})";

            if (!player.TryCreateInInventoryWithNetworking(bag))
            {
                Msg("Could not place the bag in your inventory (full?).");
                bag.Destroy();
                return;
            }

            Msg($"Created {material} salvage (WCID {wcid}): {bag.Structure} units, workmanship {bag.Workmanship:0.00}, from {numItems} items.");
            PlayerManager.BroadcastToAuditChannel(player, $"{player.Name} created a {material} salvage bag (0x{bag.Guid:X8}) via /salvagebag.");
        }
    }
}
