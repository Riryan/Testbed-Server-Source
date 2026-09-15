using System;
using System.Collections.Generic;
using Game.Shared.Backend;
using SQLite;

namespace Game.BackendServer;

internal sealed partial class BackendDatabase
{
    private const byte GuildRoleMember = 0;
    private const byte GuildRoleOwner = 1;
    private const int GuildMaxMembers = 256;

    private static void InitializeGuildSchema(SQLiteConnection conn)
    {
        conn.CreateTable<GuildRow>();
        conn.CreateTable<GuildMemberRow>();
        conn.CreateIndex("idx_guilds_name_key", "guilds", "nameKey", true);
        conn.CreateIndex("idx_guild_members_guild", "guild_members", "guildId", false);
    }

    public BackendGuildResponse LoadGuild(long characterId)
    {
        if (characterId <= 0)
            return GuildFailed("invalid character", false);

        return Execute(conn =>
        {
            GuildMemberRow membership = conn.Find<GuildMemberRow>(characterId);
            if (membership == null)
                return new BackendGuildResponse { success = true, found = false, error = string.Empty, guild = null };

            GuildRow guild = conn.Find<GuildRow>(membership.guildId);
            if (guild == null)
                return new BackendGuildResponse { success = true, found = false, error = string.Empty, guild = null };

            return GuildSucceeded(ReadGuild(conn, guild));
        });
    }

    public BackendGuildResponse CreateGuild(BackendGuildCreateRequest request)
    {
        if (request == null || request.accountId <= 0 || request.characterId <= 0 ||
            string.IsNullOrWhiteSpace(request.leaseOwnerToken))
            return GuildFailed("invalid guild create request", false);
        if (!TryNormalizeGuildName(request.name, out string guildName, out string nameKey, out string nameError))
            return GuildFailed(nameError, false);

        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return GuildFailed("character authority lease unavailable", false);

        try
        {
            return Execute(conn =>
            {
                BackendGuildResponse response = null;
                conn.RunInTransaction(() =>
                {
                    CharacterRow actor = FindOwnedCharacter(conn, request.accountId, request.characterId);
                    if (actor == null)
                    {
                        response = GuildFailed("character not found", false);
                        return;
                    }

                    if (conn.Find<GuildMemberRow>(request.characterId) != null)
                    {
                        response = GuildFailed("character is already in a guild", true);
                        return;
                    }

                    if (conn.FindWithQuery<GuildRow>("SELECT * FROM guilds WHERE nameKey=? LIMIT 1", nameKey) != null)
                    {
                        response = GuildFailed("guild name is already in use", false);
                        return;
                    }

                    long now = DateTime.UtcNow.Ticks;
                    var guild = new GuildRow
                    {
                        name = guildName,
                        nameKey = nameKey,
                        ownerCharacterId = request.characterId,
                        revision = 1,
                        createdUtcTicks = now,
                        updatedUtcTicks = now,
                    };
                    if (conn.Insert(guild) != 1 || guild.guildId <= 0)
                        throw new InvalidOperationException("Failed to create guild.");

                    if (conn.Insert(new GuildMemberRow
                    {
                        characterId = request.characterId,
                        guildId = guild.guildId,
                        role = GuildRoleOwner,
                        joinedUtcTicks = now,
                    }) != 1)
                        throw new InvalidOperationException("Failed to create guild owner membership.");

                    response = GuildSucceeded(ReadGuild(conn, guild));
                });
                return response ?? GuildFailed("guild create transaction unavailable", false);
            });
        }
        catch (SQLiteException)
        {
            return GuildFailed("guild name is already in use", false);
        }
    }

    public BackendGuildResponse JoinGuild(BackendGuildJoinRequest request)
    {
        if (request == null || request.accountId <= 0 || request.characterId <= 0 || request.guildId <= 0 ||
            string.IsNullOrWhiteSpace(request.leaseOwnerToken))
            return GuildFailed("invalid guild join request", false);
        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return GuildFailed("character authority lease unavailable", false);

        return Execute(conn =>
        {
            BackendGuildResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null)
                {
                    response = GuildFailed("character not found", false);
                    return;
                }
                if (conn.Find<GuildMemberRow>(request.characterId) != null)
                {
                    response = GuildFailed("character is already in a guild", true);
                    return;
                }

                GuildRow guild = conn.Find<GuildRow>(request.guildId);
                if (guild == null)
                {
                    response = GuildFailed("guild no longer exists", false);
                    return;
                }

                int count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM guild_members WHERE guildId=?", guild.guildId);
                if (count >= GuildMaxMembers)
                {
                    response = GuildFailed("guild is full", true);
                    return;
                }

                long now = DateTime.UtcNow.Ticks;
                if (conn.Insert(new GuildMemberRow
                {
                    characterId = request.characterId,
                    guildId = guild.guildId,
                    role = GuildRoleMember,
                    joinedUtcTicks = now,
                }) != 1)
                    throw new InvalidOperationException("Failed to join guild.");

                TouchGuild(guild, now);
                if (conn.Update(guild) != 1)
                    throw new InvalidOperationException("Failed to advance guild revision.");

                response = GuildSucceeded(ReadGuild(conn, guild));
            });
            return response ?? GuildFailed("guild join transaction unavailable", false);
        });
    }

    public BackendGuildResponse LeaveGuild(BackendGuildLeaveRequest request)
    {
        if (request == null || request.accountId <= 0 || request.characterId <= 0 || string.IsNullOrWhiteSpace(request.leaseOwnerToken))
            return GuildFailed("invalid guild leave request", false);
        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return GuildFailed("character authority lease unavailable", false);

        return Execute(conn =>
        {
            BackendGuildResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null)
                {
                    response = GuildFailed("character not found", false);
                    return;
                }

                GuildMemberRow membership = conn.Find<GuildMemberRow>(request.characterId);
                if (membership == null)
                {
                    response = GuildFailed("character is not in a guild", false);
                    return;
                }
                GuildRow guild = conn.Find<GuildRow>(membership.guildId);
                if (guild == null)
                {
                    conn.Delete<GuildMemberRow>(request.characterId);
                    response = new BackendGuildResponse { success = true, found = false, error = string.Empty, guild = null };
                    return;
                }
                if (membership.role == GuildRoleOwner || guild.ownerCharacterId == request.characterId)
                {
                    response = GuildFailed("guild owner must disband the guild instead of leaving", true);
                    return;
                }

                if (conn.Delete<GuildMemberRow>(request.characterId) != 1)
                    throw new InvalidOperationException("Failed to leave guild.");
                TouchGuild(guild, DateTime.UtcNow.Ticks);
                if (conn.Update(guild) != 1)
                    throw new InvalidOperationException("Failed to advance guild revision.");

                response = GuildSucceeded(ReadGuild(conn, guild));
            });
            return response ?? GuildFailed("guild leave transaction unavailable", false);
        });
    }

    public BackendGuildResponse KickGuildMember(BackendGuildKickRequest request)
    {
        if (request == null || request.accountId <= 0 || request.actorCharacterId <= 0 || request.targetCharacterId <= 0 ||
            request.actorCharacterId == request.targetCharacterId || string.IsNullOrWhiteSpace(request.leaseOwnerToken))
            return GuildFailed("invalid guild kick request", false);
        if (!_characterLeases.IsCurrentOwner(request.actorCharacterId, request.leaseOwnerToken))
            return GuildFailed("character authority lease unavailable", false);

        return Execute(conn =>
        {
            BackendGuildResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow actor = FindOwnedCharacter(conn, request.accountId, request.actorCharacterId);
                if (actor == null)
                {
                    response = GuildFailed("character not found", false);
                    return;
                }
                GuildMemberRow actorMembership = conn.Find<GuildMemberRow>(request.actorCharacterId);
                if (actorMembership == null || actorMembership.role != GuildRoleOwner)
                {
                    response = GuildFailed("only the guild owner can remove members in Guild V1", false);
                    return;
                }
                GuildRow guild = conn.Find<GuildRow>(actorMembership.guildId);
                if (guild == null || guild.ownerCharacterId != request.actorCharacterId)
                {
                    response = GuildFailed("guild ownership is unavailable", false);
                    return;
                }
                GuildMemberRow targetMembership = conn.Find<GuildMemberRow>(request.targetCharacterId);
                if (targetMembership == null || targetMembership.guildId != guild.guildId)
                {
                    response = GuildFailed("target is not in your guild", true);
                    return;
                }
                if (targetMembership.role == GuildRoleOwner)
                {
                    response = GuildFailed("guild owner cannot be removed", true);
                    return;
                }

                if (conn.Delete<GuildMemberRow>(request.targetCharacterId) != 1)
                    throw new InvalidOperationException("Failed to remove guild member.");
                TouchGuild(guild, DateTime.UtcNow.Ticks);
                if (conn.Update(guild) != 1)
                    throw new InvalidOperationException("Failed to advance guild revision.");
                response = GuildSucceeded(ReadGuild(conn, guild));
            });
            return response ?? GuildFailed("guild kick transaction unavailable", false);
        });
    }

    public BackendGuildResponse DisbandGuild(BackendGuildDisbandRequest request)
    {
        if (request == null || request.accountId <= 0 || request.actorCharacterId <= 0 || string.IsNullOrWhiteSpace(request.leaseOwnerToken))
            return GuildFailed("invalid guild disband request", false);
        if (!_characterLeases.IsCurrentOwner(request.actorCharacterId, request.leaseOwnerToken))
            return GuildFailed("character authority lease unavailable", false);

        return Execute(conn =>
        {
            BackendGuildResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow actor = FindOwnedCharacter(conn, request.accountId, request.actorCharacterId);
                if (actor == null)
                {
                    response = GuildFailed("character not found", false);
                    return;
                }
                GuildMemberRow membership = conn.Find<GuildMemberRow>(request.actorCharacterId);
                if (membership == null || membership.role != GuildRoleOwner)
                {
                    response = GuildFailed("only the guild owner can disband the guild", false);
                    return;
                }
                GuildRow guild = conn.Find<GuildRow>(membership.guildId);
                if (guild == null || guild.ownerCharacterId != request.actorCharacterId)
                {
                    response = GuildFailed("guild ownership is unavailable", false);
                    return;
                }

                BackendGuildSnapshotDto before = ReadGuild(conn, guild);
                conn.Execute("DELETE FROM guild_members WHERE guildId=?", guild.guildId);
                if (conn.Delete<GuildRow>(guild.guildId) != 1)
                    throw new InvalidOperationException("Failed to delete guild.");

                response = new BackendGuildResponse
                {
                    success = true,
                    found = false,
                    error = string.Empty,
                    guild = before,
                };
            });
            return response ?? GuildFailed("guild disband transaction unavailable", false);
        });
    }

    private static BackendGuildSnapshotDto ReadGuild(SQLiteConnection conn, GuildRow guild)
    {
        List<GuildMemberViewRow> rows = conn.Query<GuildMemberViewRow>(
            @"SELECT gm.characterId AS characterId,
                     gm.role AS role,
                     gm.joinedUtcTicks AS joinedUtcTicks,
                     c.name AS name
              FROM guild_members gm
              LEFT JOIN characters c ON c.characterId=gm.characterId
              WHERE gm.guildId=?
              ORDER BY gm.role DESC, gm.joinedUtcTicks ASC, gm.characterId ASC",
            guild.guildId);
        var members = new BackendGuildMemberDto[rows.Count];
        for (int i = 0; i < rows.Count; ++i)
        {
            GuildMemberViewRow row = rows[i];
            members[i] = new BackendGuildMemberDto
            {
                characterId = row.characterId,
                name = row.name ?? $"Character {row.characterId}",
                role = row.role,
                joinedUtcTicks = row.joinedUtcTicks,
            };
        }

        return new BackendGuildSnapshotDto
        {
            guildId = guild.guildId,
            name = guild.name ?? string.Empty,
            revision = guild.revision,
            members = members,
        };
    }

    private static void TouchGuild(GuildRow guild, long now)
    {
        guild.revision = checked(guild.revision + 1);
        guild.updatedUtcTicks = now;
    }

    private static BackendGuildResponse GuildSucceeded(BackendGuildSnapshotDto guild) =>
        new BackendGuildResponse { success = true, found = guild != null, error = string.Empty, guild = guild };

    private static BackendGuildResponse GuildFailed(string error, bool found) =>
        new BackendGuildResponse { success = false, found = found, error = error ?? string.Empty, guild = null };

    private static bool TryNormalizeGuildName(string raw, out string canonical, out string key, out string error)
    {
        canonical = (raw ?? string.Empty).Trim();
        key = string.Empty;
        if (canonical.Length < 3 || canonical.Length > 32)
        {
            error = "guild name must be 3-32 characters";
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
                    error = "guild name cannot contain repeated spaces";
                    return false;
                }
                previousSpace = true;
                continue;
            }
            previousSpace = false;
            if (!char.IsLetterOrDigit(c) && c != '\'' && c != '-')
            {
                error = "guild name contains unsupported characters";
                return false;
            }
        }

        key = canonical.ToUpperInvariant();
        error = string.Empty;
        return true;
    }

    private sealed class GuildMemberViewRow
    {
        public long characterId { get; set; }
        public string name { get; set; }
        public byte role { get; set; }
        public long joinedUtcTicks { get; set; }
    }

    [Table("guilds")]
    private sealed class GuildRow
    {
        [PrimaryKey, AutoIncrement]
        public long guildId { get; set; }
        [NotNull]
        public string name { get; set; }
        [NotNull]
        public string nameKey { get; set; }
        public long ownerCharacterId { get; set; }
        public long revision { get; set; }
        public long createdUtcTicks { get; set; }
        public long updatedUtcTicks { get; set; }
    }

    [Table("guild_members")]
    private sealed class GuildMemberRow
    {
        [PrimaryKey]
        public long characterId { get; set; }
        public long guildId { get; set; }
        public byte role { get; set; }
        public long joinedUtcTicks { get; set; }
    }
}
