using Game.Server.Domain.Characters;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Resources;
using Game.Shared.Identity;
using Game.Shared.Characters;
using Game.Shared.Progression;

namespace Game.Server.Domain.Players
{
    /// <summary>
    /// Atomic, dependency-light snapshot of the runtime values required by
    /// persistence adapters. This prevents asynchronous saves from reading a
    /// half-old/half-new runtime state.
    /// </summary>
    public readonly struct PlayerRuntimePersistenceSnapshot
    {
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public string Name { get; }
        public CharacterLocationState Location { get; }
        public long Revision { get; }
        public PlayerDirtyFlags DirtyFlags { get; }
        public CharacterResourcesState Resources { get; }
        public CharacterAppearanceRecipe Appearance { get; }
        public CharacterPresentationPreferences PresentationPreferences { get; }
        public ItemMagazineState[] Magazines { get; }
        public CharacterProgressionState Progression { get; }

        public PlayerRuntimePersistenceSnapshot(
            AccountId accountId,
            CharacterId characterId,
            string name,
            CharacterLocationState location,
            long revision,
            PlayerDirtyFlags dirtyFlags,
            CharacterResourcesState resources,
            CharacterAppearanceRecipe appearance,
            CharacterPresentationPreferences presentationPreferences = null,
            ItemMagazineState[] magazines = null,
            CharacterProgressionState progression = null)
        {
            AccountId = accountId;
            CharacterId = characterId;
            Name = name;
            Location = location;
            Revision = revision;
            DirtyFlags = dirtyFlags;
            Resources = resources;
            Appearance = appearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
            PresentationPreferences = presentationPreferences?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
            Magazines = magazines;
            Progression = progression?.Clone() ?? CharacterProgressionState.CreateDefault();
        }
    }
}
