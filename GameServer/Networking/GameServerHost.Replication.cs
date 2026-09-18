using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Game.GameServer.Runtime;
using Game.GameServer.Backend;
using Game.GameServer.Replication;
using Game.Server.Application.Abilities;
using Game.Server.Application.Connections;
using Game.Server.Application.Population;
using Game.Server.Application.Sessions;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Characters;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using Game.Shared.World;
using Game.Shared.WorldItems;
using Game.UnityIntegration;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Player.Networking;
using Player.Shared;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    // Player replication integration: AOI edge application, spawn/destroy/snapshot emission,
    // appearance deltas, and replication diagnostics. Simulation authority remains elsewhere.
    private void ScheduleReplicationDiagnostics()
    {
        if (!_running || !_scheduler.IsRunning)
            return;

        _replicationDiagnosticsTask = _scheduler.Schedule(ReplicationDiagnosticsIntervalSeconds, () =>
        {
            LogReplicationDiagnostics();
            ScheduleReplicationDiagnostics();
        });
    }

    private void LogReplicationDiagnostics()
    {
        bool combatWireActivity = _options.CombatWireDiagnostics &&
            (_wireCombatDamageMessages > 0 || _wireAbilityStateMessages > 0 ||
             _wireResourceDeltaMessages > 0 || _wireStatusDeltaMessages > 0);
        bool nonOutboundActivity = _replicationBudgetDeferrals > 0 ||
            _mainThreadCompletions.Count > 0 ||
            combatWireActivity;
        string outboundSummary = CaptureOutboundDiagnosticsAndReset(
            out bool outboundActivity,
            forceSummary: nonOutboundActivity);

        if (nonOutboundActivity || outboundActivity)
        {
            Console.WriteLine(
                $"Server load: replPackets={_replicationSnapshotPacketsSent}, replBytes={_replicationPayloadBytes}, " +
                $"replDeferrals={_replicationBudgetDeferrals}, pendingObserversMax={_replicationMaxPendingObservers}, " +
                $"pendingSnapshotsMax={_replicationMaxPendingSnapshots}, completionDepth={_mainThreadCompletions.Count}, " +
                $"completionHighWater={_mainThreadCompletions.HighWater}, completionProducerWaits={_mainThreadCompletions.ProducerWaits}. " +
                outboundSummary);

            if (combatWireActivity)
            {
                Console.WriteLine(
                    $"Combat wire/10s: damage={_wireCombatDamageMessages}/{_wireCombatDamageBytes}B, " +
                    $"ability={_wireAbilityStateMessages}/{_wireAbilityStateBytes}B, " +
                    $"resource={_wireResourceDeltaMessages}/{_wireResourceDeltaBytes}B, " +
                    $"status={_wireStatusDeltaMessages}/{_wireStatusDeltaBytes}B.");
            }
        }

        _wireCombatDamageMessages = 0;
        _wireCombatDamageBytes = 0;
        _wireAbilityStateMessages = 0;
        _wireAbilityStateBytes = 0;
        _wireResourceDeltaMessages = 0;
        _wireResourceDeltaBytes = 0;
        _wireStatusDeltaMessages = 0;
        _wireStatusDeltaBytes = 0;

        _replicationSnapshotCandidates = 0;
        _replicationSnapshotsSent = 0;
        _replicationSnapshotPacketsSent = 0;
        _replicationSnapshotsCoalesced = 0;
        _replicationSnapshotsLodSuppressed = 0;
        _replicationSpawnSends = 0;
        _replicationDestroySends = 0;
        _replicationPayloadBytes = 0;
        _replicationBudgetDeferrals = 0;
        _replicationMaxPendingObservers = 0;
        _replicationMaxPendingSnapshots = 0;
    }

    private void ApplyInterestChanges(IReadOnlyList<WorldInterestService.Change> changes)
    {
        if (changes == null || changes.Count == 0)
            return;

        double now = _scheduler.ServerTime;
        for (int i = 0; i < changes.Count; ++i)
        {
            WorldInterestService.Change change = changes[i];
            ClientSession observer = change.Observer;
            if (!IsCurrent(observer) || !observer.Ready || observer.Entity == null)
                continue;

            if (change.Kind == WorldInterestService.ChangeKind.Added)
            {
                ClientSession target = change.Target;
                if (!IsCurrent(target) || !target.Ready || target.Entity == null)
                    continue;

                SendSpawn(observer, target.Entity);
                if (_worldInterest.TryGetDistanceSquared(observer, target, out float distanceSquared))
                {
                    double interval = _options.ReplicationLodEnabled
                        ? _replicationLodPolicy.GetIntervalSeconds(distanceSquared)
                        : 1d / Math.Max(1, (int)_options.TickRate);
                    _worldInterest.MarkBaselineSent(observer, target, now + interval);
                }
            }
            else if (change.Kind == WorldInterestService.ChangeKind.Removed && change.TargetObjectId != 0u)
            {
                // Never emit a stale queued delta after the reliable AOI destroy.
                DropPendingSnapshot(observer, change.TargetObjectId);
                SendDestroy(observer, change.TargetObjectId);
            }
        }
    }

    private byte ApplyVisibilityPresentationFlags(IPlayerEntityPresentationSource entity, byte flags)
    {
        if (entity == null || entity.ObjectId == 0u)
            return flags;

        if (_readySessionsByObjectId.TryGetValue(entity.ObjectId, out ClientSession target) &&
            target != null &&
            (_runtime.GameMasters.IsHiddenObserver(target.AuthenticatedAccountId) ||
             _runtime.GameMasters.IsPlayerHidden(target.AuthenticatedAccountId)))
        {
            // Hidden is already an allocated PlayerEntity snapshot bit. Reusing it gives
            // authorized observers/self visible feedback without adding a message or bytes.
            flags |= (byte)PlayerEntityFlags.Hidden;
        }

        return flags;
    }

    private void SendSpawn(ClientSession recipient, ServerPlayerEntity entity)
    {
        _writer.Reset();
        _writer.PutPackedUShort(SyncBaseLineMessageType);
        _writer.PutPackedUInt(CurrentTick);
        _writer.Put((ushort)1);
        _writer.Put(GameStateSpawn);
        _writer.Put(false); // prefab, not scene object
        _writer.PutPackedInt(PlayerAssetHash);
        _writer.Put(entity.X);
        _writer.Put(entity.Y);
        _writer.Put(entity.Z);
        _writer.Put(0f);
        _writer.Put(entity.YawDegrees);
        _writer.Put(0f);
        _writer.PutPackedUInt(entity.ObjectId);
        _writer.PutPackedLong(entity.ConnectionId);
        _writer.PutPackedInt(2);

        _writer.PutPackedInt(SnapshotElementId);
        byte spawnFlags = ApplyVisibilityPresentationFlags(entity, entity.SnapshotFlags);
        WriteSnapshot(_writer, entity, entity.SnapshotSpeed, spawnFlags, entity.SnapshotMoveState);

        _writer.PutPackedInt(AppearanceElementId);
        WriteAppearance(_writer, entity);

        _replicationSpawnSends++;
        _replicationPayloadBytes += _writer.Length;
        Send(recipient, _writer, DeliveryMethod.ReliableOrdered);
    }

    private void QueueSnapshot(
        ClientSession recipient,
        IPlayerEntityPresentationSource entity,
        float speed,
        byte flags,
        byte moveState,
        bool priority,
        byte actionState = (byte)PlayerEntityActionState.None,
        byte actionId = 0)
    {
        if (!IsCurrent(recipient) || !recipient.Ready || recipient.Entity == null || entity == null)
            return;

        flags = ApplyVisibilityPresentationFlags(entity, flags);

        if (!_snapshotBatches.TryGetValue(recipient, out ObserverSnapshotBatch batch))
        {
            batch = new ObserverSnapshotBatch(recipient);
            _snapshotBatches.Add(recipient, batch);
        }

        uint objectId = entity.ObjectId;
        bool prioritySnapshot = priority;
        if (batch.Snapshots.TryGetValue(objectId, out PendingSnapshot previous))
        {
            _replicationSnapshotsCoalesced++;

            // Teleport and one-shot action pulses are edge-triggered presentation data. A
            // normal movement snapshot later in the same server frame must not erase them
            // before the observer batch is flushed.
            flags |= (byte)(previous.Flags & (byte)PlayerEntityFlags.Teleport);
            if (actionState == (byte)PlayerEntityActionState.None &&
                previous.ActionState != (byte)PlayerEntityActionState.None)
            {
                actionState = previous.ActionState;
                actionId = previous.ActionId;
            }
            prioritySnapshot |= previous.Priority;
        }

        // Latest authoritative transform/presentation state wins while the snapshot is
        // pending. Deferred entries remain coalescible across frames, preventing a network
        // burst cap from turning into an ever-growing queue of stale transforms.
        batch.Snapshots[objectId] = new PendingSnapshot(
            entity, speed, flags, moveState, actionState, actionId, prioritySnapshot);

        if (!batch.Active)
        {
            batch.Active = true;
            _activeSnapshotBatches.Add(batch);
        }
    }

    private static double ElapsedMillisecondsSince(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000d / Stopwatch.Frequency;

    private void FlushSnapshotBatches()
    {
        int batchCount = _activeSnapshotBatches.Count;
        if (batchCount == 0)
            return;

        int globalPacketsRemaining = _options.ReplicationPacketsPerFrame;
        int globalBytesRemaining = _options.ReplicationBytesPerFrame;
        double maxMilliseconds = _options.ReplicationMillisecondsPerFrame;
        long frameStartTimestamp = Stopwatch.GetTimestamp();

        int startIndex = _snapshotFlushCursor;
        if (startIndex < 0 || startIndex >= batchCount)
            startIndex = 0;

        int visited = 0;
        bool budgetExhausted = false;
        while (visited < batchCount)
        {
            if (globalPacketsRemaining <= 0 ||
                globalBytesRemaining <= 32 ||
                ElapsedMillisecondsSince(frameStartTimestamp) >= maxMilliseconds)
            {
                budgetExhausted = true;
                break;
            }

            int index = (startIndex + visited) % batchCount;
            ObserverSnapshotBatch batch = _activeSnapshotBatches[index];
            ClientSession observer = batch.Observer;

            if (!IsCurrent(observer) || !observer.Ready || observer.Entity == null)
            {
                batch.Snapshots.Clear();
                batch.KeyScratch.Clear();
                batch.PacketScratch.Clear();
                batch.Active = false;
                visited++;
                continue;
            }

            FlushSnapshotBatch(
                observer,
                batch,
                _options.ReplicationPacketsPerObserverFrame,
                ref globalPacketsRemaining,
                ref globalBytesRemaining,
                frameStartTimestamp,
                maxMilliseconds);

            visited++;
        }

        int retained = 0;
        int pendingSnapshots = 0;
        for (int i = 0; i < batchCount; ++i)
        {
            ObserverSnapshotBatch batch = _activeSnapshotBatches[i];
            if (batch.Snapshots.Count > 0 &&
                IsCurrent(batch.Observer) &&
                batch.Observer.Ready &&
                batch.Observer.Entity != null)
            {
                batch.Active = true;
                pendingSnapshots += batch.Snapshots.Count;
                _activeSnapshotBatches[retained++] = batch;
            }
            else
            {
                batch.Active = false;
                batch.KeyScratch.Clear();
                batch.PacketScratch.Clear();
            }
        }

        if (retained < _activeSnapshotBatches.Count)
            _activeSnapshotBatches.RemoveRange(retained, _activeSnapshotBatches.Count - retained);

        if (retained > 0)
            _snapshotFlushCursor = (_snapshotFlushCursor + Math.Max(1, visited)) % retained;
        else
            _snapshotFlushCursor = 0;

        if (retained > _replicationMaxPendingObservers)
            _replicationMaxPendingObservers = retained;
        if (pendingSnapshots > _replicationMaxPendingSnapshots)
            _replicationMaxPendingSnapshots = pendingSnapshots;

        if (budgetExhausted && pendingSnapshots > 0)
            _replicationBudgetDeferrals++;
    }

    private void FlushSnapshotBatch(
        ClientSession recipient,
        ObserverSnapshotBatch batch,
        int maxPackets,
        ref int globalPacketsRemaining,
        ref int globalBytesRemaining,
        long frameStartTimestamp,
        double maxMilliseconds)
    {
        if (batch == null ||
            batch.Snapshots.Count == 0 ||
            maxPackets <= 0 ||
            globalPacketsRemaining <= 0 ||
            globalBytesRemaining <= 32)
        {
            return;
        }

        batch.KeyScratch.Clear();

        // Forced/death/presentation deltas go first so ordinary movement cannot delay a
        // discrete state transition when an observer is at its burst ceiling.
        foreach (KeyValuePair<uint, PendingSnapshot> pair in batch.Snapshots)
            if (pair.Value.Priority) batch.KeyScratch.Add(pair.Key);
        foreach (KeyValuePair<uint, PendingSnapshot> pair in batch.Snapshots)
            if (!pair.Value.Priority) batch.KeyScratch.Add(pair.Key);

        int keyIndex = 0;
        int packetsSent = 0;
        while (keyIndex < batch.KeyScratch.Count &&
               packetsSent < maxPackets &&
               globalPacketsRemaining > 0 &&
               ElapsedMillisecondsSince(frameStartTimestamp) < maxMilliseconds)
        {
            BeginSnapshotPacket(out int objectCountPosition);
            ushort objectCount = 0;
            bool packetPriority = false;
            batch.PacketScratch.Clear();

            while (keyIndex < batch.KeyScratch.Count)
            {
                uint objectId = batch.KeyScratch[keyIndex];
                if (!batch.Snapshots.TryGetValue(objectId, out PendingSnapshot snapshot))
                {
                    keyIndex++;
                    continue;
                }

                IPlayerEntityPresentationSource entity = snapshot.Entity;
                if (entity == null || entity.ObjectId == 0u)
                {
                    batch.Snapshots.Remove(objectId);
                    keyIndex++;
                    continue;
                }

                // Never let ordinary movement hitch a ride on the critical reserve just
                // because it shares a packet with a forced/death/presentation snapshot.
                // KeyScratch is priority-first, so ending this packet here preserves order
                // while the next packet handles the other budget class.
                if (objectCount == 0)
                    packetPriority = snapshot.Priority;
                else if (snapshot.Priority != packetPriority)
                    break;

                WriteSnapshotObject(
                    _replicationObjectWriter,
                    entity,
                    snapshot.Speed,
                    snapshot.Flags,
                    snapshot.MoveState,
                    snapshot.ActionState,
                    snapshot.ActionId);

                int objectLength = _replicationObjectWriter.Length;
                if (objectLength <= 0)
                {
                    keyIndex++;
                    continue;
                }

                if (objectCount > 0 && _writer.Length + objectLength > MaxUnreliableReplicationPacketSize)
                    break;

                if (_writer.Length + objectLength > MaxUnreliableReplicationPacketSize)
                {
                    throw new InvalidOperationException(
                        $"PlayerEntity snapshot object {entity.ObjectId} requires {_writer.Length + objectLength} bytes, " +
                        $"exceeding the {MaxUnreliableReplicationPacketSize}-byte unreliable replication ceiling.");
                }

                _writer.Put(_replicationObjectWriter.Data.AsSpan(0, objectLength));
                batch.PacketScratch.Add(objectId);
                objectCount++;
                keyIndex++;
            }

            if (objectCount == 0)
                break;

            int packetBytes = _writer.Length;
            if (packetBytes > globalBytesRemaining)
                break;

            // Ordinary transform traffic spends the existing normal per-connection
            // outbound tokens. Priority state transitions use the existing critical reserve.
            // If budget is unavailable, keep these authoritative states pending so newer
            // state can coalesce over them instead of queueing a stale serialized packet.
            if (!TryCompleteAndSendSnapshotPacket(
                    recipient,
                    objectCountPosition,
                    objectCount,
                    packetPriority))
            {
                break;
            }

            packetsSent++;
            globalPacketsRemaining--;
            globalBytesRemaining -= packetBytes;

            for (int i = 0; i < batch.PacketScratch.Count; ++i)
                batch.Snapshots.Remove(batch.PacketScratch[i]);
        }

        batch.KeyScratch.Clear();
        batch.PacketScratch.Clear();
    }

    private void BeginSnapshotPacket(out int objectCountPosition)
    {
        _writer.Reset();
        _writer.PutPackedUShort(SyncDeltaMessageType);
        _writer.PutPackedUInt(CurrentTick);
        objectCountPosition = _writer.Length;
        _writer.Put((ushort)0);
    }

    private bool TryCompleteAndSendSnapshotPacket(
        ClientSession recipient,
        int objectCountPosition,
        ushort objectCount,
        bool priorityPacket)
    {
        if (objectCount == 0)
            return false;

        int endPosition = _writer.Length;
        _writer.SetPosition(objectCountPosition);
        _writer.Put(objectCount);
        _writer.SetPosition(endPosition);

        OutboundPriority priority = priorityPacket
            ? OutboundPriority.Critical
            : OutboundPriority.Normal;
        if (!TrySendImmediateOutbound(
                recipient,
                _writer,
                DeliveryMethod.Unreliable,
                priority,
                OutboundFamily.Movement))
        {
            return false;
        }

        _replicationSnapshotsSent += objectCount;
        _replicationSnapshotPacketsSent++;
        _replicationPayloadBytes += _writer.Length;
        return true;
    }

    private void WriteSnapshotObject(
        NetDataWriter writer,
        IPlayerEntityPresentationSource entity,
        float speed,
        byte flags,
        byte moveState,
        byte actionState,
        byte actionId)
    {
        writer.Reset();
        writer.PutPackedUInt(entity.ObjectId);

        int dataLengthPosition = writer.Length;
        writer.Put((ushort)0);
        int dataStartPosition = writer.Length;
        writer.Put((ushort)1); // element count
        writer.PutPackedInt(SnapshotElementId);
        WriteSnapshot(writer, entity, speed, flags, moveState, actionState, actionId);

        int endPosition = writer.Length;
        int dataLength = endPosition - dataStartPosition;
        if (dataLength > ushort.MaxValue)
            throw new InvalidOperationException("PlayerEntity snapshot delta exceeded protocol payload length.");

        writer.SetPosition(dataLengthPosition);
        writer.Put((ushort)dataLength);
        writer.SetPosition(endPosition);
    }

    private void DropPendingSnapshot(ClientSession observer, uint objectId)
    {
        if (observer == null || objectId == 0u)
            return;

        if (_snapshotBatches.TryGetValue(observer, out ObserverSnapshotBatch batch))
            batch.Snapshots.Remove(objectId);
    }

    private void RemoveSnapshotBatch(ClientSession observer)
    {
        if (observer == null || !_snapshotBatches.Remove(observer, out ObserverSnapshotBatch batch))
            return;

        batch.Snapshots.Clear();
        batch.KeyScratch.Clear();
        batch.PacketScratch.Clear();
        batch.Active = false;
    }

    private void SendDestroy(ClientSession recipient, uint objectId)
    {
        if (!IsCurrent(recipient) || !recipient.Ready || recipient.Entity == null || objectId == 0u)
            return;

        _writer.Reset();
        _writer.PutPackedUShort(SyncBaseLineMessageType);
        _writer.PutPackedUInt(CurrentTick);
        _writer.Put((ushort)1);
        _writer.Put(GameStateDestroy);
        _writer.PutPackedUInt(objectId);
        _writer.Put((byte)0);
        _replicationDestroySends++;
        _replicationPayloadBytes += _writer.Length;
        Send(recipient, _writer, DeliveryMethod.ReliableOrdered);
    }

    private void WriteSnapshot(
        NetDataWriter writer,
        IPlayerEntityPresentationSource entity,
        float speed,
        byte flags,
        byte moveState,
        byte actionState = (byte)PlayerEntityActionState.None,
        byte actionId = 0)
    {
        bool dead = entity.IsDead;
        if (dead)
        {
            speed = 0f;
            moveState = (byte)PlayerEntityMoveState.Dead;
            flags = (byte)(flags & ~(byte)(PlayerEntityFlags.Sprinting | PlayerEntityFlags.Running | PlayerEntityFlags.Jumping));
            actionState = (byte)PlayerEntityActionState.Dead;
            actionId = 0;
        }

        writer.PutPackedUInt(entity.ObjectId);
        writer.Put(entity.Generation);
        writer.PutPackedUInt(CurrentTick);
        writer.PutPackedInt(ServerPlayerEntity.QuantizePosition(entity.X));
        writer.PutPackedInt(ServerPlayerEntity.QuantizePosition(entity.Y));
        writer.PutPackedInt(ServerPlayerEntity.QuantizePosition(entity.Z));
        writer.Put(ServerPlayerEntity.QuantizeYaw(entity.YawDegrees));
        writer.Put(ServerPlayerEntity.QuantizeUnsignedSpeed(speed));
        writer.Put(ServerPlayerEntity.QuantizeSignedSpeed(entity.SnapshotVerticalSpeed));
        writer.Put(0);       // castingSkillHash
        writer.Put(moveState);
        writer.Put(actionState);
        writer.Put(actionId);
        writer.Put(flags);
    }

    private void WriteAppearance(NetDataWriter writer, ServerPlayerEntity entity)
    {
        PlayerItemsSnapshot itemSnapshot = _runtime.PlayerItems.GetSnapshot(entity.Runtime);
        long equipmentRevision = itemSnapshot?.equipmentRevision ?? 0L;
        uint equipmentVersion = equipmentRevision <= 0
            ? 1u
            : (uint)Math.Min(equipmentRevision, uint.MaxValue);

        var appearance = new PlayerEntityAppearance
        {
            generation = entity.Generation,
            sequence = entity.AppearanceSequence,
            characterId = entity.Runtime.CharacterId.Value,
            equipmentVersion = equipmentVersion,
            displayName = entity.Runtime.Character?.Name ?? string.Empty,
            guildName = string.Empty,
            equipmentVisuals = BuildEquipmentVisualSelections(itemSnapshot),
            appearance = entity.Runtime.CaptureAppearance(),
            presentation = entity.Runtime.CapturePresentationPreferences(),
        };
        appearance.Serialize(writer);
    }

    private PlayerEquipmentVisualSelection[] BuildEquipmentVisualSelections(PlayerItemsSnapshot snapshot)
    {
        EquipmentSlotView[] equipment = snapshot?.equipment ?? Array.Empty<EquipmentSlotView>();
        if (equipment.Length == 0)
            return Array.Empty<PlayerEquipmentVisualSelection>();

        var result = new List<PlayerEquipmentVisualSelection>(Math.Min(equipment.Length, PlayerEntityAppearance.MaxEquipmentVisualSelections));
        for (int i = 0; i < equipment.Length && result.Count < PlayerEntityAppearance.MaxEquipmentVisualSelections; ++i)
        {
            EquipmentSlotView slot = equipment[i];
            if (slot?.item == null ||
                !_runtime.Content.TryGetEquipmentSlot(slot.slotId, out EquipmentSlotDefinition slotDefinition) ||
                slotDefinition == null || slotDefinition.presentationSlotId == 0 ||
                !_runtime.Content.TryGetItem(slot.item.definitionId, out ItemDefinition itemDefinition) ||
                itemDefinition == null || itemDefinition.presentationId == 0)
                continue;

            result.Add(new PlayerEquipmentVisualSelection(
                slotDefinition.presentationSlotId,
                itemDefinition.presentationId));
        }

        return result.Count == 0 ? Array.Empty<PlayerEquipmentVisualSelection>() : result.ToArray();
    }

    private void SendAppearanceDelta(ClientSession recipient, ServerPlayerEntity entity)
    {
        if (!IsCurrent(recipient) || !recipient.Ready || recipient.Entity == null || entity == null)
            return;

        _writer.Reset();
        _writer.PutPackedUShort(SyncDeltaMessageType);
        _writer.PutPackedUInt(CurrentTick);
        _writer.Put((ushort)1); // object count
        _writer.PutPackedUInt(entity.ObjectId);

        int dataLengthPosition = _writer.Length;
        _writer.Put((ushort)0);
        int dataStartPosition = _writer.Length;
        _writer.Put((ushort)1); // element count
        _writer.PutPackedInt(AppearanceElementId);
        WriteAppearance(_writer, entity);

        int endPosition = _writer.Length;
        int dataLength = endPosition - dataStartPosition;
        if (dataLength > ushort.MaxValue)
            throw new InvalidOperationException("PlayerEntity appearance delta exceeded protocol payload length.");

        _writer.SetPosition(dataLengthPosition);
        _writer.Put((ushort)dataLength);
        _writer.SetPosition(endPosition);
        _replicationPayloadBytes += _writer.Length;
        Send(recipient, _writer, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastAppearanceDelta(ClientSession target)
    {
        if (!IsCurrent(target) || !target.Ready || target.Entity == null)
            return;

        // The owner must always observe its own equipment change even if AOI bookkeeping is
        // between reconciliation passes. Remote observers use the same appearance payload.
        SendAppearanceDelta(target, target.Entity);

        if (!_worldInterest.TryGetObservers(target, out HashSet<ClientSession> observers) || observers == null)
            return;

        foreach (ClientSession observer in observers)
        {
            if (!ReferenceEquals(observer, target))
                SendAppearanceDelta(observer, target.Entity);
        }
    }
}
