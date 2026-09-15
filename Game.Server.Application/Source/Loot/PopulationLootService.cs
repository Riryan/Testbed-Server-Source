using System;
using System.Collections.Generic;
using Game.Server.Application.Actors;
using Game.Server.Application.Content;
using Game.Server.Application.Interactions;
using Game.Server.Application.Population;
using Game.Server.Domain.Players;
using Game.Shared.Actors;
using Game.Shared.Content;

namespace Game.Server.Application.Loot
{
    /// <summary>
    /// Canonical carried/death-loot owner for Population. The immutable LootTable is rolled
    /// at most once per Population instance. Future Pickpocket can remove entries from this
    /// same runtime pool; death exposes only what remains through the existing WorldLoot path.
    /// </summary>
    public sealed class PopulationLootService : IDisposable
    {
        private readonly GameplayContentCatalog _content;
        private readonly PopulationSimulationService _population;
        private readonly WorldInteractableService _worldObjects;
        private readonly Dictionary<long, WorldLootRuntimeEntry[]> _carriedLoot =
            new Dictionary<long, WorldLootRuntimeEntry[]>();
        private readonly Dictionary<long, WorldInteractableRuntime> _deathSources =
            new Dictionary<long, WorldInteractableRuntime>();
        private readonly Dictionary<long, long> _populationByDeathSourceStableId =
            new Dictionary<long, long>();
        private readonly HashSet<long> _resolved = new HashSet<long>();

        /// <summary>
        /// Rare public-state invalidation for client interaction filtering. No loot contents are
        /// exposed here; observers only need to know when the public Loot action availability changed.
        /// </summary>
        public event Action<long> PublicInteractionStateChanged;

        public PopulationLootService(
            GameplayContentCatalog content,
            PopulationSimulationService population,
            WorldInteractableService worldObjects)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _population = population ?? throw new ArgumentNullException(nameof(population));
            _worldObjects = worldObjects ?? throw new ArgumentNullException(nameof(worldObjects));
            _population.Changed += OnPopulationChanged;
            _population.Removed += OnPopulationRemoved;
            _worldObjects.RuntimeChanged += OnWorldInteractableRuntimeChanged;
        }

        public bool CanLoot(AuthoritativeActorRuntime actor)
        {
            if (actor == null || actor.Alive || actor.HealthCurrent > 0)
                return false;
            if (!_population.TryGet(actor.Handle.actorId, out PopulationActorRuntime pop) || pop == null)
                return false;

            EnsureDeathLoot(pop);
            return _deathSources.TryGetValue(actor.Handle.actorId, out WorldInteractableRuntime source) &&
                   source != null &&
                   !source.Depleted;
        }

        public bool TryOpenDeadLoot(
            PlayerRuntime sourcePlayer,
            AuthoritativeActorRuntime actor,
            out WorldInteractableRuntime lootSource,
            out string reason)
        {
            lootSource = null;
            reason = string.Empty;
            if (sourcePlayer == null || actor == null)
            {
                reason = "loot target is unavailable";
                return false;
            }
            if (actor.Alive || actor.HealthCurrent > 0)
            {
                reason = "population target is not dead";
                return false;
            }
            if (!_population.TryGet(actor.Handle.actorId, out PopulationActorRuntime pop) || pop == null)
            {
                reason = "population target is unavailable";
                return false;
            }

            EnsureDeathLoot(pop);
            if (!_deathSources.TryGetValue(actor.Handle.actorId, out WorldInteractableRuntime runtime) ||
                runtime == null ||
                runtime.Depleted)
            {
                reason = "population target has no loot";
                return false;
            }

            if (!_worldObjects.TryOpenLoot(sourcePlayer, runtime.Key.StableId, out lootSource, out reason))
                return false;
            return true;
        }

        /// <summary>
        /// Removes quantity from the canonical carried-loot pool without rerolling. This is
        /// intentionally ready for the later humanoid-only Pickpocket interaction.
        /// </summary>
        public bool TryConsumeCarriedLoot(long populationActorId, int entryIndex, int quantity)
        {
            if (populationActorId <= 0 || quantity < 1)
                return false;

            if (!_carriedLoot.TryGetValue(populationActorId, out WorldLootRuntimeEntry[] entries))
            {
                if (!_population.TryGet(populationActorId, out PopulationActorRuntime pop) || pop == null)
                    return false;
                EnsureCarriedLoot(pop);
                if (!_carriedLoot.TryGetValue(populationActorId, out entries))
                    return false;
            }

            bool hadLoot = HasPositiveLoot(entries);
            for (int i = 0; i < entries.Length; ++i)
            {
                WorldLootRuntimeEntry entry = entries[i];
                if (entry == null || entry.EntryIndex != entryIndex || entry.Quantity < quantity)
                    continue;
                entry.Quantity -= quantity;
                if (hadLoot != HasPositiveLoot(entries))
                    PublicInteractionStateChanged?.Invoke(populationActorId);
                return true;
            }
            return false;
        }

        private void OnPopulationChanged(PopulationActorRuntime pop)
        {
            if (pop?.Actor == null || pop.Actor.Alive || pop.Actor.HealthCurrent > 0)
                return;
            EnsureDeathLoot(pop);
        }

        private void EnsureDeathLoot(PopulationActorRuntime pop)
        {
            if (pop?.Actor == null)
                return;

            long actorId = pop.Actor.Handle.actorId;
            if (_deathSources.ContainsKey(actorId))
                return;

            LootTableDefinition table = EnsureCarriedLoot(pop);
            if (table == null || !_carriedLoot.TryGetValue(actorId, out WorldLootRuntimeEntry[] entries))
                return;

            bool any = false;
            for (int i = 0; i < entries.Length; ++i)
            {
                if (entries[i] != null && entries[i].Quantity > 0)
                {
                    any = true;
                    break;
                }
            }
            if (!any)
                return;

            WorldInteractableRuntime source = _worldObjects.CreateTransientLootSource(
                pop.Actor.MapId,
                pop.Actor.InstanceId,
                string.IsNullOrWhiteSpace(pop.Actor.DisplayName) ? "Loot" : pop.Actor.DisplayName,
                pop.Actor.Position,
                table.definitionId,
                entries);
            _deathSources[actorId] = source;
            _populationByDeathSourceStableId[source.Key.StableId] = actorId;
        }

        private void OnWorldInteractableRuntimeChanged(WorldInteractableRuntime runtime)
        {
            if (runtime == null ||
                !_populationByDeathSourceStableId.TryGetValue(runtime.Key.StableId, out long actorId))
            {
                return;
            }

            PublicInteractionStateChanged?.Invoke(actorId);
        }

        private static bool HasPositiveLoot(WorldLootRuntimeEntry[] entries)
        {
            entries ??= Array.Empty<WorldLootRuntimeEntry>();
            for (int i = 0; i < entries.Length; ++i)
                if (entries[i] != null && entries[i].Quantity > 0)
                    return true;
            return false;
        }

        private LootTableDefinition EnsureCarriedLoot(PopulationActorRuntime pop)
        {
            if (pop?.Actor == null)
                return null;

            long actorId = pop.Actor.Handle.actorId;
            string tableId = pop.DeathLootTableId;
            if (_resolved.Contains(actorId))
            {
                return !string.IsNullOrWhiteSpace(tableId) && _content.TryGetLootTable(tableId, out LootTableDefinition existing)
                    ? existing
                    : null;
            }
            _resolved.Add(actorId);

            if (string.IsNullOrWhiteSpace(tableId) ||
                !_content.TryGetLootTable(tableId, out LootTableDefinition table) ||
                table == null)
            {
                _carriedLoot[actorId] = Array.Empty<WorldLootRuntimeEntry>();
                return null;
            }

            Random random = pop.Random ?? new Random(unchecked((int)(actorId ^ (actorId >> 32))));
            if (!LootTableResolver.TryRollAll(
                    table,
                    random.NextDouble,
                    null,
                    out LootRollResult[] rolls,
                    out _))
            {
                _carriedLoot[actorId] = Array.Empty<WorldLootRuntimeEntry>();
                return table;
            }

            _carriedLoot[actorId] = BuildRuntimeEntries(table, rolls);
            return table;
        }

        private void OnPopulationRemoved(PopulationActorRuntime pop)
        {
            long actorId = pop?.Actor?.Handle.actorId ?? 0;
            if (actorId <= 0)
                return;

            if (_deathSources.TryGetValue(actorId, out WorldInteractableRuntime source))
            {
                _populationByDeathSourceStableId.Remove(source.Key.StableId);
                _worldObjects.RemoveTransient(source);
                _deathSources.Remove(actorId);
            }
            _carriedLoot.Remove(actorId);
            _resolved.Remove(actorId);
        }

        private static WorldLootRuntimeEntry[] BuildRuntimeEntries(
            LootTableDefinition table,
            LootRollResult[] rolls)
        {
            rolls ??= Array.Empty<LootRollResult>();
            LootTableEntryDefinition[] definitions = table?.entries ?? Array.Empty<LootTableEntryDefinition>();
            var quantities = new Dictionary<int, int>();
            for (int i = 0; i < rolls.Length; ++i)
            {
                LootRollResult roll = rolls[i];
                if (roll.EntryIndex < 0 || roll.EntryIndex >= definitions.Length || roll.Quantity < 1)
                    continue;
                quantities[roll.EntryIndex] =
                    checked((quantities.TryGetValue(roll.EntryIndex, out int existing) ? existing : 0) + roll.Quantity);
            }

            var result = new List<WorldLootRuntimeEntry>(quantities.Count);
            foreach (KeyValuePair<int, int> pair in quantities)
            {
                LootTableEntryDefinition definition = definitions[pair.Key];
                if (definition != null && definition.itemDataId != 0 && pair.Value > 0)
                    result.Add(new WorldLootRuntimeEntry(pair.Key, definition, pair.Value));
            }
            result.Sort((a, b) => a.EntryIndex.CompareTo(b.EntryIndex));
            return result.ToArray();
        }


        public void Dispose()
        {
            _population.Changed -= OnPopulationChanged;
            _population.Removed -= OnPopulationRemoved;
            _worldObjects.RuntimeChanged -= OnWorldInteractableRuntimeChanged;
            foreach (WorldInteractableRuntime source in _deathSources.Values)
                _worldObjects.RemoveTransient(source);
            _populationByDeathSourceStableId.Clear();
            _deathSources.Clear();
            _carriedLoot.Clear();
            _resolved.Clear();
            PublicInteractionStateChanged = null;
        }
    }
}
