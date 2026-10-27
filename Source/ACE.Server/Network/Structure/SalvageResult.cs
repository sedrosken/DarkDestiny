using System;
using System.Collections.Generic;
using System.IO;
using ACE.Entity.Enum;
using ACE.Server.Entity;

namespace ACE.Server.Network.Structure
{
    public class SalvageResult
    {
        public MaterialType MaterialType;
        public double Workmanship;
        public uint Units;

        public SalvageResult(SalvageMessage message, uint units)
        {
            MaterialType = message.MaterialType;
            Workmanship = message.Workmanship / message.NumItemsInMaterial;
            Units = units;
        }
    }

    public static class SalvageResultExtensions
    {
        public static void Write(this BinaryWriter writer, SalvageResult result)
        {
            writer.Write((uint)result.MaterialType);
            writer.Write(result.Workmanship);
            writer.Write(result.Units);
        }
    }
}
