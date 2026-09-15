namespace Game.Shared.Chat
{
    /// <summary>
    /// Stable client/server chat channel identifiers. CommunicationsServer can reuse
    /// these contracts later without changing the client presentation layer.
    /// </summary>
    public enum ChatChannel : byte
    {
        Local = 0,
        Whisper = 1,
        Party = 2,
        Guild = 3,
        System = 4,
    }
}
