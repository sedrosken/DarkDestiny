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
        /// A CombatPet is built from the parent monster's cached Weenie. WeenieConverter.ConvertToBiota
        /// (referenceWeenieCollectionsForCommonProperties = true) hands the new Biota the cached Weenie's
        /// OWN PropertiesGenerator / PropertiesEmote / PropertiesCreateList / PropertiesEventFilter
        /// instances. Mutating them in place edits the cached Weenie for every live and future instance
        /// of that wcid, so they must be detached before anything is stripped.
        /// Runs at construction, before Init()/EnterWorld().
        /// </summary>
        private void StripClonedWorldData()
        {
            // ---- generator: pets must not spawn other monsters ----
            Biota.PropertiesGenerator = null;      // detach; never Clear() the shared list
            GeneratorProfiles?.Clear();            // built from the shared list in the WorldObject ctor
            RemoveProperty(PropertyInt.InitGeneratedObjects);
            RemoveProperty(PropertyInt.MaxGeneratedObjects);
            ReinitializeHeartbeats();              // IsGenerator is now false -> no generator update/regen ticks

            // ---- emotes: rebuild as private deep clones, minus world-affecting sets/actions ----
            if (Biota.PropertiesEmote != null)
            {
                var filtered = new List<PropertiesEmote>(Biota.PropertiesEmote.Count);

                foreach (var set in Biota.PropertiesEmote)
                {
                    // drop entire blocked-category sets (e.g. Scream)
                    if (BlockedEmoteCategories.Contains(set.Category))
                        continue;

                    var clone = set.Clone();       // deep: clones PropertiesEmoteAction too

                    // drop blocked action types within the (private) set
                    for (var i = clone.PropertiesEmoteAction.Count - 1; i >= 0; i--)
                    {
                        if (BlockedEmoteTypes.Contains((EmoteType)clone.PropertiesEmoteAction[i].Type))
                            clone.PropertiesEmoteAction.RemoveAt(i);
                    }

                    // drop any set left with no actions
                    if (clone.PropertiesEmoteAction.Count > 0)
                        filtered.Add(clone);
                }

                Biota.PropertiesEmote = filtered;  // detach from the cached Weenie
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