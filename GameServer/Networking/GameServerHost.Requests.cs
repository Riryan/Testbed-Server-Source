using Game.GameServer.Runtime;
using Game.Server.Application.Sessions;
using Game.Server.Domain.Players;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
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
        RegisterGuildRequests(handlers);
        RegisterWorldItemRequests(handlers);
        RegisterResourceStatusRequests(handlers);
        RegisterProgressionRequests(handlers);
        RegisterCraftingRequests(handlers);
        RegisterGameplaySettingsRequests(handlers);
        WrapGameplaySettingsCacheProbe(handlers);
        WrapOwnerStateCacheProbes(handlers);
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

    private void WrapGameplaySettingsCacheProbe(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        if (!handlers.TryGetValue(
                GameplaySettingsRequestTypes.Snapshot,
                out Action<ClientSession, uint, NetDataReader> normalHandler))
        {
            return;
        }

        handlers[GameplaySettingsRequestTypes.Snapshot] = (session, requestId, reader) =>
        {
            // Request 450 used to have an empty body. New clients append only their cached
            // content revision. Old clients therefore fall straight through to the canonical
            // handler unchanged, while new clients can validate a persistent public catalog.
            if (reader == null || reader.AvailableBytes < sizeof(long))
            {
                normalHandler(session, requestId, reader);
                return;
            }

            long knownRevision = reader.GetLong();
            long currentRevision = _runtime.Content.Revision;
            if (knownRevision > 0 && knownRevision == currentRevision)
            {
                // Only a successful revision comparison may suppress the Ready Settings
                // baseline. If content changes between this probe and Ready, the revision
                // mismatch in SendOwnerBaselinesAfterReady restores the full snapshot.
                session.GameplaySettingsValidatedRevision = currentRevision;
                SendResponse(
                    session,
                    requestId,
                    GameplaySettingsSnapshotMessage.NotModified(currentRevision));
                return;
            }

            // Before Ready a stale cache must fall back to the established authoritative
            // Settings-first owner baseline rather than receiving the full catalog twice.
            if (!session.Ready || session.Entity == null)
            {
                SendResponse(
                    session,
                    requestId,
                    GameplaySettingsSnapshotMessage.Failed("gameplay settings cache is stale"));
                return;
            }

            // In-world reconciliation still receives the existing full authoritative snapshot.
            SendResponse(session, requestId, BuildGameplaySettingsSnapshot());
        };
    }

    private void WrapOwnerStateCacheProbes(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        WrapPlayerItemsCacheProbe(handlers);
        WrapProgressionCacheProbe(handlers);
        WrapFriendsCacheProbe(handlers);
    }

    private void WrapPlayerItemsCacheProbe(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        if (!handlers.TryGetValue(
                PlayerItemRequestTypes.Snapshot,
                out Action<ClientSession, uint, NetDataReader> normalHandler))
            return;

        handlers[PlayerItemRequestTypes.Snapshot] = (session, requestId, reader) =>
        {
            if (session.Ready || reader == null || reader.AvailableBytes < sizeof(long) * 3)
            {
                normalHandler(session, requestId, reader);
                return;
            }

            var request = new PlayerItemsSnapshotRequestMessage();
            request.Deserialize(reader);
            if (!TryGetLoadedRuntimeForCacheProbe(session, out PlayerRuntime runtime) ||
                !runtime.HasPlayerItemSystems)
            {
                SendResponse(
                    session,
                    requestId,
                    PlayerItemsResponseMessage.Failed(
                        (byte)PlayerItemOperationStatus.CharacterUnavailable,
                        "player item cache cannot be validated before character load"));
                return;
            }

            var snapshot = _runtime.PlayerItems.GetSnapshot(runtime);
            bool matches =
                snapshot != null &&
                request.knownContentRevision > 0 &&
                request.knownContentRevision == snapshot.contentRevision &&
                request.knownInventoryRevision == snapshot.inventoryRevision &&
                request.knownEquipmentRevision == snapshot.equipmentRevision;

            if (!matches)
            {
                SendResponse(
                    session,
                    requestId,
                    PlayerItemsResponseMessage.Failed(
                        (byte)PlayerItemOperationStatus.PersistenceRejected,
                        "player item cache is stale"));
                return;
            }

            session.PlayerItemsValidatedContentRevision = snapshot.contentRevision;
            session.PlayerItemsValidatedInventoryRevision = snapshot.inventoryRevision;
            session.PlayerItemsValidatedEquipmentRevision = snapshot.equipmentRevision;
            SendResponse(
                session,
                requestId,
                PlayerItemsResponseMessage.NotModified(
                    snapshot.contentRevision,
                    snapshot.inventoryRevision,
                    snapshot.equipmentRevision));
        };
    }

    private void WrapProgressionCacheProbe(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        if (!handlers.TryGetValue(
                ProgressionRequestTypes.Snapshot,
                out Action<ClientSession, uint, NetDataReader> normalHandler))
            return;

        handlers[ProgressionRequestTypes.Snapshot] = (session, requestId, reader) =>
        {
            if (session.Ready || reader == null || reader.AvailableBytes < sizeof(long) * 2)
            {
                normalHandler(session, requestId, reader);
                return;
            }

            var request = new ProgressionSnapshotRequestMessage();
            request.Deserialize(reader);
            if (!TryGetLoadedRuntimeForCacheProbe(session, out PlayerRuntime runtime))
            {
                SendResponse(
                    session,
                    requestId,
                    ProgressionSnapshotMessage.Failed(
                        "progression cache cannot be validated before character load"));
                return;
            }

            ProgressionSnapshotMessage snapshot = BuildProgressionSnapshot(runtime);
            bool matches =
                snapshot.success &&
                request.knownContentRevision > 0 &&
                request.knownContentRevision == snapshot.contentRevision &&
                request.knownRevision == snapshot.revision;

            if (!matches)
            {
                SendResponse(
                    session,
                    requestId,
                    ProgressionSnapshotMessage.Failed("progression cache is stale"));
                return;
            }

            session.ProgressionValidatedContentRevision = snapshot.contentRevision;
            session.ProgressionValidatedRevision = snapshot.revision;
            SendResponse(
                session,
                requestId,
                ProgressionSnapshotMessage.NotModified(
                    snapshot.contentRevision,
                    snapshot.revision));
        };
    }

    private void WrapFriendsCacheProbe(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        if (!handlers.TryGetValue(
                FriendRequestTypes.Snapshot,
                out Action<ClientSession, uint, NetDataReader> normalHandler))
            return;

        handlers[FriendRequestTypes.Snapshot] = (session, requestId, reader) =>
        {
            if (session.Ready || reader == null || reader.AvailableBytes < sizeof(long))
            {
                normalHandler(session, requestId, reader);
                return;
            }

            var request = new EmptySocialRequestMessage();
            request.Deserialize(reader);
            session.FriendsKnownRevision = request.knownRevision;
            SendResponse(session, requestId, new FriendsStateMessage());
        };
    }

    private bool TryGetLoadedRuntimeForCacheProbe(
        ClientSession session,
        out PlayerRuntime runtime)
    {
        runtime = null;
        if (!IsCurrent(session) ||
            !TryGetAuthoritativeSession(session, out PlayerSession authoritative) ||
            authoritative.Runtime == null)
            return false;

        if (authoritative.State != PlayerSessionState.AwaitingWorldEntry &&
            authoritative.State != PlayerSessionState.InWorld)
            return false;

        runtime = authoritative.Runtime;
        return true;
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
