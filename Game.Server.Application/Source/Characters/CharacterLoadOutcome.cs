using Game.Server.Domain.Players;
using Game.Shared.Protocol;

namespace Game.Server.Application.Characters
{
    public readonly struct CharacterLoadOutcome
    {
        public bool Success { get; }
        public PlayerRuntime Runtime { get; }
        public CharacterSelectFailure Failure { get; }

        private CharacterLoadOutcome(bool success, PlayerRuntime runtime, CharacterSelectFailure failure)
        {
            Success = success;
            Runtime = runtime;
            Failure = failure;
        }

        public static CharacterLoadOutcome Succeeded(PlayerRuntime runtime) =>
            new CharacterLoadOutcome(true, runtime, CharacterSelectFailure.None);

        public static CharacterLoadOutcome Failed(CharacterSelectFailure failure) =>
            new CharacterLoadOutcome(false, null, failure);
    }
}
