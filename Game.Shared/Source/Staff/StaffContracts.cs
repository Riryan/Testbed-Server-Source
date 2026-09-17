using System;
using System.Runtime.Serialization;

namespace Game.Shared.Staff
{
    [Flags]
    public enum StaffCapability : ulong
    {
        None = 0,
        ObservePlayers = 1UL << 0,
        HiddenObserve = 1UL << 1,
        SpectatePlayers = 1UL << 2,
        TeleportSelf = 1UL << 3,
        TeleportPlayers = 1UL << 4,
        InspectInventory = 1UL << 5,
        InspectAccount = 1UL << 6,
        ModerateChat = 1UL << 7,
        Kick = 1UL << 8,
        Mute = 1UL << 9,
        Suspend = 1UL << 10,
        SpawnTestEntity = 1UL << 11,
        ManageWorld = 1UL << 12,
        ModerateAccounts = 1UL << 13,
        BanAccounts = 1UL << 14,
        ManageEntitlements = 1UL << 15,
        ManageStaff = 1UL << 16,
        All = ulong.MaxValue,
    }

    public enum StaffVisibilityMode : byte
    {
        NormalPlayer = 0,
        VisibleStaff = 1,
        HiddenObserver = 2,
        Spectating = 3,
    }

    [Serializable, DataContract]
    public sealed class StaffAuthorizationDocument
    {
        [DataMember(Name = "entries")] public StaffAuthorizationSnapshot[] entries = Array.Empty<StaffAuthorizationSnapshot>();
    }

    [Serializable, DataContract]
    public sealed class StaffAuthorizationSnapshot
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "roleName")] public string roleName = string.Empty;
        [DataMember(Name = "capabilities")] public ulong capabilities;
        public StaffCapability CapabilityMask => (StaffCapability)capabilities;
    }

    [Serializable, DataContract]
    public sealed class StaffAuditRecord
    {
        [DataMember(Name = "sequence")] public long sequence;
        [DataMember(Name = "utcTicks")] public long utcTicks;
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "action")] public string action = string.Empty;
        [DataMember(Name = "targetCharacterId")] public long targetCharacterId;
        [DataMember(Name = "detail")] public string detail = string.Empty;
        [DataMember(Name = "success")] public bool success;
    }
}
