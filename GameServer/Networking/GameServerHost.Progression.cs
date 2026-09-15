using System;
using System.Collections.Generic;
using Game.GameServer.Runtime;
using Game.Server.Application.Progression;
using Game.Server.Domain.Players;
using Game.Shared.Progression;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private void RegisterProgressionRequests(Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, ProgressionRequestTypes.Snapshot, HandleProgressionSnapshot);
    }

    private void HandleProgressionSnapshot(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new ProgressionSnapshotRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, ProgressionSnapshotMessage.Failed("character is not in world"));
            return;
        }
        SendResponse(session, requestId, BuildProgressionSnapshot(runtime));
    }

    private ProgressionSnapshotMessage BuildProgressionSnapshot(PlayerRuntime runtime)
    {
        CharacterProgressionState state = _runtime.Progression.Snapshot(runtime);
        ProgressTrackState[] tracks = state.tracks ?? Array.Empty<ProgressTrackState>();
        int visibleTrackCount = 0;
        for (int i = 0; i < tracks.Length; ++i)
            if (tracks[i] != null && tracks[i].value != 0) visibleTrackCount++;
        var trackWire = new ProgressTrackWire[visibleTrackCount];
        for (int i = 0, write = 0; i < tracks.Length; ++i)
        {
            if (tracks[i] == null || tracks[i].value == 0) continue;
            trackWire[write++] = new ProgressTrackWire { dataId = tracks[i].dataId, value = tracks[i].value };
        }
        ReputationState[] rep = state.reputation ?? Array.Empty<ReputationState>();
        var repWire = new ReputationWire[rep.Length];
        for (int i = 0; i < rep.Length; ++i)
            repWire[i] = new ReputationWire { factionDataId = rep[i].factionDataId, value = rep[i].value };
        HeatState[] heat = state.heat ?? Array.Empty<HeatState>();
        var heatWire = new HeatWire[heat.Length];
        for (int i = 0; i < heat.Length; ++i)
            heatWire[i] = new HeatWire { jurisdictionDataId = heat[i].jurisdictionDataId, value = heat[i].value, bounty = heat[i].bounty, evidence = heat[i].evidence };
        return new ProgressionSnapshotMessage
        {
            success = true,
            error = string.Empty,
            contentRevision = _runtime.Content.Revision,
            revision = state.revision,
            experience = state.experience,
            level = state.level,
            factionDataId = state.factionDataId,
            tracks = trackWire,
            reputation = repWire,
            heat = heatWire,
            knownRecipeDataIds = state.knownRecipeDataIds ?? Array.Empty<ushort>(),
        };
    }

    private void OnAuthoritativeProgressionChanged(PlayerRuntime runtime, ProgressionDelta delta)
    {
        var wire = new ProgressionDeltaMessage
        {
            revision = delta.Revision,
            kind = (byte)delta.Kind,
            dataId = delta.DataId,
            value = delta.Value,
            auxiliary = delta.Auxiliary,
            extra = delta.Extra,
        };
        _mainThreadCompletions.Enqueue(() =>
        {
            ClientSession owner = FindReadySession(runtime);
            if (owner != null)
                SendClientMessage(owner, ProgressionMessageTypes.Delta, wire, DeliveryMethod.ReliableOrdered);
        });
    }
}
