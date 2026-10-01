using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Managers;

using Position = ACE.Entity.Position;

namespace ACE.Server.Network.Managers
{
    /// <summary>
    /// Limits how many accounts per IP may roam the world at once. Extra accounts are confined to the
    /// Abandoned Mine landblock and are booted if they try to leave it.
    /// </summary>
    public static class IpAccountLimiter
    {
        // portalabandonedmine (weenie 1595) destination in the world DB: 0x01C9022D
        public const uint PenLandblock = 0x01C9;

        public static readonly Position MinePen = new Position(0x01C9022D, 72.9f, -30.2f, 0f, 0f, 0f, -0.990268f, 0.139173f);

        public const string BootMessage = " because too many accounts are connected from your IP; additional accounts must stay in the Abandoned Mine";

        public static bool IsPenLandblock(uint cell) => (cell >> 16) == PenLandblock;

        /// <summary>
        /// Called once per login on the world thread (WorldManager.DoPlayerEnterWorld),
        /// so two accounts logging in at the same moment can't race each other.
        /// </summary>
        public static bool Evaluate(Session session)
        {
            if (!PropertyManager.GetBool("ip_account_limit_enabled").Item)
                return false;

            if (session.AccessLevel > AccessLevel.Player) // never restrict staff
                return false;

            var ip = session.EndPointC2S.Address;

            var whitelist = PropertyManager.GetString("ip_account_limit_whitelist").Item
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (whitelist.Contains(ip.ToString()))
                return false;

            var limit = PropertyManager.GetLong("ip_account_limit").Item;

            // unrestricted, in-world accounts on this IP (excluding ourselves)
            var free = NetworkManager.GetSessionsByAddress(ip)
                .Count(s => s != session && s.Player != null && !s.IpRestricted && s.AccountId != session.AccountId);

            return free >= limit;
        }
    }
}
