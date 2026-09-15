namespace Game.Shared.Protocol
{
    public enum CharacterSelectFailure : byte
    {
        None = 0,
        SessionNotFound = 1,
        InvalidSessionState = 2,
        CharacterAlreadyActive = 3,
        CharacterNotFoundOrNotOwned = 4,
        CharacterDataInvalid = 5,
        CharacterLoadFailed = 6,
        SessionChangedDuringLoad = 7,
    }
}
