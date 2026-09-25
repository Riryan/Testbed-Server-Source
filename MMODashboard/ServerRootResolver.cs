using System.IO;

namespace MMODashboard;

internal static class ServerRootResolver
{
    public static string? Resolve(string startDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            if (LooksLikeServerRoot(dir.FullName))
                return dir.FullName;
        }
        return null;
    }

    public static bool LooksLikeServerRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        var hasSource = Directory.Exists(Path.Combine(path, "Source"));
        var hasGateway = Directory.Exists(Path.Combine(path, "GatewayServer")) ||
                         Directory.Exists(Path.Combine(path, "Source", "GatewayServer"));
        var hasGame = Directory.Exists(Path.Combine(path, "GameServer")) ||
                      Directory.Exists(Path.Combine(path, "Source", "GameServer"));
        return hasSource && (hasGateway || hasGame);
    }
}
