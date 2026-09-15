using Game.Shared.Identity;

namespace Game.Shared.Protocol
{
    public readonly struct CharacterCreateResult
    {
        public bool Success { get; }
        public CharacterId CharacterId { get; }
        public string Name { get; }
        public CharacterCreateFailure Failure { get; }

        private CharacterCreateResult(bool success, CharacterId characterId, string name, CharacterCreateFailure failure)
        {
            Success = success;
            CharacterId = characterId;
            Name = name ?? string.Empty;
            Failure = failure;
        }

        public static CharacterCreateResult Succeeded(CharacterId characterId, string name) =>
            new CharacterCreateResult(true, characterId, name, CharacterCreateFailure.None);

        public static CharacterCreateResult Failed(CharacterCreateFailure failure) =>
            new CharacterCreateResult(false, default(CharacterId), string.Empty, failure);
    }
}
