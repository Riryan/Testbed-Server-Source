using System.Net;
using System.Security.Cryptography;
using System.Text;
using Game.Shared.Backend;

namespace Game.BackendServer;

internal static class InternalAuthorization
{
    public static bool IsAuthorized(HttpContext context, int internalPort, string expectedKey)
    {
        if (context.Connection.LocalPort != internalPort ||
            context.Connection.RemoteIpAddress == null ||
            !IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
            return false;

        string supplied = context.Request.Headers[BackendServiceContracts.GameServerKeyHeader].ToString();
        return FixedTimeStringEquals(supplied, expectedKey);
    }

    private static bool FixedTimeStringEquals(string left, string right)
    {
        byte[] leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left ?? string.Empty));
        byte[] rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right ?? string.Empty));
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftHash);
            CryptographicOperations.ZeroMemory(rightHash);
        }
    }
}
