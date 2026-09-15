using System;
using Game.Shared.Effects;

namespace Game.Server.Domain.StatusEffects
{
    public sealed class CharacterStatusEffectsState
    {
        private readonly StatusEffectInstanceState[] _effects;

        public long Revision { get; }
        public StatusEffectMultipliers Multipliers { get; }
        public StatusStatModifierState StatModifiers { get; }
        public ulong ControlMask { get; }
        public int Count => _effects.Length;

        public CharacterStatusEffectsState(
            long revision,
            StatusEffectInstanceState[] effects,
            StatusEffectMultipliers multipliers,
            StatusStatModifierState statModifiers = null,
            ulong controlMask = 0UL)
        {
            if (revision < 0)
                throw new ArgumentOutOfRangeException(nameof(revision));
            Revision = revision;
            _effects = effects == null || effects.Length == 0
                ? Array.Empty<StatusEffectInstanceState>()
                : (StatusEffectInstanceState[])effects.Clone();
            Multipliers = multipliers;
            StatModifiers = statModifiers ?? StatusStatModifierState.Empty;
            ControlMask = controlMask;
        }

        public bool TryGet(string definitionId, out StatusEffectInstanceState state)
        {
            if (!string.IsNullOrWhiteSpace(definitionId))
            {
                for (int i = 0; i < _effects.Length; ++i)
                {
                    if (string.Equals(_effects[i].DefinitionId, definitionId, StringComparison.Ordinal))
                    {
                        state = _effects[i];
                        return true;
                    }
                }
            }
            state = default;
            return false;
        }

        public int FindIndex(string definitionId)
        {
            if (string.IsNullOrWhiteSpace(definitionId))
                return -1;
            for (int i = 0; i < _effects.Length; ++i)
                if (string.Equals(_effects[i].DefinitionId, definitionId, StringComparison.Ordinal))
                    return i;
            return -1;
        }


        public bool HasControl(ControlEffectType control)
        {
            int bit = (int)control;
            return bit > 0 && bit < 64 && (ControlMask & (1UL << bit)) != 0UL;
        }

        public StatusEffectInstanceState[] Snapshot() =>
            _effects.Length == 0
                ? Array.Empty<StatusEffectInstanceState>()
                : (StatusEffectInstanceState[])_effects.Clone();

        public static CharacterStatusEffectsState Empty =>
            new CharacterStatusEffectsState(0, Array.Empty<StatusEffectInstanceState>(), StatusEffectMultipliers.Identity);
    }
}
