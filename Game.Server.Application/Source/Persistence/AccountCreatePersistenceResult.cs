using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public readonly struct AccountCreatePersistenceResult
    {
        public bool Success { get; }
        public bool NameAlreadyExists { get; }
        public AccountId AccountId { get; }

        private AccountCreatePersistenceResult(bool success, bool nameAlreadyExists, AccountId accountId)
        {
            Success = success;
            NameAlreadyExists = nameAlreadyExists;
            AccountId = accountId;
        }

        public static AccountCreatePersistenceResult Created(AccountId accountId) =>
            new AccountCreatePersistenceResult(true, false, accountId);

        public static AccountCreatePersistenceResult DuplicateName() =>
            new AccountCreatePersistenceResult(false, true, default(AccountId));

        public static AccountCreatePersistenceResult Failed() =>
            new AccountCreatePersistenceResult(false, false, default(AccountId));
    }
}
