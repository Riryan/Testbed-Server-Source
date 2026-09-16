using System;
using System.Collections.Generic;
using System.Threading;
using Game.Server.Application.Content;
using Game.Server.Application.Population;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.Stats;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Actors;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Identity;
using Game.Shared.Interactions;
using Game.Shared.Population;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;
using Game.Shared.World;

namespace Game.Server.Application.Combat
{
    public enum CombatTestDummyPreset : byte
    {
        NormalDefense = 0,
        ConductivePlate = 1,
        PoisonResistant = 2,
        PoisonImmune = 3,
        FireWeak = 4,
        Invulnerable = 5,
    }

    public readonly struct CombatTestDummyView
    {
        public long StableId { get; }
        public string Label { get; }
        public string MapId { get; }
        public string InstanceId { get; }
        public PlayerRuntime Runtime { get; }
        public CombatTestDummyPreset Preset { get; }

        public CombatTestDummyView(long stableId, string label, string mapId, string instanceId, PlayerRuntime runtime, CombatTestDummyPreset preset)
        {
            StableId = stableId;
            Label = label ?? string.Empty;
            MapId = mapId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            Runtime = runtime;
            Preset = preset;
        }
    }

    /// <summary>
    /// Canonical combat/resource/status runtime bridge for non-player Population actors.
    ///
    /// This is the promoted Combat Test Dummy vertical slice: Population retains the existing
    /// actor registry, scheduler, movement and interaction identity while this service supplies
    /// the PlayerRuntime-backed combat state that the established Combat/Ability/Status systems
    /// already consume. The baked Combat Test Dummy remains a developer fixture on the same
    /// service so there is no second production combat-target implementation beside Population.
    /// </summary>
    public sealed class PopulationCombatService : IDisposable
    {
        private sealed class DummyEntry
        {
            public long StableId;
            public string Label = string.Empty;
            public string MapId = string.Empty;
            public string InstanceId = string.Empty;
            public ServerCombatTestDummyDefinition Definition;
            public PlayerRuntime Runtime;
            public CombatTestDummyPreset Preset;
        }

        private readonly Dictionary<string, DummyEntry> _dummiesByWorldId =
            new Dictionary<string, DummyEntry>(StringComparer.Ordinal);
        private readonly Dictionary<PlayerRuntime, DummyEntry> _dummiesByRuntime =
            new Dictionary<PlayerRuntime, DummyEntry>();
        private readonly Dictionary<PlayerRuntime, PopulationActorRuntime> _populationByRuntime =
            new Dictionary<PlayerRuntime, PopulationActorRuntime>();
        private readonly Dictionary<long, PlayerRuntime> _byCharacterId =
            new Dictionary<long, PlayerRuntime>();

        private readonly PopulationSimulationService _population;
        private readonly GameplayContentCatalog _content;
        private readonly CharacterResourceService _resources;
        private readonly StatusEffectService _statuses;
        private long _nextSyntheticId = 8_000_000_000_000_000_000L;
        private bool _disposed;

        public PopulationCombatService(
            ServerMapCatalog maps,
            PopulationSimulationService population,
            GameplayContentCatalog content,
            CharacterResourceService resources,
            StatusEffectService statuses)
        {
            _population = population ?? throw new ArgumentNullException(nameof(population));
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _statuses = statuses ?? throw new ArgumentNullException(nameof(statuses));

            // Preserve the baked Combat Test Dummy fixture's synthetic runtime ordering/IDs.
            // Population combat state is materialized lazily on first combat use so ambient
            // simulation actors keep their existing lightweight runtime until they need it.
            if (maps != null)
                BuildCombatTestFixtures(maps);

            _population.Changed += SynchronizePopulationLocation;
            _population.Removed += DetachPopulation;
        }

        public int Count => _populationByRuntime.Count + _dummiesByRuntime.Count;

        public event Action<PlayerRuntime> PopulationRuntimeActivated;
        public event Action<PlayerRuntime> PopulationRuntimeDeactivated;

        /// <summary>
        /// Lightweight canonical Population actors. Enumerating this collection does not create
        /// PlayerRuntime combat state; callers materialize only the actor that actually wins an
        /// authoritative target/contact query.
        /// </summary>
        public IEnumerable<PopulationActorRuntime> PopulationActors => _population.All;

        public IEnumerable<PlayerRuntime> CombatTestRuntimes
        {
            get
            {
                foreach (DummyEntry entry in _dummiesByRuntime.Values)
                    if (entry.Runtime != null)
                        yield return entry.Runtime;
            }
        }

        public bool TryGetPopulationActor(
            long actorId,
            ushort generation,
            out PopulationActorRuntime pop)
        {
            if (actorId > 0 && generation > 0 &&
                _population.TryGet(actorId, out pop) &&
                IsCombatTargetable(pop) &&
                pop.Actor.Handle.generation == generation)
            {
                return true;
            }

            pop = null;
            return false;
        }

        public bool TryActivatePopulation(PopulationActorRuntime pop, out PlayerRuntime runtime)
        {
            runtime = null;
            if (_disposed || !IsCombatTargetable(pop))
                return false;

            if (pop.CombatRuntime != null)
            {
                runtime = pop.CombatRuntime;
                return true;
            }

            runtime = CreatePopulationRuntime(pop);
            pop.CombatRuntime = runtime;
            _populationByRuntime[runtime] = pop;
            _byCharacterId[runtime.CharacterId.Value] = runtime;
            runtime.ResourceChanged += OnPopulationResourceChanged;
            PopulationRuntimeActivated?.Invoke(runtime);
            return true;
        }

        private static bool IsCombatTargetable(PopulationActorRuntime pop) =>
            pop?.Actor != null &&
            pop.Actor.Handle.kind == AuthoritativeActorKind.Population &&
            pop.Actor.Alive &&
            pop.Actor.HealthCurrent > 0 &&
            pop.AiState != PopulationAiState.PortalDormant &&
            !pop.IsHibernating;

        public bool TryGet(
            string mapId,
            string instanceId,
            long stableId,
            out CombatTestDummyView view)
        {
            if (_dummiesByWorldId.TryGetValue(Key(mapId, instanceId, stableId), out DummyEntry entry))
            {
                view = ToView(entry);
                return true;
            }

            view = default;
            return false;
        }

        public bool TryGetNonPlayerWorld(
            PlayerRuntime runtime,
            out string mapId,
            out string instanceId)
        {
            if (runtime != null && _populationByRuntime.TryGetValue(runtime, out PopulationActorRuntime pop) && pop?.Actor != null)
            {
                mapId = pop.Actor.MapId ?? string.Empty;
                instanceId = pop.Actor.InstanceId ?? string.Empty;
                return true;
            }

            if (runtime != null && _dummiesByRuntime.TryGetValue(runtime, out DummyEntry dummy))
            {
                mapId = dummy.MapId ?? string.Empty;
                instanceId = dummy.InstanceId ?? string.Empty;
                return true;
            }

            mapId = string.Empty;
            instanceId = string.Empty;
            return false;
        }

        public PlayerRuntime ResolveRuntime(long characterId) =>
            _byCharacterId.TryGetValue(characterId, out PlayerRuntime runtime) ? runtime : null;

        public string ExecuteDeveloperAction(
            CombatTestDummyView view,
            InteractionActionId actionId,
            double now,
            out bool success)
        {
            success = false;
            if (view.Runtime == null || !_dummiesByRuntime.TryGetValue(view.Runtime, out DummyEntry entry))
                return "combat test dummy is unavailable";

            switch (actionId)
            {
                case InteractionActionId.CombatDummyResetHealth:
                    success = ResetHealth(entry.Runtime);
                    return success ? "health reset to authoritative maximum" : "health reset failed";
                case InteractionActionId.CombatDummyClearStatuses:
                    int removed = ClearStatuses(entry.Runtime);
                    success = true;
                    return $"cleared {removed} active status effect(s)";
                case InteractionActionId.CombatDummyNormalDefense:
                    ApplyPreset(entry, CombatTestDummyPreset.NormalDefense);
                    success = true;
                    return "preset: Normal Defense";
                case InteractionActionId.CombatDummyConductivePlate:
                    ApplyPreset(entry, CombatTestDummyPreset.ConductivePlate);
                    success = true;
                    return "preset: Conductive Plate (Electric x1.75)";
                case InteractionActionId.CombatDummyPoisonResistant:
                    ApplyPreset(entry, CombatTestDummyPreset.PoisonResistant);
                    success = true;
                    return "preset: Poison Resistant (Poison x0.50)";
                case InteractionActionId.CombatDummyPoisonImmune:
                    ApplyPreset(entry, CombatTestDummyPreset.PoisonImmune);
                    success = true;
                    return "preset: Poison Immune (Poison x0.00)";
                case InteractionActionId.CombatDummyFireWeak:
                    ApplyPreset(entry, CombatTestDummyPreset.FireWeak);
                    success = true;
                    return "preset: Fire Weak (Fire x1.50)";
                case InteractionActionId.CombatDummyInvulnerable:
                    ApplyPreset(entry, CombatTestDummyPreset.Invulnerable);
                    success = true;
                    return "preset: Invulnerable";
                case InteractionActionId.CombatDummyShowStats:
                    success = true;
                    return Describe(entry);
                default:
                    return "unsupported combat test dummy action";
            }
        }

        public void HandleKilled(PlayerRuntime runtime, CombatDamageResult result)
        {
            if (runtime == null)
                return;

            if (_populationByRuntime.TryGetValue(runtime, out PopulationActorRuntime pop) && pop?.Actor != null)
            {
                // Population death is persistent until the later canonical decay/respawn policy.
                // Do not allow generic character on-death resource reset rules to revive the combat
                // runtime underneath an actor that has already entered Dead state.
                if (runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health) &&
                    health.Current > health.Minimum)
                {
                    _resources.Set(runtime, CharacterResourceId.Health, health.Minimum, CharacterResourceChangeReason.Death);
                }
                _population.SetDead(pop.Actor.Handle.actorId);
                return;
            }

            if (!_dummiesByRuntime.TryGetValue(runtime, out DummyEntry entry) ||
                entry.Definition == null || !entry.Definition.resetOnDefeat)
            {
                return;
            }

            ResetHealth(runtime);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _population.Changed -= SynchronizePopulationLocation;
            _population.Removed -= DetachPopulation;

            foreach (PlayerRuntime runtime in _populationByRuntime.Keys)
                runtime.ResourceChanged -= OnPopulationResourceChanged;
        }

        private void DetachPopulation(PopulationActorRuntime pop)
        {
            if (pop?.CombatRuntime == null)
                return;

            PlayerRuntime runtime = pop.CombatRuntime;
            runtime.ResourceChanged -= OnPopulationResourceChanged;
            _populationByRuntime.Remove(runtime);
            _byCharacterId.Remove(runtime.CharacterId.Value);
            pop.CombatRuntime = null;
            PopulationRuntimeDeactivated?.Invoke(runtime);
        }

        private void SynchronizePopulationLocation(PopulationActorRuntime pop)
        {
            if (pop?.Actor == null || pop.CombatRuntime == null)
                return;

            pop.CombatRuntime.UpdateLocation(new CharacterLocationState(
                pop.Actor.MapId,
                pop.Actor.InstanceId,
                pop.Actor.Position,
                pop.Actor.YawDegrees));
        }

        private void OnPopulationResourceChanged(CharacterResourceChange change)
        {
            if (change.ResourceId != CharacterResourceId.Health ||
                !_byCharacterId.TryGetValue(change.CharacterId.Value, out PlayerRuntime runtime) ||
                !_populationByRuntime.TryGetValue(runtime, out PopulationActorRuntime pop) ||
                pop?.Actor == null)
            {
                return;
            }

            _population.SynchronizeCombatHealth(
                pop.Actor.Handle.actorId,
                change.Current,
                change.Maximum);
        }

        private PlayerRuntime CreatePopulationRuntime(PopulationActorRuntime pop)
        {
            int healthMaximum = Math.Max(1, pop.Actor.HealthMaximum);
            PlayerRuntime runtime = CreateRuntime(
                pop.Actor.DisplayName,
                pop.Actor.MapId,
                pop.Actor.InstanceId,
                pop.Actor.Position,
                pop.Actor.YawDegrees,
                BuildPopulationStats(healthMaximum));

            if (pop.Actor.HealthCurrent < healthMaximum &&
                runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health))
            {
                _resources.Set(
                    runtime,
                    CharacterResourceId.Health,
                    Math.Max(health.Minimum, Math.Min(health.Maximum, pop.Actor.HealthCurrent)),
                    CharacterResourceChangeReason.Administrative);
            }

            return runtime;
        }

        private void BuildCombatTestFixtures(ServerMapCatalog maps)
        {
            foreach (ServerMapSnapshot map in maps.Snapshots)
            {
                if (map == null)
                    continue;

                ServerWorldInteractableDefinition[] interactables =
                    map.interactables ?? Array.Empty<ServerWorldInteractableDefinition>();
                for (int i = 0; i < interactables.Length; ++i)
                {
                    ServerWorldInteractableDefinition source = interactables[i];
                    ServerCombatTestDummyDefinition definition = source?.combatTestDummy;
                    if (source == null || definition == null || !definition.enabled || source.stableId <= 0)
                        continue;

                    var entry = new DummyEntry
                    {
                        StableId = source.stableId,
                        Label = string.IsNullOrWhiteSpace(source.label) ? "Combat Test Dummy" : source.label,
                        MapId = map.mapId ?? string.Empty,
                        InstanceId = map.instanceId ?? string.Empty,
                        Definition = definition,
                        Preset = CombatTestDummyPreset.NormalDefense,
                    };

                    entry.Runtime = CreateRuntime(
                        entry.Label,
                        entry.MapId,
                        entry.InstanceId,
                        source.pose.ToWorldPosition(),
                        source.pose.yaw,
                        BuildDummyStats(entry, CombatTestDummyPreset.NormalDefense));

                    _dummiesByWorldId[Key(entry.MapId, entry.InstanceId, entry.StableId)] = entry;
                    _dummiesByRuntime[entry.Runtime] = entry;
                    _byCharacterId[entry.Runtime.CharacterId.Value] = entry.Runtime;
                }
            }
        }

        private PlayerRuntime CreateRuntime(
            string label,
            string mapId,
            string instanceId,
            WorldPosition position,
            float yawDegrees,
            StatsState stats)
        {
            long ordinal = Interlocked.Increment(ref _nextSyntheticId);
            long accountValue = 7_000_000_000_000_000_000L +
                                (ordinal - 8_000_000_000_000_000_000L);
            var runtime = new PlayerRuntime(
                new AccountId(accountValue),
                new CharacterId(ordinal),
                PlayerSessionId.New(),
                new CharacterState(string.IsNullOrWhiteSpace(label) ? "Population" : label),
                new CharacterLocationState(mapId, instanceId, position, yawDegrees),
                0);

            runtime.InitializePlayerItemSystems(new InventoryState(1, 0), new EquipmentState(0), stats);
            runtime.InitializeCharacterResources(_resources.CreateInitialState(stats, 0, null));
            return runtime;
        }

        private static StatsState BuildPopulationStats(int healthMaximum)
        {
            var values = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("Health.Max", Math.Max(1, healthMaximum)),
                new KeyValuePair<string, float>("Mana.Max", 100f),
                new KeyValuePair<string, float>("Stamina.Max", 100f),
                new KeyValuePair<string, float>("Armor", 0f),
                new KeyValuePair<string, float>("AttackPower", 0f),
            };
            return new StatsState(values);
        }

        private void ApplyPreset(DummyEntry entry, CombatTestDummyPreset preset)
        {
            if (entry?.Runtime == null)
                return;
            entry.Preset = preset;
            entry.Runtime.SetInvincible(preset == CombatTestDummyPreset.Invulnerable);
            entry.Runtime.TryReplaceCalculatedStats(0, BuildDummyStats(entry, preset));
        }

        private StatsState BuildDummyStats(DummyEntry entry, CombatTestDummyPreset preset)
        {
            int health = Math.Max(1, entry?.Definition?.healthMaximum ?? 500);
            float armor = Math.Max(0f, entry?.Definition?.armor ?? 0f);
            var values = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("Health.Max", health),
                new KeyValuePair<string, float>("Mana.Max", 100f),
                new KeyValuePair<string, float>("Stamina.Max", 100f),
                new KeyValuePair<string, float>("Armor", armor),
                new KeyValuePair<string, float>("AttackPower", 0f),
            };

            AddResponse(values, entry?.Definition?.electricDamageTypeDefinitionId,
                preset == CombatTestDummyPreset.ConductivePlate ? 1.75f : 1f);
            AddResponse(values, entry?.Definition?.poisonDamageTypeDefinitionId,
                preset == CombatTestDummyPreset.PoisonResistant ? 0.50f :
                preset == CombatTestDummyPreset.PoisonImmune ? 0f : 1f);
            AddResponse(values, entry?.Definition?.fireDamageTypeDefinitionId,
                preset == CombatTestDummyPreset.FireWeak ? 1.50f : 1f);
            return new StatsState(values);
        }

        private void AddResponse(
            List<KeyValuePair<string, float>> values,
            string damageTypeDefinitionId,
            float multiplier)
        {
            if (values == null || string.IsNullOrWhiteSpace(damageTypeDefinitionId) ||
                !_content.TryGetDamageType(damageTypeDefinitionId.Trim(), out DamageTypeDefinition type) ||
                type == null || type.wireId == 0)
            {
                return;
            }

            values.Add(new KeyValuePair<string, float>(
                CombatDerivedStatIds.DamageResponseMultiplier(type.wireId),
                multiplier));
        }

        private bool ResetHealth(PlayerRuntime runtime)
        {
            if (runtime == null ||
                !runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health))
            {
                return false;
            }

            return _resources.Set(
                runtime,
                CharacterResourceId.Health,
                health.Maximum,
                CharacterResourceChangeReason.Administrative).Success;
        }

        private int ClearStatuses(PlayerRuntime runtime)
        {
            if (runtime == null)
                return 0;

            int removed = 0;
            StatusEffectInstanceState[] active = runtime.CaptureStatusEffects().Snapshot();
            for (int i = 0; i < active.Length; ++i)
            {
                if (_statuses.Remove(
                        runtime,
                        active[i].DefinitionId,
                        StatusEffectChangeReason.Administrative).Success)
                {
                    removed++;
                }
            }
            return removed;
        }

        private string Describe(DummyEntry entry)
        {
            PlayerRuntime runtime = entry.Runtime;
            runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health);
            int statuses = runtime.CaptureStatusEffects().Snapshot().Length;
            return $"{entry.Label}: HP {health.Current}/{health.Maximum}, Armor {runtime.GetStat("Armor", 0f):0.##}, " +
                   $"Fire x{GetResponse(runtime, entry.Definition?.fireDamageTypeDefinitionId):0.##}, " +
                   $"Electric x{GetResponse(runtime, entry.Definition?.electricDamageTypeDefinitionId):0.##}, " +
                   $"Poison x{GetResponse(runtime, entry.Definition?.poisonDamageTypeDefinitionId):0.##}, " +
                   $"Invincible={runtime.Combat.Invincible}, Statuses={statuses}";
        }

        private float GetResponse(PlayerRuntime runtime, string damageTypeDefinitionId)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(damageTypeDefinitionId) ||
                !_content.TryGetDamageType(damageTypeDefinitionId.Trim(), out DamageTypeDefinition type) ||
                type == null || type.wireId == 0)
            {
                return 1f;
            }

            return runtime.GetStat(
                CombatDerivedStatIds.DamageResponseMultiplier(type.wireId),
                1f);
        }

        private static CombatTestDummyView ToView(DummyEntry entry) =>
            new CombatTestDummyView(
                entry.StableId,
                entry.Label,
                entry.MapId,
                entry.InstanceId,
                entry.Runtime,
                entry.Preset);

        private static string Key(string mapId, string instanceId, long stableId) =>
            ServerMapId.Normalize(mapId) + "\n" + (instanceId ?? string.Empty).Trim() + "\n" + stableId;
    }
}
