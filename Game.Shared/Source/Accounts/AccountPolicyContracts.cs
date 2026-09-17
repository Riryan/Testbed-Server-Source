using System;
using System.Runtime.Serialization;
using Game.Shared.Staff;

namespace Game.Shared.Accounts
{
    public enum AccountPlan : byte
    {
        Free = 0,
        Premium = 1,
    }

    public enum AdultEligibility : byte
    {
        Unknown = 0,
        Eligible = 1,
        Blocked = 2,
    }

    public enum AccountStaffRole : byte
    {
        None = 0,
        Support = 1,
        Admin = 2,
    }

    public enum AccountAccessState : byte
    {
        Active = 0,
        Suspended = 1,
        Banned = 2,
        Closed = 3,
    }

    public enum AccountBanKind : byte
    {
        None = 0,
        Temporary = 1,
        Permanent = 2,
    }

    public static class AccountStaffRolePolicy
    {
        public const StaffCapability SupportCapabilities =
            StaffCapability.ObservePlayers |
            StaffCapability.HiddenObserve |
            StaffCapability.SpectatePlayers |
            StaffCapability.InspectInventory |
            StaffCapability.InspectAccount |
            StaffCapability.ModerateChat |
            StaffCapability.Kick |
            StaffCapability.Mute |
            StaffCapability.Suspend |
            StaffCapability.ModerateAccounts;

        public const StaffCapability AdminCapabilities =
            StaffCapability.ObservePlayers |
            StaffCapability.HiddenObserve |
            StaffCapability.SpectatePlayers |
            StaffCapability.TeleportSelf |
            StaffCapability.TeleportPlayers |
            StaffCapability.InspectInventory |
            StaffCapability.InspectAccount |
            StaffCapability.ModerateChat |
            StaffCapability.Kick |
            StaffCapability.Mute |
            StaffCapability.Suspend |
            StaffCapability.SpawnTestEntity |
            StaffCapability.ManageWorld |
            StaffCapability.ModerateAccounts |
            StaffCapability.BanAccounts |
            StaffCapability.ManageEntitlements |
            StaffCapability.ManageStaff;

        public static StaffCapability CapabilitiesFor(AccountStaffRole role)
        {
            switch (role)
            {
                case AccountStaffRole.Support:
                    return SupportCapabilities;
                case AccountStaffRole.Admin:
                    return AdminCapabilities;
                default:
                    return StaffCapability.None;
            }
        }
    }

    [Serializable, DataContract]
    public sealed class AccountPolicySnapshot
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "revision")] public long revision;
        [DataMember(Name = "plan")] public AccountPlan plan;
        [DataMember(Name = "premiumUntilUtcTicks")] public long premiumUntilUtcTicks;
        [DataMember(Name = "email")] public string email = string.Empty;
        [DataMember(Name = "emailVerified")] public bool emailVerified;
        [DataMember(Name = "emailVerifiedUtcTicks")] public long emailVerifiedUtcTicks;
        [DataMember(Name = "adultEligibility")] public AdultEligibility adultEligibility;
        [DataMember(Name = "adultEnabled")] public bool adultEnabled;
        [DataMember(Name = "staffRole")] public AccountStaffRole staffRole;
        [DataMember(Name = "staffCapabilities")] public ulong staffCapabilities;
        [DataMember(Name = "accessState")] public AccountAccessState accessState;
        [DataMember(Name = "strikeLevel")] public int strikeLevel;
        [DataMember(Name = "banKind")] public AccountBanKind banKind;
        [DataMember(Name = "banExpiresUtcTicks")] public long banExpiresUtcTicks;
        [DataMember(Name = "banReason")] public string banReason = string.Empty;
        [DataMember(Name = "banIssuedByAccountId")] public long banIssuedByAccountId;
        [DataMember(Name = "suspensionExpiresUtcTicks")] public long suspensionExpiresUtcTicks;
        [DataMember(Name = "suspensionReason")] public string suspensionReason = string.Empty;
        [DataMember(Name = "suspensionIssuedByAccountId")] public long suspensionIssuedByAccountId;
        [DataMember(Name = "updatedUtcTicks")] public long updatedUtcTicks;

        public StaffCapability StaffCapabilityMask => (StaffCapability)staffCapabilities;
        public bool AdultContentActive => adultEligibility == AdultEligibility.Eligible && adultEnabled;

        public bool IsPremiumAt(long utcNowTicks) =>
            plan == AccountPlan.Premium &&
            (premiumUntilUtcTicks <= 0 || premiumUntilUtcTicks > utcNowTicks);

        public bool IsAccessAllowedAt(long utcNowTicks)
        {
            switch (accessState)
            {
                case AccountAccessState.Active:
                    return true;
                case AccountAccessState.Suspended:
                    return suspensionExpiresUtcTicks > 0 && suspensionExpiresUtcTicks <= utcNowTicks;
                case AccountAccessState.Banned:
                    return banKind == AccountBanKind.Temporary &&
                           banExpiresUtcTicks > 0 &&
                           banExpiresUtcTicks <= utcNowTicks;
                default:
                    return false;
            }
        }
    }
}
