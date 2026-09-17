using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Game.BackendServer;

/// <summary>
/// Produces privacy-bounded account-link signals. Raw network addresses and install ids
/// never enter SQLite; only stable keyed hashes are stored. IPv4 is grouped by /24 and
/// IPv6 by /64 so routine DHCP/interface churn updates the existing branch instead of
/// growing one unrelated history record per address.
/// </summary>
internal sealed class AccountAccessTelemetry : IDisposable
{
    private readonly byte[] _hmacKey;

    public AccountAccessTelemetry(string hmacKey)
    {
        if (string.IsNullOrWhiteSpace(hmacKey))
            throw new ArgumentException("Account telemetry HMAC key is required.", nameof(hmacKey));
        _hmacKey = Encoding.UTF8.GetBytes(hmacKey.Trim());
    }

    public AccountAccessObservation Create(string remoteIp, string deviceId)
    {
        string exactHash = string.Empty;
        string prefixHash = string.Empty;
        byte family = 0;
        byte prefixLength = 0;

        if (IPAddress.TryParse((remoteIp ?? string.Empty).Trim(), out IPAddress parsed))
        {
            if (parsed.IsIPv4MappedToIPv6)
                parsed = parsed.MapToIPv4();

            byte[] exactBytes = parsed.GetAddressBytes();
            try
            {
                if (parsed.AddressFamily == AddressFamily.InterNetwork && exactBytes.Length == 4)
                {
                    family = 4;
                    prefixLength = 24;
                    byte[] prefixBytes = (byte[])exactBytes.Clone();
                    prefixBytes[3] = 0;
                    try
                    {
                        exactHash = Hash("ipv4-exact", exactBytes);
                        prefixHash = Hash("ipv4-prefix-24", prefixBytes);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(prefixBytes);
                    }
                }
                else if (parsed.AddressFamily == AddressFamily.InterNetworkV6 && exactBytes.Length == 16)
                {
                    family = 6;
                    prefixLength = 64;
                    byte[] prefixBytes = (byte[])exactBytes.Clone();
                    for (int i = 8; i < prefixBytes.Length; ++i)
                        prefixBytes[i] = 0;
                    try
                    {
                        exactHash = Hash("ipv6-exact", exactBytes);
                        prefixHash = Hash("ipv6-prefix-64", prefixBytes);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(prefixBytes);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exactBytes);
            }
        }

        string preparedDevice = (deviceId ?? string.Empty).Trim();
        string deviceHash = preparedDevice.Length is >= 8 and <= 128
            ? Hash("install-id", Encoding.UTF8.GetBytes(preparedDevice), zeroPayload: true)
            : string.Empty;

        return new AccountAccessObservation(
            family,
            prefixLength,
            prefixHash,
            exactHash,
            deviceHash);
    }

    private string Hash(string domain, byte[] payload, bool zeroPayload = false)
    {
        byte[] domainBytes = Encoding.UTF8.GetBytes(domain ?? string.Empty);
        byte[] input = new byte[domainBytes.Length + 1 + payload.Length];
        Buffer.BlockCopy(domainBytes, 0, input, 0, domainBytes.Length);
        Buffer.BlockCopy(payload, 0, input, domainBytes.Length + 1, payload.Length);
        byte[] digest;
        using (var hmac = new HMACSHA256(_hmacKey))
            digest = hmac.ComputeHash(input);
        try
        {
            return Convert.ToHexString(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domainBytes);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
            if (zeroPayload)
                CryptographicOperations.ZeroMemory(payload);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_hmacKey);
    }
}

internal readonly record struct AccountAccessObservation(
    byte AddressFamily,
    byte PrefixLength,
    string PrefixHash,
    string ExactIpHash,
    string DeviceHash);
