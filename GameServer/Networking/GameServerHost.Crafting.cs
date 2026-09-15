using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Runtime;
using Game.Server.Application.Crafting;
using Game.Server.Domain.Players;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private void RegisterCraftingRequests(Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, CraftingRequestTypes.Craft, HandleCraftRequest);
    }

    private void HandleCraftRequest(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new CraftRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, new CraftResponseMessage { success = false, error = "character is not in world" });
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, new CraftResponseMessage { success = false, error = BackendPersistenceUnavailableMessage });
            return;
        }

        RunCraftAsync(session, requestId, runtime, request.stationStableId, request.recipeDataId).Forget();
    }

    private async Task RunCraftAsync(
        ClientSession session,
        uint requestId,
        PlayerRuntime runtime,
        long stationStableId,
        ushort recipeDataId)
    {
        CraftingResult result;
        try
        {
            result = await _runtime.Crafting.CraftAsync(runtime, stationStableId, recipeDataId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Craft operation failed for peer {session?.Peer?.Id}: {ex.Message}");
            result = new CraftingResult(false, "craft operation failed", null);
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session)) return;
            SendResponse(session, requestId, new CraftResponseMessage { success = result.Success, error = result.Error });
        });
    }
}
