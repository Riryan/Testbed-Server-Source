using System;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public sealed class PersistedInventoryItem
    {
        public int SlotIndex { get; }
        public ItemInstanceId ItemInstanceId { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
        public int Durability { get; }
        public long Revision { get; }
        public string LoadedAmmoDefinitionId { get; }
        public int LoadedRounds { get; }
        public long MagazineRevision { get; }

        public PersistedInventoryItem(
            int slotIndex,
            ItemInstanceId itemInstanceId,
            string definitionId,
            int quantity,
            int durability,
            long revision,
            string loadedAmmoDefinitionId = "",
            int loadedRounds = 0,
            long magazineRevision = 0)
        {
            SlotIndex = slotIndex;
            ItemInstanceId = itemInstanceId;
            DefinitionId = definitionId;
            Quantity = quantity;
            Durability = durability;
            Revision = revision;
            LoadedAmmoDefinitionId = loadedRounds > 0 ? (loadedAmmoDefinitionId ?? string.Empty) : string.Empty;
            LoadedRounds = loadedRounds;
            MagazineRevision = magazineRevision;
        }
    }

    public sealed class PersistedEquipmentItem
    {
        public string SlotId { get; }
        public ItemInstanceId ItemInstanceId { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
        public int Durability { get; }
        public long Revision { get; }
        public string LoadedAmmoDefinitionId { get; }
        public int LoadedRounds { get; }
        public long MagazineRevision { get; }

        public PersistedEquipmentItem(
            string slotId,
            ItemInstanceId itemInstanceId,
            string definitionId,
            int quantity,
            int durability,
            long revision,
            string loadedAmmoDefinitionId = "",
            int loadedRounds = 0,
            long magazineRevision = 0)
        {
            SlotId = slotId;
            ItemInstanceId = itemInstanceId;
            DefinitionId = definitionId;
            Quantity = quantity;
            Durability = durability;
            Revision = revision;
            LoadedAmmoDefinitionId = loadedRounds > 0 ? (loadedAmmoDefinitionId ?? string.Empty) : string.Empty;
            LoadedRounds = loadedRounds;
            MagazineRevision = magazineRevision;
        }
    }

    public sealed class PlayerSystemsPersistenceRecord
    {
        public int InventoryCapacity { get; }
        public long InventoryRevision { get; }
        public long EquipmentRevision { get; }
        public PersistedInventoryItem[] InventoryItems { get; }
        public PersistedEquipmentItem[] EquipmentItems { get; }

        public PlayerSystemsPersistenceRecord(int inventoryCapacity, long inventoryRevision, long equipmentRevision, PersistedInventoryItem[] inventoryItems, PersistedEquipmentItem[] equipmentItems)
        {
            if (inventoryCapacity < 1) throw new ArgumentOutOfRangeException(nameof(inventoryCapacity));
            if (inventoryRevision < 0) throw new ArgumentOutOfRangeException(nameof(inventoryRevision));
            if (equipmentRevision < 0) throw new ArgumentOutOfRangeException(nameof(equipmentRevision));
            InventoryCapacity = inventoryCapacity;
            InventoryRevision = inventoryRevision;
            EquipmentRevision = equipmentRevision;
            InventoryItems = inventoryItems ?? Array.Empty<PersistedInventoryItem>();
            EquipmentItems = equipmentItems ?? Array.Empty<PersistedEquipmentItem>();
        }
    }
}
