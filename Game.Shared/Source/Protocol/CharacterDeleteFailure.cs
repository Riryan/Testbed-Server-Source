namespace Game.Shared.Protocol
{
    public enum CharacterDeleteFailure : byte
    {
        None = 0,
        SessionNotFound = 1,
        InvalidSessionState = 2,
        InvalidCharacter = 3,
        CharacterNotFoundOrNotOwned = 4,
        CharacterActive = 5,
        GuildOwner = 6,
        PersistenceFailed = 7,
        SessionChangedDuringDelete = 8,
    }
}
