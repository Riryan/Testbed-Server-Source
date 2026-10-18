using System;
using System.Collections.Generic;
using Game.Server.Application.Scheduling;
using Game.Server.Application.Actors;
using Game.Server.Application.Interactions;
using Game.Server.Application.Movement;
using Game.Server.Application.World;
using Game.Server.Domain.Players;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.World;

namespace Game.Server.Application.Population
{
    public readonly struct PopulationPlayerView
    {
        public long CharacterId { get; }
        public string MapId { get; }
        public string InstanceId { get; }
        public WorldPosition Position { get; }
        public bool Alive { get; }

        public PopulationPlayerView(long characterId, string mapId, string instanceId, WorldPosition position, bool alive = true)
        {
            CharacterId = characterId;
            MapId = mapId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            Position = position;
            Alive = alive;
        }
    }

    public enum PopulationPortalSequencePhase : byte
    {
        None = 0,
        Interior = 1,
        Approach = 2,
        Door = 3,
        Threshold = 4,
        Exterior = 5,
    }

    public enum SharedAiMovementMode : byte
    {
        Route = 0,
        FreeRoam = 1,
        Stationary = 2,
    }

    // Compiled once from the existing immutable baked route graph, shared by actors.
    internal sealed class PopulationRouteSegment
    {
        internal ServerPopulationRouteNode From, To;
        internal float Dx, Dz, LengthSquared, Length, SideX, SideZ, Yaw, LaneOffset, HalfWidth;
        internal int CertifiedLaneMask;
    }

    public sealed class PopulationActorRuntime
    {
        internal CharacterMotorState MotorState;
        internal ServerCharacterMotor Motor;
        internal Random Random;
        internal long CurrentNodeId;
        internal long NextNodeId;
        internal float SegmentProgress;
        internal object AssignedRouteGraph;
        internal long AssignedFromId, AssignedToId;
        internal ServerPopulationRouteNode AssignedRouteTarget;
        internal PopulationRouteSegment AssignedRouteSegment;
        internal float LaneOffset;
        internal double WaitUntil;
        internal double DormantUntil;
        internal double LastProgressAt;
        internal WorldPosition LastProgressPosition;
        internal WorldPosition ThreatPosition;
        internal double ThreatUntil;
        internal long ThreatCharacterId;
        internal WorldPosition ThreatAnchorPosition;
        internal float ThreatEngageRange;
        internal long LastPortalId;
        internal PopulationPortalSequencePhase PortalPhase;
        internal int PortalRespawnAttempts;
        internal double LastSimulationAt;
        internal double NextSimulationAt;
        // Reuse actor map/instance keys across scheduled work; refresh only on change.
        internal string CachedPartitionMapId;
        internal string CachedPartitionInstanceId;
        internal string CachedPartitionKey;
        internal double DeadDecayAt;
        internal uint ScheduleVersion;
        internal bool AwaitingPlayerActivation;
        internal double HibernatedAt;
        internal string HibernationPartitionKey = string.Empty;
        internal long HibernationCellKey;
        internal WorldPosition HibernationPosition;
        internal long PreviousNodeId;
        internal WorldPosition HomePosition;
        internal WorldPosition RoamTarget;
        internal bool HasRoamTarget;
        internal double NextRoamDecisionAt;
        // Avoid only the edge that just blocked this actor. No graph or cache rebuild.
        internal long BlockedRouteFromNodeId;
        internal long BlockedRouteToNodeId;
        internal double BlockedRouteUntil;
        // Flee-only direction fallback; follows the existing Population work cadence.
        internal double FleeSideStepUntil;
        internal int FleeSideStepSign;

        public AuthoritativeActorRuntime Actor { get; internal set; }
        public PlayerRuntime CombatRuntime { get; internal set; }
        public PopulationNpcType NpcType { get; internal set; }
        public PopulationSimulationLod SimulationLod { get; internal set; }
        public PopulationAiState AiState { get; internal set; }
        public PopulationRouteReason RouteReason { get; internal set; }
        public float WalkSpeed { get; internal set; }
        public float RunSpeed { get; internal set; }
        public float Aggression { get; internal set; }
        public float Courage { get; internal set; }
        public float CombatSkill { get; internal set; }
        public long SpawnAnchorId { get; internal set; }
        public string DeathLootTableId { get; internal set; } = string.Empty;
        public ServerSpawnKind SpawnKind { get; internal set; } = ServerSpawnKind.Population;
        public SharedAiMovementMode MovementBehavior { get; internal set; } = SharedAiMovementMode.Route;
        public float RoamRadius { get; internal set; }
        public float LeashRadius { get; internal set; }
        public float CapsuleRadius { get; internal set; } = 0.35f;
        public float CapsuleHeight { get; internal set; } = 1.8f;
        public long CurrentPortalId => LastPortalId;
        public long CurrentRouteNodeId => CurrentNodeId;
        public long NextRouteNodeId => NextNodeId;
        public float RouteSegmentProgress => SegmentProgress;
        public bool IsHibernating => AwaitingPlayerActivation;
        public PopulationPortalSequencePhase PortalSequencePhase => PortalPhase;
    }

    /// <summary>
    /// Lightweight, route-driven standalone Population simulation. It preserves the established
    /// uMMO organic startup/portal lifecycle while separating high-level route intent from the
    /// common authoritative CharacterMotor. Far actors advance as route progress only; nearby
    /// actors are promoted to grounded/collision-aware kinematic movement.
    /// </summary>
    public sealed partial class PopulationSimulationService
    {
        private sealed class MapGraph
        {
            public ServerMapSnapshot Map;
            public Dictionary<long, ServerPopulationRouteNode> Nodes = new Dictionary<long, ServerPopulationRouteNode>();
            public Dictionary<long, List<ServerPopulationRouteEdge>> Outgoing = new Dictionary<long, List<ServerPopulationRouteEdge>>();
            public Dictionary<long, ServerPopulationPortal> Portals = new Dictionary<long, ServerPopulationPortal>();
            public Dictionary<long, List<ServerPopulationPortal>> PortalsByRouteNode = new Dictionary<long, List<ServerPopulationPortal>>();
            // One live identity per authored spawn slot. Portal counts include dormant slots.
            public HashSet<long> OccupiedSpawnAnchors = new HashSet<long>();
            public Dictionary<long, long> SpawnPortalByAnchor = new Dictionary<long, long>();
            public Dictionary<long, int> PortalOwnedCounts = new Dictionary<long, int>();
            public ServerCollisionWorld Collision;
            public readonly Dictionary<(long From, long To), PopulationRouteSegment> Segments =
                new Dictionary<(long From, long To), PopulationRouteSegment>();
            // Compiled once from the baked edge order. Ambient civilians follow a stable
            // successor instead of rescanning/weighting the graph or drawing a new route
            // at every node arrival.
            public readonly Dictionary<(long Node, long Previous, PopulationNpcType Type), ServerPopulationRouteEdge>
                AmbientRouteSuccessors =
                    new Dictionary<(long Node, long Previous, PopulationNpcType Type), ServerPopulationRouteEdge>();
            // Static route certificate: authored edge and one of the three actual
            // lane offsets (-1,0,+1). Built once per immutable map load.
            public readonly HashSet<(long From, long To, int Lane)> CertifiedLanes =
                new HashSet<(long From, long To, int Lane)>();
        }

        private sealed class PortalReleaseState
        {
            public uint AdmissionGeneration;
            public int BurstCount;
            public double NextReleaseAt;
        }

        private readonly ServerMapCatalog _maps;
        private readonly AuthoritativeActorRegistry _actors;
        private readonly WorldInteractableService _worldObjects;
        private readonly Dictionary<string, MapGraph> _graphs = new Dictionary<string, MapGraph>(StringComparer.Ordinal);
        private readonly Dictionary<long, PopulationActorRuntime> _population = new Dictionary<long, PopulationActorRuntime>();
        private readonly List<PopulationActorRuntime> _populationSchedule = new List<PopulationActorRuntime>();
        private readonly MinPriorityQueue<PopulationScheduleEntry, double> _dueSchedule =
            new MinPriorityQueue<PopulationScheduleEntry, double>();
        private const float PlayerSpatialCellSize = 64f;
        private const float HibernationCellSize = 96f;

        private readonly struct PopulationScheduleEntry
        {
            public readonly PopulationActorRuntime Actor;
            public readonly uint Version;

            public PopulationScheduleEntry(PopulationActorRuntime actor, uint version)
            {
                Actor = actor;
                Version = version;
            }
        }

        private sealed class PlayerSpatialPartition
        {
            public readonly List<PopulationPlayerView> Players = new List<PopulationPlayerView>(16);
            public readonly Dictionary<long, PopulationPlayerView> ByCharacterId =
                new Dictionary<long, PopulationPlayerView>();
            public readonly Dictionary<long, List<PopulationPlayerView>> Cells =
                new Dictionary<long, List<PopulationPlayerView>>();
            public readonly Stack<List<PopulationPlayerView>> CellPool =
                new Stack<List<PopulationPlayerView>>();

            public void Reset()
            {
                foreach (List<PopulationPlayerView> cell in Cells.Values)
                {
                    cell.Clear();
                    if (CellPool.Count < 1024)
                        CellPool.Push(cell);
                }
                Cells.Clear();
                Players.Clear();
                ByCharacterId.Clear();
            }

            public void Add(PopulationPlayerView player)
            {
                Players.Add(player);
                if (player.CharacterId > 0)
                    ByCharacterId[player.CharacterId] = player;

                // The existing small-party proximity paths scan Players directly.
                // Do not rebuild a secondary cell index for 1..8 players. If a
                // partition grows to 9, index its existing 8 players exactly once.
                if (Players.Count <= 8)
                    return;
                if (Players.Count == 9)
                {
                    for (int i = 0; i < Players.Count; ++i)
                        AddPlayerToCell(Players[i]);
                    return;
                }
                AddPlayerToCell(player);
            }

            private void AddPlayerToCell(PopulationPlayerView player)
            {
                int x = ToPlayerCell(player.Position.X);
                int z = ToPlayerCell(player.Position.Z);
                long key = PackPlayerCell(x, z);
                if (!Cells.TryGetValue(key, out List<PopulationPlayerView> cell))
                {
                    cell = CellPool.Count > 0 ? CellPool.Pop() : new List<PopulationPlayerView>(8);
                    Cells.Add(key, cell);
                }
                cell.Add(player);
            }
        }

        private readonly Dictionary<string, PlayerSpatialPartition> _playersByPartition =
            new Dictionary<string, PlayerSpatialPartition>(StringComparer.Ordinal);
        // One reusable copy of the last exact player view snapshot. Do not rebuild
        // partitions when no player position, map, identity or alive flag changed.
        private readonly List<PopulationPlayerView> _lastPreparedPlayerViews =
            new List<PopulationPlayerView>(128);
        private readonly Dictionary<string, Dictionary<long, List<PopulationActorRuntime>>> _hibernatedByPartition =
            new Dictionary<string, Dictionary<long, List<PopulationActorRuntime>>>(StringComparer.Ordinal);
        private readonly HashSet<PopulationActorRuntime> _activationScratch = new HashSet<PopulationActorRuntime>();

        // Dormant activation is processed through one shared rotating cursor, not a full
        // players x cells x dormant-Pops scan inside one AI scheduler invocation.
        private readonly struct WakeSearchPlayer
        {
            public readonly PopulationPlayerView Player;
            public readonly Dictionary<long, List<PopulationActorRuntime>> Cells;
            public readonly PlayerSpatialPartition PlayerPartition;

            public WakeSearchPlayer(
                PopulationPlayerView player,
                Dictionary<long, List<PopulationActorRuntime>> cells,
                PlayerSpatialPartition playerPartition)
            {
                Player = player;
                Cells = cells;
                PlayerPartition = playerPartition;
            }
        }

        // Reused only while preparing a wake pass; no per-player/NPC registrations.
        // Players in the same hibernation cell share the identical cell search area.
        private readonly HashSet<long> _wakePlayerCenterCells = new HashSet<long>();
        private readonly List<WakeSearchPlayer> _wakeSearchPlayers = new List<WakeSearchPlayer>(64);
        private int _wakePlayerCursor;
        private int _wakeCellCursor;
        private int _wakeActorCursor;
        private bool _wakeScanPending;

        // Per-invocation ceilings. These bound preparatory work even if a portal contains
        // a dense hibernated crowd; the next AI heartbeat resumes the same spatial cell.
        private const int MaxWakeCellsPerPass = 16;
        private const int MaxWakeActorsCheckedPerPass = 32;
        // Two candidate resumes at most per scan, with an elapsed-time guard below.
        // Genuine portal releases retain all existing admission/clearance limits.
        private const int MaxWakeActionsPerPass = 2;
        private const double MaxWakeResumeWorkMillisecondsPerPass = 1.50d;
        private const int MaxStaleSchedulePrunesPerCall = 32;
        private readonly List<AuthoritativeActorRuntime> _nearbyScratch = new List<AuthoritativeActorRuntime>();
        private readonly Dictionary<long, PortalReleaseState> _portalReleaseStates =
            new Dictionary<long, PortalReleaseState>();
        private uint _portalAdmissionGeneration;
        private int _portalRespawnsThisTick;
        private int _hibernatingCount;
        private double _nextActivationHeartbeatAt;
        // The first AI work unit prepares the player partitions; the following unit
        // performs activation. This persists across AI ticks if the scheduler allows
        // only one work unit before its frame/channel budget is exhausted.
        private bool _activationHandoffPending;
        private bool _activationHandoffReady;

        // Diagnostics are sampled only from the existing Population work path.
        // Severe overrun details have their own five-second cooldown; normal actor
        // ticks and ordinary network activity never produce messages.
        private enum SlowWorkPhase : byte
        {
            None, Handoff, WakeScan, DueQueue, DeadDecay, ActorDistance,
            ActorDead, ActorDormant, ActorHibernate, ActorThreat,
            ActorRecovery, ActorWaiting, PortalExit, FreeRoamMotor,
            Stationary, LogicalRoute, CoarseRoute, ActiveMotor
        }

        private const double SevereWorkMilliseconds = 4.0d;
        private const double SlowWorkDetailCooldownSeconds = 5.0d;
        private double _nextSlowWorkDetailAt;
        private SlowWorkPhase _diagnosticPhase;
        private long _diagnosticActorId;
        private double _diagnosticPrepareMs;
        private double _diagnosticPartitionRebuildMs;
        private int _diagnosticPreparedPlayers;
        private bool _diagnosticPartitionRebuilt;
        private double _diagnosticActorMs;
        private double _diagnosticGcPauseMs;
        private long _diagnosticActorAllocatedBytes;
        private int _diagnosticGcGen0Collections;
        private int _diagnosticGcGen1Collections;
        private int _diagnosticGcGen2Collections;
        private double _diagnosticDueQueueMs;
        private int _diagnosticDueQueueChecks;
        private double _diagnosticActorSetupMs;
        private double _diagnosticRescheduleMs;
        private int _diagnosticStaleScheduleHeadsPruned;
        // Separate the movement/collision runtime from actor spatial publication
        // and the existing Population.Changed observer-presentation callback.
        // These are passive elapsed-time counters, not a new execution route.
        private double _diagnosticMotorCollisionMs;
        private double _diagnosticActiveGraphMs;
        private double _diagnosticActiveEnsureNodeMs;
        private double _diagnosticActiveRouteSetupMs;
        private double _diagnosticActiveTargetMs;
        private double _diagnosticActiveAmbientMs;
        private double _diagnosticActiveFallbackMs;
        private double _diagnosticDormantGraphMs;
        private double _diagnosticDormantProximityMs;
        private double _diagnosticDormantAdmissionMs;
        private double _diagnosticDormantSpawnClearMs;
        private double _diagnosticDormantActivateMs;
        private double _diagnosticRouteBindMs, _diagnosticRouteTravelMs, _diagnosticRouteArrivalMs;
        private double _diagnosticRouteRecoveryMs, _diagnosticRouteSelectMs;
        private int _diagnosticRouteBindings, _diagnosticStuckChecks, _diagnosticRouteReattachments;
        private double _diagnosticActorPublishMs;
        private double _diagnosticObserverPresentationMs;
        private int _diagnosticMotorCalls;
        private double _diagnosticDistanceMs;
        private double _diagnosticWakeScanMs;
        private double _diagnosticWakeAdmissionMs;
        private double _diagnosticWakeSpatialMs;
        private double _diagnosticWakeActorPublishMs;
        private double _diagnosticWakeObserverPublishMs;
        private double _diagnosticWakeScheduleMs;
        private double _diagnosticPortalMs;
        private double _diagnosticCatchUpMs;
        private int _diagnosticCatchUpHops;
        private int _diagnosticWakeCells;
        private int _diagnosticWakeActorChecks;
        private int _diagnosticWakeOrigins;
        private int _diagnosticWakeCandidates;
        private int _diagnosticWakeAdmitted;

        public bool EnableSlowWorkDiagnostics { get; set; } = true;

        private static double ElapsedDiagnosticMilliseconds(long startedAt) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) *
            (1000d / System.Diagnostics.Stopwatch.Frequency);

        private void ReportSlowPopulationWork(double now, long startedAt, string operation)
        {
            if (!EnableSlowWorkDiagnostics || now < _nextSlowWorkDetailAt)
                return;
            double totalMs = ElapsedDiagnosticMilliseconds(startedAt);
            if (totalMs < SevereWorkMilliseconds)
                return;

            _nextSlowWorkDetailAt = now + SlowWorkDetailCooldownSeconds;
            double otherActorMs = _diagnosticPhase == SlowWorkPhase.ActiveMotor
                ? Math.Max(0d, _diagnosticActorMs - _diagnosticDistanceMs -
                    _diagnosticActiveGraphMs - _diagnosticActiveEnsureNodeMs -
                    _diagnosticActiveRouteSetupMs - _diagnosticActiveTargetMs -
                    _diagnosticActiveAmbientMs - _diagnosticActiveFallbackMs)
                : _diagnosticPhase == SlowWorkPhase.ActorDormant
                ? Math.Max(0d, _diagnosticActorMs - _diagnosticDormantGraphMs -
                    _diagnosticDormantProximityMs - _diagnosticDormantAdmissionMs -
                    _diagnosticDormantSpawnClearMs - _diagnosticDormantActivateMs)
                : Math.Max(0d, _diagnosticActorMs - _diagnosticDistanceMs -
                    _diagnosticMotorCollisionMs - _diagnosticAmbientCollisionMs -
                    _diagnosticActorPublishMs - _diagnosticObserverPresentationMs);
            double outsideActorStagesMs = _diagnosticActorId == 0
                ? 0d
                : Math.Max(0d, totalMs - _diagnosticDueQueueMs - _diagnosticActorSetupMs -
                    _diagnosticActorMs - _diagnosticRescheduleMs);
            // One bounded warning for a real slow unit, not one line per actor or
            // an independent timer. Values are durations of measured sub-steps;
            // they are not guaranteed to add up to the total wall-clock time.
            Console.Error.WriteLine(
                $"[PopulationAuthority] Slow-work detail: unit={operation}, total={totalMs:F3}ms, " +
                $"phase={_diagnosticPhase}, prep={_diagnosticPrepareMs:F3}ms, " +
                $"prepPartition={_diagnosticPartitionRebuildMs:F3}ms, prepPlayers={_diagnosticPreparedPlayers}, " +
                $"prepRebuilt={_diagnosticPartitionRebuilt}, " +
                $"actor={_diagnosticActorId}, actorWork={_diagnosticActorMs:F3}ms, distance={_diagnosticDistanceMs:F3}ms, " +
                $"actorGcPause={_diagnosticGcPauseMs:F3}ms, actorAllocated={_diagnosticActorAllocatedBytes}B, " +
                $"gcCollections={_diagnosticGcGen0Collections}/{_diagnosticGcGen1Collections}/{_diagnosticGcGen2Collections}, " +
                $"dueQueueWork={_diagnosticDueQueueMs:F3}ms/{_diagnosticDueQueueChecks}checks/{_diagnosticStaleScheduleHeadsPruned}stalePruned, " +
                $"actorSetup={_diagnosticActorSetupMs:F3}ms, actorReschedule={_diagnosticRescheduleMs:F3}ms, " +
                $"outsideActorStages={outsideActorStagesMs:F3}ms, " +
                $"motorCollision={_diagnosticMotorCollisionMs:F3}ms/{_diagnosticMotorCalls}calls, " +
                $"ambientSweep={_diagnosticAmbientCollisionMs:F3}ms/{_diagnosticAmbientSamples}samples, " +
                $"ambientFallbacks={_diagnosticAmbientFallbacks}, " +
                $"actorPublish={_diagnosticActorPublishMs:F3}ms, " +
                $"observerPresentation={_diagnosticObserverPresentationMs:F3}ms, " +
                $"routeBind={_diagnosticRouteBindMs:F3}ms/{_diagnosticRouteBindings}binds, " +
                $"routeSelect={_diagnosticRouteSelectMs:F3}ms, routeTravel={_diagnosticRouteTravelMs:F3}ms, routeArrival={_diagnosticRouteArrivalMs:F3}ms, " +
                $"routeRecovery={_diagnosticRouteRecoveryMs:F3}ms/{_diagnosticStuckChecks}checks/{_diagnosticRouteReattachments}reattach, " +
                $"activeGraph={_diagnosticActiveGraphMs:F3}ms, activeEnsureNode={_diagnosticActiveEnsureNodeMs:F3}ms, " +
                $"activeRouteSetup={_diagnosticActiveRouteSetupMs:F3}ms, activeTarget={_diagnosticActiveTargetMs:F3}ms, " +
                $"activeAmbient={_diagnosticActiveAmbientMs:F3}ms, activeFallback={_diagnosticActiveFallbackMs:F3}ms, " +
                $"dormantGraph={_diagnosticDormantGraphMs:F3}ms, dormantProximity={_diagnosticDormantProximityMs:F3}ms, " +
                $"dormantAdmission={_diagnosticDormantAdmissionMs:F3}ms, dormantSpawnClear={_diagnosticDormantSpawnClearMs:F3}ms, " +
                $"dormantActivate={_diagnosticDormantActivateMs:F3}ms, " +
                $"actorOther={otherActorMs:F3}ms, wakeScan={_diagnosticWakeScanMs:F3}ms, wakeAdmission={_diagnosticWakeAdmissionMs:F3}ms, " +
                $"wakeSpatial={_diagnosticWakeSpatialMs:F3}ms, wakeActorPublish={_diagnosticWakeActorPublishMs:F3}ms, " +
                $"wakeObserverPublish={_diagnosticWakeObserverPublishMs:F3}ms, wakeSchedule={_diagnosticWakeScheduleMs:F3}ms, " +
                $"portal={_diagnosticPortalMs:F3}ms, catchUp={_diagnosticCatchUpMs:F3}ms/{_diagnosticCatchUpHops}hops, " +
                $"wakeCells={_diagnosticWakeCells}, wakeActors={_diagnosticWakeActorChecks}, " +
                $"origins={_diagnosticWakeOrigins}, candidates={_diagnosticWakeCandidates}, " +
                $"admitted={_diagnosticWakeAdmitted}, hibernating={_hibernatingCount}, " +
                $"dueQueueEntries={_dueSchedule.Count} (includes stale entries).");
        }

        public float EngagedDistance { get; set; } = 28f;
        public float ActiveDistance { get; set; } = 80f;
        // <= 0 retains the service default. The host binds this to AOI exit range.
        // Only peaceful routed civilian Population uses the narrower Active band.
        public float CivilianActiveDistance { get; set; }
        // Standalone GameServer may wire its existing CombatPresentationRange here.
        // <= 0 keeps legacy behavior by falling back to ActiveDistance.
        // This gate is intentionally FreeRoam-only; routed Population/NPC LOD is unchanged.
        public float FreeRoamActiveDistance { get; set; }
        public float CoarseDistance { get; set; } = 250f;
        public float VisibilitySuppressionDistance { get; set; } = 35f;
        public int MaximumPortalRespawnsPerTick { get; set; } = 8;

        public int MaximumAmbientWakeCatchUpHops { get; set; } = 24;
        // True simulation LOD cadence. Far/logical civilians are scheduled by next-due
        // time instead of being revisited every AI tick. A short 2s logical ceiling keeps
        // promotion latency bounded when a player approaches.
        public double EngagedCadenceSeconds { get; set; } = 0.10d;
        public double ActiveCadenceSeconds { get; set; } = 0.25d;
        public double CoarseCadenceSeconds { get; set; } = 1.00d;
        public double LogicalCadenceSeconds { get; set; } = 2.00d;
        public double DormantCadenceSeconds { get; set; } = 5.00d;
        public double DeadDecaySeconds { get; set; } = 60.00d;
        public float PlayerActivationDistance { get; set; } = 96f;
        public float PlayerHibernateDistance { get; set; } = 128f;
        public double ActivationHeartbeatSeconds { get; set; } = 1.00d;
        public double MaximumLogicalCatchUpSeconds { get; set; } = 21600d;
        public int MaximumLogicalCatchUpHops { get; set; } = 512;
        public float DefaultFreeRoamRadius { get; set; } = 20f;
        public float DefaultFreeRoamLeashRadius { get; set; } = 35f;
        public double FreeRoamMinimumPauseSeconds { get; set; } = 0.50d;
        public double FreeRoamMaximumPauseSeconds { get; set; } = 2.00d;
        public int Count => _population.Count;
        public int HibernatingCount => _hibernatingCount;
        public int SpatiallyActiveCount => Math.Max(0, _population.Count - _hibernatingCount);
        public int ActiveAmbientCount
        {
            get
            {
                int count = 0;
                foreach (PopulationActorRuntime pop in _population.Values)
                    if (IsActiveAmbient(pop)) ++count;
                return count;
            }
        }

        private static bool IsActiveAmbient(PopulationActorRuntime pop) =>
            pop?.Actor != null && pop.Actor.Alive &&
            pop.SpawnKind == ServerSpawnKind.Population &&
            !pop.AwaitingPlayerActivation &&
            pop.AiState != PopulationAiState.PortalDormant &&
            pop.AiState != PopulationAiState.Dead;

        public event Action<PopulationActorRuntime> Added;
        public event Action<PopulationActorRuntime> Changed;
        public event Action<PopulationActorRuntime> Removed;
        public event Action<PopulationActorRuntime> FightIntent;

        public PopulationSimulationService(
            ServerMapCatalog maps,
            AuthoritativeActorRegistry actors,
            WorldInteractableService worldObjects)
        {
            _maps = maps ?? throw new ArgumentNullException(nameof(maps));
            _actors = actors ?? throw new ArgumentNullException(nameof(actors));
            _worldObjects = worldObjects;
            BuildGraphs();
            SpawnBakedPopulation();
        }

        public IEnumerable<PopulationActorRuntime> All => _population.Values;

        public bool TryGet(long actorId, out PopulationActorRuntime actor) => _population.TryGetValue(actorId, out actor);

        public void Tick(float fixedDelta, double now, IReadOnlyList<PopulationPlayerView> players)
        {
            if (_populationSchedule.Count == 0 || fixedDelta <= 0f)
                return;

            TickBudgeted(fixedDelta, now, players, _populationSchedule.Count);
        }

        /// <summary>
        /// Processes a bounded rotating slice of Population actors. This is the standalone
        /// server scheduling boundary: AI load can degrade in cadence under crowd pressure
        /// without allowing one Population pass to monopolize the authoritative frame.
        /// </summary>
        public int TickBudgeted(
            float fixedDelta,
            double now,
            IReadOnlyList<PopulationPlayerView> players,
            int maxActors)
        {
            if (fixedDelta <= 0f || _populationSchedule.Count == 0 || maxActors <= 0)
                return 0;

            PrepareBudgetedTick(players, now);
            // Compatibility path: preparation and activation do not count as
            // simulated actors. Consume at most two dedicated work units here.
            if (_hibernatingCount > 0 && now + 0.000001d >= _nextActivationHeartbeatAt)
            {
                if (_activationHandoffPending)
                    TickNextBudgeted(fixedDelta, now);
                if (_activationHandoffReady)
                    TickNextBudgeted(fixedDelta, now);
            }
            int processed = 0;
            int toProcess = Math.Min(maxActors, _populationSchedule.Count);
            while (processed < toProcess && TickNextBudgeted(fixedDelta, now))
                processed++;
            return processed;
        }

        /// <summary>
        /// Prepares the observer/partition cache once for a scheduler AI tick. The host then
        /// calls TickNextBudgeted one actor at a time so the core scheduler can enforce its
        /// frame and channel CPU budgets between Population work units.
        /// </summary>
        public void PrepareBudgetedTick(IReadOnlyList<PopulationPlayerView> players, double now)
        {
            // Preparation can overrun independently; do not report attribution
            // left behind by the preceding actor work unit.
            _diagnosticActorId = 0;
            _diagnosticActorMs = 0d;
            _diagnosticGcPauseMs = 0d;
            _diagnosticActorAllocatedBytes = 0L;
            _diagnosticGcGen0Collections = 0;
            _diagnosticGcGen1Collections = 0;
            _diagnosticGcGen2Collections = 0;
            _diagnosticDueQueueMs = 0d;
            _diagnosticDueQueueChecks = 0;
            _diagnosticActorSetupMs = 0d;
            _diagnosticRescheduleMs = 0d;
            _diagnosticStaleScheduleHeadsPruned = 0;
            _diagnosticPartitionRebuildMs = 0d;
            _diagnosticPartitionRebuilt = false;
            _diagnosticPreparedPlayers = players?.Count ?? 0;
            _diagnosticDistanceMs = 0d;
            _diagnosticMotorCollisionMs = 0d;
            _diagnosticActiveGraphMs = _diagnosticActiveEnsureNodeMs = 0d;
            _diagnosticActiveRouteSetupMs = _diagnosticActiveTargetMs = 0d;
            _diagnosticActiveAmbientMs = _diagnosticActiveFallbackMs = 0d;
            _diagnosticDormantGraphMs = _diagnosticDormantProximityMs = 0d;
            _diagnosticDormantAdmissionMs = _diagnosticDormantSpawnClearMs = 0d;
            _diagnosticDormantActivateMs = 0d;
            _diagnosticActorPublishMs = 0d;
            _diagnosticObserverPresentationMs = 0d;
            _diagnosticMotorCalls = 0;
            _diagnosticAmbientCollisionMs = 0d;
            _diagnosticAmbientSamples = 0;
            _diagnosticAmbientFallbacks = 0;
            _diagnosticRouteBindMs = _diagnosticRouteTravelMs = _diagnosticRouteArrivalMs = 0d;
            _diagnosticRouteRecoveryMs = _diagnosticRouteSelectMs = 0d;
            _diagnosticRouteBindings = _diagnosticStuckChecks = _diagnosticRouteReattachments = 0;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                PrepareBudgetedTickCore(players, now);
            }
            finally
            {
                _diagnosticPrepareMs = ElapsedDiagnosticMilliseconds(startedAt);
                _diagnosticPhase = SlowWorkPhase.Handoff;
                ReportSlowPopulationWork(now, startedAt, "prepare");
            }
        }

        private void PrepareBudgetedTickCore(IReadOnlyList<PopulationPlayerView> players, double now)
        {
            // No observer => no partition bookkeeping or activation handoff.
            // Scheduled death/decay upkeep remains independent of this snapshot.
            if (players == null || players.Count == 0)
            {
                if (_playersByPartition.Count > 0)
                    _playersByPartition.Clear();
                if (_lastPreparedPlayerViews.Count > 0)
                    _lastPreparedPlayerViews.Clear();
                _activationHandoffPending = false;
                _activationHandoffReady = false;
            }
            else
            {
                // Preserve the previous partition across an in-flight handoff.
                // In ordinary ticks rebuild ONLY when the exact observer snapshot
                // has changed; never hide real player movement behind a tolerance.
                if (!_activationHandoffReady &&
                    (_playersByPartition.Count == 0 || PlayerViewsChanged(players)))
                {
                    if (!TryRefreshSmallPlayerPartition(players))
                    {
                        long partitionStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                        BuildPlayerPartitions(players);
                        CachePreparedPlayerViews(players);
                        _diagnosticPartitionRebuildMs = ElapsedDiagnosticMilliseconds(partitionStartedAt);
                        _diagnosticPartitionRebuilt = true;
                    }
                }
                if (_hibernatingCount <= 0)
                    _activationHandoffPending = _activationHandoffReady = false;
                else if (now + 0.000001d >= _nextActivationHeartbeatAt &&
                         !_activationHandoffPending && !_activationHandoffReady)
                    _activationHandoffPending = true;
            }
            _portalRespawnsThisTick = 0;
            _portalAdmissionGeneration = _portalAdmissionGeneration == uint.MaxValue
                ? 1u
                : _portalAdmissionGeneration + 1u;

            // Only refresh the shared observer partitions in this preparation phase.
            // Do NOT admit Population here. The host uses the existing scheduler
            // preparation interface, allowing an expensive preparation to yield before
            // actor work. Activation consumes a separate TickNextBudgeted work unit
            // at its own heartbeat cadence.
        }

        // Allows the host to service genuine due actor work with zero observers,
        // without running the dormant AOI activation heartbeat.
        public bool HasDueScheduledActorWork(double now)
        {
            PruneStaleScheduleHeads();
            return _dueSchedule.TryPeek(out _, out double due) && due <= now + 0.000001d;
        }

        public bool HasDueWork(double now)
        {
            if (HasDueScheduledActorWork(now))
                return true;

            // One lightweight shared heartbeat is enough to discover player proximity for all
            // hibernated Population. No dormant Pop receives a timer or simulation tick.
            return _hibernatingCount > 0 && now + 0.000001d >= _nextActivationHeartbeatAt;
        }

        public bool TickNextBudgeted(float fixedDelta, double now)
        {
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _diagnosticPhase = SlowWorkPhase.None;
            _diagnosticActorId = 0;
            _diagnosticActorMs = 0d;
            _diagnosticGcPauseMs = 0d;
            _diagnosticActorAllocatedBytes = 0L;
            _diagnosticGcGen0Collections = 0;
            _diagnosticGcGen1Collections = 0;
            _diagnosticGcGen2Collections = 0;
            _diagnosticDueQueueMs = 0d;
            _diagnosticDueQueueChecks = 0;
            _diagnosticActorSetupMs = 0d;
            _diagnosticRescheduleMs = 0d;
            _diagnosticStaleScheduleHeadsPruned = 0;
            _diagnosticMotorCollisionMs = 0d;
            _diagnosticActiveGraphMs = _diagnosticActiveEnsureNodeMs = 0d;
            _diagnosticActiveRouteSetupMs = _diagnosticActiveTargetMs = 0d;
            _diagnosticActiveAmbientMs = _diagnosticActiveFallbackMs = 0d;
            _diagnosticDormantGraphMs = _diagnosticDormantProximityMs = 0d;
            _diagnosticDormantAdmissionMs = _diagnosticDormantSpawnClearMs = 0d;
            _diagnosticDormantActivateMs = 0d;
            _diagnosticActorPublishMs = 0d;
            _diagnosticObserverPresentationMs = 0d;
            _diagnosticMotorCalls = 0;
            _diagnosticAmbientCollisionMs = 0d;
            _diagnosticAmbientSamples = 0;
            _diagnosticAmbientFallbacks = 0;
            _diagnosticRouteBindMs = _diagnosticRouteTravelMs = _diagnosticRouteArrivalMs = 0d;
            _diagnosticRouteRecoveryMs = _diagnosticRouteSelectMs = 0d;
            _diagnosticRouteBindings = _diagnosticStuckChecks = _diagnosticRouteReattachments = 0;
            _diagnosticDistanceMs = 0d;
            _diagnosticWakeScanMs = 0d;
            _diagnosticWakeAdmissionMs = 0d;
            _diagnosticWakeSpatialMs = 0d;
            _diagnosticWakeActorPublishMs = 0d;
            _diagnosticWakeObserverPublishMs = 0d;
            _diagnosticWakeScheduleMs = 0d;
            _diagnosticPortalMs = 0d;
            _diagnosticCatchUpMs = 0d;
            _diagnosticCatchUpHops = 0;
            _diagnosticWakeCells = 0;
            _diagnosticWakeActorChecks = 0;
            _diagnosticWakeOrigins = 0;
            _diagnosticWakeCandidates = 0;
            _diagnosticWakeAdmitted = 0;
            try
            {
                return TickNextBudgetedCore(fixedDelta, now);
            }
            finally
            {
                ReportSlowPopulationWork(now, startedAt, "work");
                _diagnosticPrepareMs = 0d;
                _diagnosticPartitionRebuildMs = 0d;
                _diagnosticPartitionRebuilt = false;
            }
        }

        private bool TickNextBudgetedCore(float fixedDelta, double now)
        {
            if (fixedDelta <= 0f || _populationSchedule.Count == 0)
                return false;

            // Preserve the staged activation handoff for direct service callers too:
            // preparing proximity never also admits an actor in the same service unit.
            if (_activationHandoffPending)
            {
                _diagnosticPhase = SlowWorkPhase.Handoff;
                _activationHandoffPending = false;
                _activationHandoffReady = true;
                return true;
            }

            // The next AI work unit performs ONLY the bounded activation scan.
            // If that unit is deferred to the next AI tick, preparation reuses
            // the existing partitions rather than combining the costs again.
            if (_activationHandoffReady)
            {
                _diagnosticPhase = SlowWorkPhase.WakeScan;
                _activationHandoffReady = false;
                if (_hibernatingCount > 0 && now + 0.000001d >= _nextActivationHeartbeatAt)
                {
                    WakeHibernatedNearPlayers(now);
                    // Briefly accelerate only while candidates are being resumed
                    // or there is an incomplete scan. No new heartbeat or timer.
                    _nextActivationHeartbeatAt = now + (_wakeScanPending
                        ? 0.20d
                        : Math.Max(0.25d, ActivationHeartbeatSeconds));
                }
                return true;
            }

            _diagnosticPhase = SlowWorkPhase.DueQueue;
            long dueQueueStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            int staleHeadsPruned = PruneStaleScheduleHeads();
            _diagnosticStaleScheduleHeadsPruned += staleHeadsPruned;
            _diagnosticDueQueueChecks++;
            if (!_dueSchedule.TryPeek(out PopulationScheduleEntry scheduled, out double due) ||
                due > now + 0.000001d)
            {
                _diagnosticDueQueueMs += ElapsedDiagnosticMilliseconds(dueQueueStartedAt);
                return false;
            }

            _dueSchedule.Dequeue();
            PopulationActorRuntime pop = scheduled.Actor;
            _diagnosticDueQueueMs += ElapsedDiagnosticMilliseconds(dueQueueStartedAt);
            if (pop == null || pop.Actor == null || scheduled.Version != pop.ScheduleVersion)
                return true;

            bool dead = !pop.Actor.Alive || pop.AiState == PopulationAiState.Dead;
            if (dead && pop.DeadDecayAt > 0d && now >= pop.DeadDecayAt)
            {
                _diagnosticPhase = SlowWorkPhase.DeadDecay;
                _diagnosticActorId = pop.Actor.Handle.actorId;
                RemovePopulation(pop, now);
                return true;
            }

            long actorSetupStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            float actorDelta = fixedDelta;
            if (pop.LastSimulationAt > 0d && now > pop.LastSimulationAt)
                actorDelta = (float)Math.Min(2d, Math.Max(fixedDelta, now - pop.LastSimulationAt));
            pop.LastSimulationAt = now;

            IReadOnlyList<PopulationPlayerView> partitionPlayers = GetPartitionPlayers(pop);
            _diagnosticActorId = pop.Actor.Handle.actorId;
            _diagnosticActorSetupMs = ElapsedDiagnosticMilliseconds(actorSetupStartedAt);
            TimeSpan gcPauseBeforeActor = GC.GetTotalPauseDuration();
            long allocatedBeforeActor = GC.GetTotalAllocatedBytes(precise: false);
            int gen0BeforeActor = GC.CollectionCount(0);
            int gen1BeforeActor = GC.CollectionCount(1);
            int gen2BeforeActor = GC.CollectionCount(2);
            long actorStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            TickActor(pop, actorDelta, now, partitionPlayers);
            _diagnosticActorMs = ElapsedDiagnosticMilliseconds(actorStartedAt);
            _diagnosticGcPauseMs = Math.Max(0d,
                (GC.GetTotalPauseDuration() - gcPauseBeforeActor).TotalMilliseconds);
            _diagnosticActorAllocatedBytes = Math.Max(0L,
                GC.GetTotalAllocatedBytes(precise: false) - allocatedBeforeActor);
            _diagnosticGcGen0Collections = Math.Max(0,
                GC.CollectionCount(0) - gen0BeforeActor);
            _diagnosticGcGen1Collections = Math.Max(0,
                GC.CollectionCount(1) - gen1BeforeActor);
            _diagnosticGcGen2Collections = Math.Max(0,
                GC.CollectionCount(2) - gen2BeforeActor);

            if (pop.AwaitingPlayerActivation)
                return true;

            long rescheduleStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (!pop.Actor.Alive || pop.AiState == PopulationAiState.Dead)
                {
                    if (pop.DeadDecayAt <= 0d)
                        pop.DeadDecayAt = now + Math.Max(1d, DeadDecaySeconds);
                    Schedule(pop, pop.DeadDecayAt);
                }
                else if (pop.AiState == PopulationAiState.PortalDormant && pop.DormantUntil > now)
                {
                    Schedule(pop, pop.DormantUntil);
                }
                else
                {
                    Schedule(pop, now + GetSimulationCadence(pop));
                }
            }
            finally
            {
                _diagnosticRescheduleMs = ElapsedDiagnosticMilliseconds(rescheduleStartedAt);
            }
            return true;
        }

        private int PruneStaleScheduleHeads()
        {
            int removed = 0;
            while (_dueSchedule.TryPeek(out PopulationScheduleEntry scheduled, out _))
            {
                PopulationActorRuntime pop = scheduled.Actor;
                if (pop != null && pop.Actor != null && scheduled.Version == pop.ScheduleVersion)
                    return removed;
                _dueSchedule.Dequeue();
                if (++removed >= MaxStaleSchedulePrunesPerCall)
                    break;
            }
            return removed;
        }

        private void Schedule(PopulationActorRuntime pop, double dueAt)
        {
            if (pop == null || pop.Actor == null)
                return;
            pop.ScheduleVersion = pop.ScheduleVersion == uint.MaxValue ? 1u : pop.ScheduleVersion + 1u;
            pop.NextSimulationAt = dueAt;
            _dueSchedule.Enqueue(new PopulationScheduleEntry(pop, pop.ScheduleVersion), dueAt);
        }

        private void ScheduleNow(PopulationActorRuntime pop, double now) => Schedule(pop, now);

        private double GetSimulationCadence(PopulationActorRuntime pop)
        {
            return pop.SimulationLod switch
            {
                PopulationSimulationLod.Engaged => Math.Max(0.05d, EngagedCadenceSeconds),
                PopulationSimulationLod.Active => Math.Max(0.10d, ActiveCadenceSeconds),
                PopulationSimulationLod.CoarseRoute => Math.Max(0.25d, CoarseCadenceSeconds),
                PopulationSimulationLod.Logical => Math.Max(0.50d, LogicalCadenceSeconds),
                _ => Math.Max(1.0d, DormantCadenceSeconds),
            };
        }

        private void TickActor(
            PopulationActorRuntime pop,
            float fixedDelta,
            double now,
            IReadOnlyList<PopulationPlayerView> players)
        {
            _diagnosticPhase = SlowWorkPhase.ActorDistance;
            long distanceStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            float nearestPlayerSq = pop.Actor.Alive &&
                pop.AiState != PopulationAiState.PortalDormant
                    ? GetNearestPlayerDistanceSquared(pop, players)
                    : float.PositiveInfinity;
            UpdateSimulationLod(pop, nearestPlayerSq);
            _diagnosticDistanceMs = ElapsedDiagnosticMilliseconds(distanceStartedAt);

            // Canonical Population death is terminal for normal simulation until an explicit
            // future decay/respawn policy changes it. Do not let dormant/logical route ticking
            // overwrite Dead movement state and make a defeated Pop appear to respawn.
            if (!pop.Actor.Alive || pop.AiState == PopulationAiState.Dead)
            {
                _diagnosticPhase = SlowWorkPhase.ActorDead;
                pop.Actor.MovementMode = ActorMovementMode.Dead;
                pop.Actor.VelocityX = 0f;
                pop.Actor.VelocityY = 0f;
                pop.Actor.VelocityZ = 0f;
                pop.AiState = PopulationAiState.Dead;
                if (pop.MotorState != null)
                    pop.MotorState.Mode = ActorMovementMode.Dead;
                return;
            }

            if (pop.AiState == PopulationAiState.PortalDormant)
            {
                _diagnosticPhase = SlowWorkPhase.ActorDormant;
                TickDormant(pop, now, players);
                return;
            }

            if (ShouldHibernateForPlayerDistance(pop, nearestPlayerSq))
            {
                _diagnosticPhase = SlowWorkPhase.ActorHibernate;
                EnterAoiHibernate(pop, now);
                return;
            }

            if (pop.ThreatUntil > now)
            {
                _diagnosticPhase = SlowWorkPhase.ActorThreat;
                TickThreat(pop, Math.Min(fixedDelta, 0.25f), now);
                return;
            }
            if (pop.AiState == PopulationAiState.Fleeing || pop.AiState == PopulationAiState.Fighting)
            {
                _diagnosticPhase = SlowWorkPhase.ActorRecovery;
                pop.RouteReason = PopulationRouteReason.Recovery;
                if (pop.MovementBehavior == SharedAiMovementMode.Route)
                {
                    pop.AiState = PopulationAiState.Recovering;
                    ReattachToNearestRoute(pop);
                }
                else
                {
                    pop.AiState = PopulationAiState.Idle;
                    pop.HasRoamTarget = false;
                    pop.NextRoamDecisionAt = now;
                }
            }

            if (pop.WaitUntil > now)
            {
                _diagnosticPhase = SlowWorkPhase.ActorWaiting;
                pop.AiState = PopulationAiState.Waiting;
                return;
            }

            if (pop.PortalPhase != PopulationPortalSequencePhase.None)
            {
                _diagnosticPhase = SlowWorkPhase.PortalExit;
                TickPortalExitSequence(pop, Math.Min(fixedDelta, 0.25f), now);
                return;
            }

            if (pop.MovementBehavior == SharedAiMovementMode.FreeRoam)
            {
                // FreeRoam has a tighter presentation-aware movement gate than routed actors.
                // The standalone GameServer wires its existing CombatPresentationRange here
                // (36m by default). No new distance source is introduced. If not wired, fall
                // back to the existing ActiveDistance for isolated service/Unity tests.
                float freeRoamDistance = FreeRoamActiveDistance > 0f
                    ? Math.Min(FreeRoamActiveDistance, ActiveDistance)
                    : ActiveDistance;
                if (nearestPlayerSq <= freeRoamDistance * freeRoamDistance)
                {
                    _diagnosticPhase = SlowWorkPhase.FreeRoamMotor;
                    TickFreeRoam(pop, Math.Min(fixedDelta, 0.25f), now);
                }
                else
                {
                    _diagnosticPhase = SlowWorkPhase.Stationary;
                    TickStationary(pop);
                }
                return;
            }

            if (pop.MovementBehavior == SharedAiMovementMode.Stationary)
            {
                _diagnosticPhase = SlowWorkPhase.Stationary;
                TickStationary(pop);
                return;
            }

            if (pop.SimulationLod <= PopulationSimulationLod.Logical)
            {
                _diagnosticPhase = SlowWorkPhase.LogicalRoute;
                TickLogical(pop, fixedDelta, now);
            }
            else if (pop.SimulationLod == PopulationSimulationLod.CoarseRoute)
            {
                _diagnosticPhase = SlowWorkPhase.CoarseRoute;
                TickCoarse(pop, fixedDelta, now);
            }
            else
            {
                _diagnosticPhase = SlowWorkPhase.ActiveMotor;
                TickActive(pop, Math.Min(fixedDelta, 0.25f), now);
            }
        }

        private bool PlayerViewsChanged(IReadOnlyList<PopulationPlayerView> players)
        {
            if (_lastPreparedPlayerViews.Count != players.Count)
                return true;
            // Order changes trigger a safe rebuild. Nothing is inferred: no
            // position rounding, no position prediction, no stale AOI locations.
            for (int i = 0; i < players.Count; ++i)
            {
                PopulationPlayerView current = players[i];
                PopulationPlayerView previous = _lastPreparedPlayerViews[i];
                if (current.CharacterId != previous.CharacterId ||
                    current.Alive != previous.Alive ||
                    !string.Equals(current.MapId, previous.MapId, StringComparison.Ordinal) ||
                    !string.Equals(current.InstanceId, previous.InstanceId, StringComparison.Ordinal) ||
                    current.Position.X != previous.Position.X ||
                    current.Position.Y != previous.Position.Y ||
                    current.Position.Z != previous.Position.Z)
                    return true;
            }
            return false;
        }

        // For one small partition, movement-only snapshots can refresh exact values
        // in place. Structural changes and indexed/crowded partitions still rebuild.
        private bool TryRefreshSmallPlayerPartition(IReadOnlyList<PopulationPlayerView> players)
        {
            if (players == null || players.Count == 0 || players.Count > 8 ||
                players.Count != _lastPreparedPlayerViews.Count)
                return false;

            PlayerSpatialPartition partition = null;
            foreach (PlayerSpatialPartition candidate in _playersByPartition.Values)
            {
                if (partition != null)
                    return false;
                partition = candidate;
            }

            if (partition == null || partition.Players.Count != players.Count ||
                partition.ByCharacterId.Count != players.Count || partition.Cells.Count != 0)
                return false;

            for (int i = 0; i < players.Count; ++i)
            {
                PopulationPlayerView current = players[i];
                PopulationPlayerView previous = _lastPreparedPlayerViews[i];
                if (current.CharacterId <= 0 || current.CharacterId != previous.CharacterId ||
                    !string.Equals(current.MapId, previous.MapId, StringComparison.Ordinal) ||
                    !string.Equals(current.InstanceId, previous.InstanceId, StringComparison.Ordinal) ||
                    partition.Players[i].CharacterId != current.CharacterId ||
                    !partition.ByCharacterId.ContainsKey(current.CharacterId))
                    return false;
            }

            for (int i = 0; i < players.Count; ++i)
            {
                PopulationPlayerView current = players[i];
                partition.Players[i] = current;
                partition.ByCharacterId[current.CharacterId] = current;
                _lastPreparedPlayerViews[i] = current;
            }
            return true;
        }

        private void CachePreparedPlayerViews(IReadOnlyList<PopulationPlayerView> players)
        {
            _lastPreparedPlayerViews.Clear();
            for (int i = 0; i < players.Count; ++i)
                _lastPreparedPlayerViews.Add(players[i]);
        }

        private void BuildPlayerPartitions(IReadOnlyList<PopulationPlayerView> players)
        {
            foreach (PlayerSpatialPartition partition in _playersByPartition.Values)
                partition.Reset();

            if (players == null)
                return;

            for (int i = 0; i < players.Count; ++i)
            {
                PopulationPlayerView player = players[i];
                string key = MapKey(player.MapId, player.InstanceId);
                if (!_playersByPartition.TryGetValue(key, out PlayerSpatialPartition partition))
                {
                    partition = new PlayerSpatialPartition();
                    _playersByPartition.Add(key, partition);
                }
                partition.Add(player);
            }
        }

        private IReadOnlyList<PopulationPlayerView> GetPartitionPlayers(PopulationActorRuntime pop)
        {
            AuthoritativeActorRuntime actor = pop?.Actor;
            if (actor != null &&
                _playersByPartition.TryGetValue(
                    GetCachedPartitionKey(pop),
                    out PlayerSpatialPartition partition))
            {
                return partition.Players;
            }
            return Array.Empty<PopulationPlayerView>();
        }

        private float GetNearestPlayerDistanceSquared(
            PopulationActorRuntime pop,
            IReadOnlyList<PopulationPlayerView> players)
        {
            AuthoritativeActorRuntime actor = pop?.Actor;
            if (actor == null || players == null || players.Count == 0)
                return float.PositiveInfinity;

            float searchDistance = Math.Max(1f, CoarseDistance);
            float coarseSq = searchDistance * searchDistance;

            // Most maps/instances have only a handful of connected players.
            // A direct scan avoids probing up to 81 mostly empty spatial cells
            // per Population tick at the default 250 m coarse range. For
            // crowded instances keep the existing indexed spatial lookup.
            if (players.Count <= 8)
            {
                float nearest = float.PositiveInfinity;
                for (int i = 0; i < players.Count; ++i)
                {
                    PopulationPlayerView player = players[i];
                    float x = actor.Position.X - player.Position.X;
                    float z = actor.Position.Z - player.Position.Z;
                    float sq = x * x + z * z;
                    if (sq < nearest)
                    {
                        nearest = sq;
                        if (nearest <= 0.0001f)
                            break;
                    }
                }
                return nearest <= coarseSq ? nearest : float.PositiveInfinity;
            }

            // Larger crowds still use the canonical per-map player-cell index.
            if (!_playersByPartition.TryGetValue(
                    GetCachedPartitionKey(pop),
                    out PlayerSpatialPartition partition))
                return float.PositiveInfinity;

            int radius = Math.Max(1, (int)Math.Ceiling(searchDistance / PlayerSpatialCellSize));
            int centerX = ToPlayerCell(actor.Position.X);
            int centerZ = ToPlayerCell(actor.Position.Z);
            float nearestSq = float.PositiveInfinity;

            for (int dz = -radius; dz <= radius; ++dz)
            {
                for (int dx = -radius; dx <= radius; ++dx)
                {
                    if (!partition.Cells.TryGetValue(
                            PackPlayerCell(centerX + dx, centerZ + dz),
                            out List<PopulationPlayerView> cell))
                    {
                        continue;
                    }

                    for (int i = 0; i < cell.Count; ++i)
                    {
                        PopulationPlayerView player = cell[i];
                        float px = actor.Position.X - player.Position.X;
                        float pz = actor.Position.Z - player.Position.Z;
                        float sq = px * px + pz * pz;
                        if (sq < nearestSq)
                        {
                            nearestSq = sq;
                            if (nearestSq <= 0.0001f)
                                return nearestSq;
                        }
                    }
                }
            }

            return nearestSq <= coarseSq ? nearestSq : float.PositiveInfinity;
        }

        // A grouped wake cell may contain several players. Test eligibility against
        // all players in this map/instance using the EXISTING 64 m spatial cells;
        // do not use only the representative player's position.
        private static bool HasWakeObserverWithin(
            PlayerSpatialPartition partition,
            WorldPosition candidate,
            float activationSquared,
            int playerCellRadius)
        {
            if (partition == null || partition.Players.Count == 0)
                return false;

            // The same public actor positions determine eligibility in either
            // lookup path. Avoid many empty-cell dictionary probes in small
            // instances; retain indexed traversal for dense crowds.
            if (partition.Players.Count <= 8)
            {
                for (int i = 0; i < partition.Players.Count; ++i)
                {
                    PopulationPlayerView player = partition.Players[i];
                    float x = candidate.X - player.Position.X;
                    float z = candidate.Z - player.Position.Z;
                    if (x * x + z * z <= activationSquared)
                        return true;
                }
                return false;
            }

            int centerX = ToPlayerCell(candidate.X);
            int centerZ = ToPlayerCell(candidate.Z);
            for (int dz = -playerCellRadius; dz <= playerCellRadius; ++dz)
            {
                for (int dx = -playerCellRadius; dx <= playerCellRadius; ++dx)
                {
                    if (!partition.Cells.TryGetValue(
                            PackPlayerCell(centerX + dx, centerZ + dz),
                            out List<PopulationPlayerView> cell))
                        continue;

                    for (int i = 0; i < cell.Count; ++i)
                    {
                        PopulationPlayerView player = cell[i];
                        float x = candidate.X - player.Position.X;
                        float z = candidate.Z - player.Position.Z;
                        if (x * x + z * z <= activationSquared)
                            return true;
                    }
                }
            }
            return false;
        }

        private static int ToPlayerCell(float value) =>
            (int)MathF.Floor(value / PlayerSpatialCellSize);

        private static long PackPlayerCell(int x, int z) =>
            ((long)x << 32) ^ (uint)z;

        public bool NotifyThreat(long populationActorId, WorldPosition threatPosition, float severity, double now)
        {
            if (!_population.TryGetValue(populationActorId, out PopulationActorRuntime pop) || !pop.Actor.Alive)
                return false;

            if (pop.AwaitingPlayerActivation)
                WakeImmediately(pop, now, catchUp: false);

            severity = Math.Clamp(severity, 0f, 1f);
            pop.ThreatPosition = threatPosition;
            pop.ThreatUntil = now + 5d + severity * 8d;
            // External threats wake even a logical/far actor immediately instead of waiting
            // for its coarse next-due cadence.
            ScheduleNow(pop, now);
            float fightScore = pop.Aggression * 0.55f + pop.Courage * 0.30f + pop.CombatSkill * 0.15f;
            float fleeScore = (1f - pop.Courage) * 0.65f + (1f - pop.Aggression) * 0.25f + severity * 0.10f;
            if (fightScore > fleeScore && pop.Actor.HealthCurrent > pop.Actor.HealthMaximum * 0.35f)
            {
                pop.AiState = PopulationAiState.Fighting;
                pop.RouteReason = PopulationRouteReason.Pursuing;
                FightIntent?.Invoke(pop);
            }
            else
            {
                pop.AiState = PopulationAiState.Fleeing;
                pop.RouteReason = PopulationRouteReason.Fleeing;
            }
            pop.SimulationLod = PopulationSimulationLod.Engaged;
            return true;
        }

        public bool SynchronizeCombatHealth(long populationActorId, int current, int maximum)
        {
            if (!_population.TryGetValue(populationActorId, out PopulationActorRuntime pop) || pop?.Actor == null)
                return false;

            int sanitizedMaximum = Math.Max(1, maximum);
            int sanitizedCurrent = Math.Max(0, Math.Min(sanitizedMaximum, current));
            bool changed = pop.Actor.HealthMaximum != sanitizedMaximum ||
                           pop.Actor.HealthCurrent != sanitizedCurrent;

            pop.Actor.HealthMaximum = sanitizedMaximum;
            pop.Actor.HealthCurrent = sanitizedCurrent;
            if (sanitizedCurrent <= 0 && pop.Actor.Alive)
            {
                pop.Actor.Alive = false;
                pop.Actor.MovementMode = ActorMovementMode.Dead;
                pop.AiState = PopulationAiState.Dead;
                if (pop.MotorState != null)
                    pop.MotorState.Mode = ActorMovementMode.Dead;
                ArmDeadDecay(pop);
                changed = true;
            }

            if (!changed)
                return true;

            _actors.PublishChanged(pop.Actor);
            Changed?.Invoke(pop);
            return true;
        }

        public bool SetDead(long populationActorId)
        {
            if (!_population.TryGetValue(populationActorId, out PopulationActorRuntime pop) || pop?.Actor == null)
                return false;

            bool changed = pop.Actor.Alive ||
                           pop.Actor.HealthCurrent != 0 ||
                           pop.Actor.MovementMode != ActorMovementMode.Dead ||
                           pop.AiState != PopulationAiState.Dead;
            pop.Actor.Alive = false;
            pop.Actor.HealthCurrent = 0;
            pop.Actor.MovementMode = ActorMovementMode.Dead;
            pop.AiState = PopulationAiState.Dead;
            if (pop.MotorState != null)
                pop.MotorState.Mode = ActorMovementMode.Dead;
            ArmDeadDecay(pop);

            if (changed)
            {
                PublishPopulationChanged(pop);
            }
            return true;
        }

        private void ArmDeadDecay(PopulationActorRuntime pop)
        {
            if (pop == null || pop.Actor == null || pop.DeadDecayAt > 0d)
                return;

            // We intentionally establish the absolute deadline on the next scheduler work
            // item so it uses the scheduler's monotonic time domain. Scheduling at the
            // actor's last simulation time makes the dead actor immediately due without
            // introducing another clock or a polling loop.
            ScheduleNow(pop, Math.Max(0d, pop.LastSimulationAt));
        }

        private void RemovePopulation(PopulationActorRuntime pop, double now)
        {
            if (pop?.Actor == null)
                return;

            long actorId = pop.Actor.Handle.actorId;
            if (!_population.TryGetValue(actorId, out PopulationActorRuntime current) || !ReferenceEquals(current, pop))
                return;

            string partitionKey = MapKey(pop.Actor.MapId, pop.Actor.InstanceId);
            RemoveFromHibernationIndex(pop);
            _population.Remove(actorId);
            _populationSchedule.Remove(pop);
            pop.ScheduleVersion = pop.ScheduleVersion == uint.MaxValue ? 1u : pop.ScheduleVersion + 1u;
            _actors.Remove(pop.Actor.Handle);

            // Release the exact baked slot, never a destination portal's slot.
            if (_graphs.TryGetValue(partitionKey, out MapGraph graph))
            {
                graph.OccupiedSpawnAnchors.Remove(pop.SpawnAnchorId);
                if (graph.SpawnPortalByAnchor.Remove(pop.SpawnAnchorId, out long ownerPortalId) &&
                    graph.PortalOwnedCounts.TryGetValue(ownerPortalId, out int owned))
                {
                    if (owned <= 1)
                        graph.PortalOwnedCounts.Remove(ownerPortalId);
                    else
                        graph.PortalOwnedCounts[ownerPortalId] = owned - 1;
                }
            }

            // Subscribers synchronously retire presentation and transient death loot before a
            // new authoritative actor identity is created from this same authored spawn slot.
            Removed?.Invoke(pop);
            SpawnBakedPopulation(pop.SpawnAnchorId, partitionKey, now);
        }

        private void BuildGraphs()
        {
            foreach (ServerMapSnapshot map in _maps.Snapshots)
            {
                string key = MapKey(map.mapId, map.instanceId);
                var graph = new MapGraph { Map = map };
                _maps.TryGetCollisionWorld(map.mapId, map.instanceId, out graph.Collision);

                ServerPopulationRouteNode[] nodes = map.populationNodes ?? Array.Empty<ServerPopulationRouteNode>();
                for (int i = 0; i < nodes.Length; ++i)
                    if (nodes[i] != null && nodes[i].stableId > 0)
                        graph.Nodes[nodes[i].stableId] = nodes[i];

                ServerPopulationRouteEdge[] edges = map.populationEdges ?? Array.Empty<ServerPopulationRouteEdge>();
                for (int i = 0; i < edges.Length; ++i)
                {
                    ServerPopulationRouteEdge edge = edges[i];
                    if (edge == null || edge.fromNodeId <= 0 || edge.toNodeId <= 0 || !edge.validatedClear) continue;
                    AddEdge(graph, edge.fromNodeId, edge);
                    if (!edge.oneWay)
                    {
                        AddEdge(graph, edge.toNodeId, new ServerPopulationRouteEdge
                        {
                            fromNodeId = edge.toNodeId,
                            toNodeId = edge.fromNodeId,
                            oneWay = true,
                            width = edge.width,
                            weight = edge.weight,
                            minimumClearance = edge.minimumClearance,
                            validatedClear = edge.validatedClear,
                        });
                    }
                }

                ServerPopulationPortal[] portals = map.populationPortals ?? Array.Empty<ServerPopulationPortal>();
                for (int i = 0; i < portals.Length; ++i)
                {
                    ServerPopulationPortal portal = portals[i];
                    if (portal == null || portal.stableId <= 0) continue;
                    graph.Portals[portal.stableId] = portal;

                    // routeNodeId is the portal's authored primary arrival/despawn
                    // marker. routeNodeIds are alternate nearby route-entry links
                    // used when joining the route; treating those as exits made an
                    // actor disappear at incidental sidewalk intersections.
                    long nodeId = portal.routeNodeId;
                    if (nodeId <= 0 || !graph.Nodes.ContainsKey(nodeId))
                        continue;

                    if (!graph.PortalsByRouteNode.TryGetValue(nodeId, out List<ServerPopulationPortal> nodePortals))
                    {
                        nodePortals = new List<ServerPopulationPortal>(2);
                        graph.PortalsByRouteNode.Add(nodeId, nodePortals);
                    }
                    nodePortals.Add(portal);
                }
                CompileAmbientRouteSuccessors(graph);
                CertifyBakedRouteLanes(graph);
                CompileRouteSegments(graph);
                _graphs[key] = graph;
            }
        }

        private static void CompileAmbientRouteSuccessors(MapGraph graph)
        {
            if (graph == null)
                return;

            var incoming = new Dictionary<long, HashSet<long>>();
            foreach (KeyValuePair<long, List<ServerPopulationRouteEdge>> pair in graph.Outgoing)
            {
                List<ServerPopulationRouteEdge> edges = pair.Value;
                for (int i = 0; i < edges.Count; ++i)
                {
                    ServerPopulationRouteEdge edge = edges[i];
                    if (edge == null)
                        continue;
                    if (!incoming.TryGetValue(edge.toNodeId, out HashSet<long> previousNodes))
                    {
                        previousNodes = new HashSet<long>();
                        incoming.Add(edge.toNodeId, previousNodes);
                    }
                    previousNodes.Add(edge.fromNodeId);
                }
            }

            Array npcTypes = Enum.GetValues(typeof(PopulationNpcType));
            foreach (long nodeId in graph.Outgoing.Keys)
            {
                var previousNodes = new HashSet<long> { 0, nodeId };
                if (incoming.TryGetValue(nodeId, out HashSet<long> parents))
                    previousNodes.UnionWith(parents);

                foreach (long previousNodeId in previousNodes)
                    foreach (PopulationNpcType npcType in npcTypes)
                    {
                        ServerPopulationRouteEdge selected = SelectDeterministicAmbientSuccessor(
                            graph, nodeId, previousNodeId, npcType);
                        if (selected != null)
                            graph.AmbientRouteSuccessors[(nodeId, previousNodeId, npcType)] = selected;
                    }
            }
        }

        private static ServerPopulationRouteEdge SelectDeterministicAmbientSuccessor(
            MapGraph graph, long nodeId, long previousNodeId, PopulationNpcType npcType)
        {
            if (!graph.Outgoing.TryGetValue(nodeId, out List<ServerPopulationRouteEdge> edges))
                return null;

            ServerPopulationRouteEdge firstAllowed = null;
            ServerPopulationRouteEdge firstForward = null;
            for (int i = 0; i < edges.Count; ++i)
            {
                ServerPopulationRouteEdge edge = edges[i];
                if (!EdgeAllows(graph, edge, npcType))
                    continue;
                firstAllowed ??= edge;
                if (edge.toNodeId != previousNodeId)
                {
                    firstForward = edge;
                    break;
                }
            }

            // Keep the established no-immediate-U-turn rule at intersections.
            // Baked edge order is stable and the ambient successor is selected once
            // at map load; the per-actor arrival path only performs a dictionary read.
            return firstForward ?? firstAllowed;
        }

        private static void CompileRouteSegments(MapGraph graph)
        {
            foreach (List<ServerPopulationRouteEdge> edges in graph.Outgoing.Values)
                for (int i = 0; i < edges.Count; ++i)
                {
                    ServerPopulationRouteEdge edge = edges[i];
                    if (edge == null || !edge.validatedClear ||
                        graph.Segments.ContainsKey((edge.fromNodeId, edge.toNodeId)) ||
                        !graph.Nodes.TryGetValue(edge.fromNodeId, out ServerPopulationRouteNode from) ||
                        !graph.Nodes.TryGetValue(edge.toNodeId, out ServerPopulationRouteNode to))
                        continue;
                    float dx = to.pose.x - from.pose.x, dz = to.pose.z - from.pose.z;
                    float squared = dx * dx + dz * dz;
                    float length = MathF.Sqrt(squared);
                    var segment = new PopulationRouteSegment
                    {
                        From = from, To = to, Dx = dx, Dz = dz,
                        LengthSquared = squared, Length = length,
                        SideX = length > 0.001f ? -dz / length : 0f,
                        SideZ = length > 0.001f ? dx / length : 0f,
                        Yaw = NormalizeYaw(MathF.Atan2(dx, dz) * (180f / MathF.PI)),
                        LaneOffset = Math.Max(0f, edge.width * 0.35f),
                        HalfWidth = Math.Max(0f, edge.width * 0.5f),
                    };
                    for (int lane = -1; lane <= 1; ++lane)
                        if (graph.CertifiedLanes.Contains((edge.fromNodeId, edge.toNodeId, lane)))
                            segment.CertifiedLaneMask |= 1 << (lane + 1);
                    graph.Segments.Add((edge.fromNodeId, edge.toNodeId), segment);
                }
            // Certificates now live in the compiled segments, not a second runtime index.
            graph.CertifiedLanes.Clear();
            graph.CertifiedLanes.TrimExcess();
        }

        // Resolve only at assignment/transition. ID changes in combat, portal,
        // respawn or recovery automatically invalidate the actor's reference.
        private bool ResolveRouteAssignment(MapGraph graph, PopulationActorRuntime pop)
        {
            if (ReferenceEquals(pop.AssignedRouteGraph, graph) &&
                pop.AssignedFromId == pop.CurrentNodeId && pop.AssignedToId == pop.NextNodeId)
                return pop.AssignedRouteTarget != null;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                ++_diagnosticRouteBindings;
                pop.AssignedRouteGraph = graph;
                pop.AssignedFromId = pop.CurrentNodeId;
                pop.AssignedToId = pop.NextNodeId;
                graph.Nodes.TryGetValue(pop.NextNodeId, out pop.AssignedRouteTarget);
                graph.Segments.TryGetValue((pop.CurrentNodeId, pop.NextNodeId), out pop.AssignedRouteSegment);
                return pop.AssignedRouteTarget != null;
            }
            finally { _diagnosticRouteBindMs += ElapsedDiagnosticMilliseconds(startedAt); }
        }

        private bool TryGetAssignedGraph(PopulationActorRuntime pop, out MapGraph graph)
        {
            graph = pop.AssignedRouteGraph as MapGraph;
            if (graph != null &&
                string.Equals(graph.Map.mapId, pop.Actor.MapId, StringComparison.Ordinal) &&
                string.Equals(graph.Map.instanceId ?? string.Empty, pop.Actor.InstanceId, StringComparison.Ordinal))
                return true;
            return TryGetGraph(pop.Actor, out graph);
        }

        private static void RecordRouteAdvance(PopulationActorRuntime pop, double now)
        {
            // Direct validated rail travel cannot physically become stuck. Start
            // physical recovery accounting afresh if it later leaves this path.
            pop.LastProgressAt = now;
            pop.LastProgressPosition = pop.Actor.Position;
        }

        private static void AddEdge(MapGraph graph, long nodeId, ServerPopulationRouteEdge edge)
        {
            if (!graph.Outgoing.TryGetValue(nodeId, out List<ServerPopulationRouteEdge> list))
            {
                list = new List<ServerPopulationRouteEdge>();
                graph.Outgoing.Add(nodeId, list);
            }
            list.Add(edge);
        }

        private void SpawnBakedPopulation(long onlyAnchorId = 0, string onlyPartitionKey = null, double respawnAt = 0d)
        {
            foreach (MapGraph graph in _graphs.Values)
            {
                if (onlyAnchorId > 0 && !string.Equals(
                        MapKey(graph.Map.mapId, graph.Map.instanceId), onlyPartitionKey, StringComparison.Ordinal))
                    continue;

                ServerSpawnAnchor[] anchors = graph.Map.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();
                for (int i = 0; i < anchors.Length; ++i)
                {
                    ServerSpawnAnchor anchor = anchors[i];
                    if (anchor == null || !anchor.enabled || !IsSharedAiSpawnKind(anchor.kind) ||
                        (onlyAnchorId > 0 && anchor.stableId != onlyAnchorId) ||
                        graph.OccupiedSpawnAnchors.Contains(anchor.stableId))
                        continue;

                    ServerPopulationPortal owningPortal = null;
                    bool portalOwned = anchor.portalId > 0 &&
                        graph.Portals.TryGetValue(anchor.portalId, out owningPortal) &&
                        owningPortal.mode != PopulationPortalMode.DespawnOnly &&
                        PortalAllows(owningPortal, InferNpcType(anchor.archetypeId));
                    if (portalOwned &&
                        graph.PortalOwnedCounts.TryGetValue(owningPortal.stableId, out int existingOwnedCount) &&
                        existingOwnedCount >= Math.Max(1, owningPortal.maximumActiveInWorld))
                        continue;

                    if (!TryResolveSpawn(graph, anchor, out ServerPose spawnPose))
                        continue;

                    SharedAiMovementMode movementBehavior = ResolveMovementBehavior(graph, anchor);
                    PopulationNpcType npcType = InferNpcType(anchor.archetypeId);
                    PopulationBehaviorProfileData profile = DefaultProfile(npcType, anchor.kind);
                    string displayName = string.IsNullOrWhiteSpace(anchor.label)
                        ? $"{anchor.kind} {anchor.stableId}"
                        : anchor.label;

                    // Population/NPC/Monster are intentionally the same lightweight authoritative
                    // actor family. SpawnKind + MovementBehavior select their AI; keeping the
                    // canonical Population actor kind preserves the existing presentation,
                    // interaction, combat-promotion, loot, AOI and replication paths.
                    AuthoritativeActorRuntime actor = _actors.Create(
                        AuthoritativeActorKind.Population,
                        anchor.archetypeId,
                        displayName,
                        profile.faction,
                        graph.Map.mapId,
                        graph.Map.instanceId,
                        spawnPose.ToWorldPosition(),
                        spawnPose.yaw,
                        100,
                        profile.weightClass);

                    float capsuleRadius = Math.Max(0.2f, anchor.capsuleRadius);
                    float capsuleHeight = Math.Max(anchor.capsuleHeight, capsuleRadius * 2f);
                    var motorSettings = new CharacterMotorSettings
                    {
                        Radius = capsuleRadius,
                        Height = capsuleHeight,
                        WalkSpeed = profile.walkSpeed,
                        SprintSpeed = profile.runSpeed,
                    };
                    var pop = new PopulationActorRuntime
                    {
                        Actor = actor,
                        NpcType = npcType,
                        SpawnKind = anchor.kind,
                        MovementBehavior = movementBehavior,
                        SimulationLod = PopulationSimulationLod.CoarseRoute,
                        AiState = movementBehavior == SharedAiMovementMode.Route
                            ? PopulationAiState.FollowingRoute
                            : PopulationAiState.Idle,
                        RouteReason = PopulationRouteReason.Wander,
                        WalkSpeed = profile.walkSpeed * DeterministicVariation(anchor.stableId, 0.88f, 1.12f),
                        RunSpeed = profile.runSpeed,
                        Aggression = Math.Clamp(profile.aggression, 0f, 1f),
                        Courage = Math.Clamp(profile.courage, 0f, 1f),
                        CombatSkill = Math.Clamp(profile.combatSkill, 0f, 1f),
                        SpawnAnchorId = anchor.stableId,
                        DeathLootTableId = anchor.deathLootTableId ?? string.Empty,
                        Motor = new ServerCharacterMotor(motorSettings),
                        MotorState = new CharacterMotorState(spawnPose.ToWorldPosition()),
                        Random = new Random(unchecked((int)(anchor.stableId ^ (anchor.stableId >> 32) ^ 0x51F15EED))),
                        LastProgressAt = 0d,
                        LastProgressPosition = spawnPose.ToWorldPosition(),
                        HomePosition = spawnPose.ToWorldPosition(),
                        RoamRadius = Math.Max(2f, DefaultFreeRoamRadius),
                        LeashRadius = Math.Max(Math.Max(2f, DefaultFreeRoamRadius) + 1f, DefaultFreeRoamLeashRadius),
                        CapsuleRadius = capsuleRadius,
                        CapsuleHeight = capsuleHeight,
                    };

                    pop.CurrentNodeId = movementBehavior == SharedAiMovementMode.Route
                        ? (anchor.routeNodeId > 0
                            ? anchor.routeNodeId
                            : FindNearestNode(graph, spawnPose.ToWorldPosition()))
                        : 0;

                    _population.Add(actor.Handle.actorId, pop);
                    _populationSchedule.Add(pop);
                    graph.OccupiedSpawnAnchors.Add(anchor.stableId);
                    if (portalOwned)
                    {
                        graph.SpawnPortalByAnchor[anchor.stableId] = owningPortal.stableId;
                        graph.PortalOwnedCounts.TryGetValue(owningPortal.stableId, out int previousOwnedCount);
                        graph.PortalOwnedCounts[owningPortal.stableId] = previousOwnedCount + 1;
                    }

                    if (anchor.portalId > 0 &&
                        graph.Portals.TryGetValue(anchor.portalId, out ServerPopulationPortal initialPortal) &&
                        initialPortal.mode != PopulationPortalMode.DespawnOnly &&
                        PortalAllows(initialPortal, pop.NpcType))
                    {
                        pop.LastPortalId = initialPortal.stableId;
                        pop.AiState = PopulationAiState.PortalDormant;
                        pop.SimulationLod = PopulationSimulationLod.Dormant;
                        if (pop.MovementBehavior == SharedAiMovementMode.Route)
                            pop.CurrentNodeId = initialPortal.routeNodeId > 0 ? initialPortal.routeNodeId : pop.CurrentNodeId;
                        // Keep the already validated spawn anchor as the safe dormant position.
                        pop.Actor.LastSafePosition = pop.Actor.Position;
                        pop.MotorState = new CharacterMotorState(pop.Actor.Position);
                        if (onlyAnchorId > 0)
                        {
                            double minDelay = Math.Max(0.1d, initialPortal.minimumRespawnDelay);
                            double maxDelay = Math.Max(minDelay, initialPortal.maximumRespawnDelay);
                            pop.DormantUntil = respawnAt + minDelay +
                                (maxDelay - minDelay) * pop.Random.NextDouble();
                        }
                        else
                        {
                            pop.DormantUntil = 0d;
                        }
                        ParkForPlayerActivation(pop, initialPortal.exterior.ToWorldPosition(),
                            onlyAnchorId > 0 ? respawnAt : 0d);
                    }
                    else
                    {
                        if (pop.MovementBehavior == SharedAiMovementMode.Route)
                            OrganicStartupDistribution(graph, pop, false);
                        EnterAoiHibernate(pop, onlyAnchorId > 0 ? respawnAt : 0d);
                    }

                    Added?.Invoke(pop);
                }
            }
        }

        private static bool IsSharedAiSpawnKind(ServerSpawnKind kind) =>
            kind == ServerSpawnKind.Population ||
            kind == ServerSpawnKind.Npc ||
            kind == ServerSpawnKind.Monster;

        private static SharedAiMovementMode ResolveMovementBehavior(MapGraph graph, ServerSpawnAnchor anchor)
        {
            if (anchor != null && anchor.routeNodeId > 0 && graph.Nodes.ContainsKey(anchor.routeNodeId))
                return SharedAiMovementMode.Route;

            if (anchor != null && anchor.kind == ServerSpawnKind.Monster)
                return SharedAiMovementMode.FreeRoam;

            if (anchor != null && FindNearestNode(graph, anchor.pose.ToWorldPosition()) > 0)
                return SharedAiMovementMode.Route;

            return SharedAiMovementMode.Stationary;
        }

        private static PopulationNpcType InferNpcType(string archetypeId)
        {
            string value = (archetypeId ?? string.Empty).ToLowerInvariant();
            if (value.Contains("police")) return PopulationNpcType.Police;
            if (value.Contains("hunter")) return PopulationNpcType.Hunter;
            if (value.Contains("ghoul")) return PopulationNpcType.Ghoul;
            if (value.Contains("gang")) return PopulationNpcType.GangMember;
            if (value.Contains("criminal")) return PopulationNpcType.Criminal;
            if (value.Contains("worker")) return PopulationNpcType.Worker;
            if (value.Contains("shop")) return PopulationNpcType.Shopper;
            if (value.Contains("resident")) return PopulationNpcType.Resident;
            if (value.Contains("homeless")) return PopulationNpcType.Homeless;
            if (value.Contains("night")) return PopulationNpcType.Nightlife;
            return PopulationNpcType.Civilian;
        }

        private static PopulationBehaviorProfileData DefaultProfile(PopulationNpcType type, ServerSpawnKind spawnKind)
        {
            var result = new PopulationBehaviorProfileData { npcType = type, profileId = type.ToString() };
            if (spawnKind == ServerSpawnKind.Monster)
            {
                result.profileId = "Monster.FreeRoam";
                result.faction = ActorFaction.Monster;
                result.aggression = 0.75f;
                result.courage = 0.85f;
                result.combatSkill = 0.50f;
                return result;
            }

            switch (type)
            {
                case PopulationNpcType.Police:
                    result.faction = ActorFaction.Police; result.aggression = 0.65f; result.courage = 0.85f; result.combatSkill = 0.65f; break;
                case PopulationNpcType.Hunter:
                    result.faction = ActorFaction.Hunter; result.aggression = 0.65f; result.courage = 0.85f; result.combatSkill = 0.70f; break;
                case PopulationNpcType.GangMember:
                    result.faction = ActorFaction.Gang; result.aggression = 0.70f; result.courage = 0.65f; result.combatSkill = 0.45f; break;
                case PopulationNpcType.Criminal:
                    result.faction = ActorFaction.Criminal; result.aggression = 0.55f; result.courage = 0.55f; result.combatSkill = 0.35f; break;
                default:
                    result.faction = ActorFaction.Civilian; break;
            }
            return result;
        }

        private void OrganicStartupDistribution(MapGraph graph, PopulationActorRuntime pop, bool portalSpawn)
        {
            if (pop.CurrentNodeId <= 0 || !graph.Nodes.TryGetValue(pop.CurrentNodeId, out _)) return;
            int minHops = portalSpawn ? 2 : 1;
            int hopCount = pop.Random.Next(minHops, 6);
            long node = pop.CurrentNodeId;
            long previous = 0;
            ServerPopulationRouteEdge selected = null;
            for (int hop = 0; hop < hopCount; ++hop)
            {
                selected = ChooseEdge(graph, node, previous, pop.NpcType, pop.Random);
                if (selected == null) break;
                previous = node;
                node = selected.toNodeId;
            }

            if (selected == null)
            {
                EnsureNextNode(graph, pop);
                return;
            }

            pop.CurrentNodeId = selected.fromNodeId;
            pop.NextNodeId = selected.toNodeId;
            pop.SegmentProgress = 0.15f + (float)pop.Random.NextDouble() * 0.70f;
            pop.LaneOffset = ChooseLaneOffset(selected.width, pop.Random);
            if (TryPositionOnEdge(graph, pop.CurrentNodeId, pop.NextNodeId, pop.SegmentProgress, pop.LaneOffset, out WorldPosition position, out float yaw))
            {
                if (graph.Collision != null && graph.Collision.TryFindGround(position, 0.3f, 1f, 2f, 55f, out ServerGroundHit ground))
                    position = new WorldPosition(position.X, ground.Position.Y, position.Z);
                pop.Actor.Position = position;
                pop.Actor.LastSafePosition = position;
                pop.Actor.YawDegrees = yaw;
                pop.MotorState = new CharacterMotorState(position);
            }
            ResolveRouteAssignment(graph, pop);
        }

        private float GetActiveDistance(PopulationActorRuntime pop)
        {
            if (CivilianActiveDistance > 0f &&
                pop.SpawnKind == ServerSpawnKind.Population &&
                pop.MovementBehavior == SharedAiMovementMode.Route &&
                pop.Actor.Faction == ActorFaction.Civilian &&
                pop.NpcType >= PopulationNpcType.Civilian &&
                pop.NpcType <= PopulationNpcType.Nightlife &&
                pop.PortalPhase == PopulationPortalSequencePhase.None)
                return Math.Min(ActiveDistance, CivilianActiveDistance);
            return ActiveDistance;
        }

        private void UpdateSimulationLod(PopulationActorRuntime pop, float nearestSq)
        {
            if (pop.AiState == PopulationAiState.PortalDormant || !pop.Actor.Alive)
            {
                pop.SimulationLod = PopulationSimulationLod.Dormant;
                return;
            }

            // Combat, fleeing and recovery deserve the expensive engaged cadence.
            // Peaceful NPCs standing next to a player do not: they use Active
            // cadence, retaining full combat promotion through NotifyThreat.
            if (pop.AiState == PopulationAiState.Fighting ||
                pop.AiState == PopulationAiState.Fleeing ||
                pop.AiState == PopulationAiState.Recovering ||
                pop.ThreatUntil > pop.LastSimulationAt)
                pop.SimulationLod = PopulationSimulationLod.Engaged;
            else if (nearestSq <= GetActiveDistance(pop) * GetActiveDistance(pop))
                pop.SimulationLod = PopulationSimulationLod.Active;
            else if (nearestSq <= CoarseDistance * CoarseDistance)
                pop.SimulationLod = PopulationSimulationLod.CoarseRoute;
            else
                pop.SimulationLod = PopulationSimulationLod.Logical;
        }

        private void TickLogical(PopulationActorRuntime pop, float dt, double now)
        {
            // Logical actors retain life/route state but update less often. Advancing route progress
            // is cheap and keeps their conceptual travel continuous without collision queries.
            TickCoarse(pop, Math.Min(dt, 2f), now);
        }

        private void TickStationary(PopulationActorRuntime pop)
        {
            if (pop?.Actor == null)
                return;

            bool changed =
                Math.Abs(pop.Actor.VelocityX) > 0.0001f ||
                Math.Abs(pop.Actor.VelocityY) > 0.0001f ||
                Math.Abs(pop.Actor.VelocityZ) > 0.0001f ||
                pop.AiState != PopulationAiState.Idle;

            pop.Actor.VelocityX = 0f;
            pop.Actor.VelocityY = 0f;
            pop.Actor.VelocityZ = 0f;
            pop.AiState = PopulationAiState.Idle;

            if (changed)
            {
                PublishPopulationChanged(pop);
            }
        }

        private void TickFreeRoam(PopulationActorRuntime pop, float dt, double now)
        {
            if (pop?.Actor == null || !TryGetGraph(pop.Actor, out MapGraph graph))
                return;

            if (pop.WaitUntil > now)
            {
                TickStationary(pop);
                return;
            }

            float leash = Math.Max(pop.RoamRadius + 1f, pop.LeashRadius);
            if (DistanceXZ(pop.Actor.Position, pop.HomePosition) > leash)
            {
                pop.RoamTarget = pop.HomePosition;
                pop.HasRoamTarget = true;
                pop.AiState = PopulationAiState.Recovering;
            }
            else if (!pop.HasRoamTarget)
            {
                if (now + 0.000001d < pop.NextRoamDecisionAt)
                {
                    TickStationary(pop);
                    return;
                }

                if (!TrySelectFreeRoamTarget(graph, pop, out WorldPosition roamTarget))
                {
                    pop.NextRoamDecisionAt = now + 1d;
                    TickStationary(pop);
                    return;
                }

                pop.RoamTarget = roamTarget;
                pop.HasRoamTarget = true;
                pop.AiState = PopulationAiState.FollowingRoute;
                pop.RouteReason = PopulationRouteReason.Wander;
            }

            float dx = pop.RoamTarget.X - pop.Actor.Position.X;
            float dz = pop.RoamTarget.Z - pop.Actor.Position.Z;
            float distSq = dx * dx + dz * dz;
            if (distSq <= 0.50f * 0.50f)
            {
                pop.HasRoamTarget = false;
                double minPause = Math.Max(0d, FreeRoamMinimumPauseSeconds);
                double maxPause = Math.Max(minPause, FreeRoamMaximumPauseSeconds);
                pop.WaitUntil = now + minPause + (maxPause - minPause) * pop.Random.NextDouble();
                pop.NextRoamDecisionAt = pop.WaitUntil;
                TickStationary(pop);
                return;
            }

            float inv = 1f / MathF.Sqrt(Math.Max(0.0001f, distSq));
            pop.Actor.YawDegrees = NormalizeYaw(MathF.Atan2(dx, dz) * (180f / MathF.PI));

            if (graph.Collision != null)
            {
                var intent = new CharacterMovementIntent(dx * inv, dz * inv, false, false);
                bool blocked = false;
                long motorStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    pop.Motor.Tick(pop.MotorState, intent, dt, graph.Collision,
                        allowWallSlide: false, out blocked);
                }
                finally
                {
                    _diagnosticMotorCollisionMs += ElapsedDiagnosticMilliseconds(motorStartedAt);
                    ++_diagnosticMotorCalls;
                }
                ApplyMotor(pop);
                if (blocked)
                {
                    // A ground/capsule-validated position previously occupied by this
                    // actor is safer than picking another target through the same wall.
                    if (DistanceXZ(pop.Actor.Position, pop.LastProgressPosition) > 0.75f &&
                        pop.RouteReason != PopulationRouteReason.Recovery)
                    {
                        pop.RoamTarget = pop.LastProgressPosition;
                        pop.HasRoamTarget = true;
                        pop.RouteReason = PopulationRouteReason.Recovery;
                    }
                    else
                    {
                        pop.HasRoamTarget = false;
                        pop.NextRoamDecisionAt = now + 0.5d;
                    }
                    pop.LastProgressAt = now;
                    pop.LastProgressPosition = pop.Actor.Position;
                    return;
                }
            }
            else
            {
                WorldPosition previous = pop.Actor.Position;
                float step = Math.Min(MathF.Sqrt(distSq), Math.Max(0.1f, pop.WalkSpeed) * dt);
                pop.Actor.Position = new WorldPosition(
                    previous.X + dx * inv * step,
                    previous.Y,
                    previous.Z + dz * inv * step);
                pop.Actor.LastSafePosition = pop.Actor.Position;
                pop.Actor.MovementMode = ActorMovementMode.Grounded;
                pop.Actor.VelocityX = pop.Actor.Position.X - previous.X;
                pop.Actor.VelocityY = pop.Actor.Position.Y - previous.Y;
                pop.Actor.VelocityZ = pop.Actor.Position.Z - previous.Z;
                PublishPopulationChanged(pop);
            }

            DetectStuck(graph, pop, now);
        }

        private bool TrySelectFreeRoamTarget(
            MapGraph graph,
            PopulationActorRuntime pop,
            out WorldPosition target)
        {
            target = pop?.HomePosition ?? default;
            if (pop?.Actor == null)
                return false;

            float radius = Math.Max(2f, pop.RoamRadius);
            for (int attempt = 0; attempt < 8; ++attempt)
            {
                double angle = pop.Random.NextDouble() * Math.PI * 2d;
                float distance = 2f + (float)pop.Random.NextDouble() * Math.Max(0.1f, radius - 2f);
                var candidate = new WorldPosition(
                    pop.HomePosition.X + (float)Math.Sin(angle) * distance,
                    pop.HomePosition.Y,
                    pop.HomePosition.Z + (float)Math.Cos(angle) * distance);

                if (graph.Collision == null)
                {
                    target = candidate;
                    return true;
                }

                if (!graph.Collision.TryFindGround(
                        candidate,
                        Math.Max(0.2f, pop.CapsuleRadius),
                        1f,
                        2f,
                        55f,
                        out ServerGroundHit ground))
                {
                    continue;
                }

                var capsule = new ServerCapsule(
                    Math.Max(0.2f, pop.CapsuleRadius),
                    Math.Max(pop.CapsuleHeight, pop.CapsuleRadius * 2f));
                if (!graph.Collision.IsCapsuleClear(ground.Position, capsule))
                    continue;

                target = ground.Position;
                return true;
            }

            return false;
        }

        private void TickCoarse(PopulationActorRuntime pop, float dt, double now)
        {
            if (!TryGetAssignedGraph(pop, out MapGraph graph) || !EnsureNextNode(graph, pop)) return;
            if (!ResolveRouteAssignment(graph, pop)) return;
            ServerPopulationRouteNode to = pop.AssignedRouteTarget;
            PopulationRouteSegment segment = pop.AssignedRouteSegment;
            if (segment == null && !graph.Nodes.ContainsKey(pop.CurrentNodeId)) return;
            float distance = segment != null ? segment.Length :
                DistanceXZ(graph.Nodes[pop.CurrentNodeId].pose.ToWorldPosition(), to.pose.ToWorldPosition());
            if (distance < 0.05f)
            {
                ArriveNode(graph, pop, now);
                return;
            }

            pop.SegmentProgress += pop.WalkSpeed * dt / distance;
            if (pop.SegmentProgress >= 1f)
            {
                pop.Actor.Position = to.pose.ToWorldPosition();
                ArriveNode(graph, pop, now);
                return;
            }

            if (TryPositionOnAssignedSegment(graph, pop, out WorldPosition position, out float yaw))
            {
                pop.Actor.Position = position;
                pop.Actor.YawDegrees = yaw;
                pop.Actor.MovementMode = ActorMovementMode.Grounded;
                pop.Actor.VelocityX = 0f;
                pop.Actor.VelocityY = 0f;
                pop.Actor.VelocityZ = 0f;
                PublishPopulationChanged(pop);
            }
        }

        private void TickActive(PopulationActorRuntime pop, float dt, double now)
        {
            long stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool graphReady = TryGetAssignedGraph(pop, out MapGraph graph) && graph.Collision != null;
            _diagnosticActiveGraphMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            if (!graphReady) return;

            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool hasNextNode = EnsureNextNode(graph, pop);
            _diagnosticActiveEnsureNodeMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            if (!hasNextNode) return;

            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool routeResolved = ResolveRouteAssignment(graph, pop);
            _diagnosticActiveRouteSetupMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            if (!routeResolved) return;
            ServerPopulationRouteNode targetNode = pop.AssignedRouteTarget;

            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            WorldPosition target = OffsetTargetForLane(graph, pop, targetNode.pose.ToWorldPosition());
            float dx = target.X - pop.Actor.Position.X;
            float dz = target.Z - pop.Actor.Position.Z;
            float distSq = dx * dx + dz * dz;
            _diagnosticActiveTargetMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            if (distSq <= 0.35f * 0.35f)
            {
                ArriveNode(graph, pop, now);
                return;
            }

            // Peaceful grounded route followers take the bounded cheap collision
            // sweep, not the full player-grade motor. If any condition is unsafe,
            // the untouched motor below remains authoritative immediately.
            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ambientRouteHandled = TryTickAmbientRouteFast(graph, pop, target, dt, now);
            _diagnosticActiveAmbientMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            if (ambientRouteHandled)
                return;

            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                float inv = 1f / MathF.Sqrt(Math.Max(0.0001f, distSq));
                bool running = pop.AiState == PopulationAiState.Fleeing;
                var intent = new CharacterMovementIntent(dx * inv, dz * inv, running, false);
                pop.Actor.YawDegrees = NormalizeYaw(MathF.Atan2(dx, dz) * (180f / MathF.PI));
                bool blocked = false;
                long motorStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    pop.Motor.Tick(pop.MotorState, intent, dt, graph.Collision,
                        allowWallSlide: false, out blocked);
                }
                finally
                {
                    _diagnosticMotorCollisionMs += ElapsedDiagnosticMilliseconds(motorStartedAt);
                    ++_diagnosticMotorCalls;
                }
                ApplyMotor(pop);
                if (blocked)
                    RecoverBlockedRoute(pop, now);
                else
                    DetectStuck(graph, pop, now);
            }
            finally
            {
                _diagnosticActiveFallbackMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            }
        }

        private void TickThreat(PopulationActorRuntime pop, float dt, double now)
        {
            if (!TryGetGraph(pop.Actor, out MapGraph graph) || graph.Collision == null) return;
            if (pop.AiState == PopulationAiState.Fighting)
            {
                pop.Actor.VelocityX = pop.Actor.VelocityY = pop.Actor.VelocityZ = 0f;
                FightIntent?.Invoke(pop);
                return;
            }

            float dx = pop.Actor.Position.X - pop.ThreatPosition.X;
            float dz = pop.Actor.Position.Z - pop.ThreatPosition.Z;
            float lenSq = dx * dx + dz * dz;
            if (lenSq < 0.001f) { dx = 1f; dz = 0f; lenSq = 1f; }

            bool onRoute = TryGetFleeRouteTarget(graph, pop, now, out WorldPosition routeTarget);
            if (onRoute)
            {
                // Normal fleeing still follows the existing authoritative escape
                // route, but a certified lane needs no repeated static physics.
                if (TryTickAmbientRouteFast(graph, pop, routeTarget, dt, now, fleeing: true))
                    return;
                dx = routeTarget.X - pop.Actor.Position.X;
                dz = routeTarget.Z - pop.Actor.Position.Z;
                lenSq = dx * dx + dz * dz;
            }
            else if (now < pop.FleeSideStepUntil)
            {
                // Select one escape heading per work unit. This is not collision
                // sliding and never performs additional same-tick collision probes.
                float sideX = -dz * pop.FleeSideStepSign;
                dz = dx * pop.FleeSideStepSign;
                dx = sideX;
                lenSq = dx * dx + dz * dz;
            }
            if (lenSq <= 0.000001f) return;

            float inv = 1f / MathF.Sqrt(lenSq);
            var intent = new CharacterMovementIntent(dx * inv, dz * inv, true, false);
            pop.Actor.YawDegrees = NormalizeYaw(MathF.Atan2(dx, dz) * (180f / MathF.PI));
            bool blocked = false;
            long motorStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                pop.Motor.Tick(pop.MotorState, intent, dt, graph.Collision,
                    allowWallSlide: false, out blocked);
            }
            finally
            {
                _diagnosticMotorCollisionMs += ElapsedDiagnosticMilliseconds(motorStartedAt);
                ++_diagnosticMotorCalls;
            }
            ApplyMotor(pop);
            if (blocked)
            {
                if (onRoute && pop.NextNodeId > 0)
                    RecoverBlockedRoute(pop, now);
                else
                {
                    // Alternate the chosen side only on a blocked movement event.
                    // Do not feed both X/Z slides into the collision solver.
                    pop.FleeSideStepSign = pop.FleeSideStepSign == 1 ? -1 : 1;
                    pop.FleeSideStepUntil = now + 0.65d;
                    pop.LastProgressAt = now;
                    pop.LastProgressPosition = pop.Actor.Position;
                }
                return;
            }
            DetectStuck(graph, pop, now);
        }

        private bool TryGetFleeRouteTarget(
            MapGraph graph, PopulationActorRuntime pop, double now, out WorldPosition target)
        {
            target = default;
            if (pop.MovementBehavior != SharedAiMovementMode.Route ||
                pop.CurrentNodeId <= 0 || !graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode currentNode))
                return false;

            // The actor is already on a baked route. Prefer the next connected
            // marker when it leads away from the threat. Reversing to CurrentNode
            // retraces the already-cleared segment if the forward direction is unsafe.
            if (pop.NextNodeId > 0 && graph.Nodes.TryGetValue(pop.NextNodeId, out ServerPopulationRouteNode nextNode))
            {
                target = nextNode.pose.ToWorldPosition();
                if (DistanceXZ(target, pop.Actor.Position) > 0.35f)
                {
                    if (pop.NextNodeId == pop.CurrentNodeId ||
                        DistanceXZ(target, pop.ThreatPosition) >
                        DistanceXZ(currentNode.pose.ToWorldPosition(), pop.ThreatPosition) + 0.15f)
                        return true;
                    pop.NextNodeId = pop.CurrentNodeId;
                    target = currentNode.pose.ToWorldPosition();
                    if (DistanceXZ(target, pop.Actor.Position) > 0.35f)
                        return true;
                }
                else
                {
                    pop.PreviousNodeId = pop.CurrentNodeId;
                    pop.CurrentNodeId = pop.NextNodeId;
                    pop.NextNodeId = 0;
                }
            }

            if (!graph.Nodes.TryGetValue(pop.CurrentNodeId, out currentNode)) return false;
            float currentThreatSq = DistanceXZ(currentNode.pose.ToWorldPosition(), pop.ThreatPosition);
            ServerPopulationRouteEdge best = null;
            float bestScore = currentThreatSq + 0.15f;
            if (graph.Outgoing.TryGetValue(pop.CurrentNodeId, out List<ServerPopulationRouteEdge> edges))
            {
                long blockedNode = BlockedDestinationFor(pop, pop.CurrentNodeId, now);
                for (int i = 0; i < edges.Count; ++i)
                {
                    ServerPopulationRouteEdge edge = edges[i];
                    if (!EdgeAllows(graph, edge, pop.NpcType) || edge.toNodeId == blockedNode)
                        continue;
                    WorldPosition end = graph.Nodes[edge.toNodeId].pose.ToWorldPosition();
                    float score = DistanceXZ(end, pop.ThreatPosition);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = edge;
                    }
                }
            }
            if (best != null)
            {
                pop.NextNodeId = best.toNodeId;
                pop.LaneOffset = ChooseLaneOffset(best.width, pop.Random);
                target = OffsetTargetForLane(graph, pop,
                    graph.Nodes[pop.NextNodeId].pose.ToWorldPosition());
                return true;
            }

            // The current route has no connected safe escape edge. The next
            // heading is chosen from the existing threat position, still no-slide.
            return false;
        }

        private static long BlockedDestinationFor(
            PopulationActorRuntime pop, long sourceNode, double now) =>
            now < pop.BlockedRouteUntil && pop.BlockedRouteFromNodeId == sourceNode
                ? pop.BlockedRouteToNodeId : 0;

        private void RecoverBlockedRoute(PopulationActorRuntime pop, double now)
        {
            if (pop.CurrentNodeId > 0 && pop.NextNodeId > 0 &&
                pop.CurrentNodeId != pop.NextNodeId)
            {
                pop.BlockedRouteFromNodeId = pop.CurrentNodeId;
                pop.BlockedRouteToNodeId = pop.NextNodeId;
                pop.BlockedRouteUntil = now + 10d;
                pop.NextNodeId = pop.CurrentNodeId;
                pop.LaneOffset = 0f;
                pop.RouteReason = PopulationRouteReason.Recovery;
                if (pop.AiState != PopulationAiState.Fleeing)
                    pop.AiState = PopulationAiState.Recovering;
            }
            else
            {
                // Cannot physically retrace this segment. Stay put rather than
                // repeatedly checking a blocked direction on every AI work item.
                pop.NextNodeId = 0;
                pop.WaitUntil = now + 0.75d;
            }
            pop.LastProgressAt = now;
            pop.LastProgressPosition = pop.Actor.Position;
        }

        private void ApplyMotor(PopulationActorRuntime pop)
        {
            WorldPosition previous = pop.Actor.Position;
            WorldPosition next = pop.MotorState.Position;
            pop.Actor.Position = next;
            pop.Actor.LastSafePosition = pop.MotorState.LastSafePosition;
            pop.Actor.MovementMode = pop.MotorState.Mode;
            pop.Actor.VelocityX = next.X - previous.X;
            pop.Actor.VelocityY = next.Y - previous.Y;
            pop.Actor.VelocityZ = next.Z - previous.Z;

            PublishPopulationChanged(pop);
        }

        private void PublishPopulationChanged(PopulationActorRuntime pop)
        {
            // ActorRegistry.PublishChanged owns canonical spatial updates and its
            // existing actor subscribers. Population.Changed is consumed by the
            // GameServer observer-presentation reconciliation/snapshot path.
            // Preserve invocation order and exception propagation. Both readings
            // are inclusive wall-clock durations (GC/OS pauses are possible).
            long publishStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _actors.PublishChanged(pop.Actor);
            }
            finally
            {
                _diagnosticActorPublishMs += ElapsedDiagnosticMilliseconds(publishStartedAt);
            }

            long presentationStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                Changed?.Invoke(pop);
            }
            finally
            {
                _diagnosticObserverPresentationMs += ElapsedDiagnosticMilliseconds(presentationStartedAt);
            }
        }

        private void DetectStuck(MapGraph graph, PopulationActorRuntime pop, double now)
        {
            ++_diagnosticStuckChecks;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try { DetectStuckCore(graph, pop, now); }
            finally { _diagnosticRouteRecoveryMs += ElapsedDiagnosticMilliseconds(startedAt); }
        }

        private void DetectStuckCore(MapGraph graph, PopulationActorRuntime pop, double now)
        {
            if (pop.LastProgressAt <= 0d)
            {
                pop.LastProgressAt = now;
                pop.LastProgressPosition = pop.Actor.Position;
                return;
            }
            if (DistanceXZ(pop.LastProgressPosition, pop.Actor.Position) >= 0.5f)
            {
                pop.LastProgressAt = now;
                pop.LastProgressPosition = pop.Actor.Position;
                return;
            }
            if (now - pop.LastProgressAt < 3d) return;

            pop.RouteReason = PopulationRouteReason.Recovery;
            if (pop.MovementBehavior == SharedAiMovementMode.Route)
            {
                // First recovery is route reattachment rather than teleportation.
                pop.AiState = PopulationAiState.Recovering;
                ReattachToNearestRoute(pop);
            }
            else if (pop.MovementBehavior == SharedAiMovementMode.FreeRoam)
            {
                // Free-roam recovery is cheap: discard the blocked destination and choose
                // another direction on the next shared scheduler work item.
                pop.AiState = PopulationAiState.Idle;
                pop.HasRoamTarget = false;
                pop.NextRoamDecisionAt = now + 0.25d;
            }
            else
            {
                pop.AiState = PopulationAiState.Idle;
            }

            pop.LastProgressAt = now;
            pop.LastProgressPosition = pop.Actor.Position;
        }

        private void ReattachToNearestRoute(PopulationActorRuntime pop)
        {
            ++_diagnosticRouteReattachments;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try { ReattachToNearestRouteCore(pop); }
            finally { _diagnosticRouteRecoveryMs += ElapsedDiagnosticMilliseconds(startedAt); }
        }

        private void ReattachToNearestRouteCore(PopulationActorRuntime pop)
        {
            if (pop == null || pop.MovementBehavior != SharedAiMovementMode.Route)
            {
                if (pop != null)
                {
                    pop.AiState = PopulationAiState.Idle;
                    pop.HasRoamTarget = false;
                }
                return;
            }

            if (!TryGetGraph(pop.Actor, out MapGraph graph)) return;
            long node = FindNearestNode(graph, pop.Actor.Position);
            if (node <= 0) return;
            pop.CurrentNodeId = node;
            // Rejoin this marker once before choosing another edge. This prevents
            // repeated nearest-node searches while still physically off the route.
            pop.NextNodeId = node;
            pop.SegmentProgress = 0f;
            pop.AiState = PopulationAiState.FollowingRoute;
            pop.RouteReason = PopulationRouteReason.Recovery;
            EnsureNextNode(graph, pop);
        }

        private void ArriveNode(MapGraph graph, PopulationActorRuntime pop, double now)
        {
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try { ArriveNodeCore(graph, pop, now); }
            finally { _diagnosticRouteArrivalMs += ElapsedDiagnosticMilliseconds(startedAt); }
        }

        private void ArriveNodeCore(MapGraph graph, PopulationActorRuntime pop, double now)
        {
            long fromNodeId = pop.CurrentNodeId;
            long reachedNodeId = pop.NextNodeId;
            pop.PreviousNodeId = fromNodeId;
            pop.CurrentNodeId = reachedNodeId;
            pop.NextNodeId = 0;
            pop.SegmentProgress = 0f;
            if (graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode node))
            {
                float minWait = Math.Max(0f, node.minimumWaitSeconds);
                float maxWait = Math.Max(minWait, node.maximumWaitSeconds);
                if (maxWait > 0f)
                    pop.WaitUntil = now + minWait + (maxWait - minWait) * pop.Random.NextDouble();
            }

            // Portal activation's Current==Next handoff is not a traversed edge.
            // Only a portal's primary baked routeNodeId is registered as an arrival.
            if (pop.PreviousNodeId != pop.CurrentNodeId &&
                graph.PortalsByRouteNode.TryGetValue(
                    pop.CurrentNodeId,
                    out List<ServerPopulationPortal> nodePortals))
            {
                for (int i = 0; i < nodePortals.Count; ++i)
                {
                    ServerPopulationPortal portal = nodePortals[i];
                    if (portal == null ||
                        !PortalAllows(portal, pop.NpcType) ||
                        (portal.mode != PopulationPortalMode.DespawnOnly &&
                         portal.mode != PopulationPortalMode.SpawnAndDespawn))
                    {
                        continue;
                    }

                    EnterPortalDormant(pop, portal, now);
                    return;
                }
            }
            EnsureNextNode(graph, pop);
        }

        private void EnterPortalDormant(PopulationActorRuntime pop, ServerPopulationPortal portal, double now)
        {
            RemoveFromHibernationIndex(pop);
            pop.AwaitingPlayerActivation = false;
            pop.AiState = PopulationAiState.PortalDormant;
            pop.SimulationLod = PopulationSimulationLod.Dormant;
            // The destination portal controls this actor's authored wait and next
            // appearance. SpawnAnchorId remains unchanged, so only the original
            // spawner owns capacity and death replacement.
            pop.LastPortalId = portal.stableId;
            pop.NextNodeId = 0;
            pop.PortalPhase = PopulationPortalSequencePhase.None;
            double min = Math.Max(0.1, portal.minimumRespawnDelay);
            double max = Math.Max(min, portal.maximumRespawnDelay);
            pop.DormantUntil = now + min + (max - min) * pop.Random.NextDouble();
            pop.Actor.VelocityX = pop.Actor.VelocityY = pop.Actor.VelocityZ = 0f;
            _actors.SuspendSpatial(pop.Actor);
            Changed?.Invoke(pop);
        }

        private void TickDormant(PopulationActorRuntime pop, double now, IReadOnlyList<PopulationPlayerView> players)
        {
            if (now < pop.DormantUntil || _portalRespawnsThisTick >= Math.Max(1, MaximumPortalRespawnsPerTick)) return;
            long stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            ServerPopulationPortal portal = null;
            bool hasPortal = TryGetGraph(pop.Actor, out MapGraph graph) &&
                graph.Portals.TryGetValue(pop.LastPortalId, out portal);
            _diagnosticDormantGraphMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            if (!hasPortal)
            {
                pop.AiState = PopulationAiState.Recovering;
                ReattachToNearestRoute(pop);
                return;
            }
            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool playerNearby = HasPlayerWithin(
                pop.Actor, portal.exterior.ToWorldPosition(), players, PlayerActivationDistance);
            _diagnosticDormantProximityMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            if (!playerNearby)
            {
                ParkForPlayerActivation(pop, portal.exterior.ToWorldPosition(), now);
                return;
            }

            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (portal.mode != PopulationPortalMode.DespawnOnly)
            {
                bool cadenceAllows = PortalCadenceAllows(portal, now, out double nextPortalReleaseAt);
                _diagnosticDormantAdmissionMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
                if (!cadenceAllows)
                {
                    pop.DormantUntil = Math.Max(now + 0.05d, nextPortalReleaseAt);
                    return;
                }
            }
            else
            {
                // A DespawnOnly portal is a destination, not a population source.
                // Its authored respawn delay is the only wait before this actor resumes.
                _diagnosticDormantAdmissionMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            }

            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool spawnClear = PortalSpawnClear(graph, portal, pop, players);
            _diagnosticDormantSpawnClearMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            if (!spawnClear)
            {
                pop.PortalRespawnAttempts++;
                pop.DormantUntil = now + Math.Max(0.25f, portal.blockedRetryDelay);
                return;
            }

            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool activated = ActivatePortalSpawn(graph, pop, portal, now, scheduleImmediately: false);
            _diagnosticDormantActivateMs += ElapsedDiagnosticMilliseconds(stageStartedAt);
            if (!activated)
            {
                pop.PortalRespawnAttempts++;
                pop.DormantUntil = now + Math.Max(0.25f, portal.blockedRetryDelay);
            }
        }

        private bool ActivatePortalSpawn(
            MapGraph graph,
            PopulationActorRuntime pop,
            ServerPopulationPortal portal,
            double now,
            bool scheduleImmediately)
        {
            if (graph == null || pop == null || pop.Actor == null || portal == null ||
                (graph.Collision == null && pop.SpawnKind != ServerSpawnKind.Population))
                return false;

            // A Population portal's stationary geometry was checked in World Bake.
            // Ambient civilian activation does not rerun world collision tests here.
            // Combat-capable NPCs retain the live clearance checks in PortalSpawnClear.
            WorldPosition anchor = portal.exterior.ToWorldPosition();
            if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y) ||
                !float.IsFinite(anchor.Z))
                return false;

            long joinNode = portal.routeNodeId > 0 && graph.Nodes.ContainsKey(portal.routeNodeId)
                ? portal.routeNodeId : 0;
            // Old/partial maps may have a missing primary link but valid extra links.
            if (joinNode <= 0 && portal.routeNodeIds != null)
                for (int i = 0; i < portal.routeNodeIds.Length; ++i)
                    if (graph.Nodes.ContainsKey(portal.routeNodeIds[i]))
                    {
                        joinNode = portal.routeNodeIds[i];
                        break;
                    }
            if (joinNode <= 0)
                joinNode = FindNearestNode(graph, anchor);

            if (pop.MovementBehavior == SharedAiMovementMode.Route && joinNode <= 0)
                return false;

            _portalRespawnsThisTick++;
            if (portal.mode != PopulationPortalMode.DespawnOnly)
                RecordPortalRelease(portal, pop, now);
            pop.PortalRespawnAttempts = 0;

            // Portal presentation is now a single authoritative anchor. Normal Population AI
            // owns movement immediately after spawn. For route actors, the associated/nearest
            // route node is installed as the first ordinary movement target, so the shared
            // ServerCharacterMotor physically joins the route instead of a special doorway
            // sequence moving or teleporting the actor onto it.
            pop.PortalPhase = PopulationPortalSequencePhase.None;
            pop.AiState = pop.MovementBehavior == SharedAiMovementMode.Route
                ? PopulationAiState.FollowingRoute
                : PopulationAiState.Idle;
            pop.RouteReason = PopulationRouteReason.Wander;
            pop.Actor.Position = anchor;
            pop.Actor.LastSafePosition = anchor;
            pop.MotorState = new CharacterMotorState(anchor);
            pop.PreviousNodeId = 0;
            pop.BlockedRouteFromNodeId = 0;
            pop.BlockedRouteToNodeId = 0;
            pop.BlockedRouteUntil = 0d;
            pop.CurrentNodeId = joinNode;
            pop.NextNodeId = pop.MovementBehavior == SharedAiMovementMode.Route ? joinNode : 0;
            if (pop.MovementBehavior == SharedAiMovementMode.Route)
                ResolveRouteAssignment(graph, pop);
            pop.SegmentProgress = 0f;
            pop.HomePosition = anchor;
            pop.HasRoamTarget = false;
            pop.AwaitingPlayerActivation = false;

            RemoveFromHibernationIndex(pop);
            _actors.ResumeSpatial(pop.Actor);
            _actors.PublishChanged(pop.Actor);
            Changed?.Invoke(pop);

            if (scheduleImmediately)
                ScheduleNow(pop, now);

            return true;
        }

        private bool PortalCadenceAllows(ServerPopulationPortal portal, double now, out double nextEligibleAt)
        {
            nextEligibleAt = now;
            if (portal == null)
                return false;

            if (!_portalReleaseStates.TryGetValue(portal.stableId, out PortalReleaseState state))
            {
                state = new PortalReleaseState();
                _portalReleaseStates.Add(portal.stableId, state);
            }

            int burstLimit = Math.Max(1, portal.spawnBurstLimit);

            // Multiple admissions are allowed in one shared scheduler preparation only when the
            // author explicitly configured a burst greater than one. Across scheduler passes the
            // per-portal deadline is authoritative, so no per-Pop timer or polling loop is needed.
            if (state.AdmissionGeneration == _portalAdmissionGeneration)
            {
                nextEligibleAt = state.NextReleaseAt;
                return state.BurstCount < burstLimit;
            }

            nextEligibleAt = state.NextReleaseAt;
            return now + 0.000001d >= state.NextReleaseAt;
        }

        private void RecordPortalRelease(ServerPopulationPortal portal, PopulationActorRuntime pop, double now)
        {
            if (portal == null)
                return;

            if (!_portalReleaseStates.TryGetValue(portal.stableId, out PortalReleaseState state))
            {
                state = new PortalReleaseState();
                _portalReleaseStates.Add(portal.stableId, state);
            }

            if (state.AdmissionGeneration != _portalAdmissionGeneration)
            {
                state.AdmissionGeneration = _portalAdmissionGeneration;
                state.BurstCount = 0;
            }

            state.BurstCount++;

            // Start the interval when the first actor in this burst is released. Additional actors
            // in the same configured burst may still leave during this admission generation, but
            // the next generation must wait until this shared portal deadline.
            if (state.BurstCount == 1)
            {
                double min = Math.Max(0d, portal.minimumSpawnInterval);
                double max = Math.Max(min, portal.maximumSpawnInterval);
                double interval = min;
                if (max > min + 0.000001d)
                {
                    double t = pop?.Random?.NextDouble() ?? 0.5d;
                    interval += (max - min) * t;
                }

                state.NextReleaseAt = now + interval;
            }
        }

        private bool PortalSpawnClear(MapGraph graph, ServerPopulationPortal portal, PopulationActorRuntime pop, IReadOnlyList<PopulationPlayerView> players)
        {
            if (graph == null || portal == null || pop == null || pop.Actor == null)
                return false;

            WorldPosition exterior = portal.exterior.ToWorldPosition();
            if (pop.SpawnKind == ServerSpawnKind.Population)
            {
                // Authored civilian portals are validated and capacity-limited by the baked
                // spawn anchors. No live world-blocker, Population-density, or player-LOS
                // admission query is needed for these non-colliding ambient actors.
                return float.IsFinite(exterior.X) && float.IsFinite(exterior.Y) &&
                       float.IsFinite(exterior.Z);
            }

            if (graph.Collision == null)
                return false;
            float radius = Math.Max(Math.Max(0.2f, portal.exitClearanceRadius), pop.CapsuleRadius);
            float height = Math.Max(pop.CapsuleHeight, radius * 2f);
            var capsule = new ServerCapsule(radius, height);

            // Combat-capable NPC portals retain live clearance and density protection.
            if (!graph.Collision.IsDynamicCapsuleClear(exterior, capsule))
                return false;

            // Portal density is a Population control. Do not let unrelated authoritative
            // actors (monsters, NPCs, world helpers, etc.) consume a Population doorway's
            // capacity, and do not count dormant/hibernating Population identities.
            // This also keeps multi-Pop authored spawns from serializing to one-at-a-time.
            int nearbyPopulation = 0;
            _actors.QueryRadius(pop.Actor.MapId, pop.Actor.InstanceId, exterior, Math.Max(1f, portal.activeNearbyRadius), _nearbyScratch);
            for (int i = 0; i < _nearbyScratch.Count; ++i)
            {
                AuthoritativeActorRuntime nearbyActor = _nearbyScratch[i];
                if (nearbyActor == null || !nearbyActor.Alive ||
                    nearbyActor.Handle.actorId == pop.Actor.Handle.actorId ||
                    nearbyActor.Handle.kind != AuthoritativeActorKind.Population)
                {
                    continue;
                }

                if (_population.TryGetValue(nearbyActor.Handle.actorId, out PopulationActorRuntime nearbyPop) &&
                    nearbyPop != null &&
                    !nearbyPop.AwaitingPlayerActivation &&
                    nearbyPop.AiState != PopulationAiState.PortalDormant)
                {
                    nearbyPopulation++;
                }
            }
            if (nearbyPopulation >= Math.Max(1, portal.maximumActiveNearby)) return false;

            if (portal.suppressWhenVisibleToPlayers && players != null && graph.Collision != null)
            {
                float maxVisible = Math.Max(5f, VisibilitySuppressionDistance);
                for (int i = 0; i < players.Count; ++i)
                {
                    PopulationPlayerView player = players[i];
                    if (!SamePartition(pop.Actor, player)) continue;
                    if (DistanceXZ(player.Position, exterior) > maxVisible) continue;
                    WorldPosition eye = new WorldPosition(player.Position.X, player.Position.Y + 1.6f, player.Position.Z);
                    WorldPosition target = new WorldPosition(exterior.X, exterior.Y + 1.0f, exterior.Z);
                    if (!graph.Collision.IsLineObstructed(eye, target))
                        return false;
                }
            }
            return true;
        }

        private void TickPortalExitSequence(PopulationActorRuntime pop, float dt, double now)
        {
            if (!TryGetGraph(pop.Actor, out MapGraph graph) ||
                !graph.Portals.TryGetValue(pop.LastPortalId, out ServerPopulationPortal portal))
            {
                pop.PortalPhase = PopulationPortalSequencePhase.None;
                return;
            }

            if (graph.Collision == null)
            {
                FailPortalTraversal(pop, portal, now);
                return;
            }

            ServerPose targetPose;
            switch (pop.PortalPhase)
            {
                case PopulationPortalSequencePhase.Interior:
                    targetPose = portal.approach;
                    break;
                case PopulationPortalSequencePhase.Approach:
                    targetPose = portal.interaction;
                    break;
                case PopulationPortalSequencePhase.Door:
                    if (portal.doorWorldObjectId > 0)
                    {
                        if (_worldObjects == null ||
                            !_worldObjects.TryOpenForSystem(
                                pop.Actor.MapId,
                                pop.Actor.InstanceId,
                                portal.doorWorldObjectId,
                                out _))
                        {
                            FailPortalTraversal(pop, portal, now);
                            return;
                        }
                    }
                    pop.PortalPhase = PopulationPortalSequencePhase.Threshold;
                    return;
                case PopulationPortalSequencePhase.Threshold:
                    targetPose = portal.threshold;
                    break;
                case PopulationPortalSequencePhase.Exterior:
                    targetPose = portal.exterior;
                    break;
                default:
                    pop.PortalPhase = PopulationPortalSequencePhase.None;
                    return;
            }

            if (!TryValidateBakedPortalPose(
                    graph,
                    pop,
                    targetPose,
                    requireDynamicClearance: false,
                    out _))
            {
                FailPortalTraversal(pop, portal, now);
                return;
            }

            bool arrived = MoveTowardPose(graph, pop, targetPose, dt, out bool blocked);
            if (blocked)
            {
                FailPortalTraversal(pop, portal, now);
                return;
            }
            if (arrived)
            {
                pop.PortalPhase = pop.PortalPhase switch
                {
                    PopulationPortalSequencePhase.Interior => PopulationPortalSequencePhase.Approach,
                    PopulationPortalSequencePhase.Approach => PopulationPortalSequencePhase.Door,
                    PopulationPortalSequencePhase.Threshold => PopulationPortalSequencePhase.Exterior,
                    PopulationPortalSequencePhase.Exterior => PopulationPortalSequencePhase.None,
                    _ => pop.PortalPhase,
                };
                if (pop.PortalPhase == PopulationPortalSequencePhase.None)
                {
                    pop.RouteReason = PopulationRouteReason.Wander;
                    if (pop.MovementBehavior == SharedAiMovementMode.Route)
                    {
                        pop.AiState = PopulationAiState.FollowingRoute;
                        EnsureNextNode(graph, pop);
                    }
                    else
                    {
                        pop.AiState = PopulationAiState.Idle;
                        pop.HasRoamTarget = false;
                        pop.HomePosition = pop.Actor.Position;
                        pop.NextRoamDecisionAt = now;
                    }
                }
            }
        }

        private bool MoveTowardPose(MapGraph graph, PopulationActorRuntime pop,
            ServerPose targetPose, float dt, out bool blocked)
        {
            blocked = false;
            if (graph == null || graph.Collision == null || pop == null || pop.Actor == null || pop.Motor == null)
                return false;

            WorldPosition target = targetPose.ToWorldPosition();
            float dx = target.X - pop.Actor.Position.X;
            float dz = target.Z - pop.Actor.Position.Z;
            float distSq = dx * dx + dz * dz;
            if (distSq <= 0.18f * 0.18f && IsGroundedPortalArrival(graph, pop, target))
            {
                pop.Actor.YawDegrees = NormalizeYaw(targetPose.yaw);
                return true;
            }

            float inv = 1f / MathF.Sqrt(Math.Max(0.0001f, distSq));
            var intent = new CharacterMovementIntent(dx * inv, dz * inv, false, false);
            pop.Actor.YawDegrees = NormalizeYaw(targetPose.yaw);
            long motorStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                pop.Motor.Tick(pop.MotorState, intent, dt, graph.Collision,
                    allowWallSlide: false, out blocked);
            }
            finally
            {
                _diagnosticMotorCollisionMs += ElapsedDiagnosticMilliseconds(motorStartedAt);
                ++_diagnosticMotorCalls;
            }
            ApplyMotor(pop);
            return false;
        }

        private static bool IsGroundedPortalArrival(MapGraph graph, PopulationActorRuntime pop, WorldPosition target)
        {
            if (graph?.Collision == null || pop?.Actor == null)
                return false;

            float verticalTolerance = Math.Max(0.20f, pop.CapsuleRadius * 0.75f);
            if (Math.Abs(pop.Actor.Position.Y - target.Y) > verticalTolerance)
                return false;

            float radius = Math.Max(0.2f, pop.CapsuleRadius);
            float height = Math.Max(pop.CapsuleHeight, radius * 2f);
            if (!graph.Collision.TryFindGround(
                    pop.Actor.Position,
                    radius,
                    verticalTolerance,
                    verticalTolerance + 0.10f,
                    55f,
                    out ServerGroundHit ground))
            {
                return false;
            }

            return Math.Abs(ground.Position.Y - target.Y) <= verticalTolerance &&
                   graph.Collision.IsStandingCapsuleClear(
                       ground.Position,
                       new ServerCapsule(radius, height),
                       55f);
        }

        private static bool TryValidateBakedPortalPose(
            MapGraph graph,
            PopulationActorRuntime pop,
            ServerPose pose,
            bool requireDynamicClearance,
            out WorldPosition grounded)
        {
            grounded = default;
            if (graph?.Collision == null || pop == null)
                return false;

            WorldPosition authored = pose.ToWorldPosition();
            float radius = Math.Max(0.2f, pop.CapsuleRadius);
            float height = Math.Max(pop.CapsuleHeight, radius * 2f);

            if (!graph.Collision.TryFindGround(
                    authored,
                    radius,
                    0.15f,
                    0.15f,
                    55f,
                    out ServerGroundHit ground))
            {
                return false;
            }

            if (Math.Abs(ground.Position.Y - authored.Y) > 0.10f)
                return false;

            if (requireDynamicClearance &&
                !graph.Collision.IsStandingCapsuleClear(
                    ground.Position,
                    new ServerCapsule(radius, height),
                    55f))
            {
                return false;
            }

            grounded = ground.Position;
            return true;
        }

        private void FailPortalTraversal(PopulationActorRuntime pop, ServerPopulationPortal portal, double now)
        {
            if (pop == null || pop.Actor == null || portal == null)
                return;

            pop.PortalRespawnAttempts++;
            pop.PortalPhase = PopulationPortalSequencePhase.None;
            pop.AiState = PopulationAiState.PortalDormant;
            pop.SimulationLod = PopulationSimulationLod.Dormant;
            pop.RouteReason = PopulationRouteReason.PortalTravel;
            pop.NextNodeId = 0;
            pop.Actor.VelocityX = pop.Actor.VelocityY = pop.Actor.VelocityZ = 0f;

            WorldPosition safe = pop.Actor.LastSafePosition;
            pop.Actor.Position = safe;
            pop.MotorState = new CharacterMotorState(safe);
            pop.DormantUntil = now + Math.Max(0.25f, portal.blockedRetryDelay);
            ParkForPlayerActivation(pop, portal.exterior.ToWorldPosition(), now);
        }

        private bool EnsureNextNode(MapGraph graph, PopulationActorRuntime pop)
        {
            if (pop.NextNodeId > 0 && ResolveRouteAssignment(graph, pop)) return true;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return SelectNextNode(graph, pop); }
            finally { _diagnosticRouteSelectMs += ElapsedDiagnosticMilliseconds(startedAt); }
        }

        private bool SelectNextNode(MapGraph graph, PopulationActorRuntime pop)
        {
            if (pop.CurrentNodeId <= 0 || !graph.Nodes.ContainsKey(pop.CurrentNodeId))
                pop.CurrentNodeId = FindNearestNode(graph, pop.Actor.Position);
            ServerPopulationRouteEdge edge;
            if (pop.SpawnKind == ServerSpawnKind.Population)
            {
                graph.AmbientRouteSuccessors.TryGetValue(
                    (pop.CurrentNodeId, pop.PreviousNodeId, pop.NpcType), out edge);
                if (edge == null)
                    graph.AmbientRouteSuccessors.TryGetValue(
                        (pop.CurrentNodeId, 0, pop.NpcType), out edge);
            }
            else
            {
                long blockedDestination = BlockedDestinationFor(pop, pop.CurrentNodeId, pop.LastSimulationAt);
                edge = ChooseEdge(graph, pop.CurrentNodeId, pop.PreviousNodeId,
                    pop.NpcType, pop.Random, blockedDestination);
            }
            if (edge == null) return false;
            pop.NextNodeId = edge.toNodeId;
            pop.SegmentProgress = 0f;
            pop.LaneOffset = ChooseLaneOffset(edge.width, pop.Random);
            ResolveRouteAssignment(graph, pop);
            return true;
        }

        private static ServerPopulationRouteEdge ChooseEdge(
            MapGraph graph,
            long nodeId,
            long previousNodeId,
            PopulationNpcType npcType,
            Random random,
            long excludedToNodeId = 0)
        {
            if (nodeId <= 0 || !graph.Outgoing.TryGetValue(nodeId, out List<ServerPopulationRouteEdge> edges) || edges.Count == 0)
                return null;

            // First determine whether a non-U-turn option exists. Immediate reversal is then
            // suppressed at intersections but remains legal at dead ends.
            bool hasForwardOption = false;
            for (int i = 0; i < edges.Count; ++i)
            {
                ServerPopulationRouteEdge edge = edges[i];
                if (edge == null || !EdgeAllows(graph, edge, npcType) ||
                    edge.toNodeId == excludedToNodeId)
                    continue;
                if (edge.toNodeId != previousNodeId)
                {
                    hasForwardOption = true;
                    break;
                }
            }

            float total = 0f;
            for (int i = 0; i < edges.Count; ++i)
            {
                ServerPopulationRouteEdge edge = edges[i];
                if (edge == null || !EdgeAllows(graph, edge, npcType) ||
                    edge.toNodeId == excludedToNodeId)
                    continue;
                if (hasForwardOption && edge.toNodeId == previousNodeId)
                    continue;
                total += EdgeSelectionWeight(graph, edge);
            }
            if (total <= 0f)
                return null;

            double pick = random.NextDouble() * total;
            ServerPopulationRouteEdge fallback = null;
            for (int i = 0; i < edges.Count; ++i)
            {
                ServerPopulationRouteEdge edge = edges[i];
                if (edge == null || !EdgeAllows(graph, edge, npcType) ||
                    edge.toNodeId == excludedToNodeId)
                    continue;
                if (hasForwardOption && edge.toNodeId == previousNodeId)
                    continue;
                fallback = edge;
                pick -= EdgeSelectionWeight(graph, edge);
                if (pick <= 0d)
                    return edge;
            }
            return fallback;
        }

        private static bool EdgeAllows(MapGraph graph, ServerPopulationRouteEdge edge, PopulationNpcType npcType)
        {
            if (edge == null || edge.toNodeId <= 0 || !graph.Nodes.TryGetValue(edge.toNodeId, out ServerPopulationRouteNode target))
                return false;
            if (target.hardRestricted)
                return false;
            PopulationNpcTypeMask mask = ToNpcMask(npcType);
            return target.allowedNpcTypes == PopulationNpcTypeMask.All || (target.allowedNpcTypes & mask) != 0;
        }

        private static float EdgeSelectionWeight(MapGraph graph, ServerPopulationRouteEdge edge)
        {
            float weight = Math.Max(0.01f, edge.weight);
            if (graph.Nodes.TryGetValue(edge.toNodeId, out ServerPopulationRouteNode target))
                weight *= Math.Max(0.01f, target.routeWeight);
            return weight;
        }

        private static bool PortalAllows(ServerPopulationPortal portal, PopulationNpcType npcType)
        {
            if (portal == null)
                return false;
            PopulationNpcTypeMask mask = ToNpcMask(npcType);
            return portal.allowedNpcTypes == PopulationNpcTypeMask.All || (portal.allowedNpcTypes & mask) != 0;
        }

        private static PopulationNpcTypeMask ToNpcMask(PopulationNpcType type) => type switch
        {
            PopulationNpcType.Resident => PopulationNpcTypeMask.Resident,
            PopulationNpcType.Shopper => PopulationNpcTypeMask.Shopper,
            PopulationNpcType.Worker => PopulationNpcTypeMask.Worker,
            PopulationNpcType.Homeless => PopulationNpcTypeMask.Homeless,
            PopulationNpcType.Nightlife => PopulationNpcTypeMask.Nightlife,
            PopulationNpcType.Criminal => PopulationNpcTypeMask.Criminal,
            PopulationNpcType.GangMember => PopulationNpcTypeMask.GangMember,
            PopulationNpcType.Police => PopulationNpcTypeMask.Police,
            PopulationNpcType.Hunter => PopulationNpcTypeMask.Hunter,
            PopulationNpcType.Ghoul => PopulationNpcTypeMask.Ghoul,
            _ => PopulationNpcTypeMask.Civilian,
        };

        private static float ChooseLaneOffset(float width, Random random)
        {
            float half = Math.Max(0f, width * 0.35f);
            if (half <= 0.05f) return 0f;
            int lane = random.Next(0, 3) - 1;
            return lane * half;
        }

        private bool ShouldHibernateForPlayerDistance(PopulationActorRuntime pop, float nearestPlayerSq)
        {
            if (pop == null || pop.Actor == null || pop.AwaitingPlayerActivation ||
                pop.AiState == PopulationAiState.PortalDormant ||
                pop.PortalPhase != PopulationPortalSequencePhase.None ||
                pop.ThreatUntil > pop.LastSimulationAt)
            {
                return false;
            }

            float hibernate = Math.Max(PlayerActivationDistance + 1f, PlayerHibernateDistance);
            return nearestPlayerSq > hibernate * hibernate;
        }

        private void EnterAoiHibernate(PopulationActorRuntime pop, double now)
        {
            if (pop == null || pop.Actor == null || pop.AwaitingPlayerActivation || !pop.Actor.Alive)
                return;

            pop.SimulationLod = PopulationSimulationLod.Dormant;
            pop.AwaitingPlayerActivation = true;
            pop.HibernatedAt = Math.Max(0d, now);
            pop.Actor.VelocityX = pop.Actor.VelocityY = pop.Actor.VelocityZ = 0f;
            AddToHibernationIndex(pop, pop.Actor.Position);
            _actors.SuspendSpatial(pop.Actor);
            Changed?.Invoke(pop);
        }

        private void ParkForPlayerActivation(PopulationActorRuntime pop, WorldPosition activationPosition, double now)
        {
            if (pop == null || pop.Actor == null)
                return;
            pop.SimulationLod = PopulationSimulationLod.Dormant;
            pop.AwaitingPlayerActivation = true;
            pop.HibernatedAt = Math.Max(0d, now);
            AddToHibernationIndex(pop, activationPosition);
            _actors.SuspendSpatial(pop.Actor);
            Changed?.Invoke(pop);
        }

        private void WakeHibernatedNearPlayers(double now)
        {
            long wakeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _wakeScanPending = false;
            _wakeSearchPlayers.Clear();
            if (_hibernatingCount <= 0 || _playersByPartition.Count == 0)
            {
                _wakePlayerCursor = _wakeCellCursor = _wakeActorCursor = 0;
                return;
            }

            // Build a reusable flat player view. Multiple maps/instances retain their own
            // hibernation cells, and no dormant actor receives an individual polling timer.
            foreach (KeyValuePair<string, PlayerSpatialPartition> pair in _playersByPartition)
            {
                PlayerSpatialPartition partition = pair.Value;
                if (partition == null || partition.Players.Count == 0 ||
                    !_hibernatedByPartition.TryGetValue(
                        pair.Key, out Dictionary<long, List<PopulationActorRuntime>> cells))
                    continue;

                // One search origin per occupied 96 m hibernation cell, not
                // one per overlapping player. Preserve per-map/instance partitions.
                _wakePlayerCenterCells.Clear();
                for (int p = 0; p < partition.Players.Count; ++p)
                {
                    PopulationPlayerView player = partition.Players[p];
                    long centerCell = PackPlayerCell(
                        ToHibernationCell(player.Position.X),
                        ToHibernationCell(player.Position.Z));
                    if (_wakePlayerCenterCells.Add(centerCell))
                        _wakeSearchPlayers.Add(new WakeSearchPlayer(player, cells, partition));
                }
            }

            if (_wakeSearchPlayers.Count == 0)
            {
                _wakePlayerCursor = _wakeCellCursor = _wakeActorCursor = 0;
                return;
            }

            float activation = Math.Max(1f, PlayerActivationDistance);
            int radius = Math.Max(1, (int)Math.Ceiling(activation / HibernationCellSize));
            // The normal radius is one cell. Bound invalid/extreme authoring settings
            // rather than overflow the integer cell-count calculation.
            radius = Math.Min(radius, 256);
            int side = radius * 2 + 1;
            int cellsPerPlayer = side * side;
            float activationSq = activation * activation;
            int playerCellRadius = Math.Max(1,
                (int)Math.Ceiling(activation / PlayerSpatialCellSize));
            _wakePlayerCursor %= _wakeSearchPlayers.Count;
            _wakeCellCursor %= cellsPerPlayer;
            _wakeActorCursor = Math.Max(0, _wakeActorCursor);
            _activationScratch.Clear();

            int cellChecks = 0;
            int actorChecks = 0;
            int completedPlayers = 0;
            while (completedPlayers < _wakeSearchPlayers.Count &&
                   cellChecks < MaxWakeCellsPerPass &&
                   actorChecks < MaxWakeActorsCheckedPerPass &&
                   _activationScratch.Count < MaxWakeActionsPerPass)
            {
                WakeSearchPlayer entry = _wakeSearchPlayers[_wakePlayerCursor];
                int centerX = ToHibernationCell(entry.Player.Position.X);
                int centerZ = ToHibernationCell(entry.Player.Position.Z);
                int dx = _wakeCellCursor % side - radius;
                int dz = _wakeCellCursor / side - radius;

                ++cellChecks;
                if (entry.Cells.TryGetValue(
                        PackPlayerCell(centerX + dx, centerZ + dz),
                        out List<PopulationActorRuntime> cell) && cell != null)
                {
                    // The cell may shrink when a previous pass wakes a Pop. Clamp the
                    // continuation cursor and eventually revisit cells on each rotation.
                    _wakeActorCursor = Math.Min(_wakeActorCursor, cell.Count);
                    while (_wakeActorCursor < cell.Count &&
                           actorChecks < MaxWakeActorsCheckedPerPass &&
                           _activationScratch.Count < MaxWakeActionsPerPass)
                    {
                        PopulationActorRuntime pop = cell[_wakeActorCursor++];
                        ++actorChecks;
                        if (pop == null || !pop.AwaitingPlayerActivation || pop.Actor == null)
                            continue;

                        if (HasWakeObserverWithin(
                                entry.PlayerPartition,
                                pop.HibernationPosition,
                                activationSq,
                                playerCellRadius))
                            _activationScratch.Add(pop);
                    }
                    if (_wakeActorCursor < cell.Count)
                        break; // Resume the same dense cell next AI tick.
                }

                _wakeActorCursor = 0;
                if (++_wakeCellCursor >= cellsPerPlayer)
                {
                    _wakeCellCursor = 0;
                    if (++_wakePlayerCursor >= _wakeSearchPlayers.Count)
                        _wakePlayerCursor = 0;
                    ++completedPlayers;
                }
            }

            _diagnosticWakeCells = cellChecks;
            _diagnosticWakeActorChecks = actorChecks;
            _diagnosticWakeOrigins = _wakeSearchPlayers.Count;
            _diagnosticWakeCandidates = _activationScratch.Count;
            _diagnosticWakeScanMs = ElapsedDiagnosticMilliseconds(wakeStartedAt);
            long admissionStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();

            // An already-created, already-positioned civilian resumes from its
            // existing route state. This is NOT a new portal spawn and must NOT
            // consume portal/density admission slots or global spawn cooldowns.
            // One expensive reactivation cannot multiply into a long batch spike.
            foreach (PopulationActorRuntime pop in _activationScratch)
            {
                if (_diagnosticWakeAdmitted > 0 &&
                    ElapsedDiagnosticMilliseconds(admissionStartedAt) >= MaxWakeResumeWorkMillisecondsPerPass)
                    break;
                if (pop == null || !pop.AwaitingPlayerActivation)
                    continue;
                if (pop.AiState == PopulationAiState.PortalDormant)
                {
                    // TRUE portal release: preserve authored cadence, live clearance,
                    // density and global admission throttles unchanged.
                    long portalStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    WakePortalDormantImmediately(pop, now);
                    _diagnosticPortalMs += ElapsedDiagnosticMilliseconds(portalStartedAt);
                }
                else
                {
                    // Precreated or previously active NPC: spatial re-entry only.
                    // AOI observers still receive the canonical PlayerEntity spawn.
                    WakeImmediately(pop, now, catchUp: false);
                }
                if (!pop.AwaitingPlayerActivation)
                    ++_diagnosticWakeAdmitted;
            }
            _diagnosticWakeAdmissionMs = ElapsedDiagnosticMilliseconds(admissionStartedAt);
            _activationScratch.Clear();

            _wakeScanPending = _diagnosticWakeAdmitted > 0 ||
                               cellChecks >= MaxWakeCellsPerPass ||
                               actorChecks >= MaxWakeActorsCheckedPerPass ||
                               completedPlayers < _wakeSearchPlayers.Count;
        }

        /// <summary>
        /// Portal-backed Population is admitted directly from the shared AOI activation pass.
        /// This avoids funneling a whole dormant portal queue through one-per-actor scheduler
        /// wakeups. The per-pass release ceiling bounds scheduler work; authored portal slots
        /// remain the source of civilian population capacity.
        /// </summary>
        private void WakePortalDormantImmediately(PopulationActorRuntime pop, double now)
        {
            if (pop == null || pop.Actor == null || !pop.AwaitingPlayerActivation ||
                pop.AiState != PopulationAiState.PortalDormant)
            {
                return;
            }

            if (now < pop.DormantUntil ||
                _portalRespawnsThisTick >= Math.Max(1, MaximumPortalRespawnsPerTick))
            {
                return;
            }

            if (!TryGetGraph(pop.Actor, out MapGraph graph) ||
                !graph.Portals.TryGetValue(pop.LastPortalId, out ServerPopulationPortal portal))
            {
                WakeImmediately(pop, now, catchUp: false);
                return;
            }

            IReadOnlyList<PopulationPlayerView> players = GetPartitionPlayers(pop);
            if (!HasPlayerWithin(pop.Actor, portal.exterior.ToWorldPosition(), players, PlayerActivationDistance))
                return;

            if (portal.mode != PopulationPortalMode.DespawnOnly &&
                !PortalCadenceAllows(portal, now, out _))
                return;

            if (!PortalSpawnClear(graph, portal, pop, players))
            {
                pop.PortalRespawnAttempts++;
                pop.DormantUntil = now + Math.Max(0.25f, portal.blockedRetryDelay);
                return;
            }

            if (!ActivatePortalSpawn(graph, pop, portal, now, scheduleImmediately: true))
            {
                pop.PortalRespawnAttempts++;
                pop.DormantUntil = now + Math.Max(0.25f, portal.blockedRetryDelay);
            }
        }

        private void WakeImmediately(PopulationActorRuntime pop, double now, bool catchUp)
        {
            if (pop == null || pop.Actor == null || !pop.AwaitingPlayerActivation)
                return;

            RemoveFromHibernationIndex(pop);
            pop.AwaitingPlayerActivation = false;

            if (pop.AiState == PopulationAiState.PortalDormant)
            {
                if (now + 0.000001d >= pop.DormantUntil)
                    ScheduleNow(pop, now);
                else
                    Schedule(pop, pop.DormantUntil);
                return;
            }

            if (catchUp)
                AdvanceLogicalElapsed(pop, now);

            if (pop.AiState == PopulationAiState.PortalDormant)
            {
                if (now + 0.000001d >= pop.DormantUntil)
                    ScheduleNow(pop, now);
                else
                    Schedule(pop, pop.DormantUntil);
                return;
            }

            // Wake profiling is included only in the existing severe-work warning.
            // It measures the previously opaque synchronous presentation callbacks.
            long stage = System.Diagnostics.Stopwatch.GetTimestamp();
            _actors.ResumeSpatial(pop.Actor);
            _diagnosticWakeSpatialMs += ElapsedDiagnosticMilliseconds(stage);
            stage = System.Diagnostics.Stopwatch.GetTimestamp();
            _actors.PublishChanged(pop.Actor);
            _diagnosticWakeActorPublishMs += ElapsedDiagnosticMilliseconds(stage);
            stage = System.Diagnostics.Stopwatch.GetTimestamp();
            Changed?.Invoke(pop);
            _diagnosticWakeObserverPublishMs += ElapsedDiagnosticMilliseconds(stage);
            stage = System.Diagnostics.Stopwatch.GetTimestamp();
            ScheduleNow(pop, now);
            _diagnosticWakeScheduleMs += ElapsedDiagnosticMilliseconds(stage);
        }

        private void AdvanceLogicalElapsed(PopulationActorRuntime pop, double now)
        {
            if (pop == null || pop.Actor == null || !pop.Actor.Alive || !TryGetGraph(pop.Actor, out MapGraph graph))
                return;

            double start = Math.Max(0d, pop.HibernatedAt);
            double elapsed = Math.Max(0d, now - start);
            elapsed = Math.Min(elapsed, Math.Max(1d, MaximumLogicalCatchUpSeconds));
            if (elapsed <= 0.0001d)
                return;

            // Route actors can cheaply advance deterministically while hibernated. Free-roam
            // actors have no authored path to integrate off-screen, so wake at their last safe
            // position and choose a fresh direction without spending dormant CPU.
            if (pop.MovementBehavior != SharedAiMovementMode.Route)
            {
                pop.HasRoamTarget = false;
                pop.WaitUntil = 0d;
                pop.NextRoamDecisionAt = now;
                pop.AiState = PopulationAiState.Idle;
                pop.Actor.VelocityX = 0f;
                pop.Actor.VelocityY = 0f;
                pop.Actor.VelocityZ = 0f;
                pop.MotorState = new CharacterMotorState(pop.Actor.Position);
                return;
            }

            double cursor = now - elapsed;
            long catchUpStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            int hops = 0;
            int maxHops = Math.Max(8, MaximumLogicalCatchUpHops);
            if (pop.SpawnKind == ServerSpawnKind.Population)
                maxHops = Math.Min(maxHops, Math.Max(1, MaximumAmbientWakeCatchUpHops));
            while (elapsed > 0.0001d && hops < maxHops && pop.Actor.Alive && pop.AiState != PopulationAiState.PortalDormant)
            {
                if (pop.WaitUntil > cursor)
                {
                    double wait = Math.Min(elapsed, pop.WaitUntil - cursor);
                    cursor += wait;
                    elapsed -= wait;
                    if (elapsed <= 0.0001d)
                        break;
                }

                if (!EnsureNextNode(graph, pop) ||
                    !graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode from) ||
                    !graph.Nodes.TryGetValue(pop.NextNodeId, out ServerPopulationRouteNode to))
                {
                    break;
                }

                float edgeDistance = DistanceXZ(from.pose.ToWorldPosition(), to.pose.ToWorldPosition());
                if (edgeDistance < 0.05f)
                {
                    pop.Actor.Position = to.pose.ToWorldPosition();
                    ArriveNode(graph, pop, cursor);
                    hops++;
                    continue;
                }

                float speed = Math.Max(0.1f, pop.WalkSpeed);
                float remainingDistance = edgeDistance * Math.Max(0f, 1f - pop.SegmentProgress);
                double travelSeconds = remainingDistance / speed;
                if (elapsed + 0.000001d < travelSeconds)
                {
                    pop.SegmentProgress += (float)(speed * elapsed / edgeDistance);
                    cursor += elapsed;
                    elapsed = 0d;
                    break;
                }

                pop.Actor.Position = to.pose.ToWorldPosition();
                pop.Actor.YawDegrees = to.pose.yaw;
                cursor += travelSeconds;
                elapsed -= travelSeconds;
                ArriveNode(graph, pop, cursor);
                hops++;
            }

            _diagnosticCatchUpHops = hops;
            if (pop.AiState != PopulationAiState.PortalDormant &&
                TryPositionOnEdge(graph, pop.CurrentNodeId, pop.NextNodeId, pop.SegmentProgress, pop.LaneOffset, out WorldPosition position, out float yaw))
            {
                if (graph.Collision != null && graph.Collision.TryFindGround(position, 0.3f, 1f, 2f, 55f, out ServerGroundHit ground))
                    position = new WorldPosition(position.X, ground.Position.Y, position.Z);
                pop.Actor.Position = position;
                pop.Actor.LastSafePosition = position;
                pop.Actor.YawDegrees = yaw;
                pop.MotorState = new CharacterMotorState(position);
            }
            _diagnosticCatchUpMs += ElapsedDiagnosticMilliseconds(catchUpStartedAt);
        }

        private void AddToHibernationIndex(PopulationActorRuntime pop, WorldPosition position)
        {
            if (pop == null || pop.Actor == null)
                return;

            RemoveFromHibernationIndex(pop);
            string partitionKey = MapKey(pop.Actor.MapId, pop.Actor.InstanceId);
            if (!_hibernatedByPartition.TryGetValue(partitionKey, out Dictionary<long, List<PopulationActorRuntime>> cells))
            {
                cells = new Dictionary<long, List<PopulationActorRuntime>>();
                _hibernatedByPartition.Add(partitionKey, cells);
            }

            long cellKey = PackPlayerCell(ToHibernationCell(position.X), ToHibernationCell(position.Z));
            if (!cells.TryGetValue(cellKey, out List<PopulationActorRuntime> cell))
            {
                cell = new List<PopulationActorRuntime>(8);
                cells.Add(cellKey, cell);
            }
            cell.Add(pop);
            pop.HibernationPartitionKey = partitionKey;
            pop.HibernationCellKey = cellKey;
            pop.HibernationPosition = position;
            _hibernatingCount++;
        }

        private void RemoveFromHibernationIndex(PopulationActorRuntime pop)
        {
            if (pop == null || string.IsNullOrEmpty(pop.HibernationPartitionKey))
                return;
            if (_hibernatedByPartition.TryGetValue(pop.HibernationPartitionKey, out Dictionary<long, List<PopulationActorRuntime>> cells) &&
                cells.TryGetValue(pop.HibernationCellKey, out List<PopulationActorRuntime> cell))
            {
                if (cell.Remove(pop))
                    _hibernatingCount = Math.Max(0, _hibernatingCount - 1);
                if (cell.Count == 0)
                    cells.Remove(pop.HibernationCellKey);
                if (cells.Count == 0)
                    _hibernatedByPartition.Remove(pop.HibernationPartitionKey);
            }
            pop.HibernationPartitionKey = string.Empty;
            pop.HibernationCellKey = 0;
        }

        private static int ToHibernationCell(float value) =>
            (int)MathF.Floor(value / HibernationCellSize);

        private static bool HasPlayerWithin(
            AuthoritativeActorRuntime actor,
            WorldPosition position,
            IReadOnlyList<PopulationPlayerView> players,
            float distance)
        {
            if (actor == null || players == null || players.Count == 0)
                return false;
            float maxSq = Math.Max(1f, distance) * Math.Max(1f, distance);
            for (int i = 0; i < players.Count; ++i)
            {
                PopulationPlayerView player = players[i];
                if (!SamePartition(actor, player))
                    continue;
                float dx = position.X - player.Position.X;
                float dz = position.Z - player.Position.Z;
                if (dx * dx + dz * dz <= maxSq)
                    return true;
            }
            return false;
        }

        private static bool TryPositionOnAssignedSegment(MapGraph graph, PopulationActorRuntime pop,
            out WorldPosition position, out float yaw)
        {
            PopulationRouteSegment segment = pop.AssignedRouteSegment;
            if (segment == null)
                return TryPositionOnEdge(graph, pop.CurrentNodeId, pop.NextNodeId,
                    pop.SegmentProgress, pop.LaneOffset, out position, out yaw);
            float t = Math.Clamp(pop.SegmentProgress, 0f, 1f);
            position = new WorldPosition(
                segment.From.pose.x + segment.Dx * t + segment.SideX * pop.LaneOffset,
                segment.From.pose.y + (segment.To.pose.y - segment.From.pose.y) * t,
                segment.From.pose.z + segment.Dz * t + segment.SideZ * pop.LaneOffset);
            yaw = segment.Yaw;
            return true;
        }

        private static bool TryPositionOnEdge(MapGraph graph, long fromId, long toId, float progress, float laneOffset, out WorldPosition position, out float yaw)
        {
            position = default;
            yaw = 0f;
            if (!graph.Nodes.TryGetValue(fromId, out ServerPopulationRouteNode from) || !graph.Nodes.TryGetValue(toId, out ServerPopulationRouteNode to))
                return false;
            float t = Math.Clamp(progress, 0f, 1f);
            float dx = to.pose.x - from.pose.x;
            float dz = to.pose.z - from.pose.z;
            float length = MathF.Sqrt(dx * dx + dz * dz);
            float sideX = length > 0.001f ? -dz / length : 0f;
            float sideZ = length > 0.001f ? dx / length : 0f;
            position = new WorldPosition(
                from.pose.x + (to.pose.x - from.pose.x) * t + sideX * laneOffset,
                from.pose.y + (to.pose.y - from.pose.y) * t,
                from.pose.z + (to.pose.z - from.pose.z) * t + sideZ * laneOffset);
            yaw = MathF.Atan2(dx, dz) * (180f / MathF.PI);
            return true;
        }

        private static WorldPosition OffsetTargetForLane(MapGraph graph, PopulationActorRuntime pop, WorldPosition target)
        {
            PopulationRouteSegment segment = pop.AssignedRouteSegment;
            if (ReferenceEquals(pop.AssignedRouteGraph, graph) &&
                pop.AssignedFromId == pop.CurrentNodeId && pop.AssignedToId == pop.NextNodeId &&
                segment != null)
                return new WorldPosition(target.X + segment.SideX * pop.LaneOffset,
                    target.Y, target.Z + segment.SideZ * pop.LaneOffset);
            if (!graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode from)) return target;
            float dx = target.X - from.pose.x;
            float dz = target.Z - from.pose.z;
            float length = MathF.Sqrt(dx * dx + dz * dz);
            if (length <= 0.001f) return target;
            return new WorldPosition(target.X - dz / length * pop.LaneOffset, target.Y, target.Z + dx / length * pop.LaneOffset);
        }

        private bool TryResolveSpawn(MapGraph graph, ServerSpawnAnchor anchor, out ServerPose pose)
        {
            pose = anchor.pose;
            if (anchor.kind == ServerSpawnKind.Population)
            {
                // Fixed Population slots are hand-authored and their physical placement
                // is verified when the map is baked. Portal-owned slots use the SAME
                // resolved portal pose used for every subsequent wake/respawn.
                if (anchor.portalId > 0 &&
                    graph.Portals.TryGetValue(anchor.portalId, out ServerPopulationPortal portal))
                    pose = portal.exterior;

                return float.IsFinite(pose.x) && float.IsFinite(pose.y) &&
                       float.IsFinite(pose.z) && float.IsFinite(pose.yaw);
            }

            // Keep the previous full validation for Monster/NPC and other non-Population
            // archetypes. This patch changes only the measured ambient Population path.
            if (graph.Collision == null) return true;
            var capsule = new ServerCapsule(anchor.capsuleRadius, anchor.capsuleHeight);
            return graph.Collision.TryValidateSpawn(anchor.pose, capsule, Math.Max(0.05f, anchor.maximumGroundSnap), 55f, out pose, out _);
        }

        private static long FindNearestNode(MapGraph graph, WorldPosition position)
        {
            long best = 0;
            float bestSq = float.PositiveInfinity;
            foreach (ServerPopulationRouteNode node in graph.Nodes.Values)
            {
                float dx = node.pose.x - position.X;
                float dz = node.pose.z - position.Z;
                float sq = dx * dx + dz * dz;
                if (sq < bestSq) { bestSq = sq; best = node.stableId; }
            }
            return best;
        }

        private bool TryGetGraph(AuthoritativeActorRuntime actor, out MapGraph graph) =>
            _graphs.TryGetValue(MapKey(actor.MapId, actor.InstanceId), out graph);

        private static bool SamePartition(AuthoritativeActorRuntime actor, PopulationPlayerView player) =>
            string.Equals(actor.MapId, player.MapId, StringComparison.Ordinal) &&
            string.Equals(actor.InstanceId, player.InstanceId, StringComparison.Ordinal);

        private static string GetCachedPartitionKey(PopulationActorRuntime pop)
        {
            AuthoritativeActorRuntime actor = pop?.Actor;
            if (actor == null)
                return string.Empty;

            string mapId = actor.MapId ?? string.Empty;
            string instanceId = actor.InstanceId ?? string.Empty;
            if (pop.CachedPartitionKey != null)
            {
                if (ReferenceEquals(pop.CachedPartitionMapId, mapId) &&
                    ReferenceEquals(pop.CachedPartitionInstanceId, instanceId))
                    return pop.CachedPartitionKey;

                if (string.Equals(pop.CachedPartitionMapId, mapId, StringComparison.Ordinal) &&
                    string.Equals(pop.CachedPartitionInstanceId, instanceId, StringComparison.Ordinal))
                {
                    pop.CachedPartitionMapId = mapId;
                    pop.CachedPartitionInstanceId = instanceId;
                    return pop.CachedPartitionKey;
                }
            }

            pop.CachedPartitionMapId = mapId;
            pop.CachedPartitionInstanceId = instanceId;
            pop.CachedPartitionKey = MapKey(mapId, instanceId);
            return pop.CachedPartitionKey;
        }

        private static string MapKey(string mapId, string instanceId) => (mapId ?? string.Empty) + "\n" + (instanceId ?? string.Empty);
        private static float DistanceXZ(WorldPosition a, WorldPosition b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        private static float NormalizeYaw(float value)
        {
            float result = value % 360f;
            return result < 0f ? result + 360f : result;
        }

        private static float DeterministicVariation(long seed, float min, float max)
        {
            unchecked
            {
                ulong x = (ulong)seed;
                x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
                double unit = (x & 0xFFFFFFUL) / (double)0xFFFFFFUL;
                return min + (float)unit * (max - min);
            }
        }
    }
}
