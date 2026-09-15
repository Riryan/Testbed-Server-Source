using Game.Shared.Characters;

namespace Game.Shared.Protocol
{
    public sealed class CharacterListResult
    {
        public bool Success { get; }
        public CharacterSummary[] Characters { get; }
        public string Error { get; }

        private CharacterListResult(bool success, CharacterSummary[] characters, string error)
        {
            Success = success;
            Characters = characters ?? new CharacterSummary[0];
            Error = error ?? string.Empty;
        }

        public static CharacterListResult Succeeded(CharacterSummary[] characters) =>
            new CharacterListResult(true, characters, string.Empty);

        public static CharacterListResult Failed(string error) =>
            new CharacterListResult(false, new CharacterSummary[0], error);
    }
}
