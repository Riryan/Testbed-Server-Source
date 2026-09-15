using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Backend;

namespace Game.UnityIntegration.Backend
{
    public sealed partial class BackendInternalClient
    {
        public Task<BackendGuildResponse> LoadGuildAsync(BackendGuildLoadRequest request, CancellationToken cancellationToken) =>
            PostAsync<BackendGuildLoadRequest, BackendGuildResponse>("/v1/internal/guilds/load", request, cancellationToken);

        public Task<BackendGuildResponse> CreateGuildAsync(BackendGuildCreateRequest request, CancellationToken cancellationToken) =>
            PostAsync<BackendGuildCreateRequest, BackendGuildResponse>("/v1/internal/guilds/create", request, cancellationToken);

        public Task<BackendGuildResponse> JoinGuildAsync(BackendGuildJoinRequest request, CancellationToken cancellationToken) =>
            PostAsync<BackendGuildJoinRequest, BackendGuildResponse>("/v1/internal/guilds/join", request, cancellationToken);

        public Task<BackendGuildResponse> LeaveGuildAsync(BackendGuildLeaveRequest request, CancellationToken cancellationToken) =>
            PostAsync<BackendGuildLeaveRequest, BackendGuildResponse>("/v1/internal/guilds/leave", request, cancellationToken);

        public Task<BackendGuildResponse> KickGuildMemberAsync(BackendGuildKickRequest request, CancellationToken cancellationToken) =>
            PostAsync<BackendGuildKickRequest, BackendGuildResponse>("/v1/internal/guilds/kick", request, cancellationToken);

        public Task<BackendGuildResponse> DisbandGuildAsync(BackendGuildDisbandRequest request, CancellationToken cancellationToken) =>
            PostAsync<BackendGuildDisbandRequest, BackendGuildResponse>("/v1/internal/guilds/disband", request, cancellationToken);
    }
}
