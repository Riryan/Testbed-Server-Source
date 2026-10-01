using System;
using System.Runtime.CompilerServices;
using Game.GameServer.Runtime;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Interactions;
using Game.Shared.World;
using Player.Shared;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private sealed class PersistentInteractionPresentation
    {
        public byte PresentationId;
        public uint Version;
    }

    // Weak-keyed by the existing ClientSession so disconnects cannot create a retained
    // presentation-state table. This is server-local state; nothing new crosses the wire.
    private readonly ConditionalWeakTable<ClientSession, PersistentInteractionPresentation>
        _persistentInteractionPresentation =
            new ConditionalWeakTable<ClientSession, PersistentInteractionPresentation>();

    private uint _interactionTestDummyPresentationVersion;

    private bool TryGetPersistentInteractionPresentation(
        ClientSession session,
        out byte presentationId)
    {
        presentationId = 0;
        if (session == null ||
            !_persistentInteractionPresentation.TryGetValue(
                session,
                out PersistentInteractionPresentation state) ||
            state == null ||
            state.PresentationId == InteractionPresentationWire.Stop)
        {
            return false;
        }

        presentationId = state.PresentationId;
        return true;
    }

    private uint BeginPersistentInteractionPresentation(
        ClientSession session,
        byte presentationId)
    {
        if (session == null ||
            presentationId == InteractionPresentationWire.Stop ||
            !IsCurrent(session) ||
            !session.Ready ||
            session.Entity == null)
        {
            return 0;
        }

        PersistentInteractionPresentation state =
            _persistentInteractionPresentation.GetValue(
                session,
                _ => new PersistentInteractionPresentation());

        state.Version++;
        if (state.Version == 0)
            state.Version = 1;
        state.PresentationId = presentationId;

        BroadcastSnapshot(
            session,
            session.Entity.SnapshotSpeed,
            session.Entity.SnapshotFlags,
            session.Entity.SnapshotMoveState,
            ServerPlayerSnapshotReason.PresentationState,
            (byte)PlayerEntityActionState.Interacting,
            presentationId);

        return state.Version;
    }

    private void EndPersistentInteractionPresentation(ClientSession session)
    {
        if (session == null)
            return;

        if (_persistentInteractionPresentation.TryGetValue(
                session,
                out PersistentInteractionPresentation state))
        {
            state.PresentationId = InteractionPresentationWire.Stop;
            state.Version++;
            if (state.Version == 0)
                state.Version = 1;
        }

        if (!IsCurrent(session) || !session.Ready || session.Entity == null)
            return;

        // Explicit zero-id stop first. If this unreliable snapshot is lost, the persistent
        // state has already been cleared, so the next normal/heartbeat snapshot carries None
        // and the client also treats Interacting -> None as a stop.
        BroadcastSnapshot(
            session,
            session.Entity.SnapshotSpeed,
            session.Entity.SnapshotFlags,
            session.Entity.SnapshotMoveState,
            ServerPlayerSnapshotReason.PresentationState,
            (byte)PlayerEntityActionState.Interacting,
            InteractionPresentationWire.Stop);
    }

    private void EndPersistentInteractionPresentationIfVersion(
        ClientSession session,
        uint expectedVersion)
    {
        if (session == null || expectedVersion == 0 ||
            !_persistentInteractionPresentation.TryGetValue(
                session,
                out PersistentInteractionPresentation state) ||
            state.Version != expectedVersion)
        {
            return;
        }

        EndPersistentInteractionPresentation(session);
    }

    private bool TryExecuteInteractionTestDummy(
        ClientSession sourceSession,
        PlayerRuntime source,
        PlayerRuntime target,
        InteractionActionId actionId,
        uint sequence,
        out InteractionResult result)
    {
        result = default;
        if (!_options.CombatTestDummy || !IsCombatTestDummy(target))
            return false;

        var targetHandle =
            new InteractionTargetHandle(
                InteractionTargetKind.PlayerEntity,
                CombatTestDummyCharacterId);

        if (sourceSession?.Entity == null || source == null)
        {
            result = new InteractionResult(
                sequence,
                actionId,
                InteractionResultCode.InvalidState,
                targetHandle,
                "interaction test source is unavailable");
            return true;
        }

        CharacterLocationState sourceLocation =
            sourceSession.Entity.CaptureLocation();
        CharacterLocationState targetLocation =
            _combatTestDummyEntity != null
                ? _combatTestDummyEntity.CaptureLocation()
                : target.Location;

        if (!string.Equals(
                sourceLocation.MapId,
                targetLocation.MapId,
                StringComparison.Ordinal) ||
            !string.Equals(
                sourceLocation.InstanceId,
                targetLocation.InstanceId,
                StringComparison.Ordinal))
        {
            result = new InteractionResult(
                sequence,
                actionId,
                InteractionResultCode.InvalidTarget,
                targetHandle,
                "interaction test dummy is in another world partition");
            return true;
        }

        float dx = sourceLocation.Position.X - targetLocation.Position.X;
        float dz = sourceLocation.Position.Z - targetLocation.Position.Z;
        float range = (float)Math.Max(
            0.1d,
            _runtime.Interactions.PlayerInteractionRange);

        if (dx * dx + dz * dz > range * range)
        {
            result = new InteractionResult(
                sequence,
                actionId,
                InteractionResultCode.OutOfRange,
                targetHandle,
                $"interaction test dummy is outside {range:0.##}m range");
            return true;
        }

        byte initiatorPresentation =
            InteractionPresentationWire.EncodeInteraction(
                actionId,
                receiver: false);
        byte receiverPresentation =
            InteractionPresentationWire.EncodeInteraction(
                actionId,
                receiver: true);

        uint sourceVersion =
            BeginPersistentInteractionPresentation(
                sourceSession,
                initiatorPresentation);

        uint dummyVersion =
            ++_interactionTestDummyPresentationVersion;
        if (dummyVersion == 0)
            dummyVersion = ++_interactionTestDummyPresentationVersion;

        BroadcastInteractionTestDummyPresentation(receiverPresentation);

        // One sparse reinforcement makes the development dummy robust enough for visual
        // timing work without introducing a new reliable presentation message.
        _scheduler.Schedule(
            0.12d,
            () =>
            {
                if (_interactionTestDummyPresentationVersion == dummyVersion)
                    BroadcastInteractionTestDummyPresentation(receiverPresentation);
            });

        double duration =
            InteractionPresentationWire.DevelopmentDurationSeconds(actionId);

        _scheduler.Schedule(
            duration,
            () =>
            {
                EndPersistentInteractionPresentationIfVersion(
                    sourceSession,
                    sourceVersion);

                if (_interactionTestDummyPresentationVersion == dummyVersion)
                {
                    _interactionTestDummyPresentationVersion++;
                    if (_interactionTestDummyPresentationVersion == 0)
                        _interactionTestDummyPresentationVersion = 1;

                    uint stopVersion =
                        _interactionTestDummyPresentationVersion;

                    BroadcastInteractionTestDummyPresentation(
                        InteractionPresentationWire.Stop);

                    _scheduler.Schedule(
                        0.12d,
                        () =>
                        {
                            if (_interactionTestDummyPresentationVersion == stopVersion)
                            {
                                BroadcastInteractionTestDummyPresentation(
                                    InteractionPresentationWire.Stop);
                            }
                        });
                }
            });

        result = new InteractionResult(
            sequence,
            actionId,
            InteractionResultCode.Success,
            targetHandle,
            $"interaction test dummy accepted; visual hold {duration:0.##}s");
        return true;
    }

    private void BroadcastInteractionTestDummyPresentation(byte presentationId)
    {
        if (_combatTestDummyEntity == null ||
            _combatTestDummyRuntime == null)
        {
            return;
        }

        foreach (ClientSession observer in _readySessionsByCharacterId.Values)
        {
            if (!IsCurrent(observer) ||
                !observer.Ready ||
                observer.Entity == null ||
                !IsCombatTestDummyVisibleTo(observer))
            {
                continue;
            }

            QueueSnapshot(
                observer,
                _combatTestDummyEntity,
                _combatTestDummyEntity.SnapshotSpeed,
                _combatTestDummyEntity.SnapshotFlags,
                _combatTestDummyEntity.SnapshotMoveState,
                priority: true,
                actionState: (byte)PlayerEntityActionState.Interacting,
                actionId: presentationId);
        }
    }
}
