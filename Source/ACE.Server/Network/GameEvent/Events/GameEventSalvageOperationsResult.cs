using System.Collections.Generic;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.Structure;

namespace ACE.Server.Network.GameEvent.Events
{
    public class GameEventSalvageOperationsResult : GameEventMessage
    {
        public GameEventSalvageOperationsResult(Session session, Skill skill, List<SalvageMessage> messages)
            : base(GameEventType.SalvageOperationsResult, GameMessageGroup.UIQueue, session, 40) // 40 is the max seen in retail pcaps
        {
            Writer.Write((uint)skill);
            Writer.Write(0);    // not salvagable item guid list?

            // bags hold at most 100 units: report one line per bag (e.g. 172 steel => 100 + 72)
            const uint maxPerBag = 100;
            var results = new List<SalvageResult>();
            foreach (var message in messages)
            {
                var remaining = message.Amount;
                do
                {
                    var units = System.Math.Min(remaining, maxPerBag);
                    results.Add(new SalvageResult(message, units));
                    remaining -= units;
                } while (remaining > 0);
            }

            Writer.Write(results.Count);
            foreach (var result in results)
                Writer.Write(result);

            Writer.Write(skill == Skill.Salvaging ? session.Player.AugmentationBonusSalvage * 25 : 0);
        }
    }
}
