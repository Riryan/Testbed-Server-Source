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

        private readonly struct PopulationWeaponSelection
        {
            public ItemDefinition Weapon { get; }
            public ItemDefinition Ammo { get; }
            public bool HasWeapon => Weapon != null;

            public PopulationWeaponSelection(ItemDefinition weapon, ItemDefinition ammo)
            {
                Weapon = weapon;
                Ammo = ammo;
            }
        }

        private readonly Dictionary<string, DummyEntry> _dummiesByWorldId =
            new Dictionary<string, DummyEntry>(StringComparer.Ordinal);
        private readonly Dictionary<PlayerRuntime, DummyEntry> _dummiesByRuntime =
            new Dictionary<PlayerRuntime, DummyEntry>();
        private readonly Dictionary<PlayerRuntime, PopulationActorRuntime> _populationByRuntime =
            new Dictionary<PlayerRuntime, PopulationActorRuntime>();
        private readonly Dictionary<long, PlayerRuntime> _byCharacterId =
            new Dictionary<long, PlayerRuntime>();

        // Population combat loadouts are derived from the already-loaded gameplay item catalog.
        // The cache only rebuilds when the content revision changes; ambient Population still has
        // no PlayerRuntime/item state until combat activation.
        private long _populationWeaponCatalogRevision = long.MinValue;
        private ItemDefinition[] _populationFirearms = Array.Empty<ItemDefinition>();
        private ItemDefinition[] _populationMeleeWeapons = Array.Empty<ItemDefinition>();
        private readonly Dictionary<string, ItemDefinition[]> _populationAmmoByFamily =
            new Dictionary<string, ItemDefinition[]>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<long> _ordinaryRangedRoster = new HashSet<long>();
        private bool _ordinaryRangedRosterDirty = true;

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
            _population.Added += MarkPopulationWeaponRosterDirty;
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

        public bool TryGetPopulationByCharacterId(
            long characterId,
            out PopulationActorRuntime population)
        {
            population = null;
            if (characterId <= 0 ||
                !_byCharacterId.TryGetValue(characterId, out PlayerRuntime runtime) ||
                runtime == null ||
                !_populationByRuntime.TryGetValue(runtime, out population) ||
                population?.Actor == null)
            {
                population = null;
                return false;
            }

            return true;
        }

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
            _population.Added -= MarkPopulationWeaponRosterDirty;
            _population.Removed -= DetachPopulation;

            foreach (PlayerRuntime runtime in _populationByRuntime.Keys)
                runtime.ResourceChanged -= OnPopulationResourceChanged;
        }

        private void DetachPopulation(PopulationActorRuntime pop)
        {
            _ordinaryRangedRosterDirty = true;
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

        private void MarkPopulationWeaponRosterDirty(PopulationActorRuntime _) =>
            _ordinaryRangedRosterDirty = true;

        /// <summary>
        /// Police are the only Population archetype with infinite reserve ammunition.
        /// This refills the existing transient magazine directly from authored compatible
        /// ammo content; it never creates inventory stacks and never touches persistence.
        /// </summary>
        public bool TryRefillPoliceInfiniteReserve(PlayerRuntime runtime)
        {
            if (runtime == null ||
                !_populationByRuntime.TryGetValue(runtime, out PopulationActorRuntime pop) ||
                pop?.Actor == null ||
                pop.NpcType != PopulationNpcType.Police)
            {
                return false;
            }

            PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
            ItemInstanceState mainHand = state?.Equipment?.Get("MainHand");
            if (mainHand == null ||
                !_content.TryGetItem(mainHand.DefinitionId, out ItemDefinition weapon) ||
                !IsFirearmDefinition(weapon) ||
                weapon.firearmMagazineCapacity <= 0 ||
                mainHand.LoadedRounds > 0)
            {
                return false;
            }

            EnsurePopulationWeaponCatalog();
            ItemDefinition ammo = SelectCompatibleAmmo(
                weapon,
                pop.Actor.Handle.actorId,
                0x504F4C494345414DUL); // "POLICEAM" salt
            if (ammo == null)
                return false;

            long nextMagazineRevision = checked(mainHand.MagazineRevision + 1);
            return runtime.TryCommitItemMagazine(
                mainHand.ItemInstanceId,
                mainHand.MagazineRevision,
                ammo.definitionId,
                Math.Max(1, weapon.firearmMagazineCapacity),
                nextMagazineRevision);
        }

        /// <summary>
        /// Non-Police ranged Population carries a finite transient magazine in this pass.
        /// Once empty, remove the transient firearm and let the canonical loadout service
        /// fall back to Unarmed rather than repeatedly publishing predictable NoAmmo rejects.
        /// </summary>
        public bool TryFallbackEmptyPopulationFirearmToUnarmed(PlayerRuntime runtime)
        {
            if (runtime == null ||
                !_populationByRuntime.TryGetValue(runtime, out PopulationActorRuntime pop) ||
                pop?.Actor == null ||
                pop.NpcType == PopulationNpcType.Police)
            {
                return false;
            }

            PlayerItemSystemsRuntimeSnapshot state = runtime.CapturePlayerItemSystems();
            ItemInstanceState mainHand = state?.Equipment?.Get("MainHand");
            if (mainHand == null || mainHand.LoadedRounds > 0 ||
                !_content.TryGetItem(mainHand.DefinitionId, out ItemDefinition weapon) ||
                !IsFirearmDefinition(weapon))
            {
                return false;
            }

            var unarmedEquipment = new EquipmentState(
                checked(state.Equipment.Revision + 1));
            return runtime.TryCommitPlayerItemSystems(
                state.Inventory.Revision,
                state.Equipment.Revision,
                state.Inventory,
                unarmedEquipment,
                state.Stats);
        }

        private PopulationWeaponSelection ResolvePopulationWeaponSelection(PopulationActorRuntime pop)
        {
            if (pop?.Actor == null)
                return default;

            EnsurePopulationWeaponCatalog();
            long actorId = pop.Actor.Handle.actorId;

            // These archetypes are ranged-capable by design. If the active content has no
            // valid firearm+ammo pair, fail safely to an authored melee weapon or unarmed.
            if (pop.NpcType == PopulationNpcType.Police ||
                pop.NpcType == PopulationNpcType.Hunter ||
                pop.NpcType == PopulationNpcType.GangMember)
            {
                PopulationWeaponSelection firearm = SelectFirearm(actorId, 0x52414E474544504FUL);
                if (firearm.HasWeapon)
                    return firearm;
                return SelectMelee(actorId, 0x5350454349414C4DUL);
            }

            if (!IsOrdinaryWeaponEligible(pop))
                return default;

            EnsureOrdinaryRangedRoster();
            if (_ordinaryRangedRoster.Contains(actorId))
            {
                PopulationWeaponSelection firearm = SelectFirearm(actorId, 0x4F5244494E415259UL);
                if (firearm.HasWeapon)
                    return firearm;
            }

            // The current generic melee bucket intentionally reuses existing weapon
            // definitions. No knife/dagger semantic is invented until content needs it.
            ulong meleeRoll = StablePopulationLoadoutScore(actorId, 0x4D454C4545333050UL);
            if ((meleeRoll % 100UL) < 30UL)
                return SelectMelee(actorId, 0x4D454C454553454CUL);

            return default;
        }

        private void EnsureOrdinaryRangedRoster()
        {
            if (!_ordinaryRangedRosterDirty)
                return;

            var scored = new List<KeyValuePair<ulong, long>>();
            foreach (PopulationActorRuntime candidate in _population.All)
            {
                if (!IsOrdinaryWeaponEligible(candidate) || candidate?.Actor == null)
                    continue;
                long actorId = candidate.Actor.Handle.actorId;
                scored.Add(new KeyValuePair<ulong, long>(
                    StablePopulationLoadoutScore(actorId, 0x52414E4745443543UL),
                    actorId));
            }

            scored.Sort((a, b) =>
            {
                int score = a.Key.CompareTo(b.Key);
                return score != 0 ? score : a.Value.CompareTo(b.Value);
            });

            _ordinaryRangedRoster.Clear();
            int maximumRanged = scored.Count / 20; // hard roster cap: <= 5%
            for (int i = 0; i < maximumRanged; ++i)
                _ordinaryRangedRoster.Add(scored[i].Value);
            _ordinaryRangedRosterDirty = false;
        }

        private static bool IsOrdinaryWeaponEligible(PopulationActorRuntime pop)
        {
            if (pop?.Actor == null || pop.SpawnKind == ServerSpawnKind.Monster)
                return false;

            return pop.NpcType == PopulationNpcType.Civilian ||
                   pop.NpcType == PopulationNpcType.Resident ||
                   pop.NpcType == PopulationNpcType.Shopper ||
                   pop.NpcType == PopulationNpcType.Worker ||
                   pop.NpcType == PopulationNpcType.Homeless ||
                   pop.NpcType == PopulationNpcType.Nightlife ||
                   pop.NpcType == PopulationNpcType.Criminal;
        }

        private void EnsurePopulationWeaponCatalog()
        {
            long revision = _content.Revision;
            if (_populationWeaponCatalogRevision == revision)
                return;

            ItemDefinition[] items = _content.GetItems() ?? Array.Empty<ItemDefinition>();
            var ammoLists = new Dictionary<string, List<ItemDefinition>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Length; ++i)
            {
                ItemDefinition item = items[i];
                if (item == null || item.kind != ItemKind.Ammo || string.IsNullOrWhiteSpace(item.ammoFamily))
                    continue;
                string family = item.ammoFamily.Trim();
                if (!ammoLists.TryGetValue(family, out List<ItemDefinition> list))
                {
                    list = new List<ItemDefinition>();
                    ammoLists.Add(family, list);
                }
                list.Add(item);
            }

            _populationAmmoByFamily.Clear();
            foreach (KeyValuePair<string, List<ItemDefinition>> pair in ammoLists)
            {
                pair.Value.Sort(CompareItemDefinitions);
                _populationAmmoByFamily[pair.Key] = pair.Value.ToArray();
            }

            var firearms = new List<ItemDefinition>();
            var melee = new List<ItemDefinition>();
            for (int i = 0; i < items.Length; ++i)
            {
                ItemDefinition item = items[i];
                if (!IsUsableMainHandWeapon(item))
                    continue;

                if (IsFirearmDefinition(item))
                {
                    string family = (item.ammoFamily ?? string.Empty).Trim();
                    if (item.firearmMagazineCapacity > 0 &&
                        family.Length > 0 &&
                        _populationAmmoByFamily.TryGetValue(family, out ItemDefinition[] ammo) &&
                        ammo.Length > 0)
                    {
                        firearms.Add(item);
                    }
                }
                else
                {
                    melee.Add(item);
                }
            }

            firearms.Sort(CompareItemDefinitions);
            melee.Sort(CompareItemDefinitions);
            _populationFirearms = firearms.ToArray();
            _populationMeleeWeapons = melee.ToArray();
            _populationWeaponCatalogRevision = revision;
        }

        private PopulationWeaponSelection SelectFirearm(long actorId, ulong salt)
        {
            if (_populationFirearms.Length == 0)
                return default;
            int index = (int)(StablePopulationLoadoutScore(actorId, salt) % (ulong)_populationFirearms.Length);
            ItemDefinition weapon = _populationFirearms[index];
            ItemDefinition ammo = SelectCompatibleAmmo(weapon, actorId, salt ^ 0xA66A66A66A66A66AUL);
            return ammo == null ? default : new PopulationWeaponSelection(weapon, ammo);
        }

        private PopulationWeaponSelection SelectMelee(long actorId, ulong salt)
        {
            if (_populationMeleeWeapons.Length == 0)
                return default;
            int index = (int)(StablePopulationLoadoutScore(actorId, salt) % (ulong)_populationMeleeWeapons.Length);
            return new PopulationWeaponSelection(_populationMeleeWeapons[index], null);
        }

        private ItemDefinition SelectCompatibleAmmo(ItemDefinition weapon, long actorId, ulong salt)
        {
            if (weapon == null || string.IsNullOrWhiteSpace(weapon.ammoFamily))
                return null;
            string family = weapon.ammoFamily.Trim();
            if (!_populationAmmoByFamily.TryGetValue(family, out ItemDefinition[] ammo) || ammo.Length == 0)
                return null;
            int index = (int)(StablePopulationLoadoutScore(actorId, salt) % (ulong)ammo.Length);
            return ammo[index];
        }

        private static bool IsUsableMainHandWeapon(ItemDefinition item)
        {
            if (item == null ||
                item.kind != ItemKind.Equipment ||
                item.subtype != EquipmentItemSubtype.Weapon ||
                string.IsNullOrWhiteSpace(item.definitionId))
            {
                return false;
            }

            string[] slots = item.allowedEquipmentSlots ?? Array.Empty<string>();
            if (slots.Length == 0)
                return true;
            for (int i = 0; i < slots.Length; ++i)
                if (string.Equals(slots[i], "MainHand", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool IsFirearmDefinition(ItemDefinition item) =>
            item != null &&
            (item.firearmMagazineCapacity > 0 || !string.IsNullOrWhiteSpace(item.ammoFamily));

        private static int CompareItemDefinitions(ItemDefinition a, ItemDefinition b) =>
            string.CompareOrdinal(a?.definitionId ?? string.Empty, b?.definitionId ?? string.Empty);

        private static ulong StablePopulationLoadoutScore(long actorId, ulong salt)
        {
            unchecked
            {
                ulong value = ((ulong)actorId) ^ salt;
                value ^= value >> 30;
                value *= 0xBF58476D1CE4E5B9UL;
                value ^= value >> 27;
                value *= 0x94D049BB133111EBUL;
                value ^= value >> 31;
                return value;
            }
        }

        private static EquipmentState BuildPopulationEquipment(
            long syntheticCharacterId,
            PopulationWeaponSelection loadout)
        {
            if (!loadout.HasWeapon)
                return new EquipmentState(0);

            ItemDefinition weapon = loadout.Weapon;
            bool firearm = IsFirearmDefinition(weapon);
            int loadedRounds = firearm && loadout.Ammo != null
                ? Math.Max(0, weapon.firearmMagazineCapacity)
                : 0;
            string loadedAmmoDefinitionId = loadedRounds > 0
                ? loadout.Ammo.definitionId
                : string.Empty;

            var item = new ItemInstanceState(
                new ItemInstanceId(syntheticCharacterId),
                weapon.definitionId,
                1,
                Math.Max(0, weapon.maxDurability),
                0,
                loadedAmmoDefinitionId,
                loadedRounds,
                0);
            return new EquipmentState(
                0,
                new[] { new EquippedItemState("MainHand", item) });
        }

        private PlayerRuntime CreatePopulationRuntime(PopulationActorRuntime pop)
        {
            int healthMaximum = Math.Max(1, pop.Actor.HealthMaximum);
            PopulationWeaponSelection loadout = ResolvePopulationWeaponSelection(pop);
            PlayerRuntime runtime = CreateRuntime(
                pop.Actor.DisplayName,
                pop.Actor.MapId,
                pop.Actor.InstanceId,
                pop.Actor.Position,
                pop.Actor.YawDegrees,
                BuildPopulationStats(healthMaximum),
                loadout);

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
            StatsState stats) =>
            CreateRuntime(label, mapId, instanceId, position, yawDegrees, stats, default);

        private PlayerRuntime CreateRuntime(
            string label,
            string mapId,
            string instanceId,
            WorldPosition position,
            float yawDegrees,
            StatsState stats,
            PopulationWeaponSelection loadout)
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

            EquipmentState equipment = BuildPopulationEquipment(ordinal, loadout);
            runtime.InitializePlayerItemSystems(new InventoryState(1, 0), equipment, stats);
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
