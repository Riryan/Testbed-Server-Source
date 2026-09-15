using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Content;
using Game.Server.Application.Items;
using Game.Server.Application.Persistence;
using Game.Server.Application.WorldItems;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Stats;
using Game.Shared.Backend;
using Game.Shared.Content;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.World;

internal static class Program
{
    private enum CommitBehavior
    {
        CommitThenThrow,
        CommitThenTimeout,
        StaleThenReload,
    }

    private sealed class FakeRepository : IPlayerSystemsRepository
    {
        private readonly CommitBehavior _behavior;
        private PlayerSystemsPersistenceRecord _state;

        public int ReconciliationLoads { get; private set; }

        public FakeRepository(PlayerSystemsPersistenceRecord initial, CommitBehavior behavior)
        {
            _state = initial ?? throw new ArgumentNullException(nameof(initial));
            _behavior = behavior;
        }

        public Task<PlayerSystemsPersistenceRecord> LoadAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken) =>
            Task.FromResult(_state);

        public Task<PlayerSystemsPersistenceRecord> LoadForReconciliationAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken)
        {
            ReconciliationLoads++;
            return Task.FromResult(_state);
        }

        public Task<PlayerSystemsCommitResult> TryCommitAsync(PlayerSystemsCommitRequest request, CancellationToken cancellationToken)
        {
            if (_behavior == CommitBehavior.StaleThenReload)
            {
                // Simulate a durable mutation that beat this request to revision 1. The
                // stale response must still cause the live runtime to install this state.
                ItemInstanceState[] authoritativeSlots = request.Inventory.CopySlots();
                ItemInstanceState moved = authoritativeSlots[1];
                authoritativeSlots[1] = null;
                authoritativeSlots[0] = moved;
                var authoritativeInventory = new InventoryState(
                    request.Inventory.Capacity,
                    request.Inventory.Revision,
                    authoritativeSlots);
                _state = ToRecord(authoritativeInventory, request.Equipment);
                return Task.FromResult(new PlayerSystemsCommitResult(
                    false,
                    true,
                    _state.InventoryRevision,
                    _state.EquipmentRevision,
                    "stale player-system revision"));
            }

            // The durable mutation commits first; only its response is lost.
            _state = ToRecord(request.Inventory, request.Equipment);
            if (_behavior == CommitBehavior.CommitThenTimeout)
                throw new OperationCanceledException("simulated HttpClient timeout");
            throw new InvalidOperationException("simulated lost response after commit");
        }
    }

    private static async Task Main()
    {
        await Run("lost response after commit reconciles as success", CommitThenThrowReconcilesAsSuccess);
        await Run("HttpClient-style timeout after commit reconciles as success", TimeoutAfterCommitReconcilesAsSuccess);
        await Run("stale response reloads authoritative state", StaleResponseReloadsAuthoritativeState);
        RunSync("transient world item ids are isolated from durable ids", TransientWorldIdsUseReservedRange);
        Console.WriteLine("All Game.Server.Application regression tests passed.");
    }

    private static async Task CommitThenThrowReconcilesAsSuccess()
    {
        (PlayerItemService service, PlayerRuntime runtime, FakeRepository repository) =
            CreateHarness(CommitBehavior.CommitThenThrow);

        PlayerItemOperationResult result = await service.MoveInventoryAsync(runtime, 0, 1, CancellationToken.None);
        Require(result.Success, "move should be reported successful after authoritative reconciliation");
        Require(repository.ReconciliationLoads == 1, "ambiguous outcome should perform one reconciliation load");

        PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
        Require(state.Inventory.Revision == 1, "reconciled inventory revision should be 1");
        Require(state.Inventory.Get(0) == null, "source slot should be empty after reconciled commit");
        Require(state.Inventory.Get(1)?.ItemInstanceId.Value == 10, "target slot should contain the committed item");
    }

    private static async Task TimeoutAfterCommitReconcilesAsSuccess()
    {
        (PlayerItemService service, PlayerRuntime runtime, FakeRepository repository) =
            CreateHarness(CommitBehavior.CommitThenTimeout);

        PlayerItemOperationResult result = await service.MoveInventoryAsync(runtime, 0, 1, CancellationToken.None);
        Require(result.Success, "an internal timeout must be treated as an ambiguous outcome, not caller cancellation");
        Require(repository.ReconciliationLoads == 1, "timeout should perform one reconciliation load");
        Require(runtime.CapturePlayerItemSystems().Inventory.Get(1)?.ItemInstanceId.Value == 10,
            "authoritative post-timeout state should be installed");
    }

    private static async Task StaleResponseReloadsAuthoritativeState()
    {
        (PlayerItemService service, PlayerRuntime runtime, FakeRepository repository) =
            CreateHarness(CommitBehavior.StaleThenReload);

        PlayerItemOperationResult result = await service.MoveInventoryAsync(runtime, 0, 1, CancellationToken.None);
        Require(!result.Success, "stale request should not be reclassified as successful");
        Require(result.Status == PlayerItemOperationStatus.StaleState, "stale request should preserve stale status");
        Require(repository.ReconciliationLoads == 1, "stale response should perform one reconciliation load");

        PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
        Require(state.Inventory.Revision == 1, "authoritative stale revision should be installed");
        Require(state.Inventory.Get(0)?.ItemInstanceId.Value == 10, "authoritative slot layout should replace stale live state");
        Require(state.Inventory.Get(1) == null, "rejected local move must not survive reconciliation");
    }

    private static void TransientWorldIdsUseReservedRange()
    {
        var seen = new HashSet<long>();
        for (int i = 0; i < 1024; ++i)
        {
            long value = TransientWorldItemIds.Allocate().Value;
            Require(value >= BackendServiceContracts.TransientWorldItemIdFloor,
                "transient id must be inside the reserved high range");
            Require(seen.Add(value), "transient ids must be unique");
        }
    }

    private static (PlayerItemService Service, PlayerRuntime Runtime, FakeRepository Repository) CreateHarness(CommitBehavior behavior)
    {
        var content = new GameplayContentCatalog(new GameplayContentSnapshot
        {
            revision = 1,
            baseInventoryCapacity = 2,
        });

        var item = new ItemInstanceState(new ItemInstanceId(10), "test.item", 1, 0, 0);
        var inventory = new InventoryState(2, 0, new[] { item, null });
        var equipment = new EquipmentState(0);
        var repository = new FakeRepository(ToRecord(inventory, equipment), behavior);
        var service = new PlayerItemService(content, repository);
        var runtime = new PlayerRuntime(
            new AccountId(1),
            new CharacterId(1),
            PlayerSessionId.New(),
            new CharacterState("Regression"),
            new CharacterLocationState("test", string.Empty, new WorldPosition(0f, 0f, 0f), 0f),
            0);
        runtime.InitializePlayerItemSystems(inventory, equipment, StatsState.DefaultCharacter());
        return (service, runtime, repository);
    }

    private static PlayerSystemsPersistenceRecord ToRecord(InventoryState inventory, EquipmentState equipment)
    {
        ItemInstanceState[] slots = inventory.CopySlots();
        var persistedInventory = new List<PersistedInventoryItem>();
        for (int i = 0; i < slots.Length; ++i)
        {
            ItemInstanceState item = slots[i];
            if (item == null) continue;
            persistedInventory.Add(new PersistedInventoryItem(
                i,
                item.ItemInstanceId,
                item.DefinitionId,
                item.Quantity,
                item.Durability,
                item.Revision,
                item.LoadedAmmoDefinitionId,
                item.LoadedRounds,
                item.MagazineRevision));
        }

        EquippedItemState[] equipped = equipment.Snapshot();
        var persistedEquipment = new PersistedEquipmentItem[equipped.Length];
        for (int i = 0; i < equipped.Length; ++i)
        {
            EquippedItemState entry = equipped[i];
            persistedEquipment[i] = new PersistedEquipmentItem(
                entry.SlotId,
                entry.Item.ItemInstanceId,
                entry.Item.DefinitionId,
                entry.Item.Quantity,
                entry.Item.Durability,
                entry.Item.Revision,
                entry.Item.LoadedAmmoDefinitionId,
                entry.Item.LoadedRounds,
                entry.Item.MagazineRevision);
        }

        return new PlayerSystemsPersistenceRecord(
            inventory.Capacity,
            inventory.Revision,
            equipment.Revision,
            persistedInventory.ToArray(),
            persistedEquipment);
    }

    private static async Task Run(string name, Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {name}: {ex.Message}");
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static void RunSync(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {name}: {ex.Message}");
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
