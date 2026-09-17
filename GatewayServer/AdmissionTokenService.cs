using System.Security.Cryptography;
using System.Text;
using Game.Shared.Backend;

namespace Game.BackendServer;

internal sealed class AdmissionTokenService
{
    private readonly BackendDatabaseDispatcher _database;
    private readonly TimeSpan _lifetime;

    public AdmissionTokenService(BackendDatabaseDispatcher database, TimeSpan lifetime)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _lifetime = lifetime;
    }

    public IssuedAdmission Issue(long accountId)
    {
        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
        string token = Base64Url.Encode(tokenBytes);
        string hash = HashToken(token);
        long issuedTicks = DateTime.UtcNow.Ticks;
        long expiresTicks = issuedTicks + _lifetime.Ticks;
        CryptographicOperations.ZeroMemory(tokenBytes);

        if (!_database.TryExecute(
                DatabaseWorkPriority.Normal,
                db =>
                {
                    db.StoreAdmission(hash, accountId, issuedTicks, expiresTicks);
                    return true;
                },
                out bool stored) ||
            !stored)
        {
            return null;
        }

        return new IssuedAdmission(token, expiresTicks);
    }

    public bool TryQueueRedeem(string token, out Task<BackendAdmissionRedeemResponse> completion)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
        {
            completion = Task.FromResult(new BackendAdmissionRedeemResponse
            {
                success = false,
                accountId = 0,
                policy = null,
                error = "admission unavailable",
            });
            return true;
        }

        string tokenHash = HashToken(token);
        long nowTicks = DateTime.UtcNow.Ticks;
        return _database.TryQueue(
            DatabaseWorkPriority.Critical,
            db => db.TryConsumeAdmission(tokenHash, nowTicks),
            out completion);
    }

    private static string HashToken(string token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        byte[] hash = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        try { return Convert.ToHexString(hash); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
}

internal sealed record IssuedAdmission(string Token, long ExpiresUtcTicks);
