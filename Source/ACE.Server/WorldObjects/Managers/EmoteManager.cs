using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Factories.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

using log4net;

using Position = ACE.Entity.Position;
using Spell = ACE.Server.Entity.Spell;

namespace ACE.Server.WorldObjects.Managers
{
    public class EmoteManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public WorldObject WorldObject => _proxy ?? _worldObject;

        private WorldObject _worldObject;
        private WorldObject _proxy;

        /// <summary>
        /// Returns TRUE if this WorldObject is currently busy processing other emotes
        /// </summary>
        public bool IsBusy { get; set; }
        public int Nested { get; set; }

        private const int MaxEmoteNesting = 100;

        public bool Debug = false;

        public EmoteManager(WorldObject worldObject)
        {
            _worldObject = worldObject;
        }

        /// <summary>
        /// Executes an emote
        /// </summary>
        /// <param name="emoteSet">The parent set of this emote</param>
        /// <param name="emote">The emote to execute</param>
        /// <param name="targetObject">A target object, usually player</param>
        /// <param name="actionChain">Only used for passing to further sets</param>
        public float ExecuteEmote(PropertiesEmote emoteSet, PropertiesEmoteAction emote, WorldObject targetObject = null, float campBonus = 1.0f, PropertiesEmote rootEmoteSet = null)
        {
            var player = targetObject as Player;
            var creature = WorldObject as Creature;
            var targetCreature = targetObject as Creature;

            var delay = 0.0f;
            var emoteType = (EmoteType)emote.Type;

            //if (Debug)
            //Console.WriteLine($"{WorldObject.Name}.ExecuteEmote({emoteType})");

            var text = emote.Message;

            // Quest-keyed emotes with no quest name in Message would otherwise NRE inside QuestManager
            // (and take the whole world thread down with it). Log the bad content and skip the action.
            if (EmoteRequiresQuestName(emoteType) && string.IsNullOrWhiteSpace(text))
            {
                log.Warn($"[EMOTE] 0x{WorldObject.Guid}:{WorldObject.Name} ({WorldObject.WeenieClassId}) {emoteType} has null/empty quest name in Message; skipped. EmoteSet: {emoteSet?.Category} '{emoteSet?.Quest}'");
                return delay;
            }

            switch ((EmoteType)emote.Type)
            {
                case EmoteType.Act:
                    // short for 'acting' text
                    var message = Replace(text, WorldObject, targetObject, emoteSet.Quest);
                    WorldObject.EnqueueBroadcast(new GameMessageSystemChat(message, ChatMessageType.Broadcast), 30.0f);
                    break;

                case EmoteType.Activate:

                    if (WorldObject.ActivationTarget > 0)
                    {
                        // ActOnUse delay?
                        var activationTarget = WorldObject.CurrentLandblock?.GetObject(WorldObject.ActivationTarget);
                        activationTarget?.OnActivate(player ?? WorldObject);
                    }
                    else if (WorldObject.GeneratorId.HasValue && WorldObject.GeneratorId > 0) // Fallback to linked generator
                    {
                        var linkedGenerator = WorldObject.CurrentLandblock?.GetObject(WorldObject.GeneratorId ?? 0);
                        linkedGenerator?.OnActivate(WorldObject);
                    }
                    break;

                case EmoteType.AddCharacterTitle:

                    // emoteAction.Stat == null for all EmoteType.AddCharacterTitle entries in current db?
                    if (player != null && emote.Amount != 0)
                        player.AddTitle((CharacterTitle)emote.Amount);
                    break;

                case EmoteType.AddContract:

                    // Contracts werent in emote table for 16py, guessing that Stat was used to hold id for contract.
                    if (player != null && emote.Stat.HasValue && emote.Stat.Value > 0)
                        player.ContractManager.Add(emote.Stat.Value);
                    break;

                case EmoteType.AdminSpam:

                    text = Replace(emote.Message, WorldObject, targetObject, emoteSet.Quest);

                    PlayerManager.BroadcastToChannelFromEmote(Channel.Admin, text);
                    break;

                case EmoteType.AwardLevelProportionalSkillXP:

                    var min = emote.Min64 ?? emote.Min ?? 0;
                    var max = emote.Max64 ?? emote.Max ?? 0;

                    if (player != null)
                        player.GrantLevelProportionalSkillXP((Skill)emote.Stat, emote.Percent ?? 0, min, max);
                    break;

                case EmoteType.AwardLevelProportionalXP:

                    min = emote.Min64 ?? emote.Min ?? 0;
                    max = emote.Max64 ?? emote.Max ?? 0;

                    if (player != null)
                        player.GrantLevelProportionalXp(emote.Percent ?? 0, min, max);
                    break;

                case EmoteType.AwardLuminance:

                    if (player != null)
                        player.EarnLuminance(emote.Amount64 ?? emote.HeroXP64 ?? 0, XpType.Quest, ShareType.None);

                    break;

                case EmoteType.AwardNoShareXP:

                    if (player != null)
                    {
                        var amt = emote.Amount64 ?? emote.Amount ?? 0;
                        if (amt > 0 || Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM)
                        {
                            uint? sourceId = null;
                            if (rootEmoteSet != null && rootEmoteSet.Category == EmoteCategory.Give && rootEmoteSet.WeenieClassId != 0)
                                sourceId = rootEmoteSet.WeenieClassId;

                            player.EarnXP(amt, XpType.Quest, player.Level, sourceId, (uint)(Math.Round(campBonus, 2) * 100f), null, ShareType.None);
                        }
                        else if (amt < 0)
                        {
                            player.SpendXP(-amt);
                        }
                    }
                    break;

                case EmoteType.AwardSkillPoints:

                    if (player != null)
                        player.AwardSkillPoints((Skill)emote.Stat, (uint)emote.Amount);
                    break;

                case EmoteType.AwardSkillXP:

                    if (player != null)
                    {
                        if (delay < 1) delay += 1; // because of how AwardSkillXP grants and then raises the skill, ensure delay is at least 1 to allow for processing correctly
                        player.AwardSkillXP((Skill)emote.Stat, (uint)emote.Amount, true);
                    }
                    break;

                case EmoteType.AwardTrainingCredits:

                    if (player != null)
                        player.AddSkillCredits(emote.Amount ?? 0);
                    break;

                case EmoteType.SwapRacialWeaponSkill:

                    if (player != null)
                        player.SwapRacialWeaponSkill((Skill)emote.Stat);
                    break;

                case EmoteType.AwardXP:

                    if (player != null)
                    {
                        var amt = emote.Amount64 ?? emote.Amount ?? 0;
                        if (amt > 0 || Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM)
                        {
                            uint? sourceId = null;
                            if (rootEmoteSet != null && rootEmoteSet.Category == EmoteCategory.Give && rootEmoteSet.WeenieClassId != 0)
                                sourceId = rootEmoteSet.WeenieClassId;

                            player.EarnXP(amt, XpType.Quest, player.Level, sourceId, (uint)(Math.Round(campBonus, 2) * 100f), null, ShareType.All);
                        }
                        else if (amt < 0)
                        {
                            player.SpendXP(-amt);
                        }
                    }
                    break;

                case EmoteType.BLog:

                    text = Replace(emote.Message, WorldObject, targetObject, emoteSet.Quest);

                    log.Info($"0x{WorldObject.Guid}:{WorldObject.Name}({WorldObject.WeenieClassId}).EmoteManager.BLog - {text}");
                    break;

                case EmoteType.CastSpell:

                    if (WorldObject != null)
                    {
                        var spell = new Spell((uint)emote.SpellId);
                        if (spell.NotFound)
                        {
                            log.Error($"{WorldObject.Name} ({WorldObject.Guid}) EmoteManager.CastSpell - unknown spell {emote.SpellId}");
                            break;
                        }

                        creature.CheckForHumanPreCast(spell);

                        var spellTarget = GetSpellTarget(spell, targetObject);

                        var preCastTime = creature.PreCastMotion(spellTarget);

                        delay = preCastTime + creature.GetPostCastTime(spell);

                        var castChain = new ActionChain();
                        castChain.AddDelaySeconds(preCastTime);
                        castChain.AddAction(creature, () =>
                        {
                            creature.TryCastSpell_WithRedirects(spell, spellTarget, creature);
                            creature.PostCastMotion();
                        });
                        castChain.EnqueueChain();
                    }
                    break;

                case EmoteType.CastSpellInstant:

                    if (WorldObject != null)
                    {
                        var spell = new Spell((uint)emote.SpellId);

                        if (!spell.NotFound)
                        {
                            var spellTarget = GetSpellTarget(spell, targetObject);

                            WorldObject.TryCastSpell_WithRedirects(spell, spellTarget, WorldObject);
                        }
                    }
                    break;

                case EmoteType.CloseMe:

                    // animation delay?
                    if (WorldObject is Container container)
                        container.Close(null);
                    else if (WorldObject is Door closeDoor)
                        closeDoor.Close();

                    break;

                case EmoteType.CreateTreasure:

                    if (player != null)
                    {
                        var treasureTier = emote.WealthRating ?? 1;

                        var treasureType = (TreasureItemCategory?)emote.TreasureType ?? TreasureItemCategory.Undef;

                        var treasureClass = (TreasureItemType_Orig?)emote.TreasureClass ?? TreasureItemType_Orig.Undef;

                        // Create a dummy treasure profile for passing emote values
                        var profile = new ACE.Server.Factories.Entity.TreasureDeathExtended
                        {
                            Tier = treasureTier,
                            ForceTreasureItemType = treasureClass,
                            //TreasureType = (uint)treasureType,
                            LootQualityMod = 0,
                            ItemChance = 100,
                            ItemMinAmount = 1,
                            ItemMaxAmount = 1,
                            //ItemTreasureTypeSelectionChances = (int)treasureClass,
                            MagicItemChance = 100,
                            MagicItemMinAmount = 1,
                            MagicItemMaxAmount = 1,
                            //MagicItemTreasureTypeSelectionChances = (int)treasureClass,
                            MundaneItemChance = 100,
                            MundaneItemMinAmount = 1,
                            MundaneItemMaxAmount = 1,
                            //MundaneItemTypeSelectionChances = (int)treasureClass,
                            UnknownChances = 21
                        };

                        var treasure = LootGenerationFactory.CreateRandomLootObjects(profile, treasureType);
                        if (treasure != null)
                        {
                            player.TryCreateForGive(WorldObject, treasure);
                        }
                    }
                    break;

                /* decrements a PropertyInt stat by some amount */
                case EmoteType.DecrementIntStat:

                    // only used by 1 emote in 16PY - check for lower bounds?
                    if (targetObject != null && emote.Stat != null)
                    {
                        var intProperty = (PropertyInt)emote.Stat;
                        var current = targetObject.GetProperty(intProperty) ?? 0;
                        current -= emote.Amount ?? 1;
                        targetObject.SetProperty(intProperty, current);

                        if (player != null)
                            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, intProperty, current));
                    }
                    break;

                case EmoteType.DecrementMyQuest:
                case EmoteType.DecrementQuest:

                    var questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null)
                        questTarget.QuestManager.Decrement(emote.Message, emote.Amount ?? 1);

                    break;

                case EmoteType.DeleteSelf:

                    if (player != null)
                    {
                        var wo = player.FindObject(WorldObject.Guid.Full, Player.SearchLocations.Everywhere, out _, out Container rootOwner, out bool wasEquipped);

                        WorldObject.DeleteObject(rootOwner);
                    }
                    else
                        WorldObject.DeleteObject();

                    break;

                case EmoteType.DirectBroadcast:

                    text = Replace(emote.Message, WorldObject, targetObject, emoteSet.Quest);

                    if (player != null)
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

                    break;

                case EmoteType.Enlightenment:

                    if (player != null)
                    {
                        Enlightenment.HandleEnlightenment(WorldObject, player);
                    }

                    break;

                case EmoteType.EraseMyQuest:
                case EmoteType.EraseQuest:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null)
                        questTarget.QuestManager.Erase(emote.Message);

                    break;

                case EmoteType.FellowBroadcast:

                    if (player != null)
                    {
                        var fellowship = player.Fellowship;

                        if (fellowship != null)
                        {
                            text = Replace(emote.Message, WorldObject, player, emoteSet.Quest);

                            fellowship.BroadcastToFellow(text);
                        }
                    }
                    break;

                case EmoteType.Generate:

                    if (WorldObject.IsGenerator)
                        WorldObject.Generator_Generate();
                    break;

                case EmoteType.Give:

                    bool success = false;

                    var stackSize = emote.StackSize ?? 1;

                    if (player != null && emote.WeenieClassId != null)
                    {
                        var extraMessage = "";
                        if (Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM && rootEmoteSet != null && rootEmoteSet.Category == EmoteCategory.Give && rootEmoteSet.WeenieClassId != (emote?.WeenieClassId ?? 0))
                        {
                            var roundedCampBonus = Math.Round(campBonus, 2);
                            stackSize = (int)Math.Ceiling(stackSize * roundedCampBonus);

                            if (roundedCampBonus < 1)
                                extraMessage = $"T: {(roundedCampBonus * 100).ToString("0")}%";

                            if (stackSize == 0)
                            {
                                var motionChain2 = new ActionChain();
                                if (!WorldObject.DontTurnOrMoveWhenGiving && creature != null && targetCreature != null)
                                {
                                    delay = creature.Rotate(targetCreature);
                                    motionChain2.AddDelaySeconds(delay);
                                }
                                motionChain2.AddAction(WorldObject, () => player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Your item reward amount has been reduced to zero! {extraMessage}", ChatMessageType.Broadcast)));
                                motionChain2.EnqueueChain();
                                break;
                            }
                        }

                        var motionChain = new ActionChain();

                        if (!WorldObject.DontTurnOrMoveWhenGiving && creature != null && targetCreature != null)
                        {
                            delay = creature.Rotate(targetCreature);
                            motionChain.AddDelaySeconds(delay);
                        }
                        motionChain.AddAction(WorldObject, () => player.GiveFromEmote(WorldObject, emote.WeenieClassId ?? 0, stackSize > 0 ? stackSize : 1, emote.Palette ?? 0, emote.Shade ?? 0, extraMessage));
                        motionChain.EnqueueChain();
                    }

                    break;

                /* redirects to the GotoSet category for this action */
                case EmoteType.Goto:

                    // TODO: revisit if nested chains need to back-propagate timers
                    var gotoSet = GetEmoteSet(EmoteCategory.GotoSet, emote.Message);
                    ExecuteEmoteSet(gotoSet, targetObject, true, campBonus, rootEmoteSet);
                    break;

                /* increments a PropertyInt stat by some amount */
                case EmoteType.IncrementIntStat:

                    if (targetObject != null && emote.Stat != null)
                    {
                        var intProperty = (PropertyInt)emote.Stat;
                        var current = targetObject.GetProperty(intProperty) ?? 0;
                        current += emote.Amount ?? 1;
                        targetObject.SetProperty(intProperty, current);

                        if (player != null)
                            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, intProperty, current));
                    }
                    break;

                case EmoteType.IncrementMyQuest:
                case EmoteType.IncrementQuest:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null)
                        questTarget.QuestManager.Increment(emote.Message, emote.Amount ?? 1);

                    break;

                case EmoteType.InflictVitaePenalty:
                    if (player != null)
                        player.InflictVitaePenalty(emote.Amount ?? 0);
                    break;

                case EmoteType.InqAttributeStat:

                    if (targetCreature != null)
                    {
                        var attr = targetCreature.Attributes[(PropertyAttribute)emote.Stat];

                        if (attr == null && HasValidTestNoQuality(emote.Message))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            success = attr != null && attr.Current >= (emote.Min ?? int.MinValue) && attr.Current <= (emote.Max ?? int.MaxValue);

                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqBoolStat:

                    if (targetObject != null)
                    {
                        var stat = targetObject.GetProperty((PropertyBool)emote.Stat);

                        if (stat == null && HasValidTestNoQuality(emote.Message))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            success = stat ?? false;

                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqContractsFull:

                    ExecuteEmoteSet(player != null && player.ContractManager.IsFull ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    break;

                case EmoteType.InqEvent:

                    var started = EventManager.IsEventStarted(emote.Message, WorldObject, targetObject);
                    ExecuteEmoteSet(started ? EmoteCategory.EventSuccess : EmoteCategory.EventFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    break;

                case EmoteType.InqFellowNum:

                    // unused in PY16 - ensure # of fellows between min-max?
                    var result = HasValidTestNoFellow(emote.Message) ? EmoteCategory.TestNoFellow : EmoteCategory.NumFellowsFailure;

                    if (player?.Fellowship != null)
                    {
                        var fellows = player.Fellowship.GetFellowshipMembers();

                        if (fellows.Count < (emote.Min ?? int.MinValue) || fellows.Count > (emote.Max ?? int.MaxValue))
                            result = EmoteCategory.NumFellowsFailure;
                        else
                            result = EmoteCategory.NumFellowsSuccess;
                    }
                    ExecuteEmoteSet(result, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    break;

                case EmoteType.InqFellowQuest:

                    if (player != null)
                    {
                        if (player.Fellowship != null)
                        {
                            var hasQuest = player.Fellowship.QuestManager.HasQuest(emote.Message);
                            var canSolve = player.Fellowship.QuestManager.CanSolve(emote.Message);

                            // verify: QuestSuccess = player has quest, and their last completed time + quest minDelta <= currentTime
                            success = hasQuest && !canSolve;

                            ExecuteEmoteSet(success ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                            ExecuteEmoteSet(EmoteCategory.QuestNoFellow, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    }
                    break;

                case EmoteType.InqFloatStat:

                    if (targetObject != null)
                    {
                        var stat = targetObject.GetProperty((PropertyFloat)emote.Stat);

                        if (stat == null && HasValidTestNoQuality(emote.Message))
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        else
                        {
                            stat ??= 0.0f;
                            success = stat >= (emote.MinDbl ?? double.MinValue) && stat <= (emote.MaxDbl ?? double.MaxValue);
                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqInt64Stat:

                    if (targetObject != null)
                    {
                        var stat = targetObject.GetProperty((PropertyInt64)emote.Stat);

                        if (stat == null && HasValidTestNoQuality(emote.Message))
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        else
                        {
                            stat ??= 0;
                            success = stat >= (emote.Min64 ?? long.MinValue) && stat <= (emote.Max64 ?? long.MaxValue);
                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqIntStat:

                    if (targetObject != null)
                    {
                        var stat = targetObject.GetProperty((PropertyInt)emote.Stat);

                        if (stat == null && HasValidTestNoQuality(emote.Message))
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        else
                        {
                            stat ??= 0;
                            success = stat >= (emote.Min ?? int.MinValue) && stat <= (emote.Max ?? int.MaxValue);
                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqNumCharacterTitles:

                    if (player != null)
                    {
                        var numTitles = player.NumCharacterTitles;
                        success = numTitles != null && numTitles >= (emote.Min ?? int.MinValue) && numTitles <= (emote.Max ?? int.MaxValue);
                        ExecuteEmoteSet(success ? EmoteCategory.NumCharacterTitlesSuccess : EmoteCategory.NumCharacterTitlesFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    }
                    break;

                case EmoteType.InqOwnsItems:

                    if (player != null)
					{
                        var numRequired = emote.StackSize ?? 1;

                        var items = player.GetInventoryItemsOfWCID(emote.WeenieClassId ?? 0, true);
                        items.AddRange(player.GetEquippedObjectsOfWCID(emote.WeenieClassId ?? 0));
                        var numItems = items.Sum(i => i.StackSize ?? 1);

                        success = numItems >= numRequired;

                        ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    }
                    break;

                case EmoteType.InqPackSpace:

                    if (player != null)
                    {
                        var numRequired = emote.Amount ?? 1;

                        success = false;
                        if (numRequired > 10000) // Since emote was not in 16py and we have just the two fields to go on, I will assume you could "mask" the value to pick between free Item Capacity space or free Container Capacity space
                        {
                            var freeSpace = player.GetFreeContainerSlots();

                            success = freeSpace >= (numRequired - 10000);
                        }
                        else
                        {
                            var freeSpace = player.GetFreeInventorySlots(false); // assuming this was only for main pack. makes things easier at this point.

                            success = freeSpace >= numRequired;
                        }

                        ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    }
                    break;

                case EmoteType.InqMyQuest:
                case EmoteType.InqQuest:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null)
                    {
                        var hasQuest = questTarget.QuestManager.HasQuest(emote.Message);
                        var canSolve = questTarget.QuestManager.CanSolve(emote.Message);

                        //  verify: QuestSuccess = player has quest, but their quest timer is currently still on cooldown
                        success = hasQuest && !canSolve;

                        ExecuteEmoteSet(success ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    }

                    break;

                case EmoteType.InqMyQuestBitsOff:
                case EmoteType.InqQuestBitsOff:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null)
                    {
                        var hasNoQuestBits = questTarget.QuestManager.HasNoQuestBits(emote.Message, emote.Amount ?? 0);

                        ExecuteEmoteSet(hasNoQuestBits ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    }

                    break;

                case EmoteType.InqMyQuestBitsOn:
                case EmoteType.InqQuestBitsOn:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null)
                    {
                        var hasQuestBits = questTarget.QuestManager.HasQuestBits(emote.Message, emote.Amount ?? 0);

                        ExecuteEmoteSet(hasQuestBits ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    }

                    break;

                case EmoteType.InqMyQuestSolves:
                case EmoteType.InqQuestSolves:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null)
                    {
                        var questSolves = questTarget.QuestManager.HasQuestSolves(emote.Message, emote.Min, emote.Max);

                        ExecuteEmoteSet(questSolves ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    }
                    break;

                case EmoteType.InqRawAttributeStat:

                    if (targetCreature != null)
                    {
                        var attr = targetCreature.Attributes[(PropertyAttribute)emote.Stat];

                        if (attr == null && HasValidTestNoQuality(emote.Message))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            success = attr != null && attr.Base >= (emote.Min ?? int.MinValue) && attr.Base <= (emote.Max ?? int.MaxValue);

                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqRawSecondaryAttributeStat:

                    if (targetCreature != null)
                    {
                        var vital = targetCreature.Vitals[(PropertyAttribute2nd)emote.Stat];

                        if (vital == null && HasValidTestNoQuality(emote.Message))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            success = vital != null && vital.Base >= (emote.Min ?? int.MinValue) && vital.Base <= (emote.Max ?? int.MaxValue);

                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqRawSkillStat:

                    if (targetCreature != null)
                    {
                        var skill = targetCreature.GetCreatureSkill((Skill)emote.Stat);

                        if (skill == null && HasValidTestNoQuality(emote.Message))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            success = skill != null && skill.Base >= (emote.Min ?? int.MinValue) && skill.Base <= (emote.Max ?? int.MaxValue);

                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqSecondaryAttributeStat:

                    if (targetCreature != null)
                    {
                        var vital = targetCreature.Vitals[(PropertyAttribute2nd)emote.Stat];

                        if (vital == null && HasValidTestNoQuality(emote.Message))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            success = vital != null && vital.Current >= (emote.Min ?? int.MinValue) && vital.Current <= (emote.Max ?? int.MaxValue);

                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqSkillSpecialized:

                    if (targetCreature != null)
                    {
                        var skill = targetCreature.GetCreatureSkill((Skill)emote.Stat);

                        if (skill == null && HasValidTestNoQuality(emote.Message))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            success = skill != null && skill.AdvancementClass == SkillAdvancementClass.Specialized;

                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqSkillStat:

                    if (targetCreature != null)
                    {
                        var skill = targetCreature.GetCreatureSkill((Skill)emote.Stat);

                        if (skill == null && HasValidTestNoQuality(emote.Message))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            success = skill != null && skill.Current >= (emote.Min ?? int.MinValue) && skill.Current <= (emote.Max ?? int.MaxValue);

                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqSkillTrained:

                    if (targetCreature != null)
                    {
                        var skill = targetCreature.GetCreatureSkill((Skill)emote.Stat);

                        if (skill == null && HasValidTestNoQuality(emote.Message))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            success = skill != null && skill.AdvancementClass >= SkillAdvancementClass.Trained;

                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqStringStat:

                    if (targetObject != null)
                    {
                        var stringStat = targetObject.GetProperty((PropertyString)emote.Stat);

                        if (stringStat == null && HasValidTestNoQuality(emote.Message))
                            ExecuteEmoteSet(EmoteCategory.TestNoQuality, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        else
                        {
                            success = stringStat != null && stringStat.Equals(emote.TestString);
                            ExecuteEmoteSet(success ? EmoteCategory.TestSuccess : EmoteCategory.TestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.InqYesNo:

                    if (player != null)
                    {
                        if (!player.ConfirmationManager.EnqueueSend(new Confirmation_YesNo(WorldObject.Guid, player.Guid, emote.Message), Replace(emote.TestString, WorldObject, targetObject, emoteSet.Quest)))
                        {
                            ExecuteEmoteSet(EmoteCategory.TestFailure, emote.Message, player, false, campBonus, rootEmoteSet);
                        }
                    }
                    break;

                case EmoteType.Invalid:
                    break;

                case EmoteType.KillSelf:

                    if (creature != null)
                        creature.Smite(creature);
                    break;

                case EmoteType.LocalBroadcast:

                    message = Replace(emote.Message, WorldObject, targetObject, emoteSet.Quest);
                    WorldObject.EnqueueBroadcast(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
                    break;

                case EmoteType.LocalSignal:

                    if (WorldObject != null)
                    {
                        if (WorldObject.CurrentLandblock != null)
                        {
                            // Throttle the mass teardown broadcast. The hourly arena reset re-fires
                            // ColoSelfDestruct many times in a few seconds (once per heartbeat while its
                            // condition holds), and each broadcast hits every object in the arena. Collapse
                            // those into one broadcast per cooldown so a single reset can't storm the world.
                            if (ColoIsNoiseSignal(emote.Message))
                            {
                                if ((DateTime.UtcNow - lastColoFanoutBroadcast).TotalSeconds < ColoFanoutCooldownSeconds)
                                    break;
                                lastColoFanoutBroadcast = DateTime.UtcNow;
                            }

                            WorldObject.CurrentLandblock.EmitSignal(WorldObject, emote.Message);
                        }

                        if (IsColoCoordinator)
                            ColoTrackSignal(emote.Message);
                    }
                    break;

                case EmoteType.LockFellow:

                    if (player != null && player.Fellowship != null)
                        player.HandleActionFellowshipChangeLock(true, emoteSet.Quest);

                    break;

                /* plays an animation on the target object (usually the player) */
                case EmoteType.ForceMotion:

                    var motionCommand = MotionCommandHelper.GetMotion(emote.Motion.Value);
                    var motion = new Motion(targetObject, motionCommand, emote.Extent);
                    targetObject.EnqueueBroadcastMotion(motion);
                    break;

                /* plays an animation on the source object */
                case EmoteType.Motion:

                    var debugMotion = false;

                    if (Debug)
                        Console.Write($".{(MotionCommand)emote.Motion}");

                    // If the landblock is dormant, there are no players in range
                    if (WorldObject.CurrentLandblock?.IsDormant ?? false)
                        break;

                    // are there players within emote range?
                    if (!WorldObject.PlayersInRange(ClientMaxAnimRange))
                        break;

                    if (WorldObject.PhysicsObj != null && WorldObject.PhysicsObj.IsMovingTo())
                        break;

                    if (WorldObject == null || WorldObject.CurrentMotionState == null) break;

                    // TODO: REFACTOR ME
                    if (emoteSet.Category != EmoteCategory.Vendor && emoteSet.Style != null)
                    {
                        var startingMotion = new Motion((MotionStance)emoteSet.Style, (MotionCommand)emoteSet.Substyle);
                        motion = new Motion((MotionStance)emoteSet.Style, (MotionCommand)emote.Motion, emote.Extent);

                        if (WorldObject.CurrentMotionState.Stance != startingMotion.Stance)
                        {
                            if (WorldObject.CurrentMotionState.Stance == MotionStance.Invalid)
                            {
                                if (debugMotion)
                                    Console.WriteLine($"{WorldObject.Name} running starting motion {(MotionStance)emoteSet.Style}, {(MotionCommand)emoteSet.Substyle}");

                                delay = WorldObject.ExecuteMotion(startingMotion);
                            }
                        }
                        else
                        {
                            if (WorldObject.CurrentMotionState.MotionState.ForwardCommand == startingMotion.MotionState.ForwardCommand
                                    && startingMotion.Stance == MotionStance.NonCombat)     // enforce non-combat here?
                            {
                                if (debugMotion)
                                    Console.WriteLine($"{WorldObject.Name} running motion {(MotionStance)emoteSet.Style}, {(MotionCommand)emote.Motion}");

                                float? maxRange = ClientMaxAnimRange;
                                if (MotionQueue.Contains((MotionCommand)emote.Motion))
                                    maxRange = null;

                                var motionTable = DatManager.PortalDat.ReadFromDat<DatLoader.FileTypes.MotionTable>(WorldObject.MotionTableId);
                                var animLength = motionTable.GetAnimationLength(WorldObject.CurrentMotionState.Stance, (MotionCommand)emote.Motion, MotionCommand.Ready);

                                delay = WorldObject.ExecuteMotion(motion, true, maxRange);

                                var motionChain = new ActionChain();
                                motionChain.AddDelaySeconds(animLength);
                                motionChain.AddAction(WorldObject, () =>
                                {
                                    // FIXME: better cycle handling
                                    var cmd = WorldObject.CurrentMotionState.MotionState.ForwardCommand;
                                    if (cmd != MotionCommand.Dead && cmd != MotionCommand.Sleeping && cmd != MotionCommand.Sitting && !cmd.ToString().EndsWith("State"))
                                    {
                                        if (debugMotion)
                                            Console.WriteLine($"{WorldObject.Name} running starting motion again {(MotionStance)emoteSet.Style}, {(MotionCommand)emoteSet.Substyle}");

                                        WorldObject.ExecuteMotion(startingMotion);
                                    }
                                });
                                motionChain.EnqueueChain();

                                if (debugMotion)
                                    Console.WriteLine($"{WorldObject.Name} appending time to existing chain: " + animLength);
                            }
                        }
                    }
                    else
                    {
                        // vendor / other motions
                        var startingMotion = new Motion(MotionStance.NonCombat, MotionCommand.Ready);
                        var motionTable = DatManager.PortalDat.ReadFromDat<DatLoader.FileTypes.MotionTable>(WorldObject.MotionTableId);
                        var animLength = motionTable.GetAnimationLength(WorldObject.CurrentMotionState.Stance, (MotionCommand)emote.Motion, MotionCommand.Ready);

                        motion = new Motion(MotionStance.NonCombat, (MotionCommand)emote.Motion, emote.Extent);

                        if (debugMotion)
                            Console.WriteLine($"{WorldObject.Name} running motion (block 2) {MotionStance.NonCombat}, {(MotionCommand)(emote.Motion ?? 0)}");

                        delay = WorldObject.ExecuteMotion(motion);

                        var motionChain = new ActionChain();
                        motionChain.AddDelaySeconds(animLength);
                        motionChain.AddAction(WorldObject, () => WorldObject.ExecuteMotion(startingMotion, false));

                        motionChain.EnqueueChain();
                    }

                    break;

                /* move to position relative to home */
                case EmoteType.Move:

                    if (creature != null)
                    {
                        // If the landblock is dormant, there are no players in range
                        if (WorldObject.CurrentLandblock?.IsDormant ?? false)
                            break;

                        // are there players within emote range?
                        if (!WorldObject.PlayersInRange(ClientMaxAnimRange))
                            break;

                        var newPos = new Position(creature.Home);
                        newPos.Pos += new Vector3(emote.OriginX ?? 0, emote.OriginY ?? 0, emote.OriginZ ?? 0);      // uses relative position

                        // ensure valid quaternion - all 0s for example can lock up physics engine
                        if (emote.AnglesX != null && emote.AnglesY != null && emote.AnglesZ != null && emote.AnglesW != null &&
                           (emote.AnglesX != 0    || emote.AnglesY != 0    || emote.AnglesZ != 0    || emote.AnglesW != 0) )
                        {
                            // also relative, or absolute?
                            newPos.Rotation *= new Quaternion(emote.AnglesX.Value, emote.AnglesY.Value, emote.AnglesZ.Value, emote.AnglesW.Value);  
                        }

                        if (Debug)
                            Console.WriteLine(newPos.ToLOCString());

                        // get new cell
                        newPos.LandblockId = new LandblockId(PositionExtensions.GetCell(newPos));

                        var useFinalHeading = newPos.Rotation != Quaternion.Identity;

                        // TODO: handle delay for this?
                        creature.MoveTo(newPos, 2.0f, emote.Extent, useFinalHeading);
                    }
                    break;

                case EmoteType.MoveHome:

                    // TODO: call MoveToManager on server, handle delay for this?
                    if (creature != null && creature.Home != null)
                    {
                        // are we already at home origin?
                        if (creature.Location.Pos.Equals(creature.Home.Pos))
                        {
                            // just turnto if required?
                            if (Debug)
                                Console.Write($" - already at home origin, checking rotation");

                            if (!creature.Location.Rotation.Equals(creature.Home.Rotation))
                            {
                                if (Debug)
                                    Console.Write($" - turning to");
                                delay = creature.TurnTo(creature.Home);
                            }
                            else if (Debug)
                                Console.Write($" - already at home rotation, doing nothing");
                        }
                        else
                        {
                            if (Debug)
                                Console.Write($" - {creature.Home.ToLOCString()}");

                            // how to get delay with this, callback required?
                            creature.MoveTo(creature.Home, 2.0f, emote.Extent);
                        }
                    }
                    break;

                case EmoteType.MoveToPos:

                    if (creature != null)
                    {
                        var currentPos = creature.Location;

                        var newPos = new Position();
                        newPos.LandblockId = new LandblockId(emote.ObjCellId ?? currentPos.LandblockId.Raw);

                        newPos.Pos = new Vector3(emote.OriginX ?? currentPos.Pos.X, emote.OriginY ?? currentPos.Pos.Y, emote.OriginZ ?? currentPos.Pos.Z);

                        if (emote.AnglesX == null || emote.AnglesY == null || emote.AnglesZ == null || emote.AnglesW == null)
                            newPos.Rotation = new Quaternion(currentPos.Rotation.X, currentPos.Rotation.Y, currentPos.Rotation.Z, currentPos.Rotation.W);
                        else
                            newPos.Rotation = new Quaternion(emote.AnglesX ?? 0, emote.AnglesY ?? 0, emote.AnglesZ ?? 0, emote.AnglesW ?? 1);

                        //if (emote.ObjCellId != null)
                        //newPos.LandblockId = new LandblockId(emote.ObjCellId.Value);

                        newPos.LandblockId = new LandblockId(PositionExtensions.GetCell(newPos));

                        var useFinalHeading = newPos.Rotation != Quaternion.Identity;

                        // TODO: handle delay for this?
                        creature.MoveTo(newPos, 2.0f, emote.Extent, useFinalHeading);
                    }
                    break;

                case EmoteType.OpenMe:

                    if (WorldObject is Container openContainer)
                        openContainer.Open(null);
                    else if (WorldObject is Door openDoor)
                        openDoor.Open();

                    break;

                case EmoteType.PetCastSpellOnOwner:

                    if (creature is Pet passivePet && passivePet.P_PetOwner != null)
                    {
                        var spell = new Spell((uint)emote.SpellId);
                        passivePet.TryCastSpell(spell, passivePet.P_PetOwner);
                    }
                    break;

                case EmoteType.PhysScript:

                    WorldObject.PlayParticleEffect((PlayScript)emote.PScript, WorldObject.Guid, emote.Extent);
                    break;

                case EmoteType.PopUp:

                    if (player != null)
                        player.Session.Network.EnqueueSend(new GameEventPopupString(player.Session, emote.Message));
                    break;

                case EmoteType.RemoveContract:

                    if (player != null && emote.Stat.HasValue && emote.Stat.Value > 0)
                        player.HandleActionAbandonContract((uint)emote.Stat);
                    break;

                case EmoteType.RemoveVitaePenalty:

                    if (player != null)
                        player.EnchantmentManager.RemoveVitae();

                    break;

                case EmoteType.ResetHomePosition:

                    if (WorldObject.Location != null)
                        WorldObject.Home = new Position(WorldObject.Location);
                    break;

                case EmoteType.Say:

                    if (Debug)
                        Console.Write($" - {emote.Message}");

                    message = Replace(emote.Message, WorldObject, targetObject, emoteSet.Quest);

                    var name = WorldObject.CreatureType == CreatureType.Olthoi ? WorldObject.Name + "&" : WorldObject.Name;

                    if (emote.Extent > 0)
                        WorldObject.EnqueueBroadcast(new GameMessageHearRangedSpeech(message, name, WorldObject.Guid.Full, emote.Extent, ChatMessageType.Emote), WorldObject.LocalBroadcastRange);
                    else
                        WorldObject.EnqueueBroadcast(new GameMessageHearSpeech(message, name, WorldObject.Guid.Full, ChatMessageType.Emote), WorldObject.LocalBroadcastRange);
                    break;

                case EmoteType.SetAltRacialSkills:
                    break;

                case EmoteType.SetBoolStat:

                    if (player != null)
                    {
                        player.UpdateProperty(player, (PropertyBool)emote.Stat, emote.Amount == 0 ? false : true);
                        player.EnqueueBroadcast(false, new GameMessagePublicUpdatePropertyBool(player, (PropertyBool)emote.Stat, emote.Amount == 0 ? false : true));
                    }
                    break;

                case EmoteType.SetEyePalette:
                    //if (creature != null)
                    //    creature.EyesPaletteDID = (uint)emote.Display;
                    break;

                case EmoteType.SetEyeTexture:
                    //if (creature != null)
                    //    creature.EyesTextureDID = (uint)emote.Display;
                    break;

                case EmoteType.SetFloatStat:

                    if (player != null)
                    {
                        player.UpdateProperty(player, (PropertyFloat)emote.Stat, emote.Percent);
                        player.EnqueueBroadcast(false, new GameMessagePublicUpdatePropertyFloat(player, (PropertyFloat)emote.Stat, Convert.ToDouble(emote.Percent)));
                    }
                    break;

                case EmoteType.SetHeadObject:
                    //if (creature != null)
                    //    creature.HeadObjectDID = (uint)emote.Display;
                    break;

                case EmoteType.SetHeadPalette:
                    break;

                case EmoteType.SetInt64Stat:

                    if (player != null)
                    {
                        player.UpdateProperty(player, (PropertyInt64)emote.Stat, emote.Amount64);
                        player.EnqueueBroadcast(false, new GameMessagePublicUpdatePropertyInt64(player, (PropertyInt64)emote.Stat, Convert.ToInt64(emote.Amount64)));
                    }
                    break;

                case EmoteType.SetIntStat:

                    if (player != null)
                    {
                        player.UpdateProperty(player, (PropertyInt)emote.Stat, emote.Amount);
                        player.EnqueueBroadcast(false, new GameMessagePublicUpdatePropertyInt(player, (PropertyInt)emote.Stat, Convert.ToInt32(emote.Amount)));
                    }
                    break;

                case EmoteType.SetMouthPalette:
                    break;

                case EmoteType.SetMouthTexture:
                    //if (creature != null)
                    //    creature.MouthTextureDID = (uint)emote.Display;
                    break;

                case EmoteType.SetNosePalette:
                    break;

                case EmoteType.SetNoseTexture:
                    //if (creature != null)
                    //    creature.NoseTextureDID = (uint)emote.Display;
                    break;

                case EmoteType.SetMyQuestBitsOff:
                case EmoteType.SetQuestBitsOff:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null && emote.Message != null && emote.Amount != null)
                        questTarget.QuestManager.SetQuestBits(emote.Message, (int)emote.Amount, false);

                    break;

                case EmoteType.SetMyQuestBitsOn:
                case EmoteType.SetQuestBitsOn:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null && emote.Message != null && emote.Amount != null)
                        questTarget.QuestManager.SetQuestBits(emote.Message, (int)emote.Amount);

                    break;

                case EmoteType.SetMyQuestCompletions:
                case EmoteType.SetQuestCompletions:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null && emote.Amount != null)
                        questTarget.QuestManager.SetQuestCompletions(emote.Message, (int)emote.Amount);

                    break;

                case EmoteType.SetSanctuaryPosition:

                    if (player != null)
                        player.SetPosition(PositionType.Sanctuary, new Position(emote.ObjCellId.Value, emote.OriginX.Value, emote.OriginY.Value, emote.OriginZ.Value, emote.AnglesX.Value, emote.AnglesY.Value, emote.AnglesZ.Value, emote.AnglesW.Value));
                    break;

                case EmoteType.Sound:

                    WorldObject.EnqueueBroadcast(new GameMessageSound(WorldObject.Guid, (Sound)emote.Sound, 1.0f));
                    break;

                case EmoteType.SpendLuminance:

                    if (player != null)
                        player.SpendLuminance(emote.Amount64 ?? emote.HeroXP64 ?? 0);
                    break;

                case EmoteType.StampFellowQuest:

                    if (player != null)
                    {
                        if (player.Fellowship != null)
                        {
                            var questName = emote.Message;

                            player.Fellowship.QuestManager.Stamp(emote.Message);
                        }
                    }
                    break;

                case EmoteType.StampMyQuest:
                case EmoteType.StampQuest:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null)
                    {
                        var questName = emote.Message;

                        if (questName.EndsWith("@#kt", StringComparison.Ordinal))
                            log.Warn($"0x{WorldObject.Guid}:{WorldObject.Name} ({WorldObject.WeenieClassId}).EmoteManager.ExecuteEmote: EmoteType.StampQuest({questName}) is a depreciated kill task method.");

                        questTarget.QuestManager.Stamp(emote.Message);
                    }
                    break;

                case EmoteType.StartBarber:

                    if (player != null)
                        player.StartBarber();
                    break;

                case EmoteType.StartEvent:

                    EventManager.StartEvent(emote.Message, WorldObject, targetObject);
                    break;

                case EmoteType.StopEvent:

                    EventManager.StopEvent(emote.Message, WorldObject, targetObject);
                    break;

                case EmoteType.TakeItems:

                    if (player != null)
                    {
                        var weenieItemToTake = emote.WeenieClassId ?? 0;
                        var amountToTake = emote.StackSize ?? 1;

                        if (weenieItemToTake == 0)
                        {
                            log.Warn($"EmoteManager.Execute: 0x{WorldObject.Guid} {WorldObject.Name} ({WorldObject.WeenieClassId}) EmoteType.TakeItems has invalid emote.WeenieClassId: {weenieItemToTake}");
                            break;
                        }

                        if (amountToTake < -1 || amountToTake == 0)
                        {
                            log.Warn($"EmoteManager.Execute: 0x{WorldObject.Guid} {WorldObject.Name} ({WorldObject.WeenieClassId}) EmoteType.TakeItems has invalid emote.StackSize: {amountToTake}");
                            break;
                        }

                        if ((player.GetNumInventoryItemsOfWCID(weenieItemToTake, true) > 0 && player.TryConsumeFromInventoryWithNetworking(weenieItemToTake, amountToTake == -1 ? int.MaxValue : amountToTake))
                            || (player.GetNumEquippedObjectsOfWCID(weenieItemToTake) > 0 && player.TryConsumeFromEquippedObjectsWithNetworking(weenieItemToTake, amountToTake == -1 ? int.MaxValue : amountToTake)))
                        {
                            var itemTaken = DatabaseManager.World.GetCachedWeenie(weenieItemToTake);
                            if (itemTaken != null)
                            {
                                var amount = amountToTake == -1 ? "all" : amountToTake.ToString();

                                var msg = $"You hand over {amount} of your {itemTaken.GetPluralName()}.";

                                player.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
                            }
                        }
                    }
                    break;

                case EmoteType.TeachSpell:

                    if (player != null)
                        player.LearnSpellWithNetworking((uint)emote.SpellId, false);
                    break;

                case EmoteType.TeleportSelf:

                    //if (WorldObject is Player)
                    //(WorldObject as Player).Teleport(emote.Position);
                    if (creature != null)
                    {
                        if (emote.ObjCellId.HasValue && emote.OriginX.HasValue && emote.OriginY.HasValue && emote.OriginZ.HasValue && emote.AnglesX.HasValue && emote.AnglesY.HasValue && emote.AnglesZ.HasValue && emote.AnglesW.HasValue)
                        {
                            if (emote.ObjCellId.Value > 0)
                            {
                                // ensure same landblock
                                if ((emote.ObjCellId.Value >> 16) == (creature.Location.Cell >> 16))
                                    creature.FakeTeleport(new Position(emote.ObjCellId.Value, emote.OriginX.Value, emote.OriginY.Value, emote.OriginZ.Value, emote.AnglesX.Value, emote.AnglesY.Value, emote.AnglesZ.Value, emote.AnglesW.Value));
                            }
                            else // position is relative to creature's current location
                            {
                                var relativeDestination = new Position(creature.Location);
                                relativeDestination.Pos += new Vector3(emote.OriginX.Value, emote.OriginY.Value, emote.OriginZ.Value);
                                relativeDestination.Rotation = new Quaternion(emote.AnglesX.Value, emote.AnglesY.Value, emote.AnglesZ.Value, emote.AnglesW.Value);
                                relativeDestination.LandblockId = new LandblockId(relativeDestination.GetCell());

                                // ensure same landblock
                                if ((relativeDestination.Cell >> 16) == (creature.Location.Cell >> 16))
                                    creature.FakeTeleport(relativeDestination);
                            }
                        }
                    }
                    break;

                case EmoteType.TeleportTarget:

                    if (player != null)
                    {
                        if (emote.ObjCellId.HasValue && emote.OriginX.HasValue && emote.OriginY.HasValue && emote.OriginZ.HasValue && emote.AnglesX.HasValue && emote.AnglesY.HasValue && emote.AnglesZ.HasValue && emote.AnglesW.HasValue)
                        {
                            if (emote.ObjCellId.Value > 0)
                            {
                                var destination = new Position(emote.ObjCellId.Value, emote.OriginX.Value, emote.OriginY.Value, emote.OriginZ.Value, emote.AnglesX.Value, emote.AnglesY.Value, emote.AnglesZ.Value, emote.AnglesW.Value);

                                WorldObject.AdjustDungeon(destination);
                                WorldManager.ThreadSafeTeleport(player, destination);
                            }
                            else // position is relative to WorldObject's current location
                            {
                                var relativeDestination = new Position(WorldObject.Location);
                                relativeDestination.Pos += new Vector3(emote.OriginX.Value, emote.OriginY.Value, emote.OriginZ.Value);
                                relativeDestination.Rotation = new Quaternion(emote.AnglesX.Value, emote.AnglesY.Value, emote.AnglesZ.Value, emote.AnglesW.Value);
                                relativeDestination.LandblockId = new LandblockId(relativeDestination.GetCell());

                                WorldObject.AdjustDungeon(relativeDestination);
                                WorldManager.ThreadSafeTeleport(player, relativeDestination);
                            }
                        }
                    }
                    break;

                case EmoteType.Tell:

                    if (player != null)
                    {
                        message = Replace(emote.Message, WorldObject, player, emoteSet.Quest);
                        player.Session.Network.EnqueueSend(new GameEventTell(WorldObject, message, player, ChatMessageType.Tell));
                    }
                    break;

                case EmoteType.TellFellow:

                    if (player != null)
                    {
                        var fellowship = player.Fellowship;
                        if (fellowship != null)
                        {
                            text = Replace(emote.Message, WorldObject, player, emoteSet.Quest);

                            fellowship.TellFellow(WorldObject, text);
                        }
                    }
                    break;

                case EmoteType.TextDirect:

                    if (player != null)
                    {
                        message = Replace(emote.Message, WorldObject, player, emoteSet.Quest);
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
                    }
                    break;

                case EmoteType.Turn:

                    if (creature != null)
                    {
                        // turn to heading
                        var rotation = new Quaternion(emote.AnglesX ?? 0, emote.AnglesY ?? 0, emote.AnglesZ ?? 0, emote.AnglesW ?? 1);
                        var newPos = new Position(creature.Location);
                        newPos.Rotation = rotation;

                        var rotateTime = creature.TurnTo(newPos);
                        delay = rotateTime;
                    }
                    break;

                case EmoteType.TurnToTarget:

                    if (creature != null && targetCreature != null)
                        delay = creature.Rotate(targetCreature);

                    break;

                case EmoteType.UntrainSkill:

                    if (player != null)
                        player.ResetSkill((Skill)emote.Stat);
                    break;

                case EmoteType.UpdateFellowQuest:

                    if (player != null)
                    {
                        if (player.Fellowship != null)
                        {
                            var questName = emote.Message;

                            var hasQuest = player.Fellowship.QuestManager.HasQuest(questName);

                            if (!hasQuest)
                            {
                                // add new quest
                                player.Fellowship.QuestManager.Update(questName);
                                hasQuest = player.Fellowship.QuestManager.HasQuest(questName);
                                ExecuteEmoteSet(hasQuest ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                            }
                            else
                            {
                                // update existing quest
                                var canSolve = player.Fellowship.QuestManager.CanSolve(questName);
                                if (canSolve)
                                    player.Fellowship.QuestManager.Stamp(questName);
                                ExecuteEmoteSet(canSolve ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                            }
                        }
                        else
                            ExecuteEmoteSet(EmoteCategory.QuestNoFellow, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                    }
                    break;

                case EmoteType.UpdateMyQuest:
                case EmoteType.UpdateQuest:

                    questTarget = GetQuestTarget((EmoteType)emote.Type, targetCreature, creature);

                    if (questTarget != null)
                    {
                        var questName = emote.Message;

                        var hasQuest = questTarget.QuestManager.HasQuest(questName);

                        if (!hasQuest)
                        {
                            // add new quest
                            questTarget.QuestManager.Update(questName);
                            hasQuest = questTarget.QuestManager.HasQuest(questName);
                            ExecuteEmoteSet(hasQuest ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                        else
                        {
                            // update existing quest
                            var canSolve = questTarget.QuestManager.CanSolve(questName);
                            if (canSolve)
                                questTarget.QuestManager.Stamp(questName);
                            ExecuteEmoteSet(canSolve ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, emote.Message, targetObject, true, campBonus, rootEmoteSet);
                        }
                    }

                    break;

                case EmoteType.WorldBroadcast:

                    message = Replace(text, WorldObject, targetObject, emoteSet.Quest);

                    PlayerManager.BroadcastToAll(new GameMessageSystemChat(message, ChatMessageType.WorldBroadcast));

                    PlayerManager.LogBroadcastChat(Channel.AllBroadcast, WorldObject, message);

                    break;

                case EmoteType.VendorBroadcastStockLocal:
                    {
                        if (creature is Vendor vendor)
                            vendor.BroadcastStock(false);
                        break;
                    }

                case EmoteType.VendorBroadcastStockWorld:
                    {
                        if (creature is Vendor vendor)
                            vendor.BroadcastStock(true);
                        break;
                    }

                case EmoteType.RefreshExplorationAssignments:
                    {
                        if (player != null)
                            player.RefreshExplorationAssignments(WorldObject);
                        break;
                    }

                case EmoteType.RewardExplorationAssignments:
                    {
                        if (player != null)
                            player.RewardExplorationAssignments(WorldObject);
                        break;
                    }

                case EmoteType.AcademyExplorationAssignment:
                    {
                        if (player != null)
                            player.RefreshExplorationAssignments(WorldObject, false, true);
                        break;
                    }

                case EmoteType.BlinkTarget:

                    if (player != null)
                    {
                        if (emote.ObjCellId.HasValue && emote.OriginX.HasValue && emote.OriginY.HasValue && emote.OriginZ.HasValue && emote.AnglesX.HasValue && emote.AnglesY.HasValue && emote.AnglesZ.HasValue && emote.AnglesW.HasValue)
                        {
                            if (emote.ObjCellId.Value > 0)
                            {
                                var destination = new Position(emote.ObjCellId.Value, emote.OriginX.Value, emote.OriginY.Value, emote.OriginZ.Value, emote.AnglesX.Value, emote.AnglesY.Value, emote.AnglesZ.Value, emote.AnglesW.Value);

                                WorldObject.AdjustDungeon(destination);
                                WorldManager.ThreadSafeBlink(player, destination);
                            }
                            else // position is relative to WorldObject's current location
                            {
                                var relativeDestination = new Position(WorldObject.Location);
                                relativeDestination.Pos += new Vector3(emote.OriginX.Value, emote.OriginY.Value, emote.OriginZ.Value);
                                relativeDestination.Rotation = new Quaternion(emote.AnglesX.Value, emote.AnglesY.Value, emote.AnglesZ.Value, emote.AnglesW.Value);
                                relativeDestination.LandblockId = new LandblockId(relativeDestination.GetCell());

                                var yaw = WorldObject.Location.GetYaw();
                                relativeDestination.RotateAroundPivot(WorldObject.Location, yaw);

                                WorldObject.AdjustDungeon(relativeDestination);
                                WorldManager.ThreadSafeBlink(player, relativeDestination);
                            }
                        }
                    }
                    break;

                default:
                    log.DebugFormat("EmoteManager.Execute - Encountered Unhandled EmoteType {0} for {1} ({2})", (EmoteType)emote.Type, WorldObject.Name, WorldObject.WeenieClassId);
                    break;
            }

            return delay;
        }

        /// <summary>
        /// Selects an emote set based on category, and optional: quest, vendor, rng
        /// </summary>
        public PropertiesEmote GetEmoteSet(EmoteCategory category, string questName = null, VendorType? vendorType = null, uint? wcid = null, bool useRNG = true)
        {
            //if (Debug) Console.WriteLine($"{WorldObject.Name}.EmoteManager.GetEmoteSet({category}, {questName}, {vendorType}, {wcid}, {useRNG})");

            if (_worldObject.Biota.PropertiesEmote == null)
                return null;

            // always pull emoteSet from _worldObject
            var emoteSet = _worldObject.Biota.PropertiesEmote.Where(e => e.Category == category);

            // optional criteria
            if ((category == EmoteCategory.HearChat || category == EmoteCategory.ReceiveTalkDirect) && questName != null)
                emoteSet = emoteSet.Where(e => e.Quest != null && e.Quest.Equals(questName, StringComparison.OrdinalIgnoreCase) || e.Quest == null);
            else if (questName != null)
                emoteSet = emoteSet.Where(e => e.Quest != null && e.Quest.Equals(questName, StringComparison.OrdinalIgnoreCase));
            if (vendorType != null)
                emoteSet = emoteSet.Where(e => e.VendorType != null && e.VendorType.Value == vendorType);
            if (wcid != null)
                emoteSet = emoteSet.Where(e => e.WeenieClassId == wcid.Value);

            if (category == EmoteCategory.HeartBeat)
            {
                WorldObject.GetCurrentMotionState(out MotionStance currentStance, out MotionCommand currentMotion);

                emoteSet = emoteSet.Where(e => e.Style == null || e.Style == currentStance);
                emoteSet = emoteSet.Where(e => e.Substyle == null || e.Substyle == currentMotion);
            }

            if (category == EmoteCategory.WoundedTaunt)
            {
                if (_worldObject is Creature creature)
                    emoteSet = emoteSet.Where(e => creature.Health.Percent >= e.MinHealth && creature.Health.Percent <= e.MaxHealth);
            }

            if (useRNG)
            {
                var rng = ThreadSafeRandom.Next(0.0f, 1.0f);
                emoteSet = emoteSet.Where(e => e.Probability > rng).OrderBy(e => e.Probability);
                //emoteSet = emoteSet.Where(e => e.Probability >= rng);
            }

            return emoteSet.FirstOrDefault();
        }

        /// <summary>
        /// Convenience wrapper between GetEmoteSet and ExecututeEmoteSet
        /// </summary>
        public void ExecuteEmoteSet(EmoteCategory category, string quest = null, WorldObject targetObject = null, bool nested = false, float campBonus = 1.0f, PropertiesEmote rootEmoteSet = null)
        {
            //if (Debug) Console.WriteLine($"{WorldObject.Name}.EmoteManager.ExecuteEmoteSet({category}, {quest}, {targetObject}, {nested})");

            var emoteSet = GetEmoteSet(category, quest);

            if (emoteSet == null) return;

            if (rootEmoteSet == null)
                rootEmoteSet = emoteSet;

            // TODO: revisit if nested chains need to propagate timers
            ExecuteEmoteSet(emoteSet, targetObject, nested, campBonus, rootEmoteSet);
        }

        /// <summary>
        /// Executes a set of emotes to run with delays
        /// </summary>
        /// <param name="emoteSet">A list of emotes to execute</param>
        /// <param name="targetObject">An optional target, usually player</param>
        /// <param name="actionChain">For adding delays between emotes</param>
        public bool ExecuteEmoteSet(PropertiesEmote emoteSet, WorldObject targetObject = null, bool nested = false, float campBonus = 1.0f, PropertiesEmote rootEmoteSet = null)
        {
            //if (Debug) Console.WriteLine($"{WorldObject.Name}.EmoteManager.ExecuteEmoteSet({emoteSet}, {targetObject}, {nested})");

            // detect busy state
            // TODO: maybe eventually we should consider having categories that can be queued?
            // there are some categories that shouldn't be queued, like heartbeats...
            if (ColoTraceOn(emoteSet))
                ColoTraceLog($"EMOTE {(EmoteCategory)emoteSet.Category} quest='{emoteSet.Quest}' target={targetObject?.Name ?? "-"} nested={nested}{(IsBusy && !nested ? "  ** DROPPED - BUSY **" : "")}");

            if (IsBusy && !nested) return false;

            if (rootEmoteSet == null)
                rootEmoteSet = emoteSet;

            // Hard recursion cap. The Quest==Message check in Enqueue only catches a set that branches to itself;
            // an A -> B -> A loop (or a loop starting from a Use/Generation set with no quest name) recurses
            // synchronously until the stack overflows, which cannot be caught and aborts the whole process.
            if (Nested >= MaxEmoteNesting)
            {
                var rootInfo = rootEmoteSet != null ? $"{(EmoteCategory)rootEmoteSet.Category} '{rootEmoteSet.Quest}'" : "-";
                log.Error($"[EMOTE] {WorldObject?.Name} 0x{WorldObject?.Guid}:{WorldObject?.WeenieClassId} emote nesting hit {MaxEmoteNesting}, loop aborted. " +
                          $"root set: {rootInfo}; set that would recurse: {(EmoteCategory)emoteSet.Category} '{emoteSet.Quest}'; " +
                          $"target={targetObject?.Name ?? "null"}. Fix the looping emote content on this weenie.");
                return false;
            }

            // start action chain
            Nested++;
            Enqueue(emoteSet, targetObject, 0, 0.0f, campBonus, rootEmoteSet);

            return true;
        }

        public void Enqueue(PropertiesEmote emoteSet, WorldObject targetObject, int emoteIdx = 0, float delay = 0.0f, float campBonus = 1.0f, PropertiesEmote rootEmoteSet = null)
        {
            //if (Debug) Console.WriteLine($"{WorldObject.Name}.EmoteManager.Enqueue({emoteSet}, {targetObject}, {emoteIdx}, {delay})");

            if (emoteSet == null)
            {
                Nested--;
                return;
            }

            IsBusy = true;

            var emote = emoteSet.PropertiesEmoteAction.ElementAt(emoteIdx);

            if (Nested > 75 && !string.IsNullOrEmpty(emoteSet.Quest) && emoteSet.Quest == emote.Message && EmoteIsBranchingType(emote))
            {
                var emoteStack = $"{emoteSet.Category}: {emoteSet.Quest}\n";
                foreach (var e in emoteSet.PropertiesEmoteAction)
                    emoteStack += $"       - {(EmoteType)emote.Type}{(string.IsNullOrEmpty(emote.Message) ? "" : $": {emote.Message}")}\n";

                log.Error($"[EMOTE] {WorldObject.Name}.EmoteManager.Enqueue(): Nested > 75, possible Infinite loop detected and aborted on 0x{WorldObject.Guid}:{WorldObject.WeenieClassId}\n-> {emoteStack}");

                Nested--;

                if (Nested == 0)
                    IsBusy = false;

                return;
            }

            if (delay + emote.Delay > 0)
            {
                var actionChain = new ActionChain();

                if (Debug)
                    actionChain.AddAction(WorldObject, () => Console.Write($"{emote.Delay} - "));

                // delay = post-delay from actual time of previous emote
                // emote.Delay = pre-delay for current emote
                actionChain.AddDelaySeconds(delay + emote.Delay);

                actionChain.AddAction(WorldObject, () => DoEnqueue(emoteSet, targetObject, emoteIdx, emote, campBonus, rootEmoteSet));
                actionChain.EnqueueChain();
            }
            else
            {
                DoEnqueue(emoteSet, targetObject, emoteIdx, emote, campBonus, rootEmoteSet);
            }
        }

        /// <summary>
        /// This should only be called by Enqueue
        /// </summary>
        private void DoEnqueue(PropertiesEmote emoteSet, WorldObject targetObject, int emoteIdx, PropertiesEmoteAction emote, float campBonus, PropertiesEmote rootEmoteSet = null)
        {
            if (Debug)
                Console.Write($"{(EmoteType)emote.Type}");

            //if (!string.IsNullOrEmpty(emoteSet.Quest) && emoteSet.Quest == emote.Message && EmoteIsBranchingType(emote))
            //{
            //    log.Error($"[EMOTE] {WorldObject.Name}.EmoteManager.DoEnqueue(): Infinite loop detected on 0x{WorldObject.Guid}:{WorldObject.WeenieClassId}\n-> {emoteSet.Category}: {emoteSet.Quest} to {(EmoteType)emote.Type}: {emote.Message}");

            //    Nested--;

            //    if (Nested == 0)
            //        IsBusy = false;

            //    return;
            //}

            if (ColoTraceOn(emoteSet))
                ColoTraceLog($"  ACTION [{emoteIdx}] {(EmoteType)emote.Type}{(string.IsNullOrEmpty(emote.Message) ? "" : $" '{emote.Message}'")} (from {(EmoteCategory)emoteSet.Category} '{emoteSet.Quest}')");

            float nextDelay;
            try
            {
                nextDelay = ExecuteEmote(emoteSet, emote, targetObject, campBonus, rootEmoteSet);
            }
            catch (Exception ex)
            {
                // A bad emote must never take down the world thread. Log which one it was so the content can be fixed.
                log.Error($"[EMOTE] 0x{WorldObject?.Guid}:{WorldObject?.Name} ({WorldObject?.WeenieClassId}) " +
                          $"{(EmoteType)emote.Type} idx={emoteIdx} msg='{emote.Message}' " +
                          $"set={(EmoteCategory)emoteSet.Category} quest='{emoteSet.Quest}' " +
                          $"target={targetObject?.Name ?? "null"}\n{ex}");

                // abort the rest of this set and release the busy state the same way a finished set does
                Nested--;
                if (Nested <= 0)
                {
                    Nested = 0;
                    IsBusy = false;
                }
                return;
            }

            if (Debug)
                Console.WriteLine($" - { nextDelay}");

            if (emoteIdx < emoteSet.PropertiesEmoteAction.Count - 1)
                Enqueue(emoteSet, targetObject, emoteIdx + 1, nextDelay, campBonus, rootEmoteSet);
            else
            {
                if (nextDelay > 0)
                {
                    var delayChain = new ActionChain();
                    delayChain.AddDelaySeconds(nextDelay);
                    delayChain.AddAction(WorldObject, () =>
                    {
                        Nested--;

                        if (Nested == 0)
                            IsBusy = false;
                    });
                    delayChain.EnqueueChain();
                }
                else
                {
                    Nested--;

                    if (Nested == 0)
                        IsBusy = false;
                }
            }
        }

        private static bool EmoteRequiresQuestName(EmoteType emoteType)
        {
            switch (emoteType)
            {
                case EmoteType.UpdateQuest:
                case EmoteType.InqQuest:
                case EmoteType.StampQuest:
                case EmoteType.InqQuestSolves:
                case EmoteType.EraseQuest:
                case EmoteType.DecrementQuest:
                case EmoteType.IncrementQuest:
                case EmoteType.InqFellowQuest:
                case EmoteType.UpdateFellowQuest:
                case EmoteType.StampFellowQuest:
                case EmoteType.SetQuestCompletions:
                case EmoteType.UpdateMyQuest:
                case EmoteType.InqMyQuest:
                case EmoteType.StampMyQuest:
                case EmoteType.InqMyQuestSolves:
                case EmoteType.EraseMyQuest:
                case EmoteType.DecrementMyQuest:
                case EmoteType.IncrementMyQuest:
                case EmoteType.SetMyQuestCompletions:
                case EmoteType.InqQuestBitsOn:
                case EmoteType.InqQuestBitsOff:
                case EmoteType.InqMyQuestBitsOn:
                case EmoteType.InqMyQuestBitsOff:
                case EmoteType.SetQuestBitsOn:
                case EmoteType.SetQuestBitsOff:
                case EmoteType.SetMyQuestBitsOn:
                case EmoteType.SetMyQuestBitsOff:
                    return true;
                default:
                    return false;
            }
        }

        private bool EmoteIsBranchingType(PropertiesEmoteAction emote)
        {
            if (emote == null)
                return false;

            var emoteType = (EmoteType)emote.Type;

            switch (emoteType)
            {
                case EmoteType.UpdateQuest:
                case EmoteType.InqQuest:
                case EmoteType.InqQuestSolves:
                case EmoteType.InqBoolStat:
                case EmoteType.InqIntStat:
                case EmoteType.InqFloatStat:
                case EmoteType.InqStringStat:
                case EmoteType.InqAttributeStat:
                case EmoteType.InqRawAttributeStat:
                case EmoteType.InqSecondaryAttributeStat:
                case EmoteType.InqRawSecondaryAttributeStat:
                case EmoteType.InqSkillStat:
                case EmoteType.InqRawSkillStat:
                case EmoteType.InqSkillTrained:
                case EmoteType.InqSkillSpecialized:
                case EmoteType.InqEvent:
                case EmoteType.InqFellowQuest:
                case EmoteType.InqFellowNum:
                case EmoteType.UpdateFellowQuest:
                case EmoteType.Goto:
                case EmoteType.InqNumCharacterTitles:
                case EmoteType.InqYesNo:
                case EmoteType.InqOwnsItems:
                case EmoteType.UpdateMyQuest:
                case EmoteType.InqMyQuest:
                case EmoteType.InqMyQuestSolves:
                case EmoteType.InqPackSpace:
                case EmoteType.InqQuestBitsOn:
                case EmoteType.InqQuestBitsOff:
                case EmoteType.InqMyQuestBitsOn:
                case EmoteType.InqMyQuestBitsOff:
                case EmoteType.InqInt64Stat:
                case EmoteType.InqContractsFull:
                    return true;
                default:
                    return false;
            }
        }

        public bool HasValidTestNoQuality(string testName) => GetEmoteSet(EmoteCategory.TestNoQuality, testName) != null;

        public bool HasValidTestNoFellow(string testName) => GetEmoteSet(EmoteCategory.TestNoFellow, testName) != null;

        /// <summary>
        /// The maximum animation range of the client
        /// Motions broadcast outside of this range will be automatically queued by client
        /// </summary>
        public static float ClientMaxAnimRange = 96.0f;     // verify: same indoors?

        /// <summary>
        /// The client automatically queues animations that are broadcast outside of 96.0f range
        /// Normally we exclude these emotes from being broadcast outside this range,
        /// but for certain emotes (like monsters going to sleep) we want to always broadcast / enqueue
        /// </summary>
        public static HashSet<MotionCommand> MotionQueue = new HashSet<MotionCommand>()
        {
            MotionCommand.Sleeping
        };

        public void DoVendorEmote(VendorType vendorType, WorldObject target)
        {
            var vendorSet = GetEmoteSet(EmoteCategory.Vendor, null, vendorType);
            var heartbeatSet = GetEmoteSet(EmoteCategory.Vendor, null, VendorType.Heartbeat);

            ExecuteEmoteSet(vendorSet, target);
            ExecuteEmoteSet(heartbeatSet, target, true);
        }

        public IEnumerable<PropertiesEmote> Emotes(EmoteCategory emoteCategory)
        {
            return WorldObject.Biota.PropertiesEmote.Where(x => x.Category == emoteCategory);
        }

        public string Replace(string message, WorldObject source, WorldObject target, string quest)
        {
            var result = message;

            if (result == null)
            {
                log.Warn($"[EMOTE] {WorldObject.Name}.EmoteManager.Replace(message, {source.Name}:0x{source.Guid}:{source.WeenieClassId}, {target.Name}:0x{target.Guid}:{target.WeenieClassId}, {quest}): message was null!");
                return "";
            }

            var sourceName = source != null ? source.Name : "";
            var targetName = target != null ? target.Name : "";

            result = result.Replace("%n", sourceName);
            result = result.Replace("%mn", sourceName);
            result = result.Replace("%s", targetName);
            result = result.Replace("%tn", targetName);

            var sourceLevel = source != null ? $"{source.Level ?? 0}" : "";
            var targetLevel = target != null ? $"{target.Level ?? 0}" : "";
            result = result.Replace("%ml", sourceLevel);
            result = result.Replace("%tl", targetLevel);

            //var sourceTemplate = source != null ? source.GetProperty(PropertyString.Title) : "";
            //var targetTemplate = source != null ? target.GetProperty(PropertyString.Title) : "";
            var sourceTemplate = source != null ? source.GetProperty(PropertyString.Template) : "";
            var targetTemplate = target != null ? target.GetProperty(PropertyString.Template) : "";
            result = result.Replace("%mt", sourceTemplate);
            result = result.Replace("%tt", targetTemplate);

            var sourceHeritage = source != null ? source.HeritageGroupName : "";
            var targetHeritage = target != null ? target.HeritageGroupName : "";
            result = result.Replace("%mh", sourceHeritage);
            result = result.Replace("%th", targetHeritage);

            //result = result.Replace("%mf", $"{source.GetProperty(PropertyString.Fellowship)}");
            //result = result.Replace("%tf", $"{target.GetProperty(PropertyString.Fellowship)}");

            //result = result.Replace("%l", $"{???}"); // level?
            //result = result.Replace("%pk", $"{???}"); // pk status?
            //result = result.Replace("%a", $"{???}"); // allegiance?
            //result = result.Replace("%p", $"{???}"); // patron?

            // Find quest in standard or LSD custom usage for %tqt and %CDtime
            var embeddedQuestName = result.Contains("@") ? message.Split("@")[0] : null;
            var questName = !string.IsNullOrWhiteSpace(embeddedQuestName) ? embeddedQuestName : quest;

            // LSD custom tqt usage
            result = result.Replace($"{questName}@%tqt", "You may complete this quest again in %tqt.", StringComparison.OrdinalIgnoreCase);

            // LSD custom CDtime variable
            if (result.Contains("%CDtime"))
                result = result.Replace($"{questName}@", "", StringComparison.OrdinalIgnoreCase);

            if (target is Player targetPlayer)
            {
                result = result.Replace("%tqt", !string.IsNullOrWhiteSpace(quest) ? targetPlayer.QuestManager.GetNextSolveTime(questName).GetFriendlyString() : "");
                
                result = result.Replace("%CDtime", !string.IsNullOrWhiteSpace(quest) ? targetPlayer.QuestManager.GetNextSolveTime(questName).GetFriendlyString() : "");

                result = result.Replace("%tf", $"{(targetPlayer.Fellowship != null ? targetPlayer.Fellowship.FellowshipName : "")}");

                result = result.Replace("%fqt", !string.IsNullOrWhiteSpace(quest) && targetPlayer.Fellowship != null ? targetPlayer.Fellowship.QuestManager.GetNextSolveTime(questName).GetFriendlyString() : "");

                result = result.Replace("%tqm", !string.IsNullOrWhiteSpace(quest) ? targetPlayer.QuestManager.GetMaxSolves(questName).ToString() : "");

                result = result.Replace("%tqc", !string.IsNullOrWhiteSpace(quest) ? targetPlayer.QuestManager.GetCurrentSolves(questName).ToString() : "");
            }

            if (source is Creature sourceCreature)
            {
                result = result.Replace("%mqt", !string.IsNullOrWhiteSpace(quest) ? sourceCreature.QuestManager.GetNextSolveTime(questName).GetFriendlyString() : "");

                result = result.Replace("%mxqt", !string.IsNullOrWhiteSpace(quest) ? sourceCreature.QuestManager.GetNextSolveTime(questName).GetFriendlyLongString() : "");

                //result = result.Replace("%CDtime", !string.IsNullOrWhiteSpace(quest) ? sourceCreature.QuestManager.GetNextSolveTime(questName).GetFriendlyString() : "");

                result = result.Replace("%mqc", !string.IsNullOrWhiteSpace(quest) ? sourceCreature.QuestManager.GetCurrentSolves(questName).ToString() : "");
            }

            return result;
        }

        /// <summary>
        /// Returns the creature target for quest emotes
        /// </summary>
        public static Creature GetQuestTarget(EmoteType emote, Creature target, Creature self)
        {
            switch (emote)
            {
                // MyQuest always targets self
                case EmoteType.DecrementMyQuest:
                case EmoteType.EraseMyQuest:
                case EmoteType.IncrementMyQuest:
                case EmoteType.InqMyQuest:
                case EmoteType.InqMyQuestBitsOff:
                case EmoteType.InqMyQuestBitsOn:
                case EmoteType.InqMyQuestSolves:
                case EmoteType.SetMyQuestBitsOff:
                case EmoteType.SetMyQuestBitsOn:
                case EmoteType.SetMyQuestCompletions:
                case EmoteType.StampMyQuest:
                case EmoteType.UpdateMyQuest:

                    return self;

                default:

                    return target ?? self;
            }
        }

        private WorldObject GetSpellTarget(Spell spell, WorldObject target)
        {
            var targetSelf = spell.Flags.HasFlag(SpellFlags.SelfTargeted);
            var untargeted = spell.NonComponentTargetType == ItemType.None;

            var spellTarget = target;
            if (untargeted)
                spellTarget = null;
            else if (targetSelf)
                spellTarget = WorldObject;

            return spellTarget;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void HeartBeat()
        {
            // player didn't do idle emotes in retail?
            if (WorldObject is Player)
                return;

            if (IsColoCoordinator)
            {
                ColoBusyGuard();
                ColoWatchdog();
                ColoSyncWaveCount();
            }

            // CUSTOM: the Colo coordinator must always run its heartbeat emotes (they advance the waves),
            // even if something (e.g. the wi-flag aggro scan) woke it up.
            if (WorldObject is Creature creature && creature.IsAwake && !IsColoCoordinator)
                return;

            ExecuteEmoteSet(EmoteCategory.HeartBeat);
        }

        // ------------------------------------------------------------------------------------------
        // CUSTOM: Colosseum wave watchdog.
        // The Colo Room Coordinator (71701) and Final Room Coordinator (71702) advance waves by keeping a
        // running count (ColoCrittersKilled) built from ColoCritterSpawned / ColoCritterKilled signals.
        // One lost signal leaves the count off forever and the room stalls. This watchdog checks what is
        // ACTUALLY alive in the coordinator's own room (by generator lineage, not by distance) and fixes it:
        //   1) Wave in progress, count not 0, but nothing of this room is alive or pending for ColoStuckSeconds
        //      -> set the count to 0 so the coordinator's own emotes advance to the next wave.
        //   2) A SpawnWave signal was sent but nothing from this room ever appeared for ColoStuckSeconds
        //      -> re-send that SpawnWave signal.
        // Normal rooms are never touched: it only acts when a room has been dead-stuck for ColoStuckSeconds.
        // ------------------------------------------------------------------------------------------
        private const uint ColoCoordinatorWcid = 71701;
        private const uint ColoFinalCoordinatorWcid = 71702;
        private const double ColoStuckSeconds = 30.0;
        private const int ColoMaxResends = 3;

        private bool IsColoCoordinator => WorldObject != null && (WorldObject.WeenieClassId == ColoCoordinatorWcid || WorldObject.WeenieClassId == ColoFinalCoordinatorWcid);

        private string coloLastWaveSignal;
        private DateTime coloLastWaveSignalTime;
        private int coloResendCount;
        private DateTime? coloStuckSince;
        private bool coloWaveSeenAlive;
        private DateTime? coloEmptySince;
        private const double ColoEmptyGraceSeconds = 12.0;
        private DateTime lastColoFanoutBroadcast;
        private const double ColoFanoutCooldownSeconds = 30.0;

        private DateTime? coloBusySince;

        /// <summary>
        /// The coordinator's emotes have no delays, so it should never stay busy across heartbeats.
        /// If it does (leaked Nested count), every heartbeat and signal would be dropped forever - reset it.
        /// </summary>
        private void ColoBusyGuard()
        {
            if (!IsBusy)
            {
                coloBusySince = null;
                return;
            }

            if (coloBusySince == null)
            {
                coloBusySince = DateTime.UtcNow;
                return;
            }

            if ((DateTime.UtcNow - coloBusySince.Value).TotalSeconds >= ColoStuckSeconds)
            {
                if (ColoTrace) log.Warn($"[COLO] {WorldObject.Name} 0x{WorldObject.Guid}: emote manager stuck busy for {ColoStuckSeconds}s (Nested={Nested}). Resetting.");
                Nested = 0;
                IsBusy = false;
                coloBusySince = null;
            }
        }

        private static bool ColoIsLive(WorldObject wo) => wo != null && !wo.IsDestroyed && wo.CurrentLandblock != null;

        /// <summary>
        /// Returns the linkable wave generators that belong to this coordinator's room
        /// (same rule the server uses to deliver this coordinator's SpawnWave signals).
        /// </summary>
        private List<WorldObject> ColoRoomWaveGens()
        {
            var result = new List<WorldObject>();

            var landblock = WorldObject.CurrentLandblock;
            if (landblock == null) return result;

            foreach (var wo in landblock.GetAllWorldObjectsForDiagnostics())
            {
                if (wo == null || !wo.IsGenerator || !wo.HearLocalSignals)
                    continue;

                if (wo.WeenieClassName == null || wo.WeenieClassName.IndexOf("coloarenalinkablewave", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!WorldObject.IsWithinUseRadiusOf(wo, wo.HearLocalSignalsRadius))
                    continue;

                result.Add(wo);
            }
            return result;
        }

        private static bool ColoListensFor(WorldObject wo, string signal)
        {
            var emotes = wo?.Biota?.PropertiesEmote;
            if (emotes == null) return false;

            return emotes.Any(e => e.Category == EmoteCategory.ReceiveLocalSignal && e.Quest != null && e.Quest.Equals(signal, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Logs the full state of the room's wave generators, removes dead/ghost entries from their profiles,
        /// and directly fires the wave generator that should have answered 'signal'.
        /// </summary>
        private void ColoRepairAndForceWave(string signal)
        {
            foreach (var waveGen in ColoRoomWaveGens())
            {
                var listens = ColoListensFor(waveGen, signal);
                var ghosts = 0;
                var info = new System.Text.StringBuilder();

                for (var i = 0; i < waveGen.GeneratorProfiles.Count; i++)
                {
                    var p = waveGen.GeneratorProfiles[i];
                    info.Append($" p{i}[wcid={p.WeenieClassId} spawned={p.Spawned.Count} queue={p.SpawnQueue.Count} max={p.MaxCreate} available={p.IsAvailable} nextAvail={p.NextAvailable:HH:mm:ss}]");

                    foreach (var key in p.Spawned.Keys.ToList())
                    {
                        if (!ColoIsLive(p.Spawned[key].TryGetWorldObject()))
                        {
                            p.Spawned.Remove(key);
                            ghosts++;
                        }
                    }
                }

                if (listens || ghosts > 0)
                    if (ColoTrace) log.Warn($"[COLO] repair: {waveGen.Name} 0x{waveGen.Guid} listensFor '{signal}'={listens} disabled={waveGen.GeneratorDisabled} poweringUp={waveGen.CurrentlyPoweringUp} busy={waveGen.EmoteManager.IsBusy} init={waveGen.InitCreate} max={waveGen.MaxCreate} current={waveGen.CurrentCreate} ghostsRemoved={ghosts} |{info}");

                if (!listens)
                    continue;

                // clear a stuck busy flag so its own emotes work again next time
                if (waveGen.EmoteManager.IsBusy)
                {
                    waveGen.EmoteManager.Nested = 0;
                    waveGen.EmoteManager.IsBusy = false;
                }

                foreach (var p in waveGen.GeneratorProfiles)
                    if (p.Spawned.Count == 0 && p.SpawnQueue.Count == 0)
                        p.NextAvailable = DateTime.UtcNow.AddSeconds(-1);

                waveGen.GeneratorDisabled = false;
                waveGen.Generator_Generate();

                if (ColoTrace) log.Warn($"[COLO] repair: forced Generate on {waveGen.Name} 0x{waveGen.Guid} -> current={waveGen.CurrentCreate}");
            }
        }

        private void ColoTrackSignal(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            if (message.StartsWith("SpawnWave", StringComparison.OrdinalIgnoreCase))
            {
                coloLastWaveSignal = message;
                coloLastWaveSignalTime = DateTime.UtcNow;
                coloResendCount = 0;
                coloStuckSince = null;
                coloWaveSeenAlive = false;
                coloEmptySince = null;
            }
            else if (message.Equals("RevealPortal", StringComparison.OrdinalIgnoreCase))
            {
                // room finished - nothing more to watch
                coloLastWaveSignal = null;
                coloStuckSince = null;
            }
        }

        /// <summary>
        /// Counts this room's live monsters and pending spawns by following the generator chain:
        /// linkable wave generators that hear this coordinator -> the room spawn generators they made -> the monsters those made.
        /// </summary>
        private void ColoCountRoom(out int alive, out int pending, out int spawnGens)
        {
            alive = 0; pending = 0; spawnGens = 0;

            var landblock = WorldObject.CurrentLandblock;
            if (landblock == null) return;

            foreach (var waveGen in landblock.GetAllWorldObjectsForDiagnostics())
            {
                if (waveGen == null || !waveGen.IsGenerator || !waveGen.HearLocalSignals)
                    continue;

                if (waveGen.WeenieClassName == null || waveGen.WeenieClassName.IndexOf("coloarenalinkablewave", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // same rule the server uses to deliver this coordinator's SpawnWave signals
                if (!WorldObject.IsWithinUseRadiusOf(waveGen, waveGen.HearLocalSignalsRadius))
                    continue;

                foreach (var waveProfile in waveGen.GeneratorProfiles)
                {
                    pending += waveProfile.SpawnQueue.Count;

                    foreach (var sgInfo in waveProfile.Spawned.Values)
                    {
                        var spawnGen = sgInfo.TryGetWorldObject();
                        if (!ColoIsLive(spawnGen)) continue;

                        spawnGens++;

                        if (spawnGen.GeneratorProfiles == null) continue;

                        foreach (var spawnProfile in spawnGen.GeneratorProfiles)
                        {
                            pending += spawnProfile.SpawnQueue.Count;

                            foreach (var mobInfo in spawnProfile.Spawned.Values)
                            {
                                if (mobInfo.TryGetWorldObject() is Creature mob && !mob.IsDead && ColoIsLive(mob))
                                    alive++;
                            }
                        }
                    }
                }
            }
        }

        private void ColoWatchdog()
        {
            if (!(WorldObject is Creature coordinator) || coordinator.QuestManager == null || coordinator.CurrentLandblock == null)
                return;

            // nothing sent yet, or room already finished
            if (coloLastWaveSignal == null)
                return;

            ColoCountRoom(out var alive, out var pending, out var spawnGens);

            if (ColoTrace)
                ColoTraceLog($"WATCHDOG last='{coloLastWaveSignal}' alive={alive} pending={pending} spawnGens={spawnGens} waveInProgress={coordinator.QuestManager.HasQuest("ColoWaveInProgress")} countZero={coordinator.QuestManager.HasQuestSolves("ColoCrittersKilled", 0, 0)} stuckFor={(coloStuckSince == null ? 0 : (DateTime.UtcNow - coloStuckSince.Value).TotalSeconds):0}s");

            // anything alive or about to spawn = room is working, reset the stuck timer
            if (alive > 0 || pending > 0)
            {
                coloStuckSince = null;
                return;
            }

            var waveInProgress = coordinator.QuestManager.HasQuest("ColoWaveInProgress");
            var countIsZero = coordinator.QuestManager.HasQuestSolves("ColoCrittersKilled", 0, 0);

            // case 1: normal end-of-wave (count 0) - coordinator's own emotes handle it
            if (waveInProgress && countIsZero)
            {
                coloStuckSince = null;
                return;
            }

            if (coloStuckSince == null)
            {
                coloStuckSince = DateTime.UtcNow;
                return;
            }

            if ((DateTime.UtcNow - coloStuckSince.Value).TotalSeconds < ColoStuckSeconds)
                return;

            coloStuckSince = null;

            if (waveInProgress)
            {
                // everything dead but the count is off -> fix the count
                if (ColoTrace) log.Warn($"[COLO] {WorldObject.Name} 0x{WorldObject.Guid} ({WorldObject.Location?.ToLOCString()}): room stuck with nothing alive but ColoCrittersKilled != 0 after '{coloLastWaveSignal}'. Resetting count to 0.");
                coordinator.QuestManager.SetQuestCompletions("ColoCrittersKilled", 0);
            }
            else if ((DateTime.UtcNow - coloLastWaveSignalTime).TotalSeconds >= ColoStuckSeconds && coloResendCount < ColoMaxResends)
            {
                // a wave was called for but nothing of this room ever showed up -> call it again
                coloResendCount++;
                if (ColoTrace) log.Warn($"[COLO] {WorldObject.Name} 0x{WorldObject.Guid} ({WorldObject.Location?.ToLOCString()}): '{coloLastWaveSignal}' produced nothing (spawnGens={spawnGens}). Attempt {coloResendCount}/{ColoMaxResends}: {(coloResendCount == 1 ? "re-sending signal" : "repairing wave generators + forcing spawn")}.");

                if (coloResendCount == 1)
                    coordinator.CurrentLandblock.EmitSignal(WorldObject, coloLastWaveSignal);
                else
                    ColoRepairAndForceWave(coloLastWaveSignal);

                coloLastWaveSignalTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// CUSTOM: keeps the coordinator's ColoCrittersKilled count equal to what is ACTUALLY alive in
        /// its room (from ColoCountRoom, by generator lineage) instead of trusting the
        /// ColoCritterSpawned / ColoCritterKilled LocalSignals. Those signals can be dropped (an object
        /// stuck busy, or out of hearing radius) and a single lost one leaves the count off forever, so
        /// the coordinator never sees the wave clear and the room stalls - the bug that reliably hit the
        /// rooms with the most spawns (e.g. room 16). With the count pinned to reality every heartbeat,
        /// the coordinator's own HeartBeat emotes advance the wave exactly when the room is clear, and a
        /// lost signal can no longer stall anything.
        ///
        /// The "seen alive" guard means a freshly-signalled wave is never advanced before it has actually
        /// spawned. Once the wave has appeared, alive == 0 lets the emotes advance - whether the room is
        /// truly empty, or the player cleared the first half and the second half is still only queued. The
        /// latter is the intended "clear the first half fast to skip the rest" reward, so it is preserved.
        /// </summary>
        private void ColoSyncWaveCount()
        {
            // no wave active, or the room already finished (RevealPortal clears this)
            if (coloLastWaveSignal == null)
                return;

            if (!(WorldObject is Creature coordinator) || coordinator.QuestManager == null || coordinator.CurrentLandblock == null)
                return;

            ColoCountRoom(out var alive, out var pending, out var spawnGens);

            if (alive > 0)
            {
                // the wave has materialized - from here on, alive == 0 means the player cleared it
                coloWaveSeenAlive = true;
                coloEmptySince = null;
            }
            else if (pending > 0 || spawnGens > 0)
            {
                // alive == 0 but the wave (or its skipped-able 2nd half) is still spawning - keep waiting
                coloEmptySince = null;
            }
            else
            {
                // room is completely empty: nothing alive, nothing queued, no spawn generators left.
                // If we already saw the wave fill, it is cleared now. If we NEVER saw it (its monsters are
                // invisible to the room count for any reason, or nothing ever spawned), force it through
                // after a short grace so a wave can never leave a room hung. This is the hard guarantee
                // that no room gets permanently stuck, regardless of why the signals failed.
                if (coloEmptySince == null)
                    coloEmptySince = DateTime.UtcNow;

                if ((DateTime.UtcNow - coloEmptySince.Value).TotalSeconds >= ColoEmptyGraceSeconds)
                    coloWaveSeenAlive = true;
            }

            if (!coloWaveSeenAlive)
                return;

            // count == reality: a dropped or never-sent ColoCritterSpawned/ColoCritterKilled signal can no longer desync it
            coordinator.QuestManager.SetQuestCompletions("ColoCrittersKilled", alive);

            // the advance emotes only run while ColoWaveInProgress is stamped; a missing ColoCritterKilled can
            // leave it erased even though the wave really happened - re-assert it so the clear registers
            if (!coordinator.QuestManager.HasQuest("ColoWaveInProgress"))
                coordinator.QuestManager.Stamp("ColoWaveInProgress");
        }

        public void OnUse(Creature activator)
        {
            ExecuteEmoteSet(EmoteCategory.Use, null, activator);
        }

        public void OnPortal(Creature activator)
        {
            IsBusy = false;

            ExecuteEmoteSet(EmoteCategory.Portal, null, activator);
        }

        public void OnActivation(Creature activator)
        {
            ExecuteEmoteSet(EmoteCategory.Activation, null, activator);
        }

        public void OnGeneration()
        {
            ExecuteEmoteSet(EmoteCategory.Generation, null, null);
        }

        public void OnWield(Creature wielder)
        {
            ExecuteEmoteSet(EmoteCategory.Wield, null, wielder);
        }

        public void OnUnwield(Creature wielder)
        {
            ExecuteEmoteSet(EmoteCategory.UnWield, null, wielder);
        }

        public void OnPickup(Creature initiator)
        {
            ExecuteEmoteSet(EmoteCategory.PickUp, null, initiator);
        }

        public void OnDrop(Creature dropper)
        {
            ExecuteEmoteSet(EmoteCategory.Drop, null, dropper);
        }

        /// <summary>
        /// Called when an idle mob becomes alerted by a player
        /// and initially wakes up
        /// </summary>
        public void OnWakeUp(Creature target)
        {
            ExecuteEmoteSet(EmoteCategory.Scream, null, target);
        }

        /// <summary>
        /// Called when a monster switches targets
        /// </summary>
        public void OnNewEnemy(WorldObject newEnemy)
        {
            ExecuteEmoteSet(EmoteCategory.NewEnemy, null, newEnemy);
        }

        /// <summary>
        /// Called when a monster completes an attack
        /// </summary>
        public void OnAttack(WorldObject target)
        {
            ExecuteEmoteSet(EmoteCategory.Taunt, null, target);
        }

        public void OnDamage(Creature attacker)
        {
            ExecuteEmoteSet(EmoteCategory.WoundedTaunt, null, attacker);
        }

        public void OnReceiveCritical(Creature attacker)
        {
            ExecuteEmoteSet(EmoteCategory.ReceiveCritical, null, attacker);
        }

        public void OnResistSpell(Creature attacker)
        {
            ExecuteEmoteSet(EmoteCategory.ResistSpell, null, attacker);
        }

        public void OnDeath(DamageHistoryInfo lastDamagerInfo)
        {
            IsBusy = false;

            var lastDamager = lastDamagerInfo?.TryGetPetOwnerOrAttacker();

            ExecuteEmoteSet(EmoteCategory.Death, null, lastDamager);
        }

        /// <summary>
        /// Called when a monster kills a player
        /// </summary>
        public void OnKill(Player player)
        {
            ExecuteEmoteSet(EmoteCategory.KillTaunt, null, player);
        }

        /// <summary>
        /// Called when player interacts with item that has a Quest string
        /// </summary>
        public void OnQuest(Creature initiator)
        {
            var questName = WorldObject.Quest;

            var hasQuest = initiator.QuestManager.HasQuest(questName);

            if (!hasQuest)
            {
                // add new quest
                initiator.QuestManager.Update(questName);
                hasQuest = initiator.QuestManager.HasQuest(questName);
                ExecuteEmoteSet(hasQuest ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, questName, initiator);
            }
            else
            {
                // update existing quest
                var canSolve = initiator.QuestManager.CanSolve(questName);
                if (canSolve)
                    initiator.QuestManager.Stamp(questName);
                ExecuteEmoteSet(canSolve ? EmoteCategory.QuestSuccess : EmoteCategory.QuestFailure, questName, initiator);
            }
        }

        /// <summary>
        /// Called when this NPC receives a direct text message from a player
        /// </summary>
        public void OnTalkDirect(Player player, string message)
        {
            ExecuteEmoteSet(EmoteCategory.ReceiveTalkDirect, message, player);
        }

        /// <summary>
        /// Called when this NPC receives a local signal from a player
        /// </summary>
        public void OnLocalSignal(WorldObject emitter, string message)
        {
            if (ColoTrace && IsInColo(WorldObject) && !ColoIsNoiseSignal(message))
                ColoTraceLog($"HEARD signal '{message}' from {emitter?.Name} (0x{emitter?.Guid}:{emitter?.WeenieClassId}) dist={(emitter?.Location != null && WorldObject.Location != null ? emitter.Location.DistanceTo(WorldObject.Location).ToString("0.0") : "?")} busy={IsBusy}");

            TryLocalSignal(emitter, message, 0);
        }

        // ------------------------------------------------------------------------------------------
        // CUSTOM: Colosseum trace. Logs every emote, emote action, signal heard and watchdog check
        // for objects in the Colosseum landblocks (0x00AF-0x00B6). Toggle in-game with @colotrace.
        // Lines start with [COLOTRACE] so they can be searched/filtered in the log.
        // ------------------------------------------------------------------------------------------
        public static bool ColoTrace = false;   // off by default; toggle in-game with @colotrace

        // signals that fan out to every object in a room (the hourly teardown broadcast) and bury the
        // useful wave trace under tens of thousands of lines - skip them in the HEARD logging
        private static readonly HashSet<string> ColoNoiseSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ColoSelfDestruct" };
        private static bool ColoIsNoiseSignal(string message) => message != null && ColoNoiseSignals.Contains(message);

        public static bool IsInColo(WorldObject wo)
        {
            var lb = wo?.CurrentLandblock;
            if (lb == null) return false;

            var id = lb.Id.Raw >> 16;
            return id >= 0x00AF && id <= 0x00B6;
        }

        private bool ColoTraceOn(PropertiesEmote emoteSet)
        {
            if (!ColoTrace || emoteSet == null || !IsInColo(WorldObject))
                return false;

            // idle heartbeat emotes on monsters are just animations - skip them, keep coordinator heartbeats
            if (emoteSet.Category == EmoteCategory.HeartBeat && !IsColoCoordinator)
                return false;

            // a coordinator with no wave running is just idling - skip its heartbeat cascade too, so the
            // trace only shows coordinator heartbeats while a wave is actually in progress in that room
            if (emoteSet.Category == EmoteCategory.HeartBeat && IsColoCoordinator && coloLastWaveSignal == null)
                return false;

            return true;
        }

        private void ColoTraceLog(string text)
        {
            var loc = WorldObject.Location;
            log.Info($"[COLOTRACE] {WorldObject.Name} ({WorldObject.WeenieClassId} 0x{WorldObject.Guid}) @{(loc != null ? $"{loc.Cell:X8} {loc.PositionX:0.0},{loc.PositionY:0.0}" : "?")} | {text}");
        }

        // CUSTOM: a local signal that arrives while this object is busy running another emote
        // used to be silently dropped (ExecuteEmoteSet returns early when IsBusy).
        // Now it is retried every SignalRetryDelay seconds until the object is free, up to SignalRetryMax times.
        private const int SignalRetryMax = 40;          // 40 x 0.5s = up to 20 seconds of waiting
        private const float SignalRetryDelay = 0.5f;

        private void TryLocalSignal(WorldObject emitter, string message, int attempt)
        {
            if (!IsBusy)
            {
                if (attempt > 0)
                    if (ColoTrace) log.Warn($"[SIGNAL] {WorldObject.Name} (0x{WorldObject.Guid}:{WorldObject.WeenieClassId}) received delayed signal '{message}' after {attempt} retries.");

                ExecuteEmoteSet(EmoteCategory.ReceiveLocalSignal, message, emitter);
                return;
            }

            // Mass fan-out signals (the ColoSelfDestruct teardown) must never be retried. They are broadcast
            // to every object in the arena at once, so retrying each busy listener up to SignalRetryMax times
            // feeds the self-sustaining ColoSelfDestruct storm. One delivery attempt is enough for a teardown;
            // if the listener is busy, skip it rather than scheduling 40 retries.
            if (ColoIsNoiseSignal(message))
                return;

            // only bother retrying if this object actually listens for this signal
            if (GetEmoteSet(EmoteCategory.ReceiveLocalSignal, message, useRNG: false) == null)
                return;

            if (attempt >= SignalRetryMax)
            {
                if (ColoTrace) log.Error($"[SIGNAL] {WorldObject.Name} (0x{WorldObject.Guid}:{WorldObject.WeenieClassId}) DROPPED signal '{message}' - still busy after {attempt} retries.");
                return;
            }

            var retryChain = new ActionChain();
            retryChain.AddDelaySeconds(SignalRetryDelay);
            retryChain.AddAction(WorldObject, () => TryLocalSignal(emitter, message, attempt + 1));
            retryChain.EnqueueChain();
        }

        /// <summary>
        /// Called when monster exceeds the maximum distance from home position
        /// </summary>
        public void OnHomeSick(WorldObject attackTarget)
        {
            ExecuteEmoteSet(EmoteCategory.Homesick, null, attackTarget);
        }

        /// <summary>
        /// Called when this NPC hears local chat from a player
        /// </summary>
        public void OnHearChat(Player player, string message)
        {
            ExecuteEmoteSet(EmoteCategory.HearChat, message, player);
        }

        //public bool HasAntennas => WorldObject.Biota.BiotaPropertiesEmote.Count(x => x.Category == (int)EmoteCategory.ReceiveLocalSignal) > 0;

        /// <summary>
        /// Call this function when WorldObject is being used via a proxy object, e.g.: Hooker on a Hook
        /// </summary>
        public void SetProxy(WorldObject worldObject)
        {
            _proxy = worldObject;
        }

        /// <summary>
        /// Called when this object is removed from the proxy object (Hooker is picked up from Hook)
        /// </summary>
        public void ClearProxy()
        {
            _proxy = null;
        }
    }
}
