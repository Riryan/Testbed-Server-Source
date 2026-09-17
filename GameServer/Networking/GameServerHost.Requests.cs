using Game.GameServer.Runtime;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private const double ClientRequestRefillPerSecond = 20d;
    private const double ClientRequestBurstCapacity = 40d;
    private const double PingRefillPerSecond = 2d;
    private const double PingBurstCapacity = 4d;
    private const int ReliableRequestReplayWindow = 256;
    private const long ProtocolStrikeWindowMilliseconds = 10_000;
    private const int ProtocolStrikesBeforeDisconnect = 8;

    private readonly Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> _requestHandlers;

    private Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> BuildRequestHandlers()
    {
        // Force protocol-catalog initialization before accepting network traffic. Any
        // duplicate request id therefore fails server construction rather than becoming
        // order-dependent dispatch behavior.
        _ = PlayerRequestCatalog.All.Count;

        var handlers = new Dictionary<ushort, Action<ClientSession, uint, NetDataReader>>();
        RegisterCoreAndCharacterRequests(handlers);
        RegisterPlayerItemRequests(handlers);
        RegisterSocialEconomyRequests(handlers);
        RegisterWorldItemRequests(handlers);
        RegisterResourceStatusRequests(handlers);
        RegisterProgressionRequests(handlers);
        RegisterCraftingRequests(handlers);
        RegisterGameplaySettingsRequests(handlers);
        RegisterGameplayActionRequests(handlers);
        RegisterContextInteractionRequests(handlers);
        RegisterStaffRequests(handlers);

        foreach (PlayerRequestDescriptor descriptor in PlayerRequestCatalog.All)
        {
            if (!handlers.ContainsKey(descriptor.Id))
            {
                throw new InvalidOperationException(
                    $"Protocol catalog request {descriptor} has no registered server handler.");
            }
        }

        return handlers;
    }

    private static void RegisterRequest(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers,
        ushort requestId,
        Action<ClientSession, uint, NetDataReader> handler)
    {
        if (handlers == null)
            throw new ArgumentNullException(nameof(handlers));
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        PlayerRequestDescriptor descriptor = PlayerRequestCatalog.Get(requestId);
        if (!handlers.TryAdd(requestId, handler))
        {
            throw new InvalidOperationException(
                $"Duplicate request-handler registration for {descriptor}.");
        }
    }

    private bool TryAdmitReliableRequest(ClientSession session, uint requestId)
    {
        if (session == null || !session.Connected)
            return false;

        long nowMs = Environment.TickCount64;
        long elapsedMs = Math.Max(0L, nowMs - session.ClientRequestTokenTimestampMs);
        session.ClientRequestTokenTimestampMs = nowMs;
        session.ClientRequestTokens = Math.Min(
            ClientRequestBurstCapacity,
            session.ClientRequestTokens + elapsedMs * (ClientRequestRefillPerSecond / 1000d));

        if (session.ClientRequestTokens + 0.000001d < 1d)
        {
            SendBareResponse(session, requestId, AckError);
            return false;
        }
        session.ClientRequestTokens -= 1d;

        if (!session.HasReliableRequestId)
        {
            session.HasReliableRequestId = true;
            session.HighestReliableRequestId = requestId;
            session.RecentReliableRequestIds.Add(requestId);
            return true;
        }

        if (session.RecentReliableRequestIds.Contains(requestId))
        {
            RecordProtocolStrike(session, "duplicate reliable request id");
            SendBareResponse(session, requestId, AckError);
            return false;
        }

        uint highest = session.HighestReliableRequestId;
        if (IsNewerSequence(requestId, highest))
        {
            session.HighestReliableRequestId = requestId;
            session.RecentReliableRequestIds.Add(requestId);
            PruneReliableReplayWindow(session, requestId);
            return true;
        }

        int distanceBehind = unchecked((int)(highest - requestId));
        if (distanceBehind <= 0 || distanceBehind > ReliableRequestReplayWindow)
        {
            RecordProtocolStrike(session, "stale reliable request id");
            SendBareResponse(session, requestId, AckError);
            return false;
        }

        // ReliableUnordered permits valid requests to arrive out of order. Accept unseen
        // ids inside the bounded freshness window and remember them so later replays fail.
        session.RecentReliableRequestIds.Add(requestId);
        return true;
    }

    private static bool TryAdmitPing(ClientSession session)
    {
        if (session == null || !session.Connected)
            return false;

        long nowMs = Environment.TickCount64;
        long elapsedMs = Math.Max(0L, nowMs - session.PingTokenTimestampMs);
        session.PingTokenTimestampMs = nowMs;
        session.PingTokens = Math.Min(
            PingBurstCapacity,
            session.PingTokens + elapsedMs * (PingRefillPerSecond / 1000d));

        if (session.PingTokens + 0.000001d < 1d)
            return false;

        session.PingTokens -= 1d;
        return true;
    }

    private static void PruneReliableReplayWindow(ClientSession session, uint highest)
    {
        if (session.RecentReliableRequestIds.Count <= ReliableRequestReplayWindow + 1)
            return;

        session.ReliableRequestPruneScratch.Clear();
        foreach (uint candidate in session.RecentReliableRequestIds)
        {
            if (candidate == highest)
                continue;

            int distanceBehind = unchecked((int)(highest - candidate));
            if (distanceBehind > ReliableRequestReplayWindow)
                session.ReliableRequestPruneScratch.Add(candidate);
        }

        for (int i = 0; i < session.ReliableRequestPruneScratch.Count; ++i)
            session.RecentReliableRequestIds.Remove(session.ReliableRequestPruneScratch[i]);
        session.ReliableRequestPruneScratch.Clear();
    }

    private void RecordProtocolStrike(ClientSession session, string reason)
    {
        if (session == null || !session.Connected)
            return;

        long now = Environment.TickCount64;
        if (Math.Max(0L, now - session.ProtocolStrikeWindowStartMs) >= ProtocolStrikeWindowMilliseconds)
        {
            session.ProtocolStrikeWindowStartMs = now;
            session.ProtocolStrikeCount = 0;
        }

        session.ProtocolStrikeCount++;
        if (session.ProtocolStrikeCount < ProtocolStrikesBeforeDisconnect)
            return;

        Console.Error.WriteLine(
            $"Protocol strike disconnect: peer={session.Peer.Id}, strikes={session.ProtocolStrikeCount}, reason={reason}.");
        session.Peer.Disconnect();
    }
}
