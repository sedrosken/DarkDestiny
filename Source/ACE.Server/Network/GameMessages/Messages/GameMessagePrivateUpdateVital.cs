using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessagePrivateUpdateVital : GameMessage
    {
        public GameMessagePrivateUpdateVital(WorldObject worldObject, CreatureVital creatureVital)
            : base(GameMessageOpcode.PrivateUpdateVital, GameMessageGroup.UIQueue, 25)
        {
            Writer.Write(worldObject.Sequences.GetNextSequence(Sequence.SequenceType.UpdateAttribute2ndLevel, creatureVital.Vital));

            Writer.Write((uint)creatureVital.Vital);
            Writer.Write(creatureVital.Ranks);
            // fold prestige set crystal max-vital bonus into InitLevel so the client panel matches the server
            var startingValue = creatureVital.StartingValue;
            if (worldObject is Player player)
                startingValue += (uint)player.GetSetCrystalVitalBonus(creatureVital.Vital);
            Writer.Write(startingValue);
            Writer.Write(creatureVital.ExperienceSpent);
            Writer.Write(creatureVital.Current);
        }
    }
}
