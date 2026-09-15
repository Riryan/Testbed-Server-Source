using Game.Shared.Identity;

namespace Game.Server.Application.Authentication
{
    public readonly struct AccountAuthenticationResult
    {
        public bool Success { get; }
        public AccountId AccountId { get; }
        public bool AccountCreated { get; }
        public string Error { get; }

        private AccountAuthenticationResult(bool success, AccountId accountId, bool created, string error)
        {
            Success = success;
            AccountId = accountId;
            AccountCreated = created;
            Error = error ?? string.Empty;
        }

        public static AccountAuthenticationResult Succeeded(AccountId accountId, bool created) =>
            new AccountAuthenticationResult(true, accountId, created, string.Empty);

        public static AccountAuthenticationResult Failed(string error) =>
            new AccountAuthenticationResult(false, default(AccountId), false, error);
    }
}
