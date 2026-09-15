using Game.Shared.Identity;

namespace Game.Shared.Protocol
{
    public readonly struct CharacterDeleteResult
    {
        public bool Success { get; }
        public CharacterId CharacterId { get; }
        public string Name { get; }
        public CharacterDeleteFailure Failure { get; }

        private CharacterDeleteResult(
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

        public static CharacterDeleteResult Succeeded(CharacterId characterId, string name) =>
            new CharacterDeleteResult(true, characterId, name, CharacterDeleteFailure.None);

        public static CharacterDeleteResult Failed(CharacterDeleteFailure failure) =>
            new CharacterDeleteResult(false, default(CharacterId), string.Empty, failure);
    }
}
