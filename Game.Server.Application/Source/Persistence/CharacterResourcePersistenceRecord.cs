using Game.Shared.Resources;

namespace Game.Server.Application.Persistence
{
    public sealed class PersistedCharacterResource
    {
        public CharacterResourceId ResourceId { get; }
        public int Current { get; }
        public int Maximum { get; }

        public PersistedCharacterResource(CharacterResourceId resourceId, int current, int maximum)
        {
            ResourceId = resourceId;
            Current = current;
            Maximum = maximum;
        }
    }
}
