using System.Text.Json;
using Game.Shared.Authentication;
using Game.Shared.Backend;
using Game.Shared.Characters;
using Game.Shared.Content;
using Game.Shared.Protocol;
using Game.Shared.Progression;
using SQLite;

namespace Game.BackendServer;

internal sealed partial class BackendDatabase
{
    // Account credential and admission-token persistence domain.
    public AccountSnapshot FindAccount(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return null;

        string key = AccountCredentialPolicy.NormalizeAccountKey(accountName);
        return Execute(conn =>
        {
            AccountRow account = conn.FindWithQuery<AccountRow>(
                "SELECT * FROM accounts WHERE nameKey=? LIMIT 1", key);
            if (account == null)
                return null;

            CredentialRow credential = conn.Find<CredentialRow>(account.accountId);
            return new AccountSnapshot(
                account.accountId,
                account.name,
                account.passwordVerifier,
                credential == null
                    ? null
                    : new PasswordCredential(
                        credential.algorithm,
                        credential.iterations,
                        credential.saltBase64,
                        credential.hashBase64,
                        credential.version));
        });
    }

    public long TryCreateAccount(
        string accountName,
        PasswordCredential credential,
        long utcNowTicks)
    {
        if (credential == null)
            throw new ArgumentNullException(nameof(credential));

        string key = AccountCredentialPolicy.NormalizeAccountKey(accountName);
        long createdAccountId = 0;

        try
        {
            Execute(conn =>
            {
                conn.RunInTransaction(() =>
                {
                    if (conn.FindWithQuery<AccountRow>(
                            "SELECT * FROM accounts WHERE nameKey=? LIMIT 1", key) != null)
                        return;

                    var row = new AccountRow
                    {
                        name = accountName,
                        nameKey = key,
                        // Compatibility placeholder for accounts created by the backend.
                        // New authentication never treats this column as the credential.
                        passwordVerifier = "MMO-BACKEND-V1",
                        createdUtcTicks = utcNowTicks,
                        lastLoginUtcTicks = utcNowTicks,
                    };

                    if (conn.Insert(row) != 1 || row.accountId <= 0)
                        return;

                    if (conn.Insert(CredentialRow.From(row.accountId, credential, utcNowTicks)) != 1)
                        throw new InvalidOperationException("Credential insert failed.");

                    createdAccountId = row.accountId;
                });
            });
        }
        catch (SQLiteException)
        {
            // Unique index remains authoritative for concurrent create attempts.
            return 0;
        }

        return createdAccountId;
    }

    public void StoreCredential(long accountId, PasswordCredential credential, long utcNowTicks)
    {
        if (accountId <= 0 || credential == null)
            return;

        Execute(conn => conn.InsertOrReplace(CredentialRow.From(accountId, credential, utcNowTicks)));
    }

    public void TouchLastLogin(long accountId, long utcNowTicks)
    {
        if (accountId <= 0)
            return;

        Execute(conn =>
            conn.Execute(
                "UPDATE accounts SET lastLoginUtcTicks=? WHERE accountId=?",
                utcNowTicks,
                accountId));
    }

    // -------------------------------------------------------------------------
    // One-time admissions
    // -------------------------------------------------------------------------

    public void StoreAdmission(
        string tokenHash,
        long accountId,
        long issuedUtcTicks,
        long expiresUtcTicks)
    {
        Execute(conn =>
        {
            DeleteExpiredAdmissionsIfDue(conn, issuedUtcTicks);
            conn.Insert(new AdmissionRow
            {
                tokenHash = tokenHash,
                accountId = accountId,
                issuedUtcTicks = issuedUtcTicks,
                expiresUtcTicks = expiresUtcTicks,
                consumedUtcTicks = 0,
            });
        });
    }

    public long TryConsumeAdmission(string tokenHash, long utcNowTicks)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
            return 0;

        long accountId = 0;
        Execute(conn =>
        {
            conn.RunInTransaction(() =>
            {
                AdmissionRow row = conn.Find<AdmissionRow>(tokenHash);
                if (row == null ||
                    row.consumedUtcTicks != 0 ||
                    row.expiresUtcTicks <= utcNowTicks)
                    return;

                int changed = conn.Execute(
                    "UPDATE auth_admissions SET consumedUtcTicks=? " +
                    "WHERE tokenHash=? AND consumedUtcTicks=0 AND expiresUtcTicks>?",
                    utcNowTicks,
                    tokenHash,
                    utcNowTicks);
                if (changed == 1)
                    accountId = row.accountId;
            });

            DeleteExpiredAdmissionsIfDue(conn, utcNowTicks);
        });
        return accountId;
    }

    private void DeleteExpiredAdmissionsIfDue(SQLiteConnection conn, long utcNowTicks)
    {
        if (utcNowTicks < _nextAdmissionCleanupUtcTicks)
            return;

        _nextAdmissionCleanupUtcTicks = utcNowTicks > long.MaxValue - AdmissionCleanupIntervalTicks
            ? long.MaxValue
            : utcNowTicks + AdmissionCleanupIntervalTicks;

        conn.Execute(
            "DELETE FROM auth_admissions WHERE expiresUtcTicks<? OR " +
            "(consumedUtcTicks<>0 AND consumedUtcTicks<?)",
            utcNowTicks,
            utcNowTicks);
    }

    // -------------------------------------------------------------------------
    // Characters
    // -------------------------------------------------------------------------
}
