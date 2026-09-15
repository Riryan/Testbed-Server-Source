using Game.GameServer.Runtime;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Content;
using Game.Shared.Protocol;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{

    private void RegisterResourceStatusRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, PlayerResourceRequestTypes.Snapshot,
            (session, requestId, _) => HandlePlayerResourcesSnapshot(session, requestId));
        RegisterRequest(handlers, PlayerStatusEffectRequestTypes.Snapshot,
            (session, requestId, _) => HandlePlayerStatusEffectsSnapshot(session, requestId));
    }
    private void HandlePlayerResourcesSnapshot(ClientSession session, uint requestId)
    {
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime) || !runtime.HasCharacterResources)
        {
            SendResponse(session, requestId, PlayerResourcesResponseMessage.Failed(
                (byte)CharacterResourceOperationStatus.CharacterUnavailable,
                "character resource state is unavailable"));
            return;
        }

        CharacterResourcesSnapshot snapshot = _runtime.Resources.GetSnapshot(runtime);
        if (snapshot == null)
        {
            SendResponse(session, requestId, PlayerResourcesResponseMessage.Failed(
                (byte)CharacterResourceOperationStatus.CharacterUnavailable,
                "character resource state is unavailable"));
            return;
        }

        SendResponse(session, requestId, ToWire(snapshot));
    }

    private void HandlePlayerStatusEffectsSnapshot(ClientSession session, uint requestId)
    {
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, PlayerStatusEffectsResponseMessage.Failed(
                (byte)StatusEffectOperationStatus.CharacterUnavailable,
                "character is not in world"));
            return;
        }

        StatusEffectsSnapshot snapshot = _runtime.StatusEffects.GetSnapshot(runtime);
        if (snapshot == null)
        {
            SendResponse(session, requestId, PlayerStatusEffectsResponseMessage.Failed(
                (byte)StatusEffectOperationStatus.CharacterUnavailable,
                "character status state is unavailable"));
            return;
        }

        SendResponse(session, requestId, ToWire(snapshot));
    }

    private void ActivatePlayerGameplayRuntime(ClientSession session, PlayerRuntime runtime)
    {
        if (session == null || runtime == null || session.GameplayRuntimeActive)
            return;

        session.ResourceChangedHandler = change => OnAuthoritativeResourceChanged(runtime, change);
        session.StatusEffectChangedHandler = change => OnAuthoritativeStatusEffectChanged(runtime, change);
        runtime.ResourceChanged += session.ResourceChangedHandler;
        runtime.StatusEffectChanged += session.StatusEffectChangedHandler;

        _resourceScheduler.Activate(runtime);
        _combatStateScheduler.Activate(runtime);
        _statusEffectScheduler.Activate(runtime);
        _abilityScheduler.Activate(runtime);
        session.GameplayRuntimeActive = true;
    }

    private void DeactivatePlayerGameplayRuntime(ClientSession session, PlayerRuntime runtime)
    {
        if (session == null || runtime == null || !session.GameplayRuntimeActive)
            return;

        _abilityScheduler.Deactivate(runtime);
        _statusEffectScheduler.Deactivate(runtime);
        _combatStateScheduler.Deactivate(runtime);
        _resourceScheduler.Deactivate(runtime);

        if (session.ResourceChangedHandler != null)
            runtime.ResourceChanged -= session.ResourceChangedHandler;
        if (session.StatusEffectChangedHandler != null)
            runtime.StatusEffectChanged -= session.StatusEffectChangedHandler;

        session.ResourceChangedHandler = null;
        session.StatusEffectChangedHandler = null;
        session.GameplayRuntimeActive = false;
    }

    private void OnAuthoritativeResourceChanged(PlayerRuntime runtime, CharacterResourceChange change)
    {
        if (runtime == null)
            return;

        // Resource mutations can change authoritative movement/death state. Wake only the
        // owning PlayerEntity rather than relying on a 20 Hz all-player polling pass.
        MarkPlayerSimulationDirty(FindReadySession(runtime));

        if (!_runtime.Resources.ShouldReplicateToOwner(change.ResourceId))
            return;

        var delta = new PlayerResourceDeltaMessage
        {
            resourceRevision = change.Revision,
            id = (ushort)change.ResourceId,
            previous = change.Previous,
            current = change.Current,
            minimum = change.Minimum,
            maximum = change.Maximum,
            reason = (byte)change.Reason,
        };

        _mainThreadCompletions.Enqueue(() =>
        {
            ClientSession owner = FindReadySession(runtime);
            if (owner == null)
                return;
            SendClientMessage(owner, PlayerResourceMessageTypes.Delta, delta, DeliveryMethod.ReliableOrdered);
        });
    }

    private void OnAuthoritativeStatusEffectChanged(PlayerRuntime runtime, StatusEffectChange change)
    {
        if (runtime == null)
            return;

        // Stun/root and future movement-affecting statuses must wake authority exactly when
        // state changes; dormant players otherwise remain off the Simulation cadence.
        MarkPlayerSimulationDirty(FindReadySession(runtime));

        ushort statusWireId = 0;
        ushort presentationId = 0;
        if (!string.IsNullOrWhiteSpace(change.DefinitionId) &&
            _runtime.Content.TryGetStatusEffect(change.DefinitionId, out StatusEffectDefinition definition))
        {
            statusWireId = definition.wireId;
            presentationId = definition.presentationId;
        }

        double remaining = change.EndTime > 0d
            ? Math.Max(0d, change.EndTime - _scheduler.ServerTime)
            : 0d;
        uint remainingMs = remaining >= uint.MaxValue / 1000d
            ? uint.MaxValue
            : (uint)Math.Round(remaining * 1000d, MidpointRounding.AwayFromZero);

        var delta = new PlayerStatusEffectDeltaMessage
        {
            statusRevision = change.Revision,
            kind = (byte)change.Kind,
            statusWireId = statusWireId,
            stacks = change.Stacks,
            remainingMilliseconds = remainingMs,
            reason = (byte)change.Reason,
        };

        _mainThreadCompletions.Enqueue(() =>
        {
            ClientSession owner = FindReadySession(runtime);
            if (owner == null)
                return;
            SendClientMessage(owner, PlayerStatusEffectMessageTypes.Delta, delta, DeliveryMethod.ReliableOrdered);
            QueueStatusPresentation(runtime, change, presentationId);
        });
    }

    private static PlayerResourcesResponseMessage ToWire(CharacterResourcesSnapshot snapshot)
    {
        CharacterResourceView[] source = snapshot.resources ?? Array.Empty<CharacterResourceView>();
        var resources = new CharacterResourceWire[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            resources[i] = new CharacterResourceWire
            {
                id = (ushort)source[i].id,
                displayName = source[i].displayName,
                current = source[i].current,
                minimum = source[i].minimum,
                maximum = source[i].maximum,
                replication = (byte)source[i].replication,
            };
        }

        return new PlayerResourcesResponseMessage
        {
            success = true,
            status = (byte)CharacterResourceOperationStatus.Success,
            error = string.Empty,
            contentRevision = snapshot.contentRevision,
            resourceRevision = snapshot.resourceRevision,
            resources = resources,
        };
    }

    private static PlayerStatusEffectsResponseMessage ToWire(StatusEffectsSnapshot snapshot)
    {
        StatusEffectView[] source = snapshot.effects ?? Array.Empty<StatusEffectView>();
        var effects = new StatusEffectWire[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            effects[i] = new StatusEffectWire
            {
                wireId = source[i].wireId,
                definitionId = source[i].definitionId,
                displayName = source[i].displayName,
                classification = source[i].classification,
                stacks = source[i].stacks,
                endTime = source[i].endTime,
                presentationId = source[i].presentationId,
            };
        }

        return new PlayerStatusEffectsResponseMessage
        {
            success = true,
            status = (byte)StatusEffectOperationStatus.Success,
            error = string.Empty,
            contentRevision = snapshot.contentRevision,
            statusRevision = snapshot.statusRevision,
            effects = effects,
        };
    }
}
