using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// A summonable creature-clone that fights monsters for its owner.
    /// The hatched pet is spawned from the imprinted creature's own weenie (see
    /// PetDevice.SummonCreature), so it natively has that creature's attributes, health,
    /// skills, body-part damage and spells. Its combat power is then scaled by level via
    /// Pet.PetPowerFactor() (parent-equal at level 7). It uses the stock monster combat AI.
    /// </summary>
    public partial class CombatPet : Pet
    {
        // Emote ACTION types (PropertiesEmoteAction.Type) that affect the world around the
        // pet and must be dropped from an inherited emote set.
        private static readonly HashSet<EmoteType> BlockedEmoteTypes = new HashSet<EmoteType>
        {
            EmoteType.StartEvent,      // start/stop events
            EmoteType.StopEvent,
            EmoteType.LocalBroadcast,  // local broadcast
            EmoteType.WorldBroadcast,  // world broadcast
            EmoteType.LocalSignal,     // local signal
            EmoteType.Generate,        // generate things
        };

        // Whole emote SETS (PropertiesEmote.Category) to drop outright. "Scream" is an
        // EmoteCategory, not an EmoteType, so it is filtered here.
        private static readonly HashSet<EmoteCategory> BlockedEmoteCategories = new HashSet<EmoteCategory>
        {
            EmoteCategory.Scream,
        };

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public CombatPet(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
            StripClonedWorldData();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public CombatPet(Biota biota) : base(biota)
        {
            SetEphemeralValues();
            StripClonedWorldData();
        }

        private void SetEphemeralValues()
        {
        }

        /// <summary>
        /// A CombatPet is cloned from the parent monster's weenie, so its Biota inherits that
        /// monster's generator and emotes. Neither should run on a summoned pet, so this strips
        /// them from the PET'S OWN Biota (the cached parent Weenie is never touched):
        ///   - the generator, so the pet cannot spawn other monsters;
        ///   - world-affecting emote actions (broadcasts, events, local signal, generate) and
        ///     whole scream emote sets, plus any emote set left empty afterwards.
        /// Runs at construction, before Init()/EnterWorld().
        /// </summary>
        private void StripClonedWorldData()
        {
            // ---- generator: pets must not spawn other monsters ----
            Biota.PropertiesGenerator?.Clear();
            RemoveProperty(PropertyInt.InitGeneratedObjects);
            RemoveProperty(PropertyInt.MaxGeneratedObjects);

            // ---- emotes: drop world-affecting sets/actions ----
            if (Biota.PropertiesEmote != null)
            {
                foreach (var set in Biota.PropertiesEmote.ToList())
                {
                    // drop entire blocked-category sets (e.g. Scream)
                    if (BlockedEmoteCategories.Contains(set.Category))
                    {
                        Biota.PropertiesEmote.Remove(set);
                        continue;
                    }

                    // drop blocked action types within the set
                    if (set.PropertiesEmoteAction != null)
                    {
                        foreach (var action in set.PropertiesEmoteAction.ToList())
                        {
                            if (BlockedEmoteTypes.Contains((EmoteType)action.Type))
                                set.PropertiesEmoteAction.Remove(action);
                        }
                    }

                    // drop any set left with no actions
                    if (set.PropertiesEmoteAction == null || set.PropertiesEmoteAction.Count == 0)
                        Biota.PropertiesEmote.Remove(set);
                }
            }
        }

        public override bool? Init(Player player, PetDevice petDevice)
        {
            var success = base.Init(player, petDevice);

            if (success == null || !success.Value)
                return success;

            SetCombatMode(CombatMode.Melee);
            MonsterState = State.Awake;
            IsAwake = true;

            // Tolerance.Monster: the pet only hunts monsters -- it never targets players
            // (including its owner) or other pets. TargetingTactic Nearest|LastDamager: go
            // after the closest monster, or whatever hit it last.
            SetProperty(PropertyInt.Tolerance, (int)Tolerance.Monster);
            SetProperty(PropertyInt.TargetingTactic, (int)(TargetingTactic.Nearest | TargetingTactic.LastDamager));

            // copy ratings from pet device
            DamageRating = petDevice.GearDamage;
            DamageResistRating = petDevice.GearDamageResist;
            CritDamageRating = petDevice.GearCritDamage;
            CritDamageResistRating = petDevice.GearCritDamageResist;
            CritRating = petDevice.GearCrit;
            CritResistRating = petDevice.GearCritResist;

            // combat pets attack monsters of any faction; matching the owner's faction bits
            // keeps the pet from being aggroed as a faction enemy by the owner's allies.
            Faction1Bits = player.Faction1Bits;

            return true;
        }

        public override void Sleep()
        {
            // pets dont really go to sleep -- they keep scanning for new targets
            return;
        }
    }
}