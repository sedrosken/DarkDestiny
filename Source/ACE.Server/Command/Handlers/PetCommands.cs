using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public static class PetCommands
    {
        /// <summary>
        /// Player command: control the currently summoned pet.
        ///   /pet attack   -- order the pet onto the monster you have selected
        ///   /pet follow   -- resume following you and grabbing nearby monsters (default)
        ///   /pet stay     -- hold position (still defends itself if attacked)
        ///   /pet stop     -- break off the current fight but keep following
        ///   /pet come     -- return to you now
        ///   /pet status   -- show the pet's level, mode and target
        ///   /pet damage   -- toggle a chat line for every hit your pet lands
        ///   /pet magic    -- focus on casting spells (falls back to melee when it can't)
        ///   /pet melee    -- focus on melee, never cast
        ///   /pet auto     -- choose attacks automatically (default)
        ///   /pet battle <name> -- challenge another player's pet (or accept their challenge)
        ///   /pet accept   -- accept a pet battle challenge
        ///   /pet forfeit  -- give up a pet battle / withdraw a challenge
        /// </summary>
        [CommandHandler("pet", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
            "Commands your summoned pet.",
            "<attack | follow | stay | stop | come | status | damage | magic | melee | auto | battle <name> | accept | forfeit>\n" +
            "Example: select a monster, then /pet attack")]
        public static void HandlePet(Session session, params string[] parameters)
        {
            var player = session?.Player;
            if (player == null)
                return;

            // "/pet damage" is a display preference, not a pet order -- handle it before the
            // "no pet summoned" check so it can be toggled at any time.
            switch (parameters[0].ToLowerInvariant())
            {
                case "damage":
                case "dmg":
                    var on = Pet.TogglePetDamageMessages(player);
                    CommandHandlerHelper.WriteOutputInfo(session, on
                        ? "Pet damage messages ON -- you'll see each hit your pet lands."
                        : "Pet damage messages OFF.");
                    return;
            }

            var pet = player.CurrentActivePet;
            if (pet == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "You have no pet summoned.");
                return;
            }

            var sub = parameters[0].ToLowerInvariant();

            // ---- pet battles ----
            switch (sub)
            {
                case "battle":
                case "duel":
                    if (parameters.Length < 2)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, "Usage: /pet battle <player name>");
                        return;
                    }
                    PetBattle.Challenge(player, string.Join(" ", parameters, 1, parameters.Length - 1));
                    return;

                case "accept":
                    PetBattle.Accept(player);
                    return;

                case "forfeit":
                case "surrender":
                    PetBattle.Forfeit(player);
                    return;
            }

            // orders that would pull the pet off its opponent are blocked mid-battle
            if (PetBattle.IsInBattle(player) && (sub == "attack" || sub == "kill" || sub == "stay" || sub == "hold"
                || sub == "stop" || sub == "off" || sub == "come" || sub == "here"))
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Your pet is in a battle! Use /pet forfeit to give up.");
                return;
            }

            switch (sub)
            {
                case "attack":
                case "kill":
                {
                    // the monster the player currently has selected (health bar), falling
                    // back to whatever they last assessed
                    var sel = player.HealthQueryTarget ?? player.CurrentAppraisalTarget;
                    Creature target = null;
                    if (sel.HasValue)
                        target = player.FindObject(sel.Value, Player.SearchLocations.Everywhere) as Creature;

                    if (pet.CommandAttack(target, out var error))
                        CommandHandlerHelper.WriteOutputInfo(session, $"Your pet attacks {target.Name}!");
                    else
                        CommandHandlerHelper.WriteOutputInfo(session, error);
                    break;
                }

                case "follow":
                case "heel":
                    pet.CommandFollow();
                    CommandHandlerHelper.WriteOutputInfo(session, "Your pet is now following you.");
                    break;

                case "stay":
                case "hold":
                    pet.CommandStay();
                    CommandHandlerHelper.WriteOutputInfo(session, "Your pet holds its position.");
                    break;

                case "stop":
                case "off":
                    pet.CommandStop();
                    CommandHandlerHelper.WriteOutputInfo(session, "Your pet stands down.");
                    break;

                case "come":
                case "here":
                    pet.CommandCome(player);
                    CommandHandlerHelper.WriteOutputInfo(session, "Your pet returns to you.");
                    break;

                case "status":
                case "info":
                    CommandHandlerHelper.WriteOutputInfo(session, pet.CommandStatus());
                    break;

                case "magic":
                    pet.CommandCombatStyle(Pet.PetCombatStyle.Magic);
                    CommandHandlerHelper.WriteOutputInfo(session, "Your pet will focus on casting spells (falling back to melee when it can't cast).");
                    break;

                case "melee":
                    pet.CommandCombatStyle(Pet.PetCombatStyle.Melee);
                    CommandHandlerHelper.WriteOutputInfo(session, "Your pet will focus on melee and won't cast.");
                    break;

                case "auto":
                    pet.CommandCombatStyle(Pet.PetCombatStyle.Auto);
                    CommandHandlerHelper.WriteOutputInfo(session, "Your pet will choose attacks on its own (default).");
                    break;

                default:
                    CommandHandlerHelper.WriteOutputInfo(session,
                        "Usage: /pet <attack | follow | stay | stop | come | status | damage | magic | melee | auto | battle <name> | accept | forfeit>");
                    break;
            }
        }

        /// <summary>
        /// Admin/test command: grants XP directly to the currently summoned pet, so pet
        /// leveling can be tested without farming kills. Pets otherwise only earn XP from
        /// monsters they personally damage.
        /// </summary>
        [CommandHandler("petxp", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1,
            "Grants XP to your currently summoned pet (for testing pet leveling).",
            "<amount>\nExample: /petxp 100000000  (one pet level)")]
        public static void HandlePetXp(Session session, params string[] parameters)
        {
            if (session?.Player == null)
                return;

            if (!long.TryParse(parameters[0], out var amount) || amount <= 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Usage: /petxp <positive amount>");
                return;
            }

            if (session.Player.CurrentActivePet == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "You have no pet summoned.");
                return;
            }

            session.Player.GrantPetXP(amount);
            CommandHandlerHelper.WriteOutputInfo(session, $"Granted {amount:N0} XP to your pet.");
        }
    }
}
