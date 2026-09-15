using System;
using System.Collections.Generic;

namespace Game.Server.Domain.Stats
{
    /// <summary>Immutable authoritative stat snapshot. Stat ids are definition-driven.</summary>
    public sealed class StatsState
    {
        private readonly Dictionary<string, float> _values;

        public StatsState(IEnumerable<KeyValuePair<string, float>> values)
        {
            _values = new Dictionary<string, float>(StringComparer.Ordinal);
            if (values == null)
                return;
            foreach (KeyValuePair<string, float> pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || float.IsNaN(pair.Value) || float.IsInfinity(pair.Value))
                    continue;
                _values[pair.Key] = pair.Value;
            }
        }

        public float Get(string statId, float fallback = 0f) =>
            !string.IsNullOrWhiteSpace(statId) && _values.TryGetValue(statId, out float value)
                ? value
                : fallback;

        public KeyValuePair<string, float>[] Snapshot()
        {
            var result = new KeyValuePair<string, float>[_values.Count];
            int i = 0;
            foreach (KeyValuePair<string, float> pair in _values)
                result[i++] = pair;
            return result;
        }

        public static StatsState DefaultCharacter() => new StatsState(new[]
        {
            new KeyValuePair<string, float>("Health.Max", 100f),
            new KeyValuePair<string, float>("Mana.Max", 100f),
            new KeyValuePair<string, float>("Stamina.Max", 100f),
            new KeyValuePair<string, float>("Armor", 0f),
            new KeyValuePair<string, float>("AttackPower", 0f),
        });
    }
}
