using Game.Shared.Identity;
using Game.Shared.Resources;

namespace Game.Server.Domain.Resources
{
    public readonly struct CharacterResourceChange
    {
        public CharacterId CharacterId { get; }
        public CharacterResourceId ResourceId { get; }
        public long Revision { get; }
        public int Previous { get; }
        public int Current { get; }
        public int Minimum { get; }
        public int Maximum { get; }
        public int AppliedDelta => Current - Previous;
        public CharacterResourceChangeReason Reason { get; }

        public CharacterResourceChange(
            CharacterId characterId,
            CharacterResourceId resourceId,
            long revision,
            int previous,
            int current,
            int minimum,
            int maximum,
            CharacterResourceChangeReason reason)
        {
            CharacterId = characterId;
            ResourceId = resourceId;
            Revision = revision;
            Previous = previous;
            Current = current;
            Minimum = minimum;
            Maximum = maximum;
            Reason = reason;
        }
    }
}
