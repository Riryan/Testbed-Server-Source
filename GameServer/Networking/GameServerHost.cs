using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Game.GameServer.Runtime;
using Game.GameServer.Backend;
using Game.GameServer.Replication;
using Game.Server.Application.Abilities;
using Game.Server.Application.Connections;
using Game.Server.Application.Population;
using Game.Server.Application.Sessions;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Characters;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using Game.Shared.World;
using Game.Shared.WorldItems;
using Game.UnityIntegration;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Player.Networking;
using Player.Shared;

namespace Game.GameServer.Networking;

/// <summary>
/// Standalone LiteNetLib transport/runtime host for the authoritative plain-C# GameServer.
/// This parity slice restores character loading, canonical Ready/world adoption,
/// PlayerEntity spawning, authoritative movement, and event-driven position checkpoints.
/// </summary>
internal sealed partial class GameServerHost : INetEventListener, IDisposable
{
    private const ushort RequestMessageType = 0;
    private const ushort ResponseMessageType = 1;
    private const ushort RpcMessageType = 2;
    private const ushort SyncBaseLineMessageType = 3;
    private const ushort SyncDeltaMessageType = 4;
    private const ushort PingMessageType = 8;
    private const ushort PongMessageType = 9;

    private const byte AckSuccess = 0;
    private const byte AckError = 2;
    private const byte AckUnimplemented = 3;
    private const uint PacketVersion = 1;

    private const byte RpcReceiverServer = 2;
    private const byte GameStateSpawn = 1;
    private const byte GameStateDestroy = 2;

    private const string PlayerAssetId = "mmo.testbed.playerentity.v1";
    private const string SnapshotElementIdText = "Player.Networking.PlayerEntityNetwork_0__snapshot";
    private const string AppearanceElementIdText = "Player.Networking.PlayerEntityNetwork_0__appearance";
    private const string MovementRpcIdText = "Player.Networking.PlayerEntityNetwork_0_ServerReceiveMovement";

    private const double AuthenticationSweepIntervalSeconds = 1.0;
    private const double ReplicationDiagnosticsIntervalSeconds = 10.0;
    private const double CharacterLeaseRenewalIntervalSeconds = 15.0;
    // Must stay at or below the current Unity LiteNetLib client ceiling
    // (LiteNetLibGameManager.MAX_UNRELIABLE_PACKET_SIZE).
    private const int MaxUnreliableReplicationPacketSize = 1023;

    private static readonly int PlayerAssetHash = HashLiteNetLibId(PlayerAssetId);
    private static readonly int SnapshotElementId = HashLiteNetLibId(SnapshotElementIdText);
    private static readonly int AppearanceElementId = HashLiteNetLibId(AppearanceElementIdText);
    private static readonly int MovementRpcId = HashLiteNetLibId(MovementRpcIdText);

    private readonly GameServerOptions _options;
    private readonly GameServerRuntime _runtime;
    private readonly BackendGameServerDirectoryLease _directoryLease;
    private readonly NetManager _net;
    private readonly NetDataWriter _writer = new NetDataWriter();
    private readonly NetDataWriter _replicationObjectWriter = new NetDataWriter();
    private readonly Dictionary<int, ClientSession> _sessions = new Dictionary<int, ClientSession>();
    // Hot-path ready-session indexes. These eliminate repeated O(connection-count) scans
    // for character/object resolution during combat, staff tools, item events, and schedulers.
    // The authoritative _sessions dictionary remains the connection-owner source of truth.
    private readonly Dictionary<long, ClientSession> _readySessionsByCharacterId =
        new Dictionary<long, ClientSession>();
    private readonly Dictionary<uint, ClientSession> _readySessionsByObjectId =
        new Dictionary<uint, ClientSession>();
    private readonly List<ClientSession> _readySimulationSessions = new List<ClientSession>(256);
    private readonly List<ClientSession> _activeSimulationSessions = new List<ClientSession>(256);
    private readonly List<ClientSession> _simulationWorkSessions = new List<ClientSession>(256);
    private readonly Dictionary<ClientSession, ObserverSnapshotBatch> _snapshotBatches =
        new Dictionary<ClientSession, ObserverSnapshotBatch>();
    private readonly List<ObserverSnapshotBatch> _activeSnapshotBatches = new List<ObserverSnapshotBatch>(128);
    private readonly BoundedMainThreadQueue _mainThreadCompletions =
        new BoundedMainThreadQueue(criticalCapacity: 2048, normalCapacity: 4096, backgroundCapacity: 1024);
    private readonly CoreRuntimeScheduler _scheduler;
    private readonly ICoreScheduledSystemHandle _simulationClockHandle;
    private readonly ICoreScheduledSystemHandle _playerSimulationHandle;
    private readonly ICoreScheduledSystemHandle _populationSimulationHandle;
    private readonly ICoreScheduledSystemHandle _aoiAdditionHandle;
    private readonly ICoreScheduledSystemHandle _interactionSessionHandle;
    private readonly CharacterResourceRuntimeScheduler _resourceScheduler;
    private readonly CharacterCombatStateScheduler _combatStateScheduler;
    private readonly CharacterStatusEffectRuntimeScheduler _statusEffectScheduler;
    private readonly CharacterAbilityCastScheduler _abilityScheduler;
    private readonly WorldInterestService _worldInterest;
    private readonly WorldItemInterestService _worldItemInterest;
    private readonly ReplicationLodPolicy _replicationLodPolicy;

    private ICoreScheduledTaskHandle _authenticationSweepTask;
    private ICoreScheduledTaskHandle _characterCheckpointTask;
    private ICoreScheduledTaskHandle _replicationDiagnosticsTask;
    private bool _checkpointInFlight;
    private Task _checkpointDrainTask;
    private CancellationTokenSource _characterLeaseRenewalCancellation;
    private Task _characterLeaseRenewalLoopTask;
    private Task _leaseRenewalDrainTask;
    private readonly object _finalizerGate = new object();
    private readonly HashSet<Task> _sessionFinalizers = new HashSet<Task>();
    private uint _serverTick;
    private uint _nextObjectId = 1;
    private ushort _nextGeneration = 1;
    private bool _running;

    private long _replicationSnapshotCandidates;
    private long _replicationSnapshotsSent;
    private long _replicationSnapshotPacketsSent;
    private long _replicationSnapshotsCoalesced;
    private long _replicationSnapshotsLodSuppressed;
    private long _replicationSpawnSends;
    private long _replicationDestroySends;
    private long _replicationPayloadBytes;
    private long _replicationBudgetDeferrals;
    private long _wireCombatDamageMessages;
    private long _wireCombatDamageBytes;
    private long _wireAbilityStateMessages;
    private long _wireAbilityStateBytes;
    private long _wireResourceDeltaMessages;
    private long _wireResourceDeltaBytes;
    private long _wireStatusDeltaMessages;
    private long _wireStatusDeltaBytes;
    private int _replicationMaxPendingObservers;
    private int _replicationMaxPendingSnapshots;
    private int _snapshotFlushCursor;

    private sealed class SimulationClockSystem : ICoreTickSystem
    {
        private readonly GameServerHost _owner;
        public SimulationClockSystem(GameServerHost owner) => _owner = owner;
        public string Name => "StandaloneSimulationClock";
        public void Prepare(in CoreTickContext context) { }
        public void Execute(in CoreTickContext context)
        {
            _owner.SetCurrentSimulationTick(context.TickIndex);
            _owner._runtime.WorldItems.ProcessTransientDecay(128);
        }
        public void Commit(in CoreTickContext context) { }
    }

    private sealed class InteractionSessionTickSystem : ICoreBudgetedWorkSystem
    {
        private readonly GameServerHost _owner;
        public InteractionSessionTickSystem(GameServerHost owner) => _owner = owner;
        public string Name => "InteractionSessions";

        // Reuse the scheduler's existing dormant-work gate. With no active interaction
        // sessions there is nothing to expire/complete, so an idle server should not
        // time or execute this system at all.
        public bool HasPendingWork => _owner._runtime.InteractionSessions.Count > 0;

        public void ExecuteOneWorkUnit(in CoreTickContext context) =>
            _owner._runtime.InteractionSessions.Tick(context.Now);
    }

    private readonly struct PendingSnapshot
    {
        public IPlayerEntityPresentationSource Entity { get; }
        public float Speed { get; }
        public byte Flags { get; }
        public byte MoveState { get; }
        public byte ActionState { get; }
        public byte ActionId { get; }
        public bool Priority { get; }

        public PendingSnapshot(
            IPlayerEntityPresentationSource entity,
            float speed,
            byte flags,
            byte moveState,
            byte actionState,
            byte actionId,
            bool priority)
        {
            Entity = entity;
            Speed = speed;
            Flags = flags;
            MoveState = moveState;
            ActionState = actionState;
            ActionId = actionId;
            Priority = priority;
        }
    }

    private sealed class ObserverSnapshotBatch
    {
        public ClientSession Observer { get; }
        public Dictionary<uint, PendingSnapshot> Snapshots { get; } =
            new Dictionary<uint, PendingSnapshot>(64);
        public List<uint> KeyScratch { get; } = new List<uint>(64);
        public List<uint> PacketScratch { get; } = new List<uint>(32);
        public bool Active { get; set; }

        public ObserverSnapshotBatch(ClientSession observer) => Observer = observer;
    }

    private sealed class PopulationSimulationSystem : ICoreBudgetedWorkSystem
    {
        private readonly GameServerHost _owner;
        private readonly List<PopulationPlayerView> _players = new List<PopulationPlayerView>(128);
        private long _preparedTick = long.MinValue;
        private double _nextActivationCheckAt;

        public PopulationSimulationSystem(GameServerHost owner) => _owner = owner;
        public string Name => "PopulationAuthority";
        public bool HasPendingWork
        {
            get
            {
                double now = _owner._scheduler.ServerTime;
                if (_owner._runtime.Population.HasDueWork(now))
                    return true;
                return _owner._runtime.Population.HibernatingCount > 0 &&
                       _owner._readySessionsByCharacterId.Count > 0 &&
                       now + 0.000001d >= _nextActivationCheckAt;
            }
        }

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            if (_preparedTick != context.TickIndex)
            {
                _preparedTick = context.TickIndex;
                _players.Clear();
                foreach (ClientSession session in _owner._readySessionsByCharacterId.Values)
                {
                    if (!_owner.IsCurrent(session) || !session.Ready || session.Entity == null)
                        continue;

                    ServerPlayerEntity entity = session.Entity;
                    CharacterLocationState location = entity.Runtime.Location;
                    _players.Add(new PopulationPlayerView(
                        entity.Runtime.CharacterId.Value,
                        location.MapId,
                        location.InstanceId,
                        new WorldPosition(entity.X, entity.Y, entity.Z),
                        !entity.IsDead));
                }

                _owner._runtime.Population.PrepareBudgetedTick(_players, context.Now);
                _nextActivationCheckAt = context.Now + Math.Max(0.25d, _owner._runtime.Population.ActivationHeartbeatSeconds);
            }

            _owner._runtime.Population.TickNextBudgeted(context.FixedDelta, context.Now);
        }
    }

    private sealed class AoiAdditionSystem : ICoreConditionalPreparedBudgetedWorkSystem
    {
        private const int CandidatesPerWorkUnit = 32;
        private readonly GameServerHost _owner;
        private int _remainingCandidateBudget;
        private int _remainingEdgeBudget;

        public AoiAdditionSystem(GameServerHost owner) => _owner = owner;
        public string Name => "PlayerAoiAdditions";
        public bool ShouldPrepare => _owner._worldInterest.PendingAdditionScanCount > 0;
        public bool HasPendingWork =>
            _remainingCandidateBudget > 0 &&
            _remainingEdgeBudget > 0 &&
            _owner._worldInterest.PendingAdditionScanCount > 0;

        public void Prepare(in CoreTickContext context)
        {
            _remainingCandidateBudget = _owner._options.AoiCandidateChecksPerGameplayTick;
            _remainingEdgeBudget = _owner._options.AoiEdgeAdditionsPerGameplayTick;
        }

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            int candidates = Math.Min(CandidatesPerWorkUnit, _remainingCandidateBudget);
            if (candidates <= 0)
                return;

            _remainingCandidateBudget -= candidates;
            IReadOnlyList<WorldInterestService.Change> changes = _owner._worldInterest.ProcessPendingAdditions(
                candidates,
                _remainingEdgeBudget);
            _remainingEdgeBudget = Math.Max(0, _remainingEdgeBudget - changes.Count);
            _owner.ApplyInterestChanges(changes);
        }

        public static int WorkUnitsFor(int candidateBudget) =>
            Math.Max(1, (candidateBudget + CandidatesPerWorkUnit - 1) / CandidatesPerWorkUnit);
    }

    private sealed class PlayerSimulationBatchSystem : ICoreConditionalPreparedBudgetedWorkSystem
    {
        private readonly GameServerHost _owner;
        private int _cursor;

        public PlayerSimulationBatchSystem(GameServerHost owner) => _owner = owner;
        public string Name => "PlayerEntityAuthorityBatch";
        public bool ShouldPrepare => _owner._activeSimulationSessions.Count > 0;
        public bool HasPendingWork => _cursor < _owner._simulationWorkSessions.Count;

        public void Prepare(in CoreTickContext context)
        {
            _owner._simulationWorkSessions.Clear();
            for (int i = 0; i < _owner._activeSimulationSessions.Count; ++i)
            {
                ClientSession session = _owner._activeSimulationSessions[i];
                if (_owner.IsCurrent(session) && session.Ready && session.Entity != null)
                    _owner._simulationWorkSessions.Add(session);
            }
            _cursor = 0;
            _owner.SetCurrentSimulationTick(context.TickIndex);
        }

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            if (_cursor >= _owner._simulationWorkSessions.Count)
                return;

            ClientSession session = _owner._simulationWorkSessions[_cursor++];
            if (!_owner.IsCurrent(session) || !session.Ready || session.Entity == null)
            {
                _owner.RemoveActiveSimulationSession(session);
                return;
            }

            // Consume the current wake. A concurrent gameplay/resource/status event can set
            // SimulationDirty again while this unit is executing, keeping the entity active.
            session.SimulationDirty = false;
            long nowMilliseconds = Environment.TickCount64;
            bool shouldBroadcast = session.Entity.Tick(
                context.FixedDelta,
                context.Now,
                nowMilliseconds,
                out float speed,
                out byte flags,
                out byte moveState,
                out ServerPlayerSnapshotReason reason);

            CharacterLocationState currentLocation = session.Entity.CaptureLocation();
            if (_owner._runtime.Spawns.IsOutsideRecoveryBounds(currentLocation, out string boundsDetail))
            {
                if (_owner._runtime.Spawns.TryResolveNearestPlayerRecovery(
                        currentLocation, out CharacterLocationState recovery, out string recoveryDetail))
                {
                    session.Entity.Warp(recovery);
                    speed = session.Entity.SnapshotSpeed;
                    flags = session.Entity.SnapshotFlags;
                    moveState = session.Entity.SnapshotMoveState;
                    reason = ServerPlayerSnapshotReason.Forced |
                             ServerPlayerSnapshotReason.Position |
                             ServerPlayerSnapshotReason.PresentationState;
                    shouldBroadcast = true;
                    session.SimulationDirty = true;
                    Console.Error.WriteLine(
                        $"Player world recovery: peer={session.Peer.Id}, {boundsDetail}; {recoveryDetail}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"Player world recovery failed: peer={session.Peer.Id}, {boundsDetail}; {recoveryDetail}");
                }
            }

            if (shouldBroadcast)
                _owner.BroadcastSnapshot(session, speed, flags, moveState, reason);

            if (!session.SimulationDirty && !session.Entity.RequiresContinuousSimulation(nowMilliseconds))
                _owner.RemoveActiveSimulationSession(session);
        }
    }

    public GameServerHost(
        GameServerOptions options,
        GameServerRuntime runtime,
        BackendGameServerDirectoryLease directoryLease)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _directoryLease = directoryLease ?? throw new ArgumentNullException(nameof(directoryLease));

        _runtime.Population.DeadDecaySeconds = _options.PopulationDeadDecaySeconds;
        // Population wakes slightly ahead of replication AOI so doorway exits and route
        // presentation are already coherent when the player reaches observer range.
        _runtime.Population.PlayerActivationDistance = Math.Max(
            _options.AoiRange + _options.AoiExitPadding + 24f,
            _options.AoiRange * 1.5f);
        _runtime.Population.PlayerHibernateDistance = _runtime.Population.PlayerActivationDistance + Math.Max(24f, _options.AoiExitPadding * 2f);
        _runtime.WorldItems.ConfigureTransientPolicy(
            _options.DroppedItemLifetimeSeconds,
            _options.MaxTransientDroppedItems,
            _options.DroppedItemCleanupTarget);

        _net = new NetManager(this)
        {
            ChannelsCount = 16,
            AllowPeerAddressChange = true,
        };

        CoreRuntimeSchedulerSettings schedulerSettings = CreateSchedulerSettings(_options.TickRate, _options.MaxConnections);
        _scheduler = new CoreRuntimeScheduler(schedulerSettings);
        _simulationClockHandle = _scheduler.RegisterSystem(
            new SimulationClockSystem(this),
            "Simulation",
            quarantineable: false,
            // The clock also performs bounded transient-world-item housekeeping. Sub-ms
            // work is normal on Windows; keep the detector focused on meaningful spikes.
            invocationLimitMilliseconds: 1.0);
        _playerSimulationHandle = _scheduler.RegisterWorkSystem(
            new PlayerSimulationBatchSystem(this),
            "Simulation",
            maxWorkUnitsPerTick: Math.Max(1, _options.MaxConnections),
            quarantineable: true,
            invocationLimitMilliseconds: 1.0);
        _populationSimulationHandle = _scheduler.RegisterWorkSystem(
            new PopulationSimulationSystem(this),
            "AI",
            maxWorkUnitsPerTick: Math.Min(_options.PopulationActorsPerAiTick, Math.Max(1, _runtime.Population.Count)),
            quarantineable: true,
            invocationLimitMilliseconds: 2.0);

        _resourceScheduler = new CharacterResourceRuntimeScheduler(
            _scheduler,
            _runtime.Resources,
            runtimeState => _runtime.Combat.IsInCombat(runtimeState, _scheduler.ServerTime));
        _combatStateScheduler = new CharacterCombatStateScheduler(
            _scheduler,
            _runtime.Combat,
            _resourceScheduler);
        _statusEffectScheduler = new CharacterStatusEffectRuntimeScheduler(
            _scheduler,
            _runtime.StatusEffects,
            _runtime.Effects,
            ResolveReadyRuntime);
        _abilityScheduler = new CharacterAbilityCastScheduler(
            _scheduler,
            _runtime.Abilities,
            ResolveReadyRuntime,
            CanCompleteScheduledAbilityTarget);

        // Population combat state is lazy. Only actors that actually enter combat receive the
        // established status-effect runtime scheduler; ambient Population stays lightweight.
        _runtime.PopulationCombat.PopulationRuntimeActivated += OnPopulationCombatRuntimeActivated;
        _runtime.PopulationCombat.PopulationRuntimeDeactivated += OnPopulationCombatRuntimeDeactivated;

        _worldInterest = new WorldInterestService(
            _options.AoiRange,
            _options.AoiExitPadding,
            _options.AoiCellSize,
            (observer, target) => _runtime.GameMasters.CanObserverSee(
                observer?.AuthenticatedAccountId ?? 0,
                target?.AuthenticatedAccountId ?? 0));
        _aoiAdditionHandle = _scheduler.RegisterWorkSystem(
            new AoiAdditionSystem(this),
            "Gameplay",
            maxWorkUnitsPerTick: AoiAdditionSystem.WorkUnitsFor(_options.AoiCandidateChecksPerGameplayTick),
            quarantineable: true,
            invocationLimitMilliseconds: 0.5);
        _interactionSessionHandle = _scheduler.RegisterWorkSystem(
            new InteractionSessionTickSystem(this),
            "Gameplay",
            maxWorkUnitsPerTick: 1,
            quarantineable: true,
            invocationLimitMilliseconds: 0.5);

        _worldItemInterest = new WorldItemInterestService(
            _options.AoiRange,
            _options.AoiExitPadding,
            _options.AoiCellSize);
        _replicationLodPolicy = new ReplicationLodPolicy(_options.TickRate);
        InitializePopulationPresentation();
        _requestHandlers = BuildRequestHandlers();

        _runtime.PlayerItems.Changed += OnAuthoritativePlayerItemsChanged;
        _runtime.WorldItems.Changed += OnAuthoritativeWorldItemChanged;
        _runtime.WorldInteractables.Changed += OnAuthoritativeWorldInteractableChanged;
        _runtime.InteractionSessions.Changed += OnInteractionSessionChanged;
        _runtime.Progression.Changed += OnAuthoritativeProgressionChanged;
        _runtime.Combat.DamageResolved += OnCombatDamageResolved;
        InitializePopulationReactiveCombat();
        _runtime.Abilities.CastStateChanged += OnAbilityCastStateChanged;
        InitializeCombatPresentation();
        InitializeFirearmActions();
    }

    public void Run(CancellationToken cancellationToken)
    {
        if (!_net.Start(_options.Port))
            throw new InvalidOperationException($"LiteNetLib could not bind UDP port {_options.Port}.");

        _running = true;
        _scheduler.Start();
        _mainThreadCompletions.AttachCurrentThread();
        _scheduler.BeginStartupGrace();
        ScheduleAuthenticationSweep();
        ScheduleCharacterCheckpoint();
        StartCharacterLeaseRenewalLoop(cancellationToken);
        ScheduleReplicationDiagnostics();
        StartBackendEventStream(cancellationToken);
        Console.WriteLine($"GameServer listening on LiteNetLib UDP 0.0.0.0:{_options.Port}");
        Console.WriteLine($"Backend internal API: {_runtime.Backend.BaseUrl}");
        Console.WriteLine($"Protocol: packet={PacketVersion}, player={PlayerEntityProtocol.Version}, tick={_options.TickRate} Hz");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _scheduler.BeginFrame();
                try
                {
                    // Preserve the established authoritative ordering: inbound intent first,
                    // then completions, scheduled simulation/time mechanics, then delta output.
                    _net.PollEvents();
                    DrainMainThreadCompletions();
                    if (!_scheduler.IsFrameBudgetExhausted)
                        _scheduler.UpdateScheduledSystems();
                    if (!_scheduler.IsFrameBudgetExhausted)
                        _scheduler.ProcessDelayedTasks();

                    // All simulation systems for this scheduler frame have now had a chance
                    // to contribute their latest observer deltas. Emit one or more MTU-bounded
                    // SyncDelta packets per observer instead of one send per entity update.
                    FlushSnapshotBatches();
                    FlushOutboundQueues();
                }
                finally
                {
                    _scheduler.EndFrame();
                }
                Thread.Sleep(2);
            }
        }
        finally
        {
            _running = false;
            StopBackendEventStream();
            _authenticationSweepTask?.Cancel();
            _characterCheckpointTask?.Cancel();
            StopCharacterLeaseRenewalLoop();
            _replicationDiagnosticsTask?.Cancel();
            GracefulShutdown();
            _mainThreadCompletions.Complete();
            _scheduler.Stop(clearDelayedTasks: true);
            _net.Stop();
            _sessions.Clear();
            _readySessionsByCharacterId.Clear();
            _readySessionsByObjectId.Clear();
            _readySimulationSessions.Clear();
            _activeSimulationSessions.Clear();
            _simulationWorkSessions.Clear();
            ReleaseAllOutboundStates();
            _directoryLease.UpdateConnectedPlayers(0);
        }
    }

    private uint CurrentTick => _serverTick;

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (!_running || !_directoryLease.CanAdmitNewSessions || _net.ConnectedPeersCount >= _options.MaxConnections)
        {
            request.Reject();
            return;
        }

        request.AcceptIfKey(_options.ConnectKey);
    }

    public void OnPeerConnected(NetPeer peer)
    {
        PlayerSession playerSession = _runtime.SessionService.Open(new ConnectionKey(peer.Id));
        if (playerSession == null)
        {
            Console.Error.WriteLine($"Failed to open authoritative session for peer {peer.Id}.");
            peer.Disconnect();
            return;
        }

        var session = new ClientSession(peer, playerSession.Handle, _options.AuthenticationTimeoutSeconds);
        _sessions[peer.Id] = session;
        _directoryLease.UpdateConnectedPlayers(_sessions.Count);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (_sessions.Remove(peer.Id, out ClientSession session))
        {
            RemovePlayerChatState(peer.Id);
            session.Connected = false;
            if (session.AuthenticatedAccountId > 0)
                _runtime.GameMasters.CloseSession(session.AuthenticatedAccountId);
            StopAutomaticFireForSession(session, sendOwnerCorrection: false);
            RemoveFirearmActionState(session);
            RemoveCombatPresentationState(session);
            RemoveOutboundState(session);
            BeginSessionDisconnect(session);
            _directoryLease.UpdateConnectedPlayers(_sessions.Count);
        }

    }

    public void OnNetworkReceive(
        NetPeer peer,
        NetPacketReader reader,
        byte channelNumber,
        DeliveryMethod deliveryMethod)
    {
        ClientSession session = null;
        try
        {
            if (!_sessions.TryGetValue(peer.Id, out session) ||
                !session.Connected ||
                reader.AvailableBytes <= 0)
            {
                return;
            }

            ushort messageType = reader.GetPackedUShort();
            switch (messageType)
            {
                case RequestMessageType:
                    HandleRequest(session, reader);
                    break;
                case RpcMessageType:
                    HandleRpc(session, reader);
                    break;
                case PingMessageType:
                    HandlePing(session, reader);
                    break;
                case PlayerChatMessageTypes.Submit:
                    HandlePlayerChatSubmit(session, reader);
                    break;
                case PlayerGameplayActionMessageTypes.CombatActionIntent:
                    HandleCombatActionIntent(session, reader);
                    break;
                // Client Pong and client-owned state messages are not authoritative inputs here.
            }
        }
        catch (Exception ex)
        {
            LogMalformedPacket(session, peer, ex);
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleRequest(ClientSession session, NetDataReader reader)
    {
        ushort requestType = reader.GetPackedUShort();
        uint requestId = reader.GetPackedUInt();

        if (!TryAdmitReliableRequest(session, requestId))
            return;

        if (_requestHandlers.TryGetValue(requestType, out Action<ClientSession, uint, NetDataReader> handler))
        {
            handler(session, requestId, reader);
            return;
        }

        SendBareResponse(session, requestId, AckUnimplemented);
    }

    private void HandleRpc(ClientSession session, NetDataReader reader)
    {
        if (!session.Ready || session.Entity == null)
            return;

        byte receiver = reader.GetByte();
        if (receiver != RpcReceiverServer)
            return;

        uint objectId = reader.GetPackedUInt();
        int elementId = reader.GetPackedInt();
        if (objectId != session.Entity.ObjectId || elementId != MovementRpcId)
            return;

        uint sequence = reader.GetPackedUInt();
        sbyte inputX = reader.GetSByte();
        sbyte inputZ = reader.GetSByte();
        byte yaw = reader.GetByte();
        byte flags = reader.GetByte();
        byte combatFlags = reader.GetByte();
        long nowMilliseconds = Environment.TickCount64;

        bool accepted = session.Entity.TryAcceptMovement(
            sequence,
            inputX,
            inputZ,
            yaw,
            flags,
            combatFlags,
            nowMilliseconds,
            out bool simulationChanged);
        if (accepted && simulationChanged)
            MarkPlayerSimulationDirty(session);
        if (accepted && (inputX != 0 || inputZ != 0))
            _runtime.InteractionSessions.NotifyMovementIntent(session.Entity.Runtime.CharacterId.Value);
    }

    private void HandlePing(ClientSession session, NetDataReader reader)
    {
        if (!TryAdmitPing(session))
            return;

        long pingTime = reader.GetPackedLong();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _writer.Reset();
        _writer.PutPackedUShort(PongMessageType);
        _writer.PutPackedLong(pingTime);
        _writer.PutPackedLong(now);
        _writer.PutPackedUInt(CurrentTick);
        Send(session, _writer, DeliveryMethod.ReliableUnordered);
    }

    private void SetCurrentSimulationTick(long tickIndex)
    {
        uint candidate = unchecked((uint)(tickIndex + 1));
        _serverTick = candidate == 0 ? 1u : candidate;
    }

    private void BroadcastSnapshot(
        ClientSession targetSession,
        float speed,
        byte flags,
        byte moveState,
        ServerPlayerSnapshotReason reason,
        byte actionState = (byte)PlayerEntityActionState.None,
        byte actionId = 0)
    {
        if (!IsCurrent(targetSession) || !targetSession.Ready || targetSession.Entity == null)
            return;

        // Interest is reconciled from authoritative server position before any payload is
        // considered. This is the critical scalability boundary: out-of-AOI clients never
        // reach serialization or LiteNetLib Send().
        if ((reason & (ServerPlayerSnapshotReason.Position | ServerPlayerSnapshotReason.Forced)) != 0)
        {
            ApplyInterestChanges(_worldInterest.ReconcileMoved(targetSession));
            ReconcilePopulationObserver(targetSession);

            TryReconcileWorldItemInterestMoved(targetSession);
        }

        if (!_worldInterest.TryGetObservers(targetSession, out HashSet<ClientSession> observers))
            return;

        bool forceImmediate =
            (reason & (ServerPlayerSnapshotReason.Forced |
                       ServerPlayerSnapshotReason.DeathState |
                       ServerPlayerSnapshotReason.PresentationState)) != 0;
        double now = _scheduler.ServerTime;

        foreach (ClientSession observer in observers)
        {
            if (!IsCurrent(observer) || !observer.Ready || observer.Entity == null)
                continue;

            _replicationSnapshotCandidates++;
            if (!_worldInterest.TryGetDistanceSquared(observer, targetSession, out float distanceSquared))
                continue;

            double interval = _options.ReplicationLodEnabled
                ? _replicationLodPolicy.GetIntervalSeconds(distanceSquared)
                : 1d / Math.Max(1, (int)_options.TickRate);

            if (!_worldInterest.TryAcquireSnapshotSlot(
                    observer,
                    targetSession,
                    now,
                    interval,
                    forceImmediate))
            {
                _replicationSnapshotsLodSuppressed++;
                continue;
            }

            QueueSnapshot(
                observer,
                targetSession.Entity,
                speed,
                flags,
                moveState,
                forceImmediate,
                actionState,
                actionId);
        }
    }

    private static CoreRuntimeSchedulerSettings CreateSchedulerSettings(int tickRate, int maxConnections)
    {
        var settings = new CoreRuntimeSchedulerSettings();

        // Player simulation is now one cooperative batch rather than one scheduler system
        // per connection. Preserve the existing connection-scaled scheduler admission
        // headroom in this compatibility pass so unrelated future subsystem registrations
        // are not made more restrictive by the batching change.
        const int SchedulerSystemReserve = 64;
        settings.maxRegisteredSystems = Math.Max(
            settings.maxRegisteredSystems,
            checked(Math.Max(1, maxConnections) + SchedulerSystemReserve));

        double requestedHz = Math.Max(1, tickRate);
        for (int i = 0; i < settings.channels.Count; ++i)
        {
            CoreRuntimeChannelConfig channel = settings.channels[i];
            if (channel == null || !string.Equals(channel.name, "Simulation", StringComparison.OrdinalIgnoreCase))
                continue;

            channel.baseHz = requestedHz;
            channel.minHz = Math.Min(channel.minHz, requestedHz);
            break;
        }
        return settings;
    }

    private void SendResponse(ClientSession session, uint requestId, INetSerializable response)
    {
        if (!IsCurrent(session))
            return;

        _writer.Reset();
        _writer.PutPackedUShort(ResponseMessageType);
        _writer.PutPackedUInt(requestId);
        _writer.Put(AckSuccess);
        response.Serialize(_writer);
        Send(session, _writer, DeliveryMethod.ReliableUnordered);
    }

    private void SendBareResponse(ClientSession session, uint requestId, byte ackCode)
    {
        if (!IsCurrent(session))
            return;

        _writer.Reset();
        _writer.PutPackedUShort(ResponseMessageType);
        _writer.PutPackedUInt(requestId);
        _writer.Put(ackCode);
        Send(session, _writer, DeliveryMethod.ReliableUnordered);
    }

    private void Send(ClientSession session, NetDataWriter writer, DeliveryMethod deliveryMethod)
    {
        if (IsCurrent(session) && session.Peer.ConnectionState == ConnectionState.Connected)
            session.Peer.Send(writer, 0, deliveryMethod);
    }

    private void Send(
        ClientSession session,
        NetDataWriter writer,
        DeliveryMethod deliveryMethod,
        OutboundPriority priority,
        OutboundFamily family)
    {
        QueueOutbound(session, writer, deliveryMethod, priority, family);
    }


    private void RegisterReadySessionIndexes(ClientSession session)
    {
        if (session == null || !session.Connected || !session.Ready || session.Entity == null)
            return;

        long characterId = session.Entity.Runtime?.CharacterId.Value ?? 0L;
        if (characterId > 0)
            _readySessionsByCharacterId[characterId] = session;
        if (session.Entity.ObjectId != 0u)
            _readySessionsByObjectId[session.Entity.ObjectId] = session;

        if (session.SimulationListIndex < 0)
        {
            session.SimulationListIndex = _readySimulationSessions.Count;
            _readySimulationSessions.Add(session);
        }

        MarkPlayerSimulationDirty(session);
    }

    private void MarkPlayerSimulationDirty(ClientSession session)
    {
        if (session == null || !IsCurrent(session) || !session.Ready || session.Entity == null)
            return;

        session.SimulationDirty = true;
        if (session.SimulationActiveIndex >= 0)
            return;

        session.SimulationActiveIndex = _activeSimulationSessions.Count;
        _activeSimulationSessions.Add(session);
    }

    private void RemoveActiveSimulationSession(ClientSession session)
    {
        if (session == null)
            return;

        int index = session.SimulationActiveIndex;
        if (index >= 0)
        {
            if (index >= _activeSimulationSessions.Count ||
                !ReferenceEquals(_activeSimulationSessions[index], session))
            {
                // Indexed session lists use swap-remove. If an index ever becomes stale,
                // recover the actual slot instead of removing the wrong session or throwing.
                index = _activeSimulationSessions.IndexOf(session);
            }

            if (index >= 0)
            {
                int lastIndex = _activeSimulationSessions.Count - 1;
                ClientSession moved = _activeSimulationSessions[lastIndex];
                if (index != lastIndex)
                {
                    _activeSimulationSessions[index] = moved;
                    moved.SimulationActiveIndex = index;
                }
                _activeSimulationSessions.RemoveAt(lastIndex);
            }
        }

        session.SimulationActiveIndex = -1;
        session.SimulationDirty = false;
    }

    private void UnregisterReadySessionIndexes(ClientSession session)
    {
        if (session == null)
            return;

        ServerPlayerEntity entity = session.Entity;
        long characterId = entity?.Runtime?.CharacterId.Value ?? 0L;
        if (characterId > 0 &&
            _readySessionsByCharacterId.TryGetValue(characterId, out ClientSession characterOwner) &&
            ReferenceEquals(characterOwner, session))
        {
            _readySessionsByCharacterId.Remove(characterId);
        }

        uint objectId = entity?.ObjectId ?? 0u;
        if (objectId != 0u &&
            _readySessionsByObjectId.TryGetValue(objectId, out ClientSession objectOwner) &&
            ReferenceEquals(objectOwner, session))
        {
            _readySessionsByObjectId.Remove(objectId);
        }

        int simulationIndex = session.SimulationListIndex;
        if (simulationIndex >= 0)
        {
            if (simulationIndex >= _readySimulationSessions.Count ||
                !ReferenceEquals(_readySimulationSessions[simulationIndex], session))
            {
                // Keep disconnect cleanup fail-safe if a previous swap/remove left a stale index.
                simulationIndex = _readySimulationSessions.IndexOf(session);
            }

            if (simulationIndex >= 0)
            {
                int lastIndex = _readySimulationSessions.Count - 1;
                ClientSession moved = _readySimulationSessions[lastIndex];
                if (simulationIndex != lastIndex)
                {
                    _readySimulationSessions[simulationIndex] = moved;
                    moved.SimulationListIndex = simulationIndex;
                }
                _readySimulationSessions.RemoveAt(lastIndex);
            }
        }
        session.SimulationListIndex = -1;
        RemoveActiveSimulationSession(session);
    }

    private ClientSession FindIndexedReadySessionByCharacterId(long characterId)
    {
        if (characterId <= 0 ||
            !_readySessionsByCharacterId.TryGetValue(characterId, out ClientSession session) ||
            !IsCurrent(session) ||
            !session.Ready ||
            session.Entity == null ||
            session.Entity.Runtime == null ||
            session.Entity.Runtime.CharacterId.Value != characterId)
        {
            return null;
        }

        return session;
    }

    private ClientSession FindIndexedReadySessionByObjectId(uint objectId, ushort generation)
    {
        if (objectId == 0u ||
            !_readySessionsByObjectId.TryGetValue(objectId, out ClientSession session) ||
            !IsCurrent(session) ||
            !session.Ready ||
            session.Entity == null ||
            session.Entity.ObjectId != objectId ||
            session.Entity.Generation != generation)
        {
            return null;
        }

        return session;
    }

    private void LogMalformedPacket(ClientSession session, NetPeer peer, Exception ex)
    {
        const long WindowMilliseconds = 10_000;
        const int LogsPerWindow = 4;

        // NetDataReader uses this exact InvalidOperationException family for truncated or
        // impossible reads. Do not punish a client for an unrelated server-side handler
        // exception; those still get throttled diagnostics but are not protocol strikes.
        bool malformedRead = ex is InvalidOperationException &&
            ex.Message.StartsWith("Not enough data to read", StringComparison.Ordinal);
        string label = malformedRead ? "Malformed packet" : "Packet handler failure";

        if (session == null)
        {
            Console.Error.WriteLine($"{label} from peer {peer?.Id}: {ex.Message}");
            return;
        }

        if (malformedRead)
            RecordProtocolStrike(session, "malformed packet");

        long now = Environment.TickCount64;
        long elapsed = Math.Max(0L, now - session.MalformedPacketLogWindowStartMs);
        if (elapsed >= WindowMilliseconds)
        {
            if (session.MalformedPacketSuppressedCount > 0)
            {
                Console.Error.WriteLine(
                    $"Packet receive peer={peer?.Id}: suppressed {session.MalformedPacketSuppressedCount} duplicate/error logs in the previous window.");
            }

            session.MalformedPacketLogWindowStartMs = now;
            session.MalformedPacketLogCount = 0;
            session.MalformedPacketSuppressedCount = 0;
        }

        if (session.MalformedPacketLogCount < LogsPerWindow)
        {
            session.MalformedPacketLogCount++;
            Console.Error.WriteLine($"{label} from peer {peer?.Id}: {ex.Message}");
        }
        else
        {
            session.MalformedPacketSuppressedCount++;
        }
    }

    private bool IsCurrent(ClientSession session) =>
        session != null &&
        session.Connected &&
        _sessions.TryGetValue(session.Peer.Id, out ClientSession current) &&
        ReferenceEquals(current, session);

    private bool TryGetAuthoritativeSession(ClientSession client, out PlayerSession session)
    {
        if (client == null)
        {
            session = null;
            return false;
        }
        return _runtime.SessionService.TryGetSession(client.SessionHandle, out session);
    }

    private bool QueueMainThreadCompletion(
        Action action,
        MainThreadCompletionPriority priority = MainThreadCompletionPriority.Normal)
    {
        bool accepted = _mainThreadCompletions.Enqueue(action, priority);
        if (!accepted)
            Console.Error.WriteLine("Main-thread completion rejected because the server completion queue is stopping.");
        return accepted;
    }

    private void DrainMainThreadCompletions()
    {
        _mainThreadCompletions.Drain(
            maxActions: 1024,
            onError: ex => Console.Error.WriteLine(ex));
    }

    private void SweepAuthenticationDeadlines()
    {
        if (_sessions.Count == 0)
            return;

        DateTime now = DateTime.UtcNow;
        List<ClientSession> expired = null;
        foreach (ClientSession client in _sessions.Values)
        {
            if (!client.Connected || client.AuthenticationDeadlineUtc > now)
                continue;

            if (!TryGetAuthoritativeSession(client, out PlayerSession session) || !session.HasAccount)
            {
                expired ??= new List<ClientSession>();
                expired.Add(client);
            }
        }

        if (expired == null)
            return;

        foreach (ClientSession session in expired)
        {
            Console.Error.WriteLine($"Authentication timeout: peer={session.Peer.Id}");
            session.Peer.Disconnect();
        }
    }

    private uint NextObjectId()
    {
        uint candidate = _nextObjectId++;
        if (candidate == 0)
            candidate = _nextObjectId++;
        return candidate;
    }

    private ushort NextGeneration()
    {
        ushort candidate = _nextGeneration++;
        if (candidate == 0)
            candidate = _nextGeneration++;
        return candidate;
    }

    private static EnterCharacterResponseMessage SuccessfulEnterResponse(
        long characterId,
        PlayerSessionState state,
        bool worldAdopted) =>
        new EnterCharacterResponseMessage
        {
            success = true,
            worldAdopted = worldAdopted,
            characterId = characterId,
            sessionState = (byte)state,
            failure = (byte)CharacterSelectFailure.None,
            error = string.Empty,
        };

    private static int HashLiteNetLibId(string id)
    {
        unchecked
        {
            int hash1 = 5381;
            int hash2 = hash1;
            for (int i = 0; i < id.Length && id[i] != '\0'; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ id[i];
                if (i == id.Length - 1 || id[i + 1] == '\0')
                    break;
                hash2 = ((hash2 << 5) + hash2) ^ id[i + 1];
            }
            return hash1 + (hash2 * 1566083941);
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        Console.Error.WriteLine($"LiteNetLib network error {endPoint}: {socketError}");

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    public void OnNetworkReceiveUnconnected(
        IPEndPoint remoteEndPoint,
        NetPacketReader reader,
        UnconnectedMessageType messageType) => reader.Recycle();

    private void OnPopulationCombatRuntimeActivated(PlayerRuntime runtime)
    {
        _resourceScheduler?.Activate(runtime);
        _combatStateScheduler?.Activate(runtime);
        _statusEffectScheduler?.Activate(runtime);
    }

    private void OnPopulationCombatRuntimeDeactivated(PlayerRuntime runtime)
    {
        _statusEffectScheduler?.Deactivate(runtime);
        _combatStateScheduler?.Deactivate(runtime);
        _resourceScheduler?.Deactivate(runtime);
    }

    public void Dispose()
    {
        _runtime.PlayerItems.Changed -= OnAuthoritativePlayerItemsChanged;
        _runtime.WorldItems.Changed -= OnAuthoritativeWorldItemChanged;
        _runtime.WorldInteractables.Changed -= OnAuthoritativeWorldInteractableChanged;
        _runtime.InteractionSessions.Changed -= OnInteractionSessionChanged;
        _runtime.Progression.Changed -= OnAuthoritativeProgressionChanged;
        DisposePopulationReactiveCombat();
        _runtime.Combat.DamageResolved -= OnCombatDamageResolved;
        _runtime.BasicAttacks.Resolved -= OnBasicAttackResolved;
        _runtime.Abilities.CastStateChanged -= OnAbilityCastStateChanged;
        _runtime.PopulationCombat.PopulationRuntimeActivated -= OnPopulationCombatRuntimeActivated;
        _runtime.PopulationCombat.PopulationRuntimeDeactivated -= OnPopulationCombatRuntimeDeactivated;
        DisposePopulationPresentation();
        _abilityScheduler?.Dispose();
        _combatPresentationHandle?.Unregister();
        _automaticFireHandle?.Unregister();
        _fireCycleFlushHandle?.Unregister();
        _statusEffectScheduler?.Dispose();
        _combatStateScheduler?.Dispose();
        _resourceScheduler?.Dispose();
        _running = false;
        StopBackendEventStream();
        ClearPlayerChatState();
        _authenticationSweepTask?.Cancel();
        _characterCheckpointTask?.Cancel();
        StopCharacterLeaseRenewalLoop();
        _interactionSessionHandle?.Unregister();
        _aoiAdditionHandle?.Unregister();
        _populationSimulationHandle?.Unregister();
        _playerSimulationHandle?.Unregister();
        _simulationClockHandle?.Unregister();
        _scheduler.Stop(clearDelayedTasks: true);
        _snapshotBatches.Clear();
        _activeSnapshotBatches.Clear();
        _activeSimulationSessions.Clear();
        _simulationWorkSessions.Clear();
        ReleaseAllOutboundStates();
        _directoryLease.UpdateConnectedPlayers(0);
        _net.Stop();
    }
}

internal static class TaskExtensions
{
    public static void Forget(this Task task)
    {
        _ = task.ContinueWith(
            completed => Console.Error.WriteLine(completed.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
