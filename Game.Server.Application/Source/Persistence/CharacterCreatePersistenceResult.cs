using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public readonly struct CharacterCreatePersistenceResult
    {
        public bool Success { get; }
        public bool NameAlreadyExists { get; }
        public bool CharacterLimitReached { get; }
        public CharacterId CharacterId { get; }

        private CharacterCreatePersistenceResult(
            bool success,
            bool nameAlreadyExists,
            bool characterLimitReached,
            CharacterId characterId)
        {
            Success = success;
            NameAlreadyExists = nameAlreadyExists;
            CharacterLimitReached = characterLimitReached;
            CharacterId = characterId;
        }

        public static CharacterCreatePersistenceResult Created(CharacterId characterId) =>
            new CharacterCreatePersistenceResult(true, false, false, characterId);

        public static CharacterCreatePersistenceResult DuplicateName() =>
            new CharacterCreatePersistenceResult(false, true, false, default(CharacterId));

        public static CharacterCreatePersistenceResult LimitReached() =>
            new CharacterCreatePersistenceResult(false, false, true, default(CharacterId));

        public static CharacterCreatePersistenceResult Failed() =>
            new CharacterCreatePersistenceResult(false, false, false, default(CharacterId));
    }
}
