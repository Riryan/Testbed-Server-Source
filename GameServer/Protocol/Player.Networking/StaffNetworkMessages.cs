using LiteNetLib.Utils;
using LiteNetLibManager;

namespace Player.Networking
{
    public static class StaffRequestTypes
    {
        public const ushort Status = 600;
        public const ushort SetVisibility = 601;
        public const ushort Spectate = 602;
        public const ushort StopSpectate = 603;
    }

    public struct StaffStatusRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    public struct StaffSetVisibilityRequestMessage : INetSerializable
    {
        public byte mode;
        public void Serialize(NetDataWriter writer) => writer.Put(mode);
        public void Deserialize(NetDataReader reader) => mode = reader.GetByte();
    }

    public struct StaffSpectateRequestMessage : INetSerializable
    {
        public long targetCharacterId;
        public void Serialize(NetDataWriter writer) => writer.Put(targetCharacterId);
        public void Deserialize(NetDataReader reader) => targetCharacterId = reader.GetLong();
    }

    public struct StaffStopSpectateRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    public struct StaffStatusResponseMessage : INetSerializable
    {
        public bool success;
        public string detail;
        public string roleName;
        public ulong capabilities;
        public byte visibilityMode;
        public long spectateCharacterId;
        public uint spectateObjectId;
        public ushort spectateGeneration;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(detail ?? string.Empty);
            writer.Put(roleName ?? string.Empty);
            writer.Put(capabilities);
            writer.Put(visibilityMode);
            writer.Put(spectateCharacterId);
            writer.Put(spectateObjectId);
            writer.Put(spectateGeneration);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            detail = reader.GetString(256);
            roleName = reader.GetString(128);
            capabilities = reader.GetULong();
            visibilityMode = reader.GetByte();
            spectateCharacterId = reader.GetLong();
            spectateObjectId = reader.GetUInt();
            spectateGeneration = reader.GetUShort();
        }

        public static StaffStatusResponseMessage Failed(string detail) => new StaffStatusResponseMessage
        {
            success = false,
            detail = detail ?? string.Empty,
        };
    }
}
