using Game.GameServer.Runtime;
using Game.Server.Application.Staff;
using Game.Server.Domain.Players;
using Game.Shared.Staff;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private const double PlayerReportCooldownSeconds = 30d;
    private readonly Dictionary<int, double> _nextPlayerReportAtByPeer = new Dictionary<int, double>();

    /// <summary>
    /// Text-command bridge for parity features that can already be expressed through
    /// existing chat, staff authorization, AOI visibility and audit infrastructure.
    /// No new wire message is introduced here.
    /// </summary>
    private bool TryHandleModerationCommand(ClientSession session, PlayerRuntime actor, string text)
    {
        if (StartsWithCommand(text, "/hide", out string hideArgs))
        {
            // /hide is reserved for server-authorized staff. Ordinary player Hide is a
            // gameplay state/action and is never granted by a chat command.
            HandleStaffHideCommand(session, hideArgs);
            return true;
        }
        if (StartsWithCommand(text, "/staff", out string staffArgs))
        {
            HandleStaffVisibilityChatCommand(session, staffArgs);
            return true;
        }
        if (StartsWithCommand(text, "/report", out string reportArgs))
        {
            HandlePlayerReportCommand(session, actor, reportArgs);
            return true;
        }
        return false;
    }

    private void HandleStaffHideCommand(ClientSession session, string args)
    {
        if (session == null ||
            !_runtime.GameMasters.TryGetSession(session.AuthenticatedAccountId, out StaffSessionState state) ||
            !state.IsAuthorized)
        {
            SendSystemChat(session, "The /hide command is restricted to server-authorized staff. Player Hide is gameplay-controlled.");
            return;
        }

        string option = (args ?? string.Empty).Trim();
        if (option.Length == 0 || string.Equals(option, "status", StringComparison.OrdinalIgnoreCase))
        {
            bool hidden = state.VisibilityMode == StaffVisibilityMode.HiddenObserver ||
                          state.VisibilityMode == StaffVisibilityMode.Spectating;
            SendSystemChat(session, hidden
                ? "Staff hide: ON. Commands: /hide on, /hide off."
                : "Staff hide: OFF. Commands: /hide on, /hide off.");
            return;
        }

        StaffVisibilityMode mode;
        if (string.Equals(option, "on", StringComparison.OrdinalIgnoreCase))
            mode = StaffVisibilityMode.HiddenObserver;
        else if (string.Equals(option, "off", StringComparison.OrdinalIgnoreCase))
            mode = StaffVisibilityMode.VisibleStaff;
        else
        {
            SendSystemChat(session, "Staff hide: /hide on, /hide off, /hide status.");
            return;
        }

        if (!_runtime.GameMasters.TrySetVisibility(session.AuthenticatedAccountId, mode, out string detail))
        {
            SendSystemChat(session, $"Staff hide rejected: {detail}.");
            return;
        }

        ApplyInterestChanges(_worldInterest.ClearPrivilegedFollow(session));
        ApplyInterestChanges(_worldInterest.ReconcileVisibility(session));
        PublishStaffVisibilityPresentation(session);
        SendSystemChat(session, mode == StaffVisibilityMode.HiddenObserver
            ? "Staff hide enabled. Ordinary players cannot observe your actor; authorized staff still can."
            : "Staff hide disabled. Your actor is visible normally.");
    }

    private void HandleStaffVisibilityChatCommand(ClientSession session, string args)
    {
        if (session == null ||
            !_runtime.GameMasters.TryGetSession(session.AuthenticatedAccountId, out StaffSessionState state) ||
            !state.IsAuthorized)
        {
            SendSystemChat(session, "This account has no server-authorized staff access.");
            return;
        }

        string prepared = (args ?? string.Empty).Trim();
        string command;
        string remainder;
        SplitCommand(prepared, out command, out remainder);

        if (command.Length == 0 || string.Equals(command, "status", StringComparison.OrdinalIgnoreCase))
        {
            SendSystemChat(session, $"Staff visibility: {state.VisibilityMode}. Role: {state.RoleName}. Canonical hide commands: /hide on, /hide off.");
            return;
        }

        bool? hidden = null;
        if (string.Equals(command, "hide", StringComparison.OrdinalIgnoreCase))
        {
            string option = (remainder ?? string.Empty).Trim();
            if (string.Equals(option, "on", StringComparison.OrdinalIgnoreCase))
                hidden = true;
            else if (string.Equals(option, "off", StringComparison.OrdinalIgnoreCase))
                hidden = false;
            else
            {
                SendSystemChat(session, "Staff visibility: /hide on, /hide off, /staff status.");
                return;
            }
        }
        else if (string.Equals(command, "visible", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(command, "show", StringComparison.OrdinalIgnoreCase))
        {
            // Preserve the existing alias for staff tools/scripts while making hide on/off
            // the canonical player-facing command form.
            hidden = false;
        }
        else
        {
            SendSystemChat(session, "Staff visibility: /hide on, /hide off, /staff status.");
            return;
        }

        StaffVisibilityMode mode = hidden.Value
            ? StaffVisibilityMode.HiddenObserver
            : StaffVisibilityMode.VisibleStaff;

        if (!_runtime.GameMasters.TrySetVisibility(session.AuthenticatedAccountId, mode, out string detail))
        {
            SendSystemChat(session, $"Staff visibility rejected: {detail}.");
            return;
        }

        ApplyInterestChanges(_worldInterest.ClearPrivilegedFollow(session));
        ApplyInterestChanges(_worldInterest.ReconcileVisibility(session));
        PublishStaffVisibilityPresentation(session);

        SendSystemChat(session, hidden.Value
            ? "Staff hide enabled. Ordinary players cannot observe your actor; authorized staff still can."
            : "Staff hide disabled. Your actor is visible normally.");
    }

    private void PublishStaffVisibilityPresentation(ClientSession session)
    {
        if (session == null || !IsCurrent(session) || !session.Ready || session.Entity == null)
            return;

        // PlayerEntityFlags.Hidden already exists in the current snapshot contract. The
        // replication layer adds it for hidden staff/gameplay-hidden players, so one forced
        // presentation snapshot is enough to update the owner's/staff observers' grey cue.
        BroadcastSnapshot(
            session,
            session.Entity.SnapshotSpeed,
            session.Entity.SnapshotFlags,
            session.Entity.SnapshotMoveState,
            ServerPlayerSnapshotReason.Forced | ServerPlayerSnapshotReason.PresentationState);
    }

    private void HandlePlayerReportCommand(ClientSession session, PlayerRuntime actor, string args)
    {
        if (session == null || actor == null)
            return;
        if (!TrySplitReportArguments(args, out string targetName, out string reason))
        {
            SendSystemChat(session, "Report: /report \"Player Name\" reason (reason must be 5-500 characters).");
            return;
        }
        if (_nextPlayerReportAtByPeer.TryGetValue(session.Peer.Id, out double nextAllowed) &&
            _scheduler.ServerTime < nextAllowed)
        {
            SendSystemChat(session, "Please wait before submitting another player report.");
            return;
        }
        if (!TryFindOnlinePlayerByName(targetName, out _, out PlayerRuntime target))
        {
            SendSystemChat(session, $"Player '{targetName}' is not online on this GameServer.");
            return;
        }
        if (!_runtime.GameMasters.TryWritePlayerReport(
                session.AuthenticatedAccountId,
                actor.CharacterId.Value,
                target.CharacterId.Value,
                reason,
                out string detail))
        {
            SendSystemChat(session, $"Report rejected: {detail}.");
            return;
        }

        _nextPlayerReportAtByPeer[session.Peer.Id] = _scheduler.ServerTime + PlayerReportCooldownSeconds;
        SendSystemChat(session, $"Report submitted for {target.Character?.Name ?? targetName}.");
    }

    private static bool TrySplitReportArguments(string args, out string targetName, out string reason)
    {
        targetName = string.Empty;
        reason = string.Empty;
        string prepared = (args ?? string.Empty).Trim();
        if (prepared.Length == 0)
            return false;

        if (prepared[0] == '"')
        {
            int close = prepared.IndexOf('"', 1);
            if (close <= 1)
                return false;
            targetName = prepared.Substring(1, close - 1).Trim();
            reason = prepared.Substring(close + 1).Trim();
        }
        else
        {
            int split = prepared.IndexOf(' ');
            if (split <= 0)
                return false;
            targetName = prepared.Substring(0, split).Trim();
            reason = prepared.Substring(split + 1).Trim();
        }

        return targetName.Length > 0 && reason.Length >= 5 && reason.Length <= 500;
    }

    private void RemoveModerationCommandState(int peerId) => _nextPlayerReportAtByPeer.Remove(peerId);
    private void ClearModerationCommandState() => _nextPlayerReportAtByPeer.Clear();

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
        PublishStaffVisibilityPresentation(session);
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
