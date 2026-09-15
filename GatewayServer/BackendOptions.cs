namespace Game.BackendServer;

internal sealed class BackendOptions
{
    public int HttpsPort { get; set; } = 8443;
    public int InternalPort { get; set; } = 8444;
    public string DatabasePath { get; set; } = "../Data/character-session.sqlite3";
    public string ContentDefinitionsPath { get; set; } = "../Content/GameplayContent.json";
    public string SecretsPath { get; set; } = "../Data/mmobackend.secrets.json";
    public string GameServerKeyPath { get; set; } = "../Data/game-server-auth.key";
    public string CertificatePath { get; set; } = "../Data/mmobackend-dev.pfx";
    public string CertificateFingerprintPath { get; set; } = "../Data/backend-cert.sha256";
    public string[] CertificateHosts { get; set; } = new[] { "localhost", "127.0.0.1" };
    public int AdmissionLifetimeSeconds { get; set; } = 120;
    public int Pbkdf2Iterations { get; set; } = 600_000;
    public int PasswordWorkerCount { get; set; } = 2;
    public int PasswordWorkerQueueCapacity { get; set; } = 64;
    public int DatabaseCriticalQueueCapacity { get; set; } = 512;
    public int DatabaseNormalQueueCapacity { get; set; } = 2048;
    public int DatabaseBackgroundQueueCapacity { get; set; } = 512;
    public int LoginAttemptsPerMinutePerIp { get; set; } = 10;
    public int AccountCreatesPerTenMinutesPerIp { get; set; } = 5;
    // Public /health is intentionally cheap, but a modified client must not be able to
    // turn the startup indicator into an unbounded request flood. Honest clients check
    // once at startup, every five minutes, and manual refresh is locally capped at 60s.
    public int HealthChecksPerMinutePerIp { get; set; } = 120;
    public int CharacterLimit { get; set; } = 4;

    public void Validate()
    {
        if (HttpsPort is < 1 or > 65535)
            throw new InvalidOperationException("GatewayServer:HttpsPort is invalid.");
        if (InternalPort is < 1 or > 65535 || InternalPort == HttpsPort)
            throw new InvalidOperationException("GatewayServer:InternalPort is invalid or conflicts with HttpsPort.");
        if (string.IsNullOrWhiteSpace(DatabasePath))
            throw new InvalidOperationException("GatewayServer:DatabasePath is required.");
        if (string.IsNullOrWhiteSpace(ContentDefinitionsPath))
            throw new InvalidOperationException("GatewayServer:ContentDefinitionsPath is required.");
        if (AdmissionLifetimeSeconds is < 30 or > 600)
            throw new InvalidOperationException("GatewayServer:AdmissionLifetimeSeconds must be 30-600 seconds.");
        if (Pbkdf2Iterations < 100_000)
            throw new InvalidOperationException("GatewayServer:Pbkdf2Iterations is too low.");
        if (PasswordWorkerCount is < 1 or > 64)
            throw new InvalidOperationException("GatewayServer:PasswordWorkerCount must be 1-64.");
        if (PasswordWorkerQueueCapacity is < 1 or > 4096)
            throw new InvalidOperationException("GatewayServer:PasswordWorkerQueueCapacity must be 1-4096.");
        if (DatabaseCriticalQueueCapacity is < 1 or > 65536 ||
            DatabaseNormalQueueCapacity is < 1 or > 65536 ||
            DatabaseBackgroundQueueCapacity is < 1 or > 65536)
        {
            throw new InvalidOperationException("GatewayServer database queue capacities must be 1-65536.");
        }
        if (LoginAttemptsPerMinutePerIp < 1 ||
            AccountCreatesPerTenMinutesPerIp < 1 ||
            HealthChecksPerMinutePerIp < 1)
            throw new InvalidOperationException("GatewayServer rate-limit settings must be positive.");
        if (CharacterLimit < 1 || CharacterLimit > 16)
            throw new InvalidOperationException("GatewayServer:CharacterLimit must be 1-16.");
    }
}
