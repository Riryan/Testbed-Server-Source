using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Backend;

namespace Game.UnityIntegration.Backend
{
    public sealed partial class BackendInternalClient
    {
        public Task<BackendFriendResponse> LoadFriendsAsync(BackendFriendLoadRequest request, CancellationToken cancellationToken = default) =>
            PostAsync<BackendFriendLoadRequest, BackendFriendResponse>("/v1/internal/friends/load", request, cancellationToken);

        public Task<BackendFriendResponse> AddFriendAsync(BackendFriendMutationRequest request, CancellationToken cancellationToken = default) =>
            PostAsync<BackendFriendMutationRequest, BackendFriendResponse>("/v1/internal/friends/add", request, cancellationToken);

        public Task<BackendFriendResponse> RemoveFriendAsync(BackendFriendMutationRequest request, CancellationToken cancellationToken = default) =>
            PostAsync<BackendFriendMutationRequest, BackendFriendResponse>("/v1/internal/friends/remove", request, cancellationToken);

        public Task<BackendTradeCommitResponse> CommitTradeAsync(BackendTradeCommitRequest request, CancellationToken cancellationToken = default) =>
            PostAsync<BackendTradeCommitRequest, BackendTradeCommitResponse>("/v1/internal/trade/commit", request, cancellationToken);

        public Task<BackendStorageLoadResponse> LoadStorageAsync(BackendStorageLoadRequest request, CancellationToken cancellationToken = default) =>
            PostAsync<BackendStorageLoadRequest, BackendStorageLoadResponse>("/v1/internal/storage/load", request, cancellationToken);

        public Task<BackendStorageTransferResponse> TransferStorageAsync(BackendStorageTransferRequest request, CancellationToken cancellationToken = default) =>
            PostAsync<BackendStorageTransferRequest, BackendStorageTransferResponse>("/v1/internal/storage/transfer", request, cancellationToken);
    }
}
