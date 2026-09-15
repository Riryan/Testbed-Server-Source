using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Content;
using Game.Server.Application.Persistence;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Application.WorldItems;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Stats;
using Game.Server.Domain.WorldItems;
using Game.Shared.Content;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;
using Game.Shared.World;

namespace Game.Server.Application.Items
{
    public readonly struct PlayerItemPartialGrantResult
    {
        public PlayerItemOperationResult ItemResult { get; }
        public RewardItemDefinition[] AcceptedItems { get; }
        public RewardItemDefinition[] OverflowItems { get; }
        public bool Success => ItemResult.Success;

        public PlayerItemPartialGrantResult(
            PlayerItemOperationResult itemResult,
            RewardItemDefinition[] acceptedItems,
            RewardItemDefinition[] overflowItems)
        {
            ItemResult = itemResult;
            AcceptedItems = acceptedItems ?? Array.Empty<RewardItemDefinition>();
            OverflowItems = overflowItems ?? Array.Empty<RewardItemDefinition>();
        }
    }

    /// <summary>
    /// Authoritative inventory/equipment application service. Operations are planned
    /// against immutable runtime snapshots, committed transactionally by BackendServer,
    /// then installed into the exact live runtime. Backend never becomes gameplay authority.
    /// </summary>
    public sealed class PlayerItemService
    {
        private readonly GameplayContentCatalog _content;
        private readonly IPlayerSystemsRepository _repository;
        private readonly IPlayerItemLifecycleRepository _lifecycleRepository;
        private readonly CharacterResourceService _resources;
        private readonly StatusEffectService _statusEffects;
        private readonly ConditionalWeakTable<PlayerRuntime, SemaphoreSlim> _operationGates = new ConditionalWeakTable<PlayerRuntime, SemaphoreSlim>();

        public event Action<PlayerRuntime, PlayerItemsSnapshot, PlayerItemsSnapshot> Changed;

        public PlayerItemService(
            GameplayContentCatalog content,
            IPlayerSystemsRepository repository,
            IPlayerItemLifecycleRepository lifecycleRepository = null,
            CharacterResourceService resources = null,
            StatusEffectService statusEffects = null)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _lifecycleRepository = lifecycleRepository ?? repository as IPlayerItemLifecycleRepository;
            _resources = resources;
            _statusEffects = statusEffects;
        }

        public PlayerItemsSnapshot GetSnapshot(PlayerRuntime runtime)
        {
            if (runtime == null) return null;
            PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
            return state == null ? null : BuildView(state);
        }

        public bool HasEquippedItemTag(PlayerRuntime runtime, string tag)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(tag)) return false;
            PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
            if (state?.Equipment == null) return false;

            IReadOnlyList<EquippedItemState> equipped = state.Equipment.Snapshot();
            for (int i = 0; i < equipped.Count; ++i)
            {
                ItemInstanceState item = equipped[i]?.Item;
                if (item == null || !_content.TryGetItem(item.DefinitionId, out ItemDefinition definition))
                    continue;
                string[] tags = definition.tags ?? Array.Empty<string>();
                for (int t = 0; t < tags.Length; ++t)
                    if (string.Equals(tags[t], tag, StringComparison.Ordinal)) return true;
            }
            return false;
        }



        public bool HasItemTag(PlayerRuntime runtime, string tag)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(tag)) return false;
            tag = tag.Trim();
            PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
            if (state == null) return false;

            ItemInstanceState[] inventory = state.Inventory?.CopySlots() ?? Array.Empty<ItemInstanceState>();
            for (int i = 0; i < inventory.Length; ++i)
                if (ItemHasTag(inventory[i], tag)) return true;

            EquippedItemState[] equipped = state.Equipment?.Snapshot() ?? Array.Empty<EquippedItemState>();
            for (int i = 0; i < equipped.Length; ++i)
                if (ItemHasTag(equipped[i]?.Item, tag)) return true;
            return false;
        }

        private bool ItemHasTag(ItemInstanceState item, string tag)
        {
            if (item == null || !_content.TryGetItem(item.DefinitionId, out ItemDefinition definition) || definition == null)
                return false;
            string[] tags = definition.tags ?? Array.Empty<string>();
            for (int i = 0; i < tags.Length; ++i)
                if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public Task<PlayerItemOperationResult> GrantBundleAsync(
            PlayerRuntime runtime,
            RewardItemDefinition[] rewards,
            CancellationToken cancellationToken) =>
            RunSerializedAsync(runtime, cancellationToken, token => GrantBundleCoreAsync(runtime, rewards, token));

        public Task<PlayerItemPartialGrantResult> GrantBundlePartiallyAsync(
            PlayerRuntime runtime,
            RewardItemDefinition[] rewards,
            CancellationToken cancellationToken) =>
            RunSerializedAsync(
                runtime,
                cancellationToken,
                token => GrantBundlePartiallyCoreAsync(runtime, rewards, token),
                new PlayerItemPartialGrantResult(
                    PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player runtime is unavailable"),
                    Array.Empty<RewardItemDefinition>(),
                    rewards ?? Array.Empty<RewardItemDefinition>()));

        private async Task<PlayerItemPartialGrantResult> GrantBundlePartiallyCoreAsync(
            PlayerRuntime runtime,
            RewardItemDefinition[] rewards,
            CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null)
            {
                return new PlayerItemPartialGrantResult(
                    PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable"),
                    Array.Empty<RewardItemDefinition>(),
                    rewards ?? Array.Empty<RewardItemDefinition>());
            }

            if (!TryPlanPartialRewardBundle(
                    current,
                    rewards,
                    out RewardItemDefinition[] accepted,
                    out RewardItemDefinition[] overflow,
                    out string planningError))
            {
                return new PlayerItemPartialGrantResult(
                    PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ContentUnavailable, planningError, BuildView(current)),
                    Array.Empty<RewardItemDefinition>(),
                    rewards ?? Array.Empty<RewardItemDefinition>());
            }

            if (accepted.Length == 0)
            {
                return new PlayerItemPartialGrantResult(
                    PlayerItemOperationResult.Succeeded(BuildView(current)),
                    accepted,
                    overflow);
            }

            if (_lifecycleRepository == null)
            {
                return new PlayerItemPartialGrantResult(
                    PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ContentUnavailable, "item lifecycle services are unavailable", BuildView(current)),
                    Array.Empty<RewardItemDefinition>(),
                    rewards ?? Array.Empty<RewardItemDefinition>());
            }

            var entries = new PlayerRewardItemPersistenceEntry[accepted.Length];
            for (int i = 0; i < accepted.Length; ++i)
                entries[i] = new PlayerRewardItemPersistenceEntry(accepted[i].itemDataId, accepted[i].quantity);

            PlayerItemBundleGrantPersistenceResult persisted;
            try
            {
                persisted = await _lifecycleRepository.TryGrantBundleAsync(
                    new PlayerItemBundleGrantPersistenceRequest(
                        runtime.AccountId,
                        runtime.CharacterId,
                        current.Inventory.Revision,
                        current.Equipment.Revision,
                        entries),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                PersistenceReconciliation reconciliation = await ReconcilePersistenceAsync(
                    runtime, current, "partial item reward", cancellationToken).ConfigureAwait(false);
                if (reconciliation.Loaded && InventoryRevisionAdvancedOnce(current, reconciliation.State))
                    return new PlayerItemPartialGrantResult(reconciliation.Installed, accepted, overflow);

                return new PlayerItemPartialGrantResult(
                    ReconciledUncertainFailure(reconciliation, current, "partial item reward"),
                    Array.Empty<RewardItemDefinition>(),
                    rewards ?? Array.Empty<RewardItemDefinition>());
            }

            if (!persisted.Accepted || persisted.State == null)
            {
                if (persisted.Stale)
                {
                    PlayerItemOperationResult stale = await ReconcileStaleAsync(
                        runtime, current, "partial item reward", persisted.Error, cancellationToken).ConfigureAwait(false);
                    return new PlayerItemPartialGrantResult(
                        stale,
                        Array.Empty<RewardItemDefinition>(),
                        rewards ?? Array.Empty<RewardItemDefinition>());
                }

                PlayerItemOperationStatus status = IsInventoryFullError(persisted.Error)
                    ? PlayerItemOperationStatus.InventoryFull
                    : PlayerItemOperationStatus.PersistenceRejected;
                return new PlayerItemPartialGrantResult(
                    PlayerItemOperationResult.Failed(
                        status,
                        string.IsNullOrWhiteSpace(persisted.Error) ? "partial item reward transaction rejected" : persisted.Error,
                        BuildView(current)),
                    Array.Empty<RewardItemDefinition>(),
                    rewards ?? Array.Empty<RewardItemDefinition>());
            }

            PlayerItemOperationResult installed = InstallPersistedState(runtime, current, persisted.State, "partial item reward");
            if (!installed.Success)
            {
                return new PlayerItemPartialGrantResult(
                    installed,
                    Array.Empty<RewardItemDefinition>(),
                    rewards ?? Array.Empty<RewardItemDefinition>());
            }

            return new PlayerItemPartialGrantResult(installed, accepted, overflow);
        }

        private bool TryPlanPartialRewardBundle(
            PlayerItemSystemsRuntimeSnapshot current,
            RewardItemDefinition[] rewards,
            out RewardItemDefinition[] accepted,
            out RewardItemDefinition[] overflow,
            out string error)
        {
            accepted = Array.Empty<RewardItemDefinition>();
            overflow = Array.Empty<RewardItemDefinition>();
            error = string.Empty;
            rewards ??= Array.Empty<RewardItemDefinition>();
            if (rewards.Length == 0)
                return true;

            var aggregated = new Dictionary<ushort, int>();
            for (int i = 0; i < rewards.Length; ++i)
            {
                RewardItemDefinition reward = rewards[i];
                if (reward == null || reward.quantity < 1)
                {
                    error = "reward item definition is invalid";
                    return false;
                }

                ushort dataId = reward.itemDataId;
                if (dataId == 0 &&
                    !string.IsNullOrWhiteSpace(reward.itemDefinitionId) &&
                    _content.TryGetItem(reward.itemDefinitionId, out ItemDefinition semantic))
                    dataId = semantic.dataId;

                if (dataId == 0 || !_content.TryGetItem(dataId, out ItemDefinition definition) || definition == null)
                {
                    error = "reward item is not loaded";
                    return false;
                }

                long next = (long)(aggregated.TryGetValue(dataId, out int existing) ? existing : 0) + reward.quantity;
                if (next > 1000000)
                {
                    error = "reward item quantity is too large";
                    return false;
                }
                aggregated[dataId] = (int)next;
            }

            ItemInstanceState[] sourceSlots = current.Inventory.CopySlots();
            var simulatedDefinition = new string[sourceSlots.Length];
            var simulatedDurability = new int[sourceSlots.Length];
            var simulatedQuantity = new int[sourceSlots.Length];
            for (int i = 0; i < sourceSlots.Length; ++i)
            {
                ItemInstanceState item = sourceSlots[i];
                if (item == null) continue;
                simulatedDefinition[i] = item.DefinitionId;
                simulatedDurability[i] = item.Durability;
                simulatedQuantity[i] = item.Quantity;
            }

            var acceptedList = new List<RewardItemDefinition>(aggregated.Count);
            var overflowList = new List<RewardItemDefinition>(aggregated.Count);
            foreach (KeyValuePair<ushort, int> grant in aggregated)
            {
                if (!_content.TryGetItem(grant.Key, out ItemDefinition definition) || definition == null)
                {
                    error = "reward item is not loaded";
                    return false;
                }

                int remaining = grant.Value;
                int maxStack = Math.Max(1, definition.maxStack);
                for (int i = 0; i < simulatedQuantity.Length && remaining > 0; ++i)
                {
                    if (simulatedQuantity[i] <= 0 ||
                        !string.Equals(simulatedDefinition[i], definition.definitionId, StringComparison.Ordinal) ||
                        simulatedDurability[i] != definition.maxDurability ||
                        simulatedQuantity[i] >= maxStack)
                        continue;

                    int add = Math.Min(maxStack - simulatedQuantity[i], remaining);
                    simulatedQuantity[i] += add;
                    remaining -= add;
                }

                while (remaining > 0)
                {
                    int empty = -1;
                    for (int i = 0; i < simulatedQuantity.Length; ++i)
                    {
                        if (simulatedQuantity[i] <= 0)
                        {
                            empty = i;
                            break;
                        }
                    }
                    if (empty < 0)
                        break;

                    int add = Math.Min(maxStack, remaining);
                    simulatedDefinition[empty] = definition.definitionId;
                    simulatedDurability[empty] = definition.maxDurability;
                    simulatedQuantity[empty] = add;
                    remaining -= add;
                }

                int acceptedQuantity = grant.Value - remaining;
                if (acceptedQuantity > 0)
                {
                    acceptedList.Add(new RewardItemDefinition
                    {
                        itemDataId = definition.dataId,
                        itemDefinitionId = definition.definitionId,
                        quantity = acceptedQuantity,
                    });
                }
                if (remaining > 0)
                {
                    overflowList.Add(new RewardItemDefinition
                    {
                        itemDataId = definition.dataId,
                        itemDefinitionId = definition.definitionId,
                        quantity = remaining,
                    });
                }
            }

            accepted = acceptedList.ToArray();
            overflow = overflowList.ToArray();
            return true;
        }

        private async Task<PlayerItemOperationResult> GrantBundleCoreAsync(
            PlayerRuntime runtime,
            RewardItemDefinition[] rewards,
            CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable");
            rewards = rewards ?? Array.Empty<RewardItemDefinition>();
            if (rewards.Length == 0)
                return PlayerItemOperationResult.Succeeded(BuildView(current));
            if (_lifecycleRepository == null)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ContentUnavailable, "item lifecycle services are unavailable", BuildView(current));

            var entries = new PlayerRewardItemPersistenceEntry[rewards.Length];
            for (int i = 0; i < rewards.Length; ++i)
            {
                RewardItemDefinition reward = rewards[i];
                if (reward == null || reward.quantity < 1)
                    return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ContentUnavailable, "reward item definition is invalid", BuildView(current));

                ushort itemDataId = reward.itemDataId;
                if (itemDataId == 0 && !string.IsNullOrWhiteSpace(reward.itemDefinitionId) &&
                    _content.TryGetItem(reward.itemDefinitionId, out ItemDefinition semanticItem))
                    itemDataId = semanticItem.dataId;
                if (itemDataId == 0 || !_content.TryGetItem(itemDataId, out ItemDefinition _))
                    return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ContentUnavailable, "reward item is not loaded", BuildView(current));
                entries[i] = new PlayerRewardItemPersistenceEntry(itemDataId, reward.quantity);
            }

            PlayerItemBundleGrantPersistenceResult persisted;
            try
            {
                persisted = await _lifecycleRepository.TryGrantBundleAsync(
                    new PlayerItemBundleGrantPersistenceRequest(
                        runtime.AccountId, runtime.CharacterId,
                        current.Inventory.Revision, current.Equipment.Revision,
                        entries), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                PersistenceReconciliation reconciliation = await ReconcilePersistenceAsync(
                    runtime, current, "item reward", cancellationToken).ConfigureAwait(false);
                return reconciliation.Loaded && InventoryRevisionAdvancedOnce(current, reconciliation.State)
                    ? reconciliation.Installed
                    : ReconciledUncertainFailure(reconciliation, current, "item reward");
            }

            if (!persisted.Accepted || persisted.State == null)
            {
                if (persisted.Stale)
                    return await ReconcileStaleAsync(runtime, current, "item reward", persisted.Error, cancellationToken).ConfigureAwait(false);
                PlayerItemOperationStatus status = IsInventoryFullError(persisted.Error)
                    ? PlayerItemOperationStatus.InventoryFull
                    : PlayerItemOperationStatus.PersistenceRejected;
                return PlayerItemOperationResult.Failed(status,
                    string.IsNullOrWhiteSpace(persisted.Error) ? "item reward transaction rejected" : persisted.Error,
                    BuildView(current));
            }
            return InstallPersistedState(runtime, current, persisted.State, "item reward");
        }

        public Task<PlayerItemOperationResult> CraftRecipeItemsAsync(
            PlayerRuntime runtime,
            ushort recipeDataId,
            CancellationToken cancellationToken) =>
            RunSerializedAsync(runtime, cancellationToken, token => CraftRecipeItemsCoreAsync(runtime, recipeDataId, token));

        private async Task<PlayerItemOperationResult> CraftRecipeItemsCoreAsync(
            PlayerRuntime runtime,
            ushort recipeDataId,
            CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable");
            if (recipeDataId == 0 || !_content.TryGetRecipe(recipeDataId, out RecipeDefinition _))
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ContentUnavailable, "recipe is unavailable", BuildView(current));
            if (_lifecycleRepository == null)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ContentUnavailable, "item lifecycle services are unavailable", BuildView(current));

            PlayerCraftPersistenceResult persisted;
            try
            {
                persisted = await _lifecycleRepository.TryCraftAsync(
                    new PlayerCraftPersistenceRequest(runtime.AccountId, runtime.CharacterId,
                        current.Inventory.Revision, current.Equipment.Revision, recipeDataId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                PersistenceReconciliation reconciliation = await ReconcilePersistenceAsync(
                    runtime, current, "craft", cancellationToken).ConfigureAwait(false);
                return reconciliation.Loaded && InventoryRevisionAdvancedOnce(current, reconciliation.State)
                    ? reconciliation.Installed
                    : ReconciledUncertainFailure(reconciliation, current, "craft");
            }

            if (!persisted.Accepted || persisted.State == null)
            {
                if (persisted.Stale)
                    return await ReconcileStaleAsync(runtime, current, "craft", persisted.Error, cancellationToken).ConfigureAwait(false);
                PlayerItemOperationStatus status = IsInventoryFullError(persisted.Error)
                    ? PlayerItemOperationStatus.InventoryFull
                    : PlayerItemOperationStatus.PersistenceRejected;
                return PlayerItemOperationResult.Failed(status,
                    string.IsNullOrWhiteSpace(persisted.Error) ? "craft transaction rejected" : persisted.Error,
                    BuildView(current));
            }
            return InstallPersistedState(runtime, current, persisted.State, "craft");
        }

        public Task<AmmoReloadInventoryResult> ConsumeAmmoForReloadAsync(
            PlayerRuntime runtime,
            string ammoFamily,
            string preferredAmmoDefinitionId,
            int maximumRounds,
            CancellationToken cancellationToken) =>
            RunSerializedAsync(runtime, cancellationToken,
                token => ConsumeAmmoForReloadCoreAsync(runtime, ammoFamily, preferredAmmoDefinitionId, maximumRounds, token),
                AmmoReloadInventoryResult.Failed(PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player runtime is unavailable")));

        private async Task<AmmoReloadInventoryResult> ConsumeAmmoForReloadCoreAsync(
            PlayerRuntime runtime,
            string ammoFamily,
            string preferredAmmoDefinitionId,
            int maximumRounds,
            CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null)
                return AmmoReloadInventoryResult.Failed(PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable"));
            if (string.IsNullOrWhiteSpace(ammoFamily) || maximumRounds < 1)
                return AmmoReloadInventoryResult.Failed(PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ItemUnavailable, "reload ammunition request is invalid", BuildView(current)));
            if (_lifecycleRepository == null)
                return AmmoReloadInventoryResult.Failed(PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ContentUnavailable, "item lifecycle services are unavailable", BuildView(current)));

            ItemInstanceState weapon = current.Equipment?.Get("MainHand");
            if (weapon == null || !_content.TryGetItem(weapon.DefinitionId, out ItemDefinition weaponDefinition) ||
                !string.Equals(weaponDefinition.ammoFamily ?? string.Empty, ammoFamily.Trim(), StringComparison.OrdinalIgnoreCase))
                return AmmoReloadInventoryResult.Failed(PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ItemUnavailable, "equipped firearm is unavailable", BuildView(current)));

            PlayerAmmoReloadPersistenceResult persisted;
            try
            {
                persisted = await _lifecycleRepository.TryConsumeAmmoForReloadAsync(
                    new PlayerAmmoReloadPersistenceRequest(
                        runtime.AccountId, runtime.CharacterId,
                        current.Inventory.Revision, current.Equipment.Revision,
                        ammoFamily, preferredAmmoDefinitionId, maximumRounds,
                        weapon.ItemInstanceId, weapon.MagazineRevision,
                        weapon.LoadedAmmoDefinitionId, weapon.LoadedRounds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                PersistenceReconciliation reconciliation = await ReconcilePersistenceAsync(
                    runtime, current, "reload", cancellationToken).ConfigureAwait(false);
                if (reconciliation.Loaded &&
                    InventoryRevisionAdvancedOnce(current, reconciliation.State) &&
                    TryGetPersistedEquipmentItem(reconciliation.State, weapon.ItemInstanceId, out PersistedEquipmentItem persistedWeapon))
                {
                    int consumedRounds = persistedWeapon.LoadedRounds - weapon.LoadedRounds;
                    if (consumedRounds > 0 && consumedRounds <= maximumRounds)
                    {
                        return new AmmoReloadInventoryResult(
                            true,
                            persistedWeapon.LoadedAmmoDefinitionId,
                            consumedRounds,
                            reconciliation.Installed);
                    }
                }

                return AmmoReloadInventoryResult.Failed(
                    ReconciledUncertainFailure(reconciliation, current, "reload"));
            }

            if (!persisted.Accepted || persisted.State == null)
            {
                if (persisted.Stale)
                {
                    PlayerItemOperationResult stale = await ReconcileStaleAsync(
                        runtime, current, "reload", persisted.Error, cancellationToken).ConfigureAwait(false);
                    return AmmoReloadInventoryResult.Failed(stale);
                }

                PlayerItemOperationStatus status = persisted.AmmoUnavailable
                    ? PlayerItemOperationStatus.ItemUnavailable
                    : PlayerItemOperationStatus.PersistenceRejected;
                return AmmoReloadInventoryResult.Failed(PlayerItemOperationResult.Failed(status,
                    string.IsNullOrWhiteSpace(persisted.Error) ? "reload transaction rejected" : persisted.Error,
                    BuildView(current)));
            }

            PlayerItemOperationResult installed = InstallPersistedState(runtime, current, persisted.State, "reload");
            return installed.Success
                ? new AmmoReloadInventoryResult(true, persisted.AmmoDefinitionId, persisted.ConsumedRounds, installed)
                : AmmoReloadInventoryResult.Failed(installed);
        }

        private PlayerItemOperationResult InstallPersistedState(
            PlayerRuntime runtime,
            PlayerItemSystemsRuntimeSnapshot current,
            PlayerSystemsPersistenceRecord persisted,
            string operationName)
        {
            PlayerItemsSnapshot previousSnapshot = BuildView(current);
            InventoryState inventory = BuildInventory(persisted);
            EquipmentState equipment = BuildEquipment(persisted);
            StatsState stats = CalculateStats(equipment);
            if (!runtime.TryCommitPlayerItemSystems(
                    current.Inventory.Revision, current.Equipment.Revision,
                    inventory, equipment, stats))
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.StaleState,
                    $"live item state changed while {operationName} transaction was committing", GetSnapshot(runtime));

            PlayerItemsSnapshot snapshot = GetSnapshot(runtime);
            Changed?.Invoke(runtime, previousSnapshot, snapshot);
            return PlayerItemOperationResult.Succeeded(snapshot);
        }

        private readonly struct PersistenceReconciliation
        {
            public bool Loaded { get; }
            public PlayerSystemsPersistenceRecord State { get; }
            public PlayerItemOperationResult Installed { get; }

            public PersistenceReconciliation(
                bool loaded,
                PlayerSystemsPersistenceRecord state,
                PlayerItemOperationResult installed)
            {
                Loaded = loaded;
                State = state;
                Installed = installed;
            }
        }

        private async Task<PersistenceReconciliation> ReconcilePersistenceAsync(
            PlayerRuntime runtime,
            PlayerItemSystemsRuntimeSnapshot current,
            string operationName,
            CancellationToken cancellationToken)
        {
            try
            {
                PlayerSystemsPersistenceRecord persisted = await _repository
                    .LoadForReconciliationAsync(runtime.AccountId, runtime.CharacterId, cancellationToken)
                    .ConfigureAwait(false);
                if (persisted == null)
                {
                    return new PersistenceReconciliation(
                        false,
                        null,
                        PlayerItemOperationResult.Failed(
                            PlayerItemOperationStatus.PersistenceRejected,
                            $"{operationName} persistence reconciliation could not load authoritative item state",
                            BuildView(current)));
                }

                PlayerItemOperationResult installed =
                    InstallPersistedState(runtime, current, persisted, $"{operationName} reconciliation");
                return new PersistenceReconciliation(installed.Success, persisted, installed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new PersistenceReconciliation(
                    false,
                    null,
                    PlayerItemOperationResult.Failed(
                        PlayerItemOperationStatus.PersistenceRejected,
                        $"{operationName} persistence outcome is uncertain and authoritative state could not be reconciled",
                        GetSnapshot(runtime) ?? BuildView(current)));
            }
        }

        private async Task<PlayerItemOperationResult> ReconcileStaleAsync(
            PlayerRuntime runtime,
            PlayerItemSystemsRuntimeSnapshot current,
            string operationName,
            string error,
            CancellationToken cancellationToken)
        {
            PersistenceReconciliation reconciliation = await ReconcilePersistenceAsync(
                runtime, current, operationName, cancellationToken).ConfigureAwait(false);
            if (!reconciliation.Loaded)
                return reconciliation.Installed;
            return PlayerItemOperationResult.Failed(
                PlayerItemOperationStatus.StaleState,
                string.IsNullOrWhiteSpace(error) ? $"{operationName} rejected stale state; authoritative state reconciled" : error,
                reconciliation.Installed.Snapshot);
        }

        private static PlayerItemOperationResult ReconciledUncertainFailure(
            PersistenceReconciliation reconciliation,
            PlayerItemSystemsRuntimeSnapshot current,
            string operationName)
        {
            if (!reconciliation.Loaded)
                return reconciliation.Installed;

            bool unchanged = reconciliation.State.InventoryRevision == current.Inventory.Revision &&
                             reconciliation.State.EquipmentRevision == current.Equipment.Revision;
            return PlayerItemOperationResult.Failed(
                unchanged ? PlayerItemOperationStatus.PersistenceRejected : PlayerItemOperationStatus.StaleState,
                unchanged
                    ? $"{operationName} persistence failed before the mutation committed"
                    : $"{operationName} persistence outcome was reconciled to a different authoritative state",
                reconciliation.Installed.Snapshot);
        }

        private static bool InventoryRevisionAdvancedOnce(
            PlayerItemSystemsRuntimeSnapshot current,
            PlayerSystemsPersistenceRecord persisted) =>
            persisted != null &&
            current != null &&
            current.Inventory.Revision != long.MaxValue &&
            persisted.InventoryRevision == current.Inventory.Revision + 1 &&
            persisted.EquipmentRevision == current.Equipment.Revision;

        private static bool PersistedStateMatches(
            PlayerSystemsPersistenceRecord persisted,
            InventoryState inventory,
            EquipmentState equipment,
            ItemInstanceId remappableTransientItemId = default)
        {
            if (persisted == null || inventory == null || equipment == null ||
                persisted.InventoryCapacity != inventory.Capacity ||
                persisted.InventoryRevision != inventory.Revision ||
                persisted.EquipmentRevision != equipment.Revision)
                return false;

            ItemInstanceState[] expectedInventory = inventory.CopySlots();
            var actualBySlot = new Dictionary<int, PersistedInventoryItem>();
            PersistedInventoryItem[] persistedInventory = persisted.InventoryItems ?? Array.Empty<PersistedInventoryItem>();
            for (int i = 0; i < persistedInventory.Length; ++i)
            {
                PersistedInventoryItem item = persistedInventory[i];
                if (item == null || !actualBySlot.TryAdd(item.SlotIndex, item))
                    return false;
            }

            for (int i = 0; i < expectedInventory.Length; ++i)
            {
                ItemInstanceState expected = expectedInventory[i];
                bool hasActual = actualBySlot.TryGetValue(i, out PersistedInventoryItem actual);
                if (expected == null)
                {
                    if (hasActual) return false;
                    continue;
                }
                if (!hasActual)
                    return false;
                if (!SameItem(expected, actual))
                {
                    bool allowedTransientRemap =
                        remappableTransientItemId.IsValid &&
                        expected.ItemInstanceId == remappableTransientItemId &&
                        TransientWorldItemIds.IsTransient(expected.ItemInstanceId.Value) &&
                        actual.ItemInstanceId.IsValid &&
                        !TransientWorldItemIds.IsTransient(actual.ItemInstanceId.Value) &&
                        SameItemExceptIdentity(expected, actual);
                    if (!allowedTransientRemap)
                        return false;
                }
            }

            EquippedItemState[] expectedEquipment = equipment.Snapshot();
            PersistedEquipmentItem[] persistedEquipment = persisted.EquipmentItems ?? Array.Empty<PersistedEquipmentItem>();
            if (expectedEquipment.Length != persistedEquipment.Length)
                return false;
            var actualByEquipmentSlot = new Dictionary<string, PersistedEquipmentItem>(StringComparer.Ordinal);
            for (int i = 0; i < persistedEquipment.Length; ++i)
            {
                PersistedEquipmentItem item = persistedEquipment[i];
                if (item == null || string.IsNullOrWhiteSpace(item.SlotId) || !actualByEquipmentSlot.TryAdd(item.SlotId, item))
                    return false;
            }
            for (int i = 0; i < expectedEquipment.Length; ++i)
            {
                EquippedItemState expected = expectedEquipment[i];
                if (expected == null ||
                    !actualByEquipmentSlot.TryGetValue(expected.SlotId, out PersistedEquipmentItem actual) ||
                    !SameItem(expected.Item, actual))
                    return false;
            }
            return true;
        }

        private static bool SameItem(ItemInstanceState expected, PersistedInventoryItem actual) =>
            expected != null && actual != null &&
            expected.ItemInstanceId == actual.ItemInstanceId &&
            string.Equals(expected.DefinitionId, actual.DefinitionId, StringComparison.Ordinal) &&
            expected.Quantity == actual.Quantity &&
            expected.Durability == actual.Durability &&
            expected.Revision == actual.Revision &&
            string.Equals(expected.LoadedAmmoDefinitionId ?? string.Empty, actual.LoadedAmmoDefinitionId ?? string.Empty, StringComparison.Ordinal) &&
            expected.LoadedRounds == actual.LoadedRounds &&
            expected.MagazineRevision == actual.MagazineRevision;


        private static bool SameItemExceptIdentity(ItemInstanceState expected, PersistedInventoryItem actual) =>
            expected != null && actual != null &&
            string.Equals(expected.DefinitionId, actual.DefinitionId, StringComparison.Ordinal) &&
            expected.Quantity == actual.Quantity &&
            expected.Durability == actual.Durability &&
            expected.Revision == actual.Revision &&
            string.Equals(expected.LoadedAmmoDefinitionId ?? string.Empty, actual.LoadedAmmoDefinitionId ?? string.Empty, StringComparison.Ordinal) &&
            expected.LoadedRounds == actual.LoadedRounds &&
            expected.MagazineRevision == actual.MagazineRevision;

        private static bool SameItem(ItemInstanceState expected, PersistedEquipmentItem actual) =>
            expected != null && actual != null &&
            expected.ItemInstanceId == actual.ItemInstanceId &&
            string.Equals(expected.DefinitionId, actual.DefinitionId, StringComparison.Ordinal) &&
            expected.Quantity == actual.Quantity &&
            expected.Durability == actual.Durability &&
            expected.Revision == actual.Revision &&
            string.Equals(expected.LoadedAmmoDefinitionId ?? string.Empty, actual.LoadedAmmoDefinitionId ?? string.Empty, StringComparison.Ordinal) &&
            expected.LoadedRounds == actual.LoadedRounds &&
            expected.MagazineRevision == actual.MagazineRevision;

        private static bool TryGetPersistedEquipmentItem(
            PlayerSystemsPersistenceRecord persisted,
            ItemInstanceId itemInstanceId,
            out PersistedEquipmentItem item)
        {
            PersistedEquipmentItem[] items = persisted?.EquipmentItems ?? Array.Empty<PersistedEquipmentItem>();
            for (int i = 0; i < items.Length; ++i)
            {
                if (items[i] != null && items[i].ItemInstanceId == itemInstanceId)
                {
                    item = items[i];
                    return true;
                }
            }
            item = null;
            return false;
        }

        private static InventoryState BuildInventory(PlayerSystemsPersistenceRecord record)
        {
            var slots = new ItemInstanceState[record.InventoryCapacity];
            PersistedInventoryItem[] items = record.InventoryItems ?? Array.Empty<PersistedInventoryItem>();
            for (int i = 0; i < items.Length; ++i)
            {
                PersistedInventoryItem item = items[i];
                if (item == null || item.SlotIndex < 0 || item.SlotIndex >= slots.Length || slots[item.SlotIndex] != null)
                    throw new InvalidOperationException("persisted inventory contains an invalid slot");
                slots[item.SlotIndex] = new ItemInstanceState(item.ItemInstanceId, item.DefinitionId, item.Quantity,
                    item.Durability, item.Revision, item.LoadedAmmoDefinitionId, item.LoadedRounds, item.MagazineRevision);
            }
            return new InventoryState(record.InventoryCapacity, record.InventoryRevision, slots);
        }

        private static EquipmentState BuildEquipment(PlayerSystemsPersistenceRecord record)
        {
            PersistedEquipmentItem[] persistedItems = record.EquipmentItems ?? Array.Empty<PersistedEquipmentItem>();
            var items = new EquippedItemState[persistedItems.Length];
            for (int i = 0; i < persistedItems.Length; ++i)
            {
                PersistedEquipmentItem item = persistedItems[i] ?? throw new InvalidOperationException("persisted equipment contains an invalid item");
                items[i] = new EquippedItemState(item.SlotId,
                    new ItemInstanceState(item.ItemInstanceId, item.DefinitionId, item.Quantity, item.Durability,
                        item.Revision, item.LoadedAmmoDefinitionId, item.LoadedRounds, item.MagazineRevision));
            }
            return new EquipmentState(record.EquipmentRevision, items);
        }

        private static bool IsInventoryFullError(string error) =>
            !string.IsNullOrWhiteSpace(error) && error.IndexOf("inventory", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (error.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0);

        public Task<PlayerItemOperationResult> MoveInventoryAsync(PlayerRuntime runtime, int fromIndex, int toIndex, CancellationToken cancellationToken) =>
            RunSerializedAsync(runtime, cancellationToken, token => MoveInventoryCoreAsync(runtime, fromIndex, toIndex, token));

        private async Task<PlayerItemOperationResult> MoveInventoryCoreAsync(PlayerRuntime runtime, int fromIndex, int toIndex, CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null) return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable");
            if (fromIndex < 0 || fromIndex >= current.Inventory.Capacity || toIndex < 0 || toIndex >= current.Inventory.Capacity || fromIndex == toIndex)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.InvalidSlot, "inventory slot is invalid", BuildView(current));

            ItemInstanceState[] slots = current.Inventory.CopySlots();
            ItemInstanceState from = slots[fromIndex];
            if (from == null) return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ItemUnavailable, "source slot is empty", BuildView(current));
            ItemInstanceState to = slots[toIndex];

            if (to != null && string.Equals(from.DefinitionId, to.DefinitionId, StringComparison.Ordinal) &&
                from.Durability == to.Durability && _content.TryGetItem(from.DefinitionId, out ItemDefinition definition) && definition.maxStack > 1)
            {
                int room = Math.Max(0, definition.maxStack - to.Quantity);
                int moving = Math.Min(room, from.Quantity);
                if (moving > 0)
                {
                    slots[toIndex] = new ItemInstanceState(to.ItemInstanceId, to.DefinitionId, to.Quantity + moving, to.Durability, checked(to.Revision + 1));
                    int remaining = from.Quantity - moving;
                    slots[fromIndex] = remaining == 0 ? null : new ItemInstanceState(from.ItemInstanceId, from.DefinitionId, remaining, from.Durability, checked(from.Revision + 1));
                }
                else
                {
                    slots[fromIndex] = to; slots[toIndex] = from;
                }
            }
            else
            {
                slots[fromIndex] = to; slots[toIndex] = from;
            }

            var nextInventory = current.Inventory.WithSlots(slots, checked(current.Inventory.Revision + 1));
            return await CommitAsync(runtime, current, nextInventory, current.Equipment, cancellationToken).ConfigureAwait(false);
        }

        public Task<PlayerItemOperationResult> EquipAsync(PlayerRuntime runtime, int inventoryIndex, string equipmentSlotId, CancellationToken cancellationToken) =>
            RunSerializedAsync(runtime, cancellationToken, token => EquipCoreAsync(runtime, inventoryIndex, equipmentSlotId, token));

        private async Task<PlayerItemOperationResult> EquipCoreAsync(PlayerRuntime runtime, int inventoryIndex, string equipmentSlotId, CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null) return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable");
            if (inventoryIndex < 0 || inventoryIndex >= current.Inventory.Capacity)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.InvalidSlot, "inventory slot is invalid", BuildView(current));
            if (!_content.TryGetEquipmentSlot(equipmentSlotId, out _))
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.EquipmentSlotInvalid, "equipment slot is invalid", BuildView(current));

            ItemInstanceState item = current.Inventory.Get(inventoryIndex);
            if (item == null) return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ItemUnavailable, "inventory slot is empty", BuildView(current));
            if (!_content.TryGetItem(item.DefinitionId, out ItemDefinition definition) || !AllowsSlot(definition, equipmentSlotId))
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.EquipmentNotAllowed, "item cannot be equipped in that slot", BuildView(current));

            ItemInstanceState[] inventory = current.Inventory.CopySlots();
            ItemInstanceState displaced = current.Equipment.Get(equipmentSlotId);
            inventory[inventoryIndex] = displaced?.Copy();

            var equipped = new List<EquippedItemState>(current.Equipment.Snapshot());
            for (int i = equipped.Count - 1; i >= 0; --i)
                if (string.Equals(equipped[i].SlotId, equipmentSlotId, StringComparison.Ordinal)) equipped.RemoveAt(i);
            equipped.Add(new EquippedItemState(equipmentSlotId, item.Copy()));

            var nextInventory = new InventoryState(current.Inventory.Capacity, checked(current.Inventory.Revision + 1), inventory);
            var nextEquipment = new EquipmentState(checked(current.Equipment.Revision + 1), equipped);
            return await CommitAsync(runtime, current, nextInventory, nextEquipment, cancellationToken).ConfigureAwait(false);
        }

        public Task<PlayerItemOperationResult> UnequipAsync(PlayerRuntime runtime, string equipmentSlotId, int preferredInventoryIndex, CancellationToken cancellationToken) =>
            RunSerializedAsync(runtime, cancellationToken, token => UnequipCoreAsync(runtime, equipmentSlotId, preferredInventoryIndex, token));

        private async Task<PlayerItemOperationResult> UnequipCoreAsync(PlayerRuntime runtime, string equipmentSlotId, int preferredInventoryIndex, CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null) return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable");
            ItemInstanceState item = current.Equipment.Get(equipmentSlotId);
            if (item == null) return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ItemUnavailable, "equipment slot is empty", BuildView(current));

            int target = preferredInventoryIndex;
            if (target < 0) target = current.Inventory.FindFirstEmptySlot();
            if (target < 0 || target >= current.Inventory.Capacity)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.InventoryFull, "inventory has no empty slot", BuildView(current));
            if (current.Inventory.Get(target) != null)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.InvalidSlot, "target inventory slot is not empty", BuildView(current));

            ItemInstanceState[] inventory = current.Inventory.CopySlots();
            inventory[target] = item.Copy();
            var equipped = new List<EquippedItemState>(current.Equipment.Snapshot());
            for (int i = equipped.Count - 1; i >= 0; --i)
                if (string.Equals(equipped[i].SlotId, equipmentSlotId, StringComparison.Ordinal)) equipped.RemoveAt(i);

            var nextInventory = new InventoryState(current.Inventory.Capacity, checked(current.Inventory.Revision + 1), inventory);
            var nextEquipment = new EquipmentState(checked(current.Equipment.Revision + 1), equipped);
            return await CommitAsync(runtime, current, nextInventory, nextEquipment, cancellationToken).ConfigureAwait(false);
        }

        public Task<PlayerItemOperationResult> UseAsync(
            PlayerRuntime runtime,
            int inventoryIndex,
            double serverTime,
            CancellationToken cancellationToken) =>
            RunSerializedAsync(runtime, cancellationToken, token => UseCoreAsync(runtime, inventoryIndex, serverTime, token));

        private async Task<PlayerItemOperationResult> UseCoreAsync(
            PlayerRuntime runtime,
            int inventoryIndex,
            double serverTime,
            CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable");
            if (inventoryIndex < 0 || inventoryIndex >= current.Inventory.Capacity)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.InvalidSlot, "inventory slot is invalid", BuildView(current));

            ItemInstanceState item = current.Inventory.Get(inventoryIndex);
            if (item == null)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ItemUnavailable, "inventory slot is empty", BuildView(current));
            if (!_content.TryGetItem(item.DefinitionId, out ItemDefinition definition) ||
                definition.consumeQuantity < 1 ||
                (definition.useEffects ?? Array.Empty<ItemUseEffectDefinition>()).Length == 0)
            {
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ItemNotUsable, "item is not usable", BuildView(current));
            }
            if (item.Quantity < definition.consumeQuantity)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ItemUnavailable, "item quantity is insufficient", BuildView(current));
            if (_lifecycleRepository == null || _resources == null || _statusEffects == null)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.ContentUnavailable, "item lifecycle services are unavailable", BuildView(current));
            if (!runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out var health) ||
                !health.Enabled || health.Current <= health.Minimum)
            {
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.EffectUnavailable, "dead characters cannot use items", BuildView(current));
            }
            if (!HasApplicableUseEffect(runtime, definition, serverTime))
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.EffectUnavailable, "item has no applicable effect", BuildView(current));

            ItemInstanceState[] slots = current.Inventory.CopySlots();
            int remaining = item.Quantity - definition.consumeQuantity;
            slots[inventoryIndex] = remaining == 0
                ? null
                : new ItemInstanceState(
                    item.ItemInstanceId,
                    item.DefinitionId,
                    remaining,
                    item.Durability,
                    checked(item.Revision + 1));
            var nextInventory = new InventoryState(
                current.Inventory.Capacity,
                checked(current.Inventory.Revision + 1),
                slots);

            PlayerItemConsumePersistenceResult persisted;
            try
            {
                persisted = await _lifecycleRepository.TryConsumeAsync(
                    new PlayerItemConsumePersistenceRequest(
                        runtime.AccountId,
                        runtime.CharacterId,
                        current.Inventory.Revision,
                        current.Equipment.Revision,
                        item.ItemInstanceId,
                        definition.consumeQuantity,
                        nextInventory,
                        current.Equipment),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                PersistenceReconciliation reconciliation = await ReconcilePersistenceAsync(
                    runtime, current, "item consume", cancellationToken).ConfigureAwait(false);
                if (reconciliation.Loaded && PersistedStateMatches(reconciliation.State, nextInventory, current.Equipment))
                {
                    ApplyUseEffects(runtime, definition, serverTime);
                    return reconciliation.Installed;
                }
                return ReconciledUncertainFailure(reconciliation, current, "item consume");
            }

            if (!persisted.Accepted)
            {
                if (persisted.Stale)
                    return await ReconcileStaleAsync(runtime, current, "item consume", persisted.Error, cancellationToken).ConfigureAwait(false);
                return PlayerItemOperationResult.Failed(
                    PlayerItemOperationStatus.PersistenceRejected,
                    string.IsNullOrWhiteSpace(persisted.Error) ? "item consume transaction rejected" : persisted.Error,
                    BuildView(current));
            }

            StatsState stats = CalculateStats(current.Equipment);
            if (!runtime.TryCommitPlayerItemSystems(
                    current.Inventory.Revision,
                    current.Equipment.Revision,
                    nextInventory,
                    current.Equipment,
                    stats))
            {
                return PlayerItemOperationResult.Failed(
                    PlayerItemOperationStatus.StaleState,
                    "live item state changed while consume transaction was committing",
                    GetSnapshot(runtime));
            }

            ApplyUseEffects(runtime, definition, serverTime);
            PlayerItemsSnapshot snapshot = GetSnapshot(runtime);
            Changed?.Invoke(runtime, BuildView(current), snapshot);
            return PlayerItemOperationResult.Succeeded(snapshot);
        }

        private bool HasApplicableUseEffect(PlayerRuntime runtime, ItemDefinition definition, double serverTime)
        {
            if (runtime == null || double.IsNaN(serverTime) || double.IsInfinity(serverTime) || serverTime < 0d)
                return false;

            ItemUseEffectDefinition[] effects = definition?.useEffects ?? Array.Empty<ItemUseEffectDefinition>();
            for (int i = 0; i < effects.Length; ++i)
            {
                ItemUseEffectDefinition effect = effects[i];
                if (effect == null) continue;
                if (effect.kind == ItemUseEffectKind.RestoreResource && effect.amount > 0 &&
                    runtime.TryGetCharacterResource(effect.resourceId, out _, out var resource) &&
                    resource.Enabled && resource.Current < resource.Maximum)
                {
                    return true;
                }
                if (effect.kind == ItemUseEffectKind.ApplyStatusEffect &&
                    _content.TryGetStatusEffect(effect.statusEffectId, out StatusEffectDefinition status) &&
                    effect.stacks > 0 &&
                    runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out var health) &&
                    health.Enabled && health.Current > health.Minimum)
                {
                    var active = runtime.CaptureStatusEffects();
                    if (status.stackingPolicy != StatusEffectStackingPolicy.IgnoreWhileActive ||
                        !active.TryGet(status.definitionId, out _))
                        return true;
                }
            }
            return false;
        }

        private void ApplyUseEffects(PlayerRuntime runtime, ItemDefinition definition, double serverTime)
        {
            ItemUseEffectDefinition[] effects = definition?.useEffects ?? Array.Empty<ItemUseEffectDefinition>();
            for (int i = 0; i < effects.Length; ++i)
            {
                ItemUseEffectDefinition effect = effects[i];
                if (effect == null) continue;
                switch (effect.kind)
                {
                    case ItemUseEffectKind.RestoreResource:
                        if (effect.amount > 0)
                            _resources.Add(runtime, effect.resourceId, effect.amount, CharacterResourceChangeReason.Item);
                        break;
                    case ItemUseEffectKind.ApplyStatusEffect:
                        _statusEffects.Apply(
                            runtime,
                            effect.statusEffectId,
                            runtime.CharacterId.Value,
                            Math.Max(1, effect.stacks),
                            serverTime,
                            StatusEffectChangeReason.Item);
                        break;
                }
            }
        }

        public Task<PlayerItemWorldTransferResult> DropAsync(
            PlayerRuntime runtime,
            int inventoryIndex,
            int quantity,
            string mapId,
            string instanceId,
            WorldPosition position,
            CancellationToken cancellationToken) =>
            RunSerializedAsync(runtime, cancellationToken, token => DropCoreAsync(runtime, inventoryIndex, quantity, mapId, instanceId, position, token),
                new PlayerItemWorldTransferResult(PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player runtime is unavailable"), 0, null, mapId, instanceId));

        private async Task<PlayerItemWorldTransferResult> DropCoreAsync(
            PlayerRuntime runtime,
            int inventoryIndex,
            int quantity,
            string mapId,
            string instanceId,
            WorldPosition position,
            CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null)
                return FailedWorldTransfer(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable", null, mapId, instanceId);
            if (inventoryIndex < 0 || inventoryIndex >= current.Inventory.Capacity)
                return FailedWorldTransfer(PlayerItemOperationStatus.InvalidSlot, "inventory slot is invalid", BuildView(current), mapId, instanceId);
            ItemInstanceState item = current.Inventory.Get(inventoryIndex);
            if (item == null)
                return FailedWorldTransfer(PlayerItemOperationStatus.ItemUnavailable, "inventory slot is empty", BuildView(current), mapId, instanceId);
            if (quantity < 1 || quantity > item.Quantity)
                return FailedWorldTransfer(PlayerItemOperationStatus.ItemUnavailable, "drop quantity is invalid", BuildView(current), mapId, instanceId);
            if (_lifecycleRepository == null || string.IsNullOrWhiteSpace(mapId))
                return FailedWorldTransfer(PlayerItemOperationStatus.ContentUnavailable, "item lifecycle services are unavailable", BuildView(current), mapId, instanceId);

            bool splitStack = quantity < item.Quantity;
            ItemInstanceId splitWorldItemInstanceId = splitStack ? TransientWorldItemIds.Allocate() : default;
            ItemInstanceId worldItemInstanceId = splitStack ? splitWorldItemInstanceId : item.ItemInstanceId;
            long worldItemRevision = splitStack ? 0 : checked(item.Revision + 1);
            var expectedWorldItem = new WorldItemState(
                worldItemInstanceId,
                item.DefinitionId,
                quantity,
                item.Durability,
                worldItemRevision,
                mapId,
                instanceId,
                position,
                item.LoadedAmmoDefinitionId,
                item.LoadedRounds,
                item.MagazineRevision);

            ItemInstanceState[] slots = current.Inventory.CopySlots();
            int remaining = item.Quantity - quantity;
            slots[inventoryIndex] = remaining == 0
                ? null
                : new ItemInstanceState(
                    item.ItemInstanceId, item.DefinitionId, remaining, item.Durability, checked(item.Revision + 1),
                    item.LoadedAmmoDefinitionId, item.LoadedRounds, item.MagazineRevision);
            var nextInventory = new InventoryState(current.Inventory.Capacity, checked(current.Inventory.Revision + 1), slots);

            PlayerItemDropPersistenceResult persisted;
            try
            {
                persisted = await _lifecycleRepository.TryDropAsync(
                    new PlayerItemDropPersistenceRequest(
                        runtime.AccountId,
                        runtime.CharacterId,
                        current.Inventory.Revision,
                        current.Equipment.Revision,
                        item.ItemInstanceId,
                        splitWorldItemInstanceId,
                        quantity,
                        item.LoadedAmmoDefinitionId,
                        item.LoadedRounds,
                        item.MagazineRevision,
                        mapId,
                        instanceId,
                        position,
                        nextInventory,
                        current.Equipment),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                PersistenceReconciliation reconciliation = await ReconcilePersistenceAsync(
                    runtime, current, "item drop", cancellationToken).ConfigureAwait(false);
                if (reconciliation.Loaded && PersistedStateMatches(reconciliation.State, nextInventory, current.Equipment))
                {
                    return new PlayerItemWorldTransferResult(
                        reconciliation.Installed,
                        0,
                        expectedWorldItem,
                        mapId,
                        instanceId);
                }

                PlayerItemOperationResult failure = ReconciledUncertainFailure(reconciliation, current, "item drop");
                return new PlayerItemWorldTransferResult(failure, 0, null, mapId, instanceId);
            }

            if (!persisted.Accepted || persisted.WorldItem == null)
            {
                if (persisted.Stale)
                {
                    PlayerItemOperationResult stale = await ReconcileStaleAsync(
                        runtime, current, "item drop", persisted.Error, cancellationToken).ConfigureAwait(false);
                    return new PlayerItemWorldTransferResult(stale, 0, null, mapId, instanceId);
                }
                return FailedWorldTransfer(
                    PlayerItemOperationStatus.PersistenceRejected,
                    string.IsNullOrWhiteSpace(persisted.Error) ? "item drop transaction rejected" : persisted.Error,
                    BuildView(current),
                    mapId,
                    instanceId);
            }

            StatsState stats = CalculateStats(current.Equipment);
            if (!runtime.TryCommitPlayerItemSystems(current.Inventory.Revision, current.Equipment.Revision, nextInventory, current.Equipment, stats))
                return FailedWorldTransfer(PlayerItemOperationStatus.StaleState, "live item state changed while drop transaction was committing", GetSnapshot(runtime), mapId, instanceId);

            PlayerItemsSnapshot snapshot = GetSnapshot(runtime);
            Changed?.Invoke(runtime, BuildView(current), snapshot);
            return new PlayerItemWorldTransferResult(PlayerItemOperationResult.Succeeded(snapshot), persisted.WorldRevision, persisted.WorldItem, persisted.WorldItem.MapId, persisted.WorldItem.InstanceId);
        }

        public Task<PlayerItemWorldTransferResult> PickupAsync(
            PlayerRuntime runtime,
            WorldItemState worldItem,
            CancellationToken cancellationToken) =>
            RunSerializedAsync(runtime, cancellationToken, token => PickupCoreAsync(runtime, worldItem, token),
                new PlayerItemWorldTransferResult(PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player runtime is unavailable"), 0, null, worldItem?.MapId, worldItem?.InstanceId));

        private async Task<PlayerItemWorldTransferResult> PickupCoreAsync(PlayerRuntime runtime, WorldItemState worldItem, CancellationToken cancellationToken)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null)
                return FailedWorldTransfer(PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable", null, worldItem?.MapId, worldItem?.InstanceId);
            if (worldItem == null || !_content.TryGetItem(worldItem.DefinitionId, out ItemDefinition definition))
                return FailedWorldTransfer(PlayerItemOperationStatus.ItemUnavailable, "world item is unavailable", BuildView(current), worldItem?.MapId, worldItem?.InstanceId);
            if (_lifecycleRepository == null)
                return FailedWorldTransfer(PlayerItemOperationStatus.ContentUnavailable, "item lifecycle services are unavailable", BuildView(current), worldItem.MapId, worldItem.InstanceId);

            ItemInstanceState[] slots = current.Inventory.CopySlots();
            int remaining = worldItem.Quantity;
            int maxStack = Math.Max(1, definition.maxStack);
            for (int i = 0; i < slots.Length && remaining > 0; ++i)
            {
                ItemInstanceState target = slots[i];
                if (target == null || !string.Equals(target.DefinitionId, worldItem.DefinitionId, StringComparison.Ordinal) || target.Durability != worldItem.Durability)
                    continue;
                int room = Math.Max(0, maxStack - target.Quantity);
                if (room == 0) continue;
                int add = Math.Min(room, remaining);
                slots[i] = new ItemInstanceState(
                    target.ItemInstanceId, target.DefinitionId, target.Quantity + add, target.Durability, checked(target.Revision + 1),
                    target.LoadedAmmoDefinitionId, target.LoadedRounds, target.MagazineRevision);
                remaining -= add;
            }

            if (remaining > 0)
            {
                int empty = -1;
                for (int i = 0; i < slots.Length; ++i)
                    if (slots[i] == null) { empty = i; break; }
                if (empty < 0)
                    return FailedWorldTransfer(PlayerItemOperationStatus.InventoryFull, "inventory cannot hold the complete world item", BuildView(current), worldItem.MapId, worldItem.InstanceId);
                if (remaining > maxStack)
                    return FailedWorldTransfer(PlayerItemOperationStatus.InventoryFull, "world stack exceeds one available inventory slot", BuildView(current), worldItem.MapId, worldItem.InstanceId);
                slots[empty] = new ItemInstanceState(
                    worldItem.ItemInstanceId, worldItem.DefinitionId, remaining, worldItem.Durability, checked(worldItem.Revision + 1),
                    worldItem.LoadedAmmoDefinitionId, worldItem.LoadedRounds, worldItem.MagazineRevision);
            }

            var nextInventory = new InventoryState(current.Inventory.Capacity, checked(current.Inventory.Revision + 1), slots);
            PlayerItemPickupPersistenceResult persisted;
            try
            {
                persisted = await _lifecycleRepository.TryPickupAsync(
                    new PlayerItemPickupPersistenceRequest(
                        runtime.AccountId,
                        runtime.CharacterId,
                        current.Inventory.Revision,
                        current.Equipment.Revision,
                        worldItem.ItemInstanceId,
                        worldItem.Revision,
                        nextInventory,
                        current.Equipment),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                PersistenceReconciliation reconciliation = await ReconcilePersistenceAsync(
                    runtime, current, "item pickup", cancellationToken).ConfigureAwait(false);
                if (reconciliation.Loaded && PersistedStateMatches(
                        reconciliation.State, nextInventory, current.Equipment, worldItem.ItemInstanceId))
                {
                    return new PlayerItemWorldTransferResult(
                        reconciliation.Installed,
                        0,
                        null,
                        worldItem.MapId,
                        worldItem.InstanceId);
                }

                PlayerItemOperationResult failure = ReconciledUncertainFailure(reconciliation, current, "item pickup");
                return new PlayerItemWorldTransferResult(failure, 0, null, worldItem.MapId, worldItem.InstanceId);
            }

            if (!persisted.Accepted || persisted.State == null)
            {
                if (persisted.Stale)
                {
                    PlayerItemOperationResult stale = await ReconcileStaleAsync(
                        runtime, current, "item pickup", persisted.Error, cancellationToken).ConfigureAwait(false);
                    return new PlayerItemWorldTransferResult(stale, 0, null, worldItem.MapId, worldItem.InstanceId);
                }
                return FailedWorldTransfer(
                    PlayerItemOperationStatus.PersistenceRejected,
                    string.IsNullOrWhiteSpace(persisted.Error) ? "item pickup transaction rejected" : persisted.Error,
                    BuildView(current),
                    worldItem.MapId,
                    worldItem.InstanceId);
            }

            PlayerItemOperationResult installed = InstallPersistedState(runtime, current, persisted.State, "item pickup");
            return new PlayerItemWorldTransferResult(
                installed,
                installed.Success ? persisted.WorldRevision : 0,
                null,
                persisted.MapId,
                persisted.InstanceId);
        }

        private static PlayerItemWorldTransferResult FailedWorldTransfer(PlayerItemOperationStatus status, string error, PlayerItemsSnapshot snapshot, string mapId, string instanceId) =>
            new PlayerItemWorldTransferResult(PlayerItemOperationResult.Failed(status, error, snapshot), 0, null, mapId, instanceId);

        public void RecalculateStatsForContentChange(PlayerRuntime runtime)
        {
            PlayerItemSystemsRuntimeSnapshot current = runtime?.CapturePlayerItemSystems();
            if (current == null) return;
            runtime.TryReplaceCalculatedStats(current.Equipment.Revision, CalculateStats(current.Equipment));
        }


        private async Task<PlayerItemOperationResult> RunSerializedAsync(
            PlayerRuntime runtime,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<PlayerItemOperationResult>> operation)
        {
            if (runtime == null)
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player runtime is unavailable");

            SemaphoreSlim gate = _operationGates.GetValue(runtime, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<T> RunSerializedAsync<T>(
            PlayerRuntime runtime,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<T>> operation,
            T unavailableResult)
        {
            if (runtime == null)
                return unavailableResult;

            SemaphoreSlim gate = _operationGates.GetValue(runtime, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<PlayerItemOperationResult> CommitAsync(PlayerRuntime runtime, PlayerItemSystemsRuntimeSnapshot current, InventoryState inventory, EquipmentState equipment, CancellationToken cancellationToken)
        {
            PlayerSystemsCommitResult persisted;
            try
            {
                persisted = await _repository.TryCommitAsync(new PlayerSystemsCommitRequest(
                    runtime.AccountId, runtime.CharacterId, current.Inventory.Revision, current.Equipment.Revision, inventory, equipment), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                PersistenceReconciliation reconciliation = await ReconcilePersistenceAsync(
                    runtime, current, "item transaction", cancellationToken).ConfigureAwait(false);
                return reconciliation.Loaded && PersistedStateMatches(reconciliation.State, inventory, equipment)
                    ? reconciliation.Installed
                    : ReconciledUncertainFailure(reconciliation, current, "item transaction");
            }

            if (!persisted.Accepted)
            {
                if (persisted.Stale)
                    return await ReconcileStaleAsync(runtime, current, "item transaction", persisted.Error, cancellationToken).ConfigureAwait(false);
                return PlayerItemOperationResult.Failed(
                    PlayerItemOperationStatus.PersistenceRejected,
                    string.IsNullOrWhiteSpace(persisted.Error) ? "item transaction rejected" : persisted.Error,
                    BuildView(current));
            }

            StatsState stats = CalculateStats(equipment);
            if (!runtime.TryCommitPlayerItemSystems(current.Inventory.Revision, current.Equipment.Revision, inventory, equipment, stats))
                return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.StaleState, "live item state changed while transaction was committing", GetSnapshot(runtime));

            PlayerItemsSnapshot snapshot = GetSnapshot(runtime);
            Changed?.Invoke(runtime, BuildView(current), snapshot);
            return PlayerItemOperationResult.Succeeded(snapshot);
        }

        public StatsState CalculateStats(EquipmentState equipment) => EquipmentStatCalculator.Calculate(_content, equipment);

        private PlayerItemsSnapshot BuildView(PlayerItemSystemsRuntimeSnapshot state)
        {
            ItemInstanceState[] slots = state.Inventory.CopySlots();
            var inventory = new List<PlayerItemView>();
            float weight = 0f;
            for (int i = 0; i < slots.Length; ++i)
            {
                ItemInstanceState item = slots[i];
                if (item == null) continue;
                PlayerItemView view = BuildItemView(item, i, string.Empty);
                inventory.Add(view);
                weight += view.unitWeight * view.quantity;
            }

            EquipmentSlotDefinition[] slotDefinitions = _content.GetEquipmentSlotsOrdered();
            var equipment = new EquipmentSlotView[slotDefinitions.Length];
            for (int i = 0; i < slotDefinitions.Length; ++i)
            {
                EquipmentSlotDefinition slot = slotDefinitions[i];
                ItemInstanceState item = state.Equipment.Get(slot.slotId);
                equipment[i] = new EquipmentSlotView
                {
                    slotDataId = slot.dataId, slotId = slot.slotId, displayName = slot.displayName, order = slot.order,
                    item = item == null ? null : BuildItemView(item, -1, slot.slotId),
                };
            }

            return new PlayerItemsSnapshot
            {
                contentRevision = _content.Revision,
                inventoryRevision = state.Inventory.Revision,
                equipmentRevision = state.Equipment.Revision,
                inventoryCapacity = state.Inventory.Capacity,
                inventoryWeight = weight,
                armor = state.Stats.Get("Armor"),
                attackPower = state.Stats.Get("AttackPower"),
                inventory = inventory.ToArray(),
                equipment = equipment,
            };
        }

        private PlayerItemView BuildItemView(ItemInstanceState item, int inventorySlot, string equipmentSlot)
        {
            _content.TryGetItem(item.DefinitionId, out ItemDefinition definition);
            return new PlayerItemView
            {
                inventorySlot = inventorySlot, equipmentSlotId = equipmentSlot ?? string.Empty,
                itemDataId = definition?.dataId ?? 0, itemInstanceId = item.ItemInstanceId.Value, definitionId = item.DefinitionId,
                displayName = definition?.displayName ?? item.DefinitionId, quantity = item.Quantity,
                durability = item.Durability, maxDurability = definition?.maxDurability ?? 0,
                unitWeight = definition?.weight ?? 0f,
                canUse = definition != null && definition.consumeQuantity > 0 &&
                         (definition.useEffects ?? Array.Empty<ItemUseEffectDefinition>()).Length > 0,
                consumeQuantity = definition?.consumeQuantity ?? 0,
                allowedEquipmentSlots = definition?.allowedEquipmentSlots ?? Array.Empty<string>(),
            };
        }

        private static bool AllowsSlot(ItemDefinition definition, string slotId)
        {
            string[] allowed = definition?.allowedEquipmentSlots ?? Array.Empty<string>();
            for (int i = 0; i < allowed.Length; ++i)
                if (string.Equals(allowed[i], slotId, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
