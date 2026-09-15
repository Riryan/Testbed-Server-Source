using Game.Shared.Authentication;

namespace Game.BackendServer;

internal sealed class AccountAuthService
{
    private readonly BackendDatabaseDispatcher _database;
    private readonly AdmissionTokenService _admissions;
    private readonly BackendOptions _options;

    public AccountAuthService(BackendDatabaseDispatcher database, AdmissionTokenService admissions, BackendOptions options)
    {
        _database = database;
        _admissions = admissions;
        _options = options;
    }

    public AuthOperationResult Create(string account, string password)
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

        IssuedAdmission admission = _admissions.Issue(accountId);
        return admission != null
            ? AuthOperationResult.Succeeded(accountId, admission)
            : AuthOperationResult.Failed();
    }

    public AuthOperationResult Login(string account, string password)
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
        // Successful login is the cheapest safe time to raise an older PBKDF2 cost.
        // Never lower a credential whose stored iteration count is already higher.
        PasswordCredential migrated = needsMigration || needsUpgrade
            ? PasswordHasher.Create(password, _options.Pbkdf2Iterations)
            : null;

        if (!_database.TryExecute(
                DatabaseWorkPriority.Normal,
                db =>
                {
                    if (migrated != null)
                        db.StoreCredential(snapshot.AccountId, migrated, now);
                    db.TouchLastLogin(snapshot.AccountId, now);
                    return true;
                },
                out bool updated) ||
            !updated)
        {
            return AuthOperationResult.Failed();
        }

        IssuedAdmission admission = _admissions.Issue(snapshot.AccountId);
        return admission != null
            ? AuthOperationResult.Succeeded(snapshot.AccountId, admission)
            : AuthOperationResult.Failed();
    }

}

internal sealed record AuthOperationResult(bool Success, long AccountId, IssuedAdmission Admission)
{
    public static AuthOperationResult Failed() => new(false, 0, null);
    public static AuthOperationResult Succeeded(long accountId, IssuedAdmission admission) =>
        new(true, accountId, admission);
}
