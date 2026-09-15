using LiteNetLib.Utils;

namespace Player.Networking
{
    public static class PlayerServiceStatusMessageTypes
    {
        // Top-level LiteNetLib message id. 40-43 are already owned by
        // Resources / Status Effects / Chat.
        public const ushort Status = 44;
    }

    public enum PlayerServiceKind : byte
    {
        Backend = 1,
    }

    public struct PlayerServiceStatusMessage : INetSerializable
    {
        public byte service;
        public bool available;
        public string message;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(service);
            writer.Put(available);
            writer.Put(message ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            service = reader.GetByte();
            available = reader.GetBool();
            message = reader.GetString(256);
        }

        public PlayerServiceKind Service => (PlayerServiceKind)service;
    }

    public readonly struct ClientServiceStatusNotice
    {
        public readonly PlayerServiceKind Service;
        public readonly bool Available;
        public readonly string Message;

        public ClientServiceStatusNotice(PlayerServiceKind service, bool available, string message)
        {
            Service = service;
            Available = available;
            Message = message ?? string.Empty;
        }
    }
}
