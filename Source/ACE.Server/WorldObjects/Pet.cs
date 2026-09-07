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

        // A clothed humanoid's real look (armor/robes + the palette that rides with them)
        // is assembled from its equipped items into its finished ObjDesc at runtime -- none
        // of it lives on the base weenie. So at kill time we snapshot the monster's finished
        // ObjDesc and stash it on the egg (a compact string in the unused DateOfBirth slot --
        // an item never uses or shows it), then replay it on the hatched pet. This is what
        // makes shadows/pirates hatch wearing their clothes instead of naked.
        private const PropertyString PetObjDescProp = PropertyString.DateOfBirth;

        // deserialized snapshot of the imprinted creature's finished look; set in Init when
        // the pet hatches (if the egg carries one). When non-null, CalculateObjDesc replays it.
        private ACE.Entity.ObjDesc capturedObjDesc;

        // the hatched creature's real (full) scale, remembered so in-place level-ups size correctly
        private float petFullScale = 1.0f;

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
        private void ApplyCreatureAppearance(ACE.Entity.Models.Weenie w)
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

            // Appearance + size must be set before EnterWorld().
            if (petLevel < HatchLevel)
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
                ObjScale = ScaleForLevel(petLevel, fullScale);
            }
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

            player.CurrentActivePet = this;

            petDevice.Pet = Guid.Full;
            PetDevice = petDevice.Guid.Full;
            P_PetDevice = petDevice;

            if (IsPassivePet)
                nextSlowTickTime = Time.GetUnixTime();

            return true;
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
        private double nextSlowTickTime;

        /// <summary>
        /// Called 1x per second
        /// </summary>
        public void SlowTick(double currentUnixTime)
        {
            //Console.WriteLine($"{Name}.HeartbeatStatic({currentUnixTime})");

            nextSlowTickTime += slowTickSeconds;

            if (P_PetOwner?.PhysicsObj == null)
            {
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

            return PetRadiusCache[WeenieClassId] = setup.Spheres[0].Radius * scale;
        }
    }
}
