using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Pet battles: two players pit their summoned combat pets against each other.
    ///   /pet battle <player>  -- challenge (or accept, if that player already challenged you)
    ///   /pet accept           -- accept the pending challenge
    ///   /pet forfeit          -- give up the battle (or withdraw a challenge)
    ///
    /// Battles are keyed by PLAYER (not by pet object), because a pet can be rebuilt by a
    /// warp/re-summon; the live pet is always looked up as owner.CurrentActivePet.
    /// The losing pet is defeated normally (its usual auto-resummon applies). The winner is
    /// healed to full. No XP is awarded either way (pet deaths never grant XP).
    /// </summary>
    public static class PetBattle
    {
        public const float MaxStartDistance = 30f;     // players must be this close to start
        public const double ChallengeTimeout = 60.0;   // seconds a challenge stays open

        private static readonly object BattleLock = new object();

        // challenged player guid -> (challenger guid, expire time)
        private static readonly Dictionary<uint, (uint Challenger, double Expires)> Challenges = new Dictionary<uint, (uint, double)>();

        // player guid -> opponent player guid (stored both directions)
        private static readonly Dictionary<uint, uint> Battles = new Dictionary<uint, uint>();

        private static void Tell(Player p, string msg)
        {
            p?.Session?.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
        }

        private static string PetName(Pet pet, Player owner)
        {
            var name = pet?.Name ?? "pet";
            var prefix = owner != null ? owner.Name + "'s " : null;
            if (prefix != null && name.StartsWith(prefix))
                name = name.Substring(prefix.Length);
            return name;
        }

        public static bool IsInBattle(Player p)
        {
            if (p == null) return false;
            lock (BattleLock)
                return Battles.ContainsKey(p.Guid.Full);
        }

        /// <summary>True if both creatures are combat pets whose owners are battling each other.</summary>
        public static bool AreBattling(Creature a, Creature b)
        {
            var oa = (a as Pet)?.P_PetOwner;
            var ob = (b as Pet)?.P_PetOwner;
            if (oa == null || ob == null) return false;

            lock (BattleLock)
                return Battles.TryGetValue(oa.Guid.Full, out var opp) && opp == ob.Guid.Full;
        }

        private static string Validate(Player a, Player b)
        {
            if (a == null || b == null) return "That player is not online.";
            if (a == b) return "Your pet can't battle itself.";
            if (!(a.CurrentActivePet is CombatPet)) return "You need a summoned combat pet to battle.";
            if (!(b.CurrentActivePet is CombatPet)) return $"{b.Name} doesn't have a combat pet summoned.";
            if (IsInBattle(a)) return "Your pet is already in a battle.";
            if (IsInBattle(b)) return $"{b.Name}'s pet is already in a battle.";
            if (a.Location == null || b.Location == null || a.Location.DistanceTo(b.Location) > MaxStartDistance)
                return $"{b.Name} is too far away. Stand within {(int)MaxStartDistance} meters to battle.";
            return null;
        }

        /// <summary>"/pet battle name" -- challenge, or accept if they already challenged you.</summary>
        public static void Challenge(Player challenger, string targetName)
        {
            var target = PlayerManager.GetOnlinePlayer(targetName);

            var error = Validate(challenger, target);
            if (error != null) { Tell(challenger, error); return; }

            var now = Time.GetUnixTime();
            bool theyChallengedMe;

            lock (BattleLock)
            {
                theyChallengedMe = Challenges.TryGetValue(challenger.Guid.Full, out var c)
                    && c.Challenger == target.Guid.Full && c.Expires > now;

                if (!theyChallengedMe)
                    Challenges[target.Guid.Full] = (challenger.Guid.Full, now + ChallengeTimeout);
            }

            if (theyChallengedMe)
            {
                Start(target, challenger);
                return;
            }

            Tell(challenger, $"You challenge {target.Name} to a pet battle! They have {(int)ChallengeTimeout} seconds to accept.");
            Tell(target, $"{challenger.Name} challenges your pet to a battle! Type /pet accept to fight, or ignore it to decline.");
        }

        /// <summary>"/pet accept" -- accept the pending challenge.</summary>
        public static void Accept(Player p)
        {
            uint challengerGuid;
            lock (BattleLock)
            {
                if (!Challenges.TryGetValue(p.Guid.Full, out var c) || c.Expires <= Time.GetUnixTime())
                {
                    Challenges.Remove(p.Guid.Full);
                    Tell(p, "You have no pending pet battle challenge.");
                    return;
                }
                challengerGuid = c.Challenger;
            }

            var challenger = PlayerManager.GetOnlinePlayer(challengerGuid);
            var error = Validate(p, challenger);
            if (error != null) { Tell(p, error); return; }

            Start(challenger, p);
        }

        private static void Start(Player a, Player b)
        {
            var petA = a.CurrentActivePet as CombatPet;
            var petB = b.CurrentActivePet as CombatPet;
            if (petA == null || petB == null) return;

            lock (BattleLock)
            {
                Challenges.Remove(a.Guid.Full);
                Challenges.Remove(b.Guid.Full);
                Battles[a.Guid.Full] = b.Guid.Full;
                Battles[b.Guid.Full] = a.Guid.Full;
            }

            Prepare(petA, petB);
            Prepare(petB, petA);

            var msg = $"Pet battle! {a.Name}'s {PetName(petA, a)} vs {b.Name}'s {PetName(petB, b)}. Fight!";
            Tell(a, msg);
            Tell(b, msg);
        }

        // heal to full and point at the opponent, on the pet's own thread
        private static void Prepare(CombatPet pet, CombatPet opponent)
        {
            var chain = new ActionChain();
            chain.AddAction(pet, () =>
            {
                pet.SetMaxVitals();
                pet.Mode = Pet.PetMode.Follow;
                Engage(pet, opponent);
            });
            chain.EnqueueChain();
        }

        private static void Engage(Pet pet, Creature opponent)
        {
            pet.CommandedTarget = null;
            if (pet.AttackTarget != opponent)
                pet.CurrentAttackType = null;
            pet.AttackTarget = opponent;
            if (pet.MonsterState == Creature.State.Idle)
                pet.MonsterState = Creature.State.Awake;
            pet.IsAwake = true;
        }

        /// <summary>
        /// Called from Pet.PetTryAcquireTarget when the pet has no target. While battling,
        /// keeps the pet locked on the opposing pet. If the opponent is gone (dismissed,
        /// owner logged off), the battle ends in this pet's favor. Returns true if engaged.
        /// </summary>
        public static bool TryEngageOpponent(Pet pet)
        {
            var owner = pet?.P_PetOwner;
            if (owner == null) return false;

            uint oppGuid;
            lock (BattleLock)
            {
                if (!Battles.TryGetValue(owner.Guid.Full, out oppGuid))
                    return false;
            }

            var oppPlayer = PlayerManager.GetOnlinePlayer(oppGuid);
            var oppPet = oppPlayer?.CurrentActivePet as CombatPet;

            if (oppPet == null || oppPet.IsDestroyed || oppPet.IsDead)
            {
                End(owner, oppGuid, oppPlayer, "forfeits");
                return false;
            }

            Engage(pet, oppPet);
            return true;
        }

        /// <summary>Called at the top of Pet.Die -- the dying pet's owner loses.</summary>
        public static void OnPetDefeated(Pet pet)
        {
            var loser = pet?.P_PetOwner;
            if (loser == null) return;

            uint winnerGuid;
            lock (BattleLock)
            {
                if (!Battles.TryGetValue(loser.Guid.Full, out winnerGuid))
                    return;
            }

            var winner = PlayerManager.GetOnlinePlayer(winnerGuid);
            End(winner, loser.Guid.Full, loser, "is defeated");
        }

        /// <summary>"/pet forfeit" -- give up the battle, or withdraw your outgoing challenge.</summary>
        public static void Forfeit(Player p)
        {
            uint oppGuid;
            bool inBattle;
            lock (BattleLock)
            {
                inBattle = Battles.TryGetValue(p.Guid.Full, out oppGuid);

                if (!inBattle)
                {
                    // withdraw any challenge this player sent
                    var removed = false;
                    foreach (var kvp in new List<KeyValuePair<uint, (uint Challenger, double Expires)>>(Challenges))
                    {
                        if (kvp.Value.Challenger == p.Guid.Full)
                        {
                            Challenges.Remove(kvp.Key);
                            removed = true;
                        }
                    }
                    Tell(p, removed ? "You withdraw your pet battle challenge." : "You are not in a pet battle.");
                    return;
                }
            }

            End(PlayerManager.GetOnlinePlayer(oppGuid), p.Guid.Full, p, "forfeits");
        }

        /// <summary>Ends a battle. winner/loser players may be null (logged off).</summary>
        private static void End(Player winner, uint loserGuid, Player loser, string how)
        {
            lock (BattleLock)
            {
                Battles.Remove(loserGuid);
                if (winner != null)
                    Battles.Remove(winner.Guid.Full);
                else
                {
                    // winner offline: drop whatever entry pointed at the loser
                    foreach (var kvp in new List<KeyValuePair<uint, uint>>(Battles))
                        if (kvp.Value == loserGuid)
                            Battles.Remove(kvp.Key);
                }
            }

            var winnerPet = winner?.CurrentActivePet as CombatPet;
            var loserPet = loser?.CurrentActivePet as Pet;

            var loserName = loser != null ? $"{loser.Name}'s {PetName(loserPet, loser)}" : "The opposing pet";
            var winnerName = winner != null ? $"{winner.Name}'s {PetName(winnerPet, winner)}" : "the other pet";

            var msg = $"{loserName} {how}! {winnerName} wins the pet battle!";
            Tell(winner, msg);
            Tell(loser, msg);

            // winner: heal, and drop the fight so it goes back to following its owner
            if (winnerPet != null)
            {
                var chain = new ActionChain();
                chain.AddAction(winnerPet, () =>
                {
                    winnerPet.SetMaxVitals();
                    if (winnerPet.AttackTarget is Pet)
                    {
                        winnerPet.AttackTarget = null;
                        winnerPet.CurrentAttackType = null;
                    }
                });
                chain.EnqueueChain();
            }

            // loser who forfeited (pet still alive): stand it down too
            if (loserPet != null && !loserPet.IsDead && loserPet.AttackTarget is Pet)
            {
                var chain = new ActionChain();
                chain.AddAction(loserPet, () =>
                {
                    loserPet.AttackTarget = null;
                    loserPet.CurrentAttackType = null;
                });
                chain.EnqueueChain();
            }
        }
    }
}
