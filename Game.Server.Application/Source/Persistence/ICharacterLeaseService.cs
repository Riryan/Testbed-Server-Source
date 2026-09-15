using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public interface ICharacterLeaseService
    {
        Task<bool> TryAcquireAsync(
            AccountId accountId,
            CharacterId characterId,
            PlayerSessionId sessionId,
            CancellationToken cancellationToken);
        void Release(CharacterId characterId, PlayerSessionId sessionId);
        bool IsHeldBy(CharacterId characterId, PlayerSessionId sessionId);
    }
}
