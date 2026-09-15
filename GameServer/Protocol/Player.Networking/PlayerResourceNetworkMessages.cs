using System;
using LiteNetLib.Utils;
using Game.Shared.Resources;

namespace Player.Networking
{
    public static class PlayerResourceRequestTypes
    {
        public const ushort Snapshot = 300;
    }

    public static class PlayerResourceMessageTypes
    {
        // Top-level LiteNetLib message type; request IDs use a separate namespace.
        public const ushort Delta = 40;
        public const ushort Snapshot = 57;
    }

    public struct PlayerResourcesSnapshotRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    public struct CharacterResourceWire : INetSerializable
    {
        public ushort id;
        public string displayName;
        public int current;
        public int minimum;
        public int maximum;
        public byte replication;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(id);
            writer.Put(current);
            writer.Put(maximum);
        }

        public void Deserialize(NetDataReader reader)
        {
            id = reader.GetUShort();
            current = reader.GetInt();
            maximum = reader.GetInt();

            displayName = string.Empty;
            minimum = 0;
            replication = 0;
            if (id != 0 &&
                PlayerGameplaySettingsRuntime.TryGetResource(id, out GameplayResourceRateWire resource))
            {
                displayName = resource.displayName ?? string.Empty;
                minimum = resource.minimum;
                replication = resource.replication;
            }
            else
            {
                displayName = id == 0 ? string.Empty : $"resource#{id}";
            }
        }
    }

    public struct PlayerResourcesResponseMessage : INetSerializable
    {
        public const int MaxResources = 128;
        public bool success;
        public byte status;
        public string error;
        public long contentRevision;
        public long resourceRevision;
        public CharacterResourceWire[] resources;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(status);
            writer.Put(error ?? string.Empty);
            writer.Put(contentRevision);
            writer.Put(resourceRevision);
            CharacterResourceWire[] source = resources ?? Array.Empty<CharacterResourceWire>();
            int count = Math.Min(source.Length, MaxResources);
            writer.Put((byte)count);
            for (int i = 0; i < count; ++i)
                source[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            status = reader.GetByte();
            error = reader.GetString(256);
            contentRevision = reader.GetLong();
            resourceRevision = reader.GetLong();
            int count = reader.GetByte();
            if (count > MaxResources)
                throw new InvalidOperationException("resource snapshot too large");
            resources = new CharacterResourceWire[count];
            for (int i = 0; i < count; ++i)
            {
                CharacterResourceWire item = default;
                item.Deserialize(reader);
                resources[i] = item;
            }
        }

        public static PlayerResourcesResponseMessage Failed(byte status, string error) =>
            new PlayerResourcesResponseMessage
            {
                success = false,
                status = status,
                error = error ?? string.Empty,
                resources = Array.Empty<CharacterResourceWire>(),
            };
    }

    public struct PlayerResourceDeltaMessage : INetSerializable
    {
        public long resourceRevision;
        public ushort id;
        public int previous;
        public int current;
        public int minimum;
        public int maximum;
        public byte reason;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(resourceRevision);
            writer.Put(id);
            // previous/minimum are already known from the owner's authoritative cache.
            writer.Put(current);
            writer.Put(maximum);
            writer.Put(reason);
        }

        public void Deserialize(NetDataReader reader)
        {
            resourceRevision = reader.GetLong();
            id = reader.GetUShort();
            previous = 0;
            current = reader.GetInt();
            minimum = 0;
            maximum = reader.GetInt();
            reason = reader.GetByte();
        }

        public CharacterResourceId ResourceId => (CharacterResourceId)id;
        public CharacterResourceChangeReason Reason => (CharacterResourceChangeReason)reason;
    }
}
