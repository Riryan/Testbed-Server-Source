using Game.Shared.Identity;
using Game.Shared.Protocol;

namespace Game.Server.Application.Persistence
{
    public readonly struct CharacterDeletePersistenceResult
    {
        public bool Success { get; }
        public CharacterId CharacterId { get; }
        public string Name { get; }
        public CharacterDeleteFailure Failure { get; }

        private CharacterDeletePersistenceResult(
            bool success,
            CharacterId characterId,
            string name,
            CharacterDeleteFailure failure)
        {
            Success = success;
            CharacterId = characterId;
            Name = name ?? string.Empty;
            Failure = failure;
        }

        public static CharacterDeletePersistenceResult Deleted(CharacterId characterId, string name) =>
            new CharacterDeletePersistenceResult(true, characterId, name, CharacterDeleteFailure.None);

        public static CharacterDeletePersistenceResult Failed(CharacterDeleteFailure failure) =>
            new CharacterDeletePersistenceResult(false, default(CharacterId), string.Empty, failure);
    }
}
