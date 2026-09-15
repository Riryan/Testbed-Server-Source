using System.Security.Cryptography;
using System.Text;

namespace Game.BackendServer;

internal static class PasswordHasher
{
    public const string Algorithm = "PBKDF2-HMAC-SHA256";
    public const int Version = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static PasswordCredential Create(string password, int iterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        try
        {
            return new PasswordCredential(
                Algorithm,
                iterations,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(hash),
                Version);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static bool Verify(string password, PasswordCredential credential)
    {
        if (credential == null ||
            !string.Equals(credential.Algorithm, Algorithm, StringComparison.Ordinal) ||
            credential.Iterations < 100_000 ||
            credential.Version != Version)
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(credential.SaltBase64);
            expected = Convert.FromBase64String(credential.HashBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = null;
        try
        {
            actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                credential.Iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            if (actual != null)
                CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static void PerformDummyVerification(string password, int iterations)
    {
        byte[] fullSalt = SHA256.HashData(Encoding.UTF8.GetBytes("Backend Server dummy credential"));
        byte[] salt = new byte[SaltBytes];
        Buffer.BlockCopy(fullSalt, 0, salt, 0, salt.Length);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password ?? string.Empty,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        CryptographicOperations.ZeroMemory(hash);
        CryptographicOperations.ZeroMemory(salt);
        CryptographicOperations.ZeroMemory(fullSalt);
    }
}

internal sealed record PasswordCredential(
    string Algorithm,
    int Iterations,
    string SaltBase64,
    string HashBase64,
    int Version);
