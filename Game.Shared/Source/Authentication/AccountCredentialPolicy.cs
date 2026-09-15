using System;
using System.Security.Cryptography;
using System.Text;

namespace Game.Shared.Authentication
{
    /// <summary>
    /// Shared credential input validation plus the legacy production verifier helper.
    /// New authentication sends the raw password only inside HTTPS to BackendServer, which
    /// hashes it server-side. ComputePasswordVerifier remains solely for migration/tests
    /// of existing uMMORPG-compatible credential records.
    /// </summary>
    public static class AccountCredentialPolicy
    {
        public const int AccountMaxLength = 16;
        public const int PasswordMinLength = 8;
        public const int PasswordMaxLength = 128;
        public const int VerifierHexLength = 40;
        public const int Pbkdf2Iterations = 10000;
        public const string PasswordSaltPrefix = "at_least_16_byte";

        public static bool IsAllowedAccountName(string account)
        {
            if (string.IsNullOrEmpty(account) || account.Length > AccountMaxLength)
                return false;

            for (int i = 0; i < account.Length; ++i)
            {
                char c = account[i];
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                bool digit = c >= '0' && c <= '9';
                if (!letter && !digit && c != '_')
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Login remains compatible with existing accounts that may predate the
        /// stronger creation minimum. The backend remains authoritative.
        /// </summary>
        public static bool IsAllowedPasswordForLogin(string password) =>
            !string.IsNullOrEmpty(password) &&
            password.Length <= PasswordMaxLength;

        public static bool IsAllowedPasswordForCreation(string password) =>
            !string.IsNullOrEmpty(password) &&
            password.Length >= PasswordMinLength &&
            password.Length <= PasswordMaxLength;

        // Compatibility alias used by the legacy verifier/migration tests.
        public static bool IsAllowedPassword(string password) =>
            IsAllowedPasswordForLogin(password);

        public static string NormalizeAccountKey(string account) =>
            string.IsNullOrWhiteSpace(account)
                ? string.Empty
                : account.Trim().ToUpperInvariant();

        public static string ComputePasswordVerifier(string account, string password)
        {
            if (!IsAllowedAccountName(account))
                throw new ArgumentException("Account name is invalid.", nameof(account));
            if (!IsAllowedPasswordForLogin(password))
                throw new ArgumentException("Password is invalid.", nameof(password));

            byte[] salt = Encoding.UTF8.GetBytes(PasswordSaltPrefix + account);
            byte[] hash = null;
            try
            {
#pragma warning disable SYSLIB0041 // Unity/production compatibility intentionally matches the existing PBKDF2/SHA1 verifier.
                using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Pbkdf2Iterations))
#pragma warning restore SYSLIB0041
                {
                    hash = pbkdf2.GetBytes(20);
                }

                var builder = new StringBuilder(VerifierHexLength);
                for (int i = 0; i < hash.Length; ++i)
                    builder.Append(hash[i].ToString("X2"));
                return builder.ToString();
            }
            finally
            {
                if (hash != null)
                    Array.Clear(hash, 0, hash.Length);
                Array.Clear(salt, 0, salt.Length);
            }
        }

        public static bool IsVerifierWellFormed(string verifier)
        {
            if (string.IsNullOrEmpty(verifier) || verifier.Length != VerifierHexLength)
                return false;

            for (int i = 0; i < verifier.Length; ++i)
            {
                char c = verifier[i];
                bool digit = c >= '0' && c <= '9';
                bool upper = c >= 'A' && c <= 'F';
                bool lower = c >= 'a' && c <= 'f';
                if (!digit && !upper && !lower)
                    return false;
            }

            return true;
        }
    }
}
