using System;

namespace Game.Shared.Protocol
{
    public enum PlayerItemOperationStatus : byte
    {
        None = 0,
        Success = 1,
        SessionUnavailable = 2,
        CharacterUnavailable = 3,
        InvalidSlot = 4,
        ItemUnavailable = 5,
        EquipmentSlotInvalid = 6,
        EquipmentNotAllowed = 7,
        InventoryFull = 8,
        StaleState = 9,
        PersistenceRejected = 10,
        ContentUnavailable = 11,
        ItemNotUsable = 12,
        EffectUnavailable = 13,
    }

    [Serializable]
    public sealed class PlayerItemView
    {
        public int inventorySlot;
        public ushort equipmentSlotDataId;
        public string equipmentSlotId;
        public ushort itemDataId;
        public long itemInstanceId;
        public string definitionId;
        public string displayName;
        public int quantity;
        public int durability;
        public int maxDurability;
        public float unitWeight;
        public bool canUse;
        public int consumeQuantity;
        public string[] allowedEquipmentSlots;
    }

    [Serializable]
    public sealed class EquipmentSlotView
    {
        public ushort slotDataId;
        public string slotId;
        public string displayName;
        public int order;
        public PlayerItemView item;
    }

    [Serializable]
    public sealed class PlayerItemsSnapshot
    {
        public long contentRevision;
        public long inventoryRevision;
        public long equipmentRevision;
        public int inventoryCapacity;
        public float inventoryWeight;
        public float armor;
        public float attackPower;
        public PlayerItemView[] inventory;
        public EquipmentSlotView[] equipment;
    }

    public readonly struct PlayerItemOperationResult
    {
        public bool Success { get; }
        public PlayerItemOperationStatus Status { get; }
        public string Error { get; }
        public PlayerItemsSnapshot Snapshot { get; }

        public PlayerItemOperationResult(bool success, PlayerItemOperationStatus status, string error, PlayerItemsSnapshot snapshot)
        {
            Success = success;
            Status = status;
            Error = error ?? string.Empty;
            Snapshot = snapshot;
        }

        public static PlayerItemOperationResult Succeeded(PlayerItemsSnapshot snapshot) =>
            new PlayerItemOperationResult(true, PlayerItemOperationStatus.Success, string.Empty, snapshot);

        public static PlayerItemOperationResult Failed(PlayerItemOperationStatus status, string error, PlayerItemsSnapshot snapshot = null) =>
            new PlayerItemOperationResult(false, status, error, snapshot);
    }
}
