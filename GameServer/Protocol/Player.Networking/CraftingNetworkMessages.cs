using LiteNetLib.Utils;
namespace Player.Networking
{
    public static class CraftingRequestTypes { public const ushort Craft = 1000; }
    public struct CraftRequestMessage : INetSerializable
    {
        public long stationStableId; public ushort recipeDataId;
        public void Serialize(NetDataWriter writer){writer.Put(stationStableId);writer.Put(recipeDataId);} public void Deserialize(NetDataReader reader){stationStableId=reader.GetLong();recipeDataId=reader.GetUShort();}
    }
    public struct CraftResponseMessage : INetSerializable
    {
        public bool success; public string error;
        public void Serialize(NetDataWriter writer){writer.Put(success);writer.Put(error??string.Empty);} public void Deserialize(NetDataReader reader){success=reader.GetBool();error=reader.GetString(256);}
    }
}
