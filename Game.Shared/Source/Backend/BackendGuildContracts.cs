using System;
using System.Runtime.Serialization;

namespace Game.Shared.Backend
{
    [Serializable, DataContract]
    public sealed class BackendGuildMemberDto
    {
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "name")] public string name;
        [DataMember(Name = "role")] public byte role;
        [DataMember(Name = "joinedUtcTicks")] public long joinedUtcTicks;
    }

    [Serializable, DataContract]
    public sealed class BackendGuildSnapshotDto
    {
        [DataMember(Name = "guildId")] public long guildId;
        [DataMember(Name = "name")] public string name;
        [DataMember(Name = "revision")] public long revision;
        [DataMember(Name = "members")] public BackendGuildMemberDto[] members = Array.Empty<BackendGuildMemberDto>();
    }

    [Serializable, DataContract]
    public sealed class BackendGuildLoadRequest
    {
        [DataMember(Name = "characterId")] public long characterId;
    }

    [Serializable, DataContract]
    public sealed class BackendGuildCreateRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "name")] public string name;
    }

    [Serializable, DataContract]
    public sealed class BackendGuildJoinRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "guildId")] public long guildId;
    }

    [Serializable, DataContract]
    public sealed class BackendGuildLeaveRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
    }

    [Serializable, DataContract]
    public sealed class BackendGuildKickRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "actorCharacterId")] public long actorCharacterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "targetCharacterId")] public long targetCharacterId;
    }

    [Serializable, DataContract]
    public sealed class BackendGuildDisbandRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "actorCharacterId")] public long actorCharacterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
    }

    [Serializable, DataContract]
    public sealed class BackendGuildResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "found")] public bool found;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "guild")] public BackendGuildSnapshotDto guild;
    }
}
