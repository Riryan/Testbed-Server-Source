using System;
using System.Collections.Generic;
using Game.Shared.Content;
using Game.Shared.Resources;

namespace Game.Server.Application.Content
{
    /// <summary>
    /// Thread-safe immutable-view catalog for the active gameplay content revision.
    /// Semantic ids remain authoring-facing; compact data/wire ids are assigned once by
    /// GameplayContentDataIds and indexed here for authoritative runtime/network use.
    /// </summary>
    public sealed class GameplayContentCatalog
    {
        private readonly object _gate = new object();
        private GameplayContentSnapshot _snapshot;

        private Dictionary<string, ItemDefinition> _itemsByDefinition;
        private Dictionary<ushort, ItemDefinition> _itemsByDataId;
        private Dictionary<string, EquipmentSlotDefinition> _slotsByDefinition;
        private Dictionary<ushort, EquipmentSlotDefinition> _slotsByDataId;
        private Dictionary<CharacterResourceId, CharacterResourceDefinition> _resources;
        private CharacterResourceDefinition[] _resourcesOrdered;

        private MovementRulesDefinition _movement;
        private CombatRulesDefinition _combat;

        private Dictionary<string, DamageTypeDefinition> _damageTypesByDefinition;
        private Dictionary<ushort, DamageTypeDefinition> _damageTypesByWireId;
        private Dictionary<string, AbilityDefinition> _abilitiesByDefinition;
        private Dictionary<ushort, AbilityDefinition> _abilitiesByWireId;
        private AbilityDefinition[] _abilitiesOrdered;
        private Dictionary<string, StatusEffectDefinition> _statusEffectsByDefinition;
        private Dictionary<ushort, StatusEffectDefinition> _statusEffectsByWireId;
        private StatusEffectDefinition[] _statusEffectsOrdered;

        private ProgressionRulesDefinition _progression;
        private Dictionary<ushort, ProgressTrackDefinition> _progressTracksByDataId;
        private Dictionary<string, ProgressTrackDefinition> _progressTracksByDefinition;
        private ProgressTrackDefinition[] _progressTracksOrdered;
        private Dictionary<ushort, RecipeDefinition> _recipesByDataId;
        private Dictionary<string, RecipeDefinition> _recipesByDefinition;
        private RecipeDefinition[] _recipesOrdered;
        private Dictionary<ushort, CraftingStationDefinition> _craftingStationsByDataId;
        private Dictionary<string, CraftingStationDefinition> _craftingStationsByDefinition;
        private Dictionary<ushort, FactionDefinition> _factionsByDataId;
        private Dictionary<string, FactionDefinition> _factionsByDefinition;
        private FactionDefinition[] _factionsOrdered;
        private Dictionary<ushort, JurisdictionDefinition> _jurisdictionsByDataId;
        private Dictionary<ushort, IncidentDefinition> _incidentsByDataId;
        private Dictionary<string, LootTableDefinition> _lootTablesByDefinition;
        private Dictionary<ushort, LootTableDefinition> _lootTablesByDataId;

        // Harvesting Recovery compatibility. Canonical profession authority is ProgressTrackDefinition.
        private Dictionary<string, ProfessionDefinition> _legacyProfessions;
        private Dictionary<ushort, HarvestProfileDefinition> _harvestProfiles;
        private Dictionary<string, HarvestProfileDefinition> _harvestProfilesByDefinition;

        public GameplayContentCatalog(GameplayContentSnapshot initial)
        {
            Replace(initial);
        }

        public long Revision
        {
            get { lock (_gate) return _snapshot.revision; }
        }

        public GameplayContentSnapshot Snapshot
        {
            get { lock (_gate) return _snapshot; }
        }

        public bool Replace(GameplayContentSnapshot snapshot)
        {
            if (!GameplayContentValidation.TryValidate(snapshot, out string error))
                throw new ArgumentException(error, nameof(snapshot));

            GameplayContentDataIds.EnsureAssigned(snapshot);

            var itemsByDefinition = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            var itemsByDataId = new Dictionary<ushort, ItemDefinition>();
            ItemDefinition[] itemArray = snapshot.items ?? Array.Empty<ItemDefinition>();
            for (int i = 0; i < itemArray.Length; ++i)
            {
                ItemDefinition value = itemArray[i];
                if (value == null) continue;
                itemsByDefinition.Add(value.definitionId, value);
                if (value.dataId != 0) itemsByDataId.Add(value.dataId, value);
            }

            var slotsByDefinition = new Dictionary<string, EquipmentSlotDefinition>(StringComparer.Ordinal);
            var slotsByDataId = new Dictionary<ushort, EquipmentSlotDefinition>();
            EquipmentSlotDefinition[] slotArray = snapshot.equipmentSlots ?? Array.Empty<EquipmentSlotDefinition>();
            for (int i = 0; i < slotArray.Length; ++i)
            {
                EquipmentSlotDefinition value = slotArray[i];
                if (value == null) continue;
                slotsByDefinition.Add(value.slotId, value);
                if (value.dataId != 0) slotsByDataId.Add(value.dataId, value);
            }

            var resources = new Dictionary<CharacterResourceId, CharacterResourceDefinition>();
            CharacterResourceDefinition[] resourceArray = snapshot.resources ?? Array.Empty<CharacterResourceDefinition>();
            var resourcesOrdered = new CharacterResourceDefinition[resourceArray.Length];
            for (int i = 0; i < resourceArray.Length; ++i)
            {
                CharacterResourceDefinition value = resourceArray[i];
                resources.Add(value.id, value);
                resourcesOrdered[i] = value;
            }
            Array.Sort(resourcesOrdered, (a, b) => ((ushort)a.id).CompareTo((ushort)b.id));

            var statusByDefinition = new Dictionary<string, StatusEffectDefinition>(StringComparer.Ordinal);
            var statusByWireId = new Dictionary<ushort, StatusEffectDefinition>();
            StatusEffectDefinition[] statusArray = snapshot.statusEffects ?? Array.Empty<StatusEffectDefinition>();
            var statusOrdered = new StatusEffectDefinition[statusArray.Length];
            for (int i = 0; i < statusArray.Length; ++i)
            {
                StatusEffectDefinition value = statusArray[i];
                statusByDefinition.Add(value.definitionId, value);
                if (value.wireId != 0) statusByWireId.Add(value.wireId, value);
                statusOrdered[i] = value;
            }
            Array.Sort(statusOrdered, (a, b) => a.wireId != b.wireId ? a.wireId.CompareTo(b.wireId) : string.CompareOrdinal(a.definitionId, b.definitionId));

            var abilitiesByDefinition = new Dictionary<string, AbilityDefinition>(StringComparer.Ordinal);
            var abilitiesByWireId = new Dictionary<ushort, AbilityDefinition>();
            AbilityDefinition[] abilityArray = snapshot.abilities ?? Array.Empty<AbilityDefinition>();
            var abilitiesOrdered = new AbilityDefinition[abilityArray.Length];
            for (int i = 0; i < abilityArray.Length; ++i)
            {
                AbilityDefinition value = abilityArray[i];
                abilitiesByDefinition.Add(value.definitionId, value);
                if (value.wireId != 0) abilitiesByWireId.Add(value.wireId, value);
                abilitiesOrdered[i] = value;
            }
            Array.Sort(abilitiesOrdered, (a, b) => a.wireId != b.wireId ? a.wireId.CompareTo(b.wireId) : string.CompareOrdinal(a.definitionId, b.definitionId));

            var damageByDefinition = new Dictionary<string, DamageTypeDefinition>(StringComparer.Ordinal);
            var damageByWireId = new Dictionary<ushort, DamageTypeDefinition>();
            DamageTypeDefinition[] damageArray = snapshot.damageTypes ?? Array.Empty<DamageTypeDefinition>();
            for (int i = 0; i < damageArray.Length; ++i)
            {
                DamageTypeDefinition value = damageArray[i];
                if (value == null) continue;
                damageByDefinition.Add(value.definitionId, value);
                if (value.wireId != 0) damageByWireId.Add(value.wireId, value);
            }

            var tracksByDataId = new Dictionary<ushort, ProgressTrackDefinition>();
            var tracksByDefinition = new Dictionary<string, ProgressTrackDefinition>(StringComparer.Ordinal);
            ProgressTrackDefinition[] tracks = snapshot.progressTracks ?? Array.Empty<ProgressTrackDefinition>();
            var tracksOrdered = new ProgressTrackDefinition[tracks.Length];
            for (int i = 0; i < tracks.Length; ++i)
            {
                ProgressTrackDefinition value = tracks[i];
                if (value == null) continue;
                tracksByDefinition.Add(value.definitionId, value);
                if (value.dataId != 0) tracksByDataId.Add(value.dataId, value);
                tracksOrdered[i] = value;
            }
            Array.Sort(tracksOrdered, CompareProgressTracks);

            var recipesByDataId = new Dictionary<ushort, RecipeDefinition>();
            var recipesByDefinition = new Dictionary<string, RecipeDefinition>(StringComparer.Ordinal);
            RecipeDefinition[] recipes = snapshot.recipes ?? Array.Empty<RecipeDefinition>();
            var recipesOrdered = new RecipeDefinition[recipes.Length];
            for (int i = 0; i < recipes.Length; ++i)
            {
                RecipeDefinition value = recipes[i];
                if (value == null) continue;
                recipesByDefinition.Add(value.definitionId, value);
                if (value.dataId != 0) recipesByDataId.Add(value.dataId, value);
                recipesOrdered[i] = value;
            }
            Array.Sort(recipesOrdered, CompareRecipes);

            var stationsByDataId = new Dictionary<ushort, CraftingStationDefinition>();
            var stationsByDefinition = new Dictionary<string, CraftingStationDefinition>(StringComparer.Ordinal);
            CraftingStationDefinition[] stations = snapshot.craftingStations ?? Array.Empty<CraftingStationDefinition>();
            for (int i = 0; i < stations.Length; ++i)
            {
                CraftingStationDefinition value = stations[i];
                if (value == null) continue;
                stationsByDefinition.Add(value.definitionId, value);
                if (value.dataId != 0) stationsByDataId.Add(value.dataId, value);
            }

            var factionsByDataId = new Dictionary<ushort, FactionDefinition>();
            var factionsByDefinition = new Dictionary<string, FactionDefinition>(StringComparer.Ordinal);
            FactionDefinition[] factions = snapshot.factions ?? Array.Empty<FactionDefinition>();
            var factionsOrdered = new FactionDefinition[factions.Length];
            for (int i = 0; i < factions.Length; ++i)
            {
                FactionDefinition value = factions[i];
                if (value == null) continue;
                factionsByDefinition.Add(value.definitionId, value);
                if (value.dataId != 0) factionsByDataId.Add(value.dataId, value);
                factionsOrdered[i] = value;
            }
            Array.Sort(factionsOrdered, CompareFactions);

            var jurisdictions = new Dictionary<ushort, JurisdictionDefinition>();
            JurisdictionDefinition[] jurisdictionArray = snapshot.jurisdictions ?? Array.Empty<JurisdictionDefinition>();
            for (int i = 0; i < jurisdictionArray.Length; ++i)
                if (jurisdictionArray[i] != null && jurisdictionArray[i].dataId != 0)
                    jurisdictions.Add(jurisdictionArray[i].dataId, jurisdictionArray[i]);

            var incidents = new Dictionary<ushort, IncidentDefinition>();
            IncidentDefinition[] incidentArray = snapshot.incidents ?? Array.Empty<IncidentDefinition>();
            for (int i = 0; i < incidentArray.Length; ++i)
                if (incidentArray[i] != null && incidentArray[i].dataId != 0)
                    incidents.Add(incidentArray[i].dataId, incidentArray[i]);

            var lootByDefinition = new Dictionary<string, LootTableDefinition>(StringComparer.Ordinal);
            var lootByDataId = new Dictionary<ushort, LootTableDefinition>();
            LootTableDefinition[] loot = snapshot.lootTables ?? Array.Empty<LootTableDefinition>();
            for (int i = 0; i < loot.Length; ++i)
            {
                LootTableDefinition value = loot[i];
                if (value == null) continue;
                lootByDefinition.Add(value.definitionId, value);
                if (value.dataId != 0) lootByDataId.Add(value.dataId, value);
            }

            var legacyProfessions = new Dictionary<string, ProfessionDefinition>(StringComparer.Ordinal);
            ProfessionDefinition[] professionArray = snapshot.professions ?? Array.Empty<ProfessionDefinition>();
            for (int i = 0; i < professionArray.Length; ++i)
                if (professionArray[i] != null)
                    legacyProfessions.Add(professionArray[i].definitionId, professionArray[i]);

            var harvestProfiles = new Dictionary<ushort, HarvestProfileDefinition>();
            var harvestProfilesByDefinition = new Dictionary<string, HarvestProfileDefinition>(StringComparer.Ordinal);
            HarvestProfileDefinition[] harvestArray = snapshot.harvestProfiles ?? Array.Empty<HarvestProfileDefinition>();
            for (int i = 0; i < harvestArray.Length; ++i)
            {
                HarvestProfileDefinition value = harvestArray[i];
                if (value == null || value.profileId == 0) continue;
                harvestProfiles.Add(value.profileId, value);
                harvestProfilesByDefinition.Add(value.definitionId, value);
            }

            lock (_gate)
            {
                if (_snapshot != null && snapshot.revision < _snapshot.revision)
                    return false;

                _snapshot = snapshot;
                _itemsByDefinition = itemsByDefinition;
                _itemsByDataId = itemsByDataId;
                _slotsByDefinition = slotsByDefinition;
                _slotsByDataId = slotsByDataId;
                _resources = resources;
                _resourcesOrdered = resourcesOrdered;
                _movement = snapshot.movement ?? new MovementRulesDefinition();
                _combat = snapshot.combat ?? new CombatRulesDefinition();
                _damageTypesByDefinition = damageByDefinition;
                _damageTypesByWireId = damageByWireId;
                _abilitiesByDefinition = abilitiesByDefinition;
                _abilitiesByWireId = abilitiesByWireId;
                _abilitiesOrdered = abilitiesOrdered;
                _statusEffectsByDefinition = statusByDefinition;
                _statusEffectsByWireId = statusByWireId;
                _statusEffectsOrdered = statusOrdered;
                _progression = snapshot.progression ?? new ProgressionRulesDefinition();
                _progressTracksByDataId = tracksByDataId;
                _progressTracksByDefinition = tracksByDefinition;
                _progressTracksOrdered = tracksOrdered;
                _recipesByDataId = recipesByDataId;
                _recipesByDefinition = recipesByDefinition;
                _recipesOrdered = recipesOrdered;
                _craftingStationsByDataId = stationsByDataId;
                _craftingStationsByDefinition = stationsByDefinition;
                _factionsByDataId = factionsByDataId;
                _factionsByDefinition = factionsByDefinition;
                _factionsOrdered = factionsOrdered;
                _jurisdictionsByDataId = jurisdictions;
                _incidentsByDataId = incidents;
                _lootTablesByDefinition = lootByDefinition;
                _lootTablesByDataId = lootByDataId;
                _legacyProfessions = legacyProfessions;
                _harvestProfiles = harvestProfiles;
                _harvestProfilesByDefinition = harvestProfilesByDefinition;
                return true;
            }
        }

        public bool TryGetItem(string definitionId, out ItemDefinition definition)
        {
            lock (_gate) return _itemsByDefinition.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public bool TryGetItem(ushort dataId, out ItemDefinition definition)
        {
            lock (_gate)
            {
                if (dataId == 0) { definition = null; return false; }
                return _itemsByDataId.TryGetValue(dataId, out definition);
            }
        }

        public bool TryGetEquipmentSlot(string slotId, out EquipmentSlotDefinition definition)
        {
            lock (_gate) return _slotsByDefinition.TryGetValue(slotId ?? string.Empty, out definition);
        }

        public bool TryGetEquipmentSlot(ushort dataId, out EquipmentSlotDefinition definition)
        {
            lock (_gate)
            {
                if (dataId == 0) { definition = null; return false; }
                return _slotsByDataId.TryGetValue(dataId, out definition);
            }
        }

        public bool TryGetResource(CharacterResourceId resourceId, out CharacterResourceDefinition definition)
        {
            lock (_gate) return _resources.TryGetValue(resourceId, out definition);
        }

        public CharacterResourceDefinition[] GetResources()
        {
            lock (_gate) return _resourcesOrdered;
        }

        public MovementRulesDefinition GetMovementRules()
        {
            lock (_gate) return _movement;
        }

        public CombatRulesDefinition GetCombatRules()
        {
            lock (_gate) return _combat;
        }

        public bool TryGetDamageType(string definitionId, out DamageTypeDefinition definition)
        {
            lock (_gate) return _damageTypesByDefinition.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public bool TryGetDamageType(ushort wireId, out DamageTypeDefinition definition)
        {
            lock (_gate)
            {
                if (wireId == 0) { definition = null; return false; }
                return _damageTypesByWireId.TryGetValue(wireId, out definition);
            }
        }

        public DamageResponseAggregate GetGlobalDamageResponse(ushort damageTypeId)
        {
            lock (_gate)
            {
                DamageResponseRuleDefinition[] responses = _combat?.damageResponses ?? Array.Empty<DamageResponseRuleDefinition>();
                float multiplier = 1f;
                float flat = 0f;
                for (int i = 0; i < responses.Length; ++i)
                {
                    DamageResponseRuleDefinition response = responses[i];
                    if (response == null || !string.IsNullOrWhiteSpace(response.targetTag)) continue;
                    if (response.damageTypeId != 0 && response.damageTypeId != damageTypeId) continue;
                    multiplier *= response.multiplier;
                    flat += response.flatAdjustment;
                }
                return new DamageResponseAggregate(multiplier, flat);
            }
        }

        public bool TryGetAbility(string definitionId, out AbilityDefinition definition)
        {
            lock (_gate) return _abilitiesByDefinition.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public bool TryGetAbility(ushort wireId, out AbilityDefinition definition)
        {
            lock (_gate)
            {
                if (wireId == 0) { definition = null; return false; }
                return _abilitiesByWireId.TryGetValue(wireId, out definition);
            }
        }

        public AbilityDefinition[] GetAbilities()
        {
            lock (_gate) return _abilitiesOrdered;
        }

        public bool TryGetStatusEffect(string definitionId, out StatusEffectDefinition definition)
        {
            lock (_gate) return _statusEffectsByDefinition.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public bool TryGetStatusEffect(ushort wireId, out StatusEffectDefinition definition)
        {
            lock (_gate)
            {
                if (wireId == 0) { definition = null; return false; }
                return _statusEffectsByWireId.TryGetValue(wireId, out definition);
            }
        }

        public StatusEffectDefinition[] GetStatusEffects()
        {
            lock (_gate) return _statusEffectsOrdered;
        }

        public ProgressionRulesDefinition GetProgressionRules()
        {
            lock (_gate) return _progression;
        }

        public bool TryGetProgressTrack(ushort dataId, out ProgressTrackDefinition definition)
        {
            lock (_gate)
            {
                if (dataId == 0) { definition = null; return false; }
                return _progressTracksByDataId.TryGetValue(dataId, out definition);
            }
        }

        public bool TryGetProgressTrack(string definitionId, out ProgressTrackDefinition definition)
        {
            lock (_gate) return _progressTracksByDefinition.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public ProgressTrackDefinition[] GetProgressTracks()
        {
            lock (_gate) return _progressTracksOrdered;
        }

        public bool TryGetRecipe(ushort dataId, out RecipeDefinition definition)
        {
            lock (_gate)
            {
                if (dataId == 0) { definition = null; return false; }
                return _recipesByDataId.TryGetValue(dataId, out definition);
            }
        }

        public bool TryGetRecipe(string definitionId, out RecipeDefinition definition)
        {
            lock (_gate) return _recipesByDefinition.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public RecipeDefinition[] GetRecipes()
        {
            lock (_gate) return _recipesOrdered;
        }

        public bool TryGetCraftingStation(ushort dataId, out CraftingStationDefinition definition)
        {
            lock (_gate)
            {
                if (dataId == 0) { definition = null; return false; }
                return _craftingStationsByDataId.TryGetValue(dataId, out definition);
            }
        }

        public bool TryGetCraftingStation(string definitionId, out CraftingStationDefinition definition)
        {
            lock (_gate) return _craftingStationsByDefinition.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public bool TryGetFaction(ushort dataId, out FactionDefinition definition)
        {
            lock (_gate)
            {
                if (dataId == 0) { definition = null; return false; }
                return _factionsByDataId.TryGetValue(dataId, out definition);
            }
        }

        public bool TryGetFaction(string definitionId, out FactionDefinition definition)
        {
            lock (_gate) return _factionsByDefinition.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public FactionDefinition[] GetFactions()
        {
            lock (_gate) return _factionsOrdered;
        }

        public bool TryGetJurisdiction(ushort dataId, out JurisdictionDefinition definition)
        {
            lock (_gate)
            {
                if (dataId == 0) { definition = null; return false; }
                return _jurisdictionsByDataId.TryGetValue(dataId, out definition);
            }
        }

        public bool TryGetIncident(ushort dataId, out IncidentDefinition definition)
        {
            lock (_gate)
            {
                if (dataId == 0) { definition = null; return false; }
                return _incidentsByDataId.TryGetValue(dataId, out definition);
            }
        }

        public bool TryGetLootTable(string definitionId, out LootTableDefinition definition)
        {
            lock (_gate) return _lootTablesByDefinition.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public bool TryGetLootTable(ushort dataId, out LootTableDefinition definition)
        {
            lock (_gate)
            {
                if (dataId == 0) { definition = null; return false; }
                return _lootTablesByDataId.TryGetValue(dataId, out definition);
            }
        }

        public bool TryGetProfession(string definitionId, out ProfessionDefinition definition)
        {
            lock (_gate) return _legacyProfessions.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public bool TryGetHarvestProfile(ushort profileId, out HarvestProfileDefinition definition)
        {
            lock (_gate)
            {
                if (profileId == 0) { definition = null; return false; }
                return _harvestProfiles.TryGetValue(profileId, out definition);
            }
        }

        public bool TryGetHarvestProfile(string definitionId, out HarvestProfileDefinition definition)
        {
            definitionId = (definitionId ?? string.Empty).Trim();
            lock (_gate)
            {
                if (_harvestProfilesByDefinition.TryGetValue(definitionId, out definition))
                    return true;

                // Recovery authoring briefly used a "harvest." world-profile prefix.
                if (definitionId.StartsWith("harvest.", StringComparison.Ordinal) &&
                    _harvestProfilesByDefinition.TryGetValue(definitionId.Substring("harvest.".Length), out definition))
                    return true;

                definition = null;
                return false;
            }
        }

        public ItemDefinition[] GetItems()
        {
            lock (_gate)
            {
                var result = new ItemDefinition[_itemsByDefinition.Count];
                int i = 0;
                foreach (ItemDefinition item in _itemsByDefinition.Values) result[i++] = item;
                Array.Sort(result, (a, b) => a.dataId != b.dataId ? a.dataId.CompareTo(b.dataId) : string.CompareOrdinal(a.definitionId, b.definitionId));
                return result;
            }
        }

        public EquipmentSlotDefinition[] GetEquipmentSlotsOrdered()
        {
            lock (_gate)
            {
                var result = new EquipmentSlotDefinition[_slotsByDefinition.Count];
                int i = 0;
                foreach (EquipmentSlotDefinition slot in _slotsByDefinition.Values) result[i++] = slot;
                Array.Sort(result, (a, b) => a.order != b.order ? a.order.CompareTo(b.order) : string.CompareOrdinal(a.slotId, b.slotId));
                return result;
            }
        }

        private static int CompareProgressTracks(ProgressTrackDefinition a, ProgressTrackDefinition b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            int kind = a.kind.CompareTo(b.kind);
            if (kind != 0) return kind;
            return a.dataId != b.dataId ? a.dataId.CompareTo(b.dataId) : string.CompareOrdinal(a.definitionId, b.definitionId);
        }

        private static int CompareRecipes(RecipeDefinition a, RecipeDefinition b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            return a.dataId != b.dataId ? a.dataId.CompareTo(b.dataId) : string.CompareOrdinal(a.definitionId, b.definitionId);
        }

        private static int CompareFactions(FactionDefinition a, FactionDefinition b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            return a.dataId != b.dataId ? a.dataId.CompareTo(b.dataId) : string.CompareOrdinal(a.definitionId, b.definitionId);
        }
    }
}
