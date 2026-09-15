using Game.GameServer.Runtime;
using Game.Server.Application.Staff;
using Game.Shared.Staff;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{

    private void RegisterStaffRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, StaffRequestTypes.Status,
            (session, requestId, _) => HandleStaffStatus(session, requestId));
        RegisterRequest(handlers, StaffRequestTypes.SetVisibility, HandleStaffSetVisibility);
        RegisterRequest(handlers, StaffRequestTypes.Spectate, HandleStaffSpectate);
        RegisterRequest(handlers, StaffRequestTypes.StopSpectate,
            (session, requestId, _) => HandleStaffStopSpectate(session, requestId));
    }
    private void HandleStaffStatus(ClientSession session, uint requestId)
    {
        SendResponse(session, requestId, BuildStaffStatus(session, ""));
    }

    private void HandleStaffSetVisibility(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new StaffSetVisibilityRequestMessage();
        request.Deserialize(reader);
        StaffVisibilityMode mode = (StaffVisibilityMode)request.mode;
        if (mode != StaffVisibilityMode.VisibleStaff && mode != StaffVisibilityMode.HiddenObserver)
        {
            SendResponse(session, requestId, StaffStatusResponseMessage.Failed("requested staff visibility mode is invalid"));
            return;
        }
        if (!_runtime.GameMasters.TrySetVisibility(session.AuthenticatedAccountId, mode, out string detail))
        {
            SendResponse(session, requestId, StaffStatusResponseMessage.Failed(detail));
            return;
        }

        ApplyInterestChanges(_worldInterest.ClearPrivilegedFollow(session));
        ApplyInterestChanges(_worldInterest.ReconcileVisibility(session));
        SendResponse(session, requestId, BuildStaffStatus(session, mode == StaffVisibilityMode.HiddenObserver ? "hidden observer enabled" : "visible staff mode enabled"));
    }

    private void HandleStaffSpectate(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new StaffSpectateRequestMessage();
        request.Deserialize(reader);
        if (!TryFindReadySessionByCharacterId(request.targetCharacterId, out ClientSession target))
        {
            SendResponse(session, requestId, StaffStatusResponseMessage.Failed("spectate target is not online/in-world"));
            return;
        }
        if (session.Entity == null || target.Entity == null ||
            !string.Equals(session.Entity.Runtime.Location.MapId, target.Entity.Runtime.Location.MapId, StringComparison.Ordinal) ||
            !string.Equals(session.Entity.Runtime.Location.InstanceId, target.Entity.Runtime.Location.InstanceId, StringComparison.Ordinal))
        {
            SendResponse(session, requestId, StaffStatusResponseMessage.Failed("same-map spectate is required while map transfer is deferred"));
            return;
        }
        if (!_runtime.GameMasters.TryStartSpectate(session.AuthenticatedAccountId, request.targetCharacterId, out string detail))
        {
            SendResponse(session, requestId, StaffStatusResponseMessage.Failed(detail));
            return;
        }

        ApplyInterestChanges(_worldInterest.ReconcileVisibility(session));
        ApplyInterestChanges(_worldInterest.SetPrivilegedFollow(session, target));
        SendResponse(session, requestId, BuildStaffStatus(session, "spectating"));
    }

    private void HandleStaffStopSpectate(ClientSession session, uint requestId)
    {
        if (!_runtime.GameMasters.StopSpectate(session.AuthenticatedAccountId, out string detail))
        {
            SendResponse(session, requestId, StaffStatusResponseMessage.Failed(detail));
            return;
        }
        ApplyInterestChanges(_worldInterest.ClearPrivilegedFollow(session));
        ApplyInterestChanges(_worldInterest.ReconcileVisibility(session));
        SendResponse(session, requestId, BuildStaffStatus(session, "spectate stopped; hidden observer retained"));
    }

    private StaffStatusResponseMessage BuildStaffStatus(ClientSession session, string detail)
    {
        if (session == null || session.AuthenticatedAccountId <= 0 ||
            !_runtime.GameMasters.TryGetSession(session.AuthenticatedAccountId, out StaffSessionState state) ||
            !state.IsAuthorized)
        {
            return StaffStatusResponseMessage.Failed("account has no server-authorized staff capabilities");
        }

        uint objectId = 0;
        ushort generation = 0;
        if (state.SpectateCharacterId > 0 && TryFindReadySessionByCharacterId(state.SpectateCharacterId, out ClientSession target) && target.Entity != null)
        {
            objectId = target.Entity.ObjectId;
            generation = target.Entity.Generation;
        }
        return new StaffStatusResponseMessage
        {
            success = true,
            detail = detail ?? string.Empty,
            roleName = state.RoleName,
            capabilities = (ulong)state.Capabilities,
            visibilityMode = (byte)state.VisibilityMode,
            spectateCharacterId = state.SpectateCharacterId,
            spectateObjectId = objectId,
            spectateGeneration = generation,
        };
    }

    private bool TryFindReadySessionByCharacterId(long characterId, out ClientSession result)
    {
        result = FindIndexedReadySessionByCharacterId(characterId);
        return result != null;
    }
}
