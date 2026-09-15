using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Replication;
using Game.GameServer.Runtime;
using Game.Server.Domain.Players;
using Game.Server.Domain.WorldItems;
using Game.Shared.Interactions;
using Game.Shared.Protocol;
using Game.Shared.World;
using Game.Shared.WorldItems;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{

    private void RegisterWorldItemRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, PlayerItemRequestTypes.Drop, HandleDropItem);
        RegisterRequest(handlers, WorldItemRequestTypes.Snapshot,
            (session, requestId, _) => HandleWorldItemsSnapshot(session, requestId));
        RegisterRequest(handlers, WorldItemRequestTypes.Loot, HandleWorldItemLoot);
    }
    private void HandleWorldItemsSnapshot(ClientSession session, uint requestId)
    {
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, WorldItemsSnapshotMessage.Failed("character is not in world"));
            return;
        }

        try
        {
            WorldItemsSnapshot authoritative = _runtime.WorldItems.GetSnapshot(runtime.Location.MapId, runtime.Location.InstanceId);
            SendResponse(session, requestId, ToWire(_worldItemInterest.BuildSnapshot(session, authoritative)));
        }
        catch (Exception ex)
        {
            _worldItemInterest.Unregister(session);
            Console.Error.WriteLine($"World-item AOI snapshot failed for peer {session.Peer.Id}; player session remains active: {ex}");
            SendResponse(session, requestId, WorldItemsSnapshotMessage.Failed("world-item AOI snapshot is temporarily unavailable"));
        }
    }

    private void HandleDropItem(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new DropItemRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime) || session.Entity == null)
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed((byte)PlayerItemOperationStatus.CharacterUnavailable, "character is not in world"));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.PersistenceRejected,
                BackendPersistenceUnavailableMessage));
            return;
        }
        WorldPosition position = session.Entity.CaptureLocation().Position;
        RunPlayerItemOperationAsync(session, requestId, _runtime.WorldItems.DropAsync(runtime, request.inventoryIndex, request.quantity, position, CancellationToken.None)).Forget();
    }

    private void HandleWorldItemLoot(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new WorldItemLootRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime) || request.itemInstanceId <= 0)
        {
            SendResponse(session, requestId, FailedWorldInteraction(request.sequence, request.itemInstanceId, InteractionResultCode.InvalidTarget, "world item is unavailable"));
            return;
        }

        if (!TryAdmitWorldItemInteractionRequest(session, request.sequence, out InteractionResultCode admissionCode, out string admissionDetail))
        {
            SendResponse(session, requestId, FailedWorldInteraction(request.sequence, request.itemInstanceId, admissionCode, admissionDetail));
            return;
        }

        var location = runtime.Location;
        if (!_runtime.WorldItems.TryGet(location.MapId, location.InstanceId, request.itemInstanceId, out WorldItemState item))
        {
            SendResponse(session, requestId, FailedWorldInteraction(request.sequence, request.itemInstanceId, InteractionResultCode.TargetUnavailable, "world item is unavailable"));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, FailedWorldInteraction(
                request.sequence,
                request.itemInstanceId,
                InteractionResultCode.Rejected,
                BackendPersistenceUnavailableMessage));
            return;
        }
        RunWorldItemInteractionAsync(session, requestId, request, runtime, item).Forget();
    }

    private async Task RunWorldItemInteractionAsync(ClientSession session, uint requestId, WorldItemLootRequestMessage request, PlayerRuntime runtime, WorldItemState item)
    {
        InteractionResult result;
        try
        {
            result = await _runtime.Interactions.ExecuteWorldActionAsync(
                runtime,
                new InteractionTargetHandle(InteractionTargetKind.NetworkWorldObject, item.ItemInstanceId.Value),
                item.MapId,
                item.InstanceId,
                item.Position,
                InteractionActionId.Loot,
                request.sequence,
                _scheduler.ServerTime,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"World-item interaction failed for peer {session?.Peer?.Id}: {ex.Message}");
            result = new InteractionResult(request.sequence, InteractionActionId.Loot, InteractionResultCode.Rejected,
                new InteractionTargetHandle(InteractionTargetKind.NetworkWorldObject, request.itemInstanceId), "world-item interaction failed");
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session)) return;
            SendResponse(session, requestId, ToWire(result));
        });
    }

    private void TryInitializeWorldItemInterestAfterReady(ClientSession session)
    {
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
            return;

        try
        {
            WorldItemsSnapshot authoritative = _runtime.WorldItems.GetSnapshot(runtime.Location.MapId, runtime.Location.InstanceId);
            _worldItemInterest.Register(session, authoritative);
            SendWorldItemsSnapshot(session, authoritative);
        }
        catch (Exception ex)
        {
            // World-item replication is presentation/supporting gameplay state. It must not
            // invalidate an otherwise successful authoritative player admission.
            _worldItemInterest.Unregister(session);
            Console.Error.WriteLine($"World-item AOI initialization failed for peer {session.Peer.Id}; player admission continues: {ex}");
        }
    }

    private void TryReconcileWorldItemInterestMoved(ClientSession session)
    {
        try
        {
            WorldItemsSnapshot partitionSnapshot = _worldItemInterest.NeedsPartitionRefresh(session)
                ? _runtime.WorldItems.GetSnapshot(session.Entity.Runtime.Location.MapId, session.Entity.Runtime.Location.InstanceId)
                : null;
            ApplyWorldItemInterestChanges(_worldItemInterest.ReconcileMoved(session, partitionSnapshot));
        }
        catch (Exception ex)
        {
            // Do not allow a loot-presentation/AOI defect to quarantine PlayerSimulation or
            // interrupt the already-validated player movement/replication path.
            _worldItemInterest.Unregister(session);
            Console.Error.WriteLine($"World-item AOI movement reconcile failed for peer {session.Peer.Id}; world-item AOI disabled for this session: {ex}");
        }
    }

    private void SendWorldItemsSnapshot(ClientSession session, WorldItemsSnapshot authoritativeSnapshot = null)
    {
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime)) return;
        authoritativeSnapshot ??= _runtime.WorldItems.GetSnapshot(runtime.Location.MapId, runtime.Location.InstanceId);
        SendClientMessage(
            session,
            WorldItemMessageTypes.Snapshot,
            ToWire(_worldItemInterest.BuildSnapshot(session, authoritativeSnapshot)),
            DeliveryMethod.ReliableOrdered);
    }

    private void OnAuthoritativeWorldItemChanged(WorldItemChange change)
    {
        _mainThreadCompletions.Enqueue(() =>
        {
            ApplyWorldItemInterestChanges(_worldItemInterest.ApplyAuthoritativeChange(change));
        });
    }

    private void ApplyWorldItemInterestChanges(IReadOnlyList<WorldItemInterestService.Change> changes)
    {
        if (changes == null || changes.Count == 0)
            return;

        for (int i = 0; i < changes.Count; ++i)
        {
            WorldItemInterestService.Change change = changes[i];
            ClientSession observer = change.Observer;
            if (!IsCurrent(observer) || !observer.Ready || observer.Entity == null)
                continue;

            var delta = new WorldItemDeltaMessage
            {
                changeKind = change.Kind == WorldItemInterestService.ChangeKind.Added
                    ? (byte)WorldItemChangeKind.Added
                    : (byte)WorldItemChangeKind.Removed,
                mapId = observer.Entity.Runtime.Location.MapId,
                instanceId = observer.Entity.Runtime.Location.InstanceId,
                worldRevision = change.ViewRevision,
                itemInstanceId = change.ItemInstanceId,
                hasItem = change.Kind == WorldItemInterestService.ChangeKind.Added && change.Item != null,
                item = change.Item == null ? default : ToWire(change.Item),
            };

            SendClientMessage(observer, WorldItemMessageTypes.Delta, delta, DeliveryMethod.ReliableOrdered);
        }
    }

    private static WorldItemsSnapshotMessage ToWire(WorldItemsSnapshot snapshot)
    {
        if (snapshot == null) return WorldItemsSnapshotMessage.Failed("world-item state is unavailable");
        WorldItemView[] source = snapshot.items ?? Array.Empty<WorldItemView>();
        if (source.Length > WorldItemsSnapshotMessage.MaxItems) return WorldItemsSnapshotMessage.Failed("world-item snapshot exceeds protocol capacity");
        var items = new WorldItemWire[source.Length];
        for (int i = 0; i < source.Length; ++i) items[i] = ToWire(source[i]);
        return new WorldItemsSnapshotMessage { success = true, error = string.Empty, mapId = snapshot.mapId, instanceId = snapshot.instanceId, revision = snapshot.revision, items = items };
    }

    private static WorldItemWire ToWire(WorldItemView item) => new WorldItemWire
    {
        itemInstanceId = item.itemInstanceId, itemRevision = item.itemRevision, itemDataId = item.itemDataId, definitionId = item.definitionId, displayName = item.displayName,
        quantity = item.quantity, durability = item.durability, maxDurability = item.maxDurability, unitWeight = item.unitWeight,
        mapId = item.mapId, instanceId = item.instanceId, positionX = item.positionX, positionY = item.positionY, positionZ = item.positionZ,
    };

    private static WorldItemInteractionResponseMessage ToWire(InteractionResult result) => new WorldItemInteractionResponseMessage
    {
        success = result.Success, sequence = result.Sequence, actionId = (ushort)result.ActionId, resultCode = (byte)result.ResultCode,
        itemInstanceId = result.Target.PrimaryId, detail = result.Detail,
    };

    private static WorldItemInteractionResponseMessage FailedWorldInteraction(uint sequence, long itemId, InteractionResultCode code, string detail) => new WorldItemInteractionResponseMessage
    {
        success = false, sequence = sequence, actionId = (ushort)InteractionActionId.Loot, resultCode = (byte)code, itemInstanceId = itemId, detail = detail ?? string.Empty,
    };
}
