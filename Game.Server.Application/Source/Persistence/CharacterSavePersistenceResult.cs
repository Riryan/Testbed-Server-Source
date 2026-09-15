using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    /// <summary>
    /// Persistence acknowledgement for one immutable character snapshot.
    /// Persisted=false means the caller must not clear dirty state.
    /// </summary>
    public readonly struct CharacterSavePersistenceResult
    {
        public CharacterId CharacterId { get; }
        public long RequestedRevision { get; }
        public bool Persisted { get; }
        public long StoredRevision { get; }

        public CharacterSavePersistenceResult(
            CharacterId characterId,
            long requestedRevision,
            bool persisted,
            long storedRevision)
        {
            CharacterId = characterId;
            RequestedRevision = requestedRevision;
            Persisted = persisted;
            StoredRevision = storedRevision;
        }
    }
}
