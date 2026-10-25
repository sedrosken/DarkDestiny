using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using log4net;

using ACE.Common;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Joke "companions" (Bender: "...with blackjack and hookers").
    /// Bought from an NPC for 10 notes (wcid 20630). Each one follows its buyer for 24 REAL hours
    /// (survives logout + server restarts), does nothing useful, then vanishes and silently
    /// steals 1-2 random trade notes. Up to 20 per player.
    ///
    /// Hooked from Player.TryUseItem (NPC) and Player.PlayerEnterWorld (login respawn).
    /// State is saved to companions.json next to ace.server.exe (no DB changes).
    /// </summary>
    public static class Companions
    {
        // ---- WCIDs ----
        public const uint NpcWcid = 9871114;                                 // the NPC that sells them
        public static readonly uint[] CompanionWcids = { 9871111, 9871112, 9871113 };   // random pick each purchase
        public const uint PriceWcid = 20630;                                 // Trade Note (250,000)
        public const int Price = 10;
        public const uint TokenWcid = 9871115;                               // Token of Companionship -- hand to the NPC for a free one
        public const int BlackjackWinsPerToken = 20;                         // dealer hands out a token every N winning hands

        // all trade note denominations they can steal
        private static readonly uint[] TradeNoteWcids =
        {
            2621, 2622, 2623, 2624, 2625, 2626, 2627,
            7374, 7375, 7376, 7377,
            20628, 20629, 20630,
        };

        // ---- Tuning ----
        public const int MaxPerPlayer = 20;
        public const double LifetimeSeconds = 24 * 60 * 60;   // real time
        private const double TickSeconds = 2.0;
        private const float RewarpDistance = 40f;            // further than this from owner -> respawn at owner
        private const float SpeechRange = 20f;
        public const double GiftCooldownSeconds = 60 * 60;   // must own a companion this long before gifting it

        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private class Entry
        {
            public uint Wcid { get; set; }
            public double ExpiresAt { get; set; }                 // unix seconds
            public double AcquiredAt { get; set; }                // unix seconds -- when the CURRENT owner got it (0 = old save)
            [System.Text.Json.Serialization.JsonIgnore]
            public Companion Live;
            [System.Text.Json.Serialization.JsonIgnore]
            public bool GiftPending;
        }

        private class State
        {
            public List<Entry> Entries = new List<Entry>();
            public int LoopGen;
        }

        private static readonly ConcurrentDictionary<uint, State> states = new ConcurrentDictionary<uint, State>();

        private static readonly object fileLock = new object();
        private static bool loaded;
        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "companions.json");

        // =====================================================================
        // NPC
        // =====================================================================

        /// <summary>Returns true if the item was the companion NPC (use fully handled here).</summary>
        public static bool TryHandleUse(Player player, WorldObject item)
        {
            if (item.WeenieClassId != NpcWcid)
                return false;

            EnsureLoaded();
            GiveOwedTokens(player);

            var state = states.GetOrAdd(player.Guid.Full, _ => new State());

            int count;
            lock (state) count = state.Entries.Count;

            if (count >= MaxPerPlayer)
            {
                Say(item, $"Easy there, {player.Name}. {MaxPerPlayer} is all the company one person can handle.");
                return true;
            }

            var confirm = new Confirmation_Custom(player.Guid,
                () => new ActionChain(player, () => TryBuy(player, item)).EnqueueChain(),
                () => { });

            if (!player.ConfirmationManager.EnqueueSend(confirm, $"Spend {Price} MMD notes for some company?"))
                player.SendMessage("Answer your other open question first, then try again.");

            return true;
        }

        private static void TryBuy(Player player, WorldObject npc)
        {
            var state = states.GetOrAdd(player.Guid.Full, _ => new State());

            lock (state)
            {
                if (state.Entries.Count >= MaxPerPlayer)
                    return;
            }

            if (player.GetNumInventoryItemsOfWCID(PriceWcid, true) < Price
                || !player.TryConsumeFromInventoryWithNetworking(PriceWcid, Price))
            {
                Say(npc, $"Come back when you've got {Price} MMD notes, sweetie.");
                return;
            }

            GrantCompanion(player);
        }

        /// <summary>
        /// Called from Player.GiveObjectToNPC. Returns true if this was a Token of Companionship
        /// handed to the companion NPC (give fully handled here).
        /// </summary>
        public static bool TryHandleGive(Player player, WorldObject npc, WorldObject item)
        {
            if (npc.WeenieClassId != NpcWcid || item.WeenieClassId != TokenWcid)
                return false;

            EnsureLoaded();
            var state = states.GetOrAdd(player.Guid.Full, _ => new State());

            int count;
            lock (state) count = state.Entries.Count;

            if (count >= MaxPerPlayer)
            {
                player.Session.Network.EnqueueSend(new ACE.Server.Network.GameEvent.Events.GameEventInventoryServerSaveFailed(player.Session, item.Guid.Full));
                Say(npc, $"Easy there, {player.Name}. {MaxPerPlayer} is all the company one person can handle.");
                return true;
            }

            var restOfStack = (item.StackSize ?? 1) > 1;

            if (!player.TryConsumeFromInventoryWithNetworking(TokenWcid, 1))
            {
                player.Session.Network.EnqueueSend(new ACE.Server.Network.GameEvent.Events.GameEventInventoryServerSaveFailed(player.Session, item.Guid.Full));
                return true;
            }

            if (restOfStack)   // release the remaining stack the client was holding for the give
                player.Session.Network.EnqueueSend(new ACE.Server.Network.GameEvent.Events.GameEventInventoryServerSaveFailed(player.Session, item.Guid.Full));

            player.SendMessage($"You give {npc.Name} a Token of Companionship.");
            Say(npc, "A token! This one's on the house, sugar.");

            GrantCompanion(player);
            return true;
        }

        private static void GrantCompanion(Player player)
        {
            var state = states.GetOrAdd(player.Guid.Full, _ => new State());

            var entry = new Entry
            {
                Wcid = CompanionWcids[Random.Shared.Next(CompanionWcids.Length)],
                ExpiresAt = Time.GetUnixTime() + LifetimeSeconds,
                AcquiredAt = Time.GetUnixTime(),
            };

            lock (state) state.Entries.Add(entry);
            Save();

            Spawn(player, entry);
            StartLoop(player);
        }

        // =====================================================================
        // Login / loop
        // =====================================================================

        /// <summary>Called from Player.PlayerEnterWorld.</summary>
        public static void OnLogin(Player player)
        {
            EnsureLoaded();

            if (!states.TryGetValue(player.Guid.Full, out var state))
                return;

            lock (state)
            {
                if (state.Entries.Count == 0)
                    return;

                // anything spawned before the relog is gone/stale
                foreach (var e in state.Entries)
                    e.Live = null;
            }

            StartLoop(player, 5.0);   // let the player finish entering the world first
        }

        private static void StartLoop(Player player, double firstDelay = TickSeconds)
        {
            var state = states.GetOrAdd(player.Guid.Full, _ => new State());

            int gen;
            lock (state) gen = ++state.LoopGen;   // any older loop for this player stops itself

            var chain = new ActionChain();
            chain.AddDelaySeconds(firstDelay);
            chain.AddAction(player, () => Tick(player, gen));
            chain.EnqueueChain();
        }

        private static void Tick(Player player, int gen)
        {
            if (!states.TryGetValue(player.Guid.Full, out var state))
                return;

            lock (state)
            {
                if (state.LoopGen != gen)
                    return;   // superseded
            }

            // owner logged off -> stop. (Pet.SlowTick removes the live companions on its own.)
            if (player.PhysicsObj == null || player.Session == null)
                return;

            if (!player.Teleporting && player.CurrentLandblock != null)
            {
                var now = Time.GetUnixTime();
                var expired = new List<Entry>();
                var respawn = new List<Entry>();
                bool empty;

                lock (state)
                {
                    foreach (var e in state.Entries)
                    {
                        if (now >= e.ExpiresAt)
                        {
                            expired.Add(e);
                            continue;
                        }

                        var live = e.Live;
                        if (live == null || live.IsDestroyed || live.PhysicsObj == null || live.CurrentLandblock == null)
                            respawn.Add(e);
                        else if (live.Location.DistanceTo(player.Location) > RewarpDistance)
                        {
                            live.Destroy();
                            respawn.Add(e);
                        }
                    }

                    foreach (var e in expired)
                        state.Entries.Remove(e);

                    empty = state.Entries.Count == 0;
                }

                if (expired.Count > 0)
                {
                    foreach (var e in expired)
                    {
                        if (e.Live != null && !e.Live.IsDestroyed)
                            e.Live.Destroy();
                        e.Live = null;
                        StealNotes(player);
                    }
                    Save();
                }

                foreach (var e in respawn)
                    Spawn(player, e);

                if (empty)
                    return;   // nothing left -> loop ends; a new purchase restarts it
            }

            var chain = new ActionChain();
            chain.AddDelaySeconds(TickSeconds);
            chain.AddAction(player, () => Tick(player, gen));
            chain.EnqueueChain();
        }

        // =====================================================================
        // Gifting: /giftcompanion <player>  (hands a LIVE companion + its remaining time to someone else)
        // =====================================================================

        /// <summary>
        /// Gifts the companion the giver has selected (falls back to the one with the most time left).
        /// Recipient must be online; no yes/no -- they can't refuse. Remaining time carries over; the recipient
        /// is the one robbed when it expires.
        /// </summary>
        public static void TryGift(Player giver, string targetName)
        {
            EnsureLoaded();

            if (!states.TryGetValue(giver.Guid.Full, out var gState))
            {
                giver.SendMessage("You don't have any companions to give away.");
                return;
            }

            var recipient = PlayerManager.GetOnlinePlayer(targetName);
            if (recipient == null)
            {
                giver.SendMessage($"{targetName} is not online.");
                return;
            }
            if (recipient == giver)
            {
                giver.SendMessage("You can't gift a companion to yourself.");
                return;
            }

            Entry entry;
            lock (gState)
            {
                var available = gState.Entries.Where(e => !e.GiftPending).ToList();
                if (available.Count == 0)
                {
                    giver.SendMessage(gState.Entries.Count == 0
                        ? "You don't have any companions to give away."
                        : "That companion is already on its way to someone.");
                    return;
                }

                var now = Time.GetUnixTime();
                var selected = giver.CurrentAppraisalTarget;
                var picked = available.FirstOrDefault(e => e.Live != null && selected.HasValue && e.Live.Guid.Full == selected.Value);

                if (picked != null)
                {
                    var wait = GiftWait(picked, now);
                    if (wait > 0)
                    {
                        giver.SendMessage($"You have to keep {picked.Live.Name} for at least an hour before gifting her. ({FormatTime(wait)} to go)");
                        return;
                    }
                    entry = picked;
                }
                else
                {
                    var ready = available.Where(e => GiftWait(e, now) <= 0).ToList();
                    if (ready.Count == 0)
                    {
                        var soonest = available.Min(e => GiftWait(e, now));
                        giver.SendMessage($"You have to keep a companion for at least an hour before gifting it. ({FormatTime(soonest)} to go)");
                        return;
                    }
                    entry = ready.OrderByDescending(e => e.ExpiresAt).First();
                }

                entry.GiftPending = true;
            }

            // no asking -- it just shows up (runs on the recipient's landblock)
            new ActionChain(recipient, () => AcceptGift(giver, recipient, entry)).EnqueueChain();
        }

        private static void AcceptGift(Player giver, Player recipient, Entry entry)
        {
            var gState = states.GetOrAdd(giver.Guid.Full, _ => new State());
            var rState = states.GetOrAdd(recipient.Guid.Full, _ => new State());

            lock (rState)
            {
                if (rState.Entries.Count >= MaxPerPlayer)
                {
                    entry.GiftPending = false;
                    giver.SendMessage($"{recipient.Name} already has {MaxPerPlayer} companions and can't take any more.");
                    return;
                }
            }

            // still the giver's? (could have expired while the question was open)
            lock (gState)
            {
                if (!gState.Entries.Remove(entry))
                {
                    giver.SendMessage("That companion isn't available anymore.");
                    return;
                }
            }

            entry.GiftPending = false;
            entry.AcquiredAt = Time.GetUnixTime();   // new owner's 1-hour hold starts now
            var name = entry.Live?.Name ?? "companion";

            var old = entry.Live;
            entry.Live = null;
            if (old != null && !old.IsDestroyed)
                new ActionChain(old, () => { if (!old.IsDestroyed) old.Destroy(); }).EnqueueChain();   // on its own landblock

            lock (rState) rState.Entries.Add(entry);
            Save();

            Spawn(recipient, entry);
            StartLoop(recipient);

            giver.SendMessage($"Your {name} has gone off with {recipient.Name}.");
            recipient.SendMessage($"{giver.Name} gave you a {name}. ({TimeLeft(entry)} left)");
        }

        private static string TimeLeft(Entry e) => FormatTime(e.ExpiresAt - Time.GetUnixTime());

        /// <summary>Seconds until this companion can be gifted (0 or less = ready).</summary>
        private static double GiftWait(Entry e, double now)
        {
            var acquired = e.AcquiredAt > 0 ? e.AcquiredAt : e.ExpiresAt - LifetimeSeconds;   // old saves: use purchase time
            return acquired + GiftCooldownSeconds - now;
        }

        private static string FormatTime(double secs)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling(secs)));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";
        }

        // =====================================================================
        // Blackjack reward: a token every BlackjackWinsPerToken winning hands
        // =====================================================================

        private class WinData
        {
            public int Wins { get; set; }
            public int OwedTokens { get; set; }   // earned while the pack was full
        }

        private static readonly ConcurrentDictionary<uint, WinData> wins = new ConcurrentDictionary<uint, WinData>();
        private static string WinsPath => Path.Combine(AppContext.BaseDirectory, "companiontokens.json");

        /// <summary>Called from Blackjack after a hand resolves. handsWon = winning hands this round.</summary>
        public static void OnBlackjackWins(Player player, WorldObject dealer, int handsWon)
        {
            if (player == null || handsWon <= 0)
                return;

            EnsureLoaded();
            var w = wins.GetOrAdd(player.Guid.Full, _ => new WinData());

            int earned;
            lock (w)
            {
                var before = w.Wins / BlackjackWinsPerToken;
                w.Wins += handsWon;
                earned = w.Wins / BlackjackWinsPerToken - before;
                w.OwedTokens += earned;
            }

            if (earned > 0)
                Say(dealer, $"That's {BlackjackWinsPerToken} more wins, {player.Name}. Here's a little something for some company tonight.");

            GiveOwedTokens(player);
            SaveWins();
        }

        /// <summary>Hands over any tokens the player earned but couldn't carry. Called on dealer win + NPC use.</summary>
        private static void GiveOwedTokens(Player player)
        {
            if (!wins.TryGetValue(player.Guid.Full, out var w))
                return;

            var changed = false;
            while (true)
            {
                lock (w)
                {
                    if (w.OwedTokens <= 0)
                        break;
                }

                var token = ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(TokenWcid);
                if (token == null)
                {
                    log.Error($"Companions - token weenie {TokenWcid} not found");
                    break;
                }

                if (!player.TryCreateInInventoryWithNetworking(token, out _, true))
                {
                    token.Destroy();
                    player.SendMessage("Your pack is full. Visit the companion NPC with free space to collect your Token of Companionship.");
                    break;
                }

                lock (w) w.OwedTokens--;
                changed = true;
            }

            if (changed)
                SaveWins();
        }

        // =====================================================================
        // Spawn / steal
        // =====================================================================

        private static void Spawn(Player player, Entry entry)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(entry.Wcid);
            if (weenie == null)
            {
                log.Error($"Companions.Spawn - weenie {entry.Wcid} not found");
                return;
            }

            var c = new Companion(weenie, GuidManager.NewDynamicGuid());
            if (c.Init(player))
                entry.Live = c;
            else
                c.Destroy();
        }

        /// <summary>Silently takes 1 or 2 random trade notes (weighted by how many of each the player holds).</summary>
        private static void StealNotes(Player player)
        {
            var take = Random.Shared.Next(1, 3);   // 1 or 2

            for (var i = 0; i < take; i++)
            {
                var pool = new List<(uint wcid, int count)>();
                var total = 0;

                foreach (var wcid in TradeNoteWcids)
                {
                    var n = player.GetNumInventoryItemsOfWCID(wcid, true);
                    if (n > 0)
                    {
                        pool.Add((wcid, n));
                        total += n;
                    }
                }

                if (total == 0)
                    return;   // broke -- nothing to steal

                var roll = Random.Shared.Next(total);
                foreach (var (wcid, n) in pool)
                {
                    if (roll < n)
                    {
                        player.TryConsumeFromInventoryWithNetworking(wcid, 1);
                        break;
                    }
                    roll -= n;
                }
            }
        }

        private static void Say(WorldObject npc, string text)
        {
            npc.EnqueueBroadcast(new GameMessageHearSpeech(text, npc.Name, npc.Guid.Full, ChatMessageType.Speech), SpeechRange);
        }

        // =====================================================================
        // Persistence (companions.json)
        // =====================================================================

        private static void EnsureLoaded()
        {
            if (loaded) return;

            lock (fileLock)
            {
                if (loaded) return;

                try
                {
                    if (File.Exists(FilePath))
                    {
                        var data = JsonSerializer.Deserialize<Dictionary<uint, List<Entry>>>(File.ReadAllText(FilePath));
                        if (data != null)
                        {
                            foreach (var kvp in data)
                                states[kvp.Key] = new State { Entries = kvp.Value ?? new List<Entry>() };
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Companions - failed to load {FilePath}: {ex.Message}");
                }

                try
                {
                    if (File.Exists(WinsPath))
                    {
                        var data = JsonSerializer.Deserialize<Dictionary<uint, WinData>>(File.ReadAllText(WinsPath));
                        if (data != null)
                        {
                            foreach (var kvp in data)
                                wins[kvp.Key] = kvp.Value ?? new WinData();
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Companions - failed to load {WinsPath}: {ex.Message}");
                }

                loaded = true;
            }
        }

        private static void Save()
        {
            lock (fileLock)
            {
                try
                {
                    var data = new Dictionary<uint, List<Entry>>();
                    foreach (var kvp in states)
                    {
                        lock (kvp.Value)
                        {
                            if (kvp.Value.Entries.Count > 0)
                                data[kvp.Key] = kvp.Value.Entries.ToList();
                        }
                    }

                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(data));
                    File.Move(tmp, FilePath, true);
                }
                catch (Exception ex)
                {
                    log.Error($"Companions - failed to save {FilePath}: {ex.Message}");
                }
            }
        }

        private static void SaveWins()
        {
            lock (fileLock)
            {
                try
                {
                    var data = new Dictionary<uint, WinData>();
                    foreach (var kvp in wins)
                        lock (kvp.Value) data[kvp.Key] = new WinData { Wins = kvp.Value.Wins, OwedTokens = kvp.Value.OwedTokens };

                    var tmp = WinsPath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(data));
                    File.Move(tmp, WinsPath, true);
                }
                catch (Exception ex)
                {
                    log.Error($"Companions - failed to save {WinsPath}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// The follower itself: a passive (cosmetic) pet with no device, no level, no benefit.
    /// Rides the stock passive-pet follow logic in Pet.Tick / Pet.SlowTick.
    /// </summary>
    public class Companion : Pet
    {
        public Companion(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            IsPassivePet = true;
        }

        public Companion(Biota biota) : base(biota)
        {
            IsPassivePet = true;
        }

        public bool Init(Player player)
        {
            // scatter a little so a crowd doesn't spawn stacked in one spot
            var dist = 1.5f + (float)Random.Shared.NextDouble() * 3.0f;

            Location = player.Location.InFrontOf(dist, true);
            Location.LandblockId = new LandblockId(Location.GetCell());

            TimeToRot = -1;
            NoCorpse = true;

            PetOwner = player.Guid.Full;
            P_PetOwner = player;

            if (!EnterWorld())
                return false;

            nextSlowTickTime = Time.GetUnixTime();
            return true;
        }
    }
}
