using System;

namespace Game.Server.Domain.StatusEffects
{
    public readonly struct StatusStatModifierEntry
    {
        public string StatId { get; }
        public float FlatAdd { get; }
        public float PercentAdd { get; }
        public float Multiply { get; }
        public bool HasOverride { get; }
        public float OverrideValue { get; }

        public StatusStatModifierEntry(
            string statId,
            float flatAdd,
            float percentAdd,
            float multiply,
            bool hasOverride,
            float overrideValue)
        {
            StatId = statId ?? string.Empty;
            FlatAdd = flatAdd;
            PercentAdd = percentAdd;
            Multiply = multiply;
            HasOverride = hasOverride;
            OverrideValue = overrideValue;
        }

        public float Apply(float baseValue)
        {
            if (HasOverride)
                baseValue = OverrideValue;
            double result = (baseValue + FlatAdd) * Math.Max(0d, 1d + PercentAdd) * Math.Max(0d, Multiply);
            if (double.IsNaN(result)) return baseValue;
            if (result >= float.MaxValue) return float.MaxValue;
            if (result <= float.MinValue) return float.MinValue;
            return (float)result;
        }
    }

    public sealed class StatusStatModifierState
    {
        private readonly StatusStatModifierEntry[] _entries;

        public StatusStatModifierState(StatusStatModifierEntry[] entries) =>
            _entries = entries == null || entries.Length == 0
                ? Array.Empty<StatusStatModifierEntry>()
                : (StatusStatModifierEntry[])entries.Clone();

        public float Apply(string statId, float baseValue)
        {
            if (string.IsNullOrWhiteSpace(statId))
                return baseValue;
            for (int i = 0; i < _entries.Length; ++i)
                if (string.Equals(_entries[i].StatId, statId, StringComparison.Ordinal))
                    return _entries[i].Apply(baseValue);
            return baseValue;
        }

        public static StatusStatModifierState Empty { get; } =
            new StatusStatModifierState(Array.Empty<StatusStatModifierEntry>());
    }
}
