using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Runtime;
using Game.Server.Application.Social;
using Game.Server.Domain.Players;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private bool _guildStateBridgeSubscribed;

    private void RegisterGuildRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, GuildRequestTypes.Snapshot,
            (session, requestId, _) => HandleGuildSnapshot(session, requestId));
        RegisterRequest(handlers, GuildRequestTypes.Action, HandleGuildAction);
    }

    private void BeginGuildStateReady(ClientSession session, PlayerRuntime runtime)
    {
        if (runtime == null || !IsCurrent(session) || !session.Ready)
            return;

        EnsureGuildStateBridge();
        RunGuildStateAsync(session, 0, runtime, pushOnly: true).Forget();
    }

    private void EnsureGuildStateBridge()
    {
        if (_guildStateBridgeSubscribed)
            return;

        GuildSocial.StateChanged += OnGuildStateChanged;
        _guildStateBridgeSubscribed = true;
    }

    private void OnGuildStateChanged(long characterId)
    {
        if (characterId <= 0)
            return;

        QueueMainThreadCompletion(() =>
        {
            ClientSession session = FindReadySessionByCharacterId(characterId);
            PlayerRuntime runtime = session?.Entity?.Runtime;
            if (runtime == null || !IsCurrent(session) || !session.Ready)
                return;

            RunGuildStateAsync(session, 0, runtime, pushOnly: true).Forget();
        });
    }

    private void HandleGuildSnapshot(ClientSession session, uint requestId)
    {
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, new GuildStateMessage());
            return;
        }

        EnsureGuildStateBridge();
        RunGuildStateAsync(session, requestId, runtime, pushOnly: false).Forget();
    }

    private void HandleGuildAction(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new GuildActionRequestMessage();
        request.Deserialize(reader);

        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId,
                GuildMutationResponseMessage.Failed(1, "character is not in world"));
            return;
        }

        GuildActionKind action = request.Action;
        if (action != GuildActionKind.Decline && !IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId,
                GuildMutationResponseMessage.Failed(2, BackendPersistenceUnavailableMessage));
            return;
        }

        EnsureGuildStateBridge();
        RunGuildActionAsync(session, requestId, runtime, request).Forget();
    }

    private async Task RunGuildActionAsync(
        ClientSession session,
        uint requestId,
        PlayerRuntime runtime,
        GuildActionRequestMessage request)
    {
        GuildOperationResult result;
        try
        {
            switch (request.Action)
            {
                case GuildActionKind.Create:
                    result = await GuildSocial.CreateAsync(
                        runtime, request.name, CancellationToken.None).ConfigureAwait(false);
                    break;

                case GuildActionKind.Accept:
                    result = await GuildSocial.AcceptAsync(
                        runtime, CancellationToken.None).ConfigureAwait(false);
                    break;

                case GuildActionKind.Decline:
                    result = GuildSocial.Decline(runtime);
                    break;

                case GuildActionKind.Leave:
                    result = await GuildSocial.LeaveAsync(
                        runtime, CancellationToken.None).ConfigureAwait(false);
                    break;

                case GuildActionKind.Kick:
                    result = await GuildSocial.KickAsync(
                        runtime, request.targetCharacterId, CancellationToken.None).ConfigureAwait(false);
                    break;

                case GuildActionKind.Disband:
                    result = await GuildSocial.DisbandAsync(
                        runtime, CancellationToken.None).ConfigureAwait(false);
                    break;

                default:
                    result = GuildOperationResult.Fail("guild action is invalid");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Guild action failed for character {runtime?.CharacterId.Value ?? 0}: {ex.Message}");
            result = GuildOperationResult.Fail("guild service is temporarily unavailable");
        }

        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(session))
                return;

            SendResponse(
                session,
                requestId,
                result.Success
                    ? GuildMutationResponseMessage.Ok(result.Message)
                    : GuildMutationResponseMessage.Failed(3, result.Message));

            // Failure can still consume/prune an expired invite. Reconcile the owner from
            // authoritative state without introducing a second mutation/result channel.
            if (!result.Success && session.Ready && session.Entity?.Runtime != null)
                RunGuildStateAsync(session, 0, session.Entity.Runtime, pushOnly: true).Forget();
        });
    }

    private async Task RunGuildStateAsync(
        ClientSession session,
        uint requestId,
        PlayerRuntime runtime,
        bool pushOnly)
    {
        GuildSnapshot guild = null;
        try
        {
            guild = await GuildSocial.GetGuildAsync(
                runtime.CharacterId.Value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Guild state load failed for character {runtime.CharacterId.Value}: {ex.Message}");
        }

        GuildStateMessage state = BuildGuildState(runtime.CharacterId.Value, guild);
        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(session))
                return;

            if (pushOnly)
                SendClientMessage(session, GuildMessageTypes.State, state, DeliveryMethod.ReliableOrdered);
            else
                SendResponse(session, requestId, state);
        });
    }

    private GuildStateMessage BuildGuildState(long ownerCharacterId, GuildSnapshot guild)
    {
        GuildMemberSnapshot[] source = guild?.Members ?? Array.Empty<GuildMemberSnapshot>();
        var members = new GuildMemberWire[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            GuildMemberSnapshot member = source[i];
            members[i] = new GuildMemberWire
            {
                characterId = member?.CharacterId ?? 0,
                name = member?.Name ?? string.Empty,
                role = (byte)(member?.Role ?? GuildMemberRole.Member),
            };
        }

        long pendingInviterCharacterId = 0;
        string pendingInviterName = string.Empty;
        long pendingGuildId = 0;
        string pendingGuildName = string.Empty;
        byte pendingSeconds = 0;

        if (GuildSocial.TryGetPendingInvite(ownerCharacterId, out GuildInviteSnapshot invite))
        {
            pendingInviterCharacterId = invite.InviterCharacterId;
            pendingInviterName = invite.InviterName;
            pendingGuildId = invite.GuildId;
            pendingGuildName = invite.GuildName;
            double remaining = Math.Ceiling((invite.ExpiresUtc - DateTime.UtcNow).TotalSeconds);
            pendingSeconds = (byte)Math.Clamp(
                (int)remaining, 1, (int)GuildService.InviteLifetime.TotalSeconds);
        }

        return new GuildStateMessage
        {
            ownerCharacterId = ownerCharacterId,
            guildId = guild?.GuildId ?? 0,
            revision = guild?.Revision ?? 0,
            guildName = guild?.Name ?? string.Empty,
            members = members,
            pendingInviterCharacterId = pendingInviterCharacterId,
            pendingInviterName = pendingInviterName,
            pendingGuildId = pendingGuildId,
            pendingGuildName = pendingGuildName,
            pendingInviteSecondsRemaining = pendingSeconds,
        };
    }
}
