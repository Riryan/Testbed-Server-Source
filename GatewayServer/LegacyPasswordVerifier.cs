using System.Security.Cryptography;
using System.Text;

namespace Game.BackendServer;

internal static class LegacyPasswordVerifier
{
    private const int Iterations = 10_000;
    private const string SaltPrefix = "at_least_16_byte";

    public static bool Verify(string account, string password, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(account) ||
            string.IsNullOrEmpty(password) ||
            string.IsNullOrWhiteSpace(expectedHex) ||
            expectedHex.Length != 40)
            return false;

        byte[] salt = Encoding.UTF8.GetBytes(SaltPrefix + account);
        byte[] hash = null;
        byte[] expected = null;
        try
        {
            hash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA1,
                20);
            try
            {
                expected = Convert.FromHexString(expectedHex);
            }
            catch (FormatException)
            {
                return false;
            }
            return expected.Length == hash.Length &&
                   CryptographicOperations.FixedTimeEquals(hash, expected);
        }
        finally
        {
            if (hash != null)
                CryptographicOperations.ZeroMemory(hash);
            if (expected != null)
                CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(salt);
        }
    }
}
