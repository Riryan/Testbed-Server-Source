using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    /// <summary>
    /// Supplies the current Backend-owned authority proof required to persist a
    /// character checkpoint. Implementations must fail closed after lease loss or expiry.
    /// </summary>
    public interface ICharacterPersistenceLeaseProofProvider
    {
        bool TryGetPersistenceLeaseOwnerToken(CharacterId characterId, out string ownerToken);
    }
}
