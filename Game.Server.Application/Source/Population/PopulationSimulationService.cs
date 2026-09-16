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

        public PopulationPlayerView(long characterId, string mapId, string instanceId, WorldPosition position)
        {
            CharacterId = characterId;
            MapId = mapId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            Position = position;
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

    public sealed class PopulationActorRuntime
    {
        internal CharacterMotorState MotorState;
        internal ServerCharacterMotor Motor;
        internal Random Random;
        internal long CurrentNodeId;
        internal long NextNodeId;
        internal float SegmentProgress;
        internal float LaneOffset;
        internal double WaitUntil;
        internal double DormantUntil;
        internal double LastProgressAt;
        internal WorldPosition LastProgressPosition;
        internal WorldPosition ThreatPosition;
        internal double ThreatUntil;
        internal long LastPortalId;
        internal PopulationPortalSequencePhase PortalPhase;
        internal int PortalRespawnAttempts;
        internal double LastSimulationAt;
        internal double NextSimulationAt;
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
    public sealed class PopulationSimulationService
    {
        private sealed class MapGraph
        {
            public ServerMapSnapshot Map;
            public Dictionary<long, ServerPopulationRouteNode> Nodes = new Dictionary<long, ServerPopulationRouteNode>();
            public Dictionary<long, List<ServerPopulationRouteEdge>> Outgoing = new Dictionary<long, List<ServerPopulationRouteEdge>>();
            public Dictionary<long, ServerPopulationPortal> Portals = new Dictionary<long, ServerPopulationPortal>();
            public Dictionary<long, ServerPopulationPortal> PortalByRouteNode = new Dictionary<long, ServerPopulationPortal>();
            public ServerCollisionWorld Collision;
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
            }

            public void Add(PopulationPlayerView player)
            {
                Players.Add(player);
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
        private readonly Dictionary<string, Dictionary<long, List<PopulationActorRuntime>>> _hibernatedByPartition =
            new Dictionary<string, Dictionary<long, List<PopulationActorRuntime>>>(StringComparer.Ordinal);
        private readonly HashSet<PopulationActorRuntime> _activationScratch = new HashSet<PopulationActorRuntime>();
        private readonly List<AuthoritativeActorRuntime> _nearbyScratch = new List<AuthoritativeActorRuntime>();
        private readonly Dictionary<long, PortalReleaseState> _portalReleaseStates =
            new Dictionary<long, PortalReleaseState>();
        private uint _portalAdmissionGeneration;
        private int _portalRespawnsThisTick;
        private int _hibernatingCount;
        private double _nextActivationHeartbeatAt;

        public float EngagedDistance { get; set; } = 28f;
        public float ActiveDistance { get; set; } = 80f;
        // Standalone GameServer may wire its existing CombatPresentationRange here.
        // <= 0 keeps legacy behavior by falling back to ActiveDistance.
        // This gate is intentionally FreeRoam-only; routed Population/NPC LOD is unchanged.
        public float FreeRoamActiveDistance { get; set; }
        public float CoarseDistance { get; set; } = 250f;
        public float VisibilitySuppressionDistance { get; set; } = 35f;
        public int MaximumPortalRespawnsPerTick { get; set; } = 8;
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
            BuildPlayerPartitions(players);
            _portalRespawnsThisTick = 0;
            _portalAdmissionGeneration = _portalAdmissionGeneration == uint.MaxValue
                ? 1u
                : _portalAdmissionGeneration + 1u;

            // Hibernated Population is intentionally absent from the normal per-actor due queue.
            // Advance a single shared activation heartbeat here so the core scheduler can probe
            // player presence without polling every dormant actor. This closes the scheduler
            // deadlock where HasDueWork() could remain false forever and therefore never call
            // PrepareBudgetedTick(), which is the place that wakes nearby dormant identities.
            _nextActivationHeartbeatAt = now + Math.Max(0.25d, ActivationHeartbeatSeconds);
            WakeHibernatedNearPlayers(now);
        }

        public bool HasDueWork(double now)
        {
            PruneStaleScheduleHeads();
            if (_dueSchedule.TryPeek(out _, out double due) && due <= now + 0.000001d)
                return true;

            // One lightweight shared heartbeat is enough to discover player proximity for all
            // hibernated Population. No dormant Pop receives a timer or simulation tick.
            return _hibernatingCount > 0 && now + 0.000001d >= _nextActivationHeartbeatAt;
        }

        public bool TickNextBudgeted(float fixedDelta, double now)
        {
            if (fixedDelta <= 0f || _populationSchedule.Count == 0)
                return false;

            PruneStaleScheduleHeads();
            if (!_dueSchedule.TryPeek(out PopulationScheduleEntry scheduled, out double due) ||
                due > now + 0.000001d)
                return false;

            _dueSchedule.Dequeue();
            PopulationActorRuntime pop = scheduled.Actor;
            if (pop == null || pop.Actor == null || scheduled.Version != pop.ScheduleVersion)
                return true;

            bool dead = !pop.Actor.Alive || pop.AiState == PopulationAiState.Dead;
            if (dead && pop.DeadDecayAt > 0d && now >= pop.DeadDecayAt)
            {
                RemovePopulation(pop);
                return true;
            }

            float actorDelta = fixedDelta;
            if (pop.LastSimulationAt > 0d && now > pop.LastSimulationAt)
                actorDelta = (float)Math.Min(2d, Math.Max(fixedDelta, now - pop.LastSimulationAt));
            pop.LastSimulationAt = now;

            IReadOnlyList<PopulationPlayerView> partitionPlayers = GetPartitionPlayers(pop.Actor);
            TickActor(pop, actorDelta, now, partitionPlayers);

            if (pop.AwaitingPlayerActivation)
                return true;

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
            return true;
        }

        private void PruneStaleScheduleHeads()
        {
            while (_dueSchedule.TryPeek(out PopulationScheduleEntry scheduled, out _))
            {
                PopulationActorRuntime pop = scheduled.Actor;
                if (pop != null && pop.Actor != null && scheduled.Version == pop.ScheduleVersion)
                    return;
                _dueSchedule.Dequeue();
            }
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
            UpdateSimulationLod(pop);

            // Canonical Population death is terminal for normal simulation until an explicit
            // future decay/respawn policy changes it. Do not let dormant/logical route ticking
            // overwrite Dead movement state and make a defeated Pop appear to respawn.
            if (!pop.Actor.Alive || pop.AiState == PopulationAiState.Dead)
            {
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
                TickDormant(pop, now, players);
                return;
            }

            if (ShouldHibernateForPlayerDistance(pop))
            {
                EnterAoiHibernate(pop, now);
                return;
            }

            if (pop.ThreatUntil > now)
            {
                TickThreat(pop, Math.Min(fixedDelta, 0.25f), now);
                return;
            }
            if (pop.AiState == PopulationAiState.Fleeing || pop.AiState == PopulationAiState.Fighting)
            {
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
                pop.AiState = PopulationAiState.Waiting;
                return;
            }

            if (pop.PortalPhase != PopulationPortalSequencePhase.None)
            {
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
                float nearestPlayerSq = GetNearestPlayerDistanceSquared(pop.Actor);
                if (nearestPlayerSq <= freeRoamDistance * freeRoamDistance)
                {
                    TickFreeRoam(pop, Math.Min(fixedDelta, 0.25f), now);
                }
                else
                {
                    TickStationary(pop);
                }
                return;
            }

            if (pop.MovementBehavior == SharedAiMovementMode.Stationary)
            {
                TickStationary(pop);
                return;
            }

            if (pop.SimulationLod <= PopulationSimulationLod.Logical)
                TickLogical(pop, fixedDelta, now);
            else if (pop.SimulationLod == PopulationSimulationLod.CoarseRoute)
                TickCoarse(pop, fixedDelta, now);
            else
                TickActive(pop, Math.Min(fixedDelta, 0.25f), now);
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

        private IReadOnlyList<PopulationPlayerView> GetPartitionPlayers(AuthoritativeActorRuntime actor)
        {
            if (actor != null &&
                _playersByPartition.TryGetValue(
                    MapKey(actor.MapId, actor.InstanceId),
                    out PlayerSpatialPartition partition))
            {
                return partition.Players;
            }
            return Array.Empty<PopulationPlayerView>();
        }

        private float GetNearestPlayerDistanceSquared(AuthoritativeActorRuntime actor)
        {
            if (actor == null ||
                !_playersByPartition.TryGetValue(
                    MapKey(actor.MapId, actor.InstanceId),
                    out PlayerSpatialPartition partition) ||
                partition.Players.Count == 0)
            {
                return float.PositiveInfinity;
            }

            float searchDistance = Math.Max(1f, CoarseDistance);
            int radius = Math.Max(1, (int)Math.Ceiling(searchDistance / PlayerSpatialCellSize));
            int centerX = ToPlayerCell(actor.Position.X);
            int centerZ = ToPlayerCell(actor.Position.Z);
            float nearestSq = float.PositiveInfinity;
            float coarseSq = searchDistance * searchDistance;

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

        private static int ToPlayerCell(float value) =>
            (int)MathF.Floor(value / PlayerSpatialCellSize);

        private static long PackPlayerCell(int x, int z) =>
            ((long)x << 32) ^ (uint)z;

        public bool NotifyThreat(long populationActorId, WorldPosition threatPosition, float severity, double now)
        {
            if (!_population.TryGetValue(populationActorId, out PopulationActorRuntime pop) || !pop.Actor.Alive)
                return false;

            if (pop.AwaitingPlayerActivation)
                WakeImmediately(pop, now, catchUp: true);

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
                _actors.PublishChanged(pop.Actor);
                Changed?.Invoke(pop);
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

        private void RemovePopulation(PopulationActorRuntime pop)
        {
            if (pop?.Actor == null)
                return;

            long actorId = pop.Actor.Handle.actorId;
            if (!_population.TryGetValue(actorId, out PopulationActorRuntime current) || !ReferenceEquals(current, pop))
                return;

            RemoveFromHibernationIndex(pop);
            _population.Remove(actorId);
            _populationSchedule.Remove(pop);
            pop.ScheduleVersion = pop.ScheduleVersion == uint.MaxValue ? 1u : pop.ScheduleVersion + 1u;
            _actors.Remove(pop.Actor.Handle);
            Removed?.Invoke(pop);
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
                    if (portal.routeNodeId > 0)
                        graph.PortalByRouteNode[portal.routeNodeId] = portal;
                }
                _graphs[key] = graph;
            }
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

        private void SpawnBakedPopulation()
        {
            foreach (MapGraph graph in _graphs.Values)
            {
                ServerSpawnAnchor[] anchors = graph.Map.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();
                for (int i = 0; i < anchors.Length; ++i)
                {
                    ServerSpawnAnchor anchor = anchors[i];
                    if (anchor == null || !anchor.enabled || !IsSharedAiSpawnKind(anchor.kind))
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
                        pop.Actor.Position = initialPortal.interiorSpawn.ToWorldPosition();
                        pop.Actor.LastSafePosition = pop.Actor.Position;
                        pop.MotorState = new CharacterMotorState(pop.Actor.Position);
                        pop.DormantUntil = 0d;
                        ParkForPlayerActivation(pop, initialPortal.exterior.ToWorldPosition(), 0d);
                    }
                    else
                    {
                        if (pop.MovementBehavior == SharedAiMovementMode.Route)
                            OrganicStartupDistribution(graph, pop, false);
                        EnterAoiHibernate(pop, 0d);
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
        }

        private void UpdateSimulationLod(PopulationActorRuntime pop)
        {
            if (pop.AiState == PopulationAiState.PortalDormant || !pop.Actor.Alive)
            {
                pop.SimulationLod = PopulationSimulationLod.Dormant;
                return;
            }

            float nearestSq = GetNearestPlayerDistanceSquared(pop.Actor);

            if (pop.ThreatUntil > 0d && (pop.AiState == PopulationAiState.Fleeing || pop.AiState == PopulationAiState.Fighting))
                pop.SimulationLod = PopulationSimulationLod.Engaged;
            else if (nearestSq <= EngagedDistance * EngagedDistance)
                pop.SimulationLod = PopulationSimulationLod.Engaged;
            else if (nearestSq <= ActiveDistance * ActiveDistance)
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
                _actors.PublishChanged(pop.Actor);
                Changed?.Invoke(pop);
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
                pop.Motor.Tick(pop.MotorState, intent, dt, graph.Collision);
                ApplyMotor(pop);
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
                _actors.PublishChanged(pop.Actor);
                Changed?.Invoke(pop);
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
            if (!TryGetGraph(pop.Actor, out MapGraph graph) || !EnsureNextNode(graph, pop)) return;
            if (!graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode from) ||
                !graph.Nodes.TryGetValue(pop.NextNodeId, out ServerPopulationRouteNode to))
                return;

            float distance = DistanceXZ(from.pose.ToWorldPosition(), to.pose.ToWorldPosition());
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

            if (TryPositionOnEdge(graph, pop.CurrentNodeId, pop.NextNodeId, pop.SegmentProgress, pop.LaneOffset, out WorldPosition position, out float yaw))
            {
                pop.Actor.Position = position;
                pop.Actor.YawDegrees = yaw;
                pop.Actor.MovementMode = ActorMovementMode.Grounded;
                pop.Actor.VelocityX = 0f;
                pop.Actor.VelocityY = 0f;
                pop.Actor.VelocityZ = 0f;
                _actors.PublishChanged(pop.Actor);
                Changed?.Invoke(pop);
            }
        }

        private void TickActive(PopulationActorRuntime pop, float dt, double now)
        {
            if (!TryGetGraph(pop.Actor, out MapGraph graph) || graph.Collision == null || !EnsureNextNode(graph, pop)) return;
            if (!graph.Nodes.TryGetValue(pop.NextNodeId, out ServerPopulationRouteNode targetNode)) return;

            WorldPosition target = OffsetTargetForLane(graph, pop, targetNode.pose.ToWorldPosition());
            float dx = target.X - pop.Actor.Position.X;
            float dz = target.Z - pop.Actor.Position.Z;
            float distSq = dx * dx + dz * dz;
            if (distSq <= 0.35f * 0.35f)
            {
                ArriveNode(graph, pop, now);
                return;
            }

            float inv = 1f / MathF.Sqrt(Math.Max(0.0001f, distSq));
            bool running = pop.AiState == PopulationAiState.Fleeing;
            var intent = new CharacterMovementIntent(dx * inv, dz * inv, running, false);
            pop.Actor.YawDegrees = NormalizeYaw(MathF.Atan2(dx, dz) * (180f / MathF.PI));
            pop.Motor.Tick(pop.MotorState, intent, dt, graph.Collision);
            ApplyMotor(pop);
            DetectStuck(graph, pop, now);
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
            float inv = 1f / MathF.Sqrt(lenSq);
            var intent = new CharacterMovementIntent(dx * inv, dz * inv, true, false);
            pop.Actor.YawDegrees = NormalizeYaw(MathF.Atan2(dx, dz) * (180f / MathF.PI));
            pop.Motor.Tick(pop.MotorState, intent, dt, graph.Collision);
            ApplyMotor(pop);
            DetectStuck(graph, pop, now);
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
            _actors.PublishChanged(pop.Actor);
            Changed?.Invoke(pop);
        }

        private void DetectStuck(MapGraph graph, PopulationActorRuntime pop, double now)
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
            pop.NextNodeId = 0;
            pop.SegmentProgress = 0f;
            pop.AiState = PopulationAiState.FollowingRoute;
            pop.RouteReason = PopulationRouteReason.Recovery;
            EnsureNextNode(graph, pop);
        }

        private void ArriveNode(MapGraph graph, PopulationActorRuntime pop, double now)
        {
            pop.PreviousNodeId = pop.CurrentNodeId;
            pop.CurrentNodeId = pop.NextNodeId;
            pop.NextNodeId = 0;
            pop.SegmentProgress = 0f;
            if (graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode node))
            {
                float minWait = Math.Max(0f, node.minimumWaitSeconds);
                float maxWait = Math.Max(minWait, node.maximumWaitSeconds);
                if (maxWait > 0f)
                    pop.WaitUntil = now + minWait + (maxWait - minWait) * pop.Random.NextDouble();
            }

            if (graph.PortalByRouteNode.TryGetValue(pop.CurrentNodeId, out ServerPopulationPortal portal) &&
                PortalAllows(portal, pop.NpcType) &&
                (portal.mode == PopulationPortalMode.DespawnOnly || portal.mode == PopulationPortalMode.SpawnAndDespawn))
            {
                EnterPortalDormant(pop, portal, now);
                return;
            }
            EnsureNextNode(graph, pop);
        }

        private void EnterPortalDormant(PopulationActorRuntime pop, ServerPopulationPortal portal, double now)
        {
            RemoveFromHibernationIndex(pop);
            pop.AwaitingPlayerActivation = false;
            pop.AiState = PopulationAiState.PortalDormant;
            pop.SimulationLod = PopulationSimulationLod.Dormant;
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
            if (!TryGetGraph(pop.Actor, out MapGraph graph) || !graph.Portals.TryGetValue(pop.LastPortalId, out ServerPopulationPortal portal))
            {
                pop.AiState = PopulationAiState.Recovering;
                ReattachToNearestRoute(pop);
                return;
            }
            if (portal.mode == PopulationPortalMode.DespawnOnly)
            {
                // Sink-only portals remain off-world until a future schedule/destination service
                // assigns another source portal. Do not poll them while nobody can observe them.
                ParkForPlayerActivation(pop, portal.exterior.ToWorldPosition(), now);
                return;
            }

            if (!HasPlayerWithin(pop.Actor, portal.exterior.ToWorldPosition(), players, PlayerActivationDistance))
            {
                ParkForPlayerActivation(pop, portal.exterior.ToWorldPosition(), now);
                return;
            }

            if (!PortalCadenceAllows(portal, now, out double nextPortalReleaseAt))
            {
                pop.DormantUntil = Math.Max(now + 0.05d, nextPortalReleaseAt);
                return;
            }

            if (!PortalSpawnClear(graph, portal, pop, players))
            {
                pop.PortalRespawnAttempts++;
                pop.DormantUntil = now + Math.Max(0.25f, portal.blockedRetryDelay);
                return;
            }

            ActivatePortalSpawn(pop, portal, now, scheduleImmediately: false);
        }

        private void ActivatePortalSpawn(PopulationActorRuntime pop, ServerPopulationPortal portal, double now, bool scheduleImmediately)
        {
            if (pop == null || pop.Actor == null || portal == null)
                return;

            _portalRespawnsThisTick++;
            RecordPortalRelease(portal, pop, now);
            pop.PortalRespawnAttempts = 0;
            pop.PortalPhase = PopulationPortalSequencePhase.Interior;
            pop.AiState = PopulationAiState.FollowingRoute;
            pop.RouteReason = PopulationRouteReason.PortalTravel;
            pop.Actor.Position = portal.interiorSpawn.ToWorldPosition();
            pop.Actor.LastSafePosition = pop.Actor.Position;
            pop.MotorState = new CharacterMotorState(pop.Actor.Position);
            pop.CurrentNodeId = portal.routeNodeId;
            pop.NextNodeId = 0;
            pop.AwaitingPlayerActivation = false;
            RemoveFromHibernationIndex(pop);
            _actors.ResumeSpatial(pop.Actor);
            _actors.PublishChanged(pop.Actor);
            Changed?.Invoke(pop);
            if (scheduleImmediately)
                ScheduleNow(pop, now);
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
            WorldPosition exterior = portal.exterior.ToWorldPosition();
            float radius = Math.Max(0.2f, portal.exitClearanceRadius);
            if (graph.Collision != null)
            {
                var capsule = new ServerCapsule(radius, 1.8f);
                if (!graph.Collision.IsCapsuleClear(exterior, capsule)) return false;
                if (!graph.Collision.TryFindGround(exterior, radius, 1f, 2f, 55f, out _)) return false;
            }

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
            if (!TryGetGraph(pop.Actor, out MapGraph graph) || !graph.Portals.TryGetValue(pop.LastPortalId, out ServerPopulationPortal portal))
            {
                pop.PortalPhase = PopulationPortalSequencePhase.None;
                return;
            }

            ServerPose targetPose;
            switch (pop.PortalPhase)
            {
                case PopulationPortalSequencePhase.Interior: targetPose = portal.approach; break;
                case PopulationPortalSequencePhase.Approach: targetPose = portal.interaction; break;
                case PopulationPortalSequencePhase.Door:
                    if (portal.doorWorldObjectId > 0)
                        _worldObjects?.TrySetOpenState(pop.Actor.MapId, pop.Actor.InstanceId, portal.doorWorldObjectId, true);
                    pop.PortalPhase = PopulationPortalSequencePhase.Threshold;
                    return;
                case PopulationPortalSequencePhase.Threshold: targetPose = portal.threshold; break;
                case PopulationPortalSequencePhase.Exterior: targetPose = portal.exterior; break;
                default: pop.PortalPhase = PopulationPortalSequencePhase.None; return;
            }

            if (MoveTowardPose(graph, pop, targetPose, dt))
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

        private bool MoveTowardPose(MapGraph graph, PopulationActorRuntime pop, ServerPose targetPose, float dt)
        {
            WorldPosition target = targetPose.ToWorldPosition();
            float dx = target.X - pop.Actor.Position.X;
            float dz = target.Z - pop.Actor.Position.Z;
            float distSq = dx * dx + dz * dz;
            if (distSq <= 0.18f * 0.18f)
            {
                pop.Actor.YawDegrees = targetPose.yaw;
                return true;
            }
            float inv = 1f / MathF.Sqrt(Math.Max(0.0001f, distSq));
            if (graph.Collision != null)
            {
                var intent = new CharacterMovementIntent(dx * inv, dz * inv, false, false);
                pop.Actor.YawDegrees = NormalizeYaw(targetPose.yaw);
                pop.Motor.Tick(pop.MotorState, intent, dt, graph.Collision);
                ApplyMotor(pop);
            }
            else
            {
                float step = Math.Min(MathF.Sqrt(distSq), pop.WalkSpeed * dt);
                pop.Actor.Position = new WorldPosition(pop.Actor.Position.X + dx * inv * step, pop.Actor.Position.Y, pop.Actor.Position.Z + dz * inv * step);
            }
            return false;
        }

        private bool EnsureNextNode(MapGraph graph, PopulationActorRuntime pop)
        {
            if (pop.NextNodeId > 0 && graph.Nodes.ContainsKey(pop.NextNodeId)) return true;
            if (pop.CurrentNodeId <= 0 || !graph.Nodes.ContainsKey(pop.CurrentNodeId))
                pop.CurrentNodeId = FindNearestNode(graph, pop.Actor.Position);
            ServerPopulationRouteEdge edge = ChooseEdge(graph, pop.CurrentNodeId, pop.PreviousNodeId, pop.NpcType, pop.Random);
            if (edge == null) return false;
            pop.NextNodeId = edge.toNodeId;
            pop.SegmentProgress = 0f;
            pop.LaneOffset = ChooseLaneOffset(edge.width, pop.Random);
            return true;
        }

        private static ServerPopulationRouteEdge ChooseEdge(
            MapGraph graph,
            long nodeId,
            long previousNodeId,
            PopulationNpcType npcType,
            Random random)
        {
            if (nodeId <= 0 || !graph.Outgoing.TryGetValue(nodeId, out List<ServerPopulationRouteEdge> edges) || edges.Count == 0)
                return null;

            // First determine whether a non-U-turn option exists. Immediate reversal is then
            // suppressed at intersections but remains legal at dead ends.
            bool hasForwardOption = false;
            for (int i = 0; i < edges.Count; ++i)
            {
                ServerPopulationRouteEdge edge = edges[i];
                if (edge == null || !EdgeAllows(graph, edge, npcType))
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
                if (edge == null || !EdgeAllows(graph, edge, npcType))
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
                if (edge == null || !EdgeAllows(graph, edge, npcType))
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

        private bool ShouldHibernateForPlayerDistance(PopulationActorRuntime pop)
        {
            if (pop == null || pop.Actor == null || pop.AwaitingPlayerActivation ||
                pop.AiState == PopulationAiState.PortalDormant ||
                pop.PortalPhase != PopulationPortalSequencePhase.None ||
                pop.ThreatUntil > pop.LastSimulationAt)
            {
                return false;
            }

            float hibernate = Math.Max(PlayerActivationDistance + 1f, PlayerHibernateDistance);
            return GetNearestPlayerDistanceSquared(pop.Actor) > hibernate * hibernate;
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
            if (_hibernatingCount <= 0 || _playersByPartition.Count == 0)
                return;

            _activationScratch.Clear();
            float activation = Math.Max(1f, PlayerActivationDistance);
            int cellRadius = Math.Max(1, (int)Math.Ceiling(activation / HibernationCellSize));
            float activationSq = activation * activation;

            foreach (KeyValuePair<string, PlayerSpatialPartition> partitionPair in _playersByPartition)
            {
                PlayerSpatialPartition partition = partitionPair.Value;
                if (partition == null || partition.Players.Count == 0 ||
                    !_hibernatedByPartition.TryGetValue(partitionPair.Key, out Dictionary<long, List<PopulationActorRuntime>> cells))
                {
                    continue;
                }

                for (int p = 0; p < partition.Players.Count; ++p)
                {
                    PopulationPlayerView player = partition.Players[p];
                    int centerX = ToHibernationCell(player.Position.X);
                    int centerZ = ToHibernationCell(player.Position.Z);
                    for (int dz = -cellRadius; dz <= cellRadius; ++dz)
                    {
                        for (int dx = -cellRadius; dx <= cellRadius; ++dx)
                        {
                            if (!cells.TryGetValue(PackPlayerCell(centerX + dx, centerZ + dz), out List<PopulationActorRuntime> cell))
                                continue;
                            for (int i = 0; i < cell.Count; ++i)
                            {
                                PopulationActorRuntime pop = cell[i];
                                if (pop == null || !pop.AwaitingPlayerActivation || pop.Actor == null)
                                    continue;
                                float px = pop.HibernationPosition.X - player.Position.X;
                                float pz = pop.HibernationPosition.Z - player.Position.Z;
                                if (px * px + pz * pz <= activationSq)
                                    _activationScratch.Add(pop);
                            }
                        }
                    }
                }
            }

            if (_activationScratch.Count == 0)
                return;

            PopulationActorRuntime[] wake = new PopulationActorRuntime[_activationScratch.Count];
            _activationScratch.CopyTo(wake);
            for (int i = 0; i < wake.Length; ++i)
            {
                PopulationActorRuntime pop = wake[i];
                if (pop != null && pop.AiState == PopulationAiState.PortalDormant)
                {
                    WakePortalDormantImmediately(pop, now);
                    continue;
                }

                WakeImmediately(pop, now, catchUp: true);
            }
            _activationScratch.Clear();
        }

        /// <summary>
        /// Portal-backed Population is admitted directly from the shared AOI activation pass.
        /// This avoids funneling a whole dormant portal queue through one-per-actor scheduler
        /// wakeups. Admission is still bounded by MaximumPortalRespawnsPerTick and the authored
        /// portal density/clearance rules, so a large authored population remains cheap.
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

            if (portal.mode == PopulationPortalMode.DespawnOnly)
                return;

            IReadOnlyList<PopulationPlayerView> players = GetPartitionPlayers(pop.Actor);
            if (!HasPlayerWithin(pop.Actor, portal.exterior.ToWorldPosition(), players, PlayerActivationDistance))
                return;

            if (!PortalCadenceAllows(portal, now, out _))
                return;

            if (!PortalSpawnClear(graph, portal, pop, players))
            {
                pop.PortalRespawnAttempts++;
                pop.DormantUntil = now + Math.Max(0.25f, portal.blockedRetryDelay);
                return;
            }

            ActivatePortalSpawn(pop, portal, now, scheduleImmediately: true);
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

            _actors.ResumeSpatial(pop.Actor);
            _actors.PublishChanged(pop.Actor);
            Changed?.Invoke(pop);
            ScheduleNow(pop, now);
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
            int hops = 0;
            int maxHops = Math.Max(8, MaximumLogicalCatchUpHops);
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
