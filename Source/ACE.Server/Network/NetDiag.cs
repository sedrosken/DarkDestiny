using System;
using System.Collections.Concurrent;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Network.GameAction;
using ACE.Server.Network.GameEvent;
using ACE.Server.Network.GameMessages;

namespace ACE.Server.Network
{
    /// <summary>
    /// TEMP diagnostic: per-message-type counts to/from ONE character, logged every 10s.
    /// @netdiag <character name> to start, @netdiag off to stop.
    /// Hooks: NetworkSession.EnqueueSend (both GameMessage overloads) and InboundMessageManager.HandleGameAction.
    /// </summary>
    public static class NetDiag
    {
        private static readonly ILog log = LogManager.GetLogger("NetDiag");

        private static volatile string target; // character name, null = off

        private static readonly ConcurrentDictionary<string, int> outCounts = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, int> inCounts = new ConcurrentDictionary<string, int>();

        private static readonly object windowLock = new object();
        private static DateTime windowStart = DateTime.UtcNow;
        private const double WindowSeconds = 10.0;

        private static bool IsTarget(Session session)
        {
            var t = target;
            if (t == null)
                return false;

            var player = session?.Player;
            return player != null && string.Equals(player.Name, t, StringComparison.OrdinalIgnoreCase);
        }

        public static void Outbound(Session session, GameMessage msg)
        {
            if (msg == null || !IsTarget(session))
                return;

            var key = msg is GameEventMessage ev ? $"Event.{ev.EventType}" : msg.Opcode.ToString();
            outCounts.AddOrUpdate(key, 1, (_, v) => v + 1);
            MaybeFlush(session);
        }

        public static void Inbound(Session session, GameActionType action)
        {
            if (!IsTarget(session))
                return;

            inCounts.AddOrUpdate(action.ToString(), 1, (_, v) => v + 1);
            MaybeFlush(session);
        }

        private static string Drain(ConcurrentDictionary<string, int> counts)
        {
            var snap = counts.ToArray();
            counts.Clear();

            var top = snap.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key}={kv.Value}");
            return $"{string.Join(", ", top)} (total {snap.Sum(kv => kv.Value)})";
        }

        private static void MaybeFlush(Session session)
        {
            var now = DateTime.UtcNow;
            double elapsed;

            lock (windowLock)
            {
                elapsed = (now - windowStart).TotalSeconds;
                if (elapsed < WindowSeconds)
                    return;
                windowStart = now;
            }

            try
            {
                var player = session?.Player;
                var lb = player?.CurrentLandblock;
                var where = lb == null ? "?" : lb.IsDungeon ? "dungeon" : (player.Location?.Indoors ?? false) ? "indoors" : "outdoors";

                log.Warn($"[NetDiag] {player?.Name} {where} {player?.Location?.ToLOCString()} {elapsed:0}s\n" +
                         $"  OUT: {Drain(outCounts)}\n" +
                         $"  IN:  {Drain(inCounts)}");
            }
            catch (Exception ex)
            {
                log.Error($"[NetDiag] flush failed: {ex.Message}");
            }
        }

        [CommandHandler("netdiag", AccessLevel.Admin, CommandHandlerFlag.None, 1,
            "Logs per-type message counts to/from one character every 10s",
            "<character name | off>")]
        public static void HandleNetDiag(Session session, params string[] parameters)
        {
            var arg = string.Join(" ", parameters).Trim();

            target = arg.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : arg;

            outCounts.Clear();
            inCounts.Clear();
            lock (windowLock)
                windowStart = DateTime.UtcNow;

            CommandHandlerHelper.WriteOutputInfo(session, target == null ? "netdiag off." : $"netdiag tracking '{target}'.");
        }
    }
}
