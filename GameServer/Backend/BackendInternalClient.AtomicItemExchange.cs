using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Backend;

namespace Game.UnityIntegration.Backend
{
    public sealed partial class BackendInternalClient
    {
        public Task<BackendPlayerItemExchangeResponse> ExchangePlayerItemsAsync(
            BackendPlayerItemExchangeRequest request,
            CancellationToken cancellationToken = default) =>
            PostAsync<BackendPlayerItemExchangeRequest, BackendPlayerItemExchangeResponse>(
                "/v1/internal/player-systems/exchange", request, cancellationToken);
    }
}
