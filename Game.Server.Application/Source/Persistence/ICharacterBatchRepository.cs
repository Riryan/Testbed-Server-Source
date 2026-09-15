using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Game.Server.Application.Persistence
{
    /// <summary>
    /// Optional optimized persistence boundary for repositories that can commit
    /// multiple immutable character checkpoints in one backend/database transaction.
    /// </summary>
    public interface ICharacterBatchRepository
    {
        Task<IReadOnlyList<CharacterSavePersistenceResult>> SaveBatchAsync(
            IReadOnlyList<CharacterPersistenceRecord> records,
            CancellationToken cancellationToken);
    }
}
