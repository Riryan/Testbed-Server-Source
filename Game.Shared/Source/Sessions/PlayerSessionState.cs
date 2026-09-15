namespace Game.Shared.Sessions
{
    public enum PlayerSessionState : byte
    {
        Connected = 0,
        Authenticating = 1,
        CharacterLobby = 2,
        LoadingCharacter = 3,
        AwaitingWorldEntry = 4,
        InWorld = 5,
        Disconnecting = 6,
        Closed = 7,
    }
}
