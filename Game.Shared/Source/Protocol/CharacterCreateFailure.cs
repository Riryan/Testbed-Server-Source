namespace Game.Shared.Protocol
{
    public enum CharacterCreateFailure : byte
    {
        None = 0,
        SessionNotFound = 1,
        InvalidSessionState = 2,
        InvalidName = 3,
        NameAlreadyExists = 4,
        CharacterLimitReached = 5,
        PersistenceFailed = 6,
        SessionChangedDuringCreate = 7,
        InvalidAppearance = 8,
        InvalidPresentation = 9,
    }
}
