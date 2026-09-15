using System;

namespace Game.Server.Domain.StatusEffects
{
    /// <summary>Immutable active status instance. Periodic scheduling lives outside domain state.</summary>
    public readonly struct StatusEffectInstanceState
    {
        public string DefinitionId { get; }
        public byte Stacks { get; }
        public double EndTime { get; }
        public long SourceCharacterIdValue { get; }

        public StatusEffectInstanceState(
            string definitionId,
            int stacks,
            double endTime,
            long sourceCharacterIdValue)
        {
            if (string.IsNullOrWhiteSpace(definitionId))
                throw new ArgumentException("Status definition id is required.", nameof(definitionId));
            if (stacks < 1 || stacks > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(stacks));
            if (double.IsNaN(endTime) || double.IsInfinity(endTime) || endTime < 0d)
                throw new ArgumentOutOfRangeException(nameof(endTime));
            if (sourceCharacterIdValue < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceCharacterIdValue));

            DefinitionId = definitionId;
            Stacks = (byte)stacks;
            EndTime = endTime;
            SourceCharacterIdValue = sourceCharacterIdValue;
        }
    }
}
