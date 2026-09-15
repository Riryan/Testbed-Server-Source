using System;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace Player.Networking
{
    public static class PlayerItemRequestTypes
    {
        public const ushort Snapshot = 200;
        public const ushort MoveInventory = 201;
        public const ushort Equip = 202;
        public const ushort Unequip = 203;
        public const ushort Use = 204;
        public const ushort Drop = 205;
    }

    public static class PlayerItemMessageTypes
    {
        public const ushort Delta = 210;
        public const ushort Snapshot = 56;
    }

    public struct PlayerItemsSnapshotRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    public struct MoveInventoryRequestMessage : INetSerializable
    {
        public int fromIndex; public int toIndex;
        public void Serialize(NetDataWriter writer) { writer.Put(fromIndex); writer.Put(toIndex); }
        public void Deserialize(NetDataReader reader) { fromIndex = reader.GetInt(); toIndex = reader.GetInt(); }
    }

    public struct EquipItemRequestMessage : INetSerializable
    {
        public int inventoryIndex;
        public ushort equipmentSlotDataId;
        public string equipmentSlotId;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(inventoryIndex);
            writer.Put(equipmentSlotDataId);
        }

        public void Deserialize(NetDataReader reader)
        {
            inventoryIndex = reader.GetInt();
            equipmentSlotDataId = reader.GetUShort();
            equipmentSlotId = string.Empty;
        }
    }

    public struct UnequipItemRequestMessage : INetSerializable
    {
        public ushort equipmentSlotDataId;
        public string equipmentSlotId;
        public int preferredInventoryIndex;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(equipmentSlotDataId);
            writer.Put(preferredInventoryIndex);
        }

        public void Deserialize(NetDataReader reader)
        {
            equipmentSlotDataId = reader.GetUShort();
            equipmentSlotId = string.Empty;
            preferredInventoryIndex = reader.GetInt();
        }
    }

    public struct UseItemRequestMessage : INetSerializable
    {
        public int inventoryIndex;
        public void Serialize(NetDataWriter writer) { writer.Put(inventoryIndex); }
        public void Deserialize(NetDataReader reader) { inventoryIndex = reader.GetInt(); }
    }

    public struct DropItemRequestMessage : INetSerializable
    {
        public int inventoryIndex;
        public int quantity;
        public void Serialize(NetDataWriter writer) { writer.Put(inventoryIndex); writer.Put(quantity); }
        public void Deserialize(NetDataReader reader) { inventoryIndex = reader.GetInt(); quantity = reader.GetInt(); }
    }

    public struct PlayerItemWire : INetSerializable
    {
        public int inventorySlot;
        public ushort equipmentSlotDataId;
        public string equipmentSlotId;
        public long itemInstanceId;
        public ushort itemDataId;
        public string definitionId;
        public string displayName;
        public int quantity;
        public int durability;
        public int maxDurability;
        public float unitWeight;
        public bool canUse;
        public int consumeQuantity;
        public string[] allowedEquipmentSlots;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(inventorySlot);
            writer.Put(equipmentSlotDataId);
            SerializeBody(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            int decodedInventorySlot = reader.GetInt();
            ushort decodedEquipmentSlotDataId = reader.GetUShort();
            DeserializeBody(reader, decodedInventorySlot, decodedEquipmentSlotDataId);
        }

        public void SerializeBody(NetDataWriter writer)
        {
            writer.Put(itemInstanceId);
            writer.Put(itemDataId);
            writer.Put(quantity);
            writer.Put(durability);
        }

        public void DeserializeBody(
            NetDataReader reader,
            int decodedInventorySlot,
            ushort decodedEquipmentSlotDataId)
        {
            inventorySlot = decodedInventorySlot;
            equipmentSlotDataId = decodedEquipmentSlotDataId;
            itemInstanceId = reader.GetLong();
            itemDataId = reader.GetUShort();
            quantity = reader.GetInt();
            durability = reader.GetInt();

            equipmentSlotId = string.Empty;
            if (equipmentSlotDataId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetEquipmentSlot(
                    equipmentSlotDataId,
                    out GameplayEquipmentSlotReferenceWire slot))
                equipmentSlotId = slot.slotId ?? string.Empty;

            definitionId = string.Empty;
            displayName = string.Empty;
            maxDurability = 0;
            unitWeight = 0f;
            canUse = false;
            consumeQuantity = 0;
            allowedEquipmentSlots = Array.Empty<string>();

            if (itemDataId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetItem(
                    itemDataId,
                    out GameplayItemReferenceWire definition))
            {
                definitionId = definition.definitionId ?? string.Empty;
                displayName = definition.displayName ?? definitionId;
                maxDurability = definition.maxDurability;
                unitWeight = definition.unitWeight;
                canUse = definition.canUse;
                consumeQuantity = definition.consumeQuantity;
                allowedEquipmentSlots =
                    PlayerGameplaySettingsRuntime.ResolveEquipmentSlotNames(
                        definition.allowedSlotDataIds);
            }
            else
            {
                definitionId = itemDataId == 0 ? string.Empty : $"item#{itemDataId}";
                displayName = definitionId;
            }
        }
    }

    public struct EquipmentSlotWire : INetSerializable
    {
        public ushort slotDataId;
        public string slotId;
        public string displayName;
        public int order;
        public bool hasItem;
        public PlayerItemWire item;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(slotDataId);
            writer.Put(hasItem);
            if (hasItem) item.SerializeBody(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            slotDataId = reader.GetUShort();
            hasItem = reader.GetBool();

            slotId = string.Empty;
            displayName = string.Empty;
            order = 0;
            if (slotDataId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetEquipmentSlot(
                    slotDataId,
                    out GameplayEquipmentSlotReferenceWire slot))
            {
                slotId = slot.slotId ?? string.Empty;
                displayName = slot.displayName ?? slotId;
                order = slot.order;
            }

            if (hasItem)
                item.DeserializeBody(reader, -1, slotDataId);
        }
    }

    public struct InventorySlotDeltaWire : INetSerializable
    {
        public int inventorySlot;
        public bool hasItem;
        public PlayerItemWire item;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(inventorySlot);
            writer.Put(hasItem);
            if (hasItem) item.SerializeBody(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            inventorySlot = reader.GetInt();
            hasItem = reader.GetBool();
            if (hasItem) item.DeserializeBody(reader, inventorySlot, 0);
        }
    }

    public struct EquipmentSlotDeltaWire : INetSerializable
    {
        public ushort slotDataId;
        public string slotId;
        public bool hasItem;
        public PlayerItemWire item;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(slotDataId);
            writer.Put(hasItem);
            if (hasItem) item.SerializeBody(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            slotDataId = reader.GetUShort();
            slotId = string.Empty;
            if (slotDataId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetEquipmentSlot(
                    slotDataId,
                    out GameplayEquipmentSlotReferenceWire slot))
                slotId = slot.slotId ?? string.Empty;

            hasItem = reader.GetBool();
            if (hasItem) item.DeserializeBody(reader, -1, slotDataId);
        }
    }

    [Flags]
    public enum PlayerItemsDeltaFlags : byte
    {
        None = 0,
        ContentRevision = 1 << 0,
        InventoryWeight = 1 << 1,
        CombatStats = 1 << 2,
    }

    public struct PlayerItemsDeltaMessage : INetSerializable
    {
        public const int MaxInventorySlotChanges = 512;
        public const int MaxEquipmentSlotChanges = 64;

        public long contentRevision;
        public long inventoryRevision;
        public long equipmentRevision;
        public byte changeMask;
        public float inventoryWeight;
        public float armor;
        public float attackPower;
        public InventorySlotDeltaWire[] inventoryChanges;
        public EquipmentSlotDeltaWire[] equipmentChanges;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(inventoryRevision);
            writer.Put(equipmentRevision);
            writer.Put(changeMask);
            PlayerItemsDeltaFlags flags = (PlayerItemsDeltaFlags)changeMask;
            if ((flags & PlayerItemsDeltaFlags.ContentRevision) != 0) writer.Put(contentRevision);
            if ((flags & PlayerItemsDeltaFlags.InventoryWeight) != 0) writer.Put(inventoryWeight);
            if ((flags & PlayerItemsDeltaFlags.CombatStats) != 0)
            {
                writer.Put(armor);
                writer.Put(attackPower);
            }

            InventorySlotDeltaWire[] inv = inventoryChanges ?? Array.Empty<InventorySlotDeltaWire>();
            int invCount = Math.Min(inv.Length, MaxInventorySlotChanges);
            writer.Put((ushort)invCount);
            for (int i = 0; i < invCount; ++i) inv[i].Serialize(writer);

            EquipmentSlotDeltaWire[] eq = equipmentChanges ?? Array.Empty<EquipmentSlotDeltaWire>();
            int eqCount = Math.Min(eq.Length, MaxEquipmentSlotChanges);
            writer.Put((byte)eqCount);
            for (int i = 0; i < eqCount; ++i) eq[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            inventoryRevision = reader.GetLong();
            equipmentRevision = reader.GetLong();
            changeMask = reader.GetByte();
            PlayerItemsDeltaFlags flags = (PlayerItemsDeltaFlags)changeMask;
            contentRevision = (flags & PlayerItemsDeltaFlags.ContentRevision) != 0 ? reader.GetLong() : 0L;
            inventoryWeight = (flags & PlayerItemsDeltaFlags.InventoryWeight) != 0 ? reader.GetFloat() : 0f;
            if ((flags & PlayerItemsDeltaFlags.CombatStats) != 0)
            {
                armor = reader.GetFloat();
                attackPower = reader.GetFloat();
            }
            else
            {
                armor = 0f;
                attackPower = 0f;
            }

            int invCount = reader.GetUShort();
            if (invCount > MaxInventorySlotChanges) throw new InvalidOperationException("inventory delta too large");
            inventoryChanges = new InventorySlotDeltaWire[invCount];
            for (int i = 0; i < invCount; ++i)
            {
                InventorySlotDeltaWire delta = default;
                delta.Deserialize(reader);
                inventoryChanges[i] = delta;
            }

            int eqCount = reader.GetByte();
            if (eqCount > MaxEquipmentSlotChanges) throw new InvalidOperationException("equipment delta too large");
            equipmentChanges = new EquipmentSlotDeltaWire[eqCount];
            for (int i = 0; i < eqCount; ++i)
            {
                EquipmentSlotDeltaWire delta = default;
                delta.Deserialize(reader);
                equipmentChanges[i] = delta;
            }
        }
    }

    /// <summary>Compact acknowledgement for authoritative item mutations.
    /// The resulting inventory/equipment state is delivered by PlayerItemsDeltaMessage.
    /// </summary>
    public struct PlayerItemMutationResponseMessage : INetSerializable
    {
        public bool success;
        public byte status;
        public string error;

        public void Serialize(NetDataWriter writer)
        {
            // Status is authoritative; Success is derivable and successful ACKs carry no text.
            writer.Put(status);
            if (status != (byte)Game.Shared.Protocol.PlayerItemOperationStatus.Success)
                writer.Put(error ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            status = reader.GetByte();
            success = status == (byte)Game.Shared.Protocol.PlayerItemOperationStatus.Success;
            error = success ? string.Empty : reader.GetString(256);
        }

        public static PlayerItemMutationResponseMessage Failed(byte status, string error) =>
            new PlayerItemMutationResponseMessage
            {
                success = false,
                status = status,
                error = error ?? string.Empty,
            };
    }

    public struct PlayerItemsResponseMessage : INetSerializable
    {
        public const int MaxInventoryEntries = 512;
        public const int MaxEquipmentSlots = 64;
        public bool success; public byte status; public string error;
        public long contentRevision; public long inventoryRevision; public long equipmentRevision;
        public int inventoryCapacity; public float inventoryWeight; public float armor; public float attackPower;
        public PlayerItemWire[] inventory; public EquipmentSlotWire[] equipment;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success); writer.Put(status); writer.Put(error ?? string.Empty);
            writer.Put(contentRevision); writer.Put(inventoryRevision); writer.Put(equipmentRevision);
            writer.Put(inventoryCapacity); writer.Put(inventoryWeight); writer.Put(armor); writer.Put(attackPower);
            PlayerItemWire[] inv = inventory ?? Array.Empty<PlayerItemWire>();
            int invCount = Math.Min(inv.Length, MaxInventoryEntries); writer.Put((ushort)invCount);
            for (int i = 0; i < invCount; ++i) inv[i].Serialize(writer);
            EquipmentSlotWire[] eq = equipment ?? Array.Empty<EquipmentSlotWire>();
            int eqCount = Math.Min(eq.Length, MaxEquipmentSlots); writer.Put((byte)eqCount);
            for (int i = 0; i < eqCount; ++i) eq[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool(); status = reader.GetByte(); error = reader.GetString(256);
            contentRevision = reader.GetLong(); inventoryRevision = reader.GetLong(); equipmentRevision = reader.GetLong();
            inventoryCapacity = reader.GetInt(); inventoryWeight = reader.GetFloat(); armor = reader.GetFloat(); attackPower = reader.GetFloat();
            int invCount = reader.GetUShort(); if (invCount > MaxInventoryEntries) throw new InvalidOperationException("inventory snapshot too large");
            inventory = new PlayerItemWire[invCount];
            for (int i = 0; i < invCount; ++i) { PlayerItemWire item = default; item.Deserialize(reader); inventory[i] = item; }
            int eqCount = reader.GetByte(); if (eqCount > MaxEquipmentSlots) throw new InvalidOperationException("equipment snapshot too large");
            equipment = new EquipmentSlotWire[eqCount];
            for (int i = 0; i < eqCount; ++i) { EquipmentSlotWire slot = default; slot.Deserialize(reader); equipment[i] = slot; }
        }

        public static PlayerItemsResponseMessage Failed(byte status, string error) => new PlayerItemsResponseMessage
        {
            success = false, status = status, error = error ?? string.Empty, inventory = Array.Empty<PlayerItemWire>(), equipment = Array.Empty<EquipmentSlotWire>()
        };
    }
}
