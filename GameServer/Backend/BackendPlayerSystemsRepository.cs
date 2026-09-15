using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.WorldItems;
using Game.Shared.Backend;
using Game.Shared.Identity;
using Game.Shared.World;

namespace Game.UnityIntegration.Backend
{
    public sealed class BackendPlayerSystemsRepository : IPlayerSystemsRepository, IPlayerItemLifecycleRepository, IWorldItemRepository
    {
        private readonly BackendInternalClient _backend;
        private readonly ICharacterPersistenceLeaseProofProvider _leaseProofProvider;

        public BackendPlayerSystemsRepository(
            BackendInternalClient backend,
            ICharacterPersistenceLeaseProofProvider leaseProofProvider = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _leaseProofProvider = leaseProofProvider;
        }

        private bool TryGetMutationLeaseOwnerToken(CharacterId characterId, out string leaseOwnerToken)
        {
            // Unity/legacy composition may still omit the distributed lease provider.
            // Gateway permits that only while no active Backend-owned lease exists.
            if (_leaseProofProvider == null)
            {
                leaseOwnerToken = string.Empty;
                return true;
            }

            if (_leaseProofProvider.TryGetPersistenceLeaseOwnerToken(characterId, out leaseOwnerToken) &&
                !string.IsNullOrWhiteSpace(leaseOwnerToken))
                return true;

            leaseOwnerToken = string.Empty;
            return false;
        }

        public Task<PlayerSystemsPersistenceRecord> LoadAsync(
            AccountId accountId,
            CharacterId characterId,
            CancellationToken cancellationToken) =>
            LoadAsync(accountId, characterId, mutationBarrier: false, cancellationToken: cancellationToken);

        public Task<PlayerSystemsPersistenceRecord> LoadForReconciliationAsync(
            AccountId accountId,
            CharacterId characterId,
            CancellationToken cancellationToken) =>
            LoadAsync(accountId, characterId, mutationBarrier: true, cancellationToken: cancellationToken);

        private async Task<PlayerSystemsPersistenceRecord> LoadAsync(
            AccountId accountId,
            CharacterId characterId,
            bool mutationBarrier,
            CancellationToken cancellationToken)
        {
            if (!accountId.IsValid || !characterId.IsValid) return null;
            BackendPlayerSystemsLoadResponse response = await _backend.LoadPlayerSystemsAsync(
                new BackendPlayerSystemsLoadRequest
                {
                    accountId = accountId.Value,
                    characterId = characterId.Value,
                    mutationBarrier = mutationBarrier,
                },
                cancellationToken).ConfigureAwait(false);
            if (!response.success) throw new InvalidOperationException("Backend player-systems load failed.");
            return response.found ? FromDto(response.state) : null;
        }

        public async Task<PlayerSystemsCommitResult> TryCommitAsync(PlayerSystemsCommitRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!TryGetMutationLeaseOwnerToken(request.CharacterId, out string leaseOwnerToken))
                return new PlayerSystemsCommitResult(false, false, 0, 0, "character authority lease is not held");

            BackendPlayerSystemsCommitResponse response = await _backend.CommitPlayerSystemsAsync(
                new BackendPlayerSystemsCommitRequest
                {
                    accountId = request.AccountId.Value,
                    characterId = request.CharacterId.Value,
                    leaseOwnerToken = leaseOwnerToken,
                    expectedInventoryRevision = request.ExpectedInventoryRevision,
                    expectedEquipmentRevision = request.ExpectedEquipmentRevision,
                    state = ToDto(request.Inventory, request.Equipment),
                }, cancellationToken).ConfigureAwait(false);

            return new PlayerSystemsCommitResult(
                response.success && response.accepted, response.stale,
                response.storedInventoryRevision, response.storedEquipmentRevision, response.error);
        }

        public async Task<PlayerItemConsumePersistenceResult> TryConsumeAsync(
            PlayerItemConsumePersistenceRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!TryGetMutationLeaseOwnerToken(request.CharacterId, out string leaseOwnerToken))
                return new PlayerItemConsumePersistenceResult(false, false, 0, 0, "character authority lease is not held");

            BackendPlayerItemConsumeResponse response = await _backend.ConsumePlayerItemAsync(
                new BackendPlayerItemConsumeRequest
                {
                    accountId = request.AccountId.Value,
                    characterId = request.CharacterId.Value,
                    leaseOwnerToken = leaseOwnerToken,
                    expectedInventoryRevision = request.ExpectedInventoryRevision,
                    expectedEquipmentRevision = request.ExpectedEquipmentRevision,
                    itemInstanceId = request.ItemInstanceId.Value,
                    consumeQuantity = request.ConsumeQuantity,
                    state = ToDto(request.Inventory, request.Equipment),
                }, cancellationToken).ConfigureAwait(false);

            return new PlayerItemConsumePersistenceResult(
                response.success && response.accepted,
                response.stale,
                response.storedInventoryRevision,
                response.storedEquipmentRevision,
                response.error);
        }

        public async Task<PlayerAmmoReloadPersistenceResult> TryConsumeAmmoForReloadAsync(
            PlayerAmmoReloadPersistenceRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!TryGetMutationLeaseOwnerToken(request.CharacterId, out string leaseOwnerToken))
                return new PlayerAmmoReloadPersistenceResult(
                    false, false, false, 0, 0, string.Empty, 0, null,
                    "character authority lease is not held");

            BackendPlayerAmmoReloadResponse response = await _backend.ConsumeAmmoForReloadAsync(
                new BackendPlayerAmmoReloadRequest
                {
                    accountId = request.AccountId.Value,
                    characterId = request.CharacterId.Value,
                    leaseOwnerToken = leaseOwnerToken,
                    expectedInventoryRevision = request.ExpectedInventoryRevision,
                    expectedEquipmentRevision = request.ExpectedEquipmentRevision,
                    ammoFamily = request.AmmoFamily,
                    preferredAmmoDefinitionId = request.PreferredAmmoDefinitionId,
                    maximumRounds = request.MaximumRounds,
                    weaponItemInstanceId = request.WeaponItemInstanceId.Value,
                    expectedMagazineRevision = request.ExpectedMagazineRevision,
                    currentLoadedAmmoDefinitionId = request.CurrentLoadedAmmoDefinitionId,
                    currentLoadedRounds = request.CurrentLoadedRounds,
                }, cancellationToken).ConfigureAwait(false);

            return new PlayerAmmoReloadPersistenceResult(
                response.success && response.accepted,
                response.stale,
                response.ammoUnavailable,
                response.storedInventoryRevision,
                response.storedEquipmentRevision,
                response.ammoDefinitionId,
                response.consumedRounds,
                FromDto(response.state),
                response.error);
        }

        public async Task<PlayerItemDropPersistenceResult> TryDropAsync(PlayerItemDropPersistenceRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!TryGetMutationLeaseOwnerToken(request.CharacterId, out string leaseOwnerToken))
                return new PlayerItemDropPersistenceResult(false, false, 0, 0, 0, null, "character authority lease is not held");

            BackendPlayerItemDropResponse response = await _backend.DropPlayerItemAsync(new BackendPlayerItemDropRequest
            {
                accountId = request.AccountId.Value,
                characterId = request.CharacterId.Value,
                leaseOwnerToken = leaseOwnerToken,
                expectedInventoryRevision = request.ExpectedInventoryRevision,
                expectedEquipmentRevision = request.ExpectedEquipmentRevision,
                sourceItemInstanceId = request.SourceItemInstanceId.Value,
                splitWorldItemInstanceId = request.SplitWorldItemInstanceId.IsValid ? request.SplitWorldItemInstanceId.Value : 0,
                dropQuantity = request.DropQuantity,
                sourceLoadedAmmoDefinitionId = request.SourceLoadedAmmoDefinitionId,
                sourceLoadedRounds = request.SourceLoadedRounds,
                sourceMagazineRevision = request.SourceMagazineRevision,
                mapId = request.MapId,
                instanceId = request.InstanceId,
                positionX = request.Position.X,
                positionY = request.Position.Y,
                positionZ = request.Position.Z,
                state = ToDto(request.Inventory, request.Equipment),
            }, cancellationToken).ConfigureAwait(false);

            return new PlayerItemDropPersistenceResult(
                response.success && response.accepted,
                response.stale,
                response.storedInventoryRevision,
                response.storedEquipmentRevision,
                response.worldRevision,
                ToWorldItemState(response.worldItem),
                response.error);
        }

        public async Task<PlayerItemPickupPersistenceResult> TryPickupAsync(PlayerItemPickupPersistenceRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!TryGetMutationLeaseOwnerToken(request.CharacterId, out string leaseOwnerToken))
                return new PlayerItemPickupPersistenceResult(false, false, 0, 0, 0, string.Empty, string.Empty, null, "character authority lease is not held");

            BackendPlayerItemPickupResponse response = await _backend.PickupPlayerItemAsync(new BackendPlayerItemPickupRequest
            {
                accountId = request.AccountId.Value,
                characterId = request.CharacterId.Value,
                leaseOwnerToken = leaseOwnerToken,
                expectedInventoryRevision = request.ExpectedInventoryRevision,
                expectedEquipmentRevision = request.ExpectedEquipmentRevision,
                worldItemInstanceId = request.WorldItemInstanceId.Value,
                expectedWorldItemRevision = request.ExpectedWorldItemRevision,
                state = ToDto(request.Inventory, request.Equipment),
            }, cancellationToken).ConfigureAwait(false);

            return new PlayerItemPickupPersistenceResult(
                response.success && response.accepted,
                response.stale,
                response.storedInventoryRevision,
                response.storedEquipmentRevision,
                response.worldRevision,
                response.mapId,
                response.instanceId,
                response.state == null ? null : FromDto(response.state),
                response.error);
        }

        public async Task<PlayerItemGrantPersistenceResult> TryGrantAsync(
            PlayerItemGrantPersistenceRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!TryGetMutationLeaseOwnerToken(request.CharacterId, out string leaseOwnerToken))
                return new PlayerItemGrantPersistenceResult(false, false, 0, 0, null, "character authority lease is not held");

            BackendPlayerItemGrantResponse response = await _backend.GrantPlayerItemAsync(
                new BackendPlayerItemGrantRequest
                {
                    accountId = request.AccountId.Value,
                    characterId = request.CharacterId.Value,
                    leaseOwnerToken = leaseOwnerToken,
                    expectedInventoryRevision = request.ExpectedInventoryRevision,
                    expectedEquipmentRevision = request.ExpectedEquipmentRevision,
                    definitionId = request.DefinitionId,
                    quantity = request.Quantity,
                },
                cancellationToken).ConfigureAwait(false);

            return new PlayerItemGrantPersistenceResult(
                response.success && response.accepted,
                response.stale,
                response.storedInventoryRevision,
                response.storedEquipmentRevision,
                response.state == null ? null : FromDto(response.state),
                response.error);
        }

        public async Task<PlayerItemBundleGrantPersistenceResult> TryGrantBundleAsync(
            PlayerItemBundleGrantPersistenceRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!TryGetMutationLeaseOwnerToken(request.CharacterId, out string leaseOwnerToken))
                return new PlayerItemBundleGrantPersistenceResult(false, false, 0, 0, null, "character authority lease is not held");

            var items = new BackendRewardItemDto[request.Items.Length];
            for (int i = 0; i < items.Length; ++i)
                items[i] = new BackendRewardItemDto { itemDataId = request.Items[i].ItemDataId, quantity = request.Items[i].Quantity };

            BackendPlayerItemBundleGrantResponse response = await _backend.GrantPlayerItemBundleAsync(
                new BackendPlayerItemBundleGrantRequest
                {
                    accountId = request.AccountId.Value,
                    characterId = request.CharacterId.Value,
                    leaseOwnerToken = leaseOwnerToken,
                    expectedInventoryRevision = request.ExpectedInventoryRevision,
                    expectedEquipmentRevision = request.ExpectedEquipmentRevision,
                    items = items,
                }, cancellationToken).ConfigureAwait(false);

            return new PlayerItemBundleGrantPersistenceResult(
                response.success && response.accepted,
                response.stale,
                response.storedInventoryRevision,
                response.storedEquipmentRevision,
                response.state == null ? null : FromDto(response.state),
                response.error);
        }

        public async Task<PlayerCraftPersistenceResult> TryCraftAsync(
            PlayerCraftPersistenceRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!TryGetMutationLeaseOwnerToken(request.CharacterId, out string leaseOwnerToken))
                return new PlayerCraftPersistenceResult(false, false, 0, 0, null, "character authority lease is not held");

            BackendPlayerCraftResponse response = await _backend.CraftPlayerItemAsync(
                new BackendPlayerCraftRequest
                {
                    accountId = request.AccountId.Value,
                    characterId = request.CharacterId.Value,
                    leaseOwnerToken = leaseOwnerToken,
                    expectedInventoryRevision = request.ExpectedInventoryRevision,
                    expectedEquipmentRevision = request.ExpectedEquipmentRevision,
                    recipeDataId = request.RecipeDataId,
                }, cancellationToken).ConfigureAwait(false);

            return new PlayerCraftPersistenceResult(
                response.success && response.accepted,
                response.stale,
                response.storedInventoryRevision,
                response.storedEquipmentRevision,
                response.state == null ? null : FromDto(response.state),
                response.error);
        }

        public async Task<WorldItemPersistenceWorld[]> LoadAllAsync(CancellationToken cancellationToken)
        {
            BackendWorldItemsLoadResponse response = await _backend.LoadWorldItemsAsync(cancellationToken).ConfigureAwait(false);
            if (response == null || !response.success) throw new InvalidOperationException("Backend world-item load failed.");
            BackendWorldItemWorldDto[] worlds = response.worlds ?? Array.Empty<BackendWorldItemWorldDto>();
            var result = new WorldItemPersistenceWorld[worlds.Length];
            for (int i = 0; i < worlds.Length; ++i)
            {
                BackendWorldItemWorldDto world = worlds[i];
                BackendWorldItemDto[] items = world?.items ?? Array.Empty<BackendWorldItemDto>();
                var converted = new WorldItemState[items.Length];
                for (int j = 0; j < items.Length; ++j) converted[j] = ToWorldItemState(items[j]);
                result[i] = new WorldItemPersistenceWorld(world?.mapId ?? string.Empty, world?.instanceId ?? string.Empty, Math.Max(0, world?.revision ?? 0), converted);
            }
            return result;
        }

        private static WorldItemState ToWorldItemState(BackendWorldItemDto item) =>
            item == null || item.itemInstanceId <= 0 ? null : new WorldItemState(
                new ItemInstanceId(item.itemInstanceId), item.definitionId, item.quantity, item.durability, item.revision,
                item.mapId, item.instanceId, new WorldPosition(item.positionX, item.positionY, item.positionZ),
                item.loadedAmmoDefinitionId, item.loadedRounds, item.magazineRevision);

        public static PlayerSystemsPersistenceRecord FromDto(BackendPlayerSystemsSnapshotDto dto)
        {
            if (dto == null || dto.inventoryCapacity < 1) return null;
            BackendPersistedItemDto[] inv = dto.inventoryItems ?? Array.Empty<BackendPersistedItemDto>();
            var inventory = new PersistedInventoryItem[inv.Length];
            for (int i = 0; i < inv.Length; ++i)
                inventory[i] = new PersistedInventoryItem(
                    inv[i].inventorySlot,
                    new ItemInstanceId(inv[i].itemInstanceId),
                    inv[i].definitionId,
                    inv[i].quantity,
                    inv[i].durability,
                    inv[i].revision,
                    inv[i].loadedAmmoDefinitionId,
                    inv[i].loadedRounds,
                    inv[i].magazineRevision);

            BackendPersistedItemDto[] eq = dto.equipmentItems ?? Array.Empty<BackendPersistedItemDto>();
            var equipment = new PersistedEquipmentItem[eq.Length];
            for (int i = 0; i < eq.Length; ++i)
                equipment[i] = new PersistedEquipmentItem(
                    eq[i].equipmentSlotId,
                    new ItemInstanceId(eq[i].itemInstanceId),
                    eq[i].definitionId,
                    eq[i].quantity,
                    eq[i].durability,
                    eq[i].revision,
                    eq[i].loadedAmmoDefinitionId,
                    eq[i].loadedRounds,
                    eq[i].magazineRevision);

            return new PlayerSystemsPersistenceRecord(dto.inventoryCapacity, dto.inventoryRevision, dto.equipmentRevision, inventory, equipment);
        }

        private static BackendPlayerSystemsSnapshotDto ToDto(InventoryState inventory, EquipmentState equipment)
        {
            var inv = new List<BackendPersistedItemDto>();
            ItemInstanceState[] slots = inventory.CopySlots();
            for (int i = 0; i < slots.Length; ++i)
            {
                ItemInstanceState item = slots[i];
                if (item == null) continue;
                inv.Add(ToDto(item, i, string.Empty));
            }

            EquippedItemState[] equipped = equipment.Snapshot();
            var eq = new BackendPersistedItemDto[equipped.Length];
            for (int i = 0; i < equipped.Length; ++i)
                eq[i] = ToDto(equipped[i].Item, -1, equipped[i].SlotId);

            return new BackendPlayerSystemsSnapshotDto
            {
                inventoryCapacity = inventory.Capacity, inventoryRevision = inventory.Revision, equipmentRevision = equipment.Revision,
                inventoryItems = inv.ToArray(), equipmentItems = eq,
            };
        }

        private static BackendPersistedItemDto ToDto(ItemInstanceState item, int inventorySlot, string equipmentSlotId) =>
            new BackendPersistedItemDto
            {
                itemInstanceId = item.ItemInstanceId.Value, definitionId = item.DefinitionId, quantity = item.Quantity,
                durability = item.Durability, revision = item.Revision, inventorySlot = inventorySlot,
                equipmentSlotId = equipmentSlotId ?? string.Empty,
                loadedAmmoDefinitionId = item.LoadedAmmoDefinitionId,
                loadedRounds = item.LoadedRounds,
                magazineRevision = item.MagazineRevision,
            };
    }
}
