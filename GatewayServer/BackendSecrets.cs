using System.Security.Cryptography;
using System.Text.Json;

namespace Game.BackendServer;

internal sealed class BackendSecrets
{
    public string GameServerKey { get; set; }
    public string CertificatePassword { get; set; }

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

        if (secrets == null ||
            string.IsNullOrWhiteSpace(secrets.GameServerKey) ||
            string.IsNullOrWhiteSpace(secrets.CertificatePassword))
        {
            string existingGameServerKey = File.Exists(gameServerKeyPath)
                ? File.ReadAllText(gameServerKeyPath).Trim()
                : string.Empty;

            secrets = new BackendSecrets
            {
                // Preserve the key created by the earlier AuthServer foundation when
                // upgrading in place. Generate a new one only for a fresh deployment.
                GameServerKey = !string.IsNullOrWhiteSpace(existingGameServerKey)
                    ? existingGameServerKey
                    : Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
                CertificatePassword = Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
            };

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
