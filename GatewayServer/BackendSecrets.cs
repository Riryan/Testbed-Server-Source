using System.Security.Cryptography;
using System.Text.Json;

namespace Game.BackendServer;

internal sealed class BackendSecrets
{
    public string GameServerKey { get; set; }
    public string CertificatePassword { get; set; }
    public string AccountTelemetryHmacKey { get; set; }

    public static BackendSecrets LoadOrCreate(string secretsPath, string gameServerKeyPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(secretsPath) ?? ".");
        Directory.CreateDirectory(Path.GetDirectoryName(gameServerKeyPath) ?? ".");

        BackendSecrets secrets = null;
        if (File.Exists(secretsPath))
        {
            secrets = JsonSerializer.Deserialize<BackendSecrets>(
                File.ReadAllText(secretsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        string existingGameServerKey = File.Exists(gameServerKeyPath)
            ? File.ReadAllText(gameServerKeyPath).Trim()
            : string.Empty;

        secrets ??= new BackendSecrets();
        bool changed = false;
        if (string.IsNullOrWhiteSpace(secrets.GameServerKey))
        {
            // Preserve the key created by the earlier AuthServer foundation when
            // upgrading in place. Generate a new one only for a fresh deployment.
            secrets.GameServerKey = !string.IsNullOrWhiteSpace(existingGameServerKey)
                ? existingGameServerKey
                : Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(secrets.CertificatePassword))
        {
            secrets.CertificatePassword = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(secrets.AccountTelemetryHmacKey))
        {
            // Keyed hashing keeps stored IP/device observations comparable for moderation
            // without retaining raw addresses or client installation identifiers.
            secrets.AccountTelemetryHmacKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
            changed = true;
        }

        if (changed || !File.Exists(secretsPath))
        {
            File.WriteAllText(
                secretsPath,
                JsonSerializer.Serialize(secrets, new JsonSerializerOptions { WriteIndented = true }));
        }

        PrivateFilePermissions.RestrictOwnerOnly(secretsPath);
        File.WriteAllText(gameServerKeyPath, secrets.GameServerKey + Environment.NewLine);
        PrivateFilePermissions.RestrictOwnerOnly(gameServerKeyPath);
        return secrets;
    }
}
