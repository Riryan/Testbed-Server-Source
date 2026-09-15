using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Shared.Authentication;

namespace Game.Server.Application.Authentication
{
    /// <summary>
    /// Legacy verifier-based service retained to regression-test migration-compatible
    /// account repositories. It is not part of deployed authentication; BackendServer owns
    /// raw-password validation, server-side hashing, and admission-token issuance.
    /// </summary>
    public sealed class AccountAuthenticationService : IAccountAuthenticationService
    {
        private readonly IAccountRepository _accounts;

        public AccountAuthenticationService(IAccountRepository accounts)
        {
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        }

        public async Task<AccountAuthenticationResult> LoginAsync(
            string account,
            string passwordVerifier,
            CancellationToken cancellationToken)
        {
            if (!CredentialsAreWellFormed(account, passwordVerifier))
                return AccountAuthenticationResult.Failed("login unavailable");

            AccountPersistenceRecord record = await _accounts
                .FindByNameAsync(account, cancellationToken)
                .ConfigureAwait(false);

            if (record == null || !FixedTimeEquals(record.PasswordVerifier, passwordVerifier))
                return AccountAuthenticationResult.Failed("login unavailable");

            await _accounts.TouchLastLoginAsync(
                    record.AccountId,
                    DateTime.UtcNow.Ticks,
                    cancellationToken)
                .ConfigureAwait(false);

            return AccountAuthenticationResult.Succeeded(record.AccountId, false);
        }

        public async Task<AccountAuthenticationResult> CreateAsync(
            string account,
            string passwordVerifier,
            CancellationToken cancellationToken)
        {
            if (!CredentialsAreWellFormed(account, passwordVerifier))
                return AccountAuthenticationResult.Failed("account creation unavailable");

            AccountCreatePersistenceResult created = await _accounts
                .TryCreateAsync(account, passwordVerifier.ToUpperInvariant(), DateTime.UtcNow.Ticks, cancellationToken)
                .ConfigureAwait(false);

            if (!created.Success)
            {
                return AccountAuthenticationResult.Failed(
                    created.NameAlreadyExists ? "account already exists" : "account creation unavailable");
            }

            return AccountAuthenticationResult.Succeeded(created.AccountId, true);
        }

        private static bool CredentialsAreWellFormed(string account, string passwordVerifier) =>
            AccountCredentialPolicy.IsAllowedAccountName(account) &&
            AccountCredentialPolicy.IsVerifierWellFormed(passwordVerifier);

        internal static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null)
                return false;

            int diff = left.Length ^ right.Length;
            int count = Math.Min(left.Length, right.Length);
            for (int i = 0; i < count; ++i)
                diff |= char.ToUpperInvariant(left[i]) ^ char.ToUpperInvariant(right[i]);
            return diff == 0;
        }
    }
}
