using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessagePrivateUpdateAttribute : GameMessage
    {
        public GameMessagePrivateUpdateAttribute(WorldObject worldObject, CreatureAttribute creatureAttribute)
            : base(GameMessageOpcode.PrivateUpdateAttribute, GameMessageGroup.UIQueue, 21)
        {
            Writer.Write(worldObject.Sequences.GetNextSequence(Sequence.SequenceType.UpdateAttribute, creatureAttribute.Attribute));
            Writer.Write((uint)creatureAttribute.Attribute);
            Writer.Write(creatureAttribute.Ranks);
            // fold prestige set crystal bonus into InitLevel so the client panel matches the server
            var startingValue = creatureAttribute.StartingValue;
            if (worldObject is Player player)
                startingValue += (uint)player.GetSetCrystalAttributeBonus(creatureAttribute.Attribute);
            Writer.Write(startingValue);
            Writer.Write(creatureAttribute.ExperienceSpent);
        }
    }
}
