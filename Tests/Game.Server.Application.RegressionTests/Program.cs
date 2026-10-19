using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Actors;
using Game.Server.Application.Population;
using Game.Server.Application.World;
using Game.Server.Application.Content;
using Game.Server.Application.Items;
using Game.Server.Application.Persistence;
using Game.Server.Application.WorldItems;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Stats;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.Backend;
using Game.Shared.Content;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.World;

internal static class Program
{
    private enum CommitBehavior
    {
        CommitThenThrow,
        CommitThenTimeout,
        StaleThenReload,
    }

    private sealed class FakeRepository : IPlayerSystemsRepository
    {
        private readonly CommitBehavior _behavior;
        private PlayerSystemsPersistenceRecord _state;

        public int ReconciliationLoads { get; private set; }

        public FakeRepository(PlayerSystemsPersistenceRecord initial, CommitBehavior behavior)
        {
            _state = initial ?? throw new ArgumentNullException(nameof(initial));
            _behavior = behavior;
        }

        public Task<PlayerSystemsPersistenceRecord> LoadAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken) =>
            Task.FromResult(_state);

        public Task<PlayerSystemsPersistenceRecord> LoadForReconciliationAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken)
        {
            ReconciliationLoads++;
            return Task.FromResult(_state);
        }

        public Task<PlayerSystemsCommitResult> TryCommitAsync(PlayerSystemsCommitRequest request, CancellationToken cancellationToken)
        {
            if (_behavior == CommitBehavior.StaleThenReload)
            {
                // Simulate a durable mutation that beat this request to revision 1. The
                // stale response must still cause the live runtime to install this state.
                ItemInstanceState[] authoritativeSlots = request.Inventory.CopySlots();
                ItemInstanceState moved = authoritativeSlots[1];
                authoritativeSlots[1] = null;
                authoritativeSlots[0] = moved;
                var authoritativeInventory = new InventoryState(
                    request.Inventory.Capacity,
                    request.Inventory.Revision,
                    authoritativeSlots);
                _state = ToRecord(authoritativeInventory, request.Equipment);
                return Task.FromResult(new PlayerSystemsCommitResult(
                    false,
                    true,
                    _state.InventoryRevision,
                    _state.EquipmentRevision,
                    "stale player-system revision"));
            }

            // The durable mutation commits first; only its response is lost.
            _state = ToRecord(request.Inventory, request.Equipment);
            if (_behavior == CommitBehavior.CommitThenTimeout)
                throw new OperationCanceledException("simulated HttpClient timeout");
            throw new InvalidOperationException("simulated lost response after commit");
        }
    }

    private static async Task Main(string[] args)
    {
        if (Array.IndexOf(args, "--movement-safe-fast-path") >= 0)
        {
            RunSync("certified flat movement remains within authored support", CertifiedFlatMove);
            RunSync("movement leaving a certified region returns to static collision", CertifiedMoveKeepsStaticCollision);
            RunSync("dynamic blockers still force authoritative collision", CertifiedMoveKeepsDynamicBlockers);
            return;
        }
        if (Array.IndexOf(args, "--population-route-v4") >= 0)
        {
            RunSync("validated slow travel never invokes stuck recovery", RouteSlowTravel);
            RunSync("segment references persist until an actual transition", RouteAssignmentReuse);
            RunSync("displaced actor rejoins once without repeating nearest searches", RouteDisplacementRejoin);
            RunSync("collision recovery path still detects a stationary blocked actor", RoutePhysicalRecovery);
            return;
        }
        if (Array.IndexOf(args, "--population-route-v4-workload") >= 0)
        {
            for (int sample = 0; sample < 6; ++sample)
            {
                Console.WriteLine("SAMPLE " + sample + (sample == 0 ? " (warmup)" : ""));
                AuditWorkload(0f, 60000);
                AuditWorkload(56f, 60000);
            }
            return;
        }
        if (Array.IndexOf(args, "--population-audit-v3-workload") >= 0)
        {
            AuditWorkload(0f);
            AuditWorkload(56f);
            return;
        }
        if (Array.IndexOf(args, "--population-audit-v3") >= 0)
        {
            RunSync("civilian LOD follows observer exit and preserves combat families", AuditCivilianLod);
            RunSync("wake/hibernate hysteresis and threat wake remain authoritative", AuditHysteresisAndThreat);
            RunSync("small player partition refresh keeps exact moving-player positions", AuditSmallPlayerPartitionRefresh);
            RunSync("actor partition key follows map and instance changes", AuditActorPartitionKeyInvalidation);
            RunSync("no observers leaves Population unscheduled", AuditNoObservers);
            Console.WriteLine("All Population audit V3 regression tests passed.");
            return;
        }
        await Run("lost response after commit reconciles as success", CommitThenThrowReconcilesAsSuccess);
        await Run("HttpClient-style timeout after commit reconciles as success", TimeoutAfterCommitReconcilesAsSuccess);
        await Run("stale response reloads authoritative state", StaleResponseReloadsAuthoritativeState);
        RunSync("transient world item ids are isolated from durable ids", TransientWorldIdsUseReservedRange);
        RunSync("Population hibernates out of AOI and catches route progress up on wake", PopulationHibernationCatchesUpOnWake);
        RunSync("Population portal spawn remains dormant until a player activates it", PopulationPortalActivationUsesExistingPortal);
        RunSync("Population portal can release multiple active Pops from one authored spawn", PopulationPortalAllowsMultipleActive);
        RunSync("Population portal staggers release cadence without per-Pop timers", PopulationPortalStaggersReleaseCadence);
        RunSync("Population portal cycle uses primary baked endpoints and resumes at destination", PopulationPortalCycleUsesPrimaryNodes);
        RunSync("Monster free roam uses shared actor runtime without route nodes", MonsterFreeRoamUsesSharedRuntime);
        RunSync("Monster free roam idles beyond configured free-roam distance", MonsterFreeRoamIdlesAtFarRange);
        RunSync("NPC route movement uses shared actor runtime", NpcRouteMovementUsesSharedRuntime);
        RunSync("NPC without route graph resolves Stationary", NpcWithoutRouteGraphIsStationary);
        RunSync("NPC without explicit route uses nearest-route fallback", NpcWithoutExplicitRouteUsesNearestRouteFallback);
        RunSync("NPC invalid explicit route normalizes to nearest valid route", NpcInvalidExplicitRouteNormalizesToNearestRoute);
        RunSync("Monster invalid explicit route resolves FreeRoam", MonsterInvalidExplicitRouteResolvesFreeRoam);
        Console.WriteLine("All Game.Server.Application regression tests passed.");
    }

    private static async Task CommitThenThrowReconcilesAsSuccess()
    {
        (PlayerItemService service, PlayerRuntime runtime, FakeRepository repository) =
            CreateHarness(CommitBehavior.CommitThenThrow);

        PlayerItemOperationResult result = await service.MoveInventoryAsync(runtime, 0, 1, CancellationToken.None);
        Require(result.Success, "move should be reported successful after authoritative reconciliation");
        Require(repository.ReconciliationLoads == 1, "ambiguous outcome should perform one reconciliation load");

        PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
        Require(state.Inventory.Revision == 1, "reconciled inventory revision should be 1");
        Require(state.Inventory.Get(0) == null, "source slot should be empty after reconciled commit");
        Require(state.Inventory.Get(1)?.ItemInstanceId.Value == 10, "target slot should contain the committed item");
    }

    private static async Task TimeoutAfterCommitReconcilesAsSuccess()
    {
        (PlayerItemService service, PlayerRuntime runtime, FakeRepository repository) =
            CreateHarness(CommitBehavior.CommitThenTimeout);

        PlayerItemOperationResult result = await service.MoveInventoryAsync(runtime, 0, 1, CancellationToken.None);
        Require(result.Success, "an internal timeout must be treated as an ambiguous outcome, not caller cancellation");
        Require(repository.ReconciliationLoads == 1, "timeout should perform one reconciliation load");
        Require(runtime.CapturePlayerItemSystems().Inventory.Get(1)?.ItemInstanceId.Value == 10,
            "authoritative post-timeout state should be installed");
    }

    private static async Task StaleResponseReloadsAuthoritativeState()
    {
        (PlayerItemService service, PlayerRuntime runtime, FakeRepository repository) =
            CreateHarness(CommitBehavior.StaleThenReload);

        PlayerItemOperationResult result = await service.MoveInventoryAsync(runtime, 0, 1, CancellationToken.None);
        Require(!result.Success, "stale request should not be reclassified as successful");
        Require(result.Status == PlayerItemOperationStatus.StaleState, "stale request should preserve stale status");
        Require(repository.ReconciliationLoads == 1, "stale response should perform one reconciliation load");

        PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
        Require(state.Inventory.Revision == 1, "authoritative stale revision should be installed");
        Require(state.Inventory.Get(0)?.ItemInstanceId.Value == 10, "authoritative slot layout should replace stale live state");
        Require(state.Inventory.Get(1) == null, "rejected local move must not survive reconciliation");
    }

    private static void TransientWorldIdsUseReservedRange()
    {
        var seen = new HashSet<long>();
        for (int i = 0; i < 1024; ++i)
        {
            long value = TransientWorldItemIds.Allocate().Value;
            Require(value >= BackendServiceContracts.TransientWorldItemIdFloor,
                "transient id must be inside the reserved high range");
            Require(seen.Add(value), "transient ids must be unique");
        }
    }

    private static (PopulationSimulationService Service, PopulationActorRuntime Actor) AuditPopulation(
        string archetype = "civilian.test", ServerSpawnKind kind = ServerSpawnKind.Population)
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        map.movementTriangles = map.collisionTriangles;
        map.spawnAnchors[0].archetypeId = archetype;
        map.spawnAnchors[0].kind = kind;
        var service = new PopulationSimulationService(new ServerMapCatalog(new[] { map }),
            new AuthoritativeActorRegistry(), null)
        {
            CivilianActiveDistance = 56f,
            ActiveDistance = 80f,
            PlayerActivationDistance = 80f,
            PlayerHibernateDistance = 104f,
        };
        return (service, SinglePopulation(service));
    }

    private static void AuditStep(PopulationSimulationService service,
        PopulationActorRuntime actor, float distance, double now, string instance = "")
    {
        WorldPosition position = actor.Actor.Position;
        service.PrepareBudgetedTick(new[] { new PopulationPlayerView(1,
            "population_test", instance,
            new WorldPosition(position.X, position.Y, position.Z + distance)) }, now);
        // Consume the existing preparation handoff, bounded wake and due actor work.
        // A freshly scheduled actor is not due again at this same simulation time.
        for (int i = 0; i < 16; ++i)
            if (!service.TickNextBudgeted(0.1f, now)) break;
    }

    private static void AuditCivilianLod()
    {
        foreach (float distance in new[] { 48f, 55f, 56f, 57f, 70f, 79f })
        {
            var (service, actor) = AuditPopulation();
            AuditStep(service, actor, distance, 1d);
            Require(!actor.IsHibernating, "civilian within activation range should wake");
            Require(actor.SimulationLod == (distance <= 56f
                ? PopulationSimulationLod.Active : PopulationSimulationLod.CoarseRoute),
                "civilian full cadence must end at AOI exit");
        }
        foreach (string archetype in new[] { "police", "hunter", "gang", "criminal", "ghoul" })
        {
            var (service, actor) = AuditPopulation(archetype);
            AuditStep(service, actor, 70f, 1d);
            Require(actor.SimulationLod == PopulationSimulationLod.Active,
                archetype + " must retain the original Active range");
        }
        foreach (ServerSpawnKind kind in new[] { ServerSpawnKind.Npc, ServerSpawnKind.Monster })
        {
            var (service, actor) = AuditPopulation(kind: kind);
            AuditStep(service, actor, 70f, 1d);
            Require(actor.SimulationLod == PopulationSimulationLod.Active,
                "authored NPC/Monster must retain the original Active range");
        }
    }

    private static void AuditHysteresisAndThreat()
    {
        var (service, actor) = AuditPopulation();
        AuditStep(service, actor, 70f, 1d);
        foreach (float distance in new[] { 81f, 90f, 103f })
        {
            AuditStep(service, actor, distance, distance);
            Require(!actor.IsHibernating, "awake civilian must stay awake inside hibernate band");
        }
        AuditStep(service, actor, 105f, 110d);
        Require(actor.IsHibernating, "civilian beyond 104m must hibernate");
        AuditStep(service, actor, 81f, 112d);
        Require(actor.IsHibernating, "hibernating civilian must not wake outside 80m");
        AuditStep(service, actor, 79f, 114d);
        Require(!actor.IsHibernating, "approaching player must wake inside 80m");
        AuditStep(service, actor, 0f, 116d, "another_instance");
        Require(actor.IsHibernating, "other-instance player must not keep civilian awake");
        Require(service.NotifyThreat(actor.Actor.Handle.actorId, actor.Actor.Position, 1f, 117d),
            "authoritative threat must be accepted");
        Require(!actor.IsHibernating && actor.SimulationLod == PopulationSimulationLod.Engaged &&
            service.HasDueScheduledActorWork(117d), "threat must immediately wake and schedule engaged work");
    }

    private static void AuditSmallPlayerPartitionRefresh()
    {
        var (service, actor) = AuditPopulation();
        AuditStep(service, actor, 0f, 1d);
        Require(actor.SimulationLod == PopulationSimulationLod.Active,
            "player at actor must put civilian into Active LOD");
        AuditStep(service, actor, 70f, 2d);
        Require(actor.SimulationLod == PopulationSimulationLod.CoarseRoute,
            "small-partition refresh must use exact current player position");
    }

    private static void AuditActorPartitionKeyInvalidation()
    {
        var (service, actor) = AuditPopulation();
        AuditStep(service, actor, 0f, 1d);
        actor.Actor.InstanceId = "population_test_instance_2";
        AuditStep(service, actor, 0f, 2d, "population_test_instance_2");
        Require(actor.SimulationLod == PopulationSimulationLod.Active,
            "partition key must refresh after actor instance change");
    }

    private static void AuditWorkload(float civilianActiveDistance, int ticks = 600)
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        map.movementTriangles = map.collisionTriangles;
        map.populationNodes[1].pose = new ServerPose(20f, 0f, 0f);
        map.spawnAnchors = new ServerSpawnAnchor[52];
        for (int i = 0; i < map.spawnAnchors.Length; ++i)
            map.spawnAnchors[i] = new ServerSpawnAnchor
            {
                stableId = 7001 + i,
                kind = ServerSpawnKind.Population,
                actorKind = AuthoritativeActorKind.Population,
                archetypeId = "civilian.test",
                pose = new ServerPose(0f, 0f, 0f),
                enabled = true,
                capsuleRadius = 0.35f,
                capsuleHeight = 1.8f,
                routeNodeId = 1001,
            };
        var service = new PopulationSimulationService(new ServerMapCatalog(new[] { map }),
            new AuthoritativeActorRegistry(), null)
        {
            CivilianActiveDistance = civilianActiveDistance,
            PlayerActivationDistance = 80f,
            PlayerHibernateDistance = 104f,
            EnableSlowWorkDiagnostics = false,
        };
        int changed = 0, units = 0;
        service.Changed += _ => ++changed;
        var players = new[] { new PopulationPlayerView(1, "population_test", "",
            new WorldPosition(10f, 0f, 70f)) };
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int tick = 1; tick <= ticks; ++tick)
        {
            double now = tick * 0.1d;
            service.PrepareBudgetedTick(players, now);
            for (int work = 0; work < 128; ++work)
            {
                if (!service.TickNextBudgeted(0.1f, now)) break;
                ++units;
            }
        }
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int active = 0, coarse = 0;
        foreach (PopulationActorRuntime actor in service.All)
        {
            if (actor.SimulationLod == PopulationSimulationLod.Active) ++active;
            if (actor.SimulationLod == PopulationSimulationLod.CoarseRoute) ++coarse;
        }
        Console.WriteLine($"SYNTHETIC {ticks * 0.1d:F0}s: civilianActiveDistance={civilianActiveDistance}, " +
            $"actors={service.Count}, active={active}, coarse={coarse}, " +
            $"successfulWorkUnits={units}, changedCallbacks={changed}, " +
            $"elapsedMs={watch.Elapsed.TotalMilliseconds:F3}, allocatedBytes={allocated}");
        Require(service.Count == 52 && !service.HasDueScheduledActorWork(ticks * 0.1d),
            "workload must retain all actors and drain due work");
    }

    private static object RouteField(object target, string name) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance).GetValue(target);

    private static void SetRouteField(object target, string name, object value) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance).SetValue(target, value);

    private static (PopulationSimulationService Service, PopulationActorRuntime Actor) RouteFixture()
    {
        var fixture = AuditPopulation();
        fixture.Service.EnableSlowWorkDiagnostics = false;
        AuditStep(fixture.Service, fixture.Actor, 0f, 1d);
        fixture.Actor.Actor.Position = new WorldPosition(100f, 0f, 0f);
        SetRouteField(fixture.Actor, "MotorState",
            new Game.Server.Application.Movement.CharacterMotorState(fixture.Actor.Actor.Position));
        SetRouteField(fixture.Actor, "CurrentNodeId", 1001L);
        SetRouteField(fixture.Actor, "NextNodeId", 1002L);
        SetRouteField(fixture.Actor, "SegmentProgress", 0.1f);
        // Force the existing centerline fallback to exercise its route contract.
        SetRouteField(fixture.Actor, "LaneOffset", 0.17f);
        return fixture;
    }

    private static void RouteSlowTravel()
    {
        var (service, actor) = RouteFixture();
        typeof(PopulationActorRuntime).GetProperty("WalkSpeed").SetValue(actor, 0.1f);
        float previous = actor.Actor.Position.X;
        for (int tick = 0; tick < 20; ++tick)
        {
            SetRouteField(actor, "LaneOffset", 0.17f);
            AuditStep(service, actor, 0f, 2d + tick * 0.3d);
            Require(actor.Actor.Position.X >= previous && actor.Actor.Position.X - previous < 0.12f,
                "slow route movement must remain bounded and forward");
            Require(actor.CurrentRouteNodeId == 1001 && actor.NextRouteNodeId == 1002 &&
                actor.RouteReason != PopulationRouteReason.Recovery,
                "slow but valid progress must not trigger route recovery");
            previous = actor.Actor.Position.X;
        }
    }

    private static void RouteAssignmentReuse()
    {
        var (service, actor) = RouteFixture();
        AuditStep(service, actor, 0f, 2d);
        object assignment = RouteField(actor, "AssignedRouteSegment");
        Require(assignment != null, "existing edge must resolve to a compiled segment");
        AuditStep(service, actor, 0f, 2.3d);
        Require(ReferenceEquals(assignment, RouteField(actor, "AssignedRouteSegment")),
            "unchanged assignment must reuse its compiled segment");
        SetRouteField(actor, "CurrentNodeId", 1002L);
        SetRouteField(actor, "NextNodeId", 1001L);
        SetRouteField(actor, "SegmentProgress", 0.9f);
        AuditStep(service, actor, 0f, 2.6d);
        Require(!ReferenceEquals(assignment, RouteField(actor, "AssignedRouteSegment")),
            "reversed edge must invalidate the previous reference");
        Require(actor.Actor.Position.X < 101f, "reversed travel must preserve location continuity");
    }

    private static void RouteDisplacementRejoin()
    {
        var (service, actor) = RouteFixture();
        actor.Actor.Position = new WorldPosition(100f, 0f, 20f);
        SetRouteField(actor, "MotorState",
            new Game.Server.Application.Movement.CharacterMotorState(actor.Actor.Position));
        AuditStep(service, actor, 0f, 2d);
        Require(actor.CurrentRouteNodeId == actor.NextRouteNodeId,
            "actual corridor departure must assign a nearest-marker rejoin");
        long join = actor.NextRouteNodeId;
        WorldPosition before = actor.Actor.Position;
        AuditStep(service, actor, 0f, 2.3d);
        Require(actor.NextRouteNodeId == join && actor.Actor.Position.Z < before.Z,
            "rejoin must make bounded progress without selecting a new nearest marker");
    }

    private static void RoutePhysicalRecovery()
    {
        var (service, actor) = RouteFixture();
        AuditStep(service, actor, 0f, 2d);
        SetRouteField(actor, "LastProgressAt", 1d);
        SetRouteField(actor, "LastProgressPosition", actor.Actor.Position);
        object graph = RouteField(actor, "AssignedRouteGraph");
        typeof(PopulationSimulationService).GetMethod("DetectStuck",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Invoke(service, new[] { graph, actor, (object)5d });
        Require(actor.RouteReason == PopulationRouteReason.Recovery &&
            actor.CurrentRouteNodeId == actor.NextRouteNodeId,
            "blocked physical movement must retain route recovery");
    }

    private static void AuditNoObservers()
    {
        var (service, actor) = AuditPopulation();
        service.PrepareBudgetedTick(Array.Empty<PopulationPlayerView>(), 1d);
        Require(actor.IsHibernating && !service.HasDueScheduledActorWork(1d),
            "startup civilian must have no due actor work without observers");
        AuditStep(service, actor, 0f, 2d);
        service.PrepareBudgetedTick(Array.Empty<PopulationPlayerView>(), 4d);
        for (int i = 0; i < 16; ++i)
            if (!service.TickNextBudgeted(0.1f, 4d)) break;
        Require(actor.IsHibernating && !service.HasDueScheduledActorWork(100d),
            "disconnect must return civilian to unscheduled hibernation");
    }

    private static void PopulationHibernationCatchesUpOnWake()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null)
        {
            PlayerActivationDistance = 96f,
            PlayerHibernateDistance = 128f,
        };

        PopulationActorRuntime pop = SinglePopulation(population);
        Require(pop.IsHibernating, "baked Population should start hibernated when no players are present");
        Require(population.HibernatingCount == 1, "hibernation index should contain the dormant Pop");

        WorldPosition before = pop.Actor.Position;
        var spatial = new List<AuthoritativeActorRuntime>();
        actors.QueryRadius("population_test", string.Empty, before, 8f, spatial);
        Require(spatial.Count == 0, "hibernating Population must be absent from authoritative spatial queries");

        var nearbyPlayer = new[]
        {
            new PopulationPlayerView(1, "population_test", string.Empty, before),
        };
        population.PrepareBudgetedTick(nearbyPlayer, 20d);

        Require(!pop.IsHibernating, "nearby player should reactivate the Pop");
        Require(population.HibernatingCount == 0, "reactivated Pop must leave the hibernation index");
        float dx = pop.Actor.Position.X - before.X;
        float dz = pop.Actor.Position.Z - before.Z;
        Require(dx * dx + dz * dz > 0.25f,
            "reactivated Pop should catch up route travel instead of returning at the old frozen point");

        actors.QueryRadius("population_test", string.Empty, pop.Actor.Position, 8f, spatial);
        Require(spatial.Exists(x => x.Handle.actorId == pop.Actor.Handle.actorId),
            "reactivated Population must re-enter authoritative spatial queries");
    }

    private static void PopulationPortalActivationUsesExistingPortal()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: true);
        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null)
        {
            PlayerActivationDistance = 96f,
            PlayerHibernateDistance = 128f,
        };

        PopulationActorRuntime pop = SinglePopulation(population);
        Require(pop.IsHibernating, "portal-backed Population should be parked until a player is nearby");
        Require(pop.AiState == PopulationAiState.PortalDormant, "portal-backed Population should begin off-world");
        Require(pop.CurrentPortalId == 5001, "spawn must retain its authored portal identity");

        var nearbyPlayer = new[]
        {
            new PopulationPlayerView(2, "population_test", string.Empty, new WorldPosition(0f, 0f, 0f)),
        };
        population.PrepareBudgetedTick(nearbyPlayer, 1d);
        Require(population.HasDueWork(1d), "player activation should schedule the portal spawn immediately");
        Require(population.TickNextBudgeted(0.1f, 1d), "portal activation should execute one Population work unit");

        Require(!pop.IsHibernating, "portal-spawned Population should become spatially active");
        Require(pop.AiState == PopulationAiState.FollowingRoute, "portal-spawned Population should enter its route state");
        Require(pop.PortalSequencePhase == PopulationPortalSequencePhase.None,
            "portal-spawned Population should begin normal route movement from its single baked exterior anchor");
        Require(!actors.IsSpatiallySuspended(pop.Actor), "portal-spawned Population should resume authoritative spatial presence");
    }


    private static void PopulationPortalAllowsMultipleActive()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: true);
        // Ambient civilian count is set by this spawner's authored slots/capacity;
        // nearby-density throttles must not reject its own baked actors.
        map.populationPortals[0].maximumActiveNearby = 1;
        map.populationPortals[0].maximumActiveInWorld = 17;
        map.populationPortals[0].minimumSpawnInterval = 0f;
        map.populationPortals[0].maximumSpawnInterval = 0f;
        map.populationPortals[0].spawnBurstLimit = 17;

        ServerSpawnAnchor template = map.spawnAnchors[0];
        var anchors = new ServerSpawnAnchor[17];
        for (int i = 0; i < anchors.Length; ++i)
        {
            anchors[i] = new ServerSpawnAnchor
            {
                stableId = template.stableId + i,
                label = $"Population Test Spawn #{i + 1}",
                kind = template.kind,
                actorKind = template.actorKind,
                archetypeId = template.archetypeId,
                deathLootTableId = template.deathLootTableId,
                pose = template.pose,
                priority = template.priority,
                enabled = template.enabled,
                capsuleRadius = template.capsuleRadius,
                capsuleHeight = template.capsuleHeight,
                maximumGroundSnap = template.maximumGroundSnap,
                tags = template.tags,
                routeNodeId = template.routeNodeId,
                portalId = template.portalId,
            };
        }
        map.spawnAnchors = anchors;

        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null)
        {
            PlayerActivationDistance = 96f,
            PlayerHibernateDistance = 128f,
            MaximumPortalRespawnsPerTick = 8,
        };

        Require(population.Count == 17, "test map should create all 17 authored Population identities");
        var nearbyPlayer = new[]
        {
            new PopulationPlayerView(3, "population_test", string.Empty, new WorldPosition(0f, 0f, 0f)),
        };
        // The production core scheduler only calls PrepareBudgetedTick when HasDueWork is true.
        // Portal-backed identities are hibernated and therefore have no per-actor due item until
        // player activation is probed. The shared activation heartbeat must make the system
        // schedulable without ticking each dormant Pop.
        Require(population.HasDueWork(1d),
            "hibernated Population must expose shared activation-heartbeat work to the scheduler");
        population.PrepareBudgetedTick(nearbyPlayer, 1d);

        // Portal admission is handled directly by the shared AOI activation pass. The first
        // activation probe may release up to MaximumPortalRespawnsPerTick without waiting for
        // one Population actor to finish its route or consume a separate scheduler cycle.
        int activeAfterActivationPass = 0;
        foreach (PopulationActorRuntime pop in population.All)
        {
            if (!pop.IsHibernating && pop.AiState == PopulationAiState.FollowingRoute)
                activeAfterActivationPass++;
        }
        Require(activeAfterActivationPass > 1,
            "one portal with capacity >1 must release multiple Population identities in the AOI activation pass");
        Require(activeAfterActivationPass <= population.MaximumPortalRespawnsPerTick,
            "portal activation pass must remain bounded by MaximumPortalRespawnsPerTick");

        int processed = 0;
        while (processed < 17 && population.HasDueWork(1d))
        {
            Require(population.TickNextBudgeted(0.1f, 1d), "due portal Population work should be executable");
            processed++;
        }

        int active = 0;
        foreach (PopulationActorRuntime pop in population.All)
        {
            if (!pop.IsHibernating && pop.AiState == PopulationAiState.FollowingRoute)
                active++;
        }
        Require(active > 1, "one portal with capacity >1 must not serialize Population to a single live actor");
    }

    private static void PopulationPortalStaggersReleaseCadence()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: true);
        ServerPopulationPortal portal = map.populationPortals[0];
        portal.maximumActiveNearby = 17;
        portal.minimumSpawnInterval = 1f;
        portal.maximumSpawnInterval = 1f;
        portal.spawnBurstLimit = 1;

        ServerSpawnAnchor template = map.spawnAnchors[0];
        var anchors = new ServerSpawnAnchor[4];
        for (int i = 0; i < anchors.Length; ++i)
        {
            anchors[i] = new ServerSpawnAnchor
            {
                stableId = template.stableId + i,
                label = $"Staggered Population Test Spawn #{i + 1}",
                kind = template.kind,
                actorKind = template.actorKind,
                archetypeId = template.archetypeId,
                deathLootTableId = template.deathLootTableId,
                pose = template.pose,
                priority = template.priority,
                enabled = template.enabled,
                capsuleRadius = template.capsuleRadius,
                capsuleHeight = template.capsuleHeight,
                maximumGroundSnap = template.maximumGroundSnap,
                tags = template.tags,
                routeNodeId = template.routeNodeId,
                portalId = template.portalId,
            };
        }
        map.spawnAnchors = anchors;

        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null)
        {
            PlayerActivationDistance = 96f,
            PlayerHibernateDistance = 128f,
            MaximumPortalRespawnsPerTick = 8,
        };
        var nearbyPlayer = new[]
        {
            new PopulationPlayerView(4, "population_test", string.Empty, new WorldPosition(0f, 0f, 0f)),
        };

        population.PrepareBudgetedTick(nearbyPlayer, 1d);
        Require(CountActivePopulation(population) == 1,
            "burst limit 1 should release exactly one Pop in the first activation pass");

        population.PrepareBudgetedTick(nearbyPlayer, 1.5d);
        Require(CountActivePopulation(population) == 1,
            "portal should not release another Pop before the shared portal interval expires");

        population.PrepareBudgetedTick(nearbyPlayer, 2.01d);
        Require(CountActivePopulation(population) == 2,
            "portal should release the next Pop after the shared interval without waiting for the first route to finish");
    }

    private static int CountActivePopulation(PopulationSimulationService population)
    {
        int active = 0;
        foreach (PopulationActorRuntime pop in population.All)
        {
            if (!pop.IsHibernating && pop.AiState == PopulationAiState.FollowingRoute)
                active++;
        }
        return active;
    }

    private static void PopulationPortalCycleUsesPrimaryNodes()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: true);
        map.populationNodes[1].pose = new ServerPose(1f, 0f, 0f);
        map.populationPortals[0].routeNodeIds = new[] { 1001L, 1002L };
        map.populationPortals[0].minimumRespawnDelay = 0f;
        map.populationPortals[0].maximumRespawnDelay = 0f;
        map.populationPortals = new[]
        {
            map.populationPortals[0],
            new ServerPopulationPortal
            {
                stableId = 5002,
                label = "Despawn B",
                mode = PopulationPortalMode.DespawnOnly,
                allowedNpcTypes = PopulationNpcTypeMask.All,
                routeNodeId = 1002,
                exterior = new ServerPose(1f, 0f, 0f),
                minimumRespawnDelay = 0f,
                maximumRespawnDelay = 0f,
            },
        };

        var population = new PopulationSimulationService(
            new ServerMapCatalog(new[] { map }), new AuthoritativeActorRegistry(), null)
        {
            PlayerActivationDistance = 96f,
            PlayerHibernateDistance = 128f,
            ActiveDistance = 96f,
            MaximumPortalRespawnsPerTick = 8,
        };
        PopulationActorRuntime pop = SinglePopulation(population);
        var players = new[]
        {
            new PopulationPlayerView(99, "population_test", string.Empty, new WorldPosition(0f, 0f, 0f)),
        };

        double now = 1d;
        bool reachedDestination = false;
        for (int i = 0; i < 80; ++i)
        {
            population.TickBudgeted(0.25f, now, players, 1);
            now += 0.25d;
            if (pop.AiState == PopulationAiState.PortalDormant && pop.CurrentPortalId == 5002)
            {
                reachedDestination = true;
                break;
            }
        }
        Require(reachedDestination,
            "actor should arrive at the DespawnOnly portal's primary route node instead of treating the source portal's alternate join node as an exit");
        Require(pop.SpawnAnchorId == 7001,
            "destination arrival must preserve the original authored spawn-slot owner");

        bool resumedAtDestination = false;
        for (int i = 0; i < 80; ++i)
        {
            population.TickBudgeted(0.25f, now, players, 1);
            now += 0.25d;
            if (!pop.IsHibernating && pop.CurrentPortalId == 5002 &&
                pop.CurrentRouteNodeId == 1002)
            {
                resumedAtDestination = true;
                break;
            }
        }
        Require(resumedAtDestination,
            "DespawnOnly destination should resume the existing actor from its authored endpoint after the wait");
    }

    private static void MonsterFreeRoamUsesSharedRuntime()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        map.populationNodes = Array.Empty<ServerPopulationRouteNode>();
        map.populationEdges = Array.Empty<ServerPopulationRouteEdge>();
        map.spawnAnchors[0].kind = ServerSpawnKind.Monster;
        map.spawnAnchors[0].actorKind = AuthoritativeActorKind.Monster;
        map.spawnAnchors[0].archetypeId = "monster.test";
        map.spawnAnchors[0].routeNodeId = 0;

        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null)
        {
            PlayerActivationDistance = 96f,
            PlayerHibernateDistance = 128f,
            DefaultFreeRoamRadius = 20f,
            DefaultFreeRoamLeashRadius = 35f,
        };

        PopulationActorRuntime monster = SinglePopulation(population);
        Require(monster.Actor.Handle.kind == AuthoritativeActorKind.Population,
            "Monster AI should reuse the canonical lightweight Population actor family");
        Require(monster.SpawnKind == ServerSpawnKind.Monster,
            "Monster spawn kind must remain available to AI");
        Require(monster.MovementBehavior == SharedAiMovementMode.FreeRoam,
            "route-less Monster should select FreeRoam");
        Require(monster.IsHibernating,
            "Monster should start in the shared AOI hibernation path");

        WorldPosition before = monster.Actor.Position;
        var nearbyPlayer = new[]
        {
            new PopulationPlayerView(5, "population_test", string.Empty, before),
        };

        population.PrepareBudgetedTick(nearbyPlayer, 2d);
        Require(!monster.IsHibernating, "nearby player should wake the Monster");

        for (int step = 0; step < 12; ++step)
        {
            double now = 2.25d + step * 0.25d;
            population.PrepareBudgetedTick(nearbyPlayer, now);
            int guard = 0;
            while (population.HasDueWork(now) && guard++ < 8)
                population.TickNextBudgeted(0.25f, now);
        }

        float dx = monster.Actor.Position.X - before.X;
        float dz = monster.Actor.Position.Z - before.Z;
        Require(dx * dx + dz * dz > 0.01f,
            "free-roam Monster should move without Population route nodes");
    }

    private static void MonsterFreeRoamIdlesAtFarRange()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        map.populationNodes = Array.Empty<ServerPopulationRouteNode>();
        map.populationEdges = Array.Empty<ServerPopulationRouteEdge>();
        map.spawnAnchors[0].kind = ServerSpawnKind.Monster;
        map.spawnAnchors[0].actorKind = AuthoritativeActorKind.Monster;
        map.spawnAnchors[0].archetypeId = "monster.test";
        map.spawnAnchors[0].routeNodeId = 0;

        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null)
        {
            EngagedDistance = 28f,
            ActiveDistance = 80f,
            FreeRoamActiveDistance = 36f,
            PlayerActivationDistance = 96f,
            PlayerHibernateDistance = 128f,
        };

        PopulationActorRuntime monster = SinglePopulation(population);
        WorldPosition spawn = monster.Actor.Position;

        // Wake inside the existing activation threshold first.
        var nearPlayer = new[]
        {
            new PopulationPlayerView(7, "population_test", string.Empty, spawn),
        };
        population.PrepareBudgetedTick(nearPlayer, 1d);
        Require(!monster.IsHibernating, "near player should wake the Monster");

        // Move the observer beyond the 36m FreeRoam gate but keep it inside the 80m Active LOD.
        var farPlayer = new[]
        {
            new PopulationPlayerView(
                7,
                "population_test",
                string.Empty,
                new WorldPosition(spawn.X + 40f, spawn.Y, spawn.Z)),
        };

        population.PrepareBudgetedTick(farPlayer, 2d);
        int guard = 0;
        while (population.HasDueWork(2d) && guard++ < 8)
            population.TickNextBudgeted(0.25f, 2d);

        Require(!monster.IsHibernating,
            "Monster at 40m should remain awake");
        Require(monster.SimulationLod == PopulationSimulationLod.Active,
            "Monster at 40m should still be in the existing Active LOD tier");

        WorldPosition beforeIdle = monster.Actor.Position;

        for (int step = 0; step < 4; ++step)
        {
            double now = 3d + step;
            population.PrepareBudgetedTick(farPlayer, now);
            guard = 0;
            while (population.HasDueWork(now) && guard++ < 8)
                population.TickNextBudgeted(0.25f, now);
        }

        float dx = monster.Actor.Position.X - beforeIdle.X;
        float dz = monster.Actor.Position.Z - beforeIdle.Z;
        Require(dx * dx + dz * dz <= 0.0001f,
            "Monster beyond FreeRoamActiveDistance should idle instead of free-roaming");
        Require(Math.Abs(monster.Actor.VelocityX) <= 0.0001f &&
                Math.Abs(monster.Actor.VelocityY) <= 0.0001f &&
                Math.Abs(monster.Actor.VelocityZ) <= 0.0001f,
            "far-idle Monster should publish zero movement velocity");
    }

    private static void NpcRouteMovementUsesSharedRuntime()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        map.spawnAnchors[0].kind = ServerSpawnKind.Npc;
        map.spawnAnchors[0].actorKind = AuthoritativeActorKind.Npc;
        map.spawnAnchors[0].archetypeId = "worker.test";

        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null)
        {
            PlayerActivationDistance = 96f,
            PlayerHibernateDistance = 128f,
        };

        PopulationActorRuntime npc = SinglePopulation(population);
        Require(npc.Actor.Handle.kind == AuthoritativeActorKind.Population,
            "NPC AI should reuse the canonical lightweight Population actor family");
        Require(npc.SpawnKind == ServerSpawnKind.Npc,
            "NPC spawn kind must remain available to AI");
        Require(npc.MovementBehavior == SharedAiMovementMode.Route,
            "NPC with an authored route should use Route movement");

        WorldPosition before = npc.Actor.Position;
        var nearbyPlayer = new[]
        {
            new PopulationPlayerView(6, "population_test", string.Empty, before),
        };
        population.PrepareBudgetedTick(nearbyPlayer, 20d);

        Require(!npc.IsHibernating, "nearby player should wake the NPC");
        float dx = npc.Actor.Position.X - before.X;
        float dz = npc.Actor.Position.Z - before.Z;
        Require(dx * dx + dz * dz > 0.25f,
            "NPC should reuse existing route catch-up movement");
    }

    private static void NpcWithoutRouteGraphIsStationary()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        map.populationNodes = Array.Empty<ServerPopulationRouteNode>();
        map.populationEdges = Array.Empty<ServerPopulationRouteEdge>();
        map.spawnAnchors[0].kind = ServerSpawnKind.Npc;
        map.spawnAnchors[0].actorKind = AuthoritativeActorKind.Npc;
        map.spawnAnchors[0].archetypeId = "worker.stationary";
        map.spawnAnchors[0].routeNodeId = 0;

        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null);

        PopulationActorRuntime npc = SinglePopulation(population);
        Require(npc.MovementBehavior == SharedAiMovementMode.Stationary,
            "NPC with no explicit route and no route graph should resolve Stationary");
        Require(npc.CurrentRouteNodeId == 0,
            "Stationary NPC should not retain a route node");
    }

    private static void NpcWithoutExplicitRouteUsesNearestRouteFallback()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        map.populationNodes = new[] { map.populationNodes[0] };
        map.populationEdges = Array.Empty<ServerPopulationRouteEdge>();
        map.spawnAnchors[0].kind = ServerSpawnKind.Npc;
        map.spawnAnchors[0].actorKind = AuthoritativeActorKind.Npc;
        map.spawnAnchors[0].archetypeId = "worker.nearest";
        map.spawnAnchors[0].routeNodeId = 0;

        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null);

        PopulationActorRuntime npc = SinglePopulation(population);
        Require(npc.MovementBehavior == SharedAiMovementMode.Route,
            "NPC with no explicit route should preserve the existing nearest-route fallback");
        Require(npc.CurrentRouteNodeId == 1001,
            "nearest-route fallback should bind the test NPC to the nearest valid route node");
    }

    private static void NpcInvalidExplicitRouteNormalizesToNearestRoute()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        map.populationNodes = new[] { map.populationNodes[0] };
        map.populationEdges = Array.Empty<ServerPopulationRouteEdge>();
        map.spawnAnchors[0].kind = ServerSpawnKind.Npc;
        map.spawnAnchors[0].actorKind = AuthoritativeActorKind.Npc;
        map.spawnAnchors[0].archetypeId = "worker.invalid-route";
        map.spawnAnchors[0].routeNodeId = 999999;

        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null);

        PopulationActorRuntime npc = SinglePopulation(population);
        Require(npc.MovementBehavior == SharedAiMovementMode.Route,
            "NPC with an invalid explicit route should preserve legacy nearest-route resolution");
        Require(npc.CurrentRouteNodeId == 1001,
            "invalid explicit route should normalize immediately to the nearest valid route node");
    }

    private static void MonsterInvalidExplicitRouteResolvesFreeRoam()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: false);
        map.spawnAnchors[0].kind = ServerSpawnKind.Monster;
        map.spawnAnchors[0].actorKind = AuthoritativeActorKind.Monster;
        map.spawnAnchors[0].archetypeId = "monster.invalid-route";
        map.spawnAnchors[0].routeNodeId = 999999;

        var catalog = new ServerMapCatalog(new[] { map });
        var actors = new AuthoritativeActorRegistry();
        var population = new PopulationSimulationService(catalog, actors, null);

        PopulationActorRuntime monster = SinglePopulation(population);
        Require(monster.MovementBehavior == SharedAiMovementMode.FreeRoam,
            "Monster with an invalid explicit route should preserve FreeRoam fallback");
        Require(monster.CurrentRouteNodeId == 0,
            "FreeRoam Monster should not retain the invalid route node");
    }

    private static PopulationActorRuntime SinglePopulation(PopulationSimulationService population)
    {
        PopulationActorRuntime found = null;
        int count = 0;
        foreach (PopulationActorRuntime pop in population.All)
        {
            found = pop;
            count++;
        }
        Require(count == 1 && found != null, "test map should create exactly one Population actor");
        return found;
    }

    private static ServerMapSnapshot CreatePopulationMap(bool includePortal)
    {
        var first = new ServerPopulationRouteNode
        {
            stableId = 1001,
            label = "A",
            pose = new ServerPose(0f, 0f, 0f),
            pathWidth = 2.5f,
            routeWeight = 1f,
            allowedNpcTypes = PopulationNpcTypeMask.All,
        };
        var second = new ServerPopulationRouteNode
        {
            stableId = 1002,
            label = "B",
            pose = new ServerPose(1000f, 0f, 0f),
            pathWidth = 2.5f,
            routeWeight = 1f,
            allowedNpcTypes = PopulationNpcTypeMask.All,
        };

        ServerPopulationPortal[] portals = includePortal
            ? new[]
            {
                new ServerPopulationPortal
                {
                    stableId = 5001,
                    label = "Test Door",
                    mode = PopulationPortalMode.SpawnAndDespawn,
                    portalType = PopulationPortalType.GenericBuilding,
                    allowedNpcTypes = PopulationNpcTypeMask.All,
                    routeNodeId = first.stableId,
                    interiorSpawn = new ServerPose(0f, 0f, -2f),
                    approach = new ServerPose(0f, 0f, -1.2f),
                    interaction = new ServerPose(0f, 0f, -0.8f),
                    threshold = new ServerPose(0f, 0f, -0.3f),
                    exterior = first.pose,
                    minimumRespawnDelay = 0f,
                    maximumRespawnDelay = 0f,
                    blockedRetryDelay = 0.25f,
                    minimumSpawnInterval = 0.75f,
                    maximumSpawnInterval = 1.75f,
                    spawnBurstLimit = 1,
                    maximumActiveNearby = 12,
                    activeNearbyRadius = 18f,
                    exitClearanceRadius = 0.35f,
                    suppressWhenVisibleToPlayers = false,
                },
            }
            : Array.Empty<ServerPopulationPortal>();
        return new ServerMapSnapshot
        {
            formatVersion = ServerMapFormat.Version,
            mapId = "population_test",
            instanceId = string.Empty,
            collisionTriangles = FlatPopulationTestGround(),
            populationNodes = new[] { first, second },
            populationEdges = new[]
            {
                new ServerPopulationRouteEdge
                {
                    fromNodeId = first.stableId,
                    toNodeId = second.stableId,
                    oneWay = false,
                    width = 2.5f,
                    weight = 1f,
                    validatedClear = true,
                },
            },
            populationPortals = portals,
            spawnAnchors = new[]
            {
                new ServerSpawnAnchor
                {
                    stableId = 7001,
                    label = "Population Test Spawn",
                    kind = ServerSpawnKind.Population,
                    actorKind = AuthoritativeActorKind.Population,
                    archetypeId = "civilian.test",
                    pose = first.pose,
                    enabled = true,
                    capsuleRadius = 0.35f,
                    capsuleHeight = 1.8f,
                    maximumGroundSnap = 0.65f,
                    routeNodeId = first.stableId,
                    portalId = includePortal ? 5001 : 0,
                },
            },
        };
    }

    private static ServerCollisionTriangle[] FlatPopulationTestGround()
    {
        const float extent = 2000f;
        ServerSurfaceFlags flags = ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground | ServerSurfaceFlags.Sidewalk;
        return new[]
        {
            new ServerCollisionTriangle
            {
                surfaceId = 1,
                ax = -extent, ay = 0f, az = -extent,
                bx = -extent, by = 0f, bz = extent,
                cx = extent, cy = 0f, cz = extent,
                normalX = 0f, normalY = 1f, normalZ = 0f,
                flags = flags,
            },
            new ServerCollisionTriangle
            {
                surfaceId = 1,
                ax = -extent, ay = 0f, az = -extent,
                bx = extent, by = 0f, bz = extent,
                cx = extent, cy = 0f, cz = -extent,
                normalX = 0f, normalY = 1f, normalZ = 0f,
                flags = flags,
            },
        };
    }

    private static ServerMapSnapshot CertifiedMovementMap(ServerDynamicBlocker[] blockers = null)
    {
        const float extent = 10f;
        ServerSurfaceFlags flags = ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground;
        ServerCollisionTriangle[] ground =
        {
            new ServerCollisionTriangle
            {
                surfaceId = 1,
                ax = -extent, ay = 0f, az = -extent,
                bx = -extent, by = 0f, bz = extent,
                cx = extent, cy = 0f, cz = extent,
                normalY = 1f,
                flags = flags,
            },
            new ServerCollisionTriangle
            {
                surfaceId = 1,
                ax = -extent, ay = 0f, az = -extent,
                bx = extent, by = 0f, bz = extent,
                cx = extent, cy = 0f, cz = -extent,
                normalY = 1f,
                flags = flags,
            },
        };
        return new ServerMapSnapshot
        {
            mapId = "certified_movement_test",
            movementTriangles = ground,
            collisionTriangles = ground,
            safeMovementTriangles = ground,
            safeMovementRadius = 0.35f,
            safeMovementHeight = 1.8f,
            dynamicBlockers = blockers ?? Array.Empty<ServerDynamicBlocker>(),
        };
    }

    private static void CertifiedFlatMove()
    {
        var world = new ServerCollisionWorld(CertifiedMovementMap());
        WorldPosition moved = world.ResolveHorizontalMove(
            new WorldPosition(0f, 0f, 0f), 0.5f, 0f,
            new ServerCapsule(0.35f, 1.8f), 0.4f, 0.5f, 50f,
            grounded: true, allowWallSlide: false, out bool blocked);
        Require(!blocked, "clear certified movement should remain unblocked");
        Require(Math.Abs(moved.X - 0.5f) < 0.001f && Math.Abs(moved.Y) < 0.001f,
            "clear certified movement should preserve the requested horizontal step and ground height");
    }

    private static void CertifiedMoveKeepsDynamicBlockers()
    {
        var blocker = new ServerDynamicBlocker
        {
            stableId = 9001,
            pose = new ServerPose(0.8f, 1f, 0f),
            sizeX = 0.3f,
            sizeY = 2f,
            sizeZ = 1f,
            enabledByDefault = true,
        };
        var world = new ServerCollisionWorld(CertifiedMovementMap(new[] { blocker }));
        WorldPosition moved = world.ResolveHorizontalMove(
            new WorldPosition(0f, 0f, 0f), 1.2f, 0f,
            new ServerCapsule(0.35f, 1.8f), 0.4f, 0.5f, 50f,
            grounded: true, allowWallSlide: false, out bool blocked);
        Require(blocked, "movement through an enabled dynamic blocker must still be rejected");
        Require(moved.X < 0.8f, "the authoritative result must stop before the blocker");
    }

    private static void CertifiedMoveKeepsStaticCollision()
    {
        ServerMapSnapshot map = CertifiedMovementMap();
        map.safeMovementTriangles = new[]
        {
            new ServerCollisionTriangle
            {
                ax = -1f, ay = 0f, az = -1f,
                bx = -1f, by = 0f, bz = 1f,
                cx = 0.35f, cy = 0f, cz = 1f,
                normalY = 1f,
            },
            new ServerCollisionTriangle
            {
                ax = -1f, ay = 0f, az = -1f,
                bx = 0.35f, by = 0f, bz = 1f,
                cx = 0.35f, cy = 0f, cz = -1f,
                normalY = 1f,
            },
        };

        ServerCollisionTriangle[] ground = map.collisionTriangles;
        var wallA = new ServerCollisionTriangle
        {
            ax = 0.6f, ay = 0f, az = -2f,
            bx = 0.6f, by = 2f, bz = -2f,
            cx = 0.6f, cy = 2f, cz = 2f,
        };
        var wallB = new ServerCollisionTriangle
        {
            ax = 0.6f, ay = 0f, az = -2f,
            bx = 0.6f, by = 2f, bz = 2f,
            cx = 0.6f, cy = 0f, cz = 2f,
        };
        map.collisionTriangles = new[]
        {
            ground[0], ground[1], wallA, wallB,
        };

        var world = new ServerCollisionWorld(map);
        WorldPosition moved = world.ResolveHorizontalMove(
            new WorldPosition(0f, 0f, 0f), 1.2f, 0f,
            new ServerCapsule(0.35f, 1.8f), 0.4f, 0.5f, 50f,
            grounded: true, allowWallSlide: false, out bool blocked);
        Require(blocked, "movement outside the certified region must retain static obstacle enforcement");
        Require(moved.X < 0.6f, "the authoritative result must stop before the static wall");
    }

    private static (PlayerItemService Service, PlayerRuntime Runtime, FakeRepository Repository) CreateHarness(CommitBehavior behavior)
    {
        var content = new GameplayContentCatalog(new GameplayContentSnapshot
        {
            revision = 1,
            baseInventoryCapacity = 2,
        });

        var item = new ItemInstanceState(new ItemInstanceId(10), "test.item", 1, 0, 0);
        var inventory = new InventoryState(2, 0, new[] { item, null });
        var equipment = new EquipmentState(0);
        var repository = new FakeRepository(ToRecord(inventory, equipment), behavior);
        var service = new PlayerItemService(content, repository);
        var runtime = new PlayerRuntime(
            new AccountId(1),
            new CharacterId(1),
            PlayerSessionId.New(),
            new CharacterState("Regression"),
            new CharacterLocationState("test", string.Empty, new WorldPosition(0f, 0f, 0f), 0f),
            0);
        runtime.InitializePlayerItemSystems(inventory, equipment, StatsState.DefaultCharacter());
        return (service, runtime, repository);
    }

    private static PlayerSystemsPersistenceRecord ToRecord(InventoryState inventory, EquipmentState equipment)
    {
        ItemInstanceState[] slots = inventory.CopySlots();
        var persistedInventory = new List<PersistedInventoryItem>();
        for (int i = 0; i < slots.Length; ++i)
        {
            ItemInstanceState item = slots[i];
            if (item == null) continue;
            persistedInventory.Add(new PersistedInventoryItem(
                i,
                item.ItemInstanceId,
                item.DefinitionId,
                item.Quantity,
                item.Durability,
                item.Revision,
                item.LoadedAmmoDefinitionId,
                item.LoadedRounds,
                item.MagazineRevision));
        }

        EquippedItemState[] equipped = equipment.Snapshot();
        var persistedEquipment = new PersistedEquipmentItem[equipped.Length];
        for (int i = 0; i < equipped.Length; ++i)
        {
            EquippedItemState entry = equipped[i];
            persistedEquipment[i] = new PersistedEquipmentItem(
                entry.SlotId,
                entry.Item.ItemInstanceId,
                entry.Item.DefinitionId,
                entry.Item.Quantity,
                entry.Item.Durability,
                entry.Item.Revision,
                entry.Item.LoadedAmmoDefinitionId,
                entry.Item.LoadedRounds,
                entry.Item.MagazineRevision);
        }

        return new PlayerSystemsPersistenceRecord(
            inventory.Capacity,
            inventory.Revision,
            equipment.Revision,
            persistedInventory.ToArray(),
            persistedEquipment);
    }

    private static async Task Run(string name, Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {name}: {ex.Message}");
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static void RunSync(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {name}: {ex.Message}");
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
