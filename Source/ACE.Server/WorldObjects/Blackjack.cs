using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Blackjack minigame (phase 1).
    /// Hooked from Player.TryUseItem by WCID: dealer NPC, "Hit Me" lever, "Stand" button.
    /// All game state is in memory. A server restart mid-hand loses that hand.
    /// </summary>
    public static class Blackjack
    {
        // ---- WCIDs (must match the weenie SQL) ----
        public const uint DealerWcid      = 9871095;
        public const uint HitLeverWcid    = 9871096;
        public const uint StandButtonWcid = 9871097;
        public const uint ChipWcid        = 9871098;
        public const uint ChipPouchWcid   = 9871101;

        // ---- Number plaques (scoreboard) ----
        public const uint PlayerTensWcid  = 9871103;
        public const uint PlayerOnesWcid  = 9871104;
        public const uint DealerTensWcid  = 9871105;
        public const uint DealerOnesWcid  = 9871106;
        private const byte PlaquePartIndex    = 0;           // part of the signpost Setup (0x02000290) that has the sign face
        private const uint PlaqueFaceTexture  = 0x0500136F;  // signpost face SurfaceTexture being replaced
        private const uint DigitZeroTexture   = 0x05001EC7;  // apartment-number "0"; 1-9 follow sequentially   // bought for 1 prestige medal, opens into PouchChips chips

        // ---- Tuning ----
        public const int Bet = 10;                              // chips per hand (even number so insurance / 3:2 stay whole)
        public const int PouchChips = 200;                      // chips per pouch (1 prestige medal)
        public const string DailyQuest = "BlackjackDailyFreeHand"; // needs a row in the world `quest` table, or the free hand is never offered
        private const float ControlRange = 10f;                 // lever/button must be this close to the dealer
        private const float PlayerRange = 15f;                  // player must be this close to the dealer to act
        private const double IdleTimeout = 90;                  // seconds without input -> hand forfeited
        private const int DeckCount = 6;
        private const float SpeechRange = 20f;
        private const string ControlsHint = "Pull the lever to HIT, press the button to STAND.";

        private static readonly ConcurrentDictionary<uint, Game> gamesByPlayer = new ConcurrentDictionary<uint, Game>();
        private static readonly ConcurrentDictionary<uint, Game> gamesByDealer = new ConcurrentDictionary<uint, Game>();
        private static readonly ConcurrentDictionary<uint, Shoe> shoes = new ConcurrentDictionary<uint, Shoe>();
        private static readonly ConcurrentDictionary<uint, int> owedChips = new ConcurrentDictionary<uint, int>();
        private static readonly ConcurrentDictionary<uint, int> plaqueDigits = new ConcurrentDictionary<uint, int>();

        // =====================================================================
        // Entry point
        // =====================================================================

        /// <summary>
        /// Returns true if the item was a blackjack object (use fully handled here).
        /// </summary>
        public static bool TryHandleUse(Player player, WorldObject item)
        {
            switch (item.WeenieClassId)
            {
                case DealerWcid:      OnDealerUse(player, item); return true;
                case HitLeverWcid:    OnControlUse(player, item, true); return true;
                case StandButtonWcid: OnControlUse(player, item, false); return true;
                case ChipPouchWcid:   OpenPouch(player, item); return true;
                default: return false;
            }
        }

        // =====================================================================
        // Dealer
        // =====================================================================

        private static void OnDealerUse(Player player, WorldObject dealer)
        {
            PayOwedChips(player);

            if (gamesByPlayer.TryGetValue(player.Guid.Full, out var myGame))
            {
                if (myGame.Dealer == dealer)
                    Announce(myGame, false);
                else
                    player.SendMessage("Finish your hand at the other table first.");
                return;
            }

            if (gamesByDealer.TryGetValue(dealer.Guid.Full, out var other))
            {
                Say(dealer, $"This table is in use, {player.Name}. Wait your turn.");
                return;
            }

            if (player.QuestManager.CanSolve(DailyQuest))
            {
                Ask(player, dealer, "Play your FREE daily hand of Blackjack?",
                    () => TryStart(player, dealer, true),
                    () => OfferPaidHand(player, dealer), false);
                return;
            }

            OfferPaidHand(player, dealer);
        }

        private static void OfferPaidHand(Player player, WorldObject dealer)
        {
            Ask(player, dealer, $"Bet {Bet} chips on a hand of Blackjack?",
                () => TryStart(player, dealer, false),
                null, false);
        }

        private static void TryStart(Player player, WorldObject dealer, bool free)
        {
            if (gamesByPlayer.ContainsKey(player.Guid.Full))
                return;

            if (player.Location.DistanceTo(dealer.Location) > PlayerRange)
            {
                player.SendMessage("You are too far from the table.");
                return;
            }

            if (free)
            {
                if (!player.QuestManager.CanSolve(DailyQuest))
                    return;
            }
            else if (CountChips(player) < Bet)
            {
                Say(dealer, $"You'll need {Bet} chips to play, {player.Name}.");
                return;
            }

            var game = new Game(player, dealer, free);

            // claim the seat atomically
            if (!gamesByDealer.TryAdd(dealer.Guid.Full, game))
            {
                Say(dealer, $"Someone beat you to the seat, {player.Name}.");
                return;
            }
            if (!gamesByPlayer.TryAdd(player.Guid.Full, game))
            {
                gamesByDealer.TryRemove(dealer.Guid.Full, out _);
                return;
            }

            if (free)
                player.QuestManager.Stamp(DailyQuest);
            else if (!player.TryConsumeFromInventoryWithNetworking(ChipWcid, Bet))
            {
                EndGame(game);
                return;
            }

            game.Hands.Add(new Hand { Bet = Bet });

            var shoe = shoes.GetOrAdd(dealer.Guid.Full, _ => new Shoe());
            if (shoe.NeedsShuffle)
            {
                shoe.Shuffle();
                Say(dealer, "Fresh shoe. Cards are shuffled.");
            }

            var hand = game.Hands[0];
            hand.Cards.Add(shoe.Draw());
            game.DealerCards.Add(shoe.Draw());
            hand.Cards.Add(shoe.Draw());
            game.DealerCards.Add(shoe.Draw());

            Motion(dealer, MotionCommand.Nod);
            Say(dealer, free ? $"A free hand, on the house. Good luck, {player.Name}." : $"{Bet} chips in. Good luck, {player.Name}.");
            Announce(game, false);
            Touch(game);

            // insurance (paid hands only)
            if (!free && game.DealerCards[0].Rank == 1 && CountChips(player) >= Bet / 2)
            {
                game.AwaitingAnswer = true;
                Ask(player, dealer, $"Dealer shows an Ace. Buy insurance for {Bet / 2} chips?",
                    () => { if (Alive(game)) { BuyInsurance(game); CheckNaturals(game); } },
                    () => { if (Alive(game)) CheckNaturals(game); });
                return;
            }

            CheckNaturals(game);
        }

        private static void BuyInsurance(Game game)
        {
            var player = game.Player;
            if (player == null) return;

            if (CountChips(player) >= Bet / 2 && player.TryConsumeFromInventoryWithNetworking(ChipWcid, Bet / 2))
            {
                game.Insurance = Bet / 2;
                player.SendMessage($"You buy insurance for {game.Insurance} chips.");
            }
        }

        private static void CheckNaturals(Game game)
        {
            game.AwaitingAnswer = false;

            var dealerBJ = IsBlackjack(game.DealerCards);
            var playerBJ = IsBlackjack(game.Hands[0].Cards);

            // dealer peeks when showing an Ace or ten-value card
            if (dealerBJ)
            {
                game.HoleRevealed = true;
                Announce(game, true);

                if (game.Insurance > 0)
                {
                    Pay(game, game.Insurance * 3);   // stake back + 2:1
                    game.Player?.SendMessage($"Insurance pays {game.Insurance * 2} chips.");
                }

                if (playerBJ)
                {
                    Say(game.Dealer, "Two blackjacks. It's a push.");
                    Motion(game.Dealer, MotionCommand.Shrug);
                    if (!game.Free) Pay(game, game.Hands[0].Bet);
                }
                else
                {
                    Say(game.Dealer, "Dealer blackjack. Better luck next time.");
                    Motion(game.Dealer, MotionCommand.Laugh);
                }
                EndGame(game);
                return;
            }

            if (game.Insurance > 0)
                game.Player?.SendMessage("No dealer blackjack. Your insurance is lost.");

            if (playerBJ)
            {
                game.HoleRevealed = true;
                Announce(game, true);
                var win = game.Hands[0].Bet * 3 / 2;
                Pay(game, game.Free ? win : game.Hands[0].Bet + win);
                Say(game.Dealer, $"BLACKJACK! Pays {win} chips.");
                Motion(game.Dealer, MotionCommand.ClapHands);
                PlaySound(game, ACE.Entity.Enum.Sound.RaiseTrait);
                EndGame(game);
                return;
            }

            BeginHand(game);
        }

        // =====================================================================
        // Player turn
        // =====================================================================

        /// <summary>
        /// Starts (or resumes) the active hand: offers split / double where legal.
        /// </summary>
        private static void BeginHand(Game game)
        {
            if (!Alive(game)) return;

            var hand = game.ActiveHand;
            var player = game.Player;

            if (hand.SplitAces || Total(hand.Cards) == 21)
            {
                hand.Done = true;
                NextHand(game);
                return;
            }

            if (game.Hands.Count > 1)
                Say(game.Dealer, $"Hand {game.Active + 1}: {Describe(hand.Cards)}");

            if (player == null || game.Free || hand.Cards.Count != 2)
            {
                player?.SendMessage(ControlsHint);
                return;
            }

            var canAfford = CountChips(player) >= hand.Bet;

            // split: first hand only, one split max, same value
            if (game.Hands.Count == 1 && canAfford && Value(hand.Cards[0]) == Value(hand.Cards[1]))
            {
                game.AwaitingAnswer = true;
                Ask(player, game.Dealer, $"You have a pair. Split for another {hand.Bet} chips?",
                    () => { if (Alive(game)) { game.AwaitingAnswer = false; Split(game); } },
                    () => { if (Alive(game)) { game.AwaitingAnswer = false; OfferDouble(game); } });
                return;
            }

            OfferDouble(game);
        }

        private static void OfferDouble(Game game)
        {
            var player = game.Player;
            var hand = game.ActiveHand;

            if (player == null || game.Free || hand.Cards.Count != 2 || CountChips(player) < hand.Bet)
            {
                player?.SendMessage(ControlsHint);
                return;
            }

            game.AwaitingAnswer = true;
            Ask(player, game.Dealer, $"{Describe(hand.Cards)}. Double down for another {hand.Bet} chips?",
                () => { if (Alive(game)) { game.AwaitingAnswer = false; Double(game); } },
                () => { if (Alive(game)) { game.AwaitingAnswer = false; player.SendMessage(ControlsHint); } });
        }

        private static void Split(Game game)
        {
            var player = game.Player;
            var first = game.ActiveHand;

            if (player == null || CountChips(player) < first.Bet || !player.TryConsumeFromInventoryWithNetworking(ChipWcid, first.Bet))
            {
                OfferDouble(game);
                return;
            }

            var shoe = shoes[game.Dealer.Guid.Full];
            var aces = first.Cards[0].Rank == 1;

            var second = new Hand { Bet = first.Bet, SplitAces = aces };
            second.Cards.Add(first.Cards[1]);
            first.Cards.RemoveAt(1);
            first.SplitAces = aces;

            first.Cards.Add(shoe.Draw());
            second.Cards.Add(shoe.Draw());
            game.Hands.Add(second);

            Say(game.Dealer, aces ? "Split aces: one card each." : "Hands split.");
            Touch(game);
            BeginHand(game);
        }

        private static void Double(Game game)
        {
            var player = game.Player;
            var hand = game.ActiveHand;

            if (player == null || CountChips(player) < hand.Bet || !player.TryConsumeFromInventoryWithNetworking(ChipWcid, hand.Bet))
                return;

            hand.Bet *= 2;
            hand.Cards.Add(shoes[game.Dealer.Guid.Full].Draw());
            hand.Done = true;

            Say(game.Dealer, $"Doubled. One card: {Describe(hand.Cards)}");
            UpdateBoard(game);
            if (Total(hand.Cards) > 21)
                Say(game.Dealer, "Bust!");

            Touch(game);
            NextHand(game);
        }

        private static void OnControlUse(Player player, WorldObject control, bool hit)
        {
            if (!gamesByPlayer.TryGetValue(player.Guid.Full, out var game) || control.Location.DistanceTo(game.Dealer.Location) > ControlRange)
            {
                if (gamesByDealer.Values.Any(g => control.Location.DistanceTo(g.Dealer.Location) <= ControlRange))
                    player.SendMessage("Wait your turn.");
                else
                    player.SendMessage("Talk to the dealer to start a hand.");
                return;
            }

            if (game.Busy || game.AwaitingAnswer)
            {
                player.SendMessage(game.AwaitingAnswer ? "Answer the dealer's question first." : "The dealer is busy.");
                return;
            }

            if (player.Location.DistanceTo(game.Dealer.Location) > PlayerRange)
            {
                player.SendMessage("You are too far from the table.");
                return;
            }

            // lever / button feedback
            control.EnqueueBroadcast(new GameMessageSound(control.Guid, ACE.Entity.Enum.Sound.TriggerActivated));
            if (control.MotionTableId != 0)
                Motion(control, MotionCommand.Twitch1, false);

            Touch(game);
            var hand = game.ActiveHand;

            if (!hit)
            {
                hand.Done = true;
                Say(game.Dealer, $"{player.Name} stands on {Total(hand.Cards)}.");
                NextHand(game);
                return;
            }

            hand.Cards.Add(shoes[game.Dealer.Guid.Full].Draw());
            var total = Total(hand.Cards);
            Say(game.Dealer, $"{Describe(hand.Cards)}");
            UpdateBoard(game);

            if (total > 21)
            {
                hand.Done = true;
                Say(game.Dealer, "Bust!");
                Motion(game.Dealer, MotionCommand.Shrug);
                NextHand(game);
            }
            else if (IsCharlie(hand.Cards))
            {
                hand.Done = true;
                Say(game.Dealer, "Five-card Charlie! That hand wins.");
                Motion(game.Dealer, MotionCommand.ClapHands);
                NextHand(game);
            }
            else if (total == 21)
            {
                hand.Done = true;
                NextHand(game);
            }
        }

        private static void NextHand(Game game)
        {
            if (game.Active + 1 < game.Hands.Count)
            {
                game.Active++;
                BeginHand(game);
                return;
            }

            DealerTurn(game);
        }

        // =====================================================================
        // Dealer turn + settlement
        // =====================================================================

        private static void DealerTurn(Game game)
        {
            game.Busy = true;
            game.HoleRevealed = true;
            Say(game.Dealer, $"Dealer reveals: {Describe(game.DealerCards)}");
            UpdateBoard(game);

            // everyone busted: no need to draw
            // every hand already decided (bust or Five-card Charlie): no need to draw
            if (game.Hands.All(h => Total(h.Cards) > 21 || IsCharlie(h.Cards)))
            {
                Settle(game);
                return;
            }

            DealerDrawStep(game);
        }

        private static void DealerDrawStep(Game game)
        {
            // dealer stands on all 17s (including soft 17)
            if (Total(game.DealerCards) >= 17)
            {
                Settle(game);
                return;
            }

            var chain = new ActionChain();
            chain.AddDelaySeconds(1.0);
            chain.AddAction(game.Dealer, () =>
            {
                if (!gamesByDealer.TryGetValue(game.Dealer.Guid.Full, out var g) || g != game) return;

                game.DealerCards.Add(shoes[game.Dealer.Guid.Full].Draw());
                Say(game.Dealer, $"Dealer draws: {Describe(game.DealerCards)}");
                UpdateBoard(game);
                DealerDrawStep(game);
            });
            chain.EnqueueChain();
        }

        private static void Settle(Game game)
        {
            var dealerTotal = Total(game.DealerCards);
            var dealerBust = dealerTotal > 21;
            if (dealerBust)
                Say(game.Dealer, "Dealer busts!");

            int wins = 0, losses = 0;

            for (var i = 0; i < game.Hands.Count; i++)
            {
                var hand = game.Hands[i];
                var total = Total(hand.Cards);
                var label = game.Hands.Count > 1 ? $"Hand {i + 1}" : "You";

                if (total > 21)
                {
                    game.Player?.SendMessage($"{label}: bust. You lose {hand.Bet} chips.");
                    losses++;
                }
                else if (IsCharlie(hand.Cards))
                {
                    Pay(game, game.Free ? hand.Bet : hand.Bet * 2);
                    game.Player?.SendMessage($"{label}: Five-card Charlie! You win {hand.Bet} chips!");
                    wins++;
                }
                else if (dealerBust || total > dealerTotal)
                {
                    Pay(game, game.Free ? hand.Bet : hand.Bet * 2);
                    game.Player?.SendMessage($"{label}: {total} beats {dealerTotal}. You win {hand.Bet} chips!");
                    wins++;
                }
                else if (total == dealerTotal)
                {
                    if (!game.Free) Pay(game, hand.Bet);
                    game.Player?.SendMessage($"{label}: push at {total}.{(game.Free ? "" : " Your bet is returned.")}");
                }
                else
                {
                    game.Player?.SendMessage($"{label}: {total} loses to {dealerTotal}.");
                    losses++;
                }
            }

            if (wins > losses)
            {
                Say(game.Dealer, "Winner! Well played.");
                Motion(game.Dealer, MotionCommand.BowDeep);
                PlaySound(game, ACE.Entity.Enum.Sound.ReceiveItem);
            }
            else if (losses > wins)
            {
                Say(game.Dealer, "The house wins this one.");
                Motion(game.Dealer, MotionCommand.Laugh);
            }
            else
            {
                Motion(game.Dealer, MotionCommand.Shrug);
            }

            EndGame(game);
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void EndGame(Game game)
        {
            game.Ended = true;
            gamesByPlayer.TryRemove(new KeyValuePair<uint, Game>(game.PlayerGuid, game));
            gamesByDealer.TryRemove(new KeyValuePair<uint, Game>(game.Dealer.Guid.Full, game));
        }

        private static bool Alive(Game game)
        {
            return !game.Ended && gamesByPlayer.TryGetValue(game.PlayerGuid, out var g) && g == game;
        }

        /// <summary>
        /// Records activity and (re)arms the idle-forfeit timer.
        /// </summary>
        private static void Touch(Game game)
        {
            var seq = ++game.Seq;

            var chain = new ActionChain();
            chain.AddDelaySeconds(IdleTimeout);
            chain.AddAction(game.Dealer, () =>
            {
                if (!Alive(game) || game.Seq != seq || game.Busy) return;

                game.Player?.SendMessage("You took too long. Your hand is forfeited.");
                Say(game.Dealer, "Player walked away. The table is open.");
                EndGame(game);
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// Yes/No popup. Answers are marshalled onto the dealer's thread.
        /// </summary>
        private static void Ask(Player player, WorldObject dealer, string text, Action yes, Action no, bool inGame = true)
        {
            var confirm = new Confirmation_Custom(player.Guid,
                () => new ActionChain(dealer, yes).EnqueueChain(),
                () => { if (no != null) new ActionChain(dealer, no).EnqueueChain(); });

            if (!player.ConfirmationManager.EnqueueSend(confirm, text))
            {
                player.SendMessage("Answer your other open question first, then try again.");

                // a game waiting on this answer would otherwise hang until the idle timeout
                if (inGame && no != null)
                    new ActionChain(dealer, no).EnqueueChain();
            }
        }

        // =====================================================================
        // Scoreboard plaques
        // =====================================================================

        private static bool IsPlaque(uint wcid) => wcid == PlayerTensWcid || wcid == PlayerOnesWcid || wcid == DealerTensWcid || wcid == DealerOnesWcid;

        /// <summary>
        /// Called from WorldObject.CalculateObjDesc: paints the current digit onto a plaque.
        /// </summary>
        public static void AddPlaqueTexture(WorldObject wo, ACE.Entity.ObjDesc objDesc)
        {
            if (!IsPlaque(wo.WeenieClassId)) return;

            plaqueDigits.TryGetValue(wo.Guid.Full, out var digit);   // defaults to 0
            objDesc.TextureChanges.Add(new PropertiesTextureMap { PartIndex = PlaquePartIndex, OldTexture = PlaqueFaceTexture, NewTexture = DigitZeroTexture + (uint)Math.Clamp(digit, 0, 9) });
        }

        /// <summary>
        /// Shows the active player hand total and the dealer's visible total on the plaques near this dealer.
        /// </summary>
        private static void UpdateBoard(Game game)
        {
            var landblock = game.Dealer.CurrentLandblock;
            if (landblock == null) return;

            var playerTotal = game.Hands.Count > 0 ? Total(game.ActiveHand.Cards) : 0;
            var dealerTotal = game.HoleRevealed ? Total(game.DealerCards) : (game.DealerCards.Count > 0 ? Value(game.DealerCards[0]) : 0);

            foreach (var wo in landblock.GetWorldObjectsForPhysicsHandling())
            {
                if (!IsPlaque(wo.WeenieClassId) || wo.Location.DistanceTo(game.Dealer.Location) > ControlRange)
                    continue;

                int digit;
                switch (wo.WeenieClassId)
                {
                    case PlayerTensWcid: digit = playerTotal / 10 % 10; break;
                    case PlayerOnesWcid: digit = playerTotal % 10; break;
                    case DealerTensWcid: digit = dealerTotal / 10 % 10; break;
                    default:             digit = dealerTotal % 10; break;
                }

                if (plaqueDigits.TryGetValue(wo.Guid.Full, out var old) && old == digit)
                    continue;

                plaqueDigits[wo.Guid.Full] = digit;
                wo.EnqueueBroadcastUpdateObject();
            }
        }

        private static void OpenPouch(Player player, WorldObject pouch)
        {
            if (!player.TryConsumeFromInventoryWithNetworking(pouch, 1))
            {
                player.SendMessage("Pick up the pouch first.");
                return;
            }

            player.SendMessage($"You open the pouch and find {PouchChips} Blackjack Chips.");
            GiveChips(player, PouchChips);
        }

        private static int CountChips(Player player)
        {
            return player.GetNumInventoryItemsOfWCID(ChipWcid, true);
        }

        private static void Pay(Game game, int amount)
        {
            var player = game.Player;
            if (player == null)
            {
                owedChips.AddOrUpdate(game.PlayerGuid, amount, (_, v) => v + amount);
                return;
            }
            GiveChips(player, amount);
        }

        private static void PayOwedChips(Player player)
        {
            if (owedChips.TryRemove(player.Guid.Full, out var owed) && owed > 0)
            {
                player.SendMessage($"The dealer hands over {owed} chips you were owed.");
                GiveChips(player, owed);
            }
        }

        private static void GiveChips(Player player, int amount)
        {
            while (amount > 0)
            {
                var chips = WorldObjectFactory.CreateNewWorldObject(ChipWcid);
                if (chips == null) return;

                var stack = Math.Min(amount, Math.Max(1, (int)(chips.MaxStackSize ?? 1)));
                chips.SetStackSize(stack);

                if (!player.TryCreateInInventoryWithNetworking(chips, out _, true))
                {
                    chips.Destroy();
                    owedChips.AddOrUpdate(player.Guid.Full, amount, (_, v) => v + amount);
                    player.SendMessage($"Your pack is full. Use the dealer to collect your {amount} chips.");
                    return;
                }
                amount -= stack;
            }
        }

        private static void Say(WorldObject dealer, string text)
        {
            dealer.EnqueueBroadcast(new GameMessageHearSpeech(text, dealer.Name, dealer.Guid.Full, ChatMessageType.Speech), SpeechRange);
        }

        private static void Motion(WorldObject wo, MotionCommand motion, bool useStance = true)
        {
            var chain = new ActionChain();
            wo.EnqueueMotion(chain, motion, 1.0f, useStance);
            chain.EnqueueChain();
        }

        private static void PlaySound(Game game, Sound sound)
        {
            var player = game.Player;
            if (player != null)
                player.EnqueueBroadcast(new GameMessageSound(player.Guid, sound));
        }

        private static void Announce(Game game, bool revealHole)
        {
            var dealerText = revealHole || game.HoleRevealed
                ? $"{Describe(game.DealerCards)}"
                : $"{CardName(game.DealerCards[0])} and one face down";

            var hands = game.Hands.Count == 1
                ? $"You: {Describe(game.Hands[0].Cards)}"
                : string.Join(" | ", game.Hands.Select((h, i) => $"Hand {i + 1}{(i == game.Active ? "*" : "")}: {Describe(h.Cards)}"));

            Say(game.Dealer, $"{hands}. Dealer: {dealerText}.");
            UpdateBoard(game);
        }

        // ---- card math ----

        private static int Value(Card c) => c.Rank == 1 ? 11 : Math.Min(c.Rank, 10);

        private static int Total(List<Card> cards)
        {
            var total = 0;
            var aces = 0;
            foreach (var c in cards)
            {
                total += Value(c);
                if (c.Rank == 1) aces++;
            }
            while (total > 21 && aces > 0)
            {
                total -= 10;
                aces--;
            }
            return total;
        }

        private static bool IsBlackjack(List<Card> cards) => cards.Count == 2 && Total(cards) == 21;

        // house rule: 5 cards without busting wins automatically
        private static bool IsCharlie(List<Card> cards) => cards.Count >= 5 && Total(cards) <= 21;

        private static readonly string[] RankNames = { "", "Ace", "2", "3", "4", "5", "6", "7", "8", "9", "10", "Jack", "Queen", "King" };
        private static readonly string[] SuitNames = { "Spades", "Hearts", "Diamonds", "Clubs" };

        private static string CardName(Card c) => $"{RankNames[c.Rank]} of {SuitNames[c.Suit]}";

        private static string Describe(List<Card> cards) => $"{string.Join(", ", cards.Select(CardName))} ({Total(cards)})";

        // =====================================================================
        // Types
        // =====================================================================

        private struct Card
        {
            public int Rank;   // 1 = Ace ... 13 = King
            public int Suit;   // 0-3
        }

        private class Hand
        {
            public List<Card> Cards = new List<Card>();
            public int Bet;
            public bool Done;
            public bool SplitAces;
        }

        private class Game
        {
            public readonly uint PlayerGuid;
            public readonly WorldObject Dealer;
            public readonly bool Free;

            public List<Hand> Hands = new List<Hand>();
            public int Active;
            public List<Card> DealerCards = new List<Card>();
            public int Insurance;

            public bool HoleRevealed;
            public bool AwaitingAnswer;
            public bool Busy;
            public bool Ended;
            public int Seq;

            public Game(Player player, WorldObject dealer, bool free)
            {
                PlayerGuid = player.Guid.Full;
                Dealer = dealer;
                Free = free;
            }

            public Player Player => PlayerManager.GetOnlinePlayer(PlayerGuid);
            public Hand ActiveHand => Hands[Active];
        }

        private class Shoe
        {
            private readonly List<Card> cards = new List<Card>();
            private int next;

            // reshuffle once 75% of the shoe has been dealt
            public bool NeedsShuffle => cards.Count == 0 || next >= cards.Count * 3 / 4;

            public void Shuffle()
            {
                cards.Clear();
                for (var d = 0; d < DeckCount; d++)
                    for (var s = 0; s < 4; s++)
                        for (var r = 1; r <= 13; r++)
                            cards.Add(new Card { Rank = r, Suit = s });

                for (var i = cards.Count - 1; i > 0; i--)
                {
                    var j = Random.Shared.Next(i + 1);
                    (cards[i], cards[j]) = (cards[j], cards[i]);
                }
                next = 0;
            }

            public Card Draw()
            {
                if (next >= cards.Count)
                    Shuffle();
                return cards[next++];
            }
        }
    }
}
