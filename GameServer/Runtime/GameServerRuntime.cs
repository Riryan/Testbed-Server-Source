using Game.GameServer.Backend;
using Game.Server.Application.Abilities;
using Game.Server.Application.Actors;
using Game.Server.Application.Characters;
using Game.Server.Application.Combat;
using Game.Server.Application.Crafting;
using Game.Server.Application.Content;
using Game.Server.Application.Effects;
using Game.Server.Application.Feeding;
using Game.Server.Application.Harvesting;
using Game.Server.Application.Interactions;
using Game.Server.Application.Items;
using Game.Server.Application.Lifecycle;
using Game.Server.Application.Loot;
using Game.Server.Application.Persistence;
using Game.Server.Application.Population;
using Game.Server.Application.Progression;
using Game.Server.Application.Resources;
using Game.Server.Application.Rewards;
using Game.Server.Application.Spawning;
using Game.Server.Application.Staff;
using Game.Server.Application.Sessions;
using Game.Server.Application.StatusEffects;
using Game.Server.Application.World;
using Game.Server.Application.WorldItems;
using Game.Server.Domain.Characters;
using Game.Shared.Content;
using Game.Shared.World;
using Game.UnityIntegration.Backend;

namespace Game.GameServer.Runtime;

/// <summary>
/// Standalone .NET composition root for the existing engine-free authoritative
/// character/session/domain services. Durable state remains owned by BackendServer.
/// </summary>
internal sealed class GameServerRuntime : IDisposable
{
    private sealed class ExternalNetworkWorldAdapter : IPlayerWorldAdapter
    {
        // The LiteNetLib transport host owns network entity creation/removal and therefore
        // uses AdoptExternalEnter/CompleteExternalLeave. Queued adapter entry is fail-closed.
        public bool TryEnter(
            Game.Server.Domain.Players.PlayerRuntime runtime,
            Game.Server.Application.Connections.ConnectionKey connection,
            out PlayerWorldHandle handle)
        {
            handle = default;
            return false;
        }

        public bool TryReadLocation(
            PlayerWorldHandle handle,
            out Game.Server.Domain.Characters.CharacterLocationState location)
        {
            location = default;
            return false;
        }

        public bool TryLeave(PlayerWorldHandle handle) => false;
    }

    public BackendInternalClient Backend { get; }
    public ServerMapCatalog Maps { get; }
    public ServerSpawnService Spawns { get; }
    public AuthoritativeActorRegistry Actors { get; }
    public ActorContactService ActorContacts { get; }
    public WorldInteractableService WorldInteractables { get; }
    public InteractionSessionService InteractionSessions { get; }
    public PopulationSimulationService Population { get; }
    public PopulationLootService PopulationLoot { get; }
    public GameMasterService GameMasters { get; }
    public StaffAuditFileSink StaffAudit { get; }
    public GameplayContentCatalog Content { get; }
    public ProgressionService Progression { get; }
    public WorldIncidentObservationService IncidentObservations { get; }
    public FeedingService Feeding { get; }
    public BackendPlayerSystemsRepository PlayerSystemsRepository { get; }
    public CharacterResourceService Resources { get; }
    public StatusEffectService StatusEffects { get; }
    public CombatService Combat { get; }
    public GameplayEffectService Effects { get; }
    public PopulationCombatService PopulationCombat { get; }
    public CombatLoadoutService CombatLoadout { get; }
    public BasicAttackService BasicAttacks { get; }
    public CombatReloadService Reloads { get; }
    public AbilityService Abilities { get; }
    public InteractionService Interactions { get; }
    public CharacterLifecycleService Lifecycle { get; }
    public PlayerItemService PlayerItems { get; }
    public RewardService Rewards { get; }
    public HarvestingService Harvesting { get; }
    public CraftingService Crafting { get; }
    public WorldItemService WorldItems { get; }
    public BackendCharacterRepository CharacterRepository { get; }
    public BackendCharacterLeaseService Leases { get; }
    public DirtyPlayerTracker DirtyPlayers { get; }
    public CharacterSaveService Saves { get; }
    public PlayerSessionRegistry Sessions { get; }
    public PlayerSessionService SessionService { get; }
    public PlayerWorldBindingRegistry WorldBindings { get; }
    public PlayerWorldLifecycleService WorldLifecycle { get; }

    public GameServerRuntime(
        string backendInternalUrl,
        string gameServerKey,
        TimeSpan backendTimeout,
        string mapDataDirectory = "../Content/Maps",
        bool requireMapData = false,
        string staffAuthorizationFile = "../Content/StaffAuthorizations.json",
        string staffAuditFile = "../Logs/StaffAudit.jsonl")
    {
        Backend = new BackendInternalClient(backendInternalUrl, gameServerKey, backendTimeout);
        Maps = new ServerMapCatalog(ServerMapDataLoader.LoadDirectory(mapDataDirectory, requireMapData));
        Spawns = new ServerSpawnService(Maps);
        Actors = new AuthoritativeActorRegistry();
        ActorContacts = new ActorContactService();
        GameMasters = new GameMasterService(StaffAuthorizationLoader.Load(staffAuthorizationFile));
        StaffAudit = new StaffAuditFileSink(staffAuditFile);
        GameMasters.AuditWritten += StaffAudit.Write;

        var contentResponse = Backend.GetGameplayContentAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (contentResponse == null || !contentResponse.success || contentResponse.content == null)
            throw new InvalidOperationException("Backend gameplay content is unavailable.");

        Content = new GameplayContentCatalog(contentResponse.content);
        ValidateCanonicalFirstSpawnReadiness(requireMapData);
        Progression = new ProgressionService(Content);
        IncidentObservations = new WorldIncidentObservationService(Maps);
        WorldInteractables = new WorldInteractableService(Maps, Content, Progression);
        InteractionSessions = new InteractionSessionService(WorldInteractables);

        // Population owns canonical non-player identity, movement and scheduling. Combat is
        // attached below by PopulationCombatService rather than creating another actor runtime.
        Population = new PopulationSimulationService(Maps, Actors, WorldInteractables);
        PopulationLoot = new PopulationLootService(Content, Population, WorldInteractables);

        // All standalone character-economic persistence is fenced by the same Backend-owned
        // character authority lease used for session ownership and character checkpoints.
        Leases = new BackendCharacterLeaseService(Backend);
        PlayerSystemsRepository = new BackendPlayerSystemsRepository(Backend, Leases);

        // One canonical dependency graph is shared by Item Use, passive resource work,
        // status periodic effects, and the later Combat/Ability parity slices.
        Resources = new CharacterResourceService(Content);
        StatusEffects = new StatusEffectService(Content);
        Combat = new CombatService(Content, Resources, StatusEffects);
        Effects = new GameplayEffectService(Content, Resources, Combat, StatusEffects);
        PopulationCombat = new PopulationCombatService(Maps, Population, Content, Resources, StatusEffects);
        Combat.CharacterKilled += PopulationCombat.HandleKilled;
        CombatLoadout = new CombatLoadoutService(Content);
        BasicAttacks = new BasicAttackService(Content, Combat, Resources, CombatLoadout);
        Abilities = new AbilityService(Content, Resources, Combat, StatusEffects, Effects);
        Interactions = new InteractionService();
        Feeding = new FeedingService(Content, Resources, Progression, IncidentObservations);
        if (!Interactions.Register(Feeding))
            throw new InvalidOperationException("Feeding interaction registration failed.");
        if (!Interactions.Register(new InspectPlayerInteractionHandler()))
            throw new InvalidOperationException("Core player Inspect interaction registration failed.");
        if (!Interactions.RegisterActor(new InspectActorInteractionHandler()))
            throw new InvalidOperationException("Core actor Inspect interaction registration failed.");
        Lifecycle = new CharacterLifecycleService(Content, Resources, Abilities, Spawns);
        Combat.CharacterKilled += Lifecycle.HandleKilled;
        PlayerItems = new PlayerItemService(
            Content,
            PlayerSystemsRepository,
            PlayerSystemsRepository,
            Resources,
            StatusEffects);
        Rewards = new RewardService(PlayerItems, Progression);
        Harvesting = new HarvestingService(Content, Progression, WorldInteractables);
        Crafting = new CraftingService(Content, Progression, PlayerItems, Rewards, WorldInteractables);
        Reloads = new CombatReloadService(Content, PlayerItems, CombatLoadout);
        // Ordinary player-dropped world items are intentionally runtime-only. Inventory
        // persistence owns items while carried; dropping moves them into transient world
        // state and a GameServer restart clears any remaining ground drops.
        WorldItems = new WorldItemService(Content, PlayerItems);
        if (!Interactions.RegisterWorld(WorldItems))
            throw new InvalidOperationException("World-item Loot interaction registration failed.");

        CharacterRepository = new BackendCharacterRepository(Backend, PlayerSystemsRepository, Leases);
        DirtyPlayers = new DirtyPlayerTracker();
        Saves = new CharacterSaveService(CharacterRepository);
        Sessions = new PlayerSessionRegistry();

        var characterService = new CharacterService(
            CharacterRepository,
            new CharacterValidator(),
            new CharacterRuntimeFactory(Content));

        SessionService = new PlayerSessionService(
            Sessions,
            characterService,
            Leases,
            DirtyPlayers);

        WorldBindings = new PlayerWorldBindingRegistry();
        WorldLifecycle = new PlayerWorldLifecycleService(
            SessionService,
            WorldBindings,
            new ExternalNetworkWorldAdapter());
    }

    private void ValidateCanonicalFirstSpawnReadiness(bool requireMapData)
    {
        CharacterSpawnDefinition configured = Content.Snapshot?.initialCharacterSpawn;

        // Mapless mode remains available for isolated service/dev tests, but canonical
        // character creation is deliberately unavailable until authoritative map data exists.
        if (Maps.Count == 0 && !requireMapData)
        {
            Console.WriteLine(
                "First spawn readiness: no baked server maps are loaded; canonical character creation is disabled until map data is supplied.");
            return;
        }

        if (configured == null)
            throw new InvalidOperationException(
                "Canonical first-spawn readiness failed: gameplay content has no initialCharacterSpawn definition.");

        CharacterLocationState requested;
        try
        {
            requested = new CharacterLocationState(
                configured.mapId,
                configured.instanceId ?? string.Empty,
                new WorldPosition(configured.positionX, configured.positionY, configured.positionZ),
                configured.yawDegrees);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Canonical first-spawn readiness failed: initialCharacterSpawn is invalid: {ex.Message}", ex);
        }

        if (!Spawns.TryResolveFirstSpawn(requested, out CharacterLocationState resolved, out string detail))
        {
            throw new InvalidOperationException(
                $"Canonical first-spawn readiness failed for map '{requested.MapId}' instance '{DisplayInstance(requested.InstanceId)}': {detail}");
        }

        Console.WriteLine(
            $"First spawn ready: map='{resolved.MapId}', instance='{DisplayInstance(resolved.InstanceId)}', " +
            $"position=({resolved.Position.X:0.###},{resolved.Position.Y:0.###},{resolved.Position.Z:0.###}), yaw={resolved.YawDegrees:0.###}°");
    }

    private static string DisplayInstance(string value) =>
        string.IsNullOrWhiteSpace(value) ? "<default>" : value.Trim();

    public void Dispose()
    {
        Combat.CharacterKilled -= Lifecycle.HandleKilled;
        Combat.CharacterKilled -= PopulationCombat.HandleKilled;
        PopulationCombat.Dispose();
        PopulationLoot.Dispose();
        GameMasters.AuditWritten -= StaffAudit.Write;
        Backend.Dispose();
    }
}
