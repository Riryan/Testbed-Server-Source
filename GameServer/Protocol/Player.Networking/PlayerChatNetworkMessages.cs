using Game.Shared.Chat;
using LiteNetLib.Utils;

namespace Player.Networking
{
    public static class PlayerChatMessageTypes
    {
        // Top-level message ids owned by Player.Networking. These are intentionally
        // isolated from the request/response id ranges used by gameplay systems.
        public const ushort Submit = 42;
        public const ushort Deliver = 43;
    }

    public struct PlayerChatSubmitMessage : INetSerializable
    {
        public byte channel;
        public string target;
        public string message;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(channel);
            writer.Put(target ?? string.Empty);
            writer.Put(message ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            channel = reader.GetByte();
            target = reader.GetString(64);
            message = reader.GetString(256);
        }

        public ChatChannel Channel => (ChatChannel)channel;
    }

    public struct PlayerChatDeliveryMessage : INetSerializable
    {
        public byte channel;
        public string sender;
        public string target;
        public string message;
        public long serverUtcTicks;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(channel);
            writer.Put(sender ?? string.Empty);
            writer.Put(target ?? string.Empty);
            writer.Put(message ?? string.Empty);
            writer.Put(serverUtcTicks);
        }

        public void Deserialize(NetDataReader reader)
        {
            channel = reader.GetByte();
            sender = reader.GetString(64);
            target = reader.GetString(64);
            message = reader.GetString(256);
            serverUtcTicks = reader.GetLong();
        }

        public ChatChannel Channel => (ChatChannel)channel;
    }

    public readonly struct ClientDisconnectNotice
    {
        public readonly byte Reason;
        public readonly int SocketError;
        public readonly string Message;
        public readonly bool Unexpected;

        public ClientDisconnectNotice(byte reason, int socketError, string message, bool unexpected)
        {
            Reason = reason;
            SocketError = socketError;
            Message = message ?? string.Empty;
            Unexpected = unexpected;
        }
    }
}
