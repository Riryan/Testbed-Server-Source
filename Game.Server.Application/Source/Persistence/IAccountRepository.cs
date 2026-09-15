using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public interface IAccountRepository
    {
        Task<AccountPersistenceRecord> FindByNameAsync(string accountName, CancellationToken cancellationToken);
        Task<AccountCreatePersistenceResult> TryCreateAsync(
            string accountName,
            string passwordVerifier,
            long utcNowTicks,
            CancellationToken cancellationToken);
        Task TouchLastLoginAsync(AccountId accountId, long utcNowTicks, CancellationToken cancellationToken);
    }
}
