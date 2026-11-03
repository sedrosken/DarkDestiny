using System;
using System.Collections.Generic;
using System.Numerics;

using log4net;

using ACE.Common;
using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Animation;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// A passive summonable creature
    /// </summary>
    public class Pet : Creature
    {
        public Player P_PetOwner;

        public PetDevice P_PetDevice;

        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public uint? PetDevice
        {
            get => GetProperty(PropertyInstanceId.PetDevice);
            set { if (value.HasValue) SetProperty(PropertyInstanceId.PetDevice, value.Value); else RemoveProperty(PropertyInstanceId.PetDevice); }
        }

        // =====================================================================
        //  CREATURE PET SYSTEM  (prototype)
        // ---------------------------------------------------------------------
        //  The pet's XP/level lives on the EGG (the PetDevice), so it survives
        //  logout and stowing. The pet itself is just a visual that reads the
        //  egg's level when it spawns.
        //
        //    * 10 levels total.
        //    * scale = level * 0.1   ->  L1 = 0.1  ...  L10 = 1.0 (full size)
        //    * at level 2 the egg "hatches": its inventory icon becomes the
        //      creature's icon.
        // =====================================================================

        public const int MaxPetLevel = 10;   // pet is full size at this level
        public const int HatchLevel  = 2;    // egg turns into the creature here

        // Size of the un-hatched egg follower. (0.1 -- the old level-1 scale -- looked
        // jellybean-sized, so the egg gets its own, bigger, fixed size.) Tune freely.
        public const float EggFollowerScale = 2.0f;

        // The egg remembers which creature it came from by its WCID. We store that WCID
        // in the unused LinkedPortalOne DID slot (a pet egg never uses portal links), so
        // no new property/DB column is needed.
        public const PropertyDataId SourceWcidProp = PropertyDataId.LinkedPortalOne;

        // Creatures that can't move or fight (trees), so their egg could never hatch. These
        // summon as the creature itself, at full size, from level 1. Add WCIDs here.
        public static readonly HashSet<uint> RootedCreatureWcids = new HashSet<uint>
        {
            4263,   // Tall Deru
        };

        // A clothed humanoid's real look (armor/robes + the palette that rides with them)
        // is assembled from its equipped items into its finished ObjDesc at runtime -- none
        // of it lives on the base weenie. So at kill time we snapshot the monster's finished
        // ObjDesc and stash it on the egg (a compact string in the unused DateOfBirth slot --
        // an item never uses or shows it), then replay it on the hatched pet. This is what
        // makes shadows/pirates hatch wearing their clothes instead of naked.
        public const PropertyString PetObjDescProp = PropertyString.DateOfBirth;

        // deserialized snapshot of the imprinted creature's finished look; set in Init when
        // the pet hatches (if the egg carries one). When non-null, CalculateObjDesc replays it.
        protected ACE.Entity.ObjDesc capturedObjDesc;

        // the hatched creature's real (full) scale, remembered so in-place level-ups size correctly
        private float petFullScale = 1.0f;

        // ---- player command modes ------------------------------------------
        // A summoned combat pet obeys simple chat commands (/pet attack|follow|stay|stop|come).
        // The mode is ephemeral state on the live pet (not saved on the egg) -- a fresh summon
        // always starts in Follow, the normal "tag along and grab nearby monsters" behavior.
        //   Follow : walk after the owner and auto-acquire nearby monsters (default)
        //   Stay   : hold position -- don't follow, don't proactively grab targets. Still
        //            defends itself if something attacks it.
        public enum PetMode
        {
            Follow = 0,
            Stay   = 1,
        }

        public PetMode Mode = PetMode.Follow;

        // How a combat pet chooses between casting and swinging each attack:
        //   Auto  : stock behavior -- roll the creature's normal spellcast chance (default)
        //   Magic : prefer spells -- cast whenever able, fall back to melee only when it can't
        //   Melee : never cast -- always make a physical attack
        // Ephemeral like Mode: a fresh summon resets to Auto.
        public enum PetCombatStyle
        {
            Auto  = 0,
            Magic = 1,
            Melee = 2,
        }

        public PetCombatStyle CombatStyle = PetCombatStyle.Auto;

        // A target the owner explicitly ordered via "/pet attack". While it is alive and
        // reachable the pet commits to it and the idle scan won't yank the pet onto something
        // closer. Cleared automatically when the target dies or goes out of range.
        public Creature CommandedTarget;

        // --- combat power scaling ------------------------------------------
        // A hatched pet is a scaled clone of the creature it came from. Its melee output is
        // multiplied, and incoming damage divided, by PetPowerFactor(): level / PetPowerLevelFull.
        // So at level 7 the pet is exactly as strong/tough as the parent; below 7 it is weaker,
        // and at level 10 it is stronger. Change the "7" to move where parity lands.
        public const int PetPowerLevelFull = 7;

        // XP a pet earns per monster it helped kill = that monster's own XP value
        // (XpOverride) times this multiplier. So tougher monsters level the pet faster,
        // and leveling tracks the game's own XP economy. Raise for faster leveling.
        public const float PetKillXpMultiplier = 1.0f;

        // How long after dying before the pet automatically resummons at its owner.
        private const double PetResummonDelay = 20.0;

        // How aggressively an idle pet looks for something nearby to attack (squared meters).
        private const float PetAcquireRangeSq = 25f * 25f;

        // How far from the OWNER a pet will roam to pick a fight. Keeps it near you instead of
        // bolting across the zone after a distant mob. (Applies to auto-acquire only -- an
        // explicit "/pet attack" order ignores the leash.)
        public const float PetLeashFromOwner = 30f;
        private const float PetLeashFromOwnerSq = PetLeashFromOwner * PetLeashFromOwner;

        // If an idle pet ends up further than this from its owner (or on a different
        // landblock, e.g. after a portal/recall), it is warped back to the owner instead of
        // walking. Kept generous so ordinary following is walked, not teleported.
        public const float PetWarpDistance = 25f;

        // Cumulative XP required to BE at each pet level (index = level; level 1 = 0).
        // Per-level costs: L2 1k, L3 +2k, L4 +10k, L5 +100k, L6 +500k, L7 +1M, L8 +5M,
        // L9 +10M, L10 +50M. Tune the whole progression here.
        private static readonly long[] PetXpThresholds =
        {
            0,            // index 0 (unused)
            0,            // level 1
            1_000,        // level 2 (hatch)
            3_000,        // level 3
            13_000,       // level 4
            113_000,      // level 5
            613_000,      // level 6
            1_613_000,    // level 7
            6_613_000,    // level 8
            16_613_000,   // level 9
            66_613_000,   // level 10
        };

        /// <summary>Pet level (1..MaxPetLevel) for a given total XP, using the curve above.</summary>
        public static int GetPetLevel(long totalXp)
        {
            var level = 1;
            for (var l = MaxPetLevel; l >= 1; l--)
            {
                if (totalXp >= PetXpThresholds[l])
                {
                    level = l;
                    break;
                }
            }
            return level;
        }

        public static int GetPetLevel(WorldObject egg)
        {
            return GetPetLevel(egg?.GetProperty(PropertyInt64.ItemTotalXp) ?? 0);
        }

        /// <summary>Total XP still needed to reach the next level (0 if already max).</summary>
        public static long XpToNextLevel(long totalXp)
        {
            var level = GetPetLevel(totalXp);
            if (level >= MaxPetLevel)
                return 0;
            return PetXpThresholds[level + 1] - totalXp;
        }

        /// <summary>
        /// Visual scale for a level. The pet grows toward the source creature's real
        /// size: fullScale/10 per level, so level 10 == the creature's own DefaultScale
        /// (e.g. a giant with DefaultScale 5.0 reaches 5.0 at level 10, not 1.0).
        /// </summary>
        public static float ScaleForLevel(int level, float fullScale)
        {
            if (fullScale <= 0f) fullScale = 1.0f;
            var scale = fullScale * level / MaxPetLevel;
            if (scale < 0.01f) scale = 0.01f;
            return scale;
        }

        /// <summary>
        /// When a creature dies, every combat pet that dealt damage to it earns flat XP toward
        /// its own level. This is the pet's ONLY normal XP source -- pets level by fighting,
        /// not from the owner's kills. Called from Creature.Die for non-player, non-pet deaths.
        /// </summary>
        public static void GrantKillXpToPetDamagers(Creature victim)
        {
            if (victim?.DamageHistory?.TotalDamage == null)
                return;

            var credited = new HashSet<uint>();

            foreach (var kvp in victim.DamageHistory.TotalDamage)
            {
                if (!(kvp.Value.TryGetAttacker() is CombatPet pet))
                    continue;

                var owner = pet.P_PetOwner;
                if (owner == null || !credited.Add(pet.Guid.Full))
                    continue;

                var award = (long)((victim.XpOverride ?? 0) * PetKillXpMultiplier);
                if (award > 0)
                    owner.GrantPetXP(award);
            }
        }

        // Throttle for the idle target scan below. The scan raycasts (IsDirectVisible) against
        // every visible creature, so running it on every monster tick is a lag source with many
        // pets in a busy area -- ~2x/sec is plenty responsive. DO NOT REMOVE (removing it was
        // the cause of the sudden pet-lag). Any smarter acquire logic must live behind it.
        private double NextPetAcquireTime;
        public static double PetAcquireInterval = 1.0;

        /// <summary>Points the pet at a target and wakes it. Shared by the idle scan and /pet attack.</summary>
        private void EngageTarget(Creature target)
        {
            AttackTarget = target;
            if (MonsterState == State.Idle)
                MonsterState = State.Awake;
            IsAwake = true;
        }

        /// <summary>
        /// Actively scan for the nearest attackable monster nearby and lock onto it. Ignores
        /// the parent creature's (often tiny) natural awareness range, so a pet standing next
        /// to something reliably engages it. Returns true if a target was found.
        ///
        /// Respects player commands: a Stay pet never proactively acquires, and a pet given an
        /// explicit "/pet attack" target sticks with that target until it dies / leaves range
        /// instead of being pulled onto whatever is closest.
        /// </summary>
        public bool PetTryAcquireTarget()
        {
            if (PhysicsObj?.ObjMaint == null)
                return false;

            // /pet battle: stay locked on the opposing pet (ends the battle if it's gone)
            if (PetBattle.TryEngageOpponent(this))
                return true;

            // Stay: hold position, don't go looking for a fight (still defends if hit).
            if (Mode == PetMode.Stay)
                return false;

            // A commanded target takes priority while it's alive and in range -- don't let the
            // idle scan re-target something closer out from under the owner's order.
            if (CommandedTarget != null)
            {
                if (!CommandedTarget.IsDead && CommandedTarget.Attackable && IsDirectVisible(CommandedTarget))
                {
                    var cdsq = (float)PhysicsObj.get_distance_sq_to_object(CommandedTarget.PhysicsObj, true);
                    if (cdsq <= PetAcquireRangeSq)
                    {
                        EngageTarget(CommandedTarget);
                        return true;
                    }
                }
                CommandedTarget = null;   // dead or out of range -- order fulfilled/abandoned
            }

            // throttle -- do not remove (see NextPetAcquireTime above)
            var now = Time.GetUnixTime();
            if (now < NextPetAcquireTime)
                return false;
            NextPetAcquireTime = now + PetAcquireInterval;

            // What is the owner currently fighting? An idle pet prefers to pile onto the
            // owner's selected target (the "my pet helps me" feeling) over grabbing something
            // random. Only applied here, at acquire time, so it never yanks the pet off a fight.
            var ownerTargetGuid = P_PetOwner?.HealthQueryTarget ?? 0;
            var ownerPhysics = P_PetOwner?.PhysicsObj;

            // Cheap pass first: pick candidates by DISTANCE only. Raycasts (IsDirectVisible)
            // are the expensive part, so we do NOT raycast here -- we raycast just the one we
            // actually choose, below. (Raycasting every monster in range every scan, times
            // every pet, was the lag.)
            Creature nearest = null;
            var nearestSq = float.MaxValue;
            Creature ownerTarget = null;

            foreach (var c in PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature())
            {
                if (c == null || c.IsDead || !c.Attackable)
                    continue;
                if (c is Player || c is Pet)
                    continue;

                var dsq = (float)PhysicsObj.get_distance_sq_to_object(c.PhysicsObj, true);
                if (dsq > PetAcquireRangeSq)
                    continue;

                // owner leash: don't wander off to grab something far from the owner
                if (ownerPhysics != null)
                {
                    var ownerDsq = (float)ownerPhysics.get_distance_sq_to_object(c.PhysicsObj, true);
                    if (ownerDsq > PetLeashFromOwnerSq)
                        continue;
                }

                // is this the monster the owner is fighting?
                if (ownerTargetGuid != 0 && c.Guid.Full == ownerTargetGuid)
                    ownerTarget = c;

                if (dsq < nearestSq)
                {
                    nearestSq = dsq;
                    nearest = c;
                }
            }

            // priority: the owner's current target, else the nearest valid monster.
            // Raycast ONLY the pick (at most two checks), not every candidate.
            if (ownerTarget != null && IsDirectVisible(ownerTarget))
            {
                EngageTarget(ownerTarget);
                return true;
            }
            if (nearest != null && IsDirectVisible(nearest))
            {
                EngageTarget(nearest);
                return true;
            }
            return false;
        }

        // ---- player commands (called from PetCommands.cs) ------------------
        // These only touch the pet's own state; the tick path (Monster_Tick +
        // PetTryAcquireTarget) reads Mode / CommandedTarget and acts accordingly.

        /// <summary>"/pet attack" -- order the pet onto a specific monster. Returns a player-facing
        /// result string ("" = success message handled by caller).</summary>
        public bool CommandAttack(Creature target, out string error)
        {
            error = null;

            if (target == null)
            {
                error = "You must select a target first.";
                return false;
            }
            if (target is Player || target is Pet)
            {
                error = "Your pet won't attack that.";
                return false;
            }
            if (target.IsDead || !target.Attackable)
            {
                error = "That target can't be attacked.";
                return false;
            }

            Mode = PetMode.Follow;      // so it will chase the target and resume following after
            CommandedTarget = target;
            EngageTarget(target);
            return true;
        }

        /// <summary>"/pet stay" -- hold position: stop following and stop grabbing targets.</summary>
        public void CommandStay()
        {
            Mode = PetMode.Stay;
            CommandedTarget = null;
            AttackTarget = null;
            CurrentAttackType = null;
            IsMoving = false;
        }

        /// <summary>"/pet follow" -- resume the default tag-along + auto-acquire behavior.</summary>
        public void CommandFollow()
        {
            Mode = PetMode.Follow;
        }

        /// <summary>"/pet stop" -- disengage the current fight but keep following the owner.</summary>
        public void CommandStop()
        {
            Mode = PetMode.Follow;
            CommandedTarget = null;
            AttackTarget = null;
            CurrentAttackType = null;
        }

        /// <summary>"/pet come" -- return to the owner immediately.</summary>
        public void CommandCome(Player owner)
        {
            Mode = PetMode.Follow;
            PetWarpToOwner(owner);
        }

        /// <summary>"/pet magic | melee | auto" -- bias how a combat pet picks its attacks.</summary>
        public void CommandCombatStyle(PetCombatStyle style)
        {
            CombatStyle = style;
            CurrentAttackType = null;   // re-pick on the next swing so the change takes effect now
        }

        /// <summary>"/pet status" -- a one-line readout for the owner.</summary>
        public string CommandStatus()
        {
            var level = P_PetDevice != null ? GetPetLevel(P_PetDevice) : GetPetLevel(0);
            var modeText = Mode == PetMode.Stay ? "staying" : "following";
            var styleText = CombatStyle == PetCombatStyle.Magic ? ", magic-focused"
                          : CombatStyle == PetCombatStyle.Melee ? ", melee-only" : "";
            var targetText = AttackTarget is Creature t && !t.IsDead ? $", attacking {t.Name}" : "";
            var rating = P_PetDevice != null ? GetPetRatingType(P_PetDevice) : PetRatingType.None;
            var ratingText = rating != PetRatingType.None ? $" | grants +{level} {PetRatingName(rating)}" : "";
            return $"Level {level}/{MaxPetLevel}, {modeText}{styleText}{targetText}{ratingText}.";
        }

        /// <summary>
        /// Instantly return the pet to its owner (used for portals/recalls and when an idle
        /// pet drifts too far). Rebuilds it at the owner via the egg, which also lands it on
        /// the owner's current landblock -- the one reliable cross-landblock reposition.
        /// </summary>
        public void PetWarpToOwner(Player owner)
        {
            var egg = P_PetDevice;
            var shellWcid = egg?.PetClass != null ? (uint)egg.PetClass.Value : 0u;
            if (owner == null || egg == null || shellWcid == 0)
                return;

            if (owner.CurrentActivePet == this)
                owner.CurrentActivePet = null;

            var chain = new ActionChain();
            chain.AddAction(owner, () =>
            {
                if (owner.CurrentActivePet != null) return;
                if (owner.PhysicsObj == null) return;
                if (owner.GetInventoryItem(egg.Guid.Full) == null) return;

                egg.SummonCreature(owner, shellWcid);
            });
            chain.EnqueueChain();

            Destroy();
        }

        /// <summary>
        /// A pet removed because its LANDBLOCK unloaded -- left behind on a landblock the owner
        /// isn't standing on, or a quest/portal/instance transition -- would otherwise vanish for
        /// good, because plain Destroy() doesn't run Die()'s auto-resummon. If the owner is still
        /// in-world and holding the egg, bring the pet back at the owner. Follow pets only: a
        /// "/pet stay" pet is meant to hold its spot, so it is NOT dragged to the owner (it just
        /// despawns with its landblock, as before). Called from WorldObject.Destroy when
        /// fromLandblockUnload is true, BEFORE CurrentActivePet is cleared.
        /// </summary>
        public void TrySummonAfterLandblockUnload()
        {
            var owner = P_PetOwner;
            var egg = P_PetDevice;

            // already replaced (a teleport re-summon repointed it) or dismissed/dead -> nothing to do
            if (owner?.CurrentActivePet != this)
                return;
            if (owner.PhysicsObj == null || egg?.PetClass == null)
                return;
            if (Mode == PetMode.Stay)   // a staying pet holds its spot; don't teleport it to the owner
                return;
            if (owner.GetInventoryItem(egg.Guid.Full) == null)
                return;

            var shellWcid = (uint)egg.PetClass;

            var chain = new ActionChain();
            chain.AddDelaySeconds(1.0);   // let the unload settle before respawning at the owner
            chain.AddAction(owner, () =>
            {
                if (owner.CurrentActivePet != null) return;   // don't stack a second pet
                if (owner.PhysicsObj == null) return;
                if (owner.GetInventoryItem(egg.Guid.Full) == null) return;

                egg.SummonCreature(owner, shellWcid);
            });
            chain.EnqueueChain();
        }

        // ---- owner rating buff ---------------------------------------------
        // Every pet grants ONE rating to its owner while summoned. Which rating is
        // fixed per pet, and the AMOUNT scales with the pet's level: +1 at level 1
        // up to +10 at level 10. The bonus is read live by Creature.GetPetRatingBonus
        // from inside the matching rating getter, so it applies while the pet is out
        // and disappears the moment it's dismissed / the owner logs off.

        /// <summary>The five ratings a pet can grant. Maps 1:1 to the getters in Creature_Rating.cs.</summary>
        public enum PetRatingType
        {
            None                = 0,
            Damage              = 1,  // GetDamageRating
            DamageReduction     = 2,  // GetDamageResistRating
            CritChance          = 3,  // GetCritRating
            CritDamage          = 4,  // GetCritDamageRating
            CritDamageReduction = 5,  // GetCritDamageResistRating
        }

        /// <summary>
        /// Which rating this egg's pet grants. Derived from the egg's stable GUID (a
        /// multiplicative hash spread across the five types), so it's fixed per egg and
        /// well varied across eggs -- no stored property or DB column needed.
        /// </summary>
        public static PetRatingType GetPetRatingType(WorldObject egg)
        {
            if (egg == null)
                return PetRatingType.None;

            var h = egg.Guid.Full * 2654435761u;      // Knuth multiplicative hash
            return (PetRatingType)((int)(h % 5) + 1); // 1..5
        }

        /// <summary>
        /// The bonus this pet currently grants its owner for the given rating: the pet's
        /// level (1..10) if it's this pet's rating, otherwise 0.
        /// </summary>
        public int GetOwnerRatingBonus(PetRatingType type)
        {
            if (P_PetDevice == null || type == PetRatingType.None)
                return 0;

            if (GetPetRatingType(P_PetDevice) != type)
                return 0;

            return GetPetLevel(P_PetDevice);   // 1..10
        }

        /// <summary>Friendly name for a pet's rating, shown on the egg's ID panel.</summary>
        public static string PetRatingName(PetRatingType type)
        {
            switch (type)
            {
                case PetRatingType.Damage:              return "Damage Rating";
                case PetRatingType.DamageReduction:     return "Damage Reduction Rating";
                case PetRatingType.CritChance:          return "Critical Chance Rating";
                case PetRatingType.CritDamage:          return "Critical Damage Rating";
                case PetRatingType.CritDamageReduction: return "Critical Damage Reduction Rating";
                default:                                return "Rating";
            }
        }

        /// <summary>
        /// Rebuilds the egg's appraisal (ID-panel) description so the owner can see, in
        /// inventory, what buff the pet grants: the rating and its current amount (= level,
        /// rising to +10 at level 10), plus the learned vuln once it has one. Uses the same
        /// LongDesc field the learned-spell line used. Only writes/saves when it changes.
        /// </summary>
        public static void UpdatePetDeviceDescription(PetDevice egg)
        {
            if (egg == null)
                return;

            var totalXp = egg.GetProperty(PropertyInt64.ItemTotalXp) ?? 0;
            var level = GetPetLevel(totalXp);
            var rating = GetPetRatingType(egg);

            var desc = $"Pet level {level}/{MaxPetLevel}. ";
            var toNext = XpToNextLevel(totalXp);
            if (toNext > 0)
                desc += $"{toNext:N0} XP to next level. ";
            desc += $"While summoned, grants its owner +{level} {PetRatingName(rating)}.";

            if (egg.GetProperty(PropertyBool.ZooGiftPet) == true)
                desc += "\n\nA gift from the Pet Zoo. It can never be donated to the zoo.";

            if (egg.GetProperty(PropertyString.LongDesc) != desc)
            {
                egg.SetProperty(PropertyString.LongDesc, desc);
                egg.SaveBiotaToDatabase();
            }
        }

        /// <summary>
        /// Ensures the egg tracks pet XP. The pet uses a custom, non-linear level curve
        /// (see PetXpThresholds) that the native "Fixed" item-level display can't represent,
        /// so we clear those native fields (level/XP is shown in the egg's description text
        /// instead) and start a fresh egg at 0 XP (level 1). Safe to call repeatedly.
        /// </summary>
        public static void EnsurePetXpDisplay(WorldObject egg)
        {
            if (egg == null)
                return;

            // turn off the native linear level bar -- it would show a wrong level for our curve
            egg.ItemBaseXp = null;
            egg.ItemMaxLevel = null;
            egg.ItemXpStyle = null;

            // fresh egg starts at Level 1 (0 XP)
            if (egg.GetProperty(PropertyInt64.ItemTotalXp) == null)
                egg.SetProperty(PropertyInt64.ItemTotalXp, 0);

            // each egg is a unique creature -- never let eggs stack/merge (which would
            // discard the per-egg imprint: name, source WCID, xp).
            egg.SetProperty(PropertyInt.MaxStackSize, 1);
            egg.SetProperty(PropertyInt.StackSize, 1);
        }

        /// <summary>
        /// Stamps a killed creature's identity onto an egg it dropped: the source WCID
        /// (so it knows what to hatch into), a matching name/description, and a snapshot
        /// of its finished appearance (so it hatches wearing its clothes). Called at drop.
        /// </summary>
        public static void ImprintCreature(WorldObject egg, Creature source)
        {
            if (egg == null || source == null)
                return;

            egg.SetProperty(SourceWcidProp, source.WeenieClassId);

            // Pre-hatch identity on the egg device: "<Creature>'s Egg" plus a matching
            // ID-panel line (PropertyString.Use, the field the assess panel reads for the
            // "Use this ..." text). Both are rewritten to the hatched wording at level 2
            // (see Init). If the creature has no name, we leave the egg's neutral defaults.
            if (!string.IsNullOrEmpty(source.Name))
            {
                egg.Name = source.Name + "'s Egg";
                egg.SetProperty(PropertyString.Use,
                    $"Use this Egg to summon or dismiss your unhatched {source.Name}");
            }

            // Snapshot the killed creature's fully-assembled appearance (its finished
            // ObjDesc, with equipped clothing/armor + palette baked in) and stash it on the
            // egg, so a clothed humanoid (shadows, pirates) hatches dressed instead of naked.
            // Harmless for plain creatures -- it just captures their normal look.
            var blob = SerializeObjDesc(source.CalculateObjDesc());
            if (!string.IsNullOrEmpty(blob))
                egg.SetProperty(PetObjDescProp, blob);
        }

        /// <summary>Copies the imprinted creature's visual DIDs (setup, motion, sound,
        /// physics, clothing, palette) from its weenie onto this pet, so the hatched pet
        /// looks like that creature.</summary>
        protected void ApplyCreatureAppearance(ACE.Entity.Models.Weenie w)
        {
            if (w == null)
                return;

            CopyDid(w, PropertyDataId.Setup);               // DID 1  - body model
            CopyDid(w, PropertyDataId.MotionTable);         // DID 2  - animations
            CopyDid(w, PropertyDataId.SoundTable);          // DID 3  - sounds
            CopyDid(w, PropertyDataId.PhysicsEffectTable);  // physics
            CopyDid(w, PropertyDataId.ClothingBase);        // DID 7  - clothing/texture
            CopyDid(w, PropertyDataId.PaletteBase);         // base palette

            var palTemplate = w.GetProperty(PropertyInt.PaletteTemplate);
            if (palTemplate != null) SetProperty(PropertyInt.PaletteTemplate, palTemplate.Value);   // INT 3 - palette/color

            var shade = w.GetProperty(PropertyFloat.Shade);
            if (shade != null) SetProperty(PropertyFloat.Shade, shade.Value);

            // translucency (Float 76) - reproduces ghostly / see-through creatures
            var translucency = w.GetProperty(PropertyFloat.Translucency);
            if (translucency != null) SetProperty(PropertyFloat.Translucency, translucency.Value);

            // species shown on the pet's assess panel (CreatureType = INT 2). The pet is
            // spawned from the shell weenie, so without this it keeps the shell's type
            // (e.g. always reading "armoredillo"). Stamp the imprinted creature's type.
            var creatureType = w.GetProperty(PropertyInt.CreatureType);
            if (creatureType != null) SetProperty(PropertyInt.CreatureType, creatureType.Value);
        }

        private void CopyDid(ACE.Entity.Models.Weenie w, PropertyDataId prop)
        {
            var v = w.GetProperty(prop);
            if (v != null && v != 0)
                SetProperty(prop, v.Value);
        }

        // ---- captured-appearance (ObjDesc) storage --------------------------
        // The egg stores the killed creature's finished look as one compact string:
        //   PD1 ~ paletteId ~ subPalettes ~ textureChanges ~ animPartChanges
        // records within a section are '|'-separated, fields ','-separated. Numbers
        // only, so no separator ever appears inside a value.

        public static string SerializeObjDesc(ACE.Entity.ObjDesc od)
        {
            if (od == null)
                return null;

            var subs = new List<string>();
            foreach (var p in od.SubPalettes)
                subs.Add($"{p.SubPaletteId},{p.Offset},{p.Length}");

            var texs = new List<string>();
            foreach (var t in od.TextureChanges)
                texs.Add($"{t.PartIndex},{t.OldTexture},{t.NewTexture}");

            var anims = new List<string>();
            foreach (var a in od.AnimPartChanges)
                anims.Add($"{a.Index},{a.AnimationId}");

            return "PD1~" + od.PaletteID + "~"
                 + string.Join("|", subs) + "~"
                 + string.Join("|", texs) + "~"
                 + string.Join("|", anims);
        }

        public static ACE.Entity.ObjDesc DeserializeObjDesc(string s)
        {
            if (string.IsNullOrEmpty(s))
                return null;

            var sections = s.Split('~');
            if (sections.Length < 5 || sections[0] != "PD1")
                return null;

            var od = new ACE.Entity.ObjDesc();

            if (uint.TryParse(sections[1], out var pid))
                od.PaletteID = pid;

            foreach (var rec in sections[2].Split('|'))
            {
                if (rec.Length == 0) continue;
                var f = rec.Split(',');
                if (f.Length != 3) continue;
                od.SubPalettes.Add(new PropertiesPalette
                {
                    SubPaletteId = uint.Parse(f[0]),
                    Offset = ushort.Parse(f[1]),
                    Length = ushort.Parse(f[2])
                });
            }

            foreach (var rec in sections[3].Split('|'))
            {
                if (rec.Length == 0) continue;
                var f = rec.Split(',');
                if (f.Length != 3) continue;
                od.TextureChanges.Add(new PropertiesTextureMap
                {
                    PartIndex = byte.Parse(f[0]),
                    OldTexture = uint.Parse(f[1]),
                    NewTexture = uint.Parse(f[2])
                });
            }

            foreach (var rec in sections[4].Split('|'))
            {
                if (rec.Length == 0) continue;
                var f = rec.Split(',');
                if (f.Length != 2) continue;
                od.AnimPartChanges.Add(new PropertiesAnimPart
                {
                    Index = byte.Parse(f[0]),
                    AnimationId = uint.Parse(f[1])
                });
            }

            return od;
        }

        /// <summary>
        /// A hatched pet replays the exact finished look snapshotted from the killed
        /// creature (clothes + palette baked in). Falls back to normal behavior for the
        /// egg-look follower and for eggs that carry no snapshot.
        /// </summary>
        public override ACE.Entity.ObjDesc CalculateObjDesc()
        {
            if (capturedObjDesc != null)
                return capturedObjDesc;

            return base.CalculateObjDesc();
        }

        /// <summary>
        /// Called when the pet's egg crosses into a new level (from kill XP while the
        /// pet is summoned). Grows the live pet and, at level 2, hatches the egg icon.
        /// The size is also re-applied every time the pet is summoned, so this is just
        /// the "grow in place" moment; if the client doesn't resize live, stowing and
        /// re-summoning the pet will show the new size.
        /// </summary>
        public void OnPetLevelUp(Player owner, int newLevel)
        {
            if (newLevel >= MaxPetLevel)
            {
                // The pet's Name is "<owner>'s <creature>", so trim the owner off here or the
                // line reads "Your Bob's Drudge Skulker ..." instead of "Your Drudge Skulker ...".
                var petName = Name ?? "pet";
                var ownerPrefix = owner.Name + "'s ";
                if (petName.StartsWith(ownerPrefix))
                    petName = petName.Substring(ownerPrefix.Length);

                owner.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Your {petName} has reached its max level!", ChatMessageType.Broadcast));
            }

            // keep the egg's ID-panel description current: the rating amount rises each level.
            if (P_PetDevice != null)
                UpdatePetDeviceDescription(P_PetDevice);

            // At hatch level the egg-look follower must become the fighting creature clone,
            // so re-summon it (SummonCreature picks Pet vs CombatPet by the egg's level).
            var wearingEggLook = P_PetDevice != null && SetupTableId == P_PetDevice.SetupTableId;

            if (P_PetDevice?.PetClass != null && newLevel >= HatchLevel && wearingEggLook)
            {
                var device = P_PetDevice;
                var wcid = (uint)device.PetClass;

                if (wearingEggLook)
                    owner.Session.Network.EnqueueSend(
                        new GameMessageSystemChat("Your egg hatches!", ChatMessageType.Broadcast));

                Destroy();
                owner.CurrentActivePet = null;
                device.SummonCreature(owner, wcid);   // re-spawn as creature / combat pet per egg state

                // color-burst level-up effect on the freshly (re)spawned pet
                owner.CurrentActivePet?.PlayParticleEffect(PlayScript.LevelUp, owner.CurrentActivePet.Guid);
                return;
            }

            // keep the world assess panel in sync as the pet grows
            Level = newLevel;
            EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(this, PropertyInt.Level, newLevel));

            // rescale attributes + skills for the new level (rebuilds vital maxima + heals)
            ApplyPetStatScaling(newLevel);

            ObjScale = ScaleForLevel(newLevel, petFullScale);

            // best-effort live resize
            EnqueueBroadcast(new GameMessagePublicUpdatePropertyFloat(this, PropertyFloat.DefaultScale, ObjScale ?? 1.0f));

            // color-burst level-up effect (same PlayScript the player uses)
            PlayParticleEffect(PlayScript.LevelUp, Guid);

            owner.Session.Network.EnqueueSend(
                new GameMessageSystemChat($"{Name} grew to level {newLevel}!", ChatMessageType.Broadcast));
        }

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Pet(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Pet(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            Ethereal = true;
            RadarBehavior = ACE.Entity.Enum.RadarBehavior.ShowNever;
            ItemUseable = Usable.No;

            SuppressGenerateEffect = true;
        }

        /// <summary>
        /// A pet's combat power for its current level: level / PetPowerLevelFull. Melee output
        /// is multiplied by this, incoming damage divided by it, so the pet matches the parent
        /// creature at PetPowerLevelFull (default 7), is weaker below, and stronger at 10.
        /// </summary>
        public float PetPowerFactor()
        {
            var level = 1;
            if (P_PetDevice != null)
                level = GetPetLevel(P_PetDevice);

            var f = level / (float)PetPowerLevelFull;
            if (f < 0.05f) f = 0.05f;   // guard against divide-by-near-zero on incoming damage
            return f;
        }

        // ---- level-based attribute & skill scaling -------------------------
        // An imprinted pet is a clone of its parent creature, built from that creature's OWN
        // weenie (see PetDevice.SummonCreature), so at summon its attributes and skills are the
        // parent's NATIVE values. We scale both by level / PetPowerLevelFull: ~1/7 of native at
        // level 1, EXACTLY native at level 7 (parity with the parent), and above native at 8-10.
        // Scaling attributes scales melee/missile damage, health/stamina/mana, and attack/defense
        // naturally; scaling skills scales cast reliability + defenses too.
        //
        // Each summon builds a FRESH clone (= native, unscaled), so we snapshot the native bases
        // the first time we scale and rescale from that snapshot on in-place level-ups -- never
        // from an already-scaled value.

        private static readonly PropertyAttribute[] PetScaledAttributes =
        {
            PropertyAttribute.Strength, PropertyAttribute.Endurance, PropertyAttribute.Coordination,
            PropertyAttribute.Quickness, PropertyAttribute.Focus, PropertyAttribute.Self,
        };

        private readonly Dictionary<PropertyAttribute, uint> nativeAttrStart = new Dictionary<PropertyAttribute, uint>();
        private readonly Dictionary<Skill, uint> nativeSkillInit = new Dictionary<Skill, uint>();
        private bool petStatsCaptured;

        // Optional: also scale each vital's FLAT innate bonus (Health/Stamina/Mana StartingValue).
        // Attribute-driven vital growth already scales via Endurance/Self, so this only matters for
        // monsters given a big flat hp/stam/mana bonus directly on the weenie. OFF by default to
        // keep scope to attributes + skills; flip to true if a creature's pet is too tanky early
        // (or not tanky enough) at low levels.
        private const bool ScalePetVitals = false;
        private uint nativeHealthStart, nativeStaminaStart, nativeManaStart;

        /// <summary>
        /// Records the parent creature's native attribute/skill bases the first time scaling runs
        /// on a freshly-summoned clone, so every rescale works from the true native values.
        /// </summary>
        private void CaptureNativePetStats()
        {
            if (petStatsCaptured) return;
            petStatsCaptured = true;

            foreach (var attr in PetScaledAttributes)
                nativeAttrStart[attr] = Attributes[attr].StartingValue;

            foreach (var kvp in Biota.PropertiesSkill)
                nativeSkillInit[kvp.Key] = kvp.Value.InitLevel;

            if (ScalePetVitals)
            {
                nativeHealthStart  = Health.StartingValue;
                nativeStaminaStart = Stamina.StartingValue;
                nativeManaStart    = Mana.StartingValue;
            }
        }

        /// <summary>
        /// Scales the pet's attributes and skill InitLevels to (level / PetPowerLevelFull) of the
        /// captured native values, then refreshes Health/Stamina/Mana to the new maxima. Only
        /// applies to imprinted CombatPets (the ones built from the parent creature's weenie);
        /// legacy shell pets are left alone.
        /// </summary>
        public void ApplyPetStatScaling(int level)
        {
            if (!(this is CombatPet)) return;   // only imprinted clones carry the parent's stats

            CaptureNativePetStats();

            var k = level / (float)PetPowerLevelFull;
            if (k < 0.01f) k = 0.01f;

            // Attributes: for a monster Ranks == 0, so Base == StartingValue == Current base;
            // setting StartingValue scales the attribute (and everything derived from it).
            foreach (var attr in PetScaledAttributes)
            {
                if (!nativeAttrStart.TryGetValue(attr, out var native)) continue;
                var scaled = (uint)Math.Round(native * k);
                if (scaled < 1) scaled = 1;   // attributes floor at 1
                Attributes[attr].StartingValue = scaled;
            }

            // Skills: Base = AttributeFormula(attributes) + InitLevel + Ranks. The attribute
            // portion already scaled via the attributes above, so we scale the flat InitLevel by
            // the same factor -- the whole skill Base then lands at k x native.
            foreach (var kvp in nativeSkillInit)
                GetCreatureSkill(kvp.Key).InitLevel = (uint)Math.Round(kvp.Value * k);

            if (ScalePetVitals)
            {
                Health.StartingValue  = (uint)Math.Round(nativeHealthStart  * k);
                Stamina.StartingValue = (uint)Math.Round(nativeStaminaStart * k);
                Mana.StartingValue    = (uint)Math.Round(nativeManaStart    * k);
            }

            // recompute the vital maxima from the new attributes and refill (a level-up also heals)
            SetMaxVitals();
        }

        // ---- "/pet damage" combat readout ----------------------------------
        // Owners can toggle a chat line for every hit their pet lands (melee + spells), like the
        // EoR summoner "/pet damage" command. The toggle is session-scoped (a set of owner GUIDs),
        // so it clears on relog / server restart -- no DB column, no Player-file edit. The two
        // damage sites (Monster_Melee for melee, SpellProjectile for spells) each call
        // TryReportPetDamage, which is a no-op unless the pet's owner has it switched on.
        private static readonly HashSet<uint> PetDamageMsgOwners = new HashSet<uint>();

        /// <summary>Flip "/pet damage" for this owner. Returns the new state (true = on).</summary>
        public static bool TogglePetDamageMessages(Player owner)
        {
            if (owner == null)
                return false;

            if (PetDamageMsgOwners.Contains(owner.Guid.Full))
            {
                PetDamageMsgOwners.Remove(owner.Guid.Full);
                return false;
            }

            PetDamageMsgOwners.Add(owner.Guid.Full);
            return true;
        }

        public static bool PetDamageMessagesOn(Player owner)
            => owner != null && PetDamageMsgOwners.Contains(owner.Guid.Full);

        /// <summary>
        /// If the attacker is a pet whose owner has "/pet damage" on, shows the owner a combat
        /// line for the hit. Safe to call for any attacker/amount -- it self-filters.
        /// </summary>
        public static void TryReportPetDamage(WorldObject attacker, Creature target, float amount, DamageType damageType, bool critical = false, string via = null)
        {
            if (!(attacker is Pet pet) || target == null || amount <= 0)
                return;

            var owner = pet.P_PetOwner;
            if (owner?.Session == null || !PetDamageMsgOwners.Contains(owner.Guid.Full))
                return;

            // trim the "<owner>'s " prefix so it reads "Fluffy hits ..." not "Bob's Fluffy hits ..."
            var petName = pet.Name ?? "Your pet";
            var prefix = owner.Name + "'s ";
            if (petName.StartsWith(prefix))
                petName = petName.Substring(prefix.Length);

            var critMsg = critical ? "Critical hit! " : "";
            var viaMsg = string.IsNullOrEmpty(via) ? "" : $" with {via}";

            owner.Session.Network.EnqueueSend(new GameMessageSystemChat(
                $"{critMsg}{petName} hits {target.Name} for {(int)Math.Round(amount)} points of {damageType.GetName().ToLower()} damage{viaMsg}.",
                ChatMessageType.CombatSelf));
        }

        /// <summary>
        /// A pet dying grants no kill credit or XP to whatever killed it -- skip the normal
        /// creature death economy (kill tasks, XP grant) entirely.
        /// </summary>
        public override DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)
        {
            return GetDeathMessage(lastDamager, damageType, criticalHit);
        }

        /// <summary>
        /// A pet's death: tell the owner, play the death, and schedule an automatic resummon
        /// at the owner after PetResummonDelay seconds. No corpse, no loot, no level loss --
        /// the egg (which holds the pet's level/XP) stays in inventory, so it comes back the
        /// same. This replaces the full Creature death sequence for pets.
        /// </summary>
        private bool petDieHandled;

        protected override void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            if (petDieHandled) return;
            petDieHandled = true;

            // /pet battle: this pet lost
            PetBattle.OnPetDefeated(this);

            var owner = P_PetOwner;
            var egg = P_PetDevice;
            var shellWcid = egg?.PetClass != null ? (uint)egg.PetClass.Value : 0u;

            if (owner != null)
            {
                var petName = Name ?? "pet";
                var ownerPrefix = owner.Name + "'s ";
                if (petName.StartsWith(ownerPrefix))
                    petName = petName.Substring(ownerPrefix.Length);

                owner.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Your {petName} was defeated! It will return in {(int)PetResummonDelay} seconds.",
                    ChatMessageType.Broadcast));
            }

            if (owner?.CurrentActivePet == this)
                owner.CurrentActivePet = null;

            // schedule the resummon on the owner (survives the pet being destroyed)
            if (owner != null && egg != null && shellWcid != 0)
            {
                var chain = new ActionChain();
                chain.AddDelaySeconds(PetResummonDelay);
                chain.AddAction(owner, () =>
                {
                    // only resummon if the owner is still in-world with no pet already out,
                    // and still holding this egg
                    if (owner.CurrentActivePet != null) return;
                    if (owner.PhysicsObj == null) return;
                    if (owner.GetInventoryItem(egg.Guid.Full) == null) return;

                    egg.SummonCreature(owner, shellWcid);
                });
                chain.EnqueueChain();
            }

            Destroy();
        }

        public virtual bool? Init(Player player, PetDevice petDevice)
        {
            var result = HandleCurrentActivePet(player);

            if (result == null || !result.Value)
                return result;

            // get physics radius of player and pet
            var playerRadius = player.PhysicsObj.GetPhysicsRadius();
            var petRadius = GetPetRadius();

            var spawnDist = playerRadius + petRadius + MinDistance;

            if (IsPassivePet)
            {
                Location = player.Location.InFrontOf(spawnDist, true);

                TimeToRot = -1;
            }
            else
            {
                Location = player.Location.InFrontOf(spawnDist, false);

                // combat pets must NOT rot: a dynamic-guid creature with no generator and no
                // TimeToRot is decayable, so Decay() would delete it after DefaultTimeToRot
                // (5 min) -- the "pet vanished while I stood around / whole group at once" bug.
                TimeToRot = -1;
            }

            Location.LandblockId = new LandblockId(Location.GetCell());

            Name = player.Name + "'s " + Name;

            PetOwner = player.Guid.Full;
            P_PetOwner = player;

            // All pets don't leave corpses, this maybe should have been in data, but isn't so lets make sure its true.
            NoCorpse = true;

            // ===== creature pet system (prototype) ============================
            // Make sure the egg carries its level/XP display (and level-1 seed).
            EnsurePetXpDisplay(petDevice);

            // keep the egg's ID-panel description current (rating buff + amount, + spell if learned)
            UpdatePetDeviceDescription(petDevice);

            var petLevel = GetPetLevel(petDevice);

            // show the real pet level (1-10) on the world assess panel instead of the shell weenie's level
            // (also re-applied after EnterWorld below so it definitely sticks)
            Level = petLevel;

            // Which creature is this egg imprinted with? Fall back to PetClass (the shell)
            // for old/generic eggs made before imprinting existed.
            uint? creatureWcid = petDevice.GetProperty(SourceWcidProp);
            if ((creatureWcid ?? 0) == 0 && petDevice.PetClass != null)
                creatureWcid = (uint)petDevice.PetClass.Value;

            var creatureWeenie = (creatureWcid ?? 0) != 0
                ? DatabaseManager.World.GetCachedWeenie(creatureWcid.Value) : null;
            var creatureName = creatureWeenie?.GetProperty(PropertyString.Name);

            // Rooted creatures (trees etc.) can't fight to earn the XP that hatches an egg, so they
            // skip the egg look and summon as themselves, full size. Listed by WCID -- NOT by
            // PropertyBool.Stuck, which nearly every creature weenie has set.
            var rootedCreature = creatureWcid != null && RootedCreatureWcids.Contains(creatureWcid.Value);

            // Appearance + size must be set before EnterWorld().
            if (petLevel < HatchLevel && !rootedCreature)
            {
                // un-hatched: look like the egg (egg item's model), at the egg's own size
                if (petDevice.SetupTableId != 0)
                    SetupTableId = petDevice.SetupTableId;

                ObjScale = EggFollowerScale;
                Name = player.Name + "'s " + (string.IsNullOrEmpty(creatureName) ? "Mystery" : creatureName) + " Egg";
            }
            else
            {
                // hatched: become the imprinted creature, sized by level
                float fullScale = 1.0f;
                if (creatureWeenie != null)
                {
                    ApplyCreatureAppearance(creatureWeenie);

                    // replay the killed creature's captured finished look (clothes + palette),
                    // if the egg carries one. The DID copies above set the matching setup, so
                    // the snapshot's part/texture changes line up onto the right body.
                    capturedObjDesc = DeserializeObjDesc(petDevice.GetProperty(PetObjDescProp));

                    if (!string.IsNullOrEmpty(creatureName))
                        Name = player.Name + "'s " + creatureName;

                    // capture the creature's real full size (DefaultScale, float 39)
                    fullScale = (float)(creatureWeenie.GetProperty(PropertyFloat.DefaultScale) ?? 1.0);

                    // swap the egg's inventory icon to the creature's icon (once)
                    var icon = creatureWeenie.GetProperty(PropertyDataId.Icon);
                    if (icon != null && petDevice.GetProperty(PropertyDataId.Icon) != icon)
                    {
                        petDevice.SetProperty(PropertyDataId.Icon, icon.Value);
                        player.Session.Network.EnqueueSend(
                            new GameMessagePublicUpdatePropertyDataID(petDevice, PropertyDataId.Icon, icon.Value));
                    }

                    // hatched: the egg device becomes "<Creature> Pet" with matching ID text.
                    // Guarded so each only sends its client update once (on the transition).
                    if (!string.IsNullOrEmpty(creatureName))
                    {
                        var hatchedName = creatureName + " Pet";
                        var hatchedUse = $"Use this to summon or dismiss your {creatureName} Pet";

                        if (petDevice.Name != hatchedName)
                        {
                            petDevice.Name = hatchedName;
                            player.Session.Network.EnqueueSend(
                                new GameMessagePublicUpdatePropertyString(petDevice, PropertyString.Name, hatchedName));
                        }

                        if (petDevice.GetProperty(PropertyString.Use) != hatchedUse)
                        {
                            petDevice.SetProperty(PropertyString.Use, hatchedUse);
                            player.Session.Network.EnqueueSend(
                                new GameMessagePublicUpdatePropertyString(petDevice, PropertyString.Use, hatchedUse));
                        }
                    }
                }

                petFullScale = fullScale;
                ObjScale = rootedCreature ? fullScale : ScaleForLevel(petLevel, fullScale);
            }
            // scale attributes + skills to this level (native at 7, weaker below, stronger at 8-10).
            // Must run before EnterWorld so the pet spawns with the correct vitals. No-op for
            // legacy shell pets (guarded inside ApplyPetStatScaling).
            ApplyPetStatScaling(petLevel);
            // ===== end creature pet system ====================================

            var success = EnterWorld();

            if (!success)
            {
                player.SendTransientError($"Couldn't spawn {Name}");
                return false;
            }

            // force the pet level onto the live object after it enters the world
            // (shell weenie is L16; this is what the assess panel reads)
            Level = petLevel;
            EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(this, PropertyInt.Level, petLevel));

            // evict any stray duplicate for this owner, then take ownership (one pet per owner)
            ClaimSoleActivePet(player);
            player.CurrentActivePet = this;

            petDevice.Pet = Guid.Full;
            PetDevice = petDevice.Guid.Full;
            P_PetDevice = petDevice;

            if (IsPassivePet)
                nextSlowTickTime = Time.GetUnixTime();

            return true;
        }

        // One-pet-per-owner guard. The several resummon paths (warp/come, death, hatch, teleport,
        // landblock-unload) each bring up a pet independently, and mashing commands can race two
        // into existence -- leaving an orphaned duplicate that no longer answers to the owner. This
        // registry makes the NEWEST summon authoritative: when a pet claims its owner it destroys
        // any other still-live pet registered to that owner, so at most one ever survives. A stale
        // (already-destroyed) entry is harmless -- it's skipped and overwritten on the next claim.
        private static readonly Dictionary<uint, Pet> ActivePets = new Dictionary<uint, Pet>();

        private void ClaimSoleActivePet(Player player)
        {
            // Locked: landblocks tick on parallel threads, and an unsynchronized shared dictionary
            // touched from two of them at once can corrupt or throw inside the tick loop.
            lock (ActivePets)
            {
                if (ActivePets.TryGetValue(player.Guid.Full, out var existing)
                    && existing != null && existing != this && !existing.IsDestroyed)
                {
                    existing.Destroy();   // evict the stray duplicate (plain Destroy: no resummon)
                }

                ActivePets[player.Guid.Full] = this;
            }
        }

        public bool? HandleCurrentActivePet(Player player)
        {
            if (PropertyManager.GetBool("pet_stow_replace").Item)
                return HandleCurrentActivePet_Replace(player);
            else
                return HandleCurrentActivePet_Retail(player);
        }

        public bool HandleCurrentActivePet_Replace(Player player)
        {
            // original ace logic
            if (player.CurrentActivePet == null)
                return true;

            if (player.CurrentActivePet is CombatPet)
            {
                // possibly add the ability to stow combat pets with passive pet devices here?
                player.SendTransientError($"{player.CurrentActivePet.Name} is already active");
                return false;
            }

            var stowPet = WeenieClassId == player.CurrentActivePet.WeenieClassId;

            // despawn passive pet
            player.CurrentActivePet.Destroy();

            return !stowPet;
        }

        public bool? HandleCurrentActivePet_Retail(Player player)
        {
            if (player.CurrentActivePet == null)
                return true;

            if (IsPassivePet)
            {
                // using a passive pet device
                // stow currently active passive/combat pet, as per retail
                // spawning the new passive pet requires another double click
                player.CurrentActivePet.Destroy();
            }
            else
            {
                // using a combat pet device
                if (player.CurrentActivePet is CombatPet)
                {
                    player.SendTransientError($"{player.CurrentActivePet.Name} is already active");
                }
                else
                {
                    // stow currently active passive pet
                    // stowing the currently active passive pet w/ a combat pet device will unfortunately start the cooldown timer (and decrease the structure?) on the combat pet device, as per retail
                    // spawning the combat pet will require another double click in ~45s, as per retail
                    player.CurrentActivePet.Destroy();

                    return null;
                }
            }
            return false;
        }

        /// <summary>
        /// Called 5x per second for passive pets
        /// </summary>
        public void Tick(double currentUnixTime)
        {
            // A CombatPet (learned a vuln at level 10) IS a monster, so the landblock calls
            // Monster_Tick on it directly -- that runs the find-target + cast AI -- and
            // Monster_Tick calls back into THIS Tick for the pet's per-frame physics. So the
            // combat branch does ONLY physics + the nearby-monster scan. It must NEVER call
            // Monster_Tick itself: Monster_Tick already calls us, so calling it back makes the
            // two recurse until the stack overflows (the level-10 crash). Cosmetic pets fall
            // through to the passive follow logic below.
            if (this is CombatPet combatPet)
            {
                if (IsMoving)
                {
                    PhysicsObj.update_object();

                    UpdatePosition();
                }

                if (currentUnixTime >= NextMonsterTickTime)
                {
                    NextMonsterTickTime = currentUnixTime + monsterTickInterval;

                    combatPet.PetCheckMonsters();
                }
                return;
            }

            NextMonsterTickTime = currentUnixTime + monsterTickInterval;

            if (IsMoving)
            {
                PhysicsObj.update_object();

                UpdatePosition();
            }

            if (currentUnixTime >= nextSlowTickTime)
                SlowTick(currentUnixTime);
        }

        private const double slowTickSeconds = 1.0;
        protected double nextSlowTickTime;

        /// <summary>
        /// Called 1x per second
        /// </summary>
        public void SlowTick(double currentUnixTime)
        {
            //Console.WriteLine($"{Name}.HeartbeatStatic({currentUnixTime})");

            nextSlowTickTime += slowTickSeconds;

            // pet zoo animals have no owner -- they wander instead of following
            if (this is ZooAnimal zooAnimal)
            {
                zooAnimal.ZooSlowTick(currentUnixTime);
                return;
            }

            if (P_PetOwner?.PhysicsObj == null)
            {
                if (!(this is Companion))   // joke companions: owner logged off, expected
                    log.Error($"{Name} ({Guid}).SlowTick() - P_PetOwner: {P_PetOwner}, P_PetOwner.PhysicsObj: {P_PetOwner?.PhysicsObj}");
                Destroy();
                return;
            }

            var dist = GetCylinderDistance(P_PetOwner);

            // NOTE: the old "if (dist > MaxDistance) Destroy();" was removed on purpose.
            // The pet now follows the owner through portals/recalls/teleports (re-summoned
            // at the owner in Player.OnTeleportComplete), so being far away is never a reason
            // to despawn. The only things that remove a pet are logging off (handled above,
            // when the owner's PhysicsObj goes null) and unsummoning it.

            if (!IsMoving && dist > MinDistance)
                StartFollow();
        }

        // if the passive pet is between min-max distance to owner,
        // it will turn and start running torwards its owner

        private const float MinDistance = 2.0f;
        private const float MaxDistance = 192.0f;

        private void StartFollow()
        {
            // similar to Monster_Navigation.StartTurn()

            //Console.WriteLine($"{Name}.StartFollow()");

            IsMoving = true;

            // broadcast to clients
            MoveTo(P_PetOwner);
        }

        /// <summary>
        /// Broadcasts passive pet movement to clients
        /// </summary>
        public override void MoveTo(WorldObject target, float distanceToObject = 2.0f, float speed = 1.0f, bool clientOnly = false)
        {
            if (!IsPassivePet || this is CombatPet)
            {
                base.MoveTo(target, distanceToObject, speed, clientOnly);
                return;
            }

            if (MoveSpeed == 0.0f)
                UpdateMovementSpeed();

            var motion = new Motion(this, target, MovementType.MoveToObject);

            motion.MoveToParameters.MovementParameters |= MovementParams.CanCharge;
            motion.MoveToParameters.DistanceToObject = MinDistance;
            motion.MoveToParameters.WalkRunThreshold = 0.0f;
            motion.MoveToParameters.Speed = speed;

            motion.RunRate = RunRate;

            EnqueueBroadcastMotion(motion);

            if (clientOnly)
                return;

            CurrentMotionState = motion;

            // prevent initial snap forward
            if (!PhysicsObj.IsMovingOrAnimating)
                PhysicsObj.UpdateTime = Physics.Common.PhysicsTimer.CurrentTime;

            var mvp = new MovementParameters(motion);
            PhysicsObj.MoveToObject(P_PetOwner.PhysicsObj, mvp);
        }

        /// <summary>
        /// Called when the MoveTo process has completed
        /// </summary>
        public override void OnMoveComplete(WeenieError status)
        {
            //Console.WriteLine($"{Name}.OnMoveComplete({status})");

            if (!IsPassivePet || this is CombatPet)
            {
                base.OnMoveComplete(status);
                return;
            }

            if (status != WeenieError.None)
                return;

            OnMovementStopped();
        }

        public static Dictionary<uint, float> PetRadiusCache = new Dictionary<uint, float>();

        private float GetPetRadius()
        {
            if (PetRadiusCache.TryGetValue(WeenieClassId, out var radius))
                return radius;

            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(SetupTableId);

            var scale = ObjScale ?? 1.0f;

            // not every setup has collision spheres (some use cylspheres, some have neither);
            // indexing Spheres[0] blindly threw ArgumentOutOfRangeException and aborted the summon
            float baseRadius;
            if (setup?.Spheres.Count > 0)
                baseRadius = setup.Spheres[0].Radius;
            else if (setup?.CylSpheres.Count > 0)
                baseRadius = setup.CylSpheres[0].Radius;
            else if (setup != null && setup.Radius > 0)
                baseRadius = setup.Radius;
            else if (setup?.SelectionSphere != null && setup.SelectionSphere.Radius > 0)
                baseRadius = setup.SelectionSphere.Radius;
            else
            {
                log.Warn($"{Name} ({WeenieClassId}) GetPetRadius(): setup 0x{SetupTableId:X8} has no usable radius, defaulting to 0.5");
                baseRadius = 0.5f;
            }

            return PetRadiusCache[WeenieClassId] = baseRadius * scale;
        }
    }
}
