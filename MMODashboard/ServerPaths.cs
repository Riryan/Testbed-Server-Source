using System.IO;
using System.Text.Json;

namespace MMODashboard;

internal sealed class ServerPaths
{
    public string Root { get; }

    public ServerPaths(string root) => Root = Path.GetFullPath(root);

    public string? BuildGateway => Find(
        @"Build\BuildGatewayServer.bat",
        @"Tools\Server\Build\BuildGatewayServer.bat",
        @"Tools\Server\Build\CompileGatewayServer.bat",
        "BuildGatewayServer.bat",
        "CompileGatewayServer.bat");

    public string? BuildGameServer => Find(
        @"Build\BuildGameServer.bat",
        @"Tools\Server\Build\BuildGameServer.bat",
        @"Tools\Server\Build\CompileGameServer.bat",
        "BuildGameServer.bat",
        "CompileGameServer.bat");

    public string? RunGateway => Find(
        @"Runtime\StartGatewayServer.bat",
        @"Runtime\RunGatewayServer.bat",
        @"Tools\Server\Runtime\StartGatewayServer.bat",
        @"Tools\Server\Runtime\RunGatewayServer.bat",
        "StartGatewayServer.bat",
        "RunGatewayServer.bat");

    public string? RunGameServer => Find(
        @"Runtime\StartGameServer.bat",
        @"Runtime\RunGameServer.bat",
        @"Tools\Server\Runtime\StartGameServer.bat",
        @"Tools\Server\Runtime\RunGameServer.bat",
        "StartGameServer.bat",
        "RunGameServer.bat");

    public string? StopServers => Find(
        @"Runtime\StopServers.bat",
        @"Tools\Server\Runtime\StopServers.bat",
        "StopServers.bat");

    public string? ServerConfig => Find(
        @"Config\ServerConfig.bat",
        @"Tools\Server\Config\ServerConfig.bat",
        "ServerConfig.bat");

    public string ExpectedServerConfig => Path.Combine(Root, "Config", "ServerConfig.bat");

    // For the local developer dashboard prefer the source appsettings so edits survive
    // the next build/publish. Fall back to the deployed copy for packaged server roots.
    public string? GatewayAppSettings => Find(
        @"Source\GatewayServer\appsettings.json",
        @"GatewayServer\appsettings.json");

    public string ExpectedGatewayAppSettings => Directory.Exists(Path.Combine(Root, "Source", "GatewayServer"))
        ? Path.Combine(Root, "Source", "GatewayServer", "appsettings.json")
        : Path.Combine(Root, "GatewayServer", "appsettings.json");

    public string? GameplayContent
    {
        get
        {
            string? direct = Find(@"Content\GameplayContent.json");
            if (direct is not null) return direct;

            string? settings = GatewayAppSettings;
            if (settings is null) return null;
            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(settings));
                if (!json.RootElement.TryGetProperty("GatewayServer", out JsonElement gateway) ||
                    !gateway.TryGetProperty("ContentDefinitionsPath", out JsonElement pathElement))
                    return null;
                string? configured = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(configured)) return null;
                string baseDirectory = Path.GetDirectoryName(settings) ?? Root;
                string resolved = Path.GetFullPath(Path.Combine(baseDirectory, configured));
                return File.Exists(resolved) ? resolved : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public string ExpectedGameplayContent => Path.Combine(Root, "Content", "GameplayContent.json");

    public string? Find(params string[] relativeCandidates)
    {
        foreach (var relative in relativeCandidates)
        {
            var full = Path.GetFullPath(Path.Combine(Root, relative));
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public bool HasRequiredScripts =>
        BuildGateway is not null &&
        BuildGameServer is not null &&
        RunGateway is not null &&
        RunGameServer is not null;
}
