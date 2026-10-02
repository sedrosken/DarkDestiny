using System;

using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Set while this player is outside the Abandoned Mine with their IP over ip_account_limit and the boot timer running.
        /// While set, this player does not count against other accounts' slots.
        /// </summary>
        public DateTime? IpOverLimitSince;

        private int ipLimitToken;

        /// <summary>
        /// Call after the player's final location is known (OnTeleportComplete).
        /// Safe to call repeatedly: it only starts one timer, and cancels it if the player is back in the mine.
        /// Arriving in the mine also frees a slot, so other pending players on the same IP are re-checked.
        /// </summary>
        public void IpLimitCheck()
        {
            if (Session == null)
                return;

            var inPen = IpAccountLimiter.IsPenLandblock(Location.Cell);

            if (IpAccountLimiter.IsExempt(Session) || inPen)
            {
                bool cancelled = false;

                lock (IpAccountLimiter.Gate)
                {
                    if (IpOverLimitSince != null)
                    {
                        IpOverLimitSince = null;
                        ipLimitToken++; // invalidates the pending boot
                        cancelled = true;
                    }
                }

                if (cancelled)
                    Session.Network.EnqueueSend(new GameMessageSystemChat("You are back in the Abandoned Mine. Boot timer cancelled.", ChatMessageType.System));

                if (inPen)
                    IpAccountLimiter.RecheckPending(Session.EndPointC2S.Address); // our slot (if we had one) is free now

                return;
            }

            int token;
            long grace;

            lock (IpAccountLimiter.Gate)
            {
                if (IpOverLimitSince != null)
                    return; // timer already running

                if (!IpAccountLimiter.IsOverLimit(Session))
                    return;

                grace = Math.Max(0, PropertyManager.GetLong("ip_account_limit_grace_seconds").Item);

                IpOverLimitSince = DateTime.UtcNow;
                token = ++ipLimitToken;
            }

            Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"Too many accounts from your IP are outside the Abandoned Mine. Return with /sub within {grace} seconds or you will be disconnected.",
                ChatMessageType.System));

            var chain = new ActionChain();
            chain.AddDelaySeconds(grace);
            chain.AddAction(this, () => IpLimitExpire(token));
            chain.EnqueueChain();
        }

        /// <summary>
        /// Called when another account on this IP freed a slot (returned to the mine or logged out).
        /// Cancels this player's boot timer if they are no longer over the limit.
        /// </summary>
        public void IpLimitReevaluate()
        {
            if (Session == null)
                return;

            lock (IpAccountLimiter.Gate)
            {
                if (IpOverLimitSince == null)
                    return;

                var since = IpOverLimitSince;
                IpOverLimitSince = null; // so we don't affect our own check; restored below if still over

                if (!IpAccountLimiter.IsExempt(Session) && !IpAccountLimiter.IsPenLandblock(Location.Cell) && IpAccountLimiter.IsOverLimit(Session))
                {
                    IpOverLimitSince = since;
                    return;
                }

                ipLimitToken++; // invalidates the pending boot
            }

            Session.Network.EnqueueSend(new GameMessageSystemChat("A slot opened up on your IP; boot timer cancelled. You may stay outside the Abandoned Mine.", ChatMessageType.System));
        }

        private void IpLimitExpire(int token)
        {
            if (Session == null)
                return;

            lock (IpAccountLimiter.Gate)
            {
                if (token != ipLimitToken || IpOverLimitSince == null)
                    return; // cancelled, or already handled

                // Clear our own flag first so that, if we stay, we count as an established outside account for the next player.
                IpOverLimitSince = null;

                if (IpAccountLimiter.IsExempt(Session) || IpAccountLimiter.IsPenLandblock(Location.Cell) || !IpAccountLimiter.IsOverLimit(Session))
                    return;
            }

            Session.Terminate(SessionTerminationReason.AccountBooted,
                new GameMessageBootAccount(" because too many accounts are connected from your IP and you did not return to the Abandoned Mine"),
                null, "IP account limit: outside Abandoned Mine after grace period");
        }
    }
}
