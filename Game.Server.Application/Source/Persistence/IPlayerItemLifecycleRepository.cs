using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.WorldItems;
using Game.Shared.Identity;
using Game.Shared.Backend;
using Game.Shared.World;

namespace Game.Server.Application.Persistence
{
    /// <summary>
    /// Explicit persistence boundary for value-changing item lifecycle operations.
    /// Ordinary move/equip commits conserve value; lifecycle commits declare the
    /// legitimate quantity/ownership change so BackendServer can validate it atomically.
    /// </summary>
    public interface IPlayerItemLifecycleRepository
    {
        Task<PlayerItemConsumePersistenceResult> TryConsumeAsync(PlayerItemConsumePersistenceRequest request, CancellationToken cancellationToken);
        Task<PlayerAmmoReloadPersistenceResult> TryConsumeAmmoForReloadAsync(PlayerAmmoReloadPersistenceRequest request, CancellationToken cancellationToken);
        Task<PlayerItemDropPersistenceResult> TryDropAsync(PlayerItemDropPersistenceRequest request, CancellationToken cancellationToken);
        Task<PlayerItemPickupPersistenceResult> TryPickupAsync(PlayerItemPickupPersistenceRequest request, CancellationToken cancellationToken);
        Task<PlayerItemGrantPersistenceResult> TryGrantAsync(PlayerItemGrantPersistenceRequest request, CancellationToken cancellationToken);
        Task<PlayerItemBundleGrantPersistenceResult> TryGrantBundleAsync(PlayerItemBundleGrantPersistenceRequest request, CancellationToken cancellationToken);
        Task<PlayerCraftPersistenceResult> TryCraftAsync(PlayerCraftPersistenceRequest request, CancellationToken cancellationToken);
    }

    public sealed class PlayerItemConsumePersistenceRequest
    {
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public long ExpectedInventoryRevision { get; }
        public long ExpectedEquipmentRevision { get; }
        public ItemInstanceId ItemInstanceId { get; }
        public int ConsumeQuantity { get; }
        public InventoryState Inventory { get; }
        public EquipmentState Equipment { get; }

        public PlayerItemConsumePersistenceRequest(AccountId accountId, CharacterId characterId, long expectedInventoryRevision, long expectedEquipmentRevision, ItemInstanceId itemInstanceId, int consumeQuantity, InventoryState inventory, EquipmentState equipment)
        {
            if (!itemInstanceId.IsValid) throw new ArgumentException("ItemInstanceId is invalid.", nameof(itemInstanceId));
            if (consumeQuantity < 1) throw new ArgumentOutOfRangeException(nameof(consumeQuantity));
            AccountId = accountId; CharacterId = characterId; ExpectedInventoryRevision = expectedInventoryRevision; ExpectedEquipmentRevision = expectedEquipmentRevision;
            ItemInstanceId = itemInstanceId; ConsumeQuantity = consumeQuantity;
            Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory)); Equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        }
    }

    public readonly struct PlayerItemConsumePersistenceResult
    {
        public bool Accepted { get; }
        public bool Stale { get; }
        public long StoredInventoryRevision { get; }
        public long StoredEquipmentRevision { get; }
        public string Error { get; }
        public PlayerItemConsumePersistenceResult(bool accepted, bool stale, long storedInventoryRevision, long storedEquipmentRevision, string error)
        { Accepted = accepted; Stale = stale; StoredInventoryRevision = storedInventoryRevision; StoredEquipmentRevision = storedEquipmentRevision; Error = error ?? string.Empty; }
    }

    public sealed class PlayerAmmoReloadPersistenceRequest
    {
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public long ExpectedInventoryRevision { get; }
        public long ExpectedEquipmentRevision { get; }
        public string AmmoFamily { get; }
        public string PreferredAmmoDefinitionId { get; }
        public int MaximumRounds { get; }
        public ItemInstanceId WeaponItemInstanceId { get; }
        public long ExpectedMagazineRevision { get; }
        public string CurrentLoadedAmmoDefinitionId { get; }
        public int CurrentLoadedRounds { get; }

        public PlayerAmmoReloadPersistenceRequest(
            AccountId accountId,
            CharacterId characterId,
            long expectedInventoryRevision,
            long expectedEquipmentRevision,
            string ammoFamily,
            string preferredAmmoDefinitionId,
            int maximumRounds,
            ItemInstanceId weaponItemInstanceId,
            long expectedMagazineRevision,
            string currentLoadedAmmoDefinitionId,
            int currentLoadedRounds)
        {
            if (string.IsNullOrWhiteSpace(ammoFamily)) throw new ArgumentException("AmmoFamily is required.", nameof(ammoFamily));
            if (maximumRounds < 1) throw new ArgumentOutOfRangeException(nameof(maximumRounds));
            if (!weaponItemInstanceId.IsValid) throw new ArgumentException("WeaponItemInstanceId is invalid.", nameof(weaponItemInstanceId));
            if (expectedMagazineRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedMagazineRevision));
            if (currentLoadedRounds < 0) throw new ArgumentOutOfRangeException(nameof(currentLoadedRounds));
            AccountId = accountId;
            CharacterId = characterId;
            ExpectedInventoryRevision = expectedInventoryRevision;
            ExpectedEquipmentRevision = expectedEquipmentRevision;
            AmmoFamily = ammoFamily.Trim();
            PreferredAmmoDefinitionId = (preferredAmmoDefinitionId ?? string.Empty).Trim();
            MaximumRounds = maximumRounds;
            WeaponItemInstanceId = weaponItemInstanceId;
            ExpectedMagazineRevision = expectedMagazineRevision;
            CurrentLoadedAmmoDefinitionId = currentLoadedRounds > 0
                ? (currentLoadedAmmoDefinitionId ?? string.Empty).Trim()
                : string.Empty;
            CurrentLoadedRounds = currentLoadedRounds;
        }
    }

    public readonly struct PlayerAmmoReloadPersistenceResult
    {
        public bool Accepted { get; }
        public bool Stale { get; }
        public bool AmmoUnavailable { get; }
        public long StoredInventoryRevision { get; }
        public long StoredEquipmentRevision { get; }
        public string AmmoDefinitionId { get; }
        public int ConsumedRounds { get; }
        public PlayerSystemsPersistenceRecord State { get; }
        public string Error { get; }

        public PlayerAmmoReloadPersistenceResult(
            bool accepted,
            bool stale,
            bool ammoUnavailable,
            long storedInventoryRevision,
            long storedEquipmentRevision,
            string ammoDefinitionId,
            int consumedRounds,
            PlayerSystemsPersistenceRecord state,
            string error)
        {
            Accepted = accepted;
            Stale = stale;
            AmmoUnavailable = ammoUnavailable;
            StoredInventoryRevision = storedInventoryRevision;
            StoredEquipmentRevision = storedEquipmentRevision;
            AmmoDefinitionId = ammoDefinitionId ?? string.Empty;
            ConsumedRounds = consumedRounds;
            State = state;
            Error = error ?? string.Empty;
        }
    }

    public sealed class PlayerItemDropPersistenceRequest
    {
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public long ExpectedInventoryRevision { get; }
        public long ExpectedEquipmentRevision { get; }
        public ItemInstanceId SourceItemInstanceId { get; }
        public ItemInstanceId SplitWorldItemInstanceId { get; }
        public int DropQuantity { get; }
        public string SourceLoadedAmmoDefinitionId { get; }
        public int SourceLoadedRounds { get; }
        public long SourceMagazineRevision { get; }
        public string MapId { get; }
        public string InstanceId { get; }
        public WorldPosition Position { get; }
        public InventoryState Inventory { get; }
        public EquipmentState Equipment { get; }

        public PlayerItemDropPersistenceRequest(
            AccountId accountId,
            CharacterId characterId,
            long expectedInventoryRevision,
            long expectedEquipmentRevision,
            ItemInstanceId sourceItemInstanceId,
            ItemInstanceId splitWorldItemInstanceId,
            int dropQuantity,
            string sourceLoadedAmmoDefinitionId,
            int sourceLoadedRounds,
            long sourceMagazineRevision,
            string mapId,
            string instanceId,
            WorldPosition position,
            InventoryState inventory,
            EquipmentState equipment)
        {
            if (!sourceItemInstanceId.IsValid) throw new ArgumentException("Source item id is invalid.", nameof(sourceItemInstanceId));
            if (splitWorldItemInstanceId.IsValid && splitWorldItemInstanceId.Value < BackendServiceContracts.TransientWorldItemIdFloor)
                throw new ArgumentException("Split world item id must use the transient id range.", nameof(splitWorldItemInstanceId));
            if (dropQuantity < 1) throw new ArgumentOutOfRangeException(nameof(dropQuantity));
            if (sourceLoadedRounds < 0) throw new ArgumentOutOfRangeException(nameof(sourceLoadedRounds));
            if (sourceMagazineRevision < 0) throw new ArgumentOutOfRangeException(nameof(sourceMagazineRevision));
            if (string.IsNullOrWhiteSpace(mapId)) throw new ArgumentException("MapId is required.", nameof(mapId));
            AccountId = accountId;
            CharacterId = characterId;
            ExpectedInventoryRevision = expectedInventoryRevision;
            ExpectedEquipmentRevision = expectedEquipmentRevision;
            SourceItemInstanceId = sourceItemInstanceId;
            SplitWorldItemInstanceId = splitWorldItemInstanceId;
            DropQuantity = dropQuantity;
            SourceLoadedAmmoDefinitionId = sourceLoadedRounds > 0 ? (sourceLoadedAmmoDefinitionId ?? string.Empty) : string.Empty;
            SourceLoadedRounds = sourceLoadedRounds;
            SourceMagazineRevision = sourceMagazineRevision;
            MapId = mapId;
            InstanceId = instanceId ?? string.Empty;
            Position = position;
            Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            Equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        }
    }

    public readonly struct PlayerItemDropPersistenceResult
    {
        public bool Accepted { get; }
        public bool Stale { get; }
        public long StoredInventoryRevision { get; }
        public long StoredEquipmentRevision { get; }
        public long WorldRevision { get; }
        public WorldItemState WorldItem { get; }
        public string Error { get; }
        public PlayerItemDropPersistenceResult(bool accepted, bool stale, long storedInventoryRevision, long storedEquipmentRevision, long worldRevision, WorldItemState worldItem, string error)
        { Accepted = accepted; Stale = stale; StoredInventoryRevision = storedInventoryRevision; StoredEquipmentRevision = storedEquipmentRevision; WorldRevision = worldRevision; WorldItem = worldItem; Error = error ?? string.Empty; }
    }

    public sealed class PlayerItemGrantPersistenceRequest
    {
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public long ExpectedInventoryRevision { get; }
        public long ExpectedEquipmentRevision { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }

        public PlayerItemGrantPersistenceRequest(
            AccountId accountId,
            CharacterId characterId,
            long expectedInventoryRevision,
            long expectedEquipmentRevision,
            string definitionId,
            int quantity)
        {
            if (string.IsNullOrWhiteSpace(definitionId)) throw new ArgumentException("DefinitionId is required.", nameof(definitionId));
            if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
            AccountId = accountId;
            CharacterId = characterId;
            ExpectedInventoryRevision = expectedInventoryRevision;
            ExpectedEquipmentRevision = expectedEquipmentRevision;
            DefinitionId = definitionId.Trim();
            Quantity = quantity;
        }
    }

    public readonly struct PlayerItemGrantPersistenceResult
    {
        public bool Accepted { get; }
        public bool Stale { get; }
        public long StoredInventoryRevision { get; }
        public long StoredEquipmentRevision { get; }
        public PlayerSystemsPersistenceRecord State { get; }
        public string Error { get; }

        public PlayerItemGrantPersistenceResult(
            bool accepted,
            bool stale,
            long storedInventoryRevision,
            long storedEquipmentRevision,
            PlayerSystemsPersistenceRecord state,
            string error)
        {
            Accepted = accepted;
            Stale = stale;
            StoredInventoryRevision = storedInventoryRevision;
            StoredEquipmentRevision = storedEquipmentRevision;
            State = state;
            Error = error ?? string.Empty;
        }
    }



    public readonly struct PlayerRewardItemPersistenceEntry
    {
        public ushort ItemDataId { get; }
        public int Quantity { get; }
        public PlayerRewardItemPersistenceEntry(ushort itemDataId, int quantity)
        {
            if (itemDataId == 0) throw new ArgumentOutOfRangeException(nameof(itemDataId));
            if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
            ItemDataId = itemDataId;
            Quantity = quantity;
        }
    }

    public sealed class PlayerItemBundleGrantPersistenceRequest
    {
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public long ExpectedInventoryRevision { get; }
        public long ExpectedEquipmentRevision { get; }
        public PlayerRewardItemPersistenceEntry[] Items { get; }

        public PlayerItemBundleGrantPersistenceRequest(
            AccountId accountId,
            CharacterId characterId,
            long expectedInventoryRevision,
            long expectedEquipmentRevision,
            PlayerRewardItemPersistenceEntry[] items)
        {
            if (items == null || items.Length == 0) throw new ArgumentException("At least one reward item is required.", nameof(items));
            AccountId = accountId;
            CharacterId = characterId;
            ExpectedInventoryRevision = expectedInventoryRevision;
            ExpectedEquipmentRevision = expectedEquipmentRevision;
            Items = (PlayerRewardItemPersistenceEntry[])items.Clone();
        }
    }

    public readonly struct PlayerItemBundleGrantPersistenceResult
    {
        public bool Accepted { get; }
        public bool Stale { get; }
        public long StoredInventoryRevision { get; }
        public long StoredEquipmentRevision { get; }
        public PlayerSystemsPersistenceRecord State { get; }
        public string Error { get; }
        public PlayerItemBundleGrantPersistenceResult(bool accepted, bool stale, long storedInventoryRevision, long storedEquipmentRevision, PlayerSystemsPersistenceRecord state, string error)
        { Accepted = accepted; Stale = stale; StoredInventoryRevision = storedInventoryRevision; StoredEquipmentRevision = storedEquipmentRevision; State = state; Error = error ?? string.Empty; }
    }

    public sealed class PlayerCraftPersistenceRequest
    {
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public long ExpectedInventoryRevision { get; }
        public long ExpectedEquipmentRevision { get; }
        public ushort RecipeDataId { get; }
        public PlayerCraftPersistenceRequest(AccountId accountId, CharacterId characterId, long expectedInventoryRevision, long expectedEquipmentRevision, ushort recipeDataId)
        {
            if (recipeDataId == 0) throw new ArgumentOutOfRangeException(nameof(recipeDataId));
            AccountId = accountId;
            CharacterId = characterId;
            ExpectedInventoryRevision = expectedInventoryRevision;
            ExpectedEquipmentRevision = expectedEquipmentRevision;
            RecipeDataId = recipeDataId;
        }
    }

    public readonly struct PlayerCraftPersistenceResult
    {
        public bool Accepted { get; }
        public bool Stale { get; }
        public long StoredInventoryRevision { get; }
        public long StoredEquipmentRevision { get; }
        public PlayerSystemsPersistenceRecord State { get; }
        public string Error { get; }
        public PlayerCraftPersistenceResult(bool accepted, bool stale, long storedInventoryRevision, long storedEquipmentRevision, PlayerSystemsPersistenceRecord state, string error)
        { Accepted = accepted; Stale = stale; StoredInventoryRevision = storedInventoryRevision; StoredEquipmentRevision = storedEquipmentRevision; State = state; Error = error ?? string.Empty; }
    }

        public sealed class PlayerItemPickupPersistenceRequest
    {
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public long ExpectedInventoryRevision { get; }
        public long ExpectedEquipmentRevision { get; }
        public ItemInstanceId WorldItemInstanceId { get; }
        public long ExpectedWorldItemRevision { get; }
        public InventoryState Inventory { get; }
        public EquipmentState Equipment { get; }

        public PlayerItemPickupPersistenceRequest(AccountId accountId, CharacterId characterId, long expectedInventoryRevision, long expectedEquipmentRevision, ItemInstanceId worldItemInstanceId, long expectedWorldItemRevision, InventoryState inventory, EquipmentState equipment)
        {
            if (!worldItemInstanceId.IsValid) throw new ArgumentException("World item id is invalid.", nameof(worldItemInstanceId));
            if (expectedWorldItemRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedWorldItemRevision));
            AccountId = accountId; CharacterId = characterId; ExpectedInventoryRevision = expectedInventoryRevision; ExpectedEquipmentRevision = expectedEquipmentRevision;
            WorldItemInstanceId = worldItemInstanceId; ExpectedWorldItemRevision = expectedWorldItemRevision;
            Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory)); Equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        }
    }

    public readonly struct PlayerItemPickupPersistenceResult
    {
        public bool Accepted { get; }
        public bool Stale { get; }
        public long StoredInventoryRevision { get; }
        public long StoredEquipmentRevision { get; }
        public long WorldRevision { get; }
        public string MapId { get; }
        public string InstanceId { get; }
        public PlayerSystemsPersistenceRecord State { get; }
        public string Error { get; }
        public PlayerItemPickupPersistenceResult(bool accepted, bool stale, long storedInventoryRevision, long storedEquipmentRevision, long worldRevision, string mapId, string instanceId, PlayerSystemsPersistenceRecord state, string error)
        { Accepted = accepted; Stale = stale; StoredInventoryRevision = storedInventoryRevision; StoredEquipmentRevision = storedEquipmentRevision; WorldRevision = worldRevision; MapId = mapId ?? string.Empty; InstanceId = instanceId ?? string.Empty; State = state; Error = error ?? string.Empty; }
    }
}
