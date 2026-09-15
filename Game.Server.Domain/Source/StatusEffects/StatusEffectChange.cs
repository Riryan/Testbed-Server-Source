using Game.Shared.Identity;
using Game.Shared.StatusEffects;

namespace Game.Server.Domain.StatusEffects
{
    public readonly struct StatusEffectChange
    {
        public CharacterId CharacterId { get; }
        public long Revision { get; }
        public StatusEffectChangeKind Kind { get; }
        public string DefinitionId { get; }
        public byte Stacks { get; }
        public double EndTime { get; }
        public long SourceCharacterIdValue { get; }
        public StatusEffectChangeReason Reason { get; }
        public bool ResetPeriodicClock { get; }

        public StatusEffectChange(
            CharacterId characterId,
            long revision,
            StatusEffectChangeKind kind,
            string definitionId,
            byte stacks,
            double endTime,
            long sourceCharacterIdValue,
            StatusEffectChangeReason reason,
            bool resetPeriodicClock)
        {
            CharacterId = characterId;
            Revision = revision;
            Kind = kind;
            DefinitionId = definitionId ?? string.Empty;
            Stacks = stacks;
            EndTime = endTime;
            SourceCharacterIdValue = sourceCharacterIdValue;
            Reason = reason;
            ResetPeriodicClock = resetPeriodicClock;
        }
    }
}
