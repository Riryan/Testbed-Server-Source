using System;
using System.Collections.Generic;
using Game.Server.Application.Content;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Stats;
using Game.Shared.Content;
using Game.Shared.Combat;

namespace Game.Server.Application.Items
{
    /// <summary>
    /// Deterministic authoritative stat rebuild from the active immutable content snapshot
    /// and equipped item instances. Client-provided stat totals are never consumed.
    /// </summary>
    public static class EquipmentStatCalculator
    {
        public static StatsState Calculate(GameplayContentCatalog content, EquipmentState equipment)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));

            var baseValues = new Dictionary<string, float>(StringComparer.Ordinal);
            KeyValuePair<string, float>[] defaultStats = StatsState.DefaultCharacter().Snapshot();
            for (int i = 0; i < defaultStats.Length; ++i)
                baseValues[defaultStats[i].Key] = defaultStats[i].Value;
            var additive = new Dictionary<string, float>(StringComparer.Ordinal);
            var multiplier = new Dictionary<string, float>(StringComparer.Ordinal);

            EquippedItemState[] items = equipment?.Snapshot() ?? Array.Empty<EquippedItemState>();
            for (int i = 0; i < items.Length; ++i)
            {
                if (!content.TryGetItem(items[i].Item.DefinitionId, out ItemDefinition definition))
                    throw new InvalidOperationException($"Equipped item definition '{items[i].Item.DefinitionId}' is unavailable.");

                StatModifierDefinition[] modifiers = definition.statModifiers ?? Array.Empty<StatModifierDefinition>();
                for (int j = 0; j < modifiers.Length; ++j)
                {
                    StatModifierDefinition modifier = modifiers[j];
                    if (modifier == null || string.IsNullOrWhiteSpace(modifier.statId)) continue;
                    additive[modifier.statId] = additive.TryGetValue(modifier.statId, out float a)
                        ? a + modifier.additive
                        : modifier.additive;
                    multiplier[modifier.statId] = multiplier.TryGetValue(modifier.statId, out float m)
                        ? m * modifier.multiplier
                        : modifier.multiplier;
                }
            }

            // Precompute material/equipment weakness/resistance rules on equipment changes.
            // This keeps the per-hit combat path allocation-free and tag-scan-free.
            CombatRulesDefinition combat = content.GetCombatRules() ?? new CombatRulesDefinition();
            DamageResponseRuleDefinition[] responses = combat.damageResponses ?? Array.Empty<DamageResponseRuleDefinition>();
            for (int i = 0; i < items.Length; ++i)
            {
                if (!content.TryGetItem(items[i].Item.DefinitionId, out ItemDefinition definition))
                    continue;
                string[] tags = definition.tags ?? Array.Empty<string>();
                for (int r = 0; r < responses.Length; ++r)
                {
                    DamageResponseRuleDefinition response = responses[r];
                    if (response == null || string.IsNullOrWhiteSpace(response.targetTag) || !ContainsTag(tags, response.targetTag))
                        continue;
                    string multiplierId = CombatDerivedStatIds.DamageResponseMultiplier(response.damageTypeId);
                    string flatId = CombatDerivedStatIds.DamageResponseFlat(response.damageTypeId);
                    if (!baseValues.ContainsKey(multiplierId))
                        baseValues[multiplierId] = 1f;
                    multiplier[multiplierId] = multiplier.TryGetValue(multiplierId, out float oldMultiplier)
                        ? oldMultiplier * response.multiplier
                        : response.multiplier;
                    additive[flatId] = additive.TryGetValue(flatId, out float oldFlat)
                        ? oldFlat + response.flatAdjustment
                        : response.flatAdjustment;
                }
            }

            var ids = new HashSet<string>(baseValues.Keys, StringComparer.Ordinal);
            ids.UnionWith(additive.Keys);
            ids.UnionWith(multiplier.Keys);
            var final = new List<KeyValuePair<string, float>>(ids.Count);
            foreach (string id in ids)
            {
                float baseValue = baseValues.TryGetValue(id, out float b) ? b : 0f;
                float add = additive.TryGetValue(id, out float a) ? a : 0f;
                float mult = multiplier.TryGetValue(id, out float m) ? m : 1f;
                final.Add(new KeyValuePair<string, float>(id, (baseValue + add) * mult));
            }
            return new StatsState(final);
        }

        private static bool ContainsTag(string[] tags, string target)
        {
            for (int i = 0; i < tags.Length; ++i)
                if (string.Equals(tags[i], target, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
