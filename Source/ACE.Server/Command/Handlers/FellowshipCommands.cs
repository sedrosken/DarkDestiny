using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /fcmd - chat-driven fellowship management.
    /// Works around the retail client's 9-member recruit button lockout and adds join requests.
    /// </summary>
    public static class FellowshipCommands
    {
        private const double JoinRequestTimeoutSeconds = 15.0;

        // matches the 600 second rejoin window used by Fellowship.AddFellowshipMember for locked fellowships
        private const double LockedRejoinWindowSeconds = 600.0;

        private enum JoinMode
        {
            Ask,    // default: leader must /fcmd accept or /fcmd decline
            Allow,  // requests are accepted automatically if the player is otherwise eligible
            Deny    // requests are refused outright
        }

        private class JoinSettings
        {
            public JoinMode Mode = JoinMode.Ask;
        }

        private class PendingJoin
        {
            public uint RequestId;
            public uint RequesterGuid;
            public string RequesterName;
            public Fellowship Fellowship;
            public DateTime ExpiresUtc;
        }

        // join mode lives alongside the Fellowship object and is collected with it when the fellowship disbands,
        // so no changes to Fellowship.cs are needed
        private static readonly ConditionalWeakTable<Fellowship, JoinSettings> joinSettings = new ConditionalWeakTable<Fellowship, JoinSettings>();

        // one outstanding request per requester, keyed by requester guid
        private static readonly Dictionary<uint, PendingJoin> pendingJoins = new Dictionary<uint, PendingJoin>();
        private static readonly object pendingLock = new object();
        private static uint nextRequestId;

        private const string Usage =
            "Usage: /fcmd <command> [character name]\n" +
            "  add [name]      - recruit a player (or your appraised target)\n" +
            "  remove [name]   - dismiss a player (or your appraised target)\n" +
            "  leave           - leave your fellowship\n" +
            "  leader [name]   - pass leadership to a fellow (or your appraised target)\n" +
            "  list            - list fellowship members\n" +
            "  join [name]     - ask to join the fellowship that player is in (or your appraised target's)\n" +
            "  accept [name]   - leader: accept a pending join request\n" +
            "  decline [name]  - leader: decline a pending join request\n" +
            "  allowjoin       - leader: automatically accept join requests\n" +
            "  askjoin         - leader: ask before accepting join requests (default)\n" +
            "  denyjoin        - leader: refuse all join requests";

        [CommandHandler("fcmd", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Manage your fellowship from chat (works around the client's 9-member recruit button lockout).",
            "<add|remove|leave|leader|list|join|accept|decline|allowjoin|askjoin|denyjoin> [character name]")]
        public static void HandleFcmd(Session session, params string[] parameters)
        {
            var player = session.Player;
            var name = parameters.Length > 1 ? string.Join(" ", parameters.Skip(1)) : null;

            switch (parameters[0].ToLower())
            {
                case "add":
                    HandleAdd(player, name);
                    break;
                case "remove":
                    HandleRemove(player, name);
                    break;
                case "leave":
                    HandleLeave(player);
                    break;
                case "leader":
                    HandleLeader(player, name);
                    break;
                case "list":
                    HandleList(player);
                    break;
                case "join":
                    HandleJoin(player, name);
                    break;
                case "accept":
                    HandleAcceptOrDecline(player, name, true);
                    break;
                case "decline":
                    HandleAcceptOrDecline(player, name, false);
                    break;
                case "allowjoin":
                    HandleSetJoinMode(player, JoinMode.Allow);
                    break;
                case "askjoin":
                    HandleSetJoinMode(player, JoinMode.Ask);
                    break;
                case "denyjoin":
                    HandleSetJoinMode(player, JoinMode.Deny);
                    break;
                default:
                    player.SendMessage(Usage);
                    break;
            }
        }

        // ---------------------------------------------------------------------
        // add / remove / leave / leader / list
        // ---------------------------------------------------------------------

        private static void HandleAdd(Player player, string name)
        {
            var target = ResolveTarget(player, name, "/fcmd add");
            if (target == null)
                return;

            if (target == player)
            {
                player.SendMessage("You can't add yourself to your own fellowship.");
                return;
            }

            // FellowshipRecruit() silently no-ops if the player has no fellowship yet,
            // so create one first (mirrors clicking 'Create Fellowship' in the client).
            if (player.Fellowship == null)
            {
                var shareXP = player.GetCharacterOption(CharacterOption.ShareFellowshipExpAndLuminance);
                player.FellowshipCreate($"{player.Name}'s Fellowship", shareXP);
            }

            player.FellowshipRecruit(target);
        }

        private static void HandleRemove(Player player, string name)
        {
            if (player.Fellowship == null)
            {
                player.SendMessage("You are not in a fellowship.");
                return;
            }

            var target = ResolveTarget(player, name, "/fcmd remove");
            if (target == null)
                return;

            if (target == player)
            {
                player.FellowshipQuit(false);
                return;
            }

            if (target.Fellowship != player.Fellowship)
            {
                player.SendMessage($"{target.Name} is not a member of your fellowship.");
                return;
            }

            // FellowshipDismissPlayer() enforces the leader-only rule and sends the standard client error
            player.FellowshipDismissPlayer(target.Guid.Full);
        }

        private static void HandleLeave(Player player)
        {
            if (player.Fellowship == null)
            {
                player.SendMessage("You are not in a fellowship.");
                return;
            }

            player.FellowshipQuit(false);
        }

        private static void HandleLeader(Player player, string name)
        {
            if (player.Fellowship == null)
            {
                player.SendMessage("You are not in a fellowship.");
                return;
            }

            if (!RequireLeader(player))
                return;

            var target = ResolveTarget(player, name, "/fcmd leader");
            if (target == null)
                return;

            if (target == player)
            {
                player.SendMessage("You are already the leader of your fellowship.");
                return;
            }

            if (target.Fellowship != player.Fellowship)
            {
                player.SendMessage($"{target.Name} is not a member of your fellowship.");
                return;
            }

            player.FellowshipNewLeader(target.Guid.Full);
        }

        private static void HandleList(Player player)
        {
            var fellowship = player.Fellowship;

            if (fellowship == null)
            {
                player.SendMessage("You are not in a fellowship.");
                return;
            }

            var members = fellowship.GetFellowshipMembers();

            player.SendMessage($"{fellowship.FellowshipName} ({members.Count}/{Fellowship.MaxFellows}) - join requests: {DescribeMode(GetJoinMode(fellowship))}");

            foreach (var member in members.Values)
            {
                var tag = member.Guid.Full == fellowship.FellowshipLeaderGuid ? " (Leader)" : "";
                player.SendMessage($"  {member.Name}{tag}");
            }
        }

        // ---------------------------------------------------------------------
        // join mode toggles (leader only)
        // ---------------------------------------------------------------------

        private static void HandleSetJoinMode(Player player, JoinMode mode)
        {
            if (player.Fellowship == null)
            {
                player.SendMessage("You are not in a fellowship.");
                return;
            }

            if (!RequireLeader(player))
                return;

            joinSettings.GetValue(player.Fellowship, _ => new JoinSettings()).Mode = mode;

            player.SendMessage($"Join requests for {player.Fellowship.FellowshipName} are now: {DescribeMode(mode)}.");
        }

        // ---------------------------------------------------------------------
        // join / accept / decline
        // ---------------------------------------------------------------------

        private static void HandleJoin(Player player, string name)
        {
            if (player.Fellowship != null)
            {
                player.SendMessage("You are already in a fellowship.");
                return;
            }

            var target = ResolveTarget(player, name, "/fcmd join");
            if (target == null)
                return;

            if (target == player)
            {
                player.SendMessage("You can't join your own fellowship.");
                return;
            }

            var fellowship = target.Fellowship;

            if (fellowship == null)
            {
                player.SendMessage($"{target.Name} is not in a fellowship.");
                return;
            }

            var leader = PlayerManager.GetOnlinePlayer(fellowship.FellowshipLeaderGuid);

            if (leader == null || leader.Fellowship != fellowship)
            {
                player.SendMessage($"The leader of {fellowship.FellowshipName} is not available right now.");
                return;
            }

            var mode = GetJoinMode(fellowship);

            if (mode == JoinMode.Deny)
            {
                player.SendMessage($"The leader of {fellowship.FellowshipName} has disabled join requests.");
                return;
            }

            if (!CanJoin(player, fellowship, out var reason))
            {
                player.SendMessage(reason);
                return;
            }

            if (mode == JoinMode.Allow)
            {
                CompleteJoin(leader, player, fellowship);
                return;
            }

            // JoinMode.Ask - queue the request and ask the leader
            PendingJoin request;

            lock (pendingLock)
            {
                if (pendingJoins.TryGetValue(player.Guid.Full, out var existing) && existing.ExpiresUtc > DateTime.UtcNow)
                {
                    player.SendMessage($"You already have a pending request to join {existing.Fellowship.FellowshipName}. Please wait for it to be answered or time out.");
                    return;
                }

                request = new PendingJoin
                {
                    RequestId = ++nextRequestId,
                    RequesterGuid = player.Guid.Full,
                    RequesterName = player.Name,
                    Fellowship = fellowship,
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(JoinRequestTimeoutSeconds)
                };

                pendingJoins[player.Guid.Full] = request;
            }

            leader.SendMessage($"{player.Name} (level {player.Level ?? 1}) is asking to join your fellowship. Type /fcmd accept {player.Name} or /fcmd decline {player.Name} within {JoinRequestTimeoutSeconds:0} seconds.");
            player.SendMessage($"Your request to join {fellowship.FellowshipName} has been sent to {leader.Name}.");

            var requestId = request.RequestId;
            var requesterGuid = request.RequesterGuid;

            var timeoutChain = new ActionChain();
            timeoutChain.AddDelaySeconds(JoinRequestTimeoutSeconds);
            timeoutChain.AddAction(player, () => ExpireRequest(requesterGuid, requestId));
            timeoutChain.EnqueueChain();
        }

        private static void HandleAcceptOrDecline(Player player, string name, bool accept)
        {
            var fellowship = player.Fellowship;

            if (fellowship == null)
            {
                player.SendMessage("You are not in a fellowship.");
                return;
            }

            if (!RequireLeader(player))
                return;

            var request = ClaimPendingRequest(player, fellowship, name);
            if (request == null)
                return;

            var requester = PlayerManager.GetOnlinePlayer(request.RequesterGuid);

            if (requester == null)
            {
                player.SendMessage($"{request.RequesterName} is no longer online.");
                return;
            }

            if (!accept)
            {
                player.SendMessage($"You declined {requester.Name}'s request to join the fellowship.");
                requester.SendMessage($"{player.Name} has declined your request to join {fellowship.FellowshipName}.");
                return;
            }

            // re-validate: things may have changed since the request was made
            if (!CanJoin(requester, fellowship, out var reason))
            {
                player.SendMessage($"{requester.Name} could not be added: {reason}");
                requester.SendMessage($"Your request to join {fellowship.FellowshipName} could not be completed: {reason}");
                return;
            }

            CompleteJoin(player, requester, fellowship);
        }

        /// <summary>
        /// Finds and removes the pending request the leader is answering.
        /// Removing it here means a request can't be both answered and timed out.
        /// </summary>
        private static PendingJoin ClaimPendingRequest(Player leader, Fellowship fellowship, string name)
        {
            lock (pendingLock)
            {
                var now = DateTime.UtcNow;

                var candidates = pendingJoins.Values
                    .Where(p => p.Fellowship == fellowship && p.ExpiresUtc > now)
                    .ToList();

                if (candidates.Count == 0)
                {
                    leader.SendMessage("There are no pending join requests for your fellowship.");
                    return null;
                }

                PendingJoin match;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    match = candidates.FirstOrDefault(p => p.RequesterName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                                        || p.RequesterName.Equals("+" + name, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        leader.SendMessage($"There is no pending join request from {name}.");
                        return null;
                    }
                }
                else if (candidates.Count == 1)
                {
                    match = candidates[0];
                }
                else
                {
                    leader.SendMessage($"There are multiple pending join requests ({string.Join(", ", candidates.Select(p => p.RequesterName))}). Please specify a name.");
                    return null;
                }

                pendingJoins.Remove(match.RequesterGuid);
                return match;
            }
        }

        private static void ExpireRequest(uint requesterGuid, uint requestId)
        {
            PendingJoin request;

            lock (pendingLock)
            {
                // already answered, or replaced by a newer request
                if (!pendingJoins.TryGetValue(requesterGuid, out request) || request.RequestId != requestId)
                    return;

                pendingJoins.Remove(requesterGuid);
            }

            PlayerManager.GetOnlinePlayer(requesterGuid)?.SendMessage($"Your request to join {request.Fellowship.FellowshipName} timed out.");

            var leader = PlayerManager.GetOnlinePlayer(request.Fellowship.FellowshipLeaderGuid);
            if (leader != null && leader.Fellowship == request.Fellowship)
                leader.SendMessage($"{request.RequesterName}'s request to join the fellowship has expired.");
        }

        private static void CompleteJoin(Player leader, Player requester, Fellowship fellowship)
        {
            // the requester asked to join, so skip their confirmation popup and go straight to the confirmed add;
            // AddConfirmedMember still does its own full-fellowship check
            fellowship.AddConfirmedMember(leader, requester, true);

            if (requester.Fellowship == fellowship)
                leader.SendMessage($"{requester.Name} has joined the fellowship.");
        }

        /// <summary>
        /// Mirrors the eligibility checks in Fellowship.AddFellowshipMember / Player.FellowshipRecruit
        /// that apply when the new member is the one asking.
        /// </summary>
        private static bool CanJoin(Player requester, Fellowship fellowship, out string reason)
        {
            reason = null;

            if (requester.Fellowship != null)
            {
                reason = "You are already in a fellowship.";
                return false;
            }

            if (requester.IsOlthoiPlayer)
            {
                reason = "The Olthoi's hunger for destruction is too great to understand a request for fellowship.";
                return false;
            }

            if (fellowship.IsLocked)
            {
                var allowedBack = fellowship.DepartedMembers.TryGetValue(requester.Guid.Full, out var timeDeparted)
                    && DateTime.UtcNow <= Time.GetDateTimeFromTimestamp(timeDeparted).AddSeconds(LockedRejoinWindowSeconds);

                if (!allowedBack)
                {
                    reason = $"{fellowship.FellowshipName} is locked and cannot accept new members.";
                    return false;
                }
            }

            if (fellowship.FellowshipMembers.Count >= Fellowship.MaxFellows)
            {
                reason = $"{fellowship.FellowshipName} is full.";
                return false;
            }

            return true;
        }

        // ---------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Resolves a target by typed name, or falls back to the player's last appraised target.
        /// Reads RequestedAppraisalTarget directly rather than CommandHandlerHelper.GetLastAppraisedObject(),
        /// which prints developer debug text to the player when nothing is appraised.
        /// </summary>
        private static Player ResolveTarget(Player player, string name, string command)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var named = PlayerManager.GetOnlinePlayer(name);

                if (named == null)
                    player.SendMessage($"{name} is not online.");

                return named;
            }

            var appraisedGuid = player.RequestedAppraisalTarget;
            var appraised = appraisedGuid.HasValue ? PlayerManager.GetOnlinePlayer(appraisedGuid.Value) : null;

            if (appraised == null)
                player.SendMessage($"Usage: {command} <character name>, or appraise a player first and run {command} with no name.");

            return appraised;
        }

        private static bool RequireLeader(Player player)
        {
            if (player.Guid.Full == player.Fellowship.FellowshipLeaderGuid)
                return true;

            player.SendWeenieError(WeenieError.YouMustBeLeaderOfFellowship);
            return false;
        }

        private static JoinMode GetJoinMode(Fellowship fellowship)
        {
            return joinSettings.TryGetValue(fellowship, out var settings) ? settings.Mode : JoinMode.Ask;
        }

        private static string DescribeMode(JoinMode mode)
        {
            switch (mode)
            {
                case JoinMode.Allow: return "allowed automatically";
                case JoinMode.Deny: return "disabled";
                default: return "leader approval required";
            }
        }
    }
}
