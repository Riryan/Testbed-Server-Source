using System;
using Game.GameServer.Runtime;
using Game.Server.Application.Social;
using Game.Server.Domain.Players;
using LiteNetLib;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private bool _partyStateBridgeSubscribed;

    private void BeginPartyStateReady(ClientSession session, PlayerRuntime runtime)
    {
        if (runtime == null || !IsCurrent(session) || !session.Ready)
            return;

        EnsurePartyStateBridge();
        SendPartyState(session, runtime.CharacterId.Value);
    }

    private void EnsurePartyStateBridge()
    {
        if (_partyStateBridgeSubscribed)
            return;

        PartySocial.StateChanged += OnPartyStateChanged;
        _partyStateBridgeSubscribed = true;
    }

    private void OnPartyStateChanged(long characterId)
    {
        if (characterId <= 0)
            return;

        ClientSession session = FindReadySessionByCharacterId(characterId);
        if (session == null || !IsCurrent(session) || !session.Ready || session.Entity?.Runtime == null)
            return;

        SendPartyState(session, characterId);
    }

    private void SendPartyState(ClientSession session, long ownerCharacterId)
    {
        if (session == null || ownerCharacterId <= 0 || !IsCurrent(session) || !session.Ready)
            return;

        SendClientMessage(
            session,
            PartyMessageTypes.State,
            BuildPartyState(ownerCharacterId),
            DeliveryMethod.ReliableOrdered);
    }

    private PartyStateMessage BuildPartyState(long ownerCharacterId)
    {
        PartyMemberWire[] members = Array.Empty<PartyMemberWire>();
        ulong partyId = 0;
        long leaderCharacterId = 0;

        if (PartySocial.TryGetSnapshot(ownerCharacterId, out PartySnapshot party) && party != null)
        {
            partyId = party.PartyId;
            leaderCharacterId = party.LeaderCharacterId;
            PartyMemberSnapshot[] source = party.Members ?? Array.Empty<PartyMemberSnapshot>();
            members = new PartyMemberWire[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                PartyMemberSnapshot member = source[i];
                members[i] = new PartyMemberWire
                {
                    characterId = member.CharacterId,
                    name = member.Name ?? string.Empty,
                    isLeader = member.IsLeader,
                };
            }
        }

        long pendingInviterCharacterId = 0;
        string pendingInviterName = string.Empty;
        byte pendingSeconds = 0;
        if (PartySocial.TryGetPendingInvite(ownerCharacterId, out PartyInviteSnapshot invite))
        {
            pendingInviterCharacterId = invite.InviterCharacterId;
            pendingInviterName = invite.InviterName ?? string.Empty;
            double remaining = Math.Ceiling((invite.ExpiresUtc - DateTime.UtcNow).TotalSeconds);
            pendingSeconds = (byte)Math.Clamp((int)remaining, 1, (int)PartyService.InviteLifetime.TotalSeconds);
        }

        return new PartyStateMessage
        {
            ownerCharacterId = ownerCharacterId,
            partyId = partyId,
            leaderCharacterId = leaderCharacterId,
            members = members,
            pendingInviterCharacterId = pendingInviterCharacterId,
            pendingInviterName = pendingInviterName,
            pendingInviteSecondsRemaining = pendingSeconds,
        };
    }
}
