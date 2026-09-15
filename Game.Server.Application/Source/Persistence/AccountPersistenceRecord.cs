using System;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public sealed class AccountPersistenceRecord
    {
        public AccountId AccountId { get; }
        public string Name { get; }
        public string PasswordVerifier { get; }
        public long CreatedUtcTicks { get; }
        public long LastLoginUtcTicks { get; }

        public AccountPersistenceRecord(
            AccountId accountId,
            string name,
            string passwordVerifier,
            long createdUtcTicks,
            long lastLoginUtcTicks)
        {
            if (!accountId.IsValid)
                throw new ArgumentException("AccountId is invalid.", nameof(accountId));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Account name is required.", nameof(name));
            if (string.IsNullOrWhiteSpace(passwordVerifier))
                throw new ArgumentException("Password verifier is required.", nameof(passwordVerifier));

            AccountId = accountId;
            Name = name;
            PasswordVerifier = passwordVerifier;
            CreatedUtcTicks = createdUtcTicks;
            LastLoginUtcTicks = lastLoginUtcTicks;
        }
    }
}
