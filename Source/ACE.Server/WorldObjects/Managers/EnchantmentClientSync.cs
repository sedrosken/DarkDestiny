using System.Linq;

using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.Structure;

namespace ACE.Server.WorldObjects.Managers
{
    public static class EnchantmentClientSync
    {
        /// <summary>
        /// Call right after a newly added GEAR (equip) enchantment has been sent to a player's client.
        /// If the server decided a timed buff (self-cast / player-cast) outranks that gear enchantment
        /// in the same spell category, the client may still prefer the gear one (it ranks by recency /
        /// infinite duration), so re-send both with adjusted start times to make the client agree
        /// with the server's choice. Expiry times are preserved.
        /// </summary>
        public static void SyncClientWinner(this EnchantmentManager manager, PropertiesEnchantmentRegistry added)
        {
            var player = manager.Player;
            if (player == null || added == null)
                return;

            var winner = player.Biota.PropertiesEnchantmentRegistry
                .GetEnchantmentsTopLayer(player.BiotaDatabaseLock, SpellSet.SetSpells)
                ?.FirstOrDefault(e => e.SpellCategory == added.SpellCategory);

            // nothing to do unless a different, timed (non-gear) enchantment won
            if (winner == null || ReferenceEquals(winner, added) || winner.Duration < 0)
                return;

            // present the gear enchantment as slightly older...
            var gear = new Enchantment(player, added);
            gear.StartTime = -1.0;

            // ...and the winner as freshly started, with Duration reduced so the expiry time is unchanged
            var top = new Enchantment(player, winner);
            top.Duration = winner.Duration + winner.StartTime;   // StartTime is <= 0 (elapsed)
            top.StartTime = 0;

            player.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(player.Session, gear));
            player.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(player.Session, top));
        }
    }
}
