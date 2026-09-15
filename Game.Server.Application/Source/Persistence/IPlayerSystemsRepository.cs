using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public interface IPlayerSystemsRepository
    {
        Task<PlayerSystemsPersistenceRecord> LoadAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken);
        Task<PlayerSystemsPersistenceRecord> LoadForReconciliationAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken);
        Task<PlayerSystemsCommitResult> TryCommitAsync(PlayerSystemsCommitRequest request, CancellationToken cancellationToken);
    }
}
