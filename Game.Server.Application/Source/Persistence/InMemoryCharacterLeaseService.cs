using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public sealed class InMemoryCharacterLeaseService : ICharacterLeaseService
    {
        private readonly object _gate = new object();
        private readonly Dictionary<CharacterId, PlayerSessionId> _leases = new Dictionary<CharacterId, PlayerSessionId>();

        public Task<bool> TryAcquireAsync(
            AccountId accountId,
            CharacterId characterId,
            PlayerSessionId sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool acquired;
            if (!accountId.IsValid || !characterId.IsValid || !sessionId.IsValid)
            {
                acquired = false;
            }
            else
            {
                lock (_gate)
                {
                    if (_leases.TryGetValue(characterId, out PlayerSessionId owner))
                    {
                        acquired = owner == sessionId;
                    }
                    else
                    {
                        _leases.Add(characterId, sessionId);
                        acquired = true;
                    }
                }
            }

            return Task.FromResult(acquired);
        }

        public void Release(CharacterId characterId, PlayerSessionId sessionId)
        {
            lock (_gate)
            {
                if (_leases.TryGetValue(characterId, out PlayerSessionId owner) && owner == sessionId)
                    _leases.Remove(characterId);
            }
        }

        public bool IsHeldBy(CharacterId characterId, PlayerSessionId sessionId)
        {
            lock (_gate)
                return _leases.TryGetValue(characterId, out PlayerSessionId owner) && owner == sessionId;
        }
    }
}
