using Game.Shared.Accounts;
using Game.Shared.Authentication;

namespace Game.BackendServer;

internal sealed class AccountAuthService
{
    private readonly BackendDatabaseDispatcher _database;
    private readonly AdmissionTokenService _admissions;
    private readonly BackendOptions _options;
    private readonly AccountAccessTelemetry _telemetry;

    public AccountAuthService(
        BackendDatabaseDispatcher database,
        AdmissionTokenService admissions,
        BackendOptions options,
        AccountAccessTelemetry telemetry)
    {
        _database = database;
        _admissions = admissions;
        _options = options;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public AuthOperationResult Create(
        string account,
        string password,
        string remoteIp,
        string deviceId)
    {
        if (!AccountCredentialPolicy.IsAllowedAccountName(account) ||
            !AccountCredentialPolicy.IsAllowedPasswordForCreation(password))
            return AuthOperationResult.Failed();

        PasswordCredential credential = PasswordHasher.Create(password, _options.Pbkdf2Iterations);
        long now = DateTime.UtcNow.Ticks;
        if (!_database.TryExecute(
                DatabaseWorkPriority.Normal,
                db => db.TryCreateAccount(account, credential, now),
                out long accountId) ||
            accountId <= 0)
        {
            return AuthOperationResult.Failed();
        }

        AccountAccessObservation observation = _telemetry.Create(remoteIp, deviceId);
        if (!_database.TryExecute(
                DatabaseWorkPriority.Normal,
                db => db.PrepareAuthenticatedAccount(
                    accountId,
                    account,
                    _options.DevelopmentAdminAccount,
                    observation,
                    now),
                out AccountPolicySnapshot policy) ||
            policy == null ||
            !policy.IsAccessAllowedAt(now))
        {
            return AuthOperationResult.Failed();
        }

        IssuedAdmission admission = _admissions.Issue(accountId);
        return admission != null
            ? AuthOperationResult.Succeeded(accountId, admission, policy)
            : AuthOperationResult.Failed();
    }

    public AuthOperationResult Login(
        string account,
        string password,
        string remoteIp,
        string deviceId)
    {
        if (!AccountCredentialPolicy.IsAllowedAccountName(account) ||
            !AccountCredentialPolicy.IsAllowedPasswordForLogin(password))
        {
            PasswordHasher.PerformDummyVerification(password ?? string.Empty, _options.Pbkdf2Iterations);
            return AuthOperationResult.Failed();
        }

        if (!_database.TryExecute(
                DatabaseWorkPriority.Normal,
                db => db.FindAccount(account),
                out AccountSnapshot snapshot) ||
            snapshot == null)
        {
            PasswordHasher.PerformDummyVerification(password, _options.Pbkdf2Iterations);
            return AuthOperationResult.Failed();
        }

        bool needsMigration = snapshot.Credential == null;
        bool needsUpgrade = snapshot.Credential != null &&
                            snapshot.Credential.Iterations < _options.Pbkdf2Iterations;
        bool verified = snapshot.Credential != null
            ? PasswordHasher.Verify(password, snapshot.Credential)
            : LegacyPasswordVerifier.Verify(snapshot.Name, password, snapshot.LegacyPasswordVerifier);

        if (!verified)
            return AuthOperationResult.Failed();

        long now = DateTime.UtcNow.Ticks;
        PasswordCredential migrated = needsMigration || needsUpgrade
            ? PasswordHasher.Create(password, _options.Pbkdf2Iterations)
            : null;
        AccountAccessObservation observation = _telemetry.Create(remoteIp, deviceId);

        if (!_database.TryExecute(
                DatabaseWorkPriority.Normal,
                db =>
                {
                    if (migrated != null)
                        db.StoreCredential(snapshot.AccountId, migrated, now);
                    db.TouchLastLogin(snapshot.AccountId, now);
                    return db.PrepareAuthenticatedAccount(
                        snapshot.AccountId,
                        snapshot.Name,
                        _options.DevelopmentAdminAccount,
                        observation,
                        now);
                },
                out AccountPolicySnapshot policy) ||
            policy == null ||
            !policy.IsAccessAllowedAt(now))
        {
            return AuthOperationResult.Failed();
        }

        IssuedAdmission admission = _admissions.Issue(snapshot.AccountId);
        return admission != null
            ? AuthOperationResult.Succeeded(snapshot.AccountId, admission, policy)
            : AuthOperationResult.Failed();
    }
}

internal sealed record AuthOperationResult(
    bool Success,
    long AccountId,
    IssuedAdmission Admission,
    AccountPolicySnapshot Policy)
{
    public static AuthOperationResult Failed() => new(false, 0, null, null);
    public static AuthOperationResult Succeeded(
        long accountId,
        IssuedAdmission admission,
        AccountPolicySnapshot policy) =>
        new(true, accountId, admission, policy);
}
