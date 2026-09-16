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

    private static async Task Main()
    {
        await Run("lost response after commit reconciles as success", CommitThenThrowReconcilesAsSuccess);
        await Run("HttpClient-style timeout after commit reconciles as success", TimeoutAfterCommitReconcilesAsSuccess);
        await Run("stale response reloads authoritative state", StaleResponseReloadsAuthoritativeState);
        RunSync("transient world item ids are isolated from durable ids", TransientWorldIdsUseReservedRange);
        RunSync("Population hibernates out of AOI and catches route progress up on wake", PopulationHibernationCatchesUpOnWake);
        RunSync("Population portal spawn remains dormant until a player activates it", PopulationPortalActivationUsesExistingPortal);
        RunSync("Population portal can release multiple active Pops from one authored spawn", PopulationPortalAllowsMultipleActive);
        RunSync("Population portal staggers release cadence without per-Pop timers", PopulationPortalStaggersReleaseCadence);
        RunSync("Monster free roam uses shared actor runtime without route nodes", MonsterFreeRoamUsesSharedRuntime);
        RunSync("Monster free roam idles beyond configured free-roam distance", MonsterFreeRoamIdlesAtFarRange);
        RunSync("NPC route movement uses shared actor runtime", NpcRouteMovementUsesSharedRuntime);
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
        Require(pop.PortalSequencePhase == PopulationPortalSequencePhase.Interior,
            "portal-spawned Population should begin the authored interior-to-exterior sequence");
        Require(!actors.IsSpatiallySuspended(pop.Actor), "portal-spawned Population should resume authoritative spatial presence");
    }


    private static void PopulationPortalAllowsMultipleActive()
    {
        ServerMapSnapshot map = CreatePopulationMap(includePortal: true);
        map.populationPortals[0].maximumActiveNearby = 17;
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
