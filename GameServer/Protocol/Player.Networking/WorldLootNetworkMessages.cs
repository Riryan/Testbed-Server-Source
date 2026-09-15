using System;
using Game.Shared.Interactions;
using LiteNetLib.Utils;

namespace Player.Networking
{
    /// <summary>
    /// Rare owner-only server pushes for loot UI/session state. Normal Search interaction
    /// uses this push so the client sends one compact ContextInteraction request instead
    /// of a second loot-open request. Explicit WorldLootOpen remains reconciliation-only.
    /// </summary>
    public static class WorldLootMessageTypes
    {
        public const ushort Snapshot = 61;
    }

    /// <summary>
    /// Owner-only request/response contract for baked SceneObject loot. Contents are
    /// never observer-replicated; the standalone GameServer validates range/state and
    /// sends the requesting player only the current loot snapshot.
    /// </summary>
    public struct WorldLootOpenRequestMessage : INetSerializable
    {
        public long stableId;
        public void Serialize(NetDataWriter writer) => writer.Put(stableId);
        public void Deserialize(NetDataReader reader) => stableId = reader.GetLong();
    }

    public struct WorldLootTakeRequestMessage : INetSerializable
    {
        public long stableId;
        public long lootRevision;
        public int entryIndex;
        public int quantity;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(stableId);
            writer.Put(lootRevision);
            writer.Put(entryIndex);
            writer.Put(quantity);
        }

        public void Deserialize(NetDataReader reader)
        {
            stableId = reader.GetLong();
            lootRevision = reader.GetLong();
            entryIndex = reader.GetInt();
            quantity = reader.GetInt();
        }
    }

    public struct WorldLootTakeAllRequestMessage : INetSerializable
    {
        public long stableId;
        public long lootRevision;
        public void Serialize(NetDataWriter writer) { writer.Put(stableId); writer.Put(lootRevision); }
        public void Deserialize(NetDataReader reader) { stableId = reader.GetLong(); lootRevision = reader.GetLong(); }
    }

    /// <summary>
    /// Compact result for taking one already-known loot entry. Opening/reconciliation uses
    /// WorldLootResponseMessage; successful takes only return the changed entry state.
    /// </summary>
    public struct WorldLootTakeResponseMessage : INetSerializable
    {
        public bool success;
        public byte resultCode;
        public string error;
        public long stableId;
        public long lootRevision;
        public int entryIndex;
        public int remainingQuantity;
        public bool depleted;

        public InteractionResultCode ResultCode => (InteractionResultCode)resultCode;

        public void Serialize(NetDataWriter writer)
        {
            // Request correlation already identifies stableId + entryIndex. Only return
            // authoritative changed state on success; failures carry a compact reason + detail.
            writer.Put(resultCode);
            if (resultCode == (byte)InteractionResultCode.Success)
            {
                writer.Put(lootRevision);
                writer.Put(remainingQuantity);
                writer.Put(depleted);
            }
            else
            {
                writer.Put(error ?? string.Empty);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            resultCode = reader.GetByte();
            success = resultCode == (byte)InteractionResultCode.Success;
            stableId = 0;
            entryIndex = -1;
            if (success)
            {
                error = string.Empty;
                lootRevision = reader.GetLong();
                remainingQuantity = reader.GetInt();
                depleted = reader.GetBool();
            }
            else
            {
                error = reader.GetString(256);
                lootRevision = 0;
                remainingQuantity = 0;
                depleted = false;
            }
        }

        public static WorldLootTakeResponseMessage Failed(
            long stableId,
            InteractionResultCode resultCode,
            string error) =>
            new WorldLootTakeResponseMessage
            {
                success = false,
                resultCode = (byte)resultCode,
                error = error ?? string.Empty,
                stableId = stableId,
                entryIndex = -1,
            };
    }

    public struct WorldLootEntryWire : INetSerializable
    {
        public int entryIndex;
        public ushort itemDataId;
        public string definitionId;
        public string displayName;
        public int quantity;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(entryIndex);
            writer.Put(itemDataId);
            writer.Put(quantity);
        }

        public void Deserialize(NetDataReader reader)
        {
            entryIndex = reader.GetInt();
            itemDataId = reader.GetUShort();
            quantity = reader.GetInt();

            definitionId = string.Empty;
            displayName = string.Empty;
            if (itemDataId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetItem(itemDataId, out GameplayItemReferenceWire definition))
            {
                definitionId = definition.definitionId ?? string.Empty;
                displayName = definition.displayName ?? definitionId;
            }
            else
            {
                definitionId = itemDataId == 0 ? string.Empty : $"item#{itemDataId}";
                displayName = definitionId;
            }
        }
    }

    public struct WorldLootResponseMessage : INetSerializable
    {
        public const int MaxEntries = 64;

        public bool success;
        public byte resultCode;
        public string error;
        public long stableId;
        public long lootRevision;
        public string sourceLabel;
        public bool depleted;
        public WorldLootEntryWire[] entries;

        public InteractionResultCode ResultCode => (InteractionResultCode)resultCode;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(resultCode);
            writer.Put(error ?? string.Empty);
            writer.Put(stableId);
            writer.Put(lootRevision);
            writer.Put(sourceLabel ?? string.Empty);
            writer.Put(depleted);

            WorldLootEntryWire[] values = entries ?? Array.Empty<WorldLootEntryWire>();
            int count = Math.Min(values.Length, MaxEntries);
            writer.Put((byte)count);
            for (int i = 0; i < count; ++i)
                values[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            resultCode = reader.GetByte();
            error = reader.GetString(256);
            stableId = reader.GetLong();
            lootRevision = reader.GetLong();
            sourceLabel = reader.GetString(160);
            depleted = reader.GetBool();

            int count = reader.GetByte();
            if (count > MaxEntries)
                throw new InvalidOperationException("world loot snapshot is too large");
            entries = new WorldLootEntryWire[count];
            for (int i = 0; i < count; ++i)
            {
                WorldLootEntryWire value = default;
                value.Deserialize(reader);
                entries[i] = value;
            }
        }

        public static WorldLootResponseMessage Failed(
            long stableId,
            InteractionResultCode resultCode,
            string error) =>
            new WorldLootResponseMessage
            {
                success = false,
                resultCode = (byte)resultCode,
                error = error ?? string.Empty,
                stableId = stableId,
                lootRevision = 0,
                sourceLabel = string.Empty,
                depleted = false,
                entries = Array.Empty<WorldLootEntryWire>(),
            };
    }
}
