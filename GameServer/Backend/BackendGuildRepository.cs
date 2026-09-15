using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Social;
using Game.Shared.Backend;
using Game.UnityIntegration.Backend;

namespace Game.GameServer.Backend
{
    internal sealed class BackendGuildRepository : IGuildRepository
    {
        private readonly BackendInternalClient _backend;

        public BackendGuildRepository(BackendInternalClient backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public async Task<GuildRepositoryResult> LoadByCharacterAsync(long characterId, CancellationToken cancellationToken) =>
            Convert(await _backend.LoadGuildAsync(new BackendGuildLoadRequest { characterId = characterId }, cancellationToken).ConfigureAwait(false));

        public async Task<GuildRepositoryResult> CreateAsync(long accountId, long characterId, string leaseOwnerToken, string name, CancellationToken cancellationToken) =>
            Convert(await _backend.CreateGuildAsync(new BackendGuildCreateRequest
            {
                accountId = accountId,
                characterId = characterId,
                leaseOwnerToken = leaseOwnerToken ?? string.Empty,
                name = name ?? string.Empty,
            }, cancellationToken).ConfigureAwait(false));

        public async Task<GuildRepositoryResult> JoinAsync(long accountId, long characterId, string leaseOwnerToken, long guildId, CancellationToken cancellationToken) =>
            Convert(await _backend.JoinGuildAsync(new BackendGuildJoinRequest
            {
                accountId = accountId,
                characterId = characterId,
                leaseOwnerToken = leaseOwnerToken ?? string.Empty,
                guildId = guildId,
            }, cancellationToken).ConfigureAwait(false));

        public async Task<GuildRepositoryResult> LeaveAsync(long accountId, long characterId, string leaseOwnerToken, CancellationToken cancellationToken) =>
            Convert(await _backend.LeaveGuildAsync(new BackendGuildLeaveRequest
            {
                accountId = accountId,
                characterId = characterId,
                leaseOwnerToken = leaseOwnerToken ?? string.Empty,
            }, cancellationToken).ConfigureAwait(false));

        public async Task<GuildRepositoryResult> KickAsync(long accountId, long actorCharacterId, string leaseOwnerToken, long targetCharacterId, CancellationToken cancellationToken) =>
            Convert(await _backend.KickGuildMemberAsync(new BackendGuildKickRequest
            {
                accountId = accountId,
                actorCharacterId = actorCharacterId,
                leaseOwnerToken = leaseOwnerToken ?? string.Empty,
                targetCharacterId = targetCharacterId,
            }, cancellationToken).ConfigureAwait(false));

        public async Task<GuildRepositoryResult> DisbandAsync(long accountId, long actorCharacterId, string leaseOwnerToken, CancellationToken cancellationToken) =>
            Convert(await _backend.DisbandGuildAsync(new BackendGuildDisbandRequest
            {
                accountId = accountId,
                actorCharacterId = actorCharacterId,
                leaseOwnerToken = leaseOwnerToken ?? string.Empty,
            }, cancellationToken).ConfigureAwait(false));

        private static GuildRepositoryResult Convert(BackendGuildResponse response)
        {
            if (response == null)
                return new GuildRepositoryResult(false, false, "guild backend returned no response", null);
            return new GuildRepositoryResult(response.success, response.found, response.error, Convert(response.guild));
        }

        private static GuildSnapshot Convert(BackendGuildSnapshotDto dto)
        {
            if (dto == null || dto.guildId <= 0)
                return null;
            BackendGuildMemberDto[] source = dto.members ?? Array.Empty<BackendGuildMemberDto>();
            var members = new GuildMemberSnapshot[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                members[i] = new GuildMemberSnapshot(
                    source[i].characterId,
                    source[i].name,
                    source[i].role == (byte)GuildMemberRole.Owner ? GuildMemberRole.Owner : GuildMemberRole.Member);
            }
            return new GuildSnapshot(dto.guildId, dto.name, dto.revision, members);
        }
    }
}
