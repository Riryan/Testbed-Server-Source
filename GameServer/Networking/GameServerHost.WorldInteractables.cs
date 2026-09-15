using System;
using Game.GameServer.Runtime;
using Game.Server.Application.Interactions;
using Game.Server.Domain.Characters;
using Game.Shared.World;
using LiteNetLib;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private void OnAuthoritativeWorldInteractableChanged(WorldInteractableRuntime runtime)
    {
        if (runtime == null)
            return;

        _mainThreadCompletions.Enqueue(() => BroadcastWorldInteractableState(runtime));
    }

    private void BroadcastWorldInteractableState(WorldInteractableRuntime runtime)
    {
        if (runtime == null)
            return;

        var message = ToWorldInteractableStateMessage(runtime);
        float radius = Math.Max(8f, _options.AoiRange + _options.AoiExitPadding);
        double radiusSq = radius * radius;

        for (int i = 0; i < _readySimulationSessions.Count; ++i)
        {
            ClientSession session = _readySimulationSessions[i];
            if (!IsCurrent(session) || !session.Ready || session.Entity == null)
                continue;

            CharacterLocationState location = session.Entity.Runtime.Location;
            if (!string.Equals(location.MapId, runtime.Key.MapId, StringComparison.Ordinal) ||
                !string.Equals(location.InstanceId, runtime.Key.InstanceId, StringComparison.Ordinal))
                continue;

            double dx = location.Position.X - runtime.Definition.pose.x;
            double dz = location.Position.Z - runtime.Definition.pose.z;
            if (dx * dx + dz * dz > radiusSq)
                continue;

            SendClientMessage(
                session,
                PlayerGameplayActionMessageTypes.WorldInteractableState,
                message,
                DeliveryMethod.ReliableOrdered);
        }
    }

    private void SendChangedWorldInteractableStatesAfterReady(ClientSession session)
    {
        if (!IsCurrent(session) || !session.Ready || session.Entity == null)
            return;

        CharacterLocationState location = session.Entity.Runtime.Location;
        WorldInteractableRuntime[] states = _runtime.WorldInteractables.SnapshotStates(
            location.MapId,
            location.InstanceId,
            changedOnly: true);

        for (int i = 0; i < states.Length; ++i)
        {
            SendClientMessage(
                session,
                PlayerGameplayActionMessageTypes.WorldInteractableState,
                ToWorldInteractableStateMessage(states[i]),
                DeliveryMethod.ReliableOrdered);
        }
    }

    private static WorldInteractableStateMessage ToWorldInteractableStateMessage(
        WorldInteractableRuntime runtime) =>
        new WorldInteractableStateMessage
        {
            mapId = runtime.Key.MapId,
            instanceId = runtime.Key.InstanceId,
            stableId = runtime.Key.StableId,
            revision = runtime.Revision,
            kind = (byte)runtime.Definition.kind,
            enabled = runtime.Enabled,
            open = runtime.Open,
            depleted = runtime.Depleted,
            dynamicBlockerId = runtime.Definition.dynamicBlockerId,
            blockerEnabled = runtime.Definition.dynamicBlockerId > 0 && !runtime.Open,
        };
}
