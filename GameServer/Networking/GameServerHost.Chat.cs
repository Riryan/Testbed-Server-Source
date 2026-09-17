using Game.GameServer.Runtime;
using Game.Server.Domain.Players;
using Game.Shared.Chat;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private sealed class ChatRateState
    {
        public double Tokens = ChatBurst;
        public double LastRefillTime;
        public double NextAllowedTime;
    }

    private const int ChatMaxCharacters = 200;
    private const double ChatMinimumIntervalSeconds = 0.35d;
    private const double ChatRefillPerSecond = 2d;
    private const double ChatBurst = 5d;

    private readonly Dictionary<int, ChatRateState> _chatRateStates = new Dictionary<int, ChatRateState>();
    private readonly List<ClientSession> _localChatRecipientScratch = new List<ClientSession>(32);

    private void HandlePlayerChatSubmit(ClientSession session, NetDataReader reader)
    {
        var submit = new PlayerChatSubmitMessage();
        submit.Deserialize(reader);

        if (!TryGetGameplayRuntime(session, out PlayerRuntime senderRuntime))
            return;

        string senderName = senderRuntime.Character?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(senderName))
            return;

        string text = (submit.message ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > ChatMaxCharacters)
        {
            SendSystemChat(session, $"Chat messages must be 1-{ChatMaxCharacters} characters.");
            return;
        }

        if (!TryConsumeChatRate(session.Peer.Id, _scheduler.ServerTime))
        {
            SendSystemChat(session, "You are sending messages too quickly.");
            return;
        }

        if (TryHandleSocialCommand(session, senderRuntime, text))
            return;

        switch (submit.Channel)
        {
            case ChatChannel.Local:
                BroadcastLocalChat(session, senderRuntime, senderName, text);
                break;
            case ChatChannel.Whisper:
                SendWhisperChat(session, senderName, submit.target, text);
                break;
            case ChatChannel.Party:
                SendPartyChat(session, senderRuntime, senderName, text);
                break;
            case ChatChannel.Guild:
                SendGuildChatAsync(session, senderRuntime, senderName, text).Forget();
                break;
            default:
                SendSystemChat(session, "Unsupported chat channel.");
                break;
        }
    }

    private void BroadcastLocalChat(
        ClientSession senderSession,
        PlayerRuntime senderRuntime,
        string senderName,
        string text)
    {
        if (senderSession?.Entity == null || senderRuntime == null)
            return;

        var delivery = new PlayerChatDeliveryMessage
        {
            channel = (byte)ChatChannel.Local,
            sender = senderName,
            target = string.Empty,
            message = text,
            serverUtcTicks = DateTime.UtcNow.Ticks,
        };

        // Reuse the canonical player AOI grid for authoritative proximity chat. This keeps
        // Local chat map/instance scoped and avoids a full connected-session scan without
        // creating a parallel communications observer index or a new network message.
        var location = senderRuntime.Location;
        _worldInterest.CollectSessionsNear(
            location.MapId,
            location.InstanceId,
            senderSession.Entity.X,
            senderSession.Entity.Z,
            Math.Max(1f, _options.AoiRange),
            _localChatRecipientScratch);

        for (int i = 0; i < _localChatRecipientScratch.Count; ++i)
        {
            ClientSession recipient = _localChatRecipientScratch[i];
            if (!TryGetGameplayRuntime(recipient, out _))
                continue;
            // Visibility suppresses the actor replication edge, not the player's ability
            // to communicate. Hidden/gameplay-hidden/staff-hidden players remain valid chat
            // participants and use the existing proximity recipient query.
            SendClientMessage(recipient, PlayerChatMessageTypes.Deliver, delivery, DeliveryMethod.ReliableOrdered);
        }
    }

    private void SendWhisperChat(ClientSession sender, string senderName, string targetName, string text)
    {
        string normalizedTarget = (targetName ?? string.Empty).Trim();
        if (normalizedTarget.Length == 0)
        {
            SendSystemChat(sender, "Whisper target is missing.");
            return;
        }

        ClientSession targetSession = null;
        string canonicalTargetName = string.Empty;
        foreach (ClientSession candidate in _sessions.Values)
        {
            if (!TryGetGameplayRuntime(candidate, out PlayerRuntime targetRuntime))
                continue;

            string candidateName = targetRuntime.Character?.Name ?? string.Empty;
            if (!string.Equals(candidateName, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                continue;

            targetSession = candidate;
            canonicalTargetName = candidateName;
            break;
        }

        if (targetSession == null)
        {
            SendSystemChat(sender, $"Player '{normalizedTarget}' is not online on this GameServer.");
            return;
        }

        var delivery = new PlayerChatDeliveryMessage
        {
            channel = (byte)ChatChannel.Whisper,
            sender = senderName,
            target = canonicalTargetName,
            message = text,
            serverUtcTicks = DateTime.UtcNow.Ticks,
        };

        SendClientMessage(targetSession, PlayerChatMessageTypes.Deliver, delivery, DeliveryMethod.ReliableOrdered);
        if (!ReferenceEquals(targetSession, sender))
            SendClientMessage(sender, PlayerChatMessageTypes.Deliver, delivery, DeliveryMethod.ReliableOrdered);
    }

    private void SendSystemChat(ClientSession recipient, string text)
    {
        if (!IsCurrent(recipient))
            return;

        SendClientMessage(
            recipient,
            PlayerChatMessageTypes.Deliver,
            new PlayerChatDeliveryMessage
            {
                channel = (byte)ChatChannel.System,
                sender = string.Empty,
                target = string.Empty,
                message = text ?? string.Empty,
                serverUtcTicks = DateTime.UtcNow.Ticks,
            },
            DeliveryMethod.ReliableOrdered);
    }

    private bool TryConsumeChatRate(int peerId, double now)
    {
        if (!_chatRateStates.TryGetValue(peerId, out ChatRateState state))
        {
            state = new ChatRateState
            {
                Tokens = ChatBurst,
                LastRefillTime = now,
                NextAllowedTime = 0d,
            };
            _chatRateStates.Add(peerId, state);
        }

        double elapsed = Math.Max(0d, now - state.LastRefillTime);
        state.LastRefillTime = now;
        state.Tokens = Math.Min(ChatBurst, state.Tokens + elapsed * ChatRefillPerSecond);

        if (now < state.NextAllowedTime || state.Tokens < 1d)
            return false;

        state.Tokens -= 1d;
        state.NextAllowedTime = now + ChatMinimumIntervalSeconds;
        return true;
    }

    private void RemovePlayerChatState(int peerId)
    {
        _chatRateStates.Remove(peerId);
        RemoveModerationCommandState(peerId);
    }

    private void ClearPlayerChatState()
    {
        _chatRateStates.Clear();
        ClearModerationCommandState();
    }
}
