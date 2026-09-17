using Game.Shared.Accounts;
using Game.Shared.Authentication;
using Game.Shared.Staff;
using SQLite;

namespace Game.BackendServer;

internal sealed partial class BackendDatabase
{
    private const int MaxAccountIpPrefixes = 32;
    private const int MaxAccountExactIps = 16;
    private const int MaxAccountDevices = 8;

    private void InitializeAccountPolicySchema(SQLiteConnection conn)
    {
        conn.CreateTable<AccountPolicyRow>();
        conn.CreateTable<AccountIpPrefixRow>();
        conn.CreateTable<AccountIpLeafRow>();
        conn.CreateTable<AccountDeviceRow>();
        conn.CreateTable<AccountEventRow>();

        conn.CreateIndex("idx_account_policy_staff_role", "account_policy", "staffRole", false);
        conn.CreateIndex("idx_account_policy_access_state", "account_policy", "accessState", false);
        conn.Execute(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_account_policy_email_key " +
            "ON account_policy(emailKey) WHERE emailKey<>''");

        conn.Execute(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_account_ip_prefix_unique " +
            "ON account_ip_prefixes(accountId, addressFamily, prefixLength, prefixHash)");
        conn.CreateIndex("idx_account_ip_prefix_hash", "account_ip_prefixes", "prefixHash", false);
        conn.CreateIndex("idx_account_ip_prefix_account", "account_ip_prefixes", "accountId", false);

        conn.Execute(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_account_ip_leaf_unique " +
            "ON account_ip_leaves(accountId, exactIpHash)");
        conn.CreateIndex("idx_account_ip_leaf_hash", "account_ip_leaves", "exactIpHash", false);
        conn.CreateIndex("idx_account_ip_leaf_prefix", "account_ip_leaves", "prefixHash", false);
        conn.CreateIndex("idx_account_ip_leaf_account", "account_ip_leaves", "accountId", false);

        conn.Execute(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_account_device_unique " +
            "ON account_devices(accountId, deviceHash)");
        conn.CreateIndex("idx_account_device_hash", "account_devices", "deviceHash", false);
        conn.CreateIndex("idx_account_device_account", "account_devices", "accountId", false);

        conn.CreateIndex("idx_account_events_account_time", "account_events", new[] { "accountId", "utcTicks" }, false);
        conn.CreateIndex("idx_account_events_actor_time", "account_events", new[] { "actorAccountId", "utcTicks" }, false);

        // One cheap set-based migration covers existing accounts. New accounts also receive
        // their policy row inside the account-creation transaction below.
        conn.Execute(
            "INSERT OR IGNORE INTO account_policy(" +
            "accountId,revision,plan,premiumUntilUtcTicks,email,emailKey,emailVerified,emailVerifiedUtcTicks," +
            "adultEligibility,adultEnabled,staffRole,staffCapabilities,accessState,strikeLevel," +
            "banKind,banExpiresUtcTicks,banReason,banIssuedByAccountId," +
            "suspensionExpiresUtcTicks,suspensionReason,suspensionIssuedByAccountId,updatedUtcTicks) " +
            "SELECT accountId,1,0,0,'','',0,0,0,0,0,0,0,0,0,0,'',0,0,'',0,createdUtcTicks FROM accounts");
    }

    private static AccountPolicyRow CreateDefaultPolicy(long accountId, long utcNowTicks) =>
        new AccountPolicyRow
        {
            accountId = accountId,
            revision = 1,
            plan = (byte)AccountPlan.Free,
            premiumUntilUtcTicks = 0,
            email = string.Empty,
            emailKey = string.Empty,
            emailVerified = false,
            emailVerifiedUtcTicks = 0,
            adultEligibility = (byte)AdultEligibility.Unknown,
            adultEnabled = false,
            staffRole = (byte)AccountStaffRole.None,
            staffCapabilities = 0,
            accessState = (byte)AccountAccessState.Active,
            strikeLevel = 0,
            banKind = (byte)AccountBanKind.None,
            banExpiresUtcTicks = 0,
            banReason = string.Empty,
            banIssuedByAccountId = 0,
            suspensionExpiresUtcTicks = 0,
            suspensionReason = string.Empty,
            suspensionIssuedByAccountId = 0,
            updatedUtcTicks = utcNowTicks,
        };

    private static void EnsureAccountPolicyRow(SQLiteConnection conn, long accountId, long utcNowTicks)
    {
        if (accountId <= 0 || conn.Find<AccountPolicyRow>(accountId) != null)
            return;
        conn.Insert(CreateDefaultPolicy(accountId, utcNowTicks));
    }

    public AccountPolicySnapshot PrepareAuthenticatedAccount(
        long accountId,
        string accountName,
        string developmentAdminAccount,
        AccountAccessObservation access,
        long utcNowTicks)
    {
        if (accountId <= 0)
            return null;

        return Execute(conn =>
        {
            EnsureAccountPolicyRow(conn, accountId, utcNowTicks);
            AccountPolicyRow row = conn.Find<AccountPolicyRow>(accountId);
            if (row == null)
                return null;

            string configuredAdmin = (developmentAdminAccount ?? string.Empty).Trim();
            if (configuredAdmin.Length > 0 &&
                string.Equals(
                    AccountCredentialPolicy.NormalizeAccountKey(accountName ?? string.Empty),
                    AccountCredentialPolicy.NormalizeAccountKey(configuredAdmin),
                    StringComparison.Ordinal) &&
                (AccountStaffRole)row.staffRole != AccountStaffRole.Admin)
            {
                row.staffRole = (byte)AccountStaffRole.Admin;
                row.staffCapabilities = CapabilityBits(AccountStaffRole.Admin);
                TouchPolicy(row, utcNowTicks);
                conn.Update(row);
                AppendAccountEvent(
                    conn,
                    accountId,
                    accountId,
                    "DevelopmentAdminBootstrap",
                    "Configured development admin account promoted to Admin.",
                    utcNowTicks);
            }

            row = NormalizePolicyForTime(conn, row, utcNowTicks);
            RecordAccountAccess(conn, accountId, access, utcNowTicks);
            return ToSnapshot(row);
        });
    }

    public AccountPolicySnapshot GetAccountPolicy(long accountId, long utcNowTicks)
    {
        if (accountId <= 0)
            return null;
        return Execute(conn =>
        {
            EnsureAccountPolicyRow(conn, accountId, utcNowTicks);
            AccountPolicyRow row = conn.Find<AccountPolicyRow>(accountId);
            return row == null ? null : ToSnapshot(NormalizePolicyForTime(conn, row, utcNowTicks));
        });
    }

    private AccountPolicyRow NormalizePolicyForTime(SQLiteConnection conn, AccountPolicyRow row, long utcNowTicks)
    {
        bool changed = false;
        string eventAction = string.Empty;
        string eventDetail = string.Empty;

        if ((AccountPlan)row.plan == AccountPlan.Premium &&
            row.premiumUntilUtcTicks > 0 &&
            row.premiumUntilUtcTicks <= utcNowTicks)
        {
            row.plan = (byte)AccountPlan.Free;
            row.premiumUntilUtcTicks = 0;
            changed = true;
            eventAction = "PremiumExpired";
            eventDetail = "Timed Premium entitlement expired.";
        }

        if ((AccountAccessState)row.accessState == AccountAccessState.Banned &&
            (AccountBanKind)row.banKind == AccountBanKind.Temporary &&
            row.banExpiresUtcTicks > 0 &&
            row.banExpiresUtcTicks <= utcNowTicks)
        {
            row.accessState = (byte)AccountAccessState.Active;
            row.banKind = (byte)AccountBanKind.None;
            row.banExpiresUtcTicks = 0;
            row.banReason = string.Empty;
            row.banIssuedByAccountId = 0;
            changed = true;
            eventAction = "BanExpired";
            eventDetail = "Temporary ban expired automatically.";
        }

        if ((AccountAccessState)row.accessState == AccountAccessState.Suspended &&
            row.suspensionExpiresUtcTicks > 0 &&
            row.suspensionExpiresUtcTicks <= utcNowTicks)
        {
            row.accessState = (byte)AccountAccessState.Active;
            row.suspensionExpiresUtcTicks = 0;
            row.suspensionReason = string.Empty;
            row.suspensionIssuedByAccountId = 0;
            changed = true;
            eventAction = "SuspensionExpired";
            eventDetail = "Temporary suspension expired automatically.";
        }

        if ((AdultEligibility)row.adultEligibility != AdultEligibility.Eligible && row.adultEnabled)
        {
            row.adultEnabled = false;
            changed = true;
        }

        AccountStaffRole staffRole = row.staffRole <= (byte)AccountStaffRole.Admin
            ? (AccountStaffRole)row.staffRole
            : AccountStaffRole.None;
        long expectedCapabilities = CapabilityBits(staffRole);
        if (row.staffRole != (byte)staffRole || row.staffCapabilities != expectedCapabilities)
        {
            row.staffRole = (byte)staffRole;
            row.staffCapabilities = expectedCapabilities;
            changed = true;
        }

        if (changed)
        {
            TouchPolicy(row, utcNowTicks);
            conn.Update(row);
            if (!string.IsNullOrEmpty(eventAction))
                AppendAccountEvent(conn, row.accountId, 0, eventAction, eventDetail, utcNowTicks);
        }
        return row;
    }

    private static void RecordAccountAccess(
        SQLiteConnection conn,
        long accountId,
        AccountAccessObservation access,
        long utcNowTicks)
    {
        if (accountId <= 0)
            return;

        if (access.AddressFamily != 0 &&
            access.PrefixLength != 0 &&
            !string.IsNullOrWhiteSpace(access.PrefixHash))
        {
            int changed = conn.Execute(
                "UPDATE account_ip_prefixes SET lastSeenUtcTicks=?, loginCount=loginCount+1 " +
                "WHERE accountId=? AND addressFamily=? AND prefixLength=? AND prefixHash=?",
                utcNowTicks,
                accountId,
                access.AddressFamily,
                access.PrefixLength,
                access.PrefixHash);
            if (changed == 0)
            {
                conn.Insert(new AccountIpPrefixRow
                {
                    accountId = accountId,
                    addressFamily = access.AddressFamily,
                    prefixLength = access.PrefixLength,
                    prefixHash = access.PrefixHash,
                    firstSeenUtcTicks = utcNowTicks,
                    lastSeenUtcTicks = utcNowTicks,
                    loginCount = 1,
                });
            }
            TrimOldest(conn, "account_ip_prefixes", accountId, MaxAccountIpPrefixes);
        }

        if (!string.IsNullOrWhiteSpace(access.ExactIpHash))
        {
            int changed = conn.Execute(
                "UPDATE account_ip_leaves SET lastSeenUtcTicks=?, loginCount=loginCount+1, prefixHash=? " +
                "WHERE accountId=? AND exactIpHash=?",
                utcNowTicks,
                access.PrefixHash ?? string.Empty,
                accountId,
                access.ExactIpHash);
            if (changed == 0)
            {
                conn.Insert(new AccountIpLeafRow
                {
                    accountId = accountId,
                    prefixHash = access.PrefixHash ?? string.Empty,
                    exactIpHash = access.ExactIpHash,
                    firstSeenUtcTicks = utcNowTicks,
                    lastSeenUtcTicks = utcNowTicks,
                    loginCount = 1,
                });
            }
            TrimOldest(conn, "account_ip_leaves", accountId, MaxAccountExactIps);
        }

        if (!string.IsNullOrWhiteSpace(access.DeviceHash))
        {
            int changed = conn.Execute(
                "UPDATE account_devices SET lastSeenUtcTicks=?, loginCount=loginCount+1 " +
                "WHERE accountId=? AND deviceHash=?",
                utcNowTicks,
                accountId,
                access.DeviceHash);
            if (changed == 0)
            {
                conn.Insert(new AccountDeviceRow
                {
                    accountId = accountId,
                    deviceHash = access.DeviceHash,
                    firstSeenUtcTicks = utcNowTicks,
                    lastSeenUtcTicks = utcNowTicks,
                    loginCount = 1,
                    revoked = false,
                });
            }
            TrimOldest(conn, "account_devices", accountId, MaxAccountDevices);
        }
    }

    private static void TrimOldest(SQLiteConnection conn, string tableName, long accountId, int retainCount)
    {
        // tableName is selected only from constants at the three call sites above.
        conn.Execute(
            $"DELETE FROM {tableName} WHERE accountId=? AND id NOT IN " +
            $"(SELECT id FROM {tableName} WHERE accountId=? ORDER BY lastSeenUtcTicks DESC, id DESC LIMIT {retainCount})",
            accountId,
            accountId);
    }

    private static long CapabilityBits(AccountStaffRole role) =>
        checked((long)(ulong)AccountStaffRolePolicy.CapabilitiesFor(role));

    private static void TouchPolicy(AccountPolicyRow row, long utcNowTicks)
    {
        row.revision = row.revision >= long.MaxValue ? 1 : Math.Max(1, row.revision + 1);
        row.updatedUtcTicks = utcNowTicks;
    }

    private static void AppendAccountEvent(
        SQLiteConnection conn,
        long accountId,
        long actorAccountId,
        string action,
        string detail,
        long utcNowTicks)
    {
        conn.Insert(new AccountEventRow
        {
            accountId = accountId,
            actorAccountId = actorAccountId,
            action = action ?? string.Empty,
            detail = detail ?? string.Empty,
            utcTicks = utcNowTicks,
        });
    }

    private static AccountPolicySnapshot ToSnapshot(AccountPolicyRow row) =>
        row == null
            ? null
            : new AccountPolicySnapshot
            {
                accountId = row.accountId,
                revision = row.revision,
                plan = (AccountPlan)row.plan,
                premiumUntilUtcTicks = row.premiumUntilUtcTicks,
                email = row.email ?? string.Empty,
                emailVerified = row.emailVerified,
                emailVerifiedUtcTicks = row.emailVerifiedUtcTicks,
                adultEligibility = (AdultEligibility)row.adultEligibility,
                adultEnabled = row.adultEnabled,
                staffRole = (AccountStaffRole)row.staffRole,
                staffCapabilities = unchecked((ulong)row.staffCapabilities),
                accessState = (AccountAccessState)row.accessState,
                strikeLevel = row.strikeLevel,
                banKind = (AccountBanKind)row.banKind,
                banExpiresUtcTicks = row.banExpiresUtcTicks,
                banReason = row.banReason ?? string.Empty,
                banIssuedByAccountId = row.banIssuedByAccountId,
                suspensionExpiresUtcTicks = row.suspensionExpiresUtcTicks,
                suspensionReason = row.suspensionReason ?? string.Empty,
                suspensionIssuedByAccountId = row.suspensionIssuedByAccountId,
                updatedUtcTicks = row.updatedUtcTicks,
            };

    [Table("account_policy")]
    private sealed class AccountPolicyRow
    {
        [PrimaryKey] public long accountId { get; set; }
        public long revision { get; set; }
        public byte plan { get; set; }
        public long premiumUntilUtcTicks { get; set; }
        [NotNull] public string email { get; set; }
        [NotNull] public string emailKey { get; set; }
        public bool emailVerified { get; set; }
        public long emailVerifiedUtcTicks { get; set; }
        public byte adultEligibility { get; set; }
        public bool adultEnabled { get; set; }
        public byte staffRole { get; set; }
        public long staffCapabilities { get; set; }
        public byte accessState { get; set; }
        public int strikeLevel { get; set; }
        public byte banKind { get; set; }
        public long banExpiresUtcTicks { get; set; }
        [NotNull] public string banReason { get; set; }
        public long banIssuedByAccountId { get; set; }
        public long suspensionExpiresUtcTicks { get; set; }
        [NotNull] public string suspensionReason { get; set; }
        public long suspensionIssuedByAccountId { get; set; }
        public long updatedUtcTicks { get; set; }
    }

    [Table("account_ip_prefixes")]
    private sealed class AccountIpPrefixRow
    {
        [PrimaryKey, AutoIncrement] public long id { get; set; }
        public long accountId { get; set; }
        public byte addressFamily { get; set; }
        public byte prefixLength { get; set; }
        [NotNull] public string prefixHash { get; set; }
        public long firstSeenUtcTicks { get; set; }
        public long lastSeenUtcTicks { get; set; }
        public long loginCount { get; set; }
    }

    [Table("account_ip_leaves")]
    private sealed class AccountIpLeafRow
    {
        [PrimaryKey, AutoIncrement] public long id { get; set; }
        public long accountId { get; set; }
        [NotNull] public string prefixHash { get; set; }
        [NotNull] public string exactIpHash { get; set; }
        public long firstSeenUtcTicks { get; set; }
        public long lastSeenUtcTicks { get; set; }
        public long loginCount { get; set; }
    }

    [Table("account_devices")]
    private sealed class AccountDeviceRow
    {
        [PrimaryKey, AutoIncrement] public long id { get; set; }
        public long accountId { get; set; }
        [NotNull] public string deviceHash { get; set; }
        public long firstSeenUtcTicks { get; set; }
        public long lastSeenUtcTicks { get; set; }
        public long loginCount { get; set; }
        public bool revoked { get; set; }
    }

    [Table("account_events")]
    private sealed class AccountEventRow
    {
        [PrimaryKey, AutoIncrement] public long id { get; set; }
        public long accountId { get; set; }
        public long actorAccountId { get; set; }
        [NotNull] public string action { get; set; }
        [NotNull] public string detail { get; set; }
        public long utcTicks { get; set; }
    }
}
