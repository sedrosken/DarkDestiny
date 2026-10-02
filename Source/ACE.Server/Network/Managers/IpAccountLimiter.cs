using System;
using System.Linq;
using System.Net;

using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

using Position = ACE.Entity.Position;

namespace ACE.Server.Network.Managers
{
    /// <summary>
    /// Limits how many accounts from one IP may be OUTSIDE the Abandoned Mine landblock at the same time.
    /// Accounts inside the mine never count against the limit and are never booted.
    /// </summary>
    public static class IpAccountLimiter
    {
        // Abandoned Mine ("sub") landblock. portalabandonedmine lands at 0x01C9022D, /sub lands at 0x01C90234.
        public const uint PenLandblock = 0x01C9;

        // same spot the /sub command uses
        public static readonly Position MinePen = new Position(0x01C90234, 75.240158f, -30.138903f, 0.005f, 0f, 0f, -0.998830f, -0.048363f);

        /// <summary>
        /// Serializes "decide whether a player is over the limit, then mark them" so two players can't both claim the last slot.
        /// </summary>
        public static readonly object Gate = new object();

        public static bool IsPenLandblock(uint cell) => (cell >> 16) == PenLandblock;

        public static bool IsExempt(Session session)
        {
            if (!PropertyManager.GetBool("ip_account_limit_enabled").Item)
                return true;

            if (session.AccessLevel > AccessLevel.Player) // never restrict staff
                return true;

            var whitelist = PropertyManager.GetString("ip_account_limit_whitelist").Item
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return whitelist.Contains(session.EndPointC2S.Address.ToString());
        }

        /// <summary>
        /// True if letting this session stay outside the mine would put its IP over ip_account_limit.
        /// Counts OTHER in-world, non-staff accounts on the same IP that are currently outside the mine landblock
        /// and are not themselves in an over-limit grace period. Players inside the mine are never counted.
        /// </summary>
        public static bool IsOverLimit(Session session)
        {
            if (IsExempt(session))
                return false;

            var limit = PropertyManager.GetLong("ip_account_limit").Item;

            var outside = NetworkManager.GetSessionsByAddress(session.EndPointC2S.Address)
                .Count(s => s != session
                         && s.AccountId != session.AccountId
                         && s.AccessLevel == AccessLevel.Player
                         && s.Player != null
                         && s.Player.IpOverLimitSince == null
                         && !IsPenLandblock(s.Player.Location.Cell));

            return outside >= limit;
        }

        /// <summary>
        /// A slot may have opened on this IP (an account returned to the mine or logged out).
        /// Ask every player on the IP that is in an over-limit grace period to re-check, oldest first.
        /// Each re-check runs on that player's own thread and takes the Gate, so only as many are cleared as there are free slots.
        /// </summary>
        public static void RecheckPending(IPAddress address)
        {
            var pending = NetworkManager.GetSessionsByAddress(address)
                .Where(s => s.Player != null && s.Player.IpOverLimitSince != null)
                .OrderBy(s => s.Player.IpOverLimitSince)
                .Select(s => s.Player)
                .ToList();

            foreach (var player in pending)
            {
                var p = player;
                var chain = new ActionChain();
                chain.AddAction(p, () => p.IpLimitReevaluate());
                chain.EnqueueChain();
            }
        }
    }
}
