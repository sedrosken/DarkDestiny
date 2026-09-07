using System.Collections.Concurrent;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public static class FlyingCarpetCommands
    {
        private const uint CarpetWcid    = 22255;
        private const float CarpetZOffset = -0.05f; // carpet sits just under the player's feet
        private const double TickSeconds  = 0.125;  // re-assert sit + park carpet 8x/sec

        private static readonly ConcurrentDictionary<uint, WorldObject> Carpets = new ConcurrentDictionary<uint, WorldObject>();

        [CommandHandler("carpet", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Flying carpet illusion (spectator view). Toggle on/off. Move around normally.")]
        public static void HandleCarpet(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (Carpets.TryRemove(player.Guid.Full, out var existing))
            {
                existing?.Destroy();
                player.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.Ready));
                session.Network.EnqueueSend(new GameMessageSystemChat("Carpet dismissed.", ChatMessageType.Broadcast));
                return;
            }

            var carpet = WorldObjectFactory.CreateNewWorldObject(CarpetWcid);
            if (carpet == null)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"Could not create WCID {CarpetWcid}.", ChatMessageType.Broadcast));
                return;
            }

            carpet.GravityStatus    = false;
            carpet.IgnoreCollisions = true;

            var cpos = new Position(player.Location);
            cpos.PositionZ += CarpetZOffset;
            carpet.Location = cpos;
            carpet.EnterWorld();

            Carpets[player.Guid.Full] = carpet;

            player.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.SitCrossleggedState));

            var chain = new ActionChain();
            chain.AddDelaySeconds(TickSeconds);
            chain.AddAction(player, () => CarpetTick(player));
            chain.EnqueueChain();

            session.Network.EnqueueSend(new GameMessageSystemChat("Carpet on. Others see you seated. /carpet again to stop.", ChatMessageType.Broadcast));
        }

        private static void CarpetTick(Player player)
        {
            if (!Carpets.TryGetValue(player.Guid.Full, out var carpet) || carpet.PhysicsObj == null || player.PhysicsObj == null)
                return;

            // park the carpet under the player
            var cpos = new Position(player.Location);
            cpos.PositionZ += CarpetZOffset;
            carpet.Location = cpos;
            var setPos = new Physics.Common.SetPosition(cpos.PhysPosition(),
                Physics.Common.SetPositionFlags.Teleport | Physics.Common.SetPositionFlags.Slide);
            carpet.PhysicsObj.SetPosition(setPos);
            carpet.EnqueueBroadcast(new GameMessageUpdatePosition(carpet));

            // re-assert the seated look for spectators
            player.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.SitCrossleggedState));

            var chain = new ActionChain();
            chain.AddDelaySeconds(TickSeconds);
            chain.AddAction(player, () => CarpetTick(player));
            chain.EnqueueChain();
        }
    }
}
