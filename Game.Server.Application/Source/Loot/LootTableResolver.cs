using System;
using System.Collections.Generic;
using Game.Shared.Content;

namespace Game.Server.Application.Loot
{
    public readonly struct LootRollResult
    {
        public int EntryIndex { get; }
        public ushort ItemDataId { get; }
        public string ItemDefinitionId { get; }
        public int Quantity { get; }

        public LootRollResult(int entryIndex, ushort itemDataId, string itemDefinitionId, int quantity)
        {
            EntryIndex = entryIndex;
            ItemDataId = itemDataId;
            ItemDefinitionId = itemDefinitionId ?? string.Empty;
            Quantity = quantity;
        }
    }

    /// <summary>
    /// Canonical authoritative loot resolver. Roll semantics are explicit on the table so
    /// searchable containers and weighted reward pools no longer overload the same chance
    /// field with different meanings. Random samples are supplied by the caller, keeping
    /// resolution deterministic and testable. Loot-table data never needs to cross the wire.
    /// </summary>
    public static class LootTableResolver
    {
        public static bool TryRollAll(
            LootTableDefinition table,
            Func<double> random01,
            Func<LootTableEntryDefinition, bool> predicate,
            out LootRollResult[] results,
            out string detail)
        {
            results = Array.Empty<LootRollResult>();
            detail = string.Empty;
            if (table == null)
            {
                detail = "loot table is unavailable";
                return false;
            }
            if (random01 == null)
            {
                detail = "loot random source is unavailable";
                return false;
            }

            LootTableEntryDefinition[] entries = table.entries ?? Array.Empty<LootTableEntryDefinition>();
            var output = new List<LootRollResult>(Math.Min(entries.Length, Math.Max(1, (int)table.rolls)));

            switch (table.rollMode)
            {
                case LootRollMode.Independent:
                    RollIndependent(entries, random01, predicate, output);
                    break;
                case LootRollMode.Weighted:
                    int rolls = Math.Max(1, Math.Min(32, (int)table.rolls));
                    for (int i = 0; i < rolls; ++i)
                    {
                        if (!TryWeighted(entries, random01(), random01(), predicate, out LootRollResult result))
                        {
                            if (output.Count == 0)
                            {
                                detail = "loot table has no eligible entries";
                                return false;
                            }
                            break;
                        }
                        output.Add(result);
                    }
                    break;
                default:
                    detail = "loot table uses an unsupported roll mode";
                    return false;
            }

            results = output.ToArray();
            return true;
        }

        /// <summary>
        /// Compatibility single weighted roll used by existing recovery tests/callers.
        /// New feature code should prefer TryRollAll and honor the table's explicit rollMode.
        /// </summary>
        public static bool TryRoll(
            LootTableDefinition table,
            string professionId,
            int professionLevel,
            double selectionRoll01,
            double quantityRoll01,
            Func<LootTableEntryDefinition, bool> predicate,
            out LootRollResult result,
            out string detail)
        {
            result = default;
            detail = string.Empty;
            if (table == null)
            {
                detail = "loot table is unavailable";
                return false;
            }

            return TryWeighted(
                table.entries ?? Array.Empty<LootTableEntryDefinition>(),
                selectionRoll01,
                quantityRoll01,
                predicate,
                out result,
                out detail);
        }

        private static void RollIndependent(
            LootTableEntryDefinition[] entries,
            Func<double> random01,
            Func<LootTableEntryDefinition, bool> predicate,
            List<LootRollResult> output)
        {
            for (int i = 0; i < entries.Length; ++i)
            {
                LootTableEntryDefinition entry = entries[i];
                if (!IsEligible(entry, predicate) || entry.chance <= 0f)
                    continue;
                double chance = Math.Max(0d, Math.Min(1d, entry.chance));
                if (ClampUnit(random01()) >= chance)
                    continue;
                output.Add(BuildResult(i, entry, random01()));
            }
        }

        private static bool TryWeighted(
            LootTableEntryDefinition[] entries,
            double selectionRoll01,
            double quantityRoll01,
            Func<LootTableEntryDefinition, bool> predicate,
            out LootRollResult result)
        {
            return TryWeighted(entries, selectionRoll01, quantityRoll01, predicate, out result, out _);
        }

        private static bool TryWeighted(
            LootTableEntryDefinition[] entries,
            double selectionRoll01,
            double quantityRoll01,
            Func<LootTableEntryDefinition, bool> predicate,
            out LootRollResult result,
            out string detail)
        {
            result = default;
            detail = string.Empty;
            double totalWeight = 0d;
            for (int i = 0; i < entries.Length; ++i)
            {
                LootTableEntryDefinition entry = entries[i];
                if (!IsEligible(entry, predicate) || GetWeight(entry) <= 0d)
                    continue;
                totalWeight += GetWeight(entry);
            }

            if (!(totalWeight > 0d) || double.IsNaN(totalWeight) || double.IsInfinity(totalWeight))
            {
                detail = "loot table has no eligible entries";
                return false;
            }

            double roll = ClampUnit(selectionRoll01) * totalWeight;
            double cursor = 0d;
            for (int i = 0; i < entries.Length; ++i)
            {
                LootTableEntryDefinition entry = entries[i];
                if (!IsEligible(entry, predicate) || GetWeight(entry) <= 0d)
                    continue;
                cursor += GetWeight(entry);
                if (roll < cursor)
                {
                    result = BuildResult(i, entry, quantityRoll01);
                    return true;
                }
            }

            // Numeric top-edge fallback.
            for (int i = entries.Length - 1; i >= 0; --i)
            {
                LootTableEntryDefinition entry = entries[i];
                if (!IsEligible(entry, predicate) || GetWeight(entry) <= 0d)
                    continue;
                result = BuildResult(i, entry, quantityRoll01);
                return true;
            }

            detail = "loot table has no eligible entries";
            return false;
        }

        private static LootRollResult BuildResult(int entryIndex, LootTableEntryDefinition entry, double quantityRoll01)
        {
            int minimum = Math.Max(1, entry.minQuantity);
            int maximum = Math.Max(minimum, entry.maxQuantity);
            int quantity = minimum;
            if (maximum > minimum)
            {
                int span = checked(maximum - minimum + 1);
                int offset = Math.Min(span - 1, (int)(ClampUnit(quantityRoll01) * span));
                quantity = minimum + offset;
            }
            return new LootRollResult(entryIndex, entry.itemDataId, entry.itemDefinitionId, quantity);
        }

        private static bool IsEligible(
            LootTableEntryDefinition entry,
            Func<LootTableEntryDefinition, bool> predicate)
        {
            if (entry == null || entry.itemDataId == 0)
                return false;
            return predicate == null || predicate(entry);
        }

        private static double GetWeight(LootTableEntryDefinition entry)
        {
            if (entry == null) return 0d;
            if (entry.weight > 0) return entry.weight;
            // Compatibility for existing weighted recovery data authored before explicit weight existed.
            return entry.chance > 0f ? entry.chance : 0d;
        }

        private static double ClampUnit(double value)
        {
            if (double.IsNaN(value) || value <= 0d) return 0d;
            if (value >= 1d) return BitDecrementOne;
            return value;
        }

        private const double BitDecrementOne = 0.99999999999999989d;
    }
}
