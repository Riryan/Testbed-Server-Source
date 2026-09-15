using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Authentication;
using Game.Shared.Identity;

namespace Game.Server.Application.Authentication
{
    /// <summary>
    /// Process-local verifier test double retained only for migration/regression tests.
    /// No deployed Unity runtime composition uses this authentication implementation.
    /// </summary>
    public sealed class InMemoryAccountAuthenticationService : IAccountAuthenticationService
    {
        private sealed class AccountRecord
        {
            public AccountId Id;
            public string Verifier;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, AccountRecord> _accounts =
            new Dictionary<string, AccountRecord>(StringComparer.Ordinal);
        private long _nextAccountId = 1;

        public Task<AccountAuthenticationResult> LoginAsync(
            string account,
            string passwordVerifier,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CredentialsAreWellFormed(account, passwordVerifier))
                return Task.FromResult(AccountAuthenticationResult.Failed("login unavailable"));

            lock (_gate)
            {
                if (!_accounts.TryGetValue(account, out AccountRecord existing) ||
                    !AccountAuthenticationService.FixedTimeEquals(existing.Verifier, passwordVerifier))
                {
                    return Task.FromResult(AccountAuthenticationResult.Failed("login unavailable"));
                }

                return Task.FromResult(AccountAuthenticationResult.Succeeded(existing.Id, false));
            }
        }

        public Task<AccountAuthenticationResult> CreateAsync(
            string account,
            string passwordVerifier,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CredentialsAreWellFormed(account, passwordVerifier))
                return Task.FromResult(AccountAuthenticationResult.Failed("account creation unavailable"));

            lock (_gate)
            {
                if (_accounts.ContainsKey(account))
                    return Task.FromResult(AccountAuthenticationResult.Failed("account already exists"));

                var id = new AccountId(_nextAccountId++);
                _accounts.Add(account, new AccountRecord
                {
                    Id = id,
                    Verifier = passwordVerifier.ToUpperInvariant(),
                });
                return Task.FromResult(AccountAuthenticationResult.Succeeded(id, true));
            }
        }

        // Compatibility helper retained for existing tests. Runtime/UI code uses explicit
        // CreateAsync and LoginAsync instead of implicit login-or-create.
        public AccountAuthenticationResult LoginOrCreate(string account, string passwordVerifier)
        {
            lock (_gate)
            {
                if (_accounts.TryGetValue(account, out AccountRecord existing))
                {
                    if (!CredentialsAreWellFormed(account, passwordVerifier) ||
                        !AccountAuthenticationService.FixedTimeEquals(existing.Verifier, passwordVerifier))
                    {
                        return AccountAuthenticationResult.Failed("login unavailable");
                    }
                    return AccountAuthenticationResult.Succeeded(existing.Id, false);
                }
            }

            return CreateAsync(account, passwordVerifier, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private static bool CredentialsAreWellFormed(string account, string passwordVerifier) =>
            AccountCredentialPolicy.IsAllowedAccountName(account) &&
            AccountCredentialPolicy.IsVerifierWellFormed(passwordVerifier);
    }
}
