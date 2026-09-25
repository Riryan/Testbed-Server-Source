using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Players;

namespace Game.Server.Application.Social
{
    public enum GuildMemberRole : byte
    {
        Member = 0,
        Owner = 1,
    }

    public sealed class GuildMemberSnapshot
    {
        public long CharacterId { get; }
        public string Name { get; }
        public GuildMemberRole Role { get; }

        public GuildMemberSnapshot(long characterId, string name, GuildMemberRole role)
        {
            CharacterId = characterId;
            Name = name ?? string.Empty;
            Role = role;
        }
    }

    public sealed class GuildInviteSnapshot
    {
        public long InviterCharacterId { get; }
        public string InviterName { get; }
        public long GuildId { get; }
        public string GuildName { get; }
        public DateTime ExpiresUtc { get; }

        public GuildInviteSnapshot(long inviterCharacterId, string inviterName, long guildId, string guildName, DateTime expiresUtc)
        {
            InviterCharacterId = inviterCharacterId;
            InviterName = inviterName ?? string.Empty;
            GuildId = guildId;
            GuildName = guildName ?? string.Empty;
            ExpiresUtc = expiresUtc;
        }
    }

    public sealed class GuildSnapshot
    {
        public long GuildId { get; }
        public string Name { get; }
        public long Revision { get; }
        public GuildMemberSnapshot[] Members { get; }

        public GuildSnapshot(long guildId, string name, long revision, GuildMemberSnapshot[] members)
        {
            GuildId = guildId;
            Name = name ?? string.Empty;
            Revision = revision;
            Members = members ?? Array.Empty<GuildMemberSnapshot>();
        }

        public bool TryGetMember(long characterId, out GuildMemberSnapshot member)
        {
            for (int i = 0; i < Members.Length; ++i)
            {
                if (Members[i].CharacterId != characterId)
                    continue;
                member = Members[i];
                return true;
            }
            member = null;
            return false;
        }
    }

    public readonly struct GuildRepositoryResult
    {
        public bool Success { get; }
        public bool Found { get; }
        public string Error { get; }
        public GuildSnapshot Guild { get; }

        public GuildRepositoryResult(bool success, bool found, string error, GuildSnapshot guild)
        {
            Success = success;
            Found = found;
            Error = error ?? string.Empty;
            Guild = guild;
        }
    }

    public interface IGuildRepository
    {
        Task<GuildRepositoryResult> LoadByCharacterAsync(long characterId, CancellationToken cancellationToken);
        Task<GuildRepositoryResult> CreateAsync(long accountId, long characterId, string leaseOwnerToken, string name, CancellationToken cancellationToken);
        Task<GuildRepositoryResult> JoinAsync(long accountId, long characterId, string leaseOwnerToken, long guildId, CancellationToken cancellationToken);
        Task<GuildRepositoryResult> LeaveAsync(long accountId, long characterId, string leaseOwnerToken, CancellationToken cancellationToken);
        Task<GuildRepositoryResult> KickAsync(long accountId, long actorCharacterId, string leaseOwnerToken, long targetCharacterId, CancellationToken cancellationToken);
        Task<GuildRepositoryResult> DisbandAsync(long accountId, long actorCharacterId, string leaseOwnerToken, CancellationToken cancellationToken);
    }

    public readonly struct GuildOperationResult
    {
        public bool Success { get; }
        public string Message { get; }
        public long OtherCharacterId { get; }
        public GuildSnapshot Guild { get; }

        public GuildOperationResult(bool success, string message, long otherCharacterId = 0, GuildSnapshot guild = null)
        {
            Success = success;
            Message = message ?? string.Empty;
            OtherCharacterId = otherCharacterId;
            Guild = guild;
        }

        public static GuildOperationResult Ok(string message, long otherCharacterId = 0, GuildSnapshot guild = null) =>
            new GuildOperationResult(true, message, otherCharacterId, guild);

        public static GuildOperationResult Fail(string message) => new GuildOperationResult(false, message);
    }

    /// <summary>
    /// V1 Guild authority: persistent identity/membership with in-memory short-lived invites.
    /// Storage, treasury, housing, XP, loot and gameplay perks are deliberately out of scope.
    /// </summary>
    public sealed class GuildService
    {
        public static readonly TimeSpan InviteLifetime = TimeSpan.FromSeconds(60);
        private const int MaxMembers = 256;

        private sealed class PendingInvite
        {
            public long InviterCharacterId;
            public string InviterName;
            public long TargetCharacterId;
            public long GuildId;
            public string GuildName;
            public DateTime ExpiresUtc;
        }

        private readonly object _gate = new object();
        private readonly IGuildRepository _repository;
        private readonly ICharacterPersistenceLeaseProofProvider _leaseProof;
        private readonly Dictionary<long, GuildSnapshot> _guildByCharacter = new Dictionary<long, GuildSnapshot>();
        private readonly Dictionary<long, PendingInvite> _inviteByTarget = new Dictionary<long, PendingInvite>();

        public event Action<long> StateChanged;

        public GuildService(IGuildRepository repository, ICharacterPersistenceLeaseProofProvider leaseProof)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _leaseProof = leaseProof ?? throw new ArgumentNullException(nameof(leaseProof));
        }

        public async Task<GuildOperationResult> CreateAsync(PlayerRuntime actor, string name, CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out long accountId, out long characterId, out _))
                return GuildOperationResult.Fail("Guild creation is unavailable.");
            if (!TryValidateGuildName(name, out string canonical, out string validationError))
                return GuildOperationResult.Fail(validationError);
            if (!_leaseProof.TryGetPersistenceLeaseOwnerToken(actor.CharacterId, out string leaseToken))
                return GuildOperationResult.Fail("Character authority lease unavailable.");

            GuildRepositoryResult result = await _repository.CreateAsync(accountId, characterId, leaseToken, canonical, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return GuildOperationResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? "Unable to create guild." : result.Error);
            Cache(result.Guild);
            NotifyGuildState(result.Guild);
            return GuildOperationResult.Ok($"Guild '{result.Guild.Name}' created.", 0, result.Guild);
        }

        public async Task<GuildOperationResult> InviteAsync(PlayerRuntime actor, PlayerRuntime target, CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out _, out long actorId, out string actorName) ||
                !TryIdentity(target, out _, out long targetId, out string targetName))
                return GuildOperationResult.Fail("Guild invite target is unavailable.");
            if (actorId == targetId)
                return GuildOperationResult.Fail("You cannot invite yourself to a guild.");

            GuildSnapshot actorGuild = await GetGuildAsync(actorId, cancellationToken).ConfigureAwait(false);
            if (actorGuild == null)
                return GuildOperationResult.Fail("You are not in a guild.");
            if (!actorGuild.TryGetMember(actorId, out GuildMemberSnapshot actorMember) || actorMember.Role != GuildMemberRole.Owner)
                return GuildOperationResult.Fail("Only the guild owner can invite members in Guild V1.");
            if (actorGuild.Members.Length >= MaxMembers)
                return GuildOperationResult.Fail("The guild is full.");

            GuildSnapshot targetGuild = await GetGuildAsync(targetId, cancellationToken).ConfigureAwait(false);
            if (targetGuild != null)
                return GuildOperationResult.Fail($"{targetName} is already in a guild.");

            lock (_gate)
            {
                _inviteByTarget[targetId] = new PendingInvite
                {
                    InviterCharacterId = actorId,
                    InviterName = actorName,
                    TargetCharacterId = targetId,
                    GuildId = actorGuild.GuildId,
                    GuildName = actorGuild.Name,
                    ExpiresUtc = DateTime.UtcNow + InviteLifetime,
                };
            }

            StateChanged?.Invoke(targetId);
            return GuildOperationResult.Ok($"Guild invite sent to {targetName}.", targetId, actorGuild);
        }

        public async Task<GuildOperationResult> AcceptAsync(PlayerRuntime target, CancellationToken cancellationToken)
        {
            if (!TryIdentity(target, out long accountId, out long characterId, out _))
                return GuildOperationResult.Fail("Guild acceptance is unavailable.");
            PendingInvite invite = TakeValidInvite(characterId);
            if (invite == null)
                return GuildOperationResult.Fail("You do not have a pending guild invite.");
            if (!_leaseProof.TryGetPersistenceLeaseOwnerToken(target.CharacterId, out string leaseToken))
                return GuildOperationResult.Fail("Character authority lease unavailable.");

            GuildRepositoryResult result = await _repository.JoinAsync(accountId, characterId, leaseToken, invite.GuildId, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return GuildOperationResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? "Unable to join guild." : result.Error);
            Cache(result.Guild);
            NotifyGuildState(result.Guild);
            return GuildOperationResult.Ok($"Joined guild '{result.Guild.Name}'.", invite.InviterCharacterId, result.Guild);
        }

        public GuildOperationResult Decline(PlayerRuntime target)
        {
            if (!TryIdentity(target, out _, out long characterId, out _))
                return GuildOperationResult.Fail("Guild decline is unavailable.");
            PendingInvite invite = TakeValidInvite(characterId);
            if (invite == null)
                return GuildOperationResult.Fail("You do not have a pending guild invite.");
            StateChanged?.Invoke(characterId);
            return GuildOperationResult.Ok("Guild invite declined.", invite.InviterCharacterId);
        }

        public async Task<GuildOperationResult> LeaveAsync(PlayerRuntime actor, CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out long accountId, out long characterId, out _))
                return GuildOperationResult.Fail("Guild leave is unavailable.");
            if (!_leaseProof.TryGetPersistenceLeaseOwnerToken(actor.CharacterId, out string leaseToken))
                return GuildOperationResult.Fail("Character authority lease unavailable.");

            GuildRepositoryResult result = await _repository.LeaveAsync(accountId, characterId, leaseToken, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return GuildOperationResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? "Unable to leave guild." : result.Error);
            Invalidate(result.Guild, characterId);
            NotifyGuildState(result.Guild, characterId);
            return GuildOperationResult.Ok("Left the guild.", 0, result.Guild);
        }

        public async Task<GuildOperationResult> KickAsync(PlayerRuntime actor, long targetCharacterId, CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out long accountId, out long actorId, out _))
                return GuildOperationResult.Fail("Guild kick is unavailable.");
            if (targetCharacterId <= 0 || targetCharacterId == actorId)
                return GuildOperationResult.Fail("Invalid guild member.");
            if (!_leaseProof.TryGetPersistenceLeaseOwnerToken(actor.CharacterId, out string leaseToken))
                return GuildOperationResult.Fail("Character authority lease unavailable.");

            GuildRepositoryResult result = await _repository.KickAsync(accountId, actorId, leaseToken, targetCharacterId, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return GuildOperationResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? "Unable to remove guild member." : result.Error);
            Invalidate(result.Guild, targetCharacterId);
            Cache(result.Guild);
            NotifyGuildState(result.Guild, targetCharacterId);
            return GuildOperationResult.Ok("Guild member removed.", targetCharacterId, result.Guild);
        }

        public async Task<GuildOperationResult> DisbandAsync(PlayerRuntime actor, CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out long accountId, out long actorId, out _))
                return GuildOperationResult.Fail("Guild disband is unavailable.");
            if (!_leaseProof.TryGetPersistenceLeaseOwnerToken(actor.CharacterId, out string leaseToken))
                return GuildOperationResult.Fail("Character authority lease unavailable.");

            GuildSnapshot before = await GetGuildAsync(actorId, cancellationToken).ConfigureAwait(false);
            GuildRepositoryResult result = await _repository.DisbandAsync(accountId, actorId, leaseToken, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return GuildOperationResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? "Unable to disband guild." : result.Error);
            Invalidate(before, 0);
            NotifyGuildState(before);
            return GuildOperationResult.Ok("Guild disbanded.", 0, before);
        }

        public Task<GuildSnapshot> GetGuildForChatAsync(PlayerRuntime actor, CancellationToken cancellationToken)
        {
            long characterId = actor?.CharacterId.Value ?? 0;
            return GetGuildAsync(characterId, cancellationToken);
        }

        public async Task<GuildSnapshot> GetGuildAsync(long characterId, CancellationToken cancellationToken)
        {
            if (characterId <= 0)
                return null;
            lock (_gate)
            {
                if (_guildByCharacter.TryGetValue(characterId, out GuildSnapshot cached))
                    return cached;
            }

            GuildRepositoryResult result = await _repository.LoadByCharacterAsync(characterId, cancellationToken).ConfigureAwait(false);
            if (!result.Success || !result.Found || result.Guild == null)
                return null;
            Cache(result.Guild);
            return result.Guild;
        }

        public bool TryGetPendingInvite(long targetCharacterId, out GuildInviteSnapshot snapshot)
        {
            snapshot = null;
            if (targetCharacterId <= 0)
                return false;

            lock (_gate)
            {
                if (!_inviteByTarget.TryGetValue(targetCharacterId, out PendingInvite invite))
                    return false;
                if (invite.ExpiresUtc <= DateTime.UtcNow)
                {
                    _inviteByTarget.Remove(targetCharacterId);
                    return false;
                }

                snapshot = new GuildInviteSnapshot(
                    invite.InviterCharacterId,
                    invite.InviterName,
                    invite.GuildId,
                    invite.GuildName,
                    invite.ExpiresUtc);
                return true;
            }
        }

        private PendingInvite TakeValidInvite(long targetCharacterId)
        {
            lock (_gate)
            {
                if (!_inviteByTarget.TryGetValue(targetCharacterId, out PendingInvite invite))
                    return null;
                _inviteByTarget.Remove(targetCharacterId);
                return invite.ExpiresUtc > DateTime.UtcNow ? invite : null;
            }
        }

        private void Cache(GuildSnapshot guild)
        {
            if (guild == null)
                return;
            lock (_gate)
            {
                for (int i = 0; i < guild.Members.Length; ++i)
                    _guildByCharacter[guild.Members[i].CharacterId] = guild;
            }
        }

        private void Invalidate(GuildSnapshot guild, long removedCharacterId)
        {
            lock (_gate)
            {
                if (guild != null)
                {
                    for (int i = 0; i < guild.Members.Length; ++i)
                        _guildByCharacter.Remove(guild.Members[i].CharacterId);
                }
                if (removedCharacterId > 0)
                    _guildByCharacter.Remove(removedCharacterId);
            }
        }

        private void NotifyGuildState(GuildSnapshot guild, long extraCharacterId = 0)
        {
            if (guild != null)
            {
                for (int i = 0; i < guild.Members.Length; ++i)
                {
                    long characterId = guild.Members[i]?.CharacterId ?? 0;
                    if (characterId > 0)
                        StateChanged?.Invoke(characterId);
                }
            }

            if (extraCharacterId > 0 && (guild == null || !guild.TryGetMember(extraCharacterId, out _)))
                StateChanged?.Invoke(extraCharacterId);
        }

        public static bool TryValidateGuildName(string raw, out string canonical, out string error)
        {
            canonical = (raw ?? string.Empty).Trim();
            if (canonical.Length < 3 || canonical.Length > 32)
            {
                error = "Guild name must be 3-32 characters.";
                return false;
            }

            bool previousSpace = false;
            for (int i = 0; i < canonical.Length; ++i)
            {
                char c = canonical[i];
                if (c == ' ')
                {
                    if (previousSpace)
                    {
                        error = "Guild name cannot contain repeated spaces.";
                        return false;
                    }
                    previousSpace = true;
                    continue;
                }
                previousSpace = false;
                if (!char.IsLetterOrDigit(c) && c != '\'' && c != '-')
                {
                    error = "Guild name may contain letters, numbers, spaces, apostrophes, and hyphens.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static bool TryIdentity(PlayerRuntime runtime, out long accountId, out long characterId, out string name)
        {
            accountId = runtime?.AccountId.Value ?? 0;
            characterId = runtime?.CharacterId.Value ?? 0;
            name = runtime?.Character?.Name ?? string.Empty;
            return accountId > 0 && characterId > 0 && !string.IsNullOrWhiteSpace(name);
        }
    }
}
