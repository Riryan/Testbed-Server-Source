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
    // Authoritative account-level entitlement/moderation snapshot captured at admission.
    // GameServer uses this for staff authority and future account-gated gameplay.
    public AccountPolicySnapshot AccountPolicy { get; set; }
    public bool CharacterCreateInFlight { get; set; }
    public bool CharacterDeleteInFlight { get; set; }
    public bool CharacterLoadInFlight { get; set; }
    public bool ReadyInFlight { get; set; }
    public bool Ready { get; set; }
    public ServerPlayerEntity Entity { get; set; }
    public int SimulationListIndex { get; set; } = -1;
    // Event-driven authoritative simulation wake bit. Dormant grounded players are not
    // re-simulated at full movement Hz; movement/state events set this bit instead.
    public bool SimulationDirty { get; set; }
    public int SimulationActiveIndex { get; set; } = -1;

    // Per-connection proof that this client already owns the current immutable/public
    // Gameplay Settings catalog. This is transport cache state only; it is never gameplay
    // authority and is discarded with the connection.
    public long GameplaySettingsValidatedRevision { get; set; }

    // Per-connection proofs for durable owner-visible caches. These suppress only the
    // corresponding Ready baseline after an exact authoritative revision comparison.
    public long PlayerItemsValidatedContentRevision { get; set; }
    public long PlayerItemsValidatedInventoryRevision { get; set; }
    public long PlayerItemsValidatedEquipmentRevision { get; set; }
    public long ProgressionValidatedContentRevision { get; set; }
    public long ProgressionValidatedRevision { get; set; }

    // Friends currently has no persisted backend revision, so the client advertises a
    // durable-membership fingerprint. The GameServer still hydrates authoritative membership
    // before comparing it; this field can never grant membership authority.
    public long FriendsKnownRevision { get; set; }

    // Sequence for one-shot presentation pulses carried by the existing snapshot actionId byte.
    // This is transport/session state only; it is not durable gameplay state.
    public byte PresentationActionId { get; set; }

    public bool GameplayRuntimeActive { get; set; }
    public Action<CharacterResourceChange> ResourceChangedHandler { get; set; }
    public Action<StatusEffectChange> StatusEffectChangedHandler { get; set; }

    // Transport-facing player interaction admission state. This stays per connection
    // rather than in PlayerRuntime because sequence freshness and request-rate limits are
    // network admission concerns, not durable character state.
    public uint LastPlayerInteractionSequence { get; set; }
    public uint LastContextInteractionSequence { get; set; }
    public uint LastWorldItemInteractionSequence { get; set; }
    public double PlayerInteractionTokens { get; set; } = 6d;
    public long PlayerInteractionTokenTimestampMs { get; set; } = Environment.TickCount64;

    // One-way combat intents use their own sequence/rate gate before AOI/contact/effect work.
    // Gameplay cadence remains authoritative in BasicAttackService.
    public uint LastCombatActionSequence { get; set; }
    public double CombatRequestTokens { get; set; } = 8d;
    public long CombatRequestTokenTimestampMs { get; set; } = Environment.TickCount64;

    // Coarse request-channel admission. Movement and chat have their own dedicated
    // admission paths; this bucket bounds reliable request work such as character,
    // inventory, equipment, loot, combat, ability, interaction, snapshot and staff calls.
    public double ClientRequestTokens { get; set; } = 40d;
    public long ClientRequestTokenTimestampMs { get; set; } = Environment.TickCount64;

    // Ping/Pong has its own tiny response budget so it cannot bypass reliable-request
    // admission and manufacture unbounded reliable response traffic.
    public double PingTokens { get; set; } = 4d;
    public long PingTokenTimestampMs { get; set; } = Environment.TickCount64;

    // Reliable request replay admission. LiteNetLib request IDs are monotonically allocated
    // by the normal client but ReliableUnordered responses allow multiple in-flight requests,
    // so the server keeps a bounded sliding freshness window rather than requiring strict
    // arrival order. State is per connection and is discarded on disconnect.
    public bool HasReliableRequestId { get; set; }
    public uint HighestReliableRequestId { get; set; }
    public HashSet<uint> RecentReliableRequestIds { get; } = new HashSet<uint>();
    public List<uint> ReliableRequestPruneScratch { get; } = new List<uint>(32);

    // Narrow malformed-protocol strike state. Gameplay rejection, AOI/range failures,
    // cooldowns and rate-limit responses never increment this counter.
    public int ProtocolStrikeCount { get; set; }
    public long ProtocolStrikeWindowStartMs { get; set; } = Environment.TickCount64;

    // Malformed packet diagnostics are independently throttled from gameplay/protocol
    // admission so packet garbage cannot turn into unbounded synchronous console I/O.
    public int MalformedPacketLogCount { get; set; }
    public int MalformedPacketSuppressedCount { get; set; }
    public long MalformedPacketLogWindowStartMs { get; set; } = Environment.TickCount64;

    public ClientSession(
        NetPeer peer,
        PlayerSessionHandle sessionHandle,
        int authenticationTimeoutSeconds)
    {
        Peer = peer ?? throw new ArgumentNullException(nameof(peer));
        SessionHandle = sessionHandle;
        AuthenticationDeadlineUtc = DateTime.UtcNow.AddSeconds(authenticationTimeoutSeconds);
    }
}
