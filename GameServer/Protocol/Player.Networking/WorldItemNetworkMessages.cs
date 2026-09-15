using System;
using Game.Shared.Interactions;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace Player.Networking
{
    public static class WorldItemRequestTypes
    {
        public const ushort Snapshot = 220;
        public const ushort Loot = 221;
    }

    public static class WorldItemMessageTypes
    {
        public const ushort Snapshot = 47;
        public const ushort Delta = 48;
    }

    public struct WorldItemsSnapshotRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    public struct WorldItemLootRequestMessage : INetSerializable
    {
        public long itemInstanceId;
        public uint sequence;
        public void Serialize(NetDataWriter writer) { writer.Put(itemInstanceId); writer.Put(sequence); }
        public void Deserialize(NetDataReader reader) { itemInstanceId = reader.GetLong(); sequence = reader.GetUInt(); }
    }

    public struct WorldItemWire : INetSerializable
    {
        public long itemInstanceId;
        public long itemRevision;
        public ushort itemDataId;
        public string definitionId;
        public string displayName;
        public int quantity;
        public int durability;
        public int maxDurability;
        public float unitWeight;
        public string mapId;
        public string instanceId;
        public float positionX;
        public float positionY;
        public float positionZ;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(itemInstanceId);
            writer.Put(itemRevision);
            writer.Put(itemDataId);
            writer.Put(quantity);
            writer.Put(durability);
            // World partition identity is serialized once by the enclosing snapshot/delta.
            writer.Put(positionX);
            writer.Put(positionY);
            writer.Put(positionZ);
        }

        public void Deserialize(NetDataReader reader)
        {
            itemInstanceId = reader.GetLong();
            itemRevision = reader.GetLong();
            itemDataId = reader.GetUShort();
            quantity = reader.GetInt();
            durability = reader.GetInt();
            mapId = string.Empty;
            instanceId = string.Empty;
            positionX = reader.GetFloat();
            positionY = reader.GetFloat();
            positionZ = reader.GetFloat();

            definitionId = string.Empty;
            displayName = string.Empty;
            maxDurability = 0;
            unitWeight = 0f;
            if (itemDataId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetItem(itemDataId, out GameplayItemReferenceWire definition))
            {
                definitionId = definition.definitionId ?? string.Empty;
                displayName = definition.displayName ?? definitionId;
                maxDurability = definition.maxDurability;
                unitWeight = definition.unitWeight;
            }
            else
            {
                definitionId = itemDataId == 0 ? string.Empty : $"item#{itemDataId}";
                displayName = definitionId;
            }
        }
    }

    public struct WorldItemsSnapshotMessage : INetSerializable
    {
        public const int MaxItems = 4096;
        public bool success;
        public string error;
        public string mapId;
        public string instanceId;
        public long revision;
        public WorldItemWire[] items;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success); writer.Put(error ?? string.Empty); writer.Put(mapId ?? string.Empty); writer.Put(instanceId ?? string.Empty); writer.Put(revision);
            WorldItemWire[] source = items ?? Array.Empty<WorldItemWire>();
            if (source.Length > MaxItems) throw new InvalidOperationException("world-item snapshot exceeds protocol capacity");
            writer.Put((ushort)source.Length);
            for (int i = 0; i < source.Length; ++i) source[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool(); error = reader.GetString(256); mapId = reader.GetString(128); instanceId = reader.GetString(128); revision = reader.GetLong();
            int count = reader.GetUShort(); if (count > MaxItems) throw new InvalidOperationException("world-item snapshot too large");
            items = new WorldItemWire[count];
            for (int i = 0; i < count; ++i)
            {
                WorldItemWire item = default;
                item.Deserialize(reader);
                item.mapId = mapId;
                item.instanceId = instanceId;
                items[i] = item;
            }
        }

        public static WorldItemsSnapshotMessage Failed(string error) => new WorldItemsSnapshotMessage { success = false, error = error ?? string.Empty, mapId = string.Empty, instanceId = string.Empty, items = Array.Empty<WorldItemWire>() };
    }

    public struct WorldItemDeltaMessage : INetSerializable
    {
        public byte changeKind;
        public string mapId;
        public string instanceId;
        public long worldRevision;
        public long itemInstanceId;
        public bool hasItem;
        public WorldItemWire item;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(changeKind); writer.Put(mapId ?? string.Empty); writer.Put(instanceId ?? string.Empty); writer.Put(worldRevision); writer.Put(itemInstanceId); writer.Put(hasItem);
            if (hasItem) item.Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            changeKind = reader.GetByte(); mapId = reader.GetString(128); instanceId = reader.GetString(128); worldRevision = reader.GetLong(); itemInstanceId = reader.GetLong(); hasItem = reader.GetBool();
            if (hasItem)
            {
                item.Deserialize(reader);
                item.mapId = mapId;
                item.instanceId = instanceId;
            }
        }
    }

    public struct WorldItemInteractionResponseMessage : INetSerializable
    {
        public bool success;
        public uint sequence;
        public ushort actionId;
        public byte resultCode;
        public long itemInstanceId;
        public string detail;

        public void Serialize(NetDataWriter writer)
        {
            // sequence/action/item id are already known from the correlated request.
            writer.Put(resultCode);
            writer.Put(detail ?? string.Empty);
        }
        public void Deserialize(NetDataReader reader)
        {
            resultCode = reader.GetByte();
            success = resultCode == (byte)InteractionResultCode.Success;
            sequence = 0;
            actionId = 0;
            itemInstanceId = 0;
            detail = reader.GetString(256);
        }
    }
}
