using Game.Shared.Identity;

namespace Game.Shared.Protocol
{
    public readonly struct CharacterSelectResult
    {
        public bool Success { get; }
        public CharacterId CharacterId { get; }
        public CharacterSelectFailure Failure { get; }

        private CharacterSelectResult(bool success, CharacterId characterId, CharacterSelectFailure failure)
        {
            Success = success;
            CharacterId = characterId;
            Failure = failure;
        }

        public static CharacterSelectResult Succeeded(CharacterId characterId) =>
            new CharacterSelectResult(true, characterId, CharacterSelectFailure.None);

        public static CharacterSelectResult Failed(CharacterSelectFailure failure) =>
            new CharacterSelectResult(false, default(CharacterId), failure);
    }
}
