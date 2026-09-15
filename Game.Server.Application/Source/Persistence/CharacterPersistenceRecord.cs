using System;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Shared.Identity;
using Game.Shared.Characters;
using Game.Shared.Progression;

namespace Game.Server.Application.Persistence
{
    public readonly struct PersistedMagazineState
    {
        public ItemInstanceId ItemInstanceId { get; }
        public string LoadedAmmoDefinitionId { get; }
        public int LoadedRounds { get; }
        public long MagazineRevision { get; }

        public PersistedMagazineState(
            ItemInstanceId itemInstanceId,
            string loadedAmmoDefinitionId,
            int loadedRounds,
            long magazineRevision)
        {
            if (!itemInstanceId.IsValid) throw new ArgumentException("ItemInstanceId is invalid.", nameof(itemInstanceId));
            if (loadedRounds < 0) throw new ArgumentOutOfRangeException(nameof(loadedRounds));
            if (magazineRevision < 0) throw new ArgumentOutOfRangeException(nameof(magazineRevision));
            ItemInstanceId = itemInstanceId;
            LoadedAmmoDefinitionId = loadedRounds > 0 ? (loadedAmmoDefinitionId ?? string.Empty) : string.Empty;
            LoadedRounds = loadedRounds;
            MagazineRevision = magazineRevision;
        }
    }

    public sealed class CharacterPersistenceRecord
    {
        public const int CurrentSchemaVersion = CharacterPersistenceSchema.CurrentVersion;

        public int SchemaVersion { get; }
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public string Name { get; }
        public CharacterLocationState Location { get; }
        public long Revision { get; }
        public PlayerSystemsPersistenceRecord PlayerSystems { get; }
        public long ResourceRevision { get; }
        public PersistedCharacterResource[] Resources { get; }
        public CharacterAppearanceRecipe Appearance { get; }
        public CharacterPresentationPreferences PresentationPreferences { get; }
        public PersistedMagazineState[] Magazines { get; }
        public bool HasMagazineState { get; }
        public CharacterProgressionState Progression { get; }
        public bool HasProgressionState { get; }

        /// <summary>
        /// Runtime categories represented by this immutable capture. Repository-loaded
        /// records use None because they are already durable state, not a pending write.
        /// </summary>
        public PlayerDirtyFlags CapturedDirtyFlags { get; }

        public CharacterPersistenceRecord(
            AccountId accountId,
            CharacterId characterId,
            string name,
            CharacterLocationState location,
            long revision,
            int schemaVersion = CurrentSchemaVersion,
            PlayerDirtyFlags capturedDirtyFlags = PlayerDirtyFlags.None,
            PlayerSystemsPersistenceRecord playerSystems = null,
            long resourceRevision = 0,
            PersistedCharacterResource[] resources = null,
            CharacterAppearanceRecipe appearance = null,
            CharacterPresentationPreferences presentationPreferences = null,
            PersistedMagazineState[] magazines = null,
            bool hasMagazineState = false,
            CharacterProgressionState progression = null,
            bool hasProgressionState = false)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Character name is required.", nameof(name));
            if (revision < 0)
                throw new ArgumentOutOfRangeException(nameof(revision));
            if (schemaVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(schemaVersion));
            if (resourceRevision < 0)
                throw new ArgumentOutOfRangeException(nameof(resourceRevision));

            AccountId = accountId;
            CharacterId = characterId;
            Name = name;
            Location = location;
            Revision = revision;
            SchemaVersion = schemaVersion;
            CapturedDirtyFlags = capturedDirtyFlags;
            PlayerSystems = playerSystems;
            ResourceRevision = resourceRevision;
            Resources = resources ?? Array.Empty<PersistedCharacterResource>();
            Appearance = appearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
            PresentationPreferences = presentationPreferences?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
            Magazines = magazines ?? Array.Empty<PersistedMagazineState>();
            HasMagazineState = hasMagazineState;
            Progression = progression?.Clone() ?? CharacterProgressionState.CreateDefault();
            HasProgressionState = hasProgressionState;
        }

        public static CharacterPersistenceRecord Capture(PlayerRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            PlayerRuntimePersistenceSnapshot snapshot = runtime.CapturePersistenceSnapshot();
            CharacterResourceState[] resourceStates = snapshot.Resources?.Snapshot() ?? Array.Empty<CharacterResourceState>();
            var persistentResources = new System.Collections.Generic.List<PersistedCharacterResource>(resourceStates.Length);
            for (int i = 0; i < resourceStates.Length; ++i)
            {
                CharacterResourceState resource = resourceStates[i];
                if (resource.Persistence != Game.Shared.Resources.CharacterResourcePersistenceMode.Character)
                    continue;
                persistentResources.Add(new PersistedCharacterResource(resource.Id, resource.Current, resource.Maximum));
            }
            PersistedCharacterResource[] resources = persistentResources.ToArray();

            bool hasMagazineState = (snapshot.DirtyFlags & PlayerDirtyFlags.Magazine) != 0;
            ItemMagazineState[] sourceMagazines = snapshot.Magazines ?? Array.Empty<ItemMagazineState>();
            var magazines = new PersistedMagazineState[sourceMagazines.Length];
            for (int i = 0; i < sourceMagazines.Length; ++i)
            {
                ItemMagazineState source = sourceMagazines[i];
                magazines[i] = new PersistedMagazineState(
                    source.ItemInstanceId,
                    source.LoadedAmmoDefinitionId,
                    source.LoadedRounds,
                    source.MagazineRevision);
            }

            return new CharacterPersistenceRecord(
                snapshot.AccountId,
                snapshot.CharacterId,
                snapshot.Name,
                snapshot.Location,
                snapshot.Revision,
                CurrentSchemaVersion,
                snapshot.DirtyFlags,
                null,
                snapshot.Resources?.Revision ?? 0,
                resources,
                snapshot.Appearance,
                snapshot.PresentationPreferences,
                magazines,
                hasMagazineState,
                snapshot.Progression,
                (snapshot.DirtyFlags & PlayerDirtyFlags.Progression) != 0);
        }

        public CharacterPersistenceRecord Copy() =>
            new CharacterPersistenceRecord(
                AccountId,
                CharacterId,
                Name,
                Location,
                Revision,
                SchemaVersion,
                CapturedDirtyFlags,
                PlayerSystems,
                ResourceRevision,
                Resources,
                Appearance,
                PresentationPreferences,
                Magazines,
                HasMagazineState,
                Progression,
                HasProgressionState);
    }
}
