using System.Collections.Generic;
using Game.Shared.Accounts;
using Game.Server.Application.Sessions;
using Game.Server.Domain.Resources;
using Game.Server.Domain.StatusEffects;
using LiteNetLib;

namespace Game.GameServer.Runtime;

internal sealed class ClientSession
{
    public NetPeer Peer { get; }
    public PlayerSessionHandle SessionHandle { get; }
    public bool Connected { get; set; } = true;
    public int AdmissionAttempts { get; set; }
    public DateTime AuthenticationDeadlineUtc { get; }
    public bool AuthenticationInFlight { get; set; }
    public long AuthenticatedAccountId { get; set; }
    public AccountPolicySnapshot AccountPolicy { get; set; }
    public bool CharacterCreateInFlight { get; set; }
    public bool CharacterDeleteInFlight { get; set; }
    public bool CharacterLoadInFlight { get; set; }
    public bool ReadyInFlight { get; set; }
    public bool Ready { get; set; }
    public ServerPlayerEntity Entity { get; set; }
    public int SimulationListIndex { get; set; } = -1;
    public bool SimulationDirty { get; set; }
    public int SimulationActiveIndex { get; set; } = -1;

    public long GameplaySettingsValidatedRevision { get; set; }
    public long PlayerItemsValidatedContentRevision { get; set; }
    public long PlayerItemsValidatedInventoryRevision { get; set; }
    public long PlayerItemsValidatedEquipmentRevision { get; set; }
    public long ProgressionValidatedContentRevision { get; set; }
    public long ProgressionValidatedRevision { get; set; }

    // Transport-only proofs of non-authoritative durable owner caches.
    public long FriendsKnownRevision { get; set; }
    public long GuildKnownRevision { get; set; }

    public byte PresentationActionId { get; set; }

    public bool GameplayRuntimeActive { get; set; }
    public Action<CharacterResourceChange> ResourceChangedHandler { get; set; }
    public Action<StatusEffectChange> StatusEffectChangedHandler { get; set; }

    public uint LastContextInteractionSequence { get; set; }
    public uint LastWorldItemInteractionSequence { get; set; }
    public double PlayerInteractionTokens { get; set; } = 6d;
    public long PlayerInteractionTokenTimestampMs { get; set; } = Environment.TickCount64;

    public uint LastCombatActionSequence { get; set; }
    public double CombatRequestTokens { get; set; } = 8d;
    public long CombatRequestTokenTimestampMs { get; set; } = Environment.TickCount64;

    public double ClientRequestTokens { get; set; } = 40d;
    public long ClientRequestTokenTimestampMs { get; set; } = Environment.TickCount64;

    public double PingTokens { get; set; } = 4d;
    public long PingTokenTimestampMs { get; set; } = Environment.TickCount64;

    public bool HasReliableRequestId { get; set; }
    public uint HighestReliableRequestId { get; set; }
    public HashSet<uint> RecentReliableRequestIds { get; } = new HashSet<uint>();
    public List<uint> ReliableRequestPruneScratch { get; } = new List<uint>(32);

    public int ProtocolStrikeCount { get; set; }
    public long ProtocolStrikeWindowStartMs { get; set; } = Environment.TickCount64;

    public int MalformedPacketLogCount { get; set; }
    public int MalformedPacketSuppressedCount { get; set; }
    public long MalformedPacketLogWindowStartMs { get; set; } = Environment.TickCount64;

    public ClientSession(NetPeer peer, PlayerSessionHandle sessionHandle, int authenticationTimeoutSeconds)
    {
        Peer = peer ?? throw new ArgumentNullException(nameof(peer));
        SessionHandle = sessionHandle;
        AuthenticationDeadlineUtc = DateTime.UtcNow.AddSeconds(authenticationTimeoutSeconds);
    }
}
