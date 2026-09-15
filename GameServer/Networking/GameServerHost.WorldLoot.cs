using System;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Runtime;
using Game.Server.Application.Interactions;
using Game.Server.Application.Rewards;
using Game.Server.Domain.Players;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.Protocol;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private void HandleWorldLootOpen(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new WorldLootOpenRequestMessage();
        request.Deserialize(reader);

        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, WorldLootResponseMessage.Failed(
                request.stableId,
                InteractionResultCode.InvalidState,
                "character is not in world"));
            return;
        }

        if (!_runtime.WorldInteractables.TryOpenLoot(runtime, request.stableId, out WorldInteractableRuntime target, out string reason))
        {
            SendResponse(session, requestId, WorldLootResponseMessage.Failed(request.stableId, InteractionResultCode.Rejected, reason));
            return;
        }

        if (!TryValidateWorldLootContent(target, out reason))
        {
            Console.Error.WriteLine($"[WorldLoot] Open rejected for peer {session?.Peer?.Id}, object {request.stableId}: {reason}");
            SendResponse(session, requestId, WorldLootResponseMessage.Failed(request.stableId, InteractionResultCode.InvalidState, reason));
            return;
        }

        SendResponse(session, requestId, BuildWorldLootResponse(target));
    }

    private void HandleWorldLootTake(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new WorldLootTakeRequestMessage();
        request.Deserialize(reader);

        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, WorldLootTakeResponseMessage.Failed(request.stableId, InteractionResultCode.InvalidState, "character is not in world"));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, WorldLootTakeResponseMessage.Failed(request.stableId, InteractionResultCode.Rejected, BackendPersistenceUnavailableMessage));
            return;
        }
        if (!_runtime.WorldInteractables.TryBeginLootTake(
                runtime,
                request.stableId,
                request.lootRevision,
                request.entryIndex,
                request.quantity,
                out WorldInteractableRuntime target,
                out LootTableEntryDefinition lootDefinition,
                out string reason))
        {
            SendResponse(session, requestId, WorldLootTakeResponseMessage.Failed(request.stableId, InteractionResultCode.Rejected, reason));
            return;
        }

        var reward = new RewardBundleDefinition
        {
            items = new[]
            {
                new RewardItemDefinition
                {
                    itemDataId = lootDefinition.itemDataId,
                    itemDefinitionId = lootDefinition.itemDefinitionId,
                    quantity = request.quantity,
                },
            },
        };
        RunWorldLootTakeAsync(session, requestId, request, target, lootDefinition, _runtime.Rewards.GrantAsync(runtime, reward, CancellationToken.None)).Forget();
    }

    private void HandleWorldLootTakeAll(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new WorldLootTakeAllRequestMessage();
        request.Deserialize(reader);

        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, WorldLootResponseMessage.Failed(request.stableId, InteractionResultCode.InvalidState, "character is not in world"));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, WorldLootResponseMessage.Failed(request.stableId, InteractionResultCode.Rejected, BackendPersistenceUnavailableMessage));
            return;
        }
        if (!_runtime.WorldInteractables.TryBeginLootTakeAll(
                runtime,
                request.stableId,
                request.lootRevision,
                out WorldInteractableRuntime target,
                out WorldLootRuntimeEntry[] entries,
                out string reason))
        {
            SendResponse(session, requestId, WorldLootResponseMessage.Failed(request.stableId, InteractionResultCode.Rejected, reason));
            return;
        }

        var items = new RewardItemDefinition[entries.Length];
        for (int i = 0; i < entries.Length; ++i)
        {
            LootTableEntryDefinition definition = entries[i].Definition;
            items[i] = new RewardItemDefinition
            {
                itemDataId = definition?.itemDataId ?? 0,
                itemDefinitionId = definition?.itemDefinitionId ?? string.Empty,
                quantity = entries[i].Quantity,
            };
        }
        var reward = new RewardBundleDefinition { items = items };
        RunWorldLootTakeAllAsync(session, requestId, request, target, entries, _runtime.Rewards.GrantAsync(runtime, reward, CancellationToken.None)).Forget();
    }

    private async Task RunWorldLootTakeAsync(
        ClientSession session,
        uint requestId,
        WorldLootTakeRequestMessage request,
        WorldInteractableRuntime target,
        LootTableEntryDefinition lootDefinition,
        Task<RewardGrantResult> operation)
    {
        RewardGrantResult result;
        try { result = await operation.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _mainThreadCompletions.Enqueue(() => _runtime.WorldInteractables.CompleteLootTake(target, request.entryIndex, request.quantity, false));
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"World loot reward failed for peer {session?.Peer?.Id}, object {request.stableId}: {ex.Message}");
            result = new RewardGrantResult(false, PlayerItemOperationStatus.PersistenceRejected, "loot reward failed", null);
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            _runtime.WorldInteractables.CompleteLootTake(target, request.entryIndex, request.quantity, result.Success);
            if (!IsCurrent(session)) return;
            if (!result.Success)
            {
                SendResponse(session, requestId, WorldLootTakeResponseMessage.Failed(
                    request.stableId,
                    result.Status == PlayerItemOperationStatus.InventoryFull ? InteractionResultCode.Rejected : InteractionResultCode.InvalidState,
                    string.IsNullOrWhiteSpace(result.Error) ? "loot transfer failed" : result.Error));
                return;
            }
            SendResponse(session, requestId, BuildWorldLootTakeResponse(target, request.entryIndex));
        });
    }

    private async Task RunWorldLootTakeAllAsync(
        ClientSession session,
        uint requestId,
        WorldLootTakeAllRequestMessage request,
        WorldInteractableRuntime target,
        WorldLootRuntimeEntry[] entries,
        Task<RewardGrantResult> operation)
    {
        RewardGrantResult result;
        try { result = await operation.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _mainThreadCompletions.Enqueue(() => _runtime.WorldInteractables.CompleteLootTakeAll(target, entries, false));
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"World loot TakeAll reward failed for peer {session?.Peer?.Id}, object {request.stableId}: {ex.Message}");
            result = new RewardGrantResult(false, PlayerItemOperationStatus.PersistenceRejected, "loot TakeAll reward failed", null);
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            _runtime.WorldInteractables.CompleteLootTakeAll(target, entries, result.Success);
            if (!IsCurrent(session)) return;
            if (!result.Success)
            {
                SendResponse(session, requestId, WorldLootResponseMessage.Failed(
                    request.stableId,
                    result.Status == PlayerItemOperationStatus.InventoryFull ? InteractionResultCode.Rejected : InteractionResultCode.InvalidState,
                    string.IsNullOrWhiteSpace(result.Error) ? "loot TakeAll failed" : result.Error));
                return;
            }
            SendResponse(session, requestId, BuildWorldLootResponse(target));
        });
    }

    private bool TryValidateWorldLootContent(WorldInteractableRuntime target, out string reason)
    {
        reason = string.Empty;
        WorldLootRuntimeEntry[] entries = target?.SnapshotLoot() ?? Array.Empty<WorldLootRuntimeEntry>();
        for (int i = 0; i < entries.Length; ++i)
        {
            LootTableEntryDefinition definition = entries[i].Definition;
            if (definition == null || definition.itemDataId == 0 || !_runtime.Content.TryGetItem(definition.itemDataId, out ItemDefinition _))
            {
                reason = definition == null || definition.itemDataId == 0
                    ? "loot contains an invalid item definition"
                    : $"loot item data ID '{definition.itemDataId}' is not loaded by the GameServer";
                return false;
            }
        }
        return true;
    }

    private static WorldLootTakeResponseMessage BuildWorldLootTakeResponse(WorldInteractableRuntime target, int entryIndex)
    {
        int remainingQuantity = 0;
        WorldLootRuntimeEntry[] entries = target?.SnapshotLoot() ?? Array.Empty<WorldLootRuntimeEntry>();
        for (int i = 0; i < entries.Length; ++i)
            if (entries[i].EntryIndex == entryIndex) { remainingQuantity = Math.Max(0, entries[i].Quantity); break; }

        return new WorldLootTakeResponseMessage
        {
            success = true,
            resultCode = (byte)InteractionResultCode.Success,
            error = string.Empty,
            stableId = target?.Key.StableId ?? 0,
            lootRevision = target?.Revision ?? 0,
            entryIndex = entryIndex,
            remainingQuantity = remainingQuantity,
            depleted = target == null || target.Depleted,
        };
    }

    private WorldLootResponseMessage BuildWorldLootResponse(WorldInteractableRuntime target)
    {
        WorldLootRuntimeEntry[] entries = target?.SnapshotLoot() ?? Array.Empty<WorldLootRuntimeEntry>();
        var wire = new WorldLootEntryWire[entries.Length];
        for (int i = 0; i < entries.Length; ++i)
        {
            WorldLootRuntimeEntry entry = entries[i];
            ushort itemDataId = entry.Definition?.itemDataId ?? 0;
            string definitionId = entry.Definition?.itemDefinitionId ?? string.Empty;
            string displayName = definitionId;
            if (_runtime.Content.TryGetItem(itemDataId, out ItemDefinition item))
            {
                definitionId = item.definitionId ?? definitionId;
                displayName = string.IsNullOrWhiteSpace(item.displayName) ? definitionId : item.displayName;
            }
            wire[i] = new WorldLootEntryWire { entryIndex = entry.EntryIndex, itemDataId = itemDataId, definitionId = definitionId, displayName = displayName, quantity = entry.Quantity };
        }

        return new WorldLootResponseMessage
        {
            success = true,
            resultCode = (byte)InteractionResultCode.Success,
            error = string.Empty,
            stableId = target?.Key.StableId ?? 0,
            lootRevision = target?.Revision ?? 0,
            sourceLabel = string.IsNullOrWhiteSpace(target?.Definition?.label) ? "Loot" : target.Definition.label,
            depleted = target == null || target.Depleted,
            entries = wire,
        };
    }
}
